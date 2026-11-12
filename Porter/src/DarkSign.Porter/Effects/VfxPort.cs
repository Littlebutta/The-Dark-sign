using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Ports DS3 SpEffectVfxParam rows into Elden Ring's under new IDs: what a SpEffect looks like while it lasts (the
/// glow on the Moonlight Greatsword's blade, the frost on the Pontiff Knight Curved Sword, the flame on the Onyx
/// Blade). Both games keep the visuals of an effect out of SpEffectParam, in rows the effect names (DS3 EffectVfx0-7,
/// Elden Ring vfxId-vfxId7), and the two structs are the same engine struct a game apart: the effect played when the
/// SpEffect starts, while it lasts and when it ends (three FXR IDs with a dummy poly each), the blade span a weapon
/// enchant is drawn along (eight start / end dummy poly pairs, which Paramdex's DS3 def calls SpSfxId0-15) and the
/// same flags. Without these rows a ported DS3 effect was invisible, and an effect that is only visual was skipped.
/// <para>The FXR IDs are DS3's and are renumbered by the effect port (FxrPort), which runs later in the build, so a
/// new row is written with its effect fields empty and <see cref="Bind"/> fills them in once the effects are
/// converted. An effect that couldn't be converted stays empty (-1), never a DS3 number, which would name an
/// unrelated Elden Ring effect.</para>
/// Elden Ring-only fields take the value most Elden Ring rows have. Method (RULE 3): the row layout and the way a
/// SpEffect, a bullet and a weapon name their effects follow sn0wsh00's Elden Ring FXR / bullet articles (Nexus Mods
/// article 396) and Paramdex's field names; the conversion itself is this project's.
/// </summary>
public sealed class VfxPort
{
    /// <summary>First ID of the ported rows; Elden Ring's own stop far below.</summary>
    public const int FirstNewId = 69_000_000;

    internal static readonly string[] Ds3EffectFields = ["midstSfxId", "InitSfxId", "finishSfxId"];

    // DS3 field -> Elden Ring field, same meaning. The three effect IDs wait for Bind.
    private static readonly (string Ds3, string Er)[] Fields =
    [
        ("camouflageBeginDist", "camouflageBeginDist"), ("camouflageEndDist", "camouflageEndDist"),
        ("midstDmyId", "midstDmyId"), ("initDmyId", "initDmyId"), ("finishDmyId", "finishDmyId"),
        ("effectType", "effectType"),
        ("playCategory", "playCategory"), ("playPriority", "playPriority"),
        ("existEffectForLarge", "existEffectForLarge"), ("existEffectForSoul", "existEffectForSoul"),
        ("existInvisibleAtCamouflage", "effectInvisibleAtCamouflage"), ("useCamouflage", "useCamouflage"),
        ("invisibleAtFriendCamouflage", "invisibleAtFriendCamouflage"), ("halfCamouflage", "halfCamouflage"),
        ("isInvisibleWeapon", "isInvisibleWeapon"), ("isSilence", "isSilence"),
    ];

    private static readonly (string Ds3, string Er)[] EffectFields =
        [("midstSfxId", "midstSfxId"), ("InitSfxId", "initSfxId"), ("finishSfxId", "finishSfxId")];

    // DS3 references that mean something else (or nothing) in Elden Ring: armour swaps, decals, sounds, material params.
    private static readonly string[] Cleared =
        ["midstSeId", "initSeId", "finishSeId", "transformProtectorId", "decalId1", "decalId2", "materialParamId"];

    private readonly PARAM _er, _ds3;
    private readonly object?[] _defaults;
    private readonly Dictionary<int, int> _ported = [];
    private readonly List<(PARAM.Row Row, string Field, int Ds3Fxr)> _pending = [];
    private int _next = FirstNewId;
    private int _bound, _unbound;

    public VfxPort(ParamStore er, ParamStore ds3)
        : this(er.Get("SpEffectVfxParam") ?? throw new InvalidDataException("Elden Ring has no SpEffectVfxParam"),
            ds3.Get("SpEffectVfxParam") ?? throw new InvalidDataException("DS3 has no SpEffectVfxParam"))
    {
    }

