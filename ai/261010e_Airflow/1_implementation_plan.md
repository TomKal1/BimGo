# BimGo Airflow: implementation plan (3-cone square supply diffusers)

**Scope decided 2026-10-11:** build the narrow version first: **3-cone square supply diffusers only**. The design
reasoning is in `0_plan.md`; this file is the build order, with files, types, algorithms, tests and the
"done when" for each step. Where the two disagree, this file wins for the narrow build.

**Before writing code** (README *For AI assistants*):
1. Ask Gavin for a fresh zip of his working copy; it is the source of truth.
2. Claude can't compile here (no .NET SDK, no Revit API). Every step lists the APIs to check in its build notes,
   and the tests are written to run in `tests/BimGo.Core.Tests` (MSTest 4) on Gavin's machine.
3. New folders need Gavin's yes: only one is proposed, `src/BimGo.Revit/Resources/Families/` (step 8).
4. No new packages. Core stays free of Revit, GL and UI code; App never references the Revit API.

---

## 0. Scope

### In

| Area | What |
|---|---|
| Diffusers | Ceiling **3-cone square supply diffusers**, 4-way pattern, optional blanked sides; ceiling-mounted (attached jet) or exposed (free jet) |
| Catalog | A built-in **generic** catalog generated from a formula (no copied manufacturer data), IP and SI size ranges; user catalogs imported as JSON / CSV |
| Families | Recognition of BimGo-spec families, **profiles** for known families (e.g. Autodesk defaults, once checked) and a keyword fallback; a family checker badge |
| Units | IP and SI, read from the Revit project, overridable per model |
| Display | Throw envelopes (150 / 100 / 50 fpm or 0.75 / 0.5 / 0.25 m/s) on the ceiling, jet paths, event markers, Scan gun readout + velocity graph |
| Trace | Walls, obstructions under the ceiling (lights, beams, ducts…), collisions between diffusers, falling jets, free jets |
| Comfort | EDT draft check in the **ASHRAE 62.1 breathing zone** (or Custom), **Cooling / Heating** toggle (heating = 50 % of cooling flow), advanced temperatures |
| Findings | Findings panel → Go to / Make comment (BCF-ready) |
| Edits | `SetFlow` (live, journal, push); placement from the Place gun in three modes (**unhosted at ceiling** default, hosted to ceiling face, free at elevation); ceiling X-ray; plenum check; grid snap; Re-seat to ceiling |
| Loading | Diffusers tab in the library; `family.load` live message; built-in templates + load-on-push for files; ribbon **Load diffusers…** |

### Out (later, if asked)

Slot / louvered / sidewall diffusers, return and exhaust modelling (returns are just geometry), `SetType`
(changing neck / face from BimGo), ASHRAE 55 preset and exterior-wall detection, simple velocity mode, smoke
particles, occupied-zone heatmap, cold-jet separation, CFD / OpenFOAM, ventilation calculations, a BimGo-side
duct length check (Revit errors already roll a move back).

### Assumed defaults (confirm when staging each step; recommendation first)

| Question | Default used in this plan |
|---|---|
| When to build | After the main plan's Rounds A and B (the airflow steps reuse Round B's unit setting if it exists; step 1 creates the shared `Units` helper otherwise) |
| Ceiling X-ray | See-through (~20 %), on automatically while the Place gun holds an air terminal |
| Free-jet throw factor | 0.7 for every family (setting) |
| Autodesk families | Profiles only until someone checks Autodesk's content terms; nothing from Autodesk ships |
| BimGo family | Authored by the user or Gavin in Revit 2025 to the spec in step 8 |
| Airflow key | **Y** opens the airflow panel (plain Y is free; Ctrl+Y stays redo; revisit in the hotkey review) |

---

## 1. Step 1: Core foundations (units, catalog, data model, file entries)

No Revit, no GL. Everything here is unit-tested.

### 1.1 `Scene/Units.cs`

