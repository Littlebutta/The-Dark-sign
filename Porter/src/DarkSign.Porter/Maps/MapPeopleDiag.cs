using System.Globalization;
using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "map-diag people [DS3 map ID...]": who DS3's people are and what the port made of them (RULE 4), written to
/// Work\reports\map-diag\people.txt. Read-only.
/// <list type="bullet">
/// <item>Every DS3 enemy part that is a person (c0000 human or a talker): NpcParam row, its DS3 name, its DS3
/// teamType, talk script, think row, layer (other map state?), position.</item>
/// <item>Every DS3 script line that sets a character's team (2004[2], 2004[44]) with the values it uses.</item>
/// <item>Every enemy in the built mod map of the slot: model, NpcParam row, the mod regulation's teamType and name for
/// it, think row, talk ID, entity, position; and the mod script's team lines.</item>
/// </list>
/// </summary>
public static class MapPeopleDiag
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var wanted = args.Select(MapId.Parse).ToHashSet();
        var slots = MapSlots.All.Where(s => !s.Probe && (wanted.Count == 0 || wanted.Contains(s.Ds3))).ToList();
        var dir = ctx.WorkPath("reports", "map-diag");
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        void Line(string text) => report.AppendLine(text);

        var ds3Defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var erDefs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("er-common")));
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        // Opening Elden Ring's archives registers its Oodle library, which the mod's map and script files need.
        using var erArchives = ctx.OpenArchives(PorterGame.EldenRing);
        PARAM? ds3Npc = null, modNpc = null;
        try { ds3Npc = ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcParam"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Line($"DS3 NpcParam unreadable: {ex.Message}"); }
        try { modNpc = ActionButtons.LoadRegulation(ctx).Get("NpcParam"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Line($"mod NpcParam unreadable: {ex.Message}"); }

        string Team(PARAM? p, int id) => p?[id] is { } r ? PeopleArmour.Cell(r, "teamType").ToString(CultureInfo.InvariantCulture) : "-";
        string Name(PARAM? p, int id) => p?[id]?.Name ?? "";
        string Pos(System.Numerics.Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:F1},{v.Y:F1},{v.Z:F1}");

        var (scripts, common) = MapEvents.ReadDs3(ds3, slots);
        foreach (var slot in slots)
        {
            var path = MapPorter.MsbPath(slot.Ds3);
            if (!ds3.Contains(path))
                continue;
            var msb = MSB3.Read(ds3.Read(path));
            var layerOk = MsbPort.BaseLayerMeans(msb);
            Line($"=== DS3 {slot.Ds3} -> {slot.Er}: people (c0000 or talkers)");
            Line("  name\tentity\tNpcParam\tDS3 name\tDS3 team\ttalk\tthink\tCharaInit\tother state\tposition");
            foreach (var e in msb.Parts.Enemies.Where(e => EnemyPort.People.IsHuman(e.ModelName) || e.TalkID > 0))
                Line($"  {e.Name}\t{e.EntityID}\t{e.NPCParamID}\t{Name(ds3Npc, e.NPCParamID)}\t{Team(ds3Npc, e.NPCParamID)}\t{e.TalkID}\t{e.ThinkParamID}\t{e.CharaInitID}"
                     + $"\t{(MsbPort.OtherState(e, layerOk) ? "yes" : "no")}\t{Pos(e.Position)}");
            Line($"  DS3 teams of every other enemy row in the map: " + string.Join(", ", msb.Parts.Enemies
                .Where(e => !EnemyPort.People.IsHuman(e.ModelName) && e.TalkID <= 0 && e.NPCParamID > 0)
                .GroupBy(e => Team(ds3Npc, e.NPCParamID)).OrderBy(g => g.Key).Select(g => $"team {g.Key} x{g.Count()}")));

            var passedTeams = new HashSet<long>();
            if (common is not null)
                TeamLines(common, ds3Defs, "DS3 common_func", slot == slots[0] ? Line : _ => { }, passedTeams);
            if (scripts.TryGetValue(slot.Ds3, out var script))
            {
                TeamLines(script, ds3Defs, "DS3 " + slot.Ds3, Line, passedTeams);
                Starts(script, passedTeams, "DS3 " + slot.Ds3, Line);
            }

            var modMsb = ctx.ModPath("map", "mapstudio", $"{slot.Er}.msb.dcx");
            if (!File.Exists(modMsb))
            {
                Line($"  mod map {slot.Er}: not built");
                continue;
            }
            var er = MSBE.Read(modMsb);
            Line($"=== mod {slot.Er}: every enemy part");
            Line("  name\tmodel\tentity\tNpcParam\tmod row name\tmod team\tthink\ttalk\tCharaInit\tposition");
            foreach (var e in er.Parts.Enemies)
                Line($"  {e.Name}\t{e.ModelName}\t{e.EntityID}\t{e.NPCParamID}\t{Name(modNpc, e.NPCParamID)}\t{Team(modNpc, e.NPCParamID)}\t{e.ThinkParamID}\t{e.TalkID}\t{e.CharaInitID}\t{Pos(e.Position)}");
            // Every event started with a person (quest states, hostility on damage...), written out line by line.
            var people = msb.Parts.Enemies.Where(e => EnemyPort.People.IsHuman(e.ModelName) || e.TalkID > 0).Select(e => (long)e.EntityID).Where(id => id > 0).ToHashSet();
            if (script is not null)
                Dump(script, common, ds3Defs, people, "DS3 " + slot.Ds3, Line);
            // The people's DS3 talk scripts and their converted ones, state by state (what turns a flag on, and when).
            var talkIds = msb.Parts.Enemies.Where(e => EnemyPort.People.IsHuman(e.ModelName) || e.TalkID > 0).Select(e => e.TalkID).Where(t => t > 0).ToHashSet();
            if (ds3.Contains(TalkPort.TalkPath(slot.Ds3)))
                foreach (var f in BND4.Read(ds3.Read(TalkPort.TalkPath(slot.Ds3))).Files.Where(f => TalkPort.ScriptId(f.Name) is int id && talkIds.Contains(id)))
                    DumpTalk(ESD.Read(f.Bytes), $"DS3 talk {f.Name}", Line);
            var modTalk = ctx.ModPath(MapPorter.ModParts(TalkPort.TalkPath(slot.Er)));
            if (File.Exists(modTalk))
                foreach (var f in BND4.Read(DCX.Decompress(File.ReadAllBytes(modTalk))).Files.Where(f => TalkPort.ScriptId(f.Name) is int id && id >= 900_000_000 && talkIds.Contains(id % 1_000_000)))
                    DumpTalk(ESD.Read(f.Bytes), $"mod talk {f.Name}", Line);
            var modPeople = er.Parts.Enemies.Where(e => EnemyPort.People.IsHuman(e.ModelName) || e.TalkID > 0).Select(e => (long)e.EntityID).Where(id => id > 0).ToHashSet();
            var modCommonPath = ctx.ModPath("event", "common_func.emevd.dcx");
            var modCommon = File.Exists(modCommonPath) ? EMEVD.Read(modCommonPath) : null;
            var modScript = ctx.ModPath("event", $"{slot.Er}.emevd.dcx");
            if (File.Exists(modScript))
                Dump(EMEVD.Read(modScript), modCommon, erDefs, modPeople, "mod " + slot.Er, Line);
            var flagFile = ctx.ModPath(MapEvents.FlagFile);
            if (File.Exists(flagFile))
                Line($"  flag table {MapEvents.FlagFile}: lines naming {slot.Ds3}'s people flags are in the porter log; first 5 lines: "
                     + string.Join(" | ", File.ReadLines(flagFile).Take(5)));
            if (File.Exists(modScript))
            {
                var modPassed = new HashSet<long>();
                var mod = EMEVD.Read(modScript);
                TeamLines(mod, erDefs, "mod " + slot.Er, Line, modPassed);
                Starts(mod, modPassed, "mod " + slot.Er, Line);
            }
        }
        var file = Path.Combine(dir, "people.txt");
        File.WriteAllText(file, report.ToString());
        Console.WriteLine($"map-diag people: {file}");
        return 0;
    }

    /// <summary>Every "Set Character Team Type" line (2004[2], 2004[44]) of a script, with its values and which are passed in.</summary>
    private static void TeamLines(EMEVD script, Emedf defs, string what, Action<string> line, HashSet<long> passedEvents)
    {
        foreach (var e in script.Events)
            for (int i = 0; i < e.Instructions.Count; i++)
            {
                var ins = e.Instructions[i];
                if (ins.Bank != 2004 || ins.ID is not (2 or 44) || defs.Get(2004, ins.ID) is not { } def)
                    continue;
                var passed = e.Parameters.Where(p => p.InstructionIndex == i).Select(p => $"byte {p.TargetStartByte} <- param byte {p.SourceStartByte}").ToList();
                if (passed.Count > 0)
                    passedEvents.Add(e.ID);
                line($"  {what} event {e.ID} line {i}: {def.Name}("
                     + string.Join(", ", def.Args.Select(a => $"{a.Name} {Emedf.Read(ins.ArgData, a)}")) + ")"
                     + (passed.Any() ? $" passed in: {string.Join("; ", passed)}" : ""));
            }
    }

    /// <summary>Every line that starts one of <paramref name="events"/> (2000[0] / 2000[6]), with its raw int32 arguments.</summary>
    private static void Starts(EMEVD script, HashSet<long> events, string what, Action<string> line)
    {
        foreach (var e in script.Events)
            foreach (var ins in e.Instructions.Where(x => x.Bank == 2000 && x.ID is 0 or 6))
            {
                var data = ins.ArgData;
                var ints = Enumerable.Range(0, data.Length / 4).Select(k => (long)BitConverter.ToInt32(data, k * 4)).ToList();
                if (ints.Count > 1 && ints.Take(3).Any(events.Contains))
                    line($"  {what} event {e.ID} starts: 2000[{ins.ID:D2}] {string.Join(" ", ints)}");
            }
    }

    /// <summary>Every event a start line (2000[00] / 2000[06]) starts with one of <paramref name="people"/>, from the
    /// map script or common_func, written out instruction by instruction with argument names and passed-in values.</summary>
    private static void Dump(EMEVD script, EMEVD? common, Emedf defs, HashSet<long> people, string what, Action<string> line)
    {
        var wanted = new SortedSet<long>();
        foreach (var e in script.Events)
            foreach (var ins in e.Instructions.Where(x => x.Bank == 2000 && x.ID is 0 or 6))
            {
                var data = ins.ArgData;
                var ints = Enumerable.Range(0, data.Length / 4).Select(k => (long)BitConverter.ToInt32(data, k * 4)).ToList();
                // DS3's 2000[06] starts with the event ID; Elden Ring's with a slot, then the event ID.
                int idAt = defs.Get(2000, ins.ID)?.Args.FirstOrDefault(a => a.Name.Contains("Event ID", StringComparison.OrdinalIgnoreCase)) is { } idArg ? idArg.Offset / 4 : (ins.ID == 0 ? 1 : 0);
                if (ints.Count <= idAt + 1 || !ints.Skip(idAt + 1).Any(people.Contains))
                    continue;
                wanted.Add(ints[idAt]);
                line($"  {what} start (event {e.ID}): 2000[{ins.ID:D2}] {string.Join(" ", ints)}");
            }
        foreach (var id in wanted)
        {
            var ev = script.Events.FirstOrDefault(x => x.ID == id) ?? common?.Events.FirstOrDefault(x => x.ID == id);
            if (ev is null)
            {
                line($"  {what} event {id}: not found");
                continue;
            }
            line($"  {what} event {id} ({(script.Events.Contains(ev) ? "map script" : "common_func")}, rest {ev.RestBehavior}):");
            for (int i = 0; i < ev.Instructions.Count; i++)
            {
                var ins = ev.Instructions[i];
                var def = defs.Get(ins.Bank, ins.ID);
                var args = def is null ? $"raw {Convert.ToHexString(ins.ArgData)}"
                    : string.Join(", ", def.Args.Select(a => $"{a.Name}={(a.Type == 6 ? BitConverter.Int32BitsToSingle((int)Emedf.Read(ins.ArgData, a)).ToString(CultureInfo.InvariantCulture) : Emedf.Read(ins.ArgData, a).ToString(CultureInfo.InvariantCulture))}"));
                var passed = ev.Parameters.Where(p => p.InstructionIndex == i).Select(p => $"@{p.TargetStartByte}<-X{p.SourceStartByte}").ToList();
                line($"    {i,3} {ins.Bank}[{ins.ID:D2}] {def?.Name ?? "?"}({args}){(passed.Count > 0 ? " " + string.Join(" ", passed) : "")}");
            }
        }
    }

    /// <summary>A talk script state by state: entry/exit/while commands, and each condition with its commands and target.</summary>
    private static void DumpTalk(ESD esd, string what, Action<string> line)
    {
        line($"  {what}:");
        foreach (var (groupId, group) in esd.StateGroups.OrderBy(g => g.Key))
            foreach (var (stateId, state) in group.OrderBy(s => s.Key))
            {
                line($"    group {groupId} state {stateId}");
                foreach (var (name, list) in new[] { ("entry", state.EntryCommands), ("exit", state.ExitCommands), ("while", state.WhileCommands) })
                    foreach (var c in list)
                        line($"      {name} {Command(c)}");
                foreach (var cond in state.Conditions)
                    Condition(cond, 0);
            }

        void Condition(ESD.Condition c, int depth)
        {
            line($"      {new string(' ', depth * 2)}if {Expr(c.Evaluator)}{(c.TargetState is { } t ? $" -> {t}" : "")}");
            foreach (var cmd in c.PassCommands)
                line($"      {new string(' ', depth * 2)}  do {Command(cmd)}");
            foreach (var sub in c.Subconditions)
                Condition(sub, depth + 1);
        }
        string Command(ESD.CommandCall c) => $"{c.CommandBank}:{c.CommandID}({string.Join(", ", c.Arguments.Select(Expr))})";
    }

    private static string Expr(byte[] bytes)
    {
        try { return string.Join(" ; ", TalkPort.Parse(bytes).Select(Node)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "raw " + Convert.ToHexString(bytes); }
    }

    private static string Node(TalkPort.Node n) => n switch
    {
        TalkPort.Lit l => l.Value.ToString(CultureInfo.InvariantCulture),
        TalkPort.Raw r => r.Bytes.Length == 5 && r.Bytes[0] == 0xA5 ? BitConverter.ToSingle(r.Bytes, 1).ToString(CultureInfo.InvariantCulture) : "raw" + Convert.ToHexString(r.Bytes),
        TalkPort.Call c => $"f{Node(c.Func)}({string.Join(", ", c.Args.Select(Node))})",
        TalkPort.Op o => o.Code switch
        {
            0xA1 or 0xA6 when o.Kids.Count == 1 => Node(o.Kids[0]),
            0xB8 => $"arg[{string.Join(",", o.Kids.Select(Node))}]",
            _ => $"op{o.Code:X2}({string.Join(", ", o.Kids.Select(Node))})",
        },
        _ => "?",
    };
}
