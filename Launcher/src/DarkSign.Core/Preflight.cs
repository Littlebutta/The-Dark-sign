using System.Text.RegularExpressions;

namespace DarkSign.Core;

/// <summary>
/// The launcher's "Pre-flight check": everything that can be checked before the game starts, so a test doesn't end
/// with "it didn't work" and no clue why. Fast checks only (files, settings, the me3 profile, the reports the last
/// build and the last game session left); the launcher runs the porter's validate-mod and map-validate for the deep
/// checks (sound registration, map files, flag lists) and then reads their reports back here.
/// </summary>
public static class Preflight
{
    public enum Level { Ok, Info, Warn, Fail }

    public sealed record Item(Level Level, string Area, string Text);

    /// <summary>
    /// Keys me3 v0.13.0 reads from a profile, from its source (crates/mod-protocol/src/lib.rs ModProfileV1, native.rs
    /// Native, package.rs Package, lib.rs Supports). serde ignores unknown keys silently, so a key outside these
    /// lists would simply do nothing: the check catches that before a test is wasted on it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Me3Keys = new Dictionary<string, string[]>
    {
        [""] = ["profileVersion", "supports", "natives", "native", "packages", "package", "savefile", "start_online",
                "disable_arxan", "mem_patch", "patch_mem", "mem_patch_heap_size", "debug_properties"],
        ["supports"] = ["game", "since"],
        ["natives"] = ["path", "optional", "enabled", "load_before", "load_after", "initializer", "finalizer"],
        ["packages"] = ["id", "path", "source", "enabled", "load_before", "load_after"],
    };

