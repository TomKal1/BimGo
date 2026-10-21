# BimGo: handoff for Round Y, Y-C2 onwards (lighting polish part 2, then scale), after Y-C1 r2

**Context:** BimGo is Gavin's personal project: publisher **Aussie BIM Guru**, licence **MIT-0**; C# / Visual Studio,
.NET 8; its own OpenGL renderer over Silk.NET; a Revit add-in (`BimGo.Revit`, Revit 2025 / 2026 / 2027), a standalone
app (`BimGo.App`) and `BimGo.Core`.

How Gavin works:
- **Rounds:** discuss → AskUserQuestion (recommended option first and marked, at most 4 questions per call; he sometimes
  types his own answer: follow it) → build → zip.
- **Notes:** each build gets `ai/<yymmdd><letter>_<topic>/` (`0_brief.md`, `1_build notes.md`), a README §8 to-verify
  entry (§8 holds only the open build: drop the previous one once Gavin confirms it), a §9 row, and a project doc
  `claude/BimGo_<Round>_BuildNotes.md`. Send the zip with SendUserFile (`/mnt/user-data/outputs/BimGo_<Topic>.zip`,
  zipped from the repo root: LICENSE, README.md, THIRD-PARTY-NOTICES.txt, ai, src, tests, tools).
- **Each build says what is in, what isn't, and how to check it step by step.** Send test renders when visual.
- Simple build / test errors: **just the fix** (find the cause, fix, re-zip, a few lines). He folds requests in mid-build
  (handle them in the same build, as a revision section in the build notes). "Assume recommended options" when he's
  away. When he reports a bug with screenshots / logs, add log lines that settle it if the first fix doesn't.
- **Ask whether his working copy changed** before editing; if so, wait for his zip. He is often near his daily usage
  limit: split big rounds into builds that each zip on their own.
- New input actions need a `DefaultsChanges` row (bump `DEFAULTS_VERSION`, now 6) or
  `ControlsTests.DefaultsChanges_MatchTheDefaults` fails. Hints for keys go in `GameSession.Hints.cs` (`KeyHint`).

## 1. Where things are (2026-10-21)

| Build | Folder / project doc | State |
|---|---|---|
| Round Y plan | `ai/261020e_Plan/0_plan.md`, `claude/BimGo_RoundY_Plan.md` | Agreed 2026-10-20 |
| Y-A fix-ups, review tools | `261020f_FixUps_YA`, `claude/BimGo_FixUps_YA_BuildNotes.md` | Confirmed |
| Y-B fix-ups, UI shell | `261021a_FixUps_YB`, `claude/BimGo_FixUps_YB_BuildNotes.md` | Confirmed |
| **Y-C1 lighting polish (engine)** | `261021b_Lighting_YC1`, `claude/BimGo_Lighting_YC1_BuildNotes.md` | **r2 open** (README §8): r1 tested "not bad", r2 = longer light reach |
| This handoff | `ai/261021c_Handoff/0_handoff.md`, `claude/BimGo_RoundY_YC2_Handoff.md` | |
| **Y-C2** lighting polish (app) | next | Answers below; build |

Gavin's working copy = the **Y-C1 r2 zip** (`BimGo_Lighting_YC1.zip`, with the 1b test fix) unless he says otherwise:
ask first. Confirm r2 (light reach) at the start of the next chat, then drop it from README §8 when Y-C2 opens.

## 2. Round Y so far (read before touching these areas)
- **Y-A:** Measure gun measurements saved (`measurements.json` in the model folder / inside the .bimgo; `MeasurementModels.cs`,
  `GameSession.Measurements.cs`); I shows / hides, E exports CSV, X twice deletes all; clearance along the aimed
  surface's normal (walls: level across, "↔"); comment pictures mirrored beside the model when sharing is on
  (`ModelFolders.SyncPictures`).
- **Y-B:** photo mode's panel is a **dock** (`BuildPhotoDock`; `DockColumn` starts at the top in photo mode, the minimap
  hides there); **PUSH TO REVIT** on the panel shell (standard; report rows in a `ScrollRegion`; footer actions per
  state); the **markup editor** on the shell (wide; UNDO / REDO / CLEAR header buttons; `IsMarkupOpen` is in
  `AnyBigPanelOpen`, `PanelBack` → `CancelMarkupPanel` first); the gamepad **focus ring is trapped in open dropdowns /
  confirms** (`FocusTarget.Overlay`, `_focusInOverlay` set by `DrawOverlays`, `FocusTrapped`; the ring jumps to the
  current choice and back to the button on close); TOOLS ▾ shows "PICK POINTS (K)" (`ClashToolActionLabels`).
  PUSH TO REVIT lives in the pause menu's session column (file mode only).
