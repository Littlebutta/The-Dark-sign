using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Adds ported weapons' names and descriptions to Elden Ring's English text. They go into the DLC02 item binder's
/// *_dlc02 text files, which ship almost empty, so no existing Elden Ring text is touched.
/// </summary>
public static class WeaponText
{
    private const string Binder = "/msg/engus/item_dlc02.msgbnd.dcx";
    private const int WeaponNameDlc02 = 410, WeaponCaptionDlc02 = 412;

    /// <summary>Writes the text and returns the new names by weapon ID.</summary>
    public static Dictionary<int, string> Write(PorterContext ctx, GameArchives er, GameArchives ds3, IEnumerable<WeaponPlan> plans,
        PARAM erWeapons)
    {
        var erNames = ItemText.Load(er, "WeaponName");
        var ds3Captions = ItemText.Load(ds3, "WeaponCaption");
        var ids = erWeapons.Rows.Select(r => r.ID).ToHashSet();

        var (bytes, compression) = er.ReadWithCompression(Binder);
        var bnd = BND4.Read(bytes);
        var nameFile = bnd.Files.Single(f => f.ID == WeaponNameDlc02);
        var captionFile = bnd.Files.Single(f => f.ID == WeaponCaptionDlc02);
        var names = FMG.Read(nameFile.Bytes);
        var added = new Dictionary<int, string>();
        var captions = FMG.Read(captionFile.Bytes);

        foreach (var plan in plans.Where(p => p.Status == "port"))
        {
            var templateName = erNames.GetValueOrDefault(plan.TemplateId!.Value, plan.TemplateName ?? "");
            ds3Captions.TryGetValue(plan.Ds3Id, out var caption);
            for (int offset = 0; offset < 10000; offset += 100)
            {
                if (!ids.Contains(plan.NewId!.Value + offset))
                    continue;
                // Affinity names follow Elden Ring's own pattern: "Heavy Longsword" -> "Heavy Astora Straight Sword".
                var pattern = erNames.GetValueOrDefault(plan.TemplateId.Value + offset, templateName);
                var name = templateName.Length > 0 && pattern.Contains(templateName)
                    ? pattern.Replace(templateName, plan.Name)
                    : plan.Name;
                names.Entries.Add(new FMG.Entry(plan.NewId.Value + offset, name));
                added[plan.NewId.Value + offset] = name;
                if (caption is not null)
                    captions.Entries.Add(new FMG.Entry(plan.NewId.Value + offset, caption));
            }
        }

        nameFile.Bytes = names.Write();
        captionFile.Bytes = captions.Write();
        File.WriteAllBytes(ctx.ModPath("msg", "engus", "item_dlc02.msgbnd.dcx"), DCX.Compress(bnd.Write(), compression));
        return added;
    }
}
