using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace BimGo.Calculations;

/// <summary>
/// The storey-cluster solve, ported from WWTools' LightingCalcRunner.SolveInClusters without Revit or a
/// progress window: one interreflection solve per <see cref="ZoneClusters"/> cluster, over the triangles
/// overlapping any member's region box and the luminaires inside any of them, then each member's final gather.
/// </summary>
static class NativeClusterSolve
{
    public sealed record ZoneResult(SolveStats Stats, IReadOnlyList<Luminaire> Luminaires, int Triangles);

    /// <summary>
    /// Solves every zone. <paramref name="complete"/> is false when a cluster stopped at the iteration cap.
    /// The cluster patch cap stays at <paramref name="options"/>' MaxPatches: BimGo sizes it for the whole exported domain.
    /// </summary>
    public static Dictionary<CalcZone, ZoneResult> Solve(IReadOnlyList<CalcZone> zones, IReadOnlyList<Luminaire> luminaires,
        IReadOnlyList<SceneTriangle> triangles, double margin, SolverOptions options, CalcLog log, List<string> warnings,
        Action<string> progress, CancellationToken cancel, out bool complete)
    {
        complete = true;
        var results = new Dictionary<CalcZone, ZoneResult>();
        if (zones.Count == 0) return results;
        List<ZoneCluster> clusters = ZoneClusters.Build(zones, margin, boxes => CountOverlapping(triangles, boxes));
        int done = 0;
        for (int c = 0; c < clusters.Count; c++)
        {
            cancel.ThrowIfCancellationRequested();
            ZoneCluster cluster = clusters[c];
            List<(Vec3 Min, Vec3 Max)> boxes = cluster.Boxes;

            // Triangles overlapping any member box, counting each box's own.
            (Vec3 Min, Vec3 Max) bound = Bound(boxes);
            var clusterTriangles = new List<SceneTriangle>();
            var trianglesInBox = new int[boxes.Count];
            foreach (SceneTriangle t in triangles)
            {
                if (!Overlaps(t, bound.Min, bound.Max)) continue;
                bool any = false;
                for (int b = 0; b < boxes.Count; b++)
                {
                    if (!Overlaps(t, boxes[b].Min, boxes[b].Max)) continue;
                    trianglesInBox[b]++;
                    any = true;
                }
                if (any) clusterTriangles.Add(t);
            }

            // Luminaires inside any member box, in the run's order; each box keeps its own list.
            var clusterLuminaires = new List<Luminaire>();
            var perBox = new List<int>[boxes.Count];
            for (int b = 0; b < boxes.Count; b++) perBox[b] = new List<int>();
            foreach (Luminaire l in luminaires)
            {
                int index = -1;
                for (int b = 0; b < boxes.Count; b++)
                {
                    if (!Inside(l.Position, boxes[b].Min, boxes[b].Max)) continue;
                    if (index < 0)
                    {
                        index = clusterLuminaires.Count;
                        clusterLuminaires.Add(l);
                    }
                    perBox[b].Add(index);
                }
            }

            var solver = new LightingSolver(clusterLuminaires, options);
            string label = $"Cluster {c + 1}/{clusters.Count} ({cluster.Zones.Count} zone(s))";
            SharedSolution shared = solver.SolveShared(cluster.Zones, clusterTriangles, boxes,
                perBox.Select(p => (IReadOnlyList<int>)p).ToList(), Enumerable.Range(0, cluster.Zones.Count).ToArray(),
                stage => progress?.Invoke($"{label}: {stage}"), cancel);
            SolveStats s = shared.ClusterStats;
            log.Write($"{label}: {string.Join(", ", cluster.Zones.Select(z => z.DisplayName))}; {s.InputTriangles:N0} triangles -> " +
                      $"{s.Patches:N0} patches of up to {s.PatchSize:0.##} ft, {s.Luminaires} luminaires, {s.Iterations} iterations" +
                      (s.NoLightOnSurfaces ? "; no light on any zone's points or surfaces, so interreflection was skipped" : string.Empty));
            if (cluster.OverLimit || s.Patches > ZoneClusters.PatchLimit)
                warnings.Add($"Cluster {c + 1} is over the patch limit ({s.Patches:N0} patches); it was solved whole.");
            if (options.Interreflection && !shared.Skipped && s.Iterations >= options.MaxIterations) complete = false;

            for (int k = 0; k < cluster.Zones.Count; k++)
            {
                cancel.ThrowIfCancellationRequested();
                CalcZone zone = cluster.Zones[k];
                progress?.Invoke($"{zone.DisplayName} ({++done}/{zones.Count}): final gather");
                solver.GatherZone(zone, shared, cancel);

                // The zone's own luminaires, with its reached counts in their order.
                List<Luminaire> zoneLuminaires = perBox[k].Select(j => clusterLuminaires[j]).ToList();
                SolveStats stats = solver.LastStats;
                int[] reached = stats.PointsReachedByLuminaire;
                stats.PointsReachedByLuminaire = perBox[k].Select(j => j < reached.Length ? reached[j] : 0).ToArray();
                stats.Cluster = c + 1;
                log.Write($"  {zone.DisplayName}: {zone.Points.Count} points, {zoneLuminaires.Count} luminaires, {trianglesInBox[k]:N0} triangles in region; {stats}");
                results[zone] = new ZoneResult(stats, zoneLuminaires, trianglesInBox[k]);
            }
        }
        return results;
    }

    /// <summary>Triangles overlapping any of the boxes (what <see cref="ZoneClusters.Build"/> weighs a group by).</summary>
    static int CountOverlapping(IReadOnlyList<SceneTriangle> triangles, IReadOnlyList<(Vec3 Min, Vec3 Max)> boxes)
    {
        (Vec3 Min, Vec3 Max) bound = Bound(boxes);
        int count = 0;
        foreach (SceneTriangle t in triangles)
        {
            if (!Overlaps(t, bound.Min, bound.Max)) continue;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (!Overlaps(t, boxes[b].Min, boxes[b].Max)) continue;
                count++;
                break;
            }
        }
        return count;
    }

    static (Vec3 Min, Vec3 Max) Bound(IReadOnlyList<(Vec3 Min, Vec3 Max)> boxes)
    {
        Vec3 min = boxes[0].Min, max = boxes[0].Max;
        foreach ((Vec3 bmin, Vec3 bmax) in boxes)
        {
            min = Vec3.Min(min, bmin);
            max = Vec3.Max(max, bmax);
        }
        return (min, max);
    }

    public static bool Inside(Vec3 p, Vec3 min, Vec3 max) =>
        p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z;

    static bool Overlaps(SceneTriangle t, Vec3 min, Vec3 max)
    {
        Vec3 tmin = Vec3.Min(t.A, Vec3.Min(t.B, t.C));
        Vec3 tmax = Vec3.Max(t.A, Vec3.Max(t.B, t.C));
        return tmax.X >= min.X && tmax.Y >= min.Y && tmax.Z >= min.Z && tmin.X <= max.X && tmin.Y <= max.Y && tmin.Z <= max.Z;
    }
}
