# X-D1 build notes: IES light profiles

## What's in
- **Core, `Scene/LightProfiles.cs`** (new):
  - `IesData`: IESNA LM-63 reader (1986 / 1991 / 1995 / 2002 headers, keywords, numbers split by spaces / commas /
    lines; TILT=NONE, TILT=INCLUDE skipped; type C only: types A / B are refused with a message). `Candela(gamma, C)`
    expands the symmetric layouts (one plane, 0–90, 0–180, 90–270) and wraps full files; bilinear between the file's
    angles; 0 outside its gamma range. `TotalLumens()` integrates the candela (the luminaire's output).
  - `LightProfile`: 32 gamma (0–180°) × 16 C (0–337.5°) bytes, peak 255, plus `Scale` = 4π / ∫table dΩ (so a profile
    moves light, never adds or removes it: an even light has scale 1), `Lumens` (file) and `BeamAngle` (half-peak,
    C-averaged; for the UI). `Sample()` is the same bilinear lookup the shader does.
  - `LightProfiles`: keys (`ies:<16 hex of the SHA-256 of the text, LF-normalised>`, `builtin:…`, `revit`, `lobe`), the
    five built-ins generated in code (downlight narrow 25° = cos²⁹ + spill, medium 40° = cos¹¹ + spill, wide flood (soft
    batwing to ~120°), wall washer (down plus a kick towards C 0° peaking 35° off nadir), pendant (65 % down / 25 % up /
    sideways)), and the per-light code `Encode` / `Decode` (below).
- **Core, data / format:** `LightingData.IesFiles` (`LightIesFile` hash, name, text) and per `LightSource` `Ies` (hash),
  `Aim` (gamma 0, default straight down), `Across` (C 0°, default X). `lighting.json` gains `ies` (hash, name) and per
  light `ies` / `aim` / `across` (left out for plain lights at their defaults); the files' text is `ies/<hash>.ies`.
  A missing / misnamed / oversized (> 512 KB) entry drops that file and its lights keep the lobe. New
  `light-profiles.json` (`LightProfileDocument`: per type choices + imported files' text; `Clean()` drops damage;
  `LightProfileFiles` reads / writes the model folder copy). `BimGoDocument.LightProfiles`; `ModelFolders`
  `LIGHT_PROFILES_FILE` (+ sidecar suffix, so it migrates / mirrors like the others). All additive (format 1).
- **Revit (`SceneExtractor.Lighting.cs`):** per fixture type (cached per document): `LightType.GetLightTypeFromInstance`
  → `PhotometricWebLightDistribution` (`PhotometricWebFile`, `TiltAngle`), else the *Photometric Web File* parameter.
  The file is found as given, else by name in the model's folder (3 deep), `Application.GetLibraryPaths()` folders and
  `%ProgramData%\Autodesk\RVT <version>` (cached per name); parsed to check it; copied by content. Orientation per
  instance: `FamilyInstance.GetTransform()` (× the link transform): gamma 0 = −BasisZ, C 0° = BasisX, both turned by the
  tilt about BasisY. No orientation (not a family instance) → the glowing surface's facing, else straight down. A light
  without Revit lumens takes the file's integrated output. Log line counts lights with a file and the files.
  Revit Export embeds the model folder's `light-profiles.json`.
- **Renderer:** `LIGHTS_GLSL` gains the `LightProfiles` std140 block (binding 2: `uvec4 uProfileData[768]` = 24 tables ×
  512 bytes, read as little-endian uints; `vec4 uProfileInfo[24]`, x = scale). No new sampler (the scene shader is at
  16). A light's `uLightColor.w` / `uFarColor.w` ≥ 0 is the old downward share; below 0 it is `-(code + 1)`: slot (5
  bits) · octahedral aim u, v (6 + 6) · turn about the aim from a reference axis (7). `profileLobe()` rebuilds the frame,
  works out gamma / C and reads the table bilinearly; it replaces the lobe for shadowed and far lights. Block size
  12 672 bytes (WebGL reports the same). `ArtificialLighting.ProfileBytes / ProfileInfo / SetProfile / ProfilesDirty`;
  `SceneRenderer.UploadLightProfiles()` (once per change, like the far lights).
