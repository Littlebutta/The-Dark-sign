using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Places the DS3 map's enemies and bosses in its Elden Ring slot: every DS3 enemy part becomes an Elden Ring enemy
/// part at the same spot, on the same collision, with its DS3 entity ID and entity groups moved to the slot, so the
/// converted event scripts (boss fights, ambushes, death flags, drops) find it.
///
/// Which character stands there, by method (launcher Fallbacks window, "map-enemies"):
/// <list type="bullet">
/// <item>A (default): the ported DS3 character when the enemy porter has ported it (darksign_chr_map.tsv), otherwise
/// an Elden Ring stand-in as in B.</item>
/// <item>B: Elden Ring stand-ins only.</item>
/// <item>C: no enemies.</item>
/// </list>
///
/// Stand-ins are real Elden Ring placements (model, NpcParam, ThinkParam and the part itself, copied from a vanilla
/// legacy dungeon), picked by what the DS3 enemy is: its NpcParam name ("Lothric Knight") is matched by kind
/// (knight, soldier, dog, skeleton, dragon...) to an Elden Ring enemy of the same kind, earliest dungeon first so the
/// first DS3 areas get early-game enemies. DS3 bosses (enemies whose boss health bar the map's events show) get
/// Elden Ring bosses. NPCs (talk scripts) are left out until their dialogue is ported.
/// </summary>
public static class EnemyPort
{
    public enum Method { Auto, StandIn, None }

    public static Method ParseMethod(string? text) => text?.ToLowerInvariant() switch
    {
        "standin" or "stand-in" or "b" => Method.StandIn,
        "none" or "off" or "c" => Method.None,
        _ => Method.Auto,
    };

    /// <summary>An Elden Ring character that can stand somewhere: a vanilla placement, or a ported DS3 character.</summary>
    public sealed record ErEnemy(string Model, int NpcParam, int ThinkParam, string Name, bool Boss, string Source,
        MSBE.Part.EnemyBase? Part = null, MSBE.Model.Enemy? ModelEntry = null)
    {
        public string Kind { get; } = KindOf(Name);
    }

    /// <summary>One DS3 enemy as the port sees it.</summary>
    public sealed record Ds3Enemy(string Model, int NpcParam, string Name, bool Boss, bool Npc);

    /// <summary>The kind of an enemy by its name (shared with the randomizer, which mixes enemies by the same kinds).</summary>
    public static string KindOf(string name) => DarkSign.Randomizer.Logic.EnemyKinds.KindOf(name);

    /// <summary>
    /// Picks who stands in for a DS3 enemy: the ported character if there is one (method A), else the first Elden
    /// Ring placement of the same kind and boss-ness, else the most common one of the same boss-ness.
    /// </summary>
    public sealed class Chooser
    {
        private readonly IReadOnlyList<ErEnemy> _catalog;
        private readonly ChrMap? _ported;
        private readonly Method _method;
        private readonly ErEnemy? _commonEnemy, _commonBoss;
        private readonly Dictionary<string, int> _bossTurn = new();

        public Chooser(IReadOnlyList<ErEnemy> catalog, ChrMap? ported, Method method)
        {
            _catalog = catalog;
            _ported = ported;
            _method = method;
            _commonEnemy = catalog.Where(e => !e.Boss).GroupBy(e => e.Model).OrderByDescending(g => g.Count()).Select(g => g.First()).FirstOrDefault();
            _commonBoss = catalog.FirstOrDefault(e => e.Boss);
        }

        public ErEnemy? Choose(Ds3Enemy ds3)
        {
            if (_method == Method.None || ds3.Npc)
                return null;
            if (_method == Method.Auto && _ported?.Find(ds3) is { } ported)
                return ported;
            var kind = KindOf(ds3.Name);
            var sameKind = _catalog.Where(e => e.Boss == ds3.Boss && kind.Length > 0 && e.Kind == kind).ToList();
            if (ds3.Boss)
            {
                // Spread bosses over the Elden Ring ones of their kind, so every DS3 boss isn't the same one.
                var pool = sameKind.Count > 0 ? sameKind : _catalog.Where(e => e.Boss).ToList();
                if (pool.Count == 0)
                    return _commonEnemy;
                var key = sameKind.Count > 0 ? kind : "";
                var turn = _bossTurn.GetValueOrDefault(key);
                _bossTurn[key] = turn + 1;
                return pool.DistinctBy(e => e.NpcParam).ElementAt(turn % pool.DistinctBy(e => e.NpcParam).Count());
            }
            return sameKind.FirstOrDefault() ?? _commonEnemy ?? _commonBoss;
        }
    }

