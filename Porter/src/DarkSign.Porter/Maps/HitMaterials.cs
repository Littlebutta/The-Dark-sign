using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's floor materials in Elden Ring. Every collision triangle names a hit material (fsnp primitive data
/// m_materialNameData: the HitMtrlParam row, plus a multiple of 100 for a variant: 143 is material 43), and both
/// games read it from their own HitMtrlParam: the footstep sound family, what the floor does to whoever stands on
/// it (a SpEffect), and whether a fall onto it hurts. The numbers were copied as they were, but the two tables part
/// ways for the rows that do something (both games' HitMtrlParam dumped 2026-11-22):
/// <list type="bullet">
/// <item>DS3 47 and 49 "No Fall Damage" (SpEffect 4080, the floors under DS3's designed drops) are Elden Ring's 47
/// (forced slow walk, 4101) and 49 "Lake of Rot": a drop DS3 lets the player survive became deadly, or slowed or
/// rotted them. They become a row of our own (<see cref="NoFallDamage"/>) with disableFallDamage set.</item>
/// <item>DS3 48 "No Fall Damage - Poison" (DS3's poison swamp effect 4001, which Elden Ring has as "Poison Swamp")
/// becomes <see cref="NoFallDamagePoison"/>, the same with that effect on contact.</item>
/// <item>DS3 30 "Toxic" (no effect) is Elden Ring's 30 (SpEffect 4200); it becomes 37, which both games have as a
/// toxic floor with no effect of its own.</item>
/// </list>
/// The other rows keep their numbers: footsteps sound by the same families in both (stone, wood, soil, water depths),
/// and the water and poison swamp depths apply the same slow effects (4100, 4101) in both.
/// </summary>
public static class HitMaterials
{
    /// <summary>New HitMtrlParam rows (Elden Ring's end at 62).</summary>
    public const int NoFallDamage = 70, NoFallDamagePoison = 71;

    /// <summary>Elden Ring's row the new ones copy (Rock: a hard floor's footsteps) and DS3's poison floor effect.</summary>
    private const int TemplateRow = 2, PoisonSwampEffect = 4001;

    /// <summary>A DS3 triangle's hit material as Elden Ring's (the variant hundreds kept).</summary>
    public static uint Map(uint ds3)
    {
        uint variant = ds3 / 100 * 100, material = ds3 % 100;
        uint er = material switch
        {
            47 or 49 => NoFallDamage,
            48 => NoFallDamagePoison,
            30 => 37,
            _ => material,
        };
        return variant + er;
    }

    /// <summary>Adds the rows of our own to Elden Ring's HitMtrlParam (ours from an earlier run replaced); returns a log line.</summary>
    public static string AddRows(PARAM hitMtrl)
    {
        hitMtrl.Rows.RemoveAll(r => r.ID is NoFallDamage or NoFallDamagePoison);
        var template = hitMtrl[TemplateRow] ?? throw new InvalidDataException($"HitMtrlParam has no row {TemplateRow}");
        PARAM.Row Copy(int id, string name)
        {
            var row = new PARAM.Row(id, name, hitMtrl.AppliedParamdef);
            for (int i = 0; i < row.Cells.Count; i++)
                row.Cells[i].Value = template.Cells[i].Value is byte[] b ? b.Clone() : template.Cells[i].Value;
            Set(row, "disableFallDamage", 1);
            return row;
        }
        var plain = Copy(NoFallDamage, "[DS3] No Fall Damage");
        var poison = Copy(NoFallDamagePoison, "[DS3] No Fall Damage - Poison");
        Set(poison, "spEffectIdOnHit0", PoisonSwampEffect);
        hitMtrl.Rows.Add(plain);
        hitMtrl.Rows.Add(poison);
        hitMtrl.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return $"hit materials: DS3's no-fall-damage floors (47, 49) as row {NoFallDamage}, its poison one (48) as {NoFallDamagePoison}, its toxic 30 as 37";
    }

    private static void Set(PARAM.Row row, string field, int value)
    {
        if (row.Cells.FirstOrDefault(c => c.Def.InternalName == field) is { Value: { } v } cell)
            cell.Value = Convert.ChangeType(value, v.GetType());
    }
}
