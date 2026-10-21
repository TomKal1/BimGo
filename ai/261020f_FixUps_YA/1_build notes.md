# Y-A build notes: saved measurements, clearance across, shared comment pictures

Not compiled here (no .NET SDK). Brackets, strings, char literals and UI glyphs script-checked in every changed `.cs`
file; new member names checked across the `GameSession` partials.

## What's in
- **Saved measurements.**
  - Core `Format/MeasurementModels.cs` (new): `MeasurementDocument` (`version`, `model`, `hidden`, `measurements`;
    `Clean()` drops damaged entries, at most 2000), `MeasurementRecord` (`type` distance / area / angle / clearance,
    `points` flat x, y, z in Revit internal metres, `normalX/Y/Z`, `value`, `value2`, `above`, `below`, `createdUtc`;
    `IsValid()` checks the point count per type and finite values), `MeasurementFiles` (`SidecarFor`, `Read`, `Write`,
    `ToCsv`).
  - Format: `.bimgo` entry `measurements.json` (optional; written only when there is something; `BimGoDocument.Measurements`,
    writer, reader). Model folder file `measurements.json`, in `ModelFolders.SIDECARS` with suffix
    `.bimgo-measurements.json`, so it migrates, is mirrored beside the model when sharing is on and newer shared copies
    are taken at Go, like the others. Revit's Export embeds the folder's copy.
  - App: `GameSession.Measurements.cs` (new) loads them into the Measure gun after the guns are made (the .bimgo's,
    else the model folder's), writes the folder file on every change (live) or marks the .bimgo dirty (saved with it),
    and exports the CSV. `MeasureGun`: each new measurement, RMB-remove, delete-all and show / hide call
    `Session.MeasurementsChanged()`; `ToDocument(origin)` / `LoadFrom(document, origin)` convert scene-local ↔ internal
    metres (area fills recomputed on load).
  - Keys (Measure gun, `DEFAULTS_VERSION` 6 + two rows): **I** shows / hides all (kept in the file; a new measurement
    shows them again; RMB-remove while hidden says to show them first), **E** exports a CSV (file dialog; type, value
    and unit, perimeter / above, time, points). **X** now cancels the one being made, else asks, and X again within 3 s
    deletes all (they're saved now). Panel: a "Saved N (· hidden)" line with the keys. F1 rows updated.
- **Clearance along the surface normal.** A floor / ceiling (|normal Z| ≥ 0.7): as before, down and up. A wall or steeper
  surface: level, straight out from it (the normal flattened) to the next surface across, labelled "↔ 2.400 m"; the
  panel shows "↔ Across, level". Stored with the axis in the normal (zero = vertical), so old measurements read as
  vertical. "Nothing across from this surface" when it finds nothing within 60 m.
- **Comment pictures shared.** With sharing on, writing the comments also copies the model folder's `comments\*.jpg`
  that are new or newer to `<model>.bimgo-comment-pictures\` beside the model; each Go copies newer / missing ones back
  (`ModelFolders.SyncPictures`, `SharedPicturesFolderFor`). Nothing is deleted on either side.
- **Tests:** `MeasurementTests` (clean, .bimgo round trip and left out when empty, model folder mirror, shared pictures
  both ways, CSV rows). README §3 rows added by hand to match the generator.
- **README:** §3 rows; §5 Measure gun; §6 `measurements.json` entry and the model folder line; §8 open build, the
  BCF and review-tools limitations rewritten; §9 rows (plan, Y-A).

## What isn't
- Clearance along a sloped normal (only vertical or level across).
- Measurements aren't editable (only remove last / delete all); no per-measurement hide.
- Shared comment pictures are never deleted beside the model.
- Y-B (UI shell leftovers) next.

## How to check, step by step
1. **Build:** Core tests pass (`MeasurementTests` new; `ControlsTests` with version 6).
2. **Saved (live):** Go; Measure gun (2): make a distance, an area, an angle and a clearance. Leave (Esc → LEAVE
   SESSION) and Go again: all four are back where they were. `%LocalAppData%\BimGo\Models\<model>_<hash>\measurements.json`
   exists. F5 refresh: still there.
3. **Show / hide:** I hides them (toast, panel "· hidden"); I shows them. Hide, leave, Go: still hidden. Hidden, make a new
   one: all show again.
4. **Delete:** RMB removes the last. X once: toast asks; X again within 3 s: all gone (and gone after a Go). X once, wait:
   nothing deleted.
5. **CSV:** E: a save dialog; the file lists each measurement (metres / m² / degrees, points in internal metres) and opens
   in Excel.
6. **.bimgo:** SAVE AS .BIMGO… from a live session, open the file: the measurements are there; add one: title shows *;
   save, reopen: kept. Revit Export .bimgo: includes the model folder's measurements.
7. **Clearance:** clearance mode aimed at a floor: as before (↓ / ↑). Aimed at a wall: a level line straight out to the
   opposite wall, "↔ …", panel "↔ Across, level". Aimed at a door in a corridor: the corridor width.
8. **Shared pictures** (Options → sharing beside the model on): add a comment with a picture: a `<model>.bimgo-comment-pictures`
   folder appears beside the model with its .jpg. On another machine / folder (or delete the local `comments\<id>.jpg`),
   Go: the picture comes back.
