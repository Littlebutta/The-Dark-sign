using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's light areas, ported as Elden Ring's: a light volume and a reflection cube of their own for every room DS3
/// marks out, baked from DS3's light probes. Devin, 2026-11-13 (after PR #176 and #187): the whole game still
/// "looks very flat with little contrast", lit by "the world base lighting".
/// <para>Why one volume was not enough (measured on the game's files, 2026-11-13):</para>
/// <list type="bullet">
/// <item>Elden Ring draws no sunlight indoors at all: rooms are lit only by a baked light volume (GIIV), a
/// reflection cube (GILM), screen-space ambient occlusion and local lamps (Muhammad A. Moniem, "Behind the Pretty
/// Frames: Elden Ring", mamoniem.com/?p=2011). There are no lightmaps, so the contrast DS3 bakes into its walls has to
/// come from those two.</item>
/// <item>Stormveil (m10_00_00_00.msb and its envmap binders) has 129 environment boxes, one per room or courtyard,
/// each with its own point, volume and cube. Its rooms' cubes are 15 to 150 times darker than the outdoor ones
/// (luminance 0.0002 to 0.002 against 0.03) and their volumes' order-0 alpha sits at 2 to 20 against 50 to 60
/// outdoors. Rooms nest inside larger boxes and carry a larger UnkT34 (catch-all boxes 0, areas 11 to 17, rooms
/// 19 to 34): the box with the larger number wins.</item>
/// <item>The ported maps had one even reflection cube (PR #150) and, from PR #176, one volume over the whole map
/// whose cube is the map's median brightness: every room shone with outdoor reflections.</item>
/// <item>DS3 marks out the same areas in its own layout: m40 31 environment boxes, m30 38, m31 28, with the same
/// fields (UnkT09 10 to 53 where Elden Ring has UnkT34, UnkT0A naming a shared point). They are in layout space like
/// the parts, and DS3's moved probes fall inside them: Firelink's rooms (m40 Env_Box100 to 104) hold 126 to 10,038
/// probes each at a median 0.12 to 0.30 of open sky, the shrine's undercroft (Env_Box400 to 416) 0.00 to 0.02,
/// against 0.6 to 0.7 in the courtyards.</item>
/// </list>
/// So each DS3 box becomes an Elden Ring box of the same number, size, place and turn, with a point, a volume
/// spanning the box (box-local axes, as Stormveil's volumes follow their boxes' width, height and depth) baked from
/// the probes in it, and a reflection cube as bright as those probes against open sky. The map-wide volume
/// (<see cref="MapBakedLight.PointNumber"/>) stays under them for the open ground between rooms.
/// <para>Assumptions not yet seen in game (fallback "map-light-areas" B leaves the areas out): a box's volume runs
/// along the box's own turned axes; a larger UnkT34 wins; first-order terms stay in world directions; a box that
/// names another's point in UnkT0A shares that point's volume and cube, as m45_02's Env_Box000 shares point 010.</para>
/// </summary>
public static class MapLightAreas
{
    /// <summary>A room must hold at least this many probes to get light of its own.</summary>
    public const int MinProbes = 8;
    /// <summary>Texels in a room's high detail volume at most, and the smallest texel (DS3's probes are 1.5 to 2 m apart).</summary>
    public const int MaxTexels = 120_000;
    public const float MinTexel = 1f;
    /// <summary>How far outside its box a probe still shapes the edge of a room's volume.</summary>
    public const float Margin = 3f;
    /// <summary>
    /// Box UnkT34 of the ported rooms: this plus the rank of DS3's own order (UnkT09) among the map's, above the
    /// map-wide volume's, and never past Stormveil's largest (34).
    /// </summary>
    public const short PriorityBase = 10, MaxPriority = 34;
    /// <summary>How dark a room's reflection cube may go, against open sky (Stormveil's darkest rooms: about 0.01 of outdoors).</summary>
    public const float MinCube = 0.005f;

    /// <summary>One of DS3's environment boxes, as the layout places it.</summary>
    public sealed record Area(int Number, Vector3 Position, Vector3 Rotation, Vector3 Size, int Order, int SharesPoint, Vector3? PointPosition);

