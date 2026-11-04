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
}
