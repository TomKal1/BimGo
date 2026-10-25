using System.Numerics;
using BimGo.Scene;

namespace BimGo.Calculations;

/// <summary>The physical domain exceeds a calculation budget; fewer rooms per run would fit.</summary>
public sealed class CalculationTooLargeException : Exception
{
    public CalculationTooLargeException(string message) : base(message) { }
}

/// <summary>Spatial halves of a room selection, for running a domain too large for one calculation in parts.</summary>
public static class RoomGroups
{
    /// <summary>
    /// Splits rooms into two groups of neighbours: by storey when they span more than one floor height, else across the
    /// wider plan direction of their centres. Each half is as even as the order allows. Needs at least two rooms.
    /// </summary>
    public static (int[] First, int[] Second) Bisect(SceneData scene, IReadOnlyList<int> rooms)
    {
        if (rooms == null || rooms.Count < 2) throw new ArgumentException("Two or more rooms are needed to split a calculation.", nameof(rooms));
        var centres = rooms.Select(i => (Room: i, Centre: Centre(scene.Rooms[i]))).ToList();
        float zMin = centres.Min(c => c.Centre.Z), zMax = centres.Max(c => c.Centre.Z);
        float xSpan = centres.Max(c => c.Centre.X) - centres.Min(c => c.Centre.X);
        float ySpan = centres.Max(c => c.Centre.Y) - centres.Min(c => c.Centre.Y);
        // 2 m: rooms on one storey differ in floor height by steps, not by a storey
        bool byStorey = zMax - zMin > 2f;
        Func<Vector3, float> axis = byStorey ? c => c.Z : xSpan >= ySpan ? c => c.X : c => c.Y;
        var ordered = centres.OrderBy(c => axis(c.Centre)).ThenBy(c => c.Room).Select(c => c.Room).ToArray();
        int cut = ordered.Length / 2;
        if (byStorey)
        {
            // Keep a storey together where possible: cut at the floor-height gap nearest the middle.
            int best = -1; float bestScore = float.MaxValue;
            for (int i = 1; i < ordered.Length; i++)
            {
                float gap = Centre(scene.Rooms[ordered[i]]).Z - Centre(scene.Rooms[ordered[i - 1]]).Z;
                if (gap < 1f) continue;
                float score = MathF.Abs(i - ordered.Length / 2f);
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best > 0) cut = best;
        }
        return (ordered[..cut], ordered[cut..]);
    }

    static Vector3 Centre(RoomInfo room) => new((room.Min + room.Max) * .5f, room.BottomZ);
}
