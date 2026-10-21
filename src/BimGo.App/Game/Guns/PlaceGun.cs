using System.Numerics;
using BimGo.Audio;
using BimGo.Edits;
using BimGo.Physics;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Guns namespace
namespace BimGo.Game.Guns
{
    /// <summary>
    /// Gun 9 (next round): places new instances of loadable family types from the family library.
    /// <list type="bullet">
    /// <item>Not holding anything: LMB opens the library (pause menu panel); RMB places the last picked type again.</item>
    /// <item>A picked type appears in front of the player, resting on the surface below, in the gizmo's move mode
    /// (the Clone gun's controls: WASD · E / Q · R rotate · G snap · Z / X steps · F drop to surface).</item>
    /// <item>RMB commits: Revit places the type (live session, one undo step) or the file's journal records a
    /// <c>place</c>; Esc discards.</item>
    /// </list>
    /// A placement is a clone of the type's hidden template, so once committed every other gun treats it like a
    /// clone (Gizmo moves it, Clone copies it, Demolish deletes it).
    /// <para>Round X-B: ENTOURAGE mode (the wheel switches; the default without a family library): billboards. RMB plants
    /// the type picked in the ENTOURAGE panel where you aim (or swaps the aimed billboard to it); LMB on a billboard
    /// removes it, else opens the panel; Z / X shrink / grow the aimed one, R mirrors it.</para>
    /// </summary>
    internal sealed class PlaceGun : Gun
    {
        /// <summary>How far in front of the player a new placement appears (m, plus half its plan size).</summary>
        private const float PLACE_DISTANCE = 1.6f;

        private readonly GizmoController _gizmo;
        private LibraryEntry _entry, _last;

        // Round X-B: billboards instead of families, and the billboard under the crosshair
        private bool _entourage;
        private Format.EntourageItem _aimed;
        private bool _entourageChosen;

        // Round X-B3: a live resize (Alt + wheel, or Z / X held): the billboard, its height so far, how long Z / X is held
        private Format.EntourageItem _resizing;
        private float _resizeHeight, _stepHeld;

        /// <summary>Round X-B3: one wheel notch scales the aimed billboard by this much.</summary>
        private const float WHEEL_SCALE = 1.03f;

        /// <summary>Round X-B3: Z / X held longer than this (s) scale smoothly, at <see cref="HOLD_SCALE_RATE"/> a second.</summary>
        private const float HOLD_SCALE_DELAY = 0.35f, HOLD_SCALE_RATE = 1.6f;

        public PlaceGun(GameSession session) : base(session)
        {
            _gizmo = new GizmoController(session);
        }

        public override string Name => "PLACE";
        public override string HintPrimary => _gizmo.Active ? "Esc discard" : _entourage ? (_aimed != null ? "Remove it" : "Entourage panel") : "Family library";
        public override string HintSecondary => _gizmo.Active ? (Session.EditsGoToRevit ? "Commit to Revit" : "Commit")
            : _entourage ? (_aimed != null ? "Swap to the picked type" : "Plant") : _last != null ? "Place again" : "—";

        /// <summary>Round X-B: true in ENTOURAGE mode (billboards).</summary>
        public bool EntourageMode => _entourage || (!_entourageChosen && !Session.HasLibrary);

        /// <summary>Round X-B: switches to (or from) ENTOURAGE mode.</summary>
        public void SetEntourageMode(bool on)
        {
            EndResize();
            if (_gizmo.Active) { OnCancel(); }
            _entourage = on;
            _entourageChosen = true;
            _aimed = null;
        }

        /// <summary>Round X-B: the wheel: families ↔ entourage.</summary>
        public override bool CycleMode(int direction)
        {
            if (_gizmo.Active) { return false; }
            SetEntourageMode(!EntourageMode);
            Session.Sound.Play(SoundId.UiClick);
            Session.Toast(EntourageMode
                ? $"Place gun: ENTOURAGE ({Session.EntourageTypeName}; {Platform.Hotkeys.Label(InputAction.GunPrimary)} away from billboards picks another)"
                : Session.HasLibrary ? "Place gun: FAMILIES from the family library" : "Place gun: FAMILIES (this snapshot has no family library)", 2.6f);
            return true;
        }
        /// <summary>
        /// Round X-B3: the wheel with the resize modifier held (Alt by default) scales the aimed billboard 3 % a notch,
        /// live; it saves when the modifier is let go. Held without a billboard aimed, the wheel does nothing (rather
        /// than switching modes).
        /// </summary>
        public override bool OnWheel(Platform.InputState input)
        {
            if (!EntourageMode || _gizmo.Active || !input.Down(InputAction.HoldScale)) { return false; }
            Format.EntourageItem item = _resizing ?? _aimed;
            if (item == null)
            {
                Session.Toast($"Aim at a billboard: {Platform.Hotkeys.Label(InputAction.HoldScale)} + wheel resizes it", 1.6f);
                return true;
            }
            BeginResize(item);
            _resizeHeight = Session.PreviewBillboardHeight(item, _resizeHeight * MathF.Pow(WHEEL_SCALE, input.Wheel));
            return true;
        }

