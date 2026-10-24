using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>What Revit itself says about a fixture: its level and the space or room it is in.</summary>
    public sealed class FixtureRevitInfo
    {
        public long Id { get; set; }
        public string TypeName { get; set; } = string.Empty;

        /// <summary>The light centre, after placement.</summary>
        public Vec3 Position { get; set; }

        /// <summary>The family origin (where it is mounted); null when unknown.</summary>
        public Vec3? Origin { get; set; }

        /// <summary>Its light centre was placed as a pendant, hung below its mount.</summary>
        public bool Pendant { get; set; }

        /// <summary>Its light centre was placed in or on a ceiling: recessed or surface (a pendant may hang from a floor level by design).</summary>
        public bool CeilingMounted { get; set; }

        /// <summary>Its own solids reach well below its origin (a downlight or pendant family), so its origin is its mount.</summary>
        public bool HangsBelowOrigin { get; set; }

        /// <summary>How far its light was dropped out of the slab or ceiling it was buried in (ft); null when it was not.</summary>
        public double? MovedDownOutOfSolid { get; set; }

        public string Level { get; set; } = string.Empty;

        /// <summary>The level's elevation in host coordinates; null when the fixture has no level.</summary>
        public double? LevelElevation { get; set; }

        /// <summary>The UniqueId of Revit's space or room for the fixture (compared with zone keys); empty when none.</summary>
        public string SpaceKey { get; set; } = string.Empty;

        /// <summary>That space or room as "number name".</summary>
        public string SpaceName { get; set; } = string.Empty;

        /// <summary>
        /// Revit's space or room at the light centre. Differs from <see cref="SpaceKey"/> when the
        /// origin sits in a slab or on a wall line, e.g. a pendant whose origin is in the floor above.
        /// </summary>
        public string CentreSpaceKey { get; set; } = string.Empty;
        public string CentreSpaceName { get; set; } = string.Empty;

        /// <summary>Whether either of Revit's answers is this space or room.</summary>
        public bool RevitSays(string zoneKey) => zoneKey.Length > 0 && (SpaceKey == zoneKey || CentreSpaceKey == zoneKey);

        public string Describe() =>
            "Revit level " + (Level.Length > 0
                ? Level + (LevelElevation.HasValue ? " (" + LevelElevation.Value.ToString("0.##", CultureInfo.InvariantCulture) + ")" : string.Empty)
                : "none") +
            ", space " + Label(SpaceKey, SpaceName) +
            (CentreSpaceKey.Length > 0 && CentreSpaceKey != SpaceKey ?", at the light centre " + Label(CentreSpaceKey, CentreSpaceName) : string.Empty);

        private static string Label(string key, string name) => name.Length > 0 ? name : key.Length > 0 ? key : "none";
    }

    /// <summary>
    /// Gives each fixture one owning zone and cross-checks it against Revit's own level and space.
    /// Revit's space only settles a tie between zones that both hold the light centre: Revit tests
    /// the family origin (or its room calculation point), not where the light leaves the fixture.
    /// </summary>
    public static class FixtureZoneCheck
    {
        /// <summary>
        /// Logs and warns about the fixtures, and returns each one's owning zone (null for none):
        /// see <see cref="Owner"/>.
        /// </summary>
        public static Dictionary<FixtureRevitInfo, CalcZone?> Run(IReadOnlyList<CalcZone> zones, IReadOnlyList<FixtureRevitInfo> fixtures,
            Func<CalcZone, Vec3, bool> inZone, CalcLog log, List<string> warnings)
        {
            var zoneByKey = zones.ToDictionary(z => z.Key, StringComparer.Ordinal);
            var geometric = new Dictionary<FixtureRevitInfo, CalcZone?>();
            foreach (FixtureRevitInfo f in fixtures)
                geometric[f] = Owner(zones, f, inZone);

            // Light centres below the floor of their own level. A pendant mounted at its level (on
            // or under its slab) hangs below it by design: Art Glass pendants hung from L3 into a lobby.
            foreach (IGrouping<string, FixtureRevitInfo> type in fixtures
                         .Where(f => f.LevelElevation.HasValue && f.Position.Z < f.LevelElevation.Value - LightingReport.LuminaireBelowFloor)
                         .Where(f => !(f.Pendant && f.Origin.HasValue && f.Origin.Value.Z >= f.LevelElevation!.Value - MountBelowLevel))
                         .GroupBy(f => f.TypeName))
            {
                foreach (FixtureRevitInfo f in type)
                    log.Write($"  WARNING: luminaire {f.Id} {f.TypeName}: light centre at z {F(f.Position.Z)} is " +
                              $"{F(f.LevelElevation!.Value - f.Position.Z)} ft below its Revit level {f.Level} ({F(f.LevelElevation.Value)})");
                FixtureRevitInfo first = type.First();
                warnings.Add($"{type.Key}: the light centre of {type.Count()} fixture(s) is below the floor of its Revit level " +
                             $"(e.g. {first.Id} at z {F(first.Position.Z)}, level {first.Level} at {F(first.LevelElevation!.Value)}). " +
                             "Check its Mounting in the Luminaire Manager.");
            }

            // The geometric zone and Revit's space disagree (only when one of them is a calc zone).
            // Revit's space at the light centre counts as agreeing: the origin can sit in a slab.
            var mismatches = new List<FixtureRevitInfo>();
            foreach (FixtureRevitInfo f in fixtures)
            {
                string revitKey = f.CentreSpaceKey.Length > 0 ? f.CentreSpaceKey : f.SpaceKey;
                if (revitKey.Length == 0) continue;
                CalcZone? zone = geometric[f];
                if (zone != null ? f.RevitSays(zone.Key) : !zoneByKey.ContainsKey(revitKey)) continue;
                mismatches.Add(f);
                log.Write($"  WARNING: luminaire {f.Id} {f.TypeName} at {f.Position}: light centre is in {zone?.DisplayName ?? "no calc zone"}, " +
                          $"but Revit puts it in {Space(f, zoneByKey)}");
            }
            if (mismatches.Count > 0)
            {
                FixtureRevitInfo first = mismatches[0];
                warnings.Add($"{mismatches.Count} fixture(s) have a light centre in a different zone from Revit's space " +
                             $"(e.g. {first.Id} {first.TypeName}: {geometric[first]?.DisplayName ?? "no calc zone"}, Revit {Space(first, zoneByKey)}). " +
                             "Check the space boundaries or the light centres; the calc log lists each one.");
            }

            // Fixtures over a zone's footprint, just above its top, that Revit puts in
            // no space: most likely modelled at the wrong level or elevation (a ceiling light on the
            // slab of the floor above). One in a Revit space is where the model says it is.
            var misplaced = new List<(FixtureRevitInfo Fixture, CalcZone Zone, double Distance)>();
            foreach (FixtureRevitInfo f in fixtures)
            {
                if (geometric[f] != null || f.SpaceKey.Length > 0 || f.CentreSpaceKey.Length > 0) continue;
                double z = f.Position.Z;
                // Only above: a fixture just under a zone's floor is usually the level below's, hung on the slab.
                // One whose Revit level is at or over the zone's top belongs to the floor or roof above (a roof bollard).
                CalcZone? zone = zones
                    .Where(c => c.ContainsPlan(f.Position.X, f.Position.Y))
                    .Where(c => !(f.LevelElevation >= c.TopZ - LightingReport.LuminaireBelowFloor))
                    .Where(c => z > c.TopZ + LightingReport.LuminaireAboveTop && z <= c.TopZ + LightingReport.LuminaireAboveTop + MisplacedReach)
                    .OrderByDescending(c => c.TopZ)
                    .FirstOrDefault();
                if (zone == null) continue;
                misplaced.Add((f, zone, z - zone.TopZ));
                log.Write($"  WARNING: luminaire {f.Id} {f.TypeName} at {f.Position}: light centre is {Above(zone, z - zone.TopZ)} " +
                          $"and Revit puts it in no space; check its level and elevation. {f.Describe()}");
            }
            if (misplaced.Count > 0)
            {
                var first = misplaced[0];
                warnings.Add($"{misplaced.Count} fixture(s) look placed at the wrong height: just above a zone, and in no Revit space " +
                             $"(e.g. {first.Fixture.Id} {first.Fixture.TypeName}: {Above(first.Zone, first.Distance)}). " +
                             "Check their level and elevation; the calc log lists each one.");
            }

            // The same type twice at one spot, less than a storey apart (1461361 at z 0.83 under 1496245
            // at z 4.29, or two at the same height): most likely copies left behind. One per floor is a
            // stacked layout, not a copy, even when every floor's was left on one Revit level (the
            // Pendant-Domes, all on L2); copies inside a slab are caught by the floor check below.
            var stacked = new List<List<FixtureRevitInfo>>();
            foreach (var spot in fixtures
                         .Where(f => f.Origin.HasValue)
                         .GroupBy(f => (f.TypeName, X: Math.Round(f.Origin!.Value.X / StackedTolerance), Y: Math.Round(f.Origin!.Value.Y / StackedTolerance))))
            {
                List<FixtureRevitInfo> all = spot.ToList();
                var copies = new HashSet<FixtureRevitInfo>();
                for (int i = 0; i < all.Count; i++)
                    for (int j = i + 1; j < all.Count; j++)
                        if (Math.Abs(all[i].Origin!.Value.Z - all[j].Origin!.Value.Z) < StackedMaxGap)
                        {
                            copies.Add(all[i]);
                            copies.Add(all[j]);
                        }
                if (copies.Count > 0) stacked.Add(copies.OrderBy(f => f.Origin!.Value.Z).ToList());
            }
            foreach (List<FixtureRevitInfo> spot in stacked)
                log.Write($"  WARNING: {spot[0].TypeName} fixtures stacked at one spot: " +
                          string.Join(", ", spot.Select(f => $"{f.Id} at z {F(f.Origin!.Value.Z)} ({(f.Level.Length > 0 ? f.Level : "no level")})")) +
                          "; most likely leftover copies");
            if (stacked.Count > 0)
                warnings.Add($"{stacked.Count} spot(s) have the same fixture type stacked less than a storey apart (e.g. {stacked[0][0].TypeName}: " +
                             string.Join(" and ", stacked[0].Take(2).Select(f => $"{f.Id} at z {F(f.Origin!.Value.Z)}")) +
                             "); one is most likely a leftover copy. The calc log lists each.");

            // A light buried in the slab or ceiling over a room (316 Trash's LED15FM12, mounted at the
            // top of the L4 slab): the calc lights from the underside, but the fixture belongs lower.
            var buried = fixtures.Where(f => f.MovedDownOutOfSolid.HasValue).ToList();
            foreach (FixtureRevitInfo f in buried)
                log.Write($"  WARNING: luminaire {f.Id} {f.TypeName}: its light is inside the slab or ceiling over {geometric[f]?.DisplayName ?? "no calc zone"}; " +
                          $"calculated from the underside, 1\" below z {F(f.Position.Z + LuminaireMounting.Clearance)}. " +
                          $"Move the fixture down {F(f.MovedDownOutOfSolid!.Value)} ft into the room. {f.Describe()}");
            if (buried.Count > 0)
            {
                FixtureRevitInfo first = buried[0];
                warnings.Add($"{buried.Count} fixture(s) have their light inside the slab or ceiling over a room " +
                             $"(e.g. {first.Id} {first.TypeName} over {geometric[first]?.DisplayName ?? "no calc zone"}: move it down {F(first.MovedDownOutOfSolid!.Value)} ft). " +
                             "They are calculated from the underside; move them down into the room. The calc log lists each.");
            }

            // A fixture mounted at a floor: a ceiling fixture at its own level's floor (placed on the
            // level and never raised), or a ceiling or downlight family at another storey's floor
            // height (1462403-1462406: on L1, at L2 to L5's floors, inside each slab). One already
            // flagged as buried is not listed twice.
            var storeys = zones.SelectMany(z => z.Levels).GroupBy(l => Math.Round(l.Elevation, 2)).Select(g => g.First()).ToList();
            var onFloor = new List<(FixtureRevitInfo Fixture, string Floor)>();
            foreach (FixtureRevitInfo f in fixtures.Where(f => f.Origin.HasValue && !f.MovedDownOutOfSolid.HasValue))
            {
                double z = f.Origin!.Value.Z;
                string own = LevelName(f.Level);
                if (f.CeilingMounted && f.LevelElevation.HasValue && Math.Abs(z - f.LevelElevation.Value) < AtFloor)
                    onFloor.Add((f, $"the floor of its own level {f.Level}"));
                else if (f.CeilingMounted || f.HangsBelowOrigin)
                {
                    ZoneLevel? other = storeys.FirstOrDefault(s => Math.Abs(z - s.Elevation) < AtFloor && LevelName(s.Name) != own);
                    if (other != null) onFloor.Add((f, $"the floor of {other.Name}, though its level is {(f.Level.Length > 0 ? f.Level : "none")}"));
                }
            }
            foreach ((FixtureRevitInfo f, string floor) in onFloor)
                log.Write($"  WARNING: luminaire {f.Id} {f.TypeName}: its origin (mount) is at z {F(f.Origin!.Value.Z)}, {floor}");
            if (onFloor.Count > 0)
                warnings.Add($"{onFloor.Count} ceiling or downlight fixture(s) are mounted exactly at a floor (e.g. {onFloor[0].Fixture.Id} {onFloor[0].Fixture.TypeName}: {onFloor[0].Floor}); " +
                             "most likely copies or never raised to the ceiling. Check their level and elevation; the calc log lists each.");

            // Zones with points but no fixture inside.
            foreach (CalcZone zone in zones)
            {
                if (zone.Points.Count == 0 || geometric.Values.Any(z => z == zone)) continue;
                List<FixtureRevitInfo> revitSays = fixtures.Where(f => f.RevitSays(zone.Key)).ToList();
                // Light centres in another calc zone: Revit's space is the one over the mount (502's
                // pendants, mounted at its slab, hang into 402).
                List<CalcZone> elsewhere = revitSays.Select(f => geometric[f]).Where(z => z != null && z != zone).Select(z => z!).Distinct().ToList();
                string revitText = revitSays.Count == 0
                    ? "Revit puts no fixtures in this space either"
                    : $"Revit puts {revitSays.Count} fixture(s) in it ({string.Join(", ", revitSays.Take(3).Select(f => f.Id.ToString(CultureInfo.InvariantCulture)))}), " +
                      (revitSays.All(f => geometric[f] != null && geometric[f] != zone)
                          ? $"but Revit's space for these looks wrong (mounted at the slab, light centre in {string.Join(", ", elsewhere.Select(z => z.DisplayName))})"
                          : "so check their light centres");
                foreach (var m in misplaced.Where(m => m.Zone == zone).Take(3))
                    revitText += $"; fixture {m.Fixture.Id.ToString(CultureInfo.InvariantCulture)} is over it, {F(m.Distance)} ft above its top";
                log.Write($"  WARNING: {zone.DisplayName} has no luminaires inside it; {revitText}. Nearest fixtures:");
                foreach ((FixtureRevitInfo f, double distance) in fixtures
                             .Select(f => (f, Polygon.DistanceToBoundary(zone.Loops, f.Position.X, f.Position.Y)))
                             .OrderBy(t => t.Item2)
                             .Take(3))
                    log.Write($"      {f.Id} {f.TypeName} at {f.Position}, {F(distance)} ft from the boundary; {f.Describe()}");
                warnings.Add($"{zone.DisplayName}: no luminaires inside it, so it reads zero; {revitText}.");
            }
            return geometric;
        }

        /// <summary>
        /// The zone a fixture belongs to when its light centre lies in more than one (a tall zone
        /// and the storey over part of it): the one Revit names, else the one whose floor under the
        /// light is highest (the tightest), else the smallest. Null when it is in none.
        /// </summary>
        public static CalcZone? Owner(IReadOnlyList<CalcZone> zones, FixtureRevitInfo f, Func<CalcZone, Vec3, bool> inZone)
        {
            List<CalcZone> holding = zones.Where(z => inZone(z, f.Position)).ToList();
            if (holding.Count <= 1) return holding.FirstOrDefault();
            CalcZone? named = holding.FirstOrDefault(z => z.Key.Length > 0 && z.Key == f.CentreSpaceKey)
                              ?? holding.FirstOrDefault(z => z.Key.Length > 0 && z.Key == f.SpaceKey);
            return named ?? holding.OrderByDescending(z => FloorUnder(z, f.Position.Z)).ThenBy(z => z.Area).First();
        }

        /// <summary>The zone's floor under a height: its level there for a zone spanning levels, else its bottom.</summary>
        internal static double FloorUnder(CalcZone zone, double z) =>
            zone.Levels.Count > 1 ? Math.Max(zone.BottomZ, zone.Levels[zone.LevelIndexAt(z)].Elevation) : zone.BottomZ;

        /// <summary>Fixtures of one type within this distance in plan (ft) are at the same spot.</summary>
        public const double StackedTolerance = 0.1;

        /// <summary>Fixtures at one spot closer than this in height (ft) are less than a storey apart: one is a copy.</summary>
        public const double StackedMaxGap = 6.0;

        /// <summary>An origin this close to a floor's height (ft) is mounted at that floor.</summary>
        public const double AtFloor = 0.02;

        /// <summary>A Revit level name without the " (by height)" the scene builder adds for fixtures it levelled itself.</summary>
        private static string LevelName(string name) =>
            name.EndsWith(" (by height)", StringComparison.Ordinal) ? name.Substring(0, name.Length - " (by height)".Length) : name;

        /// <summary>A pendant mounted this far under its level (a slab and its finishes) is still hung from that level.</summary>
        public const double MountBelowLevel = 1.5;

        /// <summary>A fixture this far past a zone's top (beyond the allowance) may be the zone's, placed at the wrong height; further off it is another floor's.</summary>
        public const double MisplacedReach = 6;

        private static string Above(CalcZone zone, double distance) => $"{F(distance)} ft above the top of {zone.DisplayName}";

        private static string Space(FixtureRevitInfo f, Dictionary<string, CalcZone> zones)
        {
            (string key, string name) = f.CentreSpaceKey.Length > 0 ? (f.CentreSpaceKey, f.CentreSpaceName) : (f.SpaceKey, f.SpaceName);
            return zones.TryGetValue(key, out CalcZone? z) ? z.DisplayName : name.Length > 0 ? name + " (not in this calc)" : key;
        }

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