- **Session (`GameSession.LightProfiles.cs`, new):** loads `light-profiles.json` (or the .bimgo's), resolves each light's
  key (type choice, else its Revit file, else lobe), parses each profile once, gives each distinct profile a slot (up to
  24; past that the lobe, said in the tab), and stores per light its slot and code. `Consider()` / the far list carry
  the code; moved and cloned fixtures re-encode with their instance rotation. A guessed output (`Estimated` at the
  1000 lm default) with a file profile takes the file's lumens. Probes re-bake after a profile change.
- **Pause → LIGHTS** (fourth tab; the tab row now sizes tabs to their labels with tighter spacing when equal widths
  can't hold them): fixture types × count, the current profile ("Revit: file · NN° beam", a built-in, "Imported: file",
  "BimGo lobe (no profile)", "(set)" when changed); ‹ › step Revit's file → built-ins → imported → lobe; IMPORT .IES…
  (validates, keeps the text once by content, assigns); RESET. A note when lights aren't on Glow + light. The list
  scrolls in the card.
- **Tests:** `LightProfileTests` (parser and symmetry, TILT=INCLUDE, refusals, integrated lumens, even = scale 1, energy
  kept, built-ins, keys, code round trip within 4.5° / 6°, .bimgo round trip incl. `ies/` and `light-profiles.json`,
  missing entry, `Clean()`, model folder file). Sample .ies files are hand-written in the test (CC0).
- **Checked here:** all four shaders that include the lights (scene, ground, grass, billboard) compile and link in
  headless WebGL2 with the block; a test render (`BimGo_IES_XD1_test.png`): lobe, narrow downlight, wall washer with
  C 0° to the wall and away, and the test .ies tilted 27° towards the wall, elevation and plan. The Python port of the
  table / code maths used for it matched the expectations in the tests.
- **Notices:** no third-party code or data; a line under "Not shipped with BimGo" says so and that Revit's / imported
  .ies files are only copied into the user's own files.

## What isn't
- Not compiled here (no .NET SDK in the sandbox): brackets, strings, char literals and UI glyphs checked by script;
  new member names checked across the partials.
- Revit **spot** lights (`SpotLightDistribution`: hotspot / spill) and hemispherical ones still use the lobe (they could
  become generated profiles later). Type A / B photometry is refused.
- IES tilt data (TILT=INCLUDE) is ignored. Revit's *Tilt Angle* (90° = down) turns the aim by its difference from 90° about the family's Y: the direction of that turn for tilts other than 90° is still to verify.
- Fully asymmetric files are kept at 16 C steps (22.5°); beams narrower than ~10° lose some sharpness at 32 gamma steps.
- The aim is packed to ~4° worst case (6-bit octahedral), the turn to 2.8°.
- No per-instance assignment (per type only, as agreed); no profile preview picture in the tab.
- The Revit Options window has no profile control (the app's tab only).

## How to check
1. Build; run the tests: `LightProfileTests` pass, the rest unchanged.
2. Revit: a model whose lighting fixture family uses a photometric web file. Go. The log lists the copied files
   ("photometric file … → ies/<hash>.ies") or says which can't be found.
3. Night, Lights = Glow + light: those fixtures throw the file's beam (crisp pool, wall scallops) instead of the soft
   lobe. Compare with RESET / "BimGo lobe".
4. Pause → LIGHTS: the type rows; ‹ › through the built-ins: the scene changes at once. Wall washer next to a wall:
   should light the wall when the family's X faces it; if your families aim it elsewhere, tell me which way (C 0° /
   tilt convention).
5. IMPORT .IES… on a type: assigned; a broken file gives a reason. Go again / F5: choices kept. Export .bimgo and open
   it: same. In a .bimgo, a change marks the file unsaved.
6. Gizmo-turn or clone a profiled fixture: its beam turns with it. Take a photo with many fixtures: far fixtures use
   their profiles too.
7. Brightness: overall light from a fixture is the same with any profile.

## Revision 2 (2026-10-20): beams went sideways
Gavin's first test: ceiling downlights threw their beams sideways along the ceiling. Cause: a photometric web's
*Tilt Angle* is measured from horizontal (Revit's usual value is 90° = straight down), and revision 1 turned the aim by
the whole angle, so a 90° tilt pointed every beam horizontally. Fixes (`SceneExtractor.Lighting.cs` only):
- The aim turns by (90° − tilt), and only when a tilt was read (other light types: no turn).
- A real glowing lens (a self-illuminated or keyword material, not the guessed bottom faces) more than 60° off the
  family's aim overrules it (counted in the log line, the first 5 logged). Fixtures without an obvious lens keep the
  family aim.
- Each type with a file logs its tilt ("tilt 90° (90° = down)").
- Snapshots made with revision 1 keep the wrong aim: Go / F5 again.

How to check: F5 (or Go), the same room: the downlights throw down onto the floor, with scallops on nearby walls; the
log shows "tilt 90°" for those types and few or no "aimed by their lens".

## Revision 3 (2026-10-20): light leaking through fixtures, nested up / down webs
Gavin: light leaked through some fixtures (e.g. onto the wall behind, or outside); the wall light in question holds two
nested lighting families with their own webs (one up, one down), placed on work planes.
- **Solid lenses:** the glow's facing was the sum of its triangles' normals. A solid lens glows on every face, the
  normals cancel and the remainder is noise, which then pushed the light 5 cm off the lens in a random direction
  (possibly into the wall) and, since r2, could overrule the family's aim. Now the glow has a facing only when
  |Σ normals| ≥ 0.5 × its area (a flat lens); otherwise the light sits at the glow's centre and aims by the family.
- **Nested light sources:** a fixture with no light source of its own:
  - with shared nested lighting families (`GetSubComponentIds`): those are elements of their own and get their own
    lights; the host gets none (it used to get a guessed one in its middle: a leak);
  - otherwise its family is opened once per extraction (`EditFamily`, closed unsaved; not during the family library
    pass) and every nested Lighting Fixtures instance with a light source becomes a light of its own: at its
    transform (host × nested, work plane flips included) 2 cm along its aim, its own web / tilt / lumens / colour
    (lumens: its own, else its file's, else the host's shared out). The log names "“type” holds N nested light
    source(s)" and the count.
- The app: a cloned fixture brings all its lights (they are consecutive in `lighting.json`); the LIGHTS tab counts
  fixtures, not light sources; "Revit" for a type uses each light's own file (the up and down halves keep theirs).

How to check: F5 / Go. The wall light throws up and down (two scallops on the wall), nothing on the wall behind or
out through the glass; the log shows the nested sources. Other fixtures with solid glowing lenses: no stray light
behind them.

## Revision 4 (2026-10-20): flipped / mirrored fixtures
Gavin: the nested up / down wall lights look right on some walls, not others (the same family placed another way).
`FamilyInstance.GetTransform()` leaves out facing / hand flips and mirroring, so a flipped host put its nested sources
(and its aim) on the wrong side of the wall. Now `GetTotalTransform()` for the fixture and for its nested sources. As a
safety net, a nested source landing more than 15 cm outside its fixture's extracted bounds is pulled inside them
(counted in the log line, the first 5 logged with the position).

How to check: F5 / Go; the same wall lights on every wall throw up and down onto their own wall; the log's "pulled
inside their fixture" count is 0 (if not, the transforms are still off somewhere: send the log lines).

## Revision 5 (2026-10-20): nested wall lights still dark on some walls; .ies files not found
Gavin's logs (r4, "All fixtures live" on): 138 lights, 48 from nested sources, 0 pulled inside, yet some up / down
wall lights cast nothing; "PIL_306313_EDIT.IES" and "Lens8101-3.IES" not found; 25 fixtures "aimed by their lens"
with the lens facing up against a family aim straight down.
- **Nested placement from geometry:** the 15 cm "pulled inside" slack is about a wall light's depth, so a source
  mirrored behind the wall passed the check and was shadowed by the wall. Now the geometry walk records every nested
  Lighting Fixtures family instance inside a fixture's geometry with its accumulated transform (what Revit draws:
  mirroring and flips included, `NoteNestedPlacement` from `Walk`), and each nested light source takes the placement
  with its "Family: Type" (in order; same-type halves are interchangeable). The family document's transform is only a
  fallback (counted in the log line: "N placed by transform, not geometry").
- **Aim:** a fixture whose type has a light source of its own now always aims (and sits 5 cm in front of its glow)
  along its family (−Z, tilt); a lens's facing is only used for fixtures without a light source (as before X-D1).
  Lens faces modelled backwards no longer push lights up into the ceiling.
- **.ies search:** also Revit's additional render appearance paths and BimGo's texture search folders, each with its
  parent folder (3 deep); a file still not found logs every folder searched.

How to check: F5. Log line: "48 from nested light sources (0 placed by transform, not geometry)"; every wall light
throws up and down; the "not found" lines either gone or followed by the folders searched (then say where the file is).

## Revision 6 (2026-10-20): mirrored wall lights still dark
r5 log: "48 placed by transform, not geometry": non-shared nested families are merged into the host's symbol geometry,
so there are no nested instances to read. Gavin: the dark ones are all the mirrored instances; the instance transform
(even `GetTotalTransform`) evidently doesn't carry the mirror for these. Now the nested sources are placed under the
transform Revit **draws** the host's symbol with (its top-level geometry instance, `_fixtureDrawTransform`), times the
nested transform from the family document; the instance transform is only the last fallback. The log line splits the
48 by method and counts hosts drawn unlike their instance transform (first 3 logged with both X axes and
determinants: a mirrored one shows det -1 on the drawn side).
.ies files still not found: `PIL_306313_EDIT.IES`, `Lens8101-3.IES` (searched folders listed in the log): ask where they are.

How to check: F5. Log: "48 from nested light sources (0 by nested geometry, 48 by the drawn host, 0 by the instance
transform; N hosts drawn unlike …)" with N about the number of mirrored wall lights; all wall lights throw up and down.

