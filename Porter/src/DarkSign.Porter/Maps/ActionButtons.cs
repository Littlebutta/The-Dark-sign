using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's "IF Action Button" conditions (doors, levers, chests, elevators, boss fog) carry the prompt's shape in the
/// instruction itself: dummy poly, reach, angle and the help text. Elden Ring's "IF Action Button in Area" takes an
/// ActionButtonParam row instead. Each distinct DS3 prompt gets one new row, made the way JortPob makes its door
/// prompts: a copy of a vanilla row (6000, the talk prompt) with the shape and text set.
///
/// The prompt text is DS3's own: when Elden Ring has a prompt with the same words ("Open", "Pull lever") its text
/// ID is reused; otherwise the DS3 words are added to the DLC02 menu binder's ActionButtonText_dlc02, which ships
/// nearly empty, so no Elden Ring text changes.
///
/// DS3's newer "IF Action Button in Area" already names an ActionButtonParam row, but a DS3 row: every boss fog
/// wall (4001800 Iudex Gundyr, 3001800 Vordt, 4501800 Friede...), Gundyr's sword (8900), Father Ariandel's talk
/// prompt (9373), chests (8010) and levers. Up to 1.0.383 those IDs were carried over as they were, and Elden Ring
/// has no such rows (no prompt: no fog wall to walk through, no sword to pull) or other prompts under the same
/// numbers (9600 "Become the Elden Lord", 6290 "Accept primal glintstone"). Each DS3 row a script names now gets an
/// Elden Ring copy (<see cref="RowForDs3"/>): DS3's shape, reach, angles and priority, its text found or added as
/// above, and its "invalid" / "grayed out" flags moved like the scripts' flags.
/// </summary>
public sealed class ActionButtons
{
    public const int FirstRow = 74_000_000, RowRange = 100_000;
    public const int FirstText = 74_000_000;
    public const int TemplateRow = 6000;

    public sealed record Button(int DummyPoly, float Distance, int Angle, int HelpMessage, bool BossFog);

    private readonly Dictionary<Button, int> _rows = new();
    private readonly Dictionary<int, int> _ds3Rows = new();

    /// <summary>DS3 ActionButtonParam rows the scripts name, and their Elden Ring copies.</summary>
    public IReadOnlyDictionary<int, int> Ds3Rows => _ds3Rows;

    /// <summary>Moves a DS3 event flag (a row's invalid / grayed-out flag) the way the scripts' flags move.</summary>
    public Func<long, long>? FlagMover { get; set; }
    private readonly Func<int, bool> _taken;
    private int _next = FirstRow;

    public IReadOnlyDictionary<Button, int> Rows => _rows;

    /// <param name="taken">Row IDs Elden Ring already uses (other than ours from an earlier run).</param>
    public ActionButtons(Func<int, bool>? taken = null) => _taken = taken ?? (_ => false);

    /// <summary>The ActionButtonParam row for a DS3 prompt (a new one the first time).</summary>
    public int RowFor(Button button)
    {
        if (_rows.TryGetValue(button, out var id))
            return id;
        while (_taken(_next))
            _next++;
        if (_next >= FirstRow + RowRange)
            throw new InvalidOperationException("no room left for DS3 action buttons");
        return _rows[button] = _next++;
    }

    /// <summary>The Elden Ring copy of a DS3 ActionButtonParam row (a new ID the first time; written by <see cref="Write"/>).</summary>
    public int RowForDs3(int ds3Row)
    {
        if (_ds3Rows.TryGetValue(ds3Row, out var id))
            return id;
        while (_taken(_next))
            _next++;
        if (_next >= FirstRow + RowRange)
            throw new InvalidOperationException("no room left for DS3 action buttons");
        return _ds3Rows[ds3Row] = _next++;
    }

    public static bool IsOurs(int rowId) => rowId >= FirstRow && rowId < FirstRow + RowRange;

    /// <summary>
    /// Adds the rows to ActionButtonParam (ours from an earlier run are replaced). <paramref name="textId"/> gives the
    /// Elden Ring text ID for a DS3 help message (-1 keeps the template's).
    /// </summary>
    public int WriteRows(PARAM param, Func<int, int> textId, PARAM? ds3Param = null, Func<int, int>? ds3TextId = null)
    {
        param.Rows.RemoveAll(r => IsOurs(r.ID));
        var template = param[TemplateRow] ?? param.Rows.FirstOrDefault()
                       ?? throw new InvalidDataException("ActionButtonParam has no rows to copy");
        int copied = WriteDs3Rows(param, template, ds3Param, ds3TextId ?? textId);
        foreach (var (button, id) in _rows.OrderBy(r => r.Value))
        {
            var row = new PARAM.Row(template) { ID = id, Name = $"DS3 action button {button.HelpMessage}" };
            Set(row, "regionType", (byte)0); // cylinder around the dummy poly, like DS3's
            Set(row, "dummyPoly1", button.DummyPoly);
            Set(row, "dummyPoly2", -1);
            Set(row, "radius", button.Distance);
            Set(row, "angle", button.Angle is > 0 and <= 180 ? button.Angle : 180);
            Set(row, "height", Math.Max(2f, button.Distance));
            Set(row, "baseHeightOffset", -0.5f);
            var text = textId(button.HelpMessage);
            if (text > 0)
                Set(row, "textId", text);
            Set(row, "isGrayoutForRide", (byte)1);
            Set(row, "execInvalidTime", 1f);
            param.Rows.Add(row);
        }
        param.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return _rows.Count + copied;
    }

