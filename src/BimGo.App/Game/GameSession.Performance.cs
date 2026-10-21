using BimGo.Rendering;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round Y-D1: Options → Display → Performance details: a panel under the status panel with the main view's GPU time
    /// per stage (<see cref="GpuTimers"/>, two frames behind, smoothed), the static scene's chunks in view and how many
    /// occlusion culling skipped, triangles drawn of the model's, multi-draws and this frame's occlusion queries.
    /// </summary>
    internal sealed partial class GameSession
    {
        /// <summary>The panel's height (unscaled).</summary>
        private const float PERF_HEIGHT = 150f;

        /// <summary>Draws the panel at (x, y).</summary>
        /// <returns>Its height (scaled), 0 when it is off.</returns>
        private float BuildPerformancePanel(FontAtlas f, float x, float y)
        {
            if (!_showPerf) { return 0f; }
            float w = S(UiTheme.INFO_WIDTH), h = S(PERF_HEIGHT);
            _ui.Panel(x, y, w, h, UiTheme.PANEL, UiTheme.PANEL_BORDER);
            float ix = x + S(14), right = x + w - S(14), row = S(18);
            float cy = y + S(10);

            // Title and GPU total
            GpuTimers timers = _renderer.Timers;
            _ui.Text(f.Small, ix, cy, "PERFORMANCE", UiTheme.TEXT_MUTED, S(1f));
            Text.Clear().Append("GPU ").Append(timers.TotalMs, 1).Append(" ms");
            _ui.TextRight(f.Mono, right, cy, Text.Span, UiTheme.GOOD);
            cy += S(22);

            // Stages, two columns
            float col = (w - S(28)) * 0.5f;
            for (int s = 0; s < GpuTimers.COUNT; s++)
            {
                float sx = ix + (s % 2) * col, sy = cy + (s / 2) * row;
                _ui.Text(f.Small, sx, sy, GpuTimers.NAMES[s], UiTheme.TEXT_FAINT);
                Text.Clear().Append(timers.Ms[s], 1);
                _ui.TextRight(f.Mono, sx + col - S(10), sy, Text.Span, UiTheme.TEXT);
            }
            cy += row * ((GpuTimers.COUNT + 1) / 2) + S(4);

            // Chunks in view, culled
            Text.Clear().Append("Chunks ").AppendGrouped(_renderer.ChunksInView).Append(" in view · ")
                .AppendGrouped(_renderer.ChunksOccluded).Append(" culled");
            if (!_renderer.OcclusionCulling) { Text.Append(_renderer.OcclusionMode == SceneRenderer.OCCLUSION_AUTO ? " (auto: off)" : " (off)"); }
            _ui.Text(f.Small, ix, cy, Text.Span, UiTheme.TEXT_MUTED);
            cy += row;

            // Triangles drawn of the model's, multi-draws
            Text.Clear().Append("Triangles ");
            AppendCount(_renderer.TrianglesDrawn);
            Text.Append(" of ");
            AppendCount(_renderer.TrianglesTotal);
            _ui.Text(f.Small, ix, cy, Text.Span, UiTheme.TEXT_MUTED);
            cy += row;

            Text.Clear().AppendGrouped(_renderer.DrawCalls).Append(" draws · ").AppendGrouped(_renderer.OcclusionQueries).Append(" queries · ")
                .AppendGrouped(_renderer.ChunkCount).Append(" chunks");
            _ui.Text(f.Small, ix, cy, Text.Span, UiTheme.TEXT_FAINT);
            return h;
        }

        /// <summary>Appends a count as "12,345", or "2.3 M" from a million.</summary>
        private void AppendCount(long value)
        {
            if (value >= 1_000_000) { Text.Append(value / 1_000_000.0, 1).Append(" M"); }
            else { Text.AppendGrouped(value); }
        }
    }
}
