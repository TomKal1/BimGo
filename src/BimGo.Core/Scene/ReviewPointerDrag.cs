using System.Numerics;

namespace BimGo.Scene
{
    /// <summary>Defers model clicks until release and distinguishes them from a pan, including quick press/release frames.</summary>
    public sealed class ReviewPointerDrag
    {
        private Vector2 _start, _last;
        private float _thresholdSquared;

        public bool Active { get; private set; }
        public bool Dragging { get; private set; }

        /// <summary>Starts only on the model viewport; UI controls and cube handles own their own presses.</summary>
        public void Begin(Vector2 position, float thresholdPixels)
        {
            Active = true;
            Dragging = false;
            _start = _last = position;
            _thresholdSquared = thresholdPixels * thresholdPixels;
        }

        /// <summary>A click is emitted once on release. Once a drag starts, returning to its start can never click.</summary>
        public (Vector2 Delta, bool Click) Step(Vector2 position, bool held)
        {
            if (!Active) { return default; }
            bool started = !Dragging && Vector2.DistanceSquared(position, _start) > _thresholdSquared;
            Dragging |= started;
            Vector2 delta = Dragging ? position - (started ? _start : _last) : Vector2.Zero;
            _last = position;
            bool click = !held && !Dragging;
            if (!held) { Active = false; }
            return (delta, click);
        }

        /// <summary>Focus loss or a mode change cancels a pending click instead of committing it later.</summary>
        public void Cancel() { Active = Dragging = false; }
    }
}
