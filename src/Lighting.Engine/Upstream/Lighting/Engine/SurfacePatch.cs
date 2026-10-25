using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// One radiosity patch on a zone's own surfaces (its floor, walls, ceiling and furniture,
    /// facing into it) with the light the solve found on it. Kept for the room report's 3D views
    /// and surface statistics.
    /// </summary>
    public struct SurfacePatch
    {
        public Vec3 A;
        public Vec3 B;
        public Vec3 C;

        /// <summary>Unit normal, pointing into the room.</summary>
        public Vec3 Normal;

        public SurfaceClass Class;

        /// <summary>Diffuse reflectance 0–1 used in the solve.</summary>
        public double Reflectance;

        /// <summary>Total illuminance on the patch (fc): direct plus interreflected.</summary>
        public double Illuminance;

        /// <summary>
        /// Something stands on the patch (floor under a cabinet, wall behind casework): it is
        /// left out of the statistics and coloured from its neighbours in the 3D views.
        /// </summary>
        public bool Covered;

        /// <summary>Source element identity and optional sRGB appearance hue. Neither changes the scalar diffuse solve.</summary>
        public long SourceTag;
        public Vec3? Colour;

        public SurfacePatch(SceneTriangle triangle, Vec3 normal, double reflectance, double illuminance, bool covered = false)
        {
            Covered = covered;
            SourceTag = triangle.Tag;
            Colour = triangle.Colour;
            A = triangle.A;
            B = triangle.B;
            C = triangle.C;
            Normal = normal;
            Class = triangle.Class;
            Reflectance = reflectance;
            Illuminance = illuminance;
        }

        public double Area => 0.5 * Vec3.Cross(B - A, C - A).Length;

        public Vec3 Centroid => (A + B + C) / 3.0;

        /// <summary>Exitance ρ·E (lm/ft²); luminance in cd/ft² is this ÷ π.</summary>
        public double Exitance => Reflectance * Illuminance;
    }
}
