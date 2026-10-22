using System;
using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BimGo.Tests
{
    /// <summary>Regression checks for drag-to-pan accidentally selecting rooms and jumping the fly camera.</summary>
    [TestClass]
    public sealed class ReviewPointerTests
    {
        [TestMethod]
        public void Click_IsDeferredUntilReleaseAndEmittedOnlyOnce()
        {
            var pointer = new ReviewPointerDrag();
            pointer.Begin(new Vector2(100, 100), 4);
            Assert.IsFalse(pointer.Step(new Vector2(100, 100), true).Click);
            Assert.IsFalse(pointer.Step(new Vector2(102, 101), true).Click);
            Assert.IsTrue(pointer.Step(new Vector2(102, 101), false).Click);
            Assert.IsFalse(pointer.Active);
            Assert.IsFalse(pointer.Step(new Vector2(102, 101), false).Click);
        }

        [TestMethod]
        public void Pan_CanReturnToItsStartWithoutEverBecomingAClick()
        {
            var pointer = new ReviewPointerDrag();
            pointer.Begin(Vector2.Zero, 4);
            Assert.AreEqual(Vector2.Zero, pointer.Step(new Vector2(2, 0), true).Delta);
            var start = pointer.Step(new Vector2(12, 5), true);
            Assert.AreEqual(new Vector2(12, 5), start.Delta);
            Assert.IsFalse(start.Click);
            var next = pointer.Step(new Vector2(15, 8), true);
            Assert.AreEqual(new Vector2(3, 3), next.Delta);
            Assert.IsFalse(next.Click);
            var release = pointer.Step(Vector2.Zero, false);
            Assert.AreEqual(new Vector2(-15, -8), release.Delta);
            Assert.IsFalse(release.Click, "a pan must never trigger room selection / fly teleport, even at its start point");
        }

        [TestMethod]
        public void QuickReleaseAndFocusLoss_CannotCommitADragAsAClick()
        {
            var pointer = new ReviewPointerDrag();
            pointer.Begin(Vector2.Zero, 4);
            var release = pointer.Step(new Vector2(40, 20), false);
            Assert.IsFalse(release.Click);
            Assert.AreEqual(new Vector2(40, 20), release.Delta);
            pointer.Begin(Vector2.Zero, 4);
            pointer.Cancel();
            Assert.IsFalse(pointer.Step(Vector2.Zero, false).Click);
            Assert.IsFalse(pointer.Active);
        }

        [TestMethod]
        public void Pan_TracksPixelsInParallelAndPerspectiveViewsWithoutChangingHeading()
        {
            const float width = 1200, height = 800, fovY = MathF.PI / 3;
            Vector3 eye = new(10, -10, 6), target = new(0, 0, 1);
            Vector3 forward = Vector3.Normalize(target - eye), right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
            Vector2 pixels = new(70, -35);
            foreach (bool parallel in new[] { true, false })
            {
                Matrix4x4 projection = parallel ? ReviewFraming.Orthographic(30, 20, 0.1f, 1000)
                    : Matrix4x4.CreatePerspectiveFieldOfView(fovY, width / height, 0.1f, 1000);
                float scale = parallel ? 20 / height : 2 * MathF.Tan(fovY * 0.5f) * Vector3.Dot(target - eye, forward) / height;
                Vector3 move = ReviewNavigation.Pan(pixels, right, forward, scale);
                Assert.AreEqual(0f, Vector3.Dot(move, forward), 1e-4f, "pan does not dolly the fly camera or change its heading");
                Vector2 before = Screen(target, Matrix4x4.CreateLookAt(eye, target, Vector3.UnitZ) * projection);
                Vector2 after = Screen(target, Matrix4x4.CreateLookAt(eye + move, target + move, Vector3.UnitZ) * projection);
                Assert.IsTrue(Vector2.Distance(after - before, pixels) < 0.01f);
            }
            static Vector2 Screen(Vector3 p, Matrix4x4 vp)
            {
                Vector4 clip = Vector4.Transform(new Vector4(p, 1), vp);
                return new Vector2((clip.X / clip.W * 0.5f + 0.5f) * width, (0.5f - clip.Y / clip.W * 0.5f) * height);
            }
        }
    }
}
