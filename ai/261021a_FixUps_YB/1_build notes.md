# Y-B build notes: the UI shell leftovers

Not compiled here (no .NET SDK). Brackets, strings and char literals script-checked in every changed `.cs` file; the
shell calls (`BeginPanel` / `HeaderButton` / `FooterAction` / `EndPanel`, `BeginDock` / `EndDock`, `BeginScroll` /
`EndScroll`) checked against their signatures.

## What's in
- **Photo mode's panel → a shell dock** (`GameSession.Photo.cs`, `BuildPhotoDock`). Photo mode is a live mode (RMB-drag
  looks), so it takes the dock variant of the shell, not the dimming Side panel: the right-hand column, standard dock
  width, shared header, scrolled body (`_photoDockScroll`), notice strip (saved / failed messages) and footer
  **CLOSE (M / ESC)**. The minimap hides in photo mode, so `DockColumn` starts the dock at the top of the column there.
  Same controls as before (type, size, format, view, exposure, sun, the two grid ticks, TAKE PHOTO, OPEN FOLDER); a short
  window now scrolls the body instead of cutting the panel off.
- **PUSH TO REVIT → the panel shell** (`GameSession.Push.cs`, standard width). Title by state (· PREVIEW / · DONE), the
  target (model · Revit version · phases) as the sub-line. Body: the error text, the checking / pushing line, or the
  summary, the undo note and the report rows in a clipped, pixel-scrolled `ScrollRegion` (was a row-index scroll with
  "wheel to scroll"); the conflicts tick sits under the list. Footer actions per state: TRY AGAIN · CANCEL · PUSH n EDITS
  + CANCEL · SAVE + EXPORT REPORT…; ← BACK and CLOSE as on every panel (while a real push is in flight they do nothing:
  its answer must be recorded, as before). A hint line per state.
- **Markup editor → the panel shell** (`GameSession.Markup.cs`, wide). The comment's text in the sub-line; **UNDO /
  REDO / CLEAR** in the header (faded when there's nothing to do); tools · colours · thickness on the body's first row
  (wrapping on narrow windows); the picture fitted into the rest of the body. Footer: **← CANCEL** (or **← SKIP** straight
  after a new comment's picture, nothing drawn), **SAVE**, **CLOSE** (cancels and goes back to the walkthrough). The
  markup editor joined `AnyBigPanelOpen`, and `PanelBack` closes it first (`CancelMarkupPanel`), so ← BACK and the
  gamepad's B behave as Esc does. Keys unchanged (Ctrl+Z / Ctrl+Y, tool letters, 1–5 colours, Enter saves, Esc cancels).
- **The focus ring stays in an open dropdown / confirm** (`GameSession.PadFocus.cs`, `GameSession.Overlays.cs`). The
  controls drawn by `DrawOverlays` register as overlay targets; while last frame had any, the D-pad only moves between
  them (nearest, next, the first press). Opening a list with the ring showing moves the ring to the current choice (else
  the row nearest the cursor); when the list closes the ring goes back to the button that opened it. Works for the
  panels' dropdowns and confirms and for the clash dock's.
- **K beside PICK POINTS**: TOOLS ▾ shows "PICK POINTS (K)" with the current binding (rebuilt when it changes; left off
  when unbound).
- **README:** §5 photo mode and the panel shell / gamepad paragraph; §8 open build, the UI shell and clash TOOLS lines
  removed; Round Y list; §9 row.

## What isn't
- The dock's footer hint is left out in photo mode (the CLOSE label is long); the "how to use" line is the body's first line.
- The markup picture is smaller than before (it sits inside the wide card instead of the whole window).
- A list longer than a dropdown shows scrolls by D-pad down at its last row (the wheel injection), as before.

## How to check, step by step
1. **Build:** Core tests pass (nothing in Core changed).
2. **Photo mode (M):** the panel is a dock at the top right (no minimap gap), PHOTO MODE header, the use line, the
   controls, TAKE PHOTO, OPEN FOLDER; footer CLOSE (M / ESC). Take a still: "Saved …" in the notice strip above the
   footer. Window-size check 720p (Esc → Appearance, then M): the body scrolls, nothing is cut off. 360°: fewer rows,
   the dock shortens.
3. **PUSH TO REVIT** (a .bimgo with edits): with Revit closed: the error in the body, TRY AGAIN in the footer, ← BACK to
   the pause menu. With the model open in Revit (Go, answer No): "Checking…" then the preview: summary, rows scroll with
   the wheel / thumb, a conflict shows the tick under the list (ticking re-checks), PUSH n EDITS. Done: SAVE, EXPORT
   REPORT…, CLOSE back to the walkthrough. 720p: it fits.
4. **Markup:** add a comment (picture): the editor opens on the shell: the comment's text in the sub-line, UNDO / REDO /
   CLEAR faded, ← SKIP. Draw an arrow: UNDO lights, ← SKIP becomes ← CANCEL, SAVE lights. SAVE: back to the walkthrough
   with the toast. From COMMENTS → OPEN → MARKUP…: ← CANCEL returns to the comment; CLOSE goes to the walkthrough. Esc
   and the gamepad's B cancel. Text tool: click, type, Enter. 720p: the toolbar wraps if it must; the picture fits.
5. **Focus trap (gamepad):** in a panel with a dropdown (CLASHES → a test's SET A, or the clash dock's STATUS), D-pad to
   the dropdown, A: the ring jumps to the current choice; D-pad up / down stays in the list (it can't reach the panel
   behind); A picks; the ring is back on the dropdown button. Open again, B: the list closes. A confirm (e.g. RE-RUN):
   the D-pad moves only between its buttons and tick.
6. **K beside PICK POINTS:** clash mode → TOOLS ▾: "PICK POINTS (K)". Rebind it in CONTROLS: the label follows; unbind
   it: plain "PICK POINTS".