- **Y-C1 (lighting engine):**
  - Revit **spot** (`SpotLightDistribution`: beam / field / tilt, radians; tilt read like a web's, 90° = down,
    **unverified for spots**: the log prints each spot type's tilt) and **hemispherical** lights → generated profiles:
    Core `LightProfiles.GENERATED_PREFIX` `gen:`, `SpotKey(beam, field)` = `gen:spot:<beam>:<field>`, `HEMISPHERICAL` =
    `gen:hemi`, `IsGeneratedKey`, `FromGeneratedKey` (spot: full inside half the beam, smoothstep to 0 at half the field;
    hemi: cos γ). `LightSource.Distribution` (`distribution` in lighting.json). Spherical lights stay on the lobe.
    In the app "Revit" for a type = its file, else its distribution (`LightTypeRow.RevitShape / HasRevit / RevitKey`).
  - **IES tilt:** `IesData.TiltGeometry / TiltAngles / TiltFactors / TiltFileIgnored`; `LightProfile.TiltFactor(deg)`;
    `ApplyLightProfiles` scales a light's lumens by the factor at its aim's angle from straight down.
  - **Live lights ranked** by output ÷ distance² (≥ 1 m), last frame's ×1.5 (`RankScore`, `RANK_KEEP`, `_rankKept`
    swap sets); fade of the weakest by score (low → 2.04 × low); far lights (all fixtures) strongest first (`BY_SCORE`).
  - **r2:** `GameSession.LightReach(lumens)` = 0.22 × √lm, 3–12 m (was 0.16, 2.5–9). If Gavin wants more, change it there.
    Hard limits: 32 shadowed lights (shader arrays, 6 × 32 = 192 shadow layers: GL's minimum array layers is 256, so
    don't raise past ~40 without checking `GL_MAX_ARRAY_TEXTURE_LAYERS`); *All fixtures live* adds up to 480 unshadowed.
  - Fixed: family library lights kept their profile / aim / across (they were lost in `SceneExtractor.Library.cs`).
- X-D1 lessons still hold (see `claude/BimGo_RoundX_XE_Handoff.md` §2): web tilt from horizontal; instance transforms
  leave out Hand/FacingFlipped; nested light sources from the family document; solid lenses have no facing.

## 3. Y-C2: per-instance profiles + profile previews (answers 2026-10-21)

| Question | Answer |
|---|---|
| Per-instance profiles | **Aim at a fixture + a key**: a small dock shows that fixture's profile with ‹ › and RESET; the type's row in LIGHTS counts the overrides |
| Previews | **Polar candela plot** (the classic two-plane photometric diagram) drawn with UI lines in the LIGHTS tab |

Suggested scope:
1. **Data:** `LightProfileDocument` gains per-instance choices keyed by something stable across Go / F5 / Export: the
   element's **UniqueId** (`ElementRecord.UniqueId`; linked elements need the link too: reuse the
   `EntourageSlots.InstanceKey` pattern, link key + "/" + UniqueId; clones: their clone key) → choice (same values as type choices:
   `revit`, `lobe`, built-in, `ies:` imports, and generated keys). Additive in `light-profiles.json`; `Clean()` drops
   unknown ids lazily (keep entries for elements not in this snapshot: hidden links / phases may bring them back).
2. **Resolve:** `ProfileKeyOf(light)` → instance choice, else type choice, else Revit (file / distribution), else lobe.
   Slots stay ≤ 24 (overflow → lobe, said in the tab and the dock).
