using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The F1 menu's "Cutscenes" tab: two events per converted DS3 cutscene in common.emevd. The menu sets the cutscene's
/// request flag; the request event clears it, marks the cutscene as pending (a saved flag) and warps the player to
/// the cutscene's map (its start point). The play event, started from the constructor on every load, waits for the
/// pending flag, for that map to be loaded (3[30]) and the player to stand in it (3[08]), clears the flag and plays
/// the cutscene with Elden Ring's "Play Cutscene to Player and Warp" (2002[11], back to the start point).
/// <para>Two events because one event that warped and then waited never played anything (1.0.442: "it just teleports
/// me"): whether an event of common.emevd keeps running across the warp's map load or starts over, the play event
/// sees the saved pending flag after the load and plays. A cutscene is placed against its map (CutsceneMapIdParam
/// PlayMapId), so the map, its parts and its sound are loaded when it starts.</para>
/// <para>Flags: requests 1039292300-2399 and events 1039291300-1399 in the RunEvents block (2000-2999: reset when a
/// map loads, right for a one-shot request); pending 1039290300-0399 (0-999: saved); play events 1039291700-1799.
/// darksign_cutscenes.tsv lists them for the menu.</para>
/// </summary>
public static class CutsceneMenu
{
    public const string ListFile = "darksign_cutscenes.tsv";
    public const int MaxCutscenes = 100;
    public static uint RequestFlag(int index) => RunEvents.FlagBlock + 2300 + (uint)index;
    public static uint EventId(int index) => RunEvents.FlagBlock + 1300 + (uint)index;
    /// <summary>On from the request until the play event starts the cutscene (saved, so it outlives the warp's load).</summary>
    public static uint PendingFlag(int index) => RunEvents.FlagBlock + 300 + (uint)index;
    public static uint PlayEventId(int index) => RunEvents.FlagBlock + 1700 + (uint)index;
    public static bool IsMenuEvent(long id) => id >= RunEvents.FlagBlock + 1300 && id < RunEvents.FlagBlock + 1300 + MaxCutscenes
                                               || id >= RunEvents.FlagBlock + 1700 && id < RunEvents.FlagBlock + 1700 + MaxCutscenes;

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
            // The request: mark the cutscene pending and warp to its map.
            var request = new EMEVD.Event(EventId(c.Index), EMEVD.Event.RestBehaviorType.Default);
            request.Instructions.Add(RunEvents.Make(1003, 0, On, EventFlagType, RequestFlag(c.Index)));
            request.Instructions.Add(SetFlag(RequestFlag(c.Index), Off));
            request.Instructions.Add(SetFlag(RunEvents.Ds3RunActive, On));
            request.Instructions.Add(SetFlag(PendingFlag(c.Index), On));
            request.Instructions.Add(RunEvents.Make(2003, 14, (byte)m.Area, (byte)m.Block, (byte)m.Region, (byte)m.Index, c.StartEntity, 0));
            request.Instructions.Add(RunEvents.Make(1000, 4, (byte)1));
            common.Events.Add(request);
            constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, EventId(c.Index), 0u));

            // The play: once the pending cutscene's map is loaded and the player stands in it.
            var play = new EMEVD.Event(PlayEventId(c.Index), EMEVD.Event.RestBehaviorType.Default);
            play.Instructions.Add(RunEvents.Make(1003, 0, On, EventFlagType, PendingFlag(c.Index)));
            play.Instructions.Add(RunEvents.Make(3, 30, (sbyte)0, (byte)m.Area, (byte)m.Block, (byte)m.Region, (byte)m.Index));
            play.Instructions.Add(RunEvents.Make(3, 8, (sbyte)0, On, (sbyte)m.Area, (sbyte)m.Block, (sbyte)m.Region, (sbyte)m.Index));
            play.Instructions.Add(RunEvents.Make(1001, 0, 2f));
            play.Instructions.Add(SetFlag(PendingFlag(c.Index), Off));
            // Play Cutscene to Player and Warp, the form Elden Ring's own map scripts (and the ported arena scripts, which
            // did start their cutscenes in the 1.0.417 playtest) use: cutscene ID, playback method 0, the area entity
            // and map the player stands at afterwards (the start point), the player, 0, false.
            play.Instructions.Add(RunEvents.Make(2002, 11, c.ErId, 0u, c.StartEntity, CutscenePort.PackedMap(m), Player, 0, (byte)0));
            play.Instructions.Add(RunEvents.Make(1000, 4, (byte)1));
            common.Events.Add(play);
            constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, PlayEventId(c.Index), 0u));
        }
    }

    private static EMEVD.Instruction SetFlag(uint flag, byte state) => RunEvents.Make(2003, 66, EventFlagType, flag, state);

    /// <summary>darksign_cutscenes.tsv: one line per cutscene, read by the F1 menu's "Cutscenes" tab.</summary>
    public static IEnumerable<string> ListLines(IEnumerable<Entry> entries) =>
        new[] { "# DS3 cutscene\tElden Ring cutscene\tmap name\tlabel\tseconds\tplay request flag (written by DarkSignPorter port-maps)" }
            .Concat(entries.Select(c => $"{c.Ds3Name}\t{c.ErName}\t{Clean(c.MapName)}\t{Clean(c.Label)}\t{c.Seconds:0}\t{RequestFlag(c.Index)}"));

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
