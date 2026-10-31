using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round Y-C2: one fixture's own light profile. Aim at a lighting fixture and press Ctrl+K (<see cref="InputAction.FixtureProfile"/>):
    /// a dock shows that fixture's profile (its own, or its type's), ‹ › through the same choices as the LIGHTS tab,
    /// IMPORT .IES…, RESET (back to the type's), and the profile's polar plot. The fixture is highlighted while the dock
    /// is open; the player stands still (like the grids panel).
    /// <list type="bullet">
    /// <item>The choice is kept per fixture in <c>light-profiles.json</c> by UniqueId (after the link instance's in a link),
    /// so the next Go / F5 / Export keeps it.</item>
    /// <item>A clone follows its original (its lights are the original's): aiming at a clone opens the original's dock,
    /// and a change there changes both. After F5 a committed clone is a fixture of its own (its type's profile).</item>
    /// <item>Aiming just beside a small fixture (a downlight flush with the ceiling) takes the nearest fixture within
    /// <see cref="FIXTURE_NEAR"/>.</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>How far from the aimed point a fixture's light may be and still count as aimed at (m).</summary>
        private const float FIXTURE_NEAR = 0.75f;

        private bool _fixtureDockOpen;
        private int _fixtureElement = -1;   // the fixture (an element with lights)
        private int _fixtureDynamicId;      // its moved instance or the aimed clone (the highlight), 0 = where it stands
        private bool _fixtureIsClone;       // opened from a clone (it follows the original)
        private string _fixtureKey;         // its key in light-profiles.json (null: no identity)
        private Vector4 _fixtureDockRect;
        private readonly ScrollRegion _fixtureDockScroll = new();

        #endregion

        #region Opening and closing

        /// <summary>Ctrl+K while walking: the aimed fixture's dock (again: closes it).</summary>
        private void ToggleFixtureDock()
        {
            if (_fixtureDockOpen) { CloseFixtureDock(); }
            else { OpenFixtureDock(); }
        }

        /// <summary>
        /// Opens the dock for the fixture under the crosshair (or the nearest one beside the aimed point); says why not
        /// when there is none.
        /// </summary>
        private void OpenFixtureDock()
        {
            CloseLightingReview();
            if (Scene.Lighting.Lights.Length == 0)
            {
                Sound.Play(SoundId.Error);
                Toast("This model has no lighting fixtures with lights");
                return;
            }
            if (!AimedFixture(out int element, out int dynamicId, out bool clone))
            {
                Sound.Play(SoundId.Error);
                Toast($"Aim at a lighting fixture, then {Key(InputAction.FixtureProfile)} for its own light profile", 3f);
                return;
            }

            // One panel at a time (like the grids panel)
            CloseGridsPanel();
            CloseSunPanel();
            CloseSunHours();
            CloseSectionEditor();
            ClosePhotoMode();
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            ShowUi();

            _fixtureDockOpen = true;
            _fixtureElement = element;
            _fixtureDynamicId = dynamicId;
            _fixtureIsClone = clone;
            _fixtureKey = LightProfileDocument.InstanceKeyOf(Scene, element);
            _lightNotice = null;
            _fixtureDockScroll.Reset();
            Sound.Play(SoundId.UiClick);
            ElementRecord record = Scene.Elements[element];
            Utilities.Log_Utils.Write($"Fixture profile: {record.FamilyType} (id {record.ElementId}, key {_fixtureKey ?? "none"}{(clone ? ", from a clone" : string.Empty)}).");
        }

        private void CloseFixtureDock()
        {
            if (!_fixtureDockOpen) { return; }
            _fixtureDockOpen = false;
            _fixtureDockRect = default;
            _fixtureElement = -1;
            _fixtureDynamicId = 0;
            _window.Input.ReleaseAll();
            if (!_paused && _window.IsActive && !CursorModeOpen) { _window.SetCaptured(true); }
        }

        /// <summary>True while the dock has the keys (walking).</summary>
        private bool FixtureDockHasKeys => _fixtureDockOpen;

        /// <summary>Keys while the dock is open: Esc / Ctrl+K close.</summary>
        private void UpdateFixtureDockKeys(InputState input)
        {
            if (input.Pressed(InputAction.Fullscreen)) { _window.ToggleFullscreen(); }
            if (input.Pressed(InputAction.Back) || input.Pressed(InputAction.FixtureProfile)) { CloseFixtureDock(); }
        }

        /// <summary>
        /// The fixture under the crosshair: the aimed element (or the moved / cloned instance's element) when it has
        /// lights, else the visible fixture whose light is nearest the aimed point (within <see cref="FIXTURE_NEAR"/>).
        /// </summary>
        private bool AimedFixture(out int element, out int dynamicId, out bool clone)
        {
            element = -1;
            dynamicId = 0;
            clone = false;
            if (!_aim.HasHit) { return false; }

            int aimed = _aim.Hit.Element;
            DynamicInstance instance = _aim.Hit.DynamicId > 0 ? Dynamics?.Find(_aim.Hit.DynamicId) : null;
            if (instance != null) { aimed = instance.Element; }
            if (aimed >= 0 && _lightOfElement.ContainsKey(aimed))
            {
                element = aimed;
                dynamicId = instance?.Id ?? 0;
                clone = instance != null && instance.IsClone;
                return true;
            }

            // Beside it: the nearest light of a fixture where it stands (moved fixtures: where it was moved to)
            LightSource[] lights = Scene.Lighting.Lights;
            ElementRecord[] elements = Scene.Elements;
            Vector3 point = _aim.Hit.Point;
            float best = FIXTURE_NEAR * FIXTURE_NEAR;
            for (int i = 0; i < lights.Length; i++)
            {
                int e = lights[i].Element;
                if (_userHidden[e] || ProfileHides(e) || !_groupVisible[SceneBatches.GroupOf(elements[e])]) { continue; }
                Vector3 at = LightPosition(i);
                int id = 0;
                if (_hidden[e])
                {
                    DynamicInstance moved = Dynamics?.FindOriginal(e);
                    if (moved == null || !Dynamics.IsActive(moved)) { continue; }
                    at = Vector3.Transform(at, moved.Model);
                    id = moved.Id;
                }
                float d = Vector3.DistanceSquared(at, point);
                if (d >= best) { continue; }
                best = d;
                element = e;
                dynamicId = id;
            }
            return element >= 0;
        }

        #endregion

        #region Choices

        /// <summary>The fixture's type row in the LIGHTS tab (null if it has none).</summary>
        private LightTypeRow FixtureRow()
        {
            if (_fixtureElement < 0) { return null; }
            string type = Scene.Elements[_fixtureElement].FamilyType ?? string.Empty;
            foreach (LightTypeRow row in LightTypeRows())
            {
                if (string.Equals(row.FamilyType, type, StringComparison.Ordinal)) { return row; }
            }
            return null;
        }

        /// <summary>The choice the fixture is on now: its own, else its type's.</summary>
        private string FixtureChoice(LightTypeRow row) => _lightProfileDoc.InstanceChoiceFor(_fixtureKey) ?? CurrentChoice(row);

        /// <summary>‹ / ›: the fixture's previous / next choice (its type's choice clears its own).</summary>
        private void StepFixtureProfile(LightTypeRow row, int direction)
        {
            if (_fixtureKey == null) { return; }
            List<string> options = ProfileOptionsFor(row);
            int at = options.IndexOf(FixtureChoice(row));
            int next = ((at < 0 ? 0 : at + direction) % options.Count + options.Count) % options.Count;
            string choice = options[next];
            _lightProfileDoc.SetInstanceChoice(_fixtureKey, row.FamilyType, choice == CurrentChoice(row) ? null : choice);
            _lightNotice = null;
            SaveLightProfiles();
        }

        /// <summary>The fixture's renderer slot is the lobe though it has a profile (over the slot limit).</summary>
        private bool FixtureOverflows()
        {
            if (!_lightOfElement.TryGetValue(_fixtureElement, out int first) || first >= _lightSlot.Length) { return false; }
            return _lightSlot[first] < 0 && ProfileOf(ProfileKeyOf(first)) != null;
        }

        #endregion

        #region Drawing

        /// <summary>
        /// The dock: the fixture's name, its profile (its own or its type's) with ‹ › · IMPORT .IES… · RESET, the polar
        /// plot, and its type's profile.
        /// </summary>
        private void BuildFixtureDock(FontAtlas f, InputState input)
        {
            if (!_fixtureDockOpen) { return; }
            LightTypeRow row = FixtureRow();
            if (row == null)
            {
                CloseFixtureDock();
                return;
            }
            ElementRecord record = Scene.Elements[_fixtureElement];

            DockFrame d = BeginDock("FIXTURE LIGHT PROFILE", UiTheme.SUN_LABEL, Rgba.WithAlpha(UiTheme.SUN, 0.5f), S(UiTheme.DOCK_WIDTH), _fixtureDockScroll,
                notice: _lightNotice != null);
            _fixtureDockRect = d.Rect;
            float ix = d.Ix, iw = d.Iw, gap = S(6), bw = S(28);
            float y = BeginDockBody(d, _fixtureDockScroll, input);

            // Which fixture
            y += _ui.TextWrapped(f.Body, ix, y, iw, row.FamilyType.Length == 0 ? "(no type)" : row.FamilyType, UiTheme.TEXT, maxLines: 2) + S(4);
            Text.Clear().Append("Id ").Append(record.ElementId);
            if (!string.IsNullOrEmpty(record.LevelName)) { Text.Append(" · ").Append(record.LevelName); }
            LinkInfo link = Scene.LinkOf(record);
            if (link != null) { Text.Append(" · link: ").Append(string.IsNullOrEmpty(link.Title) ? link.Name : link.Title); }
            y += _ui.TextWrapped(f.Small, ix, y, iw, Text.Span, UiTheme.TEXT_MUTED, maxLines: 2) + S(4);
            if (_fixtureIsClone)
            {
                y += _ui.TextWrapped(f.Small, ix, y, iw, "A copy: it follows the original fixture, so a change here changes both.",
                    UiTheme.MEASURE_LABEL, maxLines: 2) + S(4);
            }
            y += S(6);

            // Its profile now
            string own = _lightProfileDoc.InstanceChoiceFor(_fixtureKey);
            string current = own ?? CurrentChoice(row);
            Text.Clear();
            AppendChoiceLabel(row, current);
            Text.Append(own != null ? " (this fixture)" : " (from the type)");
            y += _ui.TextWrapped(f.Small, ix, y, iw, Text.Span, own != null ? UiTheme.PLACE_LABEL : UiTheme.TEXT_MUTED, maxLines: 2) + S(6);

            if (_fixtureKey != null)
            {
                float resetW = S(64);
                float importW = iw - 2f * (bw + gap) - (own != null ? resetW + gap : 0f);
                if (SmallButton(f, input, ix, y, bw, S(26), "‹")) { StepFixtureProfile(row, -1); }
                if (SmallButton(f, input, ix + bw + gap, y, bw, S(26), "›")) { StepFixtureProfile(row, +1); }
                if (SmallButton(f, input, ix + 2f * (bw + gap), y, importW, S(26), "IES…")) { ImportIes(row, _fixtureKey); }
                if (own != null && SmallButton(f, input, ix + iw - resetW, y, resetW, S(26), "RESET"))
                {
                    _lightProfileDoc.SetInstanceChoice(_fixtureKey, row.FamilyType, null);
                    _lightNotice = null;
                    SaveLightProfiles();
                }
                y += S(36);
            }
            else
            {
                y += _ui.TextWrapped(f.Small, ix, y, iw, "This fixture has no Revit identity (a placed family copy): it uses its type's profile.",
                    UiTheme.TEXT_FAINT, maxLines: 3) + S(8);
            }
            if (FixtureOverflows())
            {
                y += _ui.TextWrapped(f.Small, ix, y, iw, $"Over {LightProfiles.MAX_GPU_PROFILES} different profiles in use: this fixture uses the lobe for now.",
                    UiTheme.MEASURE_LABEL, maxLines: 2) + S(6);
            }

            // The polar plot
            float size = MathF.Min(iw, S(220));
            DrawPolarPlot(LightProfileDocument.KeyFor(current, row.RevitKey), ix + (iw - size) * 0.5f, y, size, small: false);
            y += size + S(6);
            float half = iw * 0.5f;
            _ui.Rect(ix, y + S(7), S(14), S(2), UiTheme.ACCENT);
            _ui.Text(f.Small, ix + S(20), y, "C 0–180", UiTheme.TEXT_MUTED);
            _ui.Rect(ix + half, y + S(7), S(14), S(2), UiTheme.TEXT_MUTED);
            _ui.Text(f.Small, ix + half + S(20), y, "C 90–270", UiTheme.TEXT_MUTED);
            y += S(20);
            y += _ui.TextWrapped(f.Small, ix, y, iw, "Straight down at the bottom; rings at 25 / 50 / 75 / 100 % of the peak.",
                UiTheme.TEXT_FAINT, maxLines: 2) + S(10);

            // Its type
            Text.Clear().Append("Its type: ");
            AppendChoiceLabel(row, CurrentChoice(row));
            y += _ui.TextWrapped(f.Small, ix, y, iw, Text.Span, UiTheme.TEXT_MUTED, maxLines: 2) + S(4);
            int others = FixtureOverrides(row.FamilyType);
            Text.Clear().AppendGrouped(row.Count).Append(row.Count == 1 ? " fixture of this type · " : " fixtures of this type · ")
                .AppendGrouped(others).Append(" set individually");
            y += _ui.TextWrapped(f.Small, ix, y, iw, Text.Span, UiTheme.TEXT_FAINT, maxLines: 2) + S(4);
            y += _ui.TextWrapped(f.Small, ix, y, iw, "Types: pause → LIGHTS. Profiles show with Lights → Glow + light.", UiTheme.TEXT_FAINT, maxLines: 2);

            EndDockBody(_fixtureDockScroll, input, y);
            if (EndDock(d, _lightNotice, HINT_FIXTURE_FOOT.Text) == DOCK_CLOSE) { CloseFixtureDock(); }
        }

        /// <summary>The fixture's highlight while the dock is open.</summary>
        private void AddFixtureHighlight(List<Guns.Highlight> highlights)
        {
            if (!_fixtureDockOpen || _fixtureElement < 0) { return; }
            if (_fixtureDynamicId > 0) { highlights.Add(new Guns.Highlight(-1, _fixtureDynamicId, UiTheme.SUN, 0.3f)); }
            else if (!_hidden[_fixtureElement]) { highlights.Add(new Guns.Highlight(_fixtureElement, 0, UiTheme.SUN, 0.3f)); }
        }

        #endregion
    }
}
