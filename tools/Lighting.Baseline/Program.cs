using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lighting.Engine;
using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace Lighting.Baseline;

public static class Program
{
    public static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new ArgumentException("Usage: Lighting.Baseline capture|verify|benchmark <corpus-directory> [output.json] [repeats=20], or exchange <results.json> <audit.json>");
            string mode = args[0], dir = Path.GetFullPath(args[1]);
            if (mode == "exchange") { Exchange.Check(dir, args[2]); return 0; }
            if (mode == "capture")
            {
                Directory.CreateDirectory(dir);
                foreach (SceneCase scene in SceneCase.Corpus())
                {
                    string path = Path.Combine(dir, scene.Name + ".scene.json");
                    // Baselines are deliberate artifacts: never overwrite an existing golden implicitly.
                    if (File.Exists(path) || File.Exists(Path.Combine(dir, scene.Name + ".golden.json")))
                        throw new IOException("Capture requires an empty corpus directory.");
                    Write(path, scene);
                    var run = Solve(scene);
                    Write(Path.Combine(dir, scene.Name + ".golden.json"), new Golden
                    { InputSha256 = Hash(File.ReadAllBytes(path)), Result = Snapshot(run.Zone, run.Solver), UpstreamCommit = EngineIdentity.UpstreamCommit });
                    Console.WriteLine($"Captured {scene.Name}: {run.Zone.Points.Count} points, {run.Solver.LastPatchCount} patches, {run.Solver.LastIterations} iterations");
                }
            }
            else if (mode is "verify" or "benchmark")
            {
                string[] paths = Directory.GetFiles(dir, "*.scene.json").Order().ToArray();
                Require(paths.Length > 0, "No replay scenes found.");
                var reports = new List<object>();
                foreach (string path in paths)
                {
                    var scene = Read<SceneCase>(path);
                    var golden = Read<Golden>(path.Replace(".scene.json", ".golden.json"));
                    Require(golden.InputSha256 == Hash(File.ReadAllBytes(path)), "Golden input hash mismatch: " + path);
                    Require(golden.UpstreamCommit == EngineIdentity.UpstreamCommit, "Golden upstream mismatch.");
                    var run = Solve(scene);
                    Compare(golden.Result, Snapshot(run.Zone, run.Solver), scene.Name);
                    if (mode == "benchmark")
                    {
                        int repeats = args.Length > 3 ? int.Parse(args[3]) : 20;
                        Require(repeats >= 2, "At least two repetitions are required.");
                        reports.Add(Measure(scene, repeats));
                    }
                    Console.WriteLine($"Verified {scene.Name}: average {run.Zone.Statistics.Average:F9} fc; {run.Solver.LastIterations} iterations");
                }
                ValidatePhysics();
                ValidateEdits(Read<SceneCase>(Path.Combine(dir, "small-room.scene.json")));
                if (mode == "benchmark")
                {
                    Require(args.Length > 2, "Benchmark requires an output file.");
                    Write(args[2], new
                    {
                        EngineIdentity.Version, EngineIdentity.PhysicalModel, EngineIdentity.UpstreamCommit,
                        Utc = DateTime.UtcNow, Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                        Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
                        Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                        Kind = "Synthetic CPU harness; sequential scenarios with engine internal parallelism. No UI/GPU or Revit parity claim.",
                        Reports = reports,
                        Convergence = Convergence(Read<SceneCase>(Path.Combine(dir, "small-room.scene.json")))
                    });
                }
            }
            else throw new ArgumentException("Unknown mode: " + mode);
            Console.WriteLine("All requested checks passed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!;
    public static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + "\n");
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static SceneCase Clone(SceneCase s) => JsonSerializer.Deserialize<SceneCase>(JsonSerializer.Serialize(s, Json), Json)!;

    public static (CalcZone Zone, LightingSolver Solver) Solve(SceneCase scene)
    {
        var input = scene.Build();
        var solver = new LightingSolver(input.Sources, scene.Options);
        solver.Solve(input.Zone, input.Triangles);
        return (input.Zone, solver);
    }

