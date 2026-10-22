# Lighting designer feedback — October 22, 2026

These changes belong to **BimGo**, based on its Tom-Main lighting workspace.
BimGo-Internal was not modified. They can be reviewed and brought into that
internal fork later.

Native lighting set controls now include:

- **Emergency fixtures only**, with instance selection, participation, output
  percentage and an explicit source height offset. Percentage multiplies normal
  maintained output (LLF is still applied); normal mode uses every included
  fixture at normal output. Different fixtures of the same type, linked instances
  and clones have separate settings. Zero percent contributes no emergency light.
- Automatic under-furniture point exclusion, enabled by default. It tests actual
  furniture triangles above/at the workplane and leaves every retained point at
  its original height. Genuine dark, unobstructed points remain in statistics.
- **Pick points to exclude in ISO** on the zone/grid tab. Click a visible native
  calculation point or value in review, then Finish excluding. Manual exclusions
  are stored by room and XYZ in internal feet; the restore action clears them.
  Recalculate to refresh the results (automatic mode will queue a run).
- Calculation-only element picking, excluded-category toggles, and restoring an
  individually excluded element. These are physical calculation choices,
  independent of display hiding and section cuts. Excluded fixtures do not emit;
  excluded surfaces neither block nor reflect.
- **Rerun current/review room only**. First run the full set in the current
  session. A partial rerun solves just the selected room and retains the other
  rooms' values/surfaces. Reports flag this explicitly and record each room's
  original input revision. Retained results are embedded in the canonical input
  so the mixed revision can replay offline. A full run from freshly exported
  scene input replaces all cached room results. Rerun all after changes that
  affect other rooms.

New sets leave **Correct recessed sources** off. New exports keep the family/nested light-source origin separately from the
preview lens position. Calculations use that origin by default; the fixture
selector's Use preview lens position switch restores the visual position.
Older exports retain their original position with a re-export warning when the
origin metadata is absent. No fixture bounding box is used to guess an origin. Explicit
source height offsets are available in the fixture selector. Existing saved sets
that explicitly enabled recessed correction retain that choice.

The selection helper is byte-identical to WWTools'
`Lighting/Engine/CalculationSelection.cs`; it lives outside the 37 pinned
`Lighting.Engine/Upstream` files, whose source blobs remain unchanged. The
WWTools comparison PDF guided the changes; numerical equivalence to ElumTools
requires the original RVT/IES inputs and matched settings, not the PDF alone.

Validation: BimGo compiles with the new controls, and new regression tests cover
emergency fractions, same-type instance selection, physical visibility, source
height overrides, point exclusion persistence and partial-run replay. The full
suite retains its existing Linux-specific SunStudy filename failure. The saved
lighting corpus verifier also reports a golden-input SHA mismatch already
present in the branch's unmodified scene fixture; no baseline outputs were
regenerated to conceal it. Interactive picking still needs a Windows/Revit
smoke test with the original model.

Workspace results: 565/566 tests pass; the remaining test is the existing
SunStudy Windows filename assumption on Linux. All six new cases pass, including
exported-origin roundtrip and exact replay of a partial run. The app and Revit
2027 exporter compile; the pinned solver's 37 source hashes still match its
manifest.
