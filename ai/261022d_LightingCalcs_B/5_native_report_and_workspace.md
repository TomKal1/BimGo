# Native report and lighting workspace — 2026-10-22

The pause menu now has a LIGHTING entry opening a wide, dedicated panel. Sets have
their own list. SETUP, MATERIALS, SOLVER, RESULTS, FIXTURES and BASIS separate the
tasks; Run, Cancel, Review Model and Report HTML remain outside the scroll area.
Fixtures use a type selector rather than a full list of every type's controls.
The former LIGHTS settings tab and ISO review both link to the workspace. Escape
closes a dropdown first, then returns to the pause menu without canceling work.

BimGo's native report presenter fills the fixture schedule, distinguishes estimated
photometry and full IES by content hash, and places fixture markers on each plan.
It includes actual patch sizes, patch counts, iterations, excluded points, points without direct light,
points and timings per zone, identifies UTC, and credits BimGo plus the shared
WWTools engine. Available IES watts are disclosed as an incomplete power basis.

The report calls Core's existing model false-colour function. Its default maximum
comes from the numerical result's rounded scale, and interactive HTML export uses
the current model scale and fc/lux units. Changing presentation does not change
the input revision or calculated values. Upstream engine source remains unchanged.

Validation:

- App Release build succeeds; existing GameWindow CS8778 warning remains.
- 560 tests pass, including 17 native calculation/report tests. New report cases
  check escaping, schedule/provenance, fixture markers, diagnostics, model palette,
  default/custom scales and lux conversion without changing the input revision.
- Compiled App smoke checks cover stale revisions, validation outcomes, physical
  versus presentation state and panel back navigation.
- Replayed the native input saved in the user's Snowdon `.bimgo`: P01 Parking
  Garage, 2,424 points. Every persisted total and direct point float matches, and
  SHA-256 remains `b1c953fe0116d1a69fd44b835f7ec7c7c05d3cbd8bd9e4c8eae7cdbd47411472`.
  The report/model scale is 0–40 fc. This is replay parity, not independent physical
  accuracy or refinement-convergence validation.
- Opened the saved model in the compiled App and inspected Setup, Results and
  Fixtures in the new panel. Report browser preview was blocked by the browser
  tool's local-file URL policy; HTML/SVG structure and content checks pass.

Reproduce the saved-input report check in PowerShell:

```powershell
./tools/check-native-report.ps1 `
  -BuildDirectory ./src/BimGo.App/bin/Release/net8.0-windows `
  -ModelPath 'C:\Users\Thomas\Downloads\Snowdon Towers Sample Electrical.bimgo' `
  -ReportPath "$PWD/tools/Lighting.Baseline/bin/P01-Parking-Garage-report.html"
```

The original report in Downloads was not overwritten. The generated preview lives
under the ignored tool build folder. Existing saved native runs must be recalculated
to populate the in-memory report used by the export buttons. The installed App
copy has not been updated; run the rebuilt project output or rebuild in VS.
