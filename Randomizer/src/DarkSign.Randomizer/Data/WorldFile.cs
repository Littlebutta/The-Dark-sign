using DarkSign.Randomizer.Logic;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DarkSign.Randomizer.Data;

/// <summary>
/// The YAML world file layout. It is FogMod's annotation layout (Areas with To, KeyItems, Warps, Entrances,
/// DefaultCost) so FogMod's DS3 file loads as-is, plus our additions for Elden Ring: area Tier, Regions and Maps,
/// and a Bosses list. Unknown keys are ignored.
/// </summary>
public sealed class WorldFile
{
    public string? Start { get; set; }
    public List<AreaDto> Areas { get; set; } = [];
    public List<KeyItemDto> KeyItems { get; set; } = [];
    public List<EntranceDto> Warps { get; set; } = [];
    public List<EntranceDto> Entrances { get; set; } = [];
    public List<BossDto> Bosses { get; set; } = [];
    public List<DungeonDto> Dungeons { get; set; } = [];
    public Dictionary<string, double> DefaultCost { get; set; } = [];
    /// <summary>Named lists used in conditions as "@name", e.g. "2 OF @shards".</summary>
    public Dictionary<string, List<string>> Groups { get; set; } = [];

    public sealed class AreaDto
    {
        public string Name { get; set; } = "";
        public string? Text { get; set; }
        public string? Tags { get; set; }
        public int DefeatFlag { get; set; }
        public double? Tier { get; set; }
        /// <summary>Elden Ring: names of the map regions (grace menu tabs) whose item locations belong here.</summary>
        public List<string>? Regions { get; set; }
        /// <summary>Elden Ring: map IDs (m10_00, m30_02...) whose item locations belong here.</summary>
        public List<string>? Maps { get; set; }
        /// <summary>Elden Ring: names of the Sites of Grace in this area; their maps' item locations belong here
        /// (how a dungeon such as "Stormfoot Catacombs" is found without hard-coding its map ID).</summary>
        public List<string>? Graces { get; set; }
        public List<SideDto>? To { get; set; }
    }

    public sealed class SideDto
    {
        public string Area { get; set; } = "";
        public string? Text { get; set; }
        public string? Tags { get; set; }
        public string? Cond { get; set; }
        public string? DestinationMap { get; set; }
    }

    public sealed class KeyItemDto
    {
        public string Name { get; set; } = "";
        public string? Text { get; set; }
        public string? ID { get; set; }
        public string? Area { get; set; }
        public string? Tags { get; set; }
    }

    public sealed class EntranceDto
    {
        public string? Name { get; set; }
        public int ID { get; set; }
        public string? Area { get; set; }
        public string? Text { get; set; }
        public string? Tags { get; set; }
        public string? DoorCond { get; set; }
        public SideDto? ASide { get; set; }
        public SideDto? BSide { get; set; }
    }

    public sealed class BossDto
    {
        public string Name { get; set; } = "";
        public string? Text { get; set; }
        public string Area { get; set; } = "";
        public string? Size { get; set; }
        public string? Tags { get; set; }
        public string? Model { get; set; }
    }

    /// <summary>A minor dungeon: an area found by its Site of Grace name, a two-way entrance from its region,
    /// and its boss (whose arena is the dungeon itself).</summary>
    public sealed class DungeonDto
    {
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";
        public string Region { get; set; } = "";
        public double Tier { get; set; }
        public string? Boss { get; set; }
        public string? BossSize { get; set; }
        public string? Tags { get; set; }
    }

    public static T Read<T>(string path)
    {
        var text = File.ReadAllText(path).TrimStart('﻿');
        var yaml = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return yaml.Deserialize<T>(text) ?? throw new InvalidDataException($"{path} is empty");
    }

    public static HashSet<string> TagSet(string? tags) =>
        (tags ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith('#')).ToHashSet();
}

/// <summary>Turns a <see cref="WorldFile"/> into a <see cref="World"/> with every name qualified by its game.</summary>
public static class WorldBuilder
{
    // Area-to-area links FogMod marks as needing tricks or options we don't offer.
    private static readonly string[] SkippedLinkTags = ["temp", "hard", "treeskip", "instawarp"];

