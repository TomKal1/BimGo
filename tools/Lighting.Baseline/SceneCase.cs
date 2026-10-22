using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace Lighting.Baseline;

// CA-0 replay format only. Not the future .bimgo calculation-scene schema.
public sealed class SceneCase
{
    public string Name { get; set; } = "";
    public string LengthUnit { get; set; } = "ft";
    public string IlluminanceUnit { get; set; } = "fc";
    public string PhysicalModel { get; set; } = Engine.EngineIdentity.PhysicalModel;
    public double Width { get; set; }
    public double Depth { get; set; }
    public double Height { get; set; }
    public double Workplane { get; set; } = 2.5;
    public double Spacing { get; set; } = 2;
    public double BoundaryOffset { get; set; } = 1;
    public Reflectances Reflectances { get; set; } = new();
    public SolverOptions Options { get; set; } = new();
    // A,B,C (xyz), class, reflectance, tag, in stable input order.
    public List<double[]> Triangles { get; set; } = new();
    public List<SourceInput> Sources { get; set; } = new();

    public (CalcZone Zone, List<SceneTriangle> Triangles, List<Luminaire> Sources) Build()
    {
        if (LengthUnit != "ft" || IlluminanceUnit != "fc" || PhysicalModel != Engine.EngineIdentity.PhysicalModel)
            throw new InvalidDataException("Unsupported units or physical model in baseline input.");
        var zone = new CalcZone { Key = Name, Name = Name, TopZ = Height, WorkplaneHeight = Workplane, Reflectances = Reflectances.Clone() };
        zone.Loops.Add(new() { new(0, 0, 0), new(Width, 0, 0), new(Width, Depth, 0), new(0, Depth, 0) });
        zone.GenerateGrid(Spacing, BoundaryOffset);
        var triangles = Triangles.Select(t => new SceneTriangle(V(t, 0), V(t, 3), V(t, 6), (SurfaceClass)(int)t[9], t[10], (long)t[11])).ToList();
        var sources = Sources.Select((s, i) => new Luminaire(V(s.Position, 0), V(s.Nadir, 0), V(s.Axis0, 0), IesPhotometry.Parse(s.Ies))
        { Id = i + 1, TypeKey = "baseline", Multiplier = s.Multiplier, Watts = 32 }).ToList();
        return (zone, triangles, sources);
    }

    static Vec3 V(double[] a, int i) => new(a[i], a[i + 1], a[i + 2]);

    public static SceneCase Room(string name, double w, double d, double h)
    {
        var s = new SceneCase { Name = name, Width = w, Depth = d, Height = h };
        s.AddBox(Vec3.Zero, new(w, d, h), false);
        return s;
    }

    public void AddBox(Vec3 min, Vec3 max, bool solid)
    {
        double x = min.X, y = min.Y, z = min.Z, X = max.X, Y = max.Y, Z = max.Z;
        void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d, SurfaceClass cls)
        {
            if (solid) cls = SurfaceClass.Furniture;
            Tri(a, solid ? c : b, solid ? b : c, cls);
            Tri(a, solid ? d : c, solid ? c : d, cls);
        }
        void Tri(Vec3 a, Vec3 b, Vec3 c, SurfaceClass cls) => Triangles.Add(new[]
        { a.X,a.Y,a.Z,b.X,b.Y,b.Z,c.X,c.Y,c.Z,(double)cls,-1, (double)Triangles.Count + 1 });
        Quad(new(x,y,z), new(X,y,z), new(X,Y,z), new(x,Y,z), SurfaceClass.Floor);
        Quad(new(x,y,Z), new(x,Y,Z), new(X,Y,Z), new(X,y,Z), SurfaceClass.Ceiling);
        Quad(new(x,y,z), new(x,Y,z), new(x,Y,Z), new(x,y,Z), SurfaceClass.Wall);
        Quad(new(X,y,z), new(X,y,Z), new(X,Y,Z), new(X,Y,z), SurfaceClass.Wall);
        Quad(new(x,y,z), new(x,y,Z), new(X,y,Z), new(X,y,z), SurfaceClass.Wall);
        Quad(new(x,Y,z), new(X,Y,z), new(X,Y,Z), new(x,Y,Z), SurfaceClass.Wall);
    }

    public static string CosineIes(double lumens, double width = 0, double length = 0)
    {
        var b = new StringBuilder("IESNA:LM-63-2002\n[TEST] BimGo CA-0 synthetic cosine fixture\nTILT=NONE\n");
        b.AppendLine(FormattableString.Invariant($"1 -1 1 181 1 1 1 {width} {length} 0\n1 1 32"));
        b.AppendLine(string.Join(" ", Enumerable.Range(0, 181)));
        b.AppendLine("0");
        b.AppendLine(string.Join(" ", Enumerable.Range(0, 181).Select(i => (i < 90 ? lumens / Math.PI * Math.Cos(i * Math.PI / 180) : 0).ToString("R", CultureInfo.InvariantCulture))));
        return b.ToString();
    }

    public static IEnumerable<SceneCase> Corpus()
    {
        var small = Room("small-room", 12, 10, 9);
        small.Sources.Add(new() { Position = new[] { 6.0, 5, 8.9 }, Ies = CosineIes(4000) });
        yield return small;
        var office = Room("open-office", 40, 30, 9);
        for (double x = 4; x < 40; x += 8)
            for (double y = 3.75; y < 30; y += 7.5)
                office.Sources.Add(new() { Position = new[] { x, y, 8.99 }, Ies = CosineIes(4000) });
        yield return office;
        var near = Room("near-field-obstruction", 12, 10, 9);
        near.Sources.Add(new() { Position = new[] { 6.0, 5, 5 }, Ies = CosineIes(4000, 2, 4) });
        near.AddBox(new(0, 0, 0), new(12, 2, 3), true);
        yield return near;
        var asymmetric = Room("asymmetric-source", 12, 10, 9);
        asymmetric.Sources.Add(new() { Position = new[] { 6.0, 5, 8.9 },
            Ies = "IESNA:LM-63-2002\nTILT=NONE\n1 -1 1 3 5 1 1 0 0 0\n1 1 32\n0 45 90\n0 90 180 270 360\n1000 900 0\n1000 400 0\n1000 100 0\n1000 200 0\n1000 900 0\n" });
        yield return asymmetric;
        var connected = Room("connected-opening", 24, 10, 9);
        // Two rooms share an open 4 ft doorway, represented in one transport domain.
        connected.AddBox(new(11.9, 0, 0), new(12.1, 3, 9), true);
        connected.AddBox(new(11.9, 7, 0), new(12.1, 10, 9), true);
        connected.AddBox(new(11.9, 3, 7), new(12.1, 7, 9), true);
        connected.Sources.Add(new() { Position = new[] { 6.0, 5, 8.9 }, Ies = CosineIes(4000) });
        yield return connected;
    }
}

public sealed class SourceInput
{
    public double[] Position { get; set; } = new double[3];
    public double[] Nadir { get; set; } = new[] { 0.0, 0, -1 };
    public double[] Axis0 { get; set; } = new[] { 1.0, 0, 0 };
    public double Multiplier { get; set; } = 0.8;
    public string Ies { get; set; } = "";
}
