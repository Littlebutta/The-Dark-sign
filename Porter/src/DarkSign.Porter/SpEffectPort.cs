using SoulsFormats;
using DarkSign.Porter.Enemies;

namespace DarkSign.Porter;

/// <summary>
/// Ports DS3 SpEffects (special effects) into Elden Ring under new IDs: the status a weapon builds up on hit (Friede's
/// frostbite, bleed, poison), the effects a weapon gives while equipped, and the buffs a DS3 weapon art casts.
/// DS3 and Elden Ring number their effects differently and name many fields differently, so a DS3 ID copied as is
/// named an unrelated Elden Ring effect (or none), which is why ported weapons had no status until now.
/// <list type="bullet">
/// <item>Status on hit: the new row is a copy of the Elden Ring row most Elden Ring weapons use for the same status
/// (so the game treats it exactly like its own bleed or frost), with DS3's buildup value. DS3's toxic becomes poison
/// (Elden Ring has no toxic).</item>
/// <item>Anything else (regeneration, attack buffs, damage cuts): a fresh row with DS3's values for the fields both
/// games share and mean the same, renamed where the names differ; other fields keep Elden Ring's defaults.</item>
/// </list>
/// Weapons build status up per upgrade level by adding ReinforceParamWeapon's spEffectId offsets to the row ID, so each
/// ported effect gets a block of 100 IDs: base + offset holds DS3's row at the same offset (or its base row).
/// Fallback "weapon-effects": DARKSIGN_WEAPON_EFFECTS=status (only status on hit) or 0 (none, as before).
/// Report: Work\reports\weapon-effects.tsv.
/// </summary>
public sealed class SpEffectPort
{
    public static readonly string Mode = (Environment.GetEnvironmentVariable("DARKSIGN_WEAPON_EFFECTS") ?? "").ToLowerInvariant();
    public static bool Enabled => Mode != "0";
    /// <summary>Fallback: weapon effects without an Elden Ring row to start from begin blank (every field 0), as up to 1.0.353.</summary>
    public static bool BlankRows => Environment.GetEnvironmentVariable("DARKSIGN_SPEFFECT_BLANK") == "1";
    private static bool StatusOnly => Mode == "status";
    /// <summary>Fallback "effect-visuals": DARKSIGN_EFFECT_VISUALS=0 ports effects without what they look like, as before.</summary>
    public static bool VisualsEnabled => Environment.GetEnvironmentVariable("DARKSIGN_EFFECT_VISUALS") != "0";

    /// <summary>What ported effects look like (SpEffectVfxParam rows); null when off or a game lacks the param.</summary>
    public VfxPort? Visuals { get; }

    private static readonly string[] Ds3VfxFields = [.. Enumerable.Range(0, 8).Select(k => $"EffectVfx{k}")];
    private static string ErVfxField(int k) => k == 0 ? "vfxId" : $"vfxId{k}";

    /// <summary>First ID of the ported effects; each takes a block of 100.</summary>
    public const int FirstNewId = 690_000_000;

    private readonly PARAM _er, _ds3;
    private readonly Dictionary<int, int> _ported = new();
    private readonly HashSet<int> _inProgress = new();
    private readonly Dictionary<string, int> _statusTemplates;
    private readonly List<string> _report = ["DS3 effect\tDS3 name\tElden Ring effect\thow\tdetail"];
    private int _nextBlock;
    private const int NotStatus = -2;

    // DS3 status buildup field -> Elden Ring's.
    private static readonly (string Ds3, string Er)[] StatusFields =
    [
        ("registPoison", "poizonAttackPower"),
        ("registToxic", "poizonAttackPower"),
        ("registBlood", "bloodAttackPower"),
        ("registCurse", "curseAttackPower"),
        ("registFrost", "freezeAttackPower"),
    ];

    private static readonly string[] ErStatusFields =
        ["poizonAttackPower", "diseaseAttackPower", "bloodAttackPower", "curseAttackPower", "freezeAttackPower", "sleepAttackPower", "madnessAttackPower"];

