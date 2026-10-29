namespace DarkSign.Core;

/// <summary>
/// Where each profile's saves live: &lt;root&gt;\TheDarkSign\Saves\&lt;profile&gt;\&lt;game&gt;\*.dksign.
/// The redirect DLL reads these locations from the environment variables set at launch.
/// </summary>
public static class SaveIsolation
{
    public const string SaveExtension = ".dksign";
    public const string RedirectDllName = "dksign_saveredirect.dll";

    public const string SaveDirVariable = "DKSIGN_SAVE_DIR";
    public const string SaveExtVariable = "DKSIGN_SAVE_EXT";
    public const string ProfileVariable = "DKSIGN_PROFILE";
    public const string GameVariable = "DKSIGN_GAME";

    public static string SavesRoot(string root) => Path.Combine(root, "TheDarkSign", "Saves");

    public static string SaveDirectory(string root, string profileName, GameId game) =>
        Path.Combine(SavesRoot(root), SanitizeFolderName(profileName), GameDefinition.Get(game).ShortName);

    public static string EnsureSaveDirectory(string root, string profileName, GameId game)
    {
        var dir = SaveDirectory(root, profileName, game);
        PathGuard.EnsureInsideRoot(dir, root);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Shader caches stay out of the save folders and are shared by every profile.</summary>
    public static string CacheDirectory(string root, GameId game) =>
        Path.Combine(root, "TheDarkSign", "Cache", GameDefinition.Get(game).ShortName);

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        return string.IsNullOrEmpty(cleaned) ? "Default" : cleaned;
    }
}
