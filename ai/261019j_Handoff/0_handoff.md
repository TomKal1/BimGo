# BimGo: handoff for Round X, X-D onwards (IES light profiles, Revit camera sync), after X-C1

**Context:** BimGo is Gavin's personal project: publisher **Aussie BIM Guru**, licence **MIT-0**; C# / Visual Studio,
.NET 8; its own OpenGL renderer over Silk.NET; a Revit add-in (`BimGo.Revit`), a standalone app (`BimGo.App`) and
`BimGo.Core`.

How Gavin works:
- **Rounds:** discuss → AskUserQuestion (recommended option first and marked, at most 4 questions per call; he sometimes
  types his own answer: follow it) → build → zip.
- **Notes:** each build gets `ai/<yymmdd><letter>_<topic>/` (`0_brief.md`, `1_build notes.md`), a README §8 to-verify
  entry (§8 holds only the open build: drop the previous one once Gavin confirms it), a §9 row, and a project doc
  `claude/BimGo_<Round>_BuildNotes.md` (sent as a file beside the zip).
- **Each build says what is in, what isn't, and how to check it step by step.** Send test renders when visual.
- Simple build / test errors: **just the fix** (find the cause, fix, re-zip, a few lines). He folds requests in mid-build
  (handle them in the same build). "Assume recommended options" when he's away; when he's away he also can't approve
  anything (stay inside the sandbox).
- **Ask whether his working copy changed** (compile fixes) before editing; if so, wait for his zip. He is often near his
  daily usage limit: split big rounds into builds that each zip on their own.
- New input actions need a `DefaultsChanges` row (bump `DEFAULTS_VERSION`) or `ControlsTests.DefaultsChanges_MatchTheDefaults` fails.

## 1. Where things are (2026-10-19)

| Build | Folder / project doc | State |
|---|---|---|
| X-A time-lapse, batch stills | `261019d_TimeLapse_XA` | Confirmed |
| X-B1 / X-B2 / X-B3 entourage | `261019e…`, `261019f…`, `261019h_EntouragePolish_XB3` | Confirmed (B3 + the `HoldScale` defaults fix) |
| X-C1 sharp reflections + all fixtures | `261019i_Reflections_XC1`, `claude/BimGo_Reflections_XC1_BuildNotes.md` | Tested, accepted as is (revision 4) |
| This handoff | `ai/261019j_Handoff/0_handoff.md`, `claude/BimGo_RoundX_XD_Handoff.md` | |
| **X-D** | next: `261019k_IES_XD` (or the next day's letter) | Answers below; build |

No open build in README §8 (X-C1 accepted). Gavin has **no traced entourage drawings** coming for now (the pipeline in
`tools/entourage/` waits).

## 2. X-C1 as it landed (read before touching reflections)
- **Live view:** the X-B3 probe reflections, plus: up-facing surfaces outside rooms (roofs, decks) reflect the sky
  instead of their fallback probe (the 8 m fallback-probe grid, box-projected differently per cell, drew a patchwork;
  `ReflectionProbes` data texel 2 `.w` = fallback flag, `probeEnvironment`).
- **Stills only** (photos, videos, batch stills, 360s; photo mode's preview with 2 planes at half resolution; thumbnails
  skip): **planar** mirrored views of the 2 / 4 / 8 biggest glass / mirror planes (Low / Medium / High; Core
  `ReflectivePlanes`: grouped by plane at load, sliding-door tracks merged with a *slack*; `SceneRenderer.PlanarReflections`,
  `PLANAR_GLSL`, unit 19 array, an 8th clip plane, `Gl.FrontFace(CW)`), and **SSR** on flat floors / water
  (`SceneRenderer.ScreenReflections`, `SSR_GLSL`, units 17 / 18; history = previous frame; each still first drawn small
  from its own camera: `WarmReflections`). Setting `ScreenReflections` (0–3, default 2) = "Sharp reflections (photos,
  videos)".
- **Why not live:** with 1–2 planes live, panes popped between mirrored view and probe as the chosen planes changed;
  SSR on glass vanished up close (what a pane facing you reflects is behind you). Gavin: "when it works it looks good":
  **revisit later** (ideas: all coplanar panes in view merged into fewer planes, cross-fade between planar and probe
  over a second, a per-room plane budget, cheaper mirrored passes: no billboards / lower LOD).
