using System;
using System.Collections.Generic;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round X-A: sunrise / sunset, the time-lapse plan (frames → sun times, overnight ranges), the fixed-view timeline,
    /// batch stills' time list, shot names and CSV index, and the new per-machine settings.
    /// </summary>
    [TestClass]
    public sealed class TimeLapseTests
    {
        private static readonly GeoLocation SYDNEY = new(-33.8688, 151.2093, 10.0, "Sydney");
        private static readonly GeoLocation TROMSO = new(69.65, 18.96, 1.0, "Tromsø");

        [TestMethod]
        public void SunEvents_SydneyWinterAndSummer()
        {
            // 21 June: about 07:00 and 16:54 (standard time)
            Assert.IsTrue(SunEvents.Find(SYDNEY, 2026, 6, 21, false, out double rise, out double set));
            Assert.IsTrue(rise > 6 * 60 + 50 && rise < 7 * 60 + 10, $"sunrise {rise:0.0}");
            Assert.IsTrue(set > 16 * 60 + 45 && set < 17 * 60 + 5, $"sunset {set:0.0}");
            Assert.AreEqual(SunEvents.HORIZON_DEGREES, SunEvents.Altitude(SYDNEY, 2026, 6, 21, rise, false), 0.01);
            Assert.AreEqual(SunEvents.HORIZON_DEGREES, SunEvents.Altitude(SYDNEY, 2026, 6, 21, set, false), 0.01);

            // 21 December on daylight saving: about 05:41 and 20:05
            Assert.IsTrue(SunEvents.Find(SYDNEY, 2026, 12, 21, true, out rise, out set));
            Assert.IsTrue(rise > 5 * 60 + 30 && rise < 5 * 60 + 55, $"sunrise {rise:0.0}");
            Assert.IsTrue(set > 19 * 60 + 55 && set < 20 * 60 + 15, $"sunset {set:0.0}");
        }

        [TestMethod]
        public void SunEvents_PolarDayAndNight_NoRange()
        {
            Assert.IsFalse(SunEvents.Find(TROMSO, 2026, 6, 21, false, out double rise, out double set));
            Assert.IsTrue(double.IsNaN(rise) && double.IsNaN(set));
            Assert.IsFalse(SunEvents.Find(TROMSO, 2026, 12, 21, false, out _, out _));

            Assert.IsNull(TimeLapsePlan.Create(TROMSO, 2026, new SunTime { Month = 6, Day = 21 }, false, 0, 0, 30, 20, 30, out string summer));
            StringAssert.Contains(summer, "doesn't set");
            Assert.IsNull(TimeLapsePlan.Create(TROMSO, 2026, new SunTime { Month = 12, Day = 21 }, false, 0, 0, 30, 20, 30, out string winter));
            StringAssert.Contains(winter, "doesn't rise");

            // Custom works anywhere
            Assert.IsNotNull(TimeLapsePlan.Create(TROMSO, 2026, new SunTime { Month = 6, Day = 21 }, true, 0, 0, 30, 20, 30, out _));
        }

        [TestMethod]
        public void Plan_SunriseToSunset_WithMargin_FramesSpreadEvenly()
        {
            TimeLapsePlan plan = TimeLapsePlan.Create(SYDNEY, 2026, new SunTime { Month = 6, Day = 21, Minutes = 600 }, false, 0, 0, 30, 20, 30, out string problem);
            Assert.IsNull(problem);
            Assert.IsTrue(plan.FromSun);
            Assert.AreEqual(600, plan.Frames);
            Assert.AreEqual(20f, plan.Seconds, 1e-4f);
            Assert.AreEqual(plan.Sunrise - 30.0, plan.StartMinutes, 1e-9);
            Assert.AreEqual(plan.Sunset + 30.0, plan.EndMinutes, 1e-9);
            Assert.AreEqual(plan.StartMinutes, plan.MinutesAt(0), 1e-9);
            Assert.AreEqual(plan.EndMinutes, plan.MinutesAt(plan.Frames - 1), 1e-9);
            Assert.AreEqual(plan.EndMinutes, plan.MinutesAt(plan.Frames + 50), 1e-9, "clamped past the end");

            double step = plan.MinutesAt(1) - plan.MinutesAt(0);
            for (int i = 2; i < plan.Frames; i++) { Assert.AreEqual(step, plan.MinutesAt(i) - plan.MinutesAt(i - 1), 1e-6); }
            Assert.AreEqual((plan.EndMinutes - plan.StartMinutes) / 20.0, plan.MinutesPerSecond, 1e-6);

            // A big margin is clamped to the day
            TimeLapsePlan wide = TimeLapsePlan.Create(SYDNEY, 2026, new SunTime { Month = 12, Day = 21, DaylightSaving = true }, false, 0, 0, 500, 1000, 60, out _);
            Assert.IsTrue(wide.StartMinutes >= 0.0 && wide.EndMinutes <= 1439.99);
            Assert.AreEqual(TimeLapsePlan.MAX_SECONDS * 60, wide.Frames, "length clamped to 120 s");
        }

        [TestMethod]
        public void Plan_CustomOvernight_RunsIntoTheNextDay()
        {
            var into = new SunTime();
            TimeLapsePlan plan = TimeLapsePlan.Create(SYDNEY, 2026, new SunTime { Month = 12, Day = 31, DaylightSaving = true }, true, 20 * 60, 6 * 60, 0, 10, 30, out _);
            Assert.AreEqual(20 * 60, plan.StartMinutes, 1e-9);
            Assert.AreEqual(30 * 60, plan.EndMinutes, 1e-9);

            double first = plan.At(0, into);
            Assert.AreEqual(12, into.Month);
            Assert.AreEqual(31, into.Day);
            Assert.AreEqual(20 * 60, first, 1e-9);
            Assert.IsTrue(into.DaylightSaving);

            double last = plan.At(plan.Frames - 1, into);
            Assert.AreEqual(1, into.Month, "New Year's Day");
            Assert.AreEqual(1, into.Day);
            Assert.AreEqual(6 * 60, last, 1e-9);
            Assert.AreEqual(360, into.Minutes);

            // Fractional minutes carry through (the sun moves smoothly between whole minutes)
            double middle = plan.At(1, into);
            Assert.IsTrue(middle > 20 * 60 + 1 && middle < 20 * 60 + 3, $"{middle}");
        }

        [TestMethod]
        public void FixedViewTimeline_HoldsOneViewWithoutFades()
        {
            var stop = new TourStop(new System.Numerics.Vector3(1f, 2f, 3f), 1.6f, 0.5f, -0.1f, 99f, 2f);
            var timeline = new FixedViewTimeline(stop, 12f);
            Assert.AreEqual(1, timeline.Count);
            Assert.AreEqual(12f, timeline.Duration);
            foreach (float t in new[] { 0f, 6f, 11.99f })
            {
                TourPose pose = timeline.Pose(t);
                Assert.AreEqual(0, pose.Stop);
                Assert.IsTrue(System.Numerics.Vector3.Distance(new System.Numerics.Vector3(1f, 2f, 4.6f), pose.Eye) < 1e-5f);
                Assert.AreEqual(0.5f, pose.Yaw);
                Assert.AreEqual(-0.1f, pose.Pitch);
                Assert.AreEqual(0f, pose.Black);
                Assert.AreEqual(pose.BlendFrom, pose.BlendTo);
                Assert.IsFalse(pose.Finished);
            }
            Assert.IsTrue(timeline.Pose(12f).Finished);
        }

        [TestMethod]
        public void BatchTimes_ParseTimesDatesAndErrors()
        {
            List<BatchTime> times = BatchStills.Parse("9:00, 12:00; 15:30 3pm 0930 9:00", 2026, out string error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[]
            {
                new BatchTime(0, 0, 540), new BatchTime(0, 0, 720), new BatchTime(0, 0, 930), new BatchTime(0, 0, 900), new BatchTime(0, 0, 570)
            }, times, "the repeated 9:00 dropped");

            times = BatchStills.Parse("12 21/6 9:00 12:00 21/12/2026 9:00 12am 12pm", 2026, out error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[]
            {
                new BatchTime(0, 0, 720), new BatchTime(6, 21, 540), new BatchTime(6, 21, 720),
                new BatchTime(12, 21, 540), new BatchTime(12, 21, 0), new BatchTime(12, 21, 720)
            }, times);
            Assert.AreEqual("12:00 21/6 9:00 12:00 21/12 9:00 0:00 12:00", BatchStills.Format(times));
            CollectionAssert.AreEqual(times, BatchStills.Parse(BatchStills.Format(times), 2026, out _), "Format reads back");

            foreach (string bad in new[] { "25:00", "9:7", "noon", "13pm", "31/2 9:00", "21/6", "", "   " })
            {
                Assert.AreEqual(0, BatchStills.Parse(bad, 2026, out error).Count, bad);
                Assert.IsNotNull(error, bad);
            }
            Assert.AreEqual(1, BatchStills.Parse("29/2 9:00", 2028, out _).Count, "29 Feb in a leap year");
            Assert.AreEqual(0, BatchStills.Parse("29/2 9:00", 2026, out _).Count, "no 29 Feb in 2026");
        }

        [TestMethod]
        public void BatchStills_NamesAndCsvIndex()
        {
            Assert.AreEqual("03 Entry 21 Jun 0930", BatchStills.ShotName(3, "Entry", 6, 21, 570));
            Assert.AreEqual("12 Roof 1 Dec 0000", BatchStills.ShotName(12, "Roof", 12, 1, 0));

            string row = BatchStills.CsvRow("01 Entry, north 21 Jun 0900.png", "Entry, north", 6, 21, 540, false, 23.456, -10.0);
            Assert.AreEqual("\"01 Entry, north 21 Jun 0900.png\",\"Entry, north\",21 Jun,09:00,no,23.5,350.0", row);
            string csv = BatchStills.Csv(new[] { row });
            Assert.IsTrue(csv.StartsWith(BatchStills.CSV_HEADER + "\r\n", StringComparison.Ordinal));
            Assert.IsTrue(csv.EndsWith(row + "\r\n", StringComparison.Ordinal));
            Assert.AreEqual("\"say \"\"hi\"\"\",x,1 Jan,00:00,yes,0.0,0.0", BatchStills.CsvRow("say \"hi\"", "x", 1, 1, 0, true, 0, 0));
        }

        [TestMethod]
        public void Settings_DefaultsCleanAndOlderFiles()
        {
            var settings = new LaunchSettings();
            Assert.IsFalse(settings.TimeLapseCustom);
            Assert.AreEqual(30, settings.TimeLapseMargin);
            Assert.AreEqual(20, settings.TimeLapseSeconds);
            Assert.IsFalse(settings.TimeLapseStamp);
            Assert.AreEqual(BatchStills.DEFAULT_TIMES, settings.BatchTimes);
            Assert.IsFalse(settings.BatchTourStops);
            Assert.IsFalse(settings.BatchStamp);

            settings = new LaunchSettings { TimeLapseFrom = -5, TimeLapseTo = 5000, TimeLapseMargin = 999, TimeLapseSeconds = 1, BatchTimes = "  " };
            settings.Sanitise();
            Assert.AreEqual(0, settings.TimeLapseFrom);
            Assert.AreEqual(1439, settings.TimeLapseTo);
            Assert.AreEqual(TimeLapsePlan.MAX_MARGIN, settings.TimeLapseMargin);
            Assert.AreEqual(TimeLapsePlan.MIN_SECONDS, settings.TimeLapseSeconds);
            Assert.AreEqual(BatchStills.DEFAULT_TIMES, settings.BatchTimes);

            LaunchSettings older = System.Text.Json.JsonSerializer.Deserialize<LaunchSettings>("{\"Msaa\":2}");
            Assert.AreEqual(20, older.TimeLapseSeconds);
            Assert.AreEqual(BatchStills.DEFAULT_TIMES, older.BatchTimes);
        }
    }
}
