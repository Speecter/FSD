using System;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Timing of the reticle's entrance when charging starts: the tick ring assembles clockwise from the top (brightest at the leading
    /// edge, swelling slightly on the way and settling), then the reticle itself is drawn on outward from its centre.
    /// The indicators call <see cref="Reset"/> while they are hidden, so every new showing plays it once.
    /// </summary>
    public class ReticleIntro
    {
        public const float RingSeconds = 0.8f;
        private const float DrawStart = 0.35f;
        private const float DrawSeconds = 0.55f;
        private const float EdgeWidth = 0.12f;   // share of the circle that glows behind the leading edge

        private float time;

        public void Reset()
        {
            time = 0f;
        }

        public void Advance(float dt)
        {
            time += dt;
        }

        private static bool Enabled
        {
            get { return HudSettings.Current.Effects; }
        }

        private static float EaseOut(float t)
        {
            t = MathHelper.Clamp(t, 0f, 1f);
            float inv = 1f - t;
            return 1f - inv * inv * inv;
        }

        public bool Done
        {
            get { return !Enabled || time >= DrawStart + DrawSeconds; }
        }

        /// <summary>0..1 how much of the ring has been swept in, clockwise from the top.</summary>
        public float Ring
        {
            get { return Enabled ? EaseOut(time / RingSeconds) : 1f; }
        }

        /// <summary>0..1 how far the reticle has been drawn on outward from its centre.</summary>
        public float Draw
        {
            get { return Enabled ? EaseOut((time - DrawStart) / DrawSeconds) : 1f; }
        }

        /// <summary>The ring swells a little while it assembles, then settles at its true radius.</summary>
        public float RadiusScale
        {
            get
            {
                if (!Enabled || time >= RingSeconds)
                    return 1f;
                return 1f + 0.08f * (float)Math.Sin(Math.PI * time / RingSeconds);
            }
        }

        /// <summary>Is the tick at this share of the circle (0 at the top, clockwise, below 1) already there?</summary>
        public bool Shown(float fraction)
        {
            return fraction <= Ring;
        }

        /// <summary>0..1 glow of a tick close behind the sweep's leading edge (0 once the sweep is past or done).</summary>
        public float Edge(float fraction)
        {
            if (Done)
                return 0f;

            float behind = Ring - fraction;
            return behind < 0f || behind > EdgeWidth ? 0f : 1f - behind / EdgeWidth;
        }
    }
}
