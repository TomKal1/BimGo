using System.Numerics;
using BimGo.Native;
using BimGo.Physics;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Round X-C1 (after Gavin's first test): planar reflections for glass and mirrors. Screen-space reflections can't
    /// show what a pane facing you reflects (it's behind you, off screen), so glass and mirrors get a true mirrored
    /// view instead. Stills only since revision 4 (live, panes popped between views as the chosen planes changed):
    /// photos, videos, batch stills and photo mode's preview (2 planes there).
    /// <list type="bullet">
    /// <item><b>Planes</b> (<see cref="ReflectivePlanes"/>): the model's glass and smooth mirror-like triangles grouped
    /// by plane at load (and when the materials change); only walls of glass, not floors (|n.z| &lt; 0.7: floors and
    /// water keep SSR).</item>
    /// <item><b>Each still</b> the planes that look biggest (2, 4 or 8 by quality, within 40 m) are drawn from the
    /// camera mirrored in the plane, everything behind the plane cut away (one more clip plane), into a texture array
    /// layer at half or full resolution: sky, the opaque scene, ground, billboards, glass. No AO, SSR or planar
    /// reflections inside the mirrored view (probes and sky as usual).</item>
    /// <item><b>The scene shader</b> looks a glass or mirror fragment up in the layer of the plane it lies on, at its own
    /// screen position (a point on the plane lands on the same pixel in both views).</item>
    /// </list>
    /// Texture unit 19 (a 2D array: one sampler for both planes, so the scene shader stays within 16 samplers).
    /// </summary>
    internal sealed unsafe partial class SceneRenderer
    {
        /// <summary>The mirrored views (a 2D array, a layer per plane).</summary>
        public const int PLANAR_UNIT = 19;

        /// <summary>Planes drawn at most (the shader's arrays).</summary>
        public const int PLANAR_MAX = 8;

        /// <summary>The mirrored views' longest side at most (px): 8 layers of 2048² are 128 MB, only while a still renders.</summary>
        private const int PLANAR_SIZE_MAX = 2048;

        /// <summary>Planes further than this (m, to their box) aren't drawn.</summary>
        private const float PLANAR_RANGE = 40f;

        /// <summary>The cut sits this far (m) in front of the plane, so both faces of a pane go.</summary>
        private const float PLANAR_CUT = 0.03f;

        private struct PlanarUniforms
        {
            public int Count, Planes, Slack, InvScreen;

            public static PlanarUniforms From(ShaderProgram p)
            {
                p.Use();
                Gl.Uniform1(p.Uniform("uPlanarTex"), PLANAR_UNIT);
                return new PlanarUniforms { Count = p.Uniform("uPlanarCount"), Planes = p.Uniform("uPlanarPlane[0]"), Slack = p.Uniform("uPlanarSlack[0]"), InvScreen = p.Uniform("uPlanarInvScreen") };
            }
        }

        private PlanarUniforms _scenePlanar;
        private ReflectivePlane[] _reflectivePlanes = Array.Empty<ReflectivePlane>();

        // This frame's mirrored views: the planes (canonical n, d) for the shader, their array texture and target
        private readonly Vector4[] _planarPlanes = new Vector4[PLANAR_MAX];
        private readonly Vector4[] _planarSlack = new Vector4[PLANAR_MAX / 4]; // per plane: how far from it still matches (m)
        private int _planarCount;
        private Vector2 _planarInvScreen;
        private uint _planarTexture, _planarFbo, _planarDepth, _planarPlaceholder;
        private int _planarWidth, _planarHeight;
        private readonly Vector4[] _planarFrustum = new Vector4[6];
        private readonly (int Plane, float Score)[] _planarPicks = new (int, float)[PLANAR_MAX];

        /// <summary>Mirrored views drawn this frame.</summary>
        public int PlanarCount => _planarCount;

        private void InitialisePlanarReflections()
        {
            _scenePlanar = PlanarUniforms.From(_sceneProgram);
            _planarPlaceholder = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _planarPlaceholder);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, (int)Gl.NEAREST);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, (int)Gl.NEAREST);
            uint black = 0;
            Gl.TexImage3D(Gl.TEXTURE_2D_ARRAY, 0, Gl.RGBA8, 1, 1, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &black);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
        }

        /// <summary>
        /// Finds the glass and mirror planes (load, and whenever the materials or the water rule change): glass is any
        /// material with Revit glass reflectivity; a mirror is a smooth material in the top shine tier (75 % +, roughness
        /// up to 0.2) or one named "mirror". Only walls of it (not floors).
        /// </summary>
        private void RefreshReflectivePlanes(MaterialData data = null)
        {
            _reflectivePlanes = Array.Empty<ReflectivePlane>();
            _planarCount = 0;
            data ??= _scene?.Materials;
            if (data == null || data.IsEmpty || data.VertexMaterial.Length != _scene.Vertices.Length) { return; }
            SceneMaterial[] materials = data.Materials;
            var reflective = new bool[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                SceneMaterial m = materials[i];
                (bool water, float shine, float? roughness, _) = MaterialOverrides.Look(m, _materials.WaterFromAsset, _materials.WaterKeywords, _materials.WaterKeywordsOpaque);
                if (water) { continue; }
                bool glass = m.Reflectivity > 0f;
                bool mirror = (shine >= 0.625f && (roughness ?? 1f) <= 0.2f) || (m.Name ?? string.Empty).Contains("mirror", StringComparison.OrdinalIgnoreCase);
                reflective[i] = glass || mirror;
            }
            try
            {
                ReflectivePlane[] all = ReflectivePlanes.Find(_scene.Vertices, _scene.Indices, data.VertexMaterial, reflective);
                _reflectivePlanes = all.Where(p => MathF.Abs(p.Normal.Z) < 0.7f).ToArray();
                Utilities.Log_Utils.Write($"Planar reflections: {_reflectivePlanes.Length} glass / mirror planes (of {all.Length} reflective planes).");
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Planar reflections: the planes couldn't be found ({ex.Message}); glass uses the probes.");
            }
        }

        /// <summary>
        /// Draws this frame's mirrored views (call after the shadows, lights and screen effects, before binding the scene
        /// target; leaves the default framebuffer bound): the best <paramref name="planes"/> glass / mirror planes in
        /// view, at <paramref name="scale"/> of the target size. 0 planes frees the targets.
        /// </summary>
        /// <param name="template">This frame's draw settings (the player's camera).</param>
        /// <param name="width">Scene target width.</param>
        /// <param name="height">Scene target height.</param>
        /// <param name="planes">How many (0–2).</param>
        /// <param name="scale">Resolution of the mirrored views (0.5 or 1).</param>
        public void UpdatePlanarReflections(in SceneDrawParams template, int width, int height, int planes, float scale,
            bool[] groupVisible, DynamicSet dynamics, float groundZ)
        {
            _planarCount = 0;
            planes = Math.Clamp(planes, 0, PLANAR_MAX);
            if (planes == 0 || _reflectivePlanes.Length == 0 || !template.Realistic || template.Whitecard || !HasMaterials)
            {
                if (planes == 0) { ReleasePlanarTargets(); }
                return;
            }

            int count = PickPlanes(template, planes);
            if (count == 0) { return; }

            float fit = MathF.Min(1f, PLANAR_SIZE_MAX / (MathF.Max(width, height) * scale));
            int w = Math.Max(16, (int)(width * scale * fit)), h = Math.Max(16, (int)(height * scale * fit));
            if (!EnsurePlanarTargets(w, h)) { return; }
            _planarInvScreen = new Vector2(1f / Math.Max(width, 1), 1f / Math.Max(height, 1));

            FlushDynamic();
            bool aoWas = _aoActive, ssrWas = _ssrGeometry;
            _aoActive = false;
            _ssrGeometry = false;
            Vector3 eye = template.Eye;
            Vector3 clear = FogColour;
            for (int k = 0; k < count; k++)
            {
                ReflectivePlane plane = _reflectivePlanes[_planarPicks[k].Plane];
                float side = Vector3.Dot(plane.Normal, eye) - plane.Offset;
                Vector3 n = side >= 0f ? plane.Normal : -plane.Normal;
                float d = side >= 0f ? plane.Offset : -plane.Offset;
                Matrix4x4 reflect = Matrix4x4.CreateReflection(new Plane(n, -d));
                Matrix4x4 viewProjection = reflect * template.ViewProjection;
                Vector3 mirroredEye = Vector3.Transform(eye, reflect);
                FpsCamera.ExtractPlanes(viewProjection, _planarFrustum);

                SceneDrawParams p = template;
                p.ViewProjection = viewProjection;
                p.Planes = _planarFrustum;
                p.Eye = mirroredEye;
                p.ScreenReflections = 0;
                p.Planar = false;
                p.Section = true;

                Gl.BindFramebuffer(Gl.FRAMEBUFFER, _planarFbo);
                Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _planarTexture, 0, k);
                Gl.Viewport(0, 0, w, h);
                Gl.ClearColor(clear.X, clear.Y, clear.Z, 1f);
                Gl.DepthMask(true);
                Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
                Gl.Enable(Gl.DEPTH_TEST);
                Gl.DepthFunc(Gl.LEQUAL);
                Gl.Disable(Gl.CULL_FACE);
                Gl.Disable(Gl.BLEND);
                Gl.FrontFace(Gl.CW); // a mirrored view winds the other way: front faces stay front faces

                Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
                DrawSky(inverse, mirroredEye);
                _mirrorClip = new Vector4(-n, -(d + plane.Slack + PLANAR_CUT)); // cut away what is behind the mirror (and the panes themselves)
                _mirrorClipOn = true;
                DrawStatic(p, groupVisible, transparent: false);
                DrawDynamic(p, dynamics, transparent: false);
                DrawGround(viewProjection, mirroredEye, groundZ);
                DrawBillboards(p);
                Gl.Enable(Gl.BLEND);
                Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
                Gl.DepthMask(false);
                DrawStatic(p, groupVisible, transparent: true);
                DrawDynamic(p, dynamics, transparent: true);
                Gl.DepthMask(true);
                Gl.Disable(Gl.BLEND);
                _mirrorClipOn = false;
                Gl.FrontFace(Gl.CCW);

                _planarPlanes[k] = new Vector4(plane.Normal, plane.Offset);
                float slack = plane.Slack + 0.03f;
                switch (k & 3)
                {
                    case 0: _planarSlack[k >> 2].X = slack; break;
                    case 1: _planarSlack[k >> 2].Y = slack; break;
                    case 2: _planarSlack[k >> 2].Z = slack; break;
                    default: _planarSlack[k >> 2].W = slack; break;
                }
            }
            _planarCount = count;
            _aoActive = aoWas;
            _ssrGeometry = ssrWas;
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        }

        /// <summary>
        /// The best planes this frame: in view, on the camera's side by at least 2 cm, within range, scored by area over
        /// squared distance (the nearest big panes win).
        /// </summary>
        private int PickPlanes(in SceneDrawParams template, int wanted)
        {
            int count = 0;
            Vector3 eye = template.Eye;
            Vector4[] frustum = template.Planes;
            for (int i = 0; i < _reflectivePlanes.Length; i++)
            {
                ReflectivePlane plane = _reflectivePlanes[i];
                float side = Vector3.Dot(plane.Normal, eye) - plane.Offset;
                if (MathF.Abs(side) < 0.02f) { continue; }
                Vector3 closest = Vector3.Clamp(eye, plane.Bounds.Min, plane.Bounds.Max);
                float distance = Vector3.Distance(closest, eye);
                if (distance > PLANAR_RANGE || (frustum != null && !BoxInView(frustum, plane.Bounds))) { continue; }
                // How big it looks: area over squared distance (a mirror at arm's length beats a far window), turned
                // away less; the planes drawn last frame keep their place unless clearly beaten (no flicker)
                Vector3 toward = eye - closest;
                float facing = toward.LengthSquared() > 1e-6f ? 0.3f + 0.7f * MathF.Abs(Vector3.Dot(plane.Normal, Vector3.Normalize(toward))) : 1f;
                float score = MathF.Min(plane.Area, 40f) * facing / (distance * distance + 0.25f);
                if (Array.IndexOf(_planarLast, i, 0, _planarLastCount) >= 0) { score *= 1.5f; }
                // Insert, best first (the worst drops out when full)
                int slot;
                if (count < wanted) { slot = count++; }
                else if (score <= _planarPicks[wanted - 1].Score) { continue; }
                else { slot = wanted - 1; }
                while (slot > 0 && _planarPicks[slot - 1].Score < score)
                {
                    _planarPicks[slot] = _planarPicks[slot - 1];
                    slot--;
                }
                _planarPicks[slot] = (i, score);
            }
            _planarLastCount = count;
            for (int k = 0; k < count; k++) { _planarLast[k] = _planarPicks[k].Plane; }
            return count;
        }

        // The planes picked last frame (they're favoured: see PickPlanes)
        private readonly int[] _planarLast = new int[PLANAR_MAX];
        private int _planarLastCount;

        private static bool BoxInView(Vector4[] planes, in Aabb box)
        {
            for (int p = 0; p < 6; p++)
            {
                Vector4 q = planes[p];
                // The box corner furthest along the plane's normal
                float x = q.X >= 0f ? box.Max.X : box.Min.X, y = q.Y >= 0f ? box.Max.Y : box.Min.Y, z = q.Z >= 0f ? box.Max.Z : box.Min.Z;
                if (q.X * x + q.Y * y + q.Z * z + q.W < 0f) { return false; }
            }
            return true;
        }

        /// <summary>Sets the planar block for one draw (only the player's view, in the Realistic mode; never inside a mirrored view).</summary>
        private void ApplyPlanarReflections(in PlanarUniforms u, in SceneDrawParams p)
        {
            bool on = p.Planar && _planarCount > 0 && !p.Plan;
            Gl.Uniform1(u.Count, on ? _planarCount : 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + PLANAR_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, on ? _planarTexture : _planarPlaceholder);
            Gl.ActiveTexture(Gl.TEXTURE0);
            if (!on) { return; }
            Gl.Uniform4(u.Planes, _planarCount, _planarPlanes);
            Gl.Uniform4(u.Slack, _planarSlack.Length, _planarSlack);
            Gl.Uniform2(u.InvScreen, _planarInvScreen.X, _planarInvScreen.Y);
        }

        private bool EnsurePlanarTargets(int w, int h)
        {
            if (_planarFbo != 0 && w == _planarWidth && h == _planarHeight) { return true; }
            ReleasePlanarTargets();
            _planarTexture = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _planarTexture);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAX_LEVEL, 0);
            Gl.TexImage3D(Gl.TEXTURE_2D_ARRAY, 0, Gl.RGBA8, w, h, PLANAR_MAX, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);

            _planarDepth = Gl.GenRenderbuffer();
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, _planarDepth);
            Gl.RenderbufferStorageMultisample(Gl.RENDERBUFFER, 0, Gl.DEPTH24_STENCIL8, w, h);
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, 0);

            _planarFbo = Gl.GenFramebuffer();
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _planarFbo);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _planarTexture, 0, 0);
            Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_STENCIL_ATTACHMENT, Gl.RENDERBUFFER, _planarDepth);
            bool complete = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) == Gl.FRAMEBUFFER_COMPLETE;
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            if (!complete)
            {
                Utilities.Log_Utils.Write($"Planar reflections: the {w} × {h} target is incomplete; glass uses the probes.");
                ReleasePlanarTargets();
                return false;
            }
            _planarWidth = w;
            _planarHeight = h;
            return true;
        }

        /// <summary>Frees the mirrored views (planar reflections off).</summary>
        public void ReleasePlanarTargets()
        {
            _planarCount = 0;
            if (_planarFbo != 0) { Gl.DeleteFramebuffer(_planarFbo); }
            if (_planarTexture != 0) { Gl.DeleteTexture(_planarTexture); }
            if (_planarDepth != 0) { Gl.DeleteRenderbuffer(_planarDepth); }
            _planarFbo = _planarTexture = _planarDepth = 0;
            _planarWidth = _planarHeight = 0;
        }

        private void DisposePlanarReflections()
        {
            ReleasePlanarTargets();
            if (_planarPlaceholder != 0) { Gl.DeleteTexture(_planarPlaceholder); }
            _planarPlaceholder = 0;
        }
    }
}
