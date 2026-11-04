using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Generate;

/// <summary>Where a fog wall side leads after shuffling.</summary>
public sealed record EntranceLink(string From, string FromText, string FromArea, int FromEntity,
    string To, string ToText, string ToArea, int ToEntity, bool CrossGame);

/// <summary>Everything a seed decided. The porter applies it to build the profile's randomized files.</summary>
public sealed class RandoPlan
{
    public int Version { get; set; } = 1;
    public long Seed { get; set; }
    public int Attempt { get; set; }
    public RandomizerOptions Options { get; set; } = new();
    public List<string> Goal { get; set; } = [];
    public List<EntranceLink> Entrances { get; set; } = [];
    /// <summary>Location ID to what it holds: "key:ds3:smalldoll", or "loc:er:..." for another location's vanilla contents.</summary>
    public Dictionary<string, string> Items { get; set; } = [];
    public List<BossPlacement> Bosses { get; set; } = [];
    public List<EnemyPlacement> Enemies { get; set; } = [];
    /// <summary>Play order: what becomes reachable at each step (areas and key items).</summary>
    public List<List<string>> Spheres { get; set; } = [];
    public List<string> Notes { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static RandoPlan FromJson(string json) => JsonSerializer.Deserialize<RandoPlan>(json, Json)!;
}

/// <summary>
/// Makes a seed: shuffles fog walls, places key items, checks the result with the solver, and rerolls (with the
/// next sub-seed, so it stays deterministic) until the run is provably completable. Then shuffles the rest of the
/// items, bosses and enemies, which can't affect progression.
/// </summary>
public static class Generator
{
    public static IReadOnlyList<string> GoalAreas(World world, Goal goal)
    {
        var er = world.Areas.ContainsKey("er:ashen_eldenbeast") ? "er:ashen_eldenbeast" : null;
        var ds3 = world.Areas.ContainsKey("ds3:kiln_soulofcinder") ? "ds3:kiln_soulofcinder" : null;
        var list = goal switch
        {
            Goal.EldenBeast => new[] { er },
            Goal.SoulOfCinder => new[] { ds3 ?? er },
            _ => new[] { er, ds3 },
        };
        return list.Where(a => a is not null).Select(a => a!).Distinct().ToList();
    }

    public static RandoPlan Generate(World world, RandomizerOptions options, List<string>? log = null,
        Dictionary<string, int>? rerollReasons = null)
    {
        log ??= [];
        var goal = GoalAreas(world, options.Goal);
        if (options.Goal != Goal.EldenBeast && !world.Areas.ContainsKey("ds3:kiln_soulofcinder"))
            log.Add("The Soul of Cinder's map isn't ported yet, so the goal is the Elden Beast for now");
        if (!options.Items)
            foreach (var k in world.KeyItems.Values) k.Tags.Add("pinned");

        string? lastFailure = null;
        for (int attempt = 0; attempt < options.MaxAttempts; attempt++)
        {
            var rng = new Rng(options.Seed, attempt);
            var layout = EntranceShuffler.Shuffle(world, options, rng, out var randomized);
            void Fail(string why)
            {
                lastFailure = why;
                if (rerollReasons is not null)
                {
                    var kind = why.Split(':')[0].Split(' ').Take(3).Aggregate((a, b) => a + " " + b);
                    rerollReasons[why] = rerollReasons.GetValueOrDefault(why) + 1;
                }
            }
            if (layout is null) { Fail("fog walls could not connect every area: " + EntranceShuffler.LastFailure); continue; }
            var solver = new Solver(world, layout, randomized);
            if (!ItemPlacer.PlaceKeys(world, solver, options, rng)) { Fail("no room for a key item"); continue; }

            var check = Check(world, solver, goal, options);
            if (check is not null) { Fail(check); continue; }

            var sweep = solver.Sweep();
            var plan = new RandoPlan
            {
                Seed = options.Seed,
                Attempt = attempt,
                Options = options,
                Goal = goal.ToList(),
                Items = ItemPlacer.Fill(world, layout, options, rng, log),
                Bosses = BossShuffler.Shuffle(world, options, rng),
                Enemies = BossShuffler.ShuffleEnemies(world, options, rng),
                Spheres = sweep.Spheres,
                Entrances = Links(world, layout, solver),
            };
            plan.Notes.AddRange(log);
            return plan;
        }
        throw new InvalidOperationException(
            $"No completable world found for seed {options.Seed} in {options.MaxAttempts} tries (last problem: {lastFailure}). " +
            "Try another seed or fewer randomized fog walls.");
    }

