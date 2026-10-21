using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round X-A: the sun time-lapse (MEDIA → VIDEO → Sun time-lapse): a fixed view (where you are, or a bookmark's
    /// view with its cut, hidden items and colour profile) while the sun crosses the sky, recorded through the video
    /// recorder.
    /// <list type="bullet">
    /// <item>The range: sunrise → sunset with 0–120 min either side (Core <see cref="SunEvents"/>), or a custom start
    /// and end (an end at or before the start runs into the next day: a night of stars). The date: the bookmark's sun if
    /// it keeps one, else the sun panel's.</item>
    /// <item>Each video frame sets the sun from <see cref="TimeLapsePlan.At"/> (fractional minutes: smooth); shadows,
    /// sky, moon and stars follow. Clouds drift <see cref="TIMELAPSE_CLOUD_RATE"/> × faster. The weather and lightning
    /// keep video time (their clock is the frame's), so strikes keep the WCAG pace in the output.</item>
    /// <item>Reflection probes re-bake as it plays (a couple of faces a frame, nearest first, a new pass when one ends).</item>
    /// <item>An optional date / time stamp, bottom right (off by default).</item>
    /// </list>
    /// The range, margin, length and stamp are per-machine settings; the view is per session.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>Clouds drift this many times their real speed in a time-lapse (per second of video).</summary>
        private const float TIMELAPSE_CLOUD_RATE = 8f;

        private static readonly string[] TIMELAPSE_RANGES = { "Sunrise → sunset", "Custom" };

        // Settings (per machine)
        private bool _timeLapseCustom;
        private int _timeLapseFrom = 6 * 60, _timeLapseTo = 20 * 60;
        private int _timeLapseMargin = 30;
        private int _timeLapseSeconds = 20;
        private bool _timeLapseStamp;

        // MEDIA → VIDEO: the time-lapse is the source; its view (-1: where you are, else a bookmark's index)
        private bool _videoTimeLapse;
        private int _timeLapseView = -1;
        private readonly ScrollRegion _timeLapseScroll = new();

        // The panel's preview (recomputed only when something it depends on changes)
        private TimeLapsePlan _timeLapsePreview;
        private string _timeLapsePreviewProblem;
        private int _timeLapsePreviewKey;
        private bool _timeLapsePreviewValid;

        // A time-lapse being recorded (null otherwise), and the sun time each frame sets
        private TimeLapsePlan _timeLapse;
        private readonly SunTime _timeLapseSun = new();

        #endregion

        #region Settings

        private void ReadTimeLapseSettings(LaunchSettings settings)
        {
            _timeLapseCustom = settings.TimeLapseCustom;
            _timeLapseFrom = Math.Clamp(settings.TimeLapseFrom, 0, 1439);
            _timeLapseTo = Math.Clamp(settings.TimeLapseTo, 0, 1439);
            _timeLapseMargin = Math.Clamp(settings.TimeLapseMargin, 0, TimeLapsePlan.MAX_MARGIN);
            _timeLapseSeconds = Math.Clamp(settings.TimeLapseSeconds, TimeLapsePlan.MIN_SECONDS, TimeLapsePlan.MAX_SECONDS);
            _timeLapseStamp = settings.TimeLapseStamp;
            ReadBatchStillsSettings(settings);
        }

        private void WriteTimeLapseSettings(LaunchSettings settings)
        {
            settings.TimeLapseCustom = _timeLapseCustom;
            settings.TimeLapseFrom = _timeLapseFrom;
            settings.TimeLapseTo = _timeLapseTo;
            settings.TimeLapseMargin = _timeLapseMargin;
            settings.TimeLapseSeconds = _timeLapseSeconds;
            settings.TimeLapseStamp = _timeLapseStamp;
            WriteBatchStillsSettings(settings);
        }

        #endregion

        #region State

        /// <summary>The bookmark the time-lapse films from, or null for where you are.</summary>
        private BookmarkRecord TimeLapseBookmark =>
            _timeLapseView >= 0 && _timeLapseView < Bookmarks.Bookmarks.Count ? Bookmarks.Bookmarks[_timeLapseView] : null;

        /// <summary>The time-lapse's date: the bookmark's sun if it keeps one, else the sun panel's.</summary>
        private SunTime TimeLapseDate(BookmarkRecord bookmark) => bookmark?.Sun ?? _sun.Time;

        /// <summary>True when the reflection probes are showing (so a change of light should re-bake them).</summary>
        private bool ProbesShown => ((_reflections && _reflectProbes) || _reflectDebug == 2) && _realistic && !_whitecard;

        /// <summary>
        /// The plan as the panel's settings stand (cached: recomputed when the date, place, range, length or rate change).
        /// </summary>
        private TimeLapsePlan TimeLapsePreview(out string problem)
        {
            SunTime date = TimeLapseDate(TimeLapseBookmark);
            var hash = new HashCode();
            hash.Add(date.Month);
            hash.Add(date.Day);
            hash.Add(date.DaylightSaving);
            hash.Add(_timeLapseCustom);
            hash.Add(_timeLapseFrom);
            hash.Add(_timeLapseTo);
            hash.Add(_timeLapseMargin);
            hash.Add(_timeLapseSeconds);
            hash.Add(_videoFps);
            hash.Add(_location.Latitude);
            hash.Add(_location.Longitude);
            hash.Add(_location.TimeZone);
            hash.Add(_location.AutoDaylightSaving);
            int key = hash.ToHashCode();
            if (!_timeLapsePreviewValid || key != _timeLapsePreviewKey)
            {
                _timeLapsePreviewValid = true;
                _timeLapsePreviewKey = key;
                _timeLapsePreview = TimeLapsePlan.Create(_location, DateTime.Today.Year, date, _timeLapseCustom, _timeLapseFrom, _timeLapseTo, _timeLapseMargin,
                    _timeLapseSeconds, _videoFps, out _timeLapsePreviewProblem);
            }
            problem = _timeLapsePreviewProblem;
            return _timeLapsePreview;
        }

        #endregion

        #region Start / frames / end

        /// <summary>
        /// RECORD TIME-LAPSE: holds the chosen view and records the sun's day through the video recorder.
        /// </summary>
        private void StartTimeLapse()
        {
            if (_video != null || _tourActive || _player == null) { return; }
            if (!VideoSizeFits(out int width, out int height)) { return; }

            BookmarkRecord bookmark = TimeLapseBookmark;
            TimeLapsePlan plan = TimeLapsePlan.Create(_location, DateTime.Today.Year, TimeLapseDate(bookmark), _timeLapseCustom, _timeLapseFrom, _timeLapseTo,
                _timeLapseMargin, _timeLapseSeconds, _videoFps, out string problem);
            if (plan == null)
            {
                _mediaNotice = problem;
                Sound.Play(SoundId.Error);
                return;
            }

            // Where you are now (taken before the walkthrough is remembered: the camera as you see it)
            Vector3 eye = Camera.Position;
            float yaw = _player.Yaw, pitch = Math.Clamp(_player.Pitch, -1.5f, 1.5f);

            FlushTourDraft();
            SaveWalkthroughForTour();
            BookmarkRecord record;
            TourStop stop;
            if (bookmark != null)
            {
                record = bookmark;
                stop = BookmarkStop(bookmark);
            }
            else
            {
                float standEye = MathF.Max(_tourSavedEye, 0.1f);
                record = new BookmarkRecord { Name = "This view" };
                stop = new TourStop(eye - new Vector3(0f, 0f, standEye), standEye, yaw, pitch, 0f, 0f);
            }

            BeginTour(new FixedViewTimeline(stop, plan.Seconds), new[] { record }, new TourSettings { ShowUi = false, Loop = false }, null);
            _timeLapse = plan;

            string range = BatchStills.Clock(ClockOf(plan.StartMinutes), colon: true, pad: true) + " → " +
                BatchStills.Clock(ClockOf(plan.EndMinutes), colon: true, pad: true) + (plan.EndMinutes >= 1440.0 ? " (next day)" : string.Empty);
            BeginVideoRecording(width, height, " time-lapse " + SafeFileName(record.Name) + " ",
                $"RECORDING TIME-LAPSE · {width} × {height} · {_videoFps} FPS · {FormatClock(plan.Seconds)} · {range}", _timeLapseStamp);
            _videoFrames = plan.Frames;
            Utilities.Log_Utils.Write($"Time-lapse: {record.Name}, {plan.Day}/{plan.Month}, {range}, {plan.Frames} frames ({plan.MinutesPerSecond:0.0} clock-min per s), stamp {_timeLapseStamp}.");
        }

        /// <summary>Whole clock minutes (0–1439) of a range time (fractional; the next day's wrap round).</summary>
        private static int ClockOf(double minutes) => ((int)Math.Round(minutes) % 1440 + 1440) % 1440;

        /// <summary>
        /// A frame's sun (after the tour pose; before the frame renders): the date and fractional time from the plan,
        /// shadows on. Nothing is recorded for saving (the walkthrough's sun comes back at the end).
        /// </summary>
        private void ApplyTimeLapseSun(int frame)
        {
            double minutes = _timeLapse.At(frame, _timeLapseSun);
            _sunPlaying = false;
            _sun.Time = _timeLapseSun;
            _sun.Enabled = true;
            _sunBlendMinutes = minutes;
            RecomputeSun();
        }

        /// <summary>
        /// While a time-lapse records: the probes re-bake as the light changes (a couple of faces per video frame, nearest
        /// first; a new pass starts when one ends). The camera is the frame's.
        /// </summary>
        private void RefreshTimeLapseProbes()
        {
            if (!ProbesShown || !_renderer.Probes.Ready) { return; }
            if (_renderer.Probes.PendingCount == 0) { _renderer.InvalidateProbes(); }
            string error = _renderer.UpdateReflectionProbes(true, true, _probeHigh ? 256 : 128, Camera.Position, ProbeTemplate(), _groupVisible, Dynamics, _groundZ);
            if (error != null) { Utilities.Log_Utils.Write("Time-lapse probes: " + error); }
        }

        /// <summary>The recording ended (or was cancelled): the sun's clock is the walkthrough's again.</summary>
        private void EndTimeLapse()
        {
            if (_timeLapse == null) { return; }
            _timeLapse = null;
            _sunBlendMinutes = double.NaN;
        }

        /// <summary>
        /// The time-lapse's stamp in the video frame, at the video's scale (its own UI batch, like the HUD).
        /// </summary>
        private void BuildVideoStamp(int w, int h)
        {
            UiBatch windowUi = _ui;
            float windowScale = UiScale;
            _ui = _videoUi;
            UiScale = _videoUiScale;
            try
            {
                QueueSunStamp(w, h);
                _ui.Flush(w, h);
            }
            finally
            {
                _ui = windowUi;
                UiScale = windowScale;
            }
        }

        /// <summary>
        /// The date and time stamp (bottom right; the colour legend sits bottom left): the clock time large (with DST
        /// when on), then the date and the place. Into <see cref="_ui"/> at its scale. Allocation-free.
        /// </summary>
        private void QueueSunStamp(float width, float height)
        {
            FontAtlas f = _ui.Atlas;
            SunTime t = _sun.Time;
            double minutes = !double.IsNaN(_sunBlendMinutes) ? _sunBlendMinutes : t.Minutes;
            int whole = Math.Clamp((int)minutes, 0, 1439);
            bool dst = SolarPosition.IsDaylightSaving(_location, DateTime.Today.Year, t.Month, t.Day, t.DaylightSaving);

            float w = S(240), h = S(70), pad = S(14);
            float x = width - S(24) - w, y = height - S(24) - h;
            _ui.Rect(x, y, w, h, Rgba.Hex(0x000000, 0.45f));
            Text.Clear();
            AppendTwoDigits(whole / 60).Append(':');
            AppendTwoDigits(whole % 60);
            if (dst) { Text.Append(" DST"); }
            _ui.Text(f.Title, x + pad, y + S(8), Text.Span, UiTheme.TEXT);
            Text.Clear().Append(Math.Clamp(t.Day, 1, 31)).Append(' ').Append(MONTHS[Math.Clamp(t.Month, 1, 12) - 1]);
            if (!string.IsNullOrEmpty(_location.Name)) { Text.Append(" · ").Append(_location.Name); }
            _ui.TextWrapped(f.Small, x + pad, y + h - S(24), w - pad * 2f, Text.Span, UiTheme.TEXT_SOFT, maxLines: 1);
        }

        #endregion

        #region MEDIA → VIDEO

        /// <summary>
        /// The time-lapse's settings (VIDEO's right column, under size and rate): the view, the range, the margin or
        /// the start and end, the length, the stamp; then what will be recorded.
        /// </summary>
        /// <returns>The bottom.</returns>
        private float BuildTimeLapseSettings(FontAtlas f, InputState input, float x, float cy, float w)
        {
            cy = SectionLabel(f, x, cy, "SUN TIME-LAPSE");

            // View: ‹ where you are / each bookmark ›
            int count = Bookmarks.Bookmarks.Count;
            if (_timeLapseView >= count) { _timeLapseView = -1; }
            _ui.Text(f.Body, x, cy + S(7), "View", UiTheme.TEXT_SOFT);
            float vx = x + S(70), vw = w - S(70);
            if (SmallButton(f, input, vx, cy, S(30), S(30), "‹")) { _timeLapseView = _timeLapseView <= -1 ? count - 1 : _timeLapseView - 1; }
            if (SmallButton(f, input, vx + vw - S(30), cy, S(30), S(30), "›")) { _timeLapseView = _timeLapseView >= count - 1 ? -1 : _timeLapseView + 1; }
            BookmarkRecord bookmark = TimeLapseBookmark;
            _ui.TextWrapped(f.Bold, vx + S(42), cy + S(6), vw - S(84), bookmark != null ? bookmark.Name : "Where you are", UiTheme.TEXT, maxLines: 1);
            cy += S(40);

            // Range
            int range = Segmented(f, input, x, cy, w, TIMELAPSE_RANGES, _timeLapseCustom ? 1 : 0);
            _timeLapseCustom = range == 1;
            cy += S(42);
            if (!_timeLapseCustom)
            {
                int d = StepRow(f, input, x, cy, w, "Either side");
                if (d != 0) { _timeLapseMargin = Math.Clamp(_timeLapseMargin + d * 15, 0, TimeLapsePlan.MAX_MARGIN); }
                Text.Clear().Append(_timeLapseMargin).Append(" min");
                StepRowValue(f, x, cy, w);
                cy += S(38);
            }
            else
            {
                int d = StepRow(f, input, x, cy, w, "From");
                if (d != 0) { _timeLapseFrom = ((_timeLapseFrom + d * 15) % 1440 + 1440) % 1440; }
                Text.Clear();
                AppendTwoDigits(_timeLapseFrom / 60).Append(':');
                AppendTwoDigits(_timeLapseFrom % 60);
                StepRowValue(f, x, cy, w);
                cy += S(38);
                d = StepRow(f, input, x, cy, w, "To");
                if (d != 0) { _timeLapseTo = ((_timeLapseTo + d * 15) % 1440 + 1440) % 1440; }
                Text.Clear();
                AppendTwoDigits(_timeLapseTo / 60).Append(':');
                AppendTwoDigits(_timeLapseTo % 60);
                if (_timeLapseTo <= _timeLapseFrom) { Text.Append(" +1"); }
                StepRowValue(f, x, cy, w);
                cy += S(38);
            }
            int ds = StepRow(f, input, x, cy, w, "Length");
            if (ds != 0) { _timeLapseSeconds = Math.Clamp(_timeLapseSeconds + ds * 5, TimeLapsePlan.MIN_SECONDS, TimeLapsePlan.MAX_SECONDS); }
            Text.Clear().Append(_timeLapseSeconds).Append(" s");
            StepRowValue(f, x, cy, w);
            cy += S(40);
            _timeLapseStamp = Checkbox(f, input, x, cy + S(4), w, "Date and time stamp in the video", _timeLapseStamp);
            cy += S(36);

            // What will be recorded
            cy = SectionLabel(f, x, cy, "THE TIME-LAPSE");
            TimeLapsePlan plan = TimeLapsePreview(out string problem);
            if (plan == null)
            {
                cy += _ui.TextWrapped(f.Small, x, cy, w, problem ?? string.Empty, UiTheme.MEASURE_LABEL, maxLines: 2) + S(8);
            }
            else
            {
                Text.Clear().Append(plan.Day).Append(' ').Append(MONTHS[Math.Clamp(plan.Month, 1, 12) - 1]).Append(" · ");
                AppendSunClock(plan.StartMinutes).Append(" → ");
                AppendSunClock(plan.EndMinutes);
                if (plan.EndMinutes >= 1440.0) { Text.Append(" next day"); }
                _ui.Text(f.Mono, x, cy, Text.Span, UiTheme.TEXT_SOFT);
                cy += S(22);
                VideoFrameSize(out int vw2, out int vh2);
                Text.Clear().Append(vw2).Append(" × ").Append(vh2).Append(" · ").Append(_videoFps).Append(" fps · ");
                AppendClock(plan.Seconds).Append(" · ").AppendGrouped(plan.Frames).Append(" frames");
                _ui.Text(f.Mono, x, cy, Text.Span, UiTheme.TEXT_SOFT);
                cy += S(22);
                Text.Clear();
                if (plan.FromSun)
                {
                    Text.Append("Sunrise ");
                    AppendSunClock(plan.Sunrise).Append(" · sunset ");
                    AppendSunClock(plan.Sunset).Append(" · ");
                }
                Text.Append((long)Math.Round(plan.MinutesPerSecond)).Append(" min of sun a second");
                _ui.TextWrapped(f.Small, x, cy, w, Text.Span, UiTheme.TEXT_MUTED, maxLines: 1);
                cy += S(22);
            }
            string dateFrom = bookmark?.Sun != null ? "The date is the bookmark's sun (it keeps one)." : "The date is the sun panel's (Shift+O).";
            cy += _ui.TextWrapped(f.Small, x, cy, w, dateFrom, UiTheme.TEXT_FAINT, maxLines: 2) + S(4);
            cy += _ui.TextWrapped(f.Small, x, cy, w,
                "The camera holds still while shadows, sky, moon and stars follow the clock; clouds drift faster. Rain, snow and lightning keep their real pace (never more than 3 flashes a second). Reflections re-bake as the light changes.",
                UiTheme.TEXT_FAINT, maxLines: 5) + S(10);
            if (_videoLastPath != null)
            {
                cy = SectionLabel(f, x, cy, "LAST VIDEO");
                cy += _ui.TextWrapped(f.Small, x, cy, w, Path.GetFileName(_videoLastPath), UiTheme.TEXT_MUTED, maxLines: 2) + S(8);
            }
            return cy;
        }

        /// <summary>
        /// A label with − / + on the right (the value goes between them: <see cref="StepRowValue"/>, from <see cref="Text"/>).
        /// </summary>
        /// <returns>−1, 0 or +1 (this frame's click).</returns>
        private int StepRow(FontAtlas f, InputState input, float x, float y, float w, string label)
        {
            _ui.Text(f.Body, x, y + S(7), label, UiTheme.TEXT_SOFT);
            float fx = x + w - S(198);
            int delta = 0;
            if (SmallButton(f, input, fx, y, S(40), S(30), "−")) { delta = -1; }
            if (SmallButton(f, input, fx + S(158), y, S(40), S(30), "+")) { delta = 1; }
            return delta;
        }

        /// <summary><see cref="Text"/> centred between a <see cref="StepRow"/>'s buttons.</summary>
        private void StepRowValue(FontAtlas f, float x, float y, float w) =>
            _ui.TextCentred(f.Bold, x + w - S(99), y + S(6), Text.Span, UiTheme.TEXT);

        /// <summary>"hh:mm" of fractional clock minutes (the next day's wrap round) into <see cref="Text"/>.</summary>
        private TextBuffer AppendSunClock(double minutes)
        {
            int whole = ClockOf(minutes);
            AppendTwoDigits(whole / 60).Append(':');
            return AppendTwoDigits(whole % 60);
        }

        #endregion
    }
}
