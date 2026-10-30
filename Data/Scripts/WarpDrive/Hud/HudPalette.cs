using RichHudFramework.UI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>Every colour and font of the FSD HUD in one place. SE cyan carries the information; amber, red and green are reserved for warning, danger and ready.</summary>
    public static class HudPalette
    {
        public static readonly Color Primary = new Color(90, 190, 255);
        public static readonly Color Dim = new Color(40, 90, 125);
        public static readonly Color Panel = new Color(8, 18, 28, 150);
        public static readonly Color Track = new Color(20, 40, 58, 200);
        public static readonly Color Warning = new Color(255, 190, 60);
        public static readonly Color Danger = new Color(255, 75, 65);
        public static readonly Color Ready = new Color(100, 235, 150);

        public const string LabelFont = "SpaceEngineers";
        public const string NumberFont = "Monospace";

        public static GlyphFormat Text(Color color, float size = 0.85f, TextAlignment align = TextAlignment.Left)
        {
            return new GlyphFormat(color, align, size, LabelFont);
        }

        public static GlyphFormat Number(Color color, float size = 0.85f, TextAlignment align = TextAlignment.Left)
        {
            return new GlyphFormat(color, align, size, NumberFont);
        }

        /// <summary>The colour with its alpha scaled by the fade and the player's HUD opacity setting.</summary>
        public static Color Fade(Color color, float alpha)
        {
            float a = MathHelper.Clamp(alpha, 0f, 1f) * HudSettings.Current.Opacity * (color.A / 255f);
            return new Color(color.R, color.G, color.B, (byte)(a * 255f));
        }

        public static Color ForSeverity(HudSeverity severity)
        {
            switch (severity)
            {
                case HudSeverity.Success: return Ready;
                case HudSeverity.Warning: return Warning;
                case HudSeverity.Critical: return Danger;
                default: return Primary;
            }
        }
    }
}
