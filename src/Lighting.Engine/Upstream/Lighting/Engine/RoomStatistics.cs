using System;
using System.Collections.Generic;
using System.Linq;

namespace WWTools.Lighting.Engine
{
    /// <summary>Area-weighted light on one class of a room's surfaces (covered patches left out).</summary>
    public sealed class SurfaceSummary
    {
        public SurfaceClass Class { get; set; }

        /// <summary>"Ceiling", "Walls", "Floor" or "Furniture".</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>ft².</summary>
        public double Area { get; set; }

        /// <summary>Area-weighted reflectance 0–1.</summary>
        public double Reflectance { get; set; }

        /// <summary>Area-weighted average illuminance (fc).</summary>
        public double Illuminance { get; set; }

        public double MinimumIlluminance { get; set; }

        public double MaximumIlluminance { get; set; }

        /// <summary>Area-weighted average luminance (cd/m²).</summary>
        public double LuminanceCdM2 { get; set; }
    }

    /// <summary>Figures for a room's report page beyond Avg / Max / Min: coverage, the reflected share and its surfaces.</summary>
    public static class RoomStatistics
    {
        /// <summary>cd/ft² to cd/m².</summary>
        public const double SquareFeetPerSquareMetre = 10.763910416709722;

        /// <summary>Share (0–1) of the zone's points (left-out points excluded) at or above <paramref name="thresholdFc"/>.</summary>
        public static double Coverage(CalcZone zone, double thresholdFc)
        {
            int total = 0, over = 0;
            foreach (double v in zone.Illuminance)
            {
                if (double.IsNaN(v)) continue;
                total++;
                if (v >= thresholdFc - 1e-9) over++;
            }
            return total == 0 ? 0 : (double)over / total;
        }

        /// <summary>Share (0–1) of the average illuminance that arrives reflected off the room's surfaces rather than direct.</summary>
        public static double ReflectedShare(CalcZone zone)
        {
            double total = zone.Statistics.Average;
            if (total <= 1e-9) return 0;
            return IesPhotometry.Clamp((total - zone.DirectStatistics.Average) / total, 0, 1);
        }

        /// <summary>The ceiling, walls, floor and furniture of the zone, those it has, in that order.</summary>
        public static List<SurfaceSummary> Surfaces(CalcZone zone)
        {
            var result = new List<SurfaceSummary>();
            foreach ((SurfaceClass cls, string label) in new[]
            {
                (SurfaceClass.Ceiling, "Ceiling"), (SurfaceClass.Wall, "Walls"),
                (SurfaceClass.Floor, "Floor"), (SurfaceClass.Furniture, "Furniture"),
            })
            {
                var patches = zone.Surfaces.Where(p => p.Class == cls && !p.Covered && p.Area > 1e-9).ToList();
                double area = patches.Sum(p => p.Area);
                if (area < 1e-6) continue;
                result.Add(new SurfaceSummary
                {
                    Class = cls,
                    Label = label,
                    Area = area,
                    Reflectance = patches.Sum(p => p.Reflectance * p.Area) / area,
                    Illuminance = patches.Sum(p => p.Illuminance * p.Area) / area,
                    MinimumIlluminance = patches.Min(p => p.Illuminance),
                    MaximumIlluminance = patches.Max(p => p.Illuminance),
                    LuminanceCdM2 = patches.Sum(p => p.Exitance * p.Area) / area / Math.PI * SquareFeetPerSquareMetre,
                });
            }
            return result;
        }

        /// <summary>
        /// The coverage curve: for each of <paramref name="steps"/> + 1 thresholds from 0 to
        /// <paramref name="maxFc"/>, the share of points at or above it.
        /// </summary>
        public static double[] CoverageCurve(CalcZone zone, double maxFc, int steps = 50)
        {
            double[] sorted = zone.Illuminance.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
            var curve = new double[steps + 1];
            if (sorted.Length == 0 || maxFc <= 0) return curve;
            int below = 0;
            for (int k = 0; k <= steps; k++)
            {
                double threshold = maxFc * k / steps;
                while (below < sorted.Length && sorted[below] < threshold - 1e-9) below++;
                curve[k] = (double)(sorted.Length - below) / sorted.Length;
            }
            return curve;
        }
    }
}
