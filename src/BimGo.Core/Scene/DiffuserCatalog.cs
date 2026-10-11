// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// One row of a diffuser catalog table, in SI: one neck / face size at one flow, with its throws. Throws are per
    /// side of a 4-way diffuser at the whole diffuser's flow, as catalogs print them.
    /// </summary>
    public sealed class CatalogRow
    {
        /// <summary>Round neck diameter (m).</summary>
        public float NeckDiameter { get; set; }

        /// <summary>Face size (m; square faces: width = length).</summary>
        public float FaceSize { get; set; }

        /// <summary>Flow of the whole diffuser (L/s).</summary>
        public float Flow { get; set; }

        /// <summary>Total pressure (Pa); 0 when the table doesn't give it.</summary>
        public float TotalPressure { get; set; }

        /// <summary>Noise criterion; 0 when the table doesn't give it.</summary>
        public float Nc { get; set; }

        /// <summary>Terminal speeds the throws are given at (m/s), same order as <see cref="Throws"/>.</summary>
        public float[] ThrowSpeeds { get; set; } = Array.Empty<float>();

        /// <summary>Throws (m) at <see cref="ThrowSpeeds"/>.</summary>
        public float[] Throws { get; set; } = Array.Empty<float>();

        /// <summary>
        /// Checks the row is usable: sizes, flow and at least one (speed, throw) pair in sensible ranges.
        /// </summary>
        /// <param name="reason">Why not, else null.</param>
        public bool IsValid(out string reason)
        {
            reason = null;
            if (!InRange(NeckDiameter, 0.05f, 1.0f)) { reason = "neck size out of range (50–1000 mm / 2–40 in)"; }
            else if (!InRange(FaceSize, 0.1f, 1.5f)) { reason = "face size out of range (100–1500 mm / 4–60 in)"; }
            else if (FaceSize < NeckDiameter) { reason = "face smaller than the neck"; }
            else if (!InRange(Flow, 1f, 10000f)) { reason = "flow out of range"; }
            else if (!float.IsFinite(TotalPressure) || TotalPressure < 0f || TotalPressure > 2000f) { reason = "pressure out of range"; }
            else if (!float.IsFinite(Nc) || Nc < 0f || Nc > 100f) { reason = "NC out of range"; }
            else if (ThrowSpeeds == null || Throws == null || ThrowSpeeds.Length != Throws.Length || Throws.Length == 0) { reason = "no throws"; }
            else
            {
                for (int i = 0; i < Throws.Length; i++)
                {
                    if (!InRange(ThrowSpeeds[i], 0.05f, 10f)) { reason = "throw speed out of range"; break; }
                    if (!InRange(Throws[i], 0.05f, 100f)) { reason = "throw out of range"; break; }
                }
            }
            return reason == null;
        }

        private static bool InRange(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
    }

    /// <summary>
    /// One diffuser product (or the built-in generic one): its rows and how BimGo recognises it.
    /// </summary>
    public sealed class CatalogFamily
    {
        /// <summary>Unique key (case-insensitive), e.g. "generic-3cone-square"; a family's "BimGo Catalog Key".</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>True for generated, typical-but-not-real data (every readout says so).</summary>
        public bool Illustrative { get; set; }

        /// <summary>Throw multiplier for exposed mounting (no ceiling for the jet to cling to).</summary>
        public float FreeJetFactor { get; set; } = 0.7f;

        /// <summary>The units the source table was written in (for display; rows are SI).</summary>
        public MeasurementSystem SourceUnits { get; set; } = MeasurementSystem.SI;

        /// <summary>Family / type name keywords (case-insensitive) for the fallback matcher.</summary>
        public List<string> Match { get; set; } = new();

        /// <summary>Where it came from: a file name, or null for built-in.</summary>
        public string Source { get; set; }

        /// <summary>The rows (SI).</summary>
        public List<CatalogRow> Rows { get; set; } = new();

        /// <summary>
        /// Drops invalid rows (each reason added to <paramref name="problems"/>), trims keywords and clamps the free-jet
        /// factor. Returns this family, or null when nothing usable is left.
        /// </summary>
        public CatalogFamily Clean(List<string> problems = null)
        {
            Key = Key?.Trim() ?? string.Empty;
            Label = string.IsNullOrWhiteSpace(Label) ? Key : Label.Trim();
            FreeJetFactor = float.IsFinite(FreeJetFactor) ? Math.Clamp(FreeJetFactor, 0.3f, 1f) : 0.7f;
            if (!Enum.IsDefined(SourceUnits)) { SourceUnits = MeasurementSystem.SI; }
            Match = (Match ?? new List<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            var kept = new List<CatalogRow>();
            int index = 0;
            foreach (CatalogRow row in Rows ?? new List<CatalogRow>())
            {
                index++;
                string reason = "empty";
                if (row != null && row.IsValid(out reason)) { kept.Add(row); }
                else { problems?.Add($"{Key}: row {index} skipped ({reason})"); }
            }
            Rows = kept;

            if (Key.Length == 0) { problems?.Add("A catalog family without a key was skipped."); return null; }
            if (Rows.Count == 0) { problems?.Add($"{Key}: no usable rows."); return null; }
            return this;
        }
    }

    /// <summary>
    /// A jet's speed against distance: <c>V(x) = A · x^-N</c> (m/s, m), capped at <see cref="MaxSpeed"/> (the neck
    /// velocity) near the diffuser, where the power law would run away.
    /// </summary>
    public readonly record struct DecayCurve(float A, float N, float MaxSpeed)
    {
        /// <summary>Speed (m/s) at a distance (m) along the throw.</summary>
        public float SpeedAt(float distance)
        {
            if (A <= 0f) { return 0f; }
            if (distance <= 0f) { return MaxSpeed > 0f ? MaxSpeed : float.PositiveInfinity; }
            float speed = A * MathF.Pow(distance, -N);
            return MaxSpeed > 0f ? MathF.Min(speed, MaxSpeed) : speed;
        }

        /// <summary>Distance (m) at which the jet has slowed to a speed (m/s); 0 at or above the neck velocity.</summary>
        public float DistanceAt(float speed)
        {
            if (A <= 0f) { return 0f; }
            if (speed <= 0f) { return float.PositiveInfinity; }
            if (MaxSpeed > 0f && speed >= MaxSpeed) { return 0f; }
            return MathF.Pow(A / speed, 1f / N);
        }

        /// <summary>The same jet with every distance multiplied by a factor (e.g. the free-jet factor).</summary>
        public DecayCurve ScaledDistance(float factor) => factor > 0f ? this with { A = A * MathF.Pow(factor, N) } : this;
    }

    /// <summary>
    /// What the catalog says about one diffuser at one flow.
    /// </summary>
    /// <param name="Curve">Speed against distance along one side's throw.</param>
    /// <param name="NeckVelocity">Flow / neck area (m/s).</param>
    /// <param name="TotalPressure">Pa (0 when the family has no pressures).</param>
    /// <param name="Nc">Noise criterion (0 when the family has no NC values).</param>
    /// <param name="Extrapolated">The flow is outside the table (end values used).</param>
    /// <param name="OutOfRange">The flow is more than 25 % outside the table: treat the numbers with caution.</param>
    /// <param name="SizeMatched">The neck (and face, when known) is within 5 % of a catalog size.</param>
    /// <param name="Illustrative">Generic data, not a real product.</param>
    /// <param name="FamilyKey">The family used.</param>
    public readonly record struct DiffuserPerformance(
        DecayCurve Curve, float NeckVelocity, float TotalPressure, float Nc,
        bool Extrapolated, bool OutOfRange, bool SizeMatched, bool Illustrative, string FamilyKey)
    {
        /// <summary>Throw (m) to a terminal speed (m/s).</summary>
        public float ThrowAt(float speed) => Curve.DistanceAt(speed);
    }

    /// <summary>
    /// The diffuser catalog: the built-in generic 3-cone square family plus any user families, and the maths that
    /// turns their tables into a jet for any neck, face and flow.
    /// <list type="bullet">
    /// <item><b>Fit:</b> each row's throws give <c>V = A · x^-N</c> by least squares on logs (N clamped to
    /// <see cref="MIN_N"/>–<see cref="MAX_N"/>; one pair → N = 1).</item>
    /// <item><b>Flow:</b> theory gives A ∝ Q / √(neck area), so per size A / Q and N are interpolated linearly in Q
    /// between rows; outside the table the end values are used (<see cref="DiffuserPerformance.Extrapolated"/>).</item>
    /// <item><b>Size:</b> the nearest catalog neck (then face); a neck more than 5 % off still works, with A / Q scaled
    /// by the diameter ratio (A ∝ 1 / d), and is flagged. Nominal sizes are never converted (8 in ≠ 200 mm).</item>
    /// <item><b>Pressure</b> ∝ Q² (scaled by the neck area ratio squared); <b>NC</b> linear in log Q between rows,
    /// extended with the table's slope (else 50 per decade: sound power rises with about the 5th power of speed).</item>
    /// </list>
    /// </summary>
    public sealed class DiffuserCatalog
    {
        #region Constants

        /// <summary>The built-in generic family's key.</summary>
        public const string GENERIC_KEY = "generic-3cone-square";

        /// <summary>Lowest / highest fitted decay exponent.</summary>
        public const float MIN_N = 0.8f, MAX_N = 1.3f;

        /// <summary>Relative size difference still counted as the same catalog size.</summary>
        public const float SIZE_TOLERANCE = 0.05f;

        /// <summary>How far outside the table's flows the numbers are still reasonable (fraction).</summary>
        public const float EXTRAPOLATION_LIMIT = 0.25f;

        /// <summary>NC rise per decade of flow when the table can't say (dB).</summary>
        private const float DEFAULT_NC_SLOPE = 50f;

        #endregion

        #region Fields

        private readonly List<CatalogFamily> _families;
        private readonly Dictionary<CatalogFamily, List<SizeGroup>> _fits = new();

        #endregion

        /// <summary>The families, in load order (later families with the same key replaced earlier ones).</summary>
        public IReadOnlyList<CatalogFamily> Families => _families;

        /// <summary>The built-in generic family (always present).</summary>
        public CatalogFamily Generic => Find(GENERIC_KEY);

        /// <summary>
        /// A catalog of families. Invalid families are dropped; a later family with an existing key replaces it (so a
        /// user file can override the generic data). The built-in generic family is added first when missing.
        /// </summary>
        public DiffuserCatalog(IEnumerable<CatalogFamily> families, List<string> problems = null)
        {
            _families = new List<CatalogFamily>();
            List<CatalogFamily> given = families?.ToList() ?? new List<CatalogFamily>();
            var all = new List<CatalogFamily>();
            if (!given.Any(f => string.Equals(f?.Key, GENERIC_KEY, StringComparison.OrdinalIgnoreCase)))
            {
                all.Add(BuildGenericFamily());
            }
            all.AddRange(given);

            foreach (CatalogFamily family in all)
            {
                CatalogFamily clean = family?.Clean(problems);
                if (clean == null) { continue; }
                int existing = _families.FindIndex(f => string.Equals(f.Key, clean.Key, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                {
                    Utilities.Log_Utils.Write($"Diffuser catalog: '{clean.Key}' from {clean.Source ?? "built-in"} replaces the one from {_families[existing].Source ?? "built-in"}.");
                    _families[existing] = clean;
                }
                else { _families.Add(clean); }
            }
        }

        /// <summary>The built-in catalog (generic family only).</summary>
        public static DiffuserCatalog BuiltIn() => new(null);

        #region Lookup

        /// <summary>The family with a key (case-insensitive), or null.</summary>
        public CatalogFamily Find(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { return null; }
            string k = key.Trim();
            return _families.FirstOrDefault(f => string.Equals(f.Key, k, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The family whose keywords appear in a Revit family or type name (case-insensitive), or null. Later families
        /// (user files) are tried first, so they win over the generic one.
        /// </summary>
        public CatalogFamily FindByName(string familyName, string typeName)
        {
            string name = $"{familyName} {typeName}";
            for (int i = _families.Count - 1; i >= 0; i--)
            {
                foreach (string keyword in _families[i].Match)
                {
                    if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) { return _families[i]; }
                }
            }
            return null;
        }

        #endregion

        #region Evaluate

        /// <summary>
        /// The jet, pressure and noise of a diffuser of a family at a flow.
        /// </summary>
        /// <param name="family">The family (normally from this catalog; any family works).</param>
        /// <param name="neckDiameter">Neck diameter (m).</param>
        /// <param name="faceSize">Face size (m), or 0 when unknown (the face is then not matched).</param>
        /// <param name="flowLps">Diffuser flow (L/s). Zero or less gives a still jet.</param>
        public DiffuserPerformance Evaluate(CatalogFamily family, float neckDiameter, float faceSize, float flowLps)
        {
            family ??= Generic;
            List<SizeGroup> groups = FitsFor(family);
            if (groups.Count == 0 || !float.IsFinite(flowLps) || flowLps <= 0f || !float.IsFinite(neckDiameter) || neckDiameter <= 0f)
            {
                return new DiffuserPerformance(default, 0f, 0f, 0f, false, false, false, family.Illustrative, family.Key);
            }

            // Nearest neck, then the nearest face among that neck's sizes
            SizeGroup best = null;
            float bestNeck = float.MaxValue, bestFace = float.MaxValue;
            foreach (SizeGroup g in groups)
            {
                float neckGap = MathF.Abs(MathF.Log(g.Neck / neckDiameter));
                float faceGap = faceSize > 0f ? MathF.Abs(MathF.Log(g.Face / faceSize)) : 0f;
                if (neckGap < bestNeck - 1e-6f || (MathF.Abs(neckGap - bestNeck) <= 1e-6f && faceGap < bestFace))
                {
                    best = g;
                    bestNeck = neckGap;
                    bestFace = faceGap;
                }
            }
            bool sizeMatched = MathF.Abs(best.Neck / neckDiameter - 1f) <= SIZE_TOLERANCE
                && (faceSize <= 0f || MathF.Abs(best.Face / faceSize - 1f) <= SIZE_TOLERANCE);

            float q = flowLps;
            float qMin = best.Flow[0], qMax = best.Flow[^1];
            bool extrapolated = q < qMin * 0.9999f || q > qMax * 1.0001f;
            bool outOfRange = q < qMin * (1f - EXTRAPOLATION_LIMIT) || q > qMax * (1f + EXTRAPOLATION_LIMIT);

            // A / Q and N at this flow; a different neck scales A / Q by the diameter ratio (A ∝ Q / d)
            float neckRatio = best.Neck / neckDiameter;
            float k = Interpolate(best.Flow, best.K, q) * neckRatio;
            float n = Interpolate(best.Flow, best.N, q);
            float neckArea = MathF.PI * neckDiameter * neckDiameter / 4f;
            float neckVelocity = q / 1000f / neckArea;
            var curve = new DecayCurve(k * q, n, neckVelocity);

            float pressure = best.PressureFlow.Length == 0 ? 0f
                : Interpolate(best.PressureFlow, best.PressureK, q) * q * q * neckRatio * neckRatio * neckRatio * neckRatio;
            float nc = best.NcFlow.Length == 0 ? 0f : MathF.Max(0f, NcAt(best, q));

            return new DiffuserPerformance(curve, neckVelocity, pressure, nc, extrapolated, outOfRange, sizeMatched, family.Illustrative, family.Key);
        }

        /// <summary>
        /// Piecewise-linear interpolation in x (sorted ascending); outside, the end value.
        /// </summary>
        private static float Interpolate(float[] xs, float[] ys, float x)
        {
            if (xs.Length == 1 || x <= xs[0]) { return ys[0]; }
            if (x >= xs[^1]) { return ys[^1]; }
            int i = 1;
            while (xs[i] < x) { i++; }
            float t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
            return ys[i - 1] + (ys[i] - ys[i - 1]) * t;
        }

        /// <summary>NC linear in log10(Q) between rows, extended with the end segment's slope.</summary>
        private static float NcAt(SizeGroup g, float q)
        {
            float[] xs = g.NcFlow, ys = g.Nc;
            float lq = MathF.Log10(q);
            if (xs.Length == 1) { return ys[0] + DEFAULT_NC_SLOPE * (lq - MathF.Log10(xs[0])); }
            int i;
            if (q <= xs[0]) { i = 1; }
            else if (q >= xs[^1]) { i = xs.Length - 1; }
            else { i = 1; while (xs[i] < q) { i++; } }
            float x0 = MathF.Log10(xs[i - 1]), x1 = MathF.Log10(xs[i]);
            float slope = x1 > x0 ? (ys[i] - ys[i - 1]) / (x1 - x0) : DEFAULT_NC_SLOPE;
            return ys[i - 1] + slope * (lq - x0);
        }

        #endregion

        #region Fit

        /// <summary>One catalog size (neck + face) with its rows' fitted values, sorted by flow.</summary>
        private sealed class SizeGroup
        {
            public float Neck, Face;
            public float[] Flow, K, N;               // per row: Q, A / Q, N
            public float[] PressureFlow, PressureK;  // rows with a pressure: Q, Pt / Q²
            public float[] NcFlow, Nc;               // rows with an NC: Q, NC
        }

        private List<SizeGroup> FitsFor(CatalogFamily family)
        {
            if (_fits.TryGetValue(family, out List<SizeGroup> groups)) { return groups; }
            groups = new List<SizeGroup>();
            foreach (IGrouping<(float, float), CatalogRow> size in family.Rows
                .GroupBy(r => (MathF.Round(r.NeckDiameter, 4), MathF.Round(r.FaceSize, 4))))
            {
                // One value per flow (duplicate flows: the first)
                List<CatalogRow> rows = size.GroupBy(r => r.Flow).Select(d => d.First()).OrderBy(r => r.Flow).ToList();
                var g = new SizeGroup
                {
                    Neck = size.Key.Item1,
                    Face = size.Key.Item2,
                    Flow = new float[rows.Count],
                    K = new float[rows.Count],
                    N = new float[rows.Count]
                };
                for (int i = 0; i < rows.Count; i++)
                {
                    (float a, float n, bool clamped) = FitRow(rows[i].ThrowSpeeds, rows[i].Throws);
                    if (clamped) { Utilities.Log_Utils.Write($"Diffuser catalog: {family.Key} {rows[i].NeckDiameter * 1000f:0} mm / {rows[i].Flow:0} L/s decay exponent clamped to {n:0.00}."); }
                    g.Flow[i] = rows[i].Flow;
                    g.K[i] = a / rows[i].Flow;
                    g.N[i] = n;
                }
                List<CatalogRow> withPressure = rows.Where(r => r.TotalPressure > 0f).ToList();
                g.PressureFlow = withPressure.Select(r => r.Flow).ToArray();
                g.PressureK = withPressure.Select(r => r.TotalPressure / (r.Flow * r.Flow)).ToArray();
                List<CatalogRow> withNc = rows.Where(r => r.Nc > 0f).ToList();
                g.NcFlow = withNc.Select(r => r.Flow).ToArray();
                g.Nc = withNc.Select(r => r.Nc).ToArray();
                groups.Add(g);
            }
            _fits[family] = groups;
            return groups;
        }

        /// <summary>
        /// Fits <c>V = A · x^-N</c> to (speed, throw) pairs: least squares on <c>ln V = ln A − N · ln x</c>. One pair (or
        /// pairs at a single distance) → N = 1. N outside <see cref="MIN_N"/>–<see cref="MAX_N"/> is clamped and A refitted.
        /// </summary>
        /// <returns>A, N, and whether N was clamped.</returns>
        public static (float A, float N, bool Clamped) FitRow(float[] speeds, float[] throws)
        {
            int count = Math.Min(speeds?.Length ?? 0, throws?.Length ?? 0);
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int used = 0;
            for (int i = 0; i < count; i++)
            {
                if (!(speeds[i] > 0f) || !(throws[i] > 0f)) { continue; }
                double x = Math.Log(throws[i]), y = Math.Log(speeds[i]);
                sx += x; sy += y; sxx += x * x; sxy += x * y;
                used++;
            }
            if (used == 0) { return (0f, 1f, false); }

            double denominator = used * sxx - sx * sx;
            double n = used >= 2 && denominator > 1e-9 ? -(used * sxy - sx * sy) / denominator : 1.0;
            bool clamped = false;
            if (used >= 2 && denominator > 1e-9 && (n < MIN_N || n > MAX_N))
            {
                n = Math.Clamp(n, MIN_N, MAX_N);
                clamped = true;
            }
            // Best ln A for this N: mean of ln V + N ln x
            double lnA = (sy + n * sx) / used;
            return ((float)Math.Exp(lnA), (float)n, clamped);
        }

        #endregion

        #region Built-in generic family

        // Generic 3-cone square throw model (IP units: CFM, ft², fpm → ft), tuned so an 8 in neck at 250 CFM throws
        // about 5 / 7 / 13 ft at 150 / 100 / 50 fpm, typical of published 3-cone diffusers. Generated, never copied.
        private const float GENERIC_C = 2.26f;
        private const float GENERIC_N = 1.15f;
        private static readonly float[] GENERIC_IP_NECKS = { 6, 8, 10, 12, 14, 16 };          // in
        private static readonly float[] GENERIC_IP_VELOCITIES = { 400, 500, 600, 700, 800 };  // fpm
        private static readonly float[] GENERIC_IP_SPEEDS = { 150, 100, 50 };                // fpm
        private static readonly float[] GENERIC_SI_NECKS = { 150, 200, 250, 300, 350, 400 };  // mm
        private static readonly float[] GENERIC_SI_VELOCITIES = { 2.0f, 2.5f, 3.0f, 3.5f, 4.0f }; // m/s
        private static readonly float[] GENERIC_SI_SPEEDS = { 0.75f, 0.5f, 0.25f };          // m/s
        private const float FT2_TO_M2 = UnitConvert.FT_TO_M * UnitConvert.FT_TO_M;

        /// <summary>
        /// The built-in generic 3-cone square family, generated from a throw model (illustrative): IP necks 6–16 in
        /// with 12 in (6, 8 in necks) and 24 in faces at 400–800 fpm neck velocity; SI necks 150–400 mm with 300 mm
        /// (150, 200 mm) and 600 mm faces at 2–4 m/s. Flows rounded to 5 CFM / 5 L/s.
        /// </summary>
        public static CatalogFamily BuildGenericFamily()
        {
            var family = new CatalogFamily
            {
                Key = GENERIC_KEY,
                Label = "Generic 3-cone square (illustrative)",
                Illustrative = true,
                SourceUnits = MeasurementSystem.IP,
                Match = new List<string> { "3 cone", "3-cone", "three cone", "square cone", "cone diffuser", "scd", "tms" }
            };

            foreach (float neckIn in GENERIC_IP_NECKS)
            {
                float areaFt2 = MathF.PI * (neckIn / 12f) * (neckIn / 12f) / 4f;
                foreach (float faceIn in neckIn <= 8f ? new[] { 12f, 24f } : new[] { 24f })
                {
                    foreach (float velocity in GENERIC_IP_VELOCITIES)
                    {
                        float cfm = MathF.Max(5f, MathF.Round(velocity * areaFt2 / 5f) * 5f);
                        family.Rows.Add(GenericRow(neckIn * UnitConvert.IN_TO_M, faceIn * UnitConvert.IN_TO_M, cfm, areaFt2,
                            GENERIC_IP_SPEEDS.Select(s => s * UnitConvert.FPM_TO_MPS).ToArray()));
                    }
                }
            }

            foreach (float neckMm in GENERIC_SI_NECKS)
            {
                float neckM = neckMm * UnitConvert.MM_TO_M;
                float areaM2 = MathF.PI * neckM * neckM / 4f;
                foreach (float faceMm in neckMm <= 200f ? new[] { 300f, 600f } : new[] { 600f })
                {
                    foreach (float velocity in GENERIC_SI_VELOCITIES)
                    {
                        float lps = MathF.Max(5f, MathF.Round(velocity * areaM2 * 1000f / 5f) * 5f);
                        family.Rows.Add(GenericRow(neckM, faceMm * UnitConvert.MM_TO_M, lps / UnitConvert.CFM_TO_LPS, areaM2 / FT2_TO_M2,
                            GENERIC_SI_SPEEDS, siFlow: lps));
                    }
                }
            }
            return family;
        }

        /// <summary>One generated row: throws from the model, pressure ≈ 1.8 neck velocity pressures, NC from neck velocity and size.</summary>
        private static CatalogRow GenericRow(float neckM, float faceM, float cfm, float areaFt2, float[] speedsMps, float siFlow = 0f)
        {
            float neckFpm = cfm / areaFt2;
            float[] throws = speedsMps
                .Select(s => MathF.Pow(GENERIC_C * cfm / (MathF.Sqrt(areaFt2) * (s / UnitConvert.FPM_TO_MPS)), 1f / GENERIC_N) * UnitConvert.FT_TO_M)
                .ToArray();
            float pressureInWg = 1.8f * (neckFpm / 4005f) * (neckFpm / 4005f);
            float nc = MathF.Round(MathF.Max(10f, 20f + 50f * MathF.Log10(neckFpm / 700f) + 10f * MathF.Log10(areaFt2 / 0.349f)));
            return new CatalogRow
            {
                NeckDiameter = neckM,
                FaceSize = faceM,
                Flow = siFlow > 0f ? siFlow : cfm * UnitConvert.CFM_TO_LPS,
                TotalPressure = pressureInWg * UnitConvert.INWG_TO_PA,
                Nc = nc,
                ThrowSpeeds = (float[])speedsMps.Clone(),
                Throws = throws
            };
        }

        #endregion
    }
}
