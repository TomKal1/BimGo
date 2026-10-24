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
        public double DirectPointsSeconds { get; set; }
        public double DirectPatchesSeconds { get; set; }
        public double FormFactorSeconds { get; set; }
        public double IterationSeconds { get; set; }
        public double GatherSeconds { get; set; }

        public double TotalSeconds =>
            PatchesSeconds + BvhSeconds + DirectPointsSeconds + DirectPatchesSeconds + FormFactorSeconds + IterationSeconds + GatherSeconds;

        public override string ToString() =>
            $"{PointsInsideObstructions} points inside obstructions (left out), {PointsWithoutDirectLight} without direct light; " +
            $"{InputTriangles:N0} triangles -> {Patches:N0} patches of up to {PatchSize:0.##} ft ({PatchesSeconds:0.00}s), BVH {BvhSeconds:0.00}s; " +
            $"{Luminaires} luminaires; direct on {Points:N0} points {DirectPointsSeconds:0.00}s; " +
            $"direct on patches {DirectPatchesSeconds:0.00}s; form factors {FormFactorSeconds:0.00}s; " +
            $"{Iterations} iterations {IterationSeconds:0.00}s; final gather {GatherSeconds:0.00}s; " +
            $"rays: direct {DirectRays:N0}, form factor {FormFactorRays:N0}, gather {GatherRays:N0}; total {TotalSeconds:0.00}s";
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

        /// <summary>
        /// Solves one zone against the triangles around it and fills its illuminance arrays and
        /// statistics.
        /// </summary>
        public void Solve(CalcZone zone, IReadOnlyList<SceneTriangle> triangles,
            Action<string>? status = null, CancellationToken cancel = default)
        {
            int pointCount = zone.Points.Count;
            zone.Illuminance = new double[pointCount];
            zone.Direct = new double[pointCount];
            var stats = new SolveStats { InputTriangles = triangles.Count, Points = pointCount, Luminaires = _luminaires.Count };
            LastStats = stats;
            _directRays = 0;
            var watch = Stopwatch.StartNew();
            double Lap()
            {
                double seconds = watch.Elapsed.TotalSeconds;
                watch.Restart();
                return seconds;
            }

            status?.Invoke($"building patches from {triangles.Count:N0} triangles");
            List<SceneTriangle> patches = BuildPatches(triangles, zone.Reflectances);
            LastPatchCount = patches.Count;
            stats.Patches = patches.Count;
            stats.PatchSize = LastPatchSize;
            stats.PatchesSeconds = Lap();
            status?.Invoke($"building ray-tracing tree over {patches.Count:N0} patches");
            var scene = new TriangleScene(patches);
            stats.BvhSeconds = Lap();
            var reflectance = new double[patches.Count];
            for (int i = 0; i < patches.Count; i++)
            {
                SceneTriangle p = patches[i];
                reflectance[i] = p.Reflectance >= 0 ? p.Reflectance : zone.Reflectances.For(p.Class);
            }

            // Points inside casework, furniture or walls see only the inside of that solid: a ray
            // straight up meets the back of a face. They are left out (NaN) rather than read as 0.
            status?.Invoke("direct light on calculation points");
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
                zone.Direct[i] = DirectIlluminance(scene, origin, normal, reached);
            });
            // Over a stair or balcony the first thing above most points is the flight or floor
            // overhead, not the ceiling: take the highest (the top of the shaft or atrium).
            zone.CeilingZ = zone.OnWalkingSurfaces ? HighestCeiling(above, zone.TopZ) : Median(above);
            stats.PointsInsideObstructions = inside.Count(b => b);
            stats.PointsWithoutDirectLight = zone.Direct.Count(d => !double.IsNaN(d) && d <= 1e-9);
            stats.PointsReachedByLuminaire = reached;
            stats.DirectPointsSeconds = Lap();

            if (!_options.Interreflection || patches.Count == 0)
            {
                Array.Copy(zone.Direct, zone.Illuminance, pointCount);
                // The room's own surfaces still get their direct light, for the report.
                List<(int Index, bool Covered)> own = RoomSurfaces(zone, patches, scene);
                var ownLight = new double[patches.Count];
                Parallel.ForEach(own.Select(o => o.Index), new ParallelOptions { CancellationToken = cancel }, i =>
                {
                    Vec3 n = scene.NormalOf(i);
                    ownLight[i] = DirectIlluminance(scene, patches[i].Centroid + n * SurfaceOffset, n);
                });
                KeepSurfaces(zone, patches, scene, reflectance, ownLight, own);
                Finish(zone);
                LastIterations = 0;
                stats.DirectRays = Interlocked.Read(ref _directRays);
                return;
            }

            // Direct light on patches.
            status?.Invoke($"direct light on {patches.Count} surface patches");
            var directOnPatch = new double[patches.Count];
            var patchCentre = new Vec3[patches.Count];
            var patchNormal = new Vec3[patches.Count];
            for (int i = 0; i < patches.Count; i++)
            {
                patchCentre[i] = patches[i].Centroid;
                patchNormal[i] = scene.NormalOf(i);
            }

            Parallel.For(0, patches.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                directOnPatch[i] = DirectIlluminance(scene, patchCentre[i] + patchNormal[i] * SurfaceOffset, patchNormal[i]);
            });
            stats.DirectPatchesSeconds = Lap();

            // Form factors by ray sampling: which patch each of a patch's rays lands on.
            status?.Invoke("sampling form factors");
            int rays = Math.Max(8, _options.PatchRays);
            var hits = new int[patches.Count * rays];
            Parallel.For(0, patches.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                Vec3 origin = patchCentre[i] + patchNormal[i] * SurfaceOffset;
                Basis(patchNormal[i], out Vec3 t, out Vec3 b);
                uint seed = Hash((uint)i);
                for (int k = 0; k < rays; k++)
                {
                    Vec3 dir = CosineDirection(k, rays, seed, patchNormal[i], t, b);
                    int hit = scene.ClosestHit(origin, dir, 1e6, out _, out bool front);
                    hits[i * rays + k] = front ? hit : -1;
                }
            });
            stats.FormFactorRays = (long)patches.Count * rays;
            stats.FormFactorSeconds = Lap();

            // Gathering iterations (Jacobi): E = E_direct + mean(ρ_hit · E_hit).
            status?.Invoke("solving interreflections");
            var e = (double[])directOnPatch.Clone();
            var next = new double[patches.Count];
            int iteration = 0;
            for (; iteration < _options.MaxIterations; iteration++)
            {
                cancel.ThrowIfCancellationRequested();
                double maxChange = 0;
                double maxValue = 1e-9;
                for (int i = 0; i < patches.Count; i++)
                {
                    double sum = 0;
                    int offset = i * rays;
                    for (int k = 0; k < rays; k++)
                    {
                        int h = hits[offset + k];
                        if (h >= 0) sum += reflectance[h] * e[h];
                    }
                    double value = directOnPatch[i] + sum / rays;
                    next[i] = value;
                    maxChange = Math.Max(maxChange, Math.Abs(value - e[i]));
                    maxValue = Math.Max(maxValue, value);
                }
                var swap = e;
                e = next;
                next = swap;
                if (maxChange / maxValue < _options.Convergence) { iteration++; break; }
            }
            LastIterations = iteration;
            stats.Iterations = iteration;
            stats.IterationSeconds = Lap();

            // Final gather at the calculation points.
            status?.Invoke("final gather");
            int gather = Math.Max(16, _options.GatherRays);
            double[] exitance = new double[patches.Count];
            for (int i = 0; i < patches.Count; i++) exitance[i] = reflectance[i] * e[i];
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
            stats.GatherSeconds = Lap();
            stats.DirectRays = Interlocked.Read(ref _directRays);

            KeepSurfaces(zone, patches, scene, reflectance, e, RoomSurfaces(zone, patches, scene));
            Finish(zone);
        }

        /// <summary>
        /// The patches that belong to the zone: those whose front side looks into its footprint,
        /// between its floor and its ceiling. A point half a foot in front of the patch must be
        /// inside the footprint, which keeps the room's own wall faces and drops their backs, the
        /// neighbours' faces and the top of the ceiling. Patches under furniture are marked covered.
        /// </summary>
        internal static List<(int Index, bool Covered)> RoomSurfaces(CalcZone zone, IReadOnlyList<SceneTriangle> patches, TriangleScene scene)
        {
            const double probe = 0.5;
            double bottom = zone.BottomZ - 0.25; // drops the undersides of things standing on the floor
            double top = (double.IsNaN(zone.CeilingZ) ? Math.Max(zone.TopZ, zone.BottomZ + zone.WorkplaneHeight) : zone.CeilingZ) + 0.25;
            var own = new List<(int, bool)>();
            for (int i = 0; i < patches.Count; i++)
            {
                Vec3 n = scene.NormalOf(i);
                Vec3 p = patches[i].Centroid + n * probe;
                if (p.Z < bottom || p.Z > top) continue;
                if (!zone.ContainsPlan(p.X, p.Y)) continue;
                // A patch covered by something standing on it (floor under a cabinet, wall behind
                // casework) looks into the inside of that solid: its first hit is a back face.
                bool covered = scene.ClosestHit(patches[i].Centroid + n * SurfaceOffset, n, 1e6, out _, out bool front) >= 0 && !front;
                own.Add((i, covered));
            }
            return own;
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
            DirectIlluminance(scene, point, normal, null);

        private double DirectIlluminance(TriangleScene scene, Vec3 point, Vec3 normal, int[]? reached)
        {
            double total = 0;
            long rays = 0;
            for (int li = 0; li < _luminaires.Count; li++)
            {
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
