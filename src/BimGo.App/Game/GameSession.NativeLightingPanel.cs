using System.Globalization;
using System.Numerics;
using BimGo.Audio;
using BimGo.Calculations;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

namespace BimGo.Game;

internal sealed partial class GameSession
{
    private const int DROP_NATIVE_SET = 1200, NATIVE_MAX_ROOMS = 32;
    private string _nativeRoomFilter = "";
    private bool _nativeShowAllRooms;
    private readonly Stack<byte[]> _nativeUndo = new(), _nativeRedo = new();
    private byte[] _nativeSetupSnapshot;
    private static readonly string[] NATIVE_QUALITIES = { "Draft", "Normal", "High" };

    private int NativeCurrentRoom()
    {
        if (_reviewOpen && _reviewRoom?.Room >= 0) return _reviewRoom.Room;
        return _player == null ? -1 : FindRoom(_player.Feet + Vector3.UnitZ * .3f);
    }
    private void SetNativeRooms(NativeLightingSet set, int[] rooms)
    {
        set.Rooms = rooms.Order().ToArray();
        set.RoomKeys = set.Rooms.Select(i => ExportedSceneInput.RoomKey(Scene, i)).ToArray();
    }
    private void CreateNativeSet()
    {
        if (Scene.Rooms.Length == 0) { _nativeStatus = "This model has no exported room boundaries. Re-export it with rooms to create a native calculation set."; return; }
        if (_nativeLighting.Sets.Count >= 100) { _nativeStatus = "A model can contain up to 100 native sets."; return; }
        int room = NativeCurrentRoom();
        if (room < 0) room = Array.FindIndex(Scene.Rooms, r => !(r.Name ?? "").Contains("stair", StringComparison.OrdinalIgnoreCase) && !(r.Name ?? "").Contains("elevator", StringComparison.OrdinalIgnoreCase));
        if (room < 0) room = 0;
        var set = new NativeLightingSet { Name = "BimGo · " + RoomLabel(room) };
        if (set.Name.Length > 100) set.Name = set.Name[..100];
        SetNativeRooms(set, new[] { room }); _nativeLighting.Sets.Add(set); SelectNativeSet(set.Id);
    }
    private void RememberNativeSetup()
    {
        byte[] next = NativeLightingFiles.Serialize(_nativeLighting);
        if (_nativeSetupSnapshot != null && !next.AsSpan().SequenceEqual(_nativeSetupSnapshot))
        {
            _nativeUndo.Push(_nativeSetupSnapshot); _nativeRedo.Clear();
            if (_nativeUndo.Count > 50)
            {
                var recent = _nativeUndo.Take(50).Reverse().ToArray(); _nativeUndo.Clear();
                foreach (var previous in recent) _nativeUndo.Push(previous);
            }
        }
        _nativeSetupSnapshot = next;
    }
    private void UndoNativeSetup(bool redo)
    {
        var from = redo ? _nativeRedo : _nativeUndo; var to = redo ? _nativeUndo : _nativeRedo;
        if (from.Count == 0) return;
        var restored = NativeLightingFiles.Parse(from.Peek(), out string error);
        if (restored == null) { _nativeStatus = error; return; }
        from.Pop(); to.Push(NativeLightingFiles.Serialize(_nativeLighting)); _nativeLighting = restored;
        _nativeSetupSnapshot = NativeLightingFiles.Serialize(restored);
        _nativeRevision++; _nativeInputRevision++; _nativeObserved = CaptureNativeState();
        InvalidateNativeCalculation(); SaveNativeLightingSettings(); UpdateTitle();
    }

