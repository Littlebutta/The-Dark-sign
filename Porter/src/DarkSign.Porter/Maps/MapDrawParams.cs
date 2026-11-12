using System.Numerics;
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
///
/// The row also says how near a map piece must be for its textures to load in full: beyond
/// <see cref="TextureDistance"/> the game draws it with the low detail copies (<see cref="MapTextures"/>).
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

    /// <summary>
    /// How near (metres) a map piece is drawn with its full textures, and the band around that distance in which
    /// the game doesn't switch back (so a step to and fro swaps nothing). Half of JortPob's 256 / 32, which it
    /// chose for an open world of small textures: a DS3 map is a few hundred metres across, so at 256 m nearly every
    /// texture of the map was in graphics memory in full all the time. At 128 m a half size texture still has more
    /// detail than a 4K screen shows (a 4096 pixel texture across 4 m of wall covers about 60 pixels there).
    /// </summary>
    public const float TextureDistance = 128f, TextureBand = 16f;

    /// <summary>JortPob's values, which every build up to 1.0.299 used ("--full-low-textures").</summary>
    public const float LegacyTextureDistance = 256f, LegacyTextureBand = 32f;

    public static bool IsOurs(int id) => id is >= FirstRow and <= LastRow;

    /// <summary>
    /// Large map pieces load their full textures from further out: radius (half the model box's diagonal, metres)
    /// up to which a row applies, and the metres added to <see cref="TextureDistance"/>. Row
    /// <see cref="FirstRow"/> + i is size i. A DS3 map piece can be a whole floor, cliff or building 100 to 500 m
    /// across (17 to 20% of the ported vertices are in pieces over 64 m across, measured on the 1.0.4xx map-pieces
    /// reports), where an Elden Ring piece is a few metres. Up to 1.0.422 every piece switched to its half size copy
    /// at the same 128 m, so on a large floor or building whose middle is 128 m away the textures under the
    /// player's feet were the low detail ones (Devin, 2026-11-12: floor and building textures not always at full
    /// detail). Which point of a piece the game measures from is unconfirmed; with the radius added, the near edge of
    /// a large piece is never further than the small pieces' distance. Rows FirstRow + 1 to + 5 sit below the
    /// objects' rows (<see cref="ObjectPort.FirstDrawRow"/> = FirstRow + 10).
    /// </summary>
    public static readonly (float Radius, float Extra)[] TextureBySize =
    [
        (16f, 0f), (32f, 32f), (64f, 64f), (128f, 128f), (256f, 256f), (float.MaxValue, 512f),
    ];

    /// <summary>The row for a map piece of this radius (metres).</summary>
    public static short RowFor(float radius)
    {
        for (int i = 0; i < TextureBySize.Length; i++)
            if (radius <= TextureBySize[i].Radius)
                return (short)(FirstRow + i);
        return (short)(FirstRow + TextureBySize.Length - 1);
    }

    /// <summary>
    /// Points each map piece of a layout that names <see cref="FirstRow"/> at the row for its size: the radius of its
    /// model's box (<paramref name="radiusOf"/>, null when unknown, which keeps the row) times the piece's largest scale.
    /// Returns a line for the log.
    /// </summary>
    public static string PointBySize(MSBE msb, Func<string, float?> radiusOf)
    {
        var counts = new int[TextureBySize.Length];
        foreach (var piece in msb.Parts.MapPieces)
        {
            if (piece.PartsDrawParamID != FirstRow || radiusOf(piece.ModelName) is not { } radius)
                continue;
            var scale = piece.Scale == Vector3.Zero ? 1f : MathF.Max(MathF.Abs(piece.Scale.X), MathF.Max(MathF.Abs(piece.Scale.Y), MathF.Abs(piece.Scale.Z)));
            var row = RowFor(radius * scale);
            piece.PartsDrawParamID = row;
            counts[row - FirstRow]++;
        }
        var parts = Enumerable.Range(0, counts.Length).Where(i => counts[i] > 0)
            .Select(i => $"{counts[i]} {(i == 0 ? $"up to {TextureBySize[0].Radius:0} m" : i == counts.Length - 1 ? $"over {TextureBySize[i - 1].Radius:0} m" : $"up to {TextureBySize[i].Radius:0} m")} (+{TextureBySize[i].Extra:0} m)");
        return $"texture distance by size: pieces by radius, and how much further out they load full textures: {string.Join(", ", parts)}";
    }

    /// <summary>Adds (or replaces) the rows (one per size of <see cref="TextureBySize"/>) and returns the first one's ID.</summary>
    public static short AddRow(PARAM param, bool legacyTextures = false)
    {
        param.Rows.RemoveAll(r => r.ID >= FirstRow && r.ID < FirstRow + TextureBySize.Length);
        var template = param[TemplateRow] ?? param[VanillaRow] ?? param.Rows.FirstOrDefault()
                       ?? throw new InvalidDataException("PartsDrawParam has no rows to copy");
        float distance = legacyTextures ? LegacyTextureDistance : TextureDistance;
        for (int i = 0; i < TextureBySize.Length; i++)
        {
            var row = new PARAM.Row(template) { ID = FirstRow + i, Name = i == 0 ? "DS3 map piece" : $"DS3 map piece, radius up to {TextureBySize[i].Radius:0} m" };
            Set(row, "lv01_BorderDist", None);
            Set(row, "lv01_PlayDist", 0f);
            Set(row, "drawDist", None);
            Set(row, "drawFadeRange", 0f);
            Set(row, "tex_lv01_BorderDist", distance + TextureBySize[i].Extra);
            Set(row, "tex_lv01_PlayDist", legacyTextures ? LegacyTextureBand : TextureBand);
            Set(row, "IncludeLodMapLv", (sbyte)0);
            Set(row, "lodType", (byte)1);
            Set(row, "DistantViewModel_BorderDist", None);
            Set(row, "DistantViewModel_PlayDist", 0f);
            // Whatever the template says, the row must not turn a piece into a shadow or reflection proxy.
            Set(row, "isShadowOnly", (sbyte)0);
            Set(row, "drawOnlyReflectCam", (sbyte)0);
            param.Rows.Add(row);
        }
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
    public static (short Row, string Note) Write(PorterContext ctx, bool legacyTextures = false)
    {
        try
        {
            var regulation = ActionButtons.LoadRegulation(ctx);
            var param = regulation.Get(ParamName) ?? throw new InvalidDataException("Elden Ring has no PartsDrawParam");
            var template = param[TemplateRow] is not null ? TemplateRow : param[VanillaRow] is not null ? VanillaRow : param.Rows[0].ID;
            var id = AddRow(param, legacyTextures);
            regulation.Save(ctx.ModPath("regulation.bin"));
            return (id, $"map piece draw settings: PartsDrawParam row {id} (a copy of row {template}, unlimited draw distance, no LOD switching, "
                        + $"full textures within {(legacyTextures ? LegacyTextureDistance : TextureDistance):F0} m, "
                        + $"further for larger pieces: rows {id + 1} to {id + TextureBySize.Length - 1})");
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
