using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>A fixture type the layout assistant can use, with the values the calc applies.</summary>
    public sealed class LayoutFixtureType
    {
        private const double DefaultSize = 2.0;

        public string TypeKey { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public IesPhotometry Photometry { get; set; } = null!;

        public double LightLossFactor { get; set; } = 1;

        public double Multiplier { get; set; } = 1;

        public double Watts { get; set; }

        public double RotationDegrees { get; set; }

        public string Aim { get; set; } = "Down";

        /// <summary>The Luminaire Manager's light centre offset (ft).</summary>
        public double CenterOffset { get; set; }

        /// <summary>The photometry is a guess, not IES data.</summary>
        public bool Estimated { get; set; }

        /// <summary>Why the type cannot be placed; empty when it can.</summary>
        public string PlacementProblem { get; set; } = string.Empty;

        /// <summary>Luminous opening along the fixture's axis (ft), 2 when the IES file gives none.</summary>
        public double Length => Size(Photometry?.OpeningLength ?? 0);

        /// <summary>Luminous opening across the fixture's axis (ft), 2 when the IES file gives none.</summary>
        public double Width => Size(Photometry?.OpeningWidth ?? 0);

        /// <summary>A round opening (LM-63 gives its width as negative).</summary>
        public bool Round => (Photometry?.OpeningWidth ?? 0) < 0;

        private static double Size(double value) => Math.Abs(value) > 1e-6 ? Math.Abs(value) : DefaultSize;
    }

    /// <summary>
    /// One room being laid out: the zone and its surroundings read once, the areas being edited,
    /// and the solve that runs only when asked.
    /// </summary>
    public sealed class LayoutSession
    {
        private const double BelowCeiling = 1.0 / 12.0;
        private const int MaxFitSolves = 3;

        private readonly double _gridSpacing;
        private readonly double _boundaryOffset;

        public LayoutSession(CalcZone zone, IReadOnlyList<SceneTriangle> triangles, IReadOnlyList<Luminaire> existing,
            double gridSpacing, double boundaryOffset, IReadOnlyList<Luminaire>? surroundings = null)
        {
            Zone = zone;
            Triangles = triangles;
            Existing = existing;
            Surroundings = surroundings ?? Array.Empty<Luminaire>();
            _gridSpacing = gridSpacing;
            _boundaryOffset = boundaryOffset;
        }

        public CalcZone Zone { get; }

        public IReadOnlyList<SceneTriangle> Triangles { get; }

        /// <summary>Fixtures that were in the room before the session.</summary>
        public IReadOnlyList<Luminaire> Existing { get; }

        /// <summary>Fixtures around the room or in links: always in the calc, never replaced.</summary>
        public IReadOnlyList<Luminaire> Surroundings { get; }

        /// <summary>Leave the existing fixtures out of the calc (they are deleted on Place).</summary>
        public bool ReplaceExisting { get; set; }

        public List<LayoutArea> Areas { get; } = new List<LayoutArea>();

        public Dictionary<string, LayoutFixtureType> Types { get; } = new Dictionary<string, LayoutFixtureType>(StringComparer.Ordinal);

        public IlluminanceUnits Units { get; set; }

        public LightingTarget? Target { get; set; }

        public CalcQuality Quality { get; set; } = CalcQuality.Draft;

        /// <summary>The layout changed since the last solve (or there has been none).</summary>
        public bool Stale { get; private set; } = true;

        /// <summary>A solve has run at least once.</summary>
        public bool HasResults { get; private set; }

        public double LastSeconds { get; private set; }

        public void MarkChanged() => Stale = true;

        public void SetWorkplaneHeight(double feet)
        {
            Zone.WorkplaneHeight = feet;
            Zone.GenerateGrid(_gridSpacing, _boundaryOffset);
            Zone.Illuminance = Array.Empty<double>();
            Zone.Direct = Array.Empty<double>();
            MarkChanged();
        }

        public List<(LayoutArea Area, LayoutSpot Spot)> CurrentSpots()
        {
            var result = new List<(LayoutArea, LayoutSpot)>();
            foreach (LayoutArea area in Areas)
                foreach (LayoutSpot spot in LayoutGenerator.Spots(area, Zone, Areas))
                    result.Add((area, spot));
            return result;
        }

        /// <summary>Every switched-on spot as a luminaire, plus the existing fixtures unless they are being replaced.</summary>
        public List<Luminaire> BuildLuminaires()
        {
            var luminaires = new List<Luminaire>();
            luminaires.AddRange(Surroundings);
            if (!ReplaceExisting) luminaires.AddRange(Existing);
            foreach ((LayoutArea area, LayoutSpot spot) in CurrentSpots())
            {
                if (!spot.On || !Types.TryGetValue(area.TypeKey, out LayoutFixtureType? type)) continue;
                double theta = area.FrameAngle + (area.Rotate90 ? Math.PI / 2 : 0) + type.RotationDegrees * Math.PI / 180;
                var position = new Vec3(spot.X, spot.Y, area.MountingZ - BelowCeiling + type.CenterOffset);
                var nadir = new Vec3(0, 0, type.Aim == "FamilyPositiveZ" ? 1 : -1);
                luminaires.Add(new Luminaire(position, nadir, new Vec3(Math.Cos(theta), Math.Sin(theta), 0), type.Photometry)
                {
                    TypeKey = type.TypeKey,
                    TypeName = type.Name,
                    Multiplier = type.LightLossFactor * type.Multiplier,
                    Watts = type.Watts,
                });
            }
            return luminaires;
        }

        /// <summary>Seeds the area's rows and columns from the lumen-method estimate for the target.</summary>
        public void ApplyEstimate(LayoutArea area)
        {
            double target = TargetFootcandles();
            if (target <= 0 || !Types.TryGetValue(area.TypeKey, out LayoutFixtureType? type)) return;
            int count = LayoutEstimate.CountFor(target, LayoutGenerator.ClaimedArea(area, Zone, Areas), type.Photometry.ComputedLumens,
                type.LightLossFactor * type.Multiplier);
            Rearrange(area, count);
        }

        public void Solve(Action<string>? status = null, CancellationToken cancel = default)
        {
            var watch = Stopwatch.StartNew();
            var solver = new LightingSolver(BuildLuminaires(), SolverOptions.ForQuality(Quality, true));
            solver.Solve(Zone, Triangles, status, cancel);
            LastSeconds = watch.Elapsed.TotalSeconds;
            Stale = false;
            HasResults = true;
        }

        /// <summary>Statistics of the calc points the area claims (inside it and not in a higher-priority area).</summary>
        public ZoneStatistics AreaStatistics(LayoutArea area)
        {
            if (Zone.Illuminance.Length != Zone.Points.Count) return new ZoneStatistics();
            var values = new List<double>();
            for (int i = 0; i < Zone.Points.Count; i++)
            {
                Vec3 p = Zone.Points[i];
                if (!Polygon.Contains(area.Loops, p.X, p.Y)) continue;
                if (Areas.Any(o => !ReferenceEquals(o, area) && o.Priority > area.Priority && Polygon.Contains(o.Loops, p.X, p.Y))) continue;
                values.Add(Zone.Illuminance[i]);
            }
            return ZoneStatistics.From(values);
        }

        /// <summary>
        /// Scales every area's count until the room average meets the target (at most three solves);
        /// returns the solves used, 0 when there is no target.
        /// </summary>
        public int FitToTarget(Action<string>? status = null, CancellationToken cancel = default)
        {
            double target = TargetFootcandles();
            if (target <= 0) return 0;

            int solves = 0;
            Solve(status, cancel);
            solves++;
            if (Zone.Statistics.Average <= 0 && solves < MaxFitSolves)
            {
                foreach (LayoutArea area in Areas) ApplyEstimate(area);
                Solve(status, cancel);
                solves++;
            }
            while (solves < MaxFitSolves && Zone.Statistics.Average > 0 && !LayoutEstimate.CloseEnough(Zone.Statistics.Average, target))
            {
                double achieved = Zone.Statistics.Average;
                List<(LayoutArea Area, LayoutSpot Spot)> spots = CurrentSpots();
                foreach (LayoutArea area in Areas)
                {
                    int on = spots.Count(s => ReferenceEquals(s.Area, area) && s.Spot.On);
                    Rearrange(area, LayoutEstimate.Scaled(Math.Max(1, on), achieved, target));
                }
                Solve(status, cancel);
                solves++;
            }
            return solves;
        }

        /// <summary>The target's minimum average in footcandles; 0 when there is none.</summary>
        public double TargetFootcandles()
        {
            if (Target == null || Target.Average <= 0) return 0;
            return Target.Average / LightingReport.Convert(1, Units);
        }

        public double TotalWatts() => BuildLuminaires().Sum(l => l.Watts);

        public double PowerDensity()
        {
            double area = Zone.FloorArea;
            return area > 1e-6 ? TotalWatts() / area : 0;
        }

        private void Rearrange(LayoutArea area, int count)
        {
            (double width, double height) = LayoutGenerator.FrameSize(area, Zone);
            (int rows, int columns) = LayoutGenerator.Arrange(count, width, height);
            area.Rows = rows;
            area.Columns = columns;
            area.LockSpacingX = false;
            area.LockSpacingY = false;
            area.ArrangementChanged();
        }
    }
}
