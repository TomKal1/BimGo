using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BimGo.Calculations;
using BimGo.Format;
using BimGo.Scene;
using Lighting.Baseline;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BimGo.Tests;

[TestClass]
public sealed partial class NativeLightingTests
{
    static readonly CalculationEdits NoEdits = new();
    static NativeLightingSet Set() => new() { Name="Native office",Rooms=new[]{0},LightLossFactor=.8,CorrectRecessedSources=false };
    static SceneData Scene(bool missingIes=false,bool template=false,string sourceView=null,Vector3 origin=default)
    {
        var example=SceneCase.Corpus().First();
        var vertices=new List<SceneVertex>();var indices=new List<uint>();var elements=new List<ElementRecord>();
        int Cat(string name)=>CategoryCatalog.Find(name).Index;
        foreach(var group in example.Triangles.GroupBy(t=>(int)t[9]))
        {
            int start=indices.Count;var bounds=Aabb.Empty;
            foreach(var t in group)
            {
                Vector3 P(int at)=>new((float)(t[at]*.3048),(float)(t[at+1]*.3048),(float)(t[at+2]*.3048));
                var a=P(0);var b=P(3);var c=P(6);var normal=Vector3.Normalize(Vector3.Cross(b-a,c-a));
                foreach(var p in new[]{a,b,c}){indices.Add((uint)vertices.Count);vertices.Add(new(p,normal,0xffffffff));bounds.Include(p);}
            }
            elements.Add(new ElementRecord { ElementId=elements.Count+1,UniqueId="surface-"+group.Key,Name="enclosure",FamilyType="Enclosure",
                CategoryIndex=Cat(group.Key==0?"ceilings":group.Key==2?"floors":"walls"),Bounds=bounds,OpaqueStart=start,OpaqueCount=indices.Count-start });
        }
        int fixture=elements.Count;var position=new Vector3(6,5,8.9f)*.3048f;
        string photometryText=SceneCase.CosineIes(4000), photometryHash=LightProfiles.HashOf(LightProfiles.KeyOf(photometryText));
        elements.Add(new ElementRecord {ElementId=100,UniqueId="fixture-100",Name="Fixture",FamilyType="Test light",CategoryIndex=Cat("lightfixtures"),
            Bounds=new(position-new Vector3(.05f),position+new Vector3(.05f)),Pivot=position,Movable=true,IsLibraryTemplate=template});
        return new SceneData {Vertices=vertices.ToArray(),Indices=indices.ToArray(),Elements=elements.ToArray(),Levels=new[]{new LevelInfo("Level 1",0)},
            Rooms=new[]{new RoomInfo {Number="101",Name="Office",Min=Vector2.Zero,Max=new Vector2(12,10)*.3048f,BottomZ=0,TopZ=9*.3048f,
                Loops=new[]{new[]{Vector2.Zero,new Vector2(12,0)*.3048f,new Vector2(12,10)*.3048f,new Vector2(0,10)*.3048f}}}},
            Lighting=new LightingData {Lights=new[]{new LightSource {Element=fixture,Position=position,Ies=missingIes?null:photometryHash,Lumens=4000,Aim=-Vector3.UnitZ,Across=Vector3.UnitX}},
                IesFiles=missingIes?Array.Empty<LightIesFile>():new[]{new LightIesFile(photometryHash,"test.ies",photometryText)}},
            ModelTitle="Native test",OriginOffset=origin,Bounds=new(Vector3.Zero,new Vector3(12,10,9)*.3048f),Settings=new LaunchSettings(),SourceView=sourceView,
            CategoryLoaded=Enumerable.Repeat(true,CategoryCatalog.All.Count).ToArray(),CategoryElementCounts=new int[CategoryCatalog.All.Count] };
    }
    static CalculationInput Input(SceneData scene,NativeLightingSet set=null,CalculationEdits edits=null,LightProfileDocument profiles=null)
        =>ExportedSceneInput.Build(scene,set??Set(),edits??NoEdits,profiles??new());
    static NativeCalculationRun Run(CalculationInput input,Vector3 origin=default)=>NativeCalculator.Run(input,origin);

