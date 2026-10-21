# Fork build LC-B: lighting calc results in BimGo (brief)

Fork-only work (TomKal1/BimGo), not one of Gavin's rounds. The plan is `ai/261022c_LightingCalcs_Plan/0_plan.md`, and
in full in the WW Tools repo (`docs/lighting-calcs/bimgo-viewer-plan.md`). This build is the plan's Phase B, the BimGo
side of Phase C (the "View in BimGo" request), and the BimGo half of the shared tests.

## Kickoff decisions (the plan's recommended options, taken as asked)
1. **UI:** a LIGHTING CALCS section at the top of Pause → LIGHTS (not a new tab or a study mode).
2. **Key:** Shift+J ("Lighting calc results: off / illuminance / luminance / points"), beside J (the sun and daylight
   study); free in every situation that has the Tools keys. `DEFAULTS_VERSION` 7 → 8.
3. **Default:** off until chosen (Shift+J, a set picked in CALCS, or WW Tools' View in BimGo). An F5 refresh keeps the
   view.
4. **Upstream:** kept in the fork for now. The reader, models and overlay are tool-neutral ("lighting calc results"),
   so they could be offered to Gavin later. Ask first.
5. **Bundling** WW Tools with BimGo: not decided; BimGo stays a separate install.

## Rules kept
- Core has no GL or UI code; App never references the Revit API; `.bimgo` stays format 1 (optional entries).
- New files, with small marked hooks ("fork build LC-B") in shared ones, so upstream merges stay easy.
- No new packages. No per-frame allocations (the overlay is a static buffer; labels and legend text are cached).
- No exceptions reach the user (the reader never throws; failures are toasts and log lines).
