using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>What the lighting calc overlay shows (fork build LC-B).</summary>
    public enum CalcDisplayMode
    {
        /// <summary>Nothing.</summary>
        Off,

        /// <summary>The surfaces in false colour by illuminance, with the points, values and isolux lines.</summary>
        Illuminance,

        /// <summary>The surfaces shaded grey by luminance (ρE/π), with the points and values.</summary>
        Luminance,

        /// <summary>The calc points and their values only.</summary>
        Points
    }

    /// <summary>A zone's statistics over its calc points (footcandles; ratios NaN when the file left them out).</summary>
    public sealed class CalcStats
    {
        public int Count { get; init; }
        public float Average { get; init; }
        public float Maximum { get; init; }
        public float Minimum { get; init; }
        public float AverageToMinimum { get; init; } = float.NaN;
        public float MaximumToMinimum { get; init; } = float.NaN;

        /// <summary>Lighting power density (W/ft²).</summary>
        public float PowerDensity { get; init; }
    }

    /// <summary>A zone's design target (footcandles; 0 = not set) and whether it was met.</summary>
    public sealed class CalcTarget
    {
        public float Average { get; init; }
        public float AverageMax { get; init; }
        public float AverageToMinimum { get; init; }

        /// <summary>"Meets", "Misses" or null (not judged).</summary>
        public string Result { get; init; }
    }

    /// <summary>One radiosity patch on a zone's surfaces, in scene metres.</summary>
    public struct CalcPatch
    {
        public Vector3 A, B, C;

        /// <summary>Unit normal, into the room.</summary>
        public Vector3 Normal;

        /// <summary>Illuminance (fc).</summary>
        public float Illuminance;

        /// <summary>Diffuse reflectance 0–1.</summary>
        public float Reflectance;

        /// <summary>Bit 0 covered, bits 1–3 the surface class (Ceiling 0, Wall 1, Floor 2, Furniture 3).</summary>
        public int Flags;

        /// <summary>Something stands on the patch: it takes its neighbours' colour.</summary>
        public readonly bool Covered => (Flags & 1) != 0;

        /// <summary>Luminance (cd/m²): ρE/π with E in lux.</summary>
        public readonly float Luminance => Reflectance * Illuminance * LightingCalcs.LUX_PER_FC / MathF.PI;

        /// <summary>Area (m²).</summary>
        public readonly float Area => 0.5f * Vector3.Cross(B - A, C - A).Length();
    }

    /// <summary>One isolux line: its level (fc) and its segments as point pairs (scene metres).</summary>
    public sealed class CalcIsoline
    {
        public float Value { get; init; }

        /// <summary>The line is the zone's target average.</summary>
        public bool IsTarget { get; init; }

        /// <summary>Segment ends: 0–1, 2–3, …</summary>
        public Vector3[] Segments { get; init; } = Array.Empty<Vector3>();
    }

    /// <summary>A luminaire the calc used (scene metres).</summary>
    public sealed class CalcLuminaire
    {
        public long Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public Vector3 Position { get; init; }
        public Vector3 Nadir { get; init; }

        /// <summary>Light loss factor × any user multiplier.</summary>
        public float Multiplier { get; init; } = 1f;
    }

    /// <summary>One calc zone (a room or space) with its solved points and surfaces, in scene metres.</summary>
    public sealed class CalcZoneResult
    {
        public string Key { get; init; } = string.Empty;
        public string Number { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Level { get; init; } = string.Empty;

        /// <summary>Floor, top and ceiling heights (scene Z, m).</summary>
        public float BottomZ { get; init; }
        public float TopZ { get; init; }
        public float CeilingZ { get; init; }

        /// <summary>Points sit on stairs and floors rather than one work plane.</summary>
        public bool OnWalkingSurfaces { get; init; }

        public CalcStats Stats { get; init; } = new();

        /// <summary>Null when the zone has none.</summary>
        public CalcTarget Target { get; init; }

        /// <summary>Footprint loops in plan (scene m): the outer loop first.</summary>
        public Vector2[][] Loops { get; init; } = Array.Empty<Vector2[]>();

        public Vector3[] Points { get; init; } = Array.Empty<Vector3>();

        /// <summary>Total illuminance per point (fc; NaN = inside an obstruction, skipped).</summary>
        public float[] PointValues { get; init; } = Array.Empty<float>();

        public CalcPatch[] Patches { get; init; } = Array.Empty<CalcPatch>();

        public CalcIsoline[] Isolines { get; init; } = Array.Empty<CalcIsoline>();

        /// <summary>
        /// Per patch corner (3 per patch): illuminance (fc) and luminance (cd/m²) averaged with the patches meeting there on
        /// the same plane, area-weighted, covered patches left out (as WW Tools' room views do). Filled by
        /// <see cref="LightingCalcs.Prepare"/>.
        /// </summary>
        public float[] CornerIlluminance { get; set; } = Array.Empty<float>();

        /// <inheritdoc cref="CornerIlluminance"/>
        public float[] CornerLuminance { get; set; } = Array.Empty<float>();

        /// <summary>Plan bounds of the loops (scene m).</summary>
        public Vector2 PlanMin { get; set; }
        public Vector2 PlanMax { get; set; }

        /// <summary>"101 Open Office" (number and name, or whichever is set).</summary>
        public string DisplayName => Number.Length > 0 && Name.Length > 0 ? Number + " " + Name : Number.Length > 0 ? Number : Name.Length > 0 ? Name : "Zone";

        /// <summary>True when a scene point is over the footprint, between the floor (less 0.5 m) and the top (plus 0.5 m).</summary>
        public bool Contains(Vector3 p)
        {
            if (p.Z < BottomZ - 0.5f || p.Z > MathF.Max(TopZ, CeilingZ) + 0.5f) { return false; }
            if (p.X < PlanMin.X || p.Y < PlanMin.Y || p.X > PlanMax.X || p.Y > PlanMax.Y) { return false; }
            return LightingCalcs.InsideLoops(Loops, p.X, p.Y);
        }
    }

    /// <summary>A lighting calc results file, read (Core <c>Format/LightingCalcFiles</c>) and converted to scene metres.</summary>
    public sealed class LightingCalcResult
    {
        /// <summary>The calc set's id (8 hex characters).</summary>
        public string SetId { get; init; } = string.Empty;

        public string SetName { get; init; } = string.Empty;

        /// <summary>The tool that wrote it ("WW Tools 1.0.0.0").</summary>
        public string Tool { get; init; } = string.Empty;

        public string ModelTitle { get; init; } = string.Empty;

        /// <summary>The model's folder key ("local:…", "cloud:…").</summary>
        public string KeySource { get; init; } = string.Empty;

        /// <summary>When the calc ran (UTC; MinValue when unknown).</summary>
        public DateTime RunUtc { get; init; }

        public bool Interreflections { get; init; }

        public string Quality { get; init; } = string.Empty;

        /// <summary>The colour scale shared with the sheets (fc).</summary>
        public float ScaleMin { get; init; }
        public float ScaleMax { get; init; } = 1f;

        /// <summary>The unit the report prints ("fc" or "lx").</summary>
        public string ReportUnits { get; init; } = "fc";

        public CalcZoneResult[] Zones { get; init; } = Array.Empty<CalcZoneResult>();

        public CalcLuminaire[] Luminaires { get; init; } = Array.Empty<CalcLuminaire>();

        /// <summary>Zones dropped as damaged, with the reason (for the log and the panel).</summary>
        public List<string> Warnings { get; } = new();

        /// <summary>The file it came from (name only, e.g. "Level 1_d339c539.json").</summary>
        public string Source { get; init; } = string.Empty;

        /// <summary>The JSON file's write time (UTC) when read from disk.</summary>
        public DateTime FileTimeUtc { get; init; }

        /// <summary>95th percentile of the patches' luminance (cd/m²): the exposure of the luminance view. Set by <see cref="LightingCalcs.Prepare"/>.</summary>
        public float LuminanceReference { get; set; } = 1f;

        public int PointCount { get; set; }
        public int PatchCount { get; set; }
    }

    /// <summary>
    /// Fork build LC-B: the maths of the lighting calc overlay. Feet → scene metres, the colour scales (WW Tools' blue → red
    /// ramp and its luminance grey, so BimGo matches the report and sheets), per-corner values averaged at shared corners,
    /// and which zone a point is in. No GL, no UI.
    /// </summary>
    public static class LightingCalcs
    {
        /// <summary>Metres per foot.</summary>
        public const float FEET = 0.3048f;

        /// <summary>Lux per footcandle.</summary>
        public const float LUX_PER_FC = 10.763910f;

        /// <summary>Candelas per square metre per foot-lambert (NIST SP 811, Appendix B.9).</summary>
        public const float CD_PER_FOOTLAMBERT = 3.4262591f;

        /// <summary>Patches past the room's floor and ceiling are clipped this far beyond them (0.25 ft, as WW Tools' report).</summary>
        public const float CLIP_MARGIN = 0.25f * FEET;

        // WW Tools' ramp brightness (HSV value) and the direction its room views shade false colour from
        private const float RAMP_VALUE = 0.9f;
        private static readonly Vector3 SHADE_FROM = Vector3.Normalize(new Vector3(-0.4f, -0.7f, 1.0f));

        /// <summary>A position in Revit internal feet to scene metres (world = local + origin).</summary>
        public static Vector3 ToScene(float x, float y, float z, Vector3 origin) => new Vector3(x * FEET, y * FEET, z * FEET) - origin;

        /// <summary>
        /// WW Tools' false colour (<c>PointColors.Ramp</c>): an HSV hue sweep from 240° (blue) at <paramref name="min"/> to 0°
        /// (red) at <paramref name="max"/>, value 0.9. NaN and values past the ends clamp.
        /// </summary>
        public static Vector3 FalseColour(float value, float min, float max)
        {
            float t = max > min && !float.IsNaN(value) ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
            float hue = 4f * (1f - t); // sextant 0–4
            int sector = Math.Min(3, (int)MathF.Floor(hue));
            float f = hue - sector, v = RAMP_VALUE;
            return sector switch
            {
                0 => new Vector3(v, v * f, 0f),          // red → yellow
                1 => new Vector3(v * (1f - f), v, 0f),   // yellow → green
                2 => new Vector3(0f, v, v * f),          // green → cyan
                _ => new Vector3(0f, v * (1f - f), v)    // cyan → blue
            };
        }

        /// <summary>False colour shaded a little by the surface's facing (as the report's room views), so planes read apart.</summary>
        public static Vector3 ShadedFalseColour(float value, float min, float max, Vector3 normal) =>
            FalseColour(value, min, max) * (0.78f + 0.22f * MathF.Max(0f, Vector3.Dot(normal, SHADE_FROM)));

        /// <summary>
        /// WW Tools' luminance grey: 1 − e^(−1.8 L / reference), slightly warm, where the reference is the set's 95th percentile.
        /// </summary>
        public static Vector3 LuminanceGrey(float luminance, float reference)
        {
            float g = 1f - MathF.Exp(-1.8f * MathF.Max(0f, luminance) / MathF.Max(reference, 1e-6f));
            return new Vector3(g, g * 0.98f, g * 0.94f);
        }

        /// <summary>Footcandles in the display unit: lux when <paramref name="lux"/>.</summary>
        public static float Display(float footcandles, bool lux) => lux ? footcandles * LUX_PER_FC : footcandles;

        /// <summary>Surface luminance in the display unit: cd/m² for metric, foot-lamberts (fL) for imperial.</summary>
        public static float DisplayLuminance(float candelasPerSquareMetre, bool metric) =>
            metric ? candelasPerSquareMetre : candelasPerSquareMetre / CD_PER_FOOTLAMBERT;

        /// <summary>Fills each zone's corner values and plan bounds, and the set's luminance reference. Call once after reading.</summary>
        public static void Prepare(LightingCalcResult result)
        {
            var luminances = new List<float>();
            int points = 0, patches = 0;
            foreach (CalcZoneResult zone in result.Zones)
            {
                Bounds(zone);
                Corners(zone);
                foreach (CalcPatch p in zone.Patches)
                {
                    if (!p.Covered) { luminances.Add(p.Luminance); }
                }
                points += zone.Points.Length;
                patches += zone.Patches.Length;
            }
            result.PointCount = points;
            result.PatchCount = patches;
            if (luminances.Count > 0)
            {
                luminances.Sort();
                float reference = luminances[Math.Min(luminances.Count - 1, (int)(luminances.Count * 0.95f))];
                result.LuminanceReference = reference > 1e-6f ? reference : 1f;
            }
        }

        /// <summary>The zone a scene point is in (the smallest when zones overlap), or null.</summary>
        public static CalcZoneResult ZoneAt(LightingCalcResult result, Vector3 point)
        {
            if (result == null) { return null; }
            CalcZoneResult best = null;
            float bestArea = float.MaxValue;
            foreach (CalcZoneResult zone in result.Zones)
            {
                if (!zone.Contains(point)) { continue; }
                Vector2 size = zone.PlanMax - zone.PlanMin;
                float area = size.X * size.Y;
                if (area < bestArea)
                {
                    best = zone;
                    bestArea = area;
                }
            }
            return best;
        }

        /// <summary>Even-odd point in loops (plan).</summary>
        public static bool InsideLoops(Vector2[][] loops, float x, float y)
        {
            bool inside = false;
            foreach (Vector2[] loop in loops)
            {
                for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                {
                    Vector2 a = loop[i], b = loop[j];
                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) { inside = !inside; }
                }
            }
            return inside;
        }

        /// <summary>
        /// Clips a triangle to the slab bottom ≤ z ≤ top (Sutherland–Hodgman), carrying a value per corner. Writes up to
        /// 5 corners into the spans and returns how many (0: nothing left).
        /// </summary>
        public static int ClipZ(Vector3 a, Vector3 b, Vector3 c, float va, float vb, float vc, float bottom, float top,
            Span<Vector3> points, Span<float> values)
        {
            Span<Vector3> p1 = stackalloc Vector3[5];
            Span<float> v1 = stackalloc float[5];
            Span<Vector3> p0 = stackalloc Vector3[3] { a, b, c };
            Span<float> v0 = stackalloc float[3] { va, vb, vc };
            int n = ClipPlane(p0, v0, 3, bottom, keepAbove: true, p1, v1);
            n = ClipPlane(p1, v1, n, top, keepAbove: false, points, values);
            return n < 3 ? 0 : n;
        }

        private static int ClipPlane(ReadOnlySpan<Vector3> pin, ReadOnlySpan<float> vin, int count, float z, bool keepAbove,
            Span<Vector3> pout, Span<float> vout)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = pin[i], q = pin[(i + 1) % count];
                float vp = vin[i], vq = vin[(i + 1) % count];
                bool pIn = keepAbove ? p.Z >= z : p.Z <= z;
                bool qIn = keepAbove ? q.Z >= z : q.Z <= z;
                if (pIn && n < pout.Length)
                {
                    pout[n] = p;
                    vout[n++] = vp;
                }
                if (pIn != qIn && n < pout.Length)
                {
                    float t = (z - p.Z) / (q.Z - p.Z);
                    pout[n] = Vector3.Lerp(p, q, t);
                    vout[n++] = vp + (vq - vp) * t;
                }
            }
            return n;
        }

        private static void Bounds(CalcZoneResult zone)
        {
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (Vector2[] loop in zone.Loops)
            {
                foreach (Vector2 p in loop)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
            }
            if (min.X > max.X) { min = max = Vector2.Zero; }
            zone.PlanMin = min;
            zone.PlanMax = max;
        }

        // A patch corner, matched to the others on the same plane (positions to 0.01 ft, normals to ~3°, as WW Tools)
        private readonly record struct CornerKey(long X, long Y, long Z, int Nx, int Ny, int Nz)
        {
            private const float STEP = 0.01f * FEET;

            public static CornerKey Of(Vector3 p, Vector3 n) => new(
                (long)MathF.Round(p.X / STEP), (long)MathF.Round(p.Y / STEP), (long)MathF.Round(p.Z / STEP),
                (int)MathF.Round(n.X * 20f), (int)MathF.Round(n.Y * 20f), (int)MathF.Round(n.Z * 20f));
        }

        private static void Corners(CalcZoneResult zone)
        {
            CalcPatch[] patches = zone.Patches;
            var sums = new Dictionary<CornerKey, (double E, double L, double W)>(patches.Length * 2);
            foreach (CalcPatch p in patches)
            {
                if (p.Covered) { continue; }
                double area = p.Area, e = p.Illuminance * area, l = p.Luminance * area;
                Add(CornerKey.Of(p.A, p.Normal));
                Add(CornerKey.Of(p.B, p.Normal));
                Add(CornerKey.Of(p.C, p.Normal));

                void Add(CornerKey key)
                {
                    sums.TryGetValue(key, out (double E, double L, double W) acc);
                    sums[key] = (acc.E + e, acc.L + l, acc.W + area);
                }
            }

            var illuminance = new float[patches.Length * 3];
            var luminance = new float[patches.Length * 3];
            for (int i = 0; i < patches.Length; i++)
            {
                CalcPatch p = patches[i];
                Set(i * 3, p.A);
                Set(i * 3 + 1, p.B);
                Set(i * 3 + 2, p.C);

                void Set(int at, Vector3 corner)
                {
                    if (sums.TryGetValue(CornerKey.Of(corner, p.Normal), out (double E, double L, double W) acc) && acc.W > 0)
                    {
                        illuminance[at] = (float)(acc.E / acc.W);
                        luminance[at] = (float)(acc.L / acc.W);
                    }
                    else
                    {
                        illuminance[at] = p.Illuminance;
                        luminance[at] = p.Luminance;
                    }
                }
            }
            zone.CornerIlluminance = illuminance;
            zone.CornerLuminance = luminance;
        }
    }
}
