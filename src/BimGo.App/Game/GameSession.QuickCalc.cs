using System.Numerics;
using BimGo.Format;
using BimGo.Scene;

namespace BimGo.Game;

/// <summary>
/// One-click lighting calculations with the default settings: the room underfoot in the walkthrough (Ctrl+J), or the
/// rooms in the model review's view (the level shown, or the rooms inside the crop box). The defaults are the quick
/// set's settings, edited in the lighting workspace like any other set.
/// </summary>
internal sealed partial class GameSession
{
    private const string QUICK_SET_NAME = "Quick calc · default settings";
    private readonly List<int> _reviewVisibleRooms = new();
    private int _reviewVisibleKey = int.MinValue, _reviewVisibleTotal;

    private NativeLightingSet QuickSet => _nativeLighting?.Sets.FirstOrDefault(s => s.Id == _nativeLighting.QuickSetId);
    private bool IsQuickSet(NativeLightingSet set) => set != null && set.Id == _nativeLighting.QuickSetId;

    /// <summary>The quick set, made on first use (a set always holds at least one room, so it starts with <paramref name="room"/>).</summary>
    private NativeLightingSet EnsureQuickSet(int room)
    {
        var set = QuickSet;
        if (set != null) return set;
        if (_nativeLighting.Sets.Count >= 100) { _nativeStatus = "A model can contain up to 100 native sets; delete one to make the quick calc set."; return null; }
        set = new NativeLightingSet { Name = QUICK_SET_NAME };
        SetNativeRooms(set, new[] { Math.Clamp(room, 0, Scene.Rooms.Length - 1) });
        _nativeLighting.Sets.Add(set);
        _nativeLighting.QuickSetId = set.Id;
        return set;
    }

    /// <summary>Runs the quick set on these rooms. Results show when it finishes, wherever the user is.</summary>
    private void RunQuickCalculation(IReadOnlyList<int> rooms, string scope)
    {
        if (Scene.Rooms.Length == 0) { Toast("This model has no exported room boundaries. Re-export it with rooms to calculate lighting.", 4f, important: true); return; }
        if (rooms.Count == 0) { Toast("No rooms in view to calculate. Show a level, select a room or widen the crop.", 4f, important: true); return; }
        var set = EnsureQuickSet(rooms[0]);
        if (set == null) { Toast(_nativeStatus, 4f, important: true); return; }
        if (NativeSet != set) SelectNativeSet(set.Id);
        SetNativeRooms(set, rooms.Take(NATIVE_MAX_ROOMS).Distinct().ToArray());
        NativeSetupChanged();
        RunNativeCalculation();
        Toast($"Calculating lighting · {scope} · default settings", 3f);
    }

    /// <summary>Walkthrough (Ctrl+J, Pause → LIGHTS): the room the player stands in.</summary>
    private void QuickCalculateCurrentRoom()
    {
        int room = FindRoom(_player.Feet + Vector3.UnitZ * .3f);
        if (room < 0) { Toast("Stand inside an exported room to calculate it.", 3f, important: true); return; }
        RunQuickCalculation(new[] { room }, RoomLabel(room));
    }

    /// <summary>Model review (Ctrl+J, the dock's primary button): every room in view, nearest the view centre first.</summary>
    private void QuickCalculateReviewRooms()
    {
        var rooms = ReviewVisibleRooms();
        string scope = _reviewRoom != null && rooms.Count == 1 ? _reviewRoom.Title
            : rooms.Count == 1 ? RoomLabel(rooms[0]) : $"{rooms.Count} rooms in {_reviewScope}";
        if (_reviewVisibleTotal > rooms.Count) Toast($"{_reviewVisibleTotal} rooms are in view; a calculation holds {NATIVE_MAX_ROOMS}, so the {NATIVE_MAX_ROOMS} nearest the view centre run.", 5f);
        RunQuickCalculation(rooms, scope);
    }

    /// <summary>
    /// Rooms in the review view: with the crop box on, rooms whose floor is inside it and at least half of whose plan it
    /// covers; otherwise the level shown (or every room). At most <see cref="NATIVE_MAX_ROOMS"/>, nearest the view centre.
    /// </summary>
    private List<int> ReviewVisibleRooms()
    {
        Vector3 boxMin = _section.BoxMin - Scene.OriginOffset, boxMax = _section.BoxMax - Scene.OriginOffset;
        var hash = new HashCode();
        hash.Add(_section.BoxOn); hash.Add(boxMin); hash.Add(boxMax); hash.Add(_reviewLevel); hash.Add(Scene.Rooms.Length);
        int key = hash.ToHashCode();
        if (key == _reviewVisibleKey) return _reviewVisibleRooms;
        _reviewVisibleKey = key;
        _reviewVisibleRooms.Clear();
        var centre = new Vector2(_reviewTarget.X, _reviewTarget.Y);
        var found = new List<(int Room, float Distance)>();
        for (int i = 0; i < Scene.Rooms.Length; i++)
        {
            RoomInfo room = Scene.Rooms[i];
            if (room.Loops == null || room.Loops.Length == 0) continue;
            if (_section.BoxOn)
            {
                if (room.BottomZ < boxMin.Z - .3f || room.BottomZ >= boxMax.Z) continue;
                Vector2 lo = Vector2.Max(room.Min, new Vector2(boxMin.X, boxMin.Y)), hi = Vector2.Min(room.Max, new Vector2(boxMax.X, boxMax.Y));
                Vector2 size = room.Max - room.Min;
                if (hi.X <= lo.X || hi.Y <= lo.Y || (hi.X - lo.X) * (hi.Y - lo.Y) < .5f * MathF.Max(size.X * size.Y, 1e-4f)) continue;
            }
            else if (_reviewLevel >= 0 && Scene.Levels.Length > 0 && LevelIndexAt(room.BottomZ) != _reviewLevel) continue;
            found.Add((i, Vector2.DistanceSquared((room.Min + room.Max) * .5f, centre)));
        }
        _reviewVisibleTotal = found.Count;
        foreach (var room in found.OrderBy(r => r.Distance).Take(NATIVE_MAX_ROOMS)) _reviewVisibleRooms.Add(room.Room);
        return _reviewVisibleRooms;
    }

    /// <summary>The lighting workspace on the quick set's settings (the defaults one-click runs use).</summary>
    private void OpenQuickCalcSettings()
    {
        int room = NativeCurrentRoom();
        var set = EnsureQuickSet(room >= 0 ? room : 0);
        if (set == null) { Toast(_nativeStatus, 4f, important: true); return; }
        if (NativeSet != set) SelectNativeSet(set.Id);
        _lightingTab = 0;
        _lightingBodyScroll.Reset();
        OpenLightingWorkspace();
    }

    /// <summary>What the quick calc is doing, for the review dock and Pause → LIGHTS (null when idle and nothing to report).</summary>
    private string QuickCalcStatus => _nativeJob != null ? "Calculating · " + System.Threading.Volatile.Read(ref _nativeProgress)
        : _nativeQueued ? "Calculation queued"
        : _nativeStatus.StartsWith("Inputs need attention", StringComparison.Ordinal) || _nativeStatus.StartsWith("Calculation incomplete", StringComparison.Ordinal) ? _nativeStatus
        : null;
}
