using System.Numerics;
using BimGo.Audio;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

namespace BimGo.Game
{
    /// <summary>Model review: parallel views, room cutaways, free flight and annotated PNGs, with optional lighting results.</summary>
    internal sealed partial class GameSession
    {
        private static readonly string[] REVIEW_VIEWS = { "ISO", "PLAN", "RCP", "FLY" };
        private static readonly string[] REVIEW_RESULTS = { "Materials", "Illuminance", "Luminance", "Calculated" };
        private static readonly string[] REVIEW_MIN_LABELS = { "West / X min", "South / Y min", "Bottom" };
        private static readonly string[] REVIEW_MAX_LABELS = { "East / X max", "North / Y max", "Top" };
        private const int DROP_REVIEW_LEVEL = 0;
        private static readonly KeyHint HINT_REVIEW_NAV = new(() =>
            $"Left drag: pan · {Key(InputAction.ReviewOrbit)} drag: orbit / fly look · {Key(InputAction.DragLook)} drag: orbit / look · wheel: zoom · click and release: select room");
        private static readonly KeyHint HINT_REVIEW_FLY = new(() =>
            $"Fly without collision · {Key(InputAction.ClashFlyUp)} up · {Key(InputAction.ClashFlyDown)} down · {Key(InputAction.Run)} faster");

        private sealed class ReviewRoom
        {
            public string Title, Detail, Search;
            public Aabb Bounds;
            public CalcZoneResult Zone;
            public int Room = -1;
        }

        private bool _reviewOpen, _reviewEditBox, _reviewPin, _reviewShowGrids, _reviewShowCaps, _reviewSceneLighting;
        private int _reviewView, _reviewLevel = -1;
        private float _reviewYaw = -MathF.PI * 0.25f, _reviewPitch = ReviewFraming.IsoPitch, _reviewHeight = 20f, _reviewDistance;
        private Vector3 _reviewTarget, _reviewEye;
        private Aabb _reviewBounds;
        private SectionCut _reviewPreviousSection;
        private ReviewRoom _reviewRoom;
        private LightingCalcResult _reviewListedCalc;
        private readonly List<ReviewRoom> _reviewRooms = new();
        private readonly List<ReviewRoom> _reviewMatches = new();
        private readonly ScrollRegion _reviewScroll = new();
        private Vector4 _reviewPanelRect;
        private string _reviewSearch = string.Empty, _reviewScope = "Building", _reviewNotice;

        /// <summary>Material colours under the classic directional light, independent of the model's light output.</summary>
        private bool ReviewMaterialShading => _reviewOpen && !_reviewSceneLighting;

        /// <summary>The walkthrough's ISO button sits beside the sun control.</summary>
        private Vector4 ReviewShortcutRect
        {
            get
            {
                SunIconRect(out float x, out float y, out float size);
                return new Vector4(x - S(116), y, S(108), size);
            }
        }

        private bool ReviewShortcutVisible => !_uiHidden && !AnyDockOpen && !IsEditingComment && !_wheelOpen;

        private bool HoverReviewShortcut(InputState input)
        {
            Vector4 r = ReviewShortcutRect;
            return ReviewShortcutVisible && Hover(input, r.X, r.Y, r.Z, r.W);
        }

