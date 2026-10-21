using System.Numerics;
using BimGo.Native;
using BimGo.Physics;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Round Y-D2: the main view's depth pre-pass. The opaque static scene (with occlusion culling's skips) and the moved /
    /// cloned elements are drawn depth-only first (<see cref="Shaders.DEPTH_PREPASS_FS"/> on <see cref="Shaders.SCENE_VS"/>,
    /// whose <c>gl_Position</c> is invariant); the shaded opaque pass then tests EQUAL with depth writes off, so the
    /// expensive per-pixel lighting runs once per pixel instead of once per surface drawn over it (chunks aren't sorted
    /// front to back). Not in the clash view or the minimap (their own drawing).
    /// </summary>
    internal sealed unsafe partial class SceneRenderer
    {
        private ShaderProgram _prepassProgram;
        private int _prepassViewProj, _prepassModel, _prepassClipCount, _prepassClipPlanes, _prepassProfilePass;
        private bool _prepassFailed;
        private bool _prepassActive;

        /// <summary>
        /// Draws the opaque scene's depth (colour writes off) and leaves the depth test on EQUAL with writes off for the
        /// shaded opaque draws that follow; call <see cref="EndDepthPrepass"/> after them.
        /// </summary>
        /// <returns>False when the pre-pass can't run (nothing changed: draw as before).</returns>
        public bool BeginDepthPrepass(in SceneDrawParams p, bool[] groupVisible, DynamicSet dynamics)
        {
            _prepassActive = false;
            if (_prepassFailed || GhostAlpha > 0f) { return false; }
            if (_prepassProgram == null)
            {
                try
                {
                    _prepassProgram = ShaderProgram.Create("depth pre-pass", Shaders.SCENE_VS, Shaders.DEPTH_PREPASS_FS);
                }
                catch (Exception ex)
                {
                    _prepassFailed = true;
                    Utilities.Log_Utils.Write($"Depth pre-pass unavailable: {ex.Message}");
                    return false;
                }
                _prepassViewProj = _prepassProgram.Uniform("uViewProj");
                _prepassModel = _prepassProgram.Uniform("uModel");
                _prepassClipCount = _prepassProgram.Uniform("uClipCount");
                _prepassClipPlanes = _prepassProgram.Uniform("uClipPlanes");
                _prepassProfilePass = _prepassProgram.Uniform("uProfilePass");
                _prepassProgram.Use();
                Gl.Uniform1(_prepassProgram.Uniform("uProfileTex"), PROFILE_UNIT);
            }

            FlushDynamic();
            _prepassProgram.Use();
            Gl.UniformMatrix4(_prepassViewProj, p.ViewProjection);
            Gl.UniformMatrix4(_prepassModel, Matrix4x4.Identity);
            ApplyClip(_prepassClipCount, _prepassClipPlanes, p.Section);
            Gl.Uniform1(_prepassProfilePass, BindProfile(true) ? 1 : 0); // as the scene pass's opaque draw (pass 1)

            Gl.Enable(Gl.DEPTH_TEST);
            Gl.DepthFunc(Gl.LEQUAL);
            Gl.DepthMask(true);
            Gl.ColorMask(false, false, false, false);
            DrawBatches(p.Planes, groupVisible, transparent: false, countStats: false, skip: p.Occlusion ? OcclusionMask : null);
            if (dynamics != null && dynamics.Instances.Count > 0) { DrawDynamicInstances(dynamics, p.Planes, transparent: false, _prepassModel); }
            Gl.ColorMask(true, true, true, true);

            // The shaded opaque draws: only the nearest surface of each pixel passes
            Gl.DepthFunc(Gl.EQUAL);
            Gl.DepthMask(false);
            _prepassActive = true;
            return true;
        }

        /// <summary>Back to the usual depth state (LEQUAL, writes on) after the shaded opaque draws.</summary>
        public void EndDepthPrepass()
        {
            if (!_prepassActive) { return; }
            _prepassActive = false;
            Gl.DepthFunc(Gl.LEQUAL);
            Gl.DepthMask(true);
        }

        private void DisposePrepass()
        {
            _prepassProgram?.Dispose();
            _prepassProgram = null;
        }
    }
}
