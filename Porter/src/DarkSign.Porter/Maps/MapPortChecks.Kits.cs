using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>Offline checks of boss kits: a DS3 boss's own map events, carried into common.emevd under fixed IDs.</summary>
public static partial class MapPortChecks
{
    public static IEnumerable<(string Name, Action Test)> KitChecks()
    {
        yield return ("boss kits: the fight's events that only touch its characters travel, arena lines are blanked, the rest stay", KitFind);
        yield return ("boss kits: kit events wait for the kit, use kit entities/flags/SpEffects, hide late companions, rerun-safe", KitEvents);
        yield return ("action buttons: a DS3 row named by IF Action Button in Area gets an Elden Ring copy, once", FightActionButtons);
        yield return ("warp points: DS3 warp destinations become player parts with their moved entity ID, once", FightWarpPoints);
        yield return ("boss arenas: arrival on a player point, else a DS3 warp point in the area, else the boss's floor + 0.5 m", FightArrival);
        yield return ("boss arenas: a first encounter waiting on a flag only dialogue sets is opened near the talk prompt", FightTalkGate);
        yield return ("boss kits: the F1 spawner's table names the boss, its kit entity and its companions, only when all are ported", KitLines);
    }

    // Both games' forms of what the kit checks use (DarkScript3's names; DS3 and Elden Ring share these layouts).
    private const string KitDefs = """
    { "main_classes": [
      { "index": 0, "instrs": [
        { "name": "IF Condition Group", "index": 0, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Desired Condition Group State", "type": 0 },
          { "name": "Target Condition Group", "type": 3 } ] } ] },
      { "index": 3, "instrs": [
        { "name": "IF Event Flag", "index": 0, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Desired Flag State", "type": 0 },
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 } ] },
        { "name": "IF Action Button in Area", "index": 24, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Action Button Parameter ID", "type": 5 },
          { "name": "Starting Point Area Entity ID", "type": 5 } ] },
        { "name": "IF In/Outside Area", "index": 2, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Desired State", "type": 0 },
          { "name": "Target Entity ID", "type": 2 }, { "name": "Area Entity ID", "type": 2 },
          { "name": "Number of Target Characters", "type": 5 } ] } ] },
      { "index": 4, "instrs": [
        { "name": "IF Character Has SpEffect", "index": 5, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Target Entity ID", "type": 2 },
          { "name": "SpEffect ID", "type": 5 }, { "name": "Should Have", "type": 0 },
          { "name": "Comparison Type", "type": 3 }, { "name": "Number of Target Characters", "type": 6 } ] },
        { "name": "IF Character Has Event Message", "index": 8, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Target Entity ID", "type": 2 },
          { "name": "Event Message ID", "type": 5 }, { "name": "Should Have", "type": 0 },
          { "name": "Comparison Type", "type": 3 }, { "name": "Number of Target Characters", "type": 6 } ] } ] },
      { "index": 1001, "instrs": [
        { "name": "WAIT Fixed Time (Seconds)", "index": 0, "args": [ { "name": "Time (Seconds)", "type": 6 } ] } ] },
      { "index": 1003, "instrs": [
        { "name": "END IF Event Flag", "index": 2, "args": [
          { "name": "Execution End Type", "type": 0 }, { "name": "Desired Flag State", "type": 0 },
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 } ] } ] },
      { "index": 2000, "instrs": [
        { "name": "Initialize Event", "index": 0, "args": [
          { "name": "Event Slot Number", "type": 5 }, { "name": "Event ID", "type": 2 }, { "name": "Additional Arguments", "type": 2 } ] } ] },
      { "index": 2003, "instrs": [
        { "name": "Set Event Flag", "index": 2, "args": [ { "name": "Event Flag ID", "type": 2 }, { "name": "Flag State", "type": 0 } ] },
        { "name": "Warp Player", "index": 14, "args": [
          { "name": "Area ID", "type": 0 }, { "name": "Block ID", "type": 0 }, { "name": "Initial Area ID", "type": 5 } ] } ] },
      { "index": 2004, "instrs": [
        { "name": "Change Character Enable State", "index": 5, "args": [
          { "name": "Character Entity ID", "type": 2 }, { "name": "Disabled/Enabled", "type": 0 } ] },
        { "name": "Set SpEffect", "index": 8, "args": [ { "name": "Entity ID", "type": 2 }, { "name": "SpEffect ID", "type": 5 } ] } ] }
    ] }
    """;

