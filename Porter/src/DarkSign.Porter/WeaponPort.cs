using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>One DS3 weapon and what the port does with it.</summary>
public sealed record WeaponPlan(
    int Ds3Id, string Name, int MotionCategory, int? TemplateId, string? TemplateName, int? NewId, int? NewModelId,
    string Status);

/// <summary>
/// Ports DS3 weapons and shields into Elden Ring's EquipParamWeapon. Each DS3 weapon becomes a copy of the Elden
/// Ring weapon of the same moveset class (its template), with DS3's damage, scaling, requirements, weight and
/// guarding carried over, and the template's affinity rows (Heavy, Keen, ...) scaled to match.
/// </summary>
public static class WeaponPort
{
    // New weapons use base IDs from 80,000,000 up in steps of 10,000; Elden Ring's own stop below 70,000,000.
    public const int FirstNewId = 80_000_000;
    // New weapon models are wp_a_9000 and up; Elden Ring's weapon models stop well below that.
    public const int FirstNewModelId = 9000;
    // Ported icons are 41000 and up; Elden Ring's own icon IDs stop at 40824.
    public const int FirstNewIconId = 41000;

    public static int NewIconId(WeaponPlan plan) => FirstNewIconId + plan.NewModelId!.Value - FirstNewModelId;

    // DS3 motion categories Elden Ring files under a different number.
    private static readonly Dictionary<int, int> MotionRemap = new() { [49] = 21 /* torch */ };

    // DS3 field -> Elden Ring field, for stats that carry over as-is (same meaning, same units).
    private static readonly (string Ds3, string Er)[] StatFields =
    [
        ("atkBasePhysics", "attackBasePhysics"), ("atkBaseMagic", "attackBaseMagic"), ("atkBaseFire", "attackBaseFire"),
        ("atkBaseThunder", "attackBaseThunder"), ("atkBaseDark", "attackBaseDark"), ("atkBaseStamina", "attackBaseStamina"),
        ("correctStrength", "correctStrength"), ("correctAgility", "correctAgility"), ("corretMagic", "correctMagic"),
        ("corretFaith", "correctFaith"), ("correctLuck", "correctLuck"),
        ("properStrength", "properStrength"), ("properAgility", "properAgility"), ("properMagic", "properMagic"),
        ("properFaith", "properFaith"),
        ("weight", "weight"),
        ("physGuardCutRate", "physGuardCutRate"), ("magGuardCutRate", "magGuardCutRate"),
        ("fireGuardCutRate", "fireGuardCutRate"), ("thunGuardCutRate", "thunGuardCutRate"),
        ("darkGuardCutRate", "darkGuardCutRate"), ("staminaGuardDef", "staminaGuardDef"),
        ("attackBaseParry", "attackBaseParry"), ("attackBaseRepel", "attackBaseRepel"),
        ("guardBaseRepel", "guardBaseRepel"), ("defenseBaseParry", "defenseBaseParry"),
        ("isNormalAttackType", "isNormalAttackType"), ("isBlowAttackType", "isBlowAttackType"),
        ("isSlashAttackType", "isSlashAttackType"), ("isThrustAttackType", "isThrustAttackType"),
    ];

    // Attack and scaling stats differ per affinity; these are scaled by DS3 / template rather than copied.
    private static readonly HashSet<string> ScaledFields =
    [
        "attackBasePhysics", "attackBaseMagic", "attackBaseFire", "attackBaseThunder", "attackBaseDark",
        "correctStrength", "correctAgility", "correctMagic", "correctFaith", "correctLuck",
    ];

    public static List<WeaponPlan> Plan(PARAM ds3, PARAM er, IReadOnlyDictionary<int, string> ds3Names,
        IReadOnlyDictionary<int, string> erNames)
    {
        var erTaken = erNames.Values.Select(ItemText.Normalize).ToHashSet();
        var templates = ChooseTemplates(er, erNames);
        var plans = new List<WeaponPlan>();
        int next = 0;

        foreach (var row in ds3.Rows.Where(r => r.ID > 0 && r.ID % 10000 == 0).OrderBy(r => r.ID))
        {
            int motion = Convert.ToInt32(row["wepmotionCategory"].Value);
            if (!ds3Names.TryGetValue(row.ID, out var name))
            {
                plans.Add(new(row.ID, row.Name ?? "", motion, null, null, null, null, "skip: no in-game name (unused)"));
                continue;
            }
            if (row.ID == 110000) // Fists: bare hands, which Elden Ring has as "Unarmed"
            {
                plans.Add(new(row.ID, name, motion, null, null, null, null, "skip: Elden Ring already has it"));
                continue;
            }
            if (motion == 0)
            {
                plans.Add(new(row.ID, name, motion, null, null, null, null, "skip: ammunition"));
                continue;
            }
            if (erTaken.Contains(ItemText.Normalize(name)))
            {
                plans.Add(new(row.ID, name, motion, null, null, null, null, "skip: Elden Ring already has it"));
                continue;
            }

            var erMotion = MotionRemap.GetValueOrDefault(motion, motion);
            var template = PickTemplate(templates, erMotion, row);
            if (template is null)
            {
                plans.Add(new(row.ID, name, motion, null, null, null, null, $"skip: no Elden Ring weapon class for motion {motion}"));
                continue;
            }
            plans.Add(new(row.ID, name, motion, template.ID, erNames.GetValueOrDefault(template.ID, template.Name),
                FirstNewId + next * 10000, FirstNewModelId + next, "port"));
            next++;
        }
        return plans;
    }

