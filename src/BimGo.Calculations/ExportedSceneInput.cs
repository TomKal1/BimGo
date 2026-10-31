using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BimGo.Format;
using BimGo.Scene;

namespace BimGo.Calculations;

/// <summary>Explicit exported-mesh basis for existing .bimgo files; never consumes clipped/visible renderer batches.</summary>
public static class ExportedSceneInput
{
    const double Feet = .3048;
    public static string RoomKey(SceneData scene, int index)
    {
        var r = scene.Rooms[index];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { r.Name, r.Number, r.Link, r.BottomZ, r.TopZ,
            Loops = r.Loops?.Select(l => l.SelectMany(p => new[] { p.X, p.Y }).ToArray()).ToArray() });
        return "room:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <param name="domainZoneKeys">Rooms whose surroundings make up the physical domain (all rooms when null). A split run
    /// solves only these rooms and keeps the others' earlier results, so their geometry is not needed.</param>
    public static CalculationInput Build(SceneData scene, NativeLightingSet definition, CalculationEdits edits,
        LightProfileDocument profiles, CancellationToken cancel = default, IReadOnlyCollection<string> domainZoneKeys = null)
    {
        var set = NativeLightingFiles.Copy(definition); set.Validate();
        if (scene?.Vertices == null || scene.Indices == null || scene.Elements == null || scene.Rooms == null) throw new InvalidDataException("The model has no exported physical geometry/rooms.");
        if (!string.IsNullOrWhiteSpace(scene.SourceView)) throw new InvalidDataException("This export contains only an active view. Re-export the model by category with its enclosure geometry included.");
        if (set.Rooms.Any(i => i >= scene.Rooms.Length)) throw new InvalidDataException("A selected room is missing. Reselect the calculation rooms.");
        var input = new CalculationInput { Set = set, Model = scene.ModelTitle };
        input.Warnings.Add("Geometry basis: exported BimGo triangles, not a certified WWTools extraction. Omitted launch categories/links cannot block or reflect light.");
        input.Warnings.Add("Scalar diffuse, electric light only; no daylight, colored bounce, specular or glass transport. Class reflectances are explicit calculation assumptions.");
        input.Warnings.Add("Exported room plan coordinates are rounded to 0.00001 ft for grid layout, preventing float unit-conversion noise from dropping a complete grid row. Physical transport triangles retain their exported positions.");
        input.Warnings.Add("Transparent exported faces are treated as openings. Fixture housing geometry is excluded. Source candela uses full IES output times the chosen LLF, without preview brightness or lumen rescaling.");
        input.Warnings.Add(set.EmergencyOnly ? "Emergency calculation: only selected fixture instances contribute, at their individual emergency output fractions times LLF." : "Normal calculation: emergency participation/output overrides do not dim normal operation.");
        input.Warnings.Add($"Calculation-only exclusions: {set.ExcludedElements.Length} elements, {set.ExcludedCategories.Length} categories, {set.ExcludedPoints.Length} manual points. Display visibility is independent.");
        Aabb domain = Aabb.Empty;
        var storeys = (scene.Levels ?? Array.Empty<LevelInfo>()).Where(l => l.IsStorey).OrderBy(l => l.Elevation).ToArray();
        input.Storeys = storeys.Select(l => new CalculationLevel { Name = l.Name, Elevation = (l.Elevation + (double)scene.OriginOffset.Z) / Feet }).ToList();
        for (int j = 0; j < set.Rooms.Length; j++)
        {
            int i = set.Rooms[j]; var room = scene.Rooms[i]; string key = RoomKey(scene, i);
            if (set.RoomKeys?.Length > 0 && (set.RoomKeys.Length != set.Rooms.Length || set.RoomKeys[j] != key))
                throw new InvalidDataException("A selected room changed since setup. Reselect the rooms before calculating.");
            if (room.Loops == null || room.Loops.Length == 0) throw new InvalidDataException("Selected room has no boundaries.");
            var levels = (scene.Levels ?? Array.Empty<LevelInfo>()).Where(l => l.Elevation >= room.BottomZ - .08 && l.Elevation < room.TopZ - .15).ToArray();
            input.Zones.Add(new CalculationZone { Key = key, Number = room.Number, Name = room.Name,
                Bottom = (room.BottomZ + (double)scene.OriginOffset.Z) / Feet, Top = (room.TopZ + (double)scene.OriginOffset.Z) / Feet,
                Loops = room.Loops.Select(l => l.SelectMany(p => new[] { Math.Round(p.X/Feet,5)+scene.OriginOffset.X/Feet, Math.Round(p.Y/Feet,5)+scene.OriginOffset.Y/Feet }).ToArray()).ToArray(),
                Level = levels.FirstOrDefault().Name ?? "", LevelNames = levels.Select(l => l.Name).ToArray(),
                LevelElevations = levels.Select(l => (l.Elevation + (double)scene.OriginOffset.Z)/Feet).ToArray() });
            // Own level: the highest storey at or just above the floor. The region reaches 3 ft over the top or up to the level above (the engine's RegionBox).
            int own = Array.FindLastIndex(storeys, l => l.Elevation <= room.BottomZ + .08);
            float ownElevation = own >= 0 ? storeys[own].Elevation : room.BottomZ;
            if (own >= 0) input.Zones[^1].LevelElevation = (ownElevation + (double)scene.OriginOffset.Z) / Feet;
            float top = room.TopZ + (float)(3*Feet);
            int above = Array.FindIndex(storeys, l => l.Elevation > ownElevation + 4*Feet && l.Elevation >= room.TopZ - 2*Feet);
            if (above >= 0) top = Math.Max(top, storeys[above].Elevation);
            if (domainZoneKeys == null || domainZoneKeys.Contains(key)) domain.Include(new Aabb(new Vector3(room.Min, room.BottomZ), new Vector3(room.Max, top)));
        }
        domain.Min -= new Vector3((float)(set.Margin*Feet), (float)(set.Margin*Feet), (float)(1.5*Feet));
        domain.Max += new Vector3((float)(set.Margin*Feet), (float)(set.Margin*Feet), 0);
        input.Warnings.Add($"Transport domain: {(domainZoneKeys == null ? "selected room union" : "union of the rooms solved in this part")} plus {set.Margin:0.##} ft XY, 1.5 ft below and 3 ft above or up to the level above; connections outside this domain are not modeled.");
        var instances = edits?.Instances ?? Array.Empty<CalculationInstance>();
        var instancesByElement = instances.ToLookup(d => d.Element);
        // The fixtures the viewer lights with (a linked model's copies of host fixtures dropped, by default)
        bool[] keptLights = FixtureSources.Kept(scene, profiles?.FixtureSource, out int linkedCopies);
        if (linkedCopies > 0) input.Warnings.Add($"{linkedCopies} linked-model light(s) on top of a host fixture of the same type left out (Lights: fixture lights from {FixtureSources.Clean(profiles?.FixtureSource)}).");
        var allLightsByElement = (scene.Lighting?.Lights ?? Array.Empty<LightSource>()).Select((l,k) => (Light:l, Index:k)).ToLookup(p => p.Light.Element);
        var lightsByElement = allLightsByElement.SelectMany(g => g).Where(p => keptLights[p.Index]).ToLookup(p => p.Light.Element);
        int proxies = 0; bool transparent = false;
        var outputDifferences = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileLumensByText = new Dictionary<string, float>(StringComparer.Ordinal);
        float FileLumens(string text)
        {
            if (!fileLumensByText.TryGetValue(text, out float lumens))
                fileLumensByText[text] = lumens = LightProfile.FromIes("calc", "calc", text, out _)?.Lumens ?? 0f;
            return lumens;
        }
        var unlit = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < scene.Elements.Length; i++)
        {
            cancel.ThrowIfCancellationRequested(); var element = scene.Elements[i];
            bool removed = edits?.Removed != null && i < edits.Removed.Length && edits.Removed[i];
            if (!element.IsLibraryTemplate && !removed) AddElement(i, Matrix4x4.Identity, 0);
            foreach (var instance in instancesByElement[i]) AddElement(i, instance.Transform, instance.InstanceId);
        }
        if (proxies > 0) input.Warnings.Add($"{proxies} included element instance(s) use exported proxy geometry.");
        if (transparent) input.Warnings.Add("This domain includes transparent faces, omitted from the opaque transport mesh.");
        // Like WWTools, a fixture family without a light source is not an emitter; it is reported, not a blocker.
        if (outputDifferences.Count > 0) input.Warnings.Add($"{outputDifferences.Count} fixture type(s) have Revit lumens 2x or more off their .ies file: the calculation uses the file's output, the view Revit's ({string.Join(", ", outputDifferences.Take(6))}{(outputDifferences.Count > 6 ? ", …" : "")}). LIGHTS → Fixture output → .ies files makes the view match.");
        if (unlit.Count > 0) input.Warnings.Add($"{unlit.Count} lighting fixture type(s) have no exported light source and contribute no light: {string.Join(", ", unlit.Take(8))}{(unlit.Count > 8 ? ", …" : "")}. Re-export with lighting data if they should emit.");
        if (input.Emitters.Count == 0 && set.EmergencyOnly) throw new InvalidDataException("No selected emergency fixtures with nonzero output are in this calculation domain.");
        if (input.Emitters.Count == 0) throw new InvalidDataException("No exported light sources are in this calculation domain. Include lighting fixtures/links in the BimGo export.");
        var missing = input.Emitters.Where(e => e.Estimated).ToArray();
        if (missing.Length > 0 && !set.AllowEstimatedPhotometry)
            throw new InvalidDataException($"{missing.Length} source(s) are missing full IES data. Import IES in LIGHTS or enable 'Allow estimated photometry'.");
        if (missing.Length > 0) input.Warnings.Add($"{missing.Length} source(s) use explicitly allowed estimated analytic distributions/output. This is not verified manufacturer photometry.");
        var options=WWTools.Lighting.Engine.SolverOptions.ForQuality((WWTools.Lighting.Engine.CalcQuality)set.Quality,set.Interreflection);
        options.MaxPatches=Math.Max(options.MaxPatches,input.Triangles.Count);
        input.Solver=CalculationSolverSettings.From(options);
        input.Validate(); return input;

