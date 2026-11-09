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
    /// Elden Ring legacy-map IDs (entities, events and map flags alike) are AABBLLLL: 4500xxxx in m45_00, 1000xxxx in
    /// m10_00 (Stormveil). The last four digits are the map-local number.
    /// </summary>
    public int EntityBase => Area * 1_000_000 + Block * 10_000;

    /// <summary>Dark Souls III entity IDs are 7 digits, AABLLLL: 4000800 is Iudex Gundyr in m40_00, 3010800 is in m30_01.</summary>
    public int Ds3EntityBase => Area * 100_000 + Block * 10_000;

    /// <summary>Dark Souls III map event IDs and map flags are 1AABLLLL: 14000800 (Iudex Gundyr defeated), 13000000.</summary>
    public int Ds3FlagBase => 10_000_000 + Ds3EntityBase;

    /// <summary>
    /// A DS3 ID of this map (an entity AABLLLL, or an event or flag 1AABLLLL) moved to an Elden Ring map
    /// (AABBLLLL), keeping the map-local number; any other ID is returned unchanged. The 8-digit AABBLLLL form is
    /// moved too, for IDs already in Elden Ring's numbering.
    /// </summary>
    public int MoveEntity(int id, MapId to)
    {
        if (id <= 0)
            return id;
        foreach (var b in new[] { Ds3EntityBase, Ds3FlagBase, EntityBase })
            if (id >= b && id < b + 10_000)
                return to.EntityBase + (id - b);
        return id;
    }

    /// <summary>True when the ID is one of this DS3 map's entities, events or flags.</summary>
    public bool OwnsDs3Id(long id) =>
        new[] { Ds3EntityBase, Ds3FlagBase }.Any(b => id >= b && id < b + 10_000);
}

/// <summary>
/// Which Elden Ring map slot hosts each Dark Souls III map. Elden Ring loads maps by ID, and the executable keeps a
/// table of the IDs it ships: a map ID outside it crashes the game at boot the moment the map registry
/// (<see cref="MapRegistry"/>) names it (1.0.238, 2026-11-09: access violation at eldenring.exe+0xAED000 on a
/// null per-map entry, twice, before any character loaded), and an ID the registry doesn't name sits on the loading
/// screen for ever (1.0.230). JortPob, the Morrowind port, only reuses existing IDs for the same reason. So ported
/// maps take over IDs the game ships and the single-player game never visits: the three colosseum arenas
/// (m45_00-02), reached only through online matchmaking, or (method B) the registry's file-less IDs.
/// </summary>
public static class MapSlots
{
    /// <param name="Template">
    /// The Elden Ring map whose files give the formats and template parts when <paramref name="Er"/> is a map ID
    /// Elden Ring doesn't ship (null: <paramref name="Er"/> itself).
    /// </param>
    /// <param name="Probe">A test slot: the same DS3 map again under another map ID. None is built by default since
    /// 2026-11-09: the m50 probe's registry line crashed the game at boot (see the class summary).</param>
    public sealed record Slot(MapId Ds3, MapId Er, string Name, MapId? Template = null, bool Probe = false)
    {
        public MapId Host => Template ?? Er;
    }

    /// <summary>
    /// The slots in use: <see cref="Colosseums"/>, or <see cref="NewIds"/> when DARKSIGN_MAP_SLOTS=new (launcher
    /// fallback "map-slots" B).
    /// </summary>
    public static IReadOnlyList<Slot> All => _all ??=
        string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_MAP_SLOTS"), "new", StringComparison.OrdinalIgnoreCase)
            ? NewIdSlots() : Colosseums;

    private static IReadOnlyList<Slot>? _all;

    /// <summary>
    /// Fallback: every DS3 map under a map ID of its own, built from the colosseum's formats. For when the colosseum
    /// slots won't load or their own settings get in the way. The IDs are ones Elden Ring's map registry names but
    /// ships no map for (the Souls Modding map list calls them "Unknown": m11_70, m11_72, m19_70), because an ID
    /// outside the executable's table crashes the game at boot (the m50 probe, 1.0.238). Untested in game. Each
    /// map's entities, events and flags move to that ID's range.
    /// </summary>
    public static IReadOnlyList<Slot> NewIdSlots() =>
    [
        new(MapId.Parse("m40_00_00_00"), MapId.Parse("m11_70_00_00"), "Firelink Shrine / Cemetery of Ash", Template: MapId.Parse("m45_00_00_00")),
        new(MapId.Parse("m30_00_00_00"), MapId.Parse("m11_72_00_00"), "High Wall of Lothric", Template: MapId.Parse("m45_01_00_00")),
        new(MapId.Parse("m31_00_00_00"), MapId.Parse("m19_70_00_00"), "Undead Settlement", Template: MapId.Parse("m45_02_00_00")),
    ];

    /// <summary>
    /// The new-ID slots builds up to 1.0.238 wrote (m50-m52, IDs the game doesn't ship): still cleaned out of the mod
    /// folder by a full run, so a mod folder built before 2026-11-09 doesn't keep the files that crashed the boot.
    /// </summary>
    public static IReadOnlyList<Slot> LegacyNewIdSlots() =>
    [
        new(MapId.Parse("m40_00_00_00"), MapId.Parse("m50_00_00_00"), "Firelink Shrine / Cemetery of Ash", Template: MapId.Parse("m45_00_00_00")),
        new(MapId.Parse("m30_00_00_00"), MapId.Parse("m51_00_00_00"), "High Wall of Lothric", Template: MapId.Parse("m45_01_00_00")),
        new(MapId.Parse("m31_00_00_00"), MapId.Parse("m52_00_00_00"), "Undead Settlement", Template: MapId.Parse("m45_02_00_00")),
    ];

    public static readonly IReadOnlyList<Slot> Colosseums =
    [
        new(MapId.Parse("m40_00_00_00"), MapId.Parse("m45_00_00_00"), "Firelink Shrine / Cemetery of Ash"),
        new(MapId.Parse("m30_00_00_00"), MapId.Parse("m45_01_00_00"), "High Wall of Lothric"),
        new(MapId.Parse("m31_00_00_00"), MapId.Parse("m45_02_00_00"), "Undead Settlement"),
        // No m50 probe any more: registering an ID the game doesn't ship crashed every boot (1.0.238).
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

    public static Slot? ForDs3(MapId ds3) => All.FirstOrDefault(s => s.Ds3 == ds3 && !s.Probe);
}
