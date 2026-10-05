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
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using ProtoBuf;

namespace WarpDriveMod
{
    public class HyperspaceSystem
    {
        public enum HyperState
        {
            Idle,
            Charging,       // Initial 10s spool up
            HoldingCharge,  // Fully charged, waiting for player to align within 5°
            Countdown,      // Within 5°, 5s countdown with smooth micro-adjust
            Active,         // In hyperspace tunnel (11s - 21s)
            Cooldown
        }

        public enum JumpMode
        {
            ManualDistance,
            GpsWaypoint
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

        public HyperState State { get; private set; } = HyperState.Idle;
        public JumpMode Mode { get; set; } = JumpMode.ManualDistance;

        // Slider value: 0.0f (0%) to 1.0f (100%) of maximum jump range
        public float JumpDistanceRatio { get; set; } = 1.0f;

        // GPS Target Data
        public string SelectedGpsName { get; set; } = string.Empty;
        public Vector3D? SelectedGpsCoords { get; set; } = null;

        // Active Jump Data (Set when jump engages)
        public Vector3D JumpOrigin { get; private set; }
        public Vector3D JumpDestination { get; private set; }
        public Vector3D JumpDirection { get; private set; }
        public MatrixD JumpOrientation { get; private set; } = MatrixD.Identity;
        public double JumpDistance { get; private set; }
        public float JourneyDuration { get; private set; }
        public long JumpStartTick { get; private set; }

        // Grid & Drive references
        public GridSystem GridSystem { get; private set; }
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
        private MyParticleEffect tunnelParticle;
        private MyParticleEffect tunnelParticleRear;
        private MyParticleEffect arrivalTrailParticle;
        private long transitElapsedTicks = 0;
        private long transitTotalTicks = 0;
        public double CurrentTransitSpeed { get; private set; } // Current speed in km/s

        // In-transit dynamic obstacle & fleet clearance checkpoints
        private bool checkedObstacle25 = false;
        private bool checkedObstacle50 = false;
        private bool checkedObstacle90 = false;
        private bool _triggeredArrivalLeadFX = false;

        // Fleet Jump & Stagger runtime fields
        public bool IsFleetHolding { get; private set; } = false;
        private int fleetStaggerTicks = 0;
        public float FleetSynchronizedJourneyDuration { get; private set; } = 0f;
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
        private const float HYPERSPACE_SHAKE = 1.0f; // magnitude fed to AddShake() every transit tick

        // Sky dome / background occluder fields (client-side only)
        private static readonly MyStringId SkyMaterial = MyStringId.GetOrCompute("Square");
        private static readonly Vector4 SkyColorVec4 = new Vector4(0.005f, 0.008f, 0.02f, 1.0f);

        // Dedicated transit-only sound emitter with NO entity binding.
        // MyEntity3DSoundEmitter(null) makes SetPosition() the sole authority on location,
        // which means it never lags behind when the grid teleports at high hyperspace speeds.
        private MyEntity3DSoundEmitter transitSound;
        private MyEntity3DSoundEmitter jumpOriginSound;
        private MyEntity3DSoundEmitter jumpArrivalSound;

        // Emergency Drop fields
        private static readonly Dictionary<IMyPowerProducer, long> _globallyDisabledPower = new Dictionary<IMyPowerProducer, long>();
        private readonly List<IMyPowerProducer> tempDisabledPower = new List<IMyPowerProducer>();
        private int emergencyPowerDisableTicks = 0;
        private int fsdLossTicks = 0;
        private int powerLossTicks = 0;
        private int controlLossTicks = 0;

        // Players in transit - Character.Save suppression (borrowed from WarpSystem)
        private readonly List<IMyPlayer> _playersInTransit = new List<IMyPlayer>();

        public HyperspaceSystem(WarpDrive drive, GridSystem gridSystem)
        {
            HostDrive = drive;
            GridSystem = gridSystem;

            WarpDriveSession.Instance?.RegisterHyperspace(this);

            if (!MyAPIGateway.Utilities.IsDedicated && gridSystem?.MainGrid != null)
            {
                sound = new MyEntity3DSoundEmitter(gridSystem.MainGrid)
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
            WarpDriveSession.Instance?.UnregisterHyperspace(this);
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
            if (HostDrive?.System != null)
            {
                HostDrive.System.SendMessage(msg, seconds, font, playerId);
            }
            else if (!MyAPIGateway.Utilities.IsDedicated)
            {
                MyAPIGateway.Utilities.ShowNotification(msg, (int)(seconds * 1000), font);
            }
        }

        public bool IsPrototechDrive => HostDrive?.IsPrototech == true || HostDrive?.System?.IsPrototech == true;

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
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.MainGrid == null || soundPair == null) return;
            if (sound == null)
            {
                sound = new MyEntity3DSoundEmitter(GridSystem.MainGrid)
                {
                    CanPlayLoopSounds = true
                };
            }
            sound.CanPlayLoopSounds = true;
            sound.SetPosition(GridSystem.MainGrid.PositionComp.GetPosition());

            if (currentSound == soundPair && sound.IsPlaying)
                return;

            currentSound = soundPair;

            // Fleet normalization: scale acoustic power by 1 / sqrt(N)
            float volumeScale = 1.0f;
            long gridId = GridSystem.MainGrid.EntityId;
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

