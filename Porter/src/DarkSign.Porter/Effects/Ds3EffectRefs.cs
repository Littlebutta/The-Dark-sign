using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Follows DS3's own params from a weapon to every visual effect (FXR) it can show outside its moves' effect events:
/// SpEffects (worn, on hit, cast by a move or a behaviour, chained from another effect) to their visual rows, and
/// behaviours to bullets to the bullets those turn into, their attacks and their SpEffects. Read-only; it is how the
/// build knows which DS3 effects to convert before any row is ported, and how the coverage report counts what each
/// weapon has.
/// </summary>
public sealed class Ds3EffectRefs
{
    private readonly PARAM? _spEffects, _vfx, _bullets, _attacks;
    private readonly ILookup<int, PARAM.Row> _behaviors;

    public HashSet<int> SpEffects { get; } = [];
    public HashSet<int> Bullets { get; } = [];
    private readonly HashSet<int> _seenAttacks = [];
    /// <summary>FXR IDs shown by SpEffects (their visual rows).</summary>
    public HashSet<int> VisualFxr { get; } = [];
    /// <summary>FXR IDs shown by bullets (flight, hit, deflect).</summary>
    public HashSet<int> BulletFxr { get; } = [];

    /// <summary>FXR IDs a weapon plays on its own model while held (EquipParamWeapon's resident effects).</summary>
    public HashSet<int> ResidentFxr { get; } = [];

    public IEnumerable<int> Fxr => VisualFxr.Concat(BulletFxr).Concat(ResidentFxr).Distinct();

    /// <summary>
    /// A DS3 weapon's resident effects: up to four (effect, dummy poly on the weapon model) pairs. Paramdex's DS3 def
    /// calls the eight fields weaponVfx0-7; they are Elden Ring's residentSfxId_1-4 then residentSfx_DmyId_1-4
    /// (Friede's Great Scythe: 303151 four times, then dummy polys 300 301 302 303).
    /// </summary>
    public static IEnumerable<(int Slot, int Fxr, int Dummy)> Resident(PARAM.Row ds3Weapon) =>
        Enumerable.Range(0, 4).Select(k => (k + 1, ParamDefaults.Int(ds3Weapon, $"weaponVfx{k}"), ParamDefaults.Int(ds3Weapon, $"weaponVfx{k + 4}")))
            .Where(t => t.Item2 > 0);

    public Ds3EffectRefs(ParamStore ds3, string behaviorParam = "BehaviorParam_PC", string attackParam = "AtkParam_Pc")
        : this(ds3.Get("SpEffectParam"), ds3.Get("SpEffectVfxParam"), ds3.Get("Bullet"), ds3.Get(attackParam), Behaviors(ds3.Get(behaviorParam)))
    {
    }

    private Ds3EffectRefs(PARAM? spEffects, PARAM? vfx, PARAM? bullets, PARAM? attacks, ILookup<int, PARAM.Row> behaviors) =>
        (_spEffects, _vfx, _bullets, _attacks, _behaviors) = (spEffects, vfx, bullets, attacks, behaviors);

    private static ILookup<int, PARAM.Row> Behaviors(PARAM? param) =>
        (param?.Rows ?? []).ToLookup(r => ParamDefaults.Int(r, "variationId"));

    /// <summary>An empty walker over the same params (they are indexed once and shared).</summary>
    public Ds3EffectRefs Fresh() => new(_spEffects, _vfx, _bullets, _attacks, _behaviors);

    public void AddSpEffect(int id)
    {
        if (id <= 0 || _spEffects?[id] is not { } row || !SpEffects.Add(id))
            return;
        Visit(row);
        // The rows a weapon's upgrade levels add to the ID (SpEffectPort ports the same block).
        for (int k = 1; k < 26 && _spEffects[id + k] is { } level; k++)
            if (SpEffects.Add(id + k))
                Visit(level);
    }

    private void Visit(PARAM.Row spEffect)
    {
        for (int k = 0; k < 8; k++)
            if (ParamDefaults.Int(spEffect, $"EffectVfx{k}") is > 0 and var vfx && _vfx?[vfx] is { } visual)
                VisualFxr.UnionWith(VfxPort.Ds3EffectFields.Select(f => ParamDefaults.Int(visual, f)).Where(f => f > 0));
        foreach (var chain in new[] { "replaceSpEffectId", "cycleOccurenceSpEffectId", "atkOccurenceSpEffectId" })
            AddSpEffect(ParamDefaults.Int(spEffect, chain));
    }

    public void AddBullet(int id)
    {
        if (id <= 0 || _bullets?[id] is not { } row || !Bullets.Add(id))
            return;
        BulletFxr.UnionWith(BulletPort.Ds3EffectFields.Select(f => ParamDefaults.Int(row, f)).Where(f => f > 0));
        foreach (var field in BulletPort.Ds3BulletFields)
            AddBullet(ParamDefaults.Int(row, field));
        foreach (var field in BulletPort.Ds3SpEffectFields)
            AddSpEffect(ParamDefaults.Int(row, field));
        AddAttack(ParamDefaults.Int(row, BulletPort.Ds3AttackField));
    }

    public void AddAttack(int id)
    {
        if (id <= 0 || !_seenAttacks.Add(id) || _attacks?[id] is not { } row)
            return;
        for (int i = 0; i < 5; i++)
            AddSpEffect(ParamDefaults.Int(row, $"spEffect{i}"));
    }

    /// <summary>Everything a behaviour variation's rows fire: attacks (0), bullets (1), SpEffects (2).</summary>
    public void AddVariation(int variation)
    {
        if (variation <= 0)
            return;
        foreach (var row in _behaviors[variation])
        {
            int id = ParamDefaults.Int(row, "refId");
            switch (ParamDefaults.Int(row, "refType"))
            {
                case 0: AddAttack(id); break;
                case 1: AddBullet(id); break;
                case 2: AddSpEffect(id); break;
            }
        }
    }

    /// <summary>A weapon's own references: effects worn and on hit, and its behaviour variation.</summary>
    public void AddWeapon(PARAM.Row ds3Weapon)
    {
        for (int s = 0; s < 3; s++)
        {
            AddSpEffect(ParamDefaults.Int(ds3Weapon, $"spEffectBehaviorId{s}"));
            AddSpEffect(ParamDefaults.Int(ds3Weapon, $"residentSpEffectId{s}"));
        }
        AddVariation(ParamDefaults.Int(ds3Weapon, "behaviorVariationId"));
        ResidentFxr.UnionWith(Resident(ds3Weapon).Select(r => r.Fxr));
    }
}