    private float BuildNativeLightingSection(FontAtlas f, InputState input, float x, float y, float w)
    {
        var set = NativeSet;
        if (set == null)
        {
            y += _ui.TextWrapped(f.Body, x, y, w, "Create a calculation set from exported rooms. No imported WWTools results are needed.", UiTheme.TEXT_MUTED, maxLines: 3) + S(12);
            return y;
        }
        if (IsQuickSet(set))
            y += _ui.TextWrapped(f.Body, x, y, w, $"Default settings for one-click calculations ({Key(InputAction.LightingCalcRun)}, Calculate rooms in view). Each quick run replaces this set's rooms.", UiTheme.ACCENT, maxLines: 3) + S(12);
        if (_lightingTab == 0)
        {
            y = BuildNativeRoomList(f, input, set, x, y, w);
            y = NativeHeading(f, x, y, w, "CALCULATION GRID");
            y = NativeNumberRow(f, input, x, y, w, "Workplane height", set.Workplane, 0, 30, v => set.Workplane = v, length: true);
            y = NativeNumberRow(f, input, x, y, w, "Grid spacing", set.Spacing, .25, 50, v => set.Spacing = v, length: true);
            y = NativeNumberRow(f, input, x, y, w, "Boundary offset", set.BoundaryOffset, 0, 20, v => set.BoundaryOffset = v, length: true);
            y = NativeNumberRow(f, input, x, y, w, "Domain margin", set.Margin, 0, 100, v => set.Margin = v, length: true);
            y += _ui.TextWrapped(f.Body, x, y, w, "Workplane height is above the zone bottom, or above each floor when walking-surface points are enabled. Use the height required for the assessment.", UiTheme.TEXT_MUTED, maxLines: 4) + S(14);
            y = NativeHeading(f, x, y, w, "POINT EXCLUSIONS");
            float half = (w - S(8)) / 2;
            if (NativeButton(f, input, x, y, half, S(34), "Pick points in ISO"))
            { _nativePickMode = 1; _nativeStatus = "In ISO review, click a calculation dot/value to exclude it. Use Finish excluding when done."; }
            if (NativeButton(f, input, x + half + S(8), y, half, S(34), $"Restore manual points ({set.ExcludedPoints.Length})", enabled: set.ExcludedPoints.Length > 0))
            { set.ExcludedPoints = Array.Empty<NativeExcludedPoint>(); NativeSetupChanged(); }
            y += S(44);
        }
        if (_lightingTab == 1)
        {
            y = NativeHeading(f, x, y, w, "DIFFUSE REFLECTANCE · 0–0.95");
            y = NativeNumberRow(f, input, x, y, w, "Ceiling", set.Ceiling, 0, .95, v => set.Ceiling = v);
            y = NativeNumberRow(f, input, x, y, w, "Walls", set.Wall, 0, .95, v => set.Wall = v);
            y = NativeNumberRow(f, input, x, y, w, "Floor", set.Floor, 0, .95, v => set.Floor = v);
            y = NativeNumberRow(f, input, x, y, w, "Furniture", set.Furniture, 0, .95, v => set.Furniture = v);
            y = NativeNumberRow(f, input, x, y, w, "LLF / output", set.LightLossFactor, 0, 1, v => set.LightLossFactor = v);
        }
        if (_lightingTab == 2)
        {
            y = NativeHeading(f, x, y, w, "QUALITY");
            int quality = Segmented(f, input, x, y, w, NATIVE_QUALITIES, set.Quality);
            if (quality != set.Quality) { set.Quality = quality; NativeSetupChanged(); }
            y += S(44);
            y = NativeHeading(f, x, y, w, "OPTIONS");
            bool reflect = Checkbox(f, input, x, y, w, "Interreflection", set.Interreflection); y += S(32);
            if (reflect != set.Interreflection) { set.Interreflection = reflect; NativeSetupChanged(); }
            bool walking = Checkbox(f, input, x, y, w, "Points on walking surfaces", set.WalkingSurfaces); y += S(32);
            if (walking != set.WalkingSurfaces) { set.WalkingSurfaces = walking; NativeSetupChanged(); }
            bool estimates = Checkbox(f, input, x, y, w, "Allow estimated photometry", set.AllowEstimatedPhotometry); y += S(32);
            if (estimates != set.AllowEstimatedPhotometry) { set.AllowEstimatedPhotometry = estimates; NativeSetupChanged(); }
            bool correction = Checkbox(f, input, x, y, w, "Auto mounting (light centres)", set.CorrectRecessedSources); y += S(32);
            if (correction != set.CorrectRecessedSources) { set.CorrectRecessedSources = correction; NativeSetupChanged(); }
            bool excludeFurniture = Checkbox(f, input, x, y, w, "Exclude points under furniture", set.AutoExcludeFurniture); y += S(32);
            if (excludeFurniture != set.AutoExcludeFurniture) { set.AutoExcludeFurniture = excludeFurniture; NativeSetupChanged(); }
            y = BuildNativeCalculationSelection(f, input, x, y, w);
            bool automatic = Checkbox(f, input, x, y, w, "Auto recalculate after edits", set.Automatic); y += S(36);
            if (automatic != set.Automatic) { set.Automatic = automatic; NativeSetupChanged(physical: false); if (!automatic) _nativeQueued = false; }
        }
        if (_lightingTab == 5)
        {
            foreach (string warning in _nativeRun?.Input.Warnings ?? new List<string> { "Uses exported model geometry, including any proxies/omissions. ISO cutaways and display hiding do not remove physical surfaces.", "Electric light only. Scalar diffuse reflection; no daylight, glossy or glass transport. Not certified as identical to WWTools extraction.", "Missing IES is an error unless estimated photometry is explicitly enabled. Use the FIXTURES tab to import full IES data." })
                y += _ui.TextWrapped(f.Body, x, y, w, warning, IsNativeAttention(warning) ? UiTheme.MEASURE_LABEL : UiTheme.TEXT_SOFT, maxLines: 6) + S(10);
        }
        if (_nativePickMode != 0)
        {
            if (NativeButton(f, input, x, y, w, S(34), "Finish excluding", primary: true)) _nativePickMode = 0;
            y += S(44);
        }
        return y + S(14);
    }

