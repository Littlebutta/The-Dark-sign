namespace DarkSign.Porter;

/// <summary>
/// How many cores the porter's parallel loops use: all of them.
/// </summary>
public static class Parallelism
{
    public static readonly int Threads = Environment.ProcessorCount;

    public static ParallelOptions Options => new() { MaxDegreeOfParallelism = Threads };

    /// <summary>
    /// How many maps port-maps ports side by side (MapTurns keeps the files the same as one at a time). A map's
    /// conversions already use every core, so more maps at once mostly fills the stretches where a map works on one
    /// core; each map in flight holds its models in memory, so the default allows one map per 4 cores and per 12 GB
    /// of memory, 4 at most.
    /// </summary>
    public static readonly int MapsAtOnce =
        DefaultMapsAtOnce(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    public static int DefaultMapsAtOnce(int cores, long memoryBytes) =>
        (int)Math.Clamp(Math.Min(cores / 4, memoryBytes / (12L << 30)), 1, 4);
}
