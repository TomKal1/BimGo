// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Auto exposure, like an eye adapting: fixtures shade in lux (100 lx = full albedo), and a room lit to several hundred
    /// lux, or a model whose fixtures are brighter than they should be, washed out to white. Each frame the light meter
    /// reads the main view's mean brightness (a few frames late, never waiting on the GPU) and the exposure moves towards
    /// the value that brings it to <see cref="EXPOSURE_TARGET"/>, easing over about half a second. It only darkens: at 1
    /// the view is lit exactly as before, so scenes that didn't wash out look the same. Review materials and the plan
    /// keep 1; reflections are exposed once, with the surface that shows them.
    /// </summary>
    internal sealed partial class GameSession
    {
        /// <summary>The mean brightness (luma, 0–1, as displayed) the exposure aims for when the view is brighter.</summary>
        private const float EXPOSURE_TARGET = 0.5f;

        /// <summary>The darkest exposure (−6 EV): enough for a lobby lit to thousands of lux.</summary>
        private const float EXPOSURE_MIN = 1f / 64f;

        /// <summary>Seconds for the exposure to cover about two thirds of a change (an eye takes about this).</summary>
        private const float EXPOSURE_ADAPT = 0.45f;

        /// <summary>The scene's light × this before the shoulder (1 = as lit).</summary>
        private float _exposure = 1f;

        /// <summary>Auto exposure on (off: the exposure stays at 1, the view as lit).</summary>
        private bool _autoExposure = true;

        /// <summary>Seconds since the meter's last reading (it reads every few frames).</summary>
        private float _exposureDt;

        /// <summary>Reads the light meter and eases the exposure towards the target (the main view, after the scene).</summary>
        private void MeterExposure(float dt)
        {
            if (!_autoExposure || _reviewOpen || (ClashViewActive && _clashView != CLASH_VIEW_REAL))
            {
                _exposure = 1f;
                return;
            }
            _exposureDt += MathF.Max(dt, 0f);
            float mean = _renderer.Meter.Measure(_target);
            if (!float.IsFinite(mean)) { return; }
            dt = MathF.Min(_exposureDt, 0.25f);
            _exposureDt = 0f;

            // The picture's brightness grows more slowly than the light near white (the shoulder, clipping), so the step
            // is softened (power 0.7) and repeated frame by frame until the mean settles at the target
            float wanted = _exposure * MathF.Pow(EXPOSURE_TARGET / MathF.Max(mean, 0.01f), 0.7f);
            wanted = Math.Clamp(wanted, EXPOSURE_MIN, 1f);
            float t = 1f - MathF.Exp(-MathF.Max(dt, 0f) / EXPOSURE_ADAPT);
            _exposure = MathF.Exp(MathF.Log(_exposure) + (MathF.Log(wanted) - MathF.Log(_exposure)) * t);
        }
    }
}
