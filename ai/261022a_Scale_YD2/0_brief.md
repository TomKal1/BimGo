# Y-D2: scale, part 2: the cost of artificial lights

Round Y item 3 "Scale" (plan: `ai/261020e_Plan/0_plan.md`; Y-D1: `ai/261021e_Scale_YD1/`). Y-D1's numbers on Gavin's
1.5 M-triangle model showed per-pixel artificial lighting, not triangles, is the cost: at one spot the opaque pass was
15.6 ms with lights on and 2.1 ms with them off (~220 k triangles drawn either way); the frame ~20 ms with culling.

Gavin's answers (2026-10-22):

| Question | Answer |
|---|---|
| Focus of the rest of Scale | Shading cost (recommended); simplified meshes later, for really big models |
| Occlusion culling default | Auto (recommended): Y-D1 r2 / r3 |
| Main fix | Depth pre-pass + light LOD (recommended) |
| Quality setting | Low / Medium / High (recommended): shadowed lights 8 / 16 / 32, shadow taps 1 / 1–4 / 4; photos and videos High |
| Target | ≤ 11 ms (90 fps) at the test spot, lights on (recommended) |
