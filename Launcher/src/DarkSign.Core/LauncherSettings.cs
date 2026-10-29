using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSign.Core;

public sealed class GameLaunchOptions
{
    public string ExtraArguments { get; set; } = "";
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
}

/// <summary>A named playthrough: its own launch options and its own save folder.</summary>
public sealed class LaunchProfile
{
    public string Name { get; set; } = "Default";
    public Dictionary<GameId, GameLaunchOptions> Games { get; set; } = [];

    public GameLaunchOptions For(GameId id)
    {
        if (!Games.TryGetValue(id, out var options))
            Games[id] = options = new GameLaunchOptions();
        return options;
    }
}

public sealed class LauncherSettings
{
    public const string FileName = "launcher_settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The project root, e.g. "D:\The Dark sign". Empty means "work it out from the launcher's location".</summary>
    public string RootPath { get; set; } = "";
    public Dictionary<GameId, string> GameExePaths { get; set; } = [];
    public List<LaunchProfile> Profiles { get; set; } = [new LaunchProfile()];
    public string ActiveProfile { get; set; } = "Default";

    /// <summary>Refuse to launch a game whose saves would still go to AppData (no redirect DLL in place).</summary>
    public bool RequireSaveRedirect { get; set; } = true;

    /// <summary>Check GitHub for a newer launcher on every start.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// A GitHub token with read access to the repository's contents. Needed while the repository
    /// is private; leave empty for a public one.
    /// </summary>
    public string GitHubToken { get; set; } = "";

    public LaunchProfile GetActiveProfile()
    {
        var profile = Profiles.FirstOrDefault(p => p.Name == ActiveProfile);
        if (profile is null)
        {
            profile = Profiles.FirstOrDefault() ?? new LaunchProfile();
            if (Profiles.Count == 0)
                Profiles.Add(profile);
            ActiveProfile = profile.Name;
        }
        return profile;
    }

    public static LauncherSettings Load(string path)
    {
        if (!File.Exists(path))
            return new LauncherSettings();
        return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), JsonOptions) ?? new LauncherSettings();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>
    /// The launcher lives in "&lt;root&gt;\Launcher\...", so the root is the parent of the nearest
    /// ancestor folder named "Launcher". Falls back to the parent of <paramref name="launcherDir"/>.
    /// </summary>
    public static string ResolveDefaultRoot(string launcherDir)
    {
        for (var dir = new DirectoryInfo(launcherDir); dir is not null; dir = dir.Parent)
        {
            if (string.Equals(dir.Name, "Launcher", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
                return dir.Parent.FullName;
        }
        return Directory.GetParent(Path.GetFullPath(launcherDir))?.FullName ?? launcherDir;
    }
}
