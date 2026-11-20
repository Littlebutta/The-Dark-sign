using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    // DarkScript3's layouts of the lines DS3's new-game events use (ds3-common, then er-common).
    private const string Ds3SetupDefs = """
    { "main_classes": [
      { "index": 1000, "instrs": [
        { "name": "WAIT For Condition Group State", "index": 0, "args": [ { "name": "Desired Condition Group State", "type": 0 }, { "name": "Target Condition Group", "type": 1 } ] },
        { "name": "END Unconditionally", "index": 4, "args": [ { "name": "Execution End Type", "type": 0 } ] },
        { "name": "SKIP IF Condition Group State (Compiled)", "index": 7, "args": [
          { "name": "Number Of Skipped Lines", "type": 0 }, { "name": "Desired Condition Group State", "type": 0 }, { "name": "Target Condition Group", "type": 1 } ] } ] },
      { "index": 1003, "instrs": [
        { "name": "WAIT For Event Flag", "index": 0, "args": [
          { "name": "Desired Flag State", "type": 0 }, { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 } ] },
        { "name": "END IF Event Flag", "index": 2, "args": [
          { "name": "Execution End Type", "type": 0 }, { "name": "Desired Flag State", "type": 0 }, { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 } ] } ] },
      { "index": 3, "instrs": [
        { "name": "IF Game Cycle", "index": 17, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Comparison Type", "type": 0 }, { "name": "Completed Game Cycles Threshold", "type": 0 } ] } ] },
      { "index": 2000, "instrs": [
        { "name": "Initialize Event", "index": 0, "args": [ { "name": "Event Slot ID", "type": 5 }, { "name": "Event ID", "type": 2 }, { "name": "Parameters", "type": 2 } ] } ] },
      { "index": 2003, "instrs": [
        { "name": "Set Event Flag", "index": 2, "args": [ { "name": "Event Flag ID", "type": 5 }, { "name": "Flag State", "type": 0 } ] } ] },
      { "index": 2004, "instrs": [
        { "name": "Set Character Remaining Yoel Levels", "index": 60, "args": [ { "name": "Level Count", "type": 0 } ] } ] }
    ] }
    """;

    private const string ErSetupDefs = """
    { "main_classes": [
      { "index": 1000, "instrs": [
        { "name": "END Unconditionally", "index": 4, "args": [ { "name": "Execution End Type", "type": 0 } ] },
        { "name": "SKIP IF Condition Group State (Compiled)", "index": 7, "args": [
          { "name": "Number Of Skipped Lines", "type": 0 }, { "name": "Desired Condition Group State", "type": 0 }, { "name": "Target Condition Group", "type": 1 } ] } ] },
      { "index": 1003, "instrs": [
        { "name": "END IF Event Flag", "index": 2, "args": [
          { "name": "Execution End Type", "type": 0 }, { "name": "Desired Flag State", "type": 0 }, { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 } ] } ] },
      { "index": 3, "instrs": [
        { "name": "IF Game Cycle", "index": 17, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Comparison Type", "type": 0 }, { "name": "Completed Game Cycles Threshold", "type": 0 } ] } ] },
      { "index": 2003, "instrs": [
        { "name": "Set Event Flag", "index": 66, "args": [
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 }, { "name": "Desired Flag State", "type": 0 } ] } ] }
    ] }
    """;

    private static IEnumerable<(string Name, Action Test)> SetupChecks()
    {
        yield return ("setup: DS3's new-game events (started by DS3's common event 50, flags only) run from Elden Ring's event 0, once per save on a saved flag", Ds3NewGameSetup);
    }

    private static void Ds3NewGameSetup()
    {
        static EMEVD.Instruction M(int bank, int id, params object[] args) => new(bank, id, args);
        // DS3's event 700, shortened: own-flag guard, flags, a game cycle check, the Yoel levels line.
        var once = new EMEVD.Event(700, EMEVD.Event.RestBehaviorType.Default);
        once.Instructions.AddRange([
            M(1003, 2, (byte)0, (byte)1, (byte)2, 0u),
            M(2003, 2, 50006020u, (byte)1),
            M(2004, 60, (byte)5),
            M(3, 17, (sbyte)6, (byte)4, (byte)1),
            M(1000, 7, (byte)1, (byte)0, (sbyte)6),
            M(2003, 2, 70000900u, (byte)1),
            M(1000, 4, (byte)0)]);
        var always = new EMEVD.Event(701, EMEVD.Event.RestBehaviorType.Default);
        always.Instructions.AddRange([M(2003, 2, 6000u, (byte)0), M(2003, 2, 6001u, (byte)1)]);
        var waits = new EMEVD.Event(741, EMEVD.Event.RestBehaviorType.Default);
        waits.Instructions.AddRange([M(1003, 0, (byte)1, (byte)0, 743u), M(2003, 2, 744u, (byte)1)]);
        var passed = new EMEVD.Event(9080, EMEVD.Event.RestBehaviorType.Default);
        passed.Instructions.Add(M(2003, 2, 0u, (byte)0));
        passed.Parameters.Add(new EMEVD.Parameter(0, 0, 0, 4));
        var notStarted = new EMEVD.Event(702, EMEVD.Event.RestBehaviorType.Default);
        notStarted.Instructions.Add(M(2003, 2, 745u, (byte)1));
        var load = new EMEVD.Event(Ds3Setup.LoadEvent, EMEVD.Event.RestBehaviorType.Default);
        foreach (var id in new uint[] { 701, 700, 741, 9080 })
            load.Instructions.Add(M(2000, 0, 0, id, 0u));
        var ds3 = new EMEVD(EMEVD.Game.DarkSouls3);
        ds3.Events.AddRange([load, once, always, waits, passed, notStarted]);

        Expect(Ds3Setup.SetupEvents(ds3).Select(e => e.ID).SequenceEqual(new long[] { 701, 700 }),
            "flag-only events event 50 starts, in its order: " + string.Join(",", Ds3Setup.SetupEvents(ds3).Select(e => e.ID)));

        var common = new EMEVD(EMEVD.Game.Sekiro);
        common.Events.Add(new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        var flags = new FlagMap();
        var port = new EventPort(Emedf.Parse(Ds3SetupDefs), Emedf.Parse(ErSetupDefs), [(Ds3Id, ErId)], flags);
        var line = Ds3Setup.AddTo(common, ds3, port);
        Ds3Setup.AddTo(common, ds3, port); // a second build replaces, never doubles
        Expect(line.Contains("2 of DS3's new-game events") && line.Contains($"once per save, flag {Ds3Setup.FirstDoneFlag + 1}"), line);
        var starts = common.Events[0].Instructions.Select(i => BitConverter.ToUInt32(i.ArgData, 4)).ToList();
        Expect(starts.SequenceEqual(new[] { Ds3Setup.FirstEventId, Ds3Setup.FirstEventId + 1 }), "event 0 starts each once: " + string.Join(",", starts));
        var setup = common.Events.Single(e => e.ID == Ds3Setup.FirstEventId + 1);
        uint Flag(EMEVD.Instruction i) => BitConverter.ToUInt32(i.ArgData, 4);
        Expect(setup.Instructions[0] is { Bank: 1003, ID: 2 } && Flag(setup.Instructions[0]) == Ds3Setup.FirstDoneFlag + 1 && setup.Instructions[0].ArgData[1] == 1
               && setup.Instructions[1] is { Bank: 2003, ID: 66 } && Flag(setup.Instructions[1]) == Ds3Setup.FirstDoneFlag + 1 && setup.Instructions[1].ArgData[8] == 1,
            "a once-per-save event ends if its saved flag is on, else turns it on first");
        Expect(setup.Instructions[2] is { Bank: 1001, ID: 0 }, "DS3's own-flag guard is a no-op");
        Expect(setup.Instructions[3] is { Bank: 2003, ID: 66 } && Flag(setup.Instructions[3]) == flags.Flags[50006020] && setup.Instructions[3].ArgData[8] == 1,
            "50006020 is set on, under the maps' number for it");
        Expect(setup.Instructions.Count == once.Instructions.Count + 2 && setup.Instructions[6] is { Bank: 1000, ID: 7 } && setup.Instructions[6].ArgData[0] == 1,
            "line for line, so skips still land where DS3's do");
        Expect(Preflight(Ds3Setup.FirstDoneFlag) == "saved" && Preflight(Ds3Setup.FirstDoneFlag + Ds3Setup.MaxEvents - 1) == "saved",
            "the once-per-save flags are in JortPob's saved range");
        Expect(Ds3Setup.FirstEventId + Ds3Setup.MaxEvents <= BonfireTravel.EventId(0), "setup events stop before the bonfire travel events");
    }

    private static string Preflight(long flag) => DarkSign.Core.Preflight.JortPobLayout(flag);
}