    /// <summary>
    /// darksign_chr_map.tsv, written by the enemy porter: which DS3 characters it ported and as what.
    /// Lines: "npc &lt;DS3 NpcParam&gt; &lt;ER model&gt; &lt;ER NpcParam&gt; &lt;ER ThinkParam&gt; [name]" for one DS3 NpcParam row, and
    /// "chr &lt;DS3 model&gt; &lt;ER model&gt; &lt;ER NpcParam&gt; &lt;ER ThinkParam&gt; [name]" for any row of a DS3 model, tab-separated.
    /// </summary>
    public sealed class ChrMap
    {
        public const string FileName = "darksign_chr_map.tsv";
        private readonly Dictionary<int, ErEnemy> _byNpc = new();
        private readonly Dictionary<string, ErEnemy> _byModel = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _byNpc.Count + _byModel.Count;

        public static ChrMap Parse(IEnumerable<string> lines)
        {
            var map = new ChrMap();
            foreach (var line in lines)
            {
                var p = line.Split('\t');
                if (line.StartsWith('#') || p.Length < 5 || !int.TryParse(p[3], out var npc) || !int.TryParse(p[4], out var think))
                    continue;
                var name = p.Length > 5 ? p[5] : p[1];
                var enemy = new ErEnemy(p[2], npc, think, name, false, "ported DS3 character");
                if (p[0] == "npc" && int.TryParse(p[1], out var ds3Npc))
                    map._byNpc[ds3Npc] = enemy;
                else if (p[0] == "chr")
                    map._byModel[p[1]] = enemy;
            }
            return map;
        }

        public ErEnemy? Find(Ds3Enemy ds3) =>
            _byNpc.TryGetValue(ds3.NpcParam, out var e) ? e with { Boss = ds3.Boss }
            : _byModel.TryGetValue(ds3.Model, out var m) ? m with { Boss = ds3.Boss } : null;
    }

    public sealed record Result(int Placed, int Ported, int StandIns, int Npcs, int Skipped, IReadOnlyList<string> Notes);

