using System.Numerics;
using BimGo.Native;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Round X-C1: screen-space reflections (Realistic mode). Upward-facing reflective surfaces (water, shiny floors and
    /// tops; after Gavin's first test, glass and mirrors use planar reflections instead: see
    /// SceneRenderer.PlanarReflections) march their reflected ray through this frame's half-resolution geometry pre-pass (view depth, the one
    /// AO and bloom use) and, where it meets a surface on screen, take that surface's colour from the previous frame
    /// (reprojected with the previous frame's camera, so the camera may move). Misses (off screen, the sky, behind
    /// something) keep the probe or sky reflection, faded in between, so SSR only ever sharpens what was there.
    /// <list type="bullet">
    /// <item><b>History:</b> the scene target's colour copied after the glass and the weather (before bloom, markers
    /// and UI) into a mipmapped texture: rougher surfaces read a blurrier mip.</item>
    /// <item><b>Quality</b> (<see cref="SceneDrawParams.ScreenReflections"/>): 0 off, 1 low (12 steps), 2 medium (24),
    /// 3 high (48), each step further than the last (6 cm to 60 m), then a few halving steps on the hit.</item>
    /// </list>
    /// Texture units 17 (pre-pass) and 18 (history): 1–16 are taken. Off for probe captures, the plan minimap and
    /// clash pictures (no pre-pass for those).
    /// </summary>
    internal sealed unsafe partial class SceneRenderer
    {
        /// <summary>The pre-pass (normal + view depth) for the reflections' ray march.</summary>
        public const int SSR_GEOMETRY_UNIT = 17;

        /// <summary>The previous frame's colour (mipmapped).</summary>
        public const int SSR_COLOUR_UNIT = 18;

        /// <summary>The history isn't kept for targets larger than this (big photos use the view's own history).</summary>
        private const int SSR_HISTORY_MAX = 4096;

        private struct SsrUniforms
        {
            public int Quality, ViewProj, PrevViewProj, Right, Up, Forward, MaxLod;

            public static SsrUniforms From(ShaderProgram p)
            {
                p.Use();
                Gl.Uniform1(p.Uniform("uSsrGeometry"), SSR_GEOMETRY_UNIT);
                Gl.Uniform1(p.Uniform("uSsrColour"), SSR_COLOUR_UNIT);
                return new SsrUniforms
                {
                    Quality = p.Uniform("uSsrQuality"),
                    ViewProj = p.Uniform("uSsrViewProj"),
                    PrevViewProj = p.Uniform("uSsrPrevViewProj"),
                    Right = p.Uniform("uSsrRight"),
                    Up = p.Uniform("uSsrUp"),
                    Forward = p.Uniform("uSsrForward"),
                    MaxLod = p.Uniform("uSsrMaxLod")
                };
            }
        }

        private SsrUniforms _sceneSsr;

        // This frame's pre-pass ran (and its camera basis); the history (texture, its framebuffer, size, camera)
        private bool _ssrGeometry;
        private Vector3 _ssrRight = Vector3.UnitY, _ssrUp = Vector3.UnitZ, _ssrForward = Vector3.UnitX;
        private uint _ssrHistory, _ssrHistoryFbo, _ssrPlaceholder;
        private int _ssrHistoryWidth, _ssrHistoryHeight, _ssrHistoryLevels;
        private bool _ssrHistoryValid;
        private Matrix4x4 _ssrHistoryViewProj = Matrix4x4.Identity;
        private Vector3 _ssrHistoryEye;

        /// <summary>A jump further than this (m) since the history (a teleport, a bookmark) skips the reflections for a frame.</summary>
        private const float SSR_JUMP = 3f;

        private void InitialiseScreenReflections()
        {
            _sceneSsr = SsrUniforms.From(_sceneProgram);
            // A 1×1 placeholder keeps both samplers valid until the first history
            _ssrPlaceholder = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D, _ssrPlaceholder);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)Gl.NEAREST);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.NEAREST);
            uint black = 0;
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGBA8, 1, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &black);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
        }

        /// <summary>
        /// Notes the pre-pass camera basis (called by <see cref="UpdateScreenEffects"/> when the pre-pass ran for the
        /// reflections).
        /// </summary>
        private void SetScreenReflectionGeometry(bool ran, Vector3 right, Vector3 up, Vector3 forward)
        {
            _ssrGeometry = ran;
            _ssrRight = right;
            _ssrUp = up;
            _ssrForward = forward;
        }

        /// <summary>
        /// Sets the reflection block for one draw: on when asked for, the pre-pass ran this frame and there is a history;
        /// binds the pre-pass and the history to their units.
        /// </summary>
        private void ApplyScreenReflections(in SsrUniforms u, in SceneDrawParams p)
        {
            int quality = Math.Clamp(p.ScreenReflections, 0, 3);
            bool on = quality > 0 && _ssrGeometry && _ssrHistoryValid && _effects.Ready && !p.Plan
                && Vector3.DistanceSquared(p.Eye, _ssrHistoryEye) < SSR_JUMP * SSR_JUMP;
            Gl.Uniform1(u.Quality, on ? quality : 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + SSR_GEOMETRY_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D, on ? _effects.GeometryTexture : _ssrPlaceholder);
            Gl.ActiveTexture(Gl.TEXTURE0 + SSR_COLOUR_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D, on ? _ssrHistory : _ssrPlaceholder);
            Gl.ActiveTexture(Gl.TEXTURE0);
            if (!on) { return; }
            Gl.UniformMatrix4(u.ViewProj, p.ViewProjection);
            Gl.UniformMatrix4(u.PrevViewProj, _ssrHistoryViewProj);
            Gl.Uniform3(u.Right, _ssrRight.X, _ssrRight.Y, _ssrRight.Z);
            Gl.Uniform3(u.Up, _ssrUp.X, _ssrUp.Y, _ssrUp.Z);
            Gl.Uniform3(u.Forward, _ssrForward.X, _ssrForward.Y, _ssrForward.Z);
            Gl.Uniform1(u.MaxLod, (float)Math.Max(_ssrHistoryLevels - 1, 0));
        }

        /// <summary>
        /// Keeps the target's colour as next frame's reflection history (call after the glass and the weather, before
        /// bloom and markers), with the camera it was drawn from; rebinds the target afterwards. Skipped for targets
        /// over 4096 px (big photos reflect the view's own history).
        /// </summary>
        /// <param name="target">The scene target (multisampled or not).</param>
        /// <param name="viewProjection">The camera it was drawn with.</param>
        /// <param name="eye">Its position.</param>
        public void CaptureReflectionHistory(RenderTarget target, Matrix4x4 viewProjection, Vector3 eye)
        {
            int w = target.Width, h = target.Height;
            if (w <= 0 || h <= 0 || w > SSR_HISTORY_MAX || h > SSR_HISTORY_MAX) { return; }
            if (!EnsureReflectionHistory(w, h))
            {
                _ssrHistoryValid = false;
                target.Bind();
                return;
            }
            target.ResolveColourTo(_ssrHistoryFbo, w, h);
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D, _ssrHistory);
            Gl.GenerateMipmap(Gl.TEXTURE_2D);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            _ssrHistoryViewProj = viewProjection;
            _ssrHistoryEye = eye;
            _ssrHistoryValid = true;
            target.Bind();
        }

        /// <summary>Forgets the history (reflections off, a new model, a jump the history can't follow).</summary>
        public void ReleaseReflectionHistory()
        {
            _ssrHistoryValid = false;
            if (_ssrHistoryFbo != 0) { Gl.DeleteFramebuffer(_ssrHistoryFbo); }
            if (_ssrHistory != 0) { Gl.DeleteTexture(_ssrHistory); }
            _ssrHistoryFbo = _ssrHistory = 0;
            _ssrHistoryWidth = _ssrHistoryHeight = _ssrHistoryLevels = 0;
        }

        private bool EnsureReflectionHistory(int w, int h)
        {
            if (_ssrHistory != 0 && w == _ssrHistoryWidth && h == _ssrHistoryHeight) { return true; }
            ReleaseReflectionHistory();
            int levels = 1;
            for (int s = Math.Max(w, h); s > 1; s >>= 1) { levels++; }
            levels = Math.Min(levels, 8); // the blurriest read is about 1/128 of the view: enough for rough floors
            _ssrHistory = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D, _ssrHistory);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR_MIPMAP_LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, levels - 1);
            int lw = w, lh = h;
            for (int level = 0; level < levels; level++)
            {
                Gl.TexImage2D(Gl.TEXTURE_2D, level, Gl.RGBA8, lw, lh, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
                lw = Math.Max(1, lw / 2);
                lh = Math.Max(1, lh / 2);
            }
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            _ssrHistoryFbo = Gl.GenFramebuffer();
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _ssrHistoryFbo);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _ssrHistory, 0);
            bool complete = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) == Gl.FRAMEBUFFER_COMPLETE;
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            if (!complete)
            {
                Utilities.Log_Utils.Write($"Screen-space reflections: the {w} × {h} history target is incomplete; reflections use the probes only.");
                ReleaseReflectionHistory();
                return false;
            }
            _ssrHistoryWidth = w;
            _ssrHistoryHeight = h;
            _ssrHistoryLevels = levels;
            return true;
        }

        private void DisposeScreenReflections()
        {
            ReleaseReflectionHistory();
            if (_ssrPlaceholder != 0) { Gl.DeleteTexture(_ssrPlaceholder); }
            _ssrPlaceholder = 0;
        }
    }
}
