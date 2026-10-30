using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// The list of every item the F1 test menu (TestMenu/, dksign_testmenu.dll) can give: Elden Ring's own items plus
/// the ported ones, as "hex ID, kind, max upgrade, infusable, name" lines in Mods\ER\darksign_items.tsv.
/// </summary>
public static class ItemList
{
    public const string FileName = "darksign_items.tsv";

    private static readonly (string Param, string Text, string Kind, uint Category)[] Kinds =
    [
        ("EquipParamWeapon", "WeaponName", "Weapon", 0x00000000),
        ("EquipParamProtector", "ProtectorName", "Armor", 0x10000000),
        ("EquipParamAccessory", "AccessoryName", "Talisman", 0x20000000),
        ("EquipParamGoods", "GoodsName", "Item", 0x40000000),
        ("EquipParamGem", "GemName", "AshOfWar", 0x80000000),
    ];

    public static void Write(PorterContext ctx, ParamStore er, GameArchives erArchives, IReadOnlyDictionary<int, string> newWeaponNames)
    {
        var reinforce = er.Get("ReinforceParamWeapon")!.Rows.Select(r => r.ID).ToHashSet();
        var lines = new List<string>();
        foreach (var (paramName, textKind, kind, category) in Kinds)
        {
            var param = er.Get(paramName)!;
            var names = ItemText.Load(erArchives, textKind);
            if (kind == "Weapon")
                foreach (var (id, name) in newWeaponNames)
                    names[id] = name;
            var ids = param.Rows.Select(r => r.ID).ToHashSet();
            var before = lines.Count;
            foreach (var row in param.Rows)
            {
                // Weapons are listed once per base weapon; the menu adds affinity (+100s) and upgrade level.
                if (kind == "Weapon" && row.ID % 10000 != 0)
                    continue;
                if (!names.TryGetValue(row.ID, out var name) || !IsRealName(name))
                    continue;
                int maxUpgrade = 0;
                bool infusable = false;
                if (kind == "Weapon")
                {
                    int reinforceType = Convert.ToInt32(row["reinforceTypeId"].Value);
                    while (maxUpgrade < 25 && reinforce.Contains(reinforceType + maxUpgrade + 1))
                        maxUpgrade++;
                    infusable = ids.Contains(row.ID + 100);
                }
                lines.Add($"{(uint)row.ID | category:X}\t{kind}\t{maxUpgrade}\t{(infusable ? 1 : 0)}\t{Clean(name)}");
            }
            Console.WriteLine($"item list: {lines.Count - before} {kind}");
        }
        var path = ctx.ModPath(FileName);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Console.WriteLine($"item list: {lines.Count} items -> {path}");
    }

    private static bool IsRealName(string name) =>
        !string.IsNullOrWhiteSpace(name) && !name.Contains("%null%") && !name.StartsWith("[ERROR]", StringComparison.Ordinal);

    private static string Clean(string name) => name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
