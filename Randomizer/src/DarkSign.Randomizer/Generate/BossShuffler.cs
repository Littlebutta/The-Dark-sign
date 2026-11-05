using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Generate;

/// <summary>A boss placed into an arena, and how much it should be scaled to fit the arena's place in the run.
/// When the porter's enemy tables are there, it also says which part to change (map and entity of the arena's boss)
/// and the character to put there (model, NpcParam, ThinkParam).</summary>
public sealed record BossPlacement(string Arena, string ArenaGame, string Boss, string BossName, string BossGame,
    double FromTier, double ToTier, string? Map = null, long Entity = 0, string? Model = null, int NpcParam = 0, int ThinkParam = 0);

/// <summary>An enemy spot given another character, from either game. Map and Entity say which part in this
/// player's build; Model, NpcParam and ThinkParam what to put there (0 when only the model is known).</summary>
public sealed record EnemyPlacement(string Spot, string Area, string Model, string Name, string FromGame, double ToTier,
    string? Map = null, long Entity = 0, int NpcParam = 0, int ThinkParam = 0, string SpotGame = "", string WasName = "");

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
        foreach (var c in world.Characters.Where(c => c.Game == SourceGame.DS3 && c.Ds3Model is not null))
            ported.Add(c.Ds3Model!);
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
                var spot = ArenaSpot(world, arena);
                var who = CharacterOf(world, boss);
                result.Add(new BossPlacement(arena.Area, arena.Game.ToString(), boss.Id, boss.Name, boss.Game.ToString(),
                    TierOf(world, boss.Area), TierOf(world, arena.Area), spot?.Map, spot?.Entity ?? 0,
                    who?.Model ?? boss.Model, who?.NpcParam ?? 0, who?.ThinkParam ?? 0));
            }
        }
        return result;
    }

    /// <summary>
    /// Gives enemy spots new characters, mixing both games: Elden Ring enemies into DS3's maps and ported DS3
    /// characters into Elden Ring's (and each game's own enemies among its own spots). With the porter's enemy
    /// tables, only spots and characters that exist in this player's build take part; bosses are left to the boss
    /// shuffle, so an arena's boss is never replaced by a regular enemy.
    /// </summary>
    public static List<EnemyPlacement> ShuffleEnemies(World world, RandomizerOptions options, Rng rng)
    {
        var result = new List<EnemyPlacement>();
        if (!options.Enemies)
            return result;
        double Weight(SourceGame pick, SourceGame spot) =>
            pick != spot ? Math.Max(0.02, options.CrossGameEnemies) : Math.Max(0.02, 1 - options.CrossGameEnemies);

        if (world.Characters.Count > 0)
        {
            var spots = world.Enemies.Where(e => e.InGame && !e.Boss && e.Class == EnemyClass.Regular)
                .OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            // Pick the game first (so a long list of one game's variants doesn't drown the other), then a model of
            // that game, preferring the spot's own kind (a knight's spot gets a knight more often), then a variant.
            var byGame = world.Characters.Where(c => !c.Boss).GroupBy(c => c.Game)
                .ToDictionary(g => g.Key, g => g.GroupBy(c => c.Model).OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.ToList()).ToList());
            if (byGame.Count == 0)
                return result;
            var games = byGame.Keys.OrderBy(g => g).ToList();
            foreach (var spot in spots)
            {
                var game = rng.PickWeighted(games, g => Weight(g, spot.Game));
                var model = rng.PickWeighted(byGame[game], m => spot.Kind.Length > 0 && m[0].Kind == spot.Kind ? 3.0 : 1.0);
                var pick = rng.Pick(model);
                result.Add(new EnemyPlacement(spot.Id, spot.Area, pick.Model, pick.Name, pick.Game.ToString(), TierOf(world, spot.Area),
                    spot.Map, spot.Entity, pick.NpcParam, pick.ThinkParam, spot.Game.ToString(), spot.Name));
            }
            return result;
        }

        // No tables (nothing ported yet): FogMod's DS3 list, and only characters known to be in Elden Ring.
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
                var pick = rng.PickWeighted(pool, p => Weight(p.Game, spot.Game));
                result.Add(new EnemyPlacement(spot.Id, spot.Area, pick.Model!, pick.Name, pick.Game.ToString(), TierOf(world, spot.Area),
                    SpotGame: spot.Game.ToString(), WasName: spot.Name));
            }
        }
        return result;
    }

    /// <summary>The boss part standing in an arena in this build (from the enemy tables), if known. DS3 spots carry
    /// their FogMod area; an Elden Ring map can hold several bosses, so there the boss's name has to match.</summary>
    private static EnemySpot? ArenaSpot(World world, Boss arena)
    {
        var spots = world.Enemies.Where(e => e.InGame && e.Boss).OrderBy(e => e.Id, StringComparer.Ordinal);
        if (arena.Game == SourceGame.DS3)
            return spots.FirstOrDefault(e => e.Area == arena.Area);
        return NameMatch(spots.Where(e => e.Game == SourceGame.ER && InMaps(world, arena, e.Map)).ToList(), arena.Name, e => e.Name);
    }

    /// <summary>The character that plays a boss in this build: the ported DS3 character, or the Elden Ring boss
    /// placed in one of the boss's maps whose name matches.</summary>
    private static Character? CharacterOf(World world, Boss boss)
    {
        if (boss.Game == SourceGame.DS3)
            return world.Characters.FirstOrDefault(c => c.Game == SourceGame.DS3 && c.Boss && c.Ds3Model == boss.Model)
                ?? world.Characters.FirstOrDefault(c => c.Game == SourceGame.DS3 && c.Ds3Model == boss.Model);
        return NameMatch(world.Characters.Where(c => c.Game == SourceGame.ER && c.Boss && InMaps(world, boss, c.Map)).ToList(), boss.Name, c => c.Name);
    }

    // An arena area without its own map list (most Elden Ring arenas) may be in any Elden Ring map; the name decides.
    private static bool InMaps(World world, Boss boss, string? map) =>
        map is not null && (!world.Areas.TryGetValue(boss.Area, out var a) || a.Maps.Count == 0 ||
                            a.Maps.Any(m => map.StartsWith(m, StringComparison.OrdinalIgnoreCase)));

    // "Godrick the Grafted" matches an NpcParam named "Godrick the Grafted" or "Godrick".
    private static T? NameMatch<T>(List<T> candidates, string bossName, Func<T, string> name) where T : class
    {
        var word = bossName.Split(' ', ',')[0];
        return candidates.FirstOrDefault(c => word.Length > 2 && name(c).Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static double TierOf(World world, string area) => world.Areas.TryGetValue(area, out var a) ? a.Tier : 0;
}
