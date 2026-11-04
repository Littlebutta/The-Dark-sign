using System.Text;
using DarkSign.Randomizer.Generate;
using DarkSign.Randomizer.Logic;
using DarkSign.Core;
using SoulsFormats;

namespace DarkSign.Porter.Rando;

/// <summary>
/// The cross-game randomizer's porter commands (logic in Randomizer/src/DarkSign.Randomizer).
///
/// randomize: makes one profile's randomized world. The launcher writes the profile's options to
/// TheDarkSign\Randomizer\&lt;profile&gt;\options.json; this reads them, adds what this build contains (which DS3 maps
/// are ported), makes a seed the solver proves can be finished, and writes plan.json and spoiler.txt next to the
/// options. Each profile has its own folder, so each character has its own world and switching profiles never
/// rebuilds anything.
///
/// Stage 1 (now): the plan is a preview; nothing in the game changes yet. The apply stage writes the profile's
/// files to TheDarkSign\Mods\ER-Rando\&lt;profile&gt;, which the launcher loads on top of Mods\ER.
///
/// rando-survey: reports what the apply stage needs from Elden Ring's files (Sites of Grace with their regions and
/// maps, and the map item lots), so the item scanner is written against real data.
/// </summary>
public static class RandomizeCommand
{
    public static string ProfileDir(PorterContext ctx, string profile)
    {
        var safe = string.Concat(profile.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dir = Path.Combine(ctx.Root, "TheDarkSign", "Randomizer", safe.Length == 0 ? "Default" : safe);
        PathGuard.EnsureInsideRoot(dir, ctx.Root);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        string profile = "Default";
        long? seed = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--profile" && i + 1 < args.Count) profile = args[++i];
            else if (args[i] == "--seed" && i + 1 < args.Count) seed = long.Parse(args[++i]);
        }
        var dir = ProfileDir(ctx, profile);
        var optionsPath = Path.Combine(dir, "options.json");
        var options = File.Exists(optionsPath)
            ? RandomizerOptions.FromJson(File.ReadAllText(optionsPath))
            : new RandomizerOptions();
        if (seed is not null) options.Seed = seed.Value;
        options.MaxAttempts = Math.Clamp(options.MaxAttempts, 1, 1000);
        if (options.Seed == 0) options.Seed = Rng.SeedFromText(profile);

        options.PortedDs3Maps = PortedMaps(ctx);
        Console.WriteLine($"randomize: profile \"{profile}\", seed {options.Seed}, {options.PortedDs3Maps.Count} DS3 maps ported");

        var log = new List<string>();
        var world = WorldLoader.Load(options, null, log);
        foreach (var line in log) Console.WriteLine("randomize: " + line);
        // Elden Ring's own item locations come from the apply stage's scanner; until then each Elden Ring area
        // holds one stand-in location so the preview shows where keys would go.
        foreach (var area in world.Areas.Values.Where(a => a.Game == SourceGame.ER).ToList())
            world.Locations.Add(new ItemLocation { Id = area.Id + "#preview", Game = SourceGame.ER, Area = area.Id, Text = "somewhere in this area (preview)" });

        var plan = Generator.Generate(world, options, log);
        var problem = Generator.Verify(world, plan);
        if (problem is not null)
            throw new InvalidOperationException($"The plan failed its own check: {problem}");

        plan.Notes.Insert(0, "Preview: this plan isn't applied to the game yet (stage 1 of the randomizer).");
        // Co-op partners compare this (the launcher folds it into the mod code): same ID, same world.
        plan.Seal();
        File.WriteAllText(Path.Combine(dir, "plan.json"), plan.ToJson(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "spoiler.txt"), Generator.Spoiler(world, plan), new UTF8Encoding(false));
        Console.WriteLine($"randomize: seed {plan.Seed} is finishable (checked after {plan.Attempt + 1} tries): " +
            $"{plan.Entrances.Count / 2} fog walls linked ({plan.Entrances.Count(e => e.CrossGame) / 2} cross games), " +
            $"{plan.Items.Count(i => i.Value.StartsWith("key:"))} key items placed, {plan.Bosses.Count} bosses moved");
        Console.WriteLine($"randomize: world ID {plan.WorldId}; plan and spoiler log in {dir}");
        return 0;
    }

