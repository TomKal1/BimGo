using System.Diagnostics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round Z-E, first run: a welcome card over the first walkthrough on this PC (settings <c>WelcomeShown</c>): how to
    /// move and look, the guns, F1 and the menu, with START WALKING and ONLINE GUIDE (the docs). Esc also closes it. Not
    /// in kiosk mode or a safe start. The player stands still and the cursor is free while it shows.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>The user guide (the repo's docs folder).</summary>
        private const string DOCS_URL = "https://github.com/aussieBIMguru/BimGo/tree/main/docs";

        private bool _welcomeOpen;

        private static readonly KeyHint HINT_WELCOME_MOVE = new(() =>
        {
            KeyMap m = Hotkeys.Map;
            return $"{m.DescribeJoined(" ", true, InputAction.MoveForward, InputAction.MoveLeft, InputAction.MoveBack, InputAction.MoveRight)}  move  ·  mouse  look  ·  {Key(InputAction.Run)}  run";
        });
        private static readonly KeyHint HINT_WELCOME_MORE = new(() =>
            $"{Key(InputAction.Jump)}  jump  ·  {Key(InputAction.Crouch)}  crouch  ·  {Key(InputAction.Fly)}  fly through walls");
        private static readonly KeyHint HINT_WELCOME_GUNS = new(() =>
            $"{Hotkeys.Map.DescribeRange(InputAction.Gun1, 9)}  tool guns (scan, measure, comment…)  ·  {Key(InputAction.GunPrimary)} / {Key(InputAction.GunSecondary)}  use them");
        private static readonly KeyHint HINT_WELCOME_HELP = new(() =>
            $"{Key(InputAction.Help)}  the keys for what you're doing  ·  Esc  menu, settings and CONTROLS");

        #endregion

        #region Open / close

        /// <summary>Shows the card (end of <see cref="Initialise"/>, first walkthrough only).</summary>
        private void OpenWelcome()
        {
            _welcomeOpen = true;
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
        }

        /// <summary>Closes the card for good (remembered in the settings) and captures the mouse to start walking.</summary>
        private void CloseWelcome()
        {
            if (!_welcomeOpen) { return; }
            _welcomeOpen = false;
            Scene.Settings.WelcomeShown = true;
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            settings.WelcomeShown = true;
            settings.Save();
            _window.Input.ReleaseAll();
            if (_window.IsActive) { _window.SetCaptured(true); }
        }

        /// <summary>Opens the user guide in the browser (never throws).</summary>
        private void OpenDocs()
        {
            try
            {
                Process.Start(new ProcessStartInfo(DOCS_URL) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"The guide could not be opened: {ex.Message}");
                Toast($"Open {DOCS_URL} in a browser", 6f, important: true);
            }
        }

        /// <summary>Per frame while the card shows (instead of the walkthrough's keys): Esc closes it.</summary>
        private void UpdateWelcome(InputState input)
        {
            _window.FocusLost = false;
            if (input.Pressed(InputAction.Back))
            {
                input.ConsumeFrame();
                CloseWelcome();
            }
        }

        #endregion

        #region Drawing

        /// <summary>The card, centred over the HUD.</summary>
        private void BuildWelcome(int width, int height)
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            string[] lines = { HINT_WELCOME_MOVE.Text, HINT_WELCOME_MORE.Text, HINT_WELCOME_GUNS.Text, HINT_WELCOME_HELP.Text };

            float textW = 0f;
            foreach (string line in lines) { textW = MathF.Max(textW, UiBatch.Measure(f.Body, line)); }
            float w = MathF.Min(MathF.Max(textW + S(64), S(520)), width - S(40));
            float lineH = S(26);
            float h = S(64) + lines.Length * lineH + S(26) + S(40) + S(28);
            float x = MathF.Round((width - w) * 0.5f), y = MathF.Round((height - h) * 0.5f);

            _ui.Rect(0, 0, width, height, Rgba.Hex(0x000000, 0.35f));
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, Rgba.WithAlpha(UiTheme.ACCENT, 0.6f));
            _ui.TextCentred(f.Title, width * 0.5f, y + S(18), "WELCOME TO BIMGO", UiTheme.TEXT, S(2));

            float ty = y + S(64);
            foreach (string line in lines)
            {
                _ui.TextCentred(f.Body, width * 0.5f, ty, line, UiTheme.TEXT_SOFT);
                ty += lineH;
            }

            float buttonW = MathF.Min(S(220), (w - S(48)) * 0.5f), gap = S(16);
            float by = ty + S(26);
            float bx = MathF.Round(width * 0.5f - buttonW - gap * 0.5f);
            if (MenuButton(f, bx, by, buttonW, "ONLINE GUIDE", false, false, height: S(40), compact: true)) { OpenDocs(); }
            if (MenuButton(f, bx + buttonW + gap, by, buttonW, "START WALKING", primary: true, danger: false, height: S(40), compact: true))
            {
                input.ConsumeClicks();
                CloseWelcome();
            }
        }

        #endregion
    }
}
