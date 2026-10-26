using System;
using Sandbox.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Turns a flat camera-shake magnitude into something that breathes: slow gusts, a faster tremble
    /// and the odd short jolt. Pure function of time (client-side visual only, nothing to sync).
    /// </summary>
    public static class ShakeShaper
    {
        public static float Natural(float magnitude, float seed)
        {
            if (magnitude <= 0.001f || MyAPIGateway.Session == null)
                return magnitude;

            double t = MyAPIGateway.Session.GameplayFrameCounter / 60.0 + seed * 17.0;

            // Slow gusts plus a faster tremble, always positive so the base level is still felt
            double gust = 0.50 * Math.Sin(0.83 * t + seed) + 0.30 * Math.Sin(2.17 * t + 2.1 * seed) + 0.20 * Math.Sin(5.3 * t + 0.7 * seed);
            double tremble = Math.Sin(11.3 * t + 1.3 * seed) * Math.Sin(7.9 * t);
            double factor = 1.0 + 0.30 * gust + 0.12 * tremble;

            // Occasional jolts: about one slot in three carries one, at an uneven offset inside the slot
            const double slot = 1.9;
            long idx = (long)Math.Floor(t / slot);
            for (long i = idx - 1; i <= idx; i++)
            {
                uint h = Hash((uint)(i * 2654435761L + (long)(seed * 1000f)));
                if ((h & 0xFF) > 85) continue;
                double start = i * slot + ((h >> 8) & 0xFF) / 255.0 * (slot * 0.8);
                double age = t - start;
                if (age < 0.0 || age > 1.2) continue;
                double amp = 0.25 + 0.25 * (((h >> 16) & 0xFF) / 255.0);
                factor += amp * (1.0 - Math.Exp(-age * 40.0)) * Math.Exp(-age * 5.0);
            }

            return (float)(magnitude * Math.Max(0.4, factor));
        }

        /// <summary>Gentler onset for gravity turbulence: barely noticeable at the edge of the well, builds toward the hard deck.</summary>
        public static float EaseGravity(float ratio)
        {
            ratio = MathHelper.Clamp(ratio, 0f, 1f);
            return ratio * ratio * (3f - 2f * ratio) * 0.5f + ratio * ratio * 0.5f;
        }

        private static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352d;
            x ^= x >> 15; x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }
    }
}
