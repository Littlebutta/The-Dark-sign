namespace DarkSign.Porter.Maps;

/// <summary>
/// Where a ported map's own flags (boss defeated, doors opened, items picked up in that map) go, by method (launcher
/// Fallbacks window, "map-flags"):
/// <list type="bullet">
/// <item>A (default): moved with the map into the slot's map flags (DS3 1AABLLLL becomes 4500LLLL for m45_00), as
/// Elden Ring's own legacy dungeons number theirs.</item>
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
}
