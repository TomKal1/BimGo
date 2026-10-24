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
        /// <summary>Samples along each pixel edge: 1, 2 or 3. Samples are accumulated in linear light.</summary>
        public int Samples { get; set; } = 2;
        public double ExposureEv { get; set; }
        /// <summary>Fixed scene luminance mapped to middle brightness at EV 0; never auto-exposed per room.</summary>
        public double ReferenceLuminance { get; set; } = 100;
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
    /// Linear luminance is rho E / pi. Model colour supplies hue only: scalar transport does not model coloured bounce,
    /// glossy reflection, glass or spectral effects. Both hosts ship this exact implementation independently.
    /// </summary>
    public static class CalculatedImage
    {
        public const string Model = "calculated-diffuse-image-v1";
        public const string Basis = "Calculated diffuse appearance: saved surface illuminance and reflectance; model colour is an appearance hue. " +
            "Fixed exposure; no daylight, coloured interreflection, glossy reflection, glazing or spectral colour simulation. Image detail is limited by the solved surface mesh.";

        /// <summary>PNG with portable calculation basis, exposure, camera and a fingerprint of the exact surface snapshot used.</summary>
        public static byte[] RenderPng(CalcZone zone, LightingImageOptions options, CancellationToken cancel = default)
        {
            PngImage image = Render(zone, options, cancel);
            string V(Vec3 v) => v.X.ToString("R", CultureInfo.InvariantCulture) + "," + v.Y.ToString("R", CultureInfo.InvariantCulture) + "," + v.Z.ToString("R", CultureInfo.InvariantCulture);
            var metadata = new Dictionary<string, string>
            {
                ["LightingModel"] = Model, ["LightingBasis"] = Basis, ["Zone"] = zone.Key + " / " + zone.DisplayName,
                ["ExposureEV"] = options.ExposureEv.ToString("R", CultureInfo.InvariantCulture),
                ["ReferenceLuminanceCdM2"] = options.ReferenceLuminance.ToString("R", CultureInfo.InvariantCulture),
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
        public static string ReportFigure(CalcZone zone)
        {
            var image = Render(zone, new LightingImageOptions { Width = 1280, Height = 800, Samples = 1 });
            string caption = System.Net.WebUtility.HtmlEncode(zone.DisplayName + ". " + Basis + " EV 0; reference 100 cd/m². " +
                (zone.Surfaces.Any(p => p.Colour.HasValue) ? "Model colours retained where available." : "No saved model colours: neutral appearance."));
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
            double Linear(double c) => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
            hue = new Vec3(Linear(hue.X), Linear(hue.Y), Linear(hue.Z));
            double y = .2126 * hue.X + .7152 * hue.Y + .0722 * hue.Z;
            return y > 1e-10 ? hue * (luminance / y) : new Vec3(luminance, luminance, luminance);
        }

        /// <summary>Fixed exposure, luminance-preserving Reinhard tone map followed by the sRGB transfer function.</summary>
        public static Vec3 DisplayColour(Vec3 linear, double exposureEv = 0, double referenceLuminance = 100)
        {
            double y = .2126 * linear.X + .7152 * linear.Y + .0722 * linear.Z;
            double e = Math.Pow(2, exposureEv) / referenceLuminance;
            Vec3 mapped = linear * (e / (1 + Math.Max(0, y) * e));
            double Srgb(double c) => Math.Max(0, Math.Min(1, c <= .0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - .055));
            return new Vec3(Srgb(mapped.X), Srgb(mapped.Y), Srgb(mapped.Z));
        }

        /// <summary>Renders a cutaway or interior view from immutable saved patches. Cancellation throws, never returns a partial image.</summary>
        public static PngImage Render(CalcZone zone, LightingImageOptions? settings = null, CancellationToken cancel = default)
        {
            if (zone == null) throw new ArgumentNullException(nameof(zone));
            var o = settings ?? new LightingImageOptions();
            o.Validate(); cancel.ThrowIfCancellationRequested();
            // Copy mutable settings up front. No per-room automatic exposure or preview brightness multipliers.
            int width = o.Width, height = o.Height, samples = o.Samples;
            double ev = o.ExposureEv, reference = o.ReferenceLuminance;
            bool perspective = o.Eye.HasValue;
            Vec3 from = o.ViewFrom.Normalized();
            Vec3 forward = perspective ? (o.Target!.Value - o.Eye!.Value).Normalized() : -from;
            Vec3 worldUp = Math.Abs(forward.Z) > .995 ? Vec3.UnitY : Vec3.UnitZ;
            Vec3 right = Vec3.Cross(forward, worldUp).Normalized(), up = Vec3.Cross(right, forward);
            double ceiling = LightingImageOptions.Finite(zone.CeilingZ) ? zone.CeilingZ : zone.TopZ;
            if (zone.Surfaces.Any(p => !LightingImageOptions.Finite(p.Illuminance) || p.Illuminance < 0 || !LightingImageOptions.Finite(p.Reflectance) ||
                p.Reflectance < 0 || p.Reflectance > 1 || !LightingImageOptions.Valid(p.A) || !LightingImageOptions.Valid(p.B) || !LightingImageOptions.Valid(p.C) || !LightingImageOptions.Valid(p.Normal)))
                throw new InvalidOperationException("Some surface results are invalid. Recalculate this room before exporting its image.");
            var patches = zone.Surfaces.Where(p => p.Area > 1e-9 && LightingImageOptions.Finite(p.Illuminance) && p.Illuminance >= 0 &&
                LightingImageOptions.Finite(p.Reflectance) && p.Reflectance >= 0 && p.Reflectance <= 1 &&
                LightingImageOptions.Valid(p.A) && LightingImageOptions.Valid(p.B) && LightingImageOptions.Valid(p.C) && LightingImageOptions.Valid(p.Normal)).ToArray();
            if (patches.Length == 0) throw new InvalidOperationException("This room has no solved surface coverage. Recalculate before exporting a lighting image.");

            // Average physical luminance before tone mapping, within a source/material/normal boundary only.
            var sums = new Dictionary<Corner, (double Sum, double Weight)>();
            foreach (var p in patches)
            {
                if (p.Covered) continue;
                double luminance = p.Exitance * LightingReport.LuxPerFootcandle / Math.PI;
                foreach (var v in new[] { p.A, p.B, p.C })
                {
                    var key = new Corner(p, v);
                    sums.TryGetValue(key, out var total);
                    sums[key] = (total.Sum + luminance * p.Area, total.Weight + p.Area);
                }
            }
            double Value(SurfacePatch p, Vec3 v) => sums.TryGetValue(new Corner(p, v), out var t) && t.Weight > 0
                ? t.Sum / t.Weight : p.Exitance * LightingReport.LuxPerFootcandle / Math.PI;

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
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    pieces.Add(new SceneTriangle(polygon[0].P, polygon[k].P, polygon[k + 1].P, p.Class));
                    values.Add(new[] { LinearColour(polygon[0].L, p.Colour), LinearColour(polygon[k].L, p.Colour), LinearColour(polygon[k + 1].L, p.Colour) });
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
            var image = new PngImage(width, height);
            var parallel = new ParallelOptions { CancellationToken = cancel };
            Parallel.For(0, height, parallel, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    if ((x & 63) == 0) cancel.ThrowIfCancellationRequested();
                    Vec3 sum = default;
                    for (int sy = 0; sy < samples; sy++)
                        for (int sx = 0; sx < samples; sx++)
                        {
                            double px = x + (sx + .5) / samples, py = y + (sy + .5) / samples;
                            Vec3 origin = perspective ? eye : right * (midX + (px - width / 2.0) * unitsPerPixel) +
                                up * (midY + (height / 2.0 - py) * unitsPerPixel) + forward * (minDepth - 1);
                            Vec3 direction = perspective ? (forward + right * ((2 * px / width - 1) * tangent * width / height) + up * ((1 - 2 * py / height) * tangent)).Normalized() : forward;
                            Vec3 colour = new Vec3(.94, .95, .97); // background only, never added to the solved lighting
                            int hit = scene.ClosestHit(origin, direction, double.MaxValue, out double distance, out _);
                            if (hit >= 0)
                            {
                                Vec3 point = origin + direction * distance;
                                // Boundary faces lie exactly on the footprint. Floating-point ray hits can land a few
                                // ulps outside; accepting the boundary avoids stippled holes in otherwise solid walls.
                                if (zone.ContainsPlan(point.X, point.Y) || Polygon.DistanceToBoundary(zone.Loops, point.X, point.Y) <= .001)
                                {
                                    var t = pieces[hit];
                                    Vec3 bary = Barycentric(point, t);
                                    var c = values[hit];
                                    colour = c[0] * bary.X + c[1] * bary.Y + c[2] * bary.Z;
                                    sum += colour;
                                    continue;
                                }
                            }
                            // Background is already a display colour; convert to the corresponding reference-linear value.
                            double Linear(double c) => Math.Pow((c + .055) / 1.055, 2.4);
                            double bgY = .2126 * Linear(colour.X) + .7152 * Linear(colour.Y) + .0722 * Linear(colour.Z);
                            sum += new Vec3(Linear(colour.X), Linear(colour.Y), Linear(colour.Z)) * (reference / Math.Pow(2, ev) / (1 - bgY));
                        }
                    Vec3 output = DisplayColour(sum / (samples * samples), ev, reference);
                    image.Set(x, y, Byte(output.X), Byte(output.Y), Byte(output.Z));
                }
            });
            return image;
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
        // Source + normal + material prevents smoothing through different objects, opposite faces or finish boundaries.
        private readonly struct Corner : IEquatable<Corner>
        {
            private readonly long _x, _y, _z, _tag;
            private readonly Vec3 _normal, _colour;
            private readonly double _reflectance;
            public Corner(SurfacePatch p, Vec3 v)
            {
                _x = (long)Math.Round(v.X * 100000); _y = (long)Math.Round(v.Y * 100000); _z = (long)Math.Round(v.Z * 100000);
                _tag = p.SourceTag; _normal = p.Normal; _colour = p.Colour ?? new Vec3(1, 1, 1); _reflectance = p.Reflectance;
            }
            public bool Equals(Corner o) => _x == o._x && _y == o._y && _z == o._z && _tag == o._tag && _normal.Equals(o._normal) && _colour.Equals(o._colour) && _reflectance == o._reflectance;
            public override bool Equals(object? obj) => obj is Corner o && Equals(o);
            public override int GetHashCode() => unchecked(_x.GetHashCode() * 397 ^ _y.GetHashCode() * 31 ^ _z.GetHashCode() ^ _tag.GetHashCode() ^ _colour.GetHashCode());
        }
    }
}
