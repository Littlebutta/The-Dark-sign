using System.Diagnostics;

namespace DarkSign.Porter;

/// <summary>Prints how long a build stage took ("time: models 85.2 s"), to see where port-weapons spends its time.</summary>
public sealed class Timing : IDisposable
{
    private readonly string _stage;
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    private Timing(string stage) => _stage = stage;

    public static Timing Stage(string stage) => new(stage);

    public void Dispose() => Console.WriteLine($"time: {_stage} {_watch.Elapsed.TotalSeconds:F1} s");
}
