using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;

namespace BimGo.Game;

internal sealed partial class GameSession
{
    private bool _lightingOpen;
    private int _lightingTab;
    private readonly ScrollRegion _lightingSetsScroll = new(), _lightingBodyScroll = new();
    private static readonly string[] LIGHTING_TABS = { "SETUP", "MATERIALS", "SOLVER", "RESULTS", "FIXTURES", "BASIS" };

    private void OpenLightingWorkspace()
    {
        if (!_paused) SetPaused(true);
        _lightingOpen = true; _menuPreview = false; _activeSlider = -1;
        CloseOverlay();
    }
    private bool CloseLightingWorkspace()
    {
        if (!_lightingOpen) return false;
        if (CloseOverlay()) return true;
        _lightingOpen = false; _activeSlider = -1; return true;
    }
    private void ReviewFromLightingWorkspace()
    {
        CloseOverlay(); _lightingOpen = false;
        if (_reviewOpen) SetPaused(false); else OpenLightingReview();
    }
    private void BuildLightingWorkspace()
    {
        var f = _ui.Atlas; var input = _window.Input;
        if (!input.LeftDown) _activeSlider = -1;
        var frame = BeginPanel(PanelSize.Wide, "LIGHTING", UiTheme.ACCENT, NativeSet?.Name ?? "Create a BimGo calculation set", overlays: true);
        float leftW = MathF.Min(S(230), frame.Iw * .26f), gap = S(24);
        float x = frame.Ix + leftW + gap, w = frame.Iw - leftW - gap, y = frame.BodyTop;
        _ui.Rect(x - gap / 2, y, MathF.Max(1, UiScale), frame.BodyHeight, UiTheme.DIVIDER);
        float half = (leftW - S(8)) / 2;
        if (NativeButton(f, input, frame.Ix, y, half, S(32), "New set")) CreateNativeSet();
        if (NativeButton(f, input, frame.Ix + half + S(8), y, half, S(32), "Duplicate", enabled: NativeSet != null) && NativeSet != null && _nativeLighting.Sets.Count < 100)
        {
            var clone = NativeLightingFiles.Copy(NativeSet); clone.Id = Guid.NewGuid().ToString("N");
            clone.Name = (clone.Name.Length > 93 ? clone.Name[..93] : clone.Name) + " copy";
            _nativeLighting.Sets.Add(clone); SelectNativeSet(clone.Id);
        }
        float listTop = y + S(108), listBottom = frame.BodyBottom - S(84);
        if (NativeButton(f, input, frame.Ix, y + S(40), half, S(32), "Rename", enabled: NativeSet != null) && NativeSet != null)
            BeginTextEdit("CALCULATION NAME", NativeSet.Name, 100, text => { if (!string.IsNullOrWhiteSpace(text)) { NativeSet.Name = text.Trim(); NativeSetupChanged(); } });
        if (NativeButton(f, input, frame.Ix + half + S(8), y + S(40), half, S(32), "Delete", enabled: NativeSet != null) && NativeSet != null)
        {
            _nativeLighting.Sets.Remove(NativeSet); SelectNativeSet(_nativeLighting.Sets.FirstOrDefault()?.Id);
            _nativeStatus = "Set deleted · UNDO SETUP restores it";
        }
        _ui.Text(f.Small, frame.Ix, y + S(86), $"CALCULATION SETS · {_nativeLighting.Sets.Count}", UiTheme.ACCENT, S(1.4f));
        float sy = BeginScroll(_lightingSetsScroll, input, frame.Ix, listTop, leftW, MathF.Max(S(50), listBottom - listTop), out _);
        foreach (var set in _nativeLighting.Sets)
        {
            bool active = set == NativeSet;
            if (NativeButton(f, input, frame.Ix, sy, leftW - S(12), S(48), set.Name, primary: active) && !active) { SelectNativeSet(set.Id); _lightingBodyScroll.Reset(); }
            _ui.Text(f.Small, frame.Ix + S(10), sy + S(33), set.Rooms.Length == 1 ? "1 room" : $"{set.Rooms.Length} rooms", active ? UiTheme.ACCENT : UiTheme.TEXT_FAINT);
            sy += S(54);
        }
        EndScroll(_lightingSetsScroll, input, sy);
        if (NativeButton(f, input, frame.Ix, frame.BodyBottom - S(76), leftW, S(32), "Undo setup", enabled: _nativeUndo.Count > 0)) UndoNativeSetup(false);
        if (NativeButton(f, input, frame.Ix, frame.BodyBottom - S(36), leftW, S(32), "Redo setup", enabled: _nativeRedo.Count > 0)) UndoNativeSetup(true);

        int tab = Tabs(f, input, x, y, w, LIGHTING_TABS, _lightingTab);
        if (tab != _lightingTab) { _lightingTab = tab; _activeSlider = -1; CloseOverlay(); _lightingBodyScroll.Reset(); }
        y += S(42);
        string status = _nativeJob != null ? _nativeStatus + " · " + System.Threading.Volatile.Read(ref _nativeProgress) : _nativeStatus;
        y = NativeStatusCallout(f, x, y, w, status) + S(14);
        float cy = BeginScroll(_lightingBodyScroll, input, x, y, w, MathF.Max(S(40), frame.BodyBottom - y), out _, wheel: _activeSlider < 0);
        float contentW = w - S(14);
        if (_lightingTab == 3)
        {
            if (_nativeRun != null && _nativeShown)
            {
                float bw = (contentW - S(16)) / 3;
                if (SmallButton(f, input, x, cy, bw, S(28), "POINT CSV…")) ExportNativeCalculation(1);
                if (SmallButton(f, input, x + bw + S(8), cy, bw, S(28), "RESULT JSON…")) ExportNativeCalculation(2);
                if (SmallButton(f, input, x + 2 * (bw + S(8)), cy, bw, S(28), "INPUT SCENE…")) ExportNativeCalculation(3);
                cy += S(40);
            }
            if (_calc != null)
            {
                if (SmallButton(f,input,x,cy,contentW,S(30),"CALCULATED IMAGES · PNG…")) OpenCalculatedImages();
                cy+=S(40);
            }
            cy = BuildLightingCalcsSection(f, input, x, cy, contentW);
        }
        else if (_lightingTab == 4) cy = BuildLightsTab(f, input, x, cy, contentW);
        else cy = BuildNativeLightingSection(f, input, x, cy, contentW);
        EndScroll(_lightingBodyScroll, input, cy);
        int rooms = NativeSet?.Rooms.Length ?? 0;
        int run = FooterAction((_nativeRun == null ? "RUN " : "RECALCULATE ") + (rooms == 1 ? "1 ROOM" : $"{rooms} ROOMS"), ShellStyle.Primary, NativeSet != null);
        int cancel = FooterAction("CANCEL", enabled: _nativeJob != null || _nativeQueued);
        int review = FooterAction("REVIEW MODEL");
        int html = FooterAction("REPORT HTML…", enabled: _nativeRun != null && _nativeShown);
        int action = EndPanel(frame, null, "Setup and results are separate tabs · display cuts preserve the physical calculation enclosure");
        if (action == run) RunNativeCalculation();
        else if (action == cancel) CancelNativeCalculation();
        else if (action == review) ReviewFromLightingWorkspace();
        else if (action == html) ExportNativeCalculation(0);
    }

