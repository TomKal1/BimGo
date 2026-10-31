using System.Collections.Generic;
using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Depth tie-break for overlapping coplanar faces: elements whose faces share a plane, face the same way and overlap
    /// get different tiers (the larger one behind); touching edges, other planes and opposite faces are left alone.
    /// </summary>
    [TestClass]
    public sealed class CoplanarTierTests
    {
        private readonly List<SceneVertex> _vertices = new();
        private readonly List<uint> _indices = new();
        private readonly List<ElementRecord> _elements = new();

        /// <summary>A horizontal rectangle at height z as one element, facing up (or down).</summary>
        private void Floor(float x0, float y0, float x1, float y1, float z, bool up = true)
        {
            int start = _indices.Count;
            uint o = (uint)_vertices.Count;
            foreach (Vector3 p in new[] { new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z) })
            {
                _vertices.Add(new SceneVertex(p, up ? Vector3.UnitZ : -Vector3.UnitZ, 0xFFFFFFFF));
            }
            _indices.AddRange(up ? new[] { o, o + 1, o + 2, o, o + 2, o + 3 } : new[] { o, o + 2, o + 1, o, o + 3, o + 2 });
            _elements.Add(new ElementRecord { OpaqueStart = start, OpaqueCount = 6 });
        }

        private byte[] Find() => CoplanarTiers.Find(_vertices.ToArray(), _indices.ToArray(), _elements.ToArray());

        [TestMethod]
        public void Find_OverlappingFloors_LargerBehind()
        {
            Floor(0, 0, 10, 8, 2.4638f); // slab
            Floor(1, 1, 9, 7, 2.4638f); // laminate over the room
            Floor(3, 2, 5, 4, 2.4638f); // a concrete patch on both
            Floor(20, 0, 25, 5, 2.4638f); // another room's floor, away from the rest

            byte[] tiers = Find();

            Assert.IsNotNull(tiers);
            Assert.AreEqual(0, tiers[0], "largest at the back");
            Assert.AreEqual(1, tiers[1]);
            Assert.AreEqual(2, tiers[2], "overlaps both: above both");
            Assert.AreEqual(0, tiers[3], "no overlap: unchanged");
        }

        [TestMethod]
        public void Find_TouchingOtherPlaneOrOppositeFaces_Null()
        {
            Floor(0, 0, 5, 5, 0f);
            Floor(5, 0, 10, 5, 0f); // shares an edge only
            Floor(0, 0, 5, 5, 0.01f); // 10 mm higher: depth tells them apart
            Floor(0, 0, 5, 5, 0.01f, up: false); // the same plane facing down: never seen together

            Assert.IsNull(Find());
        }

        [TestMethod]
        public void Find_PlaneOnBucketEdge_StillLinked()
        {
            // Offsets either side of a 2 mm quantisation step, 0.2 mm apart
            Floor(0, 0, 4, 4, 0.0019f);
            Floor(1, 1, 3, 3, 0.0021f);

            byte[] tiers = Find();

            Assert.IsNotNull(tiers);
            Assert.AreNotEqual(tiers[0], tiers[1]);
        }
    }
}