        void AddElement(int index, Matrix4x4 transform, int instance)
        {
            var element = scene.Elements[index];
            var category = element.CategoryIndex >= 0 && element.CategoryIndex < CategoryCatalog.All.Count ? CategoryCatalog.All[element.CategoryIndex].Key : "other";
            string identity = LightProfileDocument.InstanceKeyOf(scene, index) ?? "element:" + index;
            if (instance != 0) identity += "/instance:" + instance;
            if (set.ExcludedElements.Contains(identity) || set.ExcludedCategories.Contains(category)) return;
            var fixture = set.Luminaires.FirstOrDefault(l => l.Key == identity);
            double output = set.EmergencyOnly ? (fixture?.Emergency == true ? fixture.EmergencyOutput : 0) : 1;
            var bounds = TransformBounds(element.Bounds, transform);
            if (output > 0 && category == "lightfixtures" && bounds.Overlaps(domain) && !allLightsByElement[index].Any())
                unlit.Add(string.IsNullOrWhiteSpace(element.FamilyType) ? "Unnamed fixture" : element.FamilyType);
            foreach (var light in lightsByElement[index])
            {
                if (output <= 0) continue;
                var source = light.Light;
                Vector3 p = Vector3.Transform(fixture?.UsePreviewLensPosition == true ? source.Position : source.CalculationOrigin ?? source.Position, transform) + Vector3.UnitZ * (float)((fixture?.HeightOffset ?? 0) * Feet);
                if (!Inside(domain, p)) continue;
                string revitKey = source.Ies != null ? LightProfiles.FILE_PREFIX + source.Ies : source.Distribution;
                string chosen = LightProfileDocument.KeyFor(profiles?.ResolveChoice(LightProfileDocument.InstanceKeyOf(scene, index), element.FamilyType), revitKey);
                string hash = LightProfiles.HashOf(chosen);
                string ies = hash == null ? null : scene.Lighting.FindIes(hash)?.Text ?? profiles?.FindImport(hash)?.Text;
                if (chosen != null && hash == null && !LightProfiles.IsGeneratedKey(chosen))
                    throw new InvalidDataException($"{element.FamilyType}: the selected preview preset has no full calculation photometry. Select Revit/default or import a full IES file.");
                if (hash != null && string.IsNullOrWhiteSpace(ies)) throw new InvalidDataException($"Full IES data is missing for {element.FamilyType}. Import it again in LIGHTS.");
                if (source.CalculationOrigin == null && fixture?.UsePreviewLensPosition != true)
                    input.Warnings.Add("An older export lacks source-origin metadata; its exported position is retained. Re-export from the updated add-in to use the family/light-source origin.");
                string provenance = ies == null ? "Explicit analytic estimate from exported output/downward share" : "Full exported/imported IES candela; no preview normalization";
                // The engine lights with the file's own candela; say where that differs from the output the view uses
                if (ies != null)
                {
                    float fileLumens = FileLumens(ies);
                    bool guessed = source.Estimated && MathF.Abs(source.Lumens - 1000f) < 0.5f;
                    float viewLumens = LightProfiles.ResolveOutput(profiles?.Output, source.Lumens, guessed, fileLumens, out bool fromFile);
                    if (fromFile && !guessed) provenance += $"; output from the .ies file ({fileLumens:0} lm; Revit {source.Lumens:0} lm), as in the view";
                    else if (!fromFile && LightProfiles.LumenMismatch(viewLumens, fileLumens) > 0f)
                    {
                        provenance += $"; Revit's {source.Lumens:0} lm differs from the .ies file's {fileLumens:0} lm: the calculation uses the file's, the view Revit's";
                        outputDifferences.Add(element.FamilyType ?? "Unnamed fixture");
                    }
                }
                // Analytic spot is retained as parameters in provenance and resolved to full IES before solving.
                if (ies == null && TrySpot(chosen, out int beam, out int field))
                    provenance = $"Explicit analytic spot estimate; beam={beam}; field={field}";
                bool body = bounds.Max.X >= bounds.Min.X;
                input.Emitters.Add(new CalculationEmitter { Key = identity + "/source:" + light.Index, FixtureKey = identity, Type = element.FamilyType,
                    BodyMin = body ? World(bounds.Min) : null, BodyMax = body ? World(bounds.Max) : null,
                    AutoMount = source.CalculationOrigin != null && fixture?.UsePreviewLensPosition != true && (fixture?.HeightOffset ?? 0) == 0,
                    // No photometry at all: the estimated lobe's downward share is world-down, as the view lights it and WW Tools
                    // treats it (spherical); the exported aim of such a fixture is only its family's Z (up when ceiling hosted)
                    Position = World(p), Nadir = chosen == null && ies == null ? new double[] { 0, 0, -1 } : Vector(Vector3.Normalize(Vector3.TransformNormal(source.Aim, transform))),
                    Axis0 = chosen == null && ies == null ? new double[] { 1, 0, 0 } : Vector(Vector3.Normalize(Vector3.TransformNormal(source.Across, transform))),
                    Ies = ies, Estimated = ies == null, Provenance = provenance + (source.Estimated ? "; source placement/output marked estimated by exporter" : ""),
                    Lumens = source.Lumens, Downward = chosen == LightProfiles.HEMISPHERICAL ? 1 : source.Downward, Multiplier = set.LightLossFactor * output });
            }
            if (category == "lightfixtures" || lightsByElement[index].Any()) return;
            bool stair = category is "stairs" or "ramps";
            bool walkable = category == "floors" || stair;
            if (!bounds.Overlaps(domain)) return;
            if (element.IsProxy) proxies++;
            transparent |= element.TransparentCount > 0;
            int end = checked(element.OpaqueStart + element.OpaqueCount);
            if (element.OpaqueStart < 0 || end > scene.Indices.Length || element.OpaqueCount % 3 != 0) throw new InvalidDataException("Invalid exported triangle range.");
            for (int k = element.OpaqueStart; k < end; k += 3)
            {
                if ((k & 1023) == 0) cancel.ThrowIfCancellationRequested();
                var ia = scene.Indices[k]; var ib = scene.Indices[k+1]; var ic = scene.Indices[k+2];
                if (ia >= scene.Vertices.Length || ib >= scene.Vertices.Length || ic >= scene.Vertices.Length) throw new InvalidDataException("Invalid exported vertex index.");
                Vector3 a = Vector3.Transform(scene.Vertices[ia].Position, transform), b = Vector3.Transform(scene.Vertices[ib].Position, transform), c = Vector3.Transform(scene.Vertices[ic].Position, transform);
                var triBounds = new Aabb(Vector3.Min(a, Vector3.Min(b,c)), Vector3.Max(a, Vector3.Max(b,c)));
                if (!triBounds.Overlaps(domain)) continue;
                Vector3 normal = Vector3.Cross(b-a,c-a);
                if (normal.LengthSquared() < 1e-16f) continue;
                var exportedNormal = Vector3.TransformNormal(scene.Vertices[ia].Normal + scene.Vertices[ib].Normal + scene.Vertices[ic].Normal, transform);
                if (Vector3.Dot(normal, exportedNormal) < 0) { (b,c) = (c,b); normal = -normal; }
                normal = Vector3.Normalize(normal);
                int cls = category is "furniture" or "furnsys" or "casework" or "generic" or "specialty" or "plumbing" ? 3 : normal.Z > .7 ? 2 : normal.Z < -.7 ? 0 : 1;
                input.Triangles.Add(new CalculationTriangle { Vertices = World(a).Concat(World(b)).Concat(World(c)).ToArray(), Class = cls,
                    Colour = new[] { (scene.Vertices[ia].Colour & 255) / 255.0, ((scene.Vertices[ia].Colour >> 8) & 255) / 255.0, ((scene.Vertices[ia].Colour >> 16) & 255) / 255.0 },
                    Tag = index+1, SurfaceKey = identity + "/triangle:" + ((k-element.OpaqueStart)/3), Walkable = walkable, Stair = stair, Ceiling = category == "ceilings" });
                if (input.Triangles.Count > 250000) throw new CalculationTooLargeException("The physical domain exceeds 250,000 input triangles. Reduce the selected domain or re-export simpler physical geometry.");
            }
        }
        double[] World(Vector3 p) => new[] { (p.X + (double)scene.OriginOffset.X)/Feet, (p.Y + (double)scene.OriginOffset.Y)/Feet, (p.Z + (double)scene.OriginOffset.Z)/Feet };
    }
    static double[] Vector(Vector3 v) => new[] { (double)v.X, v.Y, v.Z };
    static bool TrySpot(string key,out int beam,out int field)
    {
        beam=field=0;
        if (key==null || !key.StartsWith("gen:spot:",StringComparison.Ordinal) || !LightProfiles.IsGeneratedKey(key)) return false;
        var parts=key.Substring(9).Split(':');
        return parts.Length==2&&int.TryParse(parts[0],out beam)&&int.TryParse(parts[1],out field);
    }
    static bool Inside(Aabb b, Vector3 p) => p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z;
    static Aabb TransformBounds(Aabb b, Matrix4x4 m)
    {
        var result = Aabb.Empty;
        for (int i=0;i<8;i++) result.Include(Vector3.Transform(new((i&1)==0?b.Min.X:b.Max.X,(i&2)==0?b.Min.Y:b.Max.Y,(i&4)==0?b.Min.Z:b.Max.Z),m));
        return result;
    }
}
