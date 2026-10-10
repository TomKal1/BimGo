# BimGo: Airflow (air terminals, throw, drafts): plan

**Status:** plan only, nothing built. Written 2026-10-10 from a brainstorm with the user. To be staged with
AskUserQuestion (recommendation first) round by round, like the other rounds.

**Goal:** let mechanical engineers walk the model and *see* supply air: how far each diffuser throws, where jets
collide, where they hit walls and drop, where pendant lights / beams / bulkheads under the ceiling knock them off,
and where the occupied zone gets a draft. Change a diffuser's CFM (or place a new diffuser) in BimGo and see the
result immediately; the change goes back into Revit like every other edit.

**What this is not:** CFD. Every readout is labelled *indicative: catalog-based screening*. It is a fast
design-review aid built on the same catalog throw data engineers already select diffusers with.

---

## 1. Decisions so far

| Topic | Decision |
|---|---|
| Velocity model | Empirical jet decay fitted to catalog throw data (T150 / T100 / T50), traced along surfaces against the BVH. No CFD in the first rounds |
| Catalog data | A generic, built-in **3-cone square, 4-way** family (illustrative values, clearly labelled), plus user-imported catalogs (CSV / JSON). No manufacturer tables shipped without permission |
| CFM | **Editable in BimGo and written to the Revit model**: a new additive edit op, live (one undo step in Revit) or in the file journal (pushed later) |
| Placing diffusers | **Uses the existing Place gun / family library**, not a new tool. Air Terminals added to the library; ceiling hosting improved (§5.3). Moving / copying / deleting already work through Gizmo / Clone / Demolish |
| Changes made in Revit | The existing **model changed → F5 refresh** brings new terminals, flows and sizes in. No new sync path |
| Dependencies | None. Pure C# maths in Core, drawing via `Overlay3D` / `UiBatch`. No new folders outside `ai/` |
| Compatibility | `.bimgo` `formatVersion` stays 1 (new optional `air.json` entry); live protocol stays 1 (new optional edit op, same pattern as `Place`) |

---

## 2. Data

### 2.1 Air terminals in the scene (Core: `Scene/AirTerminals.cs`)

```csharp
/// One supply / return / exhaust terminal (Revit Air Terminals category).
public sealed class AirTerminal
{
    public int Element;              // index into SceneData.Elements
    public long ElementId;           // Revit id (edits)
    public Vector3 Position;         // centre of the face, scene-local (m)
    public Vector3 Normal;           // way the face points (ceiling diffuser: -Z)
    public Vector3 Forward;          // the family's X axis in plan: orients the throw pattern
    public float FaceWidth, FaceDepth; // m
    public float NeckDiameter;       // m (round) or 0
    public float NeckWidth, NeckHeight; // m (rectangular) or 0
    public float FlowLps;            // L/s internally (display CFM or L/s)
    public bool FlowEditable;        // false when Revit computes / locks it
    public AirRole Role;             // Supply | Return | Exhaust | Unknown (from the connector's system classification)
    public string Pattern;           // "4way" | "3way" | "2way" | "2way-corner" | "1way" | "radial" | null
    public string CatalogKey;        // matched catalog family, or null
    public bool Estimated;           // flow, size or pattern guessed
}
```

Stored in `air.json` (optional `.bimgo` entry; older readers ignore it). Live sessions get it in each snapshot.

### 2.2 Catalog (Core: `Scene/DiffuserCatalog.cs`)

Raw rows exactly as a submittal prints them, plus a fitted decay curve per row.

```jsonc
{
  "families": [{
    "key": "generic-3cone-square-4way",
    "label": "Generic 3-cone square, 4-way (illustrative)",
    "pattern": "4way",
    "attached": true,                      // ceiling (Coanda) jet
    "match": ["3 cone", "3-cone", "square cone", "SCD"],  // family / type name keywords
    "units": "imperial",                   // rows in in / CFM / in wg / ft; stored internally in SI
    "rows": [
      { "neck": 8, "face": 24, "flow": 250, "pt": 0.06, "nc": 20,
        "throw": { "150": 5, "100": 8, "50": 13 } }
    ]
  }]
}
```

- **Built-in:** one generic family covering neck 6, 8, 10, 12, 14, 16 in × face 12 / 24 in, a few flows each.
  Values typical of published 3-cone diffusers, rounded and labelled illustrative.
- **User catalogs:** `%AppData%\BimGo\Catalogs\*.json` and `*.csv` (a CSV template: one row per neck / face /
  flow). Imported in Options; a bad row is skipped with a log line, never an exception.
- **Matching:** catalog `match` keywords against the family / type name (like `EmissiveKeywords`), then
  nearest neck / face size. A per-type override (catalog + pattern) saved in the model folder / `.bimgo`, like
  texture overrides. No match → the generic family, `Estimated = true`.

