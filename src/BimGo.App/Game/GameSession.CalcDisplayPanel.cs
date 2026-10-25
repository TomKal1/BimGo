using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

namespace BimGo.Game;

/// <summary>
/// One lighting-results panel for every place results are shown: the ISO review dock, Pause → LIGHTS in the
/// walkthrough, and the lighting workspace's RESULTS tab. A change in one shows in the others.
/// </summary>
internal sealed partial class GameSession
{
    private static readonly string[] CALC_PANEL_MODES = { "Materials", "Illuminance", "Luminance", "Calculated" };
    private const int DROP_CALC_PANEL_SET = 1210;
    // The view SHOW RESULTS brings back after it was switched off
    private CalcDisplayMode _calcLastMode = CalcDisplayMode.Illuminance;

    /// <summary>Materials is the model's own colours with the point overlay (the Points display mode).</summary>
    private int CalcPanelModeIndex => _calcMode switch
    {
        CalcDisplayMode.Illuminance => 1,
        CalcDisplayMode.Luminance => 2,
        CalcDisplayMode.Appearance => 3,
        _ => 0
    };

    private void SetCalcMode(CalcDisplayMode mode)
    {
        if (mode != CalcDisplayMode.Off) _calcLastMode = mode;
        if (_calc == null && _calcNames.Count > 0 && _calcLoad == null) { LoadCalc(_calcNames[0], mode, update: false); return; }
        _calcMode = mode;
        _calcBuildKey = int.MinValue;
        RememberCalcView();
    }

