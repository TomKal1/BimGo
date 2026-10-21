# Y-C1 build notes: spot / hemispherical profiles, IES tilt, lights ranked by contribution

Not compiled here (no .NET SDK). Brackets, strings and char literals script-checked in every changed `.cs` file. The
generated profiles' numbers (the values the new tests expect) checked with a Python port of the table, the bilinear
lookup and the integral. No shader changes: the generated profiles use X-D1's profile block and code as they are.

## What's in
- **Revit spot and hemispherical lights → generated profiles.**
  - Core `LightProfiles`: `GENERATED_PREFIX` `gen:`; `SpotKey(beam, field)` → `gen:spot:<beam>:<field>` (whole degrees,
    full angles; the beam 1°…the field, the field at most 179°), `HEMISPHERICAL` = `gen:hemi`; `IsGeneratedKey`;
    `FromGeneratedKey`: **spot** = full intensity inside half the beam angle, a smoothstep fall to nothing at half the
    field angle (at least a 2° fall), nothing beyond; **hemispherical** = cos γ out of the fixture, nothing behind
    (Revit's "uniform diffuse"). Same table, scale (energy kept) and code as the built-ins. `LightProfile.IsGenerated`.
  - Core `LightSource.Distribution` (and `LightDto.Distribution`, `distribution` in `lighting.json`; written only when
    set; read only when this version can make the key, else the lobe). A distribution keeps its aim / across in the file.
  - Revit `ReadTypeIes` now also reads `SpotLightDistribution` (`SpotBeamAngle`, `SpotFieldAngle`, `TiltAngle`: radians;
    the tilt turns the aim exactly as a web's, 90° = down) and `HemisphericalLightDistribution`, per type and for nested
    light sources. Each spot / hemispherical type is logged ("is a spot light: beam 30°, field 45°, tilt 90° (90° =
    down) → gen:spot:30:45"); the summary line counts "N spot / hemispherical". Spherical lights stay on the lobe (many
    families leave the default spherical light on a downlight).
  - Session: "Revit" for a type = its file, else its distribution (`LightTypeRow.RevitShape`, `HasRevit`, `RevitKey`);
    such a type offers "Revit" first in ‹ ›, and the row reads e.g. "Revit spot (30° beam, 45° field)".
  - Fixed on the way: family library lights (the library pass's drop to the floor) lost their profile, aim and across;
    they keep them now.
- **IES tilt data.** `IesData` keeps TILT=INCLUDE (`TiltGeometry`, `TiltAngles`, `TiltFactors`; angles out of order →
  no tilt; TILT=<file name> → no tilt, `TiltFileIgnored`). `LightProfile.TiltAngles / TiltFactors / TiltFactor(deg)`:
  linear between angles, held at the ends, 1 without data. LM-63 applies the factor to every candela value, so it
  scales a fixture's **output** (not the shape): `ApplyLightProfiles` multiplies the light's lumens by the factor at its
  aim's angle from straight down. A file with tilt data is logged once ("TILT=INCLUDE, 3 angles (0–90°): output × 1
  straight down") and its LIGHTS row says "· tilt data".
- **Live lights ranked by contribution** (`GameSession.Lights.cs`). Each light in view scores output (lm, after the
  profile's own output / tilt) ÷ distance² (distance at least 1 m); the 32 best get shadow maps. Lights used last
  frame score 1.5 × (≈ 22 % closer), so the set doesn't swap back and forth as you walk. When more are in view, the
  weakest picked ones fade (score from the weakest to 2 × it: the same span as the old 70–100 % of the distance). All
  fixtures (photos, videos, *All fixtures live*): the unshadowed rest are taken strongest first too.
- **Tests** (`LightProfileTests`): spot keys (rounding, clamping, refusals), the spot's shape (full beam, nothing past
  the field, round, energy kept, narrower = more concentrated, names), the hemisphere (cos, nothing behind, scale 4),
  `distribution` round trip (unknown key dropped), tilt kept / factor / separate tilt file / damaged / unordered.
- **README:** §5 lights and light profiles; §8 open build, a light profiles limitation line; Round Y list; §9 row.

## What isn't
- Spot tilt: read like a web's (90° = down). If a spot type's log says another tilt for a light that points down, tell
  me the value and I'll flip the convention for spots.
- The tilt factor uses the aim's angle from straight down for every lamp geometry (types 2 / 3 horizontal lamps
  strictly depend on the turn about the lamp's axis too).
- Ranking ignores what's on screen (a bright light behind a wall still ranks high: its shadow map keeps it off you).
- Y-C2 next: per-instance profiles, polar plot previews.

## How to check, step by step
1. **Build:** Core tests pass (`LightProfileTests` +5 tests; one updated).
2. **Spot / hemispherical:** in Revit, a lighting fixture type whose light source is a **spot** (Type Properties →
   Light Source Definition: spot; set beam e.g. 30°, field 60°), another **hemispherical**. Go (or F5). Log: "is a spot
   light: beam 30°, field 60°, tilt 90°…" and "N spot / hemispherical" in the Lighting line.
3. Night, Lights = Glow + light: the spot throws a crisp round pool (full inside ~30°, gone by ~60°); the hemispherical
   light a soft even pool, nothing on the ceiling. Pause → LIGHTS: those rows read "Revit spot (30° beam, 60° field)"
   / "Revit hemispherical"; ‹ › steps to the built-ins and back to them; RESET returns to them.
4. **Tilt data:** IMPORT .IES… a file with `TILT=INCLUDE` (most don't have one; any with a tilt table will do): the
   log line "TILT=INCLUDE, N angles…", the row "· tilt data". On a downlight the brightness is the file's factor at 0°
   (usually 1: no change). A wall-mounted fixture aimed sideways with that file: output × its factor at 90°.
5. **Ranking:** a room with a few bright downlights and many dim fixtures (or with *All fixtures live* off and more
   than 32 fixtures in view): the bright ones near you keep their shadows; walking slowly, shadows don't flicker on and
   off between two lights. Photos: unchanged look (they light every fixture).
6. **Family library:** place a lighting fixture from the library that has a web / spot: its beam is shaped and aimed
   (it was the lobe before).
7. A .bimgo exported before Y-C1 opens as before (no `distribution`: lobe for spot / hemispherical types until F5 /
   Export from Revit again).

## Revision 1b (2026-10-21): test fix
`Lighting_DistributionRoundTrips` put a light on element 2; the test scene has two elements, so the reader dropped it
and the test ran off the end of the list. All four test lights now sit on elements 0 and 1 (test only).

## Revision 2 (2026-10-21): longer light reach
Gavin: "not bad"; lights switch on not far from the player, which looks odd. Each light's reach (where its light fades
to nothing, and the sphere that must touch the view for it to count) was 0.16 × √lumens, 2.5–9 m; now
`GameSession.LightReach`: **0.22 × √lumens, 3–12 m** (1000 lm ≈ 7 m instead of 5 m; 3000 lm 12 m), one helper for
both places that set it (`InitialiseLights`, `ApplyLightProfiles`). The shadow maps' far plane follows the radius, so
maps re-render once. Costs a little more shading per pixel (each light covers more of the screen).

How to check: walk towards a lit room or along a corridor of fixtures at night: pools appear further out and fade in
more gently; if it's still too close, the factor / cap is one line (`LightReach`). With more than 32 fixtures in view
the weakest still fade (the 32-shadow-map cap: *All fixtures live* lights the rest unshadowed).
