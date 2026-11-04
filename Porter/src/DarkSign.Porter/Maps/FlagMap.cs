namespace DarkSign.Porter.Maps;

/// <summary>
/// Where Dark Souls III's global event flags (NPC progress, items picked up, bosses, shortcuts: anything not tied to
/// one map) live in Elden Ring. DS3's numbers would collide with Elden Ring's own flags, so each one gets a new flag
/// in the blocks of the empty sea tiles along the bottom edge of the world, the way JortPob allocates its flags:
/// tiles m60_30_29 to m60_38_29 (1030290000 to 1038290000), numbers 0-999 of each block (saved with the game).
/// Tile 39 is the run's own (<see cref="RunEvents"/>). Ported common events take numbers 1000-1999 of the same
/// blocks (JortPob's event range), counting down from tile 38.
///
/// The assignments are kept in a file next to the mod and reused on every rebuild, so a save made with one build
/// keeps its DS3 progress in the next.
/// </summary>
public sealed class FlagMap
{
    public const long FirstTile = 1030290000, TileStep = 1_000_000;
    public const int Tiles = 9, PerTile = 1000;

    private readonly Dictionary<long, long> _flags = new();
    private readonly Dictionary<long, long> _events = new();
    private int _nextFlag, _nextEvent;

    public IReadOnlyDictionary<long, long> Flags => _flags;
    public IReadOnlyDictionary<long, long> Events => _events;
    public int Capacity => Tiles * PerTile;

    public static long FlagSlot(int n) => FirstTile + n / PerTile * TileStep + n % PerTile;
    public static long EventSlot(int n) => FirstTile + (Tiles - 1 - n / PerTile) * TileStep + 1000 + n % PerTile;

    /// <summary>The Elden Ring flag for a DS3 global flag (a new one the first time).</summary>
    public long Flag(long ds3)
    {
        if (ds3 <= 0)
            return ds3;
        if (_flags.TryGetValue(ds3, out var er))
            return er;
        while (_flags.ContainsValue(FlagSlot(_nextFlag)))
            _nextFlag++;
        if (_nextFlag >= Capacity)
            throw new InvalidOperationException($"no room left for DS3 flag {ds3} ({Capacity} used)");
        return _flags[ds3] = FlagSlot(_nextFlag++);
    }

    /// <summary>
    /// Flags used as a range (batch checks of start..end): the whole range gets consecutive Elden Ring flags in one
    /// block, so "every flag from A to B" still means the same flags. Returns the new start, or -1 if some of the
    /// range was already placed elsewhere.
    /// </summary>
    public long Range(long start, long end)
    {
        if (start <= 0 || end < start)
            return -1;
        if (_flags.TryGetValue(start, out var first))
            return Enumerable.Range(0, (int)(end - start + 1)).All(i => _flags.TryGetValue(start + i, out var v) && v == first + i) ? first : -1;
        int count = (int)(end - start + 1);
        if (count > PerTile)
            return -1;
        // Start at the next free number whose block has room for the whole range.
        int n = _nextFlag;
        while (n % PerTile + count > PerTile || Enumerable.Range(n, count).Any(i => _flags.ContainsValue(FlagSlot(i))))
            n++;
        if (n + count > Capacity)
            return -1;
        for (int i = 0; i < count; i++)
        {
            if (_flags.ContainsKey(start + i))
                return -1;
            _flags[start + i] = FlagSlot(n + i);
        }
        if (n == _nextFlag)
            _nextFlag += count;
        return FlagSlot(n);
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

    public static FlagMap Load(string path)
    {
        var map = new FlagMap();
        if (!File.Exists(path))
            return map;
        foreach (var line in File.ReadAllLines(path))
        {
            var p = line.Split('\t');
            if (p.Length < 3 || line.StartsWith('#') || !long.TryParse(p[1], out var ds3) || !long.TryParse(p[2], out var er))
                continue;
            if (p[0] == "flag")
                map._flags[ds3] = er;
            else if (p[0] == "event")
                map._events[ds3] = er;
        }
        return map;
    }

    public void Save(string path)
    {
        var lines = new List<string> { "# kind\tDS3\tElden Ring (written by DarkSignPorter port-maps; kept so saves stay valid across rebuilds)" };
        lines.AddRange(_flags.OrderBy(f => f.Value).Select(f => $"flag\t{f.Key}\t{f.Value}"));
        lines.AddRange(_events.OrderBy(f => f.Value).Select(f => $"event\t{f.Key}\t{f.Value}"));
        File.WriteAllLines(path, lines);
    }
}
