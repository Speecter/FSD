using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProtoBuf;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    public class CapitalFSD : IHyperdictionVictim
    {
        public enum CapState
        {
            idle,
            charging,       // 48s charge
            holdingCharge,  // Waiting for alignment within 5°
            engageRiser,    // 1s riser before wormhole
            jumpIn,         // 14s wormhole entry & mesh wipe
            transit,        // 21s - 42s hyperspace transit with smooth micro-teleportation
            jumpOut,        // 18s wormhole exit & mesh reveal
            cooldown,       // 5s cooldown

            Idle = idle,
            Charging = charging,
            HoldingCharge = holdingCharge,
            EngageRiser = engageRiser,
            JumpIn = jumpIn,
            Transit = transit,
            JumpOut = jumpOut,
            Cooldown = cooldown
        }

        // Timing Constants
        public const float CHARGE_TIME_SECONDS = 48.0f;
        public const float ENGAGE_RISER_SECONDS = 1.0f;
        public const float JUMP_IN_SECONDS = 14.0f;
        public const float JUMP_IN_WIPE_DELAY_SECONDS = 2.0f;
        public const float JUMP_IN_WIPE_DURATION_SECONDS = 12.0f;
        public const float MIN_TRANSIT_SECONDS = 21.0f;
        public const float MAX_TRANSIT_SECONDS = 42.0f;
        public const float TRANSIT_ACCEL_SECONDS = 0.5f;
        public const float TRANSIT_DECEL_SECONDS = 0.5f;
        public const float DEST_WARN_SECONDS_BEFORE = 11.0f;
        public const float DEST_PREJUMP_SECONDS_BEFORE = 9.0f;
        public const float JUMP_OUT_SECONDS = 18.0f;
        public const float JUMP_OUT_WIPE_DELAY_SECONDS = 2.0f;
        public const float JUMP_OUT_WIPE_DURATION_SECONDS = 14.0f;
        public const float COOLDOWN_SECONDS = 5.0f;
        public const float COUNTDOWN_SECONDS = 5.0f;
        public const float ALIGNMENT_TOLERANCE_DEG = 5.0f;
        public const float ALIGNMENT_BREAK_DEG = 6.5f;

        // Jump Range Constants (2,000 km to 40,000 km)
        public const double MIN_CAPITAL_JUMP_DISTANCE = 50000.0;     // 50 km
        public const double HARD_MAX_CAPITAL_JUMP_DISTANCE = 40000000.0; // 40,000 km

        public CapState state { get; private set; } = CapState.idle;
        public CapState State { get { return state; } private set { state = value; } }
        public WarpDrive hostDrive { get; private set; }
        public WarpDrive HostDrive { get { return hostDrive; } private set { hostDrive = value; } }
        public GridSystem gridSystem { get; private set; }
        public GridSystem GridSystem { get { return gridSystem; } private set { gridSystem = value; } }

        public float jumpDistanceRatio { get; set; } = 1.0f;
        public float JumpDistanceRatio { get { return jumpDistanceRatio; } set { jumpDistanceRatio = value; } }
        public HyperspaceSystem.JumpMode mode { get; set; } = HyperspaceSystem.JumpMode.ManualDistance;
        public HyperspaceSystem.JumpMode Mode { get { return mode; } set { mode = value; } }
        public string selectedGpsName { get; set; } = string.Empty;
        public string SelectedGpsName { get { return selectedGpsName; } set { selectedGpsName = value; } }
        public Vector3D? selectedGpsCoords { get; set; } = null;
        public Vector3D? SelectedGpsCoords { get { return selectedGpsCoords; } set { selectedGpsCoords = value; } }

        // Active Jump Vectors
        public Vector3D jumpOrigin { get; private set; }
        public Vector3D JumpOrigin { get { return jumpOrigin; } private set { jumpOrigin = value; } }
        public Vector3D jumpDestination { get; private set; }
        public Vector3D JumpDestination { get { return jumpDestination; } private set { jumpDestination = value; } }
        public Vector3D jumpDirection { get; private set; }
        public Vector3D JumpDirection { get { return jumpDirection; } private set { jumpDirection = value; } }
        public MatrixD jumpOrientation { get; private set; } = MatrixD.Identity;
        public MatrixD JumpOrientation { get { return jumpOrientation; } private set { jumpOrientation = value; } }
        public double jumpDistance { get; private set; }
        public double JumpDistance { get { return jumpDistance; } private set { jumpDistance = value; } }
        public float transitDuration { get; private set; }
        public float TransitDuration { get { return transitDuration; } private set { transitDuration = value; } }
        public double currentTransitSpeed { get; private set; }
        public double CurrentTransitSpeed { get { return currentTransitSpeed; } private set { currentTransitSpeed = value; } }

        // State Machine Timers (in ticks, 60 ticks = 1 second)
        private long stateElapsedTicks = 0;
        private long stateTotalTicks = 0;
        private long triggeringPlayerId = 0L;

        // ------------------------------------------------------------------ read-only state for the HUD

        public long TriggeringPlayerId { get { return triggeringPlayerId; } }

        /// <summary>Seconds left in the current state; 0 while holding for alignment (that state has no timer).</summary>
        public float StateSecondsLeft { get { return stateTotalTicks > 0 ? Math.Max(0f, (stateTotalTicks - stateElapsedTicks) / 60f) : 0f; } }
        public float StateProgress { get { return stateTotalTicks > 0 ? MathHelper.Clamp((float)stateElapsedTicks / stateTotalTicks, 0f, 1f) : 0f; } }

        /// <summary>Camera-shake magnitude of the transit right now (1 = steady, higher under a Hyperdictor). The HUD shakes with the same value.</summary>
        public float TransitShakeMagnitude
        {
            get
            {
                float shake = 1.0f;
                if (Hyperdiction != null)
                    shake = Math.Max(shake, Hyperdiction.ShakeAt(Hyperdiction.ElapsedTicks));
                return shake;
            }
        }

        /// <summary>Share of the jump distance covered so far while in the tunnel.</summary>
        public float TransitDistanceProgress
        {
            get
            {
                if (State != CapState.Transit || stateTotalTicks <= 0)
                    return State == CapState.JumpOut || State == CapState.Cooldown ? 1f : 0f;

                float progress;
                double speed;
                HyperspaceSystem.CalculateTransitProgress(stateElapsedTicks / 60.0, stateTotalTicks / 60.0, JumpDistance, out progress, out speed, HyperspaceSystem.TRANSIT_ACCEL_SECONDS, 0.0);
                return progress;
            }
        }

        /// <summary>The direction the nose has to point at while holding for alignment and charging.</summary>
        public bool TryGetAlignTarget(out Vector3D dir)
        {
            return TryGetAlignTarget(GetShipController(), out dir);
        }

        /// <summary>Same, with a cockpit the caller already has.</summary>
        public bool TryGetAlignTarget(IMyShipController cockpit, out Vector3D dir)
        {
            dir = Vector3D.Zero;
            if (cockpit == null)
                return false;

            if (Mode == HyperspaceSystem.JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
                dir = SelectedGpsCoords.Value - cockpit.WorldMatrix.Translation;
            else if (JumpDirection.LengthSquared() > 0.001)
                dir = JumpDirection;
            else
                dir = cockpit.WorldMatrix.Forward;

            if (dir.LengthSquared() < 1e-9)
                return false;

            dir.Normalize();
            return true;
        }

        // Sounds & Emitters
        private MyEntity3DSoundEmitter localEmitter;
        private MyEntity3DSoundEmitter transitSound;
        private bool _spectatorHidden = false;
        private MyParticleEffect wormholeCloudParticle;
        private MyParticleEffect wormholeFlashParticle;
        private MyParticleEffect tunnelParticle;   // the hyperspace tunnel seen from aboard (local pilot only), Data/HyperspaceTunnel.sbc

        // Players in transit - Character.Save suppression
        private readonly List<IMyPlayer> _playersInTransit = new List<IMyPlayer>();

        // Hyperdiction: non-null while a Hyperdictor has pulled this ship into the 9s interdiction sequence
        public HyperdictionSequence Hyperdiction { get; private set; }
        public bool IsHyperdicted { get { return Hyperdiction != null; } }
        private MyEntity3DSoundEmitter hyperdictionSound;
        private bool hyperdictionLeadFxSpawned = false;
        private long catchRemainingTicks = 0;
        private bool hyperdictionAftermath = false;
        private int blackoutTicks = 0;
        private readonly List<IMyPowerProducer> blackoutProducers = new List<IMyPowerProducer>();

        public MyCubeGrid HyperdictionGrid { get { return GridSystem?.mainGrid; } }
        public bool CanBeHyperdicted { get { return State == CapState.Transit && Hyperdiction == null && GridSystem?.mainGrid != null && !GridSystem.mainGrid.MarkedForClose; } }

        // Mesh Wipe Session Data
        public struct BlockWipeData
        {
            public IMySlimBlock Block;
            public double TriggerTravel;
        }

        public class WipeSession
        {
            public List<BlockWipeData> BlocksToHide = new List<BlockWipeData>();
            public List<BlockWipeData> BlocksToReveal = new List<BlockWipeData>();
            public HashSet<IMySlimBlock> HiddenBlocks = new HashSet<IMySlimBlock>();
            public HashSet<IMyCubeBlock> HiddenFatBlocks = new HashSet<IMyCubeBlock>();
            public Vector3D PlaneOrigin;
            public Vector3D PlaneNormal;
            public double ShipLength;
            public double NoseOffset;
            public double CrossRadius;
            public double StepDistance;
            public Vector3D EntryWormholePos;
            public Vector3D ExitWormholePos;
            public double AccumulatedTravel;
        }

        private WipeSession currentWipe = null;
        private bool destWarnTriggered = false;
        private bool destPrejumpTriggered = false;
        private int powerLossTicks = 0;
        private int fsdLossTicks = 0;
        private int controlLossTicks = 0;

        // Cached physical grids for zero-allocation velocity zeroing
        private readonly List<IMyCubeGrid> _cachedPhysicalGrids = new List<IMyCubeGrid>();

        // Saved thruster multipliers, enabled states, and light states for visual wipe restoration
        private readonly Dictionary<long, float> _savedThrustMultipliers = new Dictionary<long, float>();
        private readonly Dictionary<long, bool> _savedThrustEnabled = new Dictionary<long, bool>();
        private readonly Dictionary<long, bool> _savedLightStates = new Dictionary<long, bool>();

        // Transit smooth materialization
        private readonly List<IMySlimBlock> _transitRevealBlocks = new List<IMySlimBlock>();
        private int _transitRevealIndex = 0;
        private int _transitRevealPerTick = 0;
        private bool _isTransitRevealActive = false;

        // The pilot's own view of the portal (local player only): hyperspace shows through the portal once the nose starts crossing,
        // surrounds the ship once the camera is through, and lingers over the first seconds of the exit while the exit cloud forms
        private bool _entryPreview;
        private bool _entryCrossed;
        private bool _exitBridge;
        private const int TRANSIT_REVEAL_TICKS = 90; // ~1.5 seconds at 60 FPS

        // In-transit dynamic obstacle re-check flags (at 25%, 50%, and 90% progress)
        private bool checkedObstacle25 = false;
        private bool checkedObstacle50 = false;
        private bool checkedObstacle90 = false;

        public CapitalFSD(WarpDrive drive)
        {
            HostDrive = drive;
            EnsureGridSystem();

            WarpDriveSession.instance?.RegisterCapitalFSD(this);

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                var cubeGrid = HostDrive?.block?.CubeGrid as MyCubeGrid;
                if (cubeGrid != null)
                {
                    localEmitter = new MyEntity3DSoundEmitter(cubeGrid)
                    {
                        CanPlayLoopSounds = true
                    };
                }
            }
        }

        private void EnsureGridSystem()
        {
            if (GridSystem == null || GridSystem.mainGrid == null || GridSystem.mainGrid.MarkedForClose)
            {
                if (HostDrive?.warpSystem?.grid != null && !HostDrive.warpSystem.grid.mainGrid.MarkedForClose)
                {
                    GridSystem = HostDrive.warpSystem.grid;
                }
                else
                {
                    var grid = HostDrive?.block?.CubeGrid as MyCubeGrid;
                    if (grid != null && !grid.MarkedForClose)
                    {
                        GridSystem = new GridSystem(grid);
                    }
                }
            }
        }

        public static void Reset()
        {
        }

        public void Close()
        {
            HyperspaceSunManager.Instance.ReleaseSuppression(this, true);
            WarpDriveSession.instance?.UnregisterCapitalFSD(this);

            if (State != CapState.Idle)
            {
                AbortJump("CAPITAL FSD CLOSED");
            }
            SetPlayersSave(true);
            _playersInTransit.Clear();
            StopAllSounds();
            StopParticles();
            RestoreAllBlocksVisibility();
            SetGridInvincibility(false);
            _cachedPhysicalGrids.Clear();
        }

        public double GetMaxJumpDistance(float powerMW = 100f)
        {
            return HARD_MAX_CAPITAL_JUMP_DISTANCE;
        }

        public double GetCurrentManualDistance(float powerMW = 100f)
        {
            return MIN_CAPITAL_JUMP_DISTANCE + ((HARD_MAX_CAPITAL_JUMP_DISTANCE - MIN_CAPITAL_JUMP_DISTANCE) * JumpDistanceRatio);
        }

        public void SendNotification(string text, float seconds = 4f, string color = "White")
        {
            // Runs on every machine in lockstep: each one shows the message only to its own local player
            HudEvents.Post(GridSystem?.mainGrid?.EntityId ?? 0L, triggeringPlayerId, text, HudEvents.Classify(text, color), HudRoute.Local, seconds);
        }

        public void TriggerJump(long playerId)
        {
            if (State == CapState.Idle)
            {
                StartCharging(playerId);
            }
            else if (State == CapState.Charging || State == CapState.HoldingCharge)
            {
                AbortJump("CHARGE ABORTED BY PILOT");
            }
        }

        public void StartCharging(long playerId = 0L)
        {
            if (State != CapState.Idle) return;

            triggeringPlayerId = playerId;

            if (HostDrive == null || HostDrive.isDuplicateFSD || HostDrive.block == null || !HostDrive.block.IsFunctional || !HostDrive.block.IsWorking || !HostDrive.block.Enabled)
            {
                SendNotification("FRAME SHIFT DRIVE OFFLINE OR DAMAGED (Only 1 active FSD allowed per ship)", 5f, "Red");
                return;
            }

            EnsureGridSystem();

            var mainGrid = GridSystem?.mainGrid ?? HostDrive?.block?.CubeGrid as MyCubeGrid;
            if (mainGrid == null)
            {
                SendNotification("GRID NOT INITIALIZED", 4f, "Red");
                return;
            }

            if (WarpDriveSession.instance != null && (WarpDriveSession.instance.IsHyperspaceActiveForGrid(mainGrid) || WarpDriveSession.instance.IsInHyperspaceOrTransit(mainGrid)))
            {
                SendNotification("HYPERSPACE IN USE - CAPITAL FSD BLOCKED", 4f, "Red");
                return;
            }

            string hyperdictionBlock = HyperdictionRules.JumpBlocker(mainGrid, true);
            if (hyperdictionBlock != null)
            {
                SendNotification(hyperdictionBlock, 4f, "Red");
                return;
            }

            if (WarpDriveSession.instance != null && WarpDriveSession.instance.IsSupercruiseActiveForGrid(mainGrid))
            {
                SendNotification("SUPERCRUISE IN USE - CAPITAL FSD BLOCKED", 4f, "Red");
                return;
            }

            if (HostDrive?.warpSystem != null && HostDrive.warpSystem.warpState != WarpSystem.State.idle)
            {
                SendNotification("SUPERCRUISE IN USE - CAPITAL FSD BLOCKED", 4f, "Red");
                return;
            }

            if (mainGrid.IsStatic)
            {
                SendNotification("UNABLE TO MOVE STATIC GRID", 5f, "Red");
                return;
            }

            var cockpit = GetShipController();
            if (cockpit == null)
            {
                SendNotification("NO PILOT SEAT OR REMOTE CONTROL FOUND", 4f, "Red");
                return;
            }

            // Natural gravity check
            Vector3D checkPos = cockpit.WorldMatrix.Translation;
            float naturalGravityMultiplier;
            Vector3D naturalGravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(checkPos, out naturalGravityMultiplier);
            float allowInGravityMax = (Settings.instance?.AllowInGravityMax ?? 0.2f);
            if (naturalGravityMultiplier > allowInGravityMax)
            {
                SendNotification($"CANNOT ENGAGE FSD IN GRAVITY > {allowInGravityMax:F2}g", 5f, "Red");
                return;
            }

            // Forward flight corridor clearance check (500m collision safety)
            if (HasObstacleAhead(mainGrid, cockpit.WorldMatrix.Forward, mainGrid.PositionComp.WorldAABB.Center))
            {
                return;
            }

            // Power check
            if (!HostDrive.hasPower && !(MyAPIGateway.Session?.CreativeMode ?? false))
            {
                SendNotification("NOT ENOUGH POWER TO INITIATE FSD CHARGE", 5f, "Red");
                return;
            }

            if (!SelectedGpsCoords.HasValue || string.IsNullOrEmpty(SelectedGpsName))
            {
                var rc = cockpit as IMyRemoteControl;
                if (rc != null && !rc.CurrentWaypoint.IsEmpty() && rc.CurrentWaypoint.Coords != Vector3D.Zero)
                {
                    Mode = HyperspaceSystem.JumpMode.GpsWaypoint;
                    SelectedGpsCoords = rc.CurrentWaypoint.Coords;
                    SelectedGpsName = rc.CurrentWaypoint.Name;
                }
                else
                {
                    Mode = HyperspaceSystem.JumpMode.ManualDistance;
                }
            }

            // Calculate Target Coordinates
            Vector3D shipPos = cockpit.WorldMatrix.Translation;

            if (Mode == HyperspaceSystem.JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
            {
                JumpDestination = SelectedGpsCoords.Value;
                JumpDistance = Vector3D.Distance(shipPos, JumpDestination);
                if (JumpDistance < MIN_CAPITAL_JUMP_DISTANCE)
                {
                    SendNotification(string.Format("DESTINATION TOO CLOSE - MIN {0:N0}km", MIN_CAPITAL_JUMP_DISTANCE / 1000.0), 4f, "Red");
                    return;
                }
                if (JumpDistance > HARD_MAX_CAPITAL_JUMP_DISTANCE)
                {
                    SendNotification(string.Format("DESTINATION EXCEEDS MAXIMUM RANGE ({0:N0}km)", HARD_MAX_CAPITAL_JUMP_DISTANCE / 1000.0), 4f, "Red");
                    return;
                }
                JumpDirection = Vector3D.Normalize(JumpDestination - shipPos);
            }
            else
            {
                JumpDistance = GetCurrentManualDistance();
                JumpDirection = cockpit.WorldMatrix.Forward;
                JumpDestination = shipPos + (JumpDirection * JumpDistance);
            }

            JumpOrigin = mainGrid.PositionComp.GetPosition();
            JumpOrientation = mainGrid.WorldMatrix.GetOrientation();

            // 500m obstacle safety clearance at arrival
            Vector3D safeDest;
            double safeDist;
            if (TryAdjustDestinationForObstacles(JumpOrigin, JumpDirection, JumpDistance, out safeDest, out safeDist))
            {
                JumpDestination = safeDest;
                JumpDistance = safeDist;
                SendNotification("OBSTRUCTION AT ARRIVAL - 500m CLEARANCE APPLIED", 4f, "White");
            }

            // Planetary trajectory occlusion check
            string blockedPlanet;
            if (HyperspaceSystem.IsTrajectoryBlockedByPlanet(JumpOrigin, JumpDestination, out blockedPlanet))
            {
                SendNotification($"TRAJECTORY OBSTRUCTED BY {blockedPlanet.ToUpper()}!", 5f, "Red");
                return;
            }

            // Planetary gravity well destination check (uses AllowInGravityMax setting like HyperspaceSystem)
            float dummyGravity;
            Vector3D destGravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(JumpDestination, out dummyGravity);
            allowInGravityMax = (Settings.instance?.AllowInGravityMax ?? 0.2f);
            if ((destGravity.Length() / WarpSystem.EARTH_GRAVITY) > allowInGravityMax)
            {
                SendNotification("DESTINATION INSIDE PLANETARY GRAVITY WELL!", 5f, "Red");
                return;
            }

            double shipRadius = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
            HyperspaceSystem.RegisterClaim(mainGrid.EntityId, JumpDestination, 500.0 + shipRadius, JumpDirection, 0L);

            // Transit duration scaled 2x from 21s to 42s based on distance
            float ratio = (float)MathHelper.Clamp((JumpDistance - MIN_CAPITAL_JUMP_DISTANCE) / (HARD_MAX_CAPITAL_JUMP_DISTANCE - MIN_CAPITAL_JUMP_DISTANCE), 0.0, 1.0);
            TransitDuration = MIN_TRANSIT_SECONDS + ((MAX_TRANSIT_SECONDS - MIN_TRANSIT_SECONDS) * ratio);

            stateElapsedTicks = 0;
            powerLossTicks = 0;
            fsdLossTicks = 0;
            controlLossTicks = 0;
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;
            stateTotalTicks = 0;
            State = CapState.HoldingCharge;
            SendNotification("ALIGN WITH TARGET TO INITIATE CHARGE", 4f, "Red");
        }

        public void Update()
        {
            EnsureGridSystem();

            if (blackoutTicks > 0)
                UpdateBlackout();

            if (State == CapState.Idle) return;

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null || mainGrid.MarkedForClose)
            {
                AbortJump("GRID INVALID");
                return;
            }

            stateElapsedTicks++;

            switch (State)
            {
                case CapState.Charging:
                    UpdateChargingTick();
                    break;

                case CapState.HoldingCharge:
                    UpdateHoldingChargeTick();
                    break;

                case CapState.EngageRiser:
                    UpdateEngageRiserTick();
                    break;

                case CapState.JumpIn:
                    UpdateJumpInTick();
                    break;

                case CapState.Transit:
                    UpdateTransitTick();
                    break;

                case CapState.JumpOut:
                    UpdateJumpOutTick();
                    break;

                case CapState.Cooldown:
                    UpdateCooldownTick();
                    break;
            }
        }

        private void UpdateChargingTick()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) { AbortJump("GRID INVALID"); return; }

            // FSD block damage abort
            if (HostDrive?.block == null || !HostDrive.block.IsFunctional || !HostDrive.block.IsWorking)
            {
                AbortJump("FSD DAMAGED - CHARGING ABORTED");
                return;
            }

            // Power loss abort
            if (!HostDrive.hasPower && !(MyAPIGateway.Session?.CreativeMode ?? false))
            {
                AbortJump("POWER FAILURE - CHARGING ABORTED");
                return;
            }

            // Gravity abort (matches HyperspaceSystem behaviour)
            float dummyGravCheck;
            Vector3D gravCheck = MyAPIGateway.Physics.CalculateNaturalGravityAt(mainGrid.PositionComp.GetPosition(), out dummyGravCheck);
            float allowGravMax = (Settings.instance?.AllowInGravityMax ?? 0.2f);
            if ((gravCheck.Length() / WarpSystem.EARTH_GRAVITY) > allowGravMax)
            {
                AbortJump("GRAVITY WELL DETECTED - CHARGING ABORTED");
                return;
            }

            // Static/docked abort
            if (mainGrid.IsStatic || (HostDrive?.warpSystem != null && HostDrive.warpSystem.ConnectedStatic(mainGrid)))
            {
                AbortJump(HostDrive?.warpSystem?.warnIsStaticHUD ?? "STATIC GRID - CHARGING ABORTED");
                return;
            }

            var cockpit = GetShipController();
            if (cockpit == null)
            {
                controlLossTicks++;
                if (controlLossTicks >= 30)
                {
                    AbortJump("NO PILOT COCKPIT FOUND");
                    return;
                }
            }
            else
            {
                controlLossTicks = 0;
            }

            // Update jump direction live for GPS waypoint mode
            if (Mode == HyperspaceSystem.JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue && cockpit != null)
            {
                JumpDirection = Vector3D.Normalize(SelectedGpsCoords.Value - cockpit.WorldMatrix.Translation);
            }

            if (Mode == HyperspaceSystem.JumpMode.ManualDistance && cockpit != null)
            {
                JumpDirection = cockpit.WorldMatrix.Forward;
                JumpDestination = mainGrid.PositionComp.GetPosition() + JumpDirection * JumpDistance;

                // Periodic planet trajectory re-check in ManualDistance mode (every 60 ticks = 1s)
                if (stateElapsedTicks % 60 == 0 && stateElapsedTicks > 0)
                {
                    string blockedPlanet;
                    if (HyperspaceSystem.IsTrajectoryBlockedByPlanet(mainGrid.PositionComp.GetPosition(), JumpDestination, out blockedPlanet))
                    {
                        AbortJump($"TRAJECTORY OBSTRUCTED BY {blockedPlanet.ToUpper()}!");
                        return;
                    }
                }
            }

            if (cockpit == null) return;
            Vector3D targetDir = Vector3D.Zero;
            if (Mode == HyperspaceSystem.JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
            {
                targetDir = Vector3D.Normalize(SelectedGpsCoords.Value - cockpit.WorldMatrix.Translation);
            }
            else
            {
                targetDir = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : cockpit.WorldMatrix.Forward;
            }

            double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
            double deviation = MathHelper.ToDegrees(Math.Acos(dot));

            if (deviation > ALIGNMENT_BREAK_DEG)
            {
                StopLocalSound();
                PlayLocalSound("spool_down");
                SendNotification("ALIGNMENT LOST - CHARGE COLLAPSED", 4f, "Red");
                State = CapState.HoldingCharge;
                stateElapsedTicks = 0;
                stateTotalTicks = 0;
                return;
            }

            // At 48s elapsed, check alignment
            if (stateElapsedTicks >= stateTotalTicks)
            {
                if (Mode == HyperspaceSystem.JumpMode.GpsWaypoint)
                {
                    MatrixD finalAligned = MicroAdjustOrientation(GridSystem.mainGrid.WorldMatrix, targetDir, 1.0f);
                    if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
                        GridSystem.mainGrid.Teleport(finalAligned);
                }
                StartEngageRiser();
            }
        }

        private void UpdateHoldingChargeTick()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) { AbortJump("GRID INVALID"); return; }

            // Hazard abort checks (matches HyperspaceSystem.UpdateHoldingChargeTick)
            if (HostDrive?.block == null || !HostDrive.block.IsFunctional || !HostDrive.block.IsWorking)
            {
                AbortJump("CHARGE COLLAPSED - FSD DAMAGED");
                return;
            }
            if (!HostDrive.hasPower && !(MyAPIGateway.Session?.CreativeMode ?? false))
            {
                AbortJump("CHARGE COLLAPSED - POWER FAILURE");
                return;
            }

            float dummyGravHold;
            Vector3D gravHold = MyAPIGateway.Physics.CalculateNaturalGravityAt(mainGrid.PositionComp.GetPosition(), out dummyGravHold);
            float allowGravMaxH = (Settings.instance?.AllowInGravityMax ?? 0.2f);
            if ((gravHold.Length() / WarpSystem.EARTH_GRAVITY) > allowGravMaxH)
            {
                AbortJump("CHARGE COLLAPSED - GRAVITY WELL DETECTED");
                return;
            }

            if (mainGrid.IsStatic || (HostDrive?.warpSystem != null && HostDrive.warpSystem.ConnectedStatic(mainGrid)))
            {
                AbortJump(HostDrive?.warpSystem?.warnIsStaticHUD ?? "STATIC GRID - CHARGE ABORTED");
                return;
            }

            var cockpit = GetShipController();
            if (cockpit == null)
            {
                AbortJump("NO PILOT COCKPIT FOUND");
                return;
            }

            if (Mode == HyperspaceSystem.JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
            {
                JumpDirection = Vector3D.Normalize(SelectedGpsCoords.Value - cockpit.WorldMatrix.Translation);
            }
            else
            {
                JumpDirection = cockpit.WorldMatrix.Forward;
                JumpDestination = mainGrid.PositionComp.GetPosition() + JumpDirection * JumpDistance;
            }

            double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, JumpDirection), -1.0, 1.0);
            double deviation = MathHelper.ToDegrees(Math.Acos(dot));

            if (deviation <= ALIGNMENT_TOLERANCE_DEG)
            {
                State = CapState.Charging;
                stateElapsedTicks = 0;
                stateTotalTicks = (long)(CHARGE_TIME_SECONDS * 60f);
                PlayLocalSound("fsd_cap_charge");
                SendNotification("CAPITAL CLASS FSD CHARGING (48s)...", 5f, "White");
            }
        }


        public MatrixD MicroAdjustOrientation(MatrixD currentMatrix, Vector3D targetDir, float slerpRatio, Vector3D? targetUp = null)
        {
            Vector3D currentForward = currentMatrix.Forward;
            Vector3D currentUp = currentMatrix.Up;
            Vector3D upVec = targetUp.HasValue ? targetUp.Value : currentUp;

            QuaternionD currentQuat = QuaternionD.CreateFromForwardUp(currentForward, currentUp);
            QuaternionD targetQuat = QuaternionD.CreateFromForwardUp(targetDir, upVec);

            QuaternionD slerpedQuat = QuaternionD.Slerp(currentQuat, targetQuat, MathHelper.Clamp(slerpRatio, 0f, 1f));
            MatrixD adjusted = MatrixD.CreateFromQuaternion(slerpedQuat);
            adjusted.Translation = currentMatrix.Translation;
            return adjusted;
        }

        public IMyShipController GetShipController()
        {
            var cockpit = GridSystem?.FindMainCockpit();
            if (cockpit != null && cockpit.IsFunctional) return cockpit;

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid != null)
            {
                foreach (var fat in mainGrid.GetFatBlocks())
                {
                    var sc = fat as IMyCockpit;
                    if (sc != null && sc.IsFunctional && !(sc is IMyCryoChamber))
                    {
                        if (GridSystem == null || GridSystem.IsAlignedWithThrusterForward(sc))
                            return sc;
                    }
                }
                foreach (var fat in mainGrid.GetFatBlocks())
                {
                    var sc = fat as IMyShipController;
                    if (sc != null && sc.IsFunctional && !(sc is IMyCryoChamber))
                    {
                        if (GridSystem == null || GridSystem.IsAlignedWithThrusterForward(sc))
                            return sc;
                    }
                }
            }
            return null;
        }

        private void StartEngageRiser()
        {
            State = CapState.EngageRiser;
            stateElapsedTicks = 0;
            stateTotalTicks = (long)(ENGAGE_RISER_SECONDS * 60f);

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid != null)
            {
                JumpOrigin = mainGrid.PositionComp.GetPosition();
                JumpOrientation = mainGrid.WorldMatrix.GetOrientation();
            }

            PlayLocalSound("fsd_cap_engage");
            SendNotification("ENGAGING CAPITAL FSD...", 2f, "Cyan");
        }

        private void UpdateEngageRiserTick()
        {
            if (stateElapsedTicks >= stateTotalTicks)
            {
                StartJumpIn();
            }
        }

        private void StartJumpIn()
        {
            State = CapState.JumpIn;
            stateElapsedTicks = 0;
            stateTotalTicks = (long)(JUMP_IN_SECONDS * 60f);

            CollectPlayersOnGrid();
            SetGridInvincibility(true);
            _entryPreview = false;
            _entryCrossed = false;
            _exitBridge = false;

            var mainGrid = GridSystem.mainGrid;
            Vector3D forward = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : mainGrid.WorldMatrix.Forward;
            Vector3D center = mainGrid.PositionComp.WorldAABB.Center;

            double noseOffset, shipLength, crossRadius;
            ComputeShipExtents(forward, out noseOffset, out shipLength, out crossRadius);

            Vector3D wormholePos = center + (forward * noseOffset);

            double totalTravelDist = shipLength + 5.0; // 5m margin to ensure 100% block clearance
            double stepDist = totalTravelDist / (JUMP_IN_WIPE_DURATION_SECONDS * 60.0);

            currentWipe = new WipeSession
            {
                ShipLength = shipLength,
                NoseOffset = noseOffset,
                CrossRadius = crossRadius,
                EntryWormholePos = wormholePos,
                PlaneOrigin = wormholePos,
                PlaneNormal = forward,
                StepDistance = stepDist
            };

            CollectAllBlocks(currentWipe);

            // 0s: Wormhole tear opens in front of ship: Flash + Black Oily Cloud & Lightning Portal
            SpawnEntryWormholeFX(currentWipe.EntryWormholePos, forward, (float)crossRadius);

            // Play & broadcast jump-in audio
            BroadcastGlobalAudio(currentWipe.EntryWormholePos, "fsd_cap_jump_in", 30000f);

            ClearPhysicsVelocities();
        }

        private void UpdateJumpInTick()
        {
            if (currentWipe == null) return;

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            float elapsedSeconds = stateElapsedTicks / 60f;

            // Keep wormhole particle positioned at fixed entry point in space
            UpdateEntryWormholeFX();

            // 0s - 2s: Ship stationary while black oily cloud & lightning expand
            // 2s - 14s (12s duration): Ship micro-teleports forward into cloud & slices mesh
            if (stateElapsedTicks == 60)
            {
                SpawnCloudWormholeFX(currentWipe.EntryWormholePos, currentWipe.PlaneNormal, (float)currentWipe.CrossRadius);
            }

            if (elapsedSeconds >= JUMP_IN_WIPE_DELAY_SECONDS)
            {
                MatrixD m = mainGrid.WorldMatrix;
                m.Translation += currentWipe.PlaneNormal * currentWipe.StepDistance;
                SafeTeleportMainGrid(m);

                // Mesh Slice: Hide blocks passing through the fixed wormhole plane. Not for a pilot who is already through: for them
                // the rest of the ship is coming into hyperspace with them
                if (!_entryCrossed)
                    SliceBlocksHide(currentWipe);
            }

            UpdateEntryHyperspaceView(elapsedSeconds);

            if (stateElapsedTicks >= stateTotalTicks)
            {
                StartTransit();
            }
        }

        /// <summary>
        /// Local pilot only (timings untouched): as soon as the nose starts into the portal, the hyperspace tunnel streams out of it ahead
        /// of the ship, so the blocks vanish into hyperspace instead of into empty space. The moment the camera passes the portal plane,
        /// the tunnel closes in all around, the sun is hidden and the ship reappears around the pilot, as it does in transit.
        /// </summary>
        private void UpdateEntryHyperspaceView(float elapsedSeconds)
        {
            if (MyAPIGateway.Utilities.IsDedicated || currentWipe == null || elapsedSeconds < JUMP_IN_WIPE_DELAY_SECONDS || !IsLocalPlayerOnboard())
                return;

            if (!_entryPreview)
            {
                _entryPreview = true;
                StartTransitTunnelFX(false);          // no transit loop yet (the jump-in sound is playing)
            }

            var camera = MyAPIGateway.Session?.Camera;
            if (!_entryCrossed && camera != null && Vector3D.Dot(camera.Position - currentWipe.PlaneOrigin, currentWipe.PlaneNormal) > 0.0)
            {
                _entryCrossed = true;
                HyperspaceSunManager.Instance.RequestSuppression(this, true);
                StartTransitSmoothReveal();
            }

            UpdateTransitTunnelFX(false);
            if (_entryCrossed)
                UpdateTransitSmoothReveal();
        }

        private void StartTransit()
        {
            // Final obstacle clearance re-check right before entering transit (matches HyperspaceSystem.EnterHyperspace)
            Vector3D safeDest;
            double safeDist;
            if (TryAdjustDestinationForObstacles(JumpOrigin, JumpDirection, JumpDistance, out safeDest, out safeDist))
            {
                JumpDestination = safeDest;
                JumpDistance = safeDist;
            }

            // Final planet trajectory check
            string blockedPlanetFinal;
            if (HyperspaceSystem.IsTrajectoryBlockedByPlanet(JumpOrigin, JumpDestination, out blockedPlanetFinal))
            {
                AbortJump($"TRAJECTORY OBSTRUCTED BY {blockedPlanetFinal.ToUpper()}!");
                return;
            }

            // Final destination gravity check
            float dummyFinal;
            Vector3D destGravFinal = MyAPIGateway.Physics.CalculateNaturalGravityAt(JumpDestination, out dummyFinal);
            float allowGravFinal = (Settings.instance?.AllowInGravityMax ?? 0.2f);
            if ((destGravFinal.Length() / WarpSystem.EARTH_GRAVITY) > allowGravFinal)
            {
                AbortJump("DESTINATION INSIDE PLANETARY GRAVITY WELL!");
                return;
            }

            // Zero-power guard at transit entry (matches HyperspaceSystem.EnterHyperspace)
            if (!HostDrive.hasPower && !(MyAPIGateway.Session?.CreativeMode ?? false))
            {
                AbortJump("POWER FAILURE - JUMP ABORTED AT ENTRY");
                return;
            }

            State = CapState.Transit;
            stateElapsedTicks = 0;
            stateTotalTicks = (long)(TransitDuration * 60f);
            destWarnTriggered = false;
            destPrejumpTriggered = false;
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;
            fsdLossTicks = 0;
            powerLossTicks = 0;
            controlLossTicks = 0;

            // Suppress procedural encounter & asteroid spawning during transit
            CollectPlayersOnGrid();
            SetPlayersSave(false);

            // Begin smooth nose-to-tail materialization inside the hyperspace tunnel (already running for a pilot who crossed the portal)
            if (!_entryCrossed)
                StartTransitSmoothReveal();

            StopWormholeParticles();
            StartTransitTunnelFX();

            SendNotification("HYPERSPACE TRANSIT ACTIVE", 4f, "Cyan");
        }

        // ------------------------------------------------------------------ Hyperdiction

        private static bool IsAuthorityMachine { get { return NetAuthority.IsAuthority; } }

        /// <summary>
        /// A Hyperdictor caught this ship. Starts the deterministic 9s sequence at half intensity (capital ships are heavier).
        /// </summary>
        public bool BeginHyperdiction(long hyperdictorBlockId, int seed, Vector3D dropPos)
        {
            var mainGrid = GridSystem?.mainGrid;
            if (!CanBeHyperdicted || mainGrid == null)
                return false;

            Vector3D localCenter = mainGrid.PositionComp.LocalAABB.Center;
            Vector3D catchCenter = Vector3D.Transform(localCenter, mainGrid.WorldMatrix);

            Hyperdiction = new HyperdictionSequence(mainGrid.EntityId, hyperdictorBlockId, seed, true, HyperdictionSequence.MassOf(mainGrid),
                catchCenter, dropPos, JumpOrientation, JumpDirection, localCenter);
            hyperdictionLeadFxSpawned = false;
            catchRemainingTicks = Math.Max(0L, stateTotalTicks - stateElapsedTicks);

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                if (transitSound != null && transitSound.IsPlaying)
                    transitSound.StopSound(true);

                if (IsLocalPlayerOnboard())
                {
                    if (hyperdictionSound == null)
                        hyperdictionSound = new MyEntity3DSoundEmitter(null) { CanPlayLoopSounds = false };

                    hyperdictionSound.SetPosition(GetListenerAnchor(catchCenter));
                    hyperdictionSound.SetVelocity(Vector3.Zero);
                    hyperdictionSound.PlaySound(WarpSound.hyperdiction, true);
                }
            }

            SendNotification("CAPITAL FSD CONDUIT DESTABILIZING - HYPERDICTION DETECTED", 6f, "Red");
            return true;
        }

        /// <summary>
        /// The Hyperdictor failed mid-sequence: start a fresh transit leg from the current position to the original destination
        /// and let the rotation offsets settle back to neutral over 2 seconds.
        /// </summary>
        public void AbortHyperdiction()
        {
            var seq = Hyperdiction;
            var mainGrid = GridSystem?.mainGrid;
            if (seq == null || seq.IsRecovering || mainGrid == null)
                return;

            Vector3D currentPos = mainGrid.PositionComp.GetPosition();
            Vector3D toDest = JumpDestination - currentPos;
            double dist = toDest.Length();

            if (dist > 1.0)
            {
                Vector3D newDir = toDest / dist;
                Vector3D oldDir = JumpDirection;

                Vector3D axis = Vector3D.Cross(oldDir, newDir);
                double axisLen = axis.Length();
                if (axisLen > 1e-6)
                {
                    MatrixD aimed = JumpOrientation * MatrixD.CreateFromAxisAngle(axis / axisLen, Math.Atan2(axisLen, Vector3D.Dot(oldDir, newDir)));
                    aimed.Translation = Vector3D.Zero;
                    JumpOrientation = aimed;
                }

                JumpOrigin = currentPos;
                JumpDirection = newDir;
                JumpDistance = dist;
            }

            stateElapsedTicks = 0;
            stateTotalTicks = Math.Max(catchRemainingTicks, 180L);
            TransitDuration = stateTotalTicks / 60f;
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;

            seq.RecoverStartTick = seq.ElapsedTicks;

            if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                hyperdictionSound.StopSound(false);

            SendNotification("HYPERDICTION FIELD COLLAPSED - RESUMING TRANSIT", 4f, "White");
        }

        /// <summary>
        /// The 9s sequence ended: open the exit portal at the drop point next to the Hyperdictor.
        /// The blackout and tumble are applied when the ship finishes emerging (FinishJump).
        /// </summary>
        public void HyperdictionDrop()
        {
            var seq = Hyperdiction;
            var mainGrid = GridSystem?.mainGrid;
            if (seq == null || mainGrid == null)
                return;

            HyperdictionRules.StartLockout(mainGrid);

            Vector3D center, tangent;
            seq.PathAt(HyperdictionSequence.TOTAL_TICKS, out center, out tangent);

            // Exit heading follows the end of the pull toward the Hyperdictor
            MatrixD cleanRot = seq.BaseRotationFor(tangent);
            JumpOrientation = cleanRot;
            JumpDirection = tangent;
            JumpDestination = seq.DropPos;
            JumpDistance = Vector3D.Distance(mainGrid.PositionComp.GetPosition(), seq.DropPos);

            // Settle the grid into a clean pose at the drop point so the portal geometry measures the true hull
            MatrixD cleanPose = cleanRot;
            cleanPose.Translation = center - Vector3D.TransformNormal(seq.LocalCenter, cleanRot);
            SafeTeleportMainGrid(cleanPose);

            Hyperdiction = null;
            hyperdictionLeadFxSpawned = false;
            hyperdictionAftermath = true;

            if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                hyperdictionSound.StopSound(false);

            StartJumpOut();
        }

        private Vector3D GetListenerAnchor(Vector3D fallback)
        {
            return MyAPIGateway.Session?.Camera != null ? MyAPIGateway.Session.Camera.Position : fallback;
        }

        // Ticked instead of the normal transit logic while the Hyperdictor holds this ship (not while recovering)
        private void UpdateHyperdictionTick(MyCubeGrid mainGrid)
        {
            var seq = Hyperdiction;
            seq.ElapsedTicks++;
            int tick = seq.ElapsedTicks;

            Vector3D center;
            MatrixD pose = seq.EvaluatePose(tick, out center);
            SafeTeleportMainGrid(pose);

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                {
                    hyperdictionSound.SetPosition(GetListenerAnchor(center));
                    hyperdictionSound.SetVelocity(Vector3.Zero);
                }
            }

            UpdateTransitClientFX(seq.ShakeAt(tick), false);

            if (tick >= HyperdictionSequence.TOTAL_TICKS)
            {
                if (IsAuthorityMachine)
                    HyperdictionSession.Instance?.OnSequenceComplete(this);
                else if (tick >= HyperdictionSequence.TOTAL_TICKS + 120)
                    HyperdictionDrop(); // the Drop message never arrived; leave the tunnel anyway
            }
        }

        // Power blackout (as long as a standard FSD's after a hyperdiction) + half-intensity tumble once the ship has emerged from the portal
        private void ApplyHyperdictionAftermath()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null)
                return;

            double blackoutSeconds = HyperdictionRules.AFTERMATH_SECONDS;
            long restoreTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(blackoutSeconds).Ticks;

            var grids = GridSystem.gridGroup;
            foreach (var g in grids)
            {
                var mg = g as MyCubeGrid;
                if (mg == null || mg.MarkedForClose) continue;
                foreach (var block in mg.GetFatBlocks())
                {
                    var producer = block as IMyPowerProducer;
                    if (producer != null && producer.IsFunctional && producer.Enabled)
                    {
                        producer.Enabled = false;
                        blackoutProducers.Add(producer);
                        HyperspaceSystem._globallyDisabledPower[producer] = restoreTicks;
                    }
                }
            }
            blackoutTicks = (int)(blackoutSeconds * 60.0);

            if (mainGrid.Physics != null)
            {
                float spinPitch = MyUtils.GetRandomSign() * MyUtils.GetRandomFloat(2.5f, 5.0f) * 0.5f;
                float spinYaw = MyUtils.GetRandomSign() * MyUtils.GetRandomFloat(2.5f, 5.0f) * 0.5f;
                float spinRoll = MyUtils.GetRandomSign() * MyUtils.GetRandomFloat(3.5f, 6.0f) * 0.5f;
                mainGrid.Physics.AngularVelocity = new Vector3(spinPitch, spinYaw, spinRoll);

                Vector3D yeet = (mainGrid.WorldMatrix.Forward * 0.8)
                    + (mainGrid.WorldMatrix.Right * MyUtils.GetRandomFloat(-0.6f, 0.6f))
                    + (mainGrid.WorldMatrix.Up * MyUtils.GetRandomFloat(-0.6f, 0.6f));
                yeet.Normalize();
                mainGrid.Physics.LinearVelocity = (Vector3)(yeet * 50.0);
            }
        }

        private void UpdateBlackout()
        {
            if (--blackoutTicks > 0)
                return;

            for (int i = 0; i < blackoutProducers.Count; i++)
            {
                var producer = blackoutProducers[i];
                HyperspaceSystem._globallyDisabledPower.Remove(producer);
                if (producer != null && !producer.Closed && producer.IsFunctional)
                    producer.Enabled = true;
            }
            blackoutProducers.Clear();
            blackoutTicks = 0;
            SendNotification("POWER RESTORED", 3f, "White");
        }

        private void UpdateTransitTick()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            // Held by a Hyperdictor: the deterministic 9s sequence replaces the normal transit logic
            if (Hyperdiction != null && !Hyperdiction.IsRecovering)
            {
                UpdateHyperdictionTick(mainGrid);
                return;
            }

            // --- Power & FSD loss detection during transit (matches HyperspaceSystem.UpdateTransitTick) ---
            bool fsdIssue = HostDrive == null || HostDrive.block == null
                || !HostDrive.block.Enabled
                || !HostDrive.block.IsFunctional;

            if (fsdIssue)
                fsdLossTicks++;
            else
                fsdLossTicks = 0;

            // Power check every 10 ticks
            if (stateElapsedTicks % 10 == 0 && !(MyAPIGateway.Session?.CreativeMode ?? false))
            {
                bool hasPower = false;
                var grids = GridSystem?.gridGroup;
                if (grids != null && grids.Count > 0)
                {
                    foreach (var g in grids)
                    {
                        if (g == null || g.MarkedForClose) continue;
                        foreach (var block in (g as MyCubeGrid)?.GetFatBlocks() ?? System.Linq.Enumerable.Empty<MyCubeBlock>())
                        {
                            var producer = block as IMyPowerProducer;
                            if (producer == null || !producer.IsFunctional || !producer.Enabled) continue;
                            var battery = block as Sandbox.ModAPI.Ingame.IMyBatteryBlock;
                            if (battery != null && (battery.ChargeMode == Sandbox.ModAPI.Ingame.ChargeMode.Recharge || battery.CurrentStoredPower <= 0.0001f))
                                continue;
                            if (producer.MaxOutput > 0.0001f) { hasPower = true; break; }
                        }
                        if (hasPower) break;
                    }
                }
                else
                {
                    hasPower = HostDrive?.hasPower ?? false;
                }

                if (!hasPower)
                    powerLossTicks += 10;
                else
                    powerLossTicks = 0;
            }

            // 30 consecutive ticks (0.5s) of sustained loss triggers emergency abort (no ship-yeet — Capital FSD aborts cleanly)
            if (fsdLossTicks >= 30)
            {
                AbortJump("FSD OFFLINE - TRANSIT ABORTED!");
                return;
            }
            if (powerLossTicks >= 30)
            {
                AbortJump("POWER FAILURE - TRANSIT ABORTED!");
                return;
            }

            float elapsedSeconds = stateElapsedTicks / 60f;
            float totalSeconds = TransitDuration;

            // Calculate smooth progress fraction and instantaneous velocity (0.5s departure accel & 0s arrival decel)
            float t;
            double speedMps;
            // Delegate to HyperspaceSystem's authoritative static implementation (no arrival deceleration for Capital FSD)
            HyperspaceSystem.CalculateTransitProgress(elapsedSeconds, totalSeconds, JumpDistance, out t, out speedMps, decelSeconds: 0.0);

            CurrentTransitSpeed = speedMps / 1000.0;

            // Dynamic in-transit obstacle + destination adjustment at 25%, 50%, and 90% travel progress
            if (t >= 0.25f && !checkedObstacle25)
            {
                checkedObstacle25 = true;
                PerformTransitObstacleCheck();
            }
            if (t >= 0.50f && !checkedObstacle50)
            {
                checkedObstacle50 = true;
                PerformTransitObstacleCheck();
            }
            if (t >= 0.90f && !checkedObstacle90)
            {
                checkedObstacle90 = true;
                PerformTransitObstacleCheck();
            }

            // Micro-teleport ship along transit conduit
            Vector3D currentPos = JumpOrigin + (JumpDirection * (JumpDistance * t));
            MatrixD currentMatrix = JumpOrientation;
            currentMatrix.Translation = currentPos;

            // Hyperdictor failed: the rotation offsets of the sequence settle back to neutral while the ship resumes transit
            float shakeMagnitude = 1.0f;
            if (Hyperdiction != null)
            {
                Hyperdiction.ElapsedTicks++;
                currentMatrix = Hyperdiction.ApplyOffsets(currentMatrix, Hyperdiction.ElapsedTicks);
                shakeMagnitude = Math.Max(shakeMagnitude, Hyperdiction.ShakeAt(Hyperdiction.ElapsedTicks));
                if (Hyperdiction.RecoveryComplete)
                    Hyperdiction = null;
            }

            SafeTeleportMainGrid(currentMatrix);

            // Planetary gravity exclusion zone check every 10 ticks during transit
            if (stateElapsedTicks % 10 == 0)
            {
                float localGrav;
                Vector3D naturalGrav = MyAPIGateway.Physics.CalculateNaturalGravityAt(currentPos, out localGrav);
                float allowInGravMax = (Settings.instance?.AllowInGravityMax ?? 0.2f);
                if ((naturalGrav.Length() / WarpSystem.EARTH_GRAVITY) > allowInGravMax)
                {
                    AbortJump("GRAVITY WELL EXCLUSION ZONE - TRANSIT ABORTED!");
                    return;
                }

                BoundingBoxD transitBox = new BoundingBoxD(currentPos - new Vector3D(50000), currentPos + new Vector3D(50000));
                var transitPlanet = MyGamePruningStructure.GetClosestPlanet(ref transitBox);
                if (transitPlanet != null)
                {
                    Vector3D pCenter = transitPlanet.PositionComp.GetPosition();
                    Vector3D pSurf = transitPlanet.GetClosestSurfacePointGlobal(ref currentPos);
                    double shipRadius = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
                    double minSafeAlt = 500.0 + shipRadius;
                    double alt = (currentPos - pCenter).Length() - (pSurf - pCenter).Length();
                    if (alt < minSafeAlt)
                    {
                        AbortJump("TERRAIN PROXIMITY - TRANSIT ABORTED 500m FROM GROUND!");
                        return;
                    }
                }
            }

            // Keep physics velocities zeroed during transit
            ClearPhysicsVelocities();

            // Client-side effects
            UpdateTransitClientFX(shakeMagnitude, true);

            // Destination Broadcast 1: 11 seconds before arrival (2s duration incoming warning)
            float secondsRemaining = totalSeconds - elapsedSeconds;
            if (secondsRemaining <= DEST_WARN_SECONDS_BEFORE && !destWarnTriggered)
            {
                destWarnTriggered = true;
                BroadcastGlobalAudio(JumpDestination, "fsd_cap_incoming", 5000f);
            }

            // Destination Broadcast 2: 9 seconds before arrival (pre-jump sound buildup)
            if (secondsRemaining <= DEST_PREJUMP_SECONDS_BEFORE && !destPrejumpTriggered)
            {
                destPrejumpTriggered = true;
                BroadcastGlobalAudio(JumpDestination, "fsd_cap_pre_jump", 5000f);
            }

            // Control loss debounce during transit
            if (GetShipController() == null)
                controlLossTicks++;
            else
                controlLossTicks = 0;

            if (controlLossTicks >= 30)
            {
                AbortJump("CONTROL LOST - TRANSIT ABORTED!");
                return;
            }

            if (stateElapsedTicks >= stateTotalTicks)
            {
                StartJumpOut();
            }
        }

        // Client-side transit effects: sun suppression, tunnel, spectator hiding and cockpit shake.
        // Shared by the normal transit tick and the hyperdiction sequence so the tunnel never drops out while a ship is held.
        private void UpdateTransitClientFX(float shakeMagnitude, bool playTransitLoop)
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            bool isLocalOnboard = IsLocalPlayerOnboard();

            if (isLocalOnboard)
            {
                HyperspaceSunManager.Instance.RequestSuppression(this, true);
                UpdateTransitTunnelFX(playTransitLoop);
                UpdateTransitSmoothReveal();

                // Spectator check for grid visibility during transit
                var controller = MyAPIGateway.Session?.CameraController;
                bool isValidView = (controller is Sandbox.ModAPI.IMyCockpit) || (controller is VRage.Game.ModAPI.IMyCharacter) || (controller is Sandbox.ModAPI.Ingame.IMyCameraBlock);
                bool isSpectator = !isValidView;

                if (isSpectator && !_spectatorHidden)
                {
                    _isTransitRevealActive = false;
                    _transitRevealBlocks.Clear();
                    HideAllBlocks(currentWipe);
                    _spectatorHidden = true;
                }
                else if (!isSpectator && _spectatorHidden)
                {
                    RestoreAllBlocksVisibility(true);
                    _spectatorHidden = false;
                }

                // Cockpit camera shake during transit
                var activeCockpit = GridSystem?.FindMainCockpit() as Sandbox.Game.Entities.MyCockpit;
                if (activeCockpit != null)
                    activeCockpit.AddShake(Hyperdiction == null ? ShakeShaper.Natural(shakeMagnitude, 0.7f) : shakeMagnitude);
            }
            else
            {
                HyperspaceSunManager.Instance.ReleaseSuppression(this, true);
                _isTransitRevealActive = false;
                _transitRevealBlocks.Clear();

                // Local player stepped off the ship during transit — stop tunnel FX immediately
                StopTransitParticles();
                if (transitSound != null && transitSound.IsPlaying)
                    transitSound.StopSound(true);
                if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                    hyperdictionSound.StopSound(true);
            }
        }

        private void PerformTransitObstacleCheck()
        {
            Vector3D safeDest;
            double safeDist;
            if (TryAdjustDestinationForObstacles(JumpOrigin, JumpDirection, JumpDistance, out safeDest, out safeDist))
            {
                JumpDestination = safeDest;
                JumpDistance = safeDist;
            }
        }

        public void StartJumpOut()
        {
            State = CapState.JumpOut;
            stateElapsedTicks = 0;
            stateTotalTicks = (long)(JUMP_OUT_SECONDS * 60f);

            // The tunnel stops emitting and fades out over the first seconds while the exit cloud forms (the sun stays hidden until then)
            StopTransitSound();
            _exitBridge = !MyAPIGateway.Utilities.IsDedicated && IsLocalPlayerOnboard();
            if (_exitBridge)
            {
                if (tunnelParticle != null) tunnelParticle.StopEmitting(JUMP_OUT_WIPE_DELAY_SECONDS);
            }
            else
            {
                StopTransitParticles();
                HyperspaceSunManager.Instance.ReleaseSuppression(this, false);
            }

            var mainGrid = GridSystem.mainGrid;
            Vector3D forward = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : mainGrid.WorldMatrix.Forward;

            double noseOffset, shipLength, crossRadius;
            ComputeShipExtents(forward, out noseOffset, out shipLength, out crossRadius);

            // Guarantee minimum 500m ground clearance on arrival
            BoundingBoxD destBox = new BoundingBoxD(JumpDestination - new Vector3D(100000), JumpDestination + new Vector3D(100000));
            var arrPlanet = MyGamePruningStructure.GetClosestPlanet(ref destBox);
            if (arrPlanet != null)
            {
                Vector3D pCenter = arrPlanet.PositionComp.GetPosition();
                Vector3D testDest = JumpDestination;
                Vector3D pSurf = arrPlanet.GetClosestSurfacePointGlobal(ref testDest);
                double minSafeAlt = 500.0 + crossRadius;
                double curAlt = (JumpDestination - pCenter).Length() - (pSurf - pCenter).Length();
                if (curAlt < minSafeAlt)
                {
                    Vector3D gNorm = Vector3D.Normalize(pSurf - pCenter);
                    JumpDestination = pSurf + (gNorm * minSafeAlt);
                }
            }

            _isTransitRevealActive = false;
            _transitRevealBlocks.Clear();

            Vector3D exitWormholePos = JumpDestination;
            double totalTravelDist = shipLength + 5.0;
            double stepDist = totalTravelDist / (JUMP_OUT_WIPE_DURATION_SECONDS * 60.0);

            // Place ship behind destination exit wormhole plane so its front nose is just behind the exit plane
            Vector3D targetCenter = exitWormholePos - (forward * (noseOffset + 5.0));
            Vector3D currentOrigin = mainGrid.PositionComp.GetPosition();
            Vector3D currentCenter = mainGrid.PositionComp.WorldAABB.Center;
            Vector3D targetOrigin = targetCenter + (currentOrigin - currentCenter);

            MatrixD targetMatrix = JumpOrientation;
            targetMatrix.Translation = targetOrigin;
            SafeTeleportMainGrid(targetMatrix);
            if (_exitBridge && tunnelParticle != null)
                AimTunnel();   // the fading tunnel jumps with the ship, so it is not left behind for a frame

            currentWipe = new WipeSession
            {
                ShipLength = shipLength,
                NoseOffset = noseOffset,
                CrossRadius = crossRadius,
                ExitWormholePos = exitWormholePos,
                PlaneOrigin = exitWormholePos,
                PlaneNormal = forward,
                StepDistance = stepDist
            };

            CollectAllBlocks(currentWipe);

            // Hide ALL blocks initially before emerging
            HideAllBlocks(currentWipe);

            // Spawn Exit Wormhole Flash and Black Oily Cloud at destination
            SpawnExitWormholeFX(exitWormholePos, forward, (float)crossRadius);

            // Play & broadcast jump-out audio
            BroadcastGlobalAudio(exitWormholePos, "fsd_cap_jump_out", 30000f);

            ClearPhysicsVelocities();
        }

        private void UpdateJumpOutTick()
        {
            if (currentWipe == null) return;

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            float elapsedSeconds = stateElapsedTicks / 60f;

            // Keep exit wormhole particle positioned at fixed exit point
            UpdateExitWormholeFX();

            // The fading tunnel follows the ship to the exit, then lets go of the sun
            if (_exitBridge)
            {
                float bridge = elapsedSeconds / JUMP_OUT_WIPE_DELAY_SECONDS;
                if (bridge >= 1f || !IsLocalPlayerOnboard())
                {
                    _exitBridge = false;
                    StopTransitParticles();
                    HyperspaceSunManager.Instance.ReleaseSuppression(this, false);
                }
                else
                {
                    UpdateTransitTunnelFX(false);
                }
            }

            // 0s - 2s: Cloud forms and tears open at destination (ship hidden)
            // 2s - 16s (14s duration): Ship micro-teleports forward out of cloud & reveals mesh
            if (stateElapsedTicks == 60)
            {
                SpawnCloudWormholeFX(currentWipe.ExitWormholePos, currentWipe.PlaneNormal, (float)currentWipe.CrossRadius);
            }

            if (elapsedSeconds >= JUMP_OUT_WIPE_DELAY_SECONDS && elapsedSeconds <= (JUMP_OUT_WIPE_DELAY_SECONDS + JUMP_OUT_WIPE_DURATION_SECONDS))
            {
                MatrixD m = mainGrid.WorldMatrix;
                m.Translation += currentWipe.PlaneNormal * currentWipe.StepDistance;
                SafeTeleportMainGrid(m);

                // Mesh Slice: Reveal blocks crossing through the fixed wormhole plane into realspace
                SliceBlocksReveal(currentWipe);
            }

            // 16s - 18s: Final settle, cloud fades out
            if (stateElapsedTicks >= stateTotalTicks)
            {
                FinishJump();
            }
        }

        private void FinishJump()
        {
            _exitBridge = false;
            RestoreAllBlocksVisibility();
            SetGridInvincibility(false);
            ClearPhysicsVelocities();
            _cachedPhysicalGrids.Clear();
            StopParticles();
            StopAllSounds();

            // Restore Character.Save on exit
            SetPlayersSave(true);
            _playersInTransit.Clear();

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid != null)
            {
                HyperspaceSystem.UnregisterClaim(mainGrid.EntityId);
            }

            // Clear GPS waypoint after jump (matches HyperspaceSystem.ExitHyperspace)
            SelectedGpsCoords = null;
            SelectedGpsName = string.Empty;

            // Reset obstacle check flags
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;
            fsdLossTicks = 0;
            powerLossTicks = 0;
            controlLossTicks = 0;

            State = CapState.Cooldown;
            stateElapsedTicks = 0;
            stateTotalTicks = (long)(COOLDOWN_SECONDS * 60f);

            if (hyperdictionAftermath)
            {
                hyperdictionAftermath = false;
                ApplyHyperdictionAftermath();
                SendNotification("FSD FAILURE - HYPERDICTION AFTERMATH - POWER LOST", 5f, "Red");
                return;
            }

            SendNotification("CAPITAL FSD JUMP COMPLETE - COOLDOWN ACTIVE", 4f, "White");
        }

        private void UpdateCooldownTick()
        {
            if (stateElapsedTicks >= stateTotalTicks)
            {
                State = CapState.Idle;
                SendNotification("CAPITAL FSD READY", 3f, "Green");
            }
        }

        public void AbortJump(string reason)
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid != null)
            {
                HyperspaceSystem.UnregisterClaim(mainGrid.EntityId);
            }

            HyperspaceSunManager.Instance.ReleaseSuppression(this, true);
            State = CapState.Idle;

            Hyperdiction = null;
            hyperdictionAftermath = false;
            if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                hyperdictionSound.StopSound(true);

            SetPlayersSave(true);
            _playersInTransit.Clear();
            StopAllSounds();
            StopParticles();
            RestoreAllBlocksVisibility();
            SetGridInvincibility(false);
            ClearPhysicsVelocities();
            _cachedPhysicalGrids.Clear();

            // Guarantee 500m clearance above ground on abort/drop
            if (mainGrid != null && (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated))
            {
                Vector3D shipPos = mainGrid.PositionComp.GetPosition();
                BoundingBoxD dropBox = new BoundingBoxD(shipPos - new Vector3D(100000), shipPos + new Vector3D(100000));
                var dropPlanet = MyGamePruningStructure.GetClosestPlanet(ref dropBox);
                if (dropPlanet != null)
                {
                    Vector3D pCenter = dropPlanet.PositionComp.GetPosition();
                    Vector3D pSurf = dropPlanet.GetClosestSurfacePointGlobal(ref shipPos);
                    double shipRadius = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
                    double minSafeAlt = 500.0 + shipRadius;
                    double curAlt = (shipPos - pCenter).Length() - (pSurf - pCenter).Length();
                    if (curAlt < minSafeAlt)
                    {
                        Vector3D gNorm = Vector3D.Normalize(pSurf - pCenter);
                        Vector3D safeDropPos = pSurf + (gNorm * minSafeAlt);
                        MatrixD safeMatrix = mainGrid.WorldMatrix;
                        safeMatrix.Translation = safeDropPos;
                        SafeTeleportMainGrid(safeMatrix);
                    }
                }
            }

            SendNotification(reason, 5f, "Red");
        }

        #region Ship Longitudinal Geometry Extents

        private void ComputeShipExtents(Vector3D forward, out double noseOffset, out double shipLength, out double crossRadius)
        {
            noseOffset = 50.0;
            shipLength = 100.0;
            crossRadius = 30.0;

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            Vector3D center = mainGrid.PositionComp.WorldAABB.Center;

            List<IMyCubeGrid> attachedGrids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Logical, attachedGrids);
            if (!attachedGrids.Contains(mainGrid))
                attachedGrids.Add(mainGrid);

            double maxFwd = double.MinValue;
            double minFwd = double.MaxValue;
            double maxPerpSq = 0.0;

            Vector3D[] corners = new Vector3D[8];

            foreach (var grid in attachedGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;

                var mg = grid as MyCubeGrid;
                if (mg == null) continue;

                MyOrientedBoundingBoxD obb = new MyOrientedBoundingBoxD(mg.PositionComp.LocalAABB, mg.WorldMatrix);
                obb.GetCorners(corners, 0);

                for (int i = 0; i < 8; i++)
                {
                    Vector3D offset = corners[i] - center;
                    double fwdProj = Vector3D.Dot(offset, forward);
                    if (fwdProj > maxFwd) maxFwd = fwdProj;
                    if (fwdProj < minFwd) minFwd = fwdProj;

                    Vector3D perpOffset = offset - (forward * fwdProj);
                    double pSq = perpOffset.LengthSquared();
                    if (pSq > maxPerpSq) maxPerpSq = pSq;
                }
            }

            if (maxFwd > minFwd)
            {
                noseOffset = maxFwd;
                shipLength = maxFwd - minFwd;
                crossRadius = Math.Sqrt(maxPerpSq);
            }
            else
            {
                BoundingSphereD sphere = BoundingSphereD.CreateFromBoundingBox(mainGrid.PositionComp.WorldAABB);
                noseOffset = sphere.Radius;
                shipLength = sphere.Radius * 2.0;
                crossRadius = sphere.Radius;
            }
        }

        #endregion

        #region Player Save Suppression

        private void CollectPlayersOnGrid()
        {
            _playersInTransit.Clear();
            var grids = GridSystem?.gridGroup;
            if (grids == null || grids.Count == 0) return;

            var seatedEntityIds = new List<long>();
            foreach (var grid in grids)
            {
                foreach (var block in (grid as MyCubeGrid)?.GetFatBlocks() ?? System.Linq.Enumerable.Empty<MyCubeBlock>())
                {
                    if (block == null) continue;
                    var cockpit = block as IMyCockpit;
                    var cryo = block as IMyCryoChamber;
                    if (cockpit != null && cockpit.Pilot != null)
                        seatedEntityIds.Add(cockpit.Pilot.EntityId);
                    else if (cryo != null && cryo.Pilot != null)
                        seatedEntityIds.Add(cryo.Pilot.EntityId);
                }
            }

            if (seatedEntityIds.Count == 0) return;

            var onlinePlayers = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(onlinePlayers);
            foreach (var player in onlinePlayers)
            {
                if (player != null && player.Character != null && seatedEntityIds.Contains(player.Character.EntityId))
                {
                    if (!_playersInTransit.Contains(player))
                        _playersInTransit.Add(player);
                }
            }
        }

        private void SetPlayersSave(bool save)
        {
            foreach (var player in _playersInTransit)
            {
                if (player == null || player.Character == null) continue;
                if (player.Character.Save != save)
                    player.Character.Save = save;
            }
        }

        private void SetGridInvincibility(bool invincible)
        {
            var grids = GridSystem?.gridGroup;
            if (grids == null) return;
            bool defaultDestructible = MyAPIGateway.Session?.SessionSettings?.DestructibleBlocks ?? true;
            foreach (var grid in grids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                var cg = grid as MyCubeGrid; if (cg != null) cg.DestructibleBlocks = invincible ? false : defaultDestructible;
            }
        }

        #endregion

        #region Dual-Layer Mesh Wipe Helpers

        private void CollectAllBlocks(WipeSession session)
        {
            session.BlocksToHide.Clear();
            session.BlocksToReveal.Clear();
            session.HiddenBlocks.Clear();
            session.HiddenFatBlocks.Clear();
            session.AccumulatedTravel = 0.0;

            RefreshCachedPhysicalGrids();

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            List<IMyCubeGrid> attachedGrids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Logical, attachedGrids);
            if (!attachedGrids.Contains(mainGrid))
                attachedGrids.Add(mainGrid);

            foreach (var grid in attachedGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                List<IMySlimBlock> blockList = new List<IMySlimBlock>();
                grid.GetBlocks(blockList);
                for (int i = 0; i < blockList.Count; i++)
                {
                    var block = blockList[i];
                    if (block == null) continue;
                    Vector3D blockCenter;
                    block.ComputeWorldCenter(out blockCenter);
                    double initialDot = Vector3D.Dot(blockCenter - session.PlaneOrigin, session.PlaneNormal);
                    
                    var data = new BlockWipeData { Block = block, TriggerTravel = -initialDot };
                    session.BlocksToHide.Add(data);
                    session.BlocksToReveal.Add(data);
                }
            }

            session.BlocksToHide.Sort((a, b) => b.TriggerTravel.CompareTo(a.TriggerTravel));
            session.BlocksToReveal.Sort((a, b) => b.TriggerTravel.CompareTo(a.TriggerTravel));
        }

        private void SliceBlocksHide(WipeSession session)
        {
            session.AccumulatedTravel += session.StepDistance;

            int count = session.BlocksToHide.Count;
            for (int i = count - 1; i >= 0; i--)
            {
                var data = session.BlocksToHide[i];
                if (data.Block == null)
                {
                    session.BlocksToHide.RemoveAt(i);
                    continue;
                }

                if (session.AccumulatedTravel > data.TriggerTravel)
                {
                    HideBlock(data.Block, session);
                    session.BlocksToHide.RemoveAt(i);
                }
                else
                {
                    break;
                }
            }
        }

        private void SliceBlocksReveal(WipeSession session)
        {
            session.AccumulatedTravel += session.StepDistance;

            int count = session.BlocksToReveal.Count;
            for (int i = count - 1; i >= 0; i--)
            {
                var data = session.BlocksToReveal[i];
                if (data.Block == null)
                {
                    session.BlocksToReveal.RemoveAt(i);
                    continue;
                }

                if (session.AccumulatedTravel > data.TriggerTravel)
                {
                    RevealBlock(data.Block, session);
                    session.BlocksToReveal.RemoveAt(i);
                }
                else
                {
                    break;
                }
            }
        }

        private void HideAllBlocks(WipeSession session)
        {
            if (session == null || session.BlocksToReveal == null) return;
            for (int i = 0; i < session.BlocksToReveal.Count; i++)
            {
                var data = session.BlocksToReveal[i];
                if (data.Block != null)
                {
                    HideBlock(data.Block, session);
                }
            }
        }

        private void StartTransitSmoothReveal()
        {
            _isTransitRevealActive = false;
            _transitRevealBlocks.Clear();
            _transitRevealIndex = 0;

            if (MyAPIGateway.Utilities.IsDedicated) return;

            var localPlayer = MyAPIGateway.Session?.Player;
            bool isOnBoard = false;
            if (localPlayer != null)
            {
                for (int i = 0; i < _playersInTransit.Count; i++)
                {
                    var p = _playersInTransit[i];
                    if (p != null && p.IdentityId == localPlayer.IdentityId)
                    {
                        isOnBoard = true;
                        break;
                    }
                }
            }
            if (!isOnBoard) return; // Outsiders keep the ship hidden!

            // Populate blocks ordered from nose to tail
            if (currentWipe != null && currentWipe.BlocksToReveal != null && currentWipe.BlocksToReveal.Count > 0)
            {
                int count = currentWipe.BlocksToReveal.Count;
                // BlocksToReveal is sorted descending by TriggerTravel (tail at index 0, nose at index count - 1)
                // Add in reverse so index 0 is nose, progressing to tail
                for (int i = count - 1; i >= 0; i--)
                {
                    var b = currentWipe.BlocksToReveal[i].Block;
                    if (b != null)
                    {
                        _transitRevealBlocks.Add(b);
                    }
                }
            }
            else
            {
                var mainGrid = GridSystem?.mainGrid;
                if (mainGrid != null)
                {
                    List<IMyCubeGrid> attachedGrids = new List<IMyCubeGrid>();
                    MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Logical, attachedGrids);
                    if (!attachedGrids.Contains(mainGrid))
                        attachedGrids.Add(mainGrid);

                    foreach (var grid in attachedGrids)
                    {
                        if (grid == null || grid.MarkedForClose) continue;
                        List<IMySlimBlock> blockList = new List<IMySlimBlock>();
                        grid.GetBlocks(blockList);
                        _transitRevealBlocks.AddRange(blockList);
                    }
                }
            }

            if (_transitRevealBlocks.Count == 0) return;

            _transitRevealPerTick = Math.Max(1, (_transitRevealBlocks.Count + TRANSIT_REVEAL_TICKS - 1) / TRANSIT_REVEAL_TICKS);
            _isTransitRevealActive = true;
        }

        private void UpdateTransitSmoothReveal()
        {
            if (!_isTransitRevealActive) return;

            int total = _transitRevealBlocks.Count;
            int end = Math.Min(_transitRevealIndex + _transitRevealPerTick, total);

            for (int i = _transitRevealIndex; i < end; i++)
            {
                var b = _transitRevealBlocks[i];
                if (b != null)
                {
                    RevealBlock(b, currentWipe);
                }
            }

            _transitRevealIndex = end;

            if (_transitRevealIndex >= total)
            {
                _isTransitRevealActive = false;
                _transitRevealBlocks.Clear();

                var mainGrid = GridSystem?.mainGrid;
                if (mainGrid != null)
                {
                    List<IMyCubeGrid> attachedGrids = new List<IMyCubeGrid>();
                    MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Logical, attachedGrids);
                    if (!attachedGrids.Contains(mainGrid))
                        attachedGrids.Add(mainGrid);

                    foreach (var grid in attachedGrids)
                    {
                        var mg = grid as MyCubeGrid;
                        if (mg != null && mg.Render != null)
                        {
                            mg.Render.Visible = true;
                        }
                    }
                }
            }
        }

        public void RestoreAllBlocksVisibility(bool forceForAll = true)
        {
            _isTransitRevealActive = false;
            _transitRevealBlocks.Clear();

            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            if (!forceForAll)
            {
                var localPlayer = MyAPIGateway.Session?.Player;
                bool isOnBoard = false;
                if (localPlayer != null)
                {
                    for (int i = 0; i < _playersInTransit.Count; i++)
                    {
                        var p = _playersInTransit[i];
                        if (p != null && p.IdentityId == localPlayer.IdentityId)
                        {
                            isOnBoard = true;
                            break;
                        }
                    }
                }
                if (!isOnBoard) return; // Outsiders keep the ship hidden!
            }

            List<IMyCubeGrid> attachedGrids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Logical, attachedGrids);
            if (!attachedGrids.Contains(mainGrid))
                attachedGrids.Add(mainGrid);

            foreach (var grid in attachedGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                List<IMySlimBlock> blockList = new List<IMySlimBlock>();
                grid.GetBlocks(blockList);
                for (int i = 0; i < blockList.Count; i++)
                {
                    var b = blockList[i];
                    if (b != null)
                    {
                        RevealBlock(b, currentWipe);
                    }
                }

                var mg = grid as MyCubeGrid;
                if (mg != null && mg.Render != null)
                {
                    mg.Render.Visible = true;
                }
            }

            // Restore any remaining saved thrusters or lights
            if (_savedThrustMultipliers.Count > 0)
            {
                foreach (var kvp in _savedThrustMultipliers)
                {
                    var ent = MyAPIGateway.Entities.GetEntityById(kvp.Key) as IMyThrust;
                    if (ent != null && !ent.MarkedForClose)
                    {
                        ent.ThrustMultiplier = kvp.Value;
                    }
                }
                _savedThrustMultipliers.Clear();
            }

            if (_savedThrustEnabled.Count > 0)
            {
                foreach (var kvp in _savedThrustEnabled)
                {
                    var ent = MyAPIGateway.Entities.GetEntityById(kvp.Key) as IMyThrust;
                    if (ent != null && !ent.MarkedForClose)
                    {
                        ent.Enabled = kvp.Value;
                    }
                }
                _savedThrustEnabled.Clear();
            }

            if (_savedLightStates.Count > 0)
            {
                foreach (var kvp in _savedLightStates)
                {
                    var ent = MyAPIGateway.Entities.GetEntityById(kvp.Key) as IMyLightingBlock;
                    if (ent != null && !ent.MarkedForClose)
                    {
                        ent.Enabled = kvp.Value;
                    }
                }
                _savedLightStates.Clear();
            }

            if (currentWipe != null)
            {
                currentWipe.HiddenBlocks.Clear();
                currentWipe.HiddenFatBlocks.Clear();
            }
        }

        private static void SetSubpartsTransparency(MyEntitySubpart subpart, float transparency)
        {
            if (subpart == null) return;
            if (subpart.Render != null)
            {
                subpart.Render.Visible = true;
                subpart.Render.Transparency = transparency;
                subpart.Render.UpdateTransparency();
            }
            if (subpart.Subparts != null)
            {
                foreach (var child in subpart.Subparts.Values)
                {
                    SetSubpartsTransparency(child, transparency);
                }
            }
        }

        private void HideBlock(IMySlimBlock block, WipeSession session)
        {
            if (block == null) return;
            if (MyAPIGateway.Utilities.IsDedicated) return;

            try
            {
                block.Dithering = -1f;

                var fat = block.FatBlock;
                if (fat != null)
                {
                    // Safeguard: Preserve the cockpit interior for the local seated pilot in first-person view
                    bool isLocalFirstPersonCockpit = false;
                    var cockpit = fat as IMyCockpit;
                    if (cockpit != null && cockpit.Pilot != null)
                    {
                        var localChar = MyAPIGateway.Session?.Player?.Character;
                        if (localChar != null && cockpit.Pilot.EntityId == localChar.EntityId && cockpit.IsInFirstPersonView)
                        {
                            isLocalFirstPersonCockpit = true;
                        }
                    }

                    if (!isLocalFirstPersonCockpit)
                    {
                        var fatEntity = fat as MyCubeBlock;
                        if (fatEntity != null)
                        {
                            if (fatEntity.Render != null)
                            {
                                fatEntity.Render.Transparency = -1f;
                                fatEntity.Render.UpdateTransparency();
                            }

                            if (fatEntity.Subparts != null)
                            {
                                foreach (var sub in fatEntity.Subparts.Values)
                                {
                                    SetSubpartsTransparency(sub, -1f);
                                }
                            }
                        }
                    }

                    // Suppress thruster flames and heat particles
                    var thrust = fat as IMyThrust;
                    if (thrust != null)
                    {
                        if (!_savedThrustMultipliers.ContainsKey(thrust.EntityId))
                        {
                            _savedThrustMultipliers[thrust.EntityId] = thrust.ThrustMultiplier;
                        }
                        thrust.ThrustMultiplier = 0f;

                        if (!_savedThrustEnabled.ContainsKey(thrust.EntityId))
                        {
                            _savedThrustEnabled[thrust.EntityId] = thrust.Enabled;
                        }
                        thrust.Enabled = false;
                    }

                    // Suppress lighting block glare / cones
                    var light = fat as IMyLightingBlock;
                    if (light != null)
                    {
                        if (!_savedLightStates.ContainsKey(light.EntityId))
                        {
                            _savedLightStates[light.EntityId] = light.Enabled;
                        }
                        light.Enabled = false;
                    }
                }
            }
            catch (Exception) { }
        }

        private void RevealBlock(IMySlimBlock block, WipeSession session)
        {
            if (block == null) return;
            if (MyAPIGateway.Utilities.IsDedicated) return;

            try
            {
                block.Dithering = 0f;

                var fat = block.FatBlock;
                if (fat != null)
                {
                    var fatEntity = fat as MyCubeBlock;
                    if (fatEntity != null)
                    {
                        if (fatEntity.Render != null)
                        {
                            fatEntity.Render.Visible = true;
                            fatEntity.Render.Transparency = 0f;
                            fatEntity.Render.UpdateTransparency();
                        }

                        if (fatEntity.Subparts != null)
                        {
                            foreach (var sub in fatEntity.Subparts.Values)
                            {
                                SetSubpartsTransparency(sub, 0f);
                            }
                        }
                    }

                    // Restore thruster flames
                    var thrust = fat as IMyThrust;
                    if (thrust != null)
                    {
                        float origMultiplier;
                        if (_savedThrustMultipliers.TryGetValue(thrust.EntityId, out origMultiplier))
                        {
                            thrust.ThrustMultiplier = origMultiplier;
                            _savedThrustMultipliers.Remove(thrust.EntityId);
                        }
                        else
                        {
                            thrust.ThrustMultiplier = 1f;
                        }

                        bool origEnabled;
                        if (_savedThrustEnabled.TryGetValue(thrust.EntityId, out origEnabled))
                        {
                            thrust.Enabled = origEnabled;
                            _savedThrustEnabled.Remove(thrust.EntityId);
                        }
                        else
                        {
                            thrust.Enabled = true;
                        }
                    }

                    // Restore lights
                    var light = fat as IMyLightingBlock;
                    if (light != null)
                    {
                        bool origEnabled;
                        if (_savedLightStates.TryGetValue(light.EntityId, out origEnabled))
                        {
                            light.Enabled = origEnabled;
                            _savedLightStates.Remove(light.EntityId);
                        }
                        else
                        {
                            light.Enabled = true;
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        #endregion

        #region Physics & Teleportation

        private void SafeTeleportMainGrid(MatrixD targetMatrix)
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
            {
                if (Hyperdiction != null)
                    HyperspaceSystem.TeleportWithRotation(mainGrid, targetMatrix);
                else
                    mainGrid.Teleport(targetMatrix);
            }

            ClearPhysicsVelocities();
        }

        private void RefreshCachedPhysicalGrids()
        {
            _cachedPhysicalGrids.Clear();
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null || mainGrid.MarkedForClose) return;

            MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Physical, _cachedPhysicalGrids);
            if (!_cachedPhysicalGrids.Contains(mainGrid))
            {
                _cachedPhysicalGrids.Add(mainGrid);
            }
        }

        private void ClearPhysicsVelocities()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            if (_cachedPhysicalGrids.Count == 0)
            {
                RefreshCachedPhysicalGrids();
            }

            for (int i = 0; i < _cachedPhysicalGrids.Count; i++)
            {
                var g = _cachedPhysicalGrids[i];
                if (g == null || g.MarkedForClose) continue;

                var mg = g as MyCubeGrid;
                if (mg != null && mg.Physics != null)
                {
                    mg.Physics.LinearVelocity = Vector3.Zero;
                    mg.Physics.AngularVelocity = Vector3.Zero;
                }
            }
        }

        #endregion

        #region Collision Checking & 500m Obstacle Clearance

        public bool HasObstacleAhead(MyCubeGrid warpGrid, Vector3D forward, Vector3D gridCenter)
        {
            if (warpGrid == null) return false;

            double noseOffset, shipLength, crossRadius;
            ComputeShipExtents(forward, out noseOffset, out shipLength, out crossRadius);

            double corridorRadius = crossRadius + 5.0;
            double corridorLength = 600.0;

            Vector3D startPos = gridCenter + (forward * (noseOffset + 5.0));
            Vector3D endPos = startPos + (forward * corridorLength);
            RayD flightRay = new RayD(startPos, forward);

            double scanRadius = Math.Max(corridorLength * 0.55, 2000.0);
            BoundingSphereD querySphere = new BoundingSphereD(startPos + (forward * (corridorLength * 0.5)), scanRadius);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref querySphere);
            if (entities == null || entities.Count == 0)
                return false;

            foreach (var ent in entities)
            {
                if (ent == null || ent.MarkedForClose) continue;

                if (ent is MySafeZone)
                {
                    var sz = ent as MySafeZone;
                    if (sz != null)
                    {
                        var szSphere = new BoundingSphereD(sz.PositionComp.GetPosition(), sz.Radius + corridorRadius);
                        double? hit = szSphere.Intersects(flightRay);
                        if (hit.HasValue && hit.Value >= 0 && hit.Value <= corridorLength)
                        {
                            SendNotification("Can't Start FSD - SafeZone in flight path!", 4f, "Red");
                            return true;
                        }
                    }
                    continue;
                }

                var foundGrid = ent as IMyCubeGrid;
                if (foundGrid != null)
                {
                    if (GridSystem != null && GridSystem.gridGroup != null)
                    {
                        bool isMember = false;
                        foreach (var g in GridSystem.gridGroup)
                        {
                            if (g != null && g.EntityId == foundGrid.EntityId)
                            {
                                isMember = true;
                                break;
                            }
                        }
                        if (isMember) continue;
                    }

                    BoundingSphereD obstacleSphere = foundGrid.PositionComp.WorldVolume;
                    Vector3D seg = endPos - startPos;
                    double segLenSq = seg.LengthSquared();
                    double t = (segLenSq > 1e-6) ? MathHelper.Clamp(Vector3D.Dot(obstacleSphere.Center - startPos, seg) / segLenSq, 0.0, 1.0) : 0.0;
                    Vector3D closestPt = startPos + (seg * t);
                    double maxDist = obstacleSphere.Radius + corridorRadius;
                    if (Vector3D.DistanceSquared(obstacleSphere.Center, closestPt) <= maxDist * maxDist)
                    {
                        Vector3D localPt = Vector3D.Transform(closestPt, foundGrid.PositionComp.WorldMatrixInvScaled);
                        double localDistSq = ((BoundingBoxD)foundGrid.PositionComp.LocalAABB).DistanceSquared(localPt);
                        if (localDistSq <= corridorRadius * corridorRadius)
                        {
                            string gridName = !string.IsNullOrWhiteSpace(foundGrid.DisplayName) ? foundGrid.DisplayName :
                                              (!string.IsNullOrWhiteSpace(foundGrid.CustomName) ? foundGrid.CustomName : "Ship/Station");
                            SendNotification($"Can't Start FSD - Obstacle ahead: {gridName}", 4f, "Red");
                            return true;
                        }
                    }
                }
                else if (ent is MyVoxelBase)
                {
                    var voxel = ent as MyVoxelBase;
                    if (voxel == null) continue;
                    if (voxel is MyPlanet || voxel.RootVoxel is MyPlanet) continue;

                    double asteroidScanDist = Math.Max(corridorLength, 3000.0);
                    BoundingBoxD asteroidCorridorBox = new BoundingBoxD(
                        Vector3D.Min(startPos, startPos + forward * asteroidScanDist) - new Vector3D(corridorRadius),
                        Vector3D.Max(startPos, startPos + forward * asteroidScanDist) + new Vector3D(corridorRadius)
                    );

                    if (voxel.PositionComp.WorldAABB.Intersects(asteroidCorridorBox))
                    {
                        if (voxel.GetIntersectionWithAABB(ref asteroidCorridorBox))
                        {
                            SendNotification("Can't Start FSD - Asteroid in flight path!", 4f, "Red");
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public bool TryAdjustDestinationForObstacles(Vector3D origin, Vector3D dir, double dist, out Vector3D safeDest, out double safeDist)
        {
            safeDest = origin + (dir * dist);
            safeDist = dist;
            bool adjusted = false;

            double shipRadius = GridSystem?.mainGrid?.PositionComp.WorldAABB.HalfExtents.AbsMax() ?? 50.0;
            double safetyBuffer = 500.0 + shipRadius;
            double searchRadius = Math.Max(safetyBuffer * 4.0, 6000.0);

            long myGridId = GridSystem?.mainGrid?.EntityId ?? 0L;

            BoundingSphereD searchSphere = new BoundingSphereD(safeDest, searchRadius);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref searchSphere);

            List<BoundingBoxD> obstacleBoxes = new List<BoundingBoxD>();
            List<BoundingSphereD> safeZones = new List<BoundingSphereD>();

            if (entities != null && entities.Count > 0)
            {
                foreach (var ent in entities)
                {
                    if (ent == null || ent.MarkedForClose) continue;

                    var grid = ent as MyCubeGrid;
                    if (grid != null)
                    {
                        if (grid.EntityId == myGridId || (GridSystem != null && GridSystem.Contains(grid)))
                            continue;
                    }

                    var safeZone = ent as MySafeZone;
                    if (safeZone != null)
                    {
                        safeZones.Add(new BoundingSphereD(safeZone.PositionComp.GetPosition(), safeZone.Radius));
                        continue;
                    }

                    if (ent is MyCubeGrid)
                    {
                        obstacleBoxes.Add(ent.PositionComp.WorldAABB);
                    }
                    else if (ent is MyVoxelBase)
                    {
                        var voxel = ent as MyVoxelBase;
                        if (voxel is MyPlanet || voxel.RootVoxel is MyPlanet) continue;
                        obstacleBoxes.Add(ent.PositionComp.WorldAABB);
                    }
                }
            }

            List<IMyVoxelBase> voxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels);
            foreach (var voxel in voxels)
            {
                if (voxel == null || voxel.MarkedForClose) continue;
                if (voxel is MyPlanet || (voxel as MyVoxelBase)?.RootVoxel is MyPlanet) continue;

                BoundingBoxD vBox = voxel.PositionComp.WorldAABB;
                if (vBox.Intersects(searchSphere))
                {
                    bool alreadyAdded = false;
                    for (int i = 0; i < obstacleBoxes.Count; i++)
                    {
                        if (Vector3D.DistanceSquared(obstacleBoxes[i].Center, vBox.Center) < 1.0)
                        {
                            alreadyAdded = true;
                            break;
                        }
                    }
                    if (!alreadyAdded)
                    {
                        obstacleBoxes.Add(vBox);
                    }
                }
            }

            foreach (var box in obstacleBoxes)
            {
                double rayBackDist = Math.Max(box.HalfExtents.AbsMax() * 2.0 + 1000.0, 6000.0);
                Vector3D rayStart = safeDest - (dir * rayBackDist);
                RayD localRay = new RayD(rayStart, dir);

                double? hit = box.Intersects(localRay);
                if (hit.HasValue)
                {
                    Vector3D hitPoint = rayStart + (dir * hit.Value);
                    Vector3D newArrival = hitPoint - (dir * safetyBuffer);
                    double newDist = Vector3D.Dot(newArrival - origin, dir);
                    if (newDist < safeDist && newDist > 0)
                    {
                        safeDist = newDist;
                        safeDest = newArrival;
                        adjusted = true;
                    }
                }
                else
                {
                    Vector3D closest = Vector3D.Clamp(safeDest, box.Min, box.Max);
                    double d = Vector3D.Distance(safeDest, closest);
                    if (d < safetyBuffer)
                    {
                        double tProj = Vector3D.Dot(closest - origin, dir);
                        if (tProj > 0)
                        {
                            Vector3D pProj = origin + (dir * tProj);
                            double dPerp = Vector3D.Distance(closest, pProj);
                            if (dPerp < safetyBuffer)
                            {
                                double forwardOffset = Math.Sqrt((safetyBuffer * safetyBuffer) - (dPerp * dPerp));
                                double newDist = tProj - forwardOffset;
                                if (newDist < safeDist && newDist > 0)
                                {
                                    safeDist = newDist;
                                    safeDest = origin + (dir * safeDist);
                                    adjusted = true;
                                }
                            }
                        }
                    }
                }
            }

            foreach (var zone in safeZones)
            {
                BoundingSphereD expandedZone = new BoundingSphereD(zone.Center, zone.Radius + safetyBuffer);
                double distToZone = Vector3D.Distance(safeDest, zone.Center);
                if (distToZone < (zone.Radius + safetyBuffer))
                {
                    double rayBackDist = Math.Max(zone.Radius * 2.0 + 1000.0, 6000.0);
                    Vector3D rayStart = safeDest - (dir * rayBackDist);
                    RayD localRay = new RayD(rayStart, dir);
                    double? hitZone = expandedZone.Intersects(localRay);
                    if (hitZone.HasValue)
                    {
                        Vector3D hitPoint = rayStart + (dir * hitZone.Value);
                        double newDist = Vector3D.Dot(hitPoint - origin, dir);
                        if (newDist < safeDist && newDist > 0)
                        {
                            safeDist = newDist;
                            safeDest = origin + (dir * safeDist);
                            adjusted = true;
                        }
                    }
                }
            }

            // Active arrival claims: prevent simultaneous same-frame collisions with other jumping ships
            // (mirrors the _activeClaims check in HyperspaceSystem.TryAdjustDestinationForObstacles)
            lock (HyperspaceSystem._activeClaims_Lock)
            {
                var activeClaims = HyperspaceSystem.GetActiveClaims();
                for (int i = 0; i < activeClaims.Count; i++)
                {
                    var claim = activeClaims[i];
                    if (myGridId != 0L && claim.GridEntityId == myGridId) continue;

                    double combinedBuffer = safetyBuffer + claim.SafetyRadius;
                    double d = Vector3D.Distance(safeDest, claim.Destination);
                    if (d < combinedBuffer)
                    {
                        double pushBack = (combinedBuffer - d) + 50.0;
                        double newDist = safeDist - pushBack;
                        if (newDist < safeDist && newDist > 0)
                        {
                            safeDist = newDist;
                            safeDest = origin + (dir * safeDist);
                            adjusted = true;
                        }
                    }
                }
            }

            // Planetary safeguard: prevent jumping into or through planets, ensuring ship drops at least 500m from the ground
            List<IMyVoxelBase> planetVoxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(planetVoxels);
            foreach (var voxel in planetVoxels)
            {
                var planet = voxel as MyPlanet;
                if (planet == null) continue;

                Vector3D planetCenter = planet.PositionComp.GetPosition();
                Vector3D toCenter = planetCenter - origin;
                double tca = Vector3D.Dot(toCenter, dir);
                double d2 = toCenter.LengthSquared() - (tca * tca);
                double maxRadius = planet.MaximumRadius;

                // Check trajectory passing through or towards planet
                if (d2 < maxRadius * maxRadius && tca > 0)
                {
                    double thc = Math.Sqrt(Math.Max(0.0, maxRadius * maxRadius - d2));
                    double tEntry = tca - thc;

                    if (tEntry < safeDist)
                    {
                        // Trajectory penetrates planet bounding sphere before or at arrival
                        Vector3D testPoint = origin + (dir * Math.Max(0.0, Math.Min(tca, safeDist)));
                        Vector3D surfacePoint = planet.GetClosestSurfacePointGlobal(ref testPoint);
                        Vector3D groundNormal = Vector3D.Normalize(surfacePoint - planetCenter);
                        Vector3D safeArrival = surfacePoint + (groundNormal * safetyBuffer);

                        double newDist = Vector3D.Dot(safeArrival - origin, dir);
                        if (newDist > 0 && newDist < safeDist)
                        {
                            safeDist = newDist;
                            safeDest = origin + (dir * safeDist);
                            adjusted = true;
                        }
                    }
                }

                // Verify destination altitude above terrain surface: ensure at least 500m + shipRadius
                Vector3D surfAtDest = planet.GetClosestSurfacePointGlobal(ref safeDest);
                double distCenterAtDest = (safeDest - planetCenter).Length();
                double surfDistCenterAtDest = (surfAtDest - planetCenter).Length();
                double altitude = distCenterAtDest - surfDistCenterAtDest;

                if (altitude < safetyBuffer)
                {
                    Vector3D groundNorm = Vector3D.Normalize(surfAtDest - planetCenter);
                    Vector3D safeArrival = surfAtDest + (groundNorm * safetyBuffer);
                    double newDist = Vector3D.Dot(safeArrival - origin, dir);
                    if (newDist > 0 && newDist < safeDist)
                    {
                        safeDist = newDist;
                        safeDest = origin + (dir * safeDist);
                        adjusted = true;
                    }
                    else
                    {
                        safeDest = safeArrival;
                        safeDist = Vector3D.Distance(origin, safeDest);
                        adjusted = true;
                    }
                }
            }

            return adjusted;
        }

        #endregion

        #region Particles & Audio

        private void StopLocalSound()
        {
            if (localEmitter != null && localEmitter.IsPlaying)
            {
                localEmitter.StopSound(false);
            }
        }

        private void PlayLocalSound(string subtypeId)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;
            try
            {
                if (localEmitter == null)
                {
                    var cubeGrid = HostDrive?.block?.CubeGrid as MyCubeGrid;
                    if (cubeGrid != null)
                    {
                        localEmitter = new MyEntity3DSoundEmitter(cubeGrid)
                        {
                            CanPlayLoopSounds = true
                        };
                    }
                }

                if (localEmitter != null)
                {
                    var soundPair = new MySoundPair(subtypeId);
                    localEmitter.PlaySound(soundPair);
                }
            }
            catch (Exception) { }
        }

        private void StopAllSounds()
        {
            try
            {
                if (localEmitter != null && localEmitter.IsPlaying)
                    localEmitter.StopSound(true);

                if (transitSound != null && transitSound.IsPlaying)
                    transitSound.StopSound(true);
            }
            catch (Exception) { }
        }

        private void StopTransitSound()
        {
            try
            {
                if (transitSound != null && transitSound.IsPlaying)
                    transitSound.StopSound(false);
            }
            catch (Exception) { }
        }

        private void SpawnEntryWormholeFX(Vector3D pos, Vector3D dir, float radius)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            try
            {
                StopWormholeParticles();

                MatrixD matrix = MatrixD.CreateWorld(pos, -dir, JumpOrientation.Up);
                float scale = MathHelper.Clamp(radius / 12f, 4.0f, 18f);

                // 1. Initial violent flash tearing open space
                MyParticlesManager.TryCreateParticleEffect("HyperspaceJumpOrigin", ref matrix, ref pos, uint.MaxValue, out wormholeFlashParticle);
                if (wormholeFlashParticle != null)
                {
                    wormholeFlashParticle.UserScale = scale;
                }
            }
            catch (Exception) { }
        }

        private void UpdateEntryWormholeFX()
        {
            if (currentWipe == null || MyAPIGateway.Utilities.IsDedicated) return;

            try
            {
                Vector3D pos = currentWipe.EntryWormholePos;
                MatrixD m = MatrixD.CreateWorld(pos, -currentWipe.PlaneNormal, JumpOrientation.Up);

                if (wormholeCloudParticle != null && !wormholeCloudParticle.IsStopped)
                {
                    wormholeCloudParticle.WorldMatrix = m;

                    float cloudAge = (stateElapsedTicks - 60f) / 60f;
                    if (cloudAge >= 0)
                    {
                        float baseScale = MathHelper.Clamp((float)currentWipe.CrossRadius / 12f, 4.0f, 18f);
                        float scaleMultiplier = 1f;

                        if (cloudAge < 2.0f)
                            scaleMultiplier = cloudAge / 2.0f;
                        else if (cloudAge > (JUMP_IN_SECONDS - 1f - 2.0f))
                            scaleMultiplier = 1f - MathHelper.Clamp((cloudAge - (JUMP_IN_SECONDS - 1f - 2.0f)) / 2.0f, 0f, 1f);

                        wormholeCloudParticle.UserScale = baseScale * scaleMultiplier;
                    }
                }
            }
            catch (Exception) { }
        }

        private void SpawnExitWormholeFX(Vector3D pos, Vector3D dir, float radius)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            try
            {
                StopWormholeParticles();

                MatrixD matrix = MatrixD.CreateWorld(pos, -dir, JumpOrientation.Up);
                float scale = MathHelper.Clamp(radius / 12f, 4.0f, 18f);

                // 1. Exit flash
                MyParticlesManager.TryCreateParticleEffect("HyperspaceJumpArrival", ref matrix, ref pos, uint.MaxValue, out wormholeFlashParticle);
                if (wormholeFlashParticle != null)
                {
                    wormholeFlashParticle.UserScale = scale;
                }
            }
            catch (Exception) { }
        }

        private void SpawnCloudWormholeFX(Vector3D pos, Vector3D dir, float radius)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            try
            {
                if (wormholeCloudParticle != null)
                {
                    wormholeCloudParticle.Stop();
                }

                MatrixD matrix = MatrixD.CreateWorld(pos, -dir, JumpOrientation.Up);
                float scale = MathHelper.Clamp(radius / 12f, 4.0f, 18f);

                if (!MyParticlesManager.TryCreateParticleEffect("CapitalWarpCloud", ref matrix, ref pos, uint.MaxValue, out wormholeCloudParticle))
                {
                    MyParticlesManager.TryCreateParticleEffect("WarpTunnel", ref matrix, ref pos, uint.MaxValue, out wormholeCloudParticle);
                }

                if (wormholeCloudParticle != null)
                {
                    wormholeCloudParticle.UserScale = scale;
                }
            }
            catch (Exception) { }
        }

        private void UpdateExitWormholeFX()
        {
            if (currentWipe == null || MyAPIGateway.Utilities.IsDedicated) return;

            try
            {
                Vector3D pos = currentWipe.ExitWormholePos;
                MatrixD m = MatrixD.CreateWorld(pos, -currentWipe.PlaneNormal, JumpOrientation.Up);

                if (wormholeCloudParticle != null && !wormholeCloudParticle.IsStopped)
                {
                    wormholeCloudParticle.WorldMatrix = m;

                    float cloudAge = (stateElapsedTicks - 60f) / 60f;
                    if (cloudAge >= 0)
                    {
                        float baseScale = MathHelper.Clamp((float)currentWipe.CrossRadius / 12f, 4.0f, 18f);
                        float scaleMultiplier = 1f;

                        if (cloudAge < 2.0f)
                            scaleMultiplier = cloudAge / 2.0f;
                        else if (cloudAge > (JUMP_OUT_SECONDS - 1f - 2.0f))
                            scaleMultiplier = 1f - MathHelper.Clamp((cloudAge - (JUMP_OUT_SECONDS - 1f - 2.0f)) / 2.0f, 0f, 1f);

                        wormholeCloudParticle.UserScale = baseScale * scaleMultiplier;
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>Opens the hyperspace view if it is not running yet (so the one started at the portal carries on into transit).</summary>
        private void StartTransitTunnelFX(bool playLoop = true)
        {
            // Only for the ship the local player is actually aboard
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.mainGrid == null) return;
            if (!IsLocalPlayerOnboard()) return;

            try
            {
                AimTunnel();

                if (!playLoop)
                    return;

                // Looping hyperspace audio — anchor to camera so extreme speeds don't outrun sound
                if (transitSound == null)
                {
                    transitSound = new MyEntity3DSoundEmitter(null)
                    {
                        CanPlayLoopSounds = true
                    };
                }

                Vector3D soundAnchor = (MyAPIGateway.Session?.Camera != null)
                    ? MyAPIGateway.Session.Camera.Position
                    : GridSystem.mainGrid.PositionComp.WorldAABB.Center;

                transitSound.SetPosition(soundAnchor);
                transitSound.SetVelocity(Vector3.Zero);
                transitSound.PlaySound(new MySoundPair("inHyperSpace"), true);
                transitSound.VolumeMultiplier = 1f;
            }
            catch (Exception) { }
        }

        /// <summary>Keeps the tunnel on the ship and pointed along the jump, opening it if it is not running.</summary>
        private void AimTunnel()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null)
                return;

            MatrixD matrix = HyperspaceSystem.TunnelMatrix(mainGrid, JumpDirection, mainGrid.WorldMatrix.Up);
            if (tunnelParticle == null)
                tunnelParticle = HyperspaceSystem.SpawnTunnelParticle(mainGrid, matrix);
            else
                tunnelParticle.WorldMatrix = matrix;
        }

        private void UpdateTransitTunnelFX(bool playTransitLoop = true)
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.mainGrid == null) return;

            if (!IsLocalPlayerOnboard())
            {
                if (transitSound != null && transitSound.IsPlaying)
                {
                    transitSound.StopSound(true);
                }
                StopTransitParticles();
                return;
            }

            try
            {
                AimTunnel();   // opens it again if the pilot came back aboard mid-jump

                if (transitSound != null)
                {
                    Vector3D soundAnchor = (MyAPIGateway.Session?.Camera != null)
                        ? MyAPIGateway.Session.Camera.Position
                        : GridSystem.mainGrid.PositionComp.WorldAABB.Center;

                    transitSound.SetPosition(soundAnchor);
                    transitSound.SetVelocity(Vector3.Zero);

                    if (playTransitLoop && !transitSound.IsPlaying)
                    {
                        transitSound.PlaySound(new MySoundPair("inHyperSpace"), true);
                    }
                }
            }
            catch (Exception) { }
        }

        private void StopWormholeParticles()
        {
            try
            {
                if (wormholeFlashParticle != null)
                {
                    wormholeFlashParticle.Stop();
                    wormholeFlashParticle = null;
                }
                if (wormholeCloudParticle != null)
                {
                    wormholeCloudParticle.Stop();
                    wormholeCloudParticle = null;
                }
            }
            catch (Exception) { }
        }

        private void StopTransitParticles()
        {
            if (tunnelParticle != null)
            {
                tunnelParticle.Stop(false);   // the last streaks fly out
                tunnelParticle = null;
            }
        }

        private void StopParticles()
        {
            StopWormholeParticles();
            StopTransitParticles();
        }

        /// <summary>
        /// Returns true if the local client player is currently on board this grid (seated in cockpit/cryo
        /// or tracked in _playersInTransit). Mirrors HyperspaceSystem.IsLocalPlayerOnboard().
        /// </summary>
        public bool IsLocalPlayerOnboard()
        {
            if (MyAPIGateway.Utilities.IsDedicated) return false;
            var localPlayer = MyAPIGateway.Session?.Player;
            if (localPlayer?.Character == null) return false;

            var seatedBlock = localPlayer.Character.Parent as VRage.Game.ModAPI.IMyCubeBlock;
            if (seatedBlock != null && GridSystem?.gridGroup != null)
            {
                var playerGrid = seatedBlock.CubeGrid;
                foreach (var g in GridSystem.gridGroup)
                {
                    if (g != null && g.EntityId == playerGrid.EntityId)
                        return true;
                }
            }

            if (_playersInTransit.Contains(localPlayer))
                return true;

            return false;
        }

        public static void BroadcastGlobalAudio(Vector3D pos, string soundSubtype, float maxDist)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            try
            {
                if (MyAPIGateway.Session?.Camera != null)
                {
                    double dist = Vector3D.Distance(pos, MyAPIGateway.Session.Camera.Position);
                    if (dist <= maxDist * 1.5)
                    {
                        var soundPair = new MySoundPair(soundSubtype);
                        MyEntity3DSoundEmitter emitter = new MyEntity3DSoundEmitter(null);
                        emitter.SetPosition(pos);
                        emitter.PlaySound(soundPair);
                    }
                }
            }
            catch (Exception) { }
        }

        #endregion
    }
}
