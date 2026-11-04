namespace DarkSign.Core;

/// <summary>
/// Removes The Dark Sign from this PC. Everything the launcher makes lives in &lt;root&gt;\TheDarkSign (the built mod,
/// me3, the porter, downloads, logs, caches, profile configs and the saves); the game installs are never modded
/// in place, because me3 loads the mod from that folder at launch. The one exception is the save-redirect DLL, which
/// older launchers and the "Other games" list put next to a game exe inside the root as dinput8.dll; ours are
/// removed (recognised by their marker), another mod's never.
/// </summary>
public static class Uninstaller
{
    public const string DataFolderName = "TheDarkSign";
    public const string SavesFolderName = "Saves";

    public sealed record Plan(IReadOnlyList<string> Folders, IReadOnlyList<string> Files, IReadOnlyList<string> KeptFolders);

    /// <summary>What <see cref="Run"/> would delete. Saves are kept unless <paramref name="deleteSaves"/>.</summary>
    public static Plan MakePlan(string root, IEnumerable<string> gameExePaths, bool deleteSaves)
    {
        var data = Path.Combine(root, DataFolderName);
        var folders = new List<string>();
        var kept = new List<string>();
        if (Directory.Exists(data))
        {
            foreach (var dir in Directory.GetDirectories(data))
            {
                if (!deleteSaves && string.Equals(Path.GetFileName(dir), SavesFolderName, StringComparison.OrdinalIgnoreCase))
                    kept.Add(dir);
                else
                    folders.Add(dir);
            }
        }

        var files = new List<string>();
        if (Directory.Exists(data))
            files.AddRange(Directory.GetFiles(data));
        foreach (var exe in gameExePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var proxy = SaveRedirectInstaller.ProxyPath(exe);
            if (PathGuard.IsInsideRoot(proxy, root) && File.Exists(proxy) && SaveRedirectInstaller.IsOurs(File.ReadAllBytes(proxy)))
                files.Add(proxy);
        }
        return new Plan(folders, files, kept);
    }

    /// <summary>Deletes what the plan lists, all of it inside the root. Returns the paths that could not be deleted.</summary>
    public static IReadOnlyList<string> Run(string root, Plan plan)
    {
        var failed = new List<string>();
        foreach (var dir in plan.Folders)
        {
            try
            {
                PathGuard.EnsureInsideRoot(dir, root);
                if (Directory.Exists(dir))
                    DeleteTree(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{dir}: {e.Message}");
            }
        }
        foreach (var file in plan.Files)
        {
            try
            {
                PathGuard.EnsureInsideRoot(file, root);
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{file}: {e.Message}");
            }
        }

        // An empty TheDarkSign folder goes too.
        var data = Path.Combine(root, DataFolderName);
        try
        {
            if (Directory.Exists(data) && !Directory.EnumerateFileSystemEntries(data).Any())
                Directory.Delete(data);
        }
        catch (IOException)
        {
            // something still open; harmless
        }
        return failed;
    }

    // Clears read-only flags first, so a file copied from read-only media doesn't stop the delete.
    private static void DeleteTree(string dir)
    {
        // Links are not followed: a junction to another folder is removed, never what it points at.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(dir, "*", options))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(dir, recursive: true);
    }
}