        private void BuildReviewShortcut(FontAtlas f, InputState input)
        {
            if (!ReviewShortcutVisible) { return; }
            Vector4 r = ReviewShortcutRect;
            bool hover = !_window.IsCaptured && HoverReviewShortcut(input);
            _ui.Panel(r.X, r.Y, r.Z, r.W, UiTheme.PANEL, hover ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
            _ui.TextCentred(f.Bold, r.X + r.Z * 0.5f, r.Y + (r.W - f.Bold.LineHeight) * 0.5f, "ISO VIEW", hover ? UiTheme.ACCENT : UiTheme.TEXT);
        }

        /// <summary>Opens in ISO on the current room or whole building, retaining the walkthrough position and cut for closing.</summary>
        private void OpenLightingReview()
        {
            if (_reviewOpen) { return; }
            if (_pathEditOn) { ExitPathEditor(); }
            if (_clashModeOn) { ExitClashMode(); }
            CloseSectionEditor();
            ClosePhotoMode();
            CloseSunPanel();
            CloseSunHours();
            CloseGridsPanel();
            CloseFixtureDock();
            _reviewPreviousSection = _section.Clone();
            _reviewOpen = true;
            _reviewView = 0;
            _reviewEditBox = _reviewPin = false;
            _reviewShowGrids = _reviewCubeDrag = false;
            _reviewShowCaps = false;
            _reviewSceneLighting = false;
            ResetReviewPointer();
            _reviewLevel = -1;
            _reviewYaw = -MathF.PI * 0.25f;
            _reviewPitch = ReviewFraming.IsoPitch;
            _reviewRoom = null;
            _reviewSearch = string.Empty;
            _reviewScroll.Reset();
            RefreshReviewRooms();
            Vector3 point = _player.Feet + Vector3.UnitZ * 0.3f;
            int currentRoom = FindRoom(point);
            ReviewRoom initialRoom = currentRoom >= 0 ? _reviewRooms.Find(room => room.Room == currentRoom)
                : _reviewRooms.Find(room => room.Zone != null && room.Zone.Contains(point));
            if (initialRoom != null) { ReviewSelectRoom(initialRoom); }
            else { ReviewBuilding(); }
            SetPaused(false);
            ShowUi();
            Sound.Play(SoundId.UiClick);
            if (_calc == null && _calcNames.Count > 0) { LoadCalc(_calcNames[0], CalcDisplayMode.Off, update: false); }
        }

        /// <summary>Returns to the walkthrough; review cuts are temporary, revisions remain saved.</summary>
        private void CloseLightingReview()
        {
            if (!_reviewOpen) { return; }
            RememberCalcView();
            ApplySection(_reviewPreviousSection, announce: false);
            _reviewOpen = false;
            _reviewPin = false;
            _reviewCubeDrag = false;
            ResetReviewPointer();
            _sectionDrag = _sectionHover = -1;
            _activeSlider = -1;
            Camera.OrthographicHeight = 0f;
            Camera.ClearCustomView();
            _window.Input.ReleaseAll();
            if (!_paused && _window.IsActive) { _window.SetCaptured(true); }
        }

        /// <summary>Rebuilds cached room rows only when opening or a calc set is replaced.</summary>
        private void RefreshReviewRooms()
        {
            string selectedKey = _reviewRoom?.Zone?.Key;
            int selectedRoom = _reviewRoom?.Room ?? -1;
            _reviewListedCalc = _calc;
            _reviewRooms.Clear();
            if (_calc != null)
            {
                foreach (CalcZoneResult zone in _calc.Zones)
                {
                    _reviewRooms.Add(new ReviewRoom
                    {
                        Title = zone.DisplayName, Detail = zone.Level + " · calc results",
                        Search = zone.DisplayName + " " + zone.Level,
                        Bounds = new Aabb(new Vector3(zone.PlanMin, zone.BottomZ),
                            new Vector3(zone.PlanMax, MathF.Max(zone.BottomZ + 0.5f, zone.CeilingZ > zone.BottomZ ? zone.CeilingZ : zone.TopZ))),
                        Zone = zone
                    });
                }
            }
            for (int i = 0; i < Scene.Rooms.Length; i++)
            {
                RoomInfo room = Scene.Rooms[i];
                ReviewRoom existing = null;
                foreach (ReviewRoom row in _reviewRooms)
                {
                    if (row.Zone != null && row.Zone.Number == room.Number && row.Zone.Name == room.Name
                        && MathF.Abs(row.Zone.BottomZ - room.BottomZ) < 0.5f
                        && room.Contains((row.Zone.PlanMin + row.Zone.PlanMax) * 0.5f)) { existing = row; break; }
                }
                if (existing != null) { existing.Room = i; continue; }
                string title = RoomLabel(i), level = LevelNameAt(room.BottomZ);
                _reviewRooms.Add(new ReviewRoom
                {
                    Title = title, Detail = _calc != null ? level + " · no calc results" : level, Search = title + " " + level, Room = i,
                    Bounds = new Aabb(new Vector3(room.Min, room.BottomZ), new Vector3(room.Max, MathF.Max(room.BottomZ + 0.5f, room.TopZ)))
                });
            }
            _reviewRooms.Sort((a, b) =>
            {
                int level = a.Bounds.Min.Z.CompareTo(b.Bounds.Min.Z);
                return level == 0 ? string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase) : level;
            });
            _reviewRoom = null;
            foreach (ReviewRoom row in _reviewRooms)
            {
                if ((selectedKey != null && row.Zone?.Key == selectedKey) || (selectedRoom >= 0 && row.Room == selectedRoom)) { _reviewRoom = row; break; }
            }
            FilterReviewRooms();
        }

