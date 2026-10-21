# BimGo: handoff for Round X, X-B3 onwards (entourage polish, reflections, IES, Revit camera sync), after X-B2

**Context:** BimGo is Gavin's personal project: publisher **Aussie BIM Guru**, licence **MIT-0**; C# / Visual Studio,
.NET 8; its own OpenGL renderer over Silk.NET; a Revit add-in (`BimGo.Revit`), a standalone app (`BimGo.App`) and
`BimGo.Core`.

How Gavin works:
- **Rounds:** discuss → AskUserQuestion (recommended option first and marked, at most 4 questions per call; he sometimes
  types his own answer: follow it) → build → zip.
- **Notes:** each build gets `ai/<yymmdd><letter>_<topic>/` (`0_brief.md`, `1_build notes.md`), a README §8 to-verify
  entry (§8 holds only the open build: drop the previous one once Gavin confirms it), a §9 row, and a project doc
  `claude/BimGo_<Round>_BuildNotes.md` (sent as a file beside the zip).
- **Each build says what is in, what isn't, and how to check it step by step.** Send headless test renders when visual.
- Simple build / test errors: **just the fix** (find the cause, fix, re-zip, a few lines). He folds requests in mid-build
  (handle them in the same build). "Assume recommended options" when he's away. README kept compact.
- **Ask whether his working copy changed** (compile fixes) before editing; if so, wait for his zip. He is often near his
  daily usage limit: split big rounds into builds that each zip on their own.

## 1. Where things are (2026-10-19)

