using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Generate;

/// <summary>
/// Places items. Key items go first with "assumed fill": each key is put somewhere the player can reach while
/// still assuming they own every key not yet placed. That rule alone means no key can end up behind itself, so the
/// finished placement is always completable (the solver checks it anyway). Then every other location's contents
/// are shuffled among the remaining locations, across both games if allowed.
/// </summary>
public static class ItemPlacer
{
    /// <summary>Key items that move. Pinned ones (given by NPCs or events) stay with their vanilla area.</summary>
    /// Keys whose game content isn't in this build (their home area and everything that needs them were left
    /// out) are left out too.
    public static List<KeyItem> MovableKeys(World world, RandomizerOptions options)
    {
        if (!options.Items) return [];
        var named = world.Edges.SelectMany(e => e.Cond.Names())
            .Concat(world.Entrances.SelectMany(e => e.A.Cond.Names().Concat(e.B.Cond.Names())))
            .Concat(world.Locations.SelectMany(l => l.Cond.Names()))
            .ToHashSet(StringComparer.Ordinal);
        return world.KeyItems.Values
            .Where(k => !k.Tags.Contains("pinned"))
            .Where(k => named.Contains(k.Id) || (k.VanillaArea is not null && world.Areas.ContainsKey(k.VanillaArea)))
            .OrderBy(k => k.Id, StringComparer.Ordinal).ToList();
    }

    public static bool PlaceKeys(World world, Solver solver, RandomizerOptions options, Rng rng)
    {
        var keys = MovableKeys(world, options);
        rng.Shuffle(keys);
        var used = new HashSet<string>(solver.Layout.KeyPlacements.Keys);
        var byId = world.Locations.ToDictionary(l => l.Id);
        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var assumed = keys.Skip(i + 1).Select(k => k.Id);
            var sweep = solver.Sweep(assumed);
            var candidates = sweep.Locations
                .Select(id => byId[id])
                .Where(l => l.KeyEligible && !used.Contains(l.Id))
                .Where(l => options.CrossGameItems || l.Game == key.Game)
                .OrderBy(l => l.Id, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0)
                return false;
            // A gentle pull toward later areas, so keys aren't all in the first room.
            var loc = rng.PickWeighted(candidates, l => 1 + 2 * (world.Areas.TryGetValue(l.Area, out var a) ? a.Tier : 0));
            solver.Layout.KeyPlacements[loc.Id] = key.Id;
            used.Add(loc.Id);
        }
        return true;
    }

    /// <summary>
    /// Every location gets something: a placed key item, or the vanilla contents of some location ("loc:ID").
    /// Locations whose vanilla contents are a moved key item give up their contents (the key now lives elsewhere).
    /// </summary>
    public static Dictionary<string, string> Fill(World world, Layout layout, RandomizerOptions options, Rng rng, List<string> log)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (loc, key) in layout.KeyPlacements)
            result[loc] = "key:" + key;
        if (!options.Items)
            return result;

        var movedKeyRefs = MovableKeys(world, options)
            .SelectMany(k => new[] { k.ItemRef, k.Text }).Where(s => !string.IsNullOrEmpty(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = options.CrossGameItems
            ? [world.Locations]
            : world.Locations.GroupBy(l => l.Game).Select(g => g.ToList()).ToList();
        int dropped = 0;
        foreach (var group in groups)
        {
            var contents = group
                .Where(l => l.VanillaItem is null || !movedKeyRefs.Contains(l.VanillaItem))
                .OrderBy(l => l.Id, StringComparer.Ordinal)
                .Select(l => "loc:" + l.Id).ToList();
            var free = group.Where(l => !result.ContainsKey(l.Id)).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
            rng.Shuffle(contents);
            // More contents than free slots happens when key items' vanilla spots aren't known yet; the extra
            // contents (random ones) are left out and listed in the spoiler log.
            while (contents.Count > free.Count) { contents.RemoveAt(contents.Count - 1); dropped++; }
            for (int i = 0; i < free.Count; i++)
                result[free[i].Id] = i < contents.Count ? contents[i] : "loc:" + free[i].Id;
        }
        if (dropped > 0)
            log.Add($"{dropped} item location contents had no slot left (key items' vanilla spots unknown) and were left out");
        return result;
    }
}
