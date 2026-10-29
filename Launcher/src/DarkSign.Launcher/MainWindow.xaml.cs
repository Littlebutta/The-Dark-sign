using System.Collections.ObjectModel;
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
    private readonly ObservableCollection<GameRow> _rows = new(GameDefinition.All.Select(d => new GameRow(d)));
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

        // Games copied in after the last scan are picked up without pressing "Scan for games".
        if (_rows.Any(r => string.IsNullOrEmpty(r.ExePath)))
            Scan();

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
            _rows.First(r => r.Definition.Id == game.Id).ExePath = game.ExePath;
        StatusText.Text = $"Found {found.Count} of {_rows.Count} games in {Root}.";
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
        Scan();
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

        if (!plan.SaveRedirectInstalled && _settings.RequireSaveRedirect)
        {
            MessageBox.Show(this,
                $"{SaveIsolation.RedirectDllName} is not installed next to {Path.GetFileName(plan.ExePath)}.\n\n" +
                "Without it the game would write its normal saves to AppData, which could touch your vanilla saves. " +
                $"Set \"RequireSaveRedirect\": false in {LauncherSettings.FileName} only if you accept that.",
                "Save redirect missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveIsolation.EnsureSaveDirectory(Root, _settings.GetActiveProfile().Name, plan.Game);
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
