using System;
using System.Collections.Generic;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>Zones solved together: they share one interreflection solve. Boxes[k] is Zones[k]'s region.</summary>
    public sealed class ZoneCluster
    {
        public List<CalcZone> Zones { get; } = new List<CalcZone>();

        public List<(Vec3 Min, Vec3 Max)> Boxes { get; } = new List<(Vec3 Min, Vec3 Max)>();

        /// <summary>A single zone alone is still over the patch limit.</summary>
        public bool OverLimit { get; set; }
    }

    /// <summary>Groups zones into clusters: one per storey, one per multi-storey zone, split to stay under the patch limit.</summary>
    public static class ZoneClusters
    {
        public const int PatchLimit = 1_000_000;

        /// <summary>
        /// The region around a zone the solve considers: plan bounds plus margin, from 1.5 ft below
        /// its floor to 3 ft above its top or up to the level above, whichever is higher. A Space
        /// that stops at a dropped ceiling still gets the slab, deck and joists over its open part
        /// (all of them lie under the level, and a triangle touching the box counts), but not the
        /// toilets and cabinets standing on the floor above.
        /// </summary>
        public static (Vec3 Min, Vec3 Max) RegionBox(CalcZone zone, double margin)
        {
            (Vec3 min, Vec3 max) = zone.PlanBounds;
            double top = zone.TopZ + 3.0;
            if (!double.IsNaN(zone.LevelAboveElevation)) top = Math.Max(top, zone.LevelAboveElevation);
            return (new Vec3(min.X - margin, min.Y - margin, zone.BottomZ - 1.5),
                    new Vec3(max.X + margin, max.Y + margin, top));
        }

        public static List<ZoneCluster> Build(IReadOnlyList<CalcZone> zones, double margin,
            Func<IReadOnlyList<(Vec3 Min, Vec3 Max)>, int> trianglesIn)
        {
            var result = new List<ZoneCluster>();
            var items = zones.Select(z => (Zone: z, Box: RegionBox(z, margin))).ToList();

            foreach (var zone in items.Where(i => i.Zone.IsMultiLevel))
                result.Add(Make(new List<(CalcZone Zone, (Vec3 Min, Vec3 Max) Box)> { zone }, trianglesIn));

            foreach (var storey in items.Where(i => !i.Zone.IsMultiLevel).GroupBy(i => Math.Round(i.Zone.LevelElevation, 2)))
                Split(storey.ToList(), trianglesIn, result);
            return result;
        }

        private static void Split(List<(CalcZone Zone, (Vec3 Min, Vec3 Max) Box)> group,
            Func<IReadOnlyList<(Vec3 Min, Vec3 Max)>, int> trianglesIn, List<ZoneCluster> result)
        {
            if (group.Count > 1 && trianglesIn(group.Select(g => g.Box).ToList()) > PatchLimit)
            {
                // Cut in half along the longer plan axis, by box centre.
                double spanX = group.Max(g => g.Box.Max.X) - group.Min(g => g.Box.Min.X);
                double spanY = group.Max(g => g.Box.Max.Y) - group.Min(g => g.Box.Min.Y);
                var ordered = (spanX >= spanY
                    ? group.OrderBy(g => g.Box.Min.X + g.Box.Max.X)
                    : group.OrderBy(g => g.Box.Min.Y + g.Box.Max.Y)).ToList();
                int half = ordered.Count / 2;
                Split(ordered.Take(half).ToList(), trianglesIn, result);
                Split(ordered.Skip(half).ToList(), trianglesIn, result);
                return;
            }
            result.Add(Make(group, trianglesIn));
        }

        private static ZoneCluster Make(List<(CalcZone Zone, (Vec3 Min, Vec3 Max) Box)> group,
            Func<IReadOnlyList<(Vec3 Min, Vec3 Max)>, int> trianglesIn)
        {
            var cluster = new ZoneCluster();
            foreach (var g in group)
            {
                cluster.Zones.Add(g.Zone);
                cluster.Boxes.Add(g.Box);
            }
            cluster.OverLimit = group.Count == 1 && trianglesIn(cluster.Boxes) > PatchLimit;
            return cluster;
        }
    }
}
