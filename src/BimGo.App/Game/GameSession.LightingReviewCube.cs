using System.Numerics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

namespace BimGo.Game
{
    /// <summary>Always-visible navigation cube: drag to orbit, click faces / edges / corners to orient, HOME for iso.</summary>
    internal sealed partial class GameSession
    {
        private static readonly Vector3[] REVIEW_CUBE_NORMALS =
        {
            Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ
        };
        private static readonly string[] REVIEW_CUBE_LABELS = { "RIGHT", "LEFT", "BACK", "FRONT", "TOP", "BOTTOM" };
        private static readonly int[,] REVIEW_CUBE_FACES =
        {
            { 1, 3, 7, 5 }, { 0, 4, 6, 2 }, { 2, 6, 7, 3 },
            { 0, 1, 5, 4 }, { 4, 5, 7, 6 }, { 0, 2, 3, 1 }
        };
        private readonly Vector2[] _reviewCubePoints = new Vector2[8];
        private readonly bool[] _reviewCubeVisibleVertices = new bool[8];
        private bool _reviewCubeDrag, _reviewCubeMoved;
        private Vector2 _reviewCubeStart, _reviewCubeLast;
        private Vector3 _reviewCubeSnap;

        /// <summary>Outside the scrolling dock and below the room header, in model-viewport coordinates.</summary>
        private Vector4 ReviewCubeRect => new(
            MathF.Max(S(10), _window.Width - S(UiTheme.DOCK_WIDTH + UiTheme.COLUMN_MARGIN + 188)), S(156), S(176), S(210));

        private bool OverReviewCube(Vector2 mouse)
        {
            Vector4 r = ReviewCubeRect;
            return mouse.X >= r.X && mouse.X < r.X + r.Z && mouse.Y >= r.Y && mouse.Y < r.Y + r.W;
        }

        /// <summary>Projects a unit-cube point using the actual camera basis, including straight up/down.</summary>
        private Vector2 ReviewCubeProject(Vector3 p, Vector4 rect)
        {
            Vector3 up = Vector3.Normalize(Vector3.Cross(Camera.Right, Camera.Forward));
            return new Vector2(rect.X + rect.Z * 0.5f + Vector3.Dot(p, Camera.Right) * S(32),
                rect.Y + S(82) - Vector3.Dot(p, up) * S(32));
        }

        private bool ReviewCubeHit(Vector2 mouse, Vector4 rect, out Vector3 hit)
        {
            Vector2 p = new((mouse.X - rect.X - rect.Z * 0.5f) / S(32), (rect.Y + S(82) - mouse.Y) / S(32));
            return ReviewNavigation.Hit(p, Camera.Forward, Vector3.Normalize(Vector3.Cross(Camera.Right, Camera.Forward)), out hit);
        }

        /// <summary>Changing orientation preserves the room, section box, orbit pivot and parallel-view zoom.</summary>
        private void ReviewCubeOrient(Vector3 outward)
        {
            bool fromFly = _reviewView == 3;
            (_reviewYaw, _reviewPitch) = ReviewNavigation.Angles(outward);
            _reviewView = 0;
            if (fromFly) { ReviewFitCamera(); }
            else { UpdateReviewCamera(); }
        }

        private void ReviewCubeOrbit(Vector2 delta)
        {
            if (_reviewView != 0)
            {
                bool fromFly = _reviewView == 3;
                (_reviewYaw, _reviewPitch) = ReviewNavigation.Angles(-Camera.Forward);
                _reviewView = 0;
                if (fromFly) { ReviewFitCamera(); }
            }
            _reviewYaw -= delta.X * 0.01f;
            _reviewPitch = Math.Clamp(_reviewPitch - delta.Y * 0.01f, -1.553f, 1.553f);
        }

        /// <summary>Owns the pointer until release, even when a drag leaves the cube; model picking never sees it.</summary>
        private bool UpdateReviewCube(InputState input)
        {
            Vector2 mouse = input.MousePosition;
            Vector4 r = ReviewCubeRect;
            bool over = mouse.X >= r.X && mouse.X < r.X + r.Z && mouse.Y >= r.Y && mouse.Y < r.Y + r.W;
            if (_reviewCubeDrag)
            {
                if (!input.LeftDown)
                {
                    _reviewCubeDrag = false;
                    if (!_reviewCubeMoved && _reviewCubeSnap != Vector3.Zero) { ReviewCubeOrient(_reviewCubeSnap); }
                    input.ConsumeClicks();
                    return true;
                }
                if (!_reviewCubeMoved && Vector2.DistanceSquared(mouse, _reviewCubeStart) > S(4) * S(4)) { _reviewCubeMoved = true; }
                if (_reviewCubeMoved)
                {
                    ReviewCubeOrbit(mouse - _reviewCubeLast);
                    _reviewCubeLast = mouse;
                }
                return true;
            }
            if (over && mouse.Y < r.Y + S(142) && input.LeftPressed)
            {
                _reviewCubeDrag = true;
                _reviewCubeMoved = false;
                _reviewCubeStart = _reviewCubeLast = mouse;
                _reviewCubeSnap = ReviewCubeHit(mouse, r, out Vector3 hit) ? ReviewNavigation.Snap(hit) : Vector3.Zero;
                input.ConsumeClicks();
            }
            return over;
        }

