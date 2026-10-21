# BimGo: handoff for Round Y after Scale (Y-D2 confirmed): next, the Revit round-trip

**Context:** BimGo is Gavin's personal project: publisher **Aussie BIM Guru**, licence **MIT-0**; C# / Visual Studio,
.NET 8; its own OpenGL renderer over Silk.NET (GL 3.3 core); a Revit add-in (`BimGo.Revit`, Revit 2025 / 2026 / 2027), a
standalone app (`BimGo.App`) and `BimGo.Core`.

How Gavin works:
- **Rounds:** discuss → AskUserQuestion (recommended option first and marked, at most 4 questions per call; he sometimes
  types his own answer: follow it) → build → zip.
- **Notes:** each build gets `ai/<yymmdd><letter>_<topic>/` (`0_brief.md`, `1_build notes.md`), a README §8 to-verify
  entry (§8 holds only open builds: drop one once Gavin confirms it), a §9 row (keep the row's last cell inside the
  closing `|`), and a project doc `claude/BimGo_<Round>_BuildNotes.md`. Send the zip with SendUserFile
  (`/mnt/user-data/outputs/BimGo_<Topic>.zip`, zipped from the repo root: LICENSE, README.md, THIRD-PARTY-NOTICES.txt,
  ai, src, tests, tools).
- **Each build says what is in, what isn't, and how to check it step by step.** Send test renders when visual.
- Simple build / test errors and small requests: **just the fix** (a revision section in the build notes, re-zip).
  "Assume recommended options" when he's away. When he reports a bug with screenshots / logs, add log lines that settle
  it if the first fix doesn't. He measures with Options → Display → Performance details and sends screenshots.
- **Ask whether his working copy changed** before editing; if so, wait for his zip. He is often near his daily usage
  limit: split big rounds into builds that each zip on their own.
- New input actions need a `DefaultsChanges` row (bump `DEFAULTS_VERSION`, now **7**) or
  `ControlsTests.DefaultsChanges_MatchTheDefaults` fails; README §3's generated row by hand (format as `KeyMap.DefaultsMarkdown`).
  Hints for keys go in `GameSession.Hints.cs` (`KeyHint`).

## 1. Where things are (2026-10-22)

| Build | Folder / project doc | State |
|---|---|---|
| Round Y plan | `ai/261020e_Plan/0_plan.md`, `claude/BimGo_RoundY_Plan.md` | Agreed 2026-10-20 |
| Y-A, Y-B fix-ups | `261020f_FixUps_YA`, `261021a_FixUps_YB` | Confirmed |
| Y-C1 lighting engine | `261021b_Lighting_YC1` | Confirmed (r2 light reach) |
| **Y-C2 per-fixture profiles, polar plots** | `261021d_Lighting_YC2`, `claude/BimGo_Lighting_YC2_BuildNotes.md` | **r2 open** (import button "IES…"): ask Gavin to confirm |
| Y-D1 performance details, occlusion culling | `261021e_Scale_YD1`, `claude/BimGo_Scale_YD1_BuildNotes.md` | Confirmed (r3) |
| Y-D2 depth pre-pass, light quality | `261022a_Scale_YD2`, `claude/BimGo_Scale_YD2_BuildNotes.md` | Confirmed: < 11 ms at the test spot |
| This handoff | `ai/261022b_Handoff/0_handoff.md`, `claude/BimGo_RoundY_YE_Handoff.md` | |

Gavin's working copy = the **Y-D2 zip** (`BimGo_Scale_YD2.zip`, which holds Y-C2 r2 and Y-D1 r3) plus this README
update, unless he says otherwise: ask first.

## 2. Round Y so far (read before touching these areas)
- **Y-A / Y-B:** see `claude/BimGo_RoundY_YC2_Handoff.md` §2 (measurements saved, clearance along normals, comment
  pictures shared; photo dock, PUSH TO REVIT and markup editor on the panel shell, focus ring trapped in overlays).
- **Y-C1:** Revit spot / hemispherical distributions → generated profiles (`gen:spot:<beam>:<field>`, `gen:hemi`); IES
  TILT=INCLUDE scales output; live lights ranked by lumens ÷ distance² with a 1.5× keep; `LightReach` 0.22 × √lm, 3–12 m.
- **Y-C2:** per-fixture choices in `light-profiles.json` (`instances`: key = UniqueId, after the link instance's UniqueId
  + "/" for links; `LightProfileDocument.InstanceKeyOf / ResolveChoice / KeyFor / ClearInstances`); Ctrl+K
  (`InputAction.FixtureProfile`, Tools) opens the fixture dock (`GameSession.FixtureProfile.cs`; finds a fixture within
  0.75 m of the aimed point; clones follow their original); LIGHTS rows: small polar plot, "N set individually",
  RESET ALL; `LightProfilePolar` (Core) builds the two-plane plot, cached per key.
- **Y-D1 (occlusion):** `SceneRenderer.Occlusion.cs` + Core `OcclusionState`: chunk boxes (+0.3 m) queried with
  `ANY_SAMPLES_PASSED` after opaque + dynamics + caps + ground, front faces only (`BOX_FACES` wound CCW outward; safe as
  the camera is never inside a queried box: near rule 1.5 m), answers read next frame without waiting; clusters (16 m
  grid × 4 m slabs) of all-hidden chunks queried as one; jump (1.5 m / 25°) and leave-view rules. Main view only
  (`SceneDrawParams.Occlusion`, set after the planar reflections). Options: Off / Auto / On (`OcclusionMode`, Auto from
  250 k triangles in view, off under 150 k). `GpuTimers` (TIME_ELAPSED, sequential stages, read 2 frames later) and the
  Performance details panel (`GameSession.Performance.cs`).
