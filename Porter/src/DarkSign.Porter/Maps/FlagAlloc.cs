using System.Text;
using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Elden Ring only keeps event flags in blocks its flag manager allocates, and it reads which maps get blocks from
/// /event/eventflag/*.eventflagalloclist.dcx (legacy maps and open-world tiles, base game and DLC). A flag in a block
/// that isn't allocated is silently never set and never saved, so the F1 menu's run flags, the ported DS3 global flags
/// and each slot's own map flags all depend on these lists.
///
/// The lists are plain text (seen in the map check of Devin's 2026-11-08 build: the file starts with blank lines, then
/// "0,m10_00_00_00,0", "1,m10_01_00_00,..."): one entry per line, <c>index,mAA_BB_CC_DD,type</c>, with the indices
/// numbered across the four files (legacy maps from 0, open-world tiles from 200, DLC tiles from 350). The same build's
/// in-game check showed the colosseum slots keeping flags in blocks 45000-45029 and the m50 test slot keeping none,
/// yet the colosseums are in none of these lists (read from the game files 2026-11-08: 83 legacy maps, m17 and
/// m34_12 among them, no m45) while the test slot was missing from the map registry (<see cref="MapRegistry"/>). So
/// the registry decides a map's flag blocks and these lists are something else (open-world tiles carry types 1-2).
/// <see cref="WriteForNewIds"/> still mirrors the template: a new-ID slot gets a line only when its template has one.
/// </summary>
public static class FlagAlloc
{
    public const string LegacyList = "/event/eventflag/legacymap.eventflagalloclist.dcx";

    public static readonly string[] Lists =
    [
        LegacyList, "/event/eventflag/legacymap_dlc02.eventflagalloclist.dcx",
        "/event/eventflag/openmap.eventflagalloclist.dcx", "/event/eventflag/openmap_dlc02.eventflagalloclist.dcx",
    ];

    /// <summary>One line of a list: its index, the map, and the fields after the map kept as written (the type).</summary>
    public sealed record Entry(int Index, MapId Map, string Rest)
    {
        public override string ToString() => $"{Index},{Map}{Rest}";
    }

    private static readonly Regex Line = new(@"^\s*(\d+)\s*,\s*(m\d\d_\d\d_\d\d_\d\d)(.*?)\s*$", RegexOptions.Compiled);

    /// <summary>The entries of a decompressed list; lines that aren't entries (the blank lines at the top) are skipped.</summary>
    public static List<Entry> Parse(byte[] data)
    {
        var entries = new List<Entry>();
        foreach (var raw in Text(data).Split('\n'))
        {
            var m = Line.Match(raw.TrimEnd('\r'));
            if (m.Success)
                entries.Add(new Entry(int.Parse(m.Groups[1].Value), MapId.Parse(m.Groups[2].Value), m.Groups[3].Value));
        }
        return entries;
    }

    private static string Text(byte[] data)
    {
        // The shipped lists are single-byte text; a UTF-16 copy (from another tool) is read too.
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 4 && data[1] == 0 && data[3] == 0)
            return Encoding.Unicode.GetString(data);
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>
    /// The map a flag block belongs to, or null for a block outside the map layouts. A block is flag / 1000; legacy map
    /// flags are AABBxxxx (block AABBx, map mAA_BB), open-world flags are 10XXYYxxxx for m60_XX_YY and 20XXYYxxxx for
    /// m61_XX_YY (the sea tile m60_39_29 holds 1039290000+). The returned map has region and index 0: a list names the
    /// tile's small map (m60_41_38_00), and <see cref="SameMap"/> compares area, block and region only.
    /// </summary>
    public static MapId? BlockMap(long block)
    {
        var prefix = block / 10;
        if (prefix is >= 1000 and < 10000)
            return new MapId((int)(prefix / 100), (int)(prefix % 100), 0, 0);
        if (prefix is >= 100000 and < 110000)
            return new MapId(60, (int)((prefix - 100000) / 100), (int)(prefix % 100), 0);
        if (prefix is >= 200000 and < 210000)
            return new MapId(61, (int)((prefix - 200000) / 100), (int)(prefix % 100), 0);
        return null;
    }

    public static bool SameMap(MapId a, MapId b) => a.Area == b.Area && a.Block == b.Block && a.Region == b.Region;

