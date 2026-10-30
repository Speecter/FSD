using System;
using System.Globalization;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    public enum HudDrive
    {
        None,
        Supercruise,
        Hyperspace,
        Capital
    }

    public enum HudPhase
    {
        Idle,
        Spool,          // supercruise charging
        SpoolDelayed,   // supercruise charging, held back by hostiles
        Hold,           // charged / waiting for the nose to be on the target
        Charging,
        Countdown,
        Engaging,       // capital: riser and wormhole entry
        Transit,
        Exiting,        // capital: wormhole exit
        Cooldown,
        Hyperdicted,
        Cruise,         // supercruise active
        FleetHold       // charged, holding in formation until the leader engages
    }

    public static class HudFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Distance(double meters)
        {
            if (meters < 1000.0)
                return ((int)meters).ToString(Inv) + " m";

            double km = meters / 1000.0;
            if (km < 100.0)
                return km.ToString("0.0", Inv) + " km";
            if (km < 10000.0)
                return ((int)km).ToString(Inv) + " km";
            return (km / 1000.0).ToString("0.00", Inv) + " Mm";
        }

        /// <summary>Whole kilometres with thousands separators, so a fast-moving distance reads as a rolling counter.</summary>
        public static string DistanceExact(double meters)
        {
            if (meters < 1000.0)
                return ((int)meters).ToString(Inv) + " m";

            return ((long)(meters / 1000.0)).ToString("N0", Inv) + " km";
        }

        /// <summary>Seconds to a hundredth under a minute (the last digit rolls), minutes:seconds above.</summary>
        public static string SecondsPrecise(float seconds)
        {
            if (seconds >= 60f)
                return Seconds(seconds);
            return seconds.ToString("0.00", Inv) + " s";
        }

        public static string Speed(double metersPerSecond)
        {
            if (metersPerSecond < 1000.0)
                return ((int)metersPerSecond).ToString(Inv) + " m/s";
            if (metersPerSecond < 10000.0)
                return (metersPerSecond / 1000.0).ToString("0.00", Inv) + " km/s";
            return (metersPerSecond / 1000.0).ToString("0.0", Inv) + " km/s";
        }

        public static string Seconds(float seconds)
        {
            if (seconds >= 60f)
                return ((int)(seconds / 60f)).ToString(Inv) + ":" + ((int)seconds % 60).ToString("00", Inv);
            return seconds.ToString("0.0", Inv) + " s";
        }

        public static string Percent(float fraction)
        {
            return ((int)(MathHelper.Clamp(fraction, 0f, 1f) * 100f + 0.5f)).ToString(Inv) + "%";
        }

        public static string Degrees(float degrees)
        {
            return degrees.ToString("0.0", Inv) + "°";
        }

        public static string Number(float value, string format)
        {
            return value.ToString(format, Inv);
        }
    }

    /// <summary>One frame's worth of everything the HUD shows for the ship the local player is in, read from whichever FSD system is active.</summary>
    public class FsdHudModel
    {
        public HudDrive Drive;
        public HudPhase Phase;
        public string DriveName = "";
        public bool Prototech;

        // Phase timing
        public bool HasProgress;
        public float Progress;
        public bool HasSeconds;
        public float SecondsLeft;
        public int CountdownDigit;

        // Alignment (the nose has to be brought onto AlignDir)
        public bool HasAlign;
        public Vector3D AlignDir;
        public Vector3D AlignLocal;          // AlignDir in the cockpit's frame: x right, y up, -z forward
        public float AlignAngle;
        public float AlignTolerance = 5f;
        public float AlignBreak = 6.5f;
        public bool IsAligned { get { return HasAlign && AlignAngle <= AlignTolerance; } }

        // Target
        public bool HasTarget;
        public string TargetName = "";
        public double TargetDistance;

        // Travel
        public bool HasSpeed;
        public double SpeedMps;

        /// <summary>Hyperspace only: the "apparent" FSD speed shown instead of the real tunnel speed - huge and never quite still.</summary>
        public double ApparentSpeedKmps;

        /// <summary>0..1 how hard the hyperdiction is shaking the ship right now (drives the screen effect).</summary>
        public float HyperdictionIntensity;

        /// <summary>Camera-shake magnitude of the ship right now (0 outside transit, 1 = steady transit); the HUD shakes with it.</summary>
        public float ShakeMagnitude;

        /// <summary>Seed the camera shake uses for this drive, so the HUD gusts in step with the camera.</summary>
        public float ShakeSeed;

        /// <summary>Seconds since this transit began (a Hyperdictor episode does not restart it); 0 outside transit.</summary>
        public float TransitSeconds;

        /// <summary>The apparent speed climbs from 0 over this long when a transit begins (the camera's entry shake lasts as long).</summary>
        public const float EntrySurgeSeconds = 3f;

        /// <summary>The last seconds of a transit during which the HUD ramps up (the camera's exit shake rises over the same 5 s).</summary>
        public const float ArrivalRampSeconds = 5f;

        /// <summary>0..1 how far into the arrival ramp this transit is (0 until 5 s are left, 1 at arrival).</summary>
        public float ArrivalRamp
        {
            get
            {
                return Phase == HudPhase.Transit && HasSeconds ? MathHelper.Clamp(1f - SecondsLeft / ArrivalRampSeconds, 0f, 1f) : 0f;
            }
        }

        private bool transitClockRunning;
        private DateTime transitClockStart;

        private const double ApparentSpeedScale = 420.0;
        private const double ApparentJitter = 0.012;
        private readonly Random jitterRandom = new Random();
        private double apparentJitter = 1.0;
        private int jitterFrames;
        public bool InTransit;
        public float TravelProgress;

        // Supercruise gauge
        public float SpeedFraction;
        public float GravityFactor;

        // Capacitor (hyperspace drives only)
        public bool HasCapacitor;
        public float CapFraction;
        public double RangeMeters;
        public string Note = "";

        // Heat and power
        public bool HasHeat;
        public float HeatFraction;
        public bool HasPower;
        public float PowerMW;

        // Hyperdictor on this ship
        public bool HasHyperdictor;
        public HyperdictorState HyperdictorState;
        public float HyperdictorCharge;
        public int HyperdictorCooldownTicks;

        // Blind (manual distance) jump: the jump follows the nose, there is nothing to align to
        public bool BlindJump;

        // Supercruise assist: time to the locked point (to the 500 m reset radius) and how urgently the ship should slow down
        public bool HasCruiseEta;

        /// <summary>Supercruise with a locked target: 0 where the target was locked .. 1 at the target (for the main bar).</summary>
        public bool HasCruiseTrip;
        public float CruiseTripProgress;
        private Vector3D tripCoords;
        private double tripStartDistance;
        public float CruiseEtaSeconds;

        /// <summary>0 = fine, 1 = slow down soon, 2 = brake now (a heuristic from the time left and the speed, since each speed step takes the pilot a moment).</summary>
        public int BrakeLevel;

        // Selected GPS target, shown as a lock marker in the world
        public bool HasGpsLock;
        public Vector3D GpsCoords;
        public string GpsName = "";

        // Fleet
        public FleetJumpLobby Lobby;
        public bool IsFleetLeader;

        private float slowGravity;
        private float slowSpeedFraction;
        private float slowCapFraction;
        private double slowRange;
        private double slowManualDistance;
        private bool slowCapacitorLow;

        public string PhaseText()
        {
            switch (Phase)
            {
                case HudPhase.Spool: return "SPOOLING";
                case HudPhase.SpoolDelayed: return "SPOOL DELAYED - HOSTILES NEARBY";
                case HudPhase.Hold: return Drive == HudDrive.Capital ? "ALIGN TO TARGET" : "STANDBY - ALIGN TO TARGET";
                case HudPhase.Charging: return Drive == HudDrive.Capital && HasAlign && !IsAligned ? "CHARGING - ALIGN TO TARGET" : "CHARGING";
                case HudPhase.Countdown: return "ENGAGING";
                case HudPhase.Engaging: return "ENTERING HYPERSPACE";
                case HudPhase.Transit: return "IN HYPERSPACE";
                case HudPhase.Exiting: return "EXITING HYPERSPACE";
                case HudPhase.Cooldown: return "COOLING DOWN";
                case HudPhase.Hyperdicted: return "ERROR";   // the drive does not know what is happening to it
                case HudPhase.Cruise: return "SUPERCRUISE";
                case HudPhase.FleetHold: return IsFleetLeader ? "FLEET HOLD - FLEET JUMP TO ENGAGE" : "FLEET HOLD - AWAITING LEADER";
                default: return string.IsNullOrEmpty(Note) ? "READY" : Note;
            }
        }

        /// <param name="slow">True a few times per second: refreshes the values that cost a planet or mass lookup.</param>
        public bool Fill(MyCubeGrid grid, bool slow)
        {
            Drive = HudDrive.None;
            HasProgress = HasSeconds = HasAlign = HasTarget = HasSpeed = InTransit = HasCapacitor = HasHeat = HasPower = HasHyperdictor = false;
            HasCruiseTrip = false;
            HyperdictionIntensity = 0f;
            ShakeMagnitude = 0f;
            HasCruiseEta = false;
            CruiseEtaSeconds = 0f;
            BrakeLevel = 0;
            Lobby = null;
            IsFleetLeader = BlindJump = HasGpsLock = false;
            Note = "";
            Progress = SecondsLeft = TravelProgress = 0f;
            CountdownDigit = 0;
            AlignTolerance = 5f;
            AlignBreak = 6.5f;

            var session = WarpDriveSession.instance;
            WarpSystem ws = session != null ? session.GetGridWarpSystem(grid) : null;
            if (ws == null)
            {
                transitClockRunning = false;
                return false;
            }

            HyperspaceSystem hs = ws.hyperspace;
            if (hs != null && (hs.HostDrive == null || !hs.HostDrive.supportsHyperspace))
                hs = null;
            CapitalFSD cap = ws.capitalFSD;

            Prototech = ws.isPrototech;

            if (cap != null && cap.State != CapitalFSD.CapState.Idle)
                FillCapital(cap, grid);
            else if (hs != null && hs.State != HyperspaceSystem.HyperState.Idle)
                FillHyperspace(hs, grid, slow);
            else if (ws.warpState != WarpSystem.State.idle)
                FillSupercruise(ws, grid, slow);
            else if (hs != null)
                FillHyperspace(hs, grid, slow);
            else if (cap != null)
                FillCapital(cap, grid);
            else
                FillSupercruise(ws, grid, slow);

            if (Drive == HudDrive.None)
            {
                transitClockRunning = false;
                return false;
            }

            UpdateTransitClock();

            if (Phase == HudPhase.Transit || Phase == HudPhase.Hyperdicted || Phase == HudPhase.Exiting || Phase == HudPhase.Engaging)
                HasGpsLock = false;

            DriveName = Drive == HudDrive.Capital ? "CAPITAL FSD" : Drive == HudDrive.Hyperspace ? (Prototech ? "PROTOTECH FSD" : "FSD") : (Prototech ? "PROTOTECH FSD" : "FSD");

            // Heat is shared by every drive type
            if (ws.driveHeat > 0 || ws.warpState != WarpSystem.State.idle)
            {
                HasHeat = true;
                HeatFraction = MathHelper.Clamp(ws.driveHeat / 100f, 0f, 1f);
            }

            if (Drive == HudDrive.Supercruise && ws.warpState != WarpSystem.State.idle)
            {
                HasPower = true;
                PowerMW = ws.DisplayPowerMW;
            }

            var hd = HyperdictionSession.Instance != null ? HyperdictionSession.Instance.GetHyperdictorOnGrid(grid) : null;
            if (hd != null)
            {
                HasHyperdictor = true;
                HyperdictorState = hd.State;
                HyperdictorCharge = hd.StoredChargeRatio;
                HyperdictorCooldownTicks = hd.CooldownTicksRemaining;
            }

            return true;
        }

        private void UpdateTransitClock()
        {
            if (Phase != HudPhase.Transit && Phase != HudPhase.Hyperdicted)
            {
                transitClockRunning = false;
                TransitSeconds = 0f;
                return;
            }

            if (!transitClockRunning)
            {
                transitClockRunning = true;
                transitClockStart = DateTime.UtcNow;
            }
            TransitSeconds = (float)(DateTime.UtcNow - transitClockStart).TotalSeconds;

            // Entry surge: the speed readout accelerates from 0 instead of starting at full
            if (InTransit)
            {
                float t = MathHelper.Clamp(TransitSeconds / EntrySurgeSeconds, 0f, 1f);
                ApparentSpeedKmps *= t * t * (3f - 2f * t);
            }
        }

        // ------------------------------------------------------------------ supercruise

        private void FillSupercruise(WarpSystem ws, MyCubeGrid grid, bool slow)
        {
            Drive = HudDrive.Supercruise;

            switch (ws.warpState)
            {
                case WarpSystem.State.charging:
                    Phase = ws.SpoolDelayedByEnemy ? HudPhase.SpoolDelayed : HudPhase.Spool;
                    HasProgress = HasSeconds = true;
                    Progress = ws.SpoolProgress;
                    SecondsLeft = ws.SpoolSecondsLeft;
                    break;

                case WarpSystem.State.active:
                    Phase = HudPhase.Cruise;
                    HasSpeed = true;
                    SpeedMps = Math.Max(0.0, ws.currentSpeedPt * 60.0);

                    if (slow)
                    {
                        double t = ws.GetGravityInfluenceFactor();
                        double high = ws.GetCurrentHighDeckMps(t);
                        slowGravity = (float)t;
                        slowSpeedFraction = high > 1.0 ? (float)MathHelper.Clamp(SpeedMps / high, 0.0, 1.0) : 0f;
                    }
                    GravityFactor = slowGravity;
                    SpeedFraction = slowSpeedFraction;
                    break;

                default:
                    Phase = HudPhase.Idle;
                    break;
            }

            FillCruiseTarget(ws, grid);
        }

        /// <summary>The assist lock: marker, name and distance, and while cruising the time left and the braking advice.</summary>
        private void FillCruiseTarget(WarpSystem ws, MyCubeGrid grid)
        {
            Vector3D coords;
            string name;
            if (!CruiseLock.TryGet(ws, out coords, out name))
                return;

            HasGpsLock = true;
            GpsCoords = coords;
            GpsName = string.IsNullOrEmpty(name) ? "GPS TARGET" : name;
            HasTarget = true;
            TargetName = GpsName;
            if (grid != null)
            {
                TargetDistance = Vector3D.Distance(coords, grid.PositionComp.GetPosition());

                // The trip starts where the target was locked (or where a farther start is seen, if the ship backed away)
                if (Vector3D.DistanceSquared(coords, tripCoords) > 1.0 || TargetDistance > tripStartDistance)
                {
                    tripCoords = coords;
                    tripStartDistance = Math.Max(TargetDistance, 1.0);
                }
                HasCruiseTrip = true;
                CruiseTripProgress = (float)MathHelper.Clamp(1.0 - TargetDistance / tripStartDistance, 0.0, 1.0);
            }

            if (Phase != HudPhase.Cruise || SpeedMps <= 1.0)
                return;

            double remaining = Math.Max(0.0, TargetDistance - CruiseLock.ReachedMeters);
            HasCruiseEta = true;
            CruiseEtaSeconds = (float)(remaining / SpeedMps);

            // Slowing down takes a few speed steps, more of them the faster the ship goes: warn earlier at high speed
            double lead = MathHelper.Clamp(2.0 + SpeedMps / 10000.0, 3.0, 12.0);
            if (SpeedMps > 1000.0 && CruiseEtaSeconds < lead)
                BrakeLevel = 2;
            else if (SpeedMps > 1000.0 && CruiseEtaSeconds < lead * 2.5)
                BrakeLevel = 1;
        }

        // ------------------------------------------------------------------ hyperspace

        private void FillHyperspace(HyperspaceSystem hs, MyCubeGrid grid, bool slow)
        {
            Drive = HudDrive.Hyperspace;
            AlignTolerance = HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG;
            AlignBreak = HyperspaceSystem.ALIGNMENT_BREAK_DEG;

            bool gps = hs.Mode == HyperspaceSystem.JumpMode.GpsWaypoint;
            if (gps && !string.IsNullOrEmpty(hs.SelectedGpsName))
            {
                HasTarget = true;
                TargetName = hs.SelectedGpsName;
            }
            else if (!gps)
            {
                HasTarget = true;
                TargetName = "BLIND";
            }
            SetGpsLock(gps, hs.SelectedGpsCoords, hs.SelectedGpsName);
            FillFleet(grid);

            if (slow)
            {
                slowRange = hs.MaxRangeNow;
                double capacity = hs.GetCapacitorCapacityMWh();
                slowCapFraction = capacity > 0.0001 ? (float)MathHelper.Clamp(hs.GetStoredEnergyMWh() / capacity, 0.0, 1.0) : 0f;
                slowManualDistance = hs.GetCurrentManualDistance(hs.GetDrivePowerMW());
                slowCapacitorLow = hs.GetAffordableDistance() < HyperspaceSystem.MIN_JUMP_DISTANCE;
            }
            HasCapacitor = true;
            CapFraction = slowCapFraction;
            RangeMeters = slowRange;

            switch (hs.State)
            {
                case HyperspaceSystem.HyperState.Charging:
                    TargetDistance = hs.JumpDistance;
                    if (hs.IsFleetHolding && hs.ChargeProgress >= 1f)
                    {
                        Phase = HudPhase.FleetHold;
                    }
                    else
                    {
                        Phase = HudPhase.Charging;
                        HasProgress = HasSeconds = true;
                        Progress = hs.ChargeProgress;
                        SecondsLeft = hs.ChargeSecondsLeft;
                    }
                    FillHyperspaceAlign(hs, gps);
                    break;

                case HyperspaceSystem.HyperState.HoldingCharge:
                    Phase = HudPhase.Hold;
                    TargetDistance = hs.JumpDistance;
                    FillHyperspaceAlign(hs, gps);
                    break;

                case HyperspaceSystem.HyperState.Countdown:
                    Phase = HudPhase.Countdown;
                    HasProgress = HasSeconds = true;
                    Progress = hs.CountdownProgress;
                    SecondsLeft = hs.CountdownSecondsLeft;
                    CountdownDigit = Math.Max(1, (int)Math.Ceiling(SecondsLeft));
                    TargetDistance = hs.JumpDistance;
                    FillHyperspaceAlign(hs, gps);
                    break;

                case HyperspaceSystem.HyperState.Active:
                    TargetDistance = hs.JumpDistance;
                    if (hs.Hyperdiction != null && !hs.Hyperdiction.IsRecovering)
                    {
                        Phase = HudPhase.Hyperdicted;
                        HasProgress = true;
                        FillHyperdictedLive(hs.Hyperdiction, hs.JumpDestination, hs.JumpDistance, hs.GridSystem?.mainGrid);
                        HyperdictionIntensity = MathHelper.Clamp((hs.Hyperdiction.ShakeAt(hs.Hyperdiction.ElapsedTicks) - 1f) / 0.6f, 0f, 1f);
                        ShakeMagnitude = hs.TransitShakeMagnitude;
                        ShakeSeed = 0.3f;
                        break;
                    }

                    Phase = HudPhase.Transit;
                    ShakeMagnitude = hs.TransitShakeMagnitude;
                    ShakeSeed = 0.3f;
                    InTransit = true;
                    TravelProgress = hs.TransitDistanceProgress;
                    HasProgress = HasSeconds = true;
                    Progress = TravelProgress;
                    SecondsLeft = hs.TransitSecondsLeft;
                    HasSpeed = true;
                    SpeedMps = hs.CurrentTransitSpeed * 1000.0;
                    UpdateApparentSpeed();
                    TargetDistance = Math.Max(0.0, hs.JumpDistance * (1.0 - TravelProgress));

                    // Just escaped a Hyperdictor: back in transit, the warning fades out over the recovery
                    if (hs.Hyperdiction != null)
                        HyperdictionIntensity = MathHelper.Clamp((hs.Hyperdiction.ShakeAt(hs.Hyperdiction.ElapsedTicks) - 1f) / 0.6f, 0f, 1f);
                    break;

                case HyperspaceSystem.HyperState.Cooldown:
                    Phase = HudPhase.Cooldown;
                    HasProgress = HasSeconds = true;
                    Progress = hs.CooldownProgress;
                    SecondsLeft = hs.CooldownSecondsLeft;
                    break;

                default:
                    Phase = HudPhase.Idle;
                    if (gps && hs.SelectedGpsCoords.HasValue && grid != null)
                        TargetDistance = Vector3D.Distance(hs.SelectedGpsCoords.Value, grid.PositionComp.GetPosition());
                    else
                        TargetDistance = slowManualDistance;

                    if (slowCapacitorLow)
                        Note = "CAPACITOR CHARGING";
                    break;
            }
        }

        private void FillHyperspaceAlign(HyperspaceSystem hs, bool gps)
        {
            // A blind solo jump goes wherever the nose points: aim, don't align. A blind fleet jump follows the fleet vector,
            // also after the leader engaged (the lobby is gone from the HUD then, the fleet registration is not)
            if (!gps && !hs.IsFleetJump)
            {
                BlindJump = true;
                return;
            }

            Vector3D dir;
            var cockpit = CachedCockpit(hs.GridSystem);
            if (cockpit != null && hs.TryGetAlignTarget(cockpit, out dir))
                SetAlign(dir, cockpit.WorldMatrix);
        }

        private void FillFleet(MyCubeGrid grid)
        {
            if (grid == null)
                return;

            FleetJumpLobby lobby = FleetJumpSystem.GetLobbyForGrid(grid.EntityId);
            if (lobby == null || lobby.IsExecuting)
                return;

            Lobby = lobby;
            IsFleetLeader = lobby.LeaderGridId == grid.EntityId;
        }

        private void SetGpsLock(bool gps, Vector3D? coords, string name)
        {
            if (!gps || !coords.HasValue)
                return;

            HasGpsLock = true;
            GpsCoords = coords.Value;
            GpsName = string.IsNullOrEmpty(name) ? "GPS TARGET" : name;
        }

        // ------------------------------------------------------------------ capital

        private void FillCapital(CapitalFSD cap, MyCubeGrid grid)
        {
            Drive = HudDrive.Capital;
            AlignTolerance = CapitalFSD.ALIGNMENT_TOLERANCE_DEG;
            AlignBreak = CapitalFSD.ALIGNMENT_BREAK_DEG;

            bool gps = cap.Mode == HyperspaceSystem.JumpMode.GpsWaypoint;
            if (gps && !string.IsNullOrEmpty(cap.SelectedGpsName))
            {
                HasTarget = true;
                TargetName = cap.SelectedGpsName;
            }
            else if (!gps)
            {
                HasTarget = true;
                TargetName = "BLIND";
            }
            SetGpsLock(gps, cap.SelectedGpsCoords, cap.SelectedGpsName);

            switch (cap.State)
            {
                case CapitalFSD.CapState.HoldingCharge:
                    Phase = HudPhase.Hold;
                    TargetDistance = cap.JumpDistance;
                    FillCapitalAlign(cap, gps);
                    break;

                case CapitalFSD.CapState.Charging:
                    Phase = HudPhase.Charging;
                    HasProgress = HasSeconds = true;
                    Progress = cap.StateProgress;
                    SecondsLeft = cap.StateSecondsLeft;
                    TargetDistance = cap.JumpDistance;
                    FillCapitalAlign(cap, gps);
                    break;

                case CapitalFSD.CapState.EngageRiser:
                case CapitalFSD.CapState.JumpIn:
                    Phase = HudPhase.Engaging;
                    HasProgress = HasSeconds = true;
                    Progress = cap.StateProgress;
                    SecondsLeft = cap.StateSecondsLeft;
                    TargetDistance = cap.JumpDistance;
                    break;

                case CapitalFSD.CapState.Transit:
                    if (cap.Hyperdiction != null && !cap.Hyperdiction.IsRecovering)
                    {
                        Phase = HudPhase.Hyperdicted;
                        HasProgress = true;
                        FillHyperdictedLive(cap.Hyperdiction, cap.JumpDestination, cap.JumpDistance, cap.GridSystem?.mainGrid);
                        HyperdictionIntensity = MathHelper.Clamp((cap.Hyperdiction.ShakeAt(cap.Hyperdiction.ElapsedTicks) - 1f) / 0.6f, 0f, 1f);
                        ShakeMagnitude = cap.TransitShakeMagnitude;
                        ShakeSeed = 0.7f;
                        break;
                    }

                    Phase = HudPhase.Transit;
                    ShakeMagnitude = cap.TransitShakeMagnitude;
                    ShakeSeed = 0.7f;
                    InTransit = true;
                    TravelProgress = cap.TransitDistanceProgress;
                    HasProgress = HasSeconds = true;
                    Progress = TravelProgress;
                    SecondsLeft = cap.StateSecondsLeft;
                    HasSpeed = true;
                    SpeedMps = cap.CurrentTransitSpeed * 1000.0;
                    UpdateApparentSpeed();
                    TargetDistance = Math.Max(0.0, cap.JumpDistance * (1.0 - TravelProgress));

                    if (cap.Hyperdiction != null)
                        HyperdictionIntensity = MathHelper.Clamp((cap.Hyperdiction.ShakeAt(cap.Hyperdiction.ElapsedTicks) - 1f) / 0.6f, 0f, 1f);
                    break;

                case CapitalFSD.CapState.JumpOut:
                    Phase = HudPhase.Exiting;
                    HasProgress = HasSeconds = true;
                    Progress = cap.StateProgress;
                    SecondsLeft = cap.StateSecondsLeft;
                    break;

                case CapitalFSD.CapState.Cooldown:
                    Phase = HudPhase.Cooldown;
                    HasProgress = HasSeconds = true;
                    Progress = cap.StateProgress;
                    SecondsLeft = cap.StateSecondsLeft;
                    break;

                default:
                    Phase = HudPhase.Idle;
                    if (gps && cap.SelectedGpsCoords.HasValue && grid != null)
                        TargetDistance = Vector3D.Distance(cap.SelectedGpsCoords.Value, grid.PositionComp.GetPosition());
                    break;
            }
        }

        private void FillCapitalAlign(CapitalFSD cap, bool gps)
        {
            if (!gps)
            {
                BlindJump = true;
                return;
            }

            Vector3D dir;
            var cockpit = CachedCockpit(cap.GridSystem);
            if (cockpit != null && cap.TryGetAlignTarget(cockpit, out dir))
                SetAlign(dir, cockpit.WorldMatrix);
        }

        // FindMainCockpit walks every block of the ship (and its thrusters): look it up a few times a second, not every frame
        private GridSystem cockpitOwner;
        private IMyShipController cockpitCache;
        private int cockpitAge;

        private IMyShipController CachedCockpit(GridSystem gridSystem)
        {
            if (gridSystem == null)
                return null;

            bool stale = cockpitCache == null || cockpitCache.Closed || !cockpitCache.IsFunctional;
            if (gridSystem != cockpitOwner || stale || ++cockpitAge >= 10)
            {
                cockpitOwner = gridSystem;
                cockpitAge = 0;
                cockpitCache = gridSystem.FindMainCockpit();
            }
            return cockpitCache;
        }

        // The real transit speed scaled up, with a restless flicker re-rolled a few times a second
        /// <summary>
        /// Held by a Hyperdictor, the readouts keep running from where the ship really is: its speed along the pull, the distance still to
        /// the destination it is no longer heading for, and an ETA worked out from those two (which stops adding up as the ship slows).
        /// </summary>
        private void FillHyperdictedLive(HyperdictionSequence seq, Vector3D destination, double jumpDistance, MyCubeGrid grid)
        {
            Vector3D here, next, tangent;
            seq.PathAt(seq.ElapsedTicks, out here, out tangent);
            seq.PathAt(seq.ElapsedTicks + 1, out next, out tangent);

            InTransit = true;
            HasSpeed = true;
            SpeedMps = Vector3D.Distance(here, next) * 60.0;
            UpdateApparentSpeed();

            Vector3D position = grid != null ? grid.PositionComp.WorldAABB.Center : here;
            TargetDistance = Vector3D.Distance(position, destination);
            TravelProgress = jumpDistance > 1.0 ? (float)MathHelper.Clamp(1.0 - TargetDistance / jumpDistance, 0.0, 1.0) : 0f;
            Progress = TravelProgress;

            HasSeconds = true;
            SecondsLeft = SpeedMps > 1.0 ? (float)Math.Min(TargetDistance / SpeedMps, 9999.0) : 9999f;
        }

        private void UpdateApparentSpeed()
        {
            if (++jitterFrames >= 4)
            {
                jitterFrames = 0;
                apparentJitter = 1.0 + ApparentJitter * (jitterRandom.NextDouble() * 2.0 - 1.0);
            }
            ApparentSpeedKmps = SpeedMps / 1000.0 * ApparentSpeedScale * apparentJitter;
        }

        // ------------------------------------------------------------------ alignment

        private void SetAlign(Vector3D dir, MatrixD cockpit)
        {
            HasAlign = true;
            AlignDir = dir;
            AlignAngle = (float)MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot((Vector3D)cockpit.Forward, dir), -1.0, 1.0)));
            AlignLocal = Vector3D.TransformNormal(dir, MatrixD.Transpose(cockpit));
        }
    }
}
