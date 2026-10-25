using System.Numerics;

namespace BimGo.Scene
{
    /// <summary>
    /// Bounded screen-space sampling for overview labels. Each cell keeps several real-point candidates,
    /// so a hidden representative can be replaced without letting a dense region consume the label budget.
    /// Storage is allocated once; begin a frame, offer all points, then take candidates from each cell.
    /// </summary>
    public sealed class ScreenLabelLayout
    {
        private const int ALTERNATES = 8;
        private readonly int _budget;
        private readonly float[] _scores, _depths;
        private readonly int[] _keys;
        private readonly bool[] _used;
        private int _columns, _rows;
        private float _width, _height;

        /// <summary>Maximum storage slots, including occlusion fallbacks.</summary>
        public int SlotCount => _used.Length;
        /// <summary>Number of cells this frame; at most one label should be drawn in each.</summary>
        public int CellCount => _columns * _rows;

        /// <summary>Creates reusable storage for at most the requested number of labels.</summary>
        public ScreenLabelLayout(int budget)
        {
            if (budget < 1) { throw new ArgumentOutOfRangeException(nameof(budget)); }
            _budget = budget;
            int slots = checked(budget * ALTERNATES);
            _scores = new float[slots];
            _depths = new float[slots];
            _keys = new int[slots];
            _used = new bool[slots];
        }

        /// <summary>Clears candidates and sizes the grid to the viewport, spacing and label budget.</summary>
        public void Begin(float width, float height, Vector2 spacing)
        {
            Array.Clear(_used);
            _width = width; _height = height;
            _columns = _rows = 0;
            if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f ||
                !float.IsFinite(spacing.X) || !float.IsFinite(spacing.Y) || spacing.X <= 0f || spacing.Y <= 0f) { return; }
            float scale = MathF.Max(1f, MathF.Sqrt(width * height / (spacing.X * spacing.Y * _budget)));
            _columns = Math.Clamp((int)(width / (spacing.X * scale)), 1, _budget);
            _rows = Math.Clamp((int)(height / (spacing.Y * scale)), 1, _budget);
            while (_columns * _rows > _budget)
            {
                if (_columns >= _rows) { _columns--; } else { _rows--; }
            }
        }

        /// <summary>
        /// Offers a point; returns a storage slot for its caller-owned payload, or -1 when rejected.
        /// Prefer points near cell centres, then nearer depth and a stable key, independently of traversal order.
        /// </summary>
        public int Offer(Vector2 screen, float depth, int key)
        {
            if (CellCount == 0 || !float.IsFinite(screen.X) || !float.IsFinite(screen.Y) || !float.IsFinite(depth) ||
                depth < 0f || screen.X < 0f || screen.Y < 0f || screen.X >= _width || screen.Y >= _height) { return -1; }
            int x = Math.Min(_columns - 1, (int)(screen.X * _columns / _width));
            int y = Math.Min(_rows - 1, (int)(screen.Y * _rows / _height));
            Vector2 centre = new((x + 0.5f) * _width / _columns, (y + 0.5f) * _height / _rows);
            float score = Vector2.DistanceSquared(screen, centre);
            int start = (y * _columns + x) * ALTERNATES, slot = -1;
            for (int i = start; i < start + ALTERNATES; i++)
            {
                if (!_used[i]) { slot = i; break; }
                if (slot < 0 || Better(slot, i)) { slot = i; } // worst occupied slot
            }
            if (_used[slot] && !Better(score, depth, key, slot)) { return -1; }
            _used[slot] = true;
            _scores[slot] = score; _depths[slot] = depth; _keys[slot] = key;
            return slot;
        }

        /// <summary>Removes the best remaining candidate from a cell, or returns -1 when exhausted.</summary>
        public int Take(int cell)
        {
            if (cell < 0 || cell >= CellCount) { return -1; }
            int start = cell * ALTERNATES, best = -1;
            for (int i = start; i < start + ALTERNATES; i++)
            {
                if (_used[i] && (best < 0 || Better(i, best))) { best = i; }
            }
            if (best >= 0) { _used[best] = false; }
            return best;
        }

        private bool Better(int a, int b) => Better(_scores[a], _depths[a], _keys[a], b);
        private bool Better(float score, float depth, int key, int b) =>
            score < _scores[b] || (score == _scores[b] && (depth < _depths[b] || (depth == _depths[b] && key < _keys[b])));
    }
}
