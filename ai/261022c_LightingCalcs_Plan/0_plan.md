# Lighting calc results in BimGo: plan (TomKal1 fork)

Fork-only work (TomKal1/BimGo), not one of Gavin's rounds. The full plan, covering both tools, is in the WW Tools repo:
`docs/lighting-calcs/bimgo-viewer-plan.md` (TomKal1/WWTools). This note covers the BimGo half, so a BimGo session can
start from here.

**Goal:** WW Tools' Lighting Calc writes its solved results (radiosity patches, calc points, isolux lines, zone stats)
to a tool-neutral exchange file. BimGo shows them in the walkthrough as an ElumTools-style calc view: surfaces in
false-colour illuminance or luminance, points with values, a legend and per-zone stats. BimGo's own IES-shaped
lights (X-D1 / Y-C1 / Y-C2) are the realistic view beside it.

## Sync first
- The fork's `main` equalled upstream `main` on 2026-10-22. Upstream `develop` was 8 commits ahead and was merged
  into `claude/optimistic-cori-g3qor9` with this plan.
- Every BimGo session: `git fetch upstream && git merge upstream/develop` before editing.
- Keep the work in new files with small hooks, so later merges stay clean.

## Exchange file (read-only for BimGo)
- `<model folder>/lighting-calcs/<set>_<id>.json` + `.bin`. In a `.bimgo` file they are optional `lighting-calcs/*`
  entries (additive, format 1).
- The JSON header: format `lighting-calc-results` v1, the calc set, the run, units (ft, Revit internal, fc), the
  colour scale, and zones with stats, target, outline and byte ranges.
- The little-endian float32 body:
  - points: x y z E Edirect;
  - patches: a b c, the normal, E, ρ, flags;
  - isolines.
- Scene metres = feet × 0.3048 − `SceneData.OriginOffset`. Luminance = ρE/π. Show lux when units are metric.

## Builds (BimGo side = plan Phase B, then C)
1. **Core:**
   - `Format/LightingCalcFiles.cs`: reader; validates offsets and counts; never throws.
   - `Scene/LightingCalcs.cs`: models, unit conversion, per-vertex colours averaged at shared corners, the
     blue → red scale and luminance grey.
   - `ModelFolders.LIGHTING_CALCS_FOLDER`.
   - `tests/BimGo.Core.Tests/LightingCalcTests.cs`.
2. **App:**
   - `Rendering/CalcOverlay.cs`: a static VBO like `GridOverlay`, lifted 1–2 cm along the normals.
   - `Game/GameSession.LightingCalcs.cs`:
     - load at start and on F5;
     - poll the folder every 2 s and toast on changes;
     - modes Off / Illuminance / Luminance / Points;
     - legend;
     - zone stats in the status panel.
   - A CALCS section in Pause → LIGHTS.
   - One new `InputAction` (`DefaultsChanges` row, `DEFAULTS_VERSION` 7 → 8).
3. **`.bimgo`:** embed on Export / Save as (skip embedding over 100 MB, and say so).
4. **Phase C:** read `lighting-calcs/open.json`, written by WW Tools' *View in BimGo* button before it posts
   `Cmd_Launch`, to pick the calc set and mode and start in a zone.

## To confirm at kickoff
UI place (LIGHTS section recommended), the key, the default display (off), and whether to offer the generic part
upstream to Gavin later.

Not compiled: no .NET SDK here. Follow the latest handoff §5 checks (script lexer, font atlas, WebGL2 shader compile).
