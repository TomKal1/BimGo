using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Round X-D1: a light's distribution (how much it sends each way) as the renderer takes it: a small table of relative
    /// intensity over the fixture's own angles, plus the scale that keeps its total output at the fixture's lumens.
    /// <list type="bullet">
    /// <item>Angles are IES type C: <b>gamma</b> 0° = straight out of the fixture (nadir for a downlight), 180° = straight
    /// back; <b>C</b> 0° along the fixture's "across" axis, 90° a quarter turn anticlockwise seen from behind the light
    /// (from above, for a downlight).</item>
    /// <item>The table is <see cref="VERTICAL"/> gamma rows (0–180° even steps) × <see cref="HORIZONTAL"/> C columns
    /// (0–337.5°, wrapping), one byte each, peak 255. Bilinear between samples (as the shader does).</item>
    /// <item><see cref="Scale"/> = 4π / ∫ table dΩ (peak 1): intensity in units of an even (omnidirectional) light of the
    /// same lumens, so swapping a profile never changes how much light a fixture gives, only where it goes.</item>
    /// </list>
    /// </summary>
    public sealed class LightProfile
    {
        #region Constants

        /// <summary>Gamma samples (0–180°).</summary>
        public const int VERTICAL = 32;

        /// <summary>C samples (0–360°, wrapping).</summary>
        public const int HORIZONTAL = 16;

        /// <summary>Bytes per table.</summary>
        public const int TABLE_SIZE = VERTICAL * HORIZONTAL;

        /// <summary>Gamma step (degrees).</summary>
        public const float GAMMA_STEP = 180f / (VERTICAL - 1);

        /// <summary>C step (degrees).</summary>
        public const float C_STEP = 360f / HORIZONTAL;

        /// <summary>The shader's finer table: gamma samples (0–180°, 2.5° steps).</summary>
        public const int FINE_VERTICAL = 73;

        /// <summary>The shader's finer table: C samples (0–360°, 7.5° steps, wrapping).</summary>
        public const int FINE_HORIZONTAL = 48;

        /// <summary>Values per fine table.</summary>
        public const int FINE_SIZE = FINE_VERTICAL * FINE_HORIZONTAL;

        /// <summary>Fine table gamma step (degrees).</summary>
        public const float FINE_GAMMA_STEP = 180f / (FINE_VERTICAL - 1);

        /// <summary>Fine table C step (degrees).</summary>
        public const float FINE_C_STEP = 360f / FINE_HORIZONTAL;

        /// <summary>Largest .ies file read (bytes): real ones are a few KB, the biggest well under this.</summary>
        public const int MAX_IES_BYTES = 512 * 1024;

        #endregion

        #region Properties

        /// <summary>Its key: <c>builtin:…</c>, or <c>ies:&lt;hash&gt;</c> for a file.</summary>
        public string Key { get; init; }

        /// <summary>The name shown (a built-in's description, or the file name).</summary>
        public string Name { get; init; }

        /// <summary>The table (<see cref="TABLE_SIZE"/> bytes, row = gamma sample, column = C sample).</summary>
        public byte[] Table { get; init; } = new byte[TABLE_SIZE];

        /// <summary>4π / the table's integral over the sphere (peak 1): see the class notes.</summary>
        public float Scale { get; init; } = 1f;

        /// <summary>
        /// The shader's table (<see cref="FINE_SIZE"/> 16-bit values, peak 65535, row = gamma, column = C): the same
        /// intensity sampled finer and without 8-bit steps, so narrow beams and dim tails keep their shape on screen.
        /// </summary>
        public ushort[] Fine { get; init; } = FilledFine();

        /// <summary><see cref="Scale"/> for <see cref="Fine"/>.</summary>
        public float FineScale { get; init; } = 1f;

        private static ushort[] FilledFine()
        {
            var fine = new ushort[FINE_SIZE];
            Array.Fill(fine, ushort.MaxValue);
            return fine;
        }

        /// <summary>The luminaire's output the file implies (its candela integrated, lm), or 0 for built-ins.</summary>
        public float Lumens { get; init; }

        /// <summary>Full angle (°) within which the C-averaged intensity stays above half its peak (0 = no clear beam).</summary>
        public float BeamAngle { get; init; }

        /// <summary>True for files from Revit or imported (not a built-in).</summary>
        public bool IsFile => Key != null && Key.StartsWith(LightProfiles.FILE_PREFIX, StringComparison.Ordinal);

        /// <summary>Round Y-C1: true for a profile generated from a Revit spot / hemispherical light's settings.</summary>
        public bool IsGenerated => Key != null && Key.StartsWith(LightProfiles.GENERATED_PREFIX, StringComparison.Ordinal);

        /// <summary>
        /// Round Y-C1: a file's TILT=INCLUDE data: lamp tilt angles (degrees, increasing) and the output factor at each,
        /// or null (TILT=NONE, or a built-in). See <see cref="TiltFactor"/>.
        /// </summary>
        public float[] TiltAngles { get; init; }

        /// <summary>Round Y-C1: the factors matching <see cref="TiltAngles"/>.</summary>
        public float[] TiltFactors { get; init; }

        /// <summary>
        /// Round Y-C1: the file's output factor for a lamp tilted this far from how it was measured (degrees: for a
        /// fixture, how far its aim is turned from straight down). Linear between the file's angles, held at its ends;
        /// 1 without tilt data. LM-63 applies it to every candela value, so it scales the output, not the shape.
        /// </summary>
        public float TiltFactor(float degrees)
        {
            float[] a = TiltAngles, f = TiltFactors;
            if (a == null || f == null || a.Length == 0 || a.Length != f.Length) { return 1f; }
            if (!float.IsFinite(degrees)) { return 1f; }
            if (degrees <= a[0]) { return f[0]; }
            if (degrees >= a[^1]) { return f[^1]; }
            int i = 0;
            while (i < a.Length - 2 && a[i + 1] < degrees) { i++; }
            float t = (degrees - a[i]) / MathF.Max(a[i + 1] - a[i], 1e-6f);
            return f[i] + (f[i + 1] - f[i]) * Math.Clamp(t, 0f, 1f);
        }

        #endregion

        #region Sampling

        /// <summary>
        /// The relative intensity (peak 1) at an angle, bilinear between the table's samples, as the shader reads it.
        /// </summary>
        /// <param name="gammaDegrees">0–180.</param>
        /// <param name="cDegrees">Any (wraps).</param>
        public float Sample(float gammaDegrees, float cDegrees) => SampleTable(Table, gammaDegrees, cDegrees);

        internal static float SampleTable(byte[] table, float gammaDegrees, float cDegrees)
        {
            float gv = Math.Clamp(gammaDegrees, 0f, 180f) / GAMMA_STEP;
            int v0 = Math.Min((int)gv, VERTICAL - 2);
            float fv = gv - v0;
            float c = cDegrees % 360f;
            if (c < 0f) { c += 360f; }
            float ch = c / C_STEP;
            int h0 = (int)ch % HORIZONTAL;
            int h1 = (h0 + 1) % HORIZONTAL;
            float fh = ch - MathF.Floor(ch);
            float a = table[v0 * HORIZONTAL + h0], b = table[v0 * HORIZONTAL + h1];
            float d = table[(v0 + 1) * HORIZONTAL + h0], e = table[(v0 + 1) * HORIZONTAL + h1];
            float top = a + (b - a) * fh, bottom = d + (e - d) * fh;
            return (top + (bottom - top) * fv) / 255f;
        }

        #endregion

        #region Building

        /// <summary>
        /// A profile from a function of (gamma, C) in degrees (any scale: normalised to peak 1 here).
        /// </summary>
        public static LightProfile FromFunction(string key, string name, Func<float, float, float> intensity, float lumens = 0f,
            float[] tiltAngles = null, float[] tiltFactors = null)
        {
            var raw = new float[TABLE_SIZE];
            float peak = 0f;
            for (int v = 0; v < VERTICAL; v++)
            {
                for (int h = 0; h < HORIZONTAL; h++)
                {
                    float value = MathF.Max(0f, intensity(v * GAMMA_STEP, h * C_STEP));
                    if (!float.IsFinite(value)) { value = 0f; }
                    raw[v * HORIZONTAL + h] = value;
                    peak = MathF.Max(peak, value);
                }
            }
            var table = new byte[TABLE_SIZE];
            if (peak <= 0f)
            {
                // Nothing at all: an even light
                Array.Fill(table, (byte)255);
            }
            else
            {
                for (int i = 0; i < TABLE_SIZE; i++) { table[i] = (byte)Math.Clamp((int)MathF.Round(raw[i] / peak * 255f), 0, 255); }
            }
            float integral = Integrate(table);
            ushort[] fine = FineTable(intensity);
            float fineIntegral = Integrate((g, c) => SampleFine(fine, g, c));
            return new LightProfile
            {
                Key = key,
                Name = name,
                Table = table,
                Scale = integral > 1e-4f ? 4f * MathF.PI / integral : 1f,
                Fine = fine,
                FineScale = fineIntegral > 1e-4f ? 4f * MathF.PI / fineIntegral : 1f,
                Lumens = lumens,
                BeamAngle = BeamOf(table),
                TiltAngles = tiltAngles,
                TiltFactors = tiltFactors
            };
        }

        /// <summary>
        /// A profile from an .ies file's text (IESNA LM-63 1986 / 1991 / 1995 / 2002, type C photometry).
        /// </summary>
        /// <returns>The profile, or null with a reason.</returns>
        public static LightProfile FromIes(string key, string name, string text, out string error)
        {
            IesData data = IesData.Parse(text, out error);
            if (data == null) { return null; }
            return FromFunction(key, name, data.Candela, data.TotalLumens(), data.TiltAngles, data.TiltFactors);
        }

        /// <summary>The fine table of <paramref name="intensity"/> (peak 65535; an even light when it gives nothing).</summary>
        private static ushort[] FineTable(Func<float, float, float> intensity)
        {
            var raw = new float[FINE_SIZE];
            float peak = 0f;
            for (int v = 0; v < FINE_VERTICAL; v++)
            {
                for (int h = 0; h < FINE_HORIZONTAL; h++)
                {
                    float value = MathF.Max(0f, intensity(v * FINE_GAMMA_STEP, h * FINE_C_STEP));
                    if (!float.IsFinite(value)) { value = 0f; }
                    raw[v * FINE_HORIZONTAL + h] = value;
                    peak = MathF.Max(peak, value);
                }
            }
            var fine = new ushort[FINE_SIZE];
            if (peak <= 0f) { Array.Fill(fine, ushort.MaxValue); }
            else
            {
                for (int i = 0; i < FINE_SIZE; i++) { fine[i] = (ushort)Math.Clamp((int)MathF.Round(raw[i] / peak * 65535f), 0, 65535); }
            }
            return fine;
        }

        /// <summary>The fine table's relative intensity (peak 1), bilinear as the shader's texture filtering reads it.</summary>
        public static float SampleFine(ushort[] fine, float gammaDegrees, float cDegrees)
        {
            float gv = Math.Clamp(gammaDegrees, 0f, 180f) / FINE_GAMMA_STEP;
            int v0 = Math.Min((int)gv, FINE_VERTICAL - 2);
            float fv = gv - v0;
            float c = cDegrees % 360f;
            if (c < 0f) { c += 360f; }
            float ch = c / FINE_C_STEP;
            int h0 = (int)ch % FINE_HORIZONTAL;
            int h1 = (h0 + 1) % FINE_HORIZONTAL;
            float fh = ch - MathF.Floor(ch);
            float a = fine[v0 * FINE_HORIZONTAL + h0], b = fine[v0 * FINE_HORIZONTAL + h1];
            float d = fine[(v0 + 1) * FINE_HORIZONTAL + h0], e = fine[(v0 + 1) * FINE_HORIZONTAL + h1];
            float top = a + (b - a) * fh, bottom = d + (e - d) * fh;
            return (top + (bottom - top) * fv) / 65535f;
        }

        /// <summary>
        /// The table's integral over the sphere (peak 1), finely sampled through the same bilinear lookup the shader uses.
        /// </summary>
        public static float Integrate(byte[] table) => Integrate((g, c) => SampleTable(table, g, c));

        /// <summary>The integral over the sphere of a relative intensity (gamma, C in degrees).</summary>
        private static float Integrate(Func<float, float, float> sample)
        {
            const int GAMMA = 360, C = 64;
            double sum = 0.0;
            double dGamma = Math.PI / GAMMA, dC = 2.0 * Math.PI / C;
            for (int i = 0; i < GAMMA; i++)
            {
                double gamma = (i + 0.5) * dGamma;
                double band = Math.Sin(gamma) * dGamma * dC;
                float gDeg = (float)(gamma * 180.0 / Math.PI);
                for (int j = 0; j < C; j++)
                {
                    sum += sample(gDeg, (float)((j + 0.5) * 360.0 / C)) * band;
                }
            }
            return (float)sum;
        }

        /// <summary>Full angle around gamma 0 where the C-averaged intensity is at least half the strongest gamma's (0 if the strongest isn't near gamma 0).</summary>
        private static float BeamOf(byte[] table)
        {
            var row = new float[VERTICAL];
            float peak = 0f;
            int peakRow = 0;
            for (int v = 0; v < VERTICAL; v++)
            {
                float s = 0f;
                for (int h = 0; h < HORIZONTAL; h++) { s += table[v * HORIZONTAL + h]; }
                row[v] = s / HORIZONTAL;
                if (row[v] > peak) { peak = row[v]; peakRow = v; }
            }
            if (peak <= 0f || peakRow * GAMMA_STEP > 30f) { return 0f; }
            for (int v = peakRow; v < VERTICAL - 1; v++)
            {
                if (row[v + 1] < peak * 0.5f)
                {
                    float t = (row[v] - peak * 0.5f) / MathF.Max(row[v] - row[v + 1], 1e-6f);
                    return 2f * (v + t) * GAMMA_STEP;
                }
            }
            return 360f;
        }

        #endregion
    }

    /// <summary>
    /// Round X-D1: an IESNA LM-63 photometric file, parsed. Type C only (types A / B, used for floodlights and road
    /// lights, are refused); the symmetric layouts (one C plane, 0–90, 0–180, 90–270) and full 0–360 files are expanded by
    /// <see cref="Candela"/>. Round Y-C1: TILT=INCLUDE data is kept (<see cref="TiltAngles"/> / <see cref="TiltFactors"/>);
    /// TILT=&lt;file name&gt; (tilt data in a separate file) is read as no tilt (<see cref="TiltFileIgnored"/>).
    /// </summary>
    public sealed class IesData
    {
        /// <summary>Number of lamps.</summary>
        public int Lamps { get; private set; }

        /// <summary>Lumens per lamp (-1 = absolute photometry).</summary>
        public float LumensPerLamp { get; private set; }

        /// <summary>The candela multiplier.</summary>
        public float Multiplier { get; private set; }

        /// <summary>1 = type C, 2 = type B, 3 = type A.</summary>
        public int PhotometricType { get; private set; }

        /// <summary>Gamma angles (degrees, increasing).</summary>
        public float[] Vertical { get; private set; }

        /// <summary>C angles (degrees, increasing).</summary>
        public float[] Horizontal { get; private set; }

        /// <summary>Candela, [horizontal][vertical], multiplier applied.</summary>
        public float[][] Values { get; private set; }

        /// <summary>Round Y-C1: TILT=INCLUDE lamp-to-luminaire geometry (1 vertical lamp, 2 / 3 horizontal), or 0.</summary>
        public int TiltGeometry { get; private set; }

        /// <summary>Round Y-C1: TILT=INCLUDE angles (degrees, increasing), or null.</summary>
        public float[] TiltAngles { get; private set; }

        /// <summary>Round Y-C1: TILT=INCLUDE output factors (one per angle), or null.</summary>
        public float[] TiltFactors { get; private set; }

        /// <summary>Round Y-C1: the file named a separate tilt file (TILT=&lt;name&gt;): not read, so no tilt.</summary>
        public bool TiltFileIgnored { get; private set; }

        /// <summary>The keywords ([TEST], [MANUFAC], [LUMINAIRE]…), first value of each.</summary>
        public Dictionary<string, string> Keywords { get; } = new(StringComparer.OrdinalIgnoreCase);

        #region Parsing

        /// <summary>
        /// Parses a file's text.
        /// </summary>
        /// <returns>The data, or null with a reason.</returns>
        public static IesData Parse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "The file is empty.";
                return null;
            }
            if (text.Length > LightProfile.MAX_IES_BYTES)
            {
                error = "The file is too big for an .ies file.";
                return null;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var data = new IesData();
            int line = 0;
            bool tiltFound = false;
            string tilt = null;
            for (; line < lines.Length; line++)
            {
                string l = lines[line].Trim();
                if (l.StartsWith("TILT", StringComparison.OrdinalIgnoreCase))
                {
                    int eq = l.IndexOf('=');
                    tilt = eq >= 0 ? l[(eq + 1)..].Trim() : string.Empty;
                    tiltFound = true;
                    line++;
                    break;
                }
                if (l.StartsWith('['))
                {
                    int close = l.IndexOf(']');
                    if (close > 1)
                    {
                        string keyword = l[1..close];
                        if (!data.Keywords.ContainsKey(keyword)) { data.Keywords[keyword] = l[(close + 1)..].Trim(); }
                    }
                }
            }
            if (!tiltFound)
            {
                error = "Not an IES photometric file (no TILT line).";
                return null;
            }

            // The rest is numbers, separated by spaces, commas or line breaks
            var numbers = new List<double>();
            for (; line < lines.Length; line++)
            {
                foreach (string token in lines[line].Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    {
                        error = $"Unreadable number “{Shorten(token)}” in the photometric data.";
                        return null;
                    }
                    numbers.Add(value);
                }
            }
            int at = 0;
            double Next() => at < numbers.Count ? numbers[at++] : double.NaN;

            // TILT=INCLUDE: lamp-to-luminaire geometry, angle count, angles, factors (Round Y-C1: kept)
            if (string.Equals(tilt, "INCLUDE", StringComparison.OrdinalIgnoreCase))
            {
                double geometry = Next();
                double count = Next();
                if (!(count >= 0 && count < 1000) || numbers.Count - at < 2 * (int)count)
                {
                    error = "Damaged TILT data.";
                    return null;
                }
                int n = (int)count;
                var angles = new float[n];
                var factors = new float[n];
                bool usable = n > 0;
                for (int i = 0; i < n; i++) { angles[i] = (float)Next(); }
                for (int i = 0; i < n; i++)
                {
                    double factor = Next();
                    factors[i] = double.IsFinite(factor) && factor >= 0 ? (float)Math.Min(factor, 10.0) : 1f;
                }
                for (int i = 0; i < n; i++)
                {
                    if (!float.IsFinite(angles[i]) || (i > 0 && !(angles[i] > angles[i - 1]))) { usable = false; }
                }
                if (usable)
                {
                    data.TiltGeometry = double.IsFinite(geometry) ? (int)geometry : 0;
                    data.TiltAngles = angles;
                    data.TiltFactors = factors;
                }
            }
            else if (!string.IsNullOrEmpty(tilt) && !string.Equals(tilt, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                data.TiltFileIgnored = true;
            }

            double lamps = Next(), lumens = Next(), multiplier = Next(), nv = Next(), nh = Next(), type = Next();
            Next(); Next(); Next(); Next(); // units, width, length, height
            Next(); Next(); Next();         // ballast factor, future use (ballast-lamp factor), input watts
            if (double.IsNaN(type) || !(nv >= 2 && nv <= 2000) || !(nh >= 1 && nh <= 2000))
            {
                error = "Damaged photometric header (angle counts).";
                return null;
            }
            data.Lamps = (int)Math.Max(1, lamps);
            data.LumensPerLamp = (float)lumens;
            data.Multiplier = multiplier > 0 && double.IsFinite(multiplier) ? (float)multiplier : 1f;
            data.PhotometricType = (int)type;
            if (data.PhotometricType != 1)
            {
                error = data.PhotometricType == 2 || data.PhotometricType == 3
                    ? $"Type {(data.PhotometricType == 2 ? "B" : "A")} photometry (floodlights, road lights) isn't supported: only type C."
                    : "Unknown photometric type.";
                return null;
            }

            int v = (int)nv, h = (int)nh;
            if (numbers.Count - at < v + h + v * h)
            {
                error = "The file ends before all its candela values.";
                return null;
            }
            data.Vertical = new float[v];
            data.Horizontal = new float[h];
            for (int i = 0; i < v; i++) { data.Vertical[i] = (float)Next(); }
            for (int i = 0; i < h; i++) { data.Horizontal[i] = (float)Next(); }
            data.Values = new float[h][];
            for (int j = 0; j < h; j++)
            {
                data.Values[j] = new float[v];
                for (int i = 0; i < v; i++)
                {
                    double cd = Next() * data.Multiplier;
                    data.Values[j][i] = double.IsFinite(cd) && cd > 0 ? (float)cd : 0f;
                }
            }
            if (!Increasing(data.Vertical) || !Increasing(data.Horizontal) || data.Vertical[0] < -0.01f || data.Vertical[^1] > 180.01f
                || data.Horizontal[0] < -0.01f || data.Horizontal[^1] > 360.01f)
            {
                error = "The angles are out of order or out of range.";
                return null;
            }
            return data;

            static bool Increasing(float[] a)
            {
                for (int i = 1; i < a.Length; i++) { if (!(a[i] > a[i - 1])) { return false; } }
                return true;
            }
        }

        private static string Shorten(string s) => s.Length > 20 ? s[..20] + "…" : s;

        #endregion

        #region Lookup

        /// <summary>
        /// Candela at an angle, expanding the file's symmetry, bilinear between its angles (0 outside its gamma range).
        /// </summary>
        public float Candela(float gammaDegrees, float cDegrees)
        {
            float g = gammaDegrees;
            if (g < Vertical[0] - 1e-3f || g > Vertical[^1] + 1e-3f) { return 0f; }
            float c = cDegrees % 360f;
            if (c < 0f) { c += 360f; }
            float first = Horizontal[0], last = Horizontal[^1];

            if (Horizontal.Length == 1) { return Along(Values[0], g); }
            if (first < 1e-3f && MathF.Abs(last - 90f) < 1e-3f)
            {
                // Quadrant symmetry
                c %= 180f;
                if (c > 90f) { c = 180f - c; }
            }
            else if (first < 1e-3f && MathF.Abs(last - 180f) < 1e-3f)
            {
                // Bilateral about the 0–180 plane
                if (c > 180f) { c = 360f - c; }
            }
            else if (MathF.Abs(first - 90f) < 1e-3f && MathF.Abs(last - 270f) < 1e-3f)
            {
                // Bilateral about the 90–270 plane
                if (c < 90f) { c = 180f - c; }
                else if (c > 270f) { c = 540f - c; }
            }

            // Bracket c (wrapping past the last angle back to the first, 360° on)
            int j = 0;
            while (j < Horizontal.Length - 1 && Horizontal[j + 1] < c) { j++; }
            if (c <= first) { return Along(Values[0], g); }
            if (j >= Horizontal.Length - 1)
            {
                float span = first + 360f - last;
                if (span < 1e-3f) { return Along(Values[^1], g); }
                float tw = Math.Clamp((c - last) / span, 0f, 1f);
                return Lerp(Along(Values[^1], g), Along(Values[0], g), tw);
            }
            float t = Math.Clamp((c - Horizontal[j]) / (Horizontal[j + 1] - Horizontal[j]), 0f, 1f);
            return Lerp(Along(Values[j], g), Along(Values[j + 1], g), t);
        }

        private float Along(float[] plane, float g)
        {
            if (g <= Vertical[0]) { return plane[0]; }
            if (g >= Vertical[^1]) { return plane[^1]; }
            int i = 0;
            while (i < Vertical.Length - 2 && Vertical[i + 1] < g) { i++; }
            float t = (g - Vertical[i]) / (Vertical[i + 1] - Vertical[i]);
            return Lerp(plane[i], plane[i + 1], Math.Clamp(t, 0f, 1f));
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>
        /// The luminaire's output (lm): its candela integrated over the sphere (finely, through <see cref="Candela"/>).
        /// </summary>
        public float TotalLumens()
        {
            const int GAMMA = 360, C = 72;
            double sum = 0.0, dGamma = Math.PI / GAMMA, dC = 2.0 * Math.PI / C;
            for (int i = 0; i < GAMMA; i++)
            {
                double gamma = (i + 0.5) * dGamma;
                double band = Math.Sin(gamma) * dGamma * dC;
                for (int j = 0; j < C; j++)
                {
                    sum += Candela((float)(gamma * 180.0 / Math.PI), (float)((j + 0.5) * 360.0 / C)) * band;
                }
            }
            return (float)sum;
        }

        #endregion
    }

    /// <summary>
    /// Round X-D1: the built-in profiles (generated here: MIT-0 like the rest of BimGo, no third-party data), file
    /// profile keys, the per-type choice values and the per-light code the shaders read.
    /// </summary>
    public static class LightProfiles
    {
        #region Keys

        /// <summary>Key prefix of a file's profile (its content hash follows).</summary>
        public const string FILE_PREFIX = "ies:";

        /// <summary>Key prefix of a built-in.</summary>
        public const string BUILT_IN_PREFIX = "builtin:";

        /// <summary>Per-type choice: the file Revit's family names (the default when it has one).</summary>
        public const string REVIT = "revit";

        /// <summary>Per-type choice: no profile (BimGo's own downward lobe, as before X-D1).</summary>
        public const string LOBE = "lobe";

        /// <summary>
        /// Round Y-C1: key prefix of a profile generated from a Revit light's own distribution (<see cref="SpotKey"/>,
        /// <see cref="HEMISPHERICAL"/>).
        /// </summary>
        public const string GENERATED_PREFIX = "gen:";

        /// <summary>
        /// A file's key: <c>ies:</c> + the first 16 hex characters of the SHA-256 of its text (line endings normalised),
        /// so one file reached by two paths (or imported twice) is one profile.
        /// </summary>
        public static string KeyOf(string text)
        {
            string normalised = (text ?? string.Empty).Replace("\r\n", "\n");
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
            return FILE_PREFIX + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>The hash part of a file key (null if it isn't one).</summary>
        public static string HashOf(string key) =>
            key != null && key.StartsWith(FILE_PREFIX, StringComparison.Ordinal) && IsSafeHash(key[FILE_PREFIX.Length..]) ? key[FILE_PREFIX.Length..] : null;

        /// <summary>True for 16 lower-case hex characters (safe as a file name).</summary>
        public static bool IsSafeHash(string hash)
        {
            if (hash == null || hash.Length != 16) { return false; }
            foreach (char c in hash) { if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) { return false; } }
            return true;
        }

        #endregion

        #region Built-ins

        /// <summary>Key of the narrow downlight.</summary>
        public const string DOWNLIGHT_NARROW = BUILT_IN_PREFIX + "downlight-narrow";

        /// <summary>Key of the medium downlight.</summary>
        public const string DOWNLIGHT_MEDIUM = BUILT_IN_PREFIX + "downlight-medium";

        /// <summary>Key of the wide flood.</summary>
        public const string FLOOD = BUILT_IN_PREFIX + "flood";

        /// <summary>Key of the wall washer.</summary>
        public const string WALL_WASHER = BUILT_IN_PREFIX + "wall-washer";

        /// <summary>Key of the pendant.</summary>
        public const string PENDANT = BUILT_IN_PREFIX + "pendant";

        private static LightProfile[] _builtIns;

        /// <summary>
        /// The built-ins, in the order they're offered (made once):
        /// <list type="bullet">
        /// <item>Downlight, narrow: a 25° beam (cos²⁹, half intensity at 12.5°) with a little spill to 60°.</item>
        /// <item>Downlight, medium: a 40° beam (cos¹¹) with spill.</item>
        /// <item>Wide flood: a soft batwing to about 120°, little above the horizon.</item>
        /// <item>Wall washer: down and strongly towards C 0° (aim C 0° at the wall), peaking 35° off nadir.</item>
        /// <item>Pendant: 65 % down (a broad cone), 25 % up, the rest sideways (an opal diffuser).</item>
        /// </list>
        /// </summary>
        public static LightProfile[] BuiltIns => _builtIns ??= new[]
        {
            LightProfile.FromFunction(DOWNLIGHT_NARROW, "Downlight, narrow (25°)", (g, c) => Down(g, 29f) + 0.04f * Down(g, 2f) * Fade(g, 60f)),
            LightProfile.FromFunction(DOWNLIGHT_MEDIUM, "Downlight, medium (40°)", (g, c) => Down(g, 11f) + 0.05f * Down(g, 1.5f) * Fade(g, 70f)),
            LightProfile.FromFunction(FLOOD, "Wide flood (120°)", (g, c) => g < 90f ? (0.75f + 0.45f * MathF.Sin(Rad(g)) * MathF.Sin(Rad(g))) * Fade(g, 82f) : 0.02f),
            LightProfile.FromFunction(WALL_WASHER, "Wall washer (aim C 0° at the wall)", (g, c) =>
            {
                float toward = MathF.Pow(0.5f + 0.5f * MathF.Cos(Rad(c)), 3f);
                float kick = MathF.Exp(-MathF.Pow((g - 35f) / 18f, 2f)) * Fade(g, 85f);
                return 0.25f * Down(g, 2f) + toward * kick;
            }),
            LightProfile.FromFunction(PENDANT, "Pendant (up / down)", (g, c) =>
            {
                float cos = MathF.Cos(Rad(g));
                return cos > 0f ? 0.18f + 0.82f * MathF.Pow(cos, 1.6f) : 0.18f + 0.45f * MathF.Pow(-cos, 1.6f);
            })
        };

        /// <summary>The built-in with a key, or null.</summary>
        public static LightProfile FindBuiltIn(string key)
        {
            foreach (LightProfile p in BuiltIns) { if (p.Key == key) { return p; } }
            return null;
        }

        #endregion

        #region Generated (Round Y-C1)

        /// <summary>Key of Revit's hemispherical distribution: an even (Lambertian, cosine) half sphere out of the fixture.</summary>
        public const string HEMISPHERICAL = GENERATED_PREFIX + "hemi";

        private const string SPOT_PREFIX = GENERATED_PREFIX + "spot:";

        /// <summary>
        /// The key of a Revit spot light's distribution: <c>gen:spot:&lt;beam&gt;:&lt;field&gt;</c>, whole degrees (full
        /// angles; the beam at least 1°, the field at least the beam, both at most 179°), so equal spots share a profile.
        /// </summary>
        /// <param name="beamDegrees">Revit's Spot Beam Angle: full intensity inside it.</param>
        /// <param name="fieldDegrees">Revit's Spot Field Angle: no light outside it.</param>
        /// <returns>The key, or null when the angles aren't usable.</returns>
        public static string SpotKey(double beamDegrees, double fieldDegrees)
        {
            if (!double.IsFinite(beamDegrees) || !double.IsFinite(fieldDegrees) || fieldDegrees <= 0) { return null; }
            int field = (int)Math.Clamp(Math.Round(fieldDegrees), 1, 179);
            int beam = (int)Math.Clamp(Math.Round(beamDegrees), 1, field);
            return string.Create(CultureInfo.InvariantCulture, $"{SPOT_PREFIX}{beam}:{field}");
        }

        /// <summary>True when a key is a generated profile this version can make.</summary>
        public static bool IsGeneratedKey(string key) => key == HEMISPHERICAL || TryParseSpot(key, out _, out _);

        private static bool TryParseSpot(string key, out int beam, out int field)
        {
            beam = field = 0;
            if (key == null || !key.StartsWith(SPOT_PREFIX, StringComparison.Ordinal)) { return false; }
            string[] parts = key[SPOT_PREFIX.Length..].Split(':');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out beam)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out field)
                && beam >= 1 && field >= beam && field <= 179;
        }

        /// <summary>
        /// A generated profile from its key (made each call: cache it), or null when the key isn't one:
        /// <list type="bullet">
        /// <item>Spot: full intensity inside half the beam angle, a smooth fall to nothing at half the field angle (at
        /// least a 2° fall, so a beam equal to its field still has a soft edge), nothing beyond.</item>
        /// <item>Hemispherical: cos γ out of the fixture, nothing behind it.</item>
        /// </list>
        /// </summary>
        public static LightProfile FromGeneratedKey(string key)
        {
            if (key == HEMISPHERICAL)
            {
                return LightProfile.FromFunction(key, "Revit hemispherical", (g, c) => g < 90f ? MathF.Cos(Rad(g)) : 0f);
            }
            if (!TryParseSpot(key, out int beam, out int field)) { return null; }
            float inner = beam * 0.5f, outer = MathF.Max(field * 0.5f, inner + 2f);
            string name = beam == field
                ? string.Create(CultureInfo.InvariantCulture, $"Revit spot ({beam}°)")
                : string.Create(CultureInfo.InvariantCulture, $"Revit spot ({beam}° beam, {field}° field)");
            return LightProfile.FromFunction(key, name, (g, c) =>
            {
                if (g <= inner) { return 1f; }
                if (g >= outer) { return 0f; }
                float t = (outer - g) / (outer - inner);
                return t * t * (3f - 2f * t);
            });
        }

        private static float Rad(float degrees) => degrees * MathF.PI / 180f;
        private static float Down(float g, float power) => g < 90f ? MathF.Pow(MathF.Cos(Rad(g)), power) : 0f;
        private static float Fade(float g, float end) => Math.Clamp((end - g) / 10f, 0f, 1f);

        #endregion

        #region Per-light code

        /// <summary>Most profiles the renderer holds at once (the shader block's size; 5 bits in the code).</summary>
        public const int MAX_GPU_PROFILES = 24;

        /// <summary>
        /// Packs a profile slot and the fixture's orientation into the negative float the shaders read in place of the
        /// downward share (<c>-(code + 1)</c>; 24 bits, exact in a float): slot (5 bits) · aim octahedral u, v (6 bits each)
        /// · turn about the aim (7 bits). The turn is measured from a reference axis built from the <em>quantised</em> aim
        /// (as the shader rebuilds it): world up projected across the aim, or world X when the aim is near vertical.
        /// </summary>
        /// <param name="slot">The profile's slot (0 – <see cref="MAX_GPU_PROFILES"/> − 1).</param>
        /// <param name="aim">Gamma 0 (out of the fixture), world.</param>
        /// <param name="across">C 0°, world (any length; projected across the aim).</param>
        public static float Encode(int slot, Vector3 aim, Vector3 across)
        {
            slot = Math.Clamp(slot, 0, 31);
            if (!(aim.LengthSquared() > 1e-10f) || !float.IsFinite(aim.X + aim.Y + aim.Z)) { aim = -Vector3.UnitZ; }
            aim = Vector3.Normalize(aim);
            Vector2 o = OctEncode(aim);
            int u = Math.Clamp((int)MathF.Round((o.X * 0.5f + 0.5f) * 63f), 0, 63);
            int v = Math.Clamp((int)MathF.Round((o.Y * 0.5f + 0.5f) * 63f), 0, 63);
            Vector3 n = OctDecode(new Vector2(u / 63f * 2f - 1f, v / 63f * 2f - 1f));
            Frame(n, out Vector3 x0, out Vector3 y0);
            float turn = MathF.Atan2(Vector3.Dot(across, y0), Vector3.Dot(across, x0));
            if (!float.IsFinite(turn)) { turn = 0f; }
            if (turn < 0f) { turn += 2f * MathF.PI; }
            int t = (int)MathF.Round(turn / (2f * MathF.PI) * 128f) & 127;
            int code = (slot << 19) | (u << 13) | (v << 7) | t;
            return -(code + 1f);
        }

        /// <summary>
        /// Unpacks <see cref="Encode"/> as the shader does: the slot, the (quantised) aim and C 0° axis.
        /// </summary>
        public static (int Slot, Vector3 Aim, Vector3 Across) Decode(float value)
        {
            int code = (int)MathF.Round(-value - 1f);
            int slot = code >> 19, u = (code >> 13) & 63, v = (code >> 7) & 63, t = code & 127;
            Vector3 n = OctDecode(new Vector2(u / 63f * 2f - 1f, v / 63f * 2f - 1f));
            Frame(n, out Vector3 x0, out Vector3 y0);
            float turn = t / 128f * 2f * MathF.PI;
            return (slot, n, MathF.Cos(turn) * x0 + MathF.Sin(turn) * y0);
        }

        /// <summary>The reference axes across an aim (keep in step with profileFrame in the shader).</summary>
        public static void Frame(Vector3 n, out Vector3 x0, out Vector3 y0)
        {
            Vector3 helper = MathF.Abs(n.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX;
            x0 = Vector3.Normalize(helper - n * Vector3.Dot(helper, n));
            y0 = Vector3.Cross(x0, n);
        }

        /// <summary>Gamma (°) and C (°, 0–360) of a direction leaving the light, in the light's frame.</summary>
        public static (float Gamma, float C) AnglesOf(Vector3 direction, Vector3 aim, Vector3 across)
        {
            Vector3 d = Vector3.Normalize(direction);
            Vector3 c90 = Vector3.Cross(across, aim);
            float gamma = MathF.Acos(Math.Clamp(Vector3.Dot(d, aim), -1f, 1f)) * 180f / MathF.PI;
            float c = MathF.Atan2(Vector3.Dot(d, c90), Vector3.Dot(d, across)) * 180f / MathF.PI;
            return (gamma, c < 0f ? c + 360f : c);
        }

        private static Vector2 OctEncode(Vector3 n)
        {
            float s = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
            var p = new Vector2(n.X / s, n.Y / s);
            if (n.Z < 0f)
            {
                p = new Vector2((1f - MathF.Abs(p.Y)) * SignNotZero(p.X), (1f - MathF.Abs(p.X)) * SignNotZero(p.Y));
            }
            return p;
        }

        private static Vector3 OctDecode(Vector2 o)
        {
            var n = new Vector3(o.X, o.Y, 1f - MathF.Abs(o.X) - MathF.Abs(o.Y));
            if (n.Z < 0f)
            {
                n = new Vector3((1f - MathF.Abs(o.Y)) * SignNotZero(o.X), (1f - MathF.Abs(o.X)) * SignNotZero(o.Y), n.Z);
            }
            return Vector3.Normalize(n);
        }

        private static float SignNotZero(float v) => v >= 0f ? 1f : -1f;

        #endregion
    }
}
