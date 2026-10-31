# Sound in neighbouring rooms from STC ratings (plan addition)

This follows `0_plan.md` and `1_market_research.md`, and was requested on 2026-10-31.
- **Input:** the user enters STC ratings (or Rw, or octave-band TL) for the types of walls, doors, windows, ceilings,
  floors and slabs.
- **Output:** BimGo calculates the sound level that equipment in one room produces in the rooms around it, and shows
  which element lets the most sound through.
- **Status:** not built; nothing is compiled. The Revit API calls named below are ones to verify, as usual for a
  plan round.

## The calculation

All values are per octave band (63 Hz – 8 kHz), then A-weighted and rated as NC / NR, as in `0_plan.md`.

**1. From STC to a TL spectrum.** STC (ASTM E413) and Rw (ISO 717-1) are single numbers fitted to the same reference
contour shape. Without measured data, a construction's TL is taken as **the rating + the contour**, averaged into
octaves:

| Octave (Hz) | 63 | 125 | 250 | 500 | 1k | 2k | 4k | 8k |
|---|---|---|---|---|---|---|---|---|
| TL − STC (dB) | −20 * | −14 | −7 | 0 | +3 | +4 | +4 | +4 * |

\* The contour has no values below 125 Hz or above 4 kHz. 63 Hz is extrapolated at about −6 dB per octave and
8 kHz is held at the 4 kHz value. Both are flagged as estimates.

**Why the input should allow octave TL too:** pump and fan noise is low-frequency heavy, and STC says little below
125 Hz. Each construction can therefore also take:
- **octave TL** from the manufacturer or a test report, preferred whenever known;
- **Rw + Ctr**, which shifts the low end.

**2. Composite partitions.** A wall with a door and a glazed panel transmits through each piece in proportion to its
area. With τ = 10^(−TL/10):

TL_composite = −10 log₁₀( Σ Sᵢ τᵢ / Σ Sᵢ )

The weak element dominates. For a 32 m² wall at STC 50 with a 2 m² door at STC 30:
- **Wall alone:** TL 50.
- **With the door:** TL ≈ 41. The door carries **87 %** of the transmitted energy.
- **With a 10 mm × 0.9 m door undercut** (τ = 1): TL ≈ 34.5. The gap alone carries **80 %**.

Doors therefore take an optional **gap / undercut** input.

**3. Source room → receiving room.**
- **The diffuse approximation:** Lp₂ = Lp₁ − TL + 10 log₁₀(S / A₂). Here S is the shared area and A₂ is the
  receiving room's absorption (m² sabins).
- **What BimGo does instead,** with the source room solved already (AC-C / AC-D):
  - It works in **power**, piece by piece. W_incident on each partition piece = the reverberant part (p²·S / 4ρc) +
    the direct part from each source (∫ I cos θ dS, sampled over the piece and blocked by line of sight).
  - So a pump right against a wall loads that wall more than one across the room. The diffuse formula cannot show this.
  - Transmitted power is W_t = τ · W_incident.
- **The receiving room:**
  - Each piece then radiates W_t into the receiving room as a small area source: reverberant Lw_t + 10 log(4 / R₂),
    plus a near field close to the piece.
  - The map shows the level rising towards the leaky door.
  - With ray tracing (AC-D), the transmitted pieces simply become sources in the receiving room's trace.
- **Example:** 80 dB at 500 Hz in a plant room, behind that wall with its door, into an 8 × 4 × 2.7 m office with
  A₂ ≈ 22 m²:
  - wall alone: ≈ 32 dB;
  - with the door: ≈ 40 dB;
  - with the undercut: ≈ 47 dB.
- **Hops:** one hop by default (source room → each neighbour). A second hop (A → B → C) is optional; it rarely
  matters next to the first.

**4. Lab vs field.** STC and Rw are laboratory ratings. Built walls usually perform worse through flanking and
workmanship (the field figures are ASTC / FSTC / R′w). A **field allowance** (dB subtracted from every rating) is a
calculation setting. Its default is a decision for the user (see below). The value is shown on every result.

