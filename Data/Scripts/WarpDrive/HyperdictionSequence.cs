using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Deterministic 9 second hyperdiction timeline for one victim grid.
    /// The pose and shake are pure functions of (tick, seed, scale), so the server and every client
    /// agree without syncing per-tick state. Timed against hyperdiction.wav (540 ticks):
    ///   0.0-2.5s swell | 2.5s yank 1 | 3.8s yank 2 | 3.8-5.0s re-align | 5.0-6.9s settle | 6.9-9.0s roll spasms
    /// </summary>
    public class HyperdictionSequence
    {
        public const int TOTAL_TICKS = 540;
        public const int RECOVERY_TICKS = 120;
        public const double SPEED_CAP_MPS = 50000.0;     // 50 km/s
        public const double MIN_SPEED_MPS = 10000.0;     // never crawl, even if caught during the departure ramp
        public const double SPREAD_DEG = 5.0;
        public const double FOLLOW_STANDOFF_METERS = 800.0; // the hyperdictor lands this far behind the victim
        public const float REFERENCE_MASS_KG = 1807008f;    // tuned ship: full-strength effects at or below this mass, weaker above
        public const float MIN_SCALE = 0.25f;

        public long VictimGridId { get; private set; }
        public long HyperdictorBlockId { get; private set; }
        public int Seed { get; private set; }
        public float Scale { get; private set; }
        public bool IsCapital { get; private set; }
        public Vector3D CatchPos { get; private set; }
        public Vector3D ControlPos { get; private set; }
        public Vector3D DropPos { get; private set; }
        public MatrixD CatchOrientation { get; private set; }
        public Vector3D CatchDirection { get; private set; }
        public Vector3D LocalCenter { get; private set; }

        public int ElapsedTicks;
        public int RecoverStartTick = -1;

        public bool IsRecovering { get { return RecoverStartTick >= 0; } }
        public bool IsFinished { get { return !IsRecovering && ElapsedTicks >= TOTAL_TICKS; } }
        public bool RecoveryComplete { get { return IsRecovering && ElapsedTicks - RecoverStartTick >= RECOVERY_TICKS; } }

        /// <param name="catchPos">World position of the grid's local AABB center at the moment of the catch.</param>
        /// <param name="localCenter">Grid local AABB center (offset between grid origin and rotation pivot).</param>
        public HyperdictionSequence(long victimGridId, long hyperdictorBlockId, int seed, bool isCapital, float massKg,
            Vector3D catchPos, Vector3D dropPos, MatrixD catchOrientation, Vector3D catchDirection, Vector3D localCenter)
        {
            VictimGridId = victimGridId;
            HyperdictorBlockId = hyperdictorBlockId;
            Seed = seed;
            IsCapital = isCapital;
            Scale = ScaleForMass(massKg);
            CatchPos = catchPos;
            DropPos = dropPos;
            catchOrientation.Translation = Vector3D.Zero;
            CatchOrientation = catchOrientation;
            LocalCenter = localCenter;
            CatchDirection = catchDirection.LengthSquared() > 0.001 ? Vector3D.Normalize(catchDirection) : (Vector3D)catchOrientation.Forward;

            double d = Vector3D.Distance(catchPos, dropPos);
            ControlPos = catchPos + CatchDirection * (d * 0.5);

            BuildKeyFrames();
        }

        /// <summary>Effect strength by mass: 1.0 up to REFERENCE_MASS_KG, then falls off as sqrt(reference / mass) (4x the reference = 0.5), never below MIN_SCALE.</summary>
        public static float ScaleForMass(float massKg)
        {
            if (massKg <= REFERENCE_MASS_KG) return 1.0f;
            return Math.Max(MIN_SCALE, (float)Math.Sqrt(REFERENCE_MASS_KG / massKg));
        }

        /// <summary>Physical mass of the grid (including connected physical grids), 0 if unavailable.</summary>
        public static float MassOf(MyCubeGrid grid)
        {
            if (grid == null) return 0f;
            float baseMass, physicalMass;
            return grid.GetCurrentMass(out baseMass, out physicalMass, GridLinkTypeEnum.Physical);
        }

        private readonly float[] simYaw = new float[TOTAL_TICKS + 1];
        private readonly float[] simPitch = new float[TOTAL_TICKS + 1];
        private readonly float[] simRoll = new float[TOTAL_TICKS + 1];

        private struct Kick
        {
            public int Tick;
            public float Yaw, Pitch, Roll;  // angular velocity impulses (rad/s)
            public float Weight;            // 0..1, how hard the camera is slammed
            public int Duration;            // ticks the shove is spread over
        }

        private readonly List<Kick> kicks = new List<Kick>();

        private static float Smooth(float x)
        {
            x = MathHelper.Clamp(x, 0f, 1f);
            return x * x * (3f - 2f * x);
        }

        /// <summary>Overall intensity of the ride, 1.0 (calm) to 1.5 (peak). Eases in to a mild buzz before the first yank, then builds.
        /// After the second yank it only relaxes to a steady rumble (never to calm); kicks lift it back up via KickProximity.</summary>
        private const float RUMBLE_FLOOR = 1.25f;

        private static float Envelope(int tick)
        {
            if (tick < 130) return 1.0f + 0.25f * Smooth(tick / 130f);
            if (tick < 242) return 1.25f + 0.25f * Smooth((tick - 130f) / 50f);
            if (tick < 300) return 1.5f - (1.5f - RUMBLE_FLOOR) * Smooth((tick - 242) / 58f);
            if (tick < 380) return RUMBLE_FLOOR;
            return RUMBLE_FLOOR + (1.5f - RUMBLE_FLOOR) * Smooth((tick - 380) / (TOTAL_TICKS - 380f));
        }

        /// <summary>0..~1 boost for how close a jolt or roll is: ramps up shortly before it and decays slowly after.</summary>
        private float KickProximity(int tick)
        {
            float p = 0f;
            for (int i = 0; i < kicks.Count; i++)
            {
                float d = tick - kicks[i].Tick;
                float w = 0.5f + 0.5f * kicks[i].Weight;
                p += w * (float)Math.Exp(d < 0 ? d / 6.0 : -d / 16.0);
            }
            return Math.Min(p, 1f);
        }

        /// <summary>
        /// The ship is a train being kicked off its rails: every axis is a spring that keeps pulling it back to the track
        /// (with soft walls so it never goes too far), while impulses and a swelling sway shove it around. The whole motion is
        /// simulated once at construction from the seed, so it is smooth, bounded, and identical on every machine.
        /// </summary>
        private void BuildKeyFrames()
        {
            var rng = new Random(Seed);
            float s = Scale;
            float vary = 0.85f + 0.3f * (float)rng.NextDouble();
            float side = rng.Next(2) == 0 ? 1f : -1f;

            // Kick schedule
            int t1 = 150 + rng.Next(-5, 6);
            kicks.Add(new Kick { Tick = t1, Yaw = 1.1f * vary * side, Pitch = 0.5f * vary, Roll = 1.6f * side, Weight = 0.6f, Duration = 16 });
            int t2 = 228 + rng.Next(-4, 5);
            kicks.Add(new Kick { Tick = t2, Yaw = -1.3f * vary * side, Pitch = -0.7f * vary, Roll = -2.3f * side, Weight = 1.0f, Duration = 18 });

            // Lull: the conduit keeps jostling the ship at uneven intervals
            int t = 262 + rng.Next(0, 25);
            while (t < 400)
            {
                float m = 0.25f + 0.25f * (float)rng.NextDouble();
                float sg = rng.Next(2) == 0 ? 1f : -1f;
                kicks.Add(new Kick { Tick = t, Yaw = sg * 0.8f * m * (float)(rng.NextDouble() - 0.3), Pitch = 0.6f * m * ((float)rng.NextDouble() - 0.5f), Roll = sg * 1.5f * m, Weight = 0.2f, Duration = 22 });
                t += 40 + rng.Next(0, 36);
            }

            // Spasms: violent roll flips with their own smaller jolts in between
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            for (int i = 0; i < 4; i++)
            {
                int tick = 414 + 30 * i + rng.Next(-6, 7);
                float mag = 1.5f + 0.8f * (float)rng.NextDouble();
                kicks.Add(new Kick { Tick = tick, Yaw = ((float)rng.NextDouble() - 0.5f) * 0.9f, Pitch = ((float)rng.NextDouble() - 0.5f) * 0.7f, Roll = sign * mag, Weight = 0.5f, Duration = 12 });
                if (rng.NextDouble() < 0.6)
                    kicks.Add(new Kick { Tick = tick + 12 + rng.Next(0, 8), Yaw = ((float)rng.NextDouble() - 0.5f) * 0.5f, Pitch = ((float)rng.NextDouble() - 0.5f) * 0.5f, Roll = -sign * (0.4f + 0.4f * (float)rng.NextDouble()), Weight = 0.2f, Duration = 12 });
                sign = -sign;
            }
            kicks.Sort((a, b) => a.Tick.CompareTo(b.Tick));

            // Sway: random torque bands, strongest while the ride is rough
            var swayFreq = new float[3, 3];
            var swayPhase = new float[3, 3];
            var buzzFreq = new float[3, 2];
            var buzzPhase = new float[3, 2];
            for (int a = 0; a < 3; a++)
            {
                for (int j = 0; j < 3; j++)
                {
                    swayFreq[a, j] = (3f + 1.8f * j) * (0.8f + 0.4f * (float)rng.NextDouble());
                    swayPhase[a, j] = (float)(rng.NextDouble() * Math.PI * 2.0);
                }
                for (int j = 0; j < 2; j++)
                {
                    buzzFreq[a, j] = (j == 0 ? 33f : 57f) * (0.8f + 0.4f * (float)rng.NextDouble());
                    buzzPhase[a, j] = (float)(rng.NextDouble() * Math.PI * 2.0);
                }
            }

            // Spring constants: yaw, pitch, roll (roll is the loosest, like a train leaning on its suspension)
            float[] k = { 12f, 14f, 9f };
            float[] zeta = { 0.30f, 0.30f, 0.27f };
            float[] lim = { 0.40f * s, 0.25f * s, 0.60f * s };
            float[] swayAmp = { 2f, 1.6f, 2.4f };
            float[] th = new float[3];
            float[] om = new float[3];
            float[] o = new float[3];

            const int SUB = 4;
            float dt = 1f / 60f / SUB;
            int nextKick = 0;
            var active = new List<Kick>();

            for (int tick = 0; tick <= TOTAL_TICKS; tick++)
            {
                float time = tick / 60f;
                float env = MathHelper.Clamp((Envelope(tick) - 1f) / 0.5f + 0.4f * KickProximity(tick), 0f, 1f);

                while (nextKick < kicks.Count && kicks[nextKick].Tick <= tick)
                    active.Add(kicks[nextKick++]);

                // Each shove is spread over its Duration so the body is wrenched, not snapped
                for (int q = active.Count - 1; q >= 0; q--)
                {
                    var kk = active[q];
                    int i = tick - kk.Tick;
                    float f = Smooth((i + 1f) / kk.Duration) - Smooth(i / (float)kk.Duration);
                    om[0] += kk.Yaw * s * f; om[1] += kk.Pitch * s * f; om[2] += kk.Roll * s * f;
                    if (i + 1 >= kk.Duration) active.RemoveAt(q);
                }

                // No whole-ship sway before the first yank: only the faint buzz
                float swayEnv = env * Smooth((tick - 140f) / 50f);

                // After the second yank the track "catches" the ship: heavier damping while it re-centers
                float dampMul = tick > 236 && tick < 330 ? 1.2f : 1f;

                for (int n = 0; n < SUB; n++)
                {
                    for (int a = 0; a < 3; a++)
                    {
                        float torque = 0f;
                        for (int j = 0; j < 3; j++)
                            torque += (float)Math.Sin(swayFreq[a, j] * time + swayPhase[a, j]) * (j == 0 ? 0.6f : j == 1 ? 0.45f : 0.3f);
                        torque *= swayAmp[a] * swayEnv * s;

                        float acc = torque - k[a] * th[a] - 2f * zeta[a] * dampMul * (float)Math.Sqrt(k[a]) * om[a];

                        float over = Math.Abs(th[a]) - lim[a];
                        if (over > 0f)
                            acc -= Math.Sign(th[a]) * k[a] * 10f * over;

                        om[a] += acc * dt;
                        th[a] += om[a] * dt;
                    }
                }

                // Fast engine buzz on top, fading in with the ride
                float buzz = 0.020f * env * s;
                for (int a = 0; a < 3; a++)
                {
                    float b = (float)(Math.Sin(buzzFreq[a, 0] * time + buzzPhase[a, 0]) * 0.6 + Math.Sin(buzzFreq[a, 1] * time + buzzPhase[a, 1]) * 0.4);
                    o[a] = th[a] + buzz * b * (a == 2 ? 1.8f : 1f);
                }
                simYaw[tick] = o[0]; simPitch[tick] = o[1]; simRoll[tick] = o[2];
            }
        }

        private void Offsets(int tick, out float yaw, out float pitch, out float roll)
        {
            int i = MathHelper.Clamp(tick, 0, TOTAL_TICKS);
            yaw = simYaw[i]; pitch = simPitch[i]; roll = simRoll[i];
        }

        /// <summary>Cockpit camera shake magnitude for the given tick (1.0 baseline, peak 1.5; capital peak 1.25).</summary>
        public float ShakeAt(int tick)
        {
            float v = Envelope(tick);

            for (int i = 0; i < kicks.Count; i++)
            {
                if (tick < kicks[i].Tick) break;
                v += 0.45f * kicks[i].Weight * (float)Math.Exp(-(tick - kicks[i].Tick) / 12.0);
            }
            v = Math.Min(v, 1.6f);

            if (IsRecovering)
            {
                float rt = MathHelper.Clamp((ElapsedTicks - RecoverStartTick) / (float)RECOVERY_TICKS, 0f, 1f);
                v = MathHelper.Lerp(v, 1.0f, rt);
            }

            return 1.0f + (v - 1.0f) * Scale;
        }

        /// <summary>Rotation offsets applied on top of a base orientation. Decays to zero when recovering.</summary>
        public MatrixD OffsetRotation(int tick)
        {
            int evalTick = IsRecovering ? Math.Min(tick, RecoverStartTick) : tick;
            float yaw, pitch, roll;
            Offsets(evalTick, out yaw, out pitch, out roll);

            if (IsRecovering)
            {
                float rt = MathHelper.Clamp((tick - RecoverStartTick) / (float)RECOVERY_TICKS, 0f, 1f);
                float k = 1f - rt * rt * (3f - 2f * rt);
                yaw *= k; pitch *= k; roll *= k;
            }

            return MatrixD.CreateFromYawPitchRoll(yaw, pitch, roll);
        }

        /// <summary>Applies the (recovering) rotation offsets on top of an ordinary transit pose, pivoting around the grid's AABB center.</summary>
        public MatrixD ApplyOffsets(MatrixD baseMatrix, int tick)
        {
            MatrixD baseRot = baseMatrix;
            baseRot.Translation = Vector3D.Zero;
            Vector3D center = baseMatrix.Translation + Vector3D.TransformNormal(LocalCenter, baseRot);

            MatrixD rot = OffsetRotation(tick) * baseRot;
            rot.Translation = center - Vector3D.TransformNormal(LocalCenter, rot);
            return rot;
        }

        /// <summary>Path center position along the quadratic Bezier toward the drop point (constant parameter rate, no braking) and its tangent.</summary>
        public void PathAt(int tick, out Vector3D center, out Vector3D tangent)
        {
            double e = MathHelper.Clamp(tick / (double)TOTAL_TICKS, 0f, 1f);

            double a = (1.0 - e) * (1.0 - e), b = 2.0 * (1.0 - e) * e, c = e * e;
            center = CatchPos * a + ControlPos * b + DropPos * c;

            Vector3D deriv = 2.0 * (1.0 - e) * (ControlPos - CatchPos) + 2.0 * e * (DropPos - ControlPos);
            tangent = deriv.LengthSquared() > 1e-6 ? Vector3D.Normalize(deriv) : CatchDirection;
        }

        /// <summary>The catch orientation re-aimed from the original heading toward the given path tangent (rotation only).</summary>
        public MatrixD BaseRotationFor(Vector3D tangent)
        {
            MatrixD baseRot = CatchOrientation;
            Vector3D axis = Vector3D.Cross(CatchDirection, tangent);
            double axisLen = axis.Length();
            if (axisLen > 1e-6)
            {
                double angle = Math.Atan2(axisLen, Vector3D.Dot(CatchDirection, tangent));
                baseRot = CatchOrientation * MatrixD.CreateFromAxisAngle(axis / axisLen, angle);
            }
            baseRot.Translation = Vector3D.Zero;
            return baseRot;
        }

        /// <summary>
        /// Full grid pose (grid origin translation + orientation) for the tick, pivoting around the grid's local AABB center.
        /// </summary>
        public MatrixD EvaluatePose(int tick, out Vector3D centerWorld)
        {
            Vector3D tangent;
            PathAt(tick, out centerWorld, out tangent);

            MatrixD rot = OffsetRotation(tick) * BaseRotationFor(tangent);
            rot.Translation = Vector3D.Zero;

            MatrixD pose = rot;
            pose.Translation = centerWorld - Vector3D.TransformNormal(LocalCenter, rot);
            return pose;
        }

        // ------------------------------------------------------------------ static helpers

        /// <summary>First point where the segment prev->cur enters the sphere. Returns true if it starts inside.</summary>
        public static bool SegmentSphereEntry(Vector3D prev, Vector3D cur, Vector3D center, double radius, out Vector3D entry)
        {
            entry = prev;
            Vector3D d = cur - prev;
            Vector3D f = prev - center;
            double r2 = radius * radius;

            if (f.LengthSquared() <= r2)
                return true;

            double a = d.LengthSquared();
            if (a < 1e-9) return false;

            double b = 2.0 * Vector3D.Dot(f, d);
            double c = f.LengthSquared() - r2;
            double disc = b * b - 4.0 * a * c;
            if (disc < 0.0) return false;

            double sq = Math.Sqrt(disc);
            double t = (-b - sq) / (2.0 * a);
            if (t < 0.0 || t > 1.0) return false;

            entry = prev + d * t;
            return true;
        }

        /// <summary>
        /// Picks where the victim leaves the conduit: DROP_SECONDS of travel from the catch point at min(current speed, 50 km/s),
        /// in a random direction within a SPREAD_DEG cone around the original heading. Deterministic for a given seed.
        /// Retries other directions when the line hits a planet, a gravity well or an occupied area.
        /// </summary>
        public static Vector3D ComputeSpreadDrop(Vector3D catchPos, Vector3D catchDir, double speedMps, int seed, double victimRadius, ICollection<IMyCubeGrid> ignoreGrids)
        {
            speedMps = MathHelper.Clamp(speedMps, MIN_SPEED_MPS, SPEED_CAP_MPS);
            double dist = speedMps * (TOTAL_TICKS / 60.0);

            Vector3D dir = catchDir.LengthSquared() > 0.001 ? Vector3D.Normalize(catchDir) : Vector3D.Forward;
            Vector3D perp1 = Vector3D.CalculatePerpendicularVector(dir);
            Vector3D perp2 = Vector3D.Cross(dir, perp1);

            var rng = new Random(seed ^ 0x5bd1e995);
            float allowGravity = (Settings.instance != null ? Settings.instance.AllowInGravityMax : 0.2f);
            Vector3D best = catchPos + dir * dist;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                double theta = MathHelper.ToRadians(SPREAD_DEG) * Math.Sqrt(rng.NextDouble());
                double phi = rng.NextDouble() * Math.PI * 2.0;
                Vector3D lateral = perp1 * Math.Cos(phi) + perp2 * Math.Sin(phi);
                Vector3D spread = Vector3D.Normalize(dir * Math.Cos(theta) + lateral * Math.Sin(theta));
                Vector3D candidate = catchPos + spread * dist;
                if (attempt == 0)
                    best = candidate;

                string blockedBy;
                if (HyperspaceSystem.IsTrajectoryBlockedByPlanet(catchPos, candidate, out blockedBy))
                    continue;

                float g;
                Vector3D gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(candidate, out g);
                if (gravity.Length() / WarpSystem.EARTH_GRAVITY > allowGravity)
                    continue;

                if (!IsAreaClear(candidate, victimRadius + 100.0, null, null, ignoreGrids))
                    continue;

                return ClampPlanetClearance(candidate, victimRadius);
            }

            return ClampPlanetClearance(best, victimRadius);
        }

        /// <summary>Where the hyperdictor ship arrives: behind the victim's drop point, back along the victim's line of travel.</summary>
        public static Vector3D ComputeFollowPosition(Vector3D catchPos, Vector3D dropPos, double standoff)
        {
            Vector3D back = catchPos - dropPos;
            if (back.LengthSquared() < 1.0)
                back = Vector3D.Backward;
            back.Normalize();
            return dropPos + back * standoff;
        }

        private static Vector3D ClampPlanetClearance(Vector3D result, double victimRadius)
        {
            var box = new BoundingBoxD(result - new Vector3D(100000), result + new Vector3D(100000));
            var planet = MyGamePruningStructure.GetClosestPlanet(ref box);
            if (planet != null)
            {
                Vector3D pCenter = planet.PositionComp.GetPosition();
                Vector3D pSurf = planet.GetClosestSurfacePointGlobal(ref result);
                double minSafeAlt = 500.0 + victimRadius;
                double alt = (result - pCenter).Length() - (pSurf - pCenter).Length();
                if (alt < minSafeAlt)
                    result = pSurf + Vector3D.Normalize(pSurf - pCenter) * minSafeAlt;
            }
            return result;
        }

        private static bool IsAreaClear(Vector3D pos, double radius, MyCubeGrid hdGrid, MyCubeGrid victimGrid, ICollection<IMyCubeGrid> ignoreGrids)
        {
            var sphere = new BoundingSphereD(pos, radius);
            List<IMyEntity> ents = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            if (ents == null) return true;

            foreach (var ent in ents)
            {
                var grid = ent as MyCubeGrid;
                if (grid != null)
                {
                    if (grid == victimGrid) continue;
                    if (ignoreGrids != null && ignoreGrids.Contains(grid)) continue;
                    return false;
                }

                if (ent is MyVoxelBase && !(ent is MyPlanet))
                    return false;
            }

            return true;
        }
    }
}
