using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Boss kits: a DS3 boss's own scripted mechanics, carried with the boss so they work wherever the boss stands.
///
/// DS3 keeps part of a boss fight in the boss's map script rather than in the character: Pontiff Sulyvahn's AI plays
/// his phase-change move at half health and sends event message 10; m37's event 13705880 then warps his clone in,
/// gives him SpEffect 10636 and wakes the clone, and only then does his AI go on. A Pontiff anywhere but m37 (the F1
/// spawner; later the boss randomizer) runs no map script, so he froze for good at half health (Devin, 1.0.383).
/// The Gravetender's wolf, Oceiros' and Gundyr's phase effects and every other per-boss event work the same way.
///
/// For every boss fight in DS3's map scripts (<see cref="BossArenas.Find"/>: the 20005800 calls; the maps listed
/// in <see cref="Ds3Maps"/> that the game files hold, ported or not) the kit is the fight's own events
/// (<see cref="BossArenas.FightEvents"/>) that name only the fight's characters, the player and plain values: no
/// regions, objects, effects, sounds, cutscenes or warps (those belong to the arena and stay with the map), nothing
/// that starts other events, and nothing that ends the fight (the defeat event stays the map's). Its characters
/// are the boss's members and the other enemies those events name (Pontiff's clone 3700851).
///
/// The kit events go into common.emevd, so they run in every map, with the characters at fixed entity IDs
/// (<see cref="KitEntity"/>: 1,190,000,000 + the DS3 entity ID), the fight's map flags at flags of the run's block
/// that reset on load (<see cref="FirstFlag"/>), and the entry / first-encounter flags turned on once the boss is
/// loaded (the fight has begun wherever the boss is). Each event first waits until every kit character it names is
/// loaded, so nothing runs before the boss exists and a kit never touches a boss placed in its home map (that one has
/// the map's own IDs and the map's own events). darksign_boss_kits.tsv tells the F1 spawner which entity IDs a boss
/// and its companions get and which companions to spawn with it (TestMenu spawn.c).
/// </summary>
public static class BossKits
{
    public const string FileName = "darksign_boss_kits.tsv";

    /// <summary>Kit characters' entity IDs: this + the DS3 entity ID (DS3's IDs are unique across its maps).</summary>
    public const uint EntityBase = 1_190_000_000;
    public static uint KitEntity(int ds3Entity) => EntityBase + (uint)ds3Entity;

    /// <summary>Kit flags: 2500-2999 of the run's block, the part that resets when a map loads (RunEvents).</summary>
    public const uint FirstFlag = RunEvents.FlagBlock + 2500, FlagCount = 500;

    /// <summary>Kit event IDs (each kit's events, then its start event): 1600-1999 of the run's block, beside the run's other events.</summary>
    public const uint FirstEvent = RunEvents.FlagBlock + 1600, EventCount = 400;
    public static bool IsKitEvent(long id) => id >= FirstEvent && id < FirstEvent + EventCount;

    /// <summary>DS3's maps with boss fights (base game and both DLCs).</summary>
    public static readonly MapId[] Ds3Maps =
    [
        .. new[]
        {
            "m30_00_00_00", "m30_01_00_00", "m31_00_00_00", "m32_00_00_00", "m33_00_00_00", "m34_01_00_00", "m35_00_00_00",
            "m37_00_00_00", "m38_00_00_00", "m39_00_00_00", "m40_00_00_00", "m41_00_00_00", "m45_00_00_00", "m46_00_00_00",
            "m47_00_00_00", "m50_00_00_00", "m51_00_00_00", "m51_01_00_00",
        }.Select(MapId.Parse),
    ];

    /// <summary>A character of a kit: its DS3 placement.</summary>
    public sealed record Character(int Ds3Entity, string Model, int NpcParam, int ThinkParam);

