using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DarkSign.Core;

/// <summary>
/// What "Play Elden Ring" needs besides the game itself: the content mod the porter builds from Dark Souls III,
/// ModEngine2 to load it, and the DLLs ModEngine2 injects (save redirect, F1 test menu). Everything lives under
/// &lt;root&gt;\TheDarkSign; the game folder is never written.
/// </summary>
public sealed class EldenRingSetup(string root)
{
    public const string TestMenuDllName = "dksign_testmenu.dll";
    public const string ItemsVariable = "DKSIGN_ITEMS";
    public const string PorterAsset = "DarkSignPorter.zip";

    public const string ModEngineUrl =
        "https://github.com/soulsmods/ModEngine2/releases/download/release-2.1.0/ModEngine-2.1.0.0-win64.zip";
    public const string ModEngineSha256 = "8A5952453A8E3851247ED6A9DD8926CA638CF6135AA96C90298B63F15D8900EF";

    public string Root { get; } = root;
    public string ToolsDir => Path.Combine(Root, "TheDarkSign", "Tools");
    public string ModDir => Path.Combine(Root, "TheDarkSign", "Mods", "ER");
    public string ItemsFile => Path.Combine(ModDir, "darksign_items.tsv");
    public string BuildStampFile => Path.Combine(ModDir, "darksign_build.txt");
    public string ModEngineDir => Path.Combine(ToolsDir, "ModEngine2");
    public string ConfigPath => Path.Combine(ModEngineDir, "config_darksign_launcher.toml");
    public string DllDir => Path.Combine(ToolsDir, "Dlls");
    public string PorterDir => Path.Combine(ToolsDir, "Porter");
    public string PorterExe => Path.Combine(PorterDir, "DarkSignPorter.exe");
    public string LogDir => Path.Combine(Root, "TheDarkSign", "Work", "logs");
    private string PorterVersionFile => Path.Combine(PorterDir, "version.txt");

    public string? FindModEngineLauncher() => Directory.Exists(ModEngineDir)
        ? Directory.EnumerateFiles(ModEngineDir, "modengine2_launcher.exe", SearchOption.AllDirectories).FirstOrDefault()
        : null;

    /// <summary>The release the installed porter came from, "local" for a hand-built one, or null if none.</summary>
    public string? PorterVersion => File.Exists(PorterVersionFile) ? File.ReadAllText(PorterVersionFile).Trim()
        : File.Exists(PorterExe) ? "local" : null;

    public string? ContentVersion => File.Exists(BuildStampFile) ? File.ReadAllText(BuildStampFile).Trim() : null;

    /// <summary>The content is rebuilt when it was never built, or was built by a different porter.</summary>
    public bool NeedsContentBuild(string porterVersion) =>
        !File.Exists(Path.Combine(ModDir, "regulation.bin")) || !File.Exists(ItemsFile) || ContentVersion != porterVersion;

    public void MarkContentBuilt(string porterVersion) => File.WriteAllText(Guard(BuildStampFile), porterVersion);

    /// <summary>Unpacks ModEngine2's release zip into TheDarkSign\Tools\ModEngine2 after checking its hash.</summary>
    public void InstallModEngine(string zipPath)
    {
        if (ModEngineSha256.Length > 0)
        {
            using var stream = File.OpenRead(zipPath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(ModEngineSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The ModEngine2 download does not match the expected file (SHA-256 {hash}).");
        }
        ZipFile.ExtractToDirectory(zipPath, Guard(ModEngineDir), overwriteFiles: true);
    }

    /// <summary>Replaces the porter with the one from <paramref name="zipPath"/>.</summary>
    public void InstallPorter(string zipPath, string version)
    {
        Guard(PorterDir);
        if (Directory.Exists(PorterDir))
            Directory.Delete(PorterDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, PorterDir);
        File.WriteAllText(PorterVersionFile, version);
    }

    /// <summary>Writes an embedded DLL to TheDarkSign\Tools\Dlls (only when it changed) and returns its path.</summary>
    public string WriteDll(string name, byte[] bytes)
    {
        var path = Guard(Path.Combine(DllDir, name));
        Directory.CreateDirectory(DllDir);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);
        return path;
    }

    public string WriteConfig(IEnumerable<string> dlls)
    {
        Directory.CreateDirectory(Guard(ModEngineDir));
        File.WriteAllText(ConfigPath, ModEngineConfig(ModDir, dlls));
        return ConfigPath;
    }

    /// <summary>ModEngine2's config: load the content mod folder and inject our DLLs.</summary>
    public static string ModEngineConfig(string modDir, IEnumerable<string> dlls)
    {
        var text = new StringBuilder();
        text.AppendLine("# Written by The Dark Sign launcher on every launch; edits here are overwritten.");
        text.AppendLine("[modengine]");
        text.AppendLine("debug = false");
        text.AppendLine("external_dlls = [");
        foreach (var dll in dlls)
            text.AppendLine($"    {Toml(dll)},");
        text.AppendLine("]");
        text.AppendLine();
        text.AppendLine("[extension.mod_loader]");
        text.AppendLine("enabled = true");
        text.AppendLine("loose_params = false");
        text.AppendLine("mods = [");
        text.AppendLine($"    {{ enabled = true, name = \"darksign\", path = {Toml(modDir)} }},");
        text.AppendLine("]");
        text.AppendLine();
        text.AppendLine("[extension.scylla_hide]");
        text.AppendLine("enabled = false");
        return text.ToString();
    }

    /// <summary>Starts ModEngine2, which starts the game with the mod and DLLs loaded and our environment set.</summary>
    public static ProcessStartInfo LaunchInfo(LaunchPlan plan, string modEngineLauncher, string config,
        IReadOnlyDictionary<string, string> extraEnvironment)
    {
        var start = new ProcessStartInfo(modEngineLauncher)
        {
            WorkingDirectory = Path.GetDirectoryName(modEngineLauncher)!,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-t", "er", "-c", config, "-p", plan.ExePath })
            start.ArgumentList.Add(arg);
        foreach (var (key, value) in plan.Environment.Concat(extraEnvironment))
            start.Environment[key] = value;
        return start;
    }

    private static string Toml(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private string Guard(string path)
    {
        PathGuard.EnsureInsideRoot(path, Root);
        return path;
    }
}
