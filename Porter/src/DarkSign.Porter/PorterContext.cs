using DarkSign.Core;

namespace DarkSign.Porter;

/// <summary>
/// Where the data folder (root), the games and the porter's work folder are. The games can be anywhere (a player's
/// own Steam installs, passed with --game); they are only read. Everything written goes under the root.
/// </summary>
public sealed class PorterContext
{
    public string Root { get; }
    public string WorkDir { get; }
    public ExternalData Data { get; }
    private readonly IReadOnlyList<DetectedGame> _games;

    public PorterContext(string root, IReadOnlyDictionary<GameId, string>? gameExes = null)
    {
        Root = Path.GetFullPath(root);
        WorkDir = Path.Combine(Root, "TheDarkSign", "Work");
        Data = new ExternalData(Path.Combine(WorkDir, "cache"));
        var given = (gameExes ?? new Dictionary<GameId, string>())
            .Where(g => File.Exists(g.Value))
            .Select(g => new DetectedGame(g.Key, Path.GetFullPath(g.Value)))
            .ToList();
        foreach (var missing in (gameExes ?? new Dictionary<GameId, string>()).Where(g => !File.Exists(g.Value)))
            Console.WriteLine($"warning: --game {GameDefinition.Get(missing.Key).ShortName}: {missing.Value} does not exist; searching {Root} instead");
        _games = given.Concat(GameDetector.Scan(Root).Where(s => given.All(g => g.Id != s.Id))).ToList();
    }

    /// <summary>
    /// The root given with --root, else the first folder above the porter's own that holds a TheDarkSign
    /// folder or the Launcher folder (the porter ships in &lt;root&gt;\TheDarkSign\Tools\Porter).
    /// </summary>
    public static string FindRoot(string? explicitRoot)
    {
        if (explicitRoot is not null)
            return explicitRoot;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.Name != "TheDarkSign" && (Directory.Exists(Path.Combine(dir.FullName, "TheDarkSign"))
                                              || Directory.Exists(Path.Combine(dir.FullName, "Launcher"))))
                return dir.FullName;
        throw new DirectoryNotFoundException("Could not find the project root; pass --root \"D:\\The Dark sign\".");
    }

    public string GameDir(PorterGame game) =>
        Path.GetDirectoryName(_games.FirstOrDefault(g => g.Id == game.Id)?.ExePath)
        ?? throw new DirectoryNotFoundException(
            $"{game.ShortName} was not found under {Root}; pass --game {game.ShortName}=<path to its exe> (the launcher does this).");

    /// <summary>A path in the Elden Ring mod folder (TheDarkSign\Mods\ER), created if needed.</summary>
    public string ModPath(params string[] parts)
    {
        var path = Path.Combine([Root, "TheDarkSign", "Mods", "ER", .. parts]);
        PathGuard.EnsureInsideRoot(path, Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>A path under the work folder, created if needed. Nothing is ever written outside it.</summary>
    public string WorkPath(params string[] parts)
    {
        var path = Path.Combine([WorkDir, .. parts]);
        PathGuard.EnsureInsideRoot(path, Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public GameArchives OpenArchives(PorterGame game)
    {
        if (game == PorterGame.EldenRing)
            AddOodle(GameDir(game));
        return GameArchives.Open(game, GameDir(game), Data);
    }

    // Elden Ring's DCX files are Oodle-compressed, and the game ships the only copy of oo2core on the PC. It is
    // copied (never moved) into the work cache and loaded from there, next to the game folder as a fallback.
    private void AddOodle(string gameDir)
    {
        var source = Directory.EnumerateFiles(gameDir, "oo2core_*_win64.dll").FirstOrDefault();
        lock (SoulsFormats.Oodle.SearchPaths)
        {
            if (source is not null)
            {
                var target = WorkPath("cache", "oodle", Path.GetFileName(source));
                // Two porter processes (port-weapons and port-maps run side by side) may both copy it the first time:
                // each copies to its own temporary name, and whoever is second keeps the first one's copy.
                if (!File.Exists(target))
                {
                    var tmp = $"{target}.{Environment.ProcessId}.part";
                    File.Copy(source, tmp, overwrite: true);
                    try { File.Move(tmp, target, overwrite: false); }
                    catch (IOException) when (File.Exists(target)) { File.Delete(tmp); }
                }
                SoulsFormats.Oodle.SearchPaths.Add(Path.GetDirectoryName(target)!);
            }
            SoulsFormats.Oodle.SearchPaths.Add(gameDir);
        }
    }

    public ParamStore LoadParams(PorterGame game) => ParamStore.Load(game, GameDir(game), Data);
}
