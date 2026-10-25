using System.Globalization;
using BimGo.Format;

// The class belongs to the Live namespace
namespace BimGo.Live
{
    /// <summary>
    /// Round Z-B: the comment marker family Gavin authored (adaptive component, one point: three status spheres) and its
    /// shared parameters, found by GUID (tools/revit/BimGo_SharedParameters.txt).
    /// </summary>
    public static class MarkerFamily
    {
        /// <summary>The family's name in a model.</summary>
        public const string FAMILY_NAME = "BimGo Comment Marker";

        /// <summary>The type used (else the family's first type).</summary>
        public const string TYPE_NAME = "Default";

        /// <summary>The family file shipped with the add-in.</summary>
        public const string FILE_NAME = "BimGo_Comment_Marker.rfa";

        /// <summary>Undo name of a sync (a "BimGo:" transaction: the live session doesn't count it as an outside change).</summary>
        public const string TRANSACTION_NAME = "BimGo: comment markers";

        // Shared parameters (instance), by GUID: names may be shown differently in a family
        public static readonly Guid ID = new("5d24ab4c-5c10-5023-bb69-b8526e44ab4a");
        public static readonly Guid TITLE = new("ead6278a-4ee3-5325-aebf-4484b31deb33");
        public static readonly Guid COMMENT = new("611d7d5a-1e4f-554b-a105-3808a805589d");
        public static readonly Guid STATUS = new("a8f374e9-ec5c-5d0d-8e65-ca460e508fb1");
        public static readonly Guid PRIORITY = new("97ce83ee-3d33-5c8e-8cf1-5ca61e26764b");
        public static readonly Guid AUTHOR = new("feca2551-65bb-5dd0-aa5a-60bd9a0891a5");
        public static readonly Guid ASSIGNED_TO = new("bc5284ca-081a-5a83-8056-9ff4235c6928");
        public static readonly Guid CREATED = new("78a872d8-7df7-5827-b781-ca0ada549931");
        public static readonly Guid UPDATED = new("7ade62cf-73fd-56af-b96d-554b14bbddac");
        public static readonly Guid LEVEL = new("0332a4cf-cf80-54fe-8d15-cd7fb9b0eeff");
        public static readonly Guid ELEMENT = new("e2cab673-8034-5e19-96de-2ffc7394bd2a");
        public static readonly Guid REPLIES = new("e04836c9-9920-59d1-a45f-22c969c33500");
        public static readonly Guid PICTURE = new("34e1f0f6-a357-5d69-926d-2071803cfef1");
        public static readonly Guid SYNCED = new("a3450b13-b57b-55d8-8b31-a06f983f7fa8");
        public static readonly Guid IS_OPEN = new("10bdc1c1-122b-5cbb-89e4-2582e186ef21");
        public static readonly Guid IS_IN_PROGRESS = new("ab02c1e9-3c4a-5a66-a3ba-9d3397e56a90");
        public static readonly Guid IS_CLOSED = new("4dc55a39-a8fd-52cd-a3cf-c25a78dc55a9");
        public static readonly Guid IS_HIGH_PRIORITY = new("b8562f32-3c77-5592-84b5-fc6b8ce7278f");
    }

