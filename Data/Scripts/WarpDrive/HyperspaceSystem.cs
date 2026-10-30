using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.Models;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using ProtoBuf;

namespace WarpDriveMod
{
    public class HyperspaceSystem : IHyperdictionVictim
    {
        public enum HyperState
        {
            Idle,
            Charging,       // Initial 10s spool up
            HoldingCharge,  // Fully charged, waiting for player to align within 5°
            Countdown,      // Within 5°, 5s countdown with smooth micro-adjust
            Active,         // In hyperspace tunnel (11s - 21s)
            Cooldown,

            // Lowercase aliases
            idle = Idle,
            charging = Charging,
            holdingCharge = HoldingCharge,
            countdown = Countdown,
            active = Active,
            cooldown = Cooldown
        }

        public enum JumpMode
        {
            ManualDistance,
            GpsWaypoint,

            // Lowercase aliases
            manualDistance = ManualDistance,
            gpsWaypoint = GpsWaypoint
        }

        // Timing
        public const float CHARGE_TIME_SECONDS = 10.0f;     // Initial charge time
        public const float COUNTDOWN_SECONDS = 5.0f;        // Alignment countdown timer
        public const float COOLDOWN_SECONDS = 5.0f;         // Cooldown timer after jump
        public const float MIN_JOURNEY_SECONDS = 11.0f;     // Minimum travel time (0% range)
        public const float MAX_JOURNEY_SECONDS = 21.0f;     // Maximum travel time (100% range)
        public const float TRANSIT_ACCEL_SECONDS = 0.5f;    // Departure smooth acceleration time (0.5s)
        public const float TRANSIT_DECEL_SECONDS = 0.5f;    // Arrival smooth deceleration time (0.5s)
        public const float ALIGNMENT_TOLERANCE_DEG = 5.0f;  // Maximum deviation angle allowed to lock alignment
        public const float ALIGNMENT_BREAK_DEG = 6.5f;      // Deviation angle to break countdown back to hold charge

        // Distances (in meters)
        public const double MIN_JUMP_DISTANCE = 500000.0;          // 500 km minimum
        public const double HARD_MAX_JUMP_DISTANCE = 25000000.0;   // 25,000 km hard cap
        // 100 km * 0.1 = 10 km per MW
        public const double RANGE_MULTIPLIER_PER_MW = 100000.0 * 0.1;

        public HyperState state { get { return State; } private set { State = value; } }
        public HyperState State { get; private set; } = HyperState.Idle;

        public JumpMode mode { get { return Mode; } set { Mode = value; } }
        public JumpMode Mode { get; set; } = JumpMode.ManualDistance;

        // Slider value: 0.0f (0%) to 1.0f (100%) of maximum jump range
        public float jumpDistanceRatio { get { return JumpDistanceRatio; } set { JumpDistanceRatio = value; } }
        public float JumpDistanceRatio { get; set; } = 1.0f;

        // GPS Target Data
        public string selectedGpsName { get { return SelectedGpsName; } set { SelectedGpsName = value; } }
        public string SelectedGpsName { get; set; } = string.Empty;
        public Vector3D? selectedGpsCoords { get { return SelectedGpsCoords; } set { SelectedGpsCoords = value; } }
        public Vector3D? SelectedGpsCoords { get; set; } = null;

        // Active Jump Data (Set when jump engages)
        public Vector3D jumpOrigin { get { return JumpOrigin; } private set { JumpOrigin = value; } }
        public Vector3D JumpOrigin { get; private set; }
        public Vector3D jumpDestination { get { return JumpDestination; } private set { JumpDestination = value; } }
        public Vector3D JumpDestination { get; private set; }
        public Vector3D jumpDirection { get { return JumpDirection; } private set { JumpDirection = value; } }
        public Vector3D JumpDirection { get; private set; }
        public MatrixD jumpOrientation { get { return JumpOrientation; } private set { JumpOrientation = value; } }
        public MatrixD JumpOrientation { get; private set; } = MatrixD.Identity;
        public double jumpDistance { get { return JumpDistance; } private set { JumpDistance = value; } }
        public double JumpDistance { get; private set; }
        public float journeyDuration { get { return JourneyDuration; } private set { JourneyDuration = value; } }
        public float JourneyDuration { get; private set; }
        public long JumpStartTick { get; private set; }

        // Grid & Drive references
        public GridSystem gridSystem { get { return GridSystem; } private set { GridSystem = value; } }
        public GridSystem GridSystem { get; private set; }
        public WarpDrive hostDrive { get { return HostDrive; } private set { HostDrive = value; } }
        public WarpDrive HostDrive { get; private set; }

        // State Machine Timers (in ticks, 60 ticks = 1 second)
        private long chargeElapsedTicks = 0;
        private long chargeTotalTicks = (long)(CHARGE_TIME_SECONDS * 60f);
        private long holdElapsedTicks = 0;  // dedicated counter for the HoldingCharge state
        private long countdownElapsedTicks = 0;
        private long countdownTotalTicks = (long)(COUNTDOWN_SECONDS * 60f);
        private long cooldownElapsedTicks = 0;
        private long cooldownTotalTicks = (long)(COOLDOWN_SECONDS * 60f);
        private long triggeringPlayerId = 0L;

        // Transit runtime fields
        private MyEntity3DSoundEmitter sound;
        private MySoundPair currentSound = null;
        private MyParticleEffect chargeParticle;
        private float baseChargeScale = 1.0f;
        private MyParticleEffect tunnelParticle;   // the hyperspace tunnel seen from aboard (local pilot only), Data/HyperspaceTunnel.sbc
        private MyParticleEffect arrivalTrailParticle;
        private long transitElapsedTicks = 0;
        private long transitTotalTicks = 0;
        public double currentTransitSpeed { get { return CurrentTransitSpeed; } private set { CurrentTransitSpeed = value; } }
        public double CurrentTransitSpeed { get; private set; } // Current speed in km/s

        // ------------------------------------------------------------------ read-only state for the HUD

        public long TriggeringPlayerId { get { return triggeringPlayerId; } }
        public float ChargeSecondsLeft { get { return Math.Max(0f, (chargeTotalTicks - chargeElapsedTicks) / 60f); } }
        public float ChargeProgress { get { return chargeTotalTicks > 0 ? MathHelper.Clamp((float)chargeElapsedTicks / chargeTotalTicks, 0f, 1f) : 0f; } }
        public float CountdownSecondsLeft { get { return Math.Max(0f, (countdownTotalTicks - countdownElapsedTicks) / 60f); } }
        public float CountdownProgress { get { return countdownTotalTicks > 0 ? MathHelper.Clamp((float)countdownElapsedTicks / countdownTotalTicks, 0f, 1f) : 0f; } }
        public float CooldownSecondsLeft { get { return Math.Max(0f, (cooldownTotalTicks - cooldownElapsedTicks) / 60f); } }
        public float CooldownProgress { get { return cooldownTotalTicks > 0 ? MathHelper.Clamp((float)cooldownElapsedTicks / cooldownTotalTicks, 0f, 1f) : 0f; } }
        public float TransitSecondsLeft { get { return Math.Max(0f, (transitTotalTicks - transitElapsedTicks) / 60f); } }

        /// <summary>Camera-shake magnitude of the transit right now (1 = steady cruise, higher at entry, exit and under a Hyperdictor). The HUD shakes with the same value.</summary>
        public float TransitShakeMagnitude
        {
            get
            {
                float shake = CalculateCurrentShake(transitElapsedTicks, transitTotalTicks);
                if (Hyperdiction != null)
                    shake = Math.Max(shake, Hyperdiction.ShakeAt(Hyperdiction.ElapsedTicks));
                return shake;
            }
        }

        /// <summary>Share of the jump distance covered so far (follows the departure/arrival easing, so it matches the real position).</summary>
        public float TransitDistanceProgress
        {
            get
            {
                if (transitTotalTicks <= 0)
                    return 0f;

                float progress;
                double speed;
                CalculateTransitProgress(transitElapsedTicks / 60.0, transitTotalTicks / 60.0, JumpDistance, out progress, out speed);
                return progress;
            }
        }

        /// <summary>True while this drive takes part in a fleet jump (from joining the lobby until arrival or abort): the jump then follows the fleet vector.</summary>
        public bool IsFleetJump
        {
            get
            {
                var mainGrid = GridSystem?.mainGrid;
                return (mainGrid != null && FleetJumpSystem.IsGridInFleet(mainGrid.EntityId)) || fleetStaggerTicks > 0 || FleetLeaderGridId != 0L;
            }
        }

        /// <summary>The direction the nose has to point at while holding charge or counting down. The one rule for the hold tick and the HUD.</summary>
        public bool TryGetAlignTarget(out Vector3D dir)
        {
            return TryGetAlignTarget(GridSystem?.FindMainCockpit(), out dir);
        }

        /// <summary>Same, with a cockpit the caller already has (FindMainCockpit walks every block, so the HUD caches it).</summary>
        public bool TryGetAlignTarget(IMyShipController cockpit, out Vector3D dir)
        {
            dir = Vector3D.Zero;
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null || cockpit == null)
                return false;

            bool isFleet = IsFleetJump;
            if (isFleet && JumpOrientation != default(MatrixD))
                dir = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : (Vector3D)JumpOrientation.Forward;
            else if (JumpDirection.LengthSquared() > 0.001)
                dir = JumpDirection;
            else if (JumpDestination != Vector3D.Zero)
                dir = JumpDestination - cockpit.WorldMatrix.Translation;
            else
                return false;

            if (dir.LengthSquared() < 1e-9)
                return false;

            dir.Normalize();
            return true;
        }

        // In-transit dynamic obstacle & fleet clearance checkpoints
        private bool checkedObstacle25 = false;
        private bool checkedObstacle50 = false;
        private bool checkedObstacle90 = false;
        private bool _triggeredArrivalLeadFX = false;

        // Fleet Jump & Stagger runtime fields
        public bool isFleetHolding { get { return IsFleetHolding; } private set { IsFleetHolding = value; } }
        public bool IsFleetHolding { get; private set; } = false;
        private int fleetStaggerTicks = 0;
        public float fleetSynchronizedJourneyDuration { get { return FleetSynchronizedJourneyDuration; } private set { FleetSynchronizedJourneyDuration = value; } }
        public float FleetSynchronizedJourneyDuration { get; private set; } = 0f;
        public long fleetLeaderGridId { get { return FleetLeaderGridId; } set { FleetLeaderGridId = value; } }
        public long FleetLeaderGridId { get; set; } = 0L;

        // Active arrival claims across all jumping ships (prevent simultaneous same-frame collisions)
        public struct HyperspaceArrivalClaim
        {
            public long GridEntityId;
            public long FleetLeaderGridId;
            public Vector3D Destination;
            public double SafetyRadius;
            public Vector3D Direction;
        }

        private static readonly List<HyperspaceArrivalClaim> _activeClaims = new List<HyperspaceArrivalClaim>();

        /// <summary>
        /// Shared lock object for the active arrival claims list.
        /// CapitalFSD and any future FSD types should lock on this when reading claims.
        /// </summary>
        public static readonly object _activeClaims_Lock = _activeClaims;

        /// <summary>
        /// Read-only view of the active arrival claims. Always lock on _activeClaims_Lock before iterating.
        /// </summary>
        public static IReadOnlyList<HyperspaceArrivalClaim> GetActiveClaims()
        {
            return _activeClaims;
        }

        public static void RegisterClaim(long gridId, Vector3D dest, double radius, Vector3D dir, long fleetLeaderId = 0L)
        {
            lock (_activeClaims)
            {
                _activeClaims.RemoveAll(c => c.GridEntityId == gridId);
                _activeClaims.Add(new HyperspaceArrivalClaim
                {
                    GridEntityId = gridId,
                    FleetLeaderGridId = fleetLeaderId,
                    Destination = dest,
                    SafetyRadius = radius,
                    Direction = dir
                });
            }
        }

        public static void UnregisterClaim(long gridId)
        {
            lock (_activeClaims)
            {
                _activeClaims.RemoveAll(c => c.GridEntityId == gridId);
            }
        }

        // Camera shake during hyperspace transit (client-side only, via MyCockpit.AddShake)
        private const float HYPERSPACE_SHAKE_BASE = 1.0f;
        private const float HYPERSPACE_SHAKE_PEAK = 1.5f;

        private float CalculateCurrentShake(long elapsedTicks, long totalTicks)
        {
            if (totalTicks <= 0)
                return HYPERSPACE_SHAKE_BASE;

            float remainingTicks = Math.Max(0, totalTicks - elapsedTicks);

            // 1. Entry phase: starts at 3.0f, smoothly subdues to 1.0f over first 3 seconds (180 ticks)
            const float entryTicks = 180f;
            if (elapsedTicks < entryTicks)
            {
                float factor = 1.0f - (elapsedTicks / entryTicks); // 1.0 -> 0.0
                // Smooth step
                factor = factor * factor * (3f - 2f * factor);
                return MathHelper.Lerp(HYPERSPACE_SHAKE_BASE, HYPERSPACE_SHAKE_PEAK, factor);
            }

            // 2. Pre-exit phase: 5 seconds (300 ticks) before jump-out, smoothly raises from 1.0f to 3.0f
            const float exitWindowTicks = 300f;
            if (remainingTicks <= exitWindowTicks)
            {
                float factor = 1.0f - (remainingTicks / exitWindowTicks); // 0.0 -> 1.0
                // Smooth step
                factor = factor * factor * (3f - 2f * factor);
                return MathHelper.Lerp(HYPERSPACE_SHAKE_BASE, HYPERSPACE_SHAKE_PEAK, factor);
            }

            // 3. Cruise phase: steady 1.0f
            return HYPERSPACE_SHAKE_BASE;
        }


        // Dedicated transit-only sound emitter with NO entity binding.
        // MyEntity3DSoundEmitter(null) makes SetPosition() the sole authority on location,
        // which means it never lags behind when the grid teleports at high hyperspace speeds.
        private MyEntity3DSoundEmitter transitSound;
        private MyEntity3DSoundEmitter jumpOriginSound;
        private MyEntity3DSoundEmitter jumpArrivalSound;

        // Emergency Drop fields
        internal static readonly Dictionary<IMyPowerProducer, long> _globallyDisabledPower = new Dictionary<IMyPowerProducer, long>();
        private readonly List<IMyPowerProducer> tempDisabledPower = new List<IMyPowerProducer>();
        private int emergencyPowerDisableTicks = 0;
        private int fsdLossTicks = 0;
        private int powerLossTicks = 0;
        private int controlLossTicks = 0;

        // Hyperdiction: non-null while a Hyperdictor has pulled this ship into the 9s interdiction sequence
        public HyperdictionSequence Hyperdiction { get; private set; }
        public bool IsHyperdicted { get { return Hyperdiction != null; } }
        private MyEntity3DSoundEmitter hyperdictionSound;
        private bool hyperdictionLeadFxSpawned = false;
        private MatrixD? lastHyperdictionPose;

        public MyCubeGrid HyperdictionGrid { get { return GridSystem?.mainGrid; } }
        public bool CanBeHyperdicted { get { return State == HyperState.Active && Hyperdiction == null && GridSystem?.mainGrid != null && !GridSystem.mainGrid.MarkedForClose; } }

        // Players in transit - Character.Save suppression (borrowed from WarpSystem)
        private readonly List<IMyPlayer> _playersInTransit = new List<IMyPlayer>();

        public HyperspaceSystem(WarpDrive drive, GridSystem gridSystem)
        {
            HostDrive = drive;
            GridSystem = gridSystem;

            WarpDriveSession.instance?.RegisterHyperspace(this);

            if (!MyAPIGateway.Utilities.IsDedicated && gridSystem?.mainGrid != null)
            {
                sound = new MyEntity3DSoundEmitter(gridSystem.mainGrid)
                {
                    CanPlayLoopSounds = true
                };

                // Transit emitter: null entity so SetPosition() is never overridden by entity tracking
                transitSound = new MyEntity3DSoundEmitter(null)
                {
                    CanPlayLoopSounds = true
                };

                // Origin and Arrival emitters: null entity anchored specifically to origin and destination coordinates
                jumpOriginSound = new MyEntity3DSoundEmitter(null)
                {
                    CanPlayLoopSounds = true
                };

                jumpArrivalSound = new MyEntity3DSoundEmitter(null)
                {
                    CanPlayLoopSounds = true
                };
            }
        }

        public void Close()
        {
            HyperspaceSunManager.Instance.ReleaseSuppression(this, true);
            WarpDriveSession.instance?.UnregisterHyperspace(this);
            if (State != HyperState.Idle)
            {
                AbortJump("FSD CLOSED");
            }
            StopSound();
            StopChargeParticles();
            StopTunnelParticle();
            StopArrivalTrailParticle();
        }

        public void SendMessage(string msg, float seconds = 5, string font = "White", long playerId = 0L)
        {
            // The jump state machine runs on every machine in lockstep: each one shows the message only to its own local player
            HudEvents.Post(GridSystem?.mainGrid?.EntityId ?? 0L, playerId, msg, HudEvents.Classify(msg, font), HudRoute.Local, seconds);
        }

        public bool IsPrototechDrive => HostDrive?.isPrototech == true || HostDrive?.warpSystem?.isPrototech == true;

        private struct SoundEventTracker
        {
            public Vector3D Position;
            public long TimestampTicks;
        }

