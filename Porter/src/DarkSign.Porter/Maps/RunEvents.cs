using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The event script behind the F1 menu's "DS3 run" tab. The menu only sets an event flag; an event added to Elden
/// Ring's common.emevd sees it, marks the run as active and warps the player with the game's own WarpPlayer
/// instruction, so the warp goes through the normal map-loading path.
///
/// Flag IDs live in the block of the empty sea tile m60_39_29 at the bottom edge of the world (1039290000+), the way
/// JortPob allocates its global flags: the block exists in every save, and no vanilla event uses it. Within a block,
/// 0-999 are saved and 2000-2999 are reset when a map loads, per JortPob's flag layout.
/// </summary>
public static class RunEvents
{
    public const uint FlagBlock = 1039290000;

    /// <summary>On while the player is in a Dark Souls III run (saved).</summary>
    public const uint Ds3RunActive = FlagBlock + 100;

    /// <summary>Set by the F1 menu to start a DS3 run; the event turns it off again.</summary>
    public const uint StartDs3Request = FlagBlock + 2100;

    /// <summary>Set by the F1 menu to leave the run and go back to Elden Ring.</summary>
    public const uint LeaveDs3Request = FlagBlock + 2101;

    public const uint StartEventId = FlagBlock + 1100;
    public const uint LeaveEventId = FlagBlock + 1101;

    /// <summary>Fallback "run-start" B: on every load outside a DS3 run, requests the start (no menu needed).</summary>
    public const uint AutoStartEventId = FlagBlock + 1102;

    /// <summary>DARKSIGN_AUTO_DS3_RUN=1 (launcher fallback "run-start" B) adds <see cref="AutoStartEventId"/>.</summary>
    public static readonly bool AutoStart = Environment.GetEnvironmentVariable("DARKSIGN_AUTO_DS3_RUN") == "1";

    /// <summary>Per-map warps (F1 menu, one button per ported map): request flag 2200+i, event 1200+i.</summary>
    public const int MaxWarps = 100;
    public static uint WarpRequestFlag(int index) => FlagBlock + 2200 + (uint)index;
    public static uint WarpEventId(int index) => FlagBlock + 1200 + (uint)index;
    private static bool IsWarpEvent(long id) => id >= FlagBlock + 1200 && id < FlagBlock + 1200 + MaxWarps;

    public sealed record Warp(MapId Map, uint EntityId, int Index);

    private const byte On = 1, Off = 0, EventFlagType = 0, EndRestart = 1;

    // Instruction layouts from DarkScript3's er-common.emedf.json. Built through Make, because SoulsFormats'
    // Instruction(int, int, uint layerMask, params object[]) overload would otherwise take a leading byte argument
    // as the layer mask and drop it from the arguments.
    public static EMEVD.Instruction Make(int bank, int id, params object[] args) => new(bank, id, args);
    private static EMEVD.Instruction WaitForFlag(uint flag) => Make(1003, 0, On, EventFlagType, flag);
    private static EMEVD.Instruction EndIfFlagOn(uint flag) => Make(1003, 2, (byte)0, On, EventFlagType, flag);
    private static EMEVD.Instruction SetFlag(uint flag, byte state) => Make(2003, 66, EventFlagType, flag, state);
    // The last argument is the area-name popup's message ID: 0 (er-common.emedf's default, and what vanilla warps pass)
    // shows none; -1 would ask for message -1.
    private static EMEVD.Instruction WarpPlayer(MapId map, uint entity) =>
        Make(2003, 14, (byte)map.Area, (byte)map.Block, (byte)map.Region, (byte)map.Index, entity, 0);
    private static EMEVD.Instruction WaitSeconds(float seconds) => Make(1001, 0, seconds);
    private static EMEVD.Instruction EndAndRestart() => Make(1000, 4, EndRestart);
    private static EMEVD.Instruction InitializeEvent(int slot, uint eventId) => Make(2000, 0, slot, eventId, 0u);

    public sealed record ReturnPoint(MapId Map, uint EntityId);

