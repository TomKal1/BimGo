using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BimGo.Format;
using BimGo.Live;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round Z-B: comments as Revit markers: the parameter values, what a sync places / updates / moves / removes,
    /// markers deleted in Revit staying gone, the message size limit, the shared parameter GUIDs, reading a file's
    /// comments only, and the settings.
    /// </summary>
    [TestClass]
    public sealed class CommentMarkerTests
    {
        private static MarkerRecord Want(string id, double x = 1, string status = CommentStatus.OPEN, string text = "Check this") =>
            CommentMarkers.FromComment(new CommentRecord { Id = id, Text = text, X = x, Y = 2, Z = 3, Status = status, Created = new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.Zero) });

        private static ExistingMarker Have(MarkerRecord values, long elementId = 1) => new() { ElementId = elementId, Values = values };

        [TestMethod]
        public void FromComment_FillsTheParameters()
        {
            var comment = new CommentRecord
            {
                Id = "abc", Author = "gavin", Text = "  First line of a long note\nsecond line", X = 1.5, Y = 2.5, Z = 3.5,
                Status = CommentStatus.IN_PROGRESS, Priority = CommentPriority.HIGH, AssignedTo = "Sam", Level = "Level 2",
                ElementUniqueId = "uid-1", Replies = new List<CommentReply> { new() { Text = "ok" }, new() { Text = "done" } },
                Created = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), Updated = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero)
            };
            MarkerRecord m = CommentMarkers.FromComment(comment, @"C:\x\comments\abc.jpg");
            Assert.AreEqual("abc", m.Id);
            Assert.AreEqual(1.5, m.X);
            Assert.AreEqual("First line of a long note", m.Title);
            Assert.AreEqual("First line of a long note\nsecond line", m.Comment);
            Assert.AreEqual("In progress", m.Status);
            Assert.AreEqual("High", m.Priority);
            Assert.AreEqual("Sam", m.AssignedTo);
            Assert.AreEqual("Level 2", m.Level);
            Assert.AreEqual("uid-1", m.Element);
            Assert.AreEqual(2, m.Replies);
            Assert.AreEqual(@"C:\x\comments\abc.jpg", m.Picture);
            Assert.IsTrue(m.IsInProgress && !m.IsOpen && !m.IsClosed && m.IsHighPriority);
            Assert.AreEqual(comment.Updated.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), m.Updated);

            // Titles are cut at 60 characters; unknown status / priority read as open / normal
            MarkerRecord odd = CommentMarkers.FromComment(new CommentRecord { Id = "x", Text = new string('a', 80), Status = "??", Priority = "??" });
            Assert.AreEqual(CommentMarkers.MAX_TITLE, odd.Title.Length);
            Assert.IsTrue(odd.Title.EndsWith("…"));
            Assert.AreEqual("Open", odd.Status);
            Assert.AreEqual("Normal", odd.Priority);
            Assert.IsTrue(odd.IsOpen && !odd.IsHighPriority);
        }

        [TestMethod]
        public void Payload_LeavesOutBlankAndIdLessComments()
        {
            var comments = new[]
            {
                new CommentRecord { Id = "a", Text = "one" },
                new CommentRecord { Id = "b", Text = "  " },
                new CommentRecord { Id = "", Text = "no id" },
                new CommentRecord { Id = "c", Text = "bad point", X = double.NaN },
                null
            };
            MarkersSyncPayload payload = CommentMarkers.Payload(comments, c => c.Id == "a" ? "pic" : null);
            Assert.AreEqual(1, payload.Markers.Count);
            Assert.AreEqual("pic", payload.Markers[0].Picture);
            Assert.IsTrue(payload.Full);
        }

        [TestMethod]
        public void Plan_PlacesUpdatesMovesAndRemoves()
        {
            MarkerRecord same = Want("same");
            MarkerRecord moved = Want("moved", x: 1);
            MarkerRecord edited = Want("edited", text: "new text");
            var existing = new List<ExistingMarker>
            {
                Have(Want("same"), 1),
                Have(Want("moved", x: 1.5), 2),
                Have(Want("edited", text: "old text"), 3),
                Have(Want("gone"), 4),
                Have(new MarkerRecord { Id = "" }, 5), // placed by hand: left alone
                Have(Want("same"), 6) // a copy: left alone
            };
            MarkerPlan plan = CommentMarkers.Plan(new[] { same, moved, edited, Want("new") }, existing, new[] { "same", "moved", "edited", "gone" },
                full: true, replaceAll: false, removeClosed: false);

            CollectionAssert.AreEqual(new[] { "new" }, plan.Place.Select(p => p.Id).ToArray());
            Assert.AreEqual(2, plan.Change.Count);
            var move = plan.Change.Single(c => c.Wanted.Id == "moved");
            Assert.IsTrue(move.Move && !move.Values);
            var edit = plan.Change.Single(c => c.Wanted.Id == "edited");
            Assert.IsTrue(!edit.Move && edit.Values);
            CollectionAssert.AreEqual(new long[] { 4 }, plan.Remove.Select(r => r.ElementId).ToArray());
            CollectionAssert.AreEquivalent(new[] { "same", "moved", "edited", "new" }, plan.PlacedAfter.ToArray());
            Assert.IsTrue(plan.HasWork);

            // Nothing differs: no work (no transaction, no undo entry)
            MarkerPlan quiet = CommentMarkers.Plan(new[] { Want("same") }, new[] { Have(Want("same")) }, new[] { "same" }, true, false, false);
            Assert.IsFalse(quiet.HasWork);
        }

        [TestMethod]
        public void Plan_DeletedInRevitStaysGoneUntilReplaceAll()
        {
            // "a" was placed before and isn't in the model any more: deleted in Revit
            MarkerPlan plan = CommentMarkers.Plan(new[] { Want("a"), Want("b") }, Array.Empty<ExistingMarker>(), new[] { "a" }, true, false, false);
            CollectionAssert.AreEqual(new[] { "b" }, plan.Place.Select(p => p.Id).ToArray());
            Assert.AreEqual(1, plan.DeletedInRevit);
            Assert.IsTrue(plan.PlacedAfter.Contains("a"), "the deletion is remembered");

            MarkerPlan again = CommentMarkers.Plan(new[] { Want("a"), Want("b") }, Array.Empty<ExistingMarker>(), new[] { "a" }, true, replaceAll: true, removeClosed: false);
            Assert.AreEqual(2, again.Place.Count);
            Assert.AreEqual(0, again.DeletedInRevit);

            // A comment deleted in BimGo whose marker was deleted in Revit too: forgotten
            MarkerPlan forgotten = CommentMarkers.Plan(Array.Empty<MarkerRecord>(), Array.Empty<ExistingMarker>(), new[] { "a" }, true, false, false);
            Assert.AreEqual(0, forgotten.PlacedAfter.Count);
        }

        [TestMethod]
        public void Plan_RemoveClosedAndAddOnly()
        {
            MarkerRecord closed = Want("c", status: CommentStatus.CLOSED);
            MarkerPlan plan = CommentMarkers.Plan(new[] { closed, Want("n", status: CommentStatus.CLOSED) }, new[] { Have(Want("c"), 9) }, new[] { "c" },
                full: true, replaceAll: false, removeClosed: true);
            Assert.AreEqual(0, plan.Place.Count);
            CollectionAssert.AreEqual(new long[] { 9 }, plan.Remove.Select(r => r.ElementId).ToArray());
            Assert.IsFalse(plan.PlacedAfter.Contains("c"), "removed by BimGo: placed again if it reopens");

            // Closed comments keep their (green) marker by default
            MarkerPlan keep = CommentMarkers.Plan(new[] { closed }, new[] { Have(Want("c"), 9) }, new[] { "c" }, true, false, false);
            Assert.AreEqual(0, keep.Remove.Count);
            Assert.AreEqual(1, keep.Change.Count);
            Assert.IsTrue(keep.Change[0].Values);

            // Add / update only (a .bimgo from someone else): other markers stay, and stay remembered
            MarkerPlan merge = CommentMarkers.Plan(new[] { Want("x") }, new[] { Have(Want("other"), 3) }, new[] { "other", "deleted" }, full: false, false, false);
            Assert.AreEqual(0, merge.Remove.Count);
            CollectionAssert.AreEquivalent(new[] { "x", "other", "deleted" }, merge.PlacedAfter.ToArray());
        }

        [TestMethod]
        public void Placed_TextRoundTrips()
        {
            string text = CommentMarkers.FormatPlaced(new[] { "b", "a", " ", null });
            Assert.AreEqual("a\nb", text);
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, CommentMarkers.ParsePlaced(text + "\r\n\n").ToArray());
            Assert.AreEqual(0, CommentMarkers.ParsePlaced(null).Count);
        }

        [TestMethod]
        public void Summary_SaysWhatChanged()
        {
            Assert.AreEqual("Revit markers are up to date", CommentMarkers.Summary(new MarkersResultPayload()));
            Assert.AreEqual("Revit markers: 2 placed, 1 removed (3 deleted in Revit left out)",
                CommentMarkers.Summary(new MarkersResultPayload { Placed = 2, Removed = 1, DeletedInRevit = 3 }));
            Assert.IsTrue(new MarkersResultPayload { Moved = 1 }.Changed);
            Assert.IsFalse(new MarkersResultPayload { DeletedInRevit = 1, Skipped = 1 }.Changed);
        }

        [TestMethod]
        public void FitTo_CutsLongTextsUntilItFits()
        {
            var payload = new MarkersSyncPayload();
            for (int i = 0; i < 50; i++) { payload.Markers.Add(Want("m" + i, text: "Title\n" + new string('x', 3000))); }
            Assert.IsTrue(CommentMarkers.FitTo(payload, 40_000));
            Assert.IsTrue(payload.Markers.All(m => m.Comment.Length <= 200));
            Assert.IsTrue(CommentMarkers.FitTo(new MarkersSyncPayload(), 100));
        }

        [TestMethod]
        public void Guids_MatchTheSharedParameterFile()
        {
            string file = FindRepoFile(Path.Combine("tools", "revit", "BimGo_SharedParameters.txt"));
            if (file == null) { Assert.Inconclusive("tools/revit/BimGo_SharedParameters.txt not found above the test folder."); }
            Dictionary<string, Guid> byName = File.ReadAllLines(file)
                .Where(l => l.StartsWith("PARAM\t", StringComparison.Ordinal))
                .Select(l => l.Split('\t'))
                .ToDictionary(p => p[2], p => Guid.Parse(p[1]));
            Assert.AreEqual(18, byName.Count);
            Assert.AreEqual(byName["BimGo_Id"], MarkerFamily.ID);
            Assert.AreEqual(byName["BimGo_Title"], MarkerFamily.TITLE);
            Assert.AreEqual(byName["BimGo_Comment"], MarkerFamily.COMMENT);
            Assert.AreEqual(byName["BimGo_Status"], MarkerFamily.STATUS);
            Assert.AreEqual(byName["BimGo_Priority"], MarkerFamily.PRIORITY);
            Assert.AreEqual(byName["BimGo_Author"], MarkerFamily.AUTHOR);
            Assert.AreEqual(byName["BimGo_AssignedTo"], MarkerFamily.ASSIGNED_TO);
            Assert.AreEqual(byName["BimGo_Created"], MarkerFamily.CREATED);
            Assert.AreEqual(byName["BimGo_Updated"], MarkerFamily.UPDATED);
            Assert.AreEqual(byName["BimGo_Level"], MarkerFamily.LEVEL);
            Assert.AreEqual(byName["BimGo_Element"], MarkerFamily.ELEMENT);
            Assert.AreEqual(byName["BimGo_Replies"], MarkerFamily.REPLIES);
            Assert.AreEqual(byName["BimGo_Picture"], MarkerFamily.PICTURE);
            Assert.AreEqual(byName["BimGo_Synced"], MarkerFamily.SYNCED);
            Assert.AreEqual(byName["BimGo_IsOpen"], MarkerFamily.IS_OPEN);
            Assert.AreEqual(byName["BimGo_IsInProgress"], MarkerFamily.IS_IN_PROGRESS);
            Assert.AreEqual(byName["BimGo_IsClosed"], MarkerFamily.IS_CLOSED);
            Assert.AreEqual(byName["BimGo_IsHighPriority"], MarkerFamily.IS_HIGH_PRIORITY);
        }

        [TestMethod]
        public void ReadComments_ReadsOnlyTheComments()
        {
            using var folder = new TempFolder();
            string path = folder.File("comments.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(), TestData.Writer, FileKinds.SAVE, out string error), error);
            CommentDocument comments = BimGoReader.ReadComments(path, out error);
            Assert.IsNotNull(comments, error);
            Assert.AreEqual(1, comments.Comments.Count);
            Assert.AreEqual("c1", comments.Comments[0].Id);
            Assert.AreEqual(CommentStatus.OPEN, comments.Comments[0].Status);

            File.WriteAllText(folder.File("bad.bimgo"), "not a zip");
            Assert.IsNull(BimGoReader.ReadComments(folder.File("bad.bimgo"), out error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void Settings_MarkerDefaults()
        {
            var settings = new LaunchSettings();
            Assert.IsTrue(settings.CommentMarkersLive);
            Assert.IsFalse(settings.CommentMarkersRemoveClosed);
            Assert.AreEqual(string.Empty, settings.CommentMarkerFamily);
            settings.CommentMarkerFamily = "  \"C:\\Families\\Marker.rfa\" ";
            settings.Sanitise();
            Assert.AreEqual(@"C:\Families\Marker.rfa", settings.CommentMarkerFamily);
            settings.CommentMarkerFamily = null;
            settings.Sanitise();
            Assert.AreEqual(string.Empty, settings.CommentMarkerFamily);
        }

        private static string FindRepoFile(string relative)
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder != null)
            {
                string candidate = Path.Combine(folder.FullName, relative);
                if (File.Exists(candidate)) { return candidate; }
                folder = folder.Parent;
            }
            return null;
        }
    }
}
