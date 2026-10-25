using System.Numerics;
using BimGo.Calculations;
using BimGo.Format;
using BimGo.Scene;

namespace BimGo.Game;

/// <summary>
/// One-click lighting calculations with the default settings: the room underfoot in the walkthrough (Ctrl+J), or the
/// rooms in the model review's view (the level shown, or the rooms inside the section box). The defaults are the quick
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
        if (rooms.Count == 0) { Toast("No rooms in view to calculate. Show a level, select a room or widen the section box.", 4f, important: true); return; }
        var set = EnsureQuickSet(rooms[0]);
        if (set == null) { Toast(_nativeStatus, 4f, important: true); return; }
        if (NativeSet != set) SelectNativeSet(set.Id);
        EndQuickSplit();
        _quickParts.Add(rooms.Take(NATIVE_MAX_ROOMS).Distinct().ToArray());
        _quickPartCount = 1;
        StartQuickPart();
        Toast($"Calculating lighting · {scope} · default settings", 3f);
    }

    // ---- Split runs: a domain over the size budget runs as spatial parts. The first part is a normal run; each later
    // part is a partial rerun that solves its own rooms (with only their surroundings as geometry) and keeps the rooms
    // already done, so the last part publishes one result holding every room.
    private readonly List<int[]> _quickParts = new();
    private readonly List<int> _quickDone = new();
    private int _quickPartCount;

    private bool QuickSplitActive => _quickParts.Count > 0;
    private int QuickPartNumber => _quickPartCount - _quickParts.Count + 1;

    private void StartQuickPart()
    {
        var set = QuickSet;
        if (set == null || NativeSet != set) { EndQuickSplit(); return; }
        int[] part = _quickParts[0];
        SetNativeRooms(set, _quickDone.Concat(part).Distinct().ToArray());
        NativeSetupChanged();
        if (_quickDone.Count == 0) RunNativeCalculation();
        else RunNativeCalculation(part.Select(i => ExportedSceneInput.RoomKey(Scene, i)).ToArray(), domainOnly: true);
    }

    /// <summary>A quick run published: the next part, or the end of the split.</summary>
    private void QuickPartFinished()
    {
        if (!QuickSplitActive) return;
        _quickDone.AddRange(_quickParts[0]);
        _quickParts.RemoveAt(0);
        if (_quickParts.Count > 0) { StartQuickPart(); return; }
        if (_quickPartCount > 1) Toast($"Lighting calculated · {_quickDone.Count} rooms in {_quickPartCount} parts (too large for one run)", 4f);
        EndQuickSplit();
    }

    /// <summary>
    /// A run was over a size budget: halve its rooms spatially and run the first half. The quick set splits an automatic
    /// recalculation the same way. False when it cannot split (a single room, or not the quick set).
    /// </summary>
    private bool SplitQuickCalculation(string reason)
    {
        var set = QuickSet;
        if (set == null || NativeSet != set) return false;
        if (!QuickSplitActive)
        {
            if (set.Rooms.Length < 2) return false;
            _quickParts.Add(set.Rooms.ToArray());
            _quickPartCount = 1;
        }
        int[] part = _quickParts[0];
        if (part.Length < 2) return false; // one room alone is over budget: the failure is reported as it stands
        var (first, second) = RoomGroups.Bisect(Scene, part);
        _quickParts[0] = first;
        _quickParts.Insert(1, second);
        _quickPartCount++;
        Utilities.Log_Utils.Write($"Quick calc split ({reason}): {first.Length} + {second.Length} rooms; {_quickPartCount} parts");
        Toast($"Too large for one calculation · running {_quickPartCount} parts", 3f);
        StartQuickPart();
        return true;
    }

    private void EndQuickSplit()
    {
        _quickParts.Clear();
        _quickDone.Clear();
        _quickPartCount = 0;
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
    /// Rooms in the review view: with the section box on, rooms whose floor is inside it and at least half of whose plan it
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
    private string QuickCalcStatus => _nativeJob != null ? (QuickSplitActive && _quickPartCount > 1 ? $"Calculating part {QuickPartNumber} of {_quickPartCount} · " : "Calculating · ")
            + System.Threading.Volatile.Read(ref _nativeProgress)
        : _nativeQueued ? "Calculation queued"
        : _nativeStatus.StartsWith("Inputs need attention", StringComparison.Ordinal) || _nativeStatus.StartsWith("Calculation incomplete", StringComparison.Ordinal) ? _nativeStatus
        : null;
}