    // Fields both games name the same and use the same way.
    private static readonly string[] SharedFields =
    [
        "effectEndurance", "motionInterval", "changeHpRate", "changeHpPoint", "changeMpRate", "changeMpPoint",
        "changeStaminaRate", "changeStaminaPoint", "maxHpRate", "maxMpRate", "haveSoulRate", "soulRate",
        "darkAttackPowerRate", "magicDamageCutRate", "fireDamageCutRate", "thunderDamageCutRate", "darkDamageCutRate",
        "slashDamageCutRate", "blowDamageCutRate", "thrustDamageCutRate", "neutralDamageCutRate", "guardStaminaCutRate",
        "staminaAttackRate", "hpRecoverRate", "staminaRecoverChangeSpeed", "mpRecoverChangeSpeed", "equipWeightChangeRate",
        "allItemWeightChangeRate", "wepParamChange", "effectTargetSelf", "effectTargetFriend", "effectTargetEnemy",
        "effectTargetPlayer", "effectTargetAI", "effectTargetLive", "effectTargetGhost", "effectTargetAttacker",
        "effectTargetOpposeTarget", "effectTargetFriendlyTarget", "effectTargetSelfTarget",
    ];

    // DS3 name -> Elden Ring name, same meaning.
    private static readonly (string Ds3, string Er)[] RenamedFields =
    [
        ("physAtkRate", "physicsAttackRate"), ("magicAtkRate", "magicAttackRate"), ("fireAtkRate", "fireAttackRate"),
        ("thunderAtkRate", "thunderAttackRate"), ("physAtkPowerRate", "physicsAttackPowerRate"),
        ("magicAtkPowerRate", "magicAttackPowerRate"), ("fireAtkPowerRate", "fireAttackPowerRate"),
        ("thunderAtkPowerRate", "thunderAttackPowerRate"), ("physAtkPower", "physicsAttackPower"),
        ("magicAtkPower", "magicAttackPower"), ("fireAtkPower", "fireAttackPower"), ("thunderAtkPower", "thunderAttackPower"),
        ("darkAtkPower", "darkAttackPower"),
    ];

    // Effects that start other effects: ported too, so the chain stays DS3's.
    private static readonly (string Ds3, string Er)[] ChainFields =
    [
        ("replaceSpEffectId", "replaceSpEffectId"), ("cycleOccurenceSpEffectId", "cycleOccurrenceSpEffectId"),
        ("atkOccurenceSpEffectId", "atkOccurrenceSpEffectId"),
    ];

    // Enemy effects (Enemies/ChrEffects.cs): the new ID is a function of the DS3 ID, so the enemy port can write an
    // effect's ID into a move before the row exists; one row per effect (enemies have no upgrade levels); and a
    // fuller copy (defence rates, life drain, flags), started from the values most Elden Ring rows have.
    private readonly Func<int, int>? _idOf;
    private readonly int _rows = 26;
    private readonly bool _full;
    private object?[]? _common;

    public SpEffectPort(ParamStore er, ParamStore ds3, Func<int, int> idOf, bool full)
        : this(er.Get("SpEffectParam") ?? throw new InvalidDataException("Elden Ring has no SpEffectParam"),
            ds3.Get("SpEffectParam") ?? throw new InvalidDataException("DS3 has no SpEffectParam"),
            er.Get("SpEffectVfxParam"), ds3.Get("SpEffectVfxParam"), er.Get("EquipParamWeapon")!, idOf, full)
    {
    }

    // DS3 fields the fuller copy adds: same name and meaning in both games...
    private static readonly string[] FullSharedFields =
    [
        // spCategory / categoryPriority: of the effects in one category a character carries only the highest priority
        // one. DS3's Jailer row names three lights in category 100 (11899 orange on the weapon, priority 4; 11900 cyan
        // lantern, 1; 11902 blue eyes, 2) and DS3 shows only the orange; without the category all three showed and the
        // ported Jailer glowed blue (1.0.326 playtest). Same names and types in both games' Paramdex defs.
        "spCategory", "categoryPriority",
        "conditionHp", "lifeReductionRate", "lifeReductionType", "soulStealRate", "noDead", "bCurrHPIndependeMaxHP", "hasTarget",
        "enableLifeTime", "isExtendSpEffectLife", "disablePoison", "disableBlood", "disableCurse",
        // animIdOffset: plays every animation shifted by a TAE section (DS3's 5404 = +1,000,000, the spear Lothric
        // Knight's moves); Elden Ring reads it the same way (its 5402 = +2,000,000 on Juvenile Scholars). Enemies/ChrVariants.cs.
        "animIdOffset",
        .. Enumerable.Range(0, 16).Select(k => $"vowType{k}"),
    ];

