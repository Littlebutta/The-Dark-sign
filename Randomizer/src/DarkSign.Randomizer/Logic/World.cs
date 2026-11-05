namespace DarkSign.Randomizer.Logic;

/// <summary>Which game a piece of content comes from. Every name in a <see cref="World"/> carries the game prefix
/// ("ds3:highwall", "er:limgrave"), so both games' graphs live in one world without clashes.</summary>
public enum SourceGame { DS3, ER }

public static class Games
{
    public static string Prefix(SourceGame g) => g == SourceGame.DS3 ? "ds3:" : "er:";
    public static string Qualify(SourceGame g, string name) => name.Contains(':') ? name : Prefix(g) + name;
    public static SourceGame Of(string qualified) => qualified.StartsWith("ds3:", StringComparison.Ordinal) ? SourceGame.DS3 : SourceGame.ER;
}

/// <summary>A place the player can stand in. Reaching a boss area means its boss can be fought and beaten.</summary>
public sealed class Area
{
    public required string Id { get; init; }
    public required SourceGame Game { get; init; }
    public string Text { get; init; } = "";
    /// <summary>The game map this area lives in (DS3 map id for DS3 areas, before the port moves it to its slot).</summary>
    public string? Map { get; init; }
    public HashSet<string> Tags { get; init; } = [];
    /// <summary>Elden Ring: grace-menu region names, map IDs and grace names that place item locations here
    /// (see EldenLocations in the porter).</summary>
    public IReadOnlyList<string> Regions { get; init; } = [];
    public IReadOnlyList<string> Maps { get; init; } = [];
    public IReadOnlyList<string> Graces { get; init; } = [];
    /// <summary>0 = start of that game, 1 = its final area. Drives enemy scaling and item desirability.</summary>
    public double Tier { get; set; }
    public bool IsBoss => Tags.Contains("boss");
    /// <summary>The vanilla game's flag set when this area's boss dies (in that game's own numbering).</summary>
    public int DefeatFlag { get; init; }
    /// <summary>Bosses whose defeat counts as a "shard" (DS3 Lords of Cinder, Elden Ring Great Rune bearers).</summary>
    public bool IsShardbearer => Tags.Contains("shardbearer");
}

/// <summary>A fixed one-way route from one area to another, usable when <see cref="Cond"/> holds.</summary>
public sealed record Edge(string From, string To, Expr Cond, string Text, IReadOnlySet<string> Tags);

/// <summary>One side of a fog wall or warp: the area you stand in to use it, and what you need to use it.</summary>
public sealed record Side(string Area, Expr Cond, string Text);

/// <summary>
/// A fog wall, door or warp. Two-way entrances (fog walls) connect <see cref="A"/> and <see cref="B"/>; one-way
/// ones (warps) go from A to B only. Randomizable ones get new partners from the entrance shuffle.
/// </summary>
public sealed class Entrance
{
    public required string Id { get; init; }
    public required SourceGame Game { get; init; }
    public string Text { get; init; } = "";
    public required Side A { get; init; }
    public required Side B { get; init; }
    public bool OneWay { get; init; }
    public HashSet<string> Tags { get; init; } = [];
    /// <summary>The entity that the game uses for this fog wall or warp (game numbering; 0 = found by the porter).</summary>
    public int EntityId { get; init; }
    public bool Randomizable { get; set; }
}

/// <summary>An item that opens the way somewhere (keys, medallions) or that a condition names.</summary>
public sealed class KeyItem
{
    public required string Id { get; init; }
    public required SourceGame Game { get; init; }
    public string Text { get; init; } = "";
    /// <summary>The game's item reference, "category:id" (DS3 FogMod style "3:2001" = goods 2001).</summary>
    public string ItemRef { get; init; } = "";
    /// <summary>Where the item is in the vanilla game.</summary>
    public string? VanillaArea { get; init; }
    public HashSet<string> Tags { get; init; } = [];
}