```csharp
public enum UnitSystem { SI, IP }

public static class Units
{
    // Factors to SI
    public const float CFM_TO_LPS = 0.471947f;
    public const float FPM_TO_MPS = 0.00508f;
    public const float INWG_TO_PA = 249.089f;
    public const float FT_TO_M = 0.3048f;
    public const float IN_TO_M = 0.0254f;

    public static float FlowToSi(float value, UnitSystem from);   // CFM or L/s → L/s
    public static float FlowFromSi(float lps, UnitSystem to);
    public static float SpeedToSi / SpeedFromSi;                  // fpm ↔ m/s
    public static float PressureToSi / PressureFromSi;            // in wg ↔ Pa
    public static float LengthToSi / LengthFromSi;                // ft ↔ m (throws)
    public static float SizeToSi / SizeFromSi;                    // in ↔ m (necks, faces)
    public static float TempDeltaToSi / TempToSi …;               // °F ↔ °C / K

    /// The envelope velocities for a system (m/s): IP 150 / 100 / 50 fpm, SI 0.75 / 0.5 / 0.25 m/s.
    public static float[] EnvelopeSpeeds(UnitSystem system);

    // Formatting (invariant culture, no allocations in per-frame callers: write into a caller's char buffer)
    public static string FormatFlow(float lps, UnitSystem s);     // "250 CFM" / "118 L/s"
    public static string FormatSpeed(float mps, UnitSystem s);    // "48 fpm" / "0.24 m/s"
    public static string FormatLength(float m, UnitSystem s);     // "12.5 ft" / "3.8 m"
    public static string FormatElevation(float m, UnitSystem s);  // "9' 0\"" / "2,700 mm"
    public static string FormatSize(float m, UnitSystem s);       // "8 in" / "200 mm"
    public static string FormatPressure(float pa, UnitSystem s);  // "0.06 in wg" / "15 Pa"
}
```

The per-frame readout uses the existing `TextBuffer` / char-buffer pattern; the `string` overloads are only for
panels built on change and for CSV export.

### 1.2 `Scene/DiffuserCatalog.cs`

```csharp
public sealed class CatalogRow                  // one table row, SI
{
    public float NeckDiameter;                  // m (round necks only in this scope)
    public float FaceSize;                      // m (square: width = length)
    public float Flow;                          // L/s, whole diffuser
    public float TotalPressure;                 // Pa (0 = unknown)
    public float Nc;                            // 0 = unknown
    public float[] ThrowSpeeds;                 // m/s, e.g. 0.762 / 0.508 / 0.254
    public float[] Throws;                      // m, same order (per side, 4-way)
}

public sealed class CatalogFamily
{
    public string Key, Label;
    public bool Illustrative;                   // built-in generic data
    public float FreeJetFactor = 0.7f;          // exposed mounting
    public List<CatalogRow> Rows;
    public string[] Match;                      // name keywords for the fallback matcher
}

public readonly record struct DecayCurve(float A, float N)   // V(x) = A · x^-N, SI
{
    public float SpeedAt(float x);              // capped at the neck velocity for x < 3 neck diameters
    public float DistanceAt(float speed);       // inverse
}

public sealed class DiffuserCatalog
{
    public IReadOnlyList<CatalogFamily> Families { get; }
    public static DiffuserCatalog BuiltIn();                   // generic 3-cone square, IP + SI ranges
    public static DiffuserCatalog Load(string folder, out List<string> problems);  // built-in + user files
    public CatalogFamily Find(string key);

    /// Curve, pressure and NC for a diffuser of this neck / face at this flow (interpolated or extrapolated).
    public DiffuserPerformance Evaluate(CatalogFamily family, float neck, float face, float flowLps);
}

public readonly record struct DiffuserPerformance(
    DecayCurve Curve, float NeckVelocity, float TotalPressure, float Nc,
    bool Extrapolated, bool SizeMatched);
```

**Fit** (`CatalogFit`, internal static):
- Per row, least squares on `ln V = ln A − N · ln x` over the row's (speed, throw) pairs. With one pair:
  `N = 1`. Clamp `N` to 0.8–1.3; log a line when clamped.
- Per (family, neck, face) size: rows sorted by flow; the per-row `A / Q` and `N` are interpolated linearly in
  `Q`. Outside the table up to ±25 % is extrapolated with the end values (`Extrapolated = true`); beyond that it is
  clamped and flagged.
- Size match: nearest neck within 5 % (else the nearest neck by area, `SizeMatched = false`); face likewise.
  Nominal sizes are never converted (8 in ≠ 200 mm).
- Pressure: `Pt = k · Q²` with `k` from the nearest row(s). NC: `NC_ref + 10 · log10(Q / Q_ref)`.

**Built-in generic family** (`generic-3cone-square`), **generated**, not typed in, so nothing is copied:
- Throw model: `T_v = (C · Q / (√A_neck · v))^(1/N)` with `N = 1.15`, `C = 2.26` (IP units: CFM, ft², fpm, ft),
  chosen so an 8 in neck at 250 CFM gives ≈ 5 / 7 / 13 ft at 150 / 100 / 50 fpm, typical of published 3-cone
  diffusers. SI rows use the same model converted to SI.
- Pressure: `Pt = 1.8 · (V_neck / 4005)²` in wg. NC: `20 + 50 · log10(V_neck / 700) + 10 · log10(A_neck / 0.349)`,
  clamped to ≥ 10.
- IP sizes: necks 6, 8, 10, 12, 14, 16 in; faces 12 in (6, 8 in necks) and 24 in (all). Flows at neck velocities
  400, 500, 600, 700, 800 fpm, rounded to 5 CFM.
