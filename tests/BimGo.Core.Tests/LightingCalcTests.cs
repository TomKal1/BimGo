using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using BimGo.Format;
using BimGo.Scene;
using BimGo.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Fork build LC-B: lighting calc results files (WW Tools' "lighting-calc-results" v1): reading, damage, coordinates,
    /// colours, corner values, zones, the model-folder list and .bimgo embedding. The files are made here from the
    /// spec (WW Tools' docs/lighting-calcs/results-format.md), not by WW Tools.
    /// </summary>
    [TestClass]
    public sealed class LightingCalcTests
    {
        #region A writer from the spec

        /// <summary>A 20 × 16 ft room (two floor triangles, a wall), 4 points, one isolux segment, with extra fields to ignore.</summary>
        private sealed class SpecFile
        {
            public readonly List<float> Body = new();
            public long PointOffset, PatchOffset, LineOffset;
            public int PointCount = 4, PatchCount = 3;
            public string Version = "1";
            public string Units = "\"length\":\"ft\",\"coordinates\":\"revit-internal\",\"illuminance\":\"fc\",\"report\":\"fc\"";
            public long? BinBytesOverride;

            public SpecFile()
            {
                PointOffset = 0;
                float[][] points = { new[] { 5f, 4f, 2.5f, 30f, 25f }, new[] { 15f, 4f, 2.5f, 40f, 35f }, new[] { 5f, 12f, 2.5f, float.NaN, float.NaN }, new[] { 15f, 12f, 2.5f, 20f, 15f } };
                foreach (float[] p in points) { Body.AddRange(p); }
                PatchOffset = Body.Count * 4;
                // Floor: two triangles sharing an edge (the shared corners average); E 10 and 30, ρ 0.2
                Body.AddRange(new[] { 0f, 0f, 0f, 20f, 0f, 0f, 20f, 16f, 0f, 0f, 0f, 1f, 10f, 0.2f, 2f * 2 });
                Body.AddRange(new[] { 0f, 0f, 0f, 20f, 16f, 0f, 0f, 16f, 0f, 0f, 0f, 1f, 30f, 0.2f, 2f * 2 });
                // A covered wall patch (flags: covered, class Wall)
                Body.AddRange(new[] { 0f, 0f, 0f, 0f, 16f, 0f, 0f, 16f, 9f, 1f, 0f, 0f, 99f, 0.5f, 1f + 2f * 1 });
                LineOffset = Body.Count * 4;
                Body.AddRange(new[] { 2f, 2f, 2.5f, 18f, 2f, 2.5f });
            }

            public byte[] Bin()
            {
                var bytes = new byte[Body.Count * 4];
                for (int i = 0; i < Body.Count; i++) { BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), Body[i]); }
                return bytes;
            }

            public byte[] Json(string zoneExtra = "")
            {
                long bin = BinBytesOverride ?? Body.Count * 4L;
                string json = "{\"format\":\"lighting-calc-results\",\"version\":" + Version + ",\"tool\":\"Test\",\"future\":{\"x\":[1,2]}," +
                    "\"calcSet\":{\"id\":\"d339c539\",\"name\":\"Level 1 Offices\"}," +
                    "\"model\":{\"title\":\"Tower\",\"keySource\":\"local:c:\\\\tower.rvt\"}," +
                    "\"run\":{\"utc\":\"2026-10-22T14:03:00Z\",\"interreflections\":true,\"quality\":\"Normal\"}," +
                    "\"units\":{" + Units + "},\"scale\":{\"min\":0,\"max\":50,\"label\":\"Illuminance\"},\"binBytes\":" + bin + "," +
                    "\"zones\":[{\"key\":\"uid-101\",\"number\":\"101\",\"name\":\"Café Ü\",\"kind\":\"Space\",\"level\":\"Level 1\"," +
                    "\"bottomZ\":0,\"topZ\":9,\"ceilingZ\":9,\"workPlane\":2.5,\"spacing\":2," +
                    "\"stats\":{\"count\":3,\"avg\":30,\"max\":40,\"min\":20,\"avgMin\":1.5,\"maxMin\":2,\"lpd\":0.4}," +
                    "\"target\":{\"avg\":30,\"avgMin\":3,\"result\":\"Meets\"},\"loops\":[[[0,0],[20,0],[20,16],[0,16]]]," +
                    "\"points\":{\"offset\":" + PointOffset + ",\"count\":" + PointCount + "}," +
                    "\"patches\":{\"offset\":" + PatchOffset + ",\"count\":" + PatchCount + "}," +
                    "\"isolines\":[{\"value\":25,\"target\":false,\"offset\":" + LineOffset + ",\"count\":1}]" + zoneExtra + "}," +
                    "{\"key\":\"uid-102\",\"number\":\"102\",\"name\":\"Store\",\"bottomZ\":0,\"topZ\":9,\"stats\":{\"count\":0}," +
                    "\"loops\":[[[20,0],[30,0],[30,16],[20,16]]],\"points\":{\"offset\":0,\"count\":0},\"patches\":{\"offset\":0,\"count\":0},\"isolines\":[]}]," +
                    "\"luminaires\":[{\"id\":1005,\"type\":\"Troffer\",\"position\":[5,4,8.9],\"nadir\":[0,0,-1],\"axis0\":[1,0,0],\"multiplier\":0.85,\"watts\":32}]}";
                return Encoding.UTF8.GetBytes(json);
            }
        }

        private static readonly Vector3 ORIGIN = new(1f, 2f, 0.5f);

        private static LightingCalcResult Parse(SpecFile file, out string error, string zoneExtra = "") =>
            LightingCalcFiles.Parse(file.Json(zoneExtra), file.Bin(), "Level 1_d339c539.json", ORIGIN, DateTime.UtcNow, out error);

        #endregion

        [TestMethod]
        public void Read_HeaderZonesPointsAndLuminaires()
        {
            LightingCalcResult r = Parse(new SpecFile(), out string error);
            Assert.IsNotNull(r, error);
            Assert.AreEqual("d339c539", r.SetId);
            Assert.AreEqual("Level 1 Offices", r.SetName);
            Assert.AreEqual(new DateTime(2026, 10, 22, 14, 3, 0, DateTimeKind.Utc), r.RunUtc);
            Assert.AreEqual(DateTimeKind.Utc, r.RunUtc.Kind);
            Assert.IsTrue(r.Interreflections);
            Assert.AreEqual(50f, r.ScaleMax);
            Assert.AreEqual(2, r.Zones.Length);
            Assert.AreEqual(0, r.Warnings.Count);

            CalcZoneResult z = r.Zones[0];
            Assert.AreEqual("101 Café Ü", z.DisplayName);
            Assert.AreEqual("Meets", z.Target.Result);
            Assert.AreEqual(3f, z.Target.AverageToMinimum);
            Assert.AreEqual(1.5f, z.Stats.AverageToMinimum);
            Assert.AreEqual(4, z.Points.Length);
            Assert.AreEqual(40f, z.PointValues[1]);
            Assert.IsTrue(float.IsNaN(z.PointValues[2]), "a point inside an obstruction stays NaN");
            Assert.AreEqual(3, z.Patches.Length);
            Assert.IsTrue(z.Patches[2].Covered);
            Assert.AreEqual(1, z.Isolines.Length);
            Assert.AreEqual(2, z.Isolines[0].Segments.Length);

            Assert.IsTrue(float.IsNaN(r.Zones[1].Stats.AverageToMinimum), "left out = NaN");
            Assert.IsNull(r.Zones[1].Target);
            Assert.AreEqual(1, r.Luminaires.Length);
            Assert.AreEqual(0.85f, r.Luminaires[0].Multiplier);
        }

        [TestMethod]
        public void Coordinates_FeetToSceneMetresLessTheOrigin()
        {
            LightingCalcResult r = Parse(new SpecFile(), out string error);
            Assert.IsNotNull(r, error);
            CalcZoneResult z = r.Zones[0];
            AssertClose(new Vector3(5f * 0.3048f - 1f, 4f * 0.3048f - 2f, 2.5f * 0.3048f - 0.5f), z.Points[0]);
            AssertClose(new Vector3(20f * 0.3048f - 1f, 16f * 0.3048f - 2f, -0.5f), z.Patches[0].C);
            Assert.AreEqual(-0.5f, z.BottomZ, 1e-5f);
            Assert.AreEqual(9f * 0.3048f - 0.5f, z.CeilingZ, 1e-5f);
            Assert.AreEqual(20f * 0.3048f - 1f, z.Loops[0][1].X, 1e-5f);
            AssertClose(new Vector3(5f * 0.3048f - 1f, 4f * 0.3048f - 2f, 8.9f * 0.3048f - 0.5f), r.Luminaires[0].Position);
        }

        [TestMethod]
        public void Damage_IsReportedNeverThrown()
        {
            Assert.IsNull(LightingCalcFiles.Parse(Encoding.UTF8.GetBytes("{nope"), new byte[0], "x.json", ORIGIN, DateTime.UtcNow, out string error));
            Assert.AreEqual("the header isn't valid JSON", error);
            Assert.IsNull(LightingCalcFiles.Parse(Encoding.UTF8.GetBytes("{\"format\":\"other\"}"), new byte[0], "x.json", ORIGIN, DateTime.UtcNow, out error));
            Assert.AreEqual("not a lighting calc results file", error);
            Assert.IsNull(LightingCalcFiles.Parse(null, null, "x.json", ORIGIN, DateTime.UtcNow, out error));

            Assert.IsNull(Parse(new SpecFile { Version = "2" }, out error));
            StringAssert.Contains(error, "version 2");
            Assert.IsNull(Parse(new SpecFile { Units = "\"length\":\"m\"" }, out error));
            StringAssert.Contains(error, "units");
            Assert.IsNull(Parse(new SpecFile { BinBytesOverride = 12 }, out error), "a body that doesn't match its header is mid-write");
            StringAssert.Contains(error, "still being written");

            // A zone whose points run past the body is dropped with a reason; the others stay
            var bad = new SpecFile { PointCount = 4000 };
            LightingCalcResult r = Parse(bad, out error);
            Assert.IsNotNull(r, error);
            Assert.AreEqual(1, r.Zones.Length);
            Assert.AreEqual("102", r.Zones[0].Number);
            Assert.AreEqual(1, r.Warnings.Count);
            StringAssert.Contains(r.Warnings[0], "101");

            // An unaligned or negative offset too
            Assert.AreEqual(1, Parse(new SpecFile { PatchOffset = 3 }, out _).Zones.Length);
            Assert.AreEqual(1, Parse(new SpecFile { PatchOffset = -60 }, out _).Zones.Length);
        }

        [TestMethod]
        public void Colours_MatchWwToolsRamp()
        {
            AssertClose(new Vector3(0f, 0f, 0.9f), LightingCalcs.FalseColour(0f, 0f, 50f));   // blue
            AssertClose(new Vector3(0f, 0.9f, 0f), LightingCalcs.FalseColour(25f, 0f, 50f));  // green
            AssertClose(new Vector3(0.9f, 0f, 0f), LightingCalcs.FalseColour(50f, 0f, 50f));  // red
            AssertClose(new Vector3(0.9f, 0f, 0f), LightingCalcs.FalseColour(80f, 0f, 50f));  // clamped
            AssertClose(new Vector3(0f, 0f, 0.9f), LightingCalcs.FalseColour(float.NaN, 0f, 50f));
            AssertClose(new Vector3(0.9f, 0.9f, 0f), LightingCalcs.FalseColour(37.5f, 0f, 50f)); // yellow

            Assert.AreEqual(0f, LightingCalcs.LuminanceGrey(0f, 10f).X);
            float at = LightingCalcs.LuminanceGrey(10f, 10f).X;
            Assert.AreEqual(1f - MathF.Exp(-1.8f), at, 1e-5f);
            Assert.IsTrue(LightingCalcs.LuminanceGrey(10f, 10f).Z < at, "slightly warm");
            Assert.AreEqual(107.6391f, LightingCalcs.Display(10f, lux: true), 1e-3f);
        }

        [TestMethod]
        public void DisplayLuminance_UsesFootlambertsForImperialAndCandelasForMetric()
        {
            // NIST SP 811 B.9: 1 footlambert = 3.426259 cd/m².
            Assert.AreEqual(1f, LightingCalcs.DisplayLuminance(3.426259f, metric: false), 1e-6f);
            Assert.AreEqual(10f, LightingCalcs.DisplayLuminance(34.26259f, metric: false), 1e-5f);
            Assert.AreEqual(34.26259f, LightingCalcs.DisplayLuminance(34.26259f, metric: true));
            Assert.AreEqual(0f, LightingCalcs.DisplayLuminance(0f, metric: false));
        }

        [TestMethod]
        public void DiffuseSurface_AtTenFootcandlesAndHalfReflectance_IsFiveFootlamberts()
        {
            CalcPatch patch = new() { Illuminance = 10f, Reflectance = 0.5f };
            Assert.AreEqual(5f, LightingCalcs.DisplayLuminance(patch.Luminance, metric: false), 1e-5f);
            Assert.AreEqual(10f, LightingCalcs.Display(patch.Illuminance, lux: false), "point illuminance remains a different quantity");
        }

        [TestMethod]
        public void Corners_AverageOnTheSamePlaneAndSkipCovered()
        {
            LightingCalcResult r = Parse(new SpecFile(), out string error);
            Assert.IsNotNull(r, error);
            CalcZoneResult z = r.Zones[0];
            Assert.AreEqual(9, z.CornerIlluminance.Length);

            // The two floor triangles have equal areas: their shared corners (A and C of the first) read the mean, 20
            Assert.AreEqual(20f, z.CornerIlluminance[0], 1e-3f);
            Assert.AreEqual(20f, z.CornerIlluminance[2], 1e-3f);
            Assert.AreEqual(10f, z.CornerIlluminance[1], 1e-3f, "corner B is the first triangle's own");
            Assert.AreEqual(30f, z.CornerIlluminance[5], 1e-3f, "the second's own corner");

            // The covered wall patch doesn't count, and has nothing on its plane: it keeps its own value
            Assert.AreEqual(99f, z.CornerIlluminance[6], 1e-3f);

            // Luminance: ρE/π with E in lux
            Assert.AreEqual(0.2f * 20f * LightingCalcs.LUX_PER_FC / MathF.PI, z.CornerLuminance[0], 1e-3f);
            Assert.IsTrue(r.LuminanceReference > 0f);
            Assert.AreEqual(4, r.PointCount);
            Assert.AreEqual(3, r.PatchCount);
        }

        [TestMethod]
        public void ZoneAt_FindsThePlanZoneBetweenFloorAndCeiling()
        {
            LightingCalcResult r = Parse(new SpecFile(), out string error);
            Assert.IsNotNull(r, error);
            Vector3 inside101 = LightingCalcs.ToScene(10f, 8f, 5f, ORIGIN);
            Vector3 inside102 = LightingCalcs.ToScene(25f, 8f, 5f, ORIGIN);
            Assert.AreEqual("101", LightingCalcs.ZoneAt(r, inside101)?.Number);
            Assert.AreEqual("102", LightingCalcs.ZoneAt(r, inside102)?.Number);
            Assert.IsNull(LightingCalcs.ZoneAt(r, LightingCalcs.ToScene(10f, 8f, 30f, ORIGIN)), "the floor above");
            Assert.IsNull(LightingCalcs.ZoneAt(r, LightingCalcs.ToScene(40f, 8f, 5f, ORIGIN)));
            Assert.IsNull(LightingCalcs.ZoneAt(null, inside101));
        }

        [TestMethod]
        public void ClipZ_CutsLongPatchesToTheRoom()
        {
            Span<Vector3> points = stackalloc Vector3[5];
            Span<float> values = stackalloc float[5];
            // A tall thin triangle from z −1 to z 5, clipped to 0…3
            int n = LightingCalcs.ClipZ(new Vector3(0, 0, -1), new Vector3(1, 0, -1), new Vector3(0, 0, 5), 0f, 0f, 6f, 0f, 3f, points, values);
            Assert.AreEqual(4, n);
            for (int i = 0; i < n; i++) { Assert.IsTrue(points[i].Z >= -1e-5f && points[i].Z <= 3f + 1e-5f); }
            Assert.AreEqual(0, LightingCalcs.ClipZ(new Vector3(0, 0, 4), new Vector3(1, 0, 4), new Vector3(0, 0, 5), 0f, 0f, 0f, 0f, 3f, points, values));
            Assert.AreEqual(3, LightingCalcs.ClipZ(new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(0, 0, 2), 0f, 0f, 0f, 0f, 3f, points, values));
        }

        [TestMethod]
        public void Folder_ListsNewestFirstAndSkipsHalfPairs()
        {
            using var temp = new TempFolder();
            string folder = Path.Combine(temp.Path, LightingCalcFiles.FOLDER);
            Directory.CreateDirectory(folder);
            var file = new SpecFile();
            File.WriteAllBytes(Path.Combine(folder, "A_1.bin"), file.Bin());
            File.WriteAllBytes(Path.Combine(folder, "A_1.json"), file.Json());
            File.WriteAllBytes(Path.Combine(folder, "B_2.bin"), file.Bin());
            File.WriteAllBytes(Path.Combine(folder, "B_2.json"), file.Json());
            File.WriteAllBytes(Path.Combine(folder, "C_3.json"), file.Json()); // no body
            File.WriteAllText(Path.Combine(folder, LightingCalcFiles.OPEN_REQUEST_FILE), "{}");
            File.SetLastWriteTimeUtc(Path.Combine(folder, "A_1.json"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            List<LightingCalcFileInfo> list = LightingCalcFiles.List(folder);
            CollectionAssert.AreEqual(new[] { "B_2", "A_1" }, list.Select(f => f.Name).ToArray());
            Assert.AreEqual(0, LightingCalcFiles.List(Path.Combine(temp.Path, "none")).Count);

            LightingCalcResult r = LightingCalcFiles.Read(list[0].Path, ORIGIN, out string error);
            Assert.IsNotNull(r, error);
            Assert.AreEqual("B_2.json", r.Source);
            Assert.IsNull(LightingCalcFiles.Read(Path.Combine(folder, "C_3.json"), ORIGIN, out error));
            Assert.AreEqual("its .bin is missing", error);
        }

        [TestMethod]
        public void Folder_IsTheModelFoldersLightingCalcs()
        {
            Assert.AreEqual("lighting-calcs", ModelFolders.LIGHTING_CALCS_FOLDER);
            string comments = Path.Combine("root", "Tower_76f16950", ModelFolders.COMMENTS_FILE);
            Assert.AreEqual(Path.Combine("root", "Tower_76f16950", "lighting-calcs"), LightingCalcFiles.FolderFor(comments));
            Assert.IsNull(LightingCalcFiles.FolderFor(null));
        }

        [TestMethod]
        public void Bimgo_EmbedsAndReadsTheResults()
        {
            using var temp = new TempFolder();
            string folder = Path.Combine(temp.Path, LightingCalcFiles.FOLDER);
            Directory.CreateDirectory(folder);
            var file = new SpecFile();
            File.WriteAllBytes(Path.Combine(folder, "Level 1_d339c539.bin"), file.Bin());
            File.WriteAllBytes(Path.Combine(folder, "Level 1_d339c539.json"), file.Json());

            Dictionary<string, byte[]> entries = LightingCalcFiles.ReadForEmbedding(folder, out string note);
            Assert.IsNull(note);
            Assert.AreEqual(2, entries.Count);
            Assert.IsNull(LightingCalcFiles.ReadForEmbedding(Path.Combine(temp.Path, "none"), out _));

            BimGoDocument document = TestData.BuildDocument();
            document.LightingCalcs = entries;
            document.LightingCalcs["lighting-calcs/../evil.json"] = new byte[] { 1 }; // never written
            string path = temp.File("model.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            Assert.IsFalse(TestData.EntryNames(path).Any(n => n.Contains("..")));

            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            CollectionAssert.AreEqual(new[] { "Level 1_d339c539" }, LightingCalcFiles.EmbeddedNames(read.LightingCalcs));
            LightingCalcResult r = LightingCalcFiles.ParseEmbedded(read.LightingCalcs, "Level 1_d339c539", ORIGIN, out error);
            Assert.IsNotNull(r, error);
            Assert.AreEqual(2, r.Zones.Length);

            // A file without results reads as none
            BimGoDocument plain = TestData.BuildDocument();
            string plainPath = temp.File("plain.bimgo");
            Assert.IsTrue(BimGoWriter.Write(plainPath, plain, TestData.Writer, FileKinds.SAVE, out error), error);
            Assert.IsNull(BimGoReader.Read(plainPath, new LaunchSettings(), out _).LightingCalcs);
        }

        [TestMethod]
        public void OpenRequest_IsTakenOnceAndOnlyWhenFresh()
        {
            using var temp = new TempFolder();
            string path = Path.Combine(temp.Path, LightingCalcFiles.OPEN_REQUEST_FILE);
            var now = new DateTime(2026, 10, 22, 14, 0, 0, DateTimeKind.Utc);
            string Request(DateTime utc) => "{\"calcSet\":\"d339c539\",\"zone\":\"uid-101\",\"mode\":\"luminance\",\"utc\":\"" +
                utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\"}";

            File.WriteAllText(path, Request(now.AddSeconds(-30)));
            LightingCalcFiles.OpenRequest request = LightingCalcFiles.TakeOpenRequest(temp.Path, TimeSpan.FromMinutes(2), now);
            Assert.IsNotNull(request);
            Assert.AreEqual("d339c539", request.CalcSet);
            Assert.AreEqual("uid-101", request.Zone);
            Assert.AreEqual("luminance", request.Mode);
            Assert.IsFalse(File.Exists(path), "taken once");
            Assert.IsNull(LightingCalcFiles.TakeOpenRequest(temp.Path, TimeSpan.FromMinutes(2), now));

            File.WriteAllText(path, Request(now.AddMinutes(-10)));
            Assert.IsNull(LightingCalcFiles.TakeOpenRequest(temp.Path, TimeSpan.FromMinutes(2), now), "stale");
            Assert.IsFalse(File.Exists(path), "a stale request is cleared too");

            File.WriteAllText(path, "{broken");
            Assert.IsNull(LightingCalcFiles.TakeOpenRequest(temp.Path, TimeSpan.FromMinutes(2), now));
        }

        private static void AssertClose(Vector3 expected, Vector3 actual)
        {
            Assert.IsTrue(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
        }
    }
}
