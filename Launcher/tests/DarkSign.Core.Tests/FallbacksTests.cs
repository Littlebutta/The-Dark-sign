using DarkSign.Core;

namespace DarkSign.Core.Tests;

public sealed class FallbacksTests
{
    [Fact]
    public void Every_feature_starts_with_A_and_has_unique_keys_and_letters()
    {
        Assert.Equal(Fallbacks.All.Count, Fallbacks.All.Select(f => f.Key).Distinct().Count());
        foreach (var feature in Fallbacks.All)
        {
            Assert.Equal("A", feature.Options[0].Letter);
            Assert.True(feature.Options.Count >= 2, feature.Key);
            Assert.Equal(feature.Options.Count, feature.Options.Select(o => o.Letter).Distinct().Count());
            // A is the current behaviour: it sets nothing.
            Assert.Empty(feature.Options[0].PorterEnv);
            Assert.Empty(feature.Options[0].MapsArgs);
            Assert.Empty(feature.Options[0].GameEnv);
            Assert.Null(feature.Options[0].Launch);
        }
    }

    [Fact]
    public void Defaults_change_nothing_and_keep_the_old_build_stamp()
    {
        var none = new Dictionary<string, string>();
        Assert.Equal("", Fallbacks.BuildSignature(none));
        Assert.Equal("1.0.150", Fallbacks.BuildStamp("1.0.150", none));
        Assert.Equal(new[] { "DARKSIGN_METHODS" }, Fallbacks.PorterEnvironment(none).Keys);
        Assert.Empty(Fallbacks.MapsArguments(none));
        Assert.Equal(new Fallbacks.LaunchTweaks(), Fallbacks.Launch(none, coop: true));
        // Unknown keys or letters fall back to A.
        Assert.Equal("", Fallbacks.BuildSignature(new Dictionary<string, string> { ["nope"] = "B", ["sounds"] = "Z" }));
    }

    [Fact]
    public void Build_methods_reach_the_porter_and_change_the_stamp()
    {
        var methods = new Dictionary<string, string> { ["sounds"] = "B", ["map-collision"] = "C", ["loader"] = "C" };
        var env = Fallbacks.PorterEnvironment(methods);
        Assert.Equal("1", env["DARKSIGN_STREAM_SOUNDS"]);
        Assert.Equal(new[] { "--binary-collision", "--simd" }, Fallbacks.MapsArguments(methods));
        // The loader is a launch method: it doesn't rebuild.
        Assert.Equal("sounds=B map-collision=C", Fallbacks.BuildSignature(methods));
        Assert.Equal("1.0.150 [sounds=B map-collision=C]", Fallbacks.BuildStamp("1.0.150", methods));
        Assert.True(Fallbacks.Launch(methods, coop: false).ModEngine2);
    }

    [Fact]
    public void Seamless_methods_only_apply_in_coop()
    {
        var methods = new Dictionary<string, string> { ["seamless"] = "B" };
        Assert.False(Fallbacks.Launch(methods, coop: false).StartOnline);
        Assert.True(Fallbacks.Launch(methods, coop: true).StartOnline);
    }

    [Fact]
    public void Me3_profile_gets_the_launch_options_before_its_tables()
    {
        var text = EldenRingSetup.Me3Profile(@"C:\mod", [@"C:\a.dll"], new Fallbacks.LaunchTweaks(DisableArxan: true, StartOnline: true));
        Assert.True(text.IndexOf("disable_arxan = true", StringComparison.Ordinal) < text.IndexOf("[[supports]]", StringComparison.Ordinal));
        Assert.Contains("start_online = true", text);
        Assert.DoesNotContain("mem_patch", text);
        Assert.DoesNotContain("disable_arxan", EldenRingSetup.Me3Profile(@"C:\mod", []));
    }
}
