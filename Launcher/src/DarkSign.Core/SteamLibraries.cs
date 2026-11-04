using System.Text.RegularExpressions;

namespace DarkSign.Core;

/// <summary>
/// Finds the "steamapps\common" folders of every Steam library on this PC, so a player's own Steam installs can be
/// found without browsing. Steam lists its libraries in steamapps\libraryfolders.vdf; Steam's own folder is in the
/// registry (HKCU\Software\Valve\Steam, SteamPath). These folders are only read: the launcher never writes into a
/// game folder outside its data folder.
/// </summary>
public static partial class SteamLibraries
{
    public static IReadOnlyList<string> CommonFolders()
    {
        var steamRoots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
            {
                try
                {
                    foreach (var name in new[] { "SteamPath", "InstallPath" })
                        if (Microsoft.Win32.Registry.GetValue(key, name, null) is string path && path.Length > 0)
                            steamRoots.Add(path.Replace('/', '\\'));
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
                {
                    // No Steam, or no access: browsing still works.
                }
            }
            steamRoots.Add(@"C:\Program Files (x86)\Steam");
        }

        var libraries = new List<string>();
        foreach (var steam in steamRoots.Where(Directory.Exists))
        {
            libraries.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    libraries.AddRange(ParseLibraryFolders(File.ReadAllText(vdf)));
            }
            catch (IOException)
            {
                // Steam may be writing it; the default library is still searched.
            }
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return libraries
            .Select(l => Path.Combine(l, "steamapps", "common"))
            .Where(Directory.Exists)
            .Select(d => Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(comparer)
            .ToList();
    }

    /// <summary>The library paths in a libraryfolders.vdf ("path" "D:\\SteamLibrary").</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdf) =>
        PathEntry().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathEntry();
}
