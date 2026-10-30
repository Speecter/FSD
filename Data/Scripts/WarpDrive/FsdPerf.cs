using System.Diagnostics;
using System.Text;

namespace WarpDriveMod
{
    /// <summary>Cheap always-on timing of the mod's per-tick work, read with "/fsd perf" to see what costs simulation speed.</summary>
    public static class FsdPerf
    {
        public enum Section
        {
            Supercruise,
            Hyperspace,
            Capital,
            Hyperdiction,
            Hud,
            // Breakdown of one hyperspace transit tick (already inside "Hyperspace jumps", not added to the total)
            TransitPower,
            TransitPlanet,
            TransitMove,
            TransitZero,
            TransitFx,
            TransitPilot,
            Count
        }

        private const int MainSections = (int)Section.Hud + 1;

        private static readonly string[] Names = { "Supercruise systems", "Hyperspace jumps", "Capital FSD", "Hyperdiction", "HUD (draw)",
            "  transit: power scan", "  transit: planet check", "  transit: teleport", "  transit: zero speed", "  transit: effects", "  transit: pilot check" };
        private static readonly double[] average = new double[(int)Section.Count];
        private static readonly double[] peak = new double[(int)Section.Count];
        private static readonly double[] pending = new double[(int)Section.Count];   // sub-sections run once per drive: summed per tick

        public static long Begin()
        {
            return Stopwatch.GetTimestamp();
        }

        public static void End(Section section, long start)
        {
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            int i = (int)section;
            if (i >= MainSections)
            {
                pending[i] += ms;
                return;
            }

            Smooth(i, ms);
            if (section == Section.Hyperspace)
            {
                // The tick's transit breakdown is complete: fold the per-drive sums in as one sample each
                for (int j = MainSections; j < (int)Section.Count; j++)
                {
                    Smooth(j, pending[j]);
                    pending[j] = 0.0;
                }
            }
        }

        private static void Smooth(int i, double ms)
        {
            average[i] = average[i] * 0.98 + ms * 0.02;           // ~1 s moving average at 60 ticks/s
            peak[i] = ms > peak[i] ? ms : peak[i] * 0.995;        // slowly decaying peak
        }

        public static string Report(int hyperspaceActive, int hyperspaceTotal)
        {
            var sb = new StringBuilder();
            sb.Append("Per-tick cost (avg / peak ms). A tick has 16.7 ms in total.\n");
            double sum = 0.0;
            for (int i = 0; i < (int)Section.Count; i++)
            {
                if (i < MainSections)
                    sum += average[i];
                sb.Append(Names[i]).Append(": ")
                  .Append(average[i].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(" / ")
                  .Append(peak[i].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            sb.Append("Mod total: ").Append(sum.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms");
            sb.Append("  |  hyperspace drives busy: ").Append(hyperspaceActive).Append(" of ").Append(hyperspaceTotal);
            return sb.ToString();
        }
    }
}
