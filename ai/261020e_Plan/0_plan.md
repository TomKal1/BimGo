# Round Y plan (agreed 2026-10-20, after X-E confirmed at r3)

Round X is done (X-A … X-E). Gavin's answers to the planning questions:

| Question | Answer |
|---|---|
| Working copy | The X-E r3 zip is current |
| Which planned groups next | Lighting + entourage polish; presentation; occlusion culling / LOD ("interesting"). Not now: review tools group, IFC import |
| Multiplayer | Park it (revisit after VR; a LAN spectator mode is the light version) |
| Standing limitations to fix | UI shell leftovers; review tool gaps. The rest stay listed |
| New ideas | Comments → Revit; room data overlay |
| Order | Fix-ups → lighting / entourage → LOD → presentation (recommended) |
| Presentation parts | Kiosk launch; live sharp reflections retry. Not VR (later) |
| Comments in Revit become | A marker in the model; **Gavin authors the .rfa**; BimGo loads and places it |
| Saved measurements | Model folder (`measurements.json`, embedded in a .bimgo on save), show / hide toggle, delete, in the CSV |
| Clearance in any direction | Along the aimed surface's normal (walls: horizontal; floors / ceilings: vertical as now) |
| Lighting / entourage items | Lighting set (Revit spot / hemispherical as profiles, IES tilt, per-instance profiles, previews) and smarter live lights (rank by contribution). Not the entourage set or billboards in probes (later) |

## Rounds, in order
1. **Y-A fix-ups, review tools:** measurements saved with show / hide, clearance along the surface normal, comment
   pictures mirrored beside the model when sharing is on.
2. **Y-B fix-ups, UI shell:** photo mode's panel, PUSH TO REVIT and the markup editor on the panel shell; an open
   dropdown traps the focus ring; K shown beside TOOLS ▾ → PICK POINTS.
3. **Lighting polish** (X-D1 "not done" list): Revit spot / hemispherical distributions as generated profiles, IES tilt
   data, per-instance profile assignment, profile preview pictures; live light picking ranked by contribution.
4. **Scale:** occlusion culling and LOD (questions at the round: GPU occlusion queries vs a software depth pre-pass,
   LOD by simplified meshes vs impostors, model size targets).
5. **Revit round-trip:** comments → Revit markers (Gavin's family: name, parameters for id / text / status / author to
   agree when he has it), room data overlay (floating tags, picked parameters, target checks; details at the round).
   Needs Gavin's .rfa first.
6. **Presentation:** kiosk launch (locked demo mode: fixed start, no edits / menus, idle reset to a tour, a launch
   shortcut), live sharp reflections retry (X-C1's notes).
7. **Installer** (Inno Setup per user; `ai/261009e_Installer`).

Added after the plan (Gavin, 2026-10-20): **async sharing through cloud folders** (Dropbox / OneDrive / Google Drive /
network drives), a round after the Revit round-trip (exact place to settle then): each person writes only their own
files (merged on read; fixes today's newest-file-wins overwrite of shared comments), each change its own small file,
a poll every few seconds with "Sam added 2 comments" toasts, care for cloud-only placeholders and rename-heavy sync
clients. Async only (sync delays rule out live presence).

Later: OpenXR VR (needs a package: Gavin's yes); review tools (find element, accessibility checks, travel tape, profile
schedule CSV); IFC import; entourage set (wind sway, fixed-heading cars, planting undo, billboards in probes).
Parked: real-time multiplayer. Gavin expects it to be web based in the end: a hosted service (servers, accounts,
uptime, cost, models leaving the office), not a hand-over tool. Bandwidth isn't the issue (each client has the model;
poses / edits are bytes; latency matters more). Lighter steps if wanted first: one BimGo hosting on the office network,
a spectator mode.