    /// <summary>The entry that allocates a block's map, in any of the parsed lists.</summary>
    public static (string List, Entry Entry)? Find(IEnumerable<(string Name, List<Entry> Entries)> lists, long block)
    {
        if (BlockMap(block) is not { } map)
            return null;
        foreach (var (name, entries) in lists)
            foreach (var e in entries)
                if (SameMap(e.Map, map))
                    return (name, e);
        return null;
    }

    /// <summary>
    /// The legacy list with a line added for each map that no list names yet, copied from its template's line (the
    /// same type field) under the first index no list uses above the legacy list's highest. The original text is
    /// kept byte for byte; only lines are appended. Returns the new text and the lines added (none: nothing to do).
    /// </summary>
    public static (byte[] Data, List<Entry> Added) WithMaps(byte[] legacy, IEnumerable<byte[]> otherLists,
        IEnumerable<(MapId Map, MapId Template)> maps)
    {
        var legacyEntries = Parse(legacy);
        var all = new List<Entry>(legacyEntries);
        foreach (var other in otherLists)
            all.AddRange(Parse(other));
        var used = all.Select(e => e.Index).ToHashSet();
        var next = legacyEntries.Count > 0 ? legacyEntries.Max(e => e.Index) + 1 : 0;
        var added = new List<Entry>();
        var text = new StringBuilder(Text(legacy));
        foreach (var (map, template) in maps)
        {
            if (all.Any(e => SameMap(e.Map, map)) || added.Any(e => SameMap(e.Map, map)))
                continue;
            // The colosseums are in no list and keep their flags anyway (the map registry decides, 2026-11-08), so a
            // map whose template isn't listed mirrors it and gets no line either.
            var from = all.FirstOrDefault(e => SameMap(e.Map, template));
            if (from is null)
                continue;
            while (used.Contains(next))
                next++;
            used.Add(next);
            var entry = new Entry(next, map, from.Rest);
            added.Add(entry);
            if (text.Length > 0 && text[^1] != '\n')
                text.Append("\r\n");
            text.Append(entry).Append("\r\n");
        }
        return (added.Count == 0 ? legacy : Encoding.UTF8.GetBytes(text.ToString()), added);
    }

