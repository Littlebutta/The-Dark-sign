using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's new-game setup in Elden Ring. DS3's common.emevd event 50 (which the game starts on every load, like event 0)
/// starts the events that put a new game's flags in their first state; event 700 among them sets 50006020 ("the Fire
/// Keeper has no eyes yet"), 9215, 70000125/128/129, the class flags 74000587/74000465, 50006162/163, 73501010-50 and
/// the game cycle flags 50-56, once per save (guarded by its own event flag). The port only carried the common_func
/// events the maps start, so none of these flags was ever on: Firelink's music event 14005450 read 50006020 off as
/// "eyes given" and played the Fire Keeper's theme instead of the shrine's (Devin's 2026-11-20 playtest, read from
/// m40's talk and event scripts and the mod's flag table on his PC).
/// <para>Every event DS3's event 50 starts that only sets flags (no waits, warps, items or cutscenes; no values passed
/// in) is converted with the maps' flag table and added to Elden Ring's common.emevd at <see cref="FirstEventId"/>+,
/// started from event 0. An event that runs once per save in DS3 (an "END IF this event's flag is on" guard) gets a
/// saved flag of its own instead (<see cref="FirstDoneFlag"/>+, JortPob's saved range), set as it starts, because the
/// event-ID flag of a ported event isn't known to be saved. Off with DARKSIGN_DS3_SETUP=0 (launcher fallback
/// "map-setup" B).</para>
/// </summary>
public static class Ds3Setup
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DARKSIGN_DS3_SETUP") != "0";

    public const string Ds3Common = "/event/common.emevd.dcx";
    /// <summary>DS3's common event that starts the setup events on every load.</summary>
    public const long LoadEvent = 50;
    public const uint FirstEventId = RunEvents.FlagBlock + 1103;
    public const int MaxEvents = 7; // 1103-1109: 1110+ are the bonfire travel events
    /// <summary>Saved flags (JortPob's 0-999) that stand for "this setup event has run in this save".</summary>
    public const uint FirstDoneFlag = RunEvents.FlagBlock + 150;

    /// <summary>The DS3 events <see cref="LoadEvent"/> starts that only set flags, in the order it starts them.</summary>
    public static List<EMEVD.Event> SetupEvents(EMEVD ds3Common)
    {
        var byId = ds3Common.Events.GroupBy(e => e.ID).ToDictionary(g => g.Key, g => g.First());
        if (!byId.TryGetValue(LoadEvent, out var load))
            return [];
        var result = new List<EMEVD.Event>();
        foreach (var ins in load.Instructions.Where(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8))
        {
            long id = BitConverter.ToUInt32(ins.ArgData, 4);
            if (byId.TryGetValue(id, out var e) && IsSetup(e) && result.All(r => r.ID != id))
                result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// Only flags: Set Event Flag lines (at least one), conditions on flags, class, game cycle, gender or world type,
    /// and flow (skips, ends, gotos, condition groups; no waits); nothing passed in. DS3's 2004[60] "Set Character Remaining Yoel Levels" is let
    /// through (it has no Elden Ring form and becomes a no-op).
    /// </summary>
    public static bool IsSetup(EMEVD.Event e)
    {
        if (e.Parameters.Count > 0 || !e.Instructions.Any(i => i.Bank == 2003 && i.ID is 2 or 22 or 62 or 63))
            return false;
        foreach (var i in e.Instructions)
        {
            bool ok = i.Bank switch
            {
                0 => true,                                   // condition groups
                1000 => i.ID is not (0 or 9),                // skips, ends and gotos; not waits (0: condition group, 9: network)
                1003 => i.ID != 0,                           // skip/end/goto on a flag or world type; not WAIT For Event Flag
                3 => i.ID is 0 or 1 or 17 or 26 or 30,      // IF flag, flag range, game cycle, not own world, gender (ds3-common)
                4 => i.ID == 11,                             // IF Player's Class
                2003 => i.ID is 2 or 22 or 62 or 63,         // DS3's Set Event Flag, Batch Set, and their network-connected forms
                2004 => i.ID == 60,                          // Yoel levels
                _ => false,
            };
            // A condition into the main group (0) waits for it.
            if (!ok || i.Bank is 0 or 3 or 4 && i.ArgData.Length > 0 && i.ArgData[0] == 0)
                return false;
        }
        return true;
    }

    /// <summary>Whether a DS3 line is "END IF this event's own flag is on" (1003[02], flag type 2): once per save.</summary>
    public static bool IsOwnFlagGuard(EMEVD.Instruction i) =>
        i.Bank == 1003 && i.ID == 2 && i.ArgData.Length >= 3 && i.ArgData[2] == 2;

    /// <summary>
    /// Adds the converted setup events to Elden Ring's common.emevd and starts them from event 0. Returns the report
    /// line. <paramref name="port"/> converts with the maps' flag table.
    /// </summary>
    public static string AddTo(EMEVD common, EMEVD ds3Common, EventPort port)
    {
        common.Events.RemoveAll(e => IsOurs(e.ID));
        foreach (var c in common.Events.Where(e => e.ID == 0))
            c.Instructions.RemoveAll(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 && IsOurs(BitConverter.ToUInt32(i.ArgData, 4)));
        var setup = SetupEvents(ds3Common);
        if (setup.Count == 0)
            return "DS3 setup: no flag-only events started by DS3's common event 50";
        var constructor = common.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
            common.Events.Insert(0, constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        var added = new List<string>();
        var left = new List<string>();
        int n = 0;
        foreach (var e in setup)
        {
            if (n >= MaxEvents)
            {
                left.Add($"{e.ID} (no event number left)");
                continue;
            }
            uint id = FirstEventId + (uint)n, done = FirstDoneFlag + (uint)n;
            var source = new EMEVD(EMEVD.Game.DarkSouls3);
            source.Events.Add(e);
            var converted = port.Convert(source, common.Format, _ => id).Events.FirstOrDefault();
            if (converted is null)
            {
                left.Add($"{e.ID} (a line has no Elden Ring form)");
                continue;
            }
            bool once = e.Instructions.Any(IsOwnFlagGuard);
            if (once)
            {
                // The event's own-flag guard becomes a saved flag of ours: end if it is on, else turn it on and go on.
                for (int i = 0; i < e.Instructions.Count; i++)
                    if (IsOwnFlagGuard(e.Instructions[i]))
                        converted.Instructions[i] = EventPort.NoOp();
                converted.Instructions.InsertRange(0, [EndIfFlagOn(done), SetFlag(done)]);
            }
            common.Events.Add(converted);
            constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, id, 0u));
            added.Add($"{e.ID} -> {id}" + (once ? $" (once per save, flag {done})" : ""));
            n++;
        }
        return $"DS3 setup: {added.Count} of DS3's new-game events added to common.emevd ({string.Join(", ", added)})"
               + (left.Count > 0 ? $"; left out: {string.Join(", ", left)}" : "");
    }

    private static bool IsOurs(long id) => id >= FirstEventId && id < FirstEventId + MaxEvents;

    private static EMEVD.Instruction EndIfFlagOn(uint flag) => RunEvents.Make(1003, 2, (byte)0, (byte)1, (byte)0, flag);
    private static EMEVD.Instruction SetFlag(uint flag) => RunEvents.Make(2003, 66, (byte)0, flag, (byte)1);
}
