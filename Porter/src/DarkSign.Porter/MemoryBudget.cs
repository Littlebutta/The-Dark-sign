namespace DarkSign.Porter;

/// <summary>
/// Lets parallel work that needs a lot of memory per item run on every core without exhausting the PC: each item
/// takes its estimated bytes from the budget before it starts and gives them back when done. An item bigger than the
/// whole budget runs alone. Used for the map collision conversion, whose XML tagfiles (fallback "map-collision" B)
/// take about 800 times the DS3 file's size in memory while they are written (a 4.7 MB file peaks near 4 GB).
/// </summary>
public sealed class MemoryBudget(long bytes)
{
    private readonly object _gate = new();
    private long _used;

    public long Bytes { get; } = bytes;

    /// <summary>A share of the machine's memory: a third of what the runtime may use, at least 2 GB.</summary>
    public static MemoryBudget Share(double fraction = 1.0 / 3) =>
        new(Math.Max(2L << 30, (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * fraction)));

    public IDisposable Take(long estimate)
    {
        lock (_gate)
        {
            while (_used > 0 && _used + estimate > Bytes)
                Monitor.Wait(_gate);
            _used += estimate;
        }
        return new Lease(this, estimate);
    }

    private void Release(long estimate)
    {
        lock (_gate)
        {
            _used -= estimate;
            Monitor.PulseAll(_gate);
        }
    }

    private sealed class Lease(MemoryBudget budget, long estimate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                budget.Release(estimate);
        }
    }
}
