using System;
using System.Collections.Generic;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>How a room view is coloured.</summary>
    public enum RoomRenderMode
    {
        /// <summary>Illuminance on each surface on the plan dots' blue-to-red scale.</summary>
        FalseColour,

        /// <summary>Grey by luminance (reflectance × illuminance): roughly how bright the room looks.</summary>
        Shaded,
    }

    /// <summary>
    /// A cutaway 3D view of a room's solved surfaces, drawn by a small z-buffer rasteriser into a
    /// PNG for the report: seen from above the south-east corner, with the ceiling and the near
    /// walls left off (back faces are culled, so only surfaces facing the viewer are drawn).
    /// Colours are blended across shared corners of patches on the same plane.
    /// </summary>
    public static class RoomRender
    {
        /// <summary>Where the viewer stands relative to the room: south-east and above.</summary>
        public static readonly Vec3 ViewFrom = new Vec3(1.0, -1.6, 1.25).Normalized();

        private static readonly Vec3 LightFrom = new Vec3(-0.4, -0.7, 1.0).Normalized();

        private const int Supersample = 2;

        /// <summary>
        /// The view, cropped to the room: at most <paramref name="maxWidth"/> × <paramref name="maxHeight"/>
        /// report pixels, with <paramref name="resolution"/> image pixels per report pixel (lines and
        /// luminaire markers keep their size in report pixels).
        /// </summary>
        public static PngImage Render(CalcZone zone, IEnumerable<Vec3> luminaires, RoomRenderMode mode,
            double scaleMaxFc, int maxWidth, int maxHeight, double resolution = 1)
        {
            int boxWidth = Math.Max(1, (int)Math.Round(maxWidth * resolution));
            int boxHeight = Math.Max(1, (int)Math.Round(maxHeight * resolution));

            var visible = zone.Surfaces.Where(p => Vec3.Dot(p.Normal, ViewFrom) > 1e-3 && p.Area > 1e-9).ToList();
            if (visible.Count == 0)
            {
                var blank = new PngImage(boxWidth, boxHeight);
                blank.Fill(255, 255, 255);
                return blank;
            }

            // Patches are kept by their centre, so a big one on a neighbouring wall or slab can
            // reach well outside the room: draw only what lies over the footprint. The margin
            // keeps the room's own wall faces (on its boundary) but not the tops of those walls
            // or the slab edges beyond them.
            PlanMask mask = PlanMask.Of(zone, 0.2);

            Vec3 forward = -ViewFrom;
            Vec3 right = Vec3.Cross(forward, Vec3.UnitZ).Normalized();
            Vec3 up = Vec3.Cross(right, forward);

            // Per-vertex values: the area-weighted mean of the patches meeting there on the same plane.
            // Covered patches (under furniture) don't count, so the bits that peek out take their
            // neighbours' colour instead of reading as dark.
            Func<SurfacePatch, double> valueOf = mode == RoomRenderMode.Shaded ? p => p.Exitance : (Func<SurfacePatch, double>)(p => p.Illuminance);
            var sums = new Dictionary<VertexKey, (double Sum, double Weight)>();
            foreach (SurfacePatch p in visible)
            {
                if (p.Covered) continue;
                double a = p.Area, v = valueOf(p);
                foreach (Vec3 corner in new[] { p.A, p.B, p.C })
                {
                    var key = new VertexKey(corner, p.Normal);
                    sums.TryGetValue(key, out var acc);
                    sums[key] = (acc.Sum + v * a, acc.Weight + a);
                }
            }
            double VertexValue(SurfacePatch patch, Vec3 corner) =>
                sums.TryGetValue(new VertexKey(corner, patch.Normal), out var acc) && acc.Weight > 0
                    ? acc.Sum / acc.Weight
                    : valueOf(patch);

            // Clip the patches to the room's height. Long thin patches (wall ends, mullions, slab
            // edges) are kept whole by the solver and can reach far above the ceiling or below the
            // floor; left in, they read as stray sticks and shrink the room in the picture.
            double ceiling = double.IsNaN(zone.CeilingZ) ? zone.TopZ : zone.CeilingZ;
            double clipBottom = zone.BottomZ - 0.25, clipTop = ceiling + 0.25;
            var pieces = new List<(Vec3 A, Vec3 B, Vec3 C, double VA, double VB, double VC, Vec3 Normal)>();
            foreach (SurfacePatch p in visible)
            {
                var polygon = new List<(Vec3 P, double V)> { (p.A, VertexValue(p, p.A)), (p.B, VertexValue(p, p.B)), (p.C, VertexValue(p, p.C)) };
                polygon = ClipZ(polygon, clipBottom, keepAbove: true);
                polygon = ClipZ(polygon, clipTop, keepAbove: false);
                for (int i = 1; i + 1 < polygon.Count; i++)
                    pieces.Add((polygon[0].P, polygon[i].P, polygon[i + 1].P, polygon[0].V, polygon[i].V, polygon[i + 1].V, p.Normal));
            }

            // Fit the clipped surfaces and the floor and ceiling outlines into the picture with a
            // margin, and crop the picture to them.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            void Extend(Vec3 v)
            {
                double sx = Vec3.Dot(v, right), sy = Vec3.Dot(v, up);
                minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
                minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
            }
            foreach (var piece in pieces)
            {
                foreach (Vec3 v in new[] { piece.A, piece.B, piece.C })
                    if (mask.Contains(v.X, v.Y)) Extend(v);
            }
            foreach (List<Vec3> loop in zone.Loops)
                foreach (Vec3 v in loop)
                {
                    Extend(new Vec3(v.X, v.Y, zone.BottomZ));
                    Extend(new Vec3(v.X, v.Y, ceiling));
                }
            const double margin = 0.03;
            double spanX = Math.Max(1e-6, maxX - minX), spanY = Math.Max(1e-6, maxY - minY);
            double fit = Math.Min(boxWidth * (1 - 2 * margin) / spanX, boxHeight * (1 - 2 * margin) / spanY);
            int width = Math.Max(1, Math.Min(boxWidth, (int)Math.Ceiling(spanX * fit / (1 - 2 * margin))));
            int height = Math.Max(1, Math.Min(boxHeight, (int)Math.Ceiling(spanY * fit / (1 - 2 * margin))));
            var image = new PngImage(width, height);

            int w = width * Supersample, h = height * Supersample;
            double scale = fit * Supersample;
            double offsetX = (w - spanX * scale) / 2;
            double offsetY = (h - spanY * scale) / 2;
            (double X, double Y, double Depth) Project(Vec3 v) =>
                (offsetX + (Vec3.Dot(v, right) - minX) * scale,
                 offsetY + (maxY - Vec3.Dot(v, up)) * scale,
                 Vec3.Dot(v, forward));

            // Strokes and markers in supersamples, sized in report pixels.
            double reportPixel = resolution * Supersample;
            int lineWidth = Math.Max(2, (int)Math.Round(reportPixel));

            // Shaded: expose for the brighter surfaces (95th percentile of luminance).
            double exposure = 1;
            if (mode == RoomRenderMode.Shaded)
            {
                double[] sorted = visible.Where(p => !p.Covered).Select(p => p.Exitance).DefaultIfEmpty(0).OrderBy(v => v).ToArray();
                double reference = sorted[(int)(sorted.Length * 0.95)];
                exposure = reference > 1e-9 ? 1.0 / reference : 1;
            }

            var colour = new float[w * h * 3];
            var depth = new float[w * h];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;
            for (int i = 0; i < colour.Length; i++) colour[i] = 1f;

            (float R, float G, float B) Colour(double value, Vec3 normal)
            {
                if (mode == RoomRenderMode.Shaded)
                {
                    double g = 1 - Math.Exp(-1.8 * value * exposure);
                    return ((float)g, (float)(g * 0.98), (float)(g * 0.94));
                }
                (byte r, byte gg, byte b) = PointColors.Ramp(scaleMaxFc > 0 ? value / scaleMaxFc : 0);
                // A little shading so floor, walls and furniture read as separate planes.
                double shade = 0.78 + 0.22 * Math.Max(0, Vec3.Dot(normal, LightFrom));
                return ((float)(r / 255.0 * shade), (float)(gg / 255.0 * shade), (float)(b / 255.0 * shade));
            }

            foreach (var piece in pieces)
            {
                bool whole = mask.Contains(piece.A.X, piece.A.Y) && mask.Contains(piece.B.X, piece.B.Y) && mask.Contains(piece.C.X, piece.C.Y);
                FillTriangle(Project(piece.A), Project(piece.B), Project(piece.C),
                    Colour(piece.VA, piece.Normal), Colour(piece.VB, piece.Normal), Colour(piece.VC, piece.Normal), colour, depth, w, h,
                    whole ? null : mask, piece.A, piece.B, piece.C);
            }

            // Outline the footprint at the floor and the ceiling, so the cut-away box reads.
            foreach (List<Vec3> loop in zone.Loops)
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    Vec3 p0 = loop[i], p1 = loop[(i + 1) % loop.Count];
                    DrawLine(Project(new Vec3(p0.X, p0.Y, zone.BottomZ)), Project(new Vec3(p1.X, p1.Y, zone.BottomZ)), 0.25f, lineWidth, colour, depth, w, h, true);
                    DrawLine(Project(new Vec3(p0.X, p0.Y, ceiling)), Project(new Vec3(p1.X, p1.Y, ceiling)), 0.55f, lineWidth, colour, depth, w, h, false);
                }
            }

            // Luminaires in the room: small yellow squares on top, the size of the plan's.
            int half = Math.Max(3, (int)Math.Round(3.5 * reportPixel));
            int edge = Math.Max(1, (int)Math.Round(reportPixel));
            foreach (Vec3 l in luminaires)
            {
                if (!zone.ContainsPlan(l.X, l.Y) || l.Z < zone.BottomZ - 1 || l.Z > ceiling + 3) continue;
                var s = Project(l);
                for (int y = (int)s.Y - half; y <= (int)s.Y + half; y++)
                    for (int x = (int)s.X - half; x <= (int)s.X + half; x++)
                    {
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        bool border = Math.Abs(x - (int)s.X) > half - edge || Math.Abs(y - (int)s.Y) > half - edge;
                        int k = (y * w + x) * 3;
                        if (border) { colour[k] = 0.29f; colour[k + 1] = 0.23f; colour[k + 2] = 0f; }
                        else { colour[k] = 1f; colour[k + 1] = 0.85f; colour[k + 2] = 0.29f; }
                    }
            }

            // Average each supersample block into the picture.
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float r = 0, g = 0, b = 0;
                    for (int sy = 0; sy < Supersample; sy++)
                        for (int sx = 0; sx < Supersample; sx++)
                        {
                            int k = ((y * Supersample + sy) * w + x * Supersample + sx) * 3;
                            r += colour[k]; g += colour[k + 1]; b += colour[k + 2];
                        }
                    float n = Supersample * Supersample;
                    image.Set(x, y, ToByte(r / n), ToByte(g / n), ToByte(b / n));
                }
            }
            return image;
        }

        /// <summary>Fills a triangle; with a <paramref name="mask"/>, only where the surface lies over it in plan.</summary>
        private static void FillTriangle((double X, double Y, double Depth) a, (double X, double Y, double Depth) b, (double X, double Y, double Depth) c,
            (float R, float G, float B) ca, (float R, float G, float B) cb, (float R, float G, float B) cc,
            float[] colour, float[] depth, int w, int h, PlanMask? mask = null, Vec3 wa3 = default, Vec3 wb3 = default, Vec3 wc3 = default)
        {
            double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(area) < 1e-9) return;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
            int x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
            int y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            for (int y = y0; y <= y1; y++)
            {
                double py = y + 0.5;
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5;
                    // Barycentric weights; a small tolerance closes hairline gaps between patches.
                    double wa = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                    double wb = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                    double wc = 1 - wa - wb;
                    const double tolerance = -1e-3;
                    if (wa < tolerance || wb < tolerance || wc < tolerance) continue;
                    // The view is parallel, so the screen weights are the weights in the room too.
                    if (mask != null && !mask.Contains(wa * wa3.X + wb * wb3.X + wc * wc3.X, wa * wa3.Y + wb * wb3.Y + wc * wc3.Y)) continue;
                    float d = (float)(wa * a.Depth + wb * b.Depth + wc * c.Depth);
                    int i = y * w + x;
                    if (d >= depth[i]) continue;
                    depth[i] = d;
                    int k = i * 3;
                    colour[k] = (float)(wa * ca.R + wb * cb.R + wc * cc.R);
                    colour[k + 1] = (float)(wa * ca.G + wb * cb.G + wc * cc.G);
                    colour[k + 2] = (float)(wa * ca.B + wb * cb.B + wc * cc.B);
                }
            }
        }

        /// <summary>
        /// The part of a convex polygon above (or below) height <paramref name="z"/>, with values
        /// interpolated along the cut edges (Sutherland–Hodgman against one plane).
        /// </summary>
        private static List<(Vec3 P, double V)> ClipZ(List<(Vec3 P, double V)> polygon, double z, bool keepAbove)
        {
            bool Inside(Vec3 p) => keepAbove ? p.Z >= z : p.Z <= z;
            if (polygon.All(v => Inside(v.P))) return polygon;
            var output = new List<(Vec3 P, double V)>(polygon.Count + 1);
            for (int i = 0; i < polygon.Count; i++)
            {
                var current = polygon[i];
                var next = polygon[(i + 1) % polygon.Count];
                bool currentIn = Inside(current.P), nextIn = Inside(next.P);
                if (currentIn) output.Add(current);
                if (currentIn != nextIn)
                {
                    double t = (z - current.P.Z) / (next.P.Z - current.P.Z);
                    output.Add((current.P + (next.P - current.P) * t, current.V + (next.V - current.V) * t));
                }
            }
            return output;
        }

        /// <summary>A dark line <paramref name="thickness"/> supersamples wide; <paramref name="depthTest"/> hides it behind surfaces.</summary>
        private static void DrawLine((double X, double Y, double Depth) a, (double X, double Y, double Depth) b, float grey, int thickness,
            float[] colour, float[] depth, int w, int h, bool depthTest)
        {
            double length = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            int steps = Math.Max(1, (int)Math.Ceiling(length));
            int shift = thickness / 2;
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t;
                float d = (float)(a.Depth + (b.Depth - a.Depth) * t);
                for (int oy = 0; oy < thickness; oy++)
                    for (int ox = 0; ox < thickness; ox++)
                    {
                        int px = (int)x + ox - shift, py = (int)y + oy - shift;
                        if (px < 0 || py < 0 || px >= w || py >= h) continue;
                        int i = py * w + px;
                        if (depthTest && d > depth[i] + 0.3f) continue;
                        colour[i * 3] = grey; colour[i * 3 + 1] = grey; colour[i * 3 + 2] = grey;
                    }
            }
        }

        private static byte ToByte(float v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);

        /// <summary>The footprint in plan, widened by a margin for its walls, as a grid of cells for quick lookups.</summary>
        private sealed class PlanMask
        {
            private readonly double _minX, _minY, _cell;
            private readonly int _nx, _ny;
            private readonly bool[] _inside;

            private PlanMask(double minX, double minY, double cell, int nx, int ny, bool[] inside)
            {
                _minX = minX; _minY = minY; _cell = cell; _nx = nx; _ny = ny; _inside = inside;
            }

            public static PlanMask Of(ZoneFootprint zone, double margin)
            {
                (Vec3 min, Vec3 max) = zone.PlanBounds;
                double cell = Math.Max(0.25, Math.Max(max.X - min.X, max.Y - min.Y) / 250);
                double x0 = min.X - margin - cell, y0 = min.Y - margin - cell;
                int nx = (int)Math.Ceiling((max.X - min.X + 2 * margin) / cell) + 3;
                int ny = (int)Math.Ceiling((max.Y - min.Y + 2 * margin) / cell) + 3;
                var inside = new bool[nx * ny];
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        double x = x0 + (i + 0.5) * cell, y = y0 + (j + 0.5) * cell;
                        // A cell is in when its centre is, or near enough to the boundary that part of it is.
                        inside[j * nx + i] = zone.ContainsPlan(x, y) || Polygon.DistanceToBoundary(zone.Loops, x, y) <= margin + cell * 0.71;
                    }
                return new PlanMask(x0, y0, cell, nx, ny, inside);
            }

            public bool Contains(double x, double y)
            {
                int i = (int)Math.Floor((x - _minX) / _cell), j = (int)Math.Floor((y - _minY) / _cell);
                return i >= 0 && j >= 0 && i < _nx && j < _ny && _inside[j * _nx + i];
            }
        }

        /// <summary>A patch corner, matched to the others on the same plane (positions to 0.01 ft, normals to ~3°).</summary>
        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            private readonly long _x, _y, _z;
            private readonly int _nx, _ny, _nz;

            public VertexKey(Vec3 p, Vec3 n)
            {
                _x = (long)Math.Round(p.X * 100);
                _y = (long)Math.Round(p.Y * 100);
                _z = (long)Math.Round(p.Z * 100);
                _nx = (int)Math.Round(n.X * 20);
                _ny = (int)Math.Round(n.Y * 20);
                _nz = (int)Math.Round(n.Z * 20);
            }

            public bool Equals(VertexKey o) => _x == o._x && _y == o._y && _z == o._z && _nx == o._nx && _ny == o._ny && _nz == o._nz;

            public override bool Equals(object? obj) => obj is VertexKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    long hash = _x * 73856093 ^ _y * 19349663 ^ _z * 83492791;
                    return (int)hash ^ (int)(hash >> 32) ^ (_nx * 31 + _ny * 17 + _nz);
                }
            }
        }
    }
}
