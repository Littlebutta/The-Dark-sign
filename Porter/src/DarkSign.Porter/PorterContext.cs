using DarkSign.Core;

namespace DarkSign.Porter;

/// <summary>Where the project root, the games and the porter's work folder are.</summary>
public sealed class PorterContext
{
    public string Root { get; }
    public string WorkDir { get; }
    public ExternalData Data { get; }
    private readonly IReadOnlyList<DetectedGame> _games;

    public PorterContext(string root)
    {
        Root = Path.GetFullPath(root);
        WorkDir = Path.Combine(Root, "TheDarkSign", "Work");
        Data = new ExternalData(Path.Combine(WorkDir, "cache"));
        _games = GameDetector.Scan(Root);
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
        ?? throw new DirectoryNotFoundException($"{game.ShortName} was not found under {Root}.");

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
            SoulsFormats.Oodle.SearchPaths.Add(GameDir(game)); // Elden Ring's DCX files need its oo2core DLL
        return GameArchives.Open(game, GameDir(game), Data);
    }

    public ParamStore LoadParams(PorterGame game) => ParamStore.Load(game, GameDir(game), Data);
}
