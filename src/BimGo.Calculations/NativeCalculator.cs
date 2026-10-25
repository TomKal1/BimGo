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
    /// <summary>The run's calculation log: clusters, per-room solve statistics and per-fixture ownership checks.</summary>
    public string Log { get; init; }
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
            var changed = CalcResultsFile.ChangedSettings(SharedSettings(previous.Input), SharedSettings(input));
            if (changed.Count > 0) throw new InvalidDataException("Shared settings changed since the complete run (" + string.Join("; ", changed) + "). Run the complete set.");
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
        // Auto mounting (WWTools LuminaireMounting): recessed in a ceiling of any thickness, pendants and surface fixtures from
        // the fixture box, standing fixtures, and lights buried in a slab dropped out of its underside.
        TriangleScene placementScene = input.Set.CorrectRecessedSources && !input.SourcesResolved ? new TriangleScene(triangles) : null;
        var ceilingTags = input.Triangles.Where(t => t.Ceiling).Select(t => t.Tag).ToHashSet();
        var floorTags = input.Triangles.Where(t => t.Walkable).Select(t => t.Tag).ToHashSet();
        var placements = new MountingPlacement?[input.Emitters.Count];
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
            if (placementScene != null && source.AutoMount && source.Nadir[2] < -.7)
            {
                var geometry=new FixtureGeometry { Origin=position,Name=source.Type??"",BodyMin=source.BodyMin==null?null:V(source.BodyMin,0),BodyMax=source.BodyMax==null?null:V(source.BodyMax,0) };
                var placed=LuminaireMounting.Place(MountingKind.Auto,true,geometry,"",0,placementScene,ceilingTags.Contains,floorTags.Contains);
                placements[i]=placed;
                source.Placement=LuminaireMounting.Word(placed.Kind)+(placed.Sure?"":", not sure")+": "+placed.Reason;
                if ((placed.Position-position).Length>.01)
                {
                    source.OriginalPosition=(double[])source.Position.Clone();
                    source.Position=new[] {placed.Position.X,placed.Position.Y,placed.Position.Z}; position=placed.Position; corrections++;
                }
            }
            luminaires.Add(new Luminaire(position,V(source.Nadir,0),V(source.Axis0,0),ies)
            { Id=i+1, TypeKey=source.Type??source.Key,TypeName=source.Type??source.Key,Multiplier=source.Multiplier,Watts=ies.InputWatts });
        }
        if (corrections>0) input.Warnings.Add($"{corrections} source(s) moved to their mounting's light centre by auto mounting; original and resolved positions are retained.");
        foreach (var type in Enumerable.Range(0,input.Emitters.Count).Where(i=>placements[i].HasValue).GroupBy(i=>input.Emitters[i].Type??input.Emitters[i].Key))
        {
            var unsure=type.Where(i=>!placements[i].Value.Sure).ToList();
            if (unsure.Count>0) input.Warnings.Add($"{type.Key}: the light centre of {unsure.Count} of {type.Count()} source(s) is not sure ({placements[unsure[0]].Value.Reason}). Check the fixture's height offset or lens position.");
        }
        input.SourcesResolved=true;
        input.Warnings=input.Warnings.Distinct(StringComparer.Ordinal).ToList();
        input.Validate(); string hash=input.ContentHash(); byte[] sceneBytes=input.Serialize();
        if (sceneBytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("The retained calculation input exceeds 64 MB. Run the complete set or reduce the selected domain.");
        var zones=new List<CalcZone>(); var stats=new List<SolveStats>(); bool complete=true; int pointBudget=0;
        TriangleScene allScene=new TriangleScene(triangles);
        var walkable=input.Triangles.Where(t=>t.Walkable).Select(t=>t.Tag).ToHashSet(); var stairs=input.Triangles.Where(t=>t.Stair).Select(t=>t.Tag).ToHashSet();
        var runNotes = new List<string>();
        var zoneRevisions = new Dictionary<string, string>();
        var log = CalcLog.InMemory();
        if (input.PreservedZones.Count > 0)
        {
            runNotes.Add("Partial rerun: unselected rooms retain previously calculated values and their own input revision hashes; selected rooms are solved together in their own storey clusters.");
            complete = input.PreservedZones.All(p => p.Complete);
        }
        var storeys = input.Storeys.Select(l => new ZoneLevel(l.Name ?? "", l.Elevation)).ToList();
        var fresh = new List<CalcZone>(); var preservedStats = new Dictionary<CalcZone, SolveStats>();
        foreach (var room in input.Zones)
        {
            var preserved = input.PreservedZones.FirstOrDefault(p => p.Definition.Key == room.Key);
            if (preserved != null)
            {
                var restored = preserved.Restore(); zones.Add(restored); preservedStats[restored] = preserved.Diagnostics; zoneRevisions[room.Key] = preserved.InputRevision;
                continue;
            }
            var zone=new CalcZone { Key=room.Key,Name=room.Name??"",Number=room.Number??"",LevelName=room.Level??"",Kind="Room",
                BottomZ=room.Bottom,TopZ=room.Top,WorkplaneHeight=input.Set.Workplane,LevelElevation=room.LevelElevation??room.Bottom,
                Reflectances=new Reflectances { Ceiling=input.Set.Ceiling,Wall=input.Set.Wall,Floor=input.Set.Floor,Furniture=input.Set.Furniture } };
            foreach(var loop in room.Loops) { var boundary=new List<Vec3>();for(int i=0;i<loop.Length;i+=2)boundary.Add(new(loop[i],loop[i+1],room.Bottom));zone.Loops.Add(boundary); }
            if (storeys.Count > 0) zone.SetLevels(storeys);
            else for(int i=0;i<Math.Min(room.LevelElevations?.Length??0,room.LevelNames?.Length??0);i++)zone.Levels.Add(new ZoneLevel(room.LevelNames[i],room.LevelElevations[i]));
            zones.Add(zone); fresh.Add(zone); zoneRevisions[room.Key] = hash;
        }
        // Points, once every zone exists: a tall zone leaves the storeys over its floor to the zones that hold them.
        TriangleScene furniture = input.Set.AutoExcludeFurniture ? CalculationSelection.FurnitureScene(triangles) : null;
        foreach (var zone in fresh)
        {
            cancel.ThrowIfCancellationRequested();
            if(input.Set.WalkingSurfaces)
                foreach (string note in WalkingSurfaces.Generate(zone,allScene,input.Set.Spacing,input.Set.BoundaryOffset,walkable.Contains,stairs.Contains,WalkingSurfaces.ClaimedByOthers(zone,zones)))
                    log.Write($"  {zone.DisplayName}: {note}");
            else
            {
                if (zone.BottomZ+input.Set.Workplane>=zone.TopZ) throw new InvalidDataException($"Workplane lies above {zone.DisplayName}.");
                zone.GenerateGrid(input.Set.Spacing,input.Set.BoundaryOffset);
            }
            int excluded = CalculationSelection.ExcludePoints(zone, furniture,
                input.Set.ExcludedPoints.Where(p => p.ZoneKey == zone.Key).Select(p => V(p.Position, 0)));
            if (excluded > 0) runNotes.Add($"{zone.DisplayName}: {excluded} point(s) excluded manually or under furniture; retained workplane heights are unchanged.");
        }
        foreach (var zone in zones)
        {
            pointBudget+=zone.Points.Count;
            if(zone.Points.Count==0 || pointBudget>100000) throw new InvalidDataException("The calculation grid is empty or exceeds 100,000 points. Adjust spacing, offset, or room selection.");
        }

        // One owning zone per source, for counts and power density (WWTools FixtureZoneCheck). BimGo has no Revit level or
        // space per fixture, so ownership is geometric; stacked, buried and floor-mounted fixtures are still flagged.
        var infos = luminaires.Select((l, i) => Info(l, input.Emitters[i], placements[i])).ToList();
        var owners = FixtureZoneCheck.Run(zones, infos, (zone, p) => LightingReport.LuminaireInZone(zone, p) && !LuminairePlacement.OnFloorAbove(zone, p, allScene), log, runNotes);
        foreach (var zone in fresh)
            for (int i = 0; i < luminaires.Count; i++)
            {
                if (owners[infos[i]] != zone) continue;
                var lum = luminaires[i];
                zone.LuminaireCount++; zone.Watts += lum.Watts; zone.LuminaireTypeCounts[lum.TypeKey] = zone.LuminaireTypeCounts.TryGetValue(lum.TypeKey, out int n) ? n + 1 : 1;
            }

        var solved = NativeClusterSolve.Solve(fresh, luminaires, triangles, input.Set.Margin, options, log, runNotes, progress, cancel, out bool converged);
        complete &= converged;
        foreach (var zone in zones)
        {
            if (preservedStats.TryGetValue(zone, out var kept)) { stats.Add(kept); continue; }
            if(zone.Statistics.Count==0)throw new InvalidDataException($"{zone.DisplayName}: every calculation point lies inside an obstruction. Adjust the workplane/grid or exported geometry.");
            var result = solved[zone]; stats.Add(result.Stats);
            if (result.Stats.PointsInsideObstructions > 0)
                runNotes.Add($"{zone.DisplayName}: {result.Stats.PointsInsideObstructions} point(s) fall inside casework, furniture or walls and are left out of the statistics.");
            for (int k = 0; k < result.Luminaires.Count && k < result.Stats.PointsReachedByLuminaire.Length; k++)
            {
                var l = result.Luminaires[k]; int index = luminaires.IndexOf(l);
                if (index >= 0 && owners[infos[index]] == zone && result.Stats.PointsReachedByLuminaire[k] == 0)
                    log.Write($"  WARNING: luminaire {l.Id} {l.TypeName} at {l.Position} is inside {zone.DisplayName} but lights none of its points");
            }
        }
        cancel.ThrowIfCancellationRequested(); progress?.Invoke("Preparing result revision and report");
        var adaptive=stats.Where(s=>s.PatchSize>options.PatchSize*1.001).ToArray();
        DateTime utc=DateTime.UtcNow;
        var data=CalcResultsFile.Build(new CalcResultsRun { Tool="BimGo calculation "+EngineIdentity.Version,CalcSetName=input.Set.Name,ModelTitle=input.Model??"",
            RunUtc=utc,Quality=((CalcQuality)input.Set.Quality).ToString(),Interreflection=input.Set.Interreflection,SharedSettings=SharedSettings(input) },zones,luminaires);
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
            Method="Pinned CPU scalar diffuse photometry; interreflection solved once per storey cluster with a per-room final gather; exported model geometry" };
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
        string noWatts=LightingReport.NoWattageWarning(report.LuminaireTypes); if(noWatts!=null)report.Warnings.Add(noWatts);
        var mounted=placements.Where(p=>p.HasValue).Select(p=>p.Value).ToList();
        report.Settings.Add(new("Source placement",input.Set.CorrectRecessedSources?"Auto mounting"+(mounted.Count>0?": "+LuminaireMounting.Summary(mounted):""):"Exported source origins"));
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
            Zones=zones,Statistics=stats,ZoneRevisions=zoneRevisions,Log=log.Text,PointCsv=PointCsv(zones,hash),ReportData=report,
            ReportHtml=NativeLightingReport.Html(report,stats,display.ScaleMax,sources:input.Emitters),StructuredResults=structured };
    }
    static double? Finite(double v)=>double.IsFinite(v)?v:null;
    /// <summary>The settings every zone shares, as "name: value"; a partial rerun is refused when any differ from the complete run.</summary>
    static List<string> SharedSettings(CalculationInput i)
    {
        string R(double v)=>v.ToString("R",CultureInfo.InvariantCulture);
        var s=i.Set;
        return new() { "engine: "+EngineIdentity.UpstreamCommit,"grid: "+R(s.Spacing)+" ft","boundary offset: "+R(s.BoundaryOffset)+" ft","work plane: "+R(s.Workplane)+" ft",
            "margin: "+R(s.Margin)+" ft","reflectances: "+R(s.Ceiling)+"/"+R(s.Wall)+"/"+R(s.Floor)+"/"+R(s.Furniture),"LLF: "+R(s.LightLossFactor),
            "quality: "+s.Quality,"interreflection: "+s.Interreflection,"walking surfaces: "+s.WalkingSurfaces,"auto mounting: "+s.CorrectRecessedSources,
            "furniture exclusion: "+s.AutoExcludeFurniture,"emergency: "+s.EmergencyOnly };
    }
    static FixtureRevitInfo Info(Luminaire l, CalculationEmitter source, MountingPlacement? placed)
    {
        Vec3 origin=V(source.OriginalPosition??source.Position,0);
        string kind=source.Placement?.Split(':',',')[0];
        return new FixtureRevitInfo { Id=l.Id,TypeName=l.TypeName,Position=l.Position,Origin=origin,
            Pendant=kind=="pendant",CeilingMounted=kind is "recessed" or "surface",
            HangsBelowOrigin=source.BodyMin!=null&&source.BodyMin[2]<origin.Z-LuminaireMounting.PlacedBelow,
            MovedDownOutOfSolid=placed?.MovedDownOutOfSolid };
    }
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
