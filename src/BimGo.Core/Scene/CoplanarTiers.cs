using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Depth tie-break for overlapping coplanar faces (two finish floors with the same top, a finish over a slab). Faces of
    /// different elements lying in one plane, facing the same way and overlapping, z-fight: the GPU picks a different
    /// winner per pixel and per frame. Each element in such an overlap gets a small tier (0 = no change); the scene vertex
    /// shader pulls higher tiers a few depth steps towards the eye, so the same element always wins.
    /// <para>Larger elements take lower tiers (a slab sits behind the finish floors on it; a room's floor behind the
    /// patches laid over it). Elements that overlap nothing keep tier 0 and are drawn exactly as before.</para>
    /// </summary>
    public static class CoplanarTiers
    {
        /// <summary>Tiers 0 … MAX_TIER; an element whose neighbours use all of them takes the top one.</summary>
        public const int MAX_TIER = 7;

        /// <summary>Faces this close along their normal (m) count as one plane.</summary>
        public const float PLANE_TOLERANCE = 0.001f;

        /// <summary>Faces must overlap at least this much (m) both ways in the plane: edges that only touch don't fight.</summary>
        public const float MIN_OVERLAP = 0.01f;

        private const float NORMAL_STEP = 0.02f, OFFSET_STEP = 0.002f, MIN_TRIANGLE_AREA = 1e-4f;

        /// <summary>One element's triangles in one plane bucket.</summary>
        private sealed class Patch
        {
            public int Element;
            public Aabb Bounds = Aabb.Empty;
            public float MinD = float.MaxValue, MaxD = float.MinValue;
            public int Mark;
        }

        /// <summary>
        /// The tier of every element (index as <paramref name="elements"/>), or null when no faces overlap.
        /// </summary>
        /// <param name="vertices">The scene's vertices.</param>
        /// <param name="indices">Triangle list.</param>
        /// <param name="elements">Elements with their opaque and transparent index ranges.</param>
        public static byte[] Find(SceneVertex[] vertices, uint[] indices, ElementRecord[] elements)
        {
            if (vertices == null || indices == null || elements == null || elements.Length < 2) { return null; }

            // 1. Each element's triangles bucketed by normal (as it faces) and offset
            var buckets = new Dictionary<(int, int, int, int), Dictionary<int, Patch>>();
            var area = new float[elements.Length];
            for (int e = 0; e < elements.Length; e++)
            {
                ElementRecord record = elements[e];
                if (record == null) { continue; }
                Collect(record.OpaqueStart, record.OpaqueCount, e);
                Collect(record.TransparentStart, record.TransparentCount, e);
            }

            // 2. Overlapping patches of different elements in one plane (the next offset bucket too: a plane straddling a
            // step), swept along the in-plane axis
            var neighbours = new Dictionary<int, HashSet<int>>();
            int mark = 0;
            foreach (var pair in buckets)
            {
                mark++;
                var (nx, ny, nz, dq) = pair.Key;
                List<Patch> here = pair.Value.Values.ToList();
                foreach (Patch patch in here) { patch.Mark = mark; }
                buckets.TryGetValue((nx, ny, nz, dq + 1), out Dictionary<int, Patch> next);
                List<Patch> candidates = next == null ? here : here.Concat(next.Values).ToList();
                if (candidates.Count < 2) { continue; }

                int skip = AxisOf(new Vector3(nx, ny, nz));
                int sweep = skip == 0 ? 1 : 0, other = 3 - skip - sweep;
                candidates.Sort((a, b) => Get(a.Bounds.Min, sweep).CompareTo(Get(b.Bounds.Min, sweep)));
                for (int i = 0; i < candidates.Count; i++)
                {
                    Patch a = candidates[i];
                    for (int j = i + 1; j < candidates.Count; j++)
                    {
                        Patch b = candidates[j];
                        if (Get(b.Bounds.Min, sweep) > Get(a.Bounds.Max, sweep) - MIN_OVERLAP) { break; }
                        if (a.Element == b.Element) { continue; }
                        if (a.Mark != mark && b.Mark != mark) { continue; } // both in the next bucket: its own turn
                        if (a.MinD > b.MaxD + PLANE_TOLERANCE || b.MinD > a.MaxD + PLANE_TOLERANCE) { continue; }
                        if (Overlap(a.Bounds, b.Bounds, sweep) < MIN_OVERLAP || Overlap(a.Bounds, b.Bounds, other) < MIN_OVERLAP) { continue; }
                        Link(a.Element, b.Element);
                        Link(b.Element, a.Element);
                    }
                }
            }
            if (neighbours.Count == 0) { return null; }

            // 3. Greedy colouring, largest first: the lowest tier no neighbour has yet
            var tiers = new byte[elements.Length];
            var coloured = new bool[elements.Length];
            Span<bool> used = stackalloc bool[MAX_TIER + 1];
            foreach (int e in neighbours.Keys.OrderByDescending(k => area[k]).ThenBy(k => k))
            {
                used.Clear();
                foreach (int n in neighbours[e])
                {
                    if (coloured[n]) { used[tiers[n]] = true; }
                }
                int tier = 0;
                while (tier < MAX_TIER && used[tier]) { tier++; }
                tiers[e] = (byte)tier;
                coloured[e] = true;
            }
            return tiers;

            void Collect(int start, int count, int element)
            {
                if (count <= 0 || start < 0) { return; }
                int end = Math.Min(indices.Length, start + count) - 2;
                for (int t = start; t < end; t += 3)
                {
                    uint i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                    if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) { continue; }
                    Vector3 a = vertices[i0].Position, b = vertices[i1].Position, c = vertices[i2].Position;
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float twice = cross.Length();
                    if (twice < 2f * MIN_TRIANGLE_AREA || !float.IsFinite(twice)) { continue; }
                    Vector3 n = cross / twice;
                    float d = Vector3.Dot(n, a);
                    var key = ((int)MathF.Round(n.X / NORMAL_STEP), (int)MathF.Round(n.Y / NORMAL_STEP), (int)MathF.Round(n.Z / NORMAL_STEP),
                        (int)MathF.Floor(d / OFFSET_STEP));
                    if (!buckets.TryGetValue(key, out Dictionary<int, Patch> patches)) { buckets[key] = patches = new Dictionary<int, Patch>(); }
                    if (!patches.TryGetValue(element, out Patch patch)) { patches[element] = patch = new Patch { Element = element }; }
                    patch.Bounds.Include(a);
                    patch.Bounds.Include(b);
                    patch.Bounds.Include(c);
                    patch.MinD = MathF.Min(patch.MinD, d);
                    patch.MaxD = MathF.Max(patch.MaxD, d);
                    area[element] += twice * 0.5f;
                }
            }

            void Link(int from, int to)
            {
                if (!neighbours.TryGetValue(from, out HashSet<int> set)) { neighbours[from] = set = new HashSet<int>(); }
                set.Add(to);
            }
        }

        /// <summary>The axis (0 x, 1 y, 2 z) the normal leans along most: the one the plane's boxes are thin in.</summary>
        private static int AxisOf(Vector3 n)
        {
            Vector3 m = Vector3.Abs(n);
            return m.X >= m.Y && m.X >= m.Z ? 0 : m.Y >= m.Z ? 1 : 2;
        }

        private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static float Overlap(in Aabb a, in Aabb b, int axis) =>
            MathF.Min(Get(a.Max, axis), Get(b.Max, axis)) - MathF.Max(Get(a.Min, axis), Get(b.Min, axis));
    }
}
