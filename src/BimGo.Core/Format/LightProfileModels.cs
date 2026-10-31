using System.Text.Json.Serialization;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Round X-D1: the model's light profile choices (<c>light-profiles.json</c> in a .bimgo or the live model folder):
    /// per fixture type ("Family : Type") which distribution its lights use, and the .ies files imported in the app.
    /// A type without a choice uses the file its Revit family names, else BimGo's own lobe. Round Y-C2: single fixtures
    /// can have a choice of their own (<see cref="Instances"/>), which wins over their type's.
    /// </summary>
    public sealed class LightProfileDocument
    {
        /// <summary>Most imported files kept.</summary>
        public const int MAX_IMPORTS = 32;

        /// <summary>Round Y-C2: most per-fixture choices kept.</summary>
        public const int MAX_INSTANCES = 20000;

        /// <summary>Layout version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>The model title when last saved (information only).</summary>
        public string Model { get; set; }

        /// <summary>The per-type choices.</summary>
        public List<LightProfileChoice> Types { get; set; } = new();

        /// <summary>The imported files (text kept here: .ies files are a few KB).</summary>
        public List<LightProfileImport> Imports { get; set; } = new();

        /// <summary>
        /// Round Y-C2: the per-fixture choices, keyed by <see cref="InstanceKeyOf"/> (stable across Go / F5 / Export).
        /// Kept for fixtures that aren't in this snapshot (a hidden link or phase may bring them back).
        /// </summary>
        public List<LightProfileInstanceChoice> Instances { get; set; } = new();

        /// <summary>Which model's fixtures light the scene (<see cref="FixtureSources"/>; null = <see cref="FixtureSources.AUTO"/>).</summary>
        public string FixtureSource { get; set; }

        /// <summary>True when there is nothing to save.</summary>
        [JsonIgnore]
        public bool IsEmpty => (Types == null || Types.Count == 0) && (Imports == null || Imports.Count == 0) && (Instances == null || Instances.Count == 0)
            && FixtureSource == null;

        // Round Y-C2: Instances by key (built on first look-up, dropped on every change)
        private Dictionary<string, LightProfileInstanceChoice> _instanceIndex;

        /// <summary>
        /// The choice for a type: <see cref="LightProfiles.REVIT"/>, <see cref="LightProfiles.LOBE"/>, a built-in key or
        /// an <c>ies:</c> key; null when none was made.
        /// </summary>
        public string ChoiceFor(string familyType)
        {
            foreach (LightProfileChoice choice in Types)
            {
                if (string.Equals(choice.FamilyType, familyType, StringComparison.Ordinal)) { return choice.Profile; }
            }
            return null;
        }

        /// <summary>Sets (or with null clears) a type's choice.</summary>
        public void SetChoice(string familyType, string profile)
        {
            Types.RemoveAll(c => string.Equals(c.FamilyType, familyType, StringComparison.Ordinal));
            if (profile != null) { Types.Add(new LightProfileChoice { FamilyType = familyType, Profile = profile }); }
        }

        #region Per fixture (Round Y-C2)

        /// <summary>
        /// A fixture's key: its UniqueId (or "id:" + ElementId), after its link instance's UniqueId and a "/" when it comes
        /// from a link (the pattern of <see cref="EntourageSlot.InstanceKey"/>); null for an element without an
        /// identity (a family library template).
        /// </summary>
        public static string InstanceKeyOf(SceneData scene, int element)
        {
            if (scene?.Elements == null || element < 0 || element >= scene.Elements.Length) { return null; }
            ElementRecord e = scene.Elements[element];
            if (e.IsLibraryTemplate) { return null; }
            string id = !string.IsNullOrEmpty(e.UniqueId) ? e.UniqueId : e.ElementId > 0 ? "id:" + e.ElementId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            if (id == null) { return null; }
            if (!e.IsLinked) { return id; }
            LinkInfo link = scene.LinkOf(e);
            string linkKey = link != null && !string.IsNullOrEmpty(link.InstanceUniqueId) ? link.InstanceUniqueId : "link:" + e.Link.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return linkKey + "/" + id;
        }

        /// <summary>A fixture's own choice (same values as a type's), or null when it follows its type.</summary>
        public string InstanceChoiceFor(string instanceKey)
        {
            if (instanceKey == null || Instances == null || Instances.Count == 0) { return null; }
            if (_instanceIndex == null)
            {
                _instanceIndex = new Dictionary<string, LightProfileInstanceChoice>(StringComparer.Ordinal);
                foreach (LightProfileInstanceChoice c in Instances) { if (c?.Key != null) { _instanceIndex[c.Key] = c; } }
            }
            return _instanceIndex.TryGetValue(instanceKey, out LightProfileInstanceChoice choice) ? choice.Profile : null;
        }

        /// <summary>Sets (or with null clears) a fixture's own choice; <paramref name="familyType"/> is kept for RESET ALL.</summary>
        public void SetInstanceChoice(string instanceKey, string familyType, string profile)
        {
            if (instanceKey == null) { return; }
            Instances ??= new List<LightProfileInstanceChoice>();
            Instances.RemoveAll(c => c == null || string.Equals(c.Key, instanceKey, StringComparison.Ordinal));
            if (profile != null) { Instances.Add(new LightProfileInstanceChoice { Key = instanceKey, FamilyType = familyType ?? string.Empty, Profile = profile }); }
            _instanceIndex = null;
        }

        /// <summary>Clears every fixture choice of a type (RESET ALL); returns how many went.</summary>
        public int ClearInstances(string familyType)
        {
            if (Instances == null) { return 0; }
            int removed = Instances.RemoveAll(c => c == null || string.Equals(c.FamilyType, familyType, StringComparison.Ordinal));
            _instanceIndex = null;
            return removed;
        }

        /// <summary>
        /// The choice that decides a fixture: its own, else its type's, else null (the default: Revit's, else the lobe).
        /// </summary>
        public string ResolveChoice(string instanceKey, string familyType) => InstanceChoiceFor(instanceKey) ?? ChoiceFor(familyType ?? string.Empty);

        /// <summary>
        /// The profile key a choice means: no choice or <see cref="LightProfiles.REVIT"/> → <paramref name="revitKey"/>
        /// (the fixture's Revit file or distribution, null when it has none), <see cref="LightProfiles.LOBE"/> → null
        /// (the lobe), else the choice itself.
        /// </summary>
        public static string KeyFor(string choice, string revitKey) =>
            choice == null || choice == LightProfiles.REVIT ? revitKey : choice == LightProfiles.LOBE ? null : choice;

        #endregion

        /// <summary>The imported file with a hash, or null.</summary>
        public LightProfileImport FindImport(string hash)
        {
            foreach (LightProfileImport import in Imports) { if (import.Hash == hash) { return import; } }
            return null;
        }

        /// <summary>
        /// Drops damaged parts: choices without a type or a known kind of value, imports without a valid hash and text
        /// (or past <see cref="MAX_IMPORTS"/>), duplicates (the last choice per type wins).
        /// </summary>
        public LightProfileDocument Clean()
        {
            Types ??= new List<LightProfileChoice>();
            Imports ??= new List<LightProfileImport>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var types = new List<LightProfileChoice>();
            for (int i = Types.Count - 1; i >= 0; i--)
            {
                LightProfileChoice c = Types[i];
                if (c == null || string.IsNullOrWhiteSpace(c.FamilyType) || !IsKnownValue(c.Profile) || !seen.Add(c.FamilyType)) { continue; }
                types.Insert(0, c);
            }
            Types = types;
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            Imports.RemoveAll(i => i == null || !LightProfiles.IsSafeHash(i.Hash) || string.IsNullOrEmpty(i.Text)
                || i.Text.Length > LightProfile.MAX_IES_BYTES || !hashes.Add(i.Hash));
            if (Imports.Count > MAX_IMPORTS) { Imports.RemoveRange(MAX_IMPORTS, Imports.Count - MAX_IMPORTS); }

            // Round Y-C2: fixture choices (the last per key wins; unknown ids are kept, see Instances)
            Instances ??= new List<LightProfileInstanceChoice>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var instances = new List<LightProfileInstanceChoice>();
            for (int i = Instances.Count - 1; i >= 0; i--)
            {
                LightProfileInstanceChoice c = Instances[i];
                if (c == null || string.IsNullOrWhiteSpace(c.Key) || c.Key.Length > 256 || !IsKnownValue(c.Profile) || !keys.Add(c.Key)) { continue; }
                c.FamilyType ??= string.Empty;
                instances.Insert(0, c);
            }
            if (instances.Count > MAX_INSTANCES) { instances.RemoveRange(0, instances.Count - MAX_INSTANCES); }
            Instances = instances;
            _instanceIndex = null;
            FixtureSource = FixtureSources.Clean(FixtureSource) == FixtureSources.AUTO ? null : FixtureSource;
            return this;
        }

        private static bool IsKnownValue(string value) =>
            value == LightProfiles.REVIT || value == LightProfiles.LOBE
            || (value != null && value.StartsWith(LightProfiles.BUILT_IN_PREFIX, StringComparison.Ordinal) && value.Length < 64)
            || LightProfiles.HashOf(value) != null;
    }

    /// <summary>
    /// Round X-D1: one fixture type's choice.
    /// </summary>
    public sealed class LightProfileChoice
    {
        /// <summary>"Family : Type" as the elements record it.</summary>
        public string FamilyType { get; set; }

        /// <summary><c>revit</c>, <c>lobe</c>, <c>builtin:…</c> or <c>ies:&lt;hash&gt;</c>.</summary>
        public string Profile { get; set; }
    }

    /// <summary>
    /// Round Y-C2: one fixture's own choice.
    /// </summary>
    public sealed class LightProfileInstanceChoice
    {
        /// <summary>The fixture (<see cref="LightProfileDocument.InstanceKeyOf"/>).</summary>
        public string Key { get; set; }

        /// <summary>Its "Family : Type" when set (RESET ALL clears a type's, including fixtures not in this snapshot).</summary>
        public string FamilyType { get; set; }

        /// <summary>As <see cref="LightProfileChoice.Profile"/>.</summary>
        public string Profile { get; set; }
    }

    /// <summary>
    /// Round X-D1: an .ies file imported in the app.
    /// </summary>
    public sealed class LightProfileImport
    {
        /// <summary>16 hex characters (<see cref="LightProfiles.KeyOf"/> without its prefix).</summary>
        public string Hash { get; set; }

        /// <summary>The file name it was imported from.</summary>
        public string Name { get; set; }

        /// <summary>Its text.</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// Round X-D1: where a live session keeps its light profile choices: <c>light-profiles.json</c> in the model's BimGo
    /// folder (or &lt;model&gt;.bimgo-light-profiles.json beside an older snapshot's comments). Never throws.
    /// </summary>
    public static class LightProfileFiles
    {
        /// <summary>The file that goes with a comments sidecar, or null.</summary>
        public static string SidecarFor(string commentsSidecarPath) => SidecarJson.BesideComments(commentsSidecarPath, BimGoFormat.LIGHT_PROFILES_SIDECAR_SUFFIX);

        /// <summary>Reads the file (null if missing or unreadable).</summary>
        public static LightProfileDocument Read(string path, out string error) => SidecarJson.Read<LightProfileDocument>(path, "Light profiles", out error)?.Clean();

        /// <summary>Writes the file atomically.</summary>
        public static bool Write(string path, LightProfileDocument document, out string error) => SidecarJson.Write(path, document, "Light profiles", out error);
    }
}
