using System;

namespace WarpDriveMod
{
    /// <summary>
    /// The HUD's shared "broken screen" state during a Hyperdiction, set once a frame by <see cref="HudFx"/>: a steady level that follows
    /// the hyperdiction's strength, the bursts on top of it, and a frame counter that ticks about 20 times a second so corrupted text,
    /// tear bars and static change in jumps like a bad signal instead of shimmering every frame. Elements read it; nothing here draws.
    /// </summary>
    public static class HudGlitch
    {
        private const string Junk = "#%&@$*!?/\\|<>=+01";
        private const float FrameSeconds = 0.05f;

        private static float frameClock;

        /// <summary>0..1 how hard the ship is being hyperdicted right now.</summary>
        public static float Level { get; private set; }

        /// <summary>0..1 strength of the glitch burst happening right now (0 between bursts).</summary>
        public static float Burst { get; private set; }

        /// <summary>Changes about 20 times a second while glitching.</summary>
        public static int Frame { get; private set; }

        public static bool Active { get { return Level > 0.01f || Burst > 0f; } }

        /// <summary>How far apart (HUD units) the red and cyan ghost copies sit: a little all the time, wide during a burst.</summary>
        public static float Split { get { return Active ? 1.2f + 2.5f * Level + 6f * Burst : 0f; } }

        public static void Update(float level, float burst, float dt)
        {
            Level = level;
            Burst = burst;
            if (!Active)
                return;

            frameClock += dt;
            if (frameClock >= FrameSeconds)
            {
                frameClock = 0f;
                Frame++;
            }
        }

        public static void Reset()
        {
            Level = 0f;
            Burst = 0f;
            frameClock = 0f;
        }

        public static uint Hash(int a, int b, int c)
        {
            uint h = unchecked((uint)a * 2654435761u ^ (uint)b * 40503u ^ (uint)c * 2246822519u);
            h ^= h >> 15;
            h = unchecked(h * 0x2c1b3c6du);
            h ^= h >> 12;
            h = unchecked(h * 0x297a2d39u);
            h ^= h >> 15;
            return h;
        }

        /// <summary>0..1, stable for one glitch frame.</summary>
        public static float Unit(int channel, int index)
        {
            return (Hash(Frame, channel, index) & 0xFFFF) / 65535f;
        }

        /// <summary>The text with some characters swapped for junk: a few now and then, many during a burst. Spaces are left alone.</summary>
        public static string Corrupt(string text, int channel)
        {
            if (!Active || string.IsNullOrEmpty(text))
                return text;

            float chance = 0.04f * Level + 0.35f * Burst;
            char[] chars = null;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == ' ' || Unit(channel, i) >= chance)
                    continue;

                if (chars == null)
                    chars = text.ToCharArray();
                chars[i] = Junk[(int)(Hash(Frame, channel + 977, i) % (uint)Junk.Length)];
            }
            return chars == null ? text : new string(chars);
        }
    }
}