    /// <summary>Adds the planned weapons (and their affinity variants) to Elden Ring's EquipParamWeapon.</summary>
    public static int Apply(IEnumerable<WeaponPlan> plans, PARAM ds3, PARAM er)
    {
        int added = 0;
        var erById = er.Rows.ToDictionary(r => r.ID);
        foreach (var plan in plans.Where(p => p.Status == "port"))
        {
            var source = ds3[plan.Ds3Id]!;
            var templateBase = erById[plan.TemplateId!.Value];
            for (int offset = 0; offset < 10000; offset += 100)
            {
                if (!erById.TryGetValue(templateBase.ID + offset, out var templateRow))
                    continue;
                var row = new PARAM.Row(plan.NewId!.Value + offset, "", er.AppliedParamdef);
                for (int i = 0; i < row.Cells.Count; i++)
                    row.Cells[i].Value = templateRow.Cells[i].Value;

                foreach (var (ds3Field, erField) in StatFields)
                {
                    var value = Convert.ToDouble(source[ds3Field].Value);
                    if (offset != 0 && ScaledFields.Contains(erField))
                    {
                        var baseValue = Convert.ToDouble(templateBase[erField].Value);
                        var affinityValue = Convert.ToDouble(templateRow[erField].Value);
                        value = baseValue > 0 ? value * affinityValue / baseValue : affinityValue;
                    }
                    Set(row, erField, value);
                }

                row["equipModelId"].Value = (ushort)plan.NewModelId!.Value;
                Set(row, "iconId", NewIconId(plan));
                row["sortId"].Value = 9_000_000 + plan.NewModelId.Value - FirstNewModelId;
                row["originEquipWep"].Value = plan.NewId.Value; // upgrades and affinities stay within the new weapon
                Set(row, "sellValue", Convert.ToDouble(source["sellValue"].Value));
                er.Rows.Add(row);
                added++;
            }
        }
        er.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return added;
    }

    // Stats are stored as different numeric types in the two games; convert to whatever the Elden Ring field holds.
    private static void Set(PARAM.Row row, string field, double value)
    {
        var cell = row[field];
        cell.Value = cell.Value switch
        {
            float => (float)value,
            byte => (byte)Math.Clamp(Math.Round(value), byte.MinValue, byte.MaxValue),
            sbyte => (sbyte)Math.Clamp(Math.Round(value), sbyte.MinValue, sbyte.MaxValue),
            ushort => (ushort)Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue),
            short => (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue),
            uint => (uint)Math.Clamp(Math.Round(value), uint.MinValue, uint.MaxValue),
            int => (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue),
            _ => throw new InvalidDataException($"Unexpected type {cell.Value?.GetType()} for {field}"),
        };
    }

    /// <summary>
    /// For each Elden Ring motion category, the candidate templates: ordinary player weapons that have an
    /// in-game name, a full set of affinity rows, and no unique skill or effects of their own.
    /// </summary>
    private static Dictionary<int, List<PARAM.Row>> ChooseTemplates(PARAM er, IReadOnlyDictionary<int, string> erNames)
    {
        var ids = er.Rows.Select(r => r.ID).ToHashSet();
        return er.Rows
            .Where(r => r.ID > 0 && r.ID % 10000 == 0 && erNames.ContainsKey(r.ID) && !erNames[r.ID].StartsWith('['))
            .Where(r => Convert.ToInt32(r["residentSpEffectId"].Value) <= 0)
            .GroupBy(r => Convert.ToInt32(r["wepmotionCategory"].Value))
            .ToDictionary(g => g.Key, g =>
            {
                // A motion class can hold odd members (a pickaxe among greataxes), so prefer the class's most
                // common weapon type, then infusable weapons, then the lowest ID.
                var commonType = g.GroupBy(r => r["wepType"].Value).OrderByDescending(t => t.Count()).First().Key;
                return g.OrderByDescending(r => Equals(r["wepType"].Value, commonType))
                    .ThenByDescending(r => ids.Contains(r.ID + 100))
                    .ThenBy(r => r.ID)
                    .ToList();
            });
    }

    private static PARAM.Row? PickTemplate(Dictionary<int, List<PARAM.Row>> templates, int erMotion, PARAM.Row ds3Row)
    {
        if (!templates.TryGetValue(erMotion, out var candidates))
            return null;
        // Catalysts share one motion class; keep sorcery tools on staves and miracle/pyromancy tools on seals.
        if (erMotion == 41)
        {
            bool sorcery = Convert.ToBoolean(ds3Row["enableMagic"].Value);
            return candidates.FirstOrDefault(r => Convert.ToBoolean(r["enableMagic"].Value) == sorcery) ?? candidates[0];
        }
        // DS3 files small and medium shields together; light ones go to Elden Ring's small-shield class.
        if (erMotion == 48 && Convert.ToSingle(ds3Row["weight"].Value) >= 3.0f && templates.TryGetValue(49, out var medium))
            return medium[0];
        return candidates[0];
    }
}
