using DarkSign.Randomizer.Data;
using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Generate;

/// <summary>Builds the combined world for a set of options: both games' data, the link between them, and only the
/// content that exists in this player's build.</summary>
public static class WorldLoader
{
    /// <summary>The F1 menu's "Start DS3 run" warp (map port, RunEvents) and its way back.</summary>
    public const string Ds3Entry = "ds3:firelink_cemetery";
    public const string ErHub = "er:roundtable";

    public static string DefaultDataDir =>
        Path.Combine(AppContext.BaseDirectory, "randomizer-data");

    public static World Load(RandomizerOptions options, string? dataDir = null, List<string>? log = null)
    {
        dataDir ??= DefaultDataDir;
        log ??= [];
        var erFile = WorldFile.Read<WorldFile>(Path.Combine(dataDir, "er", "er-world.yaml"));
        var ds3Over = WorldFile.Read<Ds3FogData.Overrides>(Path.Combine(dataDir, "ds3", "ds3-overrides.yaml"));

        // Shard group: with shared shards, both games' shardbearers count at both gates.
        var shards = erFile.Groups.TryGetValue("shards", out var erShards) ? erShards.Select(s => "er:" + s).ToList() : [];
        var ds3Shards = ds3Over.ShardAreas.Select(s => "ds3:" + s).ToList();
        var groups = new Dictionary<string, List<string>>
        {
            ["shards"] = options.SharedShards ? [.. shards, .. ds3Shards] : shards,
        };
        var er = WorldBuilder.Build(erFile, SourceGame.ER, null, groups);
        var ds3 = Ds3FogData.Load(Path.Combine(dataDir, "ds3"), options.SharedShards ? "4 OF @shards" : null, groups);

        var world = new World();
        world.Add(er);
        world.Add(ds3);
        world.Start = er.Start;

        // The F1 menu warps between Roundtable Hold and the Cemetery of Ash at any time.
        world.Edges.Add(new Edge(ErHub, Ds3Entry, Expr.True, "the F1 menu's Start DS3 run", new HashSet<string> { "menu" }));
        world.Edges.Add(new Edge(Ds3Entry, ErHub, Expr.True, "the F1 menu's Leave DS3 run", new HashSet<string> { "menu" }));

        world.Validate();
        Filter(world, options, log);
        return world;
    }

    /// <summary>Drops content this build doesn't have (unported DS3 maps, DLC when off) and anything that becomes
    /// unreachable because of it, so the solver only works with what the player can actually visit.</summary>
    public static void Filter(World world, RandomizerOptions options, List<string> log)
    {
        var ported = options.PortedDs3Maps?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Keep(Area a)
        {
            if (!options.Dlc && (a.Tags.Contains("dlc") || a.Tags.Contains("dlc1") || a.Tags.Contains("dlc2")))
                return false;
            if (a.Game == SourceGame.DS3 && ported is not null && (a.Map is null || !ported.Contains(a.Map)))
                return false;
            return true;
        }
        var dropped = world.Areas.Values.Where(a => !Keep(a)).Select(a => a.Id).ToList();
        if (dropped.Count > 0)
            log.Add($"{dropped.Count} areas left out (DS3 maps not ported yet, or DLC off)");
        RemoveAreas(world, dropped.ToHashSet());

        // Prune areas that can't be reached even with every movable key item: e.g. a DS3 area behind a map that
        // isn't ported yet. Pinned key items count only if their area is still here.
        while (true)
        {
            var all = world.KeyItems.Values
                .Where(k => !k.Tags.Contains("pinned") || (k.VanillaArea is not null && world.Areas.ContainsKey(k.VanillaArea)))
                .Select(k => k.Id).ToList();
            var reach = OptimisticReach(world, all, options);
            var unreachable = world.Areas.Keys.Where(a => !reach.Contains(a)).ToHashSet();
            if (unreachable.Count == 0)
                break;
            log.Add($"{unreachable.Count} areas unreachable in this build and left out: {string.Join(", ", unreachable.Take(12))}{(unreachable.Count > 12 ? ", ..." : "")}");
            RemoveAreas(world, unreachable);
        }
    }

    /// <summary>Reach with every movable key item owned and every fog wall open both ways (fog walls can be
    /// re-paired, so any area with one can be connected).</summary>
    private static HashSet<string> OptimisticReach(World world, List<string> items, RandomizerOptions options)
    {
        var layout = new Layout();
        var solver = new Solver(world, layout, new HashSet<string>());
        var r = solver.Sweep(items);
        // Areas with a randomizable fog side can be linked in by the entrance shuffle.
        var linked = world.Entrances.Where(e => EntranceShuffler.IsRandomizable(e, options))
            .SelectMany(e => new[] { e.A.Area, e.B.Area }).ToHashSet();
        if (linked.Count == 0) return r.Areas;
        var w2 = new World { Start = world.Start };
        w2.Add(world);
        w2.Start = world.Start;
        foreach (var a in linked)
            w2.Edges.Add(new Edge(world.Start, a, Expr.True, "", new HashSet<string>()));
        return new Solver(w2, layout, new HashSet<string>()).Sweep(items).Areas;
    }

    private static void RemoveAreas(World world, HashSet<string> gone)
    {
        if (gone.Count == 0) return;
        foreach (var a in gone) world.Areas.Remove(a);
        world.Edges.RemoveAll(e => gone.Contains(e.From) || gone.Contains(e.To));
        world.Entrances.RemoveAll(e => gone.Contains(e.A.Area) || gone.Contains(e.B.Area));
        world.Locations.RemoveAll(l => gone.Contains(l.Area) || l.Cond.Names().Any(gone.Contains));
        world.Bosses.RemoveAll(b => gone.Contains(b.Area));
        world.Enemies.RemoveAll(e => gone.Contains(e.Area));
        // A condition naming a removed area (or a pinned key item that lived there) can never hold, which the
        // solver handles as false; the pruning loop then removes whatever only that condition opened.
    }
}
