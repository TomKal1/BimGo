using System;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using BimGo.Format;
using Lighting.Baseline;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace BimGo.Tests;

[TestClass]
public sealed class LightingEngineTests
{
    static SceneCase Small() => SceneCase.Corpus().First();
    static CalcZone Solve(SceneCase s)
    {
        var input = s.Build();
        new LightingSolver(input.Sources, s.Options).Solve(input.Zone, input.Triangles);
        return input.Zone;
    }

    [TestMethod]
    public void KernelHasNoApplicationOrRevitAssemblyDependency()
    {
        var dependencies = typeof(LightingSolver).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.IsFalse(dependencies.Any(n => n.StartsWith("BimGo") || n.Contains("Revit") || n == "WWTools"));
    }

    [TestMethod]
    public void NativeFirstSolveNeedsNoImportedResults()
    {
        var zone = Solve(Small());
        Assert.AreEqual(30, zone.Points.Count);
        Assert.AreEqual(17.4487496693682, zone.Statistics.Average, 1e-9);
        Assert.IsTrue(zone.Surfaces.Count > 0);
    }

    [TestMethod]
    public void WallReflectanceRecomputesInterreflection()
    {
        var scene = Small(); var original = Solve(scene);
        scene.Reflectances.Wall = .1; var dark = Solve(scene);
        scene.Reflectances.Wall = .8; var bright = Solve(scene);
        CollectionAssert.AreEqual(original.Direct, dark.Direct);
        CollectionAssert.AreEqual(original.Direct, bright.Direct);
        Assert.IsTrue(bright.Statistics.Average > original.Statistics.Average);
        Assert.IsTrue(dark.Statistics.Average < original.Statistics.Average);
    }

    [TestMethod]
    public void MovingAndAddingSourcesChangesPhysicalLight()
    {
        var scene = Small(); var original = Solve(scene);
        scene.Sources[0].Position[0] -= 3;
        var moved = Solve(scene);
        Assert.IsTrue(original.Direct.Zip(moved.Direct, (a,b) => Math.Abs(a-b)).Max() > 1);
        scene.Sources.Add(new() { Position = new[] { 3.0,3,8.9 }, Ies = SceneCase.CosineIes(2000) });
        Assert.IsTrue(Solve(scene).Statistics.Average > moved.Statistics.Average);
        scene.Sources.RemoveAt(1); scene.Sources[0].Position[0] += 3;
        CollectionAssert.AreEqual(original.Illuminance, Solve(scene).Illuminance);
    }

    [TestMethod]
    public void EmitterFrameAndNearFieldArePreserved()
    {
        var near = SceneCase.Corpus().Single(s => s.Name == "near-field-obstruction").Build();
        Assert.AreEqual(8, near.Sources[0].SubSources.Count);
        Assert.AreEqual(20.0, near.Sources[0].NearFieldDistance);
        var asym = SceneCase.Corpus().Single(s => s.Name == "asymmetric-source").Build().Sources[0];
        var rotated = new Luminaire(asym.Position, asym.Nadir, Vec3.UnitY, asym.Photometry);
        var direction = new Vec3(1,0,-1).Normalized();
        Assert.IsTrue(Math.Abs(asym.IntensityToward(direction) - rotated.IntensityToward(direction)) > 100);
    }

    [TestMethod]
    public void ObstructedPointsRemainMissingInsteadOfZero()
    {
        var zone = Solve(SceneCase.Corpus().Single(s => s.Name == "near-field-obstruction"));
        Assert.AreEqual(6, zone.Illuminance.Count(double.IsNaN));
        Assert.AreEqual(24, zone.Statistics.Count);
    }

    [TestMethod]
    public void FullPhotometryAndUnsupportedTiltRemainVisible()
    {
        var ies = IesPhotometry.Parse(SceneCase.CosineIes(4000));
        Assert.AreEqual(181, ies.VerticalAngles.Count);
        Assert.IsFalse(ies.IsEstimated);
        Assert.IsNotNull(ies.RawText);
        Assert.IsTrue(IesPhotometry.Parse("IESNA:LM-63-2002\nTILT=INCLUDE\n1 1 0 .5\n1 -1 1 2 1 1 1 0 0 0\n1 1 10\n0 90\n0\n100 0\n").HadTiltData);
        // Compatibility parser flags tilt but ignores the factors. Do not claim tilt fidelity.
    }

    [TestMethod]
    public void CanceledSolveDoesNotReturnASuccessfulRun()
    {
        var input = Small().Build(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => new LightingSolver(input.Sources, new()).Solve(input.Zone, input.Triangles, cancel: canceled.Token));
    }

    [TestMethod]
    public void IterationCapAndSpatialSamplingAreSeparate()
    {
        var scene = Small(); var input = scene.Build();
        var solver = new LightingSolver(input.Sources, new() { MaxIterations = 1 });
        solver.Solve(input.Zone, input.Triangles);
        Assert.AreEqual(1, solver.LastIterations);
        Assert.IsTrue(Solve(scene).Statistics.Average > input.Zone.Statistics.Average);
        Assert.AreEqual(96, SolverOptions.ForQuality(CalcQuality.Normal, true).PatchRays);
        Assert.AreEqual(192, SolverOptions.ForQuality(CalcQuality.High, true).PatchRays);
    }

    [TestMethod]
    public void ActualProducerAndBimGoReaderAgreeWithoutARevitHost()
    {
        var zone = Solve(Small());
        var data = CalcResultsFile.Build(new CalcResultsRun { Tool = "BimGo characterization", CalcSetName = "Offline test" }, new[] { zone }, Array.Empty<Luminaire>());
        var imported = LightingCalcFiles.Parse(Encoding.UTF8.GetBytes(data.Json), data.Body, "offline", Vector3.Zero, DateTime.UtcNow, out string error);
        Assert.IsNotNull(imported, error);
        Assert.AreEqual(0, imported.Warnings.Count);
        Assert.AreEqual(zone.Points.Count, imported.Zones[0].PointValues.Length);
        for (int i = 0; i < zone.Points.Count; i++) Assert.AreEqual((float)zone.Illuminance[i], imported.Zones[0].PointValues[i]);
        Assert.AreEqual(zone.Surfaces.Count, imported.Zones[0].Patches.Length);
        for (int i = 0; i < zone.Surfaces.Count; i++)
        {
            var p = imported.Zones[0].Patches[i];
            Assert.AreEqual((float)zone.Surfaces[i].Illuminance, p.Illuminance);
            Assert.AreEqual((float)zone.Surfaces[i].Reflectance, p.Reflectance);
            double luminance = p.Reflectance * p.Illuminance * 10.763910 / Math.PI;
            Assert.AreEqual(luminance, p.Luminance, 1e-4);
        }
    }
}
