using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's bonfires as Elden Ring sites of grace: light it, rest at it (level up, flasks, memorise, pass time), and
/// respawn at it instead of at the last Elden Ring grace.
/// <para>
/// Both games build a rest point from the same four things, and the numbers line up (read from the game files,
/// 2026-11-19: DS3 m30/m40 and Elden Ring's Stormveil m10_00_00_00 and its BonfireWarpParam):
/// </para>
/// <list type="table">
/// <item><term>the thing registered</term><description>DS3: an object, entity xx01950+n (o000100, the sword in ash).
/// Elden Ring: an asset AEG099_060, entity xx001950+n.</description></item>
/// <item><term>the invisible talker</term><description>c1000, entity ..0950+n in both. DS3's carries the bonfire menu
/// script of its map (talk 300000...); Elden Ring's all carry talk 1000 (the grace menu, in the common talk binder),
/// NpcParam 10000000 and ThinkParam 1.</description></item>
/// <item><term>where the player stands</term><description>a player part: DS3 ..0970+n, Elden Ring ..0980+n. Elden Ring
/// finds it from the asset's number (1950+n -> 980+n in every Stormveil grace), so the number is not ours to choose
/// (JortPob Const.MAX_BEDS_PER_MSB: "entity ids being hardcoded to the range of 0950 to 0999").</description></item>
/// <item><term>the row</term><description>BonfireWarpParam: the flag that says it is lit, the registered entity, the map,
/// the name. DS3's row names the same flag its script registers the bonfire with (LocationEventId).</description></item>
/// </list>
/// Up to 1.0.46x the port carried DS3's "Register Bonfire" lines over (onto the sword asset) and nothing else: no
/// talker, no row, no point, so a bonfire was scenery.
/// <para>
/// What this step adds, for every bonfire DS3's BonfireWarpParam names in a ported map (and that has its talker in
/// the map's base state: the Untended Graves' two are left out with the rest of that state):
/// </para>
/// <list type="bullet">
/// <item>the talker (c1000, talk 1000) at DS3's talker, laid out like JortPob's interactor (infernoplus/JortPob
/// Common/MakePart.cs Bed), and the standing point 980+n at DS3's 970+n point;</item>
/// <item>the DS3 sword itself is the registered asset, with no Elden Ring asset and no golden light (an AEG099_060
/// stands in only where the map has no sword under the bonfire's entity). A bonfire's fire is its object's animations
/// in both games: the engine plays animation 1 on an unlit one and 10-103 on a lit one, and the animation's events
/// carry the effects (DS3 o000100 and Elden Ring's AEG099_060 have the same animation numbers and the same events,
/// read 2026-11-19; DS3's effects 1010, 1011, 1000-1002 are its bonfire's embers, fire and lighting burst, Elden
/// Ring's the grace's light under the same numbers). So the ported sword plays DS3's own fire, at DS3's size, from
/// its ported animations (<see cref="ObjectAnims"/>);</item>
/// <item>a BonfireWarpParam row (a copy of a Stormveil row; not drawn on the world map, whose coordinates mean nothing
/// in a DS3 map) with DS3's bonfire name in PlaceName;</item>
/// <item>"Register Bonfire" in the map script for any bonfire DS3 registers some way that didn't convert, and an event
/// that puts a flame on the sword while the bonfire's flag is on. DS3 lights its bonfires in the engine (no
/// ModelSfxParam row names o000100), so the flame is DS3's firewood fire (effect 800061, "Fire of firewood" in
/// DS3's ModelSfxParam) on dummy poly 200, where DS3's own script plays the bonfire's appearing effect.</item>
/// </list>
/// The menu's travel list is <see cref="BonfireTravel"/>.
/// </summary>
public static class Bonfires
{
    public const string GraceModel = "AEG099_060", TalkerModel = "c1000";

    /// <summary>The numbers both games give a bonfire's parts inside a map (plus n, the bonfire's own number).</summary>
    public const int AssetNumber = 1950, TalkerNumber = 950, Ds3PointNumber = 970, PointNumber = 980;

    /// <summary>Elden Ring's grace talker: the grace menu, the "Bonfire" NpcParam row and its think row.</summary>
    public const int GraceTalk = 1000, GraceNpc = 10000000, GraceThink = 1;

    /// <summary>DS3's "Fire of firewood" and the dummy poly DS3's script plays the bonfire's appearing effect on.</summary>
    public const int FlameEffect = 800061, FlameDummy = 200;

    /// <summary>
    /// The effects of DS3's bonfire object (o000100's animation events): the lighting burst, the two kindling bursts,
    /// the unlit embers, the fire, and the Untended Graves' dark one. <see cref="LitEffect"/> on <see cref="LitDummy"/>
    /// is the fire of every lit animation.
    /// </summary>
    public static readonly int[] OwnEffects = [1000, 1001, 1002, 1010, 1011, 1040];
    public const int LitEffect = 1011, LitDummy = 201;