    /// <summary>One boss's kit, in DS3's IDs.</summary>
    public sealed record Kit(MapId Map, string Name, int Defeated, int Entry, int FirstEncounter, Character Boss,
        IReadOnlyList<Character> Companions, IReadOnlyList<EMEVD.Event> Events)
    {
        public IEnumerable<Character> Characters => Companions.Prepend(Boss);
    }

    // Argument kinds that tie an event to its arena.
    private static readonly HashSet<EventPort.Kind> ArenaKinds =
        [EventPort.Kind.Area, EventPort.Kind.Block, EventPort.Kind.PackedMap, EventPort.Kind.Cutscene, EventPort.Kind.WarpPoint];

    /// <summary>The kits of one DS3 map's boss fights.</summary>
    public static List<Kit> Find(MSB3 msb, EMEVD script, MapId ds3, Emedf ds3Defs, Func<MSB3.Part.Enemy, string?>? name = null)
    {
        // Kit characters: the map's enemies with entity IDs, but not people (c0000: summons, phantoms and NPC bosses
        // like the Champion's Gravetender are built from equipment, so neither the F1 spawner nor a randomizer places
        // them; an event that waits on one would never run).
        var enemies = msb.Parts.Enemies.Where(e => e.EntityID > 0 && !IsPerson(e.ModelName)).GroupBy(e => e.EntityID)
            .ToDictionary(g => g.Key, g => g.First());
        var byId = script.Events.GroupBy(e => e.ID).ToDictionary(g => g.Key, g => g.First());
        var bars = BossArenas.HealthBars(script).ToList();
        var kits = new List<Kit>();
        foreach (var f in BossArenas.Find(msb, script, ds3, name))
        {
            var members = BossArenas.Members(msb, f.Boss).Where(enemies.ContainsKey).ToHashSet();
            if (members.Count == 0)
                continue;
            // The boss is the member with the first health bar (Friede's first stage, Pontiff), else the lowest.
            var main = bars.Where(b => members.Contains(b.Entity)).OrderBy(b => b.Slot).Select(b => b.Entity)
                .DefaultIfEmpty(members.Min()).First();
            var chars = new HashSet<int> { main };
            var chosen = new List<EMEVD.Event>();
            foreach (var id in f.Events)
            {
                if (!byId.TryGetValue(id, out var e) || e.Parameters.Count > 0)
                    continue;
                if (Portable(e, ds3Defs, ds3, (int)f.Defeated, enemies) is not { } p || !p.Characters.Overlaps(members))
                    continue;
                chars.UnionWith(p.Characters);
                chosen.Add(Stripped(e, p.Strip));
            }
            if (chosen.Count == 0)
                continue;
            Character Of(int entity) => new(entity, enemies[entity].ModelName, enemies[entity].NPCParamID, enemies[entity].ThinkParamID);
            kits.Add(new Kit(ds3, f.Name, (int)f.Defeated, (int)f.Entry, (int)f.FirstEncounter, Of(main),
                chars.Where(c => c != main).Order().Select(Of).ToList(), chosen));
        }
        return kits;
    }