    // ...and renamed (DS3 defence -> Elden Ring "Diffence").
    private static readonly (string Ds3, string Er)[] FullRenamedFields =
    [
        ("physDefRate", "physicsDiffenceRate"), ("magicDefRate", "magicDiffenceRate"), ("fireDefRate", "fireDiffenceRate"),
        ("thunderDefRate", "thunderDiffenceRate"), ("darkDefRate", "darkDiffenceRate"), ("physDef", "physicsDiffence"),
        ("magicDef", "magicDiffence"), ("fireDef", "fireDiffence"), ("thunderDef", "thunderDiffence"),
    ];

    /// <summary>For the self-tests: every field the port reads on a DS3 row and writes on an Elden Ring one.</summary>
    internal static (string[] Ds3, string[] Er) TestFields =>
        ([.. SharedFields, .. FullSharedFields, .. RenamedFields.Select(f => f.Ds3), .. FullRenamedFields.Select(f => f.Ds3), .. ChainFields.Select(f => f.Ds3),
                .. StatusFields.Select(f => f.Ds3), .. Ds3VfxFields, "stateInfo"],
            [.. SharedFields, .. FullSharedFields, .. RenamedFields.Select(f => f.Er), .. FullRenamedFields.Select(f => f.Er), .. ChainFields.Select(f => f.Er),
                .. ErStatusFields, .. Enumerable.Range(0, 8).Select(ErVfxField), "stateInfo"]);

    public SpEffectPort(ParamStore er, ParamStore ds3)
        : this(er.Get("SpEffectParam") ?? throw new InvalidDataException("Elden Ring has no SpEffectParam"),
            ds3.Get("SpEffectParam") ?? throw new InvalidDataException("DS3 has no SpEffectParam"),
            er.Get("SpEffectVfxParam"), ds3.Get("SpEffectVfxParam"), er.Get("EquipParamWeapon")!)
    {
    }

    internal SpEffectPort(PARAM er, PARAM ds3, PARAM? erVfx, PARAM? ds3Vfx, PARAM weapons, Func<int, int>? idOf = null, bool full = false)
    {
        _er = er;
        _ds3 = ds3;
        _erVfx = erVfx;
        if (idOf is not null)
        {
            _idOf = idOf;
            _rows = 1;
            _full = full;
        }
        if (VisualsEnabled)
        {
            if (erVfx is not null && ds3Vfx is not null)
                Visuals = new VfxPort(erVfx, ds3Vfx);
            else
                Console.WriteLine($"weapon effects: visuals off ({(erVfx is null ? "Elden Ring" : "DS3")} has no SpEffectVfxParam)");
        }
        // The row Elden Ring's own weapons use most for each status (their base rows; upgrades add offsets to them).
        _statusTemplates = weapons.Rows.Where(r => r.ID < WeaponPort.FirstNewId)
            .SelectMany(r => new[] { "spEffectBehaviorId0", "spEffectBehaviorId1", "spEffectBehaviorId2" }.Select(f => Int(r, f)))
            .Where(id => id > 0 && _er[id] is not null)
            .Select(id => (Id: id, Kind: StatusOf(_er[id]!)))
            .Where(t => t.Kind is not null)
            .GroupBy(t => t.Kind!)
            .ToDictionary(g => g.Key, g => g.GroupBy(t => t.Id).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First().Key);
    }

    /// <summary>Whether Elden Ring has an effect with this ID (its own; ported ones are far above DS3's IDs).</summary>
    public bool ErHas(int id) => _er[id] is not null;