    /// <summary>
    /// port-maps step: writes Mods\ER\event\eventflag\legacymap.eventflagalloclist.dcx with the new-ID slots' maps
    /// added, so the game allocates their flag blocks (the m50 probe, and every map of the map-slots B method). Slots
    /// whose map Elden Ring ships need nothing. Returns the report line.
    /// </summary>
    public static string WriteForNewIds(PorterContext ctx, GameArchives er, IEnumerable<MapSlots.Slot> slots)
    {
        var maps = slots.Where(s => s.Host != s.Er).Select(s => (s.Er, s.Host)).Distinct().ToList();
        if (maps.Count == 0)
            return "flag allocation: every slot is a map Elden Ring ships; nothing to add";
        var (legacy, compression) = er.ReadWithCompression(LegacyList);
        var others = Lists.Skip(1).Where(er.Contains).Select(er.Read).ToList();
        var (data, added) = WithMaps(legacy, others, maps);
        if (added.Count == 0)
            return "flag allocation: nothing to add (the new map IDs are listed already, or their templates aren't listed "
                   + "either, like the colosseums; the map registry decides their flag blocks)";
        File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(LegacyList)), DCX.Compress(data, compression));
        return $"flag allocation: {string.Join(", ", added)} added to legacymap.eventflagalloclist";
    }

    /// <summary>The flag blocks the port's flags fall in, with what uses them.</summary>
    public static SortedDictionary<long, string> UsedBlocks(IEnumerable<MapId> slots, IEnumerable<long> globalFlags)
    {
        var blocks = new SortedDictionary<long, string>();
        void Add(long flag, string what) => blocks.TryAdd(flag / 1000, what);
        Add(RunEvents.Ds3RunActive, "DS3 run active flag (saved)");
        Add(RunEvents.StartDs3Request, "F1 menu start/leave/warp request flags");
        Add(RunEvents.StartEventId, "run event IDs");
        foreach (var s in slots.Distinct())
            Add(s.EntityBase, $"{s} map flags (e.g. boss defeated)");
        foreach (var f in globalFlags.Where(f => f > 0))
            Add(f, "ported DS3 global flags / common events (darksign_flags.tsv)");
        return blocks;
    }

    public const string ProbeFile = "darksign_flag_blocks.txt";

    /// <summary>
    /// The flag blocks the game is asked about in game (TestMenu flagcheck.c): every block a flag of the port lives in
    /// (event IDs aren't flags and need no block). Each line is the block's probe flag (its last number, which the port
    /// never hands out) and what uses the block.
    /// </summary>
    public static IEnumerable<string> ProbeLines(IEnumerable<MapId> slots, IEnumerable<long> flags, bool slotFlags = true)
    {
        var blocks = new SortedDictionary<long, string>();
        void Add(long flag, string what) => blocks.TryAdd(flag / 1000, what);
        Add(RunEvents.Ds3RunActive, "DS3 run active flag, saved");
        Add(RunEvents.StartDs3Request, "F1 menu start/leave/warp requests");
        if (slotFlags)
            foreach (var s in slots.Distinct())
                for (long b = 0; b < 10; b++)
                    Add(s.EntityBase + b * 1000, $"{s} map flags");
        foreach (var f in flags.Where(f => f > 0))
            Add(f, Layout(f));
        yield return "# probe flag\twhat uses the block (written by port-maps; read by dksign_testmenu.dll, which logs flagcheck: lines)";
        foreach (var (block, what) in blocks)
            yield return $"{block * 1000 + 999}\t{what}";
    }

    /// <summary>What JortPob's shipped layout says a flag in the bottom-edge sea tiles is (see DarkSign.Core.Preflight).</summary>
    public static string Layout(long flag) => DarkSign.Core.Preflight.JortPobLayout(flag) switch
    {
        "saved" => "saved flags (JortPob layout)",
        "temporary" => "temporary flags (JortPob layout)",
        "event" => "event IDs (JortPob layout)",
        _ => "outside JortPob's blocks",
    };

    /// <summary>
    /// Map check: is every block the port uses allocated to a listed map? The mod's own legacy list (written by
    /// <see cref="WriteForNewIds"/>) is read in place of the game's when it exists, since that is the one the game loads.
    /// </summary>
    public static void Check(GameArchives er, MapValidate.Report report, IEnumerable<MapId> slots, IEnumerable<long> globalFlags,
        string? modLegacyList = null, ISet<MapId>? registered = null)
    {
        var lists = new List<(string Name, List<Entry> Entries)>();
        foreach (var path in Lists)
        {
            try
            {
                byte[] data;
                var name = ItemText.Leaf(path);
                if (path == LegacyList && modLegacyList is not null && File.Exists(modLegacyList))
                {
                    data = DCX.Decompress(File.ReadAllBytes(modLegacyList));
                    name += " (the mod's copy)";
                }
                else if (er.Contains(path))
                    data = er.Read(path);
                else
                    continue;
                var entries = Parse(data);
                if (entries.Count == 0)
                {
                    report.Warn($"flag alloc list {name}: {data.Length} bytes but no \"index,map,type\" lines; starts "
                                + Convert.ToHexString(data.AsSpan(0, Math.Min(32, data.Length))));
                    continue;
                }
                lists.Add((name, entries));
                report.Ok($"flag alloc list {name}: {entries.Count} maps, indices {entries.Min(e => e.Index)}-{entries.Max(e => e.Index)}");
            }
            catch (Exception ex)
            {
                report.Warn($"flag alloc list {ItemText.Leaf(path)} doesn't read: {ex.Message}");
            }
        }
        if (lists.Count == 0)
        {
            report.Warn("no Elden Ring event flag allocation list found; can't tell whether the port's flags are in allocated blocks");
            return;
        }
        foreach (var (block, what) in UsedBlocks(slots, globalFlags))
        {
            var map = BlockMap(block);
            if (map is null)
                report.Warn($"flag block {block}xxx ({what}): not in any map's flag layout; the game keeps no such flags");
            else if (Find(lists, block) is { } found)
                report.Ok($"flag block {block}xxx ({what}): {found.Entry.Map} is listed in {found.List} (entry {found.Entry.Index})");
            else if (registered is not null && registered.Any(r => SameMap(r, map.Value)))
                report.Ok($"flag block {block}xxx ({what}): {map} is in no flag allocation list but is in the map registry, "
                          + "like the colosseums, whose flags the game keeps");
            else if (map.Value.Area < 60)
                report.Warn($"flag block {block}xxx ({what}): {map} is in no flag allocation list and not in the map registry, so "
                            + "the game keeps none of these flags (flagcheck MISSING for the unregistered m50 test slot, 2026-11-08)");
            else
                report.Warn($"flag block {block}xxx ({what}): tile {map} is in no flag allocation list (the in-game flagcheck decides; "
                            + "JortPob's sea-tile flags kept in the 2026-11-08 test)");
        }
    }
}
