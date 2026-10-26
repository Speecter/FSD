using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    public enum HyperdictorState
    {
        Off,
        Charging,
        Charged,
        Engaged,
        Disrupted,
        CoolingDown
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "HyperdictorLarge")]
    public class HyperdictorBlock : MyGameLogicComponent
    {
        public static readonly Guid STORAGE_GUID = new Guid("4A911765-7489-4D2A-885E-5CE20BBE8D71");

        public const float POWER_REQUIRED_MW = 20.0f;
        public const float STANDBY_POWER_MW = 0.001f;
        public const int CHARGE_TOTAL_TICKS = 45 * 60; // 45 seconds (2700 ticks)
        public const int COOLDOWN_TOTAL_TICKS = 10 * 60 * 60; // 10 minutes (36000 ticks)
        public const double FIELD_RADIUS_METERS = 25000.0; // 25 km sphere

        public IMyFunctionalBlock Block { get; private set; }
        public MyCubeGrid MainGrid => Block?.CubeGrid as MyCubeGrid;

        public HyperdictorState State { get; private set; } = HyperdictorState.Off;
        public float StoredChargeRatio { get; private set; } = 0.0f; // 0.0f to 1.0f
        public int CooldownTicksRemaining { get; private set; } = 0;

        public bool IsEngaged => State == HyperdictorState.Engaged;
        public bool IsCoolingDown => State == HyperdictorState.CoolingDown;
        public bool IsCharged => State == HyperdictorState.Charged;
        public bool IsCharging => State == HyperdictorState.Charging;

        /// <summary>Server only: a capture sequence is currently running on this Hyperdictor.</summary>
        public bool IsBusy { get; set; } = false;

        /// <summary>True when the field is fully up and free to catch a new ship.</summary>
        /// <summary>The ship's own FSD. The Hyperdictor only works while it can actually make the chase jump.</summary>
        public HyperspaceSystem Interceptor
        {
            get
            {
                var grid = MainGrid;
                return grid != null ? WarpDriveSession.instance?.GetGridHyperspace(grid.EntityId) : null;
            }
        }

        public bool HasUsableFsd(out string reason)
        {
            var fsd = Interceptor;
            if (fsd == null)
            {
                reason = "NO FSD ON BOARD";
                return false;
            }
            return fsd.IsInterceptReady(out reason);
        }

        /// <summary>True when the field is fully up, the ship's FSD is ready, and no chase is running.</summary>
        public bool CanCatch
        {
            get
            {
                if (!IsEngaged || _engagedGraceTicks != 0 || IsBusy || Block == null || Block.MarkedForClose || !Block.IsFunctional)
                    return false;
                string reason;
                return HasUsableFsd(out reason);
            }
        }

        private MyResourceSinkComponent _sink;
        private MyEntity3DSoundEmitter _chargeEmitter;
        private MyEntity3DSoundEmitter _localEmitter;
        private MyEntity3DSoundEmitter _reactorEmitter;

        private bool _isDisruptedByMotion = false;
        private int _syncTick = 0;
        private bool _registeredWithSession = false;
        private int _engagedGraceTicks = 0;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.BEFORE_NEXT_FRAME;

            Block = Entity as IMyFunctionalBlock;
            if (Block == null) return;

            InitPowerSink();
            LoadStorageData();

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                Block.AppendingCustomInfo += OnAppendingCustomInfo;
            }
        }

        private void InitPowerSink()
        {
            _sink = new MyResourceSinkComponent();
            _sink.Init(
                MyStringHash.GetOrCompute("Utility"),
                POWER_REQUIRED_MW,
                ComputeRequiredPower,
                (MyCubeBlock)Entity);

            Entity.Components.Add(_sink);
            _sink.Update();
        }

        private float ComputeRequiredPower()
        {
            if (Block == null || !Block.IsFunctional || !Block.Enabled)
                return 0.0f;

            if (State == HyperdictorState.Charging || State == HyperdictorState.Engaged)
                return POWER_REQUIRED_MW;

            if (State == HyperdictorState.CoolingDown || State == HyperdictorState.Charged)
                return STANDBY_POWER_MW;

            return 0.0f;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            HyperdictorControls.RegisterControls();
            HyperdictionSession.Instance?.RegisterHyperdictor(this);
            _registeredWithSession = true;
        }

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();

            if (Block == null || Block.MarkedForClose) return;

            if (!_registeredWithSession && HyperdictionSession.Instance != null)
            {
                HyperdictionSession.Instance.RegisterHyperdictor(this);
                _registeredWithSession = true;
            }

            _sink?.Update();

            // Server-authoritative logic
            if (MyAPIGateway.Multiplayer.IsServer || !MyAPIGateway.Multiplayer.MultiplayerActive)
            {
                UpdateServerState();
            }

            UpdateAudioAndEffects();

            // Periodic terminal info update & multiplayer sync
            _syncTick++;
            if (_syncTick % 60 == 0)
            {
                Block.RefreshCustomInfo();
                if (MyAPIGateway.Multiplayer.IsServer)
                {
                    SyncStateToClients();
                }
            }

            // Save persistent data every 300 ticks (~5 seconds)
            if (_syncTick % 300 == 0)
            {
                SaveStorageData();
            }
        }

        private void UpdateServerState()
        {
            if (!Block.IsFunctional || !Block.Enabled)
            {
                if (State != HyperdictorState.Off && State != HyperdictorState.CoolingDown)
                {
                    Disengage(false);
                    State = HyperdictorState.Off;
                }
            }

            switch (State)
            {
                case HyperdictorState.Off:
                    // Ready to charge if powered and enabled
                    if (Block.IsFunctional && Block.Enabled)
                    {
                        State = HyperdictorState.Charging;
                    }
                    break;

                case HyperdictorState.Charging:
                    UpdateChargingTick();
                    break;

                case HyperdictorState.Charged:
                    // Capacitor fully charged, awaiting manual engagement or maintaining charge
                    if (StoredChargeRatio < 1.0f)
                    {
                        State = HyperdictorState.Charging;
                    }
                    break;

                case HyperdictorState.Engaged:
                    UpdateEngagedTick();
                    break;

                case HyperdictorState.Disrupted:
                    // Cooldown brief penalty after disruption, then resume charging
                    if (IsGridStationary())
                    {
                        _isDisruptedByMotion = false;
                        State = StoredChargeRatio >= 1.0f ? HyperdictorState.Charged : HyperdictorState.Charging;
                    }
                    break;

                case HyperdictorState.CoolingDown:
                    UpdateCooldownTick();
                    break;
            }
        }

        private void UpdateChargingTick()
        {
            float availablePower = _sink?.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) ?? 0f;

            if (availablePower >= POWER_REQUIRED_MW * 0.95f)
            {
                // Accumulate capacitor charge
                StoredChargeRatio += 1.0f / CHARGE_TOTAL_TICKS;
                if (StoredChargeRatio >= 1.0f)
                {
                    StoredChargeRatio = 1.0f;
                    State = HyperdictorState.Charged;
                    PlaySpoolDownSound();
                    NotifyShip("HYPERDICTOR CAPACITOR FULLY CHARGED - READY TO ENGAGE", "White");
                }
            }
            else
            {
                // Slow bleed if power is cut
                StoredChargeRatio = Math.Max(0.0f, StoredChargeRatio - (1.0f / (CHARGE_TOTAL_TICKS * 4f)));
            }
        }

        private void UpdateEngagedTick()
        {
            // Balancing constraint: SHIP MUST REMAIN COMPLETELY STATIONARY!
            // (the ship is allowed to turn and jump once it is committed to a chase)
            if (!IsBusy && !IsGridStationary())
            {
                Disengage(true);
                return;
            }

            // Power check: Allow brief grace period for resource distribution after engagement
            if (_engagedGraceTicks > 0)
            {
                _engagedGraceTicks--;
                return;
            }

            float availablePower = _sink?.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) ?? 0f;
            if (availablePower < POWER_REQUIRED_MW * 0.85f)
            {
                Disengage(false);
                NotifyShip("HYPERDICTOR DISENGAGED - INSUFFICIENT POWER (REQUIRES 20MW)", "Red");
                return;
            }
        }

        private void UpdateCooldownTick()
        {
            if (CooldownTicksRemaining > 0)
            {
                CooldownTicksRemaining--;
                if (CooldownTicksRemaining == 0)
                {
                    State = HyperdictorState.Off;
                    StoredChargeRatio = 0.0f;
                    NotifyShip("HYPERDICTOR THERMAL DISSIPATION COMPLETE - READY TO RECHARGE", "Green");
                    PlaySpoolDownSound();
                }
            }
            else
            {
                State = HyperdictorState.Off;
            }
        }

        public bool IsGridStationary()
        {
            var grid = MainGrid;
            if (grid == null || grid.Physics == null) return false;

            // Strict velocity thresholds: < 0.05 m/s and < 0.02 rad/s
            if (grid.Physics.LinearVelocity.LengthSquared() > 0.0025f)
                return false;

            if (grid.Physics.AngularVelocity.LengthSquared() > 0.0004f)
                return false;

            return true;
        }

        public void RequestToggleEngagement(long playerId)
        {
            if (State == HyperdictorState.CoolingDown)
            {
                int remainingSecs = CooldownTicksRemaining / 60;
                NotifyPlayer(playerId, $"Cannot engage: Hyperdictor cooling down ({remainingSecs / 60:D2}:{remainingSecs % 60:D2} remaining)", "Red");
                return;
            }

            if (State == HyperdictorState.Engaged)
            {
                Disengage(false);
                NotifyPlayer(playerId, "Hyperdictor Disengaged", "White");
                return;
            }

            if (!IsGridStationary())
            {
                NotifyPlayer(playerId, "Cannot engage Hyperdictor: Ship must be completely stationary!", "Red");
                return;
            }

            if (StoredChargeRatio < 0.999f)
            {
                NotifyPlayer(playerId, $"Cannot engage: Capacitor charging ({(int)(StoredChargeRatio * 100)}% / 100%)", "Red");
                return;
            }

            // Engage!
            State = HyperdictorState.Engaged;
            _engagedGraceTicks = 20; // Allow 20 ticks (~0.33s) for SE distributor to allocate 20 MW
            _sink?.Update();
            NotifyShip("HYPERDICTOR ENGAGED - 25KM INTERDICTION FIELD ACTIVE", "Green");
            SyncStateToClients();
        }

        public void Disengage(bool byMotion = false)
        {
            if (State == HyperdictorState.Engaged)
            {
                if (byMotion)
                {
                    _isDisruptedByMotion = true;
                    State = HyperdictorState.Disrupted;
                    StoredChargeRatio = Math.Max(0.0f, StoredChargeRatio - 0.25f); // 25% drain penalty
                    NotifyShip("HYPERDICTOR DISENGAGED - GRID MOVEMENT DETECTED!", "Red");
                }
                else
                {
                    State = StoredChargeRatio >= 1.0f ? HyperdictorState.Charged : HyperdictorState.Charging;
                }

                _engagedGraceTicks = 0;
                _sink?.Update();
                SyncStateToClients();
            }
        }

        /// <summary>
        /// Called when a victim ship in Hyperspace transit is successfully caught and forced to emergency drop next to this Hyperdictor.
        /// Initiates the mandatory 10-minute cooling off mode.
        /// </summary>
        public void OnVictimDropped(long victimGridId)
        {
            State = HyperdictorState.CoolingDown;
            CooldownTicksRemaining = COOLDOWN_TOTAL_TICKS;
            StoredChargeRatio = 0.0f; // Completely discharged

            PlaySpoolDownSound();
            NotifyShip("TARGET CAUGHT IN HYPERSPACE! HYPERDICTOR OVERHEATED - 10 MINUTE COOLDOWN INITIATED", "Red");

            SaveStorageData();
            SyncStateToClients();
        }

        private void UpdateAudioAndEffects()
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            Vector3D blockPos = Block.GetPosition();

            // Charging loop sound
            if (State == HyperdictorState.Charging && StoredChargeRatio < 1.0f)
            {
                float availablePower = _sink?.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) ?? 0f;
                if (availablePower >= POWER_REQUIRED_MW * 0.5f)
                {
                    if (_chargeEmitter == null)
                    {
                        _chargeEmitter = new MyEntity3DSoundEmitter((MyEntity)Block);
                        _chargeEmitter.CanPlayLoopSounds = true;
                    }

                    _chargeEmitter.SetPosition(blockPos);
                    if (!_chargeEmitter.IsPlaying)
                    {
                        _chargeEmitter.PlaySound(WarpSound.hyperdictorCharge, true);
                    }
                }
                else
                {
                    StopChargeLoopSound();
                }
            }
            else
            {
                StopChargeLoopSound();
            }

            // Reactor: start swell when engaged, loop while the field is up, end tail when it drops
            if (State == HyperdictorState.Engaged)
            {
                if (_reactorEmitter == null)
                {
                    _reactorEmitter = new MyEntity3DSoundEmitter((MyEntity)Block);
                    _reactorEmitter.CanPlayLoopSounds = true;
                }

                _reactorEmitter.SetPosition(blockPos);
                if (!_reactorEmitter.IsPlaying)
                    _reactorEmitter.PlaySound(WarpSound.hyperdictorReactor, true);
            }
            else
            {
                StopReactorSound(false);
            }
        }

        private void StopReactorSound(bool immediate)
        {
            if (_reactorEmitter != null && _reactorEmitter.IsPlaying)
                _reactorEmitter.StopSound(immediate);
        }

        private void StopChargeLoopSound()
        {
            if (_chargeEmitter != null && _chargeEmitter.IsPlaying)
            {
                _chargeEmitter.StopSound(true);
            }
        }

        private void PlaySpoolDownSound()
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            if (_localEmitter == null)
                _localEmitter = new MyEntity3DSoundEmitter((MyEntity)Block);

            _localEmitter.SetPosition(Block.GetPosition());
            _localEmitter.PlaySound(WarpSound.spoolDown, false);
        }

        private void PlayEngagedSound()
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            if (_localEmitter == null)
                _localEmitter = new MyEntity3DSoundEmitter((MyEntity)Block);

            _localEmitter.SetPosition(Block.GetPosition());
            _localEmitter.PlaySound(WarpSound.hyperdictorEngaged, false);
        }

        private void OnAppendingCustomInfo(IMyTerminalBlock block, StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("=== Frame Shift Hyperdictor ===");
            sb.AppendLine($"Target: Hyperspace / Witchspace Transits Only");
            sb.AppendLine($"Interdiction: predictive - the ship's own FSD jumps in behind the target");
            string fsdReason;
            sb.AppendLine(HasUsableFsd(out fsdReason) ? "FSD: READY" : $"FSD: UNAVAILABLE ({fsdReason}) - HYPERDICTOR INOPERATIVE");
            sb.AppendLine($"Power Required: {POWER_REQUIRED_MW:0.0} MW");
            sb.AppendLine($"Stationary Status: {(IsGridStationary() ? "Stationary (OK)" : "Moving (RESTRICTED)")}");
            sb.AppendLine();

            switch (State)
            {
                case HyperdictorState.Off:
                    sb.AppendLine("Status: OFFLINE");
                    break;
                case HyperdictorState.Charging:
                    sb.AppendLine($"Status: CHARGING CAPACITOR ({(int)(StoredChargeRatio * 100)}%)");
                    sb.AppendLine(GetProgressBar(StoredChargeRatio));
                    break;
                case HyperdictorState.Charged:
                    sb.AppendLine("Status: CHARGED & READY TO ENGAGE");
                    sb.AppendLine(GetProgressBar(1.0f));
                    break;
                case HyperdictorState.Engaged:
                    sb.AppendLine(IsBusy ? "Status: >>> INTERCEPT IN PROGRESS <<<" : "Status: >>> HYPERDICTOR ENGAGED - SCANNING TRANSITS <<<");
                    sb.AppendLine("Warning: Do not move the ship until a target is locked!");
                    break;
                case HyperdictorState.Disrupted:
                    sb.AppendLine("Status: DISRUPTED - GRID MOVEMENT DETECTED");
                    break;
                case HyperdictorState.CoolingDown:
                    int totalSecs = CooldownTicksRemaining / 60;
                    int minutes = totalSecs / 60;
                    int seconds = totalSecs % 60;
                    float coolProgress = 1.0f - ((float)CooldownTicksRemaining / COOLDOWN_TOTAL_TICKS);
                    sb.AppendLine($"Status: COOLING OFF - LOCKOUT ACTIVE");
                    sb.AppendLine($"Cooldown Remaining: {minutes:D2}:{seconds:D2}");
                    sb.AppendLine(GetProgressBar(coolProgress));
                    sb.AppendLine("Note: Hyperdictor locked out until cooling completes.");
                    break;
            }
        }

        private static string GetProgressBar(float ratio)
        {
            ratio = MathHelper.Clamp(ratio, 0f, 1f);
            int total = 14;
            int filled = (int)(ratio * total);
            return $"[{new string('=', filled)}{new string(' ', total - filled)}] {(int)(ratio * 100)}%";
        }

        private void NotifyShip(string message, string font = "White")
        {
            if (MainGrid == null) return;
            var players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            foreach (var p in players)
            {
                if (p?.Character != null)
                {
                    var seated = p.Character.Parent as VRage.Game.ModAPI.IMyCubeBlock;
                    if (seated != null && seated.CubeGrid == MainGrid)
                    {
                        MyVisualScriptLogicProvider.ShowNotification(message, 4000, font, p.IdentityId);
                    }
                }
            }
        }

        private void NotifyPlayer(long playerId, string message, string font = "White")
        {
            if (playerId <= 0) return;
            MyVisualScriptLogicProvider.ShowNotification(message, 3000, font, playerId);
        }

        private void SyncStateToClients()
        {
            if (!MyAPIGateway.Multiplayer.IsServer) return;

            var msg = new HyperdictorSyncMessage
            {
                entityId = Block.EntityId,
                state = (int)State,
                storedPowerRatio = StoredChargeRatio,
                cooldownTicksRemaining = CooldownTicksRemaining
            };

            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
            MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.hyperdictorSyncPacket, data);
        }

        public void ApplyServerSync(HyperdictorSyncMessage msg)
        {
            if (msg == null) return;

            HyperdictorState previous = State;
            State = (HyperdictorState)msg.state;
            StoredChargeRatio = msg.storedPowerRatio;
            CooldownTicksRemaining = msg.cooldownTicksRemaining;

            // Sounds are triggered by server logic, so remote clients play them from the state change
            if (previous != State)
            {
                if (State == HyperdictorState.Charged)
                    PlaySpoolDownSound();
            }
        }

        private void LoadStorageData()
        {
            try
            {
                if (Entity.Storage == null)
                    Entity.Storage = new MyModStorageComponent();

                string val;
                if (Entity.Storage.TryGetValue(STORAGE_GUID, out val) && !string.IsNullOrEmpty(val))
                {
                    string[] parts = val.Split(';');
                    if (parts.Length >= 2)
                    {
                        int cooldown;
                        float charge;
                        if (int.TryParse(parts[0], out cooldown))
                            CooldownTicksRemaining = cooldown;
                        if (float.TryParse(parts[1], out charge))
                            StoredChargeRatio = MathHelper.Clamp(charge, 0f, 1f);

                        if (CooldownTicksRemaining > 0)
                            State = HyperdictorState.CoolingDown;
                        else if (StoredChargeRatio >= 1.0f)
                            State = HyperdictorState.Charged;
                    }
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[HyperdictorBlock] Error loading storage: {ex.Message}");
            }
        }

        private void SaveStorageData()
        {
            try
            {
                if (Entity.Storage == null)
                    Entity.Storage = new MyModStorageComponent();

                Entity.Storage.SetValue(STORAGE_GUID, $"{CooldownTicksRemaining};{StoredChargeRatio}");
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[HyperdictorBlock] Error saving storage: {ex.Message}");
            }
        }

        public override void Close()
        {
            StopChargeLoopSound();
            StopReactorSound(true);
            if (_localEmitter != null && _localEmitter.IsPlaying)
                _localEmitter.StopSound(true);

            if (_registeredWithSession && HyperdictionSession.Instance != null)
                HyperdictionSession.Instance.UnregisterHyperdictor(this);

            if (!MyAPIGateway.Utilities.IsDedicated && Block != null)
                Block.AppendingCustomInfo -= OnAppendingCustomInfo;

            base.Close();
        }
    }
}