        private void FilterReviewRooms()
        {
            _reviewMatches.Clear();
            foreach (ReviewRoom row in _reviewRooms)
            {
                if (row.Search.Contains(_reviewSearch, StringComparison.CurrentCultureIgnoreCase)) { _reviewMatches.Add(row); }
            }
        }

        /// <summary>Building footprint, optionally limited to the chosen storey.</summary>
        private void ReviewBuilding()
        {
            _reviewRoom = null;
            _reviewBounds = Scene.Bounds;
            _reviewScope = "Whole building";
            if (_reviewLevel >= 0 && _reviewLevel < Scene.Levels.Length)
            {
                _reviewBounds.Min.Z = Scene.Levels[_reviewLevel].Elevation;
                _reviewBounds.Max.Z = MathF.Max(_reviewBounds.Min.Z + 0.5f,
                    _reviewLevel + 1 < Scene.Levels.Length ? Scene.Levels[_reviewLevel + 1].Elevation : Scene.Bounds.Max.Z);
                _reviewScope = Scene.Levels[_reviewLevel].Name;
            }
            ReviewFitCut();
            ReviewFitCamera();
        }

        /// <summary>Steps through storeys; from all levels, starts at the bottom going up or the top going down.</summary>
        private void ReviewStepLevel(int direction)
        {
            if (Scene.Levels.Length == 0) { return; }
            int level = _reviewLevel < 0 ? (direction > 0 ? 0 : Scene.Levels.Length - 1)
                : Math.Clamp(_reviewLevel + direction, 0, Scene.Levels.Length - 1);
            if (level == _reviewLevel && _reviewRoom == null) { return; }
            ResetReviewPointer();
            _reviewLevel = level;
            ReviewBuilding();
        }

        private void ReviewSelectRoom(ReviewRoom room)
        {
            ResetReviewPointer();
            _reviewRoom = room;
            _reviewLevel = Scene.Levels.Length > 0 ? LevelIndexAt(room.Bounds.Min.Z) : -1;
            _reviewBounds = room.Bounds;
            _reviewScope = room.Title;
            _reviewPin = false;
            ReviewFitCut();
            if (_reviewView == 3)
            {
                _reviewEye = ReviewRoomEye(room);
                _reviewPitch = 0f;
            }
            else { ReviewFitCamera(); }
            _calcTextKey = int.MinValue;
        }

        /// <summary>Finds an interior eye point without moving or changing the walking player.</summary>
        private Vector3 ReviewRoomEye(ReviewRoom room)
        {
            Vector2 centre = new(room.Bounds.Center.X, room.Bounds.Center.Y);
            Vector3 best = new(centre, room.Bounds.Min.Z + _player.Controller.StandEye);
            float nearest = float.MaxValue;
            for (int y = 0; y <= 20; y++)
            for (int x = 0; x <= 20; x++)
            {
                Vector2 p = new(room.Bounds.Min.X + room.Bounds.Size.X * (x + 0.5f) / 21f,
                    room.Bounds.Min.Y + room.Bounds.Size.Y * (y + 0.5f) / 21f);
                float distance = Vector2.DistanceSquared(p, centre);
                if (distance >= nearest) { continue; }
                bool inside = room.Zone != null ? LightingCalcs.InsideLoops(room.Zone.Loops, p.X, p.Y) : Scene.Rooms[room.Room].Contains(p);
                if (!inside) { continue; }
                Vector3 feet = FloorAt(p, room.Bounds.Min.Z);
                if (feet.Z + _player.Controller.StandEye >= room.Bounds.Max.Z
                    || _player.Controller.Overlaps(feet + new Vector3(0, 0, 0.01f), BimGo.Physics.CharacterController.STAND_HEIGHT)) { continue; }
                nearest = distance;
                best = feet + Vector3.UnitZ * _player.Controller.StandEye;
            }
            if (nearest == float.MaxValue) { Toast("No clear interior viewpoint found; use the fly controls to move into this room.", 4f); }
            return best;
        }

