# BimGo: handoff for Round Z after the Revit round-trip (Z-A, Z-B confirmed): next, presentation (kiosk) then installer

**Context:** BimGo is Gavin's personal project: publisher **Aussie BIM Guru**, licence **MIT-0**; C# / Visual Studio,
.NET 8; its own OpenGL renderer over Silk.NET (GL 3.3 core); a Revit add-in (`BimGo.Revit`, Revit 2025 / 2026 / 2027), a
standalone app (`BimGo.App`) and `BimGo.Core`.

How Gavin works (unchanged; see `claude/BimGo_RoundY_YE_Handoff.md` for the full list):
- **Rounds:** discuss → AskUserQuestion (recommended option first and marked, at most 4 questions; follow typed answers)
  → build → zip. He may say "hand it over": then write a handoff instead of building.
- **Notes:** each build gets `ai/<yymmdd><letter>_<topic>/` (`0_brief.md`, `1_build notes.md`), a README §8 entry while
  open (dropped when confirmed), a §9 row (last cell inside the closing `|`), and a project doc
  `claude/BimGo_<Topic>_BuildNotes.md`. Zip from the repo root (LICENSE, README.md, THIRD-PARTY-NOTICES.txt, ai, src,
  tests, tools) to `/mnt/user-data/outputs/BimGo_<Topic>.zip`, send with SendUserFile.
- **Small fixes: tell him the edit** (file, line, before / after) instead of a new zip; record it as a revision in the
  build notes and keep the local copy in step.
- **Ask whether his working copy changed** before editing (baseline now: the Z-B r6 zip, plus the hand edits below,
  which are all in it).

## 1. Where things are (2026-10-22)

| Build | Folder / project doc | State |
|---|---|---|
| Z-A room data overlay | `261022c_Rooms_ZA`, `claude/BimGo_Rooms_ZA_BuildNotes.md` | Confirmed (r3) |
| Z-B comment markers in Revit | `261022d_Markers_ZB`, `claude/BimGo_Markers_ZB_BuildNotes.md` | Confirmed (r6) |
| Marker family spec | `261022c_Rooms_ZA/2_marker_family_spec.md`, `claude/BimGo_MarkerFamily_Spec.md` | Family received |
| This handoff | `ai/261022e_Handoff/0_handoff.md`, `claude/BimGo_RoundZ_ZC_Handoff.md` | |

README §8: no open build. Async sharing through cloud folders is **shelved until after v1** (it changes the
single-player feel); don't propose it again before then.

## 2. Round Z so far (read before touching these areas)
- **Z-A room data overlay:**
  - Extraction keeps each room's UniqueId, ElementId, level, Revit area and every non-empty instance parameter (text
    plus a number in display units). Stored in `rooms.json`; `model.json` rooms gained uniqueId / id / level / area.
  - Floating tags on **Shift+N**: N was taken by the Measure gun's normal, which failed the controls tests. Every
    unmodified letter is used somewhere, so new keys need a modifier.
  - Tags sit at the room's most open point (`RoomTagPlacement`, Core `Scene/RoomData.cs`).
  - Target check: value vs target parameter, ± / at least / at most, tolerance %.
  - Set up on FIND ROOM → ROOM DATA; settings saved in `visibility.json` `rooms`.
  - r2: the class was `RoomLabel`, which clashed with `GameSession.RoomLabel(int)`.
