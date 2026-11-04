using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>Offline checks of the DS3 event script converter, on small made-up instruction definitions.</summary>
public static partial class MapPortChecks
{
    public static IEnumerable<(string Name, Action Test)> EventChecks()
    {
        yield return ("event port: instructions match by name and renames, arguments by name, kind and shared words", EventArgMatching);
        yield return ("event port: map entities, map flags and global flags move; warps go to the DS3 map's slot", EventIdMoves);
        yield return ("event port: unknown commands become no-ops, unknown conditions drop the event and its start", EventDrops);
        yield return ("event port: event parameters follow their argument and values passed in move by kind", EventParameters);
        yield return ("event port: DS3 common events are ported once, under saved IDs, with Elden Ring's slot argument", EventCommon);
        yield return ("event port: flag ranges stay consecutive and the flag table reads back the same", EventFlagTable);
        yield return ("layout: connect collisions point at the neighbouring map's slot; ones to unported maps are left out", ConnectCollisions);
        yield return ("event port: DS3 action buttons become IF Action Button in Area with one ActionButtonParam row per prompt", EventActionButtons);
    }

    // Made-up definitions in DarkScript3's format, shaped like the real ones where it matters.
    private const string Ds3Defs = """
    { "main_classes": [
      { "index": 3, "instrs": [
        { "name": "IF Event Flag", "index": 0, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Desired Flag State", "type": 0 },
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 } ] },
        { "name": "IF Steam Disconnected", "index": 38, "args": [ { "name": "Result Condition Group", "type": 3 } ] },
        { "name": "IF Action Button", "index": 5, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Target Entity Type", "type": 5 },
          { "name": "Target Entity ID", "type": 5 }, { "name": "Reaction Angle (deg)", "type": 6 },
          { "name": "Dummypoly ID", "type": 4 }, { "name": "Reaction Distance", "type": 6 },
          { "name": "Help Message ID", "type": 5 }, { "name": "Reaction Type", "type": 0 }, { "name": "Pad ID", "type": 5 } ] },
        { "name": "IF Batch Event Flags", "index": 1, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Desired Flag State", "type": 0 },
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Starting Target Event Flag ID", "type": 2 },
          { "name": "Ending Target Event Flag ID", "type": 2 } ] },
      ] },
      { "index": 2003, "instrs": [
        { "name": "Set Event Flag", "index": 2, "args": [ { "name": "Event Flag ID", "type": 2 }, { "name": "Flag State", "type": 0 } ] },
        { "name": "Warp Player", "index": 14, "args": [
          { "name": "Area ID", "type": 0 }, { "name": "Block ID", "type": 0 }, { "name": "Initial Area ID", "type": 5 } ] },
        { "name": "Hellkite Breath Control", "index": 36, "args": [ { "name": "Entity ID", "type": 5 } ] },
      ] },
      { "index": 2005, "instrs": [
        { "name": "(De)activate Object", "index": 3, "args": [ { "name": "Object Entity ID", "type": 5 }, { "name": "State", "type": 3 } ] },
      ] },
    ] }
    """;

    private const string ErDefs = """
    { "main_classes": [
      { "index": 3, "instrs": [
        // a comment, as in DarkScript3's files
        { "name": "IF Event Flag", "index": 0, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Desired Flag State", "type": 0 },
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 } ] },
        { "name": "IF Batch Event Flags", "index": 1, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Desired Flag State", "type": 0 },
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Starting Target Event Flag ID", "type": 2 },
          { "name": "Ending Target Event Flag ID", "type": 2 } ] },
        { "name": "IF Action Button in Area", "index": 24, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Action Button Parameter ID", "type": 5, "default": -1 },
          { "name": "Target Entity ID", "type": 2 } ] },
      ] },
      { "index": 2003, "instrs": [
        { "name": "Set Event Flag", "index": 66, "args": [
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 },
          { "name": "Desired Flag State", "type": 0 } ] },
        { "name": "Warp Player", "index": 14, "args": [
          { "name": "Area ID", "type": 0 }, { "name": "Block ID", "type": 0 }, { "name": "Region ID", "type": 0 },
          { "name": "Index ID", "type": 0 }, { "name": "Initial Area Entity ID", "type": 2 },
          { "name": "Subarea Name Popup Message ID", "type": 5, "default": -1 } ] },
      ] },
      { "index": 2005, "instrs": [
        { "name": "Change Asset Enable State", "index": 3, "args": [
          { "name": "State", "type": 3, "default": 1 }, { "name": "Target Asset Entity ID", "type": 5 } ] },
      ] },
    ] }
    """;

