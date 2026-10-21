# X-D0 build notes: frame rate limit

## What's in
- `LaunchSettings.FrameRateLimit` (int, default 30; `FRAME_RATE_LIMITS` = 0 / 30 / 45 / 60, 0 = off;
  `NearestFrameRateLimit` snaps a hand-edited value). Existing settings files have no value, so they get 30.
- `GameSession.FrameCap.cs`: `WaitForFrameLimit()` runs after `Swap()` in the main loop. It sleeps `Thread.Sleep(1)`
  until 2 ms before the frame is due, then spins. The schedule steps one period each frame; a frame that ran long
  resets it (no catch-up burst). `timeBeginPeriod(1)` from the first limited frame, `timeEndPeriod(1)` when the session
  ends (loop exit and `Dispose`). New `WinMm.timeBeginPeriod / timeEndPeriod` imports.
- Not limited while recording a video (incl. time-lapse) or running batch stills (`_video` / `_stillsBatch`).
  Single photos render inside one frame, so they're unaffected either way.
- Pause menu → Display: "Frame rate limit" row (Off / 30 / 45 / 60) under VSync; `RIGHT_CARD_HEIGHT` 440 → 482 to fit
  (short windows scroll as before).
- Tests: `FrameRateLimitTests` (default 30; snapping).

## What isn't
- No hotkey, no Revit options window control (pause menu only; the value is kept when Revit saves the settings).
- Home screen / progress screens aren't limited (they're already light).
- Not compiled here (no .NET SDK in the sandbox): brackets checked.

## How to check
1. Open a model, turn on Show FPS: the readout sits at ~30 (VSync on or off). Fans should quieten.
2. Pause → Display: the new row shows 30. Pick 45 / 60 / Off: the readout follows at once (Off with VSync on → 60).
3. Close and reopen: the choice is kept.
4. Record a short video and run a small batch of stills: both run at full speed (no slower than before).
5. Run the tests: `FrameRateLimitTests` pass.
