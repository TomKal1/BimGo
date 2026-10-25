# Z-C build notes: kiosk mode

Not compiled here (no .NET SDK in the sandbox). Brackets, strings and char literals script-checked in every changed
`.cs` file; new member names checked across the `GameSession` partials (all unique); UI strings use Latin-1 only (the
font atlas). README §3's generated block was updated by hand to match `KeyMap.DefaultsMarkdown()` (the README test
rewrites it if I got a character wrong: review and commit).

## What's in
- **MAKE KIOSK SHORTCUT** (pause menu, session stack under PUSH TO REVIT, files only; `GameSession.Kiosk.cs`
  `MakeKioskShortcut`):
  - Unsaved changes: "Save now, then make the shortcut?" (Yes saves to the open file, No / a cancelled save stops).
  - Writes `<Desktop>\<model> (BimGo kiosk).lnk` (invalid characters → `_`; an existing one is replaced):
    target the running `BimGo.exe`, arguments `--kiosk "<full path>"`, BimGo's icon, the exe's folder as the working
    directory, description "BimGo kiosk: <model>". `FileAssociation.CreateKioskShortcut`, sharing a new
    `WriteShortcut` with the Start-menu shortcut (now with arguments).
  - A message box reports it (the pause menu shows no toasts): the shortcut's name, what kiosk mode does (tour or home
    view, depending on whether any bookmark is in the tour), the exit key, and **which exe it runs** (a dev build's
    shortcut points at the dev build).
- **Launch** (`Program.cs`, `OpenTarget`, `AppShell`, `SessionOptions`):
  - `--kiosk` is a flag: `FirstFileArgument` now skips a value only after `--session`, so the file after `--kiosk` is
    found. `OpenTarget.KioskFile` → `AppShell.RunFile(path, kiosk)` → `SessionOptions.Kiosk`.
  - `--kiosk` with `--session` is ignored (kiosk is files only); a missing file goes to the start screen as before.
  - **Second launch:** `AppInstance.ForwardToRunningInstance` sends no inbox request for a kiosk target, only brings
    the running window forward.
- **Kiosk session** (`GameSession.Kiosk.cs`; entered at the end of `Initialise` for a file):
  - No gun (the gun state is put back on leaving), the UI hidden but for the key hint, help off, fullscreen, mouse
    captured, `GameWindow.BlockClose` on: WM_CLOSE (Alt+F4, the X, the taskbar) is swallowed. The controls-defaults
    "what's new" toast is left for a normal launch.
  - `UpdateFrame` hands every frame to `UpdateKiosk` (before the pad focus and everything else): the exit key, the
    idle timer, then **Fly**, **Jump** and mouse / pad look. Movement, run and crouch are the player's own physics. The
    mouse is captured whenever the window is active. Focus loss is cleared, never pauses.
  - **The situation:** `InputCatalog.SIT_KIOSK` (Kiosk group + move, jump, run, crouch, fly, look), its F1 rows,
    `CurrentSituation` and the pad cursor (off).
  - `PollHost`: dropped files are discarded; the inbox isn't polled (other launches' requests wait and open after
    leaving kiosk mode).
  - The run loop never shows the save prompt in kiosk mode.
  - **HUD** (`BuildKioskHud`, before the photo / tour branches in `Render`): the key hint ("WASD to move · mouse to
    look · V to fly", from the current bindings), bright for 8 s after the start or a take-over, then 45 %; error
    toasts only; the exit bar. No crosshair, minimap, markers or room tint (`_uiHidden`).
- **Idle** (`KIOSK_IDLE_SECONDS` = 120):
  - Activity = a key or button down / pressed, raw mouse movement, the wheel, typing, a pad button or stick
    (`InputState.AnyActivity`), or the free cursor moving more than 2 px. Only while the window is active.
  - `StartTour(kiosk: true)`: loop on, UI off whatever MEDIA → TOUR says; quiet when there's nothing to play (no
    notice, no error sound). The tour's own key hint is suppressed; instead "Move the mouse or press a key to explore"
    pulses gently. `AdvanceKioskTour` runs the clock (looping) without the tour's keys.
  - No bookmark in the tour: `GoHome()` once (until the next input).
  - **Take-over** (`ReleaseTourHere`, Tour.cs): the sun blend settles on the stop's sun, the tour ends where it is,
    flying if the stop's bookmark was flown, the stop's sun / cut / visibility / eye height / colour profile stay. The
    key that woke it does nothing else (that frame is consumed).
  - The next idle tour first puts the walkthrough's own state back (`ResetKioskWalkthrough`, from what the first tour
    remembered), so stops that kept nothing don't inherit the last stop's sun.
- **Exit:** `InputAction.KioskExit`, **Ctrl+Shift+Q**, Hold kind, group *Kiosk mode* (`InputContext.Kiosk`, before
  Fixed), live only in `SIT_KIOSK`; rebindable in CONTROLS. Held for `InputCatalog.KIOSK_EXIT_HOLD_SECONDS` (2):
  - Mid-tour: `StopTour` (back where the tour started, everything as it was). Otherwise the file's own sun / cut /
    visibility / eye height come back where the visitor stands.
  - Close requests allowed again, UI on, the gun as it was, keys released (the chord doesn't crouch / run), a toast,
    and the window stays fullscreen (F11 as usual).
  - `DEFAULTS_VERSION` 8 → 9 with a `DefaultsChanges` row; README §3 Kiosk table by hand.
- **Logging:** kiosk start (model name), idle (tour or home view), take-over (stop number), exit, shortcut written /
  failed.
- **Tests:** `Kiosk_ExitIsHeldModifiedAndOnlyInKiosk` (context, Hold kind, default chord, only live in kiosk, what is
  and isn't live there); SIT_KIOSK added to the situations list check. The existing clash, help and defaults-version
  tests cover the new action.

## Watch for
- OS-level lock-down is out of scope: Windows key, Alt+Tab, Ctrl+Alt+Del and Task Manager still work (for a locked
  public PC, Windows' assigned access / a kiosk account). Ctrl+Shift+Q is the in-app way out.
- The shortcut stores the exe's path: a shortcut made from a dev build runs the dev build (the message box says so).
  Remake it after installing (Z-E).
- Gamepad: the sticks and buttons bound to move / look / fly / jump work (they share the actions), and a pad counts as
  activity; its menu buttons do nothing. Not tuned beyond that (not chosen).
- A crouched tour stop's camera is lower than the walking eye: taking over there pops up to standing height.
- After leaving kiosk mode, the walkthrough is the normal one: saving works as usual (nothing the tour showed was
  recorded).

## Changed files
`src/BimGo.App/`: `Program.cs`, `Shell/OpenTarget.cs`, `Shell/AppInstance.cs`, `Shell/AppShell.cs`,
`Shell/FileAssociation.cs`, `Platform/GameWindow.cs`, `Platform/InputState.cs`, `Platform/FileDialogs.cs`,
`Game/SessionOptions.cs`, `Game/GameSession.cs`, `Game/GameSession.Kiosk.cs` (new), `Game/GameSession.Tour.cs`,
`Game/GameSession.Document.cs`, `Game/GameSession.Gamepad.cs`, `Game/GameSession.Render.cs`, `Game/GameSession.Menu.cs`.
`src/BimGo.Core/Scene/InputCatalog.cs`; `tests/BimGo.Core.Tests/ControlsTests.cs`; `README.md` (§1, §3, pause menu,
§8, §9).