    // A Pontiff-like fight in m40: the boss (group 4005850, first health bar), his clone outside the group that the
    // half-health event enables, a person, an ordinary enemy, and one event of each kind that must stay with the map.
    private static (MSB3 Msb, EMEVD Script) PontiffLike()
    {
        var ds3 = SyntheticDs3();
        var collision = ds3.Parts.Collisions[0].Name;
        foreach (var model in new[] { "c5140", "c1240" })
            ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = model });
        MSB3.Part.Enemy Enemy(string name, int entity, int npc, int think, int group = 0)
        {
            var e = new MSB3.Part.Enemy { Name = name, ModelName = name[..5], EntityID = entity, NPCParamID = npc, ThinkParamID = think, CollisionName = collision };
            e.EntityGroups[0] = group;
            return e;
        }
        ds3.Parts.Enemies.Add(Enemy("c5140_0000", 4000850, 514000, 514000, 4005850));
        ds3.Parts.Enemies.Add(Enemy("c5140_0001", 4000851, 514001, 514001));
        ds3.Parts.Enemies.Add(Enemy("c0000_0170", 4000870, 100000, 0));
        ds3.Parts.Enemies.Add(Enemy("c1240_0000", 4000100, 124000, 124000));
        var area = new MSB3.Region.Event { Name = "boss area", EntityID = 4002850, Position = new Vector3(0, 10, 0), Shape = new MSB.Shape.Box(20, 20, 6) };
        ds3.Regions.Add(area);

        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        EMEVD.Event Event(long id, params EMEVD.Instruction[] lines)
        {
            var e = new EMEVD.Event(id, EMEVD.Event.RestBehaviorType.Restart);
            e.Instructions.AddRange(lines);
            script.Events.Add(e);
            return e;
        }
        Event(14005899, RunEvents.Make(2000, 6, BossArenas.Ds3BossFight, 14000850, 4001850, 4002850, 14005855, 4001850, 4005850, 14000851, 0));
        Event(14005860, RunEvents.Make(2003, 11, (byte)1, 4000850, (short)0, 905140));
        // The half-health switch: waits on the boss's AI message, buffs him, brings the clone in behind a cutscene.
        Event(14005880,
            RunEvents.Make(1003, 2, (byte)0, (byte)1, (byte)0, 14000850),
            RunEvents.Make(4, 5, (sbyte)1, 4000850, 10636, (byte)0, (sbyte)0, 1f),
            RunEvents.Make(4, 8, (sbyte)1, 4000850, 10, (byte)1, (sbyte)0, 1f),
            RunEvents.Make(0, 0, (sbyte)0, (byte)1, (sbyte)1),
            RunEvents.Make(2002, 4, 45000010, 0, 4002812, (byte)40, (byte)0, 10000),
            RunEvents.Make(2004, 8, 4000850, 10636),
            RunEvents.Make(2004, 5, 4000851, (byte)1),
            RunEvents.Make(2003, 2, 14005801, (byte)1));
        Event(14005881, RunEvents.Make(3, 2, (sbyte)0, (byte)1, 10000, 4002850, 1), RunEvents.Make(2004, 8, 4000850, 10637)); // waits on a region
        Event(14005882, RunEvents.Make(4, 8, (sbyte)0, 4000850, 20, (byte)1, (sbyte)0, 1f), RunEvents.Make(2004, 8, 4000870, 10638)); // acts on a person only
        Event(14005883, RunEvents.Make(4, 8, (sbyte)0, 4000850, 30, (byte)1, (sbyte)0, 1f), RunEvents.Make(2000, 0, 0, 14005880u, 0u)); // starts an event
        Event(14005884, RunEvents.Make(4, 8, (sbyte)0, 4000850, 40, (byte)1, (sbyte)0, 1f), RunEvents.Make(2003, 2, 14000850, (byte)1)); // ends the fight
        Event(14005885, RunEvents.Make(4, 8, (sbyte)0, 4000100, 50, (byte)1, (sbyte)0, 1f), RunEvents.Make(2004, 8, 4000100, 10639)); // not the boss
        return (ds3, script);
    }

    private static void KitFind()
    {
        var (msb, script) = PontiffLike();
        var kits = BossKits.Find(msb, script, Ds3Id, Emedf.Parse(KitDefs));
        Expect(kits.Count == 1, $"{kits.Count} kits");
        var k = kits[0];
        Expect(k.Boss.Ds3Entity == 4000850 && k.Boss.Model == "c5140" && k.Boss.NpcParam == 514000, "the boss is the health-bar member");
        Expect(k.Companions.Select(c => c.Ds3Entity).SequenceEqual([4000851]), "the clone is a companion; the person and other enemies aren't: "
            + string.Join(" ", k.Companions.Select(c => c.Ds3Entity)));
        Expect(k.Events.Select(e => e.ID).SequenceEqual([14005880L]), "only the half-health event travels: " + string.Join(" ", k.Events.Select(e => e.ID)));
        Expect(k.Defeated == 14000850 && k.Entry == 14005855 && k.FirstEncounter == 14000851, "the fight's flags");
        var cut = k.Events[0].Instructions[4];
        Expect(cut.Bank == 1001 && cut.ID == 0, "the cutscene line is blanked, keeping the line count");
        Expect(BossKits.LateCharacters(k).SequenceEqual([4000851]), "the clone is brought in later");
        Expect(script.Events.Single(e => e.ID == 14005880).Instructions[4].Bank == 2002, "the map's own script is untouched");
    }

    private static void KitEvents()
    {
        var (msb, script) = PontiffLike();
        var defs = Emedf.Parse(KitDefs);
        var kits = BossKits.Find(msb, script, Ds3Id, defs);
        var common = SyntheticCommon();
        long? Sp(long id) => id == 10636 ? 700_010_636 : null;
        var note = BossKits.AddTo(common, kits, defs, defs, new FlagMap(), Sp);
        BossKits.AddTo(common, kits, defs, defs, new FlagMap(), Sp);
        common = EMEVD.Read(common.Write());
        Expect(note.Contains("1 of 1"), note);
        var events = common.Events.Where(e => BossKits.IsKitEvent(e.ID)).OrderBy(e => e.ID).ToList();
        Expect(events.Count == 2, $"the kit event and its start, once after a rerun: {events.Count}");
        var inits = common.Events.Single(e => e.ID == 0).Instructions.Where(i => i.Bank == 2000 && i.ID == 0).Select(i => BitConverter.ToUInt32(i.ArgData, 4)).ToList();
        Expect(events.All(e => inits.Count(id => id == e.ID) == 1), "each started once from the constructor");

        uint U(EMEVD.Instruction i, int at) => BitConverter.ToUInt32(i.ArgData, at);
        uint boss = BossKits.KitEntity(4000850), clone = BossKits.KitEntity(4000851);
        Expect(boss == 1_194_000_850, $"kit entity {boss}");
        var phase = events[0].Instructions;
        Expect(phase[0].Bank == 3 && phase[0].ID == 0 && U(phase[0], 4) is var ready && ready >= BossKits.FirstFlag && ready < BossKits.FirstFlag + BossKits.FlagCount,
            "waits for the kit's ready flag first");
        var end = phase[1];
        Expect(end.Bank == 1003 && end.ID == 2 && U(end, 4) >= BossKits.FirstFlag && U(end, 4) < BossKits.FirstFlag + BossKits.FlagCount, "the defeated flag is a kit flag");
        Expect(phase.Any(i => i.Bank == 4 && i.ID == 8 && U(i, 4) == boss), "the AI message is read from the kit boss");
        Expect(phase.Any(i => i.Bank == 2004 && i.ID == 8 && U(i, 0) == boss && BitConverter.ToInt32(i.ArgData, 4) == 700_010_636), "the SpEffect goes to its ported row");
        Expect(phase.Any(i => i.Bank == 2004 && i.ID == 5 && U(i, 0) == clone && i.ArgData[4] == 1), "the clone is enabled by kit entity");
        Expect(!phase.Any(i => i.Bank == 2002), "no cutscene");

        var start = events[1].Instructions;
        int Index(Func<EMEVD.Instruction, bool> f) => start.FindIndex(i => f(i));
        int loadedBoss = Index(i => i.Bank == 4 && i.ID == 7 && U(i, 4) == boss), loadedClone = Index(i => i.Bank == 4 && i.ID == 7 && U(i, 4) == clone);
        int hide = Index(i => i.Bank == 2004 && i.ID == 5 && U(i, 0) == clone && i.ArgData[4] == 0);
        int readyOn = Index(i => i.Bank == 2003 && i.ID == 66 && U(i, 4) == U(phase[0], 4) && i.ArgData[8] == 1);
        int readyOff = Index(i => i.Bank == 2003 && i.ID == 66 && U(i, 4) == U(phase[0], 4) && i.ArgData[8] == 0);
        Expect(readyOff >= 0 && readyOff < loadedBoss && loadedBoss < hide && loadedClone < hide && hide < readyOn && readyOn == start.Count - 1,
            $"start: flags off, wait for both, hide the clone, then ready ({readyOff} {loadedBoss} {loadedClone} {hide} {readyOn})");
        Expect(start.Count(i => i.Bank == 2003 && i.ID == 66 && i.ArgData[8] == 1) == 3, "entry, first encounter and ready turn on");
    }

    private static void KitLines()
    {
        var (msb, script) = PontiffLike();
        var kits = BossKits.Find(msb, script, Ds3Id, Emedf.Parse(KitDefs));
        EnemyPort.ErEnemy? Ported(BossKits.Character c) =>
            c.Model == "c5140" ? new EnemyPort.ErEnemy("c7140", 71400000 + c.NpcParam % 10, 71400000, "Pontiff Sulyvahn", true, "test") : null;
        var lines = BossKits.Lines(kits, Ported);
        Expect(lines.Count == 2 && lines[0].StartsWith('#'), $"{lines.Count} lines");
        Expect(lines[1] == $"c7140\t71400000\t1194000850\tBoss 14000850\tc7140:71400001:71400000:1194000851", lines[1]);
        Expect(BossKits.Lines(kits, c => c.Ds3Entity == 4000850 ? Ported(c) : null).Count == 1,
            "a kit with an unported companion has no line: its start would wait for the companion for ever");
        Expect(BossKits.Lines(kits, _ => null).Count == 1, "an unported boss has no line");
    }

    private static void FightActionButtons()
    {
        var defs = Emedf.Parse(KitDefs);
        var buttons = new ActionButtons(id => id == ActionButtons.FirstRow);
        var port = new EventPort(defs, defs, [(Ds3Id, ErId)], new FlagMap()) { Buttons = buttons };
        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        var e = new EMEVD.Event(14005811, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(RunEvents.Make(3, 24, (sbyte)0, 8900, 4000800));
        e.Instructions.Add(RunEvents.Make(3, 24, (sbyte)1, 8900, 4000800));
        e.Instructions.Add(RunEvents.Make(3, 24, (sbyte)2, 4001800, 4001800));
        script.Events.Add(e);
        var converted = port.Convert(script, EMEVD.Game.DarkSouls3, id => id).Events.Single().Instructions;
        Expect(buttons.Ds3Rows.Count == 2 && buttons.Ds3Rows[8900] == ActionButtons.FirstRow + 1, "one copy per DS3 row, skipping taken rows: "
            + string.Join(" ", buttons.Ds3Rows.Select(kv => $"{kv.Key}->{kv.Value}")));
        Expect(BitConverter.ToInt32(converted[0].ArgData, 4) == buttons.Ds3Rows[8900] && BitConverter.ToInt32(converted[1].ArgData, 4) == buttons.Ds3Rows[8900],
            "both prompts name the copy");
        Expect(BitConverter.ToInt32(converted[2].ArgData, 4) == buttons.Ds3Rows[4001800] && BitConverter.ToInt32(converted[2].ArgData, 8) == ErId.EntityBase + 1800,
            "the fog wall's row is copied and its entity moves to the slot");
        Expect(port.Stats.ActionButtonsMoved == 3, port.Stats.ToString());
    }

    private static void FightWarpPoints()
    {
        var defs = Emedf.Parse(KitDefs);
        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        var e = new EMEVD.Event(14005900, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(RunEvents.Make(2003, 14, (byte)40, (byte)0, 4002810));
        e.Instructions.Add(RunEvents.Make(2003, 14, (byte)40, (byte)0, 4000990)); // already a player part
        script.Events.Add(e);
        var warps = new EventPort(defs, defs, [], new FlagMap()).WarpPoints([script]);
        Expect(warps.SetEquals([4002810, 4000990]), "warp destinations: " + string.Join(" ", warps));

        var ds3 = SyntheticDs3();
        ds3.Regions.Add(new MSB3.Region.Event { Name = "after cutscene", EntityID = 4002810, Position = new Vector3(5, -24.5f, 7), Rotation = new Vector3(0, 90, 0) });
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId).Msb;
        var notes = new List<string>();
        var players = layout.Parts.Players.Count;
        var moved = (uint)Ds3Id.MoveEntity(4002810, ErId);
        Expect(RegionPort.AddWarpPoints(ds3, layout, Ds3Id, ErId, warps, notes) == 1, "one new player part: " + string.Join("; ", notes));
        Expect(RegionPort.AddWarpPoints(ds3, layout, Ds3Id, ErId, warps, notes) == 0, "none again on a rerun");
        var point = layout.Parts.Players.Single(p => p.EntityID == moved);
        Expect(point.Position == new Vector3(5, -24.5f, 7) && point.Rotation.Y == 90 && layout.Parts.Players.Count == players + 1, "at the region's spot and turn");
        Expect(layout.Regions.GetEntries().All(r => r.EntityID != moved), "the region gives up the ID");
        Expect(MSBE.Read(layout.Write()).Parts.Players.Count(p => p.EntityID == moved) == 1, "reads back");
    }

    private static void FightArrival()
    {
        var ds3 = SyntheticDs3();
        var area = new MSB3.Region.Event { Name = "boss area", EntityID = 4002830, Position = new Vector3(100, -52f, 100), Shape = new MSB.Shape.Box(30, 30, 8) };
        ds3.Regions.Add(area);
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c5150" });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c5150_0000", ModelName = "c5150", EntityID = 4000830, Position = new Vector3(102, -50f, 98) });
        var boss = new HashSet<int> { 4000830 };
        var (floor, _) = BossArenas.ArrivalPoint(ds3, area, null, boss);
        Expect(floor == new Vector3(100, -49.5f, 100), $"the boss's floor + 0.5 m, not inside the floor: {floor}");
        Expect(BossArenas.ArrivalPoint(ds3, area).Point == new Vector3(100, -50.5f, 100), "without a boss: the area's bottom + 1.5 m");
        ds3.Regions.Add(new MSB3.Region.Event { Name = "warp", EntityID = 4002812, Position = new Vector3(95, -50f, 95) });
        Expect(BossArenas.ArrivalPoint(ds3, area, new HashSet<int> { 4002812 }, boss).Point == new Vector3(95, -49.9f, 95), "a DS3 warp point in the area wins");
        ds3.Parts.Players.Add(new MSB3.Part.Player { Name = "c0000_0030", ModelName = "c0000", Position = new Vector3(105, -50f, 105) });
        Expect(BossArenas.ArrivalPoint(ds3, area, new HashSet<int> { 4002812 }, boss).Point == new Vector3(105, -50f, 105), "a player point in the area wins");
    }

    private static void FightTalkGate()
    {
        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        var first = new EMEVD.Event(14005810, EMEVD.Event.RestBehaviorType.Default);
        first.Instructions.Add(RunEvents.Make(3, 0, (sbyte)1, (byte)1, (byte)0, 74000300)); // only dialogue sets it
        first.Instructions.Add(RunEvents.Make(3, 0, (sbyte)1, (byte)1, (byte)0, 14005802)); // a script sets this one
        first.Instructions.Add(RunEvents.Make(3, 24, (sbyte)1, 9373, 4000803));
        first.Instructions.Add(RunEvents.Make(2003, 2, 14000801, (byte)1));
        script.Events.Add(first);
        var other = new EMEVD.Event(14005820, EMEVD.Event.RestBehaviorType.Default);
        other.Instructions.Add(RunEvents.Make(2003, 2, 14005802, (byte)1));
        script.Events.Add(other);
        var (flags, entity) = BossArenas.TalkGate(script, [script], 14000801);
        Expect(flags.SequenceEqual([74000300u]) && entity == 4000803, $"talk gate: {string.Join(" ", flags)} at {entity}");
        Expect(BossArenas.TalkGate(script, [script], 14000830).Flags.Count == 0, "a fight whose first encounter nothing sets has no gate");
    }
}
