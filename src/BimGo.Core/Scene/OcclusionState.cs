using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Round Y-D1: which render chunks the main view skips because last frame's occlusion query found nothing of their
    /// (slightly enlarged) box in front of the depth already drawn. The renderer owns the GL queries; this class holds
    /// the decisions, so they can be tested without a GPU:
    /// <list type="bullet">
    /// <item>A chunk starts visible. A query that finds no sample hides it; one that finds any shows it.</item>
    /// <item>Hidden chunks are queried every frame (so they come back a frame after they show); visible ones every
    /// <see cref="VISIBLE_RECHECK"/> frames, staggered by chunk (fewer queries).</item>
    /// <item>A query not answered yet at the start of a frame keeps its chunk drawn (a slow GPU costs speed, never a hole).</item>
    /// <item>A chunk the camera is near (inside its box grown by <see cref="NEAR"/>) is always drawn and not queried.</item>
    /// <item>A chunk that leaves the view is forgotten (drawn when it comes back), and its pending answer is ignored.</item>
    /// <item>A jump of the camera (teleport, bookmark, portal: over <see cref="JUMP_DISTANCE"/> or a turn over
    /// <see cref="JUMP_DEGREES"/> in one frame) shows everything again.</item>
    /// <item>Y-D1 r2: chunks are grouped in clusters (<see cref="BuildClusters"/>: a <see cref="CLUSTER_CELL"/> m grid,
    /// <see cref="CLUSTER_SLAB"/> m slabs). When every chunk of a cluster in view is hidden and none waits for an answer,
    /// one query of the cluster's box stands in for theirs: no sample keeps them hidden, any shows them all (their own
    /// queries then hide the ones still behind something).</item>
    /// </list>
    /// </summary>
    public sealed class OcclusionState
    {
        #region Constants

        /// <summary>How much a chunk's box grows for its query (m): a margin for the frame of lag.</summary>
        public const float MARGIN = 0.3f;

        /// <summary>A chunk whose box (grown by this, m) holds the camera is always drawn.</summary>
        public const float NEAR = 1.5f;

        /// <summary>Visible chunks are queried once in this many frames.</summary>
        public const int VISIBLE_RECHECK = 4;

        /// <summary>A move over this in one frame (m) counts as a jump.</summary>
        public const float JUMP_DISTANCE = 1.5f;

        /// <summary>A turn over this in one frame (degrees) counts as a jump.</summary>
        public const float JUMP_DEGREES = 25f;

        /// <summary>Y-D1 r2: cluster grid cell across (m).</summary>
        public const float CLUSTER_CELL = 16f;

        /// <summary>Y-D1 r2: cluster slab height (m; about a storey).</summary>
        public const float CLUSTER_SLAB = 4f;

        #endregion

        #region Fields

        private readonly bool[] _hidden;
        private readonly bool[] _pending;
        private readonly int[] _epoch;       // bumped when a chunk is forgotten: older answers are ignored
        private readonly int[] _queryEpoch;  // the epoch a pending query was issued in
        private readonly int[] _inViewFrame; // the frame a chunk was last in view (MarkInView)
        private int _frame;
        private bool _hasCamera;

        // Y-D1 r2: clusters (none until SetClusters)
        private int[] _clusterOf = Array.Empty<int>();
        private int[][] _members = Array.Empty<int[]>();
        private bool[] _clusterPending = Array.Empty<bool>();
        private Vector3 _lastEye, _lastForward;

        #endregion

        /// <summary>A state for a scene's chunks (all visible).</summary>
        public OcclusionState(int chunkCount)
        {
            chunkCount = Math.Max(0, chunkCount);
            _hidden = new bool[chunkCount];
            _pending = new bool[chunkCount];
            _epoch = new int[chunkCount];
            _queryEpoch = new int[chunkCount];
            _inViewFrame = new int[chunkCount];
            Array.Fill(_inViewFrame, -1);
        }

        #region Properties

        /// <summary>Per chunk: skip it in the main view (the renderer reads this array directly).</summary>
        public bool[] Hidden => _hidden;

        /// <summary>The number of chunks.</summary>
        public int Count => _hidden.Length;

        /// <summary>Frames since the start.</summary>
        public int Frame => _frame;

        /// <summary>Times everything was shown again after a camera jump (for the log / stats).</summary>
        public int Jumps { get; private set; }

        #endregion

        #region Frames

        /// <summary>
        /// Starts a frame: counts it and, when the camera jumped since the last one, shows every chunk again.
        /// </summary>
        /// <returns>True when the camera jumped.</returns>
        public bool BeginFrame(Vector3 eye, Vector3 forward)
        {
            _frame++;
            bool jumped = _hasCamera && Jumped(_lastEye, _lastForward, eye, forward);
            _lastEye = eye;
            _lastForward = forward;
            _hasCamera = true;
            if (jumped)
            {
                ShowAll();
                Jumps++;
            }
            return jumped;
        }

        /// <summary>Shows every chunk and ignores every pending answer (a jump, a visibility change, the setting turned on).</summary>
        public void ShowAll()
        {
            for (int c = 0; c < _hidden.Length; c++) { Forget(c); }
        }

        /// <summary>True when the camera moved or turned too far in one frame for last frame's answers to hold.</summary>
        public static bool Jumped(Vector3 lastEye, Vector3 lastForward, Vector3 eye, Vector3 forward)
        {
            if (Vector3.DistanceSquared(lastEye, eye) > JUMP_DISTANCE * JUMP_DISTANCE) { return true; }
            float a = lastForward.Length(), b = forward.Length();
            if (a < 1e-6f || b < 1e-6f) { return false; }
            float cos = Vector3.Dot(lastForward, forward) / (a * b);
            return cos < MathF.Cos(JUMP_DEGREES * MathF.PI / 180f);
        }

        #endregion

        #region Per chunk

        /// <summary>True when the chunk should get a query this frame (none pending; hidden, or its turn to be checked).</summary>
        public bool NeedsQuery(int chunk) => !_pending[chunk] && (_hidden[chunk] || (chunk + _frame) % VISIBLE_RECHECK == 0);

        /// <summary>True while a query for the chunk is out.</summary>
        public bool IsPending(int chunk) => _pending[chunk];

        /// <summary>A query was issued for the chunk.</summary>
        public void Issued(int chunk)
        {
            _pending[chunk] = true;
            _queryEpoch[chunk] = _epoch[chunk];
        }

        /// <summary>
        /// The chunk's query answered: any sample passed → drawn, none → hidden (unless the chunk was forgotten since).
        /// </summary>
        public void Answered(int chunk, bool anySamplePassed)
        {
            if (!_pending[chunk]) { return; }
            _pending[chunk] = false;
            if (_queryEpoch[chunk] != _epoch[chunk]) { return; } // forgotten since: the answer is about an older view
            _hidden[chunk] = !anySamplePassed;
        }

        /// <summary>The chunk's query hasn't answered at the start of a frame: draw it until it does.</summary>
        public void NotAnswered(int chunk) => _hidden[chunk] = false;

        /// <summary>The camera is at the chunk: draw it.</summary>
        public void Near(int chunk) => _hidden[chunk] = false;

        /// <summary>The chunk left the view (or its group was hidden): drawn when it comes back; a pending answer is ignored.</summary>
        public void Forget(int chunk)
        {
            _hidden[chunk] = false;
            if (_pending[chunk]) { _epoch[chunk]++; }
        }

        /// <summary>The chunk is in view this frame (and not near the camera): it may join its cluster's query.</summary>
        public void MarkInView(int chunk) => _inViewFrame[chunk] = _frame;

        /// <summary>True when the chunk was marked in view this frame.</summary>
        public bool InView(int chunk) => _inViewFrame[chunk] == _frame;

        /// <summary>True when the camera is inside a chunk's box grown by <see cref="NEAR"/>.</summary>
        public static bool IsNear(in Aabb box, Vector3 eye) =>
            eye.X >= box.Min.X - NEAR && eye.X <= box.Max.X + NEAR
            && eye.Y >= box.Min.Y - NEAR && eye.Y <= box.Max.Y + NEAR
            && eye.Z >= box.Min.Z - NEAR && eye.Z <= box.Max.Z + NEAR;

        #endregion

        #region Clusters (Y-D1 r2)

        /// <summary>
        /// Groups chunks by where their centres fall on a <paramref name="cell"/> m grid in <paramref name="slab"/> m slabs.
        /// </summary>
        /// <returns>Each chunk's cluster (0…count − 1, in order of first appearance).</returns>
        public static int[] BuildClusters(IReadOnlyList<Aabb> bounds, float cell, float slab, out int count)
        {
            var clusterOf = new int[bounds.Count];
            var ids = new Dictionary<(int, int, int), int>();
            for (int c = 0; c < bounds.Count; c++)
            {
                Vector3 centre = bounds[c].Center;
                var key = ((int)MathF.Floor(centre.X / cell), (int)MathF.Floor(centre.Y / cell), (int)MathF.Floor(centre.Z / slab));
                if (!ids.TryGetValue(key, out int id))
                {
                    id = ids.Count;
                    ids[key] = id;
                }
                clusterOf[c] = id;
            }
            count = ids.Count;
            return clusterOf;
        }

        /// <summary>Sets the chunks' clusters (from <see cref="BuildClusters"/>).</summary>
        public void SetClusters(int[] clusterOf, int count)
        {
            if (clusterOf == null || clusterOf.Length != _hidden.Length) { return; }
            _clusterOf = clusterOf;
            var sizes = new int[count];
            foreach (int k in clusterOf) { sizes[k]++; }
            _members = new int[count][];
            for (int k = 0; k < count; k++) { _members[k] = new int[sizes[k]]; }
            var fill = new int[count];
            for (int c = 0; c < clusterOf.Length; c++) { _members[clusterOf[c]][fill[clusterOf[c]]++] = c; }
            _clusterPending = new bool[count];
        }

        /// <summary>The number of clusters (0 without <see cref="SetClusters"/>).</summary>
        public int ClusterCount => _members.Length;

        /// <summary>A chunk's cluster, or -1.</summary>
        public int ClusterOf(int chunk) => chunk < _clusterOf.Length ? _clusterOf[chunk] : -1;

        /// <summary>The chunks of a cluster.</summary>
        public int[] Members(int cluster) => _members[cluster];

        /// <summary>True while a cluster query is out.</summary>
        public bool IsClusterPending(int cluster) => _clusterPending[cluster];

        /// <summary>
        /// True when one query can stand in for the cluster's chunks this frame: at least two of them in view, every one
        /// in view hidden with no query of its own out, and no cluster query out.
        /// </summary>
        public bool ClusterDue(int cluster)
        {
            if (_clusterPending[cluster]) { return false; }
            int inView = 0;
            foreach (int c in _members[cluster])
            {
                if (_inViewFrame[c] != _frame) { continue; }
                if (!_hidden[c] || _pending[c]) { return false; }
                inView++;
            }
            return inView >= 2;
        }

        /// <summary>A cluster query was issued.</summary>
        public void ClusterIssued(int cluster) => _clusterPending[cluster] = true;

        /// <summary>A cluster query answered: any sample shows every chunk of it; none keeps them as they are.</summary>
        public void ClusterAnswered(int cluster, bool anySamplePassed)
        {
            if (!_clusterPending[cluster]) { return; }
            _clusterPending[cluster] = false;
            if (anySamplePassed) { ShowCluster(cluster); }
        }

        /// <summary>A cluster query hasn't answered at the start of a frame: draw its chunks until it does.</summary>
        public void ClusterNotAnswered(int cluster) => ShowCluster(cluster);

        private void ShowCluster(int cluster)
        {
            foreach (int c in _members[cluster]) { _hidden[c] = false; }
        }

        #endregion

        #region Stats

        /// <summary>The number of hidden chunks (stats; counts the array).</summary>
        public int HiddenCount()
        {
            int n = 0;
            foreach (bool h in _hidden) { if (h) { n++; } }
            return n;
        }

        #endregion
    }
}
