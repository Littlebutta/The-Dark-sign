using DarkSign.Porter.Maps;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// darksign_spawn_list.tsv: the characters the F1 menu's "Spawn enemies" tab can spawn, written by the enemy port
/// next to darksign_chr_map.tsv. One line per character:
/// "game&lt;TAB&gt;model&lt;TAB&gt;NpcParam&lt;TAB&gt;ThinkParam&lt;TAB&gt;boss 0|1&lt;TAB&gt;name&lt;TAB&gt;body ds3|er",
/// game "DS3" (the ported characters, the same rows the DS3 maps place) or "ER" (Elden Ring's own map enemies, the
/// enemy port's template catalog). The body says whose model the character wears: "ds3" only for a model the port
/// built and wrote, "er" for an Elden Ring body (every ER line, and a DS3 character left on its template). The menu
/// used to guess this from the model's number (c7000-c7999), and Elden Ring ships characters in that range itself
/// (c7000, the Fallen Hawks Soldier): a DS3 character left on that body was listed as having its DS3 model.
/// The test menu (TestMenu/src/spawn.c) reads it; keep the two in step.
/// An optional eighth column, "from cNNNN:NpcParam:ThinkParam", names the character a DS3 enemy grows out of
/// (<see cref="Transforms"/>): the menu spawns that one first and swaps in the listed one when it transforms.
/// </summary>
public static class SpawnList
{
    public const string FileName = "darksign_spawn_list.tsv";
    public const string Header = "# game\tmodel\tNpcParam\tThinkParam\tboss\tname\tbody\tfrom (written by DarkSignPorter port-maps, enemy port; read by the F1 menu)";

    /// <summary>
    /// DS3 enemies that start as another character: High Wall's Pus of Man bursts out of a hollow (m30_00_00_00.msb:
    /// hollow 3000610, NpcParam 110070 / ThinkParam 110054, turns into the Pus 3000611, NpcParam 109000, at the same
    /// spot; common_func 20005415). DS3's map script swaps them, which the F1 menu's debug spawns never run, so the menu
    /// does the swap itself (fallback spawn-transform B, DKSIGN_NO_TRANSFORM=1 at launch, turns it off; 2026-11-12 playtest: "spawn as the regular undead first then transform ... if killed before
    /// transforming it just dies"). Keyed by the DS3 model; the DS3 rows are looked up in the chr map's npc and think lines.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (int HollowNpc, int HollowThink)> Transforms =
        new Dictionary<string, (int, int)> { ["c1090"] = (110070, 110054) };

    public static string Line(string game, string model, int npc, int think, bool boss, string name, bool ds3Body) =>
        string.Join('\t', game, model, npc, think, boss ? 1 : 0, Clean(name), ds3Body ? "ds3" : "er");

    private static string Clean(string name) => name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// The list: the ported DS3 characters (the "chr" lines of the chr map: one per DS3 character) first, then Elden
    /// Ring's, one per model and name, bosses after the rest, each part sorted by name.
    /// </summary>
    /// <param name="portedModels">The models the port built and wrote (c7xxx); any other DS3 line is on an Elden Ring body.</param>
    public static List<string> Lines(IEnumerable<string> chrMapLines, IEnumerable<EnemyPort.ErEnemy> erCatalog, ISet<string> ds3Bosses,
        ISet<string> portedModels, IEnumerable<string>? testLines = null)
    {
        // The test set first (ChrPort.TestPlans): ready-made lines, at the top where they are easy to find.
        var lines = new List<string> { Header };
        lines.AddRange(testLines ?? []);
        var ds3 = new List<(bool Boss, string Name, string Line)>();
        var mapLines = chrMapLines as IReadOnlyCollection<string> ?? chrMapLines.ToList();
        // The chr map's npc lines (npc, DS3 NpcParam, model, NpcParam, ThinkParam, name) and think lines (think, DS3
        // ThinkParam, model, ThinkParam, name), for the characters an enemy grows out of.
        var npcRows = new Dictionary<int, (string Model, int Npc, int Think)>();
        var thinkRows = new Dictionary<(int, string), int>();
        foreach (var p in mapLines.Where(l => !l.StartsWith('#')).Select(l => l.Split('\t')))
            if (p.Length >= 5 && p[0] == "npc" && int.TryParse(p[1], out var dn) && int.TryParse(p[3], out var en) && int.TryParse(p[4], out var eth))
                npcRows.TryAdd(dn, (p[2], en, eth));
            else if (p.Length >= 4 && p[0] == "think" && int.TryParse(p[1], out var dt) && int.TryParse(p[3], out var et))
                thinkRows.TryAdd((dt, p[2]), et);
        string? From(string ds3Model)
        {
            if (!Transforms.TryGetValue(ds3Model, out var t) || !npcRows.TryGetValue(t.HollowNpc, out var hollow))
                return null;
            return $"from {hollow.Model}:{hollow.Npc}:{(thinkRows.TryGetValue((t.HollowThink, hollow.Model), out var think) ? think : hollow.Think)}";
        }
        foreach (var line in mapLines)
        {
            var p = line.Split('\t');
            // chr, DS3 model, Elden Ring model, NpcParam, ThinkParam, name
            if (line.StartsWith('#') || p.Length < 6 || p[0] != "chr" || !int.TryParse(p[3], out var npc) || !int.TryParse(p[4], out var think))
                continue;
            var boss = ds3Bosses.Contains(p[1]);
            var entry = Line("DS3", p[2], npc, think, boss, p[5], portedModels.Contains(p[2]));
            ds3.Add((boss, p[5], From(p[1]) is { } from ? entry + "\t" + from : entry));
        }
        lines.AddRange(ds3.OrderBy(d => d.Boss).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => d.Line));

        var seen = new HashSet<(string, string)>();
        var er = new List<(bool Boss, string Name, string Line)>();
        foreach (var e in erCatalog)
        {
            if (e.NpcParam <= 0 || !seen.Add((e.Model.ToLowerInvariant(), e.Name)))
                continue;
            er.Add((e.Boss, e.Name, Line("ER", e.Model, e.NpcParam, e.ThinkParam, e.Boss, e.Name, false)));
        }
        lines.AddRange(er.OrderBy(d => d.Boss).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => d.Line));
        return lines;
    }

    public static void Write(PorterContext ctx, IEnumerable<string> chrMapLines, IEnumerable<EnemyPort.ErEnemy> erCatalog, ISet<string> ds3Bosses,
        ISet<string> portedModels, IEnumerable<string>? testLines = null)
    {
        var lines = Lines(chrMapLines, erCatalog, ds3Bosses, portedModels, testLines);
        File.WriteAllLines(ctx.ModPath(FileName), lines);
        Console.WriteLine($"enemies: F1 spawn list: {lines.Count(l => l.StartsWith("DS3\t"))} DS3 ({lines.Count(l => l.StartsWith("DS3\t") && l.EndsWith("\tds3"))} with their DS3 model), " +
                          $"{lines.Count(l => l.StartsWith("ER\t"))} Elden Ring characters");
    }
}
