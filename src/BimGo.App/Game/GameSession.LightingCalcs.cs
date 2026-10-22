using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Fork build LC-B (TomKal1/BimGo, not an upstream round): a lighting calc's results on the model, ElumTools style.
    /// <list type="bullet">
    /// <item>WW Tools' Lighting Calc writes each calc set's solved results (format "lighting-calc-results",
    /// <see cref="LightingCalcFiles"/>) into this model's folder (<c>lighting-calcs\</c>), and a .bimgo carries them as
    /// optional entries. These are the calc's own numbers, the same as its report and sheets: BimGo doesn't calculate.</item>
    /// <item>Views (Shift+J cycles; Pause → LIGHTS → CALCS): off (the default), illuminance (the surfaces in WW Tools' blue → red
    /// false colour on the sheets' scale), luminance (the surfaces shaded grey, ρE/π) and points only; the points, their
    /// values near the eye and the isolux lines on top. The legend sits in the left column with the stats of the zone you
    /// stand in (Avg / Max / Min / Avg:Min, Meets / Misses). Lux when BimGo's units are metric, else footcandles.</item>
    /// <item>Live sessions check the folder every 2 s: a re-run in WW Tools reloads the shown set with a toast.</item>
    /// <item>BimGo's own lights (IES-shaped since X-D1) are the realistic view: switch the calc off to see them.</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>Slider ids (unique across the panels' sliders).</summary>
        private const int SLIDER_CALC_OPACITY = 80, SLIDER_CALC_SCALE = 81;

        /// <summary>Folder check interval (s), value labels' reach (m) and most labels a frame.</summary>
        private const float CALC_POLL_SECONDS = 2f, CALC_LABEL_DISTANCE = 8f;
        private const int CALC_MAX_LABELS = 160;

        /// <summary>Phase C: how old WW Tools' "View in BimGo" request may be.</summary>
        private static readonly TimeSpan CALC_OPEN_MAX_AGE = TimeSpan.FromMinutes(2);

        private static readonly string[] CALC_MODE_OPTIONS = { "Off", "Illuminance", "Luminance", "Points" };

        private static readonly KeyHint HINT_CALC_CYCLE = new(() => $"{Key(InputAction.LightingCalcMode)}: off / illuminance / luminance / points");

        /// <summary>
        /// What the calc view was showing, kept for the app's lifetime so an F5 refresh (a new session) keeps it: the model
        /// folder, the set's file name and the view.
        /// </summary>
        private sealed record CalcMemory(string Folder, string Name, CalcDisplayMode Mode, bool Surfaces, bool Points, bool Values, bool Isolines, float Opacity, float ScaleTop);
        private static CalcMemory s_calcMemory;

        private readonly CalcOverlay _calcOverlay = new();
        private bool _calcOverlayReady;

        // Where the results come from: a live model folder, or the .bimgo's entries (file mode)
        private string _calcFolder;
        private Dictionary<string, byte[]> _calcEmbedded;
        private readonly List<string> _calcNames = new(); // newest first (folder) or by name (file)
        private readonly List<DateTime> _calcTimes = new();

        // The shown set and its view
        private LightingCalcResult _calc;
        private string _calcName; // the file name without .json
        private CalcDisplayMode _calcMode = CalcDisplayMode.Off;
        private bool _calcSurfaces = true, _calcPoints = true, _calcValues = true, _calcIsolines = true;
        private float _calcOpacity = 0.85f, _calcScaleTop = 1f;
        private int _calcBuildKey = int.MinValue;
        private string _calcNotice;

        // Loading on a worker thread (big buildings take a moment), and the folder check
        private Task<LightingCalcResult> _calcLoad;
        private string _calcLoadName, _calcLoadError;
        private CalcDisplayMode _calcLoadMode;
        private bool _calcLoadIsUpdate;
        private int _calcUpdateFailures;
        private float _calcNextPoll;
        private DateTime _calcFolderTime, _calcFileTime;
        private LightingCalcFiles.OpenRequest _calcOpenRequest;
        private bool _calcStartZonePending;

        // The zone you stand in (checked 4 times a second) and the legend's texts (made when they change)
        private CalcZoneResult _calcZone;
        private float _calcNextZoneCheck;
        private int _calcTextKey = int.MinValue;
        private string _calcLegendTitle, _calcZoneLine, _calcZoneVerdict;
        private readonly string[] _calcTicks = new string[5];

        #endregion

        #region Setup and saving

        /// <summary>
        /// Finds the results (the .bimgo's entries, else the model folder's lighting-calcs), restores the view an F5 kept, and
        /// takes WW Tools' "View in BimGo" request when there is a fresh one.
        /// </summary>
        private void InitialiseLightingCalcs()
        {
            if (IsFileMode)
            {
                _calcEmbedded = _options.Document?.LightingCalcs;
            }
            else
            {
                _calcFolder = LightingCalcFiles.FolderFor(Scene.CommentsPath);
                _calcOpenRequest = LightingCalcFiles.TakeOpenRequest(_calcFolder, CALC_OPEN_MAX_AGE, DateTime.UtcNow);
            }
            ListCalcs();
            if (_calcNames.Count > 0) { Utilities.Log_Utils.Write($"Lighting calcs: {_calcNames.Count} set(s) {(IsFileMode ? "in the file" : "in " + _calcFolder)}."); }

            if (_calcOpenRequest != null && OpenCalcRequest(_calcOpenRequest)) { return; }
            CalcMemory memory = s_calcMemory;
            if (memory != null && memory.Folder == (_calcFolder ?? DocumentPath) && _calcNames.Contains(memory.Name))
            {
                _calcSurfaces = memory.Surfaces;
                _calcPoints = memory.Points;
                _calcValues = memory.Values;
                _calcIsolines = memory.Isolines;
                _calcOpacity = memory.Opacity;
                _calcScaleTop = memory.ScaleTop;
                LoadCalc(memory.Name, memory.Mode, update: false);
            }
        }

        /// <summary>The results to embed in a .bimgo (Save / Save as): the file's own, or the model folder's (up to 100 MB).</summary>
        private Dictionary<string, byte[]> LightingCalcsForSave()
        {
            if (IsFileMode) { return _calcEmbedded; }
            Dictionary<string, byte[]> entries = LightingCalcFiles.ReadForEmbedding(_calcFolder, out string note);
            if (note != null) { Toast(note, 5f); }
            return entries;
        }

        /// <summary>Remembers the view for the next session (an F5 refresh).</summary>
        private void RememberCalcView()
        {
            s_calcMemory = _calcName == null ? null
                : new CalcMemory(_calcFolder ?? DocumentPath, _calcName, _calcMode, _calcSurfaces, _calcPoints, _calcValues, _calcIsolines, _calcOpacity, _calcScaleTop);
        }

        /// <summary>Lists the sets (folder: newest first; file: by name).</summary>
        private void ListCalcs()
        {
            _calcNames.Clear();
            _calcTimes.Clear();
            if (IsFileMode)
            {
                foreach (string name in LightingCalcFiles.EmbeddedNames(_calcEmbedded))
                {
                    _calcNames.Add(name);
                    _calcTimes.Add(DateTime.MinValue);
                }
                return;
            }
            foreach (LightingCalcFileInfo file in LightingCalcFiles.List(_calcFolder))
            {
                _calcNames.Add(file.Name);
                _calcTimes.Add(file.WrittenUtc);
            }
            try { _calcFolderTime = _calcFolder != null && Directory.Exists(_calcFolder) ? Directory.GetLastWriteTimeUtc(_calcFolder) : default; }
            catch { _calcFolderTime = default; }
        }

        #endregion

        #region Loading

        /// <summary>
        /// Starts loading a set on a worker thread; when it is read the view switches to it in <paramref name="mode"/> (an
        /// update keeps the current view; Off loads it without showing it: an F5 of a view that was off).
        /// </summary>
        private void LoadCalc(string name, CalcDisplayMode mode, bool update)
        {
            if (name == null || (_calcLoad != null && !_calcLoad.IsCompleted)) { return; }
            _calcLoadName = name;
            _calcLoadMode = mode;
            _calcLoadIsUpdate = update;
            _calcLoadError = null;
            Vector3 origin = Scene.OriginOffset;
            string path = _calcFolder == null ? null : Path.Combine(_calcFolder, name + ".json");
            Dictionary<string, byte[]> embedded = _calcEmbedded;
            _calcLoad = Task.Run(() =>
            {
                string error;
                LightingCalcResult result = path != null
                    ? LightingCalcFiles.Read(path, origin, out error)
                    : LightingCalcFiles.ParseEmbedded(embedded, name, origin, out error);
                _calcLoadError = error;
                return result;
            });
        }

        /// <summary>A load finished: show it (or say why not).</summary>
        private void FinishCalcLoad()
        {
            if (_calcLoad == null || !_calcLoad.IsCompleted) { return; }
            LightingCalcResult result = _calcLoad.IsCompletedSuccessfully ? _calcLoad.Result : null;
            Task<LightingCalcResult> load = _calcLoad;
            _calcLoad = null;
            if (result == null)
            {
                // Mid-write (WW Tools replaces the body, then the header): the next checks try again quietly, three times
                if (_calcLoadIsUpdate)
                {
                    if (++_calcUpdateFailures < 3) { _calcFileTime = default; }
                    else
                    {
                        _calcFileTime = CalcFileTime(_calcLoadName); // wait for the next change
                        _calcNotice = $"The re-run of “{_calcLoadName}” couldn't be read: {_calcLoadError ?? "unknown error"}";
                    }
                    return;
                }
                _calcNotice = $"“{_calcLoadName}” couldn't be read: {_calcLoadError ?? load.Exception?.GetBaseException().Message ?? "unknown error"}";
                Toast(_calcNotice, 5f, important: true);
                return;
            }

            bool update = _calcLoadIsUpdate && _calcName == _calcLoadName;
            _calcUpdateFailures = 0;
            if (!update) { _calcMode = _calcLoadMode; }
            _calc = result;
            _calcName = _calcLoadName;
            _calcBuildKey = int.MinValue;
            _calcTextKey = int.MinValue;
            _calcZone = null;
            _calcFileTime = result.FileTimeUtc;
            _calcNotice = result.Warnings.Count > 0 ? $"{result.Warnings.Count} zone(s) left out (damaged): {result.Warnings[0]}" : null;
            foreach (string warning in result.Warnings) { Utilities.Log_Utils.Write($"Lighting calc {_calcName}: {warning}"); }
            Utilities.Log_Utils.Write($"Lighting calc {_calcName}: {result.Zones.Length} zones, {result.PointCount:N0} points, {result.PatchCount:N0} patches ({result.Tool}).");
            if (update && _calcMode != CalcDisplayMode.Off) { Toast($"Lighting calc “{result.SetName}” updated", 3f); }
            else if (_calcMode != CalcDisplayMode.Off) { Toast($"Lighting calc: {result.SetName} · {ModeLabel(_calcMode)} · {Key(InputAction.LightingCalcMode)} cycles", 3f); }
            RememberCalcView();
            if (_calcStartZonePending)
            {
                _calcStartZonePending = false;
                StandInCalcZone(_calcOpenRequest?.Zone);
            }
        }

        /// <summary>
        /// Every 2 s in a live session: a changed folder lists again; the shown set reloads when its file changed (a re-run
        /// in WW Tools); a first set appearing says so. Cheap (two time stamps) and allocation-free when nothing changed.
        /// </summary>
        private void PollCalcFolder()
        {
            if (_calcFolder == null || _clock < _calcNextPoll) { return; }
            _calcNextPoll = _clock + CALC_POLL_SECONDS;
            try
            {
                DateTime folderTime = Directory.Exists(_calcFolder) ? Directory.GetLastWriteTimeUtc(_calcFolder) : default;
                if (folderTime != _calcFolderTime)
                {
                    int before = _calcNames.Count;
                    ListCalcs();
                    if (before == 0 && _calcNames.Count > 0 && _calc == null)
                    {
                        Toast($"Lighting calc results arrived: {Key(InputAction.LightingCalcMode)} shows them", 4f);
                    }
                }
                if (_calcName == null || (_calcLoad != null && !_calcLoad.IsCompleted)) { return; }
                string path = Path.Combine(_calcFolder, _calcName + ".json");
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != _calcFileTime) { LoadCalc(_calcName, _calcMode, update: true); }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Lighting calcs folder check failed: {ex.Message}");
                _calcNextPoll = _clock + 30f;
            }
        }

        /// <summary>The write time of a set's header in the folder (default when it can't be read).</summary>
        private DateTime CalcFileTime(string name)
        {
            try
            {
                string path = _calcFolder == null || name == null ? null : Path.Combine(_calcFolder, name + ".json");
                return path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// Phase C: WW Tools' "View in BimGo" asked for a set, a view and a zone to start in. True when the set is here (it
        /// then loads, and the player is stood in the zone once it has).
        /// </summary>
        private bool OpenCalcRequest(LightingCalcFiles.OpenRequest request)
        {
            int index = -1;
            for (int i = 0; i < _calcNames.Count && index < 0; i++)
            {
                if (_calcNames[i].EndsWith("_" + request.CalcSet, StringComparison.OrdinalIgnoreCase)) { index = i; }
            }
            if (index < 0)
            {
                Utilities.Log_Utils.Write($"Lighting calc request for set {request.CalcSet}: not in {_calcFolder}.");
                return false;
            }
            CalcDisplayMode mode = request.Mode switch
            {
                "luminance" => CalcDisplayMode.Luminance,
                "points" => CalcDisplayMode.Points,
                _ => CalcDisplayMode.Illuminance
            };
            _calcStartZonePending = !string.IsNullOrEmpty(request.Zone);
            LoadCalc(_calcNames[index], mode, update: false);
            return true;
        }

        /// <summary>Stands the player in a zone (by key, else the first zone): its Revit room when it is in the snapshot, else its middle.</summary>
        private void StandInCalcZone(string key)
        {
            if (_calc == null || _calc.Zones.Length == 0 || _player == null) { return; }
            CalcZoneResult zone = _calc.Zones.FirstOrDefault(z => z.Key == key) ?? _calc.Zones[0];
            for (int i = 0; i < Scene.Rooms.Length; i++)
            {
                RoomInfo room = Scene.Rooms[i];
                if (room.Number == zone.Number && room.Name == zone.Name && MathF.Abs(room.BottomZ - zone.BottomZ) < 0.5f && TeleportToRoom(i)) { return; }
            }
            Vector2 centre = (zone.PlanMin + zone.PlanMax) * 0.5f;
            if (_player.Flying) { _player.ToggleFly(); }
            _player.TeleportTo(FloorAt(centre, zone.BottomZ));
        }

        #endregion

        #region Keys

        /// <summary>
        /// Shift+J: off → illuminance → luminance → points → off. With nothing loaded it loads the newest set (or says how to
        /// make one).
        /// </summary>
        private void CycleCalcMode()
        {
            Sound.Play(SoundId.UiClick);
            if (_calc == null)
            {
                if (_calcLoad != null) { return; }
                if (_calcNames.Count == 0)
                {
                    Toast(IsFileMode
                        ? "This file has no lighting calc results (export from a model whose WW Tools Lighting Calc wrote them)"
                        : "No lighting calc results for this model yet: run a Lighting Calc in WW Tools (Write results for BimGo)", 5f);
                    return;
                }
                LoadCalc(_calcNames[0], CalcDisplayMode.Illuminance, update: false);
                return;
            }
            _calcMode = (CalcDisplayMode)(((int)_calcMode + 1) % 4);
            _calcBuildKey = int.MinValue;
            RememberCalcView();
            Toast(_calcMode == CalcDisplayMode.Off ? "Lighting calc off" : $"Lighting calc: {ModeLabel(_calcMode)}", 1.8f);
        }

        private static string ModeLabel(CalcDisplayMode mode) => mode switch
        {
            CalcDisplayMode.Illuminance => "illuminance",
            CalcDisplayMode.Luminance => "luminance",
            CalcDisplayMode.Points => "points",
            _ => "off"
        };

        /// <summary>True while the calc view shows something.</summary>
        private bool CalcShowing => _calc != null && _calcMode != CalcDisplayMode.Off;

        /// <summary>Show values in lux (metric units) or footcandles.</summary>
        private static bool CalcInLux => !UnitFormat.IsImperial;

        #endregion

        #region Drawing

        /// <summary>
        /// The surfaces, points and lines (in the 3D pass, with the study cells: results are content, so they show with the UI
        /// hidden, in photos and screenshots). The main view (not a photo or video frame) also runs the folder check and
        /// finishes loads. Allocation-free when nothing changed.
        /// </summary>
        private void DrawLightingCalcs(bool photo)
        {
            if (!photo)
            {
                FinishCalcLoad();
                PollCalcFolder();
            }
            if (!CalcShowing) { return; }
            if (!_calcOverlayReady)
            {
                _calcOverlay.Initialise();
                _calcOverlayReady = true;
            }

            float scaleMax = CalcScaleMax;
            var hash = new HashCode();
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_calc));
            hash.Add((int)_calcMode);
            hash.Add(_calcSurfaces);
            hash.Add(_calcPoints);
            hash.Add(_calcIsolines);
            hash.Add(scaleMax);
            int key = hash.ToHashCode();
            if (key != _calcBuildKey)
            {
                _calcBuildKey = key;
                _calcOverlay.Build(_calc, _calcMode, _calcSurfaces, _calcPoints, _calcIsolines && _calcMode != CalcDisplayMode.Luminance, _calc.ScaleMin, scaleMax);
            }
            _calcOverlay.Draw(Camera, _calcMode == CalcDisplayMode.Points ? 1f : _calcOpacity);
        }

        /// <summary>The top of the false colour scale (fc): the sheets' scale × the panel's scale slider.</summary>
        private float CalcScaleMax => _calc == null ? 1f : MathF.Max(_calc.ScaleMin + 0.01f, _calc.ScaleMin + (_calc.ScaleMax - _calc.ScaleMin) * _calcScaleTop);

        /// <summary>
        /// The values of the points near the eye (within 8 m, on screen and not behind the model), as 2D labels under the rest of
        /// the HUD: the nearer half of the reach first, and a label that would overlap one already drawn is skipped, so a fine
        /// grid seen at a glance stays readable. At most <see cref="CALC_MAX_LABELS"/> a frame; allocation-free.
        /// </summary>
        private void DrawCalcLabels(FontAtlas f)
        {
            if (!CalcShowing || !_calcValues || !_calcPoints || _uiHidden) { return; }
            int drawn = 0;
            float near = CALC_LABEL_DISTANCE * 0.5f;
            drawn = DrawCalcLabelBand(f, 0f, near, drawn);
            DrawCalcLabelBand(f, near, CALC_LABEL_DISTANCE, drawn);
        }

        // Where this frame's labels went (screen), for the overlap test
        private readonly Vector2[] _calcLabelSpots = new Vector2[CALC_MAX_LABELS];

        /// <summary>One distance band of <see cref="DrawCalcLabels"/>; returns the labels drawn so far.</summary>
        private int DrawCalcLabelBand(FontAtlas f, float from, float to, int drawn)
        {
            Vector3 eye = Camera.Position;
            float from2 = from * from, to2 = to * to;
            float apartX = S(34), apartY = S(15);
            int width = _window.Width, height = _window.Height;
            bool lux = CalcInLux;
            uint shadow = Rgba.Hex(0x0C0E12, 0.85f);
            foreach (CalcZoneResult zone in _calc.Zones)
            {
                // Zones whose plan box is out of reach are skipped whole
                float dx = MathF.Max(0f, MathF.Max(zone.PlanMin.X - eye.X, eye.X - zone.PlanMax.X));
                float dy = MathF.Max(0f, MathF.Max(zone.PlanMin.Y - eye.Y, eye.Y - zone.PlanMax.Y));
                if (dx * dx + dy * dy > to2) { continue; }
                for (int i = 0; i < zone.Points.Length && drawn < CALC_MAX_LABELS; i++)
                {
                    float value = zone.PointValues[i];
                    if (float.IsNaN(value)) { continue; }
                    Vector3 p = zone.Points[i];
                    Vector3 offset = p - eye;
                    float d2 = offset.LengthSquared();
                    if (d2 > to2 || d2 <= from2 || d2 < 0.04f) { continue; }
                    if (!Camera.WorldToScreen(p, out Vector2 screen)) { continue; }
                    if (screen.X < 0f || screen.Y < 0f || screen.X > width || screen.Y > height) { continue; }
                    float y = screen.Y - S(18);
                    bool crowded = false;
                    for (int k = 0; k < drawn && !crowded; k++)
                    {
                        crowded = MathF.Abs(_calcLabelSpots[k].X - screen.X) < apartX && MathF.Abs(_calcLabelSpots[k].Y - y) < apartY;
                    }
                    if (crowded) { continue; }
                    float d = MathF.Sqrt(d2);
                    if (Pick(eye, offset / d, d - 0.08f, out _)) { continue; } // behind a wall, a desk…
                    Text.Clear().Append(LightingCalcs.Display(value, lux), lux ? 0 : 1);
                    _ui.TextCentred(f.Small, screen.X + S(1), y + S(1), Text.Span, shadow);
                    _ui.TextCentred(f.Small, screen.X, y, Text.Span, UiTheme.TEXT);
                    _calcLabelSpots[drawn++] = new Vector2(screen.X, y);
                }
            }
            return drawn;
        }

        /// <summary>
        /// The legend (left column, above the sun legend): the set, the view and its unit, the colour bar with ticks, and the
        /// stats of the zone you stand in. Returns the new bottom of the column's free space.
        /// </summary>
        private float BuildCalcLegend(FontAtlas f, float x, float top, float bottom, bool collapsed)
        {
            if (!CalcShowing) { return bottom; }
            UpdateCalcTexts();
            float w = S(380), full = S(_calcZoneLine != null ? 98 : 78), small = S(UiTheme.LEGEND_COLLAPSED_HEIGHT);
            bool open = !collapsed && bottom - top >= full;
            float h = open ? full : small;
            if (bottom - h < top) { return bottom; }
            float y = bottom - h;
            DrawCalcLegendAt(f, x, y, w, h, open);
            return y - S(12);
        }

        /// <summary>The legend card at a place (the HUD column, or a screenshot's corner).</summary>
        private void DrawCalcLegendAt(FontAtlas f, float x, float y, float w, float h, bool open)
        {
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _ui.Text(f.Small, x + S(12), open ? y + S(8) : y + MathF.Round((h - f.Small.LineHeight) * 0.5f), _calcLegendTitle, UiTheme.ACCENT, S(0.5f));
            if (!open) { return; }

            float barX = x + S(12), barY = y + S(30), barW = w - S(24), barH = S(14);
            const int STEPS = 56;
            bool luminance = _calcMode == CalcDisplayMode.Luminance;
            for (int i = 0; i < STEPS; i++)
            {
                float t = (i + 0.5f) / STEPS;
                Vector3 c = luminance
                    ? LightingCalcs.LuminanceGrey(t * 2f * _calc.LuminanceReference, _calc.LuminanceReference)
                    : LightingCalcs.FalseColour(t, 0f, 1f);
                _ui.Rect(barX + barW * i / STEPS, barY, barW / STEPS + 0.5f, barH, Rgba.FromFloat(c.X, c.Y, c.Z, 1f));
            }
            for (int t = 0; t < _calcTicks.Length; t++)
            {
                float tx = barX + barW * t / (_calcTicks.Length - 1);
                _ui.Rect(tx - S(0.5f), barY + barH, S(1), S(4), UiTheme.TEXT_SOFT);
                _ui.TextCentred(f.Small, tx, barY + barH + S(6), _calcTicks[t], UiTheme.TEXT_SOFT);
            }
            if (_calcZoneLine != null)
            {
                float zy = barY + barH + S(26);
                float verdictW = _calcZoneVerdict == null ? 0f : UiBatch.Measure(f.Small, _calcZoneVerdict) + S(8);
                _ui.TextWrapped(f.Small, barX, zy, barW - verdictW, _calcZoneLine, UiTheme.TEXT, maxLines: 1);
                if (_calcZoneVerdict != null)
                {
                    _ui.TextRight(f.Small, barX + barW, zy, _calcZoneVerdict, _calcZoneVerdict == "MEETS" ? UiTheme.GOOD : UiTheme.DANGER);
                }
            }
        }

        /// <summary>The legend's title, ticks and the zone line: made again only when the set, view, units, scale or zone change.</summary>
        private void UpdateCalcTexts()
        {
            if (_clock >= _calcNextZoneCheck)
            {
                _calcNextZoneCheck = _clock + 0.25f;
                _calcZone = LightingCalcs.ZoneAt(_calc, _player.Feet + new Vector3(0f, 0f, 0.3f));
            }
            var hash = new HashCode();
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_calc));
            hash.Add((int)_calcMode);
            hash.Add(UnitFormat.Revision);
            hash.Add(_calcScaleTop);
            hash.Add(_calcZone == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_calcZone));
            int key = hash.ToHashCode();
            if (key == _calcTextKey) { return; }
            _calcTextKey = key;

            bool lux = CalcInLux;
            string unit = lux ? "lx" : "fc";
            bool luminance = _calcMode == CalcDisplayMode.Luminance;
            _calcLegendTitle = $"LIGHTING CALC · {_calc.SetName.ToUpperInvariant()} · " + (luminance ? "LUMINANCE [CD/M²]" : $"ILLUMINANCE [{unit.ToUpperInvariant()}]");
            for (int t = 0; t < _calcTicks.Length; t++)
            {
                float f = t / (float)(_calcTicks.Length - 1);
                if (luminance)
                {
                    float l = f * 2f * _calc.LuminanceReference;
                    _calcTicks[t] = l.ToString(l < 10f ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    float v = LightingCalcs.Display(_calc.ScaleMin + (CalcScaleMax - _calc.ScaleMin) * f, lux);
                    _calcTicks[t] = v.ToString(lux || v >= 100f ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + (t == _calcTicks.Length - 1 ? "+" : string.Empty);
                }
            }

            _calcZoneLine = null;
            _calcZoneVerdict = null;
            if (_calcZone != null && _calcZone.Stats.Count > 0)
            {
                CalcStats s = _calcZone.Stats;
                string V(float fc) => LightingCalcs.Display(fc, lux).ToString(lux ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
                string ratio = float.IsNaN(s.AverageToMinimum) ? "N/A" : s.AverageToMinimum.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ":1";
                _calcZoneLine = $"{_calcZone.DisplayName} · Avg {V(s.Average)} · Max {V(s.Maximum)} · Min {V(s.Minimum)} {unit} · Avg:Min {ratio}";
                _calcZoneVerdict = _calcZone.Target?.Result?.ToUpperInvariant();
            }
        }

        /// <summary>F12 with the calc view on: its legend goes in the screenshot (bottom left).</summary>
        private void QueueCalcLegendForShot(int height)
        {
            if (!CalcShowing) { return; }
            UpdateCalcTexts();
            float h = S(_calcZoneLine != null ? 98 : 78);
            DrawCalcLegendAt(_ui.Atlas, S(20), height - S(20) - h, S(380), h, open: true);
        }

        #endregion

        #region CALCS section (Pause → LIGHTS)

        /// <summary>
        /// Pause → LIGHTS, first: the calc sets (name, run time, zones), the view, what to draw, opacity, the scale, IMPORT…
        /// </summary>
        /// <returns>The bottom of the section (the fixture types follow).</returns>
        private float BuildLightingCalcsSection(FontAtlas f, InputState input, float ix, float y, float iw)
        {
            if (!input.LeftDown && (_activeSlider == SLIDER_CALC_OPACITY || _activeSlider == SLIDER_CALC_SCALE)) { _activeSlider = -1; }
            _ui.Text(f.Small, ix, y, "LIGHTING CALCS", UiTheme.ACCENT, S(0.5f));
            y += S(20);
            _ui.Text(f.Small, ix, y, HINT_CALC_CYCLE.Text, UiTheme.TEXT_FAINT); // its own line: beside the title it runs into it
            y += S(22);

            if (_calcNames.Count == 0)
            {
                y += _ui.TextWrapped(f.Small, ix, y, iw, IsFileMode
                    ? "This file has no lighting calc results. Export it again from a model whose WW Tools Lighting Calc wrote them, or IMPORT… a results file."
                    : "No results for this model yet. In WW Tools, run a Lighting Calc with “Write results for BimGo” ticked: it shows up here within seconds. Or IMPORT… a results file (… - Results.json beside the report).",
                    UiTheme.TEXT_FAINT, maxLines: 5) + S(6);
                if (SmallButton(f, input, ix, y, S(110), S(26), "IMPORT…")) { ImportCalc(); }
                y += S(34);
                if (_calcNotice != null) { y += _ui.TextWrapped(f.Small, ix, y, iw, _calcNotice, UiTheme.MEASURE_LABEL, maxLines: 3) + S(6); }
                return y + S(14);
            }

            // The sets: one row each (the shown one lit)
            for (int i = 0; i < _calcNames.Count; i++)
            {
                string name = _calcNames[i];
                bool shown = name == _calcName && _calc != null;
                bool loading = _calcLoad != null && _calcLoadName == name;
                bool hover = Hover(input, ix, y - S(2), iw, S(22));
                if (shown || hover) { _ui.Rect(ix - S(4), y - S(3), iw + S(8), S(24), shown ? Rgba.WithAlpha(UiTheme.ACCENT, 0.16f) : UiTheme.HOVER); }
                string label = shown ? _calc.SetName : DisplayCalcName(name);
                _ui.TextWrapped(f.Body, ix, y, iw - S(150), label, shown ? UiTheme.ACCENT : UiTheme.TEXT, maxLines: 1);
                Text.Clear();
                if (loading) { Text.Append("loading…"); }
                else if (shown)
                {
                    Text.AppendGrouped(_calc.Zones.Length).Append(_calc.Zones.Length == 1 ? " zone" : " zones");
                    if (_calc.RunUtc > DateTime.MinValue) { Text.Append(" · ").Append(_calc.RunUtc.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture)); }
                }
                else if (_calcTimes[i] > DateTime.MinValue) { Text.Append(_calcTimes[i].ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture)); }
                _ui.TextRight(f.Small, ix + iw, y + S(2), Text.Span, UiTheme.TEXT_MUTED);
                if (hover && input.LeftPressed && !shown && !loading)
                {
                    Sound.Play(SoundId.UiClick);
                    LoadCalc(name, _calcMode == CalcDisplayMode.Off ? CalcDisplayMode.Illuminance : _calcMode, update: false);
                }
                y += S(24);
            }
            y += S(6);

            // The view
            int mode = Segmented(f, input, ix, y, iw, CALC_MODE_OPTIONS, (int)_calcMode);
            if (mode != (int)_calcMode)
            {
                if (_calc == null && _calcNames.Count > 0) { LoadCalc(_calcNames[0], (CalcDisplayMode)mode, update: false); }
                else { _calcMode = (CalcDisplayMode)mode; }
                _calcBuildKey = int.MinValue;
                RememberCalcView();
            }
            y += S(42);

            // What to draw (two columns)
            float half = (iw - S(12)) * 0.5f;
            bool surfaces = Checkbox(f, input, ix, y, half, "Surfaces", _calcSurfaces);
            bool points = Checkbox(f, input, ix + half + S(12), y, half, "Points", _calcPoints);
            y += S(26);
            bool values = Checkbox(f, input, ix, y, half, "Values near you", _calcValues);
            bool isolines = Checkbox(f, input, ix + half + S(12), y, half, "Isolux lines", _calcIsolines);
            y += S(30);
            if (surfaces != _calcSurfaces || points != _calcPoints || values != _calcValues || isolines != _calcIsolines)
            {
                _calcSurfaces = surfaces;
                _calcPoints = points;
                _calcValues = values;
                _calcIsolines = isolines;
                RememberCalcView();
            }

            // Opacity and the scale's top (× the sheets' scale)
            Text.Clear().Append(_calcOpacity * 100f, 0).Append(" %");
            float opacity = Slider(f, input, SLIDER_CALC_OPACITY, ix, y, half, "Opacity", Text.Span, _calcOpacity, 0.3f, 1f);
            Text.Clear();
            if (_calc != null) { Text.Append(LightingCalcs.Display(CalcScaleMax, CalcInLux), CalcInLux ? 0 : 1).Append(CalcInLux ? " lx" : " fc"); }
            else { Text.Append(_calcScaleTop * 100f, 0).Append(" %"); }
            float scale = Slider(f, input, SLIDER_CALC_SCALE, ix + half + S(12), y, half, "Scale top", Text.Span, _calcScaleTop, 0.25f, 2f);
            y += S(48);
            if (MathF.Abs(opacity - _calcOpacity) > 1e-4f || MathF.Abs(scale - _calcScaleTop) > 1e-4f)
            {
                _calcOpacity = opacity;
                _calcScaleTop = MathF.Abs(scale - 1f) < 0.04f ? 1f : scale; // snaps to the sheets' scale
                RememberCalcView();
            }

            if (SmallButton(f, input, ix, y, S(110), S(26), "IMPORT…")) { ImportCalc(); }
            if (_calc != null && _calcZone == null && SmallButton(f, input, ix + S(118), y, S(150), S(26), "GO TO A ZONE"))
            {
                SetPaused(false);
                StandInCalcZone(null);
            }
            y += S(34);

            string about = _calc == null ? null
                : $"{_calc.Tool} · {(_calc.Interreflections ? "with interreflections" : "direct light only")} · " +
                  $"{_calc.PointCount:N0} points, {_calc.PatchCount:N0} surface patches · the scale is the sheets' ({LightingCalcs.Display(_calc.ScaleMax, CalcInLux):0.#} {(CalcInLux ? "lx" : "fc")} at 100 %)";
            if (about != null) { y += _ui.TextWrapped(f.Small, ix, y, iw, about, UiTheme.TEXT_FAINT, maxLines: 3) + S(4); }
            if (_calcNotice != null) { y += _ui.TextWrapped(f.Small, ix, y, iw, _calcNotice, UiTheme.MEASURE_LABEL, maxLines: 3) + S(4); }
            y += _ui.TextWrapped(f.Small, ix, y, iw,
                "These are the calc's own results (the same as its report and sheets). Switch the calc off to see BimGo's lights, shaped by each fixture's IES file.",
                UiTheme.TEXT_FAINT, maxLines: 3);

            // A divider before the fixture types
            y += S(12);
            _ui.Rect(ix, y, iw, MathF.Max(1f, UiScale), UiTheme.DIVIDER);
            return y + S(14);
        }

        /// <summary>"Level 1 Offices" from "Level 1 Offices_d339c539" (the set id dropped).</summary>
        private static string DisplayCalcName(string name)
        {
            int cut = name.LastIndexOf('_');
            return cut > 0 && name.Length - cut == 9 ? name[..cut] : name;
        }

        /// <summary>
        /// IMPORT…: a results file picked by hand (its .bin beside it). Live: copied into the model folder, so it stays. File:
        /// added to the file's results (Ctrl+S keeps it).
        /// </summary>
        private void ImportCalc()
        {
            _window.Input.ReleaseAll();
            string path = FileDialogs.ShowOpen(_window.Handle, "Lighting calc results (WW Tools: … - Results.json beside the report)",
                "Lighting calc results (*.json)|*.json|All files (*.*)|*.*", null);
            _window.Input.ReleaseAll();
            if (path == null) { return; }
            string bin = Path.ChangeExtension(path, ".bin");
            LightingCalcResult check = LightingCalcFiles.Read(path, Scene.OriginOffset, out string error);
            if (check == null)
            {
                _calcNotice = $"{Path.GetFileName(path)}: {error}";
                Sound.Play(SoundId.Error);
                return;
            }
            string name = string.IsNullOrEmpty(check.SetId) ? Path.GetFileNameWithoutExtension(path) : DisplaySafe(check.SetName) + "_" + check.SetId;
            try
            {
                if (IsFileMode)
                {
                    _calcEmbedded ??= new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    _calcEmbedded[LightingCalcFiles.ENTRY_PREFIX + name + ".json"] = File.ReadAllBytes(path);
                    _calcEmbedded[LightingCalcFiles.ENTRY_PREFIX + name + ".bin"] = File.ReadAllBytes(bin);
                    _calcNotice = "Imported into this file: Ctrl+S keeps it.";
                }
                else
                {
                    if (_calcFolder == null) { throw new InvalidOperationException("this session has no model folder"); }
                    Directory.CreateDirectory(_calcFolder);
                    File.Copy(bin, Path.Combine(_calcFolder, name + ".bin"), overwrite: true);
                    File.Copy(path, Path.Combine(_calcFolder, name + ".json"), overwrite: true);
                    _calcNotice = null;
                }
            }
            catch (Exception ex)
            {
                _calcNotice = $"Not imported: {ex.Message}";
                Sound.Play(SoundId.Error);
                return;
            }
            ListCalcs();
            LoadCalc(name, _calcMode == CalcDisplayMode.Off ? CalcDisplayMode.Illuminance : _calcMode, update: false);
        }

        /// <summary>A set name made safe for a file name (as WW Tools makes it).</summary>
        private static string DisplaySafe(string name)
        {
            var chars = (name ?? string.Empty).Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if ("\"<>|:*?\\/".IndexOf(chars[i]) >= 0 || char.IsControl(chars[i])) { chars[i] = '_'; }
            }
            string safe = new string(chars).Trim(' ', '.');
            if (safe.Length > 60) { safe = safe[..60].TrimEnd(' ', '.'); }
            return safe.Length == 0 ? "Lighting Calc" : safe;
        }

        #endregion
    }
}
