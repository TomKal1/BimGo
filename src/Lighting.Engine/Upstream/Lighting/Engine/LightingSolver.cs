using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>Solver accuracy presets.</summary>
    public enum CalcQuality
    {
        Draft,
        Normal,
        High,
    }

    public sealed class SolverOptions
    {
        /// <summary>Include light reflected between surfaces (radiosity). Off = direct only.</summary>
        public bool Interreflection { get; set; } = true;

        /// <summary>Largest patch edge for the radiosity mesh (feet).</summary>
        public double PatchSize { get; set; } = 2.0;

        /// <summary>Cosine-weighted rays per patch that sample its surroundings.</summary>
        public int PatchRays { get; set; } = 96;

        /// <summary>Cosine-weighted rays per calculation point for the final gather.</summary>
        public int GatherRays { get; set; } = 256;

        public int MaxIterations { get; set; } = 200;

        /// <summary>Stop when no patch changes by more than this share between iterations.</summary>
        public double Convergence { get; set; } = 0.0002;

        /// <summary>Upper bound on patches per zone; the patch size grows to stay under it.</summary>
        public int MaxPatches { get; set; } = 60000;

        /// <summary>A copy with every property, including ones added later.</summary>
        public SolverOptions Clone() => (SolverOptions)MemberwiseClone();

        public static SolverOptions ForQuality(CalcQuality quality, bool interreflection)
        {
            switch (quality)
            {
                case CalcQuality.Draft:
                    return new SolverOptions { Interreflection = interreflection, PatchSize = 4.0, PatchRays = 48, GatherRays = 96 };
                case CalcQuality.High:
                    return new SolverOptions { Interreflection = interreflection, PatchSize = 1.0, PatchRays = 192, GatherRays = 768 };
                default:
                    return new SolverOptions { Interreflection = interreflection };
            }
        }
    }

    /// <summary>What one zone solve cost, stage by stage (for the timing log).</summary>
    public sealed class SolveStats
    {
        public int InputTriangles { get; set; }
        public int Patches { get; set; }
        public double PatchSize { get; set; }
        public int Points { get; set; }
        public int Luminaires { get; set; }
        public int Iterations { get; set; }

        /// <summary>Calculation points that lie inside an obstruction (casework, furniture, walls): left out.</summary>
        public int PointsInsideObstructions { get; set; }

        /// <summary>Points that receive no direct light from any luminaire.</summary>
        public int PointsWithoutDirectLight { get; set; }

        /// <summary>For each luminaire passed to the solver, how many calculation points it lights directly.</summary>
        public int[] PointsReachedByLuminaire { get; set; } = Array.Empty<int>();
        public long DirectRays { get; set; }
        public long FormFactorRays { get; set; }
        public long GatherRays { get; set; }
        public double PatchesSeconds { get; set; }
        public double BvhSeconds { get; set; }

        /// <summary>Which patches face each zone and whose reflectance each takes.</summary>
        public double SurfaceOwnersSeconds { get; set; }
        public double DirectPointsSeconds { get; set; }
        public double DirectPatchesSeconds { get; set; }
        public double FormFactorSeconds { get; set; }
        public double IterationSeconds { get; set; }
        public double GatherSeconds { get; set; }

        /// <summary>No light reached the zone's points or its own surfaces (an elevator shaft), so the interreflection solve was skipped.</summary>
        public bool NoLightOnSurfaces { get; set; }

        /// <summary>Patches, BVH, direct on patches, form factors and iterations were done once for the zone's cluster, not for this zone alone.</summary>
        public bool ClusterStagesShared { get; set; }

        /// <summary>Of <see cref="DirectRays"/>, the rays of the cluster's direct light on patches.</summary>
        public long ClusterDirectRays { get; set; }

        /// <summary>The cluster's number in the run log; when set, the shared stages read "shared (cluster N)".</summary>
        public int Cluster { get; set; }

        public double TotalSeconds =>
            PatchesSeconds + BvhSeconds + SurfaceOwnersSeconds + DirectPointsSeconds + DirectPatchesSeconds + FormFactorSeconds + IterationSeconds + GatherSeconds;

        public override string ToString()
        {
            bool shared = ClusterStagesShared && Cluster > 0;
            string Time(double seconds) => shared ? $"shared (cluster {Cluster})" : $"{seconds:0.00}s";
            return $"{PointsInsideObstructions} points inside obstructions (left out), {PointsWithoutDirectLight} without direct light; " +
                $"{InputTriangles:N0} triangles -> {Patches:N0} patches of up to {PatchSize:0.##} ft ({Time(PatchesSeconds)}), BVH {Time(BvhSeconds)}; surface owners {Time(SurfaceOwnersSeconds)}; " +
                $"{Luminaires} luminaires; direct on {Points:N0} points {DirectPointsSeconds:0.00}s; " +
                $"direct on patches {Time(DirectPatchesSeconds)}; form factors {Time(FormFactorSeconds)}; " +
                $"{Iterations} iterations {Time(IterationSeconds)}; final gather {GatherSeconds:0.00}s; " +
                (shared
                    ? $"rays: direct {DirectRays - ClusterDirectRays:N0}, form factor shared (cluster {Cluster}), gather {GatherRays:N0}; zone total {DirectPointsSeconds + GatherSeconds:0.00}s"
                    : $"rays: direct {DirectRays:N0}, form factor {FormFactorRays:N0}, gather {GatherRays:N0}; total {TotalSeconds:0.00}s") +
                (NoLightOnSurfaces ? "; no light on the zone's points or surfaces, so interreflection was skipped" : string.Empty);
        }
    }

    /// <summary>
    /// Point-by-point illuminance with diffuse interreflection.
    /// <list type="number">
    /// <item>Direct: for every point and luminaire, I(θ,ψ)·cosξ/d² × LLF, skipped when geometry blocks
    /// the line between them; luminous openings are split into sub-sources inside five times
    /// their largest dimension (near field).</item>
    /// <item>Interreflection: surfaces are split into patches; each patch's direct illuminance seeds a
    /// gathering radiosity solve whose form factors are sampled with cosine-weighted rays
    /// (E = E_direct + mean(ρ·E) over the patches each ray hits), iterated to convergence.</item>
    /// <item>Final gather: each calculation point casts its own cosine-weighted rays and adds the
    /// mean exitance (ρ·E) of the patches they hit.</item>
    /// </list>
    /// Units are feet and lumens, so illuminance comes out in footcandles (lm/ft²).
    /// </summary>
    public sealed class LightingSolver
    {
        private const double SurfaceOffset = 0.01;   // ft, lifts ray origins off their surface
        private const double SourceClearance = 0.05; // ft, stops a ray short of the luminaire

        private readonly IReadOnlyList<Luminaire> _luminaires;
        private readonly SolverOptions _options;

        public LightingSolver(IReadOnlyList<Luminaire> luminaires, SolverOptions options)
        {
            _luminaires = luminaires;
            _options = options;
        }

        /// <summary>Patches used by the last <see cref="Solve"/> (for diagnostics).</summary>
        public int LastPatchCount { get; private set; }

        /// <summary>Iterations used by the last <see cref="Solve"/>.</summary>
        public int LastIterations { get; private set; }

        /// <summary>Stage timings and counts of the last <see cref="Solve"/>.</summary>
        public SolveStats LastStats { get; private set; } = new SolveStats();

        private long _directRays;
        private int[]? _allLuminaires;

        private IReadOnlyList<int> AllLuminaires =>
            _allLuminaires ?? (_allLuminaires = Enumerable.Range(0, _luminaires.Count).ToArray());

        /// <summary>
        /// Solves one zone against the triangles around it and fills its illuminance arrays and
        /// statistics: a cluster of one zone, with one box over all the triangles and every luminaire.
        /// </summary>
        public void Solve(CalcZone zone, IReadOnlyList<SceneTriangle> triangles,
            Action<string>? status = null, CancellationToken cancel = default)
        {
            var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (SceneTriangle t in triangles)
                foreach (Vec3 v in new[] { t.A, t.B, t.C })
                {
                    min = new Vec3(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));
                    max = new Vec3(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
                }
            SharedSolution shared = SolveShared(new[] { zone }, triangles, new[] { (min, max) },
                new[] { AllLuminaires }, new[] { 0 }, status, cancel);
            if (_options.Interreflection && !shared.Skipped && shared.Patches.Count > 0) status?.Invoke("final gather");
            GatherZone(zone, shared, cancel);
        }

        /// <summary>
        /// The cluster stage: patches and BVH over the cluster's triangles, each zone's point probes
        /// and direct light (from the luminaires in its own box), direct light on the patches, form
        /// factors and the gathering iterations. Each zone then calls <see cref="GatherZone"/>.
        /// <paramref name="boxes"/>[i] is a member zone's region box, <paramref name="luminairesPerBox"/>[i]
        /// the luminaires inside it and <paramref name="boxOfZone"/>[k] the box of zone k.
        /// </summary>
        public SharedSolution SolveShared(IReadOnlyList<CalcZone> zones, IReadOnlyList<SceneTriangle> triangles,
            IReadOnlyList<(Vec3 Min, Vec3 Max)> boxes, IReadOnlyList<IReadOnlyList<int>> luminairesPerBox,
            IReadOnlyList<int> boxOfZone, Action<string>? status = null, CancellationToken cancel = default)
        {
            if (zones.Count == 0) throw new ArgumentException("A cluster needs at least one zone.", nameof(zones));
            if (boxes.Count != luminairesPerBox.Count) throw new ArgumentException("One luminaire list per box.", nameof(luminairesPerBox));
            if (boxOfZone.Count != zones.Count) throw new ArgumentException("One box per zone.", nameof(boxOfZone));
            cancel.ThrowIfCancellationRequested();

            var stats = new SolveStats { InputTriangles = triangles.Count, Luminaires = _luminaires.Count };
            var watch = Stopwatch.StartNew();
            double Lap()
            {
                double seconds = watch.Elapsed.TotalSeconds;
                watch.Restart();
                return seconds;
            }

            status?.Invoke($"building patches from {triangles.Count:N0} triangles");
            List<SceneTriangle> patches = BuildPatches(triangles, zones[0].Reflectances);
            LastPatchCount = patches.Count;
            stats.Patches = patches.Count;
            stats.PatchSize = LastPatchSize;
            stats.PatchesSeconds = Lap();
            status?.Invoke($"building ray-tracing tree over {patches.Count:N0} patches");
            var scene = new TriangleScene(patches);
            stats.BvhSeconds = Lap();

            // Each zone's points, lit by the luminaires in its own box only.
            status?.Invoke("direct light on calculation points");
            var states = new Dictionary<CalcZone, ZoneSolveState>();
            for (int k = 0; k < zones.Count; k++)
            {
                CalcZone zone = zones[k];
                IReadOnlyList<int> luminaires = luminairesPerBox[boxOfZone[k]];
                states[zone] = DirectOnPoints(zone, scene, luminaires, cancel);
            }
            Lap(); // timed per zone

            foreach (CalcZone zone in zones)
            {
                ZoneSolveState state = states[zone];
                states[zone] = new ZoneSolveState(state.Inside, RoomSurfaces(zone, patches, scene, cancel), state.Luminaires, state.Stats);
            }

            // A patch with no reflectance of its own takes that of the room it faces; one facing
            // none (a free-standing wall, a neighbour's wall back) takes the nearest room's.
            int[] owner = PatchOwners(zones, patches, scene, cancel);
            (Vec3 Min, Vec3 Max)[] bounds = zones.Select(z => z.PlanBounds).ToArray();
            var reflectance = new double[patches.Count];
            Parallel.For(0, patches.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                SceneTriangle p = patches[i];
                if (p.Reflectance >= 0) { reflectance[i] = p.Reflectance; return; }
                int k = owner[i] >= 0 ? owner[i] : NearestZone(zones, bounds, p.Centroid);
                reflectance[i] = zones[k].Reflectances.For(p.Class);
            });

            var patchCentre = new Vec3[patches.Count];
            var patchNormal = new Vec3[patches.Count];
            for (int i = 0; i < patches.Count; i++)
            {
                patchCentre[i] = patches[i].Centroid;
                patchNormal[i] = scene.NormalOf(i);
            }
            IReadOnlyList<int>[] patchLuminaires = PatchLuminaires(patchCentre, boxes, luminairesPerBox, cancel);
            stats.SurfaceOwnersSeconds = Lap();

            long raysBefore = Interlocked.Read(ref _directRays);
            var directOnPatch = new double[patches.Count];
            if (!_options.Interreflection || patches.Count == 0)
            {
                // The rooms' own surfaces still get their direct light, for the report.
                int[] own = states.Values.SelectMany(s => s.OwnSurfaces.Select(o => o.Index)).Distinct().ToArray();
                Parallel.ForEach(own, new ParallelOptions { CancellationToken = cancel }, i =>
                {
                    directOnPatch[i] = DirectIlluminance(scene, patchCentre[i] + patchNormal[i] * SurfaceOffset, patchNormal[i], patchLuminaires[i], null);
                });
                stats.DirectPatchesSeconds = Lap();
                stats.DirectRays = Interlocked.Read(ref _directRays) - raysBefore;
                LastIterations = 0;
                return Shared(scene, patches, reflectance, owner, directOnPatch, false, stats, states);
            }

            status?.Invoke($"direct light on {patches.Count} surface patches");
            Parallel.For(0, patches.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                directOnPatch[i] = DirectIlluminance(scene, patchCentre[i] + patchNormal[i] * SurfaceOffset, patchNormal[i], patchLuminaires[i], null);
            });
            stats.DirectPatchesSeconds = Lap();
            stats.DirectRays = Interlocked.Read(ref _directRays) - raysBefore;

            // No light on any zone's points or its own surfaces (an elevator shaft, a room whose
            // fixtures are all next door): skip the form factors, which are most of the solve.
            // Lit rooms around it in the margin don't count; what they would reflect in through a
            // doorway, off surfaces no zone owns, is left out.
            if (zones.All(z => z.Direct.All(v => !(v > 1e-9)) && states[z].OwnSurfaces.All(o => !(directOnPatch[o.Index] > 1e-9))))
            {
                stats.NoLightOnSurfaces = true;
                LastIterations = 0;
                return Shared(scene, patches, reflectance, owner, directOnPatch, true, stats, states);
            }

            // Form factors by ray sampling: which patch each of a patch's rays lands on. Small
            // patches (a faucet cut into slivers) carry little flux, so they get fewer rays; the
            // mean over each patch's own rays stays unbiased, only its noise changes.
            status?.Invoke("sampling form factors");
            int maxRays = Math.Max(8, _options.PatchRays);
            // The smallest piece Subdivide splits off: full-size patches get every ray.
            double nominalArea = 0.25 * LastPatchSize * LastPatchSize;
            var rayStart = new int[patches.Count + 1];
            for (int i = 0; i < patches.Count; i++)
                rayStart[i + 1] = rayStart[i] + RaysFor(patches[i].Area, nominalArea, maxRays);
            var hits = new int[rayStart[patches.Count]];
            Parallel.For(0, patches.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                Vec3 origin = patchCentre[i] + patchNormal[i] * SurfaceOffset;
                Basis(patchNormal[i], out Vec3 t, out Vec3 b);
                uint seed = Hash((uint)i);
                int count = rayStart[i + 1] - rayStart[i];
                for (int k = 0; k < count; k++)
                {
                    Vec3 dir = CosineDirection(k, count, seed, patchNormal[i], t, b);
                    int hit = scene.ClosestHit(origin, dir, 1e6, out _, out bool front);
                    hits[rayStart[i] + k] = front ? hit : -1;
                }
            });
            stats.FormFactorRays = rayStart[patches.Count];
            stats.FormFactorSeconds = Lap();

            // Gathering iterations (Jacobi): E = E_direct + mean(ρ_hit · E_hit).
            status?.Invoke("solving interreflections");
            var e = (double[])directOnPatch.Clone();
            var next = new double[patches.Count];
            // Each zone converges to its own tolerance, so a dim room isn't stopped early by a
            // bright one. Patches no zone owns share one last group.
            int groups = zones.Count + 1;
            var group = new int[patches.Count];
            for (int i = 0; i < patches.Count; i++) group[i] = owner[i] >= 0 ? owner[i] : zones.Count;
            var maxChange = new double[groups];
            var maxValue = new double[groups];
            int iteration = 0;
            for (; iteration < _options.MaxIterations; iteration++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int g = 0; g < groups; g++) { maxChange[g] = 0; maxValue[g] = 1e-9; }
                for (int i = 0; i < patches.Count; i++)
                {
                    double sum = 0;
                    int start = rayStart[i];
                    int end = rayStart[i + 1];
                    for (int k = start; k < end; k++)
                    {
                        int h = hits[k];
                        if (h >= 0) sum += reflectance[h] * e[h];
                    }
                    double value = directOnPatch[i] + sum / (end - start);
                    next[i] = value;
                    int gi = group[i];
                    maxChange[gi] = Math.Max(maxChange[gi], Math.Abs(value - e[i]));
                    maxValue[gi] = Math.Max(maxValue[gi], value);
                }
                var swap = e;
                e = next;
                next = swap;
                bool converged = true;
                for (int g = 0; g < groups && converged; g++)
                    if (!(maxChange[g] / maxValue[g] < _options.Convergence)) converged = false;
                if (converged) { iteration++; break; }
            }
            LastIterations = iteration;
            stats.Iterations = iteration;
            stats.IterationSeconds = Lap();
            return Shared(scene, patches, reflectance, owner, e, false, stats, states);
        }

        private static SharedSolution Shared(TriangleScene scene, List<SceneTriangle> patches, double[] reflectance, int[] owner,
            double[] illuminance, bool skipped, SolveStats stats, Dictionary<CalcZone, ZoneSolveState> states)
        {
            var exitance = new double[patches.Count];
            for (int i = 0; i < patches.Count; i++) exitance[i] = reflectance[i] * illuminance[i];
            return new SharedSolution(scene, patches, reflectance, owner, illuminance, exitance, skipped, stats, states);
        }

        /// <summary>The inside/above probe, <see cref="CalcZone.CeilingZ"/> and direct light on one zone's points.</summary>
        private ZoneSolveState DirectOnPoints(CalcZone zone, TriangleScene scene, IReadOnlyList<int> luminaires, CancellationToken cancel)
        {
            int pointCount = zone.Points.Count;
            zone.Direct = new double[pointCount];
            var stats = new SolveStats { Points = pointCount, Luminaires = luminaires.Count };
            long raysBefore = Interlocked.Read(ref _directRays);
            var watch = Stopwatch.StartNew();

            // Points inside casework, furniture or walls see only the inside of that solid: a ray
            // straight up meets the back of a face. They are left out (NaN) rather than read as 0.
            Vec3 normal = zone.PointNormal;
            var inside = new bool[pointCount];
            var reached = new int[_luminaires.Count];
            var above = new double[pointCount];
            Parallel.For(0, pointCount, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                Vec3 origin = zone.Points[i] + normal * SurfaceOffset;
                above[i] = double.NaN;
                if (scene.Count > 0 && scene.ClosestHit(origin, Vec3.UnitZ, 1e6, out double up, out bool front) >= 0)
                {
                    if (!front)
                    {
                        inside[i] = true;
                        zone.Direct[i] = double.NaN;
                        return;
                    }
                    above[i] = origin.Z + up;
                }
                zone.Direct[i] = DirectIlluminance(scene, origin, normal, luminaires, reached);
            });
            // Over a stair or balcony the first thing above most points is the flight or floor
            // overhead, not the ceiling: take the highest (the top of the shaft or atrium).
            zone.CeilingZ = zone.OnWalkingSurfaces ? HighestCeiling(above, zone.TopZ) : Median(above);
            stats.PointsInsideObstructions = inside.Count(b => b);
            stats.PointsWithoutDirectLight = zone.Direct.Count(d => !double.IsNaN(d) && d <= 1e-9);
            stats.PointsReachedByLuminaire = reached;
            stats.DirectRays = Interlocked.Read(ref _directRays) - raysBefore;
            stats.DirectPointsSeconds = watch.Elapsed.TotalSeconds;
            return new ZoneSolveState(inside, new List<(int, bool)>(), luminaires, stats);
        }

        /// <summary>
        /// For each patch, the luminaires of every box its centroid lies in (in index order), or
        /// of all boxes when it lies in none. Patches in the same boxes share one list.
        /// </summary>
        private static IReadOnlyList<int>[] PatchLuminaires(Vec3[] centres, IReadOnlyList<(Vec3 Min, Vec3 Max)> boxes,
            IReadOnlyList<IReadOnlyList<int>> luminairesPerBox, CancellationToken cancel)
        {
            const double tolerance = 1e-6;
            var result = new IReadOnlyList<int>[centres.Length];
            var byBoxes = new Dictionary<string, IReadOnlyList<int>>();
            var inBoxes = new List<int>();
            for (int i = 0; i < centres.Length; i++)
            {
                if (i % 4096 == 0) cancel.ThrowIfCancellationRequested();
                Vec3 c = centres[i];
                inBoxes.Clear();
                for (int b = 0; b < boxes.Count; b++)
                {
                    (Vec3 min, Vec3 max) = boxes[b];
                    if (c.X >= min.X - tolerance && c.X <= max.X + tolerance &&
                        c.Y >= min.Y - tolerance && c.Y <= max.Y + tolerance &&
                        c.Z >= min.Z - tolerance && c.Z <= max.Z + tolerance)
                        inBoxes.Add(b);
                }
                // A patch in no box (part of a slab or long wall that only overlaps a box) takes
                // every box's luminaires: with one box that is the single-zone rule.
                if (inBoxes.Count == 0)
                    for (int b = 0; b < boxes.Count; b++) inBoxes.Add(b);
                string key = string.Join(",", inBoxes);
                if (!byBoxes.TryGetValue(key, out IReadOnlyList<int>? luminaires))
                {
                    luminaires = inBoxes.SelectMany(b => luminairesPerBox[b]).Distinct().OrderBy(l => l).ToArray();
                    byBoxes[key] = luminaires;
                }
                result[i] = luminaires;
            }
            return result;
        }

        /// <summary>
        /// The per-zone stage: the final gather from the zone's points against the cluster's
        /// converged exitance, its own surfaces and its statistics. <see cref="LastStats"/> becomes
        /// the zone's stats with the cluster's stages copied in and marked shared.
        /// </summary>
        public void GatherZone(CalcZone zone, SharedSolution shared, CancellationToken cancel = default)
        {
            if (!shared.Zones.TryGetValue(zone, out ZoneSolveState? state))
                throw new ArgumentException("The zone is not part of this solve.", nameof(zone));

            int pointCount = zone.Points.Count;
            zone.Illuminance = new double[pointCount];
            SolveStats stats = state.Stats;
            var watch = Stopwatch.StartNew();

            if (!_options.Interreflection || shared.Patches.Count == 0 || shared.Skipped)
            {
                Array.Copy(zone.Direct, zone.Illuminance, pointCount);
            }
            else
            {
                int gather = Math.Max(16, _options.GatherRays);
                TriangleScene scene = shared.Scene;
                double[] exitance = shared.Exitance;
                bool[] inside = state.Inside;
                Vec3 normal = zone.PointNormal;
                Basis(normal, out Vec3 nt, out Vec3 nb);
                Parallel.For(0, pointCount, new ParallelOptions { CancellationToken = cancel }, i =>
                {
                    if (inside[i])
                    {
                        zone.Illuminance[i] = double.NaN;
                        return;
                    }
                    Vec3 origin = zone.Points[i] + normal * SurfaceOffset;
                    uint seed = Hash((uint)i ^ 0x9e3779b9u);
                    double sum = 0;
                    for (int k = 0; k < gather; k++)
                    {
                        Vec3 dir = CosineDirection(k, gather, seed, normal, nt, nb);
                        int hit = scene.ClosestHit(origin, dir, 1e6, out _, out bool front);
                        if (hit >= 0 && front) sum += exitance[hit];
                    }
                    zone.Illuminance[i] = zone.Direct[i] + sum / gather;
                });
                stats.GatherRays = (long)pointCount * gather;
            }
            stats.GatherSeconds = watch.Elapsed.TotalSeconds;

            KeepSurfaces(zone, shared.Patches, shared.Scene, shared.Reflectance, shared.Illuminance, state.OwnSurfaces);
            Finish(zone);
            LastStats = WithClusterStages(stats, shared.ClusterStats);
        }

        /// <summary>A zone's stats with the cluster's stages copied in, so today's log line still reads whole.</summary>
        private static SolveStats WithClusterStages(SolveStats zone, SolveStats cluster) => new SolveStats
        {
            InputTriangles = cluster.InputTriangles,
            Patches = cluster.Patches,
            PatchSize = cluster.PatchSize,
            Points = zone.Points,
            Luminaires = zone.Luminaires,
            Iterations = cluster.Iterations,
            PointsInsideObstructions = zone.PointsInsideObstructions,
            PointsWithoutDirectLight = zone.PointsWithoutDirectLight,
            PointsReachedByLuminaire = zone.PointsReachedByLuminaire,
            DirectRays = zone.DirectRays + cluster.DirectRays,
            ClusterDirectRays = cluster.DirectRays,
            FormFactorRays = cluster.FormFactorRays,
            GatherRays = zone.GatherRays,
            PatchesSeconds = cluster.PatchesSeconds,
            BvhSeconds = cluster.BvhSeconds,
            SurfaceOwnersSeconds = cluster.SurfaceOwnersSeconds,
            DirectPointsSeconds = zone.DirectPointsSeconds,
            DirectPatchesSeconds = cluster.DirectPatchesSeconds,
            FormFactorSeconds = cluster.FormFactorSeconds,
            IterationSeconds = cluster.IterationSeconds,
            GatherSeconds = zone.GatherSeconds,
            NoLightOnSurfaces = cluster.NoLightOnSurfaces,
            ClusterStagesShared = true,
        };

        /// <summary>
        /// The patches that belong to the zone: those whose front side looks into its footprint,
        /// between its floor and its ceiling. A point half a foot in front of the patch must be
        /// inside the footprint, which keeps the room's own wall faces and drops their backs, the
        /// neighbours' faces and the top of the ceiling. Patches under furniture are marked covered.
        /// </summary>
        internal static List<(int Index, bool Covered)> RoomSurfaces(CalcZone zone, IReadOnlyList<SceneTriangle> patches, TriangleScene scene,
            CancellationToken cancel = default)
        {
            const double probe = 0.5;
            (double bottom, double top) = FaceRange(zone);
            (Vec3 Min, Vec3 Max) bounds = zone.PlanBounds;
            // Per patch: 0 not the zone's, 1 the zone's, 2 the zone's and covered; collected in index order.
            var found = new byte[patches.Count];
            Parallel.For(0, patches.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                Vec3 n = scene.NormalOf(i);
                Vec3 p = patches[i].Centroid + n * probe;
                if (p.Z < bottom || p.Z > top) return;
                if (!InPlan(zone, bounds, p)) return;
                // A patch covered by something standing on it (floor under a cabinet, wall behind
                // casework) looks into the inside of that solid: its first hit is a back face.
                bool covered = scene.ClosestHit(patches[i].Centroid + n * SurfaceOffset, n, 1e6, out _, out bool front) >= 0 && !front;
                found[i] = covered ? (byte)2 : (byte)1;
            });
            var own = new List<(int, bool)>();
            for (int i = 0; i < found.Length; i++)
                if (found[i] != 0) own.Add((i, found[i] == 2));
            return own;
        }

        /// <summary>Whether a point lies in the zone's plan; its plan bounds reject most points cheaply.</summary>
        private static bool InPlan(CalcZone zone, (Vec3 Min, Vec3 Max) bounds, Vec3 p) =>
            p.X >= bounds.Min.X && p.X <= bounds.Max.X && p.Y >= bounds.Min.Y && p.Y <= bounds.Max.Y &&
            zone.ContainsPlan(p.X, p.Y);

        /// <summary>The heights a patch's probe point must lie between to face the zone: floor to ceiling, with a quarter foot of play.</summary>
        private static (double Bottom, double Top) FaceRange(CalcZone zone) =>
            (zone.BottomZ - 0.25, // drops the undersides of things standing on the floor
             (double.IsNaN(zone.CeilingZ) ? Math.Max(zone.TopZ, zone.BottomZ + zone.WorkplaneHeight) : zone.CeilingZ) + 0.25);

        /// <summary>
        /// The zone (index) each patch faces, or -1. A patch faces a zone when the point half a foot
        /// in front of it lies in the zone's plan between its floor and ceiling, as for
        /// <see cref="RoomSurfaces"/>. When several zones hold the point, the one whose floor under
        /// it is highest wins, then the smallest (the rule of <see cref="FixtureZoneCheck.Owner"/>).
        /// </summary>
        internal static int[] PatchOwners(IReadOnlyList<CalcZone> zones, IReadOnlyList<SceneTriangle> patches, TriangleScene scene,
            CancellationToken cancel = default)
        {
            const double probe = 0.5;
            var ranges = new (double Bottom, double Top)[zones.Count];
            var bounds = new (Vec3 Min, Vec3 Max)[zones.Count];
            for (int k = 0; k < zones.Count; k++)
            {
                ranges[k] = FaceRange(zones[k]);
                bounds[k] = zones[k].PlanBounds;
            }
            var owner = new int[patches.Count];
            Parallel.For(0, patches.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                Vec3 p = patches[i].Centroid + scene.NormalOf(i) * probe;
                int best = -1;
                double bestFloor = 0;
                for (int k = 0; k < zones.Count; k++)
                {
                    if (p.Z < ranges[k].Bottom || p.Z > ranges[k].Top || !InPlan(zones[k], bounds[k], p)) continue;
                    double floor = FixtureZoneCheck.FloorUnder(zones[k], p.Z);
                    if (best < 0 || floor > bestFloor || (floor == bestFloor && zones[k].Area < zones[best].Area))
                    {
                        best = k;
                        bestFloor = floor;
                    }
                }
                owner[i] = best;
            });
            return owner;
        }

        /// <summary>The zone (index) whose footprint is nearest a point in plan; 0 inside it.</summary>
        private static int NearestZone(IReadOnlyList<CalcZone> zones, (Vec3 Min, Vec3 Max)[] bounds, Vec3 point)
        {
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int k = 0; k < zones.Count; k++)
            {
                double d = InPlan(zones[k], bounds[k], point) ? 0 : Polygon.DistanceToBoundary(zones[k].Loops, point.X, point.Y);
                if (d < bestDistance)
                {
                    best = k;
                    bestDistance = d;
                }
            }
            return best;
        }

        private static void KeepSurfaces(CalcZone zone, IReadOnlyList<SceneTriangle> patches, TriangleScene scene,
            double[] reflectance, double[] illuminance, List<(int Index, bool Covered)> own)
        {
            zone.Surfaces.Clear();
            foreach ((int i, bool covered) in own)
                zone.Surfaces.Add(new SurfacePatch(patches[i], scene.NormalOf(i), reflectance[i], illuminance[i], covered));
        }

        /// <summary>The highest surface found over the points, at most a foot over the zone's top; the top when none.</summary>
        private static double HighestCeiling(double[] values, double top)
        {
            double[] known = values.Where(v => !double.IsNaN(v)).ToArray();
            return known.Length == 0 ? top : Math.Min(known.Max(), top + 1);
        }

        private static double Median(double[] values)
        {
            double[] known = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
            return known.Length == 0 ? double.NaN : known[known.Length / 2];
        }

        private static void Finish(CalcZone zone)
        {
            zone.Statistics = ZoneStatistics.From(zone.Illuminance);
            zone.DirectStatistics = ZoneStatistics.From(zone.Direct);
        }

        /// <summary>Direct illuminance (fc) on a surface at <paramref name="point"/> facing <paramref name="normal"/>.</summary>
        public double DirectIlluminance(TriangleScene scene, Vec3 point, Vec3 normal) =>
            DirectIlluminance(scene, point, normal, AllLuminaires, null);

        /// <summary>Direct illuminance from the given luminaires only (indexes into the solver's list).</summary>
        private double DirectIlluminance(TriangleScene scene, Vec3 point, Vec3 normal, IReadOnlyList<int> luminaires, int[]? reached)
        {
            double total = 0;
            long rays = 0;
            for (int n = 0; n < luminaires.Count; n++)
            {
                int li = luminaires[n];
                Luminaire lum = _luminaires[li];
                Vec3 toCentre = lum.Position - point;
                double centreDistance = toCentre.Length;
                if (centreDistance < 1e-6) continue;

                IReadOnlyList<Vec3> sources = centreDistance < lum.NearFieldDistance
                    ? lum.SubSources
                    : lum.CentreOnly;
                double share = 1.0 / sources.Count;

                double sum = 0;
                foreach (Vec3 source in sources)
                {
                    Vec3 d = source - point;
                    double r2 = d.LengthSquared;
                    if (r2 < 1e-8) continue;
                    double r = Math.Sqrt(r2);
                    Vec3 dir = d / r;
                    double cosIncidence = Vec3.Dot(normal, dir);
                    if (cosIncidence <= 0) continue;
                    double intensity = lum.IntensityToward(-dir);
                    if (intensity <= 0) continue;
                    rays++;
                    if (scene.Count > 0 && scene.AnyHit(point, dir, r - SourceClearance)) continue;
                    sum += intensity * cosIncidence / r2;
                }
                total += sum * share * lum.Multiplier;
                if (reached != null && sum > 0) Interlocked.Increment(ref reached[li]);
            }
            if (rays > 0) Interlocked.Add(ref _directRays, rays);
            return total;
        }

        /// <summary>
        /// Splits triangles along their longest edge until no edge exceeds the patch size, growing
        /// the patch size if the zone would exceed <see cref="SolverOptions.MaxPatches"/>.
        /// </summary>
        private List<SceneTriangle> BuildPatches(IReadOnlyList<SceneTriangle> triangles, Reflectances reflectances)
        {
            // Split by area, not just edge length: long thin triangles (slab and wall edges,
            // joists, mullions) are split only while each piece keeps a useful area, so they add a
            // handful of patches instead of hundreds of slivers. If the zone still has too many
            // patches, grow the patch size and try again.
            double size = Math.Max(0.25, _options.PatchSize);
            double totalArea = triangles.Sum(t => t.Area);
            double estimated = triangles.Count + totalArea / (0.25 * size * size);
            if (estimated > _options.MaxPatches)
                size *= Math.Sqrt(estimated / _options.MaxPatches);

            List<SceneTriangle> output = Subdivide(triangles, size);
            for (int attempt = 0; attempt < 6 && output.Count > _options.MaxPatches; attempt++)
            {
                if (triangles.Count >= _options.MaxPatches) break; // can't go below one patch per triangle
                size *= Math.Max(1.2, Math.Sqrt((double)output.Count / _options.MaxPatches));
                output = Subdivide(triangles, size);
            }
            LastPatchSize = size;
            return output;
        }

        /// <summary>Fewest form-factor rays any patch gets.</summary>
        public const int MinPatchRays = 12;

        /// <summary>Form-factor rays for a patch: the maximum at the nominal area, fewer in proportion for smaller patches, never below <see cref="MinPatchRays"/> (or the maximum, if smaller).</summary>
        internal static int RaysFor(double area, double nominalArea, int maxRays)
        {
            int rays = (int)Math.Round(maxRays * area / nominalArea);
            return Math.Max(Math.Min(MinPatchRays, maxRays), Math.Min(maxRays, rays));
        }

        /// <summary>The patch size the last solve ended up using (ft).</summary>
        public double LastPatchSize { get; private set; }

        internal static List<SceneTriangle> Subdivide(IReadOnlyList<SceneTriangle> triangles, double size)
        {
            var output = new List<SceneTriangle>(triangles.Count * 2);
            var stack = new Stack<SceneTriangle>();
            double limit2 = size * size;
            double minArea = 0.25 * size * size;
            foreach (SceneTriangle triangle in triangles)
            {
                if (triangle.Area < 1e-8) continue;
                stack.Push(triangle);
                while (stack.Count > 0)
                {
                    SceneTriangle t = stack.Pop();
                    double ab = (t.B - t.A).LengthSquared;
                    double bc = (t.C - t.B).LengthSquared;
                    double ca = (t.A - t.C).LengthSquared;
                    double longest = Math.Max(ab, Math.Max(bc, ca));
                    // Halving would leave pieces under the minimum area: keep it whole.
                    if (longest <= limit2 || t.Area < 2 * minArea)
                    {
                        output.Add(t);
                        continue;
                    }

                    if (ab >= bc && ab >= ca)
                    {
                        Vec3 m = (t.A + t.B) * 0.5;
                        stack.Push(new SceneTriangle(t.A, m, t.C, t.Class, t.Reflectance, t.Tag, t.Colour));
                        stack.Push(new SceneTriangle(m, t.B, t.C, t.Class, t.Reflectance, t.Tag, t.Colour));
                    }
                    else if (bc >= ca)
                    {
                        Vec3 m = (t.B + t.C) * 0.5;
                        stack.Push(new SceneTriangle(t.A, t.B, m, t.Class, t.Reflectance, t.Tag, t.Colour));
                        stack.Push(new SceneTriangle(t.A, m, t.C, t.Class, t.Reflectance, t.Tag, t.Colour));
                    }
                    else
                    {
                        Vec3 m = (t.C + t.A) * 0.5;
                        stack.Push(new SceneTriangle(t.A, t.B, m, t.Class, t.Reflectance, t.Tag, t.Colour));
                        stack.Push(new SceneTriangle(m, t.B, t.C, t.Class, t.Reflectance, t.Tag, t.Colour));
                    }
                }
            }
            return output;
        }

        private static void Basis(Vec3 n, out Vec3 t, out Vec3 b)
        {
            Vec3 helper = Math.Abs(n.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX;
            t = Vec3.Cross(helper, n).Normalized();
            b = Vec3.Cross(n, t);
        }

        /// <summary>
        /// Sample k of n on the cosine-weighted hemisphere around n: a Hammersley point,
        /// rotated per caller by a hashed offset so neighbouring points don't alias.
        /// </summary>
        private static Vec3 CosineDirection(int k, int n, uint seed, Vec3 normal, Vec3 t, Vec3 b)
        {
            double u1 = (k + 0.5) / n;
            double u2 = RadicalInverse((uint)k);
            u1 = Frac(u1 + (seed & 0xffff) / 65536.0);
            u2 = Frac(u2 + (seed >> 16) / 65536.0);
            double r = Math.Sqrt(u1);
            double phi = 2 * Math.PI * u2;
            double x = r * Math.Cos(phi);
            double y = r * Math.Sin(phi);
            double z = Math.Sqrt(Math.Max(0, 1 - u1));
            return t * x + b * y + normal * z;
        }

        private static double Frac(double v) => v - Math.Floor(v);

        private static double RadicalInverse(uint bits)
        {
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            return bits * 2.3283064365386963e-10;
        }

        private static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }
    }
}