        private void BeginResize(Format.EntourageItem item)
        {
            if (_resizing == item) { return; }
            EndResize();
            _resizing = item;
            _resizeHeight = item.Height;
        }

        /// <summary>Round X-B3: saves a live resize (once, on release).</summary>
        private void EndResize()
        {
            if (_resizing == null) { return; }
            Format.EntourageItem item = _resizing;
            _resizing = null;
            Session.CommitBillboardHeight(item, _resizeHeight);
        }

        public override uint Colour => UiTheme.PLACE;
        public override float PanelHeight => 150f;
        public override bool CapturesInput => _gizmo.Active;

        public override void DrawIcon(UiBatch ui, float cx, float cy, float size, uint colour) => GunIcons.Place(ui, cx, cy, size, colour);

        public override void ClearMarkers() { }

        public override void OnKeys(Platform.InputState input)
        {
            if (EntourageMode && !_gizmo.Active)
            {
                // Round X-B: the aimed billboard: Z / X size, R mirror
                if (_aimed == null) { return; }
                if (_resizing != null && (input.Pressed(InputAction.HoldStepDown) || input.Pressed(InputAction.HoldStepUp))) { EndResize(); } // X-B3: save the live size first
                if (input.Pressed(InputAction.HoldStepDown)) { Session.ResizeBillboard(_aimed, 1f / 1.1f); }
                if (input.Pressed(InputAction.HoldStepUp)) { Session.ResizeBillboard(_aimed, 1.1f); }
                if (input.Pressed(InputAction.HoldRotate)) { Session.FlipBillboard(_aimed); }
                return;
            }
            GizmoPanel.HandleKeys(Session, _gizmo, input);
        }

        #region Input

        public override void Update(float dt, in AimInfo aim)
        {
            if (_gizmo.Active) { _gizmo.Update(dt, Session.Input); }
            bool entourage = EntourageMode && !_gizmo.Active;

            // Round X-B3: Z / X held scale smoothly (a tap still steps 10 %); the resize saves once nothing holds it
            Platform.InputState input = Session.Input;
            int direction = entourage ? (input.Down(InputAction.HoldStepUp) ? 1 : 0) - (input.Down(InputAction.HoldStepDown) ? 1 : 0) : 0;
            _stepHeld = direction != 0 ? _stepHeld + dt : 0f;
            Format.EntourageItem target = _resizing ?? _aimed;
            if (_stepHeld > HOLD_SCALE_DELAY && target != null)
            {
                BeginResize(target);
                _resizeHeight = Session.PreviewBillboardHeight(target, _resizeHeight * MathF.Pow(HOLD_SCALE_RATE, direction * dt));
            }
            if (_resizing != null && (!entourage || (direction == 0 && !input.Down(InputAction.HoldScale)))) { EndResize(); }

            // The aimed billboard (kept while resizing, so the aim drifting off it doesn't switch)
            _aimed = !entourage ? null : _resizing ?? Session.PickBillboard(aim.Origin, aim.Direction, aim.HasHit ? aim.Hit.Distance : 250f);
        }

        public override void OnDeselect()
        {
            EndResize();
            _aimed = null;
        }

        public override void OnPrimary(in AimInfo aim)
        {
            if (_gizmo.Active) { return; }
            if (EntourageMode)
            {
                if (_aimed != null) { Session.RemoveBillboard(_aimed); }
                else { Session.OpenEntourage(); }
                _aimed = null;
                return;
            }
            Session.OpenLibrary();
        }

        public override void OnSecondary(in AimInfo aim)
        {
            if (_gizmo.Active)
            {
                Commit();
                return;
            }
            if (EntourageMode)
            {
                if (_aimed != null) { Session.SwapBillboard(_aimed); }
                else { Session.PlantBillboard(aim); }
                return;
            }
            if (_last != null) { Begin(_last); }
            else
            {
                Session.Sound.Play(SoundId.Error);
                Session.Toast("Pick a family first: LMB opens the family library");
            }
        }

