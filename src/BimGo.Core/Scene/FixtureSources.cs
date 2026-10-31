using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Which model's fixtures light the scene (the viewer and the calculations alike). A linked architectural model often
    /// carries copies of the electrical model's fixtures: both export a light at the same spot and that spot shines twice
    /// as bright (Snowdon Towers: 445 of the link's 446 lights sit on a host fixture of the same type).
    /// <list type="bullet">
    /// <item><see cref="AUTO"/> (the default): every host fixture; a linked fixture only where no host light of the same
    /// family type is within <see cref="SAME_SPOT"/> (fixtures only the link has still light).</item>
    /// <item><see cref="HOST"/>: the host model's fixtures only. <see cref="LINKS"/>: the linked models' only.
    /// <see cref="ALL"/>: every fixture, copies included (as before).</item>
    /// </list>
    /// Glowing surfaces are geometry and stay whichever is chosen.
    /// </summary>
    public static class FixtureSources
    {
        public const string AUTO = "auto", HOST = "host", LINKS = "links", ALL = "all";

        /// <summary>The choices in the order the panel shows them.</summary>
        public static readonly string[] MODES = { AUTO, HOST, LINKS, ALL };

        /// <summary>How close (m) a linked light must be to a host light of the same type to count as its copy.</summary>
        public const float SAME_SPOT = 0.05f;

        /// <summary>A stored mode, cleaned (unknown or empty → <see cref="AUTO"/>).</summary>
        public static string Clean(string mode) => Array.IndexOf(MODES, mode) >= 0 ? mode : AUTO;

        /// <summary>
        /// Per light (index into <paramref name="scene"/>'s lights): true when it lights the scene under
        /// <paramref name="mode"/>. Lights without a valid element always do.
        /// </summary>
        public static bool[] Kept(SceneData scene, string mode, out int copies)
        {
            mode = Clean(mode);
            LightSource[] lights = scene.Lighting?.Lights ?? Array.Empty<LightSource>();
            ElementRecord[] elements = scene.Elements;
            var kept = new bool[lights.Length];
            copies = 0;
            bool Linked(int i) => lights[i].Element >= 0 && lights[i].Element < elements.Length && elements[lights[i].Element].IsLinked;

            if (mode != AUTO)
            {
                for (int i = 0; i < lights.Length; i++)
                {
                    bool linked = Linked(i);
                    kept[i] = mode == ALL || (mode == HOST ? !linked : linked) || lights[i].Element < 0 || lights[i].Element >= elements.Length;
                }
                return kept;
            }

            // Host lights by grid cell (cells of SAME_SPOT: a copy is in its cell or a neighbour)
            var host = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / SAME_SPOT), (int)MathF.Floor(p.Y / SAME_SPOT), (int)MathF.Floor(p.Z / SAME_SPOT));
            for (int i = 0; i < lights.Length; i++)
            {
                if (Linked(i)) { continue; }
                kept[i] = true;
                var cell = Cell(lights[i].Position);
                if (!host.TryGetValue(cell, out List<int> list)) { host[cell] = list = new List<int>(); }
                list.Add(i);
            }
            for (int i = 0; i < lights.Length; i++)
            {
                if (!Linked(i)) { continue; }
                kept[i] = !HasHostCopy(i);
                if (!kept[i]) { copies++; }
            }
            return kept;

            bool HasHostCopy(int i)
            {
                Vector3 p = lights[i].Position;
                string type = elements[lights[i].Element].FamilyType ?? string.Empty;
                var (cx, cy, cz) = Cell(p);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!host.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int> list)) { continue; }
                            foreach (int h in list)
                            {
                                if (Vector3.DistanceSquared(lights[h].Position, p) > SAME_SPOT * SAME_SPOT) { continue; }
                                string hostType = lights[h].Element >= 0 && lights[h].Element < elements.Length ? elements[lights[h].Element].FamilyType ?? string.Empty : string.Empty;
                                if (string.Equals(hostType, type, StringComparison.Ordinal)) { return true; }
                            }
                        }
                    }
                }
                return false;
            }
        }
    }
}