        /// <summary>Open ceilings or floors for room / storey views; all levels keeps the whole building.</summary>
        private void ReviewFitCut()
        {
            Vector3 min = _reviewBounds.Min - new Vector3(0.25f), max = _reviewBounds.Max + new Vector3(0.25f);
            if (_reviewRoom != null || _reviewLevel >= 0)
            {
                if (_reviewView == 2) { min.Z = _reviewBounds.Min.Z + 0.15f; }
                else if (_reviewView != 3) { max.Z = MathF.Max(min.Z + SectionCut.MIN_SIZE, _reviewBounds.Max.Z - (_reviewRoom != null ? 0.03f : 0.4f)); }
            }
            _section.PlaneOn = false;
            SetBoxLocal(min, max);
            _sectionDrag = _sectionHover = -1;
        }

        private Vector3 ReviewForward => new(MathF.Cos(_reviewPitch) * MathF.Cos(_reviewYaw), MathF.Cos(_reviewPitch) * MathF.Sin(_reviewYaw), MathF.Sin(_reviewPitch));

        private void ReviewFitCamera()
        {
            _reviewTarget = _reviewBounds.Center;
            Vector3 forward = _reviewView == 1 ? -Vector3.UnitZ : _reviewView == 2 ? Vector3.UnitZ : ReviewForward;
            Vector3 up = _reviewView is 1 or 2 ? Vector3.UnitY : ReviewNavigation.Up(forward);
            // Keep the model to the left of the dock; export uses the same composition.
            float usable = MathF.Max(_window.Width * 0.25f, _window.Width - S(UiTheme.DOCK_WIDTH + UiTheme.COLUMN_MARGIN * 2));
            _reviewHeight = ReviewFraming.Height(_reviewBounds, forward, up, usable / Math.Max(1, _window.Height - S(160)));
            _reviewDistance = MathF.Max(5f, _reviewBounds.Size.Length() + 2f);
            if (_reviewView == 3) { _reviewEye = _reviewTarget - ReviewForward * _reviewDistance; }
            UpdateReviewCamera();
        }

        private void ReviewChangeView(int view)
        {
            if (view == _reviewView) { return; }
            ResetReviewPointer();
            if (view == 3) { _reviewEye = Camera.Position; }
            _reviewView = view;
            _reviewPin = false;
            _reviewEditBox = false;
            if (view == 0) { _reviewPitch = ReviewFraming.IsoPitch; }
            ReviewFitCut();
            if (view == 3)
            {
                if (_reviewRoom != null) { ReviewSelectRoom(_reviewRoom); }
                else { _reviewPitch = -0.15f; }
            }
            else { ReviewFitCamera(); }
        }

        /// <summary>Writes the review pose after the normal camera update; the walking player remains untouched.</summary>
        private void UpdateReviewCamera()
        {
            Camera.Yaw = _reviewYaw;
            Camera.Pitch = _reviewPitch;
            Camera.OrthographicHeight = _reviewView == 3 ? 0f : _reviewHeight;
            if (_reviewView == 3)
            {
                Camera.ClearCustomView();
                Camera.Position = _reviewEye;
            }
            else
            {
                Vector3 forward = _reviewView == 1 ? -Vector3.UnitZ : _reviewView == 2 ? Vector3.UnitZ : ReviewForward;
                Vector3 up = _reviewView is 1 or 2 ? Vector3.UnitY : ReviewNavigation.Up(forward);
                Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
                float dockShift = S(UiTheme.DOCK_WIDTH + UiTheme.COLUMN_MARGIN) * 0.5f * _reviewHeight / Math.Max(1, _window.Height);
                Camera.Position = _reviewTarget - forward * _reviewDistance + right * dockShift;
                Camera.SetCustomView(forward, up);
            }
            Camera.Update();
        }

