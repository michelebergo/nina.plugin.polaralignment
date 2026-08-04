using System;
using System.Globalization;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// One log line per axis, carrying everything needed to read the $J= commands that follow
    /// it. Without this a support log only shows raw step counts, and the effective gear
    /// ratio, backlash, mode and speed have to be recovered by arithmetic.
    /// </summary>
    public static class OapaParameterSummary {

        public static string ForAxis(string axisLabel, double gearRatio, double backlashArcmin, string backlashMode, double speed) {
            var c = CultureInfo.InvariantCulture;
            var line = $"OAPA {axisLabel}: {gearRatio.ToString("0.##", c)} steps/arcmin, " +
                       $"backlash {backlashArcmin.ToString("0.##", c)}', mode {backlashMode}, " +
                       $"speed {speed.ToString("0.##", c)} steps/s";

            if (gearRatio > 0) {
                var arcminPerSecond = speed / gearRatio;
                line += $" (~ {arcminPerSecond.ToString("0.##", c)} '/s)";
            }

            return line;
        }
    }
}
