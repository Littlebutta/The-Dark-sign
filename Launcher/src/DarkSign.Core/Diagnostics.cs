using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DarkSign.Core;

/// <summary>
/// "Save test report": after a test, one zip with every log and report the launcher, porter, me3 and our DLLs wrote,
/// so a problem can be looked at without describing it. It holds text files only: no save files, no game files, no
/// built mod files, and the GitHub token is blanked out of the settings copy. Written to
/// &lt;root&gt;\TheDarkSign\Diagnostics\darksign-report-&lt;time&gt;.zip.
/// </summary>
public static class Diagnostics
{
    /// <summary>Extensions that are copied; anything else (saves, binders, banks, regulation.bin) is never included.</summary>
    private static readonly string[] TextExtensions = [".log", ".txt", ".tsv", ".json", ".me3", ".toml", ".ini", ".md"];

    /// <summary>Biggest single file copied (bigger logs keep their last part).</summary>
    public const long MaxFileBytes = 8 * 1024 * 1024;

    public sealed record Source(string Folder, string ZipFolder, string Pattern, bool Recursive, int Newest);

    /// <summary>Where the logs and reports live.</summary>
    public static List<Source> Sources(string root, LauncherSettings settings, string launcherDir)
    {
        var dark = Path.Combine(root, "TheDarkSign");
        var setup = new EldenRingSetup(root);
        var list = new List<Source>
        {
            new(Path.Combine(dark, "Work", "logs"), "logs", "*.log", false, 12),
            new(Path.Combine(dark, "Work", "reports"), "reports", "*", true, 400),
            new(Path.Combine(dark, "Work"), "work", "*.txt", false, 50),
            new(setup.ModDir, "mod", "*", false, 100),
            new(setup.Me3Dir, "me3", "*.me3", false, 5),
            new(setup.ModEngineDir, "modengine2", "*.toml", false, 5),
            new(Path.Combine(dark, "Randomizer"), "randomizer", "*", true, 50),
        };
        // This profile's save folders hold our DLLs' logs (never the saves: .dksign isn't a text extension).
        foreach (var game in Enum.GetValues<GameId>())
            list.Add(new(SaveIsolation.SaveDirectory(root, settings.GetActiveProfile().Name, game),
                Path.Combine("saves-logs", game.ToString()), "*.log", true, 10));
        foreach (var dir in Preflight.Me3Logs(root).Select(Path.GetDirectoryName).OfType<string>().Distinct().Take(2))
            list.Add(new(dir, "me3-logs", "*.log", false, 5));
        list.Add(new(launcherDir, "launcher", "*.log", false, 5));
        return list;
    }

    /// <summary>Writes the zip and returns its path. <paramref name="preflight"/>: the pre-flight report text, if one was run.</summary>
    public static string Write(string root, LauncherSettings settings, string launcherDir, string launcherVersion, string? preflight)
    {
        var dir = Path.Combine(root, "TheDarkSign", "Diagnostics");
        Directory.CreateDirectory(dir);
        PathGuard.EnsureInsideRoot(dir, root);
        var zipPath = Path.Combine(dir, $"darksign-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var listing = new StringBuilder();

        void AddText(string name, string text)
        {
            var entry = zip.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        AddText("summary.txt", Summary(root, settings, launcherVersion));
        if (preflight is not null)
            AddText("preflight.txt", preflight);
        AddText("launcher_settings.json", Redact(ReadShared(Path.Combine(launcherDir, LauncherSettings.FileName)) ?? "(none)"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources(root, settings, launcherDir))
        {
            if (!Directory.Exists(source.Folder))
                continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(source.Folder, source.Pattern,
                        source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => TextExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Where(f => !Path.GetFileName(f).Equals(LauncherSettings.FileName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(source.Newest).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                listing.AppendLine($"{source.Folder}: not readable ({ex.Message})");
                continue;
            }
            foreach (var file in files)
            {
                if (!seen.Add(Path.GetFullPath(file)))
                    continue;
                var text = ReadShared(file, MaxFileBytes);
                if (text is null)
                {
                    listing.AppendLine($"{file}: not readable");
                    continue;
                }
                var name = Path.Combine(source.ZipFolder, Path.GetRelativePath(source.Folder, file));
                AddText(name, text);
                listing.AppendLine($"{name}  <- {file} ({new FileInfo(file).Length:N0} bytes, {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm})");
            }
        }
        AddText("files.txt", listing.ToString());
        return zipPath;
    }

    private static string Summary(string root, LauncherSettings settings, string launcherVersion)
    {
        var setup = new EldenRingSetup(root);
        var text = new StringBuilder();
        text.AppendLine($"The Dark Sign test report, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"launcher {launcherVersion}, porter {setup.PorterVersion ?? "not installed"}, content {setup.ContentVersion ?? "not built"}");
        text.AppendLine($"profile {settings.GetActiveProfile().Name}; test menu {(settings.TestMenu ? "on" : "off")}; Seamless Co-op {(settings.SeamlessCoop ? "on" : "off")}");
        var methods = Fallbacks.Summary(settings.Methods);
        text.AppendLine($"fallback methods: {(methods.Length == 0 ? "all A" : methods)}");
        foreach (var (game, exe) in settings.GameExePaths)
            text.AppendLine($"{game}: {exe}{(File.Exists(exe) ? "" : " (missing)")}");
        text.AppendLine($"OS: {Environment.OSVersion}, .NET {Environment.Version}");
        return text.ToString();
    }

    /// <summary>Blanks secrets (the GitHub token) out of the settings JSON.</summary>
    public static string Redact(string json) =>
        Regex.Replace(json, "(\"GitHubToken\"\\s*:\\s*\")[^\"]*(\")", m => m.Groups[1].Value + (m.Value.EndsWith("\"\"") ? "" : "(removed)") + m.Groups[2].Value);

    /// <summary>A text file the game may still have open; the end of it when it is bigger than <paramref name="maxBytes"/>.</summary>
    private static string? ReadShared(string path, long maxBytes = MaxFileBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skipped = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(skipped, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return skipped > 0 ? $"[first {skipped:N0} bytes left out]\n{text}" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
