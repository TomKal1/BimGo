# BimGo: handoff for Round X, X-E onwards (Revit camera sync), after X-D1

**Context:** BimGo is Gavin's personal project: publisher **Aussie BIM Guru**, licence **MIT-0**; C# / Visual Studio,
.NET 8; its own OpenGL renderer over Silk.NET; a Revit add-in (`BimGo.Revit`, Revit 2025 / 2026 / 2027), a standalone
app (`BimGo.App`) and `BimGo.Core`.

How Gavin works:
- **Rounds:** discuss → AskUserQuestion (recommended option first and marked, at most 4 questions per call; he sometimes
  types his own answer: follow it) → build → zip.
- **Notes:** each build gets `ai/<yymmdd><letter>_<topic>/` (`0_brief.md`, `1_build notes.md`), a README §8 to-verify
  entry (§8 holds only the open build: drop the previous one once Gavin confirms it), a §9 row, and a project doc
  `claude/BimGo_<Round>_BuildNotes.md` (sent as a file beside the zip).
- **Each build says what is in, what isn't, and how to check it step by step.** Send test renders when visual.
- Simple build / test errors: **just the fix** (find the cause, fix, re-zip, a few lines). He folds requests in mid-build
  (handle them in the same build). "Assume recommended options" when he's away; when he's away he also can't approve
  anything (stay inside the sandbox). When he reports a bug with screenshots / logs, add log lines that settle the
  question if the first fix doesn't (X-D1 took 8 revisions; the logs found it).
- **Ask whether his working copy changed** (compile fixes) before editing; if so, wait for his zip. He is often near his
  daily usage limit: split big rounds into builds that each zip on their own.
- New input actions need a `DefaultsChanges` row (bump `DEFAULTS_VERSION`) or `ControlsTests.DefaultsChanges_MatchTheDefaults` fails.

## 1. Where things are (2026-10-20)

| Build | Folder / project doc | State |
|---|---|---|
| X-A, X-B1–B3, X-C1, X-D0 | see README §9 | Confirmed |
| **X-D1 IES light profiles** | `261020b_IES_XD1`, `claude/BimGo_IES_XD1_BuildNotes.md` | **Confirmed at r8b** |
| This handoff | `ai/261020c_Handoff/0_handoff.md`, `claude/BimGo_RoundX_XE_Handoff.md` | |
| **X-E** Revit camera sync | next | Questions below |

No open build in README §8. Gavin's working copy = the r8b zip (he compiled it; `SceneRenderer.PlanarReflections.cs`
gained `using BimGo.Physics;`, `SceneExtractor.Lighting.cs` uses `DB.Plane`). Gavin fixed the two missing .ies files on
his side (no code change). No traced entourage drawings coming for now.

