using System;
using System.Collections.Generic;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// Thins point values on a plan so they don't print over each other: from the view scale and
    /// text size, how many grid columns and rows one value needs, and which points to label.
    /// </summary>
    public static class PlanLabels
    {
        /// <summary>Average digit width as a share of the text height (Arial-like fonts).</summary>
        public const double CharWidth = 0.62;

        /// <summary>
        /// Grid steps between labelled points across (x) and down (y).
        /// </summary>
        /// <param name="spacing">Grid spacing in model feet.</param>
        /// <param name="viewScale">Revit view scale (96 for 1/8" = 1'-0").</param>
        /// <param name="textHeight">Text height on paper, in feet.</param>
        /// <param name="maxChars">Characters in the longest value, e.g. 4 for "12.5".</param>
        public static (int X, int Y) Stride(double spacing, int viewScale, double textHeight, int maxChars)
        {
            if (spacing <= 0 || viewScale <= 0 || textHeight <= 0) return (1, 1);
            double h = textHeight * viewScale;                 // model feet
            double width = (maxChars * CharWidth + 0.8) * h;   // value plus a gap
            double height = 1.5 * h;
            int sx = Math.Max(1, (int)Math.Ceiling(width / spacing - 1e-9));
            int sy = Math.Max(1, (int)Math.Ceiling(height / spacing - 1e-9));
            return (sx, sy);
        }

        /// <summary>A printed size in inches as a fraction to the nearest 1/64", e.g. 0.0625 → 1/16".</summary>
        public static string InchLabel(double inches)
        {
            int n = Math.Max(1, (int)Math.Round(inches * 64));
            int d = 64;
            while (n % 2 == 0 && d > 1)
            {
                n /= 2;
                d /= 2;
            }
            return d == 1 ? $"{n}\"" : $"{n}/{d}\"";
        }

        /// <summary>Indexes of the points to label: every sx-th column and sy-th row of the zone's grid.</summary>
        public static List<int> Select(IReadOnlyList<Vec3> points, double spacing, (int X, int Y) stride)
        {
            var chosen = new List<int>();
            if (points.Count == 0 || spacing <= 0) return chosen;
            if (stride.X <= 1 && stride.Y <= 1) return Enumerable.Range(0, points.Count).ToList();
            double x0 = points.Min(p => p.X);
            double y0 = points.Min(p => p.Y);
            for (int i = 0; i < points.Count; i++)
            {
                int ix = (int)Math.Round((points[i].X - x0) / spacing);
                int iy = (int)Math.Round((points[i].Y - y0) / spacing);
                if (ix % stride.X == 0 && iy % stride.Y == 0) chosen.Add(i);
            }
            return chosen;
        }
    }
}
