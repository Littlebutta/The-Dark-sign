using System.Globalization;
using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "map-diag colours [DS3 map...]": the facts behind the colours of ported map surfaces (RULE 4), written to
/// Work\reports\map-diag\colours.txt and a TSV per DS3 map. Read-only: it reads the game archives and writes reports.
/// <list type="bullet">
/// <item>DS3: every texture in each map's area binders, per suffix (_a, _n, _r ...): DDS format and the mean and spread
/// of each channel, so it is known what DS3 keeps in a normal map's blue and alpha channels.</item>
/// <item>DS3: per material type (MTD) of the map's pieces: how many materials, which texture slots they name, and
/// their vertex colours (grass and layered materials keep tints and blend weights there).</item>
/// <item>Elden Ring: the template materials the port copies (params and samplers) and the same channel figures for
/// their textures and for the normal and metallic maps of Stormveil's plain materials, so the two games' conventions
/// can be put side by side (what Elden Ring reads as roughness).</item>
/// </list>
/// </summary>
public static class MapDiag
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0].Equals("gparam", StringComparison.OrdinalIgnoreCase))
            return Gparams(ctx, args.Skip(1).ToList());
        if (args.Count > 0 && args[0].Equals("envmap", StringComparison.OrdinalIgnoreCase))
            return EnvMaps(ctx, args.Skip(1).ToList());
        if (args.Count > 0 && args[0].Equals("collision", StringComparison.OrdinalIgnoreCase))
            return MapCollisionDiag.Run(ctx, args.Skip(1).ToList());
        if (args.Count > 0 && args[0].Equals("stand-ins", StringComparison.OrdinalIgnoreCase))
            return MapStandInsDiag.Run(ctx, args.Skip(1).ToList());
        if (args.Count == 0 || !args[0].Equals("colours", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("map-diag colours [DS3 map ID...]   (default: every slot's DS3 map)");
            Console.Error.WriteLine("map-diag gparam [map ID...]        (default: the slots' hosts, Stormveil and the DS3 maps)");
            Console.Error.WriteLine("map-diag envmap [ER map ID...]     (default: the slots' hosts and Stormveil)");
            Console.Error.WriteLine("map-diag collision [DS3 map ID...] [--at x,y,z ...]   (h/l body settings, collision parts, ground under points)");
            Console.Error.WriteLine("map-diag stand-ins [DS3 map ID...] (low-detail stand-ins and far views the port leaves out, from DS3's files)");
            return 1;
        }
        var maps = args.Skip(1).Select(MapId.Parse).ToList();
        if (maps.Count == 0)
            maps = MapSlots.All.Select(s => s.Ds3).Distinct().ToList();
        var dir = ctx.WorkPath("reports", "map-diag");
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        void Line(string text)
        {
            Console.WriteLine(text);
            report.AppendLine(text);
        }

        using (var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3))
            foreach (var map in maps)
            {
                Line($"=== DS3 {map}");
                Ds3Textures(ds3, map, dir, Line);
                Ds3Materials(ds3, map, Line);
            }
        using (var er = ctx.OpenArchives(PorterGame.EldenRing))
            ErMaterials(er, Line);
        var path = Path.Combine(dir, "colours.txt");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"report: {path}");
        return 0;
    }

    /// <summary>Mean and spread (0-255) of each channel of a texture, read at 256 pixels or less, and its format.</summary>
    public sealed record Channels(string Format, int Width, int Height, double[] Mean, double[] Spread);

    public static Channels? Measure(byte[] dds)
    {
        if (DdsMips.Read(dds) is not { } info)
            return null;
        int levels = 0;
        while (Math.Max(info.Width, info.Height) >> levels > 256)
            levels++;
        var (px, _, _) = WeaponTextures.Decode(DdsMips.Drop(dds, levels, 4));
        var sum = new double[4];
        var sq = new double[4];
        foreach (var c in px)
        {
            double[] v = [c.r, c.g, c.b, c.a];
            for (int i = 0; i < 4; i++)
            {
                sum[i] += v[i];
                sq[i] += v[i] * v[i];
            }
        }
        double n = Math.Max(1, px.Length);
        var mean = sum.Select(s => s / n).ToArray();
        var spread = sq.Select((s, i) => Math.Sqrt(Math.Max(0, s / n - mean[i] * mean[i]))).ToArray();
        return new Channels(FormatName(dds), info.Width, info.Height, mean, spread);
    }

    private static string FormatName(byte[] dds)
    {
        var fourCc = Encoding.ASCII.GetString(dds, 84, 4);
        if (fourCc == "DX10" && dds.Length >= 132)
            return "DXGI" + BitConverter.ToInt32(dds, 128).ToString(CultureInfo.InvariantCulture);
        return fourCc.Trim('\0') is { Length: > 0 } f ? f : "raw";
    }

    private static string Text(Channels c) => FormattableString.Invariant(
        $"{c.Format} {c.Width}x{c.Height} mean {c.Mean[0]:0} {c.Mean[1]:0} {c.Mean[2]:0} {c.Mean[3]:0} spread {c.Spread[0]:0} {c.Spread[1]:0} {c.Spread[2]:0} {c.Spread[3]:0}");

    private static string Suffix(string name)
    {
        var i = name.LastIndexOf('_');
        return i < 0 ? "(none)" : name[i..].ToLowerInvariant();
    }

    private static void Ds3Textures(GameArchives ds3, MapId map, string dir, Action<string> line)
    {
        var files = new List<BinderFile>();
        foreach (var bhd in MapTextures.AreaBinders(ds3, map.AreaFolder))
            files.AddRange(BXF4.Read(ds3.Read(bhd), ds3.Read(bhd[..^3] + "bdt")).Files);
        var rows = new (string Name, Channels? C)[files.Count];
        Parallel.For(0, files.Count, Parallelism.Options, i =>
        {
            var f = files[i];
            var name = MapTextures.TextureNameOf(f.Name);
            try
            {
                var tpf = TPF.Read(DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes);
                rows[i] = (name, tpf.Textures.FirstOrDefault() is { } t ? Measure(t.Bytes) : null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                rows[i] = (name, null);
            }
        });
        var tsv = new StringBuilder("texture\tformat\twidth\theight\tmean r\tmean g\tmean b\tmean a\tspread r\tspread g\tspread b\tspread a\n");
        foreach (var (name, c) in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            tsv.AppendLine(c is null ? $"{name}\tunread" : FormattableString.Invariant(
                $"{name}\t{c.Format}\t{c.Width}\t{c.Height}\t{c.Mean[0]:0}\t{c.Mean[1]:0}\t{c.Mean[2]:0}\t{c.Mean[3]:0}\t{c.Spread[0]:0}\t{c.Spread[1]:0}\t{c.Spread[2]:0}\t{c.Spread[3]:0}"));
        File.WriteAllText(Path.Combine(dir, $"textures-{map}.tsv"), tsv.ToString());

        line($"  textures: {files.Count} in {map.AreaFolder}'s area binders ({rows.Count(r => r.C is null)} unread); by suffix, average of each texture's mean / spread (r g b a):");
        foreach (var group in rows.Where(r => r.C is not null).GroupBy(r => Suffix(r.Name)).OrderByDescending(g => g.Count()).Take(12))
        {
            var cs = group.Select(r => r.C!).ToList();
            string Avg(Func<Channels, double[]> pick) => string.Join(" ", Enumerable.Range(0, 4).Select(i => cs.Average(c => pick(c)[i]).ToString("0", CultureInfo.InvariantCulture)));
            var formats = string.Join(", ", cs.GroupBy(c => c.Format).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} x{g.Count()}"));
            line($"    {group.Key} x{cs.Count}: mean {Avg(c => c.Mean)}, spread {Avg(c => c.Spread)}; {formats}");
            foreach (var (name, c) in group.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where((_, i) => i % Math.Max(1, cs.Count / 4) == 0).Take(4))
                line($"      e.g. {name}: {Text(c!)}");
        }
    }

    private static void Ds3Materials(GameArchives ds3, MapId map, Action<string> line)
    {
        var prefix = $"/map/{map}/";
        var binders = ds3.Entries.Where(e => e.Path is not null && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && e.Path.EndsWith(".mapbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).OrderBy(p => p).ToList();
        // Every binder when the archive names them; otherwise the MSB's models.
        if (binders.Count == 0)
        {
            var msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(map)));
            binders = msb.Models.MapPieces.Select(m => MapPorter.Ds3MapBndPath(map, MapPorter.ModelNumber(m.Name))).Where(ds3.Contains).Distinct().ToList();
        }
        var stats = new Dictionary<string, MtdStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in binders)
        {
            BND4 bnd;
            try { bnd = BND4.Read(ds3.Read(path)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            foreach (var f in bnd.Files.Where(f => FLVER2.Is(f.Bytes)))
            {
                var flver = FLVER2.Read(f.Bytes);
                for (int mi = 0; mi < flver.Materials.Count; mi++)
                {
                    var m = flver.Materials[mi];
                    var mtd = Path.GetFileNameWithoutExtension(ItemText.Leaf(m.MTD ?? ""));
                    if (!stats.TryGetValue(mtd, out var s))
                        stats[mtd] = s = new MtdStats();
                    s.Materials++;
                    s.Slots.Add(string.Join(",", m.Textures.Where(t => t.Path.Length > 0).Select(t => t.ParamName)));
                    if (s.Example is null && m.Textures.Any(t => t.Path.Length > 0))
                        s.Example = string.Join(", ", m.Textures.Where(t => t.Path.Length > 0)
                            .Select(t => $"{t.ParamName}={Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path))}"
                                         + (t.TilingScale != System.Numerics.Vector2.One ? FormattableString.Invariant($" x{t.TilingScale.X:0.##},{t.TilingScale.Y:0.##}") : "")));
                    foreach (var mesh in flver.Meshes.Where(x => x.MaterialIndex == mi))
                        foreach (var v in mesh.Vertices)
                        {
                            s.Vertices++;
                            s.Uvs[Math.Min(v.UVs.Count, 4)]++;
                            if (v.Colors.Count == 0)
                                continue;
                            var c = v.Colors[0];
                            s.Coloured++;
                            s.Sum[0] += c.R; s.Sum[1] += c.G; s.Sum[2] += c.B; s.Sum[3] += c.A;
                            if (c.R < 0.98f || c.G < 0.98f || c.B < 0.98f)
                                s.Tinted++;
                            if (c.A < 0.98f)
                                s.Faded++;
                        }
                }
            }
        }
        line($"  materials: {binders.Count} map piece binders, {stats.Count} material types (MTD), by vertices:");
        foreach (var (mtd, s) in stats.OrderByDescending(kv => kv.Value.Vertices).Take(30))
        {
            double n = Math.Max(1, s.Coloured);
            line(FormattableString.Invariant(
                $"    {mtd}: {s.Materials} materials, {s.Vertices:N0} vertices; colour mean rgba {s.Sum[0] / n:0.00} {s.Sum[1] / n:0.00} {s.Sum[2] / n:0.00} {s.Sum[3] / n:0.00}, ")
                + FormattableString.Invariant($"{100.0 * s.Tinted / n:0}% not white, {100.0 * s.Faded / n:0}% alpha under 1, {100.0 * (s.Vertices - s.Coloured) / Math.Max(1, s.Vertices):0}% none; ")
                + $"UV channels {string.Join(" ", s.Uvs.Select((c, i) => (c, i)).Where(x => x.c > 0).Select(x => $"{x.i}:{x.c}"))}");
            foreach (var slots in s.Slots.Take(3))
                line($"      slots: {slots}");
            if (s.Example is not null)
                line($"      e.g. {s.Example}");
        }
    }

    private sealed class MtdStats
    {
        public int Materials;
        public long Vertices, Coloured, Tinted, Faded;
        public readonly long[] Uvs = new long[5];
        public readonly double[] Sum = new double[4];
        public readonly HashSet<string> Slots = new(StringComparer.Ordinal);
        public string? Example;
    }

    private static void ErMaterials(GameArchives er, Action<string> line)
    {
        line("=== Elden Ring");
        var matbins = MapPorter.AllMatbins(er);
        // Texture binders by every name they could be found under: "aet210_896" for AET210_896_n.tif.
        var tpfs = er.Entries.Where(e => e.Path is not null && e.Path.EndsWith(".tpf.dcx", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => ItemText.Leaf(e.Path!)[..^".tpf.dcx".Length], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Path!, StringComparer.OrdinalIgnoreCase);
        line($"  {matbins.Count} material files, {tpfs.Count} named texture binders");

        Channels? Texture(string samplerPath)
        {
            var stem = Path.GetFileNameWithoutExtension(ItemText.Leaf(samplerPath));
            if (stem.Length == 0)
                return null;
            var owner = stem.LastIndexOf('_') is var i and > 0 ? stem[..i] : stem;
            foreach (var key in new[] { stem, owner })
                if (tpfs.TryGetValue(key, out var path))
                {
                    var tpf = TPF.Read(er.Read(path));
                    if (tpf.Textures.FirstOrDefault(t => t.Name.Equals(stem, StringComparison.OrdinalIgnoreCase)) is { } t)
                        return Measure(t.Bytes);
                }
            return null;
        }

        line("  template materials the port copies:");
        foreach (var name in new[] { "m10_00_030", "m10_00_397", "m10_00_311", "m10_00_003" })
        {
            if (!matbins.TryGetValue(name, out var file))
            {
                line($"    {name}: not found");
                continue;
            }
            var m = MATBIN.Read(file.Bytes);
            line($"    {name}: shader {ItemText.Leaf(m.ShaderPath ?? "")}");
            foreach (var p in m.Params)
                line($"      param {p.Name} = {(p.Value is Array a ? string.Join(",", a.Cast<object>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture))) : Convert.ToString(p.Value, CultureInfo.InvariantCulture))}");
            foreach (var s in m.Samplers)
            {
                Channels? c = null;
                try { c = Texture(s.Path); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
                line(FormattableString.Invariant($"      sampler {s.Type} = {ItemText.Leaf(s.Path)} scale {s.Unk14.X:0.##},{s.Unk14.Y:0.##}: {(c is null ? "texture not found" : Text(c))}"));
            }
        }

        // The normal and metallic maps of Elden Ring's plain map materials (one layer), averaged as above.
        var bySuffix = new Dictionary<string, List<(string Name, Channels C)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, file) in matbins.Where(kv => kv.Key.StartsWith("m10_", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key).Take(400))
        {
            MATBIN m;
            try { m = MATBIN.Read(file.Bytes); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (MapModelPort.ClassifyRanked(m) is null)
                continue;
            foreach (var s in m.Samplers.Where(s => s.Path.Length > 0 && MapModelPort.Role(s.Type) is ("normal" or "metallic" or "albedo", 0)))
            {
                var suffix = MapModelPort.Role(s.Type)!.Value.Role;
                if (!bySuffix.TryGetValue(suffix, out var list))
                    bySuffix[suffix] = list = [];
                if (list.Count >= 40 || list.Any(x => x.Name.Equals(ItemText.Leaf(s.Path), StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    if (Texture(s.Path) is { } c)
                        list.Add((ItemText.Leaf(s.Path), c));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
        }
        line("  Stormveil's plain one-layer materials, their textures by role (average mean / spread, r g b a):");
        foreach (var (role, list) in bySuffix)
        {
            if (list.Count == 0)
                continue;
            string Avg(Func<Channels, double[]> pick) => string.Join(" ", Enumerable.Range(0, 4).Select(i => list.Average(x => pick(x.C)[i]).ToString("0", CultureInfo.InvariantCulture)));
            line($"    {role} x{list.Count}: mean {Avg(c => c.Mean)}, spread {Avg(c => c.Spread)}; {string.Join(", ", list.GroupBy(x => x.C.Format).Select(g => $"{g.Key} x{g.Count()}"))}");
            foreach (var (name, c) in list.Take(4))
                line($"      e.g. {name}: {Text(c)}");
        }

        // How vanilla vertices colour Stormveil's plain and grass materials.
        var colours = new Dictionary<string, (long N, double R, double G, double B, double A)>(StringComparer.OrdinalIgnoreCase);
        var msb = MSBE.Read(er.Read(MapPorter.MsbPath(MapId.Parse("m10_00_00_00"))));
        foreach (var path in msb.Models.MapPieces.Select(p => MapPorter.ErMapBndPath(MapId.Parse("m10_00_00_00"), MapPorter.ModelNumber(p.Name))).Where(er.Contains).Distinct().Take(60))
            foreach (var flver in BND4.Read(er.Read(path)).Files.Where(f => FLVER2.Is(f.Bytes)).Select(f => FLVER2.Read(f.Bytes)))
                foreach (var mesh in flver.Meshes)
                {
                    if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count
                        || !matbins.TryGetValue(ModelPort.MatbinName(flver.Materials[mesh.MaterialIndex].MTD), out var mf))
                        continue;
                    var shader = Path.GetFileNameWithoutExtension(ItemText.Leaf(MATBIN.Read(mf.Bytes).ShaderPath ?? ""));
                    var acc = colours.GetValueOrDefault(shader);
                    foreach (var v in mesh.Vertices.Where(v => v.Colors.Count > 0))
                    {
                        var c = v.Colors[0];
                        acc = (acc.N + 1, acc.R + c.R, acc.G + c.G, acc.B + c.B, acc.A + c.A);
                    }
                    colours[shader] = acc;
                }
        line("  Stormveil vertex colours by shader (mean rgba):");
        foreach (var (shader, a) in colours.Where(kv => kv.Value.N > 0).OrderByDescending(kv => kv.Value.N).Take(15))
            line(FormattableString.Invariant($"    {shader}: {a.N:N0} vertices, {a.R / a.N:0.00} {a.G / a.N:0.00} {a.B / a.N:0.00} {a.A / a.N:0.00}"));
    }

    /// <summary>
    /// "map-diag gparam": every group and param of the draw params (gparam) of the slots' host colosseums, Stormveil and
    /// the slots' DS3 maps, with every value ID's first value, to Work\reports\map-diag\gparam-&lt;map&gt;.txt; for the
    /// Elden Ring maps also the files under the map's folder and its environment map regions (ambient and reflections
    /// come from those, not from the gparam alone). A ported map's colours are what these leave the host's.
    /// </summary>
    private static int Gparams(PorterContext ctx, List<string> args)
    {
        var dir = ctx.WorkPath("reports", "map-diag");
        Directory.CreateDirectory(dir);
        var erMaps = new List<MapId>();
        var ds3Maps = new List<MapId>();
        if (args.Count > 0)
            foreach (var a in args)
                (MapId.Parse(a).Area >= 30 && MapId.Parse(a).Area <= 51 && MapId.Parse(a).Area != 45 ? ds3Maps : erMaps).Add(MapId.Parse(a));
        else
        {
            erMaps.AddRange(MapSlots.All.Select(s => s.Host).Distinct());
            erMaps.Add(MapId.Parse("m10_00_00_00"));
            ds3Maps.AddRange(MapSlots.All.Select(s => s.Ds3).Distinct());
        }
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        foreach (var (game, archives, maps) in new[] { ("ER", er, erMaps), ("DS3", ds3, ds3Maps) })
            foreach (var map in maps)
            {
                var r = new StringBuilder();
                var path = game == "ER" ? MapAtmosphere.GparamPath(map) : MapAtmosphere.Ds3GparamPaths(map).FirstOrDefault(archives.Contains);
                if (path is null || !archives.Contains(path))
                    r.AppendLine($"{game} {map}: no gparam");
                else
                {
                    var g = GPARAM.Read(archives.Read(path));
                    r.AppendLine($"{game} {map}: {path} game {g.Game}, {g.Groups.Count} groups");
                    foreach (var group in g.Groups)
                    {
                        r.AppendLine($"[{group.Name2?.Trim()}] {group.Name1}");
                        foreach (var p in group.Params)
                        {
                            var ids = p.ValueIDs.Count == p.Values.Count ? p.ValueIDs : Enumerable.Range(0, p.Values.Count).ToList();
                            var firsts = ids.Select((id, i) => (id, i)).GroupBy(x => x.id).Take(6)
                                .Select(x => $"{x.Key}:{Show(p.Values[x.First().i])}");
                            r.AppendLine($"  {p.Name1} ({p.Values.Count}) {string.Join("  ", firsts)}");
                        }
                    }
                }
                if (game == "ER")
                {
                    r.AppendLine("files under the map folder:");
                    foreach (var e in archives.Entries.Where(e => e.Path is not null && e.Path.Contains(map.ToString(), StringComparison.OrdinalIgnoreCase)
                                 && !e.Path.EndsWith(".mapbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).Order().Take(200))
                        r.AppendLine("  " + e);
                    try
                    {
                        var msb = MSBE.Read(archives.Read(MapPorter.MsbPath(map)));
                        foreach (var point in msb.Regions.EnvironmentMapPoints)
                            r.AppendLine(FormattableString.Invariant($"env map point {point.Name} at {point.Position.X:0},{point.Position.Y:0},{point.Position.Z:0} map id {point.MapID}") + $" {Fields(point)}");
                        foreach (var box in msb.Regions.EnvironmentMapEffectBoxes)
                            r.AppendLine(FormattableString.Invariant($"env map box {box.Name} at {box.Position.X:0},{box.Position.Y:0},{box.Position.Z:0}") + $" {Fields(box)}");
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        r.AppendLine($"msb: {ex.Message}");
                    }
                }
                File.WriteAllText(Path.Combine(dir, $"gparam-{map}.txt"), r.ToString());
                Console.WriteLine($"{game} {map}: {r.ToString().Count(c => c == '\n')} lines");
            }
        Console.WriteLine($"reports in {dir}");
        return 0;
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        System.Numerics.Vector2 a => FormattableString.Invariant($"({a.X:0.###},{a.Y:0.###})"),
        System.Numerics.Vector3 a => FormattableString.Invariant($"({a.X:0.###},{a.Y:0.###},{a.Z:0.###})"),
        System.Numerics.Vector4 a => FormattableString.Invariant($"({a.X:0.###},{a.Y:0.###},{a.Z:0.###},{a.W:0.###})"),
        System.Drawing.Color c => $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}",
        byte[] b => Convert.ToHexString(b),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>The public fields and properties of a region's type-specific part, "Name=value" (numbers only).</summary>
    private static string Fields(object region) => string.Join(" ", region.GetType().GetProperties()
        .Where(p => p.DeclaringType == region.GetType() && p.CanRead && (p.PropertyType.IsPrimitive || p.PropertyType == typeof(float)))
        .Select(p => $"{p.Name}={Show(p.GetValue(region))}"));

    /// <summary>
    /// "map-diag envmap": the environment map binders of the slots' hosts and Stormveil (what lights a ported map's
    /// pieces through the environment box MapLighting copies from the host), to Work\reports\map-diag\envmap.txt:
    /// each binder's entries, each texture's format, size, faces and mean colour (HDR for BC6H), and what DS3 keeps for
    /// its own environment maps.
    /// </summary>
    private static int EnvMaps(PorterContext ctx, List<string> args)
    {
        var maps = args.Count > 0 ? args.Select(MapId.Parse).ToList()
            : MapSlots.All.Select(s => s.Host).Distinct().Append(MapId.Parse("m10_00_00_00")).ToList();
        var r = new StringBuilder();
        using (var er = ctx.OpenArchives(PorterGame.EldenRing))
            foreach (var map in maps)
            {
                var prefix = $"/map/{map.AreaFolder}/{map}/{map}_envmap_";
                var files = er.Entries.Where(e => e.Path is not null && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).Order().ToList();
                r.AppendLine($"=== ER {map}: {files.Count} environment map binders");
                foreach (var path in files)
                {
                    try
                    {
                        var bytes = er.Read(path);
                        r.AppendLine($"{ItemText.Leaf(path)}: {(BND4.Is(bytes) ? "BND4" : TPF.Is(bytes) ? "TPF" : "other")} {bytes.Length:N0} bytes");
                        var tpfs = new List<(string Entry, byte[] Bytes)>();
                        if (BND4.Is(bytes))
                        {
                            var bnd = BND4.Read(bytes);
                            r.AppendLine($"  BND4 version {bnd.Version} format {bnd.Format} unicode {bnd.Unicode}, {bnd.Files.Count} files");
                            foreach (var f in bnd.Files.Take(40))
                            {
                                var inner = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
                                r.AppendLine($"  [{f.ID}] flags {f.Flags} {f.Name} {f.Bytes.Length:N0} bytes{(DCX.Is(f.Bytes) ? " (DCX)" : "")}");
                                if (TPF.Is(inner))
                                    tpfs.Add((f.Name, inner));
                            }
                        }
                        else if (TPF.Is(bytes))
                            tpfs.Add((path, bytes));
                        bool measure = path.Contains("_high_", StringComparison.OrdinalIgnoreCase) || path.Contains("_00_", StringComparison.OrdinalIgnoreCase);
                        foreach (var (entry, tb) in tpfs.Take(12))
                        {
                            var tpf = TPF.Read(tb);
                            r.AppendLine($"    tpf platform {tpf.Platform} encoding {tpf.Encoding} flag2 {tpf.Flag2}, {tpf.Textures.Count} textures");
                            foreach (var t in tpf.Textures.Take(8))
                                r.AppendLine($"    texture {t.Name} format {t.Format} type {t.Type} mips {t.Mipmaps} flags1 {t.Flags1} {t.Bytes.Length:N0} bytes {(t.Bytes.Length >= 132 ? FormatName(t.Bytes) : "")} {DdsSize(t.Bytes)}"
                                    + (measure ? " " + MeanColour(t.Bytes) : ""));
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        r.AppendLine($"{ItemText.Leaf(path)}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        using (var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3))
            foreach (var slot in MapSlots.All)
            {
                var id = slot.Ds3.ToString();
                var files = ds3.Entries.Where(e => e.Path is not null && e.Path.Contains(id, StringComparison.OrdinalIgnoreCase)
                    && (e.Path.Contains("env", StringComparison.OrdinalIgnoreCase) || e.Path.Contains("light", StringComparison.OrdinalIgnoreCase) || e.Path.Contains("gi", StringComparison.OrdinalIgnoreCase))
                    && !e.Path.EndsWith(".mapbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).Order().ToList();
                r.AppendLine($"=== DS3 {id}: {files.Count} environment/light files");
                foreach (var f in files.Take(30))
                    r.AppendLine("  " + f);
                foreach (var f in files.Where(f => f.Contains("env", StringComparison.OrdinalIgnoreCase)).Take(2))
                    try
                    {
                        var bytes = ds3.Read(f);
                        var tpfs = TPF.Is(bytes) ? [TPF.Read(bytes)] : BND4.Is(bytes) ? BND4.Read(bytes).Files.Select(x => DCX.Is(x.Bytes) ? DCX.Decompress(x.Bytes) : x.Bytes).Where(TPF.Is).Select(TPF.Read).Take(4).ToList() : [];
                        foreach (var t in tpfs.SelectMany(t => t.Textures).Take(6))
                            r.AppendLine($"    {ItemText.Leaf(f)}: texture {t.Name} format {t.Format} type {t.Type} mips {t.Mipmaps} {FormatName(t.Bytes)} {DdsSize(t.Bytes)} {MeanColour(t.Bytes)}");
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        r.AppendLine($"    {ItemText.Leaf(f)}: {ex.GetType().Name}: {ex.Message}");
                    }
            }
        var dir = ctx.WorkPath("reports", "map-diag");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, "envmap.txt");
        File.WriteAllText(outPath, r.ToString());
        Console.Write(r.ToString().Length > 6000 ? r.ToString()[..6000] + "\n..." : r.ToString());
        Console.WriteLine($"report: {outPath}");
        return 0;
    }

    private static string DdsSize(byte[] dds)
    {
        if (dds.Length < 128 || BitConverter.ToInt32(dds, 0) != 0x20534444)
            return "(not DDS)";
        int caps2 = BitConverter.ToInt32(dds, 112);
        return $"{BitConverter.ToInt32(dds, 16)}x{BitConverter.ToInt32(dds, 12)} mips {BitConverter.ToInt32(dds, 28)}{((caps2 & 0x200) != 0 ? " cube" : "")}"
               + (dds.Length >= 148 && Encoding.ASCII.GetString(dds, 84, 4) == "DX10" ? $" array {BitConverter.ToInt32(dds, 140)} misc {BitConverter.ToInt32(dds, 136)}" : "");
    }

    /// <summary>The mean colour of a texture's first face, as floats for HDR formats (BC6H), else 0-255.</summary>
    private static string MeanColour(byte[] dds)
    {
        try
        {
            using var ms = new MemoryStream(dds);
            var file = BCnEncoder.Shared.ImageFiles.DdsFile.Load(ms);
            var decoder = new BCnEncoder.Decoder.BcDecoder();
            var fmt = FormatName(dds);
            if (fmt is "DXGI95" or "DXGI96")
            {
                var px = decoder.DecodeHdr(file);
                double r = 0, g = 0, b = 0;
                foreach (var c in px) { r += c.r; g += c.g; b += c.b; }
                double n = Math.Max(1, px.Length);
                return FormattableString.Invariant($"mean hdr {r / n:0.###} {g / n:0.###} {b / n:0.###} ({px.Length} px)");
            }
            var ldr = decoder.Decode(file);
            double lr = 0, lg = 0, lb = 0;
            foreach (var c in ldr) { lr += c.r; lg += c.g; lb += c.b; }
            double m = Math.Max(1, ldr.Length);
            return FormattableString.Invariant($"mean {lr / m:0} {lg / m:0} {lb / m:0} ({ldr.Length} px)");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"(not decoded: {ex.GetType().Name}: {ex.Message})";
        }
    }
}