        /// <summary>Opaque, screen-space cube with visible face focus targets; omitted from PNG exports.</summary>
        private void BuildReviewCube(FontAtlas f, InputState input)
        {
            Vector4 r = ReviewCubeRect;
            _ui.Panel(r.X, r.Y, r.Z, r.W, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _ui.TextCentred(f.Small, r.X + r.Z * 0.5f, r.Y + S(10), "VIEW CUBE", UiTheme.TEXT_SOFT);
            for (int i = 0; i < 8; i++)
            {
                _reviewCubePoints[i] = ReviewCubeProject(new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1), r);
            }
            bool hitCube = ReviewCubeHit(input.MousePosition, r, out Vector3 hit);
            Vector3 snap = hitCube ? ReviewNavigation.Snap(hit) : Vector3.Zero;
            Span<Vector3> preview = stackalloc Vector3[4];
            int previewCount = hitCube && !_reviewCubeDrag ? ReviewNavigation.PreviewVertices(snap, preview) : 0;
            Array.Clear(_reviewCubeVisibleVertices);
            for (int face = 0; face < 6; face++)
            {
                Vector3 normal = REVIEW_CUBE_NORMALS[face];
                float facing = Vector3.Dot(normal, -Camera.Forward);
                if (facing < 0.001f) { continue; }
                bool hovered = previewCount == 4 && Vector3.Dot(snap, normal) > 0f;
                float shade = 0.58f + 0.22f * facing + (normal.Z > 0.5f ? 0.12f : 0f);
                uint fill = hovered ? Rgba.Hex(0x9DE4F2) : Rgba.FromFloat(shade * 0.93f, shade * 0.96f, shade, 1f);
                Vector2 a = _reviewCubePoints[REVIEW_CUBE_FACES[face, 0]], b = _reviewCubePoints[REVIEW_CUBE_FACES[face, 1]];
                Vector2 c = _reviewCubePoints[REVIEW_CUBE_FACES[face, 2]], d = _reviewCubePoints[REVIEW_CUBE_FACES[face, 3]];
                _ui.Triangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, fill);
                _ui.Triangle(a.X, a.Y, c.X, c.Y, d.X, d.Y, fill);
                CubeEdge(a, b); CubeEdge(b, c); CubeEdge(c, d); CubeEdge(d, a);
                for (int corner = 0; corner < 4; corner++) { _reviewCubeVisibleVertices[REVIEW_CUBE_FACES[face, corner]] = true; }
                Vector2 label = (a + b + c + d) * 0.25f;
                RegisterFocus(label.X - S(12), label.Y - S(10), S(24), S(20));
                if (facing > 0.22f)
                {
                    _ui.TextCentred(f.Small, label.X, label.Y - f.Small.LineHeight * 0.5f, REVIEW_CUBE_LABELS[face], Rgba.Hex(0x14202B));
                }
            }
            if (previewCount > 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (_reviewCubeVisibleVertices[i]) { CubeVertex(_reviewCubePoints[i], S(2), Rgba.Hex(0x64748B)); }
                }
                uint accent = Rgba.Hex(0x0891B2);
                for (int i = 0; i < previewCount; i++)
                {
                    Vector2 a = ReviewCubeProject(preview[i], r);
                    if (previewCount > 1)
                    {
                        Vector2 b = ReviewCubeProject(preview[(i + 1) % previewCount], r);
                        _ui.Line(a.X, a.Y, b.X, b.Y, S(previewCount == 2 ? 5 : 2), accent);
                    }
                    CubeVertex(a, S(previewCount == 1 ? 6 : 4), accent);
                }
                if (previewCount == 4) { CubeVertex(ReviewCubeProject(snap, r), S(3), accent); }
            }
            float half = (r.Z - S(24)) * 0.5f;
            if (SmallButton(f, input, r.X + S(8), r.Y + S(146), half, S(28), "HOME"))
            {
                ReviewCubeOrient(new Vector3(-1, 1, 1));
                ReviewFitCamera();
            }
            if (SmallButton(f, input, r.X + S(16) + half, r.Y + S(146), half, S(28), "FIT")) { ReviewFitCamera(); }
            _ui.TextCentred(f.Small, r.X + r.Z * 0.5f, r.Y + S(181), "Drag to rotate", UiTheme.TEXT_MUTED);
            _ui.TextCentred(f.Small, r.X + r.Z * 0.5f, r.Y + S(195),
                previewCount == 1 ? "CORNER · click to snap" : previewCount == 2 ? "EDGE · click to snap"
                    : previewCount == 4 ? "FACE · click to snap" : "Click face / edge / corner", UiTheme.TEXT_MUTED);

            void CubeEdge(Vector2 a, Vector2 b) => _ui.Line(a.X, a.Y, b.X, b.Y, S(1), Rgba.Hex(0x334155));
            void CubeVertex(Vector2 p, float radius, uint colour)
            {
                _ui.Circle(p.X, p.Y, radius + S(1), Rgba.Hex(0xF8FAFC), 16);
                _ui.Circle(p.X, p.Y, radius, colour, 16);
            }
        }
    }
}
