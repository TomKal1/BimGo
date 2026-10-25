# Z-B build notes: comment markers in Revit

Not compiled here (no .NET SDK in the sandbox). Brackets, strings and char literals script-checked in every changed
`.cs` file; the XAML, .resx and .csproj parse as XML; new member names checked across the `GameSession` partials and
the Revit project's type names (no clashes like Z-A's `RoomLabel`). The parameter GUIDs in code are checked against
`tools/revit/BimGo_SharedParameters.txt` by a test. All 18 shared parameter GUIDs were found inside Gavin's .rfa.

## What's in
- **The family ships with the add-in:** `src/BimGo.Revit/Resources/Families/BimGo_Comment_Marker.rfa`, embedded in
  BimGo.Revit.dll. It's written to `%LocalAppData%\BimGo\Families` the first time a model needs it, then loaded.
  Options → Player → *Marker family* can point to another .rfa instead. In a model, the family is found by name
  ("BimGo Comment Marker") and its **Default** type is used (else its first type).
- **Markers** (`Bridge/MarkerSync.cs`, Core `Live/CommentMarkers.cs`):
  - One marker per comment, at the comment's point: the adaptive placement point is set, nothing is hosted.
  - Parameters by GUID: BimGo_Id, Title (the first line, 60 characters at most), Comment (the full text), Status
    (Open / In progress / Closed), Priority (Low / Normal / High), Author, AssignedTo, Created, Updated (local time,
    yyyy-MM-dd HH:mm), Level, Element (the UniqueId it was made on), Replies, Picture (the JPEG in the model folder),
    Synced, and the four yes/no parameters (open, in progress and closed drive your spheres; high priority is there
    for filters).
  - Matched by BimGo_Id. A sync updates only values that differ, moves a marker whose comment moved (more than 1 mm),
    removes the markers of deleted comments, and places new ones.
  - **One transaction**, "BimGo: comment markers" (one undo entry), only when something differs: no undo entry
    otherwise. The live session doesn't count it as an outside change (it's a "BimGo:" transaction).
  - **Deleted in Revit stays gone:** the ids BimGo has placed are kept in the model (extensible storage on a
    DataStorage element, schema "BimGoCommentMarkers"), so it holds for everyone working on the model. A marker BimGo
    removes itself (comment deleted, or closed with "remove closed") is forgotten, and is placed again if the comment
    comes back or reopens.
  - Workshared: markers another user owns are skipped ("couldn't be changed") and the rest go ahead.
  - Markers placed by hand (no BimGo_Id) and second copies of an id are left alone.
  - A failsafe: if a new marker's BimGo_Id can't be written (wrong family or parameter), the whole sync rolls back
    and says so, rather than placing duplicates at every sync.
- **Live sessions** (`markers.sync` → `markers.result`; `hello.ack` gains `commentMarkers: true`, additive):
  - The app sends every comment 1.5 s after the last change (add, edit, status, priority, assignee, reply, move,
    picture, delete), and once when the session starts, so the model starts in step.
  - The app shows a toast only when the model changed ("Revit markers: 1 placed, 2 updated") or something failed.
  - Options → Player → *Live sessions keep a marker…* (on by default) is read at each sync, so turning it off works
    mid-session.
  - An older add-in doesn't say `commentMarkers`, so the app sends nothing.
  - Big comment sets stay under the channel's 4 MB limit: long texts are cut to 1000, then 200 characters, then the
    title only.
- **Ribbon → Comment markers** (new button, pin icon):
  - *From this model's BimGo comments (N)*: the model folder's `comments.json`, which is what walkthroughs write. A
    full sync: markers of comments deleted since are removed.
  - *From a .bimgo file…*: reads only the file's comments (`BimGoReader.ReadComments`, no geometry). It adds and
    updates markers and removes nothing (a colleague's file mustn't delete yours).
  - *Also put back markers deleted in Revit* (the dialog's tick box): re-places them.
  - A summary dialog afterwards.
- **Options → Player → COMMENT MARKERS IN REVIT:** live markers on / off, remove closed markers, and the marker family
  path with Browse…. Stored in settings.json (`CommentMarkersLive`, `CommentMarkersRemoveClosed`,
  `CommentMarkerFamily`).
- **Tests** (`CommentMarkerTests`): the parameter values from a comment, what gets left out, the plan (place / update /
  move / remove / leave hand-placed and copies), deleted in Revit until put back, remove closed, add-only from a file,
  the stored list, summaries, the size limit, the GUIDs against the shared parameter file, reading only a file's
  comments, and the settings defaults.

## What isn't
- Revit → BimGo (status or text edited on a marker isn't read back; the next sync puts BimGo's values back).
- Comments in .bimgo files opened standalone don't sync by themselves: use the button's *From a .bimgo file…*.
- Markers for linked models' comments go in the host model (where the comment's point is).
- No plan symbol (adaptive families show in plans only within the view range).

## How to check
1. **Family version:** the add-in loads the .rfa into whichever Revit runs it. If you saved it in 2026 or 2027, Revit
   2025 can't load it: save it from 2025, or say which version and I'll note it. The dialog says so if it happens.
2. **Live:** Go on a model with a few comments (open, in progress, closed, one high priority). Within a couple of
   seconds of BimGo opening, a toast says "Revit markers: N placed". In Revit: spheres at the comments, coloured by
   status. Select one: the BimGo_ parameters are filled.
