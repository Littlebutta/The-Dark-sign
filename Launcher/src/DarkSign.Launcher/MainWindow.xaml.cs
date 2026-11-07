using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DarkSign.Core;
using Microsoft.Win32;

namespace DarkSign.Launcher;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, LauncherSettings.FileName);
    // Only the two games the mod is built from have a row (Dark Souls III, and Elden Ring, the host played with the
    // panel's PLAY button). Paths saved for the other games stay in the settings, untouched.
    private static readonly GameId[] ShownGames = [GameId.DarkSouls3, GameId.EldenRing];
    private readonly ObservableCollection<GameRow> _rows;
    private readonly ActivityLog _log;
    private readonly LauncherSettings _settings;
    private bool _loadingProfile;

    public MainWindow()
    {
        InitializeComponent();
        BuildTitle.FontFamily = DarksignProgress.Cinzel;
        _log = new ActivityLog(LogBox, LogFollowBox, Path.Combine(AppContext.BaseDirectory, ActivityLog.FileName));
        _settings = LauncherSettings.Load(_settingsPath);
        _log.Write($"The Dark Sign launcher v{SelfUpdater.CurrentVersion.ToString(3)} started from {AppContext.BaseDirectory}");
        _rows = new(GameDefinition.All.Where(d => ShownGames.Contains(d.Id)).Select(d => new GameRow(d, () => Root)));
        GameGrid.ItemsSource = _rows;

        RootBox.Text = Root;
        foreach (var row in _rows)
            row.ExePath = _settings.GameExePaths.GetValueOrDefault(row.Definition.Id, "");
        RefreshProfiles();
        TestMenuBox.IsChecked = _settings.TestMenu;
        SeamlessBox.IsChecked = _settings.SeamlessCoop;

        // Games installed or copied in since the last start are picked up without pressing "Find my games".
        if (_rows.Any(r => !File.Exists(r.ExePath)))
            Scan(replaceFound: false);
        RefreshContentText();
        _log.Write($"Data folder: {Root}");
        foreach (var row in _rows)
            _log.Write($"{row.DisplayName}: {(string.IsNullOrEmpty(row.ExePath) ? "(not set)" : row.ExePath)} [{row.Status}]");

        Title = $"The Dark Sign  v{SelfUpdater.CurrentVersion.ToString(3)}";
        Loaded += async (_, _) => await UpdateLauncherAsync();
    }

    private async Task UpdateLauncherAsync()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null || !_settings.AutoUpdate)
            return;
        SelfUpdater.CleanUpPreviousUpdate(exePath);

        try
        {
            var updater = new SelfUpdater(_settings.GitHubToken);
            var update = await updater.CheckAsync(SelfUpdater.CurrentVersion);
            if (update is null)
                return;

            SetStatus($"Updating the launcher to v{update.Version.ToString(3)}…");
            IsEnabled = false;
            await updater.InstallAsync(update, exePath);
            SelfUpdater.Restart(exePath);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            IsEnabled = true;
            SetStatus(string.IsNullOrWhiteSpace(_settings.GitHubToken) && ex.Message.Contains("404")
                ? $"Update check skipped: the repository is private, so set \"GitHubToken\" in {LauncherSettings.FileName}."
                : $"Update check failed: {ex.Message}");
        }
    }

    private string Root => string.IsNullOrWhiteSpace(_settings.RootPath)
        ? LauncherSettings.ResolveDefaultRoot(AppContext.BaseDirectory)
        : _settings.RootPath;

    /// <summary>
    /// Looks for the games in the data folder, then in every Steam library. <paramref name="replaceFound"/> false keeps
    /// paths the player already set that still exist (only missing games are filled in).
    /// </summary>
    private void Scan(bool replaceFound)
    {
        var found = GameDetector.ScanAll(Root, SteamLibraries.CommonFolders());
        foreach (var game in found)
        {
            var row = _rows.First(r => r.Definition.Id == game.Id);
            if (replaceFound || !File.Exists(row.ExePath))
                row.ExePath = game.ExePath;
        }
        var missing = _rows.Where(r => !File.Exists(r.ExePath)).Select(r => r.DisplayName).ToList();
        SetStatus(missing.Count == 0
            ? "Found Dark Souls III and Elden Ring."
            : $"Found {_rows.Count - missing.Count} of {_rows.Count} games. Not found: {string.Join(", ", missing)}. " +
              "Use Browse… to point at your own install.");
        SaveSettings();
        RefreshContentText();
    }

    private void RefreshProfiles()
    {
        _loadingProfile = true;
        ProfileBox.ItemsSource = _settings.Profiles.Select(p => p.Name).ToList();
        ProfileBox.SelectedItem = _settings.GetActiveProfile().Name;
        _loadingProfile = false;
        LoadProfileArguments();
    }

    private void LoadProfileArguments()
    {
        var profile = _settings.GetActiveProfile();
        foreach (var row in _rows)
            row.Arguments = profile.For(row.Definition.Id).ExtraArguments;
    }

    private void StoreRowsIntoSettings()
    {
        var profile = _settings.GetActiveProfile();
        foreach (var row in _rows)
        {
            profile.For(row.Definition.Id).ExtraArguments = row.Arguments;
            if (string.IsNullOrEmpty(row.ExePath))
                _settings.GameExePaths.Remove(row.Definition.Id);
            else
                _settings.GameExePaths[row.Definition.Id] = row.ExePath;
        }
    }

    private void SaveSettings()
    {
        StoreRowsIntoSettings();
        _settings.Save(_settingsPath);
    }

    private void OnScan(object sender, RoutedEventArgs e) => Scan(replaceFound: true);

    private void OnChangeRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the data folder (the mod, its tools and your Dark Sign saves go in a TheDarkSign folder inside it)",
            InitialDirectory = Root,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        _settings.RootPath = dialog.FolderName;
        RootBox.Text = Root;
        // Game paths stay as they are (they may be anywhere); missing ones are looked for in the new folder.
        foreach (var row in _rows)
            row.Refresh();
        Scan(replaceFound: false);
    }

    private void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        var row = (GameRow)((FrameworkElement)sender).DataContext;
        var exeName = row.Definition.ExeNames[0];
        var dialog = new OpenFolderDialog
        {
            Title = $"Select your {row.DisplayName} folder (the one with {exeName}, or the folder above it)",
            InitialDirectory = File.Exists(row.ExePath) ? Path.GetDirectoryName(row.ExePath) : Root,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        // The player's own install can be anywhere (e.g. their Steam library); it is only read.
        var exe = GameDetector.FindIn(row.Definition.Id, dialog.FolderName);
        if (exe is null)
        {
            MessageBox.Show(this, $"{exeName} is not in {dialog.FolderName} or the folders below it.\n\n" +
                    $"For Steam, pick steamapps\\common\\{SteamFolderHint(row.Definition.Id)}.",
                "Game not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        row.ExePath = exe;
        SaveSettings();
        RefreshContentText();
        SetStatus($"{row.DisplayName}: {exe}");
    }

    private static string SteamFolderHint(GameId id) => id switch
    {
        GameId.EldenRing => "ELDEN RING",
        GameId.DarkSouls3 => "DARK SOULS III",
        GameId.DarkSoulsRemastered => "DARK SOULS REMASTERED",
        _ => "<game>",
    };

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        var row = (GameRow)((FrameworkElement)sender).DataContext;
        GameGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SaveSettings();

        LaunchPlan plan;
        try
        {
            plan = LaunchPlanner.Plan(_settings, Root, row.Definition.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IsolationViolationException)
        {
            MessageBox.Show(this, ex.Message, "Cannot launch", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!InstallSaveRedirect(row, plan))
            return;

        var profileName = _settings.GetActiveProfile().Name;
        SaveIsolation.EnsureSaveDirectory(Root, profileName, plan.Game);
        if (GameDefinition.Get(plan.Game).SaveRedirect == SaveRedirectMethod.BbportUserDir)
            BbportConfig.Prepare(Root, profileName, plan.SaveDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        if (plan.SteamAppIdFile is not null && !File.Exists(plan.SteamAppIdFile))
            File.WriteAllText(plan.SteamAppIdFile, GameDefinition.Get(plan.Game).SteamAppId.ToString());

        var start = new ProcessStartInfo(plan.ExePath, plan.Arguments)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var (key, value) in plan.Environment)
            start.Environment[key] = value;

        try
        {
            _log.Write($"Starting {plan.ExePath} {plan.Arguments}".TrimEnd());
            Process.Start(start);
            SetStatus($"Launched {row.DisplayName}. Saves: {plan.SaveDirectory}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Puts the redirect DLL in place, or explains why the game must not start.</summary>
    private bool InstallSaveRedirect(GameRow row, LaunchPlan plan)
    {
        string problem;
        try
        {
            var state = RedirectDll.Bytes is null
                ? SaveRedirectInstaller.Check(plan.Game, plan.ExePath, null)
                : SaveRedirectInstaller.Install(plan.Game, plan.ExePath, RedirectDll.Bytes, Root);
            row.Refresh();
            problem = state switch
            {
                RedirectState.Installed or RedirectState.Outdated => "",
                RedirectState.Conflict =>
                    $"Another mod already uses {SaveRedirectInstaller.ProxyPath(plan.ExePath)}, so the save redirect cannot load.",
                _ => "This launcher build does not include the save-redirect DLL.",
            };
        }
        catch (IOException ex)
        {
            problem = $"The save-redirect DLL could not be installed: {ex.Message}";
        }

        if (problem.Length == 0 || !_settings.RequireSaveRedirect)
            return true;
        MessageBox.Show(this,
            problem + "\n\nWithout it the game would write its normal saves to AppData, which could touch your vanilla saves. " +
            $"Set \"RequireSaveRedirect\": false in {LauncherSettings.FileName} only if you accept that.",
            "Save redirect missing", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void RefreshContentText()
    {
        var setup = new EldenRingSetup(Root);
        var missing = new[] { GameId.EldenRing, GameId.DarkSouls3 }
            .Where(id => !File.Exists(_settings.GameExePaths.GetValueOrDefault(id, "")))
            .Select(id => GameDefinition.Get(id).DisplayName).ToList();
        ContentText.Text = missing.Count > 0 && setup.ContentVersion is null
            ? $"To build the mod from your own game files, point the launcher at {string.Join(" and ", missing)} " +
              "in the list below (Browse…, or Find my games for Steam installs)."
            : !File.Exists(_settings.GameExePaths.GetValueOrDefault(GameId.EldenRing, ""))
            ? "Elden Ring was not found: point the launcher at it in the list below (Browse…)."
            : setup.ContentVersion is { } version
                ? $"Dark Souls III weapons are ported (content build {version}). They're sold by Merchant Kalé." +
                  (CoopCheckCode(setup) is { } code ? $" Mod code {code}: co-op partners need the same code." : "")
                : "The content isn't built yet. The first Play builds it from your game files, which takes a few minutes.";
    }

    private async void OnPlayEldenRing(object sender, RoutedEventArgs e) => await PlayEldenRingAsync(rebuild: false, launch: true);

    private async void OnRebuildContent(object sender, RoutedEventArgs e) => await PlayEldenRingAsync(rebuild: true, launch: false);

    /// <summary>
    /// Gets everything ready and starts Elden Ring through me3: the porter builds the content mod when it is
    /// missing or out of date, and me3 loads it (sound banks included) with the save redirect and (optionally) the F1 test menu.
    /// </summary>
    private async Task PlayEldenRingAsync(bool rebuild, bool launch)
    {
        SaveSettings();
        LaunchPlan plan;
        try
        {
            // me3 loads the mod and DLLs at launch, so nothing is written into the Elden Ring folder: any install works.
            plan = LaunchPlanner.Plan(_settings, Root, GameId.EldenRing, gameFolderReadOnly: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IsolationViolationException)
        {
            MessageBox.Show(this, ex.Message, "Cannot launch", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (RedirectDll.Bytes is null)
        {
            MessageBox.Show(this, "This launcher build does not include the save-redirect DLL, so Elden Ring would write over " +
                "your normal saves. Use a released launcher build.", "Save redirect missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (launch && Process.GetProcessesByName("eldenring").Length > 0)
        {
            SetStatus("Elden Ring is already running.");
            return;
        }

        var setup = new EldenRingSetup(Root);
        SetBusy(true);
        try
        {
            RemoveGameFolderProxy(plan.ExePath);
            var coop = _settings.SeamlessCoop;
            var tweaks = Fallbacks.Launch(_settings.Methods, coop);
            var loader = tweaks.ModEngine2 ? await EnsureModEngineAsync(setup) : await EnsureMe3Async(setup);
            var porterVersion = await EnsurePorterAsync(setup);
            // The stamp holds the build methods (Fallbacks window), so switching one rebuilds the content.
            var stamp = Fallbacks.BuildStamp(porterVersion, _settings.Methods);
            if (rebuild || setup.NeedsContentBuild(stamp))
            {
                if (!File.Exists(_settings.GameExePaths.GetValueOrDefault(GameId.DarkSouls3, "")) && !rebuild && launch
                    && File.Exists(Path.Combine(setup.ModDir, "regulation.bin")) && File.Exists(setup.ItemsFile))
                {
                    // Dark Souls III moved or was uninstalled after a build: play the content already built.
                    SetStatus("Dark Souls III wasn't found, so the content wasn't rebuilt; playing the last build.");
                }
                else if (!File.Exists(_settings.GameExePaths.GetValueOrDefault(GameId.DarkSouls3, "")))
                {
                    MessageBox.Show(this, "The mod is built from your own Dark Souls III files, and Dark Souls III wasn't found.\n\n" +
                        "Point the launcher at your Dark Souls III install in the list (Browse…), then press PLAY again.",
                        "Dark Souls III needed", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                else
                {
                    if (!await BuildContentAsync(setup))
                        return;
                    setup.MarkContentBuilt(stamp);
                }
            }
            RefreshContentText();
            if (!launch)
            {
                SetStatus("Content rebuilt.");
                return;
            }

            await EnsureRandomizedWorldAsync(setup, _settings.GetActiveProfile(), remake: false);
            SaveIsolation.EnsureSaveDirectory(Root, _settings.GetActiveProfile().Name, GameId.EldenRing);
            var dlls = new List<string> { setup.WriteDll(SaveIsolation.RedirectDllName, RedirectDll.Bytes) };
            var env = Fallbacks.GameEnvironment(_settings.Methods);
            env[Fallbacks.ModDirVariable] = setup.ModDir;
            var testMenuNeeded = Fallbacks.NeedsTestMenuDll(_settings.Methods);
            if ((_settings.TestMenu || testMenuNeeded) && TestMenuDll.Bytes is not null)
            {
                dlls.Add(setup.WriteDll(EldenRingSetup.TestMenuDllName, TestMenuDll.Bytes));
                env[EldenRingSetup.ItemsVariable] = setup.ItemsFile;
                if (!_settings.TestMenu)
                {
                    // Loaded only for a fallback method (DLL sounds): no menu, no hotkeys.
                    env[Fallbacks.NoOverlayVariable] = "1";
                    env[Fallbacks.NoHotkeysVariable] = "1";
                }
            }
            string? seamless = null;
            if (coop)
            {
                if (SeamlessCoop.Problem(_settings.SeamlessCoopDll) is { } problem)
                    throw new InvalidOperationException("Seamless Co-op is on, but " + char.ToLower(problem[0]) + problem[1..] +
                                                        " Untick Seamless Co-op to play alone.");
                seamless = _settings.SeamlessCoopDll;
                env[SeamlessCoop.CoopVariable] = "1";
            }
            var natives = SeamlessCoop.Natives(seamless, dlls);
            var info = tweaks.ModEngine2
                ? EldenRingSetup.LaunchInfo(plan, loader, setup.WriteConfig(natives), env)
                : EldenRingSetup.Me3LaunchInfo(plan, loader, setup.WriteProfile(natives, tweaks), env);
            _log.Write($"Starting {info.FileName} {(info.ArgumentList.Count > 0 ? string.Join(" ", info.ArgumentList.Select(Quote)) : info.Arguments)}".TrimEnd());
            foreach (var dll in natives)
                _log.Write($"  native DLL: {dll}");
            foreach (var (key, value) in env)
                _log.Write($"  env {key}={value}");
            Process.Start(info);
            var methods = Fallbacks.Summary(_settings.Methods);
            SetStatus($"Launched Elden Ring{(tweaks.ModEngine2 ? " (ModEngine2)" : "")}. Saves: {plan.SaveDirectory}" +
                (methods.Length > 0 ? $". Fallback methods: {methods}" : "") +
                (seamless is not null ? $". Seamless Co-op is on (mod code {CoopCheckCode(setup) ?? "?"})" : "") +
                (!_settings.TestMenu || !env.ContainsKey(EldenRingSetup.ItemsVariable) ? "."
                    : env.ContainsKey(Fallbacks.NoOverlayVariable)
                        ? ". Test menu hotkeys: Ctrl+Shift+F1 start the DS3 run, F2 leave, 1-9 warp to a map, F3 give the DS3 weapons."
                        : ". Press F1 in game for the test menu (it shows over Borderless or Windowed mode)."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                       or InvalidOperationException or UnauthorizedAccessException
                                       or IsolationViolationException or Win32Exception)
        {
            _log.Write($"Error ({ex.GetType().Name}): {ex}");
            MessageBox.Show(this, ex.Message, "Cannot start Elden Ring", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        PlayButton.IsEnabled = !busy;
        RebuildButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        PlayButton.Content = busy ? "WORKING…" : "PLAY";
    }

    /// <summary>
    /// Older launchers put the save redirect in the game folder as dinput8.dll. ModEngine2 now loads it, and two
    /// copies would both hook the game, so our old copy is removed (never another mod's dinput8.dll).
    /// </summary>
    private void RemoveGameFolderProxy(string exePath)
    {
        // Only copies inside the data folder ever got one; a player's own install is never written.
        var proxy = SaveRedirectInstaller.ProxyPath(exePath);
        if (!PathGuard.IsInsideRoot(proxy, Root))
            return;
        var state = SaveRedirectInstaller.Check(GameId.EldenRing, exePath, null);
        if (state is RedirectState.Installed or RedirectState.Outdated)
            File.Delete(proxy);
    }

    private async Task<string> EnsureMe3Async(EldenRingSetup setup)
    {
        if (setup.FindMe3() is { } existing)
            return existing;
        SetStatus("Downloading me3…");
        var zip = DownloadPath(Path.GetFileName(EldenRingSetup.Me3Url));
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TheDarkSignLauncher");
            var bytes = await http.GetByteArrayAsync(EldenRingSetup.Me3Url);
            await File.WriteAllBytesAsync(zip, bytes);
        }
        setup.InstallMe3(zip);
        return setup.FindMe3() ?? throw new InvalidOperationException("me3 was downloaded but me3.exe is missing.");
    }

    private async Task<string> EnsureModEngineAsync(EldenRingSetup setup)
    {
        if (setup.FindModEngineLauncher() is { } existing)
            return existing;
        SetStatus("Downloading ModEngine2…");
        var zip = DownloadPath(Path.GetFileName(EldenRingSetup.ModEngineUrl));
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TheDarkSignLauncher");
            var bytes = await http.GetByteArrayAsync(EldenRingSetup.ModEngineUrl);
            await File.WriteAllBytesAsync(zip, bytes);
        }
        setup.InstallModEngine(zip);
        return setup.FindModEngineLauncher() ?? throw new InvalidOperationException("ModEngine2 was downloaded but its launcher is missing.");
    }

    /// <summary>Gets the porter released with this launcher version (falls back to an installed one).</summary>
    private async Task<string> EnsurePorterAsync(EldenRingSetup setup)
    {
        var version = SelfUpdater.CurrentVersion.ToString(3);
        if (setup.PorterVersion != version && version != "1.0.0")
        {
            SetStatus("Downloading the porter…");
            var zip = DownloadPath($"DarkSignPorter-{version}.zip");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            try
            {
                if (await new SelfUpdater(_settings.GitHubToken, http).DownloadAssetAsync(SelfUpdater.TagPrefix + version, EldenRingSetup.PorterAsset, zip))
                    setup.InstallPorter(zip, version);
            }
            catch (Exception ex) when (setup.PorterVersion is not null && ex is HttpRequestException or TaskCanceledException)
            {
                // No connection: keep playing with the porter already installed (the content stays as it was built).
                SetStatus($"The porter could not be updated ({ex.Message}); using the installed one.");
            }
        }
        return setup.PorterVersion
            ?? throw new InvalidOperationException($"The porter is missing from {setup.PorterDir} and could not be downloaded.");
    }

    /// <summary>
    /// Runs the porter, which builds TheDarkSign\Mods\ER from the player's own game files. port-weapons and port-maps run
    /// side by side as two processes (each on its own cores); port-maps waits for port-weapons only for its last step,
    /// which edits the regulation.bin port-weapons writes. Each has its own log file.
    /// </summary>
    private async Task<bool> BuildContentAsync(EldenRingSetup setup)
    {
        // A running game keeps the mod files open, so the build would fail halfway through.
        while (Process.GetProcessesByName("eldenring").Length > 0)
        {
            if (MessageBox.Show(this, "Elden Ring is still running, and it keeps the mod files open, so the content can't be rebuilt.\n\n" +
                    "Close the game completely (check Task Manager if its window is already gone), then press OK.",
                    "Close Elden Ring first", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                SetStatus("Rebuild cancelled: Elden Ring is still running.");
                return false;
            }
        }
        Directory.CreateDirectory(setup.LogDir);
        var started = Stopwatch.StartNew();
        // Fallback "build-threads" B (one core): port-maps starts only after port-weapons, so nothing runs side by side.
        var serial = Fallbacks.SerialBuild(_settings.Methods);
        var estimator = new BuildEstimator(["weapons", "maps"], _settings.LastBuild, serial, DateTime.Now);
        using var progress = new BuildProgress(this, estimator, started);
        var weapons = StartPorter(setup, "port-weapons", [], line => progress.Update("weapons", line));
        estimator.Start("weapons", DateTime.Now);
        _ = weapons.Completion.ContinueWith(_ => estimator.Finish("weapons", DateTime.Now), TaskScheduler.Default);
        var weaponsFailed = serial && !(await weapons.Completion).Ok;
        var mapsArgs = serial ? new List<string>() : new List<string> { "--after-pid", weapons.Pid.ToString() };
        mapsArgs.AddRange(Fallbacks.MapsArguments(_settings.Methods));
        // The DS3 maps (F1 menu, "DS3 run" tab) are a test feature: if they fail, the weapons still work, so the game
        // starts anyway and the log says what went wrong.
        PorterRun? maps = null;
        try
        {
            if (!weaponsFailed)
            {
                maps = StartPorter(setup, "port-maps", mapsArgs, line => progress.Update("maps", line));
                estimator.Start("maps", DateTime.Now);
                _ = maps.Completion.ContinueWith(_ => estimator.Finish("maps", DateTime.Now), TaskScheduler.Default);
            }
            else
                estimator.Skip("maps");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            estimator.Skip("maps");
            progress.Update("maps", $"could not start: {ex.Message}");
        }

        var weaponsResult = await weapons.Completion;
        var mapsResult = maps is null ? (Ok: false, Tail: "", Log: "") : await maps.Completion;
        // Only parts that worked are kept for the next estimate (a failed run stops early and would skew it).
        foreach (var (part, stat) in estimator.Results())
            if (part == "weapons" ? weaponsResult.Ok : mapsResult.Ok)
                _settings.LastBuild[part] = stat;
        _settings.Save(_settingsPath);
        progress.Done(weaponsResult.Ok);
        if (!weaponsResult.Ok)
        {
            MessageBox.Show(this, $"Building the content failed. The last lines were:\n\n{weaponsResult.Tail}\n\nFull log: {weaponsResult.Log}",
                "Content build failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus($"Content build failed; see {weaponsResult.Log}");
            return false;
        }

        SetStatus($"Content built in {started.Elapsed.TotalMinutes:0.0} min.");
        if (!mapsResult.Ok)
            SetStatus($"The DS3 maps could not be ported (the rest of the content is fine); see {mapsResult.Log}");
        else
        {
            // port-maps reads its output back and checks it; show the verdict so a broken map is known before launch.
            var check = Path.Combine(Root, "TheDarkSign", "Mods", "ER", "darksign_maps_check.txt");
            var verdict = File.Exists(check)
                ? File.ReadLines(check).LastOrDefault(l => l.StartsWith("map-validate:", StringComparison.Ordinal))
                : null;
            if (verdict is not null && !verdict.Contains(" 0 FAIL", StringComparison.Ordinal))
                SetStatus($"DS3 maps ported with problems ({verdict["map-validate:".Length..].Trim()}); see {check}");
        }

        // The randomized world depends on which maps were ported, so it is remade after a rebuild.
        await EnsureRandomizedWorldAsync(setup, _settings.GetActiveProfile(), remake: true);
        return true;
    }

    /// <summary>Makes (or remakes) a randomized world with the porter's randomize command: the profile's own, or a
    /// friend's it joined with a co-op code. It takes seconds and reads no game files; the result lives in
    /// TheDarkSign\Randomizer\&lt;world&gt;.</summary>
    private async Task<bool> MakeRandomizedWorldAsync(EldenRingSetup setup, string worldName, RandomizerSettings settings)
    {
        settings.WriteOptions(Root, worldName);
        var run = StartPorter(setup, "randomize", ["--profile", worldName],
            line => Dispatcher.BeginInvoke(() => StatusText.Text = "Randomizing: " + line));
        var (ok, tail, log) = await run.Completion;
        var check = RandomizerCoop.CheckCode(SeamlessCoop.Fingerprint(setup.ModDir), RandomizerCoop.ReadWorldId(Root, worldName));
        SetStatus(ok
            ? $"Randomized world \"{worldName}\" ({settings.Summary}) is ready and checked finishable." +
              (check is null ? "" : $" Co-op check code {check}: friends playing with you need the same one.") +
              $" Spoiler log: {RandomizerSettings.SpoilerPath(Root, worldName)}"
            : $"The randomizer failed; see {log}");
        if (!ok)
            MessageBox.Show(this, $"The randomizer failed. The last lines were:\n\n{tail}\n\nFull log: {log}",
                "Randomizer failed", MessageBoxButton.OK, MessageBoxImage.Error);
        return ok;
    }

    /// <summary>Makes the profile's active world (own or joined) if it hasn't been made yet.</summary>
    private async Task EnsureRandomizedWorldAsync(EldenRingSetup setup, LaunchProfile profile, bool remake)
    {
        if (RandomizerCoop.ActiveWorld(profile) is not { } world)
            return;
        if (remake || RandomizerCoop.ReadWorldId(Root, world.WorldName) is null)
            await MakeRandomizedWorldAsync(setup, world.WorldName, world.Settings);
    }

    /// <summary>The code co-op partners compare: the content build, plus the randomized world when one is active.</summary>
    private string? CoopCheckCode(EldenRingSetup setup) =>
        RandomizerCoop.CheckCode(SeamlessCoop.Fingerprint(setup.ModDir),
            RandomizerCoop.ActiveWorld(_settings.GetActiveProfile()) is { } w ? RandomizerCoop.ReadWorldId(Root, w.WorldName) : null);

    private async void OnRandomizer(object sender, RoutedEventArgs e)
    {
        StoreRowsIntoSettings();
        var profile = _settings.GetActiveProfile();
        var settings = profile.Randomizer is null
            ? new RandomizerSettings { Enabled = false, Seed = RandomizerSettings.NewSeed() }
            : System.Text.Json.JsonSerializer.Deserialize<RandomizerSettings>(System.Text.Json.JsonSerializer.Serialize(profile.Randomizer))!;
        var result = RandomizerWindow.Show(this, profile.Name, settings, profile.JoinCode, out var joinCode);
        if (result == RandomizerWindow.Result.Cancelled)
            return;
        if (result == RandomizerWindow.Result.NewCharacter)
        {
            // A new character gets a new profile (its own saves) and a fresh seed; the current one is untouched.
            var name = PromptWindow.Ask(this, "New randomized character", "Name for the new character's profile:")?.Trim();
            if (string.IsNullOrEmpty(name))
                return;
            if (_settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus($"A profile named \"{name}\" already exists.");
                return;
            }
            settings.Enabled = true;
            settings.Seed = RandomizerSettings.NewSeed();
            profile = new LaunchProfile { Name = name, Randomizer = settings };
            _settings.Profiles.Add(profile);
            _settings.ActiveProfile = name;
            RefreshProfiles();
        }
        else
        {
            profile.Randomizer = settings;
            profile.JoinCode = joinCode;
        }
        _settings.Save(_settingsPath);
        if (RandomizerCoop.ActiveWorld(profile) is null)
        {
            SetStatus($"Randomizer off for \"{profile.Name}\": it plays the normal game.");
            return;
        }
        try
        {
            var setup = new EldenRingSetup(Root);
            Directory.CreateDirectory(setup.LogDir);
            await EnsurePorterAsync(setup);
            await EnsureRandomizedWorldAsync(setup, profile, remake: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException)
        {
            SetStatus($"The randomizer could not run: {ex.Message}");
        }
    }

    /// <summary>
    /// The build panel: the Darksign ignites as the build goes, with time elapsed, time remaining, a bar across the
    /// panel and the latest line of each porter process. Every line also goes to the activity log (by StartPorter).
    /// </summary>
    private sealed class BuildProgress : IDisposable
    {
        private readonly MainWindow _window;
        private readonly BuildEstimator _estimator;
        private readonly Stopwatch _started;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<string, string> _latest = new();

        public BuildProgress(MainWindow window, BuildEstimator estimator, Stopwatch started)
        {
            _window = window;
            _estimator = estimator;
            _started = started;
            window.WeaponsLine.Text = "starting…";
            window.MapsLine.Text = "starting…";
            window.BuildTitle.Text = "Kindling the Dark Sign…";
            window.Darksign.Fraction = 0;
            window.BarFill.Width = 0;
            window.GameGrid.Visibility = Visibility.Collapsed;
            window.GamesHint.Visibility = Visibility.Collapsed;
            window.BuildPanel.Visibility = Visibility.Visible;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick(), window.Dispatcher);
            _timer.Start();
            Tick();
        }

        public void Update(string part, string line)
        {
            _estimator.Line(part, DateTime.Now);
            lock (_latest)
                _latest[part] = line;
            window_Dispatch(() =>
            {
                lock (_latest)
                {
                    if (_latest.TryGetValue("weapons", out var w)) _window.WeaponsLine.Text = w;
                    if (_latest.TryGetValue("maps", out var m)) _window.MapsLine.Text = m;
                }
                _window.SetStatusQuiet("Building from your game files…");
            });
        }

        private void window_Dispatch(Action action) => _window.Dispatcher.BeginInvoke(action, DispatcherPriority.Background);

        private void Tick()
        {
            var (fraction, left) = _estimator.Estimate(DateTime.Now);
            Show(fraction);
            _window.ElapsedText.Text = BuildEstimator.Format(_started.Elapsed);
            _window.RemainingText.Text = left is { } l
                ? (_estimator.FirstBuild ? "about " : "") + BuildEstimator.Format(TimeSpan.FromSeconds(Math.Ceiling(l))) +
                  (_estimator.FirstBuild ? " (first build: a rough guess)" : "")
                : "almost done…";
        }

        private void Show(double fraction)
        {
            _window.Darksign.Fraction = fraction;
            var track = _window.BarTrack.ActualWidth;
            _window.BarFill.Width = Math.Max(0, track * fraction);
        }

        /// <summary>Ends the animation: a full ring when the build worked. The panel stays up a moment, then the game list returns.</summary>
        public void Done(bool ok)
        {
            _timer.Stop();
            if (ok)
            {
                Show(1);
                _window.RemainingText.Text = "done";
                _window.BuildTitle.Text = "The Dark Sign is kindled";
            }
            else
                _window.BuildTitle.Text = "The build failed";
            _window.ElapsedText.Text = BuildEstimator.Format(_started.Elapsed);
        }

        public void Dispose()
        {
            _timer.Stop();
            var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                _window.BuildPanel.Visibility = Visibility.Collapsed;
                _window.GameGrid.Visibility = Visibility.Visible;
                _window.GamesHint.Visibility = Visibility.Visible;
            };
            hide.Start();
        }
    }

    private sealed record PorterRun(int Pid, Task<(bool Ok, string Tail, string Log)> Completion);

    /// <summary>
    /// Starts one porter command, with its output in its own log file. The player's game paths go with it, so their
    /// installs can be anywhere (they are only read).
    /// </summary>
    private PorterRun StartPorter(EldenRingSetup setup, string command, IReadOnlyList<string> extraArgs, Action<string> onLine)
    {
        var log = Path.Combine(setup.LogDir, $"porter-{command}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var start = new ProcessStartInfo(setup.PorterExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(Root);
        foreach (var (id, exe) in _settings.GameExePaths.Where(g => File.Exists(g.Value)))
        {
            start.ArgumentList.Add("--game");
            start.ArgumentList.Add($"{GameDefinition.Get(id).ShortName}={exe}");
        }
        start.ArgumentList.Add(command);
        foreach (var arg in extraArgs)
            start.ArgumentList.Add(arg);
        // The build methods chosen in the Fallbacks window reach the porter as DARKSIGN_* variables.
        foreach (var (key, value) in Fallbacks.PorterEnvironment(_settings.Methods))
            start.Environment[key] = value;

        _log.Write($"Running porter: {string.Join(" ", start.ArgumentList.Select(Quote))}");
        foreach (var (key, value) in Fallbacks.PorterEnvironment(_settings.Methods))
            _log.Write($"  env {key}={value}");
        var process = Process.Start(start)!;
        var watch = Stopwatch.StartNew();
        var completion = WaitForPorterAsync(process, log, line =>
        {
            _log.Write($"[{command}] {line}");
            onLine(line);
        });
        _ = completion.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                _log.Write($"Porter {command} {(t.Result.Ok ? "finished" : "FAILED")} after {BuildEstimator.Format(watch.Elapsed)}. Log: {t.Result.Log}");
        }, TaskScheduler.Default);
        return new PorterRun(process.Id, completion);
    }

    private static async Task<(bool Ok, string Tail, string Log)> WaitForPorterAsync(Process process, string log, Action<string> onLine)
    {
        using (process)
        {
            await using var writer = new StreamWriter(log);
            var last = new Queue<string>();
            void OnLine(string? line)
            {
                if (line is null)
                    return;
                lock (last)
                {
                    writer.WriteLine(line);
                    last.Enqueue(line);
                    if (last.Count > 8)
                        last.Dequeue();
                }
                onLine(line);
            }
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit(); // flushes the output events

            string tail;
            lock (last)
                tail = string.Join("\n", last);
            return (process.ExitCode == 0, tail, log);
        }
    }

    /// <summary>Shows a message on the status line and writes it to the activity log.</summary>
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        _log.Write(text);
    }

    /// <summary>Status line only (for messages repeated many times, whose detail is already in the log).</summary>
    private void SetStatusQuiet(string text) => StatusText.Text = text;

    private static string Quote(string arg) => arg.Contains(' ') || arg.Length == 0 ? $"\"{arg}\"" : arg;

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogBox.Text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void OnOpenLogFile(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_log.Path))
            Process.Start(new ProcessStartInfo(_log.Path) { UseShellExecute = true });
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _log.ClearView();

    private string DownloadPath(string fileName)
    {
        var dir = Path.Combine(Root, "TheDarkSign", "Work", "downloads");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        PathGuard.EnsureInsideRoot(path, Root);
        return path;
    }

    private void OnFallbacks(object sender, RoutedEventArgs e)
    {
        StoreRowsIntoSettings();
        if (!FallbacksWindow.Show(this, _settings.Methods, out var methods))
            return;
        var before = Fallbacks.BuildSignature(_settings.Methods);
        _settings.Methods = methods;
        _settings.Save(_settingsPath);
        var summary = Fallbacks.Summary(methods);
        SetStatus((summary.Length == 0 ? "All features use their default method (A)." : $"Fallback methods: {summary}.") +
                          (Fallbacks.BuildSignature(methods) != before ? " The content is rebuilt on the next PLAY." : ""));
    }

    /// <summary>Pre-flight check window: fast checks, the porter's checks of the built mod, and the test report zip.</summary>
    private void OnPreflight(object sender, RoutedEventArgs e)
    {
        StoreRowsIntoSettings();
        SaveSettings();
        var setup = new EldenRingSetup(Root);
        PreflightWindow.Show(this,
            () => Preflight.Run(new Preflight.Context(Root, _settings, setup.PorterVersion)),
            async onLine =>
            {
                if (!File.Exists(setup.PorterExe) || !File.Exists(Path.Combine(setup.ModDir, "regulation.bin")))
                    return false;
                Directory.CreateDirectory(setup.LogDir);
                try
                {
                    await StartPorter(setup, "validate-mod", [], onLine).Completion;
                    await StartPorter(setup, "map-validate", [], onLine).Completion;
                    // validate-mod exits 1 when it found errors; the report is what counts.
                    return File.Exists(Path.Combine(Root, "TheDarkSign", "Work", "reports", "validate-mod.txt"));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
                {
                    onLine(ex.Message);
                    return false;
                }
            },
            preflight => Diagnostics.Write(Root, _settings, AppContext.BaseDirectory, SelfUpdater.CurrentVersion.ToString(3), preflight));
    }

    private void OnTestMenuChanged(object sender, RoutedEventArgs e)
    {
        _settings.TestMenu = TestMenuBox.IsChecked == true;
        _settings.Save(_settingsPath);
    }

    private void OnSeamlessChanged(object sender, RoutedEventArgs e)
    {
        var on = SeamlessBox.IsChecked == true;
        if (on && SeamlessCoop.Problem(_settings.SeamlessCoopDll) is not null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose ersc.dll from your Seamless Co-op download (the SeamlessCoop folder)",
                Filter = "Seamless Co-op|ersc.dll",
            };
            if (dialog.ShowDialog(this) != true || SeamlessCoop.Problem(dialog.FileName) is { } problem)
            {
                SeamlessBox.IsChecked = false;
                return;
            }
            _settings.SeamlessCoopDll = dialog.FileName;
        }
        if (on && SeamlessCoop.Password(_settings.SeamlessCoopDll).Length == 0)
            SetStatus($"Set a co-op password in {SeamlessCoop.SettingsName} next to ersc.dll; everyone in the session needs the same one.");
        _settings.SeamlessCoop = on;
        _settings.Save(_settingsPath);
    }

    private void OnOpenModFolder(object sender, RoutedEventArgs e)
    {
        var dir = new EldenRingSetup(Root).ModDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Removes The Dark Sign: the built mod, me3, the porter, downloads, logs and caches (all in the data folder's
    /// TheDarkSign folder), and the launcher's save-redirect DLLs in game copies inside the data folder. Game installs
    /// are never modded in place, so nothing else needs undoing. Saves are kept unless the player says otherwise.
    /// </summary>
    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (Process.GetProcessesByName("eldenring").Length > 0 || Process.GetProcessesByName("DarkSignPorter").Length > 0)
        {
            MessageBox.Show(this, "Close Elden Ring and wait for any content build to finish first: they keep the mod files open.",
                "Uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var data = Path.Combine(Root, Uninstaller.DataFolderName);
        var answer = MessageBox.Show(this,
            $"Uninstall The Dark Sign from this PC?\n\nThis deletes the mod and everything the launcher downloaded or built in\n{data}\n" +
            "and the launcher's save-redirect DLLs in game copies inside the data folder. Your game installs are not changed: " +
            "Elden Ring starts exactly as before.\n\n" +
            "Keep your Dark Sign saves?\n  Yes: uninstall and keep the saves (TheDarkSign\\Saves)\n  No: uninstall and delete the saves too\n  Cancel: do nothing",
            "Uninstall The Dark Sign", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel)
            return;
        var deleteSaves = answer == MessageBoxResult.No;
        if (deleteSaves && MessageBox.Show(this, "Delete every Dark Sign save, for every profile? This can't be undone.",
                "Delete saves", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // Every game path the settings know, not only the rows shown: older launchers put DLLs next to the others too.
        var plan = Uninstaller.MakePlan(Root, _settings.GameExePaths.Values.Concat(_rows.Select(r => r.ExePath)).Distinct(), deleteSaves);
        _log.Write($"Uninstall: {(deleteSaves ? "deleting saves too" : "keeping saves")}");
        var failed = Uninstaller.Run(Root, plan);
        foreach (var row in _rows)
            row.Refresh();
        RefreshContentText();
        var launcherDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;
        if (failed.Count > 0)
        {
            MessageBox.Show(this, "Some files could not be removed (close any program using them and press Uninstall again):\n\n" +
                                  string.Join("\n", failed.Take(10)), "Uninstall", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus($"Uninstall left {failed.Count} item(s) behind.");
            return;
        }
        SetStatus("The Dark Sign is uninstalled" + (deleteSaves ? "." : $"; your saves are still in {SaveIsolation.SavesRoot(Root)}."));
        MessageBox.Show(this,
            "The Dark Sign is uninstalled. " + (deleteSaves ? "" : $"Your saves were kept in {SaveIsolation.SavesRoot(Root)}. ") +
            $"\n\nTo remove the launcher too, close it and delete its folder:\n{launcherDir}\n\nPressing PLAY again reinstalls everything.",
            "Uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || ProfileBox.SelectedItem is not string name)
            return;
        StoreRowsIntoSettings();
        _settings.ActiveProfile = name;
        LoadProfileArguments();
        _settings.Save(_settingsPath);
    }

    private void OnNewProfile(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(this, "New profile", "Profile name:");
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();
        if (_settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"A profile named \"{name}\" already exists.");
            return;
        }
        StoreRowsIntoSettings();
        _settings.Profiles.Add(new LaunchProfile { Name = name });
        _settings.ActiveProfile = name;
        RefreshProfiles();
        _settings.Save(_settingsPath);
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (_settings.Profiles.Count <= 1)
        {
            SetStatus("The last profile cannot be deleted.");
            return;
        }
        var profile = _settings.GetActiveProfile();
        var answer = MessageBox.Show(this,
            $"Delete profile \"{profile.Name}\"? Its saves in TheDarkSign\\Saves are kept.",
            "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        _settings.Profiles.Remove(profile);
        _settings.ActiveProfile = _settings.Profiles[0].Name;
        RefreshProfiles();
        _settings.Save(_settingsPath);
    }

    private void OnOpenSaves(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(SaveIsolation.SavesRoot(Root), SaveIsolation.SanitizeFolderName(_settings.GetActiveProfile().Name));
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        GameGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SaveSettings();
        _log.Write("Launcher closed.");
        _log.Dispose();
    }
}
