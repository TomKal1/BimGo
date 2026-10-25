using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// A zone's design target in the report's units: an average (or an average range) and a
    /// largest allowed avg/min ratio. Typed as "20, 3" (20 avg, 3:1 avg/min), "1-3, 5" or
    /// "20 fc avg, 3:1 avg/min"; printed as "20 FC AVG, 3:1 AVG/MIN".
    /// </summary>
    public sealed class LightingTarget
    {
        private static readonly Regex Number = new Regex(@"\d+(?:\.\d+)?", RegexOptions.CultureInvariant);

        /// <summary>Lowest acceptable average; 0 = none.</summary>
        public double Average { get; set; }

        /// <summary>Highest acceptable average (a range like 1 - 3 fc); 0 = no upper limit.</summary>
        public double AverageMax { get; set; }

        /// <summary>Largest acceptable avg/min ratio (3 for 3:1); 0 = none.</summary>
        public double AverageToMinimum { get; set; }

        public bool IsEmpty => Average <= 0 && AverageMax <= 0 && AverageToMinimum <= 0;

        /// <summary>
        /// Reads a typed target. Blank text, or text with no numbers, is false (no override).
        /// "none" or "0" is an explicit empty target.
        /// </summary>
        public static bool TryParse(string? text, out LightingTarget target)
        {
            target = new LightingTarget();
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text!.Trim();
            if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)) return true;

            bool any = false;
            foreach (string part in trimmed.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                List<double> numbers = Number.Matches(part).Cast<Match>()
                    .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
                if (numbers.Count == 0) continue;
                any = true;
                bool ratio = part.IndexOf(':') >= 0 || part.IndexOf("min", StringComparison.OrdinalIgnoreCase) >= 0;
                if (ratio)
                {
                    target.AverageToMinimum = numbers[0];
                }
                else if (target.Average <= 0 && target.AverageMax <= 0)
                {
                    target.Average = numbers[0];
                    if (numbers.Count > 1 && numbers[1] > numbers[0]) target.AverageMax = numbers[1];
                }
                else
                {
                    // a second bare number after the average is the ratio ("20, 3")
                    target.AverageToMinimum = numbers[0];
                }
            }
            return any;
        }

        /// <summary>"20 FC AVG, 3:1 AVG/MIN", "1 - 3 FC AVG", "3:1 AVG/MIN", or "" when empty.</summary>
        public string Text(IlluminanceUnits units)
        {
            string u = LightingReport.UnitLabel(units).ToUpperInvariant();
            var parts = new List<string>();
            if (Average > 0 && AverageMax > Average) parts.Add($"{N(Average)} - {N(AverageMax)} {u} AVG");
            else if (Average > 0) parts.Add($"{N(Average)} {u} AVG");
            else if (AverageMax > 0) parts.Add($"≤ {N(AverageMax)} {u} AVG");
            if (AverageToMinimum > 0) parts.Add($"{N(AverageToMinimum)}:1 AVG/MIN");
            return string.Join(", ", parts);
        }

        /// <summary>The target as typed back into the editor ("20, 3", "1-3, 5").</summary>
        public string Input()
        {
            string avg = Average > 0 && AverageMax > Average ? $"{N(Average)}-{N(AverageMax)}" : Average > 0 ? N(Average) : string.Empty;
            if (AverageToMinimum <= 0) return avg.Length > 0 ? avg : "none";
            return avg.Length > 0 ? $"{avg}, {N(AverageToMinimum)}:1" : $"{N(AverageToMinimum)}:1";
        }

        /// <summary>
        /// Whether a zone meets the target, compared at the precision the table prints;
        /// null when the target is empty or the zone has no points.
        /// </summary>
        public bool? Meets(ZoneStatistics s, IlluminanceUnits units)
        {
            if (IsEmpty || s.Count == 0) return null;
            int digits = units == IlluminanceUnits.Lux ? 0 : 1;
            double avg = Math.Round(LightingReport.Convert(s.Average, units), digits);
            if (Average > 0 && avg < Average) return false;
            if (AverageMax > 0 && avg > AverageMax) return false;
            if (AverageToMinimum > 0)
            {
                double ratio = s.AverageToMinimum;
                if (double.IsInfinity(ratio) || double.IsNaN(ratio)) return false;
                if (Math.Round(ratio, 1) > AverageToMinimum) return false;
            }
            return true;
        }

        private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