            var grids = GridSystem?.Grids;
            if (grids != null && grids.Count > 0)
            {
                foreach (var grid in grids)
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    foreach (var block in grid.GetFatBlocks())
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
            else if (HostDrive?.Block?.CubeGrid != null)
            {
                var mainGrid = HostDrive.Block.CubeGrid as MyCubeGrid;
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
            return MathHelper.Clamp(calculatedMax, MIN_JUMP_DISTANCE, HARD_MAX_JUMP_DISTANCE);
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
        /// with 0.5s smooth ramp-up at departure and 0.5s smooth ramp-down before arrival.
        /// </summary>
        public static void CalculateTransitProgress(double elapsedSeconds, double totalSeconds, double totalDistance, out float progress, out double speedMps)
        {
            if (totalSeconds <= 0.001 || totalDistance <= 0.0)
            {
                progress = 1.0f;
                speedMps = 0.0;
                return;
            }

            double ta = Math.Min((double)TRANSIT_ACCEL_SECONDS, totalSeconds * 0.45);
            double td = Math.Min((double)TRANSIT_DECEL_SECONDS, totalSeconds * 0.45);
            double tEff = totalSeconds - (0.5 * (ta + td));
            double vCruise = totalDistance / tEff;

            double tau = MathHelper.Clamp(elapsedSeconds, 0.0, totalSeconds);

            if (tau <= ta && ta > 0.0001)
            {
                // Departure acceleration phase (0.0 to 0.5s): smooth sinusoidal ramp up from 0 to vCruise
                double phase = (Math.PI * tau) / ta;
                double dist = 0.5 * (tau - (ta / Math.PI) * Math.Sin(phase)) * vCruise;
                progress = MathHelper.Clamp((float)(dist / totalDistance), 0f, 1f);
                speedMps = 0.5 * (1.0 - Math.Cos(phase)) * vCruise;
            }
            else if (tau < (totalSeconds - td))
            {
                // Constant cruise phase
                double dist = (0.5 * ta * vCruise) + ((tau - ta) * vCruise);
                progress = MathHelper.Clamp((float)(dist / totalDistance), 0f, 1f);
                speedMps = vCruise;
            }
            else
            {
                // Arrival deceleration phase (last 0.5s before arrival): smooth sinusoidal ramp down from vCruise to 0
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

        // Planet detection: Ray-Sphere intersection to ensure hyperspace course does not fly through any planets
        public static bool IsTrajectoryBlockedByPlanet(Vector3D start, Vector3D target, out string blockedPlanetName)
        {
            blockedPlanetName = null;
            Vector3D travelVec = target - start;
            double travelDist = travelVec.Length();
            if (travelDist <= 0.001) return false;
            Vector3D travelDir = travelVec / travelDist;

            List<IMyVoxelBase> voxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels);

            foreach (var voxel in voxels)
            {
                var planet = voxel as MyPlanet;
                if (planet == null) continue;

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
                            MyAPIGateway.Utilities.ShowNotification("Can't Start FSD - SafeZone in flight path!", 4000, "Red");
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

                    if (GridSystem != null && GridSystem.Grids != null)
                    {
                        bool isMember = false;
                        foreach (var g in GridSystem.Grids)
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
                            MyAPIGateway.Utilities.ShowNotification($"Can't Start FSD - Obstacle ahead: {gridName}", 4000, "Red");
                            return true;
                        }
                    }
                }
                else if (ent is MyVoxelBase)
                {
                    var voxel = ent as MyVoxelBase;
                    if (voxel == null) continue;

                    // Ignore planets and planet terrain physics chunks (handled by planetary safety math)
                    if (voxel is MyPlanet || (voxel as MyVoxelBase)?.RootVoxel is MyPlanet)
                        continue;

                    // Asteroids: Test if solid voxel rock geometry exists inside the forward corridor (up to 2.5km)
                    double asteroidScanDist = Math.Max(corridorLength, 2500.0);
                    BoundingBoxD asteroidCorridorBox = new BoundingBoxD(
                        Vector3D.Min(startPos, startPos + forward * asteroidScanDist) - new Vector3D(corridorRadius),
                        Vector3D.Max(startPos, startPos + forward * asteroidScanDist) + new Vector3D(corridorRadius)
                    );

                    if (voxel.PositionComp.WorldAABB.Intersects(asteroidCorridorBox))
                    {
                        if (voxel.GetIntersectionWithAABB(ref asteroidCorridorBox))
                        {
                            MyAPIGateway.Utilities.ShowNotification("Can't Start FSD - Asteroid in flight path!", 4000, "Red");
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // Adjusts jump destination if any grid, asteroid, or safezone is within 500m of arrival point
        public bool TryAdjustDestinationForObstacles(Vector3D origin, Vector3D dir, double dist, out Vector3D safeDest, out double safeDist)
        {
            safeDest = origin + (dir * dist);
            safeDist = dist;
            bool adjusted = false;

            double shipRadius = GridSystem?.MainGrid?.PositionComp.WorldAABB.HalfExtents.AbsMax() ?? 50.0;
            double safetyBuffer = 500.0 + shipRadius;
            double searchRadius = Math.Max(safetyBuffer * 4.0, 5000.0);

            long myGridId = GridSystem?.MainGrid?.EntityId ?? 0L;
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
                            (GridSystem?.MainGrid != null && MyAPIGateway.GridGroups.HasConnection(GridSystem.MainGrid, grid, GridLinkTypeEnum.Logical)))
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

            return adjusted;
        }

        // Checks if any cockpit / ship controller across the grid group has a seated pilot or active remote control
        public bool HasSeatedPilot()
        {
            var grids = GridSystem?.Grids;
            if (grids == null || grids.Count == 0) return false;

            foreach (var grid in grids)
            {
                if (grid == null) continue;
                HashSet<IMyShipController> controllers;
                if (GridSystem.cockpits.TryGetValue(grid, out controllers) && controllers != null)
                {
                    foreach (var sc in controllers)
                    {
                        if (sc != null && sc.IsFunctional)
                        {
                            if (sc.IsUnderControl || sc.Pilot != null)
                                return true;
                            if (sc is IMyRemoteControl)
                                return true;
                        }
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

        // Pre-Flight & Safety Checks
        public bool CanInitiateJump(long playerId = 0L)
        {
            var warpSys = HostDrive?.System;
            if (warpSys == null || GridSystem == null || GridSystem.MainGrid == null)
                return false;

            // 0. Cooldown Check
            if (State == HyperState.Cooldown)
            {
                warpSys.SendMessage("FSD IS COOLING DOWN - PLEASE WAIT", 4f, "Red", playerId);
                return false;
            }

            // 0.1 Supercruise Conflict Check
            if (warpSys.WarpState != WarpSystem.State.Idle)
            {
                warpSys.SendMessage(warpSys.warnInUse, 5f, "Red", playerId);
                return false;
            }

            // 1. Health (Borrowed from WarpDrive / WarpSystem)
            if (!HostDrive.Block.IsFunctional || !HostDrive.Block.IsWorking)
            {
                warpSys.SendMessage(warpSys.warnDamaged, 5f, "Red", playerId);
                return false;
            }

            // 2. Power (Borrowed from WarpDrive)
            if (!HostDrive.HasPower)
            {
                warpSys.SendMessage(warpSys.warnNoPower, 5f, "Red", playerId);
                return false;
            }

            // 3. Static / Docked Grids (Borrowed from GridSystem & WarpSystem)
            if (GridSystem.IsStatic || warpSys.ConnectedStatic(GridSystem.MainGrid))
            {
                warpSys.SendMessage(warpSys.warnStatic, 5f, "Red", playerId);
                return false;
            }

            // 4. Natural Gravity at Origin (Borrowed from WarpSystem)
            if (warpSys.IsInGravity())
            {
                warpSys.SendMessage(warpSys.warnNoEstablish, 5f, "Red", playerId);
                return false;
            }

            // 5. Obstructions in front of the ship (Asteroids, Safezones, Grids)
            var gridMatrix = GridSystem.FindWorldMatrix();
            if (IsProximityDanger(gridMatrix, GridSystem.MainGrid))
            {
                warpSys.SendMessage(warpSys.ProximytyAlert, 5f, "Red", playerId);
                return false;
            }

            // 6. Cockpit & Target Coordinate Resolution
            if (!HasSeatedPilot())
            {
                warpSys.SendMessage("CANNOT JUMP - NO PILOT OR REMOTE CONTROL!", 5f, "Red", playerId);
                return false;
            }

            var cockpit = GridSystem.FindMainCockpit();
            if (cockpit == null)
            {
                warpSys.SendMessage("No active cockpit or remote control found on grid!", 5f, "Red", playerId);
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
                warpSys.SendMessage(Mode == JumpMode.GpsWaypoint ? "No valid GPS waypoint selected!" : "Invalid jump target!", 5f, "Red", playerId);
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
                warpSys.SendMessage($"Target below minimum jump range ({(MIN_JUMP_DISTANCE / 1000):N0}km)!", 5f, "Red", playerId);
                return false;
            }

            if (totalDist > maxRange)
            {
                warpSys.SendMessage($"Target exceeds maximum range ({(maxRange / 1000):N0}km)!", 5f, "Red", playerId);
                return false;
            }

            // 8. Planet Occlusion along Flight Path
            Vector3D shipPos = cockpit.WorldMatrix.Translation;
            string blockedPlanet;
            if (IsTrajectoryBlockedByPlanet(shipPos, targetCoord, out blockedPlanet))
            {
                warpSys.SendMessage($"Trajectory obstructed by {blockedPlanet}!", 5f, "Red", playerId);
                return false;
            }

            // 9. Destination inside Natural Gravity
            float dummyGravity;
            Vector3D destGravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(targetCoord, out dummyGravity);
            float allowInGravityMax = HostDrive?.Settings?.AllowInGravityMax ?? 0.2f;
            if ((destGravity.Length() / WarpSystem.EARTH_GRAVITY) > allowInGravityMax)
            {
                warpSys.SendMessage("Destination inside planetary gravity well!", 5f, "Red", playerId);
                return false;
            }

            // 10. Alignment Check (GPS Mode only when coordinates are valid)
            if (Mode == JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue)
            {
                Vector3D targetDir = Vector3D.Normalize(targetCoord - shipPos);
                double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
                double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));

                if (angleDeg > ALIGNMENT_TOLERANCE_DEG)
                {
                    warpSys.SendMessage($"Align with target! Offset: {angleDeg:F1}° (Max {ALIGNMENT_TOLERANCE_DEG:F1}°)", 3f, "Red", playerId);
                    return false;
                }
            }

            return true;
        }

        // User action: Toggles jump or aborts if already in progress
        public void TriggerJump(long playerId = 0L)
        {
            var mainGrid = GridSystem?.MainGrid;

            // Check if participating in a Fleet Jump: Triggering solo Hyperspace Jump aborts / leaves the fleet jump
            if (mainGrid != null && FleetJumpSystem.IsGridInFleet(mainGrid.EntityId))
            {
                FleetJumpSystem.CancelFleetJump(mainGrid.EntityId, playerId);
                return;
            }

            if (State == HyperState.Idle)
            {
                if (HostDrive != null && !HostDrive.SupportsHyperspace)
                {
                    HostDrive.System?.SendMessage("FSD does not support Hyperspace jumps! (Supercruise Only)", 5f, "Red", playerId);
                    return;
                }

                if (HostDrive?.System != null && HostDrive.System.WarpState != WarpSystem.State.Idle)
                {
                    HostDrive.System.SendMessage(HostDrive.System.warnInUse, 5f, "Red", playerId);
                    return;
                }

                // If an active friendly fleet jump invitation exists in 10km, join it!
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
            if (cockpit == null) return;

            if (!CanInitiateJump(playerId)) return;

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

            var mainGrid = GridSystem?.MainGrid;
            float powerMW = GetDrivePowerMW();
            Vector3D targetCoord;
            double totalDist;
            if (TryGetTargetCoordinates(cockpit, powerMW, out targetCoord, out totalDist))
            {
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
            }

            State = HyperState.Charging;
            chargeElapsedTicks = 0;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                PlaySound(WarpConstants.fsd_hyper_charge);
            }

            HostDrive?.System?.SendMessage("FLEET FSD CHARGING - HOLDING FORMATION...", 4f, "White", triggeringPlayerId);
        }

        // Leader confirmed fleet jump -> executes synchronized jump with relative formation & staggered delay
        public void ExecuteFleetMemberJump(int staggerTicks, Vector3D relativeFormationOffset, Vector3D leaderJumpDirection = default(Vector3D), float syncJourneyDuration = 0f, long leaderGridId = 0L, MatrixD leaderDepartureOrientation = default(MatrixD), double leaderJumpDistance = 0.0)
        {
            IsFleetHolding = false;
            fleetStaggerTicks = staggerTicks;
            FleetSynchronizedJourneyDuration = syncJourneyDuration;

            var cockpit = GridSystem?.FindMainCockpit();
            var mainGrid = GridSystem?.MainGrid;
            if (cockpit == null || mainGrid == null) return;

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
                if (TryGetTargetCoordinates(cockpit, powerMW, out targetCoord, out totalDist))
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
            Vector3D safeDest;
            double safeDist;
            if (TryAdjustDestinationForObstacles(JumpOrigin, JumpDirection, JumpDistance, out safeDest, out safeDist))
            {
                JumpDestination = safeDest;
                JumpDistance = safeDist;
            }

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
                HostDrive?.System?.SendMessage("FLEET JUMP CONFIRMED - COMPLETING CHARGE...", 3f, "White", triggeringPlayerId);
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

            if (!CanInitiateJump(playerId)) return;

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
            var mainGrid = GridSystem?.MainGrid;
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
                HostDrive.System.SendMessage("OBSTRUCTION AT ARRIVAL - 500m CLEARANCE APPLIED", 4f, "White", triggeringPlayerId);
            }

            State = HyperState.Charging;
            chargeElapsedTicks = 0;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                // No particle during the initial 10s charge phase — visuals start at countdown
                PlaySound(WarpConstants.fsd_hyper_charge);
            }

            HostDrive.System.SendMessage("FSD CHARGING...", 3f, "White", triggeringPlayerId);
        }

        // Ticked during the 10s charging spool up
        private void UpdateChargingTick()
        {
            var warpSys = HostDrive?.System;
            var mainGrid = GridSystem?.MainGrid;
            if (warpSys == null || mainGrid == null) return;

            // Hazard abort checks
            if (!HostDrive.Block.IsFunctional || !HostDrive.Block.IsWorking)
            {
                AbortJump("FSD DAMAGED - CHARGING ABORTED");
                return;
            }
            if (!HostDrive.HasPower)
            {
                AbortJump("POWER FAILURE - CHARGING ABORTED");
                return;
            }
            if (!HasSeatedPilot())
            {
                AbortJump("JUMP ABORTED - CONTROL LOST");
                return;
            }
            if (warpSys.IsInGravity())
            {
                AbortJump("GRAVITY WELL DETECTED - CHARGING ABORTED");
                return;
            }
            if (GridSystem.IsStatic || warpSys.ConnectedStatic(mainGrid))
            {
                AbortJump(warpSys.warnStatic);
                return;
            }

            // Proximity hazard check (bypassed if participating in Fleet Jump)
            if (!IsFleetHolding && FleetLeaderGridId == 0L && !FleetJumpSystem.IsGridInFleet(mainGrid.EntityId))
            {
                var gridMatrix = GridSystem.FindWorldMatrix();
                if (IsProximityDanger(gridMatrix, mainGrid))
                {
                    AbortJump(warpSys.ProximytyAlert);
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

            // Check if 10-second charge completed
            if (chargeElapsedTicks >= chargeTotalTicks)
            {
                if (IsFleetHolding)
                {
                    // Check alignment with fleet target direction
                    var curCockpit = GridSystem.FindMainCockpit();
                    if (curCockpit != null)
                    {
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
                                    ? Vector3D.Normalize(lobby.TargetGpsCoords.Value - curCockpit.WorldMatrix.Translation)
                                    : lobby.DepartureOrientation.Forward;
                            }
                            else
                            {
                                targetDir = JumpDirection.LengthSquared() > 0.001 
                                    ? JumpDirection 
                                    : (JumpDestination != Vector3D.Zero ? Vector3D.Normalize(JumpDestination - curCockpit.WorldMatrix.Translation) : curCockpit.WorldMatrix.Forward);
                            }
                        }

                        double dot = MathHelper.Clamp(Vector3D.Dot(curCockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
                        double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));

                        if (chargeElapsedTicks % 60 == 0)
                        {
                            if (angleDeg > ALIGNMENT_TOLERANCE_DEG)
                                warpSys.SendMessage($"ALIGN TO FLEET VECTOR | OFFSET: {angleDeg:F1}° (Max {ALIGNMENT_TOLERANCE_DEG:F1}°)", 1f, "Red", triggeringPlayerId);
                            else
                                warpSys.SendMessage("FLEET ALIGNMENT LOCKED - HOLDING CHARGE", 1f, "White", triggeringPlayerId);
                        }
                    }

                    // Stay in charging holding loop until Jump Leader confirms execution
                    if (!MyAPIGateway.Utilities.IsDedicated && sound != null && !sound.IsPlaying)
                    {
                        currentSound = null;
                        PlaySound(WarpConstants.holdCharge);
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
                PlaySound(WarpConstants.holdCharge);
            }

            HostDrive.System.SendMessage("ALIGN WITH TARGET | FSD HOLDING CHARGE", 3f, "Red", triggeringPlayerId);
        }

        // Ticked while holding 100% charge waiting for pilot to steer into 5° window
        private void UpdateHoldingChargeTick()
        {
            var warpSys = HostDrive?.System;
            var mainGrid = GridSystem?.MainGrid;
            if (warpSys == null || mainGrid == null) return;

            // Hazard abort checks
            if (!HostDrive.Block.IsFunctional || !HostDrive.Block.IsWorking || !HostDrive.HasPower || warpSys.IsInGravity())
            {
                AbortJump("CHARGE COLLAPSED - HAZARD DETECTED");
                return;
            }
            if (!HasSeatedPilot())
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
                        AbortJump("KICKED FROM FLEET JUMP - EXCEEDED 10km SECTOR LIMIT");
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
                    PlaySound(WarpConstants.holdCharge);
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

            // Show periodic alignment warning every 60 ticks (1 second)
            if (holdElapsedTicks++ % 60 == 0)
            {
                warpSys.SendMessage($"ALIGN TO TARGET | OFFSET: {angleDeg:F1}° (Max {ALIGNMENT_TOLERANCE_DEG:F1}°)", 1f, "Red", triggeringPlayerId);
            }

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
                PlaySound(WarpConstants.engaging_hyperspace);
                // Start Prototech charge particles for the 5-second spool-up phase
                StopChargeParticles();
                StartCountdownParticles();
            }

            if (Mode == JumpMode.ManualDistance)
                HostDrive.System.SendMessage("FSD ENGAGING IN 5...", 1f, "White", triggeringPlayerId);
            else
                HostDrive.System.SendMessage("ALIGNMENT LOCKED - ENGAGING IN 5...", 1f, "White", triggeringPlayerId);
        }

        // Ticked during the 5-second countdown with auto-micro adjustment
        private void UpdateCountdownTick()
        {
            var warpSys = HostDrive?.System;
            var mainGrid = GridSystem?.MainGrid;
            if (warpSys == null || mainGrid == null) return;

            // Hazard abort checks
            if (!HostDrive.Block.IsFunctional || !HostDrive.Block.IsWorking || !HostDrive.HasPower || warpSys.IsInGravity())
            {
                AbortJump("JUMP ABORTED - HAZARD DETECTED");
                return;
            }
            if (!HasSeatedPilot())
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
            else // GpsWaypoint or Fleet Jump Alignment & Micro-Adjustment
            {
                Vector3D targetDir;
                Vector3D? targetUp = null;

                if (isFleet && JumpOrientation != default(MatrixD))
                {
                    targetDir = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : JumpOrientation.Forward;
                    targetUp = JumpOrientation.Up;
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

                // Smoothly slerp grid orientation towards target vector & synchronized fleet orientation
                float slerpRatio = 0.05f;
                MatrixD adjusted = MicroAdjustOrientation(mainGrid.WorldMatrix, targetDir, slerpRatio, targetUp);
                if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
                {
                    mainGrid.Teleport(adjusted);
                }
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                sound?.SetPosition(mainGrid.PositionComp.GetPosition());
                UpdateChargeParticles();
            }

            countdownElapsedTicks++;

            // Visual countdown every 60 ticks (1 second)
            if (countdownElapsedTicks % 60 == 0)
            {
                int secondsRemaining = (int)((countdownTotalTicks - countdownElapsedTicks) / 60);
                if (secondsRemaining > 0)
                    warpSys.SendMessage($"ENGAGING IN: {secondsRemaining}...", 1f, "White", triggeringPlayerId);
            }

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

        // Aborts charging, holding, or countdown and spools down the drive
        public void AbortJump(string reason = "FSD JUMP ABORTED")
        {
            var mainGrid = GridSystem?.MainGrid;
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
                PlaySound(WarpConstants.spoolDown);
            }

            HostDrive?.System?.SendMessage(reason, 5f, "Red", triggeringPlayerId);
        }

        // Ticked during the cooldown period after arriving
        private void UpdateCooldownTick()
        {
            cooldownElapsedTicks++;
            if (cooldownElapsedTicks >= cooldownTotalTicks)
            {
                cooldownElapsedTicks = 0;
                State = HyperState.Idle;
                HostDrive?.System?.SendMessage("FSD READY", 3f, "White", triggeringPlayerId);
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
                    var allGrids = GridSystem?.Grids;
                    if (allGrids != null && allGrids.Count > 0)
                    {
                        foreach (var g in allGrids)
                        {
                            if (g == null || g.MarkedForClose) continue;
                            foreach (var b in g.GetFatBlocks())
                            {
                                var p = b as IMyPowerProducer;
                                if (p != null && !p.Closed && p.IsFunctional && !p.Enabled)
                                    p.Enabled = true;
                            }
                        }
                    }
                    else if (GridSystem?.MainGrid != null)
                    {
                        foreach (var b in GridSystem.MainGrid.GetFatBlocks())
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

        // Emergency drop out of Hyperspace transit when power or FSD drive is cut
        public void EmergencyDrop(string reason = "EMERGENCY DROP - CONDUIT COLLAPSED")
        {
            var mainGrid = GridSystem?.MainGrid;
            if (mainGrid == null) return;

            State = HyperState.Cooldown;
            cooldownElapsedTicks = 0;
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
                SpawnJumpArrivalFX();
                PlaySound(WarpConstants.EmergencyDropSound);
            }

            // 2. Forcibly disable all power producers on the ship for 5 seconds
            long restoreTimeTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(5).Ticks;
            var grids = GridSystem?.Grids;
            if (grids != null && grids.Count > 0)
            {
                foreach (var g in grids)
                {
                    if (g == null || g.MarkedForClose) continue;
                    foreach (var block in g.GetFatBlocks())
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
            emergencyPowerDisableTicks = 5 * 60; // 5 seconds (300 ticks)

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
            var mainGrid = GridSystem?.MainGrid;
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
            Vector3D safeDest;
            double safeDist;
            if (TryAdjustDestinationForObstacles(JumpOrigin, JumpDirection, JumpDistance, out safeDest, out safeDist))
            {
                JumpDestination = safeDest;
                JumpDistance = safeDist;
            }

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
            float allowInGravityMax = HostDrive?.Settings?.AllowInGravityMax ?? 0.2f;
            if ((destGravity.Length() / WarpSystem.EARTH_GRAVITY) > allowInGravityMax)
            {
                AbortJump("DESTINATION INSIDE PLANETARY GRAVITY WELL!");
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

            // 3. Audio & Visuals — stop countdown Prototech particles, spawn WarpTunnel and origin Jump FX
            StopChargeParticles();
            StartTunnelParticle();

            var iGrid = mainGrid as IMyCubeGrid;
            float gridWidth = iGrid != null && iGrid.LocalAABB.Width > iGrid.LocalAABB.Height ? iGrid.LocalAABB.Width : (iGrid != null ? iGrid.LocalAABB.Height : 10f);
            float scale = (mainGrid.GridSizeEnum == MyCubeSize.Large)
                ? MathHelper.Clamp(gridWidth / 15f, 1.5f, 5.0f)
                : MathHelper.Clamp(gridWidth / 8f, 0.8f, 2.5f);
            double avgSpeedMps = JumpDistance / Math.Max(0.5, (double)JourneyDuration);
            _triggeredArrivalLeadFX = false;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                SpawnJumpOriginFX(scale, (float)avgSpeedMps);
                PlayJumpOriginSound(IsPrototechDrive ? WarpConstants.Hyperspace_jump : WarpConstants.Hyperspace_jump);
            }

            if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
            {
                var fxMsg = new HyperspaceFXMessage
                {
                    Position = JumpOrigin != Vector3D.Zero ? JumpOrigin : mainGrid.WorldMatrix.Translation,
                    Direction = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : mainGrid.WorldMatrix.Forward,
                    Scale = scale,
                    Speed = (float)avgSpeedMps,
                    IsArrival = false,
                    IsArrivalLead = false,
                    IsPrototech = IsPrototechDrive
                };
                byte[] fxData = MyAPIGateway.Utilities.SerializeToBinary(fxMsg);
                MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.hyperspaceFXPacketId, fxData);
            }
        }



        // Ticked every frame while State == HyperState.Active. Smoothly micro-teleports the grid along the conduit
        public void UpdateTransitTick()
        {
            var mainGrid = GridSystem?.MainGrid;
            if (mainGrid == null || State != HyperState.Active) return;

            // --- Power & FSD loss detection with debounced grace period ---
            bool fsdIssue = HostDrive == null || HostDrive.Block == null
                || !HostDrive.Block.Enabled
                || !HostDrive.Block.IsFunctional;

            if (fsdIssue)
                fsdLossTicks++;
            else
                fsdLossTicks = 0;

            if (transitElapsedTicks % 10 == 0)
            {
                float activePowerMW = 0f;
                var grids = GridSystem?.Grids;
                if (grids != null && grids.Count > 0)
                {
                    foreach (var g in grids)
                    {
                        if (g == null || g.MarkedForClose) continue;
                        foreach (var block in g.GetFatBlocks())
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
            if (transitElapsedTicks % 10 == 0)
            {
                float localGrav;
                Vector3D naturalGrav = MyAPIGateway.Physics.CalculateNaturalGravityAt(currentPos, out localGrav);
                float allowInGravityMax = HostDrive?.Settings?.AllowInGravityMax ?? 0.2f;
                if ((naturalGrav.Length() / WarpSystem.EARTH_GRAVITY) > allowInGravityMax)
                {
                    EmergencyDrop("GRAVITY WELL EXCLUSION ZONE - EMERGENCY DROP!");
                    return;
                }
            }

            // Calculate current instantaneous speed in km/s (reflects 0.5s accel and decel curves)
            CurrentTransitSpeed = speedMps / 1000.0;

            // Apply micro-teleportation while strictly locking grid orientation every frame
            MatrixD currentMatrix = JumpOrientation;
            currentMatrix.Translation = currentPos;

            if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
            {
                mainGrid.Teleport(currentMatrix);
            }

            // Strictly disallow any rotation or linear physics movement during transit every tick
            var connectedGrids = GridSystem?.Grids;
            if (connectedGrids != null)
            {
                foreach (var g in connectedGrids)
                {
                    if (g?.Physics != null)
                    {
                        g.Physics.LinearVelocity = Vector3.Zero;
                        g.Physics.AngularVelocity = Vector3.Zero;
                    }
                }
            }
            else if (mainGrid.Physics != null)
            {
                mainGrid.Physics.LinearVelocity = Vector3.Zero;
                mainGrid.Physics.AngularVelocity = Vector3.Zero;
            }

            // Client-side effects
            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                bool isLocalPlayerOnboard = IsLocalPlayerOnboard();

                if (isLocalPlayerOnboard)
                {
                    // Player is riding the ship: glue audio anchor to camera position so extreme speeds never outrun the sound
                    Vector3D soundAnchor = (MyAPIGateway.Session?.Camera != null)
                        ? MyAPIGateway.Session.Camera.Position
                        : currentPos;

                    sound?.SetPosition(soundAnchor);

                    if (transitElapsedTicks == 1)
                    {
                        InitTransitSound();
                        if (transitSound != null)
                        {
                            transitSound.SetPosition(soundAnchor);
                            transitSound.SetVelocity(Vector3.Zero);
                            transitSound.CanPlayLoopSounds = true;
                            transitSound.PlaySound(WarpConstants.inHyperSpace, true);
                            transitSound.VolumeMultiplier = 1f;
                        }
                    }
                    else if (transitElapsedTicks > 1 && transitSound != null)
                    {
                        // Keep emitter glued to listener with zero velocity to eliminate Doppler cutoff
                        transitSound.SetPosition(soundAnchor);
                        transitSound.SetVelocity(Vector3.Zero);
                    }

                    // Keep WarpTunnel active throughout the journey until we exit into real space
                    UpdateTunnelParticle();

                    // Render client-side sky dome centered on camera to completely block realspace
                    DrawHyperspaceBackdrop();

                    // Hyperspace turbulence shake — SE handles damping/feel internally
                    var activeCockpit = GridSystem?.FindMainCockpit() as MyCockpit;
                    if (activeCockpit != null)
                        activeCockpit.AddShake(HYPERSPACE_SHAKE);
                }
                else
                {
                    // Player is not onboard (left the cockpit, stepped out, or watched from outside):
                    // Stop the internal hyperspace tunnel sound and corridor particle immediately!
                    if (transitSound != null && transitSound.IsPlaying)
                    {
                        transitSound.StopSound(true);
                    }
                    StopTunnelParticle();
                }

            }

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

                if (!MyAPIGateway.Utilities.IsDedicated)
                {
                    SpawnJumpArrivalLeadFX(JumpDestination, JumpDirection, scale, (float)avgTransitSpeedMps, IsPrototechDrive);
                }

                if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
                {
                    var fxMsg = new HyperspaceFXMessage
                    {
                        Position = JumpDestination != Vector3D.Zero ? JumpDestination : mainGrid.WorldMatrix.Translation,
                        Direction = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : mainGrid.WorldMatrix.Forward,
                        Scale = scale,
                        Speed = (float)avgTransitSpeedMps,
                        IsArrival = true,
                        IsArrivalLead = true,
                        IsPrototech = IsPrototechDrive
                    };
                    byte[] fxData = MyAPIGateway.Utilities.SerializeToBinary(fxMsg);
                    MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.hyperspaceFXPacketId, fxData);
                }
            }

            // Check if journey is complete (t >= 1.0)
            if (transitElapsedTicks >= transitTotalTicks)
            {
                ExitHyperspace();
                return;
            }

            // Emergency drop if no player or remote control is active on the ship during transit (debounced)
            if (!HasSeatedPilot())
            {
                controlLossTicks++;
            }
            else
            {
                controlLossTicks = 0;
            }

            if (controlLossTicks >= 30)
            {
                EmergencyDrop("CONTROL LOST - EMERGENCY DROP!");
                return;
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

        // Drops out of Hyperspace at JumpDestination
        public void ExitHyperspace()
        {
            var mainGrid = GridSystem?.MainGrid;
            if (mainGrid == null) return;

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

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                SpawnJumpArrivalFX();
                PlayJumpArrivalSound(IsPrototechDrive ? WarpConstants.PrototechJumpOutSound : WarpConstants.jumpOutSound);
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
                    Position = JumpDestination != Vector3D.Zero ? JumpDestination : mainGrid.WorldMatrix.Translation,
                    Direction = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : mainGrid.WorldMatrix.Forward,
                    Scale = scale,
                    Speed = (float)avgSpeedExitMps,
                    IsArrival = true,
                    IsArrivalLead = false,
                    IsPrototech = IsPrototechDrive
                };
                byte[] fxData = MyAPIGateway.Utilities.SerializeToBinary(fxMsg);
                MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.hyperspaceFXPacketId, fxData);
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
            State = HyperState.Cooldown;
            HostDrive.System.SendMessage("ARRIVED AT TARGET", 5f, "White");
        }

        // Player helpers - Character.Save suppression (borrowed from WarpSystem)
        private void CollectPlayersOnGrid()
        {
            _playersInTransit.Clear();
            var grids = GridSystem?.Grids;
            if (grids == null || grids.Count == 0) return;

            // Build a set of character entity IDs seated in cockpits/cryo on this grid group
            var seatedEntityIds = new List<long>();
            foreach (var grid in grids)
            {
                foreach (var block in grid.GetFatBlocks())
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
            if (seatedBlock != null && GridSystem?.Grids != null)
            {
                var playerGrid = seatedBlock.CubeGrid;
                foreach (var g in GridSystem.Grids)
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
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.MainGrid == null) return;
            var mainGrid = GridSystem.MainGrid;
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
            if (chargeParticle == null || chargeParticle.IsStopped || GridSystem?.MainGrid == null) return;
            var mainGrid = GridSystem.MainGrid;
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
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.MainGrid == null) return;
            StopTunnelParticle();

            // Strictly only spawn the interior Hyperspace conduit tunnel on the ship the local player is currently riding!
            if (!IsLocalPlayerOnboard()) return;

            var mainGrid = GridSystem.MainGrid;
            MatrixD gridMatrix = mainGrid.WorldMatrix;
            Vector3D forward = JumpDirection;
            Vector3D origin = mainGrid.PositionComp.WorldAABB.Center;

            double halfExtents = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
            double forwardOffset = Math.Max(halfExtents * 6.0, 145.0);
            Vector3D effectOffset = forward * forwardOffset;
            Vector3D frontOrigin = origin + effectOffset;
            Vector3D rearOrigin = origin - effectOffset;

            MatrixD frontDir = MatrixD.CreateWorld(frontOrigin, -forward, gridMatrix.Up);
            MatrixD rearDir = MatrixD.CreateWorld(rearOrigin, forward, gridMatrix.Up);

            var iGrid = mainGrid as IMyCubeGrid;
            float gridWidth = iGrid.LocalAABB.Width > iGrid.LocalAABB.Height ? iGrid.LocalAABB.Width : iGrid.LocalAABB.Height;
            
            string effectName = "WarpTunnel_M";
            if (mainGrid.GridSizeEnum == MyCubeSize.Small || gridWidth < 22f)
                effectName = "WarpTunnel_S";
            else if (gridWidth >= 70f)
                effectName = "WarpTunnel_L";

            float scale = 1.0f;
            if (effectName == "WarpTunnel_S")
                scale = MathHelper.Clamp(gridWidth / 8f, 2.2f, 4.5f);
            else if (effectName == "WarpTunnel_M")
                scale = MathHelper.Clamp(gridWidth / 13f, 3.5f, 7.5f);
            else
                scale = MathHelper.Clamp(gridWidth / 22f, 5.0f, 12.0f);

            // Front tunnel cone
            if (!MyParticlesManager.TryCreateParticleEffect(effectName, ref frontDir, ref frontOrigin, uint.MaxValue, out tunnelParticle))
            {
                MyParticlesManager.TryCreateParticleEffect("WarpTunnel", ref frontDir, ref frontOrigin, uint.MaxValue, out tunnelParticle);
            }
            if (tunnelParticle != null)
                tunnelParticle.UserScale = scale;

            // Rear mirrored tunnel cone (connects with front cone to form a complete bubble)
            if (!MyParticlesManager.TryCreateParticleEffect(effectName, ref rearDir, ref rearOrigin, uint.MaxValue, out tunnelParticleRear))
            {
                MyParticlesManager.TryCreateParticleEffect("WarpTunnel", ref rearDir, ref rearOrigin, uint.MaxValue, out tunnelParticleRear);
            }
            if (tunnelParticleRear != null)
                tunnelParticleRear.UserScale = scale;
        }

        private void UpdateTunnelParticle()
        {
            if (GridSystem?.MainGrid == null) return;
            var mainGrid = GridSystem.MainGrid;
            MatrixD gridMatrix = mainGrid.WorldMatrix;
            Vector3D forward = JumpDirection;
            Vector3D origin = mainGrid.PositionComp.WorldAABB.Center;

            double halfExtents = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
            double forwardOffset = Math.Max(halfExtents * 6.0, 145.0);
            Vector3D effectOffset = forward * forwardOffset;
            Vector3D frontOrigin = origin + effectOffset;
            Vector3D rearOrigin = origin - effectOffset;

            if (tunnelParticle != null && !tunnelParticle.IsStopped)
            {
                MatrixD frontDir = MatrixD.CreateWorld(frontOrigin, -forward, gridMatrix.Up);
                tunnelParticle.WorldMatrix = frontDir;
            }

            if (tunnelParticleRear != null && !tunnelParticleRear.IsStopped)
            {
                MatrixD rearDir = MatrixD.CreateWorld(rearOrigin, forward, gridMatrix.Up);
                tunnelParticleRear.WorldMatrix = rearDir;
            }
        }

        private void StopTunnelParticle()
        {
            if (tunnelParticle != null)
            {
                tunnelParticle.StopEmitting(5f);
                tunnelParticle = null;
            }
            if (tunnelParticleRear != null)
            {
                tunnelParticleRear.StopEmitting(5f);
                tunnelParticleRear = null;
            }
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

        private void DrawHyperspaceBackdrop()
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.MainGrid == null)
                return;

            try
            {
                var controller = MyAPIGateway.Session?.CameraController;
                if (controller == null)
                    return;

                // Allow cockpits, characters on the ship, and camera blocks (nose camera, docking cams, etc.)
                bool isValidView = (controller is IMyCockpit) || (controller is IMyCharacter) || (controller is Sandbox.ModAPI.Ingame.IMyCameraBlock);
                if (!isValidView)
                {
                    var cubeBlock = controller as VRage.Game.ModAPI.IMyCubeBlock;
                    if (cubeBlock != null && GridSystem.Grids != null)
                    {
                        var playerGrid = cubeBlock.CubeGrid;
                        foreach (var g in GridSystem.Grids)
                        {
                            if (g != null && g.EntityId == playerGrid.EntityId)
                            {
                                isValidView = true;
                                break;
                            }
                        }
                    }
                }

                if (!isValidView)
                    return;

                var mainGrid = GridSystem.MainGrid;
                MatrixD gridMatrix = mainGrid.WorldMatrix;
                Vector3D center = mainGrid.PositionComp.WorldAABB.Center;

                double halfExtents = mainGrid.PositionComp.WorldAABB.HalfExtents.AbsMax();
                double D = Math.Max(halfExtents * 12.0, 600.0);

                Vector3D f = gridMatrix.Forward * D;
                Vector3D r = gridMatrix.Right * D;
                Vector3D u = gridMatrix.Up * D;

                Vector4 color = SkyColorVec4;

                // 1. Forward Face
                MyQuadD qFwd;
                qFwd.Point0 = (center + f) - r - u;
                qFwd.Point1 = (center + f) + r - u;
                qFwd.Point2 = (center + f) + r + u;
                qFwd.Point3 = (center + f) - r + u;
                Vector3D nFwd = -gridMatrix.Forward;
                MyTransparentGeometry.AddQuad(SkyMaterial, ref qFwd, color, ref nFwd);

                // 2. Backward Face
                MyQuadD qBack;
                qBack.Point0 = (center - f) + r - u;
                qBack.Point1 = (center - f) - r - u;
                qBack.Point2 = (center - f) - r + u;
                qBack.Point3 = (center - f) + r + u;
                Vector3D nBack = gridMatrix.Forward;
                MyTransparentGeometry.AddQuad(SkyMaterial, ref qBack, color, ref nBack);

                // 3. Left Face
                MyQuadD qLeft;
                qLeft.Point0 = (center - r) - f - u;
                qLeft.Point1 = (center - r) + f - u;
                qLeft.Point2 = (center - r) + f + u;
                qLeft.Point3 = (center - r) - f + u;
                Vector3D nLeft = gridMatrix.Right;
                MyTransparentGeometry.AddQuad(SkyMaterial, ref qLeft, color, ref nLeft);

                // 4. Right Face
                MyQuadD qRight;
                qRight.Point0 = (center + r) + f - u;
                qRight.Point1 = (center + r) - f - u;
                qRight.Point2 = (center + r) - f + u;
                qRight.Point3 = (center + r) + f + u;
                Vector3D nRight = -gridMatrix.Right;
                MyTransparentGeometry.AddQuad(SkyMaterial, ref qRight, color, ref nRight);

                // 5. Top Face
                MyQuadD qTop;
                qTop.Point0 = (center + u) - r - f;
                qTop.Point1 = (center + u) + r - f;
                qTop.Point2 = (center + u) + r + f;
                qTop.Point3 = (center + u) - r + f;
                Vector3D nTop = -gridMatrix.Up;
                MyTransparentGeometry.AddQuad(SkyMaterial, ref qTop, color, ref nTop);

                // 6. Bottom Face
                MyQuadD qBottom;
                qBottom.Point0 = (center - u) - r + f;
                qBottom.Point1 = (center - u) + r + f;
                qBottom.Point2 = (center - u) + r - f;
                qBottom.Point3 = (center - u) - r - f;
                Vector3D nBottom = gridMatrix.Up;
                MyTransparentGeometry.AddQuad(SkyMaterial, ref qBottom, color, ref nBottom);
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] DrawHyperspaceBackdrop error: " + ex);
            }
        }

        private void SpawnJumpOriginFX(float scale, float speedMps = 0f)
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.MainGrid == null) return;
            try
            {
                var mainGrid = GridSystem.MainGrid;
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

        private void SpawnJumpArrivalFX()
        {
            if (MyAPIGateway.Utilities.IsDedicated || GridSystem?.MainGrid == null) return;

            try
            {
                var mainGrid = GridSystem.MainGrid;
                var iGrid = mainGrid as IMyCubeGrid;
                Vector3D direction = JumpDirection;
                if (direction.LengthSquared() < 0.001)
                    direction = mainGrid.WorldMatrix.Forward;

                Vector3D destination = JumpDestination;
                if (destination == Vector3D.Zero)
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
                emitter.PlaySound(WarpConstants.Hyperspace_jump, true);
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
                emitter.PlaySound(isPrototech ? WarpConstants.PrototechJumpOutSound : WarpConstants.jumpOutSound, true);
            }
            catch (Exception ex)
            {
                MyLog.Default.Error("[Hyperspace] SpawnGlobalArrivalFX error: " + ex);
            }
        }

        // Draws the 5.0-degree Yellow Warfare Targeting Alignment Reticle and 3D distance display
        public void DrawTargetReticle()
        {
            if (MyAPIGateway.Utilities.IsDedicated || MyAPIGateway.Session?.Camera == null)
                return;

            var mainGrid = GridSystem?.MainGrid;
            var cockpit = GridSystem?.FindMainCockpit();
            if (mainGrid == null || cockpit == null) return;

            // Only draw if local player is actively piloting/controlling this grid (Cockpit or Remote Control)
            var localPlayer = MyAPIGateway.Session.Player;
            if (localPlayer == null) return;
            var controlledBlock = (localPlayer.Controller?.ControlledEntity?.Entity as VRage.Game.ModAPI.IMyCubeBlock)
                               ?? (localPlayer.Character?.Parent as VRage.Game.ModAPI.IMyCubeBlock);
            if (controlledBlock == null || controlledBlock.CubeGrid != mainGrid) return;

            // Never draw reticle while inside hyperspace transit or on cooldown
            if (State == HyperState.Active || State == HyperState.Cooldown)
                return;

            bool isGps = Mode == JumpMode.GpsWaypoint && SelectedGpsCoords.HasValue;
            bool isChargingOrHolding = State == HyperState.Charging || State == HyperState.HoldingCharge || State == HyperState.Countdown || IsFleetHolding;

            var lobby = FleetJumpSystem.GetLobbyForGrid(mainGrid.EntityId);
            bool isFleetPreJump = lobby != null && !lobby.IsExecuting;

            // Reticle is visible if:
            // 1. A GPS waypoint is locked (visible before jump so pilot can align)
            // 2. Grid is part of a pre-jump Fleet Jump lobby (shows fleet target/vector before jump)
            // 3. Actively charging / holding charge / counting down
            if (!isGps && !isFleetPreJump && !isChargingOrHolding)
                return;

            Vector3D cameraPos = MyAPIGateway.Session.Camera.Position;
            MatrixD cameraMatrix = MyAPIGateway.Session.Camera.WorldMatrix;

            Vector3D targetDir = Vector3D.Zero;
            double targetDist = 0.0;

            if (isGps && SelectedGpsCoords.HasValue)
            {
                Vector3D gpsCoord = SelectedGpsCoords.Value;
                targetDist = Vector3D.Distance(cameraPos, gpsCoord);
                targetDir = Vector3D.Normalize(gpsCoord - cameraPos);
            }
            else if (isFleetPreJump || isChargingOrHolding)
            {
                if (lobby != null)
                {
                    if (lobby.Mode == JumpMode.GpsWaypoint && lobby.TargetGpsCoords.HasValue)
                    {
                        targetDist = Vector3D.Distance(cameraPos, lobby.TargetGpsCoords.Value);
                        targetDir = Vector3D.Normalize(lobby.TargetGpsCoords.Value - cameraPos);
                    }
                    else
                    {
                        targetDir = lobby.DepartureOrientation.Forward;
                        targetDist = lobby.LeaderJumpDistance > 0 ? lobby.LeaderJumpDistance : JumpDistance;
                    }
                }
                else
                {
                    targetDir = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : cockpit.WorldMatrix.Forward;
                    targetDist = JumpDistance;
                }
            }
            else
            {
                targetDir = JumpDirection.LengthSquared() > 0.001 ? JumpDirection : cockpit.WorldMatrix.Forward;
                targetDist = JumpDistance;
            }

            if (targetDir.LengthSquared() < 0.001) return;

            // Check if target direction is in front of camera
            if (Vector3D.Dot(cameraMatrix.Forward, targetDir) <= 0.05)
                return;

            // Reticle distance in front of camera
            const double HUD_DIST = 60.0;
            Vector3D centerPos = cameraPos + (targetDir * HUD_DIST);

            // Inner radius corresponds EXACTLY to ALIGNMENT_TOLERANCE_DEG (5.0 degrees)
            float innerRadius = (float)(HUD_DIST * Math.Tan(MathHelper.ToRadians(ALIGNMENT_TOLERANCE_DEG)));
            float outerRadius = innerRadius * 1.20f;

            // Calculate current ship alignment angle
            double dot = MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Forward, targetDir), -1.0, 1.0);
            double angleDeg = MathHelper.ToDegrees(Math.Acos(dot));
            bool isAligned = angleDeg <= ALIGNMENT_TOLERANCE_DEG;

            // Outer ring color (HUD Yellow / Lime when locked)
            Vector4 outerColor = isAligned 
                ? new Vector4(0.85f, 1.0f, 0.20f, 0.95f) 
                : new Vector4(1.0f, 0.90f, 0.15f, 0.85f);

            // Inner tolerance ring color (Warm Amber-Gold / Electric Lime when locked)
            Vector4 innerColor = isAligned
                ? new Vector4(0.90f, 1.0f, 0.35f, 0.95f)
                : new Vector4(1.0f, 0.72f, 0.08f, 0.65f);

            // Setup orthogonal billboard basis facing camera
            Vector3D reticleFwd = targetDir;
            Vector3D reticleUp = cameraMatrix.Up;
            Vector3D reticleRight = Vector3D.Cross(reticleFwd, reticleUp);
            if (reticleRight.LengthSquared() < 0.001) reticleRight = cameraMatrix.Right;
            reticleRight.Normalize();
            reticleUp = Vector3D.Cross(reticleRight, reticleFwd);
            reticleUp.Normalize();

            const float LINE_THICKNESS = 0.06f;

            // 1. Draw Inner 5.0° Tolerance Circle (smooth 24-segment ring in Amber-Gold)
            const int INNER_SEGMENTS = 24;
            float inStep = MathHelper.TwoPi / INNER_SEGMENTS;
            for (int i = 0; i < INNER_SEGMENTS; i++)
            {
                float a1 = i * inStep;
                float a2 = (i + 1) * inStep;
                Vector3D p0 = centerPos + (reticleRight * (Math.Cos(a1) * innerRadius)) + (reticleUp * (Math.Sin(a1) * innerRadius));
                Vector3D p1 = centerPos + (reticleRight * (Math.Cos(a2) * innerRadius)) + (reticleUp * (Math.Sin(a2) * innerRadius));
                MyTransparentGeometry.AddLineBillboard(SkyMaterial, innerColor, p0, (Vector3)(p1 - p0), 1f, LINE_THICKNESS * 0.75f);
            }

            // 2. Draw Outer 12-dash circular brackets (24 segments total)
            const int DASH_COUNT = 12;
            float stepAngle = MathHelper.TwoPi / DASH_COUNT;
            float dashFraction = 0.65f;

            for (int i = 0; i < DASH_COUNT; i++)
            {
                float a1 = i * stepAngle;
                float a2 = a1 + (stepAngle * dashFraction);
                float mid = (a1 + a2) * 0.5f;

                Vector3D p0 = centerPos + (reticleRight * (Math.Cos(a1) * outerRadius)) + (reticleUp * (Math.Sin(a1) * outerRadius));
                Vector3D p1 = centerPos + (reticleRight * (Math.Cos(mid) * outerRadius)) + (reticleUp * (Math.Sin(mid) * outerRadius));
                Vector3D p2 = centerPos + (reticleRight * (Math.Cos(a2) * outerRadius)) + (reticleUp * (Math.Sin(a2) * outerRadius));

                MyTransparentGeometry.AddLineBillboard(SkyMaterial, outerColor, p0, (Vector3)(p1 - p0), 1f, LINE_THICKNESS);
                MyTransparentGeometry.AddLineBillboard(SkyMaterial, outerColor, p1, (Vector3)(p2 - p1), 1f, LINE_THICKNESS);
            }

            // 3. Draw 4 outer crosshair tick marks (Warfare style)
            float innerTick = outerRadius * 1.06f;
            float outerTick = outerRadius * 1.22f;

            // Top
            Vector3D tTop0 = centerPos + (reticleUp * innerTick);
            Vector3D tTop1 = centerPos + (reticleUp * outerTick);
            MyTransparentGeometry.AddLineBillboard(SkyMaterial, outerColor, tTop0, (Vector3)(tTop1 - tTop0), 1f, LINE_THICKNESS);

            // Bottom
            Vector3D tBot0 = centerPos - (reticleUp * innerTick);
            Vector3D tBot1 = centerPos - (reticleUp * outerTick);
            MyTransparentGeometry.AddLineBillboard(SkyMaterial, outerColor, tBot0, (Vector3)(tBot1 - tBot0), 1f, LINE_THICKNESS);

            // Right
            Vector3D tRight0 = centerPos + (reticleRight * innerTick);
            Vector3D tRight1 = centerPos + (reticleRight * outerTick);
            MyTransparentGeometry.AddLineBillboard(SkyMaterial, outerColor, tRight0, (Vector3)(tRight1 - tRight0), 1f, LINE_THICKNESS);

            // Left
            Vector3D tLeft0 = centerPos - (reticleRight * innerTick);
            Vector3D tLeft1 = centerPos - (reticleRight * outerTick);
            MyTransparentGeometry.AddLineBillboard(SkyMaterial, outerColor, tLeft0, (Vector3)(tLeft1 - tLeft0), 1f, LINE_THICKNESS);

            // Subtle center crosshair (+)
            float centerTick = innerRadius * 0.12f;
            Vector3D cTop = centerPos + (reticleUp * centerTick);
            Vector3D cBot = centerPos - (reticleUp * centerTick);
            Vector3D cR = centerPos + (reticleRight * centerTick);
            Vector3D cL = centerPos - (reticleRight * centerTick);
            MyTransparentGeometry.AddLineBillboard(SkyMaterial, outerColor, cBot, (Vector3)(cTop - cBot), 1f, LINE_THICKNESS * 0.70f);
            MyTransparentGeometry.AddLineBillboard(SkyMaterial, outerColor, cL, (Vector3)(cR - cL), 1f, LINE_THICKNESS * 0.70f);

            // 4. Draw clean distance text underneath the reticle
            string distText;
            if (targetDist < 1000.0)
                distText = $"{targetDist:F1}m";
            else if (targetDist < 1000000.0)
                distText = $"{(targetDist / 1000.0):F1}km";
            else
                distText = $"{(targetDist / 1000000.0):F1}Mm";

            Vector3D textCenterPos = centerPos - (reticleUp * (outerRadius * 1.50f));
            Draw3DStrokeText(distText, textCenterPos, reticleRight, reticleUp, outerColor, innerRadius * 0.22f);
        }

        // Renders proportional vector stroke text in 3D world space directly on HUD plane
        private void Draw3DStrokeText(string text, Vector3D origin, Vector3D right, Vector3D up, Vector4 color, float charHeight)
        {
            float baseWidth = charHeight * 0.58f;
            float thickness = charHeight * 0.08f;
            float charGap = charHeight * 0.16f;

            // Compute total proportional width
            float totalWidth = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                float w = GetCharWidth(text[i], baseWidth);
                totalWidth += w;
                if (i < text.Length - 1) totalWidth += charGap;
            }

            Vector3D curPos = origin - (right * (totalWidth * 0.5f));

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                float w = GetCharWidth(c, baseWidth);
                DrawStrokeChar(c, curPos, right, up, color, w, charHeight, thickness);
                curPos += right * (w + charGap);
            }
        }

        private float GetCharWidth(char c, float baseWidth)
        {
            switch (c)
            {
                case '.':
                    return baseWidth * 0.25f;
                case '1':
                    return baseWidth * 0.40f;
                case 'm':
                    return baseWidth * 1.15f;
                case 'M':
                    return baseWidth * 1.05f;
                case 'k':
                case 'K':
                    return baseWidth * 0.85f;
                default:
                    return baseWidth;
            }
        }

        private void DrawStrokeChar(char c, Vector3D pos, Vector3D right, Vector3D up, Vector4 color, float w, float h, float th)
        {
            Vector3D pBL = pos;
            Vector3D pBR = pos + (right * w);
            Vector3D pML = pos + (up * (h * 0.5f));
            Vector3D pMR = pML + (right * w);
            Vector3D pTL = pos + (up * h);
            Vector3D pTR = pTL + (right * w);

            Action<Vector3D, Vector3D> line = (pA, pB) =>
            {
                MyTransparentGeometry.AddLineBillboard(SkyMaterial, color, pA, (Vector3)(pB - pA), 1f, th);
            };

            switch (c)
            {
                case '0':
                    line(pBL, pTL); line(pTL, pTR); line(pTR, pBR); line(pBR, pBL); line(pBL, pTR);
                    break;
                case '1':
                    line(pos + (right * (w * 0.5f)), pos + (right * (w * 0.5f)) + (up * h));
                    break;
                case '2':
                    line(pTL, pTR); line(pTR, pMR); line(pMR, pML); line(pML, pBL); line(pBL, pBR);
                    break;
                case '3':
                    line(pTL, pTR); line(pTR, pBR); line(pBR, pBL); line(pML, pMR);
                    break;
                case '4':
                    line(pTL, pML); line(pML, pMR); line(pTR, pBR);
                    break;
                case '5':
                    line(pTR, pTL); line(pTL, pML); line(pML, pMR); line(pMR, pBR); line(pBR, pBL);
                    break;
                case '6':
                    line(pTR, pTL); line(pTL, pBL); line(pBL, pBR); line(pBR, pMR); line(pMR, pML);
                    break;
                case '7':
                    line(pTL, pTR); line(pTR, pBL);
                    break;
                case '8':
                    line(pTL, pTR); line(pTR, pBR); line(pBR, pBL); line(pBL, pTL); line(pML, pMR);
                    break;
                case '9':
                    line(pBL, pBR); line(pBR, pTR); line(pTR, pTL); line(pTL, pML); line(pML, pMR);
                    break;
                case '.':
                    // Clean dot at bottom
                    line(pos + (right * (w * 0.5f)), pos + (right * (w * 0.5f)) + (up * (h * 0.12f)));
                    break;
                case 'k':
                case 'K':
                    line(pBL, pTL);
                    line(pML, pos + (right * w) + (up * (h * 0.85f)));
                    line(pML, pBR);
                    break;
                case 'm':
                    // Clean lowercase 'm' with 3 vertical legs and top bridge
                    float mh = h * 0.62f;
                    Vector3D mMidB = pos + (right * (w * 0.5f));
                    Vector3D mMidT = mMidB + (up * mh);
                    Vector3D mLT = pos + (up * mh);
                    Vector3D mRT = pos + (right * w) + (up * mh);
                    line(pos, mLT);
                    line(mLT, mRT);
                    line(mMidB, mMidT);
                    line(pos + (right * w), mRT);
                    break;
                case 'M':
                    line(pBL, pTL);
                    line(pTL, pos + (right * (w * 0.5f)) + (up * (h * 0.35f)));
                    line(pos + (right * (w * 0.5f)) + (up * (h * 0.35f)), pTR);
                    line(pTR, pBR);
                    break;
            }
        }
    }
}