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
        Assert.False(plan.Environment.ContainsKey("BB_USER_DIR"));
    }

    [Fact]
    public void Bloodborne_saves_go_to_the_profile_through_bbport_settings()
    {
        var exe = Touch("Bloodborne", "Play Bloodborne.exe");
        var settings = new LauncherSettings { GameExePaths = { [GameId.Bloodborne] = exe } };

        var plan = LaunchPlanner.Plan(settings, _root, GameId.Bloodborne);

        Assert.Equal(Path.Combine(_root, "TheDarkSign", "Saves", "Default", "BB"), plan.Environment["BB_USER_DIR"]);
        Assert.Equal(Path.Combine(_root, "TheDarkSign", "Cache", "BB"), plan.Environment["BB_GPU_USER_DIR"]);
        Assert.Null(plan.SteamAppIdFile);
        Assert.Equal(Path.Combine(_root, "TheDarkSign", "Config", "Default", "BB"), plan.Environment["APPDATA"]);
    }

    [Fact]
    public void Bbport_settings_copy_keeps_player_setup_but_points_saves_at_profile_and_turns_online_off()
    {
        var playerAppData = Path.Combine(Path.GetDirectoryName(_root)!, "Roaming");
        var playerFile = Path.Combine(playerAppData, "bbport-launcher", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(playerFile)!);
        File.WriteAllText(playerFile, """{ "game_dir": "D:/dump", "user_dir": "C:/old", "online": true }""");
        var saveDir = SaveIsolation.SaveDirectory(_root, "Default", GameId.Bloodborne);

        var appData = BbportConfig.Prepare(_root, "Default", saveDir, playerAppData);

        var copy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(appData, "bbport-launcher", "settings.json")))!;
        Assert.Equal("D:/dump", copy["game_dir"]!.GetValue<string>());
        Assert.Equal(saveDir, copy["user_dir"]!.GetValue<string>());
        Assert.False(copy["online"]!.GetValue<bool>());
        Assert.Contains("C:/old", File.ReadAllText(playerFile));

        // Later starts keep the profile's own copy, even if the player's file changes.
        File.WriteAllText(playerFile, """{ "game_dir": "E:/other" }""");
        BbportConfig.Prepare(_root, "Default", saveDir, playerAppData);
        Assert.Contains("dump", File.ReadAllText(Path.Combine(appData, "bbport-launcher", "settings.json")));
    }

    [Fact]
    public void Redirect_dll_is_installed_as_dinput8_and_updated_but_never_replaces_another_mod()
    {
        var exe = Touch("DARK SOULS III", "Game", "DarkSoulsIII.exe");
        var proxy = SaveRedirectInstaller.ProxyPath(exe);
        byte[] v1 = [.. "MZ v1 DkSignSaveRedirectVersion"u8], v2 = [.. "MZ v2 DkSignSaveRedirectVersion"u8];

        Assert.Equal(RedirectState.Missing, SaveRedirectInstaller.Check(GameId.DarkSouls3, exe, v1));
        Assert.Equal(RedirectState.Installed, SaveRedirectInstaller.Install(GameId.DarkSouls3, exe, v1, _root));
        Assert.Equal(RedirectState.Outdated, SaveRedirectInstaller.Check(GameId.DarkSouls3, exe, v2));
        SaveRedirectInstaller.Install(GameId.DarkSouls3, exe, v2, _root);
        Assert.Equal(v2, File.ReadAllBytes(proxy));

        File.WriteAllBytes(proxy, [.. "MZ some other mod"u8]);
        Assert.Equal(RedirectState.Conflict, SaveRedirectInstaller.Install(GameId.DarkSouls3, exe, v2, _root));
        Assert.Equal("MZ some other mod"u8.ToArray(), File.ReadAllBytes(proxy));

        Assert.Equal(RedirectState.Installed, SaveRedirectInstaller.Check(GameId.Bloodborne, exe, v2));
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
    public void Plan_allows_any_install_when_the_game_folder_is_only_read()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "Steam", "steamapps", "common", "ELDEN RING", "Game", "eldenring.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "");
        var settings = new LauncherSettings { GameExePaths = { [GameId.EldenRing] = outside } };

        var plan = LaunchPlanner.Plan(settings, _root, GameId.EldenRing, gameFolderReadOnly: true);

        Assert.Equal(outside, plan.ExePath);
        Assert.Null(plan.SteamAppIdFile); // nothing is written next to the player's own install
        Assert.Equal(Path.Combine(_root, "TheDarkSign", "Saves", "Default", "ER"), plan.SaveDirectory);
    }

    [Fact]
    public void Finds_steam_installs_and_games_in_a_picked_folder()
    {
        var vdf = """
            "libraryfolders"
            {
                "0" { "path"  "C:\\Program Files (x86)\\Steam" "label" "" }
                "1" { "path"  "E:\\SteamLibrary" }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"E:\SteamLibrary"], SteamLibraries.ParseLibraryFolders(vdf));

        var common = Path.Combine(Path.GetDirectoryName(_root)!, "steamapps", "common");
        var er = Path.Combine(common, "ELDEN RING", "Game", "eldenring.exe");
        var ds3 = Path.Combine(common, "DARK SOULS III", "Game", "DarkSoulsIII.exe");
        foreach (var exe in new[] { er, ds3 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "");
        }
        var inRoot = Touch("DARK SOULS III", "Game", "DarkSoulsIII.exe");

        var found = GameDetector.ScanAll(_root, [common]).ToDictionary(g => g.Id, g => g.ExePath);

        Assert.Equal(er, found[GameId.EldenRing]);
        Assert.Equal(inRoot, found[GameId.DarkSouls3]); // the root wins over Steam
        Assert.Equal(er, GameDetector.FindIn(GameId.EldenRing, Path.Combine(common, "ELDEN RING")));
        Assert.Null(GameDetector.FindIn(GameId.EldenRing, Path.Combine(common, "DARK SOULS III")));
    }

    [Fact]
    public void Uninstall_removes_the_data_folder_and_our_dlls_but_keeps_saves_unless_asked()
    {
        Touch("TheDarkSign", "Mods", "ER", "regulation.bin");
        Touch("TheDarkSign", "Tools", "me3", "me3.exe");
        var save = Touch("TheDarkSign", "Saves", "Default", "ER", "ER0000.dksign");
        var exe = Touch("DARK SOULS III", "Game", "DarkSoulsIII.exe");
        var ours = SaveRedirectInstaller.ProxyPath(exe);
        File.WriteAllText(ours, "xx DkSignSaveRedirectVersion xx");
        var otherExe = Touch("Dark Souls Remastered", "DarkSoulsRemastered.exe");
        var theirs = SaveRedirectInstaller.ProxyPath(otherExe);
        File.WriteAllText(theirs, "someone else's mod");

        var plan = Uninstaller.MakePlan(_root, [exe, otherExe], deleteSaves: false);
        Assert.Empty(Uninstaller.Run(_root, plan));

        Assert.False(Directory.Exists(Path.Combine(_root, "TheDarkSign", "Mods")));
        Assert.False(Directory.Exists(Path.Combine(_root, "TheDarkSign", "Tools")));
        Assert.True(File.Exists(save));
        Assert.False(File.Exists(ours));
        Assert.True(File.Exists(theirs));
        Assert.True(File.Exists(exe));

        Assert.Empty(Uninstaller.Run(_root, Uninstaller.MakePlan(_root, [exe], deleteSaves: true)));
        Assert.False(Directory.Exists(Path.Combine(_root, "TheDarkSign")));
    }

    [Fact]
    public void Settings_round_trip_and_default_root_is_parent_of_Launcher_folder_else_the_launchers_own()
    {
        var path = Path.Combine(_root, LauncherSettings.FileName);
        var settings = new LauncherSettings { ActiveProfile = "Chaos" };
        settings.Profiles.Add(new LaunchProfile { Name = "Chaos" });
        settings.Save(path);

        var loaded = LauncherSettings.Load(path);

        Assert.Equal("Chaos", loaded.GetActiveProfile().Name);
        Assert.Equal(_root, LauncherSettings.ResolveDefaultRoot(Path.Combine(_root, "Launcher", "bin", "Release")));
        var unzipped = Path.Combine(_root, "Downloads", "TheDarkSign");
        Assert.Equal(unzipped, LauncherSettings.ResolveDefaultRoot(unzipped + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Elden_ring_setup_writes_modengine_config_and_tracks_the_content_build()
    {
        var setup = new EldenRingSetup(_root);
        var exe = Touch("ELDEN RING", "Game", "eldenring.exe");
        var dll = setup.WriteDll(EldenRingSetup.TestMenuDllName, [1, 2, 3]);
        Assert.StartsWith(Path.Combine(_root, "TheDarkSign", "Tools", "Dlls"), dll);

        var config = File.ReadAllText(setup.WriteConfig([dll, @"D:\Quote""d\x.dll"]));
        Assert.Contains($"\"{dll.Replace("\\", "\\\\")}\",", config);
        Assert.Contains(@"""D:\\Quote\""d\\x.dll""", config);
        Assert.Contains($"path = \"{setup.ModDir.Replace("\\", "\\\\")}\"", config);

        Assert.True(setup.NeedsContentBuild("1.0.9"));
        Touch("TheDarkSign", "Mods", "ER", "regulation.bin");
        Touch("TheDarkSign", "Mods", "ER", "darksign_items.tsv");
        setup.MarkContentBuilt("1.0.9");
        Assert.False(setup.NeedsContentBuild("1.0.9"));
        Assert.True(setup.NeedsContentBuild("1.0.10"));

        var settings = new LauncherSettings { GameExePaths = { [GameId.EldenRing] = exe } };
        var start = EldenRingSetup.LaunchInfo(LaunchPlanner.Plan(settings, _root, GameId.EldenRing),
            Path.Combine(_root, "TheDarkSign", "Tools", "ModEngine2", "modengine2_launcher.exe"), setup.ConfigPath,
            new Dictionary<string, string> { [EldenRingSetup.ItemsVariable] = setup.ItemsFile });
        Assert.Equal(["-t", "er", "-c", setup.ConfigPath, "-p", exe], start.ArgumentList);
        Assert.Equal(setup.ItemsFile, start.Environment[EldenRingSetup.ItemsVariable]);
        Assert.Equal(".dksign", start.Environment[SaveIsolation.SaveExtVariable]);
    }

    [Fact]
    public void Elden_ring_setup_writes_the_me3_profile_and_launches_through_me3()
    {
        var setup = new EldenRingSetup(_root);
        var exe = Touch("ELDEN RING", "Game", "eldenring.exe");
        var dll = setup.WriteDll(EldenRingSetup.TestMenuDllName, [1, 2, 3]);

        var profile = File.ReadAllText(setup.WriteProfile([dll]));
        Assert.StartsWith(Path.Combine(_root, "TheDarkSign", "Tools", "me3"), setup.ProfilePath);
        Assert.Contains("profileVersion = \"v1\"", profile);
        Assert.Contains("game = \"eldenring\"", profile);
        Assert.Contains($"path = \"{setup.ModDir.Replace("\\", "\\\\")}\"", profile);
        Assert.Contains("[[natives]]", profile);
        Assert.Contains($"path = \"{dll.Replace("\\", "\\\\")}\"", profile);

        var settings = new LauncherSettings { GameExePaths = { [GameId.EldenRing] = exe } };
        var me3 = Path.Combine(_root, "TheDarkSign", "Tools", "me3", "bin", "me3.exe");
        var start = EldenRingSetup.Me3LaunchInfo(LaunchPlanner.Plan(settings, _root, GameId.EldenRing), me3, setup.ProfilePath,
            new Dictionary<string, string> { [EldenRingSetup.ItemsVariable] = setup.ItemsFile });
        Assert.Equal(["launch", "-g", "eldenring", "-e", exe, "-p", setup.ProfilePath], start.ArgumentList);
        Assert.Equal(".dksign", start.Environment[SaveIsolation.SaveExtVariable]);
        Assert.Equal(setup.ItemsFile, start.Environment[EldenRingSetup.ItemsVariable]);
    }

    [Fact]
    public void Seamless_coop_is_loaded_first_from_the_players_own_dll_and_reads_its_password()
    {
        var setup = new EldenRingSetup(_root);
        var ersc = Touch("Downloads", "SeamlessCoop", "ersc.dll");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(ersc)!, SeamlessCoop.SettingsName),
            "[GAMEPLAY]\nallow_invaders = 1\n\n[PASSWORD]\ncooppassword = ashen one\n\n[SAVE]\nsave_file_extension = co2\n");
        Assert.Null(SeamlessCoop.Problem(ersc));
        Assert.NotNull(SeamlessCoop.Problem(""));
        Assert.NotNull(SeamlessCoop.Problem(Touch("Downloads", "SeamlessCoop", "other.dll")));
        Assert.Equal("ashen one", SeamlessCoop.Password(ersc));

        var ours = setup.WriteDll(SaveIsolation.RedirectDllName, [1]);
        var profile = File.ReadAllText(setup.WriteProfile(SeamlessCoop.Natives(ersc, [ours])));
        var esc = (string p) => p.Replace("\\", "\\\\");
        Assert.True(profile.IndexOf(esc(ersc), StringComparison.Ordinal) < profile.IndexOf(esc(ours), StringComparison.Ordinal));
        Assert.Equal(new[] { ours }, SeamlessCoop.Natives(null, [ours]));
    }

    [Fact]
    public void Mod_code_is_the_same_for_the_same_regulation_and_missing_before_a_build()
    {
        var setup = new EldenRingSetup(_root);
        Assert.Null(SeamlessCoop.Fingerprint(setup.ModDir));
        Directory.CreateDirectory(setup.ModDir);
        File.WriteAllBytes(Path.Combine(setup.ModDir, "regulation.bin"), [1, 2, 3]);
        var code = SeamlessCoop.Fingerprint(setup.ModDir);
        Assert.Equal(8, code!.Length);
        Assert.Equal(code, SeamlessCoop.Fingerprint(setup.ModDir));
        File.WriteAllBytes(Path.Combine(setup.ModDir, "regulation.bin"), [1, 2, 4]);
        Assert.NotEqual(code, SeamlessCoop.Fingerprint(setup.ModDir));
    }
}