        /// <summary>Contextual keys and section handles; wheel over the dock is reserved for its scroll body.</summary>
        private void UpdateLightingReview(InputState input, float dt)
        {
            // Esc pauses on the LIGHTS tab, like the walkthrough; RESUME comes back to this view, RETURN TO WALK leaves it
            if (input.Pressed(InputAction.Back))
            {
                if (!CloseOverlay()) { _rightTab = 3; _settingsScroll.Reset(); SetPaused(true); }
                return;
            }
            if (input.Pressed(InputAction.Fullscreen)) { _window.ToggleFullscreen(); }
            if (input.Pressed(InputAction.Help)) { _showHelp = !_showHelp; }
            if (input.Pressed(InputAction.Screenshot)) { RequestScreenshot(); }
            if (input.Pressed(InputAction.LightingCalcMode)) { CycleCalcMode(); }
            if (input.Pressed(InputAction.LightingCalcRun) && _nativeJob == null) { QuickCalculateReviewRooms(); }
            if (input.Pressed(InputAction.SectionClear)) { ClearSection(); }
            if (input.Pressed(InputAction.SectionBox)) { _reviewEditBox = !_reviewEditBox; }
            if (_reviewListedCalc != _calc)
            {
                bool selected = _reviewRoom != null;
                RefreshReviewRooms();
                if (selected && _reviewRoom != null) { _reviewBounds = _reviewRoom.Bounds; _reviewScope = _reviewRoom.Title; }
                else if (selected) { ReviewBuilding(); }
                _calcTextKey = int.MinValue;
            }
            if (!input.LeftDown && _activeSlider >= 90 && _activeSlider <= 95) { _activeSlider = -1; }
            if (!UpdateReviewPointer(input)) { return; }
            Vector3 right = Camera.Right, screenUp = Vector3.Normalize(Vector3.Cross(right, Camera.Forward));
            float forwardMove = input.Axis(InputAction.MoveForward) - input.Axis(InputAction.MoveBack);
            float sideMove = input.Axis(InputAction.MoveRight) - input.Axis(InputAction.MoveLeft);
            if (_reviewView == 3)
            {
                float vertical = input.Axis(InputAction.ClashFlyUp) - input.Axis(InputAction.ClashFlyDown);
                Vector3 move = ReviewForward * forwardMove + right * sideMove + Vector3.UnitZ * vertical;
                if (move.LengthSquared() > 1f) { move = Vector3.Normalize(move); }
                _reviewEye += move * (input.Down(InputAction.Run) ? 12f : 3f) * dt;
                _reviewEye += ReviewForward * input.Wheel;
            }
            else
            {
                _reviewTarget += (screenUp * forwardMove + right * sideMove) * _reviewHeight * dt * 0.5f;
                _reviewHeight = Math.Clamp(_reviewHeight * MathF.Pow(0.86f, input.Wheel), 0.5f, 2000f);
            }
        }

        private void BuildLightingReviewHud(FontAtlas f, InputState input)
        {
            DrawCalcLabels(f);
            float x = S(UiTheme.COLUMN_MARGIN), w = MathF.Max(S(120), _window.Width - S(UiTheme.DOCK_WIDTH + UiTheme.COLUMN_MARGIN * 3));
            DrawReviewHeader(f, x, x, MathF.Min(w, S(620)));
            if (_showHelp)
            {
                _ui.TextWrapped(f.Small, x, _window.Height - S(150), w, HINT_REVIEW_NAV.Text, UiTheme.TEXT_MUTED, maxLines: 2);
            }
            if (CalcShowing) { BuildCalcLegend(f, x, S(100), _window.Height - S(24), collapsed: false); }
            BuildLightingReviewDock(f, input);
            if (_reviewOpen) { BuildReviewCube(f, input); }
            BuildToast(f, _window.Width);
        }

        private static readonly string[] REVIEW_DOCK_TABS = { "RESULTS", "VIEW", "ROOMS" };
        private int _reviewTab;

