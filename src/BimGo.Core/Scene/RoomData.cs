using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Round Z-A: one parameter of a room as extracted: the name, the display text as Revit shows it and, where the value
    /// is numeric, a number in the project's display units (so "42.10 m²" carries 42.1); NaN otherwise.
    /// </summary>
    public readonly struct RoomParameter
    {
        /// <summary>Creates a parameter.</summary>
        public RoomParameter(string name, string value, double number)
        {
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
            Number = number;
        }

        /// <summary>The parameter name.</summary>
        public string Name { get; }

        /// <summary>The display text.</summary>
        public string Value { get; }

        /// <summary>The value as a number in display units, or NaN.</summary>
        public double Number { get; }

        /// <summary>True when the value is numeric.</summary>
        public bool IsNumeric => double.IsFinite(Number);
    }

    /// <summary>
    /// Round Z-A: reading numbers out of parameter text (a text parameter holding "45 m²", a value from an older file).
    /// </summary>
    public static class RoomValues
    {
        /// <summary>
        /// The first number in a text ("45", "45.5 m²", "1,234.5", "1 234,5", "-3"), or NaN. A lone comma followed by
        /// exactly three digits is read as a thousands separator, otherwise as the decimal mark; with both marks, the last
        /// one is the decimal mark.
        /// </summary>
        public static double ParseNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return double.NaN; }
            // Start at the first digit, or a sign / point right before one ("Approx. 12" skips the abbreviation's point)
            int i = 0;
            while (i < text.Length && !StartsNumber(text, i)) { i++; }
            if (i >= text.Length) { return double.NaN; }

            Span<char> buffer = stackalloc char[Math.Min(64, text.Length - i + 1)];
            int n = 0;
            if (text[i] == '-' || text[i] == '−') { buffer[n++] = '-'; i++; }
            else if (text[i] == '+') { i++; }
            int commas = 0, dots = 0, lastComma = -1, lastDot = -1;
            for (; i < text.Length && n < buffer.Length; i++)
            {
                char c = text[i];
                if (char.IsDigit(c)) { buffer[n++] = c; }
                else if (c == ',') { commas++; lastComma = n; buffer[n++] = c; }
                else if (c == '.') { dots++; lastDot = n; buffer[n++] = c; }
                else if ((c == ' ' || c == ' ' || c == ' ') && i + 1 < text.Length && char.IsDigit(text[i + 1]) && n > 0 && char.IsDigit(buffer[n - 1])) { continue; } // 1 234
                else if (c == '\'' && i + 1 < text.Length && char.IsDigit(text[i + 1]) && n > 0 && char.IsDigit(buffer[n - 1])) { continue; } // 1'234 (Swiss)
                else { break; }
            }
            while (n > 0 && (buffer[n - 1] == ',' || buffer[n - 1] == '.')) { n--; } // "45." / "45,"

            // Decide which mark is the decimal one
            char decimalMark = '.';
            if (commas > 0 && dots > 0) { decimalMark = lastComma > lastDot ? ',' : '.'; }
            else if (commas == 1 && dots == 0)
            {
                int after = n - lastComma - 1;
                decimalMark = after == 3 ? '.' : ','; // 1,234 → thousands; 45,5 → decimal
            }
            else if (dots > 1 && commas == 0) { decimalMark = ','; } // 1.234.567 → thousands

            Span<char> clean = stackalloc char[n];
            int m = 0;
            for (int k = 0; k < n; k++)
            {
                char c = buffer[k];
                if (c == ',' || c == '.')
                {
                    if (c == decimalMark) { clean[m++] = '.'; }
                    continue;
                }
                clean[m++] = c;
            }
            if (m == 0 || (m == 1 && (clean[0] == '-' || clean[0] == '.'))) { return double.NaN; }
            return double.TryParse(clean[..m], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)
                ? value : double.NaN;
        }

        /// <summary>A digit, or a sign or point followed by a digit (a sign may be followed by a point and a digit).</summary>
        private static bool StartsNumber(string text, int i)
        {
            char c = text[i];
            if (char.IsDigit(c)) { return true; }
            bool next = i + 1 < text.Length && char.IsDigit(text[i + 1]);
            if (c == '.') { return next; }
            if (c != '-' && c != '+' && c != '−') { return false; }
            return next || (i + 2 < text.Length && text[i + 1] == '.' && char.IsDigit(text[i + 2]));
        }
    }

    /// <summary>Round Z-A: how a room's value is compared with its target.</summary>
    public enum RoomCheckRule
    {
        /// <summary>Within ± the tolerance of the target.</summary>
        Within,

        /// <summary>At least the target (less the tolerance).</summary>
        AtLeast,

        /// <summary>At most the target (plus the tolerance).</summary>
        AtMost
    }

    /// <summary>Round Z-A: a room's check result.</summary>
    public enum RoomCheckStatus
    {
        /// <summary>No value or no target (grey).</summary>
        None,

        /// <summary>Inside the tolerance (green).</summary>
        Pass,

        /// <summary>Outside the tolerance but within twice it (amber).</summary>
        Near,

        /// <summary>Beyond twice the tolerance (red).</summary>
        Fail
    }

    /// <summary>
    /// Round Z-A: the target check (a room's value against its target parameter, with a tolerance in %).
    /// </summary>
    public static class RoomChecks
    {
        /// <summary>
        /// Compares a value with a target. Green inside the tolerance, amber within twice it, red beyond; grey when either
        /// is missing or the target is zero.
        /// </summary>
        /// <param name="value">The room's value (display units).</param>
        /// <param name="target">The target (same units).</param>
        /// <param name="rule">Within ±, at least or at most.</param>
        /// <param name="tolerancePercent">The tolerance (% of the target, 0 or more).</param>
        /// <param name="differencePercent">(value − target) ÷ |target| × 100, or NaN.</param>
        public static RoomCheckStatus Evaluate(double value, double target, RoomCheckRule rule, double tolerancePercent, out double differencePercent)
        {
            differencePercent = double.NaN;
            if (!double.IsFinite(value) || !double.IsFinite(target) || Math.Abs(target) < 1e-9) { return RoomCheckStatus.None; }
            double tol = double.IsFinite(tolerancePercent) ? Math.Max(0.0, tolerancePercent) : 0.0;
            double d = (value - target) / Math.Abs(target) * 100.0;
            differencePercent = d;
            const double EPS = 1e-9;
            double over = rule switch
            {
                RoomCheckRule.AtLeast => Math.Max(0.0, -d),
                RoomCheckRule.AtMost => Math.Max(0.0, d),
                _ => Math.Abs(d)
            };
            if (over <= tol + EPS) { return RoomCheckStatus.Pass; }
            if (over <= tol * 2.0 + EPS) { return RoomCheckStatus.Near; }
            return RoomCheckStatus.Fail;
        }

        /// <summary>The rule's file name ("within", "atleast", "atmost").</summary>
        public static string RuleName(RoomCheckRule rule) => rule switch
        {
            RoomCheckRule.AtLeast => RoomOverlaySettings.RULE_AT_LEAST,
            RoomCheckRule.AtMost => RoomOverlaySettings.RULE_AT_MOST,
            _ => RoomOverlaySettings.RULE_WITHIN
        };

        /// <summary>The rule from its file name (anything else: within).</summary>
        public static RoomCheckRule ParseRule(string name) => name switch
        {
            RoomOverlaySettings.RULE_AT_LEAST => RoomCheckRule.AtLeast,
            RoomOverlaySettings.RULE_AT_MOST => RoomCheckRule.AtMost,
            _ => RoomCheckRule.Within
        };
    }

    /// <summary>
    /// Round Z-A: where a room's floating tag goes in plan: the inside point farthest from the walls (a sampled "pole of
    /// inaccessibility"), so L-shaped and U-shaped rooms get their tag inside the room rather than at the bounds' middle.
    /// </summary>
    public static class RoomTagPlacement
    {
        /// <summary>
        /// The tag point: a 16 × 16 sample of the bounds, then an 8 × 8 refinement around the best sample. Points nearer
        /// the middle of the bounds win ties. Falls back to the outer loop's vertex average for slivers.
        /// </summary>
        public static Vector2 Place(RoomInfo room)
        {
            if (room?.Loops == null || room.Loops.Length == 0) { return Vector2.Zero; }
            Vector2 min = room.Min, max = room.Max, size = max - min;
            Vector2 middle = (min + max) * 0.5f;
            if (size.X <= 1e-4f || size.Y <= 1e-4f) { return middle; }

            const int COARSE = 16, FINE = 8;
            Vector2 cell = size / COARSE;
            Vector2 best = default;
            float bestScore = float.MinValue;
            bool found = false;
            Search(room, min + cell * 0.5f, cell, COARSE, middle, ref best, ref bestScore, ref found);
            if (found)
            {
                Vector2 fine = cell / FINE;
                Search(room, best - cell * 0.5f + fine * 0.5f, fine, FINE, middle, ref best, ref bestScore, ref found);
                return best;
            }

            // A sliver the samples missed: the outer loop's vertex average if it is inside, else its first vertex
            Vector2[] outer = room.Loops[0];
            Vector2 sum = Vector2.Zero;
            foreach (Vector2 p in outer) { sum += p; }
            Vector2 average = outer.Length > 0 ? sum / outer.Length : middle;
            return room.Contains(average) || outer.Length == 0 ? average : outer[0];
        }

        private static void Search(RoomInfo room, Vector2 start, Vector2 step, int count, Vector2 middle,
            ref Vector2 best, ref float bestScore, ref bool found)
        {
            for (int y = 0; y < count; y++)
            {
                for (int x = 0; x < count; x++)
                {
                    var p = new Vector2(start.X + step.X * x, start.Y + step.Y * y);
                    if (!room.Contains(p)) { continue; }
                    // Clearance first; a small pull to the middle (2 cm per metre) breaks ties in even rooms
                    float score = room.DistanceToBoundary(p) - 0.02f * Vector2.Distance(p, middle);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = p;
                        found = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Round Z-A: the room data overlay as saved with the model (visibility.json / the live sidecar, next to the grids):
    /// floating room tags, the parameters they show, and the target check.
    /// </summary>
    public sealed class RoomOverlaySettings
    {
        /// <summary>Floating tags on (N).</summary>
        public bool Tags { get; set; }

        /// <summary>Tag height above the room's floor (m).</summary>
        public float Height { get; set; } = DEFAULT_HEIGHT;

        /// <summary>Tags within this distance of the eye (m).</summary>
        public float Distance { get; set; } = DEFAULT_DISTANCE;

        /// <summary>Tags of rooms behind walls too (else only rooms the eye can see the tag point of, and the room you're in).</summary>
        public bool ThroughWalls { get; set; }

        /// <summary>Tags for rooms from links too.</summary>
        public bool LinkRooms { get; set; } = true;

        /// <summary>The parameters the tags show under the number and name (in order, at most <see cref="MAX_SHOWN"/>).</summary>
        public List<string> Shown { get; set; } = new();

        /// <summary>The target check on.</summary>
        public bool Checks { get; set; }

        /// <summary>The checked value's parameter (default Area).</summary>
        public string Value { get; set; } = DEFAULT_VALUE;

        /// <summary>The target's parameter (e.g. "Required area"), or null (no check yet).</summary>
        public string Target { get; set; }

        /// <summary>The tolerance (% of the target).</summary>
        public float Tolerance { get; set; } = DEFAULT_TOLERANCE;

        /// <summary>"within" (±), "atleast" or "atmost".</summary>
        public string Rule { get; set; } = RULE_WITHIN;

        /// <summary>Floors tinted green / amber / red by the check (rooms near the eye).</summary>
        public bool FloorTint { get; set; } = true;

        /// <summary>Rule names.</summary>
        public const string RULE_WITHIN = "within", RULE_AT_LEAST = "atleast", RULE_AT_MOST = "atmost";

        /// <summary>Limits and defaults.</summary>
        public const int MAX_SHOWN = 4;

        /// <summary>Limits and defaults.</summary>
        public const float DEFAULT_HEIGHT = 1.5f, MIN_HEIGHT = 0.3f, MAX_HEIGHT = 3f, DEFAULT_DISTANCE = 30f, MIN_DISTANCE = 5f,
            MAX_DISTANCE = 200f, DEFAULT_TOLERANCE = 5f, MAX_TOLERANCE = 50f;

        /// <summary>The default value parameter.</summary>
        public const string DEFAULT_VALUE = "Area";

        /// <summary>The rule as an enum.</summary>
        [JsonIgnore]
        public RoomCheckRule CheckRule
        {
            get => RoomChecks.ParseRule(Rule);
            set => Rule = RoomChecks.RuleName(value);
        }

        /// <summary>True when everything is at its default (the file then leaves it out).</summary>
        [JsonIgnore]
        public bool IsDefault
        {
            get
            {
                var d = new RoomOverlaySettings();
                return Tags == d.Tags && MathF.Abs(Height - d.Height) < 1e-4f && MathF.Abs(Distance - d.Distance) < 1e-3f
                    && ThroughWalls == d.ThroughWalls && LinkRooms == d.LinkRooms && (Shown?.Count ?? 0) == 0 && Checks == d.Checks
                    && string.Equals(Value, d.Value, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(Target)
                    && MathF.Abs(Tolerance - d.Tolerance) < 1e-4f && Rule == d.Rule && FloorTint == d.FloorTint;
            }
        }

        /// <summary>Brings every field into range (guards against hand-edited files).</summary>
        /// <returns>This instance.</returns>
        public RoomOverlaySettings Clean()
        {
            Height = float.IsFinite(Height) ? MathF.Round(Math.Clamp(Height, MIN_HEIGHT, MAX_HEIGHT) * 10f) / 10f : DEFAULT_HEIGHT;
            Distance = float.IsFinite(Distance) ? MathF.Round(Math.Clamp(Distance, MIN_DISTANCE, MAX_DISTANCE) / 5f) * 5f : DEFAULT_DISTANCE;
            Tolerance = float.IsFinite(Tolerance) ? MathF.Round(Math.Clamp(Tolerance, 0f, MAX_TOLERANCE) * 2f) / 2f : DEFAULT_TOLERANCE;
            CheckRule = CheckRule;
            Value = string.IsNullOrWhiteSpace(Value) ? DEFAULT_VALUE : Value.Trim();
            Target = string.IsNullOrWhiteSpace(Target) ? null : Target.Trim();
            Shown = (Shown ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MAX_SHOWN)
                .ToList();
            return this;
        }

        /// <summary>A deep copy.</summary>
        public RoomOverlaySettings Copy()
        {
            var copy = (RoomOverlaySettings)MemberwiseClone();
            copy.Shown = new List<string>(Shown ?? new List<string>());
            return copy;
        }
    }
}
