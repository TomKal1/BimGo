using System;
using System.Collections.Generic;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>Where a ceiling grid's origin came from.</summary>
    public enum GridOriginSource
    {
        FromModel,
        Centred,
        Picked,
    }

    /// <summary>
    /// A lay-in ceiling grid in plan: tile size, angle and one grid-line intersection. Fixtures
    /// snap to tile centres or grid lines depending on how many tiles they span.
    /// </summary>
    public sealed class CeilingGrid
    {
        /// <summary>Tile size along the grid's own X (ft).</summary>
        public double TileX { get; set; }

        /// <summary>Tile size along the grid's own Y (ft).</summary>
        public double TileY { get; set; }

        /// <summary>Angle of the grid's X from project X (radians).</summary>
        public double Angle { get; set; }

        /// <summary>A grid-line intersection in project plan coordinates.</summary>
        public double OriginX { get; set; }

        public double OriginY { get; set; }

        public GridOriginSource OriginSource { get; set; }

        /// <summary>The number of tiles a fixture of this size covers along one axis (at least 1).</summary>
        public static int Span(double fixtureSize, double tileSize) =>
            tileSize > 1e-9 ? Math.Max(1, (int)Math.Round(fixtureSize / tileSize, MidpointRounding.AwayFromZero)) : 1;

        /// <summary>
        /// The nearest position for a fixture spanning <paramref name="spanX"/> × <paramref name="spanY"/>
        /// tiles: an odd span centres in a tile, an even span centres on a grid line.
        /// </summary>
        public (double X, double Y) Snap(double x, double y, int spanX, int spanY)
        {
            double cos = Math.Cos(Angle), sin = Math.Sin(Angle);
            double dx = x - OriginX, dy = y - OriginY;
            double u = dx * cos + dy * sin;
            double v = -dx * sin + dy * cos;
            u = SnapAxis(u, TileX, spanX);
            v = SnapAxis(v, TileY, spanY);
            return (OriginX + u * cos - v * sin, OriginY + u * sin + v * cos);
        }

        private static double SnapAxis(double value, double tile, int span)
        {
            if (tile <= 1e-9) return value;
            return span % 2 == 1
                ? (Math.Floor(value / tile) + 0.5) * tile
                : Math.Round(value / tile, MidpointRounding.AwayFromZero) * tile;
        }

        /// <summary>
        /// A grid centred on an outline the way architects lay one out: per axis, a grid line or a
        /// tile centre on the outline's centre, whichever leaves the larger border tiles.
        /// </summary>
        public static CeilingGrid Centred(double tileX, double tileY, double angle, IReadOnlyList<Vec3> outline)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (Vec3 p in outline)
            {
                double u = p.X * cos + p.Y * sin;
                double v = -p.X * sin + p.Y * cos;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            double ou = CentredAxis(minU, maxU, tileX);
            double ov = CentredAxis(minV, maxV, tileY);
            return new CeilingGrid
            {
                TileX = tileX,
                TileY = tileY,
                Angle = angle,
                OriginX = ou * cos - ov * sin,
                OriginY = ou * sin + ov * cos,
                OriginSource = GridOriginSource.Centred,
            };
        }

        private static double CentredAxis(double min, double max, double tile)
        {
            double centre = (min + max) / 2;
            if (tile <= 1e-9) return centre;
            double half = (max - min) / 2;
            double lineBorder = Border(half, tile);
            double tileBorder = Border(half + tile / 2, tile);
            return lineBorder >= tileBorder ? centre : centre - tile / 2;
        }

        // The width of the cut tile at the edge; a remainder of 0 is a whole tile.
        private static double Border(double reach, double tile)
        {
            double r = reach % tile;
            return r < 1e-9 || tile - r < 1e-9 ? tile : r;
        }
    }
}
