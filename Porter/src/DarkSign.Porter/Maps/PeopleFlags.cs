using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// darksign_people_flags.txt (RULE 4): every event flag the built mod's scripts use for the placed people, read by
/// dksign_testmenu.dll (peopleflags.c), which logs each flag's state once a character is loaded and every change after
/// that ("people: flag N on (who, what)"). Written from the built mod files, so the numbers are the ones the game uses.
/// <para>
/// Why: on 1.0.640 Sirris/Eygon at Firelink were on DS3's friendly team (26, PR #288) and still turned on the player.
/// DS3 makes a person hostile only through saved flags (quest-state ranges set inside their events, the hostile flag
/// the common hostility event 20006000 sets when they're hit or a talk flag is on), and a save's flags can't be read
/// from outside the game. This list lets one load show which flag is on and when it turns on.
/// </para>
/// Covered: each event the map script starts with a person's entity among its arguments (map script or common_func),
/// every flag argument of its lines, literal or passed in from the start line; flag ranges up to 64 flags are listed
/// flag by flag.
/// </summary>
public static class PeopleFlags
{
    public const string ListFile = "darksign_people_flags.txt";
    private const int MaxRange = 64;

    /// <summary>Lines "&lt;flag&gt;\t&lt;who, event, line, argument&gt;" for one built mod map, each flag once.</summary>
    public static List<string> Lines(MSBE msb, EMEVD script, EMEVD? common, Emedf defs, string map, Func<int, string>? rowName = null)
    {
        var people = new Dictionary<long, string>();
        foreach (var e in msb.Parts.Enemies.Where(e => e.EntityID > 0 && (EnemyPort.People.IsHuman(e.ModelName) || e.TalkID > 0)))
        {
            var row = rowName?.Invoke(e.NPCParamID);
            people.TryAdd(e.EntityID, string.IsNullOrWhiteSpace(row) ? $"{e.Name} ({e.EntityID})" : $"{e.Name} {row.Replace("[DS3 person] ", "")} ({e.EntityID})");
        }
        var seen = new HashSet<long>();
        var lines = new List<string>();
        void Add(long flag, string what)
        {
            if (flag > 0 && seen.Add(flag))
                lines.Add($"{flag}\t{what.Replace('\t', ' ')}");
        }

        foreach (var starter in script.Events)
            foreach (var start in starter.Instructions.Where(x => x.Bank == 2000 && x.ID is 0 or 6))
            {
                var data = start.ArgData;
                var ints = Enumerable.Range(0, data.Length / 4).Select(k => BitConverter.ToInt32(data, k * 4)).ToList();
                // DS3's 2000[06] starts with the event ID; Elden Ring's with a slot, then the event ID.
                int idAt = defs.Get(2000, start.ID)?.Args.FirstOrDefault(a => a.Name.Contains("Event ID", StringComparison.OrdinalIgnoreCase)) is { } idArg
                    ? idArg.Offset / 4 : (start.ID == 0 ? 1 : 0);
                if (ints.Count <= idAt + 1)
                    continue;
                var who = ints.Skip(idAt + 1).Select(v => (long)v).FirstOrDefault(people.ContainsKey);
                if (who == 0)
                    continue;
                long id = ints[idAt];
                var ev = script.Events.FirstOrDefault(x => x.ID == id) ?? common?.Events.FirstOrDefault(x => x.ID == id);
                if (ev is null)
                    continue;
                var param = data.AsSpan((idAt + 1) * 4).ToArray();
                foreach (var (flag, what) in Flags(ev, param, defs))
                    Add(flag, $"{people[who]}, {map} event {id} {what}");
            }
        return lines;
    }

    /// <summary>Every flag argument of an event's lines, with passed-in values read from the start line's parameters.</summary>
    private static IEnumerable<(long Flag, string What)> Flags(EMEVD.Event ev, byte[] param, Emedf defs)
    {
        for (int i = 0; i < ev.Instructions.Count; i++)
        {
            var ins = ev.Instructions[i];
            if (defs.Get(ins.Bank, ins.ID) is not { } def)
                continue;
            long rangeStart = -1;
            foreach (var a in def.Args)
            {
                var kind = EventPort.KindOf(a.Name);
                if (kind is not (EventPort.Kind.Flag or EventPort.Kind.FlagRangeStart or EventPort.Kind.FlagRangeEnd) || a.Size != 4)
                    continue;
                long value = Emedf.Read(ins.ArgData, a);
                var passed = ev.Parameters.FirstOrDefault(p => p.InstructionIndex == i && p.TargetStartByte == a.Offset);
                if (passed is not null && passed.SourceStartByte + 4 <= param.Length)
                    value = BitConverter.ToUInt32(param, (int)passed.SourceStartByte);
                var what = $"line {i} {def.Name}: {a.Name}{(passed is not null ? $" (X{passed.SourceStartByte})" : "")}";
                if (kind == EventPort.Kind.FlagRangeStart)
                    rangeStart = value;
                else if (kind == EventPort.Kind.FlagRangeEnd && rangeStart > 0 && value >= rangeStart && value - rangeStart < MaxRange)
                {
                    for (long f = rangeStart; f <= value; f++)
                        yield return (f, $"line {i} {def.Name}: range {rangeStart}-{value}");
                    rangeStart = -1;
                }
                else
                    yield return (value, what);
            }
            if (rangeStart > 0)
                yield return (rangeStart, $"line {i} {def.Name}: range start");
        }
    }

    /// <summary>Writes the list for every built map of <paramref name="maps"/>; returns how many flags it lists.</summary>
    public static int Write(PorterContext ctx, IEnumerable<MapId> maps, Emedf erDefs, Func<int, string>? rowName = null)
    {
        var commonPath = ctx.ModPath("event", "common_func.emevd.dcx");
        var common = File.Exists(commonPath) ? EMEVD.Read(commonPath) : null;
        var all = new List<string> { "# ER flag\twho, event, line, argument (written by DarkSignPorter port-maps; read by dksign_testmenu.dll, which logs people: lines)" };
        var seen = new HashSet<string>();
        foreach (var map in maps.Distinct())
        {
            var msbPath = ctx.ModPath("map", "mapstudio", $"{map}.msb.dcx");
            var scriptPath = ctx.ModPath("event", $"{map}.emevd.dcx");
            if (!File.Exists(msbPath) || !File.Exists(scriptPath))
                continue;
            foreach (var line in Lines(MSBE.Read(msbPath), EMEVD.Read(scriptPath), common, erDefs, map.ToString(), rowName))
                if (seen.Add(line[..line.IndexOf('\t')]))
                    all.Add(line);
        }
        File.WriteAllLines(ctx.ModPath(ListFile), all);
        return all.Count - 1;
    }
}
