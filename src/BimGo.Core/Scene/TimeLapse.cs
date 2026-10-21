using System.Globalization;
using System.Text;
using BimGo.Format;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Round X-A: sunrise and sunset on a date (the clock times the sun's centre crosses −0.833°: refraction and the
    /// disc's radius, as almanacs give them). Pure maths over <see cref="SolarPosition.Compute"/>, so daylight saving
    /// (the tick, or the place's zone) is handled the same way as the sun itself.
    /// </summary>
    public static class SunEvents
    {
        /// <summary>The sun's altitude at sunrise / sunset (degrees).</summary>
        public const double HORIZON_DEGREES = -0.833;

        private const int SCAN_STEP = 10; // minutes

        /// <summary>
        /// Finds the first sunrise of the day and the first sunset after it.
        /// </summary>
        /// <param name="sunrise">Clock minutes after midnight (fractional), or NaN.</param>
        /// <param name="sunset">Clock minutes after midnight (fractional), or NaN.</param>
        /// <returns>False when the sun doesn't both rise and set that day (polar day or night, or it sets after midnight).</returns>
        public static bool Find(GeoLocation location, int year, int month, int day, bool daylightSaving, out double sunrise, out double sunset)
        {
            sunrise = sunset = double.NaN;
            double previous = Altitude(location, year, month, day, 0.0, daylightSaving);
            for (int m = SCAN_STEP; m <= 1440; m += SCAN_STEP)
            {
                double altitude = Altitude(location, year, month, day, m, daylightSaving);
                if (double.IsNaN(sunrise))
                {
                    if (previous < HORIZON_DEGREES && altitude >= HORIZON_DEGREES)
                    {
                        sunrise = Refine(location, year, month, day, daylightSaving, m - SCAN_STEP, m, rising: true);
                    }
                }
                else if (previous >= HORIZON_DEGREES && altitude < HORIZON_DEGREES)
                {
                    sunset = Refine(location, year, month, day, daylightSaving, m - SCAN_STEP, m, rising: false);
                    break;
                }
                previous = altitude;
            }
            if (double.IsNaN(sunrise) || double.IsNaN(sunset))
            {
                sunrise = sunset = double.NaN;
                return false;
            }
            return true;
        }

        /// <summary>The sun's altitude in degrees at a clock time.</summary>
        public static double Altitude(GeoLocation location, int year, int month, int day, double minutes, bool daylightSaving)
        {
            SolarPosition.Compute(location, year, month, day, minutes, daylightSaving, out double altitude, out _);
            return altitude * 180.0 / Math.PI;
        }

        private static double Refine(GeoLocation location, int year, int month, int day, bool daylightSaving, double low, double high, bool rising)
        {
            for (int i = 0; i < 24; i++)
            {
                double mid = (low + high) * 0.5;
                bool above = Altitude(location, year, month, day, mid, daylightSaving) >= HORIZON_DEGREES;
                if (above == rising) { high = mid; }
                else { low = mid; }
            }
            return (low + high) * 0.5;
        }
    }

    /// <summary>
    /// Round X-A: a sun time-lapse laid out frame by frame: a clock range on one date (sunrise → sunset with a margin
    /// either side, or a custom start and end; an end at or before the start runs on into the next day) spread evenly
    /// over a clip of a set length. The App sets the sun from <see cref="At"/> for each video frame.
    /// </summary>
    public sealed class TimeLapsePlan
    {
        /// <summary>Shortest / longest clip (s).</summary>
        public const int MIN_SECONDS = 5, MAX_SECONDS = 120;

        /// <summary>Largest margin before sunrise / after sunset (minutes).</summary>
        public const int MAX_MARGIN = 120;

        private TimeLapsePlan() { }

        /// <summary>The year, month and day the range starts on.</summary>
        public int Year { get; private init; }

        /// <summary>Month 1–12.</summary>
        public int Month { get; private init; }

        /// <summary>Day of the month.</summary>
        public int Day { get; private init; }

        /// <summary>The daylight saving tick (the place's zone decides instead when it knows).</summary>
        public bool DaylightSaving { get; private init; }

        /// <summary>First frame's clock time (minutes after the start date's midnight, fractional).</summary>
        public double StartMinutes { get; private init; }

        /// <summary>Last frame's clock time (minutes after the start date's midnight; above 1440 runs into the next day).</summary>
        public double EndMinutes { get; private init; }

        /// <summary>True when the range came from sunrise and sunset.</summary>
        public bool FromSun { get; private init; }

        /// <summary>Sunrise / sunset (clock minutes) when <see cref="FromSun"/>, else NaN.</summary>
        public double Sunrise { get; private init; } = double.NaN;

        /// <summary>See <see cref="Sunrise"/>.</summary>
        public double Sunset { get; private init; } = double.NaN;

        /// <summary>Frames in the clip (at least 2).</summary>
        public int Frames { get; private init; }

        /// <summary>Frames per second.</summary>
        public int Fps { get; private init; }

        /// <summary>The clip's length (s).</summary>
        public float Seconds => Frames / (float)Fps;

        /// <summary>Clock minutes the sun moves per second of video.</summary>
        public double MinutesPerSecond => (EndMinutes - StartMinutes) / Math.Max(Seconds, 1e-3f);

        /// <summary>
        /// Lays out a time-lapse.
        /// </summary>
        /// <param name="location">Where (the sun's place).</param>
        /// <param name="year">The year (leap days).</param>
        /// <param name="date">The date and the daylight saving tick (its clock time is ignored).</param>
        /// <param name="custom">False: sunrise → sunset with <paramref name="marginMinutes"/> either side; true: the custom range.</param>
        /// <param name="fromMinutes">Custom start (clock minutes 0–1439).</param>
        /// <param name="toMinutes">Custom end (0–1439); at or before the start it is the next day's.</param>
        /// <param name="marginMinutes">Before sunrise and after sunset (0–<see cref="MAX_MARGIN"/>).</param>
        /// <param name="seconds">Clip length (s).</param>
        /// <param name="fps">Frames per second (30 or 60 in BimGo).</param>
        /// <param name="problem">Why there is no plan (the sun doesn't rise and set that day), or null.</param>
        /// <returns>The plan, or null.</returns>
        public static TimeLapsePlan Create(GeoLocation location, int year, SunTime date, bool custom, int fromMinutes, int toMinutes, int marginMinutes,
            int seconds, int fps, out string problem)
        {
            problem = null;
            year = Math.Clamp(year, 1, 9998);
            SunTime clean = (date ?? new SunTime()).Clamped(year);
            fps = Math.Clamp(fps, 1, 240);
            seconds = Math.Clamp(seconds, MIN_SECONDS, MAX_SECONDS);
            int frames = Math.Max(2, seconds * fps);

            double start, end, sunrise = double.NaN, sunset = double.NaN;
            if (custom)
            {
                start = Math.Clamp(fromMinutes, 0, 1439);
                end = Math.Clamp(toMinutes, 0, 1439);
                if (end <= start) { end += 1440.0; } // overnight
            }
            else
            {
                if (!SunEvents.Find(location, year, clean.Month, clean.Day, clean.DaylightSaving, out sunrise, out sunset))
                {
                    double noon = SunEvents.Altitude(location, year, clean.Month, clean.Day, 12 * 60, clean.DaylightSaving);
                    problem = noon > 0.0
                        ? "The sun doesn't set on this date here (midnight sun): pick Custom for the range"
                        : "The sun doesn't rise on this date here (polar night): pick Custom for the range";
                    return null;
                }
                int margin = Math.Clamp(marginMinutes, 0, MAX_MARGIN);
                start = Math.Max(0.0, sunrise - margin);
                end = Math.Min(1439.99, sunset + margin);
            }

            return new TimeLapsePlan
            {
                Year = year,
                Month = clean.Month,
                Day = clean.Day,
                DaylightSaving = clean.DaylightSaving,
                StartMinutes = start,
                EndMinutes = end,
                FromSun = !custom,
                Sunrise = sunrise,
                Sunset = sunset,
                Frames = frames,
                Fps = fps
            };
        }

        /// <summary>A frame's clock time from the start date's midnight (fractional; frames outside the clip are clamped).</summary>
        public double MinutesAt(int frame)
        {
            frame = Math.Clamp(frame, 0, Frames - 1);
            return StartMinutes + (EndMinutes - StartMinutes) * frame / (Frames - 1);
        }

        /// <summary>
        /// A frame's date and time: fills <paramref name="into"/> (month, day: the next day after midnight; whole
        /// minutes; the daylight saving tick). Allocation-free.
        /// </summary>
        /// <returns>The clock time on that day in fractional minutes (0 – 1439.99), so the sun moves smoothly.</returns>
        public double At(int frame, SunTime into)
        {
            double minutes = MinutesAt(frame);
            int month = Month, day = Day;
            if (minutes >= 1440.0)
            {
                minutes -= 1440.0;
                DateTime next = new DateTime(Year, Month, Math.Clamp(Day, 1, DateTime.DaysInMonth(Year, Month))).AddDays(1);
                month = next.Month;
                day = next.Day;
            }
            minutes = Math.Clamp(minutes, 0.0, 1439.99);
            into.Month = month;
            into.Day = day;
            into.Minutes = Math.Clamp((int)minutes, 0, 1439);
            into.DaylightSaving = DaylightSaving;
            return minutes;
        }
    }

    /// <summary>
    /// Round X-A: a tour timeline that holds one view for a set time (the sun time-lapse's fixed camera). The stop's
    /// state applies once; nothing fades or blends.
    /// </summary>
    public sealed class FixedViewTimeline : ITourTimeline
    {
        private readonly TourStop _stop;

        /// <summary>Creates the timeline.</summary>
        /// <param name="stop">Where the camera stands and looks (its hold and swivel are ignored).</param>
        /// <param name="seconds">How long it lasts.</param>
        public FixedViewTimeline(TourStop stop, float seconds)
        {
            _stop = stop;
            Duration = float.IsFinite(seconds) ? MathF.Max(0f, seconds) : 0f;
        }

        /// <inheritdoc/>
        public int Count => 1;

        /// <inheritdoc/>
        public float Duration { get; }

        /// <inheritdoc/>
        public float StartOf(int stop) => 0f;

        /// <inheritdoc/>
        public int StopAt(float time) => 0;

        /// <inheritdoc/>
        public TourPose Pose(float time) => new()
        {
            Stop = 0,
            Feet = _stop.Feet,
            EyeHeight = _stop.EyeHeight,
            Yaw = _stop.Yaw,
            Pitch = _stop.Pitch,
            Black = 0f,
            Moving = false,
            Finished = time >= Duration,
            BlendFrom = 0,
            BlendTo = 0,
            BlendT = 1f
        };
    }

    /// <summary>
    /// Round X-A: one entry of a batch's time list: a clock time, on a date of its own or (month 0) the view's.
    /// </summary>
    /// <param name="Month">1–12, or 0 for the view's date.</param>
    /// <param name="Day">Day of the month (with a month).</param>
    /// <param name="Minutes">Clock minutes after midnight (0–1439).</param>
    public readonly record struct BatchTime(int Month, int Day, int Minutes)
    {
        /// <summary>True when the entry carries its own date.</summary>
        public bool HasDate => Month >= 1;
    }

    /// <summary>
    /// Round X-A: batch stills: the time list (typed as "9:00 12:00 15:00", a date such as "21/6" applying to the times
    /// after it), the shots' names, and the CSV index written beside them.
    /// </summary>
    public static class BatchStills
    {
        /// <summary>Most entries in a time list.</summary>
        public const int MAX_TIMES = 48;

        /// <summary>The list a new machine starts with.</summary>
        public const string DEFAULT_TIMES = "9:00 12:00 15:00";

        private static readonly string[] MONTHS = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        /// <summary>
        /// Reads a time list. Separators: spaces, commas, semicolons. Times: <c>9</c>, <c>9:00</c>, <c>0930</c>,
        /// <c>15:30</c>, <c>3pm</c>, <c>3:30pm</c>. Dates (day / month, a year is ignored): <c>21/6</c>, <c>21/6/2026</c>,
        /// <c>21.6</c>; a date applies to the times after it (until the next date); times before any date use the view's
        /// date. Repeats are dropped (first kept); at most <see cref="MAX_TIMES"/>.
        /// </summary>
        /// <param name="text">The typed list.</param>
        /// <param name="year">The year (to check a 29 Feb).</param>
        /// <param name="error">What couldn't be read, or null.</param>
        /// <returns>The entries in order (empty with an error when nothing could be read).</returns>
        public static List<BatchTime> Parse(string text, int year, out string error)
        {
            error = null;
            var result = new List<BatchTime>();
            int month = 0, day = 0;
            bool dateWaiting = false;
            string[] tokens = (text ?? string.Empty).Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in tokens)
            {
                string token = raw.Trim();
                if (TryDate(token, year, out int m, out int d))
                {
                    month = m;
                    day = d;
                    dateWaiting = true;
                    continue;
                }
                if (!TryTime(token, out int minutes))
                {
                    error = $"“{token}” isn't a time (9:00, 15:30, 3pm) or a date (21/6)";
                    return new List<BatchTime>();
                }
                dateWaiting = false;
                var entry = new BatchTime(month, month >= 1 ? day : 0, minutes);
                if (!result.Contains(entry))
                {
                    if (result.Count >= MAX_TIMES)
                    {
                        error = $"At most {MAX_TIMES} times";
                        return new List<BatchTime>();
                    }
                    result.Add(entry);
                }
            }
            if (dateWaiting)
            {
                error = "A date needs a time after it (21/6 9:00)";
                return new List<BatchTime>();
            }
            if (result.Count == 0) { error = "No times: type e.g. 9:00 12:00 15:00"; }
            return result;
        }

        /// <summary>The list as it would be typed ("9:00 12:00 21/6 9:00"): dates only where they change.</summary>
        public static string Format(IReadOnlyList<BatchTime> times)
        {
            var text = new StringBuilder();
            int month = 0, day = 0;
            foreach (BatchTime time in times ?? Array.Empty<BatchTime>())
            {
                if (time.Month != month || time.Day != day)
                {
                    // (Parse puts the view's-date times first: a date carries on to the end)
                    if (time.Month >= 1)
                    {
                        if (text.Length > 0) { text.Append(' '); }
                        text.Append(time.Day.ToString(CultureInfo.InvariantCulture)).Append('/').Append(time.Month.ToString(CultureInfo.InvariantCulture));
                    }
                    month = time.Month;
                    day = time.Day;
                }
                if (text.Length > 0) { text.Append(' '); }
                text.Append(Clock(time.Minutes, colon: true));
            }
            return text.ToString();
        }

        /// <summary>A shot's file name without the extension: "03 Entry 21 Jun 0930" (the view's number in the batch).</summary>
        /// <param name="viewNumber">1-based.</param>
        /// <param name="viewName">The view's name (already safe for a file name).</param>
        public static string ShotName(int viewNumber, string viewName, int month, int day, int minutes) =>
            $"{viewNumber:00} {viewName} {day} {MONTHS[Math.Clamp(month, 1, 12) - 1]} {Clock(minutes, colon: false)}";

        /// <summary>The CSV index's header line.</summary>
        public const string CSV_HEADER = "File,View,Date,Time,Daylight saving,Sun altitude (deg),Sun azimuth (deg from true north)";

        /// <summary>One CSV line for a shot (invariant culture; text quoted when needed).</summary>
        public static string CsvRow(string file, string view, int month, int day, int minutes, bool daylightSaving, double altitude, double azimuth) =>
            string.Join(",",
                Csv(file),
                Csv(view),
                Csv($"{day} {MONTHS[Math.Clamp(month, 1, 12) - 1]}"),
                Clock(minutes, colon: true, pad: true),
                daylightSaving ? "yes" : "no",
                altitude.ToString("0.0", CultureInfo.InvariantCulture),
                (((azimuth % 360.0) + 360.0) % 360.0).ToString("0.0", CultureInfo.InvariantCulture));

        /// <summary>The whole index (header + rows, CRLF).</summary>
        public static string Csv(IEnumerable<string> rows)
        {
            var text = new StringBuilder(CSV_HEADER).Append("\r\n");
            foreach (string row in rows ?? Array.Empty<string>()) { text.Append(row).Append("\r\n"); }
            return text.ToString();
        }

        /// <summary>"9:05" / "09:05" (pad) / "0905" (no colon).</summary>
        public static string Clock(int minutes, bool colon, bool pad = false)
        {
            minutes = Math.Clamp(minutes, 0, 1439);
            int h = minutes / 60, m = minutes % 60;
            string hours = colon && !pad ? h.ToString(CultureInfo.InvariantCulture) : h.ToString("00", CultureInfo.InvariantCulture);
            return hours + (colon ? ":" : string.Empty) + m.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static bool TryDate(string token, int year, out int month, out int day)
        {
            month = day = 0;
            char separator = token.Contains('/') ? '/' : token.Contains('.') ? '.' : '\0';
            if (separator == '\0') { return false; }
            string[] parts = token.Split(separator);
            if (parts.Length < 2 || parts.Length > 3) { return false; }
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out day)) { return false; }
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out month)) { return false; }
            if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)) { return false; }
            if (month < 1 || month > 12) { return false; }
            year = Math.Clamp(year, 1, 9999);
            return day >= 1 && day <= DateTime.DaysInMonth(year, month);
        }

        private static bool TryTime(string token, out int minutes)
        {
            minutes = 0;
            string t = token.ToLowerInvariant();
            int meridiem = 0; // 1 am, 2 pm
            if (t.EndsWith("am", StringComparison.Ordinal)) { meridiem = 1; t = t[..^2]; }
            else if (t.EndsWith("pm", StringComparison.Ordinal)) { meridiem = 2; t = t[..^2]; }
            if (t.Length == 0) { return false; }

            int hours, mins = 0;
            int colon = t.IndexOf(':');
            if (colon >= 0)
            {
                if (!int.TryParse(t[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) { return false; }
                string right = t[(colon + 1)..];
                if (right.Length != 2 || !int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out mins)) { return false; }
            }
            else if (t.Length >= 3 && meridiem == 0)
            {
                // 0930 / 930
                if (t.Length > 4 || !int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int packed)) { return false; }
                hours = packed / 100;
                mins = packed % 100;
            }
            else if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out hours)) { return false; }

            if (mins < 0 || mins > 59) { return false; }
            if (meridiem != 0)
            {
                if (hours < 1 || hours > 12) { return false; }
                hours %= 12;
                if (meridiem == 2) { hours += 12; }
            }
            if (hours < 0 || hours > 23) { return false; }
            minutes = hours * 60 + mins;
            return true;
        }
    }
}