    /// <summary>The Paramdex name of a DS3 / Elden Ring effect (null when the game has no such row).</summary>
    public string? Ds3Name(int id) => _ds3[id]?.Name;
    public string? ErName(int id) => _er[id]?.Name;

    public int Count => _ported.Count(p => p.Value > 0);

    public string Summary => $"{Count} DS3 effects ported ({_report.Count(l => l.Contains("\tstatus\t"))} status, " +
                             $"{_report.Count(l => l.Contains("\tvalues\t"))} other, {_report.Count(l => l.Contains("\tvisuals\t"))} visual only), " +
                             $"{_report.Count(l => l.Contains("\tskipped\t"))} skipped; " +
                             $"status templates: {string.Join(", ", _statusTemplates.Select(t => $"{t.Key} {t.Value}"))}";

    /// <summary>The Elden Ring ID for a DS3 effect, porting it on first use; -1 when it can't be ported.</summary>
    public int Port(int ds3Id, bool statusOnly = false)
    {
        if (ds3Id <= 0)
            return -1;
        if (_ported.TryGetValue(ds3Id, out var done))
            return done;
        if (!_inProgress.Add(ds3Id))
            return -1; // a chain back to itself
        try
        {
            int id = PortNew(ds3Id, statusOnly || StatusOnly);
            if (id == NotStatus)
                return -1; // may still be ported in full where full effects are wanted
            return _ported[ds3Id] = id;
        }
        finally
        {
            _inProgress.Remove(ds3Id);
        }
    }

    private int PortNew(int ds3Id, bool statusOnly)
    {
        if (_ds3[ds3Id] is not { } source)
        {
            Note(ds3Id, "", -1, "skipped", "not in DS3's SpEffectParam");
            return -1;
        }
        var status = StatusFields.Where(f => Int(source, f.Ds3) > 0).ToList();
        string? kind = status.Count > 0 ? status[0].Er : null;
        if (kind is null && statusOnly)
        {
            if (StatusOnly)
                Note(ds3Id, source.Name, -1, "skipped", "not a status effect (weapon-effects B ports status only)");
            return NotStatus;
        }
        if (kind is not null && !_statusTemplates.ContainsKey(kind))
        {
            Note(ds3Id, source.Name, -1, "skipped", $"no Elden Ring weapon builds up {kind}");
            return -1;
        }
        if (kind is null && !HasValues(source) && !HasVisuals(source) && ErBase(source.Name) < 0)
        {
            Note(ds3Id, source.Name, -1, "skipped", "nothing Elden Ring can use (state, visuals or fields without an equivalent)");
            return -1;
        }

        int baseId = _idOf is null ? NextBlock() : _idOf(ds3Id);
        if (_idOf is not null && (baseId <= 0 || _er[baseId] is not null))
        {
            Note(ds3Id, source.Name, -1, "skipped", $"its new ID {baseId} is taken or out of range");
            return -1;
        }
        // DS3's own upgrade rows for this effect (base + 1, + 2, ...), as far as they go without a gap.
        int offsets = 1;
        while (_rows > 1 && offsets < 100 && _ds3[ds3Id + offsets] is not null && SameEffect(_ds3[ds3Id]!, _ds3[ds3Id + offsets]!))
            offsets++;
        // Elden Ring upgrades add up to 25 to the ID; give every offset a row so no upgrade level finds nothing.
        for (int k = 0; k < _rows; k++)
        {
            var from = _ds3[ds3Id + Math.Min(k, offsets - 1)]!;
            var row = kind is not null ? StatusRow(baseId + k, from, kind, k) : ValuesRow(baseId + k, from);
            _er.Rows.Add(row);
        }
        _er.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        Note(ds3Id, source.Name, baseId, kind is not null ? "status" : HasValues(source) || ErBase(source.Name) > 0 ? "values" : "visuals",
            kind is not null ? $"{kind} {Int(source, status[0].Ds3)} (copy of Elden Ring {_statusTemplates[kind]}), DS3 upgrade rows {offsets}"
                : $"DS3 upgrade rows {offsets}{(_erBased.Contains(baseId) ? $", based on Elden Ring {ErBase(source.Name)}" : "")}");
        // Chains point at their own ported rows (after the block is taken, so a loop ends).
        foreach (var k in Enumerable.Range(0, _rows))
        {
            var row = _er[baseId + k]!;
            var from = _ds3[ds3Id + Math.Min(k, offsets - 1)]!;
            foreach (var (ds3Field, erField) in ChainFields)
                if (row[erField] is { } cell && !(_erBased.Contains(row.ID) && Int(from, ds3Field) <= 0)) // an Elden Ring-based buff keeps its own chain
                    Set(cell, kind is not null && erField == "replaceSpEffectId" ? -1 : Port(Int(from, ds3Field)));
        }
        return baseId;
    }

