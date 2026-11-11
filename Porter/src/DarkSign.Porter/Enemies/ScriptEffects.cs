using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// The SpEffects DS3's map event scripts put on and take off its enemies, by the DS3 NpcParam row of the enemy they
/// name. DS3 keeps part of an enemy's look in its scripts rather than in its NpcParam row (Devin, 1.0.326 playtest):
/// <list type="bullet">
/// <item>Pontiff Sulyvahn's two sword auras are SpEffects 10640 / 10641, which only m37's event 13705886 sets on him
/// (SetSpEffect 2004[8] on entity 3700850). His NpcParam row names neither, so the port gave him no glow.</item>
/// <item>The Jailer's NpcParam row names 11899 (orange eye glow), 11900 (cyan lantern light) and 11902 (blue, dmy 199);
/// m39's events 13905220 / 13905225 take 11899-11903 off (2004[21]) or put 11899 back. Carried over as NpcParam
/// effects, all three stayed on for good and the Jailer glowed blue.</item>
/// </list>
/// Scripts work on single placed enemies (the Jailer swap is on two of m39's Jailers, 3900225 and 3900226), while a
/// ported enemy is one NpcParam row for every placement. So, for a ported row: an effect a script puts on an enemy of
/// that row and no script ever takes off one is added (part of how it looks: Pontiff's auras); an effect the row
/// names is left out only when scripts take it off, and never put it back, on every placed enemy of that row (a
/// state DS3 never shows). Everything else keeps the row's choice. Entities are matched through DS3's map files
/// (enemy parts: EntityID and entity groups -> NPCParamID); an entity that comes from the event's parameters is read
/// from the calls that start the event (one level). DS3 EMEDF (DarkScript3's ds3-common.emedf.json): 2004[8] "Set
/// SpEffect", 2004[21] "Clear SpEffect", 2000[0] "Initialize Event", 2000[6] "Initialize Common Event".
/// Fallback enemy-glow B (DARKSIGN_ENEMY_SCRIPT_EFFECTS=0) keeps the rows as they are.
/// </summary>
public sealed class ScriptEffects
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_SCRIPT_EFFECTS")?.Trim() != "0";

    /// <summary>DS3 EMEDF: 2004[8] "Set SpEffect" (entity, SpEffect), 2004[21] "Clear SpEffect" (entity, SpEffect).</summary>
    public const int Bank = 2004, SetId = 8, ClearId = 21;

    private readonly Dictionary<int, HashSet<int>> _set = [], _cleared = [];
    // Per placed enemy (entity ID): what scripts set and clear on it; and how many enemies each row has placed.
    private readonly Dictionary<int, HashSet<int>> _entitySet = [], _entityCleared = [];
    private readonly Dictionary<int, HashSet<int>> _entitiesOf = [];
    private readonly Dictionary<int, int> _placements = [];

    public int Instructions { get; private set; }
    public int Resolved { get; private set; }
    public int Unresolved { get; private set; }
    public int Maps { get; private set; }

    public IReadOnlySet<int> Set(int npcParam) => _set.TryGetValue(npcParam, out var s) ? s : new HashSet<int>();
    public IReadOnlySet<int> Cleared(int npcParam) => _cleared.TryGetValue(npcParam, out var s) ? s : new HashSet<int>();

    /// <summary>The DS3 effects a ported row gets: its own, less those scripts only take off, plus those scripts only put on.</summary>
    public (List<int> Effects, List<int> Dropped, List<int> Added) Apply(int npcParam, IEnumerable<int> rowEffects)
    {
        var set = Set(npcParam);
        var cleared = Cleared(npcParam);
        var own = rowEffects.Where(id => id > 0).Distinct().ToList();
        // Off for good on every placed enemy of the row (a row placed nowhere keeps its effects).
        var entities = _entitiesOf.GetValueOrDefault(npcParam) ?? [];
        int placed = _placements.GetValueOrDefault(npcParam);
        bool OffEverywhere(int id) => placed > 0 && entities.Count == placed
            && entities.All(e => _entityCleared.GetValueOrDefault(e)?.Contains(id) == true && _entitySet.GetValueOrDefault(e)?.Contains(id) != true);
        var dropped = own.Where(id => cleared.Contains(id) && !set.Contains(id) && OffEverywhere(id)).ToList();
        var added = set.Where(id => !cleared.Contains(id) && !own.Contains(id)).Order().ToList();
        return ([.. own.Except(dropped), .. added], dropped, added);
    }

    /// <summary>Reads every DS3 map file and event script. Null (with a note) when it can't.</summary>
    public static ScriptEffects? Read(GameArchives ds3, List<string> notes)
    {
        if (!Enabled)
        {
            Console.WriteLine("enemies: DS3 event script SpEffects are not applied (DARKSIGN_ENEMY_SCRIPT_EFFECTS=0)");
            return null;
        }
        try
        {
            var entities = new Dictionary<int, HashSet<int>>();
            var placements = new Dictionary<int, int>();
            var own = new Dictionary<int, int>();
            int maps = 0;
            foreach (var path in ds3.Entries.Select(e => e.Path).OfType<string>()
                         .Where(p => p.StartsWith("/map/mapstudio/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".msb.dcx", StringComparison.OrdinalIgnoreCase)))
            {
                MSB3 msb;
                try { msb = MSB3.Read(ds3.Read(path)); }
                catch (Exception e) when (e is not OutOfMemoryException) { continue; }
                maps++;
                foreach (var enemy in msb.Parts.Enemies)
                {
                    placements[enemy.NPCParamID] = placements.GetValueOrDefault(enemy.NPCParamID) + 1;
                    if (enemy.EntityID > 0)
                        own[enemy.EntityID] = enemy.NPCParamID;
                    foreach (var entity in enemy.EntityGroups.Prepend(enemy.EntityID).Where(id => id > 0))
                        Add(entities, entity, enemy.NPCParamID);
                }
            }
            var scripts = new List<EMEVD>();
            foreach (var path in ds3.Entries.Select(e => e.Path).OfType<string>()
                         .Where(p => p.StartsWith("/event/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".emevd.dcx", StringComparison.OrdinalIgnoreCase)))
                try { scripts.Add(EMEVD.Read(ds3.Read(path))); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            var result = From(scripts, entities, placements, own);
            result.Maps = maps;
            Console.WriteLine($"enemies: DS3 event scripts: {result.Instructions} SpEffect set/clear instructions in {scripts.Count} scripts, " +
                              $"{result.Resolved} on enemies of {maps} maps, {result.Unresolved} not matched to an enemy");
            return result;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            notes.Add($"DS3 event script SpEffects: not read ({ChrPort.Describe(e)}); NpcParam rows keep DS3's effects as they are");
            return null;
        }
    }

    private static void Add(Dictionary<int, HashSet<int>> map, int key, int value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = [];
        set.Add(value);
    }

    /// <summary>
    /// The script effects of these scripts, with entity ID -> DS3 NpcParam rows (EntityID and entity groups), how many
    /// enemies each row has placed, and each placed enemy's own entity ID -> row.
    /// </summary>
    public static ScriptEffects From(IEnumerable<EMEVD> scripts, IReadOnlyDictionary<int, HashSet<int>> entities,
        IReadOnlyDictionary<int, int>? placements = null, IReadOnlyDictionary<int, int>? ownEntities = null)
    {
        var result = new ScriptEffects();
        foreach (var (npc, count) in placements ?? new Dictionary<int, int>())
            result._placements[npc] = count;
        foreach (var (entity, npc) in ownEntities ?? new Dictionary<int, int>())
            Add(result._entitiesOf, npc, entity);
        var all = scripts.ToList();
        // The calls that start each event, with their arguments: 2000[0] (slot, event, args...), 2000[6] (event, args...).
        var starts = new Dictionary<long, List<byte[]>>();
        foreach (var ins in all.SelectMany(s => s.Events).SelectMany(e => e.Instructions).Where(i => i.Bank == 2000))
        {
            if (ins.ID == 0 && ins.ArgData.Length >= 8)
                AddStart(starts, BitConverter.ToUInt32(ins.ArgData, 4), ins.ArgData[8..]);
            else if (ins.ID == 6 && ins.ArgData.Length >= 4)
                AddStart(starts, BitConverter.ToUInt32(ins.ArgData, 0), ins.ArgData[4..]);
        }
        foreach (var e in all.SelectMany(s => s.Events))
            for (int i = 0; i < e.Instructions.Count; i++)
            {
                var ins = e.Instructions[i];
                if (ins.Bank != Bank || ins.ID is not (SetId or ClearId) || ins.ArgData.Length < 8)
                    continue;
                result.Instructions++;
                var parameters = e.Parameters.Where(p => p.InstructionIndex == i).ToList();
                List<byte[]> calls = parameters.Count == 0 ? [ins.ArgData] : [];
                if (parameters.Count > 0 && starts.TryGetValue(e.ID, out var args))
                    foreach (var a in args)
                    {
                        var data = (byte[])ins.ArgData.Clone();
                        bool ok = true;
                        foreach (var p in parameters)
                        {
                            if (p.SourceStartByte + p.ByteCount > a.Length || p.TargetStartByte + p.ByteCount > data.Length)
                            {
                                ok = false;
                                break;
                            }
                            Array.Copy(a, p.SourceStartByte, data, p.TargetStartByte, p.ByteCount);
                        }
                        if (ok)
                            calls.Add(data);
                    }
                bool any = false;
                foreach (var data in calls)
                {
                    int entity = BitConverter.ToInt32(data, 0), spEffect = BitConverter.ToInt32(data, 4);
                    if (spEffect <= 0 || !entities.TryGetValue(entity, out var npcs))
                        continue;
                    foreach (var npc in npcs)
                        Add(ins.ID == SetId ? result._set : result._cleared, npc, spEffect);
                    Add(ins.ID == SetId ? result._entitySet : result._entityCleared, entity, spEffect);
                    any = true;
                }
                if (any) result.Resolved++;
                else result.Unresolved++;
            }
        return result;
    }

    private static void AddStart(Dictionary<long, List<byte[]>> starts, long eventId, byte[] args)
    {
        if (!starts.TryGetValue(eventId, out var list))
            starts[eventId] = list = [];
        list.Add(args);
    }
}
