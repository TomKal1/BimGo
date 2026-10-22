using System;
using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BimGo.Tests
{
    /// <summary>Projection contracts for room reviews: fit in any aspect and parallel picking rays.</summary>
    [TestClass]
    public sealed class ReviewFramingTests
    {
        [TestMethod]
        public void Isometric_ProjectsAxesEqually()
        {
            float yaw = -MathF.PI * 0.25f, pitch = ReviewFraming.IsoPitch;
            Vector3 f = new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
            float x = (Vector3.UnitX - Vector3.Dot(Vector3.UnitX, f) * f).Length();
            float y = (Vector3.UnitY - Vector3.Dot(Vector3.UnitY, f) * f).Length();
            float z = (Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, f) * f).Length();
            Assert.AreEqual(x, y, 1e-5f);
            Assert.AreEqual(x, z, 1e-5f);
        }

        [TestMethod]
        public void Fit_IncludesEveryCornerInIsoAndBothPlans()
        {
            Aabb[] boxes = { new(new Vector3(-100, -3, 0), new Vector3(100, 3, 4)),
                new(new Vector3(2000, 4000, 800), new Vector3(2002, 4003, 900)), new(Vector3.Zero, Vector3.Zero) };
            Vector3 iso = Vector3.Normalize(new Vector3(1, -1, -1));
            Vector3[] directions = { iso, -Vector3.UnitZ, Vector3.UnitZ };
            foreach (Aabb box in boxes)
            foreach (float aspect in new[] { 0.25f, 1f, 2.5f })
            foreach (Vector3 forward in directions)
            {
                Vector3 up = MathF.Abs(forward.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ;
                float height = ReviewFraming.Height(box, forward, up, aspect);
                float distance = box.Size.Length() + 2f;
                Matrix4x4 vp = Matrix4x4.CreateLookAt(box.Center - forward * distance, box.Center, up)
                    * ReviewFraming.Orthographic(height * aspect, height, 0.06f, 4000f);
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = new((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                        (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                    Vector4 clip = Vector4.Transform(new Vector4(p, 1), vp);
                    Assert.IsTrue(MathF.Abs(clip.X) <= 1.001f && MathF.Abs(clip.Y) <= 1.001f);
                    Assert.IsTrue(clip.Z >= -1.001f && clip.Z <= 1.001f);
                }
            }
        }

        [TestMethod]
        public void Projection_UsesGlDepthAndParallelOffsetRays()
        {
            Matrix4x4 projection = ReviewFraming.Orthographic(20, 10, 0.1f, 100);
            Assert.AreEqual(-1f, Vector3.Transform(new Vector3(0, 0, -0.1f), projection).Z, 1e-5f);
            Assert.AreEqual(1f, Vector3.Transform(new Vector3(0, 0, -100), projection).Z, 1e-5f);
            Matrix4x4.Invert(projection, out Matrix4x4 inverse);
            Vector3 leftNear = Vector3.Transform(new Vector3(-0.5f, 0, -1), inverse);
            Vector3 rightNear = Vector3.Transform(new Vector3(0.5f, 0, -1), inverse);
            Vector3 leftFar = Vector3.Transform(new Vector3(-0.5f, 0, 1), inverse);
            Vector3 rightFar = Vector3.Transform(new Vector3(0.5f, 0, 1), inverse);
            Assert.AreEqual(10f, rightNear.X - leftNear.X, 1e-5f);
            Assert.IsTrue(Vector3.Distance(Vector3.Normalize(leftFar - leftNear), Vector3.Normalize(rightFar - rightNear)) < 1e-5f);
        }
    }
}
