using System.Numerics;

namespace BimGo.Scene
{
    /// <summary>Orientation and hit testing for the model review's Z-up navigation cube.</summary>
    public static class ReviewNavigation
    {
        /// <summary>Camera angles looking towards the origin from a cube face, edge or corner.</summary>
        public static (float Yaw, float Pitch) Angles(Vector3 outward)
        {
            Vector3 f = outward.LengthSquared() > 1e-8f ? -Vector3.Normalize(outward) : Vector3.Normalize(new Vector3(1, -1, -1));
            return (MathF.Atan2(f.Y, f.X), MathF.Asin(Math.Clamp(f.Z, -1f, 1f)));
        }

        /// <summary>Stable north-up at the poles; elsewhere world Z keeps the horizon level.</summary>
        public static Vector3 Up(Vector3 forward) => MathF.Abs(forward.Z) > 0.9999f ? Vector3.UnitY : Vector3.UnitZ;

        /// <summary>Quantizes the hit to one of the 26 face, edge and corner directions.</summary>
        public static Vector3 Snap(Vector3 hit) => new(
            MathF.Abs(hit.X) >= 0.6f ? MathF.Sign(hit.X) : 0f,
            MathF.Abs(hit.Y) >= 0.6f ? MathF.Sign(hit.Y) : 0f,
            MathF.Abs(hit.Z) >= 0.6f ? MathF.Sign(hit.Z) : 0f);

        /// <summary>One corner, two edge endpoints or four face vertices, in perimeter order for drawing the hover target.</summary>
        public static int PreviewVertices(Vector3 snap, Span<Vector3> vertices)
        {
            if (snap == Vector3.Zero) { return 0; }
            Span<int> free = stackalloc int[2];
            int axes = 0;
            for (int a = 0; a < 3; a++) { if (snap[a] == 0f) { free[axes++] = a; } }
            int count = 1 << axes;
            for (int i = 0; i < count; i++)
            {
                int bits = i ^ (i >> 1); // face perimeter, without a diagonal through the middle
                Vector3 p = snap;
                for (int a = 0; a < axes; a++) { p[free[a]] = (bits & (1 << a)) == 0 ? -1f : 1f; }
                vertices[i] = p;
            }
            return count;
        }

        /// <summary>Moves the camera in its view plane; the grabbed model point follows the pointer in either projection.</summary>
        public static Vector3 Pan(Vector2 pixelDelta, Vector3 right, Vector3 forward, float unitsPerPixel)
        {
            Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
            return (-right * pixelDelta.X + up * pixelDelta.Y) * unitsPerPixel;
        }

        /// <summary>
        /// Hits a [-1,1] cube using parallel rays. Screen coordinates are measured in cube units, right and up positive.
        /// The nearest surface is used, so the hidden back faces can never steal a click.
        /// </summary>
        public static bool Hit(Vector2 screen, Vector3 forward, Vector3 up, out Vector3 hit)
        {
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
            up = Vector3.Normalize(Vector3.Cross(right, forward));
            Vector3 origin = right * screen.X + up * screen.Y - forward * 4f;
            float enter = 0f, leave = float.MaxValue;
            hit = default;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = origin[axis], d = forward[axis];
                if (MathF.Abs(d) < 1e-6f)
                {
                    if (o < -1f || o > 1f) { return false; }
                    continue;
                }
                float a = (-1f - o) / d, b = (1f - o) / d;
                if (a > b) { (a, b) = (b, a); }
                enter = MathF.Max(enter, a);
                leave = MathF.Min(leave, b);
                if (enter > leave) { return false; }
            }
            hit = Vector3.Clamp(origin + forward * enter, new Vector3(-1), new Vector3(1));
            return true;
        }
    }
}
