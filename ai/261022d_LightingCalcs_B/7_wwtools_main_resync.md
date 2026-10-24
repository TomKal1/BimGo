# Lighting.Engine re-sync to WWTools main — 2026-10-24

BimGo's bundled engine now pins TomKal1/WWTools `main` at `d1542a8a68c4419f51a3a34d78b1caa465b4e9d5` (PR #42, "Lighting-Calc-Updates"), replacing `7c307eb` on `claude/zealous-brahmagupta-n6ies6`. Engine identity is `0.2.0` / `wwtools-diffuse-v2`.

## Engine sources

- 43 files in `src/Lighting.Engine/Upstream`, all byte-identical to WWTools main and blob-verified by `tools/check-lighting-engine.ps1`. No local overlay remains: WWTools main already carries the calculated-imaging changes, so the root `CalculatedImage.cs` and `CalculationSelection.cs` copies were removed.
- New Revit-free engine files: `SharedSolution`, `ZoneClusters`, `FixtureZoneCheck`, `LuminaireMounting`, `CalculationSelection`, `CalculatedImage`.
- `check-lighting-engine.ps1` now runs on Windows PowerShell 5.1 (it used `Path.GetRelativePath`, which needs PowerShell 7).

## Ported into BimGo.Calculations

WWTools' orchestration lives in its Revit runner, so BimGo re-implements it:

- **Storey-cluster solve** (`NativeClusterSolve`, from `LightingCalcRunner.SolveInClusters`): rooms are grouped by storey (multi-storey rooms alone), interreflection is solved once per cluster, then each room does its own final gather. A partial rerun solves only the selected rooms, in their own clusters.
- **Auto mounting** (`LuminaireMounting.Place`, Auto): replaces the old "1 inch below a nearby ceiling" correction. Uses the exported fixture box, ceiling and floor tags and the type name: recessed in ceilings of any thickness, pendants hung below their mount, standing and surface fixtures, and lights buried in a slab dropped out of its underside. Applies only to downward sources at their family/source origin with no manual height offset or lens position. The panel checkbox is now "Auto mounting (light centres)"; the saved field is still `CorrectRecessedSources`.
- **One owner per source** (`FixtureZoneCheck`): counts and power density no longer double-count overlapping rooms. A source up to 3 ft above a room's top belongs to it, unless it stands on the floor above. BimGo has no Revit level or space per fixture, so ownership is geometric. Stacked copies, buried lights and fixtures mounted at a floor are still flagged.
- **Levels**: the export carries building storeys; rooms take their level, the storeys they span, and the level above. The transport domain now reaches the level above, as the engine's `RegionBox` does.
- **Partial reruns** are refused when a shared setting changed since the complete run (grid, reflectances, quality, auto mounting, ...).
- The run keeps a calculation log (`NativeCalculationRun.Log`) with clusters, per-room statistics and per-fixture ownership warnings.

Not ported: elevator-shaft exclusion (BimGo rooms are picked by hand), Revit level/space cross-checks, wall aiming (no wall host or facing in exports), per-fixture Luminaire Manager overrides.

## Verification

- 574 Core/calculation tests pass, including new tests for auto mounting, single ownership, one cluster per storey, shared-setting refusal and storeys.
- `tools/check-lighting-engine.ps1` passes: 43 sources, 5 recaptured goldens, physics and edit checks.
- Corpus before/after: `tests/Lighting.Corpus/golden-diff.md`. The single-room scenes are unchanged; connected-opening totals move by up to 0.03 %. The main changes are in multi-room clustering, mounting and ownership, which this synthetic corpus does not exercise.
- Not done: Snowdon replay against WWTools' own rerun, live app run, `check-native-lighting.ps1` (needs PowerShell 7 and a model). A calculation input saved by engine 0.1.0 is refused on replay (engine commit check); rerun the set.
