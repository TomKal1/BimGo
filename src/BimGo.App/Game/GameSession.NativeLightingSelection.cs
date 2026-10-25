using System.Numerics;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

namespace BimGo.Game;

internal sealed partial class GameSession
{
    private int _nativePickMode;
    private string _nativeFixtureKey;
    private const int DROP_NATIVE_FIXTURES = 1202, DROP_NATIVE_CATEGORIES = 1203, DROP_NATIVE_ELEMENTS = 1204;

    private float BuildNativeCalculationSelection(FontAtlas f, InputState input, float x, float y, float w)
    {
        var set = NativeSet;
        y = NativeHeading(f, x, y, w, "FIXTURES & EXCLUSIONS");
        bool emergency = Checkbox(f, input, x, y, w, "Emergency fixtures only", set.EmergencyOnly); y += S(32);
        if (emergency != set.EmergencyOnly) { set.EmergencyOnly = emergency; NativeSetupChanged(); }
        var fixtures = new List<(string Key, string Label)>();
        foreach (int element in (Scene.Lighting?.Lights ?? Array.Empty<LightSource>()).Select(l => l.Element).Distinct())
        {
            string key = LightProfileDocument.InstanceKeyOf(Scene, element) ?? "element:" + element;
            string label = Scene.Elements[element].FamilyType + " · " + Scene.Elements[element].ElementId;
            fixtures.Add((key, label));
            foreach (var instance in Dynamics?.Instances.Where(d => d.Element == element && d.IsClone && !d.Hidden) ?? Enumerable.Empty<BimGo.Physics.DynamicInstance>())
                fixtures.Add((key + "/instance:" + instance.CloneKey, label + " (clone " + instance.CloneKey + ")"));
        }
        int selected = fixtures.FindIndex(fixture => fixture.Key == _nativeFixtureKey);
        if (selected < 0 && fixtures.Count > 0) { selected = 0; _nativeFixtureKey = fixtures[0].Key; }
        if (DropdownButton(f, input, x, y, w, S(34), selected < 0 ? "NO EXPORTED FIXTURES" : fixtures[selected].Label, IsDropdownOpen(set, DROP_NATIVE_FIXTURES), f.Body))
            OpenDropdown(set, DROP_NATIVE_FIXTURES, x, y, w, S(34), fixtures.Select(v => v.Label).ToArray(), selected,
                i => _nativeFixtureKey = fixtures[i].Key);
        y += S(42);
        if (selected >= 0)
        {
            var setting = set.Luminaires.FirstOrDefault(l => l.Key == _nativeFixtureKey);
            // Do not create a setting during rendering: only user edits change the physical revision.
            NativeLuminaireSetting Edit()
            {
                var value = set.Luminaires.FirstOrDefault(l => l.Key == _nativeFixtureKey);
                if (value == null) { value = new NativeLuminaireSetting { Key = _nativeFixtureKey }; set.Luminaires = set.Luminaires.Append(value).ToArray(); }
                return value;
            }
            bool participates = Checkbox(f, input, x, y, w, "Selected fixture is emergency", setting?.Emergency == true); y += S(32);
            if (participates != (setting?.Emergency == true)) { Edit().Emergency = participates; NativeSetupChanged(); }
            y = NativeNumberRow(f, input, x, y, w, "Emergency output (%)", (setting?.EmergencyOutput ?? 1) * 100, 0, 100, v => Edit().EmergencyOutput = v / 100);
            bool previewPosition = Checkbox(f, input, x, y, w, "Use preview lens position", setting?.UsePreviewLensPosition == true); y += S(32);
            if (previewPosition != (setting?.UsePreviewLensPosition == true)) { Edit().UsePreviewLensPosition = previewPosition; NativeSetupChanged(); }
            y = NativeNumberRow(f, input, x, y, w, "Source height offset", setting?.HeightOffset ?? 0, -100, 100, v => Edit().HeightOffset = v, length: true);
        }
        if (NativeButton(f, input, x, y, w, S(34), "Pick element to exclude in ISO"))
        { _nativePickMode = 2; _nativeStatus = "Click a model element in ISO review to exclude it from the calculation only."; }
        y += S(42);
        var categories = CategoryCatalog.All;
        if (DropdownButton(f, input, x, y, w, S(34), $"EXCLUDED CATEGORIES · {set.ExcludedCategories.Length}", IsDropdownOpen(set, DROP_NATIVE_CATEGORIES), f.Body))
            OpenDropdown(set, DROP_NATIVE_CATEGORIES, x, y, w, S(34), categories.Select(c => (set.ExcludedCategories.Contains(c.Key) ? "Excluded · " : "Included · ") + c.Label).ToArray(), -1, i =>
            {
                string key = categories[i].Key;
                set.ExcludedCategories = set.ExcludedCategories.Contains(key) ? set.ExcludedCategories.Where(k => k != key).ToArray() : set.ExcludedCategories.Append(key).ToArray();
                NativeSetupChanged();
            });
        y += S(42);
        if (set.ExcludedElements.Length > 0 && DropdownButton(f, input, x, y, w, S(34), $"RESTORE EXCLUDED ELEMENT · {set.ExcludedElements.Length}", IsDropdownOpen(set, DROP_NATIVE_ELEMENTS), f.Body))
        {
            var keys = set.ExcludedElements.ToArray();
            string Label(string key)
            {
                int element = Array.FindIndex(Scene.Elements, e => (e.UniqueId ?? "") == key);
                if (element < 0) element = Enumerable.Range(0, Scene.Elements.Length).FirstOrDefault(i => key == (LightProfileDocument.InstanceKeyOf(Scene, i) ?? "element:" + i) || key.StartsWith((LightProfileDocument.InstanceKeyOf(Scene, i) ?? "element:" + i) + "/instance:", StringComparison.Ordinal), -1);
                return element < 0 ? "Unavailable element" : Scene.Elements[element].FamilyType + " · " + Scene.Elements[element].ElementId;
            }
            OpenDropdown(set, DROP_NATIVE_ELEMENTS, x, y, w, S(34), keys.Select(Label).ToArray(), -1, i =>
            { set.ExcludedElements = set.ExcludedElements.Where(k => k != keys[i]).ToArray(); NativeSetupChanged(); });
        }
        y += S(42);
        y += _ui.TextWrapped(f.Body, x, y, w, "Calculation exclusions do not hide display geometry. Emergency output is a fraction of normal maintained output; LLF still applies. Source offsets are explicit, in model height.", UiTheme.TEXT_MUTED, maxLines: 5) + S(8);
        return y;
    }