    /// <summary>
    /// Round Z-B: one comment as a Revit marker: where it goes (Revit internal metres) and the text the family's shared
    /// parameters get. Made from a comment by <see cref="CommentMarkers.FromComment"/>.
    /// </summary>
    public sealed class MarkerRecord
    {
        /// <summary>The comment id (BimGo_Id).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>The marker point (Revit internal coordinates, metres).</summary>
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public string Title { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string AssignedTo { get; set; } = string.Empty;
        public string Created { get; set; } = string.Empty;
        public string Updated { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Element { get; set; } = string.Empty;
        public int Replies { get; set; }
        public string Picture { get; set; } = string.Empty;
        public bool IsOpen { get; set; }
        public bool IsInProgress { get; set; }
        public bool IsClosed { get; set; }
        public bool IsHighPriority { get; set; }

        /// <summary>True when every parameter value matches (the position and BimGo_Synced aren't compared).</summary>
        public bool SameValues(MarkerRecord other) => other != null
            && Same(Title, other.Title) && Same(Comment, other.Comment) && Same(Status, other.Status) && Same(Priority, other.Priority)
            && Same(Author, other.Author) && Same(AssignedTo, other.AssignedTo) && Same(Created, other.Created)
            && Same(Updated, other.Updated) && Same(Level, other.Level) && Same(Element, other.Element) && Replies == other.Replies
            && Same(Picture, other.Picture) && IsOpen == other.IsOpen && IsInProgress == other.IsInProgress && IsClosed == other.IsClosed
            && IsHighPriority == other.IsHighPriority;

        private static bool Same(string a, string b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);

        /// <summary>True when the id is set and the point is finite.</summary>
        public bool IsValid() => !string.IsNullOrWhiteSpace(Id) && double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    }

    /// <summary>
    /// markers.sync (app → Revit, Round Z-B; also what the add-in's COMMENT MARKERS button builds): the comments as
    /// markers. With <see cref="Full"/> the list is every comment, and markers of comments no longer in it are removed.
    /// </summary>
    public sealed class MarkersSyncPayload
    {
        /// <summary>The markers wanted.</summary>
        public List<MarkerRecord> Markers { get; set; } = new();

        /// <summary>True: the list is the complete set (markers of other ids are removed); false: add / update only.</summary>
        public bool Full { get; set; } = true;

        /// <summary>True: markers deleted in Revit are put back too (RE-PLACE ALL).</summary>
        public bool ReplaceAll { get; set; }
    }

    /// <summary>markers.result (Revit → app, Round Z-B): what a sync did.</summary>
    public sealed class MarkersResultPayload
    {
        public bool Success { get; set; }

        /// <summary>A one-line summary ("Revit markers: 2 placed, 1 updated"), or the reason it failed.</summary>
        public string Message { get; set; } = string.Empty;

        public int Placed { get; set; }
        public int Updated { get; set; }
        public int Moved { get; set; }
        public int Removed { get; set; }

        /// <summary>Comments whose markers were deleted in Revit (left out until RE-PLACE ALL).</summary>
        public int DeletedInRevit { get; set; }

        /// <summary>Markers that couldn't be changed (owned by someone else in a workshared model, or failed).</summary>
        public int Skipped { get; set; }

        /// <summary>True when something in the model changed (the app shows the message then; silent otherwise).</summary>
        public bool Changed => Placed + Updated + Moved + Removed > 0;
    }

    /// <summary>Round Z-B: a marker found in the model (read back by the add-in).</summary>
    public sealed class ExistingMarker
    {
        /// <summary>Its ElementId value.</summary>
        public long ElementId { get; set; }

        /// <summary>Its parameter values and point, as read (Id = BimGo_Id).</summary>
        public MarkerRecord Values { get; set; } = new();
    }

    /// <summary>Round Z-B: what a sync will do (<see cref="CommentMarkers.Plan"/>).</summary>
    public sealed class MarkerPlan
    {
        /// <summary>New markers to place.</summary>
        public List<MarkerRecord> Place { get; } = new();

        /// <summary>Markers whose parameters change (with their element), and whether the point moves too.</summary>
        public List<(ExistingMarker Marker, MarkerRecord Wanted, bool Move, bool Values)> Change { get; } = new();

        /// <summary>Markers to delete (their comment is gone, or closed with "remove closed" on).</summary>
        public List<ExistingMarker> Remove { get; } = new();

        /// <summary>Comments whose marker was deleted in Revit (left out).</summary>
        public int DeletedInRevit { get; set; }

        /// <summary>Ids BimGo has placed in this model after the sync (kept in the model, so a deletion there sticks).</summary>
        public HashSet<string> PlacedAfter { get; } = new(StringComparer.Ordinal);

        /// <summary>True when the model would change.</summary>
        public bool HasWork => Place.Count + Change.Count + Remove.Count > 0;
    }

    /// <summary>
    /// Round Z-B: comments → Revit markers. Pure (no Revit): the add-in reads the model's markers, asks for a plan and
    /// carries it out in one transaction.
    /// </summary>
    public static class CommentMarkers
    {
        /// <summary>A moved comment moves its marker when the point is further than this (metres).</summary>
        public const double MOVE_TOLERANCE = 0.001;

        /// <summary>The comment text kept on the marker (characters).</summary>
        public const int MAX_COMMENT = 4000;

        /// <summary>The title's length (characters).</summary>
        public const int MAX_TITLE = 60;

        /// <summary>
        /// A comment as a marker. Dates in local time, yyyy-MM-dd HH:mm; status and priority as BimGo shows them.
        /// </summary>
        /// <param name="comment">The comment (Revit internal metres).</param>
        /// <param name="picturePath">Its picture's full path, or null.</param>
        public static MarkerRecord FromComment(CommentRecord comment, string picturePath = null)
        {
            string status = CommentStatus.Normalise(comment.Status);
            string priority = CommentPriority.Normalise(comment.Priority);
            string text = (comment.Text ?? string.Empty).Trim();
            DateTimeOffset updated = Latest(comment.Created, comment.Edited, comment.Updated);
            return new MarkerRecord
            {
                Id = comment.Id ?? string.Empty,
                X = comment.X,
                Y = comment.Y,
                Z = comment.Z,
                Title = TitleOf(text),
                Comment = text.Length > MAX_COMMENT ? text[..(MAX_COMMENT - 1)] + "…" : text,
                Status = StatusLabel(status),
                Priority = PriorityLabel(priority),
                Author = comment.Author ?? string.Empty,
                AssignedTo = comment.AssignedTo ?? string.Empty,
                Created = Stamp(comment.Created),
                Updated = Stamp(updated),
                Level = comment.Level ?? string.Empty,
                Element = comment.ElementUniqueId ?? string.Empty,
                Replies = comment.ReplyCount,
                Picture = picturePath ?? string.Empty,
                IsOpen = status == CommentStatus.OPEN,
                IsInProgress = status == CommentStatus.IN_PROGRESS,
                IsClosed = status == CommentStatus.CLOSED,
                IsHighPriority = priority == CommentPriority.HIGH
            };
        }

        /// <summary>
        /// Every comment as a marker (blank or id-less comments left out).
        /// </summary>
        /// <param name="comments">The comments.</param>
        /// <param name="picturePath">A comment's picture path (null: none).</param>
        /// <param name="full">True: the complete set (markers of other comments are removed).</param>
        public static MarkersSyncPayload Payload(IEnumerable<CommentRecord> comments, Func<CommentRecord, string> picturePath = null, bool full = true)
        {
            var payload = new MarkersSyncPayload { Full = full };
            foreach (CommentRecord comment in comments ?? Enumerable.Empty<CommentRecord>())
            {
                if (comment == null || string.IsNullOrWhiteSpace(comment.Text) || string.IsNullOrWhiteSpace(comment.Id)) { continue; }
                MarkerRecord marker = FromComment(comment, picturePath?.Invoke(comment));
                if (marker.IsValid()) { payload.Markers.Add(marker); }
            }
            return payload;
        }

        /// <summary>
        /// Keeps a payload under a message size (the live channel drops larger ones): long comment texts are cut to
        /// 1000, then 200 characters, then left to the title, until it fits.
        /// </summary>
        /// <returns>True when it fits.</returns>
        public static bool FitTo(MarkersSyncPayload payload, long maxBytes)
        {
            if (payload == null) { return true; }
            foreach (int keep in new[] { MAX_COMMENT, 1000, 200, 0 })
            {
                foreach (MarkerRecord marker in payload.Markers)
                {
                    if (marker.Comment.Length > keep) { marker.Comment = keep == 0 ? marker.Title : marker.Comment[..(keep - 1)] + "…"; }
                }
                if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, LiveProtocol.JSON).LongLength <= maxBytes) { return true; }
            }
            return false;
        }