| Build | Folder / project doc | State |
|---|---|---|
| X-A sun time-lapse, batch stills | `261019d_TimeLapse_XA`, `claude/BimGo_TimeLapse_XA_BuildNotes.md` | Confirmed ("perfect") |
| X-B1 entourage billboards | `261019e_Billboards_XB1`, `claude/BimGo_Billboards_XB1_BuildNotes.md` | Built and run by Gavin from the B2 zip (B2 carries B1) |
| X-B2 Revit Planting / Entourage swaps | `261019f_RevitSwap_XB2`, `claude/BimGo_RevitSwap_XB2_BuildNotes.md` | Built and run (no errors reported; confirm the checks) |
| This handoff | `ai/261019g_Handoff/0_handoff.md`, `claude/BimGo_RoundX_XB3_Handoff.md` | |
| **X-B3** | next: `261019h_EntouragePolish_XB3` (or the next day's letter) | Answers below; build |

README §8 lists X-B1 + X-B2 as the open build: once Gavin confirms them, replace it with X-B3. Gavin's feedback so far on
B1: **"the silhouettes are a bit goofy"** (the procedural people especially).

Round X roadmap (agreed): X-A done · **X-B entourage** (B1, B2 done; **B3 polish next**) · **X-C reflections** (SSR live +
planar reflections for stills) · **X-D IES light profiles** · **X-E two-way Revit camera sync**. Dropped: 4D sequence
playback; a time-lapse along a camera path.

## 2. Entourage as built (what X-B3 changes)
- **Core** `Scene/EntourageArt.cs`: categories (tree / shrub / person / car / other, default colours), `EntourageBuiltIn`
  (14 built-ins: key, name, category, height), `EntourageImage` (RGBA top-down, content box, aspect, mean luminance;
  `FitInto`, `BleedEdges`, `KeyOutBackground`), `EntourageArt.Draw(key)` = a 512 px procedural canvas (circles /
  ellipses / tapered capsules AA analytically, polygons 4 × 4 supersampled; premultiplied while drawing, stored
  straight; seeded xorshift). A Python port (numpy) was used to preview: keep any new drawing code mirrored the same way
  so previews can be sent.
- **Core** `Format/EntourageModels.cs`: `EntourageType` (imported PNGs: `img:<id>`), `EntourageItem` (type key, base in
  Revit internal metres, height, flip; `Local`, `Slot` not saved), `EntourageDocument` (`textured`, `colours`, `types`,
  `items`, X-B2 `swapRevit`, `swaps`), `EntourageSwap` / `EntourageSwaps` (instance → type → auto → keep; `Result`),
  `EntourageFiles` (model folder `entourage.json` + `entourage-images\`). `Scene/EntourageSlots.cs`: `EntourageSlot`
  (Revit instance: UniqueId, link key, family / type, base, height, width, element), `AutoType`.
- **Format:** `entourage.json`, `entourage/<id>.png`, `entourage-slots.json` (optional entries; writer / reader).
- **Revit:** `SceneExtractor.Entourage.cs` records slots at every extraction; Export embeds the model folder's entourage.
- **App:** `SceneRenderer.Billboards.cs` (texture array of 512 px layers on the scratch unit 0 while drawing; instance
  VBO 16 floats; scene pass + sun-facing shadow pass in `RenderCascade`); shaders `BILLBOARD_VS / FS / SHADOW_FS`;
  `GameSession.Entourage.cs` (store, pictures, instances, picking `PickBillboard` (camera-facing quad + alpha), planting /
  swapping / removing / `ResizeBillboard` (÷ / × 1.1 per Z / X press) / flipping, Revit swaps (`RebuildSwaps`,
  `ApplyEntourageHidden`, `EntourageHides` folded into `ProfileHides`), the ENTOURAGE panel incl. FROM REVIT);
  `EntourageStore.cs`; `PlaceGun` ENTOURAGE mode (wheel switches; RMB plant / swap, LMB remove / panel, Z / X size, R
  mirror; derived billboards make instance rules).

## 3. X-B3: entourage polish (answers, 2026-10-19)

| Question | Answer |
|---|---|
| Dynamic rescale | **Live resize the aimed billboard** (recommended): hold a key + wheel (or drag) scales it smoothly with a height readout, instead of only 10 % steps |
| Outlines | **Refine the procedural ones now; Gavin's drawings later** (recommended): better people / trees in X-B3, his traced drawings replace them when he sends them |
| Drawings format | **Filled silhouettes** (recommended): solid black shapes on white, photo / scan or tablet PNG |
| Baseline | Gavin ran the B2 zip (assume as sent; ask about compile fixes before editing) |

Also agreed in discussion (no question needed): **IMPORT FOLDER…** (batch-import a licensed pack the user owns: one type
per PNG, categories guessed from file names); **third-party packs are never bundled** (Silhouette AC forbids
redistribution; MIT-0 ships files to everyone): bundle only Gavin's own drawings (credited, MIT-0 / CC0) or CC0 sources
vetted and listed in THIRD-PARTY-NOTICES. Card counts should include swapped Revit billboards.

### Suggested X-B3 scope
1. **Live resize:** while the Place gun (ENTOURAGE) aims at a billboard, hold a modifier (suggest `HoldFine`, Shift by
   default, or a new action added at the end of the Holding group with a default and the clash test updated) + the wheel
   scales it continuously (e.g. 3 % per notch, clamp 0.2–60 m); the wheel otherwise switches gun modes (`GunModeNext /
   Previous`), so the gun must consume the wheel while the modifier is held. Show the height on the gun panel / a toast
   (`UnitFormat.Length`). Planted items update `EntourageItem.Height` (save on release, not per notch: avoid writing the
   model folder file every frame); Revit-swapped ones write an instance rule `height` (same: on release). Z / X stay.
   Optional: a height field in the panel for the picked type ("planted at"), and per-item exact height via a text box.
2. **Better procedural art** (`EntourageArt` + the Python port for previews): people with adult proportions (head ≈ 1/7.5
   of height), shoulders / hips, tapered limbs (polygons, not capsules), natural stances, smaller heads, no "sausage"
   arms; trees with less bubbly canopies (more, smaller, irregular clumps, gaps showing branches, a softer edge), a better
   palm. Send a library sheet and a WebGL test render (harness below) for approval.
3. **Traced drawings pipeline** (for when Gavin sends them): threshold → largest contours (holes kept) → simplify
   (Douglas-Peucker) → smooth (Chaikin or cubic) → normalise to the ground line and a real height → store as compact path
   data in Core (e.g. `EntourageShapes.cs`: polygons with holes per type, rasterised by the same `Canvas.Poly` path at
   512 px) so they're vector, tiny, tintable and identical everywhere; optional second "detail" layer for inner lines.
   Preview every traced figure before shipping; credit in THIRD-PARTY-NOTICES / README (Gavin's own work).
4. **IMPORT FOLDER…** (`FileDialogs.ShowFolder`): every PNG / JPG in the folder (not sub-folders, or ask) becomes a type
   (`ImportEntouragePicture` logic per file, a progress screen for many), names → categories (`GuessCategory`).
5. **Card counts** include swapped Revit instances of a built-in type.

## 4. After X-B3
- **X-C reflections** (answer: **SSR live + planar for stills**): screen-space reflections in the scene pass over the
  probes (ray-march the depth buffer from the AO / geometry pre-pass or a full-res depth copy; fall back to the probes
  off-screen / at grazing failures; roughness from the existing reflect gain / threshold tiers), Realistic mode only,
  an on / off + quality setting; planar reflections for photos / videos / batch stills: cluster large glazing planes by
  normal and plane offset, render a mirrored view per dominant plane (clip plane at the glass) into a texture, sample it
  in the glass shader for those surfaces. Probes stay as the fallback. Questions to ask: SSR quality presets, which
  surfaces (glass, water, polished floors), planar plane count limit / time budget per still.
- **X-D IES profiles:** Revit lighting fixtures reference a photometric web (`.ies`): extraction reads the family's IES
  file path (and copies the file into the `.bimgo` / model folder), the app parses IESNA LM-63 into a small 2D
  candela texture (vertical × horizontal angles) per profile, `LIGHTS_GLSL` multiplies each light by its profile
  (light-space direction; a profile index per light in `uLightShadow` or a new uniform); users can assign / replace an IES
  file per light type in the app. `LightSource` gains an IES id; `lighting.json` additive. Ask: per type vs per instance,
  a bundled small IES set (only CC0 / own), fallback for missing files.
- **X-E two-way Revit camera sync** (live sessions only): new additive message kinds (e.g. `view.camera`) both ways
  (check `LiveDispatcher` ignores unknown kinds); BimGo → Revit updates a dedicated "BimGo view" 3D view (the "BimGo
  clash" view in SHOW IN REVIT is the pattern; throttled, e.g. ≤ 4 Hz, on an ExternalEvent); Revit → BimGo follows the
  active 3D view's eye / direction (ViewOrientation3D; feet ↔ scene-local metres via `GameSession.Coordinates`); a toggle
  for direction (off / BimGo → Revit / Revit → BimGo / both), loop guard. Ask: default direction, throttle, whether the
  Revit view should match FOV / section box.

## 5. Things to watch
- **Compiling:** no .NET SDK in the sandbox (say so). Script-check brackets, strings and char literals in changed `.cs`
  files (re-write the checker if needed: it also flags UI string glyphs outside the font atlas); new member names across
  the `GameSession` partials; UI strings against the font atlas (Latin-1 + `UiFont.EXTRA`: `Δ − — – … • ↑ ↓ ← → “ ” ’ ≈
  ≤ ≥ ‹ › ↔`; draw icons with UI primitives).
- **Shaders:** extract the C# string constants (resolve `+` concatenations), swap `#version 330 core` for
  `#version 300 es` + precision (incl. `sampler2DArray`, `sampler2DArrayShadow`), compile / link / **render test frames**
  in headless WebGL2 (Playwright 1.56 at `/opt/npm-tools/node_modules/playwright`, pre-installed Chromium,
  `--use-gl=angle --use-angle=swiftshader --enable-unsafe-swiftshader`). Pages must be served over http (fetch fails on
  file://): `python3 -m http.server` in the background; **don't `pkill -f` a pattern that matches your own command line**
  (it kills the shell). Give every sampler its own unit in tests (two sampler types on one unit is a draw error). GLSL:
  `flat` is a reserved word.
- **Texture units:** 1–16 are taken (16 = the cover map); unit 0 is the scratch unit (lens copy, billboard array while
  drawing, UI flushes rebind it).
- **Text fit:** dock ~300 px inside; Body 14 px ≈ 7 px a character; a 4-tab row ≈ 75 px a tab.
- **Compatibility:** additive only (`.bimgo` 1). **No per-frame allocations**; labels cached; long work on a worker with a
  status line or toast. Model-folder files are written on change: never per frame (batch slider / wheel edits, save on
  release).
- **Encodings:** keep BOMs where they are (e.g. `GameSession.cs`, `GameSession.Sun.cs`, `Shaders.cs`, `SceneRenderer.cs`,
  `LaunchSettings.cs`, `SunModels.cs`, `SceneExtractor.cs`, `TestData.cs`; `README.md` has none); new files LF, no BOM.
  Edit with a script that preserves the BOM, or the Edit tool.
- **Licensing:** bundle nothing third-party without a licence that allows redistribution; record sources in
  THIRD-PARTY-NOTICES.

## 6. Later
- Find element search, accessibility checks, travel-distance tape, kiosk launch, profile schedule CSV, OpenXR VR, IFC
  import, occlusion culling / LOD, multiplayer; entourage: wind sway, fixed-heading (non camera-facing) cars, billboards
  in reflections, undo for planting.
