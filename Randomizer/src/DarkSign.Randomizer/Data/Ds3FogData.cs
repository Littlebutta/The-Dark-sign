using DarkSign.Randomizer.Logic;

namespace DarkSign.Randomizer.Data;

/// <summary>
/// Loads Dark Souls III's world from thefifthmatt's FogMod data (Apache-2.0, see data/ds3/NOTICE.md) plus our
/// overrides: areas, connections, fog walls, warps and key items from fog.txt; item locations and enemy placements
/// from locations.txt; bosses, shard areas and pinned key items from ds3-overrides.yaml.
/// </summary>
public static class Ds3FogData
{
    // FogMod's DS3 map specs: an area's name starts with its map's name.
    private static readonly Dictionary<string, string> MapByPrefix = new()
    {
        ["highwall"] = "m30_00_00_00", ["lothric"] = "m30_01_00_00", ["settlement"] = "m31_00_00_00",
        ["archdragon"] = "m32_00_00_00", ["farronkeep"] = "m33_00_00_00", ["archives"] = "m34_01_00_00",
        ["cathedral"] = "m35_00_00_00", ["irithyll"] = "m37_00_00_00", ["catacombs"] = "m38_00_00_00",
        ["dungeon"] = "m39_00_00_00", ["firelink"] = "m40_00_00_00", ["untended"] = "m40_00_00_00",
        ["kiln"] = "m41_00_00_00", ["ariandel"] = "m45_00_00_00", ["dregheap"] = "m50_00_00_00",
        ["ringedcity"] = "m51_00_00_00", ["filianore"] = "m51_01_00_00",
    };

    public static string? MapOf(string areaName) =>
        MapByPrefix.TryGetValue(areaName.Split('_')[0], out var map) ? map : null;

    public sealed class Overrides
    {
        public List<WorldFile.BossDto> Bosses { get; set; } = [];
        public List<string> ShardAreas { get; set; } = [];
        public List<string> PinnedKeyItems { get; set; } = [];
        public string? KilnCond { get; set; }
        public Dictionary<string, string> KeyItemText { get; set; } = [];
    }

    private sealed class LocationsFile
    {
        public List<ItemDto> Items { get; set; } = [];
        public List<EnemyDto> Enemies { get; set; } = [];
    }

    private sealed class ItemDto
    {
        public string Key { get; set; } = "";
        public List<string>? DebugText { get; set; }
        public string Area { get; set; } = "";
        public string? Lots { get; set; }
        public string? Shops { get; set; }
    }

    private sealed class EnemyDto
    {
        public string ID { get; set; } = "";
        public string? DebugText { get; set; }
        public string Area { get; set; } = "";
    }

    /// <param name="dataDir">The randomizer-data\ds3 folder.</param>
    /// <param name="shardGroup">How the Kiln's condition is written: null keeps FogMod's four cinders; otherwise a
    /// condition such as "4 OF @shards" (shared shards).</param>
    public static World Load(string dataDir, string? kilnCondOverride = null,
        IReadOnlyDictionary<string, List<string>>? groups = null)
    {
        var fog = WorldFile.Read<WorldFile>(Path.Combine(dataDir, "fog.txt"));
        var over = WorldFile.Read<Overrides>(Path.Combine(dataDir, "ds3-overrides.yaml"));
        fog.Bosses = over.Bosses;

        var shardAreas = over.ShardAreas.ToHashSet();
        foreach (var a in fog.Areas)
        {
            if (shardAreas.Contains(a.Name))
                a.Tags = (a.Tags ?? "") + " shardbearer";
            // The Kiln's way in (the Firelink thrones) is the only place the four cinders are checked.
            if (kilnCondOverride is not null && over.KilnCond is not null)
                foreach (var to in a.To ?? [])
                    if (to.Cond == over.KilnCond)
                        to.Cond = kilnCondOverride;
        }
        if (kilnCondOverride is not null && over.KilnCond is not null)
            foreach (var e in fog.Warps.Concat(fog.Entrances))
                foreach (var s in new[] { e.ASide, e.BSide })
                    if (s is not null && s.Cond == over.KilnCond)
                        s.Cond = kilnCondOverride;
        foreach (var k in fog.KeyItems)
        {
            if (over.PinnedKeyItems.Contains(k.Name))
                k.Tags = (k.Tags ?? "") + " pinned";
            if (over.KeyItemText.TryGetValue(k.Name, out var text))
                k.Text = text;
        }

        var world = WorldBuilder.Build(fog, SourceGame.DS3, MapOf, groups);

        var locs = WorldFile.Read<LocationsFile>(Path.Combine(dataDir, "locations.txt"));
        foreach (var l in locs.Items)
        {
            // FogMod lists every area a location can be reached from; we require all of them (stricter is safe).
            var areas = l.Area.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => Games.Qualify(SourceGame.DS3, a)).ToList();
            world.Locations.Add(new ItemLocation
            {
                Id = "ds3:" + l.Key,
                Game = SourceGame.DS3,
                Area = areas[0],
                Cond = areas.Count > 1 ? Expr.All(areas.Skip(1).Select(Expr.Named).ToArray()) : Expr.True,
                Text = l.DebugText?.FirstOrDefault() ?? "",
                Lots = Ints(l.Lots),
                Shops = Ints(l.Shops),
            });
        }
        foreach (var e in locs.Enemies)
        {
            // "highwall c1100_0000 h001500": map name, MSB part name (model_index), collision.
            var parts = e.ID.Split(' ');
            var model = parts.Length > 1 ? parts[1].Split('_')[0] : null;
            var name = e.DebugText is null ? "" : e.DebugText.Split(" (")[0];
            world.Enemies.Add(new EnemySpot
            {
                Id = "ds3:" + e.ID,
                Game = SourceGame.DS3,
                Area = Games.Qualify(SourceGame.DS3, e.Area),
                Name = name,
                Model = model,
                Class = model == "c0000" ? EnemyClass.Npc : EnemyClass.Regular,
            });
        }
        return world;
    }

    private static List<int> Ints(string? s) =>
        s is null ? [] : s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
}
