using System.Numerics;
using BimGo.Native;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Fork build LC-B: a lighting calc's results drawn on the model: the zones' radiosity patches in false colour or
    /// luminance grey (colours blended at shared corners, clipped to each room's floor and ceiling as WW Tools' report), the
    /// calc points as small discs and the isolux lines. One static vertex buffer (the overlay shader: position + RGBA8),
    /// rebuilt only when the calc set, the mode, the toggles or the scale change, so a frame costs one draw and no uploads
    /// (patterned on <see cref="GridOverlay"/>). The values are 2D labels drawn by the session.
    /// </summary>
    internal sealed unsafe class CalcOverlay : IDisposable
    {
        /// <summary>Patches are lifted this far along their normal (m) so they don't fight the model's faces.</summary>
        public const float LIFT = 0.015f;

        /// <summary>Point disc radius (m) and segments.</summary>
        private const float POINT_RADIUS = 0.045f;
        private const int POINT_SEGMENTS = 6;

        /// <summary>Isolux line width (m), lying flat on the work plane.</summary>
        private const float LINE_WIDTH = 0.03f;

        private OverlayVertex[] _vertices = new OverlayVertex[4096];
        private int _count;
        private int _capacityOnGpu;
        private uint _vao, _vbo;
        private ShaderProgram _program;
        private int _viewProj, _alpha, _sectionPlanes, _sectionCount;
        private bool _uploaded;

        // Unit disc corners (made once)
        private static readonly Vector2[] DISC = BuildDisc();

        /// <summary>Vertices in the buffer (stats).</summary>
        public int VertexCount => _count;

        /// <summary>
        /// Creates the GL objects (once, with the renderer's context current).
        /// </summary>
        public void Initialise()
        {
            if (_program != null) { return; }
            _program = ShaderProgram.Create("calc overlay", Shaders.CALC_VS, Shaders.CALC_FS);
            _viewProj = _program.Uniform("uViewProj");
            _alpha = _program.Uniform("uAlpha");
            _sectionPlanes = _program.Uniform("uSectionPlanes[0]");
            _sectionCount = _program.Uniform("uSectionCount");

            _vao = Gl.GenVertexArray();
            Gl.BindVertexArray(_vao);
            _vbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
            _capacityOnGpu = _vertices.Length;
            Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_capacityOnGpu * OverlayVertex.SIZE, null, Gl.STATIC_DRAW);
            Gl.EnableVertexAttribArray(0);
            Gl.VertexAttribPointer(0, 3, Gl.FLOAT, false, OverlayVertex.SIZE, 0);
            Gl.EnableVertexAttribArray(1);
            Gl.VertexAttribPointer(1, 4, Gl.UNSIGNED_BYTE, true, OverlayVertex.SIZE, 12);
            Gl.BindVertexArray(0);
        }

        /// <summary>Empties the buffer (calc view off).</summary>
        public void Clear()
        {
            _count = 0;
            _uploaded = false;
        }

        /// <summary>
        /// Rebuilds the buffer for a calc set.
        /// </summary>
        /// <param name="result">The calc set.</param>
        /// <param name="mode">Illuminance or Luminance draw the surfaces; Points only the points (and lines).</param>
        /// <param name="surfaces">Draw the patches (Illuminance / Luminance).</param>
        /// <param name="points">Draw the calc points.</param>
        /// <param name="isolines">Draw the isolux lines.</param>
        /// <param name="scaleMin">Bottom of the false colour scale (fc).</param>
        /// <param name="scaleMax">Top of the false colour scale (fc).</param>
        public void Build(LightingCalcResult result, CalcDisplayMode mode, bool surfaces, bool points, bool isolines, float scaleMin, float scaleMax)
        {
            _count = 0;
            _uploaded = false;
            if (result == null || mode == CalcDisplayMode.Off) { return; }

            bool luminance = mode == CalcDisplayMode.Luminance;
            Span<Vector3> clipped = stackalloc Vector3[5];
            Span<float> clippedValues = stackalloc float[5];
            foreach (CalcZoneResult zone in result.Zones)
            {
                if (surfaces && mode != CalcDisplayMode.Points)
                {
                    float[] corners = luminance ? zone.CornerLuminance : zone.CornerIlluminance;
                    float bottom = zone.BottomZ - LightingCalcs.CLIP_MARGIN, top = zone.CeilingZ + LightingCalcs.CLIP_MARGIN;
                    for (int i = 0; i < zone.Patches.Length && i * 3 + 2 < corners.Length; i++)
                    {
                        CalcPatch p = zone.Patches[i];
                        int n = LightingCalcs.ClipZ(p.A, p.B, p.C, corners[i * 3], corners[i * 3 + 1], corners[i * 3 + 2], bottom, top, clipped, clippedValues);
                        if (n == 0) { continue; }
                        Vector3 lift = p.Normal * LIFT;
                        for (int k = 1; k + 1 < n; k++)
                        {
                            Put(clipped[0] + lift, Colour(clippedValues[0], p.Normal));
                            Put(clipped[k] + lift, Colour(clippedValues[k], p.Normal));
                            Put(clipped[k + 1] + lift, Colour(clippedValues[k + 1], p.Normal));
                        }
                    }
                }

                if (points)
                {
                    for (int i = 0; i < zone.Points.Length && i < zone.PointValues.Length; i++)
                    {
                        float value = zone.PointValues[i];
                        if (float.IsNaN(value)) { continue; }
                        Vector3 c = LightingCalcs.FalseColour(value, scaleMin, scaleMax);
                        Disc(zone.Points[i] + new Vector3(0f, 0f, 0.005f), Rgba.FromFloat(c.X, c.Y, c.Z, 1f), Rgba.Hex(0x0C0E12, 0.9f));
                    }
                }

                if (isolines)
                {
                    foreach (CalcIsoline line in zone.Isolines)
                    {
                        uint colour = line.IsTarget ? Rgba.Hex(0xFFFFFF, 0.95f) : Rgba.Hex(0x111827, 0.85f);
                        float width = line.IsTarget ? LINE_WIDTH * 1.6f : LINE_WIDTH;
                        for (int s = 0; s + 1 < line.Segments.Length; s += 2)
                        {
                            FlatLine(line.Segments[s] + new Vector3(0f, 0f, 0.008f), line.Segments[s + 1] + new Vector3(0f, 0f, 0.008f), width, colour);
                        }
                    }
                }
            }

            uint Colour(float value, Vector3 normal)
            {
                Vector3 c = luminance
                    ? LightingCalcs.LuminanceGrey(value, result.LuminanceReference)
                    : LightingCalcs.ShadedFalseColour(value, scaleMin, scaleMax, normal);
                return Rgba.FromFloat(c.X, c.Y, c.Z, 1f);
            }
        }

        // A point: a dark rim with the coloured disc on top, flat (facing up)
        private void Disc(Vector3 centre, uint colour, uint rim)
        {
            for (int i = 0; i < POINT_SEGMENTS; i++)
            {
                Vector2 a = DISC[i], b = DISC[i + 1];
                Put(centre, rim);
                Put(centre + new Vector3(a * POINT_RADIUS * 1.35f, 0f), rim);
                Put(centre + new Vector3(b * POINT_RADIUS * 1.35f, 0f), rim);
            }
            Vector3 up = centre + new Vector3(0f, 0f, 0.002f);
            for (int i = 0; i < POINT_SEGMENTS; i++)
            {
                Vector2 a = DISC[i], b = DISC[i + 1];
                Put(up, colour);
                Put(up + new Vector3(a * POINT_RADIUS, 0f), colour);
                Put(up + new Vector3(b * POINT_RADIUS, 0f), colour);
            }
        }

        // A segment as a flat quad (horizontal), width in metres
        private void FlatLine(Vector3 a, Vector3 b, float width, uint colour)
        {
            Vector3 d = b - a;
            var side = new Vector3(-d.Y, d.X, 0f);
            float length = side.Length();
            if (length < 1e-5f) { return; }
            side *= 0.5f * width / length;
            Put(a - side, colour);
            Put(a + side, colour);
            Put(b + side, colour);
            Put(a - side, colour);
            Put(b + side, colour);
            Put(b - side, colour);
        }

        private void Put(Vector3 p, uint colour)
        {
            if (_count == _vertices.Length) { Array.Resize(ref _vertices, _vertices.Length * 2); }
            _vertices[_count++] = new OverlayVertex { Position = p, Colour = colour };
        }

        private static Vector2[] BuildDisc()
        {
            var disc = new Vector2[POINT_SEGMENTS + 1];
            for (int i = 0; i <= POINT_SEGMENTS; i++)
            {
                float a = i * 2f * MathF.PI / POINT_SEGMENTS;
                disc[i] = new Vector2(MathF.Cos(a), MathF.Sin(a));
            }
            return disc;
        }

        /// <summary>
        /// Draws the buffer depth-tested against the model (no depth writes), at <paramref name="opacity"/>.
        /// </summary>
        public void Draw(FpsCamera camera, float opacity, Vector4[] sectionPlanes, int sectionCount)
        {
            if (_count == 0 || _program == null) { return; }
            Gl.BindVertexArray(_vao);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
            if (!_uploaded)
            {
                if (_vertices.Length > _capacityOnGpu)
                {
                    _capacityOnGpu = _vertices.Length;
                    Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_capacityOnGpu * OverlayVertex.SIZE, null, Gl.STATIC_DRAW);
                }
                fixed (OverlayVertex* data = _vertices)
                {
                    Gl.BufferSubData(Gl.ARRAY_BUFFER, 0, (nint)_count * OverlayVertex.SIZE, data);
                }
                _uploaded = true;
            }

            _program.Use();
            Gl.UniformMatrix4(_viewProj, camera.ViewProjection);
            Gl.Uniform1(_alpha, Math.Clamp(opacity, 0f, 1f));
            Gl.Uniform1(_sectionCount, sectionCount);
            if (sectionCount > 0) { Gl.Uniform4(_sectionPlanes, sectionCount, sectionPlanes); }

            Gl.Enable(Gl.BLEND);
            Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            Gl.Enable(Gl.DEPTH_TEST);
            Gl.DepthMask(false);
            Gl.Enable(Gl.POLYGON_OFFSET_FILL);
            Gl.PolygonOffset(-1f, -4f);
            Gl.DrawArrays(Gl.TRIANGLES, 0, _count);
            Gl.Disable(Gl.POLYGON_OFFSET_FILL);
            Gl.DepthMask(true);
            Gl.Disable(Gl.BLEND);
            Gl.BindVertexArray(0);
        }

        /// <summary>
        /// Releases GL resources.
        /// </summary>
        public void Dispose()
        {
            _program?.Dispose();
            if (_vbo != 0) { Gl.DeleteBuffer(_vbo); }
            if (_vao != 0) { Gl.DeleteVertexArray(_vao); }
        }
    }
}