### 2.3 Fit and interpolation (pure maths, unit-tested)

- **Decay:** in the main jet zone `V(x) = a · x^-n` (ASHRAE Fundamentals, *Space Air Diffusion*). Fit `a`, `n` per
  row by least squares on `ln V` vs `ln x` over the three throws; clamp `n` to 0.8–1.3; with fewer than two throws,
  `n = 1`.
- **Flow between rows:** theory gives `a ∝ Q / √A_core`, so for a neck / face size `a/Q` and `n` are close to
  constant. Interpolate `a/Q` and `n` linearly in Q; extrapolate up to ±25 % beyond the table, flagged beyond that.
- **Pressure / noise:** `Pt ∝ Q²` (fit `k` per size), `NC ≈ NC_ref + 10·log10(Q/Q_ref)`, scaled in the same way.
- **Pattern split:** flow divides evenly over open sides; a side blanked by a baffle hands its share to the others.
- **Near field:** for `x` under ~3 neck diameters velocity is capped at the face velocity (no singularity).

---

## 3. The airflow trace (Core: `Scene/AirflowTrace.cs`)

Pure maths over an injected geometry callback (`Func<ray…, hit>` / box query), so it is testable in Core with
simple boxes and run against `Bvh` in the App.

For every supply terminal and every open side:

1. **Ceiling run.** March along the throw direction in steps (~0.1 m). Velocity from the fitted curve at path
   length `s`. Jet band thickness `δ(s) ≈ k·s` (k ≈ 0.1, a setting).
2. **Events**, first one wins:
   - **Wall:** a surface ahead within the band → the path turns down the wall (`s` keeps counting, small turn
     loss). Record the velocity where it enters the occupied zone.
   - **Obstruction below the ceiling** (lighting fixtures, beams, ducts, bulkheads, anything in the band): the jet
     comes off the ceiling and drops from that point. An object fully below the band is ignored (the jet passes
     over a pendant on a long drop rod).
   - **Collision with another jet:** where two opposing jets' momentum fluxes (∝ ρ·V²·width) are equal, which
     is not the midpoint when flows differ. The combined jet drops; downward velocity from the combined momentum.
   - **Terminal:** velocity falls under the still-air threshold (~25 fpm / 0.125 m/s): the jet ends.
3. **Falling jets** (after a wall, obstruction or collision) continue as a free jet down to the floor, stopping
   on furniture / casework tops (BVH hit).
4. **Draft check:** wherever a path is inside the **occupied zone** (floor to 1.8 m, set back 0.3 m from walls;
   both settings) with velocity above the **draft limit** (50 fpm / 0.25 m/s default, a setting) → a finding.
5. **Room figures:** throw ratio T50 / L per terminal (L = distance to the wall or to halfway to the next
   terminal) and an indicative ADPI band. ASHRAE gives about 0.8 as the best T50 / L for ceiling diffusers;
   confirm the acceptable range against the table before showing pass / fail.

Results: per terminal, per side, a list of path points `(position, velocity)` and events
`(kind, position, velocity, other element)`. Cached and recomputed only for terminals near an edit.

**Later (round 4):** cold-jet separation from the Archimedes number (supply ΔT setting), heating mode, an
occupied-zone velocity heatmap on the Sun Hours grid, a per-room coarse solver or OpenFOAM export.

---

## 4. In the viewer (App)

| Feature | Detail |
|---|---|
| **Airflow toggle** | Pause menu → Airflow (and a key, chosen in the hotkey review). Off by default; the trace runs when it is turned on, in the background with the existing progress bar |
| **Throw envelopes** | 150 / 100 / 50 fpm limits drawn as see-through bands on the ceiling (and down walls where the path turns), colour per velocity, via `Overlay3D` quads. 4-way → a rounded square |
| **Paths and events** | Thin lines along each traced path; markers at events: red = draft in occupied zone, amber = collision / wall drop / obstruction above the zone, grey = jet ended |
| **Scan gun readout** | Aiming at a terminal shows type, catalog match (or *estimated*), flow, neck / face, Pt, NC, T150 / T100 / T50, T50 / L. Plus a small velocity-vs-distance graph per side (log–log) marking 150 / 100 / 50 and where the wall / collision / obstruction falls |
| **Findings list** | Panel listing every draft risk with *Go to* (teleport) and *Make comment* (a normal comment with a snapshot, so it exports to BCF) |
| **Edit CFM** | Scan a terminal → **+ / −** steps (10 CFM / 5 L/s, Shift = ×5) or type a value. Envelopes and graph update live as a preview; Enter commits the edit (§5.1), Esc cancels |
| **Ceiling X-ray** | A toggle (pause menu and a key, picked in the hotkey review; on automatically while the Place gun holds an air terminal): every Ceilings element draws see-through (~20 % opacity, no depth write, outlines kept) or hidden, the user's choice. Ceilings still take aim, so placement lands on them. Builds on the existing hide / isolate code in `GameSession.Visibility`, which only hides whole elements today |
| **Plenum check** | With X-ray on and a terminal held, a vertical probe from the aimed ceiling point up to the slab: lists what is above (ducts, pipes, beams, light housings, sprinklers, cable tray) with the clear height, and turns the placement marker amber / red when the clear height is below the terminal's needed depth (family height + a plenum allowance, a setting). Only sees categories that were loaded, so the readout says which MEP / structure categories are off |
| **Smoke mode** (round 4) | Particles riding the paths at a speed proportional to velocity, preallocated buffers, no per-frame allocations |
| **Units** | Follows the planned metric / imperial setting (Round B of the main plan); until then a local CFM ↔ L/s toggle in the airflow panel |

