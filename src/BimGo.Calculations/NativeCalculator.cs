using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BimGo.Format;
using BimGo.Scene;
using Lighting.Engine;
using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace BimGo.Calculations;

public sealed class NativeCalculationRun
{
    public string ScenarioHash { get; init; }
    public bool Complete { get; init; }
    public DateTime RunUtc { get; init; }
    public CalculationInput Input { get; init; }
    public LightingCalcResult Display { get; init; }
    public byte[] Header { get; init; }
    public byte[] Body { get; init; }
    public byte[] SceneBytes { get; init; }
    public string PointCsv { get; init; }
    public string ReportHtml { get; init; }
    public byte[] StructuredResults { get; init; }
    public IReadOnlyList<CalcZone> Zones { get; init; }
    public IReadOnlyDictionary<string, string> ZoneRevisions { get; init; }
    public IReadOnlyList<SolveStats> Statistics { get; init; }
    internal LightingReportData ReportData { get; init; }
    public string HtmlReport(double scaleMax, bool lux = false) => NativeLightingReport.Html(ReportData,Statistics,scaleMax,lux,Input.Emitters,includeImages:true);
}

/// <summary>Runs the pinned diffuse kernel entirely offline. Call on a worker with owned immutable input.</summary>
public static class NativeCalculator
{
    public static NativeCalculationRun Run(CalculationInput request, Vector3 displayOrigin, Action<string> progress = null, CancellationToken cancel = default, NativeCalculationRun previous = null, IReadOnlyCollection<string> zoneKeys = null)
    {
        // Own every mutable input used by the kernel; callers cannot change an in-flight revision.
        if(request==null)throw new InvalidDataException("Missing calculation input.");
        request.Validate();
        var input = CalculationInput.Parse(request.Serialize());
        if (zoneKeys != null)
        {
            if (previous == null || previous.Input.Set.Id != input.Set.Id || zoneKeys.Count == 0 || zoneKeys.Any(k => !input.Zones.Any(z => z.Key == k)))
                throw new InvalidDataException("Run the complete set before rerunning selected rooms, and select rooms belonging to that set.");
            input.PreservedZones = input.Zones.Where(z => !zoneKeys.Contains(z.Key)).Select(room =>
            {
                int old = previous.Zones.ToList().FindIndex(z => z.Key == room.Key);
                if (old < 0) throw new InvalidDataException("An unselected room has no cached result; run the complete set first.");
                return PreservedCalculationZone.From(previous, old);
            }).ToList();
        }
        cancel.ThrowIfCancellationRequested(); progress?.Invoke("Preparing physical geometry and photometry");
        var triangles = input.Triangles.Select(t => new SceneTriangle(V(t.Vertices,0),V(t.Vertices,3),V(t.Vertices,6),(SurfaceClass)t.Class,t.Reflectance,t.Tag,t.Colour == null ? (Vec3?)null : V(t.Colour,0))).ToList();
        var options = input.Solver.ToOptions();
        // MaxPatches cannot reduce the mesh below its input triangle count. Preserve the physical mesh;
        // budget its transport storage instead of rejecting ordinary detailed BIM exports at 60k.
        if(options.MaxPatches<triangles.Count)throw new InvalidDataException("The input mesh exceeds the declared patch budget; choose a reproducible larger domain budget.");
        long transportBudget=4L*options.MaxPatches*Math.Max(8,options.PatchRays);
        if(transportBudget>256L*1024*1024)throw new InvalidDataException("This quality/domain needs more than 256 MB of form-factor storage. Select fewer rooms or a lower quality.");
        var photometry = new Dictionary<string,IesPhotometry>(StringComparer.Ordinal);
        var luminaires = new List<Luminaire>();
        TriangleScene placementScene = input.Set.CorrectRecessedSources && !input.SourcesResolved ? new TriangleScene(triangles) : null;
        int corrections = 0;
        for (int i=0;i<input.Emitters.Count;i++)
        {
            cancel.ThrowIfCancellationRequested(); var source = input.Emitters[i];
            if (source.Ies == null)
            {
                // Preserve the complete resolved analytic table, never a compact GPU profile.
                Func<double,double> shape = a => (1-source.Downward) + 4*source.Downward*Math.Max(0,Math.Cos(a*Math.PI/180));
                if (source.Provenance.StartsWith("Explicit analytic spot estimate",StringComparison.Ordinal))
                {
                    var parts=source.Provenance.Split(';');
                    double beam=double.Parse(parts[1].Split('=')[1],CultureInfo.InvariantCulture), field=double.Parse(parts[2].Split('=')[1],CultureInfo.InvariantCulture);
                    double inner=beam/2, outer=Math.Max(field/2,inner+2);
                    shape=a=>a<=inner?1:a>=outer?0:Math.Pow((outer-a)/(outer-inner),2)*(3-2*(outer-a)/(outer-inner));
                }
                source.Ies = AnalyticIes(shape,source.Lumens);
            }
            if (!photometry.TryGetValue(source.Ies, out var ies))
            {
                ies=IesPhotometry.Parse(source.Ies);
                if (!double.IsFinite(ies.ComputedLumens) || !double.IsFinite(ies.MaxCandela) || ies.MaxCandela < 0 || ies.MaxCandela > 1e10
                    || !double.IsFinite(ies.InputWatts) || ies.InputWatts < 0 || ies.InputWatts > 1e8
                    || !Ordered(ies.VerticalAngles) || !Ordered(ies.HorizontalAngles)) throw new InvalidDataException($"Invalid full IES distribution: {source.Type}");
                photometry.Add(source.Ies,ies);
                if (ies.HadTiltData) input.Warnings.Add($"IES tilt factors are ignored by the {EngineIdentity.PhysicalModel} compatibility model ({source.Type}).");
            }
            var position=V(source.Position,0);
            if (placementScene != null && source.Nadir[2] < -.7)
            {
                double? ceiling=LuminairePlacement.CeilingUndersideBelow(placementScene,position,1);
                if (ceiling.HasValue && position.Z >= ceiling.Value-.05)
                {
                    source.OriginalPosition=(double[])source.Position.Clone();
                    source.Position=new[] {position.X,position.Y,ceiling.Value-1.0/12}; position=V(source.Position,0); corrections++;
                }
            }
            luminaires.Add(new Luminaire(position,V(source.Nadir,0),V(source.Axis0,0),ies)
            { Id=i+1, TypeKey=source.Type??source.Key,TypeName=source.Type??source.Key,Multiplier=source.Multiplier,Watts=ies.InputWatts });
        }
        if (corrections>0) input.Warnings.Add($"{corrections} recessed source(s) corrected to 1 inch below a nearby ceiling; original and resolved positions are retained.");
        input.SourcesResolved=true;
        input.Warnings=input.Warnings.Distinct(StringComparer.Ordinal).ToList();
        input.Validate(); string hash=input.ContentHash(); byte[] sceneBytes=input.Serialize();
        if (sceneBytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("The retained calculation input exceeds 64 MB. Run the complete set or reduce the selected domain.");
        var zones=new List<CalcZone>(); var stats=new List<SolveStats>(); bool complete=true; int pointBudget=0;
        TriangleScene walking=input.Set.WalkingSurfaces?new TriangleScene(triangles):null;
        var walkable=input.Triangles.Where(t=>t.Walkable).Select(t=>t.Tag).ToHashSet(); var stairs=input.Triangles.Where(t=>t.Stair).Select(t=>t.Tag).ToHashSet();
        var runNotes = new List<string>();
        var zoneRevisions = new Dictionary<string, string>();
        if (input.PreservedZones.Count > 0)
        {
            runNotes.Add("Partial rerun: unselected rooms retain previously calculated values and their own input revision hashes. Rerun the entire set after shared changes affecting other rooms.");
            complete = input.PreservedZones.All(p => p.Complete);
        }
        foreach (var room in input.Zones)
        {
            var preserved = input.PreservedZones.FirstOrDefault(p => p.Definition.Key == room.Key);
            if (preserved != null)
            {
                pointBudget += preserved.Points.Length;
                if (pointBudget > 100000) throw new InvalidDataException("The combined calculation exceeds 100,000 points. Adjust grid spacing or room selection.");
                zones.Add(preserved.Restore()); stats.Add(preserved.Diagnostics); zoneRevisions[room.Key] = preserved.InputRevision;
                continue;
            }
            cancel.ThrowIfCancellationRequested();
            var zone=new CalcZone { Key=room.Key,Name=room.Name??"",Number=room.Number??"",LevelName=room.Level??"",Kind="Room",
                BottomZ=room.Bottom,TopZ=room.Top,WorkplaneHeight=input.Set.Workplane,
                Reflectances=new Reflectances { Ceiling=input.Set.Ceiling,Wall=input.Set.Wall,Floor=input.Set.Floor,Furniture=input.Set.Furniture } };
            foreach(var loop in room.Loops) { var boundary=new List<Vec3>();for(int i=0;i<loop.Length;i+=2)boundary.Add(new(loop[i],loop[i+1],room.Bottom));zone.Loops.Add(boundary); }
            for(int i=0;i<Math.Min(room.LevelElevations?.Length??0,room.LevelNames?.Length??0);i++)zone.Levels.Add(new ZoneLevel(room.LevelNames[i],room.LevelElevations[i]));
            if(walking!=null) WalkingSurfaces.Generate(zone,walking,input.Set.Spacing,input.Set.BoundaryOffset,walkable.Contains,stairs.Contains);
            else
            {
                if (room.Bottom+input.Set.Workplane>=room.Top) throw new InvalidDataException($"Workplane lies above {zone.DisplayName}.");
                zone.GenerateGrid(input.Set.Spacing,input.Set.BoundaryOffset);
            }
            int excluded = CalculationSelection.ExcludePoints(zone, triangles, input.Set.AutoExcludeFurniture,
                input.Set.ExcludedPoints.Where(p => p.ZoneKey == room.Key).Select(p => V(p.Position, 0)));
            if (excluded > 0) runNotes.Add($"{zone.DisplayName}: {excluded} point(s) excluded manually or under furniture; retained workplane heights are unchanged.");
            zoneRevisions[room.Key] = hash;
            pointBudget+=zone.Points.Count;
            if(zone.Points.Count==0 || pointBudget>100000) throw new InvalidDataException("The calculation grid is empty or exceeds 100,000 points. Adjust spacing, offset, or room selection.");
            foreach(var lum in luminaires)
                if(zone.ContainsPlan(lum.Position.X,lum.Position.Y)&&lum.Position.Z>=zone.BottomZ&&lum.Position.Z<=zone.TopZ)
                {zone.LuminaireCount++;zone.Watts+=lum.Watts;zone.LuminaireTypeCounts[lum.TypeKey]=zone.LuminaireTypeCounts.TryGetValue(lum.TypeKey,out int n)?n+1:1;}
            var solver=new LightingSolver(luminaires,options);
            solver.Solve(zone,triangles,stage=>progress?.Invoke($"{zone.DisplayName}: {stage}"),cancel);
            if(zone.Statistics.Count==0)throw new InvalidDataException($"{zone.DisplayName}: every calculation point lies inside an obstruction. Adjust the workplane/grid or exported geometry.");
            if(options.Interreflection&&solver.LastIterations>=options.MaxIterations)complete=false;
            zones.Add(zone);stats.Add(solver.LastStats);
        }
        cancel.ThrowIfCancellationRequested(); progress?.Invoke("Preparing result revision and report");
        var adaptive=stats.Where(s=>s.PatchSize>options.PatchSize*1.001).ToArray();
        DateTime utc=DateTime.UtcNow;
        var data=CalcResultsFile.Build(new CalcResultsRun { Tool="BimGo calculation "+EngineIdentity.Version,CalcSetName=input.Set.Name,ModelTitle=input.Model??"",
            RunUtc=utc,Quality=((CalcQuality)input.Set.Quality).ToString(),Interreflection=input.Set.Interreflection },zones,luminaires);
        if(adaptive.Length>0)data.Notes.Add("Patch-size adjustment was required by the domain budget; actual patch sizes are retained in result diagnostics.");
        var header=JsonNode.Parse(data.Json).AsObject();
        header["zoneRevisions"]=JsonSerializer.SerializeToNode(zoneRevisions);
        header["scenarioHash"]=hash;header["engineVersion"]=EngineIdentity.Version;header["physicalModel"]=EngineIdentity.PhysicalModel;
        header["calculationStatus"]=complete?"Complete (iteration criterion)":"Incomplete (iteration cap)";
        header["inputBasis"]=input.GeometryBasis;
        byte[] headerBytes=Encoding.UTF8.GetBytes(header.ToJsonString());
        var display=LightingCalcFiles.Parse(headerBytes,data.Body,"native_"+input.Set.Id,displayOrigin,utc,out string error)
            ??throw new InvalidDataException("Native result conversion failed: "+error);
        foreach(var warning in input.Warnings.Concat(runNotes).Concat(data.Notes))display.Warnings.Add(warning);
        var report=new LightingReportData { ProjectName="BimGo calculation · "+input.Model,CalcSetName=input.Set.Name,RunAt=utc,Interreflection=input.Set.Interreflection,RoomPages=false,
            Method="Pinned CPU scalar diffuse photometry/interreflection; exported model geometry" };
        report.Zones.AddRange(zones);report.Warnings.AddRange(input.Warnings);report.Warnings.AddRange(runNotes);report.Warnings.AddRange(data.Notes);
        report.LuminairePositions.AddRange(luminaires.Select(l=>l.Position));
        foreach (var group in Enumerable.Range(0,input.Emitters.Count).GroupBy(i=>new {
            input.Emitters[i].Type,input.Emitters[i].Ies,input.Emitters[i].Multiplier,input.Emitters[i].Estimated,input.Emitters[i].Provenance }))
        {
            int i=group.First();var source=input.Emitters[i];var lum=luminaires[i];
            string iesHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source.Ies))).ToLowerInvariant();
            report.LuminaireTypes.Add(new LuminaireTypeSummary {TypeName=source.Type,TypeKey=lum.TypeKey,
                Photometry=iesHash,Source=source.Provenance,Estimated=source.Estimated,Lumens=lum.Photometry.ComputedLumens,
                LightLossFactor=source.Multiplier,Watts=lum.Watts,Count=group.Count()});
        }
        report.Settings.Add(new("Scenario SHA-256",hash));report.Settings.Add(new("Engine / physical model",EngineIdentity.Version+" / "+EngineIdentity.PhysicalModel+" / "+EngineIdentity.UpstreamCommit));
        report.Settings.Add(new("Status",header["calculationStatus"].ToString()));
        report.Settings.Add(new("Grid / workplane (ft)",$"{input.Set.Spacing:R} / {input.Set.Workplane:R}"));
        report.Settings.Add(new("Reflectance C/W/F/furniture",$"{input.Set.Ceiling:R}/{input.Set.Wall:R}/{input.Set.Floor:R}/{input.Set.Furniture:R}"));
        report.Settings.Add(new("Zone input revisions", string.Join("; ", zoneRevisions.Select(p => p.Key + ": " + p.Value))));
        report.Settings.Add(new("LLF",input.Set.LightLossFactor.ToString("R",CultureInfo.InvariantCulture)));
        report.Settings.Add(new("Quality",$"{options.PatchSize:R} ft patches; {options.PatchRays} patch rays; {options.GatherRays} gather rays; stop {options.Convergence:R}; max {options.MaxIterations} iterations"));
        var structured=JsonSerializer.SerializeToUtf8Bytes(new { Format="bimgo-calculation-results",Version=1,ScenarioHash=hash,EngineVersion=EngineIdentity.Version,EngineIdentity.PhysicalModel,EngineIdentity.UpstreamCommit,
            RunUtc=utc,Complete=complete,InputBasis=input.GeometryBasis,Warnings=report.Warnings,
            Zones=zones.Select(z=>new { z.Key,z.Name,z.Number,InputRevision=zoneRevisions[z.Key],Points=z.Points.Select((p,i)=>new {Position=new[]{p.X,p.Y,p.Z},Fc=Finite(z.Illuminance[i]),DirectFc=Finite(z.Direct[i])}),
                Patches=z.Surfaces.Select(p=>new { A=new[]{p.A.X,p.A.Y,p.A.Z},B=new[]{p.B.X,p.B.Y,p.B.Z},C=new[]{p.C.X,p.C.Y,p.C.Z},Normal=new[]{p.Normal.X,p.Normal.Y,p.Normal.Z},p.Reflectance,p.Illuminance,LuminanceCdM2=p.Exitance*10.763910/Math.PI,p.Covered }),
                Statistics=new {z.Statistics.Count,z.Statistics.Average,z.Statistics.Minimum,z.Statistics.Maximum} }),Diagnostics=stats },CalculationInput.Json);
        return new NativeCalculationRun { ScenarioHash=hash,Complete=complete,RunUtc=utc,Input=input,Display=display,Header=headerBytes,Body=data.Body,SceneBytes=sceneBytes,
            Zones=zones,Statistics=stats,ZoneRevisions=zoneRevisions,PointCsv=PointCsv(zones,hash),ReportData=report,
            ReportHtml=NativeLightingReport.Html(report,stats,display.ScaleMax,sources:input.Emitters),StructuredResults=structured };
    }
    static double? Finite(double v)=>double.IsFinite(v)?v:null;
    static Vec3 V(double[] p,int i)=>new(p[i],p[i+1],p[i+2]);
    static bool Ordered(IReadOnlyList<double> a)=>a.Count>0&&a.All(double.IsFinite)&&Enumerable.Range(1,a.Count-1).All(i=>a[i]>a[i-1]);
    static string AnalyticIes(Func<double,double> shape,double lumens)
    {
        var analytic=IesPhotometry.FromShape(shape,lumens,"Explicit analytic estimate");
        return "IESNA:LM-63-2002\n[TEST] BimGo explicit analytic estimate\nTILT=NONE\n1 -1 1 181 1 1 1 0 0 0\n1 1 0\n"+
            string.Join(" ",Enumerable.Range(0,181))+"\n0\n"+string.Join(" ",Enumerable.Range(0,181).Select(a=>analytic.Candela(a,0).ToString("R",CultureInfo.InvariantCulture)))+"\n";
    }
    static string PointCsv(IEnumerable<CalcZone> zones,string hash)
    {
        var b=new StringBuilder("Scenario SHA-256,"+hash+"\nZone,Number,X (ft),Y (ft),Z (ft),Illuminance (fc),Direct (fc)\n");
        string N(double v)=>double.IsFinite(v)?v.ToString("R",CultureInfo.InvariantCulture):"";
        string Q(string v)=>"\""+(v??"").Replace("\"","\"\"")+"\"";
        foreach(var z in zones)for(int i=0;i<z.Points.Count;i++){var p=z.Points[i];b.AppendLine(string.Join(",",Q(z.Name),Q(z.Number),N(p.X),N(p.Y),N(p.Z),N(z.Illuminance[i]),N(z.Direct[i])));}
        return b.ToString();
    }
}

/// <summary>Only the latest requested revision may publish. Cancellation is an optimization, not this safety gate.</summary>
public sealed class CalculationRevisionGate
{
    long _revision;
    public long Advance()=>Interlocked.Increment(ref _revision);
    public bool Matches(long revision)=>Interlocked.Read(ref _revision)==revision;
}