    [TestMethod]
    public void InputOnlyExportPerformsItsFirstOfflineSolveAndReports()
    {
        var scene=Scene();var run=Run(Input(scene));
        Assert.IsTrue(run.Complete);Assert.AreEqual(30,run.Display.PointCount);
        Assert.AreEqual(17.448749669,run.Zones[0].Statistics.Average,.001);
        Assert.IsTrue(run.Display.IsNative);Assert.AreEqual(run.ScenarioHash,run.Display.ScenarioHash);
        Assert.AreEqual(30,run.Display.Zones[0].DirectValues.Length);
        StringAssert.Contains(run.ReportHtml,run.ScenarioHash);StringAssert.Contains(run.PointCsv,run.ScenarioHash);
        StringAssert.Contains(Encoding.UTF8.GetString(run.StructuredResults),run.ScenarioHash);
        Assert.IsTrue(run.ReportHtml.Contains("exported",StringComparison.OrdinalIgnoreCase));
    }
    [TestMethod]
    public void ReportIncludesPhotometryScheduleFixtureLocationsAndActualSolveDiagnostics()
    {
        var input=Input(Scene());input.Emitters[0].Type="Fixture <A&B>";
        var original=input.Emitters[0];
        input.Emitters.Add(new CalculationEmitter {Key="above-room",Type="Transport source above room",Position=new[]{6d,5d,12.5},
            Nadir=original.Nadir,Axis0=original.Axis0,Ies=original.Ies,Provenance=original.Provenance,
            Lumens=original.Lumens,Downward=original.Downward,Multiplier=original.Multiplier});
        var run=Run(input);var html=run.ReportHtml;
        StringAssert.Contains(html,"Fixture &lt;A&amp;B&gt;");
        StringAssert.Contains(html,"Full exported/imported IES");
        StringAssert.Contains(html,"Actual patch size (ft)");
        StringAssert.Contains(html,"Iterations");
        StringAssert.Contains(html,run.Statistics[0].PatchSize.ToString("0.####",System.Globalization.CultureInfo.InvariantCulture));
        StringAssert.Contains(html,"Calculated in BimGo using the shared Lighting.Engine");
        StringAssert.Contains(html,"UTC");
        Assert.IsFalse(html.Contains("Calculated with WW Tools Lighting."));
        var plan=XDocument.Parse(Regex.Match(html,"<svg\\b.*?</svg>",RegexOptions.Singleline).Value);
        XNamespace svg="http://www.w3.org/2000/svg";
        var markers=plan.Descendants(svg+"circle").Where(e=>(string)e.Attribute("class")=="luminaire").ToArray();
        Assert.AreEqual(1,markers.Length);
        Assert.AreEqual(run.Zones[0].LuminaireCount,markers.Length);
        StringAssert.Contains(html,"Transport source above room");
        Assert.IsNotNull(markers[0].Element(svg+"title"));
        var estimated=Set();estimated.AllowEstimatedPhotometry=true;
        var estimatedHtml=Run(Input(Scene(missingIes:true),estimated)).ReportHtml;
        StringAssert.Contains(estimatedHtml,"ESTIMATED:");
        StringAssert.Contains(estimatedHtml,"fill=\"#ffc247\"");
        StringAssert.Contains(estimatedHtml,"(estimated)");
    }
    [TestMethod]
    public void ReportUsesTheModelRampAndCurrentScaleWithoutChangingTheCalculationRevision()
    {
        var run=Run(Input(Scene()));var originalBytes=(byte[])run.SceneBytes.Clone();
        string hash=run.ScenarioHash;
        StringAssert.Contains(run.ReportHtml,"0–"+run.Display.ScaleMax.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" fc");
        var fc=run.HtmlReport(50);var lux=run.HtmlReport(50,true);
        StringAssert.Contains(fc,"0–50.0 fc");StringAssert.Contains(lux,"0–538 lx");
        foreach(float value in new[]{0f,12.5f,25f,37.5f,50f})
        {
            var rgb=LightingCalcs.FalseColour(value,0,50);
            string colour=$"#{(int)MathF.Round(rgb.X*255):x2}{(int)MathF.Round(rgb.Y*255):x2}{(int)MathF.Round(rgb.Z*255):x2}";
            Assert.AreEqual(colour,NativeLightingReport.Colour(value,50));
            StringAssert.Contains(fc,colour);StringAssert.Contains(lux,colour);
        }
        Assert.AreEqual(hash,run.ScenarioHash);CollectionAssert.AreEqual(originalBytes,run.SceneBytes);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>run.HtmlReport(double.NaN));
    }
    [TestMethod]
    public void PhysicalMaterialEditChangesIndirectAndPreservesDirect()
    {
        var scene=Scene();var set=Set();var original=Run(Input(scene,set));
        set.Wall=.1;var dark=Run(Input(scene,set));set.Wall=.8;var bright=Run(Input(scene,set));
        CollectionAssert.AreEqual(original.Zones[0].Direct,dark.Zones[0].Direct);
        CollectionAssert.AreEqual(original.Zones[0].Direct,bright.Zones[0].Direct);
        Assert.IsTrue(bright.Zones[0].Statistics.Average>original.Zones[0].Statistics.Average);
        Assert.IsTrue(dark.Zones[0].Statistics.Average<original.Zones[0].Statistics.Average);
        Assert.AreNotEqual(original.ScenarioHash,dark.ScenarioHash);
        Assert.AreEqual(dark.Display.ScenarioHash,dark.ScenarioHash);
    }
    [TestMethod]
    public void MovedRotatedClonedAndRemovedSourcesAffectCurrentPhysicalSolution()
    {
        var scene=Scene();var definition=Set();var original=Run(Input(scene,definition));int fixture=scene.Elements.Length-1;
        var hidden=new bool[scene.Elements.Length];hidden[fixture]=true;
        var move=new CalculationEdits {Removed=hidden,Instances=new[]{new CalculationInstance(fixture,0,false,Matrix4x4.CreateTranslation(-.9144f,0,0))}};
        var moved=Run(Input(scene,definition,move));
        Assert.IsTrue(original.Zones[0].Direct.Zip(moved.Zones[0].Direct,(a,b)=>Math.Abs(a-b)).Max()>1);
        var clone=new CalculationEdits {Instances=new[]{new CalculationInstance(fixture,1,true,Matrix4x4.CreateTranslation(-.9144f,0,0))}};
        var added=Run(Input(scene,definition,clone));
        Assert.AreEqual(2,added.Input.Emitters.Count);Assert.IsTrue(added.Zones[0].Statistics.Average>original.Zones[0].Statistics.Average);
        var undo=Run(Input(scene,definition));CollectionAssert.AreEqual(original.Zones[0].Illuminance,undo.Zones[0].Illuminance);
        // Delete the original while retaining the clone: the result matches moving the original.
        var onlyClone=new CalculationEdits {Removed=hidden,Instances=clone.Instances};
        CollectionAssert.AreEqual(moved.Zones[0].Illuminance,Run(Input(scene,definition,onlyClone)).Zones[0].Illuminance);
        var pivot=scene.Lighting.Lights[0].Position;
        var aimUp=Matrix4x4.CreateTranslation(-pivot)*Matrix4x4.CreateRotationX(MathF.PI)*Matrix4x4.CreateTranslation(pivot);
        var rotated=new CalculationEdits {Removed=hidden,Instances=new[]{new CalculationInstance(fixture,0,false,aimUp)}};
        Assert.IsTrue(Run(Input(scene,definition,rotated)).Zones[0].Direct.All(d=>d<1e-6));
    }
    [TestMethod]
    public void PlacedLibraryLightIsIncludedButUnplacedTemplateIsNot()
    {
        var scene=Scene(template:true);int fixture=scene.Elements.Length-1;
        Assert.ThrowsExactly<InvalidDataException>(()=>Input(scene));
        var added=new CalculationEdits {Instances=new[]{new CalculationInstance(fixture,7,true,Matrix4x4.Identity)}};
        var run=Run(Input(scene,edits:added));Assert.AreEqual(1,run.Input.Emitters.Count);Assert.IsTrue(run.Zones[0].Statistics.Average>1);
    }
    [TestMethod]
    public void MissingPhotometryIsAnErrorUntilEstimatesAreExplicitlyEnabled()
    {
        var scene=Scene(missingIes:true);var definition=Set();
        Assert.ThrowsExactly<InvalidDataException>(()=>Input(scene,definition));
        definition.AllowEstimatedPhotometry=true;var run=Run(Input(scene,definition));
        Assert.IsTrue(run.Input.Emitters[0].Estimated);Assert.IsNotNull(run.Input.Emitters[0].Ies);
        Assert.IsTrue(run.Input.Warnings.Any(w=>w.Contains("estimated",StringComparison.OrdinalIgnoreCase)));
    }
    [TestMethod]
    public async Task MissingPhotometryReturnsAnActionableWorkerOutcomeAndCanBeCorrected()
    {
        var scene=Scene(missingIes:true);var definition=Set();
        var job=Task.Run(()=>NativeCalculationJob.Execute(scene,definition,NoEdits,new(),Vector3.Zero));
        var outcome=await job;
        Assert.AreEqual(TaskStatus.RanToCompletion,job.Status);
        Assert.IsTrue(outcome.NeedsInput);Assert.IsFalse(outcome.Canceled);Assert.IsNull(outcome.Run);
        StringAssert.Contains(outcome.Message,"1 source(s)");
        StringAssert.Contains(outcome.Message,"Import IES in LIGHTS");
        StringAssert.Contains(outcome.Message,"Allow estimated photometry");
        Assert.IsFalse(definition.AllowEstimatedPhotometry);
        Assert.IsNull(scene.Lighting.Lights[0].Ies);
        definition.AllowEstimatedPhotometry=true;
        var corrected=await Task.Run(()=>NativeCalculationJob.Execute(scene,definition,NoEdits,new(),Vector3.Zero));
        Assert.IsNotNull(corrected.Run);Assert.IsTrue(corrected.Run.Complete);
        Assert.IsFalse(corrected.NeedsInput);Assert.IsNull(corrected.Diagnostic);
        Assert.IsTrue(corrected.Run.Input.Emitters[0].Estimated);
    }
    [TestMethod]
    public async Task MalformedPhotometryAndCanceledJobsReturnNoNumericalResults()
    {
        var scene=Scene();var ies=scene.Lighting.IesFiles[0];
        scene.Lighting.IesFiles[0]=new LightIesFile(ies.Hash,"invalid.ies","not IES");
        var invalid=await Task.Run(()=>NativeCalculationJob.Execute(scene,Set(),NoEdits,new(),Vector3.Zero));
        Assert.IsTrue(invalid.NeedsInput);Assert.IsNull(invalid.Run);
        StringAssert.Contains(invalid.Diagnostic,nameof(FormatException));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        var canceled=await Task.Run(()=>NativeCalculationJob.Execute(scene,Set(),NoEdits,new(),Vector3.Zero,cancel:cancel.Token));
        Assert.IsTrue(canceled.Canceled);Assert.IsFalse(canceled.NeedsInput);
        Assert.IsNull(canceled.Run);Assert.IsNull(canceled.Diagnostic);
    }
    [TestMethod]
    public async Task UnexpectedWorkerFailuresKeepDiagnosticsWithoutPublishingResults()
    {
        var failed=await Task.Run(()=>NativeCalculationJob.Execute(Scene(),Set(),NoEdits,new(),Vector3.Zero,
            _=>throw new InvalidOperationException("test worker failure")));
        Assert.IsNull(failed.Run);Assert.IsFalse(failed.NeedsInput);Assert.IsFalse(failed.Canceled);
        Assert.AreEqual("test worker failure",failed.Message);
        StringAssert.Contains(failed.Diagnostic,nameof(InvalidOperationException));
        StringAssert.Contains(failed.Diagnostic,nameof(NativeCalculator.Run));
    }
    [TestMethod]
    public void FullIesOverridesUseInstanceBeforeTypeAndNeverGpuTables()
    {
        var scene=Scene();var profiles=new LightProfileDocument();
        string text=SceneCase.CosineIes(2000);string key=LightProfiles.KeyOf(text);string hash=LightProfiles.HashOf(key);
        profiles.Imports.Add(new LightProfileImport {Hash=hash,Name="half.ies",Text=text});
        profiles.SetChoice("Test light",LightProfiles.WALL_WASHER);
        profiles.SetInstanceChoice(LightProfileDocument.InstanceKeyOf(scene,scene.Elements.Length-1),"Test light",key);
        var half=Run(Input(scene,profiles:profiles));var original=Run(Input(scene));
        Assert.AreEqual(original.Zones[0].Statistics.Average*.5,half.Zones[0].Statistics.Average,.001);
        Assert.AreEqual(text,half.Input.Emitters[0].Ies);
    }
    [TestMethod]
    public void RetainedCanonicalInputReplaysSameRevisionAndValues()
    {
        var first=Run(Input(Scene()));var replay=Run(CalculationInput.Parse(first.SceneBytes));
        Assert.AreEqual(first.ScenarioHash,replay.ScenarioHash);
        CollectionAssert.AreEqual(first.Zones[0].Illuminance,replay.Zones[0].Illuminance);
        Assert.AreEqual(first.ScenarioHash,CalculationInput.Parse(first.SceneBytes).ContentHash());
    }
    [TestMethod]
    public void GlobalOriginRoundTripLeavesValuesAndLocalDisplayAligned()
    {
        var origin=new Vector3(1000,-2000,30);var shifted=Run(Input(Scene(origin:origin)),origin);var normal=Run(Input(Scene()));
        Assert.AreEqual(normal.Zones[0].Statistics.Average,shifted.Zones[0].Statistics.Average,.0001);
        Assert.IsTrue(Vector3.Distance(normal.Display.Zones[0].Points[0],shifted.Display.Zones[0].Points[0])<.0003f);
    }
    [TestMethod]
    public void CorruptUnitsModelFramePhotometryAndRoomIdentityAreRejected()
    {
        var scene=Scene();var input=Input(scene);input.LengthUnit="m";
        Assert.ThrowsExactly<InvalidDataException>(()=>Run(input));
        input=Input(scene);input.Emitters[0].Nadir=new[]{double.NaN,0,0};Assert.ThrowsExactly<InvalidDataException>(()=>Run(input));
        input=Input(scene);input.Emitters[0].Ies="not IES";Assert.ThrowsExactly<FormatException>(()=>Run(input));
        var set=Set();set.RoomKeys=new[]{"another room"};Assert.ThrowsExactly<InvalidDataException>(()=>Input(scene,set));
        Assert.ThrowsExactly<InvalidDataException>(()=>Input(Scene(sourceView:"Clipped 3D view")));
    }
    [TestMethod]
    public void CancellationAndLateCompletionCannotPublishAnOlderRevision()
    {
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>NativeCalculator.Run(Input(Scene()),Vector3.Zero,cancel:cancel.Token));
        var gate=new CalculationRevisionGate();long first=gate.Advance();long second=gate.Advance();
        Assert.IsFalse(gate.Matches(first));Assert.IsTrue(gate.Matches(second));
        gate.Advance();Assert.IsFalse(gate.Matches(second));
    }
    [TestMethod]
    public void NativeDefinitionAndCompleteInputPersistInBimgoWithoutImportedResults()
    {
        var scene=Scene();var definition=Set();var run=Run(Input(scene,definition));
        var setup=new NativeLightingDocument {Selected=definition.Id,Sets=new(){definition}};
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".bimgo");
        try
        {
            var doc=new BimGoDocument {Scene=scene,NativeLighting=NativeLightingFiles.Serialize(setup),LightingScenes=new(){[NativeLightingFiles.ScenePrefix+definition.Id+".json"]=run.SceneBytes},
                LightingCalcs=new(){[LightingCalcFiles.ENTRY_PREFIX+"native.json"]=run.Header,[LightingCalcFiles.ENTRY_PREFIX+"native.bin"]=run.Body}};
            Assert.IsTrue(BimGoWriter.Write(path,doc,new WriterInfo("Native test","1"),FileKinds.SAVE,out string error),error);
            var loaded=BimGoReader.Read(path,new LaunchSettings(),out error);Assert.IsNotNull(loaded,error);
            var settings=NativeLightingFiles.Parse(loaded.NativeLighting,out error);Assert.IsNotNull(settings,error);
            Assert.AreEqual(definition.Id,settings.Selected);Assert.AreEqual(1,settings.Sets.Count);
            var input=CalculationInput.Parse(loaded.LightingScenes[NativeLightingFiles.ScenePrefix+definition.Id+".json"]);
            Assert.AreEqual(run.ScenarioHash,Run(input).ScenarioHash);
            var imported=LightingCalcFiles.ParseEmbedded(loaded.LightingCalcs,"native",Vector3.Zero,out error);
            Assert.IsNotNull(imported,error);Assert.IsTrue(imported.IsNative);Assert.AreEqual(run.ScenarioHash,imported.ScenarioHash);
        }
        finally {if(File.Exists(path))File.Delete(path);}
    }
    /// <summary>A 12 x 10 ft room two storeys tall (L1 at 0, L2 at 10 ft), with a mezzanine floor over its west half.</summary>
    static SceneData TwoStoreyScene(string floorCategory="floors")
    {
        var vertices=new List<SceneVertex>();var indices=new List<uint>();var elements=new List<ElementRecord>();
        int Cat(string name)=>CategoryCatalog.Find(name).Index;
        void Slab(string category,float x0,float x1,float z,bool up)
        {
            int start=indices.Count;var normal=up?Vector3.UnitZ:-Vector3.UnitZ;var bounds=Aabb.Empty;
            var c=new[]{new Vector3(x0,0,z),new Vector3(x1,0,z),new Vector3(x1,10,z),new Vector3(x0,10,z)}.Select(p=>p*.3048f).ToArray();
            foreach(int k in up?new[]{0,1,2,0,2,3}:new[]{0,2,1,0,3,2}){indices.Add((uint)vertices.Count);vertices.Add(new(c[k],normal,0xffffffff));bounds.Include(c[k]);}
            elements.Add(new ElementRecord {ElementId=elements.Count+1,UniqueId="slab-"+elements.Count,Name=category,FamilyType=category,
                CategoryIndex=Cat(category),Bounds=bounds,OpaqueStart=start,OpaqueCount=indices.Count-start});
        }
        Slab(floorCategory,0,12,0,true);Slab(floorCategory,0,6,10,true);Slab(floorCategory,0,6,9.5f,false);Slab("ceilings",0,12,20,false);
        int fixture=elements.Count;var position=new Vector3(3,5,18)*.3048f;
        string text=SceneCase.CosineIes(4000),hash=LightProfiles.HashOf(LightProfiles.KeyOf(text));
        elements.Add(new ElementRecord {ElementId=100,UniqueId="fixture-100",Name="Fixture",FamilyType="Test light",CategoryIndex=Cat("lightfixtures"),
            Bounds=new(position-new Vector3(.05f),position+new Vector3(.05f)),Pivot=position,Movable=true});
        return new SceneData {Vertices=vertices.ToArray(),Indices=indices.ToArray(),Elements=elements.ToArray(),
            Levels=new[]{new LevelInfo("L1",0),new LevelInfo("L2",10*.3048f)},
            Rooms=new[]{new RoomInfo {Number="201",Name="Loft",Min=Vector2.Zero,Max=new Vector2(12,10)*.3048f,BottomZ=0,TopZ=20*.3048f,
                Loops=new[]{new[]{Vector2.Zero,new Vector2(12,0)*.3048f,new Vector2(12,10)*.3048f,new Vector2(0,10)*.3048f}}}},
            Lighting=new LightingData {Lights=new[]{new LightSource {Element=fixture,Position=position,Ies=hash,Lumens=4000,Aim=-Vector3.UnitZ,Across=Vector3.UnitX}},
                IesFiles=new[]{new LightIesFile(hash,"test.ies",text)}},
            ModelTitle="Two storey test",Bounds=new(Vector3.Zero,new Vector3(12,10,20)*.3048f),Settings=new LaunchSettings(),
            CategoryLoaded=Enumerable.Repeat(true,CategoryCatalog.All.Count).ToArray(),CategoryElementCounts=new int[CategoryCatalog.All.Count] };
    }
    [TestMethod]
    public void TwoStoreyRoomGetsPointsOnEachFloorWithoutTheWalkingSurfaceOption()
    {
        var set=Set();Assert.IsFalse(set.WalkingSurfaces);
        var run=Run(Input(TwoStoreyScene(),set));var zone=run.Zones[0];
        Assert.IsTrue(zone.IsMultiLevel);
        var upper=Enumerable.Range(0,zone.Points.Count).Where(i=>zone.Points[i].Z>11).ToArray();
        var lower=Enumerable.Range(0,zone.Points.Count).Where(i=>zone.Points[i].Z<3).ToArray();
        Assert.IsTrue(upper.Length>0,"the mezzanine has no calculation points");
        Assert.IsTrue(lower.Length>0,"the floor below has no calculation points");
        Assert.IsTrue(upper.All(i=>zone.Points[i].X<6),"upper points lie off the mezzanine");
        Assert.IsTrue(upper.Average(i=>zone.Illuminance[i])>lower.Where(i=>zone.Points[i].X<6).Average(i=>zone.Illuminance[i]),
            "the mezzanine under the fixture should be brighter than the floor it shades");
    }
    [TestMethod]
    public void TwoStoreyRoomWithoutExportedFloorsFallsBackToAFlatWorkPlane()
    {
        var run=Run(Input(TwoStoreyScene("generic")));var zone=run.Zones[0];
        Assert.IsTrue(zone.Points.Count>0);
        Assert.IsTrue(zone.Points.All(p=>Math.Abs(p.Z-2.5)<1e-6),"the fallback work plane sits 2.5 ft over the lowest floor");
        Assert.IsTrue(run.Display.Warnings.Any(w=>w.Contains("flat work plane was used",StringComparison.Ordinal)));
    }
    [TestMethod]
    public void NativeSetupRejectsUnsupportedVersionsNonfiniteRangesAndDuplicateIds()
    {
        var set=Set();set.Spacing=double.NaN;Assert.ThrowsExactly<InvalidDataException>(set.Validate);
        var doc=new NativeLightingDocument {Version=900};Assert.IsNull(NativeLightingFiles.Parse(NativeLightingFiles.Serialize(doc),out string error));Assert.IsNotNull(error);
        set=Set();doc=new(){Sets=new(){set,NativeLightingFiles.Copy(set)}};
        Assert.IsNull(NativeLightingFiles.Parse(NativeLightingFiles.Serialize(doc),out error));
    }
}
