using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Round X-C1: a flat reflective surface found in the model (all the glass or mirror triangles lying in one plane,
    /// wherever they are): the plane (<c>dot(Normal, p) = Offset</c>; the normal's largest component positive, so the two
    /// faces of a pane share it), the area and the box of its triangles.
    /// </summary>
    /// <param name="Normal">Unit normal.</param>
    /// <param name="Offset">Distance of the plane from the origin along the normal (m).</param>
    /// <param name="Area">Total area of its triangles (m², both faces of a pane counted).</param>
    /// <param name="Bounds">The box of its triangles.</param>
    /// <param name="Slack">How far its triangles lie from the plane either way (m): a pane's thickness, a sliding door's
    /// second track; the shader matches within it and the mirrored view's cut sits beyond it.</param>
    public readonly record struct ReflectivePlane(Vector3 Normal, float Offset, float Area, Aabb Bounds, float Slack = 0f);

    /// <summary>
    /// Round X-C1: finds the model's flat reflective surfaces (glass and mirrors) for planar reflections: the triangles of
    /// the given materials grouped by plane (normals within about 1°, offsets within <see cref="MERGE_OFFSET"/>, so both
    /// faces of a pane and a curtain wall's coplanar panes become one), largest first. Allocation happens once per call;
    /// cheap enough to redo when the materials change.
    /// </summary>
    public static class ReflectivePlanes
    {
        /// <summary>Panes and mirrors smaller than this (m², faces counted) are left to the probes and SSR.</summary>
        public const float MIN_AREA = 0.3f;

        /// <summary>Planes further apart than this (m) along their normal stay separate (double glazing is merged).</summary>
        public const float MERGE_OFFSET = 0.06f;

        /// <summary>
        /// Parallel planes this close (m) merge when their boxes touch: a sliding door's panels on their two tracks,
        /// a pane's faces, a frame's set-back glass.
        /// </summary>
        public const float MERGE_SPAN = 0.16f;

        /// <summary>At most this many planes are kept (the largest).</summary>
        public const int MAX_PLANES = 256;

        private const float NORMAL_STEP = 0.02f, MERGE_COS = 0.9998f;

        private sealed class Cluster
        {
            public Vector3 NormalSum;
            public double OffsetSum, Area;
            public Aabb Bounds = Aabb.Empty;
            public double MinD = double.MaxValue, MaxD = double.MinValue;
        }

        /// <summary>
        /// Groups the triangles whose first vertex has a <paramref name="reflective"/> material into planes.
        /// </summary>
        /// <param name="vertices">The scene's vertices.</param>
        /// <param name="indices">Triangle list.</param>
        /// <param name="vertexMaterial">Material per vertex (<see cref="MaterialData.NONE"/> = none).</param>
        /// <param name="reflective">Per material: true for glass / mirrors.</param>
        public static ReflectivePlane[] Find(SceneVertex[] vertices, uint[] indices, ushort[] vertexMaterial, bool[] reflective)
        {
            if (vertices == null || indices == null || vertexMaterial == null || reflective == null
                || vertexMaterial.Length != vertices.Length || Array.IndexOf(reflective, true) < 0)
            {
                return Array.Empty<ReflectivePlane>();
            }

            // 1. Bucket triangles by quantised normal and offset
            var buckets = new Dictionary<(int, int, int, int), Cluster>();
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                uint i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) { continue; }
                ushort material = vertexMaterial[i0];
                if (material >= reflective.Length || !reflective[material]) { continue; }
                Vector3 a = vertices[i0].Position, b = vertices[i1].Position, c = vertices[i2].Position;
                Vector3 cross = Vector3.Cross(b - a, c - a);
                float twice = cross.Length();
                if (twice < 1e-6f) { continue; }
                Vector3 n = cross / twice;
                n = Canonical(n);
                float d = Vector3.Dot(n, a);
                var key = ((int)MathF.Round(n.X / NORMAL_STEP), (int)MathF.Round(n.Y / NORMAL_STEP), (int)MathF.Round(n.Z / NORMAL_STEP),
                    (int)MathF.Floor(d / MERGE_OFFSET));
                if (!buckets.TryGetValue(key, out Cluster cluster)) { buckets[key] = cluster = new Cluster(); }
                float area = twice * 0.5f;
                cluster.NormalSum += n * area;
                cluster.OffsetSum += d * area;
                cluster.Area += area;
                cluster.MinD = Math.Min(cluster.MinD, d);
                cluster.MaxD = Math.Max(cluster.MaxD, d);
                cluster.Bounds.Include(a);
                cluster.Bounds.Include(b);
                cluster.Bounds.Include(c);
            }

            // 2. Merge neighbouring buckets (a plane straddling a quantisation step), largest first
            List<Cluster> sorted = buckets.Values.OrderByDescending(k => k.Area).ToList();
            var merged = new List<(Vector3 N, double D, Cluster C)>();
            foreach (Cluster k in sorted)
            {
                Vector3 n = Vector3.Normalize(k.NormalSum);
                double d = k.OffsetSum / k.Area;
                int target = -1;
                for (int i = 0; i < merged.Count && i < 4 * MAX_PLANES; i++)
                {
                    if (Vector3.Dot(merged[i].N, n) < MERGE_COS) { continue; }
                    double gap = Math.Abs(merged[i].D - d);
                    if (gap <= MERGE_OFFSET || (gap <= MERGE_SPAN && Touch(merged[i].C.Bounds, k.Bounds))) { target = i; break; }
                }
                if (target < 0)
                {
                    merged.Add((n, d, k));
                    continue;
                }
                Cluster into = merged[target].C;
                into.NormalSum += k.NormalSum;
                into.OffsetSum += k.OffsetSum;
                into.Area += k.Area;
                into.Bounds.Include(k.Bounds);
                into.MinD = Math.Min(into.MinD, k.MinD);
                into.MaxD = Math.Max(into.MaxD, k.MaxD);
                Vector3 mn = Vector3.Normalize(into.NormalSum);
                merged[target] = (mn, into.OffsetSum / into.Area, into);
            }

            return merged
                .Where(m => m.C.Area >= MIN_AREA)
                .OrderByDescending(m => m.C.Area)
                .Take(MAX_PLANES)
                .Select(m => new ReflectivePlane(m.N, (float)m.D, (float)m.C.Area, m.C.Bounds,
                    (float)Math.Max(m.C.MaxD - m.D, m.D - m.C.MinD)))
                .ToArray();
        }

        /// <summary>True when two boxes overlap or come within 0.25 m of each other.</summary>
        private static bool Touch(in Aabb a, in Aabb b)
        {
            var grow = new Vector3(0.25f);
            return new Aabb(a.Min - grow, a.Max + grow).Overlaps(b);
        }

        /// <summary>The normal or its opposite, whichever has its largest component positive (both faces of a pane agree).</summary>
        public static Vector3 Canonical(Vector3 n)
        {
            Vector3 m = Vector3.Abs(n);
            float major = m.X >= m.Y && m.X >= m.Z ? n.X : m.Y >= m.Z ? n.Y : n.Z;
            return major < 0f ? -n : n;
        }
    }
}
