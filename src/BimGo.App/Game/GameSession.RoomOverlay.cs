using System.Globalization;
using System.Numerics;
using BimGo.Audio;
using BimGo.Native;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round Z-A: the room data overlay. Floating room tags (number · name and up to four picked parameters, at the
    /// room's most open point, a set height above its floor; N shows / hides them), and the target check: a value
    /// parameter (Area by default) against a target parameter (e.g. "Required area") with a tolerance, green / amber /
    /// red on the tags, as a floor tint and as a dot in the FIND ROOM list. Set up on FIND ROOM's ROOM DATA tab (Ctrl+F),
    /// saved with the model beside the grids (visibility.json / the live sidecar). Rooms come from the snapshot
    /// (<see cref="RoomInfo.Parameters"/>: every non-empty instance parameter, extracted since Z-A).
    /// Tags of rooms behind walls fade out (a few rays a frame, round robin) unless "Through walls" is ticked; the
    /// room you stand in always shows. Nearest tags win where they would overlap. Allocation-free per frame: texts
    /// and checks are made again only when a setting changes.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>Slider ids (unique across the panels' sliders).</summary>
        private const int SLIDER_ROOM_HEIGHT = 80, SLIDER_ROOM_DISTANCE = 81, SLIDER_ROOM_TOLERANCE = 82;

        private const int ROOM_TAG_MAX = 48; // tags drawn at most
        private const int ROOM_RAYS_PER_FRAME = 12; // occlusion rays a frame (round robin over the candidates)
        private const int ROOM_PLACES_PER_FRAME = 24; // tag points found a frame (the first time a room comes near)
        private const float ROOM_TAG_FADE = 6f; // alpha per second
        private const float ROOM_TINT_ALPHA = 0.3f;
        private const float ROOM_TINT_MIN_DISTANCE = 30f;

        private static readonly string[] ROOM_TABS = { "FIND", "ROOM DATA" };
        private static readonly string[] ROOM_RULES = { "WITHIN ±", "AT LEAST", "AT MOST" };
        private static readonly string[] ROOM_SHOWN_LABELS = { "SHOWN 1", "SHOWN 2", "SHOWN 3", "SHOWN 4" };
        private static readonly uint ROOM_PASS = Rgba.Hex(0x4ADE80), ROOM_NEAR = Rgba.Hex(0xFBBF24), ROOM_FAIL = Rgba.Hex(0xF87171),
            ROOM_NONE = Rgba.Hex(0x94A3B8);

        private static readonly KeyHint HINT_ROOM_TAGS_LABEL = new(() => $"Room tags ({Key(InputAction.RoomTags)})");
        private static readonly KeyHint HINT_ROOM_NO_DATA = new(() =>
            $"This snapshot has no room parameters: export again (or {Key(InputAction.Refresh)} live) with this version to pick them. Tags still show numbers and names.");

        /// <summary>One row of the ROOM DATA parameter list (made once per session).</summary>
        private sealed class RoomParamRow
        {
            public string Name;
            public string Detail;
            public string Search;
        }

        private RoomOverlaySettings _roomOverlay = new();
        private int _roomTab; // 0 FIND, 1 ROOM DATA
        private readonly ScrollRegion _roomParamScroll = new();
        private readonly char[] _roomParamSearch = new char[ROOM_SEARCH_MAX];
        private int _roomParamSearchLength;
        private List<RoomParamRow> _roomParamRows;
        private readonly List<RoomParamRow> _roomParamMatches = new();
        private bool _roomParamMatchesDirty = true;
        private string _roomDataNotice;
        private float _roomDataNoticeUntil;

        // Per room: built for this scene on first use (texts and checks again when a setting changes)
        private SceneData _roomCacheScene;
        private Vector2[] _roomTagXY;
        private float[] _roomTagFloorZ;
        private bool[] _roomTagPlaced;
        private string[] _roomTagTitle;
        private string[][] _roomTagValues;
        private string[] _roomTagCheckText;
        private RoomCheckStatus[] _roomCheck;
        private float[] _roomTagAlpha;
        private bool[] _roomTagSeen;
        private int[][] _roomTintTriangles; // null: not made yet; empty: nothing to draw
        private int[] _roomTintLoop;
        private int _roomOverlayRevision, _roomBuiltRevision = -1;
        private bool _roomsHaveData;

        // This frame's tags
        private int[] _roomCandidates;
        private float[] _roomCandidateDistance;
        private int _roomCandidateCount, _roomRayCursor;
        private readonly Vector4[] _roomTagRects = new Vector4[ROOM_TAG_MAX];
        private float _roomTagClock = -1f;

        // Triangulation scratch (tint, made once per room)
        private readonly List<Vector3> _roomTriPoints = new();
        private readonly List<int> _roomTriScratch = new();

        // The check's counts and summary line (made with the texts)
        private int _roomCheckPass, _roomCheckNear, _roomCheckFail, _roomCheckNone;
        private string _roomCheckSummary = string.Empty;

        // Round Z-A: the floor tint has its own overlay (depth-tested only, never the markers' x-ray copy)
        private readonly Overlay3D _roomTintOverlay = new();

        #endregion

        #region Settings

        /// <summary>Restores the saved room overlay (or the defaults: tags off, no check).</summary>
        private void RestoreRoomOverlay(RoomOverlaySettings saved)
        {
            _roomOverlay = saved?.Copy().Clean() ?? new RoomOverlaySettings();
            _roomOverlayRevision++;
        }

        /// <summary>The room overlay for visibility.json (null at the defaults).</summary>
        private RoomOverlaySettings RoomOverlayForSave() => _roomOverlay.IsDefault ? null : _roomOverlay.Copy();

        /// <summary>A room overlay setting changed: texts and checks again, save with the model.</summary>
        private void RoomOverlayChanged()
        {
            _roomOverlay.Clean();
            _roomOverlayRevision++;
            VisibilityChanged();
        }

        /// <summary>N: tags on / off, with a toast saying where the options are (or why there is nothing to tag).</summary>
        private void ToggleRoomTags()
        {
            if (Scene.Rooms.Length == 0)
            {
                Sound.Play(SoundId.Error);
                Toast("This model has no placed rooms to tag", 3f);
                return;
            }
            EnsureRoomCaches();
            _roomOverlay.Tags = !_roomOverlay.Tags;
            RoomOverlayChanged();
            Sound.Play(SoundId.UiClick);
            if (!_roomOverlay.Tags)
            {
                Toast("Room tags off", 1.6f);
                return;
            }
            Toast(_roomsHaveData
                ? $"Room tags on · {Key(InputAction.FindRoom)} → ROOM DATA for parameters and checks"
                : $"Room tags on · this snapshot has no room parameters (export again / {Key(InputAction.Refresh)} with this version)", 3f);
        }

        #endregion

        #region Caches

        /// <summary>Makes the per-room arrays for this scene (once; again after a refresh brings a new scene).</summary>
        private void EnsureRoomCaches()
        {
            if (ReferenceEquals(_roomCacheScene, Scene) && _roomTagXY != null) { return; }
            _roomCacheScene = Scene;
            int n = Scene.Rooms.Length;
            _roomTagXY = new Vector2[n];
            _roomTagFloorZ = new float[n];
            _roomTagPlaced = new bool[n];
            _roomTagTitle = new string[n];
            _roomTagValues = new string[n][];
            _roomTagCheckText = new string[n];
            _roomCheck = new RoomCheckStatus[n];
            _roomTagAlpha = new float[n];
            _roomTagSeen = new bool[n];
            _roomTintTriangles = new int[n][];
            _roomTintLoop = new int[n];
            _roomCandidates = new int[n];
            _roomCandidateDistance = new float[n];
            _roomCandidateCount = 0;
            _roomRayCursor = 0;
            _roomParamRows = null;
            _roomParamMatchesDirty = true;
            _roomBuiltRevision = -1;
            _roomsHaveData = false;
            foreach (RoomInfo room in Scene.Rooms)
            {
                if (room.Parameters.Length > 0) { _roomsHaveData = true; break; }
            }
        }

        /// <summary>
        /// The tags' texts and the check results, made again when a setting changed (allocates then only).
        /// </summary>
        private void RebuildRoomTexts()
        {
            EnsureRoomCaches();
            if (_roomBuiltRevision == _roomOverlayRevision) { return; }
            _roomBuiltRevision = _roomOverlayRevision;

            List<string> shown = _roomOverlay.Shown;
            RoomCheckRule rule = _roomOverlay.CheckRule;
            string valueName = _roomOverlay.Value, targetName = _roomOverlay.Target;
            _roomCheckPass = _roomCheckNear = _roomCheckFail = _roomCheckNone = 0;
            for (int i = 0; i < Scene.Rooms.Length; i++)
            {
                RoomInfo room = Scene.Rooms[i];
                _roomTagTitle[i] = string.IsNullOrWhiteSpace(room.Number) || room.Number == "—" ? room.Name : $"{room.Number} · {room.Name}";

                string[] values = shown.Count == 0 ? Array.Empty<string>() : new string[shown.Count];
                for (int s = 0; s < shown.Count; s++)
                {
                    values[s] = room.TryGetParameter(shown[s], out RoomParameter p) ? p.Value : "—";
                }
                _roomTagValues[i] = values;

                if (!_roomOverlay.Checks)
                {
                    _roomCheck[i] = RoomCheckStatus.None;
                    _roomTagCheckText[i] = null;
                    continue;
                }
                bool hasValue = RoomValue(room, valueName, out double value, out string valueText);
                bool hasTarget = RoomValue(room, targetName, out double target, out string targetText);
                RoomCheckStatus status = RoomChecks.Evaluate(value, target, rule, _roomOverlay.Tolerance, out double difference);
                _roomCheck[i] = status;
                switch (status)
                {
                    case RoomCheckStatus.Pass: _roomCheckPass++; break;
                    case RoomCheckStatus.Near: _roomCheckNear++; break;
                    case RoomCheckStatus.Fail: _roomCheckFail++; break;
                    default: _roomCheckNone++; break;
                }
                if (status != RoomCheckStatus.None)
                {
                    _roomTagCheckText[i] = $"{valueText} / {targetText} · {difference.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)} %";
                }
                else if (string.IsNullOrEmpty(targetName))
                {
                    _roomTagCheckText[i] = null; // no target picked yet: nothing to say on the tag
                }
                else
                {
                    _roomTagCheckText[i] = !hasTarget ? $"No {targetName}" : !hasValue ? $"No {valueName}" : $"{targetName}: not a number";
                }
            }

            if (!_roomOverlay.Checks) { _roomCheckSummary = "Check off"; }
            else if (string.IsNullOrEmpty(targetName)) { _roomCheckSummary = "Pick a TARGET parameter below"; }
            else { _roomCheckSummary = $"{_roomCheckPass} pass · {_roomCheckNear} near · {_roomCheckFail} off · {_roomCheckNone} no data"; }
        }

        /// <summary>
        /// A room's value of a parameter as a number and its display text. For an older snapshot without parameters,
        /// "Area" falls back to Revit's area (else the plan area) in m².
        /// </summary>
        private static bool RoomValue(RoomInfo room, string name, out double number, out string text)
        {
            number = double.NaN;
            text = null;
            if (string.IsNullOrEmpty(name)) { return false; }
            if (room.TryGetParameter(name, out RoomParameter p))
            {
                number = p.Number;
                text = p.Value;
                return true;
            }
            if (room.Parameters.Length == 0 && string.Equals(name, RoomOverlaySettings.DEFAULT_VALUE, StringComparison.OrdinalIgnoreCase))
            {
                float area = float.IsFinite(room.Area) ? room.Area : PlanArea(room);
                number = area;
                text = UnitFormat.Area(area);
                return true;
            }
            return false;
        }

        /// <summary>The check's colour for a room.</summary>
        private static uint RoomCheckColour(RoomCheckStatus status) => status switch
        {
            RoomCheckStatus.Pass => ROOM_PASS,
            RoomCheckStatus.Near => ROOM_NEAR,
            RoomCheckStatus.Fail => ROOM_FAIL,
            _ => ROOM_NONE
        };

        /// <summary>
        /// Finds a room's tag point (its most open point) and the floor under it (picked; kept within 0.4 m of the
        /// room's bottom so a table or a stair doesn't lift it).
        /// </summary>
        private void PlaceRoomTag(int index)
        {
            RoomInfo room = Scene.Rooms[index];
            Vector2 xy = RoomTagPlacement.Place(room);
            float floor = FloorAt(xy, room.BottomZ).Z - 0.02f;
            if (!float.IsFinite(floor) || MathF.Abs(floor - room.BottomZ) > 0.4f) { floor = room.BottomZ; }
            _roomTagXY[index] = xy;
            _roomTagFloorZ[index] = floor;
            _roomTagPlaced[index] = true;
        }

        /// <summary>A placed room's tag point in the world (the set height above its floor, under its top).</summary>
        private Vector3 RoomTagPoint(int index)
        {
            RoomInfo room = Scene.Rooms[index];
            float z = _roomTagFloorZ[index] + _roomOverlay.Height;
            z = MathF.Min(z, MathF.Max(_roomTagFloorZ[index] + 0.2f, room.TopZ - 0.1f));
            return new Vector3(_roomTagXY[index], z);
        }

        /// <summary>
        /// True when a room could be near: a lower bound of the eye's distance to its volume is within the limit.
        /// Rooms of hidden links (or link rooms switched off) never are.
        /// </summary>
        private bool RoomMaybeNear(RoomInfo room, Vector3 eye, float limit2)
        {
            if (room.Link > 0 && (!_roomOverlay.LinkRooms || (uint)room.Link >= (uint)_linkVisible.Length || !_linkVisible[room.Link])) { return false; }
            Vector2 centre = (room.Min + room.Max) * 0.5f;
            float half = (room.Max - room.Min).Length() * 0.5f;
            float plan = MathF.Max(0f, Vector2.Distance(centre, new Vector2(eye.X, eye.Y)) - half);
            float dz = eye.Z - Math.Clamp(eye.Z, room.BottomZ, room.TopZ);
            return plan * plan + dz * dz <= limit2;
        }

        /// <summary>True when something opaque stands between the eye and a point (static scene and moved elements).</summary>
        private bool RoomTagHidden(Vector3 eye, Vector3 point)
        {
            Vector3 d = point - eye;
            float distance = d.Length();
            if (distance < 0.5f) { return false; }
            d /= distance;
            float reach = distance - 0.15f;
            if (_bvh.Raycast(eye, d, reach, _pickMask, out RayHit _, opaqueOnly: true)) { return true; }
            return Dynamics != null && Dynamics.Raycast(eye, d, reach, out RayHit _, null, opaqueOnly: true);
        }

        #endregion

        #region Drawing

        /// <summary>
        /// The floating tags (2D UI over the view, under the guns' labels): rooms within the distance, nearest first,
        /// fading out behind walls unless "Through walls" is ticked. Allocation-free.
        /// </summary>
        private void DrawRoomTags(FontAtlas f)
        {
            if (!_roomOverlay.Tags || Scene.Rooms.Length == 0) { return; }
            RebuildRoomTexts();

            float dt = _roomTagClock < 0f ? 0f : Math.Clamp(_clock - _roomTagClock, 0f, 0.1f);
            _roomTagClock = _clock;
            Vector3 eye = Camera.Position;
            float limit = _roomOverlay.Distance, limit2 = limit * limit;

            // Candidates: rooms near enough (tag points found as they come near, a few a frame)
            _roomCandidateCount = 0;
            int places = 0;
            for (int i = 0; i < Scene.Rooms.Length; i++)
            {
                if (!RoomMaybeNear(Scene.Rooms[i], eye, limit2))
                {
                    _roomTagAlpha[i] = 0f;
                    continue;
                }
                if (!_roomTagPlaced[i])
                {
                    if (places >= ROOM_PLACES_PER_FRAME) { continue; }
                    PlaceRoomTag(i);
                    places++;
                }
                float d2 = Vector3.DistanceSquared(RoomTagPoint(i), eye);
                if (d2 > limit2)
                {
                    _roomTagAlpha[i] = 0f;
                    continue;
                }
                _roomCandidates[_roomCandidateCount] = i;
                _roomCandidateDistance[_roomCandidateCount] = d2;
                _roomCandidateCount++;
            }
            if (_roomCandidateCount == 0) { return; }
            Array.Sort(_roomCandidateDistance, _roomCandidates, 0, _roomCandidateCount);

            // Behind walls? A few rays a frame, round robin (the room you're in always shows)
            if (!_roomOverlay.ThroughWalls)
            {
                int rays = Math.Min(ROOM_RAYS_PER_FRAME, _roomCandidateCount);
                for (int k = 0; k < rays; k++)
                {
                    int i = _roomCandidates[(_roomRayCursor + k) % _roomCandidateCount];
                    _roomTagSeen[i] = i == _roomIndex || !RoomTagHidden(eye, RoomTagPoint(i));
                }
                _roomRayCursor = (_roomRayCursor + rays) % _roomCandidateCount;
            }

            int width = _window.Width, height = _window.Height;
            float pad = S(7), gap = S(2), stalk = S(10), bar = S(3);
            UiFont titleFont = f.Bold, lineFont = f.Small;
            int drawn = 0;
            for (int c = 0; c < _roomCandidateCount && drawn < ROOM_TAG_MAX; c++)
            {
                int i = _roomCandidates[c];
                bool show = _roomOverlay.ThroughWalls || _roomTagSeen[i] || i == _roomIndex;
                float alpha = _roomTagAlpha[i];
                alpha = show ? MathF.Min(1f, alpha + dt * ROOM_TAG_FADE) : MathF.Max(0f, alpha - dt * ROOM_TAG_FADE);
                if (dt == 0f && show) { alpha = 1f; }
                _roomTagAlpha[i] = alpha;
                if (alpha < 0.02f) { continue; }

                Vector3 point = RoomTagPoint(i);
                if (!Camera.WorldToScreen(point, out Vector2 screen)) { continue; }
                if (screen.X < -S(200) || screen.Y < -S(40) || screen.X > width + S(200) || screen.Y > height + S(200)) { continue; }

                // Near tags carry the parameters and the check; far ones (past 60 % of the distance) just the title
                bool full = _roomCandidateDistance[c] < limit2 * 0.36f;
                string[] values = full ? _roomTagValues[i] : Array.Empty<string>();
                string check = full && _roomOverlay.Checks ? _roomTagCheckText[i] : null;

                string title = _roomTagTitle[i];
                float w = UiBatch.Measure(titleFont, title);
                List<string> shown = _roomOverlay.Shown;
                for (int s = 0; s < values.Length && s < shown.Count; s++)
                {
                    w = MathF.Max(w, UiBatch.Measure(lineFont, shown[s]) + S(6) + UiBatch.Measure(lineFont, values[s]));
                }
                if (check != null) { w = MathF.Max(w, UiBatch.Measure(lineFont, check)); }
                w += pad * 2f + bar;
                int lines = Math.Min(values.Length, shown.Count) + (check != null ? 1 : 0);
                float h = pad * 2f + titleFont.LineHeight + lines * (lineFont.LineHeight + gap);

                float x = MathF.Round(screen.X - w * 0.5f), y = MathF.Round(screen.Y - stalk - h);

                // Nearest first: a tag that would overlap one already drawn waits until you come closer
                bool overlaps = false;
                for (int r = 0; r < drawn; r++)
                {
                    Vector4 o = _roomTagRects[r];
                    if (x < o.X + o.Z + S(2) && x + w + S(2) > o.X && y < o.Y + o.W + S(2) && y + h + S(2) > o.Y) { overlaps = true; break; }
                }
                if (overlaps) { continue; }
                _roomTagRects[drawn++] = new Vector4(x, y, w, h);

                uint accent = _roomOverlay.Checks ? RoomCheckColour(_roomCheck[i]) : UiTheme.ACCENT;
                _ui.Rect(screen.X - S(0.5f), y + h, S(1), stalk, Rgba.WithAlpha(accent, 0.7f * alpha));
                _ui.Circle(screen.X, screen.Y, S(3), Rgba.WithAlpha(accent, alpha), 12);
                _ui.Panel(x, y, w, h, Rgba.Hex(0x0C0E12, 0.82f * alpha), Rgba.Hex(0xFFFFFF, 0.14f * alpha));
                _ui.Rect(x, y, bar, h, Rgba.WithAlpha(accent, alpha));

                float tx = x + bar + pad, ty = y + pad;
                _ui.Text(titleFont, tx, ty, title, Rgba.WithAlpha(i == _roomIndex ? UiTheme.ACCENT : UiTheme.TEXT, alpha));
                ty += titleFont.LineHeight + gap;
                for (int s = 0; s < values.Length && s < shown.Count; s++)
                {
                    float used = _ui.Text(lineFont, tx, ty, shown[s], Rgba.WithAlpha(UiTheme.TEXT_MUTED, alpha));
                    _ui.Text(lineFont, tx + used + S(6), ty, values[s], Rgba.WithAlpha(UiTheme.TEXT, alpha));
                    ty += lineFont.LineHeight + gap;
                }
                if (check != null) { _ui.Text(lineFont, tx, ty, check, Rgba.WithAlpha(accent, alpha)); }
            }
        }

        /// <summary>
        /// The check's floor tint (rooms near the eye, depth-tested only), in the main view with the markers. The
        /// outer loop is filled; islands (columns, shafts) are covered by their own geometry.
        /// </summary>
        private void DrawRoomTint()
        {
            if (!_roomOverlay.Checks || !_roomOverlay.FloorTint || string.IsNullOrEmpty(_roomOverlay.Target) || Scene.Rooms.Length == 0) { return; }
            RebuildRoomTexts();
            Vector3 eye = Camera.Position;
            float limit = MathF.Max(_roomOverlay.Distance, ROOM_TINT_MIN_DISTANCE), limit2 = limit * limit;

            _roomTintOverlay.Begin(Camera);
            int places = 0;
            for (int i = 0; i < Scene.Rooms.Length; i++)
            {
                RoomCheckStatus status = _roomCheck[i];
                if (status == RoomCheckStatus.None) { continue; }
                RoomInfo room = Scene.Rooms[i];
                if (!RoomMaybeNear(room, eye, limit2)) { continue; }
                if (!_roomTagPlaced[i])
                {
                    if (places >= ROOM_PLACES_PER_FRAME) { continue; }
                    PlaceRoomTag(i);
                    places++;
                }
                int[] triangles = RoomTintTriangles(i);
                if (triangles.Length == 0) { continue; }

                Vector2[] loop = room.Loops[_roomTintLoop[i]];
                float z = _roomTagFloorZ[i] + 0.02f;
                uint colour = Rgba.WithAlpha(RoomCheckColour(status), ROOM_TINT_ALPHA);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    _roomTintOverlay.Triangle(new Vector3(loop[triangles[t]], z), new Vector3(loop[triangles[t + 1]], z), new Vector3(loop[triangles[t + 2]], z), colour);
                }
            }
            _roomTintOverlay.Draw(Camera, depthTest: true, alpha: 1f, additive: false);
        }

        /// <summary>A room's outer loop as triangles (index triples into it), made once.</summary>
        private int[] RoomTintTriangles(int index)
        {
            if (_roomTintTriangles[index] != null) { return _roomTintTriangles[index]; }
            RoomInfo room = Scene.Rooms[index];

            // The outer loop is the largest
            int outer = -1;
            float largest = 0f;
            for (int l = 0; l < room.Loops.Length; l++)
            {
                Vector2[] loop = room.Loops[l];
                if (loop == null || loop.Length < 3) { continue; }
                float area = 0f;
                for (int a = 0, b = loop.Length - 1; a < loop.Length; b = a++) { area += loop[b].X * loop[a].Y - loop[a].X * loop[b].Y; }
                area = MathF.Abs(area);
                if (area > largest)
                {
                    largest = area;
                    outer = l;
                }
            }
            if (outer < 0)
            {
                _roomTintTriangles[index] = Array.Empty<int>();
                return _roomTintTriangles[index];
            }

            _roomTriPoints.Clear();
            foreach (Vector2 p in room.Loops[outer]) { _roomTriPoints.Add(new Vector3(p, 0f)); }
            MeasureMath.Triangulate(_roomTriPoints, Vector3.UnitZ, _roomTriScratch);
            _roomTintLoop[index] = outer;
            _roomTintTriangles[index] = _roomTriScratch.ToArray();
            return _roomTintTriangles[index];
        }

        #endregion

        #region ROOM DATA tab

        /// <summary>
        /// FIND ROOM's ROOM DATA tab: tags (on, height, distance, through walls, link rooms), the target check (on,
        /// value and target, rule, tolerance, floor tint) and the parameter list (SHOW on the tags, VALUE / TARGET for
        /// the check), filtered by typing.
        /// </summary>
        /// <returns>The panel's notice (null: none).</returns>
        private string BuildRoomDataTab(FontAtlas f, InputState input, float ix, float iw, float cy, float bottom)
        {
            if (!input.LeftDown && _activeSlider >= SLIDER_ROOM_HEIGHT && _activeSlider <= SLIDER_ROOM_TOLERANCE) { _activeSlider = -1; }
            RebuildRoomTexts();
            TypeIntoRoomParamSearch(input);

            if (!_roomsHaveData)
            {
                _ui.TextWrapped(f.Small, ix, cy, iw, HINT_ROOM_NO_DATA.Text, UiTheme.MEASURE_LABEL, maxLines: 2);
                cy += S(40);
            }

            // Tags
            float col = iw / 3f;
            if (RoomDataCheck(f, input, ix, cy, col, HINT_ROOM_TAGS_LABEL.Text, _roomOverlay.Tags, out bool v)) { _roomOverlay.Tags = v; RoomOverlayChanged(); }
            if (RoomDataCheck(f, input, ix + col, cy, col, "Through walls", _roomOverlay.ThroughWalls, out v)) { _roomOverlay.ThroughWalls = v; RoomOverlayChanged(); }
            if (Scene.Links.Length > 0)
            {
                if (RoomDataCheck(f, input, ix + col * 2f, cy, col, "Link rooms", _roomOverlay.LinkRooms, out v)) { _roomOverlay.LinkRooms = v; RoomOverlayChanged(); }
            }
            cy += S(32);

            float half = (iw - S(UiTheme.GAP)) * 0.5f;
            Text.Clear().AppendLength(_roomOverlay.Height, 1);
            float height = Slider(f, input, SLIDER_ROOM_HEIGHT, ix, cy, half, "Height above floor", Text.Span, _roomOverlay.Height,
                RoomOverlaySettings.MIN_HEIGHT, RoomOverlaySettings.MAX_HEIGHT);
            if (_activeSlider == SLIDER_ROOM_HEIGHT && MathF.Abs(MathF.Round(height * 10f) / 10f - _roomOverlay.Height) > 1e-4f)
            {
                _roomOverlay.Height = MathF.Round(height * 10f) / 10f;
                RoomOverlayChanged();
            }
            Text.Clear().AppendLength(_roomOverlay.Distance, 0);
            float distance = Slider(f, input, SLIDER_ROOM_DISTANCE, ix + half + S(UiTheme.GAP), cy, half, "Tags within", Text.Span, _roomOverlay.Distance,
                RoomOverlaySettings.MIN_DISTANCE, RoomOverlaySettings.MAX_DISTANCE);
            if (_activeSlider == SLIDER_ROOM_DISTANCE && MathF.Abs(MathF.Round(distance / 5f) * 5f - _roomOverlay.Distance) > 1e-3f)
            {
                _roomOverlay.Distance = MathF.Round(distance / 5f) * 5f;
                RoomOverlayChanged();
            }
            cy += S(58);

            // Target check
            _ui.Rect(ix, cy, iw, MathF.Max(1f, UiScale), UiTheme.DIVIDER);
            cy += S(10);
            if (RoomDataCheck(f, input, ix, cy, col, "Target check", _roomOverlay.Checks, out v)) { _roomOverlay.Checks = v; RoomOverlayChanged(); }
            if (RoomDataCheck(f, input, ix + col, cy, col, "Floor tint", _roomOverlay.FloorTint, out v)) { _roomOverlay.FloorTint = v; RoomOverlayChanged(); }
            _ui.TextRight(f.Small, ix + iw, cy + S(1), _roomCheckSummary, _roomOverlay.Checks ? UiTheme.TEXT_SOFT : UiTheme.TEXT_FAINT);
            cy += S(30);

            float labelW = S(60);
            _ui.Text(f.Small, ix, cy + S(2), "VALUE", UiTheme.TEXT_FAINT, S(1f));
            _ui.TextWrapped(f.Body, ix + labelW, cy, half - labelW, _roomOverlay.Value, UiTheme.TEXT, maxLines: 1);
            _ui.Text(f.Small, ix + half + S(UiTheme.GAP), cy + S(2), "TARGET", UiTheme.TEXT_FAINT, S(1f));
            _ui.TextWrapped(f.Body, ix + half + S(UiTheme.GAP) + labelW, cy, half - labelW, _roomOverlay.Target ?? "pick one below",
                _roomOverlay.Target == null ? UiTheme.TEXT_FAINT : UiTheme.TEXT, maxLines: 1);
            cy += S(28);

            int rule = (int)_roomOverlay.CheckRule;
            int pickedRule = Segmented(f, input, ix, cy, half, ROOM_RULES, rule);
            if (pickedRule != rule && pickedRule >= 0)
            {
                _roomOverlay.CheckRule = (RoomCheckRule)pickedRule;
                RoomOverlayChanged();
            }
            Text.Clear().Append(_roomOverlay.Tolerance, 1).Append(" %");
            float tolerance = Slider(f, input, SLIDER_ROOM_TOLERANCE, ix + half + S(UiTheme.GAP), cy - S(4), half, "Tolerance", Text.Span, _roomOverlay.Tolerance,
                0f, RoomOverlaySettings.MAX_TOLERANCE);
            if (_activeSlider == SLIDER_ROOM_TOLERANCE && MathF.Abs(MathF.Round(tolerance * 2f) / 2f - _roomOverlay.Tolerance) > 1e-4f)
            {
                _roomOverlay.Tolerance = MathF.Round(tolerance * 2f) / 2f;
                RoomOverlayChanged();
            }
            cy += S(54);

            // Parameters
            _ui.Rect(ix, cy, iw, MathF.Max(1f, UiScale), UiTheme.DIVIDER);
            cy += S(10);
            float boxH = S(UiTheme.CONTROL_HEIGHT);
            _ui.Panel(ix, cy, iw, boxH, UiTheme.CONTROL, UiTheme.CONTROL_BORDER);
            float textY = cy + boxH * 0.5f - f.Body.LineHeight * 0.5f;
            if (_roomParamSearchLength == 0) { _ui.Text(f.Body, ix + S(10), textY, "Type to find a parameter (e.g. area, finish, occupancy)", UiTheme.TEXT_FAINT); }
            else
            {
                float used = _ui.Text(f.Body, ix + S(10), textY, _roomParamSearch.AsSpan(0, _roomParamSearchLength), UiTheme.TEXT);
                if (((int)(_clock * 2f) & 1) == 0) { _ui.Rect(ix + S(11) + used, cy + S(7), S(2), boxH - S(14), UiTheme.TEXT_SOFT); }
            }
            cy += boxH + S(UiTheme.GAP_SMALL);

            RefreshRoomParamMatches();
            BuildRoomParamRows(f, input, ix, cy, iw, bottom);
            return _clock < _roomDataNoticeUntil ? _roomDataNotice : null;
        }

        /// <summary>A checkbox; true when it changed (the caller sets the field and calls <see cref="RoomOverlayChanged"/>).</summary>
        private bool RoomDataCheck(FontAtlas f, InputState input, float x, float y, float w, string label, bool value, out bool result)
        {
            result = Checkbox(f, input, x, y, w - S(6), label, value);
            return result != value;
        }

        /// <summary>
        /// The parameter rows: name and how many rooms have it (with an example), and SHOW / VALUE / TARGET.
        /// </summary>
        private void BuildRoomParamRows(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            if (_roomParamMatches.Count == 0)
            {
                _ui.Text(f.Body, x, y + S(6), _roomParamRows.Count == 0 ? "No room parameters in this snapshot." : "No parameter matches. Backspace to change the search.", UiTheme.TEXT_MUTED);
                return;
            }

            float rowH = S(UiTheme.ROW_HEIGHT);
            float top = BeginScroll(_roomParamScroll, input, x, y, w, bottom - y, out float cw);
            VisibleRows(_roomParamScroll, rowH, _roomParamMatches.Count, out int first, out int last);
            float bh = S(26), showW = S(76), valueW = S(64), targetW = S(70), bgap = S(6);
            float buttonsW = showW + valueW + targetW + bgap * 2f + S(10);
            for (int i = first; i < last; i++)
            {
                RoomParamRow row = _roomParamMatches[i];
                float ry = top + i * rowH;
                _ui.Rect(x, ry, cw, rowH - S(4), (i & 1) == 0 ? UiTheme.ROW_STRIPE : 0u);
                _ui.TextWrapped(f.Bold, x + S(12), ry + S(5), cw - buttonsW - S(12), row.Name, UiTheme.TEXT, maxLines: 1);
                _ui.TextWrapped(f.Small, x + S(12), ry + S(23), cw - buttonsW - S(12), row.Detail, UiTheme.TEXT_FAINT, maxLines: 1);

                float bx = x + cw - buttonsW + S(4), by = ry + (rowH - S(4) - bh) * 0.5f;
                int shownAt = IndexOfIgnoreCase(_roomOverlay.Shown, row.Name);
                if (RoomToggle(f, input, bx, by, showW, bh, shownAt >= 0 ? ROOM_SHOWN_LABELS[Math.Min(shownAt, ROOM_SHOWN_LABELS.Length - 1)] : "SHOW", shownAt >= 0))
                {
                    if (shownAt >= 0) { _roomOverlay.Shown.RemoveAt(shownAt); }
                    else if (_roomOverlay.Shown.Count >= RoomOverlaySettings.MAX_SHOWN) { RoomDataNotice($"Tags show up to {RoomOverlaySettings.MAX_SHOWN} parameters: take one off first."); }
                    else
                    {
                        _roomOverlay.Shown.Add(row.Name);
                        if (!_roomOverlay.Tags) { _roomOverlay.Tags = true; RoomDataNotice("Room tags on (they show the picked parameters)."); }
                    }
                    RoomOverlayChanged();
                }
                bx += showW + bgap;
                bool isValue = string.Equals(_roomOverlay.Value, row.Name, StringComparison.OrdinalIgnoreCase);
                if (RoomToggle(f, input, bx, by, valueW, bh, "VALUE", isValue) && !isValue)
                {
                    _roomOverlay.Value = row.Name;
                    RoomOverlayChanged();
                }
                bx += valueW + bgap;
                bool isTarget = string.Equals(_roomOverlay.Target, row.Name, StringComparison.OrdinalIgnoreCase);
                if (RoomToggle(f, input, bx, by, targetW, bh, "TARGET", isTarget))
                {
                    _roomOverlay.Target = isTarget ? null : row.Name;
                    if (!isTarget && !_roomOverlay.Checks)
                    {
                        _roomOverlay.Checks = true;
                        RoomDataNotice($"Target check on: {_roomOverlay.Value} against {row.Name}.");
                    }
                    RoomOverlayChanged();
                }
            }
            EndScroll(_roomParamScroll, input, top + _roomParamMatches.Count * rowH);
        }

        /// <summary>A small button lit when on.</summary>
        private bool RoomToggle(FontAtlas f, InputState input, float x, float y, float w, float h, string label, bool on)
        {
            bool hover = Hover(input, x, y, w, h);
            _ui.Rect(x, y, w, h, on ? Rgba.WithAlpha(UiTheme.ACCENT, hover ? 0.9f : 0.75f) : hover ? Rgba.Hex(0xFFFFFF, 0.1f) : UiTheme.CONTROL);
            _ui.Outline(x, y, w, h, MathF.Max(1f, UiScale), on ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
            _ui.TextCentred(f.Small, x + w * 0.5f, y + h * 0.5f - f.Small.LineHeight * 0.5f, label,
                on ? UiTheme.SCAN_TAG_TEXT : hover ? UiTheme.ACCENT : UiTheme.TEXT, S(0.8f));
            bool clicked = hover && input.LeftPressed;
            if (clicked)
            {
                input.ConsumeClicks();
                Sound.Play(SoundId.UiClick);
            }
            return clicked;
        }

        private static int IndexOfIgnoreCase(List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) { return i; }
            }
            return -1;
        }

        private void RoomDataNotice(string text)
        {
            _roomDataNotice = text;
            _roomDataNoticeUntil = _clock + 4f;
        }

        /// <summary>Typed characters go into the parameter search (Backspace deletes, Ctrl+Backspace clears; Enter ignored).</summary>
        private void TypeIntoRoomParamSearch(InputState input)
        {
            foreach (char c in input.Chars)
            {
                if (c == '\r') { continue; }
                if (c == '\b')
                {
                    if (input.IsDown(Win32.VK_CONTROL)) { _roomParamSearchLength = 0; }
                    else if (_roomParamSearchLength > 0) { _roomParamSearchLength--; }
                    _roomParamMatchesDirty = true;
                }
                else if (c >= ' ' && c != 127 && _roomParamSearchLength < ROOM_SEARCH_MAX)
                {
                    _roomParamSearch[_roomParamSearchLength++] = c;
                    _roomParamMatchesDirty = true;
                }
            }
            if (_roomParamMatchesDirty) { _roomParamScroll.Reset(); }
        }

        /// <summary>The parameter rows (made once), then the matches for the search (every word in the name).</summary>
        private void RefreshRoomParamMatches()
        {
            EnsureRoomCaches();
            _roomParamRows ??= BuildRoomParamRowList();
            if (!_roomParamMatchesDirty) { return; }
            _roomParamMatchesDirty = false;
            string[] words = new string(_roomParamSearch, 0, _roomParamSearchLength).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _roomParamMatches.Clear();
            foreach (RoomParamRow row in _roomParamRows)
            {
                bool all = true;
                foreach (string word in words)
                {
                    if (!row.Search.Contains(word, StringComparison.CurrentCultureIgnoreCase)) { all = false; break; }
                }
                if (all) { _roomParamMatches.Add(row); }
            }
        }

        /// <summary>
        /// One row per parameter name across the rooms: "in 42 of 48 rooms · e.g. 42.10 m²" (" · number" when numeric),
        /// sorted by name.
        /// </summary>
        private List<RoomParamRow> BuildRoomParamRowList()
        {
            var counts = new Dictionary<string, (int Count, string Example, bool Numeric, string Name)>(StringComparer.OrdinalIgnoreCase);
            foreach (RoomInfo room in Scene.Rooms)
            {
                foreach (RoomParameter p in room.Parameters)
                {
                    if (counts.TryGetValue(p.Name, out var entry))
                    {
                        counts[p.Name] = (entry.Count + 1, entry.Example, entry.Numeric || p.IsNumeric, entry.Name);
                    }
                    else
                    {
                        counts[p.Name] = (1, p.Value, p.IsNumeric, p.Name);
                    }
                }
            }
            int total = Scene.Rooms.Length;
            var rows = new List<RoomParamRow>(counts.Count);
            foreach (var entry in counts.Values)
            {
                string example = entry.Example.Length > 40 ? entry.Example[..37] + "…" : entry.Example;
                rows.Add(new RoomParamRow
                {
                    Name = entry.Name,
                    Detail = $"in {entry.Count} of {total} rooms · e.g. {example}{(entry.Numeric ? " · number" : string.Empty)}",
                    Search = entry.Name
                });
            }
            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return rows;
        }

        #endregion
    }
}
