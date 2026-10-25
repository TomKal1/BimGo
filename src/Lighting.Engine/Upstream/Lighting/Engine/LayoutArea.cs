using System;
using System.Collections.Generic;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// A part of a room with its own fixture type and array: usually one ceiling, or a rectangle
    /// the user drew. Fixture spots come from <see cref="LayoutGenerator.Spots"/>.
    /// </summary>
    public sealed class LayoutArea
    {
        private int _rows = 1;
        private int _columns = 1;

        public string Name { get; set; } = string.Empty;

        /// <summary>Plan outline (z ignored).</summary>
        public List<List<Vec3>> Loops { get; } = new List<List<Vec3>>();

        /// <summary>The ceiling this area came from; empty for a drawn area.</summary>
        public string CeilingKey { get; set; } = string.Empty;

        /// <summary>Where areas overlap, the higher priority area claims the spots.</summary>
        public int Priority { get; set; }

        public string TypeKey { get; set; } = string.Empty;

        /// <summary>Fixture size along its own axis (ft).</summary>
        public double FixtureLength { get; set; } = 2;

        /// <summary>Fixture size across its axis (ft).</summary>
        public double FixtureWidth { get; set; } = 2;

        public bool Round { get; set; }

        /// <summary>Fixtures along the frame's Y; never below 1.</summary>
        public int Rows { get => _rows; set => _rows = Math.Max(1, value); }

        /// <summary>Fixtures along the frame's X; never below 1.</summary>
        public int Columns { get => _columns; set => _columns = Math.Max(1, value); }

        /// <summary>Spacing along the frame's X (ft); written back by the generator when unlocked.</summary>
        public double SpacingX { get; set; }

        public double SpacingY { get; set; }

        public bool LockSpacingX { get; set; }

        public bool LockSpacingY { get; set; }

        /// <summary>Shift of the whole array from centred, in the frame (ft).</summary>
        public double OffsetX { get; set; }

        public double OffsetY { get; set; }

        /// <summary>Turns each fixture's long axis 90°; the array itself does not turn.</summary>
        public bool Rotate90 { get; set; }

        public bool Snap { get; set; } = true;

        public CeilingGrid? Grid { get; set; }

        /// <summary>The angle of the array's X from project X (radians).</summary>
        public double FrameAngle { get; set; }

        /// <summary>The ceiling underside the fixtures mount to (ft).</summary>
        public double MountingZ { get; set; }

        /// <summary>Spot indices the user switched off.</summary>
        public HashSet<int> Off { get; } = new HashSet<int>();

        /// <summary>Called when the spots move: switched-off indices no longer mean the same spots.</summary>
        public void ArrangementChanged() => Off.Clear();
    }

    /// <summary>One fixture position of an area's array.</summary>
    public sealed class LayoutSpot
    {
        /// <summary>Row-major index in the full array (row · columns + column).</summary>
        public int Index { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public bool On { get; set; }
    }
}
