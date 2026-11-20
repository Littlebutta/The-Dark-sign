namespace DarkSign.Porter;

/// <summary>
/// How many cores the porter's parallel loops use: all of them by default. DARKSIGN_THREADS=1 makes every loop run
/// one item at a time (the way builds before 1.0.13x ran), to rule threading out when chasing a bad build.
/// </summary>
public static class Parallelism
{
    public static readonly int Threads =
        int.TryParse(Environment.GetEnvironmentVariable("DARKSIGN_THREADS"), out var n) && n > 0 ? n : Environment.ProcessorCount;

    public static ParallelOptions Options => new() { MaxDegreeOfParallelism = Threads };

    public static bool Serial => Threads == 1;

    /// <summary>
    /// How many maps port-maps ports side by side (MapTurns keeps the files the same as one at a time). A map's
    /// conversions already use every core, so more maps at once mostly fills the stretches where a map works on one
    /// core; each map in flight holds its models in memory, so the default allows one map per 4 cores and per 12 GB
    /// of memory, 4 at most. DARKSIGN_MAPS_AT_ONCE sets it (1 = one at a time, as builds up to 1.0.50x), and
    /// DARKSIGN_THREADS=1 means one at a time too.
    /// </summary>
    public static readonly int MapsAtOnce =
        int.TryParse(Environment.GetEnvironmentVariable("DARKSIGN_MAPS_AT_ONCE"), out var maps) && maps > 0 ? maps
        : Serial ? 1
        : DefaultMapsAtOnce(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    public static int DefaultMapsAtOnce(int cores, long memoryBytes) =>
        (int)Math.Clamp(Math.Min(cores / 4, memoryBytes / (12L << 30)), 1, 4);
}
