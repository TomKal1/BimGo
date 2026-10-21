using System.Numerics;
using BimGo.Audio;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Artificial lights: the mode (off / glow / glow + light), the brightness, and each frame's choice of the
    /// lights the shaders get.
    /// <list type="bullet">
    /// <item>Every lighting fixture has one light (<see cref="LightingData"/>) with a cached shadow map
    /// (<see cref="LightShadows"/>), so light goes through doorways and stops at walls.</item>
    /// <item>Each frame the <see cref="ArtificialLighting.MAX_LIGHTS"/> lights whose sphere touches the view and that add
    /// the most light where you stand are used (Round Y-C1: ranked by output ÷ distance², distance at least 1 m; the
    /// lights used last frame count <see cref="RANK_KEEP"/> times more, so they don't swap shadow maps back and forth as
    /// you walk). When there are more, the weakest of them fade out so lights don't pop.</item>
    /// <item>Lights follow moved fixtures, clones bring their own, and hidden / demolished fixtures go dark.</item>
    /// <item>Daylight: with the sun up, fixtures matter less next to it (light × 0.35, glow × 0.6 at full day).</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>Candela per lumen of an omnidirectional source (1 / 4π), times lux → shading units (100 lx ≈ full albedo: night-adapted eyes).</summary>
        private const float LIGHT_SCALE = 1f / (4f * MathF.PI) / 100f;

        private static readonly string[] LIGHT_MODE_OPTIONS = { "Off", "Glow", "Glow + light" };
        private const int SLIDER_LIGHTS = 15, SLIDER_BLOOM = 16;

        private ArtificialLightMode _lightMode = ArtificialLightMode.Lights;
        private float _lightIntensity = 1f, _bloomIntensity = 1f;
        private bool _bloomFailed;

        // Per light, computed once: colour × output, reach, output (lm, Y-C1: ranks the live lights)
        private Vector3[] _lightColour = Array.Empty<Vector3>();
        private float[] _lightRadius = Array.Empty<float>();
        private float[] _lightLumens = Array.Empty<float>();

        /// <summary>
        /// Round Y-C1 r2: how far a light reaches (m; its light fades to nothing there, and it only counts for the view
        /// when that sphere touches it): 0.22 × √lumens, 3–12 m (was 0.16 × √lm, 2.5–9 m: lights came on too close to
        /// the player). 1000 lm ≈ 7 m, 3000 lm = 12 m.
        /// </summary>
        private static float LightReach(float lumens) => Math.Clamp(MathF.Sqrt(MathF.Max(lumens, 0f)) * 0.22f, 3f, 12f);

        /// <summary>Round Y-C1: a light used last frame ranks this many times higher (≈ 22 % closer), so the choice is steady.</summary>
        private const float RANK_KEEP = 1.5f;

        // Round Y-C1: the lights used last frame (their keys), and the scratch set for this frame's
        private HashSet<long> _rankKept = new(), _rankKeptNext = new();
        private readonly Dictionary<int, int> _lightOfElement = new();

        // Per frame (reused): the chosen lights, strongest first (Y-C1: _pickScore, output ÷ distance²), with their identity
        // (light index + instance id)
        private readonly int[] _pickLight = new int[ArtificialLighting.MAX_LIGHTS];
        private readonly float[] _pickScore = new float[ArtificialLighting.MAX_LIGHTS];
        private readonly Vector3[] _pickPosition = new Vector3[ArtificialLighting.MAX_LIGHTS];
        private readonly long[] _pickKey = new long[ArtificialLighting.MAX_LIGHTS];
        private readonly float[] _pickCode = new float[ArtificialLighting.MAX_LIGHTS]; // X-D1: downward share or profile code
        private readonly Vector4[] _viewPlanes = new Vector4[6];
        private int _pickCount, _candidateCount;

        // Round X-C1: all fixtures (photos, videos, stills; live with the setting): every light in view this frame, and
        // whether to collect them
        private readonly List<(int Light, Vector3 Position, float Score, long Key, float Code)> _allCandidates = new();
        private bool _collectAll;
        private static readonly Comparison<(int Light, Vector3 Position, float Score, long Key, float Code)> BY_SCORE = (a, b) => b.Score.CompareTo(a.Score); // Y-C1: strongest first

        /// <summary>Round X-C1: light every fixture in view live too (photos, videos and stills always do). Saved.</summary>
        private bool _allLightsLive;

        #endregion

        /// <summary>True if the model has lighting fixtures or glowing surfaces.</summary>
        private bool HasArtificialLighting => !Scene.Lighting.IsEmpty;

        #region Setup

        /// <summary>
        /// Reads the mode, brightness and bloom and prepares each light's colour and reach.
        /// </summary>
        private void InitialiseLights(LaunchSettings settings)
        {
            _lightMode = settings.ArtificialLights;
            _lightIntensity = settings.ArtificialLightIntensity;
            _allLightsLive = settings.AllLightsLive; // X-C1
            _bloomIntensity = settings.BloomIntensity;

            LightSource[] lights = Scene.Lighting.Lights;
            _lightColour = new Vector3[lights.Length];
            _lightRadius = new float[lights.Length];
            _lightLumens = new float[lights.Length];
            _lightOfElement.Clear();
            for (int i = 0; i < lights.Length; i++)
            {
                LightSource light = lights[i];
                _lightColour[i] = LightingData.KelvinToRgb(light.Kelvin) * (light.Lumens * LIGHT_SCALE);
                _lightRadius[i] = LightReach(light.Lumens);
                _lightLumens[i] = light.Lumens;
                _lightOfElement.TryAdd(light.Element, i);
            }
            if (lights.Length > 0 || Scene.Lighting.Emissive.Length > 0)
            {
                Utilities.Log_Utils.Write($"Artificial lights: {lights.Length} fixtures, mode {_lightMode}, brightness {_lightIntensity:0.00}, bloom {_bloomIntensity:0.00}.");
            }
        }

        #endregion

        #region Per frame

        /// <summary>Round Y-D2: the light quality setting's values (Options → Display).</summary>
        private const int LIGHT_QUALITY_LOW = 0, LIGHT_QUALITY_MEDIUM = 1, LIGHT_QUALITY_HIGH = 2;

        /// <summary>
        /// Round Y-D2: per quality, how many of the picked lights (strongest first) get shadow maps, and how many of those
        /// get 4-tap soft shadows (the rest 1 tap). Low 8 / 0, Medium 16 / 8, High 32 / 32. Picks past the shadowed ones
        /// light unshadowed.
        /// </summary>
        private static void LightQualityLimits(int quality, out int shadowed, out int softTaps)
        {
            switch (quality)
            {
                case LIGHT_QUALITY_LOW: shadowed = 8; softTaps = 0; break;
                case LIGHT_QUALITY_MEDIUM: shadowed = 16; softTaps = 8; break;
                default: shadowed = ArtificialLighting.MAX_LIGHTS; softTaps = ArtificialLighting.MAX_LIGHTS; break;
            }
        }

        /// <summary>
        /// Fills the renderer's <see cref="ArtificialLighting"/> for this frame (call after the sun lighting is set).
        /// Round X-C1: with <paramref name="allFixtures"/>, every fixture in view beyond the nearest shadowed ones is lit
        /// too, unshadowed (nearest first, up to <see cref="ArtificialLighting.MAX_FAR"/>), and nothing fades.
        /// </summary>
        private void UpdateArtificialLights(bool allFixtures = false)
        {
            ArtificialLighting a = _renderer.Artificial;
            a.Clear();
            if (_lightMode == ArtificialLightMode.Off || !HasArtificialLighting) { return; }

            // How much the sun drowns the fixtures out (the classic light sits in between)
            SunLighting sun = _renderer.Lighting;
            float day = sun.Enabled ? SmoothStep(-6f, 25f, sun.AltitudeDegrees) : 0.6f;
            float brightness = Math.Clamp(_lightIntensity, 0f, 2f);
            float glow = (0.5f + 0.5f * brightness) * Lerp(1f, 0.6f, day);
            a.Emissive = glow;
            a.Bloom = _bloomFailed ? 0f : 0.9f * glow * Math.Clamp(_bloomIntensity, 0f, 2f);
            if (_lightMode != ArtificialLightMode.Lights || Scene.Lighting.Lights.Length == 0) { return; }

            float scale = brightness * Lerp(1f, 0.35f, day);
            if (scale <= 0f) { return; }
            _collectAll = allFixtures;
            PickLights();
            _collectAll = false;
            a.KeepUnshadowed = allFixtures;

            // Fade the weakest picked lights when some were left out (not when every fixture is lit). Y-C1: by score, over
            // the same span as the old 70–100 % of the distance (score ∝ 1 / distance²: up to 1 / 0.7² ≈ 2 × the weakest)
            float fadeLow = float.MinValue, fadeHigh = float.MinValue;
            if (!allFixtures && _candidateCount > _pickCount && _pickCount > 0)
            {
                fadeLow = _pickScore[_pickCount - 1];
                fadeHigh = fadeLow * 2.04f;
            }

            // Round Y-D2: light quality (photos, videos and every-fixture views: High). The strongest get shadow maps (the
            // first of them 4-tap soft shadows, the rest 1 tap); the rest of the picks light unshadowed (no map, no taps)
            LightQualityLimits(allFixtures ? LIGHT_QUALITY_HIGH : _lightQuality, out int shadowed, out int softTaps);
            LightSource[] lights = Scene.Lighting.Lights;
            int n = 0;
            for (int k = 0; k < _pickCount; k++)
            {
                int i = _pickLight[k];
                float fade = fadeHigh > fadeLow ? SmoothStep(fadeLow, fadeHigh, _pickScore[k]) : 1f;
                Vector3 p = _pickPosition[k], c = _lightColour[i] * scale;
                if (k >= shadowed)
                {
                    a.AddFar(new Vector4(p, _lightRadius[i]), new Vector4(c * fade, _pickCode[k]));
                    continue;
                }
                a.Position[n] = new Vector4(p, _lightRadius[i]);
                a.Colour[n] = new Vector4(c, _pickCode[k]);
                a.Shadow[n] = new Vector4(-1f, fade, k >= softTaps ? 1f : 0f, 0f); // Y-D2: z = 1 → one shadow tap
                a.Key[n] = _pickKey[k];
                n++;
            }
            a.Count = n;

            // Round X-C1: the rest, unshadowed, strongest first (Y-C1)
            if (!allFixtures || _allCandidates.Count <= _pickCount) { return; }
            _allCandidates.Sort(BY_SCORE);
            foreach (var (i, p, _, key, code) in _allCandidates)
            {
                if (a.FarCount >= ArtificialLighting.MAX_FAR) { break; }
                if (Array.IndexOf(_pickKey, key, 0, _pickCount) >= 0) { continue; }
                Vector3 c = _lightColour[i] * scale;
                a.AddFar(new Vector4(p, _lightRadius[i]), new Vector4(c, code));
            }
        }

        /// <summary>
        /// Chooses the strongest lights (Y-C1: output ÷ distance², see the class notes) whose sphere touches the view:
        /// fixtures where they stand (or where they were moved to), plus clones of fixtures. Hidden fixtures and hidden
        /// categories / links are skipped.
        /// </summary>
        private void PickLights()
        {
            _pickCount = 0;
            _candidateCount = 0;
            _allCandidates.Clear();
            PickLightsInView();

            // Y-C1: this frame's choice is next frame's head start
            _rankKeptNext.Clear();
            for (int k = 0; k < _pickCount; k++) { _rankKeptNext.Add(_pickKey[k]); }
            (_rankKept, _rankKeptNext) = (_rankKeptNext, _rankKept);
        }

        /// <summary>The candidates: every light in view goes through <see cref="Consider"/>.</summary>
        private void PickLightsInView()
        {

            // Normalised view planes (sphere tests)
            Vector4[] planes = Camera.Planes;
            for (int p = 0; p < 6; p++)
            {
                Vector4 plane = planes[p];
                float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
                _viewPlanes[p] = length > 1e-8f ? plane / length : plane;
            }

            LightSource[] lights = Scene.Lighting.Lights;
            ElementRecord[] elements = Scene.Elements;
            for (int i = 0; i < lights.Length; i++)
            {
                int e = lights[i].Element;
                if (_userHidden[e] || ProfileHides(e) || !_groupVisible[SceneBatches.GroupOf(elements[e])]) { continue; }

                if (!_hidden[e])
                {
                    Consider(i, lights[i].Position, i, LightCodeOf(i));
                    continue;
                }

                // Hidden in the static scene: moved (follow it) or removed (dark)
                DynamicInstance moved = Dynamics.FindOriginal(e);
                if (moved == null || !Dynamics.IsActive(moved)) { continue; }
                Consider(i, Vector3.Transform(lights[i].Position, moved.Model), ((long)moved.Id << 32) | (uint)i, LightCodeOf(i, moved.Model));
            }

            foreach (DynamicInstance instance in Dynamics.Instances)
            {
                if (!instance.IsClone || !Dynamics.IsActive(instance)) { continue; }
                if (!_lightOfElement.TryGetValue(instance.Element, out int first)) { continue; }
                // X-D1 r3: a fixture can hold several light sources (nested up / down halves): consecutive lights
                for (int i = first; i < lights.Length && lights[i].Element == instance.Element; i++)
                {
                    Consider(i, Vector3.Transform(lights[i].Position, instance.Model), ((long)instance.Id << 32) | (uint)i, LightCodeOf(i, instance.Model));
                }
            }
        }

        /// <summary>
        /// Keeps a light if it is among the strongest so far and its sphere touches the view. Y-C1: its score is its
        /// output over its distance squared (at least 1 m), times <see cref="RANK_KEEP"/> when it was used last frame.
        /// </summary>
        /// <param name="light">Index into the scene's lights.</param>
        /// <param name="position">Where it is now.</param>
        /// <param name="key">Its identity across frames (its shadow map is cached under it).</param>
        /// <param name="code">X-D1: its downward share, or its profile code (turned with a moved / cloned fixture).</param>
        private void Consider(int light, Vector3 position, long key, float code)
        {
            float radius = _lightRadius[light];
            for (int p = 0; p < 6; p++)
            {
                Vector4 plane = _viewPlanes[p];
                if (plane.X * position.X + plane.Y * position.Y + plane.Z * position.Z + plane.W < -radius) { return; }
            }
            _candidateCount++;

            float score = RankScore(light, Vector3.DistanceSquared(position, Camera.Position), _rankKept.Contains(key));
            if (_collectAll) { _allCandidates.Add((light, position, score, key, code)); } // X-C1
            int max = ArtificialLighting.MAX_LIGHTS;
            if (_pickCount == max && score <= _pickScore[max - 1]) { return; }

            // Insertion into the sorted list (strongest first), dropping the weakest when full
            int slot = Math.Min(_pickCount, max - 1);
            while (slot > 0 && _pickScore[slot - 1] < score)
            {
                _pickLight[slot] = _pickLight[slot - 1];
                _pickScore[slot] = _pickScore[slot - 1];
                _pickPosition[slot] = _pickPosition[slot - 1];
                _pickKey[slot] = _pickKey[slot - 1];
                _pickCode[slot] = _pickCode[slot - 1];
                slot--;
            }
            _pickLight[slot] = light;
            _pickScore[slot] = score;
            _pickPosition[slot] = position;
            _pickKey[slot] = key;
            _pickCode[slot] = code;
            if (_pickCount < max) { _pickCount++; }
        }

        /// <summary>
        /// Round Y-C1: how much a light adds where you stand: its output (lm) over its distance squared (at least 1 m, so
        /// a lamp at your elbow doesn't swamp the rest), times <see cref="RANK_KEEP"/> when it was used last frame.
        /// </summary>
        private float RankScore(int light, float distanceSquared, bool keptLastFrame)
        {
            float lumens = light < _lightLumens.Length ? _lightLumens[light] : 1000f;
            float score = MathF.Max(lumens, 1f) / MathF.Max(distanceSquared, 1f);
            return keptLastFrame ? score * RANK_KEEP : score;
        }

        #endregion

        #region Controls

        /// <summary>
        /// K: off → glow → glow + light → off.
        /// </summary>
        private void CycleLightMode()
        {
            if (!HasArtificialLighting)
            {
                Sound.Play(SoundId.Error);
                Toast("This model has no lighting fixtures or glowing materials");
                return;
            }
            SetLightMode((ArtificialLightMode)(((int)_lightMode + 1) % 3));
        }

        /// <summary>
        /// Changes the mode and says what it does.
        /// </summary>
        private void SetLightMode(ArtificialLightMode mode)
        {
            _lightMode = mode;
            Sound.Play(SoundId.UiClick);
            int fixtures = Scene.Lighting.Lights.Length;
            Toast(mode switch
            {
                ArtificialLightMode.Off => "Artificial lights off",
                ArtificialLightMode.Glow => "Artificial lights: glow only",
                _ => fixtures > 0 ? $"Artificial lights: glow + light ({fixtures} fixtures)" : "Artificial lights: glow (no fixtures to light rooms)"
            });
        }

        /// <summary>
        /// The sun panel's artificial-light rows: mode, X-C1's all fixtures live, and brightness.
        /// </summary>
        /// <returns>The height used.</returns>
        private float BuildLightControls(FontAtlas f, InputState input, float x, float y, float w)
        {
            float top = y;
            _ui.Text(f.Body, x, y + S(6), "Lights", HasArtificialLighting ? UiTheme.TEXT : UiTheme.TEXT_MUTED);
            int mode = Segmented(f, input, x + S(64), y, w - S(64), LIGHT_MODE_OPTIONS, (int)_lightMode);
            if (mode != (int)_lightMode)
            {
                if (HasArtificialLighting) { SetLightMode((ArtificialLightMode)mode); }
                else { CycleLightMode(); }
            }
            y += S(46);

            // Round X-C1: every fixture in view, live (photos, videos and stills always do)
            bool all = Checkbox(f, input, x, y, w, "All fixtures live (slower)", _allLightsLive);
            if (all != _allLightsLive)
            {
                _allLightsLive = all;
                Toast(all ? "Every fixture in view is lit (the nearest 32 cast shadows): slower with many fixtures" : "The nearest 32 fixtures are lit live (photos and videos still light them all)", 3f);
            }
            y += S(32);

            // Brightness and bloom side by side (keeps the panel's height)
            float half = (w - S(16)) * 0.5f;
            Text.Clear().Append((long)MathF.Round(_lightIntensity * 100f)).Append(" %");
            float value = Slider(f, input, SLIDER_LIGHTS, x, y, half, "Light", Text.Span, _lightIntensity, 0f, 2f);
            _lightIntensity = MathF.Round(value * 20f) / 20f;
            Text.Clear().Append((long)MathF.Round(_bloomIntensity * 100f)).Append(" %");
            value = Slider(f, input, SLIDER_BLOOM, x + half + S(16), y, half, "Bloom", Text.Span, _bloomIntensity, 0f, 2f);
            _bloomIntensity = MathF.Round(value * 20f) / 20f;
            y += S(54);
            return y - top;
        }

        #endregion

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            if (edge1 <= edge0) { return x >= edge1 ? 1f : 0f; }
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
