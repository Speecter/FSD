using System;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace WarpDriveMod
{
    /// <summary>
    /// Supercruise, seen from the pilot's seat: thin blue streaks rush past the ship, the same blue as the charge effect. They are drawn
    /// in the ship's own frame (a particle effect would be left behind at these speeds), only for the local pilot, more, faster and
    /// longer the closer the ship is to its top speed.
    /// </summary>
    public static class CruiseStreakFx
    {
        private const int MaxStreaks = 90;
        private static readonly MyStringId Material = MyStringId.GetOrCompute("WeaponLaser");
        private static readonly Vector4 Blue = new Vector4(0.35f, 0.6f, 1f, 1f) * 2.5f;   // over-bright so they read against bright backgrounds too

        private struct Streak
        {
            public double Radius;      // 0..1 across the band around the ship
            public double Angle;
            public double Z;           // 0..1 along the band, 1 = ahead, 0 = behind
            public double Speed;       // per-streak speed variation
            public float Length;       // per-streak length variation
        }

        private static readonly Streak[] streaks = new Streak[MaxStreaks];
        private static readonly Random random = new Random(91);
        private static bool seeded;
        private static long lastGrid;

        /// <summary>Called every tick on the client while the ship is in supercruise.</summary>
        public static void Update(MyCubeGrid mainGrid, MatrixD shipMatrix, float speedFraction)
        {
            if (mainGrid == null || !HudSettings.Current.Effects || !IsLocalPilotAboard(mainGrid) || !LocalView.IsOnboardCamera)
                return;

            if (!seeded || lastGrid != mainGrid.EntityId)
            {
                for (int i = 0; i < MaxStreaks; i++)
                    Respawn(i, random.NextDouble());
                seeded = true;
                lastGrid = mainGrid.EntityId;
            }

            float f = MathHelper.Clamp(speedFraction, 0f, 1f);
            double shipRadius = mainGrid.PositionComp.WorldVolume.Radius;
            double innerRadius = shipRadius * 1.2 + 20.0;
            double band = shipRadius * 3.0 + 250.0;
            double ahead = shipRadius + 600.0;
            double behind = shipRadius + 400.0;
            double span = ahead + behind;

            // Visual speed past the ship (not the real one, which would be a blur): a drift at low speed, a rush near the top
            double rush = (120.0 + 2200.0 * f) / 60.0 / span;
            int count = (int)(25 + (MaxStreaks - 25) * f);
            float length = 12f + 130f * f;
            float thickness = 0.9f + 1.6f * f;

            Vector3D center = mainGrid.PositionComp.WorldAABB.Center;
            Vector3D forward = shipMatrix.Forward;
            Vector3D right = shipMatrix.Right;
            Vector3D up = shipMatrix.Up;
            Vector3 back = -(Vector3)forward;

            for (int i = 0; i < MaxStreaks; i++)
            {
                streaks[i].Z -= rush * streaks[i].Speed;
                if (streaks[i].Z < 0.0)
                    Respawn(i, 1.0);

                if (i >= count)
                    continue;

                // Fade in ahead and out behind, so no streak pops into view
                double u = streaks[i].Z;
                float alpha = (float)Math.Sin(Math.PI * u) * (0.6f + 0.4f * f);
                if (alpha <= 0.01f)
                    continue;

                double r = innerRadius + band * streaks[i].Radius;
                Vector3D position = center
                    + forward * (u * span - behind)
                    + right * (Math.Cos(streaks[i].Angle) * r)
                    + up * (Math.Sin(streaks[i].Angle) * r);

                MyTransparentGeometry.AddLineBillboard(Material, Blue * alpha, position, back, length * streaks[i].Length, thickness,
                    MyBillboard.BlendTypeEnum.AdditiveBottom);
            }
        }

        private static void Respawn(int i, double z)
        {
            streaks[i].Radius = Math.Sqrt(random.NextDouble());   // even spread over the ring, not bunched at its inner edge
            streaks[i].Angle = random.NextDouble() * Math.PI * 2.0;
            streaks[i].Z = z;
            streaks[i].Speed = 0.7 + 0.6 * random.NextDouble();
            streaks[i].Length = 0.6f + 0.8f * (float)random.NextDouble();
        }

        private static bool IsLocalPilotAboard(MyCubeGrid mainGrid)
        {
            var seat = MyAPIGateway.Session?.ControlledObject as IMyCubeBlock;
            if (seat == null || seat.CubeGrid == null)
                return false;

            return seat.CubeGrid == mainGrid || MyAPIGateway.GridGroups.HasConnection(seat.CubeGrid, mainGrid, GridLinkTypeEnum.Logical);
        }
    }
}
