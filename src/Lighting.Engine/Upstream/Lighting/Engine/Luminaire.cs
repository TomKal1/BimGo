using System;
using System.Collections.Generic;
using WWTools.Calcs.Engine;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// One placed luminaire: where its photometric centre is, how its photometric frame is
    /// oriented, its distribution and the factors applied to it. Positions are in feet.
    /// </summary>
    public sealed class Luminaire
    {
        public Luminaire(Vec3 position, Vec3 nadir, Vec3 axis0, IesPhotometry photometry)
        {
            Position = position;
            Photometry = photometry;
            Nadir = nadir.Normalized();

            // Make the 0° axis perpendicular to the aiming direction, then complete the frame so
            // horizontal angles increase counter-clockwise seen from behind the luminaire (from
            // above for a downlight), matching LM-63 Type C.
            Vec3 a0 = axis0 - Nadir * Vec3.Dot(axis0, Nadir);
            if (a0.LengthSquared < 1e-9)
            {
                Vec3 fallback = Math.Abs(Nadir.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
                a0 = fallback - Nadir * Vec3.Dot(fallback, Nadir);
            }
            Axis0 = a0.Normalized();
            Axis90 = Vec3.Cross(-Nadir, Axis0).Normalized();
        }

        /// <summary>A copy of this luminaire with its photometric centre moved.</summary>
        public Luminaire MovedTo(Vec3 position) =>
            new Luminaire(position, Nadir, Axis0, Photometry)
            {
                Id = Id,
                TypeKey = TypeKey,
                TypeName = TypeName,
                Multiplier = Multiplier,
                Watts = Watts,
            };

        /// <summary>Element id (or any caller id) for reports.</summary>
        public long Id { get; set; }

        /// <summary>Key of the luminaire type this instance belongs to.</summary>
        public string TypeKey { get; set; } = string.Empty;

        public string TypeName { get; set; } = string.Empty;

        public Vec3 Position { get; }

        /// <summary>Aiming direction (photometric 0° vertical).</summary>
        public Vec3 Nadir { get; }

        /// <summary>Photometric 0° horizontal axis (the luminaire's length).</summary>
        public Vec3 Axis0 { get; }

        /// <summary>Photometric 90° horizontal axis.</summary>
        public Vec3 Axis90 { get; }

        public IesPhotometry Photometry { get; }

        /// <summary>Total light loss factor times any user multiplier.</summary>
        public double Multiplier { get; set; } = 1.0;

        /// <summary>Input watts for lighting power density.</summary>
        public double Watts { get; set; }

        /// <summary>Intensity (cd, before <see cref="Multiplier"/>) toward a unit world direction.</summary>
        public double IntensityToward(Vec3 direction)
        {
            double x = Vec3.Dot(direction, Axis0);
            double y = Vec3.Dot(direction, Axis90);
            double z = Vec3.Dot(direction, Nadir);
            return Photometry.CandelaToward(x, y, z);
        }

        /// <summary>
        /// Points the luminous opening is split into when a calculation point is closer than five
        /// times its largest dimension (the far-field limit). Each point carries an equal share
        /// of the intensity.
        /// </summary>
        public IReadOnlyList<Vec3> SubSources
        {
            get
            {
                if (_subSources == null) _subSources = BuildSubSources();
                return _subSources;
            }
        }

        private Vec3[]? _subSources;

        /// <summary>The centre alone, as a one-element list (far field).</summary>
        public IReadOnlyList<Vec3> CentreOnly => _centreOnly ??= new[] { Position };

        private Vec3[]? _centreOnly;

        private Vec3[] BuildSubSources()
        {
            double length = Math.Abs(Photometry.OpeningLength);
            double width = Math.Abs(Photometry.OpeningWidth);
            const double cell = 1.0; // feet per sub-source along each side
            int nl = Math.Max(1, Math.Min(12, (int)Math.Ceiling(length / cell)));
            int nw = Math.Max(1, Math.Min(12, (int)Math.Ceiling(width / cell)));
            if (nl == 1 && nw == 1) return new[] { Position };

            var points = new Vec3[nl * nw];
            int k = 0;
            for (int i = 0; i < nl; i++)
            {
                double u = ((i + 0.5) / nl - 0.5) * length;
                for (int j = 0; j < nw; j++)
                {
                    double v = ((j + 0.5) / nw - 0.5) * width;
                    points[k++] = Position + Axis0 * u + Axis90 * v;
                }
            }
            return points;
        }

        /// <summary>The far-field distance: closer than this, the opening is subdivided.</summary>
        public double NearFieldDistance => 5.0 * Photometry.MaxOpeningDimension;
    }
}
