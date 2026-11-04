using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Generate;

/// <summary>
/// Pairs fog wall sides so that walking through one fog wall brings you out of another, possibly in the other game.
/// Every pairing is two-way (walk back through and you return), in the spirit of FogMod.
///
/// The pairing grows outward from the start: each step joins a side the player can already reach to a side in a
/// part of the world nobody can reach yet, so the whole world ends up connected. Conditions (keys, bosses) are
/// checked afterwards by the solver together with the item placement; a seed that fails is rerolled.
/// </summary>
public static class EntranceShuffler
{
    public static bool IsRandomizable(Entrance e, RandomizerOptions o)
    {
        if (!o.Entrances || e.OneWay) return false;
        if (e.Tags.Contains("norandom") || e.Tags.Contains("door") || e.Tags.Contains("unused") || e.Tags.Contains("kiln"))
            return false;
        if (e.Tags.Contains("boss")) return o.BossFogs;
        if (e.Tags.Contains("dungeon")) return o.DungeonEntrances;
        if (e.Tags.Contains("pvp")) return o.Ds3PvpFogs;
        return true;
    }

    /// <summary>Why the last shuffle gave up (diagnostics).</summary>
    [ThreadStatic] public static string? LastFailure;

    public static Layout? Shuffle(World world, RandomizerOptions options, Rng rng, out HashSet<string> randomized)
    {
        // A side whose condition can never hold in this build (it names an area or key that isn't here, e.g. a
        // DS3 map not ported yet) can't be walked through, so its fog wall stays vanilla.
        bool Possible(Expr c) => c.Eval(n => world.Areas.ContainsKey(n) || world.KeyItems.ContainsKey(n));
        var shuffled = world.Entrances.Where(e => IsRandomizable(e, options) && Possible(e.A.Cond) && Possible(e.B.Cond))
            .Select(e => e.Id).ToHashSet();
        randomized = shuffled;
        var layout = new Layout();
        if (shuffled.Count == 0)
            return layout;

        var entrances = world.Entrances.ToDictionary(e => e.Id);
        var sides = shuffled.OrderBy(id => id, StringComparer.Ordinal).SelectMany(id => new[] { new SideKey(id, true), new SideKey(id, false) }).ToList();
        rng.Shuffle(sides);
        string AreaOf(SideKey s) => s.IsA ? entrances[s.Entrance].A.Area : entrances[s.Entrance].B.Area;
        SourceGame GameOf(SideKey s) => Games.Of(AreaOf(s));

        // Fixed routes, ignoring conditions (the solver checks those).
        var fixedRoutes = new Dictionary<string, List<string>>();
        void Route(string a, string b)
        {
            if (!fixedRoutes.TryGetValue(a, out var l)) fixedRoutes[a] = l = [];
            l.Add(b);
        }
        foreach (var e in world.Edges) Route(e.From, e.To);
        foreach (var e in world.Entrances.Where(e => !shuffled.Contains(e.Id)))
        {
            Route(e.A.Area, e.B.Area);
            if (!e.OneWay) Route(e.B.Area, e.A.Area);
        }

        var reached = new HashSet<string>();
        void Flood(string start)
        {
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var a = stack.Pop();
                if (!reached.Add(a)) continue;
                if (fixedRoutes.TryGetValue(a, out var next))
                    foreach (var n in next) stack.Push(n);
            }
        }
        // How many unpaired sides the player would gain by arriving in an area (its fixed closure).
        int SidesGained(string area, HashSet<SideKey> unpaired)
        {
            var seen = new HashSet<string>(reached);
            var stack = new Stack<string>();
            stack.Push(area);
            int count = 0;
            var areas = new HashSet<string>();
            while (stack.Count > 0)
            {
                var a = stack.Pop();
                if (!seen.Add(a)) continue;
                areas.Add(a);
                if (fixedRoutes.TryGetValue(a, out var next))
                    foreach (var n in next) stack.Push(n);
            }
            foreach (var s in unpaired)
                if (areas.Contains(AreaOf(s))) count++;
            return count;
        }

        // An inner side opens onto a dead end: a boss arena or a minor dungeon.
        bool Inner(SideKey k) => world.Areas.TryGetValue(AreaOf(k), out var ar) && (ar.IsBoss || ar.Tags.Contains("dungeon"));

        double CrossWeight(SideKey from, SideKey to) =>
            GameOf(from) != GameOf(to) ? Math.Max(0.02, options.CrossGameFogs) : Math.Max(0.02, 1 - options.CrossGameFogs);

        var unpaired = new HashSet<SideKey>(sides);
        void Pair(SideKey a, SideKey b)
        {
            layout.Links[a] = b;
            layout.Links[b] = a;
            unpaired.Remove(a);
            unpaired.Remove(b);
        }
        Side SideData(SideKey k) => k.IsA ? entrances[k.Entrance].A : entrances[k.Entrance].B;
        var allKeys = ItemPlacer.MovableKeys(world, options).Select(k => k.Id).ToList();