**5. Receiving room absorption.** A₂ comes from the receiving room's own surfaces through the same absorption lookup
as the source room (`0_plan.md`). Furniture and people are usually missing, so a room can instead take an **assumed
RT** (A₂ = 0.161 V / T), for example 0.6 s for a furnished office. The result says which was used.

**6. Background noise (optional):** a room's background level (for example its HVAC NC). The result then reads
"intrusion vs background" as well as the absolute level.

## Which paths, by element

| Element | Rating used | Path |
|---|---|---|
| Wall | Wall type STC / Rw / TL, less any hosted openings | Side by side |
| Door, window (interior or a borrowed light) | Their type's rating; door gap optional | Through the wall they sit in. Their area is subtracted from that wall. |
| Floor / slab (incl. roof over a room) | **Assembly** rating on the floor type: floor-ceiling assemblies are rated as a whole | Vertical: plant room over an office, or the reverse |
| Ceiling (suspended) | **CAC** (ASTM E1414) on the ceiling type, not STC | **Plenum flanking:** a wall that stops at the ceiling lets sound over it, through the tiles of both rooms |
| Exterior walls / windows (OITC) | — | Later: transmission out to the site (the outdoor noise map in `1_market_research.md`) |

**Plenum detection:** when the shared wall's top is below the structure above, the path is flagged "wall stops at
ceiling: plenum path". It then uses the two rooms' ceiling CAC, as a normalised level difference. The exact
normalisation must be checked against ASTM E1414 before it is built. When the wall reaches the slab, only the
wall path is used.

**Paths not covered,** stated on every result:
- **Structure-borne sound and vibration:** pumps on a slab. Often the dominant low-frequency path for pumps.
  Inertia bases and isolators are not modelled.
- **Duct crosstalk.**
- **Pipe penetrations.**
- **Flanking through continuous slabs and facades** beyond the field allowance.

## Finding the partitions in the model

The calculation needs, for every pair of rooms, the pieces that separate them: element, type, area and kind.

**Primary: at extraction (BimGo for Revit).** This is reliable, and it uses Revit's own room geometry:
- **Room faces:**
  - `SpatialElementGeometryCalculator` (with the same `SpatialElementBoundaryOptions` as `BuildRoom`):
    `CalculateSpatialElementGeometry(room)` → each face's `GetBoundaryFaceInfo` → subfaces.
  - Each subface has its bounding element (`SpatialBoundaryElement`), its area, and side / top / bottom.
  - The room on the other side: step across the bounding element (`Wall.Width` along the face normal, plus a
    margin) and call `Document.GetRoomAtPoint(point, phase)`.
  - For tops and bottoms, probe up to about 1.5 m past the slab, because a room's upper limit is often the ceiling
    and the plenum is not a room.
- **Doors and windows:** `FamilyInstance.get_FromRoom(phase)` / `get_ToRoom(phase)` give the two rooms directly.
  - Area: from width × height, or the instance's extent in its host's plane.
  - That area is subtracted from the host wall's subface between the same rooms.
- **Links:** linked rooms (BimGo already takes rooms from links) are calculated in their own document, with their
  link transform, as `BuildRoom` does.
- **Spaces:** MEP models often have Spaces rather than Rooms, and BimGo extracts Rooms only. Spaces have the same
  calculator API, so add them if TomKal1's MEP models need them.
- **Saving it:** a new optional part, `room-adjacency.json`. This is an additive part of format 1, in the `.bimgo` and
  in live snapshots. For each room pair it lists pieces: element UniqueId, link, category, type name, kind, area (m²),
  centroid, normal and the host wall.
- **Unknown neighbours:** faces with nothing on the other side are kept as exterior or unknown.

**Fallback: older files.** For `.bimgo` files without the part, the pieces are derived from the exported triangles:
- For each boundary triangle of a room, step through its element along the normal.
- Test `RoomInfo.Contains` plus the Z range for the room on the other side.
- This is less exact: doors are found as elements whose triangles touch both rooms, and openings are not subtracted.
  The result says it used the fallback.

