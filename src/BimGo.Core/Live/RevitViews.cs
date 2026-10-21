using System.Globalization;
using System.Numerics;

// The class belongs to the Live namespace
namespace BimGo.Live
{
    /// <summary>
    /// Round X-E (r2): what a "Revit view from BimGo" does.
    /// </summary>
    public enum RevitViewAction
    {
        /// <summary>A new perspective 3D view ("BimGo 01", "BimGo 02"…), made active.</summary>
        Create = 0,

        /// <summary>Revit's active perspective 3D view takes BimGo's camera.</summary>
        Update = 1
    }

    /// <summary>
    /// Round X-E (r2): helpers shared by the app and the add-in. A live camera sync (r1) put every move on Revit's undo
    /// list, so it became two one-shot actions: each is one "BimGo: …" transaction (one undo entry).
    /// </summary>
    public static class RevitViews
    {
        /// <summary>The prefix of the views BimGo makes ("BimGo 01").</summary>
        public const string NAME_PREFIX = "BimGo ";

        /// <summary>
        /// The first free "BimGo NN" name (two digits at least; numbers already used anywhere count, gaps are not refilled
        /// so names keep their order). Case-insensitive, like Revit's view names.
        /// </summary>
        public static string NextViewName(IEnumerable<string> existingNames)
        {
            int highest = 0;
            if (existingNames != null)
            {
                foreach (string name in existingNames)
                {
                    if (name == null || name.Length <= NAME_PREFIX.Length) { continue; }
                    if (!name.StartsWith(NAME_PREFIX, StringComparison.OrdinalIgnoreCase)) { continue; }
                    string tail = name.Substring(NAME_PREFIX.Length).Trim();
                    if (int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > highest && number < 100000) { highest = number; }
                }
            }
            return NAME_PREFIX + (highest + 1).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The up vector for a look direction with no roll (world Z up; looking straight up or down uses −Y / +Y).
        /// </summary>
        public static Vector3 LevelUp(Vector3 forward)
        {
            Vector3 f = forward.LengthSquared() > 1e-12f ? Vector3.Normalize(forward) : Vector3.UnitX;
            Vector3 right = Vector3.Cross(f, Vector3.UnitZ);
            if (right.LengthSquared() < 1e-8f) { return f.Z > 0f ? -Vector3.UnitY : Vector3.UnitY; }
            return Vector3.Normalize(Vector3.Cross(Vector3.Normalize(right), f));
        }
    }

    /// <summary>
    /// view.make (app → Revit): BimGo's camera in Revit internal coordinates, metres (like clash views); the add-in
    /// converts to feet. Answered with view.result (<see cref="MessagePayload"/>).
    /// </summary>
    public sealed class RevitViewPayload
    {
        /// <summary>Make a new view or update the active one.</summary>
        public RevitViewAction Action { get; set; }

        /// <summary>True when Revit's ribbon asked (view.request): Revit then also shows a failure itself.</summary>
        public bool AskedInRevit { get; set; }

        public double EyeX { get; set; }
        public double EyeY { get; set; }
        public double EyeZ { get; set; }

        /// <summary>The look direction (unit vector).</summary>
        public double ForwardX { get; set; }
        public double ForwardY { get; set; }
        public double ForwardZ { get; set; }

        /// <summary>The camera's up (unit vector, no roll).</summary>
        public double UpX { get; set; }
        public double UpY { get; set; }
        public double UpZ { get; set; }

        /// <summary>Horizontal field of view in degrees (0 = leave the crop alone).</summary>
        public double HorizontalFov { get; set; }

        /// <summary>Width / height of BimGo's picture (0 = keep the crop's shape).</summary>
        public double Aspect { get; set; }

        /// <summary>
        /// True when every value is finite and the direction has a length.
        /// </summary>
        public bool IsValid()
        {
            double length = Math.Sqrt(ForwardX * ForwardX + ForwardY * ForwardY + ForwardZ * ForwardZ);
            return double.IsFinite(EyeX) && double.IsFinite(EyeY) && double.IsFinite(EyeZ) && double.IsFinite(length) && length > 1e-6
                && double.IsFinite(UpX) && double.IsFinite(UpY) && double.IsFinite(UpZ) && double.IsFinite(HorizontalFov) && double.IsFinite(Aspect);
        }
    }

    /// <summary>
    /// view.request (Revit → app): Revit's ribbon asks for BimGo's camera; the app answers with view.make.
    /// </summary>
    public sealed class RevitViewRequestPayload
    {
        public RevitViewAction Action { get; set; }
    }
}
