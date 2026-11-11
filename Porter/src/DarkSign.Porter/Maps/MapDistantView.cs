using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Elden Ring's sky and Erdtree over a DS3 map (2026-11-11 playtest of 1.0.299: "still seeing the erd tree on all
/// of the skyboxes, for each map").
///
/// Every Elden Ring legacy map has a second layout beside its own, index 99 (m45_02_00_99 for m45_02_00_00): its
/// "distant view". It holds what is seen around the map from anywhere inside it: the sky (assets AEG096_xxx), the
/// Erdtree (AEG094_000 to _010, with the burning one, AEG094_1xx, switched in by LegacyDistantViewPartsReplaceParam
/// rows 450080 and up, whose TargetMapId is 45000099, 45010099, 45020099) and far scenery. The game loads it with
/// the map; nothing in the map's own layout names it, which is why a DS3 layout built from scratch still stood
/// under Elden Ring's sky with the Erdtree behind its houses. The colosseums have no WorldMapLegacyConvParam row,
/// so the open world's own distant view (m60_00_00_99) is not what was seen.
///
/// The port writes an empty layout in its place, so the only sky left is the one DS3 built into the map: the dome
/// of map piece m009000 (drawn since 1.0.285). "--host-sky" (launcher Fallbacks "map-sky" B) leaves the host's
/// distant view alone, and removes an empty one an earlier build wrote.
///
/// JortPob (infernoplus and contributors, https://github.com/infernoplus/JortPob, OverworldManager.cs) is where
/// the index-99 layout was identified as the one holding the sky: it edits m60_00_00_99 the same way for its
/// Morrowind port, deleting every part but the AEG096 sky assets. See CREDITS.md.
/// </summary>
public static class MapDistantView
{
    /// <summary>The index of a map's distant view layout.</summary>
    public const int Index = 99;

    /// <summary>m45_02_00_00 -> m45_02_00_99.</summary>
    public static MapId Of(MapId map) => map with { Index = Index };

    /// <summary>True for the Erdtree's assets (AEG094_xxx) and the sky's (AEG096_xxx).</summary>
    public static bool IsErdtree(string model) => model.StartsWith("AEG094_", StringComparison.OrdinalIgnoreCase);
    public static bool IsSky(string model) => model.StartsWith("AEG096_", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a distant view layout draws, for the report line.</summary>
    public static string Describe(MSBE view)
    {
        var assets = view.Parts.Assets;
        int sky = assets.Count(a => IsSky(a.ModelName)), tree = assets.Count(a => IsErdtree(a.ModelName));
        return $"{sky} sky assets, {tree} Erdtree assets, {assets.Count - sky - tree} other assets, {view.Parts.MapPieces.Count} map pieces";
    }

    /// <summary>The layout written in the host's place: no parts, no regions, no events.</summary>
    public static MSBE Empty() => new();

    /// <summary>True when the layout draws nothing (the validator's check).</summary>
    public static bool DrawsNothing(MSBE view) =>
        view.Parts.GetEntries().Count == 0 && view.Regions.GetEntries().Count == 0 && view.Events.GetEntries().Count == 0;

    /// <summary>
    /// Replaces the slot's distant view with an empty layout (or, with keepHost, leaves the host's and deletes an
    /// empty one written before). Returns the report line.
    /// </summary>
    public static string Write(PorterContext ctx, GameArchives er, MapId erId, bool keepHost)
    {
        var path = MapPorter.MsbPath(Of(erId));
        var modPath = ctx.ModPath(MapPorter.ModParts(path));
        if (keepHost)
        {
            bool removed = File.Exists(modPath);
            if (removed)
                File.Delete(modPath);
            return $"sky: the host's own (--host-sky): Elden Ring's sky and Erdtree stay over this map{(removed ? "; the empty distant view of an earlier build was removed" : "")}";
        }
        if (!er.Contains(path))
            return $"sky: Elden Ring has no distant view for {erId} ({Of(erId)}), so there is no Elden Ring sky or Erdtree to remove";

        var (bytes, compression) = er.ReadWithCompression(path);
        var was = Describe(MSBE.Read(bytes));
        Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
        File.WriteAllBytes(modPath, DCX.Compress(Empty().Write(), compression));
        return $"sky: Elden Ring's distant view {Of(erId)} emptied (it drew {was}); the sky is DS3's own dome";
    }
}
