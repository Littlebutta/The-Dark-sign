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
    /// <summary>This profile's cross-game randomizer (null or disabled = the normal game).</summary>
    public RandomizerSettings? Randomizer { get; set; }
    /// <summary>A friend's co-op code: while set, this profile loads that friend's randomized world so the two can
    /// play together (see <see cref="RandomizerCoop"/>). Its own <see cref="Randomizer"/> seed is kept for later.</summary>
    public string? JoinCode { get; set; }

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

    /// <summary>Load the F1 test menu (give any item, set runes) when playing Elden Ring.</summary>
    public bool TestMenu { get; set; } = true;

    /// <summary>Load Seamless Co-op (the player's own ersc.dll, <see cref="SeamlessCoopDll"/>) when playing Elden Ring.</summary>
    public bool SeamlessCoop { get; set; }

    /// <summary>Full path of ersc.dll from the player's own Seamless Co-op download. Never copied or shipped.</summary>
    public string SeamlessCoopDll { get; set; } = "";

    /// <summary>
    /// The method chosen for each feature in the Fallbacks window, by feature key ("sounds" -> "B"). Missing keys use
    /// option A, the default. See <see cref="Fallbacks"/>.
    /// </summary>
    public Dictionary<string, string> Methods { get; set; } = [];

    /// <summary>How long each part of the last content build took (port-weapons, port-maps), for the time-left estimate.</summary>
    public Dictionary<string, BuildStat> LastBuild { get; set; } = [];

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
        try
        {
            var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), JsonOptions) ?? new LauncherSettings();
            // A hand-edited file may say "null" for these; the rest of the launcher expects them set.
            settings.GameExePaths ??= [];
            settings.Profiles ??= [];
            settings.Methods ??= [];
            settings.LastBuild ??= [];
            settings.RootPath ??= "";
            settings.SeamlessCoopDll ??= "";
            settings.GitHubToken ??= "";
            settings.ActiveProfile ??= "Default";
            return settings;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            // A broken file (hand edit, a crash mid-write) must not stop the launcher from starting: it is kept
            // aside for the player and the defaults are used.
            try { File.Copy(path, path + ".broken", overwrite: true); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            return new LauncherSettings();
        }
    }

    /// <summary>Writes the file through a temporary copy, so a crash or full disk never leaves half a file.</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// In the original layout the launcher lives in "&lt;root&gt;\Launcher\...", so the root is the parent of the nearest
    /// ancestor folder named "Launcher". Otherwise (a player who unzipped the launcher into any folder) the launcher's
    /// own folder is the root: the mod, tools and saves go in a "TheDarkSign" folder next to the exe.
    /// </summary>
    public static string ResolveDefaultRoot(string launcherDir)
    {
        for (var dir = new DirectoryInfo(launcherDir); dir is not null; dir = dir.Parent)
        {
            if (string.Equals(dir.Name, "Launcher", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
                return dir.Parent.FullName;
        }
        return Path.GetFullPath(launcherDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