- **All fixtures:** stills (and photo mode's preview) light every fixture in view: the nearest 32 shadowed, the rest
  unshadowed up to 480 (`ArtificialLighting.FarPosition / FarColour`, `FarLights` std140 block at binding 1 in
  `LIGHTS_GLSL`; `UpdateArtificialLights(allFixtures)`); live opt-in "All fixtures live (slower)" (`AllLightsLive`).
  Unshadowed lights pass through walls (short reach).
- The scene shader now uses **16 samplers** (GL 3.3's per-stage minimum): any new texture for the scene shader must
  replace or pack into an existing one (e.g. an array layer, or the `uProbeData` / material table approach), or move
  to a uniform block.

## 3. X-D: IES light profiles (answers, 2026-10-19)

| Question | Answer |
|---|---|
| Assignment | **Per fixture type** (recommended): read each type's IES from Revit; in the app, replace or assign one per type |
| No / missing IES | **Today's lobe + a small CC0 set** (recommended): keep the current light as the default; ship a few generic profiles made by BimGo itself (downlight, wide flood, wall washer, pendant up/down) to assign in the app |

### Suggested X-D scope (split into two builds if long)
1. **Extraction (Revit):** for each lighting fixture type, the photometric web file path (the family's `Photometric Web
   File` parameter / light source definition; check `SceneExtractor.Lighting.cs`, which reads lumens / kelvin today);
   copy the `.ies` into the `.bimgo` (`ies/<hash>.ies`) and the live model folder; record per light (`LightSource`: add
   an IES id / type key) and per type in `lighting.json` (additive; `FileModels.cs` lighting DTOs).
2. **Core:** an IESNA LM-63 parser (1995 / 2002; TILT=NONE; type C photometry; vertical angles 0–180, horizontal
   0–360 with the symmetric cases 0, 0–90, 0–180), normalised to a small 2D candela table (e.g. 64 vertical × 16
   horizontal, peak 1) plus its total-lumen ratio; unit tests with a couple of sample files written by hand (own,
   CC0).
3. **Built-in profiles (MIT-0, generated in code):** downlight (narrow / medium), wide flood, wall washer (asymmetric),
   pendant (up / down split): the same table format.
4. **Renderer:** one texture holding every profile table as rows (it can't be a new sampler in the scene shader at 16
   already: pack the profiles into an existing texture or use a 2D array layer elsewhere, or sample them via a uniform
   block of floats if small: 32 profiles × 64 × 16 halves is too big for a block, so prefer packing into a free array
   texture, e.g. a layer of an existing array, or replace `uLightShadow`'s spare components with a profile row and use
   the material table's texture approach). Per light: profile row + the fixture's rotation (its family transform's
   up / forward: extraction must record the fixture's orientation). `LIGHTS_GLSL`: replace the downward lobe by the
   profile lookup in the light's local frame (keep the lobe when there's no profile); the far (unshadowed) lights too.
5. **App UI:** sun panel → Lights (or the pause menu Lights tab): per fixture type (family : type, count, profile:
   "from Revit / built-in name / none"), ASSIGN… (a built-in or IMPORT .IES…), RESET; saved in the model folder /
   `.bimgo` like entourage rules.
6. Check visually with headless WebGL renders: a wall with a downlight and a wall washer.
7. **Fold in: a frame-rate cap** (Gavin's P5200 laptop fans run loud at 50–60 fps: the GPU works flat out to reach
   VSync's 60): a setting *Frame rate limit* Off / 30 / 45 / 60 (default Off; saved in `LaunchSettings`), next to VSync
   in the settings; the main loop sleeps to the target (`Thread.Sleep` for most of the wait, a short spin for the
   rest, high-resolution timer `timeBeginPeriod(1)` while running), not applied while recording a video or rendering
   stills. Works with or without VSync.

## 4. After X-D
- **X-E two-way Revit camera sync** (live sessions only): new additive message kinds (e.g. `view.camera`) both ways
  (check `LiveDispatcher` ignores unknown kinds); BimGo → Revit updates a dedicated "BimGo view" 3D view (the "BimGo
  clash" view in SHOW IN REVIT is the pattern; throttled, e.g. ≤ 4 Hz, on an ExternalEvent); Revit → BimGo follows the
  active 3D view's eye / direction (ViewOrientation3D; feet ↔ scene-local metres via `GameSession.Coordinates`); a toggle
  for direction (off / BimGo → Revit / Revit → BimGo / both), loop guard. Ask: default direction, throttle, whether the
  Revit view should match FOV / section box.
- **Later:** live sharp reflections (above); billboards in the probe captures and a probe re-bake when entourage is planted / moved (live glass would show the people and trees, still soft); find element search, accessibility checks, travel-distance tape, kiosk
  launch, profile schedule CSV, OpenXR VR, IFC import, occlusion culling / LOD, multiplayer; entourage: wind sway,
  fixed-heading cars, billboards in reflections, undo for planting; Gavin's traced drawings when he has them.

## 5. Things to watch
- **Compiling:** no .NET SDK in the sandbox (say so). Script-check brackets, strings and char literals in changed `.cs`
  files (a checker is easy to rewrite: it also flags UI string glyphs outside the font atlas); new member names across
  the `GameSession` partials; UI strings against the font atlas (Latin-1 + `UiFont.EXTRA`: `Δ − — – … • ↑ ↓ ← → “ ” ’ ≈
  ≤ ≥ ‹ › ↔`).
- **Shaders:** extract the C# string constants (resolve `+` concatenations, private consts too: `PROFILE_WIDTH_TEXT`),
  swap `#version 330 core` for `#version 300 es` + precision, compile / link / render test frames in headless WebGL2
  (Playwright at `/opt/npm-tools/node_modules/playwright`, pre-installed Chromium, `--use-gl=angle
  --use-angle=swiftshader --enable-unsafe-swiftshader`), served over http (`python3 -m http.server` in the background).
  **Never `pkill -f` a pattern matching your own command line** (it kills the shell). Give every sampler its own unit and
  never sample a texture you're rendering into (feedback loop error).
- **Texture units:** 1–16 as before, 17 (SSR pre-pass), 18 (SSR history), 19 (planar array); unit 0 is scratch.
- **Compatibility:** additive only (`.bimgo` 1). No per-frame allocations; model-folder files written on change, not per
  frame.
- **Encodings:** keep BOMs where they are (e.g. `GameSession.cs`, `Shaders.cs`, `SceneRenderer.cs`, `InputCatalog.cs`,
  `Gl.cs`); new files LF, no BOM. Edit with a script that preserves the BOM, or the Edit tool.
- **Licensing:** bundle nothing third-party without a redistributable licence (IES files from manufacturers are not
  bundled; only BimGo-generated profiles); record sources in THIRD-PARTY-NOTICES.