    private PARAM.Row StatusRow(int id, PARAM.Row ds3, string kind, int k)
    {
        int template = _statusTemplates[kind];
        var from = _er[template + k] is { } up && StatusOf(up) == kind ? up : _er[template]!;
        var row = Copy(from, id, ds3.Name);
        foreach (var field in ErStatusFields)
            if (row[field] is { } cell)
                Set(cell, 0);
        int value = StatusFields.Where(f => f.Er == kind).Sum(f => Int(ds3, f.Ds3));
        Set(row[kind]!, value);
        // Elden Ring's chain from its template row would start the template's own follow-up effects.
        foreach (var (_, erField) in ChainFields)
            if (row[erField] is { } cell)
                Set(cell, -1);
        return row;
    }

    // DS3 weapon art buffs Elden Ring has its own version of (Paramdex names: DS3 120750 "Weapon Art - Perserverence",
    // ER 1650 "[Weapon] Endure - Poise Buff", 1810-1818 "[Weapon] War Cry"). The ported row starts from Elden Ring's,
    // so it has Elden Ring's icon under the health bar, glow and poise (DS3 keeps poise in fields Paramdex doesn't
    // name), with DS3's duration and rates on top. "No FP" is the weaker twin weapon arts cast without enough FP.
    private static readonly (string[] Words, int Er, int ErNoFp)[] ErBuffs =
    [
        (["perserverence", "perseverance", "endure", "stomp", "hold", "lockout"], 1650, 1651),
        (["warcry", "war cry", "rage", "roar"], 1811, 1816),
    ];

    private int ErBase(string? ds3Name)
    {
        var name = (ds3Name ?? "").ToLowerInvariant();
        if (!name.Contains("weapon art") && !name.Contains("skill"))
            return -1;
        bool noFp = name.Contains("lack of fp") || name.Contains("no fp") || name.Contains("insufficient");
        foreach (var (words, er, erNoFp) in ErBuffs)
            if (words.Any(name.Contains) && _er[noFp ? erNoFp : er] is not null)
                return noFp ? erNoFp : er;
        return -1;
    }

    private readonly HashSet<int> _erBased = new();

