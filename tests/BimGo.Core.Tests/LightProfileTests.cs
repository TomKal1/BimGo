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
    /// Round X-D1: the IES parser, profile tables, built-ins, the per-light code, and their place in the .bimgo
    /// (lighting.json + ies/*.ies, light-profiles.json). The sample files are written here by hand (CC0).
    /// </summary>
    [TestClass]
    public sealed class LightProfileTests
    {
        #region Sample files

        /// <summary>A 30° spot, bilateral (C 0–180), 0–90° gamma, 1000 lm lamp.</summary>
        // LF whatever the checkout's line endings: the tests edit it by "\n"
        private static readonly string SPOT = @"IESNA:LM-63-2002
[TEST] BimGo test file (hand-written, CC0)
[MANUFAC] BimGo
[LUMINAIRE] Test spot, 30 deg, bilateral 0-180
TILT=NONE
1 1000 1 7 3 1 2 0.1 0.1 0
1 1 10
0 10 20 30 45 60 90
0 90 180
2000 1800 900 300 80 20 0
2000 1600 700 200 60 15 0
2000 1400 500 150 40 10 0
".ReplaceLineEndings("\n");

        /// <summary>An even 100 cd source over the whole sphere (one C plane): 400π ≈ 1257 lm.</summary>
        private const string EVEN = "IESNA91\n[TEST] even\nTILT=NONE\n1,-1,1,3,1,1,2,0,0,0\n1 1 5\n0 90 180\n0\n100 100 100\n";

        /// <summary>Quadrant symmetric (C 0–90): brighter along C 0 than C 90.</summary>
        private const string QUADRANT = "TILT=NONE\n1 500 2 2 2 1 2 0 0 0\n1 1 5\n0 90\n0 90\n100 50\n40 20\n";

        /// <summary>Bilateral about the 90–270 plane: C 90 strong, C 270 weak.</summary>
        private const string PLANE_90 = "TILT=NONE\n1 500 1 2 3 1 2 0 0 0\n1 1 5\n0 90\n90 180 270\n10 10\n50 50\n200 200\n";

        /// <summary>Full 0–270 (no 360): C 315 is half way between C 270 and C 0 (wrapping).</summary>
        private const string WRAP = "TILT=NONE\n1 500 1 2 4 1 2 0 0 0\n1 1 5\n0 90\n0 90 180 270\n100 100\n0 0\n0 0\n300 300\n";

        /// <summary>TILT=INCLUDE: the tilt block (geometry, 3 angles, 3 factors): Round Y-C1 keeps it.</summary>
        private const string TILTED = "IESNA:LM-63-1995\nTILT=INCLUDE\n1\n3\n0 45 90\n1 0.9 0.8\n1 1000 1 2 1 1 2 0 0 0\n1 1 10\n0 90\n0\n500 100\n";

        #endregion

        #region Parsing

        [TestMethod]
        public void Ies_ParsesHeaderAnglesAndKeywords()
        {
            IesData data = IesData.Parse(SPOT, out string error);
            Assert.IsNotNull(data, error);
            Assert.AreEqual(1, data.Lamps);
            Assert.AreEqual(1000f, data.LumensPerLamp);
            Assert.AreEqual(1, data.PhotometricType);
            CollectionAssert.AreEqual(new[] { 0f, 10f, 20f, 30f, 45f, 60f, 90f }, data.Vertical);
            CollectionAssert.AreEqual(new[] { 0f, 90f, 180f }, data.Horizontal);
            Assert.AreEqual("BimGo", data.Keywords["MANUFAC"]);
            Assert.AreEqual(2000f, data.Candela(0f, 0f), 1e-3f);
            Assert.AreEqual(1900f, data.Candela(5f, 0f), 1e-3f, "half way between 0° and 10°");
        }

        [TestMethod]
        public void Ies_ExpandsSymmetry()
        {
            IesData spot = IesData.Parse(SPOT, out _);
            Assert.AreEqual(spot.Candela(20f, 90f), spot.Candela(20f, 270f), 1e-3f, "bilateral 0–180: C 270 mirrors C 90");
            Assert.AreEqual(spot.Candela(20f, 45f), spot.Candela(20f, 315f), 1e-3f);
            Assert.AreEqual(0f, spot.Candela(120f, 0f), "nothing above the file's gamma range");

            IesData even = IesData.Parse(EVEN, out string error);
            Assert.IsNotNull(even, error);
            Assert.AreEqual(100f, even.Candela(130f, 77f), 1e-3f, "one C plane: the same all round");

            IesData quadrant = IesData.Parse(QUADRANT, out error);
            Assert.IsNotNull(quadrant, error);
            Assert.AreEqual(200f, quadrant.Candela(0f, 0f), 1e-3f, "multiplier 2 applied");
            Assert.AreEqual(quadrant.Candela(45f, 30f), quadrant.Candela(45f, 150f), 1e-3f);
            Assert.AreEqual(quadrant.Candela(45f, 30f), quadrant.Candela(45f, 330f), 1e-3f);

            IesData plane90 = IesData.Parse(PLANE_90, out error);
            Assert.IsNotNull(plane90, error);
            Assert.AreEqual(50f, plane90.Candela(30f, 0f), 1e-3f, "C 0 mirrors C 180 about the 90–270 plane");
            Assert.AreEqual(plane90.Candela(30f, 60f), plane90.Candela(30f, 120f), 1e-3f);
            Assert.AreEqual(plane90.Candela(30f, 300f), plane90.Candela(30f, 240f), 1e-3f);

            IesData wrap = IesData.Parse(WRAP, out error);
            Assert.IsNotNull(wrap, error);
            Assert.AreEqual(200f, wrap.Candela(30f, 315f), 1e-3f, "wraps from 270 back to 0");
        }

        [TestMethod]
        public void Ies_SkipsIncludedTilt()
        {
            IesData data = IesData.Parse(TILTED, out string error);
            Assert.IsNotNull(data, error);
            Assert.AreEqual(500f, data.Candela(0f, 0f), 1e-3f);
            Assert.AreEqual(300f, data.Candela(45f, 200f), 1e-3f);
        }

        [TestMethod]
        public void Ies_KeepsIncludedTilt()
        {
            // Round Y-C1: the tilt block is read: geometry 1, factors 1 / 0.9 / 0.8 at 0 / 45 / 90°
            IesData data = IesData.Parse(TILTED, out string error);
            Assert.IsNotNull(data, error);
            Assert.AreEqual(1, data.TiltGeometry);
            CollectionAssert.AreEqual(new[] { 0f, 45f, 90f }, data.TiltAngles);
            CollectionAssert.AreEqual(new[] { 1f, 0.9f, 0.8f }, data.TiltFactors);
            Assert.IsFalse(data.TiltFileIgnored);

            LightProfile profile = LightProfile.FromIes("ies:t", "tilted.ies", TILTED, out error);
            Assert.IsNotNull(profile, error);
            Assert.AreEqual(1f, profile.TiltFactor(0f), 1e-5f, "straight down: as measured");
            Assert.AreEqual(0.95f, profile.TiltFactor(22.5f), 1e-5f, "linear between angles");
            Assert.AreEqual(0.8f, profile.TiltFactor(120f), 1e-5f, "held past the last angle");
            Assert.AreEqual(1f, profile.TiltFactor(-5f), 1e-5f, "held before the first");

            // No tilt data: always 1; a separate tilt file is noted and read as none
            LightProfile plain = LightProfile.FromIes("ies:s", "spot.ies", SPOT, out error);
            Assert.IsNotNull(plain, error);
            Assert.IsNull(plain.TiltAngles);
            Assert.AreEqual(1f, plain.TiltFactor(60f));
            IesData external = IesData.Parse(EVEN.Replace("TILT=NONE", "TILT=lamp.tlt"), out error);
            Assert.IsNotNull(external, error);
            Assert.IsTrue(external.TiltFileIgnored);
            Assert.IsNull(external.TiltAngles);

            // Damaged tilt data is refused, angles out of order are dropped (no tilt)
            Assert.IsNull(IesData.Parse("TILT=INCLUDE\n1\n3\n0 45\n", out _));
            IesData unordered = IesData.Parse(TILTED.Replace("0 45 90", "0 90 45"), out error);
            Assert.IsNotNull(unordered, error);
            Assert.IsNull(unordered.TiltAngles);
        }

        [TestMethod]
        public void Ies_RefusesDamagedOrUnsupported()
        {
            Assert.IsNull(IesData.Parse("", out string error));
            Assert.IsNotNull(error);
            Assert.IsNull(IesData.Parse("IESNA:LM-63-2002\n[TEST] x\n1 2 3\n", out error), "no TILT line");
            Assert.IsNull(IesData.Parse(EVEN.Replace("1,-1,1,3,1,1,", "1,-1,1,3,1,2,"), out error), "type B");
            StringAssert.Contains(error, "type C");
            Assert.IsNull(IesData.Parse(SPOT[..^20], out error), "truncated");
            Assert.IsNull(IesData.Parse(SPOT.Replace("0 10 20 30 45 60 90", "0 20 10 30 45 60 90"), out error), "angles out of order");
            Assert.IsNull(IesData.Parse(SPOT.Replace("1 1 10\n", "1 1 x10\n"), out error), "not a number");
        }

        [TestMethod]
        public void Ies_TotalLumensIntegratesTheCandela()
        {
            IesData even = IesData.Parse(EVEN, out _);
            Assert.AreEqual(400f * MathF.PI, even.TotalLumens(), 400f * MathF.PI * 0.01f);
            IesData spot = IesData.Parse(SPOT, out _);
            Assert.AreEqual(884f, spot.TotalLumens(), 20f, "the luminaire's output, not the lamp's 1000 lm");
        }

        #endregion

        #region Tables and built-ins

        [TestMethod]
        public void Profile_EvenLightHasScaleOne()
        {
            LightProfile even = LightProfile.FromFunction("t:even", "Even", (g, c) => 3f);
            Assert.IsTrue(even.Table.All(b => b == 255));
            Assert.AreEqual(1f, even.Scale, 0.01f);
            Assert.AreEqual(1f, even.Sample(123f, 321f), 1e-6f);

            LightProfile fromIes = LightProfile.FromIes(LightProfiles.KeyOf(EVEN), "even.ies", EVEN, out string error);
            Assert.IsNotNull(fromIes, error);
            Assert.AreEqual(1f, fromIes.Scale, 0.01f);
            Assert.AreEqual(400f * MathF.PI, fromIes.Lumens, 15f);
            Assert.IsTrue(fromIes.IsFile);
        }

        [TestMethod]
        public void Profile_NarrowBeamsConcentrate()
        {
            LightProfile spot = LightProfile.FromIes(LightProfiles.KeyOf(SPOT), "spot.ies", SPOT, out string error);
            Assert.IsNotNull(spot, error);
            Assert.AreEqual(1f, spot.Sample(0f, 0f), 1e-6f, "peak 1 straight out");
            Assert.AreEqual(0f, spot.Sample(150f, 0f), 1e-6f);
            Assert.IsTrue(spot.Scale > 15f && spot.Scale < 40f, $"scale {spot.Scale}");
            Assert.IsTrue(spot.BeamAngle > 25f && spot.BeamAngle < 45f, $"beam {spot.BeamAngle}");

            // Energy: the table × scale integrates to 4π (an even light of the same output)
            Assert.AreEqual(4f * MathF.PI, LightProfile.Integrate(spot.Table) * spot.Scale, 0.01f);
        }

        [TestMethod]
        public void Profile_FineTableMatchesTheShapeAndKeepsEnergy()
        {
            LightProfile spot = LightProfile.FromIes(LightProfiles.KeyOf(SPOT), "spot.ies", SPOT, out string error);
            Assert.IsNotNull(spot, error);
            Assert.AreEqual(LightProfile.FINE_SIZE, spot.Fine.Length);
            Assert.AreEqual(ushort.MaxValue, spot.Fine.Max(), "peak 1 at 16 bits");
            Assert.AreEqual(1f, LightProfile.SampleFine(spot.Fine, 0f, 0f), 1e-4f, "peak straight out");
            Assert.AreEqual(0f, LightProfile.SampleFine(spot.Fine, 150f, 0f), 1e-4f);
            // The shader's table follows the coarse one across the beam (the coarse table's 8-bit steps and 5.8° rows aside)
            for (float g = 0f; g <= 90f; g += 5f)
            {
                Assert.AreEqual(spot.Sample(g, 0f), LightProfile.SampleFine(spot.Fine, g, 0f), 0.08f, $"gamma {g}");
            }
            // Energy: both scales (4π / each table's integral) agree on how concentrated the beam is
            Assert.AreEqual(spot.Scale, spot.FineScale, spot.Scale * 0.1f);
        }

        [TestMethod]
        public void BuiltIns_AreDistinctAndShaped()
        {
            LightProfile[] all = LightProfiles.BuiltIns;
            Assert.AreEqual(5, all.Length);
            Assert.AreEqual(all.Length, all.Select(p => p.Key).Distinct().Count());
            Assert.IsTrue(all.All(p => p.Key.StartsWith(LightProfiles.BUILT_IN_PREFIX, StringComparison.Ordinal) && !p.IsFile && p.Lumens == 0f));
            Assert.AreSame(all[0], LightProfiles.FindBuiltIn(LightProfiles.DOWNLIGHT_NARROW));
            Assert.IsNull(LightProfiles.FindBuiltIn("builtin:nope"));

            LightProfile narrow = LightProfiles.FindBuiltIn(LightProfiles.DOWNLIGHT_NARROW);
            LightProfile medium = LightProfiles.FindBuiltIn(LightProfiles.DOWNLIGHT_MEDIUM);
            LightProfile flood = LightProfiles.FindBuiltIn(LightProfiles.FLOOD);
            Assert.IsTrue(narrow.Scale > medium.Scale && medium.Scale > flood.Scale && flood.Scale > 1f);
            Assert.AreEqual(25f, narrow.BeamAngle, 4f);
            Assert.AreEqual(40f, medium.BeamAngle, 5f);

            LightProfile washer = LightProfiles.FindBuiltIn(LightProfiles.WALL_WASHER);
            Assert.IsTrue(washer.Sample(35f, 0f) > 3f * washer.Sample(35f, 180f), "throws towards C 0");

            LightProfile pendant = LightProfiles.FindBuiltIn(LightProfiles.PENDANT);
            Assert.IsTrue(pendant.Sample(180f, 0f) > 0.3f && pendant.Sample(0f, 0f) > pendant.Sample(180f, 0f), "some up, more down");
        }

        #endregion

        #region Keys and codes

        [TestMethod]
        public void Keys_HashTheContent()
        {
            string key = LightProfiles.KeyOf(SPOT);
            Assert.AreEqual(key, LightProfiles.KeyOf(SPOT.Replace("\n", "\r\n")), "line endings don't matter");
            Assert.AreNotEqual(key, LightProfiles.KeyOf(EVEN));
            string hash = LightProfiles.HashOf(key);
            Assert.IsTrue(LightProfiles.IsSafeHash(hash));
            Assert.AreEqual(LightProfiles.FILE_PREFIX + hash, key);
            Assert.IsNull(LightProfiles.HashOf("ies:../../x"));
            Assert.IsNull(LightProfiles.HashOf(LightProfiles.DOWNLIGHT_NARROW));
            Assert.IsFalse(LightProfiles.IsSafeHash("ABCDEF0123456789"));
        }

        [TestMethod]
        public void Code_RoundTripsSlotAndOrientation()
        {
            // Straight down along X: exact
            float down = LightProfiles.Encode(5, -Vector3.UnitZ, Vector3.UnitX);
            Assert.IsTrue(down < 0f && down == MathF.Round(down));
            (int slot, Vector3 aim, Vector3 across) = LightProfiles.Decode(down);
            Assert.AreEqual(5, slot);
            Assert.IsTrue(Vector3.Distance(aim, -Vector3.UnitZ) < 1e-5f);
            Assert.IsTrue(Vector3.Distance(across, Vector3.UnitX) < 1e-5f);

            var random = new Random(7);
            for (int i = 0; i < 200; i++)
            {
                Vector3 a = Vector3.Normalize(new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1));
                Vector3 c = Vector3.Normalize(Vector3.Cross(a, new Vector3(0.3f, -0.7f, 0.2f)));
                int s = random.Next(LightProfiles.MAX_GPU_PROFILES);
                (int gotSlot, Vector3 gotAim, Vector3 gotAcross) = LightProfiles.Decode(LightProfiles.Encode(s, a, c));
                Assert.AreEqual(s, gotSlot);
                Assert.IsTrue(Vector3.Dot(gotAim, a) > MathF.Cos(4.5f * MathF.PI / 180f), $"aim {a} → {gotAim}"); // 6-bit octahedral: under 4° worst case
                Assert.IsTrue(Vector3.Dot(gotAcross, c) > MathF.Cos(6f * MathF.PI / 180f), $"across {c} → {gotAcross}");
                Assert.AreEqual(0f, Vector3.Dot(gotAcross, gotAim), 1e-4f);
            }

            // The angles the shader works out
            (float gamma, float cAngle) = LightProfiles.AnglesOf(new Vector3(0f, 1f, -1f), -Vector3.UnitZ, Vector3.UnitX);
            Assert.AreEqual(45f, gamma, 1e-3f);
            Assert.AreEqual(90f, cAngle, 1e-3f, "C 90 is a quarter turn anticlockwise from above");
        }

        #endregion

        #region Generated profiles (Round Y-C1)

        [TestMethod]
        public void Generated_SpotKeysRoundAndClamp()
        {
            Assert.AreEqual("gen:spot:30:45", LightProfiles.SpotKey(30.2, 44.8));
            Assert.AreEqual("gen:spot:40:40", LightProfiles.SpotKey(60, 40), "the beam never exceeds the field");
            Assert.AreEqual("gen:spot:1:179", LightProfiles.SpotKey(0, 200), "clamped");
            Assert.IsNull(LightProfiles.SpotKey(double.NaN, 40));
            Assert.IsNull(LightProfiles.SpotKey(20, 0));
            Assert.IsTrue(LightProfiles.IsGeneratedKey("gen:spot:30:45"));
            Assert.IsTrue(LightProfiles.IsGeneratedKey(LightProfiles.HEMISPHERICAL));
            Assert.IsFalse(LightProfiles.IsGeneratedKey("gen:spot:50:40"), "beam wider than the field");
            Assert.IsFalse(LightProfiles.IsGeneratedKey("gen:spot:x:40"));
            Assert.IsFalse(LightProfiles.IsGeneratedKey("gen:sphere"));
            Assert.IsFalse(LightProfiles.IsGeneratedKey(null));
            Assert.IsNull(LightProfiles.FromGeneratedKey("builtin:flood"));
        }

        [TestMethod]
        public void Generated_SpotHasFullBeamAndNothingPastTheField()
        {
            LightProfile spot = LightProfiles.FromGeneratedKey("gen:spot:30:60");
            Assert.IsNotNull(spot);
            Assert.IsTrue(spot.IsGenerated);
            Assert.IsFalse(spot.IsFile);
            Assert.AreEqual(1f, spot.Sample(0f, 0f), 1e-3f);
            Assert.AreEqual(1f, spot.Sample(5.8f, 90f), 0.02f, "inside half the beam");
            Assert.AreEqual(0f, spot.Sample(40f, 0f), 1e-3f, "past half the field");
            Assert.AreEqual(0f, spot.Sample(170f, 0f), 1e-3f, "nothing backwards");
            Assert.AreEqual(spot.Sample(20f, 0f), spot.Sample(20f, 200f), 1e-3f, "round");
            Assert.IsTrue(spot.Sample(20f, 0f) > 0.1f && spot.Sample(20f, 0f) < 0.95f, "falling between beam and field");
            Assert.IsTrue(spot.Scale > 4f, "a cone concentrates its output");
            Assert.AreEqual(4f * MathF.PI, LightProfile.Integrate(spot.Table) * spot.Scale, 1e-2f, "energy kept");
            StringAssert.Contains(spot.Name, "30° beam, 60° field");

            LightProfile narrow = LightProfiles.FromGeneratedKey("gen:spot:20:20");
            Assert.IsNotNull(narrow);
            Assert.IsTrue(narrow.Scale > spot.Scale, "a narrower cone concentrates more");
            Assert.AreEqual("Revit spot (20°)", narrow.Name);
        }

        [TestMethod]
        public void Generated_HemisphereIsCosineOutOfTheFixture()
        {
            LightProfile hemi = LightProfiles.FromGeneratedKey(LightProfiles.HEMISPHERICAL);
            Assert.IsNotNull(hemi);
            Assert.AreEqual(1f, hemi.Sample(0f, 0f), 1e-3f);
            Assert.AreEqual(0.5f, hemi.Sample(60f, 45f), 0.03f, "cos 60°");
            Assert.AreEqual(0f, hemi.Sample(120f, 0f), 1e-3f, "nothing behind");
            Assert.AreEqual(4f, hemi.Scale, 0.1f, "a cosine half sphere: 4π / π");
        }

        [TestMethod]
        public void Lighting_DistributionRoundTrips()
        {
            using var folder = new TempFolder();
            var lighting = new LightingData
            {
                Lights = new[]
                {
                    new LightSource { Element = 0, Position = Vector3.One, Distribution = "gen:spot:30:45", Aim = Vector3.Normalize(new Vector3(1, 0, -1)) },
                    new LightSource { Element = 1, Position = Vector3.One, Distribution = LightProfiles.HEMISPHERICAL },
                    new LightSource { Element = 1, Position = Vector3.One, Distribution = "gen:unknown" },
                    new LightSource { Element = 0, Position = Vector3.One } // the test scene has two elements (0, 1)
                }
            };
            string path = folder.File("spots.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(lighting), TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            LightSource[] got = read.Scene.Lighting.Lights;
            Assert.AreEqual("gen:spot:30:45", got[0].Distribution);
            Assert.IsTrue(Vector3.Distance(lighting.Lights[0].Aim, got[0].Aim) < 1e-5f, "a distribution keeps its aim");
            Assert.AreEqual(LightProfiles.HEMISPHERICAL, got[1].Distribution);
            Assert.IsNull(got[2].Distribution, "a key this version can't make is dropped (the lobe)");
            Assert.IsNull(got[3].Distribution);
        }

        #endregion

        #region Files

        [TestMethod]
        public void Lighting_IesFilesAndOrientationRoundTrip()
        {
            using var folder = new TempFolder();
            string hash = LightProfiles.HashOf(LightProfiles.KeyOf(SPOT));
            var lighting = new LightingData
            {
                Lights = new[]
                {
                    new LightSource { Element = 0, Position = new Vector3(1, 2, 2.6f), Ies = hash, Aim = Vector3.Normalize(new Vector3(0, 1, -1)), Across = Vector3.UnitX },
                    new LightSource { Element = 1, Position = new Vector3(4, 5, 2.4f) }
                },
                IesFiles = new[] { new LightIesFile(hash, "spot.ies", SPOT) }
            };
            BimGoDocument document = TestData.BuildDocument(lighting);
            document.LightProfiles = new LightProfileDocument();
            document.LightProfiles.SetChoice("Downlight : 600", LightProfiles.WALL_WASHER);
            document.LightProfiles.Imports.Add(new LightProfileImport { Hash = LightProfiles.HashOf(LightProfiles.KeyOf(EVEN)), Name = "even.ies", Text = EVEN });

            string path = folder.File("ies.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
            {
                Assert.IsNotNull(zip.GetEntry("ies/" + hash + ".ies"));
                Assert.IsNotNull(zip.GetEntry("light-profiles.json"));
            }
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);

            LightingData got = read.Scene.Lighting;
            Assert.AreEqual(1, got.IesFiles.Length);
            Assert.AreEqual("spot.ies", got.IesFiles[0].Name);
            Assert.AreEqual(SPOT, got.IesFiles[0].Text);
            Assert.AreEqual(hash, got.Lights[0].Ies);
            Assert.IsTrue(Vector3.Distance(lighting.Lights[0].Aim, got.Lights[0].Aim) < 1e-5f);
            Assert.IsNull(got.Lights[1].Ies);
            Assert.AreEqual(-Vector3.UnitZ, got.Lights[1].Aim, "defaults when not written");
            Assert.AreEqual(Vector3.UnitX, got.Lights[1].Across);

            Assert.IsNotNull(read.LightProfiles);
            Assert.AreEqual(LightProfiles.WALL_WASHER, read.LightProfiles.ChoiceFor("Downlight : 600"));
            Assert.AreEqual(1, read.LightProfiles.Imports.Count);
            Assert.AreEqual(EVEN, read.LightProfiles.Imports[0].Text);
        }

        [TestMethod]
        public void Lighting_MissingIesEntryFallsBackToTheLobe()
        {
            using var folder = new TempFolder();
            string hash = LightProfiles.HashOf(LightProfiles.KeyOf(SPOT));
            var lighting = new LightingData
            {
                Lights = new[] { new LightSource { Element = 0, Position = Vector3.One, Ies = hash } },
                IesFiles = new[] { new LightIesFile(hash, "spot.ies", SPOT) }
            };
            string path = folder.File("missing.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(lighting), TestData.Writer, FileKinds.SAVE, out string error), error);
            using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
            {
                zip.GetEntry("ies/" + hash + ".ies").Delete();
            }
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(0, read.Scene.Lighting.IesFiles.Length);
            Assert.IsNull(read.Scene.Lighting.Lights[0].Ies);
        }

        [TestMethod]
        public void ProfileDocument_CleanDropsDamage()
        {
            var document = new LightProfileDocument();
            document.Types.Add(new LightProfileChoice { FamilyType = "A : 1", Profile = LightProfiles.LOBE });
            document.Types.Add(new LightProfileChoice { FamilyType = "A : 1", Profile = LightProfiles.REVIT });
            document.Types.Add(new LightProfileChoice { FamilyType = "B : 1", Profile = "something else" });
            document.Types.Add(new LightProfileChoice { FamilyType = "", Profile = LightProfiles.LOBE });
            document.Types.Add(null);
            document.Imports.Add(new LightProfileImport { Hash = "../evil", Name = "x", Text = SPOT });
            document.Imports.Add(new LightProfileImport { Hash = LightProfiles.HashOf(LightProfiles.KeyOf(SPOT)), Name = "spot.ies", Text = SPOT });
            document.Imports.Add(new LightProfileImport { Hash = LightProfiles.HashOf(LightProfiles.KeyOf(SPOT)), Name = "again.ies", Text = SPOT });
            document.Clean();

            Assert.AreEqual(1, document.Types.Count);
            Assert.AreEqual(LightProfiles.REVIT, document.ChoiceFor("A : 1"), "the last choice per type wins");
            Assert.IsNull(document.ChoiceFor("B : 1"));
            Assert.AreEqual(1, document.Imports.Count);
            Assert.AreEqual("spot.ies", document.Imports[0].Name);

            document.SetChoice("A : 1", null);
            Assert.IsNull(document.ChoiceFor("A : 1"));
            Assert.IsFalse(document.IsEmpty, "the import is still there");
        }

        [TestMethod]
        public void ProfileDocument_LivesInTheModelFolder()
        {
            Assert.AreEqual(ModelFolders.LIGHT_PROFILES_FILE, ModelFolders.FileForSuffix(BimGoFormat.LIGHT_PROFILES_SIDECAR_SUFFIX));

            using var folder = new TempFolder();
            string path = folder.File("light-profiles.json");
            var document = new LightProfileDocument();
            document.SetChoice("Washer : 1200", LightProfiles.WALL_WASHER);
            Assert.IsTrue(LightProfileFiles.Write(path, document, out string error), error);
            LightProfileDocument read = LightProfileFiles.Read(path, out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(LightProfiles.WALL_WASHER, read.ChoiceFor("Washer : 1200"));
        }

        [TestMethod]
        public void ProfileDocument_InstanceChoicesRoundTripAndClean()
        {
            string spot = LightProfiles.KeyOf(SPOT);
            var document = new LightProfileDocument();
            document.SetChoice("Down : 1", LightProfiles.FLOOD);
            document.SetInstanceChoice("uid-a", "Down : 1", LightProfiles.WALL_WASHER);
            document.SetInstanceChoice("link-uid/uid-b", "Down : 1", LightProfiles.LOBE);
            document.SetInstanceChoice("uid-c", "Other : 2", spot);
            document.SetInstanceChoice("uid-a", "Down : 1", LightProfiles.DOWNLIGHT_NARROW); // replaces
            Assert.AreEqual(3, document.Instances.Count);
            Assert.AreEqual(LightProfiles.DOWNLIGHT_NARROW, document.InstanceChoiceFor("uid-a"));
            Assert.IsNull(document.InstanceChoiceFor("uid-zz"));

            // The file keeps them
            using var folder = new TempFolder();
            string path = folder.File("light-profiles.json");
            Assert.IsTrue(LightProfileFiles.Write(path, document, out string error), error);
            LightProfileDocument read = LightProfileFiles.Read(path, out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(LightProfiles.DOWNLIGHT_NARROW, read.InstanceChoiceFor("uid-a"));
            Assert.AreEqual(LightProfiles.LOBE, read.InstanceChoiceFor("link-uid/uid-b"));
            Assert.AreEqual(spot, read.InstanceChoiceFor("uid-c"));
            Assert.AreEqual("Other : 2", read.Instances.First(i => i.Key == "uid-c").FamilyType);

            // Clean: damage goes, the last per key wins, unknown (absent) fixtures stay
            read.Instances.Add(new LightProfileInstanceChoice { Key = "uid-c", FamilyType = "Other : 2", Profile = LightProfiles.REVIT });
            read.Instances.Add(new LightProfileInstanceChoice { Key = "", Profile = LightProfiles.LOBE });
            read.Instances.Add(new LightProfileInstanceChoice { Key = "uid-d", Profile = "nonsense" });
            read.Instances.Add(null);
            read.Clean();
            Assert.AreEqual(3, read.Instances.Count);
            Assert.AreEqual(LightProfiles.REVIT, read.InstanceChoiceFor("uid-c"));
            Assert.IsNull(read.InstanceChoiceFor("uid-d"));

            // RESET ALL for a type
            Assert.AreEqual(2, read.ClearInstances("Down : 1"));
            Assert.IsNull(read.InstanceChoiceFor("uid-a"));
            Assert.AreEqual(LightProfiles.REVIT, read.InstanceChoiceFor("uid-c"));

            // Only instance choices: still something to save
            var only = new LightProfileDocument();
            Assert.IsTrue(only.IsEmpty);
            only.SetInstanceChoice("uid-a", "Down : 1", LightProfiles.FLOOD);
            Assert.IsFalse(only.IsEmpty);
            only.SetInstanceChoice("uid-a", "Down : 1", null);
            Assert.IsTrue(only.IsEmpty);
        }

        [TestMethod]
        public void ProfileDocument_ResolvesFixtureThenTypeThenRevit()
        {
            const string revit = "ies:0123456789abcdef";
            var document = new LightProfileDocument();

            // Nothing chosen: Revit's (its file or distribution), else the lobe
            Assert.AreEqual(revit, LightProfileDocument.KeyFor(document.ResolveChoice("uid-a", "Down : 1"), revit));
            Assert.IsNull(LightProfileDocument.KeyFor(document.ResolveChoice("uid-a", "Down : 1"), null));

            // The type's choice
            document.SetChoice("Down : 1", LightProfiles.FLOOD);
            Assert.AreEqual(LightProfiles.FLOOD, LightProfileDocument.KeyFor(document.ResolveChoice("uid-a", "Down : 1"), revit));

            // The fixture's own wins; "revit" there means Revit's even when the type chose otherwise
            document.SetInstanceChoice("uid-a", "Down : 1", LightProfiles.REVIT);
            Assert.AreEqual(revit, LightProfileDocument.KeyFor(document.ResolveChoice("uid-a", "Down : 1"), revit));
            document.SetInstanceChoice("uid-a", "Down : 1", LightProfiles.LOBE);
            Assert.IsNull(LightProfileDocument.KeyFor(document.ResolveChoice("uid-a", "Down : 1"), revit));
            Assert.AreEqual(LightProfiles.FLOOD, LightProfileDocument.KeyFor(document.ResolveChoice("uid-b", "Down : 1"), revit), "another fixture keeps the type's");
            Assert.AreEqual(LightProfiles.FLOOD, LightProfileDocument.KeyFor(document.ResolveChoice(null, "Down : 1"), revit), "no identity: the type's");
        }

        [TestMethod]
        public void ProfileDocument_InstanceKeysFollowTheLink()
        {
            var scene = new SceneData
            {
                Elements = new[]
                {
                    new ElementRecord { ElementId = 11, UniqueId = "host-uid" },
                    new ElementRecord { ElementId = 12, UniqueId = "linked-uid", Link = 1 },
                    new ElementRecord { ElementId = 13, UniqueId = "other-uid", Link = 2 },
                    new ElementRecord { ElementId = 14, UniqueId = "" },
                    new ElementRecord { IsLibraryTemplate = true }
                },
                Links = new[] { new LinkInfo { Index = 1, InstanceUniqueId = "link-instance" }, new LinkInfo { Index = 2 } }
            };
            Assert.AreEqual("host-uid", LightProfileDocument.InstanceKeyOf(scene, 0));
            Assert.AreEqual("link-instance/linked-uid", LightProfileDocument.InstanceKeyOf(scene, 1));
            Assert.AreEqual("link:2/other-uid", LightProfileDocument.InstanceKeyOf(scene, 2));
            Assert.AreEqual("id:14", LightProfileDocument.InstanceKeyOf(scene, 3));
            Assert.IsNull(LightProfileDocument.InstanceKeyOf(scene, 4), "library templates have no identity");
            Assert.IsNull(LightProfileDocument.InstanceKeyOf(scene, 9));
        }

        [TestMethod]
        public void Polar_DownlightHangsBelowAndPeaksAtOne()
        {
            LightProfile spot = LightProfile.FromIes(LightProfiles.KeyOf(SPOT), "spot.ies", SPOT, out string error);
            Assert.IsNotNull(spot, error);
            LightProfilePolar plot = LightProfilePolar.Build(spot);
            Assert.AreEqual(LightProfilePolar.SEGMENTS + 1, plot.C0.Length);
            Assert.AreEqual(plot.C0[0], plot.C0[^1], "closed");

            // Gamma 0 (first point) straight down at the peak; nothing reaches above the centre (gamma 90+ is dark)
            Assert.AreEqual(0f, plot.C0[0].X, 1e-4f);
            Assert.AreEqual(1f, plot.C0[0].Y, 0.02f);
            float longest = plot.C0.Concat(plot.C90).Max(p => p.Length());
            Assert.AreEqual(1f, longest, 1e-3f, "normalised to the planes' peak");
            Assert.IsTrue(plot.C0.All(p => p.Y >= -1e-3f), "a downlight sends nothing up");

            // Right half is C 0 / C 90, left half C 180 / C 270: mirror points of a symmetric spot match
            int quarter = LightProfilePolar.SEGMENTS / 4;
            Assert.AreEqual(plot.C0[quarter].Length(), plot.C0[LightProfilePolar.SEGMENTS - quarter].Length(), 0.05f);
            Assert.IsTrue(plot.C0[quarter].X >= 0f && plot.C0[LightProfilePolar.SEGMENTS - quarter].X <= 0f);

            Assert.IsNull(LightProfilePolar.Build(null), "the lobe has no plot");

            // An even light is a circle
            LightProfile even = LightProfile.FromIes(LightProfiles.KeyOf(EVEN), "even.ies", EVEN, out error);
            LightProfilePolar circle = LightProfilePolar.Build(even);
            Assert.IsTrue(circle.C0.All(p => MathF.Abs(p.Length() - 1f) < 0.02f));
        }

        #endregion
    }
}