        private static readonly List<SoundEventTracker> _recentOriginEvents = new List<SoundEventTracker>();
        private static readonly List<SoundEventTracker> _recentArrivalEvents = new List<SoundEventTracker>();

        private static float CalculateSpatialFleetVolumeScale(Vector3D pos, List<SoundEventTracker> eventList)
        {
            long now = DateTime.UtcNow.Ticks;
            long windowTicks = TimeSpan.FromSeconds(1.5).Ticks;

            // Prune expired events
            for (int i = eventList.Count - 1; i >= 0; i--)
            {
                if (now - eventList[i].TimestampTicks > windowTicks)
                    eventList.RemoveAt(i);
            }

            // Register current event
            eventList.Add(new SoundEventTracker { Position = pos, TimestampTicks = now });

            // Count events within 3000m radius
            int count = 0;
            for (int i = 0; i < eventList.Count; i++)
            {
                if (Vector3D.DistanceSquared(pos, eventList[i].Position) <= 9000000.0) // 3000m ^ 2
                    count++;
            }

            if (count <= 1) return 1.0f;
            return (float)(1.0 / Math.Sqrt(count));
        }

        private void PlaySound(MySoundPair soundPair)
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.mainGrid == null || soundPair == null) return;
            if (sound == null)
            {
                sound = new MyEntity3DSoundEmitter(GridSystem.mainGrid)
                {
                    CanPlayLoopSounds = true
                };
            }
            sound.CanPlayLoopSounds = true;
            sound.SetPosition(GridSystem.mainGrid.PositionComp.GetPosition());

            if (currentSound == soundPair && sound.IsPlaying)
                return;

            currentSound = soundPair;

            // Fleet normalization: scale acoustic power by 1 / sqrt(N)
            float volumeScale = 1.0f;
            long gridId = GridSystem.mainGrid.EntityId;
            if (FleetJumpSystem.IsGridInFleet(gridId))
            {
                var lobby = FleetJumpSystem.GetLobbyForGrid(gridId);
                int count = lobby?.Members?.Count ?? 1;
                if (count > 1)
                {
                    volumeScale = (float)(1.0 / Math.Sqrt(count));
                }
            }

            sound.VolumeMultiplier = volumeScale;
            sound.PlaySound(soundPair, true);
        }

        private void PlayJumpOriginSound(MySoundPair soundPair)
        {
            if (MyAPIGateway.Utilities.IsDedicated || soundPair == null) return;
            try
            {
                if (jumpOriginSound == null)
                {
                    jumpOriginSound = new MyEntity3DSoundEmitter(null)
                    {
                        CanPlayLoopSounds = true
                    };
                }

                Vector3D originPos = JumpOrigin;
                var localPlayer = MyAPIGateway.Session?.Player;
                if (localPlayer?.Character != null && _playersInTransit.Contains(localPlayer) && MyAPIGateway.Session?.Camera != null)
                {
                    originPos = MyAPIGateway.Session.Camera.Position;
                }

                jumpOriginSound.SetPosition(originPos);
                jumpOriginSound.SetVelocity(Vector3.Zero);
                jumpOriginSound.VolumeMultiplier = CalculateSpatialFleetVolumeScale(originPos, _recentOriginEvents);
                jumpOriginSound.PlaySound(soundPair, true);
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] PlayJumpOriginSound error: " + ex);
            }
        }

        private void PlayJumpArrivalSound(MySoundPair soundPair)
        {
            if (MyAPIGateway.Utilities.IsDedicated || soundPair == null) return;
            try
            {
                if (jumpArrivalSound == null)
                {
                    jumpArrivalSound = new MyEntity3DSoundEmitter(null)
                    {
                        CanPlayLoopSounds = true
                    };
                }

                Vector3D arrivalPos = JumpDestination;
                var localPlayer = MyAPIGateway.Session?.Player;
                if (localPlayer?.Character != null && _playersInTransit.Contains(localPlayer) && MyAPIGateway.Session?.Camera != null)
                {
                    arrivalPos = MyAPIGateway.Session.Camera.Position;
                }

                jumpArrivalSound.SetPosition(arrivalPos);
                jumpArrivalSound.SetVelocity(Vector3.Zero);
                jumpArrivalSound.VolumeMultiplier = CalculateSpatialFleetVolumeScale(arrivalPos, _recentArrivalEvents);
                jumpArrivalSound.PlaySound(soundPair, true);
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] PlayJumpArrivalSound error: " + ex);
            }
        }

        private void InitTransitSound()
        {
            if (transitSound == null)
            {
                transitSound = new MyEntity3DSoundEmitter(null)
                {
                    CanPlayLoopSounds = true
                };
            }
        }

        private void StopSound()
        {
            if (sound != null && sound.IsPlaying)
                sound.StopSound(true);

            if (transitSound != null && transitSound.IsPlaying)
                transitSound.StopSound(true);

            currentSound = null;
        }

        private float cachedPowerMW = 0f;
        private long lastPowerCheckTicks = 0;

        private static float GetProducerMaxPowerOutput(IMyPowerProducer producer)
        {
            if (producer == null) return 0f;
            if (producer.MaxOutput > 0.0001f) return producer.MaxOutput;
            var def = MyDefinitionManager.Static?.GetCubeBlockDefinition(producer.BlockDefinition) as MyPowerProducerDefinition;
            return def != null ? def.MaxPowerOutput : 0f;
        }

        // Gets the total operational power of the ship in MW (reactors, batteries, solar, engines).
        public float GetDrivePowerMW()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if (cachedPowerMW > 0f && (nowTicks - lastPowerCheckTicks < 5000000L) && nowTicks >= lastPowerCheckTicks)
            {
                return cachedPowerMW;
            }

            lastPowerCheckTicks = nowTicks;
            float totalPowerMW = 0f;

            var grids = GridSystem?.gridGroup;
            if (grids != null && grids.Count > 0)
            {
                foreach (var grid in grids)
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    foreach (var block in (grid as MyCubeGrid)?.GetFatBlocks() ?? System.Linq.Enumerable.Empty<MyCubeBlock>())
                    {
                        var producer = block as IMyPowerProducer;
                        if (producer == null || !producer.IsFunctional || !producer.Enabled)
                            continue;

                        var battery = block as Sandbox.ModAPI.Ingame.IMyBatteryBlock;
                        if (battery != null)
                        {
                            if (battery.ChargeMode == Sandbox.ModAPI.Ingame.ChargeMode.Recharge || battery.CurrentStoredPower <= 0.0001f)
                                continue;
                        }

                        float output = GetProducerMaxPowerOutput(producer);
                        totalPowerMW += output;
                    }
                }
            }
            else if (HostDrive?.block?.CubeGrid != null)
            {
                var mainGrid = HostDrive.block.CubeGrid as MyCubeGrid;
                if (mainGrid != null)
                {
                    foreach (var block in mainGrid.GetFatBlocks())
                    {
                        var producer = block as IMyPowerProducer;
                        if (producer == null || !producer.IsFunctional || !producer.Enabled)
                            continue;

                        var battery = block as Sandbox.ModAPI.Ingame.IMyBatteryBlock;
                        if (battery != null)
                        {
                            if (battery.ChargeMode == Sandbox.ModAPI.Ingame.ChargeMode.Recharge || battery.CurrentStoredPower <= 0.0001f)
                                continue;
                        }

                        float output = GetProducerMaxPowerOutput(producer);
                        totalPowerMW += output;
                    }
                }
            }

            if (totalPowerMW <= 0f && HostDrive != null)
            {
                // Do NOT fall back to RequiredPower here — a zero sum means the ship truly has no
                // active power producers, and the transit check must be able to detect that.
                totalPowerMW = 0f;
            }

            cachedPowerMW = totalPowerMW; // may legitimately be 0
            return cachedPowerMW;
        }

        // Calculates the maximum jump distance based on available power in MW, capped at 25,000 km.
        public double GetMaxJumpDistance(float powerInMW)
        {
            double calculatedMax = RANGE_MULTIPLIER_PER_MW * Math.Max(powerInMW, 0f);
            calculatedMax = Math.Min(calculatedMax, GetAffordableDistance());
            return MathHelper.Clamp(calculatedMax, MIN_JUMP_DISTANCE, HARD_MAX_JUMP_DISTANCE);
        }

        // Hyperspace jumps are paid for with the energy stored in the Hyper Capacitors on the ship's grid
        public const double JUMP_MWH_PER_KG_KM = 0.000000002;

        public double GetJumpEnergyMWh(double distanceMeters)
        {
            float mass = HostDrive?.warpSystem?.GetShipMass() ?? 1f;
            return mass * (distanceMeters / 1000.0) * JUMP_MWH_PER_KG_KM;
        }

        public double GetStoredEnergyMWh()
        {
            return HostDrive?.CapStoredMWh ?? 0f;
        }

        /// <summary>Longest jump (meters) the stored capacitor energy pays for. Can be below the minimum jump distance.</summary>
        public double GetAffordableDistance()
        {
            double perMeter = GetJumpEnergyMWh(1.0);
            return perMeter > 0.0 ? GetStoredEnergyMWh() / perMeter : 0.0;
        }

        // Hyperspace capacitor: it never draws more than this share of the ship's power, and a full charge takes between 1 and 5 minutes
        public const double CAP_MAX_POWER_SHARE = 0.5;
        public const double CAP_MIN_CHARGE_MINUTES = 1.0;
        public const double CAP_MAX_CHARGE_MINUTES = 5.0;

        /// <summary>
        /// Most energy (MWh) the capacitor can hold: a full range jump, but no more than what CAP_MAX_POWER_SHARE of the ship's power
        /// delivers in CAP_MAX_CHARGE_MINUTES. Heavy ships with little power therefore get a shorter maximum range instead of a longer wait.
        /// </summary>
        public double GetCapacitorCapacityMWh()
        {
            float shipPower = GetDrivePowerMW();
            if (shipPower <= 0f)
                return 0.0;

            double fullRangeMWh = GetJumpEnergyMWh(Math.Min(RANGE_MULTIPLIER_PER_MW * shipPower, HARD_MAX_JUMP_DISTANCE));
            double chargeableMWh = shipPower * CAP_MAX_POWER_SHARE * (CAP_MAX_CHARGE_MINUTES / 60.0);
            return Math.Min(fullRangeMWh, chargeableMWh);
        }

        /// <summary>Power (MW) drawn while charging an empty capacitor of the given size: as fast as the power share allows, but never faster than CAP_MIN_CHARGE_MINUTES.</summary>
        public double GetCapacitorChargePowerMW(double capacityMWh)
        {
            float shipPower = GetDrivePowerMW();
            if (shipPower <= 0f || capacityMWh <= 0.0)
                return 0.0;

            double minutes = Math.Max(CAP_MIN_CHARGE_MINUTES, capacityMWh / (shipPower * CAP_MAX_POWER_SHARE) * 60.0);
            return capacityMWh / (minutes / 60.0);
        }

        public bool HasEnergyForJump(double distanceMeters)
        {
            return GetStoredEnergyMWh() + 0.0001 >= GetJumpEnergyMWh(distanceMeters);
        }

        // Takes the jump's energy out of the capacitors (server / single player; clients get the new charge by sync)
        private bool ConsumeJumpEnergy(double distanceMeters)
        {
            if (MyAPIGateway.Multiplayer.MultiplayerActive && !MyAPIGateway.Multiplayer.IsServer)
                return true;

            return HostDrive != null && HostDrive.TryDrainCapacitor((float)GetJumpEnergyMWh(distanceMeters));
        }

        public double GetCurrentManualDistance(float powerInMW)
        {
            double maxRange = GetMaxJumpDistance(powerInMW);
            if (maxRange <= MIN_JUMP_DISTANCE)
                return MIN_JUMP_DISTANCE;
            double dist = MIN_JUMP_DISTANCE + ((maxRange - MIN_JUMP_DISTANCE) * JumpDistanceRatio);
            return MathHelper.Clamp(dist, MIN_JUMP_DISTANCE, maxRange);
        }

        /// <summary>
        /// Calculates smooth progress fraction [0.0 to 1.0] and current velocity in m/s
        /// with smooth ramp-up at departure and optional smooth ramp-down before arrival.
        /// </summary>
        public static void CalculateTransitProgress(double elapsedSeconds, double totalSeconds, double totalDistance, out float progress, out double speedMps, double accelSeconds = TRANSIT_ACCEL_SECONDS, double decelSeconds = TRANSIT_DECEL_SECONDS)
        {
            if (totalSeconds <= 0.001 || totalDistance <= 0.0)
            {
                progress = 1.0f;
                speedMps = 0.0;
                return;
            }

            double ta = Math.Min(accelSeconds, totalSeconds * 0.45);
            double td = Math.Max(0.0, Math.Min(decelSeconds, totalSeconds * 0.45));
            double tEff = totalSeconds - (0.5 * (ta + td));
            if (tEff <= 0.001) tEff = totalSeconds;
            double vCruise = totalDistance / tEff;

            double tau = MathHelper.Clamp(elapsedSeconds, 0.0, totalSeconds);

            if (tau <= ta && ta > 0.0001)
            {
                // Departure acceleration phase: smooth sinusoidal ramp up from 0 to vCruise
                double phase = (Math.PI * tau) / ta;
                double dist = 0.5 * (tau - (ta / Math.PI) * Math.Sin(phase)) * vCruise;
                progress = MathHelper.Clamp((float)(dist / totalDistance), 0f, 1f);
                speedMps = 0.5 * (1.0 - Math.Cos(phase)) * vCruise;
            }
            else if (td <= 0.0001 || tau < (totalSeconds - td))
            {
                // Constant cruise phase
                double dist = (0.5 * ta * vCruise) + ((tau - ta) * vCruise);
                progress = MathHelper.Clamp((float)(dist / totalDistance), 0f, 1f);
                speedMps = vCruise;
            }
            else
            {
                // Arrival deceleration phase: smooth sinusoidal ramp down from vCruise to 0
                double tauPrime = tau - (totalSeconds - td);
                double phase = (Math.PI * tauPrime) / td;
                double distCruise = (0.5 * ta * vCruise) + ((totalSeconds - td - ta) * vCruise);
                double distDecel = 0.5 * (tauPrime + (td / Math.PI) * Math.Sin(phase)) * vCruise;
                double dist = distCruise + distDecel;
                progress = MathHelper.Clamp((float)(dist / totalDistance), 0f, 1f);
                speedMps = 0.5 * (1.0 + Math.Cos(phase)) * vCruise;
            }

            if (tau >= totalSeconds)
            {
                progress = 1.0f;
                speedMps = 0.0;
            }
        }

        public float CalculateJourneyTime(double distance, double maxRange)
        {
            if (maxRange <= 0) return MIN_JOURNEY_SECONDS;
            float ratio = MathHelper.Clamp((float)(distance / maxRange), 0f, 1f);
            return MIN_JOURNEY_SECONDS + ((MAX_JOURNEY_SECONDS - MIN_JOURNEY_SECONDS) * ratio);
        }

        public bool TryGetTargetCoordinates(IMyShipController cockpit, float powerInMW, out Vector3D targetCoord, out double totalDistance)
        {
            targetCoord = Vector3D.Zero;
            totalDistance = 0;

            if (cockpit == null)
                return false;

            Vector3D shipPos = cockpit.WorldMatrix.Translation;

            if (Mode == JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
            {
                targetCoord = SelectedGpsCoords.Value;
                totalDistance = Vector3D.Distance(shipPos, targetCoord);
                return totalDistance >= MIN_JUMP_DISTANCE;
            }
            else // ManualDistance
            {
                totalDistance = GetCurrentManualDistance(powerInMW);
                targetCoord = shipPos + (cockpit.WorldMatrix.Forward * totalDistance);
                return true;
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

        // The planets of the world barely ever change, while every jump check of every fleet ship walks all of them: look them up every two seconds
        private static readonly List<MyPlanet> planetCache = new List<MyPlanet>();
        private static readonly List<IMyVoxelBase> voxelScratch = new List<IMyVoxelBase>();
        private static long planetCacheTick = long.MinValue;

        private static List<MyPlanet> GetPlanets()
        {
            long now = WarpDriveSession.instance != null ? WarpDriveSession.instance.runtime : 0L;
            if (planetCacheTick != long.MinValue && now >= planetCacheTick && now - planetCacheTick < 120)
                return planetCache;

            planetCache.Clear();
            voxelScratch.Clear();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxelScratch);
            for (int i = 0; i < voxelScratch.Count; i++)
            {
                var planet = voxelScratch[i] as MyPlanet;
                if (planet != null)
                    planetCache.Add(planet);
            }
            voxelScratch.Clear();
            planetCacheTick = now;
            return planetCache;
        }

        // Planet detection: Ray-Sphere intersection to ensure hyperspace course does not fly through any planets
        public static bool IsTrajectoryBlockedByPlanet(Vector3D start, Vector3D target, out string blockedPlanetName)
        {
            blockedPlanetName = null;
            Vector3D travelVec = target - start;
            double travelDist = travelVec.Length();
            if (travelDist <= 0.001) return false;
            Vector3D travelDir = travelVec / travelDist;

            var planets = GetPlanets();
            for (int p = 0; p < planets.Count; p++)
            {
                var planet = planets[p];
                if (planet == null || planet.MarkedForClose) continue;

                Vector3D planetCenter = planet.PositionComp.GetPosition();
                double safetyRadius = planet.HasAtmosphere ? planet.AtmosphereRadius : planet.MaximumRadius;

                Vector3D toCenter = planetCenter - start;
                double tca = Vector3D.Dot(toCenter, travelDir);

                if (tca < 0 || tca > travelDist) continue;

                double d2 = toCenter.LengthSquared() - (tca * tca);
                if (d2 < safetyRadius * safetyRadius)
                {
                    blockedPlanetName = planet.Generator?.FolderName ?? "Planet";
                    return true;
                }
            }
            return false;
        }

        // Proximity danger check in front of ship during charging (ignores friendly fleet wingmen & subgrids)
        public bool IsProximityDanger(MatrixD gridMatrix, MyCubeGrid warpGrid)
        {
            if (warpGrid == null || warpGrid.Physics == null)
                return false;

            Vector3D forward = gridMatrix.Forward;
            Vector3D gridCenter = warpGrid.PositionComp.WorldAABB.Center;

            // Compute the ship's forward extent (front nose offset) and cross-sectional radius
            BoundingBoxD localAABB = warpGrid.PositionComp.LocalAABB;
            MyOrientedBoundingBoxD shipOBB = new MyOrientedBoundingBoxD(localAABB, warpGrid.WorldMatrix);
            Vector3D[] corners = new Vector3D[8];
            shipOBB.GetCorners(corners, 0);

            double maxForwardDist = 0.0;
            double maxPerpDist = (warpGrid.GridSizeEnum == MyCubeSize.Small) ? 2.5 : 5.0;

            for (int i = 0; i < 8; i++)
            {
                Vector3D offset = corners[i] - gridCenter;
                double fwdProj = Vector3D.Dot(offset, forward);
                if (fwdProj > maxForwardDist)
                    maxForwardDist = fwdProj;

                Vector3D perpOffset = offset - (forward * fwdProj);
                double perpDist = perpOffset.Length();
                if (perpDist > maxPerpDist)
                    maxPerpDist = perpDist;
            }

            double corridorRadius = maxPerpDist + 2.0;
            double corridorLength = (warpGrid.GridSizeEnum == MyCubeSize.Small) ? 400.0 : 500.0;

            // Start strictly outside the ship's nose to prevent self-collision
            Vector3D startPos = gridCenter + (forward * (maxForwardDist + 5.0));
            Vector3D endPos = startPos + (forward * corridorLength);
            RayD flightRay = new RayD(startPos, forward);

            double scanRadius = Math.Max(corridorLength * 0.55, 1500.0);
            BoundingSphereD querySphere = new BoundingSphereD(startPos + (forward * (corridorLength * 0.5)), scanRadius);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref querySphere);
            if (entities == null || entities.Count == 0)
                return false;

            long myGridId = warpGrid.EntityId;
            long myFleetLeader = (FleetLeaderGridId != 0L) ? FleetLeaderGridId : FleetJumpSystem.GetLeaderIdForGrid(myGridId);

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
                            HudEvents.Post(warpGrid.EntityId, 0L, "Can't Start FSD - SafeZone in flight path!", HudSeverity.Warning, HudRoute.Local, 4f);
                            return true;
                        }
                    }
                    continue;
                }

                var foundGrid = ent as IMyCubeGrid;
                if (foundGrid != null)
                {
                    if (WarpDrive.IsGridIgnored(foundGrid, warpGrid, myFleetLeader))
                        continue;

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

                    // 3D Capsule-to-Box Corridor Check:
                    BoundingSphereD obstacleSphere = foundGrid.PositionComp.WorldVolume;
                    Vector3D seg = endPos - startPos;
                    double segLenSq = seg.LengthSquared();
                    double t = (segLenSq > 1e-6) ? MathHelper.Clamp(Vector3D.Dot(obstacleSphere.Center - startPos, seg) / segLenSq, 0.0, 1.0) : 0.0;
                    Vector3D closestPt = startPos + (seg * t);
                    double maxDist = obstacleSphere.Radius + corridorRadius;
                    if (Vector3D.DistanceSquared(obstacleSphere.Center, closestPt) <= maxDist * maxDist)
                    {
                        // Transform closest point on flight line into obstacle's local coordinate space
                        Vector3D localPt = Vector3D.Transform(closestPt, foundGrid.PositionComp.WorldMatrixInvScaled);
                        double localDistSq = ((BoundingBoxD)foundGrid.PositionComp.LocalAABB).DistanceSquared(localPt);
                        if (localDistSq <= corridorRadius * corridorRadius)
                        {
                            string gridName = !string.IsNullOrWhiteSpace(foundGrid.DisplayName) ? foundGrid.DisplayName :
                                              (!string.IsNullOrWhiteSpace(foundGrid.CustomName) ? foundGrid.CustomName : "Ship/Station");
                            HudEvents.Post(warpGrid.EntityId, 0L, $"Can't Start FSD - Obstacle ahead: {gridName}", HudSeverity.Warning, HudRoute.Local, 4f);
                            return true;
                        }
                    }
                }
                else if (ent is MyVoxelBase)
                {
                    var voxel = ent as MyVoxelBase;
                    if (voxel == null) continue;

                    var planet = voxel as MyPlanet ?? (voxel as MyVoxelBase)?.RootVoxel as MyPlanet;
                    if (planet != null)
                    {
                        // Planet terrain collision check using GetIntersectionWithAABB on forward corridor
                        BoundingBoxD planetCorridorBox = new BoundingBoxD(
                            Vector3D.Min(startPos, endPos) - new Vector3D(corridorRadius),
                            Vector3D.Max(startPos, endPos) + new Vector3D(corridorRadius)
                        );

                        if (planet.PositionComp.WorldAABB.Intersects(planetCorridorBox))
                        {
                            if (planet.GetIntersectionWithAABB(ref planetCorridorBox))
                            {
                                HudEvents.Post(warpGrid.EntityId, 0L, "Can't Start FSD - Planet in flight path!", HudSeverity.Warning, HudRoute.Local, 4f);
                                return true;
                            }
                        }
                        continue;
                    }

                    // Asteroids: oriented corridor against the asteroid's box, then a coarse voxel storage read (no mesh raycasts)
                    var corridorBox = new MyOrientedBoundingBoxD((startPos + endPos) / 2, new Vector3D(corridorRadius, corridorRadius, corridorLength / 2), Quaternion.CreateFromRotationMatrix(gridMatrix));
                    var asteroidBox = new MyOrientedBoundingBoxD(new BoundingBoxD(ent.LocalAABB.Min, ent.LocalAABB.Max), ent.WorldMatrix);
                    if (!corridorBox.Intersects(ref asteroidBox))
                        continue;

                    if (!(ent is MyVoxelMap) || WarpDrive.VoxelPathBlocked((IMyVoxelBase)ent, ref corridorBox, new LineD(startPos, endPos), gridMatrix, corridorRadius, corridorRadius))
                    {
                        HudEvents.Post(warpGrid.EntityId, 0L, "Can't Start FSD - Asteroid in flight path!", HudSeverity.Warning, HudRoute.Local, 4f);
                        return true;
                    }
                }
            }

            return false;
        }

        // Adjusts jump destination if any grid, asteroid, or safezone is within 500m of arrival point
        // fleetMargin widens the checked lane to the whole formation: the leader clears the way for every ship of its fleet
        public bool TryAdjustDestinationForObstacles(Vector3D origin, Vector3D dir, double dist, out Vector3D safeDest, out double safeDist, double fleetMargin = 0.0)
        {
            safeDest = origin + (dir * dist);
            safeDist = dist;
            bool adjusted = false;

            double shipRadius = GridSystem?.mainGrid?.PositionComp.WorldAABB.HalfExtents.AbsMax() ?? 50.0;
            double safetyBuffer = 500.0 + shipRadius + fleetMargin;
            double searchRadius = Math.Max(safetyBuffer * 4.0, 5000.0);

            long myGridId = GridSystem?.mainGrid?.EntityId ?? 0L;
            long myFleetLeader = (FleetLeaderGridId != 0L) ? FleetLeaderGridId : FleetJumpSystem.GetLeaderIdForGrid(myGridId);

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
                        if (grid.EntityId == myGridId ||
                            (GridSystem != null && GridSystem.Contains(grid)) ||
                            (GridSystem?.mainGrid != null && MyAPIGateway.GridGroups.HasConnection(GridSystem.mainGrid, grid, GridLinkTypeEnum.Logical)))
                        {
                            continue;
                        }

                        // Ignore friendly fleet members in the same fleet jump formation (active or recently arrived)
                        if (myFleetLeader != 0L && (grid.EntityId == myFleetLeader || FleetJumpSystem.IsSameFleet(myGridId, grid.EntityId, myFleetLeader)))
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

            // Also check VoxelMaps instances (asteroids) directly to guarantee none are missed
            voxelScratch.Clear();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxelScratch);
            foreach (var voxel in voxelScratch)
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
            voxelScratch.Clear();

            foreach (var obstacleBox in obstacleBoxes)
            {
                // A fleet flies in formation: an obstacle beside the leader's lane is in the way of a wingman
                BoundingBoxD box = fleetMargin > 0.0 ? obstacleBox.Inflate(fleetMargin) : obstacleBox;

                // 1. Direct ray intersection with obstacle bounding box in local arrival zone
                double rayBackDist = Math.Max(box.HalfExtents.AbsMax() * 2.0 + 1000.0, 5000.0);
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
                    // 2. Ray misses direct face hit, but destination passes within safetyBuffer of obstacle surface
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

            // Test safezones (local check)
            foreach (var zone in safeZones)
            {
                BoundingSphereD expandedZone = new BoundingSphereD(zone.Center, zone.Radius + safetyBuffer);
                double distToZone = Vector3D.Distance(safeDest, zone.Center);
                if (distToZone < (zone.Radius + safetyBuffer))
                {
                    double rayBackDist = Math.Max(zone.Radius * 2.0 + 1000.0, 5000.0);
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

            // Test active arrival claims (simultaneous in-flight ships from other independent jumps)
            lock (_activeClaims)
            {
                for (int i = 0; i < _activeClaims.Count; i++)
                {
                    var claim = _activeClaims[i];
                    if (myGridId != 0L && claim.GridEntityId == myGridId)
                        continue;

                    // Skip claims belonging to wingmen of the same coordinated fleet jump
                    if (myFleetLeader != 0L && (claim.FleetLeaderGridId == myFleetLeader || FleetJumpSystem.IsSameFleet(myGridId, claim.GridEntityId, myFleetLeader)))
                        continue;

                    double combinedBuffer = safetyBuffer + claim.SafetyRadius;

                    // Check if arrival points overlap
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

            // 4. Planetary safeguard: prevent jumping into or through planets, ensuring ship drops at least 500m from the ground
            var worldPlanets = GetPlanets();
            for (int pi = 0; pi < worldPlanets.Count; pi++)
            {
                var planet = worldPlanets[pi];
                if (planet == null || planet.MarkedForClose) continue;

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

                // Verify destination altitude above terrain surface: ensure at least 500m + shipRadius.
                // The surface never lies beyond MaximumRadius, so a destination further out than that plus the buffer is clear (and the surface lookup is costly)
                if ((safeDest - planetCenter).LengthSquared() >= (maxRadius + safetyBuffer) * (maxRadius + safetyBuffer))
                    continue;

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

        // Checks if any cockpit / ship controller across the grid group has a seated pilot or active remote control
        public bool HasSeatedPilot()
        {
            var grids = GridSystem?.gridGroup;
            if (grids == null || grids.Count == 0) return false;

            foreach (var igrid in grids)
            {
                var grid = igrid as MyCubeGrid;
                if (grid == null) continue;
                foreach (var block in grid.GetFatBlocks())
                {
                    var sc = block as IMyShipController;
                    if (sc != null && sc.IsFunctional)
                    {
                        if (sc.IsUnderControl || sc.Pilot != null)
                            return true;
                        if (sc is IMyRemoteControl)
                            return true;
                    }
                }
            }

            var mainCockpit = GridSystem.FindMainCockpit();
            if (mainCockpit != null && mainCockpit.IsFunctional)
            {
                if (mainCockpit.IsUnderControl || mainCockpit.Pilot != null)
                    return true;
                if (mainCockpit is IMyRemoteControl)
                    return true;
            }

            return false;
        }

        // HasSeatedPilot walks every block of the grid group. The charge, hold and countdown ticks ask every tick, for every ship of a fleet:
        // answer from a sample a few times a second instead (the length varies per ship so a fleet's samples do not all land on one tick)
        private long pilotSampleTick = long.MinValue;
        private bool pilotSample;

        private bool HasSeatedPilotSampled()
        {
            long now = WarpDriveSession.instance != null ? WarpDriveSession.instance.runtime : 0L;
            long id = GridSystem?.mainGrid != null ? GridSystem.mainGrid.EntityId : 0L;
            long ttl = 8 + (id & 0x7FFFFFFF) % 5;

            if (pilotSampleTick != long.MinValue && now >= pilotSampleTick && now - pilotSampleTick < ttl)
                return pilotSample;

            pilotSample = HasSeatedPilot();
            pilotSampleTick = now;
            return pilotSample;
        }

        // Checks if a physical human player is seated in a cockpit or actively remote-controlling this ship
        public bool HasHumanPilot()
        {
            var grids = GridSystem?.gridGroup;
            if (grids == null || grids.Count == 0) return false;

            foreach (var igrid in grids)
            {
                var grid = igrid as MyCubeGrid;
                if (grid == null) continue;
                foreach (var block in grid.GetFatBlocks())
                {
                    var cockpit = block as IMyCockpit;
                    if (cockpit != null && cockpit.Pilot != null)
                        return true;

                    var rc = block as IMyRemoteControl;
                    if (rc != null && rc.IsUnderControl)
                        return true;
                }
            }

            return false;
        }

        // Pre-Flight & Safety Checks
        public bool CanInitiateJump(long playerId = 0L, bool checkAlignment = true)
        {
            var warpSys = HostDrive?.warpSystem;
            if (warpSys == null || GridSystem == null || GridSystem.mainGrid == null)
                return false;

            // 0. Cooldown Check
            if (State == HyperState.Cooldown)
            {
                SendMessage("FSD IS COOLING DOWN - PLEASE WAIT", 4f, "Red", playerId);
                return false;
            }

            // 0.05 Hyperdiction: recently pulled out of hyperspace, or inside a hostile Hyperdictor's field
            string hyperdictionBlock = HyperdictionRules.JumpBlocker(GridSystem.mainGrid, true);
            if (hyperdictionBlock != null)
            {
                SendMessage(hyperdictionBlock, 4f, "Red", playerId);
                return false;
            }

            // 0.1 Supercruise Conflict Check
            if (warpSys.warpState != WarpSystem.State.idle)
            {
                SendMessage(warpSys.warnDriveInUseHUD, 5f, "Red", playerId);
                return false;
            }

            // 1. Health (Borrowed from WarpDrive / WarpSystem)
            if (!HostDrive.block.IsFunctional || !HostDrive.block.IsWorking)
            {
                SendMessage(warpSys.warnDamagedHUD, 5f, "Red", playerId);
                return false;
            }

            // 2. Power (Borrowed from WarpDrive)
            if (!HostDrive.hasPower)
            {
                SendMessage(warpSys.warnPowerHUD, 5f, "Red", playerId);
                return false;
            }

            // 3. Static / Docked Grids (Borrowed from GridSystem & WarpSystem)
            if (GridSystem.isStatic || warpSys.ConnectedStatic(GridSystem.mainGrid))
            {
                SendMessage(warpSys.warnIsStaticHUD, 5f, "Red", playerId);
                return false;
            }

            // 4. Natural Gravity at Origin (Borrowed from WarpSystem)
            if (warpSys.IsInGravity())
            {
                SendMessage(warpSys.warnPlanetIntHUD, 5f, "Red", playerId);
                return false;
            }

            // 5. Obstructions in front of the ship (Asteroids, Safezones, Grids)
            var gridMatrix = GridSystem.FindWorldMatrix();
            if (IsProximityDanger(gridMatrix, GridSystem.mainGrid))
            {
                SendMessage(warpSys.warnProximityHUD, 5f, "Red", playerId);
                return false;
            }

            // 6. Cockpit & Target Coordinate Resolution
            if (!HasSeatedPilot())
            {
                SendMessage("CANNOT JUMP - NO PILOT OR REMOTE CONTROL!", 5f, "Red", playerId);
                return false;
            }

            var cockpit = GridSystem.FindMainCockpit();
            if (cockpit == null)
            {
                SendMessage("No active cockpit or remote control found on grid!", 5f, "Red", playerId);
                return false;
            }

            if (!SelectedGpsCoords.HasValue || string.IsNullOrEmpty(SelectedGpsName))
            {
                var rc = cockpit as IMyRemoteControl;
                if (rc != null && !rc.CurrentWaypoint.IsEmpty() && rc.CurrentWaypoint.Coords != Vector3D.Zero)
                {
                    Mode = JumpMode.GpsWaypoint;
                    SelectedGpsCoords = rc.CurrentWaypoint.Coords;
                    SelectedGpsName = rc.CurrentWaypoint.Name;
                }
                else
                {
                    Mode = JumpMode.ManualDistance;
                }
            }

            float powerMW = GetDrivePowerMW();
            Vector3D targetCoord;
            double totalDist;

            if (!TryGetTargetCoordinates(cockpit, powerMW, out targetCoord, out totalDist))
            {
                SendMessage(Mode == JumpMode.GpsWaypoint ? "No valid GPS waypoint selected!" : "Invalid jump target!", 5f, "Red", playerId);
                return false;
            }

            Vector3D checkOrigin = cockpit.WorldMatrix.Translation;
            Vector3D checkDir = Mode == JumpMode.ManualDistance ? cockpit.WorldMatrix.Forward : Vector3D.Normalize(targetCoord - checkOrigin);
            Vector3D safeCoord;
            double safeDist;
            if (TryAdjustDestinationForObstacles(checkOrigin, checkDir, totalDist, out safeCoord, out safeDist))
            {
                totalDist = safeDist;
                targetCoord = safeCoord;
            }

            // 7. Distance Limits
            double maxRange = GetMaxJumpDistance(powerMW);
            if (totalDist < MIN_JUMP_DISTANCE)
            {
                SendMessage($"Target below minimum jump range ({(MIN_JUMP_DISTANCE / 1000):N0}km)!", 5f, "Red", playerId);
                return false;
            }

            if (totalDist > maxRange)
            {
                SendMessage($"Target exceeds maximum range ({(maxRange / 1000):N0}km)!", 5f, "Red", playerId);
                return false;
            }

            if (!HasEnergyForJump(totalDist))
            {
                SendMessage($"INSUFFICIENT CAPACITOR CHARGE! Need {GetJumpEnergyMWh(totalDist):N1} MWh, stored {GetStoredEnergyMWh():N1} MWh", 5f, "Red", playerId);
                return false;
            }

            // 8. Planet Occlusion along Flight Path
            Vector3D shipPos = cockpit.WorldMatrix.Translation;
            string blockedPlanet;
            if (IsTrajectoryBlockedByPlanet(shipPos, targetCoord, out blockedPlanet))
            {
                SendMessage($"Trajectory obstructed by {blockedPlanet}!", 5f, "Red", playerId);
                return false;
            }

            // 9. Destination inside Natural Gravity
            float dummyGravity;
            Vector3D destGravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(targetCoord, out dummyGravity);
            float allowInGravityMax = (Settings.instance?.AllowInGravityMax ?? 0.2f);
            if ((destGravity.Length() / WarpSystem.EARTH_GRAVITY) > allowInGravityMax)
            {
                SendMessage("Destination inside planetary gravity well!", 5f, "Red", playerId);
                return false;
            }

            // 10. Alignment Check (GPS Mode only when coordinates are valid)
            if (checkAlignment && Mode == JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
            {
                Vector3D targetDir = Vector3D.Normalize(targetCoord - shipPos);
                double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
                double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));

                if (angleDeg > ALIGNMENT_TOLERANCE_DEG)
                {
                    SendMessage($"Align with target! Offset: {angleDeg:F1}° (Max {ALIGNMENT_TOLERANCE_DEG:F1}°)", 3f, "Red", playerId);
                    return false;
                }
            }

            return true;
        }

        // User action: Toggles jump or aborts if already in progress
        public void TriggerJump(long playerId = 0L)
        {
            var mainGrid = GridSystem?.mainGrid;

            // Check if participating in a Fleet Jump: Triggering solo Hyperspace Jump aborts / leaves the fleet jump
            if (mainGrid != null && FleetJumpSystem.IsGridInFleet(mainGrid.EntityId))
            {
                FleetJumpSystem.CancelFleetJump(mainGrid.EntityId, playerId);
                return;
            }

            if (State == HyperState.Idle)
            {
                if (HostDrive != null && !HostDrive.supportsHyperspace)
                {
                    SendMessage("FSD does not support Hyperspace jumps! (Supercruise Only)", 5f, "Red", playerId);
                    return;
                }

                if (HostDrive?.warpSystem != null && HostDrive.warpSystem.warpState != WarpSystem.State.idle)
                {
                    SendMessage(HostDrive.warpSystem.warnDriveInUseHUD, 5f, "Red", playerId);
                    return;
                }

                // If an active friendly fleet jump invitation exists within the fleet sector (5 km), join it!
                if (mainGrid != null)
                {
                    FleetJumpLobby lobby = FleetJumpSystem.GetLobbyForGrid(mainGrid.EntityId);
                    if (lobby != null)
                    {
                        FleetJumpSystem.JoinFleetJump(mainGrid, playerId);
                        return;
                    }
                }

                StartCharging(playerId);
            }
            else if (State == HyperState.Charging || State == HyperState.HoldingCharge || State == HyperState.Countdown)
            {
                AbortJump("FSD SPOOLING DOWN - JUMP CANCELLED");
            }
        }

        // Puts FSD into indefinite fleet holding charging loop
        public void EnterFleetChargingLoop(long playerId)
        {
            if (State != HyperState.Idle) return;

            var cockpit = GridSystem?.FindMainCockpit();
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            // Pre-flight check: allow entering fleet holding loop without requiring immediate heading alignment
            if (!CanInitiateJump(playerId, false)) return;

            triggeringPlayerId = playerId;
            IsFleetHolding = true;
            fleetStaggerTicks = 0;
            cooldownElapsedTicks = 0;
            chargeElapsedTicks = 0;
            holdElapsedTicks = 0;
            countdownElapsedTicks = 0;
            transitElapsedTicks = 0;
            fsdLossTicks = 0;
            powerLossTicks = 0;
            controlLossTicks = 0;
            _triggeredArrivalLeadFX = false;
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;

            AimFleetJump(cockpit, mainGrid);

            State = HyperState.Charging;
            chargeElapsedTicks = 0;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                PlaySound(WarpSound.fsdHyperCharge);
            }

            SendMessage("FLEET FSD CHARGING - HOLDING FORMATION...", 4f, "White", triggeringPlayerId);
        }

        // Works out the fleet jump's origin, direction and distance from the current target (GPS or manual range)
        private void AimFleetJump(IMyShipController cockpit, MyCubeGrid mainGrid)
        {
            MatrixD refMatrix = (cockpit != null) ? cockpit.WorldMatrix : mainGrid.WorldMatrix;
            JumpOrigin = mainGrid.PositionComp.GetPosition();
            float powerMW = GetDrivePowerMW();
            Vector3D targetCoord;
            double totalDist;
            if (cockpit != null && TryGetTargetCoordinates(cockpit, powerMW, out targetCoord, out totalDist))
            {
                JumpDistance = totalDist;
                if (Mode == JumpMode.ManualDistance)
                {
                    JumpDirection = refMatrix.Forward;
                    JumpDestination = JumpOrigin + (JumpDirection * JumpDistance);
                }
                else
                {
                    JumpDestination = targetCoord;
                    JumpDirection = Vector3D.Normalize(JumpDestination - JumpOrigin);
                }
            }
            else
            {
                JumpDirection = refMatrix.Forward;
                if (SelectedGpsCoords.HasValue)
                {
                    JumpDestination = SelectedGpsCoords.Value;
                    JumpDirection = Vector3D.Normalize(JumpDestination - JumpOrigin);
                    JumpDistance = Vector3D.Distance(JumpOrigin, JumpDestination);
                }
                else
                {
                    JumpDistance = GetCurrentManualDistance(powerMW);
                    JumpDestination = JumpOrigin + (JumpDirection * JumpDistance);
                }
            }
        }

        /// <summary>The fleet's target changed while holding: re-aim without restarting the charge.</summary>
        public void RetargetFleetHold()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (!IsFleetHolding || mainGrid == null)
                return;

            AimFleetJump(GridSystem.FindMainCockpit(), mainGrid);
        }

        // Leader confirmed fleet jump -> executes synchronized jump with relative formation & staggered delay
        public void ExecuteFleetMemberJump(int staggerTicks, Vector3D relativeFormationOffset, Vector3D leaderJumpDirection = default(Vector3D), float syncJourneyDuration = 0f, long leaderGridId = 0L, MatrixD leaderDepartureOrientation = default(MatrixD), double leaderJumpDistance = 0.0)
        {
            IsFleetHolding = false;
            fleetStaggerTicks = staggerTicks;
            FleetSynchronizedJourneyDuration = syncJourneyDuration;
            ResetFleetAutoSteerGyros();

            var cockpit = GridSystem?.FindMainCockpit();
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            FleetLeaderGridId = (leaderGridId != 0L) ? leaderGridId : (staggerTicks == 0 ? mainGrid.EntityId : 0L);

            MatrixD depOrientation = (leaderDepartureOrientation != default(MatrixD)) ? leaderDepartureOrientation : mainGrid.WorldMatrix.GetOrientation();
            JumpOrientation = depOrientation;

            // Ensure destination and distance are fully populated
            if (leaderJumpDistance > 1.0)
            {
                JumpDistance = leaderJumpDistance;
            }
            else if (JumpDistance < 1.0)
            {
                float powerMW = GetDrivePowerMW();
                Vector3D targetCoord;
                double totalDist;
                if (cockpit != null && TryGetTargetCoordinates(cockpit, powerMW, out targetCoord, out totalDist))
                {
                    JumpDistance = totalDist;
                }
            }

            JumpOrigin = mainGrid.PositionComp.GetPosition();

            if (leaderJumpDirection.LengthSquared() > 0.001)
            {
                JumpDirection = leaderJumpDirection;
            }
            else if (Mode == JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
            {
                JumpDirection = Vector3D.Normalize(SelectedGpsCoords.Value - JumpOrigin);
            }
            else
            {
                JumpDirection = depOrientation.Forward;
            }

            if (Mode == JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
            {
                JumpDestination = SelectedGpsCoords.Value;
                // Apply relative formation offset using leader's departure orientation to the shared GPS waypoint
                if (relativeFormationOffset != Vector3D.Zero)
                {
                    Vector3D offsetWorld = Vector3D.TransformNormal(relativeFormationOffset, depOrientation);
                    JumpDestination = JumpDestination + offsetWorld;
                }
                JumpDistance = Vector3D.Distance(JumpOrigin, JumpDestination);
            }
            else
            {
                JumpDestination = JumpOrigin + (JumpDirection * JumpDistance);
            }

            // Adjust for static world obstacles (asteroids/safezones) at final arrival spot, skipping fleet members
            ResolveJumpObstacles();

            double shipRadius = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
            RegisterClaim(mainGrid.EntityId, JumpDestination, 500.0 + shipRadius, JumpDirection, FleetLeaderGridId);

            // If drive is still Idle, transition into charging now
            if (State == HyperState.Idle)
            {
                State = HyperState.Charging;
            }

            // If drive is still in the middle of initial charging, let it finish charging first!
            if (chargeElapsedTicks < chargeTotalTicks)
            {
                SendMessage("FLEET JUMP CONFIRMED - COMPLETING CHARGE...", 3f, "White", triggeringPlayerId);
            }
            else
            {
                // Already fully charged -> check alignment or start countdown immediately
                Vector3D targetDir = JumpDirection.LengthSquared() > 0.001 
                    ? JumpDirection 
                    : Vector3D.Normalize(JumpDestination - mainGrid.PositionComp.GetPosition());
                double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
                double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));

                if (angleDeg <= ALIGNMENT_TOLERANCE_DEG)
                    StartCountdown(staggerTicks);
                else
                    EnterHoldingCharge();
            }
        }

        // Starts the 10-second spool up phase
        public void StartCharging(long playerId = 0L)
        {
            if (State != HyperState.Idle) return;

            var cockpit = GridSystem?.FindMainCockpit();
            if (cockpit == null) return;

            if (!SelectedGpsCoords.HasValue || string.IsNullOrEmpty(SelectedGpsName))
            {
                var rc = cockpit as IMyRemoteControl;
                if (rc != null && !rc.CurrentWaypoint.IsEmpty() && rc.CurrentWaypoint.Coords != Vector3D.Zero)
                {
                    Mode = JumpMode.GpsWaypoint;
                    SelectedGpsCoords = rc.CurrentWaypoint.Coords;
                    SelectedGpsName = rc.CurrentWaypoint.Name;
                }
                else
                {
                    Mode = JumpMode.ManualDistance;
                }
            }

            // No alignment needed to start: the drive charges right away and waits on standby (HoldingCharge) until the nose is on target
            if (!CanInitiateJump(playerId, false)) return;

            triggeringPlayerId = playerId;
            cooldownElapsedTicks = 0;
            chargeElapsedTicks = 0;
            holdElapsedTicks = 0;
            countdownElapsedTicks = 0;
            transitElapsedTicks = 0;
            fsdLossTicks = 0;
            powerLossTicks = 0;
            controlLossTicks = 0;
            _triggeredArrivalLeadFX = false;
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;

            // Cache jump coordinates
            var mainGrid = GridSystem?.mainGrid;
            float powerMW = GetDrivePowerMW();
            Vector3D targetCoord;
            double totalDist;
            if (!TryGetTargetCoordinates(cockpit, powerMW, out targetCoord, out totalDist)) return;

            JumpOrigin = mainGrid != null ? mainGrid.PositionComp.GetPosition() : cockpit.WorldMatrix.Translation;
            JumpDistance = totalDist;
            if (Mode == JumpMode.ManualDistance)
            {
                JumpDirection = cockpit.WorldMatrix.Forward;
                JumpDestination = JumpOrigin + (JumpDirection * JumpDistance);
            }
            else
            {
                JumpDestination = targetCoord;
                JumpDirection = Vector3D.Normalize(JumpDestination - JumpOrigin);
            }

            // Apply 500m obstacle safety clearance at arrival
            Vector3D safeDest;
            double safeDist;
            if (TryAdjustDestinationForObstacles(JumpOrigin, JumpDirection, JumpDistance, out safeDest, out safeDist))
            {
                JumpDestination = safeDest;
                JumpDistance = safeDist;
                SendMessage("OBSTRUCTION AT ARRIVAL - 500m CLEARANCE APPLIED", 4f, "White", triggeringPlayerId);
            }

            State = HyperState.Charging;
            chargeElapsedTicks = 0;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                // No particle during the initial 10s charge phase — visuals start at countdown
                PlaySound(WarpSound.fsdHyperCharge);
            }

            SendMessage("FSD CHARGING...", 3f, "White", triggeringPlayerId);
        }

        // Ticked during the 10s charging spool up
        private void UpdateChargingTick()
        {
            var warpSys = HostDrive?.warpSystem;
            var mainGrid = GridSystem?.mainGrid;
            if (warpSys == null || mainGrid == null) return;

            // Hazard abort checks
            if (!HostDrive.block.IsFunctional || !HostDrive.block.IsWorking)
            {
                AbortJump("FSD DAMAGED - CHARGING ABORTED");
                return;
            }
            if (!HostDrive.hasPower)
            {
                AbortJump("POWER FAILURE - CHARGING ABORTED");
                return;
            }
            if (!HasSeatedPilotSampled())
            {
                AbortJump("JUMP ABORTED - CONTROL LOST");
                return;
            }
            if (warpSys.IsInGravity())
            {
                AbortJump("GRAVITY WELL DETECTED - CHARGING ABORTED");
                return;
            }
            if (GridSystem.isStatic || warpSys.ConnectedStatic(mainGrid))
            {
                AbortJump(warpSys.warnIsStaticHUD);
                return;
            }

            // Proximity hazard check (bypassed if participating in Fleet Jump)
            if (!IsFleetHolding && FleetLeaderGridId == 0L && !FleetJumpSystem.IsGridInFleet(mainGrid.EntityId))
            {
                var gridMatrix = GridSystem.FindWorldMatrix();
                if (IsProximityDanger(gridMatrix, mainGrid))
                {
                    AbortJump(warpSys.warnProximityHUD);
                    return;
                }
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                sound?.SetPosition(mainGrid.PositionComp.GetPosition());
                UpdateChargeParticles();
            }

            bool isFleet = (mainGrid != null && FleetJumpSystem.IsGridInFleet(mainGrid.EntityId)) || IsFleetHolding || fleetStaggerTicks > 0 || FleetLeaderGridId != 0L;

            if (Mode == JumpMode.ManualDistance && !isFleet)
            {
                var curCockpit = GridSystem.FindMainCockpit();
                if (curCockpit != null)
                {
                    JumpOrigin = mainGrid.PositionComp.GetPosition();
                    JumpDirection = curCockpit.WorldMatrix.Forward;
                    JumpDestination = JumpOrigin + (JumpDirection * JumpDistance);

                    // Periodically check if manual steering pointed into a planet during charging (every 60 ticks = 1.0s)
                    if (chargeElapsedTicks % 60 == 0 && chargeElapsedTicks > 0)
                    {
                        string blockedPlanet;
                        if (IsTrajectoryBlockedByPlanet(JumpOrigin, JumpDestination, out blockedPlanet))
                        {
                            AbortJump($"TRAJECTORY OBSTRUCTED BY {blockedPlanet.ToUpper()}!");
                            return;
                        }
                    }
                }
            }

            chargeElapsedTicks++;

            // Auto-steer unmanned drones while holding in fleet formation
            if (IsFleetHolding)
            {
                UpdateFleetDroneAutoSteer();
            }

            // Check if 10-second charge completed
            if (chargeElapsedTicks >= chargeTotalTicks)
            {
                if (IsFleetHolding)
                {
                    // Check alignment with fleet target direction
                    var curCockpit = GridSystem.FindMainCockpit();
                    MatrixD refMatrix = (curCockpit != null) ? curCockpit.WorldMatrix : mainGrid.WorldMatrix;

                    Vector3D targetDir;
                    if (JumpOrientation != default(MatrixD))
                    {
                        targetDir = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : JumpOrientation.Forward;
                    }
                    else
                    {
                        var lobby = FleetJumpSystem.GetLobbyForGrid(mainGrid.EntityId);
                        if (lobby != null)
                        {
                            targetDir = (lobby.Mode == JumpMode.GpsWaypoint && lobby.TargetGpsCoords.HasValue)
                                ? Vector3D.Normalize(lobby.TargetGpsCoords.Value - refMatrix.Translation)
                                : lobby.DepartureOrientation.Forward;
                        }
                        else
                        {
                            targetDir = JumpDirection.LengthSquared() > 0.001 
                                ? JumpDirection 
                                : (JumpDestination != Vector3D.Zero ? Vector3D.Normalize(JumpDestination - refMatrix.Translation) : refMatrix.Forward);
                        }
                    }

                    double dot = MathHelper.Clamp(Vector3D.Dot(refMatrix.Forward, targetDir), -1.0, 1.0);
                    double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));

                    // Stay in charging holding loop until Jump Leader confirms execution
                    if (!MyAPIGateway.Utilities.IsDedicated && sound != null && !sound.IsPlaying)
                    {
                        currentSound = null;
                        PlaySound(WarpSound.holdCharge);
                    }
                    return;
                }

                var cockpit = GridSystem.FindMainCockpit();
                if (cockpit == null)
                {
                    AbortJump("COCKPIT LOST");
                    return;
                }

                if (Mode == JumpMode.ManualDistance && !isFleet)
                {
                    StartCountdown(fleetStaggerTicks);
                }
                else // GpsWaypoint or Fleet Jump
                {
                    Vector3D targetDir = (isFleet && JumpOrientation != default(MatrixD))
                        ? (JumpDirection.LengthSquared() > 0.001 ? JumpDirection : JumpOrientation.Forward)
                        : (JumpDirection.LengthSquared() > 0.001 ? JumpDirection : Vector3D.Normalize(JumpDestination - cockpit.WorldMatrix.Translation));
                    double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
                    double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));

                    if (angleDeg <= ALIGNMENT_TOLERANCE_DEG)
                        StartCountdown(fleetStaggerTicks);
                    else
                        EnterHoldingCharge();
                }
            }
        }

        // Transitions into HoldingCharge state when charged but out of alignment
        private void EnterHoldingCharge()
        {
            State = HyperState.HoldingCharge;
            holdElapsedTicks = 0;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                PlaySound(WarpSound.holdCharge);
            }

            SendMessage("ALIGN WITH TARGET | FSD HOLDING CHARGE", 3f, "Red", triggeringPlayerId);
        }

        // Ticked while holding 100% charge waiting for pilot to steer into 5° window
        private void UpdateHoldingChargeTick()
        {
            var warpSys = HostDrive?.warpSystem;
            var mainGrid = GridSystem?.mainGrid;
            if (warpSys == null || mainGrid == null) return;

            // Hazard abort checks
            if (!HostDrive.block.IsFunctional || !HostDrive.block.IsWorking || !HostDrive.hasPower || warpSys.IsInGravity())
            {
                AbortJump("CHARGE COLLAPSED - HAZARD DETECTED");
                return;
            }
            if (!HasSeatedPilotSampled())
            {
                AbortJump("JUMP ABORTED - CONTROL LOST");
                return;
            }

            // Check sector tether if participating in a fleet jump as a wingman
            if (IsFleetHolding)
            {
                var lobby = FleetJumpSystem.GetLobbyForGrid(mainGrid.EntityId);
                if (lobby != null && lobby.LeaderGridId != mainGrid.EntityId)
                {
                    double distToLeader = Vector3D.Distance(mainGrid.PositionComp.GetPosition(), lobby.DepartureOrigin);
                    if (distToLeader > FleetJumpSystem.FLEET_SCAN_RADIUS)
                    {
                        AbortJump("KICKED FROM FLEET JUMP - EXCEEDED " + (FleetJumpSystem.FLEET_SCAN_RADIUS / 1000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "km SECTOR LIMIT");
                        return;
                    }
                }
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                sound?.SetPosition(mainGrid.PositionComp.GetPosition());
                UpdateChargeParticles();

                if (sound != null && !sound.IsPlaying)
                {
                    currentSound = null;
                    PlaySound(WarpSound.holdCharge);
                }
            }

            var cockpit = GridSystem.FindMainCockpit();
            if (cockpit == null)
            {
                AbortJump("COCKPIT LOST");
                return;
            }

            bool isFleet = (mainGrid != null && FleetJumpSystem.IsGridInFleet(mainGrid.EntityId)) || fleetStaggerTicks > 0 || FleetLeaderGridId != 0L;
            Vector3D targetDir = (isFleet && JumpOrientation != default(MatrixD))
                ? (JumpDirection.LengthSquared() > 0.001 ? JumpDirection : JumpOrientation.Forward)
                : (JumpDirection.LengthSquared() > 0.001 ? JumpDirection : Vector3D.Normalize(JumpDestination - cockpit.WorldMatrix.Translation));
            double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
            double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));

            // The offset and the way to turn are shown by the HUD's alignment indicator
            holdElapsedTicks++;

            // Player steered within the 5-degree window
            if (angleDeg <= ALIGNMENT_TOLERANCE_DEG)
            {
                StartCountdown(fleetStaggerTicks);
            }
        }

        // Starts the 5-second alignment countdown
        private void StartCountdown(int extraStaggerTicks = 0)
        {
            State = HyperState.Countdown;
            countdownElapsedTicks = 0;

            countdownTotalTicks = (long)(COUNTDOWN_SECONDS * 60f) + extraStaggerTicks;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                PlaySound(WarpSound.engagingHyperspace);
                // Start Prototech charge particles for the 5-second spool-up phase
                StopChargeParticles();
                StartCountdownParticles();
            }

            if (Mode == JumpMode.ManualDistance)
                SendMessage("FSD ENGAGING IN 5...", 1f, "White", triggeringPlayerId);
            else
                SendMessage("ALIGNMENT LOCKED - ENGAGING IN 5...", 1f, "White", triggeringPlayerId);
        }

        // Ticked during the 5-second countdown with auto-micro adjustment
        private void UpdateCountdownTick()
        {
            var warpSys = HostDrive?.warpSystem;
            var mainGrid = GridSystem?.mainGrid;
            if (warpSys == null || mainGrid == null) return;

            // Hazard abort checks
            if (!HostDrive.block.IsFunctional || !HostDrive.block.IsWorking || !HostDrive.hasPower || warpSys.IsInGravity())
            {
                AbortJump("JUMP ABORTED - HAZARD DETECTED");
                return;
            }
            if (!HasSeatedPilotSampled())
            {
                AbortJump("JUMP ABORTED - CONTROL LOST");
                return;
            }

            var cockpit = GridSystem.FindMainCockpit();
            if (cockpit == null)
            {
                AbortJump("COCKPIT LOST");
                return;
            }

            bool isFleet = (mainGrid != null && FleetJumpSystem.IsGridInFleet(mainGrid.EntityId)) || fleetStaggerTicks > 0 || FleetLeaderGridId != 0L;

            if (Mode == JumpMode.ManualDistance && !isFleet)
            {
                var curCockpit = GridSystem.FindMainCockpit();
                if (curCockpit != null)
                {
                    JumpOrigin = mainGrid.PositionComp.GetPosition();
                    JumpDirection = curCockpit.WorldMatrix.Forward;
                    JumpDestination = JumpOrigin + (JumpDirection * JumpDistance);

                    // Periodically check if manual steering pointed into a planet (every 30 ticks = 0.5s)
                    if (countdownElapsedTicks % 30 == 0)
                    {
                        string blockedPlanet;
                        if (IsTrajectoryBlockedByPlanet(JumpOrigin, JumpDestination, out blockedPlanet))
                        {
                            AbortJump($"TRAJECTORY OBSTRUCTED BY {blockedPlanet.ToUpper()}!");
                            return;
                        }
                    }
                }
            }
            else // GpsWaypoint or Fleet Jump: the nose has to stay on the target
            {
                Vector3D targetDir;

                if (isFleet && JumpOrientation != default(MatrixD))
                {
                    targetDir = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : JumpOrientation.Forward;
                }
                else
                {
                    targetDir = JumpDirection.LengthSquared() > 0.001 
                        ? JumpDirection 
                        : Vector3D.Normalize(JumpDestination - cockpit.WorldMatrix.Translation);
                }

                double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
                double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));

                // If pilot aggressively steers away (> 6.5°), freeze and drop back to HoldingCharge
                if (angleDeg > ALIGNMENT_BREAK_DEG)
                {
                    countdownElapsedTicks = 0;
                    EnterHoldingCharge();
                    return;
                }

                // No per-tick orientation correction here: MyCubeGrid.Teleport ignores rotation, so it never turned the ship,
                // while teleporting every counting-down ship each tick cost ~0.5 ms per ship
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                sound?.SetPosition(mainGrid.PositionComp.GetPosition());
                UpdateChargeParticles();
            }

            countdownElapsedTicks++;

            // Countdown finished: jump into Hyperspace
            if (countdownElapsedTicks >= countdownTotalTicks)
            {
                if (Mode == JumpMode.GpsWaypoint || isFleet)
                {
                    Vector3D targetDir = (isFleet && JumpOrientation != default(MatrixD))
                        ? (JumpDirection.LengthSquared() > 0.001 ? JumpDirection : JumpOrientation.Forward)
                        : (JumpDirection.LengthSquared() > 0.001 ? JumpDirection : Vector3D.Normalize(JumpDestination - cockpit.WorldMatrix.Translation));

                    Vector3D? targetUp = (isFleet && JumpOrientation != default(MatrixD)) ? (Vector3D?)JumpOrientation.Up : null;

                    // Snap final orientation dead center
                    MatrixD finalAligned = MicroAdjustOrientation(mainGrid.WorldMatrix, targetDir, 1.0f, targetUp);
                    if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
                        mainGrid.Teleport(finalAligned);

                    if (isFleet && JumpOrientation != default(MatrixD))
                    {
                        JumpOrientation = finalAligned.GetOrientation();
                    }
                }

                EnterHyperspace();
            }
        }

        private bool abortInProgress;

        // Aborts charging, holding, or countdown and spools down the drive
        public void AbortJump(string reason = "FSD JUMP ABORTED")
        {
            // Leaving the fleet (CancelFleetJump) aborts every holding member, this ship included: abort once, with this reason
            if (abortInProgress)
                return;

            abortInProgress = true;
            try
            {
                AbortJumpOnce(reason);
            }
            finally
            {
                abortInProgress = false;
            }
        }

        private void AbortJumpOnce(string reason)
        {
            Hyperdiction = null;
            var mainGrid = GridSystem?.mainGrid;
            long gridId = mainGrid?.EntityId ?? 0L;

            if (gridId != 0L && FleetJumpSystem.IsGridInFleet(gridId))
            {
                FleetJumpSystem.CancelFleetJump(gridId, triggeringPlayerId);
            }

            State = HyperState.Idle;
            chargeElapsedTicks = 0;
            holdElapsedTicks = 0;
            countdownElapsedTicks = 0;
            IsFleetHolding = false;
            fleetStaggerTicks = 0;
            FleetSynchronizedJourneyDuration = 0f;
            FleetLeaderGridId = 0L;
            _triggeredArrivalLeadFX = false;
            ResetFleetAutoSteerGyros();

            if (mainGrid != null)
            {
                UnregisterClaim(mainGrid.EntityId);
                FleetJumpSystem.UnregisterFleetGrid(mainGrid.EntityId);
            }

            StopChargeParticles();
            StopTunnelParticle();
            StopArrivalTrailParticle();
            StopSound();

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                PlaySound(WarpSound.spoolDown);
            }

            SendMessage(reason, 5f, "Red", triggeringPlayerId);
        }

        // Ticked during the cooldown period after arriving
        private void UpdateCooldownTick()
        {
            cooldownElapsedTicks++;
            if (cooldownElapsedTicks >= cooldownTotalTicks)
            {
                cooldownElapsedTicks = 0;
                State = HyperState.Idle;
                SendMessage("FSD READY", 3f, "White", triggeringPlayerId);
            }
        }

        // Master simulation tick called every frame from WarpDrive / Session
        public void UpdateSimulationTick()
        {
            if (_globallyDisabledPower.Count > 0)
            {
                long now = DateTime.UtcNow.Ticks;
                List<IMyPowerProducer> toRestore = null;
                foreach (var kvp in _globallyDisabledPower)
                {
                    if (now >= kvp.Value)
                    {
                        if (toRestore == null) toRestore = new List<IMyPowerProducer>();
                        toRestore.Add(kvp.Key);
                    }
                }

                if (toRestore != null)
                {
                    for (int i = 0; i < toRestore.Count; i++)
                    {
                        var producer = toRestore[i];
                        _globallyDisabledPower.Remove(producer);
                        if (producer != null && !producer.Closed && producer.IsFunctional)
                        {
                            producer.Enabled = true;
                        }
                    }
                }
            }

            if (emergencyPowerDisableTicks > 0)
            {
                emergencyPowerDisableTicks--;

                if (emergencyPowerDisableTicks == 0)
                {
                    // 5 seconds elapsed! Turn power back on
                    for (int i = 0; i < tempDisabledPower.Count; i++)
                    {
                        var producer = tempDisabledPower[i];
                        _globallyDisabledPower.Remove(producer);
                        if (producer != null && !producer.Closed && producer.IsFunctional)
                        {
                            producer.Enabled = true;
                        }
                    }
                    tempDisabledPower.Clear();

                    // Safety guarantee: re-enable all functional power producers across the grid
                    var allGrids = GridSystem?.gridGroup;
                    if (allGrids != null && allGrids.Count > 0)
                    {
                        foreach (var g in allGrids)
                        {
                            if (g == null || g.MarkedForClose) continue;
                            foreach (var b in (g as MyCubeGrid)?.GetFatBlocks() ?? System.Linq.Enumerable.Empty<MyCubeBlock>())
                            {
                                var p = b as IMyPowerProducer;
                                if (p != null && !p.Closed && p.IsFunctional && !p.Enabled)
                                    p.Enabled = true;
                            }
                        }
                    }
                    else if (GridSystem?.mainGrid != null)
                    {
                        foreach (var b in GridSystem.mainGrid.GetFatBlocks())
                        {
                            var p = b as IMyPowerProducer;
                            if (p != null && !p.Closed && p.IsFunctional && !p.Enabled)
                                p.Enabled = true;
                        }
                    }

                    cachedPowerMW = 0f;
                    SendMessage("POWER RESTORED", 3f, "White", triggeringPlayerId);
                }
            }

            switch (State)
            {
                case HyperState.Charging:
                    UpdateChargingTick();
                    break;
                case HyperState.HoldingCharge:
                    UpdateHoldingChargeTick();
                    break;
                case HyperState.Countdown:
                    UpdateCountdownTick();
                    break;
                case HyperState.Active:
                    UpdateTransitTick();
                    break;
                case HyperState.Cooldown:
                    UpdateCooldownTick();
                    break;
            }
        }

        private static readonly List<IMyCubeGrid> _poseGroupScratch = new List<IMyCubeGrid>();
        private static readonly List<KeyValuePair<MyCubeGrid, MatrixD>> _poseTargetsScratch = new List<KeyValuePair<MyCubeGrid, MatrixD>>();

        /// <summary>
        /// Moves the grid and everything physically attached to it to the given pose, rotation included.
        /// MyCubeGrid.Teleport only applies the translation of the matrix it is given and ignores its orientation,
        /// so the orientation is applied afterwards to each grid of the group, keeping their relative placement.
        /// </summary>
        public static void TeleportWithRotation(MyCubeGrid mainGrid, MatrixD pose)
        {
            MatrixD before = mainGrid.WorldMatrix;

            _poseGroupScratch.Clear();
            _poseTargetsScratch.Clear();
            MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Physical, _poseGroupScratch);
            if (!_poseGroupScratch.Contains(mainGrid))
                _poseGroupScratch.Add(mainGrid);

            MatrixD inverse = MatrixD.Invert(before);
            for (int i = 0; i < _poseGroupScratch.Count; i++)
            {
                var g = _poseGroupScratch[i] as MyCubeGrid;
                if (g == null || g.MarkedForClose) continue;
                _poseTargetsScratch.Add(new KeyValuePair<MyCubeGrid, MatrixD>(g, g.WorldMatrix * inverse * pose));
            }

            mainGrid.Teleport(pose);

            for (int i = 0; i < _poseTargetsScratch.Count; i++)
            {
                var g = _poseTargetsScratch[i].Key;
                if (g.MarkedForClose) continue;
                g.WorldMatrix = _poseTargetsScratch[i].Value;
            }

            _poseGroupScratch.Clear();
            _poseTargetsScratch.Clear();
        }

        /// <summary>Longest jump this ship's FSD can make with the power it has right now.</summary>
        public double MaxRangeNow { get { return Math.Min(RANGE_MULTIPLIER_PER_MW * GetDrivePowerMW(), Math.Min(GetAffordableDistance(), HARD_MAX_JUMP_DISTANCE)); } }

        // ------------------------------------------------------------------ Hyperdiction (this ship is the victim)

        private static bool IsAuthorityMachine { get { return NetAuthority.IsAuthority; } }

        /// <summary>
        /// A Hyperdictor caught this ship. Starts the deterministic 9s sequence (runs on the server and on every client).
        /// </summary>
        public bool BeginHyperdiction(long hyperdictorBlockId, int seed, Vector3D dropPos)
        {
            var mainGrid = GridSystem?.mainGrid;
            if (!CanBeHyperdicted || mainGrid == null)
                return false;

            Vector3D localCenter = mainGrid.PositionComp.LocalAABB.Center;
            Vector3D catchCenter = Vector3D.Transform(localCenter, mainGrid.WorldMatrix);

            Hyperdiction = new HyperdictionSequence(mainGrid.EntityId, hyperdictorBlockId, seed, false, HyperdictionSequence.MassOf(mainGrid),
                catchCenter, dropPos, JumpOrientation, JumpDirection, localCenter);
            hyperdictionLeadFxSpawned = false;
            lastHyperdictionPose = null;

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

            SendMessage("FRAME SHIFT ANOMALY - HYPERDICTION DETECTED", 6f, "Red", triggeringPlayerId);
            return true;
        }

        /// <summary>
        /// The Hyperdictor failed mid-sequence: aim a fresh transit leg from the current position to the original destination
        /// and let the sequence's rotation offsets settle back to neutral over 2 seconds.
        /// </summary>
        public void AbortHyperdiction()
        {
            var seq = Hyperdiction;
            var mainGrid = GridSystem?.mainGrid;
            if (seq == null || seq.IsRecovering || mainGrid == null)
                return;

            long remainingTicks = Math.Max(0L, transitTotalTicks - transitElapsedTicks);

            Vector3D currentPos = mainGrid.PositionComp.GetPosition();
            Vector3D toDest = JumpDestination - currentPos;
            double dist = toDest.Length();

            if (dist > 1.0)
            {
                Vector3D newDir = toDest / dist;
                Vector3D oldDir = JumpDirection;

                // Re-aim the locked orientation along with the new heading
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

            transitElapsedTicks = 0;
            transitTotalTicks = Math.Max(remainingTicks, 180L);
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;
            _triggeredArrivalLeadFX = false;

            seq.RecoverStartTick = seq.ElapsedTicks;

            if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                hyperdictionSound.StopSound(false);

            SendMessage("HYPERDICTION FIELD COLLAPSED - RESUMING TRANSIT", 4f, "White", triggeringPlayerId);
        }

        /// <summary>The 9s sequence ended: kick the ship into realspace next to the Hyperdictor.</summary>
        public void HyperdictionDrop()
        {
            if (Hyperdiction == null)
                return;

            HyperdictionRules.StartLockout(GridSystem?.mainGrid);
            EmergencyDrop("FSD FAILURE - HYPERDICTION CONDUIT COLLAPSE", HyperdictionRules.AFTERMATH_SECONDS);
            cooldownTotalTicks = (long)(HyperdictionRules.AFTERMATH_SECONDS * 60.0);   // the FSD stays down as long as the power
        }

        private Vector3D GetListenerAnchor(Vector3D fallback)
        {
            return MyAPIGateway.Session?.Camera != null ? MyAPIGateway.Session.Camera.Position : fallback;
        }

        private void ZeroTransitVelocities(MyCubeGrid mainGrid)
        {
            var connectedGrids = GridSystem?.gridGroup;
            if (connectedGrids != null)
            {
                foreach (var g in connectedGrids)
                {
                    ZeroVelocity(g?.Physics as VRage.Game.Components.MyPhysicsComponentBase);
                }
            }
            else
            {
                ZeroVelocity(mainGrid.Physics);
            }
        }

        // Writing a velocity wakes the body and touches the physics cluster: only do it when something is actually moving
        private static void ZeroVelocity(VRage.Game.Components.MyPhysicsComponentBase physics)
        {
            if (physics == null)
                return;
            if (physics.LinearVelocity != Vector3.Zero)
                physics.LinearVelocity = Vector3.Zero;
            if (physics.AngularVelocity != Vector3.Zero)
                physics.AngularVelocity = Vector3.Zero;
        }

        // Ticked instead of the normal transit logic while the Hyperdictor holds this ship (not while recovering)
        private void UpdateHyperdictionTick(MyCubeGrid mainGrid)
        {
            var seq = Hyperdiction;
            seq.ElapsedTicks++;
            int tick = seq.ElapsedTicks;

            Vector3D center;
            MatrixD pose = seq.EvaluatePose(tick, out center);

            // Diagnostic: the grid as it stands now (before this tick's Teleport) against the pose commanded last tick.
            // Error near 0 = the rotation sticks; error near the commanded offset = something undoes it.
            if (tick % 15 == 0 && IsAuthorityMachine && lastHyperdictionPose.HasValue)
            {
                MatrixD last = lastHyperdictionPose.Value;
                MatrixD actual = mainGrid.WorldMatrix;
                double fwdErr = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(actual.Forward, last.Forward), -1.0, 1.0)));
                double upErr = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(actual.Up, last.Up), -1.0, 1.0)));
                double cmdTilt = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(last.Forward, seq.CatchDirection), -1.0, 1.0)));
                double cmdRoll = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(last.Up, seq.CatchOrientation.Up), -1.0, 1.0)));
                MyLog.Default.WriteLine(string.Format("[Hyperdiction] tick {0}: commanded heading offset {1:F1} deg, up offset {2:F1} deg; grid error vs last command: forward {3:F2} deg, up {4:F2} deg, position {5:F1} m",
                    tick, cmdTilt, cmdRoll, fwdErr, upErr, Vector3D.Distance(actual.Translation, last.Translation)));
            }

            if (IsAuthorityMachine)
            {
                TeleportWithRotation(mainGrid, pose);
                lastHyperdictionPose = pose;
            }

            ZeroTransitVelocities(mainGrid);

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                {
                    hyperdictionSound.SetPosition(GetListenerAnchor(center));
                    hyperdictionSound.SetVelocity(Vector3.Zero);
                }

                // Half a second before the drop, open the exit flash at the drop point
                if (!hyperdictionLeadFxSpawned && tick >= HyperdictionSequence.TOTAL_TICKS - 30)
                {
                    hyperdictionLeadFxSpawned = true;
                    if (MyAPIGateway.Session?.Camera != null && Vector3D.DistanceSquared(seq.DropPos, MyAPIGateway.Session.Camera.Position) < 50000.0 * 50000.0)
                    {
                        var iGrid = mainGrid as IMyCubeGrid;
                        float gridWidth = iGrid.LocalAABB.Width > iGrid.LocalAABB.Height ? iGrid.LocalAABB.Width : iGrid.LocalAABB.Height;
                        float scale = (mainGrid.GridSizeEnum == MyCubeSize.Large)
                            ? MathHelper.Clamp(gridWidth / 15f, 1.5f, 5.0f)
                            : MathHelper.Clamp(gridWidth / 8f, 0.8f, 2.5f);
                        SpawnGlobalArrivalLeadFX(seq.DropPos, seq.CatchDirection, scale, 20000f, IsPrototechDrive);
                    }
                }
            }

            UpdateTransitClientFX(center, seq.ShakeAt(tick), false);

            if (tick >= HyperdictionSequence.TOTAL_TICKS)
            {
                if (IsAuthorityMachine)
                    HyperdictionSession.Instance?.OnSequenceComplete(this);
                else if (tick >= HyperdictionSequence.TOTAL_TICKS + 120)
                    HyperdictionDrop(); // the Drop message never arrived; leave the tunnel anyway
            }
        }



        // Emergency drop out of Hyperspace transit when power or FSD drive is cut
        public void EmergencyDrop(string reason = "EMERGENCY DROP - CONDUIT COLLAPSED", double blackoutSeconds = 5.0)
        {
            HyperspaceSunManager.Instance.ReleaseSuppression(this, true);
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            State = HyperState.Cooldown;
            cooldownElapsedTicks = 0;
            cooldownTotalTicks = (long)(COOLDOWN_SECONDS * 60f);
            transitElapsedTicks = 0;
            chargeElapsedTicks = 0;
            holdElapsedTicks = 0;
            countdownElapsedTicks = 0;
            fsdLossTicks = 0;
            powerLossTicks = 0;
            controlLossTicks = 0;
            IsFleetHolding = false;
            fleetStaggerTicks = 0;
            FleetSynchronizedJourneyDuration = 0f;
            _triggeredArrivalLeadFX = false;
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;

            Hyperdiction = null;
            hyperdictionLeadFxSpawned = false;
            if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                hyperdictionSound.StopSound(false);

            UnregisterClaim(mainGrid.EntityId);
            FleetJumpSystem.UnregisterFleetGrid(mainGrid.EntityId);
            FleetLeaderGridId = 0L;

            // Restore Character.Save on emergency drop
            SetPlayersSave(true);
            _playersInTransit.Clear();

            // 1. Audio & Visual teardown
            StopChargeParticles();
            StopTunnelParticle();
            StopArrivalTrailParticle();
            StopSound();

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                // Spawn the exit flash where the ship actually is, not at the (unreached) jump destination
                SpawnJumpArrivalFX(mainGrid.PositionComp.WorldAABB.Center);
                PlaySound(WarpSound.emergencyDropSound);
            }

            // 2. Forcibly disable all power producers on the ship (5s for ordinary emergency drops, 6s after a hyperdiction)
            long restoreTimeTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(blackoutSeconds).Ticks;
            var grids = GridSystem?.gridGroup;
            if (grids != null && grids.Count > 0)
            {
                foreach (var g in grids)
                {
                    if (g == null || g.MarkedForClose) continue;
                    foreach (var block in (g as MyCubeGrid)?.GetFatBlocks() ?? System.Linq.Enumerable.Empty<MyCubeBlock>())
                    {
                        var producer = block as IMyPowerProducer;
                        if (producer != null && producer.IsFunctional && producer.Enabled)
                        {
                            producer.Enabled = false;
                            if (!tempDisabledPower.Contains(producer))
                                tempDisabledPower.Add(producer);
                            _globallyDisabledPower[producer] = restoreTimeTicks;
                        }
                    }
                }
            }
            else if (mainGrid != null)
            {
                foreach (var block in mainGrid.GetFatBlocks())
                {
                    var producer = block as IMyPowerProducer;
                    if (producer != null && producer.IsFunctional && producer.Enabled)
                    {
                        producer.Enabled = false;
                        if (!tempDisabledPower.Contains(producer))
                            tempDisabledPower.Add(producer);
                        _globallyDisabledPower[producer] = restoreTimeTicks;
                    }
                }
            }
            emergencyPowerDisableTicks = (int)(blackoutSeconds * 60.0);

            // Guarantee 500m clearance above ground on emergency drop
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
                        mainGrid.Teleport(safeMatrix);
                    }
                }
            }

            // 3. Violently yeet the ship and apply uncontrolled spin
            if (mainGrid.Physics != null)
            {
                // Violent tumble on all 3 axes (2.5 to 5.0 rad/s = ~140 to 285 deg/sec)
                float spinPitch = MyUtils.GetRandomSign() * MyUtils.GetRandomFloat(2.5f, 5.0f);
                float spinYaw = MyUtils.GetRandomSign() * MyUtils.GetRandomFloat(2.5f, 5.0f);
                float spinRoll = MyUtils.GetRandomSign() * MyUtils.GetRandomFloat(3.5f, 6.0f);
                mainGrid.Physics.AngularVelocity = new Vector3(spinPitch, spinYaw, spinRoll);

                // Violent linear yeet: skew forward vector and launch at 100 m/s
                Vector3D forward = mainGrid.WorldMatrix.Forward;
                Vector3D right = mainGrid.WorldMatrix.Right;
                Vector3D up = mainGrid.WorldMatrix.Up;
                Vector3D yeetDir = (forward * 0.8) + (right * MyUtils.GetRandomFloat(-0.6f, 0.6f)) + (up * MyUtils.GetRandomFloat(-0.6f, 0.6f));
                yeetDir.Normalize();

                mainGrid.Physics.LinearVelocity = (Vector3)(yeetDir * 100.0f);
            }

            SendMessage(reason, 5f, "Red", triggeringPlayerId);
        }

        // Initiates the jump into Hyperspace transit once countdown hits zero
        public void EnterHyperspace()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            bool isFleet = (mainGrid != null && FleetJumpSystem.IsGridInFleet(mainGrid.EntityId)) || fleetStaggerTicks > 0 || FleetLeaderGridId != 0L;

            if (Mode == JumpMode.ManualDistance && !isFleet)
            {
                var curCockpit = GridSystem.FindMainCockpit();
                if (curCockpit != null)
                {
                    JumpOrigin = mainGrid.PositionComp.GetPosition();
                    JumpDirection = curCockpit.WorldMatrix.Forward;
                    JumpDestination = JumpOrigin + (JumpDirection * JumpDistance);
                }
            }

            // Final obstacle clearance check right before transit
            ResolveJumpObstacles();

            // Final check: Trajectory obstructed by a planet
            string blockedPlanet;
            if (IsTrajectoryBlockedByPlanet(JumpOrigin, JumpDestination, out blockedPlanet))
            {
                AbortJump($"TRAJECTORY OBSTRUCTED BY {blockedPlanet.ToUpper()}!");
                return;
            }

            // Final check: Destination inside planetary gravity well
            float dummyGravity;
            Vector3D destGravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(JumpDestination, out dummyGravity);
            float allowInGravityMax = (Settings.instance?.AllowInGravityMax ?? 0.2f);
            if ((destGravity.Length() / WarpSystem.EARTH_GRAVITY) > allowInGravityMax)
            {
                AbortJump("DESTINATION INSIDE PLANETARY GRAVITY WELL!");
                return;
            }

            if (!ConsumeJumpEnergy(JumpDistance))
            {
                AbortJump("INSUFFICIENT CAPACITOR CHARGE!");
                return;
            }

            State = HyperState.Active;
            transitElapsedTicks = 0;
            fsdLossTicks = 0;
            powerLossTicks = 0;
            controlLossTicks = 0;

            if (!isFleet || JumpOrientation == default(MatrixD))
            {
                JumpOrientation = mainGrid.WorldMatrix.GetOrientation();
            }

            // 1. Calculate strict travel duration and total ticks
            //    Guard against zero power at this exact frame — if the ship somehow lost all power
            //    between CanInitiateJump and here, abort rather than doing a silent 100km minimum hop.
            float powerMW = GetDrivePowerMW();
            if (powerMW <= 0.001f)
            {
                State = HyperState.Idle;
                EmergencyDrop("POWER FAILURE - JUMP ABORTED AT ENTRY");
                return;
            }
            double maxRange = GetMaxJumpDistance(powerMW);
            if (FleetSynchronizedJourneyDuration > 0f)
                JourneyDuration = FleetSynchronizedJourneyDuration;
            else
                JourneyDuration = CalculateJourneyTime(JumpDistance, maxRange);
            transitTotalTicks = (long)(JourneyDuration * 60f);
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;

            double shipRadius = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
            RegisterClaim(mainGrid.EntityId, JumpDestination, 500.0 + shipRadius, JumpDirection, FleetLeaderGridId);

            // 2. Physics cleanup (prevent Havok physics explosion at hyper speed)
            if (mainGrid.Physics != null)
                mainGrid.Physics.ClearSpeed();

            // 3a. Suppress encounter/cargo-ship spawns during transit (borrowed from WarpSystem)
            CollectPlayersOnGrid();
            SetPlayersSave(false);

            // 3. Audio & Visuals — stop countdown Prototech particles, open the hyperspace view and spawn the origin Jump FX
            StopChargeParticles();
            StartTunnelParticle();

            var iGrid = mainGrid as IMyCubeGrid;
            float gridWidth = iGrid != null && iGrid.LocalAABB.Width > iGrid.LocalAABB.Height ? iGrid.LocalAABB.Width : (iGrid != null ? iGrid.LocalAABB.Height : 10f);
            float scale = (mainGrid.GridSizeEnum == MyCubeSize.Large)
                ? MathHelper.Clamp(gridWidth / 15f, 1.5f, 5.0f)
                : MathHelper.Clamp(gridWidth / 8f, 0.8f, 2.5f);
            double avgSpeedMps = JumpDistance / Math.Max(0.5, (double)JourneyDuration);
            _triggeredArrivalLeadFX = false;

            if (MyAPIGateway.Multiplayer.IsServer && !MyAPIGateway.Utilities.IsDedicated)
            {
                SpawnJumpOriginFX(scale, (float)avgSpeedMps);
                PlayJumpOriginSound(IsPrototechDrive ? WarpSound.hyperspaceJump : WarpSound.hyperspaceJump);
            }

            if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
            {
                var fxMsg = new HyperspaceFXMessage
                {
                    position = JumpOrigin != Vector3D.Zero ? JumpOrigin : mainGrid.WorldMatrix.Translation,
                    direction = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : mainGrid.WorldMatrix.Forward,
                    scale = scale,
                    speed = (float)avgSpeedMps,
                    isArrival = false,
                    isArrivalLead = false,
                    isPrototech = IsPrototechDrive
                };
                byte[] fxData = MyAPIGateway.Utilities.SerializeToBinary(fxMsg);
                MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.hyperspaceFXPacket, fxData);
            }
        }



        // Ticked every frame while State == HyperState.Active. Smoothly micro-teleports the grid along the conduit
        public void UpdateTransitTick()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null || State != HyperState.Active) return;

            // Held by a Hyperdictor: the deterministic 9s sequence replaces the normal transit logic
            if (Hyperdiction != null && !Hyperdiction.IsRecovering)
            {
                UpdateHyperdictionTick(mainGrid);
                return;
            }

            // --- Power & FSD loss detection with debounced grace period ---
            bool fsdIssue = HostDrive == null || HostDrive.block == null
                || !HostDrive.block.Enabled
                || !HostDrive.block.IsFunctional;

            if (fsdIssue)
                fsdLossTicks++;
            else
                fsdLossTicks = 0;

            // Every drive in a fleet starts on the same tick: spread the periodic scans over ten ticks by grid id so they don't all land on one
            bool scanTick = (transitElapsedTicks + (mainGrid.EntityId % 10)) % 10 == 0;

            long perf = FsdPerf.Begin();
            if (scanTick)
            {
                float activePowerMW = 0f;
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
                            float pOut = GetProducerMaxPowerOutput(producer);
                            if (pOut > 0.0001f)
                                activePowerMW += pOut;
                        }
                    }
                }
                else if (mainGrid != null)
                {
                    foreach (var block in mainGrid.GetFatBlocks())
                    {
                        var producer = block as IMyPowerProducer;
                        if (producer == null || !producer.IsFunctional || !producer.Enabled) continue;
                        var battery = block as Sandbox.ModAPI.Ingame.IMyBatteryBlock;
                        if (battery != null && (battery.ChargeMode == Sandbox.ModAPI.Ingame.ChargeMode.Recharge || battery.CurrentStoredPower <= 0.0001f))
                            continue;
                        float pOut = GetProducerMaxPowerOutput(producer);
                        if (pOut > 0.0001f)
                            activePowerMW += pOut;
                    }
                }
                cachedPowerMW = activePowerMW;

                if (cachedPowerMW <= 0.001f)
                    powerLossTicks += 10;
                else
                    powerLossTicks = 0;
            }
            FsdPerf.End(FsdPerf.Section.TransitPower, perf);

            // Require 30 consecutive ticks (0.5s) of sustained loss before triggering emergency drop
            if (fsdLossTicks >= 30 || powerLossTicks >= 30)
            {
                EmergencyDrop(fsdLossTicks >= 30 ? "FSD OFFLINE - EMERGENCY DROP!" : "POWER FAILURE - EMERGENCY DROP!");
                return;
            }

            transitElapsedTicks++;

            double elapsedSec = (double)transitElapsedTicks / 60.0;
            double totalSec = (double)transitTotalTicks / 60.0;

            float t;
            double speedMps;
            CalculateTransitProgress(elapsedSec, totalSec, JumpDistance, out t, out speedMps);

            // Multi-phase in-transit obstacle and fleet clearance checks (at 25%, 50%, and 90% travel progress)
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

            // Interpolate current position with smooth 0.5s departure accel & 0.5s arrival decel
            Vector3D currentPos = JumpOrigin + (JumpDirection * (JumpDistance * t));

            // Planetary exclusion zone & gravity check during transit (checked every 10 ticks = ~0.16s, ultra-light pure math)
            perf = FsdPerf.Begin();
            if (scanTick)
            {
                float localGrav;
                Vector3D naturalGrav = MyAPIGateway.Physics.CalculateNaturalGravityAt(currentPos, out localGrav);
                float allowInGravityMax = (Settings.instance?.AllowInGravityMax ?? 0.2f);
                if ((naturalGrav.Length() / WarpSystem.EARTH_GRAVITY) > allowInGravityMax)
                {
                    EmergencyDrop("GRAVITY WELL EXCLUSION ZONE - EMERGENCY DROP!");
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
                        EmergencyDrop("TERRAIN PROXIMITY - EMERGENCY DROP 500m FROM GROUND!");
                        return;
                    }
                }
            }
            FsdPerf.End(FsdPerf.Section.TransitPlanet, perf);

            // Calculate current instantaneous speed in km/s (reflects 0.5s accel and decel curves)
            CurrentTransitSpeed = speedMps / 1000.0;

            // Apply micro-teleportation while strictly locking grid orientation every frame
            MatrixD currentMatrix = JumpOrientation;
            currentMatrix.Translation = currentPos;

            // Hyperdictor failed: the rotation offsets of the sequence settle back to neutral while the ship resumes transit
            float shakeMagnitude = CalculateCurrentShake(transitElapsedTicks, transitTotalTicks);
            if (Hyperdiction != null)
            {
                Hyperdiction.ElapsedTicks++;
                currentMatrix = Hyperdiction.ApplyOffsets(currentMatrix, Hyperdiction.ElapsedTicks);
                shakeMagnitude = Math.Max(shakeMagnitude, Hyperdiction.ShakeAt(Hyperdiction.ElapsedTicks));
                if (Hyperdiction.RecoveryComplete)
                    Hyperdiction = null;
            }

            perf = FsdPerf.Begin();
            if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
            {
                if (Hyperdiction != null)
                    TeleportWithRotation(mainGrid, currentMatrix);
                else
                    mainGrid.Teleport(currentMatrix);
            }
            FsdPerf.End(FsdPerf.Section.TransitMove, perf);

            // Strictly disallow any rotation or linear physics movement during transit every tick
            perf = FsdPerf.Begin();
            ZeroTransitVelocities(mainGrid);
            FsdPerf.End(FsdPerf.Section.TransitZero, perf);

            // Client-side effects
            perf = FsdPerf.Begin();
            UpdateTransitClientFX(currentPos, shakeMagnitude, true);
            FsdPerf.End(FsdPerf.Section.TransitFx, perf);

            // Trigger pre-arrival lead FX (~0.5s before exit, calculated from jump speed and 2km distance)
            double avgTransitSpeedMps = JumpDistance / Math.Max(0.5, (double)JourneyDuration);
            long leadTicks = Math.Min(30L, Math.Max(10L, transitTotalTicks / 4));
            if (transitElapsedTicks >= (transitTotalTicks - leadTicks) && !_triggeredArrivalLeadFX)
            {
                _triggeredArrivalLeadFX = true;
                var iGrid = mainGrid as IMyCubeGrid;
                float gridWidth = iGrid != null && iGrid.LocalAABB.Width > iGrid.LocalAABB.Height ? iGrid.LocalAABB.Width : (iGrid != null ? iGrid.LocalAABB.Height : 10f);
                float scale = (mainGrid.GridSizeEnum == MyCubeSize.Large)
                    ? MathHelper.Clamp(gridWidth / 15f, 1.5f, 5.0f)
                    : MathHelper.Clamp(gridWidth / 8f, 0.8f, 2.5f);

                if (MyAPIGateway.Multiplayer.IsServer && !MyAPIGateway.Utilities.IsDedicated)
                {
                    SpawnJumpArrivalLeadFX(JumpDestination, JumpDirection, scale, (float)avgTransitSpeedMps, IsPrototechDrive);
                }

                if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
                {
                    var fxMsg = new HyperspaceFXMessage
                    {
                        position = JumpDestination != Vector3D.Zero ? JumpDestination : mainGrid.WorldMatrix.Translation,
                        direction = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : mainGrid.WorldMatrix.Forward,
                        scale = scale,
                        speed = (float)avgTransitSpeedMps,
                        isArrival = true,
                        isArrivalLead = true,
                        isPrototech = IsPrototechDrive
                    };
                    byte[] fxData = MyAPIGateway.Utilities.SerializeToBinary(fxMsg);
                    MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.hyperspaceFXPacket, fxData);
                }
            }

            // Check if journey is complete (t >= 1.0)
            if (transitElapsedTicks >= transitTotalTicks)
            {
                ExitHyperspace();
                return;
            }

            // Emergency drop if no player or remote control is active on the ship during transit (debounced).
            // Looking for a pilot walks every block of the grid group, so sample it every 10 ticks and count the gap
            perf = FsdPerf.Begin();
            if (scanTick)
            {
                if (!HasSeatedPilot())
                    controlLossTicks += 10;
                else
                    controlLossTicks = 0;
            }
            FsdPerf.End(FsdPerf.Section.TransitPilot, perf);

            if (controlLossTicks >= 30)
            {
                EmergencyDrop("CONTROL LOST - EMERGENCY DROP!");
                return;
            }
        }

        // Client-side transit effects: sun suppression, tunnel, backdrop, cockpit shake and (optionally) the inHyperspace loop.
        // Shared by the normal transit tick and the hyperdiction sequence so the tunnel never drops out while a ship is held.
        private void UpdateTransitClientFX(Vector3D currentPos, float shakeMagnitude, bool playTransitLoop)
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            bool isLocalPlayerOnboard = IsLocalPlayerOnboard();

            if (isLocalPlayerOnboard)
            {
                HyperspaceSunManager.Instance.RequestSuppression(this, true);

                // Player is riding the ship: glue audio anchor to camera position so extreme speeds never outrun the sound
                Vector3D soundAnchor = (MyAPIGateway.Session?.Camera != null)
                    ? MyAPIGateway.Session.Camera.Position
                    : currentPos;

                sound?.SetPosition(soundAnchor);

                if (playTransitLoop)
                {
                    if (transitElapsedTicks == 1)
                    {
                        InitTransitSound();
                        if (transitSound != null)
                        {
                            transitSound.SetPosition(soundAnchor);
                            transitSound.SetVelocity(Vector3.Zero);
                            transitSound.CanPlayLoopSounds = true;
                            transitSound.PlaySound(WarpSound.inHyperspace, true);
                            transitSound.VolumeMultiplier = 1f;
                        }
                    }
                    else if (transitElapsedTicks > 1 && transitSound != null)
                    {
                        // Keep emitter glued to listener with zero velocity to eliminate Doppler cutoff
                        transitSound.SetPosition(soundAnchor);
                        transitSound.SetVelocity(Vector3.Zero);

                        if (!transitSound.IsPlaying)
                        {
                            transitSound.PlaySound(WarpSound.inHyperspace, true);
                        }
                    }
                }

                // The hyperspace tunnel all the way until we exit into real space
                UpdateTunnelParticle();

                // Hyperspace turbulence shake — SE handles damping/feel internally
                var activeCockpit = GridSystem?.FindMainCockpit() as MyCockpit;
                if (activeCockpit != null)
                {
                    activeCockpit.AddShake(Hyperdiction == null ? ShakeShaper.Natural(shakeMagnitude, 0.3f) : shakeMagnitude);
                }
            }
            else
            {
                HyperspaceSunManager.Instance.ReleaseSuppression(this, true);

                // Player is not onboard (left the cockpit, stepped out, or watched from outside):
                // Stop the internal hyperspace tunnel sound and corridor particle immediately!
                if (transitSound != null && transitSound.IsPlaying)
                {
                    transitSound.StopSound(true);
                }
                if (hyperdictionSound != null && hyperdictionSound.IsPlaying)
                {
                    hyperdictionSound.StopSound(true);
                }
                StopTunnelParticle();
            }
        }

        private void PerformTransitObstacleCheck()
        {
            ResolveJumpObstacles();
        }

        /// <summary>
        /// Clears the jump's destination of obstacles. A fleet jump is one jump: its leader does the clearing, for the width of the whole
        /// formation, and every wingman flies the leader's distance, so the whole fleet covers the same leg in the same time at the same speed.
        /// </summary>
        private void ResolveJumpObstacles()
        {
            long gridId = GridSystem?.mainGrid != null ? GridSystem.mainGrid.EntityId : 0L;
            FleetJumpLobby lobby = (FleetLeaderGridId != 0L) ? FleetJumpSystem.GetLobbyByLeader(FleetLeaderGridId) : null;

            if (lobby != null && lobby.LeaderGridId != gridId)
            {
                if (lobby.LeaderJumpDistance > 1.0)
                {
                    JumpDistance = lobby.LeaderJumpDistance;
                    JumpDestination = JumpOrigin + (JumpDirection * JumpDistance);
                }
                return;
            }

            Vector3D safeDest;
            double safeDist;
            if (TryAdjustDestinationForObstacles(JumpOrigin, JumpDirection, JumpDistance, out safeDest, out safeDist, lobby != null ? lobby.FormationRadius : 0.0))
            {
                JumpDestination = safeDest;
                JumpDistance = safeDist;
            }

            if (lobby != null)
                lobby.LeaderJumpDistance = JumpDistance;
        }

        // Drops out of Hyperspace at JumpDestination
        public void ExitHyperspace()
        {
            Hyperdiction = null;
            HyperspaceSunManager.Instance.ReleaseSuppression(this, false);
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            // Guarantee minimum 500m ground clearance on arrival
            BoundingBoxD destBox = new BoundingBoxD(JumpDestination - new Vector3D(100000), JumpDestination + new Vector3D(100000));
            var arrPlanet = MyGamePruningStructure.GetClosestPlanet(ref destBox);
            if (arrPlanet != null)
            {
                Vector3D pCenter = arrPlanet.PositionComp.GetPosition();
                Vector3D testDest = JumpDestination;
                Vector3D pSurf = arrPlanet.GetClosestSurfacePointGlobal(ref testDest);
                double shipRadius = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
                double minSafeAlt = 500.0 + shipRadius;
                double curAlt = (JumpDestination - pCenter).Length() - (pSurf - pCenter).Length();
                if (curAlt < minSafeAlt)
                {
                    Vector3D gNorm = Vector3D.Normalize(pSurf - pCenter);
                    JumpDestination = pSurf + (gNorm * minSafeAlt);
                }
            }

            // 1. Snap precisely to destination with locked orientation
            MatrixD finalMatrix = JumpOrientation;
            finalMatrix.Translation = JumpDestination;

            if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
                mainGrid.Teleport(finalMatrix);

            if (mainGrid.Physics != null)
                mainGrid.Physics.ClearSpeed();

            // 2. Audio & Visual teardown + Arrival FX
            StopTunnelParticle();
            StopArrivalTrailParticle();
            StopSound();
            _triggeredArrivalLeadFX = false;
            double avgSpeedExitMps = JumpDistance / Math.Max(0.5, (double)JourneyDuration);

            if (MyAPIGateway.Multiplayer.IsServer && !MyAPIGateway.Utilities.IsDedicated)
            {
                SpawnJumpArrivalFX();
                PlayJumpArrivalSound(IsPrototechDrive ? WarpSound.jumpOutSound : WarpSound.jumpOutSound);
            }

            if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
            {
                var iGrid = mainGrid as IMyCubeGrid;
                float gridWidth = iGrid != null && iGrid.LocalAABB.Width > iGrid.LocalAABB.Height ? iGrid.LocalAABB.Width : (iGrid != null ? iGrid.LocalAABB.Height : 10f);
                float scale = (mainGrid.GridSizeEnum == MyCubeSize.Large)
                    ? MathHelper.Clamp(gridWidth / 15f, 1.5f, 5.0f)
                    : MathHelper.Clamp(gridWidth / 8f, 0.8f, 2.5f);

                var fxMsg = new HyperspaceFXMessage
                {
                    position = JumpDestination != Vector3D.Zero ? JumpDestination : mainGrid.WorldMatrix.Translation,
                    direction = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : mainGrid.WorldMatrix.Forward,
                    scale = scale,
                    speed = (float)avgSpeedExitMps,
                    isArrival = true,
                    isArrivalLead = false,
                    isPrototech = IsPrototechDrive
                };
                byte[] fxData = MyAPIGateway.Utilities.SerializeToBinary(fxMsg);
                MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.hyperspaceFXPacket, fxData);
            }

            // 3. Restore Character.Save now that transit is over
            SetPlayersSave(true);
            _playersInTransit.Clear();

            UnregisterClaim(mainGrid.EntityId);
            FleetJumpSystem.UnregisterFleetGrid(mainGrid.EntityId);
            FleetSynchronizedJourneyDuration = 0f;
            FleetLeaderGridId = 0L;
            IsFleetHolding = false;
            fleetStaggerTicks = 0;
            chargeElapsedTicks = 0;
            holdElapsedTicks = 0;
            countdownElapsedTicks = 0;
            transitElapsedTicks = 0;
            fsdLossTicks = 0;
            powerLossTicks = 0;
            controlLossTicks = 0;
            cachedPowerMW = 0f;
            _triggeredArrivalLeadFX = false;
            checkedObstacle25 = false;
            checkedObstacle50 = false;
            checkedObstacle90 = false;

            // 4. Clear reached GPS waypoint & set Cooldown state
            SelectedGpsCoords = null;
            SelectedGpsName = string.Empty;
            cooldownElapsedTicks = 0;
            cooldownTotalTicks = (long)(COOLDOWN_SECONDS * 60f);
            State = HyperState.Cooldown;
            SendMessage("ARRIVED AT TARGET", 5f, "White");
        }

        // Player helpers - Character.Save suppression (borrowed from WarpSystem)
        private void CollectPlayersOnGrid()
        {
            _playersInTransit.Clear();
            var grids = GridSystem?.gridGroup;
            if (grids == null || grids.Count == 0) return;

            // Build a set of character entity IDs seated in cockpits/cryo on this grid group
            var seatedEntityIds = new List<long>();
            foreach (var grid in grids)
            {
                foreach (var block in (grid as MyCubeGrid)?.GetFatBlocks() ?? System.Linq.Enumerable.Empty<MyCubeBlock>())
                {
                    if (block == null) continue;
                    var cockpit = block as IMyCockpit;
                    var cryo = block as IMyCryoChamber;
                    if (cockpit?.Pilot != null)
                        seatedEntityIds.Add(cockpit.Pilot.EntityId);
                    else if (cryo?.Pilot != null)
                        seatedEntityIds.Add(cryo.Pilot.EntityId);
                }
            }

            if (seatedEntityIds.Count == 0) return;

            var onlinePlayers = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(onlinePlayers);
            foreach (var player in onlinePlayers)
            {
                if (player?.Character != null && seatedEntityIds.Contains(player.Character.EntityId))
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

        // Visual and Particle Helpers (Borrowed from Supercruise)

        // Shared helper: spawns a charge particle at the nose of the ship
        private void SpawnChargeParticle(string particleName)
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.mainGrid == null) return;
            var mainGrid = GridSystem.mainGrid;
            MatrixD gridMatrix = GridSystem.FindWorldMatrix();
            Vector3D forward = gridMatrix.Forward;
            Vector3D origin = mainGrid.PositionComp.WorldAABB.Center;
            Vector3D effectOffset = forward * mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax() * 2.0;

            MatrixD fromDir = MatrixD.CreateWorld(origin + effectOffset, -forward, gridMatrix.Up);

            var iGrid = mainGrid as IMyCubeGrid;
            float gridWidth = iGrid.LocalAABB.Width > iGrid.LocalAABB.Height ? iGrid.LocalAABB.Width : iGrid.LocalAABB.Height;
            float scale = (mainGrid.GridSizeEnum == MyCubeSize.Large) ? (gridWidth / 60f) : (gridWidth / 30f);
            baseChargeScale = scale;

            MyParticlesManager.TryCreateParticleEffect(particleName, ref fromDir, ref origin, uint.MaxValue, out chargeParticle);
            if (chargeParticle != null)
                chargeParticle.UserScale = scale;
        }

        // Used during the 10s charging phase (currently no visual — reserved for future use)
        private void StartChargeParticles()
        {
            // Intentionally empty: no particle during the initial charge phase.
            // Visuals are deferred to StartCountdownParticles() once the 5s countdown begins.
        }

        // Used during the 5s countdown/spool-up phase 
        private void StartCountdownParticles()
        {
            StopChargeParticles();
            SpawnChargeParticle("Warp_Prototech");
        }

        private void UpdateChargeParticles()
        {
            if (chargeParticle == null || chargeParticle.IsStopped || GridSystem?.mainGrid == null) return;
            var mainGrid = GridSystem.mainGrid;
            MatrixD gridMatrix = GridSystem.FindWorldMatrix();
            Vector3D forward = gridMatrix.Forward;
            Vector3D effectOffset = forward * mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax() * 2.0;
            Vector3D origin = mainGrid.PositionComp.WorldAABB.Center + effectOffset;
            chargeParticle.SetTranslation(ref origin);

            // Warp_Prototech plays normally for first 2.5 seconds (150 ticks),
            // then goes into reverse scale down to 0 over the remaining 2.5 seconds (150-300 ticks)
            // with particle velocities and direction unchanged.
            float progress = (countdownElapsedTicks <= 150)
                ? 1.0f
                : MathHelper.Clamp((float)(countdownTotalTicks - countdownElapsedTicks) / 150f, 0f, 1f);

            chargeParticle.UserScale = baseChargeScale * progress;
        }

        private void StopChargeParticles()
        {
            if (chargeParticle == null) return;
            try
            {
                chargeParticle.StopEmitting(0.1f);
                chargeParticle.Stop(false);
            }
            catch { }
            chargeParticle = null;
            baseChargeScale = 1.0f;
        }

        private void StartTunnelParticle()
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.mainGrid == null) return;
            StopTunnelParticle();

            // Strictly only for the ship the local player is currently riding
            if (!IsLocalPlayerOnboard()) return;

            UpdateTunnelParticle();
        }

        private void UpdateTunnelParticle()
        {
            var mainGrid = GridSystem?.mainGrid;
            if (mainGrid == null) return;

            // While a Hyperdictor shakes the ship, keep the tunnel's "up" fixed in space so the ship's roll shows against it
            Vector3D tunnelUp = (Hyperdiction != null && JumpOrientation != default(MatrixD)) ? (Vector3D)JumpOrientation.Up : (Vector3D)mainGrid.WorldMatrix.Up;
            MatrixD matrix = TunnelMatrix(mainGrid, JumpDirection, tunnelUp);

            if (tunnelParticle == null)
                tunnelParticle = SpawnTunnelParticle(mainGrid, matrix);   // also when coming back aboard mid-jump
            else
                tunnelParticle.WorldMatrix = matrix;
        }

        private void StopTunnelParticle()
        {
            if (tunnelParticle != null)
            {
                tunnelParticle.Stop(false);   // the last streaks fly out
                tunnelParticle = null;
            }
        }

        /// <summary>The FSD_HyperTunnel effect of Data/HyperspaceTunnel.sbc in the size made for this ship (null if it is missing).</summary>
        public static MyParticleEffect SpawnTunnelParticle(MyCubeGrid grid, MatrixD matrix)
        {
            double radius = Math.Max(1.0, ((IMyCubeGrid)grid).LocalAABB.HalfExtents.AbsMax());
            string size = radius < 14 ? "S" : radius < 42 ? "M" : radius < 118 ? "L" : "XL";
            double sizeRadius = radius < 14 ? 8 : radius < 42 ? 25 : radius < 118 ? 70 : 200;

            MyParticleEffect effect;
            Vector3D position = matrix.Translation;
            if (!MyParticlesManager.TryCreateParticleEffect("FSD_HyperTunnel_" + size, ref matrix, ref position, uint.MaxValue, out effect) || effect == null)
                return null;

            effect.UserScale = MathHelper.Clamp((float)(radius / sizeRadius), 0.6f, 2.5f);
            return effect;
        }

        /// <summary>Centred on the ship; the effect's particles fly along its -Z, from far ahead back through the ship.</summary>
        public static MatrixD TunnelMatrix(MyCubeGrid grid, Vector3D direction, Vector3D up)
        {
            Vector3D forward = direction.LengthSquared() > 0.001 ? Vector3D.Normalize(direction) : grid.WorldMatrix.Forward;
            if (Math.Abs(Vector3D.Dot(forward, Vector3D.Normalize(up))) > 0.99)
                up = Vector3D.CalculatePerpendicularVector(forward);
            return MatrixD.CreateWorld(grid.PositionComp.WorldAABB.Center, -forward, up);
        }

        private void StopArrivalTrailParticle()
        {
            if (arrivalTrailParticle != null)
            {
                try
                {
                    arrivalTrailParticle.StopEmitting(0.05f);
                    arrivalTrailParticle.Stop(false);
                }
                catch { }
                arrivalTrailParticle = null;
            }
        }

        private void SpawnJumpOriginFX(float scale, float speedMps = 0f)
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.mainGrid == null) return;
            try
            {
                var mainGrid = GridSystem.mainGrid;
                Vector3D direction = JumpDirection;
                if (direction.LengthSquared() < 0.001)
                    direction = mainGrid.WorldMatrix.Forward;

                Vector3D origin = JumpOrigin;
                if (origin == Vector3D.Zero)
                    origin = mainGrid.WorldMatrix.Translation;

                SpawnGlobalOriginFX(origin, direction, scale, speedMps, IsPrototechDrive);
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] SpawnJumpOriginFX error: " + ex);
            }
        }

        private void SpawnJumpArrivalLeadFX(Vector3D destination, Vector3D direction, float scale, float speedMps, bool isPrototech)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;
            SpawnGlobalArrivalLeadFX(destination, direction, scale, speedMps, isPrototech);
        }

        private void SpawnJumpArrivalFX(Vector3D? positionOverride = null)
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.mainGrid == null) return;

            try
            {
                var mainGrid = GridSystem.mainGrid;
                var iGrid = mainGrid as IMyCubeGrid;
                Vector3D direction = JumpDirection;
                if (direction.LengthSquared() < 0.001)
                    direction = mainGrid.WorldMatrix.Forward;

                Vector3D destination = JumpDestination;
                if (positionOverride.HasValue)
                    destination = positionOverride.Value;
                else if (destination == Vector3D.Zero)
                    destination = mainGrid.WorldMatrix.Translation;

                float forwardOffset = (float)(iGrid.LocalAABB.HalfExtents.Z + 8f);
                Vector3D fxPos = destination + direction * forwardOffset;

                float gridWidth = iGrid.LocalAABB.Width > iGrid.LocalAABB.Height ? iGrid.LocalAABB.Width : iGrid.LocalAABB.Height;
                float scale = (mainGrid.GridSizeEnum == MyCubeSize.Large)
                    ? MathHelper.Clamp(gridWidth / 15f, 1.5f, 5.0f)
                    : MathHelper.Clamp(gridWidth / 8f, 0.8f, 2.5f);

                SpawnGlobalArrivalFX(fxPos, direction, scale, IsPrototechDrive);
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] SpawnJumpArrivalFX error: " + ex);
            }
        }

        public static void SpawnGlobalOriginFX(Vector3D origin, Vector3D direction, float scale, bool isPrototech)
        {
            SpawnGlobalOriginFX(origin, direction, scale, 0f, isPrototech);
        }

        public static void SpawnGlobalOriginFX(Vector3D origin, Vector3D direction, float scale, float speedMps, bool isPrototech)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;
            try
            {
                if (direction.LengthSquared() < 0.001) direction = Vector3D.Forward;
                MatrixD worldMatrix = MatrixD.CreateWorld(origin, direction, Vector3D.Up);
                MyParticleEffect originParticle;
                if (!MyParticlesManager.TryCreateParticleEffect("HyperspaceJumpOrigin", ref worldMatrix, ref origin, uint.MaxValue, out originParticle))
                {
                    MyParticlesManager.TryCreateParticleEffect("BlinkDriveTrail", ref worldMatrix, ref origin, uint.MaxValue, out originParticle);
                }
                if (originParticle != null)
                {
                    float speedFactor = speedMps > 0f ? MathHelper.Clamp(speedMps / 50000f, 0.8f, 3.0f) : 1f;
                    originParticle.UserScale = (scale > 0 ? scale : 2.5f) * (float)Math.Pow(speedFactor, 0.25);
                }

                var emitter = new MyEntity3DSoundEmitter(null);
                emitter.SetPosition(origin);
                emitter.VolumeMultiplier = CalculateSpatialFleetVolumeScale(origin, _recentOriginEvents);
                emitter.PlaySound(WarpSound.hyperspaceJump, true);
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] SpawnGlobalOriginFX error: " + ex);
            }
        }

        public static void SpawnGlobalArrivalLeadFX(Vector3D destination, Vector3D direction, float scale, float speedMps, bool isPrototech)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;
            try
            {
                if (direction.LengthSquared() < 0.001) direction = Vector3D.Forward;
                MatrixD worldMatrix = MatrixD.CreateWorld(destination, direction, Vector3D.Up);
                MyParticleEffect leadParticle;
                if (!MyParticlesManager.TryCreateParticleEffect("HyperspaceJumpArrivalLead", ref worldMatrix, ref destination, uint.MaxValue, out leadParticle))
                {
                    MyParticlesManager.TryCreateParticleEffect("HyperspaceJumpArrival", ref worldMatrix, ref destination, uint.MaxValue, out leadParticle);
                }
                if (leadParticle != null)
                {
                    float speedFactor = speedMps > 0f ? MathHelper.Clamp(speedMps / 50000f, 0.8f, 3.0f) : 1f;
                    leadParticle.UserScale = (scale > 0 ? scale : 2.5f) * (float)Math.Pow(speedFactor, 0.25);
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] SpawnGlobalArrivalLeadFX error: " + ex);
            }
        }

        public static void SpawnGlobalArrivalFX(Vector3D destination, Vector3D direction, float scale, bool isPrototech)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;
            try
            {
                if (direction.LengthSquared() < 0.001) direction = Vector3D.Forward;
                Vector3D fxPos = destination + direction * 10f;
                MatrixD worldMatrix = MatrixD.CreateWorld(fxPos, direction, Vector3D.Up);
                MyParticleEffect arrivalParticle;
                if (!MyParticlesManager.TryCreateParticleEffect("HyperspaceJumpArrival", ref worldMatrix, ref fxPos, uint.MaxValue, out arrivalParticle))
                {
                    MyParticlesManager.TryCreateParticleEffect("HyperspaceJumpOrigin", ref worldMatrix, ref fxPos, uint.MaxValue, out arrivalParticle);
                }
                if (arrivalParticle != null)
                    arrivalParticle.UserScale = scale > 0 ? scale : 2.5f;

                var emitter = new MyEntity3DSoundEmitter(null);
                emitter.SetPosition(destination);
                emitter.VolumeMultiplier = CalculateSpatialFleetVolumeScale(destination, _recentArrivalEvents);
                emitter.PlaySound(isPrototech ? WarpSound.jumpOutSound : WarpSound.jumpOutSound, true);
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] SpawnGlobalArrivalFX error: " + ex);
            }
        }

        private int fleetSteerTick;
        private int fleetSteerRefresh;
        private bool fleetSteerHuman;
        private bool fleetSteerOverriding;
        private readonly List<IMyGyro> fleetSteerGyros = new List<IMyGyro>();

        public void UpdateFleetDroneAutoSteer()
        {
            if (!IsFleetHolding || GridSystem?.mainGrid == null || GridSystem.gridGroup == null)
                return;

            // A proportional controller does not need every tick, and finding the pilot and the gyros walks every block of the grid group:
            // steer 15 times a second and look the pilot and the gyros up twice a second
            if ((fleetSteerTick++ & 3) != 0)
                return;

            if (fleetSteerRefresh-- <= 0)
            {
                fleetSteerRefresh = 30;
                fleetSteerHuman = HasHumanPilot();
                fleetSteerGyros.Clear();
                foreach (var igrid in GridSystem.gridGroup)
                {
                    var cg = igrid as MyCubeGrid;
                    if (cg == null) continue;
                    foreach (var b in cg.GetFatBlocks())
                    {
                        var g = b as IMyGyro;
                        if (g != null && g.IsFunctional && g.Enabled)
                            fleetSteerGyros.Add(g);
                    }
                }
            }

            // If a human is piloting or actively remote-controlling the ship, do not override gyros!
            if (fleetSteerHuman)
            {
                if (fleetSteerOverriding)
                    ResetFleetAutoSteerGyros();
                return;
            }

            var cockpit = GridSystem.FindMainCockpit();
            MatrixD refMatrix = (cockpit != null) ? cockpit.WorldMatrix : GridSystem.mainGrid.WorldMatrix;

            Vector3D targetDir = Vector3D.Zero;
            if (JumpDirection.LengthSquared() > 0.001)
            {
                targetDir = JumpDirection;
            }
            else
            {
                var lobby = FleetJumpSystem.GetLobbyForGrid(GridSystem.mainGrid.EntityId);
                if (lobby != null)
                {
                    targetDir = (lobby.Mode == JumpMode.GpsWaypoint && lobby.TargetGpsCoords.HasValue)
                        ? Vector3D.Normalize(lobby.TargetGpsCoords.Value - refMatrix.Translation)
                        : (lobby.DepartureOrientation.Forward);
                }
                else if (SelectedGpsCoords.HasValue)
                {
                    targetDir = Vector3D.Normalize(SelectedGpsCoords.Value - refMatrix.Translation);
                }
                else
                {
                    targetDir = refMatrix.Forward;
                }
            }

            if (targetDir.LengthSquared() < 0.001)
                return;

            double dot = MathHelper.Clamp(Vector3D.Dot(refMatrix.Forward, targetDir), -1.0, 1.0);
            double angleRad = Math.Acos(dot);
            double angleDeg = MathHelper.ToDegrees(angleRad);

            var gyros = fleetSteerGyros;
            if (gyros.Count == 0) return;
            fleetSteerOverriding = true;

            if (angleDeg <= 1.0)
            {
                // Aligned within 1 degree -> dampen and hold
                foreach (var g in gyros)
                {
                    g.GyroOverride = true;
                    g.Pitch = 0f;
                    g.Yaw = 0f;
                    g.Roll = 0f;
                }
                return;
            }

            Vector3D axisWorld = Vector3D.Cross(refMatrix.Forward, targetDir);
            if (axisWorld.LengthSquared() > 1e-6)
                axisWorld = Vector3D.Normalize(axisWorld);

            // Proportional velocity controller capped at 3.0 rad/s
            double speed = MathHelper.Clamp(angleRad * 2.5, 0.2, 3.0);
            Vector3D worldAngVel = axisWorld * speed;

            foreach (var g in gyros)
            {
                Vector3D localAngVel = Vector3D.TransformNormal(worldAngVel, MatrixD.Transpose(g.WorldMatrix));
                g.GyroOverride = true;
                g.Pitch = (float)localAngVel.X;
                g.Yaw = (float)localAngVel.Y;
                g.Roll = (float)localAngVel.Z;
            }
        }

        public void ResetFleetAutoSteerGyros()
        {
            fleetSteerOverriding = false;
            fleetSteerRefresh = 0;
            fleetSteerGyros.Clear();
            if (GridSystem?.gridGroup == null) return;
            foreach (var igrid in GridSystem.gridGroup)
            {
                var cg = igrid as MyCubeGrid;
                if (cg == null) continue;
                foreach (var b in cg.GetFatBlocks())
                {
                    var g = b as IMyGyro;
                    if (g != null && g.GyroOverride)
                    {
                        g.GyroOverride = false;
                        g.Pitch = 0f;
                        g.Yaw = 0f;
                        g.Roll = 0f;
                    }
                }
            }
        }
    }
}