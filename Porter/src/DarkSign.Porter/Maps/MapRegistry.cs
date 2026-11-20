using System.Text;
using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Elden Ring's list of the maps it will load: /map/worldmsblist.worldloadlistlist.dcx (plus _dlc01 and _dlc02),
/// plain text, one <c>map:/MapStudio/mAA_BB_CC_DD.msb </c> line per map (trailing space, blank lines between
/// groups, <c>,testmap=1</c> after the name for test maps). Read from Devin's game files on 2026-11-08: every map
/// the game ships is listed, the colosseums m45_00-02 included, and so are IDs with no files at all (m11_70,
/// m19_70, test maps m70/m80/m88). A warp to the unlisted m50 test slot sat on the loading screen for ever while
/// the game asked for none of its files (me3 log, 1.0.230), and the flag manager had no blocks for it, so this list
/// is the gate a new map ID has to pass. Adding the m50 probe's line (1.0.234 to 1.0.238) crashed the game at boot
/// on every launch: not because the game refuses IDs it doesn't ship, but because area 50 is an open-world area
/// number and the game has room for six of those, all used (see the constants below). Since 2026-11-19 the porter
/// adds lines for new blocks of dungeon areas the game already has, within the arrays' sizes (<see cref="Write"/>).
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

    /// <summary>The mod's copy of the base list, decompressed.</summary>
    public static byte[] ReadModList(string path)
    {
        var raw = File.ReadAllBytes(path);
        return DCX.Is(raw) ? DCX.Decompress(raw).ToArray() : raw;
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

    // Elden Ring 2.7.2's fixed arrays for the map table it builds from these lists at boot (eldenring.exe, read
    // 2026-11-19 with the struct names of vswarte/fromsoftware-rs, crates/eldenring/src/cs/field_area.rs, MIT/Apache):
    // WorldInfo holds 28 dungeon area entries (0x50 bytes each), 192 block entries (0xE0) and 6 open-world grid area
    // entries (0xF80); an area number 50-88 is a grid area (eldenring.exe+0x662270: area - 0x32 <= 0x26). The fill
    // functions (+0x66CE10 dungeons, +0x66D0A0 grid) write one entry per distinct area and block without a bounds check.
    // Vanilla already has 6 grid areas (m60, m61, m62, m70, m80, m88), so the m50 probe's line made a seventh, written
    // past the array over the object at owner+0xB3630 whose vtable call then read 0xC0: the 1.0.238 boot crash at
    // +0xAED000. The base list also fills a 96-entry array of its dungeon IDs ("is a base game map", +0x66C4D0, a
    // DL_PANIC past 0x60); vanilla has 90.
    public const int MaxDungeonAreas = 28;
    public const int MaxBlocks = 192;
    public const int MaxBaseDungeonLines = 96;

    /// <summary>Area numbers 50-88 are open-world grid areas: Elden Ring has room for no more of them.</summary>
    public static bool IsGridArea(MapId id) => id.Area is >= 50 and <= 88;

    /// <summary>Every map the shipped lists name, list by list (the base list first).</summary>
    public static List<List<Entry>> ReadLists(GameArchives er) =>
        Lists.Select(path => er.Contains(path) ? Parse(er.Read(path)) : []).ToList();

    /// <summary>
    /// Which of <paramref name="slots"/> can be added to the base list, and why the others can't: an open-world area
    /// number, or no room left in the game's arrays. Slots the lists already name need no line.
    /// </summary>
    public static (List<MapSlots.Slot> Added, List<(MapSlots.Slot Slot, string Reason)> Refused) Additions(
        IReadOnlyList<List<Entry>> lists, IEnumerable<MapSlots.Slot> slots)
    {
        var all = lists.SelectMany(l => l).Select(e => e.Map).ToHashSet();
        var dungeon = all.Where(m => !IsGridArea(m)).ToHashSet();
        int baseLines = lists.Count == 0 ? 0 : lists[0].Count(e => !IsGridArea(e.Map));
        var areas = dungeon.Select(m => m.Area).ToHashSet();
        int blocks = dungeon.Count;
        var added = new List<MapSlots.Slot>();
        var refused = new List<(MapSlots.Slot, string)>();
        foreach (var slot in slots.Where(s => !all.Contains(s.Er)).DistinctBy(s => s.Er))
        {
            string? why = IsGridArea(slot.Er) ? $"area {slot.Er.Area} is an open-world area number (50-88) and Elden Ring has room for no more of those (the 1.0.238 boot crash)"
                : baseLines + 1 > MaxBaseDungeonLines ? $"the base list already names {baseLines} of the {MaxBaseDungeonLines} dungeon maps the game has room for"
                : blocks + 1 > MaxBlocks ? $"the game's {MaxBlocks} map blocks are all named"
                : !areas.Contains(slot.Er.Area) && areas.Count + 1 > MaxDungeonAreas ? $"the game's {MaxDungeonAreas} dungeon areas are all in use; use an area number the game has"
                : null;
            if (why is not null)
            {
                refused.Add((slot, why));
                continue;
            }
            added.Add(slot);
            baseLines++;
            blocks++;
            areas.Add(slot.Er.Area);
        }
        return (added, refused);
    }

    /// <summary>The base list's text with a line for each added map after the last line of its area.</summary>
    public static string WithLines(string text, IEnumerable<MapId> maps)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        foreach (var map in maps.OrderBy(m => m.ToString(), StringComparer.Ordinal))
        {
            var line = $"map:/MapStudio/{map}.msb ";
            int at = lines.FindLastIndex(l => Line.Match(l) is { Success: true } m && MapId.Parse(m.Groups[1].Value).Area == map.Area);
            if (at < 0)
                at = lines.FindLastIndex(l => Line.IsMatch(l));
            lines.Insert(at + 1, line);
        }
        return string.Join(newline, lines);
    }

    /// <summary>
    /// port-maps step: writes the base list with a line for each slot it doesn't name yet (and that fits, see
    /// <see cref="Additions"/>), compressed the way the game's own copy is. Writes nothing when every slot is named.
    /// </summary>
    public static string Write(PorterContext ctx, GameArchives er, IReadOnlyList<MapSlots.Slot> slots)
    {
        var (added, refused) = Additions(ReadLists(er), slots);
        var notes = refused.Select(r => $"{r.Slot.Ds3} -> {r.Slot.Er}: not added ({r.Reason})").ToList();
        if (added.Count == 0)
            return "map registry: every slot is a map Elden Ring lists; the game's own list is used" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        var raw = er.ReadRaw(ListPath);
        var compressed = DCX.Is(raw);
        DCX.CompressionInfo? compression = null;
        var bytes = compressed ? DCX.Decompress(raw, out compression).ToArray() : raw;
        bool bom = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        var text = WithLines(Text(bytes), added.Select(s => s.Er));
        var encoded = bom ? [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)] : Encoding.UTF8.GetBytes(text);
        var output = compressed ? DCX.Compress(encoded, compression!).ToArray() : encoded;
        var path = ctx.ModPath(MapPorter.ModParts(ListPath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, output);
        return $"map registry: {added.Count} map(s) added to Elden Ring's base list ({string.Join(", ", added.Select(s => s.Er))})"
               + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
    }

    /// <summary>
    /// The slots the shipped lists don't name and that can't be added either (<see cref="Additions"/>). The porter
    /// skips them: an unlisted ID never loads (endless loading screen, 1.0.230), and an open-world area number past
    /// the game's six crashes the boot (1.0.238).
    /// </summary>
    public static List<MapSlots.Slot> Unregistered(IReadOnlyList<List<Entry>> lists, IEnumerable<MapSlots.Slot> slots)
    {
        var refused = Additions(lists, slots).Refused.Select(r => r.Slot.Er).ToHashSet();
        return slots.Where(s => refused.Contains(s.Er)).ToList();
    }

    public static string SkipReason(IReadOnlyList<List<Entry>> lists, MapSlots.Slot slot) =>
        $"{slot.Ds3} -> {slot.Er}: SKIPPED: {slot.Er} is not in Elden Ring's map registry (worldmsblist.worldloadlistlist) and can't be added: "
        + (Additions(lists, [slot]).Refused.FirstOrDefault().Reason ?? "no room") + ".";
}
