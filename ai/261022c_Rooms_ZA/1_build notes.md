# Z-A build notes: room data overlay

Not compiled here (no .NET SDK in the sandbox). Brackets, strings and char literals script-checked in every changed
`.cs` file, UI strings against the font atlas, new member names checked across the `GameSession` partials. The number
reader and the tag placement were ported to Python and run against the test cases. No shader changes (the floor tint
uses the existing overlay shader).

Gavin was away for this build, so the recommended options were taken: all room instance parameters, value vs target
parameter checks, N for the tags.

## What's in
- **Room data in the snapshot** (Revit `SceneExtractor.RoomData.cs`, Core `RoomInfo`, `RoomData.cs`, `RoomDataFiles.cs`).
  Each room now keeps its **UniqueId**, **ElementId**, **level name**, **Revit's area** (m²) and **every non-empty
  instance parameter**: the text as Revit shows it, plus a number in the model's display units (doubles converted
  from internal units with the model's format options for that spec, integers as they are, text parameters read for a
  leading number such as "45 m²" or "1 234,5"). So Area and a "Required area" compare in the same units, whatever the
  project's units are. Links' rooms use the link's own units.
  - File: `rooms.json` (optional, pooled like `parameters.json`; rows aligned with `model.json`'s rooms), and
    `uniqueId`, `id`, `level`, `area` on `model.json`'s rooms. Additive, format 1. Older files read with no
    parameters.
  - FIND ROOM's rows now show Revit's level name and area (older files: the level under the room, the plan area).
- **Floating room tags** (`GameSession.RoomOverlay.cs`; **N**, in walking; `InputAction.RoomTags`, controls defaults
  version 8). Each tag shows number · name and up to four picked parameters (name in grey, value in white).
  - Placement: at the room's most open point (the point farthest from its walls, so an L-shaped room's tag sits in
    the room, not at the bounds' middle), a set height above the floor found under that point (kept within 0.4 m of
    the room's bottom so a table can't lift it).
  - Rooms within the distance (default 30 m) only, nearest first. A tag that would overlap a nearer one waits until you
    come closer. Past 60 % of the distance a tag shows only number · name.
  - Behind walls: tags fade out (12 opaque rays a frame, round robin over the nearby rooms; glass doesn't hide them).
    The room you're in always shows, and "Through walls" shows them all.
  - Rooms of hidden links don't show; "Link rooms" switches off all linked rooms.
  - Allocation-free per frame: texts and checks are made again only when a setting changes.
- **Target check** (`RoomChecks.Evaluate`): a **value** parameter (default Area) against a **target** parameter, with
  a **tolerance** (% of the target, default 5 %) and a rule:
  - **Within ±**, **At least** (above the target is always fine) or **At most** (below the target is always fine).
  - Colours: green inside the tolerance, amber within twice it, red beyond, grey when either value is missing.
  - It shows on the tags (coloured side bar and stalk, and a line such as `42.10 m² / 45.00 m² · −6.4 %`), as a
    **floor tint** on rooms near you (its own depth-tested overlay, so it never shows through walls), and as a dot
    beside each room in the FIND list.
- **FIND ROOM → ROOM DATA tab** (tabs in the panel header, like MEDIA's):
  - Tags: on, through walls, link rooms; height above floor (0.3–3 m) and distance (5–200 m) sliders.
  - Target check: on, floor tint, value / target shown, rule, tolerance (0–50 %), and a summary line ("31 pass · 8 near
    · 4 off · 10 no data").
  - The parameter list (type to filter): each name with "in 42 of 48 rooms · e.g. 42.10 m² · number", and **SHOW**
    (on the tags, numbered in order, up to 4), **VALUE** and **TARGET** buttons. Picking a target turns the check on;
    SHOW turns the tags on.
- **Saved** with the model in `visibility.json` (`rooms`), beside the grids (file mode: the .bimgo; live: the model
  folder's sidecar). Not part of bookmarks or camera path keys (it belongs to the model, like colour profiles).
- **Marker family spec for Z-B:** `2_marker_family_spec.md`, and the shared parameter file
  `tools/revit/BimGo_SharedParameters.txt` (18 parameters, fixed GUIDs, group *BimGo*).
- **Tests** (`RoomDataTests`): room data round trip (incl. pooled values), older files and a damaged `rooms.json`,
  the number reader, the check's colours for each rule, tag placement inside an L, overlay settings
  (clean / defaults / round trip through `visibility.json`). The controls defaults table has the new row (version 8).

## What isn't
- Comment markers in Revit (Z-B, once the family exists).
- Tags in clash mode, in photo mode and photos (it's UI, like the grid bubbles), and on the minimap.
- Editing room parameters from BimGo (read-only overlay).
- A per-room tint for rooms without a check (the tint is only for the check).
- Rooms' islands (columns, shafts) aren't cut out of the tint: the outer loop is filled and their own geometry hides
  it.

## How to check
1. **Re-export** (or F5 in a live session) a model with rooms that have some parameters (e.g. a "Required area"
   project parameter on a few rooms). Older snapshots work but have no parameters.
2. Walk into a room, press **N**: a toast says tags are on. The room you're in has a tag at about 1.5 m. Walk through a
   corridor: nearby rooms' tags appear as their tag points come into view and fade out behind walls.
3. Esc → FIND ROOM (or Ctrl+F) → **ROOM DATA**. Type "dep" and press SHOW on Department, then SHOW on two more. Close
   the panel: the tags show them underneath, in that order. Far tags show only number · name.
4. Back on ROOM DATA: TARGET on "Required area" (the check turns on). Tags get a green / amber / red bar and the line
   "value / target · ±%". Floors tint near you. The summary line counts them. Try AT LEAST and the tolerance slider:
   colours change as you drag.
5. FIND tab: the rooms have coloured dots. Search, GO to a red one.
6. Height / distance sliders move the tags and change how many show. Tick "Through walls": every tag within the
   distance shows.
7. Linked model with rooms: untick "Link rooms": its tags go. Hide the link itself: its tags go too.
8. Save (file mode) and reopen, or close and reopen a live session: the tags, picks and check come back.
9. Check the walking F1 sheet's row "Grids · colour profile · room tags" and CONTROLS (N is listed, tagged NEW).
10. Options → Display → Performance details with tags and tint on in a room-heavy area: say if the HUD or overlay
    time moves noticeably.
11. Optional: a model in imperial units: Area and "Required area" (both area parameters) still compare. Say if a text
    "Required area" with odd formatting doesn't read.

## Files
- Core: `Scene/RoomData.cs` (new: `RoomParameter`, `RoomValues`, `RoomChecks`, `RoomTagPlacement`, `RoomOverlaySettings`),
  `Scene/SceneData.cs` (`RoomInfo` fields), `Format/RoomDataFiles.cs` (new), `Format/FileModels.cs` (`RoomDto`,
  `RoomDataDto`), `Format/BimGoFormat.cs`, `Format/BimGoWriter.cs`, `Format/BimGoReader.cs`,
  `Format/VisibilityModels.cs`, `Format/CameraPathModels.cs`, `Scene/InputCatalog.cs`.
- Revit: `Extraction/SceneExtractor.RoomData.cs` (new), `Extraction/SceneExtractor.cs` (`BuildRoom`).
- App: `Game/GameSession.RoomOverlay.cs` (new), `GameSession.Rooms.cs`, `GameSession.cs`, `GameSession.Render.cs`,
  `GameSession.Visibility.cs`.
- Tests: `RoomDataTests.cs` (new), `TestData.cs` (rooms parameter).
- Tools: `tools/revit/BimGo_SharedParameters.txt` (new).

## Revision r2 (build error)
- **CS0234 / name clash in `GameSession.RoomOverlay.cs`:** `GameSession` already has a `RoomLabel(int)` method (sun
  hours, section box), so `RoomLabel.Place(room)` bound to the method. The Core class is now **`RoomTagPlacement`**
  (`RoomTagPlacement.Place`); tests and notes follow. The other new Core types were checked against every
  `GameSession` member name: no other clashes.
- **Marker family received** (`src/BimGo.Revit/Resources/Families/BimGo_Comment_Marker.rfa`): three status spheres (*Radius* drives them), no
  flag (BimGo_IsHighPriority stays as a parameter for Revit filters). All 18 shared parameter GUIDs are in the file.

## Revision r3 (tests)
- Room tags moved from **N** to **Shift+N**: N is the Measure gun's normal projection (`Defaults_HaveNoClashInAnySituation`
  and the clash counts in the keymap tests). README §3's row follows. Gavin applied it by hand (no zip).
