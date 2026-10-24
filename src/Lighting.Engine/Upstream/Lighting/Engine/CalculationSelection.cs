using System;
using System.Collections.Generic;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>Host-independent selection policies. Points remain on their original workplane.</summary>
    public static class CalculationSelection
    {
        public const double PointTolerance = 0.001; // feet; stable across save/load and display conversions

        /// <summary>Replaces only recalculated zones; missing cached results require a complete run.</summary>
        public static List<CalcZone> MergeZones(IEnumerable<string> allKeys, IEnumerable<CalcZone> previous, IEnumerable<CalcZone> updated)
        {
            var old = previous.ToDictionary(z => z.Key, StringComparer.Ordinal);
            var replacements = updated.ToDictionary(z => z.Key, StringComparer.Ordinal);
            return allKeys.Select(key => replacements.TryGetValue(key, out var next) ? next
                : old.TryGetValue(key, out var cached) ? cached
                : throw new InvalidOperationException("A zone has no previous result; run the complete set first.")).ToList();
        }

        public static Vec3 SourcePosition(Vec3 origin, Vec3 min, Vec3 max, string mode, double offset)
        {
            Vec3 position = origin;
            if (mode == "BoundingBoxBottom") position = new Vec3((min.X + max.X) / 2, (min.Y + max.Y) / 2, min.Z - 1.0 / 12);
            else if (mode == "BoundingBoxCenter") position = (min + max) * .5;
            // Auto used to guess from the whole fixture box, including stems and symbolic geometry.
            // It now preserves the family insertion/light-source origin; explicit box choices remain available.
            return position + new Vec3(0, 0, offset);
        }

        /// <summary>Removes manual exclusions and points at/under actual furniture triangles, retaining all other heights.</summary>
        public static int ExcludePoints(CalcZone zone, IReadOnlyList<SceneTriangle> geometry,
            bool underFurniture, IEnumerable<Vec3> manual) =>
            ExcludePoints(zone, underFurniture ? FurnitureScene(geometry) : null, manual);

        /// <summary>
        /// The furniture of the geometry, ready for <see cref="ExcludePoints(CalcZone, TriangleScene?, IEnumerable{Vec3})"/>.
        /// Build it once per run: over a whole building it takes seconds (3.3 s a zone on Snowdon).
        /// </summary>
        public static TriangleScene FurnitureScene(IReadOnlyList<SceneTriangle> geometry) =>
            new TriangleScene(geometry.Where(t => t.Class == SurfaceClass.Furniture).ToList());

        /// <summary>As above, with the furniture scene already built; null leaves points under furniture in.</summary>
        public static int ExcludePoints(CalcZone zone, TriangleScene? furniture, IEnumerable<Vec3> manual)
        {
            var excluded = (manual ?? Enumerable.Empty<Vec3>()).ToArray();
            var keep = new List<int>();
            for (int i = 0; i < zone.Points.Count; i++)
            {
                Vec3 p = zone.Points[i];
                if (excluded.Any(e => (p - e).Length <= PointTolerance)) continue;
                // Check upward from just below the plane, including points on a thin table top.
                // A legitimate shadow on an unobstructed workplane is retained, even when its value is zero.
                if (furniture != null && furniture.ClosestHit(p - Vec3.UnitZ * .01, Vec3.UnitZ,
                    Math.Max(.02, zone.TopZ - p.Z + .02), out _, out _) >= 0) continue;
                keep.Add(i);
            }
            int removed = zone.Points.Count - keep.Count;
            if (removed == 0) return 0;
            var points = keep.Select(i => zone.Points[i]).ToArray();
            zone.PointLevel = keep.Select(i => i < zone.PointLevel.Length ? zone.PointLevel[i] : 0).ToArray();
            zone.PointPart = keep.Select(i => i < zone.PointPart.Length ? zone.PointPart[i] : -1).ToArray();
            zone.Points.Clear(); zone.Points.AddRange(points);
            return removed;
        }
    }
}
