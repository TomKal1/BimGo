# Lighting calculations (TomKal1 fork)

Fork-only features of TomKal1/BimGo, not part of upstream aussieBIMguru/BimGo. They were documented in the fork's README before
upstream moved its detail into `docs/`. Build notes for each round are in `ai/261022d_LightingCalcs_B/`.

## Native calculations and the shared engine

BimGo calculates illuminance itself with the bundled `Lighting.Engine`, a pinned, byte-identical copy of the WWTools lighting
engine (TomKal1/WWTools `main`). Rooms on a storey share one interreflection solve; light centres are placed by auto mounting;
each fixture counts in one room. See `ai/261022d_LightingCalcs_B/7_wwtools_main_resync.md` and `src/Lighting.Engine/UPSTREAM-NOTICE.md`.

## Calculated images

**Independent calculated lighting images (Tom-Main, 2026-10-22).** LIGHTING → RESULTS and Model Review →
CALCULATED IMAGES · PNG… open a local room-image viewer with cutaway/interior cameras, fixed exposure
and 1080p/4K export. Model Review's Calculated mode uses saved diffuse lighting and model hues.
Native report exports include the images. The same CPU renderer ships independently in WWTools;
neither application requires the other, and no new external dependency was added. This is calculated
diffuse appearance, with no coloured-bounce/gloss/glass transport, rather than full photorealism.
See [build notes](../ai/261022d_LightingCalcs_B/6_independent_calculated_images.md).


## Lighting calc results from WW Tools

- **Lighting calc results** (fork build LC-B, TomKal1/BimGo; Core `Format/LightingCalcFiles.cs`, `Scene/LightingCalcs.cs`, App `Rendering/CalcOverlay.cs`, `GameSession.LightingCalcs`). WW Tools' Lighting Calc writes each calc set's solved results into the model folder's `lighting-calcs\` (`<set>_<id>.json` + `.bin`; WW Tools names the folder with a port of `ModelFolders.FolderNameFor`, tested on shared vectors). BimGo reads them on a worker thread (offsets and counts checked against the body, damaged zones dropped with a reason, never throws), converts feet to scene metres (× 0.3048 − `OriginOffset`) and averages each patch corner with the patches meeting there on the same plane (area-weighted, covered patches left out, as WW Tools' room views). Shift+J (`InputAction.LightingCalcMode`) cycles off → illuminance → luminance → points; Pause → LIGHTS → LIGHTING CALCS picks the set and what to draw (surfaces, points, values, isolux lines), opacity, the scale's top and IMPORT… (a results file by hand). The overlay is one static buffer on the overlay shader (rebuilt only on a change; patches lifted 1.5 cm along their normal, clipped to the room's floor and ceiling like the report), drawn with the study cells so photos and screenshots keep it; illuminance uses WW Tools' blue → red ramp on the sheets' scale, luminance its grey (1 − e^(−1.8 L / the 95th percentile)). Values are 2D labels within 8 m (nearer first, overlapping ones skipped, behind-the-model ones hidden by a BVH ray); the legend in the left column carries the zone you stand in (Avg / Max / Min / Avg:Min, MEETS / MISSES). Lux in metric, footcandles in imperial. Live sessions check the folder every 2 s and reload a re-run. WW Tools' View in BimGo leaves `lighting-calcs\open.json` (set id, zone UniqueId, view) and posts the Go button; the session takes it when under 2 minutes old, shows that set and stands you in the zone (its Revit room when the snapshot has it, else the zone's middle). Off by default; an F5 refresh keeps the view.

| File | Notes |
|---|---|
| `lighting-calcs/*.json` · `.bin` | optional (fork build LC-B, TomKal1/BimGo): lighting calc results written by WW Tools' Lighting Calc (format `lighting-calc-results` v1: a JSON header per calc set (set, run, scale, zones with stats, targets, footprint loops and byte ranges, luminaires) and a little-endian float32 body (points x y z E Edirect; patches a b c normal E ρ flags; isolux segments); Revit internal feet, footcandles; the spec is WW Tools' `docs/lighting-calcs/results-format.md`). Copied as held from the model folder's `lighting-calcs\` at Export / Save (newest first, up to 100 MB) |

## Model review

**Model review:** **ISO VIEW** on the main pause menu or beside the walkthrough's sun control opens an isometric cutaway of the room you occupy, or the whole building when outside a room. No WW Tools import is required. ISO, floor plans, reflected ceiling plans, free flight, the level dropdown (including All levels), up/down level arrows, ViewCube HOME/FIT, section controls, PNG export and revision pins work with the model alone. Imported lighting results add the calculation display and statistics. Pause → LIGHTS → LIGHTING CALCS → **REVIEW MODEL · ISO / PLAN / FLY** remains available, and WW Tools' View in BimGo opens this workspace on its requested zone. See [review notes](ai/261022d_LightingCalcs_B/2_lighting_review.md) for controls and earlier validation.

