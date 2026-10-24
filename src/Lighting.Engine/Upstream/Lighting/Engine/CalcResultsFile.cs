using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>What a results file says about the run that made it (everything but the zones and luminaires).</summary>
    public sealed class CalcResultsRun
    {
        /// <summary>"WW Tools 1.0.0".</summary>
        public string Tool { get; set; } = "WW Tools";

        public string CalcSetName { get; set; } = string.Empty;

        public string ModelTitle { get; set; } = string.Empty;

        /// <summary>The model's BimGo folder key ("cloud:…", "central:…", "local:…", "unsaved:…"; see <see cref="BimGoFolders"/>).</summary>
        public string KeySource { get; set; } = string.Empty;

        public string ProjectInfoUniqueId { get; set; } = string.Empty;

        public DateTime RunUtc { get; set; } = DateTime.UtcNow;

        public bool Interreflection { get; set; } = true;

        /// <summary>"Draft", "Normal" or "High".</summary>
        public string Quality { get; set; } = "Normal";

        /// <summary>The report's units: targets are typed in them and the default scale top is rounded in them.</summary>
        public IlluminanceUnits Units { get; set; } = IlluminanceUnits.Footcandles;

        /// <summary>Top of the colour scale (fc) shared by the sheets and report; 0 = a round number above the highest value.</summary>
        public double ColorScaleMax { get; set; }

        /// <summary>The settings all zones share, as "name: value" (grid, geometry detail, quality, ...); empty in older files.</summary>
        public List<string> SharedSettings { get; set; } = new List<string>();
    }

    /// <summary>A built results file: the JSON header and the binary body it describes.</summary>
    public sealed class CalcResultsData
    {
        public string Json { get; set; } = string.Empty;

        public byte[] Body { get; set; } = Array.Empty<byte>();

        public int Zones { get; set; }

        public int Points { get; set; }

        public int Patches { get; set; }

        public int IsolineSegments { get; set; }

        /// <summary>What was left out to stay under <see cref="CalcResultsFile.MaxPatches"/>, for the run log.</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// The lighting calc results exchange file (format "lighting-calc-results" version 1, specified in
    /// docs/lighting-calcs/results-format.md): a small JSON header per calc set and a little-endian float32 body
    /// beside it with the same name and ".bin". It carries the solved calc points, the zones' radiosity patches,
    /// isolux segments, zone statistics and targets, so a viewer (BimGo) can show the same numbers as the report.
    /// Coordinates stay in Revit internal feet (host model) and values in footcandles. Tool-neutral on purpose.
    /// </summary>
    public static class CalcResultsFile
    {
        public const string Format = "lighting-calc-results";
        public const int Version = 1;

        /// <summary>Patches across the whole file; beyond it covered patches, then furniture, then whole zones' meshes are left out.</summary>
        public const int MaxPatches = 1000000;

        /// <summary>Floats per calc point: x, y, z, E, E direct.</summary>
        public const int PointFloats = 5;

        /// <summary>Floats per patch: a, b, c, normal, E, ρ, flags.</summary>
        public const int PatchFloats = 15;

        /// <summary>Floats per isolux segment: a, b.</summary>
        public const int SegmentFloats = 6;

        /// <summary>The sub-folder of a BimGo model folder the files go in.</summary>
        public const string FolderName = "lighting-calcs";

        /// <summary>A short stable id for a calc set: the first 8 hex characters of the SHA-256 of its lower-cased name.</summary>
        public static string SetId(string calcSetName)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((calcSetName ?? string.Empty).Trim().ToLowerInvariant()));
                return BimGoFolders.Hex(hash, 4);
            }
        }

        /// <summary>The file name (no extension) in a BimGo model folder: "&lt;safe name&gt;_&lt;id&gt;".</summary>
        public static string ModelFolderBaseName(string calcSetName) =>
            BimGoFolders.SafeTitle(calcSetName, "Lighting Calc") + "_" + SetId(calcSetName);

        /// <summary>Builds the header and body for a solved calc set.</summary>
        public static CalcResultsData Build(CalcResultsRun run, IReadOnlyList<CalcZone> zones, IReadOnlyList<Luminaire> luminaires)
        {
            var data = new CalcResultsData();
            bool[] keepMesh = PlanPatches(zones, data.Notes, out bool dropCovered, out bool dropFurniture);

            double highest = zones.Where(z => z.Statistics.Count > 0).Select(z => z.Statistics.Maximum).DefaultIfEmpty(0).Max();
            double scaleMax = run.ColorScaleMax > 0
                ? run.ColorScaleMax
                : ToFootcandles(RoomReport.RoundUp(LightingReport.Convert(Math.Max(highest, 1e-6), run.Units)), run.Units);

            var header = new HeaderDto
            {
                Format = Format,
                Version = Version,
                Tool = run.Tool,
                CalcSet = new CalcSetDto { Id = SetId(run.CalcSetName), Name = run.CalcSetName },
                Model = new ModelDto { Title = run.ModelTitle, KeySource = run.KeySource, ProjectInfoUniqueId = run.ProjectInfoUniqueId },
                Run = new RunDto
                {
                    Utc = run.RunUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                    Interreflections = run.Interreflection,
                    Quality = run.Quality,
                    LlfApplied = true,
                    SharedSettings = run.SharedSettings.Count > 0 ? run.SharedSettings.ToList() : null,
                },
                Units = new UnitsDto { Length = "ft", Coordinates = "revit-internal", Illuminance = "fc", Report = LightingReport.UnitLabel(run.Units) },
                Scale = new ScaleDto { Min = 0, Max = Round(scaleMax), Label = "Illuminance" },
            };

            using (var stream = new MemoryStream())
            using (var body = new BinaryWriter(stream))
            {
                for (int zi = 0; zi < zones.Count; zi++)
                {
                    CalcZone zone = zones[zi];
                    var dto = ZoneDto(zone, run.Units);

                    int points = Math.Min(zone.Points.Count, zone.Illuminance.Length);
                    dto.Points = new RangeDto { Offset = stream.Position, Count = points };
                    for (int i = 0; i < points; i++)
                    {
                        Vec3 p = zone.Points[i];
                        WriteVec(body, p);
                        body.Write((float)zone.Illuminance[i]);
                        body.Write((float)(i < zone.Direct.Length ? zone.Direct[i] : double.NaN));
                    }
                    data.Points += points;
                    if (zone.IsMultiLevel && zone.PointLevel.Length >= points)
                    {
                        dto.Levels = zone.Levels.Select(l => new LevelDto { Name = l.Name, Elevation = Round(l.Elevation) }).ToList();
                        dto.PointLevels = zone.PointLevel.Take(points).ToArray();
                    }

                    long patchStart = stream.Position;
                    int patches = 0;
                    if (keepMesh[zi])
                    {
                        foreach (SurfacePatch patch in zone.Surfaces)
                        {
                            if (dropCovered && patch.Covered) continue;
                            if (dropFurniture && patch.Class == SurfaceClass.Furniture) continue;
                            WriteVec(body, patch.A);
                            WriteVec(body, patch.B);
                            WriteVec(body, patch.C);
                            WriteVec(body, patch.Normal);
                            body.Write((float)patch.Illuminance);
                            body.Write((float)patch.Reflectance);
                            body.Write((float)Flags(patch));
                            patches++;
                        }
                    }
                    dto.Patches = new RangeDto { Offset = patchStart, Count = patches };
                    data.Patches += patches;
                    // Optional v1 extension: identity + sRGB hue, indexed exactly like the retained patches.
                    dto.Appearance = new RangeDto { Offset = stream.Position, Count = patches };
                    if (keepMesh[zi]) foreach (SurfacePatch patch in zone.Surfaces)
                    {
                        if (dropCovered && patch.Covered) continue;
                        if (dropFurniture && patch.Class == SurfaceClass.Furniture) continue;
                        body.Write(patch.SourceTag);
                        WriteVec(body, patch.Colour ?? new Vec3(-1, -1, -1));
                    }


                    foreach (IsoluxLine line in IsoluxFor(zone, run.Units))
                    {
                        long start = stream.Position;
                        int segments = 0;
                        foreach (List<Vec3> polyline in line.Polylines)
                        {
                            for (int i = 0; i + 1 < polyline.Count; i++)
                            {
                                WriteVec(body, polyline[i]);
                                WriteVec(body, polyline[i + 1]);
                                segments++;
                            }
                        }
                        if (segments == 0) continue;
                        dto.Isolines.Add(new IsolineDto { Value = Round(line.Level), Target = line.IsTarget, Offset = start, Count = segments });
                        data.IsolineSegments += segments;
                    }

                    header.Zones.Add(dto);
                }

                foreach (Luminaire l in luminaires)
                {
                    header.Luminaires.Add(new LuminaireDto
                    {
                        Id = l.Id,
                        Type = l.TypeName,
                        TypeKey = l.TypeKey,
                        Position = Array3(l.Position),
                        Nadir = Array3(l.Nadir),
                        Axis0 = Array3(l.Axis0),
                        Multiplier = Round(l.Multiplier),
                        Watts = Round(l.Watts),
                    });
                }

                body.Flush();
                data.Body = stream.ToArray();
            }

            header.BinBytes = data.Body.LongLength;
            data.Zones = zones.Count;
            data.Json = Serialize(header);
            return data;
        }

        /// <summary>Restores solved zones for a partial rerun; refuses truncated or incompatible caches.</summary>
        public static List<CalcZone> ReadZones(string jsonPath) => ReadZones(jsonPath, out _);

        /// <summary>Every saved luminaire position (feet, world frame), for marking fixtures on calculated images.</summary>
        public static List<Vec3> ReadLuminairePositions(string jsonPath)
        {
            if (new FileInfo(jsonPath).Length > 64 * 1024 * 1024) throw new InvalidDataException("Results header is too large.");
            HeaderDto header;
            using (var stream = File.OpenRead(jsonPath))
                header = (HeaderDto?)new DataContractJsonSerializer(typeof(HeaderDto)).ReadObject(stream) ?? throw new InvalidDataException("Missing cached lighting results.");
            return header.Luminaires.Where(l => l.Position != null && l.Position.Length == 3)
                .Select(l => new Vec3(l.Position[0], l.Position[1], l.Position[2])).ToList();
        }

        public static List<CalcZone> ReadZones(string jsonPath, out double scaleMax) => ReadZones(jsonPath, out scaleMax, out _);

        /// <summary>
        /// The shared settings in <paramref name="now"/> that differ from <paramref name="before"/>, as
        /// "grid 1 ft → 2 ft". A setting only one of them has counts as changed.
        /// </summary>
        public static List<string> ChangedSettings(IReadOnlyList<string> before, IReadOnlyList<string> now)
        {
            (string Name, string Value) Split(string s)
            {
                int colon = s.IndexOf(": ", StringComparison.Ordinal);
                return colon < 0 ? (s, string.Empty) : (s.Substring(0, colon), s.Substring(colon + 2));
            }
            var old = before.Select(Split).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Value);
            var changes = new List<string>();
            foreach ((string name, string value) in now.Select(Split))
            {
                old.TryGetValue(name, out string? was);
                if (was != value) changes.Add($"{name} {was ?? "(not recorded)"} → {value}");
                old.Remove(name);
            }
            changes.AddRange(old.Select(p => $"{p.Key} {p.Value} → (not set)"));
            return changes;
        }

        /// <summary>Restores solved zones and the run's shared settings (empty for older files).</summary>
        public static List<CalcZone> ReadZones(string jsonPath, out double scaleMax, out List<string> sharedSettings)
        {
            if (new FileInfo(jsonPath).Length > 64 * 1024 * 1024) throw new InvalidDataException("Results header is too large.");
            HeaderDto header;
            using (var stream = File.OpenRead(jsonPath))
                header = (HeaderDto?)new DataContractJsonSerializer(typeof(HeaderDto)).ReadObject(stream) ?? throw new InvalidDataException("Missing cached lighting results.");
            byte[] bytes = File.ReadAllBytes(Path.ChangeExtension(jsonPath, ".bin"));
            if (header.Format != Format || header.Version != Version || header.BinBytes != bytes.LongLength)
                throw new InvalidDataException("Previous lighting results are incompatible or incomplete. Run the complete calc set first.");
            scaleMax = header.Scale.Max;
            sharedSettings = header.Run?.SharedSettings?.ToList() ?? new List<string>();
            var zones = new List<CalcZone>();
            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream))
            {
                Vec3 Vector() => new Vec3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                void Seek(RangeDto range, int stride)
                {
                    if (range == null || range.Count < 0 || range.Offset < 0 || range.Offset > bytes.LongLength - (long)range.Count * stride * 4)
                        throw new InvalidDataException("Invalid cached result range.");
                    stream.Position = range.Offset;
                }
                foreach (ZoneDtoData dto in header.Zones)
                {
                    var zone = new CalcZone { Key = dto.Key, Number = dto.Number, Name = dto.Name, Kind = dto.Kind,
                        LevelName = dto.Level, LevelElevation = dto.LevelElevation, BottomZ = dto.BottomZ, TopZ = dto.TopZ,
                        CeilingZ = dto.CeilingZ, WorkplaneHeight = dto.WorkPlane, GridSpacing = dto.Spacing,
                        OnWalkingSurfaces = dto.OnWalkingSurfaces, FloorArea = dto.FloorArea, LuminaireCount = dto.LuminaireCount, Watts = dto.Watts };
                    foreach (double[][] loop in dto.Loops)
                        zone.Loops.Add(loop.Select(p => new Vec3(p[0], p[1], dto.BottomZ)).ToList());
                    if (dto.Target != null) zone.Target = new LightingTarget { Average = LightingReport.Convert(dto.Target.Avg ?? 0, header.Units.Report == "lx" ? IlluminanceUnits.Lux : IlluminanceUnits.Footcandles),
                        AverageMax = LightingReport.Convert(dto.Target.AvgMax ?? 0, header.Units.Report == "lx" ? IlluminanceUnits.Lux : IlluminanceUnits.Footcandles), AverageToMinimum = dto.Target.AvgMin ?? 0 };
                    Seek(dto.Points, PointFloats);
                    zone.Illuminance = new double[dto.Points.Count]; zone.Direct = new double[dto.Points.Count];
                    for (int i = 0; i < dto.Points.Count; i++)
                    { zone.Points.Add(Vector()); zone.Illuminance[i] = reader.ReadSingle(); zone.Direct[i] = reader.ReadSingle(); }
                    zone.Statistics = ZoneStatistics.From(zone.Illuminance); zone.DirectStatistics = ZoneStatistics.From(zone.Direct);
                    if (dto.Levels != null && dto.Levels.Count > 1 && dto.PointLevels != null && dto.PointLevels.Length == zone.Points.Count &&
                        dto.PointLevels.All(l => l >= 0 && l < dto.Levels.Count))
                    {
                        zone.Levels.AddRange(dto.Levels.Select(l => new ZoneLevel(l.Name, l.Elevation)));
                        zone.PointLevel = dto.PointLevels;
                    }
                    Seek(dto.Patches, PatchFloats);
                    for (int i = 0; i < dto.Patches.Count; i++)
                    {
                        Vec3 a = Vector(), b = Vector(), c = Vector(), normal = Vector();
                        double light = reader.ReadSingle(), reflectance = reader.ReadSingle(); int flags = (int)reader.ReadSingle();
                        zone.Surfaces.Add(new SurfacePatch(new SceneTriangle(a, b, c, (SurfaceClass)((flags >> 1) & 7)),
                            normal, reflectance, light, (flags & 1) != 0));
                    }
                    if (dto.Appearance != null)
                    {
                        if (dto.Appearance.Count != zone.Surfaces.Count) throw new InvalidDataException("Appearance coverage differs from the surface mesh.");
                        Seek(dto.Appearance, 5); // 8-byte source tag + three float32 colour components
                        for (int i = 0; i < zone.Surfaces.Count; i++)
                        {
                            long tag = reader.ReadInt64(); Vec3 colour = Vector();
                            bool absent = colour.X == -1 && colour.Y == -1 && colour.Z == -1;
                            if (!absent && (!LightingImageOptions.Valid(colour) || colour.X < 0 || colour.Y < 0 || colour.Z < 0 || colour.X > 1 || colour.Y > 1 || colour.Z > 1))
                                throw new InvalidDataException("Invalid cached surface colour.");
                            SurfacePatch patch = zone.Surfaces[i]; patch.SourceTag = tag; patch.Colour = absent ? (Vec3?)null : colour;
                            zone.Surfaces[i] = patch;
                        }
                    }
                    if (dto.LuminaireTypes != null)
                    {
                        foreach (TypeCountDto type in dto.LuminaireTypes)
                            if (!string.IsNullOrEmpty(type.TypeKey) && type.Count > 0) zone.LuminaireTypeCounts[type.TypeKey] = type.Count;
                    }
                    // Older files: recount by position (no geometry, so a fixture on the floor above may still count).
                    else foreach (LuminaireDto lum in header.Luminaires)
                    {
                        if (lum.Position.Length != 3 || string.IsNullOrEmpty(lum.TypeKey)) continue;
                        if (!LightingReport.LuminaireInZone(zone, new Vec3(lum.Position[0], lum.Position[1], lum.Position[2]))) continue;
                        zone.LuminaireTypeCounts.TryGetValue(lum.TypeKey, out int count);
                        zone.LuminaireTypeCounts[lum.TypeKey] = count + 1;
                    }
                    zones.Add(zone);
                }
            }
            return zones;
        }

        /// <summary>
        /// Writes "&lt;baseName&gt;.bin" then "&lt;baseName&gt;.json" into <paramref name="folder"/> (created if needed), each through a
        /// temporary file and a replace, so a reader watching the JSON never sees it before its body.
        /// </summary>
        /// <returns>The JSON path.</returns>
        public static string Write(string folder, string baseName, CalcResultsData data)
        {
            Directory.CreateDirectory(folder);
            string json = Path.Combine(folder, baseName + ".json");
            string bin = Path.Combine(folder, baseName + ".bin");
            WriteAtomically(bin, data.Body);
            WriteAtomically(json, new UTF8Encoding(false).GetBytes(data.Json));
            return json;
        }

        /// <summary>"View in BimGo"'s request, read once by BimGo at the start of its next session (within 2 minutes).</summary>
        public const string OpenRequestFile = "open.json";

        /// <summary>
        /// Writes <see cref="OpenRequestFile"/> into a BimGo lighting-calcs folder: the calc set to show, a zone key to start in
        /// (or empty) and the view ("illuminance", "luminance" or "points").
        /// </summary>
        /// <returns>The path written.</returns>
        public static string WriteOpenRequest(string folder, string setId, string? zoneKey, string mode, DateTime utc)
        {
            var request = new OpenRequestDto
            {
                CalcSet = setId ?? string.Empty,
                Zone = zoneKey ?? string.Empty,
                Mode = mode ?? "illuminance",
                Utc = utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            };
            var serializer = new DataContractJsonSerializer(typeof(OpenRequestDto));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, request);
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, OpenRequestFile);
                WriteAtomically(path, stream.ToArray());
                return path;
            }
        }

        /// <summary>The set id at the end of a model-folder file name ("Level 1_d339c539" → "d339c539"), or empty.</summary>
        public static string SetIdOfFileName(string baseName)
        {
            int cut = (baseName ?? string.Empty).LastIndexOf('_');
            return cut >= 0 && baseName!.Length - cut == 9 ? baseName.Substring(cut + 1) : string.Empty;
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                File.Replace(temp, path, null);
            }
            catch (IOException)
            {
                // Some network shares don't support replace: fall back to delete and move.
                File.Delete(path);
                File.Move(temp, path);
            }
        }

        /// <summary>
        /// Which zones keep their mesh, and whether covered patches and furniture are dropped, to stay under
        /// <see cref="MaxPatches"/>. Notes say what was left out.
        /// </summary>
        private static bool[] PlanPatches(IReadOnlyList<CalcZone> zones, List<string> notes, out bool dropCovered, out bool dropFurniture)
        {
            var keep = Enumerable.Repeat(true, zones.Count).ToArray();
            dropCovered = false;
            dropFurniture = false;
            long total = zones.Sum(z => (long)z.Surfaces.Count);
            if (total <= MaxPatches) return keep;

            dropCovered = true;
            total = zones.Sum(z => (long)z.Surfaces.Count(p => !p.Covered));
            notes.Add($"Results file: over {MaxPatches:N0} surface patches; covered patches left out.");
            if (total <= MaxPatches) return keep;

            dropFurniture = true;
            total = zones.Sum(z => (long)z.Surfaces.Count(p => !p.Covered && p.Class != SurfaceClass.Furniture));
            notes.Add("Results file: furniture patches left out too.");
            if (total <= MaxPatches) return keep;

            long running = 0;
            var dropped = new List<string>();
            for (int i = 0; i < zones.Count; i++)
            {
                int count = zones[i].Surfaces.Count(p => !p.Covered && p.Class != SurfaceClass.Furniture);
                if (running + count > MaxPatches)
                {
                    keep[i] = false;
                    dropped.Add(zones[i].DisplayName);
                    continue;
                }
                running += count;
            }
            notes.Add($"Results file: surfaces of {dropped.Count} zone(s) left out (points and statistics kept): {string.Join(", ", dropped.Take(10))}{(dropped.Count > 10 ? ", …" : string.Empty)}.");
            return keep;
        }

        /// <summary>Bit 0 covered, bits 1–3 the surface class (Ceiling 0, Wall 1, Floor 2, Furniture 3).</summary>
        internal static int Flags(SurfacePatch patch) => (patch.Covered ? 1 : 0) | ((int)patch.Class << 1);

        /// <summary>The report's isolux lines for a flat work plane; none for points on walking surfaces.</summary>
        private static List<IsoluxLine> IsoluxFor(CalcZone zone, IlluminanceUnits units)
        {
            if (zone.OnWalkingSurfaces || zone.Statistics.Count == 0) return new List<IsoluxLine>();
            double targetFc = zone.Target != null && zone.Target.Average > 0 ? ToFootcandles(zone.Target.Average, units) : 0;
            List<double> levels = Isolux.Levels(zone.Statistics.Minimum, zone.Statistics.Maximum, units, targetFc);
            return Isolux.Trace(zone, levels, targetFc);
        }

        private static ZoneDtoData ZoneDto(CalcZone zone, IlluminanceUnits units)
        {
            ZoneStatistics s = zone.Statistics;
            var dto = new ZoneDtoData
            {
                Key = zone.Key,
                Number = zone.Number,
                Name = zone.Name,
                Kind = zone.Kind,
                Level = zone.LevelName,
                LevelElevation = Round(zone.LevelElevation),
                BottomZ = Round(zone.BottomZ),
                TopZ = Round(zone.TopZ),
                CeilingZ = Round(double.IsNaN(zone.CeilingZ) ? zone.TopZ : zone.CeilingZ),
                WorkPlane = Round(zone.WorkplaneHeight),
                Spacing = Round(zone.GridSpacing),
                OnWalkingSurfaces = zone.OnWalkingSurfaces,
                FloorArea = Round(zone.FloorArea),
                LuminaireCount = zone.LuminaireCount,
                Watts = Round(zone.Watts),
                // The run's own decision (it can see slabs), so a reread counts exactly what the report did.
                LuminaireTypes = zone.LuminaireTypeCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new TypeCountDto { TypeKey = kv.Key, Count = kv.Value }).ToList(),
                Stats = new StatsDto
                {
                    Count = s.Count,
                    Avg = Round(s.Average),
                    Max = Round(s.Maximum),
                    Min = Round(s.Minimum),
                    AvgMin = Finite(s.AverageToMinimum),
                    MaxMin = Finite(s.MaximumToMinimum),
                    Lpd = Round(zone.PowerDensity),
                },
                Loops = zone.Loops.Select(loop => loop.Select(p => new[] { Round(p.X), Round(p.Y) }).ToArray()).ToArray(),
            };
            if (zone.Target != null && !zone.Target.IsEmpty)
            {
                bool? meets = zone.Target.Meets(s, units);
                dto.Target = new TargetDto
                {
                    Avg = zone.Target.Average > 0 ? Round(ToFootcandles(zone.Target.Average, units)) : (double?)null,
                    AvgMax = zone.Target.AverageMax > 0 ? Round(ToFootcandles(zone.Target.AverageMax, units)) : (double?)null,
                    AvgMin = zone.Target.AverageToMinimum > 0 ? zone.Target.AverageToMinimum : (double?)null,
                    Result = meets == null ? null : meets.Value ? "Meets" : "Misses",
                };
            }
            return dto;
        }

        private static void WriteVec(BinaryWriter w, Vec3 v)
        {
            w.Write((float)v.X);
            w.Write((float)v.Y);
            w.Write((float)v.Z);
        }

        private static double[] Array3(Vec3 v) => new[] { Round(v.X), Round(v.Y), Round(v.Z) };

        private static double ToFootcandles(double value, IlluminanceUnits units) =>
            units == IlluminanceUnits.Lux ? value / LightingReport.LuxPerFootcandle : value;

        /// <summary>Six decimals: a micro-foot, well below float32 precision at building scale.</summary>
        private static double Round(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 6);

        private static double? Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : Math.Round(v, 6);

        private static string Serialize(HeaderDto header)
        {
            var serializer = new DataContractJsonSerializer(typeof(HeaderDto));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, header);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        #region JSON contract

        [DataContract]
        private sealed class HeaderDto
        {
            [DataMember(Name = "format", Order = 1)] public string Format = string.Empty;
            [DataMember(Name = "version", Order = 2)] public int Version;
            [DataMember(Name = "tool", Order = 3)] public string Tool = string.Empty;
            [DataMember(Name = "calcSet", Order = 4)] public CalcSetDto CalcSet = new CalcSetDto();
            [DataMember(Name = "model", Order = 5)] public ModelDto Model = new ModelDto();
            [DataMember(Name = "run", Order = 6)] public RunDto Run = new RunDto();
            [DataMember(Name = "units", Order = 7)] public UnitsDto Units = new UnitsDto();
            [DataMember(Name = "scale", Order = 8)] public ScaleDto Scale = new ScaleDto();
            [DataMember(Name = "binBytes", Order = 9)] public long BinBytes;
            [DataMember(Name = "zones", Order = 10)] public List<ZoneDtoData> Zones = new List<ZoneDtoData>();
            [DataMember(Name = "luminaires", Order = 11)] public List<LuminaireDto> Luminaires = new List<LuminaireDto>();
        }

        [DataContract]
        private sealed class CalcSetDto
        {
            [DataMember(Name = "id", Order = 1)] public string Id = string.Empty;
            [DataMember(Name = "name", Order = 2)] public string Name = string.Empty;
        }

        [DataContract]
        private sealed class ModelDto
        {
            [DataMember(Name = "title", Order = 1)] public string Title = string.Empty;
            [DataMember(Name = "keySource", Order = 2)] public string KeySource = string.Empty;
            [DataMember(Name = "projectInfoUniqueId", Order = 3)] public string ProjectInfoUniqueId = string.Empty;
        }

        [DataContract]
        private sealed class RunDto
        {
            [DataMember(Name = "utc", Order = 1)] public string Utc = string.Empty;
            [DataMember(Name = "interreflections", Order = 2)] public bool Interreflections;
            [DataMember(Name = "quality", Order = 3)] public string Quality = string.Empty;
            [DataMember(Name = "llfApplied", Order = 4)] public bool LlfApplied;
            [DataMember(Name = "sharedSettings", Order = 5, EmitDefaultValue = false)] public List<string>? SharedSettings;
        }

        [DataContract]
        private sealed class UnitsDto
        {
            [DataMember(Name = "length", Order = 1)] public string Length = string.Empty;
            [DataMember(Name = "coordinates", Order = 2)] public string Coordinates = string.Empty;
            [DataMember(Name = "illuminance", Order = 3)] public string Illuminance = string.Empty;
            [DataMember(Name = "report", Order = 4)] public string Report = string.Empty;
        }

        [DataContract]
        private sealed class ScaleDto
        {
            [DataMember(Name = "min", Order = 1)] public double Min;
            [DataMember(Name = "max", Order = 2)] public double Max;
            [DataMember(Name = "label", Order = 3)] public string Label = string.Empty;
        }

        [DataContract]
        private sealed class ZoneDtoData
        {
            [DataMember(Name = "key", Order = 1)] public string Key = string.Empty;
            [DataMember(Name = "number", Order = 2)] public string Number = string.Empty;
            [DataMember(Name = "name", Order = 3)] public string Name = string.Empty;
            [DataMember(Name = "kind", Order = 4)] public string Kind = string.Empty;
            [DataMember(Name = "level", Order = 5)] public string Level = string.Empty;
            [DataMember(Name = "levelElevation", Order = 6)] public double LevelElevation;
            [DataMember(Name = "bottomZ", Order = 7)] public double BottomZ;
            [DataMember(Name = "topZ", Order = 8)] public double TopZ;
            [DataMember(Name = "ceilingZ", Order = 8)] public double CeilingZ;
            [DataMember(Name = "workPlane", Order = 9)] public double WorkPlane;
            [DataMember(Name = "spacing", Order = 10)] public double Spacing;
            [DataMember(Name = "onWalkingSurfaces", Order = 11)] public bool OnWalkingSurfaces;
            [DataMember(Name = "floorArea", Order = 12)] public double FloorArea;
            [DataMember(Name = "luminaireCount", Order = 13)] public int LuminaireCount;
            [DataMember(Name = "watts", Order = 14)] public double Watts;
            [DataMember(Name = "stats", Order = 15)] public StatsDto Stats = new StatsDto();
            [DataMember(Name = "target", Order = 16, EmitDefaultValue = false)] public TargetDto? Target;
            [DataMember(Name = "loops", Order = 17)] public double[][][] Loops = Array.Empty<double[][]>();
            [DataMember(Name = "points", Order = 18)] public RangeDto Points = new RangeDto();
            [DataMember(Name = "patches", Order = 19)] public RangeDto Patches = new RangeDto();
            [DataMember(Name = "appearance", Order = 21, EmitDefaultValue = false)] public RangeDto? Appearance;
            [DataMember(Name = "luminaireTypes", Order = 22, EmitDefaultValue = false)] public List<TypeCountDto>? LuminaireTypes;
            /// <summary>A zone spanning levels: its levels and each point's level index, so plan values can be placed again per level.</summary>
            [DataMember(Name = "levels", Order = 23, EmitDefaultValue = false)] public List<LevelDto>? Levels;
            [DataMember(Name = "pointLevels", Order = 24, EmitDefaultValue = false)] public int[]? PointLevels;
            [DataMember(Name = "isolines", Order = 20)] public List<IsolineDto> Isolines = new List<IsolineDto>();
        }

        [DataContract]
        private sealed class LevelDto
        {
            [DataMember(Name = "name", Order = 1)] public string Name = string.Empty;
            [DataMember(Name = "elevation", Order = 2)] public double Elevation;
        }

        [DataContract]
        private sealed class StatsDto
        {
            [DataMember(Name = "count", Order = 1)] public int Count;
            [DataMember(Name = "avg", Order = 2)] public double Avg;
            [DataMember(Name = "max", Order = 3)] public double Max;
            [DataMember(Name = "min", Order = 4)] public double Min;
            [DataMember(Name = "avgMin", Order = 5, EmitDefaultValue = false)] public double? AvgMin;
            [DataMember(Name = "maxMin", Order = 6, EmitDefaultValue = false)] public double? MaxMin;
            [DataMember(Name = "lpd", Order = 7)] public double Lpd;
        }

        [DataContract]
        private sealed class TargetDto
        {
            [DataMember(Name = "avg", Order = 1, EmitDefaultValue = false)] public double? Avg;
            [DataMember(Name = "avgMax", Order = 2, EmitDefaultValue = false)] public double? AvgMax;
            [DataMember(Name = "avgMin", Order = 3, EmitDefaultValue = false)] public double? AvgMin;
            [DataMember(Name = "result", Order = 4, EmitDefaultValue = false)] public string? Result;
        }

        [DataContract]
        private sealed class RangeDto
        {
            [DataMember(Name = "offset", Order = 1)] public long Offset;
            [DataMember(Name = "count", Order = 2)] public int Count;
        }

        [DataContract]
        private sealed class TypeCountDto
        {
            [DataMember(Name = "typeKey", Order = 1)] public string TypeKey = string.Empty;
            [DataMember(Name = "count", Order = 2)] public int Count;
        }

        [DataContract]
        private sealed class IsolineDto
        {
            [DataMember(Name = "value", Order = 1)] public double Value;
            [DataMember(Name = "target", Order = 2)] public bool Target;
            [DataMember(Name = "offset", Order = 3)] public long Offset;
            [DataMember(Name = "count", Order = 4)] public int Count;
        }

        [DataContract]
        private sealed class OpenRequestDto
        {
            [DataMember(Name = "calcSet", Order = 1)] public string CalcSet = string.Empty;
            [DataMember(Name = "zone", Order = 2)] public string Zone = string.Empty;
            [DataMember(Name = "mode", Order = 3)] public string Mode = string.Empty;
            [DataMember(Name = "utc", Order = 4)] public string Utc = string.Empty;
        }

        [DataContract]
        private sealed class LuminaireDto
        {
            [DataMember(Name = "id", Order = 1)] public long Id;
            [DataMember(Name = "type", Order = 2)] public string Type = string.Empty;
            [DataMember(Name = "typeKey", Order = 3)] public string TypeKey = string.Empty;
            [DataMember(Name = "position", Order = 4)] public double[] Position = Array.Empty<double>();
            [DataMember(Name = "nadir", Order = 5)] public double[] Nadir = Array.Empty<double>();
            [DataMember(Name = "axis0", Order = 6)] public double[] Axis0 = Array.Empty<double>();
            [DataMember(Name = "multiplier", Order = 7)] public double Multiplier;
            [DataMember(Name = "watts", Order = 8)] public double Watts;
        }

        #endregion
    }
}
