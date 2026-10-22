using System;
using System.Collections.Generic;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>One isolux line: every place on the work plane at one illuminance.</summary>
    public sealed class IsoluxLine
    {
        /// <summary>The illuminance (fc).</summary>
        public double Level { get; set; }

        /// <summary>The line is the zone's target average.</summary>
        public bool IsTarget { get; set; }

        /// <summary>Joined polylines in plan (Z = the work plane).</summary>
        public List<List<Vec3>> Polylines { get; } = new List<List<Vec3>>();
    }

    /// <summary>
    /// Isolux contours over a zone's calculation grid (marching squares; cells with a point left
    /// out are skipped), with round contour levels in the report's units.
    /// </summary>
    public static class Isolux
    {
        private static readonly double[] NiceSteps = { 1, 2, 2.5, 5 };

        /// <summary>
        /// Round levels (fc) strictly between <paramref name="minFc"/> and <paramref name="maxFc"/>:
        /// a 1 / 2 / 2.5 / 5 × 10ⁿ step in the report's units giving at most <paramref name="most"/>
        /// lines, plus the target when it falls in the range.
        /// </summary>
        public static List<double> Levels(double minFc, double maxFc, IlluminanceUnits units, double targetFc = 0, int most = 6)
        {
            var levels = new List<double>();
            double min = LightingReport.Convert(minFc, units);
            double max = LightingReport.Convert(maxFc, units);
            if (!(max > min) || double.IsInfinity(max)) return levels;

            double step = 0;
            for (int power = -2; power <= 6 && step == 0; power++)
            {
                foreach (double nice in NiceSteps)
                {
                    double s = nice * Math.Pow(10, power);
                    if (Math.Floor(max / s) - Math.Floor(min / s) <= most) { step = s; break; }
                }
            }
            if (step == 0) return levels;

            for (double v = (Math.Floor(min / step) + 1) * step; v < max - step * 1e-6; v += step)
                levels.Add(ToFootcandles(Math.Round(v / step) * step, units));

            if (targetFc > minFc && targetFc < maxFc && !levels.Any(l => Math.Abs(l - targetFc) < targetFc * 0.02))
            {
                levels.Add(targetFc);
                levels.Sort();
            }
            return levels;
        }

        /// <summary>Traces each level over the zone's grid; <paramref name="targetFc"/> marks the target line.</summary>
        public static List<IsoluxLine> Trace(CalcZone zone, IReadOnlyList<double> levels, double targetFc = 0)
        {
            var lines = new List<IsoluxLine>();
            double s = zone.GridSpacing;
            int count = Math.Min(zone.Points.Count, zone.Illuminance.Length);
            if (s <= 0 || count == 0) return lines;

            // The grid is regular: recover each point's column and row.
            double minX = double.MaxValue, minY = double.MaxValue, z = zone.Points[0].Z;
            for (int i = 0; i < count; i++)
            {
                minX = Math.Min(minX, zone.Points[i].X);
                minY = Math.Min(minY, zone.Points[i].Y);
            }
            var grid = new Dictionary<(int, int), double>();
            for (int i = 0; i < count; i++)
            {
                double v = zone.Illuminance[i];
                if (double.IsNaN(v)) continue;
                int col = (int)Math.Round((zone.Points[i].X - minX) / s);
                int row = (int)Math.Round((zone.Points[i].Y - minY) / s);
                grid[(col, row)] = v;
            }

            foreach (double level in levels)
            {
                var segments = new List<(Vec3 A, Vec3 B)>();
                foreach (KeyValuePair<(int, int), double> corner in grid)
                {
                    (int c, int r) = corner.Key;
                    if (!grid.TryGetValue((c + 1, r), out double v1) ||
                        !grid.TryGetValue((c + 1, r + 1), out double v2) ||
                        !grid.TryGetValue((c, r + 1), out double v3)) continue;
                    double v0 = corner.Value;
                    Vec3 p0 = new Vec3(minX + c * s, minY + r * s, z);
                    Cell(p0, s, v0, v1, v2, v3, level, segments);
                }
                if (segments.Count == 0) continue;
                var line = new IsoluxLine { Level = level, IsTarget = targetFc > 0 && Math.Abs(level - targetFc) < targetFc * 0.02 };
                line.Polylines.AddRange(Join(segments));
                lines.Add(line);
            }
            return lines;
        }

        /// <summary>
        /// One marching-squares cell with corners v0 (p0), v1 (+x), v2 (+x+y), v3 (+y). Saddles
        /// are split by the cell's average.
        /// </summary>
        private static void Cell(Vec3 p0, double s, double v0, double v1, double v2, double v3, double level, List<(Vec3, Vec3)> segments)
        {
            int index = (v0 >= level ? 1 : 0) | (v1 >= level ? 2 : 0) | (v2 >= level ? 4 : 0) | (v3 >= level ? 8 : 0);
            if (index == 0 || index == 15) return;

            Vec3 c0 = p0, c1 = p0 + new Vec3(s, 0, 0), c2 = p0 + new Vec3(s, s, 0), c3 = p0 + new Vec3(0, s, 0);
            Vec3 Edge(Vec3 a, double va, Vec3 b, double vb)
            {
                double t = Math.Abs(vb - va) < 1e-12 ? 0.5 : (level - va) / (vb - va);
                return a + (b - a) * IesPhotometry.Clamp(t, 0, 1);
            }
            Vec3 bottom() => Edge(c0, v0, c1, v1);
            Vec3 right() => Edge(c1, v1, c2, v2);
            Vec3 top() => Edge(c3, v3, c2, v2);
            Vec3 left() => Edge(c0, v0, c3, v3);

            switch (index)
            {
                case 1: case 14: segments.Add((left(), bottom())); break;
                case 2: case 13: segments.Add((bottom(), right())); break;
                case 3: case 12: segments.Add((left(), right())); break;
                case 4: case 11: segments.Add((right(), top())); break;
                case 6: case 9: segments.Add((bottom(), top())); break;
                case 7: case 8: segments.Add((left(), top())); break;
                case 5:
                case 10:
                {
                    // Saddle: corners 0 and 2 against 1 and 3. If the middle sides with corner 0,
                    // the lines cut off corners 1 and 3; otherwise they cut off 0 and 2.
                    bool middleHigh = (v0 + v1 + v2 + v3) / 4 >= level;
                    bool zeroHigh = index == 5;
                    if (middleHigh == zeroHigh)
                    {
                        segments.Add((bottom(), right()));
                        segments.Add((top(), left()));
                    }
                    else
                    {
                        segments.Add((left(), bottom()));
                        segments.Add((right(), top()));
                    }
                    break;
                }
            }
        }

        /// <summary>Chains segments that share end points into polylines.</summary>
        private static List<List<Vec3>> Join(List<(Vec3 A, Vec3 B)> segments)
        {
            (long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));
            var byEnd = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                foreach (Vec3 p in new[] { segments[i].A, segments[i].B })
                {
                    if (!byEnd.TryGetValue(Key(p), out List<int>? list)) byEnd[Key(p)] = list = new List<int>();
                    list.Add(i);
                }
            }

            var used = new bool[segments.Count];
            var polylines = new List<List<Vec3>>();
            for (int start = 0; start < segments.Count; start++)
            {
                if (used[start]) continue;
                used[start] = true;
                var line = new LinkedList<Vec3>(new[] { segments[start].A, segments[start].B });
                // Grow from both ends until no unused segment continues it.
                for (int side = 0; side < 2; side++)
                {
                    while (true)
                    {
                        Vec3 end = side == 0 ? line.Last!.Value : line.First!.Value;
                        int next = -1;
                        foreach (int candidate in byEnd[Key(end)])
                        {
                            if (!used[candidate]) { next = candidate; break; }
                        }
                        if (next < 0) break;
                        used[next] = true;
                        Vec3 other = Key(segments[next].A) == Key(end) ? segments[next].B : segments[next].A;
                        if (side == 0) line.AddLast(other);
                        else line.AddFirst(other);
                    }
                }
                polylines.Add(line.ToList());
            }
            return polylines;
        }

        private static double ToFootcandles(double value, IlluminanceUnits units) =>
            units == IlluminanceUnits.Lux ? value / LightingReport.LuxPerFootcandle : value;
    }
}
