using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Native;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round X-B: entourage: billboards of trees, shrubs, people and cars to populate a scene (and to swap for proper
    /// renders later).
    /// <list type="bullet">
    /// <item><b>Pictures</b>: 15 built-in ones drawn in code (<see cref="EntourageArt"/>, on a worker at start; X-B3
    /// redrew the people and trees and added a gum tree; traced drawings in <see cref="EntourageShapes"/> take over when
    /// there are any), plus the user's own PNG cut-outs (IMPORT PNG…, or X-B3's IMPORT FOLDER… for a whole pack; a plain
    /// background is made see-through), all fitted into 512 px layers.</item>
    /// <item><b>Placing</b>: the Place gun's ENTOURAGE mode (the wheel switches families ↔ entourage): RMB plants the
    /// chosen type on the aimed surface (its height ± 8 %, mirrored at random) or swaps the aimed billboard to it; LMB
    /// on a billboard removes it (else opens the panel); Z / X shrink / grow it 10 % (held: smoothly), X-B3's Alt + wheel
    /// resizes it live (saved on release); R mirrors it.</item>
    /// <item><b>Look</b>: silhouettes in a colour per category (trees, shrubs, people, cars, other), or the pictures'
    /// own colours; saved with the model.</item>
    /// <item><b>ENTOURAGE panel</b> (pause menu, or LMB with the Place gun in ENTOURAGE mode): the types as cards by
    /// category, the chosen one's details (count, remove all; an imported one's name, category, height, delete), the look
    /// and colours, IMPORT PNG…, PLANT.</item>
    /// </list>
    /// Saved in the .bimgo (entourage.json + entourage/) or, in a live session, in the model folder (so Export carries
    /// it). Billboards always face the camera, cast sun shadows and are clipped by sections; they don't block walking.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>A billboard's place in the picture array and its content box.</summary>
        private readonly record struct BillboardLayer(int Index, Vector4 Uv, float Aspect, float InverseLuminance, EntourageImage Picture);

        /// <summary>Planted billboards vary their type's height by up to this much either way.</summary>
        private const float PLANT_HEIGHT_JITTER = 0.08f;

        /// <summary>Thumbnails' size (px).</summary>
        private const int ENTOURAGE_THUMB = 128;

        private static readonly string[] ENTOURAGE_LOOKS = { "Colour silhouettes", "Own colours" };

        /// <summary>The model's entourage (placed billboards, imported types, the look).</summary>
        public EntourageStore Entourage { get; private set; }

        // The built-in pictures (drawn on a worker at start), the imported ones decoded (by file name), the layers by type key
        private Task<EntourageImage[]> _builtInTask;
        private EntourageImage[] _builtInPictures;
        private readonly Dictionary<string, EntourageImage> _importedPictures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BillboardLayer> _billboardLayers = new(StringComparer.Ordinal);
        private int _billboardPictureRevision = -1, _billboardInstanceKey = int.MinValue;
        private float[] _billboardData = Array.Empty<float>();
        private int _billboardKey;
        private bool _billboardsShown = true;
        private int _billboardPreview; // X-B3: bumped by a live resize (redraws without saving)

        // Panel thumbnails by type key (GL textures, made on first sight)
        private readonly Dictionary<string, uint> _entourageThumbs = new(StringComparer.Ordinal);

        // The type the Place gun plants, the panel's state
        private string _entourageType = "tree-round";
        private bool _entourageOpen;
        private string _entourageNotice;
        private readonly ScrollRegion _entourageScroll = new(), _entourageDetailScroll = new();
        private string _entourageRemoveArmed;
        private readonly Random _entourageRandom = new();
        private string _entouragePanelLabel;
        private int _entouragePanelLabelCount = -1;

        // Round X-B2: Revit Planting / Entourage swapped for billboards (made when the rules change), the elements their
        // swap hides, what the crosshair can pick, and the panel's list of Revit types
        private readonly List<EntourageItem> _derived = new();
        private int _derivedRevision = -1;
        private bool[] _entourageHidden, _entourageWant;
        private readonly List<EntourageItem> _pickable = new();
        private int _swappedCount, _hiddenSlotCount;
        private List<(string FamilyType, string Category, int Count, EntourageSlot First)> _slotTypes;
        private string _slotClearArmed;

        // Round X-B3: IMPORT FOLDER… (pictures prepared on a worker, added together when done), the cards' counts
        private Task<List<PreparedPicture>> _importTask;
        private int _importDone, _importTotal;
        private readonly Dictionary<string, int> _typeCounts = new(StringComparer.Ordinal);
        private int _typeCountsKey = int.MinValue;

        /// <summary>Round X-B3: a picture file read and cut out for import (or why it couldn't be).</summary>
        private sealed record PreparedPicture(string File, string Name, string Category, float Height, byte[] Png, string Error);

        #endregion

        #region Setup

        /// <summary>
        /// Creates the store (embedded in a .bimgo, else entourage.json in the model folder) and starts drawing the
        /// built-in pictures on a worker.
        /// </summary>
        private void InitialiseEntourage()
        {
            if (IsFileMode)
            {
                Entourage = new EntourageStore(null, Scene.ModelTitle, Scene.OriginOffset);
                Entourage.LoadFrom(_options.Document?.Entourage, _options.Document?.EntourageImages);
            }
            else
            {
                Entourage = new EntourageStore(EntourageFiles.SidecarFor(Scene.CommentsPath), Scene.ModelTitle, Scene.OriginOffset);
                Entourage.Load();
            }
            Entourage.Changed += UpdateTitle;
            _builtInTask = Task.Run(() =>
            {
                var pictures = new EntourageImage[EntourageArt.BUILT_INS.Length];
                for (int i = 0; i < pictures.Length; i++) { pictures[i] = EntourageArt.Draw(EntourageArt.BUILT_INS[i].Key); }
                return pictures;
            });
        }

        /// <summary>Frees the panel's thumbnails (session end).</summary>
        private void DisposeEntourage()
        {
            ClearEntourageThumbs();
        }

        private void ClearEntourageThumbs()
        {
            foreach (uint texture in _entourageThumbs.Values) { if (texture != 0) { Gl.DeleteTexture(texture); } }
            _entourageThumbs.Clear();
        }

        #endregion

        #region Types

        /// <summary>A type's category (built-in or imported; unknown → other).</summary>
        private string EntourageCategoryOf(string key)
        {
            EntourageBuiltIn? builtIn = EntourageArt.Find(key);
            if (builtIn != null) { return builtIn.Value.Category; }
            return Entourage?.Document.FindType(key)?.Category ?? EntourageCategories.OTHER;
        }

        /// <summary>A type's name ("Round tree", the picture's name).</summary>
        private string EntourageNameOf(string key)
        {
            EntourageBuiltIn? builtIn = EntourageArt.Find(key);
            if (builtIn != null) { return builtIn.Value.Name; }
            return Entourage?.Document.FindType(key)?.Name ?? "Missing picture";
        }

        /// <summary>A type's planting height (m).</summary>
        private float EntourageHeightOf(string key)
        {
            EntourageBuiltIn? builtIn = EntourageArt.Find(key);
            if (builtIn != null) { return builtIn.Value.Height; }
            return Entourage?.Document.FindType(key)?.Height ?? 1.7f;
        }

        /// <summary>The type the Place gun plants (shown on its panel).</summary>
        public string EntourageTypeName => EntourageNameOf(_entourageType);

        /// <summary>Billboards placed.</summary>
        public int EntourageCount => Entourage?.Document.Items.Count ?? 0;

        /// <summary>
        /// The types to show, in order: the built-ins, then the imported ones (each list in its own order).
        /// </summary>
        private IEnumerable<string> EntourageTypeKeys()
        {
            foreach (EntourageBuiltIn b in EntourageArt.BUILT_INS) { yield return b.Key; }
            if (Entourage == null) { yield break; }
            foreach (EntourageType t in Entourage.Document.Types) { yield return t.Key; }
        }

        /// <summary>
        /// Round X-B3: how many billboards of a type there are: planted ones plus the Revit instances swapped for it
        /// (counted again only when the entourage changes).
        /// </summary>
        private int EntourageTypeCount(string key)
        {
            int stamp = HashCode.Combine(Entourage.Revision, _derivedRevision, _derived.Count);
            if (stamp != _typeCountsKey)
            {
                _typeCountsKey = stamp;
                _typeCounts.Clear();
                foreach (EntourageItem item in Entourage.Document.Items) { _typeCounts[item.Type] = _typeCounts.GetValueOrDefault(item.Type) + 1; }
                foreach (EntourageItem item in _derived) { _typeCounts[item.Type] = _typeCounts.GetValueOrDefault(item.Type) + 1; }
            }
            return _typeCounts.GetValueOrDefault(key);
        }

        #endregion

        #region Pictures and instances

        /// <summary>
        /// Before the 3D passes (and the shadow maps): the pictures once the built-ins are drawn (again when imported
        /// types change), then the billboards whenever they change. Allocation-free while nothing changes.
        /// </summary>
        private void UpdateBillboards()
        {
            if (_renderer == null || Entourage == null) { return; }
            if (_derivedRevision != Entourage.Revision) { RebuildSwaps(); } // X-B2 (it may hide or show Revit elements; before the pictures are ready)
            if (_builtInPictures == null)
            {
                if (_builtInTask == null || !_builtInTask.IsCompleted) { return; }
                try { _builtInPictures = _builtInTask.Result; }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Built-in billboards not drawn: {ex}");
                    _builtInPictures = Array.Empty<EntourageImage>();
                }
                _builtInTask = null;
            }
            if (_billboardPictureRevision != Entourage.PictureRevision) { RebuildBillboardPictures(); }
            int key = HashCode.Combine(Entourage.Revision, _sceneRevision, _billboardsShown, _billboardPictureRevision, _billboardPreview);
            if (key != _billboardInstanceKey) { RebuildBillboardInstances(key); }
        }

        /// <summary>Lays the pictures out as layers (built-ins first) and uploads them.</summary>
        private void RebuildBillboardPictures()
        {
            _billboardPictureRevision = Entourage.PictureRevision;
            _billboardLayers.Clear();
            var pictures = new List<EntourageImage>();
            void Add(string key, EntourageImage picture)
            {
                if (picture == null || picture.IsEmpty) { return; }
                float size = picture.Width;
                var uv = new Vector4(picture.MinX / size, picture.MinY / size, picture.MaxX / size, picture.MaxY / size);
                _billboardLayers[key] = new BillboardLayer(pictures.Count, uv, picture.Aspect, 1f / MathF.Max(picture.MeanLuminance, 0.05f), picture);
                pictures.Add(picture);
            }
            for (int i = 0; i < _builtInPictures.Length && i < EntourageArt.BUILT_INS.Length; i++) { Add(EntourageArt.BUILT_INS[i].Key, _builtInPictures[i]); }
            foreach (EntourageType type in Entourage.Document.Types) { Add(type.Key, ImportedPicture(type)); }

            string error = _renderer.SetBillboardPictures(pictures);
            if (error != null)
            {
                _billboardLayers.Clear();
                Toast(error, 5f, important: true);
            }
            ClearEntourageThumbs();
        }

        /// <summary>An imported type's picture, decoded and fitted into a layer (cached by file name), or null.</summary>
        private EntourageImage ImportedPicture(EntourageType type)
        {
            if (type?.Image == null) { return null; }
            if (_importedPictures.TryGetValue(type.Image, out EntourageImage cached)) { return cached; }
            EntourageImage picture = null;
            if (Entourage.Images.TryGetValue(type.Image, out byte[] png))
            {
                EntourageImage decoded = DecodePicture(png, out string error);
                if (decoded == null) { Utilities.Log_Utils.Write($"Billboard picture {type.Image} ({type.Name}) unreadable: {error}"); }
                else
                {
                    decoded.KeyOutBackground();
                    picture = decoded.FitInto(SceneRenderer.BILLBOARD_LAYER);
                    picture.BleedEdges();
                }
            }
            _importedPictures[type.Image] = picture;
            return picture;
        }

        /// <summary>Writes the billboards' instance data (planted ones, then the Revit swaps that show) and uploads it.</summary>
        private void RebuildBillboardInstances(int key)
        {
            _billboardInstanceKey = key;
            EntourageDocument document = Entourage.Document;
            int needed = (document.Items.Count + _derived.Count) * SceneRenderer.BILLBOARD_FLOATS;
            if (_billboardData.Length < needed) { _billboardData = new float[Math.Max(needed, _billboardData.Length * 2)]; }
            int n = 0;
            _pickable.Clear();
            if (_billboardsShown)
            {
                float textured = document.Textured ? 1f : 0f;
                for (int i = 0; i < document.Items.Count + _derived.Count; i++)
                {
                    EntourageItem item = i < document.Items.Count ? document.Items[i] : _derived[i - document.Items.Count];
                    if (!_billboardLayers.TryGetValue(item.Type, out BillboardLayer layer)) { continue; }
                    if (item.Slot != null && !SlotShown(item.Slot)) { continue; }
                    _pickable.Add(item);
                    uint rgb = document.ColourOf(EntourageCategoryOf(item.Type));
                    int o = n * SceneRenderer.BILLBOARD_FLOATS;
                    float[] d = _billboardData;
                    d[o] = item.Local.X;
                    d[o + 1] = item.Local.Y;
                    d[o + 2] = item.Local.Z;
                    d[o + 3] = item.Height;
                    d[o + 4] = item.Height * layer.Aspect;
                    d[o + 5] = item.Flip ? -1f : 1f;
                    d[o + 6] = layer.Index;
                    d[o + 7] = textured;
                    d[o + 8] = ((rgb >> 16) & 0xFF) / 255f;
                    d[o + 9] = ((rgb >> 8) & 0xFF) / 255f;
                    d[o + 10] = (rgb & 0xFF) / 255f;
                    d[o + 11] = layer.InverseLuminance;
                    d[o + 12] = layer.Uv.X;
                    d[o + 13] = layer.Uv.Y;
                    d[o + 14] = layer.Uv.Z;
                    d[o + 15] = layer.Uv.W;
                    n++;
                }
            }
            _renderer.SetBillboards(_billboardData, n);
            _billboardKey++; // the shadow maps redraw
        }

        /// <summary>Decodes a PNG / JPEG into RGBA (top row first), or null.</summary>
        private static EntourageImage DecodePicture(byte[] bytes, out string error)
        {
            error = null;
            try
            {
                using var stream = new MemoryStream(bytes);
                using var decoded = new System.Drawing.Bitmap(stream);
                int width = decoded.Width, height = decoded.Height;
                if (width < 8 || height < 8 || (long)width * height > 64L * 1024 * 1024)
                {
                    error = $"{width} × {height} px: too small or too large";
                    return null;
                }
                using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) { graphics.DrawImage(decoded, 0, 0, width, height); }
                System.Drawing.Imaging.BitmapData bits = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rgba = new byte[width * height * 4];
                try
                {
                    var row = new byte[width * 4];
                    for (int y = 0; y < height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                        for (int x = 0; x < width; x++)
                        {
                            int i = (y * width + x) * 4, j = x * 4;
                            rgba[i] = row[j + 2];
                            rgba[i + 1] = row[j + 1];
                            rgba[i + 2] = row[j];
                            rgba[i + 3] = row[j + 3];
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bits);
                }
                return new EntourageImage(width, height, rgba);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Encodes RGBA (top row first) as PNG.</summary>
        private static byte[] EncodePng(EntourageImage image)
        {
            using var bitmap = new System.Drawing.Bitmap(image.Width, image.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData bits = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, image.Width, image.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[image.Width * 4];
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width; x++)
                    {
                        int i = (y * image.Width + x) * 4, j = x * 4;
                        row[j] = image.Rgba[i + 2];
                        row[j + 1] = image.Rgba[i + 1];
                        row[j + 2] = image.Rgba[i];
                        row[j + 3] = image.Rgba[i + 3];
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        #endregion

        #region Revit Planting / Entourage (X-B2)

        /// <summary>
        /// The rules changed: which Revit instances show a billboard, which are hidden (a billboard or nothing), and the
        /// billboards that stand in for them (in the slot's place, its own height unless an instance rule says).
        /// </summary>
        private void RebuildSwaps()
        {
            _derivedRevision = Entourage.Revision;
            _derived.Clear();
            _swappedCount = _hiddenSlotCount = 0;
            EntourageSlot[] slots = Scene.EntourageSlots;
            int elements = Scene.Elements.Length;
            if (slots.Length == 0 && _entourageHidden == null) { return; }

            _entourageWant ??= new bool[elements];
            Array.Clear(_entourageWant);
            EntourageSwaps.Index(Entourage.Document, out Dictionary<string, EntourageSwap> instances, out Dictionary<string, EntourageSwap> types);
            foreach (EntourageSlot slot in slots)
            {
                EntourageSwaps.Result result = EntourageSwaps.Resolve(Entourage.Document, slot, instances, types);
                if (slot.Element >= 0 && result.HidesElement) { _entourageWant[slot.Element] = true; }
                if (result.IsBillboard)
                {
                    _derived.Add(new EntourageItem { Id = slot.InstanceKey, Type = result.Type, Height = result.Height, Flip = result.Flip, Local = slot.Base, Slot = slot });
                    _swappedCount++;
                }
                else if (result.Type == EntourageSwaps.NONE) { _hiddenSlotCount++; }
            }
            ApplyEntourageHidden(_entourageWant);
        }

        /// <summary>
        /// Hides the Revit elements a swap stands in for (drawing, shadows, picking, collision) and brings back those
        /// it no longer does; only elements that change are touched.
        /// </summary>
        private void ApplyEntourageHidden(bool[] want)
        {
            if (_entourageHidden == null)
            {
                if (Array.IndexOf(want, true) < 0) { return; }
                _entourageHidden = new bool[want.Length];
            }
            bool changed = false;
            ElementRecord[] records = Scene.Elements;
            for (int e = 0; e < _entourageHidden.Length && e < records.Length; e++)
            {
                if (_entourageHidden[e] == want[e]) { continue; }
                _entourageHidden[e] = want[e];
                changed = true;
                bool profile = _profileHidden != null && e < _profileHidden.Length && _profileHidden[e];
                _renderer.SetElementHidden(e, want[e] || _hidden[e] || _userHidden[e] || profile);
                bool visible = !want[e] && !_hidden[e] && !_userHidden[e] && !profile && _groupVisible[SceneBatches.GroupOf(records[e])];
                _pickMask[e] = visible;
                _collisionMask[e] = visible && records[e].CategoryIndex != _doorCategory;
            }
            if (changed) { _sceneRevision++; }
        }

        /// <summary>True when a swap hides the element (shared with the colour profile's hiding: see <see cref="ProfileHides"/>).</summary>
        private bool EntourageHides(int element) => _entourageHidden != null && (uint)element < (uint)_entourageHidden.Length && _entourageHidden[element];

        /// <summary>
        /// Whether a Revit instance's billboard shows: always when its geometry wasn't extracted; else as its element
        /// would (demolished, hidden, the category toggled off, a profile hiding it: no billboard either).
        /// </summary>
        private bool SlotShown(EntourageSlot slot)
        {
            int e = slot.Element;
            if (e < 0 || e >= Scene.Elements.Length) { return true; }
            bool profile = _profileHidden != null && e < _profileHidden.Length && _profileHidden[e];
            return !_hidden[e] && !_userHidden[e] && !profile && _groupVisible[SceneBatches.GroupOf(Scene.Elements[e])];
        }

        /// <summary>The Revit types (Planting first), with how many instances and one of them (built once).</summary>
        private List<(string FamilyType, string Category, int Count, EntourageSlot First)> SlotTypes()
        {
            if (_slotTypes != null) { return _slotTypes; }
            _slotTypes = Scene.EntourageSlots
                .GroupBy(s => s.FamilyType, StringComparer.Ordinal)
                .Select(g => (g.Key, g.First().Category, g.Count(), g.First()))
                .OrderBy(t => t.Item2 == EntourageSlots.PLANTING ? 0 : 1)
                .ThenBy(t => t.Item1, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return _slotTypes;
        }

        /// <summary>What a Revit type shows (its rule, else the automatic choice or the element itself), as text.</summary>
        private string SlotTypeShows(string familyType, EntourageSlot first, out bool ruled)
        {
            EntourageSwap rule = Entourage.FindSwap(EntourageSwaps.TYPE, familyType);
            ruled = rule != null && EntourageSwaps.IsKnownType(Entourage.Document, rule.Type);
            string type = ruled ? rule.Type : Entourage.Document.SwapRevit ? EntourageSlots.AutoType(first) : EntourageSwaps.KEEP;
            return type switch
            {
                EntourageSwaps.KEEP => "Revit's own",
                EntourageSwaps.NONE => "Hidden",
                _ => EntourageNameOf(type)
            };
        }

        /// <summary>
        /// The panel's FROM REVIT section: automatic swapping on / off, the counts and CLEAR (instance changes), then each
        /// Revit type with what it shows and PICKED · REVIT · HIDE · AUTO.
        /// </summary>
        private float BuildEntourageFromRevit(FontAtlas f, InputState input, float x, float cy, float w)
        {
            EntourageSlot[] slots = Scene.EntourageSlots;
            cy = SectionLabel(f, x, cy, "FROM REVIT (PLANTING & ENTOURAGE)");
            if (slots.Length == 0)
            {
                cy += _ui.TextWrapped(f.Small, x, cy, w,
                    _live != null || !IsFileMode
                        ? "No Planting or Entourage in this snapshot (Go or F5 records them, whether or not their categories are ticked)."
                        : "This file has no Planting or Entourage recorded (exports from this build on record them).",
                    UiTheme.TEXT_FAINT, maxLines: 3) + S(10);
                return cy;
            }
            bool auto = Checkbox(f, input, x, cy + S(4), w, "Swap the rest automatically (by name and size)", Entourage.Document.SwapRevit);
            if (auto != Entourage.Document.SwapRevit) { Entourage.SetSwapRevit(auto); }
            cy += S(30);

            int instanceRules = 0;
            foreach (EntourageSwap swap in Entourage.Document.Swaps) { if (swap.Scope == EntourageSwaps.INSTANCE) { instanceRules++; } }
            Text.Clear().AppendGrouped(slots.Length).Append(" in the model · ").AppendGrouped(_swappedCount).Append(" billboards · ")
                .AppendGrouped(_hiddenSlotCount).Append(" hidden · ").AppendGrouped(instanceRules).Append(instanceRules == 1 ? " one-off change" : " one-off changes");
            _ui.TextWrapped(f.Small, x, cy, w - S(84), Text.Span, UiTheme.TEXT_MUTED, maxLines: 2);
            bool armed = _slotClearArmed == "instances";
            if (instanceRules > 0 && SmallButton(f, input, x + w - S(76), cy - S(4), S(76), S(26), armed ? "CONFIRM" : "CLEAR", danger: armed))
            {
                if (armed)
                {
                    int cleared = Entourage.ClearInstanceSwaps();
                    _entourageNotice = $"{cleared} one-off change{(cleared == 1 ? "" : "s")} cleared: every instance follows its type again";
                    _slotClearArmed = null;
                }
                else
                {
                    _slotClearArmed = "instances";
                    _entourageNotice = "CLEAR again to undo every one-off change (swaps, hides, sizes made with the Place gun)";
                }
            }
            cy += S(36);

            string picked = EntourageNameOf(_entourageType);
            foreach ((string familyType, string category, int count, EntourageSlot first) in SlotTypes())
            {
                _ui.TextWrapped(f.Body, x, cy, w - S(60), familyType, UiTheme.TEXT, maxLines: 1);
                Text.Clear().Append('×').AppendGrouped(count);
                _ui.TextRight(f.Small, x + w, cy + S(2), Text.Span, UiTheme.TEXT_MUTED);
                cy += S(20);
                string shows = SlotTypeShows(familyType, first, out bool ruled);
                Text.Clear().Append(category == EntourageSlots.PLANTING ? "Planting → " : "Entourage → ").Append(shows).Append(ruled ? " (set)" : " (auto)");
                _ui.TextWrapped(f.Small, x, cy, w, Text.Span, ruled ? UiTheme.PLACE_LABEL : UiTheme.TEXT_MUTED, maxLines: 1);
                cy += S(20);
                float bw = (w - S(18)) / 4f;
                if (SmallButton(f, input, x, cy, bw, S(26), "PICKED"))
                {
                    Entourage.SetSwap(EntourageSwaps.TYPE, familyType, _entourageType);
                    _entourageNotice = $"Every “{familyType}”: {picked}";
                }
                if (SmallButton(f, input, x + (bw + S(6)), cy, bw, S(26), "REVIT"))
                {
                    Entourage.SetSwap(EntourageSwaps.TYPE, familyType, EntourageSwaps.KEEP);
                    _entourageNotice = $"Every “{familyType}”: Revit's own (if its category is extracted)";
                }
                if (SmallButton(f, input, x + (bw + S(6)) * 2f, cy, bw, S(26), "HIDE"))
                {
                    Entourage.SetSwap(EntourageSwaps.TYPE, familyType, EntourageSwaps.NONE);
                    _entourageNotice = $"Every “{familyType}”: hidden";
                }
                if (SmallButton(f, input, x + (bw + S(6)) * 3f, cy, bw, S(26), "AUTO"))
                {
                    Entourage.ClearSwap(EntourageSwaps.TYPE, familyType);
                    _entourageNotice = $"Every “{familyType}”: automatic";
                }
                cy += S(38);
            }
            return cy + S(4);
        }

        #endregion

        #region Picking and editing

        /// <summary>
        /// The billboard the ray meets first (its picture's opaque part, as it faces the eye), nearer than
        /// <paramref name="maxDistance"/>, or null.
        /// </summary>
        public EntourageItem PickBillboard(Vector3 origin, Vector3 direction, float maxDistance)
        {
            if (Entourage == null || !_billboardsShown) { return null; }
            EntourageItem best = null;
            float bestT = maxDistance;
            foreach (EntourageItem item in _pickable)
            {
                if (!_billboardLayers.TryGetValue(item.Type, out BillboardLayer layer)) { continue; }
                Vector3 b = item.Local;
                var toward = new Vector3(origin.X - b.X, origin.Y - b.Y, 0f);
                float length = toward.Length();
                if (length < 1e-4f) { continue; }
                toward /= length;
                float denominator = Vector3.Dot(direction, toward);
                if (MathF.Abs(denominator) < 1e-5f) { continue; }
                float t = Vector3.Dot(b - origin, toward) / denominator;
                if (t <= 0f || t >= bestT) { continue; }
                Vector3 p = origin + direction * t;
                var right = new Vector3(-toward.Y, toward.X, 0f);
                float h = item.Height, w = h * layer.Aspect;
                float x = Vector3.Dot(p - b, right), z = p.Z - b.Z;
                if (MathF.Abs(x) > w * 0.5f || z < 0f || z > h) { continue; }
                float u = x / w + 0.5f;
                if (item.Flip) { u = 1f - u; }
                EntourageImage picture = layer.Picture;
                int px = (int)(picture.MinX + u * (picture.MaxX - picture.MinX));
                int py = (int)(picture.MinY + (1f - z / h) * (picture.MaxY - picture.MinY));
                if (picture.AlphaAt(px, py) < 128) { continue; }
                best = item;
                bestT = t;
            }
            return best;
        }

        /// <summary>Plants the chosen type where the aim meets a surface facing up.</summary>
        public void PlantBillboard(in Guns.AimInfo aim)
        {
            if (!aim.HasHit || aim.Hit.Normal.Z < 0.35f)
            {
                Sound.Play(SoundId.Error);
                Toast("Aim at the ground, a floor or a roof to plant it", 2.4f);
                return;
            }
            if (!_billboardLayers.ContainsKey(_entourageType))
            {
                Sound.Play(SoundId.Error);
                Toast(_builtInPictures == null ? "Billboards are still being drawn: a moment…" : "That picture is missing: pick another type (LMB)", 2.4f);
                return;
            }
            float height = EntourageHeightOf(_entourageType) * (1f + PLANT_HEIGHT_JITTER * (float)(_entourageRandom.NextDouble() * 2.0 - 1.0));
            EntourageItem item = Entourage.Add(_entourageType, aim.Hit.Point, height, _entourageRandom.Next(2) == 1);
            if (item == null)
            {
                Sound.Play(SoundId.Error);
                return;
            }
            Sound.Play(SoundId.CommentPlace);
            if (Entourage.LastError != null) { Toast(Entourage.LastError, 4f, important: true); }
        }

        /// <summary>Swaps a billboard to the chosen type (keeping its place and mirroring; the new type's height).</summary>
        public void SwapBillboard(EntourageItem item)
        {
            if (item == null) { return; }
            if (item.Type == _entourageType)
            {
                Toast($"It's already a {EntourageNameOf(item.Type)}: LMB with the Place gun away from billboards picks another type", 2.6f);
                return;
            }
            if (item.Slot != null)
            {
                // X-B2: this Revit instance becomes the picked type (its own size kept)
                Entourage.SetSwap(EntourageSwaps.INSTANCE, item.Slot.InstanceKey, _entourageType);
                Sound.Play(SoundId.UiClick);
                Toast($"This {item.Slot.Family}: {EntourageNameOf(_entourageType)} (every “{item.Slot.FamilyType}”: ENTOURAGE → FROM REVIT)", 3f);
                return;
            }
            float height = EntourageHeightOf(_entourageType) * (1f + PLANT_HEIGHT_JITTER * (float)(_entourageRandom.NextDouble() * 2.0 - 1.0));
            Entourage.Update(item, _entourageType, height, item.Flip);
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>Removes a billboard.</summary>
        public void RemoveBillboard(EntourageItem item)
        {
            if (item == null) { return; }
            string name = EntourageNameOf(item.Type);
            if (item.Slot != null)
            {
                // X-B2: a Revit instance: hidden (neither the billboard nor the element; ENTOURAGE → FROM REVIT clears it)
                Entourage.SetSwap(EntourageSwaps.INSTANCE, item.Slot.InstanceKey, EntourageSwaps.NONE);
                Sound.Play(SoundId.Remove);
                Toast($"This {item.Slot.Family} hidden in the walkthrough (ENTOURAGE → FROM REVIT → CLEAR brings it back)", 3f);
                return;
            }
            Entourage.Remove(item);
            Sound.Play(SoundId.Remove);
            Toast($"{name} removed", 1.6f);
        }

        /// <summary>Makes a billboard bigger or smaller.</summary>
        public void ResizeBillboard(EntourageItem item, float factor)
        {
            if (item == null) { return; }
            float height = Math.Clamp(item.Height * factor, EntourageItem.MIN_HEIGHT, EntourageItem.MAX_HEIGHT);
            if (item.Slot != null) { Entourage.SetSwap(EntourageSwaps.INSTANCE, item.Slot.InstanceKey, item.Type, height: height); } // X-B2
            else { Entourage.Update(item, item.Type, height, item.Flip); }
            Toast($"{EntourageNameOf(item.Type)}: {UnitFormat.Length(height, 2)} high", 1.2f);
        }

        /// <summary>
        /// Round X-B3: a live resize's step: the billboard shows at <paramref name="height"/> straight away, nothing is
        /// saved until <see cref="CommitBillboardHeight"/> (on release).
        /// </summary>
        /// <returns>The height used (clamped).</returns>
        public float PreviewBillboardHeight(EntourageItem item, float height)
        {
            if (item == null) { return height; }
            height = Math.Clamp(height, EntourageItem.MIN_HEIGHT, EntourageItem.MAX_HEIGHT);
            if (item.Height != height)
            {
                item.Height = height;
                _billboardPreview++;
            }
            return height;
        }

        /// <summary>
        /// Round X-B3: a live resize ended: saves the height (a planted billboard's own; a Revit instance's one-off rule).
        /// </summary>
        public void CommitBillboardHeight(EntourageItem item, float height)
        {
            if (item == null) { return; }
            height = Math.Clamp(height, EntourageItem.MIN_HEIGHT, EntourageItem.MAX_HEIGHT);
            if (item.Slot != null) { Entourage.SetSwap(EntourageSwaps.INSTANCE, item.Slot.InstanceKey, item.Type, height: height); }
            else if (Entourage.Document.Items.Contains(item)) { Entourage.Update(item, item.Type, height, item.Flip); }
            else { return; } // removed meanwhile
            Sound.Play(SoundId.UiClick);
            Toast($"{EntourageNameOf(item.Type)}: {UnitFormat.Length(height, 2)} high", 1.4f);
        }

        /// <summary>Mirrors a billboard.</summary>
        public void FlipBillboard(EntourageItem item)
        {
            if (item == null) { return; }
            if (item.Slot != null) { Entourage.SetSwap(EntourageSwaps.INSTANCE, item.Slot.InstanceKey, item.Type, flip: !item.Flip); } // X-B2
            else { Entourage.Update(item, item.Type, item.Height, !item.Flip); }
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>The aimed billboard's outline (the Place gun in ENTOURAGE mode).</summary>
        public void DrawBillboardFrame(Overlay3D overlay, EntourageItem item, uint colour)
        {
            if (item == null || !_billboardLayers.TryGetValue(item.Type, out BillboardLayer layer)) { return; }
            Vector3 b = item.Local, eye = Camera.Position;
            var toward = new Vector3(eye.X - b.X, eye.Y - b.Y, 0f);
            if (toward.LengthSquared() < 1e-8f) { return; }
            toward = Vector3.Normalize(toward);
            var right = new Vector3(-toward.Y, toward.X, 0f) * (item.Height * layer.Aspect * 0.5f);
            Vector3 up = new(0f, 0f, item.Height), lift = toward * 0.02f;
            Vector3 a = b - right + lift, c = b + right + lift;
            overlay.Line(a, c, 2f, colour);
            overlay.Line(c, c + up, 2f, colour);
            overlay.Line(c + up, a + up, 2f, colour);
            overlay.Line(a + up, a, 2f, colour);
        }

        #endregion

        #region Panel: open / close

        /// <summary>True while the ENTOURAGE panel shows (it replaces the pause menu).</summary>
        private bool IsEntouragePanelOpen => _entourageOpen;

        /// <summary>"ENTOURAGE (n)" for the pause menu.</summary>
        private string EntourageMenuLabel()
        {
            int count = EntourageCount;
            if (count != _entouragePanelLabelCount || _entouragePanelLabel == null)
            {
                _entouragePanelLabelCount = count;
                _entouragePanelLabel = count == 0 ? "ENTOURAGE" : $"ENTOURAGE ({count})";
            }
            return _entouragePanelLabel;
        }

        /// <summary>Opens the ENTOURAGE panel (pausing the walkthrough).</summary>
        public void OpenEntourage()
        {
            if (Entourage == null) { return; }
            if (!_paused) { SetPaused(true); }
            _entourageOpen = true;
            _entourageNotice = null;
            _entourageRemoveArmed = null;
        }

        /// <summary>Closes the panel (back to the pause menu).</summary>
        /// <returns>True if it was open.</returns>
        private bool CloseEntourage()
        {
            if (!_entourageOpen) { return false; }
            _entourageOpen = false;
            _entourageRemoveArmed = null;
            return true;
        }

        /// <summary>PLANT: back to the walkthrough with the Place gun in ENTOURAGE mode.</summary>
        private void PlantFromEntourage()
        {
            CloseEntourage();
            SetPaused(false);
            int slot = Array.IndexOf(_guns, _placeGun);
            if (slot >= 0) { SelectGun(slot); }
            _placeGun.SetEntourageMode(true);
            Toast($"{EntourageNameOf(_entourageType)}: {Hotkeys.Label(InputAction.GunSecondary)} plants it where you aim · {Hotkeys.Label(InputAction.GunPrimary)} on one removes it", 4f);
        }

        #endregion

        #region Panel

        /// <summary>
        /// The ENTOURAGE panel: the types as cards by category (left; a click picks one), the picked type, the look and
        /// colours (right); IMPORT PNG… and PLANT in the footer.
        /// </summary>
        private void BuildEntouragePanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            if (!input.LeftDown) { _activeSlider = -1; }
            if (!_billboardLayers.ContainsKey(_entourageType) && _builtInPictures != null) { _entourageType = EntourageArt.BUILT_INS[0].Key; }

            Text.Clear().AppendGrouped(EntourageCount).Append(EntourageCount == 1 ? " billboard" : " billboards").Append(" · ")
                .AppendGrouped(Entourage.Document.Types.Count).Append(" own pictures · saved to ").Append(Entourage.FileName);
            PanelFrame frame = BeginPanel(PanelSize.Wide, "ENTOURAGE", UiTheme.PLACE_LABEL, Text.Span);
            float x = frame.Ix, y = frame.BodyTop, w = frame.Iw, bottom = frame.BodyBottom;

            float lw = MathF.Floor(w * 0.56f);
            BuildEntourageCards(f, input, x, y, lw, bottom);
            float rx = x + lw + S(32), rw = w - lw - S(32);
            BuildEntourageDetails(f, input, rx, y, rw, bottom);

            PollFolderImport(); // X-B3
            bool importing = _importTask != null;
            int import = FooterAction("IMPORT PNG…", enabled: !importing);
            int folder = FooterAction("IMPORT FOLDER…", enabled: !importing); // X-B3
            int plant = FooterAction("PLANT", ShellStyle.Primary, _billboardLayers.ContainsKey(_entourageType));
            int pressed = EndPanel(frame, _entourageNotice, HINT_ENTOURAGE.Text);
            if (pressed == FOOTER_NONE || pressed == FOOTER_LEFT) { return; }
            if (pressed == import) { ImportEntouragePicture(); }
            else if (pressed == folder) { ImportEntourageFolder(); }
            else if (pressed == plant) { PlantFromEntourage(); }
        }

        private static readonly KeyHint HINT_ENTOURAGE = new(() =>
            $"Place gun in ENTOURAGE mode ({Hotkeys.Label(InputAction.GunModeNext)} switches): {Hotkeys.Label(InputAction.GunSecondary)} plants · {Hotkeys.Label(InputAction.GunPrimary)} on one removes it · {Hotkeys.Label(InputAction.HoldStepDown)} / {Hotkeys.Label(InputAction.HoldStepUp)} or {Hotkeys.Label(InputAction.HoldScale)}+wheel size · {Hotkeys.Label(InputAction.HoldRotate)} mirror");

        /// <summary>The types as cards, a section per category.</summary>
        private void BuildEntourageCards(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            float top = BeginScroll(_entourageScroll, input, x, y, w, bottom - y, out float cw);
            float card = S(104), gap = S(8);
            int perRow = Math.Max(1, (int)((cw + gap) / (card + gap)));
            float cy = top;
            if (_builtInPictures == null)
            {
                _ui.Text(f.Body, x, cy, "Drawing the built-in billboards…", UiTheme.TEXT_MUTED);
                cy += S(30);
            }
            for (int c = 0; c < EntourageCategories.ALL.Length; c++)
            {
                string category = EntourageCategories.ALL[c];
                int column = 0;
                bool any = false;
                foreach (string key in EntourageTypeKeys())
                {
                    if (EntourageCategoryOf(key) != category) { continue; }
                    if (!any)
                    {
                        cy = SectionLabel(f, x, cy, EntourageCategories.LABELS[c].ToUpperInvariant());
                        any = true;
                    }
                    float cx = x + column * (card + gap);
                    BuildEntourageCard(f, input, key, cx, cy, card);
                    if (++column >= perRow)
                    {
                        column = 0;
                        cy += card + S(22) + gap;
                    }
                }
                if (any)
                {
                    if (column > 0) { cy += card + S(22) + gap; }
                    cy += S(6);
                }
            }
            EndScroll(_entourageScroll, input, cy);
        }

        /// <summary>One type's card: its picture, name and how many are placed; a click picks it.</summary>
        private void BuildEntourageCard(FontAtlas f, InputState input, string key, float x, float y, float size)
        {
            bool on = key == _entourageType;
            bool hover = Hover(input, x, y, size, size + S(20));
            _ui.Rect(x, y, size, size, on ? Rgba.WithAlpha(UiTheme.PLACE, 0.18f) : hover ? Rgba.Hex(0xFFFFFF, 0.07f) : UiTheme.CONTROL);
            _ui.Outline(x, y, size, size, MathF.Max(1f, UiScale), on ? UiTheme.PLACE : UiTheme.CONTROL_BORDER);
            uint thumb = EntourageThumb(key);
            uint tint = 0xFFFFFFFF;
            if (thumb != 0 && !Entourage.Document.Textured)
            {
                uint rgb = Entourage.Document.ColourOf(EntourageCategoryOf(key));
                tint = Rgba.Hex(rgb);
            }
            if (thumb != 0) { _ui.Image(thumb, x + S(6), y + S(6), size - S(12), size - S(12), _window.Width, _window.Height, tint); }
            else { _ui.TextCentred(f.Small, x + size * 0.5f, y + size * 0.5f - S(6), _builtInPictures == null ? "…" : "missing", UiTheme.TEXT_FAINT); }
            int count = EntourageTypeCount(key); // X-B3: Revit swaps included
            if (count > 0)
            {
                Text.Clear().Append(count);
                _ui.TextRight(f.Small, x + size - S(6), y + S(4), Text.Span, UiTheme.PLACE_LABEL);
            }
            _ui.TextWrapped(f.Small, x, y + size + S(3), size, EntourageNameOf(key), on ? UiTheme.PLACE_LABEL : UiTheme.TEXT_SOFT, maxLines: 1);
            if (hover && input.LeftPressed)
            {
                input.ConsumeClicks();
                Sound.Play(SoundId.UiClick);
                _entourageType = key;
                _entourageRemoveArmed = null;
                _entourageNotice = null;
            }
        }

        /// <summary>
        /// A type's thumbnail (made on first sight from its layer picture; the silhouettes' look tints it in the
        /// panel: white is drawn grey-scale).
        /// </summary>
        private unsafe uint EntourageThumb(string key)
        {
            if (_entourageThumbs.TryGetValue(key, out uint texture)) { return texture; }
            if (!_billboardLayers.TryGetValue(key, out BillboardLayer layer)) { return 0; }
            EntourageImage small = layer.Picture.FitInto(ENTOURAGE_THUMB);
            if (!Entourage.Document.Textured)
            {
                // Grey-scale (so the card's tint shows the category colour, shaded like the silhouettes)
                byte[] p = small.Rgba;
                float inverse = layer.InverseLuminance;
                for (int i = 0; i < p.Length; i += 4)
                {
                    float lum = (0.299f * p[i] + 0.587f * p[i + 1] + 0.114f * p[i + 2]) / 255f * inverse;
                    byte g = (byte)Math.Clamp((int)(255f * Math.Clamp(1f + (lum - 1f) * 0.7f, 0.45f, 1.5f) / 1.5f + 0.5f), 0, 255);
                    p[i] = p[i + 1] = p[i + 2] = g;
                }
            }
            texture = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D, texture);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
            Gl.PixelStore(Gl.UNPACK_ALIGNMENT, 4);
            fixed (byte* data = small.Rgba)
            {
                Gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGBA8, small.Width, small.Height, Gl.RGBA, Gl.UNSIGNED_BYTE, data);
            }
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            _entourageThumbs[key] = texture;
            return texture;
        }

        /// <summary>The picked type (count, remove all; an imported one's name, category, height, delete), then the look.</summary>
        private void BuildEntourageDetails(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            float cy = BeginScroll(_entourageDetailScroll, input, x, y, w, bottom - y, out float cw);
            string key = _entourageType;
            EntourageType imported = Entourage.Document.FindType(key);

            cy = SectionLabel(f, x, cy, "PICKED");
            _ui.TextWrapped(f.Bold, x, cy, cw, EntourageNameOf(key), UiTheme.TEXT, maxLines: 1);
            cy += S(24);
            int count = 0;
            foreach (EntourageItem item in Entourage.Document.Items) { if (item.Type == key) { count++; } }
            int fromRevit = EntourageTypeCount(key) - count; // X-B3
            Text.Clear().Append(EntourageCategories.LABELS[EntourageCategories.IndexOf(EntourageCategoryOf(key))]).Append(" · planted ")
                .AppendLength(EntourageHeightOf(key), 1).Append(" high · ").AppendGrouped(count).Append(" placed");
            if (fromRevit > 0) { Text.Append(" + ").AppendGrouped(fromRevit).Append(" from Revit"); }
            _ui.Text(f.Small, x, cy, Text.Span, UiTheme.TEXT_MUTED);
            cy += S(26);

            if (imported != null)
            {
                // Its name, category and planting height
                if (SmallButton(f, input, x, cy, S(110), S(28), "RENAME…"))
                {
                    EntourageType target = imported;
                    BeginTextEdit("PICTURE NAME", target.Name, EntourageType.MAX_NAME, text =>
                    {
                        if (text.Length > 0) { Entourage.UpdateType(target, text, null, target.Height); }
                    });
                }
                cy += S(38);
                int d = StepRow(f, input, x, cy, cw, "Category");
                int index = EntourageCategories.IndexOf(imported.Category);
                if (d != 0)
                {
                    index = (index + d + EntourageCategories.ALL.Length) % EntourageCategories.ALL.Length;
                    Entourage.UpdateType(imported, null, EntourageCategories.ALL[index], imported.Height);
                }
                Text.Clear().Append(EntourageCategories.LABELS[index]);
                StepRowValue(f, x, cy, cw);
                cy += S(38);
                d = StepRow(f, input, x, cy, cw, "Height");
                if (d != 0)
                {
                    float step = imported.Height < 3f ? 0.1f : 0.5f;
                    Entourage.UpdateType(imported, null, null, MathF.Round((imported.Height + d * step) * 10f) / 10f);
                }
                Text.Clear().AppendLength(imported.Height, 1);
                StepRowValue(f, x, cy, cw);
                cy += S(42);
            }

            // Remove all of this type (armed: a second click does it), delete an imported picture
            bool armed = _entourageRemoveArmed == key;
            if (count > 0 && SmallButton(f, input, x, cy, S(200), S(28), armed ? "CONFIRM: REMOVE ALL" : "REMOVE ALL OF THESE", danger: armed))
            {
                if (armed)
                {
                    int removed = Entourage.RemoveAll(key);
                    _entourageNotice = $"{removed} removed";
                    _entourageRemoveArmed = null;
                }
                else { _entourageRemoveArmed = key; }
            }
            if (imported != null && SmallButton(f, input, x + cw - S(150), cy, S(150), S(28), "DELETE PICTURE", danger: true))
            {
                if (_entourageRemoveArmed == "delete:" + key)
                {
                    Entourage.RemoveType(imported);
                    _importedPictures.Remove(imported.Image);
                    _entourageType = EntourageArt.BUILT_INS[0].Key;
                    _entourageNotice = $"{imported.Name} deleted (and its billboards)";
                    _entourageRemoveArmed = null;
                }
                else
                {
                    _entourageRemoveArmed = "delete:" + key;
                    _entourageNotice = $"Click DELETE PICTURE again to delete {imported.Name} and its billboards";
                }
            }
            cy += S(44);

            // X-B2: Revit Planting / Entourage
            cy = BuildEntourageFromRevit(f, input, x, cy, cw);

            // The look
            cy = SectionLabel(f, x, cy, "LOOK (SAVED WITH THE MODEL)");
            int look = Segmented(f, input, x, cy, cw, ENTOURAGE_LOOKS, Entourage.Document.Textured ? 1 : 0);
            if ((look == 1) != Entourage.Document.Textured)
            {
                Entourage.SetTextured(look == 1);
                ClearEntourageThumbs();
            }
            cy += S(42);
            if (!Entourage.Document.Textured)
            {
                for (int c = 0; c < EntourageCategories.ALL.Length; c++)
                {
                    string category = EntourageCategories.ALL[c];
                    uint rgb = Entourage.Document.ColourOf(category);
                    _ui.Rect(x, cy + S(3), S(22), S(22), Rgba.Hex(rgb));
                    _ui.Outline(x, cy + S(3), S(22), S(22), MathF.Max(1f, UiScale), UiTheme.CONTROL_BORDER);
                    _ui.Text(f.Body, x + S(32), cy + S(5), EntourageCategories.LABELS[c], UiTheme.TEXT_SOFT);
                    if (SmallButton(f, input, x + cw - S(170), cy, S(92), S(28), "CHANGE…"))
                    {
                        _window.Input.ReleaseAll();
                        uint? picked = FileDialogs.PickColour(_window.Handle, rgb);
                        _window.Input.ReleaseAll();
                        if (picked.HasValue) { Entourage.SetColour(category, picked.Value); }
                    }
                    if (SmallButton(f, input, x + cw - S(70), cy, S(70), S(28), "RESET"))
                    {
                        Entourage.SetColour(category, EntourageCategories.DEFAULT_COLOURS[c]);
                    }
                    cy += S(34);
                }
            }
            else
            {
                cy += _ui.TextWrapped(f.Small, x, cy, cw, "The pictures in their own colours: the built-ins painted, your PNGs as they are.",
                    UiTheme.TEXT_FAINT, maxLines: 2) + S(8);
            }
            cy += S(6);
            bool shown = Checkbox(f, input, x, cy + S(4), cw, "Show billboards", _billboardsShown);
            if (shown != _billboardsShown) { _billboardsShown = shown; }
            cy += S(34);
            cy += _ui.TextWrapped(f.Small, x, cy, cw,
                "Billboards face the camera, cast sun shadows and show in photos and videos; you walk through them. IMPORT PNG… takes a cut-out with a transparent background (a plain one is made see-through).",
                UiTheme.TEXT_FAINT, maxLines: 5) + S(8);
            EndScroll(_entourageDetailScroll, input, cy);
        }

        /// <summary>
        /// IMPORT PNG…: a picture becomes a billboard type (cropped to its content, at most 1024 px, a plain background
        /// made see-through); its category is guessed from the file name.
        /// </summary>
        private void ImportEntouragePicture()
        {
            _window.Input.ReleaseAll();
            string path = FileDialogs.ShowOpen(_window.Handle, "Billboard picture (a PNG cut-out)", "Pictures (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*", null);
            _window.Input.ReleaseAll();
            if (path == null) { return; }
            PreparedPicture picture = PreparePicture(path);
            if (picture.Error != null)
            {
                _entourageNotice = picture.Error;
                Sound.Play(SoundId.Error);
                return;
            }
            EntourageType type = Entourage.AddType(picture.Name, picture.Category, picture.Height, picture.Png);
            if (type == null)
            {
                _entourageNotice = Entourage.LastError ?? "The picture couldn't be added";
                Sound.Play(SoundId.Error);
                return;
            }
            _entourageType = type.Key;
            _entourageNotice = $"{type.Name} added as {EntourageCategories.LABELS[EntourageCategories.IndexOf(picture.Category)].ToLowerInvariant()} ({UnitFormat.Length(picture.Height, 1)}): change its category and height on the right";
            Sound.Play(SoundId.Commit);
        }

        /// <summary>
        /// Reads a picture file and cuts it out for import (any thread): cropped to its content, at most 1024 px, a plain
        /// background made see-through; the category guessed from the file name and a height to suit.
        /// </summary>
        private static PreparedPicture PreparePicture(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            PreparedPicture Fail(string error) => new(path, name, null, 0f, null, error);
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 64L * 1024 * 1024) { return Fail("That picture is too large (over 64 MB)"); }
                EntourageImage image = DecodePicture(File.ReadAllBytes(path), out string error);
                if (image == null) { return Fail("The picture couldn't be read: " + error); }
                bool keyed = image.KeyOutBackground();
                if (image.IsEmpty || (!keyed && image.MinX == 0 && image.MinY == 0 && image.MaxX == image.Width && image.MaxY == image.Height && NoTransparency(image)))
                {
                    return Fail("The picture needs a transparent (or plain) background to cut out");
                }
                int longest = Math.Max(image.MaxX - image.MinX, image.MaxY - image.MinY);
                EntourageImage stored = image.FitInto(Math.Min(1024, longest + 2));
                string category = GuessCategory(name);
                float height = category switch
                {
                    EntourageCategories.TREE => 7f,
                    EntourageCategories.SHRUB => 1.2f,
                    EntourageCategories.PERSON => 1.72f,
                    EntourageCategories.CAR => 1.5f,
                    _ => 2f
                };
                return new PreparedPicture(path, name, category, height, EncodePng(stored), null);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Billboard import failed ({path}): {ex}");
                return Fail("The picture couldn't be imported: " + ex.Message);
            }
        }

        /// <summary>
        /// Round X-B3: IMPORT FOLDER…: every PNG / JPEG in a folder (not its sub-folders) becomes a type (a licensed pack
        /// you own, say), prepared on a worker with the count in the notice strip, then added together (one save).
        /// </summary>
        private void ImportEntourageFolder()
        {
            if (_importTask != null) { return; }
            _window.Input.ReleaseAll();
            string folder = FileDialogs.ShowFolder(_window.Handle, "A folder of billboard pictures (PNG cut-outs or plain backgrounds): each becomes a type", null);
            _window.Input.ReleaseAll();
            if (folder == null) { return; }
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(folder)
                    .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _entourageNotice = "The folder couldn't be read: " + ex.Message;
                Sound.Play(SoundId.Error);
                return;
            }
            if (files.Length == 0)
            {
                _entourageNotice = "No PNG or JPEG pictures in that folder (sub-folders aren't searched)";
                Sound.Play(SoundId.Error);
                return;
            }
            _importDone = 0;
            _importTotal = files.Length;
            _entourageNotice = $"Importing {files.Length} pictures…";
            _importTask = Task.Run(() =>
            {
                var prepared = new List<PreparedPicture>(files.Length);
                foreach (string file in files)
                {
                    prepared.Add(PreparePicture(file));
                    Interlocked.Increment(ref _importDone);
                }
                return prepared;
            });
        }

        /// <summary>Round X-B3: the folder import's progress (notice strip), and its pictures added once ready.</summary>
        private void PollFolderImport()
        {
            if (_importTask == null) { return; }
            if (!_importTask.IsCompleted)
            {
                _entourageNotice = $"Importing pictures: {Volatile.Read(ref _importDone)} / {_importTotal}…";
                return;
            }
            List<PreparedPicture> prepared;
            try { prepared = _importTask.Result; }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Folder import failed: {ex}");
                prepared = new List<PreparedPicture>();
            }
            _importTask = null;
            var good = prepared.Where(p => p.Error == null).ToList();
            List<EntourageType> added = Entourage.AddTypes(good.Select(p => (p.Name, p.Category, p.Height, p.Png)));
            int skipped = prepared.Count - added.Count;
            if (added.Count > 0) { _entourageType = added[0].Key; }
            var notice = new System.Text.StringBuilder();
            notice.Append(added.Count).Append(added.Count == 1 ? " picture added" : " pictures added");
            if (added.Count > 0)
            {
                // By category ("12 people, 3 trees")
                var byCategory = added.GroupBy(t => t.Category).Select(g => $"{g.Count()} {EntourageCategories.LABELS[EntourageCategories.IndexOf(g.Key)].ToLowerInvariant()}");
                notice.Append(" (").Append(string.Join(", ", byCategory)).Append(": categories guessed from the names)");
            }
            if (skipped > 0)
            {
                PreparedPicture first = prepared.FirstOrDefault(p => p.Error != null);
                notice.Append(" · ").Append(skipped).Append(" skipped");
                if (first != null) { notice.Append(" (").Append(Path.GetFileName(first.File)).Append(": ").Append(first.Error).Append(')'); }
                else if (Entourage.LastError != null) { notice.Append(" (").Append(Entourage.LastError).Append(')'); }
            }
            _entourageNotice = notice.ToString();
            Sound.Play(added.Count > 0 ? SoundId.Commit : SoundId.Error);
        }

        private static bool NoTransparency(EntourageImage image)
        {
            for (int i = 3; i < image.Rgba.Length; i += 4) { if (image.Rgba[i] < 250) { return false; } }
            return true;
        }

        /// <summary>A category from a file name ("oak tree.png" → tree).</summary>
        private static string GuessCategory(string name)
        {
            string n = (name ?? string.Empty).ToLowerInvariant();
            static bool Has(string text, params string[] words) => words.Any(text.Contains);
            if (Has(n, "tree", "palm", "oak", "gum", "pine", "eucalypt", "conifer")) { return EntourageCategories.TREE; }
            if (Has(n, "shrub", "bush", "hedge", "grass", "plant", "flower")) { return EntourageCategories.SHRUB; }
            if (Has(n, "person", "people", "man", "woman", "child", "kid", "boy", "girl", "human", "figure")) { return EntourageCategories.PERSON; }
            if (Has(n, "car", "van", "truck", "suv", "ute", "vehicle", "bike", "bus")) { return EntourageCategories.CAR; }
            return EntourageCategories.OTHER;
        }

        #endregion
    }
}
