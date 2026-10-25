using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WWTools.Lighting.Engine
{
    /// <summary>IES photometric types (LM-63 field 6).</summary>
    public enum PhotometricType
    {
        TypeC = 1,
        TypeB = 2,
        TypeA = 3,
    }

    /// <summary>
    /// A luminaire's candela distribution, read from an IES LM-63 file (1986, 1991, 1995, 2002 and
    /// 2019 layouts) or built from a simple analytic shape. Candela values are stored with the
    /// candela multiplier and ballast factors applied, so <see cref="Candela"/> returns final
    /// intensities. Luminous-opening dimensions are converted to feet.
    /// </summary>
    public sealed class IesPhotometry
    {
        private double[] _vertical = Array.Empty<double>();
        private double[] _horizontal = Array.Empty<double>();
        private double[][] _candela = Array.Empty<double[]>();
        private HorizontalSymmetry _symmetry;

        private enum HorizontalSymmetry
        {
            Axial,
            Quadrant,
            Bilateral0To180,
            Bilateral90To270,
            Full,
            Partial,
        }

        public PhotometricType Type { get; private set; } = PhotometricType.TypeC;

        /// <summary>Header keywords such as TEST, MANUFAC, LUMCAT, LUMINAIRE, LAMP (upper case, no brackets).</summary>
        public Dictionary<string, string> Keywords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int LampCount { get; private set; } = 1;

        /// <summary>Rated lumens per lamp, or -1 for absolute photometry (typical for LED).</summary>
        public double LumensPerLamp { get; private set; } = -1;

        /// <summary>Input watts from the file, or 0 when the file does not say.</summary>
        public double InputWatts { get; private set; }

        /// <summary>Luminous opening width in feet (along the 90°–270° axis). Negative means round.</summary>
        public double OpeningWidth { get; private set; }

        /// <summary>Luminous opening length in feet (along the 0°–180° axis).</summary>
        public double OpeningLength { get; private set; }

        /// <summary>Luminous opening height in feet.</summary>
        public double OpeningHeight { get; private set; }

        /// <summary>True when the file carried a lamp tilt table, which this engine ignores.</summary>
        public bool HadTiltData { get; private set; }

        /// <summary>The flux found by integrating the candela table over the sphere (lm).</summary>
        public double ComputedLumens { get; private set; }

        public double MaxCandela { get; private set; }

        /// <summary>True when the distribution was synthesised instead of read from a file.</summary>
        public bool IsEstimated { get; private set; }

        /// <summary>The file's text as parsed (null for synthesised distributions), so it can be stored.</summary>
        public string? RawText { get; private set; }

        /// <summary>Short description for reports: catalogue number, luminaire, or the source.</summary>
        public string Description { get; private set; } = string.Empty;

        public IReadOnlyList<double> VerticalAngles => _vertical;

        public IReadOnlyList<double> HorizontalAngles => _horizontal;

        /// <summary>The largest luminous-opening dimension in feet.</summary>
        public double MaxOpeningDimension =>
            Math.Max(Math.Abs(OpeningWidth), Math.Max(Math.Abs(OpeningLength), Math.Abs(OpeningHeight)));

        public static IesPhotometry Load(string path)
        {
            string text = File.ReadAllText(path, Encoding.GetEncoding("ISO-8859-1"));
            IesPhotometry result = Parse(text);
            if (string.IsNullOrEmpty(result.Description))
                result.Description = Path.GetFileName(path);
            return result;
        }

        /// <summary>Parses the text of an IES file. Throws <see cref="FormatException"/> when it is not one.</summary>
        public static IesPhotometry Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("The photometric file is empty.");

            var result = new IesPhotometry { RawText = text };
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            int tiltLine = -1;
            string lastKeyword = string.Empty;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("TILT", StringComparison.OrdinalIgnoreCase))
                {
                    tiltLine = i;
                    break;
                }

                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = line.IndexOf(']');
                    if (close > 1)
                    {
                        string key = line.Substring(1, close - 1).Trim();
                        string value = line.Substring(close + 1).Trim();
                        if (key.Equals("MORE", StringComparison.OrdinalIgnoreCase) && lastKeyword.Length > 0)
                        {
                            result.Keywords[lastKeyword] = result.Keywords[lastKeyword] + " " + value;
                        }
                        else
                        {
                            result.Keywords[key] = value;
                            lastKeyword = key;
                        }
                    }
                }
            }

            if (tiltLine < 0)
                throw new FormatException("No TILT line was found, so this is not an IES LM-63 file.");

            string tilt = lines[tiltLine].Trim();
            int eq = tilt.IndexOf('=');
            string tiltValue = eq >= 0 ? tilt.Substring(eq + 1).Trim() : "NONE";

            var tokens = new List<string>();
            for (int i = tiltLine + 1; i < lines.Length; i++)
            {
                foreach (string token in lines[i].Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    tokens.Add(token);
            }

            int t = 0;
            double Next()
            {
                if (t >= tokens.Count)
                    throw new FormatException("The photometric file ends before its candela table is complete.");
                string token = tokens[t++];
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw new FormatException($"Unexpected value '{token}' in the photometric data.");
                return value;
            }

            if (tiltValue.Equals("INCLUDE", StringComparison.OrdinalIgnoreCase))
            {
                result.HadTiltData = true;
                Next(); // lamp-to-luminaire geometry
                int pairs = (int)Next();
                for (int i = 0; i < pairs * 2; i++) Next();
            }
            else if (!tiltValue.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                result.HadTiltData = true;
            }

            result.LampCount = Math.Max(1, (int)Next());
            result.LumensPerLamp = Next();
            double multiplier = Next();
            int verticalCount = (int)Next();
            int horizontalCount = (int)Next();
            int type = (int)Next();
            int units = (int)Next();
            double width = Next();
            double length = Next();
            double height = Next();

            double ballastFactor = Next();
            double ballastLampFactor = Next();
            result.InputWatts = Next();

            if (verticalCount < 1 || horizontalCount < 1)
                throw new FormatException("The candela table has no angles.");
            if (type < 1 || type > 3)
                throw new FormatException($"Unknown photometric type {type}.");

            double toFeet = units == 2 ? 1.0 / 0.3048 : 1.0;
            result.OpeningWidth = width * toFeet;
            result.OpeningLength = length * toFeet;
            result.OpeningHeight = height * toFeet;
            result.Type = (PhotometricType)type;

            if (ballastFactor <= 0) ballastFactor = 1.0;
            if (ballastLampFactor <= 0) ballastLampFactor = 1.0;
            if (multiplier <= 0) multiplier = 1.0;
            double scale = multiplier * ballastFactor * ballastLampFactor;

            var vertical = new double[verticalCount];
            for (int i = 0; i < verticalCount; i++) vertical[i] = Next();
            var horizontal = new double[horizontalCount];
            for (int i = 0; i < horizontalCount; i++) horizontal[i] = Next();

            var candela = new double[horizontalCount][];
            for (int h = 0; h < horizontalCount; h++)
            {
                candela[h] = new double[verticalCount];
                for (int v = 0; v < verticalCount; v++)
                    candela[h][v] = Math.Max(0.0, Next() * scale);
            }

            result.SetTable(vertical, horizontal, candela);

            if (result.Keywords.TryGetValue("LUMCAT", out string? cat) && cat.Length > 0)
                result.Description = cat;
            else if (result.Keywords.TryGetValue("LUMINAIRE", out string? lum) && lum.Length > 0)
                result.Description = lum;

            return result;
        }

        /// <summary>
        /// Builds a rotationally symmetric distribution from an intensity shape, scaled so it emits
        /// <paramref name="lumens"/>. Used for fixtures without an IES file.
        /// </summary>
        /// <param name="shape">Relative intensity for an angle from nadir in degrees (0–180).</param>
        public static IesPhotometry FromShape(Func<double, double> shape, double lumens, string description)
        {
            var vertical = new double[181];
            var relative = new double[181];
            for (int i = 0; i <= 180; i++)
            {
                vertical[i] = i;
                relative[i] = Math.Max(0.0, shape(i));
            }

            var result = new IesPhotometry { IsEstimated = true, Description = description };
            result.SetTable(vertical, new[] { 0.0 }, new[] { relative });
            double flux = result.ComputedLumens;
            double factor = flux > 0 ? lumens / flux : 0.0;
            for (int i = 0; i <= 180; i++) relative[i] *= factor;
            result.SetTable(vertical, new[] { 0.0 }, new[] { relative });
            return result;
        }

        /// <summary>An even sphere of light (bare lamp).</summary>
        public static IesPhotometry Isotropic(double lumens, string description) =>
            FromShape(_ => 1.0, lumens, description);

        /// <summary>A cosine (Lambertian) downlight: all light below the horizontal.</summary>
        public static IesPhotometry Lambertian(double lumens, string description) =>
            FromShape(a => a < 90 ? Math.Cos(a * Math.PI / 180.0) : 0.0, lumens, description);

        /// <summary>A spot: full intensity inside the beam angle, fading to zero at the field angle.</summary>
        public static IesPhotometry Spot(double lumens, double beamAngle, double fieldAngle, string description)
        {
            double beamHalf = Math.Max(1.0, beamAngle / 2.0);
            double fieldHalf = Math.Max(beamHalf + 1.0, fieldAngle / 2.0);
            return FromShape(a =>
            {
                if (a <= beamHalf) return 1.0;
                if (a >= fieldHalf) return 0.0;
                double f = (a - beamHalf) / (fieldHalf - beamHalf);
                return 0.5 * (1.0 + Math.Cos(f * Math.PI));
            }, lumens, description);
        }

        private void SetTable(double[] vertical, double[] horizontal, double[][] candela)
        {
            _vertical = vertical;
            _horizontal = horizontal;
            _candela = candela;
            _symmetry = DetectSymmetry(horizontal, Type);
            MaxCandela = candela.SelectMany(row => row).DefaultIfEmpty(0).Max();
            ComputedLumens = IntegrateLumens();
        }

        private static HorizontalSymmetry DetectSymmetry(double[] h, PhotometricType type)
        {
            if (h.Length == 1) return HorizontalSymmetry.Axial;
            double first = h[0];
            double last = h[h.Length - 1];
            if (type != PhotometricType.TypeC) return HorizontalSymmetry.Partial;
            if (Near(first, 0) && Near(last, 90)) return HorizontalSymmetry.Quadrant;
            if (Near(first, 0) && Near(last, 180)) return HorizontalSymmetry.Bilateral0To180;
            if (Near(first, 90) && Near(last, 270)) return HorizontalSymmetry.Bilateral90To270;
            if (Near(last - first, 360)) return HorizontalSymmetry.Full;
            return HorizontalSymmetry.Partial;
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

        /// <summary>
        /// Intensity (cd) toward a direction in the luminaire's photometric frame. For Type C,
        /// <paramref name="thetaDeg"/> is measured from nadir (0–180) and <paramref name="psiDeg"/> is
        /// the horizontal angle (0° along the luminaire's length, counter-clockwise seen from above).
        /// </summary>
        public double Candela(double thetaDeg, double psiDeg)
        {
            if (_candela.Length == 0) return 0.0;

            if (_symmetry == HorizontalSymmetry.Axial)
                return InterpolateVertical(_candela[0], thetaDeg);

            double psi = MapHorizontal(psiDeg);
            int count = _horizontal.Length;

            if (psi <= _horizontal[0])
                return InterpolateVertical(_candela[0], thetaDeg);

            if (psi >= _horizontal[count - 1])
                return InterpolateVertical(_candela[count - 1], thetaDeg);

            int hi = UpperIndex(_horizontal, psi);
            int lo = hi - 1;
            double span = _horizontal[hi] - _horizontal[lo];
            double f = span > 1e-9 ? (psi - _horizontal[lo]) / span : 0.0;
            double a = InterpolateVertical(_candela[lo], thetaDeg);
            double b = InterpolateVertical(_candela[hi], thetaDeg);
            return a + (b - a) * f;
        }

        /// <summary>
        /// Intensity (cd) toward a unit direction expressed in the luminaire frame: x along the
        /// 0° axis (length), y along the 90° axis, z toward nadir (the aiming direction).
        /// </summary>
        public double CandelaToward(double x, double y, double z)
        {
            switch (Type)
            {
                case PhotometricType.TypeB:
                {
                    // Polar axis = x. Horizontal angle rotates about it from the aim axis (z);
                    // vertical angle is the elevation toward the polar axis.
                    double h = Math.Atan2(y, z) * 180.0 / Math.PI;
                    double v = Math.Asin(Clamp(x, -1, 1)) * 180.0 / Math.PI;
                    return CandelaTypeB(h, v);
                }
                case PhotometricType.TypeA:
                {
                    double h = Math.Atan2(x, z) * 180.0 / Math.PI;
                    double v = Math.Asin(Clamp(y, -1, 1)) * 180.0 / Math.PI;
                    return CandelaTypeB(h, v);
                }
                default:
                {
                    double theta = Math.Acos(Clamp(z, -1, 1)) * 180.0 / Math.PI;
                    double psi = Math.Atan2(y, x) * 180.0 / Math.PI;
                    if (psi < 0) psi += 360.0;
                    return Candela(theta, psi);
                }
            }
        }

        private double CandelaTypeB(double h, double v)
        {
            // Type B/A tables often cover only one side (0..90) when symmetric.
            if (_horizontal.Length > 0 && _horizontal[0] >= -0.01) h = Math.Abs(h);
            if (_vertical.Length > 0 && _vertical[0] >= -0.01) v = Math.Abs(v);
            int count = _horizontal.Length;
            if (count == 1) return InterpolateVertical(_candela[0], v);
            if (h <= _horizontal[0]) return h < _horizontal[0] - 0.01 ? 0.0 : InterpolateVertical(_candela[0], v);
            if (h >= _horizontal[count - 1]) return h > _horizontal[count - 1] + 0.01 ? 0.0 : InterpolateVertical(_candela[count - 1], v);
            int hi = UpperIndex(_horizontal, h);
            int lo = hi - 1;
            double f = (h - _horizontal[lo]) / Math.Max(1e-9, _horizontal[hi] - _horizontal[lo]);
            double a = InterpolateVertical(_candela[lo], v);
            double b = InterpolateVertical(_candela[hi], v);
            return a + (b - a) * f;
        }

        private double MapHorizontal(double psi)
        {
            psi %= 360.0;
            if (psi < 0) psi += 360.0;

            switch (_symmetry)
            {
                case HorizontalSymmetry.Quadrant:
                    if (psi > 270) return 360 - psi;
                    if (psi > 180) return psi - 180;
                    if (psi > 90) return 180 - psi;
                    return psi;
                case HorizontalSymmetry.Bilateral0To180:
                    return psi > 180 ? 360 - psi : psi;
                case HorizontalSymmetry.Bilateral90To270:
                    // Mirror about the 90-270 plane: angles outside 90..270 fold to 180 - psi.
                    if (psi < 90) return 180 - psi;
                    if (psi > 270) return 540 - psi;
                    return psi;
                case HorizontalSymmetry.Full:
                {
                    double first = _horizontal[0];
                    while (psi < first) psi += 360;
                    while (psi > first + 360) psi -= 360;
                    return psi;
                }
                default:
                    return psi;
            }
        }

        private double InterpolateVertical(double[] row, double theta)
        {
            int count = _vertical.Length;
            if (count == 1) return Near(theta, _vertical[0]) ? row[0] : 0.0;
            if (theta < _vertical[0] - 1e-6 || theta > _vertical[count - 1] + 1e-6) return 0.0;
            if (theta <= _vertical[0]) return row[0];
            if (theta >= _vertical[count - 1]) return row[count - 1];
            int hi = UpperIndex(_vertical, theta);
            int lo = hi - 1;
            double span = _vertical[hi] - _vertical[lo];
            double f = span > 1e-9 ? (theta - _vertical[lo]) / span : 0.0;
            return row[lo] + (row[hi] - row[lo]) * f;
        }

        /// <summary>First index whose value is greater than <paramref name="x"/> (1..count-1).</summary>
        private static int UpperIndex(double[] values, double x)
        {
            int lo = 0;
            int hi = values.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (values[mid] > x) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        private double IntegrateLumens()
        {
            const int thetaSteps = 360;
            const int psiSteps = 144;
            double total = 0.0;
            double dTheta = Math.PI / thetaSteps;
            double dPsi = 2 * Math.PI / psiSteps;
            for (int i = 0; i < thetaSteps; i++)
            {
                double theta0 = i * dTheta;
                double theta1 = theta0 + dTheta;
                double thetaMid = (theta0 + theta1) / 2;
                double band = (Math.Cos(theta0) - Math.Cos(theta1)) * dPsi;
                for (int j = 0; j < psiSteps; j++)
                {
                    double psi = (j + 0.5) * dPsi;
                    double x = Math.Sin(thetaMid) * Math.Cos(psi);
                    double y = Math.Sin(thetaMid) * Math.Sin(psi);
                    double z = Math.Cos(thetaMid);
                    total += CandelaToward(x, y, z) * band;
                }
            }
            return total;
        }

        internal static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    }
}
