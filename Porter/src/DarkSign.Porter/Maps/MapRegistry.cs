using System.Text;
using System.Text.RegularExpressions;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Elden Ring's list of the maps it will load: /map/worldmsblist.worldloadlistlist.dcx (plus _dlc01 and _dlc02),
/// plain text, one <c>map:/MapStudio/mAA_BB_CC_DD.msb </c> line per map (trailing space, blank lines between
/// groups, <c>,testmap=1</c> after the name for test maps). Read from Devin's game files on 2026-11-08: every map
/// the game ships is listed, the colosseums m45_00-02 included, and so are IDs with no files at all (m11_70,
/// m19_70, test maps m70/m80/m88). A warp to the unlisted m50 test slot sat on the loading screen for ever while
/// the game asked for none of its files (me3 log, 1.0.230), and the flag manager had no blocks for it, so this list
/// is the gate a new map ID has to pass. Adding a line for an ID the game doesn't ship (the m50 probe, 1.0.234 to
/// 1.0.238) crashed the game at boot on every launch, so the mod never writes this file: a slot's ID has to be one
/// the shipped lists name (<see cref="Unregistered"/>).
/// </summary>
public static class MapRegistry
{
    public const string ListPath = "/map/worldmsblist.worldloadlistlist.dcx";

    public static readonly string[] Lists =
        [ListPath, "/map/worldmsblist_dlc01.worldloadlistlist.dcx", "/map/worldmsblist_dlc02.worldloadlistlist.dcx"];

    /// <summary>One line: the map and whatever follows its name (",testmap=1" or nothing).</summary>
    public sealed record Entry(MapId Map, string Options);

    private static readonly Regex Line = new(@"^\s*map:/MapStudio/(m\d\d_\d\d_\d\d_\d\d)\.msb(.*?)\s*$", RegexOptions.Compiled);

    public static List<Entry> Parse(byte[] data)
    {
        var entries = new List<Entry>();
        foreach (var raw in Text(data).Split('\n'))
        {
            var m = Line.Match(raw.TrimEnd('\r'));
            if (m.Success)
                entries.Add(new Entry(MapId.Parse(m.Groups[1].Value), m.Groups[2].Value));
        }
        return entries;
    }

    private static string Text(byte[] data) =>
        data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
        : Encoding.UTF8.GetString(data);

    /// <summary>The maps every list names (null for a list that isn't there).</summary>
    public static HashSet<MapId> Registered(GameArchives er)
    {
        var maps = new HashSet<MapId>();
        foreach (var path in Lists.Where(er.Contains))
            foreach (var e in Parse(er.Read(path)))
                maps.Add(e.Map);
        return maps;
    }

    /// <summary>
    /// The slots whose Elden Ring map ID the registry doesn't name. The porter skips them: the registry is a list the
    /// game reads, not a table it is built from. Adding a line for an ID the executable doesn't know (the m50 probe,
    /// PR #95, 1.0.234-1.0.238) crashed the game at boot on every launch, twice on 2026-11-09, right after the
    /// registry loaded and before any character or map: access violation at eldenring.exe+0xAED000, a virtual call
    /// through the null vtable of the per-map entry the ID should have had (fault address 0xC0, rax 0). And an ID
    /// the registry doesn't name is never looked up (endless loading screen, 1.0.230). So the mod never writes this
    /// file, and a slot has to use an ID the shipped lists already carry.
    /// </summary>
    public static List<MapSlots.Slot> Unregistered(HashSet<MapId> registered, IEnumerable<MapSlots.Slot> slots) =>
        slots.Where(s => !registered.Contains(s.Er)).ToList();

    public static string SkipReason(MapSlots.Slot slot) =>
        $"{slot.Ds3} -> {slot.Er}: SKIPPED: {slot.Er} is not in Elden Ring's map registry (worldmsblist.worldloadlistlist). "
        + "The game only knows the map IDs it ships: registering another one crashed every boot (1.0.238, "
        + "eldenring.exe+0xAED000), and an unregistered one never loads. Use a registered ID.";
}
