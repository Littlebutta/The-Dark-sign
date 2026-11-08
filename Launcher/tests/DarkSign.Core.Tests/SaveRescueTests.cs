using DarkSign.Core;

namespace DarkSign.Core.Tests;

public sealed class SaveRescueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darksign-rescue-" + Guid.NewGuid().ToString("N"));
    private string Mod => Path.Combine(_root, "TheDarkSign", "Mods", "ER");
    private string Hidden => SaveRescue.HiddenDir(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Put(string rel, string text = "x")
    {
        var path = Path.Combine(Mod, rel.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Hide_moves_only_the_ported_maps_and_restore_brings_them_back()
    {
        Put(@"map\mapstudio\m45_00_00_00.msb.dcx");
        Put(@"map\mapstudio\m45_01_00_00.msb.dcx");
        Put(@"map\m45\m45_00_00_00\m45_00_00_00_000052.mapbnd.dcx");
        Put(@"map\m45\common\m45_0000.tpfbhd");
        Put(@"event\m45_00_00_00.emevd.dcx");
        Put(@"event\common.emevd.dcx", "keep");
        Put(@"chr\c7014.chrbnd.dcx", "keep");
        Put("regulation.bin", "keep");

        var moved = SaveRescue.Hide(Mod, Hidden);
        Assert.Contains(Path.Combine("map", "mapstudio", "m45_00_00_00.msb.dcx"), moved);
        Assert.Contains(Path.Combine("map", "m45"), moved);
        Assert.Equal(3, moved.Count);
        Assert.False(File.Exists(Path.Combine(Mod, "map", "mapstudio", "m45_00_00_00.msb.dcx")));
        Assert.False(Directory.Exists(Path.Combine(Mod, "map", "m45")));
        // The slot's empty event script stays, so the vanilla arena's events don't run in the fallback arena.
        Assert.True(File.Exists(Path.Combine(Mod, "event", "m45_00_00_00.emevd.dcx")));
        Assert.True(File.Exists(Path.Combine(Mod, "event", "common.emevd.dcx")));
        Assert.True(File.Exists(Path.Combine(Mod, "chr", "c7014.chrbnd.dcx")));
        Assert.True(File.Exists(Path.Combine(Mod, "regulation.bin")));
        Assert.True(SaveRescue.IsHidden(Hidden));
        Assert.True(File.Exists(Path.Combine(Hidden, "map", "m45", "common", "m45_0000.tpfbhd")));

        var restored = SaveRescue.Restore(Mod, Hidden);
        Assert.Equal(4, restored.Count);
        Assert.True(File.Exists(Path.Combine(Mod, "map", "mapstudio", "m45_00_00_00.msb.dcx")));
        Assert.True(File.Exists(Path.Combine(Mod, "map", "m45", "m45_00_00_00", "m45_00_00_00_000052.mapbnd.dcx")));
        Assert.False(SaveRescue.IsHidden(Hidden));
        Assert.False(Directory.Exists(Hidden));
    }

    [Fact]
    public void Restore_keeps_files_a_rebuild_wrote_meanwhile_and_is_harmless_when_nothing_is_hidden()
    {
        Assert.Empty(SaveRescue.Restore(Mod, Hidden));

        Put(@"map\m45\m45_00_00_00\old.mapbnd.dcx", "old");
        Put(@"map\mapstudio\m45_00_00_00.msb.dcx", "old");
        SaveRescue.Hide(Mod, Hidden);
        // A rebuild while hidden writes a new layout and model.
        Put(@"map\mapstudio\m45_00_00_00.msb.dcx", "new");
        Put(@"map\m45\m45_00_00_00\new.mapbnd.dcx", "new");

        SaveRescue.Restore(Mod, Hidden);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Mod, "map", "mapstudio", "m45_00_00_00.msb.dcx")));
        Assert.True(File.Exists(Path.Combine(Mod, "map", "m45", "m45_00_00_00", "new.mapbnd.dcx")));
        Assert.True(File.Exists(Path.Combine(Mod, "map", "m45", "m45_00_00_00", "old.mapbnd.dcx")));
        Assert.False(Directory.Exists(Hidden));
    }

    [Fact]
    public void Restore_brings_back_whatever_an_older_launcher_hid()
    {
        // 1.0.220 hid the slot event scripts too; a later launcher must still put them back.
        var stale = Path.Combine(Hidden, "event", "m45_00_00_00.emevd.dcx");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "events");
        Assert.True(SaveRescue.IsHidden(Hidden));
        var restored = SaveRescue.Restore(Mod, Hidden);
        Assert.Single(restored);
        Assert.Equal("events", File.ReadAllText(Path.Combine(Mod, "event", "m45_00_00_00.emevd.dcx")));
        Assert.False(Directory.Exists(Hidden));
    }

    [Fact]
    public void Hiding_twice_loses_nothing()
    {
        Put(@"map\m45\a.dcx", "a");
        SaveRescue.Hide(Mod, Hidden);
        Put(@"map\m45\b.dcx", "b");
        SaveRescue.Hide(Mod, Hidden);
        Assert.False(Directory.Exists(Path.Combine(Mod, "map", "m45")));
        SaveRescue.Restore(Mod, Hidden);
        Assert.True(File.Exists(Path.Combine(Mod, "map", "m45", "a.dcx")));
        Assert.True(File.Exists(Path.Combine(Mod, "map", "m45", "b.dcx")));
    }
}
