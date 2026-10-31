# One acoustics engine for WW Tools and BimGo (plan update, 2026-10-31)

**Request:** the acoustic calculation must also run inside WW Tools, without BimGo, the way the Lighting Calc does.
This note changes *where code lives* in `0_plan.md` and `2_transmission.md`. The physics, the inputs and the
results described there are unchanged.

It is based on TomKal1/WWTools `main` at `103c26d`, read on 2026-10-31.

## The lighting pattern, which acoustics copies

| Part | Lighting today | Acoustics |
|---|---|---|
| Pure engine (no Revit / WPF / GL) | WWTools `src/WWTools/Lighting/Engine/`, on the shared calc core `src/WWTools/Calcs/Engine/` (Vec3, Polygon, ZoneFootprint, CalcLog, ReportHtml, PngImage) | **New:** WWTools `src/WWTools/Acoustics/Engine/`, namespace `WWTools.Acoustics.Engine`, on the same calc core |
| Ray casting | `Lighting/Engine/TriangleScene.cs` (BVH, `ClosestHit`) | Reused as it is. It is not moved, because moving it would churn the pinned lighting snapshot. |
| Engine tests | `tests/WWTools.UnitTests` links the engine files (`<Compile Include=".../Lighting/Engine/*.cs">`) | Same: link `Acoustics/Engine/*.cs`; tests in `tests/WWTools.UnitTests/Acoustics/` |
| Revit side | `LightingSceneBuilder`, `LightingCalcRunner`, `Commands/Lighting/*`, Luminaire Manager | `Acoustics/AcousticSceneBuilder`, `AcousticCalcRunner`, `Commands/Acoustics/*`, Sound Source Manager, Construction Manager |
| Zones and calc sets | `Calcs/ZoneCollector` (Spaces, Rooms, LinkedRooms), `ICalcSet`, `ModelStorage` | The same: `AcousticCalcSet : ICalcSet` with its own storage schema GUID |
| Exchange to BimGo | `lighting-calc-results` v1 (`docs/lighting-calcs/results-format.md`, `CalcResultsFile.cs`), written to the BimGo model folder via `BimGoFolders` / `BimGoModelFolder` | **New:** `acoustic-calc-results` v1 (`docs/acoustic-calcs/results-format.md`), written to the model folder's `acoustic-calcs\` |
| Engine in BimGo | `src/Lighting.Engine/Upstream/` byte-identical snapshot, `upstream.lock.json`, `tools/check-lighting-engine.ps1`, orchestration in `BimGo.Calculations` | **New:** `src/Acoustics.Engine/Upstream/` (only the `Acoustics/Engine` files) with its own lock and check script. It references `Lighting.Engine` for the calc core and `TriangleScene`. The adapter and job go in `BimGo.Calculations`. |

**Engine rules:**
- **Targets:** WW Tools builds for net48 (older Revit years) through net10.0-windows, so the engine must compile on
  net48. It targets netstandard2.0, like `Lighting.Engine`.
- **Language:** C# latest with nullable on; no Revit, WPF or OpenGL references.
- **Units:**
  - Geometry crosses the API in **Revit internal feet**, as the lighting engine and its results format do.
  - The physics is computed in SI inside the engine (m, m², m³, c = 343 m/s).
  - Reported areas follow the user's units (sabins as ft² or m²).
- **Hosts:** the engine never reads files or Revit. Hosts pass it plain input objects.

## Where each piece of the earlier plan now goes

| Earlier plan item | Now |
|---|---|
| AC-A Core maths (bands, A-weighting, dB sum, Sabine / Eyring, Hopkins–Stryker, NC / NR, air absorption) | WWTools `Acoustics/Engine` |
| Absorption classes, keyword rules, resolve order | Engine. The material lookup itself happens on each host's side: the WWTools Revit side, the BimGo extraction. |
| STC / Rw → TL, composite TL, door gaps, power-based transmission (`2_transmission.md`) | Engine |
| Partition pieces between rooms (Revit `SpatialElementGeometryCalculator`, `FromRoom` / `ToRoom`, `GetRoomAtPoint`) | A thin Revit query in **each** add-in: WWTools `Acoustics/AdjacencyReader`, and BimGo extraction's `room-adjacency.json`. Both produce the same engine input type. The logic that turns pieces into paths is in the engine. |
| Statistical solve (AC-C) and transmission (AC-T) | Engine, one entry point: `AcousticSolver.Solve(AcousticInput, options, progress, cancel)` |
| Ray-traced reflected field (AC-D) | Engine, later. Both tools get it at once. |
| SOUND gun, markers, map overlay, probe, weak-link colouring, CONSTRUCTIONS tab | BimGo App, as planned |
| Results CSV / report | WWTools: report (HTML → PDF like the lighting report), plan-view output and Space / Room parameters. BimGo: CSV and stills. |

## Data both tools must agree on

**Recommendation:** Revit holds the durable data. Both add-ins already read it, and BimGo's own principle is that edits
go back to Revit.