        public override void OnCancel()
        {
            DynamicInstance placed = _gizmo.End();
            if (placed != null) { Session.Dynamics.Remove(placed); }
            _entry = null;
            Session.Sound.Play(SoundId.Remove);
            Session.Toast("Placement discarded");
        }

        #endregion

        #region Placing

        /// <summary>
        /// Makes a new instance of a library type in front of the player, drops it onto the surface below and locks
        /// the gizmo on in move mode. Discards a placement still being held.
        /// </summary>
        public void Begin(LibraryEntry entry)
        {
            if (entry == null) { return; }
            _entourage = false; // a family picked from the library: FAMILIES mode
            _entourageChosen = true;
            if (!entry.Placeable || entry.Element < 0)
            {
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{entry.Label}: {entry.Reason ?? "not placeable"}", important: true);
                return;
            }
            if (_gizmo.Active) { OnCancel(); }

            DynamicInstance placed = Session.CreatePlacement(entry);
            if (placed == null)
            {
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{entry.Label} has no geometry in this snapshot", important: true);
                return;
            }

            // In front of the player at foot level, far enough that its footprint clears the player
            Aabb template = Session.Scene.Elements[entry.Element].Bounds;
            float reach = PLACE_DISTANCE + 0.5f * MathF.Max(template.Size.X, template.Size.Y);
            Vector3 forward = Session.Camera.Forward;
            var flat = new Vector3(forward.X, forward.Y, 0f);
            flat = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitX;
            Vector3 feet = Session.Player.Feet;
            Vector3 pivotToBase = placed.BasePivot - template.Min; // the template's pivot relative to its box bottom
            var target = new Vector3(feet.X + flat.X * reach, feet.Y + flat.Y * reach, feet.Z + MathF.Max(0f, pivotToBase.Z));
            Session.SetPlacement(placed, target, 0f);

            _entry = entry;
            _last = entry;
            _gizmo.Begin(placed);
            _gizmo.DropToSurface(out _); // quietly onto the floor (or desk) in front; F does it again after moving
            Session.Sound.Play(SoundId.Grab);
            Session.Toast($"{entry.Label}: move it, then {Platform.Hotkeys.Label(InputAction.GunSecondary)} commits it (Esc discards it)");
        }

        /// <summary>
        /// Keeps the placement and asks the model source to create it: Revit places the type at the instance's
        /// pivot, turned by its angle; a file records a <c>place</c> entry.
        /// </summary>
        private void Commit()
        {
            LibraryEntry entry = _entry;
            DynamicInstance placed = _gizmo.End();
            _entry = null;
            if (placed == null || entry == null) { return; }
            placed.Committed = true;

            Session.Sound.Play(SoundId.Commit);
            var request = new EditRequest
            {
                Op = EditOp.Place,
                NewCloneKey = placed.CloneKey,
                TypeUniqueId = entry.TypeUniqueId,
                TypeId = entry.TypeId,
                Pivot = Session.ToRevit(placed.Pivot),
                Angle = placed.Angle,
                Label = "Place " + entry.Label
            };

            bool sent = Session.SubmitEdit(request, result =>
            {
                if (result.Success)
                {
                    if (result.NewElementId > 0)
                    {
                        placed.RevitId = result.NewElementId;
                        Session.Toast($"Placed in Revit: {entry.Label} (id {result.NewElementId})");
                    }
                    else
                    {
                        Session.Toast($"Placed: {entry.Label}");
                    }
                    return;
                }

                Session.Dynamics.Remove(placed);
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{Session.EditTargetName} refused the placement ({result.Message}). Removed.", 4f, important: true);
            });

            if (!sent) { Session.Toast($"{entry.Label} kept in the walkthrough only (not connected to Revit)"); }
        }

        #endregion

        #region Drawing

        public override void CollectHighlights(List<Highlight> highlights)
        {
            if (_gizmo.Active) { highlights.Add(new Highlight(_gizmo.Target.Element, _gizmo.Target.Id, UiTheme.PLACE, 0.3f)); }
        }

        public override void DrawWorld(Overlay3D overlay, bool selected)
        {
            if (selected) { _gizmo.Draw(overlay, UiTheme.PLACE); }
            if (selected && _aimed != null) { Session.DrawBillboardFrame(overlay, _aimed, UiTheme.PLACE); } // X-B
        }

