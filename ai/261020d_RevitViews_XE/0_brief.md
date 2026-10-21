# X-E: two-way Revit camera sync (live sessions)

From the X-E handoff (`ai/261020c_Handoff/0_handoff.md` §3) and Gavin's answers (2026-10-20):

| Question | Answer |
|---|---|
| Working copy changed since the handoff zip? | No: build on the handoff zip (r8b + Gavin's compile fixes) |
| Default direction | Off until toggled (recommended) |
| What the Revit view matches | Perspective + FOV (recommended); no section box |
| Where the toggle lives | Pause menu + a key (recommended), **and a Revit ribbon button too** |
| Revit → BimGo movement | Fly while following (recommended); walk comes back when sync stops following |
| Throttle | 4 Hz, only on change (recommended) |

Scope: additive messages both ways (`view.sync`, `view.camera`); BimGo → Revit updates a dedicated perspective
"BimGo view" (the C-B2 "BimGo clash" view is the pattern) on the ExternalEvent; Revit → BimGo follows the active 3D
view's `ViewOrientation3D` (feet ↔ Revit internal metres; scene-local via `Scene.OriginOffset`); a direction cycle
(off / BimGo → Revit / Revit → BimGo / both) and a loop guard. README §7 lists the protocol.

## r2 (2026-10-20): live sync dropped, views on demand
After r1 Gavin: "the undo stack is brutal" (every BimGo → Revit move was a `BimGo: camera view` undo entry). Instead:
a "create view from BimGo" button in BimGo and in Revit, plus (optionally) an update-view tool. Answers:

| Question | Answer |
|---|---|
| Working copy changed since r1? | No: build on the r1 zip |
| Keep Revit → BimGo following (read-only in Revit)? | **Drop all live sync**: only the view buttons |
| Each "create view" | A new perspective view each time, "BimGo 01", "BimGo 02"… (recommended) |
| Open it in Revit? | Yes, make it active (recommended) |
| Update view (Gavin, mid-build) | Also: puts BimGo's camera on Revit's active perspective 3D view |

## r3 (2026-10-20)
Update view key moved from Ctrl+F5 to **Alt+F5** (Ctrl is crouch, so holding it to update ducked the camera). Alt+key reaches the input (WM_SYSKEYDOWN) and the Alt menu is already swallowed.
