# Y-D1 build notes: performance details, occlusion culling

Not compiled here (no .NET SDK). Brackets, strings and char literals script-checked in every changed `.cs` file, UI
strings against the font atlas. The query pass checked in headless WebGL2 (SwiftShader): a wall drawn, then a chunk box
behind it and one in front queried with colour / depth writes off and `LEQUAL` → behind 0 samples, in front 1, no GL
error (the box table `BOX_FACES` is the one the renderer uses).

## What's in
- **Occlusion culling of the main view** (`SceneRenderer.Occlusion.cs`, Core `OcclusionState`).
  - After the opaque scene, the moved / cloned elements, the section caps and the ground, every chunk in view that is
    due gets an `ANY_SAMPLES_PASSED` query of its box grown by 0.3 m, against the depth drawn so far (colour and depth
    writes off; the shadow depth program; all boxes in one buffer, 36 vertices a chunk, so a query is one draw).
  - At the start of the next frame the answers that are in decide what the **opaque pass, the AO / glow pre-pass and
    the transparent pass** skip (`DrawBatches(…, skip)`). The CPU never waits: an answer not in yet keeps its chunk drawn.
  - Due: hidden chunks every frame (they come back a frame after they show); visible ones every 4th frame, staggered.
  - Never hidden: a chunk whose box (grown by 1.5 m) holds the camera (checked at the start of the frame too).
  - Shown again: everything after a camera jump (over 1.5 m or 25° in one frame: teleport, bookmarks, portals, the
    tour's glides); a chunk when it leaves the view or its category / link is hidden (its pending answer is ignored).
  - Only the main view culls: photos, videos, clash view and clash pictures, planar / probe reflections, the minimap,
    shadows and light shadows draw as before. Glass chunks are culled too (their boxes against the opaque depth).
  - Options → Display → **Occlusion culling** (on by default; `LaunchSettings.OcclusionCulling`). Turning it on shows
    everything until the queries answer. A GL error setting it up switches it off for the session (logged; the option
    says so). The log line at first use: chunks, triangles, box buffer size.
- **Performance details** (Options → Display; off by default; `LaunchSettings.ShowPerformance`): a panel under the
  status panel (and coordinates): GPU time of the main view per stage (shadows, AO / glow, reflections, opaque,
  occlusion, glass / rest; `TIME_ELAPSED` queries one after another, read two frames later, smoothed) and their total;
  chunks in view and culled; triangles drawn of the model's; multi-draws; queries this frame; chunks in the model. No
  queries are made while it's off. The minimap no longer counts in the draw stats.
- **Display tab** returns its height (the tab scrolls when the two new rows don't fit).
- **GL:** `GenQueries`, `DeleteQueries`, `BeginQuery`, `EndQuery`, `GetQueryObject` (uiv) wrappers and their entry points.
- **Tests** (`OcclusionStateTests`): start visible, answers hide / show, one query at a time; hidden every frame and
  visible staggered (each once in 4 frames); late answers keep the chunk drawn; forgetting ignores the pending
  answer; camera jumps (move and turn) show everything; the near box; the settings' defaults (older files cull).

## What isn't
- Small-detail culling, simplified meshes, LOD in the shadow passes: Y-D2.
- A chunk coming into sight from behind a wall shows one frame late (two if the GPU runs a frame behind): the margin,
  the near rule and the jump rule hide most of it. Opening a section cut or hiding a wall reveals what's behind it a
  frame late the same way.
- Moved / cloned elements and billboards aren't culled (few; drawn as before). Shadow passes don't use occlusion.
- No CPU frame-time split (the FPS readout's ms is the whole frame).

## How to check
1. Esc → Options → Display: tick **Performance details**; **Occlusion culling** is ticked. Close.
2. The panel under the status panel shows GPU ms per stage and their total, "Chunks N in view · M culled", "Triangles
   … of …".
3. Stand inside a room with closed walls: "culled" should be most of the chunks in view, triangles drawn far below
   the model's, and the GPU opaque time lower than with culling off (untick in Options, compare; tick again).
4. Walk through a door and turn quickly round corners: nothing should be missing for more than a blink. Report any
   spot where something stays missing.
5. Teleport (Portal gun / bookmark / H): no missing walls on arrival.
6. Section box: cut into the building: what's revealed appears at once (or a frame late).
7. Hide a category (pause → VISIBILITY) and show it again: it comes back whole.
8. Photo (M → TAKE PHOTO), a video, clash view and clash pictures: whole, as before (they don't cull).
9. Glass: curtain walls behind solid walls culled, in front of you drawn.
10. Untick Performance details: the panel goes (no timer queries). Settings keep both choices after a restart.
11. Log (`%LOCALAPPDATA%` BimGo log): "Occlusion culling: N chunks, … triangles, box buffer … KB."
12. Tests: `OcclusionStateTests` pass.

Send the panel's numbers from your biggest model (inside a room and outside looking at the building): they set Y-D2's
LOD thresholds.

## Revision r2 (2026-10-22): cheaper queries, Auto

Gavin's numbers (1.5 M-triangle model, 1,935 chunks):

| | Culling on | Culling off | On, lights on | On, lights off |
|---|---|---|---|---|
| Triangles drawn | 110 k | 1.3 M | 223 k | 216 k |
| Opaque | 8.7 ms | 10.6 ms | 15.6 ms | 2.1 ms |
| Occlusion queries | 2.2 ms (1,519) | – | 1.2 ms (882) | 1.3 ms (853) |
| GPU total | 13.9 ms | 14.0 ms | 21.5 ms | 7.4 ms |

Culling removed 92 % of the triangles but the queries cost about what it saved; and **artificial lights cost ~13.5 ms
of the opaque pass** (per-pixel lighting, not triangles). Decided (2026-10-22): Y-D2 goes after **shading cost**
(simplified meshes later, for really big models); occlusion culling defaults to **Auto**.

- **Front faces only:** the boxes are wound counter-clockwise from outside (`BOX_FACES`, checked: every face's normal
  points out) and the queries cull back faces (half the fill). Safe because the camera is never inside a queried box
  (the near rule: boxes grown by 1.5 m; the margin is 0.3 m).
- **Clusters** (Core `OcclusionState.BuildClusters`, `SetClusters`, `ClusterDue`…): chunks grouped by a 16 m grid and
  4 m slabs (across categories and links). When two or more of a cluster's chunks are in view, all hidden, none with a
  query out, one query of the cluster's box (the union, grown by the margin) stands in for theirs. No sample: they stay
  hidden. Any: all shown (their own queries then hide the ones still behind something). Late: shown meanwhile. Not
  when the camera is at the cluster. Cluster boxes follow the chunk boxes in the same buffer; one more query object each.
- **Off / Auto / On** (Options → Display → Occlusion culling; `LaunchSettings.OcclusionMode` 0 / 1 / 2, default 1;
  the r1 `OcclusionCulling` key is dropped and read as Auto). Auto culls while last frame's triangles in the view
  frustum (drawn or culled: `TrianglesInView`) are ≥ 1 M, and stops under 700 k. Starting to cull shows everything
  until the answers come. The panel says "(auto: off)" / "(off)" when not culling.
- **Tests:** clusters (cell / storey grouping, members), cluster stand-in (due only when all in-view chunks are hidden
  and none pending, at least two; no sample keeps them hidden, any shows all, late shows), settings (Auto default,
  older file → Auto, clamped).

### Check r2
1. Same spot as before, Performance details on, Occlusion culling **On**: "queries" should be well under the chunk
   count when most are culled, and the "occlusion" ms lower than r1's 1.2–2.2 ms.
2. **Auto**: in your 1.5 M model it culls while ≥ 1 M triangles are in the view; look at a small part of it (under
   700 k): "(auto: off)", queries 0.
3. Walk fast round corners and through doors: nothing stays missing for more than a blink (a cluster answer shows a
   whole cluster at once, so if anything it should pop less).
4. Send the r2 numbers (On, lights on) from the same spot as r1.

## Revision r3 (2026-10-22): Auto from 250 k triangles

r2 measured at Gavin's spot (lights on): culling **On** 0.3 ms of queries (374), opaque 16.5 ms, GPU 20.5 ms; **Auto**
stayed off there (834 k triangles in view, under r2's 1 M) with opaque 18.3 ms, GPU 23.9 ms. So the cheaper queries pay
well below 1 M: Auto now culls from **250 k** triangles in view and stops under **150 k** (`AUTO_ON_TRIANGLES`,
`AUTO_OFF_TRIANGLES`). The Options note says so.

Check: same spot, **Auto**: it should now cull (no "(auto: off)"), with the On numbers.
