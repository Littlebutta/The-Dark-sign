namespace DarkSign.Core;

/// <summary>How a game's saves are kept out of the vanilla save folder.</summary>
public enum SaveRedirectMethod
{
    /// <summary>dksign_saveredirect.dll, installed next to the exe as dinput8.dll.</summary>
    ProxyDll,

    /// <summary>The bbport port's own BB_USER_DIR setting; saves keep their PS4 file names.</summary>
    BbportUserDir,
}

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
    SaveRedirectMethod SaveRedirect)
{
    public static readonly IReadOnlyList<GameDefinition> All =
    [
        new(GameId.DarkSoulsRemastered, "Dark Souls Remastered", "DSR",
            ["DarkSoulsRemastered.exe"],
            ["dark souls remastered", "darksoulsremastered", "dsr", "ds1"],
            570940, SaveRedirectMethod.ProxyDll),
        new(GameId.DarkSouls3, "Dark Souls III", "DS3",
            ["DarkSoulsIII.exe"],
            ["dark souls iii", "dark souls 3", "darksoulsiii", "darksouls3", "ds3"],
            374320, SaveRedirectMethod.ProxyDll),
        // The recomp's game exe is "Play Bloodborne.exe". Its "Bloodborne.exe" is only a settings
        // launcher, so it is never picked. The list is checked in order, most specific first.
        new(GameId.Bloodborne, "Bloodborne", "BB",
            ["Play Bloodborne.exe", "shadPS4.exe"],
            ["bloodborne", "bb"],
            null, SaveRedirectMethod.BbportUserDir),
        // eldenring.exe is launched directly; start_protected_game.exe (Easy Anti-Cheat) is never used.
        new(GameId.EldenRing, "Elden Ring", "ER",
            ["eldenring.exe"],
            ["elden ring", "eldenring", "er"],
            1245620, SaveRedirectMethod.ProxyDll),
    ];

    public static GameDefinition Get(GameId id) => All.First(g => g.Id == id);
}
