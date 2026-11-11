using System.Numerics;
using System.Text.RegularExpressions;
using DarkSign.Porter.Maps;
using HKLib.hk2018;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// Diagnostics for the enemy port (RULE 4), written to Work\reports\enemy-diag:
/// "enemy-diag survey" lists every Elden Ring and DS3 character's animation skeleton (bones with parents, height)
/// and animation IDs, the facts the template choice and the animation fallbacks are made from;
/// "enemy-diag unpack &lt;file&gt;..." writes built or game files decompressed to Work\extract\MOD, to look at elsewhere;
/// "enemy-diag tae &lt;anibnd&gt; [from-to...]" lists a decompressed animation binder's TAE entries with their events, and
/// each animation file's length, tracks and blend hint (no game needed: it runs on files staged into a cloud thread).
/// </summary>
public static partial class EnemyDiag
{
    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimLeaf();

    [GeneratedRegex(@"^/chr/c(\d{4})\.anibnd\.dcx$", RegexOptions.IgnoreCase)]
    private static partial Regex MainAnibnd();

    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && args[0].Equals("unpack", StringComparison.OrdinalIgnoreCase))
            return Unpack(ctx, args.Skip(1).ToList());
        if (args.Count == 1 && args[0].Equals("survey", StringComparison.OrdinalIgnoreCase))
            return Survey(ctx);
        if (args.Count >= 1 && args[0].Equals("plan", StringComparison.OrdinalIgnoreCase))
            return Plan(ctx, args.Skip(1).ToList());
        if (args.Count >= 2 && args[0].Equals("pixels", StringComparison.OrdinalIgnoreCase))
            return Pixels(ctx, args[1], args.Skip(2).ToList());
        if (args.Count >= 2 && args[0].Equals("textures", StringComparison.OrdinalIgnoreCase))
            return Textures(ctx, args.Skip(1).ToList());
        if (args.Count >= 2 && args[0].Equals("binder", StringComparison.OrdinalIgnoreCase))
            return Binder(ctx, args.Skip(1).ToList());
        if (args.Count >= 3 && args[0].Equals("matbin", StringComparison.OrdinalIgnoreCase))
            return Matbin(ctx, args[1], args.Skip(2).ToList());
        Console.Error.WriteLine("enemy-diag survey | plan [build <DS3 chr>...] | textures <DS3 chr>... | binder <file>... | matbin <binder> <name part>... | unpack <file>...");
        return 1;
    }

    /// <summary>
    /// The template choice for every DS3 character with the runners-up (Work\reports\enemy-diag\plan.tsv and, short,
    /// the console); "plan build c2230 c1280" also builds those characters' files without writing them and prints
    /// their notes (skeleton, animations, textures).
    /// </summary>
    private static int Plan(PorterContext ctx, List<string> args)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var notes = new List<string>();
        var catalog = EnemyPort.BuildCatalog(er, ctx.LoadParams(PorterGame.EldenRing).Get("NpcParam"), notes);
        var characters = ChrPort.Ds3Characters(ds3, ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcParam"));
        TAE.ValidateEventBank = false;
        var ranks = new Dictionary<int, string>();
        var plans = ChrPort.MakePlans(er, ds3, characters, catalog, ChrPort.CurrentMethod, notes, null, (c, ranked) =>
            ranks[c.Chr] = string.Join(" | ", ranked.Take(4).Select(r => FormattableString.Invariant(
                $"{r.Template.Model} {Short(r.Template.Name)} b{r.Match:0.00} s{r.Size:0.00} ={r.Score:0.00}"))));
        var lines = new List<string> { "DS3 chr\tname\tboss\tmethod\ttemplate files\tbest four (model name, b = share of DS3 bones, s = size match, = score)" };
        foreach (var p in plans)
            lines.Add(string.Join('\t', $"c{p.Ds3.Chr:D4}", Short(p.Ds3.Name), p.Ds3.Boss ? 1 : 0, p.Method, $"c{p.TemplateChr:D4}", ranks.GetValueOrDefault(p.Ds3.Chr, "")));
        var path = ctx.WorkPath("reports", "enemy-diag", "plan.tsv");
        File.WriteAllLines(path, lines);
        foreach (var line in lines)
            Console.WriteLine(line);
        Console.WriteLine("templates used: " + string.Join(", ", plans.GroupBy(p => $"{p.Template.Model} {Short(p.Template.Name)} ({p.Method})")
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} x {g.Key}")));
        foreach (var n in notes)
            Console.WriteLine($"note: {n}");
        // "build" also builds the test set's stages of the characters named (ChrPort.TestPlans).
        var tests = ChrPort.TestPlans(plans, c => er.Contains($"/chr/c{c:D4}.chrbnd.dcx"));
        if (args.Count >= 2 && args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
            foreach (var plan in plans.Concat(tests).Where(p => args.Skip(1).Any(a => a.Equals($"c{p.Ds3.Chr:D4}", StringComparison.OrdinalIgnoreCase))))
                try
                {
                    var files = ChrPort.BuildFiles(ctx, er, ds3, plan);
                    Console.WriteLine($"build c{plan.Ds3.Chr:D4} on c{plan.TemplateChr:D4} as c{plan.NewChr:D4} ({plan.Method}, stage {plan.Stage}): {files.Outputs.Count} files, {files.Outputs.Sum(o => (long)o.Bytes.Length)} bytes; {files.Note}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.WriteLine($"build c{plan.Ds3.Chr:D4} FAILED: {ChrPort.Describe(ex)}");
                }
        return 0;
    }

    /// <summary>
    /// "enemy-diag textures c5280 c5140": a DS3 character's materials (material file, every sampler and the texture
    /// it names, which one the port takes as colour) and the textures its binders hold (format, size, mip levels),
    /// with the names the model uses that no binder has. Reads DS3 only.
    /// </summary>
    private static int Textures(PorterContext ctx, List<string> args)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        foreach (var arg in args)
        {
            int x = int.Parse(arg.TrimStart('c', 'C'));
            var chrbnd = BND4.Read(ds3.Read($"/chr/c{x:D4}.chrbnd.dcx"));
            var flver = FLVER2.Read(chrbnd.Files.First(f => FLVER2.Is(f.Bytes)).Bytes);
            Console.WriteLine($"== c{x:D4}: {flver.Meshes.Count} meshes, {flver.Materials.Count} materials; chrbnd files: {string.Join(", ", chrbnd.Files.Select(f => ItemText.Leaf(f.Name)))}");
            var held = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Hold(string where, byte[] bytes)
            {
                foreach (var t in TPF.Read(bytes).Textures)
                {
                    var d = t.Bytes;
                    string fourCc = d.Length > 88 ? System.Text.Encoding.ASCII.GetString(d, 84, 4).TrimEnd('\0') : "?";
                    string info = d.Length > 132 ? $"{BitConverter.ToInt32(d, 16)}x{BitConverter.ToInt32(d, 12)} mips {BitConverter.ToInt32(d, 28)} {fourCc}{(fourCc == "DX10" ? "/" + BitConverter.ToInt32(d, 128) : "")} caps2 {BitConverter.ToInt32(d, 112):X}" : "short";
                    held.TryAdd(t.Name, $"{info} fmt {t.Format} flags {t.Flags1} {d.Length / 1024} KB in {where}");
                }
            }
            foreach (var f in chrbnd.Files.Where(f => TPF.Is(f.Bytes)))
                Hold("chrbnd", f.Bytes);
            foreach (var path in ds3.Entries.Where(e => e.Path is { } p && p.StartsWith($"/chr/c{x:D4}", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".texbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).ToList())
                foreach (var f in BND4.Read(ds3.Read(path)).Files.Where(f => TPF.Is(f.Bytes)))
                    Hold(ItemText.Leaf(path), f.Bytes);
            for (int i = 0; i < flver.Materials.Count; i++)
            {
                var m = flver.Materials[i];
                int meshes = flver.Meshes.Count(mesh => mesh.MaterialIndex == i);
                long verts = flver.Meshes.Where(mesh => mesh.MaterialIndex == i).Sum(mesh => (long)mesh.Vertices.Count);
                Console.WriteLine($"  material {i} \"{m.Name}\" {ItemText.Leaf(m.MTD)} ({meshes} meshes, {verts} vertices, cut-out {ChrModel.IsAlphaTested(m.MTD)})");
                foreach (var t in m.Textures)
                {
                    var leaf = t.Path.Length > 0 ? Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path)) : "";
                    Console.WriteLine($"    {t.ParamName} = {(t.Path.Length > 0 ? t.Path : "(empty)")}{(leaf.Length == 0 ? "" : held.ContainsKey(leaf) ? "" : "  << IN NO BINDER")} scale {t.TilingScale.X}x{t.TilingScale.Y}");
                }
            }
            foreach (var (name, info) in held.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  texture {name}: {info}");
        }
        return 0;
    }

    /// <summary>
    /// "enemy-diag pixels &lt;texture binder or tpf&gt; [name part...]": what the textures hold. Per texture the mean
    /// colour (sRGB 0-255), the darkest and brightest luminance, the share of pixels cut out (alpha under 128) and,
    /// for _m maps, the share that is metal (over 128); a 256-pixel PNG preview of each goes to
    /// Work\reports\enemy-diag\png (alpha dropped, so a cut-out texture still shows its colours).
    /// </summary>
    private static int Pixels(PorterContext ctx, string file, List<string> parts)
    {
        var bytes = ReadAny(file);
        var tpfs = TPF.Is(bytes) ? [TPF.Read(bytes)] : BND4.Read(bytes).Files.Where(f => TPF.Is(f.Bytes)).Select(f => TPF.Read(f.Bytes)).ToList();
        var dir = ctx.WorkPath("reports", "enemy-diag", "png");
        Directory.CreateDirectory(dir);
        foreach (var t in tpfs.SelectMany(p => p.Textures).Where(t => parts.Count == 0 || parts.Any(p => t.Name.Contains(p, StringComparison.OrdinalIgnoreCase))))
            try
            {
                var (px, w, h) = WeaponTextures.Decode(t.Bytes);
                double r = 0, g = 0, b = 0, a = 0;
                int cut = 0, bright = 0, lo = 255, hi = 0;
                foreach (var c in px)
                {
                    r += c.r; g += c.g; b += c.b; a += c.a;
                    int lum = (c.r * 54 + c.g * 183 + c.b * 19) >> 8;
                    lo = Math.Min(lo, lum); hi = Math.Max(hi, lum);
                    cut += c.a < 128 ? 1 : 0;
                    bright += c.r > 128 ? 1 : 0;
                }
                double n = px.Length;
                Console.WriteLine(FormattableString.Invariant($"{t.Name}: {w}x{h} mean rgb {r / n:0} {g / n:0} {b / n:0} alpha {a / n:0}, luminance {lo}-{hi}, cut out {100 * cut / n:0.0}%, red over 128 {100 * bright / n:0.0}%"));
                int step = Math.Max(1, Math.Max(w, h) / 256), pw = w / step, ph = h / step;
                var rgb = new byte[ph * (1 + pw * 3)];
                for (int y = 0; y < ph; y++)
                    for (int x = 0; x < pw; x++)
                    {
                        var c = px[y * step * w + x * step];
                        int o = y * (1 + pw * 3) + 1 + x * 3;
                        rgb[o] = c.r; rgb[o + 1] = c.g; rgb[o + 2] = c.b;
                    }
                File.WriteAllBytes(Path.Combine(dir, string.Concat(t.Name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '-')) + ".png"), Png(rgb, pw, ph));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"{t.Name}: not decoded ({ex.GetType().Name}: {ex.Message})");
            }
        Console.WriteLine($"previews in {dir}");
        return 0;
    }

    /// <summary>An 8-bit RGB PNG from scanlines that each start with a 0 filter byte.</summary>
    private static byte[] Png(byte[] scanlines, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var head = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
            ms.Write(head);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(body);
            uint crc = 0xFFFFFFFF;
            foreach (byte v in body)
            {
                crc ^= v;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, ~crc);
            ms.Write(head);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var deflate = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true))
                deflate.Write(scanlines);
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", []);
        return ms.ToArray();
    }

    private static string Short(string name) => name.Replace('\t', ' ').Length > 34 ? name.Replace('\t', ' ')[..34] : name.Replace('\t', ' ');

    private static byte[] ReadAny(string path)
    {
        var raw = File.ReadAllBytes(path);
        return DCX.Is(raw) ? DCX.Decompress(raw) : raw;
    }

    /// <summary>What is inside binder files on disk (built or extracted): files, textures of each TPF, materials of each model.</summary>
    private static int Binder(PorterContext ctx, List<string> paths)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing); // makes Oodle available
        foreach (var path in paths)
        {
            Console.WriteLine($"== {path}");
            try
            {
                var bnd = BND4.Read(ReadAny(path));
                Console.WriteLine($"   binder format {bnd.Format} unicode {bnd.Unicode} bigendian {bnd.BigEndian} extended {bnd.Extended} unk04 {bnd.Unk04} unk05 {bnd.Unk05} version {bnd.Version}; {bnd.Files.Count} files");
                foreach (var f in bnd.Files.Take(40))
                {
                    Console.WriteLine($"   {f.ID} flags {f.Flags} {f.Name} ({f.Bytes.Length} bytes)");
                    if (TPF.Is(f.Bytes))
                    {
                        var t = TPF.Read(f.Bytes);
                        Console.WriteLine($"      tpf platform {t.Platform} encoding {t.Encoding} flag2 {t.Flag2}, {t.Textures.Count} textures");
                        foreach (var x in t.Textures.Take(12))
                            Console.WriteLine($"      {x.Name} format {x.Format} type {x.Type} mips {x.Mipmaps} flags1 {x.Flags1} {x.Bytes.Length} bytes {Dds(x.Bytes)}");
                        if (t.Textures.Count > 12)
                            Console.WriteLine($"      ... names: {string.Join(" ", t.Textures.Skip(12).Take(60).Select(x => x.Name))}");
                    }
                    else if (FLVER2.Is(f.Bytes))
                    {
                        var fl = FLVER2.Read(f.Bytes);
                        Console.WriteLine($"      model version {fl.Header.Version:X} {fl.Meshes.Count} meshes {fl.Nodes.Count} nodes {fl.Materials.Count} materials");
                        foreach (var m in fl.Materials.Take(10))
                            Console.WriteLine($"      material {m.Name} | {m.MTD} | {string.Join(" ; ", m.Textures.Select(x => $"{x.ParamName}={x.Path}"))}");
                    }
                }
                if (bnd.Files.Count > 40)
                    Console.WriteLine($"   ... {bnd.Files.Count - 40} more: {string.Join(" ", bnd.Files.Skip(40).Take(40).Select(f => ItemText.Leaf(f.Name)))}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"   FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }

    private static string Dds(byte[] b)
    {
        if (b.Length < 128 || b[0] != 'D')
            return "(no DDS header)";
        var fourCc = System.Text.Encoding.ASCII.GetString(b, 84, 4).TrimEnd('\0');
        return $"{BitConverter.ToInt32(b, 16)}x{BitConverter.ToInt32(b, 12)} {fourCc}{(fourCc == "DX10" && b.Length >= 148 ? $" dxgi {BitConverter.ToInt32(b, 128)}" : "")}";
    }

    /// <summary>The materials in a material binder whose name contains one of the parts: shader, samplers, a few values.</summary>
    private static int Matbin(PorterContext ctx, string binder, List<string> parts)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var bnd = BND4.Read(ReadAny(binder));
        Console.WriteLine($"== {binder}: {bnd.Files.Count} files");
        int shown = 0;
        foreach (var f in bnd.Files.Where(f => parts.Any(p => f.Name.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            if (shown++ >= 12)
                break;
            var m = MATBIN.Read(f.Bytes);
            Console.WriteLine($"   {f.ID} {f.Name}");
            Console.WriteLine($"      shader {m.ShaderPath} source {m.SourcePath} key {m.Key}");
            foreach (var x in m.Samplers)
                Console.WriteLine($"      sampler {x.Type} = {x.Path} key {x.Key}");
            Console.WriteLine("      " + string.Join(" ", m.Params.Where(x => x.Name is "g_AlphaRef" or "g_BlendMode").Select(x => $"{x.Name}={(x.Value is Array a ? string.Join(",", a.Cast<object>()) : x.Value)}")));
        }
        Console.WriteLine($"   {bnd.Files.Count(f => parts.Any(p => f.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))} match");
        return 0;
    }

    private static int Unpack(PorterContext ctx, List<string> paths)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing); // makes Oodle available
        int done = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"not found: {path}");
                continue;
            }
            var raw = File.ReadAllBytes(path);
            var bytes = DCX.Is(raw) ? DCX.Decompress(raw) : raw;
            var name = Path.GetFileName(path);
            if (name.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            var outPath = ctx.WorkPath("extract", "MOD", Path.GetFileName(Path.GetDirectoryName(path)!) ?? "x", name);
            File.WriteAllBytes(outPath, bytes);
            Console.WriteLine(outPath);
            done++;
        }
        return done > 0 ? 0 : 1;
    }

    /// <summary>
    /// "enemy-diag tae &lt;anibnd&gt; [from-to|id ...]": every TAE entry of a decompressed animation binder in the given
    /// ID ranges (all without one) with what it plays and its events (type, start-end, parameter bytes), plus the
    /// animation files the binder holds. For comparing a built character's damage, death and attack moves with its
    /// template's and DS3's.
    /// </summary>
    public static int Tae(string path, List<string> ranges)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"not found: {path}");
            return 1;
        }
        var wanted = ranges.Select(r => r.Split('-')).Select(p => (From: long.Parse(p[0]), To: long.Parse(p[^1]))).ToList();
        bool Wanted(long id) => wanted.Count == 0 || wanted.Any(w => id % 1_000_000 >= w.From && id % 1_000_000 <= w.To);
        TAE.ValidateEventBank = false;
        var bnd = BND4.Read(File.ReadAllBytes(path));
        var hkx = bnd.Files.Select(f => ItemText.Leaf(f.Name)).Where(n => AnimLeaf().IsMatch(n)).ToList();
        Console.WriteLine($"{Path.GetFileName(path)}: {bnd.Files.Count} files, {hkx.Count} animation files");
        var compendium = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
        foreach (var name in hkx.Where(n => Wanted(long.Parse(AnimLeaf().Match(n).Groups[2].Value))))
        {
            var bytes = bnd.Files.First(f => ItemText.Leaf(f.Name) == name).Bytes;
            Console.WriteLine($"  file {name} {bytes.Length} {ClipFacts(bytes, compendium)}");
        }
        foreach (var file in bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
        {
            TAE tae;
            try { tae = TAE.Read(file.Bytes); }
            catch (Exception ex) { Console.WriteLine($"{ItemText.Leaf(file.Name)}: unreadable ({ex.Message})"); continue; }
            Console.WriteLine($"{ItemText.Leaf(file.Name)}: {tae.Animations.Count} entries");
            foreach (var anim in tae.Animations.Where(a => Wanted(a.ID)))
            {
                string plays = anim.MiniHeader switch
                {
                    TAE.Animation.AnimMiniHeader.ImportOtherAnim imp => $"plays entry {imp.ImportFromAnimID}",
                    TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: true } s => $"file of {s.ImportHKXSourceAnimID}",
                    _ => "own file",
                };
                Console.WriteLine($" {anim.ID} ({plays}) {anim.Events.Count} events");
                foreach (var e in anim.Events.OrderBy(e => e.StartTime))
                    Console.WriteLine(FormattableString.Invariant($"    {e.Type,4} {e.StartTime:0.00}-{e.EndTime:0.00} {System.Convert.ToHexString(e.GetParameterBytes(false))}"));
            }
        }
        return 0;
    }

    /// <summary>An Elden Ring animation file's length, bound tracks and blend hint ("" for a DS3 or unreadable file).</summary>
    private static string ClipFacts(byte[] bytes, byte[]? compendium)
    {
        try
        {
            lock (AnimPort.HavokLock)
            {
                var container = AnimPort.Container((hkRootLevelContainer)AnimPort.ReadEr(bytes, compendium));
                var binding = container.m_bindings.FirstOrDefault();
                var anim = container.m_animations.FirstOrDefault();
                return FormattableString.Invariant($"{anim?.m_duration ?? 0:0.00}s tracks {anim?.m_numberOfTransformTracks} bound {binding?.m_transformTrackToBoneIndices.Count} blend {binding?.m_blendHint}");
            }
        }
        catch (Exception)
        {
        }
        try
        {
            var hkx = SoulsAssetPipeline.Animation.HKX.Read(bytes, SoulsAssetPipeline.Animation.HKX.HKXVariation.HKXDS3, false);
            var anim = hkx.DataSection.Objects.OfType<SoulsAssetPipeline.Animation.HKX.HKASplineCompressedAnimation>().FirstOrDefault();
            var binding = hkx.DataSection.Objects.OfType<SoulsAssetPipeline.Animation.HKX.HKAAnimationBinding>().FirstOrDefault();
            return FormattableString.Invariant($"{anim?.Duration ?? 0:0.00}s tracks {anim?.TransformTrackCount} bound {binding?.TransformTrackToBoneIndices.Size} blend {binding?.BlendHint} (DS3)");
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>World positions of a skeleton's bones in its rest pose.</summary>
    public static Vector3[] WorldPositions(IReadOnlyList<short> parents, IReadOnlyList<(Vector3 T, Quaternion R)> local)
    {
        var pos = new Vector3[local.Count];
        var rot = new Quaternion[local.Count];
        for (int i = 0; i < local.Count; i++)
        {
            int p = i < parents.Count ? parents[i] : -1;
            if (p < 0 || p >= i)
            {
                pos[i] = local[i].T;
                rot[i] = local[i].R;
            }
            else
            {
                pos[i] = pos[p] + Vector3.Transform(local[i].T, rot[p]);
                rot[i] = rot[p] * local[i].R;
            }
        }
        return pos;
    }

    private static string Bones(IReadOnlyList<string> names, IReadOnlyList<short> parents) =>
        string.Join("|", names.Select((n, i) => $"{n}>{(i < parents.Count ? parents[i] : -1)}"));

    private static string Size(Vector3[] world)
    {
        if (world.Length == 0)
            return "0\t0\t0";
        var min = world.Aggregate(Vector3.Min);
        var max = world.Aggregate(Vector3.Max);
        var s = max - min;
        return FormattableString.Invariant($"{s.X:0.00}\t{s.Y:0.00}\t{s.Z:0.00}");
    }

    private static string Ids(IEnumerable<string> leaves) =>
        string.Join(",", leaves.Select(l => AnimLeaf().Match(l)).Where(m => m.Success)
            .Select(m => (S: int.Parse(m.Groups[1].Value), I: int.Parse(m.Groups[2].Value))).Distinct().OrderBy(k => k)
            .Select(k => $"{k.S}:{k.I}"));

    private static int Survey(PorterContext ctx)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var notes = new List<string>();
        var erNpc = ctx.LoadParams(PorterGame.EldenRing).Get("NpcParam");
        var catalog = EnemyPort.BuildCatalog(er, erNpc, notes);
        var byModel = catalog.GroupBy(c => ChrPort.ChrId(c.Model)).ToDictionary(g => g.Key, g => g.ToList());

        var lines = new List<string> { "chr\tin catalog\tkind\tboss\tnpc\tthink\tnames\tbehbnd\ttae\tbones\tsize x\tsize y\tsize z\tanimations\tbone list\tanimation ids" };
        foreach (var entry in er.Entries.Where(e => e.Path is { } p && MainAnibnd().IsMatch(p)).OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            int chr = int.Parse(MainAnibnd().Match(entry.Path!).Groups[1].Value);
            try
            {
                var bnd = BND4.Read(er.Read(entry.Path!));
                var skeletonFile = bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
                bool tae = bnd.Files.Any(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase));
                var leaves = new List<string>();
                foreach (var a in er.Entries.Where(e => e.Path is { } p && Regex.IsMatch(ItemText.Leaf(p), $@"^c{chr:D4}(_div\d+)?\.anibnd\.dcx$", RegexOptions.IgnoreCase)))
                    leaves.AddRange((a.Path == entry.Path ? bnd : BND4.Read(er.Read(a.Path!))).Files.Select(f => ItemText.Leaf(f.Name)));
                string bones = "", size = "0\t0\t0";
                int count = 0;
                if (skeletonFile is not null)
                    lock (AnimPort.HavokLock)
                    {
                        var compendium = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
                        var s = AnimPort.ReadErSkeleton(skeletonFile.Bytes, compendium);
                        count = s.Names.Count;
                        bones = Bones(s.Names, s.Parents);
                        size = Size(WorldPositions(s.Parents, s.ReferencePose.Select(t =>
                            (new Vector3(t.m_translation.X, t.m_translation.Y, t.m_translation.Z), t.m_rotation)).ToList()));
                    }
                var cat = byModel.GetValueOrDefault(chr) ?? [];
                var first = cat.FirstOrDefault();
                lines.Add(string.Join('\t', $"c{chr:D4}", cat.Count, first?.Kind ?? "", cat.Any(c => c.Boss) ? 1 : 0, first?.NpcParam ?? 0, first?.ThinkParam ?? 0,
                    string.Join(" / ", cat.Select(c => c.Name.Replace('\t', ' ')).Distinct().Take(4)),
                    er.Contains($"/chr/c{chr:D4}.behbnd.dcx") ? 1 : 0, tae ? 1 : 0, count, size,
                    leaves.Count(l => AnimLeaf().IsMatch(l)), bones, Ids(leaves)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"c{chr:D4}\tFAIL {ex.GetType().Name}: {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}");
            }
        }
        var erPath = ctx.WorkPath("reports", "enemy-diag", "er-chr.tsv");
        File.WriteAllLines(erPath, lines);
        Console.WriteLine($"{erPath}: {lines.Count - 1} Elden Ring characters");

        var ds3Npc = ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcParam");
        lines = ["chr\tname\tkind\tboss\tmodel nodes\tbones\tsize x\tsize y\tsize z\tanimations\tbone list\tanimation ids"];
        foreach (var c in ChrPort.Ds3Characters(ds3, ds3Npc))
        {
            try
            {
                var files = ChrPort.Ds3AnimationSources(c.Chr).SelectMany(chr => ds3.Entries
                        .Where(e => e.Path is { } p && Regex.IsMatch(ItemText.Leaf(p), $@"^c{chr:D4}(_.*)?\.anibnd\.dcx$", RegexOptions.IgnoreCase))
                        .OrderBy(e => e.Path, StringComparer.Ordinal)
                        .SelectMany(e => BND4.Read(ds3.Read(e.Path!)).Files))
                    .ToList();
                var skeletonFile = files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
                string bones = "", size = "0\t0\t0";
                int count = 0;
                if (skeletonFile is not null)
                {
                    var s = AnimPort.ReadDs3Skeleton(skeletonFile.Bytes);
                    count = s.Names.Count;
                    bones = Bones(s.Names, s.Parents);
                    size = Size(WorldPositions(s.Parents, s.ReferencePose.Select(t => (t.Translation, t.Rotation)).ToList()));
                }
                var leaves = files.Select(f => ItemText.Leaf(f.Name)).ToList();
                lines.Add(string.Join('\t', $"c{c.Chr:D4}", c.Name.Replace('\t', ' '), EnemyPort.KindOf(c.Name), c.Boss ? 1 : 0,
                    ChrPort.FlverBones(ds3, c.Chr)?.Count ?? 0, count, size, leaves.Count(l => AnimLeaf().IsMatch(l)), bones, Ids(leaves)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"c{c.Chr:D4}\tFAIL {ex.GetType().Name}: {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}");
            }
        }
        var ds3Path = ctx.WorkPath("reports", "enemy-diag", "ds3-chr.tsv");
        File.WriteAllLines(ds3Path, lines);
        Console.WriteLine($"{ds3Path}: {lines.Count - 1} DS3 characters");
        foreach (var n in notes)
            Console.WriteLine($"note: {n}");
        return 0;
    }
}
