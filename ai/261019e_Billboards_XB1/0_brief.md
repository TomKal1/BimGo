# Round X, build X-B1: brief (2026-10-19)

Baseline: X-A as sent (Gavin: "perfect"; no changes in his copy).

Gavin's ideas for the rest of Round X: billboard proxies (face-the-camera silhouettes, up / down scaled, placed fresh,
colours overridable globally, to populate scenes and swap out with AI later; a mesh proxy is overkill); 4D left out (too
narrow); better reflections (as an option, or for stills); IES light profiles; a time-lapse along a path isn't needed.

## Answers (AskUserQuestion, 2026-10-19)

| Question | Answer |
|---|---|
| Baseline | As sent; **two-way camera sync worth keeping** |
| Order | **Billboards → reflections → IES → camera sync** (recommended) |
| Billboards | **Built-in procedural library**, **swap Revit planting / entourage**, **import your own PNGs**, **textured look too**, and Gavin's own: "billboards should be placeable but also able to swap all of a type or all of an instance, and be remembered to do so in case a user re-runs bimgo down the line" |
| Reflections (X-C) | **SSR live + planar for stills** (recommended) |

Split (big round, near the usage limit): **X-B1** the billboards themselves (library, placing, look, own pictures,
storage); **X-B2** Revit Planting / Entourage swapped per instance or per type, the rules remembered in the model folder
for every later Go / F5 / Export.

Decided without asking: placing lives in the Place gun (an ENTOURAGE mode, the wheel switches; the default without a
family library) and an ENTOURAGE panel (pause menu); keys reuse Z / X / R (no new actions); silhouette colours per
category (trees, shrubs, people, cars, other) with an architectural default palette; billboards always face the camera.