    /// <summary>First map-local number tried for the flame events.</summary>
    public const int FirstEvent = 9960;

    public const string ParamName = "BonfireWarpParam";

    /// <summary>A row of DS3's BonfireWarpParam: the registered object, the lit flag, the name text and the row's own name.</summary>
    /// <param name="Id">The row's number: DS3 lists a tab's bonfires in row order.</param>
    /// <param name="List">DS3's warp-menu tab (ListId: 0 Cemetery of Ash, 1 High Wall...; 99 = not listed).</param>
    /// <param name="Picture">PictureId: the bonfire's picture in DS3's warp menu (a texture of menu/mapimage).</param>
    public sealed record Ds3Row(int Entity, long Flag, int NameId, string Name, int Id = 0, int List = 99, int Picture = -1);

    /// <summary>A DS3 bonfire of one map: n is its number (object 1950+n, talker 950+n, point 970+n).</summary>
    public sealed record Bonfire(int Number, int Ds3Entity, long Ds3Flag, int NameId, string Name, Vector3 Position, float Turn,
        Vector3 TalkerPosition, string? Collision)
    {
        /// <summary>DS3's warp-menu tab, the row's place in it and the tab's name ("High Wall of Lothric").</summary>
        public int List { get; init; } = 99;
        public int Order { get; init; }
        public string Area { get; init; } = "";
        /// <summary>The picture DS3's warp menu shows for it (BonfireWarpParam PictureId), -1 for none.</summary>
        public int Picture { get; init; } = -1;
    }

    /// <summary>What a bonfire became in the slot's map file.</summary>
    public sealed record Placed(Bonfire Source, uint Entity, uint Talker, uint Point, uint Sword, Vector3 PointPosition, bool GraceAsset);

