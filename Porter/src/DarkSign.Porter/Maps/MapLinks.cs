using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Tells Elden Ring that neighbouring ported maps share one world space, so a map loaded through a connect collision
/// sits where DS3 put it.
///
/// DS3 places every map in one world: each map's parts carry its MSB MapOffset (m30_00 at the origin, m31_00 at
/// 50, -155, -580 turned -156 degrees, m37_00 at 423, -242, -925; read from Devin's DS3 layouts on 2026-11-19), and
/// a connect collision in one map names the neighbour whose parts lie right there. The port keeps every part's DS3
/// position (<see cref="MsbPort"/>), so the ported maps are in that same world space.
///
/// Elden Ring keeps a row per pair of linked legacy dungeon maps in WorldMapLegacyConvParam: a point in the source
/// map (area, block as "grid X", region as "grid Z", position) and the same point in the destination map. Maps built
/// in one space get the same point on both sides: Leyndell (m11_00) and the Subterranean Shunning-Grounds (m35_00)
/// are rows 12 and 110, both at -184, -88.5, -163, isBasePoint 1 (vanilla regulation, read 2026-11-19). The port adds
/// a pair of such rows, at the origin on both sides, for every two ported maps a DS3 connect collision joins, copied
/// from row 12. Rows 9000 and up (vanilla's last row is 1145).
/// </summary>
public static class MapLinks
{
    public const string ParamName = "WorldMapLegacyConvParam";
    public const int FirstRow = 9000, LastRow = 9999, TemplateRow = 12;

    public static bool IsOurs(int id) => id >= FirstRow && id <= LastRow;

    /// <summary>The pairs of ported maps that DS3's connect collisions join (each pair once, in slot order).</summary>
    public static List<(MapSlots.Slot A, MapSlots.Slot B)> Pairs(IReadOnlyList<MapSlots.Slot> slots, Func<MapId, IEnumerable<MapId>> connectsOf)
    {
        var pairs = new List<(MapSlots.Slot, MapSlots.Slot)>();
        var seen = new HashSet<(MapId, MapId)>();
        foreach (var a in slots.Where(s => !s.Probe))
            foreach (var target in connectsOf(a.Ds3))
            {
                var b = slots.FirstOrDefault(s => !s.Probe && s.Ds3 == target);
                if (b is null || b.Er == a.Er)
                    continue;
                var key = string.CompareOrdinal(a.Er.ToString(), b.Er.ToString()) < 0 ? (a.Er, b.Er) : (b.Er, a.Er);
                if (seen.Add(key))
                    pairs.Add((a, b));
            }
        return pairs;
    }

    /// <summary>The maps among <paramref name="maps"/> a DS3 layout's connect collisions name (see <see cref="MsbPort.ConnectTarget"/>).</summary>
    public static IEnumerable<MapId> ConnectTargets(MSB3 msb, IReadOnlyList<MapId> maps) =>
        msb.Parts.ConnectCollisions.Select(c => MsbPort.ConnectTarget(c.MapID, maps)).OfType<MapId>().Distinct();