    internal VfxPort(PARAM er, PARAM ds3)
    {
        _er = er;
        _ds3 = ds3;
        _defaults = ParamDefaults.Common(_er);
        // The blade span Elden Ring's own enchant rows use, per enchant type (1 right-hand weapon, 2 left hand / held
        // flame): the most common set of its eight start / end dummy poly pairs.
        _enchantSpans = _er.Rows.Where(r => ParamDefaults.Int(r, "effectType") is 1 or 2).GroupBy(r => ParamDefaults.Int(r, "effectType"))
            .ToDictionary(g => g.Key, g => g.GroupBy(r => string.Join(",", SpanFields.Select(f => ParamDefaults.Int(r, f))))
                .OrderByDescending(x => x.Count()).First().Key.Split(',').Select(int.Parse).ToArray());
    }

    private readonly Dictionary<int, int[]> _enchantSpans;

    private static readonly string[] SpanFields =
        [.. Enumerable.Range(0, 8).SelectMany(k => new[] { $"enchantStartDmyId_{k}", $"enchantEndDmyId_{k}" })];

    /// <summary>Whether a DS3 row draws a weapon enchant (effectType 1 or 2 in both games).</summary>
    public bool IsEnchant(int ds3Id) => _ds3[ds3Id] is { } row && ParamDefaults.Int(row, "effectType") is 1 or 2;

    public int Count => _ported.Count(p => p.Value > 0);

    /// <summary>DS3 row -> Elden Ring row, for the rows ported so far (-1: nothing to show).</summary>
    public IReadOnlyDictionary<int, int> Ported => _ported;

    /// <summary>The DS3 FXR IDs a DS3 row plays (start, while it lasts, end).</summary>
    public IEnumerable<int> FxrIds(int ds3Id) =>
        _ds3[ds3Id] is { } row ? Ds3EffectFields.Select(f => ParamDefaults.Int(row, f)).Where(id => id > 0) : [];

    /// <summary>Whether a DS3 row shows anything: an effect, or hides the weapon.</summary>
    public bool Visible(int ds3Id) => _ds3[ds3Id] is { } row && (FxrIds(ds3Id).Any() || ParamDefaults.Int(row, "isInvisibleWeapon") != 0);

    /// <summary>The Elden Ring ID for a DS3 row, porting it on first use; -1 when DS3 has no such row or it shows nothing.</summary>
    public int Port(int ds3Id)
    {
        if (ds3Id <= 0)
            return -1;
        if (_ported.TryGetValue(ds3Id, out var done))
            return done;
        if (!Visible(ds3Id))
            return _ported[ds3Id] = -1;
        var source = _ds3[ds3Id]!;
        while (_er[_next] is not null)
            _next++;
        var row = new PARAM.Row(_next++, source.Name ?? "", _er.AppliedParamdef);
        for (int i = 0; i < row.Cells.Count; i++)
            if (_defaults[i] is { } value)
                row.Cells[i].Value = value;
        foreach (var (ds3Field, erField) in Fields)
            ParamDefaults.Copy(source, ds3Field, row, erField);
        // The span a weapon enchant is drawn along: sixteen dummy poly IDs (start / end pairs), in the same order in
        // both games (DS3's Dark Blade row has 10300 11300 10310 ... in every other one, exactly as Elden Ring's own
        // enchant rows do). Most DS3 enchant rows leave them all empty and DS3 falls back to the weapon's enchant
        // dummy polys; Elden Ring's rows always spell them out, so an empty DS3 span takes Elden Ring's usual one.
        bool anySpan = Enumerable.Range(0, 16).Any(k => ParamDefaults.Int(source, $"SpSfxId{k}") > 0);
        if (anySpan)
            for (int k = 0; k < 16; k++)
                ParamDefaults.Set(row, SpanFields[k], ParamDefaults.Int(source, $"SpSfxId{k}") is > 0 and var dummy ? dummy : -1);
        else if (_enchantSpans.TryGetValue(ParamDefaults.Int(source, "effectType"), out var span))
            for (int k = 0; k < 16; k++)
                ParamDefaults.Set(row, SpanFields[k], span[k]);
        // The enchant's tint on the weapon model is a PhantomParam row, numbered differently in each game.
        ParamDefaults.Set(row, "soulParamIdForWepEnchant", 0);
        foreach (var field in Cleared)
            ParamDefaults.Set(row, field, -1);
        // DS3 has one enchant effect per row; Elden Ring's "add the weapon's enchant size to the ID" would ask for
        // effects that don't exist next to the ported one.
        ParamDefaults.Set(row, "isUseOffsetEnchantSfxSize", 0);
        foreach (var (ds3Field, erField) in EffectFields)
        {
            ParamDefaults.Set(row, erField, -1);
            if (ParamDefaults.Int(source, ds3Field) is > 0 and var fxr)
                _pending.Add((row, erField, fxr));
        }
        _er.Rows.Add(row);
        _er.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        if (_effects is not null)
            BindPending();
        return _ported[ds3Id] = row.ID;
    }

