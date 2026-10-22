using System;
using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BimGo.Tests
{
    /// <summary>Cube clicks must pick the visible surface and map to every standard face, edge and corner.</summary>
    [TestClass]
    public sealed class ReviewNavigationTests
    {
        [TestMethod]
        public void EveryStandardOrientation_LooksTowardsCentreAndHitsItsVisibleSurface()
        {
            int count = 0;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                Vector3 outward = new(x, y, z);
                if (outward == Vector3.Zero) { continue; }
                (float yaw, float pitch) = ReviewNavigation.Angles(outward);
                Vector3 forward = new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
                Assert.IsTrue(Vector3.Distance(forward, -Vector3.Normalize(outward)) < 1e-5f);
                Vector3 up = ReviewNavigation.Up(forward);
                Assert.IsTrue(Vector3.Cross(forward, up).LengthSquared() > 0.001f, "pole views need a stable up vector");
                Assert.IsTrue(ReviewNavigation.Hit(Vector2.Zero, forward, up, out Vector3 hit));
                Assert.AreEqual(outward, ReviewNavigation.Snap(hit));
                count++;
            }
            Assert.AreEqual(26, count);
        }

        [TestMethod]
        public void FrontFace_EdgeAndCornerClicksHaveDistinctSnapTargets()
        {
            Vector3 forward = Vector3.UnitY;
            Assert.IsTrue(ReviewNavigation.Hit(Vector2.Zero, forward, Vector3.UnitZ, out Vector3 face));
            Assert.AreEqual(new Vector3(0, -1, 0), ReviewNavigation.Snap(face));
            Assert.IsTrue(ReviewNavigation.Hit(new Vector2(0.85f, 0), forward, Vector3.UnitZ, out Vector3 edge));
            Assert.AreEqual(new Vector3(1, -1, 0), ReviewNavigation.Snap(edge));
            Assert.IsTrue(ReviewNavigation.Hit(new Vector2(0.85f, 0.85f), forward, Vector3.UnitZ, out Vector3 corner));
            Assert.AreEqual(new Vector3(1, -1, 1), ReviewNavigation.Snap(corner));
            Assert.AreEqual(-1f, corner.Y, 1e-6f, "never pick the hidden back face");
        }

        [TestMethod]
        public void OutsideSilhouette_MissesInFaceAndIsometricViews()
        {
            Assert.IsFalse(ReviewNavigation.Hit(new Vector2(1.01f, 0), Vector3.UnitY, Vector3.UnitZ, out _));
            Assert.IsFalse(ReviewNavigation.Hit(new Vector2(0, 1.01f), -Vector3.UnitZ, Vector3.UnitY, out _));
            Vector3 forward = Vector3.Normalize(new Vector3(1, -1, -1));
            Assert.IsFalse(ReviewNavigation.Hit(new Vector2(2, 0), forward, Vector3.UnitZ, out _));
            Assert.IsFalse(ReviewNavigation.Hit(new Vector2(0, 2), forward, Vector3.UnitZ, out _));
        }

        [TestMethod]
        public void HoverPreview_ShowsTheExactVerticesForAll26ClickTargets()
        {
            Span<Vector3> vertices = stackalloc Vector3[4];
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                Vector3 target = new(x, y, z);
                if (target == Vector3.Zero) { continue; }
                int fixedAxes = Math.Abs(x) + Math.Abs(y) + Math.Abs(z);
                int count = ReviewNavigation.PreviewVertices(target, vertices);
                Assert.AreEqual(1 << (3 - fixedAxes), count);
                for (int i = 0; i < count; i++)
                {
                    Vector3 p = vertices[i];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        Assert.AreEqual(1f, MathF.Abs(p[axis]));
                        if (target[axis] != 0) { Assert.AreEqual(target[axis], p[axis]); }
                    }
                    for (int j = 0; j < i; j++) { Assert.AreNotEqual(p, vertices[j]); }
                    if (count > 1) { Assert.AreEqual(2f, Vector3.Distance(p, vertices[(i + 1) % count]), 1e-6f); }
                }
            }
            Assert.AreEqual(0, ReviewNavigation.PreviewVertices(Vector3.Zero, vertices));
        }
    }
}