    /// <summary>Problems with an me3 profile's text: keys me3 v0.13.0 doesn't know, and a missing profileVersion.</summary>
    public static List<string> CheckMe3Profile(string text)
    {
        var problems = new List<string>();
        string table = "";
        bool version = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith('['))
            {
                table = line.Trim('[', ']', ' ');
                if (!Me3Keys.ContainsKey(table) && !Me3Keys[""].Contains(table))
                    problems.Add($"table [{table}] isn't something me3 v0.13.0 reads");
                if (table is "native") table = "natives";
                if (table is "package") table = "packages";
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            if (table == "" && key == "profileVersion")
                version = line[(eq + 1)..].Contains("\"v1\"");
            if (Me3Keys.TryGetValue(table, out var keys) && !keys.Contains(key))
                problems.Add($"{(table.Length > 0 ? $"[{table}] " : "")}{key} isn't a key me3 v0.13.0 reads (it would be ignored)");
        }
        if (!version)
            problems.Add("profileVersion = \"v1\" is missing (me3 refuses the profile)");
        return problems;
    }

    /// <summary>What the launcher knows when the check runs.</summary>
    public sealed record Context(string Root, LauncherSettings Settings, string? PorterVersion);

    public static List<Item> Run(Context c)
    {
        var items = new List<Item>();
        void Add(Level level, string area, string text) => items.Add(new Item(level, area, text));
        var s = c.Settings;
        var setup = new EldenRingSetup(c.Root);
        var tweaks = Fallbacks.Launch(s.Methods, s.SeamlessCoop);

        // ---- Games ----
        if (!Directory.Exists(c.Root))
            Add(Level.Fail, "Data folder", $"{c.Root} doesn't exist");
        else
            Add(Level.Ok, "Data folder", c.Root);
        var er = s.GameExePaths.GetValueOrDefault(GameId.EldenRing, "");
        if (!File.Exists(er))
            Add(Level.Fail, "Elden Ring", er.Length == 0 ? "not set: use Browse… in the game list" : $"{er} doesn't exist");
        else
        {
            var version = FileVersion(er);
            Add(version is null or "2.7.2.0" or "2.7.2" ? Level.Ok : Level.Warn, "Elden Ring",
                $"{er}{(version is null ? "" : $" (version {version})")}" +
                (version is null or "2.7.2.0" or "2.7.2" ? "" : ". The F1 menu's motion and DLL-sound hooks were made for 2.7.2; other versions may not hook"));
            if (File.Exists(Path.Combine(Path.GetDirectoryName(er)!, "dinput8.dll")))
                Add(Level.Warn, "Elden Ring", "a dinput8.dll is in the game folder; an old launcher or another mod may load twice (the launcher removes its own on PLAY)");
        }
        var ds3 = s.GameExePaths.GetValueOrDefault(GameId.DarkSouls3, "");
        Add(File.Exists(ds3) ? Level.Ok : setup.ContentVersion is null ? Level.Fail : Level.Warn, "Dark Souls III",
            File.Exists(ds3) ? ds3 : "not found; it is needed to build the content" + (setup.ContentVersion is null ? "" : " (the last build can still be played)"));

        // ---- Tools ----
        if (tweaks.ModEngine2)
            Add(setup.FindModEngineLauncher() is not null ? Level.Ok : Level.Info, "Loader",
                setup.FindModEngineLauncher() is { } me2 ? $"ModEngine2 (Fallbacks: loader C): {me2}" : "ModEngine2 is downloaded on the next PLAY");
        else
            Add(setup.FindMe3() is not null ? Level.Ok : Level.Info, "Loader",
                setup.FindMe3() is { } me3 ? $"me3 v0.13.0: {me3}" : "me3 is downloaded on the next PLAY");
        var profile = EldenRingSetup.Me3Profile(setup.ModDir,
            [Path.Combine(setup.DllDir, SaveIsolation.RedirectDllName), Path.Combine(setup.DllDir, EldenRingSetup.TestMenuDllName)], tweaks);
        var me3Problems = CheckMe3Profile(profile);
        if (File.Exists(setup.ProfilePath))
            me3Problems.AddRange(CheckMe3Profile(File.ReadAllText(setup.ProfilePath)).Select(p => "last written profile: " + p));
        if (me3Problems.Count == 0)
        {
            var keys = new[] { tweaks.DisableArxan ? "disable_arxan" : null, tweaks.NoMemPatch ? "mem_patch" : null, tweaks.StartOnline ? "start_online" : null }
                .OfType<string>().ToList();
            Add(Level.Ok, "me3 profile", "every key is one me3 v0.13.0 reads" + (keys.Count > 0 ? $" (incl. {string.Join(", ", keys)})" : ""));
        }
        foreach (var p in me3Problems.Distinct())
            Add(Level.Fail, "me3 profile", p);
        foreach (var log in Me3Logs(c.Root).Take(1))
        {
            var bad = ReadShared(log).Where(l => l.Contains("unknown field", StringComparison.OrdinalIgnoreCase)
                                                   || l.Contains(" ERROR ", StringComparison.Ordinal)).Take(3).ToList();
            Add(bad.Count == 0 ? Level.Ok : Level.Warn, "me3 log",
                bad.Count == 0 ? $"last me3 log has no errors ({Path.GetFileName(log)})" : $"{Path.GetFileName(log)}: {string.Join(" | ", bad)}");
        }
        Add(c.PorterVersion is not null ? Level.Ok : Level.Info, "Porter",
            c.PorterVersion is not null ? $"installed ({c.PorterVersion})" : "downloaded on the next PLAY");
        if (s.SeamlessCoop)
            Add(SeamlessCoop.Problem(s.SeamlessCoopDll) is not null ? Level.Fail : Level.Ok, "Seamless Co-op",
                SeamlessCoop.Problem(s.SeamlessCoopDll) ?? s.SeamlessCoopDll);

        // ---- Content ----
        var stamp = c.PorterVersion is null ? null : Fallbacks.BuildStamp(c.PorterVersion, s.Methods);
        if (!File.Exists(Path.Combine(setup.ModDir, "regulation.bin")))
            Add(Level.Info, "Content", "not built yet; PLAY builds it (takes a few minutes)");
        else if (stamp is not null && setup.NeedsContentBuild(stamp))
            Add(Level.Info, "Content", $"built with {setup.ContentVersion ?? "an unknown porter"}; PLAY rebuilds it for {stamp}");
        else
            Add(Level.Ok, "Content", $"built ({setup.ContentVersion})");
        var methods = Fallbacks.Summary(s.Methods);
        Add(Level.Info, "Fallbacks", methods.Length == 0 ? "every feature uses method A" : methods);

        // ---- Sounds ----
        var sounds = Fallbacks.Chosen(s.Methods, Fallbacks.Get("sounds")!).Letter;
        if (File.Exists(Path.Combine(setup.ModDir, "regulation.bin")) && sounds != "off")
        {
            if (tweaks.ModEngine2)
                Add(Level.Warn, "Sounds", "ModEngine2 never loads modded sound banks (proven 2026-11-01): ported swings will be silent or Elden Ring's");
            var bank = sounds == "E" ? Path.Combine("sd", "cs_c9990.bnk") : Path.Combine("sd", "enus", "cs_main.bnk");
            if (sounds != "D")
                Add(File.Exists(Path.Combine(setup.ModDir, bank)) ? Level.Ok : Level.Fail, "Sounds",
                    File.Exists(Path.Combine(setup.ModDir, bank)) ? $"{bank} built (method {sounds})" : $"{bank} is missing (method {sounds}); ported swings will be silent");
            if (sounds is "A" or "B" or "C" or "E")
            {
                var index = Path.Combine(setup.ModDir, "sound", "soundbanksinfo.mobnkinfo");
                Add(File.Exists(index) ? Level.Ok : Level.Warn, "Sounds",
                    File.Exists(index) ? "the game's sound index (sound\\soundbanksinfo.mobnkinfo) lists the new sounds"
                        : "sound\\soundbanksinfo.mobnkinfo wasn't written, so the game's index doesn't list the new sounds (see the port-weapons log, \"sounds: index\")");
            }
        }
        ReportVerdict(items, "Build checks", Path.Combine(c.Root, "TheDarkSign", "Work", "reports", "validate-mod.txt"),
            l => l.StartsWith("ERROR ", StringComparison.Ordinal), l => l.Contains("sound", StringComparison.OrdinalIgnoreCase) ? "Sounds" : "Build checks");

        // ---- Maps and flags ----
        var mapsCheck = Path.Combine(setup.ModDir, "darksign_maps_check.txt");
        ReportVerdict(items, "DS3 maps", mapsCheck, l => l.StartsWith("FAIL", StringComparison.Ordinal),
            l => l.Contains("flag", StringComparison.OrdinalIgnoreCase) ? "Event flags" : "DS3 maps");
        CheckFlags(items, setup);

        // ---- Enemies ----
        var chrMap = Path.Combine(setup.ModDir, "darksign_chr_map.tsv");
        if (File.Exists(chrMap))
        {
            var rows = File.ReadLines(chrMap).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            int models = rows.Count(l => l.StartsWith("chr\t", StringComparison.Ordinal)), npcs = rows.Count - models;
            Add(rows.Count > 0 ? Level.Ok : Level.Warn, "Enemies",
                $"darksign_chr_map.tsv: {npcs} DS3 NpcParam rows ported, {models} DS3 models with their own Elden Ring character");
        }
        else if (File.Exists(Path.Combine(setup.ModDir, "regulation.bin")))
            Add(Level.Info, "Enemies", "no ported DS3 characters yet (darksign_chr_map.tsv); DS3 maps use Elden Ring stand-ins");
        CheckEnemyReport(items, Path.Combine(c.Root, "TheDarkSign", "Work", "reports", "enemy-port.tsv"));

        // ---- The last game session ----
        var saves = SaveIsolation.SaveDirectory(c.Root, s.GetActiveProfile().Name, GameId.EldenRing);
        var redirect = Path.Combine(saves, "dksign_saveredirect.log");
        if (File.Exists(redirect))
        {
            var lines = ReadShared(redirect);
            var bad = lines.Where(l => l.Contains("BLOCKED", StringComparison.Ordinal) || l.Contains("failed", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            Add(bad.Count == 0 ? Level.Ok : Level.Fail, "Save redirect",
                bad.Count == 0 ? $"last session redirected saves to {saves} ({lines.Count(l => l.Contains(".dksign", StringComparison.Ordinal))} .dksign opens)"
                    : string.Join(" | ", bad));
        }
        var menuLog = Path.Combine(saves, "dksign_testmenu.log");
        if (File.Exists(menuLog))
        {
            var lines = ReadShared(menuLog);
            var scan = lines.LastOrDefault(l => l.Contains("scan ", StringComparison.Ordinal) && l.Contains("EventFlagMan", StringComparison.Ordinal));
            if (scan is not null)
                Add(scan.Contains(" +0,", StringComparison.Ordinal) || scan.EndsWith(" +0", StringComparison.Ordinal) ? Level.Warn : Level.Ok,
                    "F1 test menu", "last session: " + scan.Trim());
            foreach (var line in lines.Where(l => l.Contains("did not stick", StringComparison.Ordinal)).Take(2))
                Add(Level.Fail, "Event flags", "last session: " + line.Trim());
        }
        return items;
    }

    /// <summary>
    /// Event flags: what the port's flag table puts where (JortPob's shipped layout for the sea-tile blocks) and, when
    /// a game session has run since, what the game itself said about each block (dksign_testmenu.log, flagcheck).
    /// </summary>
    private static void CheckFlags(List<Item> items, EldenRingSetup setup)
    {
        var flagFile = Path.Combine(setup.ModDir, "darksign_flags.tsv");
        if (File.Exists(flagFile))
        {
            var flags = File.ReadLines(flagFile).Where(l => l.StartsWith("flag\t", StringComparison.Ordinal))
                .Select(l => long.TryParse(l.Split('\t')[2], out var v) ? v : 0).Where(v => v > 0).ToList();
            var notSaved = flags.Where(f => JortPobLayout(f) is not "saved").Take(3).ToList();
            items.Add(new Item(notSaved.Count == 0 ? Level.Ok : Level.Warn, "Event flags", notSaved.Count == 0
                ? $"{flags.Count} ported DS3 flags, all in saved ranges of the blocks JortPob's Morrowind port saves its flags in"
                : $"flags outside JortPob's saved ranges: {string.Join(", ", notSaved)}"));
        }
        var menuLog = Directory.Exists(Path.Combine(setup.Root, "TheDarkSign", "Saves"))
            ? Directory.EnumerateFiles(Path.Combine(setup.Root, "TheDarkSign", "Saves"), "dksign_testmenu.log", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        var checks = menuLog is null ? [] : ReadShared(menuLog).Where(l => l.Contains("flagcheck:", StringComparison.Ordinal)).ToList();
        if (checks.Count == 0)
        {
            items.Add(new Item(Level.Info, "Event flags", "not checked in game yet: load a character once with the F1 test menu ticked (the game is asked about each flag block)"));
            return;
        }
        foreach (var line in checks.Where(l => l.Contains("MISSING", StringComparison.Ordinal)))
            items.Add(new Item(Level.Fail, "Event flags", "in game: " + line[(line.IndexOf("flagcheck:", StringComparison.Ordinal) + 10)..].Trim()
                + (line.Contains("map flags", StringComparison.Ordinal) ? " → switch Fallbacks \"map-flags\" to B" : "")));
        var summary = checks.LastOrDefault(l => l.Contains("blocks ok", StringComparison.Ordinal));
        if (summary is not null && !summary.Contains("MISSING", StringComparison.Ordinal))
            items.Add(new Item(summary.Contains(" 0 missing", StringComparison.Ordinal) ? Level.Ok : Level.Warn, "Event flags",
                "in game: " + summary[(summary.IndexOf("flagcheck:", StringComparison.Ordinal) + 10)..].Trim()));
    }

    /// <summary>JortPob's ScriptCommon.cs layout for the sea-tile blocks 1030290000-1039299999: "saved", "temporary", "event" or "other".</summary>
    public static string JortPobLayout(long flag)
    {
        if (flag < 1_030_290_000 || flag > 1_039_299_999 || flag / 10_000 % 100 != 29)
            return "other";
        return (flag % 10_000) switch
        {
            < 1000 or (>= 4000 and < 5000) or >= 7000 => "saved",
            (>= 2000 and < 3000) or (>= 5000 and < 6000) => "temporary",
            _ => "event",
        };
    }

    private static void ReportVerdict(List<Item> items, string area, string report, Func<string, bool> isProblem, Func<string, string> areaOf)
    {
        if (!File.Exists(report))
            return;
        var problems = ReadShared(report).Where(isProblem).ToList();
        if (problems.Count == 0)
            items.Add(new Item(Level.Ok, area, $"{Path.GetFileName(report)}: no problems"));
        foreach (var p in problems.Take(8))
            items.Add(new Item(Level.Fail, areaOf(p), p.Trim()));
        if (problems.Count > 8)
            items.Add(new Item(Level.Fail, area, $"… {problems.Count - 8} more in {report}"));
    }

    /// <summary>me3's own logs, newest first: %LOCALAPPDATA%\garyttierney\me3\data\logs (ProjectDirs com.github/garyttierney/me3).</summary>
    public static IEnumerable<string> Me3Logs(string root)
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "garyttierney", "me3", "data", "logs"),
            Path.Combine(root, "TheDarkSign", "Tools", "me3"),
        };
        return dirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.log", SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc);
    }

    /// <summary>Reads a text file the game may still have open for writing.</summary>
    public static List<string> ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? FileVersion(string exe)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return info.FileVersion is { Length: > 0 } v ? Regex.Replace(v, @"\s", "") : null;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The report as text, worst first within each area's order of appearance.</summary>
    /// <summary>The enemy porter's report: how many characters got which method, and its failure notes.</summary>
    public static void CheckEnemyReport(List<Item> items, string path)
    {
        if (!File.Exists(path))
            return;
        var lines = ReadShared(path).Where(l => l.Length > 0).ToList();
        var methods = lines.Skip(1).Where(l => !l.StartsWith('#')).Select(l => l.Split('\t'))
            .Where(cols => cols.Length > 7).GroupBy(cols => cols[7]).ToDictionary(g => g.Key, g => g.Count());
        var failures = lines.Where(l => l.StartsWith('#') && l.Contains("failed", StringComparison.OrdinalIgnoreCase)).ToList();
        int Count(string m) => methods.GetValueOrDefault(m);
        items.Add(new Item(Count("full") + Count("model") > 0 ? Level.Ok : Level.Info, "Enemies",
            $"enemy port: {Count("full")} with DS3 model and animations, {Count("model")} DS3 model only, {Count("stats")} on Elden Ring bodies, " +
            $"{Count("skipped")} skipped (Work\\reports\\enemy-port.tsv)"));
        if (failures.Count > 0)
            items.Add(new Item(Level.Warn, "Enemies", $"enemy port: {failures.Count} failure note(s), first: {failures[0].TrimStart('#', ' ')}. " +
                "Many failures: try Fallbacks enemy-port B or C."));
    }

    public static string Format(IEnumerable<Item> items) =>
        string.Join(Environment.NewLine, items.Select(i => $"{Tag(i.Level),-5} {i.Area}: {i.Text}"));

    public static string Tag(Level level) => level switch
    {
        Level.Ok => "OK",
        Level.Info => "INFO",
        Level.Warn => "WARN",
        _ => "FAIL",
    };
}