    /// <summary>
    /// Adds (or replaces) the run events in common.emevd and starts them from its constructor (event 0).
    /// </summary>
    public static void AddTo(EMEVD common, MapId host, uint startEntity, ReturnPoint back, bool autoStart = false)
    {
        common.Events.RemoveAll(e => e.ID is StartEventId or LeaveEventId or AutoStartEventId);
        foreach (var e in common.Events.Where(e => e.ID == 0))
            e.Instructions.RemoveAll(IsOurInit);

        var start = new EMEVD.Event(StartEventId, EMEVD.Event.RestBehaviorType.Default);
        start.Instructions.Add(WaitForFlag(StartDs3Request));
        start.Instructions.Add(SetFlag(StartDs3Request, Off));
        start.Instructions.Add(SetFlag(Ds3RunActive, On));
        start.Instructions.Add(WarpPlayer(host, startEntity));
        start.Instructions.Add(WaitSeconds(1));
        start.Instructions.Add(EndAndRestart());

        var leave = new EMEVD.Event(LeaveEventId, EMEVD.Event.RestBehaviorType.Default);
        leave.Instructions.Add(WaitForFlag(LeaveDs3Request));
        leave.Instructions.Add(SetFlag(LeaveDs3Request, Off));
        leave.Instructions.Add(SetFlag(Ds3RunActive, Off));
        leave.Instructions.Add(WarpPlayer(back.Map, back.EntityId));
        leave.Instructions.Add(WaitSeconds(1));
        leave.Instructions.Add(EndAndRestart());

        common.Events.Add(start);
        common.Events.Add(leave);

        // Runs once per load: outside a DS3 run, wait for the world to settle and ask the start event to warp.
        EMEVD.Event? auto = null;
        if (autoStart)
        {
            auto = new EMEVD.Event(AutoStartEventId, EMEVD.Event.RestBehaviorType.Default);
            auto.Instructions.Add(EndIfFlagOn(Ds3RunActive));
            auto.Instructions.Add(WaitSeconds(3));
            auto.Instructions.Add(SetFlag(StartDs3Request, On));
            common.Events.Add(auto);
        }

        var constructor = common.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
        {
            constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
            common.Events.Insert(0, constructor);
        }
        constructor.Instructions.Add(InitializeEvent(0, StartEventId));
        constructor.Instructions.Add(InitializeEvent(0, LeaveEventId));
        if (auto is not null)
            constructor.Instructions.Add(InitializeEvent(0, AutoStartEventId));
    }

    private static bool IsOurInit(EMEVD.Instruction i) =>
        i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8
        && (BitConverter.ToUInt32(i.ArgData, 4) is StartEventId or LeaveEventId or AutoStartEventId || IsWarpEvent(BitConverter.ToUInt32(i.ArgData, 4)));

    /// <summary>
    /// Adds (or replaces) one warp event per ported map: it waits for the map's request flag, turns the run on and
    /// warps to the map's start point. Lets each slot be tested on its own from the F1 menu.
    /// </summary>
    public static void AddWarps(EMEVD common, IEnumerable<Warp> warps)
    {
        common.Events.RemoveAll(e => IsWarpEvent(e.ID));
        var constructor = common.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
        {
            constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
            common.Events.Insert(0, constructor);
        }
        constructor.Instructions.RemoveAll(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 && IsWarpEvent(BitConverter.ToUInt32(i.ArgData, 4)));
        foreach (var w in warps)
        {
            if (w.Index < 0 || w.Index >= MaxWarps)
                throw new ArgumentOutOfRangeException(nameof(warps), $"warp index {w.Index}");
            var e = new EMEVD.Event(WarpEventId(w.Index), EMEVD.Event.RestBehaviorType.Default);
            e.Instructions.Add(WaitForFlag(WarpRequestFlag(w.Index)));
            e.Instructions.Add(SetFlag(WarpRequestFlag(w.Index), Off));
            e.Instructions.Add(SetFlag(Ds3RunActive, On));
            e.Instructions.Add(WarpPlayer(w.Map, w.EntityId));
            e.Instructions.Add(WaitSeconds(1));
            e.Instructions.Add(EndAndRestart());
            common.Events.Add(e);
            constructor.Instructions.Add(InitializeEvent(0, WarpEventId(w.Index)));
        }
    }

    /// <summary>
    /// Where "Leave DS3 run" sends the player: the first WarpPlayer target into Roundtable Hold (m11_10) found in
    /// Elden Ring's own event scripts, else the first WarpPlayer target at all. Taken from the game's files so the
    /// entity is one the game really warps to.
    /// </summary>
    public static ReturnPoint? FindReturnPoint(IEnumerable<EMEVD> scripts)
    {
        ReturnPoint? any = null;
        foreach (var script in scripts)
            foreach (var instruction in script.Events.SelectMany(e => e.Instructions))
            {
                if (instruction.Bank != 2003 || instruction.ID != 14 || instruction.ArgData.Length < 8)
                    continue;
                var a = instruction.ArgData;
                // Event parameters (X0_4 etc.) leave the bytes 0 here; a real target names a map and an entity.
                var entity = BitConverter.ToUInt32(a, 4);
                if (a[0] == 0 || entity == 0)
                    continue;
                var point = new ReturnPoint(new MapId(a[0], a[1], a[2], a[3]), entity);
                if (a[0] == 11 && a[1] == 10)
                    return point;
                any ??= point;
            }
        return any;
    }

    /// <summary>A map event script with only an empty constructor, so a host slot's own events never run.</summary>
    public static EMEVD EmptyMapScript(EMEVD template)
    {
        var script = new EMEVD(template.Format) { Compression = template.Compression };
        script.Events.Add(new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        script.Events.Add(new EMEVD.Event(50, EMEVD.Event.RestBehaviorType.Default));
        return script;
    }
}
