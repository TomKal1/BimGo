using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BimGo.Format;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round Y-A: saved measurements (validation, .bimgo round trip, model folder sharing, CSV) and comment pictures
    /// shared beside the model.
    /// </summary>
    [TestClass]
    public sealed class MeasurementTests
    {
        private static MeasurementRecord Distance(double length) => new()
        {
            Type = MeasurementRecord.DISTANCE,
            Points = new List<double> { 0, 0, 0, length, 0, 0 },
            Value = length,
            CreatedUtc = new DateTime(2026, 10, 20, 9, 30, 0, DateTimeKind.Utc)
        };

        [TestMethod]
        public void Clean_DropsDamagedEntries()
        {
            var document = new MeasurementDocument
            {
                Measurements = new List<MeasurementRecord>
                {
                    Distance(2),
                    null,
                    new() { Type = MeasurementRecord.DISTANCE, Points = new List<double> { 0, 0, 0 } }, // one point
                    new() { Type = MeasurementRecord.AREA, Points = new List<double> { 0, 0, 0, 1, 0, 0 } }, // two corners
                    new() { Type = "volume", Points = new List<double> { 0, 0, 0, 1, 0, 0 } },
                    new() { Type = MeasurementRecord.ANGLE, Points = new List<double> { 0, 0, 0, 1, 0, 0, 1, double.NaN, 0 } },
                    new() { Type = MeasurementRecord.CLEARANCE, Points = new List<double> { 0, 0, 2.7, 0, 0, 0, 0, 0, 0 }, Value2 = 2.7, Above = true }
                }
            }.Clean();
            Assert.AreEqual(2, document.Measurements.Count);
            Assert.AreEqual(MeasurementRecord.CLEARANCE, document.Measurements[1].Type);
            Assert.IsFalse(document.IsEmpty);
            Assert.IsTrue(new MeasurementDocument().IsEmpty);
            Assert.IsFalse(new MeasurementDocument { Hidden = true }.IsEmpty, "hidden is kept");
            Assert.AreEqual(0, new MeasurementDocument { Measurements = null }.Clean().Measurements.Count);
        }

        [TestMethod]
        public void Bimgo_KeepsMeasurementsAndLeavesThemOutWhenNone()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            document.Measurements = new MeasurementDocument { Hidden = true, Measurements = new List<MeasurementRecord> { Distance(3.25) } };
            string path = folder.File("measured.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            CollectionAssert.Contains(TestData.EntryNames(path), "measurements.json");
            BimGoDocument read = BimGoReader.Read(path, null, out error);
            Assert.IsNotNull(read, error);
            Assert.IsTrue(read.Measurements.Hidden);
            MeasurementRecord m = read.Measurements.Measurements.Single();
            Assert.AreEqual(3.25, m.Value);
            Assert.AreEqual(3.25, m.Points[3]);
            Assert.AreEqual(new DateTime(2026, 10, 20, 9, 30, 0, DateTimeKind.Utc), m.CreatedUtc.ToUniversalTime());

            document.Measurements = new MeasurementDocument();
            string empty = folder.File("none.bimgo");
            Assert.IsTrue(BimGoWriter.Write(empty, document, TestData.Writer, FileKinds.SAVE, out error), error);
            CollectionAssert.DoesNotContain(TestData.EntryNames(empty), "measurements.json");
            Assert.IsNull(BimGoReader.Read(empty, null, out error).Measurements);
        }

        [TestMethod]
        public void ModelFolder_FileIsSharedLikeTheOthers()
        {
            using var temp = new TempFolder();
            string modelDir = Path.Combine(temp.Path, "Shared");
            Directory.CreateDirectory(modelDir);
            string beside = Path.Combine(modelDir, "Tower" + BimGoFormat.SIDECAR_SUFFIX);
            string folder = Path.Combine(temp.Path, "Models", "Tower_abc");
            var info = new ModelFolderInfo { Title = "Tower", ShareBesideModel = true, BesideCommentsPath = beside };
            string comments = ModelFolders.Prepare(folder, info);

            string file = MeasurementFiles.SidecarFor(comments);
            Assert.AreEqual(Path.Combine(folder, ModelFolders.MEASUREMENTS_FILE), file);
            Assert.IsTrue(MeasurementFiles.Write(file, new MeasurementDocument { Measurements = new List<MeasurementRecord> { Distance(1) } }, out string error), error);
            string shared = Path.Combine(modelDir, "Tower" + BimGoFormat.MEASUREMENTS_SIDECAR_SUFFIX);
            Assert.IsTrue(File.Exists(shared), "mirrored beside the model");
            Assert.AreEqual(1, MeasurementFiles.Read(shared, out _).Measurements.Count);
        }

        [TestMethod]
        public void CommentPictures_AreSharedBesideTheModel()
        {
            using var temp = new TempFolder();
            string modelDir = Path.Combine(temp.Path, "Shared");
            Directory.CreateDirectory(modelDir);
            string beside = Path.Combine(modelDir, "Tower" + BimGoFormat.SIDECAR_SUFFIX);
            string folder = Path.Combine(temp.Path, "Models", "Tower_abc");
            var info = new ModelFolderInfo { Title = "Tower", ShareBesideModel = true, BesideCommentsPath = beside };
            string comments = ModelFolders.Prepare(folder, info);

            // My comment's picture goes beside the model with the comments
            var document = new CommentDocument
            {
                Comments = new List<CommentRecord>
                {
                    new() { Id = "mine", Text = "Mine", Snapshot = CommentSnapshots.NameFor("mine"), SnapshotData = new byte[] { 1, 2, 3 }, SnapshotDirty = true }
                }
            };
            Assert.IsTrue(CommentFiles.Write(comments, document, out string error), error);
            string pictures = ModelFolders.SharedPicturesFolderFor(beside);
            Assert.AreEqual(Path.Combine(modelDir, "Tower" + BimGoFormat.COMMENT_PICTURES_SIDECAR_SUFFIX), pictures);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(pictures, "mine.jpg")));

            // A colleague's picture comes back at the next Go
            File.WriteAllBytes(Path.Combine(pictures, "theirs.jpg"), new byte[] { 9 });
            ModelFolders.Prepare(folder, info);
            CollectionAssert.AreEqual(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(folder, "comments", "theirs.jpg")));

            // Without sharing nothing goes beside the model
            Assert.AreEqual(0, ModelFolders.SyncPictures(Path.Combine(temp.Path, "nowhere"), pictures));
            Assert.IsNull(ModelFolders.SharedPicturesFolderFor(null));
        }

        [TestMethod]
        public void Csv_HasARowPerMeasurementInMetres()
        {
            var document = new MeasurementDocument
            {
                Measurements = new List<MeasurementRecord>
                {
                    Distance(2.5),
                    new()
                    {
                        Type = MeasurementRecord.CLEARANCE, Points = new List<double> { 5, 0, 1, 0, 0, 1, 0, 0, 1 }, NormalX = 1,
                        Value = 0, Value2 = 5, Above = true
                    },
                    new()
                    {
                        Type = MeasurementRecord.CLEARANCE, Points = new List<double> { 0, 0, 2.7, 0, 0, 1, 0, 0, 0 },
                        Value = 1, Value2 = 1.7, Above = true, Below = true
                    }
                }
            };
            string[] lines = MeasurementFiles.ToCsv(document, "Tower, level 2").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(5, lines.Length);
            Assert.AreEqual("BimGo measurements,\"Tower, level 2\"", lines[0]);
            StringAssert.StartsWith(lines[2], "1,distance,2.5,m,,,2026-10-20 09:30:00,");
            StringAssert.EndsWith(lines[2], ",0.000 0.000 0.000; 2.500 0.000 0.000");
            StringAssert.StartsWith(lines[3], "2,clearance,5,m (across),,,");
            StringAssert.StartsWith(lines[4], "3,clearance,1,m (below),1.7,m (above),");
        }
    }
}