- SI sizes: necks 150, 200, 250, 300, 350, 400 mm; faces 300 mm (150, 200) and 600 mm (all); flows at 2.0 – 4.0 m/s
  neck velocity in 0.5 steps, rounded to 5 L/s.
- `Illustrative = true`; every readout of it says *generic catalog data*.

**User catalogs:** `%AppData%\BimGo\Catalogs\*.json` (same shape as the DTO in 1.4) and `*.csv`:

```
family_key,label,units,neck,face,flow,pt,nc,v1,t1,v2,t2,v3,t3
acme-scd,ACME SCD,IP,8,24,250,0.06,20,150,5,100,8,50,13
```

Bad rows are skipped with a log line and listed in the airflow panel's catalog section (never an exception).

### 1.3 `Scene/AirTerminals.cs`

```csharp
public enum AirTerminalMount { Ceiling, Exposed }          // decided per trace (§4), stored for display
public enum AirTerminalHosting { Unhosted, FaceHosted, CeilingHosted }

public sealed class AirTerminal
{
    public int Element;                 // index into SceneData.Elements
    public Vector3 Position;            // centre of the face, scene-local (m)
    public Vector3 Normal;              // face direction (−Z for ceiling diffusers)
    public Vector3 Forward;             // family X axis in plan (pattern orientation)
    public float FaceSize, NeckDiameter;    // m
    public float FlowLps;               // Revit "Flow" (cooling design flow)
    public bool FlowEditable;
    public string FlowParameter;        // which parameter holds it (for SetFlow), e.g. "RBS_DUCT_FLOW_PARAM" / "Flow"
    public byte BlankedSides;           // bit 0 +X, 1 +Y, 2 −X, 3 −Y (family axes)
    public AirTerminalHosting Hosting;
    public bool Connected;              // any connector connected
    public string CatalogKey;           // matched family, or the generic key
    public FamilyRating Rating;         // Ready / Guessed / NotModelled (see 2.3)
    public string RatingReason;
    public float FaceOffset;            // origin → face distance along Normal (m), 0 for spec families
}

public sealed class AirData
{
    public static readonly AirData Empty = new();
    public UnitSystem ProjectUnits;     // from Revit
    public AirTerminal[] Terminals = Array.Empty<AirTerminal>();
    public CatalogFamily[] Catalog = Array.Empty<CatalogFamily>();  // families used, so a file is self-contained
}
```

`SceneData` gains `public AirData Air { get; init; } = AirData.Empty;` (like `Lighting`).

### 1.4 Files: `air.json` and `airflow.json`

