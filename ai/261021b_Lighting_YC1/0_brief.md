# Y-C1: lighting polish (Revit and the engine)

Round Y item 3 (plan: `ai/261020e_Plan/0_plan.md`; X-D1's "not done" list), split in two builds:
- **Y-C1 (this):** Revit spot / hemispherical distributions as generated profiles; IES tilt data (TILT=INCLUDE)
  applied; live lights ranked by contribution instead of distance.
- **Y-C2 (next):** per-instance profiles; profile preview pictures in the LIGHTS tab.

Gavin's answers (2026-10-21):

| Question | Answer |
|---|---|
| Y-B | Works; the Y-B zip is the working copy |
| Per-instance profiles (Y-C2) | Aim at a fixture + a key: a small dock with its profile, ‹ › and RESET; the type's row in LIGHTS counts the overrides (recommended) |
| Profile previews (Y-C2) | Polar candela plot (two planes) drawn with UI lines (recommended) |
| Ranking live lights | Output ÷ distance², with a margin so lights don't swap shadow maps back and forth (recommended) |
