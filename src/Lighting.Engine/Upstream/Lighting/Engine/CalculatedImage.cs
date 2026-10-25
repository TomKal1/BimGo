using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>Presentation settings only: rendering never changes physical geometry or calculated values.</summary>
    public sealed class LightingImageOptions
    {
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 800;
        /// <summary>Samples along each pixel edge: 1, 2 or 3. Samples are tone mapped, then averaged in display-linear light.</summary>
        public int Samples { get; set; } = 2;
        /// <summary>Exposure offset, applied after the automatic key or to the fixed reference.</summary>
        public double ExposureEv { get; set; }
        /// <summary>
        /// Key the exposure to this view's brightest visible surfaces (default), so every room fills the tonal range
        /// and its light distribution reads. Off: <see cref="ReferenceLuminance"/> is fixed, for comparing rooms.
        /// </summary>
        public bool AutoExposure { get; set; } = true;
        /// <summary>Fixed scene luminance mapped to middle brightness at EV 0 when <see cref="AutoExposure"/> is off.</summary>
        public double ReferenceLuminance { get; set; } = 100;
        /// <summary>Tint surfaces with the model's shading colour. Off (default): neutral grey, so the picture reads as light, not finishes.</summary>
        public bool ModelColours { get; set; }
        /// <summary>Floor and ceiling outlines of the footprint and luminaire markers, drawn over the picture.</summary>
        public bool Overlays { get; set; } = true;
        /// <summary>Luminaire positions to mark when <see cref="Overlays"/> is on, in the same feet/world frame as the patches.</summary>
        public IReadOnlyList<Vec3>? Luminaires { get; set; }
        public Vec3 ViewFrom { get; set; } = new Vec3(1, -1.6, 1.25);
        /// <summary>Optional interior camera, in the same feet/world frame as the solved patches.</summary>
        public Vec3? Eye { get; set; }
        public Vec3? Target { get; set; }
        public double FieldOfView { get; set; } = 60;

        /// <summary>The same four cutaway corners and automatic interior camera in either host's image window.</summary>
        public static LightingImageOptions ForView(CalcZone zone, int view, int width, int height, int samples, double exposureEv)
        {
            Vec3[] directions = { new Vec3(1, -1.6, 1.25), new Vec3(-1, -1.6, 1.25), new Vec3(-1, 1.6, 1.25), new Vec3(1, 1.6, 1.25) };
            var options = new LightingImageOptions { Width = width, Height = height, Samples = samples, ExposureEv = exposureEv, ViewFrom = directions[Math.Max(0, Math.Min(3, view))] };
            if (view == 4)
            {
                Vec3[] candidates = zone.Surfaces.Where(p => p.Normal.Z > .7 && !p.Covered && zone.ContainsPlan(p.Centroid.X, p.Centroid.Y)).Select(p => p.Centroid).ToArray();
                if (candidates.Length == 0) throw new InvalidOperationException("No solved floor is available for an interior camera. Use a cutaway view.");
                double floor = candidates.Min(p => p.Z);
                candidates = candidates.Where(p => p.Z <= floor + .5).ToArray();
                double centreX = candidates.Average(p => p.X), centreY = candidates.Average(p => p.Y);
                Vec3 near = candidates.OrderBy(p => p.Y).ThenBy(p => Math.Abs(p.X - centreX)).First();
                double ceiling = Finite(zone.CeilingZ) ? zone.CeilingZ : zone.TopZ;
                double eyeHeight = floor + Math.Min(5.25, Math.Max(.25, (ceiling - floor) * .55));
                options.Eye = new Vec3(near.X, near.Y, eyeHeight);
                Vec3 target = new Vec3(centreX, centreY, eyeHeight - .3);
                options.Target = (target - options.Eye.Value).LengthSquared > .01 ? target : options.Eye.Value + Vec3.UnitY;
                options.FieldOfView = 75;
            }
            return options;
        }

        public void Validate()
        {
            if (Width < 16 || Height < 16 || Width > 7680 || Height > 7680 || (long)Width * Height > 17000000 || Samples < 1 || Samples > 3)
                throw new ArgumentOutOfRangeException(nameof(Width), "Image size must be 16–7680 px, at most 17 megapixels, with 1–3 samples per edge.");
            if (!Finite(ExposureEv) || ExposureEv < -8 || ExposureEv > 8 || !Finite(ReferenceLuminance) || ReferenceLuminance <= 0 || ReferenceLuminance > 100000)
                throw new ArgumentOutOfRangeException(nameof(ExposureEv));
            if (!Valid(ViewFrom) || ViewFrom.LengthSquared < 1e-12 || Eye.HasValue != Target.HasValue ||
                (Eye.HasValue && (!Valid(Eye.Value) || !Valid(Target!.Value) || (Eye.Value - Target.Value).LengthSquared < 1e-12)) ||
                !Finite(FieldOfView) || FieldOfView < 10 || FieldOfView > 120)
                throw new ArgumentException("The image camera is invalid.");
        }
        internal static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        internal static bool Valid(Vec3 p) => Finite(p.X) && Finite(p.Y) && Finite(p.Z);
    }

    /// <summary>
    /// Dependency-free calculated diffuse appearance. Camera rays see the saved solution, never artistic preview lights.
    /// Linear luminance is rho E / pi. Surfaces are neutral unless model colour is asked for, which supplies hue only:
    /// scalar transport does not model coloured bounce, glossy reflection, glass or spectral effects.
    /// Both hosts ship this exact implementation independently.
    /// </summary>
    public static class CalculatedImage
    {
        public const string Model = "calculated-diffuse-image-v2";
        public const string Basis = "Calculated diffuse appearance: saved surface illuminance and reflectance. " +
            "No daylight, coloured interreflection, glossy reflection, glazing or spectral colour simulation. Image detail is limited by the solved surface mesh.";

        /// <summary>
        /// Automatic exposure's display curve, as the report's shaded cutaway: 1 − exp(−1.8 L / key) on screen, so the key
        /// (95th-percentile) luminance shows at about 0.83 and darker surfaces keep their contrast instead of being lifted.
        /// </summary>
        private const double AutoCurve = 1.8;

        /// <summary>
        /// Lowest automatic key (cd/m²), about 1.2 fc on a 50 % wall: a room with less light than that is shown dark,
        /// not stretched until a few hundredths of a footcandle look like a lit room.
        /// </summary>
        private const double MinimumKey = 2;

        /// <summary>PNG with portable calculation basis, exposure, camera and a fingerprint of the exact surface snapshot used.</summary>
        public static byte[] RenderPng(CalcZone zone, LightingImageOptions options, CancellationToken cancel = default)
        {
            PngImage image = Render(zone, options, cancel, out double reference);
            string V(Vec3 v) => v.X.ToString("R", CultureInfo.InvariantCulture) + "," + v.Y.ToString("R", CultureInfo.InvariantCulture) + "," + v.Z.ToString("R", CultureInfo.InvariantCulture);
            var metadata = new Dictionary<string, string>
            {
                ["LightingModel"] = Model, ["LightingBasis"] = Basis, ["Zone"] = zone.Key + " / " + zone.DisplayName,
                ["Exposure"] = options.AutoExposure ? "Automatic: 95th-percentile visible luminance" : "Fixed",
                ["ExposureEV"] = options.ExposureEv.ToString("R", CultureInfo.InvariantCulture),
                ["ReferenceLuminanceCdM2"] = reference.ToString("R", CultureInfo.InvariantCulture),
                ["Appearance"] = options.ModelColours ? "Model colour hue" : "Neutral",
                ["SamplesPerPixel"] = (options.Samples * options.Samples).ToString(CultureInfo.InvariantCulture),
                ["Camera"] = options.Eye.HasValue ? "Perspective feet; eye " + V(options.Eye.Value) + "; target " + V(options.Target!.Value) + "; FOV " + options.FieldOfView.ToString("R", CultureInfo.InvariantCulture) : "Cutaway orthographic; direction " + V(options.ViewFrom),
                ["SurfaceSnapshotSHA256"] = Fingerprint(zone)
            };
            return image.ToPng(metadata);
        }

        /// <summary>Identity of rendered geometry/values, including appearance. No current clock or machine state enters the hash.</summary>
        public static string Fingerprint(CalcZone zone)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    void V(Vec3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
                    writer.Write(Model); writer.Write(zone.Key); writer.Write(zone.BottomZ); writer.Write(zone.TopZ); writer.Write(zone.CeilingZ);
                    writer.Write(zone.Loops.Count); foreach (var loop in zone.Loops) { writer.Write(loop.Count); foreach (var v in loop) V(v); }
                    writer.Write(zone.Surfaces.Count);
                    foreach (var p in zone.Surfaces) { V(p.A); V(p.B); V(p.C); V(p.Normal); writer.Write(p.SourceTag); writer.Write(p.Reflectance); writer.Write(p.Illuminance); writer.Write(p.Covered); writer.Write(p.Colour.HasValue); if (p.Colour.HasValue) V(p.Colour.Value); }
                }
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>A portable embedded figure, shared by the independently installed report presenters.</summary>
        public static string ReportFigure(CalcZone zone, IReadOnlyList<Vec3>? luminaires = null)
        {
            var image = Render(zone, new LightingImageOptions { Width = 1280, Height = 800, Samples = 2, Luminaires = luminaires }, default, out double reference);
            string caption = System.Net.WebUtility.HtmlEncode(zone.DisplayName + ". " + Basis + " Neutral surfaces. Exposure keyed to this view's brightest surfaces (key " +
                reference.ToString("0.#", CultureInfo.InvariantCulture) + " cd/m²): compare brightness within this image, not between rooms.");
            return "<figure style=\"margin:12px 0;break-inside:avoid\"><img style=\"display:block;width:100%;max-width:100%;height:auto\" alt=\"Calculated diffuse lighting image\" src=\"" +
                image.ToDataUri() + "\"><figcaption style=\"font-size:11px\">" + caption + "</figcaption></figure>";
        }

        /// <summary>Unprocessed linear RGB whose photopic luminance equals the solved luminance, before exposure/tone mapping.</summary>
        public static Vec3 LinearColour(double luminance, Vec3? srgb)
        {
            if (!LightingImageOptions.Finite(luminance) || luminance < 0) throw new ArgumentOutOfRangeException(nameof(luminance));
            Vec3 hue = srgb ?? new Vec3(1, 1, 1);
            if (!LightingImageOptions.Valid(hue) || hue.X < 0 || hue.Y < 0 || hue.Z < 0 || hue.X > 1 || hue.Y > 1 || hue.Z > 1)
                throw new ArgumentException("Appearance colour must be finite sRGB in 0–1.");
            hue = new Vec3(Linear(hue.X), Linear(hue.Y), Linear(hue.Z));
            double y = .2126 * hue.X + .7152 * hue.Y + .0722 * hue.Z;
            return y > 1e-10 ? hue * (luminance / y) : new Vec3(luminance, luminance, luminance);
        }

        /// <summary>Exposure, luminance-preserving Reinhard tone map followed by the sRGB transfer function.</summary>
        public static Vec3 DisplayColour(Vec3 linear, double exposureEv = 0, double referenceLuminance = 100) =>
            Encode(ToneMap(linear, exposureEv, referenceLuminance));

        /// <summary>
        /// The exposure reference at EV 0 (cd/m²): fixed, mapped to middle brightness; or under automatic exposure the key,
        /// the area-weighted 95th-percentile luminance of the uncovered surfaces this camera can face.
        /// </summary>
        public static double ExposureReference(CalcZone zone, LightingImageOptions options)
        {
            if (!options.AutoExposure) return options.ReferenceLuminance;
            Vec3 from = options.ViewFrom.Normalized();
            bool perspective = options.Eye.HasValue;
            var visible = zone.Surfaces.Where(p => !p.Covered && p.Area > 1e-9 && LightingImageOptions.Finite(p.Exitance) && p.Exitance >= 0 &&
                    (perspective || Vec3.Dot(p.Normal, from) > 1e-4) &&
                    (zone.ContainsPlan(p.Centroid.X, p.Centroid.Y) || Polygon.DistanceToBoundary(zone.Loops, p.Centroid.X, p.Centroid.Y) <= .25))
                .Select(p => (L: Luminance(p), A: p.Area)).OrderBy(s => s.L).ToArray();
            if (visible.Length == 0) return MinimumKey;
            double total = visible.Sum(s => s.A), running = 0, key = visible[visible.Length - 1].L;
            foreach (var s in visible)
            {
                running += s.A;
                if (running >= total * .95) { key = s.L; break; }
            }
            return Math.Max(key, MinimumKey);
        }

        /// <summary>Renders a cutaway or interior view from immutable saved patches. Cancellation throws, never returns a partial image.</summary>
        public static PngImage Render(CalcZone zone, LightingImageOptions? settings = null, CancellationToken cancel = default) =>
            Render(zone, settings ?? new LightingImageOptions(), cancel, out _);

        private static PngImage Render(CalcZone zone, LightingImageOptions o, CancellationToken cancel, out double reference)
        {
            if (zone == null) throw new ArgumentNullException(nameof(zone));
            o.Validate(); cancel.ThrowIfCancellationRequested();
            // Copy mutable settings up front. Automatic exposure is from the saved values only, never preview multipliers.
            int width = o.Width, height = o.Height, samples = o.Samples;
            double ev = o.ExposureEv;
            bool auto = o.AutoExposure, perspective = o.Eye.HasValue, colours = o.ModelColours, overlays = o.Overlays;
            Vec3[] luminaires = (o.Luminaires ?? Array.Empty<Vec3>()).Where(LightingImageOptions.Valid).ToArray();
            Vec3 from = o.ViewFrom.Normalized();
            Vec3 forward = perspective ? (o.Target!.Value - o.Eye!.Value).Normalized() : -from;
            Vec3 worldUp = Math.Abs(forward.Z) > .995 ? Vec3.UnitY : Vec3.UnitZ;
            Vec3 right = Vec3.Cross(forward, worldUp).Normalized(), up = Vec3.Cross(right, forward);
            double ceiling = LightingImageOptions.Finite(zone.CeilingZ) ? zone.CeilingZ : zone.TopZ;
            if (zone.Surfaces.Any(p => !LightingImageOptions.Finite(p.Illuminance) || p.Illuminance < 0 || !LightingImageOptions.Finite(p.Reflectance) ||
                p.Reflectance < 0 || p.Reflectance > 1 || !LightingImageOptions.Valid(p.A) || !LightingImageOptions.Valid(p.B) || !LightingImageOptions.Valid(p.C) || !LightingImageOptions.Valid(p.Normal)))
                throw new InvalidOperationException("Some surface results are invalid. Recalculate this room before exporting its image.");
            var patches = zone.Surfaces.Where(p => p.Area > 1e-9).ToArray();
            if (patches.Length == 0) throw new InvalidOperationException("This room has no solved surface coverage. Recalculate before exporting a lighting image.");
            reference = ExposureReference(zone, o);
            double exposureReference = reference;

            // Average physical luminance before tone mapping across corners on the same plane and finish. Positions and
            // normals are matched with a tolerance: subdivided and float32-cached patches never agree to the last bit.
            var sums = new Dictionary<Corner, (double Sum, double Weight)>();
            foreach (var p in patches)
            {
                if (p.Covered) continue;
                double luminance = Luminance(p);
                foreach (var v in new[] { p.A, p.B, p.C })
                {
                    var key = new Corner(p, v, colours);
                    sums.TryGetValue(key, out var total);
                    sums[key] = (total.Sum + luminance * p.Area, total.Weight + p.Area);
                }
            }
            double Value(SurfacePatch p, Vec3 v) => sums.TryGetValue(new Corner(p, v, colours), out var t) && t.Weight > 0 ? t.Sum / t.Weight : Luminance(p);

            // Clip display geometry only. The ceiling still contributed to the already completed physical solve.
            var pieces = new List<SceneTriangle>();
            var values = new List<Vec3[]>();
            foreach (var p in patches)
            {
                cancel.ThrowIfCancellationRequested();
                if (!perspective && Vec3.Dot(p.Normal, from) <= 1e-4) continue;
                var polygon = new List<Vertex> { new Vertex(p.A, Value(p, p.A)), new Vertex(p.B, Value(p, p.B)), new Vertex(p.C, Value(p, p.C)) };
                if (!perspective)
                {
                    polygon = Clip(polygon, zone.BottomZ - .02, true);
                    polygon = Clip(polygon, ceiling + .02, false);
                }
                Vec3? hue = colours ? p.Colour : null;
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    pieces.Add(new SceneTriangle(polygon[0].P, polygon[k].P, polygon[k + 1].P, p.Class));
                    values.Add(new[] { LinearColour(polygon[0].L, hue), LinearColour(polygon[k].L, hue), LinearColour(polygon[k + 1].L, hue) });
                }
            }
            if (pieces.Count == 0) throw new InvalidOperationException("No calculated surfaces face this camera.");
            var scene = new TriangleScene(pieces);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, minDepth = double.MaxValue;
            foreach (var t in pieces)
                foreach (var v in new[] { t.A, t.B, t.C })
                {
                    double x = Vec3.Dot(v, right), y = Vec3.Dot(v, up);
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    minDepth = Math.Min(minDepth, Vec3.Dot(v, forward));
                }
            double unitsPerPixel = Math.Max((maxX - minX) / (width * .92), (maxY - minY) / (height * .92));
            unitsPerPixel = Math.Max(unitsPerPixel, 1e-6);
            double midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;
            double tangent = Math.Tan(o.FieldOfView * Math.PI / 360);
            Vec3 eye = o.Eye ?? default;
            // Background is a display colour; it joins the surfaces after tone mapping so it cannot bleed into edges.
            var background = new Vec3(Linear(.94), Linear(.95), Linear(.97));
            var display = new Vec3[width * height];
            var depth = new double[width * height];
            var parallel = new ParallelOptions { CancellationToken = cancel };
            Parallel.For(0, height, parallel, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    if ((x & 63) == 0) cancel.ThrowIfCancellationRequested();
                    Vec3 sum = default;
                    double nearest = double.PositiveInfinity;
                    for (int sy = 0; sy < samples; sy++)
                        for (int sx = 0; sx < samples; sx++)
                        {
                            double px = x + (sx + .5) / samples, py = y + (sy + .5) / samples;
                            Vec3 origin = perspective ? eye : right * (midX + (px - width / 2.0) * unitsPerPixel) +
                                up * (midY + (height / 2.0 - py) * unitsPerPixel) + forward * (minDepth - 1);
                            Vec3 direction = perspective ? (forward + right * ((2 * px / width - 1) * tangent * width / height) + up * ((1 - 2 * py / height) * tangent)).Normalized() : forward;
                            Vec3 colour = background;
                            // Surfaces outside the footprint (slab edges, wall tops, neighbouring rooms) are looked
                            // through, not painted as background, so the room behind them stays solid.
                            Vec3 ray = origin;
                            for (int step = 0; step < 32; step++)
                            {
                                int hit = scene.ClosestHit(ray, direction, double.MaxValue, out double distance, out _);
                                if (hit < 0) break;
                                Vec3 point = ray + direction * distance;
                                // Boundary faces lie exactly on the footprint. Floating-point ray hits can land a few
                                // ulps outside; accepting the boundary avoids stippled holes in otherwise solid walls.
                                if (zone.ContainsPlan(point.X, point.Y) || Polygon.DistanceToBoundary(zone.Loops, point.X, point.Y) <= .001)
                                {
                                    var t = pieces[hit];
                                    Vec3 bary = Barycentric(point, t);
                                    var c = values[hit];
                                    Vec3 linear = c[0] * bary.X + c[1] * bary.Y + c[2] * bary.Z;
                                    colour = Fit(auto ? KeyCurve(linear, ev, exposureReference) : ToneMap(linear, ev, exposureReference));
                                    nearest = Math.Min(nearest, Vec3.Dot(perspective ? point - eye : point, forward));
                                    break;
                                }
                                ray = point + direction * 1e-4;
                            }
                            sum += colour;
                        }
                    display[y * width + x] = sum / (samples * samples);
                    depth[y * width + x] = nearest;
                }
            });

            if (overlays)
            {
                // Outlines and markers sized for the picture, like the report's plan dots.
                int thickness = Math.Max(2, (int)Math.Round(Math.Min(width, height) / 360.0));
                int half = Math.Max(3, (int)Math.Round(Math.Min(width, height) / 110.0));
                int edge = Math.Max(1, (int)Math.Round(half / 3.5));
                (double X, double Y, double Depth)? Project(Vec3 v)
                {
                    if (!perspective)
                        return ((Vec3.Dot(v, right) - midX) / unitsPerPixel + width / 2.0, height / 2.0 - (Vec3.Dot(v, up) - midY) / unitsPerPixel, Vec3.Dot(v, forward));
                    Vec3 d = v - eye;
                    double z = Vec3.Dot(d, forward);
                    if (z < .05) return null;
                    return ((Vec3.Dot(d, right) / (z * tangent * width / height) + 1) * width / 2.0, (1 - Vec3.Dot(d, up) / (z * tangent)) * height / 2.0, z);
                }
                void Line(Vec3 a, Vec3 b, double grey, bool depthTest)
                {
                    if (perspective)
                    {
                        // Keep only the part in front of the camera.
                        double za = Vec3.Dot(a - eye, forward), zb = Vec3.Dot(b - eye, forward);
                        if (za < .05 && zb < .05) return;
                        if (za < .05) a = a + (b - a) * ((.05 - za) / (zb - za));
                        else if (zb < .05) b = b + (a - b) * ((.05 - zb) / (za - zb));
                    }
                    var pa = Project(a); var pb = Project(b);
                    if (pa == null || pb == null) return;
                    var s = pa.Value; var e = pb.Value;
                    int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(e.X - s.X), Math.Abs(e.Y - s.Y))));
                    if (steps > 4 * (width + height)) return;
                    int shift = thickness / 2;
                    for (int i = 0; i <= steps; i++)
                    {
                        double t = (double)i / steps, lx = s.X + (e.X - s.X) * t, ly = s.Y + (e.Y - s.Y) * t, ld = s.Depth + (e.Depth - s.Depth) * t;
                        for (int oy = 0; oy < thickness; oy++)
                            for (int ox = 0; ox < thickness; ox++)
                            {
                                int qx = (int)Math.Floor(lx) + ox - shift, qy = (int)Math.Floor(ly) + oy - shift;
                                if (qx < 0 || qy < 0 || qx >= width || qy >= height) continue;
                                int at = qy * width + qx;
                                if (depthTest && ld > depth[at] + .3) continue;
                                display[at] = new Vec3(Linear(grey), Linear(grey), Linear(grey));
                            }
                    }
                }
                foreach (List<Vec3> loop in zone.Loops)
                    for (int i = 0; i < loop.Count; i++)
                    {
                        Vec3 p0 = loop[i], p1 = loop[(i + 1) % loop.Count];
                        Line(new Vec3(p0.X, p0.Y, zone.BottomZ), new Vec3(p1.X, p1.Y, zone.BottomZ), .25, true);
                        Line(new Vec3(p0.X, p0.Y, ceiling), new Vec3(p1.X, p1.Y, ceiling), .55, perspective);
                    }
                // Luminaires in the room: yellow squares with a dark edge, on top in a cutaway.
                Vec3 fill = new Vec3(Linear(1), Linear(.85), Linear(.29)), border = new Vec3(Linear(.29), Linear(.23), 0);
                foreach (Vec3 l in luminaires)
                {
                    if (!zone.ContainsPlan(l.X, l.Y) || !LightingReport.LuminaireAtZoneHeight(zone, l.Z, ceiling)) continue;
                    var projected = Project(l);
                    if (projected == null) continue;
                    var s = projected.Value;
                    int cx = (int)Math.Floor(s.X), cy = (int)Math.Floor(s.Y);
                    if (perspective && cx >= 0 && cy >= 0 && cx < width && cy < height && s.Depth > depth[cy * width + cx] + .5) continue;
                    for (int qy = cy - half; qy <= cy + half; qy++)
                        for (int qx = cx - half; qx <= cx + half; qx++)
                        {
                            if (qx < 0 || qy < 0 || qx >= width || qy >= height) continue;
                            bool rim = Math.Abs(qx - cx) > half - edge || Math.Abs(qy - cy) > half - edge;
                            display[qy * width + qx] = rim ? border : fill;
                        }
                }
            }

            var image = new PngImage(width, height);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Vec3 output = Encode(display[y * width + x]);
                    image.Set(x, y, Byte(output.X), Byte(output.Y), Byte(output.Z));
                }
            return image;
        }

        private static double Luminance(SurfacePatch p) => p.Exitance * LightingReport.LuxPerFootcandle / Math.PI;
        private static double Linear(double c) => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        private static Vec3 ToneMap(Vec3 linear, double exposureEv, double referenceLuminance)
        {
            double y = .2126 * linear.X + .7152 * linear.Y + .0722 * linear.Z;
            double e = Math.Pow(2, exposureEv) / referenceLuminance;
            return linear * (e / (1 + Math.Max(0, y) * e));
        }
        private static Vec3 KeyCurve(Vec3 linear, double exposureEv, double key)
        {
            double y = .2126 * linear.X + .7152 * linear.Y + .0722 * linear.Z;
            if (y <= 1e-12) return default;
            double shown = 1 - Math.Exp(-AutoCurve * y * Math.Pow(2, exposureEv) / key);
            // Display-linear, so encoding returns the curve's value; hue keeps its channel ratios.
            return linear * (Linear(shown) / y);
        }
        /// <summary>Brings an out-of-range saturated colour into the display gamut by moving it toward grey at the same luminance, not by clipping its hue.</summary>
        private static Vec3 Fit(Vec3 c)
        {
            double max = Math.Max(c.X, Math.Max(c.Y, c.Z));
            if (max <= 1) return c;
            double y = Math.Min(1, .2126 * c.X + .7152 * c.Y + .0722 * c.Z);
            double t = (max - 1) / Math.Max(1e-12, max - y);
            return c * (1 - t) + new Vec3(y, y, y) * t;
        }
        private static Vec3 Encode(Vec3 c)
        {
            double Srgb(double v) => Math.Max(0, Math.Min(1, v <= .0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - .055));
            return new Vec3(Srgb(c.X), Srgb(c.Y), Srgb(c.Z));
        }
        private static byte Byte(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
        private static Vec3 Barycentric(Vec3 p, SceneTriangle t)
        {
            Vec3 a = t.B - t.A, b = t.C - t.A, d = p - t.A;
            double aa = Vec3.Dot(a, a), ab = Vec3.Dot(a, b), bb = Vec3.Dot(b, b), da = Vec3.Dot(d, a), db = Vec3.Dot(d, b);
            double denominator = aa * bb - ab * ab;
            double v = (bb * da - ab * db) / denominator, w = (aa * db - ab * da) / denominator;
            return new Vec3(1 - v - w, v, w);
        }
        private readonly struct Vertex
        {
            public readonly Vec3 P;
            public readonly double L;
            public Vertex(Vec3 p, double l) { P = p; L = l; }
        }
        private static List<Vertex> Clip(List<Vertex> polygon, double z, bool above)
        {
            var result = new List<Vertex>();
            if (polygon.Count == 0) return result;
            Vertex previous = polygon[polygon.Count - 1];
            bool inside = above ? previous.P.Z >= z : previous.P.Z <= z;
            foreach (Vertex current in polygon)
            {
                bool next = above ? current.P.Z >= z : current.P.Z <= z;
                if (inside != next)
                {
                    double a = (z - previous.P.Z) / (current.P.Z - previous.P.Z);
                    result.Add(new Vertex(previous.P + (current.P - previous.P) * a, previous.L + (current.L - previous.L) * a));
                }
                if (next) result.Add(current);
                previous = current; inside = next;
            }
            return result;
        }
        // A patch corner, matched to others on the same plane (positions to 0.01 ft, normals to ~3°) and finish
        // (reflectance, and hue when shown), so smoothing stops at opposite faces and finish boundaries.
        private readonly struct Corner : IEquatable<Corner>
        {
            private readonly long _x, _y, _z;
            private readonly int _nx, _ny, _nz;
            private readonly double _reflectance;
            private readonly Vec3 _colour;
            public Corner(SurfacePatch p, Vec3 v, bool colours)
            {
                _x = (long)Math.Round(v.X * 100); _y = (long)Math.Round(v.Y * 100); _z = (long)Math.Round(v.Z * 100);
                _nx = (int)Math.Round(p.Normal.X * 20); _ny = (int)Math.Round(p.Normal.Y * 20); _nz = (int)Math.Round(p.Normal.Z * 20);
                _reflectance = Math.Round(p.Reflectance, 3);
                _colour = colours ? p.Colour ?? new Vec3(1, 1, 1) : default;
            }
            public bool Equals(Corner o) => _x == o._x && _y == o._y && _z == o._z && _nx == o._nx && _ny == o._ny && _nz == o._nz &&
                _reflectance == o._reflectance && _colour.Equals(o._colour);
            public override bool Equals(object? obj) => obj is Corner o && Equals(o);
            public override int GetHashCode()
            {
                unchecked
                {
                    long hash = _x * 73856093 ^ _y * 19349663 ^ _z * 83492791;
                    return (int)hash ^ (int)(hash >> 32) ^ (_nx * 31 + _ny * 17 + _nz) ^ _reflectance.GetHashCode() ^ _colour.GetHashCode();
                }
            }
        }
    }
}
