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
| Placing diffusers | **Uses the existing Place gun / family library**, not a new tool. Air Terminals added to the library. **Unhosted at the aimed ceiling's bottom elevation by default**, hosted to the ceiling face as an option (§5.3). Moving / copying / deleting already work through Gizmo / Clone / Demolish |
| Units | **IP and SI from the start**, **read from the Revit project's units** and saved in the file (decided 2026-10-10): SI internally, every input / readout / catalog / family set in either (§2.4) |
| Diffuser families | A **BimGo diffuser family set** known to work, **loaded from BimGo's Place gun** when picked (live: Revit loads it; file: loaded on push), plus a checker that rates any other family against the same spec (§2.5) |
| Moving connected diffusers | **Keep today's behaviour** (decided 2026-10-10): any Revit error rolls the move back, so Revit never deletes a duct (§5.4) |
| Changes made in Revit | The existing **model changed → F5 refresh** brings new terminals, flows and sizes in. No new sync path |
| Dependencies | None. Pure C# maths in Core, drawing via `Overlay3D` / `UiBatch`. No new folders outside `ai/` |
| Compatibility | `.bimgo` `formatVersion` stays 1 (new optional `air.json` entry); live protocol stays 1 (new optional edit op and `family.load` message, same pattern as `Place`) |

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

### 2.4 Units: IP and SI

- **Internally SI** everywhere in Core: m, m/s, L/s, Pa. Conversion only at the edges (catalog import, readouts,
  inputs, CSV export). One Core helper, `Scene/Units.cs`, with the factors and formatting:
  1 CFM = 0.471947 L/s · 1 fpm = 0.00508 m/s · 1 in wg = 249.089 Pa · 1 ft = 0.3048 m · 1 in = 25.4 mm.
- **One setting, read from the project (decided 2026-10-10):** extraction reads the Revit project's units
  (`doc.GetUnits()`: the length format of `SpecTypeId.Length`, and the duct flow format of `SpecTypeId.AirFlow`)
  and stores the result as `units` in `air.json`, so `.bimgo` files open in the units they were exported in. IP /
  SI in Options → Airflow is an override, saved per model (model folder / `.bimgo`), never changing the Revit
  project. A project mixing systems (e.g. mm lengths with CFM flows) follows its **flow** units for airflow
  readouts and its **length** units for sizes and elevations. The main plan's Round B metric / imperial setting adopts the same `Units` helper (whichever round is
  built first creates it; the other reuses it), so there is one switch in the end.
- **Readouts and inputs:**

  | Quantity | IP | SI |
  |---|---|---|
  | Flow | CFM | L/s (m³/h as an option) |
  | Velocity | fpm | m/s |
  | Pressure | in wg | Pa |
  | Throw, distances | ft (ft-in for elevations) | m (mm for elevations) |
  | Neck / face | in | mm |
  | CFM editor step | 10 CFM (Shift ×5) | 5 L/s (Shift ×5) |

- **Envelope velocities follow the unit system,** not a strict conversion: IP shows the usual **150 / 100 / 50
  fpm**, SI the usual **0.75 / 0.5 / 0.25 m/s** (150 fpm is 0.76 m/s). Because every row has a fitted curve,
  BimGo computes either set from either catalog. Draft checking is temperature-aware (§3 step 4); its simple
  mode defaults to 50 / 70 fpm or 0.25 / 0.35 m/s (cooling / heating).
- **Catalog files declare their units** (`"units": "IP"` or `"SI"`), and SI catalogs list their own terminal
  velocities (`"throw": { "0.75": 1.6, "0.5": 2.5, "0.25": 4.1 }`, in m). The fit doesn't care which.
- **Nominal sizes are not conversions:** an 8 in neck is 203 mm, a metric 200 mm neck is a different product.
  Necks and faces match on the nearest catalog size within a tolerance (5 %), never by converting the name.
  The built-in generic catalog has an IP size range (6–16 in necks, 12 / 24 in faces) and an SI range (150–400 mm
  necks, 300 / 600 mm faces).
