using DarkSign.Randomizer.Generate;
using DarkSign.Randomizer.Logic;
using Xunit;
using Xunit.Abstractions;

namespace DarkSign.Randomizer.Tests;

public class ExprTests
{
    [Theory]
    [InlineData("a", "a", true)]
    [InlineData("AND a b", "a", false)]
    [InlineData("AND a b", "a b", true)]
    [InlineData("OR a b", "b", true)]
    [InlineData("2 OF a b c", "a c", true)]
    [InlineData("2 OF a b c", "c", false)]
    [InlineData("AND a (OR b c)", "a c", true)]
    [InlineData("AND a (OR b c)", "b c", false)]
    [InlineData("AND x (2 OF a b c)", "x a b", true)]
    public void ParsesAndEvaluates(string text, string owned, bool expected)
    {
        var have = owned.Split(' ').ToHashSet();
        Assert.Equal(expected, Expr.Parse(text)!.Eval(have.Contains));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("AND")]
    [InlineData("AND a (OR b")]
    public void RejectsBadConditions(string text) => Assert.ThrowsAny<FormatException>(() => Expr.Parse(text));
}

public class RandomizerTests
{
    private readonly ITestOutputHelper _out;
    public RandomizerTests(ITestOutputHelper output) => _out = output;

    /// <summary>Seeds checked per preset; set DARKSIGN_RANDO_SEEDS=2000 for a long stress run.</summary>
    private static int SeedsPerPreset =>
        int.TryParse(Environment.GetEnvironmentVariable("DARKSIGN_RANDO_SEEDS"), out var n) ? n : 20;

    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "randomizer-data");

    /// <summary>Elden Ring's item locations come from the game files on the player's PC; tests use three
    /// stand-ins per area.</summary>
    internal static World LoadWorld(RandomizerOptions o, List<string>? log = null)
    {
        var world = WorldLoader.Load(o, DataDir, log);
        foreach (var area in world.Areas.Values.Where(a => a.Game == SourceGame.ER).ToList())
            for (int i = 0; i < 3; i++)
                world.Locations.Add(new ItemLocation { Id = $"{area.Id}#test{i}", Game = SourceGame.ER, Area = area.Id, Text = "test location" });
        return world;
    }

    [Fact]
    public void WorldDataLoads()
    {
        var log = new List<string>();
        var world = LoadWorld(new RandomizerOptions(), log);
        foreach (var l in log) _out.WriteLine(l);
        _out.WriteLine($"{world.Areas.Count} areas, {world.Entrances.Count} entrances, {world.KeyItems.Count} key items, " +
            $"{world.Locations.Count} locations, {world.Bosses.Count} bosses, {world.Enemies.Count} enemies");
        Assert.True(world.Areas.Count > 150);
        Assert.Contains("ds3:kiln_soulofcinder", world.Areas.Keys);
        Assert.Contains("er:ashen_eldenbeast", world.Areas.Keys);
        Assert.True(world.Locations.Count(l => l.Game == SourceGame.DS3) > 900);
    }

    [Fact]
    public void VanillaWorldIsCompletable()
    {
        var o = new RandomizerOptions { Entrances = false, Items = false, Bosses = false, Enemies = false };
        var world = LoadWorld(o);
        var plan = Generator.Generate(world, o);
        Assert.Null(Generator.Verify(world, plan));
        Assert.Equal(0, plan.Attempt);
    }

    public static IEnumerable<object[]> Presets()
    {
        yield return ["everything", new RandomizerOptions()];
        yield return ["no shared shards", new RandomizerOptions { SharedShards = false }];
        yield return ["items within each game", new RandomizerOptions { CrossGameItems = false }];
        yield return ["fog walls only", new RandomizerOptions { Items = false }];
        yield return ["items only", new RandomizerOptions { Entrances = false }];
        yield return ["all cross-game", new RandomizerOptions { CrossGameFogs = 1, CrossGameBosses = 1 }];
        yield return ["no DLC", new RandomizerOptions { Dlc = false }];
        yield return ["Elden Beast goal", new RandomizerOptions { Goal = Goal.EldenBeast }];
        yield return ["ported maps today", new RandomizerOptions { PortedDs3Maps = ["m40_00_00_00", "m30_00_00_00", "m31_00_00_00"] }];
    }

    /// <summary>The promise to the player: every seed can be finished. Many seeds per preset, each
    /// re-verified from its plan alone.</summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void EverySeedIsCompletable(string name, RandomizerOptions preset)
    {
        var attempts = new List<int>();
        for (long seed = 1; seed <= SeedsPerPreset; seed++)
        {
            var o = preset.Clone();
            o.Seed = seed * 7919;
            var world = LoadWorld(o);
            var plan = Generator.Generate(world, o);
            var problem = Generator.Verify(LoadWorld(o), plan);
            Assert.True(problem is null, $"{name}, seed {o.Seed}: {problem}");
            attempts.Add(plan.Attempt);
        }
        _out.WriteLine($"{name}: average {attempts.Average():F1} rerolls, max {attempts.Max()}");
    }

    [Fact(Skip = "diagnostic: lists why seeds were rerolled")]
    public void RerollReasons()
    {
        var reasons = new Dictionary<string, int>();
        for (long seed = 1; seed <= 30; seed++)
        {
            var o = new RandomizerOptions { Seed = seed, PortedDs3Maps = ["m40_00_00_00", "m30_00_00_00", "m31_00_00_00"] };
            var log = new List<string>();
            Generator.Generate(LoadWorld(o, log), o, null, reasons);
            if (seed == 1) foreach (var l in log) _out.WriteLine(l);
        }
        foreach (var (k, v) in reasons.OrderByDescending(kv => kv.Value).Take(25))
            _out.WriteLine($"{v}\t{k}");
    }

    [Fact]
    public void SameSeedSameWorld()
    {
        var o = new RandomizerOptions { Seed = 424242 };
        var a = Generator.Generate(LoadWorld(o), o).ToJson();
        var b = Generator.Generate(LoadWorld(o.Clone()), o.Clone()).ToJson();
        Assert.Equal(a, b);
        var c = new RandomizerOptions { Seed = 424243 };
        Assert.NotEqual(a, Generator.Generate(LoadWorld(c), c).ToJson());
    }

    [Fact]
    public void FogWallsCrossGamesAndEveryKeyIsPlacedOnce()
    {
        var o = new RandomizerOptions { Seed = 99 };
        var world = LoadWorld(o);
        var plan = Generator.Generate(world, o);
        Assert.Contains(plan.Entrances, e => e.CrossGame);
        foreach (var k in ItemPlacer.MovableKeys(world, o))
            Assert.Single(plan.Items, kv => kv.Value == "key:" + k.Id);
        // Every location holds exactly one thing.
        Assert.Equal(world.Locations.Count, plan.Items.Count);
        _out.WriteLine(Generator.Spoiler(world, plan)[..3000]);
    }

    [Fact]
    public void BossesFitTheirArenasAndFixedBossesStay()
    {
        var o = new RandomizerOptions { Seed = 5 };
        var world = LoadWorld(o);
        var plan = Generator.Generate(world, o);
        var bosses = world.Bosses.ToDictionary(b => b.Id);
        var arenaSize = world.Bosses.GroupBy(b => b.Area).ToDictionary(g => g.Key, g => g.Max(b => b.Size));
        foreach (var p in plan.Bosses)
        {
            var boss = bosses[p.Boss];
            Assert.False(boss.Fixed, $"{boss.Id} is fixed but moved");
            Assert.True(boss.Size <= arenaSize[p.Arena], $"{boss.Id} too big for {p.Arena}");
            Assert.True(boss.Available);
        }
        // Fixed Elden Ring bosses keep their arenas.
        Assert.DoesNotContain(plan.Bosses, p => p.Arena == "er:ashen_eldenbeast");
        // Ported DS3 arenas whose bosses aren't in Elden Ring yet get Elden Ring bosses.
        Assert.Contains(plan.Bosses, p => p.ArenaGame == "DS3" && p.BossGame == "ER");
    }

    /// <summary>A plan whose key was put behind itself must be rejected by the verifier.</summary>
    [Fact]
    public void VerifierCatchesAKeyBehindItsOwnDoor()
    {
        var o = new RandomizerOptions { Seed = 3, Entrances = false };
        var world = LoadWorld(o);
        var plan = Generator.Generate(world, o);
        var lockedLoc = world.Locations.First(l => l.Area == "er:raya_lucaria");
        var keyLoc = plan.Items.First(kv => kv.Value == "key:er:academy_glintstone_key").Key;
        plan.Items[keyLoc] = plan.Items[lockedLoc.Id];
        plan.Items[lockedLoc.Id] = "key:er:academy_glintstone_key";
        Assert.NotNull(Generator.Verify(LoadWorld(o), plan));
    }

    [Fact]
    public void SameWorldOnAnyPcWhateverItsLanguage()
    {
        // Co-op partners build the host's world on their own PCs, so the result must not depend on the PC's culture.
        var o = new RandomizerOptions { Seed = 777 };
        string Make()
        {
            var plan = Generator.Generate(LoadWorld(o.Clone()), o.Clone());
            plan.Seal();
            return plan.ToJson();
        }
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        string expected;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            expected = Make();
            foreach (var name in new[] { "tr-TR", "de-DE", "ja-JP", "ar-SA" })
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);
                Assert.Equal(expected, Make());
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void WorldIdChangesWithTheWorld()
    {
        var o = new RandomizerOptions { Seed = 31337 };
        var a = Generator.Generate(LoadWorld(o), o);
        var id = a.Seal();
        Assert.Matches("^[0-9A-F]{8}$", id);
        Assert.Equal(id, RandoPlan.FromJson(a.ToJson()).Seal());
        var b = Generator.Generate(LoadWorld(o.Clone()), o.Clone());
        Assert.Equal(id, b.Seal());
        b.Notes.Add("changed");
        Assert.NotEqual(id, b.Seal());
    }

    [Fact]
    public void FriendsCoopCodeMakesTheHostsWorld()
    {
        var host = new DarkSign.Core.RandomizerSettings
        {
            Enabled = true, Seed = 418_902_337_115, Enemies = false, Dlc = false, Goal = "SoulOfCinder", CrossGameFogs = 0.3,
        };
        var code = DarkSign.Core.RandomizerCoop.Encode(host);
        _out.WriteLine(code);
        // Read out loud or retyped: lower case, spaces, O for 0 and l for 1 still work.
        var typed = code.ToLowerInvariant().Replace("-", " ").Replace('0', 'o').Replace('1', 'l');
        var friend = DarkSign.Core.RandomizerCoop.Decode(typed);
        Assert.NotNull(friend);
        var root = Path.Combine(Path.GetTempPath(), "dksign-rando-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var mine = RandomizerOptions.FromJson(File.ReadAllText(host.WriteOptions(root, "Host")));
            var theirs = RandomizerOptions.FromJson(File.ReadAllText(friend!.WriteOptions(root, DarkSign.Core.RandomizerCoop.GuestWorldName(code))));
            var a = Generator.Generate(LoadWorld(mine), mine);
            var b = Generator.Generate(LoadWorld(theirs), theirs);
            Assert.Equal(a.Seal(), b.Seal());
            Assert.Equal(a.ToJson(), b.ToJson());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LauncherSettingsReachTheRandomizer()
    {
        var root = Path.Combine(Path.GetTempPath(), "dksign-rando-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var s = new DarkSign.Core.RandomizerSettings
            {
                Enabled = true, Seed = 123456789012, Entrances = false, CrossGameItems = false, SharedShards = false,
                Dlc = false, Goal = "EldenBeast", CrossGameFogs = 0.25,
            };
            var path = s.WriteOptions(root, "Devin's run");
            var o = RandomizerOptions.FromJson(File.ReadAllText(path));
            Assert.Equal(123456789012, o.Seed);
            Assert.False(o.Entrances);
            Assert.False(o.CrossGameItems);
            Assert.False(o.SharedShards);
            Assert.False(o.Dlc);
            Assert.True(o.Items);
            Assert.Equal(Goal.EldenBeast, o.Goal);
            Assert.Equal(0.25, o.CrossGameFogs);
            Assert.EndsWith(Path.Combine("TheDarkSign", "Randomizer", "Devin's run", "options.json"), path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SeedFromTextIsStable()
    {
        Assert.Equal(Rng.SeedFromText("Devin's run"), Rng.SeedFromText(" Devin's run "));
        Assert.Equal(new Rng(1).NextULong(), new Rng(1).NextULong());
    }
}