3. **Key + dock:** a new `InputAction` (suggest unbound-by-default or a free key: check `InputCatalog` clashes; walking
   context; F1 / hints / README §3 generated rows; `DEFAULTS_VERSION` 7 + `DefaultsChanges` row). Aim ray → the
   element under the crosshair; if it (or its host) has lights, open a **dock** (`BeginDock`, like the section box /
   photo docks; free cursor, RMB-drag looks?) titled with "Family : Type", showing: the current profile label (as
   `AppendChoiceLabel`), "(this fixture)" / "(from the type)", ‹ › through `ProfileOptionsFor`, IMPORT .IES…, RESET
   (back to the type's), the polar plot of the current profile, CLOSE. Highlight the fixture while the dock is open.
   Moved / cloned fixtures: decide what a clone inherits (suggest: the original's override).
4. **LIGHTS tab:** each type row gets "· N fixtures set individually" and a RESET ALL for them; the polar plot beside
   the row's profile (small, e.g. 64 px), or a larger one for the row under the cursor.
5. **Polar plot:** from `LightProfile.Sample(gamma, C)`: two planes (C 0–180 and C 90–270), gamma 0 at the bottom (a
   downlight's beam points down), normalised to the peak, rings at 25 / 50 / 75 / 100 %, `_ui.Line` polylines (≈ 64
   segments each, built once per profile and cached as points: no per-frame allocation). Two colours (C0 accent, C90
   muted), both themes fine (the app has one dark theme).
6. **Tests:** document round trip with instance choices, `Clean()`, resolve precedence (Core-testable helper if the
   precedence lives in Core).

## 4. After Y-C2 (Round Y order)
4. **Scale:** occlusion culling and LOD. Ask at the round: GPU occlusion queries vs a software depth pre-pass; LOD by
   simplified meshes vs impostors; model size targets.
5. **Revit round-trip:** comments → Revit markers (Gavin authors the .rfa: name, parameters for id / text / status /
   author to agree when he has it), room data overlay (floating tags, picked parameters, target checks). Needs the .rfa.
6. **Async sharing through cloud folders** (Dropbox / OneDrive / Google Drive / network drives; each person writes only
   their own small files, merged on read; poll + toasts; care for cloud placeholders): a round after the round-trip.
7. **Presentation:** kiosk launch (locked demo: fixed start, no edits / menus, idle reset to a tour, a launch shortcut),
   live sharp reflections retry (X-C1 notes).
8. **Installer** (Inno Setup per user; `ai/261009e_Installer`).

Later: OpenXR VR; review tools (find element, accessibility checks, travel tape, profile schedule CSV); IFC import;
entourage set (wind sway, fixed-heading cars, planting undo, billboards in probes). Parked: real-time multiplayer
(Gavin expects it to be web based / hosted in the end; lighter steps: one BimGo hosting on the LAN, a spectator mode).

## 5. Things to watch
- **Compiling:** no .NET SDK in the sandbox (and its network can't fetch one): say so. Script-check brackets, strings
  and char literals in changed `.cs` files (a small Python lexer: strip comments, handle `$` / `@` / `$@` strings and
  interpolation holes, check char literals and bracket balance); also UI strings against the font atlas (Latin-1 +
  `UiFont.EXTRA`: `Δ − — – … • ↑ ↓ ← → “ ” ’ ≈ ≤ ≥ ‹ › ↔`; `×` and `°` are Latin-1). Check new member names across the
  `GameSession` partials and widget signatures (`BeginPanel`, `HeaderButton`, `FooterAction`, `EndPanel`, `BeginDock`,
  `EndDock`, `BeginScroll`, `EndScroll`, `SmallButton`, `MenuButton`, `Segmented`, `Checkbox`, `StepperValue`).
- **Tests:** the test scene (`TestData.BuildScene`) has **two elements** (0, 1): lights / records on other indices are
  dropped by the reader (Y-C1 1b).
- **Revit name clashes:** `Plane`, `Transform`… with `System.Numerics` imported: use `DB.` for Revit types there.
- **Shaders:** extract the C# string constants, swap `#version 330 core` for `#version 300 es` + precision, compile /
  render in headless WebGL2 (Playwright at `/opt/npm-tools/node_modules/playwright`, Chromium, `--use-gl=angle
  --use-angle=swiftshader --enable-unsafe-swiftshader`) over `python3 -m http.server`. The scene shader uses **16
  samplers**; uniform buffer bindings: 1 FarLights, 2 LightProfiles. Never `pkill -f` a pattern matching your own shell.
- **Compatibility:** additive only (`.bimgo` format 1). No per-frame allocations; model-folder files written on change.
- **Encodings:** keep BOMs where they are (`GameSession.cs`, `GameSession.Menu.cs`, `Shaders.cs`, `SceneRenderer.cs`,
  `InputCatalog.cs`, `Gl.cs`…); most files are UTF-8 without BOM, LF; new files LF, no BOM. Read / write with plain
  `utf-8` (not `utf-8-sig`, which adds a BOM) unless the file already has one.
- **Licensing:** bundle nothing third-party without a redistributable licence; record in THIRD-PARTY-NOTICES.
  Manufacturer .ies files are never bundled.
