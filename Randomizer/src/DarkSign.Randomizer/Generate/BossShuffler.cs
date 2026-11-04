using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Generate;

/// <summary>A boss placed into an arena, and how much it should be scaled to fit the arena's place in the run.</summary>
public sealed record BossPlacement(string Arena, string ArenaGame, string Boss, string BossName, string BossGame,
    double FromTier, double ToTier);

/// <summary>A regular enemy replaced by another game's or another area's enemy.</summary>
public sealed record EnemyPlacement(string Spot, string Area, string Model, string Name, string FromGame, double ToTier);

/// <summary>
/// Moves bosses between arenas, mixing both games. Progression never depends on which boss stands in an arena:
/// the arena keeps its own defeat flag, drops and fog walls, so beating whoever is there counts as beating the
/// arena's boss. Fixed bosses (scripted fights) and bosses whose characters aren't in Elden Ring yet stay out.
/// </summary>
public static class BossShuffler
{
    public static List<BossPlacement> Shuffle(World world, RandomizerOptions options, Rng rng)
    {
        var result = new List<BossPlacement>();
        if (!options.Bosses)
            return result;
        var ported = options.PortedDs3Characters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var b in world.Bosses.Where(b => b.Game == SourceGame.DS3))
            b.Available = b.Model is not null && ported.Contains(b.Model);

        // Arenas that take a new boss: every movable boss's, plus fixed ones whose boss isn't in Elden Ring yet
        // (a ported DS3 arena must not stand empty). Only movable, available bosses travel.
        var movable = world.Bosses.Where(b => !b.Fixed || !b.Available).ToList();
        foreach (var tier in options.KeepBossTiers ? new[] { true, false } : new[] { true })
        {
            var arenas = options.KeepBossTiers ? movable.Where(b => b.Tags.Contains("minor") != tier).ToList() : movable;
            var pool = arenas.Where(b => b.Available && !b.Fixed).ToList();
            if (pool.Count == 0)
                continue;
            var order = arenas.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            rng.Shuffle(order);
            // Biggest needs first, so large bosses still find large arenas.
            order = order.OrderByDescending(a => a.Size).ToList();
            var remaining = new List<Boss>(pool);
            foreach (var arena in order)
            {
                if (remaining.Count == 0) remaining.AddRange(pool);   // more arenas than bosses: reuse
                var fits = remaining.Where(b => b.Size <= arena.Size).ToList();
                if (fits.Count == 0) fits = pool.Where(b => b.Size <= arena.Size).ToList();
                if (fits.Count == 0) continue;                         // keep vanilla
                var boss = rng.PickWeighted(fits, b => b.Game != arena.Game
                    ? Math.Max(0.02, options.CrossGameBosses) : Math.Max(0.02, 1 - options.CrossGameBosses));
                remaining.Remove(boss);
                result.Add(new BossPlacement(arena.Area, arena.Game.ToString(), boss.Id, boss.Name, boss.Game.ToString(),
                    TierOf(world, boss.Area), TierOf(world, arena.Area)));
            }
        }
        return result;
    }

    public static List<EnemyPlacement> ShuffleEnemies(World world, RandomizerOptions options, Rng rng)
    {
        var result = new List<EnemyPlacement>();
        if (!options.Enemies)
            return result;
        var ported = options.PortedDs3Characters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Available(EnemySpot e) => e.Model is not null && (e.Game == SourceGame.ER || ported.Contains(e.Model));
        foreach (var cls in new[] { EnemyClass.Regular, EnemyClass.Elite, EnemyClass.Miniboss })
        {
            var spots = world.Enemies.Where(e => e.Class == cls).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            var pool = spots.Where(Available).GroupBy(e => (e.Game, e.Model)).Select(g => g.First()).ToList();
            if (pool.Count == 0)
                continue;
            foreach (var spot in spots)
            {
                var pick = rng.PickWeighted(pool, p => p.Game != spot.Game
                    ? Math.Max(0.02, options.CrossGameEnemies) : Math.Max(0.02, 1 - options.CrossGameEnemies));
                result.Add(new EnemyPlacement(spot.Id, spot.Area, pick.Model!, pick.Name, pick.Game.ToString(), TierOf(world, spot.Area)));
            }
        }
        return result;
    }

    private static double TierOf(World world, string area) => world.Areas.TryGetValue(area, out var a) ? a.Tier : 0;
}