    private static readonly MapId Ds3High = MapId.Parse("m30_00_00_00"), ErHigh = MapId.Parse("m45_01_00_00");

    private static EventPort NewEventPort(FlagMap? flags = null) =>
        new(Emedf.Parse(Ds3Defs), Emedf.Parse(ErDefs), [(Ds3Id, ErId), (Ds3High, ErHigh)], flags ?? new FlagMap());

    private static EMEVD.Instruction Ins(int bank, int id, params object[] args) => RunEvents.Make(bank, id, args);

    private static long Arg(EMEVD.Instruction ins, string name, Emedf defs)
    {
        var def = defs.Get(ins.Bank, ins.ID) ?? throw new Exception($"no definition for {ins.Bank}[{ins.ID}]");
        return Emedf.Read(ins.ArgData, def.Args.First(a => a.Name == name));
    }

    private static EMEVD OneEvent(long id, params EMEVD.Instruction[] instructions)
    {
        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        var e = new EMEVD.Event(id, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.AddRange(instructions);
        script.Events.Add(e);
        return script;
    }

    private static Func<long, long> MoveToSlot => id => id is 0 or 50 || !Ds3Id.OwnsDs3Id(id) ? id : Ds3Id.MoveEntity((int)id, ErId);

    private static void EventArgMatching()
    {
        var er = Emedf.Parse(ErDefs);
        var ds3 = OneEvent(14000100,
            Ins(2005, 3, 4000100, (sbyte)0),        // (De)activate Object -> Change Asset Enable State, arguments swapped
            Ins(2003, 2, 14000500u, (byte)1));      // Set Event Flag: flag type stays 0, state goes to Desired Flag State
        var port = NewEventPort();
        var result = port.Convert(ds3, EMEVD.Game.Sekiro, MoveToSlot);
        var ins = result.Events[0].Instructions;
        Expect(ins[0].Bank == 2005 && ins[0].ID == 3, $"(De)activate Object became {ins[0].Bank}[{ins[0].ID}]");
        Expect(Arg(ins[0], "State", er) == 0, "Change Asset Enable State lost the DS3 state");
        Expect(Arg(ins[0], "Target Asset Entity ID", er) == 45000100, $"asset entity {Arg(ins[0], "Target Asset Entity ID", er)}, expected 45000100");
        Expect(ins[1].Bank == 2003 && ins[1].ID == 66, $"Set Event Flag became {ins[1].Bank}[{ins[1].ID}]");
        Expect(Arg(ins[1], "Target Event Flag Type", er) == 0, "Set Event Flag's flag type took the DS3 flag state");
        Expect(Arg(ins[1], "Desired Flag State", er) == 1, "Set Event Flag lost the DS3 flag state");
        Expect(result.Events[0].ID == 45000100, $"event ID {result.Events[0].ID}, expected 45000100");
        result.Write(); // writes in Elden Ring's format
    }

    private static void EventIdMoves()
    {
        var er = Emedf.Parse(ErDefs);
        var flags = new FlagMap();
        var ds3 = OneEvent(14000101,
            Ins(2003, 2, 14000500u, (byte)1),       // this map's flag
            Ins(2003, 2, 13000800u, (byte)1),       // High Wall's flag: moves to its slot
            Ins(2003, 2, 50000u, (byte)1),          // a global flag: gets an Elden Ring flag of its own
            Ins(2003, 14, (byte)30, (byte)0, 3000990), // warp to High Wall -> its slot
            Ins(2003, 14, (byte)35, (byte)0, 3500990)); // warp to a map with no slot: left out
        var result = NewEventPort(flags).Convert(ds3, EMEVD.Game.Sekiro, MoveToSlot);
        var ins = result.Events[0].Instructions;
        Expect(Arg(ins[0], "Target Event Flag ID", er) == 45000500, $"map flag became {Arg(ins[0], "Target Event Flag ID", er)}");
        Expect(Arg(ins[1], "Target Event Flag ID", er) == 45010800, $"other map's flag became {Arg(ins[1], "Target Event Flag ID", er)}");
        Expect(Arg(ins[2], "Target Event Flag ID", er) == FlagMap.FlagSlot(0), $"global flag became {Arg(ins[2], "Target Event Flag ID", er)}");
        Expect(flags.Flags.GetValueOrDefault(50000) == FlagMap.FlagSlot(0), "the global flag wasn't recorded in the flag table");
        Expect(ins[3].Bank == 2003 && ins[3].ID == 14, "the warp didn't convert");
        Expect(Arg(ins[3], "Area ID", er) == 45 && Arg(ins[3], "Block ID", er) == 1, "the warp doesn't go to High Wall's slot");
        Expect(Arg(ins[3], "Initial Area Entity ID", er) == 45010990, $"warp point {Arg(ins[3], "Initial Area Entity ID", er)}, expected 45010990");
        Expect(Arg(ins[3], "Subarea Name Popup Message ID", er) == -1, "Elden Ring-only argument lost its default");
        Expect(ins[4].Bank == 1001 && ins[4].ID == 0, "a warp to an unported map should be a no-op");
    }

    private static void EventDrops()
    {
        var ds3 = new EMEVD(EMEVD.Game.DarkSouls3);
        var constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        constructor.Instructions.Add(Ins(2000, 0, 0, 14000300u, 0));
        constructor.Instructions.Add(Ins(2000, 0, 0, 14000301u, 0));
        ds3.Events.Add(constructor);
        var unknownCommand = new EMEVD.Event(14000300, EMEVD.Event.RestBehaviorType.Default);
        unknownCommand.Instructions.Add(Ins(2003, 36, 4000100));        // no Elden Ring form: a no-op
        unknownCommand.Instructions.Add(Ins(2003, 2, 14000500u, (byte)1));
        ds3.Events.Add(unknownCommand);
        var unknownCondition = new EMEVD.Event(14000301, EMEVD.Event.RestBehaviorType.Default);
        unknownCondition.Instructions.Add(Ins(3, 38, (sbyte)0));        // a condition with no Elden Ring form
        ds3.Events.Add(unknownCondition);

        var port = NewEventPort();
        var result = port.Convert(ds3, EMEVD.Game.Sekiro, MoveToSlot);
        Expect(result.Events.Select(e => e.ID).SequenceEqual(new long[] { 0, 45000300 }), $"events {string.Join(",", result.Events.Select(e => e.ID))}");
        var c = result.Events[0].Instructions;
        Expect(c[0].Bank == 2000 && BitConverter.ToUInt32(c[0].ArgData, 4) == 45000300, "the kept event's start lost its new ID");
        Expect(c[1].Bank == 1001 && c[1].ID == 0, "the dropped event is still started");
        var kept = result.Events[1].Instructions;
        Expect(kept.Count == 2 && kept[0].Bank == 1001 && kept[1].Bank == 2003, "the unknown command isn't a no-op on the same line");
        Expect(port.Stats.EventsDropped == 1 && port.Stats.NoOps == 1, $"report: {port.Stats}");
    }

    private static void EventParameters()
    {
        var er = Emedf.Parse(ErDefs);
        var ds3 = new EMEVD(EMEVD.Game.DarkSouls3);
        var constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        constructor.Instructions.Add(Ins(2000, 0, 0, 14000200u, 4000300, 14000600u));
        ds3.Events.Add(constructor);
        var e = new EMEVD.Event(14000200, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(Ins(2005, 3, 0, (sbyte)1));   // entity from parameter X0_4
        e.Instructions.Add(Ins(2003, 2, 0u, (byte)1));   // flag from parameter X4_4
        e.Parameters.Add(new EMEVD.Parameter(0, 0, 0, 4));
        e.Parameters.Add(new EMEVD.Parameter(1, 0, 4, 4));
        ds3.Events.Add(e);

        var port = NewEventPort();
        port.Learn(ds3);
        var result = port.Convert(ds3, EMEVD.Game.Sekiro, MoveToSlot);
        var init = result.Events[0].Instructions[0];
        Expect(BitConverter.ToUInt32(init.ArgData, 4) == 45000200, "the started event's ID didn't move");
        Expect(BitConverter.ToInt32(init.ArgData, 8) == 45000300, $"entity passed in became {BitConverter.ToInt32(init.ArgData, 8)}");
        Expect(BitConverter.ToInt32(init.ArgData, 12) == 45000600, $"flag passed in became {BitConverter.ToInt32(init.ArgData, 12)}");
        var ps = result.Events[1].Parameters;
        var assetArg = er.Get(2005, 3)!.Args.First(a => a.Name == "Target Asset Entity ID");
        Expect(ps.Count == 2 && ps[0].TargetStartByte == assetArg.Offset, $"entity parameter targets byte {ps.FirstOrDefault()?.TargetStartByte}, expected {assetArg.Offset}");
        var flagArg = er.Get(2003, 66)!.Args.First(a => a.Name == "Target Event Flag ID");
        Expect(ps[1].TargetStartByte == flagArg.Offset && ps[1].SourceStartByte == 4, "flag parameter didn't follow its argument");
    }

    private static void EventCommon()
    {
        var ds3Defs = Emedf.Parse(Ds3Defs);
        var erDefs = Emedf.Parse(ErDefs);
        var flags = new FlagMap();
        var mapScript = OneEvent(0, Ins(2000, 6, 20005000u, 4000400));
        var common = new EMEVD(EMEVD.Game.DarkSouls3);
        var c1 = new EMEVD.Event(20005000, EMEVD.Event.RestBehaviorType.Default);
        c1.Instructions.Add(Ins(2005, 3, 0, (sbyte)1));
        c1.Instructions.Add(Ins(2000, 6, 20005001u, 0));
        c1.Parameters.Add(new EMEVD.Parameter(0, 0, 0, 4));
        common.Events.Add(c1);
        var c2 = new EMEVD.Event(20005001, EMEVD.Event.RestBehaviorType.Default);
        c2.Instructions.Add(Ins(3, 38, (sbyte)0)); // can't convert: dropped, and what starts it disabled
        common.Events.Add(c2);
        common.Events.Add(new EMEVD.Event(20009999, EMEVD.Event.RestBehaviorType.Default)); // never started: not ported

        var erCommon = new EMEVD(EMEVD.Game.Sekiro);
        erCommon.Events.Add(new EMEVD.Event(90005000, EMEVD.Event.RestBehaviorType.Default));
        erCommon.Events.Add(new EMEVD.Event(FlagMap.EventSlot(5), EMEVD.Event.RestBehaviorType.Default)); // from an earlier run
        var template = new EMEVD(EMEVD.Game.Sekiro) { LinkedFileOffsets = [0], StringData = [1, 2, 3, 4] };
        var slot = MapSlots.All.First(s => s.Ds3 == Ds3Id && !s.Probe);

        var result = MapEvents.Convert(ds3Defs, erDefs, flags, [slot], new Dictionary<MapId, EMEVD> { [Ds3Id] = mapScript },
            common, _ => template, erCommon);
        var script = result.Scripts[ErId];
        Expect(script.LinkedFileOffsets.Count == 1 && script.StringData.Length == 4, "the slot script lost its link to common_func");
        Expect(script.Events.Any(e => e.ID == 50), "event 50 is missing");
        var start = script.Events.First(e => e.ID == 0).Instructions[0];
        var id1 = flags.Events[20005000];
        Expect(start.Bank == 2000 && start.ID == 6 && BitConverter.ToInt32(start.ArgData, 0) == 0, "InitializeCommonEvent has no slot argument");
        Expect(BitConverter.ToUInt32(start.ArgData, 4) == id1 && FlagMap.IsEventSlot(id1), $"common event started as {BitConverter.ToUInt32(start.ArgData, 4)}");
        Expect(BitConverter.ToInt32(start.ArgData, 8) == 45000400, "the entity passed to the common event didn't move");
        var ids = result.CommonFunc.Events.Select(e => e.ID).ToList();
        Expect(ids.Contains(90005000), "Elden Ring's own common event was lost");
        Expect(!ids.Contains(FlagMap.EventSlot(5)), "an earlier run's ported event was kept");
        Expect(ids.Contains(id1) && !ids.Contains(flags.Events.GetValueOrDefault(20005001)), "common events: wrong ones ported");
        Expect(!flags.Events.ContainsKey(20009999), "a common event nothing starts was ported");
        var ported = result.CommonFunc.Events.First(e => e.ID == id1);
        Expect(ported.Instructions[1].Bank == 1001, "the start of the dropped common event wasn't disabled");
        var assetArg = erDefs.Get(2005, 3)!.Args.First(a => a.Name == "Target Asset Entity ID");
        Expect(ported.Parameters.Count == 1 && ported.Parameters[0].TargetStartByte == assetArg.Offset, "the common event's parameter didn't follow its argument");
        result.CommonFunc.Write();
    }

    private static void EventFlagTable()
    {
        var flags = new FlagMap();
        var ds3 = OneEvent(14000102, Ins(3, 1, (sbyte)0, (byte)1, (byte)0, 60000u, 60009u));
        var port = NewEventPort(flags);
        port.Learn(ds3);
        flags.Flag(70000);
        var start = flags.Flags[60000];
        Expect(Enumerable.Range(0, 10).All(i => flags.Flags[60000 + i] == start + i), "the batch flag range isn't consecutive");
        var result = port.Convert(ds3, EMEVD.Game.Sekiro, MoveToSlot);
        var er = Emedf.Parse(ErDefs);
        var ins = result.Events[0].Instructions[0];
        Expect(Arg(ins, "Ending Target Event Flag ID", er) - Arg(ins, "Starting Target Event Flag ID", er) == 9, "the converted range lost its length");
        flags.Event(20005000);

        var path = Path.Combine(Path.GetTempPath(), $"darksign-flags-{Guid.NewGuid():N}.tsv");
        try
        {
            flags.Save(path);
            var back = FlagMap.Load(path);
            Expect(back.Flags.Count == flags.Flags.Count && back.Flags.All(f => flags.Flags[f.Key] == f.Value), "flags don't read back the same");
            Expect(back.Events.GetValueOrDefault(20005000) == flags.Events[20005000], "events don't read back the same");
            Expect(back.Flag(80000) != back.Flags[70000] && back.Flags.Values.Distinct().Count() == back.Flags.Count, "a new flag reused a saved number");
            Expect(back.Event(20005001) != back.Events[20005000], "a new event reused a saved number");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void EventActionButtons()
    {
        var er = Emedf.Parse(ErDefs);
        // A lever twice with the same prompt, and a door with another: two rows. The second lever's entity is a parameter.
        var ds3 = new EMEVD(EMEVD.Game.DarkSouls3);
        var e = new EMEVD.Event(14000400, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(Ins(3, 5, (sbyte)0, 1, 4001100, 60f, (short)100, 1.5f, 10010400, (byte)0, 0));
        e.Instructions.Add(Ins(3, 5, (sbyte)-1, 1, 0, 60f, (short)100, 1.5f, 10010400, (byte)0, 0));
        e.Instructions.Add(Ins(3, 5, (sbyte)1, 1, 4001200, 180f, (short)-1, 2f, 10010401, (byte)0, 0));
        e.Parameters.Add(new EMEVD.Parameter(1, 8, 0, 4)); // Target Entity ID is at byte 8 (after the s8 group and the s32 type)
        ds3.Events.Add(e);

        var taken = new HashSet<int> { ActionButtons.FirstRow };
        var buttons = new ActionButtons(taken.Contains);
        var port = new EventPort(Emedf.Parse(Ds3Defs), er, [(Ds3Id, ErId)], new FlagMap()) { Buttons = buttons };
        port.Learn(ds3);
        var result = port.Convert(ds3, EMEVD.Game.Sekiro, MoveToSlot);
        Expect(result.Events.Count == 1, "the event with action buttons was left out");
        var ins = result.Events[0].Instructions;
        Expect(ins.All(i => i.Bank == 3 && i.ID == 24), "not every action button became IF Action Button in Area");
        Expect(Arg(ins[0], "Target Entity ID", er) == 45001100, $"button entity {Arg(ins[0], "Target Entity ID", er)}, expected 45001100");
        Expect(Arg(ins[1], "Result Condition Group", er) == -1, "condition group lost");
        long row0 = Arg(ins[0], "Action Button Parameter ID", er), row1 = Arg(ins[1], "Action Button Parameter ID", er),
            row2 = Arg(ins[2], "Action Button Parameter ID", er);
        Expect(row0 == row1 && row0 != row2, "the same prompt should share one row and another prompt get its own");
        Expect(row0 == ActionButtons.FirstRow + 1, $"row {row0} doesn't skip an ID Elden Ring already uses");
        Expect(buttons.Rows.Count == 2 && buttons.Rows.Keys.Any(b => b.DummyPoly == 100 && b.Angle == 60 && b.Distance == 1.5f && b.HelpMessage == 10010400),
            "the prompt's shape wasn't read from the DS3 instruction");
        var p = result.Events[0].Parameters;
        var entityArg = er.Get(3, 24)!.Args[2];
        Expect(p.Count == 1 && p[0].InstructionIndex == 1 && p[0].TargetStartByte == entityArg.Offset, "the entity parameter didn't follow its argument");

        // Without rows to point at, the event is left out as before.
        var without = NewEventPort().Convert(ds3, EMEVD.Game.Sekiro, MoveToSlot);
        Expect(without.Events.Count == 0, "action buttons converted with nowhere to put their rows");
    }

    private static void ConnectCollisions()
    {
        var ds3 = SyntheticDs3();
        ds3.Parts.ConnectCollisions.Add(new MSB3.Part.ConnectCollision
        {
            Name = "h000100_0001", ModelName = "h000100", CollisionName = "h000100_0000",
        });
        ds3.Parts.ConnectCollisions[0].MapID[0] = 30; // High Wall: ported to m45_01
        var unported = new MSB3.Part.ConnectCollision { Name = "h000100_0002", ModelName = "h000100", CollisionName = "h000100_0000" };
        unported.MapID[0] = 35;
        ds3.Parts.ConnectCollisions.Add(unported);
        var template = new MSBE.Part.ConnectCollision { Name = "h000000_0000", ModelName = "h000000", CollisionName = "x" };

        var result = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId, null, [(Ds3Id, ErId), (Ds3High, ErHigh)], template);
        var connects = result.Msb.Parts.ConnectCollisions;
        Expect(connects.Count == 1, $"{connects.Count} connect collisions, expected 1");
        Expect(connects[0].MapID.SequenceEqual(new byte[] { 45, 1, 0, 0 }), $"connect collision points at {string.Join(",", connects[0].MapID)}");
        Expect(connects[0].CollisionName == "h000100_0000", "connect collision lost its collision");
        var bytes = result.Msb.Write();
        var back = MSBE.Read(bytes);
        Expect(back.Parts.ConnectCollisions.Count == 1 && back.Parts.ConnectCollisions[0].CollisionName == "h000100_0000", "connect collision doesn't read back");

        var none = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        Expect(none.Msb.Parts.ConnectCollisions.Count == 0, "connect collisions ported without knowing the slots");
    }
}
