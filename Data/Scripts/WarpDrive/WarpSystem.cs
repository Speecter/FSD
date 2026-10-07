using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    public class WarpSystem
    {
        public bool valid => grid != null && grid.valid;
        public int id { get; private set; }
        public State warpState { get; set; }
        public event Action<WarpSystem> OnSystemInvalidatedAction;
        public List<IMyPlayer> onlinePlayers = new List<IMyPlayer>();
        public double currentSpeedPt = Settings.GetShared().startSpeed;
        public int driveHeat { get; set; }
        public float heatGenerationRate { get; private set; }
        public float heatDissipationRate { get; private set; }
        public float heatCapacity { get; private set; }
        public GridSystem grid;
        public bool isSleeping { get; private set; }

        private MatrixD gridMatrix;
        private readonly Dictionary<IMyCubeGrid, HashSet<WarpDrive>> warpDrives = new Dictionary<IMyCubeGrid, HashSet<WarpDrive>>();
        private readonly List<IMyPlayer> playersInWarpList = new List<IMyPlayer>();
        private readonly Dictionary<long, float> playerOxygenSnapshot = new Dictionary<long, float>();
        private long pilotId;
        private IMyShipController controlSeat;
        private readonly Dictionary<IMyGyro, float> gyroBlocks = new Dictionary<IMyGyro, float>();
        private MyParticleEffect effect;
        private readonly MyEntity3DSoundEmitter sound;
        private long startChargeRuntime;
        private long spoolFinishTick;
        private bool enemyInRange;
        private bool hasEnoughPower = true;
        private bool primaryFunctional;
        private WarpDrive primaryDrive;
        private float totalHeat = 0;
        private int cachedActiveSinks = 0;
        private int cachedActiveSmallSinks = 0;
        private int _updateTicks = 0;
        private int speedUpSendToServerTick = 0;
        private int speedDownSendToServerTick = 0;
        private int powerCheckTick = 0;
        private bool? wasInGravity = null;

        public bool isPrototech = false;

        public string warnPlanetIntHUD = "Planetary interference detected!";
        public string warnDamagedHUD = "FS Drive Offline or Damaged!";
        public string warnPowerHUD = "Not enough power!";
        public string emergencyStopHUD = "Emergency Stop!";
        public string warnIsStaticHUD = "Unable to move static grid!";
        public string warnOverheatHUD = "FS Drive overheated!";
        public string warnProximityHUD = "Can't Start FS Drive. Proximity Alert!";
        public string warnNotSeatedHUD = "Emergency Stop! Occupant not seated";
        public string warnDriveInUseHUD = "Another seat is piloting this grid";
        public string warnRestabilizingHUD = "FS Drive Restabilizing...";
        public string warnConnectionHUD = "Emergency Stop! Connection changes detected";
        public string warnCoolingHUD = "Cannot start FS Drive. Another drive is still cooling";

        private const float heatPerMWDivisor = 100f;
        private const float heatPerMWDivisorPrototech = 150f;
        private const float smallRatio = 1f / 5f;
        private bool isSmall => grid?.mainGrid?.GridSizeEnum == MyCubeSize.Small;
        private bool inAllowedGravity => Settings.instance.AllowInGravity && GridGravityNow() > 0;

        public WarpSystem(WarpDrive block)
        {
            if (block == null || block.block == null || block.block.CubeGrid == null)
                return;

            isPrototech = IsPrototech(block);
            GetPlayerList();

            id = WarpDriveSession.instance.rand.Next(int.MinValue, int.MaxValue);

            grid = new GridSystem((MyCubeGrid)block.block.CubeGrid);

            GridSystem.BlockCounter warpDriveCounter = new GridSystem.BlockCounter((b) => b?.GameLogic?.GetAs<WarpDrive>() != null);
            warpDriveCounter.OnBlockAdded += OnDriveAdded;
            warpDriveCounter.OnBlockRemoved += OnDriveRemoved;
            grid.AddCounter("WarpDrives", warpDriveCounter);

            grid.OnSystemInvalidated += OnSystemInvalidated;

            if (!MyAPIGateway.Utilities.IsDedicated && grid.mainGrid != null)
            {
                sound = new MyEntity3DSoundEmitter(grid.mainGrid)
                {
                    CanPlayLoopSounds = true
                };
            }

            UpdateHeatStats(totalHeat > 0f);

            block.SetWarpSystem(this);
        }

        private void GetPlayerList()
        {
            onlinePlayers.Clear();
            MyAPIGateway.Players.GetPlayers(onlinePlayers);
        }

        public void Wake()
        {
            if (!isSleeping)
                return;

            isSleeping = false;
            GetPlayerList();
        }

        public void UpdateBeforeSimulation()
        {
            if (WarpDriveSession.instance == null || Settings.instance == null || grid == null || grid.mainGrid == null)
                return;

            var mainGrid = grid.mainGrid;

            if (WarpDriveSession.instance.runtime % 300 == 0)
                GetPlayerList();

            if (warpDrives.Count == 0)
            {
                grid.Invalidate();
                return;
            }

            CheckGravityStateChange();

            if (WarpDriveSession.instance.runtime % 10 == 0)
                UpdateHeatStats(true);

            UpdateHeatPower();

            if (warpState == State.charging || warpState == State.active)
                gridMatrix = grid.FindWorldMatrix(controlSeat);

            if (warpState == State.charging)
                InSpool();

            if (warpState == State.active)
            {
                if (!MyAPIGateway.Utilities.IsDedicated && sound != null)
                {
                    sound.SetPosition(mainGrid.PositionComp.GetPosition());

                    if (!sound.IsPlaying)
                        sound.PlaySound(WarpSound.cruiseOperatingSound, true);
                }

                if (!MyAPIGateway.Utilities.IsDedicated)
                {
                    DrawCruiseLine(Color.CornflowerBlue, 1000, 180);

                    if (currentSpeedPt < 316.6666)
                        DrawCruiseLine(Color.Indigo, 800, 220);
                }

                if (InWarp() && currentSpeedPt > 1f)
                {
                    gridMatrix.Translation += gridMatrix.Forward * currentSpeedPt;

                    if (MyAPIGateway.Multiplayer.IsServer)
                        mainGrid.Teleport(gridMatrix);

                    if (!MyAPIGateway.Utilities.IsDedicated)
                    {
                        DrawCruiseLine(Color.SteelBlue, 1200, 240);

                        if (currentSpeedPt > 316.6666)
                            DrawCruiseLine(Color.LightGoldenrodYellow, 1500, 90);
                    }
                }
            }

            isSleeping = warpState == State.idle && totalHeat <= 0f;
        }

        private bool InWarp()
        {
            if (grid.mainGrid == null)
                return false;

            var mainGrid = grid.mainGrid;

            if (WarpDriveSession.instance.runtime % 120 == 0)
            {
                if (mainGrid.Physics?.LinearVelocity.Length() >= 1f)
                    mainGrid.Physics.LinearVelocity = Vector3.Zero;

                GyroNerfer(true);
            }

            if (!MyAPIGateway.Multiplayer.IsServer)
            {
                ClientSpeedInput();
                return true;
            }

            PlayerO2Pause();

            if (IsInGravity())
            {
                SendMessage(warnPlanetIntHUD);

                Dewarp(inAllowedGravity);

                return false;
            }

            if (WarpDrive.ProximityDanger(gridMatrix, mainGrid, currentSpeedPt))
            {
                SendMessage(emergencyStopHUD);

                Dewarp(true);
                primaryDrive?.IsRestabilizing();

                return false;
            }

            if (!hasEnoughPower)
            {
                SendMessage(warnPowerHUD);
                Dewarp();
                primaryDrive?.IsRestabilizing();

                return false;
            }

            if (!primaryFunctional)
            {
                SendMessage(warnDamagedHUD);
                Dewarp();

                return false;
            }

            if (primaryDrive.heat >= heatCapacity)
            {
                SendMessage(warnOverheatHUD);
                Dewarp();
                primaryDrive?.IsRestabilizing();

                return false;
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
                ClientSpeedInput();

            currentSpeedPt = Math.Min(currentSpeedPt, inAllowedGravity ? Math.Min(Settings.instance.maxSpeed, Settings.instance.AllowInGravityMaxSpeed) : Settings.instance.maxSpeed);

            if (currentSpeedPt <= -1f)
            {
                Dewarp();
                primaryDrive?.IsRestabilizing();

                    return false;
                }

            if (WarpDriveSession.instance.runtime % 11 == 0)
                BroadcastSpeed();

            return true;
        }

        private void DrawCruiseLine(Vector4 color, double behind, double ahead)
        {
            if (grid.mainGrid == null)
                return;

            try
            {
                bool small = grid.mainGrid.GridSizeEnum == MyCubeSize.Small;
                Vector3D pos = grid.mainGrid.Physics.CenterOfMassWorld;
                Vector3D startPos = pos + (gridMatrix.Forward * (small ? ahead / 2 : ahead)) + (gridMatrix.Down * 12f);
                Vector3D endPos = pos - (gridMatrix.Forward * ((small ? behind / 2 : behind) - (currentSpeedPt / 3))) + (gridMatrix.Down * 12f);
                MyStringId material = MyStringId.GetOrCompute("SciFiEngineThrustMiddle");
                float thickness = MyUtils.GetRandomFloat(1.1f * (small ? 18 : 38), 1.8f * (small ? 18 : 38));

                MySimpleObjectDraw.DrawLine(startPos, endPos, material, ref color, thickness);
                MySimpleObjectDraw.DrawLine(startPos, endPos, material, ref color, thickness * 0.66f);
                MySimpleObjectDraw.DrawLine(startPos, endPos, material, ref color, thickness * 0.33f);
            }
            catch { }
        }

        public bool IsPilot(IMyPlayer player)
        {
            return controlSeat != null && player?.Character?.Parent?.EntityId == controlSeat.EntityId;
        }

        public void RequestToggle(WarpDrive drive, IMyPlayer player)
        {
            if (!MyAPIGateway.Multiplayer.IsServer || drive?.block == null || player == null)
                return;

            Wake();

            if (warpState == State.idle)
            {
                IMyShipController seat = player.Character?.Parent as IMyShipController;
                if (seat?.CubeGrid == null || !grid.Contains((MyCubeGrid)seat.CubeGrid) || !hasEnoughPower)
                    return;

                if (drive.isRestabilizing)
                {
                    SendMessage(warnRestabilizingHUD, 2f, "White", player.IdentityId);
                    return;
                }

                if (!IsDriveFunctional(drive))
                {
                    SendMessage(warnDamagedHUD, 5f, "Red", player.IdentityId);
                    return;
                }

                if (WarpDrive.ProximityDanger(grid.FindWorldMatrix(seat), drive.block.CubeGrid, 0, true))
                {
                    SendMessage(warnProximityHUD, 2f, "Red", player.IdentityId);
                    return;
                }

                StartCharging(drive, player.IdentityId, seat);
            }
            else
            {
                if (!IsPilot(player))
                {
                    SendMessage(warnDriveInUseHUD, 5f, "Red", player.IdentityId);
                    return;
                }

                Dewarp();
                primaryDrive?.IsRestabilizing();
            }
        }

        private void BroadcastState(bool clearSpeed = false)
        {
            if (!MyAPIGateway.Multiplayer.IsServer || primaryDrive?.block == null)
                return;

            MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.warpStatePacket,
                MyAPIGateway.Utilities.SerializeToBinary(new WarpStateMessage
                {
                    entityId = primaryDrive.block.EntityId,
                    state = (int)warpState,
                    speed = currentSpeedPt,
                    spoolTicksRemaining = warpState == State.charging ? (int)Math.Max(0L, spoolFinishTick - WarpDriveSession.instance.runtime) : 0,
                    enemyDelay = enemyInRange,
                    clearSpeed = clearSpeed,
                    cockpitId = controlSeat?.EntityId ?? 0
                }));
        }

        public void ApplyState(WarpDrive drive, WarpStateMessage message)
        {
            if (drive == null || message == null || MyAPIGateway.Multiplayer.IsServer)
                return;

            Wake();

            State newState = (State)message.state;

            if (newState == State.idle)
            {
                if (warpState != State.idle)
                    Dewarp(message.clearSpeed);

                return;
            }

            primaryDrive = drive;
            isPrototech = IsPrototech(drive);
            IMyEntity seat;
            controlSeat = MyAPIGateway.Entities.TryGetEntityById(message.cockpitId, out seat) ? seat as IMyShipController : null;

            if (newState == State.charging)
            {
                if (warpState != State.charging)
                {
                    warpState = State.charging;
                    UpdateHeatStats(true);
                }

                spoolFinishTick = WarpDriveSession.instance.runtime + message.spoolTicksRemaining;
                enemyInRange = message.enemyDelay;
                return;
            }

            currentSpeedPt = message.speed;

            if (warpState != State.active)
            {
                warpState = State.active;
                gridMatrix = grid.FindWorldMatrix(controlSeat);
                
                StopParticleEffect();

                if (sound != null)
                {
                    sound.PlaySound(isPrototech ? WarpSound.cruiseStartSoundProto : WarpSound.cruiseStartSound, true);
                    sound.VolumeMultiplier = 1;
                }
            }
        }

        private void ClientSpeedInput()
        {
            IMyPlayer localPlayer = MyAPIGateway.Session?.Player;
            if (localPlayer == null || primaryDrive?.block == null || !IsPilot(localPlayer))
                return;

            bool forwardPressed = MyAPIGateway.Input.IsGameControlPressed(MyControlsSpace.FORWARD);
            bool backwardPressed = MyAPIGateway.Input.IsGameControlPressed(MyControlsSpace.BACKWARD);
            int direction = 0;

            if (forwardPressed && !backwardPressed && speedUpSendToServerTick++ >= 10)
            {
                speedUpSendToServerTick = 0;
                direction = 1;
            }
            else if (backwardPressed && !forwardPressed && speedDownSendToServerTick++ >= 10)
            {
                speedDownSendToServerTick = 0;
                direction = -1;
            }

            if (direction == 0)
                return;

            if (MyAPIGateway.Multiplayer.IsServer)
                ApplySpeedStep(direction);
            else
                WarpDriveSession.instance.TransmitWarpSpeed(primaryDrive.block, direction);
        }

        public void ReadClientSpeedInput(WarpDrive drive, IMyPlayer player, double request)
        {
            if (warpState != State.active || request == 0 || drive == null || drive != primaryDrive || !IsPilot(player))
                return;

            ApplySpeedStep(request > 0 ? 1 : -1);
        }

        private void ApplySpeedStep(int direction)
        {
            currentSpeedPt += direction * ((isPrototech ? 2000d : 1000d) / 60d);

            if (direction < 0 && currentSpeedPt < 1f)
                currentSpeedPt = -5f;
        }

        private void BroadcastSpeed()
        {
            if (!MyAPIGateway.Multiplayer.IsServer || primaryDrive?.block == null)
                return;

            MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.warpSpeedPacket,
                MyAPIGateway.Utilities.SerializeToBinary(new SpeedMessage
                {
                    entityId = primaryDrive.block.EntityId,
                    warpSpeed = currentSpeedPt
                }));
        }

        public void ApplyServerSpeed(double speed)
        {
            if (MyAPIGateway.Multiplayer.IsServer || warpState != State.active)
                return;

            currentSpeedPt = speed;
        }

        public bool Contains(WarpDrive drive)
        {
            return grid.Contains((MyCubeGrid)drive.block.CubeGrid);
        }

        private void GetOnboardPlayers()
        {
            foreach (MyCubeGrid connectedGrid in grid.gridGroup)
            {
                foreach (var block in connectedGrid.GetFatBlocks())
                {
                    var pilot = (block as IMyCockpit)?.Pilot ?? (block as IMyCryoChamber)?.Pilot;
                    if (pilot == null)
                        continue;

                    foreach (var onlinePlayer in onlinePlayers)
                    {
                        if (onlinePlayer.Character == null || onlinePlayer.Character.EntityId != pilot.EntityId || playersInWarpList.Contains(onlinePlayer))
                            continue;

                        playersInWarpList.Add(onlinePlayer);

                        var oxygenComponent = onlinePlayer.Character.Components?.Get<MyCharacterOxygenComponent>();
                        if (oxygenComponent != null)
                            playerOxygenSnapshot[onlinePlayer.IdentityId] = oxygenComponent.SuitOxygenLevel;
                    }
                }
            }
        }

        public bool IsPlayerSeated(long characterEntityId)
        {
            if (warpState != State.active)
                return false;

            foreach (var player in playersInWarpList)
            {
                if (player?.Character != null && player.Character.EntityId == characterEntityId)
                    return true;
            }

            return false;
        }
        
        private void PlayerO2Pause()
        {
            foreach (var player in playersInWarpList)
            {
                if (player?.Character == null)
                    continue;

                float snapshot;
                if (!playerOxygenSnapshot.TryGetValue(player.IdentityId, out snapshot))
                    continue;

                var oxygenComponent = player.Character.Components?.Get<MyCharacterOxygenComponent>();
                if (oxygenComponent != null && oxygenComponent.SuitOxygenLevel != snapshot)
                    oxygenComponent.SuitOxygenLevel = snapshot;
            }
        }

        public static void PlayerO2DamagePause(object target, ref MyDamageInformation info, IReadOnlyList<WarpSystem> systems)
        {
            if (info.Type != MyDamageType.Asphyxia && info.Type != MyDamageType.LowPressure)
                return;

            var character = target as IMyCharacter;
            if (character == null)
                return;

            foreach (WarpSystem system in systems)
            {
                if (system != null && system.IsPlayerSeated(character.EntityId))
                {
                    info.Amount = 0f;
                    return;
                }
            }
        }

        public bool OccupantLeft(long identityId)
        { //Stops cruise on player(s) standing up during cruise
            if (!MyAPIGateway.Multiplayer.IsServer || warpState != State.active)
                return false;

            if (!playersInWarpList.Any(player => player != null && player.IdentityId == identityId))
                return false;

            foreach (var player in playersInWarpList)
            {
                if (player != null)
                    MyVisualScriptLogicProvider.ShowNotification(warnNotSeatedHUD, 5000, "Red", player.IdentityId);
            }

            Dewarp(true);
            primaryDrive?.IsRestabilizing();
            return true;
        }
        
        public void PilotLeft(IMyShipController cockpit)
        {//Stops spool on pilot standing up during spool
            if (!MyAPIGateway.Multiplayer.IsServer || warpState == State.idle || cockpit == null || controlSeat?.EntityId != cockpit.EntityId)
                return;

            Dewarp(warpState == State.active);
            primaryDrive?.IsRestabilizing();
        }

        public void PilotDisconnected(long identityId)
        {
            if (!MyAPIGateway.Multiplayer.IsServer || warpState == State.idle || pilotId == 0 || identityId != pilotId)
                return;

            Dewarp(true);
            primaryDrive?.IsRestabilizing();
        }

        private bool ConnectedStatic(IMyCubeGrid myGrid)
        {
            if (myGrid == null)
                return false;

            var attachedList = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(myGrid, GridLinkTypeEnum.Physical, attachedList);

            if (attachedList.Count > 1)
            {
                foreach (var attachedGrid in attachedList)
                {
                    if (attachedGrid != null)
                    {
                        if (attachedGrid.IsStatic)
                            return true;
                    }
                }
            }
            return false;
        }

        private void StartCharging(WarpDrive drive, long playerID, IMyShipController seat)
        {
            if (grid.mainGrid == null)
                return;

            if (IsInGravity())
            {
                SendMessage(warnPlanetIntHUD, 5f, "Red", playerID);
                return;
            }

            if (grid.isStatic || ConnectedStatic(grid.mainGrid))
            {
                SendMessage(warnIsStaticHUD, 5f, "Red", playerID);
                return;
            }

            UpdateHeatStats(true);

            bool prototech = IsPrototech(drive);
            float capacity = GetHeatCapacity(prototech, cachedActiveSinks, cachedActiveSmallSinks);

            if (totalHeat > drive.heat)
            {
                SendMessage(warnCoolingHUD, 5f, "Red", playerID);
                return;
            }

            if (drive.heat >= capacity)
            {
                SendMessage(warnOverheatHUD, 5f, "Red", playerID);
                return;
            }

            primaryDrive = drive;
            isPrototech = prototech;
            heatCapacity = capacity;

            warpState = State.charging;
            startChargeRuntime = WarpDriveSession.instance.runtime;
            powerCheckTick = 0;

            enemyInRange = Settings.instance.AllowToDetectEnemyGrids && WarpDrive.EnemyProximityCharge(grid.mainGrid);
            GetSpoolTimer();

            pilotId = playerID;
            controlSeat = seat;

            BroadcastState();
        }

        private void GetSpoolTimer()
        {
            Settings settings = Settings.instance;
            double seconds = isPrototech ? settings.PrototechJump : settings.DelayJump;

            if (settings.AllowToDetectEnemyGrids && enemyInRange)
                seconds = Math.Max(seconds, settings.DelayJumpIfEnemyIsNear);

            spoolFinishTick = startChargeRuntime + (long)(seconds * 60);
        }

        private void StartWarp()
        {
            warpState = State.active;

            GyroNerfer(true);

            gridMatrix = grid.FindWorldMatrix(controlSeat);

            currentSpeedPt = inAllowedGravity ? 1000 / 60d : Settings.instance.startSpeed;

            GetOnboardPlayers();

            BroadcastState();

            StopParticleEffect();

            if (sound != null)
            {
                sound.PlaySound(isPrototech ? WarpSound.cruiseStartSoundProto : WarpSound.cruiseStartSound, true);
                sound.VolumeMultiplier = 1;
            }
            }

        private void GyroNerfer(bool cruising)
        {
            if (!MyAPIGateway.Utilities.IsDedicated && !MyAPIGateway.Multiplayer.IsServer)
                return;

            if (!cruising)
            {
                foreach (var pair in gyroBlocks)
                {
                    if (pair.Key != null && !pair.Key.Closed)
                        pair.Key.GyroStrengthMultiplier = pair.Value;
                }
                gyroBlocks.Clear();
                return;
            }

            if (grid == null)
                return;

            foreach (MyCubeGrid g in grid.gridGroup)
            {
                foreach (MyCubeBlock block in g.GetFatBlocks())
                {
                    IMyGyro gyro = block as IMyGyro;
                    if (gyro == null || gyroBlocks.ContainsKey(gyro))
                        continue;

                    gyroBlocks[gyro] = gyro.GyroStrengthMultiplier;
                    gyro.GyroStrengthMultiplier = gyro.GyroStrengthMultiplier * 0.25f;
                }
            }
        }

        private bool IsDriveFunctional(WarpDrive drive)
        {
            HashSet<WarpDrive> gridDrives;
            return drive?.block != null && !drive.block.MarkedForClose && drive.block.CubeGrid != null && !drive.block.CubeGrid.MarkedForClose
                && warpDrives.TryGetValue(drive.block.CubeGrid, out gridDrives) && gridDrives.Contains(drive)
                && drive.block.IsFunctional && drive.block.IsWorking;
        }

        public static bool IsPrototech(WarpDrive drive)
        {
            return drive?.block != null && new[] { "PrototechFSDriveLarge", "PrototechFSDriveSmall" }.Contains(drive.block.BlockDefinition.SubtypeId);
        }

        public static bool IsSmallDrive(WarpDrive drive)
        {
            return drive?.block != null && new[] { "FSDriveSmall", "PrototechFSDriveSmall" }.Contains(drive.block.BlockDefinition.SubtypeId);
        }

        public void Dewarp(bool collision = false)
        {
            GyroNerfer(false);

            Wake();

            var mainGrid = grid?.mainGrid;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                StopParticleEffect();

                if (sound != null)
                {
                    if (mainGrid != null)
                sound.SetPosition(mainGrid.PositionComp.GetPosition());

                    sound.StopSound(warpState == State.active);

                    if (warpState == State.active)
                    {
                        sound.PlaySound(isPrototech ? WarpSound.cruiseEndSoundProto : WarpSound.cruiseEndSound, true);
                        sound.VolumeMultiplier = 1;
                    }
                }
            }

            if (warpState == State.active && mainGrid?.Physics != null)
            {
                if (collision)
                    mainGrid.Physics.ClearSpeed();
                else
                    mainGrid.Physics.LinearVelocity = Vector3.Zero;
            }

            if (warpState != State.idle)
            {
                warpState = State.idle;
                BroadcastState(collision);
            }

            currentSpeedPt = Settings.GetShared().startSpeed;
            enemyInRange = false;

                playersInWarpList.Clear();
                playerOxygenSnapshot.Clear();

            pilotId = 0;
            controlSeat = null;
        }

        private void InSpool()
        {
            if (grid.mainGrid == null)
                return;

            var mainGrid = grid.mainGrid;

            if (MyAPIGateway.Multiplayer.IsServer)
            {
                if (!primaryFunctional)
                {
                SendMessage(warnDamagedHUD);
                Dewarp();
                return;
                }

            if (!hasEnoughPower)
                {
                SendMessage(warnPowerHUD);
                Dewarp();
                return;
                }

            if (IsInGravity())
                {
                SendMessage(warnPlanetIntHUD);
                Dewarp();
                return;
                }

                if (grid.isStatic || ConnectedStatic(mainGrid))
                {
                SendMessage(warnIsStaticHUD);
                Dewarp();
                return;
                }

                if (Settings.instance.AllowToDetectEnemyGrids && WarpDriveSession.instance.runtime % 30 == 0)
                {
                    bool enemyFound = WarpDrive.EnemyProximityCharge(mainGrid);
                    if (enemyFound != enemyInRange)
                    {
                        enemyInRange = enemyFound;
                        GetSpoolTimer();
                        BroadcastState();
                    }
                }

                if (WarpDriveSession.instance.runtime >= spoolFinishTick)
                {
                    StartWarp();
                return;
                }
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                long ticksLeft = spoolFinishTick - WarpDriveSession.instance.runtime;

                if (effect == null)
                {
                    if (ticksLeft <= 600)
                    {
                        PlayParticleEffect();

                        if (effect != null && sound != null)
                        {
                            sound.PlaySound(isPrototech ? WarpSound.cruiseSpoolSoundProto : WarpSound.cruiseSpoolSound, true);
                            sound.VolumeMultiplier = isPrototech ? 1 : 2;
                        }
                    }
                }
                else if (ticksLeft > 600)
                {
                    StopParticleEffect(true);
                    sound?.StopSound(true);
                }

                if (effect != null)
                    effect.WorldMatrix = MatrixD.CreateWorld(effect.WorldMatrix.Translation, -gridMatrix.Forward, gridMatrix.Up);

                UpdateParticleEffect();
            }
        }

        bool IsInGravity()
        {
            if (grid?.mainGrid == null)
                    return true;

            float gravity = GridGravityNow();

            if (!Settings.instance.AllowInGravity)
                return gravity > 0.01;

            if (gravity > Settings.instance.AllowInGravityMax)
                return true;

            if (gravity <= 0 || grid.mainGrid.Physics == null)
                return false;

            BoundingBoxD worldAABB = grid.mainGrid.PositionComp.WorldAABB;
            var closestPlanet = MyGamePruningStructure.GetClosestPlanet(ref worldAABB);
            if (closestPlanet == null)
                return false;

            Vector3D centerOfMassWorld = grid.mainGrid.Physics.CenterOfMassWorld;
            return Vector3D.Distance(closestPlanet.GetClosestSurfacePointGlobal(ref centerOfMassWorld), centerOfMassWorld) < Settings.instance.AllowInGravityMinAltitude;
        }

        float GridGravityNow()
        {
            if (grid?.mainGrid == null)
                return 0f;

            float naturalGravityInterference;
            return MyAPIGateway.Physics.CalculateNaturalGravityAt(grid.mainGrid.PositionComp.GetPosition(), out naturalGravityInterference).Length() / 9.806652f;
        }

        private void CheckGravityStateChange()
        {
            if (MyAPIGateway.Utilities.IsDedicated || sound == null)
                return;

            if (warpState != State.active)
            {
                wasInGravity = null;
                return;
            }

            bool inGravityNow = GridGravityNow() > 0.01f;

            if (wasInGravity == null)
            {
                wasInGravity = inGravityNow;
                return;
            }

            if (inGravityNow == wasInGravity.Value)
                return;

            wasInGravity = inGravityNow;

            sound.PlaySound(inGravityNow ? WarpSound.gravityGlideOn : WarpSound.gravityGlideOff, true);
        }

        private void UpdateHeatPower()
        {
            hasEnoughPower = true;

            try
            {
                Settings settings = Settings.instance;

                primaryFunctional = warpState != State.idle && IsDriveFunctional(primaryDrive);

                if (primaryFunctional && (WarpDriveSession.instance.runtime % 11 == 0 || primaryDrive.requiredPower == 0))
                {
                    float mass = GetShipMass();
                    primaryDrive.requiredPower = warpState == State.charging ? GetSpoolPower(primaryDrive, mass) : GetCruisePower(primaryDrive, mass, currentSpeedPt);
                }

                int hotDrives = 0;

                foreach (HashSet<WarpDrive> gridDrives in warpDrives.Values)
                {
                    foreach (WarpDrive drive in gridDrives)
                    {
                        if (drive == null || drive.block == null)
                            continue;

                        if (drive.requiredPower != 0 && !(primaryFunctional && drive == primaryDrive))
                            drive.requiredPower = 0;

                        if (drive.heat > 0f)
                            hotDrives++;
                    }
                }

                // give SIM some chance before drop warp if power check missed.
                if (primaryFunctional && MyAPIGateway.Multiplayer.IsServer && powerCheckTick++ > 20)
                {
                    powerCheckTick = 0;

                    if (!primaryDrive.hasPower)
                    {
                        if (currentSpeedPt > 90)
                            currentSpeedPt -= 90f;
                        else
                            hasEnoughPower = false;
                    }
                }
                
                float displayPower = primaryFunctional ? primaryDrive.requiredPower : 0f;

                heatGenerationRate = warpState == State.active && settings.heatGain > 0f
                    ? displayPower / (isPrototech ? heatPerMWDivisorPrototech : heatPerMWDivisor) * (1f + MathHelper.Clamp(GridGravityNow() / Math.Max(settings.AllowInGravityMax, 0.0001f), 0f, 1f)) * settings.heatGain / 60f
                    : 0f;

                totalHeat = 0f;
                driveHeat = 0;

                if (hotDrives > 0 || heatGenerationRate > 0f)
                {
                    foreach (HashSet<WarpDrive> gridDrives in warpDrives.Values)
                    {
                        foreach (WarpDrive drive in gridDrives)
                        {
                            if (drive == null || drive.block == null)
                                continue;

                            if (drive.heat > 0f)
                                drive.heat = Math.Max(drive.heat - heatDissipationRate / hotDrives, 0f);

                            if (drive == primaryDrive && heatGenerationRate > 0f && drive.heat < heatCapacity)
                                drive.heat = Math.Min(drive.heat + heatGenerationRate, heatCapacity);

                            totalHeat += drive.heat;
                            driveHeat = Math.Max(driveHeat, (int)(drive.heat / GetHeatCapacity(IsPrototech(drive), cachedActiveSinks, cachedActiveSmallSinks) * 100));
                        }
                    }
                }

                if ((warpState != State.idle || driveHeat > 0) && _updateTicks++ >= (MyAPIGateway.Utilities.IsDedicated ? 61 : 62))
                {
                    _updateTicks = 0;

                    bool enemyDelay = warpState == State.charging && settings.AllowToDetectEnemyGrids && enemyInRange;

                    if (warpState == State.active)
                        SendMessage($"Speed: {currentSpeedPt * 60 / 1000:0} km/s", 1f, "White");
                    else if (enemyDelay)
                        SendMessage("Enemy Detected! Spooling Delayed!", 1f, "Red");

                    if (driveHeat > 0)
                        SendMessage($"Heat Level: {driveHeat}%" + (driveHeat >= 75 ? "!" : "") + (driveHeat >= 85 ? "*" : "") + (driveHeat >= 90 ? "*" : "") + (driveHeat >= 95 ? "*" : ""),
                            1f, driveHeat >= (warpState == State.charging ? 65 : 85) ? "Red" : "White");

                    if (warpState == State.active)
                        SendMessage($"Power Usage : {displayPower:0.00}Mw", 1f, "White");
                    else if (warpState == State.charging)
                        SendMessage($"Power Usage: {displayPower:0.00}Mw\nSeconds to Warp: {(Math.Max(0L, spoolFinishTick - WarpDriveSession.instance.runtime) + 59) / 60}",
                            1f, enemyDelay ? "Red" : "White");
                }
            }
            catch { }
        }

        public static float GetSpoolPower(WarpDrive drive, float mass)
        {
            if (drive?.block == null || Settings.instance == null)
                return 0f;

            return (IsPrototech(drive) ? 0.5f : 1f) * (IsSmallDrive(drive) ? Settings.instance.baseRequiredPowerSmall : Settings.instance.baseRequiredPower) + (mass * 2.1f / 100000f);
        }

        public static float GetCruisePower(WarpDrive drive, float mass, double speedPt)
        {
            Settings settings = Settings.instance;
            if (drive?.block?.CubeGrid == null || settings == null)
                return 0f;

            float speedNormalize = (float)(speedPt * 0.06); // 60 / 1000
            float percent = (float)(1f + speedPt / settings.maxSpeed * settings.powerRequirementMultiplier)
                + mass * ((1f + (speedNormalize * speedNormalize)) / 0.528f) / (drive.block.CubeGrid.GridSizeEnum == MyCubeSize.Small ? 700000f : 1000000f);

            if (percent == 0)
                percent = 1;

            return ((IsSmallDrive(drive) ? settings.baseRequiredPowerSmall : settings.baseRequiredPower) * (IsPrototech(drive) ? 0.5f : 1f) + percent)
                / (IsSmallDrive(drive) ? settings.powerRequirementBySpeedDeviderSmall : settings.powerRequirementBySpeedDeviderLarge);
        }

        private float GetHeatCapacity(bool prototech, int sinks, int smallSinks)
        {
            float capacityPerSink = Settings.instance?.HeatSinkCapacityBonus ?? 50f;

            return Math.Max(0f, (prototech ? 300f : 200f) * (isSmall ? smallRatio : 1f) * (Settings.instance?.maxHeat ?? 1f)
                + (((sinks - smallSinks) * capacityPerSink) + (smallSinks * capacityPerSink * smallRatio)));
        }

        private void UpdateHeatStats(bool apply)
        {
            cachedActiveSinks = grid.CountActiveSinks(out cachedActiveSmallSinks, apply);
            heatCapacity = GetHeatCapacity(isPrototech, cachedActiveSinks, cachedActiveSmallSinks);
            heatDissipationRate = (cachedActiveSinks - cachedActiveSmallSinks + cachedActiveSmallSinks * smallRatio) * Settings.instance.HeatSinkDissipation
                + Settings.instance.heatDissipationDrive * (isSmall ? smallRatio : 1f);
        }

        public void HeatInfo(WarpDrive drive, out float capacity, out float breakEvenPower)
        {   // Terminal UI info
            bool prototech = warpState == State.idle ? IsPrototech(drive) : isPrototech;

            if (isSleeping && grid != null)
                UpdateHeatStats(false);

            capacity = GetHeatCapacity(prototech, cachedActiveSinks, cachedActiveSmallSinks);
            breakEvenPower = Settings.instance.heatGain > 0f
                ? heatDissipationRate * 60f * (prototech ? heatPerMWDivisorPrototech : heatPerMWDivisor) / Settings.instance.heatGain
                : 0f;
        }

        private void PlayParticleEffect()
        {
            if (grid.mainGrid == null)
                return;
                
            gridMatrix = grid.FindWorldMatrix(controlSeat);

            if (effect != null)
            {
                effect.Play();
                return;
            }

            MatrixD fromDir = MatrixD.CreateFromDir(-gridMatrix.Forward);
            Vector3D origin = grid.mainGrid.PositionComp.WorldAABB.Center;
            fromDir.Translation = effectPosition;

            MyParticlesManager.TryCreateParticleEffect(isPrototech ? "Warp_Prototech" : "WarpStart", ref fromDir, ref origin, uint.MaxValue, out effect);

            if (effect != null)
            {
                BoundingBox localBox = ((IMyCubeGrid)grid.mainGrid).LocalAABB;
                effect.UserScale = Math.Max(localBox.Width, localBox.Height) / (grid.mainGrid.GridSizeEnum == MyCubeSize.Large ? 60 : 30);
            }
        }

        private Vector3D effectPosition => grid.mainGrid.PositionComp.WorldAABB.Center + gridMatrix.Forward * grid.mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax() * 2.0;

        private void UpdateParticleEffect()
        {
            if (effect == null || effect.IsStopped || grid.mainGrid == null)
                return;

            Vector3D origin = effectPosition;
            effect.SetTranslation(ref origin);
        }

        private void StopParticleEffect(bool instantly = false)
        {
            if (effect == null)
                return;

            if (instantly)
                effect.Stop();
            else
                effect.StopEmitting(10f);

                effect = null;
        }

        public float GetShipMass()
        {
            float baseMass = 0f;
            float physicalMass = 0f;
            float mass = grid?.mainGrid?.GetCurrentMass(out baseMass, out physicalMass, GridLinkTypeEnum.Physical) ?? 0f;

            return mass > 0f ? mass : 1f;
        }

        private void OnSystemInvalidated(GridSystem system)
        {
            if (warpState != State.idle)
            {
                if (MyAPIGateway.Multiplayer.IsServer)
                    SendMessage(warnConnectionHUD);

                Dewarp(warpState == State.active);
            }

            GyroNerfer(false);

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                sound?.StopSound(true);
                effect?.Stop();
            }
            OnSystemInvalidatedAction?.Invoke(this);
            OnSystemInvalidatedAction = null;
        }

        public void SendMessage(string msg, float seconds = 5, string font = "Red", long playerID = 0L)
        {
            var hostplayer = MyAPIGateway.Session?.Player;
            var cockpit = hostplayer?.Character?.Parent as IMyShipController;

            if (onlinePlayers != null && onlinePlayers.Count > 0 && playerID > 0)
            {
                foreach (var selectedPlayer in onlinePlayers)
                {
                    if (selectedPlayer.IdentityId == playerID)
                    {
                        MyVisualScriptLogicProvider.ShowNotification(msg, (int)(seconds * 1000), font, selectedPlayer.IdentityId);
                        return;
                    }
                }
            }

            if (hostplayer != null && cockpit?.CubeGrid != null && grid.Contains((MyCubeGrid)cockpit.CubeGrid))
                MyVisualScriptLogicProvider.ShowNotification(msg, (int)(seconds * 1000), font, hostplayer.IdentityId);

            if (onlinePlayers != null && onlinePlayers.Count > 0)
            {
                foreach (var clientPlayer in onlinePlayers)
                {
                    if (hostplayer != null && clientPlayer.IdentityId == hostplayer.IdentityId)
                        continue;

                    var clientCockpit = clientPlayer?.Character?.Parent as IMyShipController;

                    if (clientCockpit?.CubeGrid != null && grid.Contains((MyCubeGrid)clientCockpit.CubeGrid))
                        MyVisualScriptLogicProvider.ShowNotification(msg, (int)(seconds * 1000), font, clientPlayer.IdentityId);
                }
            }
        }

        private void OnDriveAdded(IMyCubeBlock block)
        {
            WarpDrive drive = block.GameLogic.GetAs<WarpDrive>();
            HashSet<WarpDrive> gridDrives;
            drive.SetWarpSystem(this);

            if (!warpDrives.TryGetValue(block.CubeGrid, out gridDrives))
                gridDrives = new HashSet<WarpDrive>();

            if (gridDrives.Add(drive))
                totalHeat += drive.heat;
                warpDrives[block.CubeGrid] = gridDrives;

            Wake();
        }

        private void OnDriveRemoved(IMyCubeBlock block)
        {
            WarpDrive drive = block.GameLogic.GetAs<WarpDrive>();
            HashSet<WarpDrive> gridDrives;

            if (warpDrives.TryGetValue(block.CubeGrid, out gridDrives))
            {
                if (gridDrives.Remove(drive))
                    totalHeat = Math.Max(totalHeat - drive.heat, 0f);

                if (gridDrives.Count > 0)
                    warpDrives[block.CubeGrid] = gridDrives;
                else
                    warpDrives.Remove(block.CubeGrid);
            }

            Wake();

            if (drive != null && drive == primaryDrive)
            {
                if (warpState != State.idle)
                {
                    if (MyAPIGateway.Multiplayer.IsServer)
                        SendMessage(warnDamagedHUD);

                    Dewarp();
                }

                primaryDrive = null;
            }
        }

        public override bool Equals(object obj)
        {
            var system = obj as WarpSystem;
            return system != null && id == system.id;
        }

        public override int GetHashCode()
        {
            return 2108858624 + id.GetHashCode();
        }

        public enum State
        {
            idle, charging, active
        }
    }
}
