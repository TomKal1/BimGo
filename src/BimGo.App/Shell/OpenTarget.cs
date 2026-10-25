using BimGo.Game;

// The class belongs to the Shell namespace
namespace BimGo.Shell
{
    /// <summary>
    /// Something the app should open: a .bimgo file, or a live Revit session (optionally with the pose to resume).
    /// </summary>
    internal sealed class OpenTarget
    {
        /// <summary>A .bimgo path (file targets).</summary>
        public string Path { get; init; }

        /// <summary>A session id (live targets).</summary>
        public string SessionId { get; init; }

        /// <summary>Where to put the player (reloads), or null.</summary>
        public SessionPose Pose { get; init; }

        /// <summary>Round Z-C: open the file in kiosk mode (walk, fly and look only; idle tour; Ctrl+Shift+Q held leaves).</summary>
        public bool Kiosk { get; init; }

        /// <summary>True for a live session.</summary>
        public bool IsLive => SessionId != null;

        /// <summary>A file target.</summary>
        public static OpenTarget File(string path) => new() { Path = path };

        /// <summary>Round Z-C: a file target in kiosk mode (<c>BimGo.exe --kiosk "file"</c>).</summary>
        public static OpenTarget KioskFile(string path) => new() { Path = path, Kiosk = true };

        /// <summary>A live session target.</summary>
        public static OpenTarget Live(string sessionId, SessionPose pose = null) => new() { SessionId = sessionId, Pose = pose };
    }
}
