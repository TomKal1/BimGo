using BimGo.Native;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Round Y-D1: GPU time per stage of the main view (Options → Performance details), with <c>TIME_ELAPSED</c> queries
    /// one after another (GL allows no nesting), read two frames later so the CPU never waits, smoothed for reading.
    /// Off (no queries) unless <see cref="Enabled"/>.
    /// </summary>
    internal sealed class GpuTimers : IDisposable
    {
        /// <summary>The stages, in frame order.</summary>
        public const int SHADOWS = 0, EFFECTS = 1, REFLECTIONS = 2, OPAQUE = 3, OCCLUSION = 4, REST = 5, COUNT = 6;

        /// <summary>Short stage names for the readout.</summary>
        public static readonly string[] NAMES = { "shadows", "AO / glow", "reflections", "opaque", "occlusion", "glass / rest" };

        private const int RING = 3;
        private const float SMOOTH = 0.15f;

        private uint[] _queries = Array.Empty<uint>();
        private readonly bool[] _used = new bool[RING * COUNT];
        private int _slot, _active = -1;
        private bool _failed;

        /// <summary>Time per stage (ms, smoothed).</summary>
        public float[] Ms { get; } = new float[COUNT];

        /// <summary>Time per stage of the latest frame read back (ms, unsmoothed; the benchmark's samples).</summary>
        public float[] Raw { get; } = new float[COUNT];

        /// <summary>Counts frames whose answers were read back (the benchmark takes a sample when it moves).</summary>
        public int ReadFrames { get; private set; }

        /// <summary>Wait for each answer instead of skipping one that isn't back yet (the benchmark: every frame counted).</summary>
        public bool WaitForAnswers { get; set; }

        /// <summary>The stages' sum (ms).</summary>
        public float TotalMs
        {
            get
            {
                float total = 0f;
                foreach (float ms in Ms) { total += ms; }
                return total;
            }
        }

        /// <summary>Time the main view (set each frame from the readout's setting).</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Starts a frame: reads the oldest frame's answers (the slot about to be reused) and moves to it.
        /// </summary>
        public void BeginFrame()
        {
            _active = -1;
            if (!Enabled || _failed) { return; }
            if (_queries.Length == 0)
            {
                for (int i = 0; i < 16 && Gl.GetError() != Gl.NO_ERROR; i++) { } // start clean
                _queries = new uint[RING * COUNT];
                Gl.GenQueries(_queries);
                if (Gl.GetError() != Gl.NO_ERROR)
                {
                    _failed = true;
                    Utilities.Log_Utils.Write("GPU timers: not available on this GPU (no TIME_ELAPSED queries).");
                    return;
                }
            }

            _slot = (_slot + 1) % RING;
            bool read = false;
            for (int s = 0; s < COUNT; s++)
            {
                int i = _slot * COUNT + s;
                Raw[s] = 0f;
                if (!_used[i]) { continue; }
                _used[i] = false;
                if (!WaitForAnswers && Gl.GetQueryObject(_queries[i], Gl.QUERY_RESULT_AVAILABLE) == 0) { continue; }
                float ms = Gl.GetQueryObject(_queries[i], Gl.QUERY_RESULT) / 1_000_000f;
                Ms[s] += (ms - Ms[s]) * SMOOTH;
                Raw[s] = ms;
                read = true;
            }
            if (read) { ReadFrames++; }
        }

        /// <summary>Ends the stage being timed (if any) and starts timing <paramref name="stage"/>.</summary>
        public void Stage(int stage)
        {
            if (!Enabled || _failed || _queries.Length == 0) { return; }
            if (_active >= 0) { Gl.EndQuery(Gl.TIME_ELAPSED); }
            int i = _slot * COUNT + stage;
            Gl.BeginQuery(Gl.TIME_ELAPSED, _queries[i]);
            _used[i] = true;
            _active = stage;
        }

        /// <summary>Ends the frame's last stage.</summary>
        public void EndFrame()
        {
            if (_active < 0) { return; }
            Gl.EndQuery(Gl.TIME_ELAPSED);
            _active = -1;
        }

        public void Dispose()
        {
            Gl.DeleteQueries(_queries);
            _queries = Array.Empty<uint>();
        }
    }
}
