# Round X, build X-B3: entourage polish (2026-10-19)

Built on the X-B2 zip (no working-copy changes). **Not compiled here** (no .NET SDK in the sandbox): build Core and App
(the Revit add-in is unchanged), then run the Core tests. Checked by script: brackets / strings / char literals in every
changed `.cs` file, UI strings against the font atlas, new member names across the `GameSession` partials, encodings
(BOMs kept on `GameSession.cs` and `InputCatalog.cs`; new files LF, no BOM). **No shader changes. One new key: `HoldScale`
(Alt).** Preview sheets were drawn by the Python port (`tools/entourage/preview`), not by the app:
`BimGo_EntouragePolish_XB3_library.png` (own colours above, the silhouette look below), `BimGo_EntouragePolish_XB3_scene.png`
(a composited mock-up, not an in-game render).

## In

| Item | What it does | Where |
|---|---|---|
| **Live resize** | Place gun in ENTOURAGE mode, aim at a billboard, **hold Alt and turn the wheel**: it grows / shrinks 3 % a notch, live; the gun panel shows "Resizing: 7.02 m high". It's saved **once, when Alt is released** (a toast confirms). **Z / X** still step 10 % per tap; **held** longer than 0.35 s they scale smoothly. While Alt is held the wheel never switches modes (no billboard aimed: a toast says to aim at one). The aimed billboard stays locked while resizing, so drifting the aim off it doesn't switch. Works on Revit-swapped billboards too (saved as that instance's one-off height). Range 0.2–60 m. | `PlaceGun.OnWheel` / `Update`, `Gun.OnWheel`, `GameSession` input (before gun / mode switching), `PreviewBillboardHeight`, `CommitBillboardHeight` |
| **New key** | `HoldScale` "Resize the aimed billboard (hold + wheel; Place gun, entourage)", default **Alt**, rebindable in CONTROLS; listed in F1 help for the Gizmo / Clone / Place guns. Controls defaults version 4 (fix after the first test run: `DefaultsChanges` lists it as added, so existing users see it in the new-controls notice). | `InputCatalog` |
| **People redrawn** | Adult proportions (head ≈ 1/7.5 of the height; the child ≈ 1/5.5), shoulders, waist and hips; limbs taper joint to joint (thigh → knee → calf → ankle, shoulder → elbow → wrist) as smoothed outlines, no capsule "sausages"; hair cap and fringe; shoes. Poses: man at ease with his weight on one leg and a hand in a pocket; woman walking towards you (front leg straight, back heel lifted, arms swinging, A-line dress); child waving in a T-shirt and shorts; person on the phone with a coat and a shoulder bag. | `EntourageArt` `PersonMan` / `PersonWoman` / `PersonChild` / `PersonPhone`, `Canvas.Limb`, `Canvas.Shape` |
| **Trees redrawn** | Branch skeletons (`Grow`: 2–3 children per fork, tapered, bent) carrying **clusters of small leaves** (no big circles): denser in the middle, ragged at the edge, with gaps where the branches show; lit across the whole canopy from the upper left so clusters don't each read as a ball. Round, columnar, spreading, conifer (drooping needle sprays) and round shrub; a new **palm** (curved ringed trunk, arching pinnate fronds with tapering leaflets, dead fronds under the crown). | `Grow`, `Foliage`, `TreeRound` … `ShrubRound`, `TreePalm` / `Frond` |
| **Gum tree** (new) | A eucalypt, 12 m: pale crooked trunk with bark patches, an open drooping crown with sky through it. | `tree-gum` |
| **Traced drawings pipeline** | For Gavin's drawings: `tools/entourage/trace.py` thresholds a filled silhouette (dark on light, or a transparent PNG), takes every contour (holes kept), drops specks, simplifies (Douglas–Peucker), normalises to the ground and a height of 1, and prints a C# entry for `EntourageShapes.ALL` (with `--preview` showing it as BimGo will draw it). In Core the rings are filled even-odd at 512 px (`Canvas.Rings`) with Chaikin smoothing; an entry with a built-in's key replaces that picture, a new key adds a type. Empty for now. Tested on a figure rendered and re-traced (2 rings, 244 points). | `Scene/EntourageShapes.cs`, `EntourageArt.WithShapes` / `DrawShape`, `tools/entourage/` |
| **IMPORT FOLDER…** | ENTOURAGE footer: pick a folder; every PNG / JPEG in it (not sub-folders) is read and cut out on a worker (the notice strip counts "Importing pictures: 12 / 40…"), then added together with **one save**; categories guessed from the file names (with a height to suit); the summary says how many of each and what was skipped (and why). | `ImportEntourageFolder`, `PollFolderImport`, `PreparePicture`, `EntourageStore.AddTypes` |
| **Card counts** | A card's number includes the Revit instances swapped to that type; the PICKED line adds "+ n from Revit". | `EntourageTypeCount` (counted when the entourage changes) |
| **Licensing** | THIRD-PARTY-NOTICES: built-in billboards are drawn by BimGo (MIT-0), traced ones will be Gavin's own; imported packs are never shipped, but a saved .bimgo carries them (sharing it follows the pack's terms). | `THIRD-PARTY-NOTICES.txt` |

