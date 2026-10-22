using System;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// Colours for point dots on plans, ElumTools style: blue for the lowest values through green,
    /// yellow and orange to red for the highest. The ramp is an HSV hue sweep (240° → 0°), slightly
    /// darkened so yellow still reads on white paper.
    /// </summary>
    public static class PointColors
    {
        /// <summary>Brightness (HSV value) of the ramp.</summary>
        public const double Brightness = 0.9;

        /// <summary>The colour for <paramref name="value"/> on a 0 to <paramref name="scaleMax"/> scale.</summary>
        public static (byte R, byte G, byte B) For(double value, double scaleMax)
        {
            double t = scaleMax > 0 && !double.IsNaN(value) ? value / scaleMax : 0;
            return Ramp(t);
        }

        /// <summary>The ramp at <paramref name="t"/> (0 = blue, 1 = red), clamped.</summary>
        public static (byte R, byte G, byte B) Ramp(double t)
        {
            t = IesPhotometry.Clamp(t, 0, 1);
            double hue = 240.0 * (1 - t) / 60.0; // sextant 0–4
            int sector = Math.Min(3, (int)Math.Floor(hue));
            double f = hue - sector;
            double v = Brightness;
            double r, g, b;
            switch (sector)
            {
                case 0: r = v; g = v * f; b = 0; break;           // red → yellow
                case 1: r = v * (1 - f); g = v; b = 0; break;     // yellow → green
                case 2: r = 0; g = v; b = v * f; break;           // green → cyan
                default: r = 0; g = v * (1 - f); b = v; break;    // cyan → blue
            }
            return (ToByte(r), ToByte(g), ToByte(b));
        }

        /// <summary>
        /// The top of the colour scale in footcandles: <paramref name="configuredMax"/> (in the
        /// report's units) when set, else the highest value in the set.
        /// </summary>
        public static double ScaleMax(double configuredMax, IlluminanceUnits units, double highestFootcandles)
        {
            if (configuredMax > 0)
                return units == IlluminanceUnits.Lux ? configuredMax / LightingReport.LuxPerFootcandle : configuredMax;
            return highestFootcandles > 0 ? highestFootcandles : 1;
        }

        private static byte ToByte(double channel) => (byte)Math.Round(IesPhotometry.Clamp(channel, 0, 1) * 255);
    }
}
