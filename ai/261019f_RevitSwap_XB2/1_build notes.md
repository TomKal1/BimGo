# Round X, build X-B2: Revit Planting / Entourage swapped for billboards (2026-10-19)

Built on X-B1 (same zip: B1 isn't confirmed yet). **Not compiled here** (no .NET SDK): build first (all three
projects: the Revit add-in changed too), then the Core tests (+3 more in `EntourageTests`: 8 in all). Checked: brackets /
strings / char literals, encodings, new member names across the partials, UI strings against the font atlas. No shader
changes. **No new keys.**

## In

| Item | What it does | Where |
|---|---|---|
| **Recorded at every Go / F5 / Export** | Every Planting and Entourage family instance in scope (the host and the ticked links; with "active view only", what the view shows) is recorded, **whether or not those categories are ticked** (no geometry needed): its UniqueId, family and type, its base (location point), height and width (from its box), and its extracted element when the category was extracted. | Revit `SceneExtractor.Entourage.cs`; `entourage-slots.json` |
| **Automatic swap** (default on) | Each instance without a rule becomes a billboard at its own height: by its names first (palm, conifer / pine…, hedge, grass / lomandra, poplar / columnar, shrub / bush; SUV / van / truck, car, child, woman / female, phone, man / male / person), else its size (planting under 2 m → a round shrub, or a hedge when long and low; narrow → columnar tree; wide → spreading; else round; entourage car-sized → car or SUV, under 1.4 m → child, else a man). Mirrored at random but always the same for an instance. ENTOURAGE → FROM REVIT → untick **Swap the rest automatically** to see Revit's own elements again (rules still apply). | Core `EntourageSlots.AutoType`, `EntourageSwaps.Resolve` |
| **Per type** | ENTOURAGE → **FROM REVIT** lists each Revit type ("Family : Type", ×count, what it shows: "(auto)" or "(set)"). **PICKED** = every instance of that type becomes the billboard picked on the left; **REVIT** = Revit's own element (when its category is extracted); **HIDE** = nothing; **AUTO** = back to automatic. | `BuildEntourageFromRevit` |
| **Per instance** | With the Place gun in ENTOURAGE mode, aim at a swapped billboard (its panel says "From Revit: Family : Type"): **RMB** swaps this one to the picked type, **LMB** hides this one, **Z / X** size it, **R** mirrors it. These one-offs win over the type's rule; FROM REVIT shows how many there are and **CLEAR** (twice) undoes them all. | `SwapBillboard`, `RemoveBillboard`, `ResizeBillboard`, `FlipBillboard` |
| **Revit's element hidden** | When a billboard (or HIDE) stands in for an instance whose geometry was extracted, that element is hidden in the walkthrough (drawing, shadows, picking, collision), the same way a colour profile hides rows; it comes back when the rule goes. A swapped billboard disappears with its element (demolished, hidden with Scan, the Planting category toggled off, a profile hiding it). | `ApplyEntourageHidden`; `ProfileHides` |
| **Remembered** | Rules are saved in `entourage.json`: in a live session in the model's BimGo folder, so **every later Go, F5 and Export** applies them again (by UniqueId and "Family : Type": instances keep their rule as the model changes); in a .bimgo with the file. | `EntourageDocument.Swaps`, `SwapRevit` |

### Files on disk (additive)
- `entourage-slots.json` (in a .bimgo; live snapshots too): `slots` (`uniqueId`, `elementId`, `link`, `linkKey`,
  `category`, `family`, `type`, `base`, `height`, `width`, `element`).
- `entourage.json`: `swapRevit` (true), `swaps` (`scope` instance / type, `key`, `type` = a billboard key, `keep` or
  `none`, instance-only `height`, `flip`). Files from X-B1 read as automatic swapping on, no rules.

### Tests (+3)
`Swaps_AutoChoiceByNameThenSize`, `Swaps_InstanceThenTypeThenAuto`, `Slots_RoundTripInBimGo` (`TestData.BuildScene` /
`BuildDocument` take optional slots).

## How to check it
1. **Build** (Core, App, Revit), run the Core tests.
2. A Revit model with some planting (RPC trees, a hedge family) and entourage (RPC people, a car), Planting / Entourage
   **unticked** in Options (the default). **Go**: billboards stand where they are (trees as trees, people as people, cars
   as cars), at their Revit heights; a palm as a palm.
3. Tick Planting in Options, **Go** again: the same billboards; Revit's planting geometry hidden under them (no doubled
   trees). ENTOURAGE → FROM REVIT → on a tree type **REVIT**: the Revit trees come back for that type; **AUTO**: billboards
   again.
4. **Per type:** pick **Conifer** on the left, on a tree type **PICKED**: every instance becomes a conifer. **HIDE**: they
   all go.
5. **Per instance:** Place gun in ENTOURAGE mode, aim at one swapped tree (panel: "From Revit: …"): **RMB** with Man
   standing picked → only that one becomes a man; **Z / X** resize it; **R** mirrors; **LMB** hides it. FROM REVIT shows
   "n one-off changes" → **CLEAR** twice → back to the type's choice.
6. **Remembered:** after some type and instance changes, **F5** (or close and **Go**): all kept. **Export** a .bimgo and
   open it: the same. Untick **Swap the rest automatically**: unruled ones show Revit's own (or nothing when not
   extracted); ruled ones stay.
7. **Hidden with the element:** with Planting ticked, Demolish a swapped tree's element from Revit (or toggle the Planting
   category off in the pause menu): its billboard goes too.
8. Planted billboards (X-B1) are unaffected by all of this.

## Not in
- Billboards for other categories (furniture, site), matching link instances by anything but their link instance,
  picking a Revit type's row by aiming (use FROM REVIT), sending swaps back to Revit (nothing is written into the model).

## Things to look at when building
- `ProfileHides(e)` now also returns true for elements a swap hides (`EntourageHides`), so every place that composes the
  hidden state (Scan hide, demolish, profiles, grass) includes it; `ApplyProfileHidden` adds it explicitly.
- `UpdateBillboards` rebuilds the swaps when the entourage revision changes (hiding / showing elements bumps
  `_sceneRevision`), and the instances when the revision, `_sceneRevision`, the pictures or "Show billboards" change.
- `SceneExtractor.Run` calls `CollectEntourageSlots(sources, view)` after the geometry (to link extracted elements).
