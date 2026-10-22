using System.Security.Cryptography;
using System.Text.Json;
using BimGo.Format;
using Lighting.Engine;

namespace BimGo.Calculations;

/// <summary>Versioned, self-contained diffuse input. No host API or renderer is needed to replay it.</summary>
public sealed class CalculationInput
{
    public int Version { get; set; } = 1;
    public string LengthUnit { get; set; } = "ft";
    public string CoordinateFrame { get; set; } = "revit-internal";
    public string PhysicalModel { get; set; } = EngineIdentity.PhysicalModel;
    public string EngineVersion { get; set; } = EngineIdentity.Version;
    public string EngineCommit { get; set; } = EngineIdentity.UpstreamCommit;
    public string Model { get; set; }
    public string GeometryBasis { get; set; } = "BimGo exported model geometry; extraction may omit or simplify elements";
    public NativeLightingSet Set { get; set; }
    public CalculationSolverSettings Solver { get; set; }
    public string SamplingPolicy { get; set; } = "wwtools-index-hammersley-v1";
    public List<CalculationZone> Zones { get; set; } = new();
    public List<CalculationTriangle> Triangles { get; set; } = new();
    public List<CalculationEmitter> Emitters { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool SourcesResolved { get; set; }
    public List<PreservedCalculationZone> PreservedZones { get; set; } = new();
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Json);
    public string ContentHash() => Convert.ToHexString(SHA256.HashData(Serialize())).ToLowerInvariant();
    public static CalculationInput Parse(byte[] bytes)
    {
        if (bytes == null || bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Calculation scene exceeds 64 MB or is missing.");
        var input = JsonSerializer.Deserialize<CalculationInput>(bytes, Json) ?? throw new InvalidDataException("Empty calculation scene.");
        input.Validate(); return input;
    }
    public void Validate()
    {
        if (Version != 1 || LengthUnit != "ft" || CoordinateFrame != "revit-internal" || PhysicalModel != EngineIdentity.PhysicalModel
            || EngineVersion != EngineIdentity.Version || EngineCommit != EngineIdentity.UpstreamCommit)
            throw new InvalidDataException("Unsupported calculation scene units, engine, or physical model.");
        if (Set == null) throw new InvalidDataException("Missing calculation set."); Set.Validate();
        if (Solver == null || SamplingPolicy != "wwtools-index-hammersley-v1") throw new InvalidDataException("Missing solver settings or unsupported sampling policy.");
        Solver.Validate();
        if (Solver.Interreflection != Set.Interreflection) throw new InvalidDataException("Inconsistent interreflection settings.");
        if (Zones == null || Zones.Count == 0 || Zones.Count > 32 || Triangles == null || Triangles.Count == 0 || Triangles.Count > 250000
            || Emitters == null || Emitters.Count == 0 || Emitters.Count > 10000) throw new InvalidDataException("Incomplete or oversized physical scene.");
        foreach (var zone in Zones)
        {
            if (zone == null || string.IsNullOrWhiteSpace(zone.Key) || zone.Loops == null || zone.Loops.Length == 0
                || !double.IsFinite(zone.Bottom) || !double.IsFinite(zone.Top) || zone.Top <= zone.Bottom)
                throw new InvalidDataException("Invalid calculation zone.");
            foreach (var loop in zone.Loops) { if (loop == null || loop.Length < 6 || loop.Length % 2 != 0 || loop.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid room boundary."); }
        }
        foreach (var triangle in Triangles)
            if (triangle == null || triangle.Vertices == null || triangle.Vertices.Length != 9 || triangle.Vertices.Any(v => !double.IsFinite(v))
                || triangle.Class < 0 || triangle.Class > 3 || !double.IsFinite(triangle.Reflectance) || triangle.Reflectance < -1 || triangle.Reflectance > .95)
                throw new InvalidDataException("Invalid physical triangle.");
        if (PreservedZones == null || PreservedZones.Count > Zones.Count || PreservedZones.Select(z => z?.Definition?.Key).Distinct().Count() != PreservedZones.Count)
            throw new InvalidDataException("Invalid retained room list.");
        foreach (var saved in PreservedZones)
        {
            if (saved == null) throw new InvalidDataException("Missing retained room result.");
            saved.Validate();
            if (!Zones.Any(z => z.Key == saved.Definition.Key)) throw new InvalidDataException("Retained result is outside the calculation set.");
        }
        foreach (var emitter in Emitters)
        {
            if (emitter == null || string.IsNullOrWhiteSpace(emitter.Key) || string.IsNullOrWhiteSpace(emitter.Provenance) || !double.IsFinite(emitter.Multiplier) || emitter.Multiplier < 0 || emitter.Multiplier > 1
                || !double.IsFinite(emitter.Lumens) || emitter.Lumens < 0 || emitter.Lumens > 1e8
                || !double.IsFinite(emitter.Downward) || emitter.Downward < 0 || emitter.Downward > 1)
                throw new InvalidDataException("Invalid source output.");
            foreach (var vector in new[] { emitter.Position, emitter.Nadir, emitter.Axis0 })
                if (vector == null || vector.Length != 3 || vector.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid source frame.");
            if (emitter.Nadir.Sum(v => v*v) < .9 || emitter.Axis0.Sum(v => v*v) < .9) throw new InvalidDataException("Missing source orientation.");
            if (emitter.Ies != null && emitter.Ies.Length > 512*1024) throw new InvalidDataException("Source photometry exceeds 512 KB.");
            if (emitter.Estimated && !Set.AllowEstimatedPhotometry) throw new InvalidDataException("Estimated sources require Allow estimated photometry.");
            if (!emitter.Estimated && string.IsNullOrWhiteSpace(emitter.Ies)) throw new InvalidDataException("Missing full source photometry.");
        }
    }
}

public sealed class CalculationSolverSettings
{
    public bool Interreflection { get; set; }
    public double PatchSize { get; set; }
    public int PatchRays { get; set; }
    public int GatherRays { get; set; }
    public int MaxIterations { get; set; }
    public double Convergence { get; set; }
    public int MaxPatches { get; set; }
    public static CalculationSolverSettings From(WWTools.Lighting.Engine.SolverOptions o) => new()
    { Interreflection=o.Interreflection,PatchSize=o.PatchSize,PatchRays=o.PatchRays,GatherRays=o.GatherRays,MaxIterations=o.MaxIterations,Convergence=o.Convergence,MaxPatches=o.MaxPatches };
    public WWTools.Lighting.Engine.SolverOptions ToOptions() => new()
    { Interreflection=Interreflection,PatchSize=PatchSize,PatchRays=PatchRays,GatherRays=GatherRays,MaxIterations=MaxIterations,Convergence=Convergence,MaxPatches=MaxPatches };
    public void Validate()
    {
        if(!double.IsFinite(PatchSize)||PatchSize<.25||PatchSize>100||PatchRays<8||PatchRays>2048||GatherRays<16||GatherRays>8192
            ||MaxIterations<1||MaxIterations>5000||!double.IsFinite(Convergence)||Convergence<=0||Convergence>.1||MaxPatches<1||MaxPatches>500000)
            throw new InvalidDataException("Invalid or unsupported solver quality/convergence settings.");
    }
}

public sealed class CalculationZone
{
    public string Key { get; set; }
    public string Number { get; set; }
    public string Name { get; set; }
    public string Level { get; set; }
    public double Bottom { get; set; }
    public double Top { get; set; }
    public double[][] Loops { get; set; }
    public double[] LevelElevations { get; set; } = Array.Empty<double>();
    public string[] LevelNames { get; set; } = Array.Empty<string>();
}
public sealed class CalculationTriangle
{
    public double[] Vertices { get; set; }
    public int Class { get; set; }
    public double Reflectance { get; set; } = -1;
    public long Tag { get; set; }
    public string SurfaceKey { get; set; }
    public bool Walkable { get; set; }
    public bool Stair { get; set; }
}
public sealed class CalculationEmitter
{
    public string Key { get; set; }
    public string Type { get; set; }
    public string FixtureKey { get; set; }
    public double[] Position { get; set; }
    public double[] Nadir { get; set; }
    public double[] Axis0 { get; set; }
    public string Ies { get; set; }
    public bool Estimated { get; set; }
    public string Provenance { get; set; }
    public double Lumens { get; set; }
    public double Downward { get; set; }
    public double Multiplier { get; set; }
    public double Watts { get; set; }
    public double[] OriginalPosition { get; set; }
}

/// <summary>Physical changes only. Presentation visibility, camera and section state are deliberately absent.</summary>
public sealed class CalculationEdits
{
    public bool[] Removed { get; init; } = Array.Empty<bool>();
    public CalculationInstance[] Instances { get; init; } = Array.Empty<CalculationInstance>();
}
public sealed record CalculationInstance(int Element, int InstanceId, bool IsClone, System.Numerics.Matrix4x4 Transform);
