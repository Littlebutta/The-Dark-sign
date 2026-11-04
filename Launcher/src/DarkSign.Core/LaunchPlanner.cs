namespace DarkSign.Core;

public sealed record LaunchPlan(
    GameId Game,
    string ExePath,
    string WorkingDirectory,
    string Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string SaveDirectory,
    string? SteamAppIdFile);

/// <summary>Turns settings into a validated launch plan. Starting the process is left to the UI.</summary>
public static class LaunchPlanner
{
    /// <summary>
    /// <paramref name="gameFolderReadOnly"/>: the launch writes nothing into the game folder (Elden Ring through me3,
    /// which injects the save redirect itself), so the game may be any install, e.g. the player's own Steam copy.
    /// Otherwise the exe must be inside the root, because the save-redirect DLL and steam_appid.txt go next to it.
    /// </summary>
    public static LaunchPlan Plan(LauncherSettings settings, string root, GameId game, bool gameFolderReadOnly = false)
    {
        if (!settings.GameExePaths.TryGetValue(game, out var exe) || string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException($"No exe is set for {GameDefinition.Get(game).DisplayName}.");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Game exe not found.", exe);
        if (!gameFolderReadOnly && !PathGuard.IsInsideRoot(exe, root))
            throw new IsolationViolationException(
                $"{GameDefinition.Get(game).DisplayName} is outside the data folder '{root}'. Its files are only read to build the " +
                "Elden Ring mod; to launch it from here, put a copy of the game inside the data folder.");

        var definition = GameDefinition.Get(game);
        var profile = settings.GetActiveProfile();
        var options = profile.For(game);
        var workingDir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var saveDir = SaveIsolation.SaveDirectory(root, profile.Name, game);

        var env = new Dictionary<string, string>(options.EnvironmentVariables)
        {
            [SaveIsolation.SaveDirVariable] = saveDir,
            [SaveIsolation.SaveExtVariable] = SaveIsolation.SaveExtension,
            [SaveIsolation.ProfileVariable] = profile.Name,
            [SaveIsolation.GameVariable] = definition.ShortName,
        };

        if (definition.SaveRedirect == SaveRedirectMethod.BbportUserDir)
        {
            env["BB_USER_DIR"] = saveDir;
            env["BB_GPU_USER_DIR"] = SaveIsolation.CacheDirectory(root, game);
            env["APPDATA"] = BbportConfig.AppDataDirectory(root, profile.Name);
        }

        // Launching the exe directly (not through Steam) needs the app id, or Steam restarts
        // the game from its own install folder, which would break isolation.
        string? appIdFile = null;
        if (definition.SteamAppId is int appId)
        {
            env["SteamAppId"] = appId.ToString();
            env["SteamGameId"] = appId.ToString();
            if (!gameFolderReadOnly)
                appIdFile = Path.Combine(workingDir, "steam_appid.txt");
        }

        return new LaunchPlan(game, Path.GetFullPath(exe), workingDir, options.ExtraArguments.Trim(), env,
            saveDir, appIdFile);
    }
}
