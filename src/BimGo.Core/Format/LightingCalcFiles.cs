using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>A results file in a lighting-calcs folder (for the CALCS list).</summary>
    public sealed class LightingCalcFileInfo
    {
        /// <summary>The JSON file's full path.</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>The file name without ".json".</summary>
        public string Name { get; init; } = string.Empty;

        public DateTime WrittenUtc { get; init; }
    }

    /// <summary>
    /// Fork build LC-B: lighting calc results files (format "lighting-calc-results" version 1, written by WW Tools' Lighting
    /// Calc; the spec is WW Tools' docs/lighting-calcs/results-format.md). Each calc set is a JSON header and a little-endian
    /// float32 body beside it (same name, ".bin"), in <c>&lt;model folder&gt;\lighting-calcs\</c> for live sessions and as
    /// optional <c>lighting-calcs/…</c> entries inside a .bimgo. Read only: BimGo never writes these.
    /// <list type="bullet">
    /// <item>Coordinates are Revit internal feet (host model): scene metres = feet × 0.3048 − <see cref="SceneData.OriginOffset"/>.</item>
    /// <item>Values stay in footcandles; the UI shows lux when BimGo's units are metric.</item>
    /// <item>The reader checks every offset and count against the body, drops a damaged zone (with a reason) and never
    /// throws. A body shorter or longer than the header's <c>binBytes</c> means the writer was mid-write: try again later.</item>
    /// </list>
    /// </summary>
    public static class LightingCalcFiles
    {
        /// <summary>The model-folder sub-folder, and the entry prefix inside a .bimgo.</summary>
        public const string FOLDER = ModelFolders.LIGHTING_CALCS_FOLDER;

        /// <summary>The entry prefix inside a .bimgo.</summary>
        public const string ENTRY_PREFIX = FOLDER + "/";

        /// <summary>The header's format name.</summary>
        public const string FORMAT = "lighting-calc-results";

        /// <summary>The newest version this reader understands.</summary>
        public const int VERSION = 1;

        /// <summary>Phase C: the request WW Tools' "View in BimGo" leaves in the folder (calc set, zone, mode).</summary>
        public const string OPEN_REQUEST_FILE = "open.json";

        /// <summary>Largest header (MB) and body (MB) read.</summary>
        private const long MAX_JSON_BYTES = 32L << 20, MAX_BIN_BYTES = 400L << 20;

        /// <summary>Results embedded in a .bimgo at most (bytes, all sets together): beyond it the newest sets that fit go in.</summary>
        public const long MAX_EMBED_BYTES = 100L << 20;

        private const int POINT_FLOATS = 5, PATCH_FLOATS = 15, SEGMENT_FLOATS = 6;

        #region Folders and lists

        /// <summary>The lighting-calcs folder of a live session's model folder (from its comments path), or null.</summary>
        public static string FolderFor(string commentsPath)
        {
            string folder = ModelFolders.FolderOf(commentsPath);
            return folder == null ? null : System.IO.Path.Combine(folder, FOLDER);
        }

        /// <summary>The results files in a folder, newest first (empty when there is no folder). Never throws.</summary>
        public static List<LightingCalcFileInfo> List(string folder)
        {
            var list = new List<LightingCalcFileInfo>();
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { return list; }
                foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (string.Equals(System.IO.Path.GetFileName(path), OPEN_REQUEST_FILE, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (!File.Exists(System.IO.Path.ChangeExtension(path, ".bin"))) { continue; }
                    list.Add(new LightingCalcFileInfo { Path = path, Name = name, WrittenUtc = File.GetLastWriteTimeUtc(path) });
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Lighting calcs folder unreadable ({folder}): {ex.Message}");
            }
            list.Sort((a, b) => b.WrittenUtc.CompareTo(a.WrittenUtc));
            return list;
        }

        #endregion

        #region Reading

        /// <summary>Reads a results file and its body from disk. Null with a reason on failure; never throws.</summary>
        public static LightingCalcResult Read(string jsonPath, Vector3 origin, out string error)
        {
            try
            {
                string binPath = System.IO.Path.ChangeExtension(jsonPath, ".bin");
                if (!File.Exists(jsonPath)) { error = "the file is missing"; return null; }
                if (!File.Exists(binPath)) { error = "its .bin is missing"; return null; }
                if (new FileInfo(jsonPath).Length > MAX_JSON_BYTES || new FileInfo(binPath).Length > MAX_BIN_BYTES) { error = "the file is too big"; return null; }
                DateTime written = File.GetLastWriteTimeUtc(jsonPath);
                byte[] json = File.ReadAllBytes(jsonPath);
                byte[] bin = File.ReadAllBytes(binPath);
                return Parse(json, bin, System.IO.Path.GetFileName(jsonPath), origin, written, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Utilities.Log_Utils.Write($"Lighting calc results unreadable ({jsonPath}): {ex}");
                return null;
            }
        }

        /// <summary>
        /// Parses a header and its body (from disk or a .bimgo's entries), converting to scene metres and preparing the corner
        /// values. Null with a reason when the file isn't usable; damaged zones are dropped and listed in
        /// <see cref="LightingCalcResult.Warnings"/>. Never throws.
        /// </summary>
        public static LightingCalcResult Parse(byte[] json, byte[] bin, string source, Vector3 origin, DateTime writtenUtc, out string error)
        {
            error = null;
            try
            {
                if (json == null || bin == null) { error = "the header or body is missing"; return null; }
                using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !string.Equals(Str(root, "format"), FORMAT, StringComparison.Ordinal))
                {
                    error = "not a lighting calc results file";
                    return null;
                }
                int version = Int(root, "version");
                if (version < 1 || version > VERSION)
                {
                    error = $"format version {version} (this BimGo reads version {VERSION})";
                    return null;
                }
                long binBytes = Long(root, "binBytes", -1);
                if (binBytes >= 0 && binBytes != bin.LongLength)
                {
                    error = "the body doesn't match its header (still being written?)";
                    return null;
                }

                JsonElement units = Obj(root, "units");
                if (!string.Equals(Str(units, "length", "ft"), "ft", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Str(units, "coordinates", "revit-internal"), "revit-internal", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Str(units, "illuminance", "fc"), "fc", StringComparison.OrdinalIgnoreCase))
                {
                    error = "units other than feet, Revit internal coordinates and footcandles";
                    return null;
                }

                JsonElement set = Obj(root, "calcSet"), model = Obj(root, "model"), run = Obj(root, "run"), scale = Obj(root, "scale");
                var warnings = new List<string>();
                var zones = new List<CalcZoneResult>();
                if (root.TryGetProperty("zones", out JsonElement zoneArray) && zoneArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement z in zoneArray.EnumerateArray())
                    {
                        CalcZoneResult zone = ReadZone(z, bin, origin, out string reason);
                        if (zone != null) { zones.Add(zone); }
                        else { warnings.Add($"{Str(z, "number")} {Str(z, "name")}".Trim() + ": " + reason); }
                    }
                }

                var luminaires = new List<CalcLuminaire>();
                if (root.TryGetProperty("luminaires", out JsonElement lumArray) && lumArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement l in lumArray.EnumerateArray())
                    {
                        if (!TryVec(l, "position", out Vector3 position)) { continue; }
                        TryVec(l, "nadir", out Vector3 nadir);
                        luminaires.Add(new CalcLuminaire
                        {
                            Id = Long(l, "id"),
                            Type = Str(l, "type"),
                            Position = LightingCalcs.ToScene(position.X, position.Y, position.Z, origin),
                            Nadir = nadir,
                            Multiplier = Float(l, "multiplier", 1f)
                        });
                    }
                }

                float scaleMin = Float(scale, "min", 0f), scaleMax = Float(scale, "max", 0f);
                if (!(scaleMax > scaleMin))
                {
                    // No usable scale: 0 to the highest value
                    scaleMin = 0f;
                    scaleMax = MathF.Max(1f, zones.Count == 0 ? 1f : zones.Max(z => z.Stats.Maximum));
                }

                DateTime.TryParse(Str(run, "utc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime runUtc);
                var result = new LightingCalcResult
                {
                    SetId = Str(set, "id"),
                    SetName = Str(set, "name", System.IO.Path.GetFileNameWithoutExtension(source ?? string.Empty)),
                    Tool = Str(root, "tool"),
                    ModelTitle = Str(model, "title"),
                    KeySource = Str(model, "keySource"),
                    RunUtc = runUtc,
                    Interreflections = Bool(run, "interreflections"),
                    Quality = Str(run, "quality"),
                    ScaleMin = scaleMin,
                    ScaleMax = scaleMax,
                    ReportUnits = Str(units, "report", "fc"),
                    Zones = zones.ToArray(),
                    Luminaires = luminaires.ToArray(),
                    Source = source ?? string.Empty,
                    FileTimeUtc = writtenUtc
                };
                result.Warnings.AddRange(warnings);
                LightingCalcs.Prepare(result);
                return result;
            }
            catch (Exception ex)
            {
                error = ex is JsonException ? "the header isn't valid JSON" : ex.Message;
                Utilities.Log_Utils.Write($"Lighting calc results unreadable ({source}): {ex.Message}");
                return null;
            }
        }

        private static CalcZoneResult ReadZone(JsonElement z, byte[] bin, Vector3 origin, out string reason)
        {
            reason = null;
            if (z.ValueKind != JsonValueKind.Object) { reason = "not an object"; return null; }

            JsonElement pointRange = Obj(z, "points"), patchRange = Obj(z, "patches");
            if (!Range(pointRange, POINT_FLOATS, bin.LongLength, out long pointOffset, out int pointCount)) { reason = "its points lie outside the body"; return null; }
            if (!Range(patchRange, PATCH_FLOATS, bin.LongLength, out long patchOffset, out int patchCount)) { reason = "its surfaces lie outside the body"; return null; }

            ReadOnlySpan<byte> body = bin;
            var points = new Vector3[pointCount];
            var values = new float[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                int at = (int)(pointOffset + (long)i * POINT_FLOATS * 4);
                points[i] = LightingCalcs.ToScene(F(body, at), F(body, at + 4), F(body, at + 8), origin);
                float e = F(body, at + 12);
                values[i] = float.IsFinite(e) ? e : float.NaN;
            }

            var patches = new CalcPatch[patchCount];
            int kept = 0;
            for (int i = 0; i < patchCount; i++)
            {
                int at = (int)(patchOffset + (long)i * PATCH_FLOATS * 4);
                Vector3 n = new(F(body, at + 36), F(body, at + 40), F(body, at + 44));
                float e = F(body, at + 48), rho = F(body, at + 52), flags = F(body, at + 56);
                if (!float.IsFinite(e) || !float.IsFinite(rho) || !(n.LengthSquared() > 0.25f)) { continue; } // skip a bad patch, keep the zone
                patches[kept++] = new CalcPatch
                {
                    A = LightingCalcs.ToScene(F(body, at), F(body, at + 4), F(body, at + 8), origin),
                    B = LightingCalcs.ToScene(F(body, at + 12), F(body, at + 16), F(body, at + 20), origin),
                    C = LightingCalcs.ToScene(F(body, at + 24), F(body, at + 28), F(body, at + 32), origin),
                    Normal = Vector3.Normalize(n),
                    Illuminance = MathF.Max(0f, e),
                    Reflectance = Math.Clamp(rho, 0f, 1f),
                    Flags = float.IsFinite(flags) ? (int)flags : 0
                };
            }
            if (kept < patchCount) { Array.Resize(ref patches, kept); }

            var isolines = new List<CalcIsoline>();
            if (z.TryGetProperty("isolines", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement line in lines.EnumerateArray())
                {
                    if (!Range(line, SEGMENT_FLOATS, bin.LongLength, out long offset, out int count)) { reason = "its isolux lines lie outside the body"; return null; }
                    var segments = new Vector3[count * 2];
                    for (int i = 0; i < count * 2; i++)
                    {
                        int at = (int)(offset + (long)i * 12);
                        segments[i] = LightingCalcs.ToScene(F(body, at), F(body, at + 4), F(body, at + 8), origin);
                    }
                    isolines.Add(new CalcIsoline { Value = Float(line, "value"), IsTarget = Bool(line, "target"), Segments = segments });
                }
            }

            var loops = new List<Vector2[]>();
            if (z.TryGetProperty("loops", out JsonElement loopArray) && loopArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement loop in loopArray.EnumerateArray())
                {
                    if (loop.ValueKind != JsonValueKind.Array) { continue; }
                    var corners = new List<Vector2>();
                    foreach (JsonElement corner in loop.EnumerateArray())
                    {
                        if (corner.ValueKind != JsonValueKind.Array || corner.GetArrayLength() < 2) { continue; }
                        if (!corner[0].TryGetDouble(out double x) || !corner[1].TryGetDouble(out double y)) { continue; }
                        corners.Add(new Vector2((float)(x * LightingCalcs.FEET) - origin.X, (float)(y * LightingCalcs.FEET) - origin.Y));
                    }
                    if (corners.Count >= 3) { loops.Add(corners.ToArray()); }
                }
            }

            JsonElement stats = Obj(z, "stats");
            CalcTarget target = null;
            if (z.TryGetProperty("target", out JsonElement t) && t.ValueKind == JsonValueKind.Object)
            {
                string result = Str(t, "result", null);
                target = new CalcTarget
                {
                    Average = Float(t, "avg"),
                    AverageMax = Float(t, "avgMax"),
                    AverageToMinimum = Float(t, "avgMin"),
                    Result = result == "Meets" || result == "Misses" ? result : null
                };
            }

            float bottom = Float(z, "bottomZ") * LightingCalcs.FEET - origin.Z;
            float top = Float(z, "topZ") * LightingCalcs.FEET - origin.Z;
            float ceiling = z.TryGetProperty("ceilingZ", out _) ? Float(z, "ceilingZ") * LightingCalcs.FEET - origin.Z : top;
            return new CalcZoneResult
            {
                Key = Str(z, "key"),
                Number = Str(z, "number"),
                Name = Str(z, "name"),
                Kind = Str(z, "kind"),
                Level = Str(z, "level"),
                BottomZ = bottom,
                TopZ = MathF.Max(top, bottom),
                CeilingZ = MathF.Max(ceiling, bottom),
                OnWalkingSurfaces = Bool(z, "onWalkingSurfaces"),
                Stats = new CalcStats
                {
                    Count = Int(stats, "count"),
                    Average = Float(stats, "avg"),
                    Maximum = Float(stats, "max"),
                    Minimum = Float(stats, "min"),
                    AverageToMinimum = Float(stats, "avgMin", float.NaN),
                    MaximumToMinimum = Float(stats, "maxMin", float.NaN),
                    PowerDensity = Float(stats, "lpd")
                },
                Target = target,
                Loops = loops.ToArray(),
                Points = points,
                PointValues = values,
                Patches = patches,
                Isolines = isolines.ToArray()
            };
        }

        /// <summary>An {offset, count} range of records of <paramref name="floats"/> floats inside a body of <paramref name="length"/> bytes.</summary>
        private static bool Range(JsonElement range, int floats, long length, out long offset, out int count)
        {
            offset = 0;
            count = 0;
            if (range.ValueKind != JsonValueKind.Object) { return true; } // missing = none
            offset = Long(range, "offset");
            long n = Long(range, "count");
            if (offset < 0 || n < 0 || n > int.MaxValue / floats) { return false; }
            count = (int)n;
            return count == 0 || (offset % 4 == 0 && offset + (long)count * floats * 4 <= length);
        }

        private static float F(ReadOnlySpan<byte> body, int at) => BinaryPrimitives.ReadSingleLittleEndian(body.Slice(at, 4));

        private static JsonElement Obj(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Object ? v : default;

        private static string Str(JsonElement e, string name, string fallback = "") =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        private static long Long(JsonElement e, string name, long fallback = 0) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : fallback;

        private static int Int(JsonElement e, string name) => (int)Math.Clamp(Long(e, name), int.MinValue, int.MaxValue);

        private static float Float(JsonElement e, string name, float fallback = 0f) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)
                ? (float)d : fallback;

        private static bool TryVec(JsonElement e, string name, out Vector3 v)
        {
            v = Vector3.Zero;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 3) { return false; }
            if (!a[0].TryGetDouble(out double x) || !a[1].TryGetDouble(out double y) || !a[2].TryGetDouble(out double z)) { return false; }
            v = new Vector3((float)x, (float)y, (float)z);
            return true;
        }

        #endregion

        #region Inside a .bimgo

        /// <summary>
        /// The results files to embed in a .bimgo (Export / Save as): entry name ("lighting-calcs/&lt;name&gt;.json" and ".bin")
        /// → bytes, newest sets first while they fit under <see cref="MAX_EMBED_BYTES"/>. <paramref name="note"/> says what was
        /// left out (null when everything went in). Never throws.
        /// </summary>
        public static Dictionary<string, byte[]> ReadForEmbedding(string folder, out string note)
        {
            note = null;
            var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            int skipped = 0;
            foreach (LightingCalcFileInfo file in List(folder))
            {
                try
                {
                    string bin = System.IO.Path.ChangeExtension(file.Path, ".bin");
                    long size = new FileInfo(file.Path).Length + new FileInfo(bin).Length;
                    if (total + size > MAX_EMBED_BYTES)
                    {
                        skipped++;
                        continue;
                    }
                    entries[ENTRY_PREFIX + file.Name + ".json"] = File.ReadAllBytes(file.Path);
                    entries[ENTRY_PREFIX + file.Name + ".bin"] = File.ReadAllBytes(bin);
                    total += size;
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Lighting calc results not embedded ({file.Path}): {ex.Message}");
                }
            }
            if (skipped > 0)
            {
                note = $"{skipped} lighting calc set{(skipped == 1 ? " was" : "s were")} left out of the file: over {MAX_EMBED_BYTES >> 20} MB of results.";
            }
            return entries.Count == 0 ? null : entries;
        }

        /// <summary>The calc sets held in a .bimgo's entries (names without ".json"), newest-first order not known: by name.</summary>
        public static List<string> EmbeddedNames(Dictionary<string, byte[]> entries)
        {
            var names = new List<string>();
            if (entries == null) { return names; }
            foreach (string key in entries.Keys)
            {
                if (!key.StartsWith(ENTRY_PREFIX, StringComparison.OrdinalIgnoreCase) || !key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { continue; }
                string name = key.Substring(ENTRY_PREFIX.Length, key.Length - ENTRY_PREFIX.Length - 5);
                if (name.Length == 0 || name.Contains('/') || string.Equals(name + ".json", OPEN_REQUEST_FILE, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (entries.ContainsKey(ENTRY_PREFIX + name + ".bin")) { names.Add(name); }
            }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        /// <summary>Parses an embedded calc set by name (see <see cref="EmbeddedNames"/>). Null with a reason on failure.</summary>
        public static LightingCalcResult ParseEmbedded(Dictionary<string, byte[]> entries, string name, Vector3 origin, out string error)
        {
            error = "not in the file";
            if (entries == null || name == null) { return null; }
            if (!entries.TryGetValue(ENTRY_PREFIX + name + ".json", out byte[] json) || !entries.TryGetValue(ENTRY_PREFIX + name + ".bin", out byte[] bin)) { return null; }
            return Parse(json, bin, name + ".json", origin, DateTime.MinValue, out error);
        }

        /// <summary>True for an entry name a .bimgo may carry under <see cref="ENTRY_PREFIX"/> (a plain name, .json or .bin).</summary>
        public static bool IsEntryName(string entry)
        {
            if (entry == null || !entry.StartsWith(ENTRY_PREFIX, StringComparison.OrdinalIgnoreCase)) { return false; }
            string name = entry.Substring(ENTRY_PREFIX.Length);
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..")) { return false; }
            return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Open request (Phase C)

        /// <summary>WW Tools' "View in BimGo" request: which calc set, zone and mode to start with.</summary>
        public sealed class OpenRequest
        {
            /// <summary>The calc set's id.</summary>
            public string CalcSet { get; init; } = string.Empty;

            /// <summary>A zone key to start in, or empty.</summary>
            public string Zone { get; init; } = string.Empty;

            /// <summary>"illuminance", "luminance", "points" or empty (illuminance).</summary>
            public string Mode { get; init; } = string.Empty;

            /// <summary>When it was written (UTC).</summary>
            public DateTime Utc { get; init; }
        }

        /// <summary>
        /// Reads and deletes <c>open.json</c> in a lighting-calcs folder when it is under <paramref name="maxAge"/> old (an older
        /// one is deleted and ignored). Null when there is none. Never throws.
        /// </summary>
        public static OpenRequest TakeOpenRequest(string folder, TimeSpan maxAge, DateTime nowUtc)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) { return null; }
                string path = System.IO.Path.Combine(folder, OPEN_REQUEST_FILE);
                if (!File.Exists(path)) { return null; }
                byte[] bytes = File.ReadAllBytes(path);
                File.Delete(path);
                using JsonDocument doc = JsonDocument.Parse(bytes);
                JsonElement root = doc.RootElement;
                if (!DateTime.TryParse(Str(root, "utc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc)) { return null; }
                if (nowUtc - utc > maxAge || utc - nowUtc > TimeSpan.FromMinutes(5)) { return null; }
                return new OpenRequest { CalcSet = Str(root, "calcSet"), Zone = Str(root, "zone"), Mode = Str(root, "mode"), Utc = utc };
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Lighting calc open request unreadable ({folder}): {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