- **Noise:** NC in both. SI catalogs that give NR store it as well; the readout shows whichever the row has.
- **Tests:** every conversion both ways, an IP and an SI catalog of the same product giving the same curve, and
  round trips of `air.json` and catalog CSV in both systems.

### 2.5 BimGo diffuser families (known to work)

**Why:** most of the guessing in this plan (where the face is, which parameter is flow, neck size, pattern,
hosted or not) disappears with families built to one spec. Firms will still use their own, so BimGo also checks
any family against the same spec.

**The family set** (authored in Revit by the user or Gavin, decided 2026-10-10; saved in Revit 2025 so 2026 /
2027 upgrade them on load; plain generic geometry, no manufacturer branding or data).

**Starting point: Autodesk's default diffusers (Load Autodesk Family, the cloud content catalog).**
- **Licence first.** That content comes under Autodesk's terms for use with Autodesk software. Using it in
  projects is fine; **redistributing the `.rfa` files** (or edited copies) inside BimGo's MIT-0 repo and
  installer may not be. Check Autodesk's content terms before anything derived from them ships.
- **Two ways, depending on the answer:**
  - *Redistribution allowed:* adapt the Autodesk families to the spec below, ship them as the BimGo set, and
    list them in `THIRD-PARTY-NOTICES.txt` under Autodesk's terms (not as MIT-0).
  - *Not allowed (assume this until checked):* **ship nothing from Autodesk.** Instead BimGo ships a
    **family profile** for each Autodesk default diffuser: its family name, which parameters hold flow, neck and
    face size, where its face sits relative to its origin, its pattern and the catalog key to use. The user loads
    the family from Autodesk's catalog in Revit as usual; the checker recognises it by name and rates it
    **Ready** through the profile. BimGo's own families (authored from scratch to the spec) can still ship
    MIT-0 alongside.
- Profiles are plain JSON in Core (`DiffuserProfiles.json`), so a firm can add profiles for its own families
  the same way, without editing the family.
- Note: many Autodesk default diffusers are **face-based**. In unhosted mode BimGo puts face-based families
  on the level's plane at the ceiling height (§5.3), so they still don't attach to the ceiling.

| Family | Types | Placement |
|---|---|---|
| `BimGo Square Cone Diffuser` | IP: 12×12 and 24×24 face × 6–16 in necks · SI: 300×300 and 600×600 × 150–400 mm necks | Unhosted (level-based) |
| `BimGo Square Cone Diffuser (Face Hosted)` | Same types | Face-based, for the hosted mode |
| Later | 1 / 2 / 3-way louvered face, linear slot, return / exhaust grille, sidewall grille | Both |

**The spec** (what makes a family "BimGo-ready"):
1. Category **Air Terminals**; unhosted = level-based, hosted = face-based work plane.
2. **Origin at the centre of the face, face at Z = 0**, X along a side: placement and the flush offset need no
   guessing.
3. **One duct connector** on top, system classification Supply Air (Return / Exhaust for grilles), flow
   configuration Preset, its flow linked to an **instance** parameter **Flow** (so `SetFlow` can write it) and its
   size linked to **Neck Diameter** (or **Neck Width** / **Neck Height**).
4. Type parameters **Face Width**, **Face Length**, **Pattern** (text: `4way` …) and **BimGo Catalog Key** (text,
   matches a catalog family key exactly, so no keyword matching is needed).
5. Optional instance parameter **Blanked Sides** (text, e.g. `N,E`) for corner and wall-side diffusers.
6. Type names carry the units: `24x24 - 8in Neck` / `600x600 - 200mm Neck`.

**Getting them into a project: from BimGo, at the point of placing (decided 2026-10-10).**
- **Diffusers tab in the Place gun's library.** Lists every BimGo diffuser type in the project's units (IP or SI
  set, §2.4), each marked **loaded** or **not loaded**, grouped by family → face → neck, with the catalog
  numbers (flow range, T50 at mid flow, NC) beside each. Firm families already in the project sit in the same
  tab with their checker badge.
