using System;
using System.IO;
using System.Linq;
using System.Numerics;
using BimGo.Calculations;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BimGo.Tests;

/// <summary>BimGo's port of the WWTools main calculation process: auto mounting, fixture ownership and the storey-cluster solve.</summary>
public sealed partial class NativeLightingTests
{
    static CalculationInput TwoRooms()
    {
        var input = Input(Scene()); var room = input.Zones[0];
        input.Set.Rooms = new[] { 0, 1 }; input.Set.RoomKeys = Array.Empty<string>();
        input.Zones.Add(new CalculationZone { Key = "room-two", Name = "Other room", Number = "102", Bottom = room.Bottom, Top = room.Top,
            Loops = room.Loops, Level = room.Level, LevelNames = room.LevelNames, LevelElevations = room.LevelElevations, LevelElevation = room.LevelElevation });
        return input;
    }

    [TestMethod]
    public void AutoMountingRecessesASourceAtTheCeilingUnderside()
    {
        var scene = Scene(); var light = scene.Lighting.Lights[0];
        scene.Lighting.Lights[0] = new LightSource { Element = light.Element, Position = light.Position, CalculationOrigin = light.Position,
            Aim = light.Aim, Across = light.Across, Ies = light.Ies, Lumens = light.Lumens };
        var set = Set(); set.CorrectRecessedSources = true;
        var input = Input(scene, set);
        Assert.IsTrue(input.Emitters[0].AutoMount);
        Assert.IsTrue(input.Triangles.Any(t => t.Ceiling));
        var run = Run(input);
        var source = run.Input.Emitters[0];
        StringAssert.StartsWith(source.Placement, "recessed");
        Assert.AreEqual(9 - 1.0 / 12, source.Position[2], 1e-6);
        Assert.AreEqual(8.9, source.OriginalPosition[2], 1e-5);
        StringAssert.Contains(run.ReportHtml, "Auto mounting");
    }

    [TestMethod]
    public void RoomsOnOneStoreyShareOneClusterAndEachSourceHasOneOwner()
    {
        var run = Run(TwoRooms());
        StringAssert.Contains(run.Log, "Cluster 1/1 (2 zone(s))");
        Assert.AreEqual(1, run.Zones.Sum(z => z.LuminaireCount));
        CollectionAssert.AreEqual(run.Zones[0].Illuminance, run.Zones[1].Illuminance);
    }

    [TestMethod]
    public void PartialRerunIsRefusedAfterASharedSettingChanges()
    {
        var first = Run(TwoRooms());
        var next = CalculationInput.Parse(first.SceneBytes); next.Set.Spacing = 1;
        var error = Assert.ThrowsExactly<InvalidDataException>(() => NativeCalculator.Run(next, Vector3.Zero, previous: first, zoneKeys: new[] { next.Zones[0].Key }));
        StringAssert.Contains(error.Message, "grid 2 ft");
    }

    [TestMethod]
    public void StoreysGiveZonesTheirLevelAndTheRegionTheLevelAbove()
    {
        var input = Input(Scene());
        CollectionAssert.AreEqual(new[] { "Level 1" }, input.Storeys.Select(l => l.Name).ToArray());
        Assert.AreEqual(0, input.Zones[0].LevelElevation.Value, 1e-9);
        Assert.IsTrue(input.Warnings.Any(w => w.Contains("up to the level above")));
        input.Storeys.Add(new CalculationLevel { Name = "Level 2", Elevation = 12 });
        var zone = Run(input).Zones[0];
        Assert.AreEqual(1, zone.Levels.Count);
        Assert.AreEqual(12, zone.LevelAboveElevation, 1e-9);
    }
}
