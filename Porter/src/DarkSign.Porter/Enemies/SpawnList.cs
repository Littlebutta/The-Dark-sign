using DarkSign.Porter.Maps;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// darksign_spawn_list.tsv: the characters the F1 menu's "Spawn enemies" tab can spawn, written by the enemy port
/// next to darksign_chr_map.tsv. One line per character: "game&lt;TAB&gt;model&lt;TAB&gt;NpcParam&lt;TAB&gt;ThinkParam&lt;TAB&gt;boss 0|1&lt;TAB&gt;name",
/// game "DS3" (the ported characters, the same rows the DS3 maps place) or "ER" (Elden Ring's own map enemies, the
/// enemy port's template catalog). The test menu (TestMenu/src/spawn.c) reads it; keep the two in step.
/// </summary>
public static class SpawnList
{
    public const string FileName = "darksign_spawn_list.tsv";
    public const string Header = "# game\tmodel\tNpcParam\tThinkParam\tboss\tname (written by DarkSignPorter port-maps, enemy port; read by the F1 menu)";

    public static string Line(string game, string model, int npc, int think, bool boss, string name) =>
        string.Join('\t', game, model, npc, think, boss ? 1 : 0, Clean(name));

    private static string Clean(string name) => name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// The list: the ported DS3 characters (the "chr" lines of the chr map: one per DS3 character) first, then Elden
    /// Ring's, one per model and name, bosses after the rest, each part sorted by name.
    /// </summary>
    public static List<string> Lines(IEnumerable<string> chrMapLines, IEnumerable<EnemyPort.ErEnemy> erCatalog, ISet<string> ds3Bosses)
    {
        var lines = new List<string> { Header };
        var ds3 = new List<(bool Boss, string Name, string Line)>();
        foreach (var line in chrMapLines)
        {
            var p = line.Split('\t');
            // chr, DS3 model, Elden Ring model, NpcParam, ThinkParam, name
            if (line.StartsWith('#') || p.Length < 6 || p[0] != "chr" || !int.TryParse(p[3], out var npc) || !int.TryParse(p[4], out var think))
                continue;
            var boss = ds3Bosses.Contains(p[1]);
            ds3.Add((boss, p[5], Line("DS3", p[2], npc, think, boss, p[5])));
        }
        lines.AddRange(ds3.OrderBy(d => d.Boss).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => d.Line));

        var seen = new HashSet<(string, string)>();
        var er = new List<(bool Boss, string Name, string Line)>();
        foreach (var e in erCatalog)
        {
            if (e.NpcParam <= 0 || !seen.Add((e.Model.ToLowerInvariant(), e.Name)))
                continue;
            er.Add((e.Boss, e.Name, Line("ER", e.Model, e.NpcParam, e.ThinkParam, e.Boss, e.Name)));
        }
        lines.AddRange(er.OrderBy(d => d.Boss).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => d.Line));
        return lines;
    }

    public static void Write(PorterContext ctx, IEnumerable<string> chrMapLines, IEnumerable<EnemyPort.ErEnemy> erCatalog, ISet<string> ds3Bosses)
    {
        var lines = Lines(chrMapLines, erCatalog, ds3Bosses);
        File.WriteAllLines(ctx.ModPath(FileName), lines);
        Console.WriteLine($"enemies: F1 spawn list: {lines.Count(l => l.StartsWith("DS3\t"))} DS3, {lines.Count(l => l.StartsWith("ER\t"))} Elden Ring characters");
    }
}
