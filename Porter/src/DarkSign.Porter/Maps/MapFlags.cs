namespace DarkSign.Porter.Maps;

/// <summary>
/// Where a ported map's own flags (boss defeated, doors opened, items picked up in that map) go, by method (launcher
/// Fallbacks window, "map-flags"):
/// <list type="bullet">
/// <item>A (default): moved with the map into the slot's map flags (DS3 1AABLLLL becomes 4500LLLL for m45_00), as
/// Elden Ring's own legacy dungeons number theirs, where the game keeps that block (<see cref="SlotBlockKept"/>);
/// flags of blocks it doesn't keep get numbers in JortPob's saved ranges.</item>
/// <item>B (DARKSIGN_MAP_FLAGS=common): new numbers in the bottom-edge sea tiles' saved ranges, the blocks JortPob's
/// shipped Morrowind port keeps all its flags in (<see cref="FlagMap.LocalFlag"/>), in case the colosseum slots' flag
/// blocks don't exist in single-player saves.</item>
/// </list>
/// Entity and event IDs move with the map either way.
/// </summary>
public static class MapFlags
{
    public static bool Common =>
        string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_MAP_FLAGS"), "common", StringComparison.OrdinalIgnoreCase);

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
    /// block, otherwise (and always under map-flags B) a number in JortPob's saved ranges (<see cref="FlagMap.LocalFlag"/>),
    /// which the same flag check showed kept. A flag that already has such a number (a range placed together) keeps it.
    /// Fallback map-flags C (DARKSIGN_MAP_FLAGS=slot) moves every one into the slot, as up to 1.0.422.
    /// </summary>
    public static long Move(FlagMap flags, MapId ds3, MapId er, long flag)
    {
        if (flag <= 0)
            return flag;
        if (Common || flags.Flags.ContainsKey(flag))
            return flags.LocalFlag(flag);
        long moved = ds3.MoveEntity((int)flag, er);
        return SlotOnly || SlotBlockKept(moved) ? moved : flags.LocalFlag(flag);
    }

    /// <summary>Whether every flag of a ported map's range start..end can stay in the slot's map flags.</summary>
    public static bool RangeKept(MapId ds3, MapId er, long start, long end) =>
        !Common && (SlotOnly || Enumerable.Range(0, (int)Math.Min(end - start + 1, 10_000)).All(i => SlotBlockKept(ds3.MoveEntity((int)(start + i), er))));

    public static bool SlotOnly =>
        string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_MAP_FLAGS"), "slot", StringComparison.OrdinalIgnoreCase);
}
