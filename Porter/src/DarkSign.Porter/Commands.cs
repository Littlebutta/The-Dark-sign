using System.Text.Json;
using SoulsFormats;

namespace DarkSign.Porter;

public static class Commands
{
    /// <summary>Opens every archive of both games and writes a file index per game.</summary>
    public static int Scan(PorterContext ctx)
    {
        foreach (var game in PorterGame.All)
        {
            using var archives = ctx.OpenArchives(game);
            var indexPath = ctx.WorkPath("index", $"{game.ShortName}.tsv");
            using (var w = new StreamWriter(indexPath))
            {
                w.WriteLine("archive\thash\tsize\tpath");
                foreach (var e in archives.Entries.OrderBy(e => e.Path ?? "~").ThenBy(e => e.Hash))
                    w.WriteLine($"{e.Archive}\t{e.Hash:x}\t{e.Header.PaddedFileSize}\t{e.Path}");
            }

            Console.WriteLine($"{game.ShortName}: {archives.Entries.Count} files");
            foreach (var group in archives.Entries.GroupBy(e => e.Archive).OrderBy(g => g.Key))
                Console.WriteLine($"  {group.Key,-14} {group.Count(),7} files, {group.Count(e => e.Path is not null),7} named");
            Console.WriteLine($"  index: {indexPath}");
        }
        return 0;
    }

    /// <summary>Copies files out of a game's archives into Work\extract\&lt;game&gt;, decompressed.</summary>
    public static int Extract(PorterContext ctx, PorterGame game, IReadOnlyList<string> patterns)
    {
        using var archives = ctx.OpenArchives(game);
        var matches = archives.Entries
            .Where(e => e.Path is not null && patterns.Any(p => Matches(e.Path, p)))
            .OrderBy(e => e.Path)
            .ToList();
        foreach (var entry in matches)
        {
            var raw = archives.ReadRaw(entry);
            var bytes = DCX.Is(raw) ? DCX.Decompress(raw) : raw;
            var relative = entry.Path!.TrimStart('/');
            if (relative.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
                relative = relative[..^4];
            var outPath = ctx.WorkPath(["extract", game.ShortName, .. relative.Split('/')]);
            File.WriteAllBytes(outPath, bytes);
            Console.WriteLine(outPath);
        }
        Console.WriteLine($"{matches.Count} files extracted.");
        return matches.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Lists what DS3 has to port (weapons, armor, rings, spells, enemies) and what Elden Ring already uses,
    /// and writes it to Work\reports\inventory.json.
    /// </summary>
    public static int Inventory(PorterContext ctx)
    {
        var report = new Dictionary<string, object>();
        foreach (var game in PorterGame.All)
        {
            var store = ctx.LoadParams(game);
            var section = new Dictionary<string, object>();
            foreach (var (label, param, filter) in InventoryParams(game))
            {
                var p = store.Get(param);
                if (p is null)
                    continue;
                var rows = p.Rows.Where(filter).ToList();
                section[label] = new
                {
                    param,
                    count = rows.Count,
                    rows = rows.Select(r => new { id = r.ID, name = r.Name ?? "" }),
                };
                Console.WriteLine($"{game.ShortName} {label,-10} {rows.Count,5}  ({param})");
            }
            report[game.ShortName] = section;
        }

        var path = ctx.WorkPath("reports", "inventory.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"report: {path}");
        return 0;
    }

    // Base items only: in both games a weapon's upgrade levels and infusions are extra rows above the base ID.
    private static IEnumerable<(string Label, string Param, Func<PARAM.Row, bool> Filter)> InventoryParams(PorterGame game)
    {
        yield return ("weapons", "EquipParamWeapon", r => r.ID > 0 && r.ID % 10000 == 0);
        yield return ("armor", "EquipParamProtector", r => r.ID > 0);
        yield return ("rings", "EquipParamAccessory", r => r.ID > 0);
        yield return ("spells", "Magic", r => r.ID > 0);
        yield return ("npcs", "NpcParam", r => r.ID > 0);
    }

    private static bool Matches(string path, string pattern)
    {
        var p = pattern.Replace('\\', '/');
        if (!p.StartsWith('/'))
            p = "/" + p;
        if (p.EndsWith('*'))
            return path.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(path, p, StringComparison.OrdinalIgnoreCase);
    }
}