        private void BuildLightingReviewDock(FontAtlas f, InputState input)
        {
            BeginOverlays(input);
            DockFrame d = BeginDock("MODEL REVIEW", UiTheme.ACCENT, UiTheme.ACCENT, S(UiTheme.DOCK_WIDTH), _reviewScroll, notice: true, fullHeight: true);
            _reviewPanelRect = d.Rect;
            float x = d.Ix, w = d.Iw, half = (w - S(8)) * 0.5f;
            float y = BeginDockBody(d, _reviewScroll, input);
            int view = Segmented(f, input, x, y, w, REVIEW_VIEWS, _reviewView);
            if (view != _reviewView) { ReviewChangeView(view); }
            y += S(42);
            if (Scene.Levels.Length > 0)
            {
                float levelWidth = w - S(76);
                if (DropdownButton(f, input, x, y, levelWidth, S(32), _reviewLevel < 0 ? "All levels" : Scene.Levels[_reviewLevel].Name,
                    IsDropdownOpen(_reviewScroll, DROP_REVIEW_LEVEL), f.Body))
                {
                    var levels = new List<string>(Scene.Levels.Length + 1) { "All levels" };
                    foreach (LevelInfo level in Scene.Levels) { levels.Add(level.Name); }
                    OpenDropdown(_reviewScroll, DROP_REVIEW_LEVEL, x, y, levelWidth, S(32), levels, _reviewLevel + 1, i =>
                    {
                        ResetReviewPointer();
                        _reviewLevel = i - 1;
                        ReviewBuilding();
                    });
                }
                if (SmallButton(f, input, x + levelWidth + S(8), y, S(30), S(32), "↓")) { ReviewStepLevel(-1); }
                if (SmallButton(f, input, x + levelWidth + S(46), y, S(30), S(32), "↑")) { ReviewStepLevel(1); }
                y += S(42);
            }

            // One click: the rooms in view with the default settings
            var visible = ReviewVisibleRooms();
            float settingsW = S(92);
            string calcLabel = visible.Count == 0 ? "No rooms in view" : visible.Count == 1 ? "Calculate 1 room in view" : $"Calculate {visible.Count} rooms in view";
            if (NativeButton(f, input, x, y, w - settingsW - S(8), S(40), calcLabel, primary: true, enabled: visible.Count > 0 && _nativeJob == null)) { QuickCalculateReviewRooms(); }
            if (NativeButton(f, input, x + w - settingsW, y, settingsW, S(40), "Settings…")) { OpenQuickCalcSettings(); }
            y += S(46);
            string status = QuickCalcStatus;
            if (status != null)
            {
                bool working = _nativeJob != null || _nativeQueued;
                y += _ui.TextWrapped(f.Body, x, y, w, status, working ? UiTheme.ACCENT : UiTheme.DANGER, maxLines: 3) + S(6);
            }
            y += S(4);

            int tab = Tabs(f, input, x, y, w, REVIEW_DOCK_TABS, _reviewTab);
            if (tab != _reviewTab) { _reviewTab = tab; _activeSlider = -1; CloseOverlay(); }
            y += S(40);
            if (_reviewTab == 0) { y = BuildCalcDisplayPanel(f, input, x, y, w); }
            else if (_reviewTab == 1) { y = BuildReviewViewTab(f, input, x, y, w, half); }
            else { y = BuildReviewRoomsTab(f, input, x, y, w, half); }
            EndDockBody(_reviewScroll, input, y);
            _reviewNotice = _reviewPin ? "Click a visible surface to pin a revision, then enter a comment."
                : _calc != null && _reviewRoom != null && _reviewRoom.Zone == null ? $"No results for this room yet · {Key(InputAction.LightingCalcRun)} calculates the rooms in view"
                : _reviewView == 3 ? "Left drag pans · middle drag looks · select fly destinations in the room list"
                : "Left drag pans · middle drag orbits · click and release to select a room";
            bool close = EndDock(d, _reviewNotice, null, closeLabel: "RETURN TO WALK") == DOCK_CLOSE;
            DrawOverlays(f, input, d.X, d.Y, d.W, d.H);
            if (close) { RememberCalcView(); CloseLightingReview(); }
        }

