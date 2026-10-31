Start the acoustics feature in TomKal1/BimGo (fork-only work).

Branch from `ccr-2eb0edf5-fmmjmc`, which holds the plan notes. First read these, then DEVELOPING.md's "For AI assistants" section:
- `ai/261031a_Acoustics_Plan/0_plan.md`: sound sources placed on equipment, and a sound pressure heat map. Statistical first, then hybrid ray tracing.
- `ai/261031a_Acoustics_Plan/1_market_research.md`: positioning as MEP plant-room noise screening. Record time-resolved energy in the tracer.
- `ai/261031a_Acoustics_Plan/2_transmission.md`: levels in neighbouring rooms from STC / Rw / octave TL per wall, door, window and floor / slab type, and CAC for ceilings.

Build order: AC-A → AC-B → AC-C → AC-T → AC-D. Start with **AC-A** (Core only, no UI):
- `Scene/Acoustics.cs`:
  - octave bands 63–8k and A-weighting;
  - dB addition;
  - Sabine / Eyring;
  - Hopkins–Stryker;
  - NC / NR;
  - air absorption.
- `Scene/AcousticMaterials.cs`:
  - an octave-band α table, with sources cited;
  - keyword rules;
  - the resolve order: override → parameter → keyword → category.
- The STC / Rw → octave TL contour, composite TL with door gaps, and the transmitted level (the maths from `2_transmission.md`).
- `Format/AcousticFiles.cs`: `acoustic-sources.json`, `acoustic-materials.json`, `acoustic-constructions.json`.
- Tests in `tests/BimGo.Core.Tests`:
  - free field −6 dB per doubling of distance;
  - two equal sources +3 dB;
  - a Sabine shoebox;
  - the composite door / undercut examples (TL 41.4 and 34.5);
  - rating parsing ("STC 50");
  - document round-trips.

Rules:
- Own MIT-0 code. Do not touch or depend on `src/Lighting.Engine` (separate copyright, pinned snapshot).
- Follow the repo's code style: XML docs, no per-frame allocations, no exceptions shown to the user.
- No fallback for older .bimgo exports: they are test files, and a re-export is fine.
- Missing STC ratings are never guessed: the path is left out and listed as a warning.
- Revit API calls can't be compiled in the cloud session: list them in the build notes for verification on Windows.

Open decisions. Ask me before AC-T; until then use these defaults:
- field allowance: 0 dB, with a warning;
- Rooms only (no MEP Spaces);
- STC parameter names: "STC", "STC Rating", "Rw", "CAC";
- ratings stay in BimGo (no write-back to Revit).

Write the round's notes in `ai/` (e.g. `2611xx_Acoustics_AC-A/`), commit, and push.