- **Z-B comment markers:**
  - Gavin's family "BimGo Comment Marker" (adaptive, one point, three status spheres, type "Default") is embedded in
    the add-in (`src/BimGo.Revit/Resources/Families`). It's written to `%LocalAppData%\BimGo\Families`, compared byte
    for byte: Revit files grow in whole blocks, so a length check missed a re-saved family.
  - 18 shared parameters, all USERMODIFIABLE 1: the API can't set non-user-modifiable ones. Six were re-issued with new
    GUIDs. `tools/revit/BimGo_SharedParameters.txt`; `MarkerFamily` GUIDs are checked by a test.
  - **Markers are matched by a tag in extensible storage on each instance** (schema "BimGoCommentMarker", field
    CommentId), BimGo_Id second. On Gavin's family, `Parameter.Set` returned true but read back empty straight after;
    after a `doc.Regenerate()` the values fill. The ids BimGo placed live in a DataStorage (schema
    "BimGoCommentMarkers"), so deletions in Revit stick until "put back".
  - Live: `markers.sync` 1.5 s after comment changes, one "BimGo: comment markers" transaction only when something
    differs. Ribbon → Comment markers (model folder full sync, or a .bimgo add / update only).
  - Options → Player: live on / off, remove closed, family path.

## 3. Next: Z-C kiosk mode (answers agreed 2026-10-22)
- **Launch:** **MAKE KIOSK SHORTCUT** (from a saved .bimgo, file mode) writes a desktop shortcut:
  `BimGo.exe --kiosk "<file>"`. Nothing in the file changes.
  - `Program.ParseArguments` / `FirstFileArgument` (it skips a switch's value: make `--kiosk` a flag without a value, or
    adjust that).
  - `OpenTarget` gains a Kiosk flag, passed through `AppShell` to `GameSession` (options).
  - `AppInstance.ForwardToRunningInstance`: decide what a second kiosk launch does (suggest: open in kiosk mode).
  - Shortcut writing: `Shell/FileAssociation.CreateShortcut` already uses IShellLinkW / IPersistFile. Reuse it, with
    arguments + icon + working directory.
- **Visitors can only walk, fly and look.** No pause menu, guns, edits, panels, tool keys, F1 sheet, saving or
  file-drop.
  - Gate it in `GameSession.UpdateFrame` (a kiosk branch before the normal key handling).
  - Hide the gun bar / HUD clutter; keep a small unobtrusive hint ("WASD · mouse to look · V to fly").
  - Esc does nothing, and closing the window shouldn't prompt to save.
- **Idle reset:** 2 minutes without input → the bookmark tour (`GameSession.Tour.cs`: `StartTour()`, `BeginTour`,
  `StopTour`), or the home view if there are no bookmarks. Any key or mouse move stops it and gives control back,
  continuing from where the tour was.
- **Exit:** **Ctrl+Shift+Q** (held, or pressed) leaves kiosk mode into the normal walkthrough. It's a new
  `InputAction` with a `DefaultsChanges` row (`DEFAULTS_VERSION` 8 → 9), the README §3 row by hand, and a check
  against clashes (`Defaults_HaveNoClashInAnySituation`).
- Probably also: kiosk opens fullscreen, cursor captured, UI scale as saved; logging of start / exit.
- Then **Z-D: live sharp reflections retry** (see `claude/BimGo_Reflections_XC1_BuildNotes.md` and the X-C1 handoff
  for why live was set aside), then **Z-E: installer** (Inno Setup per user; `ai/261009e_Installer`).

Later (unchanged): OpenXR VR; review tools; IFC import; entourage set; Scale part 3 if needed. Parked: real-time
multiplayer. After v1: async sharing through cloud folders.

## 4. Things to watch (adds to the Y-E handoff's §5)
- **Name clashes with `GameSession` members:** `GameSession` is huge. Before adding a Core type used inside it, grep
  for a member of that name (Z-A's `RoomLabel`). Likewise Revit project types (`BimGo.Availability.Document / Family`
  exist in their own namespace).
- **Keys:** list every default across all contexts (gun contexts too) before choosing one; unmodified letters are all
  taken.
- **Revit API:** non-user-modifiable shared parameters can't be set; values set on a new adaptive instance may read
  back empty until `Regenerate()`; `Autodesk.Revit.Exceptions.FileArgumentException` doesn't exist.
- **Compiling:** still no .NET SDK in the sandbox; script-check brackets / strings / char literals, XAML / resx /
  csproj as XML, UI strings against the font atlas, shaders in headless WebGL2 (see Y-E §5).
