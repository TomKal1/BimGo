using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Round Y-A: the Measure gun's measurements (<c>measurements.json</c> in a .bimgo or the live model folder), kept
    /// between sessions. Points are Revit internal coordinates in metres (like bookmarks), so they survive a snapshot
    /// with another origin.
    /// </summary>
    public sealed class MeasurementDocument
    {
        /// <summary>Most measurements kept.</summary>
        public const int MAX_MEASUREMENTS = 2000;

        /// <summary>Layout version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>The model title when last saved (information only).</summary>
        public string Model { get; set; }

        /// <summary>True while the saved measurements are hidden (the Measure gun's show / hide).</summary>
        public bool Hidden { get; set; }

        /// <summary>The measurements, oldest first.</summary>
        public List<MeasurementRecord> Measurements { get; set; } = new();

        /// <summary>True when there is nothing to save.</summary>
        [JsonIgnore]
        public bool IsEmpty => (Measurements == null || Measurements.Count == 0) && !Hidden;

        /// <summary>
        /// Drops damaged entries (unknown type, wrong point count, non-finite values) and anything past the limit.
        /// </summary>
        public MeasurementDocument Clean()
        {
            Measurements ??= new List<MeasurementRecord>();
            Measurements.RemoveAll(m => m == null || !m.IsValid());
            if (Measurements.Count > MAX_MEASUREMENTS) { Measurements.RemoveRange(MAX_MEASUREMENTS, Measurements.Count - MAX_MEASUREMENTS); }
            return this;
        }
    }

    /// <summary>
    /// Round Y-A: one measurement.
    /// </summary>
    public sealed class MeasurementRecord
    {
        /// <summary>Most corners in an area outline.</summary>
        public const int MAX_POINTS = 64;

        public const string DISTANCE = "distance";
        public const string AREA = "area";
        public const string ANGLE = "angle";
        public const string CLEARANCE = "clearance";

        /// <summary><c>distance</c>, <c>area</c>, <c>angle</c> or <c>clearance</c>.</summary>
        public string Type { get; set; } = DISTANCE;

        /// <summary>
        /// The points as x, y, z triples (Revit internal metres). Distance: start, end. Area: the corners. Angle: arm,
        /// vertex, arm. Clearance: far end (above / across), the measured point, near end (below).
        /// </summary>
        public List<double> Points { get; set; } = new();

        /// <summary>Area: the outline's plane normal. Clearance: the axis measured along (zero or straight up: vertical).</summary>
        public double NormalX { get; set; }
        public double NormalY { get; set; }
        public double NormalZ { get; set; }

        /// <summary>Distance: metres. Area: m². Angle: degrees. Clearance: metres below (vertical) or 0 (across).</summary>
        public double Value { get; set; }

        /// <summary>Area: perimeter (m). Clearance: metres above, or across.</summary>
        public double Value2 { get; set; }

        /// <summary>Clearance: a surface was found above / across and below.</summary>
        public bool Above { get; set; }
        public bool Below { get; set; }

        /// <summary>When it was made.</summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>The number of points this type needs (area: 3 to 64).</summary>
        public bool IsValid()
        {
            if (Points == null || Points.Count % 3 != 0) { return false; }
            int count = Points.Count / 3;
            bool countOk = Type switch
            {
                DISTANCE => count == 2,
                ANGLE => count == 3,
                CLEARANCE => count == 3,
                AREA => count >= 3 && count <= MAX_POINTS,
                _ => false
            };
            if (!countOk) { return false; }
            foreach (double v in Points) { if (!double.IsFinite(v)) { return false; } }
            return double.IsFinite(NormalX) && double.IsFinite(NormalY) && double.IsFinite(NormalZ) && double.IsFinite(Value) && double.IsFinite(Value2);
        }
    }

    /// <summary>
    /// Round Y-A: where a live session keeps its measurements: <c>measurements.json</c> in the model's BimGo folder (or
    /// &lt;model&gt;.bimgo-measurements.json beside an older snapshot's comments), mirrored beside the model when sharing
    /// is on. Never throws.
    /// </summary>
    public static class MeasurementFiles
    {
        /// <summary>The file that goes with a comments sidecar, or null.</summary>
        public static string SidecarFor(string commentsSidecarPath) => SidecarJson.BesideComments(commentsSidecarPath, BimGoFormat.MEASUREMENTS_SIDECAR_SUFFIX);

        /// <summary>Reads the file (null if missing or unreadable).</summary>
        public static MeasurementDocument Read(string path, out string error) => SidecarJson.Read<MeasurementDocument>(path, "Measurements", out error)?.Clean();

        /// <summary>Writes the file atomically.</summary>
        public static bool Write(string path, MeasurementDocument document, out string error) => SidecarJson.Write(path, document, "Measurements", out error);

        /// <summary>
        /// The measurements as CSV (metres, square metres and degrees; points in Revit internal metres): a header, then
        /// one row per measurement.
        /// </summary>
        public static string ToCsv(MeasurementDocument document, string model)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var csv = new StringBuilder();
            csv.Append("BimGo measurements,").Append(Quote(model)).Append('\n');
            csv.Append("#,Type,Value,Unit,Value 2,Unit 2,Made (UTC),Points (Revit internal X Y Z; m)\n");
            int n = 0;
            foreach (MeasurementRecord m in document?.Measurements ?? new List<MeasurementRecord>())
            {
                if (m == null || !m.IsValid()) { continue; }
                n++;
                bool across = m.Type == MeasurementRecord.CLEARANCE && Math.Abs(m.NormalZ) < 0.7 && (m.NormalX != 0 || m.NormalY != 0);
                (string unit, string unit2, double value, double value2) = m.Type switch
                {
                    MeasurementRecord.AREA => ("m2", "m (perimeter)", m.Value, m.Value2),
                    MeasurementRecord.ANGLE => ("deg", string.Empty, m.Value, double.NaN),
                    MeasurementRecord.CLEARANCE when across => ("m (across)", string.Empty, m.Value2, double.NaN),
                    MeasurementRecord.CLEARANCE => ("m (below)", "m (above)", m.Below ? m.Value : double.NaN, m.Above ? m.Value2 : double.NaN),
                    _ => ("m", string.Empty, m.Value, double.NaN)
                };
                csv.Append(n.ToString(inv)).Append(',').Append(m.Type).Append(',')
                    .Append(Number(value)).Append(',').Append(unit).Append(',')
                    .Append(Number(value2)).Append(',').Append(unit2).Append(',')
                    .Append(m.CreatedUtc == default ? string.Empty : m.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(',');
                var points = new StringBuilder();
                for (int i = 0; i + 2 < m.Points.Count; i += 3)
                {
                    if (i > 0) { points.Append("; "); }
                    points.Append(m.Points[i].ToString("0.000", inv)).Append(' ').Append(m.Points[i + 1].ToString("0.000", inv)).Append(' ').Append(m.Points[i + 2].ToString("0.000", inv));
                }
                csv.Append(Quote(points.ToString())).Append('\n');
            }
            return csv.ToString();

            string Number(double v) => double.IsFinite(v) ? v.ToString("0.###", inv) : string.Empty;
        }

        private static string Quote(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
