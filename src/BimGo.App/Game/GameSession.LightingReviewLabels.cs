using System.Numerics;
using BimGo.Rendering;
using BimGo.Scene;

namespace BimGo.Game
{
    /// <summary>Distributed, readable calculation values in room and building overviews.</summary>
    internal sealed partial class GameSession
    {
        private readonly ScreenLabelLayout _reviewLabelLayout = new(CALC_MAX_LABELS);
        private ReviewLabel[] _reviewLabels;

        private struct ReviewLabel
        {
            public Vector3 Point;
            public Vector2 Screen;
            public float Value;
        }

        /// <summary>
        /// Project every eligible point before choosing labels. A cell can draw one actual result, with bounded
        /// fallbacks when its preferred point is hidden. Dense source rows cannot starve the rest of the model.
        /// The same layout is used for the review HUD and PNG export; buffers are reused after the first frame.
        /// </summary>
        private void DrawReviewCalcLabels(FontAtlas f)
        {
            _reviewLabels ??= new ReviewLabel[_reviewLabelLayout.SlotCount];
            _reviewLabelLayout.Begin(_window.Width, _window.Height, new Vector2(S(96), S(64)));
            Vector3 eye = Camera.Position, forward = Camera.Forward;
            int key = 0;
            foreach (CalcZoneResult zone in _calc.Zones)
            {
                if (_reviewRoom != null && !ReferenceEquals(zone, _reviewRoom.Zone)) { continue; }
                int count = Math.Min(zone.Points.Length, zone.PointValues.Length);
                for (int i = 0; i < count; i++, key++)
                {
                    float value = zone.PointValues[i];
                    Vector3 p = zone.Points[i];
                    if (!float.IsFinite(value) || SectionCut.IsCut(_sectionPlanes, _sectionCount, p)) { continue; }
                    if (!Camera.WorldToScreen(p, out Vector2 screen)) { continue; }
                    float depth = Vector3.Dot(p - eye, forward);
                    if (depth > FpsCamera.FAR) { continue; }
                    int slot = _reviewLabelLayout.Offer(screen, depth, key);
                    if (slot >= 0) { _reviewLabels[slot] = new ReviewLabel { Point = p, Screen = screen, Value = value }; }
                }
            }

            int drawn = 0;
            bool lux = CalcInLux;
            uint shadow = Rgba.Hex(0x0C0E12, 0.85f);
            float apartX = S(48), apartY = S(24);
            for (int cell = 0; cell < _reviewLabelLayout.CellCount; cell++)
            {
                int slot;
                while ((slot = _reviewLabelLayout.Take(cell)) >= 0)
                {
                    ReviewLabel label = _reviewLabels[slot];
                    Vector2 spot = label.Screen - new Vector2(0, S(18));
                    if (spot.Y < S(8)) { continue; }
                    bool crowded = false;
                    for (int k = 0; k < drawn && !crowded; k++)
                    {
                        crowded = MathF.Abs(_calcLabelSpots[k].X - spot.X) < apartX && MathF.Abs(_calcLabelSpots[k].Y - spot.Y) < apartY;
                    }
                    if (crowded) { continue; }
                    Vector3 origin = Camera.IsOrthographic ? label.Point - forward * Vector3.Dot(label.Point - eye, forward) : eye;
                    Vector3 ray = label.Point - origin;
                    float distance = ray.Length();
                    if (distance < 0.08f || Pick(origin, ray / distance, distance - 0.08f, out _)) { continue; }
                    Text.Clear().Append(LightingCalcs.Display(label.Value, lux), lux ? 0 : 1);
                    _ui.TextCentred(f.Small, spot.X + S(1), spot.Y + S(1), Text.Span, shadow);
                    _ui.TextCentred(f.Small, spot.X, spot.Y, Text.Span, UiTheme.TEXT);
                    _calcLabelSpots[drawn++] = spot;
                    break;
                }
            }
        }
    }
}
