using System.Numerics;
using BimGo.Format;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round X-B: the model's entourage (placed billboards, imported picture types, the look), persisted like camera
    /// paths:
    /// <list type="bullet">
    /// <item><b>Model folder</b> (Revit sessions): <c>entourage.json</c> with the pictures in <c>entourage-images</c>
    /// beside it, written on every change (so the next Go, F5 or Export picks it up).</item>
    /// <item><b>Embedded</b> (.bimgo files): kept in memory and saved with the file; changes raise
    /// <see cref="Changed"/> so the document is marked dirty.</item>
    /// </list>
    /// Positions are Revit internal metres in the file; <see cref="EntourageItem.Local"/> is the scene-local copy.
    /// </summary>
    internal sealed class EntourageStore
    {
        private readonly string _path, _imagesFolder, _model;
        private readonly Vector3 _origin;

        /// <summary>The document (never null).</summary>
        public EntourageDocument Document { get; private set; } = new();

        /// <summary>The imported pictures by file name (PNG bytes).</summary>
        public Dictionary<string, byte[]> Images { get; } = new(StringComparer.Ordinal);

        /// <summary>The last IO error, or null.</summary>
        public string LastError { get; private set; }

        /// <summary>True when kept inside a .bimgo rather than the model folder.</summary>
        public bool IsEmbedded => _path == null;

        /// <summary>Where it's kept (for the panel).</summary>
        public string FileName => IsEmbedded ? "this file" : Path.GetFileName(_path);

        /// <summary>Incremented on every change (dirty tracking; the renderer's instance data).</summary>
        public int Revision { get; private set; }

        /// <summary>Incremented when the picture set changes (types added / removed).</summary>
        public int PictureRevision { get; private set; }

        /// <summary>Raised after any change.</summary>
        public event Action Changed;

        /// <summary>Creates the store.</summary>
        /// <param name="sidecarPath">The model folder's entourage.json, or null to keep it embedded.</param>
        public EntourageStore(string sidecarPath, string model, Vector3 origin)
        {
            _path = sidecarPath;
            _imagesFolder = EntourageFiles.ImagesFolderFor(sidecarPath);
            _model = model;
            _origin = origin;
        }

        #region Load / save

        /// <summary>Loads the model folder's file and pictures. No-op for embedded stores.</summary>
        public void Load()
        {
            if (IsEmbedded) { return; }
            EntourageDocument document = EntourageFiles.Read(_path, out string error);
            LastError = error;
            if (document != null) { LoadFrom(document, EntourageFiles.ReadImages(_imagesFolder, document)); }
        }

        /// <summary>Takes a document and its pictures (a .bimgo's). Does not count as a change.</summary>
        public void LoadFrom(EntourageDocument document, Dictionary<string, byte[]> images)
        {
            if (document == null) { return; }
            Document = document.Clean();
            Images.Clear();
            if (images != null)
            {
                foreach (KeyValuePair<string, byte[]> pair in images) { Images[pair.Key] = pair.Value; }
            }
            foreach (EntourageItem item in Document.Items) { Prepare(item); }
            PictureRevision++;
            Revision++;
        }

        /// <summary>The document for saving into a .bimgo (null when there is nothing to save).</summary>
        public EntourageDocument ToDocument()
        {
            if (Document.IsEmpty) { return null; }
            Document.Model = _model;
            return Document;
        }

        /// <summary>The pictures for saving into a .bimgo (only those a type still uses).</summary>
        public Dictionary<string, byte[]> ImagesForSave()
        {
            var used = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (EntourageType type in Document.Types)
            {
                if (type.Image != null && Images.TryGetValue(type.Image, out byte[] png)) { used[type.Image] = png; }
            }
            return used;
        }

        /// <summary>Records a change: writes the model folder's file, or (embedded) flags it for the document.</summary>
        public bool Save()
        {
            Revision++;
            bool saved = true;
            if (!IsEmbedded)
            {
                Document.Model = _model;
                saved = EntourageFiles.Write(_path, Document, out string error);
                LastError = error;
            }
            try { Changed?.Invoke(); }
            catch (Exception ex) { Utilities.Log_Utils.Write($"Entourage change handler failed: {ex.Message}"); }
            return saved;
        }

        #endregion

        #region Items

        /// <summary>Plants a billboard (scene-local base) and saves.</summary>
        public EntourageItem Add(string type, Vector3 local, float height, bool flip)
        {
            var item = new EntourageItem { Type = type, Height = height, Flip = flip };
            SetPosition(item, local);
            if (!item.Clean()) { return null; }
            Prepare(item);
            Document.Items.Add(item);
            Save();
            return item;
        }

        /// <summary>Removes a billboard and saves.</summary>
        public void Remove(EntourageItem item)
        {
            if (Document.Items.Remove(item)) { Save(); }
        }

        /// <summary>Changes a billboard's type, height or mirroring and saves.</summary>
        public void Update(EntourageItem item, string type, float height, bool flip)
        {
            if (item == null || !Document.Items.Contains(item)) { return; }
            item.Type = type ?? item.Type;
            item.Height = Math.Clamp(height, EntourageItem.MIN_HEIGHT, EntourageItem.MAX_HEIGHT);
            item.Flip = flip;
            Save();
        }

        /// <summary>Removes every billboard of a type (or all when null) and saves.</summary>
        /// <returns>How many went.</returns>
        public int RemoveAll(string type)
        {
            int removed = Document.Items.RemoveAll(i => type == null || i.Type == type);
            if (removed > 0) { Save(); }
            return removed;
        }

        private void SetPosition(EntourageItem item, Vector3 local)
        {
            item.X = Math.Round(local.X + (double)_origin.X, 4);
            item.Y = Math.Round(local.Y + (double)_origin.Y, 4);
            item.Z = Math.Round(local.Z + (double)_origin.Z, 4);
        }

        private void Prepare(EntourageItem item) =>
            item.Local = new Vector3((float)(item.X - _origin.X), (float)(item.Y - _origin.Y), (float)(item.Z - _origin.Z));

        #endregion

        #region Types and look

        /// <summary>
        /// Adds a picture type (PNG bytes already processed) and saves; in a live session the picture is written beside
        /// the file.
        /// </summary>
        public EntourageType AddType(string name, string category, float height, byte[] png)
        {
            var type = new EntourageType { Name = name, Category = category, Height = height };
            type.Image = type.Id + ".png";
            if (!type.Clean() || png == null) { return null; }
            if (!IsEmbedded && !EntourageFiles.WriteImage(_imagesFolder, type.Image, png, out string error))
            {
                LastError = error;
                return null;
            }
            Images[type.Image] = png;
            Document.Types.Add(type);
            PictureRevision++;
            Save();
            return type;
        }

        /// <summary>
        /// Round X-B3: adds several picture types at once (IMPORT FOLDER…), saving once. A picture that can't be written
        /// is skipped (its error in <see cref="LastError"/>).
        /// </summary>
        /// <returns>The types added, in order.</returns>
        public List<EntourageType> AddTypes(IEnumerable<(string Name, string Category, float Height, byte[] Png)> pictures)
        {
            var added = new List<EntourageType>();
            string lastError = null;
            foreach ((string name, string category, float height, byte[] png) in pictures)
            {
                var type = new EntourageType { Name = name, Category = category, Height = height };
                type.Image = type.Id + ".png";
                if (!type.Clean() || png == null) { continue; }
                if (!IsEmbedded && !EntourageFiles.WriteImage(_imagesFolder, type.Image, png, out string error))
                {
                    lastError = error;
                    continue;
                }
                Images[type.Image] = png;
                Document.Types.Add(type);
                added.Add(type);
            }
            if (added.Count > 0)
            {
                PictureRevision++;
                Save();
            }
            if (lastError != null) { LastError = lastError; }
            return added;
        }

        /// <summary>Removes a picture type and its billboards, and saves.</summary>
        public void RemoveType(EntourageType type)
        {
            if (type == null || !Document.Types.Remove(type)) { return; }
            Document.Items.RemoveAll(i => i.Type == type.Key);
            Images.Remove(type.Image);
            if (!IsEmbedded) { EntourageFiles.DeleteImage(_imagesFolder, type.Image); }
            PictureRevision++;
            Save();
        }

        /// <summary>Renames a picture type, changes its category or default height, and saves.</summary>
        public void UpdateType(EntourageType type, string name, string category, float height)
        {
            if (type == null || !Document.Types.Contains(type)) { return; }
            type.Name = name ?? type.Name;
            type.Category = category ?? type.Category;
            type.Height = height;
            type.Clean();
            Save();
        }

        /// <summary>Sets the look (own colours or silhouettes) and saves.</summary>
        public void SetTextured(bool textured)
        {
            if (Document.Textured == textured) { return; }
            Document.Textured = textured;
            Save();
        }

        /// <summary>Round X-B2: Revit Planting / Entourage without a rule swapped automatically (on / off), and saves.</summary>
        public void SetSwapRevit(bool on)
        {
            if (Document.SwapRevit == on) { return; }
            Document.SwapRevit = on;
            Save();
        }

        /// <summary>The rule for a key, or null.</summary>
        public EntourageSwap FindSwap(string scope, string key) => Document.Swaps.Find(s => s.Scope == scope && s.Key == key);

        /// <summary>
        /// Round X-B2: sets the rule for an instance or a type (a billboard type, keep or none) and saves. An instance
        /// rule keeps its height and mirroring unless they're given.
        /// </summary>
        public void SetSwap(string scope, string key, string type, float? height = null, bool? flip = null)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(type)) { return; }
            EntourageSwap swap = FindSwap(scope, key);
            if (swap == null)
            {
                swap = new EntourageSwap { Scope = scope, Key = key };
                Document.Swaps.Add(swap);
            }
            swap.Type = type;
            if (scope == EntourageSwaps.INSTANCE)
            {
                if (height.HasValue) { swap.Height = Math.Clamp(height.Value, EntourageItem.MIN_HEIGHT, EntourageItem.MAX_HEIGHT); }
                if (flip.HasValue) { swap.Flip = flip.Value; }
            }
            Save();
        }

        /// <summary>Removes the rule for a key (back to the type's rule or the automatic choice) and saves.</summary>
        public void ClearSwap(string scope, string key)
        {
            if (Document.Swaps.RemoveAll(s => s.Scope == scope && s.Key == key) > 0) { Save(); }
        }

        /// <summary>Removes every instance rule and saves.</summary>
        /// <returns>How many went.</returns>
        public int ClearInstanceSwaps()
        {
            int removed = Document.Swaps.RemoveAll(s => s.Scope == EntourageSwaps.INSTANCE);
            if (removed > 0) { Save(); }
            return removed;
        }

        /// <summary>Sets a category's colour and saves.</summary>
        public void SetColour(string category, uint rgb)
        {
            if (Document.ColourOf(category) == (rgb & 0xFFFFFF)) { return; }
            Document.SetColour(category, rgb);
            Save();
        }

        #endregion
    }
}
