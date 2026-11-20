using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "map-diag object-events [DS3 map ID...]": what drives each DS3 object of a map (RULE 4), written to
/// Work\reports\map-diag\object-events.txt. Read-only: it reads DS3's map files and scripts and the last port's
/// object report. For lifts and doors that are moved by a lever or a script rather than by the player standing on
/// them, this shows which events name them and whether the port keeps those events.
/// <list type="bullet">
/// <item>Each object with an entity ID: its part name, model, entity ID, and DS3's ObjAct map events that name it
/// (the lever or door the player uses, its ObjAct param row and flag).</item>
/// <item>The last port's row for the object in Work\reports\object-port.tsv (whether it moves in Elden Ring).</item>
/// <item>Every DS3 event that names one of those entities, directly or through the values a start line passes in,
/// written out line by line, then converted on its own: kept or left out, and which lines had no Elden Ring form.</item>
/// </list>
/// </summary>
public static class MapObjectEventsDiag
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var wanted = args.Select(MapId.Parse).ToHashSet();
        var slots = MapSlots.All.Where(s => !s.Probe && (wanted.Count == 0 || wanted.Contains(s.Ds3))).ToList();
        var dir = ctx.WorkPath("reports", "map-diag");
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        void Line(string text) => report.AppendLine(text);

        var defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var erDefs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("er-common")));
        var portReport = ctx.WorkPath("reports", "object-port.tsv");
        var portRows = File.Exists(portReport) ? File.ReadAllLines(portReport) : [];
        Line(portRows.Length == 0 ? "(no Work\\reports\\object-port.tsv: run port-maps first for the port's side)" : $"object-port.tsv header: {portRows[0]}");

        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var (scripts, common) = MapEvents.ReadDs3(ds3, MapSlots.All.Where(s => !s.Probe));
        var commonById = common?.Events.GroupBy(e => e.ID).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var ported = MapSlots.All.Where(s => !s.Probe).Select(s => (s.Ds3, s.Er)).ToList();

        foreach (var slot in slots)
        {
            var path = MapPorter.MsbPath(slot.Ds3);
            if (!ds3.Contains(path) || !scripts.TryGetValue(slot.Ds3, out var script))
            {
                Line($"=== DS3 {slot.Ds3}: no map file or script");
                continue;
            }
            var msb = MSB3.Read(ds3.Read(path));
            var objects = msb.Parts.Objects.Where(o => o.EntityID > 0).ToDictionary(o => (long)o.EntityID, o => o);
            var objActs = msb.Events.ObjActs.ToList();
            Line("");
            Line($"=== DS3 {slot.Ds3} -> {slot.Er}: {objects.Count} objects with entity IDs, {objActs.Count} ObjAct events");

            // Which events name which objects: directly, or through a start line's values (one level, map or common).
            var named = new Dictionary<(string Where, long Event), SortedSet<long>>();
            void Name(string where, long ev, long entity)
            {
                if (!named.TryGetValue((where, ev), out var set))
                    named[(where, ev)] = set = [];
                set.Add(entity);
            }
            var starts = new List<string>();
            foreach (var e in script.Events)
                for (int i = 0; i < e.Instructions.Count; i++)
                {
                    var ins = e.Instructions[i];
                    if (ins.Bank == 2000 && ins.ID is 0 or 6)
                    {
                        int head = ins.ID == 0 ? 8 : 4;
                        if (ins.ArgData.Length < head)
                            continue;
                        long callee = BitConverter.ToUInt32(ins.ArgData, head - 4);
                        var hits = new List<long>();
                        for (int o = head; o + 4 <= ins.ArgData.Length; o += 4)
                            if (objects.ContainsKey(BitConverter.ToInt32(ins.ArgData, o)))
                                hits.Add(BitConverter.ToInt32(ins.ArgData, o));
                        foreach (var h in hits)
                            Name(ins.ID == 6 ? "common_func" : slot.Ds3.ToString(), callee, h);
                        if (hits.Count > 0)
                            starts.Add($"  event {e.ID} line {i}: {MapMusicDiag.Text(e, i, defs)}");
                        continue;
                    }
                    if (defs.Get(ins.Bank, ins.ID) is not { } def)
                        continue;
                    foreach (var a in def.Args)
                        if (a.Offset + a.Size <= ins.ArgData.Length && a.Name.Contains("Entity", StringComparison.OrdinalIgnoreCase)
                            && objects.ContainsKey(Emedf.Read(ins.ArgData, a)))
                            Name(slot.Ds3.ToString(), e.ID, Emedf.Read(ins.ArgData, a));
                }

            foreach (var (id, o) in objects.OrderBy(kv => kv.Key))
            {
                var acts = objActs.Where(a => a.ObjActPartName == o.Name || a.ObjActEntityID == id).ToList();
                var events = named.Where(n => n.Value.Contains(id)).Select(n => $"{n.Key.Where} {n.Key.Event}").ToList();
                var row = portRows.FirstOrDefault(r => r.Contains(slot.Ds3.ToString()) && r.Contains(o.Name));
                Line($"  {o.Name} model {o.ModelName} entity {id}" +
                     (acts.Count > 0 ? "; ObjAct " + string.Join(" / ", acts.Select(a => $"{a.Name} (entity {a.ObjActEntityID}, param {a.ObjActParamID}, flag {a.EventFlagID}, state {a.ObjActStateType})")) : "") +
                     (events.Count > 0 ? $"; events {string.Join(", ", events)}" : "; named by no event"));
                if (row is not null)
                    Line($"      port: {row}");
            }
            Line("");
            Line("--- start lines that pass an object entity");
            starts.ForEach(Line);

            foreach (var ((where, ev), entities) in named.OrderBy(n => n.Key.Where).ThenBy(n => n.Key.Event))
            {
                bool isCommon = where == "common_func";
                var e = isCommon ? commonById.GetValueOrDefault(ev) : script.Events.FirstOrDefault(x => x.ID == ev);
                Line("");
                Line($"--- {where} event {ev} (names {string.Join(" ", entities.Select(x => objects[x].Name))})");
                if (e is null)
                {
                    Line("  (event not found)");
                    continue;
                }
                for (int i = 0; i < e.Instructions.Count; i++)
                    Line($"  {i,3}: {MapMusicDiag.Text(e, i, defs)}");
                // The same event converted on its own, as the map port would.
                var port = new EventPort(defs, erDefs, ported, new FlagMap());
                if (common is not null)
                    port.Learn(common, common: true);
                foreach (var other in scripts.Values)
                    port.Learn(other);
                var source = new EMEVD(script.Format);
                source.Events.Add(e);
                try
                {
                    var converted = port.Convert(source, EMEVD.Game.Sekiro, x => x);
                    var missed = string.Join(", ", port.Stats.Unconverted.Select(u => $"{u.Key} x{u.Value}"));
                    Line(converted.Events.Count == 0
                        ? $"  PORT: LEFT OUT (a condition or flow line has no Elden Ring form: {missed})"
                        : $"  PORT: kept, {port.Stats.NoOps} lines became no-ops" + (missed.Length > 0 ? $" ({missed})" : ""));
                    foreach (var note in port.Stats.Notes)
                        Line($"  note: {note}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Line($"  PORT: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        var file = Path.Combine(dir, "object-events.txt");
        File.WriteAllText(file, report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine($"report: {file}");
        return 0;
    }
}
