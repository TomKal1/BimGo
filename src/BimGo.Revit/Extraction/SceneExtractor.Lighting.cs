using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using BimGo.Scene;
using SceneLight = BimGo.Scene.LightSource;
using Visual = Autodesk.Revit.DB.Visual;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Artificial lighting capture: which surfaces glow and where lighting fixtures emit light.
    /// <list type="bullet">
    /// <item><b>Glow</b> comes from a material's Revit self-illumination (Generic "Self Illumination" luminance, filter
    /// colour and colour temperature; Advanced / Physical "Emission"), anywhere in the model. Inside Lighting Fixtures
    /// elements, materials whose name contains one of <see cref="LaunchSettings.EmissiveKeywords"/> glow too, and a
    /// raised fixture with neither gets its bottom, downward-facing faces as a guessed lens.</item>
    /// <item><b>Lights:</b> one per Lighting Fixtures element, at the centre of its glowing surface (just in front of
    /// it), pointing the way that surface faces; output and colour temperature from the fixture's parameters when they
    /// can be read ("Initial Intensity", "Initial Color"…), else estimated.</item>
    /// </list>
    /// Everything here is best effort: a failure leaves the element unlit, never fails the extraction.
    /// </summary>
    internal sealed partial class SceneExtractor
    {
        #region Constants

        /// <summary>Glow strength of keyword-matched and guessed lenses (≈ a 2000 cd/m² diffuser).</summary>
        private const float KEYWORD_STRENGTH = 2.3f;

        /// <summary>A fixture whose bottom is this far above the level below counts as ceiling / wall mounted (m).</summary>
        private const float RAISED_FIXTURE = 1.2f;

        /// <summary>X-D1 r3: a glow has a facing when |Σ normals| ≥ this share of its area (a flat lens ≈ 1, a closed solid ≈ 0).</summary>
        private const float COHERENT_LENS = 0.5f;

        /// <summary>Output assumed when a fixture's parameters don't say (lm).</summary>
        private const float DEFAULT_LUMENS = 1000f;

        /// <summary>Colour temperature assumed when a fixture's parameters don't say (K).</summary>
        private const float DEFAULT_KELVIN = 3500f;

        private static readonly string[] LUMEN_PARAMETERS = { "Initial Intensity", "Luminous Flux", "Lumens", "Light Output", "Lamp Lumens" };
        private static readonly string[] KELVIN_PARAMETERS = { "Initial Color", "Initial Colour", "Color Temperature", "Colour Temperature", "CCT" };

        #endregion

        #region Fields

        private readonly string[] _emissiveKeywords;
        private float[] _levelElevations = Array.Empty<float>();

        // The element being extracted, when it is a lighting fixture
        private bool _fixture;
        private float? _fixtureLumens, _fixtureKelvin;
        private bool _fixtureGuessedLens;

        // Round X-D1: the fixture's photometric file (hash) and orientation (scene axes; null when it isn't a family instance)
        private string _fixtureIes;
        private float _fixtureIesLumens;
        private string _fixtureShape; // Round Y-C1: a spot / hemispherical light's generated profile key

        // X-D1 r3: the fixture's transform (scene axes incl. a link's), its nested light sources (non-shared nested
        // lighting families, read from the family once), and whether shared nested lights stand in for it
        private Transform _fixtureTransform;
        private NestedLight[] _fixtureNested = Array.Empty<NestedLight>();
        private bool _fixtureSharedLights;

        // X-D1 r5: the fixture type has a light source of its own; its nested lighting families as Revit placed them
        // (from its geometry: true placement, mirroring included), by "Family: Type"
        private bool _fixtureOwnLight;
        private ElementId _fixtureTypeId;
        private readonly List<(string Name, Transform World)> _fixturePlacements = new();

        // X-D1 r6: the transform Revit draws the fixture's own symbol with (its top-level geometry instance: true
        // placement, mirroring included), or null when its geometry isn't an instance (cut / joined)
        private Transform _fixtureDrawTransform;
        private bool _fixtureMirrored, _fixtureHandFlipped, _fixtureFacingFlipped;
        private Vector3? _fixtureAim, _fixtureAcross;

        // Output
        private readonly List<EmissiveRun> _emissiveRuns = new();
        private readonly List<SceneLight> _lights = new();
        private int _selfIlluminatedMaterials, _guessedLenses, _estimatedLights;

        // Round X-D1: photometric files by content hash, each type's (per document), where each file name was found
        private readonly Dictionary<string, LightIesFile> _iesFiles = new(StringComparer.Ordinal);
        private readonly Dictionary<(Document Doc, long Type), (string Hash, double Tilt, float Lumens, string Shape)> _iesOfType = new();
        private readonly Dictionary<string, string> _iesPathOfName = new(StringComparer.OrdinalIgnoreCase);
        private int _shapeLights; // Round Y-C1: lights with a spot / hemispherical distribution
        private int _iesLights, _lensOverrules, _nestedLights, _nestedByTransform, _nestedByDrawTransform, _drawDiffers;
        private readonly Dictionary<(Document Doc, long Type), bool> _ownLightOfType = new();
        private readonly Dictionary<(Document Doc, long Family), NestedLight[]> _nestedOfFamily = new();

        /// <summary>
        /// X-D1 r3: a light source nested in a fixture family (a non-shared nested lighting family, e.g. the up and down
        /// halves of a wall light, each on its own work plane).
        /// </summary>
        /// <param name="Local">Its transform in the host family.</param>
        /// <param name="Hash">Its photometric file, or null.</param>
        /// <param name="Tilt">Its web's tilt (radians, 90° = down), NaN when unknown.</param>
        /// <param name="IesLumens">The file's output, or 0.</param>
        /// <param name="Lumens">Its own lumens parameter, or null.</param>
        /// <param name="Kelvin">Its own colour temperature, or null.</param>
        /// <param name="Shape">Round Y-C1: its spot / hemispherical distribution's generated profile key, or null.</param>
        private readonly record struct NestedLight(string Name, Transform Local, string Hash, double Tilt, float IesLumens, float? Lumens, float? Kelvin, string Shape);

        #endregion

        /// <summary>
        /// A material's colour and glow, cached per document.
        /// </summary>
        /// <param name="Colour">RGBA8 colour (transparency in alpha).</param>
        /// <param name="SelfIllumination">Packed glow from the appearance asset, or 0.</param>
        /// <param name="Keyword">True if the name contains an emissive keyword (counts inside lighting fixtures only).</param>
        /// <param name="Material">The material's index in the Realistic-mode table (<see cref="MaterialData.NONE"/> when
        /// textures aren't extracted or there is no material).</param>
        private readonly record struct MaterialLook(uint Colour, uint SelfIllumination, bool Keyword, ushort Material);

        #region Per element

        /// <summary>
        /// Notes whether the element is a lighting fixture and reads its output and colour temperature.
        /// </summary>
        private void BeginFixture(Element element)
        {
            _fixture = false;
            _fixtureLumens = _fixtureKelvin = null;
            _fixtureGuessedLens = false;
            _fixtureIes = null;
            _fixtureIesLumens = 0f;
            _fixtureShape = null;
            _fixtureAim = _fixtureAcross = null;
            _fixtureTransform = null;
            _fixtureNested = Array.Empty<NestedLight>();
            _fixtureSharedLights = false;
            _fixtureOwnLight = false;
            _fixtureTypeId = null;
            _fixturePlacements.Clear();
            _fixtureDrawTransform = null;
            _fixtureMirrored = _fixtureHandFlipped = _fixtureFacingFlipped = false;
            try
            {
                _fixture = element.Category?.Id.Value == (long)BuiltInCategory.OST_LightingFixtures;
                if (!_fixture) { return; }
                _fixtureTypeId = element.GetTypeId();
                Element type = _src.Doc.GetElement(_fixtureTypeId);
                _fixtureLumens = ReadLumens(element, type);
                _fixtureKelvin = ReadKelvin(element, type);
                ReadPhotometry(element, type); // Round X-D1
                ReadNestedLights(element, type); // X-D1 r3
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: fixture {element.Id.Value} parameters unreadable: {ex.Message}");
            }
        }

        /// <summary>
        /// The glow a surface with this material gets in the current element (0 = none).
        /// </summary>
        private uint EmissiveOf(MaterialLook look)
        {
            if (look.SelfIllumination != 0u) { return look.SelfIllumination; }
            if (_fixture && look.Keyword) { return LightingData.PackEmissive(LightingData.KelvinToRgb(_fixtureKelvin ?? DEFAULT_KELVIN), KEYWORD_STRENGTH); }
            return 0u;
        }

        /// <summary>
        /// For a raised fixture with no glowing material: marks its downward-facing faces in the bottom 15 % (at least
        /// 3 cm) as the lens.
        /// </summary>
        private void MarkFallbackLens()
        {
            while (_tmpEmissive.Count < _tmpVertices.Count) { _tmpEmissive.Add(0u); }
            foreach (uint e in _tmpEmissive) { if (e != 0u) { return; } }
            if (_tmpOpaque.Count == 0) { return; }

            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (SceneVertex v in _tmpVertices)
            {
                minZ = MathF.Min(minZ, v.Position.Z);
                maxZ = MathF.Max(maxZ, v.Position.Z);
            }
            if (minZ - LevelBelow(minZ) < RAISED_FIXTURE) { return; }

            float limit = minZ + MathF.Max(0.03f, 0.15f * (maxZ - minZ));
            uint glow = LightingData.PackEmissive(LightingData.KelvinToRgb(_fixtureKelvin ?? DEFAULT_KELVIN), KEYWORD_STRENGTH);
            bool any = false;
            for (int i = 0; i + 2 < _tmpOpaque.Count; i += 3)
            {
                int a = _tmpOpaque[i], b = _tmpOpaque[i + 1], c = _tmpOpaque[i + 2];
                Vector3 pa = _tmpVertices[a].Position, pb = _tmpVertices[b].Position, pc = _tmpVertices[c].Position;
                Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
                float length = cross.Length();
                if (length < 1e-10f || cross.Z / length > -0.7f) { continue; }
                if (MathF.Max(pa.Z, MathF.Max(pb.Z, pc.Z)) > limit) { continue; }
                _tmpEmissive[a] = _tmpEmissive[b] = _tmpEmissive[c] = glow;
                any = true;
            }
            if (any)
            {
                _fixtureGuessedLens = true;
                _guessedLenses++;
            }
        }

        /// <summary>
        /// Appends the element's glowing vertices as runs (merged with the previous run when contiguous and equal).
        /// </summary>
        /// <param name="vertexBase">Where the element's vertices start in the scene.</param>
        private void CommitEmissive(int vertexBase)
        {
            while (_tmpEmissive.Count < _tmpVertices.Count) { _tmpEmissive.Add(0u); }
            int i = 0;
            while (i < _tmpVertices.Count)
            {
                uint value = _tmpEmissive[i];
                if (value == 0u) { i++; continue; }
                int start = i;
                while (i < _tmpVertices.Count && _tmpEmissive[i] == value) { i++; }

                int sceneStart = vertexBase + start, count = i - start;
                if (_emissiveRuns.Count > 0)
                {
                    EmissiveRun last = _emissiveRuns[^1];
                    if (last.Emissive == value && last.Start + last.Count == sceneStart)
                    {
                        _emissiveRuns[^1] = last with { Count = last.Count + count };
                        continue;
                    }
                }
                _emissiveRuns.Add(new EmissiveRun(sceneStart, count, value));
            }
        }

        /// <summary>
        /// Adds the fixture's light: at the area-weighted centre of its glowing triangles, 5 cm in front of them, aimed
        /// the way they face; without any, near the top of the fixture and nearly omnidirectional (a floor or table lamp).
        /// </summary>
        /// <param name="elementIndex">The element's index in the snapshot.</param>
        /// <param name="bounds">The element's bounds (scene-local).</param>
        private void AddFixtureLight(int elementIndex, Aabb bounds)
        {
            // X-D1 r3: shared nested lights are elements of their own (they light it); nested light sources light from
            // where they sit, each its own way
            if (_fixtureSharedLights) { return; }
            if (_fixtureNested.Length > 0 && _fixtureTransform != null)
            {
                AddNestedLights(elementIndex, bounds);
                return;
            }

            Vector3 centroid = Vector3.Zero, normal = Vector3.Zero;
            float area = 0f;
            for (int i = 0; i + 2 < _tmpOpaque.Count; i += 3)
            {
                int a = _tmpOpaque[i], b = _tmpOpaque[i + 1], c = _tmpOpaque[i + 2];
                if (a >= _tmpEmissive.Count || _tmpEmissive[a] == 0u) { continue; }
                Vector3 pa = _tmpVertices[a].Position, pb = _tmpVertices[b].Position, pc = _tmpVertices[c].Position;
                Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
                float twiceArea = cross.Length();
                if (twiceArea < 1e-10f) { continue; }
                centroid += (pa + pb + pc) / 3f * twiceArea;
                normal += cross;
                area += twiceArea;
            }

            // X-D1 r3: the glow only has a facing when its triangles mostly agree (a flat lens). A solid lens glows on every
            // face, its normals cancel and what's left is noise: no facing then (no 5 cm push, no aim from it)
            Vector3? lens = area > 1e-8f && normal.Length() > COHERENT_LENS * area ? Vector3.Normalize(normal) : null;

            // X-D1 r5: a fixture with a light source of its own aims (and sits in front of its glow) the way its family
            // says: lens faces can be modelled backwards. Without one, the lens's facing as before X-D1, else the family's
            bool family = _fixtureOwnLight && _fixtureAim != null;
            if (!family && lens != null && _fixtureAim != null) { _lensOverrules++; }
            Vector3 aim = family ? _fixtureAim.Value : lens ?? _fixtureAim ?? -Vector3.UnitZ;

            Vector3 position;
            float downward;
            bool estimated = _fixtureGuessedLens || _fixtureLumens == null;
            if (area > 1e-8f)
            {
                centroid /= area;
                position = family || lens != null ? centroid + aim * 0.05f : centroid;
                downward = aim.Z < -0.5f ? 0.8f : aim.Z > 0.5f ? 0.15f : 0.4f;
            }
            else
            {
                if (!bounds.IsValid) { return; }
                Vector3 size = bounds.Size;
                position = new Vector3(bounds.Center.X, bounds.Center.Y, bounds.Max.Z - 0.15f * size.Z);
                downward = 0.2f;
                estimated = true;
            }

            Vector3 across = _fixtureAcross ?? Vector3.UnitX;
            _lights.Add(new SceneLight
            {
                Element = elementIndex,
                Position = position,
                CalculationOrigin = _fixtureTransform == null ? null : ToScenePoint(_fixtureTransform.Origin),
                // Revit's lumens; without them the photometric file's own output (X-D1), else a guess
                Lumens = _fixtureLumens ?? (_fixtureIesLumens >= 10f ? Math.Clamp(_fixtureIesLumens, 50f, 50000f) : DEFAULT_LUMENS),
                Kelvin = _fixtureKelvin ?? DEFAULT_KELVIN,
                Downward = downward,
                Estimated = estimated,
                Ies = _fixtureIes,
                Aim = aim,
                Across = across,
                Distribution = _fixtureIes == null ? _fixtureShape : null // Round Y-C1
            });
            if (estimated) { _estimatedLights++; }
            if (_fixtureIes != null) { _iesLights++; }
            else if (_fixtureShape != null) { _shapeLights++; }
        }

        /// <summary>
        /// The elevation of the highest level at or below a height (scene Z), or the lowest level.
        /// </summary>
        private float LevelBelow(float z)
        {
            float best = float.MinValue;
            foreach (float elevation in _levelElevations)
            {
                if (elevation <= z + 0.01f && elevation > best) { best = elevation; }
            }
            if (best == float.MinValue) { best = _levelElevations.Length > 0 ? _levelElevations[0] : z; }
            return best;
        }

        #endregion

        #region Snapshot

        /// <summary>
        /// The captured lighting for the snapshot (logged).
        /// </summary>
        private LightingData BuildLighting()
        {
            Utilities.Log_Utils.Write($"Lighting: {_lights.Count} fixture lights ({_estimatedLights} estimated, {_iesLights} with a photometric file, {_shapeLights} spot / hemispherical, {_lensOverrules} without a light source aimed by their lens, {_nestedLights} from nested light sources ({_nestedLights - _nestedByTransform - _nestedByDrawTransform} by nested geometry, {_nestedByDrawTransform} by the drawn host, {_nestedByTransform} by the instance transform; {_drawDiffers} hosts drawn unlike their instance transform; placed {_mirrorChoices[3]} by their hand / facing flips, overruled by the drawn bounds: {_mirrorChoices[0]} as is, {_mirrorChoices[1]} across YZ, {_mirrorChoices[2]} across XZ; {_flippedHosts} hosts flipped), {_outsideLights} pulled inside their fixture), " +
                $"{_emissiveRuns.Count} glowing vertex runs, {_selfIlluminatedMaterials} self-illuminated materials, {_guessedLenses} guessed lenses, {_iesFiles.Count} .ies files.");
            return _emissiveRuns.Count == 0 && _lights.Count == 0
                ? LightingData.Empty
                : new LightingData { Emissive = _emissiveRuns.ToArray(), Lights = _lights.ToArray(), IesFiles = _iesFiles.Values.ToArray() };
        }

        #endregion

        #region Materials

        /// <summary>
        /// True if a material name contains one of the emissive keywords.
        /// </summary>
        private bool MatchesEmissiveKeyword(string name)
        {
            if (string.IsNullOrEmpty(name) || _emissiveKeywords.Length == 0) { return false; }
            string lower = name.ToLowerInvariant();
            foreach (string keyword in _emissiveKeywords)
            {
                if (lower.Contains(keyword, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// The glow from a material's appearance asset: Generic self-illumination (luminance, filter colour, colour
        /// temperature) or Advanced / Physical emission. 0 if none or unreadable.
        /// </summary>
        private uint ReadSelfIllumination(Material material)
        {
            try
            {
                ElementId assetId = material.AppearanceAssetId;
                if (assetId == null || assetId == ElementId.InvalidElementId) { return 0u; }
                if (_src.Doc.GetElement(assetId) is not AppearanceAssetElement appearance) { return 0u; }
                Visual.Asset asset = appearance.GetRenderingAsset();
                if (asset == null) { return 0u; }

                // Advanced / Physical materials switch emission on and off explicitly
                if (asset.FindByName("opaque_emission") is Visual.AssetPropertyBoolean emission && !emission.Value) { return 0u; }

                double luminance = FindDouble(asset, "generic_self_illum_luminance") ?? FindDouble(asset, "opaque_luminance") ?? 0.0;
                if (!(luminance > 0.0)) { return 0u; }

                Vector3 colour = FindColour(asset, "generic_self_illum_filter_map") ?? FindColour(asset, "opaque_luminance_modifier") ?? Vector3.One;
                double kelvin = FindDouble(asset, "generic_self_illum_color_temperature") ?? 0.0;
                if (kelvin >= 1000.0) { colour *= LightingData.KelvinToRgb((float)kelvin); }

                _selfIlluminatedMaterials++;
                Utilities.Log_Utils.Write($"{_src.Describe()}: material “{material.Name}” glows ({luminance:0} cd/m²).");
                return LightingData.PackEmissive(colour, LightingData.StrengthFromLuminance((float)luminance));
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: material “{material.Name}” appearance unreadable: {ex.Message}");
                return 0u;
            }
        }

        private static double? FindDouble(Visual.Asset asset, string name) => asset.FindByName(name) switch
        {
            Visual.AssetPropertyDouble d => d.Value,
            Visual.AssetPropertyFloat f => f.Value,
            _ => null
        };

        private static Vector3? FindColour(Visual.Asset asset, string name)
        {
            if (asset.FindByName(name) is not Visual.AssetPropertyDoubleArray4d property) { return null; }
            IList<double> rgba = property.GetValueAsDoubles();
            if (rgba == null || rgba.Count < 3) { return null; }
            var colour = new Vector3((float)rgba[0], (float)rgba[1], (float)rgba[2]);
            return colour.X + colour.Y + colour.Z > 1e-3f ? colour : null;
        }

        #endregion

        #region Photometry (Round X-D1)

        /// <summary>
        /// X-D1 r5: called by the geometry walk for each family instance inside a lighting fixture's geometry: a nested
        /// Lighting Fixtures family (not the fixture's own symbol) is recorded with its accumulated (true) placement.
        /// </summary>
        private void NoteNestedPlacement(GeometryInstance instance, Transform world)
        {
            if (!_fixture || _fixtureNested.Length == 0) { return; }
            try
            {
                // r6: the first instance met is the fixture's own symbol (the walk visits the top level first)
                ElementId symbolId = instance.GetSymbolGeometryId().SymbolId;
                if (_fixtureDrawTransform == null && (symbolId == _fixtureTypeId || _fixturePlacements.Count == 0))
                {
                    _fixtureDrawTransform = world;
                    if (symbolId == _fixtureTypeId) { return; }
                }
                if (symbolId == null || symbolId == ElementId.InvalidElementId || symbolId == _fixtureTypeId) { return; }
                if (_src.Doc.GetElement(symbolId) is not FamilySymbol symbol) { return; }
                if (symbol.Category?.Id.Value != (long)BuiltInCategory.OST_LightingFixtures) { return; }
                _fixturePlacements.Add(($"{symbol.FamilyName}: {symbol.Name}", world));
            }
            catch (Exception)
            {
                // Best effort: the family document's transform is used instead
            }
        }

        private int _outsideLights;

        /// <summary>
        /// X-D1 r4: a nested light source's position, pulled inside its fixture's bounds when it landed more than 15 cm
        /// outside them (a light behind the wall would light the far side).
        /// </summary>
        private Vector3 InsideFixture(Vector3 p, Aabb bounds, int elementIndex)
        {
            if (!bounds.IsValid) { return p; }
            const float SLACK = 0.15f;
            Vector3 min = bounds.Min, max = bounds.Max;
            bool outside = p.X < min.X - SLACK || p.Y < min.Y - SLACK || p.Z < min.Z - SLACK || p.X > max.X + SLACK || p.Y > max.Y + SLACK || p.Z > max.Z + SLACK;
            if (!outside) { return p; }
            if (_outsideLights++ < 5)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: fixture {elementIndex}: a nested light source landed outside the fixture ({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}): pulled inside.");
            }
            return Vector3.Clamp(p, min, max);
        }

        /// <summary>
        /// The fixture's orientation (its family transform: IES gamma 0 along the family's −Z, C 0° along its X; a
        /// photometric web's Tilt Angle is measured from horizontal, so Revit's usual 90° is straight down and only the
        /// difference from 90° turns the aim, about the family's Y) and its type's photometric web file (read once per type, copied into
        /// the snapshot by content). Best effort: anything unreadable leaves the light on BimGo's own lobe.
        /// </summary>
        private void ReadPhotometry(Element element, Element type)
        {
            double tilt = double.NaN; // unknown: no turn
            if (type != null)
            {
                var key = (_src.Doc, type.Id.Value);
                if (!_iesOfType.TryGetValue(key, out (string Hash, double Tilt, float Lumens, string Shape) found))
                {
                    found = ReadTypeIes(_src.Doc, element, type, FamilyTypeOf(element));
                    _iesOfType[key] = found;
                }
                _fixtureIes = found.Hash;
                _fixtureIesLumens = found.Lumens;
                _fixtureShape = found.Shape;
                tilt = found.Tilt;
            }

            if (element is not FamilyInstance instance) { return; }
            // r4: the total transform (GetTransform leaves out facing / hand flips and mirroring)
            Transform t = instance.GetTotalTransform();
            if (_src.Transform != null) { t = _src.Transform.Multiply(t); }
            _fixtureTransform = t;
            _fixtureMirrored = instance.Mirrored;
            _fixtureHandFlipped = instance.HandFlipped;
            _fixtureFacingFlipped = instance.FacingFlipped;
            (Vector3 Aim, Vector3 Across)? orient = Orient(t, tilt);
            if (orient != null)
            {
                _fixtureAim = orient.Value.Aim;
                _fixtureAcross = orient.Value.Across;
            }
        }

        /// <summary>
        /// Gamma 0 and C 0° of a light with a transform: −BasisZ and BasisX, turned about BasisY by the web's tilt's
        /// difference from 90° (Revit measures it from horizontal: 90° = straight down). Null if degenerate.
        /// </summary>
        private static (Vector3 Aim, Vector3 Across)? Orient(Transform t, double tilt)
        {
            XYZ aim = -t.BasisZ, across = t.BasisX;
            double turnBy = double.IsFinite(tilt) ? Math.PI / 2.0 - tilt : 0.0;
            if (Math.Abs(turnBy) > 1e-4)
            {
                Transform turn = Transform.CreateRotation(t.BasisY, turnBy);
                aim = turn.OfVector(aim);
                across = turn.OfVector(across);
            }
            var a = new Vector3((float)aim.X, (float)aim.Y, (float)aim.Z);
            var c = new Vector3((float)across.X, (float)across.Y, (float)across.Z);
            if (a.LengthSquared() < 1e-8f || c.LengthSquared() < 1e-8f) { return null; }
            return (Vector3.Normalize(a), Vector3.Normalize(c));
        }

        /// <summary>
        /// X-D1 r3: a fixture without a light source of its own that holds lights: shared nested lighting families (they
        /// are elements of their own: the host gets no light) or non-shared ones (read from the family document once per
        /// family: each nested light's transform, file, tilt, lumens, colour). Not in the family library pass (inside a
        /// transaction: the family can't be opened then).
        /// </summary>
        private void ReadNestedLights(Element element, Element type)
        {
            if (element is not FamilyInstance instance || type == null || _libraryPass) { return; }
            var key = (_src.Doc, type.Id.Value);
            if (!_ownLightOfType.TryGetValue(key, out bool own))
            {
                own = false;
                try { own = Autodesk.Revit.DB.Lighting.LightType.GetLightTypeFromInstance(_src.Doc, element.Id) != null; }
                catch (Exception) { own = false; }
                _ownLightOfType[key] = own;
            }
            _fixtureOwnLight = own;
            if (own) { return; }

            // Shared nested lights: separate elements, extracted with their own light
            foreach (ElementId id in instance.GetSubComponentIds())
            {
                if (_src.Doc.GetElement(id) is FamilyInstance sub && sub.Category?.Id.Value == (long)BuiltInCategory.OST_LightingFixtures)
                {
                    _fixtureSharedLights = true;
                    return;
                }
            }

            Family family = instance.Symbol?.Family;
            if (family == null || family.IsInPlace) { return; }
            var familyKey = (_src.Doc, family.Id.Value);
            if (!_nestedOfFamily.TryGetValue(familyKey, out NestedLight[] nested))
            {
                nested = ReadFamilyLights(family, FamilyTypeOf(element));
                _nestedOfFamily[familyKey] = nested;
            }
            _fixtureNested = nested;
        }

        /// <summary>
        /// X-D1 r3: the light sources nested (not shared) in a fixture family, read from its family document (opened and
        /// closed without saving). Empty when there are none or the family can't be opened.
        /// </summary>
        private NestedLight[] ReadFamilyLights(Family family, string label)
        {
            Document familyDoc = null;
            try
            {
                if (!family.IsEditable) { return Array.Empty<NestedLight>(); }
                familyDoc = _src.Doc.EditFamily(family);
                var lights = new List<NestedLight>();
                foreach (FamilyInstance nested in new FilteredElementCollector(familyDoc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    if (nested.Category?.Id.Value != (long)BuiltInCategory.OST_LightingFixtures) { continue; }
                    Element nestedType = familyDoc.GetElement(nested.GetTypeId());
                    if (nestedType == null) { continue; }
                    bool hasLight;
                    try { hasLight = Autodesk.Revit.DB.Lighting.LightType.GetLightTypeFromInstance(familyDoc, nested.Id) != null; }
                    catch (Exception) { hasLight = false; }
                    if (!hasLight) { continue; }
                    (string hash, double tilt, float iesLumens, string shape) = ReadTypeIes(familyDoc, nested, nestedType, $"{label} › {FamilyTypeOf(nested)}");
                    lights.Add(new NestedLight(FamilyTypeOf(nested), nested.GetTotalTransform(), hash, tilt, iesLumens, ReadLumens(nested, nestedType), ReadKelvin(nested, nestedType), shape));
                }
                if (lights.Count > 0) { Utilities.Log_Utils.Write($"{_src.Describe()}: “{label}” holds {lights.Count} nested light source(s)."); }
                return lights.ToArray();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: family of “{label}” not opened for nested lights: {ex.Message}");
                return Array.Empty<NestedLight>();
            }
            finally
            {
                try { familyDoc?.Close(false); }
                catch (Exception ex) { Utilities.Log_Utils.Write($"Family document not closed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// X-D1 r3: one light per nested light source, where it sits in the placed fixture, aimed its own way. Output: its
        /// own lumens, else its file's, else the fixture's shared between them.
        /// </summary>
        /// <param name="bounds">The fixture's extracted bounds: a source landing outside them (+15 cm) was placed wrongly
        /// (an unexpected transform) and is pulled back inside (counted, the first few logged).</param>
        private void AddNestedLights(int elementIndex, Aabb bounds)
        {
            int count = _fixtureNested.Length;
            var used = new bool[_fixturePlacements.Count];
            if (_fixtureDrawTransform != null && _fixtureTransform != null && !_fixtureDrawTransform.AlmostEqual(_fixtureTransform))
            {
                // r6: diagnostics: Revit draws this one differently from its instance transform (mirrored / flipped?)
                if (_drawDiffers++ < 3)
                {
                    XYZ a = _fixtureTransform.BasisX, b = _fixtureDrawTransform.BasisX;
                    Utilities.Log_Utils.Write($"{_src.Describe()}: fixture {elementIndex} drawn with X ({b.X:0.00}, {b.Y:0.00}, {b.Z:0.00}) det {_fixtureDrawTransform.Determinant:0.0}, " +
                        $"instance transform X ({a.X:0.00}, {a.Y:0.00}, {a.Z:0.00}) det {_fixtureTransform.Determinant:0.0}.");
                }
            }
            foreach (NestedLight n in _fixtureNested)
            {
                // r5: where Revit placed this nested family (its geometry), else the family document's transform
                Transform t = null;
                for (int k = 0; k < _fixturePlacements.Count; k++)
                {
                    if (used[k] || !string.Equals(_fixturePlacements[k].Name, n.Name, StringComparison.Ordinal)) { continue; }
                    used[k] = true;
                    t = _fixturePlacements[k].World;
                    break;
                }
                if (t == null && _fixtureDrawTransform != null)
                {
                    // r6: non-shared nested families are merged into the host's symbol geometry (no instance of their own):
                    // the nested transform in the family, under the transform Revit draws the host's symbol with. r7: a
                    // mirrored instance's transforms don't carry the mirror, so the placement is checked against where the
                    // fixture was really drawn (its extracted bounds) as is and mirrored across the family's X and Y planes
                    t = PlaceInsideDrawn(_fixtureDrawTransform, n.Local, bounds, elementIndex);
                    _nestedByDrawTransform++;
                }
                if (t == null)
                {
                    t = _fixtureTransform.Multiply(n.Local);
                    _nestedByTransform++;
                }
                (Vector3 Aim, Vector3 Across) orient = Orient(t, n.Tilt) ?? (-Vector3.UnitZ, Vector3.UnitX);
                float lumens = n.Lumens ?? (n.IesLumens >= 10f ? Math.Clamp(n.IesLumens, 50f, 50000f) : (_fixtureLumens ?? DEFAULT_LUMENS) / count);
                bool estimated = n.Lumens == null && n.IesLumens < 10f && _fixtureLumens == null;
                _lights.Add(new SceneLight
                {
                    Element = elementIndex,
                    Position = InsideFixture(ToScenePoint(t.Origin) + orient.Aim * 0.02f, bounds, elementIndex),
                    CalculationOrigin = ToScenePoint(t.Origin),
                    Lumens = lumens,
                    Kelvin = n.Kelvin ?? _fixtureKelvin ?? DEFAULT_KELVIN,
                    Downward = orient.Aim.Z < -0.5f ? 0.8f : orient.Aim.Z > 0.5f ? 0.15f : 0.4f,
                    Estimated = estimated,
                    Ies = n.Hash,
                    Aim = orient.Aim,
                    Across = orient.Across,
                    Distribution = n.Hash == null ? n.Shape : null // Round Y-C1
                });
                if (estimated) { _estimatedLights++; }
                if (n.Hash != null) { _iesLights++; }
                else if (n.Shape != null) { _shapeLights++; }
                _nestedLights++;
            }
        }

        // Made on first use (Revit API objects: not in a static initialiser)
        private Transform[] _mirrors;

        private static readonly string[] MIRROR_NAMES = { "as placed", "mirrored across the family's YZ plane", "mirrored across the family's XZ plane" };
        private readonly int[] _mirrorChoices = new int[4];
        private int _mirrorLogged, _flippedHosts;

        /// <summary>
        /// X-D1 r7: a nested light source's placement under the host's transform, as is or mirrored across the family's
        /// X / Y planes, whichever lands its origin inside (or nearest) the fixture's extracted bounds; as is on a tie.
        /// Mirrored instances report no mirror in their transforms, but their drawn geometry is mirrored.
        /// </summary>
        private Transform PlaceInsideDrawn(Transform host, Transform local, Aabb bounds, int elementIndex)
        {
            if (!bounds.IsValid) { return host.Multiply(local); }
            _mirrors ??= new[]
            {
                Transform.Identity,
                Transform.CreateReflection(DB.Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero)),
                Transform.CreateReflection(DB.Plane.CreateByNormalAndOrigin(XYZ.BasisY, XYZ.Zero))
            };
            // r8: the instance's own flips first (hand = across the family's YZ plane, facing = across its XZ plane; both
            // together a half turn): Revit keeps them out of the transform. Then the alternatives, should a transform
            // already carry them; the one landing inside (or nearest) the drawn fixture wins, the flips on a tie
            Transform flips = Transform.Identity;
            if (_fixtureHandFlipped) { flips = flips.Multiply(_mirrors[1]); }
            if (_fixtureFacingFlipped) { flips = flips.Multiply(_mirrors[2]); }
            Transform best = host.Multiply(flips).Multiply(local);
            int bestIndex = 3;
            float bestDistance = OutsideBy(ToScenePoint(best.Origin), bounds);
            for (int i = 0; i < _mirrors.Length; i++)
            {
                Transform t = host.Multiply(_mirrors[i]).Multiply(local);
                float distance = OutsideBy(ToScenePoint(t.Origin), bounds);
                if (distance < bestDistance - 0.005f)
                {
                    best = t;
                    bestIndex = i;
                    bestDistance = distance;
                }
            }
            _mirrorChoices[bestIndex]++;
            if (_fixtureHandFlipped || _fixtureFacingFlipped) { _flippedHosts++; }
            if (bestIndex != 3 && _mirrorLogged++ < 3)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: fixture {elementIndex} (hand flipped {_fixtureHandFlipped}, facing flipped {_fixtureFacingFlipped}, mirrored {_fixtureMirrored}): " +
                    $"its nested light source lands inside the fixture {MIRROR_NAMES[bestIndex]}, not with its flips ({bestDistance * 100f:0} cm outside).");
            }
            return best;
        }

        /// <summary>How far a point is outside a box (0 inside).</summary>
        private static float OutsideBy(Vector3 p, Aabb box)
        {
            Vector3 d = Vector3.Max(Vector3.Max(box.Min - p, p - box.Max), Vector3.Zero);
            return d.Length();
        }

        /// <summary>
        /// A fixture type's photometric web file (hash into <see cref="_iesFiles"/>, or null) and tilt (radians). Round
        /// Y-C1: a spot light's beam / field angles (and tilt) or a hemispherical light as a generated profile key
        /// (<c>Shape</c>; spherical lights stay on BimGo's lobe).
        /// </summary>
        private (string Hash, double Tilt, float Lumens, string Shape) ReadTypeIes(Document doc, Element element, Element type, string label)
        {
            string file = null;
            double tilt = double.NaN;
            string shape = null;
            try
            {
                Autodesk.Revit.DB.Lighting.LightType light = Autodesk.Revit.DB.Lighting.LightType.GetLightTypeFromInstance(doc, element.Id);
                Autodesk.Revit.DB.Lighting.LightDistribution distribution = light?.GetLightDistribution();
                if (distribution is Autodesk.Revit.DB.Lighting.PhotometricWebLightDistribution web)
                {
                    file = web.PhotometricWebFile;
                    tilt = web.TiltAngle;
                }
                else if (distribution is Autodesk.Revit.DB.Lighting.SpotLightDistribution spot)
                {
                    // Round Y-C1: Revit's angles are radians, full cone angles; the tilt as a web's (90° = down)
                    tilt = spot.TiltAngle;
                    shape = LightProfiles.SpotKey(spot.SpotBeamAngle * 180.0 / Math.PI, spot.SpotFieldAngle * 180.0 / Math.PI);
                    Utilities.Log_Utils.Write($"{_src.Describe()}: “{label}” is a spot light: beam {spot.SpotBeamAngle * 180.0 / Math.PI:0.#}°, field " +
                        $"{spot.SpotFieldAngle * 180.0 / Math.PI:0.#}°, tilt {tilt * 180.0 / Math.PI:0.#}° (90° = down) → {shape ?? "lobe (angles unusable)"}.");
                }
                else if (distribution is Autodesk.Revit.DB.Lighting.HemisphericalLightDistribution)
                {
                    shape = LightProfiles.HEMISPHERICAL;
                    Utilities.Log_Utils.Write($"{_src.Describe()}: “{label}” is a hemispherical light → {shape}.");
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: light type of “{label}” unreadable: {ex.Message}");
            }
            if (string.IsNullOrWhiteSpace(file))
            {
                // Older families: the type parameter
                Parameter parameter = type.LookupParameter("Photometric Web File") ?? element.LookupParameter("Photometric Web File");
                if (parameter != null && parameter.StorageType == StorageType.String) { file = parameter.AsString(); }
            }
            if (string.IsNullOrWhiteSpace(file)) { return (null, tilt, 0f, shape); }

            string path = FindIes(file.Trim());
            if (path == null)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: “{label}” names photometric file “{file}”, not found (its fixtures keep BimGo's lobe).");
                return (null, tilt, 0f, null);
            }
            try
            {
                if (new FileInfo(path).Length > LightProfile.MAX_IES_BYTES) { return (null, tilt, 0f, null); }
                string text = File.ReadAllText(path, System.Text.Encoding.Latin1);
                IesData data = IesData.Parse(text, out string error);
                if (data == null)
                {
                    Utilities.Log_Utils.Write($"{_src.Describe()}: photometric file {path} not usable: {error}");
                    return (null, tilt, 0f, null);
                }
                string hash = LightProfiles.HashOf(LightProfiles.KeyOf(text));
                if (!_iesFiles.ContainsKey(hash))
                {
                    _iesFiles[hash] = new LightIesFile(hash, Path.GetFileName(path), text);
                    Utilities.Log_Utils.Write($"{_src.Describe()}: photometric file {path} → ies/{hash}.ies.");
                }
                Utilities.Log_Utils.Write($"{_src.Describe()}: “{label}” uses {Path.GetFileName(path)}, tilt " +
                    (double.IsFinite(tilt) ? $"{tilt * 180.0 / Math.PI:0.#}° (90° = down)" : "unknown (aimed down the family's −Z)") + ".");
                return (hash, tilt, data.TotalLumens(), null);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: photometric file {path} unreadable: {ex.Message}");
                return (null, tilt, 0f, null);
            }
        }

        /// <summary>
        /// Where a photometric file named by a family is: the path itself when it exists, else a file of that name in the
        /// model's folder (and its sub-folders, 3 deep), Revit's library paths, or Revit's ProgramData folder. Cached by name.
        /// </summary>
        private string FindIes(string file)
        {
            try
            {
                if (Path.IsPathRooted(file) && File.Exists(file)) { return file; }
            }
            catch (ArgumentException)
            {
                return null;
            }
            string name = Path.GetFileName(file);
            if (string.IsNullOrEmpty(name)) { return null; }
            if (_iesPathOfName.TryGetValue(name, out string cached)) { return cached; }

            var roots = new List<string>();
            try
            {
                string model = _src.Doc.PathName;
                if (!string.IsNullOrEmpty(model) && Path.IsPathRooted(model)) { roots.Add(Path.GetDirectoryName(model)); }
                foreach (string library in _src.Doc.Application.GetLibraryPaths().Values) { roots.Add(library); }
                roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + _src.Doc.Application.VersionNumber));

                // r5: Revit's additional render appearance paths (Options → Rendering, where Revit also finds .ies files)
                // and BimGo's texture search folders, each with its parent (an "IES" folder beside "Maps")
                TextureLocator locator = _locator ?? TextureLocator.Discover(_src.Doc.Application.VersionNumber);
                foreach (string extra in locator.ExtraPaths.Concat(locator.SearchFolders))
                {
                    roots.Add(extra);
                    string parent = Path.GetDirectoryName(extra.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent) && Path.GetPathRoot(parent) != parent) { roots.Add(parent); }
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Photometric file search folders unreadable: {ex.Message}");
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            string result = null;
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { continue; }
                    result = Directory.EnumerateFiles(root, name, options).FirstOrDefault();
                    if (result != null) { break; }
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Photometric file search in {root} failed: {ex.Message}");
                }
            }
            if (result == null)
            {
                Utilities.Log_Utils.Write($"Photometric file {name}: searched {string.Join("; ", roots.Distinct(StringComparer.OrdinalIgnoreCase))} (3 folders deep).");
            }
            _iesPathOfName[name] = result;
            return result;
        }

        #endregion

        #region Fixture parameters

        private static readonly Regex NUMBER_UNIT = new(@"(?<number>[0-9][0-9.,   ']*)\s*(?<unit>lm/w|lm|cd|w|k|lx)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The fixture's output in lumens from its instance or type parameters (lm, cd or W [@ lm/W]), or null.
        /// </summary>
        private static float? ReadLumens(Element element, Element type)
        {
            string text = ParameterText(element, type, LUMEN_PARAMETERS);
            if (text == null) { return null; }

            MatchCollection matches = NUMBER_UNIT.Matches(text);
            if (matches.Count == 0 || !TryParseNumber(matches[0].Groups["number"].Value, out double value) || value <= 0.0) { return null; }
            string unit = matches[0].Groups["unit"].Value.ToLowerInvariant();

            double lumens = unit switch
            {
                "lm" => value,
                "cd" => value * 2.0 * Math.PI,       // a downward hemisphere
                "w" => value * Efficacy(matches),     // wattage × efficacy (Revit's "W @ lm/W")
                _ => double.NaN                       // illuminance or unknown: can't convert
            };
            if (double.IsNaN(lumens)) { return null; }
            return (float)Math.Clamp(lumens, 50.0, 50000.0);

            static double Efficacy(MatchCollection all)
            {
                foreach (Match m in all)
                {
                    if (string.Equals(m.Groups["unit"].Value, "lm/w", StringComparison.OrdinalIgnoreCase) && TryParseNumber(m.Groups["number"].Value, out double e) && e > 0.0)
                    {
                        return e;
                    }
                }
                return 15.0; // incandescent, Revit's default efficacy
            }
        }

        /// <summary>
        /// The fixture's colour temperature in kelvin from its instance or type parameters, or null.
        /// </summary>
        private static float? ReadKelvin(Element element, Element type)
        {
            string text = ParameterText(element, type, KELVIN_PARAMETERS);
            if (text == null) { return null; }
            foreach (Match m in NUMBER_UNIT.Matches(text))
            {
                if (!TryParseNumber(m.Groups["number"].Value, out double value)) { continue; }
                bool kelvinUnit = string.Equals(m.Groups["unit"].Value, "k", StringComparison.OrdinalIgnoreCase);
                if ((kelvinUnit || m.Groups["unit"].Value.Length == 0) && value >= 1000.0 && value <= 15000.0) { return (float)value; }
            }
            return null;
        }

        /// <summary>
        /// The display text of the first named parameter found on the instance, then the type.
        /// </summary>
        private static string ParameterText(Element element, Element type, string[] names)
        {
            foreach (string name in names)
            {
                Parameter parameter = element.LookupParameter(name) ?? type?.LookupParameter(name);
                if (parameter == null || !parameter.HasValue) { continue; }
                string text = parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
                if (!string.IsNullOrWhiteSpace(text)) { return text; }
            }
            return null;
        }

        /// <summary>
        /// Parses a displayed number in either "1,500.5" or "1.500,5" style (spaces / apostrophes as group separators).
        /// </summary>
        internal static bool TryParseNumber(string text, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(text)) { return false; }
            string s = text.Trim().Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("'", "").TrimEnd('.', ',');
            int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
            if (lastDot >= 0 && lastComma >= 0)
            {
                // Both: the later one is the decimal separator
                s = lastDot > lastComma ? s.Replace(",", "") : s.Replace(".", "").Replace(',', '.');
            }
            else if (lastComma >= 0)
            {
                // Only commas: "1,500" (groups of three) is thousands, "2,5" is a decimal
                bool grouped = s.Split(',').Skip(1).All(part => part.Length == 3);
                s = grouped ? s.Replace(",", "") : s.Replace(',', '.');
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        #endregion
    }
}