    /// <summary>The number in a region name ("Env_Box104" -> 104), or -1.</summary>
    public static int NumberOf(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1]))
            i--;
        return i < name.Length && int.TryParse(name[i..], out var n) ? n : -1;
    }

    /// <summary>DS3's environment boxes with a box shape and a number, with the point of the same number where there is one.</summary>
    public static List<Area> Read(MSB3 ds3)
    {
        var points = new Dictionary<int, Vector3>();
        foreach (var p in ds3.Regions.EnvironmentMapPoints)
            if (NumberOf(p.Name) is var n and >= 0)
                points.TryAdd(n, p.Position);
        var areas = new List<Area>();
        foreach (var b in ds3.Regions.EnvironmentMapEffectBoxes)
        {
            int n = NumberOf(b.Name);
            if (n < 0 || b.Shape is not MSB.Shape.Box s || s.Width <= 0 || s.Height <= 0 || s.Depth <= 0 || areas.Any(a => a.Number == n))
                continue;
            areas.Add(new Area(n, b.Position, b.Rotation, new Vector3(s.Width, s.Height, s.Depth), b.UnkT09,
                b.UnkT0A >= 0 && b.UnkT0A != n ? b.UnkT0A : -1, points.TryGetValue(n, out var pp) ? pp : null));
        }
        return areas;
    }

    /// <summary>From layout space into a box's own: x across (width), y up from its floor, z along (depth).</summary>
    public static Matrix4x4 ToLocal(Vector3 position, Vector3 rotationDegrees) =>
        Matrix4x4.CreateTranslation(-position) * Matrix4x4.CreateRotationY(-rotationDegrees.Y * MathF.PI / 180);

    /// <summary>Whether a box-local point lies within the box, grown by <paramref name="grow"/> on every side.</summary>
    public static bool Within(Vector3 local, Vector3 size, float grow = 0) =>
        MathF.Abs(local.X) <= size.X / 2 + grow && MathF.Abs(local.Z) <= size.Z / 2 + grow && local.Y >= -grow && local.Y <= size.Y + grow;

    private static int Round4(float v) => Math.Max(4, (int)MathF.Round(v / 4) * 4);

    /// <summary>The brightness of a probe (largest channel of order 0), as <see cref="MapBakedLight.Bake"/> measures it.</summary>
    private static float Bright(MapBakedLight.Probe p) => MathF.Max(p.Sh[0], MathF.Max(p.Sh[1], p.Sh[2]));

    /// <summary>
    /// Bakes one room: the probes in and near its box spread over a volume spanning the box in the box's own axes,
    /// encoded like the map-wide volume (DS3's open sky <paramref name="sky"/> = <paramref name="hostAlpha"/>), and a
    /// cube as bright as the room's median probe against open sky. Null when the box holds too few probes.
    /// </summary>
    public static MapBakedLight.Volume? Bake(Area area, IReadOnlyList<MapBakedLight.Probe> probes, float hostAlpha, float sky,
        MapBakedLight.Options options)
    {
        var toLocal = ToLocal(area.Position, area.Rotation);
        var near = new List<(Vector3 Position, float[] Sh)>();
        var inside = new List<float>();
        int noSun = 0;
        foreach (var p in probes)
        {
            var q = Vector3.Transform(p.Position, toLocal);
            if (!Within(q, area.Size, Margin))
                continue;
            near.Add((q, p.Sh));
            if (Within(q, area.Size))
            {
                inside.Add(Bright(p));
                if (p.SunMask == 0)
                    noSun++;
            }
        }
        if (inside.Count < MinProbes)
            return null;

        var size = area.Size;
        float texel = MathF.Max(MinTexel, MathF.Cbrt(size.X * size.Y * size.Z / MaxTexels));
        int hw = Round4(size.X / texel), hh = Round4(size.Y / texel), hd = Math.Max(4, (int)MathF.Round(size.Z / texel));
        var min = new Vector3(-size.X / 2, 0, -size.Z / 2);
        float scale = hostAlpha / sky;
        var levels = new Dictionary<string, MapBakedLight.Level>();
        foreach (var (name, div) in new[] { ("high", 1), ("middle", 2), ("low", 4) })
        {
            int w = Round4(hw / (float)div), h = Round4(hh / (float)div), d = Math.Max(1, (int)MathF.Round(hd / (float)div));
            var step = new Vector3(size.X / w, size.Y / h, size.Z / d);
            float radius = MathF.Max(3 * MathF.Max(step.X, MathF.Max(step.Y, step.Z)), 4);
            var field = LightVolumeMath.Spread(near, min, step, w, h, d, 12, radius);
            levels[name] = new MapBakedLight.Level(w, h, d, MapBakedLight.Encode(field, w, h, d, scale, hostAlpha, options));
        }
        inside.Sort();
        float median = inside[inside.Count / 2] / sky;
        var high = levels["high"];
        return new MapBakedLight.Volume
        {
            Number = area.Number,
            Min = min,
            Size = size,
            Levels = levels,
            CubeScale = Math.Clamp(median, MinCube, 1f),
            Summary = FormattableString.Invariant(
                $"{area.Number}: {size.X:0}x{size.Y:0}x{size.Z:0} m, {inside.Count} probes ({noSun} without sun), median {median:0.000} of open sky, {high.W}x{high.H}x{high.D} texels"),
        };
    }

    /// <summary>
    /// The box order Elden Ring reads (UnkT34, larger wins) for a DS3 box's order (UnkT09): its rank among the
    /// map's orders, so DS3's 1 to 144 (m40) keep their order inside the range Elden Ring's own maps use.
    /// </summary>
    public static short Priority(int ds3Order, IReadOnlyList<int> mapOrders)
    {
        var distinct = mapOrders.Distinct().Order().ToList();
        int rank = Math.Max(0, distinct.IndexOf(ds3Order));
        return (short)Math.Min(PriorityBase + rank, MaxPriority);
    }

    /// <summary>
    /// port-maps step (inside <see cref="MapBakedLight.Build"/>): bakes every DS3 room with enough probes and adds its
    /// environment point and box to the layout, copies of <paramref name="hostPoint"/> / <paramref name="hostBox"/>.
    /// Numbers the layout already uses are skipped. Returns the rooms' volumes and a line for the log.
    /// </summary>
    public static (List<MapBakedLight.Volume> Volumes, string Line) Build(MSB3 ds3, IReadOnlyList<MapBakedLight.Probe> probes, MSBE msb,
        MSBE.Region.EnvironmentMapPoint hostPoint, MSBE.Region.EnvironmentMapEffectBox hostBox, float hostAlpha, float sky,
        MapBakedLight.Options options)
    {
        var areas = Read(ds3);
        if (areas.Count == 0)
            return ([], "light areas: the DS3 layout marks out no rooms (no environment boxes)");
        var taken = msb.Regions.EnvironmentMapEffectBoxes.Select(b => NumberOf(b.Name))
            .Concat(msb.Regions.EnvironmentMapPoints.Select(p => NumberOf(p.Name))).ToHashSet();
        var clashes = areas.Where(a => taken.Contains(a.Number)).Select(a => a.Number).ToList();
        areas.RemoveAll(a => taken.Contains(a.Number));

        var orders = areas.Select(a => a.Order).ToList();
        var volumes = new List<MapBakedLight.Volume>();
        var empty = new List<int>();
        var baked = new Dictionary<int, MapBakedLight.Volume>();
        // Rooms with points of their own first; a box that shares another's point is placed after.
        foreach (var area in areas.Where(a => a.SharesPoint < 0))
        {
            if (Bake(area, probes, hostAlpha, sky, options) is { } v)
            {
                baked[area.Number] = v;
                volumes.Add(v);
            }
            else
                empty.Add(area.Number);
        }
        int shared = 0;
        foreach (var area in areas)
        {
            int pointNumber = area.SharesPoint >= 0 && baked.ContainsKey(area.SharesPoint) ? area.SharesPoint : area.Number;
            if (!baked.ContainsKey(pointNumber))
            {
                if (area.SharesPoint >= 0)
                {
                    // Its point's room had no light of its own: bake this one as a room.
                    if (Bake(area with { SharesPoint = -1 }, probes, hostAlpha, sky, options) is { } v)
                    {
                        baked[area.Number] = v;
                        volumes.Add(v);
                        pointNumber = area.Number;
                    }
                    else
                    {
                        empty.Add(area.Number);
                        continue;
                    }
                }
                else
                    continue;
            }
            if (pointNumber != area.Number)
                shared++;
            else
            {
                var point = (MSBE.Region.EnvironmentMapPoint)hostPoint.DeepCopy();
                point.Name = $"Env_Point{area.Number:D3}";
                point.Position = area.PointPosition ?? area.Position + new Vector3(0, area.Size.Y / 2, 0);
                point.Rotation = Vector3.Zero;
                msb.Regions.EnvironmentMapPoints.Add(point);
            }
            var box = (MSBE.Region.EnvironmentMapEffectBox)hostBox.DeepCopy();
            box.Name = $"Env_Box{area.Number:D3}";
            box.Shape = new MSB.Shape.Box { Width = area.Size.X, Height = area.Size.Y, Depth = area.Size.Z };
            box.Position = area.Position;
            box.Rotation = area.Rotation;
            box.EnableDist = 0;
            // Stormveil's rooms blend in over 0.15 to 5 m, most over 1 to 3.
            box.TransitionDist = 2;
            box.UnkT34 = Priority(area.Order, orders);
            box.UnkT0A = (short)(pointNumber != area.Number ? pointNumber : hostBox.UnkT0A >= 0 ? area.Number : -1);
            msb.Regions.EnvironmentMapEffectBoxes.Add(box);
        }

        var line = $"light areas: {volumes.Count} of DS3's {areas.Count + clashes.Count} rooms got a light volume and reflection cube of their own"
                   + (shared > 0 ? $", {shared} more boxes share one" : "")
                   + (empty.Count > 0 ? $"; {empty.Count} left out with fewer than {MinProbes} probes ({string.Join(", ", empty.Take(8))})" : "")
                   + (clashes.Count > 0 ? $"; {clashes.Count} skipped, their number already used ({string.Join(", ", clashes)})" : "");
        if (volumes.Count > 0)
        {
            var cubes = volumes.Select(v => v.CubeScale).Order().ToArray();
            line += FormattableString.Invariant($"; reflections from {cubes[0]:0.000} to {cubes[^1]:0.000} of open sky (median {cubes[cubes.Length / 2]:0.000})");
            line += "; " + string.Join("; ", volumes.Take(6).Select(v => v.Summary)) + (volumes.Count > 6 ? "; ..." : "");
        }
        return (volumes, line);
    }
}