- **Picking a type that isn't loaded, live session:** BimGo asks Revit to load it. A new additive live message
  `family.load` (`{ family: "BimGo Square Cone Diffuser", types: ["24x24 - 8in Neck"] }`) → the add-in runs
  `doc.LoadFamily` from the add-in's `Families` folder in one transaction ("BimGo: Load diffuser family", one
  undo step), keeps every existing type (never overwrites), and loads only the types asked for
  (`LoadFamilySymbol`) so the project doesn't fill with unused sizes. The answer `family.load.result` carries
  the new type ids and the library template for them (the existing rolled-back template pass, run for those
  types only), so the type appears ready to place **without an F5**. An older add-in doesn't know the message:
  the app times out and says to update the add-in, like `Place`.
- **Picking a type that isn't loaded, `.bimgo` file:** there is no Revit to load into, so BimGo uses a
  **built-in template** for each BimGo type: its geometry is simple enough to generate in the app from face
  and neck sizes (a square cone stack and a round neck), so no Revit is needed to preview or place. The journal
  records the `place` with the family and type **name** (no type id yet). On **push**, the add-in loads any
  missing BimGo family types first, inside the push's transaction group (so one Ctrl+Z undoes it all), then
  places; the push preview lists "will load: BimGo Square Cone Diffuser, 2 types" before anything changes. A
  firm family that isn't in the project can't be placed in file mode.
- **Revit ribbon** → BimGo → **Load diffusers…** stays as a second way in (same loader), for users who want the
  types in the project before opening BimGo.
- The `.rfa` files ship with the add-in (a `Families` folder beside it; **a new folder: needs Gavin's yes**) and
  are listed in `THIRD-PARTY-NOTICES.txt` (§2.5 licence note). Loading from the Diffusers tab only offers
  families BimGo ships; Autodesk families covered by a profile but not shipped show as "load from Autodesk's
  catalog in Revit", and are placeable once loaded. Loading never needs the family library option turned
  on: picking from the Diffusers tab turns on the Air Terminals category for the session.

**The checker** (runs during extraction, Revit side):
- Rates each loaded air terminal family **Ready** (meets the spec), **Works with guesses** (BimGo can place it
  and read flow, but estimates face offset, size or pattern) or **Can't place** (ceiling-hosted in unhosted mode,
  unsupported placement type, no readable flow), with the reasons.
- Shown as a badge in the Place gun's library and the Scan gun's readout, and as a CSV report from the airflow
  panel, so a firm can fix its own families to the spec.

---

## 3. The airflow trace (Core: `Scene/AirflowTrace.cs`)

Pure maths over an injected geometry callback (`Func<ray…, hit>` / box query), so it is testable in Core with
simple boxes and run against `Bvh` in the App.

For every supply terminal and every open side:

