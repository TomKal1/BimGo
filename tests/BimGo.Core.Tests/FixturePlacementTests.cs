using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// A fixture without a glowing surface: a pendant (origin at its canopy, the top of its box) lights from just below its
    /// shade, a floor lamp (origin at its base) from near its top; older exports' pendant placement (under the ceiling) is
    /// recognised and moved.
    /// </summary>
    [TestClass]
    public sealed class FixturePlacementTests
    {
        // Snowdon's Pendant-Dome 18" D x 5" H: shade bottom 3.6322, cord to the ceiling at 5.588, origin at the canopy; it
        // hangs into a double-height room, only 1.17 m above the L2 level plane
        private static readonly Aabb PENDANT = new(new Vector3(-15.786725f, 4.9215226f, 3.6322f), new Vector3(-15.329525f, 5.3372617f, 5.588f));
        private static readonly Vector3 CANOPY = new(-15.558125f, 5.1313f, 5.588f);
        private static readonly Aabb LAMP = new(new Vector3(0f, 0f, 0f), new Vector3(0.4f, 0.4f, 1.6f));
        private static readonly Vector3 BASE = new(0.2f, 0.2f, 0f);

        [TestMethod]
        public void WithoutLens_PendantLightsBelowItsShade()
        {
            Vector3 p = FixturePlacement.WithoutLens(PENDANT, CANOPY);
            Assert.AreEqual(3.6322f - FixturePlacement.BELOW_BOTTOM, p.Z, 1e-5f);
            Assert.AreEqual(PENDANT.Center.X, p.X, 1e-5f);
        }

        [TestMethod]
        public void WithoutLens_FloorLampOrUnknownOriginLightsNearTheTop()
        {
            Assert.AreEqual(1.6f - 0.15f * 1.6f, FixturePlacement.WithoutLens(LAMP, BASE).Z, 1e-5f);
            Assert.AreEqual(5.588f - 0.15f * (5.588f - 3.6322f), FixturePlacement.WithoutLens(PENDANT, null).Z, 1e-5f, "no origin: as before");
        }

        [TestMethod]
        public void Corrected_MovesAnOlderExportsPendantLight()
        {
            Vector3 exported = new(-15.558125f, 5.129392f, 5.29463f); // as exported: 15 % of the height below the top
            Vector3 p = FixturePlacement.Corrected(exported, PENDANT, CANOPY, out bool moved);
            Assert.IsTrue(moved);
            Assert.AreEqual(3.6322f - FixturePlacement.BELOW_BOTTOM, p.Z, 1e-4f);
        }

        [TestMethod]
        public void Corrected_LeavesLensLightsFloorLampsAndUnknownOriginsAlone()
        {
            Vector3 lens = new(-15.558125f, 5.129392f, 3.70f); // a light at a glowing shade
            Assert.AreEqual(lens, FixturePlacement.Corrected(lens, PENDANT, CANOPY, out bool moved));
            Assert.IsFalse(moved);
            Vector3 top = FixturePlacement.WithoutLens(LAMP, BASE);
            Assert.AreEqual(top, FixturePlacement.Corrected(top, LAMP, BASE, out moved));
            Assert.IsFalse(moved, "a standing lamp keeps its light near the top");
            Vector3 old = new(-15.558125f, 5.129392f, 5.29463f);
            Assert.AreEqual(old, FixturePlacement.Corrected(old, PENDANT, null, out moved));
            Assert.IsFalse(moved, "a linked fixture without an origin isn't guessed at");
        }
    }
}
