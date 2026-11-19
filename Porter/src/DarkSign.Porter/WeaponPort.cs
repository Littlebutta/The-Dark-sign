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
    // Ported icons are 50000 and up. Elden Ring's item sheets stop at 40824, but character creation uses 41000-41603
    // (04_050_chrmake_icongrid.gfx), and icon IDs are 16-bit, so they must stay below 65536.
    public const int FirstNewIconId = 50000;

    public static int NewIconId(WeaponPlan plan) => FirstNewIconId + plan.NewModelId!.Value - FirstNewModelId;

    // DS3 motion categories Elden Ring files under a different number.
    private static readonly Dictionary<int, int> MotionRemap = new() { [49] = 21 /* torch */ };

    // DS3's params file some weapon types together that Elden Ring keeps apart: its reapers share the halberds'
    // category, curved greatswords the curved swords', claws the fists'. DS3's own menus (and its wikis) name the
    // type, so these weapons go to the Elden Ring class of that name instead.
    private static readonly Dictionary<string, int> TypeByName = new(StringComparer.OrdinalIgnoreCase)
    {
        // Reapers -> Elden Ring Reaper (motion 50)
        ["Great Scythe"] = 50, ["Friede's Great Scythe"] = 50, ["Pontiff Knight Great Scythe"] = 50, ["Great Corvian Scythe"] = 50,
        // Curved Greatswords -> Elden Ring Curved Greatsword (motion 40)
        ["Carthus Curved Greatsword"] = 40, ["Exile Greatsword"] = 40, ["Old Wolf Curved Sword"] = 40,
        ["Harald Curved Greatsword"] = 40, ["Murakumo"] = 40,
        // Claws -> Elden Ring Claw (motion 22)
        ["Claw"] = 22, ["Manikin Claws"] = 22, ["Crow Talons"] = 22,
    };

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
        // Never take a base ID block or a model ID Elden Ring (or a later patch / DLC) already uses: a clash would
        // overwrite or duplicate vanilla rows. The slots are skipped in order, so the IDs stay the same on every PC.
        var erBlocks = er.Rows.Select(r => r.ID / 10000).ToHashSet();
        var erModels = er.Rows.Select(r => Convert.ToInt32(r["equipModelId"].Value)).ToHashSet();
        bool Free(int slot) => !erBlocks.Contains((FirstNewId + slot * 10000) / 10000) && !erModels.Contains(FirstNewModelId + slot);

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

            var erMotion = TypeByName.TryGetValue(name.Replace('\u2019', '\''), out var typed) ? typed : MotionRemap.GetValueOrDefault(motion, motion);
            var template = PickTemplate(templates, erMotion, row);
            if (template is null)
            {
                plans.Add(new(row.ID, name, motion, null, null, null, null, $"skip: no Elden Ring weapon class for motion {motion}"));
                continue;
            }
            while (!Free(next))
                next++;
            if (FirstNewIconId + next > ushort.MaxValue)
            {
                plans.Add(new(row.ID, name, motion, null, null, null, null, "skip: no free icon ID left"));
                continue;
            }
            plans.Add(new(row.ID, name, motion, template.ID, erNames.GetValueOrDefault(template.ID, template.Name),
                FirstNewId + next * 10000, FirstNewModelId + next, "port"));
            next++;
        }
        return plans;
    }

    /// <summary>DARKSIGN_DS3_HOLD=0 (fallback "weapon-hold" B): ported weapons stand and walk in their template's hold.</summary>
    public static readonly bool Ds3Hold = Environment.GetEnvironmentVariable("DARKSIGN_DS3_HOLD") != "0";

    /// <summary>The two-handed hold that means "two-handing doesn't change the grip" (shields, staffs, seals): Elden
    /// Ring's scripts read it to tell a weapon's guard from a shield's or a catalyst's, so it is never given or taken.</summary>
    private const int NoTwoHandGrip = 15;

    /// <summary>
    /// The hold a ported weapon stands, walks and runs in: wepmotionOneHandId and wepmotionBothHandId ("stay anim
    /// category", numbered alike in both games: 0 at the side, 2 a big weapon carried low, 3 resting on the shoulder,
    /// 10-16 the two-handed grips; Elden Ring's c0000.hks GetBaseCategory reads it for the idle and move poses).
    /// The row is a copy of its Elden Ring template, and a class's hold differs between the games: Elden Ring carries
    /// every scythe on the shoulder (3), DS3 carries its scythes upright at the side (0) and only its halberds on the
    /// shoulder. A DS3 scythe was therefore carried in a pose its model and its DS3 moves were never made for (Friede's
    /// Great Scythe's "funny walk", 2026-11-19; paired weapons got DS3's holds already, AbsorpPort). DS3's value is
    /// taken when an Elden Ring weapon uses that value too; the two-handed one only between weapon grips.
    /// Returns the DS3 hold to write for each field, or null to keep the template's.
    /// </summary>
    internal static (int? OneHand, int? BothHands) Ds3HoldFor(int ds3One, int ds3Both, int templateOne, int templateBoth, IReadOnlySet<int> erHolds)
    {
        int? one = ds3One != templateOne && erHolds.Contains(ds3One) ? ds3One : null;
        int? both = ds3Both != templateBoth && erHolds.Contains(ds3Both) && ds3Both >= 10 && ds3Both != NoTwoHandGrip && templateBoth != NoTwoHandGrip
            ? ds3Both : null;
        return (one, both);
    }

    /// <summary>Adds the planned weapons (and their affinity variants) to Elden Ring's EquipParamWeapon.</summary>
    public static int Apply(IEnumerable<WeaponPlan> plans, PARAM ds3, PARAM er)
    {
        int added = 0;
        var erById = er.Rows.ToDictionary(r => r.ID);
        // Holds Elden Ring's own weapons use (before the ported rows are added).
        var erHolds = er.Rows.SelectMany(r => new[] { r["wepmotionOneHandId"], r["wepmotionBothHandId"] })
            .Where(c => c is not null).Select(c => Convert.ToInt32(c!.Value)).ToHashSet();
        var holdNotes = new List<string>();
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

                // References to the template's own rows (originEquipWep..originEquipWep25: the weapon an affinity or
                // reinforcement reverts to) follow the copy, else removing an affinity handed out the template weapon.
                foreach (var cell in row.Cells.Where(c => c.Def.InternalName.StartsWith("originEquipWep", StringComparison.Ordinal)))
                    if (cell.Value is int origin && origin >= templateBase.ID && origin < templateBase.ID + 10000)
                        cell.Value = plan.NewId!.Value + (origin - templateBase.ID);
                // DS3 has no arcane requirement; the template's would otherwise stay.
                if (row["properLuck"] is { } luck)
                    luck.Value = Convert.ChangeType(0, luck.Value!.GetType());
                // Status effects built into the template (a scythe's bleed) are Elden Ring's weapon, not the DS3 one:
                // a slot holding the same effect as the template's standard row is dropped; an affinity's own effect
                // (Blood, Cold, ...) differs from it and stays.
                foreach (var slot in new[] { "spEffectBehaviorId0", "spEffectBehaviorId1", "spEffectBehaviorId2" })
                    if (row[slot] is { } effect && Convert.ToInt32(templateBase[slot].Value) > 0
                        && Convert.ToInt32(effect.Value) == Convert.ToInt32(templateBase[slot].Value))
                        effect.Value = Convert.ChangeType(-1, effect.Value!.GetType());

                if (Ds3Hold && row["wepmotionOneHandId"] is { } oneHand && row["wepmotionBothHandId"] is { } bothHands
                    && source["wepmotionOneHandId"] is { } ds3One && source["wepmotionBothHandId"] is { } ds3Both)
                {
                    int templateOne = Convert.ToInt32(oneHand.Value), templateBoth = Convert.ToInt32(bothHands.Value);
                    var (one, both) = Ds3HoldFor(Convert.ToInt32(ds3One.Value), Convert.ToInt32(ds3Both.Value), templateOne, templateBoth, erHolds);
                    if (one is { } o)
                        oneHand.Value = Convert.ChangeType(o, oneHand.Value!.GetType());
                    if (both is { } b)
                        bothHands.Value = Convert.ChangeType(b, bothHands.Value!.GetType());
                    if (offset == 0 && (one ?? both) is not null)
                        holdNotes.Add($"{plan.Name} {templateOne}/{templateBoth} -> {one ?? templateOne}/{both ?? templateBoth}");
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
        Console.WriteLine(Ds3Hold
            ? $"weapons: {holdNotes.Count} weapons stand and walk in DS3's hold instead of their template's (one-handed/two-handed): {string.Join("; ", holdNotes)} [DARKSIGN_DS3_HOLD=0 keeps the template's]"
            : "weapons: holds are the templates' (DARKSIGN_DS3_HOLD=0)");
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
