using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Airflow step 1: air.json and airflow.json in .bimgo files, their validation, the airflow sidecar, the state's
    /// clamping, the global airflow settings and the terminal helpers.
    /// </summary>
    [TestClass]
    public sealed class AirflowFileTests
    {
        private static AirData SampleAir() => new()
        {
            ProjectUnits = MeasurementSystem.IP,
            Terminals = new[]
            {
                new AirTerminal
                {
                    Element = 0, Position = new Vector3(1, 2, 2.7f), Normal = -Vector3.UnitZ, Forward = Vector3.UnitY,
                    FaceSize = 0.6096f, NeckDiameter = 0.2032f, FlowLps = 118f, FlowEditable = true, FlowParameter = "RBS_DUCT_FLOW_PARAM",
                    BlankedSides = AirTerminal.SIDE_NEG_X, Hosting = AirTerminalHosting.Unhosted, Connected = true,
                    CatalogKey = DiffuserCatalog.GENERIC_KEY, Rating = FamilyRating.Ready, FaceOffset = 0f
                },
                new AirTerminal
                {
                    Element = 1, Position = new Vector3(5, 2, 3f), FaceSize = 0.6f, NeckDiameter = 0.2f, FlowLps = 90f,
                    Hosting = AirTerminalHosting.FaceHosted, CatalogKey = "acme-scd", Rating = FamilyRating.Guessed,
                    RatingReason = "Face offset guessed from the bounds", FaceOffset = 0.012f
                }
            },
            Catalog = new[] { DiffuserCatalog.BuildGenericFamily() }
        };

        private static BimGoDocument WriteAndRead(BimGoDocument document, TempFolder folder, out string path)
        {
            path = folder.File("air.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            return read;
        }

        [TestMethod]
        public void Air_RoundTrips()
        {
            using var folder = new TempFolder();
            AirData air = SampleAir();
            BimGoDocument read = WriteAndRead(TestData.BuildDocument(air: air), folder, out string path);
            CollectionAssert.Contains(TestData.EntryNames(path), "air.json");

            AirData got = read.Scene.Air;
            Assert.AreEqual(MeasurementSystem.IP, got.ProjectUnits);
            Assert.AreEqual(2, got.Terminals.Length);
            AirTerminal a = got.Terminals[0], expected = air.Terminals[0];
            Assert.AreEqual(expected.Element, a.Element);
            Assert.AreEqual(expected.Position, a.Position);
            Assert.AreEqual(expected.Normal, a.Normal);
            Assert.AreEqual(expected.Forward, a.Forward);
            Assert.AreEqual(expected.FaceSize, a.FaceSize);
            Assert.AreEqual(expected.NeckDiameter, a.NeckDiameter);
            Assert.AreEqual(expected.FlowLps, a.FlowLps);
            Assert.IsTrue(a.FlowEditable);
            Assert.AreEqual("RBS_DUCT_FLOW_PARAM", a.FlowParameter);
            Assert.AreEqual(AirTerminal.SIDE_NEG_X, a.BlankedSides);
            Assert.IsTrue(a.Connected);
            Assert.AreEqual(FamilyRating.Ready, a.Rating);

            AirTerminal b = got.Terminals[1];
            Assert.AreEqual(AirTerminalHosting.FaceHosted, b.Hosting);
            Assert.AreEqual("acme-scd", b.CatalogKey);
            Assert.AreEqual(FamilyRating.Guessed, b.Rating);
            Assert.AreEqual("Face offset guessed from the bounds", b.RatingReason);
            Assert.AreEqual(0.012f, b.FaceOffset);
            Assert.AreSame(b, got.ForElement(1));
            Assert.IsNull(got.ForElement(7));

            // The catalog travels with the file and evaluates the same
            Assert.AreEqual(1, got.Catalog.Length);
            CatalogFamily family = got.Catalog[0];
            Assert.AreEqual(DiffuserCatalog.GENERIC_KEY, family.Key);
            Assert.IsTrue(family.Illustrative);
            var original = new DiffuserCatalog(air.Catalog);
            var reread = new DiffuserCatalog(got.Catalog);
            float t1 = original.Evaluate(original.Generic, 0.2032f, 0.6096f, 118f).ThrowAt(0.254f);
            float t2 = reread.Evaluate(reread.Generic, 0.2032f, 0.6096f, 118f).ThrowAt(0.254f);
            Assert.AreEqual(t1, t2, t1 * 1e-4f);
        }

        [TestMethod]
        public void Air_ReaderDropsDamagedTerminals()
        {
            using var folder = new TempFolder();
            var air = new AirData
            {
                Terminals = new[]
                {
                    new AirTerminal { Element = 0, FaceSize = 0.6f, NeckDiameter = 0.2f, FlowLps = 100f, Normal = new Vector3(0, 0, -3), Forward = new Vector3(2, 0, 1) },
                    new AirTerminal { Element = 0, FaceSize = 0.6f, NeckDiameter = 0.2f, FlowLps = 50f },          // same element twice
                    new AirTerminal { Element = 99, FaceSize = 0.6f, NeckDiameter = 0.2f, FlowLps = 50f },         // no such element
                    new AirTerminal { Element = 1, FaceSize = 0.6f, NeckDiameter = 0.2f, FlowLps = -5f },          // negative flow
                    new AirTerminal { Element = 1, FaceSize = 3f, NeckDiameter = 0.2f, FlowLps = 50f },            // face too big
                    new AirTerminal { Element = 1, FaceSize = 0.6f, NeckDiameter = 0.2f, FlowLps = 50f, BlankedSides = 0xF3 }
                }
            };
            BimGoDocument read = WriteAndRead(TestData.BuildDocument(air: air), folder, out _);

            AirTerminal[] got = read.Scene.Air.Terminals;
            Assert.AreEqual(2, got.Length);
            Assert.AreEqual(100f, got[0].FlowLps);
            Assert.AreEqual(-Vector3.UnitZ, got[0].Normal);                          // normalised
            Assert.AreEqual(Vector3.UnitX, got[0].Forward);                          // flattened to plan, normalised
            Assert.AreEqual((byte)3, got[1].BlankedSides);                          // only the four side bits
            Assert.AreEqual(MeasurementSystem.SI, read.Scene.Air.ProjectUnits);
        }

        [TestMethod]
        public void Air_OlderFilesAndModelsWithoutTerminalsHaveNone()
        {
            using var folder = new TempFolder();
            BimGoDocument read = WriteAndRead(TestData.BuildDocument(), folder, out string path);
            List<string> entries = TestData.EntryNames(path);
            CollectionAssert.DoesNotContain(entries, "air.json");
            CollectionAssert.DoesNotContain(entries, "airflow.json");
            Assert.AreSame(AirData.Empty, read.Scene.Air);
            Assert.IsNull(read.Airflow);
        }

        [TestMethod]
        public void Airflow_RoundTrips_AndDefaultsAreNotWritten()
        {
            using var folder = new TempFolder();
            var state = new AirflowState
            {
                On = true, Mode = AirflowMode.Heating, UnitsOverride = MeasurementSystem.IP, HeatingShare = 0.4f,
                ZonePreset = OccupiedZonePreset.Custom, CustomZoneTop = 2.0f, PlacementMode = DiffuserPlacementMode.FreeAtElevation,
                XRay = CeilingXRay.Hidden, TargetElevation = 3.6f, GridSnap = true,
                TypeCatalogOverrides = new Dictionary<string, string> { ["type-uid"] = "acme-scd" }
            };
            BimGoDocument read = WriteAndRead(TestData.BuildDocument(airflow: state), folder, out string path);
            CollectionAssert.Contains(TestData.EntryNames(path), "airflow.json");

            AirflowState got = read.Airflow;
            Assert.IsNotNull(got);
            Assert.IsTrue(got.On);
            Assert.AreEqual(AirflowMode.Heating, got.Mode);
            Assert.AreEqual(MeasurementSystem.IP, got.UnitsOverride);
            Assert.AreEqual(0.4f, got.HeatingShare);
            Assert.AreEqual(0.4f, got.FlowShare);
            Assert.AreEqual(got.HeatingSupplyC, got.SupplyC);
            Assert.AreEqual(OccupiedZonePreset.Custom, got.ZonePreset);
            Assert.AreEqual(2.0f, got.ZoneTop);
            Assert.AreEqual(DiffuserPlacementMode.FreeAtElevation, got.PlacementMode);
            Assert.AreEqual(CeilingXRay.Hidden, got.XRay);
            Assert.AreEqual(3.6f, got.TargetElevation);
            Assert.IsTrue(got.GridSnap);
            Assert.AreEqual("acme-scd", got.TypeCatalogOverrides["type-uid"]);
            Assert.AreEqual(MeasurementSystem.IP, got.UnitsFor(MeasurementSystem.SI));

            // A default state isn't written
            using var other = new TempFolder();
            WriteAndRead(TestData.BuildDocument(airflow: new AirflowState()), other, out path);
            CollectionAssert.DoesNotContain(TestData.EntryNames(path), "airflow.json");
        }

        [TestMethod]
        public void Airflow_CleanClampsEverything()
        {
            var state = new AirflowState
            {
                Version = 9,
                Mode = (AirflowMode)42,
                UnitsOverride = (MeasurementSystem)7,
                HeatingShare = 5f,
                CoolingSupplyC = float.NaN,
                HeatingRoomC = -40f,
                ZonePreset = (OccupiedZonePreset)3,
                CustomZoneBottom = 0.9f,
                CustomZoneTop = 0.5f,
                CustomZoneSetback = float.PositiveInfinity,
                PlacementMode = (DiffuserPlacementMode)9,
                XRay = (CeilingXRay)9,
                TargetElevation = 100f,
                TypeCatalogOverrides = new Dictionary<string, string> { [" "] = "x", ["a"] = " ", ["b"] = " key " }
            }.Clean();

            Assert.AreEqual(1, state.Version);
            Assert.AreEqual(AirflowMode.Cooling, state.Mode);
            Assert.IsNull(state.UnitsOverride);
            Assert.AreEqual(1f, state.HeatingShare);
            Assert.AreEqual(12.8f, state.CoolingSupplyC);
            Assert.AreEqual(10f, state.HeatingRoomC);
            Assert.AreEqual(OccupiedZonePreset.Ashrae621, state.ZonePreset);
            Assert.AreEqual(0.9f, state.CustomZoneBottom);
            Assert.AreEqual(1.0f, state.CustomZoneTop, 1e-6f);                       // kept 0.1 m above the bottom
            Assert.AreEqual(0.6f, state.CustomZoneSetback);
            Assert.AreEqual(DiffuserPlacementMode.UnhostedAtCeiling, state.PlacementMode);
            Assert.AreEqual(CeilingXRay.SeeThrough, state.XRay);
            Assert.AreEqual(30f, state.TargetElevation);
            Assert.AreEqual(1, state.TypeCatalogOverrides.Count);
            Assert.AreEqual("key", state.TypeCatalogOverrides["b"]);

            // ASHRAE 62.1 breathing zone values whatever the custom ones say
            Assert.AreEqual(0.075f, state.ZoneBottom);
            Assert.AreEqual(1.8f, state.ZoneTop);
            Assert.AreEqual(0.6f, state.ZoneSetback);
            Assert.IsTrue(new AirflowState().IsDefault());
            Assert.IsFalse(new AirflowState { GridSnap = true }.IsDefault());
        }

        [TestMethod]
        public void Airflow_SidecarWritesAndReads()
        {
            using var folder = new TempFolder();
            string comments = folder.File("Tower.bimgo-comments.json");
            string path = AirflowFiles.SidecarFor(comments);
            Assert.AreEqual(folder.File("Tower.bimgo-airflow.json"), path);

            Assert.IsTrue(AirflowFiles.Write(path, new AirflowState { Mode = AirflowMode.Heating, HeatingShare = 7f }, out string error), error);
            AirflowState read = AirflowFiles.Read(path, out error);
            Assert.IsNull(error);
            Assert.AreEqual(AirflowMode.Heating, read.Mode);
            Assert.AreEqual(1f, read.HeatingShare);                                    // cleaned on read
            Assert.IsNull(AirflowFiles.Read(folder.File("missing.json"), out error));
            Assert.IsNull(error);
        }

        [TestMethod]
        public void Settings_AirflowValuesAreClamped()
        {
            var settings = new LaunchSettings
            {
                AirflowFreeJetFactor = 2f,
                AirflowBandFactor = float.NaN,
                AirflowPlenumAllowance = -1f,
                AirflowDefaultMount = 50f,
                AirflowOffCeilingTolerance = 0f
            };
            settings.Sanitise();
            Assert.AreEqual(1f, settings.AirflowFreeJetFactor);
            Assert.AreEqual(0.1f, settings.AirflowBandFactor);
            Assert.AreEqual(0f, settings.AirflowPlenumAllowance);
            Assert.AreEqual(10f, settings.AirflowDefaultMount);
            Assert.AreEqual(0.005f, settings.AirflowOffCeilingTolerance);

            LaunchSettings copy = new LaunchSettings { AirflowDefaultMount = 3.2f }.Clone();
            Assert.AreEqual(3.2f, copy.AirflowDefaultMount);
        }

        [TestMethod]
        public void Terminal_SidesAndModelling()
        {
            var open = new AirTerminal { NeckDiameter = 0.2f, FaceSize = 0.6f, Rating = FamilyRating.Guessed };
            Assert.AreEqual(4, open.OpenSides);
            Assert.AreEqual(1f, open.SideFlowFactor);
            Assert.IsTrue(open.Modelled);

            var corner = new AirTerminal { NeckDiameter = 0.2f, Rating = FamilyRating.Ready, BlankedSides = AirTerminal.SIDE_NEG_X | AirTerminal.SIDE_NEG_Y };
            Assert.AreEqual(2, corner.OpenSides);
            Assert.AreEqual(2f, corner.SideFlowFactor);
            Assert.IsTrue(corner.IsSideOpen(0));
            Assert.IsTrue(corner.IsSideOpen(1));
            Assert.IsFalse(corner.IsSideOpen(2));
            Assert.IsFalse(corner.IsSideOpen(3));

            Assert.IsFalse(new AirTerminal { NeckDiameter = 0.2f, Rating = FamilyRating.Ready, BlankedSides = AirTerminal.ALL_SIDES }.Modelled);
            Assert.AreEqual(0f, new AirTerminal { BlankedSides = AirTerminal.ALL_SIDES }.SideFlowFactor);
            Assert.IsFalse(new AirTerminal { NeckDiameter = 0.2f, Rating = FamilyRating.NotModelled }.Modelled);
            Assert.IsFalse(new AirTerminal { NeckDiameter = 0.2f, Rating = FamilyRating.Ready, Supply = false }.Modelled);
            Assert.IsFalse(new AirTerminal { Rating = FamilyRating.Ready }.Modelled);   // no neck
        }
    }
}
