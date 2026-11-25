using System.Globalization;
using System.Numerics;
using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "map-diag world [DS3 map ID...]" (RULE 4): every DS3 map's pieces, collisions and walkable floor in DS3's shared
/// world space, straight from DS3's files, so the pieces the port draws where the player walks can be measured across
/// maps (low-detail and horizon models clipping into playable areas). Read-only. Writes, in Work\reports\map-diag\:
/// <list type="bullet">
/// <item>world-pieces.tsv: every map piece: map, part, model, helper kind, world box, vertices, vertices in "low"
/// textured meshes, whether every mesh has DS3's LOD face sets, its DS3 draw groups (8 words hex).</item>
/// <item>world-collisions.tsv: every collision: map, part, model, DS3 display groups and draw groups, the maps its
/// connect collisions name.</item>
/// <item>world-floor.tsv: walkable floor (h collision triangles facing up) sampled on a 4 m grid: map, collision part,
/// point. "What DS3 draws here" is that collision's display groups.</item>
/// <item>world-points.tsv: enemies, player points and objects: map, kind, part, model, point, the collision named.</item>
/// </list>
/// </summary>
public static class MapWorldDiag
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var maps = args.Select(MapId.Parse).ToList();
        var slots = MapSlots.All.Where(s => !s.Probe && (maps.Count == 0 || maps.Contains(s.Ds3))).DistinctBy(s => s.Ds3).ToList();
        var dir = ctx.WorkPath("reports", "map-diag");
        Directory.CreateDirectory(dir);
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var pieces = new StringBuilder("map\tslot\tpart\tmodel\thelper\tminX\tminY\tminZ\tmaxX\tmaxY\tmaxZ\tvertices\tlowVertices\tlod\tdrawGroups\n");
        var collisions = new StringBuilder("map\tpart\tmodel\tdispGroups\tdrawGroups\tconnects\n");
        var floor = new StringBuilder("map\tcollision\tx\ty\tz\n");
        var points = new StringBuilder("map\tkind\tpart\tmodel\tx\ty\tz\tcollision\n");
        var world = new List<MapConnectDisplay.MapData>();
        foreach (var slot in slots)
        {
            try
            {
                world.Add(Dump(ds3, slot, pieces, collisions, floor, points));
                Console.WriteLine($"{slot.Ds3} -> {slot.Er}: done");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"{slot.Ds3} -> {slot.Er}: FAILED: {ex.Message}");
            }
        }
        File.WriteAllText(Path.Combine(dir, "world-pieces.tsv"), pieces.ToString());
        File.WriteAllText(Path.Combine(dir, "world-collisions.tsv"), collisions.ToString());
        File.WriteAllText(Path.Combine(dir, "world-floor.tsv"), floor.ToString());
        File.WriteAllText(Path.Combine(dir, "world-points.tsv"), points.ToString());
        // The doorway display check (MapConnectDisplay): what the port draws over each map's walkable floor where DS3
        // hides it, before and after the doorway lists.
        var check = new StringBuilder();
        var plan = MapConnectDisplay.Build(world);
        var result = MapConnectDisplay.Verify(world, plan);
        check.AppendLine(string.Join(Environment.NewLine, plan.Notes));
        check.AppendLine($"a neighbour's pieces drawn over walkable floor where DS3 hides them: {result.NeighbourBefore:N0} cells with every piece drawn (before this change), {result.NeighbourAfter:N0} with the doorway lists");
        check.AppendLine($"own stand-ins for a neighbour on their own doorway collisions: {result.OwnBefore:N0} before, {result.OwnAfter:N0} after");
        check.AppendLine($"cells where a piece DS3 shows would be hidden: {result.HiddenButShown:N0}");
        foreach (var (m, (b, a)) in result.PerMap.OrderBy(p => p.Key.ToString()))
            check.AppendLine($"  {m}: {b:N0} -> {a:N0}");
        foreach (var l in result.Leftovers)
            check.AppendLine($"  still drawn: {l}");
        foreach (var l in result.WronglyHidden)
            check.AppendLine($"  wrongly hidden: {l}");
        foreach (var (er, mp) in plan.Maps.OrderBy(p => p.Key.ToString()))
            check.AppendLine($"  {er}: {mp.DoorwayLists} doorway lists into it; own pieces hidden on its doorways: {string.Join(", ", mp.BorderHidden.Select(b => $"{b.Key} (over {string.Join("/", b.Value)})"))}"
                             + (mp.Rooms.Count > 0 ? $"; rooms (show only what DS3 shows there): {string.Join(", ", mp.Rooms)}" : ""));
        File.WriteAllText(Path.Combine(dir, "world-check.txt"), check.ToString());
        Console.Write(check.ToString());
        Console.WriteLine($"reports: {dir}\\world-*.tsv, world-check.txt");
        return 0;
    }

    private static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Hex(uint[] g) => string.Concat(g.Select(w => w.ToString("X8")));

    private static MapConnectDisplay.MapData Dump(GameArchives ds3, MapSlots.Slot slot, StringBuilder pieces, StringBuilder collisions, StringBuilder floor, StringBuilder points)
    {
        var map = slot.Ds3;
        var msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(map)));
        bool layerOk = MsbPort.BaseLayerMeans(msb);
        var models = new Dictionary<string, (List<MapModelPort.MeshInfo> Meshes, bool Lod)>(StringComparer.OrdinalIgnoreCase);
        var boxes = new List<MapStandIns.PieceBox>();
        foreach (var p in msb.Parts.MapPieces)
        {
            var number = MapPorter.ModelNumber(p.ModelName);
            if (!models.TryGetValue(number, out var info))
            {
                var path = MapPorter.Ds3MapBndPath(map, number);
                var list = new List<MapModelPort.MeshInfo>();
                bool lod = false;
                if (ds3.Contains(path))
                {
                    lod = true;
                    foreach (var file in BND4.Read(ds3.Read(path)).Files.Where(f => FLVER2.Is(f.Bytes)))
                    {
                        var flver = FLVER2.Read(file.Bytes);
                        list.AddRange(MapModelPort.Describe(flver));
                        lod &= flver.Meshes.Count > 0 && flver.Meshes.All(m => m.FaceSets.Any(f => (f.Flags & FLVER2.FaceSet.FSFlags.LodLevel1) != 0));
                    }
                }
                models[number] = info = (list, lod);
            }
            var drawn = info.Meshes.Where(m => m.Vertices > 0).ToList();
            if (drawn.Count == 0)
            {
                pieces.Append($"{map}\t{slot.Er}\t{p.Name}\t{p.ModelName}\tno model\t\t\t\t\t\t\t0\t0\t0\t{Hex(p.DrawGroups)}\n");
                continue;
            }
            var (min, max) = MapStandIns.WorldBox(p, drawn.Select(m => m.Min).Aggregate(Vector3.Min), drawn.Select(m => m.Max).Aggregate(Vector3.Max));
            int verts = drawn.Sum(m => m.Vertices), low = drawn.Where(m => MapStandIns.IsLowTexture(m.Albedo)).Sum(m => m.Vertices);
            if (MsbPort.HelperKind(p, layerOk) is null)
                boxes.Add(new MapStandIns.PieceBox(p.Name, min, max, verts, low > 0 && low * 2 >= verts));
            pieces.Append($"{map}\t{slot.Er}\t{p.Name}\t{p.ModelName}\t{MsbPort.HelperKind(p, layerOk) ?? ""}\t{F(min.X)}\t{F(min.Y)}\t{F(min.Z)}\t{F(max.X)}\t{F(max.Y)}\t{F(max.Z)}\t{verts}\t{low}\t{(info.Lod ? 1 : 0)}\t{Hex(p.DrawGroups)}\n");
        }

        var connects = msb.Parts.ConnectCollisions.GroupBy(c => c.CollisionName ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(c => string.Join("_", c.MapID.Select(b => b.ToString())))), StringComparer.Ordinal);
        foreach (var c in msb.Parts.Collisions)
            collisions.Append($"{map}\t{c.Name}\t{c.ModelName}\t{Hex(c.DispGroups)}\t{Hex(c.DrawGroups)}\t{connects.GetValueOrDefault(c.Name) ?? ""}\n");

        foreach (var e in msb.Parts.Enemies)
            points.Append($"{map}\tenemy\t{e.Name}\t{e.ModelName}\t{F(e.Position.X)}\t{F(e.Position.Y)}\t{F(e.Position.Z)}\t{e.CollisionName}\n");
        foreach (var e in msb.Parts.Players)
            points.Append($"{map}\tplayer\t{e.Name}\t{e.ModelName}\t{F(e.Position.X)}\t{F(e.Position.Y)}\t{F(e.Position.Z)}\t\n");
        foreach (var e in msb.Parts.Objects)
            points.Append($"{map}\tobject\t{e.Name}\t{e.ModelName}\t{F(e.Position.X)}\t{F(e.Position.Y)}\t{F(e.Position.Z)}\t{e.CollisionName}\n");

        var cells = MapWorldFloor.Sample(ds3, map, msb);
        foreach (var cell in cells)
            floor.Append($"{map}\t{cell.Collision}\t{F(cell.At.X)}\t{F(cell.At.Y)}\t{F(cell.At.Z)}\n");
        // The backdrops, judged as the port judges them, so the plan below is the one a build writes.
        var skip = msb.Parts.MapPieces.Where(p => MsbPort.HelperKind(p, layerOk) is not null).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var judged = MapStandIns.Find(msb, models.ToDictionary(m => m.Key, m => m.Value.Meshes, StringComparer.OrdinalIgnoreCase), skip, cells);
        return new MapConnectDisplay.MapData(map, slot.Er, msb, boxes, cells) { Backdrops = judged.Backdrops };
    }
}