    /// <summary>DS3 maps the map port has written (darksign_maps.tsv); none if maps haven't been ported.</summary>
    public static List<string> PortedMaps(PorterContext ctx)
    {
        var path = Path.Combine(ctx.Root, "TheDarkSign", "Mods", "ER", "darksign_maps.tsv");
        if (!File.Exists(path)) return [];
        // Columns: DS3 map, Elden Ring slot, name, warp flag. The new-map-ID probe ports m40 a second time; skip it.
        return File.ReadLines(path).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => !l.Contains("new map ID test", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Split('\t')[0].Trim()).Where(m => m.StartsWith('m')).Distinct().ToList();
    }

    /// <summary>Writes Work\rando-survey.txt: Elden Ring's graces (name, region tab, map, position) and map item
    /// lots, which the apply stage's item scanner is built from.</summary>
    public static int Survey(PorterContext ctx)
    {
        var er = ctx.LoadParams(PorterGame.EldenRing);
        var archives = ctx.OpenArchives(PorterGame.EldenRing);
        var places = SafeText(archives, "PlaceName");
        var outPath = ctx.WorkPath("rando-survey.txt");
        using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));

        w.WriteLine("== BonfireWarpSubCategoryParam (grace menu regions) ==");
        var sub = er.Get("BonfireWarpSubCategoryParam");
        var subNames = new Dictionary<int, string>();
        if (sub is null) w.WriteLine("(missing)");
        else
        {
            w.WriteLine("fields: " + string.Join(", ", sub.Rows.FirstOrDefault()?.Cells.Select(c => c.Def.InternalName) ?? []));
            foreach (var row in sub.Rows)
            {
                var text = Field(row, "textId");
                var name = text is int t && places.TryGetValue(t, out var n) ? n : row.Name ?? "";
                subNames[row.ID] = name;
                w.WriteLine($"{row.ID}\t{text}\t{name}\t{Field(row, "tabId")}");
            }
        }

        w.WriteLine();
        w.WriteLine("== BonfireWarpParam (Sites of Grace) ==");
        var graces = er.Get("BonfireWarpParam");
        if (graces is null) w.WriteLine("(missing)");
        else
        {
            w.WriteLine("fields: " + string.Join(", ", graces.Rows.FirstOrDefault()?.Cells.Select(c => c.Def.InternalName) ?? []));
            w.WriteLine("id\tname\tregion\tmap (area_gridX_gridZ)\tpos\tentity");
            foreach (var row in graces.Rows)
            {
                var text = Field(row, "textId1");
                var name = text is int t && places.TryGetValue(t, out var n) ? n : row.Name ?? "";
                var subId = Field(row, "bonfireSubCategoryId");
                var region = subId is int s && subNames.TryGetValue(s, out var r) ? r : subId?.ToString() ?? "";
                w.WriteLine($"{row.ID}\t{name}\t{region}\tm{Field(row, "areaNo")}_{Field(row, "gridXNo")}_{Field(row, "gridZNo")}" +
                    $"\t{Field(row, "posX")},{Field(row, "posY")},{Field(row, "posZ")}\t{Field(row, "bonfireEntityId")}");
            }
        }

        w.WriteLine();
        w.WriteLine("== ItemLotParam_map (map pickups) ==");
        var lots = er.Get("ItemLotParam_map");
        int lotCount = 0;
        if (lots is null) w.WriteLine("(missing)");
        else
        {
            w.WriteLine("fields: " + string.Join(", ", lots.Rows.FirstOrDefault()?.Cells.Select(c => c.Def.InternalName) ?? []));
            w.WriteLine("id\tflag\titem1 (category:id x count)\tname");
            foreach (var row in lots.Rows)
            {
                lotCount++;
                w.WriteLine($"{row.ID}\t{Field(row, "getItemFlagId")}\t{Field(row, "lotItemCategory01")}:{Field(row, "lotItemId01")} x{Field(row, "lotItemNum01")}\t{row.Name}");
            }
        }
        Console.WriteLine($"rando-survey: {graces?.Rows.Count ?? 0} graces, {sub?.Rows.Count ?? 0} regions, {lotCount} map item lots, " +
            $"{places.Count} place names -> {outPath}");
        return 0;
    }

    private static object? Field(PARAM.Row row, string name) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static Dictionary<int, string> SafeText(GameArchives archives, string kind)
    {
        try { return ItemText.Load(archives, kind); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: {kind} text unreadable ({ex.Message})");
            return [];
        }
    }
}
