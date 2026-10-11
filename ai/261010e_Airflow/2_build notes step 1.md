# Airflow step 1: Core foundations (build notes)

**Date:** 2026-10-11. **Scope:** step 1 of `1_implementation_plan.md`: units, catalog, terminal data, file
entries, settings. Core and tests only: **no App or Revit change**, nothing visible in the walkthrough yet.

**Built on:** the repo as of commit `3089b9f` (Gavin's last merge). If your working copy has moved on, merge
these files in (all new files plus small, additive edits listed below).

## How it was checked

- The .NET 8 SDK (Ubuntu package) was available in this session, so **Core and the Core tests were compiled and
  run** (Linux). Result: **256 / 257 pass**.
- The one failure is **pre-existing and Linux-only**: `SunStudyTests.Studies_WriteListReadDelete` expects `:` to be
  replaced in file names, which only happens on Windows. It fails the same way without these changes. Expect
  257 / 257 on Windows.
- BimGo.App and BimGo.Revit were **not** compiled (the Linux SDK has no Windows desktop targets; no Revit API).
  The changes only add to Core's API, and none of the new type names exist in App or Revit, but please build the
  whole solution once.

## New files

| File | What |
|---|---|
| `Core/Scene/UnitConvert.cs` | `MeasurementSystem` (SI / IP) and `UnitConvert`: conversions (flow, speed, pressure, length, size, temperature, temperature difference), envelope speed sets, `Parse`, `FromRevitUnitTypeId` (for step 2), string formatting |
| `Core/Scene/DiffuserCatalog.cs` | `CatalogRow`, `CatalogFamily`, `DecayCurve`, `DiffuserPerformance`, `DiffuserCatalog` (fit, interpolation, size matching, pressure / NC, generated generic family) |
| `Core/Scene/AirTerminals.cs` | `AirTerminal`, `AirData`, `AirTerminalHosting`, `FamilyRating` |
| `Core/Scene/AirflowState.cs` | `AirflowState` (per-model study state) and its enums: `AirflowMode`, `OccupiedZonePreset`, `DiffuserPlacementMode`, `CeilingXRay` |
| `Core/Format/CatalogFiles.cs` | User catalogs (`%AppData%\BimGo\Catalogs\*.json` / `*.csv`), DTO ↔ family conversion |
| `tests/…/AirflowCatalogTests.cs` | 21 tests: units, fit, curve, generic family, interpolation, extrapolation, size matching, catalog, CSV, folder load, DTO round trip |
| `tests/…/AirflowFileTests.cs` | 8 tests: `air.json` / `airflow.json` round trips and validation, old files, state clamping, sidecar, settings, terminal helpers |

## Edited files (all additive)

| File | Change |
|---|---|
| `Core/Scene/SceneData.cs` | `SceneData.Air` (`AirData.Empty` by default) |
| `Core/Scene/LaunchSettings.cs` | `AirflowFreeJetFactor`, `AirflowBandFactor`, `AirflowPlenumAllowance`, `AirflowDefaultMount`, `AirflowOffCeilingTolerance` + clamps in `Sanitise` |
| `Core/Format/BimGoFormat.cs` | `ENTRY_AIR`, `ENTRY_AIRFLOW`, `AIRFLOW_SIDECAR_SUFFIX`; header comment |
| `Core/Format/BimGoDocument.cs` | `BimGoDocument.Airflow` |
| `Core/Format/BimGoWriter.cs` | Writes `air.json` (when there are terminals) and `airflow.json` (when not default) |
| `Core/Format/BimGoReader.cs` | Reads both, validated (`BuildAir`): damaged terminals and catalog rows are dropped with a log line, never failing the load |
| `Core/Format/FileModels.cs` | `AirDto`, `AirTerminalDto`, `CatalogFileDto`, `CatalogFamilyDto`, `CatalogRowDto` |
| `Core/Format/SidecarJson.cs` | `AirflowFiles` (like `SunFiles`) |
| `Core/Format/ModelFolders.cs` | `AIRFLOW_FILE` in the sidecar list, so it mirrors beside the model like `sun.json` |
| `tests/…/TestData.cs` | `BuildScene` / `BuildDocument` take optional `air` / `airflow` |
| `README.md` | §6 format table, settings keys, catalog folder; §9 history row |

`FORMAT_VERSION` stays 1 and the live protocol is untouched.

## Changes from the plan (and why)

1. **Names:** `MeasurementSystem` and `UnitConvert` instead of `UnitSystem` and `Units`. BimGo.Revit has
   `global using Autodesk.Revit.DB`, which already defines `Units` and `UnitSystem`; the plan's names would be
   ambiguous in the add-in.
2. **Flows beyond ±25 % of the table are flagged, not clamped.** `DiffuserPerformance` has `Extrapolated` (outside
   the table) and `OutOfRange` (more than 25 % outside). Clamping the flow would show a wrong throw silently;
   flagging lets the readout warn.
3. **NC beyond the table** uses the table's own slope (linear in log Q), falling back to 50 per decade (sound
   power rises with about the fifth power of speed) instead of the plan's 10 per decade.
4. **A neck more than 5 % from any catalog neck** still works: A / Q is scaled by the diameter ratio (A ∝ Q / d)
   and `SizeMatched` is false.
5. **`AirTerminalMount` (ceiling / exposed) isn't stored.** It's decided by the trace from the geometry (step 4),
   so it lives there.
6. **Formatting** returns strings (panels built on change, CSV export). The per-frame, allocation-free readout
   variants come with the readout in step 3.
7. **In wg factor** 249.089 Pa (4 °C water column), as in the plan; some HVAC references use 248.84 (60 °F). The
   difference is 0.1 %.
8. `AirflowState` also holds **per-type catalog overrides** (type UniqueId → catalog key) for the Scan readout's
   "use this catalog" choice (step 2 / 3).
9. **CSV values can't contain commas** (simple split); documented in `CatalogFiles`.

## The generic family (numbers to sanity-check)

Generated from `T_v = (2.26 · Q / (√A_neck · v))^(1 / 1.15)` (IP units). Checked by the tests:

| Diffuser | T150 | T100 | T50 | Pt | NC |
|---|---|---|---|---|---|
| 8 in neck, 24 in face, 250 CFM (716 fpm neck) | ≈ 5.0 ft | ≈ 7.1 ft | ≈ 13.0 ft | ≈ 0.058 in wg | ≈ 20 |

IP sizes 6–16 in necks (12 in faces for 6 / 8 in), 400–800 fpm; SI sizes 150–400 mm necks (300 mm faces for
150 / 200 mm), 2–4 m/s. **Please compare a few sizes against a real 3-cone submittal**; if they're off, only the two
constants (`GENERIC_C`, `GENERIC_N`) and the pressure / NC formulas in `DiffuserCatalog` need changing.

## To verify on Windows

- Build the whole solution (R25 / R26 / R27 configurations) and run the Core tests (expect 257 / 257).
- Nothing else: no App or Revit behaviour changed.

## Next: step 2 (Revit extraction)

`SceneExtractor.Air.cs` + `FamilyCheck.cs` + `DiffuserProfiles`: read Air Terminals into `AirData`, project units
via `UnitConvert.FromRevitUnitTypeId`, ratings. Needs Revit to test; the API list is in the plan, §2.4.