### Tests
`EntourageTests.BuiltIns_DrawStandOnTheGroundAndRepeat` now covers the gum tree too (every built-in stands on the
bottom edge, people narrower than 0.6, seeded), plus unique keys, `EntourageShapes.Find`, `FillOf`. The controls tests
check `HoldScale` = Alt against every other key (no clash). README §3's table has the new row (if the generator words it
differently, the README test rewrites it and reports Inconclusive: commit that).

## Not in (later)
- Gavin's traced drawings themselves (send them as photos / scans / PNGs: filled silhouettes, one figure per file).
- A detail layer (inner lines) for traced figures; a "planted at" height for built-in types; an exact height typed per
  billboard.
- Cars, hedge and grass are unchanged.

## How to check it
1. **Build** Core and App; run the Core tests (all pass; README §3 possibly regenerated).
2. **Library:** Esc → ENTOURAGE. The trees, shrubs and people cards show the new drawings (compare with
   `BimGo_EntouragePolish_XB3_library.png`); a **Gum tree** card sits after the palm. Switch LOOK between
   *Colour silhouettes* and *Own colours*: both read well.
3. **In the scene:** PLANT a few of each; check them in sun with shadows on (the silhouettes cast the new shapes), in a
   photo, and from far away (mipmaps: no fringe or dark halo; thin palm leaflets may thin out with distance).
4. **Live resize (planted):** Place gun (9) in ENTOURAGE mode, aim at a billboard, hold **Alt**, roll the wheel both ways:
   it scales smoothly, the gun panel reads "Resizing: x.xx m high". Release Alt: one toast with the height. Esc →
   ENTOURAGE → close and reopen the model (or F5 in a live session): the height stuck. Look at `entourage.json` in the
   model folder (live): written once per resize, not per notch.
5. **Wheel safety:** with Alt held and nothing aimed, roll the wheel: a toast, and the gun does **not** switch to FAMILIES.
   Without Alt the wheel still switches families ↔ entourage; Shift + wheel still changes guns.
6. **Z / X:** tap: 10 % steps as before. Hold X: after a moment it grows smoothly until released (then saved).
7. **Revit swaps:** aim at a billboard standing in for a Revit Planting instance ("From Revit: …"): Alt + wheel resizes
   it; after release ENTOURAGE → FROM REVIT counts one more one-off change; F5 / Go keeps it.
8. **IMPORT FOLDER…:** ENTOURAGE → IMPORT FOLDER… → a folder with several PNGs (e.g. named "oak tree", "woman", "car",
   one unreadable file): the notice counts up, then "n pictures added (… categories guessed from the names) · 1 skipped
   (…)". The cards appear under their categories; live sessions: one `entourage.json` write, the pictures in
   `entourage-images\`.
9. **Counts:** with Revit Planting swapped to *Round tree*, the Round tree card's number includes them and the PICKED
   line says "+ n from Revit".
10. **Rebinding:** CONTROLS → the Gizmo / Clone / Place guns group lists "Resize the aimed billboard"; rebind it (e.g. to
    Ctrl) and resize again.