    private bool TryNativeCalculationPick(Vector2 mouse)
    {
        var set = NativeSet;
        if (_nativePickMode == 0 || set == null) return false;
        if (_nativePickMode == 1)
        {
            var result = _nativePreviousRun?.Input.Set.Id == set.Id ? _nativePreviousRun.Display : null;
            if (result == null) { _nativeStatus = "Run this set before excluding points."; return true; }
            float best = S(14) * S(14);
            CalcZoneResult chosen = null; int point = -1;
            foreach (var zone in result.Zones)
            {
                if (_reviewRoom?.Zone != null && zone.Key != _reviewRoom.Zone.Key) continue;
                for (int i = 0; i < zone.Points.Length; i++)
                {
                    Vector3 p = zone.Points[i];
                    if (i >= zone.PointValues.Length || !float.IsFinite(zone.PointValues[i]) || SectionCut.IsCut(_sectionPlanes, _sectionCount, p) || !Camera.WorldToScreen(p, out var screen)) continue;
                    float distance = MathF.Min(Vector2.DistanceSquared(mouse, screen), Vector2.DistanceSquared(mouse, screen - new Vector2(0, S(18))));
                    if (distance >= best) continue;
                    Vector3 eye = Camera.IsOrthographic ? p - Camera.Forward * Vector3.Dot(p - Camera.Position, Camera.Forward) : Camera.Position;
                    Vector3 ray = p - eye; float length = ray.Length();
                    if (length < .08f || Pick(eye, ray / length, length - .08f, out _)) continue;
                    if (distance < best) { best = distance; chosen = zone; point = i; }
                }
            }
            if (point < 0) { _nativeStatus = "Click within 14 pixels of a calculation point."; return true; }
            Vector3 chosenPosition = chosen.Points[point];
            double[] raw = { (chosenPosition.X + (double)Scene.OriginOffset.X) / .3048, (chosenPosition.Y + (double)Scene.OriginOffset.Y) / .3048, (chosenPosition.Z + (double)Scene.OriginOffset.Z) / .3048 };
            if (!set.ExcludedPoints.Any(e => e.ZoneKey == chosen.Key && e.Position.Zip(raw, (a, b) => Math.Abs(a - b)).Max() < .001))
                set.ExcludedPoints = set.ExcludedPoints.Append(new NativeExcludedPoint { ZoneKey = chosen.Key, Position = raw }).ToArray();
            NativeSetupChanged();
            _nativeStatus = "Point excluded from this set; recalculate to update its statistics.";
            return true;
        }
        ScreenRay(mouse, out var origin, out var direction);
        if (!Pick(origin, direction, FpsCamera.FAR, out var hit) || hit.Element < 0) return true;
        string key = LightProfileDocument.InstanceKeyOf(Scene, hit.Element) ?? "element:" + hit.Element;
        if (hit.DynamicId != 0)
        {
            var instance = Dynamics?.Instances.FirstOrDefault(d => d.Id == hit.DynamicId);
            if (instance?.IsClone == true) key += "/instance:" + instance.CloneKey;
        }
        if (!set.ExcludedElements.Contains(key)) set.ExcludedElements = set.ExcludedElements.Append(key).ToArray();
        NativeSetupChanged(); _nativeStatus = "Element excluded from calculation; display geometry is unchanged.";
        return true;
    }
}