- **Y-D2 (shading):** depth pre-pass (`SceneRenderer.Prepass.cs`, `DEPTH_PREPASS_FS` on `SCENE_VS` which now declares
  `invariant gl_Position`; the lit opaque pass runs EQUAL, writes off; the pre-pass must drop exactly what the lit
  pass drops: section cuts and profile codes; not in the clash view / ghost alpha). Light quality Low / Medium / High
  (`LightQualityLimits`: shadowed 8 / 16 / 32, soft 0 / 8 / 32; the rest of the picks go to the far, unshadowed list;
  `uLightShadow[i].z` = 1 → one tap); photos, videos, photo mode and All fixtures live use High.
- Numbers at Gavin's test spot (1.5 M triangles, 1,935 chunks, lights on): GPU ~20 ms (Y-D1 r2) → **< 11 ms** with the
  pre-pass. Lights cost ~13.5 ms of opaque before the pre-pass; triangles were never the limit at this size.

## 3. Next: Revit round-trip (Round Y item 4)
Needs Gavin's marker family before the comment half can be built: ask whether he has it.
- **Comments → Revit markers:** Gavin authors the marker .rfa (family name, and parameters to agree when he has it:
  suggest BimGo id (text), comment text, status, author, date, a picture path or none). BimGo loads the family into the
  model if missing and places / updates / removes one marker per comment (live: through the protocol, one undo entry;
  file mode: on the next Revit session's sync). Questions to ask: which comments (all / open only / picked), placement
  (the comment's point, hosted or not, a level), what happens when a marker is deleted in Revit, and status mapping.
- **Room data overlay:** floating room tags (name / number at the room's centre, height above the floor), picked
  parameters (a picker over the rooms' parameters), target checks (e.g. area against a "Required area" parameter:
  green / amber / red with a tolerance). Room data comes from the snapshot (`RoomInfo` + the parameter table): check
  what's extracted for rooms before asking.
- Split: Z-A room overlay (no .rfa needed, can go first), Z-B markers (with the .rfa).

## 4. After (Round Y order)
5. **Async sharing through cloud folders** (each person writes only their own small files, merged on read; poll +
   toasts; care for cloud placeholders).
6. **Presentation:** kiosk launch (locked demo: fixed start, no edits / menus, idle reset to a tour, a launch
   shortcut), live sharp reflections retry (X-C1 notes).
7. **Installer** (Inno Setup per user; `ai/261009e_Installer`).

Later: OpenXR VR; review tools (find element, accessibility checks, travel tape, profile schedule CSV); IFC import;
entourage set; Scale part 3 if big models need it (small-detail culling, simplified meshes, tiled light culling).
Parked: real-time multiplayer.

## 5. Things to watch
- **Compiling:** no .NET SDK in the sandbox (and its network can't fetch one): say so. Script-check brackets, strings
  and char literals in changed `.cs` files (a small Python lexer: strip comments, handle `$` / `@` / `$@` strings and
  interpolation holes, check char literals and bracket balance); also UI strings against the font atlas (Latin-1 +
  `UiFont.EXTRA`: `Δ − — – … • ↑ ↓ ← → “ ” ’ ≈ ≤ ≥ ‹ › ↔`; `×` and `°` are Latin-1). Check new member names across the
  `GameSession` partials and widget signatures (`BeginPanel`, `HeaderButton`, `FooterAction`, `EndPanel`, `BeginDock`,
  `EndDock`, `BeginDockBody`, `EndDockBody`, `SmallButton`, `MenuButton`, `Segmented`, `Checkbox`, `Slider`).
- **Shaders:** extract the C# string constants (concatenations of `@"…"`, `"…"` and other constants), swap
  `#version 330 core` for `#version 300 es` + precisions (incl. `sampler2DArrayShadow`), compile / link in headless
  WebGL2 (Playwright at `/opt/npm-tools/node_modules/playwright`, Chromium, `--use-gl=angle --use-angle=swiftshader
  --enable-unsafe-swiftshader`) over `python3 -m http.server`. The scene shader uses **16 samplers**; uniform buffer
  bindings: 1 FarLights, 2 LightProfiles. Never `pkill -f` a pattern matching your own shell.
- **GL wrappers:** new GL calls need a wrapper in `Native/Gl.cs` and their entry point in `RequiredEntryPoints`.
- **Tests:** the test scene (`TestData.BuildScene`) has two elements (0, 1).
- **Revit name clashes:** `Plane`, `Transform`… with `System.Numerics` imported: use `DB.` for Revit types there.
- **Compatibility:** additive only (`.bimgo` format 1). No per-frame allocations; model-folder files written on change.
- **Encodings:** keep BOMs where they are (`GameSession.cs`, `GameSession.Menu.cs`, `GameSession.Render.cs`, `Shaders.cs`,
  `SceneRenderer.cs`, `InputCatalog.cs`, `Gl.cs`, `LaunchSettings.cs`…); most files are UTF-8 without BOM, LF; new files
  LF, no BOM.
- **Licensing:** bundle nothing third-party without a redistributable licence; record in THIRD-PARTY-NOTICES.
  Manufacturer .ies files are never bundled.
