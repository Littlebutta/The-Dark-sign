using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSign.Core;

/// <summary>
/// One profile's cross-game randomizer choices. Each profile has its own save folder, so each character gets its
/// own randomized world; the porter makes it once (TheDarkSign\Randomizer\&lt;profile&gt;) and switching profiles never
/// rebuilds anything. Property names match the porter's RandomizerOptions, which reads options.json.
/// </summary>
public sealed class RandomizerSettings
{
    public bool Enabled { get; set; }
    public long Seed { get; set; }
    public bool Entrances { get; set; } = true;
    public bool Items { get; set; } = true;
    public bool CrossGameItems { get; set; } = true;
    public bool Bosses { get; set; } = true;
    public bool Enemies { get; set; } = true;
    public bool SharedShards { get; set; } = true;
    public bool Dlc { get; set; } = true;
    public double CrossGameFogs { get; set; } = 0.5;
    public double CrossGameBosses { get; set; } = 0.6;
    /// <summary>"Both", "EldenBeast" or "SoulOfCinder".</summary>
    public string Goal { get; set; } = "Both";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>TheDarkSign\Randomizer\&lt;profile&gt; (same folder-name rule as the porter).</summary>
    public static string ProfileDir(string root, string profile)
    {
        var safe = string.Concat(profile.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dir = Path.Combine(root, "TheDarkSign", "Randomizer", safe.Length == 0 ? "Default" : safe);
        PathGuard.EnsureInsideRoot(dir, root);
        return dir;
    }

    /// <summary>Writes options.json for the porter's randomize command and returns its path.</summary>
    public string WriteOptions(string root, string profile)
    {
        var dir = ProfileDir(root, profile);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "options.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        return path;
    }

    public static string SpoilerPath(string root, string profile) => Path.Combine(ProfileDir(root, profile), "spoiler.txt");

    /// <summary>A fresh random seed (12 digits, easy to read out to a friend).</summary>
    public static long NewSeed() => Random.Shared.NextInt64(100_000_000_000L, 1_000_000_000_000L);

    [JsonIgnore]
    public string Summary => !Enabled ? "off" :
        $"seed {Seed}: " + string.Join(", ", new[]
        {
            Entrances ? "fog walls" : null, Items ? (CrossGameItems ? "items (both games)" : "items") : null,
            Bosses ? "bosses" : null, Enemies ? "enemies" : null, SharedShards ? "shared Great Runes/Cinders" : null,
        }.Where(s => s is not null));
}
