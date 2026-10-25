using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using BimGo.Shell;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Kiosk mode (Round Z-C): a saved .bimgo opened from a kiosk shortcut (<c>BimGo.exe --kiosk "file"</c>, written by
    /// pause menu → MAKE KIOSK SHORTCUT) for a screen in a foyer or at an exhibition stand.
    /// <para>Visitors can only walk, fly and look: no pause menu, guns, edits, panels, tool keys, F1 sheet, saving or file
    /// drops, and Esc does nothing. The window opens fullscreen with the mouse captured, keeps running when it loses focus
    /// (the next click takes the mouse back) and ignores close requests (Alt+F4, the taskbar), so nothing prompts to save.
    /// The HUD is a small key hint only.</para>
    /// <para>After <see cref="KIOSK_IDLE_SECONDS"/> without input the bookmark tour loops (UI-free); with no bookmarks to
    /// tour, the player goes to the home view. Any key, click, mouse or gamepad move hands control back right where the
    /// tour is (that stop's sun, cut and visibility stay); the next idle tour starts from the walkthrough's own state
    /// again.</para>
    /// <para>The kiosk exit key (Ctrl+Shift+Q) held for <see cref="InputCatalog.KIOSK_EXIT_HOLD_SECONDS"/> leaves kiosk
    /// mode into the normal walkthrough. Start and exit are logged.</para>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>Two minutes without input start the idle tour.</summary>
        private const float KIOSK_IDLE_SECONDS = 120f;

        /// <summary>The key hint shows fully for this long after kiosk mode starts or a visitor takes over, then dims.</summary>
        private const float KIOSK_HINT_SECONDS = 8f;

        private static readonly KeyHint HINT_KIOSK = new(() =>
        {
            InputSituation kiosk = InputCatalog.Situation(InputCatalog.SIT_KIOSK);
            string move = kiosk != null && kiosk.Help.Count > 0 ? Hotkeys.Map.HelpKeys(kiosk.Help[0]) : string.Empty;
            string fly = Key(InputAction.Fly);
            var text = new System.Text.StringBuilder();
            if (move.Length > 0) { text.Append(move).Append(" to move · "); }
            text.Append("mouse to look");
            if (!string.IsNullOrEmpty(fly)) { text.Append(" · ").Append(fly).Append(" to fly"); }
            return text.ToString();
        });

        private const string KIOSK_TOUR_HINT = "Move the mouse or press a key to explore";
        private const string KIOSK_EXIT_HINT = "Keep holding to leave kiosk mode";

        private bool _kiosk;                // kiosk mode is on
        private bool _kioskToured;          // an idle tour has played (its remembered state is the base for the next one)
        private bool _kioskIdleDone;        // the idle action (tour or home view) has happened since the last input
        private bool _kioskSavedUnarmed;    // the gun state to put back on leaving
        private float _kioskIdle;           // seconds without input
        private float _kioskExitHeld;       // seconds the exit key has been held
        private float _kioskHintFrom;       // when the key hint last came back (clock)
        private Vector2 _kioskMouse;        // the cursor last frame (a free cursor moving counts as input)

        #endregion

        #region Kiosk shortcut

        /// <summary>
        /// Pause menu → MAKE KIOSK SHORTCUT (files): a desktop shortcut that opens the saved file in kiosk mode. Unsaved
        /// changes are offered a save first (the shortcut opens what is on disk); the result is reported in a message box
        /// (the pause menu shows no toasts).
        /// </summary>
        private void MakeKioskShortcut()
        {
            if (!IsFileMode) { return; }
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();

            if (IsDirty || string.IsNullOrEmpty(DocumentPath))
            {
                bool save = FileDialogs.AskYesNo(_window.Handle,
                    "This model has unsaved changes. A kiosk shortcut opens the saved file, so visitors wouldn't see them.\n\nSave now, then make the shortcut?",
                    "BimGo kiosk shortcut");
                if (!save || !Save(saveAs: false)) { return; }
            }

            string error = FileAssociation.CreateKioskShortcut(DocumentPath, out string shortcut);
            if (error != null)
            {
                Sound.Play(SoundId.Error);
                Utilities.Log_Utils.Write($"Kiosk shortcut not created ({shortcut ?? "no path"}): {error}");
                FileDialogs.ShowError(_window.Handle, $"The kiosk shortcut could not be made:\n{error}", "BimGo kiosk shortcut");
                return;
            }

            Sound.Play(SoundId.Commit);
            Utilities.Log_Utils.Write($"Kiosk shortcut written: {shortcut} → {DocumentPath}");
            bool tour = false;
            foreach (BookmarkRecord record in Bookmarks.Bookmarks)
            {
                if (!record.TourSkip)
                {
                    tour = true;
                    break;
                }
            }
            string idle = tour
                ? "after two minutes without input the bookmark tour plays (on a loop, without UI) until someone moves the mouse or presses a key"
                : "after two minutes without input it goes back to the home view (bookmark some views to have a tour play instead)";
            string exe = Environment.ProcessPath ?? "BimGo.exe";
            FileDialogs.ShowInfo(_window.Handle,
                $"A kiosk shortcut is on your desktop:\n{Path.GetFileName(shortcut)}\n\n" +
                $"It opens {DocumentName} fullscreen in kiosk mode. Visitors can only walk, fly and look; {idle}.\n\n" +
                $"To leave kiosk mode, hold {Key(InputAction.KioskExit)} for {InputCatalog.KIOSK_EXIT_HOLD_SECONDS:0} seconds.\n\n" +
                $"The shortcut runs this copy of BimGo:\n{exe}",
                "BimGo kiosk shortcut");
        }

        #endregion

        #region Enter / leave

        /// <summary>
        /// Starts kiosk mode (end of <see cref="Initialise"/> for a kiosk launch): no gun, the UI hidden but for the key
        /// hint, fullscreen, the mouse captured, close requests ignored.
        /// </summary>
        private void EnterKiosk()
        {
            _kiosk = true;
            _kioskSavedUnarmed = _unarmed;
            if (!_unarmed)
            {
                _guns[_activeGun].OnDeselect();
                _unarmed = true;
            }
            _uiHidden = true;
            _showHelp = false;
            _toast = null;
            _kioskIdle = 0f;
            _kioskIdleDone = false;
            _kioskExitHeld = 0f;
            _kioskHintFrom = _clock;
            _kioskMouse = _window.Input.MousePosition;

            _window.BlockClose = true;
            if (!_window.IsFullscreen) { _window.ToggleFullscreen(); }
            _window.Input.ReleaseAll();
            if (_window.IsActive) { _window.SetCaptured(true); }
            Utilities.Log_Utils.Write($"Kiosk mode started: {_options.Source.DisplayName}");
        }

        /// <summary>
        /// Leaves kiosk mode (the exit key held): the normal walkthrough where the visitor stood, its own sun, cut and
        /// visibility (an idle tour's stop state is put back), the gun as it was, the UI on. The window stays fullscreen.
        /// </summary>
        private void LeaveKiosk()
        {
            if (!_kiosk) { return; }
            if (_tourActive)
            {
                StopTour(finished: false); // back where the tour started, everything as it was
            }
            else if (_kioskToured)
            {
                ResetKioskWalkthrough();
            }
            _tourSavedVisibility = null;
            _tourSavedIsolate = null;

            _kiosk = false;
            _kioskExitHeld = 0f;
            _window.BlockClose = false;
            _uiHidden = false;
            if (!_kioskSavedUnarmed) { _unarmed = false; }
            _window.Input.ReleaseAll(); // the exit chord's keys don't also crouch / run
            Sound.Play(SoundId.UiClick);
            Toast($"Kiosk mode off: the full walkthrough is back ({Key(InputAction.Back)} for the menu)", 4f);
            Utilities.Log_Utils.Write("Kiosk mode left (exit key held): normal walkthrough.");
        }

        /// <summary>
        /// The walkthrough's own sun, colour profile, cut, visibility and eye height as the first idle tour remembered them
        /// (an idle tour that a visitor took over leaves its stop's state showing).
        /// </summary>
        private void ResetKioskWalkthrough()
        {
            if (_tourSavedVisibility == null) { return; } // nothing remembered
            RestoreWalkthroughState();
            _player.Controller.SetStandingEye(_tourSavedEye, force: true);
            _eyePreset = _tourSavedPreset;
            _eyeCustom = _tourSavedEyeCustom;
        }

        #endregion

        #region Per frame

        /// <summary>
        /// Per frame in kiosk mode, instead of the walkthrough's keys (<see cref="UpdateFrame"/>): the exit key, the idle
        /// timer and tour, then walking, flying and looking only. Mirrors <see cref="InputCatalog.SIT_KIOSK"/>.
        /// </summary>
        private void UpdateKiosk(InputState input, float dt)
        {
            _window.FocusLost = false; // losing focus doesn't pause a kiosk

            // Anything a visitor does (a free cursor moving over the window counts too)
            Vector2 mouse = input.MousePosition;
            bool moved = Vector2.DistanceSquared(mouse, _kioskMouse) > 4f;
            _kioskMouse = mouse;
            bool activity = _window.IsActive && (input.AnyActivity() || moved);

            // The exit key, held (from the idle tour too)
            if (_window.IsActive && input.Down(InputAction.KioskExit))
            {
                _kioskExitHeld += dt;
                if (_kioskExitHeld >= InputCatalog.KIOSK_EXIT_HOLD_SECONDS)
                {
                    LeaveKiosk();
                    return;
                }
            }
            else
            {
                _kioskExitHeld = 0f;
            }

            if (activity)
            {
                _kioskIdle = 0f;
                _kioskIdleDone = false;
                if (_tourActive)
                {
                    ReleaseTourHere();
                    _kioskHintFrom = _clock;
                    input.ConsumeFrame(); // the key or click that woke it does nothing else
                }
            }
            else
            {
                _kioskIdle += dt;
            }

            // The mouse stays captured while the window is active (it comes back on the click that refocuses it)
            if (_window.IsActive && !_window.IsCaptured)
            {
                _window.SetCaptured(true);
                input.ConsumeClicks();
            }

            if (_tourActive)
            {
                AdvanceKioskTour(dt);
                return;
            }
            if (!_kioskIdleDone && _kioskIdle >= KIOSK_IDLE_SECONDS)
            {
                StartKioskIdle();
                if (_tourActive) { return; }
            }

            UpdateRoom();
            if (input.Pressed(InputAction.Fly)) { _player.ToggleFly(); }
            if (input.Pressed(InputAction.Jump) && !_player.Flying) { _player.QueueJump(); }
            if (_window.IsCaptured) { _player.Look(input.MouseDeltaX, input.MouseDeltaY, _sensitivity, _invertY); }
            if (_window.IsActive) { PadLook(input); }
        }

        /// <summary>
        /// Two minutes without input: the bookmark tour (looping, no UI), else the home view.
        /// </summary>
        private void StartKioskIdle()
        {
            _kioskIdleDone = true;
            if (_kioskToured) { ResetKioskWalkthrough(); } // stops that kept nothing use the walkthrough's own state
            if (StartTour(kiosk: true))
            {
                _kioskToured = true;
                _tourHintUntil = 0f; // the tour's own key hint doesn't apply here
                _toast = null;
                Utilities.Log_Utils.Write("Kiosk: idle, the bookmark tour is playing.");
                return;
            }
            _player.GoHome();
            Utilities.Log_Utils.Write("Kiosk: idle, back to the home view (no bookmarks to tour).");
        }

        /// <summary>
        /// The idle tour's clock (looping; no tour keys: any input hands control back first).
        /// </summary>
        private void AdvanceKioskTour(float dt)
        {
            _tourTime += dt;
            if (_tour.Duration > 0f && _tourTime >= _tour.Duration) { _tourTime %= _tour.Duration; }
            EvaluateTour();
            UpdateRoom();
        }

        #endregion

        #region HUD

        /// <summary>
        /// Kiosk mode's 2D layer: the idle tour's fades and "press a key" line, or the small key hint (bright for a few
        /// seconds, then dim); the exit bar while the exit key is held; error toasts only.
        /// </summary>
        private void BuildKioskHud(int width, int height)
        {
            FontAtlas f = _ui.Atlas;
            if (_toastImportant) { BuildToast(f, width); }

            string hint;
            float alpha;
            if (_tourActive)
            {
                if (_tourPose.Black > 0.002f) { _ui.Rect(0, 0, width, height, Rgba.Hex(0x000000, Math.Clamp(_tourPose.Black, 0f, 1f))); }
                hint = KIOSK_TOUR_HINT;
                alpha = 0.55f + 0.25f * MathF.Sin(_clock * 1.6f); // a slow pulse
            }
            else
            {
                hint = HINT_KIOSK.Text;
                float shown = _clock - _kioskHintFrom;
                alpha = shown < KIOSK_HINT_SECONDS ? 1f : MathF.Max(0.45f, 1f - (shown - KIOSK_HINT_SECONDS) / 1.5f);
            }

            float hintW = UiBatch.Measure(f.Body, hint) + S(28);
            float hx = MathF.Round(width * 0.5f - hintW * 0.5f);
            float hy = height - S(56);
            _ui.Panel(hx, hy, hintW, S(30), Rgba.WithAlpha(UiTheme.PANEL, 0.74f * alpha), Rgba.WithAlpha(UiTheme.PANEL_BORDER, alpha));
            _ui.TextCentred(f.Body, width * 0.5f, hy + S(7), hint, Rgba.WithAlpha(UiTheme.TEXT, alpha));

            // Leaving: a bar fills while the exit key is held
            if (_kioskExitHeld > 0f)
            {
                float t = Math.Clamp(_kioskExitHeld / InputCatalog.KIOSK_EXIT_HOLD_SECONDS, 0f, 1f);
                float w = MathF.Max(UiBatch.Measure(f.Body, KIOSK_EXIT_HINT) + S(40), S(300)), h = S(58);
                float x = MathF.Round(width * 0.5f - w * 0.5f), y = MathF.Round(height * 0.5f - h * 0.5f);
                _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, Rgba.WithAlpha(UiTheme.ACCENT, 0.6f));
                _ui.TextCentred(f.Body, width * 0.5f, y + S(11), KIOSK_EXIT_HINT, UiTheme.TEXT);
                float barX = x + S(16), barW = w - S(32), barY = y + h - S(18);
                _ui.Rect(barX, barY, barW, S(5), Rgba.Hex(0xFFFFFF, 0.15f));
                _ui.Rect(barX, barY, barW * t, S(5), UiTheme.ACCENT);
            }
        }

        #endregion
    }
}
