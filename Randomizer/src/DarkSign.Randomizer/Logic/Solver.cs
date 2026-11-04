namespace DarkSign.Randomizer.Logic;

/// <summary>One side of a randomizable entrance: entrance ID plus "A" or "B".</summary>
public readonly record struct SideKey(string Entrance, bool IsA)
{
    public override string ToString() => Entrance + (IsA ? ":A" : ":B");
    public static SideKey Parse(string s) => new(s[..^2], s.EndsWith(":A", StringComparison.Ordinal));
}

/// <summary>A randomized world to check: which fog sides lead where, and which key item is where.</summary>
public sealed class Layout
{
    /// <summary>Going through side X arrives at side Y (always stored both ways for two-way entrances).</summary>
    public Dictionary<SideKey, SideKey> Links { get; } = [];
    /// <summary>Location ID to key item ID.</summary>
    public Dictionary<string, string> KeyPlacements { get; } = new(StringComparer.Ordinal);
}

/// <summary>What a sweep from the start reached, in the order a player could get it.</summary>
public sealed class SweepResult
{
    public HashSet<string> Areas { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Items { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Locations { get; } = new(StringComparer.Ordinal);
    /// <summary>Spheres: everything newly reachable in each round (for the spoiler log's play order).</summary>
    public List<List<string>> Spheres { get; } = [];
    public bool Has(string name) => Areas.Contains(name) || Items.Contains(name);
}

/// <summary>
/// Sweeps a world from its start: repeatedly follows every route whose condition holds and picks up every key item
/// whose location is reached, until nothing changes. Because Elden Ring can warp to any reached Site of Grace
/// (and ported DS3 bonfires become graces), anything reached once stays reachable, so this fixpoint is exactly what
/// a player can do. A seed is accepted only if this sweep reaches the goal (and, by default, every location).
/// </summary>
public sealed class Solver
{
    private readonly World _world;
    private readonly Dictionary<string, List<(string To, Expr Cond)>> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ItemLocation>> _locationsByArea = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<KeyItem>> _pinnedByArea = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entrance> _entrances;

    /// <param name="randomized">Entrances whose sides follow <see cref="Layout.Links"/> instead of vanilla.</param>
    public Solver(World world, Layout layout, IReadOnlySet<string> randomized)
    {
        _world = world;
        Layout = layout;
        _entrances = world.Entrances.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var e in world.Edges)
            Route(e.From, e.To, e.Cond);
        foreach (var e in world.Entrances)
        {
            if (randomized.Contains(e.Id))
            {
                foreach (var isA in new[] { true, false })
                {
                    var from = new SideKey(e.Id, isA);
                    if (!layout.Links.TryGetValue(from, out var to)) continue;
                    var side = isA ? e.A : e.B;
                    Route(side.Area, SideOf(to).Area, side.Cond);
                }
            }
            else
            {
                Route(e.A.Area, e.B.Area, e.A.Cond);
                if (!e.OneWay)
                    Route(e.B.Area, e.A.Area, e.B.Cond);
            }
        }
        foreach (var l in world.Locations)
            Add(_locationsByArea, l.Area, l);
        foreach (var k in world.KeyItems.Values)
            if (k.Tags.Contains("pinned") && k.VanillaArea is not null)
                Add(_pinnedByArea, k.VanillaArea, k);
    }

    public Layout Layout { get; }

    public Side SideOf(SideKey key) => key.IsA ? _entrances[key.Entrance].A : _entrances[key.Entrance].B;

    private void Route(string from, string to, Expr cond)
    {
        if (!_routes.TryGetValue(from, out var list))
            _routes[from] = list = [];
        list.Add((to, cond));
    }

    private static void Add<T>(Dictionary<string, List<T>> d, string k, T v)
    {
        if (!d.TryGetValue(k, out var list))
            d[k] = list = [];
        list.Add(v);
    }

    /// <param name="assumedItems">Key items treated as owned from the start (assumed fill).</param>
    public SweepResult Sweep(IEnumerable<string>? assumedItems = null)
    {
        var r = new SweepResult();
        if (assumedItems is not null)
            r.Items.UnionWith(assumedItems);
        var pendingRoutes = new List<(string To, Expr Cond)>();
        var pendingLocations = new List<ItemLocation>();

        void Reach(string area, List<string> sphere)
        {
            if (!r.Areas.Add(area)) return;
            sphere.Add(area);
            if (_routes.TryGetValue(area, out var routes)) pendingRoutes.AddRange(routes);
            if (_locationsByArea.TryGetValue(area, out var locs)) pendingLocations.AddRange(locs);
            if (_pinnedByArea.TryGetValue(area, out var pins))
                foreach (var k in pins)
                    if (r.Items.Add(k.Id)) sphere.Add(k.Id);
        }

        var first = new List<string>();
        Reach(_world.Start, first);
        r.Spheres.Add(first);
        while (true)
        {
            var sphere = new List<string>();
            bool changed = true;
            // Areas within a sphere chain freely; items found in this sphere count from the next one.
            while (changed)
            {
                changed = false;
                for (int i = 0; i < pendingRoutes.Count; i++)
                {
                    var (to, cond) = pendingRoutes[i];
                    if (r.Areas.Contains(to)) { pendingRoutes.RemoveAt(i--); continue; }
                    if (cond.Eval(r.Has))
                    {
                        pendingRoutes.RemoveAt(i--);
                        Reach(to, sphere);
                        changed = true;
                    }
                }
            }
            var newItems = new List<string>();
            for (int i = 0; i < pendingLocations.Count; i++)
            {
                var loc = pendingLocations[i];
                if (!loc.Cond.Eval(r.Has)) continue;
                pendingLocations.RemoveAt(i--);
                r.Locations.Add(loc.Id);
                if (Layout.KeyPlacements.TryGetValue(loc.Id, out var key) && !r.Items.Contains(key))
                    newItems.Add(key);
            }
            foreach (var k in newItems)
                if (r.Items.Add(k)) sphere.Add(k);
            if (sphere.Count == 0)
                break;
            r.Spheres.Add(sphere);
        }
        return r;
    }
}
