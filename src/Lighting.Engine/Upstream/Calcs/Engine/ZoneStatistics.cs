using System;
using System.Collections.Generic;
using System.Linq;

namespace WWTools.Calcs.Engine
{
    /// <summary>Average, maximum, minimum and uniformity ratios of a set of point values.</summary>
    public sealed class ZoneStatistics
    {
        public int Count { get; set; }
        public double Average { get; set; }
        public double Maximum { get; set; }
        public double Minimum { get; set; }

        /// <summary>Average ÷ minimum, or +∞ when the minimum is zero.</summary>
        public double AverageToMinimum => Minimum > 1e-9 ? Average / Minimum : double.PositiveInfinity;

        /// <summary>Maximum ÷ minimum, or +∞ when the minimum is zero.</summary>
        public double MaximumToMinimum => Minimum > 1e-9 ? Maximum / Minimum : double.PositiveInfinity;

        public static ZoneStatistics From(IEnumerable<double> values)
        {
            var list = values.Where(v => !double.IsNaN(v)).ToList();
            if (list.Count == 0) return new ZoneStatistics();
            return new ZoneStatistics
            {
                Count = list.Count,
                Average = list.Average(),
                Maximum = list.Max(),
                Minimum = list.Min(),
            };
        }
    }
}
