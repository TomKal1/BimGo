using System;
using System.Collections.Generic;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>Turns an area's rows, columns, spacing and offset into fixture spots.</summary>
    public static class LayoutGenerator
    {
        private const double CollisionDistance = 0.01;

        /// <summary>
        /// The spots of <paramref name="area"/> that are inside the room, inside the area, and not
        /// claimed by a higher-priority area in <paramref name="allAreas"/>. Unlocked spacing is
        /// written back to the area.
        /// </summary>
        public static List<LayoutSpot> Spots(LayoutArea area, ZoneFootprint room, IReadOnlyList<LayoutArea> allAreas)
        {
            var spots = new List<LayoutSpot>();
            if (area.Loops.Count == 0) return spots;

            (double minU, double minV, double maxU, double maxV) = FrameBounds(area, room);
            double[] us = Positions(minU, maxU, area.Columns, area.LockSpacingX, area.SpacingX, out double sx);
            double[] vs = Positions(minV, maxV, area.Rows, area.LockSpacingY, area.SpacingY, out double sy);
            if (!area.LockSpacingX) area.SpacingX = sx;
            if (!area.LockSpacingY) area.SpacingY = sy;

            double cos = Math.Cos(area.FrameAngle), sin = Math.Sin(area.FrameAngle);
            bool snap = area.Snap && area.Grid != null;
            int spanX = 1, spanY = 1;
            if (snap)
            {
                double alongX = area.Rotate90 ? area.FixtureWidth : area.FixtureLength;
                double alongY = area.Rotate90 ? area.FixtureLength : area.FixtureWidth;
                spanX = CeilingGrid.Span(alongX, area.Grid!.TileX);
                spanY = CeilingGrid.Span(alongY, area.Grid.TileY);
            }

            for (int row = 0; row < vs.Length; row++)
            {
                for (int col = 0; col < us.Length; col++)
                {
                    double u = us[col] + area.OffsetX;
                    double v = vs[row] + area.OffsetY;
                    double x = u * cos - v * sin;
                    double y = u * sin + v * cos;
                    if (snap) (x, y) = area.Grid!.Snap(x, y, spanX, spanY);

                    if (!room.ContainsPlan(x, y) || !Polygon.Contains(area.Loops, x, y)) continue;
                    if (ClaimedByOther(area, allAreas, x, y)) continue;
                    if (spots.Exists(s => (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) < CollisionDistance * CollisionDistance)) continue;

                    int index = row * us.Length + col;
                    spots.Add(new LayoutSpot { Index = index, X = x, Y = y, On = !area.Off.Contains(index) });
                }
            }
            return spots;
        }

        private static bool ClaimedByOther(LayoutArea area, IReadOnlyList<LayoutArea> allAreas, double x, double y)
        {
            foreach (LayoutArea other in allAreas)
            {
                if (ReferenceEquals(other, area) || other.Priority <= area.Priority) continue;
                if (Polygon.Contains(other.Loops, x, y)) return true;
            }
            return false;
        }

        private static double[] Positions(double min, double max, int count, bool locked, double lockedSpacing, out double spacing)
        {
            var result = new double[count];
            if (locked && lockedSpacing > 0)
            {
                spacing = lockedSpacing;
                double start = (min + max) / 2 - (count - 1) * spacing / 2;
                for (int i = 0; i < count; i++) result[i] = start + i * spacing;
            }
            else
            {
                spacing = (max - min) / count;
                for (int i = 0; i < count; i++) result[i] = min + spacing / 2 + i * spacing;
            }
            return result;
        }

        private static (double MinU, double MinV, double MaxU, double MaxV) Bounds(IEnumerable<List<Vec3>> loops, double angle)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
            foreach (List<Vec3> loop in loops)
            {
                foreach (Vec3 p in loop)
                {
                    double u = p.X * cos + p.Y * sin;
                    double v = -p.X * sin + p.Y * cos;
                    minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                    minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
                }
            }
            return (minU, minV, maxU, maxV);
        }

        /// <summary>
        /// The area's bounding box in its frame, cut to the room's: a ceiling that runs past the
        /// room (a corridor, an open office) still lays its array out over the room.
        /// </summary>
        private static (double MinU, double MinV, double MaxU, double MaxV) FrameBounds(LayoutArea area, ZoneFootprint room)
        {
            (double minU, double minV, double maxU, double maxV) = Bounds(area.Loops, area.FrameAngle);
            if (room.Loops.Count == 0) return (minU, minV, maxU, maxV);
            (double rMinU, double rMinV, double rMaxU, double rMaxV) = Bounds(room.Loops, area.FrameAngle);
            double u0 = Math.Max(minU, rMinU), v0 = Math.Max(minV, rMinV), u1 = Math.Min(maxU, rMaxU), v1 = Math.Min(maxV, rMaxV);
            return u1 > u0 && v1 > v0 ? (u0, v0, u1, v1) : (minU, minV, maxU, maxV);
        }

        /// <summary>The area's bounding box in its own frame, cut to the room: width along X, height along Y (ft).</summary>
        public static (double Width, double Height) FrameSize(LayoutArea area, ZoneFootprint room)
        {
            if (area.Loops.Count == 0) return (0, 0);
            (double minU, double minV, double maxU, double maxV) = FrameBounds(area, room);
            return (maxU - minU, maxV - minV);
        }

        /// <summary>
        /// The floor area (ft²) the area lays fixtures over: inside the room, inside the area, and not
        /// claimed by a higher-priority area. Sampled on a 0.5 ft grid, so separate ceilings merged
        /// into one area add up and a ceiling larger than the room counts only the room.
        /// </summary>
        public static double ClaimedArea(LayoutArea area, ZoneFootprint room, IReadOnlyList<LayoutArea> allAreas)
        {
            const double step = 0.5;
            if (area.Loops.Count == 0) return 0;
            (double minU, double minV, double maxU, double maxV) = FrameBounds(area, room);
            double cos = Math.Cos(area.FrameAngle), sin = Math.Sin(area.FrameAngle);
            int cells = 0;
            for (double u = minU + step / 2; u < maxU; u += step)
            {
                for (double v = minV + step / 2; v < maxV; v += step)
                {
                    double x = u * cos - v * sin, y = u * sin + v * cos;
                    if (room.ContainsPlan(x, y) && Polygon.Contains(area.Loops, x, y) && !ClaimedByOther(area, allAreas, x, y))
                        cells++;
                }
            }
            return cells * step * step;
        }

        /// <summary>Rows and columns for about <paramref name="count"/> fixtures with near-square cells.</summary>
        public static (int Rows, int Columns) Arrange(int count, double width, double height)
        {
            count = Math.Max(1, count);
            double aspect = height > 1e-9 ? width / height : 1;
            int columns = Math.Max(1, (int)Math.Round(Math.Sqrt(count * aspect), MidpointRounding.AwayFromZero));
            int rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
            return (rows, columns);
        }

        /// <summary>The angle of the room's longest boundary edge, folded into [0, π/2).</summary>
        public static double LongestEdgeAngle(ZoneFootprint room)
        {
            if (room.Loops.Count == 0) return 0;
            List<Vec3> loop = room.Loops[0];
            double best = -1, angle = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                Vec3 a = loop[i], b = loop[(i + 1) % loop.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double length = dx * dx + dy * dy;
                if (length <= best + 1e-9) continue;
                best = length;
                angle = Math.Atan2(dy, dx);
            }
            double quarter = Math.PI / 2;
            angle %= quarter;
            if (angle < 0) angle += quarter;
            return quarter - angle < 1e-9 ? 0 : angle;
        }
    }
}