- `BimGoFormat`: `ENTRY_AIR = "air.json"` (extraction output, rewritten on refresh) and
  `ENTRY_AIRFLOW = "airflow.json"` (the user's airflow state). Both optional; documented in the header comment;
  `FORMAT_VERSION` stays 1.
- `FileModels.cs`: `AirDto` (units, terminals by element index, catalog families) and `AirflowStateDto`.
- `BimGoWriter`: writes `air.json` when `scene.Air.Terminals.Length > 0`; `airflow.json` when the state differs from
  defaults. `BimGoReader`: validates like `BuildLighting` (element indices in range, finite numbers, flows ≥ 0,
  sizes 25 mm – 1.5 m); anything invalid is dropped with a log line, never fails the load.
- **Live sessions:** `air.json` rides in each snapshot (the snapshot is a `.bimgo`). `airflow.json` lives in the
  model folder (`ModelFolders`) next to `sun.json`, with the same mirror-beside-the-model option.

`AirflowState` (Core, `Scene/AirflowState.cs`): `On`, `Mode` (Cooling / Heating), `UnitsOverride` (null = project),
`HeatingShare` (0.5), `CoolingSupplyC` (12.8), `CoolingRoomC` (23.9), `HeatingSupplyC` (32.2), `HeatingRoomC`
(21.1), `ZonePreset` (`Ashrae621` / `Custom`), custom zone values, `PlacementMode`, `XRay`, `TargetElevation`,
`GridSnap`. `Clean()` clamps everything (like `SunHoursSettings.Clean`).

`LaunchSettings` (global, Revit + App): `AirflowFreeJetFactor` (0.7), `AirflowBandFactor` (0.1),
`AirflowPlenumAllowance` (0.15 m), `AirflowDefaultMount` (2.7 m), `AirflowOffCeilingTolerance` (0.025 m), with
`Sanitise` clamps.

### 1.5 Tests (`tests/BimGo.Core.Tests/AirflowCatalogTests.cs`, `AirflowFileTests.cs`)

- Every `Units` conversion both ways; `EnvelopeSpeeds` per system; formatting samples.
- Fit: a row with exact power-law data returns its `A`, `N`; clamping; one-pair rows.
- Built-in: 8 in / 24 in / 250 CFM → T150 ≈ 5 ft, T100 ≈ 7 ft, T50 ≈ 13 ft (±10 %); the SI 200 mm row agrees
  with the IP 8 in row scaled by area (±10 %).
- Interpolation between rows; extrapolation flag at +20 % and clamp at +40 %; nearest-size matching, 5 % rule.
- CSV import: good file, bad rows skipped and reported, IP and SI files of the same product give the same curve.
- `air.json` / `airflow.json` round trip; reader drops invalid terminals; an old file without them loads.

**Done when:** tests green on Gavin's machine; no App or Revit change yet.

---

## 2. Step 2: Revit extraction (`SceneExtractor.Air.cs`)

### 2.1 Hook

Same pattern as lighting: in the per-element loop of `SceneExtractor.cs`, after the element is added, call
`AddAirTerminal(element, _elements.Count - 1, bounds)` when its category is `OST_DuctTerminal`. Build
`AirData` next to `BuildLighting()`. Best effort: any exception logs and skips that terminal.

### 2.2 What it reads

| Field | Source (in order) |
|---|---|
| Supply? | Connector `DuctSystemType` / system classification is SupplyAir; else the BimGo / profile parameter; else not modelled |
| Position, normal, forward | `FamilyInstance.GetTransform()`: origin + `FaceOffset` along the face normal; normal = −`FacingOrientation` for ceiling families (checked against the bounds: the face is the lowest face for ceiling mounting); forward = `HandOrientation` |
| Face size | Type parameters `Face Width` / `Face Length` (spec), profile names, else the instance bounds' plan size |
| Neck | The round connector's `Radius × 2`; else `Neck Diameter` (spec / profile) |
| Flow | `RBS_DUCT_FLOW_PARAM` on the instance; else the family parameter the connector's flow is associated with (`FamilyManager` isn't available in the project: read via `Connector.Flow` and the instance parameter of the same name from the profile); `FlowEditable` = parameter exists, not read-only, not formula-driven |
| Blanked sides | `Blanked Sides` instance parameter (spec), else none |
| Hosting | `FamilyPlacementType` (`OneLevelBased` → Unhosted, `WorkPlaneBased` with a face host → FaceHosted, `OneLevelBasedHosted` → CeilingHosted) |
| Connected | any connector `IsConnected` |
| Units | `doc.GetUnits()`: `SpecTypeId.AirFlow` format unit (CFM → IP, L/s / m³/h → SI); length format as the fallback |

### 2.3 Which family is it? (`Extraction/FamilyCheck.cs` + Core `Scene/DiffuserProfiles.cs`)

1. **Spec family:** has a `BimGo Catalog Key` type parameter → that key; rating **Ready** when the other spec
   parameters are present, else **Guessed** with the missing names.
2. **Profile:** `DiffuserProfiles.json` (built-in in Core, plus `%AppData%\BimGo\Catalogs\profiles\*.json`) maps
   a family name (exact, case-insensitive) to: parameter names for face, neck, flow, blanked sides; `FaceOffset`;
   catalog key. Match → **Ready**.
3. **Keyword fallback:** family or type name contains one of the generic family's `Match` keywords (`cone`,
   `3-cone`, `square cone`, `scd`, `tms`…) **and** the face is square (±5 %) → generic key, **Guessed**.
4. Anything else (slots, louvers, round, returns…) → **NotModelled** with "not a 3-cone square supply
   diffuser". Kept in `AirData` so the Scan readout can say so; ignored by the trace.

Per-type overrides (catalog key, rating forced to modelled) set from the Scan readout are saved in the model
folder / `.bimgo` (`airflow.json`, keyed by type UniqueId), like texture overrides.

### 2.4 To verify (build notes)

`Connector.Flow`, `Connector.Radius`, `DuctSystemType` on an unconnected terminal; `RBS_DUCT_FLOW_PARAM`
read-only state with Preset / Calculated flow configuration; `FacingOrientation` / `HandOrientation` for
face-based vs level-based diffusers; `doc.GetUnits().GetFormatOptions(SpecTypeId.AirFlow).GetUnitTypeId()` on
2025–2027; the Autodesk default diffusers' family and parameter names (for the first profiles).

**Done when:** a test model's 3-cone diffusers export into `air.json` with correct flow, sizes, ratings and
units; other air terminals are listed as NotModelled; extraction time grows by < 2 %.

---

## 3. Step 3: First visible result (panel, envelopes, Scan readout)

### 3.1 `GameSession.Airflow.cs` + `Game/AirflowStudy.cs`

- **Y** toggles airflow (pause menu → Studies gets an AIRFLOW button too). Off by default; state in
  `AirflowState.On`.
- **Airflow panel** (free cursor like the sun panel, Esc closes): COOLING | HEATING; units (Project / IP / SI);
  zone preset; *Advanced* (collapsed): four temperatures, heating share; catalog section (families loaded,
  problems); counts (modelled / guessed / not modelled).