        public override void DrawPanel(UiBatch ui, float x, float y, float width)
        {
            if (_gizmo.Active)
            {
                GizmoPanel.Draw(ui, Session, _gizmo, "PLACE", UiTheme.PLACE_LABEL, -1, x, y, width, Session.UiScale);
                return;
            }

            FontAtlas f = ui.Atlas;
            float s(float value) => value * Session.UiScale;
            if (EntourageMode)
            {
                // Round X-B: billboards
                ui.Text(f.Small, x, y, "PLACE · ENTOURAGE", UiTheme.PLACE_LABEL, s(1.1f));
                y += s(20);
                ui.TextWrapped(f.Body, x, y, width, Session.EntourageTypeName, UiTheme.PLACE_LABEL, maxLines: 1);
                y += s(20);
                ui.TextWrapped(f.Body, x, y, width, _aimed != null ? HINT_AIMED.Text : HINT_PLANT.Text, UiTheme.TEXT_SOFT, maxLines: 2);
                y += s(38);
                if (_aimed != null)
                {
                    // X-B3: its height (live while resizing)
                    TextBuffer size = Session.Text.Clear().Append(_resizing != null ? "Resizing: " : "Aimed: ").AppendLength(_aimed.Height, 2)
                        .Append(" high");
                    if (_resizing == null) { size.Append(" · ").Append(HINT_RESIZE.Text); }
                    ui.TextWrapped(f.Small, x, y, width, size.Span, _resizing != null ? UiTheme.PLACE_LABEL : UiTheme.TEXT_MUTED, maxLines: 1);
                    y += s(19);
                }
                if (_aimed?.Slot != null)
                {
                    // X-B2: a billboard standing in for a Revit instance (its changes are one-offs for this instance)
                    TextBuffer from = Session.Text.Clear().Append("From Revit: ").Append(_aimed.Slot.FamilyType);
                    ui.TextWrapped(f.Small, x, y, width, from.Span, UiTheme.PLACE_LABEL, maxLines: 1);
                    return;
                }
                if (_aimed != null) { return; }
                TextBuffer placed = Session.Text.Clear().AppendGrouped(Session.EntourageCount).Append(" placed · ").Append(HINT_WHEEL.Text);
                ui.TextWrapped(f.Small, x, y, width, placed.Span, UiTheme.TEXT_MUTED, maxLines: 1);
                return;
            }
            ui.Text(f.Small, x, y, "PLACE", UiTheme.PLACE_LABEL, s(1.1f));
            y += s(20);
            if (!Session.HasLibrary)
            {
                ui.TextWrapped(f.Body, x, y, width, "No family library in this snapshot.", UiTheme.TEXT_MUTED, maxLines: 1);
                y += s(19);
                ui.TextWrapped(f.Body, x, y, width, "Revit: Options → Load → Family library, then Go.", UiTheme.TEXT_MUTED, maxLines: 2);
                return;
            }

            TextBuffer count = Session.Text.Clear().AppendGrouped(Session.Library.PlaceableCount).Append(" family types to place");
            ui.Text(f.Body, x, y, count.Span, UiTheme.TEXT);
            y += s(22);
            ui.Text(f.Body, x, y, "LMB opens the family library", UiTheme.TEXT_SOFT);
            y += s(20);
            if (_last != null)
            {
                ui.TextWrapped(f.Body, x, y, width, _last.Label, UiTheme.PLACE_LABEL, maxLines: 1);
                y += s(19);
                ui.Text(f.Body, x, y, "RMB places it again", UiTheme.TEXT_MUTED);
                y += s(19);
            }
            if (Session.EditsLocalOnly) { ui.Text(f.Body, x, y, "Not connected to Revit: walkthrough only", UiTheme.DANGER); }
        }

        #endregion

        private static readonly Platform.KeyHint HINT_PLANT = new(() =>
            $"{Platform.Hotkeys.Label(InputAction.GunSecondary)} plants it where you aim · {Platform.Hotkeys.Label(InputAction.GunPrimary)} picks another");
        private static readonly Platform.KeyHint HINT_AIMED = new(() =>
            $"{Platform.Hotkeys.Label(InputAction.GunPrimary)} removes it · {Platform.Hotkeys.Label(InputAction.GunSecondary)} swaps it · {Platform.Hotkeys.Map.DescribeJoined("/", false, InputAction.HoldStepDown, InputAction.HoldStepUp)} size · {Platform.Hotkeys.Label(InputAction.HoldRotate)} mirror");
        private static readonly Platform.KeyHint HINT_RESIZE = new(() => $"{Platform.Hotkeys.Label(InputAction.HoldScale)}+wheel resizes");
        private static readonly Platform.KeyHint HINT_WHEEL = new(() => $"{Platform.Hotkeys.Label(InputAction.GunModeNext)}: families");
    }
}
