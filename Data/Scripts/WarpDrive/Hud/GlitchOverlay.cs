using System;
using RichHudFramework.UI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Drawn over the HUD during a Hyperdiction: horizontal tear bars that cut across the panel and the reticle, and small blocks of
    /// static that flicker around them. Everything jumps to new places with each glitch frame (about 20 times a second) and grows
    /// with the hyperdiction; bursts tear the hardest.
    /// </summary>
    public class GlitchOverlay : HudElementBase
    {
        private const int MaxBars = 6;
        private const int MaxBlocks = 48;
        private const float ReticleSpread = 240f;

        private static readonly Color Red = new Color(255, 50, 60);
        private static readonly Color Cyan = new Color(60, 230, 255);

        /// <summary>The status panel and the reticle the glitch gathers around; set by FsdHud.</summary>
        public HudElementBase Panel;
        public HudElementBase Reticle;

        public GlitchOverlay(HudParentBase parent) : base(parent)
        {
            Visible = false;
        }

        public void Update()
        {
            Visible = HudSettings.Current.Effects && HudGlitch.Active;
        }

        protected override void Draw()
        {
            MatrixD[] space = HudSpace.PlaneToWorldRef;
            float level = HudGlitch.Level;
            float burst = HudGlitch.Burst;

            bool panel = Panel != null && Panel.Visible;
            bool reticle = Reticle != null && Reticle.Visible;
            Vector2 panelCenter = panel ? Panel.Position : Vector2.Zero;
            Vector2 panelHalf = panel ? Panel.Size * 0.5f : Vector2.Zero;
            Vector2 reticleCenter = reticle ? Reticle.Position : Vector2.Zero;

            // Tear bars: one now and then at a low level, a handful in a burst
            int bars = (int)Math.Round(MaxBars * MathHelper.Clamp(0.12f * level + 0.9f * burst, 0f, 1f));
            for (int i = 0; i < bars; i++)
            {
                bool onPanel = panel && (!reticle || HudGlitch.Unit(1, i) < 0.5f);
                Vector2 c = onPanel ? panelCenter : reticleCenter;
                float halfH = onPanel ? panelHalf.Y : ReticleSpread * 0.5f;
                float halfW = (onPanel ? panelHalf.X : ReticleSpread) * (0.6f + 0.6f * HudGlitch.Unit(2, i));

                float y = c.Y + (HudGlitch.Unit(3, i) * 2f - 1f) * halfH;
                float x = c.X + (HudGlitch.Unit(4, i) * 2f - 1f) * halfW * 0.4f;
                float height = 1.5f + 6f * HudGlitch.Unit(5, i) * (0.4f + burst);

                float pick = HudGlitch.Unit(6, i);
                Color color = pick < 0.4f ? Red : pick < 0.8f ? Cyan : Color.White;
                float alpha = (0.18f + 0.4f * HudGlitch.Unit(7, i)) * (0.5f + 0.5f * Math.Max(level, burst));
                HudDraw.Line(space, new Vector2(x - halfW, y), new Vector2(x + halfW, y), height, HudPalette.Fade(color, alpha));
            }

            // Static: small blocks scattered over the panel and around the reticle
            int blocks = (int)Math.Round(MaxBlocks * MathHelper.Clamp(0.15f * level + 0.85f * burst, 0f, 1f));
            for (int i = 0; i < blocks; i++)
            {
                bool onPanel = panel && (!reticle || HudGlitch.Unit(11, i) < 0.55f);
                Vector2 c = onPanel ? panelCenter : reticleCenter;
                Vector2 half = onPanel ? panelHalf : new Vector2(ReticleSpread * 0.5f);

                Vector2 p = c + new Vector2((HudGlitch.Unit(12, i) * 2f - 1f) * half.X, (HudGlitch.Unit(13, i) * 2f - 1f) * half.Y);
                float w = 3f + 18f * HudGlitch.Unit(14, i);
                float h = 2f + 7f * HudGlitch.Unit(15, i);

                float pick = HudGlitch.Unit(16, i);
                Color color = pick < 0.3f ? Red : pick < 0.55f ? Cyan : pick < 0.8f ? new Color(200, 200, 200) : Color.Black;
                float alpha = 0.25f + 0.5f * HudGlitch.Unit(17, i);
                HudDraw.Line(space, new Vector2(p.X - w * 0.5f, p.Y), new Vector2(p.X + w * 0.5f, p.Y), h, HudPalette.Fade(color, alpha));
            }
        }
    }
}
