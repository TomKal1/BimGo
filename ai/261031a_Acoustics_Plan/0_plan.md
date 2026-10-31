# Sound pressure maps from placed sound sources: evaluation and plan (TomKal1 fork)

Fork-only proposal (TomKal1/BimGo), not one of Gavin's rounds. Nothing here is built yet. It records how acoustic sources
and a sound pressure heat map would fit the code as it stands, so a later session can start from this note.

**Goal:** the user puts sound sources on surfaces in the walkthrough, for example on two pumps in a mechanical room. Each
source has a sound power level (Lw, octave bands). **Calculate** returns sound pressure levels (Lp) over the room as a
heat map. The sources add on an energy basis, the room's surfaces reflect or absorb them, and the result shows as a
false-colour map with a legend, a readout under the crosshair and a target check.

## What already exists (and is reused)

| Need | Existing code | Notes |
|---|---|---|
| Click a point on a surface | `Gun` + `AimInfo` (`Guns/CommentGun.cs` is the closest model: hit point + normal, element, level) | `aim.Hit.Element` gives the `ElementRecord`: category, family type, UniqueId, bounds |
| Markers that persist, list panel, edit, remove | Comments (`CommentStore`, `GameSession.Comments`), measurements | Same lifecycle for sources |
| Per-model sidecar documents, also inside `.bimgo` | `Format/SidecarJson.cs`, `ModelFolders`, `light-profiles.json`, `sun-studies\` | `acoustic-sources.json`, `acoustic-materials.json`, `acoustic-studies\` |
| Room selection, test grids on floors / walls, pass / fail, CSV, saved studies | Studies (`SunHoursStudy`, `GameSession.SunHours`, Core `SunHours` / `Daylight`) | Same receiver grid (ear height instead of work plane) |
| Static false-colour overlay, legend, value labels | `Rendering/CalcOverlay.cs`, study cells, `ScreenLabelLayout` | One static buffer, no per-frame cost |
| Fast ray casts | App `Physics/Bvh.cs` (read-only once built, no allocation per query), Core `Clashes/ClashTree.cs` | Solver needs its own triangle set (see below) |
| Background job with progress and cancel | `GameSession.NativeLighting` → `NativeCalculationJob` (`Task.Run`, snapshot input) | Same pattern |
| Material per triangle | `MaterialData.VertexMaterial` → `SceneMaterial` (name, UniqueId) | Only when "Extract materials" was ticked; no acoustic data (see Absorption) |
| Element parameters | `ParameterTable` (per element, pooled) | Lw can be read from a family parameter when the model has one |
| Audio output | `Audio/SoundSystem.cs` | Optional later: hear the calculated level while walking |

**Not reused: `Lighting.Engine`.** Steady-state acoustic radiosity uses almost the same maths as the lighting
interreflection solve: patch exitance = (1 − α) × incident energy, with form factors from rays. The receiver is
different, though. A microphone is omnidirectional (energy density, no cosine), and there is air absorption per
band. More importantly, the engine is a pinned, byte-checked snapshot under a separate copyright, and its
`UPSTREAM-NOTICE.md` says not to maintain a second solver there. Acoustics needs its own MIT-0 code. Decide first
whether WW Tools will want the same calculation. If it will, give the solver a shared home from the start, so it does
not repeat the lighting engine's licensing and sync situation.

## The physics (what "adds, then reflects or is absorbed" means)

Every calculation is done per octave band (63 Hz – 8 kHz, 8 bands). It is then A-weighted (−26.2, −16.1, −8.6, −3.2,
0, +1.2, +1.0, −1.1 dB) and summed for dB(A).

- **Sources add as energy:** L = 10 log₁₀ Σ 10^(Lᵢ/10). Two equal pumps give +3 dB. A pump 10 dB quieter than the other
  adds about 0.4 dB.
- **Statistical method (tier 1, Hopkins–Stryker):** Lp = Lw + 10 log₁₀( Q / (4πr²) + 4 / R ). Here R = Sᾱ / (1 − ᾱ) is the
  room constant and Q is directivity: 1 in free space, 2 on a floor, 4 at a wall-floor edge, 8 in a corner. Placing on a
  surface gives Q directly, by counting the reflecting planes close to the source. The reverberation time comes from
  the same data: Sabine T = 0.161 V / (A + 4mV), with Eyring used when ᾱ > ~0.2.
  - A worked example: a 10 × 8 × 4 m concrete room (S = 304 m², α ≈ 0.02 at 1 kHz), and one pump with Lw = 90 dB on the
    floor. At 3 m it gives about 88 dB, almost all of it reverberant. Two pumps give about 91 dB. Lining the 80 m²
    ceiling with α ≈ 0.8 absorber brings the single pump at 3 m down to about 78 dB. That is roughly a 10 dB benefit, and
    it is the number a designer wants. (The α values are illustrative.)
- **Geometric method (tier 2, hybrid ray tracing):** the reverberant term above is the same everywhere in the room.
  For the map to reflect the room's shape, equipment shadowing and treatment placed in one area, use:
  - **Direct field, deterministic:** each cell, each source: Lw − 10 log₁₀(4πr²) − air absorption, kept only when a
    line-of-sight ray is clear. Later, Maekawa barrier attenuation can replace the hard cut-off behind equipment.
  - **Reflected field, stochastic (energy particle tracing):** each source emits N rays uniformly, each carrying an
    8-band energy vector. One geometric trace covers all bands. At each hit, energy × (1 − α_b) and air ×
    e^(−m_b d). The new direction is specular, mixed with Lambert by a scattering coefficient (default 0.2–0.3, higher
    for equipment-filled rooms). The ray stops below −60 dB (Russian roulette). Cells collect a track-length estimate:
    w = Σ P·ℓ / (c·V_cell), and Lp = 10 log₁₀(c·w / 10⁻¹²).
  - **Floor reflections are modelled:** in this method the source is omnidirectional (Q = 1). A pump standing on the
    floor gets its floor reflection from the geometry. Q = 2 here would count it twice.
  - **Rough cost:** 0.5 m cells in that room give ~320 receivers. 2–5 × 10⁵ rays × ~30 bounces is a few million ray casts:
    seconds on a worker thread.

## Absorption data (the main data gap)

Revit materials carry no acoustic properties: there is no acoustic asset, only appearance, physical and thermal.
`SceneMaterial` has a name and UniqueId only. Absorption is resolved in this order:

1. **A per-model override:** `acoustic-materials.json` (material UniqueId → α[8] + scattering), edited in a MATERIALS tab
   of the acoustics panel, like the LIGHTS tab in `light-profiles.json`.
2. **A Revit parameter**, when the model has one (for example a shared "NRC" or per-band α on the material or type).
   Parameter names are configurable.
3. **A keyword class** from the material name: concrete, CMU, gypsum, acoustic ceiling tile / ACT, mineral wool,
   carpet, timber, glass, metal deck, and so on. These use a built-in octave-band α table, with sources cited in the file.
4. **The category default** (walls / floors / ceilings / doors / glazing / equipment). This is also the only option when
   materials were not extracted.

**Glass is a reflector:** unlike the lighting input ("transparent faces are treated as openings"), glazing reflects
sound. The source's own element is left out of tracing (as the lighting calc leaves out fixture housings). Otherwise
rays would start inside a closed solid.

## Sources: placing on a surface

- **A new SOUND gun** (or a slot on the gun wheel):
  - **LMB on a surface** adds a source. When the hit element is equipment (Mechanical Equipment, Plumbing Fixtures,
    Electrical Equipment, Generic Models), the source binds to the element: UniqueId + link + an offset. It then
    follows Gizmo moves and survives F5. The default acoustic centre is the middle of the element's bounding box.
    Otherwise it is the hit point lifted 0.3 m along the normal.
  - **RMB** removes a source; **E** edits it.
  - **"Same for all of this type"** adds a source to every instance of the family type. Four identical pumps take one click.
- **Source data:**
  - Lw per octave band (dB re 1 pW), or an overall dB(A) with a spectrum shape;
  - directivity (omni by default);
  - on / off, so "duty only" and "duty + standby" are both checkable;
  - name, prefilled from the family type.
- **Autofill:** when the family has Lw parameters (names configurable), they are read from `ParameterTable`.
- **Presets:** pump, AHU, fan, chiller, generator. Presets are marked as typical shapes, not manufacturer data. Any
  estimate from motor power is labelled as such in the results.
- **Large equipment:** a chiller or generator can be split into several point sources over its bounding box. A single
  point under-predicts near a large radiating casing.
- **Markers:** a speaker icon. When selected, a sphere shows the distance where direct sound equals reverberant sound
  (r = ¼√(QR/π)), which is useful on its own.
- **Stored in** `acoustic-sources.json` (sidecar; an optional `.bimgo` entry).

## Calculation and results

- **Domain:** the selected rooms (`RoomInfo` loops, bottom / top), plus a margin of geometry, built on a worker like
  `ExportedSceneInput`. Each triangle carries its α[8] and scattering. Rooms with doors closed are treated as closed
  boxes. Coupled spaces and open doors come later.
- **Receivers:** the study grid at ear height (1.5 m standing, 1.2 m seated; 0.25 / 0.5 / 1 m cells). Optional: wall
  surfaces, for treatment placement.
- **Display:**
  - dB(A) or one band;
  - banded noise-map colours at 5 dB steps (e.g. 40–100 dB) with contour lines;
  - opacity;
  - values near the eye.
- **Crosshair probe:** the total and each source's part (direct vs reflected). For example: "Pump 1: 84, Pump 2: 81,
  reflected: 86 → 89 dB(A)".
- **Targets:** a dB(A) limit or an NR / NC curve. 85 dB(A) is the obvious default for mechanical rooms (hearing
  protection zone). The map then shows pass / fail. Room stats: mean / max Lp, and T per band.
- **Outputs:** CSV of each cell's bands, a screenshot with the legend, and saved studies in `acoustic-studies\<name>.json`.
- **Later:** while walking, play a pump loop through `SoundSystem` at the calculated level at the eye.

## Limits to state in the results (like the daylight study's "early design indicator")

- **Low frequencies:** below the Schroeder frequency f ≈ 2000 √(T/V), room modes dominate (≈ 140 Hz for 320 m³ at
  1.5 s). The 63 and 125 Hz results are indicative only.
- **Diffraction:** pure rays don't bend around equipment, so shadows behind it are too deep at low frequencies.
- **Near field:** within about one source dimension of the equipment, a point source is not valid. These cells are
  masked or flagged.
- **Not covered yet:**
  - structure-borne sound, vibration, duct and pipe breakout;
  - transmission to neighbouring rooms until AC-T;
  - the equipment's real directivity.
- **Absorption data:** the α values are assumptions, and the report lists them, as the lighting input lists its
  reflectances.

## Builds (proposed)

1. **AC-A, Core (no UI):**
   - `Scene/Acoustics.cs`: bands, A-weighting, dB addition, Sabine / Eyring, Hopkins–Stryker, NR / NC curves, air
     absorption (ISO 9613-1 at 20 °C / 50 % RH).
   - `Scene/AcousticMaterials.cs`: the class table, keyword rules, resolve order.
   - `Format/AcousticFiles.cs`: sources, materials and studies documents.
   - Tests:
     - free field: −6 dB per doubling of distance;
     - two equal sources: +3 dB;
     - Sabine for a uniform shoebox;
     - α resolve order;
     - round-trip of the documents.
2. **AC-B, App:**
   - the SOUND gun, markers and the sources panel (Esc → ACOUSTICS → SOURCES);
   - binding to elements, and "all of this type";
   - parameter autofill;
   - saving, and inclusion in the `.bimgo`.
3. **AC-C, first usable map:**
   - a statistical solve, with the direct term checked by line of sight;
   - the receiver grid, overlay, legend, probe, target, CSV.
   - This already answers "two pumps add, and the room absorbs".
4. **AC-T, transmission to neighbouring rooms** (requested; detail in `2_transmission.md`). It comes after AC-C and
   before AC-D:
   - STC / Rw / octave TL per wall, door, window, floor / slab type, and CAC for ceilings;
   - composite partitions;
   - room adjacency from extraction;
   - the neighbours' levels, with the dominant path.
5. **AC-D, ray-traced reflected field (hybrid):**
   - a worker job with progress and cancel;
   - quality presets (rays, maximum order);
   - T per band from the traced decay, compared with Sabine / Eyring;
   - the MATERIALS tab.
6. **AC-E, optional:**
   - Maekawa barriers;
   - listening through `SoundSystem`;
   - an HTML report.

**Validation corpus:**
- a shoebox room against Sabine / Eyring;
- a free-field half-space (a source on the floor) against Lw − 20 log r − 8;
- one real plant room against a hand calculation, and against a commercial tool (Odeon / CATT) where available.

## Decisions needed

1. **Where the solver lives:** in BimGo only (`BimGo.Core` + a small `BimGo.Acoustics` project, MIT-0), or shared with WW
   Tools from the start.
2. **Whether v1 stops at the statistical map (AC-C)**, or must show geometry-specific reflection (AC-D) before it is
   useful. The statistical map is quick and matches the hand calculations engineers already trust. The ray-traced map
   is what makes it a "heat map".
3. **Where absorption data comes from in their models:** an existing shared parameter, material naming standards, or
   the in-app table only.
4. **Where Lw comes from:** whether manufacturer data is already in equipment family parameters (and their names).
