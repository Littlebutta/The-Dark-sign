using DarkSign.Core;

namespace DarkSign.Core.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dksign-tests-" + Guid.NewGuid().ToString("N"), "The Dark sign");

    public CoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Detects_games_in_their_own_folders_including_nested_steam_layout()
    {
        var dsr = Touch("Dark Souls Remastered", "DarkSoulsRemastered.exe");
        var ds3 = Touch("DARK SOULS III", "Game", "DarkSoulsIII.exe");
        var er = Touch("Elden Ring", "Game", "eldenring.exe");
        Touch("Elden Ring", "Game", "start_protected_game.exe");
        var bb = Touch("Bloodborne", "Play Bloodborne.exe");
        Touch("Bloodborne", "Bloodborne.exe");

        var found = GameDetector.Scan(_root).ToDictionary(g => g.Id, g => g.ExePath);

        Assert.Equal(dsr, found[GameId.DarkSoulsRemastered]);
        Assert.Equal(ds3, found[GameId.DarkSouls3]);
        Assert.Equal(er, found[GameId.EldenRing]);
        Assert.Equal(bb, found[GameId.Bloodborne]);
    }

    [Fact]
    public void Ignores_launcher_and_save_folders_and_generic_exes_in_unrelated_folders()
    {
        Touch("Launcher", "DarkSoulsIII.exe");
        Touch("Emulators", "shadPS4.exe");

        Assert.Empty(GameDetector.Scan(_root));
    }

    [Fact]
    public void Path_guard_rejects_paths_outside_root_and_sibling_prefixes()
    {
        Assert.True(PathGuard.IsInsideRoot(Path.Combine(_root, "Game", "a.exe"), _root));
        Assert.False(PathGuard.IsInsideRoot(Path.Combine(_root, "..", "Steam", "a.exe"), _root));
        Assert.False(PathGuard.IsInsideRoot(_root + " (copy)" + Path.DirectorySeparatorChar + "a.exe", _root));
    }

    [Fact]
    public void Save_directory_is_under_TheDarkSign_Saves_per_profile_and_game()
    {
        var dir = SaveIsolation.EnsureSaveDirectory(_root, "Run: 1", GameId.DarkSouls3);

        Assert.Equal(Path.Combine(_root, "TheDarkSign", "Saves", "Run_ 1", "DS3"), dir);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Plan_sets_save_redirect_environment_and_steam_app_id()
    {
        var exe = Touch("DARK SOULS III", "Game", "DarkSoulsIII.exe");
        var settings = new LauncherSettings { GameExePaths = { [GameId.DarkSouls3] = exe } };
        settings.GetActiveProfile().For(GameId.DarkSouls3).ExtraArguments = " -windowed ";

        var plan = LaunchPlanner.Plan(settings, _root, GameId.DarkSouls3);

        Assert.Equal("-windowed", plan.Arguments);
        Assert.Equal(Path.GetDirectoryName(exe), plan.WorkingDirectory);
        Assert.Equal(".dksign", plan.Environment["DKSIGN_SAVE_EXT"]);
        Assert.Equal(plan.SaveDirectory, plan.Environment["DKSIGN_SAVE_DIR"]);
        Assert.Equal("374320", plan.Environment["SteamAppId"]);
        Assert.False(plan.SaveRedirectInstalled);
    }

    [Fact]
    public void Plan_refuses_an_exe_outside_the_root()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "Steam", "eldenring.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "");
        var settings = new LauncherSettings { GameExePaths = { [GameId.EldenRing] = outside } };

        Assert.Throws<IsolationViolationException>(() => LaunchPlanner.Plan(settings, _root, GameId.EldenRing));
    }

    [Fact]
    public void Settings_round_trip_and_default_root_is_parent_of_Launcher_folder()
    {
        var path = Path.Combine(_root, LauncherSettings.FileName);
        var settings = new LauncherSettings { ActiveProfile = "Chaos" };
        settings.Profiles.Add(new LaunchProfile { Name = "Chaos" });
        settings.Save(path);

        var loaded = LauncherSettings.Load(path);

        Assert.Equal("Chaos", loaded.GetActiveProfile().Name);
        Assert.Equal(_root, LauncherSettings.ResolveDefaultRoot(Path.Combine(_root, "Launcher", "bin", "Release")));
    }
}
