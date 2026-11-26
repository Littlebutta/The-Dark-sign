namespace DarkSign.Porter.Maps;

/// <summary>
/// Where Dark Souls III's global event flags (NPC progress, items picked up, bosses, shortcuts: anything not tied to
/// one map) live in Elden Ring. DS3's numbers would collide with Elden Ring's own flags, so each one gets a new flag
/// in the blocks of the empty sea tiles along the bottom edge of the world, the way JortPob allocates its flags:
/// tiles m60_30_29 to m60_38_29 (1030290000 to 1038290000), numbers 0-999 of each block (saved with the game).
/// Tile 39 is the run's own (<see cref="RunEvents"/>). Ported common events take numbers 1000-1999 of the same
/// blocks (JortPob's event range), counting down from tile 38.
///
/// The assignments are written to a file next to the mod. A full port-maps run starts without it (from vanilla
/// inputs, so every PC gets the same numbers); numbers are handed out in a fixed order from the game files, so a
/// rebuild from the same files and maps gives the same flags and a save keeps its DS3 progress. A --only run reuses
/// the file, so the maps it doesn't rebuild keep matching.
/// </summary>
public sealed class FlagMap
{
    public const long FirstTile = 1030290000, TileStep = 1_000_000;
    public const int Tiles = 9, PerTile = 1000;

    private readonly Dictionary<long, long> _flags = new();
    private readonly Dictionary<long, long> _events = new();
    /// <summary>The Elden Ring numbers <see cref="_flags"/> hands out (kept beside it: a lookup per new flag).</summary>
    private readonly HashSet<long> _used = new();
    private int _nextFlag, _nextEvent;

    public IReadOnlyDictionary<long, long> Flags => _flags;
    public IReadOnlyDictionary<long, long> Events => _events;
    public int Capacity => Tiles * PerTile;

    public static long FlagSlot(int n) => FirstTile + n / PerTile * TileStep + n % PerTile;
    public static long EventSlot(int n) => FirstTile + (Tiles - 1 - n / PerTile) * TileStep + 1000 + n % PerTile;

    /// <summary>
    /// Flags of a ported map's blocks the game doesn't keep (the rest move into its slot's map flags, 4500xxxx) get
    /// numbers here, in the saved ranges JortPob uses that the global flags don't: 4000-4999 and 7000-9999 of the
    /// same tiles (4000 a tile, 36000 in all).
    /// </summary>
    public const int LocalPerTile = 4000;
    public int LocalCapacity => Tiles * LocalPerTile;

    public static long LocalSlot(int n)
    {
        int k = n % LocalPerTile;
        return FirstTile + n / LocalPerTile * TileStep + (k < 1000 ? 4000 + k : 7000 + (k - 1000));
    }

    private int _nextLocal;

    /// <summary>The Elden Ring flag for a DS3 global flag (a new one the first time).</summary>
    public long Flag(long ds3) => Take(ds3, FlagSlot, Capacity, ref _nextFlag);

    /// <summary>The Elden Ring flag for one of a ported map's own flags under map-flags B.</summary>
    public long LocalFlag(long ds3) => Take(ds3, LocalSlot, LocalCapacity, ref _nextLocal);

    /// <summary>
    /// <see cref="LocalFlag"/>, or null once the saved ranges are full (counted in <see cref="LocalOverflow"/>), so a
    /// full range never stops an hour-long build: the caller keeps the slot's own number.
    /// </summary>
    public long? TryLocalFlag(long ds3)
    {
        if (_flags.TryGetValue(ds3, out var er))
            return er;
        try { return LocalFlag(ds3); }
        catch (InvalidOperationException)
        {
            LocalOverflow++;
            return null;
        }
    }

    /// <summary>Map flags and event IDs that found no room in the saved ranges (they stay in the slot's own blocks).</summary>
    public int LocalOverflow { get; private set; }

    /// <summary>How many saved-range numbers ported maps' own flags and events took.</summary>
    public int LocalUsed => _flags.Values.Count(v => v >= FirstTile && v < FirstTile + Tiles * TileStep && (v - FirstTile) % TileStep is >= 4000 and < 5000 or >= 7000 and < 10000);

    private long Take(long ds3, Func<int, long> slot, int capacity, ref int next)
    {
        if (ds3 <= 0)
            return ds3;
        if (_flags.TryGetValue(ds3, out var er))
            return er;
        while (next < capacity && _used.Contains(slot(next)))
            next++;
        if (next >= capacity)
            throw new InvalidOperationException($"no room left for DS3 flag {ds3} ({capacity} used)");
        var taken = slot(next++);
        _used.Add(taken);
        return _flags[ds3] = taken;
    }

