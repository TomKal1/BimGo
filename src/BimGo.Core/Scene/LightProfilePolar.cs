using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Round Y-C2: a light profile's polar candela plot (the classic two-plane photometric diagram), as points ready to
    /// join with lines. Plot space: the centre at (0, 0), radius 1 = the peak of the two planes, x to the right, y down,
    /// so gamma 0 (straight down) is at (0, +r) and a downlight's beam hangs below the centre, as on a data sheet.
    /// The C 0–180 plane runs C 0 on the right and C 180 on the left; the C 90–270 plane C 90 right, C 270 left.
    /// </summary>
    public sealed class LightProfilePolar
    {
        /// <summary>Segments per curve (a full turn: gamma 0 → 180 on one side and back on the other).</summary>
        public const int SEGMENTS = 64;

        /// <summary>The C 0–180 curve (<see cref="SEGMENTS"/> + 1 points, closed: the last is the first).</summary>
        public Vector2[] C0 { get; } = new Vector2[SEGMENTS + 1];

        /// <summary>The C 90–270 curve.</summary>
        public Vector2[] C90 { get; } = new Vector2[SEGMENTS + 1];

        /// <summary>The larger of the two planes' peaks, as a share of the whole table's (≤ 1).</summary>
        public float Peak { get; private set; }

        /// <summary>
        /// The plot of a profile, normalised to the larger of its two planes' peaks (so a light whose peak is off both
        /// planes still fills the plot). Null for no profile (the lobe).
        /// </summary>
        public static LightProfilePolar Build(LightProfile profile)
        {
            if (profile == null) { return null; }
            var plot = new LightProfilePolar();
            float peak = 0f;
            for (int i = 0; i <= SEGMENTS; i++)
            {
                Angles(i, 0f, out float gamma, out float c0, out float side);
                Angles(i, 90f, out _, out float c90, out _);
                float a = profile.Sample(gamma, c0), b = profile.Sample(gamma, c90);
                peak = MathF.Max(peak, MathF.Max(a, b));
                float s = MathF.Sin(gamma * MathF.PI / 180f) * side, d = MathF.Cos(gamma * MathF.PI / 180f);
                plot.C0[i] = new Vector2(s * a, d * a);
                plot.C90[i] = new Vector2(s * b, d * b);
            }
            plot.Peak = peak;
            float k = peak > 1e-5f ? 1f / peak : 0f;
            for (int i = 0; i <= SEGMENTS; i++)
            {
                plot.C0[i] *= k;
                plot.C90[i] *= k;
            }
            return plot;
        }

        /// <summary>Point i's gamma (0–180), its C (the plane's first half, or its second) and side (+1 right, -1 left).</summary>
        private static void Angles(int i, float planeC, out float gamma, out float c, out float side)
        {
            float t = i * 360f / SEGMENTS; // 0 → 360 round the plot, from straight down, up the right, down the left
            if (t <= 180f)
            {
                gamma = t;
                c = planeC;
                side = 1f;
            }
            else
            {
                gamma = 360f - t;
                c = planeC + 180f;
                side = -1f;
            }
        }
    }
}