    /// <returns>The panel's bottom.</returns>
    private float BuildCalcDisplayPanel(FontAtlas f, InputState input, float x, float y, float w)
    {
        if (!input.LeftDown && (_activeSlider == SLIDER_CALC_OPACITY || _activeSlider == SLIDER_CALC_SCALE)) { _activeSlider = -1; }
        float half = (w - S(8)) * 0.5f;
        y = NativeHeading(f, x, y, w, "LIGHTING RESULTS");

        if (_calcNames.Count == 0 && _calc == null)
        {
            y += _ui.TextWrapped(f.Body, x, y, w, IsFileMode
                ? "No saved results. Run a set in the lighting workspace, or import a saved results file."
                : "No saved results. Run a set in the lighting workspace. WWTools saved runs can also be imported.",
                UiTheme.TEXT_MUTED, maxLines: 4) + S(10);
            if (NativeButton(f, input, x, y, half, S(34), "Import…")) ImportCalc();
            if (NativeButton(f, input, x + half + S(8), y, half, S(34), "Lighting workspace…")) OpenLightingWorkspace();
            y += S(42);
            if (_calcNotice != null) y += _ui.TextWrapped(f.Body, x, y, w, _calcNotice, UiTheme.MEASURE_LABEL, maxLines: 3) + S(6);
            return y + S(8);
        }

        // Which results
        if (DropdownButton(f, input, x, y, w, S(34), _calcLoad != null ? "Loading…" : _calc?.SetName ?? "Select saved results",
            IsDropdownOpen(this, DROP_CALC_PANEL_SET), f.Body))
        {
            var names = _calcNames.ToArray();
            OpenDropdown(this, DROP_CALC_PANEL_SET, x, y, w, S(34), names.Select(DisplayCalcName).ToArray(), Array.IndexOf(names, _calcName),
                i => { if (names[i] != _calcName || _calc == null) LoadCalc(names[i], _calcMode == CalcDisplayMode.Off ? _calcLastMode : _calcMode, update: false); });
        }
        y += S(42);

        // Review always draws results; the walkthrough can hide them
        bool shown = _reviewOpen || _calcMode != CalcDisplayMode.Off;
        if (!_reviewOpen)
        {
            bool show = Checkbox(f, input, x, y + S(3), w, "Show results over the model", shown);
            if (show != shown) SetCalcMode(show ? _calcLastMode : CalcDisplayMode.Off);
            y += S(34);
        }
        if (shown)
        {
            int current = CalcPanelModeIndex;
            int picked = Segmented(f, input, x, y, w, CALC_PANEL_MODES, current);
            if (picked != current)
                SetCalcMode(picked switch { 1 => CalcDisplayMode.Illuminance, 2 => CalcDisplayMode.Luminance, 3 => CalcDisplayMode.Appearance, _ => CalcDisplayMode.Points });
            y += S(44);
        }

        if (_calc != null && shown)
        {
            UpdateCalcTexts();
            y += _ui.TextWrapped(f.Body, x, y, w, _calcSurfaceCaption, UiTheme.TEXT_SOFT, maxLines: 2) + S(2);
            y += _ui.TextWrapped(f.Body, x, y, w, _calcPointCaption, UiTheme.TEXT_SOFT, maxLines: 2) + S(10);

            bool surfaceMode = _calcMode is CalcDisplayMode.Illuminance or CalcDisplayMode.Luminance or CalcDisplayMode.Appearance;
            bool surfaces = surfaceMode ? Checkbox(f, input, x, y, half, "Surfaces", _calcSurfaces) : _calcSurfaces;
            if (!surfaceMode) _ui.Text(f.Body, x, y - S(1), "Material colours", UiTheme.TEXT_MUTED);
            bool points = Checkbox(f, input, x + half + S(8), y, half, "Points", _calcPoints);
            y += S(30);
            bool values = Checkbox(f, input, x, y, half, "Values", _calcValues);
            bool isolines = Checkbox(f, input, x + half + S(8), y, half, "Isolux lines", _calcIsolines);
            y += S(34);
            if (surfaces != _calcSurfaces || points != _calcPoints || values != _calcValues || isolines != _calcIsolines)
            {
                _calcSurfaces = surfaces; _calcPoints = points; _calcValues = values; _calcIsolines = isolines;
                RememberCalcView();
            }

            Text.Clear().Append(_calcOpacity * 100f, 0).Append(" %");
            float opacity = _calcOpacity;
            if (_calcMode == CalcDisplayMode.Appearance) _ui.Text(f.Body, x, y + S(14), "Calculated: opaque", UiTheme.TEXT_MUTED);
            else opacity = Slider(f, input, SLIDER_CALC_OPACITY, x, y, half, "Opacity", Text.Span, _calcOpacity, 0.3f, 1f);
            Text.Clear().Append(LightingCalcs.Display(CalcScaleMax, CalcInLux), CalcInLux ? 0 : 1).Append(CalcInLux ? " lx" : " fc");
            float scale = Slider(f, input, SLIDER_CALC_SCALE, x + half + S(8), y, half, "Scale top", Text.Span, _calcScaleTop, 0.25f, 2f);
            y += S(50);
            if (MathF.Abs(opacity - _calcOpacity) > 1e-4f || MathF.Abs(scale - _calcScaleTop) > 1e-4f)
            {
                _calcOpacity = opacity;
                _calcScaleTop = MathF.Abs(scale - 1f) < 0.04f ? 1f : scale; // snaps to the sheets' scale
                RememberCalcView();
            }
        }

        if (NativeButton(f, input, x, y, half, S(34), "Calculated images…", enabled: _calc != null)) OpenCalculatedImages();
        if (NativeButton(f, input, x + half + S(8), y, half, S(34), "Import…")) ImportCalc();
        y += S(42);
        if (!_reviewOpen && _calc != null && _calcZone == null && _paused)
        {
            if (NativeButton(f, input, x, y, w, S(34), "Go to a calc zone")) { SetPaused(false); StandInCalcZone(null); }
            y += S(42);
        }

        string about = _calc == null ? null
            : $"{_calc.Tool} · {(_calc.Interreflections ? "with interreflections" : "direct light only")} · {_calc.PointCount:N0} points, {_calc.PatchCount:N0} surface patches";
        if (_calc?.IsNative == true) about = $"{(_nativeShown ? "BimGo calculation" : "Saved BimGo calculation · inputs not reverified")} · revision {_calc.ScenarioHash[..Math.Min(8, _calc.ScenarioHash.Length)]} · {_calc.CalculationStatus} · {_calc.PointCount:N0} points";
        if (about != null) y += _ui.TextWrapped(f.Body, x, y, w, about, UiTheme.TEXT_FAINT, maxLines: 3) + S(4);
        if (_calcNotice != null) y += _ui.TextWrapped(f.Body, x, y, w, _calcNotice, UiTheme.MEASURE_LABEL, maxLines: 3) + S(4);
        return y + S(10);
    }