    /// <summary>The run status, tinted by kind so a blocking problem reads differently from progress.</summary>
    private float NativeStatusCallout(FontAtlas f, float x, float y, float w, string status)
    {
        bool problem = status.StartsWith("Inputs need attention", StringComparison.Ordinal) || status.StartsWith("Calculation incomplete", StringComparison.Ordinal)
            || status.Contains("failed", StringComparison.OrdinalIgnoreCase);
        bool done = status.StartsWith("BimGo calculation complete", StringComparison.Ordinal);
        uint tint = problem ? UiTheme.DANGER : done ? UiTheme.GOOD : _nativeJob != null ? UiTheme.ACCENT : UiTheme.TEXT_MUTED;
        float pad = S(10), textH = _ui.TextWrapped(f.Body, 0, 0, w - pad * 2 - S(4), status, UiTheme.TEXT, maxLines: 5, draw: false);
        float h = textH + pad * 2;
        _ui.Rect(x, y, w, h, Rgba.WithAlpha(tint, .1f));
        _ui.Rect(x, y, S(3), h, tint);
        _ui.TextWrapped(f.Body, x + pad + S(4), y + pad, w - pad * 2 - S(4), status, problem ? UiTheme.DANGER : UiTheme.TEXT, maxLines: 5);
        return y + h;
    }
}
