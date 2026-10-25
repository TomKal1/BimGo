// The class belongs to the Utilities namespace
namespace BimGo.Utilities
{
    /// <summary>
    /// File-name helpers shared by the app and the add-in.
    /// </summary>
    public static class FileNames
    {
        /// <summary>
        /// A name usable as a file name: characters Windows doesn't allow in file names become '_'; blank gives
        /// <paramref name="fallback"/>.
        /// </summary>
        public static string Safe(string name, string fallback = "Model")
        {
            if (string.IsNullOrWhiteSpace(name)) { return fallback; }
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name) { builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c); }
            return builder.ToString();
        }
    }
}
