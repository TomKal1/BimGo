using WWTools.Calcs.Engine;
namespace WWTools.Lighting.Engine
{
    /// <summary>Geometry checks on where a luminaire's light leaves it.</summary>
    public static class LuminairePlacement
    {
        /// <summary>
        /// The Z of the lowest downward-facing surface (a ceiling underside) within
        /// <paramref name="reach"/> below, or 0.05 ft above, <paramref name="position"/>; null when
        /// there is none. The ray passes through the ceiling's top face (which faces up) on the way.
        /// </summary>
        public static double? CeilingUndersideBelow(TriangleScene scene, Vec3 position, double reach)
        {
            var down = new Vec3(0, 0, -1);
            Vec3 origin = position + new Vec3(0, 0, 0.05);
            double remaining = reach + 0.05;
            double? underside = null;
            for (int guard = 0; guard < 16 && remaining > 0; guard++)
            {
                int hit = scene.ClosestHit(origin, down, remaining, out double distance, out _);
                if (hit < 0) break;
                Vec3 at = origin + down * distance;
                if (scene.NormalOf(hit).Z < -0.7) underside = at.Z;
                origin = at + down * 0.002;
                remaining -= distance + 0.002;
            }
            return underside;
        }
    }
}
