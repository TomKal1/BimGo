# Y-C2 build notes: per-fixture light profiles, polar candela plots

Not compiled here (no .NET SDK). Brackets, strings and char literals script-checked in every changed `.cs` file, UI
strings checked against the font atlas. The polar plot's geometry checked with a Python port (the table resample, the
bilinear lookup and `LightProfilePolar.Build`): a spot hangs below the centre, a hemisphere is a circle through it.

## What's in
- **Per-fixture choices** (Core `LightProfileModels.cs`). `LightProfileDocument.Instances`: `{ key, familyType, profile }`
  (`instances` in `light-profiles.json`; additive: older files have none, older builds ignore it). Values as a type's
  (`revit`, `lobe`, `builtin:…`, `ies:<hash>`). The key (`LightProfileDocument.InstanceKeyOf`): the UniqueId (or
  "id:" + ElementId), after the link instance's UniqueId and "/" for a linked fixture (`link:<n>` when the link has no
  UniqueId); family library templates have none. `InstanceChoiceFor` (indexed), `SetInstanceChoice`, `ClearInstances(type)`
  (RESET ALL), `ResolveChoice(instance, type)` = the fixture's, else the type's; `KeyFor(choice, revitKey)` = no choice /
  `revit` → Revit's file or distribution, `lobe` → none, else the choice. `Clean()`: drops damaged entries, the last per
  key wins, **keeps** keys not in this snapshot (a hidden link or phase can bring them back), at most 20,000.
- **Resolving** (`GameSession.LightProfiles`): `ProfileKeyOf(light index)` uses the above; each light's fixture key is
  worked out once (`_lightInstanceKey`; nested sources share their fixture's). The 24-slot limit is unchanged (the
  rest use the lobe: said in the tab and in the dock).
- **Ctrl+K: the fixture dock** (`GameSession.FixtureProfile.cs`, new `InputAction.FixtureProfile`, Tools, `DEFAULTS_VERSION`
  7 + its `DefaultsChanges` row, F1 situation "Fixture light profile", README §3 row). Aim at a lighting fixture (or
  within 0.75 m of one: small downlights flush with the ceiling) and press Ctrl+K: the right-hand column gets a dock
  (the cursor is free, the player stands still, like the grids panel; Ctrl+K / Esc / CLOSE closes; it closes the other
  docks and they close it). It shows "Family : Type", the id, level and link; the fixture's profile with
  "(this fixture)" / "(from the type)"; ‹ › through the same choices as the type's row (landing on the type's choice
  clears the fixture's own); IES… (import a file for this fixture only); RESET (back to the type's); the polar plot (220 px);
  the type's profile and "N fixtures of this type · M set individually". The fixture is highlighted (sun yellow).
  **Clones** follow their original (they share its lights): aiming at a clone opens the original's dock with a note
  that a change changes both. A moved fixture is found where it was moved to.
- **LIGHTS tab:** each type row draws its profile's polar plot (58 px, rings at 50 / 100 %) beside the label and
  buttons; "· N fixtures set individually" + **RESET ALL** (clears the type's fixture choices, also those of fixtures
  not in this snapshot); a line on Ctrl+K and how to read the plots.
- **Polar plot** (Core `LightProfilePolar`): from `LightProfile.Sample` (what the shader reads), 64 segments per
  curve, C 0–180 (C 0 right) and C 90–270 (C 90 right), gamma 0 at the bottom, normalised to the larger plane peak;
  built once per profile key and cached (`_polarCache`; cleared for a re-imported file); drawn with `_ui.Line`
  (no per-frame allocation). C 0–180 in the accent colour, C 90–270 muted; rings 25 / 50 / 75 / 100 %; the lobe (no
  profile) says "lobe".
- **Tests:** `LightProfileTests`: fixture choices round trip / Clean / RESET ALL / IsEmpty; resolve precedence; keys
  with and without links (and library templates); the polar plot (closed, peak 1, a downlight sends nothing up, an
  even light is a circle). `ControlsTests.Help_KeyAndGamepadText` now borrows Ctrl+J (Ctrl+K is taken).

## What isn't
- A clone can't have a profile of its own (it follows the original); after F5 a committed clone is a fixture of its
  own and uses its type's profile.
- Placed family-library copies (Place gun) have no identity: the dock shows their type's profile, read-only.
- The plot shows the profile at BimGo's 32 × 16 resolution (what the renderer uses), not the file's own angles.
- No hover enlargement of the tab's small plots; no RMB-drag look while the dock is open.

## How to check
1. Open a model with lighting fixtures (live or a .bimgo), K to Glow + light.
2. Esc → LIGHTS: each type row has a small polar plot on the right (downlights hang down; the lobe says "lobe"). Step
   a row with ‹ ›: the plot follows.
3. Walk under a fixture, aim at it, **Ctrl+K**: the dock opens on the right, the fixture is tinted yellow, the cursor is
   free. Its row reads "… (from the type)".
4. Press › a few times: the label turns yellow "(this fixture)", that fixture's light changes, its neighbours of the
   same type don't. Close (Ctrl+K / Esc), walk on: it keeps its profile.
5. Esc → LIGHTS: that type's row says "· 1 fixture set individually". Step the type's profile: the overridden fixture
   keeps its own, the rest change.
6. Ctrl+K on the same fixture → RESET: it follows the type again; the tab's count goes.
7. Ctrl+K on a fixture → IES…: only that fixture takes the file (notice "This fixture: name.ies").
8. Live: F5 (or Go again): the fixture's choice is still there (`light-profiles.json` in the model folder has an
   `instances` list). File mode: save, reopen: still there. Export a .bimgo from Revit: still there.
9. Set two fixtures individually, Esc → LIGHTS → RESET ALL on their type: both follow the type again.
10. A linked model's fixture: Ctrl+K works; its key in the json starts with the link instance's UniqueId.
11. Clone a fixture that has its own profile (Clone gun): the copy shows the same light; Ctrl+K on the copy says it
    follows the original.
12. Aim at the ceiling just beside a small downlight, Ctrl+K: it still finds the downlight. Aim at a wall far from any
    fixture: a toast says to aim at a fixture.
13. F1 with the dock open: "Fixture light profile" rows. Controls: "Light profile of the aimed fixture (dock)" under
    Views and tools, Ctrl+K; first start after this build lists it as a new default.
14. Tests: `LightProfileTests` and `ControlsTests` pass (README §3 already has the Ctrl+K row).

## Revision r2 (2026-10-21)
- The import buttons (LIGHTS rows and the fixture dock) read **IES…** instead of IMPORT .IES… (it didn't fit beside
  the plot). Same action: pick an .ies file and assign it.
