# Round X, build X-B1: entourage billboards (2026-10-19)

Built on X-A (confirmed). **Not compiled here** (no .NET SDK in the sandbox): build first, then the Core tests (+5 in the
new `EntourageTests`). Checked: brackets / strings / char literals in every changed `.cs`, encodings kept (BOMs where
they were, new files LF without), new member names across the `GameSession` partials, UI strings against the font atlas.
The built-in pictures were drawn by a Python port of the same code (`BimGo_Billboards_XB1_library.png`); the three new
shaders were extracted, compiled, linked and rendered in headless WebGL2 with a sun shadow map
(`BimGo_Billboards_XB1_test.png`: silhouettes on the left, own colours on the right, their shadows on the ground).
**No new keys.**

## In

| Item | What it does | Where |
|---|---|---|
| **Built-in library** | 14 billboards drawn in code (no third-party pictures; the same on every machine): round, columnar and spreading trees, a conifer, a palm; a round shrub, a hedge, ornamental grass; a man standing, a woman walking, a child, a person on the phone; a car and an SUV. Drawn on a worker at start (they appear a moment after the model). | Core `Scene/EntourageArt.cs` |
| **Your own pictures** | ENTOURAGE → **IMPORT PNG…**: a cut-out with a transparent background (a JPEG or flat PNG with a plain background has it made see-through when the four corners agree). Cropped to its content, kept at ≤ 1024 px. Its category is guessed from the file name ("oak tree.png" → trees), then **RENAME…**, **Category ‹ ›**, **Height − / +** on the right; **DELETE PICTURE** (twice) removes it and its billboards. | `ImportEntouragePicture`, `EntourageStore.AddType` |
| **Look** | **Colour silhouettes** (default): each category in its own colour (sage trees, lighter shrubs, off-white people, pale grey cars, warm grey other), shaded by the picture's light and dark; **CHANGE…** (the Windows colour picker) / **RESET** per category. **Own colours**: the built-ins painted, your PNGs as they are. **Show billboards** hides them all (this session). Saved with the model. | `BILLBOARD_FS`; `EntourageDocument.Colours` |
| **ENTOURAGE panel** | Pause menu → **ENTOURAGE (n)** (or LMB with the Place gun in ENTOURAGE mode, away from billboards): the types as cards by category (how many of each are placed on the card); click one to pick it; on the right its details, **REMOVE ALL OF THESE** (twice), the look; footer **IMPORT PNG…** and **PLANT** (back to the walk with the Place gun ready). | `BuildEntouragePanel` |
| **Planting** | The Place gun (9) has an **ENTOURAGE** mode: the wheel switches families ↔ entourage (it starts in ENTOURAGE when the snapshot has no family library). **RMB** plants the picked type where the crosshair meets the ground, a floor or a roof (its height ± 8 %, mirrored at random, so a row of trees doesn't repeat). Aiming at a billboard outlines it: **RMB** swaps it to the picked type, **LMB** removes it, **Z / X** make it 10 % smaller / bigger, **R** mirrors it. The gun's panel says what it will plant and the keys. | `PlaceGun`; `PlantBillboard`, `PickBillboard` |
| **Drawing** | Each billboard turns about the vertical to face the camera, stands on its base and is cut out by its picture (alpha 0.5, mipmapped). Lit like foliage: the sky (brighter towards the top), the sun wrapped round, its shadows (other billboards' and the building's), the artificial lights, fog, the section cut. In photos, 360s, videos, time-lapses, batch stills and thumbnails. | `SceneRenderer.Billboards`, `BILLBOARD_VS` / `BILLBOARD_FS` |
| **Shadows** | Billboards cast sun (and moon) shadows: in the shadow maps each is turned to face the sun, so its shadow is the whole silhouette; a billboard never shades itself (its own lookup is moved half its width towards the sun). | `DrawBillboardShadows`, `BILLBOARD_SHADOW_FS` |
| **Saved** | A .bimgo keeps them inside (`entourage.json` + `entourage/*.png`, saved with SAVE; the title shows *). A live session writes `entourage.json` and `entourage-images\` in the model's BimGo folder on every change, so the next Go / F5 brings them back and a Revit **Export** embeds them. | `EntourageStore`, `EntourageFiles`; writer / reader; `Cmds_BimGo` export |

Positions are kept in Revit internal metres (like bookmarks), so billboards stay put across snapshots.

### Files on disk (additive)
`entourage.json` (in a .bimgo and the model folder): `version`, `model`, `textured`, `colours` (category → #RRGGBB, left
out at the defaults), `types` (`id`, `name`, `category`, `height`, `image`), `items` (`id`, `type`, `x` `y` `z`,
`height`, `flip` when true). Pictures: `entourage/<id>.png` in a .bimgo, `entourage-images\<id>.png` beside the model
folder's file. Nothing is written while there's nothing to say (older files and readers are unaffected).

### Tests (+5, `EntourageTests`)
`BuiltIns_DrawStandOnTheGroundAndRepeat`, `Picture_FitKeyOutAndBleed`, `Document_CleanColoursAndTypes`,
`BimGo_RoundTripsEntourageAndPictures`, `ModelFolder_FileAndPictures`.

## How to check it
1. **Build**, run the Core tests.
2. **Panel:** Esc → **ENTOURAGE**. A moment after opening the model the cards show (trees, shrubs, people, cars) in their
   category colours. Click **Round tree** → right side: Trees · planted 7 m high · 0 placed.
3. **Plant:** **PLANT** → walking with the Place gun in ENTOURAGE mode (its panel: "PLACE · ENTOURAGE", Round tree). Aim at
   the ground, RMB a few times: trees appear standing on the ground, slightly different heights, some mirrored. Walk
   round one: it keeps facing you. Aim at a wall: "Aim at the ground…".
4. **Edit one:** aim at a tree (amber outline): **Z / X** shrink / grow, **R** mirrors, **LMB** removes it. Pick **Man
   standing** (LMB away from billboards opens the panel), aim at a tree, **RMB**: it becomes a man.
5. **Shadows:** sun panel → shadows on, mid-afternoon: each billboard casts its silhouette on the ground and on the
   building; trees shade people behind them; a billboard never darkens itself in odd patches.
6. **Look:** ENTOURAGE → **Own colours**: painted trees, people in coloured clothes. Back to **Colour silhouettes** →
   People **CHANGE…** → pick a colour: every person changes. **RESET**.
7. **Import:** **IMPORT PNG…** → a PNG cut-out (e.g. a tree with a transparent background): it appears under its guessed
   category; set its height; plant it. Try a JPEG on a white background: the white becomes see-through.
8. **Section box / cut:** billboards are cut like the model.
9. **Pictures:** photo mode (M), a 360, a short video and a bookmark thumbnail all show the billboards.
10. **Save (file):** SAVE, close, reopen: the billboards, your picture and the look come back. **Live:** plant some, F5:
    still there; **Export** from Revit, open the .bimgo: there too.
11. **Wheel:** with a family library (live), the wheel switches the Place gun back to FAMILIES (LMB opens the library).

## Not in (X-B2 next)
- Swapping Revit Planting / Entourage elements for billboards (per instance or per type, remembered for every later
  run). Also not in: billboards in reflections or AO, collision, undo for planting / removing, a car / person that keeps a
  fixed heading, wind sway.

## Things to look at when building
- `SceneRenderer.Billboards` binds its picture array to the scratch unit 0 only while drawing (the UI rebinds 0 on each
  flush); the scene's samplers keep 1–16.
- `RenderScene` calls `UpdateBillboards()` before the shadow maps (`ShadowSceneKey` hashes `_billboardKey`, bumped when the
  instances change), and draws them after the grass.
- The Place gun's `EntourageMode` is ENTOURAGE until a mode is chosen when there's no family library; `Begin(entry)` (a
  family picked from the library) switches it to FAMILIES.
