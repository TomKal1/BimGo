using System;
using System.Collections.Generic;
using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BimGo.Tests
{
    /// <summary>Overview sampling must cover the viewport rather than the first rows in the source file.</summary>
    [TestClass]
    public sealed class ScreenLabelLayoutTests
    {
        [TestMethod]
        public void DenseEarlyRegion_DoesNotConsumeOtherRegionsBudget()
        {
            ScreenLabelLayout layout = new(8);
            int[] payloads = new int[layout.SlotCount];
            layout.Begin(400, 200, new Vector2(100, 100));
            for (int i = 0; i < 1000; i++)
            {
                int slot = layout.Offer(new Vector2(5 + i % 20, 5 + i / 20), 10, i);
                if (slot >= 0) { payloads[slot] = i; }
            }
            for (int cell = 0; cell < 8; cell++)
            {
                Vector2 screen = new(50 + (cell % 4) * 100, 50 + (cell / 4) * 100);
                int slot = layout.Offer(screen, 10, 1000 + cell);
                Assert.IsTrue(slot >= 0);
                payloads[slot] = 1000 + cell;
            }
            Assert.AreEqual(8, layout.CellCount);
            for (int cell = 0; cell < 8; cell++) { Assert.AreEqual(1000 + cell, payloads[layout.Take(cell)]); }
        }

        [TestMethod]
        public void RepresentativesAndFallbacks_AreIndependentOfTraversalOrder()
        {
            int[] Sample(bool reverse)
            {
                ScreenLabelLayout layout = new(1);
                int[] payloads = new int[layout.SlotCount];
                layout.Begin(100, 100, new Vector2(100));
                for (int n = 0; n < 30; n++)
                {
                    int id = reverse ? 29 - n : n;
                    // Same pixel: nearest depth wins, then stable point ID breaks ties.
                    int slot = layout.Offer(new Vector2(50), id / 3, id);
                    if (slot >= 0) { payloads[slot] = id; }
                }
                List<int> result = new();
                int next;
                while ((next = layout.Take(0)) >= 0) { result.Add(payloads[next]); }
                return result.ToArray();
            }
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, Sample(false));
            CollectionAssert.AreEqual(Sample(false), Sample(true));
        }

        [TestMethod]
        public void HiddenRepresentative_HasRealPointFallbacksInTheSameCell()
        {
            ScreenLabelLayout layout = new(1);
            int[] payloads = new int[layout.SlotCount];
            layout.Begin(100, 100, new Vector2(100));
            for (int i = 20; i >= 0; i--)
            {
                int slot = layout.Offer(new Vector2(50 + i, 50), 10, i);
                if (slot >= 0) { payloads[slot] = i; }
            }
            Assert.AreEqual(0, payloads[layout.Take(0)], "the point closest to the cell centre is tried first");
            Assert.AreEqual(1, payloads[layout.Take(0)], "a hidden point can be replaced with another actual value");
            for (int i = 2; i < 8; i++) { Assert.AreEqual(i, payloads[layout.Take(0)]); }
            Assert.AreEqual(-1, layout.Take(0), "occlusion work is bounded even in dense cells");
        }

        [TestMethod]
        [DataRow(2024, 1129)]
        [DataRow(800, 600)]
        [DataRow(7680, 4320)]
        [DataRow(10000, 20)]
        [DataRow(20, 10000)]
        public void ViewportGrid_RespectsBudgetAndCoversEveryRegion(int width, int height)
        {
            ScreenLabelLayout layout = new(160);
            layout.Begin(width, height, new Vector2(96, 64));
            Assert.IsTrue(layout.CellCount > 0 && layout.CellCount <= 160);
            for (int y = 0; y < height; y += 4)
            for (int x = 0; x < width; x += 4) { layout.Offer(new Vector2(x, y), 10, y * width + x); }
            for (int cell = 0; cell < layout.CellCount; cell++)
            {
                Assert.IsTrue(layout.Take(cell) >= 0, "each region receives a candidate, including the far edges");
            }
        }

        [TestMethod]
        public void NewFrame_DropsOldPointsAndRejectsOutsideOrInvalidCandidates()
        {
            ScreenLabelLayout layout = new(1);
            layout.Begin(100, 100, new Vector2(100));
            Assert.IsTrue(layout.Offer(new Vector2(50), 10, 0) >= 0);
            layout.Begin(100, 100, new Vector2(100));
            Assert.AreEqual(-1, layout.Take(0));
            Assert.AreEqual(-1, layout.Offer(new Vector2(-1, 50), 10, 0));
            Assert.AreEqual(-1, layout.Offer(new Vector2(100, 50), 10, 0));
            Assert.AreEqual(-1, layout.Offer(new Vector2(50, 100), 10, 0));
            Assert.AreEqual(-1, layout.Offer(new Vector2(float.NaN, 50), 10, 0));
            Assert.AreEqual(-1, layout.Offer(new Vector2(50), -10, 0));
            Assert.AreEqual(-1, layout.Offer(new Vector2(50), float.PositiveInfinity, 0));
            layout.Begin(0, 100, new Vector2(100));
            Assert.AreEqual(0, layout.CellCount);
            Assert.AreEqual(-1, layout.Offer(new Vector2(50), 10, 0));
        }

        [TestMethod]
        public void RepeatedFrames_ReuseStorageWithoutAllocating()
        {
            ScreenLabelLayout layout = new(160);
            void Frame()
            {
                layout.Begin(1920, 1080, new Vector2(96, 64));
                for (int i = 0; i < 1000; i++) { layout.Offer(new Vector2(i % 1900, i % 1000), 10, i); }
                for (int cell = 0; cell < layout.CellCount; cell++) { while (layout.Take(cell) >= 0) { } }
            }
            Frame();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 20; frame++) { Frame(); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
        }
    }
}
