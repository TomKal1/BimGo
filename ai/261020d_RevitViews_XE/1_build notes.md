# X-E build notes: Revit views from BimGo's camera (r2)

**r2 replaces r1.** r1 was a two-way live camera sync (modes off / BimGo → Revit / Revit → BimGo / both, 4 Hz, a
loop guard, a "BimGo view" moved with every step). Every BimGo → Revit move was a Revit transaction and so an undo
entry, which Gavin found brutal; r2 drops all live sync (r1's `CameraSync` code, `view.sync` / `view.camera`, the
Camera ribbon button and the follow / fly logic are gone) for two one-shot actions.

Not compiled here (no .NET SDK, no Revit API). Brackets, strings, char literals and UI glyphs were script-checked in
every changed `.cs` file; new member names were checked across the `GameSession` partials.

## What's in
- **Core (`Live/RevitViews.cs`, new; `LiveProtocol.cs`; `LiveSessionSource.cs`):** `RevitViewAction` (`create` /
  `update`); `RevitViews.NextViewName` (first "BimGo NN" past the highest number in use, case-insensitive, two digits
  at least, gaps not refilled) and `LevelUp` (no-roll up vector); `RevitViewPayload` (action, `askedInRevit`, eye /
  forward / up in Revit internal metres, horizontal FOV, aspect; `IsValid`); `RevitViewRequestPayload`. Messages
  `view.make` (app → Revit), `view.result` (Revit → app, `MessagePayload`, shown as a toast through the notices) and
  `view.request` (Revit → app: the ribbon asks for the camera). `hello.ack` gains `revitViews: true`. All additive
  (protocol 1); older sides ignore the new types. `ILiveLink`: `RevitViewsSupported`, `MakeRevitView`,
  `TryTakeViewRequest`.
- **App (`GameSession.RevitViews.cs`, new):** `SendRevitView(action)` sends the rendered camera (a playing tour or
  path counts), the FOV and the window's aspect; toasts "Making a Revit view from here…" / "Updating Revit's active view
  to here…", then Revit's answer. `UpdateRevitViews()` (with the host work each frame) answers Revit's ribbon requests.
  Keys **Shift+F5** (`RevitViewNew`) and **Alt+F5** (`RevitViewUpdate`) (`DEFAULTS_VERSION` 5, two `DefaultsChanges`
  rows; live walking and in the pause menu, one F1 row there). Pause menu, live sessions only, under SAVE AS .BIMGO…:
  **NEW REVIT VIEW HERE** and **UPDATE REVIT VIEW**. An older add-in: "need the BimGo add-in from build X-E or later".
- **Revit (`LiveDispatcher.cs`):** `view.make` → `MakeView` in one transaction:
  - create ("BimGo: new view"): `View3D.CreatePerspective` with the first 3D view type, no view template, named by
    `NextViewName` over every view's name (if the name is refused, Revit's own name stays; logged);
  - update ("BimGo: update view"): the active view, which must be a perspective `View3D` (not a template, not locked)
    in the session's model; otherwise a message saying to open one;
  - both: `SetOrientation(eye, up, forward)` and `SetPerspectiveFov` (below). Then a new view is made active (if the
    session's model is the active document; BimGo keeps the focus) and an updated one is redrawn. `view.result` says
    "New Revit view “BimGo 03” from here" / "Updated “…” in Revit to this view" or why not; a failure asked from Revit
    is also shown in a Revit dialog. `DocumentChanged` already ignores `BimGo:` transactions.
  - `RequestView(host, action)` sends `view.request` (the ribbon commands).
  - **FOV on the crop:** the crop box is in view coordinates; the rectangle's half width over `−CropBox.Max.Z` is taken
    as tan(fov / 2) (Revit's docs: the perspective crop box is a slice of a pyramid with its apex at the eye); the
    height follows BimGo's aspect; centred on the axis when the crop box's origin is the eye. Every time it logs
    "View from BimGo FOV: …" (BimGo's FOV and aspect, the crop before and its FOV by this rule, the distance, origin at
    eye, the crop after and its FOV).
- **Ribbon:** **New view from BimGo** (`Cmd_NewView`) and **Update view from BimGo** (`Cmd_UpdateView`, which first checks
  the active view is a perspective 3D view) beside Live, with icons (`BimGo_NewView16/32`, `BimGo_UpdateView16/32`)
  and tooltips. No session / BimGo not connected: a message.
- **Tests:** `RevitViewTests` (names, no-roll up, payload checks, the three messages and hello.ack with / without
  `revitViews` through a channel pair). `ControlsTests` cover the two new actions. README §3 rows added by hand to match
  the generator.
- **README:** §3 rows, §4 files, AI note 6 (the views BimGo may make / change in the model), §7 protocol rows, §8
  to-verify, §9 row.

## What isn't
- No live following either way (r1, dropped).
- Update needs the view open (active) in Revit; it doesn't pick "the last view BimGo made".
- No section box, no roll; the new view's other settings (detail level, visual style…) are the 3D view type's
  defaults.
- If the BimGo window is minimised, a request from Revit's ribbon waits until it's restored (the loop skips host work
  while minimised).
- The crop's FOV rule is from the docs, to confirm with the log line.

## How to check, step by step
1. **Build:** Core tests pass (`RevitViewTests` new; `ControlsTests` with the new actions). Revit's BimGo tab shows
   **New view from BimGo** and **Update view from BimGo** after Live, with icons and tooltips.
2. **New view from BimGo:** Go; stand somewhere in BimGo; **Shift+F5** (or Esc → NEW REVIT VIEW HERE). Toast
   "New Revit view “BimGo 01” from here"; Revit opens the perspective view **BimGo 01** behind BimGo. Side by side:
   same eye, direction and width of view. Do it again elsewhere: **BimGo 02**. Revit's undo list: one "BimGo: new view"
   per view.
3. **Update:** in Revit open BimGo 01 (or any perspective view); move in BimGo; **Alt+F5** (or UPDATE REVIT VIEW).
   The view jumps to BimGo's camera ("Updated “BimGo 01” in Revit to this view"; one "BimGo: update view"). With a plan
   or isometric view active: the toast says to open a perspective 3D view.
4. **From Revit:** the two ribbon buttons do the same with wherever BimGo's camera is (BimGo toasts the result). With
   no session, or BimGo not connected: a message in Revit. Update with a plan active: Revit says so first.
5. **FOV:** if the widths differ, send me the "View from BimGo FOV" log line.

## APIs to verify (no Revit here)
`View3D.CreatePerspective`, `View3D.SetOrientation`, `View3D.IsPerspective`, `View.IsLocked`, `View.CropBox` (get / set
on a perspective view), `UIDocument.ActiveView` (set), `UIDocument.RefreshActiveView`, `Document.IsReadOnly`.
