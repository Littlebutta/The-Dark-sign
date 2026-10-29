using System.Text.Json;
using DarkSign.Core;

namespace DarkSign.Core.Tests;

public sealed class SelfUpdaterTests
{
    private static JsonElement Releases(params (string tag, bool hasAsset, bool prerelease)[] items) =>
        JsonDocument.Parse(JsonSerializer.Serialize(items.Select(i => new
        {
            tag_name = i.tag,
            draft = false,
            prerelease = i.prerelease,
            assets = i.hasAsset ? new[] { new { name = SelfUpdater.AssetName, url = $"https://api/{i.tag}" } } : [],
        }))).RootElement;

    [Fact]
    public void Picks_newest_launcher_release_with_the_exe_attached()
    {
        var releases = Releases(("launcher-v1.0.3", true, false), ("launcher-v1.0.9", false, false),
            ("launcher-v1.0.7", true, false), ("launcher-v1.0.8", true, true), ("other-v9.0.0", true, false));

        var update = SelfUpdater.PickNewest(releases, new Version(1, 0, 2, 0));

        Assert.NotNull(update);
        Assert.Equal("launcher-v1.0.7", update.Tag);
        Assert.Equal("https://api/launcher-v1.0.7", update.AssetApiUrl);
    }

    [Fact]
    public void Returns_null_when_already_on_the_latest_version()
    {
        var releases = Releases(("launcher-v1.0.7", true, false));

        Assert.Null(SelfUpdater.PickNewest(releases, new Version(1, 0, 7, 0)));
    }
}