        /// <summary>The dock's VIEW tab: shading, the crop box, reference grids and the PNG export.</summary>
        private float BuildReviewViewTab(FontAtlas f, InputState input, float x, float y, float w, float half)
        {
            bool sceneLighting = Checkbox(f, input, x, y, w, "Use scene lighting", _reviewSceneLighting);
            if (sceneLighting != _reviewSceneLighting)
            {
                _reviewSceneLighting = sceneLighting;
                _renderer.ResetOcclusion();
                _renderer.ReleaseReflectionHistory();
            }
            y += S(34);
            y = NativeHeading(f, x, y, w, "CROP BOX");
            if (NativeButton(f, input, x, y, half, S(34), _section.BoxOn ? "Crop: on" : "Crop: off", primary: _section.BoxOn))
            {
                _section.BoxOn = !_section.BoxOn;
                SectionChanged();
            }
            if (NativeButton(f, input, x + half + S(8), y, half, S(34), "Reset crop")) { ReviewFitCut(); }
            y += S(44);
            _reviewEditBox = Checkbox(f, input, x, y, w, "Edit box faces / show handles", _reviewEditBox);
            y += S(32);
            _reviewShowGrids = Checkbox(f, input, x, y, w, "Show model grids", _reviewShowGrids);
            y += S(32);
            bool caps = Checkbox(f, input, x, y, w, "Show cut caps", _reviewShowCaps);
            if (caps != _reviewShowCaps) { _reviewShowCaps = caps; _renderer.ResetOcclusion(); }
            y += S(34);
            if (_reviewEditBox && _section.BoxOn)
            {
                Vector3 min = _section.BoxMin - Scene.OriginOffset, max = _section.BoxMax - Scene.OriginOffset;
                Vector3 oldMin = min, oldMax = max;
                for (int axis = 0; axis < 3; axis++)
                {
                    float lo = MathF.Min(_reviewBounds.Min[axis] - 1f, min[axis]);
                    float hi = MathF.Max(_reviewBounds.Max[axis] + 1f, max[axis]);
                    Text.Clear().AppendLength(min[axis] - _reviewBounds.Min[axis], 2);
                    min[axis] = Slider(f, input, 90 + axis * 2, x, y, half, REVIEW_MIN_LABELS[axis], Text.Span,
                        min[axis], lo, MathF.Max(lo + 0.01f, max[axis] - SectionCut.MIN_SIZE));
                    Text.Clear().AppendLength(max[axis] - _reviewBounds.Min[axis], 2);
                    max[axis] = Slider(f, input, 91 + axis * 2, x + half + S(8), y, half, REVIEW_MAX_LABELS[axis], Text.Span,
                        max[axis], MathF.Min(hi - 0.01f, min[axis] + SectionCut.MIN_SIZE), hi);
                    y += S(48);
                }
                if (min != oldMin || max != oldMax) { SetBoxLocal(min, max); }
            }
            y = NativeHeading(f, x, y, w, "EXPORT");
            if (NativeButton(f, input, x, y, half, S(34), "Export PNG")) { RememberCalcView(); RequestScreenshot(); }
            if (NativeButton(f, input, x + half + S(8), y, half, S(34), _reviewPin ? "Cancel pin" : "Pin revision", primary: _reviewPin)) { _reviewPin = !_reviewPin; }
            y += S(42);
            Text.Clear().Append(_window.Width).Append(" × ").Append(_window.Height).Append(" px · includes the legend and visible values");
            y += _ui.TextWrapped(f.Body, x, y, w, Text.Span, UiTheme.TEXT_FAINT, maxLines: 2) + S(8);
            return y;
        }

