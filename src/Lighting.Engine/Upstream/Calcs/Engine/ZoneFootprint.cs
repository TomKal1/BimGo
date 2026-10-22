using System;
using System.Collections.Generic;
using System.Linq;

namespace WWTools.Calcs.Engine
{
    /// <summary>A building level a zone spans: its name and elevation (host internal Z, ft).</summary>
    public sealed class ZoneLevel
    {
        public ZoneLevel(string name, double elevation)
        {
            Name = name;
            Elevation = elevation;
        }

        public string Name { get; }

        public double Elevation { get; }
    }

    /// <summary>
    /// What every calc needs to know about a zone: which room or space it is, its footprint in
    /// plan, and the heights it spans. Calc-specific zones (lighting grids, airflow terminals)
    /// derive from this so the same zone reader serves them all.
    /// </summary>
    public class ZoneFootprint
    {
        /// <summary>Stable key (e.g. the room or space UniqueId).</summary>
        public string Key { get; set; } = string.Empty;

        public string Number { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string LevelName { get; set; } = string.Empty;

        /// <summary>Elevation of the zone's level in host internal coordinates (feet), for matching zones to plan views.</summary>
        public double LevelElevation { get; set; }

        /// <summary>"Space", "Room" or "Linked Room".</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Boundary loops in plan (Z ignored); a point is inside when inside an odd number of loops.</summary>
        public List<List<Vec3>> Loops { get; } = new List<List<Vec3>>();

        /// <summary>
        /// Boundary edges on an exterior (perimeter) wall, as (loop, edge): edge i runs from
        /// point i to point i + 1 of its loop. Empty when nothing classified the walls.
        /// </summary>
        public HashSet<(int Loop, int Edge)> ExteriorEdges { get; } = new HashSet<(int Loop, int Edge)>();

        public bool IsExteriorEdge(int loop, int edge) => ExteriorEdges.Contains((loop, edge));

        public double BottomZ { get; set; }

        public double TopZ { get; set; }

        /// <summary>
        /// The levels the zone spans, lowest first: its own level, then any level whose floor lies
        /// inside its height (a stair shaft, a lobby with a balcony). One entry for an ordinary room.
        /// </summary>
        public List<ZoneLevel> Levels { get; } = new List<ZoneLevel>();

        /// <summary>The zone spans more than one level.</summary>
        public bool IsMultiLevel => Levels.Count > 1;

        /// <summary>
        /// The level a height belongs to: the highest whose elevation is at or below it (with 3"
        /// of slack for a floor finished just under its level); 0 when none. A stair flight
        /// belongs to the level it starts from.
        /// </summary>
        public int LevelIndexAt(double z)
        {
            int index = 0;
            for (int i = 0; i < Levels.Count; i++)
                if (Levels[i].Elevation <= z + 0.25) index = i;
            return index;
        }

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Number) ? Name : string.IsNullOrWhiteSpace(Name) ? Number : Number + " " + Name;

        /// <summary>Plan area of the footprint (ft²).</summary>
        public double Area
        {
            get
            {
                // Even-odd loops: the outer loop is the largest; every other loop is a hole.
                var areas = Loops.Select(l => Math.Abs(Polygon.SignedArea(l))).OrderByDescending(a => a).ToList();
                if (areas.Count == 0) return 0;
                return Math.Max(0, areas[0] - areas.Skip(1).Sum());
            }
        }

        public (Vec3 Min, Vec3 Max) PlanBounds
        {
            get
            {
                var all = Loops.SelectMany(l => l).ToList();
                if (all.Count == 0) return (Vec3.Zero, Vec3.Zero);
                Vec3 min = all[0], max = all[0];
                foreach (Vec3 p in all)
                {
                    min = Vec3.Min(min, p);
                    max = Vec3.Max(max, p);
                }
                return (new Vec3(min.X, min.Y, BottomZ), new Vec3(max.X, max.Y, TopZ));
            }
        }

        public bool ContainsPlan(double x, double y) => Polygon.Contains(Loops, x, y);
    }
}
