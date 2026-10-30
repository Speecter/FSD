using System;
using RichHudFramework.UI;
using RichHudFramework.UI.Rendering;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>Critical events: a short banner near the top of the screen that fades in and out. No flashing, no screen edges.</summary>
    public class DangerOverlay
    {
        private const float DurationSeconds = 3f;
        private const float FadeInSeconds = 0.25f;
        private const float FadeOutSeconds = 0.8f;
        private const float BannerWidth = 520f;
        private const float BannerHeight = 34f;

        private readonly TexturedBox back;
        private readonly TexturedBox topLine;
        private readonly TexturedBox bottomLine;
        private readonly Label label;
        private string text = "";
        private float remaining;
        private int shownStep = -1;

        public DangerOverlay(HudParentBase parent)
        {
            back = new TexturedBox(parent) { Size = new Vector2(BannerWidth, BannerHeight), Visible = false };
            topLine = new TexturedBox(back) { Size = new Vector2(BannerWidth, 2f), Offset = new Vector2(0f, BannerHeight * 0.5f - 1f) };
            bottomLine = new TexturedBox(back) { Size = new Vector2(BannerWidth, 2f), Offset = new Vector2(0f, -BannerHeight * 0.5f + 1f) };
            label = new Label(back)
            {
                AutoResize = false,
                Size = new Vector2(BannerWidth - 16f, BannerHeight),
                BuilderMode = TextBuilderModes.Unlined,
                VertCenterText = true
            };
        }

        public void Trigger(string message)
        {
            text = message.ToUpperInvariant();
            remaining = DurationSeconds;
            shownStep = -1;
        }

        public void Update(float dt, float bannerY)
        {
            if (remaining <= 0f)
            {
                back.Visible = false;
                return;
            }

            remaining -= dt;
            float elapsed = DurationSeconds - remaining;
            float alpha = Math.Min(1f, Math.Min(elapsed / FadeInSeconds, Math.Max(0f, remaining) / FadeOutSeconds));

            back.Visible = true;
            back.Offset = new Vector2(0f, bannerY);
            back.Color = HudPalette.Fade(new Color(40, 12, 12, 200), alpha);
            topLine.Color = bottomLine.Color = HudPalette.Fade(HudPalette.Danger, alpha * 0.8f);

            int step = (int)(alpha * 8f);
            if (step != shownStep)
            {
                shownStep = step;
                label.TextBoard.SetText(text, HudPalette.Text(HudPalette.Fade(HudPalette.Danger, step / 8f), 0.95f, TextAlignment.Center));
            }
        }

        public void Hide()
        {
            remaining = 0f;
            back.Visible = false;
        }
    }
}
