using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Puts the ported weapons up for sale. Until Greirat himself is ported, Merchant Kalé's main shop carries them:
/// his talk scripts open a new lineup that holds his own goods followed by every ported weapon.
/// </summary>
public static class ShopPort
{
    // Kalé's regular shop (talk scripts t8000xxxxx, command 1:22 = open shop).
    private const int KaleShopStart = 100500, KaleShopEnd = 100524;
    private const int NewShopStart = 130000;
    private const int OpenShopBank = 1, OpenShopCommand = 22;

    public static int Write(PorterContext ctx, GameArchives er, PARAM shop, PARAM ds3Weapons, IEnumerable<WeaponPlan> plans)
    {
        var byId = shop.Rows.ToDictionary(r => r.ID);
        var kale = Enumerable.Range(KaleShopStart, KaleShopEnd - KaleShopStart + 1)
            .Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        var template = kale[0];
        int next = NewShopStart;

        foreach (var original in kale)
            shop.Rows.Add(CopyRow(original, next++, shop));
        foreach (var plan in plans.Where(p => p.Status == "port"))
        {
            var row = CopyRow(template, next++, shop);
            row["equipId"].Value = plan.NewId!.Value;
            row["equipType"].Value = (byte)0; // weapon
            row["value"].Value = Price(ds3Weapons[plan.Ds3Id]!);
            row["sellQuantity"].Value = (short)-1; // unlimited
            row["eventFlag_forStock"].Value = 0u;
            row["eventFlag_forRelease"].Value = 0u;
            shop.Rows.Add(row);
        }
        shop.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        int last = next - 1;

        // Point every Kalé shop-open command at the new lineup.
        int patched = 0;
        foreach (var path in er.Entries.Select(e => e.Path).Where(p => p?.StartsWith("/script/talk/") == true && p.EndsWith(".talkesdbnd.dcx")).OrderBy(p => p))
        {
            var (bytes, compression) = er.ReadWithCompression(path!);
            var bnd = BND4.Read(bytes);
            bool changed = false;
            foreach (var file in bnd.Files.Where(f => ItemText.Leaf(f.Name).StartsWith("t8000", StringComparison.Ordinal)))
            {
                var esd = ESD.Read(file.Bytes);
                bool fileChanged = false;
                foreach (var cmd in Explore.AllCommands(esd))
                {
                    if (cmd.CommandBank == OpenShopBank && cmd.CommandID == OpenShopCommand && cmd.Arguments.Count == 2
                        && EsdExpr.ReadInt(cmd.Arguments[0]) == KaleShopStart && EsdExpr.ReadInt(cmd.Arguments[1]) == KaleShopEnd)
                    {
                        cmd.Arguments[0] = EsdExpr.WriteInt(NewShopStart);
                        cmd.Arguments[1] = EsdExpr.WriteInt(last);
                        fileChanged = true;
                        patched++;
                    }
                }
                if (fileChanged)
                {
                    file.Bytes = esd.Write();
                    changed = true;
                }
            }
            if (changed)
                File.WriteAllBytes(ctx.ModPath(["script", "talk", ItemText.Leaf(path!)]), DCX.Compress(bnd.Write(), compression));
        }
        Console.WriteLine($"shop: {next - NewShopStart} lineup rows ({NewShopStart}-{last}), {patched} Kalé shop commands redirected");
        return patched;
    }

    // DS3 prices are mostly zero (DS3 sells few weapons), so price from the sell value, kept in Elden Ring's range.
    private static int Price(PARAM.Row ds3Weapon)
    {
        var sell = Convert.ToInt32(ds3Weapon["sellValue"].Value);
        return Math.Clamp(sell * 20, 1000, 30000) / 100 * 100;
    }

    private static PARAM.Row CopyRow(PARAM.Row source, int id, PARAM param)
    {
        var row = new PARAM.Row(id, source.Name, param.AppliedParamdef);
        for (int i = 0; i < row.Cells.Count; i++)
            row.Cells[i].Value = source.Cells[i].Value;
        return row;
    }
}
