using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SoulsFormats;
using BCnEncoder.Decoder;
using BCnEncoder.Shared.ImageFiles;

namespace DarkSign.Porter;

/// <summary>
/// `texture-check`: reads the ported weapon models in the mod folder back and checks that every texture their
/// materials sample is one the game can find, and compares each with what the DS3 original used. A weapon whose
/// albedo (colour) texture can't be found renders dark grey in game. Writes Work\reports\texture-check.tsv and
/// prints a short summary.
/// </summary>
public static class TextureCheck
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        // --convert: also convert each weapon's model in memory with this porter (nothing is written to the mod) and
        // report its textures, to check a texture change before a rebuild.
        bool convert = args.Contains("--convert");
        args = args.Where(a => a != "--convert").ToList();
        var planPath = ctx.WorkPath("reports", "weapons-plan.json");
        var plans = JsonSerializer.Deserialize<List<WeaponPlan>>(File.ReadAllText(planPath))!
            .Where(p => p.Status == "port").ToList();
        if (args.Count > 0)
            plans = plans.Where(p => args.Any(a => p.Name.Contains(a, StringComparison.OrdinalIgnoreCase)
                || p.NewModelId.ToString() == a)).ToList();
        var ds3Weapons = ctx.LoadParams(PorterGame.DarkSouls3).Get("EquipParamWeapon")!;
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);

        // Every texture name in every DS3 weapon binder, to say where a texture a model uses but lacks really lives.
        var ds3Index = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ds3Parts = ds3.Entries.Select(e => e.Path).Where(p => p is not null && p.StartsWith("/parts/wp_", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".partsbnd.dcx", StringComparison.OrdinalIgnoreCase)).Cast<string>().ToList();
        Parallel.ForEach(ds3Parts, Parallelism.Options, path =>
        {
            foreach (var f in BND4.Read(ds3.Read(path)).Files.Where(f => TPF.Is(f.Bytes)))
                foreach (var t in TPF.Read(f.Bytes).Textures)
                    ds3Index.TryAdd(t.Name, ItemText.Leaf(path));
        });

        var matbins = new Dictionary<string, MATBIN>(StringComparer.OrdinalIgnoreCase);
        var modMaterials = ctx.ModPath(["material", "allmaterial_dlc02.matbinbnd.dcx"]);
        foreach (var f in BND4.Read(File.ReadAllBytes(modMaterials)).Files)
            matbins.TryAdd(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)), MATBIN.Read(f.Bytes));

        var rows = new ConcurrentBag<(int Order, string Line)>();
        var bad = new ConcurrentBag<(int Order, string Line)>();
        Parallel.For(0, plans.Count, Parallelism.Options, i =>
        {
            var plan = plans[i];
            int ds3Model = Convert.ToInt32(ds3Weapons[plan.Ds3Id]!["equipModelId"].Value);
            var sb = new StringBuilder();
            var problems = new List<string>();
            foreach (var lod in new[] { false, true })
            {
                var ds3Path = ModelPort.PartsPath(ds3Model, lod);
                if (!ds3.Contains(ds3Path))
                    continue;
                var src = BND4.Read(ds3.Read(ds3Path));
                var srcTex = src.Files.Where(f => TPF.Is(f.Bytes)).SelectMany(f => TPF.Read(f.Bytes).Textures)
                    .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in src.Files.Where(f => FLVER2.Is(f.Bytes)))
                    foreach (var m in FLVER2.Read(f.Bytes).Materials)
                        foreach (var t in m.Textures.Where(t => t.Path.Length > 0))
                        {
                            var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path));
                            var where = srcTex.Contains(name) ? "own" : ds3Index.TryGetValue(name, out var b) ? b : "none";
                            sb.AppendLine($"{plan.Name}\t{plan.NewModelId}{(lod ? "_l" : "")}\tds3\t{ItemText.Leaf(f.Name)}\t{m.Name}\t{ItemText.Leaf(m.MTD)}\t{t.ParamName}\t{name}\t{where}");
                        }

                var modPath = ctx.ModPath(["parts", ModelPort.PartsPath(plan.NewModelId!.Value, lod)[7..]]);
                if (!File.Exists(modPath))
                {
                    problems.Add($"no mod file {Path.GetFileName(modPath)}");
                    continue;
                }
                var mod = BND4.Read(File.ReadAllBytes(modPath));
                var modTex = mod.Files.Where(f => TPF.Is(f.Bytes)).SelectMany(f => TPF.Read(f.Bytes).Textures)
                    .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in mod.Files.Where(f => FLVER2.Is(f.Bytes)))
                    foreach (var m in FLVER2.Read(f.Bytes).Materials)
                    {
                        var matbinName = ModelPort.MatbinName(m.MTD);
                        if (!matbins.TryGetValue(matbinName, out var matbin))
                        {
                            problems.Add($"{(lod ? "LOD " : "")}{ItemText.Leaf(f.Name)}: no MATBIN {matbinName}");
                            continue;
                        }
                        foreach (var s in matbin.Samplers.Where(s => s.Path.Length > 0))
                        {
                            var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(s.Path));
                            bool shared = s.Path.Contains(@"\parts\tex\", StringComparison.OrdinalIgnoreCase)
                                || s.Path.Contains(@"\systex\", StringComparison.OrdinalIgnoreCase);
                            var status = modTex.Contains(name) ? "ok" : shared ? "shared" : "MISSING";
                            sb.AppendLine($"{plan.Name}\t{plan.NewModelId}{(lod ? "_l" : "")}\tmod\t{ItemText.Leaf(f.Name)}\t{m.Name}\t{matbinName}\t{s.Type}\t{name}\t{status}");
                            if (status == "MISSING")
                                problems.Add($"{(lod ? "LOD " : "")}{ItemText.Leaf(f.Name)} {Kind(s.Type)} -> {name} missing");
                        }
                    }
            }
            rows.Add((i, sb.ToString()));
            if (problems.Count > 0)
                bad.Add((i, $"{plan.Name} (wp_a_{plan.NewModelId}, DS3 wp_a_{ds3Model:D4}): {string.Join("; ", problems.Distinct().Take(6))}"));
        });

        WriteStats(ctx, er, ds3Weapons, plans, convert ? ds3 : null);

        var report = ctx.WorkPath("reports", "texture-check.tsv");
        File.WriteAllText(report, "weapon\tmodel\tside\tflver\tmaterial\tmtd/matbin\tslot\ttexture\twhere\n"
            + string.Concat(rows.OrderBy(r => r.Order).Select(r => r.Line)));
        Console.WriteLine($"texture-check: {plans.Count} weapons, {bad.Count} with textures the game can't find");
        foreach (var b in bad.OrderBy(b => b.Order).Take(60))
            Console.WriteLine("  " + b.Line);
        Console.WriteLine($"report: {report}");
        return 0;
    }

    /// <summary>
    /// Texture formats, sizes and average colours, and mesh vertex colours, of the ported models and of the Elden Ring
    /// template models they were built on: Work\reports\texture-stats.tsv. Shows what differs between a weapon that
    /// renders right and one that renders grey even when every texture is found.
    /// </summary>
    private static void WriteStats(PorterContext ctx, GameArchives er, PARAM ds3Weapons, List<WeaponPlan> plans, GameArchives? ds3)
    {
        var erWeapons = ctx.LoadParams(PorterGame.EldenRing).Get("EquipParamWeapon")!;
        var jobs = new List<(string Label, Func<byte[]?> Read)>();
        if (ds3 is not null)
            foreach (var plan in plans)
            {
                int ds3Model = Convert.ToInt32(ds3Weapons[plan.Ds3Id]!["equipModelId"].Value);
                int templateModel = Convert.ToInt32(erWeapons[plan.TemplateId!.Value]!["equipModelId"].Value);
                jobs.Add(($"{plan.Name}\tnew {plan.NewModelId}", () =>
                    ModelPort.Convert(ds3, er, ds3Model, templateModel, plan.NewModelId!.Value, false)?.Binder.Write()));
            }
        foreach (var plan in plans)
        {
            var modPath = ctx.ModPath(["parts", ModelPort.PartsPath(plan.NewModelId!.Value)[7..]]);
            jobs.Add(($"{plan.Name}\tmod {plan.NewModelId}", () => File.Exists(modPath) ? File.ReadAllBytes(modPath) : null));
        }
        foreach (var t in plans.Select(p => p.TemplateId!.Value).Distinct())
        {
            int model = Convert.ToInt32(erWeapons[t]!["equipModelId"].Value);
            var path = ModelPort.PartsPath(model);
            jobs.Add(($"ER template {plans.First(p => p.TemplateId == t).TemplateName}\ter {model:D4}", () => er.Contains(path) ? er.Read(path) : null));
        }
        var lines = new string[jobs.Count];
        Parallel.For(0, jobs.Count, Parallelism.Options, i =>
        {
            var sb = new StringBuilder();
            try
            {
                var bytes = jobs[i].Read();
                if (bytes is null) { lines[i] = ""; return; }
                var bnd = BND4.Read(bytes);
                foreach (var f in bnd.Files)
                {
                    if (TPF.Is(f.Bytes))
                        foreach (var t in TPF.Read(f.Bytes).Textures)
                            sb.AppendLine($"{jobs[i].Label}\ttex\t{t.Name}\t{TexInfo(t)}");
                    else if (FLVER2.Is(f.Bytes))
                    {
                        var flver = FLVER2.Read(f.Bytes);
                        foreach (var mesh in flver.Meshes)
                        {
                            var v = mesh.Vertices;
                            int n = v.Count == 0 ? 1 : v.Count;
                            int colors = v.Count == 0 ? 0 : v.Min(x => x.Colors.Count);
                            var parts = new List<string>();
                            for (int c = 0; c < colors; c++)
                                parts.Add($"c{c}=({v.Average(x => x.Colors[c].R):0.00},{v.Average(x => x.Colors[c].G):0.00},{v.Average(x => x.Colors[c].B):0.00},{v.Average(x => x.Colors[c].A):0.00})");
                            var uv = v.Count == 0 || v[0].UVs.Count == 0 ? "" :
                                $" uv=({v.Min(x => x.UVs[0].X):0.00}..{v.Max(x => x.UVs[0].X):0.00},{v.Min(x => x.UVs[0].Y):0.00}..{v.Max(x => x.UVs[0].Y):0.00}) uvs={v[0].UVs.Count}";
                            var layout = string.Join(" ", mesh.VertexBuffers.SelectMany(b => flver.BufferLayouts[b.LayoutIndex]).Select(m => $"{m.Semantic}{m.Index}:{m.Type}"));
                            sb.AppendLine($"{jobs[i].Label}\tmesh\t{ItemText.Leaf(f.Name)} mat={flver.Materials[mesh.MaterialIndex].Name}\tverts={v.Count} {string.Join(" ", parts)}{uv} layout=[{layout}]");
                        }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine($"{jobs[i].Label}\terror\t{ex.GetType().Name}: {ex.Message}"); }
            lines[i] = sb.ToString();
        });
        var path2 = ctx.WorkPath("reports", "texture-stats.tsv");
        File.WriteAllText(path2, string.Concat(lines));
        Console.WriteLine($"stats: {path2}");
    }

    // Format, size and the average colour of the texture's smallest mip of at least 16x16.
    private static string TexInfo(TPF.Texture t)
    {
        try
        {
            using var ms = new MemoryStream(t.Bytes);
            var dds = DdsFile.Load(ms);
            var h = dds.header;
            var fmt = dds.dx10Header.dxgiFormat != 0 ? dds.dx10Header.dxgiFormat.ToString()
                : System.Text.Encoding.ASCII.GetString(t.Bytes, 84, 4);
            var mips = new BcDecoder().DecodeAllMipMaps2D(dds);
            var mip = mips.LastOrDefault(m => m.Width >= 16 && m.Height >= 16);
            if (mip.Width == 0) mip = mips[0];
            double r = 0, g = 0, b = 0, a = 0; long n = 0;
            var span = mip.Span;
            for (int y = 0; y < span.Height; y++)
                for (int x = 0; x < span.Width; x++)
                {
                    var p = span[y, x]; r += p.r; g += p.g; b += p.b; a += p.a; n++;
                }
            return $"fmt={t.Format} {fmt} {h.dwWidth}x{h.dwHeight} mips={h.dwMipMapCount} avg=({r / n:0},{g / n:0},{b / n:0},{a / n:0})";
        }
        catch (Exception ex) { return $"fmt={t.Format} (not decoded: {ex.GetType().Name}: {ex.Message})"; }
    }

    private static string Kind(string type) =>
        type.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ? "albedo"
        : type.Contains("Normal", StringComparison.OrdinalIgnoreCase) ? "normal"
        : type.Contains("Metallic", StringComparison.OrdinalIgnoreCase) ? "metallic" : type;
}
