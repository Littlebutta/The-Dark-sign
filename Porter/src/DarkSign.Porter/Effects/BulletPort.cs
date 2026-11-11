using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Ports DS3 bullets (projectiles) into Elden Ring's Bullet param under new IDs: the Moonlight Greatsword's wave, the
/// frost Friede's scythes throw along the ground, the Dragonslayer Swordspear's lightning, a Crystal Sage's Rapier
/// soul dart. A move fires one through a behaviour row (BehaviorParam_PC refType 1) that names the bullet; DS3's IDs
/// name unrelated Elden Ring bullets (or none), so until now the row was replaced by the template weapon's or left
/// out, and no DS3 weapon threw anything.
/// <para>Elden Ring's bullet struct is DS3's with fields added at the end and a few bits regrouped, under the
/// official field names (Paramdex's DS3 names are community guesses), so the copy goes field by field in struct
/// order: the same field under its Elden Ring name. What a bullet references is ported with it: its effects (FXR,
/// filled in by <see cref="Bind"/> once the effect port has run), its attack (AtkParam_Pc), the SpEffects it gives
/// the shooter and the target, and the bullets it turns into on hit or emits as it flies. Elden Ring-only fields
/// take the value most Elden Ring bullets have.</para>
/// Method (RULE 3): which fields tie a bullet to its effects, attack and follow-up bullets follows sn0wsh00's
/// Elden Ring bullet articles (Nexus Mods article 396) and Paramdex's defs for both games.
/// Fallback "weapon-bullets": DARKSIGN_BULLETS=0 leaves bullet behaviours on the template's row, as before.
/// </summary>
public sealed class BulletPort
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_BULLETS") != "0";

    /// <summary>Fallback "weapon-visuals" F: DS3 bullet events copied byte for byte, as in builds up to 1.0.271.</summary>
    public static bool Ds3Events => Environment.GetEnvironmentVariable("DARKSIGN_BULLET_EVENTS") == "ds3";

    /// <summary>
    /// A DS3 bullet event (TAE type 2) in Elden Ring's layout. Both games start the same: dummy poly, an index, the
    /// behaviour judge, attachment type, enable, state info (DSAnimStudio's TAE templates for both games, RULE 3).
    /// The four bytes at offset 16 differ. Every DS3 player bullet event but seven has FF FF 00 00 there (a short of
    /// -1); Elden Ring's 2,106 never do: its byte 16 is 0, 1 or 2, and its byte 18 is FF (none) in more than half of
    /// them (survey: attack-events). Copied as they were, a ported event carried a value Elden Ring never uses in
    /// byte 16 and 0 where it means "none" with FF, so the four bytes are written the way most of Elden Ring's own are.
    /// </summary>
    internal static byte[] ConvertEvent(byte[] ds3)
    {
        var er = (byte[])ds3.Clone();
        if (Ds3Events || er.Length < 20)
            return er;
        er[16] = 0;
        er[17] = 0;
        er[18] = 0xFF;
        er[19] = 0;
        return er;
    }

    /// <summary>First ID of the ported bullets; Elden Ring's own stop far below.</summary>
    public const int FirstNewId = 69_000_000;

    internal static readonly string[] Ds3EffectFields = ["sfxIdBullet", "sfxIdHit", "sfxIdFlick"];
    internal static readonly string[] Ds3BulletFields = ["hitBulletId", "bulletEmitter_bulletId"];
    internal static readonly string[] Ds3SpEffectFields = ["spEffectIdForShooter", "spEffectId0", "spEffectId1", "spEffectId2", "spEffectId3", "spEffectId4"];
    internal const string Ds3AttackField = "atkBullet_Id";

    // DS3 field -> Elden Ring field at the same place in the struct, where the names differ. Fields with the same
    // name (ignoring case) are copied as they are.
    private static readonly (string Ds3, string Er)[] Renamed =
    [
        ("homingStopRange", "hormingStopRange"), ("homingOffsetRange", "hormingOffsetRange"),
        ("magicDamageDamp", "spelDamageDamp"), ("prevVellocityDirRate", "prevVelocityDirRate"),
        ("IsUseShardHitList", "isUseSharedHitList"), ("IsInheritEffectToChild", "isInheritSfxToChild"),
        ("bulletHitType0", "bulletSfxDeleteType_byHit"), ("bulletHitType1", "bulletSfxDeleteType_byLifeDead"),
        ("SpHomingYAngle", "targetYOffsetRange"), ("rand_ShootAngleXZ", "shootAngleYMaxRandom"), ("rand_ShootANgleY", "shootAngleXMaxRandom"),
        ("bulletEmitter_ShootInterval0", "intervalCreateTimeMin"), ("bulletEmitter_InitInterval", "intervalCreateTimeMax"),
        ("SpHomingYAngleForEnemy", "predictionShootObserveTime"), ("bulletEmitter_ShootInterval1", "intervalCreateWaitTime"),
        ("LaunchType", "sfxPostureType"), ("EmittePosRadius", "randomCreateRadius"), ("subLife", "lifeRandomRange"),
        ("subHomingAngle", "homingAngleX"),
    ];

    // Same name in both games and the same meaning.
    private static readonly string[] Shared =
    [
        "life", "dist", "shootInterval", "gravityInRange", "gravityOutRange", "initVellocity", "accelInRange", "accelOutRange",
        "maxVellocity", "minVellocity", "accelTime", "homingBeginDist", "hitRadius", "hitRadiusMax", "spreadTime", "expDelay",
        "dmgHitRecordLifeTime", "externalForce", "numShoot", "homingAngle", "shootAngle", "shootAngleInterval", "shootAngleXInterval",
        "damageDamp", "fireDamageDamp", "thunderDamageDamp", "staminaDamp", "knockbackDamp", "shootAngleXZ", "lockShootLimitAng",
        "atkAttribute", "spAttribute", "Material_AttackType", "Material_AttackMaterial", "launchConditionType",
        "isAttackSFX", "isEndlessHit", "isPenetrateMap", "isHitBothTeam", "isUseMultiDmyPolyIfPlace", "isHitForceMagic",
        "isIgnoreSfxIfHitWater", "isIgnoreMoveStateIfHitWater", "isHitDarkForceMagic", "isInheritSpeedToChild", "darkDamageDamp",
    ];

    private static readonly (string Ds3, string Er)[] EffectFields =
        [("sfxIdBullet", "sfxId_Bullet"), ("sfxIdHit", "sfxId_Hit"), ("sfxIdFlick", "sfxId_Flick")];

    private readonly PARAM _er, _ds3;
    private readonly object?[] _defaults;
    private readonly SpEffectPort? _spEffects;
    private readonly Func<int, int> _portAttack;
    private readonly Dictionary<int, int> _ported = [];
    private readonly List<(PARAM.Row Row, string Field, int Ds3Fxr)> _pending = [];
    private int _next = FirstNewId;
    private int _bound, _unbound;
    private FxrPort.Result? _effects;

    /// <param name="portAttack">Ports a DS3 AtkParam_Pc row and returns its Elden Ring ID (the DS3 ID when it has no row).</param>
    public BulletPort(ParamStore er, ParamStore ds3, SpEffectPort? spEffects, Func<int, int> portAttack)
        : this(er.Get("Bullet") ?? throw new InvalidDataException("Elden Ring has no Bullet param"),
            ds3.Get("Bullet") ?? throw new InvalidDataException("DS3 has no Bullet param"), spEffects, portAttack)
    {
    }

    internal BulletPort(PARAM er, PARAM ds3, SpEffectPort? spEffects, Func<int, int> portAttack)
    {
        _er = er;
        _ds3 = ds3;
        _defaults = ParamDefaults.Common(_er);
        _spEffects = spEffects;
        _portAttack = portAttack;
    }

    public int Count => _ported.Count(p => p.Value > 0);

    public string Summary => $"{Count} DS3 bullets ported, {_bound} of their effects shown, {_unbound} left empty (effect not converted)";

    /// <summary>The Elden Ring ID for a DS3 bullet, porting it (and what it references) on first use; -1 when DS3 has none.</summary>
    public int Port(int ds3Id)
    {
        if (ds3Id <= 0)
            return -1;
        if (_ported.TryGetValue(ds3Id, out var done))
            return done;
        if (_ds3[ds3Id] is not { } source)
            return _ported[ds3Id] = -1;
        while (_er[_next] is not null)
            _next++;
        var row = new PARAM.Row(_next++, source.Name ?? "", _er.AppliedParamdef);
        // Registered before its references are followed, so bullets that name each other (or themselves) end.
        _ported[ds3Id] = row.ID;
        for (int i = 0; i < row.Cells.Count; i++)
            if (_defaults[i] is { } value)
                row.Cells[i].Value = value;
        foreach (var field in Shared)
            ParamDefaults.Copy(source, Ds3Name(source, field), row, field);
        foreach (var (ds3Field, erField) in Renamed)
            ParamDefaults.Copy(source, ds3Field, row, erField);
        // DS3 keeps these as one bit per field; Elden Ring as a 3-bit number in the same bits.
        ParamDefaults.Set(row, "FollowType", Bits(source, "FollowType0", "FollowType1", "FollowType2"));
        ParamDefaults.Set(row, "EmittePosType", Bits(source, "EmittePosType0", "EmittePosType1", "EmittePosType2"));
        ParamDefaults.Set(row, "attachEffectType", Bits(source, "attachEffectType0", "attachEffectType1"));
        // DS3's one "goes through" byte is Elden Ring's two bits, characters and objects.
        int penetrate = ParamDefaults.Int(source, "isPenetrate") != 0 ? 1 : 0;
        ParamDefaults.Set(row, "isPenetrateChr", penetrate);
        ParamDefaults.Set(row, "isPenetrateObj", penetrate);
        // References to DS3 things Elden Ring numbers differently or doesn't have: AI, objects, limit groups, sounds.
        ParamDefaults.Set(row, "autoSearchNPCThinkID", -1);
        ParamDefaults.Set(row, "assetNo_Hit", -1);
        ParamDefaults.Set(row, "createLimitGroupId", 0);
        foreach (var sound in new[] { "seId_Bullet1", "seId_Bullet2", "seId_Hit", "seId_Flick" })
            ParamDefaults.Set(row, sound, -1);
        ParamDefaults.Set(row, "sfxId_ForceErase", -1);

        foreach (var (ds3Field, erField) in EffectFields)
        {
            ParamDefaults.Set(row, erField, -1);
            if (ParamDefaults.Int(source, ds3Field) is > 0 and var fxr)
                _pending.Add((row, erField, fxr));
        }
        int attack = ParamDefaults.Int(source, Ds3AttackField);
        ParamDefaults.Set(row, "atkId_Bullet", attack > 0 ? _portAttack(attack) : -1);
        ParamDefaults.Set(row, "HitBulletID", Port(ParamDefaults.Int(source, "hitBulletId")));
        ParamDefaults.Set(row, "intervalCreateBulletId", Port(ParamDefaults.Int(source, "bulletEmitter_bulletId")));
        ParamDefaults.Set(row, "spEffectIDForShooter", PortEffect(ParamDefaults.Int(source, "spEffectIdForShooter")));
        for (int i = 0; i < 5; i++)
            ParamDefaults.Set(row, $"spEffectId{i}", PortEffect(ParamDefaults.Int(source, $"spEffectId{i}")));

        _er.Rows.Add(row);
        _er.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        if (_effects is not null)
            BindPending();
        return row.ID;
    }

    private int PortEffect(int ds3Id) => ds3Id > 0 && _spEffects?.Port(ds3Id) is > 0 and var id ? id : -1;

    // Paramdex's DS3 def capitalises some names Elden Ring's doesn't (IsAttackSfx / isAttackSFX).
    private static string Ds3Name(PARAM.Row ds3, string erName) =>
        ds3.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(erName, StringComparison.OrdinalIgnoreCase))?.Def.InternalName ?? erName;

    private static int Bits(PARAM.Row row, params string[] fields) =>
        fields.Select((f, bit) => ParamDefaults.Int(row, f) != 0 ? 1 << bit : 0).Sum();

    /// <summary>Gives every ported bullet (and the ones ported after this) the Elden Ring IDs of its converted effects.</summary>
    public void Bind(FxrPort.Result effects)
    {
        _effects = effects;
        BindPending();
    }

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
}
