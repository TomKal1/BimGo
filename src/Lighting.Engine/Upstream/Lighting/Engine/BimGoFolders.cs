using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// BimGo's per-model folders, <c>%LocalAppData%\BimGo\Models\&lt;title&gt;_&lt;hash&gt;\</c>, named exactly as BimGo names them
    /// (a port of BimGo.Core <c>ModelFolders.FolderNameFor</c>; the unit tests share its test vectors). WW Tools writes
    /// its lighting calc results there so a live BimGo walkthrough of the same model finds them.
    /// The key source is built from the document by <c>BimGoModelFolder.KeySource</c>.
    /// </summary>
    public static class BimGoFolders
    {
        /// <summary>Longest title part of a folder name.</summary>
        private const int MaxTitle = 60;

        /// <summary>Windows' invalid file name characters, listed so the name is the same on any OS (tests run on Linux too).</summary>
        private static readonly char[] Invalid = { '"', '<', '>', '|', ':', '*', '?', '\\', '/' };

        /// <summary><c>%LocalAppData%\BimGo</c>: present once BimGo has run on this machine.</summary>
        public static string BimGoRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BimGo");

        /// <summary><c>%LocalAppData%\BimGo\Models</c>.</summary>
        public static string ModelsRoot => Path.Combine(BimGoRoot, "Models");

        /// <summary>
        /// Paths ("local:", "central:") are trimmed, use backslashes and are lower-cased; other kinds ("cloud:",
        /// "unsaved:") are trimmed only.
        /// </summary>
        public static string NormaliseKey(string? keySource)
        {
            string key = (keySource ?? string.Empty).Trim();
            if (key.StartsWith("local:", StringComparison.OrdinalIgnoreCase) || key.StartsWith("central:", StringComparison.OrdinalIgnoreCase))
                key = key.Replace('/', '\\').ToLowerInvariant();
            return key;
        }

        /// <summary>The first 8 hex characters (lower case) of the SHA-256 of the normalised key.</summary>
        public static string HashOf(string? keySource)
        {
            using (SHA256 sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(NormaliseKey(keySource))), 4);
        }

        /// <summary>"&lt;safe title&gt;_&lt;hash&gt;": the title without ".rvt", invalid characters as "_", at most 60 characters, "Model" when blank.</summary>
        public static string FolderNameFor(string? title, string? keySource)
        {
            string safe = (title ?? string.Empty).Trim();
            if (safe.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) safe = safe.Substring(0, safe.Length - 4);
            return SafeTitle(safe, "Model") + "_" + HashOf(keySource);
        }

        /// <summary>The model's folder under <paramref name="root"/> (default <see cref="ModelsRoot"/>); not created.</summary>
        public static string FolderFor(string? title, string? keySource, string? root = null) =>
            Path.Combine(root ?? ModelsRoot, FolderNameFor(title, keySource));

        /// <summary>A name made safe the way BimGo makes folder titles safe, or <paramref name="fallback"/> when nothing is left.</summary>
        internal static string SafeTitle(string? name, string fallback)
        {
            string text = (name ?? string.Empty).Trim();
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
                builder.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
            string safe = builder.ToString().Trim(' ', '.');
            if (safe.Length > MaxTitle) safe = safe.Substring(0, MaxTitle).TrimEnd(' ', '.');
            return safe.Length == 0 ? fallback : safe;
        }

        internal static string Hex(byte[] bytes, int count)
        {
            var sb = new StringBuilder(count * 2);
            for (int i = 0; i < count && i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
