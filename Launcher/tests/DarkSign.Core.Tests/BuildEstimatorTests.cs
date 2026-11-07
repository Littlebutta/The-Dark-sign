using DarkSign.Core;

namespace DarkSign.Core.Tests;

public sealed class BuildEstimatorTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0);

    [Fact]
    public void First_build_uses_the_default_guess_and_says_so()
    {
        var e = new BuildEstimator(["weapons", "maps"], null, serial: false, T0);
        Assert.True(e.FirstBuild);
        e.Start("weapons", T0);
        e.Start("maps", T0);
        var (fraction, left) = e.Estimate(T0.AddSeconds(60));
        Assert.Equal(BuildEstimator.DefaultSeconds - 60, left!.Value, 1);
        Assert.Equal(60 / BuildEstimator.DefaultSeconds, fraction, 2);
    }

    [Fact]
    public void Parallel_parts_wait_for_the_slowest_and_serial_parts_add_up()
    {
        var history = new Dictionary<string, BuildStat>
        {
            ["weapons"] = new() { Seconds = 100 },
            ["maps"] = new() { Seconds = 300 },
        };
        var parallel = new BuildEstimator(["weapons", "maps"], history, serial: false, T0);
        Assert.Equal(300, parallel.Estimate(T0).SecondsLeft!.Value, 1);
        var serial = new BuildEstimator(["weapons", "maps"], history, serial: true, T0);
        Assert.Equal(400, serial.Estimate(T0).SecondsLeft!.Value, 1);
        Assert.False(serial.FirstBuild);
    }

    [Fact]
    public void Fraction_never_goes_back_and_reaches_one_only_when_all_parts_finish()
    {
        var history = new Dictionary<string, BuildStat> { ["weapons"] = new() { Seconds = 100, Lines = 100 } };
        var e = new BuildEstimator(["weapons"], history, serial: false, T0);
        e.Start("weapons", T0);
        for (var i = 0; i < 90; i++)
            e.Line("weapons", T0.AddSeconds(10));
        var ahead = e.Estimate(T0.AddSeconds(10)).Fraction;
        Assert.True(ahead > 0.1, $"lines ahead of last build should push the bar on ({ahead})");
        // Overrunning last build's time: "almost done", still below 100%.
        var (late, left) = e.Estimate(T0.AddSeconds(500));
        Assert.Null(left);
        Assert.True(late >= ahead && late < 1);
        e.Finish("weapons", T0.AddSeconds(510));
        Assert.Equal(1, e.Estimate(T0.AddSeconds(510)).Fraction);
        var stat = e.Results()["weapons"];
        Assert.Equal(510, stat.Seconds);
        Assert.Equal(90, stat.Lines);
    }

    [Fact]
    public void A_skipped_part_stops_counting()
    {
        var e = new BuildEstimator(["weapons", "maps"], null, serial: true, T0);
        e.Skip("maps");
        Assert.Equal(BuildEstimator.DefaultSeconds, e.Estimate(T0).SecondsLeft!.Value, 1);
        Assert.Equal("4:05", BuildEstimator.Format(TimeSpan.FromSeconds(245)));
        Assert.Equal("1:02:10", BuildEstimator.Format(new TimeSpan(1, 2, 10)));
    }

    [Fact]
    public void Old_settings_files_without_build_history_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dksign-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"LastBuild\": null}");
        try { Assert.NotNull(LauncherSettings.Load(path).LastBuild); }
        finally { File.Delete(path); }
    }
}
