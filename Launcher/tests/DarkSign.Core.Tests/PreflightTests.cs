using System.IO.Compression;
using DarkSign.Core;
using Xunit;

namespace DarkSign.Core.Tests;

public sealed class PreflightTests
{
    [Fact]
    public void Launcher_me3_profile_uses_only_keys_me3_reads()
    {
        // Every combination of launch tweaks the Fallbacks window can ask for (audit 2026-11-05: mem_patch, disable_arxan).
        foreach (var arxan in new[] { false, true })
            foreach (var mem in new[] { false, true })
                foreach (var online in new[] { false, true })
                {
                    var text = EldenRingSetup.Me3Profile(@"C:\mod", [@"C:\a.dll", @"C:\b.dll"],
                        new Fallbacks.LaunchTweaks(DisableArxan: arxan, NoMemPatch: mem, StartOnline: online));
                    Assert.Empty(Preflight.CheckMe3Profile(text));
                }
    }

    [Fact]
    public void Unknown_me3_keys_and_missing_version_are_reported()
    {
        var problems = Preflight.CheckMe3Profile("disable_arxan_typo = true\n[[packages]]\nid = \"x\"\nsrc = \"y\"\n");
        Assert.Contains(problems, p => p.Contains("disable_arxan_typo"));
        Assert.Contains(problems, p => p.Contains("[packages] src"));
        Assert.Contains(problems, p => p.Contains("profileVersion"));
    }

    [Theory]
    [InlineData(1030290005, "saved")]
    [InlineData(1039290100, "saved")]
    [InlineData(1039292100, "temporary")]
    [InlineData(1039291100, "event")]
    [InlineData(1030294005, "saved")]
    [InlineData(1038299999, "saved")]
    [InlineData(45000800, "other")]
    [InlineData(1039300100, "other")]
    public void JortPob_layout(long flag, string kind) => Assert.Equal(kind, Preflight.JortPobLayout(flag));

    [Fact]
    public void Run_flags_and_the_global_flag_table_sit_in_saved_or_temporary_ranges()
    {
        // RunEvents.cs: run active (saved), start/leave/warp requests (temporary).
        Assert.Equal("saved", Preflight.JortPobLayout(1039290100));
        Assert.Equal("temporary", Preflight.JortPobLayout(1039292101));
        Assert.Equal("temporary", Preflight.JortPobLayout(1039292200 + 99));
    }

    [Fact]
    public void Enemy_port_report_is_counted_and_failures_warned()
    {
        var path = Path.Combine(Path.GetTempPath(), "dks-enemy-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            File.WriteAllLines(path, [
                "DS3 chr\tname\tboss\ttemplate\ttemplate name\tbone match\tnew chr\tmethod\tnotes",
                "c1100\tLothric Knight\t0\tc4300\tBanished Knight\t0.80\tc7000\tfull\t",
                "c1200\tHollow\t0\tc4310\tSoldier\t0.70\tc7001\tmodel\t",
                "c1300\tRat\t0\tc4410\tWolf\t0.10\t\tstats\tbone match 0.10 below 0.25",
                "# c1400 Crystal Lizard: files failed (InvalidDataException: bad flver); using the template's body",
            ]);
            var items = new List<Preflight.Item>();
            Preflight.CheckEnemyReport(items, path);
            Assert.Contains(items, i => i.Level == Preflight.Level.Ok && i.Text.Contains("1 with DS3 model and animations, 1 DS3 model only, 1 on Elden Ring bodies"));
            Assert.Contains(items, i => i.Level == Preflight.Level.Warn && i.Text.Contains("c1400 Crystal Lizard"));
            items.Clear();
            Preflight.CheckEnemyReport(items, path + ".missing");
            Assert.Empty(items);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_blanks_the_token()
    {
        Assert.Contains("\"GitHubToken\": \"(removed)\"", Diagnostics.Redact("{ \"GitHubToken\": \"ghp_secret\" }"));
        Assert.DoesNotContain("ghp_secret", Diagnostics.Redact("{\"GitHubToken\":\"ghp_secret\"}"));
        Assert.Contains("\"GitHubToken\": \"\"", Diagnostics.Redact("{ \"GitHubToken\": \"\" }"));
    }

    [Fact]
    public void Report_zip_has_logs_but_no_saves_and_no_token()
    {
        var root = Path.Combine(Path.GetTempPath(), "dks-diag-" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "Launcher");
        try
        {
            var settings = new LauncherSettings { RootPath = root, GitHubToken = "ghp_secret" };
            Directory.CreateDirectory(launcher);
            settings.Save(Path.Combine(launcher, LauncherSettings.FileName));
            var saves = SaveIsolation.EnsureSaveDirectory(root, "Default", GameId.EldenRing);
            File.WriteAllText(Path.Combine(saves, "ER0000.dksign"), "save");
            File.WriteAllText(Path.Combine(saves, "dksign_testmenu.log"), "flagcheck: 2 blocks ok, 0 missing");
            var reports = Path.Combine(root, "TheDarkSign", "Work", "reports");
            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, "moveset-timing.tsv"), "a\tb");
            var mod = new EldenRingSetup(root).ModDir;
            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "regulation.bin"), "binary");
            File.WriteAllText(Path.Combine(mod, "darksign_maps_check.txt"), "map-validate: 0 FAIL");

            var zipPath = Diagnostics.Write(root, settings, launcher, "1.0.0", "OK x");
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("summary.txt", names);
            Assert.Contains("preflight.txt", names);
            Assert.Contains(names, n => n.EndsWith("moveset-timing.tsv"));
            Assert.Contains(names, n => n.EndsWith("dksign_testmenu.log"));
            Assert.Contains(names, n => n.EndsWith("darksign_maps_check.txt"));
            Assert.DoesNotContain(names, n => n.EndsWith(".dksign") || n.EndsWith("regulation.bin"));
            using var reader = new StreamReader(zip.GetEntry("launcher_settings.json")!.Open());
            Assert.DoesNotContain("ghp_secret", reader.ReadToEnd());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_reports_a_missing_game_and_reads_in_game_flag_checks()
    {
        var root = Path.Combine(Path.GetTempPath(), "dks-pre-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var settings = new LauncherSettings { RootPath = root };
            var saves = SaveIsolation.EnsureSaveDirectory(root, "Default", GameId.EldenRing);
            File.WriteAllLines(Path.Combine(saves, "dksign_testmenu.log"),
            [
                "flagcheck: block 1039290 (DS3 run active flag, saved): ok, the game keeps flags here",
                "flagcheck: block 45000 (m45_00_00_00 map flags): MISSING, the game has no such block",
                "flagcheck: 1 blocks ok, 1 missing",
            ]);
            var items = Preflight.Run(new Preflight.Context(root, settings, null));
            Assert.Contains(items, i => i.Area == "Elden Ring" && i.Level == Preflight.Level.Fail);
            Assert.Contains(items, i => i.Area == "Event flags" && i.Level == Preflight.Level.Fail && i.Text.Contains("map-flags"));
            Assert.Contains(items, i => i.Area == "me3 profile" && i.Level == Preflight.Level.Ok);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
