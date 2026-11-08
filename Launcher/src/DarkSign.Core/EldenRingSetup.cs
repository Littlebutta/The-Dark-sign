using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DarkSign.Core;

/// <summary>
/// What "Play Elden Ring" needs besides the game itself: the content mod the porter builds from Dark Souls III,
/// me3 to load it, and the DLLs me3 loads (save redirect, F1 test menu). Everything lives under
/// &lt;root&gt;\TheDarkSign (me3 keeps its own logs in %LOCALAPPDATA%); the game folder is never written.
/// me3 replaced ModEngine2 on 2026-11-01: ModEngine2 2.1.0 never loads modded sound banks (sd\...), me3 does.
/// </summary>
public sealed class EldenRingSetup(string root)
{
    public const string TestMenuDllName = "dksign_testmenu.dll";
    public const string ItemsVariable = "DKSIGN_ITEMS";
    public const string PorterAsset = "DarkSignPorter.zip";
    public const string BuildStampName = "darksign_build.txt";

    public const string ModEngineUrl =
        "https://github.com/soulsmods/ModEngine2/releases/download/release-2.1.0/ModEngine-2.1.0.0-win64.zip";
    public const string ModEngineSha256 = "8A5952453A8E3851247ED6A9DD8926CA638CF6135AA96C90298B63F15D8900EF";

    /// <summary>me3 (garyttierney/me3, MIT/Apache-2.0), the portable Windows build.</summary>
    public const string Me3Url = "https://github.com/garyttierney/me3/releases/download/v0.13.0/me3-windows-amd64.zip";
    public const string Me3Sha256 = "A8B693C574106F20532AB1E8B58B2452F37370679B938A884FE75F87F9B68C2B";

    public string Root { get; } = root;
    public string ToolsDir => Path.Combine(Root, "TheDarkSign", "Tools");
    public string ModDir => Path.Combine(Root, "TheDarkSign", "Mods", "ER");
    public string ItemsFile => Path.Combine(ModDir, "darksign_items.tsv");
    public string BuildStampFile => Path.Combine(ModDir, BuildStampName);
    public string ModEngineDir => Path.Combine(ToolsDir, "ModEngine2");
    public string ConfigPath => Path.Combine(ModEngineDir, "config_darksign_launcher.toml");
    public string Me3Dir => Path.Combine(ToolsDir, "me3");
    public string ProfilePath => Path.Combine(Me3Dir, "darksign-er.me3");
    public string DllDir => Path.Combine(ToolsDir, "Dlls");
    public string PorterDir => Path.Combine(ToolsDir, "Porter");
    public string PorterExe => Path.Combine(PorterDir, "DarkSignPorter.exe");
    public string LogDir => Path.Combine(Root, "TheDarkSign", "Work", "logs");
    private string PorterVersionFile => Path.Combine(PorterDir, "version.txt");

    public string? FindModEngineLauncher() => Directory.Exists(ModEngineDir)
        ? Directory.EnumerateFiles(ModEngineDir, "modengine2_launcher.exe", SearchOption.AllDirectories).FirstOrDefault()
        : null;

    public string? FindMe3() => Directory.Exists(Me3Dir)
        ? Directory.EnumerateFiles(Me3Dir, "me3.exe", SearchOption.AllDirectories).FirstOrDefault()
        : null;

    /// <summary>The release the installed porter came from, "local" for a hand-built one, or null if none.</summary>
    public string? PorterVersion => File.Exists(PorterVersionFile) ? File.ReadAllText(PorterVersionFile).Trim()
        : File.Exists(PorterExe) ? "local" : null;

    public string? ContentVersion => File.Exists(BuildStampFile) ? File.ReadAllText(BuildStampFile).Trim() : null;

    /// <summary>The content is rebuilt when it was never built, or was built by a different porter.</summary>
    public bool NeedsContentBuild(string porterVersion) =>
        !File.Exists(Path.Combine(ModDir, "regulation.bin")) || !File.Exists(ItemsFile) || ContentVersion != porterVersion
        || IconFilesProblem() is not null;

