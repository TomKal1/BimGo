using System.Globalization;
using System.Net;
using System.Text;
using BimGo.Scene;
using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace BimGo.Calculations;

/// <summary>BimGo presentation of the shared engine's results. Never changes the physical solution.</summary>
public static class NativeLightingReport
{
    static string H(string value) => WebUtility.HtmlEncode(value ?? "");
    static string N(double value, string format = "0.##") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>The same unshaded HSV ramp used by model points and legends.</summary>
    public static string Colour(double value, double maximum)
    {
        var rgb = LightingCalcs.FalseColour((float)value, 0, (float)maximum);
        return $"#{(int)MathF.Round(rgb.X * 255):x2}{(int)MathF.Round(rgb.Y * 255):x2}{(int)MathF.Round(rgb.Z * 255):x2}";
    }

    public static string Html(LightingReportData data, IReadOnlyList<SolveStats> diagnostics, double scaleMax, bool lux = false,
        IReadOnlyList<CalculationEmitter> sources = null)
    {
        if (!double.IsFinite(scaleMax) || scaleMax <= 0) throw new ArgumentOutOfRangeException(nameof(scaleMax));
        string unit = lux ? "lx" : "fc";
        string V(double fc) => N(lux ? fc * LightingReport.LuxPerFootcandle : fc, lux ? "0" : "0.0");
        var b = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>");
        b.Append(H(data.CalcSetName)).Append(" — BimGo lighting</title><style>")
            .Append("body{font:14px Segoe UI,Arial,sans-serif;color:#1d2228;margin:28px}h1{font-size:24px}h2{font-size:18px;margin-top:24px}table{border-collapse:collapse;width:100%;margin:12px 0}th,td{border:1px solid #ccd;padding:6px;text-align:left;overflow-wrap:anywhere}th{background:#eef1f5}.note{color:#555}.warn{background:#fff6e0;padding:8px;margin:8px 0}.est{color:#8b4f00}svg{max-width:100%;height:auto}.zone{break-inside:avoid}@media print{body{margin:8mm;font-size:10px}thead{display:table-header-group}}")
            .Append("</style></head><body><h1>").Append(H(data.CalcSetName)).Append(" — Lighting calculation</h1><p>")
            .Append(H(data.ProjectName)).Append("<br>Run ").Append(data.RunAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .Append(" · Units: ").Append(unit).Append("</p>");
        foreach (var warning in data.Warnings) b.Append("<div class=\"warn\">").Append(H(warning)).Append("</div>");
        b.Append("<h2>Summary</h2><table><thead><tr><th>Zone</th><th>Level</th><th>Points</th><th>Avg (").Append(unit)
            .Append(")</th><th>Max</th><th>Min</th><th>Avg/Min</th><th>Max/Min</th><th>Luminaires</th><th>Watts*</th><th>LPD (W/ft²)*</th></tr></thead><tbody>");
        foreach (var z in data.Zones)
        {
            var s = z.Statistics;
            Row(b, z.DisplayName, z.LevelName, s.Count.ToString(CultureInfo.InvariantCulture), V(s.Average), V(s.Maximum), V(s.Minimum),
                LightingReport.FormatRatio(s.AverageToMinimum), LightingReport.FormatRatio(s.MaximumToMinimum), N(z.LuminaireCount), N(z.Watts), N(z.PowerDensity));
        }
        b.Append("</tbody></table><p class=\"note\">Ratios use unrounded values. Watts and LPD use available IES input watts; missing power data contributes zero and is not a verified connected-load total. Summary fixture counts include sources within each zone; the schedule lists the complete transport domain.</p>");
        b.Append("<h2>Luminaire schedule</h2><table><thead><tr><th>Type</th><th>Photometry / IES SHA-256</th><th>Source</th><th>Lumens</th><th>LLF</th><th>Watts*</th><th>Count</th></tr></thead><tbody>");
        foreach (var t in data.LuminaireTypes)
            Row(b, t.TypeName, t.Photometry, (t.Estimated ? "ESTIMATED: " : "") + t.Source, N(t.Lumens), N(t.LightLossFactor), N(t.Watts), N(t.Count));
        b.Append("</tbody></table><h2>Method and settings</h2><p>").Append(H(data.Method)).Append("</p><table>");
        foreach (var setting in data.Settings) Row(b, setting.Key, setting.Value);
        Row(b, "Report and model colour scale", "Blue → cyan → green → yellow → red; 0–" + V(scaleMax) + " " + unit);
        b.Append("</table><h2>Actual solve diagnostics</h2><table><thead><tr><th>Zone</th><th>Actual patch size (ft)</th><th>Patches</th><th>Iterations</th><th>Obstructed points excluded</th><th>Points without direct light</th><th>Solve time (s)</th></tr></thead><tbody>");
        for (int i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            Row(b, data.Zones[i].DisplayName, N(d.PatchSize, "0.####"), N(d.Patches), N(d.Iterations), N(d.PointsInsideObstructions), N(d.PointsWithoutDirectLight), N(d.TotalSeconds));
        }
        b.Append("</tbody></table><p class=\"note\">Iteration completion measures the solver stopping criterion. It does not establish mesh/ray refinement convergence or validate realistic appearance.</p>");
        foreach (var z in data.Zones)
        {
            b.Append("<section class=\"zone\"><h2>").Append(H(z.DisplayName)).Append("</h2><p>")
                .Append(z.OnWalkingSurfaces ? "Points follow walking surfaces; height above each surface: " : "Horizontal workplane above zone bottom: ")
                .Append(N(z.WorkplaneHeight)).Append(" ft · Grid: ").Append(N(z.GridSpacing)).Append(" ft</p>");
            Plan(b, z, data, scaleMax, lux, sources);
            b.Append("</section>");
        }
        b.Append("<p class=\"note\">Calculated in BimGo using the shared Lighting.Engine (WWTools diffuse engine). Results depend on the listed geometry, photometry, reflectances and light loss factors.</p></body></html>");
        return b.ToString();
    }

    static void Row(StringBuilder b, params string[] cells)
    {
        b.Append("<tr>");
        foreach (var cell in cells) b.Append("<td>").Append(H(cell)).Append("</td>");
        b.Append("</tr>");
    }

    static void Plan(StringBuilder b, CalcZone zone, LightingReportData data, double maximum, bool lux, IReadOnlyList<CalculationEmitter> sources)
    {
        var all = zone.Loops.SelectMany(l => l).ToArray();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        double scale = Math.Min(1000 / Math.Max(.01, maxX - minX), 600 / Math.Max(.01, maxY - minY));
        double width = (maxX - minX) * scale + 28, height = (maxY - minY) * scale + 80;
        double X(double x) => 14 + (x - minX) * scale;
        double Y(double y) => 14 + (maxY - y) * scale;
        b.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"Illuminance plan\" width=\"{N(width)}\" height=\"{N(height)}\" viewBox=\"0 0 {N(width)} {N(height)}\">");
        double cell = zone.GridSpacing * scale;
        for (int i = 0; i < zone.Points.Count; i++)
        {
            double value = zone.Illuminance[i]; if (!double.IsFinite(value)) continue;
            var p = zone.Points[i];
            b.Append($"<rect x=\"{N(X(p.X) - cell / 2)}\" y=\"{N(Y(p.Y) - cell / 2)}\" width=\"{N(cell)}\" height=\"{N(cell)}\" fill=\"{Colour(value, maximum)}\"><title>{N(lux ? value * LightingReport.LuxPerFootcandle : value, "0.0")} {(lux ? "lx" : "fc")}</title></rect>");
        }
        foreach (var loop in zone.Loops)
            b.Append("<polygon fill=\"none\" stroke=\"#1d2228\" stroke-width=\"1.5\" points=\"").Append(string.Join(" ", loop.Select(p => N(X(p.X)) + "," + N(Y(p.Y))))).Append("\"/>");
        for (int i = 0; i < data.LuminairePositions.Count; i++)
        {
            var p = data.LuminairePositions[i];
            if (!zone.ContainsPlan(p.X,p.Y) || p.Z < zone.BottomZ || p.Z > zone.TopZ) continue;
            var source = sources != null && i < sources.Count ? sources[i] : null;
            string fill = source?.Estimated == true ? "#ffc247" : "#fff";
            b.Append($"<circle class=\"luminaire\" cx=\"{N(X(p.X))}\" cy=\"{N(Y(p.Y))}\" r=\"4\" fill=\"{fill}\" stroke=\"#171717\"><title>Source {i + 1}: {H(source?.Type)}{(source?.Estimated == true ? " (estimated)" : "")}</title></circle>");
        }
        double legendY = height - 42, legendWidth = Math.Min(360, width - 28);
        string rampId = "lighting-scale-" + data.Zones.IndexOf(zone);
        b.Append($"<defs><linearGradient id=\"{rampId}\">");
        for (int i = 0; i <= 4; i++) b.Append($"<stop offset=\"{i * 25}%\" stop-color=\"{Colour(maximum * i / 4, maximum)}\"/>");
        b.Append($"</linearGradient></defs><rect x=\"14\" y=\"{N(legendY)}\" width=\"{N(legendWidth)}\" height=\"12\" fill=\"url(#{rampId})\"/>");
        for (int i = 0; i <= 4; i++) b.Append($"<text x=\"{N(14 + i * legendWidth / 4)}\" y=\"{N(legendY + 26)}\" font-size=\"10\">{N(maximum * i / 4 * (lux ? LightingReport.LuxPerFootcandle : 1), "0.#")}</text>");
        b.Append($"<text x=\"14\" y=\"{N(legendY - 8)}\" font-size=\"11\">Illuminance ({(lux ? "lx" : "fc")}); circles: zone luminaires (amber: estimated)</text></svg>");
    }
}
