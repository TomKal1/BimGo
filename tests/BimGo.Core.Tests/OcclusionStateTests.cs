using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round Y-D1: the occlusion culling decisions (the GL queries themselves are the renderer's).
    /// </summary>
    [TestClass]
    public sealed class OcclusionStateTests
    {
        private static readonly Vector3 FORWARD = Vector3.UnitY;

        [TestMethod]
        public void Chunks_StartVisibleAndFollowTheirAnswers()
        {
            var state = new OcclusionState(3);
            state.BeginFrame(Vector3.Zero, FORWARD);
            Assert.AreEqual(0, state.HiddenCount());

            state.Issued(1);
            Assert.IsTrue(state.IsPending(1));
            Assert.IsFalse(state.NeedsQuery(1), "one query at a time");
            state.Answered(1, anySamplePassed: false);
            Assert.IsTrue(state.Hidden[1]);
            Assert.IsFalse(state.IsPending(1));

            state.Issued(1);
            state.Answered(1, anySamplePassed: true);
            Assert.IsFalse(state.Hidden[1]);
            Assert.AreEqual(0, state.HiddenCount());
        }

        [TestMethod]
        public void Queries_HiddenEveryFrameVisibleStaggered()
        {
            var state = new OcclusionState(OcclusionState.VISIBLE_RECHECK * 3);
            state.BeginFrame(Vector3.Zero, FORWARD);
            state.Issued(0);
            state.Answered(0, false);

            for (int f = 0; f < OcclusionState.VISIBLE_RECHECK; f++)
            {
                if (f > 0) { state.BeginFrame(Vector3.Zero, FORWARD); }
                Assert.IsTrue(state.NeedsQuery(0), "a hidden chunk is checked every frame");
                int visibleDue = 0;
                for (int c = 1; c < state.Count; c++) { if (state.NeedsQuery(c)) { visibleDue++; } }
                Assert.IsTrue(visibleDue >= 2 && visibleDue <= 3, $"frame {f}: {visibleDue} visible chunks due (about a quarter)");
            }

            // Over VISIBLE_RECHECK frames every visible chunk comes due once
            var due = new int[state.Count];
            for (int f = 0; f < OcclusionState.VISIBLE_RECHECK; f++)
            {
                state.BeginFrame(Vector3.Zero, FORWARD);
                for (int c = 1; c < state.Count; c++) { if (state.NeedsQuery(c)) { due[c]++; } }
            }
            for (int c = 1; c < state.Count; c++) { Assert.AreEqual(1, due[c], $"chunk {c}"); }
        }

        [TestMethod]
        public void LateAnswers_KeepTheChunkDrawn()
        {
            var state = new OcclusionState(1);
            state.BeginFrame(Vector3.Zero, FORWARD);
            state.Issued(0);
            state.Answered(0, false);
            Assert.IsTrue(state.Hidden[0]);

            state.BeginFrame(Vector3.Zero, FORWARD);
            state.Issued(0);
            state.BeginFrame(Vector3.Zero, FORWARD);
            state.NotAnswered(0); // the GPU is behind
            Assert.IsFalse(state.Hidden[0], "drawn until the answer comes");
            state.Answered(0, false);
            Assert.IsTrue(state.Hidden[0]);
        }

        [TestMethod]
        public void Forget_IgnoresThePendingAnswer()
        {
            var state = new OcclusionState(2);
            state.BeginFrame(Vector3.Zero, FORWARD);
            state.Issued(0);
            state.Forget(0); // left the view
            state.Answered(0, false);
            Assert.IsFalse(state.Hidden[0], "an answer about an older view doesn't hide it");
            Assert.IsFalse(state.IsPending(0));

            state.Issued(0);
            state.Answered(0, false);
            Assert.IsTrue(state.Hidden[0], "a fresh query counts again");

            state.Near(0);
            Assert.IsFalse(state.Hidden[0]);
        }

        [TestMethod]
        public void CameraJump_ShowsEverything()
        {
            var state = new OcclusionState(2);
            Assert.IsFalse(state.BeginFrame(Vector3.Zero, FORWARD), "the first frame isn't a jump");
            state.Issued(0);
            state.Answered(0, false);
            state.Issued(1);

            Assert.IsFalse(state.BeginFrame(new Vector3(0.2f, 0f, 0f), FORWARD), "walking");
            Assert.IsTrue(state.Hidden[0]);

            Assert.IsTrue(state.BeginFrame(new Vector3(10f, 0f, 0f), FORWARD), "teleport");
            Assert.IsFalse(state.Hidden[0]);
            state.Answered(1, false);
            Assert.IsFalse(state.Hidden[1], "an answer from before the jump is ignored");
            Assert.AreEqual(1, state.Jumps);

            Vector3 turned = Vector3.Normalize(new Vector3(1f, 1f, 0f)); // 45°
            Assert.IsTrue(state.BeginFrame(new Vector3(10f, 0f, 0f), turned), "a quick turn");
            Vector3 small = Vector3.Normalize(new Vector3(1f, 1.2f, 0f)); // a few degrees on
            Assert.IsFalse(state.BeginFrame(new Vector3(10f, 0f, 0f), small));
        }

        [TestMethod]
        public void Near_GrowsTheBox()
        {
            var box = new Aabb(new Vector3(0f, 0f, 0f), new Vector3(4f, 4f, 3f));
            Assert.IsTrue(OcclusionState.IsNear(box, new Vector3(2f, 2f, 1.6f)), "inside");
            Assert.IsTrue(OcclusionState.IsNear(box, new Vector3(-1f, 2f, 1.6f)), "a metre outside");
            Assert.IsFalse(OcclusionState.IsNear(box, new Vector3(-2f, 2f, 1.6f)));
            Assert.IsFalse(OcclusionState.IsNear(box, new Vector3(2f, 2f, 5f)), "the floor above");
        }

        [TestMethod]
        public void Settings_CullingAutoDetailsOff()
        {
            Assert.AreEqual(1, new LaunchSettings().OcclusionMode, "auto");
            Assert.IsFalse(new LaunchSettings().ShowPerformance);
            LaunchSettings older = System.Text.Json.JsonSerializer.Deserialize<LaunchSettings>("{\"ShowFps\":true,\"OcclusionCulling\":true}");
            Assert.AreEqual(1, older.OcclusionMode, "a settings file from before Y-D1 r2 is auto");
            LaunchSettings on = System.Text.Json.JsonSerializer.Deserialize<LaunchSettings>("{\"OcclusionMode\":2,\"ShowPerformance\":true}");
            Assert.AreEqual(2, on.OcclusionMode);
            Assert.IsTrue(on.ShowPerformance);
            var odd = new LaunchSettings { OcclusionMode = 9 };
            odd.Sanitise();
            Assert.AreEqual(2, odd.OcclusionMode);
        }

        [TestMethod]
        public void Clusters_GroupByCellAndStorey()
        {
            var bounds = new[]
            {
                new Aabb(new Vector3(1, 1, 0), new Vector3(3, 3, 3)),     // cell (0, 0), storey 0
                new Aabb(new Vector3(5, 5, 0), new Vector3(9, 9, 3)),     // same cell and storey
                new Aabb(new Vector3(5, 5, 4), new Vector3(9, 9, 7)),     // the storey above
                new Aabb(new Vector3(20, 1, 0), new Vector3(22, 3, 3))    // the next cell
            };
            int[] clusterOf = OcclusionState.BuildClusters(bounds, OcclusionState.CLUSTER_CELL, OcclusionState.CLUSTER_SLAB, out int count);
            Assert.AreEqual(3, count);
            Assert.AreEqual(clusterOf[0], clusterOf[1]);
            Assert.AreNotEqual(clusterOf[1], clusterOf[2]);
            Assert.AreNotEqual(clusterOf[0], clusterOf[3]);

            var state = new OcclusionState(4);
            state.SetClusters(clusterOf, count);
            Assert.AreEqual(3, state.ClusterCount);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, state.Members(clusterOf[0]));
        }

        [TestMethod]
        public void Clusters_StandInForHiddenChunks()
        {
            var state = new OcclusionState(3);
            state.SetClusters(new[] { 0, 0, 0 }, 1);
            state.BeginFrame(Vector3.Zero, FORWARD);
            for (int c = 0; c < 3; c++) { state.MarkInView(c); }
            Assert.IsFalse(state.ClusterDue(0), "visible chunks: no cluster query");

            for (int c = 0; c < 3; c++)
            {
                state.Issued(c);
                state.Answered(c, false);
            }
            Assert.IsTrue(state.ClusterDue(0), "all hidden: one query for the lot");
            state.ClusterIssued(0);
            Assert.IsTrue(state.IsClusterPending(0));
            Assert.IsFalse(state.ClusterDue(0), "one at a time");

            // No sample: they stay hidden
            state.ClusterAnswered(0, false);
            Assert.AreEqual(3, state.HiddenCount());

            // Late: drawn meanwhile
            state.BeginFrame(Vector3.Zero, FORWARD);
            for (int c = 0; c < 3; c++) { state.MarkInView(c); }
            state.ClusterIssued(0);
            state.ClusterNotAnswered(0);
            Assert.AreEqual(0, state.HiddenCount());

            // Any sample: all shown
            for (int c = 0; c < 3; c++)
            {
                state.Issued(c);
                state.Answered(c, false);
            }
            state.ClusterAnswered(0, true);
            Assert.AreEqual(0, state.HiddenCount());

            // A chunk with its own query out, or only one chunk in view: no cluster query
            state.BeginFrame(Vector3.Zero, FORWARD);
            for (int c = 0; c < 3; c++)
            {
                state.Issued(c);
                state.Answered(c, false);
                state.MarkInView(c);
            }
            state.Issued(1);
            Assert.IsFalse(state.ClusterDue(0));
            state.BeginFrame(Vector3.Zero, FORWARD);
            state.Answered(1, false);
            state.MarkInView(0);
            Assert.IsFalse(state.ClusterDue(0), "one chunk in view: its own query will do");
        }

        [TestMethod]
        public void Settings_LightQualityMediumPrepassOn()
        {
            Assert.AreEqual(1, new LaunchSettings().LightQuality, "Y-D2: medium");
            Assert.IsTrue(new LaunchSettings().DepthPrepass);
            LaunchSettings older = System.Text.Json.JsonSerializer.Deserialize<LaunchSettings>("{\"ShowFps\":true}");
            Assert.AreEqual(1, older.LightQuality, "a settings file from before Y-D2 is medium");
            Assert.IsTrue(older.DepthPrepass);
            var odd = new LaunchSettings { LightQuality = -4 };
            odd.Sanitise();
            Assert.AreEqual(0, odd.LightQuality);
        }
    }
}
