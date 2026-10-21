using System.Diagnostics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round X-D0: the frame rate limit (pause menu → Display: Off / 30 / 45 / 60, default 30, per machine in
    /// LaunchSettings). After each presented frame the loop waits until the next frame is due: Thread.Sleep(1) for most
    /// of the wait (with a 1 ms system timer while limiting), a short spin for the last 2 ms. Not applied while recording
    /// a video or running batch stills. Works with or without VSync (at 30 on a 60 Hz display VSync shows each frame
    /// for two refreshes).
    /// </summary>
    internal sealed partial class GameSession
    {
        private static readonly string[] FRAME_LIMIT_OPTIONS = { "Off", "30", "45", "60" };

        /// <summary>Target frames per second (0 = no limit).</summary>
        private int _frameLimit = LaunchSettings.DEFAULT_FRAME_RATE_LIMIT;

        /// <summary>When the next frame is due (Stopwatch ticks; 0 = not limiting).</summary>
        private long _frameDue;

        /// <summary>timeBeginPeriod(1) is in force.</summary>
        private bool _timerPeriodSet;

        /// <summary>
        /// Waits until the next frame is due (call after the frame is presented).
        /// </summary>
        private void WaitForFrameLimit()
        {
            if (_frameLimit <= 0 || _video != null || _stillsBatch != null)
            {
                _frameDue = 0;
                return;
            }
            if (!_timerPeriodSet)
            {
                Native.WinMm.timeBeginPeriod(1);
                _timerPeriodSet = true;
            }

            long period = Stopwatch.Frequency / _frameLimit;
            long now = Stopwatch.GetTimestamp();
            if (_frameDue == 0)
            {
                _frameDue = now + period;
                return;
            }

            long spin = Stopwatch.Frequency / 500; // the last 2 ms
            while (_frameDue - now > spin)
            {
                Thread.Sleep(1);
                now = Stopwatch.GetTimestamp();
            }
            while (now < _frameDue)
            {
                Thread.SpinWait(64);
                now = Stopwatch.GetTimestamp();
            }

            // Next due one period on; a frame that ran long resets the schedule (no burst to catch up)
            _frameDue += period;
            if (now - _frameDue > 0) { _frameDue = now + period; }
        }

        /// <summary>
        /// Puts the system timer back (session end).
        /// </summary>
        private void EndFrameLimit()
        {
            if (!_timerPeriodSet) { return; }
            Native.WinMm.timeEndPeriod(1);
            _timerPeriodSet = false;
        }

        /// <summary>
        /// The Display tab's frame rate limit row.
        /// </summary>
        /// <returns>The height used.</returns>
        private float BuildFrameLimitRow(FontAtlas f, InputState input, float ix, float y, float iw)
        {
            _ui.Text(f.Body, ix, y, "Frame rate limit", UiTheme.TEXT);
            int current = Array.IndexOf(LaunchSettings.FRAME_RATE_LIMITS, _frameLimit);
            int picked = Segmented(f, input, ix + iw * 0.45f, y - S(8), iw * 0.55f, FRAME_LIMIT_OPTIONS, current);
            if (picked != current && picked >= 0)
            {
                _frameLimit = LaunchSettings.FRAME_RATE_LIMITS[picked];
                _frameDue = 0;
            }
            return S(36);
        }
    }
}
