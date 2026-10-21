using System.Numerics;
using BimGo.Native;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Round Y-D1: occlusion culling of the main view, with GL occlusion queries.
    /// <list type="bullet">
    /// <item>Each frame (main view only: never photos, videos, clash pictures, probes, reflections or shadows), after
    /// the opaque scene, the dynamic elements, the section caps and the ground are drawn, every chunk in view that is due
    /// (<see cref="OcclusionState"/>) gets an <c>ANY_SAMPLES_PASSED</c> query of its box (grown by
    /// <see cref="OcclusionState.MARGIN"/>), with colour and depth writes off.</item>
    /// <item>At the start of the next frame the answers that are in decide which chunks the opaque pass, the AO pre-pass
    /// and the transparent pass skip. An answer that isn't in yet keeps its chunk drawn: the CPU never waits.</item>
    /// <item>Boxes for every chunk sit in one buffer (36 vertices each, made once), so a query is one draw.</item>
    /// <item>Y-D1 r2: boxes draw their front faces only (half the fill); a cluster of hidden chunks (<see cref="OcclusionState"/>)
    /// is queried as one box; <see cref="OcclusionMode"/> Auto culls only while the view holds over
    /// <see cref="AUTO_ON_TRIANGLES"/> triangles (off again under <see cref="AUTO_OFF_TRIANGLES"/>).</item>
    /// <item>The trade: a chunk that comes into sight from behind a wall shows a frame late (the margin, the near rule
    /// and the camera-jump rule hide most of that).</item>
    /// </list>
    /// </summary>
    internal sealed unsafe partial class SceneRenderer
    {
        #region Fields

        private OcclusionState _occlusion;
        private uint[] _occlusionQueries = Array.Empty<uint>();
        private uint _occlusionVao, _occlusionVbo;
        private Aabb[] _clusterBounds = Array.Empty<Aabb>(); // Y-D1 r2
        private int _occlusionMode = OCCLUSION_AUTO;          // the setting
        private bool _autoCulling;                            // Auto: culling now (hysteresis on triangles in view)
        private bool _occlusionFrame;       // this frame's main view culls
        private bool _occlusionFailed;      // a GL error setting it up: off for the session

        #endregion

        #region Stats

        /// <summary>Round Y-D1: GPU time per stage of the main view (Performance details).</summary>
        public GpuTimers Timers { get; } = new();

        /// <summary>Round Y-D1: chunks in the view frustum in the main view's opaque and transparent passes (stats).</summary>
        public int ChunksInView { get; private set; }

        /// <summary>Round Y-D1: of those, skipped by occlusion culling (stats).</summary>
        public int ChunksOccluded { get; private set; }

        /// <summary>Y-D1 r2: triangles of the static scene in the view frustum, drawn or culled (stats; Auto decides on it).</summary>
        public long TrianglesInView { get; private set; }

        /// <summary>Round Y-D1: triangles of the static scene drawn in the main view (stats).</summary>
        public long TrianglesDrawn { get; private set; }

        /// <summary>Round Y-D1: multi-draws of the static scene in the main view (stats).</summary>
        public int DrawCalls { get; private set; }

        /// <summary>Round Y-D1: occlusion queries issued this frame (stats).</summary>
        public int OcclusionQueries { get; private set; }

        /// <summary>Round Y-D1: triangles in the static scene (stats).</summary>
        public long TrianglesTotal => _batches == null ? 0 : _batches.Indices.Length / 3;

        /// <summary>Round Y-D1: render chunks in the static scene (stats).</summary>
        public int ChunkCount => _batches == null ? 0 : _batches.Chunks.Length;

        /// <summary>Clears the main view's counts (the opaque pass starts them).</summary>
        private void ResetDrawStats()
        {
            ChunksDrawn = 0;
            ChunksInView = 0;
            ChunksOccluded = 0;
            TrianglesDrawn = 0;
            TrianglesInView = 0;
            DrawCalls = 0;
        }

        #endregion

        #region Control

        /// <summary>Y-D1 r2: the setting's values.</summary>
        public const int OCCLUSION_OFF = 0, OCCLUSION_AUTO = 1, OCCLUSION_ON = 2;

        /// <summary>Y-D1 r2: Auto culls once the view holds this many triangles…</summary>
        public const long AUTO_ON_TRIANGLES = 250_000;

        /// <summary>…and stops under this many.</summary>
        public const long AUTO_OFF_TRIANGLES = 150_000;

        /// <summary>
        /// Round Y-D1 (r2: Off / Auto / On): the Options setting. Starting to cull shows every chunk until the queries answer.
        /// </summary>
        public int OcclusionMode
        {
            get => _occlusionMode;
            set => _occlusionMode = Math.Clamp(value, OCCLUSION_OFF, OCCLUSION_ON);
        }

        /// <summary>True while the main view culls (On, or Auto over the threshold) and culling works.</summary>
        public bool OcclusionCulling => !_occlusionFailed && (_occlusionMode == OCCLUSION_ON || (_occlusionMode == OCCLUSION_AUTO && _autoCulling));

        /// <summary>True when the last setup failed (the Options note says why culling is off).</summary>
        public bool OcclusionFailed => _occlusionFailed;

        /// <summary>
        /// Shows every chunk again until the next answers (after something that changes what hides what: a section cut, a
        /// category shown again).
        /// </summary>
        public void ResetOcclusion() => _occlusion?.ShowAll();

        /// <summary>This frame's per-chunk skip mask (null when the frame doesn't cull).</summary>
        private bool[] OcclusionMask => _occlusionFrame ? _occlusion.Hidden : null;

        /// <summary>
        /// Starts a frame. For the main view (<paramref name="mainView"/>, culling on): reads the answers that are in and
        /// lets the opaque, AO and transparent passes skip the hidden chunks. Other frames (photos, videos, clash
        /// pictures) draw everything and leave the state alone. Call before <see cref="UpdateScreenEffects"/>.
        /// </summary>
        public void BeginOcclusionFrame(FpsCamera camera, bool mainView)
        {
            _occlusionFrame = false;
            if (!mainView) { return; }
            OcclusionQueries = 0;

            // Auto: from last frame's triangles in view (counted whether culling or not)
            bool was = OcclusionCulling;
            if (TrianglesInView >= AUTO_ON_TRIANGLES) { _autoCulling = true; }
            else if (TrianglesInView < AUTO_OFF_TRIANGLES) { _autoCulling = false; }
            if (!OcclusionCulling || _batches == null || _batches.Chunks.Length == 0) { return; }
            if (!EnsureOcclusion()) { return; }
            if (!was) { _occlusion.ShowAll(); } // just started: everything until the answers come

            _occlusion.BeginFrame(camera.Position, camera.Forward);
            uint[] queries = _occlusionQueries;
            RenderChunk[] chunks = _batches.Chunks;
            bool[] hidden = _occlusion.Hidden;
            Vector3 eye = camera.Position;

            // Y-D1 r2: cluster answers first (they only ever show chunks)
            for (int k = 0; k < _occlusion.ClusterCount; k++)
            {
                if (!_occlusion.IsClusterPending(k)) { continue; }
                uint q = queries[chunks.Length + k];
                if (Gl.GetQueryObject(q, Gl.QUERY_RESULT_AVAILABLE) != 0) { _occlusion.ClusterAnswered(k, Gl.GetQueryObject(q, Gl.QUERY_RESULT) != 0); }
                else { _occlusion.ClusterNotAnswered(k); }
            }

            for (int c = 0; c < chunks.Length; c++)
            {
                if (_occlusion.IsPending(c))
                {
                    if (Gl.GetQueryObject(queries[c], Gl.QUERY_RESULT_AVAILABLE) != 0)
                    {
                        _occlusion.Answered(c, Gl.GetQueryObject(queries[c], Gl.QUERY_RESULT) != 0);
                    }
                    else
                    {
                        _occlusion.NotAnswered(c);
                    }
                }

                // Walked up to a hidden chunk: draw it this frame already
                if (hidden[c] && OcclusionState.IsNear(chunks[c].Bounds, eye)) { _occlusion.Near(c); }
            }
            _occlusionFrame = true;
        }

        /// <summary>
        /// Queries the boxes of the chunks in view that are due, against the depth drawn so far (call after the opaque
        /// scene, the dynamic elements, the section caps and the ground; before the transparent pass). Nothing outside a
        /// culling frame.
        /// </summary>
        public void QueryOcclusion(in Matrix4x4 viewProjection, Vector4[] planes, Vector3 eye, bool[] groupVisible)
        {
            if (!_occlusionFrame) { return; }

            Gl.ColorMask(false, false, false, false);
            Gl.DepthMask(false);
            Gl.Enable(Gl.DEPTH_TEST);
            Gl.DepthFunc(Gl.LEQUAL);
            Gl.Enable(Gl.CULL_FACE); // Y-D1 r2: front faces only (the camera is never inside a queried box: the near rule)
            Gl.CullFace(Gl.BACK);
            Gl.FrontFace(Gl.CCW);
            Gl.Disable(Gl.BLEND);
            _shadowDepthProgram.Use();
            Gl.UniformMatrix4(_depthViewProj, viewProjection);
            Gl.UniformMatrix4(_depthModel, Matrix4x4.Identity);
            Gl.BindVertexArray(_occlusionVao);

            RenderBatch[] batches = _batches.Batches;
            RenderChunk[] chunks = _batches.Chunks;
            uint[] queries = _occlusionQueries;
            int issued = 0;

            // 1. Which chunks are in view this frame (the rest are forgotten; near ones drawn)
            for (int b = 0; b < batches.Length; b++)
            {
                RenderBatch batch = batches[b];
                int end = batch.ChunkStart + batch.ChunkCount;
                bool shown = groupVisible[batch.Group];
                for (int c = batch.ChunkStart; c < end; c++)
                {
                    // Out of sight (a hidden category, outside the view): drawn when it comes back
                    if (!shown || !FpsCamera.IsVisible(planes, chunks[c].Bounds))
                    {
                        _occlusion.Forget(c);
                        continue;
                    }
                    if (OcclusionState.IsNear(chunks[c].Bounds, eye))
                    {
                        _occlusion.Near(c);
                        continue;
                    }
                    _occlusion.MarkInView(c);
                }
            }

            // 2. Y-D1 r2: one query for each cluster whose chunks in view are all hidden (not when the camera is at it)
            for (int k = 0; k < _occlusion.ClusterCount; k++)
            {
                if (OcclusionState.IsNear(_clusterBounds[k], eye) || !_occlusion.ClusterDue(k)) { continue; }
                Gl.BeginQuery(Gl.ANY_SAMPLES_PASSED, queries[chunks.Length + k]);
                Gl.DrawArrays(Gl.TRIANGLES, (chunks.Length + k) * 36, 36);
                Gl.EndQuery(Gl.ANY_SAMPLES_PASSED);
                _occlusion.ClusterIssued(k);
                issued++;
            }

            // 3. The other chunks in view that are due (not those a cluster query covers)
            for (int c = 0; c < chunks.Length; c++)
            {
                if (!_occlusion.InView(c) || !_occlusion.NeedsQuery(c)) { continue; }
                int k = _occlusion.ClusterOf(c);
                if (k >= 0 && _occlusion.IsClusterPending(k) && _occlusion.Hidden[c]) { continue; }
                Gl.BeginQuery(Gl.ANY_SAMPLES_PASSED, queries[c]);
                Gl.DrawArrays(Gl.TRIANGLES, c * 36, 36);
                Gl.EndQuery(Gl.ANY_SAMPLES_PASSED);
                _occlusion.Issued(c);
                issued++;
            }

            Gl.Disable(Gl.CULL_FACE);
            Gl.BindVertexArray(0);
            Gl.DepthMask(true);
            Gl.ColorMask(true, true, true, true);
            OcclusionQueries = issued;
        }

        #endregion

        #region Resources

        /// <summary>The state, one query per chunk and the box buffer, made on first use; false (and off) on a GL error.</summary>
        private bool EnsureOcclusion()
        {
            if (_occlusion != null) { return true; }
            if (_occlusionFailed) { return false; }

            RenderChunk[] chunks = _batches.Chunks;
            for (int i = 0; i < 16 && Gl.GetError() != Gl.NO_ERROR; i++) { } // start clean
            _occlusion = new OcclusionState(chunks.Length);

            // Y-D1 r2: clusters (a grid of cells and storey slabs) and their boxes (the union of their chunks')
            var bounds = new Aabb[chunks.Length];
            for (int c = 0; c < chunks.Length; c++) { bounds[c] = chunks[c].Bounds; }
            int[] clusterOf = OcclusionState.BuildClusters(bounds, OcclusionState.CLUSTER_CELL, OcclusionState.CLUSTER_SLAB, out int clusterCount);
            _occlusion.SetClusters(clusterOf, clusterCount);
            _clusterBounds = new Aabb[clusterCount];
            Array.Fill(_clusterBounds, Aabb.Empty);
            for (int c = 0; c < chunks.Length; c++) { _clusterBounds[clusterOf[c]].Include(chunks[c].Bounds); }

            // One query per chunk, then one per cluster
            _occlusionQueries = new uint[chunks.Length + clusterCount];
            Gl.GenQueries(_occlusionQueries);

            // Every chunk's box, then every cluster's, grown by the margin: 12 triangles, 36 vertices each
            var vertices = new Vector3[(chunks.Length + clusterCount) * 36];
            Vector3 m = new(OcclusionState.MARGIN);
            for (int c = 0; c < chunks.Length; c++) { WriteBox(vertices, c * 36, chunks[c].Bounds.Min - m, chunks[c].Bounds.Max + m); }
            for (int k = 0; k < clusterCount; k++) { WriteBox(vertices, (chunks.Length + k) * 36, _clusterBounds[k].Min - m, _clusterBounds[k].Max + m); }
            _occlusionVao = Gl.GenVertexArray();
            Gl.BindVertexArray(_occlusionVao);
            _occlusionVbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _occlusionVbo);
            fixed (Vector3* v = vertices)
            {
                Gl.BufferData(Gl.ARRAY_BUFFER, (nint)vertices.Length * sizeof(Vector3), v, Gl.STATIC_DRAW);
            }
            Gl.EnableVertexAttribArray(0);
            Gl.VertexAttribPointer(0, 3, Gl.FLOAT, false, sizeof(Vector3), 0);
            Gl.BindVertexArray(0);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);

            uint error = Gl.GetError();
            if (error != Gl.NO_ERROR)
            {
                Utilities.Log_Utils.Write($"Occlusion culling: setup failed (GL error 0x{error:X}): off for this session.");
                DisposeOcclusion();
                _occlusionFailed = true;
                return false;
            }
            Utilities.Log_Utils.Write($"Occlusion culling: {chunks.Length:N0} chunks in {clusterCount:N0} clusters, {TrianglesTotal:N0} triangles, box buffer {vertices.Length * 12 / 1024:N0} KB.");
            return true;
        }

        // A box's faces as corner quads (corner i: bit 0 = max X, bit 1 = max Y, bit 2 = max Z) → triangles a b c, a c d,
        // counter-clockwise seen from outside (Y-D1 r2: the queries draw front faces only)
        private static readonly int[] BOX_FACES = { 0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5 };

        /// <summary>Writes a box's 12 triangles (36 vertices) at <paramref name="at"/>.</summary>
        private static void WriteBox(Vector3[] v, int at, Vector3 min, Vector3 max)
        {
            Span<Vector3> p = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                p[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            }
            for (int f = 0; f < 6; f++)
            {
                int a = BOX_FACES[f * 4], b = BOX_FACES[f * 4 + 1], c = BOX_FACES[f * 4 + 2], d = BOX_FACES[f * 4 + 3];
                v[at++] = p[a]; v[at++] = p[b]; v[at++] = p[c];
                v[at++] = p[a]; v[at++] = p[c]; v[at++] = p[d];
            }
        }

        private void DisposeOcclusion()
        {
            Gl.DeleteQueries(_occlusionQueries);
            _occlusionQueries = Array.Empty<uint>();
            _clusterBounds = Array.Empty<Aabb>();
            Gl.DeleteBuffer(_occlusionVbo);
            Gl.DeleteVertexArray(_occlusionVao);
            _occlusionVbo = _occlusionVao = 0;
            _occlusion = null;
            _occlusionFrame = false;
        }

        #endregion
    }
}
