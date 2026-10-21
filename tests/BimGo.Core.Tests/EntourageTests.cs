using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round X-B: billboards: the built-in pictures, the entourage document (cleaning, colours), its place in a .bimgo
    /// and in a live session's model folder, and the picture helpers (fit, key-out, edge bleed).
    /// </summary>
    [TestClass]
    public sealed class EntourageTests
    {
        [TestMethod]
        public void BuiltIns_DrawStandOnTheGroundAndRepeat()
        {
            var keys = new HashSet<string>();
            foreach (EntourageBuiltIn builtIn in EntourageArt.BUILT_INS)
            {
                Assert.IsTrue(keys.Add(builtIn.Key), "unique key " + builtIn.Key);
                Assert.IsTrue(Array.IndexOf(EntourageCategories.ALL, builtIn.Category) >= 0, builtIn.Key);
                EntourageImage image = EntourageArt.Draw(builtIn.Key);
                Assert.AreEqual(EntourageArt.SIZE, image.Width);
                Assert.IsFalse(image.IsEmpty, builtIn.Key + " drew nothing");
                Assert.IsTrue(image.MaxY >= EntourageArt.SIZE - 12, $"{builtIn.Key} stands on the bottom edge ({image.MaxY})");
                Assert.IsTrue(image.MinX >= 0 && image.MaxX <= EntourageArt.SIZE && image.MinY >= 0, builtIn.Key);
                Assert.IsTrue(image.MeanLuminance > 0.05f && image.MeanLuminance < 0.95f, builtIn.Key);
                switch (builtIn.Category)
                {
                    case EntourageCategories.PERSON: Assert.IsTrue(image.Aspect < 0.6f, $"{builtIn.Key} aspect {image.Aspect}"); break;
                    case EntourageCategories.CAR: Assert.IsTrue(image.Aspect > 2f, $"{builtIn.Key} aspect {image.Aspect}"); break;
                }
            }
            Assert.IsTrue(EntourageArt.Draw("shrub-hedge").Aspect > 2f);
            CollectionAssert.AreEqual(EntourageArt.Draw("tree-round").Rgba, EntourageArt.Draw("tree-round").Rgba, "seeded: the same picture every time");
            Assert.IsTrue(EntourageArt.Draw("no-such").IsEmpty);
            Assert.IsNotNull(EntourageArt.Find("car-suv"));
            Assert.IsNull(EntourageArt.Find("car-tank"));

            // Round X-B3: the gum tree; traced silhouettes (none shipped yet) and their fill
            Assert.IsNotNull(EntourageArt.Find("tree-gum"));
            Assert.AreEqual(EntourageArt.BUILT_INS.Length, EntourageArt.BUILT_INS.Select(b => b.Key).Distinct().Count());
            Assert.IsNull(EntourageShapes.Find("person-man"));
            Assert.IsTrue(EntourageShapes.FillOf(EntourageCategories.PERSON) < EntourageCategories.DEFAULT_COLOURS[2]);
        }

        [TestMethod]
        public void Picture_FitKeyOutAndBleed()
        {
            // A 40 × 20 white picture with a 10 × 16 red block standing on its bottom edge, no transparency
            var rgba = new byte[40 * 20 * 4];
            for (int y = 0; y < 20; y++)
            {
                for (int x = 0; x < 40; x++)
                {
                    int i = (y * 40 + x) * 4;
                    bool block = x >= 15 && x < 25 && y >= 4;
                    rgba[i] = 255;
                    rgba[i + 1] = rgba[i + 2] = (byte)(block ? 0 : 255);
                    rgba[i + 3] = 255;
                }
            }
            var image = new EntourageImage(40, 20, rgba);
            Assert.AreEqual(40, image.MaxX - image.MinX, "opaque: the whole picture");
            Assert.IsTrue(image.KeyOutBackground());
            Assert.AreEqual(15, image.MinX);
            Assert.AreEqual(25, image.MaxX);
            Assert.AreEqual(4, image.MinY);
            Assert.AreEqual(20, image.MaxY);
            Assert.AreEqual(10f / 16f, image.Aspect, 1e-6f);
            Assert.IsFalse(image.KeyOutBackground(), "already transparent");

            EntourageImage fitted = image.FitInto(64);
            Assert.AreEqual(64, fitted.Width);
            Assert.IsTrue(fitted.MaxY >= 62, "stands on the bottom");
            Assert.AreEqual(image.Aspect, fitted.Aspect, 0.08f);
            Assert.IsTrue(Math.Abs((fitted.MinX + fitted.MaxX) / 2 - 32) <= 2, "centred");

            fitted.BleedEdges(2);
            int outside = ((fitted.MaxY - 1) * 64 + fitted.MinX - 1) * 4;
            Assert.AreEqual(0, fitted.Rgba[outside + 3], "alpha unchanged");
            Assert.IsTrue(fitted.Rgba[outside] > 200 && fitted.Rgba[outside + 1] < 60, "the red spread into the transparent edge");
        }

        [TestMethod]
        public void Document_CleanColoursAndTypes()
        {
            var document = new EntourageDocument
            {
                Colours = new Dictionary<string, string> { ["Tree"] = "#112233", ["person"] = "nonsense", ["boat"] = "445566" },
                Types = new List<EntourageType>
                {
                    new() { Id = "a1", Name = "  My oak ", Category = "TREE", Height = 500f, Image = "a1.png" },
                    new() { Id = "a1", Name = "Duplicate", Image = "a1b.png" },
                    new() { Id = "bad", Name = "Unsafe", Image = "../evil.png" }
                },
                Items = new List<EntourageItem>
                {
                    new() { Id = "i1", Type = "tree-round", X = 1, Y = 2, Z = 0, Height = 7f },
                    new() { Id = "i2", Type = "img:a1", X = 3, Y = 4, Z = 0, Height = 0.01f, Flip = true },
                    new() { Id = "i3", Type = "img:bad", X = 0, Y = 0, Z = 0 },
                    new() { Id = "i4", Type = "car-tank", X = 0, Y = 0, Z = 0 },
                    new() { Id = "i1", Type = "tree-round", X = 9, Y = 9, Z = 0 },
                    new() { Id = "i5", Type = "person-man", X = double.NaN, Y = 0, Z = 0 },
                    null
                }
            }.Clean();

            Assert.AreEqual(1, document.Types.Count);
            Assert.AreEqual("My oak", document.Types[0].Name);
            Assert.AreEqual(EntourageCategories.TREE, document.Types[0].Category);
            Assert.AreEqual(EntourageItem.MAX_HEIGHT, document.Types[0].Height);
            CollectionAssert.AreEqual(new[] { "i1", "i2" }, document.Items.Select(i => i.Id).ToArray());
            Assert.AreEqual(EntourageItem.MIN_HEIGHT, document.Items[1].Height);
            Assert.AreSame(document.Types[0], document.FindType("img:a1"));
            Assert.IsNull(document.FindType("tree-round"));

            Assert.AreEqual(0x112233u, document.ColourOf("tree"));
            Assert.AreEqual(0x445566u, document.ColourOf("other"), "unknown category → other");
            Assert.AreEqual(EntourageCategories.DEFAULT_COLOURS[EntourageCategories.IndexOf("person")], document.ColourOf("person"));
            document.SetColour("car", 0xABCDEF);
            Assert.AreEqual("#ABCDEF", document.Colours["car"]);
            document.SetColour("car", EntourageCategories.DEFAULT_COLOURS[EntourageCategories.IndexOf("car")]);
            Assert.IsFalse(document.Colours.ContainsKey("car"), "the default colour clears the entry");

            Assert.IsTrue(new EntourageDocument().IsEmpty);
            Assert.IsFalse(new EntourageDocument { Textured = true }.IsEmpty);
        }

        [TestMethod]
        public void BimGo_RoundTripsEntourageAndPictures()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            document.Entourage = new EntourageDocument
            {
                Model = "Test Model",
                Textured = true,
                Types = new List<EntourageType> { new() { Id = "pic1", Name = "Oak", Category = "tree", Height = 12f, Image = "pic1.png" } },
                Items = new List<EntourageItem>
                {
                    new() { Id = "a", Type = "img:pic1", X = 1.5, Y = -2.25, Z = 0.1, Height = 11f, Flip = true },
                    new() { Id = "b", Type = "person-woman", X = 4, Y = 5, Z = 0, Height = 1.66f }
                }
            };
            document.Entourage.SetColour("person", 0x102030);
            document.EntourageImages = new Dictionary<string, byte[]> { ["pic1.png"] = png };

            string path = folder.File("model.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            Assert.IsNotNull(read.Entourage);
            Assert.IsTrue(read.Entourage.Textured);
            Assert.AreEqual(0x102030u, read.Entourage.ColourOf("person"));
            Assert.AreEqual(2, read.Entourage.Items.Count);
            Assert.AreEqual(-2.25, read.Entourage.Items[0].Y);
            Assert.IsTrue(read.Entourage.Items[0].Flip);
            Assert.AreEqual("Oak", read.Entourage.Types[0].Name);
            CollectionAssert.AreEqual(png, read.EntourageImages["pic1.png"]);

            // Nothing to say: no entry at all (older readers and files stay as they were)
            BimGoDocument bare = TestData.BuildDocument();
            bare.Entourage = new EntourageDocument();
            string barePath = folder.File("bare.bimgo");
            Assert.IsTrue(BimGoWriter.Write(barePath, bare, TestData.Writer, FileKinds.SAVE, out error), error);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(barePath)) { Assert.IsNull(zip.GetEntry("entourage.json")); }
            Assert.IsNull(BimGoReader.Read(barePath, new LaunchSettings(), out _).Entourage);
        }

        private static EntourageSlot Slot(string category, string family, string type, float height, float width, string uid = "u1", string link = "") =>
            new() { UniqueId = uid, LinkKey = link, Category = category, Family = family, Type = type, Height = height, Width = width, Base = new System.Numerics.Vector3(1, 2, 0) };

        [TestMethod]
        public void Swaps_AutoChoiceByNameThenSize()
        {
            Assert.AreEqual("tree-palm", EntourageSlots.AutoType(Slot("planting", "RPC Tree - Tropical", "Date Palm", 8f, 6f)));
            Assert.AreEqual("tree-conifer", EntourageSlots.AutoType(Slot("planting", "Pine", "Scots Pine 12m", 12f, 5f)));
            Assert.AreEqual("shrub-hedge", EntourageSlots.AutoType(Slot("planting", "Hedge", "Box 1m", 1f, 6f)));
            Assert.AreEqual("shrub-grass", EntourageSlots.AutoType(Slot("planting", "Lomandra", "Clump", 0.8f, 0.8f)));
            Assert.AreEqual("tree-round", EntourageSlots.AutoType(Slot("planting", "Deciduous", "Maple", 9f, 7f)));
            Assert.AreEqual("tree-tall", EntourageSlots.AutoType(Slot("planting", "Deciduous", "X", 12f, 4f)));
            Assert.AreEqual("tree-spreading", EntourageSlots.AutoType(Slot("planting", "Deciduous", "X", 8f, 12f)));
            Assert.AreEqual("shrub-round", EntourageSlots.AutoType(Slot("planting", "Generic", "X", 1.2f, 1.5f)));
            Assert.AreEqual("car-suv", EntourageSlots.AutoType(Slot("entourage", "RPC Mid-Size SUV", "Van", 1.9f, 4.8f)));
            Assert.AreEqual("car-sedan", EntourageSlots.AutoType(Slot("entourage", "Entourage", "Generic", 1.4f, 4.5f)));
            Assert.AreEqual("person-woman", EntourageSlots.AutoType(Slot("entourage", "RPC Female", "Lisa", 1.65f, 0.5f)));
            Assert.AreEqual("person-child", EntourageSlots.AutoType(Slot("entourage", "Generic", "X", 1.1f, 0.4f)));
            Assert.AreEqual("person-man", EntourageSlots.AutoType(Slot("entourage", "RPC Male", "Alex", 1.8f, 0.5f)));
        }

        [TestMethod]
        public void Swaps_InstanceThenTypeThenAuto()
        {
            EntourageSlot a = Slot("planting", "Oak", "Large", 9f, 7f, "a");
            EntourageSlot b = Slot("planting", "Oak", "Large", 9f, 7f, "b");
            EntourageSlot c = Slot("planting", "Oak", "Large", 9f, 7f, "c", link: "L1");
            var document = new EntourageDocument
            {
                Swaps = new List<EntourageSwap>
                {
                    new() { Scope = EntourageSwaps.TYPE, Key = "Oak : Large", Type = "tree-spreading" },
                    new() { Scope = EntourageSwaps.INSTANCE, Key = "a", Type = "person-man", Height = 2.5f, Flip = true },
                    new() { Scope = EntourageSwaps.INSTANCE, Key = "L1/c", Type = EntourageSwaps.NONE },
                    new() { Scope = EntourageSwaps.INSTANCE, Key = "x", Type = "no-such-type" },
                    new() { Scope = "sideways", Key = "y", Type = "tree-round" }
                }
            }.Clean();
            Assert.AreEqual(3, document.Swaps.Count, "unknown types and scopes dropped");
            EntourageSwaps.Index(document, out var instances, out var types);

            EntourageSwaps.Result ra = EntourageSwaps.Resolve(document, a, instances, types);
            Assert.AreEqual("person-man", ra.Type);
            Assert.AreEqual(2.5f, ra.Height);
            Assert.IsTrue(ra.Flip);
            Assert.AreEqual(EntourageSwaps.INSTANCE, ra.Rule);
            Assert.IsTrue(ra.IsBillboard && ra.HidesElement);

            EntourageSwaps.Result rb = EntourageSwaps.Resolve(document, b, instances, types);
            Assert.AreEqual("tree-spreading", rb.Type);
            Assert.AreEqual(9f, rb.Height, "the Revit element's own height");
            Assert.AreEqual(EntourageSwaps.TYPE, rb.Rule);

            EntourageSwaps.Result rc = EntourageSwaps.Resolve(document, c, instances, types);
            Assert.AreEqual(EntourageSwaps.NONE, rc.Type, "the link's instance key is link/element");
            Assert.IsFalse(rc.IsBillboard);
            Assert.IsTrue(rc.HidesElement);

            EntourageSlot other = Slot("planting", "Gum", "Big", 14f, 10f, "d");
            Assert.AreEqual("tree-round", EntourageSwaps.Resolve(document, other, instances, types).Type, "auto");
            document.SwapRevit = false;
            EntourageSwaps.Result off = EntourageSwaps.Resolve(document, other, instances, types);
            Assert.AreEqual(EntourageSwaps.KEEP, off.Type);
            Assert.IsFalse(off.HidesElement);
            Assert.AreEqual("tree-spreading", EntourageSwaps.Resolve(document, b, instances, types).Type, "rules still apply with automatic off");
            Assert.IsFalse(document.IsEmpty, "automatic off is worth saving");

            // The same mirroring every time for an instance without a rule
            Assert.AreEqual(EntourageSwaps.Resolve(document, b, instances, types).Flip, EntourageSwaps.Resolve(document, b, instances, types).Flip);
            Assert.AreEqual(EntourageSwaps.StableHash("abc"), EntourageSwaps.StableHash("abc"));
        }

        [TestMethod]
        public void Slots_RoundTripInBimGo()
        {
            using var folder = new TempFolder();
            int elements = TestData.BuildScene().Elements.Length;
            BimGoDocument copy = TestData.BuildDocument(entourageSlots: new[]
            {
                new EntourageSlot { UniqueId = "p1", ElementId = 55, Category = "planting", Family = "Oak", Type = "Large", Base = new System.Numerics.Vector3(1.5f, -2f, 0.25f), Height = 9f, Width = 7f, Element = 0 },
                new EntourageSlot { UniqueId = "e1", Link = 1, LinkKey = "link-uid", Category = "entourage", Family = "RPC Male", Type = "Alex", Height = 1.8f, Width = 0.5f, Element = elements + 5 }
            });
            string path = folder.File("slots.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, copy, TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            EntourageSlot[] slots = read.Scene.EntourageSlots;
            Assert.AreEqual(2, slots.Length);
            Assert.AreEqual("Oak : Large", slots[0].FamilyType);
            Assert.AreEqual(0, slots[0].Element);
            Assert.AreEqual(new System.Numerics.Vector3(1.5f, -2f, 0.25f), slots[0].Base);
            Assert.AreEqual("link-uid/e1", slots[1].InstanceKey);
            Assert.AreEqual(-1, slots[1].Element, "an index past the scene's elements is dropped");
            Assert.AreEqual(0, BimGoReader.Read(WriteBare(folder), new LaunchSettings(), out _).Scene.EntourageSlots.Length);
        }

        private static string WriteBare(TempFolder folder)
        {
            string path = folder.File("bare-slots.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(), TestData.Writer, FileKinds.SAVE, out string error), error);
            return path;
        }

        [TestMethod]
        public void ModelFolder_FileAndPictures()
        {
            using var folder = new TempFolder();
            string json = folder.File("entourage.json");
            string images = EntourageFiles.ImagesFolderFor(json);
            Assert.AreEqual(folder.File("entourage-images"), images);

            var document = new EntourageDocument
            {
                Types = new List<EntourageType> { new() { Id = "p", Name = "Person", Image = "p.png" }, new() { Id = "q", Name = "Gone", Image = "q.png" } },
                Items = new List<EntourageItem> { new() { Type = "img:p", X = 1, Y = 1, Z = 0 } }
            };
            Assert.IsTrue(EntourageFiles.Write(json, document, out string error), error);
            Assert.IsTrue(EntourageFiles.WriteImage(images, "p.png", new byte[] { 1, 2, 3 }, out error), error);
            Assert.IsFalse(EntourageFiles.WriteImage(images, "..\\x.png", new byte[] { 1 }, out _), "unsafe names refused");

            EntourageDocument read = EntourageFiles.Read(json, out error);
            Assert.IsNotNull(read, error);
            Dictionary<string, byte[]> pictures = EntourageFiles.ReadImages(images, read);
            Assert.AreEqual(1, pictures.Count, "the missing picture is left out");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, pictures["p.png"]);

            EntourageFiles.DeleteImage(images, "p.png");
            Assert.IsFalse(File.Exists(Path.Combine(images, "p.png")));
            Assert.IsNull(EntourageFiles.Read(folder.File("missing.json"), out error));
            Assert.IsNull(error);
        }
    }
}
