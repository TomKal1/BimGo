using System.Numerics;
using BimGo.Scene;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Round X-B2: records every Planting and Entourage instance in the extraction's scope (the host and the ticked
    /// links; what the active view shows when extracting the active view only), whether or not their categories'
    /// geometry is extracted, so the walkthrough can swap them for billboards: UniqueId, family and type, base point,
    /// size, and the extracted element when there is one.
    /// </summary>
    internal sealed partial class SceneExtractor
    {
        private static readonly (BuiltInCategory Category, string Key)[] ENTOURAGE_CATEGORIES =
        {
            (BuiltInCategory.OST_Planting, EntourageSlots.PLANTING),
            (BuiltInCategory.OST_Entourage, EntourageSlots.ENTOURAGE)
        };

        /// <summary>The Planting / Entourage instances (after the geometry, so extracted ones are linked to their element).</summary>
        private EntourageSlot[] CollectEntourageSlots(List<SourceModel> sources, DB.View view)
        {
            var slots = new List<EntourageSlot>();
            var extracted = new Dictionary<(int Link, long Id), int>();
            for (int i = 0; i < _elements.Count; i++) { extracted.TryAdd((_elements[i].Link, _elements[i].ElementId), i); }

            foreach (SourceModel source in sources)
            {
                foreach ((BuiltInCategory category, string key) in ENTOURAGE_CATEGORIES)
                {
                    IList<Element> elements;
                    try
                    {
                        FilteredElementCollector collector = view == null
                            ? new FilteredElementCollector(source.Doc)
                            : source.Link == 0 ? ViewScope.CollectHost(_doc, view) : ViewScope.CollectLink(_doc, view, source.LinkInstanceId);
                        if (collector == null) { continue; }
                        elements = collector.OfCategory(category).WhereElementIsNotElementType().ToElements();
                    }
                    catch (Exception ex)
                    {
                        Utilities.Log_Utils.Write($"{source.Describe()}: {key} instances not listed: {ex.Message}");
                        continue;
                    }

                    foreach (Element element in elements)
                    {
                        if (slots.Count >= EntourageSlots.MAX_SLOTS) { break; }
                        try
                        {
                            EntourageSlot slot = SlotOf(element, source, key, extracted);
                            if (slot != null) { slots.Add(slot); }
                        }
                        catch (Exception ex)
                        {
                            Utilities.Log_Utils.Write($"{source.Describe()}: {key} {element.Id.Value} not recorded: {ex.Message}");
                        }
                    }
                }
            }
            if (slots.Count > 0)
            {
                Utilities.Log_Utils.Write($"Entourage: {slots.Count} Planting / Entourage instance(s) recorded ({slots.Count(s => s.Element >= 0)} with geometry).");
            }
            return slots.ToArray();
        }

        /// <summary>One instance's slot (null when it isn't a placed family instance standing in the new phase).</summary>
        private EntourageSlot SlotOf(Element element, SourceModel source, string key, Dictionary<(int Link, long Id), int> extracted)
        {
            if (element is not FamilyInstance instance || !IsExtractable(element, source.Phases)) { return null; }
            BoundingBoxXYZ box = element.get_BoundingBox(null);
            if (box == null) { return null; }

            // The box's corners in host coordinates (a link's own transform first)
            Transform transform = source.Transform;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int c = 0; c < 8; c++)
            {
                var corner = new XYZ((c & 1) == 0 ? box.Min.X : box.Max.X, (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z);
                if (box.Transform != null && !box.Transform.IsIdentity) { corner = box.Transform.OfPoint(corner); }
                if (transform != null) { corner = transform.OfPoint(corner); }
                minX = Math.Min(minX, corner.X);
                minY = Math.Min(minY, corner.Y);
                minZ = Math.Min(minZ, corner.Z);
                maxX = Math.Max(maxX, corner.X);
                maxY = Math.Max(maxY, corner.Y);
                maxZ = Math.Max(maxZ, corner.Z);
            }

            // The base: its location point when it has one (on the ground it was placed on), else the box's bottom centre
            XYZ basePoint = new((minX + maxX) * 0.5, (minY + maxY) * 0.5, minZ);
            if (instance.Location is LocationPoint location && location.Point != null)
            {
                XYZ p = transform != null ? transform.OfPoint(location.Point) : location.Point;
                basePoint = new XYZ(p.X, p.Y, Math.Min(p.Z, maxZ));
            }

            FamilySymbol symbol = instance.Symbol;
            return new EntourageSlot
            {
                UniqueId = element.UniqueId ?? string.Empty,
                ElementId = element.Id.Value,
                Link = source.Link,
                LinkKey = source.Info?.InstanceUniqueId ?? string.Empty,
                Category = key,
                Family = symbol?.FamilyName ?? string.Empty,
                Type = symbol?.Name ?? string.Empty,
                Base = new Vector3((float)(basePoint.X * FT - _origin.X), (float)(basePoint.Y * FT - _origin.Y), (float)(basePoint.Z * FT - _origin.Z)),
                Height = (float)((maxZ - basePoint.Z) * FT),
                Width = (float)(Math.Max(maxX - minX, maxY - minY) * FT),
                Element = extracted.TryGetValue((source.Link, element.Id.Value), out int index) ? index : -1
            };
        }
    }
}
