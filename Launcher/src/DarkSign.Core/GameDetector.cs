namespace DarkSign.Core;

public sealed record DetectedGame(GameId Id, string ExePath);

/// <summary>Finds each game's exe in the project root's subfolders.</summary>
public static class GameDetector
{
    // Steam layouts put the exe one or two levels down (e.g. "DARK SOULS III\Game\DarkSoulsIII.exe").
    private const int MaxDepth = 3;

    private static readonly HashSet<string> IgnoredFolders =
        new(StringComparer.OrdinalIgnoreCase) { "Launcher", "TheDarkSign" };

    public static IReadOnlyList<DetectedGame> Scan(string root)
    {
        if (!Directory.Exists(root))
            return [];

        var gameFolders = Directory.GetDirectories(root)
            .Where(d => !IgnoredFolders.Contains(Path.GetFileName(d)))
            .ToList();

        var results = new List<DetectedGame>();
        foreach (var game in GameDefinition.All)
        {
            // Folders whose name matches the game are searched first, so an exe that two games
            // could share (e.g. an emulator) is attributed to the right one.
            var ordered = gameFolders.OrderByDescending(d => MatchesHint(game, Path.GetFileName(d)));
            foreach (var folder in ordered)
            {
                var exe = FindExe(folder, game.ExeNames, MaxDepth);
                if (exe is null)
                    continue;
                if (!MatchesHint(game, Path.GetFileName(folder)) && game.ExeNames.Count > 1 && IsGenericExe(game, exe))
                    continue;
                results.Add(new DetectedGame(game.Id, exe));
                break;
            }
        }
        return results;
    }

    internal static bool MatchesHint(GameDefinition game, string folderName)
    {
        var name = folderName.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ').Trim();
        return game.FolderHints.Any(h => name == h || (h.Length > 3 && name.Contains(h)));
    }

    // A generic exe (anything but the first, game-specific name) only counts in a folder named for the game.
    private static bool IsGenericExe(GameDefinition game, string exe) =>
        !string.Equals(Path.GetFileName(exe), game.ExeNames[0], StringComparison.OrdinalIgnoreCase);

    private static string? FindExe(string dir, IReadOnlyList<string> exeNames, int depth)
    {
        foreach (var name in exeNames)
        {
            var candidate = Directory.EnumerateFiles(dir)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
                return candidate;
        }
        if (depth <= 1)
            return null;

        foreach (var sub in SafeGetDirectories(dir))
        {
            var found = FindExe(sub, exeNames, depth - 1);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static string[] SafeGetDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (UnauthorizedAccessException) { return []; }
    }
}