    static Result Snapshot(CalcZone z, LightingSolver solver) => new()
    {
        Direct = z.Direct, Total = z.Illuminance, Patches = solver.LastPatchCount, Iterations = solver.LastIterations,
        Points = z.Points.SelectMany(V).ToArray(),
        Surfaces = z.Surfaces.SelectMany(p => V(p.A).Concat(V(p.B)).Concat(V(p.C)).Concat(V(p.Normal))
            .Concat(new[] { p.Reflectance, p.Illuminance, (double)p.Class, p.Covered ? 1.0 : 0 })).ToArray(),
        Statistics = new[] { (double)z.Statistics.Count, z.Statistics.Average, z.Statistics.Minimum, z.Statistics.Maximum,
            z.Statistics.AverageToMinimum, z.Statistics.MaximumToMinimum, z.CeilingZ },
        Inside = solver.LastStats.PointsInsideObstructions
    };
    static double[] V(Vec3 v) => new[] { v.X, v.Y, v.Z };

    static void Compare(Result a, Result b, string label)
    {
        Require(a.Patches == b.Patches && a.Iterations == b.Iterations && a.Inside == b.Inside, label + ": counts differ");
        Equal(a.Direct, b.Direct, label + " direct"); Equal(a.Total, b.Total, label + " total");
        Equal(a.Points, b.Points, label + " grid"); Equal(a.Surfaces, b.Surfaces, label + " surfaces");
        Equal(a.Statistics, b.Statistics, label + " statistics");
    }
    public static void Equal(double[] a, double[] b, string label)
    {
        Require(a.Length == b.Length, label + ": lengths differ");
        for (int i = 0; i < a.Length; i++)
            Require(a[i].Equals(b[i]) || (double.IsFinite(a[i]) && double.IsFinite(b[i]) &&
                Math.Abs(a[i] - b[i]) <= 1e-10 + 1e-10 * Math.Max(Math.Abs(a[i]), Math.Abs(b[i]))), $"{label}[{i}]: {a[i]:R} != {b[i]:R}");
    }
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    static void ValidatePhysics()
    {
        var p = IesPhotometry.Parse("IESNA:LM-63-2002\nTILT=NONE\n1 -1 1 2 1 1 1 0 0 0\n1 1 10\n0 180\n0\n100 100\n");
        var lum = new Luminaire(new(0, 0, 10), -Vec3.UnitZ, Vec3.UnitX, p);
        var solver = new LightingSolver(new[] { lum }, new SolverOptions { Interreflection = false });
        var empty = new TriangleScene(Array.Empty<SceneTriangle>());
        Equal(new[] { 1.0, 0.25, 100 / 200.0 / Math.Sqrt(2) }, new[]
        { solver.DirectIlluminance(empty, Vec3.Zero, Vec3.UnitZ), solver.DirectIlluminance(empty, new(0,0,-10), Vec3.UnitZ),
            solver.DirectIlluminance(empty, new(10,0,0), Vec3.UnitZ) }, "inverse square/cosine");
        lum.Multiplier = 0.5;
        Equal(new[] { 0.5 }, new[] { solver.DirectIlluminance(empty, Vec3.Zero, Vec3.UnitZ) }, "LLF");
        var shelf = new TriangleScene(new[] { new SceneTriangle(new(-10,-10,5), new(10,-10,5), new(0,10,5), SurfaceClass.Furniture) });
        Require(solver.DirectIlluminance(shelf, Vec3.Zero, Vec3.UnitZ) == 0, "Obstruction failed");
        var cube = SceneCase.Room("energy-cube", 20, 20, 20);
        cube.Workplane = 0; cube.Reflectances = new() { Ceiling = .6, Wall = .6, Floor = .6 };
        var input = cube.Build();
        var iso = new Luminaire(new(10,10,10), -Vec3.UnitZ, Vec3.UnitX, IesPhotometry.Isotropic(10000, "explicit analytic input"));
        new LightingSolver(new[] { iso }, new SolverOptions { PatchRays = 128, GatherRays = 512 }).Solve(input.Zone, input.Triangles);
        double theory = 10000 * .6 / (2400 * .4);
        double indirect = input.Zone.Illuminance.Zip(input.Zone.Direct, (t,d) => t-d).Average();
        Require(Math.Abs(indirect - theory) / theory < .10, "Closed diffuse enclosure energy check exceeds 10% spatial/sampling allowance.");
        var zero = SceneCase.Room("zero-light", 12, 10, 9);
        var zeroRun = Solve(zero);
        Require(zeroRun.Zone.Illuminance.All(e => e == 0), "An unlit enclosure must remain zero.");
        zero.Sources.Add(new() { Position = new[] { 6.0, 5, 8.9 }, Ies = SceneCase.CosineIes(4000), Multiplier = 1e-12 });
        var tiny = Solve(zero);
        Require(tiny.Zone.Illuminance.All(e => double.IsFinite(e) && e >= 0 && e < 1e-8), "Near-zero results must remain finite and bounded in absolute fc.");
        Console.WriteLine("Photometric laws, obstruction and closed-enclosure energy check passed (separate from golden parity).");
    }

