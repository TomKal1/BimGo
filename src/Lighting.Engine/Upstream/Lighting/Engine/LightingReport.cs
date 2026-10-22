using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>Illuminance display units.</summary>
    public enum IlluminanceUnits
    {
        Footcandles,
        Lux,
    }

    /// <summary>One luminaire type row of the report's luminaire schedule.</summary>
    public sealed class LuminaireTypeSummary
    {
        /// <summary>Angle between samples of the polar curves (degrees).</summary>
        public const double PolarStep = 5.0;

        /// <summary>Matches <see cref="CalcZone.LuminaireTypeCounts"/>.</summary>
        public string TypeKey { get; set; } = string.Empty;

        public string TypeName { get; set; } = string.Empty;
        public string Photometry { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public double Lumens { get; set; }
        public double LightLossFactor { get; set; } = 1.0;
        public double Watts { get; set; }
        public int Count { get; set; }
        public bool Estimated { get; set; }

        /// <summary>
        /// Candela in the vertical plane along the luminaire's length (0°–180°), every
        /// <see cref="PolarStep"/> from −180° to 180° about nadir (negative = the 180° side).
        /// </summary>
        public double[] PolarLengthwise { get; set; } = Array.Empty<double>();

        /// <summary>The same across the luminaire (the 90°–270° plane).</summary>
        public double[] PolarCrosswise { get; set; } = Array.Empty<double>();

        /// <summary>Samples a polar curve from the photometry (no light loss factor).</summary>
        public static double[] SamplePolar(IesPhotometry photometry, bool crosswise)
        {
            int steps = (int)Math.Round(360 / PolarStep);
            var candela = new double[steps + 1];
            for (int k = 0; k <= steps; k++)
            {
                double a = (-180 + k * PolarStep) * Math.PI / 180;
                double side = Math.Sin(a);
                double down = Math.Cos(a);
                candela[k] = crosswise
                    ? photometry.CandelaToward(0, side, down)
                    : photometry.CandelaToward(side, 0, down);
            }
            return candela;
        }
    }

    /// <summary>Everything a lighting report shows. Built by the Revit runner; pure data.</summary>
    public sealed class LightingReportData
    {
        public string ProjectName { get; set; } = string.Empty;
        public string ModelPath { get; set; } = string.Empty;
        public string CalcSetName { get; set; } = string.Empty;
        public DateTime RunAt { get; set; } = DateTime.Now;
        public IlluminanceUnits Units { get; set; } = IlluminanceUnits.Footcandles;
        public string Method { get; set; } = string.Empty;
        public List<KeyValuePair<string, string>> Settings { get; } = new List<KeyValuePair<string, string>>();
        public List<CalcZone> Zones { get; } = new List<CalcZone>();
        public List<Vec3> LuminairePositions { get; } = new List<Vec3>();
        public List<LuminaireTypeSummary> LuminaireTypes { get; } = new List<LuminaireTypeSummary>();
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>The solve included interreflection (off = direct light only).</summary>
        public bool Interreflection { get; set; } = true;

        /// <summary>One detailed page per zone (views, isolux lines, surfaces, luminaires) instead of a plan each.</summary>
        public bool RoomPages { get; set; } = true;

        /// <summary>Top of the colour scale (fc) shared by every zone; 0 = a round number above each zone's maximum.</summary>
        public double ColorScaleMax { get; set; }
    }

    /// <summary>Formats results: values in the chosen units, a printable HTML report and a CSV of points.</summary>
    public static class LightingReport
    {
        public const double LuxPerFootcandle = 10.763910416709722;

        public static double Convert(double footcandles, IlluminanceUnits units) =>
            units == IlluminanceUnits.Lux ? footcandles * LuxPerFootcandle : footcandles;

        public static string UnitLabel(IlluminanceUnits units) => units == IlluminanceUnits.Lux ? "lx" : "fc";

        /// <summary>A point value as printed on plans: fc to one decimal, lux to whole numbers.</summary>
        public static string FormatValue(double footcandles, IlluminanceUnits units)
        {
            double v = Convert(footcandles, units);
            return units == IlluminanceUnits.Lux
                ? v.ToString("0", CultureInfo.InvariantCulture)
                : v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        public static string FormatRatio(double ratio) =>
            double.IsInfinity(ratio) || double.IsNaN(ratio) ? "N/A" : ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1";

        /// <summary>A short statistics block for a zone (used in plan notes and the summary view).</summary>
        public static string StatisticsText(CalcZone zone, IlluminanceUnits units)
        {
            string u = UnitLabel(units);
            ZoneStatistics s = zone.Statistics;
            string name = zone.DisplayName + (zone.OnWalkingSurfaces && !zone.IsMultiLevel && zone.Key.Contains("#") ? $" ({zone.LevelName})" : string.Empty);
            return $"{name}\nAvg {FormatValue(s.Average, units)} {u}  Max {FormatValue(s.Maximum, units)}  Min {FormatValue(s.Minimum, units)}\n" +
                   $"Avg/Min {FormatRatio(s.AverageToMinimum)}  Max/Min {FormatRatio(s.MaximumToMinimum)}";
        }

        public static string Csv(LightingReportData data)
        {
            var sb = new StringBuilder();
            string u = UnitLabel(data.Units);
            sb.AppendLine($"Zone,Level,X (ft),Y (ft),Z (ft),Total ({u}),Direct ({u}),Stair part");
            // Points inside casework, furniture or walls have blank values. A zone spanning
            // levels gives each point's own level.
            foreach (CalcZone zone in data.Zones)
            {
                for (int i = 0; i < zone.Points.Count; i++)
                {
                    Vec3 p = zone.Points[i];
                    double total = i < zone.Illuminance.Length ? zone.Illuminance[i] : double.NaN;
                    double direct = i < zone.Direct.Length ? zone.Direct[i] : double.NaN;
                    int level = i < zone.PointLevel.Length ? zone.PointLevel[i] : 0;
                    int part = i < zone.PointPart.Length ? zone.PointPart[i] : -1;
                    sb.Append(ReportHtml.CsvField(zone.DisplayName)).Append(',')
                      .Append(ReportHtml.CsvField(level < zone.Levels.Count ? zone.Levels[level].Name : zone.LevelName)).Append(',')
                      .Append(p.X.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                      .Append(p.Y.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                      .Append(p.Z.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                      .Append(CsvNumber(total, data.Units)).Append(',')
                      .Append(CsvNumber(direct, data.Units)).Append(',')
                      .Append(part >= 0 && part < zone.Parts.Count ? ReportHtml.CsvField(zone.Parts[part].Name) : string.Empty)
                      .AppendLine();
                }
            }
            return sb.ToString();
        }

        /// <summary>Blank for points left out (inside an obstruction).</summary>
        private static string CsvNumber(double footcandles, IlluminanceUnits units) =>
            double.IsNaN(footcandles) ? string.Empty : Convert(footcandles, units).ToString("0.###", CultureInfo.InvariantCulture);

        public static string Html(LightingReportData data)
        {
            string u = UnitLabel(data.Units);
            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
            sb.AppendLine($"<title>{H(data.CalcSetName)} – Lighting Calculation</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(ReportHtml.Styles);
            if (data.RoomPages) sb.AppendLine(RoomReport.Styles);
            sb.AppendLine("</style></head><body>");

            sb.AppendLine($"<h1>{H(data.CalcSetName)} — Lighting Calculation</h1>");
            sb.AppendLine($"<div class=\"meta\">{H(data.ProjectName)} · {H(data.ModelPath)}<br>Run {data.RunAt:yyyy-MM-dd HH:mm} · Units: {(data.Units == IlluminanceUnits.Lux ? "lux" : "footcandles")}</div>");

            foreach (string warning in data.Warnings)
                sb.AppendLine($"<div class=\"warn\">{H(warning)}</div>");

            // Summary table
            bool anyTarget = data.Zones.Any(z => z.Target != null && !z.Target.IsEmpty);
            sb.AppendLine("<h2>Summary</h2><table><tr><th>Zone</th><th>Level</th><th>Points</th>" +
                          $"<th>Avg ({u})</th><th>Max ({u})</th><th>Min ({u})</th><th>Avg/Min</th><th>Max/Min</th>" +
                          (anyTarget ? "<th>Target</th><th>Result</th>" : "") +
                          "<th>Luminaires</th><th>Watts</th><th>LPD (W/ft²)</th></tr>");
            foreach (CalcZone z in data.Zones)
            {
                ZoneStatistics s = z.Statistics;
                string target = "";
                if (anyTarget)
                {
                    bool? pass = z.Target?.Meets(s, data.Units);
                    target = $"<td>{H(z.Target?.Text(data.Units))}</td><td>{(pass == null ? "" : pass == true ? "Meets" : "<b>Misses</b>")}</td>";
                }
                string levels = z.IsMultiLevel ? z.Levels[0].Name + " – " + z.Levels[z.Levels.Count - 1].Name : z.LevelName;
                sb.AppendLine($"<tr><td>{H(z.DisplayName)}</td><td>{H(levels)}</td><td class=\"n\">{s.Count}</td>" +
                              $"<td class=\"n\">{FormatValue(s.Average, data.Units)}</td><td class=\"n\">{FormatValue(s.Maximum, data.Units)}</td>" +
                              $"<td class=\"n\">{FormatValue(s.Minimum, data.Units)}</td><td class=\"n\">{FormatRatio(s.AverageToMinimum)}</td>" +
                              $"<td class=\"n\">{FormatRatio(s.MaximumToMinimum)}</td>{target}<td class=\"n\">{z.LuminaireCount}</td>" +
                              $"<td class=\"n\">{z.Watts:0}</td><td class=\"n\">{z.PowerDensity:0.00}</td></tr>");
            }
            sb.AppendLine("</table>");

            // Luminaires
            sb.AppendLine("<h2>Luminaire schedule</h2><table><tr><th>Type</th><th>Photometry</th><th>Source</th>" +
                          "<th>Lumens</th><th>LLF</th><th>Watts</th><th>Count</th></tr>");
            foreach (LuminaireTypeSummary t in data.LuminaireTypes)
            {
                sb.AppendLine($"<tr><td>{H(t.TypeName)}</td><td>{H(t.Photometry)}</td>" +
                              $"<td{(t.Estimated ? " class=\"est\"" : "")}>{H(t.Source)}</td><td class=\"n\">{t.Lumens:0}</td>" +
                              $"<td class=\"n\">{t.LightLossFactor:0.00}</td><td class=\"n\">{t.Watts:0.#}</td><td class=\"n\">{t.Count}</td></tr>");
            }
            sb.AppendLine("</table>");

            // Method and settings
            sb.AppendLine("<h2>Method and settings</h2>");
            sb.AppendLine($"<p class=\"note\">{H(data.Method)}</p><table>");
            foreach (KeyValuePair<string, string> kv in data.Settings)
                sb.AppendLine($"<tr><th style=\"width:30%\">{H(kv.Key)}</th><td>{H(kv.Value)}</td></tr>");
            sb.AppendLine("</table>");

            if (data.RoomPages)
            {
                // Each page draws two 3D views; build them on a few cores at once.
                var pages = new string[data.Zones.Count];
                Parallel.For(0, pages.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)) },
                    i => pages[i] = RoomReport.Page(data.Zones[i], data));
                sb.AppendLine("<p class=\"note\">Calculated with WW Tools Lighting. Results depend on the photometric files, light loss factors, " +
                              "reflectances and model geometry listed above; review them before relying on the values. One page per zone follows.</p>");
                foreach (string page in pages) sb.Append(page);
                sb.AppendLine(RoomReport.Script);
                sb.AppendLine("</body></html>");
                return sb.ToString();
            }

            // Plans
            double scaleMax = data.Zones.Where(z => z.Statistics.Count > 0).Select(z => z.Statistics.Maximum).DefaultIfEmpty(1).Max();
            foreach (CalcZone z in data.Zones)
            {
                sb.AppendLine("<div class=\"zone\">");
                sb.AppendLine($"<h2>{H(z.DisplayName)} <span class=\"note\">({H(z.LevelName)})</span></h2>");
                sb.AppendLine($"<div class=\"note\">{H(StatisticsText(z, data.Units).Replace("\n", " · "))}" +
                              $" · Work plane {z.WorkplaneHeight:0.##} ft · Grid {z.GridSpacing:0.##} ft · Reflectances C/W/F {z.Reflectances.Ceiling:0.##}/{z.Reflectances.Wall:0.##}/{z.Reflectances.Floor:0.##}</div>");
                sb.AppendLine(ZoneSvg(z, data, scaleMax));
                sb.AppendLine("</div>");
            }

            sb.AppendLine("<p class=\"note\">Calculated with WW Tools Lighting. Results depend on the photometric files, light loss factors, " +
                          "reflectances and model geometry listed above; review them before relying on the values.</p>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string ZoneSvg(CalcZone zone, LightingReportData data, double scaleMax)
        {
            (Vec3 min, Vec3 max) = zone.PlanBounds;
            double w = Math.Max(1, max.X - min.X);
            double h = Math.Max(1, max.Y - min.Y);
            const double maxPixels = 900;
            double scale = Math.Min(maxPixels / w, 640 / h);
            scale = Math.Min(scale, 60);
            double pad = 14;
            double pw = w * scale + 2 * pad;
            double ph = h * scale + 2 * pad + 30;
            string X(double x) => (pad + (x - min.X) * scale).ToString("0.#", CultureInfo.InvariantCulture);
            string Y(double y) => (pad + (max.Y - y) * scale).ToString("0.#", CultureInfo.InvariantCulture);

            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{pw:0}\" height=\"{ph:0}\" viewBox=\"0 0 {pw:0} {ph:0}\">");

            double cell = Math.Max(2, zone.GridSpacing * scale);
            double fontSize = Math.Max(5, Math.Min(11, cell * 0.36));
            bool showText = cell >= 16;
            for (int i = 0; i < zone.Points.Count && i < zone.Illuminance.Length; i++)
            {
                Vec3 p = zone.Points[i];
                double v = zone.Illuminance[i];
                if (double.IsNaN(v)) continue;
                string colour = Ramp(scaleMax > 0 ? v / scaleMax : 0, out bool dark);
                double cx = pad + (p.X - min.X) * scale;
                double cy = pad + (max.Y - p.Y) * scale;
                sb.Append($"<rect x=\"{cx - cell / 2:0.#}\" y=\"{cy - cell / 2:0.#}\" width=\"{cell:0.#}\" height=\"{cell:0.#}\" fill=\"{colour}\" opacity=\"0.85\"/>");
                if (showText)
                {
                    sb.Append($"<text x=\"{cx:0.#}\" y=\"{cy + fontSize * 0.35:0.#}\" font-size=\"{fontSize:0.#}\" text-anchor=\"middle\" fill=\"{(dark ? "#fff" : "#111")}\">{FormatValue(v, data.Units)}</text>");
                }
            }

            foreach (List<Vec3> loop in zone.Loops)
            {
                sb.Append("<polygon fill=\"none\" stroke=\"#1d2228\" stroke-width=\"1.5\" points=\"");
                sb.Append(string.Join(" ", loop.Select(p => X(p.X) + "," + Y(p.Y))));
                sb.Append("\"/>");
            }

            foreach (Vec3 l in data.LuminairePositions)
            {
                if (l.X < min.X - 0.5 || l.X > max.X + 0.5 || l.Y < min.Y - 0.5 || l.Y > max.Y + 0.5) continue;
                if (l.Z < zone.BottomZ - 1 || l.Z > zone.TopZ + 3) continue;
                sb.Append($"<circle cx=\"{X(l.X)}\" cy=\"{Y(l.Y)}\" r=\"4\" fill=\"#ffd84a\" stroke=\"#4a3b00\" stroke-width=\"1\"/>");
            }

            // Legend
            double ly = ph - 18;
            for (int k = 0; k < 10; k++)
            {
                string c = Ramp(k / 9.0, out _);
                sb.Append($"<rect x=\"{pad + k * 22:0}\" y=\"{ly:0}\" width=\"22\" height=\"10\" fill=\"{c}\"/>");
            }
            sb.Append($"<text x=\"{pad:0}\" y=\"{ly - 3:0}\" font-size=\"9\">0</text>");
            sb.Append($"<text x=\"{pad + 220:0}\" y=\"{ly - 3:0}\" font-size=\"9\" text-anchor=\"end\">{FormatValue(scaleMax, data.Units)} {UnitLabel(data.Units)}</text>");
            sb.Append($"<circle cx=\"{pad + 240:0}\" cy=\"{ly + 5:0}\" r=\"4\" fill=\"#ffd84a\" stroke=\"#4a3b00\"/>");
            sb.Append($"<text x=\"{pad + 248:0}\" y=\"{ly + 9:0}\" font-size=\"9\">luminaire</text>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>A dark-blue → teal → yellow ramp (viridis-like) for 0–1.</summary>
        private static string Ramp(double t, out bool dark)
        {
            t = IesPhotometry.Clamp(t, 0, 1);
            double[][] stops =
            {
                new[] { 0.00, 68, 1, 84 },
                new[] { 0.25, 59, 82, 139 },
                new[] { 0.50, 33, 145, 140 },
                new[] { 0.75, 94, 201, 98 },
                new[] { 1.00, 253, 231, 37 },
            };
            int i = 1;
            while (i < stops.Length - 1 && t > stops[i][0]) i++;
            double[] a = stops[i - 1];
            double[] b = stops[i];
            double f = (t - a[0]) / (b[0] - a[0]);
            int r = (int)Math.Round(a[1] + (b[1] - a[1]) * f);
            int g = (int)Math.Round(a[2] + (b[2] - a[2]) * f);
            int bl = (int)Math.Round(a[3] + (b[3] - a[3]) * f);
            dark = 0.2126 * r + 0.7152 * g + 0.0722 * bl < 128;
            return $"#{r:x2}{g:x2}{bl:x2}";
        }

        private static string H(string? text) => ReportHtml.Encode(text);
    }
}
