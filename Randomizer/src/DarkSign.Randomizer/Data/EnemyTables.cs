using System.Globalization;
using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Data;

/// <summary>
/// The porter's enemy tables, which tell the randomizer what enemies really exist in this player's build. Both are
/// written by port-maps into Mods\ER (tab-separated, '#' lines are comments):
/// <list type="bullet">
/// <item><see cref="PlacedFile"/>: every DS3 enemy placed in a ported map:
/// DS3 map, DS3 part, DS3 entity, Elden Ring slot, Elden Ring part, Elden Ring entity, DS3 model, DS3 NpcParam, name,
/// boss (1/0), source ("ported" for a ported DS3 character, else "stand-in"), Elden Ring model, NpcParam, ThinkParam.</item>
/// <item><see cref="ErFile"/>: the Elden Ring enemies the map port's stand-ins come from (vanilla legacy-dungeon
/// placements): map, part, entity, model, NpcParam, ThinkParam, name, boss (1/0).</item>
/// </list>
/// With them, the enemy shuffle mixes real spots of both games with real characters of both games; without them
/// (no maps ported yet) it falls back to FogMod's DS3 enemy list and places nothing that isn't in the game.
/// </summary>
public static class EnemyTables
{
    public const string PlacedFile = "darksign_enemies.tsv";
    public const string ErFile = "darksign_er_enemies.tsv";

    public const string PlacedHeader = "# DS3 map\tDS3 part\tDS3 entity\tElden Ring slot\tElden Ring part\tElden Ring entity\tDS3 model\tDS3 NpcParam\tname\tboss\tsource\tmodel\tNpcParam\tThinkParam";
    public const string ErHeader = "# map\tpart\tentity\tmodel\tNpcParam\tThinkParam\tname\tboss";

    /// <summary>Adds the tables to the world: marks DS3 spots that are placed (with their slot and entity), adds
    /// Elden Ring spots in areas the world knows, collects every placeable character, and fills DS3 bosses' models.
    /// Returns a one-line summary.</summary>
    public static string Apply(World world, IEnumerable<string>? placed, IEnumerable<string>? er)
    {
        int placedSpots = 0, unknownSpots = 0, erSpots = 0;
        var seen = new HashSet<(SourceGame, string, int, int)>();
        void AddCharacter(Character c)
        {
            if (seen.Add((c.Game, c.Model, c.NpcParam, c.ThinkParam)))
                world.Characters.Add(c);
        }

        // FogMod's DS3 spots are "highwall c1100_0000 h001500": map alias, part, collision.
        var ds3Spots = new Dictionary<(string Map, string Part), EnemySpot>();
        foreach (var spot in world.Enemies.Where(e => e.Game == SourceGame.DS3))
        {
            var parts = spot.Id[4..].Split(' ');
            if (parts.Length > 1 && Ds3FogData.MapOf(parts[0]) is { } map)
                ds3Spots.TryAdd((map, parts[1]), spot);
        }

        foreach (var p in Rows(placed, 14))
        {
            var boss = p[9] == "1";
            if (ds3Spots.TryGetValue((p[0], p[1]), out var spot))
            {
                spot.Map = p[3];
                spot.PartName = p[4];
                spot.Entity = Long(p[5]);
                spot.Boss = boss;
                placedSpots++;
                if (boss)
                    foreach (var b in world.Bosses.Where(b => b.Game == SourceGame.DS3 && b.Area == spot.Area && b.Model is null))
                        b.Model = p[6];
            }
            else
                unknownSpots++;
            if (p[10] == "ported" && Int(p[12]) > 0)
                AddCharacter(new Character(SourceGame.DS3, p[11], Int(p[12]), Int(p[13]), p[8], boss, p[6]));
        }

        // An Elden Ring map belongs to the first area that lists it (er-world.yaml "Maps: [m10_00]").
        var areaByMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in world.Areas.Values.Where(a => a.Game == SourceGame.ER).OrderBy(a => a.Id, StringComparer.Ordinal))
            foreach (var m in a.Maps)
                areaByMap.TryAdd(m, a.Id);
        foreach (var p in Rows(er, 8))
        {
            var boss = p[7] == "1";
            AddCharacter(new Character(SourceGame.ER, p[3], Int(p[4]), Int(p[5]), p[6], boss, Map: p[0]));
            var shortMap = string.Join('_', p[0].Split('_').Take(2));
            if (!areaByMap.TryGetValue(p[0], out var area) && !areaByMap.TryGetValue(shortMap, out area))
                continue;
            world.Enemies.Add(new EnemySpot
            {
                Id = $"er:{p[0]} {p[1]}", Game = SourceGame.ER, Area = area, Name = p[6], Model = p[3],
                Map = p[0], PartName = p[1], Entity = Long(p[2]), Boss = boss,
            });
            erSpots++;
        }
        return $"enemy tables: {placedSpots} DS3 spots in ported maps ({unknownSpots} not in FogMod's list), {erSpots} Elden Ring spots, " +
               $"{world.Characters.Count(c => c.Game == SourceGame.ER)} Elden Ring and {world.Characters.Count(c => c.Game == SourceGame.DS3)} ported DS3 characters";
    }

    private static IEnumerable<string[]> Rows(IEnumerable<string>? lines, int columns) =>
        (lines ?? []).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).Where(p => p.Length >= columns);

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static long Long(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
