using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// chr-survey: the first step of porting an enemy or boss (plan: "Bring Midir and Friede into Elden Ring").
/// For each character ID it lists every archive file named after it (models, textures, animations, behaviour,
/// effects, sounds), what is inside each binder (FLVER bones/dummies/materials, TAE event types, Havok versions),
/// the AI scripts named after it, and its NpcParam / NpcThinkParam rows. Run it on the DS3 bosses and on an
/// Elden Ring template enemy to compare. Reads only; writes one report per character to Work\reports\chr.
/// </summary>
public static class ChrSurvey
{
    public static int Run(PorterContext ctx, PorterGame game, IReadOnlyList<string> ids)
    {
        using var archives = ctx.OpenArchives(game);
        var param = ctx.LoadParams(game);
        TAE.ValidateEventBank = false;
        var luabnds = archives.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/script/", StringComparison.OrdinalIgnoreCase)
            && e.Path.Contains(".luabnd", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).OrderBy(p => p).ToList();

        foreach (var idText in ids)
        {
            int chr = int.Parse(idText.TrimStart('c', 'C'));
            string tag = $"c{chr:D4}";
            var text = new StringBuilder();
            text.AppendLine($"# {game.ShortName} {tag}");

            var files = archives.Entries.Where(e => e.Path is not null && e.Path.Contains(tag, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Path!).OrderBy(p => p).ToList();
            text.AppendLine($"\n## Archive files ({files.Count})");
            long totalBytes = 0;
            foreach (var path in files)
            {
                byte[] bytes;
                try { bytes = archives.Read(path); }
                catch (Exception ex) { text.AppendLine($"{path}: unreadable ({ex.Message})"); continue; }
                totalBytes += bytes.Length;
                text.AppendLine($"{path} ({bytes.Length:N0} bytes)");
                if (BND4.Is(bytes))
                    DescribeBinder(BND4.Read(bytes), text);
            }

            text.AppendLine("\n## AI scripts named after it");
            string prefix = chr.ToString();
            foreach (var path in luabnds)
            {
                BND4 bnd;
                try { bnd = BND4.Read(archives.Read(path)); }
                catch { continue; }
                foreach (var f in bnd.Files.Where(f => ItemText.Leaf(f.Name).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    text.AppendLine($"{path} :: {ItemText.Leaf(f.Name)} ({f.Bytes.Length:N0} bytes, {LuaKind(f.Bytes)})");
            }

            text.AppendLine("\n## Params (rows whose ID / 100 is the character ID)");
            foreach (var name in new[] { "NpcParam", "NpcThinkParam" })
            {
                var p = param.Get(name);
                if (p is null) { text.AppendLine($"{name}: missing"); continue; }
                foreach (var row in p.Rows.Where(r => r.ID / 100 == chr))
                {
                    var cells = row.Cells.Where(c => c.Def.InternalName is "behaviorVariationId" or "hp" or "soulValue" or "logicId"
                        or "battleGoalID" or "modelDispMask0" or "npcType" or "teamType" or "spEffectID0" or "chrType")
                        .Select(c => $"{c.Def.InternalName}={c.Value}");
                    text.AppendLine($"{name} {row.ID} '{row.Name}' {string.Join(" ", cells)}");
                }
            }
            foreach (var name in new[] { "BehaviorParam", "AtkParam_Npc", "Bullet", "SpEffectParam" })
            {
                var p = param.Get(name);
                int rows = p?.Rows.Count(r => r.ID.ToString().StartsWith(prefix)) ?? -1;
                text.AppendLine($"{name}: {rows} rows whose ID starts with {prefix}");
            }

            var path2 = ctx.WorkPath("reports", "chr", $"{game.ShortName}-{tag}.txt");
            File.WriteAllText(path2, text.ToString());
            Console.WriteLine($"{game.ShortName} {tag}: {files.Count} files, {totalBytes / 1048576.0:0.0} MB -> {path2}");
        }
        return 0;
    }

    static void DescribeBinder(BND4 bnd, StringBuilder text)
    {
        var byExt = bnd.Files.GroupBy(f => Path.GetExtension(ItemText.Leaf(f.Name)).ToLowerInvariant())
            .Select(g => $"{g.Key}x{g.Count()}");
        text.AppendLine($"  {bnd.Files.Count} files: {string.Join(" ", byExt)}");
        var hkxVersions = new Dictionary<string, int>();
        foreach (var f in bnd.Files)
        {
            var leaf = ItemText.Leaf(f.Name);
            if (FLVER2.Is(f.Bytes))
            {
                var flver = FLVER2.Read(f.Bytes);
                text.AppendLine($"  {leaf}: flver 0x{flver.Header.Version:X} bones={flver.Nodes.Count} dummies={flver.Dummies.Count} " +
                    $"(ids {string.Join(",", flver.Dummies.Select(d => d.ReferenceID).Distinct().Order().Take(40))}) meshes={flver.Meshes.Count}");
                foreach (var mtd in flver.Materials.Select(m => m.MTD).Distinct())
                    text.AppendLine($"    material {mtd}");
            }
            else if (leaf.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var tae = TAE.Read(f.Bytes);
                    var types = tae.Animations.SelectMany(a => a.Events).GroupBy(e => e.Type).OrderBy(g => g.Key)
                        .Select(g => $"{g.Key}x{g.Count()}");
                    text.AppendLine($"  {leaf}: {tae.Animations.Count} animations, event types {string.Join(" ", types)}");
                }
                catch (Exception ex) { text.AppendLine($"  {leaf}: TAE unreadable ({ex.Message})"); }
            }
            else if (leaf.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
            {
                var v = HavokVersion(f.Bytes);
                hkxVersions[v] = hkxVersions.GetValueOrDefault(v) + 1;
                if (!leaf.StartsWith("a", StringComparison.OrdinalIgnoreCase))
                    text.AppendLine($"  {leaf}: {f.Bytes.Length:N0} bytes, {v}");
            }
        }
        if (hkxVersions.Count > 0)
            text.AppendLine($"  hkx versions: {string.Join(" ", hkxVersions.Select(kv => $"{kv.Key}x{kv.Value}"))}");
    }

    /// <summary>The Havok SDK string in an HKX header ("hk_2014.1.0-r1" in DS3 packfiles, "20180100" in Elden Ring tagfiles).</summary>
    static string HavokVersion(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 256));
        int at = head.IndexOf("hk_20", StringComparison.Ordinal);
        if (at >= 0)
            return head.Substring(at, Math.Min(14, head.Length - at)).TrimEnd('\0');
        at = head.IndexOf("2018", StringComparison.Ordinal);
        return at >= 0 ? "tagfile-2018" : "unknown";
    }

    static string LuaKind(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == 0x1B && bytes[1] == (byte)'L' ? $"compiled lua 0x{bytes[4]:X2}"
        : bytes.Length > 4 && bytes[0] == 0x1B ? "compiled (other)" : "plain text";
}
