using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The viewer benchmark (<c>BimGo.exe --bench out.json model.bimgo</c>): the rooms with the most fixture lights, four
    /// directions each from the room's centre at eye height (flying, so nothing moves), timed with no frame rate limit or
    /// VSync and the UI hidden. Each view warms up (shadow maps, probes, occlusion answers) before its frames are sampled:
    /// the GPU stages' unsmoothed times and the CPU frame time. Results (median, mean, p95 per view and over all views)
    /// go to the JSON file, then the app exits without saving anything. The settings are the user's own, so a before /
    /// after pair must run on the same machine and settings.
    /// </summary>
    internal sealed partial class GameSession
    {
        private const int BENCH_ROOMS = 4, BENCH_YAWS = 4, BENCH_WARM_ROOM = 150, BENCH_WARM_TURN = 45, BENCH_SAMPLES = 180;

        private sealed class BenchView
        {
            public string Name;
            public Vector3 Feet;
            public float Yaw;
            public int Warm;
            public readonly List<float[]> Gpu = new();
            public readonly List<float> Cpu = new();
            public readonly List<int> Lights = new(), FarLights = new();
        }

        private List<BenchView> _bench;
        private int _benchView, _benchFrame, _benchReadFrames;
        private Stopwatch _benchClock;

        /// <summary>True while the benchmark runs (the welcome and start toasts stay away).</summary>
        private bool Benchmarking => _bench != null;

        /// <summary>Picks the views and sets the session up for timing (call at the end of Initialise).</summary>
        private void StartBench()
        {
            _bench = new List<BenchView>();
            LightSource[] lights = Scene.Lighting.Lights;
            var rooms = Scene.Rooms
                .Select(r => (Room: r, Count: lights.Count(l => l.Position.X >= r.Min.X && l.Position.X <= r.Max.X && l.Position.Y >= r.Min.Y &&
                    l.Position.Y <= r.Max.Y && l.Position.Z >= r.BottomZ && l.Position.Z <= r.TopZ)))
                .Where(x => x.Count > 0)
                .OrderByDescending(x => x.Count).ThenBy(x => x.Room.Number, StringComparer.Ordinal)
                .Take(BENCH_ROOMS)
                .ToList();
            foreach (var (room, count) in rooms)
            {
                var feet = new Vector3((room.Min.X + room.Max.X) * 0.5f, (room.Min.Y + room.Max.Y) * 0.5f, room.BottomZ + 0.05f);
                for (int k = 0; k < BENCH_YAWS; k++)
                {
                    _bench.Add(new BenchView
                    {
                        Name = $"{room.Number} {room.Name} ({count} lights) yaw {k * 360 / BENCH_YAWS}",
                        Feet = feet,
                        Yaw = k * MathF.Tau / BENCH_YAWS,
                        Warm = k == 0 ? BENCH_WARM_ROOM : BENCH_WARM_TURN,
                    });
                }
            }
            if (_bench.Count == 0)
            {
                // No rooms with lights: the start view, four ways
                Vector3 start = _player.Feet;
                for (int k = 0; k < BENCH_YAWS; k++)
                {
                    _bench.Add(new BenchView { Name = $"start yaw {k * 360 / BENCH_YAWS}", Feet = start, Yaw = k * MathF.Tau / BENCH_YAWS, Warm = k == 0 ? BENCH_WARM_ROOM : BENCH_WARM_TURN });
                }
            }

            _frameLimit = 0;
            _vsync = false;
            Native.Wgl.SetSwapInterval(false);
            _showPerf = true;
            _renderer.Timers.WaitForAnswers = true; // every frame's GPU times (the CPU frame time then includes the wait)
            _uiHidden = true;
            _paused = false;
            if (!_player.Flying) { _player.ToggleFly(); }
            _benchView = -1;
            _benchClock = Stopwatch.StartNew();
            Utilities.Log_Utils.Write($"Benchmark: {_bench.Count} views → {Program.BenchOutput}");
            NextBenchView();
        }

        private void NextBenchView()
        {
            _benchView++;
            _benchFrame = 0;
            if (_benchView >= _bench.Count)
            {
                FinishBench();
                return;
            }
            BenchView view = _bench[_benchView];
            _player.TeleportTo(view.Feet, view.Yaw, 0f);
        }

        /// <summary>One presented frame: warm up, then sample (GPU times only on frames whose answers came back).</summary>
        private void StepBench(float dt)
        {
            BenchView view = _bench[_benchView];
            _player.TeleportTo(view.Feet, view.Yaw, 0f); // stay put whatever the controller did
            GpuTimers timers = _renderer.Timers;
            _benchFrame++;
            if (_benchFrame <= view.Warm)
            {
                _benchReadFrames = timers.ReadFrames;
                return;
            }
            view.Cpu.Add(dt * 1000f);
            view.Lights.Add(_renderer.Artificial.Count);
            view.FarLights.Add(_renderer.Artificial.FarCount);
            if (timers.ReadFrames != _benchReadFrames)
            {
                _benchReadFrames = timers.ReadFrames;
                view.Gpu.Add((float[])timers.Raw.Clone());
            }
            if (view.Cpu.Count >= BENCH_SAMPLES) { NextBenchView(); }
        }

        private void FinishBench()
        {
            static object Stats(List<float> values)
            {
                if (values.Count == 0) { return null; }
                var sorted = values.OrderBy(v => v).ToArray();
                float P(float q) => sorted[Math.Clamp((int)MathF.Round(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
                return new { median = Math.Round(P(0.5f), 3), mean = Math.Round(sorted.Average(), 3), p95 = Math.Round(P(0.95f), 3), n = sorted.Length };
            }
            static Dictionary<string, object> Stages(IEnumerable<float[]> frames)
            {
                var list = frames.ToList();
                var result = new Dictionary<string, object> { ["gpu_total"] = Stats(list.Select(f => f.Sum()).ToList()) };
                for (int s = 0; s < GpuTimers.COUNT; s++) { result[GpuTimers.NAMES[s]] = Stats(list.Select(f => f[s]).ToList()); }
                return result;
            }

            var report = new
            {
                version = Program.Version,
                when = DateTime.Now.ToString("s"),
                elements = Scene.Elements.Length,
                triangles = Scene.TriangleCount,
                gpu = Native.Gl.GetString(Native.Gl.RENDERER),
                window = $"{_window.Width}x{_window.Height}",
                msaa = _msaa,
                realistic = _realistic,
                lightMode = _lightMode.ToString(),
                lightQuality = _lightQuality,
                allLightsLive = _allLightsLive,
                seconds = Math.Round(_benchClock.Elapsed.TotalSeconds, 1),
                overall = new
                {
                    cpu_frame = Stats(_bench.SelectMany(v => v.Cpu).ToList()),
                    stages = Stages(_bench.SelectMany(v => v.Gpu)),
                },
                views = _bench.Select(v => new
                {
                    name = v.Name,
                    lights = v.Lights.Count == 0 ? 0 : v.Lights.Max(),
                    farLights = v.FarLights.Count == 0 ? 0 : v.FarLights.Max(),
                    cpu_frame = Stats(v.Cpu),
                    stages = Stages(v.Gpu),
                }).ToArray(),
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(Program.BenchOutput)!);
            File.WriteAllText(Program.BenchOutput, json);
            Utilities.Log_Utils.Write($"Benchmark written: {Program.BenchOutput}");
            Environment.Exit(0); // nothing saved: the settings were changed for the run only
        }
    }
}