    /// <summary>
    /// Flags used as a range (batch checks of start..end): the whole range gets consecutive Elden Ring flags in one
    /// block, so "every flag from A to B" still means the same flags. Returns the new start, or -1 if some of the
    /// range was already placed elsewhere.
    /// </summary>
    public long Range(long start, long end) => Range(start, end, FlagSlot, Capacity, ref _nextFlag);

    /// <summary>A range of a ported map's own flags under map-flags B.</summary>
    public long LocalRange(long start, long end) => Range(start, end, LocalSlot, LocalCapacity, ref _nextLocal);

    private long Range(long start, long end, Func<int, long> slot, int capacity, ref int next)
    {
        if (start <= 0 || end < start)
            return -1;
        if (_flags.TryGetValue(start, out var first))
            return Enumerable.Range(0, (int)(end - start + 1)).All(i => _flags.TryGetValue(start + i, out var v) && v == first + i) ? first : -1;
        int count = (int)(end - start + 1);
        if (count > PerTile)
            return -1;
        var used = _used;
        // Start at the next free number whose block has room for the whole range.
        int n = next;
        while (n % PerTile + count > PerTile || Enumerable.Range(n, count).Any(i => used.Contains(slot(i))))
            n++;
        if (n + count > capacity)
            return -1;
        for (int i = 0; i < count; i++)
        {
            if (_flags.ContainsKey(start + i))
                return -1;
            _flags[start + i] = slot(n + i);
            _used.Add(slot(n + i));
        }
        if (n == next)
            next += count;
        return slot(n);
    }

    /// <summary>Whether an Elden Ring event ID is one of the numbers given to ported DS3 common events.</summary>
    public static bool IsEventSlot(long id) =>
        id >= FirstTile + 1000 && id < FirstTile + Tiles * TileStep && (id - FirstTile) % TileStep is >= 1000 and < 2000;

    /// <summary>The Elden Ring event ID for a DS3 common event.</summary>
    public long Event(long ds3)
    {
        if (_events.TryGetValue(ds3, out var er))
            return er;
        while (_events.ContainsValue(EventSlot(_nextEvent)))
            _nextEvent++;
        if (_nextEvent >= Capacity)
            throw new InvalidOperationException($"no room left for DS3 common event {ds3}");
        return _events[ds3] = EventSlot(_nextEvent++);
    }

    /// <summary>
    /// The table every build starts from (ds3-flags-seed.tsv beside the porter: build 1.0.667's numbers).
    /// <para>
    /// Numbers are handed out in the order the scripts first name a flag, so any change to what the porter reads, or
    /// in what order, gave most flags another number, while a save kept its bits where they were. PR #285 (placing
    /// ranges first) moved the whole table, and Devin's save then read as DS3 states no script can make: Andre and the
    /// Shrine Handmaid's state ranges had a stray bit on, so their events (14005660, 14005680) found no "is at
    /// Firelink" state and switched them off, eight people read as angry or hostile, and three one-of ranges had two
    /// flags on (1.0.667). A flag in the seed keeps its number for good; only flags the seed doesn't know get new ones.
    /// </para>
    /// </summary>
    public const string SeedFile = "ds3-flags-seed.tsv";

    /// <summary>Where <see cref="SeedFile"/> is read from (tests point it elsewhere or at nothing).</summary>
    public static string SeedPath { get; set; } = Path.Combine(AppContext.BaseDirectory, SeedFile);

    /// <summary>How many flag and event numbers this table took from the seed (0: read from the mod's own file, or no seed).</summary>
    public int Seeded { get; private set; }

    /// <summary>The mod's table, or the seed when the mod has none yet (a full run starts without one).</summary>
    public static FlagMap Load(string path)
    {
        var map = new FlagMap();
        bool seed = !File.Exists(path);
        if (seed)
            path = SeedPath;
        if (!File.Exists(path))
            return map;
        map.Read(path);
        if (seed)
            map.Seeded = map._flags.Count + map._events.Count;
        return map;
    }

    private void Read(string path)
    {
        var map = this;
        foreach (var line in File.ReadAllLines(path))
        {
            var p = line.Split('\t');
            if (p.Length < 3 || line.StartsWith('#') || !long.TryParse(p[1], out var ds3) || !long.TryParse(p[2], out var er))
                continue;
            if (p[0] == "flag")
            {
                map._flags[ds3] = er;
                map._used.Add(er);
            }
            else if (p[0] == "event")
                map._events[ds3] = er;
        }
    }

    public void Save(string path)
    {
        var lines = new List<string> { "# kind\tDS3\tElden Ring (written by DarkSignPorter port-maps; kept so saves stay valid across rebuilds)" };
        lines.AddRange(_flags.OrderBy(f => f.Value).Select(f => $"flag\t{f.Key}\t{f.Value}"));
        lines.AddRange(_events.OrderBy(f => f.Value).Select(f => $"event\t{f.Key}\t{f.Value}"));
        File.WriteAllLines(path, lines);
    }
}
