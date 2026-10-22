using System;
using System.Collections.Generic;
using System.Linq;

namespace WWTools.Calcs.Engine
{
    /// <summary>Plan polygon helpers (Z ignored).</summary>
    public static class Polygon
    {
        public static double SignedArea(IReadOnlyList<Vec3> loop)
        {
            double sum = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                Vec3 a = loop[i];
                Vec3 b = loop[(i + 1) % loop.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2;
        }

        public static bool Contains(IEnumerable<IReadOnlyList<Vec3>> loops, double x, double y)
        {
            bool inside = false;
            foreach (IReadOnlyList<Vec3> loop in loops)
            {
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    Vec3 a = loop[i];
                    Vec3 b = loop[j];
                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
            }
            return inside;
        }

        public static bool Contains(List<List<Vec3>> loops, double x, double y) =>
            Contains(loops.Cast<IReadOnlyList<Vec3>>(), x, y);

        public static double DistanceToBoundary(List<List<Vec3>> loops, double x, double y)
        {
            double best = double.MaxValue;
            foreach (List<Vec3> loop in loops)
            {
                for (int i = 0; i < loop.Count; i++)
                    best = Math.Min(best, DistanceToEdge(loop, i, x, y));
            }
            return best;
        }

        /// <summary>
        /// Distance from (x, y) along the plan direction (dx, dy) to the first boundary edge
        /// further than <paramref name="minDistance"/> away, or +∞ when the ray leaves without
        /// meeting one. The minimum skips the wall an outlet is mounted on.
        /// </summary>
        public static double RayDistance(List<List<Vec3>> loops, double x, double y, double dx, double dy, double minDistance = 0) =>
            RayHit(loops, x, y, dx, dy, minDistance).Distance;

        /// <summary>
        /// Like <see cref="RayDistance"/>, plus which edge was hit: edge i of a loop runs from
        /// point i to point i + 1. Loop and edge are -1 when nothing is hit.
        /// </summary>
        public static (double Distance, int Loop, int Edge) RayHit(List<List<Vec3>> loops, double x, double y, double dx, double dy, double minDistance = 0)
        {
            (double Distance, int Loop, int Edge) best = (double.PositiveInfinity, -1, -1);
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) return best;
            dx /= len;
            dy /= len;
            for (int k = 0; k < loops.Count; k++)
            {
                List<Vec3> loop = loops[k];
                for (int i = 0; i < loop.Count; i++)
                {
                    Vec3 a = loop[i];
                    Vec3 b = loop[(i + 1) % loop.Count];
                    double ex = b.X - a.X;
                    double ey = b.Y - a.Y;
                    double denom = dx * ey - dy * ex;
                    if (Math.Abs(denom) < 1e-12) continue; // parallel to the edge
                    double wx = a.X - x;
                    double wy = a.Y - y;
                    double t = (wx * ey - wy * ex) / denom;   // along the ray
                    double s = (wx * dy - wy * dx) / denom;   // along the edge
                    if (s < -1e-9 || s > 1 + 1e-9 || t <= minDistance) continue;
                    if (t < best.Distance) best = (t, k, i);
                }
            }
            return best;
        }

        /// <summary>Distance from (x, y) to one edge (edge i runs from point i to point i + 1).</summary>
        public static double DistanceToEdge(IReadOnlyList<Vec3> loop, int edge, double x, double y)
        {
            Vec3 a = loop[edge];
            Vec3 b = loop[(edge + 1) % loop.Count];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 1e-12 ? ((x - a.X) * dx + (y - a.Y) * dy) / len2 : 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double px = a.X + t * dx - x;
            double py = a.Y + t * dy - y;
            return Math.Sqrt(px * px + py * py);
        }
    }
}
