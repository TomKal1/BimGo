using System.Globalization;
using System.Numerics;
using BimGo.Calculations;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;

namespace BimGo.Game;

internal sealed partial class GameSession
{
    private const int DROP_NATIVE_SET = 1200, DROP_NATIVE_ROOMS = 1201;
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
            y += _ui.TextWrapped(f.Small, x, y, w, "Create a calculation set from exported rooms. No imported WWTools results are needed.", UiTheme.TEXT_MUTED, maxLines: 3) + S(12);
            return y;
        }
        if (_lightingTab == 0)
        {
            if (DropdownButton(f, input, x, y, w, S(30), $"ROOMS · {set.Rooms.Length} selected", IsDropdownOpen(set, DROP_NATIVE_ROOMS)))
            {
                var labels = Scene.Rooms.Select((r, i) => (set.Rooms.Contains(i) ? "✓ " : "   ") + RoomLabel(i)).ToArray();
                OpenDropdown(set, DROP_NATIVE_ROOMS, x, y, w, S(30), labels, -1, i =>
                {
                    var rooms = set.Rooms.ToList();
                    if (rooms.Contains(i)) { if (rooms.Count == 1) { _nativeStatus = "Keep at least one room in a set."; return; } rooms.Remove(i); }
                    else { if (rooms.Count >= 32) { _nativeStatus = "Select up to 32 rooms in one set."; return; } rooms.Add(i); }
                    SetNativeRooms(set, rooms.ToArray()); NativeSetupChanged();
                });
            }
            y += S(38);
            if (SmallButton(f, input, x, y, w, S(26), "USE CURRENT / REVIEW ROOM"))
            {
                int room = NativeCurrentRoom();
                if (room < 0) _nativeStatus = "Stand in a room, or select an exported room in ISO review.";
                else { SetNativeRooms(set, new[] { room }); NativeSetupChanged(); }
            }
            y += S(34);
            y += _ui.TextWrapped(f.Small, x, y, w, string.Join(" · ", set.Rooms.Where(i => i < Scene.Rooms.Length).Select(RoomLabel)), UiTheme.TEXT_MUTED, maxLines: 3) + S(6);
            if (SmallButton(f, input, x, y, w, S(26), "RERUN CURRENT / REVIEW ROOM ONLY"))
            {
                int room = NativeCurrentRoom();
                if (room < 0 || !set.Rooms.Contains(room)) _nativeStatus = "Choose a current/review room belonging to this set.";
                else RunNativeCalculation(ExportedSceneInput.RoomKey(Scene, room));
            }
            y += S(34);
            if (SmallButton(f, input, x, y, w, S(26), "PICK POINTS TO EXCLUDE IN ISO"))
            { _nativePickMode = 1; _nativeStatus = "In ISO review, click a calculation dot/value to exclude it. Use Cancel picking to finish."; }
            y += S(32);
            if (SmallButton(f, input, x, y, w, S(26), $"RESTORE MANUAL POINTS ({set.ExcludedPoints.Length})"))
            { set.ExcludedPoints = Array.Empty<NativeExcludedPoint>(); NativeSetupChanged(); }
            y += S(32);
            y = NativeNumberRow(f, input, x, y, w, "Workplane", set.Workplane, 0, 30, v => set.Workplane = v, length: true);
            y = NativeNumberRow(f, input, x, y, w, "Grid spacing", set.Spacing, .25, 50, v => set.Spacing = v, length: true);
            y = NativeNumberRow(f, input, x, y, w, "Boundary offset", set.BoundaryOffset, 0, 20, v => set.BoundaryOffset = v, length: true);
            y = NativeNumberRow(f, input, x, y, w, "Domain margin", set.Margin, 0, 100, v => set.Margin = v, length: true);
            y += _ui.TextWrapped(f.Small, x, y, w, "Workplane height is above the zone bottom, or above each floor when walking-surface points are enabled. Use the height required for the assessment.", UiTheme.TEXT_MUTED, maxLines: 4) + S(12);
        }
        if (_lightingTab == 1)
        {
            _ui.Text(f.Small, x, y, "DIFFUSE REFLECTANCE · 0–0.95", UiTheme.TEXT_MUTED); y += S(22);
            y = NativeNumberRow(f, input, x, y, w, "Ceiling", set.Ceiling, 0, .95, v => set.Ceiling = v);
            y = NativeNumberRow(f, input, x, y, w, "Walls", set.Wall, 0, .95, v => set.Wall = v);
            y = NativeNumberRow(f, input, x, y, w, "Floor", set.Floor, 0, .95, v => set.Floor = v);
            y = NativeNumberRow(f, input, x, y, w, "Furniture", set.Furniture, 0, .95, v => set.Furniture = v);
            y = NativeNumberRow(f, input, x, y, w, "LLF / output", set.LightLossFactor, 0, 1, v => set.LightLossFactor = v);
        }
        if (_lightingTab == 2)
        {
            int quality = Segmented(f, input, x, y, w, NATIVE_QUALITIES, set.Quality);
            if (quality != set.Quality) { set.Quality = quality; NativeSetupChanged(); }
            y += S(40);
            bool reflect = Checkbox(f, input, x, y, w, "Interreflection", set.Interreflection); y += S(27);
            if (reflect != set.Interreflection) { set.Interreflection = reflect; NativeSetupChanged(); }
            bool walking = Checkbox(f, input, x, y, w, "Points on walking surfaces", set.WalkingSurfaces); y += S(27);
            if (walking != set.WalkingSurfaces) { set.WalkingSurfaces = walking; NativeSetupChanged(); }
            bool estimates = Checkbox(f, input, x, y, w, "Allow estimated photometry", set.AllowEstimatedPhotometry); y += S(27);
            if (estimates != set.AllowEstimatedPhotometry) { set.AllowEstimatedPhotometry = estimates; NativeSetupChanged(); }
            bool correction = Checkbox(f, input, x, y, w, "Auto mounting (light centres)", set.CorrectRecessedSources); y += S(27);
            if (correction != set.CorrectRecessedSources) { set.CorrectRecessedSources = correction; NativeSetupChanged(); }
            bool excludeFurniture = Checkbox(f, input, x, y, w, "Exclude points under furniture", set.AutoExcludeFurniture); y += S(27);
            if (excludeFurniture != set.AutoExcludeFurniture) { set.AutoExcludeFurniture = excludeFurniture; NativeSetupChanged(); }
            y = BuildNativeCalculationSelection(f, input, x, y, w);
            bool automatic = Checkbox(f, input, x, y, w, "Auto recalculate after edits", set.Automatic); y += S(33);
            if (automatic != set.Automatic) { set.Automatic = automatic; NativeSetupChanged(physical: false); if (!automatic) _nativeQueued = false; }
        }
        if (_lightingTab == 5)
        {
            foreach (string warning in _nativeRun?.Input.Warnings ?? new List<string> { "Uses exported model geometry, including any proxies/omissions. ISO cutaways and display hiding do not remove physical surfaces.", "Electric light only. Scalar diffuse reflection; no daylight, glossy or glass transport. Not certified as identical to WWTools extraction.", "Missing IES is an error unless estimated photometry is explicitly enabled. Use the FIXTURES tab to import full IES data." })
                y += _ui.TextWrapped(f.Small, x, y, w, warning, UiTheme.TEXT_MUTED, maxLines: 6) + S(6);
        }
        if (_nativePickMode != 0)
        {
            if (SmallButton(f, input, x, y, w, S(26), "FINISH EXCLUDING")) _nativePickMode = 0;
            y += S(32);
        }
        return y + S(14);
    }
    private float NativeNumberRow(FontAtlas f, InputState input, float x, float y, float w, string label, double value, double min, double max, Action<double> assign, bool length = false)
    {
        bool metric = length && !UnitFormat.IsImperial; double factor = metric ? .3048 : 1;
        string units = length ? (metric ? " m" : " ft") : "";
        string shown = (value * factor).ToString("0.###", CultureInfo.InvariantCulture) + units;
        if (SmallButton(f, input, x, y, w, S(26), label + ": " + shown))
            BeginTextEdit(label.ToUpperInvariant() + (length ? (metric ? " (METRES)" : " (DECIMAL FEET)") : ""), (value * factor).ToString("0.######", CultureInfo.InvariantCulture), 30, text =>
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed) || parsed / factor < min || parsed / factor > max)
                { _nativeStatus = $"{label}: enter {(min * factor).ToString("0.###", CultureInfo.InvariantCulture)}–{(max * factor).ToString("0.###", CultureInfo.InvariantCulture)}{units}."; return; }
                assign(parsed / factor); NativeSetupChanged();
            });
        return y + S(32);
    }
}
