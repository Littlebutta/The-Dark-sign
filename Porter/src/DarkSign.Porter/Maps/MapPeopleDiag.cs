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
            var modScript = ctx.ModPath("event", $"{slot.Er}.emevd.dcx");
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
}
