using System.Text.RegularExpressions;

namespace DarkSign.Porter.Maps;

/// <summary>A FromSoftware map ID, mAA_BB_CC_DD (area, block, region, index).</summary>
public readonly record struct MapId(int Area, int Block, int Region, int Index)
{
    public static MapId Parse(string text)
    {
        var m = Regex.Match(text, @"m?(\d\d)_(\d\d)_(\d\d)_(\d\d)");
        if (!m.Success)
            throw new FormatException($"not a map ID: {text}");
        return new MapId(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            int.Parse(m.Groups[4].Value));
    }

    /// <summary>"m40_00_00_00".</summary>
    public override string ToString() => $"m{Area:D2}_{Block:D2}_{Region:D2}_{Index:D2}";

    /// <summary>"40_00_00_00", the ID without its letter (collision binders are h40_00_00_00.hkxbhd).</summary>
    public string Digits => ToString()[1..];

    /// <summary>"m40", the area folder (textures: map/m40/m40_0000.tpfbhd).</summary>
    public string AreaFolder => $"m{Area:D2}";

    /// <summary>
    /// Entity IDs of a legacy map start with its area and block: 4000xxxx in DS3's m40_00, 4500xxxx in Elden Ring's
    /// m45_00. Moving a map moves its IDs: the last four digits stay, the prefix becomes the host's.
    /// </summary>
    public int EntityBase => Area * 1_000_000 + Block * 10_000;

    /// <summary>The ID moved from this map to another, or the ID unchanged when it isn't one of this map's.</summary>
    public int MoveEntity(int id, MapId to)
    {
        if (id <= 0)
            return id;
        // Legacy-map IDs: AABBxxxx (8 digits), with a few maps using AABB0xxxx-style 9-digit ranges for generated
        // entities; only the 8-digit form is moved.
        if (id >= EntityBase && id < EntityBase + 10_000)
            return to.EntityBase + (id - EntityBase);
        return id;
    }
}

/// <summary>
/// Which Elden Ring map slot hosts each Dark Souls III map. Elden Ring loads maps by ID, and whether the game
/// accepts IDs it doesn't ship is unconfirmed (JortPob, the Morrowind port, only reuses existing IDs), so ported
/// maps take over existing slots the single-player game never visits: the three colosseum arenas (m45_00-02), which
/// are only reached through online colosseum matchmaking.
/// </summary>
public static class MapSlots
{
    public sealed record Slot(MapId Ds3, MapId Er, string Name);

    public static readonly IReadOnlyList<Slot> All =
    [
        new(MapId.Parse("m40_00_00_00"), MapId.Parse("m45_00_00_00"), "Firelink Shrine / Cemetery of Ash"),
        new(MapId.Parse("m30_00_00_00"), MapId.Parse("m45_01_00_00"), "High Wall of Lothric"),
        new(MapId.Parse("m31_00_00_00"), MapId.Parse("m45_02_00_00"), "Undead Settlement"),
    ];

    /// <summary>Every DS3 map the single-player game uses, for reports of what still has no slot.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Ds3Maps =
    [
        ("m30_00_00_00", "High Wall of Lothric"), ("m30_01_00_00", "Lothric Castle"),
        ("m31_00_00_00", "Undead Settlement"), ("m32_00_00_00", "Archdragon Peak"),
        ("m33_00_00_00", "Road of Sacrifices / Farron Keep"), ("m34_01_00_00", "Grand Archives"),
        ("m35_00_00_00", "Cathedral of the Deep"), ("m37_00_00_00", "Irithyll of the Boreal Valley"),
        ("m38_00_00_00", "Catacombs of Carthus / Smouldering Lake"), ("m39_00_00_00", "Irithyll Dungeon"),
        ("m40_00_00_00", "Firelink Shrine / Cemetery of Ash"), ("m41_00_00_00", "Kiln of the First Flame"),
        ("m45_00_00_00", "Painted World of Ariandel"), ("m50_00_00_00", "The Dreg Heap"),
        ("m51_00_00_00", "The Ringed City"), ("m51_01_00_00", "Filianore's Rest"),
    ];

    public static Slot? ForDs3(MapId ds3) => All.FirstOrDefault(s => s.Ds3 == ds3);
}