    public static bool IsPerson(string model) => model.StartsWith("c0000", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a portable event names: the characters it waits on or acts on, and the lines to blank.</summary>
    public sealed record Portability(HashSet<int> Characters, HashSet<int> Strip);

    /// <summary>
    /// Whether an event can travel with its boss, or null when it can't: it starts events, ends the fight, or
    /// waits on something that stays with the arena (a region, object, other map's entity, person, cutscene or
    /// warp). Commands that act on the arena (map effects and sounds, cutscenes, warps to regions, the arena's
    /// cameras, item lots, a person) are blanked instead, so Friede's phase change still swaps her for her second
    /// stage without the cutscene that hides it in Ariandel's chapel.
    /// </summary>
    public static Portability? Portable(EMEVD.Event e, Emedf defs, MapId ds3, int defeated, IReadOnlyDictionary<int, MSB3.Part.Enemy> enemies)
    {
        var refs = new HashSet<int>();
        var strip = new HashSet<int>();
        var acts = false;
        for (var i = 0; i < e.Instructions.Count; i++)
        {
            var ins = e.Instructions[i];
            var command = ins.Bank >= 2000;
            if (ins.Bank == 2000 && ins.ID is 0 or 6)
                return null;
            if (ins.Bank == 2003 && ins.ID is 12 or 74)
                return null; // Handle Boss Defeat
            if (ins.Bank == 2003 && ins.ID is 2 or 62 && ins.ArgData.Length >= 5 && BitConverter.ToInt32(ins.ArgData, 0) == defeated && ins.ArgData[4] == 1)
                return null;
            if (command && (ins.Bank is 2002 or 2006 or 2008 or 2010 || ins.Bank == 2003 && ins.ID == 4))
            {
                strip.Add(i); // cutscenes, map effects, cameras, map sounds, item lots
                continue;
            }
            if (defs.Get(ins.Bank, ins.ID) is not { } def)
            {
                if (!command)
                    return null;
                strip.Add(i);
                continue;
            }
            var named = new List<int>();
            var arena = false;
            foreach (var arg in def.Args)
            {
                var kind = EventPort.KindOf(arg.Name);
                var v = Emedf.Read(ins.ArgData, arg);
                if (ArenaKinds.Contains(kind) && v > 0)
                    arena = true;
                else if (kind == EventPort.Kind.Entity && v >= 1_000_000)
                {
                    if (ds3.OwnsDs3Id(v) && enemies.ContainsKey((int)v))
                        named.Add((int)v);
                    else
                        arena = true; // a region, object, person or another map's entity
                }
                // Entities under 1,000,000 are the player (10000) and other global characters.
            }
            if (arena)
            {
                if (!command)
                    return null;
                strip.Add(i);
                continue;
            }
            refs.UnionWith(named);
            if (command)
                acts = true;
        }
        return acts ? new Portability(refs, strip) : null; // nothing left to do once the arena's lines are gone
    }

    // The event with its arena lines blanked (a zero wait keeps every skip count right).
    private static EMEVD.Event Stripped(EMEVD.Event e, HashSet<int> strip)
    {
        if (strip.Count == 0)
            return e;
        var copy = new EMEVD.Event(e.ID, e.RestBehavior);
        for (var i = 0; i < e.Instructions.Count; i++)
            copy.Instructions.Add(strip.Contains(i) ? RunEvents.Make(1001, 0, 0f) : e.Instructions[i]);
        return copy;
    }

    /// <summary>The kits of every DS3 map the game files hold, and a note per map.</summary>
    public static List<Kit> Plan(GameArchives ds3, Emedf ds3Defs, Func<MSB3.Part.Enemy, string?>? name, List<string> notes)
    {
        var kits = new List<Kit>();
        foreach (var map in Ds3Maps)
        {
            if (!ds3.Contains(MapEvents.ScriptPath(map)) || !ds3.Contains(MapPorter.MsbPath(map)))
                continue;
            try
            {
                var found = Find(MSB3.Read(ds3.Read(MapPorter.MsbPath(map))), EMEVD.Read(ds3.Read(MapEvents.ScriptPath(map))), map, ds3Defs, name);
                kits.AddRange(found);
                if (found.Count > 0)
                    notes.Add($"{map}: " + string.Join("; ", found.Select(k => $"{k.Name} ({k.Events.Count} events, "
                        + $"{k.Companions.Count} companions{(k.Companions.Count > 0 ? ": " + string.Join(" ", k.Companions.Select(c => c.Ds3Entity)) : "")})")));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"{map}: not read ({ex.Message})");
            }
        }
        return kits;
    }

