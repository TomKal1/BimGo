using System;
using System.Collections.Generic;
using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round X-C1: finding the glass / mirror planes for planar reflections: both faces of a pane are one plane, coplanar
    /// panes merge, other materials and tiny panes are left out.
    /// </summary>
    [TestClass]
    public sealed class ReflectivePlaneTests
    {
        private static void Quad(List<SceneVertex> v, List<uint> i, List<ushort> m, ushort material, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            uint o = (uint)v.Count;
            foreach (Vector3 p in new[] { a, b, c, d })
            {
                v.Add(new SceneVertex(p, Vector3.UnitZ, 0xFFFFFFFF));
                m.Add(material);
            }
            i.AddRange(new[] { o, o + 1, o + 2, o, o + 2, o + 3 });
        }

        [TestMethod]
        public void Find_MergesPaneFacesAndCoplanarPanes_SkipsOthers()
        {
            var v = new List<SceneVertex>();
            var i = new List<uint>();
            var m = new List<ushort>();
            // Glass (material 0): a 2 × 2 m pane in the plane y = 5, faces 10 mm apart, wound both ways
            Quad(v, i, m, 0, new Vector3(0, 5f, 0), new Vector3(2, 5f, 0), new Vector3(2, 5f, 2), new Vector3(0, 5f, 2));
            Quad(v, i, m, 0, new Vector3(0, 5.01f, 0), new Vector3(0, 5.01f, 2), new Vector3(2, 5.01f, 2), new Vector3(2, 5.01f, 0));
            // A second pane in the same plane, further along
            Quad(v, i, m, 0, new Vector3(6, 5f, 0), new Vector3(8, 5f, 0), new Vector3(8, 5f, 2), new Vector3(6, 5f, 2));
            // A mirror (material 2) on the wall x = 1
            Quad(v, i, m, 2, new Vector3(1, 0, 0.5f), new Vector3(1, 1, 0.5f), new Vector3(1, 1, 1.5f), new Vector3(1, 0, 1.5f));
            // A tiny glass pane (0.1 × 0.1 m) and a wall (material 1, not reflective)
            Quad(v, i, m, 0, new Vector3(3, 2, 0), new Vector3(3, 2.1f, 0), new Vector3(3, 2.1f, 0.1f), new Vector3(3, 2, 0.1f));
            Quad(v, i, m, 1, new Vector3(0, 9, 0), new Vector3(9, 9, 0), new Vector3(9, 9, 3), new Vector3(0, 9, 3));

            ReflectivePlane[] planes = ReflectivePlanes.Find(v.ToArray(), i.ToArray(), m.ToArray(), new[] { true, false, true });

            Assert.AreEqual(2, planes.Length);
            ReflectivePlane glass = planes[0];
            Assert.AreEqual(1f, glass.Normal.Y, 1e-4f, "canonical normal: largest component positive");
            Assert.AreEqual(5.005f, glass.Offset, 0.01f);
            Assert.AreEqual(12f, glass.Area, 0.01f, "two faces of one pane and the other pane");
            Assert.AreEqual(0f, glass.Bounds.Min.X, 1e-4f);
            Assert.AreEqual(8f, glass.Bounds.Max.X, 1e-4f);
            Assert.IsTrue(glass.Slack >= 0.004f && glass.Slack < 0.02f, "the pane's faces either side");
            Assert.AreEqual(1f, planes[1].Normal.X, 1e-4f);
            Assert.AreEqual(1f, planes[1].Offset, 1e-4f);

            // A sliding door: two panels on tracks 8 cm apart, overlapping in plan, are one plane with the slack to match
            var v2 = new List<SceneVertex>();
            var i2 = new List<uint>();
            var m2 = new List<ushort>();
            Quad(v2, i2, m2, 0, new Vector3(0, 2f, 0), new Vector3(1.2f, 2f, 0), new Vector3(1.2f, 2f, 2.1f), new Vector3(0, 2f, 2.1f));
            Quad(v2, i2, m2, 0, new Vector3(1f, 2.08f, 0), new Vector3(2.2f, 2.08f, 0), new Vector3(2.2f, 2.08f, 2.1f), new Vector3(1f, 2.08f, 2.1f));
            ReflectivePlane[] door = ReflectivePlanes.Find(v2.ToArray(), i2.ToArray(), m2.ToArray(), new[] { true });
            Assert.AreEqual(1, door.Length);
            Assert.AreEqual(2.04f, door[0].Offset, 0.005f);
            Assert.AreEqual(0.04f, door[0].Slack, 0.005f);

            Assert.AreEqual(0, ReflectivePlanes.Find(v.ToArray(), i.ToArray(), m.ToArray(), new[] { false, false, false }).Length);
            Assert.AreEqual(Vector3.UnitX, ReflectivePlanes.Canonical(-Vector3.UnitX));
        }
    }
}
