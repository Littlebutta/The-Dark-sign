using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Carries a DS3 map's regions (MSB3 "points") and the map-file events built on them into the Elden Ring layout.
/// The event scripts are already converted (<see cref="EventPort"/>), but they wait on things the map file holds:
/// "is the player inside region 3002800", "warp to point 3002990", an enemy's patrol route, an enemy generator's
/// spawn points. Until this step the ported map file had none of them, so those events never fired.
///
/// What a DS3 region becomes, by what the game uses it for:
/// <list type="bullet">
/// <item>Event, ActivationArea, WarpPoint, Logic, Other: an Elden Ring "Other" region (the plain script region) with
/// DS3's shape, spot and entity ID moved to the slot. The settings Elden Ring adds are the ones JortPob's script
/// regions carry (MakePart.Region: no map ID, UnkE08 255, UnkS0C -1), and each gets its own RegionID.</item>
/// <item>SpawnPoint: an Elden Ring SpawnPoint (enemy generators name them).</item>
/// <item>PatrolRoute, MovementPoint: an Elden Ring PatrolRoute point (JortPob's MakePart.PatrolRoute).</item>
/// <item>Everything else (invasion points, sound and effect emitters, wind, messages, muffling, environment map
/// points) is left out and counted: their IDs name DS3 sounds, effects and message rows, which other port steps
/// own. Lighting's environment map box is added by <see cref="MapLighting"/>, the effect emitters (SFX) by
/// <see cref="MapSfx"/> once their effects are converted.</item>
/// </list>
///
/// Map-file events: PatrolInfo (an enemy's route: a list of points), PlatoonInfo (enemies that fight as a group)
/// and Generator (respawning enemies) are carried with their part and region names moved to the ported names.
/// Treasures and ObjActs belong to the object port (<see cref="ObjectPort"/>).
/// </summary>
public static class RegionPort
{
    public sealed record Result(int Regions, int WithEntity, int RoutePoints, int Patrols, int EnemiesOnPatrol, int Platoons,
        int Generators, IReadOnlyDictionary<string, int> LeftOut, IReadOnlyList<string> Notes)
    {
        public string Summary =>
            $"regions: {Regions} carried ({WithEntity} with an entity ID the scripts can name, {RoutePoints} patrol points)"
            + (LeftOut.Count == 0 ? "" : "; left out: " + string.Join(", ", LeftOut.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")))
            + $"; map-file events: {Patrols} patrol routes ({EnemiesOnPatrol} enemies walk one), {Platoons} platoons, {Generators} generators";
    }

    /// <param name="partNames">DS3 part name -> ported part name, for every enemy and object that was placed.</param>
    public static Result Convert(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, IReadOnlyDictionary<string, string> partNames)
    {
        var notes = new List<string>();
        var leftOut = new Dictionary<string, int>();
        // DS3 region name -> ported region name (names must be unique in an Elden Ring map file: references are by name).
        var regionNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = msb.Regions.GetEntries().Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        int nextRegionId = msb.Regions.GetEntries().Select(r => r.RegionID).DefaultIfEmpty(-1).Max() + 1;
        int regions = 0, withEntity = 0, routePoints = 0;

        string Unique(string name)
        {
            if (string.IsNullOrEmpty(name))
                name = "DS3 region";
            var unique = name;
            for (int n = 2; !taken.Add(unique); n++)
                unique = $"{name} ({n})";
            return unique;
        }

        void Fill(MSB3.Region src, MSBE.Region region)
        {
            region.Name = Unique(src.Name);
            regionNames.TryAdd(src.Name, region.Name);
            region.Shape = src.Shape.DeepCopy();
            region.Position = src.Position;
            region.Rotation = src.Rotation;
            region.RegionID = nextRegionId++;
            region.MapStudioLayer = 0xFFFFFFFF;
            region.MapID = -1;
            region.UnkE08 = 255;
            region.UnkS04 = 0;
            region.UnkS0C = -1;
            region.EntityID = src.EntityID > 0 ? (uint)ds3Id.MoveEntity(src.EntityID, erId) : 0;
            // A region tied to a part only exists while that part does; one tied to a part that wasn't ported stays on.
            region.ActivationPartName = src.ActivationPartName is { Length: > 0 } part && partNames.TryGetValue(part, out var ported) ? ported : null;
            regions++;
            if (region.EntityID > 0)
                withEntity++;
        }

        foreach (var src in ds3.Regions.GetEntries())
        {
            switch (src)
            {
                case MSB3.Region.Event or MSB3.Region.ActivationArea or MSB3.Region.WarpPoint or MSB3.Region.Logic or MSB3.Region.Other:
                {
                    var region = new MSBE.Region.Other();
                    Fill(src, region);
                    msb.Regions.Others.Add(region);
                    break;
                }
                case MSB3.Region.SpawnPoint:
                {
                    var region = new MSBE.Region.SpawnPoint();
                    Fill(src, region);
                    msb.Regions.SpawnPoints.Add(region);
                    break;
                }
                case MSB3.Region.PatrolRoute or MSB3.Region.MovementPoint:
                {
                    var region = new MSBE.Region.PatrolRoute { UnkT00 = -1 };
                    Fill(src, region);
                    msb.Regions.PatrolRoutes.Add(region);
                    routePoints++;
                    break;
                }
                default:
                {
                    var kind = src.GetType().Name;
                    leftOut[kind] = leftOut.GetValueOrDefault(kind) + 1;
                    break;
                }
            }
        }

        // ---- Map-file events ----
        var eventNames = msb.Events.GetEntries().Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        int nextEventId = msb.Events.GetEntries().Select(e => e.EventID).DefaultIfEmpty(-1).Max() + 1;

        void FillEvent(MSB3.Event src, MSBE.Event e)
        {
            var name = string.IsNullOrEmpty(src.Name) ? e.GetType().Name : src.Name;
            var unique = name;
            for (int n = 2; !eventNames.Add(unique); n++)
                unique = $"{name} ({n})";
            e.Name = unique;
            e.EventID = nextEventId++;
            e.EntityID = src.EntityID > 0 ? (uint)ds3Id.MoveEntity(src.EntityID, erId) : 0;
            e.PartName = src.PartName is { Length: > 0 } part && partNames.TryGetValue(part, out var ported) ? ported : null;
            e.RegionName = src.PointName is { Length: > 0 } point && regionNames.TryGetValue(point, out var region) ? region : null;
            // JortPob's events (MakePart.PatrolRandom): no map ID, UnkE0C 255, UnkS0C -1.
            e.MapID = -1;
            e.Unk14 = 0;
            e.UnkE0C = 255;
            e.UnkS04 = 0;
            e.UnkS08 = 0;
            e.UnkS0C = -1;
        }

        // Patrol routes. An enemy names its route by the event's name.
        var patrolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        int patrols = 0, emptyPatrols = 0;
        foreach (var src in ds3.Events.PatrolInfo)
        {
            var points = src.WalkPointNames.Where(n => !string.IsNullOrEmpty(n))
                .Select(n => regionNames.GetValueOrDefault(n!)).Where(n => n is not null).ToList();
            if (points.Count == 0)
            {
                emptyPatrols++;
                continue;
            }
            var patrol = new MSBE.Event.PatrolInfo();
            FillEvent(src, patrol);
            // DS3's route kind (UnkT00: 0 walk there and back, 1 and 2 loops in the files seen by Smithbox) is the
            // same byte Elden Ring calls PatrolType.
            patrol.PatrolType = (byte)Math.Clamp(src.UnkT00, 0, 255);
            for (int i = 0; i < points.Count && i < patrol.WalkRegionNames.Length; i++)
                patrol.WalkRegionNames[i] = points[i];
            msb.Events.PatrolInfo.Add(patrol);
            patrolNames.TryAdd(src.Name, patrol.Name);
            patrols++;
        }
        if (emptyPatrols > 0)
            notes.Add($"{emptyPatrols} patrol routes left out (none of their points were carried)");

        // Enemies take their DS3 route.
        int walking = 0;
        var byName = msb.Parts.Enemies.ToDictionary(e => e.Name, StringComparer.Ordinal);
        foreach (var src in ds3.Parts.Enemies)
        {
            if (string.IsNullOrEmpty(src.WalkRouteName) || !patrolNames.TryGetValue(src.WalkRouteName, out var route))
                continue;
            if (partNames.TryGetValue(src.Name, out var ported) && byName.TryGetValue(ported, out var enemy))
            {
                enemy.WalkRouteName = route;
                walking++;
            }
        }

        // Platoons: enemies that are alerted together.
        int platoons = 0;
        foreach (var src in ds3.Events.PlatoonInfo)
        {
            var members = src.GroupPartsNames.Where(n => !string.IsNullOrEmpty(n))
                .Select(n => partNames.GetValueOrDefault(n!)).Where(n => n is not null).ToList();
            if (members.Count == 0)
                continue;
            var platoon = new MSBE.Event.PlatoonInfo();
            FillEvent(src, platoon);
            platoon.PlatoonIDScriptActivate = src.PlatoonIDScriptActivate;
            platoon.State = src.State;
            for (int i = 0; i < members.Count && i < platoon.GroupPartsNames.Length; i++)
                platoon.GroupPartsNames[i] = members[i];
            msb.Events.PlatoonInfo.Add(platoon);
            platoons++;
        }

        // Generators: enemies that keep arriving (DS3's rats, slimes, ambush waves).
        int generators = 0;
        foreach (var src in ds3.Events.Generators)
        {
            var parts = src.SpawnPartNames.Where(n => !string.IsNullOrEmpty(n))
                .Select(n => partNames.GetValueOrDefault(n!)).Where(n => n is not null).ToList();
            var points = src.SpawnPointNames.Where(n => !string.IsNullOrEmpty(n))
                .Select(n => regionNames.GetValueOrDefault(n!)).Where(n => n is not null).ToList();
            // A generator with nobody to spawn does nothing; one with no point spawns at the enemy's own spot.
            if (parts.Count == 0)
                continue;
            var generator = new MSBE.Event.Generator();
            FillEvent(src, generator);
            generator.MaxNum = src.MaxNum;
            generator.GenType = src.GenType;
            generator.LimitNum = src.LimitNum;
            generator.MinGenNum = src.MinGenNum;
            generator.MaxGenNum = src.MaxGenNum;
            generator.MinInterval = src.MinInterval;
            generator.MaxInterval = src.MaxInterval;
            generator.InitialSpawnCount = src.InitialSpawnCount;
            generator.UnkT14 = src.UnkT14;
            generator.UnkT18 = src.UnkT18;
            for (int i = 0; i < points.Count && i < generator.SpawnRegionNames.Length; i++)
                generator.SpawnRegionNames[i] = points[i];
            for (int i = 0; i < parts.Count && i < generator.SpawnPartNames.Length; i++)
                generator.SpawnPartNames[i] = parts[i];
            msb.Events.Generators.Add(generator);
            generators++;
        }

        return new Result(regions, withEntity, routePoints, patrols, walking, platoons, generators, leftOut, notes);
    }

    /// <summary>First map-local number for warp-point player parts whose own number is taken by another player part.</summary>
    public const int WarpPointSpare = 9900;

    /// <summary>
    /// Makes every DS3 point the scripts warp the player to (or set as a respawn point) a player part: Elden Ring's
    /// "Warp Player" and respawn points land on player parts, and DS3 mostly names map regions (Vordt's
    /// post-cutscene point 3002810, Friede's 4502810). A warp to a region lands on the map's default start instead,
    /// which is what sent Devin back to the High Wall's start at Vordt's door (1.0.383). Each gets a player part at the
    /// region's spot and turn (a copy of the layout's first player part) with the region's entity ID; the region
    /// gives the ID up, so the ID names one thing. DS3 points that are already player parts are left as they are.
    /// </summary>
    /// <param name="warpPoints">DS3 entity IDs the scripts use as warp destinations (<see cref="EventPort.WarpPoints"/>).</param>
    public static int AddWarpPoints(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, IEnumerable<int> warpPoints, List<string> notes)
    {
        var template = msb.Parts.Players.FirstOrDefault();
        if (template is null)
        {
            notes.Add("warp points: the layout has no player part to copy; DS3's warps land on the map's start");
            return 0;
        }
        var names = msb.Parts.GetEntries().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0, spare = WarpPointSpare;
        foreach (var id in warpPoints.Where(w => ds3Id.OwnsDs3Id(w)).Distinct().Order())
        {
            var entity = (uint)ds3Id.MoveEntity(id, erId);
            if (msb.Parts.Players.Any(p => p.EntityID == entity))
                continue;
            var src = ds3.Regions.GetEntries().FirstOrDefault(r => r.EntityID == id);
            if (src is null)
            {
                notes.Add($"warp points: {id} is not a region or player part of {ds3Id}; warps to it land on the start");
                continue;
            }
            var local = (int)(entity % 10_000);
            var name = $"c0000_{local:D4}";
            while (names.Contains(name) && spare < 10_000)
                name = $"c0000_{spare++:D4}";
            names.Add(name);
            var part = (MSBE.Part.Player)template.DeepCopy();
            part.Name = name;
            part.InstanceID = int.Parse(name[6..]);
            part.EntityID = entity;
            part.Position = src.Position;
            part.Rotation = new System.Numerics.Vector3(0, src.Rotation.Y, 0);
            msb.Parts.Players.Add(part);
            foreach (var r in msb.Regions.GetEntries().Where(r => r.EntityID == entity))
                r.EntityID = 0;
            added++;
        }
        if (added > 0)
            notes.Add($"warp points: {added} DS3 warp destinations are player parts now");
        return added;
    }

    /// <summary>
    /// Takes the regions and map-file events this step added back out (fallback "map-regions" B, or when the layout
    /// with them fails its write/read check): the map is then as it was before the step existed.
    /// </summary>
    public static void Strip(MSBE msb)
    {
        var environment = msb.Regions.EnvironmentMapPoints.ToList();
        var boxes = msb.Regions.EnvironmentMapEffectBoxes.ToList();
        msb.Regions.Others.Clear();
        msb.Regions.SpawnPoints.Clear();
        msb.Regions.PatrolRoutes.Clear();
        msb.Events.PatrolInfo.Clear();
        msb.Events.PlatoonInfo.Clear();
        msb.Events.Generators.Clear();
        foreach (var enemy in msb.Parts.Enemies)
            enemy.WalkRouteName = "";
        // Lighting's regions stay.
        msb.Regions.EnvironmentMapPoints = environment;
        msb.Regions.EnvironmentMapEffectBoxes = boxes;
    }
}
