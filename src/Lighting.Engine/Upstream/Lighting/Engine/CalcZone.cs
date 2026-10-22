using System;
using System.Collections.Generic;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>Diffuse reflectances (0–1) for the surface classes of one zone.</summary>
    public sealed class Reflectances
    {
        public double Ceiling { get; set; } = 0.8;
        public double Wall { get; set; } = 0.5;
        public double Floor { get; set; } = 0.2;
        public double Furniture { get; set; } = 0.5;

        public double For(SurfaceClass surfaceClass)
        {
            switch (surfaceClass)
            {
                case SurfaceClass.Ceiling: return Ceiling;
                case SurfaceClass.Floor: return Floor;
                case SurfaceClass.Furniture: return Furniture;
                default: return Wall;
            }
        }

        public Reflectances Clone() => new Reflectances { Ceiling = Ceiling, Wall = Wall, Floor = Floor, Furniture = Furniture };
    }

    /// <summary>What part of a zone a stair element is.</summary>
    public enum ZonePartKind
    {
        Landing,
        Flight,
    }

    /// <summary>A stair landing or flight inside a zone: its outline in plan and the heights it spans.</summary>
    public sealed class ZonePart
    {
        /// <summary>"Landing 2", "Flight 3": numbered bottom to top within the zone.</summary>
        public string Name { get; set; } = string.Empty;

        public ZonePartKind Kind { get; set; }

        /// <summary>Outline in plan (Z ignored).</summary>
        public List<Vec3> Outline { get; } = new List<Vec3>();

        public double MinZ { get; set; }

        public double MaxZ { get; set; }

        /// <summary>The element it came from, for the log.</summary>
        public long ElementId { get; set; }

        public bool Contains(double x, double y, double z) =>
            z >= MinZ - 0.5 && z <= MaxZ + 0.5 && Polygon.Contains(new List<List<Vec3>> { Outline }, x, y);
    }

    /// <summary>
    /// A lighting calculation zone: a room or space footprint, the horizontal grid of calculation
    /// points laid over it, and, after solving, the illuminance at each point (footcandles).
    /// Zones that span several levels or hold a stair get their points on the walking surfaces
    /// instead (see <see cref="WalkingSurfaces"/>).
    /// </summary>
    public sealed class CalcZone : ZoneFootprint
    {
        /// <summary>Stair landings and flights in the zone, landings first.</summary>
        public List<ZonePart> Parts { get; } = new List<ZonePart>();

        /// <summary>Points sit on the floors, landings and treads rather than on one flat work plane.</summary>
        public bool OnWalkingSurfaces { get; set; }

        /// <summary>Points on stairs and ramps sit on the surface; set when the zone is mostly stair, so every point does.</summary>
        public bool PointsOnSurface { get; set; }

        /// <summary>Index into <see cref="ZoneFootprint.Levels"/> per point (all 0 for a flat grid).</summary>
        public int[] PointLevel { get; set; } = Array.Empty<int>();

        /// <summary>Index into <see cref="Parts"/> per point, or -1.</summary>
        public int[] PointPart { get; set; } = Array.Empty<int>();

        private double _floorArea;

        /// <summary>
        /// Area people walk on (ft²): the footprint for a flat grid; for walking surfaces, the
        /// footprint times how many levels of floor, landing or tread lie over it.
        /// </summary>
        public double FloorArea
        {
            get => _floorArea > 0 ? _floorArea : Area;
            set => _floorArea = value;
        }

        /// <summary>Height of the work plane above <see cref="BottomZ"/> (feet).</summary>
        public double WorkplaneHeight { get; set; } = 2.5;

        public Reflectances Reflectances { get; set; } = new Reflectances();

        public double GridSpacing { get; set; }

        public List<Vec3> Points { get; } = new List<Vec3>();

        /// <summary>Normal of every calculation point (up for a horizontal work plane).</summary>
        public Vec3 PointNormal { get; set; } = Vec3.UnitZ;

        /// <summary>Total illuminance per point (fc).</summary>
        public double[] Illuminance { get; set; } = Array.Empty<double>();

        /// <summary>Direct component per point (fc).</summary>
        public double[] Direct { get; set; } = Array.Empty<double>();

        /// <summary>
        /// The zone's own surfaces after solving: the patches of its floor, walls, ceiling and
        /// furniture that face into it, with their illuminance. Empty before a solve.
        /// </summary>
        public List<SurfacePatch> Surfaces { get; } = new List<SurfacePatch>();

        /// <summary>Height of the ceiling over the zone (absolute Z, ft), from rays straight up; NaN when open above.</summary>
        public double CeilingZ { get; set; } = double.NaN;

        /// <summary>How many luminaires of each type (by type key) sit inside the zone.</summary>
        public Dictionary<string, int> LuminaireTypeCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Connected watts of the luminaires inside the zone.</summary>
        public double Watts { get; set; }

        public int LuminaireCount { get; set; }

        /// <summary>The zone's design target (report units); null = none.</summary>
        public LightingTarget? Target { get; set; }

        public ZoneStatistics Statistics { get; set; } = new ZoneStatistics();

        public ZoneStatistics DirectStatistics { get; set; } = new ZoneStatistics();

        /// <summary>Lighting power density (W/ft²).</summary>
        public double PowerDensity => FloorArea > 1e-6 ? Watts / FloorArea : 0;

        /// <summary>
        /// Lays a square grid over the footprint at the work plane. The grid is centred on the
        /// footprint's bounding box so the edge rows sit symmetrically, then points closer than
        /// <paramref name="boundaryOffset"/> to the boundary (or outside it) are dropped.
        /// </summary>
        public void GenerateGrid(double spacing, double boundaryOffset)
        {
            Points.Clear();
            GridSpacing = spacing;
            OnWalkingSurfaces = false;
            double z = BottomZ + WorkplaneHeight;
            foreach ((double x, double y) in PlanGrid(spacing, boundaryOffset))
                Points.Add(new Vec3(x, y, z));
            PointLevel = new int[Points.Count];
            PointPart = Enumerable.Repeat(-1, Points.Count).ToArray();
            FloorArea = 0;
        }

        /// <summary>The grid's positions in plan (see <see cref="GenerateGrid"/>).</summary>
        public List<(double X, double Y)> PlanGrid(double spacing, double boundaryOffset)
        {
            var result = new List<(double X, double Y)>();
            if (spacing <= 0 || Loops.Count == 0) return result;

            (Vec3 min, Vec3 max) = PlanBounds;
            double width = max.X - min.X - 2 * boundaryOffset;
            double depth = max.Y - min.Y - 2 * boundaryOffset;
            if (width < 0 || depth < 0)
            {
                width = Math.Max(0, width);
                depth = Math.Max(0, depth);
            }

            int nx = (int)Math.Floor(width / spacing + 1e-9) + 1;
            int ny = (int)Math.Floor(depth / spacing + 1e-9) + 1;
            if ((long)nx * ny > 200000)
                throw new InvalidOperationException($"{DisplayName}: the grid would have {(long)nx * ny} points; use a larger spacing.");

            double x0 = (min.X + max.X) / 2 - (nx - 1) * spacing / 2;
            double y0 = (min.Y + max.Y) / 2 - (ny - 1) * spacing / 2;
            double minDistance = boundaryOffset * 0.999;

            for (int j = 0; j < ny; j++)
            {
                double y = y0 + j * spacing;
                for (int i = 0; i < nx; i++)
                {
                    double x = x0 + i * spacing;
                    if (!Polygon.Contains(Loops, x, y)) continue;
                    if (minDistance > 0 && Polygon.DistanceToBoundary(Loops, x, y) < minDistance) continue;
                    result.Add((x, y));
                }
            }
            return result;
        }
    }
}
