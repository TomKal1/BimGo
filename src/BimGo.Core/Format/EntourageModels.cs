using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Round X-B: a billboard type made from the user's own picture (a PNG cut-out): its name, category, the height a
    /// new one is planted at and the picture's file (<c>entourage/&lt;file&gt;</c> in a .bimgo, the images folder beside
    /// <c>entourage.json</c> in a live session). Built-in types are not stored (<see cref="EntourageArt.BUILT_INS"/>).
    /// </summary>
    public sealed class EntourageType
    {
        /// <summary>Longest name.</summary>
        public const int MAX_NAME = 60;

        /// <summary>Stable id (GUID, no dashes).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Shown in the panel.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>See <see cref="EntourageCategories"/>.</summary>
        public string Category { get; set; } = EntourageCategories.OTHER;

        /// <summary>Default height of a new one (m).</summary>
        public float Height { get; set; } = 1.7f;

        /// <summary>The picture's file name (<c>&lt;id&gt;.png</c>).</summary>
        public string Image { get; set; }

        /// <summary>The type's key as items refer to it (<c>img:&lt;id&gt;</c>).</summary>
        [JsonIgnore]
        public string Key => EntourageDocument.IMPORTED_PREFIX + Id;

        /// <summary>Clamps and fills in values; false when the type is unusable (no picture).</summary>
        public bool Clean()
        {
            if (string.IsNullOrWhiteSpace(Id) || !EntourageDocument.IsSafeId(Id)) { Id = Guid.NewGuid().ToString("N"); }
            Name = string.IsNullOrWhiteSpace(Name) ? "Picture" : Name.Trim();
            if (Name.Length > MAX_NAME) { Name = Name[..MAX_NAME]; }
            Category = EntourageCategories.Normalise(Category);
            Height = float.IsFinite(Height) ? Math.Clamp(Height, EntourageItem.MIN_HEIGHT, EntourageItem.MAX_HEIGHT) : 1.7f;
            return EntourageDocument.IsSafeImageName(Image);
        }
    }

    /// <summary>
    /// Round X-B: one placed billboard: its type (a built-in key such as <c>tree-round</c>, or <c>img:&lt;id&gt;</c>),
    /// where it stands (the base, Revit internal metres like bookmarks, so it stays put across snapshots), its height
    /// and whether its picture is mirrored. It always turns to face the camera.
    /// </summary>
    public sealed class EntourageItem
    {
        /// <summary>Smallest / largest height (m).</summary>
        public const float MIN_HEIGHT = 0.2f, MAX_HEIGHT = 60f;

        /// <summary>Stable id (GUID, no dashes).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The type's key.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Base X (Revit internal metres).</summary>
        public double X { get; set; }

        /// <summary>Base Y (Revit internal metres).</summary>
        public double Y { get; set; }

        /// <summary>Base Z (Revit internal metres).</summary>
        public double Z { get; set; }

        /// <summary>Height (m).</summary>
        public float Height { get; set; } = 1.7f;

        /// <summary>Mirrored left to right.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Flip { get; set; }

        /// <summary>The base in scene-local metres (set by the session from the scene's origin; not saved).</summary>
        [JsonIgnore]
        public Vector3 Local { get; set; }

        /// <summary>Round X-B2: the Revit instance this billboard stands in for (a swap), or null for a planted one.</summary>
        [JsonIgnore]
        public EntourageSlot Slot { get; set; }

        /// <summary>Clamps; false when the item is unusable (no type, bad numbers).</summary>
        public bool Clean()
        {
            if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Z) || string.IsNullOrWhiteSpace(Type)) { return false; }
            if (string.IsNullOrWhiteSpace(Id)) { Id = Guid.NewGuid().ToString("N"); }
            Type = Type.Trim();
            Height = float.IsFinite(Height) ? Math.Clamp(Height, MIN_HEIGHT, MAX_HEIGHT) : 1.7f;
            return true;
        }
    }

    /// <summary>
    /// Round X-B2: what to show for Revit Planting / Entourage: for one instance (its UniqueId, "link/element" in a
    /// link) or every instance of a type ("Family : Type"): a billboard type, <see cref="EntourageSwaps.KEEP"/> (the
    /// Revit element itself) or <see cref="EntourageSwaps.NONE"/> (nothing). An instance rule may also set the height
    /// and mirroring. Rules are remembered in the model folder, so every later Go, F5 and Export applies them.
    /// </summary>
    public sealed class EntourageSwap
    {
        /// <summary><see cref="EntourageSwaps.INSTANCE"/> or <see cref="EntourageSwaps.TYPE"/>.</summary>
        public string Scope { get; set; } = EntourageSwaps.TYPE;

        /// <summary>The instance key or "Family : Type".</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>A billboard type key, "keep" or "none".</summary>
        public string Type { get; set; } = EntourageSwaps.KEEP;

        /// <summary>Instance rules: the billboard's height (m), or null for the Revit element's.</summary>
        public float? Height { get; set; }

        /// <summary>Instance rules: mirrored, or null for the default.</summary>
        public bool? Flip { get; set; }
    }

    /// <summary>
    /// Round X-B2: which billboard (if any) stands in for a Revit Planting / Entourage instance: its instance rule, else
    /// its type's rule, else (with <see cref="EntourageDocument.SwapRevit"/> on) the automatic choice from its names and
    /// size, else the Revit element itself.
    /// </summary>
    public static class EntourageSwaps
    {
        /// <summary>Rule scopes.</summary>
        public const string INSTANCE = "instance", TYPE = "type";

        /// <summary>Show the Revit element itself / show nothing.</summary>
        public const string KEEP = "keep", NONE = "none";

        /// <summary>Most rules kept.</summary>
        public const int MAX_SWAPS = 50000;

        /// <summary>What a slot shows.</summary>
        /// <param name="Type">A billboard type key, <see cref="KEEP"/> or <see cref="NONE"/>.</param>
        /// <param name="Height">The billboard's height (m).</param>
        /// <param name="Flip">Mirrored.</param>
        /// <param name="Rule">Which rule decided: "instance", "type" or "auto" (or "off").</param>
        public readonly record struct Result(string Type, float Height, bool Flip, string Rule)
        {
            /// <summary>True when a billboard shows (the Revit element is then hidden).</summary>
            public bool IsBillboard => Type != KEEP && Type != NONE;

            /// <summary>True when the Revit element is hidden (a billboard or nothing).</summary>
            public bool HidesElement => Type != KEEP;
        }

        /// <summary>The rules by key: instance rules and type rules.</summary>
        public static void Index(EntourageDocument document, out Dictionary<string, EntourageSwap> instances, out Dictionary<string, EntourageSwap> types)
        {
            instances = new Dictionary<string, EntourageSwap>(StringComparer.Ordinal);
            types = new Dictionary<string, EntourageSwap>(StringComparer.Ordinal);
            if (document?.Swaps == null) { return; }
            foreach (EntourageSwap swap in document.Swaps)
            {
                if (swap == null) { continue; }
                (swap.Scope == INSTANCE ? instances : types)[swap.Key] = swap;
            }
        }

        /// <summary>True for a type a rule may name: a built-in, an imported one in the document, keep or none.</summary>
        public static bool IsKnownType(EntourageDocument document, string type) =>
            type == KEEP || type == NONE || EntourageArt.Find(type) != null || document?.FindType(type) != null;

        /// <summary>Resolves one slot.</summary>
        public static Result Resolve(EntourageDocument document, EntourageSlot slot, Dictionary<string, EntourageSwap> instances, Dictionary<string, EntourageSwap> types)
        {
            instances.TryGetValue(slot.InstanceKey, out EntourageSwap instance);
            types.TryGetValue(slot.FamilyType, out EntourageSwap type);
            string chosen = null, rule = null;
            if (instance != null && IsKnownType(document, instance.Type)) { chosen = instance.Type; rule = INSTANCE; }
            else if (type != null && IsKnownType(document, type.Type)) { chosen = type.Type; rule = TYPE; }
            else if (document == null || document.SwapRevit) { chosen = EntourageSlots.AutoType(slot); rule = "auto"; }
            else { chosen = KEEP; rule = "off"; }

            float height = instance?.Height ?? (slot.Height >= 0.2f ? slot.Height : DefaultHeight(document, chosen));
            bool flip = instance?.Flip ?? ((StableHash(slot.InstanceKey) & 1) == 1);
            return new Result(chosen, Math.Clamp(height, EntourageItem.MIN_HEIGHT, EntourageItem.MAX_HEIGHT), flip, rule);
        }

        /// <summary>A type's planting height (built-in or imported; 1.7 m otherwise).</summary>
        public static float DefaultHeight(EntourageDocument document, string type)
        {
            EntourageBuiltIn? builtIn = EntourageArt.Find(type);
            if (builtIn != null) { return builtIn.Value.Height; }
            return document?.FindType(type)?.Height ?? 1.7f;
        }

        /// <summary>FNV-1a of a string (the same on every machine and run, unlike string.GetHashCode).</summary>
        public static uint StableHash(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text ?? string.Empty)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }
    }

    /// <summary>
    /// Round X-B: a model's entourage (<c>entourage.json</c>): the placed billboards, the user's own picture types and
    /// how they look (silhouettes in a colour per category, or their own colours). Inside a .bimgo with the pictures
    /// under <c>entourage/</c>; in a live session in the model's BimGo folder (<see cref="EntourageFiles"/>).
    /// </summary>
    public sealed class EntourageDocument
    {
        /// <summary>Prefix of an imported type's key.</summary>
        public const string IMPORTED_PREFIX = "img:";

        /// <summary>Most items / imported types kept (a damaged file stays bounded).</summary>
        public const int MAX_ITEMS = 20000, MAX_TYPES = 200;

        private static readonly Regex SAFE_ID = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly Regex SAFE_IMAGE = new("^[A-Za-z0-9_-]{1,64}\\.png$", RegexOptions.CultureInvariant);

        public int Version { get; set; } = 1;

        /// <summary>The model's title when saved (readability).</summary>
        public string Model { get; set; }

        /// <summary>True: pictures in their own colours; false (default): silhouettes in the category colours.</summary>
        public bool Textured { get; set; }

        /// <summary>Category → colour ("#RRGGBB"); missing ones use <see cref="EntourageCategories.DEFAULT_COLOURS"/>.</summary>
        public Dictionary<string, string> Colours { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The user's picture types.</summary>
        public List<EntourageType> Types { get; set; } = new();

        /// <summary>The placed billboards.</summary>
        public List<EntourageItem> Items { get; set; } = new();

        /// <summary>Round X-B2: Revit Planting / Entourage without a rule are swapped for billboards automatically (default on).</summary>
        public bool SwapRevit { get; set; } = true;

        /// <summary>Round X-B2: swap rules for Revit Planting / Entourage (instances and types).</summary>
        public List<EntourageSwap> Swaps { get; set; } = new();

        /// <summary>True when there is nothing worth saving (no items, no types, the default look).</summary>
        [JsonIgnore]
        public bool IsEmpty => (Items == null || Items.Count == 0) && (Types == null || Types.Count == 0) && !Textured && (Colours == null || Colours.Count == 0)
            && SwapRevit && (Swaps == null || Swaps.Count == 0);

        /// <summary>True for an id that is safe in a file name.</summary>
        public static bool IsSafeId(string id) => id != null && SAFE_ID.IsMatch(id);

        /// <summary>True for a picture file name this format writes (<c>&lt;id&gt;.png</c>).</summary>
        public static bool IsSafeImageName(string name) => name != null && SAFE_IMAGE.IsMatch(name);

        /// <summary>A category's colour (0xRRGGBB).</summary>
        public uint ColourOf(string category)
        {
            string key = EntourageCategories.Normalise(category);
            if (Colours != null && Colours.TryGetValue(key, out string hex) && TryParseColour(hex, out uint rgb)) { return rgb; }
            return EntourageCategories.DEFAULT_COLOURS[EntourageCategories.IndexOf(key)];
        }

        /// <summary>Sets a category's colour (the default colour clears the entry).</summary>
        public void SetColour(string category, uint rgb)
        {
            string key = EntourageCategories.Normalise(category);
            Colours ??= new Dictionary<string, string>(StringComparer.Ordinal);
            rgb &= 0xFFFFFF;
            if (rgb == EntourageCategories.DEFAULT_COLOURS[EntourageCategories.IndexOf(key)]) { Colours.Remove(key); }
            else { Colours[key] = "#" + rgb.ToString("X6", CultureInfo.InvariantCulture); }
        }

        /// <summary>The imported type with a key (<c>img:&lt;id&gt;</c>), or null.</summary>
        public EntourageType FindType(string key)
        {
            if (key == null || !key.StartsWith(IMPORTED_PREFIX, StringComparison.Ordinal) || Types == null) { return null; }
            string id = key[IMPORTED_PREFIX.Length..];
            return Types.Find(t => t.Id == id);
        }

        /// <summary>
        /// Drops what can't be used (items of unknown types, bad numbers, unsafe names, duplicate ids), clamps the rest
        /// and bounds the counts. Returns itself.
        /// </summary>
        public EntourageDocument Clean()
        {
            Version = 1;
            Types ??= new List<EntourageType>();
            Items ??= new List<EntourageItem>();
            var colours = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Colours != null)
            {
                foreach (KeyValuePair<string, string> pair in Colours)
                {
                    string key = EntourageCategories.Normalise(pair.Key);
                    if (TryParseColour(pair.Value, out uint rgb)) { colours[key] = "#" + rgb.ToString("X6", CultureInfo.InvariantCulture); }
                }
            }
            Colours = colours;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            Types = Types.Where(t => t != null && t.Clean() && ids.Add(t.Id)).Take(MAX_TYPES).ToList();
            var known = new HashSet<string>(Types.Select(t => t.Key), StringComparer.Ordinal);
            foreach (EntourageBuiltIn b in EntourageArt.BUILT_INS) { known.Add(b.Key); }
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            Items = Items.Where(i => i != null && i.Clean() && known.Contains(i.Type) && itemIds.Add(i.Id)).Take(MAX_ITEMS).ToList();

            // X-B2: rules with a scope, a key and a known type; the last one for a key wins
            var swaps = new Dictionary<(string, string), EntourageSwap>();
            foreach (EntourageSwap swap in Swaps ?? new List<EntourageSwap>())
            {
                if (swap == null || string.IsNullOrWhiteSpace(swap.Key) || (swap.Scope != EntourageSwaps.INSTANCE && swap.Scope != EntourageSwaps.TYPE)) { continue; }
                if (swap.Type != EntourageSwaps.KEEP && swap.Type != EntourageSwaps.NONE && !known.Contains(swap.Type ?? string.Empty)) { continue; }
                if (swap.Scope == EntourageSwaps.TYPE) { swap.Height = null; swap.Flip = null; }
                if (swap.Height is float h) { swap.Height = float.IsFinite(h) ? Math.Clamp(h, EntourageItem.MIN_HEIGHT, EntourageItem.MAX_HEIGHT) : null; }
                swaps.Remove((swap.Scope, swap.Key));
                swaps[(swap.Scope, swap.Key)] = swap;
            }
            Swaps = swaps.Values.Take(EntourageSwaps.MAX_SWAPS).ToList();
            return this;
        }

        /// <summary>Reads "#RRGGBB" / "RRGGBB".</summary>
        public static bool TryParseColour(string text, out uint rgb)
        {
            rgb = 0;
            string t = (text ?? string.Empty).Trim().TrimStart('#');
            return t.Length == 6 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
        }
    }

    /// <summary>
    /// Round X-B: where a live session keeps its entourage: <c>entourage.json</c> in the model's BimGo folder (or
    /// &lt;model&gt;.bimgo-entourage.json beside an older snapshot's comments), with the pictures in the folder of the
    /// same name plus <c>-images</c>. Never throws.
    /// </summary>
    public static class EntourageFiles
    {
        /// <summary>The entourage file that goes with a comments sidecar, or null.</summary>
        public static string SidecarFor(string commentsSidecarPath) => SidecarJson.BesideComments(commentsSidecarPath, BimGoFormat.ENTOURAGE_SIDECAR_SUFFIX);

        /// <summary>The pictures' folder for an entourage file.</summary>
        public static string ImagesFolderFor(string entouragePath) =>
            string.IsNullOrEmpty(entouragePath) ? null
            : Path.Combine(Path.GetDirectoryName(entouragePath) ?? string.Empty, Path.GetFileNameWithoutExtension(entouragePath) + "-images");

        /// <summary>Reads the file (null if missing or unreadable).</summary>
        public static EntourageDocument Read(string path, out string error) => SidecarJson.Read<EntourageDocument>(path, "Entourage", out error)?.Clean();

        /// <summary>Writes the file atomically.</summary>
        public static bool Write(string path, EntourageDocument document, out string error) => SidecarJson.Write(path, document, "Entourage", out error);

        /// <summary>
        /// Reads the pictures the document's types name from the folder (missing ones are left out).
        /// </summary>
        public static Dictionary<string, byte[]> ReadImages(string folder, EntourageDocument document)
        {
            var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(folder) || document?.Types == null) { return images; }
            foreach (EntourageType type in document.Types)
            {
                if (!EntourageDocument.IsSafeImageName(type.Image)) { continue; }
                try
                {
                    string path = Path.Combine(folder, type.Image);
                    if (File.Exists(path) && new FileInfo(path).Length <= BimGoFormat.MAX_ENTOURAGE_IMAGE_BYTES) { images[type.Image] = File.ReadAllBytes(path); }
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Entourage picture {type.Image} not read: {ex.Message}");
                }
            }
            return images;
        }

        /// <summary>Writes one picture into the folder (atomically).</summary>
        public static bool WriteImage(string folder, string name, byte[] bytes, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(folder) || !EntourageDocument.IsSafeImageName(name) || bytes == null)
            {
                error = "The picture has no place to go.";
                return false;
            }
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name), temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = $"The picture could not be saved: {ex.Message}";
                Utilities.Log_Utils.Write(error);
                return false;
            }
        }

        /// <summary>Deletes one picture from the folder (quietly).</summary>
        public static void DeleteImage(string folder, string name)
        {
            if (string.IsNullOrEmpty(folder) || !EntourageDocument.IsSafeImageName(name)) { return; }
            try
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Entourage picture {name} not deleted: {ex.Message}");
            }
        }
    }
}
