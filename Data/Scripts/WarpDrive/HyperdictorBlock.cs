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
        public const int COOLDOWN_TOTAL_TICKS = 10 * 60 * 60; // 10 minutes (36000 ticks)
        public const int CHARGE_TOTAL_TICKS = 45 * 60; // 45 seconds from empty at full power
        public const double MAX_FIELD_RADIUS_METERS = 500000.0; // 500 km sphere

        public IMyFunctionalBlock Block { get; private set; }
        public MyCubeGrid MainGrid => Block?.CubeGrid as MyCubeGrid;

        public HyperdictorState State { get; private set; } = HyperdictorState.Off;

        /// <summary>The Hyperdictor's own built-in capacitor, 0.0f to 1.0f. It is saved with the block and never touches the FSD's capacitor.</summary>
        public float StoredChargeRatio { get; private set; } = 0.0f;
        public double FieldRadiusMeters => MAX_FIELD_RADIUS_METERS;
        private bool IsFull => StoredChargeRatio >= 0.999f;
        public int CooldownTicksRemaining { get; private set; } = 0;

        public bool IsEngaged => State == HyperdictorState.Engaged;
        public bool IsCoolingDown => State == HyperdictorState.CoolingDown;
        public bool IsCharged => State == HyperdictorState.Charged;
        public bool IsCharging => State == HyperdictorState.Charging;

        /// <summary>Server only: a capture sequence is currently running on this Hyperdictor.</summary>
        public bool IsBusy { get; set; } = false;

        /// <summary>True when the field is fully up and free to catch a new ship.</summary>
        public bool CanCatch
        {
            get
            {
                // The ship's own FSD is not needed: a caught ship is dropped next to the Hyperdictor, nobody chases it
                return IsEngaged && _engagedGraceTicks == 0 && !IsBusy && Block != null && !Block.MarkedForClose && Block.IsFunctional;
            }
        }

        /// <summary>Why <see cref="CanCatch"/> is false right now (empty when it is true). For the log.</summary>
        public string CatchBlocker
        {
            get
            {
                if (!IsEngaged)
                    return "FIELD NOT ENGAGED";
                if (_engagedGraceTicks != 0)
                    return "FIELD STILL SETTLING AFTER ENGAGING";
                if (IsBusy)
                    return "BUSY WITH A CHASE";
                if (FtlBlocker().Length > 0)
                    return "OWN SHIP " + FtlBlocker();
                if (Block == null || Block.MarkedForClose)
                    return "BLOCK GONE";
                if (!Block.IsFunctional)
                    return "BLOCK DAMAGED";

                return "";
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
            LoadStorageData();
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
            if (NetAuthority.IsAuthority)
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

            // Cooldown and stored energy are written whenever they change
            if (_syncTick % 60 == 0)
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
                    // Built-in capacitor full, awaiting manual engagement
                    if (!IsFull)
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
                        State = IsFull ? HyperdictorState.Charged : HyperdictorState.Charging;
                    }
                    break;

                case HyperdictorState.CoolingDown:
                    UpdateCooldownTick();
                    break;
            }
        }

        private void UpdateChargingTick()
        {
            // Charges only while fully powered; without power it simply pauses (no bleed), so a freshly loaded save keeps its charge
            float availablePower = _sink?.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) ?? 0f;
            if (availablePower >= POWER_REQUIRED_MW * 0.95f)
                StoredChargeRatio = Math.Min(1.0f, StoredChargeRatio + 1.0f / CHARGE_TOTAL_TICKS);

            if (IsFull)
            {
                StoredChargeRatio = 1.0f;
                State = HyperdictorState.Charged;
                PlaySpoolDownSound();
                NotifyShip($"HYPERDICTOR CAPACITOR FULLY CHARGED - {FieldRadiusMeters / 1000.0:0.#} KM FIELD READY", "White");
            }
        }

        private void UpdateEngagedTick()
        {
            // The field only holds in normal space (in hyperspace the ship is moved by teleports, so it still reads as stationary)
            string ftl = FtlBlocker();
            if (ftl.Length > 0)
            {
                Disengage(false);
                NotifyShip("HYPERDICTOR DISENGAGED - FIELD COLLAPSED " + ftl, "Red");
                return;
            }

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
                    NotifyShip("HYPERDICTOR THERMAL DISSIPATION COMPLETE - READY TO RECHARGE", "Green");
                    PlaySpoolDownSound();
                }
            }
            else
            {
                State = HyperdictorState.Off;
            }
        }

        /// <summary>Where the ship's own drive has taken it, if out of normal space ("" when it is in normal space).</summary>
        public string FtlBlocker()
        {
            var grid = MainGrid;
            var session = WarpDriveSession.instance;
            if (grid == null || session == null)
                return "";

            WarpSystem ws = session.GetGridWarpSystem(grid);
            if (ws != null && ws.warpState == WarpSystem.State.active)
                return "IN SUPERCRUISE";

            foreach (var hs in session.GetHyperspaceSystems())
            {
                if (hs != null && (hs.State == HyperspaceSystem.HyperState.Countdown || hs.State == HyperspaceSystem.HyperState.Active)
                    && hs.GridSystem != null && hs.GridSystem.Contains(grid))
                    return "IN HYPERSPACE";
            }

            foreach (var cap in session.GetCapitalFSDSystems())
            {
                if (cap != null && cap.State >= CapitalFSD.CapState.engageRiser && cap.State <= CapitalFSD.CapState.jumpOut
                    && cap.GridSystem != null && cap.GridSystem.Contains(grid))
                    return "IN HYPERSPACE";
            }

            return "";
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
                // Letting go now would cancel the chase and free every ship it has caught
                if (HyperdictionSession.Instance != null && HyperdictionSession.Instance.IsCatching(this))
                {
                    NotifyPlayer(playerId, "Cannot disengage: a catch is in progress", "Red");
                    return;
                }

                Disengage(false);
                NotifyPlayer(playerId, "Hyperdictor Disengaged", "White");
                return;
            }

            string ftl = FtlBlocker();
            if (ftl.Length > 0)
            {
                NotifyPlayer(playerId, "Cannot engage Hyperdictor " + ftl.ToLowerInvariant() + ": the field only holds in normal space", "Red");
                return;
            }

            if (!IsGridStationary())
            {
                NotifyPlayer(playerId, "Cannot engage Hyperdictor: Ship must be completely stationary!", "Red");
                return;
            }

            if (!IsFull)
            {
                NotifyPlayer(playerId, $"Cannot engage: Capacitor charging ({(int)(StoredChargeRatio * 100)}% / 100%)", "Red");
                return;
            }

            // Engage!
            State = HyperdictorState.Engaged;
            _engagedGraceTicks = 20; // Allow 20 ticks (~0.33s) for SE distributor to allocate 20 MW
            _sink?.Update();
            PlayEngagedSound();
            NotifyShip($"HYPERDICTOR ENGAGED - {FieldRadiusMeters / 1000.0:0.#}KM INTERDICTION FIELD ACTIVE", "Green");
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
                    StoredChargeRatio *= 0.75f; // 25% drain penalty
                    NotifyShip("HYPERDICTOR DISENGAGED - GRID MOVEMENT DETECTED!", "Red");
                }
                else
                {
                    State = IsFull ? HyperdictorState.Charged : HyperdictorState.Charging;
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
            if (State == HyperdictorState.Charging && !IsFull)
            {
                if ((_sink?.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) ?? 0f) >= POWER_REQUIRED_MW * 0.5f)
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
            sb.AppendLine("Caught ships are dropped next to this ship");
            sb.AppendLine($"Power Required: {POWER_REQUIRED_MW:0.0} MW");
            sb.AppendLine($"Field Radius: {FieldRadiusMeters / 1000.0:0} km");
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
            HudEvents.Post(MainGrid.EntityId, 0L, message, HudEvents.Classify(message, font), HudRoute.Server, 4f);
        }

        private void NotifyPlayer(long playerId, string message, string font = "White")
        {
            if (playerId <= 0) return;
            HudEvents.Post(MainGrid != null ? MainGrid.EntityId : 0L, playerId, message, HudEvents.Classify(message, font), HudRoute.Server, 3f);
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

            StoredChargeRatio = MathHelper.Clamp(msg.storedPowerRatio, 0f, 1f);

            HyperdictorState previous = State;
            State = (HyperdictorState)msg.state;
            CooldownTicksRemaining = msg.cooldownTicksRemaining;

            // Sounds are triggered by server logic, so remote clients play them from the state change
            if (previous != State)
            {
                if (State == HyperdictorState.Charged)
                    PlaySpoolDownSound();
                else if (State == HyperdictorState.Engaged)
                    PlayEngagedSound();
            }
        }

        private int _lastSavedCooldown = -1;
        private float _lastSavedRatio = -1f;

        // Stores "cooldownTicks;chargeRatio", always with the invariant culture
        private void LoadStorageData()
        {
            try
            {
                string val;
                if (Entity.Storage != null && Entity.Storage.TryGetValue(STORAGE_GUID, out val) && !string.IsNullOrEmpty(val))
                {
                    string[] parts = val.Split(';');
                    int cooldown;
                    if (int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out cooldown))
                    {
                        CooldownTicksRemaining = Math.Max(0, cooldown);
                        if (CooldownTicksRemaining > 0)
                            State = HyperdictorState.CoolingDown;
                    }

                    float stored;
                    if (parts.Length >= 2 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out stored))
                    {
                        StoredChargeRatio = MathHelper.Clamp(stored, 0f, 1f);
                        if (CooldownTicksRemaining <= 0 && IsFull)
                            State = HyperdictorState.Charged;
                    }
                }

                _lastSavedCooldown = CooldownTicksRemaining;
                _lastSavedRatio = StoredChargeRatio;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[HyperdictorBlock] Error loading storage: {ex.Message}");

                // Nothing readable is left to protect: without this the guard in SaveStorageData would block every later save
                _lastSavedCooldown = CooldownTicksRemaining;
                _lastSavedRatio = StoredChargeRatio;
            }
        }

        private void SaveStorageData()
        {
            if (!NetAuthority.IsAuthority)
                return;
            if (_lastSavedCooldown < 0)
                return; // not loaded yet, saving now would overwrite the stored values
            if (CooldownTicksRemaining == _lastSavedCooldown && Math.Abs(StoredChargeRatio - _lastSavedRatio) < 0.0005f)
                return;

            try
            {
                if (Entity.Storage == null)
                    Entity.Storage = new MyModStorageComponent();

                Entity.Storage.SetValue(STORAGE_GUID,
                    CooldownTicksRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" +
                    StoredChargeRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                _lastSavedCooldown = CooldownTicksRemaining;
                _lastSavedRatio = StoredChargeRatio;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[HyperdictorBlock] Error saving storage: {ex.Message}");
            }
        }

        public override void Close()
        {
            SaveStorageData();
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
