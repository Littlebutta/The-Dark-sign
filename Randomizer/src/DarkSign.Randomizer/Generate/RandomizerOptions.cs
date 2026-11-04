using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Generate;

public enum Goal
{
    /// <summary>Beat both the Elden Beast and the Soul of Cinder.</summary>
    Both,
    EldenBeast,
    SoulOfCinder,
}

/// <summary>
/// Everything a profile chooses about its randomization. Saved per launcher profile, so each character's world
/// is its own; the same seed and options always build the same world.
/// </summary>
public sealed class RandomizerOptions
{
    public long Seed { get; set; }

    // Fog walls
    /// <summary>Shuffle where fog walls lead.</summary>
    public bool Entrances { get; set; } = true;
    public bool BossFogs { get; set; } = true;
    /// <summary>Elden Ring minor dungeon entrances (catacombs, caves, tunnels, hero's graves).</summary>
    public bool DungeonEntrances { get; set; } = true;
    /// <summary>DS3's PvP fog walls (the ones normally only seen when invaded).</summary>
    public bool Ds3PvpFogs { get; set; } = true;
    /// <summary>0 = a fog wall leads to its own game when it can, 1 = always to the other game when it can.</summary>
    public double CrossGameFogs { get; set; } = 0.5;

    // Items
    public bool Items { get; set; } = true;
    /// <summary>Let DS3 items turn up in Elden Ring locations and the other way round.</summary>
    public bool CrossGameItems { get; set; } = true;

    // Bosses and enemies
    public bool Bosses { get; set; } = true;
    /// <summary>0 = bosses stay in their own game when they can, 1 = always swap games when they can.</summary>
    public double CrossGameBosses { get; set; } = 0.6;
    /// <summary>Dungeon bosses swap with dungeon bosses, main bosses with main bosses.</summary>
    public bool KeepBossTiers { get; set; } = true;
    public bool Enemies { get; set; } = true;
    public double CrossGameEnemies { get; set; } = 0.5;

    // Progression
    /// <summary>Elden Ring's Great Rune bearers and DS3's Lords of Cinder count toward both games' gates:
    /// Leyndell needs any 2, the Firelink thrones any 4.</summary>
    public bool SharedShards { get; set; } = true;
    public Goal Goal { get; set; } = Goal.Both;
    /// <summary>Also require that every item location and every area can be reached (not just the goal).</summary>
    public bool EverythingReachable { get; set; } = true;
    public bool Dlc { get; set; } = true;

    // Content that exists in this player's build (set by the launcher/porter, not by the player)
    /// <summary>DS3 maps that have been ported into Elden Ring (null = all, for planning and tests).</summary>
    public IReadOnlyCollection<string>? PortedDs3Maps { get; set; }
    /// <summary>DS3 characters (c5110...) that the enemy port has brought into Elden Ring.</summary>
    public IReadOnlyCollection<string> PortedDs3Characters { get; set; } = [];

    public int MaxAttempts { get; set; } = 300;

    public RandomizerOptions Clone() => (RandomizerOptions)MemberwiseClone();

    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Reads the launcher's options.json (DarkSign.Core.RandomizerSettings; unknown keys are ignored).</summary>
    public static RandomizerOptions FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<RandomizerOptions>(json, Json) ?? new RandomizerOptions();
}
