using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// One printable page per room for the lighting report: a verdict against the target, the
    /// key figures, the work plane with isolux lines, cutaway 3D views of the room's surfaces
    /// (false colour and shaded), how much of the room reaches each level, the surfaces' light,
    /// and the luminaires in the room with their polar curves.
    /// </summary>
    public static class RoomReport
    {
        /// <summary>Extra styles for the room pages (added to <see cref="ReportHtml.Styles"/>).</summary>
        public const string Styles = @"
.room{break-before:page;page-break-before:always;padding-top:4px}
.room-head{display:flex;justify-content:space-between;align-items:flex-start;gap:12px;border-bottom:2px solid #1d2228;padding-bottom:6px}
.room-head h2{border:0;margin:0;padding:0;font-size:19px}.eyebrow{color:#555;font-size:11px}
.verdict{border-radius:4px;padding:5px 10px;text-align:right;font-weight:700;font-size:14px;white-space:nowrap}
.verdict small{display:block;font-weight:400;font-size:10.5px}
.verdict.pass{background:#e3f4e8;color:#14532d;border:1px solid #6fbf87}
.verdict.fail{background:#fde8e8;color:#7f1d1d;border:1px solid #e08585}
.verdict.none{background:#eef1f5;color:#444;border:1px solid #c9ced6}
.kpis{display:flex;gap:6px;margin:8px 0}.kpi{flex:1;border:1px solid #d7dbe1;border-radius:4px;padding:4px 7px}
.kpi b{display:block;font-size:16px;font-variant-numeric:tabular-nums}.kpi span{color:#555;font-size:10.5px}
.views{display:grid;grid-template-columns:auto 1fr;gap:10px}.views img{margin:0 auto}.lower{display:grid;grid-template-columns:0.85fr 1.35fr 1fr;gap:10px;margin-top:4px}
.overview{display:grid;grid-template-columns:1fr auto;gap:12px;align-items:start}
.room figure{margin:0}.room figcaption{color:#555;font-size:10.5px;margin-top:2px}
.room img{display:block;max-width:100%;height:auto}.room table{margin:2px 0 6px;font-size:11px}
.room th,.room td{padding:2px 5px}.room h3{font-size:12px;margin:4px 0 2px}
.room tr.lvl td{background:#f4f6f9;font-weight:600}.room tr.part td:first-child{padding-left:16px}
.scalebar{display:flex;align-items:flex-start;gap:6px;font-size:10.5px;color:#333;margin-top:3px}
.scalebar .ramp{position:relative;display:inline-block;height:9px;width:220px;border:1px solid #888;margin:1px 8px 12px}
.scalebar .ramp em{position:absolute;top:11px;transform:translateX(-50%);font-style:normal;font-size:9.5px;white-space:nowrap}
.scalebar .ramp b{position:absolute;top:7px;width:1px;height:4px;background:#555}.lum{width:40px;text-align:center}.room .note{font-size:10.5px}
.zoomable{cursor:zoom-in}.zhint{color:#2c5d8f}
#zoom{display:none;position:fixed;inset:0;background:rgba(20,24,28,.93);z-index:99;overflow:hidden;user-select:none}
#zoom.on{display:block}#zoom .zbar{position:absolute;top:8px;left:0;right:0;text-align:center;color:#dfe3ea;font-size:12px;pointer-events:none;z-index:1}
#zoom .zstage>*{position:absolute;left:0;top:0;transform-origin:0 0;background:#fff;cursor:grab;max-width:none;box-shadow:0 4px 30px #000}
@media print{#zoom,.zhint{display:none!important}}
@page{size:letter landscape;margin:9mm}";

        /// <summary>
        /// Click any view or plan to see it full screen: scroll zooms about the cursor, dragging
        /// moves it, double-click fits it again, Esc or a click outside closes it.
        /// </summary>
        public const string Script = @"<script>(function(){
var o=document.createElement('div');o.id='zoom';o.innerHTML='<div class=""zbar"">Scroll to zoom · drag to move · double-click to fit · Esc or click outside to close</div><div class=""zstage""></div>';
document.body.appendChild(o);var st=o.querySelector('.zstage'),el=null,s=1,x=0,y=0,d=null,moved=false;
function put(){el.style.transform='translate('+x+'px,'+y+'px) scale('+s+')';}
function fit(){var r=el.__r,W=innerWidth*0.94,H=innerHeight*0.88,w=Math.min(W,H*r),h=w/r;el.style.width=w+'px';el.style.height=h+'px';s=1;x=(innerWidth-w)/2;y=(innerHeight-h)/2+12;put();}
function show(src){st.innerHTML='';el=src.cloneNode(true);el.classList.remove('zoomable');el.removeAttribute('width');el.removeAttribute('height');
var r=src.tagName=='IMG'?src.naturalWidth/src.naturalHeight:(src.viewBox&&src.viewBox.baseVal&&src.viewBox.baseVal.width?src.viewBox.baseVal.width/src.viewBox.baseVal.height:src.clientWidth/src.clientHeight);
el.__r=r||1;st.appendChild(el);o.classList.add('on');fit();}
function hide(){o.classList.remove('on');st.innerHTML='';el=null;}
document.addEventListener('click',function(e){var t=e.target.closest&&e.target.closest('.zoomable');if(t&&!o.contains(t)){e.preventDefault();show(t);}});
o.addEventListener('wheel',function(e){if(!el)return;e.preventDefault();var f=Math.exp(-e.deltaY*0.0015),n=Math.max(0.5,Math.min(12,s*f));f=n/s;x=e.clientX-(e.clientX-x)*f;y=e.clientY-(e.clientY-y)*f;s=n;put();},{passive:false});
o.addEventListener('mousedown',function(e){d={x:e.clientX-x,y:e.clientY-y};moved=false;});
addEventListener('mousemove',function(e){if(!d||!el)return;var nx=e.clientX-d.x,ny=e.clientY-d.y;if(Math.abs(nx-x)+Math.abs(ny-y)>2)moved=true;x=nx;y=ny;put();});
addEventListener('mouseup',function(e){var was=d;d=null;if(was&&!moved&&el&&!el.contains(e.target))hide();});
o.addEventListener('dblclick',function(){if(el)fit();});
addEventListener('keydown',function(e){if(e.key=='Escape')hide();});
addEventListener('resize',function(){if(el)fit();});
})();</script>";

        /// <summary>Size in CSS pixels of the work plane view.</summary>
        private const int ViewWidth = 440;
        private const int ViewHeight = 265;

        /// <summary>The largest the cutaway view gets in CSS pixels (the rest of the row, as tall as the plan with its scale).</summary>
        private const int CutawayWidth = 540;
        private const int CutawayHeight = 300;

        /// <summary>A level's own page has the room for a taller cutaway.</summary>
        private const int LevelCutawayHeight = 470;

        /// <summary>The whole of a zone spanning levels, beside its breakdown.</summary>
        private const int OverviewWidth = 420;
        private const int OverviewHeight = 400;

        /// <summary>Image pixels per CSS pixel for the 3D views, so they stay sharp when printed or zoomed.</summary>
        private const double CutawayResolution = 3;

        /// <summary>
        /// The page for one zone (an HTML section). A zone spanning levels gets an overview page
        /// (the whole zone's figures, a breakdown by level and stair part, and the whole zone in
        /// 3D) and then a page per level with its own plan and cutaway.
        /// </summary>
        public static string Page(CalcZone zone, LightingReportData data)
        {
            if (zone.IsMultiLevel && zone.Statistics.Count > 0) return MultiLevelPages(zone, data);

            IlluminanceUnits units = data.Units;
            ZoneStatistics s = zone.Statistics;
            double scaleMax = ScaleMax(zone, data);
            double targetFc = TargetFc(zone, units);
            var sb = new StringBuilder();

            sb.AppendLine("<section class=\"room\">");
            Head(sb, zone, data, zone.LevelName, zone.DisplayName, showVerdict: true);
            if (s.Count == 0)
            {
                sb.AppendLine("<p class=\"note\">No calculation points: the room is smaller than the grid, or every point fell inside furniture or walls.</p></section>");
                return sb.ToString();
            }
            Kpis(sb, zone, data, targetFc, power: true);
            Views(sb, zone, data, scaleMax, targetFc);

            // A stair on one level: its landings and flights.
            if (zone.Parts.Count > 0) sb.Append(BreakdownTable(zone, units));

            Lower(sb, zone, data, scaleMax, targetFc);
            sb.Append(LuminaireTable(zone, data));
            Basis(sb, zone, data);
            sb.AppendLine("</section>");
            return sb.ToString();
        }

        private static string MultiLevelPages(CalcZone zone, LightingReportData data)
        {
            IlluminanceUnits units = data.Units;
            // One colour scale for every level, so the pages compare.
            double scaleMax = ScaleMax(zone, data);
            double targetFc = TargetFc(zone, units);
            string levelRange = zone.Levels[0].Name + " – " + zone.Levels[zone.Levels.Count - 1].Name;
            var sb = new StringBuilder();

            // Overview: the whole zone.
            sb.AppendLine("<section class=\"room\">");
            Head(sb, zone, data, levelRange, zone.DisplayName, showVerdict: true);
            Kpis(sb, zone, data, targetFc, power: true);
            sb.Append("<div class=\"overview\"><div>");
            sb.Append(BreakdownTable(zone, units));
            sb.Append($"<p class=\"note\">{H(PointsText(zone))} The whole {H(zone.Kind.ToLowerInvariant())} is solved at once, so light " +
                      "passes between its levels; each level has its own page with a plan and a cutaway view.</p>");
            Lower(sb, zone, data, scaleMax, targetFc, shadedView: false);
            sb.Append("</div><figure>");
            if (zone.Surfaces.Count > 0)
            {
                PngImage view = RoomRender.Render(zone, data.LuminairePositions, RoomRenderMode.FalseColour, scaleMax,
                    OverviewWidth, OverviewHeight, CutawayResolution);
                sb.Append(Img(view, "Cutaway view of the whole zone coloured by illuminance"));
                sb.Append(ScaleBar(scaleMax, units));
                sb.Append("<figcaption>All levels, from above the south-east corner with the near walls cut away. " + ZoomHint + "</figcaption>");
            }
            sb.AppendLine("</figure></div>");
            sb.Append(LuminaireTable(zone, data));
            Basis(sb, zone, data);
            sb.AppendLine("</section>");

            // A page per level.
            for (int level = 0; level < zone.Levels.Count; level++)
            {
                CalcZone slice = WalkingSurfaces.Slice(zone, level);
                if (slice.Points.Count == 0) continue;
                sb.AppendLine("<section class=\"room\">");
                Head(sb, slice, data, slice.LevelName, $"{zone.DisplayName} — {slice.LevelName}", showVerdict: false);
                if (slice.Statistics.Count == 0)
                {
                    sb.AppendLine("<p class=\"note\">Every point on this level fell inside furniture or walls.</p></section>");
                    continue;
                }
                Kpis(sb, slice, data, targetFc, power: false);
                Views(sb, slice, data, scaleMax, targetFc, LevelCutawayHeight);
                if (slice.Parts.Count > 0) sb.Append(BreakdownTable(slice, units));
                sb.AppendLine("</section>");
            }
            return sb.ToString();
        }

        private const string ZoomHint = "<span class=\"zhint\">Click to enlarge.</span>";

        private static double TargetFc(CalcZone zone, IlluminanceUnits units) =>
            zone.Target != null && zone.Target.Average > 0 ? ToFootcandles(zone.Target.Average, units) : 0;

        private static void Head(StringBuilder sb, CalcZone zone, LightingReportData data, string levelText, string title, bool showVerdict)
        {
            sb.Append("<div class=\"room-head\"><div>");
            string eyebrow = string.Join(" · ", new[] { levelText, zone.Kind, data.CalcSetName, data.RunAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            sb.Append($"<div class=\"eyebrow\">{H(eyebrow)}</div>");
            sb.Append($"<h2>{H(title)}</h2></div>");
            if (showVerdict) sb.Append(Verdict(zone, data.Units));
            sb.AppendLine("</div>");
        }

        private static void Kpis(StringBuilder sb, CalcZone zone, LightingReportData data, double targetFc, bool power)
        {
            IlluminanceUnits units = data.Units;
            string u = LightingReport.UnitLabel(units);
            ZoneStatistics s = zone.Statistics;
            sb.Append("<div class=\"kpis\">");
            Kpi(sb, $"{V(s.Average, units)} {u}", "Average");
            Kpi(sb, $"{V(s.Maximum, units)} {u}", "Maximum");
            Kpi(sb, $"{V(s.Minimum, units)} {u}", "Minimum");
            Kpi(sb, LightingReport.FormatRatio(s.AverageToMinimum), "Avg / min");
            Kpi(sb, LightingReport.FormatRatio(s.MaximumToMinimum), "Max / min");
            if (targetFc > 0)
                Kpi(sb, $"{RoomStatistics.Coverage(zone, targetFc) * 100:0} %", $"of points ≥ {V(targetFc, units)} {u}");
            if (data.Interreflection)
                Kpi(sb, $"{RoomStatistics.ReflectedShare(zone) * 100:0} %", "of the average is reflected light");
            if (power)
                Kpi(sb, $"{zone.PowerDensity:0.00} W/ft²", $"{zone.LuminaireCount} luminaire(s), {zone.Watts:0} W" +
                    (zone.OnWalkingSurfaces ? $" over {zone.FloorArea:0} ft² walked on" : string.Empty));
            else
                Kpi(sb, $"{s.Count}", $"points, {zone.FloorArea:0} ft² walked on");
            sb.AppendLine("</div>");
        }

        /// <summary>The plan with isolux lines and the large cutaway, side by side.</summary>
        private static void Views(StringBuilder sb, CalcZone zone, LightingReportData data, double scaleMax, double targetFc,
            int cutawayHeight = CutawayHeight)
        {
            IlluminanceUnits units = data.Units;
            string u = LightingReport.UnitLabel(units);
            ZoneStatistics s = zone.Statistics;
            List<double> levels = Isolux.Levels(s.Minimum, s.Maximum, units, targetFc);
            List<IsoluxLine> lines = Isolux.Trace(zone, levels, targetFc);
            sb.Append("<div class=\"views\"><figure>");
            sb.Append(PlanSvg(zone, data, scaleMax, lines));
            sb.Append(ScaleBar(scaleMax, units));
            sb.Append($"<figcaption>{H(PointsText(zone))} Point colours and isolux lines ({u})" +
                      (targetFc > 0 ? "; the heavy line is the target average" : string.Empty) +
                      (zone.Parts.Count > 0 ? "; dashed outlines are stair landings and flights" : string.Empty) + ". " + ZoomHint + "</figcaption>");
            sb.Append("</figure><figure>");
            if (zone.Surfaces.Count > 0)
            {
                PngImage view = RoomRender.Render(zone, data.LuminairePositions, RoomRenderMode.FalseColour, scaleMax,
                    CutawayWidth, cutawayHeight, CutawayResolution);
                sb.Append(Img(view, "Cutaway view of the room coloured by illuminance"));
                sb.Append($"<figcaption>Illuminance ({u}) on the room's surfaces on the plan's colour scale, from above the south-east corner " +
                          (zone.OnWalkingSurfaces && zone.Key.Contains("#") ? "with the level above and near walls cut away. " : "with the ceiling and near walls cut away. ") +
                          ZoomHint + "</figcaption>");
            }
            else
            {
                sb.Append("<p class=\"note\">No surface results for this room.</p>");
            }
            sb.AppendLine("</figure></div>");
        }

        /// <summary>Where the points are, as a sentence.</summary>
        private static string PointsText(CalcZone zone)
        {
            if (!zone.OnWalkingSurfaces) return $"Work plane at {Feet(zone.WorkplaneHeight)}.";
            if (zone.PointsOnSurface) return "Points on the treads, landings and floors.";
            return $"Points {Feet(zone.WorkplaneHeight)} above each floor and balcony, and on stair treads and landings.";
        }

        /// <summary>Shaded view, surfaces and coverage.</summary>
        private static void Lower(StringBuilder sb, CalcZone zone, LightingReportData data, double scaleMax, double targetFc, bool shadedView = true)
        {
            IlluminanceUnits units = data.Units;
            sb.Append(shadedView ? "<div class=\"lower\"><figure>" : "<div class=\"lower\" style=\"grid-template-columns:1fr\">");
            if (shadedView)
            {
                if (zone.Surfaces.Count > 0)
                {
                    PngImage shaded = RoomRender.Render(zone, data.LuminairePositions, RoomRenderMode.Shaded, scaleMax,
                        260, 170, CutawayResolution);
                    sb.Append(Img(shaded, "Shaded cutaway view of the room"));
                    sb.Append("<figcaption>Shaded by luminance: how bright the surfaces look.</figcaption>");
                }
                sb.Append("</figure>");
            }
            sb.Append("<div>");
            sb.Append(SurfaceTable(zone, units, data.Interreflection));
            sb.Append("</div><figure>");
            sb.Append(CoverageSvg(zone, units, scaleMax, targetFc));
            sb.Append($"<figcaption>Share of the {(zone.OnWalkingSurfaces ? "points" : "work plane")} at or above each level.</figcaption></figure></div>");
        }

        private static void Basis(StringBuilder sb, CalcZone zone, LightingReportData data)
        {
            Reflectances r = zone.Reflectances;
            sb.Append($"<p class=\"note\">{(data.Interreflection ? "Direct light from IES data plus interreflection (radiosity)" : "Direct light only (no interreflection)")}; " +
                      $"grid {Feet(zone.GridSpacing)}; reflectances ceiling {Pct(r.Ceiling)}, walls {Pct(r.Wall)}, floor {Pct(r.Floor)}, furniture {Pct(r.Furniture)} " +
                      "unless a material sets its own; maintained values (light loss factors applied). " +
                      (zone.OnWalkingSurfaces
                          ? $"Footprint {zone.Area:0} ft², {zone.FloorArea:0} ft² walked on (power density is over this). Floors with less than {WalkingSurfaces.Headroom:0.#} ft clear over them (under a flight) have no points. "
                          : $"Area {zone.Area:0} ft². ") +
                      "Points inside furniture or walls are left out. Polar curves: solid along the luminaire, " +
                      "dashed across it, nadir down, scaled to each type's peak.</p>");
        }

        /// <summary>Statistics per level and per stair landing and flight.</summary>
        private static string BreakdownTable(CalcZone zone, IlluminanceUnits units)
        {
            List<ZoneBreakdownRow> rows = WalkingSurfaces.Breakdown(zone);
            if (rows.Count == 0) return string.Empty;
            string u = LightingReport.UnitLabel(units);
            var sb = new StringBuilder();
            sb.Append($"<h3>{(zone.IsMultiLevel ? "By level and stair part" : "Stair landings and flights")}</h3>");
            sb.Append($"<table><tr><th>Part</th><th>Points</th><th>Avg ({u})</th><th>Max</th><th>Min</th><th>Avg/Min</th><th>Max/Min</th></tr>");
            foreach (ZoneBreakdownRow row in rows)
            {
                ZoneStatistics s = row.Statistics;
                string css = row.Kind == "Level" ? "lvl" : zone.IsMultiLevel ? "part" : string.Empty;
                sb.Append($"<tr class=\"{css}\"><td>{H(row.Name)}</td><td class=\"n\">{s.Count}</td><td class=\"n\">{V(s.Average, units)}</td>" +
                          $"<td class=\"n\">{V(s.Maximum, units)}</td><td class=\"n\">{V(s.Minimum, units)}</td>" +
                          $"<td class=\"n\">{LightingReport.FormatRatio(s.AverageToMinimum)}</td><td class=\"n\">{LightingReport.FormatRatio(s.MaximumToMinimum)}</td></tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        /// <summary>The top of the colour scale (fc): the report's fixed scale, else a round number above the room's maximum.</summary>
        public static double ScaleMax(CalcZone zone, LightingReportData data)
        {
            if (data.ColorScaleMax > 0) return data.ColorScaleMax;
            double max = Math.Max(zone.Statistics.Maximum, 1e-6);
            double inUnits = LightingReport.Convert(max, data.Units);
            return ToFootcandles(RoundUp(inUnits), data.Units);
        }

        /// <summary>The smallest of 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8 × 10ⁿ at or above <paramref name="value"/>.</summary>
        public static double RoundUp(double value)
        {
            if (value <= 0) return 1;
            double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (double m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
                if (m * power >= value - 1e-9) return m * power;
            return 10 * power;
        }

        private static string Verdict(CalcZone zone, IlluminanceUnits units)
        {
            LightingTarget? target = zone.Target;
            bool? meets = target?.Meets(zone.Statistics, units);
            if (meets == null || target == null)
                return "<div class=\"verdict none\">NO TARGET<small>Set one in the calc set to check this room</small></div>";
            string text = H(target.Text(units));
            return meets == true
                ? $"<div class=\"verdict pass\">✓ MEETS TARGET<small>{text}</small></div>"
                : $"<div class=\"verdict fail\">✗ MISSES TARGET<small>{text}{H(Shortfall(zone, target, units))}</small></div>";
        }

        /// <summary>Why a zone misses its target, e.g. " — avg 24.1, avg/min 4.2:1".</summary>
        private static string Shortfall(CalcZone zone, LightingTarget target, IlluminanceUnits units)
        {
            ZoneStatistics s = zone.Statistics;
            var parts = new List<string>();
            double avg = LightingReport.Convert(s.Average, units);
            if ((target.Average > 0 && avg < target.Average) || (target.AverageMax > 0 && avg > target.AverageMax))
                parts.Add($"avg {V(s.Average, units)}");
            if (target.AverageToMinimum > 0 && !(s.AverageToMinimum <= target.AverageToMinimum + 0.05))
                parts.Add($"avg/min {LightingReport.FormatRatio(s.AverageToMinimum)}");
            return parts.Count == 0 ? string.Empty : " — " + string.Join(", ", parts);
        }

        /// <summary>A rendered view at its size in CSS pixels (the image has <see cref="CutawayResolution"/> × as many).</summary>
        private static string Img(PngImage image, string alt) =>
            $"<img class=\"zoomable\" src=\"{image.ToDataUri()}\" width=\"{Math.Round(image.Width / CutawayResolution)}\" " +
            $"height=\"{Math.Round(image.Height / CutawayResolution)}\" alt=\"{H(alt)}\">";

        private static void Kpi(StringBuilder sb, string value, string label) =>
            sb.Append($"<div class=\"kpi\"><b>{H(value)}</b><span>{H(label)}</span></div>");

        /// <summary>The work plane: coloured points, isolux lines, the boundary and the luminaires.</summary>
        private static string PlanSvg(CalcZone zone, LightingReportData data, double scaleMax, List<IsoluxLine> lines)
        {
            (Vec3 min, Vec3 max) = zone.PlanBounds;
            double w = Math.Max(1, max.X - min.X), h = Math.Max(1, max.Y - min.Y);
            const double pad = 10;
            const double scaleBarRoom = 12;
            double scale = Math.Min((ViewWidth - 2 * pad) / w, (ViewHeight - 2 * pad - scaleBarRoom) / h);
            double ox = (ViewWidth - w * scale) / 2, oy = (ViewHeight - scaleBarRoom - h * scale) / 2;
            string X(double x) => F(ox + (x - min.X) * scale);
            string Y(double y) => F(oy + (max.Y - y) * scale);

            var sb = new StringBuilder();
            sb.Append($"<svg class=\"zoomable\" xmlns=\"http://www.w3.org/2000/svg\" width=\"{ViewWidth}\" height=\"{ViewHeight}\" viewBox=\"0 0 {ViewWidth} {ViewHeight}\" role=\"img\" aria-label=\"Work plane plan with isolux lines\">");

            double cell = Math.Max(1.5, zone.GridSpacing * scale);
            for (int i = 0; i < zone.Points.Count && i < zone.Illuminance.Length; i++)
            {
                double v = zone.Illuminance[i];
                if (double.IsNaN(v)) continue;
                (byte r, byte g, byte b) = PointColors.For(v, scaleMax);
                Vec3 p = zone.Points[i];
                sb.Append($"<rect x=\"{F(ox + (p.X - min.X) * scale - cell / 2)}\" y=\"{F(oy + (max.Y - p.Y) * scale - cell / 2)}\" " +
                          $"width=\"{F(cell)}\" height=\"{F(cell)}\" fill=\"#{r:x2}{g:x2}{b:x2}\" fill-opacity=\"0.6\"><title>{LightingReport.FormatValue(v, data.Units)} {LightingReport.UnitLabel(data.Units)}</title></rect>");
            }

            foreach (List<Vec3> loop in zone.Loops)
                sb.Append($"<polygon fill=\"none\" stroke=\"#1d2228\" stroke-width=\"1.5\" points=\"{string.Join(" ", loop.Select(p => X(p.X) + "," + Y(p.Y)))}\"/>");

            // Stair landings and flights, dashed.
            foreach (ZonePart part in zone.Parts)
                sb.Append($"<polygon fill=\"none\" stroke=\"#1d2228\" stroke-width=\"0.9\" stroke-dasharray=\"4 2\" points=\"{string.Join(" ", part.Outline.Select(p => X(p.X) + "," + Y(p.Y)))}\"/>");

            // Isolux lines with a white halo.
            foreach (IsoluxLine line in lines)
            {
                double width = line.IsTarget ? 2.6 : 1.1;
                foreach (List<Vec3> polyline in line.Polylines)
                {
                    string d = "M" + string.Join("L", polyline.Select(p => X(p.X) + "," + Y(p.Y)));
                    sb.Append($"<path d=\"{d}\" fill=\"none\" stroke=\"#fff\" stroke-width=\"{F(width + 2)}\" stroke-linejoin=\"round\" stroke-opacity=\"0.85\"/>");
                    sb.Append($"<path d=\"{d}\" fill=\"none\" stroke=\"#1d2228\" stroke-width=\"{F(width)}\" stroke-linejoin=\"round\"/>");
                }
            }

            var placer = new LabelPlacer(ViewWidth, ViewHeight - scaleBarRoom);
            foreach (Vec3 l in data.LuminairePositions)
            {
                if (!zone.ContainsPlan(l.X, l.Y) || l.Z < zone.BottomZ - 1 || l.Z > zone.TopZ + 3) continue;
                double lx = ox + (l.X - min.X) * scale, ly = oy + (max.Y - l.Y) * scale;
                sb.Append($"<rect x=\"{F(lx - 3.5)}\" y=\"{F(ly - 3.5)}\" width=\"7\" height=\"7\" fill=\"#ffd84a\" stroke=\"#4a3b00\"/>");
                placer.Take(lx - 4.5, ly - 4.5, lx + 4.5, ly + 4.5);
            }

            // Labels last, none over another or over a luminaire; one that finds no room is left
            // off. Each stair part is labelled with its average where it is the surface seen.
            foreach ((string text, List<Vec3> candidates) in PlanPartLabels(zone, data.Units))
            {
                foreach (Vec3 c in candidates)
                {
                    double cx = ox + (c.X - min.X) * scale, cy = oy + (max.Y - c.Y) * scale;
                    if (placer.TryPlace(cx, cy, text.Length * 5.1 + 6, 12, out double tx, out double ty))
                    {
                        sb.Append($"<text x=\"{F(tx)}\" y=\"{F(ty + 3.2)}\" font-size=\"9\" font-weight=\"600\" text-anchor=\"middle\" fill=\"#1d2228\" " +
                                  $"stroke=\"#fff\" stroke-width=\"2.6\" paint-order=\"stroke\" font-style=\"italic\">{H(text)}</text>");
                        break;
                    }
                }
            }
            // Isolux values: tried along each line, longest run first, until one fits.
            foreach (IsoluxLine line in lines)
            {
                string label = LightingReport.Convert(line.Level, data.Units).ToString("0.#", CultureInfo.InvariantCulture) + (line.IsTarget ? " target" : string.Empty);
                bool placed = false;
                foreach (List<Vec3> run in line.Polylines.OrderByDescending(Length))
                {
                    foreach (double t in new[] { 0.5, 0.3, 0.7, 0.15, 0.85, 0.05, 0.95 })
                    {
                        Vec3 at = run[(int)Math.Round((run.Count - 1) * t)];
                        double cx = ox + (at.X - min.X) * scale, cy = oy + (max.Y - at.Y) * scale;
                        if (!placer.TryPlace(cx, cy, label.Length * 5.6 + 4, 12, out double tx, out double ty, nudge: false)) continue;
                        sb.Append($"<text x=\"{F(tx)}\" y=\"{F(ty + 3.5)}\" font-size=\"10\" font-weight=\"{(line.IsTarget ? 700 : 400)}\" " +
                                  $"text-anchor=\"middle\" fill=\"#1d2228\" stroke=\"#fff\" stroke-width=\"3\" paint-order=\"stroke\">{H(label)}</text>");
                        placed = true;
                        break;
                    }
                    if (placed) break;
                }
            }

            // Scale bar: a round length near a fifth of the width.
            double barFeet = RoundDown(w / 5);
            double barPixels = barFeet * scale;
            double by = ViewHeight - 4;
            sb.Append($"<path d=\"M{F(pad)},{F(by)}h{F(barPixels)}\" stroke=\"#1d2228\" stroke-width=\"2\"/>");
            sb.Append($"<text x=\"{F(pad + barPixels + 4)}\" y=\"{F(by + 3)}\" font-size=\"9.5\" fill=\"#333\">{barFeet:0.##} ft</text>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>
        /// A label for each stair part the plan shows: its average, at spots where its points
        /// are the ones seen (not under a flight or landing above on the same level), nearest
        /// the middle of those first. A part hidden under another isn't labelled here.
        /// </summary>
        private static List<(string Text, List<Vec3> Candidates)> PlanPartLabels(CalcZone zone, IlluminanceUnits units)
        {
            var labels = new List<(string, List<Vec3>)>();
            int count = Math.Min(zone.Points.Count, zone.Illuminance.Length);
            double s = zone.GridSpacing > 0 ? zone.GridSpacing : 1;
            var top = new Dictionary<(long, long), int>();
            for (int i = 0; i < count; i++)
            {
                var key = ((long)Math.Round(zone.Points[i].X / s), (long)Math.Round(zone.Points[i].Y / s));
                if (!top.TryGetValue(key, out int j) || zone.Points[i].Z >= zone.Points[j].Z) top[key] = i;
            }
            var shown = new HashSet<int>(top.Values);

            foreach (int part in Enumerable.Range(0, zone.Parts.Count).OrderBy(p => zone.Parts[p].MinZ))
            {
                List<int> all = Enumerable.Range(0, count)
                    .Where(i => i < zone.PointPart.Length && zone.PointPart[i] == part && !double.IsNaN(zone.Illuminance[i])).ToList();
                List<int> seen = all.Where(shown.Contains).ToList();
                if (seen.Count == 0 || seen.Count < 0.3 * all.Count) continue;
                double mx = seen.Average(i => zone.Points[i].X), my = seen.Average(i => zone.Points[i].Y);
                List<Vec3> candidates = seen
                    .OrderBy(i => (zone.Points[i].X - mx) * (zone.Points[i].X - mx) + (zone.Points[i].Y - my) * (zone.Points[i].Y - my))
                    .Take(8).Select(i => zone.Points[i]).ToList();
                labels.Add(($"{zone.Parts[part].Name} {V(all.Average(i => zone.Illuminance[i]), units)} {LightingReport.UnitLabel(units)}", candidates));
            }
            return labels;
        }

        /// <summary>Places labels so none covers another (or anything taken first, such as luminaire markers).</summary>
        private sealed class LabelPlacer
        {
            private readonly double _width, _height;
            private readonly List<(double X0, double Y0, double X1, double Y1)> _taken = new List<(double, double, double, double)>();

            public LabelPlacer(double width, double height)
            {
                _width = width;
                _height = height;
            }

            public void Take(double x0, double y0, double x1, double y1) => _taken.Add((x0, y0, x1, y1));

            /// <summary>
            /// Centres a <paramref name="w"/> × <paramref name="h"/> box on (x, y), or with
            /// <paramref name="nudge"/> a little above, below or beside it; false when no spot is free.
            /// </summary>
            public bool TryPlace(double x, double y, double w, double h, out double cx, out double cy, bool nudge = true)
            {
                (double, double)[] offsets = nudge
                    ? new[] { (0.0, 0.0), (0, -h), (0, h), (w * 0.6, 0), (-w * 0.6, 0), (0, -2 * h), (0, 2 * h) }
                    : new[] { (0.0, 0.0) };
                foreach ((double dx, double dy) in offsets)
                {
                    cx = x + dx;
                    cy = y + dy;
                    double x0 = cx - w / 2, y0 = cy - h / 2, x1 = cx + w / 2, y1 = cy + h / 2;
                    if (x0 < 0 || y0 < 0 || x1 > _width || y1 > _height) continue;
                    if (_taken.Any(b => x0 < b.X1 && x1 > b.X0 && y0 < b.Y1 && y1 > b.Y0)) continue;
                    _taken.Add((x0, y0, x1, y1));
                    return true;
                }
                cx = cy = 0;
                return false;
            }
        }

        /// <summary>The colour scale under the plan (the same ramp as the 3D view and the plan dots in Revit).</summary>
        private static string ScaleBar(double scaleMax, IlluminanceUnits units)
        {
            var stops = new StringBuilder();
            for (int k = 0; k <= 8; k++)
            {
                (byte r, byte g, byte b) = PointColors.Ramp(k / 8.0);
                stops.Append($",#{r:x2}{g:x2}{b:x2}");
            }
            // Round ticks along the bar, so a colour reads as a value.
            var ticks = new StringBuilder();
            List<double> values = Isolux.Levels(0, scaleMax, units, 0, 5);
            values.Insert(0, 0);
            values.Add(scaleMax);
            foreach (double value in values.Distinct())
            {
                double at = scaleMax > 0 ? value / scaleMax * 100 : 0;
                string text = LightingReport.Convert(value, units).ToString(units == IlluminanceUnits.Lux ? "0" : "0.#", CultureInfo.InvariantCulture);
                ticks.Append($"<b style=\"left:{F(at)}%\"></b><em style=\"left:{F(at)}%\">{text}</em>");
            }
            return $"<div class=\"scalebar\"><span class=\"ramp\" style=\"background:linear-gradient(90deg{stops})\">{ticks}</span>{LightingReport.UnitLabel(units)}" +
                   "<span style=\"margin-left:10px\"><svg width=\"9\" height=\"9\"><rect x=\"1\" y=\"1\" width=\"7\" height=\"7\" fill=\"#ffd84a\" stroke=\"#4a3b00\"/></svg> luminaire</span></div>";
        }

        private static string SurfaceTable(CalcZone zone, IlluminanceUnits units, bool interreflection)
        {
            string u = LightingReport.UnitLabel(units);
            var sb = new StringBuilder();
            sb.Append("<h3>Surfaces</h3><table><tr><th>Surface</th><th>Reflectance</th>" +
                      $"<th>Avg ({u})</th><th>Min</th><th>Max</th><th>Luminance (cd/m²)</th></tr>");
            ZoneStatistics s = zone.Statistics;
            sb.Append($"<tr><td>Work plane</td><td></td><td class=\"n\">{V(s.Average, units)}</td><td class=\"n\">{V(s.Minimum, units)}</td>" +
                      $"<td class=\"n\">{V(s.Maximum, units)}</td><td></td></tr>");
            if (interreflection)
            {
                ZoneStatistics d = zone.DirectStatistics;
                sb.Append($"<tr><td>Work plane, direct only</td><td></td><td class=\"n\">{V(d.Average, units)}</td><td class=\"n\">{V(d.Minimum, units)}</td>" +
                          $"<td class=\"n\">{V(d.Maximum, units)}</td><td></td></tr>");
            }
            foreach (SurfaceSummary surface in RoomStatistics.Surfaces(zone))
            {
                sb.Append($"<tr><td>{H(surface.Label)}</td><td class=\"n\">{Pct(surface.Reflectance)}</td><td class=\"n\">{V(surface.Illuminance, units)}</td>" +
                          $"<td class=\"n\">{V(surface.MinimumIlluminance, units)}</td><td class=\"n\">{V(surface.MaximumIlluminance, units)}</td>" +
                          $"<td class=\"n\">{surface.LuminanceCdM2:0.#}</td></tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        /// <summary>A step curve: the share of points at or above each level, with the target marked.</summary>
        private static string CoverageSvg(CalcZone zone, IlluminanceUnits units, double scaleMax, double targetFc)
        {
            const int width = 290, height = 150, left = 34, right = 10, top = 10, bottom = 26;
            double plotW = width - left - right, plotH = height - top - bottom;
            const int steps = 60;
            double[] curve = RoomStatistics.CoverageCurve(zone, scaleMax, steps);
            string Px(double fc) => F(left + plotW * IesPhotometry.Clamp(fc / scaleMax, 0, 1));
            string Py(double share) => F(top + plotH * (1 - share));

            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Coverage curve\">");
            // Recessive grid and axes
            foreach (double share in new[] { 0, 0.25, 0.5, 0.75, 1.0 })
            {
                sb.Append($"<path d=\"M{left},{Py(share)}H{F(left + plotW)}\" stroke=\"#e3e6ea\" stroke-width=\"1\"/>");
                sb.Append($"<text x=\"{left - 4}\" y=\"{F(top + plotH * (1 - share) + 3)}\" font-size=\"9\" text-anchor=\"end\" fill=\"#555\">{share * 100:0}%</text>");
            }
            List<double> ticks = Isolux.Levels(0, scaleMax, units, 0, 4);
            ticks.Insert(0, 0);
            ticks.Add(scaleMax);
            foreach (double t in ticks.Distinct())
                sb.Append($"<text x=\"{Px(t)}\" y=\"{height - bottom + 12}\" font-size=\"9\" text-anchor=\"middle\" fill=\"#555\">{LightingReport.Convert(t, units):0.#}</text>");
            sb.Append($"<text x=\"{F(left + plotW / 2)}\" y=\"{height - 2}\" font-size=\"9\" text-anchor=\"middle\" fill=\"#555\">{LightingReport.UnitLabel(units)}</text>");
            sb.Append($"<path d=\"M{left},{F(top + plotH)}H{F(left + plotW)}\" stroke=\"#8a929c\" stroke-width=\"1\"/>");

            // The curve, with a light fill under it
            var path = new StringBuilder($"M{Px(0)},{Py(curve[0])}");
            for (int k = 1; k <= steps; k++)
                path.Append($"H{Px(scaleMax * k / steps)}V{Py(curve[k])}");
            sb.Append($"<path d=\"{path}V{Py(0)}H{Px(0)}Z\" fill=\"#2c5d8f\" fill-opacity=\"0.12\"/>");
            sb.Append($"<path d=\"{path}\" fill=\"none\" stroke=\"#2c5d8f\" stroke-width=\"2\"/>");

            if (targetFc > 0 && targetFc <= scaleMax)
            {
                double share = RoomStatistics.Coverage(zone, targetFc);
                sb.Append($"<path d=\"M{Px(targetFc)},{top}V{F(top + plotH)}\" stroke=\"#1d2228\" stroke-width=\"1\" stroke-dasharray=\"3 2\"/>");
                sb.Append($"<circle cx=\"{Px(targetFc)}\" cy=\"{Py(share)}\" r=\"4\" fill=\"#2c5d8f\" stroke=\"#fff\" stroke-width=\"2\"/>");
                bool leftSide = targetFc / scaleMax > 0.6;
                double labelY = share > 0.85 ? top + plotH * (1 - share) + 14 : top + plotH * (1 - share) - 6;
                sb.Append($"<text x=\"{F(left + plotW * targetFc / scaleMax + (leftSide ? -6 : 6))}\" y=\"{F(labelY)}\" font-size=\"10\" " +
                          $"text-anchor=\"{(leftSide ? "end" : "start")}\" fill=\"#1d2228\" stroke=\"#fff\" stroke-width=\"3\" paint-order=\"stroke\">{share * 100:0}% ≥ target</text>");
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string LuminaireTable(CalcZone zone, LightingReportData data)
        {
            var sb = new StringBuilder("<h3>Luminaires in this room</h3>");
            var rows = data.LuminaireTypes
                .Where(t => zone.LuminaireTypeCounts.ContainsKey(t.TypeKey))
                .Select(t => (Type: t, Count: zone.LuminaireTypeCounts[t.TypeKey]))
                .ToList();
            if (rows.Count == 0)
            {
                sb.Append("<p class=\"note\">None: the room's light comes from luminaires outside it.</p>");
                return sb.ToString();
            }
            sb.Append("<table><tr><th class=\"lum\">Polar</th><th>Type</th><th>Photometry</th><th>Qty</th><th>Lumens</th><th>LLF</th><th>Watts each</th><th>Max cd</th></tr>");
            foreach ((LuminaireTypeSummary t, int count) in rows)
            {
                double maxCd = t.PolarLengthwise.Concat(t.PolarCrosswise).DefaultIfEmpty(0).Max();
                sb.Append($"<tr><td class=\"lum\">{PolarSvg(t)}</td><td>{H(t.TypeName)}</td>" +
                          $"<td{(t.Estimated ? " class=\"est\"" : string.Empty)}>{H(t.Source)}{(t.Photometry.Length > 0 ? " · " + H(t.Photometry) : string.Empty)}</td>" +
                          $"<td class=\"n\">{count}</td><td class=\"n\">{t.Lumens:0}</td><td class=\"n\">{t.LightLossFactor:0.00}</td>" +
                          $"<td class=\"n\">{t.Watts:0.#}</td><td class=\"n\">{maxCd:0}</td></tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        /// <summary>A 28 px polar curve: lengthwise solid, crosswise dashed, nadir down.</summary>
        private static string PolarSvg(LuminaireTypeSummary t)
        {
            const double size = 28, c = size / 2, radius = size / 2 - 2;
            double peak = t.PolarLengthwise.Concat(t.PolarCrosswise).DefaultIfEmpty(0).Max();
            var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 {size} {size}\">");
            sb.Append($"<circle cx=\"{c}\" cy=\"{c}\" r=\"{F(radius)}\" fill=\"none\" stroke=\"#d7dbe1\"/>");
            sb.Append($"<circle cx=\"{c}\" cy=\"{c}\" r=\"{F(radius / 2)}\" fill=\"none\" stroke=\"#e3e6ea\"/>");
            sb.Append($"<path d=\"M{c},2V{size - 2}M2,{c}H{size - 2}\" stroke=\"#e3e6ea\"/>");
            if (peak > 0)
            {
                foreach ((double[] curve, string dash) in new[] { (t.PolarLengthwise, string.Empty), (t.PolarCrosswise, " stroke-dasharray=\"2 1.5\"") })
                {
                    if (curve.Length < 2) continue;
                    var points = new List<string>();
                    for (int k = 0; k < curve.Length; k++)
                    {
                        double a = (-180 + k * LuminaireTypeSummary.PolarStep) * Math.PI / 180;
                        double r = radius * curve[k] / peak;
                        points.Add(F(c + Math.Sin(a) * r) + "," + F(c + Math.Cos(a) * r));
                    }
                    sb.Append($"<polyline points=\"{string.Join(" ", points)}\" fill=\"none\" stroke=\"#2c5d8f\" stroke-width=\"1.3\"{dash}/>");
                }
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static double Length(List<Vec3> polyline)
        {
            double length = 0;
            for (int i = 1; i < polyline.Count; i++) length += (polyline[i] - polyline[i - 1]).Length;
            return length;
        }

        private static double RoundDown(double value)
        {
            if (value <= 0) return 1;
            double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (double m in new[] { 5, 2.5, 2, 1 })
                if (m * power <= value) return m * power;
            return power;
        }

        private static double ToFootcandles(double value, IlluminanceUnits units) =>
            units == IlluminanceUnits.Lux ? value / LightingReport.LuxPerFootcandle : value;

        private static string V(double footcandles, IlluminanceUnits units) => LightingReport.FormatValue(footcandles, units);

        private static string Pct(double fraction) => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

        /// <summary>Feet and inches, e.g. 2'-6".</summary>
        private static string Feet(double feet)
        {
            int inches = (int)Math.Round(feet * 12);
            return $"{inches / 12}'-{inches % 12}\"";
        }

        private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        private static string H(string? text) => ReportHtml.Encode(text);
    }
}
