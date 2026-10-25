using System;
using System.Linq;
using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round Z-A: room data in a .bimgo (identity, level, area in model.json; parameters in rooms.json), older files
    /// without them, numbers read out of text, the target check, tag placement and the overlay settings.
    /// </summary>
    [TestClass]
    public sealed class RoomDataTests
    {
        private static RoomInfo Box(string number, string name, float x0, float y0, float x1, float y1, RoomParameter[] parameters = null) => new()
        {
            Number = number, Name = name, BottomZ = 0f, TopZ = 3f,
            Loops = new[] { new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) } },
            Min = new Vector2(x0, y0), Max = new Vector2(x1, y1),
            Parameters = parameters ?? Array.Empty<RoomParameter>()
        };

        private static RoomInfo[] SampleRooms()
        {
            RoomInfo a = Box("G01", "Lobby", 0, 0, 4, 3, new[]
            {
                new RoomParameter("Area", "12.00 m²", 12.0),
                new RoomParameter("Required area", "12.5 m²", 12.5),
                new RoomParameter("Department", "Front of house", double.NaN)
            });
            RoomInfo b = Box("G02", "Office", 4, 0, 8, 3, new[]
            {
                new RoomParameter("Area", "12.00 m²", 12.0), // pooled with the first room's value
                new RoomParameter("Department", "Admin", double.NaN)
            });
            RoomInfo c = Box("G03", "Store", 8, 0, 9, 1);
            return new[]
            {
                new RoomInfo
                {
                    Number = a.Number, Name = a.Name, BottomZ = a.BottomZ, TopZ = a.TopZ, Loops = a.Loops, Min = a.Min, Max = a.Max,
                    Parameters = a.Parameters, UniqueId = "room-a", ElementId = 9001, LevelName = "Level 1", Area = 12f
                },
                b, c
            };
        }

        [TestMethod]
        public void RoomData_RoundTripsThroughBimgo()
        {
            using var folder = new TempFolder();
            string path = folder.File("rooms.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(rooms: SampleRooms()), TestData.Writer, FileKinds.SAVE, out string error), error);

            RoomInfo[] rooms = BimGoReader.Read(path, new LaunchSettings(), out error).Scene.Rooms;
            Assert.IsNotNull(rooms, error);
            Assert.AreEqual(3, rooms.Length);
            Assert.AreEqual("room-a", rooms[0].UniqueId);
            Assert.AreEqual(9001, rooms[0].ElementId);
            Assert.AreEqual("Level 1", rooms[0].LevelName);
            Assert.AreEqual(12f, rooms[0].Area, 1e-4f);
            Assert.AreEqual(3, rooms[0].Parameters.Length);
            Assert.IsTrue(rooms[0].TryGetParameter("required AREA", out RoomParameter required));
            Assert.AreEqual(12.5, required.Number, 1e-9);
            Assert.AreEqual("12.5 m²", required.Value);
            Assert.IsTrue(rooms[0].TryGetParameter("Department", out RoomParameter department));
            Assert.IsFalse(department.IsNumeric);

            Assert.AreEqual(2, rooms[1].Parameters.Length);
            Assert.AreEqual(12.0, rooms[1].Parameters[0].Number, 1e-9);
            Assert.IsNull(rooms[1].UniqueId);
            Assert.AreEqual(-1, rooms[1].ElementId);
            Assert.IsTrue(float.IsNaN(rooms[1].Area));
            Assert.AreEqual(0, rooms[2].Parameters.Length);
        }

        [TestMethod]
        public void RoomData_OlderFilesAndNoParametersLeaveTheEntryOut()
        {
            // No room has parameters: no rooms.json, rooms read with none (and no identity)
            Assert.IsNull(RoomDataFiles.ToDto(TestData.BuildScene().Rooms));
            using var folder = new TempFolder();
            string path = folder.File("plain.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(), TestData.Writer, FileKinds.SAVE, out string error), error);
            RoomInfo room = BimGoReader.Read(path, new LaunchSettings(), out error).Scene.Rooms.Single();
            Assert.AreEqual(0, room.Parameters.Length);
            Assert.IsTrue(float.IsNaN(room.Area));
            Assert.IsNull(room.LevelName);

            // Bad indices and repeated names in a hand-edited entry are skipped
            var pools = new RoomDataFiles.Pools(new RoomDataDto
            {
                Names = new[] { "Area", "area" }, Values = new[] { "5" }, Numbers = new double?[] { 5 },
                Rows = new[] { new[] { 0, 0, 1, 0, 7, 0, 0, 9 } }
            });
            RoomParameter[] row = pools.Row(0);
            Assert.AreEqual(1, row.Length);
            Assert.AreEqual(5.0, row[0].Number);
            Assert.AreEqual(0, pools.Row(3).Length);
            Assert.AreEqual(0, new RoomDataFiles.Pools(null).Row(0).Length);
        }

        [TestMethod]
        public void ParseNumber_ReadsTheFirstNumber()
        {
            Assert.AreEqual(45.0, RoomValues.ParseNumber("45"), 1e-9);
            Assert.AreEqual(45.5, RoomValues.ParseNumber("45.5 m²"), 1e-9);
            Assert.AreEqual(45.5, RoomValues.ParseNumber("45,5 m²"), 1e-9);
            Assert.AreEqual(1234.5, RoomValues.ParseNumber("1,234.5"), 1e-9);
            Assert.AreEqual(1234.5, RoomValues.ParseNumber("1.234,5"), 1e-9);
            Assert.AreEqual(1234.0, RoomValues.ParseNumber("1,234"), 1e-9);
            Assert.AreEqual(1234.5, RoomValues.ParseNumber("1 234,5"), 1e-9);
            Assert.AreEqual(-3.0, RoomValues.ParseNumber("−3 °C"), 1e-9);
            Assert.AreEqual(12.0, RoomValues.ParseNumber("Approx. 12 m²"), 1e-9);
            Assert.AreEqual(0.5, RoomValues.ParseNumber(".5"), 1e-9);
            Assert.IsTrue(double.IsNaN(RoomValues.ParseNumber("Office")));
            Assert.IsTrue(double.IsNaN(RoomValues.ParseNumber("")));
            Assert.IsTrue(double.IsNaN(RoomValues.ParseNumber("-")));
            Assert.IsTrue(double.IsNaN(RoomValues.ParseNumber(null)));
        }

        [TestMethod]
        public void Checks_GreenAmberRed()
        {
            // Within ± 5 %: 100 vs 100 green, 107 amber (within 10 %), 89 red
            Assert.AreEqual(RoomCheckStatus.Pass, RoomChecks.Evaluate(100, 100, RoomCheckRule.Within, 5, out double d));
            Assert.AreEqual(0.0, d, 1e-9);
            Assert.AreEqual(RoomCheckStatus.Pass, RoomChecks.Evaluate(105, 100, RoomCheckRule.Within, 5, out _));
            Assert.AreEqual(RoomCheckStatus.Near, RoomChecks.Evaluate(107, 100, RoomCheckRule.Within, 5, out d));
            Assert.AreEqual(7.0, d, 1e-9);
            Assert.AreEqual(RoomCheckStatus.Fail, RoomChecks.Evaluate(89, 100, RoomCheckRule.Within, 5, out d));
            Assert.AreEqual(-11.0, d, 1e-9);

            // At least: bigger is always fine; at most: smaller is
            Assert.AreEqual(RoomCheckStatus.Pass, RoomChecks.Evaluate(150, 100, RoomCheckRule.AtLeast, 5, out _));
            Assert.AreEqual(RoomCheckStatus.Near, RoomChecks.Evaluate(92, 100, RoomCheckRule.AtLeast, 5, out _));
            Assert.AreEqual(RoomCheckStatus.Fail, RoomChecks.Evaluate(80, 100, RoomCheckRule.AtLeast, 5, out _));
            Assert.AreEqual(RoomCheckStatus.Pass, RoomChecks.Evaluate(50, 100, RoomCheckRule.AtMost, 5, out _));
            Assert.AreEqual(RoomCheckStatus.Fail, RoomChecks.Evaluate(120, 100, RoomCheckRule.AtMost, 5, out _));

            // Zero tolerance: exact only; missing values or a zero target: no result
            Assert.AreEqual(RoomCheckStatus.Pass, RoomChecks.Evaluate(100, 100, RoomCheckRule.Within, 0, out _));
            Assert.AreEqual(RoomCheckStatus.Fail, RoomChecks.Evaluate(100.5, 100, RoomCheckRule.Within, 0, out _));
            Assert.AreEqual(RoomCheckStatus.None, RoomChecks.Evaluate(double.NaN, 100, RoomCheckRule.Within, 5, out d));
            Assert.IsTrue(double.IsNaN(d));
            Assert.AreEqual(RoomCheckStatus.None, RoomChecks.Evaluate(10, 0, RoomCheckRule.Within, 5, out _));
        }

        [TestMethod]
        public void Label_SitsInsideLShapedRooms()
        {
            // An L: the bounds' middle (5, 5) is outside the room
            var l = new RoomInfo
            {
                Number = "1", Name = "L",
                Loops = new[] { new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 3), new Vector2(3, 3), new Vector2(3, 10), new Vector2(0, 10) } },
                Min = Vector2.Zero, Max = new Vector2(10, 10)
            };
            Assert.IsFalse(l.Contains(new Vector2(5, 5)));
            Vector2 p = RoomTagPlacement.Place(l);
            Assert.IsTrue(l.Contains(p));
            Assert.IsTrue(l.DistanceToBoundary(p) > 1.2f, $"{p}: {l.DistanceToBoundary(p)}");

            // A plain box: near its middle
            Vector2 box = RoomTagPlacement.Place(Box("2", "Box", 0, 0, 6, 4));
            Assert.AreEqual(3f, box.X, 0.3f);
            Assert.AreEqual(2f, box.Y, 0.3f);
        }

        [TestMethod]
        public void OverlaySettings_CleanDefaultsAndVisibilityRoundTrip()
        {
            Assert.IsTrue(new RoomOverlaySettings().IsDefault);
            Assert.IsNull(new VisibilitySettings { Rooms = new RoomOverlaySettings() }.Clean().Rooms);
            Assert.IsTrue(new VisibilitySettings { Rooms = new RoomOverlaySettings() }.Clean().IsEmpty);

            var bad = new RoomOverlaySettings
            {
                Height = 99f, Distance = float.NaN, Tolerance = -4f, Rule = "sideways", Value = " ", Target = "  ",
                Shown = new() { "A", "a", "B", null, "C", "D", "E" }
            }.Clean();
            Assert.AreEqual(RoomOverlaySettings.MAX_HEIGHT, bad.Height);
            Assert.AreEqual(RoomOverlaySettings.DEFAULT_DISTANCE, bad.Distance);
            Assert.AreEqual(0f, bad.Tolerance);
            Assert.AreEqual(RoomOverlaySettings.RULE_WITHIN, bad.Rule);
            Assert.AreEqual(RoomOverlaySettings.DEFAULT_VALUE, bad.Value);
            Assert.IsNull(bad.Target);
            CollectionAssert.AreEqual(new[] { "A", "B", "C", "D" }, bad.Shown);

            var copy = bad.Copy();
            copy.Shown.Add("Z");
            Assert.AreEqual(4, bad.Shown.Count);

            using var folder = new TempFolder();
            string path = folder.File("overlay.bimgo");
            BimGoDocument document = TestData.BuildDocument();
            var overlay = new RoomOverlaySettings { Tags = true, Checks = true, Target = "Required area", CheckRule = RoomCheckRule.AtLeast, Tolerance = 2.5f, Shown = new() { "Department" } };
            var saved = new BimGoDocument { Scene = document.Scene, Comments = document.Comments, Visibility = new VisibilitySettings { Rooms = overlay }.Clean() };
            Assert.IsTrue(BimGoWriter.Write(path, saved, TestData.Writer, FileKinds.SAVE, out string error), error);
            RoomOverlaySettings read = BimGoReader.Read(path, new LaunchSettings(), out error).Visibility?.Rooms;
            Assert.IsNotNull(read, error);
            Assert.IsTrue(read.Tags);
            Assert.IsTrue(read.Checks);
            Assert.AreEqual("Required area", read.Target);
            Assert.AreEqual(RoomCheckRule.AtLeast, read.CheckRule);
            Assert.AreEqual(2.5f, read.Tolerance);
            CollectionAssert.AreEqual(new[] { "Department" }, read.Shown);
        }
    }
}
