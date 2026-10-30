using System.Collections.Generic;
using RichHudFramework.UI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>A horizontal fill bar: dark track, coloured fill growing from the left, optional segment dividers.</summary>
    public class HudBar : HudElementBase
    {
        private readonly TexturedBox track;
        private readonly TexturedBox fill;
        private readonly TexturedBox glint;
        private float glintT;
        private readonly List<TexturedBox> dividers = new List<TexturedBox>();
        private float shownValue = -1f;
        private Color shownColor;
        private float shownAlpha = -1f;

        public HudBar(HudParentBase parent, float width, float height, int segments = 0) : base(parent)
        {
            Size = new Vector2(width, height);

            track = new TexturedBox(this) { Size = Size, Color = HudPalette.Track };
            fill = new TexturedBox(track) { ParentAlignment = ParentAlignments.InnerLeft, Height = height, Width = 1f, Visible = false };
            glint = new TexturedBox(track) { ParentAlignment = ParentAlignments.InnerLeft, Height = height, Width = 1f, Visible = false };

            for (int i = 1; i < segments; i++)
            {
                dividers.Add(new TexturedBox(track)
                {
                    Size = new Vector2(2f, height),
                    Offset = new Vector2(width * i / segments - width * 0.5f, 0f),
                    Color = new Color(8, 18, 28, 230)
                });
            }
        }

        /// <summary>Resizes the whole bar; the fill is recomputed on the next Set.</summary>
        public void SetWidth(float width)
        {
            if (System.Math.Abs(Width - width) < 0.5f)
                return;

            Width = width;
            track.Width = width;
            shownValue = -1f;
        }

        /// <summary>A soft highlight that keeps sliding along the filled part: the bar reads as flowing, not static.</summary>
        /// <param name="speed">Sweep rate multiplier (1 = the quiet default).</param>
        /// <param name="strength">Brightness and length multiplier of the sweep (1 = the quiet default).</param>
        public void Animate(float dt, bool on, Color color, float speed = 1f, float strength = 1f)
        {
            float fillWidth = shownValue > 0f ? Width * shownValue : 0f;
            if (!on || fillWidth < 28f)
            {
                glint.Visible = false;
                return;
            }

            glintT = (glintT + dt * 0.45f * speed) % 1f;
            float length = System.Math.Min(34f * (0.8f + 0.7f * strength), fillWidth);
            glint.Visible = true;
            glint.Width = length;
            glint.Offset = new Vector2((fillWidth - length) * glintT, 0f);
            glint.Color = HudPalette.Fade(Color.Lerp(color, Color.White, MathHelper.Clamp(0.55f * strength, 0f, 0.9f)), 0.5f * strength * (float)System.Math.Sin(glintT * System.Math.PI));
        }

        /// <param name="value">0..1</param>
        /// <param name="alpha">Overall fade 0..1 (the player's opacity setting is applied on top).</param>
        public void Set(float value, Color color, float alpha = 1f)
        {
            value = MathHelper.Clamp(value, 0f, 1f);
            if (value == shownValue && color == shownColor && alpha == shownAlpha)
                return;

            shownValue = value;
            shownColor = color;
            shownAlpha = alpha;

            track.Color = HudPalette.Fade(HudPalette.Track, alpha);
            fill.Visible = value > 0.002f;
            fill.Width = System.Math.Max(1f, Width * value);
            fill.Color = HudPalette.Fade(color, alpha);

            Color gap = HudPalette.Fade(new Color(8, 18, 28, 230), alpha);
            for (int i = 0; i < dividers.Count; i++)
                dividers[i].Color = gap;
        }
    }
}
