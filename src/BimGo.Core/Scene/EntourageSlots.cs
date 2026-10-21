using System.Numerics;
using System.Text.Json.Serialization;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Round X-B2: one Revit Planting or Entourage instance as the extraction found it (always recorded, whether or not
    /// its category's geometry was extracted): who it is, what family and type, where its base stands (scene-local
    /// metres), how big it is, and the extracted element when there is one (hidden when it is swapped for a billboard).
    /// </summary>
    public sealed class EntourageSlot
    {
        /// <summary>Revit's UniqueId of the instance.</summary>
        public string UniqueId { get; set; } = string.Empty;

        /// <summary>Revit's element id.</summary>
        public long ElementId { get; set; }

        /// <summary>0 for the host, else the link number (as <see cref="ElementRecord.Link"/>).</summary>
        public int Link { get; set; }

        /// <summary>The link instance's UniqueId (empty for the host).</summary>
        public string LinkKey { get; set; } = string.Empty;

        /// <summary>"planting" or "entourage" (the Revit category).</summary>
        public string Category { get; set; } = EntourageSlots.PLANTING;

        /// <summary>The family's name.</summary>
        public string Family { get; set; } = string.Empty;

        /// <summary>The type's name.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Where its base stands (scene-local metres).</summary>
        public Vector3 Base { get; set; }

        /// <summary>Height and width (m; width = the larger horizontal size of its box).</summary>
        public float Height { get; set; }

        /// <summary>See <see cref="Height"/>.</summary>
        public float Width { get; set; }

        /// <summary>The extracted element's index, or -1 when its geometry wasn't extracted.</summary>
        public int Element { get; set; } = -1;

        private string _familyType, _instanceKey;

        /// <summary>"Family : Type": the key of a type rule (the same type in every link).</summary>
        [JsonIgnore]
        public string FamilyType => _familyType ??= EntourageSlots.FamilyTypeKey(Family, Type);

        /// <summary>The key of an instance rule: the UniqueId, after the link instance's ("link/element") in a link.</summary>
        [JsonIgnore]
        public string InstanceKey => _instanceKey ??= string.IsNullOrEmpty(LinkKey) ? UniqueId : LinkKey + "/" + UniqueId;

        /// <summary>False when the slot can't be used (no id, bad numbers).</summary>
        public bool Clean(int elementCount)
        {
            if (string.IsNullOrWhiteSpace(UniqueId)) { return false; }
            if (!float.IsFinite(Base.X) || !float.IsFinite(Base.Y) || !float.IsFinite(Base.Z) || !float.IsFinite(Height) || !float.IsFinite(Width)) { return false; }
            Category = Category == EntourageSlots.ENTOURAGE ? EntourageSlots.ENTOURAGE : EntourageSlots.PLANTING;
            Family ??= string.Empty;
            Type ??= string.Empty;
            LinkKey ??= string.Empty;
            Height = Math.Clamp(Height, 0f, 200f);
            Width = Math.Clamp(Width, 0f, 200f);
            if (Element < -1 || Element >= elementCount) { Element = -1; }
            _familyType = _instanceKey = null;
            return true;
        }
    }

    /// <summary>Round X-B2: <c>entourage-slots.json</c> in a .bimgo (the snapshot's Planting / Entourage instances).</summary>
    public sealed class EntourageSlotsDto
    {
        public int Version { get; set; } = 1;

        /// <summary>The instances.</summary>
        public List<EntourageSlot> Slots { get; set; } = new();
    }

    /// <summary>
    /// Round X-B2: Revit Planting / Entourage instances: the file entry, and which billboard fits one when no rule says
    /// (its family and type names first: "palm", "hedge", "child", "SUV"…, then its size).
    /// </summary>
    public static class EntourageSlots
    {
        /// <summary>The Revit categories recorded.</summary>
        public const string PLANTING = "planting", ENTOURAGE = "entourage";

        /// <summary>Most slots kept.</summary>
        public const int MAX_SLOTS = 50000;

        /// <summary>"Family : Type".</summary>
        public static string FamilyTypeKey(string family, string type) => $"{family ?? string.Empty} : {type ?? string.Empty}";

        /// <summary>The entry for a scene's slots, or null when there are none.</summary>
        public static EntourageSlotsDto ToDto(IReadOnlyList<EntourageSlot> slots) =>
            slots == null || slots.Count == 0 ? null : new EntourageSlotsDto { Slots = slots.ToList() };

        /// <summary>The slots of an entry (bad ones dropped; element indices checked against the scene).</summary>
        public static EntourageSlot[] FromDto(EntourageSlotsDto dto, int elementCount)
        {
            if (dto?.Slots == null) { return Array.Empty<EntourageSlot>(); }
            return dto.Slots.Where(s => s != null && s.Clean(elementCount)).Take(MAX_SLOTS).ToArray();
        }

        /// <summary>
        /// The built-in billboard that fits an instance: its names decide when they say ("palm", "conifer", "hedge",
        /// "grass", "shrub", "child", "woman", "SUV", "car"…), else its size (planting: tall → a tree, narrow → columnar,
        /// wide → spreading; low → a shrub, long and low → a hedge; entourage: car-sized → a car or SUV, person-sized →
        /// a person, a child under 1.4 m).
        /// </summary>
        public static string AutoType(EntourageSlot slot)
        {
            string names = ((slot?.Family ?? string.Empty) + " " + (slot?.Type ?? string.Empty)).ToLowerInvariant();
            static bool Has(string text, params string[] words)
            {
                foreach (string word in words) { if (text.Contains(word, StringComparison.Ordinal)) { return true; } }
                return false;
            }
            float h = slot?.Height ?? 0f, w = slot?.Width ?? 0f;
            float ratio = h > 0.01f ? w / h : 1f;
            if (slot?.Category == ENTOURAGE)
            {
                if (Has(names, "suv", "van", "truck", "ute", "4wd", "bus")) { return "car-suv"; }
                if (Has(names, "car", "sedan", "vehicle", "hatch", "coupe")) { return "car-sedan"; }
                if (Has(names, "child", "kid", "boy", "girl")) { return "person-child"; }
                if (Has(names, "woman", "female", "lady")) { return "person-woman"; }
                if (Has(names, "phone")) { return "person-phone"; }
                if (Has(names, "man", "male", "person", "people", "figure", "human")) { return "person-man"; }
                if (Has(names, "tree", "palm")) { return names.Contains("palm", StringComparison.Ordinal) ? "tree-palm" : "tree-round"; }
                if (w >= 2.4f && h < 2.6f) { return h > 1.65f ? "car-suv" : "car-sedan"; }
                if (h > 0.5f && h < 1.4f && w < 1.2f) { return "person-child"; }
                return "person-man";
            }
            if (Has(names, "palm")) { return "tree-palm"; }
            if (Has(names, "conifer", "pine", "spruce", "fir", "cedar", "cypress", "araucaria")) { return "tree-conifer"; }
            if (Has(names, "hedge")) { return "shrub-hedge"; }
            if (Has(names, "grass", "lomandra", "sedge", "tussock")) { return "shrub-grass"; }
            if (Has(names, "poplar", "column", "fastigiate", "upright")) { return "tree-tall"; }
            if (Has(names, "shrub", "bush", "groundcover")) { return ratio > 2.2f && h < 2f ? "shrub-hedge" : "shrub-round"; }
            if (h > 0f && h < 2f) { return ratio > 2.2f ? "shrub-hedge" : "shrub-round"; }
            if (ratio < 0.5f) { return "tree-tall"; }
            if (ratio > 1.15f) { return "tree-spreading"; }
            return "tree-round";
        }
    }
}
