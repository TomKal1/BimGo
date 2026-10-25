# Comment marker family: spec for Z-B

**Received 2026-10-22:** `src/BimGo.Revit/Resources/Families/BimGo_Comment_Marker.rfa` (Gavin): three status spheres sized by a *Radius*
family parameter (100 mm); no high-priority flag (BimGo_IsHighPriority stays as a parameter for Revit filters). All 18
shared parameter GUIDs present.

For Gavin to author. BimGo (Z-B) loads it if the model doesn't have it, then places, updates, moves and removes one
marker per comment, matching them by the **BimGo_Id** parameter.

## The family
- **Template:** `Generic Model Adaptive.rft`. **Category:** Generic Models, with a subcategory **BimGo Markers** on all
  the geometry (so V/G can switch markers off without touching other generic models).
- **Name:** family `BimGo Comment Marker` (file `BimGo_Comment_Marker.rfa`), one type `Default`. BimGo looks for the
  family by name and uses its first type.
- **One adaptive point** (placement point 1). BimGo sets its position to the comment's point (where you aimed: on a
  wall, a floor, a desk…). Orientation: *Global (Z) then Host (XY)*, so the marker stays upright whatever it sits on.
- **Geometry** (on the point's reference planes; sizes driven by a family length parameter **Marker Size**, instance,
  default 200 mm. It's a plain family parameter, not shared, and BimGo never sets it):
  - three spheres (or short cylinders) of diameter Marker Size centred on the point, one per status, each with its
    **Visible** tied to a yes/no below: *Open* (purple, `#A78BFA`), *In progress* (amber, `#FBBF24`), *Closed* (green,
    `#4ADE80`), the same colours as BimGo's markers. Materials fixed in the family (`BimGo Open`, `BimGo In progress`,
    `BimGo Closed`);
  - a small flag above (about Marker Size × 1.5 up a thin stem), red, **Visible** = BimGo_IsHighPriority.
- **Plans:** an adaptive family has no plan symbol of its own, so markers show in plans when they're inside the view
  range (generic models below the cut plane show in projection). If you want plan symbols later, a nested
  face-based / annotation trick is possible, but it isn't needed for Z-B.
- **Don't:** host it, make it shared, or give it a room calculation point.

## Shared parameters (instance)
Load them from `tools/revit/BimGo_SharedParameters.txt` (group *BimGo*), all **instance**; any parameter group in the
family (e.g. Identity Data / Other). BimGo finds them **by GUID**, so the names can be shown however you like, but keep
the GUIDs. "Editable" = USERMODIFIABLE in the file: all 1, since Revit's API can't set a parameter that isn't user-modifiable (r3).

| Parameter | Type | GUID | Editable | What BimGo writes |
|---|---|---|---|---|
| `BimGo_Id` | TEXT | `5d24ab4c-5c10-5023-bb69-b8526e44ab4a` | yes (BimGo overwrites it) | The BimGo comment id (32 hex). BimGo's key for updating / removing the marker: don't edit. |
| `BimGo_Title` | TEXT | `ead6278a-4ee3-5325-aebf-4484b31deb33` | yes | The comment's first line (60 characters at most), for tags and schedules. |
| `BimGo_Comment` | TEXT | `611d7d5a-1e4f-554b-a105-3808a805589d` | yes | The comment's full text. |
| `BimGo_Status` | TEXT | `a8f374e9-ec5c-5d0d-8e65-ca460e508fb1` | yes | Open / In progress / Closed (as BimGo shows it). |
| `BimGo_Priority` | TEXT | `97ce83ee-3d33-5c8e-8cf1-5ca61e26764b` | yes | Low / Normal / High. |
| `BimGo_Author` | TEXT | `feca2551-65bb-5dd0-aa5a-60bd9a0891a5` | yes | Who made the comment. |
| `BimGo_AssignedTo` | TEXT | `bc5284ca-081a-5a83-8056-9ff4235c6928` | yes | Who it is assigned to (empty: nobody). |
| `BimGo_Created` | TEXT | `78a872d8-7df7-5827-b781-ca0ada549931` | yes | When it was made (yyyy-MM-dd HH:mm). |
| `BimGo_Updated` | TEXT | `7ade62cf-73fd-56af-b96d-554b14bbddac` | yes | When it last changed in BimGo (yyyy-MM-dd HH:mm). |
| `BimGo_Level` | TEXT | `0332a4cf-cf80-54fe-8d15-cd7fb9b0eeff` | yes | The level it was made on (adaptive markers have no level of their own). |
| `BimGo_Element` | TEXT | `e2cab673-8034-5e19-96de-2ffc7394bd2a` | yes | The UniqueId of the element it was made on (empty: none). |
| `BimGo_Replies` | INTEGER | `e04836c9-9920-59d1-a45f-22c969c33500` | yes | Number of replies. |
| `BimGo_Picture` | TEXT | `34e1f0f6-a357-5d69-926d-2071803cfef1` | yes | The comment picture's file (JPEG beside the model), or empty. |
| `BimGo_Synced` | TEXT | `a3450b13-b57b-55d8-8b31-a06f983f7fa8` | yes (BimGo overwrites it) | When BimGo last wrote this marker (yyyy-MM-dd HH:mm). |
| `BimGo_IsOpen` | YESNO | `10bdc1c1-122b-5cbb-89e4-2582e186ef21` | yes (BimGo overwrites it) | Status is Open (drives the open solid's visibility). |
| `BimGo_IsInProgress` | YESNO | `ab02c1e9-3c4a-5a66-a3ba-9d3397e56a90` | yes (BimGo overwrites it) | Status is In progress (drives its solid's visibility). |
| `BimGo_IsClosed` | YESNO | `4dc55a39-a8fd-52cd-a3cf-c25a78dc55a9` | yes (BimGo overwrites it) | Status is Closed (drives its solid's visibility). |
| `BimGo_IsHighPriority` | YESNO | `b8562f32-3c77-5592-84b5-fc6b8ce7278f` | yes (BimGo overwrites it) | Priority is High (drives the flag's visibility). |

The four yes/no parameters exist only to drive visibility (one solid per status, the flag); the text ones are for tags,
schedules and filters (e.g. a view filter on BimGo_Status). Checked in Z-B: the API can't set a parameter that isn't
user-modifiable, so every one is USERMODIFIABLE 1 (edits made in Revit are overwritten at the next sync).

## What BimGo will do with it (Z-B; proposed defaults, to agree then)
- **Load:** if `BimGo Comment Marker` isn't in the model, load it from a path in the add-in's Options (default: beside
  the add-in, `BimGo_Comment_Marker.rfa`), else say so and skip.
- **Place:** `AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance`, then the placement point's
  `ReferencePoint.Position` = the comment's point in Revit internal coordinates. A moved comment moves its point; nothing
  is hosted, so nothing breaks when walls move.
- **Which comments:** all of them; closed ones stay (green) unless "Remove closed markers" is ticked.
- **Match:** by BimGo_Id. A comment deleted in BimGo removes its marker. A marker deleted in Revit isn't put back until
  "Re-place all" (so deleting one in Revit is a way to say "not here").
- **Direction:** BimGo → Revit only in Z-B (edits to the text parameters in Revit are overwritten on the next sync);
  reading status back from Revit could come later.
- **When:** live sessions through the protocol (one undo entry "BimGo: comment markers"); file mode on the next Revit
  session's sync (the add-in reads the model folder's comments).
- **Links:** comments on linked elements get their marker in the host model.
