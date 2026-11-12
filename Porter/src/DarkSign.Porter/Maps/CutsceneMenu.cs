using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The F1 menu's "Cutscenes" tab: one event per converted DS3 cutscene in common.emevd, the way the boss arena warps
/// work (<see cref="BossArenas.AddWarps"/>). The menu sets the cutscene's request flag; the event clears it, warps the
/// player to the cutscene's map (its start point), waits until that map is loaded (3[30]) and the player stands in it,
/// and plays the cutscene with Elden Ring's "Play Cutscene to Player and Warp" (2002[11], back to the start point). A cutscene is placed against its map (CutsceneMapIdParam
/// PlayMapId), so the warp first means the map, its parts and its sound are loaded when it starts.
/// <para>Flags 1039292300-2399 and events 1039291300-1399 in the RunEvents block (2000-2999: reset when a map loads,
/// right for a one-shot request). darksign_cutscenes.tsv lists them for the menu.</para>
/// </summary>
public static class CutsceneMenu
{
    public const string ListFile = "darksign_cutscenes.tsv";
    public const int MaxCutscenes = 100;
    public static uint RequestFlag(int index) => RunEvents.FlagBlock + 2300 + (uint)index;
    public static uint EventId(int index) => RunEvents.FlagBlock + 1300 + (uint)index;
    public static bool IsMenuEvent(long id) => id >= RunEvents.FlagBlock + 1300 && id < RunEvents.FlagBlock + 1300 + MaxCutscenes;

    private const byte On = 1, Off = 0, EventFlagType = 0;
    private const uint Player = 10000;

    /// <param name="Label">What the menu shows: the characters in it, else the cutscene's number.</param>
    public sealed record Entry(int Index, string Ds3Name, string ErName, int ErId, MapId Map, string MapName, uint StartEntity, string Label, double Seconds);

    /// <summary>The menu's entries: the converted cutscenes in map and number order, each with its map's start point.</summary>
    /// <param name="names">A DS3 character model's name (c6020 -> Sister Friede), or null.</param>
    public static List<Entry> Entries(IEnumerable<CutscenePort.Info> infos, IReadOnlyDictionary<MapId, (string Name, uint StartEntity)> maps,
        Func<string, string?> names)
    {
        var list = new List<Entry>();
        foreach (var info in infos.OrderBy(i => i.ErName, StringComparer.Ordinal))
        {
            if (list.Count >= MaxCutscenes || !maps.TryGetValue(info.Slot, out var map) || map.StartEntity == 0)
                continue;
            // Named characters first (Sister Friede), then models with no name.
            var people = info.Characters.Select(c => (Name: names(c), Model: c)).OrderBy(x => x.Name is null).Select(x => x.Name ?? x.Model).Distinct().ToList();
            var label = people.Count > 0 ? string.Join(", ", people) : $"cutscene {info.Ds3Name[^4..]}";
            list.Add(new Entry(list.Count, info.Ds3Name, info.ErName, CutscenePort.IdOf(info.ErName)!.Value, info.Slot, map.Name, map.StartEntity, label, info.Seconds));
        }
        return list;
    }

    /// <summary>Adds (or replaces) one event per entry in common.emevd and starts each from the constructor.</summary>
    public static void AddEvents(EMEVD common, IReadOnlyList<Entry> entries)
    {
        common.Events.RemoveAll(e => IsMenuEvent(e.ID));
        var constructor = common.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
        {
            constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
            common.Events.Insert(0, constructor);
        }
        constructor.Instructions.RemoveAll(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 && IsMenuEvent(BitConverter.ToUInt32(i.ArgData, 4)));
        foreach (var c in entries)
        {
            if (c.Index < 0 || c.Index >= MaxCutscenes)
                throw new ArgumentOutOfRangeException(nameof(entries), $"cutscene index {c.Index}");
            var m = c.Map;
            var e = new EMEVD.Event(EventId(c.Index), EMEVD.Event.RestBehaviorType.Default);
            e.Instructions.Add(RunEvents.Make(1003, 0, On, EventFlagType, RequestFlag(c.Index)));
            e.Instructions.Add(SetFlag(RequestFlag(c.Index), Off));
            e.Instructions.Add(SetFlag(RunEvents.Ds3RunActive, On));
            e.Instructions.Add(RunEvents.Make(2003, 14, (byte)m.Area, (byte)m.Block, (byte)m.Region, (byte)m.Index, c.StartEntity, 0));
            // Once the map is loaded and the player stands in it.
            e.Instructions.Add(RunEvents.Make(1001, 0, 3f));
            e.Instructions.Add(RunEvents.Make(3, 30, (sbyte)0, (byte)m.Area, (byte)m.Block, (byte)m.Region, (byte)m.Index));
            e.Instructions.Add(RunEvents.Make(3, 8, (sbyte)0, On, (sbyte)m.Area, (sbyte)m.Block, (sbyte)m.Region, (sbyte)m.Index));
            e.Instructions.Add(RunEvents.Make(1001, 0, 2f));
            // Play Cutscene to Player and Warp, the form Elden Ring's own map scripts (and the ported arena scripts, which
            // did start their cutscenes in the 1.0.417 playtest) use: cutscene ID, playback method 0, the area entity
            // and map the player stands at afterwards (the start point), the player, 0, false.
            e.Instructions.Add(RunEvents.Make(2002, 11, c.ErId, 0u, c.StartEntity, CutscenePort.PackedMap(m), Player, 0, (byte)0));
            e.Instructions.Add(RunEvents.Make(1000, 4, (byte)1));
            common.Events.Add(e);
            constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, EventId(c.Index), 0u));
        }
    }

    private static EMEVD.Instruction SetFlag(uint flag, byte state) => RunEvents.Make(2003, 66, EventFlagType, flag, state);

    /// <summary>darksign_cutscenes.tsv: one line per cutscene, read by the F1 menu's "Cutscenes" tab.</summary>
    public static IEnumerable<string> ListLines(IEnumerable<Entry> entries) =>
        new[] { "# DS3 cutscene\tElden Ring cutscene\tmap name\tlabel\tseconds\tplay request flag (written by DarkSignPorter port-maps)" }
            .Concat(entries.Select(c => $"{c.Ds3Name}\t{c.ErName}\t{Clean(c.MapName)}\t{Clean(c.Label)}\t{c.Seconds:0}\t{RequestFlag(c.Index)}"));

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
