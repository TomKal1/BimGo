using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using BimGo.Calculations;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BimGo.Tests;

public sealed partial class NativeLightingTests
{
    static SceneData TwoFixtures()
    {
        var scene = Scene(); int original = scene.Lighting.Lights[0].Element;
        var a = scene.Elements[original]; var l = scene.Lighting.Lights[0];
        return new SceneData { Vertices = scene.Vertices, Indices = scene.Indices,
            Elements = scene.Elements.Append(new ElementRecord { ElementId = 200, UniqueId = "fixture-200", FamilyType = a.FamilyType,
                Name = a.Name, CategoryIndex = a.CategoryIndex, Bounds = a.Bounds, Pivot = a.Pivot }).ToArray(),
            Rooms = scene.Rooms, Levels = scene.Levels, OriginOffset = scene.OriginOffset, ModelTitle = scene.ModelTitle,
            Settings = scene.Settings, Bounds = scene.Bounds,
            Lighting = new LightingData { IesFiles = scene.Lighting.IesFiles, Lights = new[] { l,
                new LightSource { Element = scene.Elements.Length, Position = l.Position, Aim = l.Aim, Across = l.Across, Ies = l.Ies, Lumens = l.Lumens } } } };
    }

    [TestMethod]
    public void EmergencyModeUsesOnlySelectedInstancesOfTheSameFixtureTypeAndTheirOutput()
    {
        var scene = TwoFixtures(); var set = Set();
        set.Luminaires = new[] { new NativeLuminaireSetting { Key = "fixture-100", Emergency = true, EmergencyOutput = .1 } };
        var normal = Input(scene, set);
        Assert.AreEqual(2, normal.Emitters.Count);
        set.EmergencyOnly = true;
        var emergency = Input(scene, set);
        Assert.AreEqual(1, emergency.Emitters.Count);
        Assert.AreEqual("fixture-100", emergency.Emitters[0].FixtureKey);
        Assert.AreEqual(.08, emergency.Emitters[0].Multiplier, 1e-10);
        set.Interreflection = false;
        var reduced = Run(Input(scene, set));
        var baselineSet = Set(); baselineSet.Interreflection = false;
        var baseline = Run(Input(Scene(), baselineSet));
        Assert.AreEqual(baseline.Zones[0].Statistics.Average * .1, reduced.Zones[0].Statistics.Average, 1e-6);
        set.Luminaires[0].EmergencyOutput = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => Input(scene, set));
    }

    [TestMethod]
    public void GeometryExclusionsArePhysicalAndIndependentOfDisplayCategories()
    {
        var scene = Scene(); var set = Set(); var all = Input(scene, set);
        set.ExcludedCategories = new[] { "walls" };
        var noWalls = Input(scene, set);
        Assert.IsTrue(noWalls.Triangles.Count < all.Triangles.Count);
        Assert.AreEqual(all.Emitters.Count, noWalls.Emitters.Count);
        set.ExcludedCategories = Array.Empty<string>();
        set.ExcludedElements = new[] { "surface-1" };
        Assert.AreEqual(noWalls.Triangles.Count, Input(scene, set).Triangles.Count);
        set.ExcludedElements = new[] { "fixture-100" };
        Assert.ThrowsExactly<InvalidDataException>(() => Input(scene, set));
    }

    [TestMethod]
    public void ManualPointExclusionSurvivesRoundtripWithoutMovingTheWorkplane()
    {
        var scene = Scene(); var set = Set(); var first = Run(Input(scene, set));
        var point = first.Zones[0].Points[0];
        set.ExcludedPoints = new[] { new NativeExcludedPoint { ZoneKey = first.Zones[0].Key, Position = new[] { point.X, point.Y, point.Z } } };
        var saved = NativeLightingFiles.Parse(NativeLightingFiles.Serialize(new NativeLightingDocument { Sets = { set } }), out var error);
        Assert.IsNull(error);
        var next = Run(Input(scene, saved.Sets[0]));
        Assert.AreEqual(first.Zones[0].Points.Count - 1, next.Zones[0].Points.Count);
        Assert.IsTrue(next.Zones[0].Points.All(p => p.Z == point.Z));
        Assert.AreEqual(next.Zones[0].Points.Count, next.Zones[0].Statistics.Count);
        Assert.AreEqual(next.ScenarioHash, CalculationInput.Parse(next.SceneBytes).ContentHash());
    }

    [TestMethod]
    public void SourceHeightRemainsExportedUnlessExplicitlyOverridden()
    {
        var scene = Scene(); var set = Set();
        Assert.IsFalse(new NativeLightingSet().CorrectRecessedSources);
        double original = Input(scene, set).Emitters[0].Position[2];
        set.Luminaires = new[] { new NativeLuminaireSetting { Key = "fixture-100", HeightOffset = -.25 } };
        Assert.AreEqual(original - .25, Input(scene, set).Emitters[0].Position[2], 1e-5);
        set.Luminaires[0].EmergencyOutput = double.NaN;
        Assert.ThrowsExactly<InvalidDataException>(() => set.Validate());
    }

    [TestMethod]
    public void ExportedOriginIsUsedAndRoundtripsWithoutChangingThePreviewPosition()
    {
        var scene = Scene(); var light = scene.Lighting.Lights[0];
        Vector3 origin = light.Position + Vector3.UnitZ * .3048f;
        scene.Lighting.Lights[0] = new LightSource { Element = light.Element, Position = light.Position, CalculationOrigin = origin,
            Aim = light.Aim, Across = light.Across, Ies = light.Ies, Lumens = light.Lumens };
        var set = Set();
        Assert.AreEqual(origin.Z / .3048, Input(scene, set).Emitters[0].Position[2], 1e-5);
        set.Luminaires = new[] { new NativeLuminaireSetting { Key = "fixture-100", UsePreviewLensPosition = true } };
        Assert.AreEqual(light.Position.Z / .3048, Input(scene, set).Emitters[0].Position[2], 1e-5);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bimgo");
        try
        {
            Assert.IsTrue(BimGoWriter.Write(path, new BimGoDocument { Scene = scene }, new WriterInfo("Test", "1"), FileKinds.SAVE, out string error), error);
            var loaded = BimGoReader.Read(path, new LaunchSettings(), out error); Assert.IsNotNull(loaded, error);
            Assert.AreEqual(origin, loaded.Scene.Lighting.Lights[0].CalculationOrigin);
            Assert.AreEqual(light.Position, loaded.Scene.Lighting.Lights[0].Position);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void PartialRerunKeepsTheOtherRoomsValuesAndTheirOriginalRevision()
    {
        var input = Input(Scene()); var room = input.Zones[0];
        input.Set.Rooms = new[] { 0, 1 }; input.Set.RoomKeys = Array.Empty<string>();
        input.Zones.Add(new CalculationZone { Key = "room-two", Name = "Other room", Number = "102", Bottom = room.Bottom,
            Top = room.Top, Loops = room.Loops, Level = room.Level, LevelNames = room.LevelNames, LevelElevations = room.LevelElevations });
        var first = Run(input);
        var nextInput = CalculationInput.Parse(first.SceneBytes); nextInput.Emitters[0].Multiplier *= .5;
        var next = NativeCalculator.Run(nextInput, Vector3.Zero, previous: first, zoneKeys: new[] { room.Key });
        CollectionAssert.AreEqual(first.Zones[1].Illuminance, next.Zones[1].Illuminance);
        Assert.AreEqual(first.ScenarioHash, next.ZoneRevisions["room-two"]);
        Assert.AreEqual(next.ScenarioHash, next.ZoneRevisions[room.Key]);
        Assert.AreNotEqual(first.ScenarioHash, next.ScenarioHash);
        Assert.IsTrue(next.Zones[0].Statistics.Average < first.Zones[0].Statistics.Average);
        StringAssert.Contains(next.ReportHtml, "Partial rerun");
        StringAssert.Contains(Encoding.UTF8.GetString(next.StructuredResults), first.ScenarioHash);
        Assert.AreEqual(next.ScenarioHash, CalculationInput.Parse(next.SceneBytes).ContentHash());
        var replay = Run(CalculationInput.Parse(next.SceneBytes));
        Assert.AreEqual(next.ScenarioHash, replay.ScenarioHash);
        CollectionAssert.AreEqual(next.Zones[1].Illuminance, replay.Zones[1].Illuminance);
        CollectionAssert.AreEqual(next.Zones[0].Illuminance, replay.Zones[0].Illuminance);
        Assert.ThrowsExactly<InvalidDataException>(() => NativeCalculator.Run(nextInput, Vector3.Zero, zoneKeys: new[] { room.Key }));
    }
}