    private PARAM.Row ValuesRow(int id, PARAM.Row ds3)
    {
        int erBase = ErBase(ds3.Name);
        if (erBase > 0)
        {
            // Elden Ring's buff, with DS3's values only where DS3 sets them.
            var based = Copy(_er[erBase]!, id, ds3.Name);
            _erBased.Add(id);
            foreach (var field in SharedFields.Where(f => !f.StartsWith("effectTarget", StringComparison.Ordinal)))
                if (ds3[field]?.Value is { } v and not byte[] && IsSet(field, Convert.ToDouble(v)))
                    CopyField(ds3, field, based, field);
            foreach (var (d, e) in RenamedFields)
                if (ds3[d]?.Value is { } v and not byte[] && IsSet(d, Convert.ToDouble(v)))
                    CopyField(ds3, d, based, e);
            return based;
        }
        var row = new PARAM.Row(id, ds3.Name ?? "", _er.AppliedParamdef);
        // Every new row starts from the values most of Elden Ring's own rows have. A blank row has every damage,
        // defence, poise and sight multiplier at 0: 1.0.353, Friede's art (DS3 143021910 -> 690002800) made the
        // enemies it hit take and deal no damage. Weapon effects did this from #71 until 1.0.353 (only enemy effects
        // started from the usual values). Fallback weapon-effects C (DARKSIGN_SPEFFECT_BLANK=1) keeps blank rows.
        if (_full || !BlankRows)
        {
            _common ??= ParamDefaults.Common(_er, r => r.ID < FirstNewId);
            for (int i = 0; i < row.Cells.Count; i++)
                if (_common[i] is { } usual)
                    row.Cells[i].Value = usual;
            foreach (var (_, e) in ChainFields)
                ParamDefaults.Set(row, e, -1);
        }
        foreach (var field in SharedFields)
            CopyField(ds3, field, row, field);
        foreach (var (d, e) in RenamedFields)
            CopyField(ds3, d, row, e);
        if (_full)
        {
            foreach (var field in FullSharedFields)
                ParamDefaults.Copy(ds3, field, row, field);
            foreach (var (d, e) in FullRenamedFields)
                ParamDefaults.Copy(ds3, d, row, e);
        }
        foreach (var (d, e) in StatusFields)
            if (Int(ds3, d) > 0 && row[e] is { } cell)
                Set(cell, Int(ds3, d));
        // What the effect looks like: DS3's visual rows under new IDs (their effects are filled in by BindVisuals).
        if (Visuals is not null)
        {
            for (int k = 0; k < 8; k++)
                if (ParamDefaults.Cell(row, ErVfxField(k)) is { } vfx)
                    Set(vfx, Visuals.Port(ParamDefaults.Int(ds3, Ds3VfxFields[k])));
            // A weapon enchant is only drawn while the effect is in an enchant state (stateInfo: 28 magic weapon, 62
            // fire, 151 lightning, 152 resin, 158 shield...). The two games share those numbers; a DS3 state is kept
            // only when Elden Ring's own enchants use the same one, so no DS3-only state reaches the game.
            int state = Int(ds3, "stateInfo");
            if (state > 0 && EnchantStates.Contains(state) && Ds3VfxFields.Any(f => Visuals.IsEnchant(ParamDefaults.Int(ds3, f)))
                && ParamDefaults.Cell(row, "stateInfo") is { } stateCell)
                Set(stateCell, state);
        }
        return row;
    }

    private HashSet<int>? _enchantStates;

    /// <summary>The stateInfo values of Elden Ring's own SpEffects whose visual rows draw a weapon enchant.</summary>
    private HashSet<int> EnchantStates
    {
        get
        {
            if (_enchantStates is not null)
                return _enchantStates;
            var enchantRows = _erVfx?.Rows.Where(v => ParamDefaults.Int(v, "effectType") is 1 or 2).Select(v => v.ID).ToHashSet() ?? [];
            return _enchantStates = _er.Rows.Where(r => r.ID < FirstNewId && Enumerable.Range(0, 8).Any(k => enchantRows.Contains(ParamDefaults.Int(r, ErVfxField(k)))))
                .Select(r => Int(r, "stateInfo")).Where(s => s > 0).ToHashSet();
        }
    }

    private readonly PARAM? _erVfx;

    private bool HasVisuals(PARAM.Row ds3) =>
        Visuals is not null && Ds3VfxFields.Any(f => Visuals.Visible(ParamDefaults.Int(ds3, f)));

    /// <summary>
    /// Gives the ported visual rows the Elden Ring IDs of their converted effects. The effect port runs after the
    /// weapons' own effects are ported, so the rows wait for it; a build whose effect port never ran calls this with
    /// an empty result, which leaves every row without an effect rather than with a DS3 number.
    /// </summary>
    public void BindVisuals(FxrPort.Result effects) => Visuals?.Bind(effects);

    public bool VisualsBound => Visuals is null || Visuals.IsBound;

    private bool HasValues(PARAM.Row ds3) =>
        HasSharedValues(ds3) || (_full && ChrVariants.Enabled && Int(ds3, "animIdOffset") > 0) || (_full && (FullRenamedFields.Any(f => ParamDefaults.Cell(ds3, f.Ds3)?.Value is { } v and not byte[] && IsSet(f.Ds3, Convert.ToDouble(v)))
                                           || (ParamDefaults.Cell(ds3, "lifeReductionRate")?.Value is { } drain && Convert.ToDouble(drain) != 0)));