        /// <summary>
        /// What a sync does:
        /// <list type="bullet">
        /// <item>a wanted marker already in the model is updated (values) and moved (point) where they differ;</item>
        /// <item>one not in the model is placed, unless BimGo placed it before (it was deleted in Revit: left out until
        /// <paramref name="replaceAll"/>);</item>
        /// <item>with <paramref name="full"/>, markers of comments not in the list are removed; with
        /// <paramref name="removeClosed"/>, closed comments' markers are removed and not placed.</item>
        /// </list>
        /// A marker BimGo removes is forgotten (placed again if its comment comes back or reopens); a deleted one stays
        /// remembered. Markers without an id (placed by hand) and second copies of an id are left alone.
        /// </summary>
        /// <param name="wanted">The comments as markers.</param>
        /// <param name="existing">The markers in the model.</param>
        /// <param name="placedBefore">The ids BimGo has placed in this model.</param>
        /// <param name="full">The list is the complete set.</param>
        /// <param name="replaceAll">Put back markers deleted in Revit.</param>
        /// <param name="removeClosed">Closed comments get no marker.</param>
        public static MarkerPlan Plan(IReadOnlyList<MarkerRecord> wanted, IReadOnlyList<ExistingMarker> existing, IEnumerable<string> placedBefore,
            bool full, bool replaceAll, bool removeClosed)
        {
            var plan = new MarkerPlan();
            var placed = new HashSet<string>(placedBefore ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            // The model's markers by id (the first of a repeated id is the one BimGo manages)
            var byId = new Dictionary<string, ExistingMarker>(StringComparer.Ordinal);
            foreach (ExistingMarker marker in existing ?? Array.Empty<ExistingMarker>())
            {
                string id = marker?.Values?.Id;
                if (string.IsNullOrWhiteSpace(id)) { continue; }
                byId.TryAdd(id, marker);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (MarkerRecord want in wanted ?? Array.Empty<MarkerRecord>())
            {
                if (want == null || !want.IsValid() || !seen.Add(want.Id)) { continue; }
                bool skip = removeClosed && want.IsClosed;
                if (byId.TryGetValue(want.Id, out ExistingMarker have))
                {
                    if (skip)
                    {
                        plan.Remove.Add(have);
                        continue;
                    }
                    MarkerRecord v = have.Values;
                    bool move = Math.Abs(v.X - want.X) > MOVE_TOLERANCE || Math.Abs(v.Y - want.Y) > MOVE_TOLERANCE || Math.Abs(v.Z - want.Z) > MOVE_TOLERANCE
                        || !double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Z);
                    bool values = !v.SameValues(want);
                    if (move || values) { plan.Change.Add((have, want, move, values)); }
                    plan.PlacedAfter.Add(want.Id);
                    continue;
                }
                if (skip) { continue; }
                if (placed.Contains(want.Id) && !replaceAll)
                {
                    plan.DeletedInRevit++;
                    plan.PlacedAfter.Add(want.Id); // still remembered: the deletion sticks
                    continue;
                }
                plan.Place.Add(want);
                plan.PlacedAfter.Add(want.Id);
            }

            // Markers of comments no longer there (full sync only)
            if (full)
            {
                foreach (ExistingMarker have in byId.Values)
                {
                    if (!seen.Contains(have.Values.Id)) { plan.Remove.Add(have); }
                }
            }
            else
            {
                // Add / update only: keep remembering everything else
                foreach (string id in placed)
                {
                    if (!seen.Contains(id)) { plan.PlacedAfter.Add(id); }
                }
            }
            foreach (ExistingMarker removed in plan.Remove) { plan.PlacedAfter.Remove(removed.Values.Id); }
            return plan;
        }

        /// <summary>The ids BimGo remembers placing, from the stored text (one per line), and back.</summary>
        public static HashSet<string> ParsePlaced(string stored)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(stored)) { return set; }
            foreach (string line in stored.Split('\n'))
            {
                string id = line.Trim();
                if (id.Length > 0) { set.Add(id); }
            }
            return set;
        }

