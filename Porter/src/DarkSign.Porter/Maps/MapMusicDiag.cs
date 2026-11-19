using System.Globalization;
using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "map-diag music [DS3 map ID...]": the facts behind when DS3 starts and stops its music (RULE 4), written to
/// Work\reports\map-diag\music.txt. Read-only: it reads DS3's map files and scripts.
/// <list type="bullet">
/// <item>Every Sound region of each map: type, sound ID, entity ID, shape and size, the part that activates it and
/// its child regions.</item>
/// <item>Every DS3 event with a sound instruction (bank 2010) that names music (a music region's entity, sound type
/// 6, or a value passed in), written out line by line with which values are passed in.</item>
/// <item>Every line that starts one of those events, with the values it passes.</item>
/// <item>The music plan the port makes of them (<see cref="MapMusic.Build"/>).</item>
/// </list>
/// </summary>
public static class MapMusicDiag
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
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var maps = new List<(MapSlots.Slot Slot, MSB3 Msb)>();
        var musicEntities = new HashSet<long>();
        foreach (var slot in slots)
        {
            var path = MapPorter.MsbPath(slot.Ds3);
            if (!ds3.Contains(path))
            {
                Line($"=== DS3 {slot.Ds3}: no map file");
                continue;
            }
            var msb = MSB3.Read(ds3.Read(path));
            maps.Add((slot, msb));
            Line($"=== DS3 {slot.Ds3} -> {slot.Er}: sound regions");
            foreach (var r in msb.Regions.GetEntries().OfType<MSB3.Region.Sound>())
            {
                if (MapMusic.IsMusic(r) && r.EntityID > 0)
                    musicEntities.Add(r.EntityID);
                var children = r.ChildRegionNames.Where(n => !string.IsNullOrEmpty(n)).ToList();
                Line($"  {(MapMusic.IsMusic(r) ? "MUSIC" : "     ")} {r.Name}: type {(uint)r.SoundType} sound {r.SoundID} entity {r.EntityID} {Shape(r.Shape)} " +
                     $"at {F(r.Position.X)},{F(r.Position.Y)},{F(r.Position.Z)} layer {r.MapStudioLayer:X}" +
                     (string.IsNullOrEmpty(r.ActivationPartName) ? "" : $" activated by {r.ActivationPartName}") +
                     (children.Count > 0 ? $" children [{string.Join(", ", children)}]" : ""));
            }
        }

        var (scripts, common) = MapEvents.ReadDs3(ds3, slots);
        var all = scripts.Select(kv => (Name: kv.Key.ToString(), Map: (MapId?)kv.Key, Script: kv.Value)).ToList();
        if (common is not null)
            all.Add(("common_func", null, common));

        // Events with a music line: a 2010 instruction that names a music region, sound type 6, or passes a value in.
        var musicEvents = new HashSet<long>();
        foreach (var (name, _, script) in all)
            foreach (var e in script.Events)
            {
                bool music = false;
                for (int i = 0; i < e.Instructions.Count && !music; i++)
                {
                    var ins = e.Instructions[i];
                    if (ins.Bank != 2010 || defs.Get(2010, ins.ID) is not { } def)
                        continue;
                    bool passed = e.Parameters.Any(p => p.InstructionIndex == i);
                    music = passed || def.Args.Any(a =>
                        a.Name.Contains("Entity", StringComparison.OrdinalIgnoreCase) && musicEntities.Contains(Emedf.Read(ins.ArgData, a))
                        || a.Name.Equals("Sound Type", StringComparison.OrdinalIgnoreCase) && Emedf.Read(ins.ArgData, a) == MapMusic.BgmSoundType);
                }
                if (!music)
                    continue;
                musicEvents.Add(e.ID);
                Line("");
                Line($"--- {name} event {e.ID} (rest {e.RestBehavior})");
                for (int i = 0; i < e.Instructions.Count; i++)
                    Line($"  {i,3}: {Text(e, i, defs)}");
            }

        Line("");
        Line("=== lines that start those events");
        foreach (var (name, _, script) in all)
            foreach (var e in script.Events)
                for (int i = 0; i < e.Instructions.Count; i++)
                {
                    var ins = e.Instructions[i];
                    if (ins.Bank != 2000 || ins.ID is not (0 or 6))
                        continue;
                    int head = ins.ID == 0 ? 8 : 4;
                    if (ins.ArgData.Length < head || !musicEvents.Contains(BitConverter.ToUInt32(ins.ArgData, head - 4)))
                        continue;
                    var values = new List<string>();
                    for (int o = head; o + 4 <= ins.ArgData.Length; o += 4)
                        values.Add(BitConverter.ToInt32(ins.ArgData, o).ToString(CultureInfo.InvariantCulture));
                    Line($"  {name} event {e.ID} line {i}: start {BitConverter.ToUInt32(ins.ArgData, head - 4)}" +
                         (ins.ID == 0 ? $" slot {BitConverter.ToInt32(ins.ArgData, 0)}" : "") + $" ({string.Join(", ", values)})" +
                         (e.Parameters.Any(p => p.InstructionIndex == i) ? " [values passed on]" : ""));
                }

        Line("");
        Line("=== the port's music plan");
        var plan = MapMusic.Build(maps, all.Select(s => new MapMusic.Script(s.Map, s.Script)), defs);
        Line(plan.Summary);
        foreach (var s in plan.Sources)
            Line($"  {s}");
        foreach (var n in plan.Notes)
            Line($"  note: {n}");

        // The same events as the port writes them, so the flags each line sets can be read off.
        Line("");
        Line("=== converted (Elden Ring's form; a start line's values after the event ID; @byte=Xn: filled from the event's value n)");
        var erDefs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("er-common")));
        var ported = slots.Select(s => (s.Ds3, s.Er)).ToList();
        foreach (var (name, map, script) in all)
        {
            var port = new EventPort(defs, erDefs, ported, new FlagMap()) { Music = plan, MusicMap = map };
            if (common is not null)
                port.Learn(common, common: true);
            foreach (var other in scripts.Values)
                port.Learn(other);
            var starters = script.Events.Where(e => e.Instructions.Any(i => i.Bank == 2000 && i.ID is 0 or 6
                && i.ArgData.Length >= (i.ID == 0 ? 8 : 4) && musicEvents.Contains(BitConverter.ToUInt32(i.ArgData, i.ID == 0 ? 4 : 0)))).Select(e => e.ID).ToHashSet();
            var source = new EMEVD(script.Format);
            source.Events.AddRange(script.Events.Where(e => musicEvents.Contains(e.ID) || starters.Contains(e.ID) && e.ID is not (0 or 50)));
            EMEVD converted;
            try { converted = port.Convert(source, EMEVD.Game.Sekiro, id => id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Line($"--- {name}: {ex.GetType().Name}: {ex.Message}"); continue; }
            foreach (var e in converted.Events)
            {
                Line($"--- {name} event {e.ID}");
                for (int i = 0; i < e.Instructions.Count; i++)
                {
                    var ins = e.Instructions[i];
                    bool music = ins.Bank == 2003 && ins.ID is 66 or 22;
                    bool start = ins.Bank == 2000 && ins.ID is 0 or 6;
                    if (!music && !start)
                        continue;
                    var filled = string.Join(" ", e.Parameters.Where(p => p.InstructionIndex == i).Select(p => $"@{p.TargetStartByte}=X{p.SourceStartByte}/{p.ByteCount}"));
                    var values = new List<string>();
                    for (int o = start ? 4 : 0; o + 4 <= ins.ArgData.Length; o += 4)
                        values.Add(BitConverter.ToUInt32(ins.ArgData, o).ToString(CultureInfo.InvariantCulture));
                    Line($"  {i,3}: {(music ? (ins.ID == 66 ? "Set Event Flag" : "Batch Set Event Flags") : "start")} {ins.Bank}[{ins.ID:D2}] ({string.Join(", ", values)}) {filled}");
                }
            }
            foreach (var note in port.Stats.Notes)
                Line($"  note: {note}");
        }
        Line("");
        Line("=== lines each slot's event 0 begins with");
        foreach (var slot in slots)
            Line($"  {slot.Er}: {string.Join("; ", plan.LoadLines(slot.Er).Select(i => $"flag {BitConverter.ToUInt32(i.ArgData, 4)} {(i.ArgData[8] != 0 ? "on" : "off")}"))}");

        var file = Path.Combine(dir, "music.txt");
        File.WriteAllText(file, report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine($"report: {file}");
        return 0;
    }

    private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Shape(MSB.Shape shape) => shape switch
    {
        MSB.Shape.Point => "point",
        MSB.Shape.Sphere s => $"sphere r{F(s.Radius)}",
        MSB.Shape.Cylinder c => $"cylinder r{F(c.Radius)} h{F(c.Height)}",
        MSB.Shape.Box b => $"box {F(b.Width)}x{F(b.Height)}x{F(b.Depth)}",
        MSB.Shape.Circle c => $"circle r{F(c.Radius)}",
        MSB.Shape.Rectangle r => $"rectangle {F(r.Width)}x{F(r.Depth)}",
        _ => shape.GetType().Name,
    };

    /// <summary>One script line: its DS3 name and each argument's value (X&lt;byte&gt;: passed in from that byte of the event's values).</summary>
    public static string Text(EMEVD.Event e, int index, Emedf defs)
    {
        var ins = e.Instructions[index];
        var passed = e.Parameters.Where(p => p.InstructionIndex == index).ToDictionary(p => p.TargetStartByte, p => p.SourceStartByte);
        if (defs.Get(ins.Bank, ins.ID) is not { } def)
            return $"{ins.Bank}[{ins.ID:D2}] {System.Convert.ToHexString(ins.ArgData)}" + (passed.Count > 0 ? $" passed {string.Join(",", passed.Select(p => $"@{p.Key}=X{p.Value}"))}" : "");
        var parts = new List<string>();
        foreach (var a in def.Args)
        {
            if (a.Offset + a.Size > ins.ArgData.Length)
                break;
            parts.Add(passed.TryGetValue(a.Offset, out var from) ? $"{a.Name}=X{from}"
                : a.Type == 6 ? $"{a.Name}={BitConverter.ToSingle(ins.ArgData, a.Offset).ToString("0.###", CultureInfo.InvariantCulture)}"
                : $"{a.Name}={Emedf.Read(ins.ArgData, a)}");
        }
        // Start lines carry the values after the declared arguments.
        if (ins.Bank == 2000 && ins.ID is 0 or 6)
        {
            int head = ins.ID == 0 ? 8 : 4;
            for (int o = head; o + 4 <= ins.ArgData.Length; o += 4)
                parts.Add(passed.TryGetValue(o, out var from) ? $"X{from}" : BitConverter.ToInt32(ins.ArgData, o).ToString(CultureInfo.InvariantCulture));
        }
        return $"{ins.Bank}[{ins.ID:D2}] {def.Name}({string.Join(", ", parts)})";
    }
}