    /// <summary>Pause → LIGHTS: the shared results panel, then the way into (or out of) ISO review and the workspace.</summary>
    private float BuildPauseLightsTab(FontAtlas f, InputState input, float x, float y, float w)
    {
        y = NativeHeading(f, x, y, w, "CALCULATE · DEFAULT SETTINGS");
        float calcW = w - S(100);
        string label = _reviewOpen ? "Calculate rooms in view" : "Calculate this room";
        if (NativeButton(f, input, x, y, calcW, S(38), $"{label} ({Key(InputAction.LightingCalcRun)})", primary: true, enabled: _nativeJob == null))
        {
            SetPaused(false);
            if (_reviewOpen) QuickCalculateReviewRooms(); else QuickCalculateCurrentRoom();
            return y;
        }
        if (NativeButton(f, input, x + calcW + S(8), y, S(92), S(38), "Settings…")) OpenQuickCalcSettings();
        y += S(44);
        string status = QuickCalcStatus;
        if (status != null) y += _ui.TextWrapped(f.Body, x, y, w, status, _nativeJob != null || _nativeQueued ? UiTheme.ACCENT : UiTheme.DANGER, maxLines: 3) + S(6);
        y += S(8);
        y = BuildCalcDisplayPanel(f, input, x, y, w);
        y = NativeHeading(f, x, y, w, "VIEW");
        float half = (w - S(8)) * 0.5f;
        if (_reviewOpen)
        {
            if (NativeButton(f, input, x, y, half, S(34), "Return to walk")) { RememberCalcView(); CloseLightingReview(); SetPaused(false); }
        }
        else if (NativeButton(f, input, x, y, half, S(34), "ISO review")) OpenLightingReview();
        if (NativeButton(f, input, x + half + S(8), y, half, S(34), "Lighting workspace…")) OpenLightingWorkspace();
        y += S(42);
        if (!_reviewOpen) y += _ui.TextWrapped(f.Body, x, y, w, HINT_CALC_CYCLE.Text, UiTheme.TEXT_FAINT, maxLines: 2) + S(4);
        return y;
    }

    /// <summary>
    /// Pause → APPEARANCE, above the settings: says when lighting results are drawn over the model (they cover what
    /// colour-by and the material settings change), with a way to hide them or go to LIGHTS.
    /// </summary>
    /// <returns>The height used (0 when no results are shown).</returns>
    private float BuildAppearanceCalcNotice(FontAtlas f, InputState input, float x, float y, float w)
    {
        if (!CalcShowing && !_reviewOpen) return 0f;
        float top = y, pad = S(10), iw = w - pad * 2 - S(4);
        string message = CalcShowing
            ? $"{char.ToUpperInvariant(ModeLabel(CalcOverlayMode)[0])}{ModeLabel(CalcOverlayMode)[1..]} from “{_calc.SetName}” is drawn over the model. "
              + (_calcMode is CalcDisplayMode.Points or CalcDisplayMode.Off ? "Points and values sit on top of the colours set here." : "It covers the colours and materials set here.")
            : "No lighting results are loaded.";
        if (_reviewOpen && !_reviewSceneLighting) message += " Model review uses flat material shading: tick “Use scene lighting” in the review dock to see these settings.";
        float textH = _ui.TextWrapped(f.Body, 0, 0, iw, message, UiTheme.TEXT, maxLines: 5, draw: false);
        float h = pad + f.Small.LineHeight + S(8) + textH + S(10) + S(32) + pad;
        _ui.Rect(x, y, w, h, Rgba.WithAlpha(UiTheme.ACCENT, .1f));
        _ui.Rect(x, y, S(3), h, UiTheme.ACCENT);
        float ix = x + pad + S(4);
        y += pad;
        _ui.Text(f.Small, ix, y, CalcShowing ? "LIGHTING RESULTS SHOWN" : "MODEL REVIEW", UiTheme.ACCENT, S(1.4f));
        y += f.Small.LineHeight + S(8);
        y += _ui.TextWrapped(f.Body, ix, y, iw, message, UiTheme.TEXT, maxLines: 5) + S(10);
        float half = (iw - S(8)) * 0.5f;
        if (CalcShowing && (!_reviewOpen || _calcMode is not (CalcDisplayMode.Points or CalcDisplayMode.Off))
            && NativeButton(f, input, ix, y, half, S(32), _reviewOpen ? "Materials view" : "Hide results"))
            SetCalcMode(_reviewOpen ? CalcDisplayMode.Points : CalcDisplayMode.Off);
        if (NativeButton(f, input, ix + half + S(8), y, half, S(32), "Lighting tab"))
        { _rightTab = 3; _settingsScroll.Reset(); }
        return y + S(32) + pad - top + S(14);
    }
}