    /// <summary>
    /// Adds the DS3 map's enemies to the converted layout. <paramref name="describe"/> says what each DS3 enemy is
    /// (name from its NpcParam, boss or not, NPC or not).
    /// </summary>
    public static Result Place(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, MsbPort.Options options, Chooser chooser,
        Func<MSB3.Part.Enemy, Ds3Enemy> describe, MSBE.Part.Enemy? fallbackTemplate = null, List<string>? table = null,
        Dictionary<string, string>? partNames = null)
    {
        int placed = 0, ported = 0, standIns = 0, npcs = 0, skipped = 0;
        var notes = new List<string>();
        var collisions = msb.Parts.Collisions.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = msb.Parts.GetEntries().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextInstance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var standInUse = new Dictionary<string, int>();

        foreach (var src in ds3.Parts.Enemies)
        {
            var what = describe(src);
            if (what.Npc)
            {
                npcs++;
                continue;
            }
            var who = chooser.Choose(what);
            if (who is null)
            {
                skipped++;
                continue;
            }
            var template = who.Part as MSBE.Part.Enemy ?? fallbackTemplate ?? new MSBE.Part.Enemy();
            var part = (MSBE.Part.Enemy)template.DeepCopy();
            MsbPort.CopyCommon(src, part, ds3Id, erId);
            MsbPort.ApplyGroups(src, part.Unk1, options);
            // Part names are model_instance, unique in the map.
            var instance = nextInstance.GetValueOrDefault(who.Model, 9000);
            while (names.Contains($"{who.Model}_{instance:D4}"))
                instance++;
            nextInstance[who.Model] = instance + 1;
            part.Name = $"{who.Model}_{instance:D4}";
            part.InstanceID = instance;
            names.Add(part.Name);
            partNames?.TryAdd(src.Name, part.Name);
            part.ModelName = who.Model;
            part.NPCParamID = who.NpcParam;
            part.ThinkParamID = who.ThinkParam;
            part.TalkID = 0;
            part.PlatoonID = src.PlatoonID;
            part.CharaInitID = -1;
            part.WalkRouteName = ""; // DS3 patrol routes need the map's regions, which aren't ported yet
            part.BackupEventAnimID = -1;
            part.CollisionPartName = collisions.Contains(src.CollisionName ?? "") ? src.CollisionName : "";
            // Load and activation settings come from this map, not the dungeon the stand-in was copied from.
            if (msb.Parts.MapPieces.FirstOrDefault()?.TileLoad is { } tile)
                part.TileLoad = tile.DeepCopy();
            part.ChrActivateCondParamID = 0; // some vanilla enemies only appear at night or after an event
            msb.Parts.Enemies.Add(part);
            MsbPort.AddModel(msb.Models.Enemies, who.Model, who.ModelEntry, erId);
            placed++;
            // One row of darksign_enemies.tsv (Randomizer EnemyTables.PlacedFile), so the randomizer knows this spot.
            table?.Add(string.Join('\t', ds3Id, src.Name, src.EntityID, erId, part.Name, part.EntityID, src.ModelName, src.NPCParamID,
                Clean(what.Name), what.Boss ? 1 : 0, who.Source == "ported DS3 character" ? "ported" : "stand-in", who.Model, who.NpcParam, who.ThinkParam));
            if (who.Source == "ported DS3 character")
                ported++;
            else
            {
                standIns++;
                var key = $"{what.Name} -> {who.Name}";
                standInUse[key] = standInUse.GetValueOrDefault(key) + 1;
            }
            if (what.Boss)
                notes.Add($"boss {what.Name} (entity {part.EntityID}) -> {who.Name} ({who.Model}, {who.Source})");
        }
        if (standInUse.Count > 0)
            notes.Add("stand-ins: " + string.Join("; ", standInUse.OrderByDescending(s => s.Value).Take(15).Select(s => $"{s.Key} x{s.Value}")));
        return new Result(placed, ported, standIns, npcs, skipped, notes);
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>darksign_er_enemies.tsv rows (Randomizer EnemyTables.ErFile): the catalog's Elden Ring placements.</summary>
    public static IEnumerable<string> CatalogRows(IEnumerable<ErEnemy> catalog) =>
        catalog.Where(c => c.Part is not null && c.Source.StartsWith('m')).Select(c => string.Join('\t', c.Source, c.Part!.Name,
            c.Part.EntityID, c.Model, c.NpcParam, c.ThinkParam, Clean(c.Name), c.Boss ? 1 : 0));

    // ---- What the event scripts say about bosses ----

    /// <summary>
    /// The entity IDs a game's scripts show a boss health bar for: given directly, or passed to the event that shows
    /// it. <paramref name="ds3Layout"/>: DS3's InitializeCommonEvent has no slot argument.
    /// </summary>
    public static HashSet<long> BossEntities(IEnumerable<EMEVD> scripts, bool ds3Layout)
    {
        var list = scripts.ToList();
        var bosses = new HashSet<long>();
        var passed = new Dictionary<long, HashSet<long>>(); // event ID -> parameter source bytes holding a boss
        foreach (var e in list.SelectMany(s => s.Events))
            for (int i = 0; i < e.Instructions.Count; i++)
            {
                var ins = e.Instructions[i];
                if (ins.Bank != 2003 || ins.ID is not (11 or 80) || ins.ArgData.Length < 8)
                    continue;
                var param = e.Parameters.FirstOrDefault(p => p.InstructionIndex == i && p.TargetStartByte == 4);
                if (param is null)
                    bosses.Add(BitConverter.ToInt32(ins.ArgData, 4));
                else
                    (passed.TryGetValue(e.ID, out var set) ? set : passed[e.ID] = new()).Add(param.SourceStartByte);
            }
        foreach (var ins in list.SelectMany(s => s.Events).SelectMany(e => e.Instructions).Where(i => i.Bank == 2000 && i.ID is 0 or 6))
        {
            // InitializeEvent: slot, event, args. InitializeCommonEvent: [slot,] event, args.
            int eventAt = ins.ID == 6 && ds3Layout ? 0 : 4;
            if (ins.ArgData.Length < eventAt + 4)
                continue;
            var callee = BitConverter.ToUInt32(ins.ArgData, eventAt);
            if (!passed.TryGetValue(callee, out var sources))
                continue;
            foreach (var source in sources)
            {
                var at = eventAt + 4 + (int)source;
                if (at + 4 <= ins.ArgData.Length)
                    bosses.Add(BitConverter.ToInt32(ins.ArgData, at));
            }
        }
        bosses.Remove(0);
        return bosses;
    }

    // ---- The Elden Ring catalog ----

    // Legacy dungeons in rough order of progression: stand-ins come from the earliest one that has the kind.
    public static readonly string[] CatalogMaps =
    [
        "m18_00_00_00", "m10_00_00_00", "m10_01_00_00", "m14_00_00_00", "m16_00_00_00", "m11_00_00_00",
        "m11_05_00_00", "m35_00_00_00", "m13_00_00_00", "m15_00_00_00", "m12_01_00_00", "m12_02_00_00",
    ];

    /// <summary>Every distinct enemy (model, NpcParam, ThinkParam) placed in Elden Ring's legacy dungeons, with its name.</summary>
    public static List<ErEnemy> BuildCatalog(GameArchives er, PARAM? npcParam, List<string> notes)
    {
        var catalog = new List<ErEnemy>();
        var seen = new HashSet<(string, int, int)>();
        int bystanders = 0;
        var names = npcParam?.Rows.ToDictionary(r => r.ID, r => r.Name ?? "") ?? new();
        foreach (var name in CatalogMaps)
        {
            var id = MapId.Parse(name);
            var msbPath = MapPorter.MsbPath(id);
            if (!er.Contains(msbPath))
                continue;
            MSBE msb;
            try
            {
                msb = MSBE.Read(er.Read(msbPath));
            }
            catch (Exception ex)
            {
                notes.Add($"catalog: {name} doesn't read ({ex.Message})");
                continue;
            }
            var scripts = new List<EMEVD>();
            var eventPath = MapEvents.ScriptPath(id);
            if (er.Contains(eventPath))
                scripts.Add(EMEVD.Read(er.Read(eventPath)));
            if (er.Contains(MapEvents.CommonFunc))
                scripts.Add(EMEVD.Read(er.Read(MapEvents.CommonFunc)));
            var bosses = BossEntities(scripts, ds3Layout: false);
            foreach (var part in msb.Parts.Enemies)
            {
                // Humans (c0000) are NPCs or invaders built from equipment; talkers need their dialogue.
                if (part.ModelName.StartsWith("c0000", StringComparison.OrdinalIgnoreCase) || part.TalkID > 0 || part.NPCParamID <= 0)
                    continue;
                if (!seen.Add((part.ModelName, part.NPCParamID, part.ThinkParamID)))
                    continue;
                // Wildlife and helper characters (deer, eagles, talk dummies, bonfires) are nobody's stand-in.
                if (DarkSign.Randomizer.Logic.EnemyKinds.IsBystander(names.GetValueOrDefault(part.NPCParamID, "")))
                {
                    bystanders++;
                    continue;
                }
                var model = msb.Models.Enemies.FirstOrDefault(m => m.Name.Equals(part.ModelName, StringComparison.OrdinalIgnoreCase));
                catalog.Add(new ErEnemy(part.ModelName, part.NPCParamID, part.ThinkParamID,
                    names.GetValueOrDefault(part.NPCParamID, part.ModelName), bosses.Contains(part.EntityID), name, part, model));
            }
        }
        if (bystanders > 0)
            notes.Add($"catalog: {bystanders} wildlife and helper placements left out (deer, eagles, dummies, bonfires)");
        return catalog;
    }

    /// <summary>The Elden Ring map enemies are read from first, for a part to copy when no stand-in has one.</summary>
    public static MSBE.Part.Enemy? AnyEnemyPart(IReadOnlyList<ErEnemy> catalog) => catalog.Select(c => c.Part).OfType<MSBE.Part.Enemy>().FirstOrDefault();
}
