using RichHudFramework.UI;
using RichHudFramework.UI.Rendering;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>The replacement for vanilla notifications: a short stack of fading lines above the status panel. Repeats of the same text merge into one line with a count.</summary>
    public class EventFeed
    {
        public const int MaxLines = 4;
        public const float LineWidth = 480f;
        public const float LineHeight = 24f;
        private const float Gap = 4f;
        private const float MergeWindowSeconds = 1.5f;
        private const float FadeInSeconds = 0.12f;
        private const float FadeOutSeconds = 0.6f;
        private const int AlphaSteps = 8;
        private const float TypeRate = 85f;           // characters per second
        private const float CriticalTypeRate = 55f;   // critical lines are typed slower, with a glitch
        private const float TickFlashSeconds = 0.3f;
        private const float GlitchSeconds = 0.4f;

        private class Line
        {
            public TexturedBox back;
            public TexturedBox tick;
            public Label label;
            public string text;
            public HudSeverity severity;
            public float age;
            public float typeAge;
            public float life;
            public int count;
            public int shownStep = -1;
            public int shownChars = -1;
            public int shownGlitch = -1;
            public bool active;
        }

        // Index 0 is the newest line and sits lowest, next to the status panel
        private readonly Line[] lines = new Line[MaxLines];

        public EventFeed(HudParentBase parent)
        {
            for (int i = 0; i < MaxLines; i++)
            {
                var line = new Line();
                line.back = new TexturedBox(parent) { Size = new Vector2(LineWidth, LineHeight), Color = HudPalette.Panel, Visible = false };
                line.tick = new TexturedBox(line.back) { Size = new Vector2(3f, LineHeight), ParentAlignment = ParentAlignments.InnerLeft };
                line.label = new Label(line.back)
                {
                    AutoResize = false,
                    Size = new Vector2(LineWidth - 16f, LineHeight),
                    ParentAlignment = ParentAlignments.InnerLeft,
                    Offset = new Vector2(10f, 0f),
                    BuilderMode = TextBuilderModes.Unlined,
                    VertCenterText = true
                };
                lines[i] = line;
            }
        }

        public void Push(string text, HudSeverity severity, float seconds)
        {
            Line newest = lines[0];
            if (newest.active && newest.severity == severity && newest.age < MergeWindowSeconds && BaseText(newest) == text)
            {
                newest.count++;
                newest.age = 0f;
                newest.life = seconds;
                newest.shownStep = -1;
                newest.shownChars = -1;
                return;
            }

            // Everything moves one slot up, the oldest line falls off
            Line recycled = lines[MaxLines - 1];
            for (int i = MaxLines - 1; i > 0; i--)
                lines[i] = lines[i - 1];
            lines[0] = recycled;

            recycled.text = text;
            recycled.severity = severity;
            recycled.age = 0f;
            recycled.typeAge = 0f;
            recycled.life = seconds;
            recycled.count = 1;
            recycled.shownStep = -1;
            recycled.shownChars = -1;
            recycled.active = true;
        }

        private static string BaseText(Line line)
        {
            return line.text;
        }

        /// <param name="baseY">Vertical offset of the lowest line's centre.</param>
        public void Update(float dt, float baseY)
        {
            for (int i = 0; i < MaxLines; i++)
            {
                Line line = lines[i];
                if (!line.active)
                {
                    line.back.Visible = false;
                    continue;
                }

                line.age += dt;
                line.typeAge += dt;
                if (line.age >= line.life)
                {
                    line.active = false;
                    line.back.Visible = false;
                    continue;
                }

                float alpha = 1f;
                if (line.age < FadeInSeconds)
                    alpha = line.age / FadeInSeconds;
                else if (line.life - line.age < FadeOutSeconds)
                    alpha = (line.life - line.age) / FadeOutSeconds;

                line.back.Visible = true;
                line.back.Offset = new Vector2(0f, baseY + i * (LineHeight + Gap));

                Color color = HudPalette.ForSeverity(line.severity);
                bool effects = HudSettings.Current.Effects;
                bool critical = line.severity == HudSeverity.Critical;

                // The severity tick flashes wide and white on arrival, then settles
                float flash = effects ? MathHelper.Clamp(1f - line.typeAge / TickFlashSeconds, 0f, 1f) : 0f;
                line.back.Color = HudPalette.Fade(HudPalette.Panel, alpha);
                line.tick.Width = 3f + 6f * flash;
                line.tick.Color = HudPalette.Fade(Color.Lerp(color, Color.White, flash), alpha);

                // Critical lines glitch for a moment: the text shudders sideways and the tick blinks
                float glitch = effects && critical ? MathHelper.Clamp(1f - line.typeAge / GlitchSeconds, 0f, 1f) : 0f;
                if (glitch > 0f)
                {
                    int frameSlot = (int)(line.typeAge * 40f);
                    float jolt = ((unchecked((uint)frameSlot * 2654435761u) >> 20) & 0xFF) / 127.5f - 1f;
                    line.label.Offset = new Vector2(10f + 5f * glitch * jolt, 0f);
                    if (frameSlot % 3 == 0)
                        line.tick.Color = HudPalette.Fade(Color.White, alpha);
                }
                else
                {
                    line.label.Offset = new Vector2(10f, 0f);
                }

                // Typewriter: the text types in character by character (all at once with effects off)
                string full = line.count > 1 ? line.text + "  x" + line.count : line.text;
                int chars = effects ? (int)(line.typeAge * (critical ? CriticalTypeRate : TypeRate)) : full.Length;
                bool typing = chars < full.Length;
                int visibleChars = typing ? chars : full.Length;
                int cursor = typing ? 1 : 0;

                // Text is rebuilt only when the quantized fade or the typed length changes, not every frame
                // During a Hyperdiction the text is also rebuilt with each glitch frame, with some characters corrupted
                int step = (int)(alpha * AlphaSteps);
                int key = visibleChars * 2 + cursor;
                bool corrupt = effects && HudGlitch.Active;
                int glitchFrame = corrupt ? HudGlitch.Frame : -1;
                if (step != line.shownStep || key != line.shownChars || glitchFrame != line.shownGlitch)
                {
                    line.shownStep = step;
                    line.shownChars = key;
                    line.shownGlitch = glitchFrame;
                    string shown = typing ? full.Substring(0, visibleChars) + "_" : full;
                    if (corrupt)
                        shown = HudGlitch.Corrupt(shown, 200 + i);
                    line.label.TextBoard.SetText(shown, HudPalette.Text(HudPalette.Fade(color, step / (float)AlphaSteps), 0.85f));
                }
            }
        }

        public void Clear()
        {
            for (int i = 0; i < MaxLines; i++)
            {
                lines[i].active = false;
                lines[i].back.Visible = false;
            }
        }
    }
}