## Revision 7 (2026-10-20): mirrored instances
r6 log: "48 by the drawn host … 0 hosts drawn unlike their instance transform": for Gavin's mirrored wall lights the
drawn (top-level geometry instance) transform equals the instance transform, so neither carries the mirror, yet the
drawn geometry is mirrored. Every failing light is a mirrored copy. The nested sources' family offsets (a few cm out
from the wall) therefore landed behind the wall, inside r4's 15 cm slack, shadowed by the wall.
Now each nested source is placed under the host transform three ways (as is; mirrored across the family's YZ plane;
across its XZ plane) and keeps the one whose origin lies inside (or nearest) the fixture's extracted bounds, which come
from the geometry Revit really drew; as is on a tie. The log line counts each choice; the first 3 non-"as is" choices
are logged with `Mirrored` and the distances.

Revision 8 (same day): Gavin: Revit mirrors with `HandFlipped` / `FacingFlipped` (one true = mirrored, both = a half
turn), which the transforms leave out. The nested placement is now host × flips (hand: across the family's YZ plane,
facing: across its XZ plane) × nested; r7's bounds check stays as a guard (it overrules the flips only when an
alternative lands clearly closer to the drawn fixture, logged).

How to check: F5. Log: "placed 48 by their hand / facing flips, overruled by the drawn bounds: 0 …; N hosts flipped";
every wall light throws up and down.