    private static bool IsNativeAttention(string warning) => warning.Contains("no exported light source", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("estimated", StringComparison.OrdinalIgnoreCase) && warning.Contains("source(s)", StringComparison.Ordinal);

    /// <summary>The set's rooms as a checklist. Checked rooms are the rooms a run calculates.</summary>
    private float BuildNativeRoomList(FontAtlas f, InputState input, NativeLightingSet set, float x, float y, float w)
    {
        y = NativeHeading(f, x, y, w, $"ROOMS TO CALCULATE · {set.Rooms.Length} of {NATIVE_MAX_ROOMS} max");
        float third = (w - S(16)) / 3;
        int current = NativeCurrentRoom();
        if (NativeButton(f, input, x, y, third, S(34), "Add current room", enabled: current >= 0 && !set.Rooms.Contains(current)))
            ToggleNativeRoom(set, current);
        if (NativeButton(f, input, x + third + S(8), y, third, S(34), "Only current room", enabled: current >= 0))
        { SetNativeRooms(set, new[] { current }); NativeSetupChanged(); }
        if (NativeButton(f, input, x + 2 * (third + S(8)), y, third, S(34), _nativeRoomFilter.Length == 0 ? "Find rooms…" : "Filter: " + _nativeRoomFilter))
            BeginTextEdit("FIND ROOMS (NUMBER OR NAME)", _nativeRoomFilter, 60, text => { _nativeRoomFilter = text.Trim(); _nativeShowAllRooms = false; });
        y += S(42);
        if (current < 0) y += _ui.TextWrapped(f.Body, x, y, w, "Stand in a room, or select a room in ISO review, to use the current-room buttons.", UiTheme.TEXT_FAINT, maxLines: 2) + S(6);

        // Checked rooms first, then unchecked rooms that match the filter (or every room when expanded).
        var matches = Enumerable.Range(0, Scene.Rooms.Length).Where(i => !set.Rooms.Contains(i)
            && (_nativeRoomFilter.Length > 0 ? RoomLabel(i).Contains(_nativeRoomFilter, StringComparison.OrdinalIgnoreCase) : _nativeShowAllRooms)).ToArray();
        float rowH = S(30);
        foreach (int room in set.Rooms.Where(i => i < Scene.Rooms.Length).Concat(matches).ToArray())
        {
            bool on = set.Rooms.Contains(room);
            if (on) _ui.Rect(x, y - S(4), w, rowH, Rgba.WithAlpha(UiTheme.ACCENT, .07f));
            if (Checkbox(f, input, x + S(8), y + S(3), w - S(16), RoomLabel(room) + (room == current ? "   · current" : ""), on) != on) ToggleNativeRoom(set, room);
            y += rowH;
        }
        y += S(8);
        if (_nativeRoomFilter.Length > 0)
        {
            if (matches.Length == 0) y += _ui.TextWrapped(f.Body, x, y, w, $"No other rooms match \"{_nativeRoomFilter}\".", UiTheme.TEXT_FAINT, maxLines: 2) + S(6);
            float half = (w - S(8)) / 2;
            if (NativeButton(f, input, x, y, half, S(34), $"Check all {matches.Length} matches", enabled: matches.Length > 0))
            {
                var rooms = set.Rooms.Concat(matches).Distinct().ToArray();
                if (rooms.Length > NATIVE_MAX_ROOMS) _nativeStatus = $"A set holds up to {NATIVE_MAX_ROOMS} rooms; narrow the filter or split the rooms across sets.";
                else { SetNativeRooms(set, rooms); NativeSetupChanged(); }
            }
            if (NativeButton(f, input, x + half + S(8), y, half, S(34), "Clear filter")) _nativeRoomFilter = "";
            y += S(42);
        }
        else
        {
            if (NativeButton(f, input, x, y, w, S(34), _nativeShowAllRooms ? "Hide unchecked rooms" : $"Show all {Scene.Rooms.Length} rooms")) _nativeShowAllRooms = !_nativeShowAllRooms;
            y += S(42);
        }
        if (NativeButton(f, input, x, y, w, S(34), current >= 0 && set.Rooms.Contains(current) ? "Rerun current room only · " + RoomLabel(current) : "Rerun current room only",
                enabled: current >= 0 && set.Rooms.Contains(current)))
            RunNativeCalculation(ExportedSceneInput.RoomKey(Scene, current));
        y += S(40);
        y += _ui.TextWrapped(f.Body, x, y, w, "RUN calculates every checked room together. A rerun keeps the other rooms' last results.", UiTheme.TEXT_MUTED, maxLines: 3) + S(16);
        return y;
    }
    private void ToggleNativeRoom(NativeLightingSet set, int room)
    {
        var rooms = set.Rooms.ToList();
        if (rooms.Contains(room)) { if (rooms.Count == 1) { _nativeStatus = "Keep at least one room in a set."; return; } rooms.Remove(room); }
        else { if (rooms.Count >= NATIVE_MAX_ROOMS) { _nativeStatus = $"A set holds up to {NATIVE_MAX_ROOMS} rooms."; return; } rooms.Add(room); }
        SetNativeRooms(set, rooms.ToArray()); NativeSetupChanged();
    }

    /// <summary>A section heading with a rule under it.</summary>
    private float NativeHeading(FontAtlas f, float x, float y, float w, string label)
    {
        _ui.Text(f.Small, x, y, label, UiTheme.ACCENT, S(1.4f));
        y += f.Small.LineHeight + S(6);
        _ui.Rect(x, y, w, MathF.Max(1, UiScale), UiTheme.DIVIDER);
        return y + S(10);
    }
    /// <summary>A body-size button for the lighting workspace (SmallButton's caps are too small to scan).</summary>
    private bool NativeButton(FontAtlas f, InputState input, float x, float y, float w, float h, string label, bool primary = false, bool enabled = true)
    {
        bool hover = enabled && Hover(input, x, y, w, h);
        _ui.Rect(x, y, w, h, primary ? Rgba.WithAlpha(UiTheme.ACCENT, hover ? .3f : .18f) : hover ? Rgba.Hex(0xFFFFFF, 0.1f) : UiTheme.CONTROL);
        _ui.Outline(x, y, w, h, MathF.Max(1f, UiScale), primary || hover ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
        _ui.TextWrapped(f.Body, x + S(10), y + h * 0.5f - f.Body.LineHeight * 0.5f, w - S(20), label,
            !enabled ? UiTheme.TEXT_FAINT : hover ? UiTheme.ACCENT : UiTheme.TEXT, maxLines: 1);
        bool clicked = hover && input.LeftPressed;
        if (clicked) { input.ConsumeClicks(); Sound.Play(SoundId.UiClick); }
        return clicked;
    }
    private float NativeNumberRow(FontAtlas f, InputState input, float x, float y, float w, string label, double value, double min, double max, Action<double> assign, bool length = false)
    {
        bool metric = length && !UnitFormat.IsImperial; double factor = metric ? .3048 : 1;
        string units = length ? (metric ? " m" : " ft") : "";
        string shown = (value * factor).ToString("0.###", CultureInfo.InvariantCulture) + units;
        float h = S(34), fieldW = MathF.Min(S(180), w * .4f), fx = x + w - fieldW;
        bool hover = Hover(input, x, y, w, h);
        _ui.Text(f.Body, x, y + h * 0.5f - f.Body.LineHeight * 0.5f, label, hover ? UiTheme.ACCENT : UiTheme.TEXT_SOFT);
        _ui.Rect(fx, y, fieldW, h, hover ? Rgba.Hex(0xFFFFFF, 0.1f) : UiTheme.CONTROL);
        _ui.Outline(fx, y, fieldW, h, MathF.Max(1f, UiScale), hover ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
        _ui.TextRight(f.Bold, fx + fieldW - S(12), y + h * 0.5f - f.Bold.LineHeight * 0.5f, shown, UiTheme.TEXT);
        bool clicked = hover && input.LeftPressed;
        if (clicked) { input.ConsumeClicks(); Sound.Play(SoundId.UiClick); }
        if (clicked)
            BeginTextEdit(label.ToUpperInvariant() + (length ? (metric ? " (METRES)" : " (DECIMAL FEET)") : ""), (value * factor).ToString("0.######", CultureInfo.InvariantCulture), 30, text =>
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed) || parsed / factor < min || parsed / factor > max)
                { _nativeStatus = $"{label}: enter {(min * factor).ToString("0.###", CultureInfo.InvariantCulture)}–{(max * factor).ToString("0.###", CultureInfo.InvariantCulture)}{units}."; return; }
                assign(parsed / factor); NativeSetupChanged();
            });
        return y + S(42);
    }
}
