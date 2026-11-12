using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's map effects: the candle flames in Firelink Shrine, the fog banks, light shafts, crows, flies and fires that
/// DS3 places as SFX regions (MSB3 "points" of type SFX: an effect ID, a spot and a shape). Up to 1.0.366 they were
/// left out with the other emitters (<see cref="RegionPort"/>), so Firelink had its torches (objects) but no
/// candle light, and the maps had none of DS3's fog.
///
/// Two processes take part, because the effects are converted with the weapons' effects (port-weapons owns Elden
/// Ring's common effect binder, <see cref="FxrPort"/>) while port-maps writes the map files:
/// <list type="number">
/// <item>port-weapons asks for every effect the ported maps' SFX regions name (<see cref="Wanted"/>), converts them
/// with the rest under 900,000,000 + the DS3 ID, and writes which ones made it to Work\map-sfx.tsv
/// (<see cref="WriteTable"/>).</item>
/// <item>port-maps, after waiting for port-weapons, adds an Elden Ring SFX region for each DS3 one whose effect made
/// it (<see cref="Apply"/>), with DS3's spot, shape, start state and entity ID moved to the slot. An effect that
/// didn't convert leaves its regions out, counted in the report.</item>
/// </list>
/// The common binder is resident, so a map SFX region naming one of its effects should play it; this is unproven
/// in game (2026-11-12). DARKSIGN_MAP_SFX=0 (launcher fallback "map-effects" B) leaves every map effect out again.
/// </summary>
public static class MapSfx
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_MAP_SFX") != "0";

    public const string TableFile = "map-sfx.tsv";

    /// <summary>Name prefix of the regions this step adds, so a rerun can find and replace them.</summary>
    public const string NamePrefix = "DS3 SFX ";

    /// <summary>The effect IDs the DS3 maps of every slot in use name in their SFX regions.</summary>
    public static SortedSet<int> Wanted(GameArchives ds3, IEnumerable<MapSlots.Slot> slots, List<string>? notes = null)
    {
        var ids = new SortedSet<int>();
        foreach (var ds3Id in slots.Select(s => s.Ds3).Distinct())
        {
            var path = MapPorter.MsbPath(ds3Id);
            if (!ds3.Contains(path))
            {
                notes?.Add($"map effects: {ds3Id} has no map file");
                continue;
            }
            var msb = MSB3.Read(ds3.Read(path));
            var mine = msb.Regions.SFX.Select(r => r.EffectID).Where(id => id > 0).ToList();
            ids.UnionWith(mine);
            notes?.Add($"map effects: {ds3Id} has {mine.Count} SFX regions naming {mine.Distinct().Count()} effects");
        }
        return ids;
    }

    /// <summary>Writes DS3 effect ID -> converted ID for the map effects that converted; the rest are listed with why not.</summary>
    public static string WriteTable(PorterContext ctx, IEnumerable<int> wanted, FxrPort.Result effects)
    {
        var lines = new List<string> { "ds3_effect\ter_effect\tnote" };
        int ok = 0, missing = 0;
        foreach (var id in wanted.Distinct().Order())
        {
            if (effects.Map(id) is { } er)
            {
                lines.Add($"{id}\t{er}\t");
                ok++;
            }
            else
            {
                lines.Add($"{id}\t\t{effects.Reasons.GetValueOrDefault(id, "not converted")}");
                missing++;
            }
        }
        File.WriteAllLines(ctx.WorkPath(TableFile), lines);
        return $"map effects: {ok} of {ok + missing} DS3 map effects converted" + (missing == 0 ? "" : $", {missing} not (Work\\{TableFile})");
    }

    /// <summary>Removes the table, so a weapons run that stops before converting effects leaves no stale one behind.</summary>
    public static void ClearTable(PorterContext ctx)
    {
        var path = ctx.WorkPath(TableFile);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>DS3 effect ID -> converted ID, from the table port-weapons wrote (empty when there is none).</summary>
    public static Dictionary<int, int> ReadTable(PorterContext ctx)
    {
        var map = new Dictionary<int, int>();
        var path = ctx.WorkPath(TableFile);
        if (!File.Exists(path))
            return map;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var cols = line.Split('\t');
            if (cols.Length >= 2 && int.TryParse(cols[0], out var ds3) && int.TryParse(cols[1], out var er))
                map[ds3] = er;
        }
        return map;
    }

    public sealed record Result(int Added, int Missing, IReadOnlyDictionary<int, int> MissingByEffect);

    /// <summary>
    /// Adds an SFX region to <paramref name="msb"/> for each of the DS3 map's SFX regions whose effect is in
    /// <paramref name="converted"/>, replacing the ones an earlier run added.
    /// </summary>
    public static Result Convert(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, IReadOnlyDictionary<int, int> converted)
    {
        msb.Regions.SFX.RemoveAll(r => r.Name.StartsWith(NamePrefix, StringComparison.Ordinal));
        var taken = msb.Regions.GetEntries().Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        int nextRegionId = msb.Regions.GetEntries().Select(r => r.RegionID).DefaultIfEmpty(-1).Max() + 1;
        var missing = new Dictionary<int, int>();
        int added = 0;
        foreach (var src in ds3.Regions.SFX)
        {
            if (!converted.TryGetValue(src.EffectID, out var effect))
            {
                missing[src.EffectID] = missing.GetValueOrDefault(src.EffectID) + 1;
                continue;
            }
            var name = NamePrefix + src.Name;
            for (int n = 2; !taken.Add(name); n++)
                name = $"{NamePrefix}{src.Name} ({n})";
            msb.Regions.SFX.Add(new MSBE.Region.SFX
            {
                Name = name,
                Shape = src.Shape.DeepCopy(),
                Position = src.Position,
                Rotation = src.Rotation,
                RegionID = nextRegionId++,
                // The settings RegionPort gives every region it adds (JortPob's MakePart.Region).
                MapStudioLayer = 0xFFFFFFFF,
                MapID = -1,
                UnkE08 = 255,
                UnkS04 = 0,
                UnkS0C = -1,
                EntityID = src.EntityID > 0 ? (uint)ds3Id.MoveEntity(src.EntityID, erId) : 0,
                EffectID = effect,
                StartDisabled = src.StartDisabled ? 1 : 0,
            });
            added++;
        }
        return new Result(added, missing.Values.Sum(), missing);
    }

    /// <summary>The port-maps step: rewrites each ported slot's map file with its DS3 map effects.</summary>
    public static string Apply(PorterContext ctx, GameArchives ds3, IEnumerable<MapSlots.Slot> slots)
    {
        if (!Enabled)
            return "map effects: off (DARKSIGN_MAP_SFX=0)";
        var converted = ReadTable(ctx);
        if (converted.Count == 0)
            return $"map effects: none added (port-weapons wrote no converted map effects to Work\\{TableFile})";
        var lines = new List<string>();
        foreach (var slot in slots)
        {
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            if (!File.Exists(path))
                continue;
            var ds3Msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(slot.Ds3)));
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            var result = Convert(ds3Msb, msb, slot.Ds3, slot.Er, converted);
            File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
            lines.Add($"map effects: {slot.Ds3} -> {slot.Er}: {result.Added} SFX regions added" +
                      (result.Missing == 0 ? "" : $", {result.Missing} left out (effects not converted: " +
                          string.Join(", ", result.MissingByEffect.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key} x{k.Value}")) + ")"));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
