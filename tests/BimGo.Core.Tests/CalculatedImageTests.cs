using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using BimGo.Calculations;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace BimGo.Tests;

[TestClass]
public sealed class CalculatedImageTests
{
    private static CalcZone Plane()
    {
        var z=new CalcZone {Key="parity-plane",Name="Office",BottomZ=0,TopZ=9,CeilingZ=9};
        var a=new Vec3(0,0,0);var b=new Vec3(10,0,0);var c=new Vec3(10,10,0);var d=new Vec3(0,10,0);
        z.Loops.Add(new(){a,b,c,d});
        z.GenerateGrid(2,1);z.Illuminance=z.Points.Select(_=>50.0).ToArray();z.Direct=z.Illuminance.ToArray();z.Statistics=ZoneStatistics.From(z.Illuminance);
        foreach(var t in new[]{new SceneTriangle(a,b,c,SurfaceClass.Floor,.2,42,new Vec3(.8,.3,.2)),new SceneTriangle(a,c,d,SurfaceClass.Floor,.2,42,new Vec3(.8,.3,.2))})
            z.Surfaces.Add(new SurfacePatch(t,Vec3.UnitZ,.2,50));
        return z;
    }
    [TestMethod]
    public void FullPrecisionImageIsDeterministicAndPreservesPhotopicLuminance()
    {
        var z=Plane();var o=new LightingImageOptions {Width=64,Height=64,Samples=1,ViewFrom=Vec3.UnitZ};
        CollectionAssert.AreEqual(CalculatedImage.RenderPng(z,o),CalculatedImage.RenderPng(z,o));
        var raw=CalculatedImage.LinearColour(100,new Vec3(.8,.3,.2));
        Assert.AreEqual(100,.2126*raw.X+.7152*raw.Y+.0722*raw.Z,1e-9);
        var png=CalculatedImage.RenderPng(z,o);
        var text=Encoding.UTF8.GetString(png);
        StringAssert.Contains(text,CalculatedImage.Model);StringAssert.Contains(text,CalculatedImage.Fingerprint(z));
    }
    [TestMethod]
    public void ResultsExchangeRetainsColourSourceAndCoordinateFrame()
    {
        var z=Plane();var d=CalcResultsFile.Build(new CalcResultsRun(),new[]{z},Array.Empty<Luminaire>());
        var origin=new Vector3(1,2,.5f);
        var display=LightingCalcFiles.Parse(Encoding.UTF8.GetBytes(d.Json),d.Body,"test",origin,DateTime.UtcNow,out string error);
        Assert.IsNotNull(display,error);var patch=display.Zones[0].Patches[0];
        Assert.AreEqual(42,patch.SourceTag);Assert.IsTrue(patch.Colour.HasValue);Assert.AreEqual(.8f,patch.Colour.Value.X,1e-6f);
        var restored=CalculatedImages.FromDisplay(display,origin).Single();
        Assert.AreEqual(0,restored.Surfaces[0].A.X,1e-6);
        Assert.AreEqual(z.Surfaces[0].Illuminance,restored.Surfaces[0].Illuminance,1e-6);
        Assert.AreEqual(42,restored.Surfaces[0].SourceTag);
        var header=JsonNode.Parse(d.Json);header["zones"][0]["appearance"]["count"]=1;
        var damaged=LightingCalcFiles.Parse(Encoding.UTF8.GetBytes(header.ToJsonString()),d.Body,"test",origin,DateTime.UtcNow,out error);
        Assert.IsTrue(damaged==null||damaged.Zones.Length==0);
    }
    [TestMethod]
    public void OlderExchangeHasNeutralAppearanceAndStillRenders()
    {
        var z=Plane();var d=CalcResultsFile.Build(new CalcResultsRun(),new[]{z},Array.Empty<Luminaire>());
        var header=JsonNode.Parse(d.Json);header["zones"][0].AsObject().Remove("appearance");
        var display=LightingCalcFiles.Parse(Encoding.UTF8.GetBytes(header.ToJsonString()),d.Body,"test",Vector3.Zero,DateTime.UtcNow,out string error);
        Assert.IsNotNull(display,error);Assert.IsNull(display.Zones[0].Patches[0].Colour);
        Assert.IsNotNull(CalculatedImage.Render(CalculatedImages.FromDisplay(display,Vector3.Zero)[0],new(){Width=64,Height=64,Samples=1}));
    }
    [TestMethod]
    public void NativePartialCachePreservesAppearanceWithoutRequiringTheOtherProduct()
    {
        var zone=Plane();
        var saved=new PreservedCalculationZone {InputRevision=new string('a',64),Definition=new CalculationZone {Key=zone.Key,Loops=new[]{new double[]{0,0,10,0,10,10,0,10}},Bottom=0,Top=9},
            Points=Array.Empty<double[]>(),Values=Array.Empty<double?>(),Direct=Array.Empty<double?>(),Patches=new[]{new double[]{0,0,0,10,0,0,10,10,0,0,0,1,.2,50,2,0}},
            Appearance=new[]{new double[]{42,.8,.3,.2}},Diagnostics=new SolveStats(),Types=new(),Spacing=2,Ceiling=9};
        var restored=saved.Restore();
        Assert.AreEqual(42,restored.Surfaces[0].SourceTag);Assert.AreEqual(.8,restored.Surfaces[0].Colour.Value.X,1e-9);
    }
}
