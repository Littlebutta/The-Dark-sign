namespace DarkSign.Core;

/// <summary>How long one porter command took last time, and how many lines it printed.</summary>
public sealed class BuildStat
{
    public double Seconds { get; set; }
    public int Lines { get; set; }
}

/// <summary>
/// Estimates how far a content build has got and how long is left. The porter doesn't print "step 3 of 9", so the
/// estimate compares this build with the last one: how long each part (port-weapons, port-maps) took and how many
/// lines it printed. Before any build has finished, <see cref="DefaultSeconds"/> is used. Thread safe.
/// </summary>
public sealed class BuildEstimator
{
    /// <summary>A guess for a part with no history (a first build takes a few minutes).</summary>
    public const double DefaultSeconds = 240;

    private sealed class Part
    {
        public BuildStat? Last;
        public DateTime? Started;
        public DateTime? Finished;
        public int Lines;
    }

    private readonly Dictionary<string, Part> _parts = new();
    private readonly bool _serial;
    private readonly DateTime _started;
    private double _shown;

    /// <param name="parts">The parts, in run order.</param>
    /// <param name="history">Last build's stats by part name (may be missing some or all).</param>
    /// <param name="serial">The parts run one after another (Fallbacks "build-threads" B) instead of side by side.</param>
    public BuildEstimator(IEnumerable<string> parts, IReadOnlyDictionary<string, BuildStat>? history, bool serial, DateTime now)
    {
        foreach (var name in parts)
            _parts[name] = new Part { Last = history?.GetValueOrDefault(name) is { Seconds: > 0 } s ? s : null };
        _serial = serial;
        _started = now;
    }

    /// <summary>True when no part has a previous build to compare with, so the time left is only a guess.</summary>
    public bool FirstBuild { get { lock (_parts) return _parts.Values.All(p => p.Last is null); } }

    public void Start(string part, DateTime now) { lock (_parts) _parts[part].Started ??= now; }

    public void Line(string part, DateTime now)
    {
        lock (_parts)
        {
            var p = _parts[part];
            p.Started ??= now;
            p.Lines++;
        }
    }

    public void Finish(string part, DateTime now)
    {
        lock (_parts)
        {
            var p = _parts[part];
            p.Started ??= now;
            p.Finished ??= now;
        }
    }

    /// <summary>A part that won't run (it failed to start, or was skipped) no longer counts toward the time left.</summary>
    public void Skip(string part) { lock (_parts) _parts.Remove(part); }

    /// <summary>
    /// Fraction done (0..1, never goes backwards while the build runs) and the estimated seconds left
    /// (null when a part has run past its usual time and only "almost done" can be said).
    /// </summary>
    public (double Fraction, double? SecondsLeft) Estimate(DateTime now)
    {
        lock (_parts)
        {
            double? left = 0;
            foreach (var p in _parts.Values)
            {
                var partLeft = PartLeft(p, now);
                if (partLeft is null) { left = null; break; }
                left = _serial ? left + partLeft : Math.Max(left!.Value, partLeft.Value);
            }
            var elapsed = (now - _started).TotalSeconds;
            var fraction = left is { } l ? elapsed / Math.Max(1e-6, elapsed + l) : 0.97;
            if (_parts.Values.All(p => p.Finished is not null))
                fraction = 1;
            // While running, cap below 100% and never step back (a slower stretch only slows the bar).
            fraction = Math.Clamp(fraction, 0, _parts.Values.All(p => p.Finished is not null) ? 1 : 0.99);
            _shown = Math.Max(_shown, fraction);
            return (_shown, left);
        }
    }

    private static double? PartLeft(Part p, DateTime now)
    {
        if (p.Finished is not null)
            return 0;
        var expected = p.Last?.Seconds ?? DefaultSeconds;
        if (p.Started is null)
            return expected;
        var elapsed = (now - p.Started.Value).TotalSeconds;
        var timeLeft = expected - elapsed;
        // Lines printed against last time's count: a part that is ahead of (or behind) last build's pace adjusts.
        if (p.Last is { Lines: > 20 } last && p.Lines > 0)
        {
            var lineFraction = Math.Min(0.99, (double)p.Lines / last.Lines);
            if (lineFraction > 0.05)
                timeLeft = (timeLeft + elapsed * (1 - lineFraction) / lineFraction) / 2;
        }
        return timeLeft > 1 ? timeLeft : null;
    }

    /// <summary>This build's stats for the parts that finished, to compare the next build with.</summary>
    public Dictionary<string, BuildStat> Results()
    {
        lock (_parts)
            return _parts.Where(p => p.Value is { Started: not null, Finished: not null })
                .ToDictionary(p => p.Key, p => new BuildStat
                {
                    Seconds = Math.Round((p.Value.Finished!.Value - p.Value.Started!.Value).TotalSeconds, 1),
                    Lines = p.Value.Lines,
                });
    }

    /// <summary>"4:05" or "1:02:10".</summary>
    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