    /// <summary>
    /// Replaces the port's rows in the param with two rows per pair. Returns the number of rows written.
    /// <para>
    /// Elden Ring's own rows that name a map the port fills (<paramref name="ported"/>) are taken out too
    /// (<see cref="VanillaRowsLeftOut"/> counts them): they describe what the game shipped under that ID, not the
    /// DS3 map now there. Vanilla has two such rows, 234 (m38_10 at 0,0,-416 = m38_11 at 0,0,256) and 235 (m38_11 at
    /// 0,0,-160 = m38_12 at 0,0,256), for its test maps, the IDs the Catacombs, Irithyll and Irithyll Dungeon now use.
    /// They sit before the port's rows (9000 and up) and say the maps are 672 and 416 m apart, where DS3 has them
    /// in one space; with both the game had two answers for where Irithyll lies against the Catacombs (1.0.53x
    /// playtest: Irithyll "totally broken", cave rock and stray geometry over the city after walking in from the
    /// Catacombs). Read from the mod's regulation on 2026-11-20.
    /// </para>
    /// </summary>
    public static int WriteRows(PARAM param, IReadOnlyList<(MapSlots.Slot A, MapSlots.Slot B)> pairs, IEnumerable<MapId>? ported = null)
    {
        param.Rows.RemoveAll(r => IsOurs(r.ID));
        var taken = (ported ?? pairs.SelectMany(p => new[] { p.A.Er, p.B.Er })).Select(m => (m.Area, m.Block, m.Region)).ToHashSet();
        VanillaRowsLeftOut = param.Rows.RemoveAll(r => r.ID != TemplateRow && (Names(r, "src", taken) || Names(r, "dst", taken)));
        var template = param[TemplateRow] ?? param.Rows.FirstOrDefault()
                       ?? throw new InvalidDataException($"{ParamName} has no rows to copy");
        int id = FirstRow;
        foreach (var (a, b) in pairs)
            foreach (var (src, dst) in new[] { (a.Er, b.Er), (b.Er, a.Er) })
            {
                if (id > LastRow)
                    break;
                var row = new PARAM.Row(template) { ID = id++, Name = $"[DS3] {src} -> {dst}" };
                Set(row, "srcAreaNo", (byte)src.Area);
                Set(row, "srcGridXNo", (byte)src.Block);
                Set(row, "srcGridZNo", (byte)src.Region);
                Set(row, "dstAreaNo", (byte)dst.Area);
                Set(row, "dstGridXNo", (byte)dst.Block);
                Set(row, "dstGridZNo", (byte)dst.Region);
                foreach (var f in new[] { "srcPosX", "srcPosY", "srcPosZ", "dstPosX", "dstPosY", "dstPosZ" })
                    Set(row, f, 0f);
                Set(row, "isBasePoint", (byte)1);
                param.Rows.Add(row);
            }
        param.Rows.Sort((x, y) => x.ID.CompareTo(y.ID));
        return id - FirstRow;
    }

    /// <summary>port-maps step: the rows for every pair of ported maps DS3 joins, into the mod's regulation.</summary>
    public static string Write(PorterContext ctx, GameArchives ds3, IReadOnlyList<MapSlots.Slot> slots)
    {
        var targets = new Dictionary<MapId, List<MapId>>();
        var maps = slots.Where(s => !s.Probe).Select(s => s.Ds3).Distinct().ToList();
        foreach (var slot in slots.Where(s => !s.Probe).DistinctBy(s => s.Ds3))
            targets[slot.Ds3] = ds3.Contains(MapPorter.MsbPath(slot.Ds3))
                ? ConnectTargets(MSB3.Read(ds3.Read(MapPorter.MsbPath(slot.Ds3))), maps).ToList()
                : [];
        var pairs = Pairs(slots, m => targets.GetValueOrDefault(m) ?? []);
        var regulation = ActionButtons.LoadRegulation(ctx);
        var param = regulation.Get(ParamName) ?? throw new InvalidDataException($"Elden Ring has no {ParamName}");
        int rows = WriteRows(param, pairs, slots.Where(s => !s.Probe).Select(s => s.Er));
        regulation.Save(ctx.ModPath("regulation.bin"));
        return $"map links: {pairs.Count} pairs of ported maps DS3 joins, {rows} {ParamName} rows, {VanillaRowsLeftOut} of Elden Ring's own rows for those map IDs taken out "
               + $"({string.Join(", ", pairs.Select(p => $"{p.A.Ds3.Area:D2}_{p.A.Ds3.Block:D2} {p.A.Name} <-> {p.B.Name}"))})";
    }

    /// <summary>Elden Ring's own rows <see cref="WriteRows"/> took out on its last call.</summary>
    public static int VanillaRowsLeftOut { get; private set; }

    private static bool Names(PARAM.Row row, string side, HashSet<(int, int, int)> maps)
    {
        int Get(string field) => row.Cells.FirstOrDefault(c => string.Equals(c.Def.InternalName, side + field, StringComparison.OrdinalIgnoreCase))?.Value is { } v
            ? Convert.ToInt32(v) : -1;
        return maps.Contains((Get("AreaNo"), Get("GridXNo"), Get("GridZNo")));
    }

    private static void Set(PARAM.Row row, string field, object value)
    {
        var cell = row.Cells.FirstOrDefault(c => string.Equals(c.Def.InternalName, field, StringComparison.OrdinalIgnoreCase));
        if (cell is null)
            return;
        cell.Value = Convert.ChangeType(value, cell.Value.GetType());
    }
}
