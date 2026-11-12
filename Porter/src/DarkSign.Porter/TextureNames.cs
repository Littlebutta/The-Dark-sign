using System.Collections.Concurrent;
using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// `texture-names`: finds textures the mod adds under a name Elden Ring already uses, or under a name two mod files
/// both use. The game looks a texture up by its name alone (a material's sampler path is only read for the file
/// name), so two loaded texture binders holding the same name share one texture: when the character that loaded it
/// first is unloaded, the other character's surface goes untextured, and while both are loaded one of them may draw
/// the other's picture. That fits a texture that "sometimes disappears" (a player's armour piece during a fight).
/// <para>Reads every texture binder in Elden Ring's parts\ and chr\ folders and every one the mod writes (parts\,
/// chr\, asset\), and writes Work\reports\texture-names.tsv: one line per shared name with the mod file and the files
/// it collides with. `texture-names er:/parts/bd_m_1290` (any archive path prefix) also lists the textures and
/// material samplers of those Elden Ring binders, to see which names one armour piece needs.</para>
/// </summary>
public static class TextureNames
{
    /// <summary>The folders whose texture binders are character and armour resources.</summary>
    private static readonly string[] Folders = ["/parts/", "/chr/"];

    public static bool IsTextureHolder(string path)
    {
        var p = path.ToLowerInvariant();
        return p.EndsWith(".partsbnd.dcx") || p.EndsWith(".texbnd.dcx") || p.EndsWith(".tpf.dcx") || p.EndsWith(".tpf")
            || p.EndsWith(".chrbnd.dcx") || p.EndsWith(".geombnd.dcx") || p.EndsWith(".partsbnd") || p.EndsWith(".texbnd");
    }

    /// <summary>Every texture name in a file (binders and texture binders nested in binders), with its size in bytes.</summary>
    public static IEnumerable<(string Name, int Bytes)> Textures(byte[] bytes)
    {
        if (DCX.Is(bytes))
            bytes = DCX.Decompress(bytes);
        if (TPF.Is(bytes))
        {
            foreach (var t in TPF.Read(bytes).Textures)
                yield return (t.Name, t.Bytes.Length);
            yield break;
        }
        if (BND4.Is(bytes))
            foreach (var f in BND4.Read(bytes).Files)
                foreach (var t in Textures(f.Bytes))
                    yield return t;
    }

    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        foreach (var prefix in args.Where(a => a.StartsWith("er:", StringComparison.OrdinalIgnoreCase)))
            ListVanilla(er, prefix[3..]);

        // Elden Ring's own names: name -> the files that hold it.
        var vanilla = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
        var erFiles = er.Entries.Select(e => e.Path).OfType<string>()
            .Where(p => Folders.Any(f => p.StartsWith(f, StringComparison.OrdinalIgnoreCase)) && IsTextureHolder(p)).ToList();
        int unreadable = 0;
        Parallel.ForEach(erFiles, Parallelism.Options, path =>
        {
            try
            {
                foreach (var (name, _) in Textures(er.ReadRaw(path)))
                    vanilla.GetOrAdd(name, _ => []).Add(path);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Interlocked.Increment(ref unreadable);
            }
        });
        Console.WriteLine($"Elden Ring: {vanilla.Count} texture names in {erFiles.Count} parts/chr files ({unreadable} unreadable)");

