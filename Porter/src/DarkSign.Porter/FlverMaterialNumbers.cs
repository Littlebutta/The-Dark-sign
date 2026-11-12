using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Numbers a FLVER's materials the way Elden Ring's own models do, before the porter writes any Elden Ring model.
/// <para>Elden Ring (FLVER 0x2001A) stores a material number in each material (SoulsFormats calls it
/// <c>Material.Index</c>): the position of the first material in the list that uses the same material file. Vanilla
/// c4340 numbers its 18 materials 0-12, 2, 14-17 (material 13 reuses material 2's BardedWire material), the armour
/// bd_m_1010 gives its four extra Fabric materials 0, and map piece m10_00_00_00_000214 gives its second m10_00_028
/// material 0. Dark Souls III leaves the number 0 in every material, and up to 1.0.383 every ported enemy, weapon,
/// armour piece, object and effect model kept DS3's 0s (only map pieces were renumbered, in PR #117), so to Elden Ring
/// every material of those models claimed to be the first one. Ported enemies drew without their textures and cut-outs
/// until frostbite's wet-look overlay redrew them (Devin's 1.0.383 playtest).</para>
/// Materials share a number only when their material file and every texture entry match, so two materials that
/// differ in any way never share one. Method: read from vanilla Elden Ring and DS3 models on Devin's PC with
/// SoulsFormats (soulsmods/SoulsFormatsNEXT); the rule is this project's reading of those files.
/// </summary>
public static class FlverMaterialNumbers
{
    /// <summary>Whether the porter numbers materials (fallback <c>DARKSIGN_MATERIAL_NUMBERS=ds3</c> keeps DS3's).</summary>
    public static bool Enabled =>
        !string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_MATERIAL_NUMBERS"), "ds3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets every material's number as Elden Ring's models do; returns how many changed.</summary>
    public static int Apply(FLVER2 flver)
    {
        if (!Enabled)
            return 0;
        var first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int changed = 0;
        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var material = flver.Materials[i];
            var key = Key(material);
            if (!first.TryGetValue(key, out int number))
                first[key] = number = i;
            if (material.Index != number)
            {
                material.Index = number;
                changed++;
            }
        }
        return changed;
    }

    private static string Key(FLVER2.Material material) =>
        material.MTD + "|" + material.GXIndex + "|" + string.Join("|", material.Textures.Select(t =>
            $"{t.ParamName}={t.Path}@{t.TilingScale.X},{t.TilingScale.Y}"));
}
