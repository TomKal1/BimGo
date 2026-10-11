using System.Globalization;
using System.Text.Json;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Diffuser catalog files: user catalogs in <see cref="DefaultFolder"/> (<c>*.json</c> and <c>*.csv</c>) and the
    /// conversion between catalog families (SI) and their file form (IP or SI). Never throws: a bad file or row is
    /// skipped and described in the problems list.
    /// <para><b>CSV</b>: a header row, then one row per neck / face / flow. Columns (any order, case-insensitive):
    /// <c>family_key, label, units, neck, face, flow, pt, nc, v1, t1, v2, t2 …</c> (up to 6 speed / throw pairs),
    /// optional <c>match</c> (keywords separated by ';') and <c>free_jet_factor</c>. <c>units</c> is IP (in, CFM,
    /// in wg, fpm, ft) or SI (mm, L/s, Pa, m/s, m). Empty <c>pt</c> / <c>nc</c> mean unknown. Lines starting with
    /// '#' are comments. Values can't contain commas.</para>
    /// </summary>
    public static class CatalogFiles
    {
        /// <summary>Most speed / throw pairs per CSV row.</summary>
        private const int MAX_PAIRS = 6;

        /// <summary>
        /// The user catalog folder: <c>%AppData%\BimGo\Catalogs</c>.
        /// </summary>
        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BimGo", "Catalogs");

        #region Load

        /// <summary>
        /// The built-in catalog plus every family in a folder's catalog files (files in name order; a family whose key
        /// is already loaded replaces it). A missing folder gives the built-in catalog.
        /// </summary>
        /// <param name="folder">The folder (normally <see cref="DefaultFolder"/>), or null.</param>
        /// <param name="problems">Skipped files and rows, for the airflow panel.</param>
        public static DiffuserCatalog Load(string folder, out List<string> problems)
        {
            problems = new List<string>();
            var families = new List<CatalogFamily>();
            try
            {
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    foreach (string path in Directory.GetFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        string extension = Path.GetExtension(path);
                        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) { families.AddRange(ReadJsonFile(path, problems)); }
                        else if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)) { families.AddRange(ReadCsvFile(path, problems)); }
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add($"Catalog folder could not be read: {ex.Message}");
            }

            var catalog = new DiffuserCatalog(families, problems);
            foreach (string problem in problems) { Utilities.Log_Utils.Write($"Diffuser catalog: {problem}"); }
            return catalog;
        }

        /// <summary>The families in a JSON catalog file (<see cref="CatalogFileDto"/>), cleaned.</summary>
        public static List<CatalogFamily> ReadJsonFile(string path, List<string> problems)
        {
            string name = Path.GetFileName(path);
            try
            {
                CatalogFileDto file = JsonSerializer.Deserialize<CatalogFileDto>(File.ReadAllText(path), BimGoFormat.JSON_INDENTED);
                return (file?.Families ?? new List<CatalogFamilyDto>())
                    .Select(dto => FromDto(dto, name, problems)?.Clean(problems))
                    .Where(f => f != null)
                    .ToList();
            }
            catch (Exception ex)
            {
                problems?.Add($"{name} skipped: {ex.Message}");
                return new List<CatalogFamily>();
            }
        }

        /// <summary>The families in a CSV catalog file.</summary>
        public static List<CatalogFamily> ReadCsvFile(string path, List<string> problems)
        {
            string name = Path.GetFileName(path);
            try { return ParseCsv(File.ReadAllLines(path), name, problems); }
            catch (Exception ex)
            {
                problems?.Add($"{name} skipped: {ex.Message}");
                return new List<CatalogFamily>();
            }
        }

        /// <summary>
        /// Parses CSV lines into cleaned families (rows grouped by <c>family_key</c>, label / units / match / free-jet
        /// factor from each family's first row).
        /// </summary>
        public static List<CatalogFamily> ParseCsv(IReadOnlyList<string> lines, string source, List<string> problems)
        {
            var families = new List<CatalogFamily>();
            Dictionary<string, int> columns = null;
            var byKey = new Dictionary<string, (CatalogFamilyDto Dto, int Line)>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i]?.Trim() ?? string.Empty;
                if (line.Length == 0 || line.StartsWith('#')) { continue; }
                string[] cells = line.Split(',').Select(c => c.Trim().Trim('"').Trim()).ToArray();

                if (columns == null)
                {
                    columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int c = 0; c < cells.Length; c++) { columns.TryAdd(cells[c], c); }
                    string missing = new[] { "family_key", "units", "neck", "face", "flow", "v1", "t1" }.FirstOrDefault(n => !columns.ContainsKey(n));
                    if (missing != null)
                    {
                        problems?.Add($"{source}: header has no '{missing}' column; file skipped.");
                        return families;
                    }
                    continue;
                }

                string where = $"{source} line {i + 1}";
                string Cell(string column) => columns.TryGetValue(column, out int c) && c < cells.Length ? cells[c] : string.Empty;

                string key = Cell("family_key");
                if (key.Length == 0) { problems?.Add($"{where}: no family_key; row skipped."); continue; }
                if (!TryNumber(Cell("neck"), out float neck) || !TryNumber(Cell("face"), out float face) || !TryNumber(Cell("flow"), out float flow))
                {
                    problems?.Add($"{where}: neck, face or flow is not a number; row skipped.");
                    continue;
                }
                float pt = TryNumber(Cell("pt"), out float p) ? p : 0f;
                float nc = TryNumber(Cell("nc"), out float n) ? n : 0f;

                var row = new CatalogRowDto { Neck = neck, Face = face, Flow = flow, Pt = pt, Nc = nc };
                for (int pair = 1; pair <= MAX_PAIRS; pair++)
                {
                    string speedText = Cell($"v{pair}"), throwText = Cell($"t{pair}");
                    if (speedText.Length == 0 && throwText.Length == 0) { continue; }
                    if (!TryNumber(speedText, out float speed) || !TryNumber(throwText, out float distance))
                    {
                        problems?.Add($"{where}: v{pair} / t{pair} is not a number; pair skipped.");
                        continue;
                    }
                    row.Throw[speed.ToString("0.####", CultureInfo.InvariantCulture)] = distance;
                }

                if (!byKey.TryGetValue(key, out (CatalogFamilyDto Dto, int Line) entry))
                {
                    var dto = new CatalogFamilyDto
                    {
                        Key = key,
                        Label = Cell("label"),
                        Units = Cell("units"),
                        Match = Cell("match").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                        FreeJetFactor = TryNumber(Cell("free_jet_factor"), out float factor) ? factor : null
                    };
                    entry = (dto, i + 1);
                    byKey[key] = entry;
                    order.Add(key);
                }
                else if (UnitConvert.Parse(Cell("units")) != UnitConvert.Parse(entry.Dto.Units))
                {
                    problems?.Add($"{where}: units differ from the family's first row (line {entry.Line}); row skipped.");
                    continue;
                }
                entry.Dto.Rows.Add(row);
            }

            if (columns == null) { problems?.Add($"{source}: empty file."); }
            foreach (string key in order)
            {
                CatalogFamily family = FromDto(byKey[key].Dto, source, problems)?.Clean(problems);
                if (family != null) { families.Add(family); }
            }
            return families;
        }

        private static bool TryNumber(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

        #endregion

        #region Conversion

        /// <summary>
        /// A file family (IP or SI) to a catalog family (SI). Returns null, with a problem line, when the units are
        /// missing; rows are checked later by <see cref="CatalogFamily.Clean"/>.
        /// </summary>
        public static CatalogFamily FromDto(CatalogFamilyDto dto, string source, List<string> problems)
        {
            if (dto == null) { return null; }
            MeasurementSystem? parsed = UnitConvert.Parse(dto.Units);
            if (parsed == null)
            {
                problems?.Add($"{source}: family '{dto.Key}' has no units (IP or SI); skipped.");
                return null;
            }
            MeasurementSystem units = parsed.Value;

            var family = new CatalogFamily
            {
                Key = dto.Key ?? string.Empty,
                Label = dto.Label,
                Illustrative = dto.Illustrative,
                FreeJetFactor = dto.FreeJetFactor ?? 0.7f,
                SourceUnits = units,
                Match = dto.Match ?? new List<string>(),
                Source = source
            };
            foreach (CatalogRowDto row in dto.Rows ?? new List<CatalogRowDto>())
            {
                if (row == null) { continue; }
                var speeds = new List<float>();
                var throws = new List<float>();
                foreach (KeyValuePair<string, float> pair in (row.Throw ?? new Dictionary<string, float>()).OrderByDescending(p => ParseOrNaN(p.Key)))
                {
                    if (!TryNumber(pair.Key, out float speed))
                    {
                        problems?.Add($"{source}: family '{dto.Key}' throw speed '{pair.Key}' is not a number; skipped.");
                        continue;
                    }
                    speeds.Add(UnitConvert.SpeedToSi(speed, units));
                    throws.Add(UnitConvert.LengthToSi(pair.Value, units));
                }
                family.Rows.Add(new CatalogRow
                {
                    NeckDiameter = UnitConvert.SizeToSi(row.Neck, units),
                    FaceSize = UnitConvert.SizeToSi(row.Face, units),
                    Flow = UnitConvert.FlowToSi(row.Flow, units),
                    TotalPressure = UnitConvert.PressureToSi(row.Pt, units),
                    Nc = row.Nc,
                    ThrowSpeeds = speeds.ToArray(),
                    Throws = throws.ToArray()
                });
            }
            return family;
        }

        /// <summary>A catalog family to its file form in SI (how air.json stores the families a model uses).</summary>
        public static CatalogFamilyDto ToDto(CatalogFamily family)
        {
            var dto = new CatalogFamilyDto
            {
                Key = family.Key,
                Label = family.Label,
                Units = UnitConvert.Name(MeasurementSystem.SI),
                Illustrative = family.Illustrative,
                FreeJetFactor = family.FreeJetFactor,
                Match = family.Match.Count > 0 ? family.Match.ToList() : null
            };
            foreach (CatalogRow row in family.Rows)
            {
                var throws = new Dictionary<string, float>();
                for (int i = 0; i < row.Throws.Length && i < row.ThrowSpeeds.Length; i++)
                {
                    throws[row.ThrowSpeeds[i].ToString("0.#####", CultureInfo.InvariantCulture)] = row.Throws[i];
                }
                dto.Rows.Add(new CatalogRowDto
                {
                    Neck = row.NeckDiameter / UnitConvert.MM_TO_M,
                    Face = row.FaceSize / UnitConvert.MM_TO_M,
                    Flow = row.Flow,
                    Pt = row.TotalPressure,
                    Nc = row.Nc,
                    Throw = throws
                });
            }
            return dto;
        }

        private static float ParseOrNaN(string text) => TryNumber(text, out float value) ? value : float.NaN;

        #endregion
    }
}