        // The mod's names: name -> (file, size).
        var modRoot = ctx.ModPath();
        var modFiles = new[] { "parts", "chr", "asset" }.Select(d => Path.Combine(modRoot, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)).Where(IsTextureHolder).ToList();
        var mod = new ConcurrentDictionary<string, ConcurrentBag<(string File, int Bytes)>>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(modFiles, Parallelism.Options, file =>
        {
            var relative = "/" + Path.GetRelativePath(modRoot, file).Replace('\\', '/');
            try
            {
                foreach (var (name, size) in Textures(File.ReadAllBytes(file)))
                    mod.GetOrAdd(name, _ => []).Add((relative, size));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"warning: {relative}: {e.GetType().Name}: {e.Message}");
            }
        });
        Console.WriteLine($"mod: {mod.Count} texture names in {modFiles.Count} files");

        var sb = new StringBuilder("kind\ttexture\tmod files\tother files\n");
        int withVanilla = 0, betweenMod = 0;
        var byFolder = new Dictionary<string, int>();
        foreach (var (name, holders) in mod.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mine = holders.Select(h => h.File).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
            // A mod file that replaces the vanilla file of the same path is not a collision with that file.
            var theirs = vanilla.TryGetValue(name, out var v)
                ? v.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !mine.Contains(p, StringComparer.OrdinalIgnoreCase)).Order().ToList()
                : [];
            if (theirs.Count > 0)
            {
                withVanilla++;
                sb.Append("elden-ring\t").Append(name).Append('\t').Append(string.Join(" ", mine)).Append('\t').AppendLine(string.Join(" ", theirs.Take(12)) + (theirs.Count > 12 ? $" (+{theirs.Count - 12})" : ""));
                foreach (var f in mine)
                {
                    var folder = f.Split('/')[1];
                    byFolder[folder] = byFolder.GetValueOrDefault(folder) + 1;
                }
            }
            // The same name in two mod files of different models (a model's own _l copy and its m/f twin count as one).
            var models = mine.Select(ModelKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (models.Count > 1)
            {
                betweenMod++;
                sb.Append("mod\t").Append(name).Append('\t').Append(string.Join(" ", mine)).AppendLine("\t");
            }
        }
        Materials(ctx, er, sb);
        var report = ctx.WorkPath("reports", "texture-names.tsv");
        File.WriteAllText(report, sb.ToString());
        Console.WriteLine($"names the mod shares with Elden Ring's own parts/chr files: {withVanilla}"
            + (byFolder.Count > 0 ? $" ({string.Join(", ", byFolder.Select(b => $"{b.Value} in mod {b.Key}\\"))})" : ""));
        Console.WriteLine($"names two different mod models share: {betweenMod}");
        foreach (var line in sb.ToString().Split('\n').Skip(1).Where(l => l.Length > 0).Take(40))
            Console.WriteLine("  " + line.Replace('\t', ' '));
        Console.WriteLine($"report: {report}");
        return 0;
    }

    /// <summary>
    /// Materials are found by name too. For each material binder the mod writes: vanilla materials it changed or
    /// left out (an Elden Ring armour piece would lose its look), and materials it added under a name another
    /// binder already has.
    /// </summary>
    private static void Materials(PorterContext ctx, GameArchives er, StringBuilder sb)
    {
        var vanilla = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var binder in Explore.MaterialBinders.Where(er.Contains))
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in BND4.Read(er.Read(binder)).Files)
                files.TryAdd(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)), f.Bytes);
            vanilla[binder] = files;
        }
        var added = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var binder in Explore.MaterialBinders)
        {
            var modFile = ctx.ModPath(["material", ItemText.Leaf(binder)]);
            if (!File.Exists(modFile))
                continue;
            var own = vanilla.GetValueOrDefault(binder) ?? [];
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            int twice = 0;
            foreach (var f in BND4.Read(DCX.Decompress(File.ReadAllBytes(modFile))).Files)
                if (!files.TryAdd(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)), f.Bytes))
                    twice++;
            var changed = own.Where(o => files.TryGetValue(o.Key, out var b) && !b.AsSpan().SequenceEqual(o.Value)).Select(o => o.Key).ToList();
            var missing = own.Keys.Where(k => !files.ContainsKey(k)).ToList();
            foreach (var name in files.Keys.Where(k => !own.ContainsKey(k)))
                added.GetOrAdd(name).Add(ItemText.Leaf(binder));
            Console.WriteLine($"materials {ItemText.Leaf(binder)}: {files.Count} in the mod's copy, {own.Count} in Elden Ring's; "
                + $"{changed.Count} Elden Ring materials changed, {missing.Count} left out, {twice} names twice");
            foreach (var name in changed)
                sb.Append("material-changed\t").Append(name).Append('\t').Append(ItemText.Leaf(binder)).AppendLine("\t");
            foreach (var name in missing)
                sb.Append("material-missing\t").Append(name).Append('\t').Append(ItemText.Leaf(binder)).AppendLine("\t");
        }
        int clashes = 0;
        foreach (var (name, binders) in added.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
        {
            var theirs = vanilla.Where(v => v.Value.ContainsKey(name)).Select(v => ItemText.Leaf(v.Key)).ToList();
            if (theirs.Count == 0 && binders.Count == 1)
                continue;
            clashes++;
            sb.Append("material-name\t").Append(name).Append('\t').Append(string.Join(" ", binders)).Append('\t').AppendLine(string.Join(" ", theirs));
        }
        Console.WriteLine($"materials the mod adds under a name another binder has: {clashes}");
    }

    private static List<string> GetOrAdd(this Dictionary<string, List<string>> d, string key) =>
        d.TryGetValue(key, out var list) ? list : d[key] = [];

    /// <summary>"/parts/bd_f_5003_l.partsbnd.dcx" -> "parts/bd_5003": a model's high/low detail and male/female files are one model.</summary>
    public static string ModelKey(string modFile)
    {
        var leaf = ItemText.Leaf(modFile).ToLowerInvariant();
        var dot = leaf.IndexOf('.');
        if (dot > 0)
            leaf = leaf[..dot];
        if (leaf.EndsWith("_l") || leaf.EndsWith("_h"))
            leaf = leaf[..^2];
        leaf = System.Text.RegularExpressions.Regex.Replace(leaf, @"^(hd|bd|am|lg)_[mfa]_", "$1_");
        return modFile.Split('/')[1] + "/" + leaf;
    }

    /// <summary>Prints the texture names and material samplers of the Elden Ring files under a path prefix.</summary>
    private static void ListVanilla(GameArchives er, string prefix)
    {
        foreach (var path in er.Entries.Select(e => e.Path).OfType<string>().Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order())
        {
            Console.WriteLine($"== {path}");
            var bytes = er.Read(path);
            if (!BND4.Is(bytes))
                continue;
            foreach (var f in BND4.Read(bytes).Files)
            {
                if (TPF.Is(f.Bytes))
                    foreach (var t in TPF.Read(f.Bytes).Textures)
                        Console.WriteLine($"   texture {t.Name}");
                else if (FLVER2.Is(f.Bytes))
                    foreach (var m in FLVER2.Read(f.Bytes).Materials)
                        Console.WriteLine($"   material {m.Name}: {ModelPort.MatbinName(m.MTD)}");
            }
        }
    }
}
