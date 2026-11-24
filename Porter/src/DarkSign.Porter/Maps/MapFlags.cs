namespace DarkSign.Porter.Maps;

/// <summary>
/// Where a ported map's own flags (boss defeated, doors opened, items picked up in that map) go: moved with the map
/// into the slot's map flags (DS3 1AABLLLL becomes 4500LLLL for m45_00), as Elden Ring's own legacy dungeons number
/// theirs, where the game keeps that block (<see cref="SlotBlockKept"/>); flags of blocks it doesn't keep get numbers
/// in the bottom-edge sea tiles' saved ranges, the blocks JortPob's shipped Morrowind port keeps all its flags in
/// (<see cref="FlagMap.LocalFlag"/>).
/// Entity and event IDs move with the map either way.
/// </summary>
public static class MapFlags
{
    /// <summary>
    /// Whether Elden Ring keeps flags in the block an Elden Ring flag falls in, for a slot's own map flags. Read off the
    /// F1 menu's flag check (flagcheck: lines) in Devin's 1.0.417 log: the colosseums keep 45000-45029 except each
    /// map's 7th block (45006, 45016, 45026), and the Painted World's slot m19_70 has no blocks at all (19700-19709
    /// missing), so every Painted World flag (Friede's fight, the pot that plays her cutscene) was never set. Only blocks
    /// seen kept count: any other slot's (m11_70, m11_72) are treated as missing until a flag check shows them.
    /// </summary>
    public static bool SlotBlockKept(long erFlag)
    {
        long block = erFlag / 1000;
        return block is >= 45000 and <= 45029 && block % 10 != 6;
    }

    /// <summary>
    /// A ported map's own DS3 flag in Elden Ring: moved with the map into its slot's map flags where the game keeps that
    /// block, otherwise a number in JortPob's saved ranges (<see cref="FlagMap.LocalFlag"/>),
    /// which the same flag check showed kept. A flag that already has such a number (a range placed together) keeps it.
    /// </summary>
    public static long Move(FlagMap flags, MapId ds3, MapId er, long flag)
    {
        if (flag <= 0)
            return flag;
        if (flags.Flags.ContainsKey(flag))
            return flags.LocalFlag(flag);
        long moved = ds3.MoveEntity((int)flag, er);
        return SlotBlockKept(moved) ? moved : flags.TryLocalFlag(flag) ?? moved;
    }

    /// <summary>
    /// A ported map's own DS3 event ID in Elden Ring. An event's ID is a flag too: a run-once (Default) event turns
    /// flag ID + slot on when it ends, and won't run again while it is on, and DS3's scripts read other events' flags
    /// (and the fight-reset events turn them off). In a block the game doesn't keep, that flag is never set, so every
    /// one-time event of the map (an item's pickup, a door's first opening, a boss's death reward) runs again on each
    /// load. So an event moves exactly like the flag of the same number (<see cref="Move"/>): into the slot where the
    /// block is kept, otherwise to the same saved number in JortPob's ranges that flag gets.
    /// Values that aren't DS3 event numbers (1AABLLLL) move as entities.
    /// </summary>
    public static long MoveEvent(FlagMap flags, MapId ds3, MapId er, long id) =>
        id >= ds3.Ds3FlagBase && id < ds3.Ds3FlagBase + 10_000 ? Move(flags, ds3, er, id) : ds3.MoveEntity((int)id, er);

    /// <summary>Whether every flag of a ported map's range start..end can stay in the slot's map flags.</summary>
    public static bool RangeKept(MapId ds3, MapId er, long start, long end) =>
        (Enumerable.Range(0, (int)Math.Min(end - start + 1, 10_000)).All(i => SlotBlockKept(ds3.MoveEntity((int)(start + i), er))));

}
