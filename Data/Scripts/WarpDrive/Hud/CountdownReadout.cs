using System;
using System.Globalization;
using RichHudFramework.UI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// The big rolling countdown above a reticle: seconds.hundredths.ten-thousandths (e.g. 4.87.31), reading 0.00.00 when the drive
    /// engages. It is counted down smoothly between the model's updates, so the last two groups roll every frame.
    /// </summary>
    public class CountdownReadout
    {
        private readonly Label label;
        private readonly Color color;
        private float seconds;
        private bool running;

        public CountdownReadout(HudParentBase parent, Vector2 offset, Color color)
        {
            this.color = color;
            label = new Label(parent)
            {
                AutoResize = false,
                Size = new Vector2(240f, 48f),
                Offset = offset,
                BuilderMode = TextBuilderModes.Unlined,
                VertCenterText = true,
                Visible = false
            };
        }

        public Vector2 Offset
        {
            set { label.Offset = value; }
        }

        public bool Visible
        {
            get { return label.Visible; }
        }

        public void Reset()
        {
            running = false;
            label.Visible = false;
        }

        public static string Format(float seconds)
        {
            int total = (int)(Math.Max(0f, seconds) * 10000f);
            var inv = CultureInfo.InvariantCulture;
            return (total / 10000).ToString(inv) + "." + ((total / 100) % 100).ToString("00", inv) + "." + (total % 100).ToString("00", inv);
        }

        /// <param name="show">Whether the countdown is running; the label hides itself otherwise.</param>
        /// <param name="modelSeconds">The time left as the model last read it.</param>
        public void Update(bool show, float modelSeconds, float dt)
        {
            label.Visible = show;
            if (!show)
            {
                running = false;
                return;
            }

            if (!running || Math.Abs(seconds - dt - modelSeconds) > 0.1f)
            {
                seconds = modelSeconds;
            }
            else
            {
                float extrapolated = seconds - dt;
                seconds = extrapolated + (modelSeconds - extrapolated) * 0.25f;
            }
            running = true;

            // Whitening through the final second
            Color digits = Color.Lerp(color, Color.White, MathHelper.Clamp(1f - seconds, 0f, 1f));
            label.TextBoard.SetText(Format(seconds), HudPalette.Number(digits, 1.55f, TextAlignment.Center));
        }
    }
}
