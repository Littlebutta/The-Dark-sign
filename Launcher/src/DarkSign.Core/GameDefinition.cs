namespace DarkSign.Core;

public enum GameId
{
    DarkSoulsRemastered,
    DarkSouls3,
    Bloodborne,
    EldenRing,
}

/// <summary>Static facts about one supported game: how to find it on disk and how to launch it.</summary>
public sealed record GameDefinition(
    GameId Id,
    string DisplayName,
    string ShortName,
    IReadOnlyList<string> ExeNames,
    IReadOnlyList<string> FolderHints,
    int? SteamAppId,
    string VanillaSaveExtension)
{
    public static readonly IReadOnlyList<GameDefinition> All =
    [
        new(GameId.DarkSoulsRemastered, "Dark Souls Remastered", "DSR",
            ["DarkSoulsRemastered.exe"],
            ["dark souls remastered", "darksoulsremastered", "dsr", "ds1"],
            570940, ".sl2"),
        new(GameId.DarkSouls3, "Dark Souls III", "DS3",
            ["DarkSoulsIII.exe"],
            ["dark souls iii", "dark souls 3", "darksoulsiii", "darksouls3", "ds3"],
            374320, ".sl2"),
        // Bloodborne has no native PC build; the exe depends on the framework in use.
        // The list is checked in order, so the most specific names come first.
        new(GameId.Bloodborne, "Bloodborne", "BB",
            ["Bloodborne.exe", "shadPS4.exe"],
            ["bloodborne", "bb"],
            null, ".sl2"),
        // eldenring.exe is launched directly; start_protected_game.exe (Easy Anti-Cheat) is never used.
        new(GameId.EldenRing, "Elden Ring", "ER",
            ["eldenring.exe"],
            ["elden ring", "eldenring", "er"],
            1245620, ".sl2"),
    ];

    public static GameDefinition Get(GameId id) => All.First(g => g.Id == id);
}
