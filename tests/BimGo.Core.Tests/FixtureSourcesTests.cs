using System;
using System.Linq;
using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Which model's fixtures light the scene: a linked model's copy of a host fixture (same type, same spot) is dropped
    /// by default; fixtures only the link has still light; each mode picks what it says.
    /// </summary>
    [TestClass]
    public sealed class FixtureSourcesTests
    {
        /// <summary>
        /// Lights: 0 host downlight; 1 its copy in link 1 (2 cm away, or <c>copyX</c>); 2 a link-only sconce; 3 a link fixture of another type
        /// on the host downlight's spot (not a copy); 4 a host pendant.
        /// </summary>
        private static SceneData Scene(float copyX = 1.02f)
        {
            ElementRecord Element(string type, int link) => new() { FamilyType = type, Link = link };
            LightSource Light(int element, float x, float y, float z) => new() { Element = element, Position = new Vector3(x, y, z) };
            return new SceneData
            {
                Vertices = Array.Empty<SceneVertex>(),
                Indices = Array.Empty<uint>(),
                Elements = new[]
                {
                    Element("Downlight: 4\"", 0),
                    Element("Downlight: 4\"", 1),
                    Element("Sconce: Uplight", 1),
                    Element("Exit Sign: Single", 1),
                    Element("Pendant: Dome", 0)
                },
                Lighting = new LightingData
                {
                    Lights = new[]
                    {
                        Light(0, 1f, 2f, 3f),
                        Light(1, copyX, 2f, 3f),
                        Light(2, 5f, 2f, 3f),
                        Light(3, 1f, 2f, 3f),
                        Light(4, 9f, 2f, 3f)
                    }
                },
                Settings = new LaunchSettings()
            };
        }

        [TestMethod]
        public void Auto_DropsOnlyLinkedCopiesOfHostFixtures()
        {
            bool[] kept = FixtureSources.Kept(Scene(), FixtureSources.AUTO, out int copies);
            CollectionAssert.AreEqual(new[] { true, false, true, true, true }, kept);
            Assert.AreEqual(1, copies);
        }

        [TestMethod]
        public void Auto_KeepsALinkedFixtureFartherThanTheSameSpot()
        {
            SceneData scene = Scene(copyX: 1f + FixtureSources.SAME_SPOT * 2f);
            Assert.IsTrue(FixtureSources.Kept(scene, FixtureSources.AUTO, out int copies)[1]);
            Assert.AreEqual(0, copies);
        }

        [TestMethod]
        public void Modes_PickTheirModel()
        {
            SceneData scene = Scene();
            CollectionAssert.AreEqual(new[] { true, false, false, false, true }, FixtureSources.Kept(scene, FixtureSources.HOST, out _));
            CollectionAssert.AreEqual(new[] { false, true, true, true, false }, FixtureSources.Kept(scene, FixtureSources.LINKS, out _));
            Assert.IsTrue(FixtureSources.Kept(scene, FixtureSources.ALL, out _).All(k => k));
        }

        [TestMethod]
        public void Document_KeepsAChoiceAndDropsAuto()
        {
            var document = new BimGo.Format.LightProfileDocument { FixtureSource = FixtureSources.HOST }.Clean();
            Assert.AreEqual(FixtureSources.HOST, document.FixtureSource);
            Assert.IsFalse(document.IsEmpty, "a choice is saved");
            document = new BimGo.Format.LightProfileDocument { FixtureSource = FixtureSources.AUTO }.Clean();
            Assert.IsNull(document.FixtureSource, "the default isn't stored");
            Assert.IsTrue(document.IsEmpty);
        }

        [TestMethod]
        public void Clean_FallsBackToAuto()
        {
            Assert.AreEqual(FixtureSources.AUTO, FixtureSources.Clean(null));
            Assert.AreEqual(FixtureSources.AUTO, FixtureSources.Clean("everything"));
            Assert.AreEqual(FixtureSources.HOST, FixtureSources.Clean(FixtureSources.HOST));
        }
    }
}
