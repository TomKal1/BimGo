using System.Globalization;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// The unit system readouts, inputs and catalogs use. Everything is stored and computed in SI; this only decides
    /// how numbers come in and go out. (Not "UnitSystem": Revit's DB namespace, global in BimGo.Revit, has one.)
    /// </summary>
    public enum MeasurementSystem
    {
        /// <summary>Metric: L/s, m/s, Pa, m, mm, °C.</summary>
        SI,

        /// <summary>Inch-pound: CFM, fpm, in wg, ft, in, °F.</summary>
        IP
    }

    /// <summary>
    /// Unit conversions and formatting for the airflow tools (and later the general metric / imperial setting).
    /// Values are SI inside BimGo: flow L/s, speed m/s, pressure Pa, lengths and sizes m, temperature °C.
    /// <list type="bullet">
    /// <item>Catalog input: lengths (throws) are ft or m; sizes (necks, faces) are in or <b>mm</b>.</item>
    /// <item>Envelope speeds follow the system's usual set (150 / 100 / 50 fpm, 0.75 / 0.5 / 0.25 m/s), not a strict
    /// conversion of each other.</item>
    /// </list>
    /// </summary>
    public static class UnitConvert
    {
        #region Factors (to SI)

        /// <summary>1 CFM in L/s.</summary>
        public const float CFM_TO_LPS = 0.4719474f;

        /// <summary>1 fpm in m/s.</summary>
        public const float FPM_TO_MPS = 0.00508f;

        /// <summary>1 in wg (inch of water column, 4 °C) in Pa.</summary>
        public const float INWG_TO_PA = 249.089f;

        /// <summary>1 ft in m.</summary>
        public const float FT_TO_M = 0.3048f;

        /// <summary>1 in in m.</summary>
        public const float IN_TO_M = 0.0254f;

        /// <summary>1 mm in m.</summary>
        public const float MM_TO_M = 0.001f;

        #endregion

        #region Envelope speeds

        // m/s, fastest first
        private static readonly float[] IP_ENVELOPE = { 150f * FPM_TO_MPS, 100f * FPM_TO_MPS, 50f * FPM_TO_MPS };
        private static readonly float[] SI_ENVELOPE = { 0.75f, 0.5f, 0.25f };

        /// <summary>
        /// The throw envelope speeds of a system (m/s, fastest first): 150 / 100 / 50 fpm or 0.75 / 0.5 / 0.25 m/s.
        /// </summary>
        public static ReadOnlySpan<float> EnvelopeSpeeds(MeasurementSystem system) => system == MeasurementSystem.IP ? IP_ENVELOPE : SI_ENVELOPE;

        #endregion

        #region Conversions

        /// <summary>CFM or L/s to L/s.</summary>
        public static float FlowToSi(float value, MeasurementSystem from) => from == MeasurementSystem.IP ? value * CFM_TO_LPS : value;

        /// <summary>L/s to CFM or L/s.</summary>
        public static float FlowFromSi(float lps, MeasurementSystem to) => to == MeasurementSystem.IP ? lps / CFM_TO_LPS : lps;

        /// <summary>fpm or m/s to m/s.</summary>
        public static float SpeedToSi(float value, MeasurementSystem from) => from == MeasurementSystem.IP ? value * FPM_TO_MPS : value;

        /// <summary>m/s to fpm or m/s.</summary>
        public static float SpeedFromSi(float mps, MeasurementSystem to) => to == MeasurementSystem.IP ? mps / FPM_TO_MPS : mps;

        /// <summary>in wg or Pa to Pa.</summary>
        public static float PressureToSi(float value, MeasurementSystem from) => from == MeasurementSystem.IP ? value * INWG_TO_PA : value;

        /// <summary>Pa to in wg or Pa.</summary>
        public static float PressureFromSi(float pa, MeasurementSystem to) => to == MeasurementSystem.IP ? pa / INWG_TO_PA : pa;

        /// <summary>A length (throws, distances): ft or m to m.</summary>
        public static float LengthToSi(float value, MeasurementSystem from) => from == MeasurementSystem.IP ? value * FT_TO_M : value;

        /// <summary>A length: m to ft or m.</summary>
        public static float LengthFromSi(float m, MeasurementSystem to) => to == MeasurementSystem.IP ? m / FT_TO_M : m;

        /// <summary>A size (necks, faces): in or mm to m.</summary>
        public static float SizeToSi(float value, MeasurementSystem from) => from == MeasurementSystem.IP ? value * IN_TO_M : value * MM_TO_M;

        /// <summary>A size: m to in or mm.</summary>
        public static float SizeFromSi(float m, MeasurementSystem to) => to == MeasurementSystem.IP ? m / IN_TO_M : m / MM_TO_M;

        /// <summary>A temperature: °F or °C to °C.</summary>
        public static float TemperatureToSi(float value, MeasurementSystem from) => from == MeasurementSystem.IP ? (value - 32f) / 1.8f : value;

        /// <summary>A temperature: °C to °F or °C.</summary>
        public static float TemperatureFromSi(float celsius, MeasurementSystem to) => to == MeasurementSystem.IP ? celsius * 1.8f + 32f : celsius;

        /// <summary>A temperature difference: °F or K to K.</summary>
        public static float TemperatureDeltaToSi(float value, MeasurementSystem from) => from == MeasurementSystem.IP ? value / 1.8f : value;

        /// <summary>A temperature difference: K to °F or K.</summary>
        public static float TemperatureDeltaFromSi(float kelvin, MeasurementSystem to) => to == MeasurementSystem.IP ? kelvin * 1.8f : kelvin;

        #endregion

        #region Names

        /// <summary>
        /// "IP" / "SI" (case-insensitive; also "imperial" / "metric") to a system; anything else is null.
        /// </summary>
        public static MeasurementSystem? Parse(string text)
        {
            string t = text?.Trim() ?? string.Empty;
            if (t.Equals("IP", StringComparison.OrdinalIgnoreCase) || t.Equals("imperial", StringComparison.OrdinalIgnoreCase)) { return MeasurementSystem.IP; }
            if (t.Equals("SI", StringComparison.OrdinalIgnoreCase) || t.Equals("metric", StringComparison.OrdinalIgnoreCase)) { return MeasurementSystem.SI; }
            return null;
        }

        /// <summary>"IP" or "SI".</summary>
        public static string Name(MeasurementSystem system) => system == MeasurementSystem.IP ? "IP" : "SI";

        /// <summary>
        /// The system a Revit unit type id stands for (the <c>ForgeTypeId.TypeId</c> string of a project's flow or length
        /// format, e.g. <c>autodesk.unit.unit:cubicFeetPerMinute-1.0.1</c>): feet, inches, gallons and CFM read as IP,
        /// anything else as SI. Kept in Core (plain strings) so it is testable without Revit.
        /// </summary>
        public static MeasurementSystem FromRevitUnitTypeId(string typeId)
        {
            string id = typeId ?? string.Empty;
            int colon = id.LastIndexOf(':');
            if (colon >= 0) { id = id[(colon + 1)..]; }
            string[] imperial = { "feet", "foot", "inch", "gallon", "cfm", "fahrenheit" };
            foreach (string word in imperial)
            {
                if (id.Contains(word, StringComparison.OrdinalIgnoreCase)) { return MeasurementSystem.IP; }
            }
            return MeasurementSystem.SI;
        }

        #endregion

        #region Formatting

        // Panels built on change and CSV export only: the per-frame readout writes into a char buffer instead (App)
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;

        /// <summary>"250 CFM" / "118 L/s".</summary>
        public static string FormatFlow(float lps, MeasurementSystem system) => system == MeasurementSystem.IP
            ? FlowFromSi(lps, system).ToString("0", INV) + " CFM"
            : lps.ToString("0", INV) + " L/s";

        /// <summary>"48 fpm" / "0.24 m/s".</summary>
        public static string FormatSpeed(float mps, MeasurementSystem system) => system == MeasurementSystem.IP
            ? SpeedFromSi(mps, system).ToString("0", INV) + " fpm"
            : mps.ToString("0.00", INV) + " m/s";

        /// <summary>"12.5 ft" / "3.8 m" (throws and distances).</summary>
        public static string FormatLength(float m, MeasurementSystem system) => system == MeasurementSystem.IP
            ? LengthFromSi(m, system).ToString("0.0", INV) + " ft"
            : m.ToString("0.0", INV) + " m";

        /// <summary>"9' 0\"" (nearest inch) / "2,700 mm" (nearest mm). Negative values keep their sign.</summary>
        public static string FormatElevation(float m, MeasurementSystem system)
        {
            if (system == MeasurementSystem.SI) { return MathF.Round(m / MM_TO_M).ToString("#,##0", INV) + " mm"; }
            int totalInches = (int)MathF.Round(MathF.Abs(m) / IN_TO_M);
            string sign = m < 0 && totalInches > 0 ? "-" : string.Empty;
            return $"{sign}{totalInches / 12}' {totalInches % 12}\"";
        }

        /// <summary>"8 in" / "200 mm" (necks, faces).</summary>
        public static string FormatSize(float m, MeasurementSystem system) => system == MeasurementSystem.IP
            ? SizeFromSi(m, system).ToString("0.#", INV) + " in"
            : SizeFromSi(m, system).ToString("0", INV) + " mm";

        /// <summary>"0.060 in wg" / "15 Pa".</summary>
        public static string FormatPressure(float pa, MeasurementSystem system) => system == MeasurementSystem.IP
            ? PressureFromSi(pa, system).ToString("0.000", INV) + " in wg"
            : pa.ToString("0", INV) + " Pa";

        /// <summary>"55 °F" / "13 °C".</summary>
        public static string FormatTemperature(float celsius, MeasurementSystem system) => system == MeasurementSystem.IP
            ? TemperatureFromSi(celsius, system).ToString("0", INV) + " °F"
            : celsius.ToString("0", INV) + " °C";

        #endregion
    }
}
