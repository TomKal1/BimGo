using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// The sheet results table (ElumTools style): a title, then NAME, TARGET, AVG, MAX, MIN,
    /// AVG/MIN, MAX/MIN and MAX/AVG for each zone. Builds the cell text and wraps it to the
    /// column widths; the Revit side only draws it.
    /// </summary>
    public static class ResultsTable
    {
        public const string DefaultTitle = "GENERAL ILLUMINANCE RESULTS";

        public static readonly string[] Headers = { "NAME", "TARGET", "AVG", "MAX", "MIN", "AVG /\nMIN", "MAX /\nMIN", "MAX /\nAVG" };

        /// <summary>Printed column widths in inches.</summary>
        public static readonly double[] ColumnWidths = { 2.0, 2.0, 0.75, 0.75, 0.75, 0.55, 0.55, 0.55 };

        /// <summary>Average capital-letter width as a share of the text height (Arial-like fonts).</summary>
        public const double CapWidth = 0.72;

        /// <summary>One row per zone, in natural name order.</summary>
        public static List<string[]> Rows(IEnumerable<CalcZone> zones, Func<CalcZone, LightingTarget?> target, IlluminanceUnits units)
        {
            string u = LightingReport.UnitLabel(units);
            string Value(double fc) => LightingReport.FormatValue(fc, units) + " " + u;
            return zones
                .OrderBy(z => z.DisplayName, NaturalComparer.Instance)
                .Select(z =>
                {
                    ZoneStatistics s = z.Statistics;
                    bool any = s.Count > 0;
                    return new[]
                    {
                        z.DisplayName.ToUpperInvariant(),
                        target(z)?.Text(units) ?? string.Empty,
                        any ? Value(s.Average) : "-",
                        any ? Value(s.Maximum) : "-",
                        any ? Value(s.Minimum) : "-",
                        any ? Ratio(s.AverageToMinimum) : "-",
                        any ? Ratio(s.MaximumToMinimum) : "-",
                        any ? Ratio(s.Average > 1e-9 ? s.Maximum / s.Average : double.PositiveInfinity) : "-",
                    };
                })
                .ToList();
        }

        /// <summary>A ratio as the table prints it ("1.6"; "N/A" when the minimum is zero).</summary>
        public static string Ratio(double ratio) =>
            double.IsInfinity(ratio) || double.IsNaN(ratio) ? "N/A" : ratio.ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>How many capitals fit across a column (less a small margin each side).</summary>
        public static int MaxChars(double columnInches, double textInches)
        {
            if (textInches <= 0) return int.MaxValue;
            double usable = columnInches - 2 * 0.4 * textInches;
            return Math.Max(1, (int)Math.Floor(usable / (CapWidth * textInches)));
        }

        /// <summary>Word-wraps text to lines of at most <paramref name="maxChars"/> (longer words are split).</summary>
        public static List<string> Wrap(string text, int maxChars)
        {
            var lines = new List<string>();
            foreach (string paragraph in (text ?? string.Empty).Split('\n'))
            {
                var line = new StringBuilder();
                foreach (string raw in paragraph.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string word = raw;
                    while (word.Length > maxChars)
                    {
                        if (line.Length > 0)
                        {
                            lines.Add(line.ToString());
                            line.Clear();
                        }
                        lines.Add(word.Substring(0, maxChars));
                        word = word.Substring(maxChars);
                    }
                    if (line.Length > 0 && line.Length + 1 + word.Length > maxChars)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }
                    if (line.Length > 0) line.Append(' ');
                    line.Append(word);
                }
                lines.Add(line.ToString());
            }
            return lines;
        }
    }
}
