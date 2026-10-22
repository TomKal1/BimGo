using System.Numerics;

namespace BimGo.Scene
{
    /// <summary>Camera fitting for orthographic model reviews, independent of the renderer.</summary>
    public static class ReviewFraming
    {
        /// <summary>The equal-axis isometric downward angle (radians).</summary>
        public static readonly float IsoPitch = -MathF.Asin(1f / MathF.Sqrt(3f));

        /// <summary>Fits all eight corners, including tall rooms and narrow viewports, with a 12 percent margin.</summary>
        public static float Height(Aabb bounds, Vector3 forward, Vector3 up, float aspect)
        {
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
            up = Vector3.Normalize(Vector3.Cross(right, forward));
            float width = 0f, height = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = new Vector3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                    (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z) - bounds.Center;
                width = MathF.Max(width, MathF.Abs(Vector3.Dot(p, right)) * 2f);
                height = MathF.Max(height, MathF.Abs(Vector3.Dot(p, up)) * 2f);
            }
            return MathF.Max(1f, MathF.Max(height, width / MathF.Max(0.1f, aspect)) * 1.12f);
        }

        /// <summary>A GL-depth orthographic matrix (row vectors; near maps to -1, far to +1).</summary>
        public static Matrix4x4 Orthographic(float width, float height, float near, float far) => new(
            2f / width, 0f, 0f, 0f,
            0f, 2f / height, 0f, 0f,
            0f, 0f, -2f / (far - near), 0f,
            0f, 0f, -(far + near) / (far - near), 1f);
    }
}
