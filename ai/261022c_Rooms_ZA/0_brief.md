# Z-A brief: room data overlay (Round Y item 4, Revit round-trip part 1)

Agreed 2026-10-22 (Gavin away for the build: recommended options taken where open).

- **Room data from extraction:** every non-empty room **instance parameter** (not a picked list), with each room's
  UniqueId, ElementId, level name and Revit's area. Numbers in the model's display units, so "Area" and a
  "Required area" compare directly.
- **Floating room tags:** number · name at the room's centre (its most open point), a set height above the floor;
  up to four picked parameters underneath. A key toggles them (N).
- **Target checks:** a value parameter (default Area) against a target parameter (e.g. "Required area") with a ± tolerance %:
  green inside, amber within twice the tolerance, red beyond; on the tag and as a floor tint. Rules: within ±, at least,
  at most.
- Setup on FIND ROOM's new ROOM DATA tab; saved with the model (visibility.json beside the grids).
- **Comment markers (Z-B, next):** Gavin authors the family once this build specs it: **adaptive component, one point**
  (Gavin's choice: easiest to move), the BimGo id baked in so BimGo can update / move / remove markers. Spec:
  `2_marker_family_spec.md`; shared parameters: `tools/revit/BimGo_SharedParameters.txt`.
