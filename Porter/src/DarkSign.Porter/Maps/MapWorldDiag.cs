using System.Globalization;
using System.Numerics;
using System.Text;
using SoulsFormats;
using Ds3 = HKX2;

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
    private const float Grid = 4f;

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
        foreach (var slot in slots)
        {
            try
            {
                Dump(ds3, slot, pieces, collisions, floor, points);
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
        Console.WriteLine($"reports: {dir}\\world-*.tsv");
        return 0;
    }

    private static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Hex(uint[] g) => string.Concat(g.Select(w => w.ToString("X8")));

    private static void Dump(GameArchives ds3, MapSlots.Slot slot, StringBuilder pieces, StringBuilder collisions, StringBuilder floor, StringBuilder points)
    {
        var map = slot.Ds3;
        var msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(map)));
        bool layerOk = MsbPort.BaseLayerMeans(msb);
        var models = new Dictionary<string, (List<MapModelPort.MeshInfo> Meshes, bool Lod)>(StringComparer.OrdinalIgnoreCase);
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

        // Walkable floor: the h collision's upward faces, one sample per 4 m cell per collision part.
        var hPath = MapCollisions.Ds3BinderPath(map, "h");
        if (!ds3.Contains(hPath))
            return;
        var bxf = BXF4.Read(ds3.Read(hPath), ds3.Read(hPath[..^3] + "bdt"));
        foreach (var f in bxf.Files.Where(f => f.Name.EndsWith(".hkx.dcx", StringComparison.OrdinalIgnoreCase)))
        {
            Ds3.hkRootLevelContainer root;
            try { root = CollisionPort.ReadDs3(DCX.Decompress(f.Bytes)); }
            catch (Exception) { continue; }
            var number = MapCollisions.ModelNumberOf(ItemText.Leaf(f.Name));
            var parts = msb.Parts.Collisions.Where(c => MapPorter.ModelNumber(c.ModelName) == number).ToList();
            if (parts.Count == 0)
                continue;
            var bodies = (root.m_namedVariants ?? []).Select(v => v.m_variant).OfType<Ds3.hknpPhysicsSceneData>()
                .SelectMany(s => s.m_systemDatas ?? []).SelectMany(s => s.m_bodyCinfos ?? []).ToList();
            var decoded = CollisionPort.Decode(root);
            foreach (var part in parts)
            {
                const float rad = MathF.PI / 180;
                var r = part.Rotation;
                var scale = part.Scale == Vector3.Zero ? Vector3.One : part.Scale;
                var partM = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(r.X * rad) * Matrix4x4.CreateRotationZ(r.Z * rad)
                            * Matrix4x4.CreateRotationY(r.Y * rad) * Matrix4x4.CreateTranslation(part.Position);
                var cells = new HashSet<(int, int, int)>();
                for (int b = 0; b < decoded.Count; b++)
                {
                    var body = bodies.FirstOrDefault(x => x.m_name == decoded[b].Name) ?? (b < bodies.Count ? bodies[b] : null);
                    var bodyM = body is null ? Matrix4x4.Identity
                        : Matrix4x4.CreateFromQuaternion(body.m_orientation == default ? Quaternion.Identity : body.m_orientation)
                          * Matrix4x4.CreateTranslation(body.m_position.X, body.m_position.Y, body.m_position.Z);
                    var m = bodyM * partM;
                    foreach (var (a0, b0, c0) in decoded[b].Triangles)
                    {
                        var a = Vector3.Transform(a0, m);
                        var bb = Vector3.Transform(b0, m);
                        var c = Vector3.Transform(c0, m);
                        var n = Vector3.Cross(bb - a, c - a);
                        if (n.Length() < 1e-6f)
                            continue;
                        n = Vector3.Normalize(n);
                        if (Math.Abs(n.Y) < 0.7f)
                            continue;
                        // Sample the triangle on a grid finer than the cells, so large floor triangles cover every cell.
                        int steps = Math.Clamp((int)MathF.Ceiling(MathF.Max((bb - a).Length(), MathF.Max((c - bb).Length(), (a - c).Length())) / (Grid / 2)), 1, 200);
                        for (int i = 0; i <= steps; i++)
                        for (int j = 0; i + j <= steps; j++)
                        {
                            var q = a + (bb - a) * (i / (float)steps) + (c - a) * (j / (float)steps);
                            var cell = ((int)MathF.Floor(q.X / Grid), (int)MathF.Floor(q.Y / Grid), (int)MathF.Floor(q.Z / Grid));
                            if (cells.Add(cell))
                                floor.Append($"{map}\t{part.Name}\t{F(q.X)}\t{F(q.Y)}\t{F(q.Z)}\n");
                        }
                    }
                }
            }
        }
    }
}