    static void ValidateEdits(SceneCase scene)
    {
        var original = Solve(scene);
        var dark = Clone(scene); dark.Reflectances.Wall = .1;
        var bright = Clone(scene); bright.Reflectances.Wall = .8;
        var d = Solve(dark); var b = Solve(bright);
        Equal(original.Zone.Direct, d.Zone.Direct, "material direct unchanged");
        Equal(original.Zone.Direct, b.Zone.Direct, "material direct unchanged");
        Require(b.Zone.Statistics.Average > original.Zone.Statistics.Average && original.Zone.Statistics.Average > d.Zone.Statistics.Average,
            "Material edits must change coupled interreflection.");
        var moved = Clone(scene); moved.Sources[0].Position[0] -= 3;
        Require(Solve(moved).Zone.Direct.Zip(original.Zone.Direct, (x,y) => Math.Abs(x-y)).Max() > 1, "Move must change physical direct solution");
        var added = Clone(scene); added.Sources.Add(new() { Position = new[] { 3.0, 3, 8.9 }, Ies = SceneCase.CosineIes(2000) });
        var extra = Solve(added);
        Require(extra.Zone.Statistics.Average > original.Zone.Statistics.Average, "Added source must increase physical solution");
        added.Sources.RemoveAt(1);
        var undone = Solve(added);
        Compare(Snapshot(original.Zone, original.Solver), Snapshot(undone.Zone, undone.Solver), "source undo");
        var repeat = Solve(scene);
        Compare(Snapshot(original.Zone, original.Solver), Snapshot(repeat.Zone, repeat.Solver), "repeat full solve");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var input = scene.Build();
        try { new LightingSolver(input.Sources, scene.Options).Solve(input.Zone, input.Triangles, cancel: cancel.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Require(LightingReport.Csv(new LightingReportData { Zones = { original.Zone } }).Length > 100, "Offline report failed");
        Console.WriteLine("Material, move, add/remove, repeatability, cancellation and offline CSV checks passed; no UI/cached-solve claim.");
    }

    static object Measure(SceneCase scene, int repeats)
    {
        var variants = new List<(string Name, SceneCase Scene)> { ("full-rebuild", scene) };
        var material = Clone(scene); material.Reflectances.Wall = .7; variants.Add(("material-full-rebuild", material));
        var move = Clone(scene); move.Sources[0].Position[0] += 1; variants.Add(("fixture-move-full-rebuild", move));
        var add = Clone(scene); add.Sources.Add(new() { Position = new[] { scene.Width / 2, scene.Depth / 2, scene.Height - .1 }, Ies = SceneCase.CosineIes(2000) }); variants.Add(("source-add-full-rebuild", add));
        var geometry = Clone(scene); geometry.AddBox(new(2,2,0), new(3,3,4), true); variants.Add(("geometry-full-rebuild", geometry));
        return new { scene.Name, Operations = variants.Select(v =>
        {
            long before = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            var first = Solve(v.Scene); double firstMs = watch.Elapsed.TotalMilliseconds;
            long firstBytes = GC.GetTotalAllocatedBytes(true) - before;
            var elapsed = new List<double>(); var allocations = new List<long>();
            Solve(v.Scene); // warmup; first measurement includes any JIT remaining for this scenario.
            for (int i = 0; i < repeats; i++)
            {
                before = GC.GetTotalAllocatedBytes(true); watch.Restart();
                var current = Solve(v.Scene); elapsed.Add(watch.Elapsed.TotalMilliseconds);
                allocations.Add(GC.GetTotalAllocatedBytes(true) - before);
                Compare(Snapshot(first.Zone, first.Solver), Snapshot(current.Zone, current.Solver), "benchmark repeat " + v.Name);
            }
            elapsed.Sort(); allocations.Sort();
            Console.WriteLine($"  {scene.Name}/{v.Name}: p50 {elapsed[(repeats-1)/2]:F1} ms, p95 {elapsed[(int)Math.Ceiling(repeats*.95)-1]:F1} ms");
            return new { Operation = v.Name, Repeats = repeats, FirstValidMilliseconds = firstMs, FirstAllocatedBytes = firstBytes,
                P50Milliseconds = elapsed[(int)Math.Ceiling(repeats*.5)-1], P95Milliseconds = elapsed[(int)Math.Ceiling(repeats*.95)-1],
                P50AllocatedBytes = allocations[(int)Math.Ceiling(repeats*.5)-1],
                ProcessPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                Stats = first.Solver.LastStats, Note = "Full reconstruction; no cache or UI. Allocation is process-wide including worker threads; peak working set is cumulative." };
        }).ToArray() };
    }

    static object Convergence(SceneCase scene)
    {
        var tight = Clone(scene); tight.Options.Convergence = 1e-9; tight.Options.MaxIterations = 500;
        var reference = Solve(tight);
        var iterations = new[] { .01, .0002, .000001 }.Select(tolerance =>
        {
            var s = Clone(scene); s.Options.Convergence = tolerance; var r = Solve(s);
            return new { Tolerance = tolerance, r.Solver.LastIterations, MaxPointDifferenceFc = r.Zone.Illuminance.Zip(reference.Zone.Illuminance, (a,b) => Math.Abs(a-b)).Max() };
        }).ToArray();
        var samples = new[] { CalcQuality.Draft, CalcQuality.Normal, CalcQuality.High }.Select(q =>
        {
            var s = Clone(scene); s.Options = SolverOptions.ForQuality(q, true); var r = Solve(s);
            return new { Quality = q.ToString(), r.Solver.LastIterations, r.Solver.LastPatchCount, AverageFc = r.Zone.Statistics.Average, s.Options.PatchRays, s.Options.GatherRays };
        }).ToArray();
        var capped = Clone(scene); capped.Options.MaxIterations = 1; var cap = Solve(capped);
        return new { IterationStudy = iterations, SpatialAndRayStudy = samples,
            CappedAtOne = new { cap.Solver.LastIterations, AverageFc = cap.Zone.Statistics.Average },
            Note = "Original engine exposes iteration count but no final residual or converged flag. Equality to MaxIterations is conservatively incomplete; fewer iterations indicates the internal stop criterion. Spatial/ray changes are separate and need not converge monotonically." };
    }
}

public sealed class Golden
{
    public string UpstreamCommit { get; set; } = "";
    public string InputSha256 { get; set; } = "";
    public Result Result { get; set; } = new();
}
public sealed class Result
{
    public double[] Direct { get; set; } = Array.Empty<double>();
    public double[] Total { get; set; } = Array.Empty<double>();
    public double[] Points { get; set; } = Array.Empty<double>();
    public double[] Surfaces { get; set; } = Array.Empty<double>();
    public double[] Statistics { get; set; } = Array.Empty<double>();
    public int Patches { get; set; }
    public int Iterations { get; set; }
    public int Inside { get; set; }
}
