using System;
using System.Linq;
using System.Numerics;
using BimGo.Calculations;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BimGo.Tests;

public sealed partial class NativeLightingTests
{
    static RoomInfo Room(string number, float x, float y, float z) => new()
    {
        Number = number, Name = "Room", Min = new Vector2(x, y), Max = new Vector2(x + 3, y + 3), BottomZ = z, TopZ = z + 3,
        Loops = new[] { new[] { new Vector2(x, y), new Vector2(x + 3, y), new Vector2(x + 3, y + 3), new Vector2(x, y + 3) } }
    };

    static SceneData With(SceneData s, RoomInfo[] rooms = null, SceneVertex[] vertices = null, uint[] indices = null, ElementRecord[] elements = null) => new()
    {
        Vertices = vertices ?? s.Vertices, Indices = indices ?? s.Indices, Elements = elements ?? s.Elements, Rooms = rooms ?? s.Rooms,
        Levels = s.Levels, OriginOffset = s.OriginOffset, ModelTitle = s.ModelTitle, Settings = s.Settings, Bounds = s.Bounds,
        Lighting = s.Lighting, CategoryLoaded = s.CategoryLoaded, CategoryElementCounts = s.CategoryElementCounts
    };

    [TestMethod]
    public void BisectSplitsOneStoreyAcrossItsWiderDirection()
    {
        var scene = new SceneData { Rooms = new[] { Room("4", 30, 0, 0), Room("1", 0, 1, 0), Room("3", 20, 0, 0), Room("2", 10, 2, 0) } };
        var (first, second) = RoomGroups.Bisect(scene, new[] { 0, 1, 2, 3 });
        CollectionAssert.AreEquivalent(new[] { 1, 3 }, first);
        CollectionAssert.AreEquivalent(new[] { 0, 2 }, second);
    }

    [TestMethod]
    public void BisectKeepsAStoreyTogetherBeforeBalancingTheHalves()
    {
        var scene = new SceneData { Rooms = new[] { Room("101", 0, 0, 0), Room("102", 10, 0, 0), Room("103", 20, 0, 0), Room("201", 0, 0, 4) } };
        var (first, second) = RoomGroups.Bisect(scene, new[] { 3, 2, 1, 0 });
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, first);
        CollectionAssert.AreEquivalent(new[] { 3 }, second);
        Assert.ThrowsExactly<ArgumentException>(() => RoomGroups.Bisect(scene, new[] { 0 }));
    }

    [TestMethod]
    public void SplitPartKeepsTheEarlierPartAndPublishesEveryRoom()
    {
        // The second room shares the first one's enclosure, so each part has light and geometry of its own.
        var single = Scene(); var office = single.Rooms[0];
        var scene = With(single, rooms: new[] { office, new RoomInfo { Number = "102", Name = "Office copy", Min = office.Min, Max = office.Max,
            BottomZ = office.BottomZ, TopZ = office.TopZ, Loops = office.Loops } });
        var set = Set(); set.RoomKeys = new[] { ExportedSceneInput.RoomKey(scene, 0) };
        var first = Run(Input(scene, set));

        set.Rooms = new[] { 0, 1 }; set.RoomKeys = new[] { 0, 1 }.Select(i => ExportedSceneInput.RoomKey(scene, i)).ToArray();
        string second = set.RoomKeys[1];
        var part = ExportedSceneInput.Build(scene, set, NoEdits, new(), domainZoneKeys: new[] { second });
        StringAssert.Contains(part.Warnings.First(w => w.StartsWith("Transport domain", StringComparison.Ordinal)), "rooms solved in this part");
        var merged = NativeCalculator.Run(part, Vector3.Zero, previous: first, zoneKeys: new[] { second });

        Assert.AreEqual(2, merged.Zones.Count);
        CollectionAssert.AreEqual(first.Zones[0].Illuminance, merged.Zones.First(z => z.Key == set.RoomKeys[0]).Illuminance);
        Assert.AreEqual(first.ScenarioHash, merged.ZoneRevisions[set.RoomKeys[0]]);
        Assert.AreEqual(2, merged.Display.Zones.Length);
        Assert.IsTrue(merged.Zones.First(z => z.Key == second).Statistics.Average > 0);
    }

    [TestMethod]
    public void OversizedDomainIsReportedAsSplittable()
    {
        var scene = Scene(); int count = 250_001;
        var vertices = scene.Vertices.ToList(); var indices = scene.Indices.ToList();
        int start = indices.Count;
        for (int i = 0; i < count; i++)
        {
            var p = new Vector3(.5f + i % 500 * .001f, .5f + i / 500 * .001f, .1f);
            foreach (var q in new[] { p, p + new Vector3(.0005f, 0, 0), p + new Vector3(0, .0005f, 0) })
            { indices.Add((uint)vertices.Count); vertices.Add(new(q, Vector3.UnitZ, 0xffffffff)); }
        }
        var clutter = new ElementRecord { ElementId = 900, UniqueId = "clutter", FamilyType = "Clutter", CategoryIndex = CategoryCatalog.Find("floors").Index,
            Bounds = new Aabb(new Vector3(.5f, .5f, .1f), new Vector3(1.5f, 1.5f, .1f)), OpaqueStart = start, OpaqueCount = count * 3 };
        scene = With(scene, vertices: vertices.ToArray(), indices: indices.ToArray(), elements: scene.Elements.Append(clutter).ToArray());
        var outcome = NativeCalculationJob.Execute(scene, Set(), NoEdits, new(), Vector3.Zero);
        Assert.IsNull(outcome.Run);
        Assert.IsTrue(outcome.TooLarge && outcome.NeedsInput, outcome.Message);
        StringAssert.Contains(outcome.Message, "250,000");
    }
}