    public static World Build(WorldFile file, SourceGame game, Func<string, string?>? mapOfArea = null,
        IReadOnlyDictionary<string, List<string>>? extraGroups = null)
    {
        var world = new World();
        string Q(string n) => Games.Qualify(game, n);
        var groups = new Dictionary<string, List<string>>(file.Groups);
        if (extraGroups is not null)
            foreach (var (k, v) in extraGroups) groups[k] = v;
        Expr? Cond(string? text)
        {
            if (text is null) return null;
            foreach (var (name, members) in groups)
                text = text.Replace("@" + name, string.Join(' ', members));
            return Expr.Parse(text)?.Map(Q);
        }

        foreach (var d in file.Dungeons)
        {
            file.Areas.Add(new WorldFile.AreaDto
            {
                Name = "dungeon_" + d.Name, Text = d.Text, Tier = d.Tier, Graces = [d.Text],
                Tags = ("dungeon " + (d.Tags ?? "")).Trim(),
            });
            file.Entrances.Add(new WorldFile.EntranceDto
            {
                Name = d.Name, Area = d.Region, Text = $"entrance to {d.Text}", Tags = ("dungeon " + (d.Tags ?? "")).Trim(),
                ASide = new WorldFile.SideDto { Area = d.Region }, BSide = new WorldFile.SideDto { Area = "dungeon_" + d.Name },
            });
            if (d.Boss is not null)
                file.Bosses.Add(new WorldFile.BossDto
                {
                    Name = "dungeon_" + d.Name, Text = d.Boss, Area = "dungeon_" + d.Name, Size = d.BossSize ?? "small",
                    Tags = "minor",
                });
        }
        file.Dungeons = [];

        double maxCost = file.DefaultCost.Count > 0 ? Math.Max(file.DefaultCost.Values.Max(), 1e-9) : 1;
        foreach (var a in file.Areas)
        {
            var area = new Area
            {
                Id = Q(a.Name),
                Game = game,
                Text = a.Text ?? a.Name,
                Map = a.Maps?.FirstOrDefault() ?? mapOfArea?.Invoke(a.Name),
                Tags = WorldFile.TagSet(a.Tags),
                DefeatFlag = a.DefeatFlag,
                Regions = a.Regions ?? [],
                Maps = a.Maps ?? [],
                Graces = a.Graces ?? [],
                Tier = a.Tier ?? (file.DefaultCost.TryGetValue(a.Name, out var c) ? c / maxCost : 0),
            };
            world.Areas[area.Id] = area;
        }
        world.Start = Q(file.Start ?? file.Areas.First().Name);

        foreach (var a in file.Areas)
        {
            foreach (var to in a.To ?? [])
            {
                var tags = WorldFile.TagSet(to.Tags);
                if (SkippedLinkTags.Any(tags.Contains))
                    continue;
                var cond = Cond(to.Cond) ?? Expr.True;
                world.Edges.Add(new Edge(Q(a.Name), Q(to.Area), cond, to.Text ?? "", tags));
                // FogMod: a shortcut can be used backwards once its far side has been reached.
                if (tags.Contains("shortcut") || tags.Contains("twoway"))
                    world.Edges.Add(new Edge(Q(to.Area), Q(a.Name),
                        tags.Contains("shortcut") ? Expr.All(Expr.Named(Q(a.Name)), cond) : cond, to.Text ?? "", tags));
            }
        }

        foreach (var k in file.KeyItems)
        {
            world.KeyItems[Q(k.Name)] = new KeyItem
            {
                Id = Q(k.Name),
                Game = game,
                Text = k.Text ?? k.Name,
                ItemRef = k.ID ?? "",
                VanillaArea = k.Area is null ? null : Q(k.Area),
                Tags = WorldFile.TagSet(k.Tags),
            };
        }

        void AddEntrance(WorldFile.EntranceDto e, bool oneWay)
        {
            var tags = WorldFile.TagSet(e.Tags);
            if (tags.Contains("unused") || e.ASide is null || e.BSide is null)
                return;
            var door = Cond(e.DoorCond) ?? Expr.True;
            Side MakeSide(WorldFile.SideDto s) =>
                new(Q(s.Area), Expr.All(door, Cond(s.Cond) ?? Expr.True), s.Text ?? "");
            world.Entrances.Add(new Entrance
            {
                Id = $"{Games.Prefix(game)}{e.Area ?? e.ASide.Area}_{(e.ID != 0 ? e.ID.ToString() : e.Name)}",
                Game = game,
                Text = e.Text ?? e.Name ?? "",
                A = MakeSide(e.ASide),
                B = MakeSide(e.BSide),
                OneWay = oneWay,
                Tags = tags,
                EntityId = e.ID,
            });
        }
        foreach (var e in file.Entrances) AddEntrance(e, oneWay: false);
        foreach (var w in file.Warps) AddEntrance(w, oneWay: true);

        foreach (var b in file.Bosses)
        {
            world.Bosses.Add(new Boss
            {
                Id = Q(b.Name),
                Game = game,
                Name = b.Text ?? b.Name,
                Area = Q(b.Area),
                Size = Enum.TryParse<ArenaSize>(b.Size, true, out var size) ? size : ArenaSize.Medium,
                Tags = WorldFile.TagSet(b.Tags),
                Model = b.Model,
            });
        }
        return world;
    }
}