        /// <summary>The dock's ROOMS tab: search and the room / calc zone list (a click frames the room).</summary>
        private float BuildReviewRoomsTab(FontAtlas f, InputState input, float x, float y, float w, float half)
        {
            if (NativeButton(f, input, x, y, half, S(34), _reviewSearch.Length == 0 ? "Find room…" : "Find: " + _reviewSearch))
            {
                BeginTextEdit("Find room (number, name or level)", _reviewSearch, 60, search => { _reviewSearch = search; FilterReviewRooms(); });
            }
            if (NativeButton(f, input, x + half + S(8), y, half, S(34), "Clear search", enabled: _reviewSearch.Length > 0)) { _reviewSearch = string.Empty; FilterReviewRooms(); }
            y += S(44);
            if (_reviewMatches.Count == 0)
            {
                y += _ui.TextWrapped(f.Body, x, y, w, _reviewRooms.Count == 0 ? "This model has no rooms. Review the whole building or adjust the crop box."
                    : "No rooms match your search.", UiTheme.TEXT_MUTED, maxLines: 2) + S(8);
            }
            foreach (ReviewRoom room in _reviewMatches)
            {
                if (y + S(48) >= _reviewScroll.Y && y <= _reviewScroll.Y + _reviewScroll.H)
                {
                    bool hover = Hover(input, x, y, w, S(46));
                    _ui.Rect(x, y, w, S(46), room == _reviewRoom ? Rgba.WithAlpha(UiTheme.ACCENT, 0.16f) : hover ? UiTheme.HOVER : UiTheme.ROW_STRIPE);
                    _ui.TextWrapped(f.Bold, x + S(8), y + S(5), w - S(16), room.Title, room == _reviewRoom ? UiTheme.ACCENT : UiTheme.TEXT, maxLines: 1);
                    _ui.TextWrapped(f.Small, x + S(8), y + S(26), w - S(16), room.Detail, UiTheme.TEXT_MUTED, maxLines: 1);
                    if (hover && input.LeftPressed) { ReviewSelectRoom(room); input.ConsumeClicks(); }
                }
                y += S(50);
            }
            return y;
        }

        /// <summary>The export has the composed model, labels, scope and legend; no dock or section handles.</summary>
        private void QueueReviewExport(int width)
        {
            DrawCalcLabels(_ui.Atlas);
            DrawReviewHeader(_ui.Atlas, S(20), S(20), MathF.Min(width - S(40), S(640)));
        }

        private void DrawReviewHeader(FontAtlas f, float x, float y, float w)
        {
            CalcZoneResult zone = _reviewRoom?.Zone;
            bool stats = zone != null && zone.Stats.Count > 0;
            _ui.Panel(x, y, w, S(stats ? 120 : 76), UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _ui.Text(f.Small, x + S(16), y + S(12), "BIMGO · MODEL REVIEW", UiTheme.ACCENT, S(1));
            _ui.TextWrapped(f.Bold, x + S(16), y + S(36), w - S(32), _reviewScope, UiTheme.TEXT, maxLines: 1);
            if (!stats) { return; }
            CalcStats s = zone.Stats;
            bool lux = CalcInLux;
            int decimals = lux ? 0 : 1;
            Text.Clear().Append("Illuminance · Avg ").Append(LightingCalcs.Display(s.Average, lux), decimals)
                .Append("   Min ").Append(LightingCalcs.Display(s.Minimum, lux), decimals)
                .Append("   Max ").Append(LightingCalcs.Display(s.Maximum, lux), decimals).Append(lux ? " lx" : " fc");
            _ui.TextWrapped(f.Small, x + S(16), y + S(65), w - S(32), Text.Span, UiTheme.TEXT_SOFT, maxLines: 1);
            Text.Clear().Append("Avg:Min ");
            if (float.IsFinite(s.AverageToMinimum)) { Text.Append(s.AverageToMinimum, 2).Append(":1"); }
            else { Text.Append("N/A"); }
            if (zone.Target?.Average > 0f)
            {
                Text.Append("   Target ").Append(LightingCalcs.Display(zone.Target.Average, lux), decimals).Append(lux ? " lx" : " fc");
            }
            if (!string.IsNullOrEmpty(zone.Target?.Result)) { Text.Append(" · ").Append(zone.Target.Result); }
            _ui.TextWrapped(f.Small, x + S(16), y + S(89), w - S(32), Text.Span,
                string.Equals(zone.Target?.Result, "Misses", StringComparison.OrdinalIgnoreCase) ? UiTheme.DANGER : UiTheme.TEXT_SOFT, maxLines: 1);
        }
    }
}
