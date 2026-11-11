using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Offline checks for whose sky is over a ported map (1.0.299 playtest: Elden Ring's sky and the Erdtree over every
/// DS3 map). See <see cref="MapDistantView"/>.
/// </summary>
public static partial class MapPortChecks
{
    private static void DistantView()
    {
        var slot = MapId.Parse("m45_02_00_00");
        Expect(MapDistantView.Of(slot).ToString() == "m45_02_00_99", "the distant view of m45_02_00_00 is m45_02_00_99");
        Expect(MapPorter.MsbPath(MapDistantView.Of(slot)) == "/map/mapstudio/m45_02_00_99.msb.dcx", "its archive path");
        Expect(MapDistantView.IsErdtree("AEG094_003") && MapDistantView.IsSky("aeg096_611") && !MapDistantView.IsSky("AEG400_059"),
            "Erdtree (AEG094) and sky (AEG096) assets are told apart from scenery");
        Expect(MapPorter.Options.Parse(["--host-sky"]).HostSky && !new MapPorter.Options().HostSky, "--host-sky is read");

        // A stand-in for the host's distant view: a sky asset, an Erdtree asset, scenery and a region.
        var host = new MSBE();
        foreach (var model in new[] { "AEG096_611", "AEG094_000", "AEG094_001", "AEG400_059" })
        {
            host.Models.Assets.Add(new MSBE.Model.Asset { Name = model });
            host.Parts.Assets.Add(new MSBE.Part.Asset { Name = model + "_9000", ModelName = model });
        }
        Expect(MapDistantView.Describe(host) == "1 sky assets, 2 Erdtree assets, 1 other assets, 0 map pieces", "describe: " + MapDistantView.Describe(host));
        Expect(!MapDistantView.DrawsNothing(host), "the stand-in host view draws something");

        // What is written reads back, in the game's format, with nothing in it.
        var written = MSBE.Read(MapDistantView.Empty().Write());
        Expect(MapDistantView.DrawsNothing(written) && written.Models.GetEntries().Count == 0, "the written distant view is empty");

        var dir = Path.Combine(Path.GetTempPath(), "dks-sky-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "m45_02_00_99.msb.dcx");
            var deflate = new DCX.DcxDfltCompressionInfo(DCX.DfltCompressionPreset.DCX_DFLT_11000_44_9);
            // No file in the mod: the host's sky shows (a warning where the host has one).
            Expect(MapValidate.CheckDistantView(true, slot, file).Level == MapValidate.Level.Warn, "validate: the host's view in place is a warning");
            Expect(MapValidate.CheckDistantView(false, slot, file).Level == MapValidate.Level.Ok, "validate: a slot without a distant view is fine");
            File.WriteAllBytes(file, DCX.Compress(MapDistantView.Empty().Write(), deflate));
            var ok = MapValidate.CheckDistantView(true, slot, file);
            Expect(ok.Level == MapValidate.Level.Ok, "validate: an empty view passes: " + ok.Text);
            File.WriteAllBytes(file, DCX.Compress(host.Write(), deflate));
            var bad = MapValidate.CheckDistantView(true, slot, file);
            Expect(bad.Level == MapValidate.Level.Fail && bad.Text.Contains("2 Erdtree"), "validate: a view that still draws the Erdtree fails: " + bad.Text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
