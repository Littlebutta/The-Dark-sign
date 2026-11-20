namespace DarkSign.Porter.Maps;

/// <summary>
/// Keeps the steps that touch what every map shares in the order a one-map-at-a-time run does them, while the maps
/// themselves port side by side (port-maps, 1.0.50x).
///
/// A map's port has two stretches that read and change the run's shared state: placing its enemies and people (the
/// stand-in chooser, the armour and talk plans, darksign_enemies.tsv), and everything from its textures and materials
/// through its collision, navmesh, objects, regions and boss arenas (the area's texture binders, the object port and
/// its numbering, HKLib, the arena list). What they write depends on which map came first, so each stretch is a
/// "turn": map N takes a turn only after every map before it has given that turn back, or has finished. The rest
/// (converting map pieces, lighting, sky, writing the layout) only touches the map's own files and runs freely.
///
/// The two turns don't share state with each other, so "map 2 places its enemies before map 1 ports its objects"
/// gives the same files as the serial order. With one map at a time nothing ever waits.
/// </summary>
public sealed class MapTurns(int maps)
{
    /// <summary>Layout, enemies and people, material templates.</summary>
    public const int Prepare = 0;
    /// <summary>Textures and materials, collision, navmesh, objects, regions, boss arenas.</summary>
    public const int Shared = 1;
    private const int Turns = 2;

    private readonly object _gate = new();
    // _given[turn] = how many maps, counted from the first, have given that turn back.
    private readonly bool[,] _given = new bool[Turns, maps];
    private readonly bool[] _done = new bool[maps];

    /// <summary>Waits until every earlier map has given this turn back.</summary>
    public void Take(int turn, int map)
    {
        lock (_gate)
        {
            while (!EarlierGave(turn, map))
                Monitor.Wait(_gate);
        }
    }

    public void Give(int turn, int map)
    {
        lock (_gate)
        {
            _given[turn, map] = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>The map is finished (ported or failed): it gives back every turn, taken or not.</summary>
    public void Done(int map)
    {
        lock (_gate)
        {
            _done[map] = true;
            for (int turn = 0; turn < Turns; turn++)
                _given[turn, map] = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Waits until the map is finished.</summary>
    public void WaitFor(int map)
    {
        lock (_gate)
        {
            while (!_done[map])
                Monitor.Wait(_gate);
        }
    }

    private bool EarlierGave(int turn, int map)
    {
        for (int earlier = 0; earlier < map; earlier++)
            if (!_given[turn, earlier])
                return false;
        return true;
    }

    /// <summary>
    /// Runs <paramref name="port"/> for every map, up to <paramref name="atOnce"/> side by side, each on its own
    /// thread. A map starts once the map <paramref name="atOnce"/> places before it is finished (so the earliest
    /// unfinished map is always running and nothing can wait on itself), and once every earlier map in
    /// <paramref name="after"/>[map] is (a map that copies another's files). <paramref name="port"/> must not throw.
    /// </summary>
    public void Run(int atOnce, Func<int, IEnumerable<int>> after, Action<int> port)
    {
        if (atOnce <= 1)
        {
            for (int map = 0; map < maps; map++)
            {
                try { port(map); }
                finally { Done(map); }
            }
            return;
        }
        var threads = new List<Thread>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        for (int i = 0; i < maps; i++)
        {
            int map = i;
            var thread = new Thread(() =>
            {
                try
                {
                    if (map - atOnce >= 0)
                        WaitFor(map - atOnce);
                    foreach (var earlier in after(map).Where(e => e < map))
                        WaitFor(earlier);
                    port(map);
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
                finally
                {
                    Done(map);
                }
            }) { IsBackground = true, Name = $"map {map}" };
            threads.Add(thread);
            thread.Start();
        }
        foreach (var thread in threads)
            thread.Join();
        if (!failures.IsEmpty)
            throw new AggregateException(failures);
    }
}
