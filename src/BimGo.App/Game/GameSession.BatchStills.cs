using System.Text;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round X-A: batch stills (MEDIA → PHOTO → RENDER BATCH STILLS): every bookmark (or the tour's stops) × a list of
    /// times, one shot each with the PHOTO settings (still or 360°, size, format, exposure, grid lines).
    /// <list type="bullet">
    /// <item>Plays the views through the tour player like PHOTOS OF ALL KEYS: each bookmark's cut, hidden items, eye
    /// height and colour profile show; then for each time the sun is set (on the view's date, the bookmark's sun if it
    /// keeps one, else the sun panel's; or a date typed before the times), the reflection probes re-bake when they
    /// show, a few frames settle, and the shot is taken.</item>
    /// <item>Saved as "01 &lt;view&gt; 21 Jun 0900" in a folder of its own under Pictures\BimGo with <c>index.csv</c>
    /// (file, view, date, time, daylight saving, the sun's altitude and azimuth).</item>
    /// <item>An optional date / time stamp in stills (bottom right; not in 360s). Esc stops; the walkthrough comes back.</item>
    /// </list>
    /// The time list, views choice and stamp are per-machine settings.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>Frames to wait after the sun is set before probes are re-baked (the lighting reaches the renderer).</summary>
        private const int BATCH_PROBE_DELAY = 2;

        /// <summary>Longest wait for the probes to re-bake for one shot (frames).</summary>
        private const int BATCH_PROBE_MAX_FRAMES = 900;

        /// <summary>The longest time list text.</summary>
        private const int MAX_BATCH_TIMES_TEXT = 200;

        private static readonly string[] BATCH_VIEW_OPTIONS = { "All bookmarks", "Tour stops" };

        // Settings (per machine) and the list as read (re-read when the text changes)
        private string _batchTimes = BatchStills.DEFAULT_TIMES;
        private bool _batchTourStops;
        private bool _batchStamp;
        private int _batchTimeCount;
        private string _batchTimesError;

        private readonly ScrollRegion _photoRightScroll = new();

        /// <summary>A running batch (null when none).</summary>
        private sealed class StillsBatch
        {
            public BookmarkRecord[] Views;
            public List<BatchTime> Times;
            public SunTime BaseSun;       // the sun panel's date when it started (views without a sun of their own)
            public string Folder;
            public bool Stamp;
            public int View = -1, Time = -1; // the shot being made
            public int Shot;              // 1-based, for the progress
            public int Total;
            public int ProbeDelay, ProbeWait, Settle;
            public bool Shooting;
            public string PendingRow;     // its index line, added once it was taken
            public int Taken;
            public bool Done;
            public readonly List<string> Rows = new();
        }

        private StillsBatch _stillsBatch;

        /// <summary>The next shot carries the date / time stamp (a batch still).</summary>
        private bool _photoStampOn;

        #endregion

        #region Settings

        private void ReadBatchStillsSettings(LaunchSettings settings)
        {
            _batchTimes = string.IsNullOrWhiteSpace(settings.BatchTimes) ? BatchStills.DEFAULT_TIMES : settings.BatchTimes.Trim();
            _batchTourStops = settings.BatchTourStops;
            _batchStamp = settings.BatchStamp;
            RereadBatchTimes();
        }

        private void WriteBatchStillsSettings(LaunchSettings settings)
        {
            settings.BatchTimes = _batchTimes;
            settings.BatchTourStops = _batchTourStops;
            settings.BatchStamp = _batchStamp;
        }

        /// <summary>Counts the time list (and keeps its error) once, when it changes.</summary>
        private void RereadBatchTimes()
        {
            List<BatchTime> times = BatchStills.Parse(_batchTimes, DateTime.Today.Year, out _batchTimesError);
            _batchTimeCount = times.Count;
        }

        /// <summary>EDIT… committed: a readable list replaces the old one (written back as read); else a notice.</summary>
        private void CommitBatchTimes(string text)
        {
            List<BatchTime> times = BatchStills.Parse(text, DateTime.Today.Year, out string error);
            if (error != null)
            {
                _mediaNotice = "Times not changed: " + error;
                Sound.Play(SoundId.Error);
                return;
            }
            _batchTimes = BatchStills.Format(times);
            RereadBatchTimes();
            _mediaNotice = $"{times.Count} time{(times.Count == 1 ? "" : "s")}: {_batchTimes}";
        }

        /// <summary>The views a batch shoots: every bookmark, or those the tour plays.</summary>
        private int BatchViewCount()
        {
            int count = 0;
            foreach (BookmarkRecord record in Bookmarks.Bookmarks)
            {
                if (!_batchTourStops || !record.TourSkip) { count++; }
            }
            return count;
        }

        /// <summary>
        /// A bookmark's view laid out as the tour lays its stops out (its eye height, a crouched one lower), held still.
        /// </summary>
        private TourStop BookmarkStop(BookmarkRecord record)
        {
            float crouchRatio = _player.Controller.StandEye > 0.01f ? _player.Controller.CrouchEye / _player.Controller.StandEye : 0.6f;
            float eye = (record.EyeHeight ?? _tourSavedEye) * (record.Crouched && !record.Flying ? crouchRatio : 1f);
            return new TourStop(record.Local, eye, record.Yaw, Math.Clamp(record.Pitch, -1.5f, 1.5f), 1f, 0f);
        }

        #endregion

        #region Run

        /// <summary>
        /// RENDER BATCH STILLS: the views through the tour player, each time on each, one shot per pair.
        /// </summary>
        private void StartStillsBatch()
        {
            if (_tourActive || _video != null || _player == null) { return; }
            List<BatchTime> times = BatchStills.Parse(_batchTimes, DateTime.Today.Year, out string error);
            if (error != null)
            {
                _mediaNotice = "Batch times: " + error;
                Sound.Play(SoundId.Error);
                return;
            }
            var views = new List<BookmarkRecord>();
            foreach (BookmarkRecord record in Bookmarks.Bookmarks)
            {
                if (!_batchTourStops || !record.TourSkip) { views.Add(record); }
            }
            if (views.Count == 0)
            {
                _mediaNotice = Bookmarks.Bookmarks.Count == 0
                    ? $"No bookmarks yet: press {Key(InputAction.AddBookmark)} at each view to shoot"
                    : "Every bookmark is left out of the tour: pick All bookmarks (or tick them back in MEDIA → TOUR)";
                Sound.Play(SoundId.Error);
                return;
            }
            FlushTourDraft();

            string model = SafeFileName(Path.GetFileNameWithoutExtension(DocumentName));
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string folder = Path.Combine(ScreenshotFolder, $"{model} batch {stamp}");

            SaveWalkthroughForTour();
            var stops = new List<TourStop>(views.Count);
            foreach (BookmarkRecord record in views) { stops.Add(BookmarkStop(record)); }
            BeginTour(new TourTimeline(stops, TourTransition.Fade, 0f, 1f), views.ToArray(), new TourSettings { ShowUi = false, Loop = false }, null);
            _stillsBatch = new StillsBatch
            {
                Views = views.ToArray(),
                Times = times,
                BaseSun = _tourSavedSun.Copy(),
                Folder = folder,
                Stamp = _batchStamp,
                Total = views.Count * times.Count
            };
            _toast = null;
            Utilities.Log_Utils.Write($"Batch stills: {views.Count} view(s) × {times.Count} time(s), {(_photoKind == 0 ? "stills" : "360s")} into {folder}.");
        }

        /// <summary>
        /// Per frame while the batch runs (from <see cref="UpdateTour"/>): Esc stops; else the next view and time, the
        /// probes' re-bake, the settle frames, the shot; at the end, the saves are waited for.
        /// </summary>
        private void UpdateStillsBatch(InputState input)
        {
            StillsBatch batch = _stillsBatch;
            if (input.Pressed(InputAction.Back))
            {
                FinishStillsBatch(cancelled: true);
                return;
            }
            if (_photoShotIn > 0) { return; } // the shot renders this frame

            if (batch.Shooting)
            {
                batch.Shooting = false;
                batch.Taken++;
                batch.Rows.Add(batch.PendingRow);
                _photoNamed = null;
                _photoStampOn = false;
            }
            if (batch.Done)
            {
                if (Volatile.Read(ref _photoSaving) == 0) { FinishStillsBatch(cancelled: false); }
                return;
            }

            // The probes: once the new light has reached the renderer, a re-bake now (not after the usual settle)
            if (batch.ProbeDelay > 0)
            {
                if (--batch.ProbeDelay == 0)
                {
                    _probeKeyChangedAt = -1f;
                    _renderer.InvalidateProbes();
                    batch.ProbeWait = BATCH_PROBE_MAX_FRAMES;
                }
                return;
            }
            if (batch.ProbeWait > 0)
            {
                if (_renderer.Probes.PendingCount == 0 || --batch.ProbeWait == 0)
                {
                    batch.ProbeWait = 0;
                    batch.Settle = KEY_PHOTO_SETTLE_FRAMES;
                }
                return;
            }
            if (batch.Settle > 0)
            {
                if (--batch.Settle == 0)
                {
                    // The shot, named by its view, date and time; its index line waits until it is taken
                    BookmarkRecord view = batch.Views[batch.View];
                    SunTime sun = _sun.Time;
                    string name = BatchStills.ShotName(batch.View + 1, SafeFileName(view.Name), sun.Month, sun.Day, sun.Minutes);
                    string extension = _photoKind == 1 || _photoJpeg ? ".jpg" : ".png";
                    bool dst = SolarPosition.IsDaylightSaving(_location, DateTime.Today.Year, sun.Month, sun.Day, sun.DaylightSaving);
                    batch.PendingRow = BatchStills.CsvRow(name + extension, view.Name, sun.Month, sun.Day, sun.Minutes, dst, _sunAltitude, _sunAzimuth);
                    _photoNamed = Path.Combine(batch.Folder, name);
                    _photoStampOn = batch.Stamp;
                    _photoShotIn = 1;
                    batch.Shooting = true;
                }
                return;
            }

            // On to the next shot (waiting while earlier photos are still being saved)
            if (Volatile.Read(ref _photoSaving) >= KEY_PHOTO_MAX_SAVING) { return; }
            int next = batch.View, time = batch.Time + 1;
            if (next < 0 || time >= batch.Times.Count)
            {
                next++;
                time = 0;
            }
            if (next >= batch.Views.Length || next >= _tour.Count)
            {
                batch.Done = true;
                return;
            }
            if (next != batch.View)
            {
                batch.View = next;
                _tourTime = _tour.StartOf(next);
                EvaluateTour();
                UpdateRoom();
            }
            batch.Time = time;
            batch.Shot++;

            // The sun for this time on this view's date (a typed date wins), shadows on
            BatchTime entry = batch.Times[time];
            SunTime date = batch.Views[next].Sun ?? batch.BaseSun;
            EndTourSunBlend();
            SetSunQuiet(new SunTime
            {
                Month = entry.HasDate ? entry.Month : date.Month,
                Day = entry.HasDate ? entry.Day : date.Day,
                Minutes = entry.Minutes,
                DaylightSaving = date.DaylightSaving
            }, shadows: true);

            if (ProbesShown && _renderer.Probes.Ready) { batch.ProbeDelay = BATCH_PROBE_DELAY; }
            else { batch.Settle = KEY_PHOTO_SETTLE_FRAMES; }
        }

        /// <summary>
        /// Ends the batch: the walkthrough comes back; the index is written beside the shots; a toast says how many
        /// went where.
        /// </summary>
        private void FinishStillsBatch(bool cancelled)
        {
            StillsBatch batch = _stillsBatch;
            if (batch == null) { return; }
            _stillsBatch = null;
            _photoNamed = null;
            _photoShotIn = 0;
            _photoStampOn = false;
            StopTour(finished: !cancelled);

            string index = string.Empty;
            if (batch.Rows.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(batch.Folder);
                    File.WriteAllText(Path.Combine(batch.Folder, "index.csv"), BatchStills.Csv(batch.Rows), new UTF8Encoding(true));
                    index = " with index.csv";
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Batch index not written: {ex.Message}");
                    index = " (index.csv couldn't be written)";
                }
            }
            string folder = Path.GetFileName(batch.Folder);
            Toast(batch.Taken == 0
                ? "Batch stills stopped: none taken"
                : cancelled
                    ? $"Batch stills stopped: {batch.Taken} of {batch.Total} saved in “{folder}”{index} (Pictures › BimGo)"
                    : $"{batch.Taken} still{(batch.Taken == 1 ? "" : "s")} saved in “{folder}”{index} (Pictures › BimGo)", 6f);
            Sound.Play(cancelled ? SoundId.UiClick : SoundId.Commit);
            Utilities.Log_Utils.Write($"Batch stills {(cancelled ? "stopped" : "done")}: {batch.Taken} of {batch.Total} taken.");
        }

        /// <summary>The batch's progress over the view (not in the photos).</summary>
        private void BuildStillsBatchOverlay(FontAtlas f, int width, int height)
        {
            StillsBatch batch = _stillsBatch;
            Text.Clear().Append("BATCH STILLS · ").Append(Math.Clamp(batch.Shot, 0, batch.Total)).Append(" / ").Append(batch.Total);
            if (batch.Done) { Text.Append(" · SAVING…"); }
            else if (batch.ProbeWait > 0 || batch.ProbeDelay > 0) { Text.Append(" · REFLECTIONS…"); }
            float w = MathF.Min(S(560), width - S(40)), h = S(64);
            float x = MathF.Round((width - w) * 0.5f), y = height - S(24) - h;
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.ACCENT);
            _ui.Text(f.Small, x + S(14), y + S(12), Text.Span, UiTheme.ACCENT, S(1.4f));

            Text.Clear();
            if (batch.View >= 0 && batch.View < batch.Views.Length)
            {
                SunTime sun = _sun.Time;
                Text.Append(batch.Views[batch.View].Name).Append(" · ").Append(sun.Day).Append(' ').Append(MONTHS[Math.Clamp(sun.Month, 1, 12) - 1]).Append(' ');
                AppendTwoDigits(sun.Minutes / 60).Append(':');
                AppendTwoDigits(sun.Minutes % 60);
            }
            _ui.TextWrapped(f.Bold, x + S(14), y + S(32), w - S(150), Text.Span, UiTheme.TEXT, maxLines: 1);
            _ui.TextRight(f.Small, x + w - S(14), y + S(36), "Esc stops", UiTheme.TEXT_MUTED);
            float fraction = batch.Total > 0 ? (batch.Shot - (batch.Done ? 0f : 0.5f)) / batch.Total : 1f;
            _ui.Rect(x + S(14), y + h - S(8), (w - S(28)) * Math.Clamp(fraction, 0f, 1f), S(3), UiTheme.ACCENT);
        }

        #endregion

        #region MEDIA → PHOTO

        /// <summary>
        /// PHOTO's batch section (right column, under PHOTO MODE): the views, the time list (EDIT…), the stamp, and how
        /// many shots it makes. RENDER BATCH STILLS is in the footer.
        /// </summary>
        /// <returns>The bottom.</returns>
        private float BuildBatchStillsSettings(FontAtlas f, InputState input, float x, float cy, float w)
        {
            cy = SectionLabel(f, x, cy, "BATCH STILLS");
            int views = Segmented(f, input, x, cy, w, BATCH_VIEW_OPTIONS, _batchTourStops ? 1 : 0);
            _batchTourStops = views == 1;
            cy += S(42);

            // The time list: shown as read, EDIT… opens the text box
            float editW = S(70), boxX = x + S(60), boxW = w - S(60) - editW - S(8);
            _ui.Text(f.Body, x, cy + S(7), "Times", UiTheme.TEXT_SOFT);
            _ui.Rect(boxX, cy, boxW, S(30), UiTheme.CONTROL);
            _ui.Outline(boxX, cy, boxW, S(30), MathF.Max(1f, UiScale), _batchTimesError != null ? UiTheme.MEASURE_LABEL : UiTheme.CONTROL_BORDER);
            _ui.TextWrapped(f.Mono, boxX + S(8), cy + S(7), boxW - S(16), _batchTimes, UiTheme.TEXT, maxLines: 1);
            if (SmallButton(f, input, x + w - editW, cy, editW, S(30), "EDIT…"))
            {
                BeginTextEdit("BATCH TIMES (9:00 12:00 15:00 · 21/6 9:00 FOR ANOTHER DATE)", _batchTimes, MAX_BATCH_TIMES_TEXT, CommitBatchTimes);
            }
            cy += S(38);
            if (_batchTimesError != null)
            {
                cy += _ui.TextWrapped(f.Small, x, cy, w, _batchTimesError, UiTheme.MEASURE_LABEL, maxLines: 2) + S(6);
            }
            cy += _ui.TextWrapped(f.Small, x, cy, w,
                "Times are on each view's date (the bookmark's sun, else the sun panel's); a date such as 21/6 before times shoots those on that day.",
                UiTheme.TEXT_FAINT, maxLines: 3) + S(8);
            _batchStamp = Checkbox(f, input, x, cy + S(4), w, "Date and time stamp (stills)", _batchStamp);
            cy += S(34);

            int viewCount = BatchViewCount();
            Text.Clear().Append(viewCount).Append(viewCount == 1 ? " view × " : " views × ").Append(_batchTimeCount)
                .Append(_batchTimeCount == 1 ? " time = " : " times = ").AppendGrouped((long)viewCount * _batchTimeCount).Append(" shots");
            _ui.Text(f.Mono, x, cy, Text.Span, UiTheme.TEXT_SOFT);
            cy += S(24);
            cy += _ui.TextWrapped(f.Small, x, cy, w,
                "With the photo settings on the left (still or 360°, size, format, exposure). Saved in a folder of their own under Pictures\\BimGo with index.csv (view, date, time, the sun's position). Reflections re-bake for each time; Esc stops.",
                UiTheme.TEXT_FAINT, maxLines: 5) + S(8);
            return cy;
        }

        /// <summary>RENDER BATCH STILLS can start (views and a readable time list).</summary>
        private bool CanStartStillsBatch => _batchTimesError == null && _batchTimeCount > 0 && BatchViewCount() > 0;

        #endregion
    }
}
