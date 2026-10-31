using System.Text.Json;
using SoulsFormats;

namespace DarkSign.Porter;

public static class Commands
{
    /// <summary>Opens every archive of both games and writes a file index per game.</summary>
    public static int Scan(PorterContext ctx)
    {
        foreach (var game in PorterGame.All)
        {
            using var archives = ctx.OpenArchives(game);
            var indexPath = ctx.WorkPath("index", $"{game.ShortName}.tsv");
            using (var w = new StreamWriter(indexPath))
            {
                w.WriteLine("archive\thash\tsize\tpath");
                foreach (var e in archives.Entries.OrderBy(e => e.Path ?? "~").ThenBy(e => e.Hash))
                    w.WriteLine($"{e.Archive}\t{e.Hash:x}\t{e.Header.PaddedFileSize}\t{e.Path}");
            }

            Console.WriteLine($"{game.ShortName}: {archives.Entries.Count} files");
            foreach (var group in archives.Entries.GroupBy(e => e.Archive).OrderBy(g => g.Key))
                Console.WriteLine($"  {group.Key,-14} {group.Count(),7} files, {group.Count(e => e.Path is not null),7} named");
            Console.WriteLine($"  index: {indexPath}");
        }
        return 0;
    }

    /// <summary>Copies files out of a game's archives into Work\extract\&lt;game&gt;, decompressed.</summary>
    public static int Extract(PorterContext ctx, PorterGame game, IReadOnlyList<string> patterns)
    {
        using var archives = ctx.OpenArchives(game);
        var matches = archives.Entries
            .Where(e => e.Path is not null && patterns.Any(p => Matches(e.Path, p)))
            .OrderBy(e => e.Path)
            .ToList();
        foreach (var entry in matches)
        {
            var raw = archives.ReadRaw(entry);
            var bytes = DCX.Is(raw) ? DCX.Decompress(raw) : raw;
            var relative = entry.Path!.TrimStart('/');
            if (relative.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
                relative = relative[..^4];
            var outPath = ctx.WorkPath(["extract", game.ShortName, .. relative.Split('/')]);
            File.WriteAllBytes(outPath, bytes);
            Console.WriteLine(outPath);
        }
        foreach (var p in patterns.Where(p => !matches.Any(e => Matches(e.Path!, p))))
            Console.Error.WriteLine($"not found in the {game.ShortName} archives: {p}");
        Console.WriteLine($"{matches.Count} files extracted.");
        return matches.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Lists what DS3 has to port (weapons, armor, rings, spells, enemies) and what Elden Ring already uses,
    /// and writes it to Work\reports\inventory.json.
    /// </summary>
    public static int Inventory(PorterContext ctx)
    {
        var report = new Dictionary<string, object>();
        foreach (var game in PorterGame.All)
        {
            var store = ctx.LoadParams(game);
            var section = new Dictionary<string, object>();
            foreach (var (label, param, filter) in InventoryParams(game))
            {
                var p = store.Get(param);
                if (p is null)
                    continue;
                var rows = p.Rows.Where(filter).ToList();
                section[label] = new
                {
                    param,
                    count = rows.Count,
                    rows = rows.Select(r => new { id = r.ID, name = r.Name ?? "" }),
                };
                Console.WriteLine($"{game.ShortName} {label,-10} {rows.Count,5}  ({param})");
            }
            report[game.ShortName] = section;
        }

        var path = ctx.WorkPath("reports", "inventory.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"report: {path}");
        return 0;
    }

    // Base items only: in both games a weapon's upgrade levels and infusions are extra rows above the base ID.
    private static IEnumerable<(string Label, string Param, Func<PARAM.Row, bool> Filter)> InventoryParams(PorterGame game)
    {
        yield return ("weapons", "EquipParamWeapon", r => r.ID > 0 && r.ID % 10000 == 0);
        yield return ("armor", "EquipParamProtector", r => r.ID > 0);
        yield return ("rings", "EquipParamAccessory", r => r.ID > 0);
        yield return ("spells", "Magic", r => r.ID > 0);
        yield return ("npcs", "NpcParam", r => r.ID > 0);
    }

    /// <summary>Decides which DS3 weapons to port and how; writes Work\reports\weapons-plan.json.</summary>
    public static List<WeaponPlan> WeaponsPlan(PorterContext ctx)
    {
        var ds3Params = ctx.LoadParams(PorterGame.DarkSouls3).Get("EquipParamWeapon")!;
        var erParams = ctx.LoadParams(PorterGame.EldenRing).Get("EquipParamWeapon")!;
        Dictionary<int, string> ds3Names, erNames;
        using (var a = ctx.OpenArchives(PorterGame.DarkSouls3))
            ds3Names = ItemText.LoadNamesOrRowNames(a, ds3Params);
        using (var a = ctx.OpenArchives(PorterGame.EldenRing))
            erNames = ItemText.LoadNamesOrRowNames(a, erParams);

        var plans = WeaponPort.Plan(ds3Params, erParams, ds3Names, erNames);
        var path = ctx.WorkPath("reports", "weapons-plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plans, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var g in plans.GroupBy(p => p.Status.Split(':')[0] == "port" ? "port" : p.Status))
            Console.WriteLine($"{g.Count(),4}  {g.Key}");
        foreach (var g in plans.Where(p => p.Status == "port").GroupBy(p => p.TemplateName))
            Console.WriteLine($"      as {g.Key}: {string.Join(", ", g.Select(p => p.Name))}");
        Console.WriteLine($"plan: {path}");
        return plans;
    }

    /// <summary>
    /// Builds the weapon part of the Elden Ring mod: new EquipParamWeapon rows in Mods\ER\regulation.bin.
    /// Every run starts again from the game's own regulation, so it can be re-run safely.
    /// </summary>
    public static int PortWeapons(PorterContext ctx)
    {
        using var total = Timing.Stage("port-weapons total");
        var plans = WeaponsPlan(ctx);
        var paramsTime = Timing.Stage("params");
        var ds3Store = ctx.LoadParams(PorterGame.DarkSouls3);
        var ds3 = ds3Store.Get("EquipParamWeapon")!;
        var erStore = ctx.LoadParams(PorterGame.EldenRing);
        var erWeapons = erStore.Get("EquipParamWeapon")!;
        var added = WeaponPort.Apply(plans, ds3, erWeapons);
        paramsTime.Dispose();
        Console.WriteLine($"added {added} weapon rows ({plans.Count(p => p.Status == "port")} weapons with their affinities)");

        using var erArchives = ctx.OpenArchives(PorterGame.EldenRing);
        using var ds3Archives = ctx.OpenArchives(PorterGame.DarkSouls3);
        Dictionary<int, string> newNames;
        using (Timing.Stage("text and shop"))
        {
            newNames = WeaponText.Write(ctx, erArchives, ds3Archives, plans, erWeapons);
            ShopPort.Write(ctx, erArchives, erStore.Get("ShopLineupParam")!, ds3, plans);
        }
        using (Timing.Stage("models"))
            PortWeaponModels(ctx, erArchives, ds3Archives, plans, ds3, erWeapons);
        using (Timing.Stage("icons"))
            IconPort.Write(ctx, erArchives, ds3Archives, plans.Where(p => p.Status == "port")
                .Select(p => new IconPort.Entry(Convert.ToInt32(ds3[p.Ds3Id]!["iconId"].Value), WeaponPort.NewIconId(p))).ToList());
        try
        {
            using var _ = Timing.Stage("movesets, effects and sounds");
            MovesetPort.Apply(ctx, erArchives, ds3Archives, erStore, ds3Store, plans);
        }
        catch (Exception e)
        {
            // Weapons keep their template movesets; the rest of the content still builds.
            Console.WriteLine($"moveset: failed, weapons keep Elden Ring movesets ({e.GetType().Name}: {e.Message})");
            Console.WriteLine(e.StackTrace);
            foreach (var file in MovesetPort.OutputFiles(ctx).Where(File.Exists))
                File.Delete(file);
        }

        var path = ctx.ModPath("regulation.bin");
        using (Timing.Stage("regulation and item list"))
        {
            erStore.Save(path);
            ItemList.Write(ctx, erStore, erArchives, newNames);
        }
        File.WriteAllText(ctx.ModPath("README.txt"), ModReadme);
        Console.WriteLine($"mod: {Path.GetDirectoryName(path)}");
        return 0;
    }

    private const string ModReadme = """
        The Dark Sign: Elden Ring content mod (generated, do not hand-edit)

        Built by `DarkSignPorter port-weapons` from the Dark Souls III and Elden Ring game files on this PC.
        Re-running it rebuilds everything here from scratch; nothing in the game folders is changed.
        ModEngine2 loads this folder over the game (TheDarkSign\Tools\ModEngine2, config_darksign_er.toml).

          regulation.bin                     Elden Ring params plus the ported DS3 weapons (IDs 80000000+)
                                             and a new shop lineup (130000+) for Merchant Kalé
          msg\engus\item_dlc02.msgbnd.dcx    names and descriptions of the ported weapons
          script\talk\*.talkesdbnd.dcx       Kalé's talk scripts, opening the new shop lineup
          parts\wp_a_9NNN(_l).partsbnd.dcx    ported weapon models (and low-detail versions)
          material\allmaterial_dlc02...       materials for the ported models
          menu\hi|low\01_common.*, 00_solo.*  the ported weapons' DS3 icons (sheet SB_Icon_DS3, IDs 41000+)
          darksign_items.tsv                 every item the F1 test menu can give (not a game file)
        """;

    /// <summary>Writes the test menu's item list from Elden Ring alone, for when no content has been ported.</summary>
    public static int ItemListOnly(PorterContext ctx)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        ItemList.Write(ctx, ctx.LoadParams(PorterGame.EldenRing), er, new Dictionary<int, string>());
        return 0;
    }

    private static void PortWeaponModels(PorterContext ctx, GameArchives er, GameArchives ds3, List<WeaponPlan> plans,
        PARAM ds3Weapons, PARAM erWeapons)
    {
        var materials = new List<ModelPort.NewMaterial>();
        int converted = 0;
        var failed = new List<string>();
        foreach (var plan in plans.Where(p => p.Status == "port"))
        {
            int ds3Model = Convert.ToInt32(ds3Weapons[plan.Ds3Id]!["equipModelId"].Value);
            int templateModel = Convert.ToInt32(erWeapons[plan.TemplateId!.Value]!["equipModelId"].Value);
            try
            {
                foreach (var lod in new[] { false, true })
                {
                    var model = ModelPort.Convert(ds3, er, ds3Model, templateModel, plan.NewModelId!.Value, lod);
                    if (model is null)
                        continue;
                    var path = ctx.ModPath(["parts", ModelPort.PartsPath(plan.NewModelId.Value, lod)[7..]]);
                    File.WriteAllBytes(path, DCX.Compress(model.Binder.Write(), model.Compression));
                    materials.AddRange(model.Materials);
                    if (!lod)
                        converted++;
                }
            }
            catch (Exception ex)
            {
                // SoulsFormats' writer can mask the real error with "Not all reservations filled"; report the root.
                var root = ex;
                while (root.InnerException is not null)
                    root = root.InnerException;
                failed.Add($"{plan.Name} (wp_a_{ds3Model:D4}): {root.GetType().Name}: {root.Message}");
            }
        }
        ModelPort.WriteMaterials(ctx, er, materials);
        Console.WriteLine($"models: {converted} converted, {failed.Count} failed");
        foreach (var f in failed)
            Console.WriteLine($"  failed: {f}");
    }

    /// <summary>Prints chosen fields of a param as TSV (all rows, or rows whose ID is in [min, max]).</summary>
    public static int DumpParam(PorterContext ctx, PorterGame game, string paramName, IReadOnlyList<string> fields)
    {
        var param = ctx.LoadParams(game).Get(paramName)
            ?? throw new ArgumentException($"{game.ShortName} has no param {paramName}.");
        var columns = fields.Count > 0 ? fields : param.AppliedParamdef.Fields.Select(f => f.InternalName).ToList();
        Console.WriteLine("id\tname\t" + string.Join('\t', columns));
        foreach (var row in param.Rows)
            Console.WriteLine($"{row.ID}\t{row.Name}\t" + string.Join('\t', columns.Select(c => row[c]?.Value?.ToString() ?? "")));
        return 0;
    }

    private static bool Matches(string path, string pattern)
    {
        var p = pattern.Replace('\\', '/');
        if (!p.StartsWith('/'))
            p = "/" + p;
        if (p.EndsWith('*'))
            return path.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(path, p, StringComparison.OrdinalIgnoreCase);
    }
}
