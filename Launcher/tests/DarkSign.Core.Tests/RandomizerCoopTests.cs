using DarkSign.Core;

namespace DarkSign.Core.Tests;

public sealed class RandomizerCoopTests
{
    private static RandomizerSettings Sample() => new()
    {
        Enabled = true, Seed = 123_456_789_012, Entrances = true, Items = false, CrossGameItems = true, Bosses = false,
        Enemies = true, SharedShards = false, Dlc = true, Goal = "EldenBeast", CrossGameFogs = 0.25, CrossGameBosses = 0.6,
    };

    [Fact]
    public void CodeRoundTripsEverySetting()
    {
        var s = Sample();
        var code = RandomizerCoop.Encode(s);
        Assert.Matches("^[0-9A-Z]{4}(-[0-9A-Z]{1,4})+$", code);
        Assert.True(code.Length <= 30, code);
        var back = RandomizerCoop.Decode(code)!;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(s), System.Text.Json.JsonSerializer.Serialize(back));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(999_999_999_999L)]
    [InlineData(long.MaxValue)]
    public void AnySeedFits(long seed)
    {
        var s = Sample();
        s.Seed = seed;
        Assert.Equal(seed, RandomizerCoop.Decode(RandomizerCoop.Encode(s))!.Seed);
    }

    [Fact]
    public void TyposAreCaught()
    {
        var code = RandomizerCoop.Normalize(RandomizerCoop.Encode(Sample()));
        int caught = 0, total = 0;
        for (int i = 0; i < code.Length; i++)
            foreach (var c in "0123456789ABCDEFGHJKMNPQRSTVWXYZ")
            {
                if (c == code[i]) continue;
                total++;
                if (RandomizerCoop.Decode(code[..i] + c + code[(i + 1)..]) is null) caught++;
            }
        // A one-byte checksum misses about 1 in 256 random typos; the world ID check on the main window catches the rest.
        Assert.True(caught > total * 0.97, $"{caught}/{total}");
        Assert.Null(RandomizerCoop.Decode(""));
        Assert.Null(RandomizerCoop.Decode("not a code"));
        Assert.Null(RandomizerCoop.Decode(code + "0"));
        Assert.Null(RandomizerCoop.Decode(code[..^1]));
    }

    [Fact]
    public void JoinedWorldWinsOverTheProfilesOwn()
    {
        var friend = Sample();
        var profile = new LaunchProfile { Name = "Mine", Randomizer = new RandomizerSettings { Enabled = true, Seed = 5 } };
        Assert.Equal("Mine", RandomizerCoop.ActiveWorld(profile)!.Value.WorldName);
        profile.JoinCode = RandomizerCoop.Encode(friend);
        var active = RandomizerCoop.ActiveWorld(profile)!.Value;
        Assert.StartsWith("Co-op ", active.WorldName);
        Assert.Equal(friend.Seed, active.Settings.Seed);
        Assert.Equal(5, profile.Randomizer.Seed);
        profile.JoinCode = null;
        profile.Randomizer.Enabled = false;
        Assert.Null(RandomizerCoop.ActiveWorld(profile));
    }

    [Fact]
    public void CheckCodeCoversModAndWorld()
    {
        Assert.Equal("ABCD1234", RandomizerCoop.CheckCode("ABCD1234", null));
        var a = RandomizerCoop.CheckCode("ABCD1234", "00000001");
        Assert.NotEqual(a, RandomizerCoop.CheckCode("ABCD1234", "00000002"));
        Assert.NotEqual(a, RandomizerCoop.CheckCode("ABCD1235", "00000001"));
        Assert.Equal(a, RandomizerCoop.CheckCode("ABCD1234", "00000001"));
    }

    [Fact]
    public void WorldIdIsReadFromThePlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "dksign-coop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = RandomizerSettings.ProfileDir(root, "Co-op XYZ");
            Directory.CreateDirectory(dir);
            Assert.Null(RandomizerCoop.ReadWorldId(root, "Co-op XYZ"));
            File.WriteAllText(Path.Combine(dir, "plan.json"), "{ \"Version\": 1, \"WorldId\": \"1A2B3C4D\" }");
            Assert.Equal("1A2B3C4D", RandomizerCoop.ReadWorldId(root, "Co-op XYZ"));
            File.WriteAllText(Path.Combine(dir, "plan.json"), "{ broken");
            Assert.Null(RandomizerCoop.ReadWorldId(root, "Co-op XYZ"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
