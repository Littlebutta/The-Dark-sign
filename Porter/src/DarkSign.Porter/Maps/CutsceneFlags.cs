using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The event flags that keep a converted map script's cutscene from playing a second time, for the F1 menu's "Clear
/// played cutscenes" button. Read from the converted Elden Ring scripts, per cutscene the scripts play (2002[*]):
/// <list type="bullet">
/// <item>A flag the cutscene's event both sets ON (2003[66]) and checks (any other instruction naming it): DS3's
/// "seen" flags, such as Vordt's first encounter 13000801, which his intro event reads at the start and sets after the
/// cutscene. A flag the event only checks (the boss's defeated flag, set by another event) is left alone.</item>
/// <item>The event's own flag (event ID + slot): a map event that ends sets it, and an event whose flag is on does not
/// run again.</item>
/// </list>
/// A cutscene ID or flag passed in as an event parameter is read from each place that starts the event (2000[00]
/// Initialize Event, 2000[06] Initialize Common Event). Common events' own IDs are shared by all their starts, so they
/// are left out.
/// </summary>
public static class CutsceneFlags
{
    private const int PlayBank = 2002, FlowBank = 2000, InitEvent = 0, InitCommon = 6, FlagBank = 2003, SetFlag = 66;

    private sealed record Start(int Slot, byte[]? Args, bool Common);

    /// <summary>The flags per Elden Ring cutscene ID (only cutscenes some script plays are listed).</summary>
    /// <param name="defs">Elden Ring's instruction definitions (which arguments are event flags).</param>
    /// <param name="scripts">The converted map scripts, common_func included.</param>
    /// <param name="skip">Events to leave out (the F1 menu's own).</param>
    public static Dictionary<int, SortedSet<uint>> Played(Emedf defs, IEnumerable<EMEVD> scripts, Func<long, bool>? skip = null)
    {
        var list = scripts.ToList();
        var starts = new Dictionary<long, List<Start>>();
        foreach (var ev in list.SelectMany(s => s.Events))
            foreach (var ins in ev.Instructions)
                if (ins.Bank == FlowBank && ins.ID is InitEvent or InitCommon && ins.ArgData.Length >= 8)
                {
                    long id = BitConverter.ToUInt32(ins.ArgData, 4);
                    if (!starts.TryGetValue(id, out var l))
                        starts[id] = l = [];
                    l.Add(new Start(BitConverter.ToInt32(ins.ArgData, 0), ins.ArgData[8..], ins.ID == InitCommon));
                }

        var result = new Dictionary<int, SortedSet<uint>>();
        foreach (var ev in list.SelectMany(s => s.Events))
        {
            if (skip?.Invoke(ev.ID) == true || !ev.Instructions.Any(i => i.Bank == PlayBank))
                continue;
            // An event nothing starts (the constructors) runs once, with no parameters.
            var instances = starts.TryGetValue(ev.ID, out var s) ? s : [new Start(0, null, false)];
            foreach (var start in instances)
            {
                var cutscenes = new HashSet<int>();
                var setOn = new HashSet<uint>();
                var named = new HashSet<uint>();
                for (int i = 0; i < ev.Instructions.Count; i++)
                {
                    var ins = ev.Instructions[i];
                    if (ins.Bank == PlayBank)
                    {
                        if (Value(ev, i, ins, 0, start.Args) is { } c and > 0)
                            cutscenes.Add((int)c);
                        continue;
                    }
                    if (ins.Bank == FlagBank && ins.ID == SetFlag && ins.ArgData.Length >= 9)
                    {
                        // Event flags only (type 0), switched on.
                        if (ins.ArgData[0] == 0 && Value(ev, i, ins, 8, start.Args, 1) == 1 && Value(ev, i, ins, 4, start.Args) is { } f and > 0)
                            setOn.Add((uint)f);
                        continue;
                    }
                    if (defs.Get(ins.Bank, ins.ID) is not { } def)
                        continue;
                    foreach (var arg in def.Args)
                        if (arg.Size == 4 && EventPort.KindOf(arg.Name) is EventPort.Kind.Flag && Value(ev, i, ins, arg.Offset, start.Args) is { } f and > 0)
                            named.Add((uint)f);
                }
                if (cutscenes.Count == 0)
                    continue;
                var flags = setOn.Where(named.Contains).ToHashSet();
                if (!start.Common && ev.ID > 0)
                    flags.Add((uint)(ev.ID + Math.Max(0, start.Slot)));
                foreach (var c in cutscenes)
                {
                    if (!result.TryGetValue(c, out var set))
                        result[c] = set = [];
                    set.UnionWith(flags);
                }
            }
        }
        return result;
    }

    /// <summary>An argument's value: the instruction's own bytes, or the starting instruction's parameter bytes when
    /// the event takes it as a parameter (null when that start doesn't pass it).</summary>
    private static long? Value(EMEVD.Event ev, int index, EMEVD.Instruction ins, int offset, byte[]? args, int size = 4)
    {
        var p = ev.Parameters.FirstOrDefault(p => p.InstructionIndex == index && p.TargetStartByte == offset);
        if (p is null)
            return offset + size <= ins.ArgData.Length ? Read(ins.ArgData, offset, size) : null;
        if (args is null || p.SourceStartByte + size > args.Length)
            return null;
        return Read(args, (int)p.SourceStartByte, size);
    }

    private static long Read(byte[] data, int offset, int size) => size == 1 ? data[offset] : BitConverter.ToUInt32(data, offset);
}