    /// <summary>The rows of DS3's BonfireWarpParam.</summary>
    public static List<Ds3Row> Rows(PARAM ds3Param)
    {
        var rows = new List<Ds3Row>();
        foreach (var row in ds3Param.Rows)
        {
            long Cell(string field) => row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(field, StringComparison.OrdinalIgnoreCase))
                ?.Value is { } v and not byte[] ? System.Convert.ToInt64(v) : -1;
            long list = Cell("ListId");
            rows.Add(new Ds3Row((int)Cell("WarpEventId"), Cell("LocationEventId"), (int)Cell("BonfireNameId"), row.Name ?? "", row.ID, list < 0 ? 99 : (int)list,
                (int)Cell("PictureId")));
        }
        return rows;
    }

    /// <summary>"High Wall of Lothric - Knight's Rest" -> "Knight's Rest": Paramdex names the rows "area - bonfire".</summary>
    public static string NameOf(string rowName)
    {
        var at = rowName.LastIndexOf(" - ", StringComparison.Ordinal);
        return (at >= 0 ? rowName[(at + 3)..] : rowName).Trim();
    }

    /// <summary>"High Wall of Lothric - Knight's Rest" -> "High Wall of Lothric"; a name without " - " is its own area.</summary>
    public static string AreaOf(string rowName)
    {
        var at = rowName.IndexOf(" - ", StringComparison.Ordinal);
        return (at >= 0 ? rowName[..at] : rowName).Trim();
    }

    /// <summary>
    /// The bonfires of a DS3 map: the rows that name one of the map's bonfire entities (1950-1969) whose talker
    /// (950+n) stands in the map's base state. Rows without a talker are DS3's leftovers (m30 names 3001958, 3001959
    /// and an "Initial Warp" 3001960 whose sword stands at y -999). Where two rows name one bonfire (Firelink Shrine
    /// has a warp-list row and a place row), the first with a name wins.
    /// </summary>
    public static List<Bonfire> Find(IEnumerable<Ds3Row> rows, MSB3 ds3, MapId ds3Id)
    {
        var layerOk = MsbPort.BaseLayerMeans(ds3);
        var found = new SortedDictionary<int, Bonfire>();
        var all = rows.ToList();
        Ds3Row listed(Ds3Row row) => all.Where(r => r.Entity == row.Entity && r.List != 99).OrderBy(r => r.Id).FirstOrDefault() ?? row;
        foreach (var row in all)
        {
            int n = row.Entity - ds3Id.Ds3EntityBase - AssetNumber;
            if (n < 0 || n >= 20 || row.Flag <= 0 || found.ContainsKey(n))
                continue;
            var talker = ds3.Parts.Enemies.FirstOrDefault(e => e.EntityID == ds3Id.Ds3EntityBase + TalkerNumber + n
                && e.ModelName.Equals(TalkerModel, StringComparison.OrdinalIgnoreCase) && !MsbPort.OtherState(e, layerOk));
            if (talker is null)
                continue;
            var sword = ds3.Parts.Objects.FirstOrDefault(o => o.EntityID == row.Entity && !MsbPort.OtherState(o, layerOk));
            found[n] = new Bonfire(n, row.Entity, row.Flag, row.NameId, NameOf(row.Name), sword?.Position ?? talker.Position,
                sword?.Rotation.Y ?? talker.Rotation.Y, talker.Position, string.IsNullOrEmpty(talker.CollisionName) ? null : talker.CollisionName)
            {
                // Its place in DS3's warp menu: the listed row of the same bonfire (Firelink has an unlisted row first).
                List = listed(row).List, Order = listed(row).Id, Area = AreaOf(listed(row).Name),
                Picture = listed(row).Picture > 0 ? listed(row).Picture : row.Picture,
            };
        }
        return [.. found.Values];
    }

    /// <summary>
    /// Adds each bonfire's talker and standing point to the slot's map, and a grace asset where the map has no sword.
    /// Anything already holding one of the three entities gives it up, so each ID names one thing.
    /// </summary>
    /// <param name="partNames">DS3 part name -> ported part name (collisions), for the talker's floor.</param>
    public static List<Placed> Place(MSBE msb, MapId ds3Id, MapId erId, IReadOnlyList<Bonfire> bonfires,
        IReadOnlyDictionary<string, string>? partNames, List<string> notes)
    {
        var placed = new List<Placed>();
        if (bonfires.Count == 0)
            return placed;
        var names = msb.Parts.GetEntries().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = msb.Parts.GetEntries().Select(p => p.EntityID).Concat(msb.Regions.GetEntries().Select(r => r.EntityID))
            .Concat(msb.Events.GetEntries().Select(e => e.EntityID)).Where(e => e > 0).ToHashSet();
        uint Base(int number) => (uint)(erId.EntityBase + number);
        string Name(string model, int from)
        {
            int i = from;
            while (!names.Add($"{model}_{i:D4}"))
                i++;
            return $"{model}_{i:D4}";
        }
        var playerTemplate = msb.Parts.Players.FirstOrDefault();
        // The talker is seen from where the map's other enemies are (their groups are the port's choice: MsbPort.ApplyGroups).
        var likeEnemy = msb.Parts.Enemies.FirstOrDefault(e => !e.ModelName.Equals(TalkerModel, StringComparison.OrdinalIgnoreCase));
        foreach (var b in bonfires)
        {
            uint entity = Base(AssetNumber + b.Number), talkerId = Base(TalkerNumber + b.Number), pointId = Base(PointNumber + b.Number);
            if (pointId == (uint)MsbPort.StartEntityId(erId))
            {
                notes.Add($"bonfire {b.Ds3Entity} ({b.Name}) left out: its standing point would take the run's start point ({pointId})");
                continue;
            }
            // The sword (or whatever DS3 registers: Firelink's own bonfire object).
            var sword = msb.Parts.Assets.FirstOrDefault(a => a.EntityID == entity);
            bool graceAsset = sword is null;
            uint swordId = sword is null ? 0 : entity;
            if (graceAsset)
            {
                var grace = ObjectPort.NewPart();
                grace.Name = Name(GraceModel, 9000 + b.Number);
                grace.InstanceID = int.Parse(grace.Name[^4..]);
                grace.ModelName = GraceModel;
                grace.Position = b.Position;
                grace.Rotation = new Vector3(0, b.Turn, 0);
                grace.Scale = Vector3.One;
                grace.EntityID = entity;
                // Elden Ring's own graces name no part of their own and no PartsDrawParam row (Stormveil's nine).
                grace.AssetUnk2.Unk04 = -1;
                msb.Parts.Assets.Add(grace);
                if (msb.Models.Assets.All(m => m.Name != GraceModel))
                    msb.Models.Assets.Add(new MSBE.Model.Asset
                    {
                        Name = GraceModel,
                        SibPath = $@"N:\GR\data\Asset\Environment\geometry\AEG099\{GraceModel}\sib\{GraceModel}.sib",
                    });
                taken.Add(entity);
            }

            foreach (var other in msb.Parts.Enemies.Where(e => e.EntityID == talkerId))
                other.EntityID = 0;
            var talker = NewTalker();
            talker.Name = Name(TalkerModel, 9000 + b.Number);
            talker.InstanceID = int.Parse(talker.Name[^4..]);
            talker.Position = b.TalkerPosition;
            talker.Rotation = new Vector3(0, b.Turn, 0);
            talker.EntityID = talkerId;
            if (likeEnemy is not null)
                talker.Unk1 = likeEnemy.Unk1.DeepCopy();
            if (b.Collision is not null && partNames is not null && partNames.TryGetValue(b.Collision, out var floor)
                && msb.Parts.Collisions.Any(c => c.Name == floor))
                talker.CollisionPartName = floor;
            msb.Parts.Enemies.Add(talker);
            MsbPort.AddModel(msb.Models.Enemies, TalkerModel, msb.Models.Enemies.FirstOrDefault(), erId);
            taken.Add(talkerId);

            // Where the player stands after a rest, a respawn or a warp: DS3's point for this bonfire.
            var ds3Point = msb.Parts.Players.FirstOrDefault(p => p.EntityID == Base(Ds3PointNumber + b.Number));
            foreach (var other in msb.Parts.Players.Where(p => p.EntityID == pointId))
            {
                other.EntityID = 0;
                notes.Add($"bonfire {b.Ds3Entity} ({b.Name}): DS3's point {other.Name} had the standing point's entity {pointId} and gave it up; "
                          + "a DS3 warp to it lands at the bonfire");
            }
            foreach (var region in msb.Regions.GetEntries().Where(r => r.EntityID == pointId))
                notes.Add($"bonfire {b.Ds3Entity} ({b.Name}): region {region.Name} shares entity {pointId} with the standing point");
            var point = playerTemplate is null ? new MSBE.Part.Player() : (MSBE.Part.Player)playerTemplate.DeepCopy();
            point.Name = Name("c0000", 9000 + PointNumber + b.Number);
            point.InstanceID = int.Parse(point.Name[^4..]);
            point.ModelName = "c0000";
            point.EntityID = pointId;
            if (ds3Point is not null)
            {
                point.Position = ds3Point.Position;
                point.Rotation = ds3Point.Rotation;
            }
            else
            {
                // No DS3 point: a step in front of the bonfire, facing it.
                var turn = b.Turn * MathF.PI / 180f;
                point.Position = b.Position + new Vector3(MathF.Sin(turn), 0, MathF.Cos(turn)) * 1.5f;
                point.Rotation = new Vector3(0, b.Turn + 180f, 0);
            }
            msb.Parts.Players.Add(point);
            MsbPort.AddModel(msb.Models.Players, "c0000", msb.Models.Players.FirstOrDefault(), erId);
            taken.Add(pointId);
            placed.Add(new Placed(b, entity, talkerId, pointId, swordId, point.Position, graceAsset));
        }
        return placed;
    }

    /// <summary>
    /// The invisible c1000 a grace is used through, with the settings of JortPob's (Common/MakePart.cs Bed) and the
    /// three IDs every Stormveil grace talker has.
    /// </summary>
    public static MSBE.Part.Enemy NewTalker()
    {
        var enemy = new MSBE.Part.Enemy
        {
            ModelName = TalkerModel,
            NPCParamID = GraceNpc,
            ThinkParamID = GraceThink,
            TalkID = GraceTalk,
            CharaInitID = -1,
            IsShadowDest = true,
            MapStudioLayer = 0xFFFFFFFF,
            Scale = Vector3.One,
            UnkE0F = 1,
            UnkE3C = -1,
            UnkT84 = 1,
        };
        enemy.Gparam.FogParamID = -1;
        enemy.Gparam.LightSetID = -1;
        enemy.TileLoad.MapID = [255, 255, 255, 255];
        enemy.TileLoad.CullingHeightBehavior = -1;
        enemy.TileLoad.Unk0C = -1;
        enemy.Unk1.UnkC4 = -1;
        return enemy;
    }

    /// <summary>The BonfireWarpParam row of a slot's bonfire: map area * 10000 + block * 100 + n, as Elden Ring numbers its own.</summary>
    public static int RowId(MapId er, int number) => er.Area * 10_000 + er.Block * 100 + number;

    /// <summary>
    /// Writes a row for each placed bonfire: a copy of <paramref name="template"/> (a plain Stormveil grace) with the
    /// bonfire's flag, entity, map, standing point and name. Not drawn on the world map (dispMask00/01 = 0). With no
    /// Elden Ring asset there is no dummy poly for the unlit glow, so the row names none.
    /// </summary>
    /// <returns>Rows written (existing rows of the same ID are replaced).</returns>
    public static int WriteRows(PARAM param, PARAM.Row template, MapId er, IReadOnlyList<Placed> placed, Func<long, long> flag,
        Func<Placed, int> textId)
    {
        foreach (var p in placed)
        {
            int id = RowId(er, p.Source.Number);
            param.Rows.RemoveAll(r => r.ID == id);
            var row = new PARAM.Row(template) { ID = id, Name = $"[DS3] {p.Source.Name} ({p.Source.Ds3Entity})" };
            Set(row, "eventflagId", flag(p.Source.Ds3Flag));
            Set(row, "bonfireEntityId", p.Entity);
            Set(row, "areaNo", er.Area);
            Set(row, "gridXNo", er.Block);
            Set(row, "gridZNo", er.Region);
            Set(row, "posX", p.PointPosition.X);
            Set(row, "posY", p.PointPosition.Y);
            Set(row, "posZ", p.PointPosition.Z);
            Set(row, "dispMask00", 0);
            Set(row, "dispMask01", 0);
            Set(row, "clearedEventFlagId", 0);
            Set(row, "textId1", textId(p));
            for (int i = 2; i <= 8; i++)
            {
                Set(row, $"textId{i}", -1);
                Set(row, $"textEnableFlagId{i}", 0);
                Set(row, $"textDisableFlagId{i}", 0);
                Set(row, $"textType{i}", 0);
            }
            if (!p.GraceAsset)
            {
                Set(row, "noIgnitionSfxDmypolyId_0", -1);
                Set(row, "noIgnitionSfxId_0", -1);
                Set(row, "noIgnitionSfxDmypolyId_1", -1);
                Set(row, "noIgnitionSfxId_1", -1);
            }
            param.Rows.Add(row);
        }
        param.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return placed.Count;
    }

    private static void Set(PARAM.Row row, string field, object value)
    {
        var cell = row.Cells.FirstOrDefault(c => c.Def.InternalName == field);
        if (cell is not null && cell.Value is not byte[])
            cell.Value = System.Convert.ChangeType(value, cell.Value.GetType());
    }

    /// <summary>
    /// A plain grace row to copy: the first Stormveil row that is on the map, names no second text and has the unlit
    /// glow most rows have (Elden Ring has two, 6410 and 6411), else any row that names a grace.
    /// </summary>
    public static PARAM.Row? Template(PARAM param)
    {
        long Cell(PARAM.Row r, string field) => r.Cells.FirstOrDefault(c => c.Def.InternalName == field)?.Value is { } v and not byte[]
            ? System.Convert.ToInt64(v) : 0;
        var graces = param.Rows.Where(r => Cell(r, "bonfireEntityId") > 0 && Cell(r, "dispMask00") == 1).ToList();
        if (graces.Count == 0)
            return null;
        var glow = graces.GroupBy(r => Cell(r, "noIgnitionSfxId_0")).OrderByDescending(g => g.Count()).First().Key;
        return graces.FirstOrDefault(r => Cell(r, "areaNo") == 10 && Cell(r, "gridXNo") == 0 && Cell(r, "textId3") <= 0 && Cell(r, "noIgnitionSfxId_0") == glow)
               ?? graces.FirstOrDefault(r => Cell(r, "noIgnitionSfxId_0") == glow) ?? graces[0];
    }

    private const int RegisterBank = 2009, RegisterId = 3;

    /// <summary>
    /// Whether a converted script registers <paramref name="entity"/> as a bonfire: written in a "Register Bonfire"
    /// line, or passed to an event (the map's or a common one) that holds one (DS3's 20005500 shows a boss's bonfire
    /// when the boss is dead and registers it then).
    /// </summary>
    public static bool Registers(EMEVD script, EMEVD? common, uint entity)
    {
        var registering = script.Events.Concat(common?.Events ?? [])
            .Where(e => e.Instructions.Any(i => i.Bank == RegisterBank && i.ID == RegisterId)).Select(e => (uint)e.ID).ToHashSet();
        foreach (var e in script.Events)
            for (int at = 0; at < e.Instructions.Count; at++)
            {
                var i = e.Instructions[at];
                // A literal line: its entity is not filled in from an event parameter.
                if (i.Bank == RegisterBank && i.ID == RegisterId && i.ArgData.Length >= 8 && BitConverter.ToUInt32(i.ArgData, 4) == entity
                    && !e.Parameters.Any(p => p.InstructionIndex == at && p.TargetStartByte == 4))
                    return true;
                if (i.Bank == 2000 && i.ID is 0 or 6 && i.ArgData.Length >= 12)
                {
                    var ints = Enumerable.Range(0, i.ArgData.Length / 4).Select(k => BitConverter.ToUInt32(i.ArgData, k * 4)).ToList();
                    if (ints.Take(2).Any(registering.Contains) && ints.Skip(2).Contains(entity))
                        return true;
                }
            }
        return false;
    }

    /// <summary>
    /// Adds to a slot's converted script: "Register Bonfire" for each bonfire nothing registers yet, and for each
    /// bonfire with a sword a flame event (wait for the lit flag, put <paramref name="flameSfx"/> on the sword's
    /// dummy poly; started from event 0, so it runs again on every load).
    /// </summary>
    /// <returns>(registrations added, flame events added).</returns>
    public static (int Registered, int Flames) AddEvents(EMEVD script, EMEVD? common, MapId er, IReadOnlyList<Placed> placed,
        Func<long, long> flag, int flameSfx) =>
        AddEvents(script, common, er, placed, flag, _ => (flameSfx, FlameDummy));

    /// <param name="flame">The effect and dummy poly of a bonfire's flame event; an effect of 0 or less adds none (a
    /// sword that plays its own fire from its animations needs none).</param>
    public static (int Registered, int Flames) AddEvents(EMEVD script, EMEVD? common, MapId er, IReadOnlyList<Placed> placed,
        Func<long, long> flag, Func<Placed, (int Sfx, int Dummy)> flame)
    {
        if (placed.Count == 0)
            return (0, 0);
        var constructor = script.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
            script.Events.Insert(0, constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        int registered = 0, flames = 0;
        var eventIds = script.Events.Select(e => e.ID).ToHashSet();
        long nextEvent = er.EntityBase + FirstEvent;
        foreach (var p in placed)
        {
            var lit = (uint)flag(p.Source.Ds3Flag);
            if (!Registers(script, common, p.Entity))
            {
                // Elden Ring's own boss-grace event (common_func 9005810) registers with 5 m, 180 degrees.
                constructor.Instructions.Add(RunEvents.Make(RegisterBank, RegisterId, lit, p.Entity, 5f, 180f, 0, 0f));
                registered++;
            }
            var (flameSfx, flameDummy) = flame(p);
            if (p.Sword == 0 || flameSfx <= 0)
                continue;
            while (!eventIds.Add(nextEvent))
                nextEvent++;
            var e = new EMEVD.Event(nextEvent, EMEVD.Event.RestBehaviorType.Restart);
            e.Instructions.Add(RunEvents.Make(3, 0, (sbyte)0, (byte)1, (byte)0, lit));            // IF Event Flag (MAIN, ON, flag)
            e.Instructions.Add(RunEvents.Make(2006, 4, p.Sword, flameDummy, flameSfx));            // Create Asset-following SFX
            script.Events.Add(e);
            constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, (uint)nextEvent, 0u));
            flames++;
        }
        return (registered, flames);
    }

    private const string ErItems = "/msg/engus/item_dlc02.msgbnd.dcx";
    private static readonly string[] Ds3Menus = ["/msg/engus/menu_dlc2.msgbnd.dcx", "/msg/engus/menu.msgbnd.dcx"];

    /// <summary>DS3's text by ID across its menu binders (bonfire names live with the event text), first found wins.</summary>
    private static Dictionary<int, string> Ds3Names(GameArchives ds3)
    {
        var all = new Dictionary<int, string>();
        foreach (var path in Ds3Menus.Where(ds3.Contains))
            foreach (var f in BND4.Read(ds3.Read(path)).Files.Where(f => f.Name.EndsWith(".fmg", StringComparison.OrdinalIgnoreCase)))
                foreach (var e in FMG.Read(f.Bytes).Entries)
                    if (!string.IsNullOrWhiteSpace(e.Text))
                        all.TryAdd(e.ID, e.Text.Trim());
        return all;
    }

    /// <summary>
    /// The port-maps step, after the events step: every slot's map file, script and the regulation get their
    /// bonfires. Rows and text are written first, so a map never registers a bonfire that has no row.
    /// </summary>
    public static string Apply(PorterContext ctx, GameArchives ds3, GameArchives er, IReadOnlyList<MapSlots.Slot> slots,
        Func<MapId, IReadOnlyDictionary<string, string>?> partNames)
    {
        var ds3Rows = Rows(ctx.LoadParams(PorterGame.DarkSouls3).Get(ParamName) ?? throw new InvalidDataException($"DS3 has no {ParamName}"));
        var regulation = ActionButtons.LoadRegulation(ctx);
        var param = regulation.Get(ParamName) ?? throw new InvalidDataException($"Elden Ring has no {ParamName}");
        var template = Template(param) ?? throw new InvalidDataException($"{ParamName} has no grace row to copy");
        var ds3Defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var erDefs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("er-common")));
        var flags = FlagMap.Load(ctx.ModPath(MapEvents.FlagFile));
        var flameTable = MapSfx.ReadTable(ctx);
        Dictionary<int, string> ds3Names;
        try { ds3Names = Ds3Names(ds3); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ds3Names = new(); _ = ex; }

        // Text: DS3's bonfire names into PlaceName, under the row's own number (as Elden Ring's graces have it).
        var modItems = ctx.ModPath(MapPorter.ModParts(ErItems));
        var (vanillaItems, itemCompression) = er.ReadWithCompression(ErItems);
        var items = BND4.Read(File.Exists(modItems) ? DCX.Decompress(File.ReadAllBytes(modItems)) : vanillaItems);
        var placeFile = new[] { "PlaceName_dlc02", "PlaceName_dlc01", "PlaceName" }
            .Select(n => items.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith(n + ".", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(f => f is not null);
        var places = placeFile is null ? null : FMG.Read(placeFile.Bytes);
        var erPlaces = places is null ? new Dictionary<int, string>() : ItemText.Load(er, "PlaceName");

        var lines = new List<string>();
        var main = slots.Where(s => !s.Probe).Select(s => (s.Ds3, s.Er)).ToList();
        var work = new List<(MapSlots.Slot Slot, string MsbPath, MSBE Msb, DCX.CompressionInfo MsbCompression, List<Placed> Placed, Func<long, long> Flag)>();
        // A sword that is the bonfire burns by its own animations; one whose animations didn't port gets a flame event.
        var animated = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var swordModel = new Dictionary<uint, string>();
        bool Animated(string model)
        {
            if (animated.TryGetValue(model, out var known))
                return known;
            bool yes = false;
            try
            {
                var file = ctx.ModPath(MapPorter.ModParts(ObjectPort.GeomBndPath(model)));
                if (File.Exists(file))
                    yes = BND4.Read(DCX.Decompress(File.ReadAllBytes(file))).Files.Any(f => f.ID == 400
                        && ObjectAnims.BinderEffects(f.Bytes).Contains(flameTable.GetValueOrDefault(LitEffect, -1)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; }
            return animated[model] = yes;
        }
        int ownFire = 0;
        int rows = 0, named = 0;
        foreach (var slot in slots)
        {
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            if (!File.Exists(path) || !ds3.Contains(MapPorter.MsbPath(slot.Ds3)))
                continue;
            var ds3Msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(slot.Ds3)));
            var found = Find(ds3Rows, ds3Msb, slot.Ds3);
            if (found.Count == 0)
            {
                lines.Add($"bonfires: {slot.Ds3} -> {slot.Er}: DS3 names no bonfire with a talker in this map");
                continue;
            }
            // One map whose layout doesn't read back must not take every other map's bonfires with it (1.0.521: one
            // DS3 hit filter in four maps failed the whole step, and no bonfire anywhere could be rested at or lit).
            MSBE msb;
            DCX.CompressionInfo compression;
            try { msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out compression)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"bonfires: {slot.Ds3} -> {slot.Er}: WARN the ported layout doesn't read back ({ex.Message}); this map's {found.Count} bonfires are left as they are");
                continue;
            }
            var notes = new List<string>();
            var placed = Place(msb, slot.Ds3, slot.Er, found, partNames(slot.Er), notes);
            MSBE.Read(msb.Write());
            var maps = new List<(MapId, MapId)> { (slot.Ds3, slot.Er) };
            maps.AddRange(main.Where(m => m.Ds3 != slot.Ds3));
            var port = new EventPort(ds3Defs, erDefs, maps, flags);
            long Flag(long v) => port.Move(v, EventPort.Kind.Flag);
            int TextId(Placed p)
            {
                if (places is null)
                    return -1;
                int id = RowId(slot.Er, p.Source.Number);
                if (erPlaces.ContainsKey(id))
                    id += 8_000_000;
                var name = ds3Names.GetValueOrDefault(p.Source.NameId) is { Length: > 0 } text ? text : p.Source.Name;
                places.Entries.RemoveAll(e => e.ID == id);
                places.Entries.Add(new FMG.Entry(id, name));
                named++;
                return id;
            }
            foreach (var p in placed.Where(p => !p.GraceAsset && p.Sword != 0))
                if (msb.Parts.Assets.FirstOrDefault(a => a.EntityID == p.Sword) is { } swordPart)
                {
                    swordModel[p.Sword] = swordPart.ModelName;
                    if (Animated(swordPart.ModelName))
                        ownFire++;
                }
            rows += WriteRows(param, template, slot.Er, placed, Flag, TextId);
            work.Add((slot, path, msb, compression, placed, Flag));
            lines.Add($"bonfires: {slot.Ds3} -> {slot.Er}: {placed.Count} of {found.Count} DS3 bonfires are sites of grace ("
                      + string.Join(", ", placed.Select(p => $"{p.Source.Name} {p.Source.Ds3Entity}->{p.Entity}{(p.GraceAsset ? "" : " sword")}")) + ")");
            lines.AddRange(notes.Select(n => "bonfires: " + n));
        }
        if (work.Count == 0)
        {
            lines.Add("bonfire respawn: " + StartPointRespawns(ctx, slots));
            return lines.Count == 1 ? "bonfires: no ported map has one; " + lines[0] : string.Join(Environment.NewLine, lines);
        }

        if (places is not null && placeFile is not null)
        {
            places.Entries.Sort((a, b) => a.ID.CompareTo(b.ID));
            placeFile.Bytes = places.Write();
            File.WriteAllBytes(modItems, DCX.Compress(items.Write(), itemCompression));
        }
        regulation.Save(ctx.ModPath("regulation.bin"));

        // The bonfire menu: Elden Ring's grace menu with "Travel" (DS3's warp list) and "Return to the Lands Between".
        try
        {
            var destinations = work.SelectMany(w => w.Placed.Select(p => new BonfireTravel.Destination(w.Slot.Er, p.Source.Number,
                ds3Names.GetValueOrDefault(p.Source.NameId) is { Length: > 0 } text ? text : p.Source.Name,
                p.Source.Area.Length > 0 ? p.Source.Area : w.Slot.Ds3.ToString(), p.Source.List, p.Source.Order, (uint)w.Flag(p.Source.Ds3Flag), p.Point)
                { Picture = p.Source.Picture }));
            lines.Add(BonfireTravel.Write(ctx, er, destinations, ds3));
            foreach (var w in work)
                foreach (var p in w.Placed)
                    foreach (var talker in w.Msb.Parts.Enemies.Where(e => e.EntityID == p.Talker))
                        talker.TalkID = BonfireTravel.TalkId;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"bonfire travel: not added ({ex.GetType().Name}: {ex.Message}); the bonfires have Elden Ring's plain grace menu");
        }

        // Scripts: the common events the maps call are in the mod's common_func.
        var commonPath = ctx.ModPath(MapPorter.ModParts(MapEvents.CommonFunc));
        var common = File.Exists(commonPath) ? EMEVD.Read(DCX.Decompress(File.ReadAllBytes(commonPath))) : null;
        int registered = 0, flames = 0, noScript = 0, respawnEvents = 0, respawnMaps = 0;
        var startOnly = StartPointRespawns(ctx, slots.Where(s => work.All(w => w.Slot != s)));
        foreach (var w in work)
        {
            var scriptPath = ctx.ModPath(MapPorter.ModParts(MapEvents.ScriptPath(w.Slot.Er)));
            if (!File.Exists(scriptPath))
            {
                noScript++;
                continue;
            }
            var script = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(scriptPath), out var scriptCompression));
            var (r, f) = AddEvents(script, common, w.Slot.Er, w.Placed, w.Flag, p =>
                p.GraceAsset ? (0, 0)
                : swordModel.TryGetValue(p.Sword, out var model) && Animated(model) ? (0, 0)
                : (flameTable.GetValueOrDefault(LitEffect), LitDummy));
            registered += r;
            flames += f;
            // A death here returns to the last DS3 bonfire rested at, or the one nearest to where the player came in.
            respawnEvents += BonfireRespawn.AddEvents(script, w.Slot.Er, w.Placed.Select(p => p.Point).ToList());
            respawnMaps++;
            File.WriteAllBytes(scriptPath, DCX.Compress(script.Write(), scriptCompression));
            File.WriteAllBytes(w.MsbPath, DCX.Compress(w.Msb.Write(), w.MsbCompression));
        }
        lines.Add($"bonfires: {rows} {ParamName} rows, {named} names in {(placeFile is null ? "no PlaceName file (unnamed)" : ItemText.Leaf(placeFile.Name))}; "
                  + $"{registered} registered here (the rest by DS3's own lines), {flames} flame events"
                  + $"; DS3's swords are the registered bonfires, {ownFire} burn by their own animations (DS3 effects "
                  + string.Join(", ", OwnEffects.Select(e => flameTable.TryGetValue(e, out var c) ? $"{e} -> {c}" : $"{e} not converted")) + ")"
                  + (noScript > 0 ? $"; {noScript} maps have no script and stay without bonfires" : ""));
        lines.Add($"bonfire respawn: {respawnMaps} maps send a death to their nearest bonfire until the player rests ({respawnEvents} events; "
                  + $"flag {BonfireRespawn.FollowsFlag}, cleared by every warp in); " + startOnly);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Maps with no bonfire of their own (or whose layout didn't read back) respawn the player at the map's start
    /// point (<see cref="MsbPort.StartEntityId"/>, where the F1 warp lands) until they rest somewhere.
    /// </summary>
    private static string StartPointRespawns(PorterContext ctx, IEnumerable<MapSlots.Slot> slots)
    {
        var done = new List<string>();
        var none = new List<string>();
        foreach (var slot in slots)
        {
            var msbPath = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            var scriptPath = ctx.ModPath(MapPorter.ModParts(MapEvents.ScriptPath(slot.Er)));
            if (!File.Exists(msbPath) || !File.Exists(scriptPath))
                continue;
            uint start = (uint)MsbPort.StartEntityId(slot.Er);
            bool has;
            try { has = MSBE.Read(DCX.Decompress(File.ReadAllBytes(msbPath))).Parts.Players.Any(p => p.EntityID == start); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { has = false; _ = ex; }
            if (!has)
            {
                none.Add(slot.Er.ToString());
                continue;
            }
            var script = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(scriptPath), out var compression));
            BonfireRespawn.AddEvents(script, slot.Er, [start]);
            File.WriteAllBytes(scriptPath, DCX.Compress(script.Write(), compression));
            done.Add(slot.Er.ToString());
        }
        return $"{done.Count} maps without a bonfire use their start point ({string.Join(", ", done)})"
               + (none.Count > 0 ? $"; WARN {none.Count} have neither, a death there keeps the last respawn point ({string.Join(", ", none)})" : "");
    }
}
