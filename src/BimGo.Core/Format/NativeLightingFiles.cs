using System.Text.Json;

namespace BimGo.Format;

/// <summary>Native calculator definitions. Inputs/results are separate optional entries; imported runs stay unchanged.</summary>
public sealed class NativeLightingDocument
{
    public int Version { get; set; } = 1;
    public string Selected { get; set; }
    public List<NativeLightingSet> Sets { get; set; } = new();
}

/// <summary>All lengths are feet, independently of the display preference.</summary>
public sealed class NativeLightingSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "BimGo calculation";
    public int[] Rooms { get; set; } = Array.Empty<int>();
    public string[] RoomKeys { get; set; } = Array.Empty<string>();
    public double Workplane { get; set; } = 2.5;
    public double Spacing { get; set; } = 2;
    public double BoundaryOffset { get; set; } = 1;
    public double Margin { get; set; } = 15;
    public double Ceiling { get; set; } = .8;
    public double Wall { get; set; } = .5;
    public double Floor { get; set; } = .2;
    public double Furniture { get; set; } = .5;
    public double LightLossFactor { get; set; } = .85;
    public int Quality { get; set; } = 1;
    public bool Interreflection { get; set; } = true;
    public bool AllowEstimatedPhotometry { get; set; }
    public bool CorrectRecessedSources { get; set; }
    public bool WalkingSurfaces { get; set; }
    public bool Automatic { get; set; } = true;

    public bool EmergencyOnly { get; set; }
    public bool AutoExcludeFurniture { get; set; } = true;
    public NativeLuminaireSetting[] Luminaires { get; set; } = Array.Empty<NativeLuminaireSetting>();
    public string[] ExcludedElements { get; set; } = Array.Empty<string>();
    public string[] ExcludedCategories { get; set; } = Array.Empty<string>();
    public NativeExcludedPoint[] ExcludedPoints { get; set; } = Array.Empty<NativeExcludedPoint>();

    public void Validate()
    {
        if (Id == null || Id.Length != 32 || !Id.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid native set identity.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new InvalidDataException("Name must contain 1–100 characters.");
        if (Rooms == null || Rooms.Length == 0 || Rooms.Length > 32 || Rooms.Any(i => i < 0) || Rooms.Distinct().Count() != Rooms.Length)
            throw new InvalidDataException("Select 1–32 distinct exported rooms.");
        if (RoomKeys == null || (RoomKeys.Length != 0 && (RoomKeys.Length != Rooms.Length || RoomKeys.Any(string.IsNullOrWhiteSpace))))
            throw new InvalidDataException("Invalid saved room identities.");
        Range(Workplane, 0, 30, "workplane"); Range(Spacing, .25, 50, "spacing"); Range(BoundaryOffset, 0, 20, "boundary offset");
        Range(Margin, 0, 100, "domain margin"); Range(Ceiling, 0, .95, "ceiling reflectance"); Range(Wall, 0, .95, "wall reflectance");
        Range(Floor, 0, .95, "floor reflectance"); Range(Furniture, 0, .95, "furniture reflectance"); Range(LightLossFactor, 0, 1, "LLF");
        if (Luminaires == null || ExcludedElements == null || ExcludedCategories == null || ExcludedPoints == null
            || Luminaires.Length > 10000 || ExcludedElements.Length > 100000 || ExcludedPoints.Length > 100000)
            throw new InvalidDataException("Invalid calculation selections.");
        foreach (var lum in Luminaires)
        {
            if (lum == null || string.IsNullOrWhiteSpace(lum.Key)) throw new InvalidDataException("Missing fixture identity.");
            Range(lum.EmergencyOutput, 0, 1, "emergency output"); Range(lum.HeightOffset, -100, 100, "source height offset");
        }
        if (Luminaires.Select(l => l.Key).Distinct().Count() != Luminaires.Length) throw new InvalidDataException("Duplicate fixture settings.");
        foreach (var point in ExcludedPoints)
            if (point == null || string.IsNullOrWhiteSpace(point.ZoneKey) || point.Position == null || point.Position.Length != 3 || point.Position.Any(v => !double.IsFinite(v)))
                throw new InvalidDataException("Invalid excluded calculation point.");
        if (ExcludedElements.Any(string.IsNullOrWhiteSpace) || ExcludedCategories.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid geometry exclusion.");
        if (Quality < 0 || Quality > 2) throw new InvalidDataException("Unsupported calculation quality.");
    }
    private static void Range(double v, double min, double max, string name)
    { if (!double.IsFinite(v) || v < min || v > max) throw new InvalidDataException($"Invalid {name}: expected {min}–{max}."); }
}

/// <summary>Instance-specific emergency participation and output, independent of type/preview choices.</summary>
public sealed class NativeLuminaireSetting
{
    public string Key { get; set; }
    public bool Emergency { get; set; }
    public double EmergencyOutput { get; set; } = 1;
    public double HeightOffset { get; set; }
    public bool UsePreviewLensPosition { get; set; }
}
public sealed class NativeExcludedPoint
{
    public string ZoneKey { get; set; }
    public double[] Position { get; set; }
}

public static class NativeLightingFiles
{
    public const string Entry = "native-lighting.json";
    public const string ScenePrefix = "lighting-scenes/";
    public const int MaxBytes = 4 * 1024 * 1024;
    public static string SidecarFor(string comments) => SidecarJson.BesideComments(comments, ".bimgo-native-lighting.json");
    public static NativeLightingDocument Parse(byte[] bytes, out string error)
    {
        error = null;
        if (bytes == null) return new();
        try
        {
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Native calculator setup is too large.");
            var doc = JsonSerializer.Deserialize<NativeLightingDocument>(bytes, BimGoFormat.JSON_INDENTED) ?? throw new InvalidDataException("Empty calculator setup.");
            if (doc.Version != 1) throw new InvalidDataException($"Unsupported native calculator version {doc.Version}.");
            if (doc.Sets == null || doc.Sets.Count > 100) throw new InvalidDataException("Invalid calculator set list.");
            foreach (var set in doc.Sets) { if (set == null) throw new InvalidDataException("Null calculator set."); set.Validate(); }
            if (doc.Sets.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != doc.Sets.Count) throw new InvalidDataException("Duplicate calculator set identity.");
            return doc;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }
    public static byte[] Serialize(NativeLightingDocument doc) => JsonSerializer.SerializeToUtf8Bytes(doc, BimGoFormat.JSON_INDENTED);
    public static NativeLightingSet Copy(NativeLightingSet set) => JsonSerializer.Deserialize<NativeLightingSet>(JsonSerializer.Serialize(set, BimGoFormat.JSON_INDENTED), BimGoFormat.JSON_INDENTED);
    public static bool Write(string path, NativeLightingDocument doc, out string error) => SidecarJson.Write(path, doc, "Native lighting", out error);
    public static NativeLightingDocument Read(string path, out string error)
    {
        error = null;
        try { return path != null && File.Exists(path) ? Parse(File.ReadAllBytes(path), out error) : new(); }
        catch (Exception ex) { error = ex.Message; return null; }
    }
    public static bool IsSceneEntry(string name) => name != null && name.StartsWith(ScenePrefix, StringComparison.Ordinal)
        && name.EndsWith(".json", StringComparison.Ordinal) && name.Length == ScenePrefix.Length + 37
        && name.Substring(ScenePrefix.Length, 32).All(Uri.IsHexDigit);
}