    private static bool HasSharedValues(PARAM.Row ds3)
    {
        foreach (var field in SharedFields.Where(f => !f.StartsWith("effectTarget", StringComparison.Ordinal) && f != "effectEndurance" && f != "motionInterval" && f != "wepParamChange"))
            if (ds3[field]?.Value is { } v && IsSet(field, Convert.ToDouble(v)))
                return true;
        foreach (var (d, _) in RenamedFields)
            if (ds3[d]?.Value is { } v && IsSet(d, Convert.ToDouble(v)))
                return true;
        foreach (var (d, _) in ChainFields)
            if (Int(ds3, d) > 0)
                return true;
        return false;
    }

    // Rates are neutral at 1 (100 for the ...Rate ints), amounts at 0.
    private static bool IsSet(string field, double value) =>
        field.EndsWith("Rate", StringComparison.Ordinal) || field.EndsWith("Speed", StringComparison.Ordinal)
            ? Math.Abs(value - 1) > 1e-6 && Math.Abs(value - 100) > 1e-6 && value != 0
            : value != 0;

    private static bool SameEffect(PARAM.Row a, PARAM.Row b) =>
        StatusFields.Select(f => Int(a, f.Ds3) > 0).SequenceEqual(StatusFields.Select(f => Int(b, f.Ds3) > 0))
        && Int(a, "stateInfo") == Int(b, "stateInfo");

    private static string? StatusOf(PARAM.Row er)
    {
        var set = ErStatusFields.Where(f => Int(er, f) > 0).ToList();
        return set.Count == 1 ? set[0] : null;
    }

    private int NextBlock()
    {
        while (true)
        {
            int id = FirstNewId + _nextBlock++ * 100;
            if (Enumerable.Range(0, 100).All(k => _er[id + k] is null))
                return id;
        }
    }

    private PARAM.Row Copy(PARAM.Row from, int id, string? name)
    {
        var row = new PARAM.Row(id, name ?? "", _er.AppliedParamdef);
        for (int i = 0; i < row.Cells.Count; i++)
            row.Cells[i].Value = from.Cells[i].Value is byte[] b ? b.Clone() : from.Cells[i].Value;
        return row;
    }

