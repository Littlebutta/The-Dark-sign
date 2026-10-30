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
    public static LaunchPlan Plan(LauncherSettings settings, string root, GameId game)
    {
        if (!settings.GameExePaths.TryGetValue(game, out var exe) || string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException($"No exe is set for {GameDefinition.Get(game).DisplayName}.");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Game exe not found.", exe);
        PathGuard.EnsureInsideRoot(exe, root);

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
            appIdFile = Path.Combine(workingDir, "steam_appid.txt");
        }

        return new LaunchPlan(game, Path.GetFullPath(exe), workingDir, options.ExtraArguments.Trim(), env,
            saveDir, appIdFile);
    }
}
