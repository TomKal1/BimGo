using System;
using System.Collections.Generic;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// The interreflection solve of a group of zones (a cluster): one set of patches and one
    /// converged illuminance per patch, which each member zone then gathers from.
    /// </summary>
    public sealed class SharedSolution
    {
        public SharedSolution(TriangleScene scene, List<SceneTriangle> patches, double[] reflectance, int[] owner,
            double[] illuminance, double[] exitance, bool skipped, SolveStats clusterStats,
            IReadOnlyDictionary<CalcZone, ZoneSolveState> zones)
        {
            Scene = scene;
            Patches = patches;
            Reflectance = reflectance;
            Owner = owner;
            Illuminance = illuminance;
            Exitance = exitance;
            Skipped = skipped;
            ClusterStats = clusterStats;
            Zones = zones;
        }

        public TriangleScene Scene { get; }

        public List<SceneTriangle> Patches { get; }

        public double[] Reflectance { get; }

        /// <summary>The member zone (index) each patch faces, or -1 for a patch that faces none.</summary>
        public int[] Owner { get; }

        /// <summary>Converged illuminance per patch (fc); direct only when the form factors were skipped or interreflection is off.</summary>
        public double[] Illuminance { get; }

        /// <summary>ρ·E per patch, what the final gather adds up.</summary>
        public double[] Exitance { get; }

        /// <summary>No light reached any member zone's points or own surfaces, so the form factors were skipped.</summary>
        public bool Skipped { get; }

        /// <summary>The stages done once for the cluster: patches, BVH, direct on patches, form factors, iterations.</summary>
        public SolveStats ClusterStats { get; }

        public IReadOnlyDictionary<CalcZone, ZoneSolveState> Zones { get; }
    }

    /// <summary>What the cluster solve worked out for one member zone, for its final gather.</summary>
    public sealed class ZoneSolveState
    {
        public ZoneSolveState(bool[] inside, List<(int Index, bool Covered)> ownSurfaces, IReadOnlyList<int> luminaires, SolveStats stats)
        {
            Inside = inside;
            OwnSurfaces = ownSurfaces;
            Luminaires = luminaires;
            Stats = stats;
        }

        /// <summary>Points inside an obstruction, left out (NaN).</summary>
        public bool[] Inside { get; }

        /// <summary>The cluster patches that belong to the zone, and whether each is covered by furniture.</summary>
        public List<(int Index, bool Covered)> OwnSurfaces { get; }

        /// <summary>Indexes into the solver's luminaires that light the zone's points.</summary>
        public IReadOnlyList<int> Luminaires { get; }

        /// <summary>The per-zone part: points, inside, without direct, reached, direct on points and gather.</summary>
        public SolveStats Stats { get; }
    }
}
