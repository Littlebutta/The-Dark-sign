using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// What the next map shows from each "doorway" (connect collision), carried over from DS3 (the "world stand-ins"
/// step, <see cref="MapPorter"/>). This replaces the guesses of PRs #237 and #243 with DS3's own data.
/// <para>
/// <b>How DS3 hides one map's pieces while the player stands in another.</b> Each connect collision carries its own
/// display groups, read in the bit space of the map it names: standing on High Wall collision h002700, the Undead
/// Settlement shows only its pieces in group (word 6, bit 26), and Lothric Castle only its pieces in (word 5, bit 26)
/// and (word 7, bit 31). Lothric Castle's low-detail copies of the High Wall (m9002xx, groups word 6/7) are drawn from
/// 57 of the castle's 71 collisions and from none of the High Wall's connects into the castle, so DS3 never shows them
/// while the player is in the High Wall. Elden Ring keeps the same field: vanilla Stormveil's connects into m10_01
/// display groups that differ from their own collision's (h011600: connect 0000000000000508..., collision
/// ...0538001...), so the game evaluates a neighbour's parts against the connect, not the player's collision.
/// </para>
/// <para>
/// <b>What the port did.</b> Every ported connect displayed every group and every piece of the next map had none, so
/// once a neighbour loaded all of it was drawn: its stand-ins and far views of this map, and its copies of the pieces
/// along the border (m203291/m203395/m203396 at Dancer's hall). The "map-diag world" report on Devin's files (54,000
/// pieces, 514,000 floor cells, 2026-11-20) found 529 written pieces on 14 maps that sit over floor where DS3 never
/// shows them, all next to a border. #243 covered only the "low" textured stand-ins, and with bits of its own.
/// </para>
/// <para>
/// <b>What is written.</b> DS3's in-map culling stays off (map-groups A: every map's own pieces are drawn from every one
/// of its own collisions), and the groups are rewritten so that only the cross-map part of DS3's culling applies:
/// <list type="bullet">
/// <item>Own bit (word 0, bit = the map's place among the ported maps): every piece of the map has it, and every
/// collision of the map without a ported connect shows only it. So in-map drawing is unchanged, and a loaded
/// neighbour's pieces, which have a different own bit, are not shown from a collision with no doorway to it.</item>
/// <item>Doorway bits (words 4-7, numbered per map): each distinct DS3 display list of a connect into a map gets one
/// bit; a piece of that map has the bit when DS3 would show it from that connect (its DS3 draw groups meet the list,
/// or it has none). The connect shows only that bit. So what the next map shows from each doorway is exactly DS3's.</item>
/// <item>Border bits (words 1-3, numbered across all maps): a collision with a doorway also hides the map's own pieces
/// that stand over the floor of the map the doorway opens onto, where DS3 never shows them, when DS3 hides them on
/// this collision too (High Wall h004000 hides its castle stand-ins m980001/2/8 while the castle is shown). Each
/// distinct hidden set gets one bit; every other piece of the map has it, and the collision shows only it.</item>
/// </list>
/// Collisions keep no draw groups of their own (enemies standing on a collision with draw groups can fall through,
/// MsbPort.ApplyCollisionGroups). Objects and characters keep none either and are drawn as before.
/// </para>
/// </summary>
public static class MapConnectDisplay
{
    public const int OwnWord = 0, BorderFirstWord = 1, BorderWords = 3, DoorwayFirstWord = 4, DoorwayWords = 4;

    /// <summary>Floor cells inside a piece's box (grown by this much) are "under or beside it".</summary>
    public const float Reach = 2f;

    /// <summary>A piece stands over a neighbour's floor when at least this many of that map's cells are inside its box.</summary>
    public const int MinCells = 3;

    /// <summary>One ported map: its DS3 layout, the boxes of its judged pieces and its walkable floor.</summary>
    public sealed record MapData(MapId Ds3, MapId Er, MSB3 Msb, IReadOnlyList<MapStandIns.PieceBox> Pieces, IReadOnlyList<MapWorldFloor.Cell> Floor);

