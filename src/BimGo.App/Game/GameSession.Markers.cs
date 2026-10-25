using BimGo.Format;
using BimGo.Live;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round Z-B: comments → Revit markers in live sessions. When the comments change (add, edit, status, move, delete,
    /// replies, pictures) the whole set goes to Revit 1.5 s after the last change (markers.sync); the add-in places,
    /// updates, moves and removes the family's instances in one "BimGo: comment markers" transaction, only when
    /// something differs. The first sync runs as soon as Revit says it keeps markers (hello.ack), so a session starts
    /// in step. File mode sends nothing (Revit's COMMENT MARKERS button reads the comments instead).
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private const float MARKERS_DELAY = 1.5f;
        private const float MARKERS_RETRY = 5f;

        private bool _markersDirty;
        private bool _markersStarted;
        private float _markersDueAt;

        #endregion

        #region Sync

        /// <summary>Hooks the comment store (live sessions only).</summary>
        private void InitialiseCommentMarkers()
        {
            if (_live == null || Comments == null) { return; }
            Comments.Changed += OnCommentsChangedForMarkers;
        }

        private void OnCommentsChangedForMarkers()
        {
            _markersDirty = true;
            _markersDueAt = _clock + MARKERS_DELAY;
        }

        /// <summary>
        /// Sends the comments when they changed and the delay has passed (and once at the start of a session).
        /// Allocates only when it sends.
        /// </summary>
        private void UpdateCommentMarkers()
        {
            if (_live == null || !_live.Connected || !_live.CommentMarkersSupported || Comments == null) { return; }
            if (!_markersStarted)
            {
                _markersStarted = true;
                _markersDirty = true;
                _markersDueAt = _clock;
            }
            if (!_markersDirty || _clock < _markersDueAt) { return; }
            _markersDirty = false;

            string folder = string.IsNullOrEmpty(Scene.CommentsPath) ? null : Path.GetDirectoryName(Scene.CommentsPath);
            MarkersSyncPayload payload = CommentMarkers.Payload(Comments.Comments, c => PicturePath(folder, c), full: true);
            if (!CommentMarkers.FitTo(payload, LiveProtocol.MAX_MESSAGE_BYTES * 3 / 4))
            {
                Utilities.Log_Utils.Write($"Comment markers: {payload.Markers.Count} comments don't fit one message; not sent.");
                return;
            }
            if (!_live.SyncCommentMarkers(payload))
            {
                _markersDirty = true;
                _markersDueAt = _clock + MARKERS_RETRY;
            }
        }

        /// <summary>A comment's picture beside the comments (the model folder), or null.</summary>
        private static string PicturePath(string folder, CommentRecord comment)
        {
            if (folder == null || string.IsNullOrEmpty(comment.Snapshot)) { return null; }
            try
            {
                string path = Path.GetFullPath(Path.Combine(folder, comment.Snapshot));
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
