using System;
using System.Collections.Generic;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>One row of a multi-level zone's breakdown: a level or a stair part, with its statistics.</summary>
    public sealed class ZoneBreakdownRow
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>"Level", "Landing" or "Flight".</summary>
        public string Kind { get; set; } = string.Empty;

        public string LevelName { get; set; } = string.Empty;

        public ZoneStatistics Statistics { get; set; } = new ZoneStatistics();
    }

    /// <summary>
    /// Calculation points for zones that span levels or hold a stair (stair shafts, lobbies with
    /// balconies, atriums). Instead of one flat work plane, a ray dropped at every grid position
    /// finds each floor, landing and tread under it with headroom over it: ordinary floors get a
    /// point at the work plane height above them, stairs and ramps a point on the tread. The whole
    /// zone is solved at once, so light passes between its levels; results then split by level
    /// and by stair landing and flight.
    /// </summary>
    public static class WalkingSurfaces
    {
        /// <summary>Clear height a surface needs over it to count as walked on (ft): leaves out the floor under a flight.</summary>
        public const double Headroom = 6.5;

        /// <summary>A level whose floor covers this share of the footprint is the slab over the zone, not a balcony: it and the levels above are dropped.</summary>
        public const double LidCoverage = 0.85;

        /// <summary>A floor at no level this far over the level under it (ft) is reported as a level of its own; lower, it is a step in that level's floor.</summary>
        public const double LooseFloorRise = 4.0;

        /// <summary>When this share of the walking surfaces is stair, every point sits on its surface (a stair shaft).</summary>
        public const double StairShare = 0.25;

        /// <summary>Only near-level faces are walked on (treads, landings, floors, gentle ramps; not stringer tops).</summary>
        private const double MinNormalZ = 0.95;

        /// <summary>The zone uses walking surfaces: it spans levels or a stair lies in it.</summary>
        public static bool Applies(CalcZone zone) => zone.IsMultiLevel || zone.Parts.Count > 0;

        /// <summary>
        /// Lays the zone's points on its walking surfaces. <paramref name="isWalkable"/> says
        /// which element tags are floors, stairs or ramps; <paramref name="isStair"/> which are
        /// stairs or ramps. <paramref name="claimedBy"/> names the other calc zone a floor
        /// position (x, y, z) belongs to, if any (see <see cref="ClaimedByOthers"/>): those
        /// points are left to it. Returns notes for the log.
        /// </summary>
        public static List<string> Generate(CalcZone zone, TriangleScene scene, double spacing, double boundaryOffset,
            Func<long, bool> isWalkable, Func<long, bool> isStair, Func<double, double, double, CalcZone?>? claimedBy = null)
        {
            var notes = new List<string>();
            zone.Points.Clear();
            zone.GridSpacing = spacing;
            zone.OnWalkingSurfaces = true;
            if (zone.Levels.Count == 0) zone.Levels.Add(new ZoneLevel(zone.LevelName, zone.BottomZ));

            List<(double X, double Y)> plan = zone.PlanGrid(spacing, boundaryOffset);
            var hits = new List<(double X, double Y, double Z, bool Stair, int Level, CalcZone? Owner)>();
            double low = zone.BottomZ - 0.5, high = zone.TopZ - 1.5;
            var down = new Vec3(0, 0, -1);
            foreach ((double x, double y) in plan)
            {
                double above = double.PositiveInfinity; // the nearest face over the current position
                bool inside = false;                    // the ray is inside a solid (entered its top, not yet out its bottom)
                var origin = new Vec3(x, y, zone.TopZ + 0.25);
                for (int k = 0; k < 64 && origin.Z > low; k++)
                {
                    int hit = scene.ClosestHit(origin, down, origin.Z - low + 0.01, out double distance, out bool front);
                    if (hit < 0) break;
                    double z = origin.Z - distance;
                    long tag = scene.Triangles[hit].Tag;
                    if (front && !inside && scene.NormalOf(hit).Z > MinNormalZ && z >= low && z <= high &&
                        above - z >= Headroom && isWalkable(tag))
                        hits.Add((x, y, z, isStair(tag), zone.LevelIndexAt(z), claimedBy?.Invoke(x, y, z)));
                    // Faces point out of their solids: going down, a front face is a way in, a back face the way out.
                    inside = front;
                    above = z;
                    origin = new Vec3(x, y, z - 0.02);
                }
            }

            // A level floored almost everywhere is the slab over the zone (its upper limit was set
            // above the next floor), not a balcony or landing: keep only what lies below it.
            for (int level = 1; level < zone.Levels.Count && plan.Count > 0; level++)
            {
                int covered = hits.Where(h => h.Level == level && !h.Stair).Select(h => (h.X, h.Y)).Distinct().Count();
                if (covered < LidCoverage * plan.Count) continue;
                notes.Add($"{zone.DisplayName}: the floor at {zone.Levels[level].Name} covers {covered * 100 / plan.Count}% of the zone, so it is " +
                          "taken as the slab over it; points from that level up are left out.");
                hits.RemoveAll(h => h.Level >= level);
                break;
            }

            // A floor that is another calc zone's (the storey over a double-height shop, corridors
            // over a lobby) is that zone's to calculate: counting it here would count it twice.
            foreach (IGrouping<CalcZone, (double X, double Y, double Z, bool Stair, int Level, CalcZone? Owner)> owner in
                     hits.Where(h => h.Owner != null).GroupBy(h => h.Owner!))
                notes.Add($"{zone.DisplayName}: {owner.Count()} position(s) on the floor at {zone.Levels[owner.First().Level].Name} " +
                          $"lie in {owner.Key.DisplayName}, so they are left to it.");
            hits.RemoveAll(h => h.Owner != null);

            int[] partOf = hits.Select(h => zone.Parts.FindIndex(p => p.Contains(h.X, h.Y, h.Z))).ToArray();
            bool OnFlight(int i) => partOf[i] >= 0 && zone.Parts[partOf[i]].Kind == ZonePartKind.Flight;

            // A level counts only where a floor or landing lies at it. A storey of a neighbouring
            // block, or a mezzanine the zone only passes, would otherwise cut flights in two.
            var anchored = new List<ZoneLevel> { zone.Levels[0] };
            for (int level = 1; level < zone.Levels.Count; level++)
            {
                double elevation = zone.Levels[level].Elevation;
                int at = Enumerable.Range(0, hits.Count).Count(i => !OnFlight(i) && Math.Abs(hits[i].Z - elevation) <= 1.0);
                if (at >= 3) anchored.Add(zone.Levels[level]);
                else if (hits.Any(h => h.Level == level))
                    notes.Add($"{zone.DisplayName}: no floor or landing lies at {zone.Levels[level].Name}, so it is not split there.");
            }
            // A floor at no level (a loft 9.6 ft over L5, short of the roof level) gets a level of its
            // own, named from the level under it, rather than being mixed into that level's statistics.
            List<double> loose = Enumerable.Range(0, hits.Count)
                .Where(i => !hits[i].Stair && partOf[i] < 0 && anchored.All(l => Math.Abs(hits[i].Z - l.Elevation) > 1.0))
                .Select(i => hits[i].Z).OrderBy(z => z).ToList();
            var extra = new List<ZoneLevel>();
            for (int start = 0; start < loose.Count;)
            {
                int end = start;
                while (end + 1 < loose.Count && loose[end + 1] - loose[end] < 0.25) end++;
                double lo = loose[start], hi = loose[end];
                ZoneLevel? below = anchored.Where(l => l.Elevation <= lo).OrderByDescending(l => l.Elevation).FirstOrDefault();
                if (end - start + 1 >= 3 && hi - lo <= 1.0 && below != null && lo - below.Elevation > LooseFloorRise)
                {
                    var level = new ZoneLevel($"{below.Name} + {lo - below.Elevation:0.#} ft", lo);
                    extra.Add(level);
                    notes.Add($"{zone.DisplayName}: a floor at {lo:0.##} lies at no level, so its {end - start + 1} point(s) are reported as {level.Name}.");
                }
                start = end + 1;
            }
            anchored.AddRange(extra);
            anchored.Sort((a, b) => a.Elevation.CompareTo(b.Elevation));
            zone.Levels.Clear();
            zone.Levels.AddRange(anchored);
            int[] levelOf = hits.Select(h => zone.LevelIndexAt(h.Z)).ToArray();

            // A landing or flight belongs whole to the level it starts from.
            for (int part = 0; part < zone.Parts.Count; part++)
            {
                int[] mine = Enumerable.Range(0, hits.Count).Where(i => partOf[i] == part).ToArray();
                if (mine.Length == 0) continue;
                int home = zone.LevelIndexAt(mine.Min(i => hits[i].Z));
                foreach (int i in mine) levelOf[i] = home;
            }

            // Keep only the levels that have points, renumbered.
            List<int> used = levelOf.Distinct().OrderBy(l => l).ToList();
            if (used.Count == 0) used.Add(0);
            List<ZoneLevel> kept = used.Select(l => zone.Levels[l]).ToList();
            var renumber = used.Select((l, i) => (l, i)).ToDictionary(p => p.l, p => p.i);
            zone.Levels.Clear();
            zone.Levels.AddRange(kept);

            zone.PointsOnSurface = hits.Count > 0 && hits.Count(h => h.Stair) >= StairShare * hits.Count;
            var points = new List<Vec3>();
            var levels = new List<int>();
            var parts = new List<int>();
            foreach (int i in Enumerable.Range(0, hits.Count).OrderBy(i => levelOf[i]).ThenBy(i => hits[i].Z).ThenBy(i => hits[i].Y).ThenBy(i => hits[i].X))
            {
                var h = hits[i];
                double z = h.Z + (h.Stair || zone.PointsOnSurface ? 0 : zone.WorkplaneHeight);
                points.Add(new Vec3(h.X, h.Y, z));
                levels.Add(renumber[levelOf[i]]);
                parts.Add(partOf[i]);
            }
            zone.Points.AddRange(points);
            zone.PointLevel = levels.ToArray();
            zone.PointPart = parts.ToArray();
            zone.FloorArea = plan.Count > 0 ? zone.Area * hits.Count / plan.Count : 0;

            string onLevels = string.Join(", ", zone.Levels.Select((l, i) => $"{l.Name} {levels.Count(v => v == i)}"));
            notes.Add($"{zone.DisplayName}: {points.Count} points on walking surfaces ({onLevels}); " +
                      (zone.PointsOnSurface ? "all on the surface (stair)" : $"{zone.WorkplaneHeight:0.##} ft above floors, on the surface of stairs") +
                      $"; {zone.Parts.Count} stair part(s).");
            return notes;
        }

        /// <summary>
        /// The calc zone, other than <paramref name="zone"/>, that a floor position belongs to: one
        /// over it in plan whose own floor starts higher than this zone's and whose height range
        /// holds the position (the storey over a double-height room). Null when none.
        /// </summary>
        public static Func<double, double, double, CalcZone?> ClaimedByOthers(CalcZone zone, IEnumerable<CalcZone> zones)
        {
            List<CalcZone> above = zones.Where(o => !ReferenceEquals(o, zone) && o.BottomZ > zone.BottomZ + 1.0 && o.Loops.Count > 0).ToList();
            return (x, y, z) => above.FirstOrDefault(o => z >= o.BottomZ - 0.5 && z < o.TopZ && o.ContainsPlan(x, y));
        }

        /// <summary>
        /// One level of a solved multi-level zone as a zone of its own, for the report and plan
        /// views: its points and the surfaces between its floor and the next level up.
        /// </summary>
        public static CalcZone Slice(CalcZone zone, int level)
        {
            ZoneLevel lvl = zone.Levels[level];
            double ceiling = double.IsNaN(zone.CeilingZ) ? zone.TopZ : zone.CeilingZ;
            // Up to just under the next floor, so its top doesn't lid this level's view.
            double bottom = lvl.Elevation - 0.5;
            double top = level + 1 < zone.Levels.Count ? zone.Levels[level + 1].Elevation - 0.1 : Math.Max(ceiling, bottom + 1);

            var slice = new CalcZone
            {
                Key = zone.Key + "#" + level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Number = zone.Number,
                Name = zone.Name,
                LevelName = lvl.Name,
                LevelElevation = lvl.Elevation,
                Kind = zone.Kind,
                BottomZ = bottom,
                TopZ = top,
                CeilingZ = top - 0.25, // the 3D view clips a quarter foot over the ceiling: at the top

                WorkplaneHeight = zone.WorkplaneHeight,
                Reflectances = zone.Reflectances,
                GridSpacing = zone.GridSpacing,
                PointNormal = zone.PointNormal,
                Target = zone.Target,
                OnWalkingSurfaces = zone.OnWalkingSurfaces,
                PointsOnSurface = zone.PointsOnSurface,
            };
            slice.Loops.AddRange(zone.Loops);
            slice.Levels.Add(lvl);

            var partMap = new Dictionary<int, int>();
            var points = new List<int>();
            for (int i = 0; i < zone.Points.Count; i++)
                if (i < zone.PointLevel.Length && zone.PointLevel[i] == level) points.Add(i);
            foreach (int i in points)
            {
                int part = i < zone.PointPart.Length ? zone.PointPart[i] : -1;
                if (part >= 0 && !partMap.ContainsKey(part))
                {
                    partMap[part] = slice.Parts.Count;
                    slice.Parts.Add(zone.Parts[part]);
                }
            }

            slice.Points.AddRange(points.Select(i => zone.Points[i]));
            slice.Illuminance = points.Select(i => i < zone.Illuminance.Length ? zone.Illuminance[i] : double.NaN).ToArray();
            slice.Direct = points.Select(i => i < zone.Direct.Length ? zone.Direct[i] : double.NaN).ToArray();
            slice.PointLevel = new int[points.Count];
            slice.PointPart = points.Select(i => i < zone.PointPart.Length && zone.PointPart[i] >= 0 ? partMap[zone.PointPart[i]] : -1).ToArray();
            slice.Statistics = ZoneStatistics.From(slice.Illuminance);
            slice.DirectStatistics = ZoneStatistics.From(slice.Direct);
            slice.FloorArea = zone.Points.Count > 0 ? zone.FloorArea * points.Count / zone.Points.Count : 0;
            // Patches reaching into the slice; the 3D view clips them at its floor and top.
            slice.Surfaces.AddRange(zone.Surfaces.Where(p =>
                Math.Max(p.A.Z, Math.Max(p.B.Z, p.C.Z)) >= bottom - 0.25 && Math.Min(p.A.Z, Math.Min(p.B.Z, p.C.Z)) <= top));
            return slice;
        }

        /// <summary>Statistics per level and per stair landing and flight, each part after its level.</summary>
        public static List<ZoneBreakdownRow> Breakdown(CalcZone zone)
        {
            var rows = new List<ZoneBreakdownRow>();
            int count = Math.Min(zone.Points.Count, zone.Illuminance.Length);
            IEnumerable<double> Values(Func<int, bool> pick) =>
                Enumerable.Range(0, count).Where(pick).Select(i => zone.Illuminance[i]);
            int LevelOf(int i) => i < zone.PointLevel.Length ? zone.PointLevel[i] : 0;
            int PartOf(int i) => i < zone.PointPart.Length ? zone.PointPart[i] : -1;

            for (int level = 0; level < Math.Max(1, zone.Levels.Count); level++)
            {
                string levelName = level < zone.Levels.Count ? zone.Levels[level].Name : zone.LevelName;
                if (zone.IsMultiLevel)
                {
                    ZoneStatistics s = ZoneStatistics.From(Values(i => LevelOf(i) == level));
                    if (s.Count > 0) rows.Add(new ZoneBreakdownRow { Name = levelName, Kind = "Level", LevelName = levelName, Statistics = s });
                }
                // Parts listed under the level most of their points belong to, bottom to top.
                foreach (int part in Enumerable.Range(0, zone.Parts.Count).OrderBy(p => zone.Parts[p].MinZ))
                {
                    var mine = Enumerable.Range(0, count).Where(i => PartOf(i) == part).ToList();
                    if (mine.Count == 0) continue;
                    int home = mine.GroupBy(LevelOf).OrderByDescending(g => g.Count()).First().Key;
                    if (home != level) continue;
                    rows.Add(new ZoneBreakdownRow
                    {
                        Name = zone.Parts[part].Name,
                        Kind = zone.Parts[part].Kind.ToString(),
                        LevelName = levelName,
                        Statistics = ZoneStatistics.From(mine.Select(i => zone.Illuminance[i])),
                    });
                }
            }
            return rows;
        }

        /// <summary>
        /// Gives the zone its own copies of the stair parts whose outline centre lies inside its
        /// footprint and whose heights overlap its own, named bottom to top.
        /// </summary>
        public static void AssignParts(CalcZone zone, IEnumerable<ZonePart> parts)
        {
            zone.Parts.Clear();
            foreach (ZonePart part in parts)
            {
                if (part.Outline.Count < 3 || part.MaxZ < zone.BottomZ - 0.5 || part.MinZ > zone.TopZ) continue;
                double cx = part.Outline.Average(p => p.X), cy = part.Outline.Average(p => p.Y);
                if (!zone.ContainsPlan(cx, cy)) continue;
                var copy = new ZonePart { Kind = part.Kind, MinZ = part.MinZ, MaxZ = part.MaxZ, ElementId = part.ElementId };
                copy.Outline.AddRange(part.Outline);
                zone.Parts.Add(copy);
            }
            NameParts(zone.Parts);
        }

        /// <summary>
        /// Numbers stair parts bottom to top ("Landing 1", "Flight 1", ...) and orders them
        /// landings first, so a point on both a landing and the end of a flight counts as landing.
        /// </summary>
        public static void NameParts(List<ZonePart> parts)
        {
            int landing = 0, flight = 0;
            foreach (ZonePart part in parts.OrderBy(p => p.MinZ).ThenBy(p => p.Kind))
                part.Name = part.Kind == ZonePartKind.Landing ? $"Landing {++landing}" : $"Flight {++flight}";
            List<ZonePart> ordered = parts.OrderBy(p => p.Kind == ZonePartKind.Landing ? 0 : 1).ThenBy(p => p.MinZ).ToList();
            parts.Clear();
            parts.AddRange(ordered);
        }
    }
}
