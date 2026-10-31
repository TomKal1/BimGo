using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Where a fixture without a glowing surface emits, by the calculations' auto-mounting rule (WW Tools'
    /// LuminaireMounting): a family whose origin is at the top of its body (a pendant hung from its canopy, a surface fixture
    /// on a ceiling) lights from just below the body's bottom; any other (a floor lamp's origin is at its base) from near
    /// its top. Exports before this rule put every such fixture near the top of its box, 15 % of its height down: a pendant's
    /// light then sat under the ceiling and lit a bright patch there that the calculations don't have.
    /// <see cref="Corrected"/> recognises that exact placement and moves it. Levels aren't used: in a double-height space
    /// the level under a pendant isn't the floor under it.
    /// </summary>
    public static class FixturePlacement
    {
        /// <summary>An origin this close below the body's top is at its top (m): the engine's ¼ ft.</summary>
        public const float ORIGIN_AT_TOP = 0.0762f;

        /// <summary>How far below a hung fixture's bottom its light goes (m): the engine's 1 inch clearance.</summary>
        public const float BELOW_BOTTOM = 0.0254f;

        /// <summary>True when the family origin is at the top of the body (hung or surface mounted).</summary>
        public static bool HangsFromOrigin(Aabb bounds, Vector3 origin) =>
            bounds.IsValid && origin.Z >= bounds.Max.Z - ORIGIN_AT_TOP && origin.Z <= bounds.Max.Z + ORIGIN_AT_TOP;

        /// <summary>The light of a fixture with no glowing surface: below a hung one's bottom, else near the top.</summary>
        /// <param name="bounds">The fixture's box (scene-local).</param>
        /// <param name="origin">Its family origin (scene-local), or null when unknown.</param>
        public static Vector3 WithoutLens(Aabb bounds, Vector3? origin) =>
            origin is Vector3 o && HangsFromOrigin(bounds, o)
                ? new Vector3(bounds.Center.X, bounds.Center.Y, bounds.Min.Z - BELOW_BOTTOM)
                : OldPlacement(bounds);

        /// <summary>
        /// An exported light's position, corrected when it is an older export's no-lens placement (the box's centre, 15 % of
        /// its height below the top, to the millimetre) in a fixture hung from its origin; else unchanged.
        /// </summary>
        public static Vector3 Corrected(Vector3 position, Aabb bounds, Vector3? origin, out bool moved)
        {
            moved = false;
            if (!bounds.IsValid || origin is not Vector3 o || !HangsFromOrigin(bounds, o)) { return position; }
            if (Vector3.DistanceSquared(position, OldPlacement(bounds)) > 1e-6f) { return position; }
            moved = true;
            return WithoutLens(bounds, o);
        }

        private static Vector3 OldPlacement(Aabb bounds) => new(bounds.Center.X, bounds.Center.Y, bounds.Max.Z - 0.15f * bounds.Size.Z);
    }
}