## Entering STC

- **From the model:** a configurable list of type parameter names ("STC", "STC Rating", "Acoustic Rating", "Rw",
  "CAC"). The first number in the value is taken, so "STC 50" reads as 50. Extraction reads these names whenever
  they are set, without the user having to tick them under extra parameters.
- **In BimGo:** Esc → ACOUSTICS → **CONSTRUCTIONS**:
  - **Rows:** every type found on a partition of the chosen rooms. Columns: category, type, count, total area,
    rating kind (STC / Rw / octave TL / CAC), value, where it came from (parameter, override, or **missing** in red),
    and the door gap.
  - **Saving:** edits go to `acoustic-constructions.json`: per type, keyed by link + category + type name. The file
    sits in the model folder and in the `.bimgo`, beside `acoustic-materials.json`.
  - **In live sessions:** edits could later be written back to the Revit type parameter through the journal, the way
    edits are pushed now.
- **Missing ratings are never guessed.** A path with an unrated piece is left out and listed as a warning ("Door type
  D2: no rating, 3 doors not counted").

## Results

- **Room table:**
  - the source room level (dB(A), NC / NR);
  - for each neighbour: the level, the target (e.g. NC 35 for an office), the margin and PASS / FAIL;
  - the composite TL of the shared boundary;
  - **the dominant path with its share** (e.g. "door 1.07: 87 %").
- **Map:** the receiving rooms are drawn on the same heat map as the source room, so the levels can be seen across
  walls.
- **Weak links in 3D:** partition pieces are coloured by their share of the transmitted energy (overlay buffer, as
  `CalcOverlay`). The undercut door glows red. The crosshair probe on a wall or door shows its TL, area, incident level
  and share.
- **What-ifs:** changing a type's STC recomputes the statistical result at once (no re-trace). For example, "STC 30 →
  45 door: office 40 → 33 dB(A)".
- **Exports:** CSV of paths per room pair; the report lists the assumptions (contour-derived TL, field allowance,
  assumed RTs, uncovered paths).

## Builds

These fit into `0_plan.md` as **AC-T**, after the first map (AC-C) and before ray tracing. The statistical method
only needs the source room's levels at the partitions.

1. **AC-T1, extraction (BimGo.Revit):**
   - room faces and their bounding elements, the room across, doors / windows, openings subtracted;
   - `room-adjacency.json`; the triangle fallback in Core.
   - **To verify on Windows:** the calculator APIs above on linked rooms, curtain walls, room separation lines (no
     element: treated as open), and phases.
2. **AC-T2, Core maths and data:**
   - STC / Rw → octave TL, composite TL, door gaps;
   - incident power per piece, transmitted power, receiving room level (Sabine / Eyring or assumed RT), field allowance;
   - `acoustic-constructions.json` and parameter parsing.
   - Tests:
     - composite TL (the door and undercut examples above);
     - Lp₂ against the hand formula;
     - the energy share sums to 100 %;
     - "STC 50" / "50" / "Rw 52 dB" parsing;
     - a missing rating leaves the path out.
3. **AC-T3, App:**
   - the CONSTRUCTIONS tab;
   - the room table with dominant paths;
   - neighbour rooms on the map, weak-link colouring, the probe on partitions;
   - CSV export.
4. **With AC-D (ray tracing):** incident power comes from traced hits per piece. Transmitted pieces emit into the
   receiving room's trace.

## Decisions needed

1. **Field allowance default:** 0 dB (pure laboratory STC, the warning only), or a deduction such as 5 dB, shown on
   every result.
2. **Rooms only, or MEP Spaces too** (the host MEP model may have Spaces and no Rooms).
3. **The parameter names** your Revit types use for STC / Rw / CAC today, if any.
4. **Ratings in live sessions:** BimGo-only, or written back to the Revit type parameter.