- `AirflowStudy` holds per-terminal results in preallocated arrays (rays × samples), rebuilt only when the trace
  reruns; drawing reads them with no allocations.

### 3.2 Envelopes (no events yet in this step)

- Per modelled terminal: 36 rays in plan (10° apart) from the face centre, along the ceiling plane.
- Per ray, the side flow: `Q_side = Q · 4 / openSides`; a **pattern factor** `f(θ)` scales distance:
  1.0 at a side normal, 0.8 at a corner (cosine blend), 0 for a blanked side's ±45°.
- Distance to each envelope speed `v`: `f(θ) · Curve(Q_side).DistanceAt(v)`.
- Draw: three filled translucent loops per terminal with `Overlay3D.Triangle` fans, 5 mm below the face plane,
  colours from `UiTheme` (150 = warm, 100 = mid, 50 = cool), plus outline rings. Depth-tested so walls hide them.

### 3.3 Scan gun readout + graph (`Guns/ScanGun.cs`)

When the scanned element is an air terminal, the panel adds:
- Family / type, rating badge (Ready / Guessed + reason / Not modelled), catalog (*generic* flagged).
- Flow (mode-adjusted: heating shows the share), neck, face, neck velocity, Pt, NC, T150 / T100 / T50 (or SI
  set), mount (ceiling / exposed), connected / unconnected, hosting.
- **Graph** (`UiBatch.Line`): log–log, x = 0.3–15 m (1–50 ft), y = 0.1–5 m/s (20–1000 fpm); one curve per open
  side; dashed horizontal lines at the envelope speeds; in step 4 vertical markers where events happen.

**Done when:** opening a real model shows envelopes under every 3-cone diffuser; the readout matches the
catalog; switching IP / SI changes every number and the envelope set; no per-frame allocations (check with
the existing allocation counter in the Debug tab).

---

## 4. Step 4: The trace (Core `Scene/AirflowTrace.cs`, App adapter)

### 4.1 Geometry interface (keeps Core testable)

```csharp
public interface IAirflowGeometry
{
    /// First hit along a ray, ignoring the given element; false if none within maxDistance.
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, int ignoreElement, out AirHit hit);

    /// Any element (other than ignoreElement and ceilings) intersecting the box; returns the lowest-bottom one.
    bool BoxBlocked(in Aabb box, int ignoreElement, out AirHit hit);

    /// Underside of the ceiling (or other surface) directly above a point, within maxUp; NaN if none.
    float SurfaceAbove(Vector3 point, float maxUp, out int element);

    float FloorBelow(Vector3 point);                 // walkable surface Z (FloorAt exists in the App)
}
public readonly record struct AirHit(float Distance, Vector3 Point, Vector3 Normal, int Element, bool IsCeiling);
```

App: `BvhAirflowGeometry` wraps `Bvh.Raycast` / `Bvh.Query` with the visible-elements mask, the transparent flag
ignored (glass blocks air), ceilings identified by category index.

### 4.2 Algorithm (per modelled terminal, per ray; all SI)

Inputs: terminal, `DiffuserPerformance` (mode-adjusted flow), neighbours (terminals within
`T50_i + T50_j + 1 m`), settings, temperatures.

1. **Mount.** `SurfaceAbove(face, 0.05 m)` hits → **Ceiling** (attached). Otherwise **Exposed**: the curve is
   scaled by `FreeJetFactor` (distances × 0.7).
