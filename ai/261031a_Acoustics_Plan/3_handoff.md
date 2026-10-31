Start the acoustics calculation engine (build W1) in TomKal1/WWTools. It must run in WW Tools without BimGo, like the Lighting Calc, and BimGo will pin a copy of it later.

This session needs both repos: TomKal1/WWTools (where the work goes, on a new branch from main) and TomKal1/BimGo (read only). Read these, on BimGo branch ccr-2eb0edf5-fmmjmc, in this order:
- `ai/261031a_Acoustics_Plan/4_shared_engine.md`: where the code lives, the build order (W1 → W2 → B1 → B2 → W3 / B3), the data the tools share, and the results format.
- `0_plan.md` and `2_transmission.md` in the same folder: the physics. This covers the statistical method with a line-of-sight direct term, and transmission from STC / Rw / TL with composite partitions.
- In WWTools: `docs/lighting-calcs/README.md` and `results-format.md`, `src/WWTools/Calcs/Engine`, `src/WWTools/Lighting/Engine` (especially `TriangleScene`), and how `tests/WWTools.UnitTests` links the engine files.

W1 scope: pure engine, no Revit and no UI.
- **Engine location:** `src/WWTools/Acoustics/Engine/`, namespace `WWTools.Acoustics.Engine`.
  - It must compile on net48 (netstandard2.0-compatible).
  - Nullable on.
  - It reuses `WWTools.Calcs.Engine`, and `TriangleScene` where it is.
  - Geometry crosses the API in Revit feet; the physics runs in SI.
- **The maths:**
  - octave bands 63–8k, A-weighting, dB addition;
  - Sabine / Eyring, Hopkins–Stryker with a direct term blocked by line of sight;
  - air absorption;
  - NC / NR;
  - STC / Rw → octave TL;
  - composite TL with door gaps;
  - power-based transmission to neighbouring zones, with each piece's share.
- **Types and entry point:** input / output types, a shared settings class (field allowance, receiver height, grid, method), and `AcousticSolver.Solve(input, options, progress, cancel)`.
- **Results format:** `docs/acoustic-calcs/README.md` (the method), then `docs/acoustic-calcs/results-format.md` (`acoustic-calc-results` v1, modelled on lighting's), then the writer.
- **Tests** in `tests/WWTools.UnitTests/Acoustics/`:
  - free field −6 dB per doubling of distance;
  - two equal sources +3 dB;
  - Sabine shoebox;
  - composite TL 41.4 / 34.5 dB for the door and undercut examples;
  - Lp₂ against the hand formula;
  - path shares sum to 100 %;
  - "STC 50" parsing;
  - a missing rating leaves the path out;
  - writer round-trip.

Rules:
- Don't change `Lighting/Engine`: BimGo pins it byte for byte.
- Missing ratings are never guessed.
- **Data provenance:** Airflow was put on hold over ASHRAE tables. Prefer published formulas. Put every table (NC / NR, the STC / Rw contour, absorption) in one data file with its source cited, and list them in the notes as pending a terms check.
- If a .NET SDK isn't available, say so; don't claim tests pass.

Open decisions (`4_shared_engine.md`). Ask me about 1 and 2 before W2 / B2. Until then, use these defaults:
1. **Engine licence / home:** the same arrangement as lighting.
2. **Table terms:** as above.
3. **Shared parameter names:** proposed in the docs, not bound yet.
4. **Field allowance:** 0 dB with a warning.
5. **Zones:** `ZoneCollector`'s Spaces / Rooms / LinkedRooms.

Commit and push the WWTools branch. Don't open a PR unless I ask.
