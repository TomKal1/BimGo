using System.Numerics;
using BimGo.Native;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Round X-B: billboards (entourage: trees, shrubs, people, cars, the user's own cut-outs). Their pictures are one
    /// RGBA texture array (512 px layers, mipmapped, bound to the scratch unit 0 only while they draw); each billboard is
    /// one instance of a 4-vertex strip (16 floats: base and height, width / flip / layer / look, colour and luminance
    /// scale, the picture's content box). They face the eye in the scene and the sun in the shadow maps (so their shadow
    /// is the full silhouette), take the sun, its shadows and the artificial lights, and stay out of AO, probes, the
    /// minimap and light shadows.
    /// </summary>
    internal sealed unsafe partial class SceneRenderer
    {
        #region Fields

        /// <summary>Floats per billboard instance.</summary>
        public const int BILLBOARD_FLOATS = 16;

        /// <summary>The pictures' layer size (px).</summary>
        public const int BILLBOARD_LAYER = EntourageArt.SIZE;

        private ShaderProgram _billboardProgram, _billboardShadowProgram;
        private bool _billboardFailed;
        private LightUniforms _billboardLight;
        private AoUniforms _billboardAo;
        private ArtificialUniforms _billboardLights;
        private int _bViewProj, _bEye, _bFaceSun, _bFaceDir, _bFogColor, _bFogDensity, _bClipCount, _bClipPlanes;
        private int _bsViewProj, _bsFaceSun, _bsFaceDir;

        private uint _billboardArray, _billboardVao, _billboardVbo;
        private int _billboardLayers, _billboardCount, _billboardCapacity;

        /// <summary>Billboards uploaded (drawn this frame when the pictures are there).</summary>
        public int BillboardCount => _billboardArray != 0 ? _billboardCount : 0;

        #endregion

        #region Setup

        private bool EnsureBillboardProgram()
        {
            if (_billboardProgram != null) { return true; }
            if (_billboardFailed) { return false; }
            try
            {
                _billboardProgram = ShaderProgram.Create("billboard", Shaders.BILLBOARD_VS, Shaders.BILLBOARD_FS);
                _billboardShadowProgram = ShaderProgram.Create("billboard shadow", Shaders.BILLBOARD_VS, Shaders.BILLBOARD_SHADOW_FS);
            }
            catch (Exception ex)
            {
                _billboardFailed = true;
                _billboardProgram?.Dispose();
                _billboardProgram = null;
                Utilities.Log_Utils.Write($"Billboard shader failed: {ex.Message}");
                return false;
            }
            ShaderProgram p = _billboardProgram;
            _billboardLight = LightUniforms.From(p);
            _billboardAo = AoUniforms.From(p);
            _billboardLights = ArtificialUniforms.From(p);
            p.Use();
            Gl.Uniform1(p.Uniform("uBillboards"), 0);
            _bViewProj = p.Uniform("uViewProj");
            _bEye = p.Uniform("uEye");
            _bFaceSun = p.Uniform("uFaceSun");
            _bFaceDir = p.Uniform("uFaceDir");
            _bFogColor = p.Uniform("uFogColor");
            _bFogDensity = p.Uniform("uFogDensity");
            _bClipCount = p.Uniform("uClipCount");
            _bClipPlanes = p.Uniform("uClipPlanes");

            ShaderProgram s = _billboardShadowProgram;
            s.Use();
            Gl.Uniform1(s.Uniform("uBillboards"), 0);
            _bsViewProj = s.Uniform("uViewProj");
            _bsFaceSun = s.Uniform("uFaceSun");
            _bsFaceDir = s.Uniform("uFaceDir");
            Gl.UseProgram(0);

            // The instance buffer: four vec4 attributes, one step per instance
            _billboardVao = Gl.GenVertexArray();
            _billboardVbo = Gl.GenBuffer();
            Gl.BindVertexArray(_billboardVao);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _billboardVbo);
            for (uint a = 0; a < 4; a++)
            {
                Gl.EnableVertexAttribArray(a);
                Gl.VertexAttribPointer(a, 4, Gl.FLOAT, false, BILLBOARD_FLOATS * sizeof(float), (nint)(a * 4 * sizeof(float)));
                Gl.VertexAttribDivisor(a, 1);
            }
            Gl.BindVertexArray(0);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
            return true;
        }

        /// <summary>
        /// Uploads the billboard pictures (each <see cref="BILLBOARD_LAYER"/> px square, RGBA top row first), one layer
        /// each in the given order. An empty list frees them.
        /// </summary>
        /// <returns>Null, or why billboards can't be shown.</returns>
        public string SetBillboardPictures(IReadOnlyList<EntourageImage> pictures)
        {
            ReleaseBillboardArray();
            if (pictures == null || pictures.Count == 0) { return null; }
            if (!EnsureBillboardProgram()) { return "Billboards could not be set up on this graphics card (see the log)."; }

            int layers = pictures.Count;
            _billboardArray = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _billboardArray);
            Gl.PixelStore(Gl.UNPACK_ALIGNMENT, 4);
            Gl.TexImage3D(Gl.TEXTURE_2D_ARRAY, 0, Gl.RGBA8, BILLBOARD_LAYER, BILLBOARD_LAYER, layers, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
            for (int i = 0; i < layers; i++)
            {
                EntourageImage picture = pictures[i];
                if (picture == null || picture.Width != BILLBOARD_LAYER || picture.Height != BILLBOARD_LAYER) { continue; }
                fixed (byte* p = picture.Rgba)
                {
                    Gl.TexSubImage3D(Gl.TEXTURE_2D_ARRAY, 0, 0, 0, i, BILLBOARD_LAYER, BILLBOARD_LAYER, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
                }
            }
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR_MIPMAP_LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
            Gl.GenerateMipmap(Gl.TEXTURE_2D_ARRAY);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
            if (Gl.GetError() == Gl.OUT_OF_MEMORY)
            {
                ReleaseBillboardArray();
                return "Not enough graphics memory for the billboard pictures: billboards are off.";
            }
            _billboardLayers = layers;
            Utilities.Log_Utils.Write($"Billboards: {layers} picture(s) uploaded ({BILLBOARD_LAYER} px).");
            return null;
        }

        /// <summary>
        /// Uploads the billboards (<see cref="BILLBOARD_FLOATS"/> floats each; <paramref name="count"/> of them).
        /// </summary>
        public void SetBillboards(float[] data, int count)
        {
            _billboardCount = 0;
            if (data == null || count <= 0) { return; }
            if (!EnsureBillboardProgram()) { return; }
            count = Math.Min(count, data.Length / BILLBOARD_FLOATS);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _billboardVbo);
            fixed (float* p = data)
            {
                if (count > _billboardCapacity)
                {
                    _billboardCapacity = Math.Max(count, _billboardCapacity * 2);
                    Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_billboardCapacity * BILLBOARD_FLOATS * sizeof(float), null, Gl.DYNAMIC_DRAW);
                }
                Gl.BufferSubData(Gl.ARRAY_BUFFER, 0, (nint)count * BILLBOARD_FLOATS * sizeof(float), p);
            }
            Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
            _billboardCount = count;
        }

        private void ReleaseBillboardArray()
        {
            if (_billboardArray != 0) { Gl.DeleteTexture(_billboardArray); }
            _billboardArray = 0;
            _billboardLayers = 0;
        }

        /// <summary>Frees the billboard objects (renderer dispose).</summary>
        private void DisposeBillboards()
        {
            ReleaseBillboardArray();
            Gl.DeleteBuffer(_billboardVbo);
            Gl.DeleteVertexArray(_billboardVao);
            _billboardVbo = _billboardVao = 0;
            _billboardProgram?.Dispose();
            _billboardShadowProgram?.Dispose();
            _billboardProgram = _billboardShadowProgram = null;
        }

        #endregion

        #region Draw

        private bool BillboardsReady => _billboardArray != 0 && _billboardCount > 0 && _billboardProgram != null;

        /// <summary>
        /// Draws the billboards facing the eye (call after the opaque scene and the ground, depth test and writes on).
        /// </summary>
        public void DrawBillboards(in SceneDrawParams p)
        {
            if (!BillboardsReady) { return; }
            _billboardProgram.Use();
            Gl.UniformMatrix4(_bViewProj, p.ViewProjection);
            Gl.Uniform3(_bEye, p.Eye.X, p.Eye.Y, p.Eye.Z);
            Gl.Uniform1(_bFaceSun, 0);
            Gl.Uniform3(_bFaceDir, 0f, -1f, 0f);
            Vector3 fog = p.Sun ? FogColour : FOG_COLOUR;
            Gl.Uniform3(_bFogColor, fog.X, fog.Y, fog.Z);
            Gl.Uniform1(_bFogDensity, p.FogDensity);
            ApplyLight(_billboardLight, p.Sun, transmit: false);
            ApplyAo(_billboardAo, on: false);
            ApplyArtificial(_billboardLights, on: true);
            ApplyClip(_bClipCount, _bClipPlanes, p.Section);

            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _billboardArray);
            Gl.Disable(Gl.CULL_FACE);
            Gl.BindVertexArray(_billboardVao);
            Gl.DrawArraysInstanced(Gl.TRIANGLE_STRIP, 0, 4, _billboardCount);
            Gl.BindVertexArray(0);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
        }

        /// <summary>
        /// Draws the billboards into the current shadow cascade, each turned to face the sun (depth only, cut out by the
        /// picture; the caller has set the colour mask, depth state and polygon offset).
        /// </summary>
        private void DrawBillboardShadows(in Matrix4x4 lightViewProjection)
        {
            if (!BillboardsReady || _billboardShadowProgram == null) { return; }
            Vector3 sun = Lighting.SunDirection;
            var level = new Vector3(sun.X, sun.Y, 0f);
            level = level.LengthSquared() > 1e-8f ? Vector3.Normalize(level) : new Vector3(0f, -1f, 0f);
            _billboardShadowProgram.Use();
            Gl.UniformMatrix4(_bsViewProj, lightViewProjection);
            Gl.Uniform1(_bsFaceSun, 1);
            Gl.Uniform3(_bsFaceDir, level.X, level.Y, level.Z);
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _billboardArray);
            Gl.BindVertexArray(_billboardVao);
            Gl.DrawArraysInstanced(Gl.TRIANGLE_STRIP, 0, 4, _billboardCount);
            Gl.BindVertexArray(0);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
        }

        #endregion
    }
}
