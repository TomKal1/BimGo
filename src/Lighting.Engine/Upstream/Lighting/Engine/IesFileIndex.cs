using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// IES files found under a set of folders (with subfolders), looked up by the file name a
    /// family names: exact name first, then the name without its extension ("generic" finds
    /// generic.ies), then ignoring case, spaces, dashes and underscores. The first folder wins
    /// when two hold the same name.
    /// </summary>
    public sealed class IesFileIndex
    {
        private readonly Dictionary<string, string> _byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byLooseStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count => _byName.Count;

        public static IesFileIndex Build(IEnumerable<string> folders)
        {
            var index = new IesFileIndex();
            foreach (string folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        if (Path.GetExtension(file).Equals(".ies", StringComparison.OrdinalIgnoreCase))
                            index.Add(file);
                    }
                }
                catch
                {
                    // unreadable folders are skipped
                }
            }
            return index;
        }

        public void Add(string path)
        {
            string name = Path.GetFileName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!_byName.ContainsKey(name)) _byName[name] = path;
            if (!_byStem.ContainsKey(stem)) _byStem[stem] = path;
            string loose = Loose(stem);
            if (loose.Length > 0 && !_byLooseStem.ContainsKey(loose)) _byLooseStem[loose] = path;
        }

        /// <summary>The file for a name a family gives (with or without a folder or extension), or null.</summary>
        public string? Find(string? webFileName)
        {
            if (string.IsNullOrWhiteSpace(webFileName)) return null;
            // Families store Windows paths; take what follows the last \ or / on any platform.
            string trimmed = webFileName!.Trim();
            string name = trimmed.Substring(Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/')) + 1);
            if (_byName.TryGetValue(name, out string? path)) return path;
            string stem = Path.GetExtension(name).Equals(".ies", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(name)
                : name;
            if (_byStem.TryGetValue(stem, out path)) return path;
            return _byLooseStem.TryGetValue(Loose(stem), out path) ? path : null;
        }

        private static string Loose(string stem) =>
            new string(stem.Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-' && c != '.').ToArray());
    }
}