2. **Ceiling run** (attached only). March `s` from 0 in 0.1 m steps to `DistanceAt(terminal speed 0.125 m/s)`:
   - Band: from the ceiling plane down `δ(s) = BandFactor · s` (min 0.05 m).
   - `BoxBlocked(step box)` → **Obstruction** at `s`: element, depth into the band. Objects entirely below the
     band don't count (a pendant on a long rod is below a thin band near the diffuser).
   - `Raycast` ahead at band centre: a hit with |normal.Z| < 0.3 within the step → **Wall** at `s`.
   - **Collision:** at the sample point `p`, for each neighbour `j` with `p` inside `j`'s reach in its own ray
     direction `d_j = normalize(p − c_j)` (and `j`'s ray toward `p` not blocked before `|p − c_j|`): if
     `dot(dir_i, d_j) < −0.3` and `V_j(|p − c_j|) ≥ V_i(s)` → **Collision** at `s`, with `V_meet = V_i(s)`.
   - First event ends the ceiling run. No event → **Terminal** (jet dies on the ceiling).
3. **Exposed run:** horizontal from the face at the face height; same obstruction / wall / collision tests with a
   band centred on the jet (±δ/2); in **cooling** the jet turns down once `V < 0.5 m/s` (setting) → falling jet.
4. **Falling jet** (after Wall, Obstruction, Collision, or exposed turn-down):
   - Wall: runs down the wall face (offset 0.05 m). Velocity `V_i(s + z) · 0.9` (turn loss, setting). At the
     floor it turns out along the floor, away from the wall.
   - Obstruction / collision / exposed: straight down from the event point, `V(s + z)` (collision: from `V_meet`
     onward using the stronger jet's curve).
   - Stops at `FloorBelow` or at the first surface below (`Raycast` down: desks, casework).
5. **Comfort check** along the falling part (and the floor run after a wall), at 0.1 m steps:
   - In the zone? **ASHRAE 62.1 breathing zone:** 0.075 – 1.8 m above `FloorBelow`, and ≥ 0.6 m from the nearest
     wall (a horizontal `Raycast` fan of 8 directions, cached per 0.25 m cell). Custom: user heights / setback.
   - `t_x − t_room = 0.8 · (t_supply − t_room) · V / V_neck`.
   - `EDT = (t_x − t_room) − 8 · (V − 0.15)` (K, m/s). **Draft** if `EDT < −1.7` (cold) or `EDT > 1.1` or
     `V > 0.35` (warm / fast).
   - The first failing point → a **finding**: kind (cold draft / warm / fast), position, V, EDT, cause (wall
     drop, obstruction + element, collision + other terminal, exposed fall), terminal, ray.
6. **Room figure:** `T50 / L` per side, `L` = wall distance or half the distance to the colliding neighbour along
   the side normal. Shown as information ("about 0.8 is best"), no pass / fail.

**Output** per terminal: per ray the samples `(point, V)` (ceiling + falling parts, ≤ 128 each), the event, and
the findings. Rays that end in the same finding within 0.5 m are merged into one finding.

**Constants to confirm** against ASHRAE Fundamentals before release (build notes): EDT form and band,
0.35 m/s cap, 0.8 temperature ratio, the 62.1 breathing-zone wording.

### 4.3 Running it (App)

- Time-sliced on the game thread, 8 ms per frame (like the sun hours study), with the existing progress bar and
  CANCEL; reruns when the toggle turns on, the mode changes, a terminal changes (only that terminal and its
  neighbours), or after F5.
- Draw: ray paths as thin `Overlay3D.Line`s coloured by speed; markers: red disc = draft finding, amber =
  event above the zone (wall drop, obstruction, collision), grey = jet ended on the ceiling; envelopes now
  clipped by events.
- **Findings panel** (in the airflow panel): sorted by severity, *Go to* (teleport to 1.5 m from the point,
  looking at it) and *Make comment* (a normal comment with the snapshot and text such as "Cold draft 0.32 m/s
  (63 fpm) at 1.1 m: wall drop from SD-12"; exports to BCF like any comment). EXPORT CSV.

### 4.4 Tests (`AirflowTraceTests.cs`, with a box-world `IAirflowGeometry` fake)

- Empty 20 × 20 m room, one diffuser: no events, envelopes = catalog throws.
- Wall 2 m away: Wall event at 2 m; finding when the floor run is fast enough; none in heating at 50 %.
- Pendant box in the band at 1.5 m: Obstruction; same box 0.6 m lower: ignored.
- Two diffusers 4 m apart, equal flow: collision at 2 m; unequal flow: collision nearer the weaker one (checked
  against the closed-form `V_i(s) = V_j(D − s)`).
- Exposed diffuser: throws × 0.7; cooling turn-down at 0.5 m/s.
- EDT: a point at 0.3 m/s fails in cooling and passes in heating at the same speed.

**Done when:** in a test room the findings match a hand calculation from the catalog table (written up in the
build notes), and a 300-diffuser model traces in < 10 s.

---

## 5. Step 5: `SetFlow`

- `EditOp.SetFlow` (append to the enum: values are serialised by name; check) + `EditRequest.Value` (float,
  L/s). Doc comment: additive to protocol 1; an older add-in times out like `Place`.
- **Revit** (`RevitEditor.SetFlow`): find the parameter named in `AirTerminal.FlowParameter` (instance only),
  refuse read-only / formula / type parameters with a clear reason; convert with
  `UnitUtils.ConvertToInternalUnits(lps, UnitTypeId.LitersPerSecond)`; `RunTransaction` ("BimGo: Set flow").
  Result carries the stored value read back.
- **Journal:** `JournalOps.SET_FLOW = "setflow"`; `JournalEntry` gains `Value` and `OldValue` (optional, additive).
  Undo / redo restore `FlowLps`. **Push:** applies it; if Revit's current value differs from `OldValue` by > 0.5 %
  → a conflict (existing conflict UI); the push preview lists it.
- **App:** Scan a terminal → **+ / −** (10 CFM or 5 L/s; Shift × 5) or **Enter** to type a value. The envelopes,
  graph and that terminal's trace update live as a preview; **Enter** commits, **Esc** cancels. Heating mode
  edits the cooling flow (the readout says so).

**Tests:** journal round trip, undo / redo, push conflict detection (Core side of `RevitEditor.Push` logic that
lives in Core).

**Done when:** a flow changed in BimGo shows in Revit's air terminal schedule, one Ctrl+Z undoes it, and a file
edit pushes with a conflict when Revit changed meanwhile.

---

## 6. Step 6: Placement (Place gun)

### 6.1 Library

- Air Terminals added to the family library categories (`SceneExtractor.Library.cs`), so the existing template
  pass makes previews. A **Diffusers** tab in the library panel (`GameSession.Library.cs`) lists 3-cone square
  supply types only (rating ≠ NotModelled), grouped family → face → neck, with the rating badge and catalog
  numbers (flow range, T50 at mid flow, NC). Picking from the tab turns the Air Terminals category on for the
  session even if the family library option is off.

### 6.2 Three modes

**C** cycles the mode while the Place gun holds an air terminal (C is free today; revisit in the hotkey review).
The readout shows the current mode.

1. **Unhosted at ceiling (default).**
   - Aim hits a Ceilings element → `SurfaceAbove(hit − 0.01 m·normal…)`: an upward ray against that element from
     just below the hit gives the **bottom** even when aiming at its top (X-ray, section).
   - Not a ceiling → the underside of the aimed surface if it faces down; else `LaunchSettings.AirflowDefaultMount`
     above the level.
   - Preview flush: template position = bottom + `FaceOffset` (from the template's bounds when not in the
     profile); readout "Ceiling bottom 2,700 mm (Level 1 + 2,700)". **F** re-seats after a manual vertical move.
   - Sloped ceiling (> 2°): stays level; note "hosted mode follows slopes".
2. **Hosted to ceiling face.** Face-based families only (ceiling-hosted `OneLevelBasedHosted` families: same
   model ceilings only; linked → refused). Non-hostable families fall back to unhosted with a toast.
3. **Free at elevation.** Target elevation typed in the airflow panel (level + offset, kept between placements);
   aim gives the plan position; aiming at a duct snaps under its centreline at its bottom.

`EditRequest` (Place) gains optional fields: `PlacementMode`, `HostElementId`, `HostLinkInstanceId`,
`HostPoint`, `HostNormal`, `LevelOffset`. Revit side (`FamilyPlacer`):
- Unhosted / free: level-based → `NewFamilyInstance(point, symbol, level, NonStructural)` then set the level
  offset parameter; work-plane / face-based → the level's plane (today's path) with the offset.
- Hosted: find the host element's face whose normal matches `HostNormal` and contains `HostPoint`
  (`Face.Project`), its `Reference` (`CreateLinkReference` for a link), `NewFamilyInstance(reference, point,
  refDir, symbol)`.
- Every placement still goes through `RunTransaction`: any Revit error rolls it back.

### 6.3 Ceiling X-ray

- A per-element **ghost** flag in `SceneRenderer` (next to `SetElementHidden`): ghosted elements are skipped in
  the opaque passes and drawn after the transparent pass with blending at ~20 % alpha, no depth write, edges
  kept. Shadows: ghosted elements don't cast while X-ray is on.
- Airflow panel: X-RAY CEILINGS (see-through / hidden / off); on automatically while the Place gun holds an air
  terminal (setting). Picking and the BVH are unchanged, so ceilings can still be aimed at.
- GLSL check per README rule 10 if a shader changes (prefer a uniform alpha on the existing transparent shader).

### 6.4 Plenum check

While holding a terminal: rays up from the aimed ceiling point (ignoring ceilings and the template) repeated
past each hit, up to the slab or 3 m: a list (≤ 5) of what is above with clear heights; needed depth =
template height + `AirflowPlenumAllowance`; marker green / amber (< needed + 50 mm) / red (< needed). Says which
MEP / structure categories aren't loaded.

### 6.5 Grid snap

Optional 600 mm / 24 in grid (origin from the aimed ceiling's bounding-box corner, or the project base point),
toggled with the gizmo's existing snap key (**G**) while holding a terminal.

**Tests:** Core parts only (grid snap maths, ceiling-bottom selection with fake geometry, placement request
round trip in the journal).

**Done when:** a diffuser placed in each mode lands where previewed in Revit (unhosted at the ceiling bottom,
level + offset correct), shows its envelope at once, and X-ray / plenum check work in a model with ducts.

---

## 7. Step 7: Ceiling changes (orphans, off-ceiling, Re-seat)

- At load / after F5: for each modelled terminal, `SurfaceAbove` / below within 0.3 m against Ceilings:
  - Unhosted and more than `AirflowOffCeilingTolerance` (25 mm) from the nearest ceiling bottom → finding
    **off ceiling by X** (only when a ceiling is within 0.3 m: exposed diffusers aren't flagged).
  - Hosted with `ElementRecord.HostId == 0` → **orphaned (lost its host)**.
- **Re-seat to ceiling** (finding action, one or *Re-seat all*): a `Transform` edit with `Translation = (0, 0, dz)`
  per terminal, each its own edit, so Revit errors (e.g. a duct drop too short) refuse only that one; the
  skipped ones are listed with Revit's message.

**Done when:** lowering a ceiling in Revit, F5 in BimGo, *Re-seat all* puts every unhosted diffuser back on it,
and a connected one that can't move is reported, not forced.

---

## 8. Step 8: BimGo family and loading

### 8.1 The family (authored in Revit 2025 by the user or Gavin)

`BimGo Square Cone Diffuser` (level-based, unhosted) and `BimGo Square Cone Diffuser (Face Hosted)`:
- Category Air Terminals; **origin at the centre of the face, face at Z = 0**, X along a side.
- One round duct connector on top: Supply Air, flow configuration Preset, flow → instance **Flow**, diameter →
  **Neck Diameter**.
- Type: **Face Width**, **Face Length**, **Pattern** (`4way`), **BimGo Catalog Key** (`generic-3cone-square`).
  Instance: **Flow**, **Blanked Sides**.
- Types: IP `24x24 - 6in Neck` … `16in`, `12x12 - 6in / 8in`; SI `600x600 - 150mm Neck` … `400mm`,
  `300x300 - 150mm / 200mm`.
- Saved in `src/BimGo.Revit/Resources/Families/` (**new folder: Gavin's yes**), copied to the add-in folder by the
  build, listed in `THIRD-PARTY-NOTICES.txt` as BimGo's own (MIT-0).

### 8.2 Loading

- Core `Live/LiveProtocol.cs`: `MessageTypes.FAMILY_LOAD = "family.load"`, `FAMILY_LOAD_RESULT =
  "family.load.result"`; payloads `{ family, types[] }` → `{ success, message, entries: LibraryEntry[] }` plus a
  small `.bimgo` with the new templates' geometry (same snapshot mechanism as extraction).
- Revit `Extraction/FamilyLoader.cs`: `LoadFamilySymbol` per type from the families folder, `IFamilyLoadOptions`
  never overwriting; one transaction "BimGo: Load diffuser types"; then the library template pass for those
  types only (rolled back as today). Used by `LiveDispatcher` (`family.load`), the ribbon
  `Cmd_LoadDiffusers` (IP / SI picker), and push.
- **Files:** `Rendering/DiffuserMeshes.cs` (App) generates preview geometry for spec types from face / neck
  (three nested square cones + neck), so not-loaded BimGo types can be placed. The journal's `place` gains
  optional `FamilyName` / `TypeName`; push loads missing BimGo types first inside its `TransactionGroup`; the
  preview lists "will load …".
- Diffusers tab shows **loaded / not loaded**; firm families and profiled Autodesk families appear only when
  loaded in the project (Autodesk ones say "load from Autodesk's catalog in Revit").

**Done when:** in a project without the family, picking a type in BimGo loads it (live) or loads it on push
(file), one Ctrl+Z each; existing types untouched.

---

## 9. Step 9: Documentation and wrap-up

- README: §1 (airflow row), §3 controls (Y, C, +/−, F re-seat, G snap in airflow), §5 *How it works* (airflow
  paragraph like the sun hours one), §6 format (`air.json`, `airflow.json`), §7 live (`family.load`, `SetFlow`),
  §8 limitations / to verify (every API in the steps' lists; the ASHRAE constants; generic catalog is
  illustrative), §9 history, §10 (no new packages).
- `ai/2610xx_Airflow/` build notes per step (what changed, APIs to verify, test results).
- F1 help entries.

---

## 10. Order, size and risks

| Step | Depends on | Size (rough) | Main risk |
|---|---|---|---|
| 1 Core foundations | — | M | None (pure maths, tested) |
| 2 Extraction | 1 | M | Flow parameter variety across families |
| 3 Display | 1, 2 | M | Envelope drawing cost with many diffusers (batch into one `Overlay3D` pass) |
| 4 Trace | 3 | L | Calibration: the model is empirical; mitigated by catalog-based curves and hand-checked test rooms |
| 5 SetFlow | 2 | S | Read-only / calculated flow parameters (clear refusal) |
| 6 Placement | 2, 3 | L | Ceiling-hosted families and linked-face references |
| 7 Ceiling changes | 4, 6 | S | — |
| 8 Family + loading | 6 | M | Family authoring; `LoadFamilySymbol` during a live session |
| 9 Docs | all | S | — |

Steps 1–4 deliver the core value (see throws and drafts in an existing model) and can ship on their own.
5–8 add editing and can follow in a second round. Each step is one zip + notes for Gavin, per the usual rounds.
