using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    public class WarpSystem : IMyOxygenProvider
    {
        public bool valid => grid != null && grid.valid;
        public int id { get; private set; }
        public State warpState { get; set; }
        public event Action<WarpSystem> OnSystemInvalidatedAction;
        public List<IMyPlayer> onlinePlayers = new List<IMyPlayer>();
        public double currentSpeedPt = Settings.GetShared().startSpeed;
        public double throttleRatio = 0.0;
        public int driveHeat { get; set; }
        public float heatGenerationRate { get; private set; }
        public float heatDissipationRate { get; private set; }
        public float heatCapacity { get; private set; }
        public GridSystem grid;
        public bool isSleeping { get; private set; }
        public HyperspaceSystem hyperspace { get; private set; }
        public CapitalFSD capitalFSD { get; private set; }
        public int functionalDrivesCount
        {
            get
            {
                int count = 0;
                foreach (var set in warpDrives.Values)
                {
                    foreach (var drive in set)
                    {
                        if (drive != null && drive.block != null && drive.block.IsFunctional)
                            count++;
                    }
                }
                return count;
            }
        }

        private MatrixD gridMatrix;
        private readonly Dictionary<IMyCubeGrid, HashSet<WarpDrive>> warpDrives = new Dictionary<IMyCubeGrid, HashSet<WarpDrive>>();
        private readonly List<IMyPlayer> playersInWarpList = new List<IMyPlayer>();
        private readonly Dictionary<long, float> playerOxygenSnapshot = new Dictionary<long, float>();
        private long pilotId;
        private IMyShipController controlSeat;
        private readonly Dictionary<IMyGyro, float> gyroBlocks = new Dictionary<IMyGyro, float>();
        private MyParticleEffect effect;
        private readonly MyEntity3DSoundEmitter sound;
        private readonly MyEntity3DSoundEmitter loopSound;
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
        public const float EARTH_GRAVITY = 9.806652f;
        public const double MIN_CRUISE_SPEED_MPS = 500.0;
        public const double MIN_CRUISE_SPEED_PT = MIN_CRUISE_SPEED_MPS / 60.0;

        public const double SPACE_LOW_DECK_MPS = 2500.0;
        public const double GRAVITY_LOW_DECK_MPS = 500.0;
        public const double GRAVITY_HIGH_DECK_MPS = 2500.0;

        private static readonly Dictionary<long, float> _planetSurfaceGravCache = new Dictionary<long, float>();

        public static string FormatSpeed(double speedPt)
        {
            double mps = Math.Max(0.0, speedPt * 60.0);
            if (mps < 1000.0)
                return $"{mps:0} m/s";
            if (mps < 10000.0)
                return $"{mps / 1000.0:0.0} km/s";
            return $"{mps / 1000.0:0} km/s";
        }

        private void SetWarpPlayersSave(bool save)
        {
            foreach (var player in playersInWarpList)
            {
                if (player?.Character == null)
                    continue;

                if (player.Character.Save != save)
                    player.Character.Save = save;
            }
        }

        public string warnPlanetIntHUD = "Planetary interference detected!";
        public string warnDamagedHUD = "FS Drive Offline or Damaged!";
        public string warnPowerHUD = "Not enough power!";
        public string emergencyStopHUD = "Emergency Stop!";
        public string warnIsStaticHUD = "Unable to move static grid!";
        public string warnOverheatHUD = "FS Drive overheated!";
        public string warnProximityHUD = "Can't Start FS Drive. Proximity Alert!";
        public string warnCharacterHUD = "Emergency Stop! Lifeform detected!";
        public string warnNotSeatedHUD = "Emergency Stop! Occupant not seated!";
        public string warnDriveInUseHUD = "Another seat is piloting this grid!";
        public string warnRestabilizingHUD = "FS Drive Restabilizing...";
        public string warnConnectionHUD = "Emergency Stop! Connection changes detected!";
        public string warnCoolingHUD = "Cannot start FS Drive. Another drive is still cooling!";

        private const float heatPerMWDivisor = 100f;
        private const float heatPerMWDivisorPrototech = 150f;
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
                loopSound = new MyEntity3DSoundEmitter(grid.mainGrid)
                {
                    CanPlayLoopSounds = true
                };
            }

            UpdateHeatStats(totalHeat > 0f);

            if (block.isCapitalFSD && capitalFSD == null)
                capitalFSD = new CapitalFSD(block);
            else if (block.supportsHyperspace && hyperspace == null)
                hyperspace = new HyperspaceSystem(block, grid);

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
                if (!MyAPIGateway.Utilities.IsDedicated)
                {
                    if (sound != null)
                        sound.SetPosition(mainGrid.PositionComp.GetPosition());

                    if (loopSound != null)
                    {
                        loopSound.SetPosition(mainGrid.PositionComp.GetPosition());

                        if (!loopSound.IsPlaying)
                            loopSound.PlaySound(WarpSound.cruiseOperatingSound, true);
                    }

                    float shake = GetSupercruiseGravityShakeMagnitude();
                    if (shake > 0.001f)
                    {
                        var activeCockpit = grid?.FindMainCockpit() as MyCockpit;
                        if (activeCockpit != null)
                            activeCockpit.AddShake(shake);
                    }
                }

                if (InWarp() && currentSpeedPt > 1f)
                {
                    gridMatrix.Translation += gridMatrix.Forward * currentSpeedPt;

                    if (MyAPIGateway.Multiplayer.IsServer)
                        mainGrid.Teleport(gridMatrix);
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

            PlayerO2Pause();

            if (!MyAPIGateway.Multiplayer.IsServer)
            {
                ClientSpeedInput();

                if (currentSpeedPt <= -1f)
                {
                    Dewarp();
                    primaryDrive?.IsRestabilizing();
                    return false;
                }

                double tClient = GetGravityInfluenceFactor();
                double lowClient = GetCurrentLowDeckMps(tClient);
                double highClient = GetCurrentHighDeckMps(tClient);
                currentSpeedPt = MathHelper.Lerp(lowClient, highClient, throttleRatio) / 60.0;

                return true;
            }

            if (IsInGravity())
            {
                SendMessage(warnPlanetIntHUD);

                Dewarp(inAllowedGravity);

                return false;
            }

            IMyEntity obstacle = WarpDrive.ProximityDanger(gridMatrix, grid, currentSpeedPt);
            if (obstacle != null)
            {
                SendMessage(obstacle is IMyCharacter ? warnCharacterHUD : emergencyStopHUD);

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

            if (currentSpeedPt <= -1f)
            {
                Dewarp();
                primaryDrive?.IsRestabilizing();

                return false;
            }

            double t = GetGravityInfluenceFactor();
            double lowDeckMps = GetCurrentLowDeckMps(t);
            double highDeckMps = GetCurrentHighDeckMps(t);
            currentSpeedPt = MathHelper.Lerp(lowDeckMps, highDeckMps, throttleRatio) / 60.0;

            if (WarpDriveSession.instance.runtime % 11 == 0)
                BroadcastSpeed();

            return true;
        }

        public void Draw()
        {
            hyperspace?.DrawTargetReticle();
            capitalFSD?.DrawTargetReticle();

            if (warpState != State.active)
                return;

            DrawCruiseLine(Color.CornflowerBlue, 1000, 180);

            if (currentSpeedPt < 316.6666)
                DrawCruiseLine(Color.Indigo, 800, 220);

            if (currentSpeedPt <= 1f)
                return;

            DrawCruiseLine(Color.SteelBlue, 1200, 240);

            if (currentSpeedPt > 316.6666)
                DrawCruiseLine(Color.LightGoldenrodYellow, 1500, 90);
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

                if ((!Settings.instance.AllowInSafeZone && !MySessionComponentSafeZones.IsActionAllowed(grid.mainGrid.PositionComp.WorldAABB.Center, MySafeZoneAction.Damage))
                    || WarpDrive.ProximityDanger(grid.FindWorldMatrix(seat), grid, 0, true) != null)
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
            double tState = GetGravityInfluenceFactor();
            double lowState = GetCurrentLowDeckMps(tState);
            double highState = GetCurrentHighDeckMps(tState);
            throttleRatio = MathHelper.Clamp((message.speed * 60.0 - lowState) / Math.Max(1.0, highState - lowState), 0.0, 1.0);

            if (warpState != State.active)
            {
                warpState = State.active;
                gridMatrix = grid.FindWorldMatrix(controlSeat);
                GyroNerfer(true);
                GetOnboardPlayers();
                
                StopParticleEffect();

                if (sound != null)
                {
                    sound.PlaySound(isPrototech ? WarpSound.cruiseStartSoundProto : WarpSound.cruiseStartSound, true);
                    sound.VolumeMultiplier = 1;
                }

                if (loopSound != null)
                {
                    loopSound.PlaySound(WarpSound.cruiseOperatingSound, true);
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
            if (direction == 0)
                return;

            double currentMps = currentSpeedPt * 60.0;
            double t = GetGravityInfluenceFactor();
            double currentLowMps = GetCurrentLowDeckMps(t);
            double currentHighMps = GetCurrentHighDeckMps(t);

            if (direction < 0 && currentMps <= currentLowMps + 1.0)
            {
                currentSpeedPt = -5.0;
                return;
            }

            double stepMps;
            if (currentMps < 1500.0)
                stepMps = isPrototech ? 100.0 : 50.0;
            else if (currentMps < 5000.0)
                stepMps = isPrototech ? 500.0 : 250.0;
            else if (currentMps < 20000.0)
                stepMps = isPrototech ? 2000.0 : 1000.0;
            else
                stepMps = isPrototech ? 5000.0 : 2500.0;

            double newMps = currentMps + (direction * stepMps);

            if (direction < 0 && newMps < currentLowMps)
                newMps = currentLowMps;

            newMps = MathHelper.Clamp(newMps, currentLowMps, currentHighMps);
            currentSpeedPt = newMps / 60.0;
            throttleRatio = MathHelper.Clamp((newMps - currentLowMps) / Math.Max(1.0, currentHighMps - currentLowMps), 0.0, 1.0);
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
            double t = GetGravityInfluenceFactor();
            double lowMps = GetCurrentLowDeckMps(t);
            double highMps = GetCurrentHighDeckMps(t);
            throttleRatio = MathHelper.Clamp((speed * 60.0 - lowMps) / Math.Max(1.0, highMps - lowMps), 0.0, 1.0);
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
                        if (oxygenComponent != null && oxygenComponent.EnvironmentOxygenLevel >= MyCharacterOxygenComponent.LOW_OXYGEN_RATIO)
                            playerOxygenSnapshot[onlinePlayer.IdentityId] = oxygenComponent.SuitOxygenLevel;
                    }
                }
            }

            if (playerOxygenSnapshot.Count > 0)
                MyAPIGateway.Session.OxygenProviderSystem.AddOxygenGenerator(this);

            if (MyAPIGateway.Multiplayer.IsServer)
                SetWarpPlayersSave(false);
        }

        public bool IsPlayerSeated(long characterEntityId)
        {
            if (warpState != State.active)
                return false;

            foreach (var player in playersInWarpList)
            {
                if (player?.Character != null && player.Character.EntityId == characterEntityId)
                    return playerOxygenSnapshot.ContainsKey(player.IdentityId);
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

        public float GetOxygenForPosition(Vector3D worldPoint)
        {
            return 1f;
        }

        public bool IsPositionInRange(Vector3D worldPoint)
        {
            if (warpState != State.active)
                return false;

            foreach (var player in playersInWarpList)
            {
                if (player?.Character != null && playerOxygenSnapshot.ContainsKey(player.IdentityId) && player.Character.WorldAABB.Contains(worldPoint) != ContainmentType.Disjoint)
                    return true;
            }

            return false;
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

        public bool ConnectedStatic(IMyCubeGrid myGrid)
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
            float capacity = GetHeatCapacity(drive, cachedActiveSinks, cachedActiveSmallSinks);

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

            double t = GetGravityInfluenceFactor();
            double localLowMps = GetCurrentLowDeckMps(t);
            double localHighMps = GetCurrentHighDeckMps(t);
            double baseStartMps = inAllowedGravity ? 1000.0 : ((Settings.instance?.startSpeed ?? (1000.0 / 60.0)) * 60.0);
            double clampedStartMps = MathHelper.Clamp(baseStartMps, localLowMps, localHighMps);
            currentSpeedPt = clampedStartMps / 60.0;
            throttleRatio = MathHelper.Clamp((clampedStartMps - localLowMps) / Math.Max(1.0, localHighMps - localLowMps), 0.0, 1.0);

            GetOnboardPlayers();

            BroadcastState();

            StopParticleEffect();

            if (sound != null)
            {
                sound.PlaySound(isPrototech ? WarpSound.cruiseStartSoundProto : WarpSound.cruiseStartSound, true);
                sound.VolumeMultiplier = 1;
            }

            if (loopSound != null)
            {
                loopSound.PlaySound(WarpSound.cruiseOperatingSound, true);
            }
            }

        private void GyroNerfer(bool cruising)
        {
            if (MyAPIGateway.Utilities.IsDedicated)
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

                if (loopSound != null)
                {
                    if (mainGrid != null)
                        loopSound.SetPosition(mainGrid.PositionComp.GetPosition());

                    loopSound.StopSound(true);
                }

                if (sound != null)
                {
                    if (mainGrid != null)
                sound.SetPosition(mainGrid.PositionComp.GetPosition());

                    sound.StopSound(true);

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
            throttleRatio = 0.0;
            enemyInRange = false;

            if (MyAPIGateway.Multiplayer.IsServer)
                SetWarpPlayersSave(true);

            playersInWarpList.Clear();
            playerOxygenSnapshot.Clear();
            MyAPIGateway.Session?.OxygenProviderSystem?.RemoveOxygenGenerator(this);

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

        public bool IsInGravity()
        {
            if (grid?.mainGrid == null)
                return true;

            float gravity = GridGravityNow();

            if (!Settings.instance.AllowInGravity)
                return gravity > 0.01f;

            if (gravity <= 0f || grid.mainGrid.Physics == null)
                return false;

            BoundingBoxD worldAABB = grid.mainGrid.PositionComp.WorldAABB;
            var closestPlanet = MyGamePruningStructure.GetClosestPlanet(ref worldAABB);
            if (closestPlanet == null)
                return false;

            // Hard deck: MaxGravityPercentage of planet surface gravity
            float surfaceG = GetPlanetSurfaceGravity(closestPlanet);
            float maxGravPct = MathHelper.Clamp(Settings.instance?.MaxGravityPercentage ?? 0.9f, 0.0f, 0.9f);
            if (surfaceG > 0.01f && (gravity / surfaceG) >= maxGravPct)
                return true;

            Vector3D centerOfMassWorld = grid.mainGrid.Physics.CenterOfMassWorld;
            Vector3D planetCenter = closestPlanet.PositionComp.GetPosition();
            Vector3D closestSurface = closestPlanet.GetClosestSurfacePointGlobal(ref centerOfMassWorld);
            double altitude = (centerOfMassWorld - planetCenter).Length() - (closestSurface - planetCenter).Length();

            double requiredDropAltitude = GetProportionalDropAltitude(closestPlanet, centerOfMassWorld);
            return altitude < requiredDropAltitude;
        }

        public static float GetPlanetSurfaceGravity(MyPlanet planet)
        {
            if (planet == null) return 1.0f;
            float surfaceGrav;
            if (!_planetSurfaceGravCache.TryGetValue(planet.EntityId, out surfaceGrav))
            {
                if (planet.Generator != null && planet.Generator.SurfaceGravity > 0f)
                {
                    surfaceGrav = planet.Generator.SurfaceGravity;
                }
                else
                {
                    Vector3D center = planet.PositionComp.GetPosition();
                    Vector3D testPos = center + Vector3D.Up * Math.Max(planet.AverageRadius, 1000.0);
                    Vector3D surfacePoint = planet.GetClosestSurfacePointGlobal(ref testPos);
                    float naturalInterference;
                    surfaceGrav = MyAPIGateway.Physics.CalculateNaturalGravityAt(surfacePoint, out naturalInterference).Length() / EARTH_GRAVITY;
                    if (surfaceGrav <= 0.01f) surfaceGrav = 1.0f;
                }
                _planetSurfaceGravCache[planet.EntityId] = surfaceGrav;
            }
            return surfaceGrav;
        }

        public double GetProportionalDropAltitude(MyPlanet planet, Vector3D shipPos)
        {
            if (planet == null) return Settings.instance?.AllowInGravityMinAltitude ?? 1500.0;

            Vector3D planetCenter = planet.PositionComp.GetPosition();
            Vector3D surfacePoint = planet.GetClosestSurfacePointGlobal(ref shipPos);
            double surfaceRadius = (surfacePoint - planetCenter).Length();

            float surfaceG = GetPlanetSurfaceGravity(planet);
            double gravScale = MathHelper.Clamp(surfaceG, 0.5f, 1.5f);

            double calculatedDropAlt;
            if (planet.HasAtmosphere && planet.AtmosphereRadius > surfaceRadius + 500.0)
            {
                // 1/8 of atmosphere height above local terrain scaled by gravity well intensity (5km on Earth-like)
                double atmoHeight = planet.AtmosphereRadius - surfaceRadius;
                calculatedDropAlt = atmoHeight * 0.125 * gravScale;
            }
            else
            {
                // Airless Moons & Planets: 2.5% of radius scaled by gravity well intensity
                calculatedDropAlt = planet.AverageRadius * 0.025 * gravScale;
            }

            double minSafetyFloor = Math.Max(1500.0, Settings.instance?.AllowInGravityMinAltitude ?? 1500.0);
            return Math.Max(calculatedDropAlt, minSafetyFloor);
        }

        public double GetGravityInfluenceFactor()
        {
            if (grid?.mainGrid == null) return 0.0;
            float currentG = GridGravityNow();
            if (currentG <= 0.001f) return 0.0;

            BoundingBoxD worldAABB = grid.mainGrid.PositionComp.WorldAABB;
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(ref worldAABB);
            float surfaceG = GetPlanetSurfaceGravity(planet);

            double ratio = MathHelper.Clamp((double)(currentG / surfaceG), 0.0, 1.0);
            return Math.Pow(ratio, 0.285);
        }

        public float GetSupercruiseGravityShakeMagnitude()
        {
            if (grid?.mainGrid?.Physics == null) return 0f;
            float currentG = GridGravityNow();
            if (currentG <= 0.001f) return 0f;

            BoundingBoxD worldAABB = grid.mainGrid.PositionComp.WorldAABB;
            MyPlanet closestPlanet = MyGamePruningStructure.GetClosestPlanet(ref worldAABB);
            if (closestPlanet == null) return 0f;

            float surfaceG = GetPlanetSurfaceGravity(closestPlanet);
            if (surfaceG <= 0.01f) return 0f;

            float maxGravPct = MathHelper.Clamp(Settings.instance?.MaxGravityPercentage ?? 0.9f, 0.01f, 0.9f);
            float gravityRatio = currentG / surfaceG;

            // Camera shake scales smoothly from 0.0f at edge of gravity well to 1.0f at the hard deck limit
            float shake = MathHelper.Clamp(gravityRatio / maxGravPct, 0f, 1f);
            return shake;
        }

        public double GetCurrentLowDeckMps()
        {
            return GetCurrentLowDeckMps(GetGravityInfluenceFactor());
        }

        public double GetCurrentLowDeckMps(double t)
        {
            float speedPct = MathHelper.Clamp(Settings.instance?.AllowInGravitySpeedPercentage ?? 1.0f, 0.1f, 1.0f);
            double targetLowDeck = GRAVITY_LOW_DECK_MPS * speedPct;
            return MathHelper.Lerp(SPACE_LOW_DECK_MPS, targetLowDeck, t);
        }

        public double GetCurrentHighDeckMps()
        {
            return GetCurrentHighDeckMps(GetGravityInfluenceFactor());
        }

        public double GetCurrentHighDeckMps(double t)
        {
            float speedPct = MathHelper.Clamp(Settings.instance?.AllowInGravitySpeedPercentage ?? 1.0f, 0.1f, 1.0f);
            double targetHighDeck = GRAVITY_HIGH_DECK_MPS * speedPct;
            double spaceMaxMps = Math.Max(targetHighDeck, (Settings.instance?.maxSpeed ?? (50000.0 / 60.0)) * 60.0);
            return MathHelper.Lerp(spaceMaxMps, targetHighDeck, t);
        }


        public float GridGravityNow()
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
                        {
                            currentSpeedPt -= 90f;
                            double t = GetGravityInfluenceFactor();
                            double lowMps = GetCurrentLowDeckMps(t);
                            double highMps = GetCurrentHighDeckMps(t);
                            throttleRatio = MathHelper.Clamp((currentSpeedPt * 60.0 - lowMps) / Math.Max(1.0, highMps - lowMps), 0.0, 1.0);
                        }
                        else
                            hasEnoughPower = false;
                    }
                }
                
                float displayPower = primaryFunctional ? primaryDrive.requiredPower : 0f;

                heatGenerationRate = warpState == State.active && settings.heatGain > 0f
                    ? displayPower / (isPrototech ? heatPerMWDivisorPrototech : heatPerMWDivisor) * (1f + (float)GetGravityInfluenceFactor()) * settings.heatGain / 60f
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
                            driveHeat = Math.Max(driveHeat, (int)(drive.heat / GetHeatCapacity(drive, cachedActiveSinks, cachedActiveSmallSinks) * 100));
                        }
                    }
                }

                if ((warpState != State.idle || driveHeat > 0) && _updateTicks++ >= (MyAPIGateway.Utilities.IsDedicated ? 61 : 62))
                {
                    _updateTicks = 0;

                    bool enemyDelay = warpState == State.charging && settings.AllowToDetectEnemyGrids && enemyInRange;

                    if (warpState == State.active)
                        SendMessage($"Speed: {FormatSpeed(currentSpeedPt)}", 1f, "White");
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

        private float GetHeatCapacity(WarpDrive drive, int sinks, int smallSinks)
        {
            float capacityPerSink = Settings.instance?.HeatSinkCapacityBonus ?? 80f;
            bool small = IsSmallDrive(drive);

            return Math.Max(0f, (IsPrototech(drive) ? (small ? 75f : 400f) : (small ? 50f : 250f)) * (Settings.instance?.maxHeat ?? 1f)
                + (sinks - smallSinks) * capacityPerSink + smallSinks * capacityPerSink * 0.2f);
        }

        private void UpdateHeatStats(bool apply)
        {
            cachedActiveSinks = grid.CountActiveSinks(out cachedActiveSmallSinks, apply);
            heatCapacity = GetHeatCapacity(primaryDrive, cachedActiveSinks, cachedActiveSmallSinks);

            float driveCooling = 0f;
            foreach (HashSet<WarpDrive> gridDrives in warpDrives.Values)
            {
                foreach (WarpDrive drive in gridDrives)
                    driveCooling = Math.Max(driveCooling, IsSmallDrive(drive) ? 0.25f : 1f);
            }

            heatDissipationRate = (cachedActiveSinks - cachedActiveSmallSinks + cachedActiveSmallSinks * 0.2f) * Settings.instance.HeatSinkDissipation
                + Settings.instance.heatDissipationDrive * driveCooling;
        }

        public void HeatInfo(WarpDrive drive, out float capacity, out float breakEvenPower)
        {   // Terminal UI info
            WarpDrive shownDrive = warpState == State.idle ? drive : primaryDrive;

            if (isSleeping && grid != null)
                UpdateHeatStats(false);

            capacity = GetHeatCapacity(shownDrive, cachedActiveSinks, cachedActiveSmallSinks);
            breakEvenPower = Settings.instance.heatGain > 0f
                ? heatDissipationRate * 60f * (IsPrototech(shownDrive) ? heatPerMWDivisorPrototech : heatPerMWDivisor) / Settings.instance.heatGain
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
                if (sound != null)
                    sound.StopSound(true);
                if (loopSound != null)
                    loopSound.StopSound(true);
            }

            if (MyAPIGateway.Multiplayer.IsServer)
                SetWarpPlayersSave(true);

            hyperspace?.Close();
            capitalFSD?.Close();

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

            if (drive != null)
            {
                if (drive.isCapitalFSD && capitalFSD == null)
                    capitalFSD = new CapitalFSD(drive);
                else if (drive.supportsHyperspace && hyperspace == null)
                    hyperspace = new HyperspaceSystem(drive, grid);
            }

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

            if (drive != null)
            {
                if (capitalFSD != null && capitalFSD.HostDrive == drive)
                {
                    capitalFSD.Close();
                    capitalFSD = null;
                }
                if (hyperspace != null && hyperspace.HostDrive == drive)
                {
                    hyperspace.Close();
                    hyperspace = null;
                }

                if (capitalFSD == null)
                {
                    foreach (var set in warpDrives.Values)
                    {
                        var capCandidate = set.FirstOrDefault(d => d != null && d.isCapitalFSD);
                        if (capCandidate != null)
                        {
                            capitalFSD = new CapitalFSD(capCandidate);
                            break;
                        }
                    }
                }
                if (hyperspace == null)
                {
                    foreach (var set in warpDrives.Values)
                    {
                        var hsCandidate = set.FirstOrDefault(d => d != null && d.supportsHyperspace);
                        if (hsCandidate != null)
                        {
                            hyperspace = new HyperspaceSystem(hsCandidate, grid);
                            break;
                        }
                    }
                }
            }

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
