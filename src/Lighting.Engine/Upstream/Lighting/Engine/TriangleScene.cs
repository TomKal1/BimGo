using System;
using System.Collections.Generic;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>What a surface is, for picking its reflectance.</summary>
    public enum SurfaceClass
    {
        Ceiling,
        Wall,
        Floor,
        Furniture,
    }

    /// <summary>
    /// One input triangle. The normal is taken from the winding (A, B, C counter-clockwise seen
    /// from the front), which for Revit solids points out of the solid, into the room.
    /// </summary>
    public struct SceneTriangle
    {
        public Vec3 A;
        public Vec3 B;
        public Vec3 C;

        /// <summary>Diffuse reflectance 0–1, or a negative value to use the class default.</summary>
        public double Reflectance;

        public SurfaceClass Class;

        /// <summary>Caller's tag (element id) for diagnostics.</summary>
        public long Tag;

        /// <summary>Optional model sRGB colour (0–1), used as an appearance hue, independently of physical reflectance.</summary>
        public Vec3? Colour;

        public SceneTriangle(Vec3 a, Vec3 b, Vec3 c, SurfaceClass surfaceClass, double reflectance = -1, long tag = 0)
            : this(a, b, c, surfaceClass, reflectance, tag, null) { }

        public SceneTriangle(Vec3 a, Vec3 b, Vec3 c, SurfaceClass surfaceClass, double reflectance, long tag, Vec3? colour)
        {
            A = a;
            B = b;
            C = c;
            Class = surfaceClass;
            Reflectance = reflectance;
            Tag = tag;
            Colour = colour;
        }

        public Vec3 Normal => Vec3.Cross(B - A, C - A).Normalized();

        public double Area => 0.5 * Vec3.Cross(B - A, C - A).Length;

        public Vec3 Centroid => (A + B + C) / 3.0;
    }

    /// <summary>
    /// Triangles in a bounding volume hierarchy for closest-hit and any-hit ray queries. Built
    /// once per calculation zone; safe to query from many threads at once.
    /// </summary>
    public sealed class TriangleScene
    {
        private readonly Vec3[] _a;
        private readonly Vec3[] _e1;
        private readonly Vec3[] _e2;
        private readonly Vec3[] _normal;
        private readonly int[] _order;
        private Node[] _nodes = Array.Empty<Node>();
        private int _nodeCount;

        private struct Node
        {
            public Vec3 Min;
            public Vec3 Max;
            public int LeftOrFirst; // child index for inner nodes, first triangle for leaves
            public int Count;       // 0 for inner nodes
        }

        public TriangleScene(IReadOnlyList<SceneTriangle> triangles)
        {
            int n = triangles.Count;
            Triangles = triangles;
            _a = new Vec3[n];
            _e1 = new Vec3[n];
            _e2 = new Vec3[n];
            _normal = new Vec3[n];
            _order = new int[n];
            var centroids = new Vec3[n];
            var mins = new Vec3[n];
            var maxs = new Vec3[n];
            for (int i = 0; i < n; i++)
            {
                SceneTriangle t = triangles[i];
                _a[i] = t.A;
                _e1[i] = t.B - t.A;
                _e2[i] = t.C - t.A;
                _normal[i] = Vec3.Cross(_e1[i], _e2[i]).Normalized();
                _order[i] = i;
                mins[i] = Vec3.Min(t.A, Vec3.Min(t.B, t.C));
                maxs[i] = Vec3.Max(t.A, Vec3.Max(t.B, t.C));
                centroids[i] = (mins[i] + maxs[i]) * 0.5;
            }

            if (n > 0) Build(centroids, mins, maxs);
        }

        public IReadOnlyList<SceneTriangle> Triangles { get; }

        public int Count => _a.Length;

        public Vec3 NormalOf(int triangle) => _normal[triangle];

        private void Build(Vec3[] centroids, Vec3[] mins, Vec3[] maxs)
        {
            _nodes = new Node[Math.Max(1, 2 * _a.Length)];
            _nodeCount = 1;
            _nodes[0] = new Node { LeftOrFirst = 0, Count = _a.Length };
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int index = stack.Pop();
                ref Node node = ref _nodes[index];
                Vec3 min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
                Vec3 max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
                Vec3 cmin = min;
                Vec3 cmax = max;
                for (int i = node.LeftOrFirst; i < node.LeftOrFirst + node.Count; i++)
                {
                    int t = _order[i];
                    min = Vec3.Min(min, mins[t]);
                    max = Vec3.Max(max, maxs[t]);
                    cmin = Vec3.Min(cmin, centroids[t]);
                    cmax = Vec3.Max(cmax, centroids[t]);
                }
                node.Min = min;
                node.Max = max;
                if (node.Count <= 4) continue;

                Vec3 extent = cmax - cmin;
                int axis = extent.X > extent.Y ? (extent.X > extent.Z ? 0 : 2) : (extent.Y > extent.Z ? 1 : 2);
                if (extent[axis] < 1e-9) continue;

                int split = SplitBySah(node.LeftOrFirst, node.Count, axis, cmin[axis], extent[axis], centroids, mins, maxs);
                int leftCount = split - node.LeftOrFirst;
                if (leftCount == 0 || leftCount == node.Count)
                {
                    // SAH found nothing better: split in the middle of the range.
                    split = node.LeftOrFirst + node.Count / 2;
                    PartialSortByAxis(node.LeftOrFirst, node.Count, axis, centroids);
                    leftCount = split - node.LeftOrFirst;
                }

                int left = _nodeCount++;
                int right = _nodeCount++;
                _nodes[left] = new Node { LeftOrFirst = node.LeftOrFirst, Count = leftCount };
                _nodes[right] = new Node { LeftOrFirst = split, Count = node.Count - leftCount };
                node.LeftOrFirst = left;
                node.Count = 0;
                stack.Push(left);
                stack.Push(right);
            }
        }

        private int SplitBySah(int first, int count, int axis, double origin, double extent,
            Vec3[] centroids, Vec3[] mins, Vec3[] maxs)
        {
            const int bins = 12;
            var binCount = new int[bins];
            var binMin = new Vec3[bins];
            var binMax = new Vec3[bins];
            for (int b = 0; b < bins; b++)
            {
                binMin[b] = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
                binMax[b] = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            }

            double scale = bins / extent;
            for (int i = first; i < first + count; i++)
            {
                int t = _order[i];
                int b = Math.Min(bins - 1, (int)((centroids[t][axis] - origin) * scale));
                binCount[b]++;
                binMin[b] = Vec3.Min(binMin[b], mins[t]);
                binMax[b] = Vec3.Max(binMax[b], maxs[t]);
            }

            double bestCost = double.MaxValue;
            int bestSplit = -1;
            for (int s = 1; s < bins; s++)
            {
                int lc = 0, rc = 0;
                Vec3 lmin = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), lmax = -lmin;
                Vec3 rmin = lmin, rmax = -lmin;
                for (int b = 0; b < s; b++)
                {
                    if (binCount[b] == 0) continue;
                    lc += binCount[b];
                    lmin = Vec3.Min(lmin, binMin[b]);
                    lmax = Vec3.Max(lmax, binMax[b]);
                }
                for (int b = s; b < bins; b++)
                {
                    if (binCount[b] == 0) continue;
                    rc += binCount[b];
                    rmin = Vec3.Min(rmin, binMin[b]);
                    rmax = Vec3.Max(rmax, binMax[b]);
                }
                if (lc == 0 || rc == 0) continue;
                double cost = lc * HalfArea(lmin, lmax) + rc * HalfArea(rmin, rmax);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestSplit = s;
                }
            }

            if (bestSplit < 0) return first;

            int i0 = first;
            int j0 = first + count - 1;
            while (i0 <= j0)
            {
                int b = Math.Min(bins - 1, (int)((centroids[_order[i0]][axis] - origin) * scale));
                if (b < bestSplit)
                {
                    i0++;
                }
                else
                {
                    int tmp = _order[i0];
                    _order[i0] = _order[j0];
                    _order[j0] = tmp;
                    j0--;
                }
            }
            return i0;
        }

        private void PartialSortByAxis(int first, int count, int axis, Vec3[] centroids)
        {
            Array.Sort(_order, first, count, Comparer<int>.Create((a, b) => centroids[a][axis].CompareTo(centroids[b][axis])));
        }

        private static double HalfArea(Vec3 min, Vec3 max)
        {
            Vec3 e = max - min;
            return e.X * e.Y + e.Y * e.Z + e.Z * e.X;
        }

        /// <summary>
        /// The nearest triangle a ray hits within <paramref name="maxDistance"/>, or -1.
        /// <paramref name="frontFacing"/> tells whether the ray hit the triangle's front side.
        /// </summary>
        public int ClosestHit(Vec3 origin, Vec3 direction, double maxDistance, out double distance, out bool frontFacing)
        {
            distance = maxDistance;
            frontFacing = false;
            int hit = -1;
            if (_nodeCount == 0) return -1;

            Vec3 inv = new Vec3(1.0 / NonZero(direction.X), 1.0 / NonZero(direction.Y), 1.0 / NonZero(direction.Z));
            int[] nodes = Stack();
            int sp = 0;
            nodes[sp++] = 0;
            while (sp > 0)
            {
                int index = nodes[--sp];
                Node node = _nodes[index];
                if (!HitsBox(origin, inv, node.Min, node.Max, distance)) continue;
                if (node.Count > 0)
                {
                    for (int i = node.LeftOrFirst; i < node.LeftOrFirst + node.Count; i++)
                    {
                        int t = _order[i];
                        if (Intersect(t, origin, direction, distance, out double d))
                        {
                            distance = d;
                            hit = t;
                        }
                    }
                }
                else
                {
                    if (sp + 2 > nodes.Length) nodes = GrowStack();
                    nodes[sp++] = node.LeftOrFirst;
                    nodes[sp++] = node.LeftOrFirst + 1;
                }
            }

            if (hit >= 0) frontFacing = Vec3.Dot(direction, _normal[hit]) < 0;
            return hit;
        }

        /// <summary>True when anything lies on the segment from origin along direction up to maxDistance.</summary>
        public bool AnyHit(Vec3 origin, Vec3 direction, double maxDistance)
        {
            if (_nodeCount == 0) return false;
            Vec3 inv = new Vec3(1.0 / NonZero(direction.X), 1.0 / NonZero(direction.Y), 1.0 / NonZero(direction.Z));
            int[] nodes = Stack();
            int sp = 0;
            nodes[sp++] = 0;
            while (sp > 0)
            {
                Node node = _nodes[nodes[--sp]];
                if (!HitsBox(origin, inv, node.Min, node.Max, maxDistance)) continue;
                if (node.Count > 0)
                {
                    for (int i = node.LeftOrFirst; i < node.LeftOrFirst + node.Count; i++)
                    {
                        if (Intersect(_order[i], origin, direction, maxDistance, out _)) return true;
                    }
                }
                else
                {
                    if (sp + 2 > nodes.Length) nodes = GrowStack();
                    nodes[sp++] = node.LeftOrFirst;
                    nodes[sp++] = node.LeftOrFirst + 1;
                }
            }
            return false;
        }

        [ThreadStatic]
        private static int[]? t_stack;

        private static int[] Stack() => t_stack ??= new int[128];

        private static int[] GrowStack()
        {
            var grown = new int[t_stack!.Length * 2];
            Array.Copy(t_stack, grown, t_stack.Length);
            t_stack = grown;
            return grown;
        }

        private static double NonZero(double v) => Math.Abs(v) < 1e-12 ? (v < 0 ? -1e-12 : 1e-12) : v;

        private static bool HitsBox(Vec3 origin, Vec3 inv, Vec3 min, Vec3 max, double maxDistance)
        {
            double t1 = (min.X - origin.X) * inv.X;
            double t2 = (max.X - origin.X) * inv.X;
            double tmin = Math.Min(t1, t2);
            double tmax = Math.Max(t1, t2);
            t1 = (min.Y - origin.Y) * inv.Y;
            t2 = (max.Y - origin.Y) * inv.Y;
            tmin = Math.Max(tmin, Math.Min(t1, t2));
            tmax = Math.Min(tmax, Math.Max(t1, t2));
            t1 = (min.Z - origin.Z) * inv.Z;
            t2 = (max.Z - origin.Z) * inv.Z;
            tmin = Math.Max(tmin, Math.Min(t1, t2));
            tmax = Math.Min(tmax, Math.Max(t1, t2));
            return tmax >= Math.Max(tmin, 0.0) && tmin <= maxDistance;
        }

        /// <summary>Möller–Trumbore, two-sided.</summary>
        private bool Intersect(int t, Vec3 origin, Vec3 direction, double maxDistance, out double distance)
        {
            distance = 0;
            Vec3 e1 = _e1[t];
            Vec3 e2 = _e2[t];
            Vec3 p = Vec3.Cross(direction, e2);
            double det = Vec3.Dot(e1, p);
            if (Math.Abs(det) < 1e-14) return false;
            double invDet = 1.0 / det;
            Vec3 s = origin - _a[t];
            double u = Vec3.Dot(s, p) * invDet;
            if (u < 0.0 || u > 1.0) return false;
            Vec3 q = Vec3.Cross(s, e1);
            double v = Vec3.Dot(direction, q) * invDet;
            if (v < 0.0 || u + v > 1.0) return false;
            double d = Vec3.Dot(e2, q) * invDet;
            if (d <= 1e-6 || d >= maxDistance) return false;
            distance = d;
            return true;
        }
    }
}