## 2. X-D1 as it landed (read before touching lighting)
- **Core:** `Scene/LightProfiles.cs`: `IesData` (LM-63 1986–2002, type C only, TILT=INCLUDE skipped, symmetric layouts
  expanded), `LightProfile` (32 gamma × 16 C bytes, peak 255, `Scale` = 4π / integral: a profile moves light, never adds
  any), five built-ins generated in code (`builtin:downlight-narrow / downlight-medium / flood / wall-washer / pendant`),
  keys (`ies:<16 hex>` content hash, `revit`, `lobe`), per-light code `Encode / Decode` (slot 5 bits · octahedral aim 6+6 ·
  turn 7, stored as `-(code+1)` in the light colour's `.w`; ≥ 0 is the old downward share). `Format/LightProfileModels.cs`:
  `light-profiles.json` (per "Family : Type" choice + imported files' text), `LightProfileFiles` (model folder).
  `LightingData.IesFiles`, `LightSource.Ies / Aim / Across`; `.bimgo` entries `ies/<hash>.ies`, `light-profiles.json`.
- **Renderer:** `LightProfiles` std140 block, binding 2 (768 uvec4 of table bytes + 24 vec4 scales; up to 24 profiles at
  once) in `LIGHTS_GLSL`; `profileLobe()` replaces the lobe for shadowed and far lights. No sampler added (scene shader
  still at **16**). `ArtificialLighting.SetProfile`, `SceneRenderer.UploadLightProfiles`.
- **App:** `GameSession.LightProfiles.cs` (resolve per light, slots, codes; moved / cloned fixtures re-encode; probes
  re-bake on change); Pause → **LIGHTS** tab (‹ › Revit file / built-ins / imported / lobe, IMPORT .IES…, RESET); the tab
  row sizes tabs to their labels when four don't fit equally. A fixture can have **several consecutive lights** (nested
  sources); clones bring them all; the tab counts fixtures.
- **Revit extraction (`SceneExtractor.Lighting.cs`)** and the lessons it took 8 revisions to learn:
  - `LightType.GetLightTypeFromInstance` → `PhotometricWebLightDistribution` (file, `TiltAngle`). **Tilt is measured from
    horizontal: 90° = straight down**; the aim is −BasisZ turned by (90° − tilt) about BasisY (direction of that turn for
    other tilts unverified).
  - A fixture with a light source of its own aims by its family; **lens faces can be modelled backwards**; a **solid
    glowing lens has no facing** (normals cancel: only |Σn| ≥ 0.5 × area counts).
  - **Nested light sources:** shared nested lighting families are their own elements (host gets no light); non-shared
    ones are **merged into the host's symbol geometry** (no nested geometry instances), so the family is opened once
    (`EditFamily`, closed unsaved, not in the library pass) and each nested Lighting Fixtures instance with a light
    source becomes a light.
  - **Mirroring:** instance transforms (`GetTransform`, `GetTotalTransform`, and the drawn top-level geometry instance's)
    **leave out `HandFlipped` / `FacingFlipped`**. Family-space offsets need host × flips (hand = across the family's
    YZ plane, facing = across XZ; both = half turn) × nested. A bounds check against the drawn fixture guards it (logged).
  - .ies search: path as given, model folder, Revit library paths, `%ProgramData%\Autodesk\RVT <ver>`, additional render
    appearance paths and texture search folders (each + parent, 3 deep); unfound files log the folders searched.
  - The "Lighting:" log line counts all of this (nested placement methods, flips, overrules).
- **Not done (later):** Revit spot / hemispherical distributions as generated profiles; IES tilt data; per-instance
  assignment; profile preview pictures; smarter live light picking (the live view lights the nearest 32 in view, which
  interior lights seen through glass can crowd out: rank by contribution instead).

## 3. X-E: two-way Revit camera sync (live sessions only), to discuss and ask
- New additive message kinds both ways (e.g. `view.camera`): check `LiveDispatcher` (Revit) and the app's live client
  ignore unknown kinds. BimGo → Revit updates a dedicated "BimGo view" 3D view (the "BimGo clash" view from SHOW IN
  REVIT, `LiveDispatcher` ~line 446, is the pattern), throttled on an ExternalEvent; Revit → BimGo follows the active 3D
  view's `ViewOrientation3D` (feet ↔ scene-local metres via `GameSession.Coordinates`); a direction toggle (off / BimGo →
  Revit / Revit → BimGo / both) and a loop guard. README §7 lists the protocol.
- **Ask:** default direction; throttle (e.g. ≤ 4 Hz); whether the Revit view matches FOV (perspective) / section box;
  where the toggle lives (pause menu, a key, the Revit ribbon); whether Revit → BimGo moves the player (walk) or flies.

## 4. Later
Live sharp reflections (ideas in the X-C1 notes); billboards in probe captures and a probe re-bake when entourage
changes; find element search, accessibility checks, travel-distance tape, kiosk launch, profile schedule CSV, OpenXR VR,
IFC import, occlusion culling / LOD, multiplayer; entourage: wind sway, fixed-heading cars, undo for planting; the
lighting items in §2 "Not done"; Gavin's traced entourage drawings when he has them.

## 5. Things to watch
- **Compiling:** no .NET SDK in the sandbox (say so). Script-check brackets, strings and char literals in changed `.cs`
  files (also flag UI string glyphs outside the font atlas: Latin-1 + `UiFont.EXTRA` `Δ − — – … • ↑ ↓ ← → “ ” ’ ≈ ≤ ≥ ‹ › ↔`);
  check new member names across the `GameSession` partials. **Revit name clashes:** `Plane`, `Transform`… with
  `System.Numerics` imported: use `DB.` (global alias) for Revit geometry types in files that use Numerics.
- **Shaders:** extract the C# string constants (resolve `+` concatenations), swap `#version 330 core` for
  `#version 300 es` + precision (incl. `sampler2DArray`, `sampler2DArrayShadow`), compile / link / render in headless
  WebGL2 (Playwright at `/opt/npm-tools/node_modules/playwright`, Chromium, `--use-gl=angle --use-angle=swiftshader
  --enable-unsafe-swiftshader`) over `python3 -m http.server`. Avoid GLSL reserved words (`packed`, `input`, `output`,
  `sample`…). Never `pkill -f` a pattern matching your own command line.
- **Texture units:** 1–16, 17 (SSR pre-pass), 18 (SSR history), 19 (planar array); unit 0 scratch. Uniform buffer
  bindings: 1 FarLights, 2 LightProfiles.
- **Compatibility:** additive only (`.bimgo` 1). No per-frame allocations; model-folder files written on change.
- **Encodings:** keep BOMs where they are (`GameSession.cs`, `GameSession.Menu.cs`, `Shaders.cs`, `SceneRenderer.cs`,
  `InputCatalog.cs`, `Gl.cs`…); new files LF, no BOM; edit with a BOM-preserving script or the Edit tool.
- **Licensing:** bundle nothing third-party without a redistributable licence; record in THIRD-PARTY-NOTICES.
