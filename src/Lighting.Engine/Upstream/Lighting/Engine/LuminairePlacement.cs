using WWTools.Calcs.Engine;
namespace WWTools.Lighting.Engine
{
    /// <summary>Geometry checks on where a luminaire's light leaves it.</summary>
    public static class LuminairePlacement
    {
        /// <summary>
        /// Whether a luminaire above a zone's top stands on the floor of the level above: looking down from it, an
        /// upward-facing surface lies more than <see cref="LightingReport.LuminaireBelowFloor"/> above the zone top.
        /// That keeps a slab top (a slab is thicker than that), and ignores the top of a ceiling at the zone top.
        /// </summary>
        public static bool OnFloorAbove(CalcZone zone, Vec3 position, TriangleScene scene)
        {
            double lowest = zone.TopZ + LightingReport.LuminaireBelowFloor;
            if (position.Z <= lowest) return false;
            var down = new Vec3(0, 0, -1);
            Vec3 origin = position + new Vec3(0, 0, 0.05);
            double remaining = origin.Z - lowest;
            for (int guard = 0; guard < 16 && remaining > 0; guard++)
            {
                int hit = scene.ClosestHit(origin, down, remaining, out double distance, out _);
                if (hit < 0) break;
                if (scene.NormalOf(hit).Z > 0.7) return true;
                Vec3 at = origin + down * distance;
                origin = at + down * 0.002;
                remaining -= distance + 0.002;
            }
            return false;
        }

        /// <summary>
        /// The Z of the lowest downward-facing surface (a ceiling underside) within
        /// <paramref name="reach"/> below, or 0.05 ft above, <paramref name="position"/>; null when
        /// there is none. The ray passes through the ceiling's top face (which faces up) on the way,
        /// but never through a floor: a solid thicker than <see cref="LightingReport.LuminaireBelowFloor"/>
        /// is a slab, and a fixture standing on it stays in its room.
        /// </summary>
        public static double? CeilingUndersideBelow(TriangleScene scene, Vec3 position, double reach)
        {
            var down = new Vec3(0, 0, -1);
            Vec3 origin = position + new Vec3(0, 0, 0.05);
            double remaining = reach + 0.05;
            double? underside = null, top = null;
            for (int guard = 0; guard < 16 && remaining > 0; guard++)
            {
                int hit = scene.ClosestHit(origin, down, remaining, out double distance, out _);
                if (hit < 0) break;
                Vec3 at = origin + down * distance;
                double nz = scene.NormalOf(hit).Z;
                if (nz > 0.7) top = at.Z;
                else if (nz < -0.7)
                {
                    if (top.HasValue && top.Value - at.Z > LightingReport.LuminaireBelowFloor) break;
                    underside = at.Z;
                }
                origin = at + down * 0.002;
                remaining -= distance + 0.002;
            }
            return underside;
        }
    }
}
