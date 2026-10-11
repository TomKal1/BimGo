using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Airflow step 1: unit conversions and formatting, the catalog fit, the generated generic 3-cone family,
    /// interpolation / extrapolation / size matching, and catalog files (JSON and CSV).
    /// </summary>
    [TestClass]
    public sealed class AirflowCatalogTests
    {
        private const float FT = UnitConvert.FT_TO_M;
        private const float IN = UnitConvert.IN_TO_M;
        private static float Cfm(float cfm) => cfm * UnitConvert.CFM_TO_LPS;
        private static float Fpm(float fpm) => fpm * UnitConvert.FPM_TO_MPS;

        private static void Near(float expected, float actual, float relative, string message = null) =>
            Assert.IsTrue(MathF.Abs(actual - expected) <= MathF.Abs(expected) * relative,
                $"{message} expected {expected} ±{relative:P0}, got {actual}");

        #region Units

        [TestMethod]
        public void Units_ConvertBothWays()
        {
            Near(117.99f, UnitConvert.FlowToSi(250f, MeasurementSystem.IP), 0.001f);
            Near(250f, UnitConvert.FlowFromSi(UnitConvert.FlowToSi(250f, MeasurementSystem.IP), MeasurementSystem.IP), 1e-5f);
            Assert.AreEqual(118f, UnitConvert.FlowToSi(118f, MeasurementSystem.SI));

            Near(0.254f, UnitConvert.SpeedToSi(50f, MeasurementSystem.IP), 1e-5f);
            Near(150f, UnitConvert.SpeedFromSi(0.762f, MeasurementSystem.IP), 1e-4f);
            Near(249.089f, UnitConvert.PressureToSi(1f, MeasurementSystem.IP), 1e-5f);
            Near(0.06f, UnitConvert.PressureFromSi(UnitConvert.PressureToSi(0.06f, MeasurementSystem.IP), MeasurementSystem.IP), 1e-5f);
            Near(3.048f, UnitConvert.LengthToSi(10f, MeasurementSystem.IP), 1e-5f);
            Assert.AreEqual(3.5f, UnitConvert.LengthToSi(3.5f, MeasurementSystem.SI));

            // Sizes: inches or millimetres
            Near(0.2032f, UnitConvert.SizeToSi(8f, MeasurementSystem.IP), 1e-5f);
            Near(0.2f, UnitConvert.SizeToSi(200f, MeasurementSystem.SI), 1e-5f);
            Near(200f, UnitConvert.SizeFromSi(0.2f, MeasurementSystem.SI), 1e-5f);

            Near(12.778f, UnitConvert.TemperatureToSi(55f, MeasurementSystem.IP), 1e-4f);
            Near(69.98f, UnitConvert.TemperatureFromSi(21.1f, MeasurementSystem.IP), 1e-4f);
            Near(10f, UnitConvert.TemperatureDeltaToSi(18f, MeasurementSystem.IP), 1e-5f);
            Near(18f, UnitConvert.TemperatureDeltaFromSi(10f, MeasurementSystem.IP), 1e-5f);
        }

        [TestMethod]
        public void Units_EnvelopeSpeedsFollowTheSystem()
        {
            float[] ip = UnitConvert.EnvelopeSpeeds(MeasurementSystem.IP).ToArray();
            float[] si = UnitConvert.EnvelopeSpeeds(MeasurementSystem.SI).ToArray();
            Assert.AreEqual(3, ip.Length);
            Near(0.762f, ip[0], 1e-5f);
            Near(0.508f, ip[1], 1e-5f);
            Near(0.254f, ip[2], 1e-5f);
            CollectionAssert.AreEqual(new[] { 0.75f, 0.5f, 0.25f }, si);
        }

        [TestMethod]
        public void Units_ParseNamesAndRevitUnitIds()
        {
            Assert.AreEqual(MeasurementSystem.IP, UnitConvert.Parse("ip"));
            Assert.AreEqual(MeasurementSystem.IP, UnitConvert.Parse(" Imperial "));
            Assert.AreEqual(MeasurementSystem.SI, UnitConvert.Parse("SI"));
            Assert.AreEqual(MeasurementSystem.SI, UnitConvert.Parse("metric"));
            Assert.IsNull(UnitConvert.Parse("furlongs"));
            Assert.IsNull(UnitConvert.Parse(null));
            Assert.AreEqual("IP", UnitConvert.Name(MeasurementSystem.IP));

            Assert.AreEqual(MeasurementSystem.IP, UnitConvert.FromRevitUnitTypeId("autodesk.unit.unit:cubicFeetPerMinute-1.0.1"));
            Assert.AreEqual(MeasurementSystem.IP, UnitConvert.FromRevitUnitTypeId("autodesk.unit.unit:feetFractionalInches-1.0.0"));
            Assert.AreEqual(MeasurementSystem.IP, UnitConvert.FromRevitUnitTypeId("autodesk.unit.unit:usGallonsPerMinute-1.0.0"));
            Assert.AreEqual(MeasurementSystem.SI, UnitConvert.FromRevitUnitTypeId("autodesk.unit.unit:litersPerSecond-1.0.1"));
            Assert.AreEqual(MeasurementSystem.SI, UnitConvert.FromRevitUnitTypeId("autodesk.unit.unit:cubicMetersPerHour-1.0.1"));
            Assert.AreEqual(MeasurementSystem.SI, UnitConvert.FromRevitUnitTypeId("autodesk.unit.unit:millimeters-1.0.1"));
            Assert.AreEqual(MeasurementSystem.SI, UnitConvert.FromRevitUnitTypeId(null));
        }

        [TestMethod]
        public void Units_Format()
        {
            Assert.AreEqual("250 CFM", UnitConvert.FormatFlow(Cfm(250f), MeasurementSystem.IP));
            Assert.AreEqual("118 L/s", UnitConvert.FormatFlow(118f, MeasurementSystem.SI));
            Assert.AreEqual("50 fpm", UnitConvert.FormatSpeed(Fpm(50f), MeasurementSystem.IP));
            Assert.AreEqual("0.24 m/s", UnitConvert.FormatSpeed(0.24f, MeasurementSystem.SI));
            Assert.AreEqual("12.5 ft", UnitConvert.FormatLength(12.5f * FT, MeasurementSystem.IP));
            Assert.AreEqual("3.8 m", UnitConvert.FormatLength(3.8f, MeasurementSystem.SI));
            Assert.AreEqual("9' 0\"", UnitConvert.FormatElevation(9f * FT, MeasurementSystem.IP));
            Assert.AreEqual("8' 6\"", UnitConvert.FormatElevation(8.5f * FT, MeasurementSystem.IP));
            Assert.AreEqual("-1' 0\"", UnitConvert.FormatElevation(-1f * FT, MeasurementSystem.IP));
            Assert.AreEqual("2,700 mm", UnitConvert.FormatElevation(2.7f, MeasurementSystem.SI));
            Assert.AreEqual("8 in", UnitConvert.FormatSize(8f * IN, MeasurementSystem.IP));
            Assert.AreEqual("200 mm", UnitConvert.FormatSize(0.2f, MeasurementSystem.SI));
            Assert.AreEqual("0.060 in wg", UnitConvert.FormatPressure(UnitConvert.PressureToSi(0.06f, MeasurementSystem.IP), MeasurementSystem.IP));
            Assert.AreEqual("15 Pa", UnitConvert.FormatPressure(15f, MeasurementSystem.SI));
            Assert.AreEqual("55 °F", UnitConvert.FormatTemperature(12.8f, MeasurementSystem.IP));
            Assert.AreEqual("13 °C", UnitConvert.FormatTemperature(12.8f, MeasurementSystem.SI));
        }

        #endregion

        #region Fit and curve

        [TestMethod]
        public void Fit_RecoversAnExactPowerLaw()
        {
            float[] throws = { 1f, 2f, 4f };
            float[] speeds = throws.Select(x => 2f * MathF.Pow(x, -1.1f)).ToArray();
            (float a, float n, bool clamped) = DiffuserCatalog.FitRow(speeds, throws);
            Near(2f, a, 1e-4f);
            Near(1.1f, n, 1e-4f);
            Assert.IsFalse(clamped);
        }

        [TestMethod]
        public void Fit_OnePairUsesN1_AndSkipsBadPairs()
        {
            (float a, float n, bool clamped) = DiffuserCatalog.FitRow(new[] { 0.25f, 0f }, new[] { 4f, 3f });
            Assert.AreEqual(1f, n);
            Near(1f, a, 1e-5f);
            Assert.IsFalse(clamped);

            (a, n, _) = DiffuserCatalog.FitRow(Array.Empty<float>(), Array.Empty<float>());
            Assert.AreEqual(0f, a);
            Assert.AreEqual(1f, n);
        }

        [TestMethod]
        public void Fit_ClampsTheExponent()
        {
            // Speed drops tenfold over 20 % more distance: far steeper than a real jet
            (float a, float n, bool clamped) = DiffuserCatalog.FitRow(new[] { 1f, 0.1f }, new[] { 1f, 1.2f });
            Assert.IsTrue(clamped);
            Assert.AreEqual(DiffuserCatalog.MAX_N, n);
            Assert.IsTrue(a > 0f);
        }

        [TestMethod]
        public void Curve_CapsAtNeckVelocity_AndInverts()
        {
            var curve = new DecayCurve(2f, 1f, 3f);
            Assert.AreEqual(3f, curve.SpeedAt(0f));
            Assert.AreEqual(3f, curve.SpeedAt(0.5f));          // 4 m/s by the power law, capped
            Near(0.5f, curve.SpeedAt(4f), 1e-6f);
            Near(4f, curve.DistanceAt(0.5f), 1e-6f);
            Assert.AreEqual(0f, curve.DistanceAt(5f));         // faster than the neck: at the face
            Assert.AreEqual(float.PositiveInfinity, curve.DistanceAt(0f));
            Near(2.8f, curve.ScaledDistance(0.7f).DistanceAt(0.5f), 1e-5f);
            Assert.AreEqual(0f, default(DecayCurve).SpeedAt(1f));
        }

        #endregion

        #region Generic family

        [TestMethod]
        public void Generic_8In24In250Cfm_ThrowsAboutFive_Seven_Thirteen()
        {
            DiffuserCatalog catalog = DiffuserCatalog.BuiltIn();
            DiffuserPerformance p = catalog.Evaluate(catalog.Generic, 8f * IN, 24f * IN, Cfm(250f));

            Near(5f * FT, p.ThrowAt(Fpm(150f)), 0.1f, "T150");
            Near(7f * FT, p.ThrowAt(Fpm(100f)), 0.1f, "T100");
            Near(13f * FT, p.ThrowAt(Fpm(50f)), 0.1f, "T50");
            Assert.IsTrue(p.SizeMatched);
            Assert.IsFalse(p.Extrapolated);
            Assert.IsTrue(p.Illustrative);
            Assert.AreEqual(DiffuserCatalog.GENERIC_KEY, p.FamilyKey);
            Near(Fpm(716f), p.NeckVelocity, 0.01f, "neck velocity");
            Near(UnitConvert.PressureToSi(0.058f, MeasurementSystem.IP), p.TotalPressure, 0.05f, "Pt");
            Assert.IsTrue(p.Nc >= 18f && p.Nc <= 23f, $"NC {p.Nc}");
        }

        [TestMethod]
        public void Generic_SiAndIpSizesAgree()
        {
            DiffuserCatalog catalog = DiffuserCatalog.BuiltIn();
            DiffuserPerformance ip = catalog.Evaluate(catalog.Generic, 8f * IN, 24f * IN, 118f);
            DiffuserPerformance si = catalog.Evaluate(catalog.Generic, 0.2f, 0.6f, 118f);
            Assert.IsTrue(ip.SizeMatched && si.SizeMatched);
            Near(ip.ThrowAt(0.25f), si.ThrowAt(0.25f), 0.1f, "T at 0.25 m/s");
        }

        [TestMethod]
        public void Generic_HasBothSizeRanges()
        {
            CatalogFamily generic = DiffuserCatalog.BuildGenericFamily();
            float[] necks = generic.Rows.Select(r => MathF.Round(r.NeckDiameter * 1000f)).Distinct().OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new float[] { 150, 152, 200, 203, 250, 254, 300, 305, 350, 356, 400, 406 }, necks);
            Assert.IsTrue(generic.Rows.All(r => r.IsValid(out _)));
            // Small faces only for small necks
            Assert.IsTrue(generic.Rows.Where(r => r.FaceSize < 0.4f).All(r => r.NeckDiameter < 0.21f));
            // Flows rounded to 5 CFM (IP rows) or 5 L/s (SI rows)
            Assert.IsTrue(generic.Rows.All(r =>
                MathF.Abs(r.Flow / 5f - MathF.Round(r.Flow / 5f)) < 1e-3f
                || MathF.Abs(r.Flow / UnitConvert.CFM_TO_LPS / 5f - MathF.Round(r.Flow / UnitConvert.CFM_TO_LPS / 5f)) < 1e-3f));
        }

        [TestMethod]
        public void Evaluate_InterpolatesBetweenRows()
        {
            var family = new CatalogFamily
            {
                Key = "two-rows",
                Rows = new List<CatalogRow>
                {
                    Row(0.2f, 0.6f, 100f, a: 2f, n: 1f),
                    Row(0.2f, 0.6f, 200f, a: 6f, n: 1.2f)
                }
            };
            var catalog = new DiffuserCatalog(new[] { family });
            DiffuserPerformance p = catalog.Evaluate(catalog.Find("two-rows"), 0.2f, 0.6f, 150f);
            // A / Q: 0.02 and 0.03 → 0.025; N: 1.1
            Near(0.025f * 150f, p.Curve.A, 1e-3f);
            Near(1.1f, p.Curve.N, 1e-3f);
            Assert.IsFalse(p.Extrapolated);
        }

        [TestMethod]
        public void Evaluate_FlagsExtrapolationAndOutOfRange()
        {
            DiffuserCatalog catalog = DiffuserCatalog.BuiltIn();
            // 8 in neck rows: 140–280 CFM
            DiffuserPerformance inside = catalog.Evaluate(catalog.Generic, 8f * IN, 24f * IN, Cfm(200f));
            DiffuserPerformance plus20 = catalog.Evaluate(catalog.Generic, 8f * IN, 24f * IN, Cfm(280f * 1.2f));
            DiffuserPerformance plus40 = catalog.Evaluate(catalog.Generic, 8f * IN, 24f * IN, Cfm(280f * 1.4f));
            DiffuserPerformance minus40 = catalog.Evaluate(catalog.Generic, 8f * IN, 24f * IN, Cfm(140f * 0.6f));
            Assert.IsFalse(inside.Extrapolated || inside.OutOfRange);
            Assert.IsTrue(plus20.Extrapolated && !plus20.OutOfRange);
            Assert.IsTrue(plus40.Extrapolated && plus40.OutOfRange);
            Assert.IsTrue(minus40.Extrapolated && minus40.OutOfRange);
            // More flow still throws further
            Assert.IsTrue(plus40.ThrowAt(0.25f) > plus20.ThrowAt(0.25f));
        }

        [TestMethod]
        public void Evaluate_MatchesSizesWithinFivePercent_AndScalesOtherNecks()
        {
            DiffuserCatalog catalog = DiffuserCatalog.BuiltIn();
            CatalogFamily generic = catalog.Generic;

            // 210 mm: 3 % from 8 in (203 mm) → matched
            Assert.IsTrue(catalog.Evaluate(generic, 0.21f, 0.6f, 120f).SizeMatched);
            // Face unknown: only the neck is matched
            Assert.IsTrue(catalog.Evaluate(generic, 0.2f, 0f, 120f).SizeMatched);
            // 600 mm face against 12 in / 24 in / 300 mm / 600 mm faces → matched
            Assert.IsTrue(catalog.Evaluate(generic, 0.2f, 0.6f, 120f).SizeMatched);
            // 900 mm face: no catalog face within 5 %
            Assert.IsFalse(catalog.Evaluate(generic, 0.2f, 0.9f, 120f).SizeMatched);

            // 450 mm neck: nearest is 16 in (406 mm), 10 % off → not matched, throw scaled down by d ratio^(1/N)
            DiffuserPerformance big = catalog.Evaluate(generic, 0.45f, 0.6f, 900f);
            DiffuserPerformance sixteen = catalog.Evaluate(generic, 16f * IN, 24f * IN, 900f);
            Assert.IsFalse(big.SizeMatched);
            Near(MathF.Pow(16f * IN / 0.45f, 1f / sixteen.Curve.N), big.ThrowAt(0.25f) / sixteen.ThrowAt(0.25f), 0.01f);
        }

        [TestMethod]
        public void Evaluate_NoFlowOrNoNeck_GivesAStillJet()
        {
            DiffuserCatalog catalog = DiffuserCatalog.BuiltIn();
            Assert.AreEqual(0f, catalog.Evaluate(catalog.Generic, 0.2f, 0.6f, 0f).ThrowAt(0.25f));
            Assert.AreEqual(0f, catalog.Evaluate(catalog.Generic, 0f, 0.6f, 100f).ThrowAt(0.25f));
            Assert.AreEqual(0f, catalog.Evaluate(catalog.Generic, 0.2f, 0.6f, float.NaN).ThrowAt(0.25f));
        }

        #endregion

        #region Catalog

        [TestMethod]
        public void Catalog_FindsByKeyAndByName()
        {
            DiffuserCatalog catalog = DiffuserCatalog.BuiltIn();
            Assert.AreEqual(1, catalog.Families.Count);
            Assert.AreSame(catalog.Generic, catalog.Find("GENERIC-3CONE-SQUARE"));
            Assert.IsNull(catalog.Find("nope"));
            Assert.AreSame(catalog.Generic, catalog.FindByName("M_Supply Diffuser - 3-Cone", "600x600 - 200 Neck"));
            Assert.AreSame(catalog.Generic, catalog.FindByName("Square Cone Diffuser", "24x24"));
            Assert.IsNull(catalog.FindByName("Linear Slot Diffuser", "2 Slot"));
        }

        [TestMethod]
        public void Catalog_UserFamilyReplacesBuiltIn_AndBadRowsAreReported()
        {
            var problems = new List<string>();
            var mine = new CatalogFamily
            {
                Key = "Generic-3Cone-Square",
                Label = "Our generic",
                Match = new List<string> { "cone", " cone ", "" },
                Rows = new List<CatalogRow>
                {
                    Row(0.2f, 0.6f, 120f, a: 3f, n: 1f),
                    new() { NeckDiameter = 0.2f, FaceSize = 0.6f, Flow = -1f, ThrowSpeeds = new[] { 0.25f }, Throws = new[] { 3f } },
                    null
                }
            };
            var catalog = new DiffuserCatalog(new[] { mine }, problems);
            Assert.AreEqual(1, catalog.Families.Count);
            Assert.AreEqual("Our generic", catalog.Generic.Label);
            Assert.AreEqual(1, catalog.Generic.Rows.Count);
            Assert.AreEqual(1, catalog.Generic.Match.Count);
            Assert.AreEqual(2, problems.Count, string.Join(" | ", problems));
            Assert.IsTrue(problems[0].Contains("flow out of range"));

            // A family with nothing usable is dropped
            problems.Clear();
            catalog = new DiffuserCatalog(new[] { new CatalogFamily { Key = "empty" }, new CatalogFamily { Key = "  " } }, problems);
            Assert.IsNull(catalog.Find("empty"));
            Assert.AreEqual(1, catalog.Families.Count);
            Assert.AreEqual(2, problems.Count);
        }

        [TestMethod]
        public void Csv_ReadsFamilies_IpAndSiGiveTheSameCurve()
        {
            string[] ip =
            {
                "# ACME SCD, from the submittal",
                "family_key,label,units,neck,face,flow,pt,nc,v1,t1,v2,t2,v3,t3,match",
                "acme-scd,ACME SCD,IP,8,24,200,0.04,15,150,4,100,6,50,11,acme;scd",
                "acme-scd,ACME SCD,IP,8,24,300,0.09,25,150,6,100,9,50,16,",
                ""
            };
            var problems = new List<string>();
            List<CatalogFamily> families = CatalogFiles.ParseCsv(ip, "acme.csv", problems);
            Assert.AreEqual(0, problems.Count, string.Join(" | ", problems));
            Assert.AreEqual(1, families.Count);
            CatalogFamily family = families[0];
            Assert.AreEqual("ACME SCD", family.Label);
            Assert.AreEqual(MeasurementSystem.IP, family.SourceUnits);
            CollectionAssert.AreEqual(new[] { "acme", "scd" }, family.Match);
            Assert.AreEqual(2, family.Rows.Count);
            Near(8f * IN, family.Rows[0].NeckDiameter, 1e-5f);
            Near(Cfm(200f), family.Rows[0].Flow, 1e-5f);
            Near(UnitConvert.PressureToSi(0.04f, MeasurementSystem.IP), family.Rows[0].TotalPressure, 1e-5f);
            Near(Fpm(150f), family.Rows[0].ThrowSpeeds[0], 1e-5f);
            Near(4f * FT, family.Rows[0].Throws[0], 1e-5f);

            // The same table in SI
            static string Si(float neckIn, float faceIn, float cfm, float inwg, float nc, float[] fpm, float[] ft) =>
                string.Join(",", new[] { "acme-si", "ACME SCD", "SI",
                    F(neckIn * 25.4f), F(faceIn * 25.4f), F(cfm * UnitConvert.CFM_TO_LPS), F(inwg * UnitConvert.INWG_TO_PA), F(nc) }
                    .Concat(fpm.Zip(ft, (v, t) => F(v * UnitConvert.FPM_TO_MPS) + "," + F(t * UnitConvert.FT_TO_M))));
            static string F(float v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
            string[] si =
            {
                "family_key,label,units,neck,face,flow,pt,nc,v1,t1,v2,t2,v3,t3",
                Si(8, 24, 200, 0.04f, 15, new[] { 150f, 100f, 50f }, new[] { 4f, 6f, 11f }),
                Si(8, 24, 300, 0.09f, 25, new[] { 150f, 100f, 50f }, new[] { 6f, 9f, 16f })
            };
            List<CatalogFamily> siFamilies = CatalogFiles.ParseCsv(si, "acme-si.csv", problems);
            Assert.AreEqual(0, problems.Count, string.Join(" | ", problems));

            var catalog = new DiffuserCatalog(families.Concat(siFamilies));
            DiffuserPerformance a = catalog.Evaluate(catalog.Find("acme-scd"), 8f * IN, 24f * IN, Cfm(250f));
            DiffuserPerformance b = catalog.Evaluate(catalog.Find("acme-si"), 8f * IN, 24f * IN, Cfm(250f));
            Near(a.Curve.A, b.Curve.A, 0.01f, "A");
            Near(a.Curve.N, b.Curve.N, 0.01f, "N");
            Near(a.TotalPressure, b.TotalPressure, 0.01f, "Pt");
            Near(a.Nc, b.Nc, 0.01f, "NC");
            Assert.IsTrue(a.Nc > 15f && a.Nc < 25f);
        }

        [TestMethod]
        public void Csv_ReportsBadRowsAndHeaders()
        {
            var problems = new List<string>();
            string[] lines =
            {
                "family_key,label,units,neck,face,flow,pt,nc,v1,t1",
                "fam,Fam,IP,eight,24,200,,,50,11",          // neck not a number
                "fam,Fam,IP,8,24,200,,,50,11",              // good (pt / nc unknown)
                "fam,Fam,SI,200,600,100,,,0.25,3",          // units differ from the family's first good row
                ",Fam,IP,8,24,200,,,50,11",                 // no key
                "fam,Fam,IP,8,24,250,,,fast,12"             // pair not a number → row has no throws → dropped by Clean
            };
            List<CatalogFamily> families = CatalogFiles.ParseCsv(lines, "bad.csv", problems);
            Assert.AreEqual(1, families.Count);
            Assert.AreEqual(1, families[0].Rows.Count);
            Assert.AreEqual(0f, families[0].Rows[0].TotalPressure);
            Assert.AreEqual(5, problems.Count, string.Join(" | ", problems));
            Assert.IsTrue(problems.Any(p => p.Contains("line 2")));
            Assert.IsTrue(problems.Any(p => p.Contains("units differ")));

            problems.Clear();
            Assert.AreEqual(0, CatalogFiles.ParseCsv(new[] { "key,neck" , "x,8" }, "header.csv", problems).Count);
            Assert.IsTrue(problems.Single().Contains("family_key"));

            problems.Clear();
            Assert.AreEqual(0, CatalogFiles.ParseCsv(new[] { "# nothing" }, "empty.csv", problems).Count);
            Assert.IsTrue(problems.Single().Contains("empty"));
        }

        [TestMethod]
        public void Folder_LoadsJsonAndCsv_SkipsBadFiles()
        {
            using var folder = new TempFolder();
            var file = new CatalogFileDto
            {
                Families = new List<CatalogFamilyDto>
                {
                    new()
                    {
                        Key = "json-fam", Label = "JSON family", Units = "SI", FreeJetFactor = 0.6f,
                        Rows = new List<CatalogRowDto>
                        {
                            new() { Neck = 250, Face = 600, Flow = 150, Pt = 20, Nc = 22, Throw = new Dictionary<string, float> { ["0.75"] = 2f, ["0.5"] = 3.2f, ["0.25"] = 6f } }
                        }
                    },
                    new() { Key = "no-units", Rows = new List<CatalogRowDto>() }
                }
            };
            File.WriteAllText(folder.File("a.json"), JsonSerializer.Serialize(file));
            File.WriteAllText(folder.File("b.csv"), "family_key,units,neck,face,flow,v1,t1\ncsv-fam,IP,10,24,400,50,15\n");
            File.WriteAllText(folder.File("c.json"), "{ not json");
            File.WriteAllText(folder.File("readme.txt"), "ignored");

            DiffuserCatalog catalog = CatalogFiles.Load(folder.Path, out List<string> problems);
            Assert.AreEqual(3, catalog.Families.Count);
            CatalogFamily json = catalog.Find("json-fam");
            Assert.AreEqual(0.6f, json.FreeJetFactor);
            Assert.AreEqual("a.json", json.Source);
            Near(0.25f, json.Rows[0].NeckDiameter, 1e-5f);
            CollectionAssert.AreEqual(new[] { 0.75f, 0.5f, 0.25f }, json.Rows[0].ThrowSpeeds);
            Assert.IsNotNull(catalog.Find("csv-fam"));
            Assert.IsTrue(problems.Any(p => p.Contains("no-units")));
            Assert.IsTrue(problems.Any(p => p.StartsWith("c.json")));

            // A missing folder: built-in only
            catalog = CatalogFiles.Load(folder.File("missing"), out problems);
            Assert.AreEqual(1, catalog.Families.Count);
            Assert.AreEqual(0, problems.Count);
        }

        [TestMethod]
        public void Dto_RoundTripsThroughSi()
        {
            CatalogFamily generic = DiffuserCatalog.BuildGenericFamily();
            CatalogFamily back = CatalogFiles.FromDto(CatalogFiles.ToDto(generic), "air.json", null).Clean();
            Assert.AreEqual(generic.Rows.Count, back.Rows.Count);
            Assert.IsTrue(back.Illustrative);
            for (int i = 0; i < generic.Rows.Count; i++)
            {
                Near(generic.Rows[i].NeckDiameter, back.Rows[i].NeckDiameter, 1e-5f);
                Near(generic.Rows[i].Flow, back.Rows[i].Flow, 1e-5f);
                Near(generic.Rows[i].Throws[2], back.Rows[i].Throws[2], 1e-5f);
                Near(generic.Rows[i].ThrowSpeeds[0], back.Rows[i].ThrowSpeeds[0], 1e-4f);
            }
        }

        #endregion

        /// <summary>A row whose throws follow V = a · x^-n exactly (at 0.75 / 0.5 / 0.25 m/s).</summary>
        private static CatalogRow Row(float neck, float face, float flow, float a, float n)
        {
            float[] speeds = { 0.75f, 0.5f, 0.25f };
            return new CatalogRow
            {
                NeckDiameter = neck,
                FaceSize = face,
                Flow = flow,
                ThrowSpeeds = speeds,
                Throws = speeds.Select(v => MathF.Pow(a / v, 1f / n)).ToArray()
            };
        }
    }
}
