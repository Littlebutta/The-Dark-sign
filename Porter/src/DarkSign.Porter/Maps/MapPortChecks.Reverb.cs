using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> ReverbChecks()
    {
        yield return ("reverb: DS3's per-ground reverb becomes non-overlapping Elden Ring reverb regions; a room inside dry ground keeps its reverb, the ground around it stays dry", ReverbRegions);
    }

    private static void ReverbRegions()
    {
        // A flat dry yard 100 x 100 m (two triangles) with a 10 x 10 m hall (type 7) standing on it, 4 m tall.
        static MapReverb.Model Quad(float size, float height)
        {
            var (a, b, c, d) = (new Vector3(0, 0, 0), new Vector3(size, 0, 0), new Vector3(size, 0, size), new Vector3(0, 0, size));
            var tris = new List<(Vector3, Vector3, Vector3)> { (a, b, c), (a, c, d) };
            if (height > 0)
                tris.Add((a, b, new Vector3(size, height, 0))); // a wall, not a floor
            return new MapReverb.Model(Vector3.Zero, new Vector3(size, height, size), tris);
        }
        var ds3 = new MSB3();
        ds3.Parts.Collisions.Add(new MSB3.Part.Collision { Name = "h000000_0000", ModelName = "h000000", HitFilterID = 8 });
        ds3.Parts.Collisions.Add(new MSB3.Part.Collision { Name = "h000100_0000", ModelName = "h000100", HitFilterID = 8, Position = new Vector3(40, 0.2f, 40),
            SoundSpaceType = MSB3.Part.Collision.SoundSpace.ExtraLargeReverbA });
        ds3.Parts.Collisions.Add(new MSB3.Part.Collision { Name = "h000200_0000", ModelName = "h000200", HitFilterID = 9 }); // camera only: skipped
        var models = new Dictionary<string, MapReverb.Model> { ["000000"] = Quad(100, 0), ["000100"] = Quad(10, 4), ["000200"] = Quad(500, 0) };
        var msb = new MSBE();
        var result = MapReverb.Port(ds3, msb, n => models.GetValueOrDefault(n), []);
        Expect(result.Collisions == 2 && result.WithReverb == 1, result.Summary);
        var back = MSBE.Read(msb.Write());
        var regions = back.Regions.SoundRegions.Where(r => r.Name.StartsWith(MapReverb.Prefix, StringComparison.Ordinal)).ToList();
        Expect(regions.Count == result.Regions && regions.Count > 0 && regions.All(r => r.UnkT03 == 255 && r.UnkT04 == -1 && r.UnkT0A == -1 && r.UnkT0C == 255),
            $"{regions.Count} regions read back with only the reverb override set");
        var plan = regions.Select(r =>
        {
            var b = (MSB.Shape.Box)r.Shape;
            var lo = new Vector3(r.Position.X - b.Width / 2, r.Position.Y, r.Position.Z - b.Depth / 2);
            return new MapReverb.Box(lo, lo + new Vector3(b.Width, b.Height, b.Depth), r.UnkT00, r.Name);
        }).ToList();
        Expect(MapReverb.TypeAt(plan, new Vector3(45, 1.7f, 45)) == 7, "standing in the hall: type 7");
        Expect(MapReverb.TypeAt(plan, new Vector3(10, 1.7f, 10)) == 0, "standing in the yard: dry (0), not Elden Ring's automatic reverb");
        Expect(MapReverb.TypeAt(plan, new Vector3(90, 30f, 90)) == 0, "in the air over the map: dry");
        for (int i = 0; i < plan.Count; i++)
            for (int j = i + 1; j < plan.Count; j++)
            {
                var (a, b) = (plan[i], plan[j]);
                bool overlap = a.Min.X < b.Max.X - 1e-3f && b.Min.X < a.Max.X - 1e-3f && a.Min.Y < b.Max.Y - 1e-3f && b.Min.Y < a.Max.Y - 1e-3f
                               && a.Min.Z < b.Max.Z - 1e-3f && b.Min.Z < a.Max.Z - 1e-3f;
                Expect(!overlap, $"regions {a.Source} and {b.Source} overlap");
            }
        MapReverb.AddRegions(back, plan.Take(1).ToList());
        Expect(back.Regions.SoundRegions.Count(r => r.Name.StartsWith(MapReverb.Prefix, StringComparison.Ordinal)) == 1, "a rebuild replaces our regions, never doubles them");
    }
}
