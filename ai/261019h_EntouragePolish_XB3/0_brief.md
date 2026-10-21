# Round X, build X-B3: entourage polish (brief, 2026-10-19)

From the handoff `ai/261019g_Handoff/0_handoff.md` §3 (answers given there); Gavin: no working-copy changes since the
B2 zip; X-B1 + X-B2 confirmed; "assume all default recommended answers" (away during the build).

| Question | Answer |
|---|---|
| Dynamic rescale | Live resize the aimed billboard: hold a key + wheel scales it smoothly with a height readout (Z / X 10 % steps stay) |
| Outlines | Refine the procedural people / trees now; Gavin's traced drawings replace them later |
| Drawings format | Filled silhouettes (black on white; photo / scan or tablet PNG) |
| Baseline | The B2 zip as sent |

Agreed without a question: IMPORT FOLDER… (a licensed pack the user owns, one type per picture, categories from the
names); third-party packs are never bundled (only Gavin's own drawings, credited, or vetted CC0); card counts include
swapped Revit billboards.

Defaults taken while Gavin was away (recommended options):
- The resize modifier is a new action **`HoldScale`, default Alt** (Shift + wheel already switches guns, Ctrl crouches
  and would move the aim); 3 % a notch, Z / X held for more than 0.35 s scale smoothly (60 % a second), saved once on
  release; works on Revit-swapped billboards too (an instance rule's `height`).
- A **gum tree** added to the built-ins (Australian projects; additive key `tree-gum`).
- Traced drawings: vector rings in Core `EntourageShapes` (empty until the drawings arrive), the tracing script in
  `tools/entourage/` (Python; run here when Gavin sends drawings).
- IMPORT FOLDER… reads the folder itself only (not sub-folders).
- Not this build: a per-built-in "planted at" height field, an exact-height text box per billboard, a detail
  (inner lines) layer for traced figures.
