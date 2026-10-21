# X-D1: IES light profiles

From the X-D handoff (`ai/261020a_Handoff/0_handoff.md` §3) and Gavin's answers:

| Question | Answer |
|---|---|
| Assignment (2026-10-19) | Per fixture type: read each type's IES from Revit; in the app, replace or assign one per type |
| No / missing IES (2026-10-19) | Today's lobe stays the default; ship a few generic profiles made by BimGo (downlight, wide flood, wall washer, pendant up / down) |
| Where the per-type list lives (2026-10-20) | Pause menu, a LIGHTS tab (recommended) |
| Brightness with a profile (2026-10-20) | Revit's lumens; the IES only shapes the light (recommended). The file's own output only when Revit's is missing |
| Split (2026-10-20) | One X-D1 build, everything (recommended): Gavin away ~40 min |

Gavin also: if IES needs third-party code or data, note it and add it to the notices (it doesn't: own parser, own
generated profiles; noted under "Not shipped with BimGo").
