using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using DarkSign.Core;
using Microsoft.Win32;

namespace DarkSign.Launcher;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, LauncherSettings.FileName);
    // Elden Ring has its own panel (Play with the ported content); the list holds the other games.
    private readonly ObservableCollection<GameRow> _rows =
        new(GameDefinition.All.Where(d => d.Id != GameId.EldenRing).Select(d => new GameRow(d)));
    private readonly LauncherSettings _settings;
    private bool _loadingProfile;

    public MainWindow()
    {
        InitializeComponent();
        _settings = LauncherSettings.Load(_settingsPath);
        GameGrid.ItemsSource = _rows;

        RootBox.Text = Root;
        foreach (var row in _rows)
            row.ExePath = _settings.GameExePaths.GetValueOrDefault(row.Definition.Id, "");
        RefreshProfiles();
        TestMenuBox.IsChecked = _settings.TestMenu;

        // Games copied in after the last scan are picked up without pressing "Scan for games".
        if (_rows.Any(r => string.IsNullOrEmpty(r.ExePath)) || !_settings.GameExePaths.ContainsKey(GameId.EldenRing))
            Scan();
        RefreshContentText();

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

            StatusText.Text = $"Updating the launcher to v{update.Version.ToString(3)}…";
            IsEnabled = false;
            await updater.InstallAsync(update, exePath);
            SelfUpdater.Restart(exePath);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            IsEnabled = true;
            StatusText.Text = string.IsNullOrWhiteSpace(_settings.GitHubToken) && ex.Message.Contains("404")
                ? $"Update check skipped: the repository is private, so set \"GitHubToken\" in {LauncherSettings.FileName}."
                : $"Update check failed: {ex.Message}";
        }
    }

    private string Root => string.IsNullOrWhiteSpace(_settings.RootPath)
        ? LauncherSettings.ResolveDefaultRoot(AppContext.BaseDirectory)
        : _settings.RootPath;

    private void Scan()
    {
        var found = GameDetector.Scan(Root);
        foreach (var game in found)
        {
            if (game.Id == GameId.EldenRing)
                _settings.GameExePaths[GameId.EldenRing] = game.ExePath;
            else
                _rows.First(r => r.Definition.Id == game.Id).ExePath = game.ExePath;
        }
        StatusText.Text = $"Found {found.Count} of {GameDefinition.All.Count} games in {Root}.";
        SaveSettings();
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

    private void OnScan(object sender, RoutedEventArgs e) => Scan();

    private void OnChangeRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select The Dark Sign project folder", InitialDirectory = Root };
        if (dialog.ShowDialog(this) != true)
            return;
        _settings.RootPath = dialog.FolderName;
        RootBox.Text = Root;
        foreach (var row in _rows.Where(r => !PathGuard.IsInsideRoot(r.ExePath, Root)))
            row.ExePath = "";
        _settings.GameExePaths.Remove(GameId.EldenRing);
        Scan();
        RefreshContentText();
    }

    private void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        var row = (GameRow)((FrameworkElement)sender).DataContext;
        var dialog = new OpenFileDialog
        {
            Title = $"Select the {row.DisplayName} exe",
            Filter = "Executables (*.exe)|*.exe",
            InitialDirectory = Root,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        if (!PathGuard.IsInsideRoot(dialog.FileName, Root))
        {
            MessageBox.Show(this, $"Only games inside {Root} can be used. Vanilla installs stay untouched.",
                "Outside project folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        row.ExePath = dialog.FileName;
        SaveSettings();
    }

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
            Process.Start(start);
            StatusText.Text = $"Launched {row.DisplayName}. Saves: {plan.SaveDirectory}";
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
        ContentText.Text = !_settings.GameExePaths.ContainsKey(GameId.EldenRing)
            ? $"Elden Ring was not found in {Root}."
            : setup.ContentVersion is { } version
                ? $"Dark Souls III weapons are ported (content build {version}). They're sold by Merchant Kalé."
                : "The content isn't built yet. The first Play builds it from your game files, which takes a few minutes.";
    }

    private async void OnPlayEldenRing(object sender, RoutedEventArgs e) => await PlayEldenRingAsync(rebuild: false, launch: true);

    private async void OnRebuildContent(object sender, RoutedEventArgs e) => await PlayEldenRingAsync(rebuild: true, launch: false);

    /// <summary>
    /// Gets everything ready and starts Elden Ring through ModEngine2: the porter builds the content mod when it is
    /// missing or out of date, and ModEngine2 loads it with the save redirect and (optionally) the F1 test menu.
    /// </summary>
    private async Task PlayEldenRingAsync(bool rebuild, bool launch)
    {
        SaveSettings();
        LaunchPlan plan;
        try
        {
            plan = LaunchPlanner.Plan(_settings, Root, GameId.EldenRing);
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
            StatusText.Text = "Elden Ring is already running.";
            return;
        }

        var setup = new EldenRingSetup(Root);
        SetBusy(true);
        try
        {
            RemoveGameFolderProxy(plan.ExePath);
            var modEngine = await EnsureModEngineAsync(setup);
            var porterVersion = await EnsurePorterAsync(setup);
            if (rebuild || setup.NeedsContentBuild(porterVersion))
            {
                if (!await BuildContentAsync(setup))
                    return;
                setup.MarkContentBuilt(porterVersion);
            }
            RefreshContentText();
            if (!launch)
            {
                StatusText.Text = "Content rebuilt.";
                return;
            }

            SaveIsolation.EnsureSaveDirectory(Root, _settings.GetActiveProfile().Name, GameId.EldenRing);
            var dlls = new List<string> { setup.WriteDll(SaveIsolation.RedirectDllName, RedirectDll.Bytes) };
            var env = new Dictionary<string, string>();
            if (_settings.TestMenu && TestMenuDll.Bytes is not null)
            {
                dlls.Add(setup.WriteDll(EldenRingSetup.TestMenuDllName, TestMenuDll.Bytes));
                env[EldenRingSetup.ItemsVariable] = setup.ItemsFile;
            }
            var config = setup.WriteConfig(dlls);
            Process.Start(EldenRingSetup.LaunchInfo(plan, modEngine, config, env));
            StatusText.Text = $"Launched Elden Ring. Saves: {plan.SaveDirectory}" + (env.Count > 0
                ? ". Press F1 in game for the test menu (it shows over Borderless or Windowed mode)."
                : ".");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                       or InvalidOperationException or UnauthorizedAccessException
                                       or IsolationViolationException or Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "Cannot start Elden Ring", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = ex.Message;
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
        PlayButton.Content = busy ? "WORKING…" : "PLAY";
    }

    /// <summary>
    /// Older launchers put the save redirect in the game folder as dinput8.dll. ModEngine2 now loads it, and two
    /// copies would both hook the game, so our old copy is removed (never another mod's dinput8.dll).
    /// </summary>
    private void RemoveGameFolderProxy(string exePath)
    {
        var state = SaveRedirectInstaller.Check(GameId.EldenRing, exePath, null);
        if (state is RedirectState.Installed or RedirectState.Outdated)
        {
            var proxy = SaveRedirectInstaller.ProxyPath(exePath);
            PathGuard.EnsureInsideRoot(proxy, Root);
            File.Delete(proxy);
        }
    }

    private async Task<string> EnsureModEngineAsync(EldenRingSetup setup)
    {
        if (setup.FindModEngineLauncher() is { } existing)
            return existing;
        StatusText.Text = "Downloading ModEngine2…";
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
            StatusText.Text = "Downloading the porter…";
            var zip = DownloadPath($"DarkSignPorter-{version}.zip");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            if (await new SelfUpdater(_settings.GitHubToken, http).DownloadAssetAsync(SelfUpdater.TagPrefix + version, EldenRingSetup.PorterAsset, zip))
                setup.InstallPorter(zip, version);
        }
        return setup.PorterVersion
            ?? throw new InvalidOperationException($"The porter is missing from {setup.PorterDir} and could not be downloaded.");
    }

    /// <summary>Runs the porter, which builds TheDarkSign\Mods\ER from the game files. Output goes to a log file.</summary>
    private async Task<bool> BuildContentAsync(EldenRingSetup setup)
    {
        Directory.CreateDirectory(setup.LogDir);
        var log = Path.Combine(setup.LogDir, $"porter-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        StatusText.Text = "Building the Dark Souls III content from your game files (a few minutes)…";
        var start = new ProcessStartInfo(setup.PorterExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "--root", Root, "port-weapons" })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
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
            Dispatcher.BeginInvoke(() => StatusText.Text = $"Building content: {line}");
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        process.WaitForExit(); // flushes the output events

        if (process.ExitCode == 0)
            return true;
        string tail;
        lock (last)
            tail = string.Join("\n", last);
        MessageBox.Show(this, $"Building the content failed. The last lines were:\n\n{tail}\n\nFull log: {log}",
            "Content build failed", MessageBoxButton.OK, MessageBoxImage.Error);
        StatusText.Text = $"Content build failed; see {log}";
        return false;
    }

    private string DownloadPath(string fileName)
    {
        var dir = Path.Combine(Root, "TheDarkSign", "Work", "downloads");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        PathGuard.EnsureInsideRoot(path, Root);
        return path;
    }

    private void OnTestMenuChanged(object sender, RoutedEventArgs e)
    {
        _settings.TestMenu = TestMenuBox.IsChecked == true;
        _settings.Save(_settingsPath);
    }

    private void OnOpenModFolder(object sender, RoutedEventArgs e)
    {
        var dir = new EldenRingSetup(Root).ModDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
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
            StatusText.Text = $"A profile named \"{name}\" already exists.";
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
            StatusText.Text = "The last profile cannot be deleted.";
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
    }
}
