using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round X-D1: light profiles (IES photometric files and BimGo's built-ins) per fixture type.
    /// <list type="bullet">
    /// <item>Each fixture type ("Family : Type") uses, unless changed: the .ies file its Revit family names (copied into the
    /// snapshot at extraction), else (Round Y-C1) a profile generated from its Revit spot (beam / field angles) or
    /// hemispherical distribution, else BimGo's own downward lobe as before.</item>
    /// <item>Round Y-C1: a file's TILT=INCLUDE factors scale the fixture's output by how far its aim is turned from
    /// straight down.</item>
    /// <item>Pause → LIGHTS lists the types: ‹ › step through Revit's file, the built-ins, the imported files and the lobe;
    /// IMPORT .IES… reads a file and assigns it; RESET goes back to the default. Round Y-C2: each row draws its
    /// profile's polar plot, counts the fixtures set individually and RESET ALL clears them.</item>
    /// <item>Round Y-C2: a single fixture can have its own choice (aim, Ctrl+K: the fixture dock),
    /// which wins over its type's; clones follow their original.</item>
    /// <item>Choices and imported files are kept in <c>light-profiles.json</c> (the model folder, or inside a .bimgo),
    /// like the entourage rules, so the next Go / F5 / Export keeps them.</item>
    /// <item>The renderer holds up to <see cref="LightProfiles.MAX_GPU_PROFILES"/> different profiles at once; past that,
    /// the remaining types fall back to the lobe (said in the tab).</item>
    /// <item>A profile only changes where a fixture's light goes, never how much (Revit's lumens; a fixture whose lumens
    /// were guessed takes an imported file's own output).</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private LightProfileDocument _lightProfileDoc = new();
        private string _lightProfilePath; // null = inside the .bimgo
        private int _lightProfileRevision;

        // Parsed profiles by key (null = unusable, logged once)
        private readonly Dictionary<string, LightProfile> _profileCache = new(StringComparer.Ordinal);

        // Per light: its renderer slot (-1 = lobe) and code at its extracted orientation
        private int[] _lightSlot = Array.Empty<int>();
        private float[] _lightCode = Array.Empty<float>();
        private readonly List<string> _slotKeys = new();
        private bool _profileOverflow;

        // The tab's rows (made once) and its last message
        private List<LightTypeRow> _lightTypeRows;
        private string _lightNotice;
        private string _lightingFixtureType;
        private const int DROP_LIGHTING_FIXTURE=1202;

        // Round Y-C2: each light's fixture key (made once; null = no identity), the fixtures per type with a choice of
        // their own (counted on every change), and the polar plots by profile key (made once each)
        private string[] _lightInstanceKey;
        private readonly Dictionary<string, int> _fixtureOverrides = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LightProfilePolar> _polarCache = new(StringComparer.Ordinal);

        /// <summary>A fixture type in the LIGHTS tab.</summary>
        /// <param name="RevitShape">Round Y-C1: its Revit spot / hemispherical distribution's generated key (no file), or null.</param>
        private sealed record LightTypeRow(string FamilyType, int Count, string RevitHash, string RevitName, string RevitShape)
        {
            /// <summary>Revit gives this type a profile of its own (a file or a spot / hemispherical distribution).</summary>
            public bool HasRevit => RevitHash != null || RevitShape != null;

            /// <summary>The profile key "Revit" means for this type (its file, else its distribution).</summary>
            public string RevitKey => RevitHash != null ? LightProfiles.FILE_PREFIX + RevitHash : RevitShape;
        }

        #endregion

        #region Setup and saving

        /// <summary>
        /// Loads the choices (inside a .bimgo, else the model folder's light-profiles.json) and fills the renderer's tables.
        /// </summary>
        private void InitialiseLightProfiles()
        {
            if (IsFileMode)
            {
                _lightProfilePath = null;
                _lightProfileDoc = _options.Document?.LightProfiles ?? new LightProfileDocument();
            }
            else
            {
                _lightProfilePath = LightProfileFiles.SidecarFor(Scene.CommentsPath);
                _lightProfileDoc = (_lightProfilePath == null ? null : LightProfileFiles.Read(_lightProfilePath, out _)) ?? new LightProfileDocument();
            }
            _lightProfileDoc.Clean();
            ApplyLightProfiles();
            if (Scene.Lighting.Lights.Length > 0)
            {
                Utilities.Log_Utils.Write($"Light profiles: {_slotKeys.Count} in use, {Scene.Lighting.IesFiles.Length} .ies files from Revit, " +
                    $"{_lightProfileDoc.Imports.Count} imported, {_lightProfileDoc.Types.Count} type choices.");
            }
        }

        /// <summary>The choices for saving into a .bimgo (null when there are none).</summary>
        private LightProfileDocument LightProfilesForSave()
        {
            if (_lightProfileDoc.IsEmpty) { return null; }
            _lightProfileDoc.Model = Scene.ModelTitle;
            return _lightProfileDoc;
        }

        /// <summary>Records a change: re-fills the tables, writes the model folder's file (or marks the .bimgo changed).</summary>
        private void SaveLightProfiles()
        {
            _lightProfileRevision++;
            ApplyLightProfiles();
            if (_lightProfilePath != null)
            {
                _lightProfileDoc.Model = Scene.ModelTitle;
                if (!LightProfileFiles.Write(_lightProfilePath, _lightProfileDoc, out string error)) { _lightNotice = error; }
            }
            UpdateTitle();
        }

        #endregion

        #region Resolving

        /// <summary>
        /// The profile key a light uses (Round Y-C2): its fixture's own choice, else its type's, else its Revit file or
        /// (Y-C1) spot / hemispherical distribution; null = the lobe.
        /// </summary>
        private string ProfileKeyOf(int index)
        {
            LightSource light = Scene.Lighting.Lights[index];
            string revit = light.Ies != null ? LightProfiles.FILE_PREFIX + light.Ies : light.Distribution;
            string instance = _lightInstanceKey != null && index < _lightInstanceKey.Length ? _lightInstanceKey[index] : null;
            return LightProfileDocument.KeyFor(_lightProfileDoc.ResolveChoice(instance, Scene.Elements[light.Element].FamilyType), revit);
        }

        /// <summary>Round Y-C2: the fixture key of every light (made once: element identities don't change in a session).</summary>
        private void EnsureLightInstanceKeys()
        {
            LightSource[] lights = Scene.Lighting.Lights;
            if (_lightInstanceKey != null && _lightInstanceKey.Length == lights.Length) { return; }
            _lightInstanceKey = new string[lights.Length];
            int lastElement = -1;
            string lastKey = null;
            for (int i = 0; i < lights.Length; i++)
            {
                int e = lights[i].Element;
                if (e != lastElement) { lastKey = LightProfileDocument.InstanceKeyOf(Scene, e); } // nested sources: consecutive lights
                lastElement = e;
                _lightInstanceKey[i] = lastKey;
            }
        }

        /// <summary>
        /// A profile by key (built-in, Revit's file or an imported one), parsed once; null if unknown or unusable.
        /// </summary>
        private LightProfile ProfileOf(string key)
        {
            if (key == null) { return null; }
            if (_profileCache.TryGetValue(key, out LightProfile cached)) { return cached; }
            LightProfile profile = LightProfiles.FindBuiltIn(key) ?? LightProfiles.FromGeneratedKey(key); // Y-C1: spot / hemisphere
            string hash = LightProfiles.HashOf(key);
            if (profile == null && hash != null)
            {
                string name = null, text = null;
                LightIesFile revit = Scene.Lighting.FindIes(hash);
                if (revit != null) { name = revit.Name; text = revit.Text; }
                else
                {
                    LightProfileImport import = _lightProfileDoc.FindImport(hash);
                    if (import != null) { name = import.Name; text = import.Text; }
                }
                if (text != null)
                {
                    profile = LightProfile.FromIes(key, name, text, out string error);
                    if (profile == null) { Utilities.Log_Utils.Write($"Light profile {name} unusable: {error}"); }
                    else if (profile.TiltAngles != null)
                    {
                        Utilities.Log_Utils.Write($"Light profile {name}: TILT=INCLUDE, {profile.TiltAngles.Length} angles " +
                            $"({profile.TiltAngles[0]:0.#}–{profile.TiltAngles[^1]:0.#}°): output × {profile.TiltFactor(0f):0.###} straight down."); // Y-C1
                    }
                }
            }
            _profileCache[key] = profile;
            return profile;
        }

        /// <summary>
        /// Works out every light's profile slot and code, fills the renderer's tables and (for guessed outputs with an
        /// imported file) the light's output. Called at start and on every choice change.
        /// </summary>
        private void ApplyLightProfiles()
        {
            LightSource[] lights = Scene.Lighting.Lights;
            if (_lightSlot.Length != lights.Length)
            {
                _lightSlot = new int[lights.Length];
                _lightCode = new float[lights.Length];
            }
            _slotKeys.Clear();
            _profileOverflow = false;
            EnsureLightInstanceKeys();
            CountFixtureOverrides();
            ArtificialLighting a = _renderer?.Artificial;
            for (int i = 0; i < lights.Length; i++)
            {
                LightSource light = lights[i];
                string key = ProfileKeyOf(i);
                LightProfile profile = ProfileOf(key);
                int slot = -1;
                if (profile != null)
                {
                    slot = _slotKeys.IndexOf(key);
                    if (slot < 0)
                    {
                        if (_slotKeys.Count < LightProfiles.MAX_GPU_PROFILES)
                        {
                            slot = _slotKeys.Count;
                            _slotKeys.Add(key);
                            a?.SetProfile(slot, profile);
                        }
                        else { _profileOverflow = true; }
                    }
                }
                _lightSlot[i] = slot;
                _lightCode[i] = slot < 0 ? light.Downward : LightProfiles.Encode(slot, light.Aim, light.Across);

                // Output: Revit's lumens; a guessed output takes an imported / Revit file's own (when it has one)
                float lumens = light.Lumens;
                if (profile != null && profile.IsFile && light.Estimated && MathF.Abs(light.Lumens - 1000f) < 0.5f && profile.Lumens >= 10f)
                {
                    lumens = Math.Clamp(profile.Lumens, 50f, 50000f);
                }

                // Round Y-C1: the file's tilt factor at the fixture's tilt (its aim's angle from straight down)
                if (profile?.TiltAngles != null) { lumens *= profile.TiltFactor(TiltFromDown(light.Aim)); }
                if (i < _lightColour.Length)
                {
                    _lightColour[i] = LightingData.KelvinToRgb(light.Kelvin) * (lumens * LIGHT_SCALE);
                    _lightRadius[i] = LightReach(lumens);
                    _lightLumens[i] = lumens; // Y-C1: ranks the live lights
                }
            }
        }

        /// <summary>Round Y-C2: per type, how many of this snapshot's fixtures have a choice of their own.</summary>
        private void CountFixtureOverrides()
        {
            _fixtureOverrides.Clear();
            if (_lightProfileDoc.Instances == null || _lightProfileDoc.Instances.Count == 0) { return; }
            LightSource[] lights = Scene.Lighting.Lights;
            int lastElement = -1;
            for (int i = 0; i < lights.Length; i++)
            {
                int e = lights[i].Element;
                if (e == lastElement) { continue; } // a fixture, not its sources
                lastElement = e;
                if (_lightProfileDoc.InstanceChoiceFor(_lightInstanceKey[i]) == null) { continue; }
                string type = Scene.Elements[e].FamilyType ?? string.Empty;
                _fixtureOverrides[type] = _fixtureOverrides.TryGetValue(type, out int n) ? n + 1 : 1;
            }
        }

        /// <summary>Round Y-C2: the fixtures of a type set individually (in this snapshot).</summary>
        private int FixtureOverrides(string familyType) => _fixtureOverrides.TryGetValue(familyType ?? string.Empty, out int n) ? n : 0;

        /// <summary>Round Y-C1: how far an aim is turned from straight down (degrees, 0–180).</summary>
        private static float TiltFromDown(Vector3 aim) =>
            aim.LengthSquared() > 1e-10f ? MathF.Acos(Math.Clamp(-Vector3.Normalize(aim).Z, -1f, 1f)) * 180f / MathF.PI : 0f;

        /// <summary>X-D1: a light's code at its extracted orientation (its downward share when it has no profile).</summary>
        private float LightCodeOf(int light) => light < _lightCode.Length ? _lightCode[light] : Scene.Lighting.Lights[light].Downward;

        /// <summary>X-D1: a light's code turned with a moved or cloned fixture.</summary>
        private float LightCodeOf(int light, in Matrix4x4 model)
        {
            if (light >= _lightSlot.Length || _lightSlot[light] < 0) { return LightCodeOf(light); }
            LightSource source = Scene.Lighting.Lights[light];
            return LightProfiles.Encode(_lightSlot[light], Vector3.TransformNormal(source.Aim, model), Vector3.TransformNormal(source.Across, model));
        }

        #endregion

        #region LIGHTS tab

        /// <summary>The fixture types with lights, by name (made once).</summary>
        private List<LightTypeRow> LightTypeRows()
        {
            if (_lightTypeRows != null) { return _lightTypeRows; }
            var counts = new Dictionary<string, (int Count, string Hash, string Shape)>(StringComparer.Ordinal);
            int lastElement = -1;
            foreach (LightSource light in Scene.Lighting.Lights)
            {
                string type = Scene.Elements[light.Element].FamilyType ?? string.Empty;
                counts.TryGetValue(type, out (int Count, string Hash, string Shape) entry);
                // Fixtures, not light sources (X-D1 r3: a fixture's nested sources are consecutive lights)
                counts[type] = (entry.Count + (light.Element == lastElement ? 0 : 1), entry.Hash ?? light.Ies, entry.Shape ?? light.Distribution);
                lastElement = light.Element;
            }
            _lightTypeRows = counts
                .OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new LightTypeRow(p.Key, p.Value.Count, p.Value.Hash, Scene.Lighting.FindIes(p.Value.Hash)?.Name, p.Value.Hash == null ? p.Value.Shape : null))
                .ToList();
            return _lightTypeRows;
        }

        /// <summary>
        /// The choices a type steps through: Revit's file (when it has one), the built-ins, the imported files, the lobe.
        /// </summary>
        private List<string> ProfileOptionsFor(LightTypeRow row)
        {
            var options = new List<string>();
            if (row.HasRevit) { options.Add(LightProfiles.REVIT); }
            foreach (LightProfile p in LightProfiles.BuiltIns) { options.Add(p.Key); }
            foreach (LightProfileImport import in _lightProfileDoc.Imports)
            {
                string key = LightProfiles.FILE_PREFIX + import.Hash;
                if (import.Hash != row.RevitHash) { options.Add(key); }
            }
            options.Add(LightProfiles.LOBE);
            return options;
        }

        /// <summary>The choice a type is on now (its default when none was made).</summary>
        private string CurrentChoice(LightTypeRow row) =>
            _lightProfileDoc.ChoiceFor(row.FamilyType) ?? (row.HasRevit ? LightProfiles.REVIT : LightProfiles.LOBE);

        /// <summary>Appends what a choice is to <see cref="Text"/>.</summary>
        private void AppendChoiceLabel(LightTypeRow row, string choice)
        {
            if (choice == LightProfiles.LOBE) { Text.Append("BimGo lobe (no profile)"); return; }
            string key = choice == LightProfiles.REVIT ? row.RevitKey : choice;
            LightProfile profile = ProfileOf(key);
            if (choice == LightProfiles.REVIT && row.RevitHash == null && profile != null) { Text.Append(profile.Name); } // Y-C1: "Revit spot (…)"
            else if (choice == LightProfiles.REVIT) { Text.Append("Revit: ").Append(row.RevitName ?? "its .ies"); }
            else if (profile == null) { Text.Append("Missing profile"); return; }
            else if (profile.IsFile) { Text.Append("Imported: ").Append(profile.Name); }
            else { Text.Append(profile.Name); }
            if (profile == null) { Text.Append(" (unusable)"); return; }
            if (profile.IsFile && profile.BeamAngle > 0f && profile.BeamAngle < 180f) { Text.Append(" · ").Append(profile.BeamAngle, 0).Append("° beam"); }
            if (profile.TiltAngles != null) { Text.Append(" · tilt data"); } // Y-C1
        }

        /// <summary>
        /// Pause → LIGHTS: each fixture type with its count, its profile, and ‹ › · IMPORT .IES… · RESET.
        /// </summary>
        /// <returns>The bottom of the content.</returns>
        private float BuildLightsTab(FontAtlas f, InputState input, float ix, float y, float iw)
        {
            List<LightTypeRow> rows = LightTypeRows();
            if (rows.Count == 0)
            {
                y += _ui.TextWrapped(f.Small, ix, y, iw,
                    "No lighting fixtures in this model. Light profiles (IES files and built-ins) apply to Lighting Fixtures elements.",
                    UiTheme.TEXT_FAINT, maxLines: 4);
                return y;
            }

            Text.Clear().AppendGrouped(rows.Count).Append(rows.Count == 1 ? " fixture type · " : " fixture types · ")
                .AppendGrouped(Scene.Lighting.IesFiles.Length).Append(Scene.Lighting.IesFiles.Length == 1 ? " .ies file from Revit" : " .ies files from Revit");
            _ui.TextWrapped(f.Small, ix, y, iw, Text.Span, UiTheme.TEXT_MUTED, maxLines: 2);
            y += S(20);
            string notice = _lightNotice ?? (_profileOverflow ? $"Over {LightProfiles.MAX_GPU_PROFILES} different profiles in use: the rest use the lobe" : null)
                ?? (_lightMode != ArtificialLightMode.Lights ? "Profiles show with Lights → Glow + light (sun panel, or K)" : null);
            if (notice != null)
            {
                y += _ui.TextWrapped(f.Small, ix, y, iw, notice, UiTheme.MEASURE_LABEL, maxLines: 3) + S(6);
            }
            y += S(6);

            int selected=Math.Max(0,rows.FindIndex(r=>r.FamilyType==_lightingFixtureType));
            _lightingFixtureType=rows[selected].FamilyType;
            if(DropdownButton(f,input,ix,y,iw,S(32),_lightingFixtureType,IsDropdownOpen(this,DROP_LIGHTING_FIXTURE)))
                OpenDropdown(this,DROP_LIGHTING_FIXTURE,ix,y,iw,S(32),rows.Select(r=>$"{r.FamilyType} (×{r.Count})").ToArray(),selected,i=>_lightingFixtureType=rows[i].FamilyType);
            y+=S(44);

            float bw = S(28), gap = S(6);
            float plot = S(58), rw = iw - plot - S(12); // Y-C2: the row's polar plot on the right
            foreach (LightTypeRow row in rows.Where(r=>r.FamilyType==_lightingFixtureType))
            {
                _ui.TextWrapped(f.Body, ix, y, iw - S(56), row.FamilyType.Length == 0 ? "(no type)" : row.FamilyType, UiTheme.TEXT, maxLines: 1);
                Text.Clear().Append('×').AppendGrouped(row.Count);
                _ui.TextRight(f.Small, ix + iw, y + S(2), Text.Span, UiTheme.TEXT_MUTED);
                y += S(20);

                string current = CurrentChoice(row);
                bool set = _lightProfileDoc.ChoiceFor(row.FamilyType) != null;
                DrawPolarPlot(LightProfileDocument.KeyFor(current, row.RevitKey), ix + iw - plot, y + S(2), plot, small: true);
                Text.Clear();
                AppendChoiceLabel(row, current);
                if (set) { Text.Append(" (set)"); }
                _ui.TextWrapped(f.Small, ix, y, rw, Text.Span, set ? UiTheme.PLACE_LABEL : UiTheme.TEXT_MUTED, maxLines: 1);
                y += S(20);

                float importW = rw - 2f * (bw + gap) - S(64) - gap;
                if (SmallButton(f, input, ix, y, bw, S(26), "‹")) { StepProfile(row, -1); }
                if (SmallButton(f, input, ix + bw + gap, y, bw, S(26), "›")) { StepProfile(row, +1); }
                if (SmallButton(f, input, ix + 2f * (bw + gap), y, importW, S(26), "IES…")) { ImportIes(row); }
                if (set && SmallButton(f, input, ix + rw - S(64), y, S(64), S(26), "RESET"))
                {
                    _lightProfileDoc.SetChoice(row.FamilyType, null);
                    _lightNotice = null;
                    SaveLightProfiles();
                }
                y += S(32);

                // Round Y-C2: the fixtures of this type with a profile of their own
                int own = FixtureOverrides(row.FamilyType);
                if (own > 0)
                {
                    Text.Clear().Append("· ").AppendGrouped(own).Append(own == 1 ? " fixture set individually" : " fixtures set individually");
                    _ui.TextWrapped(f.Small, ix, y + S(5), rw - S(96) - gap, Text.Span, UiTheme.PLACE_LABEL, maxLines: 1);
                    if (SmallButton(f, input, ix + rw - S(96), y, S(96), S(24), "RESET ALL"))
                    {
                        int cleared = _lightProfileDoc.ClearInstances(row.FamilyType);
                        _lightNotice = $"“{row.FamilyType}”: {cleared} {(cleared == 1 ? "fixture follows" : "fixtures follow")} the type again";
                        Sound.Play(SoundId.UiClick);
                        SaveLightProfiles();
                    }
                    y += S(30);
                }
                y += S(8);
            }
            y += _ui.TextWrapped(f.Small, ix, y, iw,
                $"Aim at a fixture and press {Key(InputAction.FixtureProfile)} to give just that one its own profile. The plots: C 0–180 in blue, C 90–270 in grey, straight down at the bottom, normalised to the peak.",
                UiTheme.TEXT_FAINT, maxLines: 4) + S(6);
            y += _ui.TextWrapped(f.Small, ix, y, iw,
                "An IES file's C 0° runs along the family's X axis; a wall washer's C 0° should face the wall. Photos and videos light every fixture in view.",
                UiTheme.TEXT_FAINT, maxLines: 4);
            return y;
        }

        /// <summary>‹ / ›: the type's previous / next choice.</summary>
        private void StepProfile(LightTypeRow row, int direction)
        {
            List<string> options = ProfileOptionsFor(row);
            int at = options.IndexOf(CurrentChoice(row));
            int next = ((at < 0 ? 0 : at + direction) % options.Count + options.Count) % options.Count;
            string choice = options[next];
            string fallback = row.HasRevit ? LightProfiles.REVIT : LightProfiles.LOBE;
            _lightProfileDoc.SetChoice(row.FamilyType, choice == fallback ? null : choice);
            _lightNotice = null;
            SaveLightProfiles();
        }

        /// <summary>
        /// IMPORT .IES…: reads a photometric file, keeps it (once, by content) and assigns it to the type, or (Round
        /// Y-C2, <paramref name="instanceKey"/> given) to that one fixture.
        /// </summary>
        private void ImportIes(LightTypeRow row, string instanceKey = null)
        {
            _window.Input.ReleaseAll();
            string title = instanceKey == null ? $"Photometric file (IES) for “{row.FamilyType}”" : $"Photometric file (IES) for this “{row.FamilyType}”";
            string path = FileDialogs.ShowOpen(_window.Handle, title, "IES photometric files (*.ies)|*.ies|All files (*.*)|*.*", null);
            _window.Input.ReleaseAll();
            if (path == null) { return; }

            string text;
            try
            {
                if (new FileInfo(path).Length > LightProfile.MAX_IES_BYTES) { Fail("That file is too big for an .ies file"); return; }
                text = File.ReadAllText(path, System.Text.Encoding.Latin1);
            }
            catch (Exception ex)
            {
                Fail($"The file couldn't be read: {ex.Message}");
                return;
            }
            string key = LightProfiles.KeyOf(text), hash = LightProfiles.HashOf(key), name = Path.GetFileName(path);
            if (LightProfile.FromIes(key, name, text, out string error) == null)
            {
                Fail($"{name}: {error}");
                return;
            }
            if (Scene.Lighting.FindIes(hash) == null && _lightProfileDoc.FindImport(hash) == null)
            {
                if (_lightProfileDoc.Imports.Count >= LightProfileDocument.MAX_IMPORTS)
                {
                    Fail($"Already {LightProfileDocument.MAX_IMPORTS} imported files: reset some types first");
                    return;
                }
                _lightProfileDoc.Imports.Add(new LightProfileImport { Hash = hash, Name = name, Text = text });
            }
            _profileCache.Remove(key);
            _polarCache.Remove(key);
            if (instanceKey != null)
            {
                string choice = hash == row.RevitHash ? LightProfiles.REVIT : key;
                _lightProfileDoc.SetInstanceChoice(instanceKey, row.FamilyType, choice == CurrentChoice(row) ? null : choice);
                _lightNotice = $"This fixture: {name}";
            }
            else
            {
                _lightProfileDoc.SetChoice(row.FamilyType, hash == row.RevitHash ? null : key);
                _lightNotice = $"Every “{row.FamilyType}”: {name}";
            }
            Sound.Play(SoundId.UiClick);
            SaveLightProfiles();

            void Fail(string message)
            {
                _lightNotice = message;
                Sound.Play(SoundId.Error);
            }
        }

        #endregion

        #region Polar plot (Round Y-C2)

        // A unit circle (rings), made once
        private const int RING_SEGMENTS = 48;
        private static readonly Vector2[] UNIT_RING = BuildUnitRing();

        private static Vector2[] BuildUnitRing()
        {
            var ring = new Vector2[RING_SEGMENTS + 1];
            for (int i = 0; i <= RING_SEGMENTS; i++)
            {
                float a = i * 2f * MathF.PI / RING_SEGMENTS;
                ring[i] = new Vector2(MathF.Cos(a), MathF.Sin(a));
            }
            return ring;
        }

        /// <summary>A profile's polar plot by key, made once (null: the lobe or an unusable profile).</summary>
        private LightProfilePolar PolarOf(string key)
        {
            if (key == null) { return null; }
            if (_polarCache.TryGetValue(key, out LightProfilePolar cached)) { return cached; }
            LightProfilePolar plot = LightProfilePolar.Build(ProfileOf(key));
            _polarCache[key] = plot;
            return plot;
        }

        /// <summary>
        /// Draws a profile's polar candela plot in a square: rings at 25 / 50 / 75 / 100 % of the peak (small: 50 and
        /// 100), the cross, then C 0–180 (accent) over C 90–270 (muted); gamma 0 (straight down) at the bottom. The lobe
        /// (no profile) gets a note instead.
        /// </summary>
        private void DrawPolarPlot(string key, float x, float y, float size, bool small)
        {
            float r = size * 0.5f - S(2), cx = x + size * 0.5f, cy = y + size * 0.5f;
            LightProfilePolar plot = PolarOf(key);
            float thin = MathF.Max(1f, UiScale);
            for (int ring = small ? 2 : 1; ring <= 4; ring += small ? 2 : 1)
            {
                Polyline(UNIT_RING, cx, cy, r * ring * 0.25f, thin, ring == 4 ? UiTheme.PANEL_BORDER : UiTheme.DIVIDER);
            }
            _ui.Line(cx - r, cy, cx + r, cy, thin, UiTheme.DIVIDER);
            _ui.Line(cx, cy - r, cx, cy + r, thin, UiTheme.DIVIDER);
            if (plot == null)
            {
                _ui.TextCentred(_ui.Atlas.Small, cx, cy - _ui.Atlas.Small.LineHeight * 0.5f, key == null ? "lobe" : "n/a", UiTheme.TEXT_FAINT);
                return;
            }
            float width = small ? MathF.Max(1f, S(1.25f)) : S(2f);
            Polyline(plot.C90, cx, cy, r, width, UiTheme.TEXT_MUTED);
            Polyline(plot.C0, cx, cy, r, width, UiTheme.ACCENT);
        }

        /// <summary>Joins points (plot space) scaled by <paramref name="r"/> about (cx, cy).</summary>
        private void Polyline(Vector2[] points, float cx, float cy, float r, float width, uint colour)
        {
            for (int i = 1; i < points.Length; i++)
            {
                Vector2 a = points[i - 1], b = points[i];
                _ui.Line(cx + a.X * r, cy + a.Y * r, cx + b.X * r, cy + b.Y * r, width, colour);
            }
        }

        #endregion
    }
}