0. **Attached or free.** Catalog throws assume a diffuser flush with a ceiling (the jet clings to it). If no
   surface lies within ~50 mm above the face (exposed / open-ceiling diffusers hung off the duct), the jet is
   **free**: throw scaled by a correction (setting, default 0.7, to check against catalog "exposed mounting"
   notes), no ceiling run, and a cooling jet starts falling straight away. The readout says *free jet*.
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
4. **Draft check: temperature and speed together** (decided 2026-10-10: cooling must allow less speed than
   heating). A fixed velocity limit can't do that, so the check uses the **effective draft temperature** (EDT),
   the comfort index behind ADPI (ASHRAE Fundamentals, *Space Air Diffusion*):
   - IP: `EDT = (t_x − t_room) − 0.07 · (V_x − 30)` (°F, fpm). SI: `EDT = (t_x − t_room) − 8 · (V_x − 0.15)`
     (K, m/s).
   - **Comfortable** when −3 °F < EDT < +2 °F (−1.7 K to +1.1 K) **and** V_x < 70 fpm (0.35 m/s); otherwise a
     finding: *cold draft* (EDT too low) or *warm / too fast* (EDT too high or speed over the cap).
   - **Jet temperature** `t_x` decays with the velocity: `(t_x − t_room) / (t_supply − t_room) ≈ 0.8 · V_x / V_0`
     (setting, default 0.8), using the same fitted curve.
   - **Effect:** a cold jet already carries a negative temperature difference, so it fails at a lower speed than a
     warm one: in cooling, the limit at the edge of the zone works out near 50 fpm or lower; in heating, speeds
     up to the 70 fpm cap can pass. That's the behaviour asked for, from one formula.
   - **What the user enters: one toggle** (decided 2026-10-10: temperatures and heating flows aren't in the
     model, and per-box entry is too much work). The airflow panel has **Cooling | Heating**, nothing else
     by default:
     - **Cooling:** each diffuser at its Revit **Flow** (taken as the design maximum), with typical design
       temperatures: 55 °F (13 °C) supply, 75 °F (24 °C) room.
     - **Heating:** every diffuser at **one global share of its cooling flow** (**default 50 %, decided 2026-10-10**;
       the usual VAV reheat box heating minimum range), with 90 °F (32 °C) supply, 70 °F (21 °C) room. No per-box entry.
     - **Advanced** (collapsed, saved per model): the four temperatures and the heating share. Most users never
       open it.
     - Later, only if asked for: a per-terminal heating flow read from a parameter when the model has one.
   - **Heating** still shows how far down each jet gets (the 150 / 100 / 50 fpm envelopes and paths), so warm air
     that stalls near the ceiling is visible. No ventilation calculations: ASHRAE 62.1 Ez / outdoor air stays
     out of BimGo (decided 2026-10-10).
   - **Simple mode** (a toggle): a fixed limit per mode instead of EDT, default 50 fpm / 0.25 m/s cooling and
     70 fpm / 0.35 m/s heating, for users who want a plain velocity check.
   - **To check before shipping:** the EDT constants, comfort band, 70 fpm cap and the 0.8 temperature-decay
     ratio are quoted from memory of ASHRAE Fundamentals; confirm them against the current edition.
   - The same (default) supply / room temperatures drive the cold-jet separation and heating behaviour planned for later
     (Archimedes number), so those inputs are needed only once.

   **The zone the check applies in:**
   - **Default: ASHRAE 62.1 *breathing zone*** (decided 2026-10-10): 3 in to 72 in (75 to 1,800 mm) above the
     floor, more than 2 ft (600 mm) from walls and fixed air-conditioning equipment. One setback for every wall,
     so it needs no exterior / interior information.
   - 62.1 is the ventilation standard; draft comfort is ASHRAE 55's subject. Older editions of 55 define an
     *occupied zone* of floor to 6 ft (1.8 m), 3.3 ft (1 m) from exterior walls / windows and fixed HVAC
     equipment, 1 ft (0.3 m) from interior walls. Offered as a second preset; check the current 55 edition
     before shipping its numbers.
   - A **Custom** preset with every value editable.
   - Presets are named after the standard with its edition, e.g. "ASHRAE 62.1 breathing zone", so the user knows
     where the numbers come from.

   **Exterior or interior wall** (only the 55 preset and Custom need it). The wall type's *Function* parameter
   can't be trusted: many templates leave every wall at the default. So BimGo decides from the geometry,
   reusing the daylight study's ray classification (`DaylightRay`: sky / ground / outside surface / own room):
   1. **Glazing:** curtain walls, and walls hosting windows or doors to the outside, are exterior.
   2. **Rays:** from a grid of points on the wall's room-facing side, cast rays through to the far side and out
      across a hemisphere (glass passes). If most escape to sky or ground without re-entering a room or hitting
      the model again, the wall is exterior. Works without Revit rooms (rooms only refine "far side is
      inside").
   3. **Tie-break:** the *Function* parameter, only when the ray test is split.
   4. **Override:** Scan a wall → *Mark exterior / interior*; saved per model (model folder / `.bimgo`, additive),
      like texture overrides. The zone overlay shows exterior walls in a different colour so mistakes are easy to
      spot.
   Computed once per model when the 55 preset or Custom first needs it, then cached; recomputed after F5 for
   walls that changed.
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
| **Units** | IP / SI setting (§2.4); every readout, input, graph axis and envelope set follows it |

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

- **Library:** add Air Terminals to the family library categories so diffuser types show in the Place gun, with
  the checker's badge (§2.5). The BimGo families need none of the guessing below; other families fall back to it.
- **Three placement modes** (Options → Airflow, default **Unhosted at ceiling**; a key while holding a terminal
  cycles them for that placement, picked in the hotkey review; the readout shows the current mode):

  **Unhosted at ceiling (default, decided 2026-10-10).** The diffuser is never attached to the ceiling, so an
  architect deleting or replacing a ceiling never deletes it.
  1. *Elevation:* when the aim hits a **Ceilings** element, BimGo takes that ceiling's **bottom elevation at the
     aimed point**: a short upward ray against that element only, from just below the hit, so aiming at the top
     face from above (X-ray / section) still finds the underside. Linked ceilings count (they are in the BVH).
  2. *Preview:* the held diffuser snaps flush under that surface and follows it as you aim; the readout shows
     "Ceiling bottom 2,700 mm (Level 1 + 2,700)". Gizmo vertical moves still work and drop the snap until the
     next re-aim; **F** re-seats it to the ceiling above (instead of "drop to surface").
  3. *Face flush:* the family's insertion point isn't always its face. The library template's bounds give the
     offset between origin and lowest point; BimGo places so the **face** sits on the ceiling bottom.
  4. *In Revit:* level-based families → **Level** = the level the ceiling is on (else the nearest level at or
     below), **Offset from Level** = ceiling bottom − level elevation (written as the offset parameter, not just a
     move, so schedules read it). Work-plane / face-based families → the level's plane with the same offset
     (today's `FamilyPlacer` path), so they stay off the ceiling too.
  5. *No ceiling aimed* (open to structure, bulkhead, soffit): the underside of whatever surface is aimed at
     (slab, deck, bulkhead), else a default mounting height (setting, 2,700 mm).
  6. *Sloped ceilings:* an unhosted diffuser stays level at the aimed point's height; slopes over 2° show a note
     suggesting hosted mode, which follows the face.

  **Hosted to ceiling face (option).**
  - Face-based families: `NewFamilyInstance` on the ceiling's bottom face reference, or a linked ceiling's
    (`Reference.CreateLinkReference`). Follows the ceiling up / down and on slopes.
  - Ceiling-hosted families (`OneLevelBasedHosted`, the "Ceiling hosted" template): on the ceiling element, same
    model only (Revit can't host these on a link: refused with a toast). Today `CanPlace` refuses them;
    this mode adds them.
  - Families that can't be hosted (level-based) fall back to unhosted, with a toast saying so.
  - A hosted family type picked in **unhosted** mode: face-based ones go on the level's plane (step 4);
    ceiling-hosted ones can't be unhosted, so they ask to switch to hosted mode.
  **Free at elevation (option, open ceilings).** For diffusers hung off the duct with no ceiling.
  - Unhosted, like the default, but the height is a **target elevation**: typed in the airflow panel as a height
    above the level (e.g. Level 1 + 3,200), kept between placements. The aim gives only the plan position.
  - **Snap under duct:** aiming at a duct puts the diffuser under its centreline at the duct's bottom minus a drop
    (setting, default 0 = tight to the duct); otherwise the typed elevation is used.
  - The preview shows a vertical line to the floor and the clear height to anything above (the plenum check),
    and the trace treats it as a free jet (§3 step 0).
  - It is placed unconnected; connecting it to the duct stays a Revit job.

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
  - **Re-seat:** a flagged unhosted diffuser has a *Re-seat to ceiling* fix (one, or all in the list): it sets the
    offset to the new ceiling bottom through the existing `Transform` edit (vertical move only), so it works live,
    in the journal and on push with no new edit op.
- **Snap:** an optional 600 mm / 24 in ceiling-grid snap for the Place / Gizmo gun when holding a terminal.
- **Flow on a new terminal:** the family's default; it isn't connected to ductwork, so the readout marks it
  *unconnected* and the user sets CFM with `SetFlow`. Connecting ducts stays a Revit job.

### 5.4 Moving connected diffusers (decided 2026-10-10: keep today's behaviour)

**The problem.** A diffuser connected to ductwork drags the connected duct when it moves. Moved up, the drop
above it shortens; past the fittings' minimum length Revit can't keep the run and wants to **delete** the duct or
break the connection.

**The decision.** Keep `RevitEditor.RunTransaction` / `SwallowFailures` as they are: **any Revit error rolls the
whole move back**, so Revit never reaches its "delete elements" resolution and nothing is deleted as a side
effect. The move is refused with Revit's own message as a toast. This already applies to Gizmo moves, *Re-seat to
ceiling* and pushed `transform` entries (a refused push entry shows as a conflict, and the push preview lists it
first).

**Small additions that don't change that behaviour:**
- **Re-seat all** moves each diffuser in its own transaction, so one refusal doesn't stop the rest; the skipped
  ones are listed with Revit's reason.
- The Scan readout marks a diffuser **connected** / **unconnected** (from its connector), so the user knows a
  move may drag ductwork.

**Left for later (not in this plan):** a BimGo-side length check before sending, an in-transaction connection
check, and surfacing Revit warnings (today they're deleted silently). Revisit if users hit warning-only cases,
e.g. a connection that breaks without an error.

---

## 6. Build rounds

| Round | Scope | Done when |
|---|---|---|
| **AF1: data + readout** | `Units` (IP / SI), `AirTerminals`, `DiffuserCatalog` (built-in generic, CSV / JSON import, fit, interpolation), `air.json` read / write, `SceneExtractor.Air.cs`, Scan gun readout + throw graph, ceiling envelopes (straight runs, no events) | A real model's diffusers show catalog throw envelopes and correct readouts; Core tests green |
| **AF2: trace + findings** | `AirflowTrace` (walls, obstructions, collisions, falling jets, occupied zone presets (62.1 breathing zone default, 55, custom) + exterior wall detection (rays, glazing, override), EDT draft check with a Cooling / Heating toggle (global heating share, defaults; advanced temperatures) + simple mode, T50 / L), paths and markers, findings panel → comments / BCF, settings | Draft risks found in a test room match a hand calculation from the catalog table |
| **AF3: edits** | `SetFlow` op (live, journal, undo / redo, push + conflicts), CFM editor with live preview, Air Terminals in the family library, ceiling X-ray + plenum check, unhosted-at-ceiling placement (default) + hosted and free-at-elevation options, Re-seat all + connected / unconnected readout (§5.4), BimGo diffuser families + Diffusers tab in the Place gun + `family.load` (live) / load-on-push (file) + built-in templates + ribbon **Load diffusers…** + family checker (§2.5), orphan / off-ceiling flags + re-seat, grid snap | CFM changed in BimGo appears in Revit's schedule; a diffuser placed in BimGo shows its envelope at once |
| **AF4: extras** (optional) | Smoke particles, occupied-zone heatmap, cold-jet separation (Archimedes number, same temperatures), OpenFOAM export | Chosen with the user after AF3 |

Fit with the main plan: after Rounds A and B (presentation, review tools) or between them; the user decides.
AF1 depends on nothing; AF3's CFM display benefits from Round B's unit setting.

---

## 7. Files

| Project | File | Notes |
|---|---|---|
| Core | `Scene/Units.cs` | IP / SI factors, formatting, standard envelope velocity sets |
| Core | `Scene/AirTerminals.cs` | Terminal records, roles, patterns |
| Core | `Scene/DiffuserCatalog.cs` | Schema, built-in generic family, CSV / JSON import, fit, interpolation |
| Core | `Scene/AirflowTrace.cs` | Trace + events over an injected geometry query |
| Core | `Format/…` | `air.json` entry in reader / writer; `setflow` in `EditJournal` |
| Core | `Edits/EditMessages.cs` | `EditOp.SetFlow`, optional `Value` |
| Core | `Scene/LaunchSettings.cs` | Airflow settings (mode, supply / room temperatures, EDT or simple limits, zone preset, band factor, units), sanitised |
| Revit | `Extraction/SceneExtractor.Air.cs` | Air Terminals: position, normal, sizes, flow, role, pattern, catalog match |
| Revit | `Extraction/FamilyCheck.cs` | Rates air terminal families against the spec or a profile (§2.5) |
| Core | `Scene/DiffuserProfiles.cs` + built-in `DiffuserProfiles.json` | Parameter / origin / pattern / catalog mappings for known families (Autodesk defaults, firm families) |
| Revit | `Commands/Cmds_BimGo.cs` | `Cmd_LoadDiffusers` (ribbon button, tooltip, icon) |
| Revit | `Extraction/FamilyLoader.cs` | Loads BimGo family types (ribbon, `family.load`, push), keeps existing types, then the template pass for the new types |
| Revit | `Live/LiveDispatcher.cs` | `family.load` → `family.load.result` |
| Core | `Live/LiveProtocol.cs` | `MessageTypes.FAMILY_LOAD` / `FAMILY_LOAD_RESULT` (additive) |
| App | `Rendering/DiffuserMeshes.cs` | Built-in template geometry for BimGo types (file mode) |
| App | `Game/GameSession.Library.cs` | Diffusers tab, loaded / not loaded, load requests |
| Revit | `Families/*.rfa` | BimGo diffuser families (new folder: needs Gavin's yes) |
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
- Unhosted placement: the right "Offset from Level" parameter on level-based air terminals
  (`INSTANCE_FREE_HOST_OFFSET_PARAM` vs `INSTANCE_ELEVATION_PARAM` across 2025–2027), and that setting it moves
  the instance as expected; the offset parameter for work-plane-based instances on a level plane.
- `NewFamilyInstance(XYZ, FamilySymbol, Element host, Level, StructuralType)` for ceiling-hosted
  (`OneLevelBasedHosted`) families on a same-model ceiling.
- Units: `UnitTypeId.LitersPerSecond` / `CubicFeetPerMinute` conversion helpers on 2025–2027.

- Moving a connected air terminal with `ElementTransformUtils.MoveElement`: connected ducts follow as in the UI,
  and a drop made too short raises an **error** (rolled back), not only a warning.
- `doc.LoadFamily` of a Revit 2025 `.rfa` in 2026 / 2027 (upgrade on load); keeping existing types
  (`IFamilyLoadOptions` returning false for overwrite).
- Reading a connector's associated family parameter (flow, diameter) for the checker.
- `Document.LoadFamilySymbol` for single types vs `LoadFamily` (which loads every type); loading during a live
  session through the `ExternalEvent`, then the template pass for just the new types.
- Parameter names, origins and placement types of the Autodesk default diffusers chosen, for their profiles;
  the wall type *Function* parameter (`FUNCTION_PARAM`), tie-break only; doors / windows' host walls for glazing.
- `doc.GetUnits().GetFormatOptions(SpecTypeId.AirFlow / SpecTypeId.Length).GetUnitTypeId()` on 2025–2027.

## 9. Questions to stage (AskUserQuestion, recommendation first)

1. **Return / exhaust grilles:** show them (capture zone only, no throw) or ignore in AF1?
2. **Linear slot diffusers and sidewall grilles:** in scope for the catalog schema now (recommended: schema yes,
   built-in data no), or square ceiling diffusers only?
3. **Placement order:** AF1–AF3 after main Round A / B, or before?
4. **Real catalog data:** one or two submittal tables (kept out of the repo) to check the fit against.
5. **`SetType` (change neck / face size from BimGo):** include in AF3, or later?
6. **Ceiling X-ray:** see-through (recommended) or hidden by default; on automatically with a terminal held?
7. **Free-jet throw correction** for exposed diffusers: 0.7 by default, or per catalog family only?
8. **Autodesk content terms:** who checks them (user / Gavin), and if redistribution isn't allowed, ship profiles
   only (recommended) or also author from-scratch BimGo families?
