using System;
using System.Collections.Generic;
using System.Linq;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>How a fixture is mounted, in the Luminaire Manager's words. Each picks where its light leaves it.</summary>
    public enum MountingKind
    {
        /// <summary>Worked out per fixture from its geometry and host, when the calc set allows it; else Origin.</summary>
        Auto,
        /// <summary>The family origin, as placed.</summary>
        Origin,
        /// <summary>In a ceiling: 1" below the ceiling face at the origin.</summary>
        Recessed,
        /// <summary>On a ceiling or slab: 1" below the bottom of the fixture.</summary>
        Surface,
        /// <summary>Hung below its mount: 1" below the bottom of the fixture.</summary>
        Pendant,
        /// <summary>On or in a wall: 1" out of the wall face at the origin; a bracket whose shade hangs below, 1" below the shade.</summary>
        Wall,
        /// <summary>Under a cabinet, placed at the level below it: 1" below the cabinet underside at the top of the fixture.</summary>
        UnderCabinet,
        /// <summary>Standing on a floor or roof (bollards, floor lamps): 1 ft below the top of the fixture.</summary>
        Standing,
        /// <summary>The Luminaire Manager's own light centre and offset.</summary>
        Custom,
    }

    /// <summary>What a fixture is hosted to in Revit.</summary>
    public enum FixtureHost { None, Level, Ceiling, Floor, Wall, Face, Other }

    /// <summary>A fixture as placed: its origin, the box of its own solids, and its host.</summary>
    public sealed class FixtureGeometry
    {
        public Vec3 Origin { get; set; }

        /// <summary>Box of the fixture's visible solids (Revit's element box when it has none); null when unknown.</summary>
        public Vec3? BodyMin { get; set; }
        public Vec3? BodyMax { get; set; }

        /// <summary>True when the box came from the fixture's solids, not Revit's element box.</summary>
        public bool BodyFromSolids { get; set; }

        public FixtureHost Host { get; set; }

        /// <summary>Horizontal direction out of the wall the fixture is on; null when it is not on a wall.</summary>
        public Vec3? OutOfWall { get; set; }

        /// <summary>Revit's element box, kept beside the solids box for the log.</summary>
        public Vec3? RevitBoxMin { get; set; }
        public Vec3? RevitBoxMax { get; set; }

        /// <summary>What Revit says the host is (e.g. "Level : L4"), and the family's facing and Z directions, for the log.</summary>
        public string HostDescription { get; set; } = string.Empty;
        public Vec3? Facing { get; set; }
        public Vec3? FamilyZ { get; set; }

        /// <summary>One line for the log: boxes, host and facing.</summary>
        public string Describe()
        {
            string Box(Vec3? min, Vec3? max) => min.HasValue && max.HasValue
                ? $"z {min.Value.Z:0.###}..{max.Value.Z:0.###} ({min.Value} to {max.Value})" : "none";
            return $"solids {(BodyFromSolids ? Box(BodyMin, BodyMax) : "none")}; Revit box {Box(RevitBoxMin, RevitBoxMax)}; " +
                   $"host {Host}{(HostDescription.Length > 0 ? " (" + HostDescription + ")" : string.Empty)}; " +
                   $"facing {(Facing.HasValue ? Facing.Value.ToString() : "-")}; family Z {(FamilyZ.HasValue ? FamilyZ.Value.ToString() : "-")}" +
                   $"{(OutOfWall.HasValue ? "; out of wall " + OutOfWall.Value : string.Empty)}";
        }

        public bool HasBody => BodyMin.HasValue && BodyMax.HasValue;

        /// <summary>"Family : Type", for telling a pendant from a ceiling fixture when the geometry can't.</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>What a fixture's family and type name say it is.</summary>
    public enum NameHint { None, Pendant, Ceiling }

    /// <summary>Where a fixture's light centre went, as what, how sure, and why.</summary>
    public readonly struct MountingPlacement
    {
        public MountingPlacement(Vec3 position, MountingKind kind, bool sure, string reason, Vec3? aim = null, double? movedDownOutOfSolid = null)
        {
            Position = position;
            Kind = kind;
            Sure = sure;
            Reason = reason;
            Aim = aim;
            MovedDownOutOfSolid = movedDownOutOfSolid;
        }

        public Vec3 Position { get; }
        public MountingKind Kind { get; }
        public bool Sure { get; }
        public string Reason { get; }

        /// <summary>Where the mounting says the light points (out of a wall face); null when straight down.</summary>
        public Vec3? Aim { get; }

        /// <summary>
        /// How far (ft) a downward light was dropped out of the slab or ceiling it was buried in, to
        /// light from its underside; null when it was not. The fixture itself belongs that far lower.
        /// </summary>
        public double? MovedDownOutOfSolid { get; }
    }

    /// <summary>
    /// Places light centres by mounting. Auto reads the fixture's own geometry and host: an origin
    /// well below the body is only a placement point (an under-cabinet light placed at its level);
    /// a body hanging well below its origin is a pendant; an origin at a ceiling board is recessed;
    /// a wall host pushes the light out of the wall. When none fits, or the light still ends up
    /// inside something, the placement is marked not sure so the user is told.
    /// </summary>
    public static class LuminaireMounting
    {
        /// <summary>A light 1" clear of the surface it leaves.</summary>
        public const double Clearance = 1.0 / 12;

        /// <summary>A ceiling face this close to the origin is the fixture's own ceiling.</summary>
        public const double Near = 0.25;

        /// <summary>A body reaching this far below its origin hangs: a pendant.</summary>
        public const double Hang = 1.0;

        /// <summary>A body starting this far above its origin was placed below it (at its level).</summary>
        public const double PlacedBelow = 0.5;

        /// <summary>How far a light inside a solid is looked for a way out.</summary>
        public const double Reach = 1.5;

        public static readonly string[] Names = Enum.GetNames(typeof(MountingKind));

        public static MountingKind Parse(string? text) =>
            Enum.TryParse(text, true, out MountingKind kind) ? kind : MountingKind.Auto;

        /// <summary>The light centre and offset a mounting fills into the Luminaire Manager.</summary>
        public static (string Center, double Offset) PresetCentre(MountingKind kind)
        {
            switch (kind)
            {
                case MountingKind.Surface:
                case MountingKind.Pendant:
                case MountingKind.UnderCabinet:
                    return ("BoundingBoxBottom", 0);
                default:
                    return ("Origin", 0);
            }
        }

        /// <summary>
        /// The light centre for a type's mounting. Auto without <paramref name="autoPlace"/> keeps
        /// the origin; Custom uses the configured light centre and offset as before.
        /// <paramref name="isCeiling"/> says which scene tags are Revit ceilings: a fixture in one
        /// is recessed whatever the ceiling's thickness. <paramref name="isFloor"/> says which are
        /// floors (what a pendant must stay clear of); without it any upward face but furniture
        /// and ceilings counts.
        /// </summary>
        public static MountingPlacement Place(MountingKind mounting, bool autoPlace, FixtureGeometry g, string center, double offset, TriangleScene scene,
            Func<long, bool>? isCeiling = null, Func<long, bool>? isFloor = null)
        {
            switch (mounting)
            {
                case MountingKind.Auto:
                    return autoPlace ? Detect(g, scene, isCeiling, isFloor) : new MountingPlacement(g.Origin, MountingKind.Origin, true, "auto placement off: family origin");
                case MountingKind.Custom:
                {
                    Vec3 min = g.BodyMin ?? g.Origin, max = g.BodyMax ?? g.Origin;
                    return new MountingPlacement(CalculationSelection.SourcePosition(g.Origin, min, max, center, offset), MountingKind.Custom, true, "light centre " + center);
                }
                case MountingKind.Recessed:
                    return Checked(Recessed(g, scene, isCeiling) ?? new MountingPlacement(g.Origin, MountingKind.Recessed, false, "no ceiling face at the origin"), scene);
                case MountingKind.Surface:
                case MountingKind.Pendant:
                    return Checked(g.HasBody
                        ? AboveFloor(new MountingPlacement(BodyBottom(g), mounting, true, "fixture bottom"), g, scene, isCeiling, isFloor)
                        : new MountingPlacement(g.Origin, mounting, false, "no fixture geometry: family origin"), scene);
                case MountingKind.UnderCabinet:
                    return Checked(g.HasBody ? UnderCabinet(g, scene, "set as under cabinet")
                        : new MountingPlacement(g.Origin, mounting, false, "no fixture geometry: family origin"), scene);
                case MountingKind.Standing:
                    return Checked(g.HasBody ? Standing(g)
                        : new MountingPlacement(g.Origin, mounting, false, "no fixture geometry: family origin"), scene);
                case MountingKind.Wall:
                    return Checked(Wall(g, scene, g.OutOfWall), scene);
                default:
                    return new MountingPlacement(g.Origin, MountingKind.Origin, true, "family origin");
            }
        }

        /// <summary>Auto: what the fixture's geometry and host say it is (see <see cref="Place"/> for <paramref name="isCeiling"/> and <paramref name="isFloor"/>).</summary>
        public static MountingPlacement Detect(FixtureGeometry g, TriangleScene scene, Func<long, bool>? isCeiling = null, Func<long, bool>? isFloor = null)
        {
            if (g.OutOfWall.HasValue || g.Host == FixtureHost.Wall)
                return Checked(Wall(g, scene, g.OutOfWall), scene);

            MountingPlacement? recessed = Recessed(g, scene, isCeiling);
            if (!g.HasBody)
                return Checked(recessed ?? new MountingPlacement(g.Origin, MountingKind.Origin, false, "no fixture geometry and no ceiling at the origin"), scene);

            Vec3 min = g.BodyMin!.Value, max = g.BodyMax!.Value, o = g.Origin;
            double above = min.Z - o.Z, below = o.Z - min.Z;
            if (above > PlacedBelow)
            {
                string why = $"origin {above:0.##} ft below the fixture (placed at its {HostWord(g.Host)})";
                MountingPlacement under = UnderCabinet(g, scene, why);
                // No cabinet over it (an uplight sconce placed at its level): the fixture's own body.
                return Checked(under.Sure ? under
                    : new MountingPlacement((min + max) * 0.5, MountingKind.Origin, false, why + "; no cabinet over it, so the middle of the fixture"), scene);
            }
            // A body hanging from a ceiling can be a pendant (Art Glass, 36" Drop) or a downlight
            // family's oversized solid (LED15FM12's 2 ft box): the name decides.
            NameHint hint = Hint(g.Name);
            if (hint == NameHint.Pendant && below > Near && o.Z >= max.Z - Near)
                return Checked(AboveFloor(new MountingPlacement(BodyBottom(g), MountingKind.Pendant, true,
                    $"pendant by name, hangs {below:0.##} ft below its origin"), g, scene, isCeiling, isFloor), scene);
            if (recessed.HasValue) return Checked(recessed.Value, scene);
            if (o.Z <= min.Z + Near && max.Z - min.Z > Hang)
                return Checked(Standing(g), scene);
            if (below > Hang && o.Z >= max.Z - Near && hint == NameHint.Ceiling)
            {
                // A ceiling or downlight family on a slab (the parking 1x4 boxes): the solid under it is not the light.
                (double Z, double Thickness, long Tag)? face = MountingFaceAt(scene, o);
                double z = face.HasValue ? face.Value.Z : o.Z;
                return Checked(new MountingPlacement(new Vec3(o.X, o.Y, z - Clearance), MountingKind.Surface, face.HasValue,
                    $"ceiling fixture by name; its solid reaches {below:0.##} ft below its origin, so 1\" under its mounting face at {z:0.###}"), scene);
            }
            if (below > Hang && o.Z >= max.Z - Near)
                return Checked(AboveFloor(new MountingPlacement(BodyBottom(g), MountingKind.Pendant, true, $"fixture hangs {below:0.##} ft below its origin"), g, scene, isCeiling, isFloor), scene);
            if (o.Z >= max.Z - Near)
                return Checked(AboveFloor(new MountingPlacement(BodyBottom(g), MountingKind.Surface, true, "origin at the top of the fixture"), g, scene, isCeiling, isFloor), scene);
            return Checked(new MountingPlacement(o, MountingKind.Origin, false, "origin inside the fixture's height; mounting unclear"), scene);
        }

        private static readonly string[] PendantWords = { "pendant", "chandelier", "drop", "hanging", "suspended", "suspension" };
        private static readonly string[] CeilingWords = { "recessed", "downlight", "down light", "troffer", "ceiling light", "surface", "flush" };

        /// <summary>What a fixture's name says it is: a pendant word wins over a ceiling word.</summary>
        public static NameHint Hint(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return NameHint.None;
            string n = name!.ToLowerInvariant();
            if (PendantWords.Any(n.Contains)) return NameHint.Pendant;
            return CeilingWords.Any(n.Contains) ? NameHint.Ceiling : NameHint.None;
        }

        /// <summary>A body reaching this far above its mounting face is set into it.</summary>
        public const double SetInto = 0.1;

        /// <summary>
        /// Recessed: 1" below the mounting face at the origin, when that face is a ceiling (a Revit
        /// ceiling of any thickness, or any solid as thin as a board), or when the fixture's body
        /// reaches above the face (a housing set into a ceiling or slab; a pendant or surface
        /// fixture hangs entirely below its mount). Null when neither.
        /// </summary>
        private static MountingPlacement? Recessed(FixtureGeometry g, TriangleScene scene, Func<long, bool>? isCeiling)
        {
            (double Z, double Thickness, long Tag)? face = MountingFaceAt(scene, g.Origin);
            if (face == null) return null;
            double z = face.Value.Z;
            // An origin on top of the solid stands on it (a bollard on a thin roof layer), it isn't set into its underside.
            if (g.Origin.Z - z > face.Value.Thickness / 2) return null;
            if (IsCeilingFace(face.Value, isCeiling))
                return new MountingPlacement(new Vec3(g.Origin.X, g.Origin.Y, z - Clearance), MountingKind.Recessed, true, $"in the ceiling at {z:0.###}");
            if (g.HasBody && g.BodyMax!.Value.Z > z + SetInto)
                return new MountingPlacement(new Vec3(g.Origin.X, g.Origin.Y, z - Clearance), MountingKind.Recessed, true,
                    $"housing {g.BodyMax.Value.Z - z:0.##} ft above its mounting face at {z:0.###}");
            return null;
        }

        /// <summary>
        /// Under a cabinet: the fixture's body can reach well below the light bar (some families
        /// carry a 2 ft solid under it), so the light goes 1" below the cabinet underside at the top
        /// of the body; without one, 1" below the body's top, not sure.
        /// </summary>
        private static MountingPlacement UnderCabinet(FixtureGeometry g, TriangleScene scene, string why)
        {
            Vec3 min = g.BodyMin!.Value, max = g.BodyMax!.Value;
            var top = new Vec3((min.X + max.X) / 2, (min.Y + max.Y) / 2, max.Z);
            (double Z, double Thickness, long Tag)? cabinet = MountingFaceAt(scene, top);
            return cabinet.HasValue
                ? new MountingPlacement(new Vec3(top.X, top.Y, cabinet.Value.Z - Clearance), MountingKind.UnderCabinet, true, $"{why}; under the cabinet at {cabinet.Value.Z:0.###}")
                : new MountingPlacement(new Vec3(top.X, top.Y, max.Z - Clearance), MountingKind.UnderCabinet, false, $"{why}; no cabinet underside at the top of the fixture");
        }

        /// <summary>Standing (a bollard, a floor lamp): 1 ft below the top of the fixture, centred.</summary>
        private static MountingPlacement Standing(FixtureGeometry g)
        {
            Vec3 min = g.BodyMin!.Value, max = g.BodyMax!.Value;
            return new MountingPlacement(new Vec3((min.X + max.X) / 2, (min.Y + max.Y) / 2, max.Z - Hang), MountingKind.Standing, true,
                $"stands {max.Z - min.Z:0.##} ft tall on its origin");
        }

        /// <summary>
        /// On a wall: 1" out of the wall face at the origin's height. A bracket whose shade hangs
        /// more than <see cref="Hang"/> below its wall mount lights from 1" below the shade.
        /// </summary>
        private static MountingPlacement Wall(FixtureGeometry g, TriangleScene scene, Vec3? outward)
        {
            Vec3 o = g.Origin;
            if (g.HasBody && o.Z - g.BodyMin!.Value.Z > Hang && o.Z >= g.BodyMax!.Value.Z - Near)
                return new MountingPlacement(BodyBottom(g), MountingKind.Wall, true, $"wall bracket: shade hangs {o.Z - g.BodyMin.Value.Z:0.##} ft below its mount");
            if (outward.HasValue)
            {
                Vec3 n = new Vec3(outward.Value.X, outward.Value.Y, 0).Normalized();
                // The room side is along the family's Z. An origin exactly on the wall face reads as
                // inside, and a ray from it misses that face, so step 1" out first; only a light
                // still in the wall looks for the face ahead, and only then the far side (flagged:
                // that lights the room behind the wall).
                // Set in the wall face (no shade hanging below), it lights out of the wall.
                Vec3 step = o + n * Clearance;
                if (!InsideSolid(scene, step))
                    return new MountingPlacement(step, MountingKind.Wall, true, "on the wall face", n);
                double? ahead = ExitDistance(scene, o, n, Reach);
                if (ahead.HasValue)
                    return new MountingPlacement(o + n * (ahead.Value + Clearance), MountingKind.Wall, true, $"moved {ahead.Value + Clearance:0.##} ft out of the wall", n);
                double? behind = ExitDistance(scene, o, -n, Reach);
                if (behind.HasValue)
                    return new MountingPlacement(o - n * (behind.Value + Clearance), MountingKind.Wall, false,
                        $"no wall face ahead along its facing: moved {behind.Value + Clearance:0.##} ft out of the far side", -n);
                return new MountingPlacement(o, MountingKind.Wall, false, "inside the wall with no face within reach", n);
            }
            Vec3? way = NearestExit(scene, o);
            return way.HasValue
                ? new MountingPlacement(way.Value, MountingKind.Wall, false, "wall-hosted but its facing is unknown: moved to the nearest face")
                : new MountingPlacement(o, MountingKind.Wall, false, "wall-hosted but its facing is unknown");
        }

        /// <summary>
        /// A placement that leaves the light inside a solid is moved out and marked not sure: the
        /// mounting rule did not fit the model. A light pointing down (in a ceiling or under a slab)
        /// drops out of the underside, into the room it is over, and records how far (316 Trash's
        /// LED15FM12, mounted at the top of the slab above it). Any other goes to the nearest face
        /// within reach, or is kept.
        /// </summary>
        private static MountingPlacement Checked(MountingPlacement p, TriangleScene scene)
        {
            if (!InsideSolid(scene, p.Position)) return p;
            if (p.Aim == null && p.Kind != MountingKind.Standing && p.Kind != MountingKind.UnderCabinet)
            {
                double? below = ExitDistance(scene, p.Position, -Vec3.UnitZ, Reach);
                if (below.HasValue)
                {
                    double drop = below.Value + Clearance;
                    Vec3 at = p.Position - Vec3.UnitZ * drop;
                    return new MountingPlacement(at, p.Kind, false,
                        $"{p.Reason}; that is inside the slab or ceiling, so 1\" under its underside at {at.Z + Clearance:0.###}: move the fixture down {drop:0.##} ft",
                        p.Aim, drop);
                }
            }
            Vec3? way = NearestExit(scene, p.Position);
            return way.HasValue
                ? new MountingPlacement(way.Value, p.Kind, false, p.Reason + "; was inside geometry, moved to the nearest face", p.Aim)
                : new MountingPlacement(p.Position, p.Kind, false, p.Reason + "; still inside geometry", p.Aim);
        }

        /// <summary>A pendant or surface light centre stays at least this far above the floor under the fixture (ft).</summary>
        public const double MinAboveFloor = 3.0;

        /// <summary>
        /// A light centre from the bottom of the fixture's body that ends up within
        /// <see cref="MinAboveFloor"/> of the floor (or under it) is not where the light is: the
        /// family carries a tall solid below its origin. It goes 1" under the mounting face at the
        /// origin instead (or stays at the origin), not sure.
        /// </summary>
        private static MountingPlacement AboveFloor(MountingPlacement p, FixtureGeometry g, TriangleScene scene, Func<long, bool>? isCeiling, Func<long, bool>? isFloor)
        {
            double? floor = FloorBelow(scene, g.Origin, isCeiling, isFloor);
            if (floor == null || p.Position.Z >= floor.Value + MinAboveFloor) return p;
            string why = $"{p.Reason}, but that is {p.Position.Z - floor.Value:0.##} ft above the floor at {floor.Value:0.###}";
            (double Z, double Thickness, long Tag)? face = MountingFaceAt(scene, g.Origin);
            return face.HasValue
                ? new MountingPlacement(new Vec3(g.Origin.X, g.Origin.Y, face.Value.Z - Clearance), MountingKind.Recessed, false, $"{why}: under its mounting face at {face.Value.Z:0.###}")
                : new MountingPlacement(g.Origin, MountingKind.Origin, false, why + ": family origin");
        }

        /// <summary>
        /// The top of the first floor straight below a point; null within 60 ft of none. A floor is
        /// what <paramref name="isFloor"/> says, else any upward face but furniture and ceilings: a
        /// pendant passes through the ceiling and joists of the room it hangs into (402's, from the L5 slab).
        /// </summary>
        private static double? FloorBelow(TriangleScene scene, Vec3 p, Func<long, bool>? isCeiling, Func<long, bool>? isFloor)
        {
            var down = new Vec3(0, 0, -1);
            Vec3 start = p + down * 0.01;
            double remaining = 60;
            for (int guard = 0; guard < 32 && remaining > 0; guard++)
            {
                int hit = scene.ClosestHit(start, down, remaining, out double distance, out bool front);
                if (hit < 0) return null;
                Vec3 at = start + down * distance;
                SceneTriangle t = scene.Triangles[hit];
                bool floor = isFloor != null ? isFloor(t.Tag) : t.Class != SurfaceClass.Furniture && isCeiling?.Invoke(t.Tag) != true;
                if (front && scene.NormalOf(hit).Z > 0.7 && floor) return at.Z;
                start = at + down * 0.002;
                remaining -= distance + 0.002;
            }
            return null;
        }

        private static Vec3 BodyBottom(FixtureGeometry g)
        {
            Vec3 min = g.BodyMin!.Value, max = g.BodyMax!.Value;
            return new Vec3((min.X + max.X) / 2, (min.Y + max.Y) / 2, min.Z - Clearance);
        }

        private static string HostWord(FixtureHost host) => host == FixtureHost.Level ? "level" : host == FixtureHost.Floor ? "floor" : "host";

        /// <summary>
        /// The underside of a ceiling at the origin: a downward face within <see cref="Near"/> of it
        /// that belongs to a Revit ceiling (<paramref name="isCeiling"/>), or whose top lies no more
        /// than <see cref="LightingReport.LuminaireBelowFloor"/> above. Any other slab underside is
        /// not a ceiling a fixture is recessed in.
        /// </summary>
        public static double? CeilingBoardAt(TriangleScene scene, Vec3 origin, Func<long, bool>? isCeiling = null)
        {
            (double Z, double Thickness, long Tag)? face = MountingFaceAt(scene, origin);
            return face.HasValue && IsCeilingFace(face.Value, isCeiling) ? face.Value.Z : (double?)null;
        }

        private static bool IsCeilingFace((double Z, double Thickness, long Tag) face, Func<long, bool>? isCeiling) =>
            face.Thickness <= LightingReport.LuminaireBelowFloor || isCeiling?.Invoke(face.Tag) == true;

        /// <summary>
        /// The downward face within <see cref="Near"/> of the origin (a ceiling or slab underside), the
        /// thickness of the solid above it, and its tag.
        /// </summary>
        public static (double Z, double Thickness, long Tag)? MountingFaceAt(TriangleScene scene, Vec3 origin)
        {
            var down = new Vec3(0, 0, -1);
            Vec3 start = origin + new Vec3(0, 0, Near);
            double remaining = 2 * Near;
            double? lastTop = null;
            for (int guard = 0; guard < 16 && remaining > 0; guard++)
            {
                int hit = scene.ClosestHit(start, down, remaining, out double distance, out _);
                if (hit < 0) break;
                Vec3 at = start + down * distance;
                double nz = scene.NormalOf(hit).Z;
                if (nz > 0.7) lastTop = at.Z;
                else if (nz < -0.7)
                {
                    double top = lastTop ?? FaceAbove(scene, at);
                    return (at.Z, top - at.Z, scene.Triangles[hit].Tag);
                }
                start = at + down * 0.002;
                remaining -= distance + 0.002;
            }
            return null;
        }

        /// <summary>The Z of the upward face just above a downward face (the top of that solid).</summary>
        private static double FaceAbove(TriangleScene scene, Vec3 underside)
        {
            int hit = scene.ClosestHit(underside + new Vec3(0, 0, 0.002), Vec3.UnitZ, 10, out double distance, out _);
            return hit >= 0 ? underside.Z + 0.002 + distance : double.PositiveInfinity;
        }

        /// <summary>Inside a closed solid: looking up, the first face is seen from its back.</summary>
        public static bool InsideSolid(TriangleScene scene, Vec3 p)
        {
            int hit = scene.ClosestHit(p, Vec3.UnitZ, 100, out _, out bool front);
            return hit >= 0 && !front;
        }

        /// <summary>From inside a solid, the distance to the face where a ray leaves it; null within reach of none.</summary>
        private static double? ExitDistance(TriangleScene scene, Vec3 p, Vec3 direction, double reach)
        {
            int hit = scene.ClosestHit(p, direction, reach, out double distance, out bool front);
            return hit >= 0 && !front ? distance : (double?)null;
        }

        /// <summary>The nearest way out of a solid along the six axis directions, 1" past the face; null when none is within reach.</summary>
        private static Vec3? NearestExit(TriangleScene scene, Vec3 p)
        {
            Vec3[] directions = { Vec3.UnitX, -Vec3.UnitX, Vec3.UnitY, -Vec3.UnitY, -Vec3.UnitZ, Vec3.UnitZ };
            double best = double.MaxValue;
            Vec3? result = null;
            foreach (Vec3 d in directions)
            {
                double? exit = ExitDistance(scene, p, d, Reach);
                if (exit.HasValue && exit.Value < best)
                {
                    best = exit.Value;
                    result = p + d * (exit.Value + Clearance);
                }
            }
            return result;
        }

        /// <summary>One line per type for the log and report: how its fixtures were placed.</summary>
        public static string Summary(IEnumerable<MountingPlacement> placements)
        {
            var list = placements.ToList();
            string kinds = string.Join(", ", list.GroupBy(p => p.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {Word(g.Key)}"));
            int unsure = list.Count(p => !p.Sure);
            return unsure > 0 ? $"{kinds}; {unsure} not sure" : kinds;
        }

        /// <summary>A placement moved up (or down) by a fixture's own offset; unchanged for 0.</summary>
        public static MountingPlacement Nudged(MountingPlacement p, double nudge) =>
            Math.Abs(nudge) < 1e-9 ? p
                : new MountingPlacement(p.Position + new Vec3(0, 0, nudge), p.Kind, p.Sure, $"{p.Reason}; offset {nudge:0.##} ft", p.Aim, p.MovedDownOutOfSolid);

        public static string Word(MountingKind kind) => kind == MountingKind.UnderCabinet ? "under cabinet" : kind.ToString().ToLowerInvariant();
    }
}
