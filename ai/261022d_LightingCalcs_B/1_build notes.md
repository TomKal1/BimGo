# Fork build LC-B build notes: lighting calc results from WW Tools

**Compiled here, not run on Windows.** This sandbox got the .NET 8 SDK (Ubuntu's package) this time:
- **Core** builds, and the Core tests run: 509 pass, plus 1 Linux-only failure that predates this build
  (`SunStudyTests.Studies_WriteListReadDelete`: ':' is a valid file name character on Linux).
- **App** compiles in full against the Windows Desktop reference pack (from NuGet): 0 errors, no new warnings.
- **BimGo.Revit** wasn't compiled (no Revit API here). Its change is one line, on Core types.
- **UI strings** checked against the font atlas.
- **No new shader:** the overlay reuses `OVERLAY_VS` / `OVERLAY_FS`. Those were compiled in headless WebGL2 and drew the
  real `CalcOverlay.Build` vertices for the test renders beside this file.

## What's in
- **Results files** (Core `Format/LightingCalcFiles.cs`). The format is `lighting-calc-results` v1, written by WW Tools'
  Lighting Calc; the spec is WW Tools' `docs/lighting-calcs/results-format.md`.
  - Each set is a JSON header plus a float32 body: `<model folder>\lighting-calcs\<set>_<id>.json` + `.bin`.
    `ModelFolders.LIGHTING_CALCS_FOLDER`.
  - `List` (newest first; a header without its body is skipped), `Read`, and `Parse`, which never throws:
    - it checks `binBytes` against the body (a mismatch means the file is mid-write: try later);
    - it checks units, and every offset and count against the body;
    - a damaged zone is dropped with a reason (`Warnings`), and a bad patch is skipped.
  - Feet → scene metres (× 0.3048 − `OriginOffset`).
  - `.bimgo`: `ReadForEmbedding` (newest sets first, up to 100 MB), `EmbeddedNames`, `ParseEmbedded`; `IsEntryName`
    refuses `..` and sub-folders.
  - `TakeOpenRequest`: reads and deletes `open.json`. It is used only when under 2 minutes old.
- **Models and maths** (Core `Scene/LightingCalcs.cs`).
  - Types: `LightingCalcResult`, `CalcZoneResult`, `CalcPatch`, `CalcIsoline`, `CalcStats`, `CalcTarget`,
    `CalcLuminaire`, and `CalcDisplayMode` (Off / Illuminance / Luminance / Points).
  - Colours match WW Tools:
    - false colour is `PointColors.Ramp` (HSV 240° → 0°, value 0.9) on the sheets' scale, shaded 0.78–1 by facing as the
      report's room views;
    - luminance grey is 1 − e^(−1.8 L / the set's 95th percentile), slightly warm.
  - Corner values: area-weighted means of the patches meeting at a corner on the same plane (0.01 ft, normals ×20),
    covered patches left out. This is the same rule as `RoomRender`.
  - `ClipZ` clips patches to the zone's floor and ceiling ± 0.25 ft, as the report does, so long slab-edge patches don't
    poke through floors. `ZoneAt` returns the smallest zone over a point, between its floor and top.
- **The overlay** (App `Rendering/CalcOverlay.cs`). One static VBO of `OverlayVertex` on the overlay shader, rebuilt
  only when the set, view, toggles or scale change:
  - patches: per-corner colours, lifted 1.5 cm along the normal, polygon offset, depth-tested, no depth writes;
  - points: flat hexagon discs (4.5 cm) with a dark rim;
  - isolux lines: flat 3 cm strips on the work plane, the target line white and wider.
- **The session** (App `Game/GameSession.LightingCalcs.cs`):
  - **Loading.** It finds the results (the `.bimgo`'s entries, else the model folder) and loads on a worker thread.
  - **Folder check.** Every 2 s in live sessions it reads two time stamps. A changed shown set reloads with
    "Lighting calc “X” updated"; the first results to arrive toast.
  - **Shift+J** cycles the view. With nothing loaded it loads the newest set, or says how to make one.
  - **Labels** are drawn for points within 8 m: the nearer 4 m first, at most 160, overlapping ones skipped, and points
    behind the model hidden by a BVH ray.
  - **Legend.** Left column, above the sun legend: the set, view and unit, the bar with 5 ticks, and the zone you stand
    in (Avg / Max / Min, Avg:Min, MEETS / MISSES).
  - **Units.** Lux in metric, footcandles in imperial.
  - **F12** screenshots carry the legend.
  - **Pause → LIGHTS → LIGHTING CALCS**:
    - the sets (name, run time, zone count), and the view;
    - toggles: surfaces, points, values near you, isolux lines;
    - opacity, and the scale top (× the sheets' scale, snaps to 100 %);
    - IMPORT… (a results file by hand: live, copied into the model folder; file mode, added to the file's entries);
    - GO TO A ZONE; what the set is (tool, interreflections, counts).
  - **F5** keeps the view (a static memory of the folder, set and toggles).
- **Phase C (BimGo side).** WW Tools' View in BimGo writes `open.json` (set id, zone UniqueId, view), then posts BimGo's
  Go button. The new session takes the request, loads that set in that view, and stands you in the zone: its Revit room
  when the snapshot has one (number, name and floor within 0.5 m), else the zone's middle.
- **`.bimgo`.** `BimGoDocument.LightingCalcs` holds the raw entries. They are written as held, and read up to 100 MB.
  Export (Revit) and Save in a live session embed the model folder's results; Save in file mode keeps the file's own.
- **Hooks** in shared files, each marked "fork build LC-B":
  - `GameSession.cs`: init, the key, dispose;
  - `GameSession.Render.cs`: draw beside the study cells, labels, legend, screenshot;
  - `GameSession.Menu.cs`: CALCS before the LIGHTS rows;
  - `GameSession.Document.cs`: save;
  - `InputCatalog.cs`: the action, default and `DefaultsChanges` row;
  - `BimGoDocument` / `BimGoReader` / `BimGoWriter`, `ModelFolders` (the constant), `Cmds_BimGo.cs` (Export);
  - README §3 is regenerated by the tests.
- **Tests** (`tests/BimGo.Core.Tests/LightingCalcTests.cs`, 11). They use files made from the spec in the test, not by
  WW Tools:
  - header, zones, points, luminaires;
  - feet → metres with the origin;
  - damage: bad JSON, wrong format, version 2, units, mid-write, points or patches outside the body;
  - colours against WW Tools' ramp; corner averaging, and covered patches left out;
  - `ZoneAt`, `ClipZ`;
  - the folder list (newest first, half pairs skipped), the folder name, the `.bimgo` round trip (and `..` refused);
  - the open request (fresh once, stale ignored, broken ignored).
- **Cross-check** with WW Tools' real output (a solved 30 × 20 ft room, 6 troffers). This BimGo read WW Tools' results
  file and its `open.json`: stats, target, points and isolines agree, `ZoneAt` finds the zone, and the request matched
  the set.

## Not in (later, the plan's Phase D)
- Compare mode (calc and realistic view at the same camera).
- BimGo's lights taking the calc's LLF and photometry.
- Pass / fail colouring of points and patches.
- A "calc tour" of the zones.
- Daylight plus electric.
- The legend in photo mode (photos keep the overlay; F12 keeps the legend).
- Gamepad binding for Shift+J.

## To verify on Windows
1. In WW Tools, run a Lighting Calc ("Write results for BimGo" on, the default). Then Go in Revit and press Shift+J:
   the toast names the set, and the surfaces show in false colour.
2. Surfaces sit on the walls, floors and ceilings with no z-fighting, also at grazing angles and in a section box cut.
   Long patches don't poke above the ceiling or below the floor.
3. Walk to a point and compare its label with the WW Tools CSV (lux = fc × 10.764 in metric; Options → units imperial
   shows fc).
4. The legend's top tick matches the sheets' colour scale. Standing in a zone shows its stats and MEETS / MISSES as in
   the report.
5. Shift+J cycles illuminance → luminance (grey, no isolux lines) → points → off. The CALCS section switches sets,
   toggles, opacity and scale top. IMPORT… takes a `… - Results.json` from the report folder.
6. Re-run the calc set in WW Tools with BimGo open: within ~2 s the "updated" toast, and the new values.
7. F5 keeps the view. F12's screenshot has the legend.
8. Export .bimgo, or Save in a live session; then open the file without Revit: CALCS lists the sets and Shift+J shows
   them.
9. In WW Tools, Lighting Tools ▾ → View in BimGo (select a space first), or the calc window's View in BimGo: BimGo's Go
   starts, shows that set and stands you in the space. If the id `CustomCtrl_%CustomCtrl_%BimGo%Walkthrough%BimGo_Launch`
   isn't found, a Revit journal made while pressing Go gives the right one.
10. A big model (100 k+ patches): the time to build when switching views, and the cost in Options → Performance details.