        // Out of open sides with places still unconnected: take a link whose two areas stay reachable without it,
        // and send one of its sides to the unconnected place instead. The freed side becomes open again.
        bool Repair(List<SideKey> outside)
        {
            var target = outside[0];
            var links = layout.Links.Where(kv => string.CompareOrdinal(kv.Key.ToString(), kv.Value.ToString()) < 0)
                .Select(kv => (kv.Key, kv.Value)).OrderBy(p => p.Key.ToString(), StringComparer.Ordinal).ToList();
            rng.Shuffle(links);
            foreach (var (x, y) in links.Take(40))
            {
                foreach (var (keep, free) in new[] { (x, y), (y, x) })
                {
                    if (Inner(keep) && Inner(target)) continue;
                    layout.Links.Remove(x);
                    layout.Links.Remove(y);
                    var r = new Solver(world, layout, shuffled).Sweep(allKeys);
                    if (r.Areas.Contains(AreaOf(keep)) && r.Areas.Contains(AreaOf(free)) && SideData(keep).Cond.Eval(r.Has))
                    {
                        unpaired.Add(x);
                        unpaired.Add(y);
                        Pair(keep, target);
                        return true;
                    }
                    layout.Links[x] = y;
                    layout.Links[y] = x;
                }
            }
            return false;
        }

        while (true)
        {
            // What the player can reach so far, assuming every movable key (the item placer guarantees keys).
            var sweep = new Solver(world, layout, shuffled).Sweep(allKeys);
            reached.Clear();
            reached.UnionWith(sweep.Areas);
            var frontier = sides.Where(s => unpaired.Contains(s) && reached.Contains(AreaOf(s)) && SideData(s).Cond.Eval(sweep.Has)).ToList();
            var outside = sides.Where(s => unpaired.Contains(s) && !reached.Contains(AreaOf(s))).ToList();
            if (outside.Count == 0)
                break;
            if (frontier.Count == 0 && Repair(outside))
                continue;
            if (frontier.Count == 0)
            {
                LastFailure = $"{outside.Count} sides unreachable, e.g. {string.Join(", ", outside.Take(4).Select(AreaOf))}";
                return null;
            }
            // Inner sides (boss arenas, dungeons) only pair with outer sides, and two outer sides only pair while
            // enough outer sides remain for every inner one; that keeps every arena and dungeon placeable.
            int innerLeft = unpaired.Count(Inner), outerLeft = unpaired.Count - innerLeft;
            bool Allowed(SideKey f, SideKey t) =>
                !(Inner(f) && Inner(t)) && (Inner(f) || Inner(t) || outerLeft - 2 >= innerLeft);
            var pairs = frontier.SelectMany(f => outside.Where(t => Allowed(f, t)).Select(t => (f, t))).ToList();
            if (pairs.Count == 0)
            {
                LastFailure = $"no allowed pairing ({frontier.Count} open sides, {outside.Count} unreached)";
                return null;
            }
            var from = rng.Pick(pairs.Select(p => p.f).Distinct().ToList());
            var allowed = pairs.Where(p => p.f.Equals(from)).Select(p => p.t).ToList();
            // Keep enough open sides for every remaining dead end (a side whose area adds no new open sides),
            // and grow through areas that open more fog walls first.
            var gain = outside.ToDictionary(o => o, o => SidesGained(AreaOf(o), unpaired));
            int deadOutside = gain.Count(kv => kv.Value <= 1);
            bool KeepsBudget(SideKey t)
            {
                int newFrontier = frontier.Count - 1 + Math.Max(0, gain[t] - 1);
                int newDead = deadOutside - (gain[t] <= 1 ? 1 : 0);
                return outside.Count == 1 || newFrontier >= Math.Max(1, newDead);
            }
            var safe = allowed.Where(KeepsBudget).ToList();
            if (safe.Count == 0) safe = allowed;
            var candidates = safe.Where(t => gain[t] >= 2).ToList();
            if (candidates.Count == 0) candidates = safe;
            var to = rng.PickWeighted(candidates, c => CrossWeight(from, c));
            Pair(from, to);
        }

        // Everything is connected; pair the remaining open sides among themselves, inner sides first.
        var rest = sides.Where(unpaired.Contains).ToList();
        foreach (var inner in rest.Where(Inner).ToList())
        {
            if (!rest.Contains(inner)) continue;
            rest.Remove(inner);
            var outers = rest.Where(o => !Inner(o)).ToList();
            if (outers.Count == 0) { LastFailure = "more arena/dungeon sides than fog walls to reach them"; return null; }
            var o = rng.PickWeighted(outers, c => CrossWeight(inner, c));
            rest.Remove(o);
            Pair(inner, o);
        }
        while (rest.Count > 1)
        {
            var a = rest[^1];
            rest.RemoveAt(rest.Count - 1);
            var others = rest.Where(s => s.Entrance != a.Entrance).ToList();
            if (others.Count == 0) others = rest;
            var b = rng.PickWeighted(others, c => CrossWeight(a, c));
            rest.Remove(b);
            Pair(a, b);
        }
        if (rest.Count == 1)
        {
            // Odd one out (only when sides were dropped by filtering): send it to its own entrance's other side.
            var s = rest[0];
            var other = new SideKey(s.Entrance, !s.IsA);
            layout.Links[s] = other;
        }
        return layout;
    }
}
