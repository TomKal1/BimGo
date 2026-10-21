# Y-D1: scale, part 1: measure, then occlusion culling

Round Y item 3 "Scale" (plan: `ai/261020e_Plan/0_plan.md`), split in two builds:
- **Y-D1 (this):** a performance readout (GPU time per stage, chunks, triangles) and occlusion culling of the main view
  with GPU occlusion queries.
- **Y-D2 (next):** small-detail culling and simplified meshes for distant chunks, in the main view and the shadow passes.

Gavin's answers (2026-10-21):

| Question | Answer |
|---|---|
| Target model size | No slow model yet: "pick a reasonable assumption". Assumed: a big building with links, ~10–30 M triangles, 60 fps on a mid-range GPU |
| Occlusion culling | GPU occlusion queries (recommended) |
| Level of detail | Small-detail culling + simplified meshes (recommended): Y-D2 |
| Coverage | Main view + shadows (recommended): occlusion is the main view only (a camera's view says nothing about the sun's); Y-D2's LOD covers the shadows |

Where it starts: the static scene is already in chunks (≤ 48 elements, ≤ 96k indices, ≤ 14 m across, per category and
model), frustum-culled and multi-drawn per batch (`SceneBatches`, `SceneRenderer.DrawBatches`). GL 3.3 core: occlusion
queries, conditional rendering and timer queries are there; indirect draws and compute aren't.