    /// <summary>Elden Ring's fog wall prompt ("Traverse the mist"), the template for DS3's fog walls.</summary>
    public const int ErFogRow = 10000, Ds3FogRow = 10000;

    // Fields that are not copied by name: the text and flags are moved, padding and Elden Ring-only fields keep the template's.
    private static readonly HashSet<string> NotCopied = new(StringComparer.OrdinalIgnoreCase) { "textId", "InvalidFlag", "grayoutFlag", "category" };

    /// <summary>
    /// The Elden Ring copies of the DS3 rows the scripts name. Each starts from Elden Ring's fog wall row when DS3's
    /// row shows DS3's fog text (DS3's own generic fog row 10000 says which text that is), else from the talk row;
    /// DS3's values are copied field by field where both games' definitions name the same field (regionType,
    /// dummyPoly1/2, radius, angle, depth, width, height, baseHeightOffset, angleCheckType, allowAngle, textBoxType,
    /// priority, execInvalidTime, execButtonCircle). A DS3 row that isn't in DS3's regulation keeps the template.
    /// </summary>
    private int WriteDs3Rows(PARAM param, PARAM.Row talk, PARAM? ds3Param, Func<int, int> textId)
    {
        if (_ds3Rows.Count == 0)
            return 0;
        var fog = param[ErFogRow] ?? talk;
        var ds3FogText = ds3Param?[Ds3FogRow] is { } ds3Fog ? Get(ds3Fog, "textId") : null;
        foreach (var (ds3Id, id) in _ds3Rows.OrderBy(r => r.Value))
        {
            var src = ds3Param?[ds3Id];
            var isFog = src is not null && ds3FogText is not null && Equals(Get(src, "textId"), ds3FogText);
            var row = new PARAM.Row(isFog ? fog : talk) { ID = id, Name = $"DS3 action button row {ds3Id}" + (src?.Name is { Length: > 0 } n ? $" ({n})" : "") };
            if (src is not null)
            {
                foreach (var cell in src.Cells)
                {
                    var name = cell.Def.InternalName;
                    if (NotCopied.Contains(name) || name.StartsWith("pad", StringComparison.OrdinalIgnoreCase) || cell.Value is byte[])
                        continue;
                    var to = row.Cells.FirstOrDefault(c => string.Equals(c.Def.InternalName, name, StringComparison.OrdinalIgnoreCase));
                    if (to is null || to.Value is byte[])
                        continue;
                    try
                    {
                        to.Value = Convert.ChangeType(cell.Value, to.Value.GetType());
                    }
                    catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
                    {
                        // a field both games name but hold differently keeps the template's value
                    }
                }
                if (Get(src, "textId") is { } text && System.Convert.ToInt32(text) is > 0 and var t && textId(t) is > 0 and var erText)
                    Set(row, "textId", erText);
                SetFlag(row, "invalidFlag", Get(src, "InvalidFlag"));
                SetFlag(row, "grayoutFlag", Get(src, "grayoutFlag"));
            }
            param.Rows.Add(row);
        }
        return _ds3Rows.Count;
    }

    private void SetFlag(PARAM.Row row, string field, object? ds3Value)
    {
        var flag = ds3Value is null ? 0L : System.Convert.ToInt64(ds3Value);
        // DS3 writes -1 (or 0) for "no flag"; Elden Ring 0.
        Set(row, field, flag > 0 ? (uint)Math.Min(FlagMover?.Invoke(flag) ?? flag, uint.MaxValue) : 0u);
    }

    private static object? Get(PARAM.Row row, string field) =>
        row.Cells.FirstOrDefault(c => string.Equals(c.Def.InternalName, field, StringComparison.OrdinalIgnoreCase))?.Value;

    private static void Set(PARAM.Row row, string field, object value)
    {
        var cell = row.Cells.FirstOrDefault(c => string.Equals(c.Def.InternalName, field, StringComparison.OrdinalIgnoreCase));
        if (cell is null)
            return;
        cell.Value = System.Convert.ChangeType(value, cell.Value.GetType());
    }

    // ---- Game files ----

    private const string Ds3Menu = "/msg/engus/menu_dlc2.msgbnd.dcx", Ds3MenuBase = "/msg/engus/menu.msgbnd.dcx";
    private static readonly string[] ErMenus = ["/msg/engus/menu.msgbnd.dcx", "/msg/engus/menu_dlc01.msgbnd.dcx", "/msg/engus/menu_dlc02.msgbnd.dcx"];
    private const string ErModMenu = ModMenu;
    /// <summary>The menu binder port-maps writes the DS3 prompt text into (only port-maps writes it).</summary>
    public const string ModMenu = "/msg/engus/menu_dlc02.msgbnd.dcx";

