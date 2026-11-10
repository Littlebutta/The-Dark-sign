using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The PartsDrawParam row ported map pieces point at. Every vanilla map piece names one (342 of Stormveil's 343,
/// "isUsePartsDrawParamID"); it carries the draw distance and the LOD switch distances. The colosseums' only map
/// piece, a reflection proxy, names none, and pieces copied from it stayed invisible in the 2026-11-10 playtest
/// although their models, materials and draw flags were in order.
///
/// The row is made the way JortPob makes its rows (Paramanager.GeneratePartDrawParams): a copy of vanilla row 1001
/// with an unlimited draw distance and no LOD switching (DS3's far meshes are their own map pieces).
/// </summary>
public static class MapDrawParams
{
    public const string ParamName = "PartsDrawParam";
    /// <summary>Our row; JortPob's start at 9000.</summary>
    public const short FirstRow = 9400, LastRow = 9499;
    /// <summary>JortPob's template row, "generic long distance lod".</summary>
    public const int TemplateRow = 1001;
    /// <summary>Stormveil's most common map piece row (239 of 343): used when the regulation can't be written.</summary>
    public const short VanillaRow = 2046;
    private const float None = 99999f;

    public static bool IsOurs(int id) => id is >= FirstRow and <= LastRow;

    /// <summary>Adds (or replaces) the row and returns its ID.</summary>
    public static short AddRow(PARAM param)
    {
        param.Rows.RemoveAll(r => IsOurs(r.ID));
        var template = param[TemplateRow] ?? param[VanillaRow] ?? param.Rows.FirstOrDefault()
                       ?? throw new InvalidDataException("PartsDrawParam has no rows to copy");
        var row = new PARAM.Row(template) { ID = FirstRow, Name = "DS3 map piece" };
        Set(row, "lv01_BorderDist", None);
        Set(row, "lv01_PlayDist", 0f);
        Set(row, "drawDist", None);
        Set(row, "drawFadeRange", 0f);
        Set(row, "tex_lv01_BorderDist", 256f);
        Set(row, "tex_lv01_PlayDist", 32f);
        Set(row, "IncludeLodMapLv", (sbyte)0);
        Set(row, "lodType", (byte)1);
        Set(row, "DistantViewModel_BorderDist", None);
        Set(row, "DistantViewModel_PlayDist", 0f);
        // Whatever the template says, the row must not turn a piece into a shadow or reflection proxy.
        Set(row, "isShadowOnly", (sbyte)0);
        Set(row, "drawOnlyReflectCam", (sbyte)0);
        param.Rows.Add(row);
        param.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return FirstRow;
    }

    private static void Set(PARAM.Row row, string field, object value)
    {
        var cell = row.Cells.FirstOrDefault(c => c.Def.InternalName == field);
        if (cell is not null)
            cell.Value = Convert.ChangeType(value, cell.Value.GetType());
    }

    /// <summary>
    /// Writes the row into the mod's regulation and returns the ID map pieces should name. If that fails the pieces
    /// name a vanilla row instead, so they are never left pointing at a row that doesn't exist.
    /// </summary>
    public static (short Row, string Note) Write(PorterContext ctx)
    {
        try
        {
            var regulation = ActionButtons.LoadRegulation(ctx);
            var param = regulation.Get(ParamName) ?? throw new InvalidDataException("Elden Ring has no PartsDrawParam");
            var template = param[TemplateRow] is not null ? TemplateRow : param[VanillaRow] is not null ? VanillaRow : param.Rows[0].ID;
            var id = AddRow(param);
            regulation.Save(ctx.ModPath("regulation.bin"));
            return (id, $"map piece draw settings: PartsDrawParam row {id} (a copy of row {template}, unlimited draw distance, no LOD switching)");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (VanillaRow, $"map piece draw settings: could not add a PartsDrawParam row ({ex.Message}); pieces use vanilla row {VanillaRow}");
        }
    }

    /// <summary>Row IDs in the mod's regulation (or Elden Ring's when the mod has none), for the validator.</summary>
    public static HashSet<int>? RowsInRegulation(PorterContext ctx)
    {
        try { return ActionButtons.LoadRegulation(ctx).Get(ParamName)?.Rows.Select(r => r.ID).ToHashSet(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}