**Lighting review follow-up:** light-blue grid/level overlays no longer expand in orthographic views and start hidden; optional reference grids clip to the box. A draggable navigation cube provides face/edge/corner snaps and HOME/FIT controls; orbit works above and below the model. App builds with 0 errors; 516 Core tests pass; headless WebGL2 verifies constant 2-pixel grid width at 10/300 m and section clipping. Revit's post-build installation now warns on locked DLLs and retries on the next build; close Revit before rebuilding to install updates. Details are in the review notes above.

**Mouse and cut-face update:** middle drag rotates (looks in place in fly mode), left drag pans, and released clicks select rooms in parallel views. Fly viewport drags/clicks cannot teleport into rooms; use the room list for destinations. ViewCube hover previews mark the exact corner, edge endpoints or face vertices. Grey section-cap fills start off in lighting review and can be restored with **Show cut caps**. Defaults version 9 adds the rebindable MMB ReviewOrbit action. App builds with 0 errors; 521 Core tests pass, including gesture, pan and preview regressions; the compiled input handler also passes a one-frame drag smoke check. Native interactive verification remains open.

**Overview calculation values:** lighting review samples actual point values across screen-space regions instead of spending its 160-label limit in calculation-file order. Spacing adapts to viewport size; hidden points have bounded alternatives in the same region, and overlapping labels are skipped. PNG exports share the layout. Walkthrough proximity labels retain their existing behaviour. App builds with 0 errors; 531 Core tests pass, including coverage, order independence, occlusion fallbacks, viewport budgets and allocation-free reuse. Visual verification on the Snowdon model remains open.

**Material review update:** the launch HUD's Material colour appearance is now available in review's **Materials** view with calculation points, values and the legend retained in ISO, PLAN, RCP, FLY and PNG exports. Review starts with neutral directional material shading, without sun, fixture accumulation, bloom or distance fog. **Use scene lighting** opts into the model's lit preview using the normal walking fixture budget; review no longer forces hundreds of unshadowed fixtures into the scene. The walkthrough's colour and lighting settings are preserved. Existing WW Tools Points requests map to Materials; no format or enum changes. App builds with 0 errors; 531 Core tests pass and the compiled App passes 32 review/view/mode mesh checks. Native visual verification remains open.

**Lighting review units:** imperial now displays point illuminance in foot-candles (fc) and surface luminance in foot-lamberts (fL); metric displays lux (lx) and cd/m² respectively. Dock captions and the shared HUD/PNG legend distinguish the two quantities, and room statistics explicitly identify illuminance. Luminance tick values convert with the unit preference; shading and saved calculation data remain in their original units. App builds with 0 errors; 533 Core tests pass, plus compiled-App checks for 12 unit/mode transitions and 32 overlay cases. Native visual verification remains open.

**Fork build LC-B (TomKal1/BimGo, not an upstream round): lighting calc results from WW Tools** (`ai/261022d_LightingCalcs_B/`): compiled here (.NET 8 SDK, the App against the Windows Desktop reference pack; Core tests green), not run on Windows. To verify: Go after a WW Tools Lighting Calc run ("Write results for BimGo" on) and Shift+J: the surfaces line up with the walls, floors and ceilings and don't fight them (also at grazing angles and in a section cut); the values at points match the WW Tools CSV (lux in metric = fc × 10.764); the legend's scale matches the sheets; a re-run in WW Tools toasts "updated" within ~2 s; the CALCS section lists, switches and imports sets; F12 keeps the legend; Export / Save carry the results into the .bimgo and a file opened without Revit shows them; WW Tools' View in BimGo (Lighting Tools ▾, or the calc window) opens Go on the set and stands you in the selected space; a 100 k-patch building: build time when switching modes, and the Performance details panel's cost.

## History

| Date | Round | Summary |
|---|---|---|
| 2026-10-22 | Plan (`261022c_LightingCalcs_Plan`) | Fork only (TomKal1/BimGo): plan for WW Tools' lighting calc results in BimGo |
| 2026-10-22 | Lighting calcs, fork build LC-B (`261022d_LightingCalcs_B`) | Fork only (TomKal1/BimGo): WW Tools' lighting calc results on the model: `lighting-calcs/` results files (model folder, optional .bimgo entries) read and shown as surfaces in false colour (the sheets' scale) or luminance, points, values near the eye, isolux lines, a legend with the zone's stats and target; Shift+J cycles; Pause → LIGHTS → LIGHTING CALCS; live reload on a re-run; WW Tools' View in BimGo request (`open.json`) picks the set and the zone to start in; DEFAULTS_VERSION 8 |
| 2026-10-21 | Lighting model review, fork extension (`261022d_LightingCalcs_B/2_lighting_review.md`) | Building/storey and room isometrics, floor and reflected ceiling plans, free flight, six-face clipping of geometry and calculation overlays, room statistics, PNG exports and revision pins. App builds; 513 Core tests pass; interactive verification remains open. |