    /// <summary>
    /// Re-checks a finished plan from scratch (as the porter does before applying one, and the tests do for every
    /// seed): rebuilds the fog links and key placements from the plan alone and sweeps. Null when completable.
    /// </summary>
    public static string? Verify(World world, RandoPlan plan)
    {
        var layout = new Layout();
        foreach (var e in plan.Entrances)
            layout.Links[SideKey.Parse(e.From)] = SideKey.Parse(e.To);
        foreach (var (loc, content) in plan.Items)
            if (content.StartsWith("key:", StringComparison.Ordinal))
                layout.KeyPlacements[loc] = content[4..];
        var randomized = layout.Links.Keys.Select(k => k.Entrance).ToHashSet();
        var opts = plan.Options;
        if (!opts.Items)
            foreach (var k in world.KeyItems.Values) k.Tags.Add("pinned");
        // Every movable key must be placed exactly once.
        foreach (var k in ItemPlacer.MovableKeys(world, opts))
        {
            int n = layout.KeyPlacements.Values.Count(v => v == k.Id);
            if (n != 1) return $"key item {k.Id} placed {n} times";
        }
        return Check(world, new Solver(world, layout, randomized), plan.Goal, opts);
    }

    /// <summary>Null when the layout is completable (and, if asked, everything is reachable); else the reason.</summary>
    public static string? Check(World world, Solver solver, IReadOnlyList<string> goal, RandomizerOptions options)
    {
        var r = solver.Sweep();
        var missing = goal.Where(g => !r.Areas.Contains(g)).ToList();
        if (missing.Count > 0)
            return "goal unreachable: " + string.Join(", ", missing);
        foreach (var k in ItemPlacer.MovableKeys(world, options))
            if (!r.Items.Contains(k.Id))
                return $"key item {k.Id} unreachable";
        if (options.EverythingReachable)
        {
            var area = world.Areas.Keys.FirstOrDefault(a => !r.Areas.Contains(a));
            if (area is not null) return $"area {area} unreachable";
            var loc = world.Locations.FirstOrDefault(l => !r.Locations.Contains(l.Id));
            if (loc is not null) return $"location {loc.Id} unreachable";
        }
        return null;
    }

    private static List<EntranceLink> Links(World world, Layout layout, Solver solver)
    {
        var byId = world.Entrances.ToDictionary(e => e.Id);
        var list = new List<EntranceLink>();
        foreach (var (from, to) in layout.Links.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
        {
            var fs = solver.SideOf(from);
            var ts = solver.SideOf(to);
            list.Add(new EntranceLink(from.ToString(), byId[from.Entrance].Text, fs.Area, byId[from.Entrance].EntityId,
                to.ToString(), byId[to.Entrance].Text, ts.Area, byId[to.Entrance].EntityId,
                Games.Of(fs.Area) != Games.Of(ts.Area)));
        }
        return list;
    }

    /// <summary>A readable spoiler log: where every fog wall goes, where every key is, and who's in each arena.</summary>
    public static string Spoiler(World world, RandoPlan plan)
    {
        string AreaText(string id) => world.Areas.TryGetValue(id, out var a) ? $"{a.Text} ({(a.Game == SourceGame.DS3 ? "DS3" : "ER")})" : id;
        var sb = new StringBuilder();
        sb.AppendLine($"The Dark Sign cross-game randomizer, seed {plan.Seed} (attempt {plan.Attempt})");
        sb.AppendLine($"Goal: {string.Join(" and ", plan.Goal.Select(AreaText))}");
        foreach (var n in plan.Notes) sb.AppendLine("Note: " + n);
        sb.AppendLine();
        sb.AppendLine("== Key items ==");
        foreach (var (loc, content) in plan.Items.Where(kv => kv.Value.StartsWith("key:")).OrderBy(kv => kv.Value))
        {
            var l = world.Locations.First(x => x.Id == loc);
            var key = world.KeyItems[content[4..]];
            sb.AppendLine($"{key.Text}: {AreaText(l.Area)} - {l.Text}");
        }
        sb.AppendLine();
        sb.AppendLine("== Fog walls ==");
        foreach (var e in plan.Entrances.Where(e => string.CompareOrdinal(e.From, e.To) < 0))
            sb.AppendLine($"{AreaText(e.FromArea)} [{e.FromText}] <-> {AreaText(e.ToArea)} [{e.ToText}]{(e.CrossGame ? "  (crosses games)" : "")}");
        sb.AppendLine();
        sb.AppendLine("== Bosses ==");
        foreach (var b in plan.Bosses)
            sb.AppendLine($"{AreaText(b.Arena)}: {b.BossName} ({b.BossGame})");
        sb.AppendLine();
        sb.AppendLine("== Play order ==");
        for (int i = 0; i < plan.Spheres.Count; i++)
            sb.AppendLine($"{i}: {string.Join(", ", plan.Spheres[i].Select(n => world.Areas.ContainsKey(n) ? AreaText(n) : world.KeyItems.TryGetValue(n, out var k) ? "[" + k.Text + "]" : n))}");
        return sb.ToString();
    }
}