## For the next build / handover (2026-10-20)
- Gavin fixed the missing .ies files (`PIL_306313_EDIT.IES`, `Lens8101-3.IES`) on his side; no BimGo change needed.
  The search (r5) covers the model folder, Revit's library paths, `%ProgramData%\Autodesk\RVT <ver>`, the additional
  render appearance paths and BimGo's texture search folders (each with its parent, 3 deep), and logs every folder
  searched when a file isn't found.
- Gavin's working copy may differ from r8 (his own fixes): **ask for his zip before editing** the X-D1 files
  (`SceneExtractor.Lighting.cs`, `SceneExtractor.cs`, `GameSession.Lights.cs`, `GameSession.LightProfiles.cs`).
- Lessons for Revit lighting: a web's Tilt Angle is from horizontal (90° = down); instance transforms (even
  `GetTotalTransform`, and the drawn geometry instance's) leave out `HandFlipped` / `FacingFlipped`, so placements
  derived from family-space offsets must apply them (hand = across the family's YZ plane, facing = across XZ; both = a
  half turn); non-shared nested families are merged into the host's symbol geometry (no nested geometry instances: read
  them from the family document); solid glowing lenses have no facing; lens faces can be modelled backwards.
- Open: confirm r8 on the mirrored wall lights (log "placed 48 by their hand / facing flips, overruled …: 0").
