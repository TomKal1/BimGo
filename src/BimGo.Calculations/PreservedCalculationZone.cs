using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace BimGo.Calculations;

/// <summary>A retained room result and its original revision, embedded so a partial run can replay offline.</summary>
public sealed class PreservedCalculationZone
{
    public bool Complete { get; set; }
    public string InputRevision { get; set; }
    public CalculationZone Definition { get; set; }
    public double Workplane { get; set; }
    public double Spacing { get; set; }
    public double FloorArea { get; set; }
    public double? Ceiling { get; set; }
    public bool WalkingSurfaces { get; set; }
    public int LuminaireCount { get; set; }
    public double Watts { get; set; }
    public Dictionary<string, int> Types { get; set; } = new();
    public double[][] Points { get; set; } = Array.Empty<double[]>();
    public double?[] Values { get; set; } = Array.Empty<double?>();
    public double?[] Direct { get; set; } = Array.Empty<double?>();
    // a,b,c,normal,reflectance,illuminance,class,covered (16 doubles).
    public double[][] Patches { get; set; } = Array.Empty<double[]>();
    public SolveStats Diagnostics { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double[][] Appearance { get; set; }

    public static PreservedCalculationZone From(NativeCalculationRun run, int index)
    {
        var zone = run.Zones[index];
        return new PreservedCalculationZone
        {
            Complete = run.Input.PreservedZones.FirstOrDefault(p => p.Definition.Key == zone.Key)?.Complete
                ?? (!run.Input.Solver.Interreflection || run.Statistics[index].Iterations < run.Input.Solver.MaxIterations),
            InputRevision = run.ZoneRevisions != null && run.ZoneRevisions.TryGetValue(zone.Key, out var revision) ? revision : run.ScenarioHash,
            Definition = run.Input.Zones.Single(z => z.Key == zone.Key), Workplane = zone.WorkplaneHeight, Spacing = zone.GridSpacing,
            FloorArea = zone.FloorArea, Ceiling = double.IsFinite(zone.CeilingZ) ? zone.CeilingZ : null, WalkingSurfaces = zone.OnWalkingSurfaces,
            LuminaireCount = zone.LuminaireCount, Watts = zone.Watts, Types = new(zone.LuminaireTypeCounts),
            Points = zone.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            Values = zone.Illuminance.Select(v => double.IsFinite(v) ? (double?)v : null).ToArray(),
            Direct = zone.Direct.Select(v => double.IsFinite(v) ? (double?)v : null).ToArray(),
            Appearance = zone.Surfaces.Select(p => new[] { (double)p.SourceTag, p.Colour?.X ?? -1, p.Colour?.Y ?? -1, p.Colour?.Z ?? -1 }).ToArray(),
            Patches = zone.Surfaces.Select(p => new[] { p.A.X,p.A.Y,p.A.Z,p.B.X,p.B.Y,p.B.Z,p.C.X,p.C.Y,p.C.Z,p.Normal.X,p.Normal.Y,p.Normal.Z,
                p.Reflectance,p.Illuminance,(double)p.Class,p.Covered?1:0 }).ToArray(), Diagnostics = run.Statistics[index]
        };
    }
    public void Validate()
    {
        if (Definition == null || string.IsNullOrWhiteSpace(Definition.Key) || InputRevision == null || InputRevision.Length != 64 || !InputRevision.All(Uri.IsHexDigit)
            || Points == null || Values == null || Direct == null || Patches == null || Diagnostics == null || Types == null
            || Points.Length != Values.Length || Points.Length != Direct.Length || Points.Length > 100000 || Patches.Length > 500000
            || !double.IsFinite(Workplane) || !double.IsFinite(Spacing) || !double.IsFinite(FloorArea) || !double.IsFinite(Watts))
            throw new InvalidDataException("Invalid retained room result.");
        if (Definition.Loops == null || Definition.Loops.Length == 0 || !double.IsFinite(Definition.Bottom) || !double.IsFinite(Definition.Top)
            || Definition.Top <= Definition.Bottom || Definition.Loops.Any(loop => loop == null || loop.Length < 6 || loop.Length % 2 != 0 || loop.Any(v => !double.IsFinite(v))))
            throw new InvalidDataException("Invalid retained room boundary.");
        foreach (var point in Points) if (point == null || point.Length != 3 || point.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid retained point.");
        foreach (var patch in Patches) if (patch == null || patch.Length != 16 || patch.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid retained surface.");
        if (Appearance != null && (Appearance.Length != Patches.Length || Appearance.Any(a => a == null || a.Length != 4 || a.Any(v => !double.IsFinite(v)) ||
            !((a[1] == -1 && a[2] == -1 && a[3] == -1) || (a.Skip(1).All(v => v >= 0 && v <= 1)))))) throw new InvalidDataException("Invalid retained surface appearance.");
        if (Values.Concat(Direct).Any(v => v.HasValue && !double.IsFinite(v.Value))) throw new InvalidDataException("Invalid retained illuminance.");
    }
    public CalcZone Restore()
    {
        Validate(); var d = Definition;
        var zone = new CalcZone { Key=d.Key,Number=d.Number??"",Name=d.Name??"",LevelName=d.Level??"",Kind="Room",BottomZ=d.Bottom,TopZ=d.Top,
            WorkplaneHeight=Workplane,GridSpacing=Spacing,FloorArea=FloorArea,CeilingZ=Ceiling??double.NaN,OnWalkingSurfaces=WalkingSurfaces,LuminaireCount=LuminaireCount,Watts=Watts };
        foreach (var loop in d.Loops)
        { var points=new List<Vec3>();for(int i=0;i<loop.Length;i+=2)points.Add(new(loop[i],loop[i+1],d.Bottom));zone.Loops.Add(points); }
        foreach (var p in Points) zone.Points.Add(new(p[0],p[1],p[2]));
        zone.Illuminance=Values.Select(v=>v??double.NaN).ToArray();zone.Direct=Direct.Select(v=>v??double.NaN).ToArray();
        zone.Statistics=ZoneStatistics.From(zone.Illuminance);zone.DirectStatistics=ZoneStatistics.From(zone.Direct);
        foreach(var pair in Types)zone.LuminaireTypeCounts[pair.Key]=pair.Value;
        Vec3 V(double[] p,int i)=>new(p[i],p[i+1],p[i+2]);
        foreach(var p in Patches)zone.Surfaces.Add(new SurfacePatch(new SceneTriangle(V(p,0),V(p,3),V(p,6),(SurfaceClass)(int)p[14]),V(p,9),p[12],p[13],p[15]!=0));
        if (Appearance != null) for (int i=0;i<zone.Surfaces.Count;i++)
        {
            var a=Appearance[i];var p=zone.Surfaces[i];p.SourceTag=(long)a[0];p.Colour=a[1]<0?(Vec3?)null:new Vec3(a[1],a[2],a[3]);zone.Surfaces[i]=p;
        }
        return zone;
    }
}
