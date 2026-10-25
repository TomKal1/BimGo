# Z-C brief: kiosk mode (Round Z, presentation)

Agreed 2026-10-22 (the Z-C handoff's plan plus Gavin's answers to four questions).

- **Launch:** pause menu → **MAKE KIOSK SHORTCUT** (session stack, files only) writes a desktop shortcut
  `BimGo.exe --kiosk "<file>"`. Nothing in the file changes. (Built: unsaved changes are offered a save first, since
  the shortcut opens what is on disk.)
- **Visitors can only walk, fly and look.** No pause menu, guns, edits, panels, tool keys, F1 sheet, saving or file
  drops. Esc does nothing. A small key hint only ("WASD to move · mouse to look · V to fly").
- **Idle:** 2 minutes without input → the bookmark tour (looping, UI-free), else the home view. **Any input takes over
  at the tour's current pose and look; that stop's sun, cut and visibility stay.**
- **Exit:** **Ctrl+Shift+Q held for 2 s** (a bar fills) → the normal walkthrough. New action, defaults version 9.
- **Lock-down (chosen):** close requests (Alt+F4, the X, the taskbar) are ignored in kiosk mode; focus loss doesn't
  pause (the next click takes the mouse back).
- **Not chosen:** gamepad-specific kiosk support; a second kiosk launch reopening the file (it only brings the running
  window forward).
- Also: fullscreen on start, mouse captured, UI scale as saved, start / exit logged.