    /// <summary>The groups to write into one ported layout.</summary>
    public sealed class MapPlan
    {
        public uint[] Fallback { get; init; } = new uint[8];
        public Dictionary<string, uint[]> PieceDraw { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint[]> CollisionDisplay { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint[]> ConnectDisplay { get; } = new(StringComparer.Ordinal);
        /// <summary>Own pieces hidden on at least one border collision, with the maps whose floor they stand over.</summary>
        public Dictionary<string, List<MapId>> BorderHidden { get; } = new(StringComparer.Ordinal);
        public int DoorwayLists { get; set; }
    }

    public sealed record Plan(Dictionary<MapId, MapPlan> Maps, List<string> Notes);

    private static bool Empty(uint[] g)
    {
        foreach (var w in g)
            if (w != 0)
                return false;
        return true;
    }

    private static bool Meet(uint[] a, uint[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if ((a[i] & b[i]) != 0)
                return true;
        return false;
    }

    private static void SetBit(uint[] words, int bit) => words[bit / 32] |= 1u << (bit % 32);

    private sealed class ListKey : IEqualityComparer<uint[]>
    {
        public static readonly ListKey Instance = new();
        public bool Equals(uint[]? a, uint[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);
        public int GetHashCode(uint[] a)
        {
            var h = new HashCode();
            foreach (var w in a)
                h.Add(w);
            return h.ToHashCode();
        }
    }

    /// <summary>The DS3 map a DS3 connect names among the ported maps, if it is another one.</summary>
    private static MapId? Target(MSB3.Part.ConnectCollision c, MapId own, IReadOnlyList<MapId> maps) =>
        MsbPort.ConnectTarget(c.MapID, maps) is { } t && t != own ? t : null;

    /// <summary>
    /// Whether DS3 shows <paramref name="draw"/> (a piece of <paramref name="pieceMap"/>) to a player on
    /// <paramref name="collision"/> of <paramref name="floorMap"/>.
    /// </summary>
    private static bool ShownOn(MapId pieceMap, uint[] draw, MapId floorMap, string collision,
        Dictionary<(MapId, string), uint[]> display, Dictionary<(MapId, string), List<(MapId Target, uint[] Display)>> doorways)
    {
        if (pieceMap == floorMap)
            return Empty(draw) || (display.TryGetValue((floorMap, collision), out var d) && Meet(draw, d));
        if (!doorways.TryGetValue((floorMap, collision), out var list))
            return false;
        foreach (var (t, d) in list)
            if (t == pieceMap && (Empty(draw) || Meet(draw, d)))
                return true;
        return false;
    }

    public static Plan Build(IReadOnlyList<MapData> maps)
    {
        var notes = new List<string>();
        var ids = maps.Select(m => m.Ds3).ToList();
        var byId = maps.ToDictionary(m => m.Ds3);
        var erOrder = maps.Select(m => m.Er).Distinct().OrderBy(m => m.ToString()).ToList();
        var display = new Dictionary<(MapId, string), uint[]>();
        var doorways = new Dictionary<(MapId, string), List<(MapId Target, uint[] Display)>>();
        var joined = ids.ToDictionary(m => m, _ => new HashSet<MapId>());
        foreach (var m in maps)
        {
            foreach (var c in m.Msb.Parts.Collisions)
                display[(m.Ds3, c.Name)] = c.DispGroups;
            foreach (var c in m.Msb.Parts.ConnectCollisions)
                if (Target(c, m.Ds3, ids) is { } t)
                {
                    if (!doorways.TryGetValue((m.Ds3, c.CollisionName ?? ""), out var list))
                        doorways[(m.Ds3, c.CollisionName ?? "")] = list = [];
                    list.Add((t, c.DispGroups));
                    joined[m.Ds3].Add(t);
                    joined[t].Add(m.Ds3);
                }
        }

        // Floor cells by 32 m column, for box queries.
        const float column = 32f;
        var floorIndex = new Dictionary<(int, int), List<(MapId Map, MapWorldFloor.Cell Cell)>>();
        foreach (var m in maps)
            foreach (var cell in m.Floor)
            {
                var key = ((int)MathF.Floor(cell.At.X / column), (int)MathF.Floor(cell.At.Z / column));
                if (!floorIndex.TryGetValue(key, out var list))
                    floorIndex[key] = list = [];
                list.Add((m.Ds3, cell));
            }

        // Each piece standing over a joined map's floor where DS3 shows it from none of the floor around it.
        var overNeighbour = new Dictionary<(MapId, string), List<MapId>>();
        foreach (var m in maps)
        {
            var draws = m.Msb.Parts.MapPieces.ToDictionary(p => p.Name, p => p.DrawGroups, StringComparer.Ordinal);
            foreach (var piece in m.Pieces)
            {
                if (!draws.TryGetValue(piece.Name, out var draw))
                    continue;
                var lo = piece.Min - new Vector3(Reach);
                var hi = piece.Max + new Vector3(Reach);
                bool shown = false;
                var hidden = new Dictionary<MapId, int>();
                for (int x = (int)MathF.Floor(lo.X / column); x <= (int)MathF.Floor(hi.X / column) && !shown; x++)
                    for (int z = (int)MathF.Floor(lo.Z / column); z <= (int)MathF.Floor(hi.Z / column) && !shown; z++)
                    {
                        if (!floorIndex.TryGetValue((x, z), out var cells))
                            continue;
                        foreach (var (floorMap, cell) in cells)
                        {
                            if (floorMap != m.Ds3 && !joined[m.Ds3].Contains(floorMap))
                                continue;
                            if (Vector3.Clamp(cell.At, lo, hi) != cell.At)
                                continue;
                            if (ShownOn(m.Ds3, draw, floorMap, cell.Collision, display, doorways))
                            {
                                shown = true;
                                break;
                            }
                            if (floorMap != m.Ds3)
                                hidden[floorMap] = hidden.GetValueOrDefault(floorMap) + 1;
                        }
                    }
                var over = hidden.Where(h => h.Value >= MinCells).Select(h => h.Key).OrderBy(h => h.ToString()).ToList();
                if (!shown && over.Count > 0)
                    overNeighbour[(m.Ds3, piece.Name)] = over;
            }
        }

        // Border classes: the own pieces each doorway collision hides, numbered across all maps.
        var borderBits = new Dictionary<(MapId, string), int>();
        var classes = new Dictionary<(MapId, string), HashSet<string>>();
        var classIds = new List<(MapId Map, HashSet<string> Hidden)>();
        int borderDropped = 0;
        foreach (var m in maps)
        {
            var draws = m.Msb.Parts.MapPieces.ToDictionary(p => p.Name, p => p.DrawGroups, StringComparer.Ordinal);
            foreach (var c in m.Msb.Parts.Collisions)
            {
                if (!doorways.TryGetValue((m.Ds3, c.Name), out var list))
                    continue;
                var targets = list.Select(l => l.Target).ToHashSet();
                var hide = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ((map, name), over) in overNeighbour)
                    if (map == m.Ds3 && over.Any(targets.Contains) && draws.TryGetValue(name, out var draw) && !Empty(draw) && !Meet(draw, c.DispGroups))
                        hide.Add(name);
                if (hide.Count == 0)
                    continue;
                int index = classIds.FindIndex(k => k.Map == m.Ds3 && k.Hidden.SetEquals(hide));
                if (index < 0)
                {
                    if (classIds.Count >= BorderWords * 32)
                    {
                        borderDropped++;
                        continue;
                    }
                    classIds.Add((m.Ds3, hide));
                    index = classIds.Count - 1;
                }
                borderBits[(m.Ds3, c.Name)] = BorderFirstWord * 32 + index;
                classes[(m.Ds3, c.Name)] = hide;
            }
        }
        if (borderDropped > 0)
            notes.Add($"{borderDropped} doorway collisions had more hidden sets than the {BorderWords * 32} border bits; they hide nothing of their own map");

        var plans = new Dictionary<MapId, MapPlan>();
        foreach (var m in maps)
        {
            int own = erOrder.IndexOf(m.Er);
            if (own >= 32)
            {
                notes.Add($"{m.Er}: more than 32 ported maps, no own bit; its groups are left as they were");
                continue;
            }
            // Doorway lists into this map, from every other ported map.
            var lists = new List<uint[]>();
            var listIndex = new Dictionary<uint[], int>(ListKey.Instance);
            foreach (var other in maps)
                foreach (var c in other.Msb.Parts.ConnectCollisions)
                    if (Target(c, other.Ds3, ids) == m.Ds3 && !listIndex.ContainsKey(c.DispGroups))
                    {
                        if (lists.Count >= DoorwayWords * 32)
                        {
                            notes.Add($"{m.Er}: more than {DoorwayWords * 32} distinct doorway lists; the rest show nothing");
                            break;
                        }
                        listIndex[c.DispGroups] = lists.Count;
                        lists.Add(c.DispGroups);
                    }
            var plan = new MapPlan { DoorwayLists = lists.Count };
            plans[m.Er] = plan;
            var myBorders = borderBits.Where(b => b.Key.Item1 == m.Ds3).Select(b => (b.Value, Hidden: classes[b.Key])).DistinctBy(b => b.Value).ToList();
            // A piece the port adds that DS3 doesn't have is drawn from everywhere this map's groups reach.
            SetBit(plan.Fallback, OwnWord * 32 + own);
            foreach (var (bit, _) in myBorders)
                SetBit(plan.Fallback, bit);
            for (int i = 0; i < lists.Count; i++)
                SetBit(plan.Fallback, DoorwayFirstWord * 32 + i);
            foreach (var p in m.Msb.Parts.MapPieces)
            {
                var g = new uint[8];
                SetBit(g, OwnWord * 32 + own);
                foreach (var (bit, hidden) in myBorders)
                    if (!hidden.Contains(p.Name))
                        SetBit(g, bit);
                for (int i = 0; i < lists.Count; i++)
                    if (Empty(p.DrawGroups) || Meet(p.DrawGroups, lists[i]))
                        SetBit(g, DoorwayFirstWord * 32 + i);
                plan.PieceDraw[p.Name] = g;
                if (overNeighbour.TryGetValue((m.Ds3, p.Name), out var over) && myBorders.Any(b => b.Hidden.Contains(p.Name)))
                    plan.BorderHidden[p.Name] = over.Select(o => byId[o].Er).ToList();
            }
            foreach (var c in m.Msb.Parts.Collisions)
            {
                var g = new uint[8];
                SetBit(g, borderBits.TryGetValue((m.Ds3, c.Name), out var bit) ? bit : OwnWord * 32 + own);
                plan.CollisionDisplay[c.Name] = g;
            }
            foreach (var c in m.Msb.Parts.ConnectCollisions)
            {
                if (Target(c, m.Ds3, ids) is not { } t || byId[t].Er == m.Er)
                    continue;
                // Filled in below, once the target's lists are numbered.
                plan.ConnectDisplay[c.Name] = [];
            }
        }
        // Each connect shows the bit of its DS3 list in its target's numbering.
        foreach (var m in maps)
        {
            if (!plans.TryGetValue(m.Er, out var plan))
                continue;
            foreach (var c in m.Msb.Parts.ConnectCollisions)
            {
                if (!plan.ConnectDisplay.ContainsKey(c.Name) || Target(c, m.Ds3, ids) is not { } t)
                    continue;
                var g = new uint[8];
                var targetLists = TargetLists(maps, t, ids);
                int i = targetLists.FindIndex(l => l.AsSpan().SequenceEqual(c.DispGroups));
                if (i >= 0 && i < DoorwayWords * 32)
                    SetBit(g, DoorwayFirstWord * 32 + i);
                plan.ConnectDisplay[c.Name] = g;
            }
        }
        notes.Add($"over a neighbour's floor where DS3 never shows them: {overNeighbour.Count} pieces; border bits used {classIds.Count} of {BorderWords * 32}");
        return new Plan(plans, notes);
    }

    /// <summary>The distinct doorway lists into <paramref name="target"/>, in the order <see cref="Build"/> numbers them.</summary>
    private static List<uint[]> TargetLists(IReadOnlyList<MapData> maps, MapId target, IReadOnlyList<MapId> ids)
    {
        var lists = new List<uint[]>();
        var seen = new HashSet<uint[]>(ListKey.Instance);
        foreach (var other in maps)
            foreach (var c in other.Msb.Parts.ConnectCollisions)
                if (Target(c, other.Ds3, ids) == target && seen.Add(c.DispGroups))
                    lists.Add(c.DispGroups);
        return lists;
    }

    /// <summary>Writes one map's groups into its ported layout. Returns (pieces, collisions, connects) written.</summary>
    public static (int Pieces, int Collisions, int Connects) Apply(MSBE msb, MapPlan plan)
    {
        int pieces = 0, collisions = 0, connects = 0;
        foreach (var p in msb.Parts.MapPieces)
        {
            MapDrawGroups.Set(p.Unk1, ReadOnlySpan<uint>.Empty, plan.PieceDraw.GetValueOrDefault(p.Name) ?? plan.Fallback);
            pieces++;
        }
        foreach (var c in msb.Parts.Collisions)
            if (plan.CollisionDisplay.TryGetValue(c.Name, out var g))
            {
                MapDrawGroups.Set(c.Unk1, g, ReadOnlySpan<uint>.Empty);
                collisions++;
            }
        foreach (var c in msb.Parts.ConnectCollisions)
            if (plan.ConnectDisplay.TryGetValue(c.Name, out var g))
            {
                if (Empty(g))
                    MapDrawGroups.Clear(c.Unk1);
                else
                    MapDrawGroups.Set(c.Unk1, g, ReadOnlySpan<uint>.Empty);
                connects++;
            }
        return (pieces, collisions, connects);
    }

    /// <summary>
    /// The check (RULE 4): for every piece and every walkable cell inside its box (grown by <see cref="Reach"/>) of its
    /// own map or a joined one, whether DS3 shows it there and whether the port draws it there. Before = as builds before
    /// this change drew it (every piece of a loaded map from everywhere, PR #243 aside); after = with <paramref name="plan"/>, reading Elden
    /// Ring's groups the way DS3 reads its own (a neighbour's pieces through the doorway the player stands on).
    /// Counts (piece, cell) pairs where the port draws a piece DS3 hides, split by own map and neighbours, and the
    /// pairs where the port now hides a piece DS3 shows (should be 0).
    /// </summary>
    public sealed record Check(long NeighbourBefore, long NeighbourAfter, long OwnBefore, long OwnAfter, long HiddenButShown,
        Dictionary<MapId, (long Before, long After)> PerMap, List<string> Leftovers)
    {
        public List<string> WronglyHidden { get; init; } = [];
    }

    public static Check Verify(IReadOnlyList<MapData> maps, Plan plan)
    {
        var ids = maps.Select(m => m.Ds3).ToList();
        var byId = maps.ToDictionary(m => m.Ds3);
        var display = new Dictionary<(MapId, string), uint[]>();
        var doorways = new Dictionary<(MapId, string), List<(MapId Target, uint[] Display)>>();
        var erDoorways = new Dictionary<(MapId, string), List<(MapId Target, uint[] Display)>>();
        var joined = ids.ToDictionary(m => m, _ => new HashSet<MapId>());
        foreach (var m in maps)
        {
            foreach (var c in m.Msb.Parts.Collisions)
                display[(m.Ds3, c.Name)] = c.DispGroups;
            plan.Maps.TryGetValue(m.Er, out var mp);
            foreach (var c in m.Msb.Parts.ConnectCollisions)
                if (Target(c, m.Ds3, ids) is { } t)
                {
                    var key = (m.Ds3, c.CollisionName ?? "");
                    if (!doorways.TryGetValue(key, out var l))
                        doorways[key] = l = [];
                    l.Add((t, c.DispGroups));
                    if (!erDoorways.TryGetValue(key, out var e))
                        erDoorways[key] = e = [];
                    e.Add((t, mp?.ConnectDisplay.GetValueOrDefault(c.Name) ?? Enumerable.Repeat(0xFFFFFFFFu, 8).ToArray()));
                    joined[m.Ds3].Add(t);
                    joined[t].Add(m.Ds3);
                }
        }
        const float column = 32f;
        var floorIndex = new Dictionary<(int, int), List<(MapId Map, MapWorldFloor.Cell Cell)>>();
        foreach (var m in maps)
            foreach (var cell in m.Floor)
            {
                var key = ((int)MathF.Floor(cell.At.X / column), (int)MathF.Floor(cell.At.Z / column));
                if (!floorIndex.TryGetValue(key, out var list))
                    floorIndex[key] = list = [];
                list.Add((m.Ds3, cell));
            }
        long nb = 0, na = 0, ob = 0, oa = 0, wrong = 0;
        var perMap = new Dictionary<MapId, (long, long)>();
        var left = new Dictionary<string, long>();
        var wrongAt = new Dictionary<string, long>();
        foreach (var m in maps)
        {
            plan.Maps.TryGetValue(m.Er, out var mp);
            var draws = m.Msb.Parts.MapPieces.ToDictionary(p => p.Name, p => p.DrawGroups, StringComparer.Ordinal);
            long mb = 0, ma = 0;
            foreach (var piece in m.Pieces)
            {
                if (!draws.TryGetValue(piece.Name, out var draw))
                    continue;
                var er = mp?.PieceDraw.GetValueOrDefault(piece.Name) ?? mp?.Fallback;
                var lo = piece.Min - new Vector3(Reach);
                var hi = piece.Max + new Vector3(Reach);
                var pairs = new List<(MapId Floor, string Collision, bool Ds3, bool After)>();
                for (int x = (int)MathF.Floor(lo.X / column); x <= (int)MathF.Floor(hi.X / column); x++)
                    for (int z = (int)MathF.Floor(lo.Z / column); z <= (int)MathF.Floor(hi.Z / column); z++)
                    {
                        if (!floorIndex.TryGetValue((x, z), out var cells))
                            continue;
                        foreach (var (floorMap, cell) in cells)
                        {
                            bool own = floorMap == m.Ds3;
                            if (!own && !joined[m.Ds3].Contains(floorMap))
                                continue;
                            if (Vector3.Clamp(cell.At, lo, hi) != cell.At)
                                continue;
                            bool ds3Shows = ShownOn(m.Ds3, draw, floorMap, cell.Collision, display, doorways);
                            bool after;
                            if (er is null)
                                after = true;
                            else if (own)
                                after = mp is null || !mp.CollisionDisplay.TryGetValue(cell.Collision, out var cd) || Drawn(er, cd);
                            else
                                after = erDoorways.TryGetValue((floorMap, cell.Collision), out var list) && list.Any(l => l.Target == m.Ds3 && Drawn(er, l.Display));
                            if (ds3Shows && !after)
                            {
                                wrong++;
                                var key = $"{m.Ds3} {piece.Name} hidden on {floorMap} {cell.Collision}";
                                wrongAt[key] = wrongAt.GetValueOrDefault(key) + 1;
                            }
                            pairs.Add((floorMap, cell.Collision, ds3Shows, after));
                        }
                    }
                // Neighbours' floor: every cell where DS3 hides the piece counts (earlier builds drew all of a
                // loaded map). Own floor counts only for a piece standing over a neighbour (shown over none of the floor
                // around it), on its own doorway collisions into that neighbour: there the neighbour's real pieces show.
                var over = pairs.Any(q => q.Ds3) ? [] : pairs.Where(q => q.Floor != m.Ds3).GroupBy(q => q.Floor).Where(g => g.Count() >= MinCells).Select(g => g.Key).ToHashSet();
                foreach (var q in pairs.Where(q => !q.Ds3))
                {
                    if (q.Floor != m.Ds3)
                    {
                        nb++;
                        mb++;
                        if (q.After)
                        {
                            na++;
                            ma++;
                            var key = $"{m.Ds3} {piece.Name} over {q.Floor}";
                            left[key] = left.GetValueOrDefault(key) + 1;
                        }
                    }
                    else if (over.Count > 0 && doorways.TryGetValue((m.Ds3, q.Collision), out var dl) && dl.Any(d => over.Contains(d.Target)))
                    {
                        ob++;
                        if (q.After)
                            oa++;
                    }
                }
            }
            perMap[m.Er] = (mb, ma);
        }
        return new Check(nb, na, ob, oa, wrong, perMap,
            left.OrderByDescending(l => l.Value).Take(20).Select(l => $"{l.Key}: {l.Value} cells").ToList())
        {
            WronglyHidden = wrongAt.OrderByDescending(l => l.Value).Take(20).Select(l => $"{l.Key}: {l.Value} cells").ToList(),
        };
    }

    /// <summary>Whether a piece with these groups is drawn while the player stands where <paramref name="display"/> is shown.</summary>
    public static bool Drawn(uint[] draw, uint[] display) => MapDrawGroups.DrawnFrom(draw, display);
}