    private FxrPort.Result? _effects;

    /// <summary>Gives every ported row (and the ones ported after this) the Elden Ring IDs of its converted effects.</summary>
    public void Bind(FxrPort.Result effects)
    {
        _effects = effects;
        BindPending();
    }

    public bool IsBound => _effects is not null;

    private void BindPending()
    {
        foreach (var (row, field, fxr) in _pending)
        {
            if (_effects!.Map(fxr) is { } id)
            {
                ParamDefaults.Set(row, field, id);
                _bound++;
            }
            else
                _unbound++;
        }
        _pending.Clear();
    }

    public string Summary => $"{Count} visual rows, {_bound} effects shown, {_unbound} left empty (effect not converted)";
}

/// <summary>Helpers for writing rows of an Elden Ring param from a DS3 row of a differently named def.</summary>
internal static class ParamDefaults
{
    /// <summary>For each field, the value most rows of the param have (null for byte arrays and empty params).</summary>
    public static object?[] Common(PARAM param, Func<PARAM.Row, bool>? own = null)
    {
        int cells = param.AppliedParamdef.Fields.Count;
        var result = new object?[cells];
        var rows = own is null ? param.Rows : param.Rows.Where(own).ToList();
        if (rows.Count == 0)
            return result;
        for (int i = 0; i < cells; i++)
        {
            if (rows[0].Cells[i].Value is null or byte[])
                continue;
            result[i] = rows.GroupBy(r => r.Cells[i].Value!).OrderByDescending(g => g.Count()).First().Key;
        }
        return result;
    }

    /// <summary>Copies a field when both rows have it, converted to the target's type; out of range keeps the target's value.</summary>
    public static void Copy(PARAM.Row from, string fromField, PARAM.Row to, string toField)
    {
        if (Cell(from, fromField)?.Value is not { } value || value is byte[] || Cell(to, toField) is not { } cell || cell.Value is null or byte[])
            return;
        try
        {
            cell.Value = cell.Value switch
            {
                float => Convert.ToSingle(value),
                double => Convert.ToDouble(value),
                // The same byte, which Paramdex's DS3 defs often type unsigned where Elden Ring's is signed (a bullet's
                // shootAngleXZ 226 is -30).
                sbyte when value is byte b => unchecked((sbyte)b),
                short when value is ushort u => unchecked((short)u),
                _ => Convert.ChangeType(Math.Round(Convert.ToDouble(value)), cell.Value.GetType()),
            };
        }
        catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException)
        {
        }
    }

    public static void Set(PARAM.Row row, string field, double value)
    {
        if (Cell(row, field) is not { } cell || cell.Value is null or byte[])
            return;
        try { cell.Value = Convert.ChangeType(value, cell.Value.GetType()); }
        catch (OverflowException) { }
    }

    /// <summary>The row's field of this name, or null when the def has none (PARAM.Row's indexer throws).</summary>
    public static PARAM.Cell? Cell(PARAM.Row row, string field) => row.Cells.FirstOrDefault(c => c.Def.InternalName == field);

    public static int Int(PARAM.Row row, string field) => Cell(row, field)?.Value is { } v and not byte[] ? Convert.ToInt32(v) : 0;
}