    /// <summary>The menu files the porter writes for item icons, in menu\hi and menu\low (see the porter's IconPort).</summary>
    public static readonly string[] IconFiles = ["01_common.tpf.dcx", "01_common.sblytbnd.dcx", "00_solo.tpfbhd", "00_solo.tpfbdt"];

    /// <summary>
    /// Why the built mod's icon menu files are not a whole set, or null. A build that ran while the game held them
    /// open once left menu\hi with only 00_solo.tpfbdt, and the DS3 weapon icons vanished; a rebuild fixes it.
    /// </summary>
    public string? IconFilesProblem()
    {
        var counts = new[] { "hi", "low" }.ToDictionary(q => q, q => IconFiles.Where(f => File.Exists(Path.Combine(ModDir, "menu", q, f))).ToList());
        foreach (var (quality, files) in counts)
            if (files.Count is > 0 and < 4)
                return $"menu\\{quality} is missing {string.Join(", ", IconFiles.Except(files))}";
        if (counts["hi"].Count != counts["low"].Count)
            return counts["hi"].Count == 0 ? "menu\\hi has no icon files but menu\\low has" : "menu\\low has no icon files but menu\\hi has";
        return null;
    }

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

    /// <summary>Unpacks me3's portable release zip into TheDarkSign\Tools\me3 after checking its hash.</summary>
    public void InstallMe3(string zipPath)
    {
        using (var stream = File.OpenRead(zipPath))
        {
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(Me3Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The me3 download does not match the expected file (SHA-256 {hash}).");
        }
        ZipFile.ExtractToDirectory(zipPath, Guard(Me3Dir), overwriteFiles: true);
    }

    /// <summary>Writes the me3 profile: the content mod folder as a package, and our DLLs as natives.</summary>
    public string WriteProfile(IEnumerable<string> dlls, Fallbacks.LaunchTweaks? tweaks = null)
    {
        Directory.CreateDirectory(Guard(Me3Dir));
        File.WriteAllText(ProfilePath, Me3Profile(ModDir, dlls, tweaks));
        return ProfilePath;
    }

    public static string Me3Profile(string modDir, IEnumerable<string> dlls, Fallbacks.LaunchTweaks? tweaks = null)
    {
        var text = new StringBuilder();
        text.AppendLine("# Written by The Dark Sign launcher on every launch; edits here are overwritten.");
        text.AppendLine("profileVersion = \"v1\"");
        // Fallback methods (launcher Fallbacks window): me3 v0.13.0 profile options (crates/mod-protocol ModProfileV1).
        if (tweaks?.DisableArxan == true)
            text.AppendLine("disable_arxan = true");
        if (tweaks?.NoMemPatch == true)
            text.AppendLine("mem_patch = false");
        if (tweaks?.StartOnline == true)
            text.AppendLine("start_online = true");
        text.AppendLine();
        text.AppendLine("[[supports]]");
        text.AppendLine("game = \"eldenring\"");
        text.AppendLine();
        text.AppendLine("[[packages]]");
        text.AppendLine("id = \"darksign\"");
        text.AppendLine($"path = {Toml(modDir)}");
        foreach (var dll in dlls)
        {
            text.AppendLine();
            text.AppendLine("[[natives]]");
            text.AppendLine($"path = {Toml(dll)}");
        }
        return text.ToString();
    }

    /// <summary>Starts me3, which starts the game (offline, no EAC) with the mod and DLLs loaded and our environment set.</summary>
    public static ProcessStartInfo Me3LaunchInfo(LaunchPlan plan, string me3Exe, string profile,
        IReadOnlyDictionary<string, string> extraEnvironment)
    {
        var start = new ProcessStartInfo(me3Exe)
        {
            WorkingDirectory = Path.GetDirectoryName(me3Exe)!,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "launch", "-g", "eldenring", "-e", plan.ExePath, "-p", profile })
            start.ArgumentList.Add(arg);
        foreach (var (key, value) in plan.Environment.Concat(extraEnvironment))
            start.Environment[key] = value;
        return start;
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
