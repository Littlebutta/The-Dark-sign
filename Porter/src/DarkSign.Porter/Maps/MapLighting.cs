using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// What lights a ported DS3 map. Up to 1.0.284 a ported map was lit by one thing only, the host's weak secondary
/// light that shines straight down (0.1 to 0.25 against the sun's 0.5 to 0.6): top faces dim, every side face black,
/// no shadows anywhere (2026-11-10 playtests of all three slots). Three things were missing:
/// <list type="bullet">
/// <item>The sun. Elden Ring's map pieces cast shadows whatever their layout flags say (332 of Stormveil's 343
/// pieces have IsShadowSrc off), and DS3's sky is a map piece: an opaque dome some 9 km across around the whole
/// map, so the dome shadowed everything inside it. Unlit DS3 materials no longer cast shadows
/// (<see cref="MapModelPort.MatbinFiles"/>; DS3 lit its sky dome with nothing and let it shadow nothing).</item>
/// <item>Ambient light. Elden Ring takes it from environment maps, each applied inside a box region of the layout
/// (EnvironmentMapEffectBox, with the EnvironmentMapPoint it was captured at). The ported layout is built from
/// scratch and had no such region, and the colosseum's own box (246 x 316 x 350 m around the arena) ends 400 m
/// short of where the Cemetery of Ash begins. <see cref="AddAmbient"/> carries the host's regions over with the
/// box grown to hold the whole DS3 map, as Stormveil's first box holds all of Stormveil (4500 x 4500 x 3500 m).</item>
/// <item>Lamps. DS3 keeps a map's candles, bonfires and torches in a light file (.btl), as Elden Ring does, in
/// an older version of the same format. <see cref="PortLights"/> writes them in the host's version; the host's
/// own lamps (the colosseum's 35 arena lights) go, since they would hang in the air of the DS3 map.</item>
/// </list>
/// The sun, sky colours, fog and exposure stay the host's (its draw params are untouched).
/// "--host-lighting" (launcher Fallbacks "map-lighting" B) leaves all three out.
/// </summary>
public static class MapLighting
{
    // Stormveil's catch-all environment box, the smallest a ported map's box is made.
    private const float MinWidth = 4500, MinHeight = 3500;
    private const float Margin = 500;

    /// <summary>
    /// Copies the host's first environment map box and its capture point into the ported layout, the box grown to
    /// hold everywhere the DS3 map is played and never smaller than Stormveil's catch-all box. Returns a line for
    /// the log.
    /// </summary>
    public static string AddAmbient(MSBE msb, MSBE host, MSB3 ds3)
    {
        var points = host.Regions.EnvironmentMapPoints;
        var boxes = host.Regions.EnvironmentMapEffectBoxes;
        if (points.Count == 0 || boxes.Count == 0)
            return "ambient light: the host map has no environment map region to copy; the map keeps direct light only";

        // Where the map is played: start points, enemies, objects and regions. Map pieces don't say (most sit at
        // the origin with their place baked into the model, and the Cemetery's far mountains stand 7 km out).
        var positions = ds3.Parts.Players.Select(p => p.Position)
            .Concat(ds3.Parts.Enemies.Select(p => p.Position))
            .Concat(ds3.Parts.Objects.Select(p => p.Position))
            .Concat(ds3.Regions.GetEntries().Select(r => r.Position))
            .Append(points[0].Position)
            .ToList();
        var (min, max) = Bounds(positions);
        var size = max - min;
        float width = MathF.Max(MinWidth, size.X + 2 * Margin);
        float depth = MathF.Max(MinWidth, size.Z + 2 * Margin);
        float height = MathF.Max(MinHeight, size.Y + 2 * Margin);
        // A box region stands on its position: the centre of its bottom face.
        var centre = (min + max) / 2;
        var bottom = new Vector3(centre.X, centre.Y - height / 2, centre.Z);

        // One box and the point it was captured at are enough: every box would cover the same space.
        var point = (MSBE.Region.EnvironmentMapPoint)points[0].DeepCopy();
        var box = (MSBE.Region.EnvironmentMapEffectBox)boxes[0].DeepCopy();
        box.Shape = new MSB.Shape.Box { Width = width, Depth = depth, Height = height };
        box.Position = bottom;
        box.Rotation = Vector3.Zero;
        point.ActivationPartName = null;
        box.ActivationPartName = null;
        msb.Regions.EnvironmentMapPoints.Add(point);
        msb.Regions.EnvironmentMapEffectBoxes.Add(box);
        return $"ambient light: the host's environment map ({point.Name}) applies in a {width:F0} x {depth:F0} x {height:F0} m box around the map " +
               $"(the host's own box is {BoxText(boxes[0])})";
    }

