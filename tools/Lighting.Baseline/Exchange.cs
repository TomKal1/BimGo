using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using BimGo.Format;

namespace Lighting.Baseline;

// Saved-run data agreement only: no attempt to infer missing physical inputs.
static class Exchange
{
    public static void Check(string path, string output)
    {
        byte[] header = File.ReadAllBytes(path), body = File.ReadAllBytes(Path.ChangeExtension(path, ".bin"));
        using var doc = JsonDocument.Parse(header);
        var imported = LightingCalcFiles.Parse(header, body, Path.GetFileName(path), Vector3.Zero, DateTime.MinValue, out string error)
            ?? throw new InvalidDataException(error);
        if (imported.Warnings.Count > 0) throw new InvalidDataException(string.Join("; ", imported.Warnings));
        var zones = doc.RootElement.GetProperty("zones");
        if (zones.GetArrayLength() != imported.Zones.Length) throw new InvalidDataException("Dropped zones");
        int points = 0, patches = 0, missingPoints = 0;
        double maxStatsDelta = 0, maxCoordinateErrorMetres = 0;
        float F(int at) => BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(at, 4));
        void Exact(float a, float b) { if (!a.Equals(b)) throw new InvalidDataException($"Stored value differs: {a:R} != {b:R}"); }
        void Coordinates(int at, Vector3 actual)
        {
            for (int axis = 0; axis < 3; axis++) maxCoordinateErrorMetres = Math.Max(maxCoordinateErrorMetres, Math.Abs(F(at+axis*4)*.3048 - actual[axis]));
        }
        for (int z = 0; z < zones.GetArrayLength(); z++)
        {
            var saved = zones[z]; var actual = imported.Zones[z];
            var pointRange = saved.GetProperty("points"); var patchRange = saved.GetProperty("patches");
            int count = pointRange.GetProperty("count").GetInt32(), offset = pointRange.GetProperty("offset").GetInt32();
            if (count != actual.PointValues.Length) throw new InvalidDataException("Dropped points");
            for (int i = 0; i < count; i++)
            {
                Exact(F(offset+i*20+12), actual.PointValues[i]); Coordinates(offset+i*20, actual.Points[i]);
                if (float.IsNaN(actual.PointValues[i])) missingPoints++;
            }
            var finite = actual.PointValues.Where(float.IsFinite).Select(v => (double)v).ToArray();
            if (finite.Length != actual.Stats.Count) throw new InvalidDataException("Statistics count differs");
            foreach (var (expected, value) in new[] { (finite.DefaultIfEmpty(0).Average(), (double)actual.Stats.Average),
                (finite.DefaultIfEmpty(0).Min(), (double)actual.Stats.Minimum), (finite.DefaultIfEmpty(0).Max(), (double)actual.Stats.Maximum) })
            {
                double delta = Math.Abs(expected-value); maxStatsDelta = Math.Max(maxStatsDelta, delta);
                if (delta > 1e-6 + 2e-6*Math.Abs(expected)) throw new InvalidDataException("Statistics exceed JSON/float serialization tolerance");
            }
            points += count; count = patchRange.GetProperty("count").GetInt32(); offset = patchRange.GetProperty("offset").GetInt32();
            if (count != actual.Patches.Length) throw new InvalidDataException("Dropped patches");
            for (int i = 0; i < count; i++)
            {
                int at = offset+i*60; var p = actual.Patches[i];
                Exact(F(at+48), p.Illuminance); Exact(F(at+52), p.Reflectance); Exact(F(at+56), p.Flags);
                Coordinates(at, p.A); Coordinates(at+12, p.B); Coordinates(at+24, p.C);
            }
            patches += count;
        }
        Program.Write(output, new
        {
            Kind = "Saved WWTools v1 result data agreement; no solver or appearance parity claim",
            HeaderSha256 = Convert.ToHexString(SHA256.HashData(header)).ToLowerInvariant(),
            BodySha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), BodyBytes = body.Length,
            imported.Tool, imported.RunUtc, imported.Quality, imported.Interreflections,
            Zones = zones.GetArrayLength(), Points = points, MissingPoints = missingPoints, Patches = patches,
            MaxStatisticsDeltaFc = maxStatsDelta, MaxCoordinateErrorMetres = maxCoordinateErrorMetres,
            PointAndPatchPhotometricValues = "Exact float32 agreement, including NaN point sentinels",
            StatisticsTolerance = "1e-6 fc + 2e-6 relative (JSON rounding and binary float32)",
            MissingInputs = "Full emitter photometry/openings, complete transport geometry and identities, patch/sample policy and engine version. Direct point records are present in v1 but current BimGo DTO discards them."
        });
        Console.WriteLine($"Saved exchange agrees: {zones.GetArrayLength()} zones, {points} points, {patches} patches. This is data agreement, not a scene replay.");
    }
}