/// <summary>A place an item can be: a pickup, chest, boss or enemy drop, or a finite shop slot.</summary>
public sealed class ItemLocation
{
    public required string Id { get; init; }
    public required SourceGame Game { get; init; }
    public required string Area { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Item lot rows (pickups and drops) behind this location.</summary>
    public IReadOnlyList<int> Lots { get; init; } = [];
    /// <summary>Shop rows behind this location.</summary>
    public IReadOnlyList<int> Shops { get; init; } = [];
    /// <summary>True when the location may hold a key item (always obtainable, not missable, not farmable).</summary>
    public bool KeyEligible { get; init; } = true;
    /// <summary>The item there in the vanilla game ("category:id"), if known.</summary>
    public string? VanillaItem { get; set; }
    /// <summary>Anything else needed to get the item besides reaching <see cref="Area"/>.</summary>
    public Expr Cond { get; init; } = Expr.True;
}

/// <summary>A boss that can move to another arena, and the arena it starts in.</summary>
public sealed class Boss
{
    public required string Id { get; init; }
    public required SourceGame Game { get; init; }
    public string Name { get; init; } = "";
    public required string Area { get; init; }
    /// <summary>Arena size the boss needs: small &lt; medium &lt; large. A boss may go into an arena at least as big.</summary>
    public ArenaSize Size { get; init; } = ArenaSize.Medium;
    public HashSet<string> Tags { get; init; } = [];
    /// <summary>The model ("c5110") when known, so the porter can find the boss in a map without a name lookup.</summary>
    public string? Model { get; set; }
    /// <summary>False until the boss's character files exist in Elden Ring (DS3 bosses need the enemy port).</summary>
    public bool Available { get; set; } = true;
    /// <summary>Bosses with scripted fights (final bosses, item-gated mechanics) stay where they are.</summary>
    public bool Fixed => Tags.Contains("fixed") || Tags.Contains("final");
}

public enum ArenaSize { Small, Medium, Large }

/// <summary>A regular or elite enemy placement in a map.</summary>
public sealed class EnemySpot
{
    public required string Id { get; init; }
    public required SourceGame Game { get; init; }
    public required string Area { get; init; }
    public string Name { get; init; } = "";
    public string? Model { get; init; }
    public EnemyClass Class { get; init; } = EnemyClass.Regular;
    /// <summary>Where the spot is in this player's Elden Ring build: the map (a DS3 map's slot) and entity ID. Set
    /// from the porter's enemy tables; spots without it aren't in the game yet.</summary>
    public string? Map { get; set; }
    public long Entity { get; set; }
    public string PartName { get; set; } = "";
    /// <summary>The scripts show a boss health bar for this enemy: it is an arena's boss, not a regular enemy.</summary>
    public bool Boss { get; set; }
    public bool InGame => Map is not null;
    public string Kind => EnemyKinds.KindOf(Name);
}

/// <summary>A character that exists in this player's Elden Ring build and can stand in an enemy spot: an Elden Ring
/// enemy (model, NpcParam and ThinkParam from a vanilla placement), or a DS3 character the enemy porter has ported.</summary>
public sealed record Character(SourceGame Game, string Model, int NpcParam, int ThinkParam, string Name, bool Boss,
    string? Ds3Model = null, string? Map = null)
{
    public string Kind { get; } = EnemyKinds.KindOf(Name);
}

public enum EnemyClass { Regular, Elite, Miniboss, Npc, Unused }

/// <summary>Both games' content in one graph.</summary>
public sealed class World
{
    public Dictionary<string, Area> Areas { get; } = new(StringComparer.Ordinal);
    public List<Edge> Edges { get; } = [];
    public List<Entrance> Entrances { get; } = [];
    public Dictionary<string, KeyItem> KeyItems { get; } = new(StringComparer.Ordinal);
    public List<ItemLocation> Locations { get; } = [];
    public List<Boss> Bosses { get; } = [];
    public List<EnemySpot> Enemies { get; } = [];
    /// <summary>Characters this build can place (from the porter's enemy tables); empty without them.</summary>
    public List<Character> Characters { get; } = [];
    public string Start { get; set; } = "er:chapel";

    public void Add(World other)
    {
        foreach (var (k, v) in other.Areas) Areas[k] = v;
        Edges.AddRange(other.Edges);
        Entrances.AddRange(other.Entrances);
        foreach (var (k, v) in other.KeyItems) KeyItems[k] = v;
        Locations.AddRange(other.Locations);
        Bosses.AddRange(other.Bosses);
        Enemies.AddRange(other.Enemies);
    }

    /// <summary>Throws when a condition names nothing, an edge leads nowhere, or IDs repeat.</summary>
    public void Validate()
    {
        var problems = new List<string>();
        bool Known(string n) => Areas.ContainsKey(n) || KeyItems.ContainsKey(n);
        void Check(Expr e, string where)
        {
            foreach (var n in e.Names())
                if (!Known(n)) problems.Add($"{where}: condition names unknown '{n}'");
        }
        foreach (var e in Edges)
        {
            if (!Areas.ContainsKey(e.From)) problems.Add($"edge from unknown area {e.From}");
            if (!Areas.ContainsKey(e.To)) problems.Add($"edge {e.From} -> unknown area {e.To}");
            Check(e.Cond, $"edge {e.From} -> {e.To}");
        }
        foreach (var e in Entrances)
        {
            foreach (var s in new[] { e.A, e.B })
            {
                if (!Areas.ContainsKey(s.Area)) problems.Add($"entrance {e.Id}: unknown area {s.Area}");
                Check(s.Cond, $"entrance {e.Id}");
            }
        }
        foreach (var l in Locations)
            if (!Areas.ContainsKey(l.Area)) problems.Add($"location {l.Id}: unknown area {l.Area}");
        foreach (var b in Bosses)
            if (!Areas.ContainsKey(b.Area)) problems.Add($"boss {b.Id}: unknown area {b.Area}");
        foreach (var dup in Entrances.GroupBy(e => e.Id).Where(g => g.Count() > 1))
            problems.Add($"duplicate entrance {dup.Key}");
        foreach (var dup in Locations.GroupBy(e => e.Id).Where(g => g.Count() > 1))
            problems.Add($"duplicate location {dup.Key}");
        if (!Areas.ContainsKey(Start)) problems.Add($"start area {Start} unknown");
        if (problems.Count > 0)
            throw new InvalidDataException("World data errors:\n  " + string.Join("\n  ", problems.Take(40)));
    }
}
