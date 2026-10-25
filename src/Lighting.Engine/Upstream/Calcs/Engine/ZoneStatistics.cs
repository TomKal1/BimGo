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

        /// <summary>
        /// Smallest minimum and average a ratio is given for: the report's display resolution
        /// (0.05 fc; 0.5 lx is just under it). Below it the value prints as zero and a ratio
        /// over it means nothing (a 0.0 minimum gave 585,522:1).
        /// </summary>
        public const double RatioFloor = 0.05;

        /// <summary>Average ÷ minimum, or +∞ (printed N/A) when the minimum or average reads zero.</summary>
        public double AverageToMinimum => HasRatios ? Average / Minimum : double.PositiveInfinity;

        /// <summary>Maximum ÷ minimum, or +∞ (printed N/A) when the minimum or average reads zero.</summary>
        public double MaximumToMinimum => HasRatios ? Maximum / Minimum : double.PositiveInfinity;

        private bool HasRatios => Minimum >= RatioFloor && Average >= RatioFloor;

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