    /// <summary>The smallest and largest corner of a set of positions; both zero when it is empty.</summary>
    public static (Vector3 Min, Vector3 Max) Bounds(IReadOnlyCollection<Vector3> positions)
    {
        if (positions.Count == 0)
            return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    /// <summary>True when a box region (standing on its position) holds a point. Rotation is not applied.</summary>
    public static bool Holds(MSBE.Region box, Vector3 p)
    {
        if (box.Shape is not MSB.Shape.Box b)
            return false;
        var o = box.Position;
        return MathF.Abs(p.X - o.X) <= b.Width / 2 && MathF.Abs(p.Z - o.Z) <= b.Depth / 2 && p.Y >= o.Y && p.Y <= o.Y + b.Height;
    }

    private static string BoxText(MSBE.Region r) =>
        r.Shape is MSB.Shape.Box b ? $"{b.Width:F0} x {b.Depth:F0} x {b.Height:F0} m" : r.Shape.GetType().Name;

    public static string Ds3LightPath(MapId id, int n) => $"/map/{id}/{id}_{n:D4}.btl.dcx";
    public static string ErLightPath(MapId id, int n = 0) => $"/map/{id.AreaFolder}/{id}/{id}_{n:D4}.btl.dcx";

    /// <summary>
    /// Writes the DS3 map's lamps as the slot's light file, in the host file's version. Returns a line for the log.
    /// A map with no DS3 light file, or a host with none to take the format from, keeps the host's lights.
    /// </summary>
    public static string PortLights(PorterContext ctx, GameArchives ds3, GameArchives er, MapId ds3Id, MapId erId, MapId hostId)
    {
        var hostPath = ErLightPath(hostId);
        if (!er.Contains(hostPath))
            return $"lamps: the host map has no light file ({hostPath.TrimStart('/')}); none written";
        var sources = Enumerable.Range(0, 10).Select(n => Ds3LightPath(ds3Id, n)).Where(ds3.Contains).ToList();
        if (sources.Count == 0)
            return "lamps: the DS3 map has no light file; the host's lights stay";

        var (hostBytes, compression) = er.ReadWithCompression(hostPath);
        var hostLights = BTL.Read(hostBytes);
        var ds3Lights = sources.SelectMany(path => BTL.Read(ds3.Read(path)).Lights).ToList();
        var ported = Convert(ds3Lights, hostLights.Version, hostLights.LongOffsets, out var skipped);
        var target = ctx.ModPath(MapPorter.ModParts(ErLightPath(erId)));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, DCX.Compress(ported.Write(), compression));
        return $"lamps: {ported.Lights.Count} DS3 lights ({ported.Lights.Count(l => l.Type == BTL.LightType.Point)} point, " +
               $"{ported.Lights.Count(l => l.Type == BTL.LightType.Spot)} spot) replace the host's {hostLights.Lights.Count}" +
               (skipped > 0 ? $"; {skipped} directional left out (the sun is the host's)" : "");
    }

    /// <summary>
    /// DS3 lights (file version 6) as an Elden Ring light file. What a lamp is (kind, colours, strength, reach,
    /// cone, place, flicker, sharpness) is DS3's. The fields Elden Ring's version adds, and the ones that hold one
    /// value in all 251 lights of Stormveil's and the colosseum's files but another in DS3, take Elden Ring's value.
    /// Directional lights are left out: the map's sun comes from the host's draw params.
    /// </summary>
    public static BTL Convert(IEnumerable<BTL.Light> ds3Lights, int version, bool longOffsets, out int skipped)
    {
        var btl = new BTL { Version = version, LongOffsets = longOffsets };
        skipped = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in ds3Lights)
        {
            if (source.Type is not (BTL.LightType.Point or BTL.LightType.Spot))
            {
                skipped++;
                continue;
            }
            var light = source.Clone();
            // Two DS3 light files of one map can both hold a "light_000".
            var name = light.Name;
            for (int n = 2; !names.Add(name); n++)
                name = $"{light.Name}_{n}";
            light.Name = name;
            bool spot = light.Type == BTL.LightType.Spot;
            light.Unk30 = 0;
            light.Unk54 = 0;
            light.Unk5C = -1;
            light.Unk64 = [0, 0, 0, 1];
            light.Unk68 = 0;
            light.Unk70 = 0;
            light.UnkA0 = [0, 0, 2, (byte)(spot ? 1 : 0)];
            light.UnkAC = 1;
            light.UnkC0 = [1, 1, 0, 1];
            light.UnkC4 = 20;
            light.UnkC8 = 1;
            light.UnkCC = 1;
            light.UnkD0 = 0;
            light.UnkD4 = 0;
            light.UnkD8 = 0.2f;
            light.UnkDC = 0;
            light.UnkE0 = 0;
            light.UnkE4 = 1;
            btl.Lights.Add(light);
        }
        return btl;
    }
}
