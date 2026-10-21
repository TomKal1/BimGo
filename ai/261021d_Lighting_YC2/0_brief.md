# Y-C2: lighting polish (the app): per-fixture profiles and polar plots

Round Y item 2, second half (plan: `ai/261020e_Plan/0_plan.md`; handoff `ai/261021c_Handoff/0_handoff.md` §3).
Y-C1 r2 (longer light reach) confirmed by Gavin 2026-10-21; this build starts from the Y-C1 r2 zip.

Gavin's answers:

| Question | Answer |
|---|---|
| Per-instance profiles | Aim at a fixture + a key: a small dock with its profile, ‹ › and RESET; the type's row in LIGHTS counts the overrides (2026-10-21) |
| Previews | Polar candela plot (two planes) drawn with UI lines in the LIGHTS tab (2026-10-21) |
| The key | Ctrl+K (recommended; K = lights, Shift+K = the path editor) |
| A clone of a fixture with its own profile | Gets the original's (recommended): clones follow their original |

Scope: per-fixture choices in `light-profiles.json` (additive), resolve fixture → type → Revit → lobe, the Ctrl+K dock with
the fixture highlighted, the LIGHTS tab's override counts / RESET ALL / small plots, tests.