- **Type data, as shared parameters** (one shared-parameter file, bound by WW Tools' parameter tools):
  - equipment types: Lw at 63 … 8k (8 parameters, dB re 1 pW);
  - wall / door / window / floor / ceiling types: STC or Rw, CAC on ceilings, and optional TL at 63 … 8k;
  - doors: optional undercut / gap;
  - materials: optionally α at 63 … 8k, or an NRC class.
  - Exact names are a decision.
  - BimGo reads them at extraction. Its CONSTRUCTIONS and SOURCES edits are first kept as BimGo overrides. Later they
    can be pushed to Revit through a new live / journal operation, "set type parameter".
- **Calc sets stay per tool:**
  - WW Tools stores zones, which sources are on (duty / standby), and the settings (field allowance, receiver height,
    grid, quality, targets) in `ModelStorage`. BimGo keeps its own JSON.
  - Both use the **same settings class from the engine**, so values mean the same thing, and the results file records
    them.
- **Sources BimGo places on non-equipment surfaces** stay BimGo-only until there is a Revit object to hold them.
  In WW Tools, sources are equipment instances whose type has Lw data.

## Results exchange: `acoustic-calc-results` v1

This is modelled on `lighting-calc-results`: a JSON header plus a little-endian float32 body, the `.bin` written
before the `.json`, and `binBytes` checked. The spec goes in WWTools `docs/acoustic-calcs/results-format.md`,
written before either the writer or the reader.

- **Header:**
  - format / version / tool, the calc set, the model, the run;
  - units: length ft, revit-internal coordinates, dB re 20 µPa;
  - **settings** (field allowance, method: statistical or traced, receiver height);
  - **zones:** key, kind, number, name, level, loops, stats (Lp dB(A) avg / max, NC / NR, RT per band), target and result;
  - **sources:** element key, type, Lw[8], position;
  - **paths:** source zone → receiving zone, composite TL[8], level, and the share per piece with element keys;
  - **warnings:** unrated pieces, estimated 63 Hz values, fallbacks used.
- **Body:**
  - receiver points: x y z, Lp[8], dB(A);
  - optionally partition pieces: triangle, share, TL at 500 Hz, so BimGo can colour weak links without recomputing.
- **Where it is written:**
  - WW Tools: the report folder, plus `<BimGo model folder>\acoustic-calcs\<set>_<id>.json/.bin`.
  - BimGo's own runs: the same format in the same folder. **One display path in BimGo for both.**

## Build order (replaces `0_plan.md`'s list; same physics)

| Build | Repo | What |
|---|---|---|
| **W1** | WWTools | Engine core. Bands, weighting, dB sum, Sabine / Eyring, Hopkins–Stryker with a line-of-sight direct term (`TriangleScene`), STC / Rw → TL, composite TL and gaps, power-based transmission, NC / NR. Input / output types and the shared settings class. `docs/acoustic-calcs/README.md` (method) and `results-format.md`, plus the writer. All the tests from `0_plan.md` and `2_transmission.md`. |
| **W2** | WWTools | Revit side. Sound Source Manager (Lw per equipment type from parameters, or entered), Construction Manager (STC / Rw / CAC per type, missing ones in red), material α. `AdjacencyReader`. The Acoustic Calc command: a calc set over `ZoneCollector` zones, a run, plan-view output, levels on Space / Room parameters, the report, and results to the BimGo folder. |
| **B1** | BimGo | Viewer. Read `acoustic-calc-results`, draw the map, room levels, weak links and the probe; reload live (as LC-B). WW Tools users see results in the walkthrough. |
| **B2** | BimGo | Native calc. Pin the engine snapshot (`src/Acoustics.Engine`, lock, check script). Extraction adds `room-adjacency.json` and the acoustic parameters. An adapter in `BimGo.Calculations` (scene metres → feet, room keys) runs on a worker. Add the SOUND gun, SOURCES / CONSTRUCTIONS tabs and what-ifs. Output is the same results file, shown by B1. |
| **W3 / B3** | Both | The ray-traced reflected field and decay-curve RT in the engine. Then re-pin in BimGo. |

This is the order lighting took: the WW Tools engine first, then the BimGo viewer, then BimGo's own calc on the pinned
engine.

## Decisions needed (before W1)

1. **Licence and home of the engine.**
   - **The problem:** WWTools is under an "Internal Use License, all rights reserved (Thomas Ames and Wiley &
     Wilson)". BimGo is MIT-0. `src/Lighting.Engine/UPSTREAM-NOTICE.md` already flags that a pinned copy does not
     relicense it.
   - **Choice 1:** accept the same arrangement for acoustics.
   - **Choice 2:** give the acoustics engine an explicit licence grant, or its own repo, that both tools consume. As
     this is new code, now is the cheapest time to choose.
2. **Data provenance.** Airflow went on hold over ASHRAE tables (`docs/airflow-calcs/README.md`). The acoustics engine
   needs:
   - NC curves (ANSI / ASA S12.2) and NR curves;
   - the STC / Rw contour (ASTM E413, ISO 717-1);
   - absorption tables.
   Confirm the terms for using each. Prefer published formulas (A-weighting per IEC 61672, ISO 9613-1 air absorption)
   and manufacturer data. Keep every table in one file, with its source cited.
3. **Shared parameter names** for Lw[8], STC / Rw / CAC / TL[8], the door gap and α[8].
4. **Field allowance default** (0 dB with a warning, or a deduction such as 5 dB). It is now a shared setting.
5. **Zones:** WW Tools already does Spaces, Rooms and LinkedRooms. BimGo extracts Rooms only. Add Spaces to BimGo's
   extraction for B2 if MEP models use Spaces.