        /// <inheritdoc cref="ParsePlaced"/>
        public static string FormatPlaced(IEnumerable<string> ids) =>
            string.Join("\n", (ids ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).OrderBy(i => i, StringComparer.Ordinal));

        /// <summary>"Revit markers: 2 placed, 1 updated, 1 removed" (or "Revit markers are up to date").</summary>
        public static string Summary(MarkersResultPayload result)
        {
            var parts = new List<string>();
            if (result.Placed > 0) { parts.Add($"{result.Placed} placed"); }
            if (result.Updated > 0) { parts.Add($"{result.Updated} updated"); }
            if (result.Moved > 0) { parts.Add($"{result.Moved} moved"); }
            if (result.Removed > 0) { parts.Add($"{result.Removed} removed"); }
            if (result.Skipped > 0) { parts.Add($"{result.Skipped} couldn't be changed"); }
            string deleted = result.DeletedInRevit > 0 ? $" ({result.DeletedInRevit} deleted in Revit left out)" : string.Empty;
            return parts.Count == 0 ? "Revit markers are up to date" + deleted : "Revit markers: " + string.Join(", ", parts) + deleted;
        }

        /// <summary>The first line, shortened to <see cref="MAX_TITLE"/> characters.</summary>
        public static string TitleOf(string text)
        {
            if (string.IsNullOrEmpty(text)) { return string.Empty; }
            int end = text.IndexOfAny(new[] { '\r', '\n' });
            string line = (end >= 0 ? text[..end] : text).Trim();
            return line.Length > MAX_TITLE ? line[..(MAX_TITLE - 1)].TrimEnd() + "…" : line;
        }

        /// <summary>Open / In progress / Closed.</summary>
        public static string StatusLabel(string status) => status switch
        {
            CommentStatus.IN_PROGRESS => "In progress",
            CommentStatus.CLOSED => "Closed",
            _ => "Open"
        };

        /// <summary>Low / Normal / High.</summary>
        public static string PriorityLabel(string priority) => priority switch
        {
            CommentPriority.LOW => "Low",
            CommentPriority.HIGH => "High",
            _ => "Normal"
        };

        private static string Stamp(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private static DateTimeOffset Latest(DateTimeOffset created, DateTimeOffset? edited, DateTimeOffset? updated)
        {
            DateTimeOffset latest = created;
            if (edited is DateTimeOffset e && e > latest) { latest = e; }
            if (updated is DateTimeOffset u && u > latest) { latest = u; }
            return latest;
        }
    }
}