    /// <summary>The kits' events in Elden Ring's form, added to common.emevd and started from its constructor.</summary>
    /// <param name="spEffects">The ported row of a DS3 SpEffect (as <see cref="EventPort.SpEffects"/>).</param>
    /// <param name="buttons">Where DS3 action button rows go (Iudex Gundyr's sword, 8900); null leaves them.</param>
    public static string AddTo(EMEVD common, IReadOnlyList<Kit> kits, Emedf ds3Defs, Emedf erDefs, FlagMap flags, Func<long, long?>? spEffects,
        ActionButtons? buttons = null)
    {
        common.Events.RemoveAll(e => IsKitEvent(e.ID));
        var constructor = common.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
        {
            constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
            common.Events.Insert(0, constructor);
        }
        constructor.Instructions.RemoveAll(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 && IsKitEvent(BitConverter.ToUInt32(i.ArgData, 4)));
        uint nextFlag = FirstFlag, nextEvent = FirstEvent;
        int added = 0, events = 0, dropped = 0;
        var left = new List<string>();
        foreach (var kit in kits)
        {
            var chars = kit.Characters.Select(c => c.Ds3Entity).ToHashSet();
            // The fight's own map flags (its events' flags, entry, first encounter, defeated) become kit flags.
            var kitFlags = new Dictionary<long, uint>();
            var localFlags = kit.Events.SelectMany(e => e.Instructions).SelectMany(ins => FlagArgs(ins, ds3Defs))
                .Concat([kit.Defeated, kit.Entry, kit.FirstEncounter]).Where(v => v > 0 && kit.Map.OwnsDs3Id(v)).Distinct().ToList();
            if (nextFlag + localFlags.Count + 1 > FirstFlag + FlagCount || nextEvent + kit.Events.Count + 1 > FirstEvent + EventCount)
            {
                left.Add($"{kit.Name} (no room)");
                continue;
            }
            foreach (var flag in localFlags)
                kitFlags[flag] = nextFlag++;
            var ready = nextFlag++;
            var kitEvents = kit.Events.ToDictionary(e => e.ID, _ => nextEvent++);
            var port = new EventPort(ds3Defs, erDefs, [], flags)
            {
                SpEffects = spEffects,
                Buttons = buttons,
                Override = (v, kind) => kind switch
                {
                    EventPort.Kind.Entity when chars.Contains((int)Math.Min(v, int.MaxValue)) => KitEntity((int)v),
                    EventPort.Kind.Flag or EventPort.Kind.FlagRangeStart or EventPort.Kind.FlagRangeEnd when kitFlags.TryGetValue(v, out var f) => f,
                    EventPort.Kind.Event when kitEvents.TryGetValue(v, out var ev) => ev,
                    _ => null,
                },
            };
            var source = new EMEVD(EMEVD.Game.DarkSouls3);
            source.Events.AddRange(kit.Events);
            var converted = port.Convert(source, common.Format, id => kitEvents.TryGetValue(id, out var ev) ? ev : id);
            dropped += port.Stats.EventsDropped;
            foreach (var e in converted.Events)
            {
                // Nothing runs before the kit is ready: the boss and its companions loaded, the late ones hidden.
                e.Instructions.Insert(0, IfFlagOn(ready));
                common.Events.Add(e);
                constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, (uint)e.ID, 0u));
                events++;
            }
            // The kit's start: its flags off; once every character is loaded, the ones a kit event brings in later
            // (Pontiff's clone, Friede's second stage) hidden as DS3's map had them, and the fight counts as begun.
            var start = new EMEVD.Event(nextEvent++, EMEVD.Event.RestBehaviorType.Default);
            foreach (var f in kitFlags.Values.Append(ready))
                start.Instructions.Add(SetFlag(f, 0));
            foreach (var c in kit.Characters)
                start.Instructions.Add(Loaded(KitEntity(c.Ds3Entity)));
            foreach (var c in LateCharacters(kit))
                start.Instructions.Add(RunEvents.Make(2004, 5, KitEntity(c), (byte)0));
            foreach (var f in new[] { kit.Entry, kit.FirstEncounter })
                if (f > 0 && kitFlags.TryGetValue(f, out var on))
                    start.Instructions.Add(SetFlag(on, 1));
            start.Instructions.Add(SetFlag(ready, 1));
            common.Events.Add(start);
            constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, (uint)start.ID, 0u));
            added++;
        }
        return $"boss kits: {added} of {kits.Count} DS3 bosses carry their own scripts ({events} events"
               + (dropped > 0 ? $", {dropped} didn't convert" : "") + ")" + (left.Count > 0 ? "; left out: " + string.Join(", ", left) : "");
    }

    // IF Character Backread Status (4[07]): group 0, the character, loaded, comparison =, 1 character.
    private static EMEVD.Instruction Loaded(uint entity) => RunEvents.Make(4, 7, (sbyte)0, entity, (byte)1, (sbyte)0, 1f);

    private static EMEVD.Instruction SetFlag(uint flag, byte state) => RunEvents.Make(2003, 66, (byte)0, flag, state);

    // IF Event Flag (3[00]): group 0, ON, type 0, the flag.
    private static EMEVD.Instruction IfFlagOn(uint flag) => RunEvents.Make(3, 0, (sbyte)0, (byte)1, (byte)0, flag);

    /// <summary>
    /// The kit's companions that a kit event enables (Change Character Enable State, on): DS3's map keeps them
    /// disabled until then, so the kit hides them when it starts.
    /// </summary>
    public static IEnumerable<int> LateCharacters(Kit kit)
    {
        var companions = kit.Companions.Select(c => c.Ds3Entity).ToHashSet();
        return kit.Events.SelectMany(e => e.Instructions)
            .Where(i => i.Bank == 2004 && i.ID == 5 && i.ArgData.Length >= 5 && i.ArgData[4] == 1)
            .Select(i => BitConverter.ToInt32(i.ArgData, 0)).Where(companions.Contains).Distinct().Order();
    }

    private static IEnumerable<int> EntityArgs(EMEVD.Instruction ins, Emedf defs) =>
        defs.Get(ins.Bank, ins.ID) is { } def
            ? def.Args.Where(a => EventPort.KindOf(a.Name) == EventPort.Kind.Entity).Select(a => (int)Emedf.Read(ins.ArgData, a))
            : [];

    private static IEnumerable<long> FlagArgs(EMEVD.Instruction ins, Emedf defs) =>
        defs.Get(ins.Bank, ins.ID) is { } def
            ? def.Args.Where(a => EventPort.KindOf(a.Name) is EventPort.Kind.Flag or EventPort.Kind.FlagRangeStart or EventPort.Kind.FlagRangeEnd)
                .Select(a => Emedf.Read(ins.ArgData, a))
            : [];

    /// <summary>
    /// darksign_boss_kits.tsv for the F1 spawner: one line per kit whose boss and companions the enemy port all
    /// ported (a kit's start waits for every one of its characters), "boss model, NpcParam, kit entity, name,
    /// companions" (companions: model:NpcParam:ThinkParam:entity, comma-separated). The spawner gives a spawned boss of
    /// that model its kit entity and spawns the companions with it.
    /// </summary>
    public static List<string> Lines(IEnumerable<Kit> kits, Func<Character, EnemyPort.ErEnemy?> ported)
    {
        var lines = new List<string> { "# boss model\tNpcParam\tkit entity\tname\tcompanions model:NpcParam:ThinkParam:entity (written by DarkSignPorter port-maps; read by the F1 menu)" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kit in kits)
        {
            if (ported(kit.Boss) is not { } boss)
                continue;
            var companions = kit.Companions.Select(c => (c, er: ported(c))).ToList();
            if (companions.Any(x => x.er is null) || !seen.Add(boss.Model))
                continue;
            lines.Add(string.Join('\t', boss.Model, boss.NpcParam, KitEntity(kit.Boss.Ds3Entity), kit.Name.Replace('\t', ' '),
                string.Join(',', companions.Select(x => $"{x.er!.Model}:{x.er.NpcParam}:{x.er.ThinkParam}:{KitEntity(x.c.Ds3Entity)}"))));
        }
        return lines;
    }
}
