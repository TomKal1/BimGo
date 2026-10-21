# Round X, build X-A: sun time-lapse and batch stills (2026-10-19)

Built on the Round X handoff (W-C confirmed). **Not compiled here** (no .NET SDK in the sandbox): build first, then the
Core tests (+8 in the new `TimeLapseTests`). Checked: brackets / strings / char literals in every changed `.cs`, encodings
kept (BOMs where they were, new files LF without), new member names across the `GameSession` partials, UI strings against
the font atlas (`→ ‹ › − × ·` only). Sunrise / sunset was ported to Python and checked for Sydney (21 Jun ≈ 07:00 /
16:54; 21 Dec on DST ≈ 05:41 / 20:05) and Tromsø (no range at either solstice). No shader changes, so no test renders.
**No new keys.**

## In

| Item | What it does | Where |
|---|---|---|
| **Sun time-lapse** | MEDIA → VIDEO → **Sun time-lapse** (the first source) → **RECORD TIME-LAPSE**. The camera holds one view while the sun crosses the sky: shadows, sky colour, the moon and the turning stars follow the clock. Recorded off-screen like the walkthrough video (size / rate as set, progress, Esc / CANCEL deletes the partial file); saved as `Videos\BimGo\<model> time-lapse <view> <date time>.mp4`. | `GameSession.TimeLapse` (`StartTimeLapse`, `ApplyTimeLapseSun`); Core `FixedViewTimeline` |
| **View** | ‹ › steps through **Where you are** (the camera when you pressed RECORD) and each bookmark (its view, eye height, cut, hidden items and colour profile, as the tour shows it). | `BuildTimeLapseSettings` |
| **Range** | **Sunrise → sunset** with 0–120 min **either side** (15-min steps, 30 by default), or **Custom** From / To (15-min steps); a To at or before From runs into the next day (shown "+1"): a night of stars. The date: the bookmark's sun if it keeps one, else the sun panel's. Where the sun doesn't rise or set (polar day / night) it says so and RECORD is greyed: pick Custom. | Core `SunEvents.Find` (centre at −0.833°), `TimeLapsePlan` |
| **Length** | 5–120 s (5-s steps, 20 by default). The summary shows the date and range, size · fps · length · frames, sunrise / sunset and how many minutes of sun pass per second. | `TimeLapsePlan.Frames`, `MinutesPerSecond` |
| **Smooth sun** | Each frame sets the date and a fractional clock time (the next day after midnight), so the shadows creep rather than tick. | `TimeLapsePlan.At` → `_sunBlendMinutes` |
| **Clouds, water, weather** | Clouds drift 8× their speed (a time-lapse look); water ripples at the video's pace. Rain, snow and lightning run on video time, so lightning keeps its real pace in the output (never more than 3 flashes a second). | `RenderVideoFrame`, `TIMELAPSE_CLOUD_RATE` |
| **Reflections** | While recording, the probes re-bake continuously (two cube faces per frame, nearest first; a new pass starts as each ends), so glass and water reflect the dusk sky rather than the noon one. | `RefreshTimeLapseProbes` |
| **Every video** | Clouds, water and the stars' twinkle now move by the video frame's step, not the window's (they used to jump when several video frames were rendered per window frame). | `Run` loop `worldDt`; `AdvanceSky` time |
| **Batch stills** | MEDIA → PHOTO → **BATCH STILLS**: **All bookmarks** or **Tour stops** × a **time list** (**EDIT…**: `9:00 12:00 15:00`; also `0930`, `3pm`; a date such as `21/6` shoots the times after it on that day: `9:00 12:00 21/12 9:00 12:00`). The list is checked when you press Enter (a bad entry keeps the old list and says why) and shown as read. The summary shows views × times = shots. Footer **RENDER BATCH STILLS**. | `GameSession.BatchStills`; Core `BatchStills.Parse` / `Format` |
| **The run** | Plays the views through the tour player like PHOTOS OF ALL KEYS (UI hidden, progress bar with the view, date and time; Esc stops): for each view, for each time, the sun is set (shadows on), the reflection probes re-bake when they show ("REFLECTIONS…", up to 15 s), 3 frames settle, then one shot with the PHOTO settings (still or 360°, size, format, exposure, grid lines). The walkthrough comes back as it was. | `UpdateStillsBatch` |
| **Files** | `Pictures\BimGo\<model> batch <date time>\01 <view> 21 Jun 0900.png` (the view's number first, so they sort by view then time) and **index.csv** (file, view, date, time, daylight saving, sun altitude, sun azimuth from true north; UTF-8 with BOM for Excel). | `BatchStills.ShotName`, `CsvRow`, `Csv` |
| **Stamp** (off by default) | The time large (with DST when on), the date and the place, in a dark box bottom right (the colour legend stays bottom left). Time-lapse: tick under VIDEO; batch stills: tick under BATCH STILLS (stills only, not 360s). Scaled with the picture. | `QueueSunStamp`; `BuildVideoStamp`; `RenderPhotoView` |

Settings (per machine, settings.json, additive): `TimeLapseCustom` (false), `TimeLapseFrom` / `TimeLapseTo` (360 / 1200),
`TimeLapseMargin` (30), `TimeLapseSeconds` (20), `TimeLapseStamp` (false), `BatchTimes` ("9:00 12:00 15:00"),
`BatchTourStops` (false), `BatchStamp` (false). The time-lapse view is per session. Nothing new in the `.bimgo`.

### Tests (+8, `TimeLapseTests`)
`SunEvents_SydneyWinterAndSummer`, `SunEvents_PolarDayAndNight_NoRange`, `Plan_SunriseToSunset_WithMargin_FramesSpreadEvenly`,
`Plan_CustomOvernight_RunsIntoTheNextDay`, `FixedViewTimeline_HoldsOneViewWithoutFades`, `BatchTimes_ParseTimesDatesAndErrors`,
`BatchStills_NamesAndCsvIndex`, `Settings_DefaultsCleanAndOlderFiles`.

## How to check it
1. **Build**, run the Core tests.
2. **Time-lapse, where you are:** stand outside facing the building (shadows don't need to be on). Shift+M → VIDEO →
   click **Sun time-lapse**. The right side shows View "Where you are", Sunrise → sunset, 30 min, 20 s and the summary
   (date · range, 1920 × 1080 · 30 fps · 0:20 · 600 frames, sunrise / sunset). **RECORD TIME-LAPSE**: the progress panel
   says RECORDING TIME-LAPSE with the range. Play the MP4: the view holds still, the sky goes from dawn to dusk, shadows
   swing round smoothly, clouds race. Afterwards: back where you were with your own sun time.
3. **From a bookmark:** ‹ › to a bookmark that keeps a sun (its date is used: the note says so) and one with a cut or
   hidden items (they show in the video).
4. **Custom overnight:** Custom, From 20:00, To 06:00 (+1), stars on (NIGHT tab). The video runs through the night: stars
   wheel, the moon crosses, the date turns over at midnight (tick the stamp to see it).
5. **Stamp:** tick it, record 5 s: the time / date / place box bottom right, sharp at 1080p and 4K.
6. **Reflections:** Realistic with probes on, a glazed façade: the reflections follow the light (a step behind) rather
   than staying at noon.
7. **Polar check (optional):** sun panel → CHANGE… → Tromsø, 21 Jun: the summary says the sun doesn't set and RECORD is
   greyed; Custom works.
8. **Any video:** record a short tour with clouds on: clouds and water move smoothly (no stutter).
9. **Batch stills:** Shift+M → PHOTO. BATCH STILLS: All bookmarks; EDIT… → `9:00 12:00 15:00 21/12 9:00` → Enter (shown
   as read; try `25:00` to see the error and the old list kept). Summary: n views × 4 times. **RENDER BATCH STILLS**: the
   bar steps through each view and time ("REFLECTIONS…" while probes re-bake), Esc stops. The toast names the folder;
   OPEN FOLDER: `01 <view> 21 Jun 0900.png` … and `index.csv` (open in Excel: one row per shot with the sun's position).
10. **Tour stops / 360 / stamp:** Tour stops (only ticked stops), 360° kind (JPEGs, no stamp), a still with the stamp
    ticked (bottom right; with a colour profile legend ticked it stays bottom left).
11. **Settings kept:** close and reopen BimGo: the range, length, stamp, time list and views choice are as left.

## Not in
- A time-lapse along a camera path (the path player already animates the sun per key), several dates side by side, a PNG
  sequence, a sun-path dial, a contact sheet; batch stills of comments or path keys (PHOTOS OF ALL KEYS stays for keys).

## Things to look at when building
- `StartVideo` is split: `VideoSizeFits` and `BeginVideoRecording(width, height, what, header, stamp)` (the time-lapse
  uses both); `_videoUi` is made when the HUD **or** the stamp needs it, and `BuildVideoHud` now checks `_tourRun.ShowUi`.
- `EndVideo` / `AbortVideo` call `EndTimeLapse()` (clears `_sunBlendMinutes`) before the tour puts the sun back.
- `ProbeTemplate()` was pulled out of `UpdateReflectionProbes` (shared with the time-lapse's re-bake).
- The batch's probe re-bake clears `_probeKeyChangedAt` so the usual 1-s settle doesn't start a second pass.
