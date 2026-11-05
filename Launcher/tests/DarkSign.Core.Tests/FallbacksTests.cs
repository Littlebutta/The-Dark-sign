using System.Text.RegularExpressions;
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
    public void Stamp_signature_round_trips_and_one_core_runs_the_steps_one_after_another()
    {
        var methods = new Dictionary<string, string> { ["sounds"] = "B", ["two-hand"] = "off" };
        Assert.Equal(Fallbacks.BuildSignature(methods), Fallbacks.SignatureOfStamp(Fallbacks.BuildStamp("1.0.150", methods)));
        Assert.Equal("", Fallbacks.SignatureOfStamp("1.0.150"));
        Assert.Equal("", Fallbacks.SignatureOfStamp(null));
        Assert.False(Fallbacks.SerialBuild(methods));
        Assert.True(Fallbacks.SerialBuild(new Dictionary<string, string> { ["build-threads"] = "B" }));
    }

    [Fact]
    public void Mod_code_covers_the_build_methods()
    {
        var root = Path.Combine(Path.GetTempPath(), "dks-fb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var setup = new EldenRingSetup(root);
            Directory.CreateDirectory(setup.ModDir);
            File.WriteAllBytes(Path.Combine(setup.ModDir, "regulation.bin"), [1, 2, 3]);
            var plain = SeamlessCoop.Fingerprint(setup.ModDir);
            setup.MarkContentBuilt(Fallbacks.BuildStamp("1.0.150", new Dictionary<string, string>()));
            Assert.Equal(plain, SeamlessCoop.Fingerprint(setup.ModDir)); // all-A: the regulation hash, as before
            setup.MarkContentBuilt(Fallbacks.BuildStamp("1.0.150", new Dictionary<string, string> { ["anim-map"] = "B" }));
            var withB = SeamlessCoop.Fingerprint(setup.ModDir);
            Assert.NotEqual(plain, withB);
            Assert.Equal(8, withB!.Length);
            setup.MarkContentBuilt(Fallbacks.BuildStamp("1.0.151", new Dictionary<string, string> { ["anim-map"] = "B" }));
            Assert.Equal(withB, SeamlessCoop.Fingerprint(setup.ModDir)); // the porter version alone doesn't change it
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
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

public sealed class FallbacksDocTests
{
    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "fallbacks.md")))
            dir = dir.Parent;
        return dir;
    }

    private static Dictionary<string, string> Sources(DirectoryInfo root, string folder, params string[] patterns)
    {
        var dir = Path.Combine(root.FullName, folder);
        var sep = Path.DirectorySeparatorChar;
        return !Directory.Exists(dir) ? []
            : patterns.SelectMany(p => Directory.EnumerateFiles(dir, p, SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}third_party{sep}"))
                .Distinct()
                .ToDictionary(f => f, File.ReadAllText);
    }

    /// <summary>Dev-only switches the porter reads that are deliberately not in the Fallbacks window.</summary>
    private static readonly string[] NotInCatalog = ["DARKSIGN_WIDE_MOTION"];

    [Fact]
    public void Every_variable_and_argument_in_the_catalog_has_a_consumer_and_back()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var porter = Sources(root!, Path.Combine("Porter", "src"), "*.cs");
        var game = Sources(root!, Path.Combine("TestMenu", "src"), "*.c", "*.cpp")
            .Concat(Sources(root!, Path.Combine("SaveRedirect", "src"), "*.c", "*.cpp")).ToDictionary();
        if (porter.Count == 0 || game.Count == 0)
            return; // sources not next to the test (e.g. a packaged test run)

        // Porter variables: read with Environment.GetEnvironmentVariable("NAME"); a word value is compared in that file.
        var porterVars = Fallbacks.All.SelectMany(f => f.Options).SelectMany(o => o.PorterEnv)
            .Append(new("DARKSIGN_METHODS", "")).ToList();
        foreach (var (name, value) in porterVars)
        {
            var readers = porter.Where(s => s.Value.Contains($"GetEnvironmentVariable(\"{name}\")")).ToList();
            Assert.True(readers.Count > 0, $"no porter source reads {name}");
            if (value.Length > 0 && !value.All(char.IsDigit))
                Assert.True(readers.Any(r => r.Value.Contains($"\"{value}\"")), $"no reader of {name} compares it with \"{value}\"");
        }

        // Game variables: read by our DLLs with GetEnvironmentVariableW(L"NAME"); the flags are "1" (the DLLs test for '1').
        var gameVars = Fallbacks.All.SelectMany(f => f.Options).SelectMany(o => o.GameEnv).ToList();
        foreach (var name in new[] { Fallbacks.NoHotkeysVariable, Fallbacks.NoOverlayVariable, Fallbacks.DllSoundsVariable })
            gameVars.Add(new(name, "1"));
        foreach (var (name, value) in gameVars)
        {
            Assert.True(game.Values.Any(s => s.Contains($"L\"{name}\"")), $"no DLL source reads {name}");
            Assert.Equal("1", value);
        }
        Assert.Contains(game.Values, s => s.Contains($"L\"{Fallbacks.ModDirVariable}\""));

        // port-maps arguments: every flag and value is a string the porter's parsers know.
        foreach (var arg in Fallbacks.All.SelectMany(f => f.Options).SelectMany(o => o.MapsArgs).Distinct())
            Assert.True(porter.Values.Any(s => s.Contains($"\"{arg}\"")), $"port-maps doesn't know {arg}");

        // And back: every DARKSIGN_ variable the porter reads is set by some method (or is a known dev-only switch).
        var catalog = porterVars.Select(v => v.Key).ToHashSet();
        var read = porter.Values.SelectMany(s => Regex.Matches(s, "GetEnvironmentVariable\\(\"(DARKSIGN_[A-Z0-9_]+)\"\\)"))
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(read);
        foreach (var name in read)
            Assert.True(catalog.Contains(name) || NotInCatalog.Contains(name), $"the porter reads {name}, which no fallback method sets");
    }

    [Fact]
    public void Docs_name_every_variable_and_argument()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var doc = File.ReadAllText(Path.Combine(root!.FullName, "docs", "fallbacks.md"));
        foreach (var option in Fallbacks.All.SelectMany(f => f.Options))
        {
            foreach (var (k, v) in option.PorterEnv.Concat(option.GameEnv))
                Assert.Contains($"{k}={v}", doc);
            foreach (var arg in option.MapsArgs)
                Assert.Contains(arg, doc);
        }
    }

    [Fact]
    public void Docs_list_every_feature_and_option()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "fallbacks.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "fallbacks.md"));
        foreach (var feature in Fallbacks.All)
        {
            Assert.Contains($"(`{feature.Key}`", doc);
            foreach (var option in feature.Options)
                Assert.Contains($"**{option.Letter}: {option.Title}.**", doc);
        }
    }
}
