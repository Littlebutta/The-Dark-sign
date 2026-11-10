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
        using var ds3Archives = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var erArchives = ctx.OpenArchives(PorterGame.EldenRing);
        return WeaponsPlan(ctx, ctx.LoadParams(PorterGame.DarkSouls3), ctx.LoadParams(PorterGame.EldenRing), ds3Archives, erArchives);
    }

    /// <summary>The plan from params and archives already open: port-weapons decrypts and indexes them once, not twice.</summary>
    public static List<WeaponPlan> WeaponsPlan(PorterContext ctx, ParamStore ds3Store, ParamStore erStore, GameArchives ds3Archives, GameArchives erArchives)
    {
        var ds3Params = ds3Store.Get("EquipParamWeapon")!;
        var erParams = erStore.Get("EquipParamWeapon")!;
        var ds3Names = ItemText.LoadNamesOrRowNames(ds3Archives, ds3Params);
        var erNames = ItemText.LoadNamesOrRowNames(erArchives, erParams);

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
        // The regulations (decrypted and parsed) and the archive indexes (RSA-decrypted headers) are loaded once,
        // side by side, and shared by the plan and every stage below; earlier builds loaded each of them twice.
        var openTime = Timing.Stage("game files");
        ParamStore ds3Store = null!, erStore = null!;
        GameArchives erArchives = null!, ds3Archives = null!;
        Parallel.Invoke(
            () => ds3Store = ctx.LoadParams(PorterGame.DarkSouls3),
            () => erStore = ctx.LoadParams(PorterGame.EldenRing),
            () => erArchives = ctx.OpenArchives(PorterGame.EldenRing),
            () => ds3Archives = ctx.OpenArchives(PorterGame.DarkSouls3));
        using var _erArchives = erArchives;
        using var _ds3Archives = ds3Archives;
        openTime.Dispose();
        var plans = WeaponsPlan(ctx, ds3Store, erStore, ds3Archives, erArchives);
        var paramsTime = Timing.Stage("params");
        var ds3 = ds3Store.Get("EquipParamWeapon")!;
        var erWeapons = erStore.Get("EquipParamWeapon")!;
        var added = WeaponPort.Apply(plans, ds3, erWeapons);
        paramsTime.Dispose();
        Console.WriteLine($"added {added} weapon rows ({plans.Count(p => p.Status == "port")} weapons with their affinities)");

        Dictionary<int, string> newNames;
        using (Timing.Stage("text and shop"))
        {
            // Neither may sink the whole build: without them the weapons are still in the regulation and the F1 menu.
            try
            {
                newNames = WeaponText.Write(ctx, erArchives, ds3Archives, plans, erWeapons);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"text: failed, ported weapons have no in-game names ({e.GetType().Name}: {e.Message})");
                newNames = plans.Where(p => p.Status == "port").ToDictionary(p => p.NewId!.Value, p => p.Name);
            }
            try
            {
                ShopPort.Write(ctx, erArchives, erStore.Get("ShopLineupParam")!, ds3, plans);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"shop: failed, ported weapons are not sold ({e.GetType().Name}: {e.Message})");
            }
        }
        // The icons only read the archives and write the menu files, which nothing else touches, so they are built on
        // other cores while the models, model positions and movesets are ported here.
        var iconEntries = plans.Where(p => p.Status == "port")
            .Select(p => new IconPort.Entry(Convert.ToInt32(ds3[p.Ds3Id]!["iconId"].Value), WeaponPort.NewIconId(p))).ToList();
        var icons = Task.Run(() =>
        {
            using var _ = Timing.Stage("icons (side by side)");
            IconPort.Write(ctx, erArchives, ds3Archives, iconEntries);
        });
        if (Parallelism.Serial)
            icons.GetAwaiter().GetResult();
        using (Timing.Stage("models"))
            PortWeaponModels(ctx, erArchives, ds3Archives, plans, ds3, erWeapons);
        try
        {
            using var _ = Timing.Stage("model positions");
            AbsorpPort.Apply(erArchives, ds3Archives, erStore, ds3Store, plans);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine($"absorp: failed, weapons keep their template's model positions ({e.GetType().Name}: {e.Message})");
        }
        // DS3's weapon effects (status on hit, equipped effects): the moveset port below uses the same instance for
        // the effects DS3's weapon arts cast.
        SpEffectPort? spEffects = null;
        if (SpEffectPort.Enabled)
            try
            {
                using var _ = Timing.Stage("weapon effects");
                spEffects = new SpEffectPort(erStore, ds3Store);
                spEffects.ApplyWeapons(plans, ds3, erWeapons);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"weapon effects: failed, ported weapons have no DS3 status or equipped effects ({e.GetType().Name}: {e.Message})");
                spEffects = null;
            }
        try
        {
            using var _ = Timing.Stage("movesets, effects and sounds");
            MovesetPort.Apply(ctx, erArchives, ds3Archives, erStore, ds3Store, plans, spEffects);
        }
        catch (Exception e)
        {
            // Weapons keep their template movesets; the rest of the content still builds.
            Console.WriteLine($"moveset: failed, weapons keep Elden Ring movesets ({e.GetType().Name}: {e.Message})");
            Console.WriteLine(e.StackTrace);
            FxrPort.FinishWrites();
            foreach (var file in MovesetPort.OutputFiles(ctx).Where(File.Exists))
                File.Delete(file);
        }
        // A build whose effect port never ran (the moveset port failed first) leaves the ported visual rows without
        // effects rather than waiting for IDs that never come.
        if (spEffects is { VisualsBound: false })
            spEffects.BindVisuals(new FxrPort.Result());
        using (Timing.Stage("waiting for effects"))
            FxrPort.FinishWrites();

        using (Timing.Stage("waiting for icons"))
        {
            try
            {
                icons.GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Without icons the weapons still work. Nothing of this build reached the mod (the icon files are
                // moved in only once all are written), but an older build's icons may name other weapons, so every
                // icon menu file is removed and the game loads its own menus.
                Console.WriteLine($"icons: failed, ported weapons have no icons ({e.GetType().Name}: {e.Message})");
                foreach (var left in IconPort.RemoveFromMod(ctx))
                    Console.WriteLine($"icons: could not remove {left} (open in another program?); delete it by hand or rebuild with the game closed");
            }
        }

        spEffects?.WriteReport(ctx);
        try
        {
            MovesetPort.WriteArtNames(ctx, erArchives, MovesetPort.ArtNames);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine($"arts: names not written, ported arts show their template's name ({e.GetType().Name}: {e.Message})");
        }
        var path = ctx.ModPath("regulation.bin");
        using (Timing.Stage("regulation and item list"))
        {
            erStore.Save(path);
            ItemList.Write(ctx, erStore, erArchives, newNames);
        }
        File.WriteAllText(ctx.ModPath("README.txt"), ModReadme);
        Console.WriteLine($"mod: {Path.GetDirectoryName(path)}");
        // Offline checks of what was just built; problems are reported, the build still counts.
        try
        {
            using var _ = Timing.Stage("validate");
            ModValidate.Run(ctx);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine($"validate: could not run ({e.GetType().Name}: {e.Message})");
        }
        return 0;
    }

    private const string ModReadme = """
        The Dark Sign: Elden Ring content mod (generated, do not hand-edit)

        Built by `DarkSignPorter port-weapons` from the Dark Souls III and Elden Ring game files on this PC.
        Re-running it rebuilds everything here from scratch; nothing in the game folders is changed.
        me3 loads this folder over the game (TheDarkSign\Tools\me3, darksign-er.me3).

          regulation.bin                     Elden Ring params plus the ported DS3 weapons (IDs 80000000+)
                                             and a new shop lineup (130000+) for Merchant Kalé
          msg\engus\item_dlc02.msgbnd.dcx    names and descriptions of the ported weapons
          script\talk\*.talkesdbnd.dcx       Kalé's talk scripts, opening the new shop lineup
          parts\wp_a_9NNN(_l).partsbnd.dcx    ported weapon models (and low-detail versions)
          material\allmaterial_dlc02...       materials for the ported models
          menu\hi|low\01_common.*, 00_solo.*  the ported weapons' DS3 icons (sheets SB_Icon_DS3_NN, IDs 50000+)
          sfx\sfxbnd_commoneffects...       Elden Ring's common effects plus the ported DS3 ones (IDs 900000000+)
          sd\enus\cs_main.bnk, sd\wem\     Elden Ring's main sound bank plus events for the DS3 sounds, and their audio
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
        // Each weapon's model is independent (its own binder in, its own file out), so they convert on every core.
        // Results are kept per weapon and merged in plan order, so the materials list is the same on every run.
        var ported = plans.Where(p => p.Status == "port").ToList();
        var models = ported.Select(plan => (
            Ds3Model: Convert.ToInt32(ds3Weapons[plan.Ds3Id]!["equipModelId"].Value),
            TemplateModel: Convert.ToInt32(erWeapons[plan.TemplateId!.Value]!["equipModelId"].Value))).ToList();
        var results = new (List<ModelPort.NewMaterial> Materials, bool Converted, string? Failure)[ported.Count];
        Parallel.For(0, ported.Count, Parallelism.Options, i =>
        {
            var plan = ported[i];
            var (ds3Model, templateModel) = models[i];
            var mine = new List<ModelPort.NewMaterial>();
            bool done = false;
            try
            {
                foreach (var lod in new[] { false, true })
                {
                    var model = ModelPort.Convert(ds3, er, ds3Model, templateModel, plan.NewModelId!.Value, lod);
                    if (model is null)
                        continue;
                    var path = ctx.ModPath(["parts", ModelPort.PartsPath(plan.NewModelId.Value, lod)[7..]]);
                    File.WriteAllBytes(path, DCX.Compress(model.Binder.Write(), model.Compression));
                    mine.AddRange(model.Materials);
                    if (!lod)
                        done = true;
                }
                results[i] = (mine, done, null);
            }
            catch (Exception ex)
            {
                // SoulsFormats' writer can mask the real error with "Not all reservations filled"; report the root.
                var root = ex;
                while (root.InnerException is not null)
                    root = root.InnerException;
                results[i] = ([], false, $"{plan.Name} (wp_a_{ds3Model:D4}): {root.GetType().Name}: {root.Message}");
            }
        });
        var materials = results.SelectMany(r => r.Materials).ToList();
        int converted = results.Count(r => r.Converted);
        var failed = results.Where(r => r.Failure is not null).Select(r => r.Failure!).ToList();
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
