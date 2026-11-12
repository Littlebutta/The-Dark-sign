using System.Globalization;
using System.Numerics;
using System.Text;
using HKLib.hk2018;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "map-diag collision [DS3 map...] [--at x,y,z ...]": the facts behind characters falling through ported map
/// collision while the player stands (RULE 4). Read-only: it reads the game archives and the mod's binders and writes
/// Work\reports\map-diag\collision-&lt;ER slot&gt;.txt.
/// <list type="bullet">
/// <item>Body settings of every collision file, per binder: DS3's h and l, the host colosseum's h and l, Stormveil's h
/// and l (a whole legacy dungeon, for reference) and the mod's h and l. Each distinct (collision filter, flags,
/// collision control, quality, motion type) set is counted, so it shows whether the game tells h from l bodies by
/// their settings and what the port wrote.</item>
/// <item>DS3's collision parts: hit filter, model, whether DS3's h and l binders hold the model.</item>
/// <item>Ground under points: the run start, every DS3 player point and enemy, and the --at points, in map
/// coordinates. For each, the surface right under it in DS3's h and in DS3's l collision. A point with h ground and no
/// l ground is a place where a character that stands on l collision falls.</item>
/// </list>
/// </summary>
public static class MapCollisionDiag
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var maps = new List<MapId>();
        var points = new List<(string Name, Vector3 At)>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--at" && i + 1 < args.Count)
            {
                var v = args[++i].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                points.Add(($"--at {args[i]}", new Vector3(v[0], v[1], v[2])));
            }
            else
                maps.Add(MapId.Parse(args[i]));
        }
        var slots = MapSlots.All.Where(s => maps.Count == 0 || maps.Contains(s.Ds3)).ToList();
        var dir = ctx.WorkPath("reports", "map-diag");
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        foreach (var slot in slots)
        {
            var report = new StringBuilder();
            void Line(string text)
            {
                Console.WriteLine(text);
                report.AppendLine(text);
            }
            Line($"=== {slot.Ds3} -> {slot.Er} (host {slot.Host})");
            try
            {
                Report(ctx, ds3, er, slot, points, Line);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Line($"FAILED: {ex}");
            }
            var path = Path.Combine(dir, $"collision-{slot.Er}.txt");
            File.WriteAllText(path, report.ToString());
            Console.WriteLine($"report: {path}");
        }
        return 0;
    }

    private static void Report(PorterContext ctx, GameArchives ds3, GameArchives er, MapSlots.Slot slot,
        List<(string Name, Vector3 At)> extra, Action<string> line)
    {
        var msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(slot.Ds3)));

        // --- Body settings per binder.
        line("");
        line("--- body settings (filter = m_collisionFilterInfo hex, flags, cntrl = m_collisionCntrl, quality, motion type, material id) ");
        var ds3Files = new Dictionary<string, List<(string Leaf, Ds3.hkRootLevelContainer Root)>>();
        foreach (var kind in new[] { "h", "l" })
        {
            var path = MapCollisions.Ds3BinderPath(slot.Ds3, kind);
            if (!ds3.Contains(path))
            {
                line($"DS3 {kind}: no {path}");
                continue;
            }
            var bxf = BXF4.Read(ds3.Read(path), ds3.Read(path[..^3] + "bdt"));
            var files = new List<(string, Ds3.hkRootLevelContainer)>();
            var sets = new Dictionary<string, int>();
            foreach (var f in bxf.Files.Where(f => f.Name.EndsWith(".hkx.dcx", StringComparison.OrdinalIgnoreCase)))
            {
                var root = CollisionPort.ReadDs3(DCX.Decompress(f.Bytes));
                files.Add((ItemText.Leaf(f.Name), root));
                foreach (var b in Ds3Bodies(root))
                    Count(sets, $"filter {b.m_collisionFilterInfo:X8} flags {b.m_flags:X} quality {b.m_qualityId} material {b.m_materialId}");
            }
            ds3Files[kind] = files;
            line($"DS3 {kind} ({ItemText.Leaf(path)}): {files.Count} files, {sets.Values.Sum()} bodies");
            foreach (var (k, n) in sets.OrderByDescending(s => s.Value).Take(20))
                line($"  {n,5} x {k}");
        }
        foreach (var (label, map) in new[] { ("host", slot.Host), ("Stormveil", MapId.Parse("m10_00_00_00")) })
            foreach (var kind in new[] { "h", "l" })
                ErBinder(line, $"ER {label} {kind}", MapCollisions.ErBinderPath(map, kind), er.Contains(MapCollisions.ErBinderPath(map, kind))
                    ? () => BXF4.Read(er.Read(MapCollisions.ErBinderPath(map, kind)), er.Read(MapCollisions.ErBinderPath(map, kind)[..^3] + "bdt"))
                    : null);
        foreach (var kind in new[] { "h", "l" })
        {
            var bhd = ctx.ModPath(MapPorter.ModParts(MapCollisions.ErBinderPath(slot.Er, kind)));
            ErBinder(line, $"mod {kind}", bhd, File.Exists(bhd) ? () => BXF4.Read(bhd, bhd[..^3] + "bdt") : null);
        }

        // --- Collision parts.
        line("");
        line("--- DS3 collision parts");
        var hNumbers = ds3Files.GetValueOrDefault("h")?.Select(f => MapCollisions.ModelNumberOf(f.Leaf)).ToHashSet() ?? [];
        var lNumbers = ds3Files.GetValueOrDefault("l")?.Select(f => MapCollisions.ModelNumberOf(f.Leaf)).ToHashSet() ?? [];
        line($"hit filters: {string.Join(", ", msb.Parts.Collisions.GroupBy(c => c.HitFilterID).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"))}");
        foreach (var c in msb.Parts.Collisions)
        {
            var number = MapPorter.ModelNumber(c.ModelName);
            line(FormattableString.Invariant($"  {c.Name} model {c.ModelName} filter {c.HitFilterID} at {c.Position.X:0.0},{c.Position.Y:0.0},{c.Position.Z:0.0} rot {c.Rotation.Y:0}")
                 + $" h {(hNumbers.Contains(number) ? "yes" : "NO")} l {(lNumbers.Contains(number) ? "yes" : "NO")}");
        }
        line($"h files no part uses: {string.Join(", ", hNumbers.Except(msb.Parts.Collisions.Select(c => MapPorter.ModelNumber(c.ModelName))).Order())}");
        line($"l files no part uses: {string.Join(", ", lNumbers.Except(msb.Parts.Collisions.Select(c => MapPorter.ModelNumber(c.ModelName))).Order())}");

        // --- Ground under points.
        line("");
        line("--- ground under points (map coordinates; surface heights within 30 m below and 2 m above the point)");
        var surfaces = new Dictionary<string, List<Surface>>();
        foreach (var (kind, files) in ds3Files)
            surfaces[kind] = Place(msb, files);
        var points = new List<(string Name, Vector3 At)>();
        if (MsbPort.ChooseStart(msb, slot.Ds3) is { } start)
            points.Add(($"run start {start.Name}", start.Position));
        points.AddRange(extra);
        points.AddRange(msb.Parts.Players.Select(p => ($"player {p.Name}", p.Position)));
        points.AddRange(msb.Parts.Enemies.Select(e => ($"enemy {e.Name} ({e.ModelName}) on {e.CollisionName}", e.Position)));
        int both = 0, hOnly = 0, lOnly = 0, none = 0;
        var hOnlyNames = new List<string>();
        foreach (var (name, at) in points)
        {
            var h = Ground(surfaces.GetValueOrDefault("h"), at);
            var l = Ground(surfaces.GetValueOrDefault("l"), at);
            if (h is not null && l is not null) both++;
            else if (h is not null) { hOnly++; hOnlyNames.Add(name); }
            else if (l is not null) lOnly++;
            else none++;
            line(FormattableString.Invariant($"  {name} at {at.X:0.0},{at.Y:0.0},{at.Z:0.0}: h {Text(h, at)}; l {Text(l, at)}"));
            // For the points asked for, every surface in the column, so a missing l floor under an h floor shows.
            if (extra.Any(e => e.At == at))
                foreach (var (kind, list) in surfaces)
                    foreach (var (y, file) in Column(list, at, 60))
                        line(FormattableString.Invariant($"      {kind} surface at y {y:0.00} ({at.Y - y:0.0} m below) {file}"));
        }
        line($"summary: {points.Count} points: {both} over h and l, {hOnly} over h only, {lOnly} over l only, {none} over neither");
        if (hOnlyNames.Count > 0)
            line($"over h only: {string.Join("; ", hOnlyNames.Take(30))}");
        foreach (var (kind, list) in surfaces)
            if (list.Count > 0)
            {
                var lo = list.Aggregate(new Vector3(float.MaxValue), (a, s) => Vector3.Min(a, s.Min));
                var hi = list.Aggregate(new Vector3(float.MinValue), (a, s) => Vector3.Max(a, s.Max));
                line(FormattableString.Invariant($"DS3 {kind} collision spans {lo.X:0},{lo.Y:0},{lo.Z:0} .. {hi.X:0},{hi.Y:0},{hi.Z:0} in map coordinates"));
            }
    }

    private static void ErBinder(Action<string> line, string label, string path, Func<BXF4>? open)
    {
        if (open is null)
        {
            line($"{label}: no {path}");
            return;
        }
        try
        {
            var bxf = open();
            var compendiumFile = bxf.Files.FirstOrDefault(f => f.Name.Contains(".compendium", StringComparison.OrdinalIgnoreCase));
            var compendium = compendiumFile is null ? null : DCX.Is(compendiumFile.Bytes) ? DCX.Decompress(compendiumFile.Bytes) : compendiumFile.Bytes;
            var sets = new Dictionary<string, int>();
            int files = 0, failed = 0, xml = 0;
            string? firstError = null;
            foreach (var f in bxf.Files.Where(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase)))
            {
                files++;
                try
                {
                    var bytes = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
                    if (bytes.Length > 0 && bytes[0] == (byte)'<')
                        xml++;
                    var root = CollisionPort.Read(bytes, compendium);
                    foreach (var variant in root.m_namedVariants)
                        if (variant.m_variant is hknpPhysicsSceneData scene)
                            foreach (var system in scene.m_systemDatas)
                                foreach (var b in system?.m_bodyCinfos ?? [])
                                    Count(sets, $"filter {b.m_collisionFilterInfo:X8} flags {b.m_flags:X} cntrl {b.m_collisionCntrl} quality {b.m_qualityId} motion {b.m_motionType} material {b.m_materialId} shape {b.m_shape?.GetType().Name}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed++;
                    firstError ??= $"{ItemText.Leaf(f.Name)}: {ex.Message}";
                }
            }
            line($"{label} ({ItemText.Leaf(path)}): {files} files ({xml} XML), {sets.Values.Sum()} bodies, compendium {(compendium is null ? "none" : $"{compendium.Length} bytes")}"
                 + (failed > 0 ? $", {failed} unreadable (first: {firstError})" : ""));
            foreach (var (k, n) in sets.OrderByDescending(s => s.Value).Take(20))
                line($"  {n,5} x {k}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            line($"{label}: {path} unreadable: {ex.Message}");
        }
    }

    private static IEnumerable<Ds3.hknpBodyCinfo> Ds3Bodies(Ds3.hkRootLevelContainer root) =>
        (root.m_namedVariants ?? []).Select(v => v.m_variant).OfType<Ds3.hknpPhysicsSceneData>()
        .SelectMany(s => s.m_systemDatas ?? []).SelectMany(s => s.m_bodyCinfos ?? []);

    private static void Count(Dictionary<string, int> sets, string key) => sets[key] = sets.GetValueOrDefault(key) + 1;

    private sealed record Surface(string File, Vector3 Min, Vector3 Max, List<(Vector3 A, Vector3 B, Vector3 C)> Triangles);

    /// <summary>Every file's triangles in map coordinates: body transform, then each collision part that uses the model.</summary>
    private static List<Surface> Place(MSB3 msb, List<(string Leaf, Ds3.hkRootLevelContainer Root)> files)
    {
        var result = new List<Surface>();
        foreach (var (leaf, root) in files)
        {
            var number = MapCollisions.ModelNumberOf(leaf);
            var parts = msb.Parts.Collisions.Where(c => MapPorter.ModelNumber(c.ModelName) == number).ToList();
            var bodies = Ds3Bodies(root).ToList();
            var decoded = CollisionPort.Decode(root);
            foreach (var part in parts)
            {
                const float rad = MathF.PI / 180;
                var r = part.Rotation;
                var scale = part.Scale == Vector3.Zero ? Vector3.One : part.Scale;
                var partM = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(r.X * rad) * Matrix4x4.CreateRotationZ(r.Z * rad)
                            * Matrix4x4.CreateRotationY(r.Y * rad) * Matrix4x4.CreateTranslation(part.Position);
                var tris = new List<(Vector3, Vector3, Vector3)>();
                for (int b = 0; b < decoded.Count; b++)
                {
                    var body = bodies.FirstOrDefault(x => x.m_name == decoded[b].Name) ?? (b < bodies.Count ? bodies[b] : null);
                    var bodyM = body is null ? Matrix4x4.Identity
                        : Matrix4x4.CreateFromQuaternion(body.m_orientation == default ? Quaternion.Identity : body.m_orientation)
                          * Matrix4x4.CreateTranslation(body.m_position.X, body.m_position.Y, body.m_position.Z);
                    var m = bodyM * partM;
                    foreach (var (a, bb, c) in decoded[b].Triangles)
                        tris.Add((Vector3.Transform(a, m), Vector3.Transform(bb, m), Vector3.Transform(c, m)));
                }
                if (tris.Count == 0)
                    continue;
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (var (a, b, c) in tris)
                {
                    lo = Vector3.Min(lo, Vector3.Min(a, Vector3.Min(b, c)));
                    hi = Vector3.Max(hi, Vector3.Max(a, Vector3.Max(b, c)));
                }
                result.Add(new Surface($"{leaf} ({part.Name})", lo, hi, tris));
            }
        }
        return result;
    }

    /// <summary>The highest surface under (or up to 2 m above) the point, within 30 m below it.</summary>
    private static (float Y, string File)? Ground(List<Surface>? surfaces, Vector3 p)
    {
        if (surfaces is null)
            return null;
        (float Y, string File)? best = null;
        foreach (var s in surfaces)
        {
            if (p.X < s.Min.X || p.X > s.Max.X || p.Z < s.Min.Z || p.Z > s.Max.Z || p.Y - 30 > s.Max.Y || p.Y + 2 < s.Min.Y)
                continue;
            foreach (var (a, b, c) in s.Triangles)
                if (HeightAt(a, b, c, p.X, p.Z) is { } y && y <= p.Y + 2 && y >= p.Y - 30 && (best is null || y > best.Value.Y))
                    best = (y, s.File);
        }
        return best;
    }

    /// <summary>Every surface crossing the vertical line through the point, within the range above and below it.</summary>
    private static List<(float Y, string File)> Column(List<Surface> surfaces, Vector3 p, float range)
    {
        var hits = new List<(float, string)>();
        foreach (var s in surfaces)
        {
            if (p.X < s.Min.X || p.X > s.Max.X || p.Z < s.Min.Z || p.Z > s.Max.Z || p.Y - range > s.Max.Y || p.Y + range < s.Min.Y)
                continue;
            foreach (var (a, b, c) in s.Triangles)
                if (HeightAt(a, b, c, p.X, p.Z) is { } y && MathF.Abs(y - p.Y) <= range)
                    hits.Add((y, s.File));
        }
        return hits.OrderByDescending(h => h.Item1).ToList();
    }

    private static float? HeightAt(Vector3 a, Vector3 b, Vector3 c, float x, float z)
    {
        var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-9f)
            return null;
        var u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
        var v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
        var w = 1 - u - v;
        if (u < -1e-4f || v < -1e-4f || w < -1e-4f)
            return null;
        return u * a.Y + v * b.Y + w * c.Y;
    }

    private static string Text((float Y, string File)? g, Vector3 at) => g is { } x
        ? FormattableString.Invariant($"{x.Y:0.0} ({at.Y - x.Y:0.0} m below, {x.File})")
        : "NONE";
}