    private static void CopyField(PARAM.Row from, string fromField, PARAM.Row to, string toField)
    {
        if (from[fromField]?.Value is not { } value || to[toField] is not { } cell || cell.Value is null)
            return;
        try
        {
            cell.Value = cell.Value switch
            {
                float => Convert.ToSingle(value),
                double => Convert.ToDouble(value),
                _ => Convert.ChangeType(Math.Round(Convert.ToDouble(value)), cell.Value.GetType()),
            };
        }
        catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException)
        {
            // Out of the Elden Ring field's range: keep the default.
        }
    }

    private static void Set(PARAM.Cell cell, int value)
    {
        try { cell.Value = Convert.ChangeType(value, cell.Value!.GetType()); }
        catch (OverflowException) { }
    }

    internal static int Int(PARAM.Row row, string field) =>
        row[field]?.Value is { } v and not byte[] ? Convert.ToInt32(v) : 0;

    private void Note(int ds3Id, string? name, int erId, string how, string detail) =>
        _report.Add($"{ds3Id}\t{name}\t{erId}\t{how}\t{detail}");

    /// <summary>
    /// Gives each ported weapon (every affinity row) its DS3 base weapon's status on hit and equipped effects, in the
    /// slots its Elden Ring template leaves free, with the passive-effect text Elden Ring shows for that status.
    /// </summary>
    public void ApplyWeapons(IEnumerable<WeaponPlan> plans, PARAM ds3Weapons, PARAM erWeapons)
    {
        // The menu text Elden Ring's own weapons show for each status. A weapon's texts (spEffectMsgId0-2) are a
        // list in menu order, not one text per spEffectBehaviorId slot: the Cold Great Knife has blood in behaviour
        // slot 0 and frost in slot 1, but its texts are 6601 (frost) then 6501 (blood). Pairing slot with slot made
        // frost vote for blood's text (and Vordt's Great Hammer list "Blood loss buildup"), so the vote only counts
        // weapons with a single status and a single text, which pair unambiguously.
        var messageVotes = new Dictionary<string, Dictionary<int, int>>();
        foreach (var r in erWeapons.Rows.Where(r => r.ID < WeaponPort.FirstNewId))
        {
            var kinds = Enumerable.Range(0, 3).Select(s => Int(r, $"spEffectBehaviorId{s}")).Where(id => id > 0)
                .Select(id => _er[id] is { } effect ? StatusOf(effect) : null).Where(k => k is not null).Distinct().ToList();
            var texts = Enumerable.Range(0, 3).Select(s => Int(r, $"spEffectMsgId{s}")).Where(m => m > 0).Distinct().ToList();
            if (kinds.Count != 1 || texts.Count != 1)
                continue;
            if (!messageVotes.TryGetValue(kinds[0]!, out var votes))
                messageVotes[kinds[0]!] = votes = [];
            votes[texts[0]] = votes.GetValueOrDefault(texts[0]) + 1;
        }
        var messages = messageVotes.ToDictionary(v => v.Key, v => v.Value.MaxBy(x => x.Value).Key);
        Console.WriteLine("weapon effects: menu text per status (spEffectMsgId): " + string.Join(", ", messages.OrderBy(m => m.Key).Select(m => $"{m.Key} {m.Value}")));

        int weapons = 0, slots = 0;
        foreach (var plan in plans.Where(p => p.Status == "port"))
        {
            var source = ds3Weapons[plan.Ds3Id]!;
            var onHit = Enumerable.Range(0, 3).Select(s => Int(source, $"spEffectBehaviorId{s}")).Where(id => id > 0)
                .Select(id => (Ds3: id, Er: Port(id))).Where(t => t.Er > 0).ToList();
            var equipped = StatusOnly ? [] : Enumerable.Range(0, 3).Select(s => Int(source, $"residentSpEffectId{s}")).Where(id => id > 0)
                .Select(id => (Ds3: id, Er: Port(id))).Where(t => t.Er > 0).ToList();
            if (onHit.Count + equipped.Count == 0)
                continue;
            weapons++;
            foreach (var row in erWeapons.Rows.Where(r => r.ID >= plan.NewId!.Value && r.ID < plan.NewId.Value + 10000))
            {
                foreach (var (ds3Id, erId) in onHit)
                {
                    int slot = Enumerable.Range(0, 3).FirstOrDefault(s => Int(row, $"spEffectBehaviorId{s}") <= 0, -1);
                    if (slot < 0)
                        break;
                    Set(row[$"spEffectBehaviorId{slot}"]!, erId);
                    // The text goes in the first free text slot (the list is in menu order, see above), if the status
                    // has one; vanilla never uses text 0, so 0 and -1 both count as free.
                    int textSlot = Enumerable.Range(0, 3).FirstOrDefault(s => Int(row, $"spEffectMsgId{s}") <= 0, -1);
                    if (textSlot >= 0 && _er[erId] is { } e && StatusOf(e) is { } kind && messages.TryGetValue(kind, out var msg))
                        Set(row[$"spEffectMsgId{textSlot}"]!, msg);
                    slots++;
                }
                var residentFields = new[] { "residentSpEffectId", "residentSpEffectId1", "residentSpEffectId2" };
                foreach (var (_, erId) in equipped)
                {
                    var field = residentFields.FirstOrDefault(f => row[f] is not null && Int(row, f) <= 0);
                    if (field is null)
                        break;
                    Set(row[field]!, erId);
                    slots++;
                }
            }
        }
        Console.WriteLine($"weapon effects: {weapons} weapons got DS3 effects ({slots} slots over their affinity rows); {Summary}");
    }

    public void WriteReport(PorterContext ctx, string file = "weapon-effects.tsv")
    {
        var path = ctx.WorkPath("reports", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, _report);
    }
}
