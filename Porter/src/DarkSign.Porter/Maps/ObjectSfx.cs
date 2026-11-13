using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The effects DS3 objects carry on themselves: Firelink Shrine's candle flames, braziers, glowing lanterns.
/// <para>
/// DS3 doesn't light a candle with a map effect. The candle object (o391000, 409 placements in Firelink and the
/// Cemetery, 349 in the Undead Settlement) names a ModelSfxParam row on its map part (MSB3 object
/// ModelSfxParamRelativeIDs[0]: 0 = lit, -1 = unlit; 210 of m40's 409 candles are lit), and the row
/// (model number * 1000 + relative ID, e.g. 391000000 = effect 839010 on dummy poly 200) puts the flame on the
/// model's dummy poly. Elden Ring does the same with AssetModelSfxParam (asset row * 1000 + the part's
/// AssetSfxParamRelativeID; vanilla 228039000 is a Roundtable Hold candle, effect 800401 on dummy 200), which is the
/// recipe JortPob uses for Morrowind's lights and fires (infernoplus/JortPob Paramanager.GenerateAssetRows,
/// MakePart.Asset). Up to 1.0.42x the port left every relative ID at -1, so no ported candle was lit.
/// </para>
/// <list type="bullet">
/// <item>port-weapons asks for the effects of every row a placed DS3 object names (<see cref="Wanted"/>) and converts
/// them with the map effects (<see cref="MapSfx"/>: Work\map-sfx.tsv, 900M + the DS3 ID, resident).</item>
/// <item>port-maps, in the "map effects" step (after the object rows exist), writes an AssetModelSfxParam row for each
/// asset whose DS3 row has a converted effect and gives its parts DS3's relative ID (<see cref="Apply"/>). A part
/// whose effects didn't convert keeps -1, so no part names a row that isn't there.</item>
/// </list>
/// Dummy poly IDs are DS3's: the asset model keeps DS3's dummies (<see cref="ObjectPort.ConvertBinder(BND4, string, string, MapModelPort, BND4, Binder.FileFlags)"/>).
/// DARKSIGN_OBJECT_SFX=0 (launcher fallback "map-effects" C) leaves them out again.
/// </summary>
public static class ObjectSfx
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_OBJECT_SFX") != "0";

    public const string Ds3ParamName = "ModelSfxParam", ErParamName = "AssetModelSfxParam";

    /// <summary>DS3 rows have ten effect slots (VfxId1..10 / DummyPolyId1..10), Elden Ring's eight (sfxId_0..7 / dmypolyId_0..7).</summary>
    public const int Ds3Slots = 10, ErSlots = 8;

    /// <summary>"o391000" -> 391000; -1 when the name isn't an object model number.</summary>
    public static int ModelNumber(string? model) =>
        model is { Length: > 1 } && (model[0] is 'o' or 'O') && int.TryParse(model.AsSpan(1), out var n) ? n : -1;

    /// <summary>The DS3 ModelSfxParam row an object part names, or -1 when it names none.</summary>
    public static long Ds3Row(MSB3.Part.Object part)
    {
        var rel = part.ModelSfxParamRelativeIDs is { Length: > 0 } ids ? ids[0] : (short)-1;
        var model = ModelNumber(part.ModelName);
        return rel < 0 || model < 0 ? -1 : model * 1000L + rel;
    }

    /// <summary>The (effect, dummy poly) pairs of a DS3 ModelSfxParam row, in slot order; empty slots left out.</summary>
    public static List<(int Effect, int Dummy)> Effects(PARAM.Row row)
    {
        var list = new List<(int, int)>();
        for (int i = 1; i <= Ds3Slots; i++)
        {
            var effect = Int(row, $"VfxId{i}");
            if (effect > 0)
                list.Add((effect, Int(row, $"DummyPolyId{i}")));
        }
        return list;
    }

    private static int Int(PARAM.Row row, string field)
    {
        var cell = row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(field, StringComparison.OrdinalIgnoreCase));
        return cell?.Value is null or byte[] ? -1 : System.Convert.ToInt32(cell.Value);
    }

    /// <summary>The effect IDs the rows of a DS3 map's objects name (parts of other map states included: cheap, and harmless).</summary>
    public static SortedSet<int> Wanted(MSB3 msb, PARAM ds3Param)
    {
        var ids = new SortedSet<int>();
        foreach (var rowId in msb.Parts.Objects.Select(Ds3Row).Where(r => r >= 0 && r <= int.MaxValue).Distinct())
            if (ds3Param[(int)rowId] is { } row)
                ids.UnionWith(Effects(row).Select(e => e.Effect));
        return ids;
    }

    /// <summary>The effect IDs every slot's DS3 objects name (for port-weapons).</summary>
    public static SortedSet<int> Wanted(GameArchives ds3, PARAM ds3Param, IEnumerable<MapSlots.Slot> slots, List<string>? notes = null)
    {
        var ids = new SortedSet<int>();
        foreach (var ds3Id in slots.Select(s => s.Ds3).Distinct())
        {
            var path = MapPorter.MsbPath(ds3Id);
            if (!ds3.Contains(path))
                continue;
            var mine = Wanted(MSB3.Read(ds3.Read(path)), ds3Param);
            ids.UnionWith(mine);
            notes?.Add($"object effects: {ds3Id}'s objects carry {mine.Count} effects (candle flames, braziers...)");
        }
        return ids;
    }

    public sealed record Result(int Rows, int Parts, int NoRow, int NotConverted, IReadOnlyDictionary<int, int> MissingByEffect)
    {
        public string Summary(MapId ds3Id, MapId erId) =>
            $"object effects: {ds3Id} -> {erId}: {Parts} object parts carry DS3's own effects ({Rows} asset effect rows)"
            + (NoRow == 0 ? "" : $"; {NoRow} name a DS3 row that isn't there")
            + (NotConverted == 0 ? "" : $"; {NotConverted} left without (effects not converted: "
                + string.Join(", ", MissingByEffect.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key} x{k.Value}")) + ")");
    }

    /// <summary>
    /// Gives the asset parts placed for <paramref name="ds3"/>'s objects (<paramref name="partNames"/>: DS3 part name ->
    /// asset part name) DS3's relative IDs and adds the Elden Ring rows they name to <paramref name="erParam"/>.
    /// <paramref name="converted"/> maps DS3 effect IDs to converted ones; a row with none of its effects converted
    /// isn't written, and its parts keep -1.
    /// </summary>
    public static Result Convert(MSB3 ds3, MSBE msb, IReadOnlyDictionary<string, string> partNames, PARAM ds3Param,
        IReadOnlyDictionary<int, int> converted, PARAM erParam)
    {
        var assets = msb.Parts.Assets.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var template = erParam.Rows.FirstOrDefault() ?? throw new InvalidDataException($"{ErParamName} has no rows to copy");
        var written = new Dictionary<int, bool>();
        var missing = new Dictionary<int, int>();
        int parts = 0, noRow = 0, notConverted = 0;
        foreach (var src in ds3.Parts.Objects)
        {
            if (!partNames.TryGetValue(src.Name, out var erName) || !assets.TryGetValue(erName, out var part))
                continue;
            part.AssetSfxParamRelativeID = -1;
            var ds3RowId = Ds3Row(src);
            if (ds3RowId < 0)
                continue;
            var ds3Row = ds3RowId <= int.MaxValue ? ds3Param[(int)ds3RowId] : null;
            if (ds3Row is null)
            {
                noRow++;
                continue;
            }
            var rel = src.ModelSfxParamRelativeIDs[0];
            var assetRow = AssetRow(part.ModelName);
            if (assetRow < 0)
                continue;
            var erRowId = assetRow * 1000 + rel;
            if (!written.TryGetValue(erRowId, out var ok))
            {
                var effects = Effects(ds3Row);
                var kept = effects.Where(e => converted.ContainsKey(e.Effect)).Take(ErSlots).ToList();
                foreach (var e in effects.Where(e => !converted.ContainsKey(e.Effect)))
                    missing[e.Effect] = missing.GetValueOrDefault(e.Effect) + 1;
                ok = kept.Count > 0;
                if (ok)
                {
                    erParam.Rows.RemoveAll(r => r.ID == erRowId);
                    var row = new PARAM.Row(template) { ID = erRowId, Name = $"[DS3] {src.ModelName} effects ({ds3RowId})" };
                    for (int i = 0; i < ErSlots; i++)
                    {
                        Set(row, $"sfxId_{i}", i < kept.Count ? converted[kept[i].Effect] : -1);
                        Set(row, $"dmypolyId_{i}", i < kept.Count ? kept[i].Dummy : -1);
                    }
                    erParam.Rows.Add(row);
                }
                written[erRowId] = ok;
            }
            if (!ok)
            {
                notConverted++;
                continue;
            }
            part.AssetSfxParamRelativeID = rel;
            parts++;
        }
        erParam.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return new Result(written.Count(w => w.Value), parts, noRow, notConverted, missing);
    }

    /// <summary>"AEG920_207" -> 920207 (the asset's AssetEnvironmentGeometryParam row); -1 for any other name.</summary>
    public static int AssetRow(string? model) =>
        model is { Length: 10 } && model.StartsWith("AEG", StringComparison.OrdinalIgnoreCase) && model[6] == '_'
        && int.TryParse(model.AsSpan(3, 3), out var g) && int.TryParse(model.AsSpan(7, 3), out var i) ? g * 1000 + i : -1;

    private static void Set(PARAM.Row row, string field, int value)
    {
        var cell = row.Cells.FirstOrDefault(c => c.Def.InternalName == field);
        if (cell is not null)
            cell.Value = System.Convert.ChangeType(value, cell.Value.GetType());
    }

    /// <summary>
    /// The port-maps step: the asset effect rows go into the mod's regulation, each slot's map file gets DS3's relative
    /// IDs. If the regulation can't be written, the map files are left as they are (every part at -1).
    /// </summary>
    public static string Apply(PorterContext ctx, GameArchives ds3, IEnumerable<MapSlots.Slot> slots,
        IReadOnlyDictionary<int, int> converted, Func<MapId, IReadOnlyDictionary<string, string>?> partNames)
    {
        if (!Enabled)
            return "object effects: off (DARKSIGN_OBJECT_SFX=0)";
        if (converted.Count == 0)
            return "object effects: none (port-weapons converted no map effects)";
        var ds3Param = ctx.LoadParams(PorterGame.DarkSouls3).Get(Ds3ParamName)
                       ?? throw new InvalidDataException($"DS3 has no {Ds3ParamName}");
        var regulation = ActionButtons.LoadRegulation(ctx);
        var erParam = regulation.Get(ErParamName) ?? throw new InvalidDataException($"Elden Ring has no {ErParamName}");
        var lines = new List<string>();
        var maps = new List<(string Path, MSBE Msb, DCX.CompressionInfo Compression)>();
        foreach (var slot in slots)
        {
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            var names = partNames(slot.Er);
            if (!File.Exists(path) || names is null || names.Count == 0)
                continue;
            var ds3Msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(slot.Ds3)));
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            var result = Convert(ds3Msb, msb, names, ds3Param, converted, erParam);
            lines.Add(result.Summary(slot.Ds3, slot.Er));
            if (result.Parts > 0)
                maps.Add((path, msb, compression));
        }
        if (maps.Count == 0)
            return lines.Count == 0 ? "object effects: no ported objects carry DS3 effects" : string.Join(Environment.NewLine, lines);
        // Rows first: a map file must never name a row the regulation doesn't have.
        regulation.Save(ctx.ModPath("regulation.bin"));
        foreach (var (path, msb, compression) in maps)
            File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
        return string.Join(Environment.NewLine, lines);
    }
}