3. In BimGo: change a comment's status. About 1.5 s later its sphere changes colour ("1 updated"). Edit the text: Title
   and Comment change. If you move a comment's marker in BimGo, the sphere moves too. Delete the comment: the marker goes.
   Each is one undo entry "BimGo: comment markers". The live status shouldn't count them as outside changes.
4. In Revit, delete a marker, then change that comment in BimGo: it isn't put back (the toast says "1 deleted in
   Revit left out"). Ribbon → Comment markers, tick *Also put back markers deleted in Revit*, *From this model's BimGo
   comments*: it's back.
5. Options → Player: tick *Remove the markers of closed comments*, then change any comment: closed comments' markers
   go. Untick it: they come back on the next change.
6. Without a live session (BimGo closed): Comment markers → *From this model's BimGo comments*: "up to date". Try
   *From a .bimgo file…* with an exported file that has extra comments: they're added, nothing removed.
7. A model without the family: it's loaded from the add-in (or from Options' path when set).
8. Optional, workshared: a marker borrowed by someone else is reported as "couldn't be changed", the rest sync.

## Files
- Core: `Live/CommentMarkers.cs` (new: `MarkerFamily`, `MarkerRecord`, `MarkersSyncPayload`, `MarkersResultPayload`,
  `ExistingMarker`, `MarkerPlan`, `CommentMarkers`), `Live/LiveProtocol.cs` (`markers.sync` / `markers.result`,
  `HelloAckPayload.CommentMarkers`), `Live/LiveSessionSource.cs`, `Format/BimGoReader.cs` (`ReadComments`),
  `Scene/LaunchSettings.cs`.
- Revit: `Bridge/MarkerSync.cs` (new), `Commands/Cmds_BimGo.cs` (`Cmd_Markers`), `Live/LiveDispatcher.cs`,
  `Application.cs`, `Forms/OptionsWindow.xaml(.cs)`, `Resources/Families/BimGo_Comment_Marker.rfa` (embedded),
  `Resources/Icons16|32/BimGo_Markers*.png`, `Resources/Files/Tooltips.resx` / `.Designer.cs`, `BimGo.Revit.csproj`.
- App: `Game/GameSession.Markers.cs` (new), `GameSession.cs`, `GameSession.Document.cs`.
- Tests: `CommentMarkerTests.cs` (new).

## Revision r2 (build error)
- CS0234 in `MarkerSync.Explain`: `Autodesk.Revit.Exceptions.FileArgumentException` doesn't exist. The check is now
  the message alone ("newer"). Gavin applied it by hand (no zip).

## Revision r3 (BimGo_Id can't be set)
- Revit's API can't set shared parameters that aren't user-modifiable (USERMODIFIABLE 0), so BimGo_Id, BimGo_Synced and
  the four yes/no parameters were read-only to BimGo and the failsafe rolled the sync back. All 18 are now
  USERMODIFIABLE 1 in `tools/revit/BimGo_SharedParameters.txt` (same GUIDs). The flag is stored in the family when a
  parameter is added, so Gavin re-adds those six to the family, and models that already loaded the old family need it
  deleted first. The failsafe's message now says this.
- r3 (cont.): the six re-issued with **new GUIDs** (ID, SYNCED, IS_OPEN, IS_IN_PROGRESS, IS_CLOSED, IS_HIGH_PRIORITY),
  so Revit's binding map doesn't hold on to the old read-only definitions; `MarkerFamily` and the spec follow. The
  GUID test still checks code against the file. Gavin swaps them in the family (Edit Parameter → family → shared from
  the new file keeps the visibility links).
- r3 (cont.): Gavin's updated family is in `Resources/Families`: all 18 current GUIDs present (the old six's bytes
  still appear in the file, most likely leftovers that Revit keeps until a compacting save).

## Revision r4 (the old family kept loading)
- The shipped family is cached in `%LocalAppData%\BimGo\Families` and was only rewritten when its length differed;
  Revit files grow in whole blocks, so the re-saved family had the same length and the old copy kept loading. The
  cache is now compared byte for byte.
- The BimGo_Id failsafe now says whether the model's family lacks the parameter (old GUID) or has it read-only, and
  that an older copy is loaded (delete it from the model and sync again).

## Revision r5 (diagnosing BimGo_Id on a fresh model)
- Still "can't set BimGo_Id" on a fresh model with Gavin's editable family (GUIDs checked inside the .rfa: each sits
  with its own name). The r4 message guessed "read-only"; it only knew the value didn't read back. Now the failure
  says what Revit reports: found by GUID or only by name, storage type, read-only and user-modifiable flags, Set's
  return (or exception), and what it reads back; every shared parameter on the instance (name, GUID, type, flags,
  value) goes to `%LocalAppData%\BimGo\Logs\BimGo.Revit.log`.

## Revision r6 (markers tagged in the model)
- r5's report: BimGo_Id found by GUID, String, not read-only, user-modifiable, Set returned true, yet it read back
  empty straight after. So matching no longer depends on reading it: each marker carries its comment id in
  extensible storage on the instance (schema "BimGoCommentMarker", field CommentId); markers are matched by that tag
  first, BimGo_Id second (hand-copied or older markers). The tag is checked instead of the parameter (that failure
  still rolls back).
- The parameters are checked after a `doc.Regenerate()` (in case adaptive instances only settle then). If BimGo_Id is
  still empty the markers stay and the summary says "the marker parameters stayed empty (details in the log)"; the
  log lists every shared parameter on the instance with its value.