---

## 5. Writing back to Revit

### 5.1 New edit op: `SetFlow`

- `EditOp.SetFlow` (additive to protocol 1, same pattern as `Place`: an older add-in can't read it and the app
  times out with a clear toast). `EditRequest` gains an optional `float Value` (flow, L/s).
- **Revit side** (`Bridge/RevitEditor.cs`): one transaction "BimGo: Set flow". Find the flow parameter on the
  instance: `RBS_DUCT_FLOW_PARAM` first, then the family parameter the supply connector's flow is associated
  with. Convert L/s → internal units (`UnitUtils.ConvertToInternalUnits(…, UnitTypeId.LitersPerSecond)`), `Set`.
  Read-only / formula-driven / calculated → `Success = false` with the reason ("flow is calculated by the
  system", "flow is a type parameter", etc.), shown as a toast. The parameter is never changed on the type.
- **File mode:** the journal records `setflow` (element, old value, new value); undo / redo restore it.
  **Push** applies it and reports a conflict when Revit's current value differs from the recorded old value,
  matching how other push conflicts are shown.
- **After success:** the app updates its `AirTerminal.FlowLps` and re-traces nearby terminals. No re-extract.
- Changing neck / face size means changing the **type**: a `SetType` op is a possible follow-up, not in this plan.

### 5.2 Changes made in Revit

Already handled: Revit edits → *model changed* → F5 → re-extract (which now includes `air.json`). Nothing new
except making the airflow overlay rebuild after a refresh.

### 5.3 Placing diffusers (existing Place gun)

- **Library:** add Air Terminals to the family library categories so diffuser types show in the Place gun.
- **Ceiling hosting:** today `FamilyPlacer` hosts work-plane / face-based types on the **level's** plane and
  moves them up. For diffusers, when the aimed surface is a ceiling, host on the ceiling's bottom face instead
  (reference from the aimed element's geometry), falling back to the current behaviour. Non-hosted (level-based)
  diffuser families work today with the instance moved to the ceiling height.
- **Hosting choice (decided per placement setting, see the note below):** *unhosted* (default recommendation:
  level-based or work-plane on the level, moved to the ceiling height) or *hosted on the ceiling face*.
- **What happens when a host ceiling is deleted:**
  - *Ceiling in the same model:* Revit deletes hosted instances with their host (like doors in a wall), so the
    diffuser goes too. BimGo's Demolish gun already tells you about hosted inserts; extend that warning to say how
    many air terminals a ceiling carries before it is demolished / deleted.
  - *Ceiling in a linked architectural model* (the usual setup: MEP in its own model): the diffuser is **not**
    deleted. After the link reloads it becomes an *orphan* (Revit warns that it lost its host), keeps its position
    and stops following the ceiling height. Hosting on a linked face needs `Reference.CreateLinkReference`; BimGo
    loads links read-only, so it can aim at them.
  - *Unhosted diffusers* never get deleted by ceiling changes, but don't follow a ceiling that moves up or down.
  - BimGo flags terminals that are orphaned (hosted family, no host) or more than 25 mm off the nearest ceiling
    surface below / above them in the findings list, so ceiling changes by the architect show up after F5.
- **Snap:** an optional 600 mm / 24 in ceiling-grid snap for the Place / Gizmo gun when holding a terminal.
- **Flow on a new terminal:** the family's default; it isn't connected to ductwork, so the readout marks it
  *unconnected* and the user sets CFM with `SetFlow`. Connecting ducts stays a Revit job.

---

## 6. Build rounds

| Round | Scope | Done when |
|---|---|---|
| **AF1: data + readout** | `AirTerminals`, `DiffuserCatalog` (built-in generic, CSV / JSON import, fit, interpolation), `air.json` read / write, `SceneExtractor.Air.cs`, Scan gun readout + throw graph, ceiling envelopes (straight runs, no events) | A real model's diffusers show catalog throw envelopes and correct readouts; Core tests green |
| **AF2: trace + findings** | `AirflowTrace` (walls, obstructions, collisions, falling jets, occupied zone, draft limit, T50 / L), paths and markers, findings panel → comments / BCF, settings | Draft risks found in a test room match a hand calculation from the catalog table |
| **AF3: edits** | `SetFlow` op (live, journal, undo / redo, push + conflicts), CFM editor with live preview, Air Terminals in the family library, ceiling X-ray + plenum check, hosting choice + orphan / off-ceiling flags, grid snap | CFM changed in BimGo appears in Revit's schedule; a diffuser placed in BimGo shows its envelope at once |
| **AF4: extras** (optional) | Smoke particles, occupied-zone heatmap, cold-jet separation / heating mode, OpenFOAM export | Chosen with the user after AF3 |

Fit with the main plan: after Rounds A and B (presentation, review tools) or between them; the user decides.
AF1 depends on nothing; AF3's CFM display benefits from Round B's unit setting.

---

## 7. Files

| Project | File | Notes |
|---|---|---|
| Core | `Scene/AirTerminals.cs` | Terminal records, roles, patterns |
| Core | `Scene/DiffuserCatalog.cs` | Schema, built-in generic family, CSV / JSON import, fit, interpolation |
| Core | `Scene/AirflowTrace.cs` | Trace + events over an injected geometry query |
| Core | `Format/…` | `air.json` entry in reader / writer; `setflow` in `EditJournal` |
| Core | `Edits/EditMessages.cs` | `EditOp.SetFlow`, optional `Value` |
| Core | `Scene/LaunchSettings.cs` | Airflow settings (draft limit, zone, band factor, units), sanitised |
| Revit | `Extraction/SceneExtractor.Air.cs` | Air Terminals: position, normal, sizes, flow, role, pattern, catalog match |
| Revit | `Extraction/FamilyPlacer.cs` | Ceiling-face hosting |
| Revit | `Extraction/SceneExtractor.Library.cs` | Air Terminals in the library categories |
| Revit | `Bridge/RevitEditor.cs`, `RevitEditor.Push.cs` | `SetFlow` live and pushed |
| App | `Game/GameSession.Airflow.cs` | Toggle, background trace, drawing, findings panel, CFM editor |
| App | `Game/AirflowStudy.cs` | Trace state / cache, BVH adapter |
| App | `Game/Guns/ScanGun.cs` | Terminal readout + graph |
| Tests | `AirflowTests.cs` | Fit vs known rows, interpolation, pattern split, collision point with unequal flows, wall turn, obstruction in / under the band, `air.json` round trip, `setflow` journal + undo |

---

## 8. To verify (Revit API, needs Gavin's build)

- Air terminal flow: is `RBS_DUCT_FLOW_PARAM` writable on typical diffuser families, and how do connector
  "Flow configuration" settings (Calculated / Preset / System) change that? Reading the associated family
  parameter from the connector.
- Neck size: from the connector (`Connector.Radius` / `Width` / `Height`) vs type parameters; face size from type
  parameters vs bounding box.
- System classification (supply / return / exhaust) from the connector when the terminal isn't connected.
- `NewFamilyInstance(Reference face, …)` with a ceiling's bottom face reference for face-based diffusers, for a
  host ceiling and for a linked ceiling (`Reference.CreateLinkReference`).
- Host deletion: confirm hosted air terminals are deleted with a same-model ceiling and orphaned (kept) when a
  linked ceiling goes; how to read the orphan state (`FamilyInstance.Host == null` on a face-based family,
  `HostFace`).
- Units: `UnitTypeId.LitersPerSecond` / `CubicFeetPerMinute` conversion helpers on 2025–2027.

## 9. Questions to stage (AskUserQuestion, recommendation first)

1. **Units:** CFM / fpm / in only, or both with a toggle (recommended: both; internal SI)?
2. **Draft limit and occupied zone defaults:** 50 fpm (0.25 m/s), 1.8 m / 6 ft, 0.3 m / 1 ft wall setback?
3. **Return / exhaust grilles:** show them (capture zone only, no throw) or ignore in AF1?
4. **Linear slot diffusers and sidewall grilles:** in scope for the catalog schema now (recommended: schema yes,
   built-in data no), or square ceiling diffusers only?
5. **Placement order:** AF1–AF3 after main Round A / B, or before?
6. **Real catalog data:** one or two submittal tables (kept out of the repo) to check the fit against.
7. **`SetType` (change neck / face size from BimGo):** include in AF3, or later?
8. **Ceiling X-ray:** see-through (recommended) or hidden by default; on automatically with a terminal held?
9. **Hosting default for diffusers placed in BimGo:** unhosted (recommended: survives ceiling deletion in
   either model) or hosted on the ceiling face?
