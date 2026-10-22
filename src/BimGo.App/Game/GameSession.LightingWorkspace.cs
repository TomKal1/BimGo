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
        if (SmallButton(f, input, frame.Ix, y, half, S(28), "NEW SET")) CreateNativeSet();
        if (SmallButton(f, input, frame.Ix + half + S(8), y, half, S(28), "DUPLICATE") && NativeSet != null && _nativeLighting.Sets.Count < 100)
        {
            var clone = NativeLightingFiles.Copy(NativeSet); clone.Id = Guid.NewGuid().ToString("N");
            clone.Name = (clone.Name.Length > 93 ? clone.Name[..93] : clone.Name) + " copy";
            _nativeLighting.Sets.Add(clone); SelectNativeSet(clone.Id);
        }
        float listTop = y + S(76), listBottom = frame.BodyBottom - S(76);
        if (SmallButton(f, input, frame.Ix, y + S(36), half, S(28), "RENAME") && NativeSet != null)
            BeginTextEdit("CALCULATION NAME", NativeSet.Name, 100, text => { if (!string.IsNullOrWhiteSpace(text)) { NativeSet.Name = text.Trim(); NativeSetupChanged(); } });
        if (SmallButton(f, input, frame.Ix + half + S(8), y + S(36), half, S(28), "DELETE") && NativeSet != null)
        {
            _nativeLighting.Sets.Remove(NativeSet); SelectNativeSet(_nativeLighting.Sets.FirstOrDefault()?.Id);
            _nativeStatus = "Set deleted · UNDO SETUP restores it";
        }
        float sy = BeginScroll(_lightingSetsScroll, input, frame.Ix, listTop, leftW, MathF.Max(S(50), listBottom - listTop), out _);
        foreach (var set in _nativeLighting.Sets)
        {
            bool active = set == NativeSet;
            if (active) _ui.Rect(frame.Ix, sy, leftW - S(10), S(36), Rgba.WithAlpha(UiTheme.ACCENT, .15f));
            if (SmallButton(f, input, frame.Ix, sy, leftW - S(12), S(32), set.Name) && !active) { SelectNativeSet(set.Id); _lightingBodyScroll.Reset(); }
            sy += S(40);
        }
        EndScroll(_lightingSetsScroll, input, sy);
        if (SmallButton(f, input, frame.Ix, frame.BodyBottom - S(66), leftW, S(26), "UNDO SETUP")) UndoNativeSetup(false);
        if (SmallButton(f, input, frame.Ix, frame.BodyBottom - S(32), leftW, S(26), "REDO SETUP")) UndoNativeSetup(true);

        int tab = Tabs(f, input, x, y, w, LIGHTING_TABS, _lightingTab);
        if (tab != _lightingTab) { _lightingTab = tab; _activeSlider = -1; CloseOverlay(); _lightingBodyScroll.Reset(); }
        y += S(42);
        string status = _nativeJob != null ? _nativeStatus + " · " + System.Threading.Volatile.Read(ref _nativeProgress) : _nativeStatus;
        y += _ui.TextWrapped(f.Small, x, y, w, status, UiTheme.TEXT_SOFT, maxLines: 4) + S(16);
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
            cy = BuildLightingCalcsSection(f, input, x, cy, contentW);
        }
        else if (_lightingTab == 4) cy = BuildLightsTab(f, input, x, cy, contentW);
        else cy = BuildNativeLightingSection(f, input, x, cy, contentW);
        EndScroll(_lightingBodyScroll, input, cy);
        int run = FooterAction(_nativeRun == null ? "RUN IN BIMGO" : "RECALCULATE", ShellStyle.Primary, NativeSet != null);
        int cancel = FooterAction("CANCEL", enabled: _nativeJob != null || _nativeQueued);
        int review = FooterAction("REVIEW MODEL");
        int html = FooterAction("REPORT HTML…", enabled: _nativeRun != null && _nativeShown);
        int action = EndPanel(frame, null, "Setup and results are separate tabs · display cuts preserve the physical calculation enclosure");
        if (action == run) RunNativeCalculation();
        else if (action == cancel) CancelNativeCalculation();
        else if (action == review) ReviewFromLightingWorkspace();
        else if (action == html) ExportNativeCalculation(0);
    }
}
