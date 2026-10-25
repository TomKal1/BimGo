using System.Numerics;
using BimGo.Scene;
using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;

namespace BimGo.Calculations;

/// <summary>Converts saved display results back to the renderer's declared feet/world frame; no recalculation or preview lighting.</summary>
public static class CalculatedImages
{
    public static IReadOnlyList<CalcZone> FromDisplay(LightingCalcResult results, Vector3 origin)
    {
        Vec3 V(Vector3 p) => new((p.X+(double)origin.X)/.3048,(p.Y+(double)origin.Y)/.3048,(p.Z+(double)origin.Z)/.3048);
        var zones = new List<CalcZone>();
        foreach (var saved in results.Zones)
        {
            var zone = new CalcZone { Key=saved.Key,Number=saved.Number,Name=saved.Name,LevelName=saved.Level,
                BottomZ=(saved.BottomZ+(double)origin.Z)/.3048,TopZ=(saved.TopZ+(double)origin.Z)/.3048,CeilingZ=(saved.CeilingZ+(double)origin.Z)/.3048 };
            foreach (var loop in saved.Loops) zone.Loops.Add(loop.Select(p=>V(new Vector3(p,saved.BottomZ))).ToList());
            foreach (var p in saved.Patches)
            {
                Vec3? colour=p.Colour is Vector3 c?new Vec3(c.X,c.Y,c.Z):null;
                zone.Surfaces.Add(new SurfacePatch(new SceneTriangle(V(p.A),V(p.B),V(p.C),(SurfaceClass)((p.Flags>>1)&7),p.Reflectance,p.SourceTag,colour),
                    new Vec3(p.Normal.X,p.Normal.Y,p.Normal.Z),p.Reflectance,p.Illuminance,p.Covered));
            }
            zones.Add(zone);
        }
        return zones;
    }
}
