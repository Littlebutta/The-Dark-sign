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
/// is the gate a new map ID has to pass; <see cref="WriteForNewIds"/> adds a line for every new-ID slot of a build.
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
    /// The list with a line for each map it (or another list) doesn't name yet, inserted after its last map line
    /// in the file's own line ending and shape ("map:/MapStudio/m50_00_00_00.msb " with the trailing space the
    /// shipped lines carry). The rest of the text is kept byte for byte.
    /// </summary>
    public static (byte[] Data, List<MapId> Added) WithMaps(byte[] list, IEnumerable<byte[]> otherLists, IEnumerable<MapId> maps)
    {
        var known = Parse(list).Select(e => e.Map).ToHashSet();
        foreach (var other in otherLists)
            foreach (var e in Parse(other))
                known.Add(e.Map);
        var added = new List<MapId>();
        var text = Text(list);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var last = lines.FindLastIndex(l => Line.IsMatch(l));
        foreach (var map in maps.Distinct())
        {
            if (known.Contains(map))
                continue;
            known.Add(map);
            added.Add(map);
            lines.Insert(last + 1, $"map:/MapStudio/{map}.msb ");
            last++;
        }
        return (added.Count == 0 ? list : Encoding.UTF8.GetBytes(string.Join(newline, lines)), added);
    }

    /// <summary>
    /// port-maps step: writes Mods\ER\map\worldmsblist.worldloadlistlist.dcx with every new-ID slot's map added, so the
    /// game loads it like a map it ships. Returns the report line.
    /// </summary>
    public static string WriteForNewIds(PorterContext ctx, GameArchives er, IEnumerable<MapSlots.Slot> slots)
    {
        var maps = slots.Where(s => s.Host != s.Er).Select(s => s.Er).Distinct().ToList();
        if (maps.Count == 0)
            return "map registry: every slot is a map Elden Ring ships; nothing to add";
        var (list, compression) = er.ReadWithCompression(ListPath);
        var others = Lists.Skip(1).Where(er.Contains).Select(er.Read).ToList();
        var (data, added) = WithMaps(list, others, maps);
        if (added.Count == 0)
            return "map registry: the new map IDs are already listed; nothing to add";
        File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(ListPath)), DCX.Compress(data, compression));
        return $"map registry: {string.Join(", ", added)} added to worldmsblist.worldloadlistlist";
    }
}
