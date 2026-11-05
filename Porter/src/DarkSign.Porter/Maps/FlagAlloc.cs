namespace DarkSign.Porter.Maps;

/// <summary>
/// Elden Ring only keeps event flags in blocks its flag manager allocates, and it reads which blocks exist from
/// /event/eventflag/*.eventflagalloclist.dcx (legacy maps and open-world tiles, base game and DLC). A flag in a block
/// that isn't listed is silently never set and never saved, so the F1 menu's run flags, the ported DS3 global flags
/// and the slot's map flags all depend on these lists.
///
/// The list format isn't documented anywhere this project can check, so this is a best-effort probe: it looks for each
/// block number (flag / 1000, the flag manager's divisor) as a 32-bit value in the decompressed lists, and puts the
/// files' sizes and first bytes in the report so the format can be worked out from a player's map check.
/// </summary>
public static class FlagAlloc
{
    public static readonly string[] Lists =
    [
        "/event/eventflag/legacymap.eventflagalloclist.dcx", "/event/eventflag/legacymap_dlc02.eventflagalloclist.dcx",
        "/event/eventflag/openmap.eventflagalloclist.dcx", "/event/eventflag/openmap_dlc02.eventflagalloclist.dcx",
    ];

    /// <summary>Whether a block number (or the block's 10000-flag group) appears as a little-endian 32-bit value.</summary>
    public static bool Mentions(byte[] data, long block)
    {
        foreach (var value in new[] { block, block / 10 })
        {
            var v = (uint)value;
            for (int i = 0; i + 4 <= data.Length; i++)
                if (BitConverter.ToUInt32(data, i) == v)
                    return true;
        }
        return false;
    }

    /// <summary>The blocks the port's flags fall in, with what uses them.</summary>
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

    public static void Check(GameArchives er, MapValidate.Report report, IEnumerable<MapId> slots, IEnumerable<long> globalFlags)
    {
        var lists = new List<(string Name, byte[] Data)>();
        foreach (var path in Lists.Where(er.Contains))
        {
            try
            {
                var data = er.Read(path);
                lists.Add((ItemText.Leaf(path), data));
                report.Ok($"flag alloc list {ItemText.Leaf(path)}: {data.Length} bytes; starts {Convert.ToHexString(data.AsSpan(0, Math.Min(48, data.Length)))}");
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
            var where = lists.Where(l => Mentions(l.Data, block)).Select(l => l.Name).ToList();
            if (where.Count > 0)
                report.Ok($"flag block {block}xxx ({what}): listed in {string.Join(", ", where)}");
            else
                report.Warn($"flag block {block}xxx ({what}): not found in Elden Ring's flag allocation lists (format unconfirmed); "
                            + "if these flags never turn on in game, this block is not allocated");
        }
    }
}