    /// <summary>Row IDs ActionButtonParam already has, other than ours: read before converting the scripts.</summary>
    public static HashSet<int> TakenRows(PARAM param) => param.Rows.Select(r => r.ID).Where(id => !IsOurs(id)).ToHashSet();

    /// <summary>The mod's regulation if a port step already wrote one, otherwise Elden Ring's.</summary>
    public static ParamStore LoadRegulation(PorterContext ctx)
    {
        var modDir = Path.GetDirectoryName(ctx.ModPath("regulation.bin"))!;
        return File.Exists(Path.Combine(modDir, "regulation.bin"))
            ? ParamStore.Load(PorterGame.EldenRing, modDir, ctx.Data)
            : ctx.LoadParams(PorterGame.EldenRing);
    }

    /// <summary>Writes the rows into the mod's regulation and the prompt text into the DLC02 menu binder.</summary>
    public string Write(PorterContext ctx, GameArchives ds3, GameArchives er, ParamStore regulation)
    {
        var param = regulation.Get("ActionButtonParam") ?? throw new InvalidDataException("Elden Ring has no ActionButtonParam");
        var ds3Text = LoadDs3Help(ds3);
        var erText = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ErMenus.Where(er.Contains))
            foreach (var (id, text) in ReadFmgs(BND4.Read(er.Read(path)), "ActionButtonText"))
                if (!string.IsNullOrWhiteSpace(text) && !(id >= FirstText && id < FirstText + RowRange))
                    erText.TryAdd(text.Trim(), id);

        var modMenuPath = ctx.ModPath(MapPorter.ModParts(ErModMenu));
        var (vanillaMenu, compression) = er.ReadWithCompression(ErModMenu);
        var menu = BND4.Read(File.Exists(modMenuPath) ? DCX.Decompress(File.ReadAllBytes(modMenuPath)) : vanillaMenu);
        var file = menu.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith("ActionButtonText", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException($"{ItemText.Leaf(ErModMenu)} has no ActionButtonText file");
        var fmg = FMG.Read(file.Bytes);
        fmg.Entries.RemoveAll(e => e.ID >= FirstText && e.ID < FirstText + RowRange);
        int nextText = FirstText, reused = 0, added = 0, missing = 0;
        var byMessage = new Dictionary<int, int>();

        int TextId(int message)
        {
            if (byMessage.TryGetValue(message, out var known))
                return known;
            if (!ds3Text.TryGetValue(message, out var words) || string.IsNullOrWhiteSpace(words))
            {
                missing++;
                return byMessage[message] = -1;
            }
            if (erText.TryGetValue(words.Trim(), out var same))
            {
                reused++;
                return byMessage[message] = same;
            }
            var id = nextText++;
            fmg.Entries.Add(new FMG.Entry(id, words.Trim()));
            added++;
            return byMessage[message] = id;
        }

        // DS3's rows name DS3's own button text IDs (10010403 "Traverse the fog"): the same lookup.
        PARAM? ds3Param = null;
        try
        {
            ds3Param = _ds3Rows.Count == 0 ? null : ctx.LoadParams(PorterGame.DarkSouls3).Get("ActionButtonParam");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            // the copies keep the template's shape and text
        }
        var rows = WriteRows(param, TextId, ds3Param);
        file.Bytes = fmg.Write();
        File.WriteAllBytes(modMenuPath, DCX.Compress(menu.Write(), compression));
        regulation.Save(ctx.ModPath("regulation.bin"));
        return $"action buttons: {rows} ActionButtonParam rows ({_ds3Rows.Count} copies of DS3 rows"
               + (_ds3Rows.Count > 0 && ds3Param is null ? ", DS3's regulation unreadable: shapes from the template" : "") + "); prompt text {reused} reused from Elden Ring, {added} added from DS3, {missing} not found";
    }

    private static Dictionary<int, string> LoadDs3Help(GameArchives ds3)
    {
        // The help messages live in DS3's menu text; take the event and action button files first.
        var all = new Dictionary<int, string>();
        foreach (var path in new[] { Ds3Menu, Ds3MenuBase }.Where(ds3.Contains))
        {
            var bnd = BND4.Read(ds3.Read(path));
            foreach (var (id, text) in ReadFmgs(bnd, "Event").Concat(ReadFmgs(bnd, "ActionButton")).Concat(ReadFmgs(bnd, "")))
                if (!string.IsNullOrWhiteSpace(text))
                    all.TryAdd(id, text);
        }
        return all;
    }

    private static IEnumerable<(int Id, string Text)> ReadFmgs(BND4 bnd, string namePart)
    {
        foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".fmg", StringComparison.OrdinalIgnoreCase)
                                               && ItemText.Leaf(f.Name).Contains(namePart, StringComparison.OrdinalIgnoreCase)))
            foreach (var e in FMG.Read(f.Bytes).Entries)
                yield return (e.ID, e.Text);
    }
}
