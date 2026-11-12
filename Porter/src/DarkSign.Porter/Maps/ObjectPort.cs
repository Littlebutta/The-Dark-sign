using System.Numerics;
using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Ports DS3's map objects (obj/oNNNNNN.objbnd: statues, candles, crates, doors, ladders, corpses, the bonfire's
/// sword) into Elden Ring assets and places them where DS3's map files have them, with DS3's entity IDs moved to the
/// slot so the converted event scripts find them.
///
/// Elden Ring replaced DS3's objects with "assets". How one is built is taken from JortPob (infernoplus), the
/// Morrowind port, which makes thousands of them (Common/Bind.cs BindAsset, Common/MakePart.cs Asset,
/// Paramanager.cs GenerateAssetRows):
/// <list type="bullet">
/// <item>the model: asset/aeg/aegGGG/aegGGG_NNN.geombnd.dcx, a BND4 (07D7R6) holding the FLVER under file ID 200 as
/// N:\GR\data\INTERROOT_win64\asset\aeg\aegGGG\aegGGG_NNN\sib\aegGGG_NNN.flver;</item>
/// <item>its collision, when it has one: aegGGG_NNN_l.geomhkxbnd.dcx holding ...\hkx_L\AEGGGG_NNN_L.hkx under file
/// ID 300, in the same format as map collision (JortPob makes both with one converter);</item>
/// <item>a row GGGNNN in AssetEnvironmentGeometryParam, a copy of row 7077 (a stone ruin) with its draw distance
/// row, hitCreateType 0 (low collision only) and behaviorType 1 with collision, 0 without;</item>
/// <item>the map part: a new asset part with the settings of JortPob's generic asset (taken from AEG007_077).</item>
/// </list>
/// The model itself is converted like a map piece (<see cref="MapModelPort.ConvertObject"/>): the same cloned
/// materials, with the object's own textures (inside its objbnd) added to the map texture binder.
///
/// Every DS3 object model used by a map that has a slot gets a number, in name order, so each PC gives the same
/// model the same asset ID: aeg{first free group from 920}_{000-999}.
///
/// Every object is drawn. Objects that never move or break get their collision as it is. Objects that move (an
/// animation binder in the objbnd: doors, gates, lifts, levers, chests) get their animations and moving collision
/// (<see cref="ObjectAnims"/>), their meshes stay on their nodes (<see cref="MapModelPort.KeepNodes"/>), and the
/// DS3 map's ObjAct events (open this door, pull this lever) are placed with DS3's ObjActParam rows, moved to
/// <see cref="ObjActBase"/> + the DS3 ID (<see cref="AddObjActRows"/>). Lifts need nothing more: DS3's scripts drive
/// them ("Force Animation Playback" on the lift's entity, converted by <see cref="EventPort"/>), and their asset rows
/// say they carry the player (isMoveObj, from DS3's ObjectParam). Objects that break (hit points) still have no
/// collision, so none of them can block the way. Fallback "map-doors" (<see cref="MovingMethod"/>) turns this off.
/// </summary>
public sealed class ObjectPort
{
    public enum Method { Full, NoCollision, None }

    /// <summary>
    /// --moving-objects (launcher Fallbacks "map-doors"): Full moves doors, lifts and levers with their collision;
    /// NoCollision moves them but leaves them without collision (walk through them, as before); Off draws them
    /// standing still with no collision and no ObjAct, as builds up to 1.0.37x did.
    /// </summary>
    public enum MovingMethod { Full, NoCollision, Off }

    public static MovingMethod ParseMoving(string? text) => text?.ToLowerInvariant() switch
    {
        "nocollision" or "no-collision" or "b" => MovingMethod.NoCollision,
        "off" or "none" or "c" => MovingMethod.Off,
        _ => MovingMethod.Full,
    };

    /// <summary>
    /// Ported ObjActParam rows are DS3's at this base + the DS3 ID: Elden Ring already has 49 of DS3's 118 IDs (its own
    /// door 219000 among its 198 rows, which run 0-9,999,992), and nothing between 70,000,000 and 75,000,000.
    /// </summary>
    public const int ObjActBase = 70_000_000;

    /// <summary>The Elden Ring ObjActParam row of a DS3 one (0 and less stay: "none").</summary>
    public static long ObjActRow(long ds3Id) => ds3Id > 0 && ds3Id < 5_000_000 ? ObjActBase + ds3Id : ds3Id;

    /// <summary>Elden Ring's one-way door row (219000), the template the ported rows start from.</summary>
    public const int ObjActTemplateRow = 219000;

    /// <summary>
    /// Ported ObjAct events carry DS3's flag until the event scripts are converted (the flag numbers are given out
    /// there, <see cref="RemapObjActFlags"/>); their names start with this until then, and with "[DS3] " after.
    /// </summary>
    public const string PendingFlag = "[DS3 flag] ";

    public static Method ParseMethod(string? text) => text?.ToLowerInvariant() switch
    {
        "nocollision" or "no-collision" or "b" => Method.NoCollision,
        "none" or "off" or "c" => Method.None,
        _ => Method.Full,
    };

    /// <summary>The first asset group tried for ported objects; Elden Ring's own stop well below, JortPob uses 900-911.</summary>
    public const int FirstGroupWanted = 920;
    public const string ManifestFile = "darksign_object_files.txt";
    public const string TableFile = "darksign_objects.tsv";
    public const string ReportFile = "object-port.tsv";
    /// <summary>JortPob's template row, a stone ruin at the Church of Elleh.</summary>
    public const int TemplateRow = 7077;

    /// <summary>Ported materials are named DKS_{map}_{hash}; objects are shared by every map, so theirs carry no map.</summary>
    public static readonly MapId MaterialId = new(0, 0, 0, 0);

    /// <summary>One DS3 object model and the Elden Ring asset it becomes.</summary>
    public sealed class Asset
    {
        public required string Ds3Model { get; init; }
        public required int Index { get; init; }
        public required string Name { get; init; }          // "AEG920_001"
        public string Group => Name[..6];                   // "AEG920"
        public int Row => int.Parse(Name[3..6]) * 1000 + int.Parse(Name[7..10]);

        public bool Tried, Converted;
        public string? Failure;
        public bool Collision;
        public string CollisionNote = "";
        public int Meshes, Dummies, Placed;
        public float Radius;
        public bool Animated, Breakable, Ladder, ObjActTarget;
        /// <summary>It moves in Elden Ring: animations, nodes and moving collision were ported.</summary>
        public bool Moving;
        /// <summary>DS3's ObjectParam marks it as something the player rides (a lift).</summary>
        public bool MoveObj;
        public string MovingNote = "";
        public int Animations, MovingBodies;
        public string Contents = "";
        public List<string> Textures = [];
    }

    private readonly PorterContext _ctx;
    private readonly GameArchives _ds3, _er;
    private readonly MapModelPort _models;
    private readonly Method _method;
    private readonly PARAM? _objectParam;
    private readonly ErCollisionTemplate? _collisionTemplate;
    private readonly bool _xmlCollision, _keepSimd;
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _objActTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TPF.Texture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _written = [];
    private readonly BND4 _binderTemplate;
    private readonly DCX.CompressionInfo _compression;
    private readonly Binder.FileFlags _fileFlags;

    /// <summary>DS3's ObjActParam, for the rows ported ObjActs name (null: no ObjAct rows, so no ObjActs).</summary>
    public PARAM? Ds3ObjActParam { get; init; }
    public MovingMethod Moving { get; init; } = MovingMethod.Full;
    private ObjectAnims.Template? _animTemplate;
    private bool _animTemplateTried;

    public MapModelPort Models => _models;
    public IReadOnlyCollection<Asset> Assets => _assets.Values;
    public int FirstGroup { get; }
    public List<string> Notes { get; } = [];

    /// <param name="models">A fresh model port with the run's material templates; objects get their own materials.</param>
    /// <param name="ds3Maps">Every DS3 map file of a map that has a slot (not only the ones being ported now), so
    /// asset numbers don't depend on which maps a run rebuilds.</param>
    public ObjectPort(PorterContext ctx, GameArchives ds3, GameArchives er, MapModelPort models, Method method,
        IEnumerable<MSB3> ds3Maps, PARAM? ds3ObjectParam, ErCollisionTemplate? collisionTemplate, bool xmlCollision, bool keepSimd)
    {
        _ctx = ctx;
        _ds3 = ds3;
        _er = er;
        _models = models;
        _method = method;
        _objectParam = ds3ObjectParam;
        _collisionTemplate = collisionTemplate;
        _xmlCollision = xmlCollision;
        _keepSimd = keepSimd;

        var maps = ds3Maps.ToList();
        foreach (var msb in maps)
            foreach (var act in msb.Events.ObjActs)
                if (msb.Parts.Objects.FirstOrDefault(o => o.Name == act.ObjActPartName) is { } target)
                    _objActTargets.Add(target.ModelName);
        var names = Plan(maps.SelectMany(m => m.Parts.Objects).Select(o => o.ModelName));
        FirstGroup = FreeGroup(er.Entries.Select(e => e.Path), (names.Count + 999) / 1000);
        foreach (var (model, index) in names)
            _assets[model] = new Asset { Ds3Model = model, Index = index, Name = AssetName(FirstGroup, index) };

        // The binder settings and the FLVER entry's flags come from a vanilla asset when the game has it.
        _binderTemplate = new BND4 { Version = "07D7R6" };
        _compression = MapTextures.EntryCompression;
        _fileFlags = Binder.FileFlags.Flag1;
        try
        {
            if (er.Contains(MapPorter.TemplateAsset))
            {
                var (bytes, compression) = er.ReadWithCompression(MapPorter.TemplateAsset);
                _binderTemplate = BND4.Read(bytes);
                _compression = compression;
                if (_binderTemplate.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes)) is { } flver)
                    _fileFlags = flver.Flags;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Notes.Add($"vanilla asset {ItemText.Leaf(MapPorter.TemplateAsset)} unreadable ({ex.Message}); binder defaults used");
        }
    }

    /// <summary>A port that only knows these assets: for <see cref="AddRows"/> in the offline checks.</summary>
    public static ObjectPort ForRows(IEnumerable<Asset> assets) => new(assets);

    private ObjectPort(IEnumerable<Asset> assets)
    {
        _ctx = null!;
        _ds3 = null!;
        _er = null!;
        _models = null!;
        _binderTemplate = new BND4();
        _compression = MapTextures.EntryCompression;
        foreach (var asset in assets)
            _assets[asset.Ds3Model] = asset;
        FirstGroup = FirstGroupWanted;
    }

    /// <summary>Each distinct object model and its number, in name order.</summary>
    public static List<(string Model, int Index)> Plan(IEnumerable<string> models) =>
        models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.ToLowerInvariant()).Distinct()
            .OrderBy(m => m, StringComparer.Ordinal).Select((m, i) => (m, i)).ToList();

    public static string AssetName(int firstGroup, int index) => $"AEG{firstGroup + index / 1000:D3}_{index % 1000:D3}";

    /// <summary>
    /// The first run of <paramref name="count"/> asset groups from <see cref="FirstGroupWanted"/> up that Elden Ring
    /// has no file in (by the archive's file names). 920 when the names say nothing.
    /// </summary>
    public static int FreeGroup(IEnumerable<string?> archivePaths, int count)
    {
        count = Math.Max(1, count);
        var used = new HashSet<int>();
        foreach (var path in archivePaths)
        {
            if (path is null || !path.StartsWith("/asset/aeg/aeg", StringComparison.OrdinalIgnoreCase) || path.Length < 17)
                continue;
            if (int.TryParse(path.AsSpan(14, 3), out var group))
                used.Add(group);
        }
        for (int g = FirstGroupWanted; g + count <= 1000; g++)
            if (Enumerable.Range(g, count).All(x => !used.Contains(x)))
                return g;
        return FirstGroupWanted;
    }

    public sealed record Result(int Placed, int Models, int LeftOut, int WithCollision, IReadOnlyDictionary<string, string> PartNames,
        IReadOnlyList<string> Notes)
    {
        public string Summary => $"objects: {Placed} placed as assets ({Models} models, {WithCollision} parts with collision), {LeftOut} left out";
    }

    /// <summary>
    /// Converts the models this map's objects use (once per run) and adds an asset part for every DS3 object part.
    /// </summary>
    public Result Place(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, MsbPort.Options options)
    {
        var partNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();
        if (_method == Method.None)
            return new Result(0, 0, ds3.Parts.Objects.Count, 0, partNames, notes);

        var wanted = ds3.Parts.Objects.Select(o => o.ModelName).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => _assets.GetValueOrDefault(m)).Where(a => a is not null && !a.Tried).Select(a => a!).ToList();
        var moving = new System.Collections.Concurrent.ConcurrentBag<Asset>();
        Parallel.ForEach(wanted, Parallelism.Options, asset =>
        {
            asset.Tried = true;
            try
            {
                if (Convert(asset, movingPass: false))
                    asset.Converted = true;
                else
                    moving.Add(asset);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                asset.Failure = ex.Message;
            }
        });
        // Objects that move, one at a time: their animations go through HKLib under AnimPort's lock, while the
        // collision files above serialize without it (CollisionPort.LockFree), so the two must not overlap.
        foreach (var asset in moving.OrderBy(a => a.Index))
            try
            {
                Convert(asset, movingPass: true);
                asset.Converted = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                asset.Failure = ex.Message;
            }

        var result = PlaceParts(ds3, msb, ds3Id, erId, options, m => _assets.GetValueOrDefault(m), objActs: Ds3ObjActParam is not null);
        if (Ds3ObjActParam is null && Moving != MovingMethod.Off)
            return result with { Notes = [.. result.Notes, "no ObjActs placed: DS3's ObjActParam could not be read, so doors and levers can't be used"] };
        return result;
    }

    /// <summary>
    /// Adds an asset part for every DS3 object part whose model <paramref name="lookup"/> has converted, and an ObjAct
    /// event for every DS3 ObjAct whose object moves (<paramref name="objActs"/>: off for the offline checks of parts).
    /// </summary>
    public static Result PlaceParts(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, MsbPort.Options options, Func<string, Asset?> lookup, bool objActs = true)
    {
        var placedAssets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var partNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var names = msb.Parts.GetEntries().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextInstance = new Dictionary<string, int>();
        int placed = 0, leftOut = 0, withCollision = 0, otherState = 0;
        var failures = new Dictionary<string, int>();
        var layerOk = !options.AllLayers && MsbPort.BaseLayerMeans(ds3);
        foreach (var src in ds3.Parts.Objects)
        {
            if (MsbPort.OtherState(src, layerOk))
            {
                otherState++;
                continue;
            }
            var asset = lookup(src.ModelName);
            if (asset is null || !asset.Converted)
            {
                leftOut++;
                var why = asset?.Failure ?? "no asset number";
                failures[why] = failures.GetValueOrDefault(why) + 1;
                continue;
            }
            var part = NewPart();
            MsbPort.CopyCommon(src, part, ds3Id, erId);
            MsbPort.ApplyPieceGroups(src, part.Unk1, options);
            var instance = nextInstance.GetValueOrDefault(asset.Name, 9000);
            while (names.Contains($"{asset.Name}_{instance:D4}"))
                instance++;
            nextInstance[asset.Name] = instance + 1;
            part.Name = $"{asset.Name}_{instance:D4}";
            part.InstanceID = instance;
            part.ModelName = asset.Name;
            // JortPob's asset names itself in these three slots.
            part.UnkT54PartName = part.Name;
            part.UnkPartNames[4] = part.Name;
            part.UnkPartNames[5] = part.Name;
            names.Add(part.Name);
            partNames[src.Name] = part.Name;
            placedAssets[part.Name] = asset;
            msb.Parts.Assets.Add(part);
            if (msb.Models.Assets.All(m => m.Name != asset.Name))
                msb.Models.Assets.Add(new MSBE.Model.Asset
                {
                    Name = asset.Name,
                    SibPath = $@"N:\GR\data\Asset\Environment\geometry\{asset.Group}\{asset.Name}\sib\{asset.Name}.sib",
                });
            asset.Placed++;
            placed++;
            if (asset.Collision)
                withCollision++;
        }
        foreach (var f in failures.OrderByDescending(f => f.Value).Take(8))
            notes.Add($"{f.Value} object parts left out: {f.Key}");
        if (objActs)
            notes.Add(PlaceObjActs(ds3, msb, ds3Id, erId, partNames, placedAssets));
        if (otherState > 0)
            notes.Add($"{otherState} object parts left out: they belong to another state of the map (off DS3's layer bit 0, e.g. the Untended Graves)");
        return new Result(placed, ds3.Parts.Objects.Select(o => o.ModelName).Distinct(StringComparer.OrdinalIgnoreCase).Count(m => lookup(m)?.Converted == true),
            leftOut + otherState, withCollision, partNames, notes);
    }

    /// <summary>
    /// DS3's ObjAct events (open this door, pull this lever, open this chest) for the placed objects that move, laid
    /// out like Elden Ring's own (Stormveil's: no part, region or map of its own, UnkE0C 255, UnkS0C -1; the object
    /// by part name; the row by ObjActID; the state type DS3's, whose values mean the same in both: 0 a lever, 1 a
    /// one-way door, 4 a two-way door, 5 a chest). The ObjAct's flag stays DS3's until the scripts are converted
    /// (<see cref="RemapObjActFlags"/>). An object that doesn't move gets none: there would be nothing to open.
    /// </summary>
    public static string PlaceObjActs(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, IReadOnlyDictionary<string, string> partNames,
        IReadOnlyDictionary<string, Asset> placedAssets)
    {
        var eventNames = msb.Events.GetEntries().Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        int nextEventId = msb.Events.GetEntries().Select(e => e.EventID).DefaultIfEmpty(-1).Max() + 1;
        int placed = 0, noPart = 0, still = 0;
        uint Entity(int id) => id <= 0 ? 0 : (uint)(ds3Id.OwnsDs3Id(id) ? ds3Id.MoveEntity(id, erId) : id);
        foreach (var src in ds3.Events.ObjActs)
        {
            if (src.ObjActPartName is not { Length: > 0 } target || !partNames.TryGetValue(target, out var part))
            {
                noPart++;
                continue;
            }
            if (!placedAssets.TryGetValue(part, out var asset) || !asset.Moving)
            {
                still++;
                continue;
            }
            var baseName = string.IsNullOrEmpty(src.Name) ? $"ObjAct {part}" : src.Name;
            var name = PendingFlag + baseName;
            for (int n = 2; !eventNames.Add(name); n++)
                name = $"{PendingFlag}{baseName} ({n})";
            msb.Events.ObjActs.Add(new MSBE.Event.ObjAct
            {
                Name = name,
                EventID = nextEventId++,
                EntityID = Entity(src.EntityID),
                MapID = -1,
                UnkE0C = 255,
                UnkS0C = -1,
                ObjActEntityID = Entity(src.ObjActEntityID),
                ObjActPartName = part,
                ObjActID = (int)ObjActRow(src.ObjActParamID),
                StateType = (byte)src.ObjActStateType,
                EventFlagID = src.EventFlagID > 0 ? (uint)src.EventFlagID : 0,
            });
            placed++;
        }
        return $"ObjActs: {placed} placed" + (still > 0 ? $", {still} left out (their object doesn't move)" : "")
               + (noPart > 0 ? $", {noPart} left out (their object wasn't placed)" : "");
    }

    /// <summary>
    /// Gives the ObjAct events <see cref="PlaceObjActs"/> wrote into this map file their Elden Ring flags (DS3's door
    /// flags, e.g. 63000155, are global flags that get new numbers; a map's own move with the map), the way the
    /// scripts that wait on them were converted. Returns how many were changed.
    /// </summary>
    public static int RemapObjActFlags(PorterContext ctx, MapId erId, Func<long, long> moveFlag)
    {
        var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(erId)));
        if (!File.Exists(path))
            return 0;
        var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
        int changed = RemapObjActFlags(msb, moveFlag);
        if (changed > 0)
            File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
        return changed;
    }

    public static int RemapObjActFlags(MSBE msb, Func<long, long> moveFlag)
    {
        int changed = 0;
        foreach (var e in msb.Events.ObjActs.Where(e => e.Name?.StartsWith(PendingFlag, StringComparison.Ordinal) == true))
        {
            var moved = e.EventFlagID > 0 ? moveFlag(e.EventFlagID) : 0;
            e.EventFlagID = moved is > 0 and <= uint.MaxValue ? (uint)moved : 0;
            e.Name = "[DS3] " + e.Name[PendingFlag.Length..];
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// A new asset part with the settings of JortPob's generic asset (Common/MakePart.cs Asset, values "taken from
    /// a random stone ruin in the church of elleh area 'AEG007_077'"), which thousands of its placements share.
    /// </summary>
    public static MSBE.Part.Asset NewPart()
    {
        var asset = new MSBE.Part.Asset
        {
            AssetSfxParamRelativeID = -1,
            MapStudioLayer = 0xFFFFFFFF,
            IsShadowDest = true,
            UnkE0F = 1,
            UnkE3C = -1,
            UnkT12 = 255,
            UnkT1E = -1,
            UnkT24 = -1,
            UnkT30 = -1,
            UnkT34 = -1,
            UnkModelMaskAndAnimID = -1,
            UnkT5C = -1,
            UnkT60 = -1,
            UnkT64 = -1,
        };
        asset.Gparam.FogParamID = -1;
        asset.Gparam.LightSetID = -1;
        asset.Unk1.UnkC4 = -1;
        asset.Unk2.Condition = -1;
        asset.Unk2.Unk26 = -1;
        asset.TileLoad.MapID = [255, 255, 255, 255];
        asset.TileLoad.CullingHeightBehavior = -1;
        asset.Grass.Unk18 = -1;
        asset.AssetUnk1.Unk1C = -1;
        asset.AssetUnk1.Unk24 = -1;
        asset.AssetUnk1.Unk26 = -1;
        asset.AssetUnk1.Unk28 = -1;
        asset.AssetUnk1.Unk2C = -1;
        asset.AssetUnk2.Unk04 = 100;
        asset.AssetUnk2.Unk14 = -1f;
        asset.AssetUnk2.Unk1C = 255;
        asset.AssetUnk2.Unk1D = 255;
        asset.AssetUnk2.Unk1E = 255;
        asset.AssetUnk2.Unk1F = 255;
        asset.AssetUnk3.Unk04 = 64.808716f;
        asset.AssetUnk3.Unk09 = 255;
        asset.AssetUnk3.Unk0B = 255;
        asset.AssetUnk3.Unk0C = -1;
        asset.AssetUnk3.Unk10 = -1f;
        asset.AssetUnk3.DisableWhenMapLoadedMapID = [-1, -1, -1, -1];
        asset.AssetUnk3.Unk18 = -1;
        asset.AssetUnk3.Unk1C = -1;
        asset.AssetUnk3.Unk20 = -1;
        asset.AssetUnk3.Unk24 = 255;
        asset.AssetUnk4.Unk01 = 255;
        asset.AssetUnk4.Unk02 = 255;
        return asset;
    }

    public static string ObjBndPath(string model) => $"/obj/{model.ToLowerInvariant()}.objbnd.dcx";
    public static string GeomBndPath(string asset) => $"/asset/aeg/{asset[..6].ToLowerInvariant()}/{asset.ToLowerInvariant()}.geombnd.dcx";
    public static string GeomHkxBndPath(string asset) => $"/asset/aeg/{asset[..6].ToLowerInvariant()}/{asset.ToLowerInvariant()}_l.geomhkxbnd.dcx";

    /// <summary>
    /// Converts one object model. Returns false, having done nothing, when the object moves and this isn't the
    /// moving pass (<see cref="Place"/> converts those one at a time afterwards).
    /// </summary>
    private bool Convert(Asset asset, bool movingPass)
    {
        var path = ObjBndPath(asset.Ds3Model);
        if (!_ds3.Contains(path))
            throw new FileNotFoundException($"DS3 has no {path}");
        var bnd = BND4.Read(_ds3.Read(path));
        asset.Contents = Describe(bnd);
        asset.Animated = bnd.Files.Any(f => ItemText.Leaf(f.Name).EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase));
        asset.ObjActTarget = _objActTargets.Contains(asset.Ds3Model);
        if (_objectParam is not null && int.TryParse(asset.Ds3Model.AsSpan(1), out var objectId) && _objectParam[objectId] is { } row)
        {
            asset.Breakable = Number(row, "HP") >= 0 || Number(row, "isBreakByPlayerCollide") != 0 || Number(row, "isAnimBreak") != 0;
            asset.Ladder = Number(row, "isLadder") != 0;
            asset.MoveObj = Number(row, "isMoveObj") != 0;
        }

        // Objects that move: animations, nodes and moving collision (on the moving pass). Breaking objects stay
        // still (their animations are the breaking; ported, they would block the way while unbreakable).
        bool moves = asset.Animated && !asset.Breakable && !asset.Ladder && Moving != MovingMethod.Off;
        if (moves && !movingPass)
            return false;
        if (moves)
        {
            try
            {
                ConvertMoving(asset, bnd);
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                asset.Moving = false;
                asset.Animations = 0;
                asset.MovingBodies = 0;
                asset.MovingNote = $"stands still: {ex.Message}";
            }
        }
        else if (asset.Animated)
            asset.MovingNote = Moving == MovingMethod.Off ? "stands still (--moving-objects off)" : asset.Breakable ? "stands still: it breaks" : "stands still: a ladder";

        var result = ConvertBinder(bnd, asset.Ds3Model, asset.Name, _models, _binderTemplate, _fileFlags);
        asset.Meshes = result.Flver.Meshes.Count;
        asset.Dummies = result.Flver.Dummies.Count;
        asset.Radius = Vector3.Distance(result.Flver.Header.BoundingBoxMin, result.Flver.Header.BoundingBoxMax) / 2;
        asset.Textures = result.Textures.Keys.ToList();
        lock (_textures)
            foreach (var (name, texture) in result.Textures)
                _textures.TryAdd(name, texture);
        Write(GeomBndPath(asset.Name), DCX.Compress(result.Binder.Write(), _compression));

        // Collision, for objects that stay put.
        if (_method == Method.NoCollision)
            asset.CollisionNote = "off (--objects nocollision)";
        else if (asset.Animated || asset.ObjActTarget)
            asset.CollisionNote = "none: the object doesn't move here (see moves); it would block the way";
        else if (asset.Breakable || asset.Ladder)
            asset.CollisionNote = asset.Ladder ? "none: a ladder" : "none yet: the object breaks; it would block the way";
        else
            ConvertCollision(asset, bnd, null);
        return true;
    }

    /// <summary>
    /// The object's collision from its first DS3 collision file that converts: compressed meshes and convex hulls.
    /// <paramref name="adjust"/> renames and sets up moving bodies (<see cref="ObjectAnims.MovingBodies"/>).
    /// </summary>
    private void ConvertCollision(Asset asset, BND4 bnd, Action<HKLib.hk2018.hkRootLevelContainer, List<string>>? adjust)
    {
        var hkxFiles = bnd.Files.Where(f => ItemText.Leaf(f.Name).EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).ToList();
        if (hkxFiles.Count == 0)
            asset.CollisionNote = "none in DS3";
        var errors = new List<string>();
        foreach (var file in hkxFiles)
        {
            try
            {
                var hkx = DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes;
                var converted = CollisionPort.Convert(hkx, _collisionTemplate, _xmlCollision, _keepSimd, adjust, dropBroken: true);
                if (converted.Triangles == 0 && converted.Convex == 0)
                {
                    errors.Add($"{ItemText.Leaf(file.Name)}: no mesh or convex shape");
                    continue;
                }
                Write(GeomHkxBndPath(asset.Name), DCX.Compress(CollisionBinder(asset.Name, converted.Bytes, _binderTemplate, _fileFlags).Write(), _compression));
                asset.Collision = true;
                asset.CollisionNote = $"{converted.Triangles} triangles, {converted.Convex} convex bodies from {ItemText.Leaf(file.Name)}"
                                      + (converted.Notes.Count > 0 ? $" ({string.Join("; ", converted.Notes.Distinct().Take(2))})" : "");
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"{ItemText.Leaf(file.Name)}: {ex.Message}");
            }
        }
        if (!asset.Collision && errors.Count > 0)
            asset.CollisionNote = "not converted (" + string.Join("; ", errors.Take(3)) + ")";
    }

    /// <summary>
    /// An object that moves: the model with its meshes on their nodes, the animation binder (<see cref="ObjectAnims"/>)
    /// as file 400 of the asset binder, and collision whose bodies follow the bones they are named after. The DS3
    /// root's name ("o300310") becomes the asset name in the model, the skeleton and the collision alike, so they keep
    /// matching each other. Throws when the animations can't be ported (the caller then makes it a still object).
    /// </summary>
    private void ConvertMoving(Asset asset, BND4 bnd)
    {
        if (!_animTemplateTried)
            lock (_assets)
                if (!_animTemplateTried)
                {
                    try
                    {
                        _animTemplate = ObjectAnims.LoadTemplate(_er);
                        Notes.AddRange(_animTemplate.Notes);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Notes.Add($"moving objects: Elden Ring's door {ItemText.Leaf(ObjectAnims.TemplateAsset)} unreadable ({ex.Message}); doors and lifts stand still");
                    }
                    _animTemplateTried = true;
                }
        var template = _animTemplate ?? throw new InvalidDataException("no Elden Ring animation template");
        var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [asset.Ds3Model] = asset.Name };
        var anims = ObjectAnims.Build(bnd, asset.Name, template, rename);
        var result = ConvertBinder(bnd, asset.Ds3Model, asset.Name, _models, _binderTemplate, _fileFlags, rename, out var nodeNote);
        var nodeNames = result.Flver.Nodes.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatched = anims.Bones.Skip(1).Where(b => !nodeNames.Contains(b)).ToList();
        result.Binder.Files.Add(new BinderFile(_fileFlags, 400, ObjectAnims.AnibndName(asset.Name), anims.Anibnd));
        asset.Moving = true;
        asset.Animations = anims.Animations;
        asset.MovingNote = anims.Summary + (nodeNote is null ? "" : "; " + nodeNote)
                           + (unmatched.Count > 0 ? $"; bones with no model node: {string.Join(" ", unmatched.Take(4))}" : "");
        asset.Meshes = result.Flver.Meshes.Count;
        asset.Dummies = result.Flver.Dummies.Count;
        asset.Radius = Vector3.Distance(result.Flver.Header.BoundingBoxMin, result.Flver.Header.BoundingBoxMax) / 2;
        asset.Textures = result.Textures.Keys.ToList();
        lock (_textures)
            foreach (var (name, texture) in result.Textures)
                _textures.TryAdd(name, texture);
        Write(GeomBndPath(asset.Name), DCX.Compress(result.Binder.Write(), _compression));

        if (_method == Method.NoCollision)
            asset.CollisionNote = "off (--objects nocollision)";
        else if (Moving == MovingMethod.NoCollision)
            asset.CollisionNote = "off (--moving-objects nocollision)";
        else
        {
            var movingBones = anims.Bones.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int bodies = 0;
            ConvertCollision(asset, bnd, (root, notes) => bodies = ObjectAnims.MovingBodies(root, movingBones, rename, template, notes));
            asset.MovingBodies = asset.Collision ? bodies : 0;
            if (asset.Collision)
                asset.CollisionNote += $", {bodies} moving";
        }
    }

    public sealed record Converted(BND4 Binder, FLVER2 Flver, Dictionary<string, TPF.Texture> Textures);

    /// <summary>
    /// A DS3 objbnd's model as an Elden Ring asset binder, and the textures the objbnd carries. The model is the
    /// FLVER named after the object (its other FLVERs are broken pieces and low-detail copies).
    /// </summary>
    public static Converted ConvertBinder(BND4 objbnd, string ds3Model, string assetName, MapModelPort models, BND4 template, Binder.FileFlags flags) =>
        ConvertBinder(objbnd, ds3Model, assetName, models, template, flags, null, out _);

    /// <param name="moving">For an object that moves: its meshes stay on their nodes, and nodes are renamed by this
    /// (the DS3 root to the asset name, as in <see cref="ObjectAnims.Build"/>); a node named after the asset is added
    /// when none is, as Elden Ring's door has one. Null: a still object (meshes baked onto the root).</param>
    public static Converted ConvertBinder(BND4 objbnd, string ds3Model, string assetName, MapModelPort models, BND4 template, Binder.FileFlags flags,
        IReadOnlyDictionary<string, string>? moving, out string? nodeNote)
    {
        var file = objbnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(ds3Model + ".flver", StringComparison.OrdinalIgnoreCase))
                   ?? objbnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes))
                   ?? throw new InvalidDataException("the objbnd holds no model");
        var flver = FLVER2.Read(file.Bytes);
        if (moving is not null)
        {
            foreach (var node in flver.Nodes)
                if (node.Name is not null && moving.TryGetValue(node.Name, out var renamed))
                    node.Name = renamed;
            // Added last, so no vertex's or dummy's node number changes.
            if (flver.Nodes.Count > 0 && flver.Nodes.All(n => !assetName.Equals(n.Name, StringComparison.OrdinalIgnoreCase)))
                flver.Nodes.Add(new FLVER.Node { Name = assetName, ParentIndex = -1, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1 });
        }
        nodeNote = models.ConvertObject(flver, MaterialId, keepNodes: moving is not null);
        if (moving is null && flver.Nodes.Count > 0 && flver.Nodes.All(n => !n.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)))
            flver.Nodes[0].Name = assetName;

        var textures = new Dictionary<string, TPF.Texture>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in objbnd.Files.Where(f => ItemText.Leaf(f.Name).EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                foreach (var t in TPF.Read(DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes).Textures)
                    textures.TryAdd(t.Name, t);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An unreadable texture file leaves grey stand-ins, not a missing object.
            }
        }

        var lower = assetName.ToLowerInvariant();
        var binder = NewBinder(template);
        FlverMaterialNumbers.Apply(flver);
        binder.Files.Add(new BinderFile(flags, 200, $@"N:\GR\data\INTERROOT_win64\asset\aeg\{lower[..6]}\{lower}\sib\{lower}.flver", flver.Write()));
        return new Converted(binder, flver, textures);
    }

    /// <summary>The collision binder JortPob writes next to an asset: one low-detail .hkx under file ID 300.</summary>
    public static BND4 CollisionBinder(string assetName, byte[] hkx, BND4 template, Binder.FileFlags flags)
    {
        var upper = assetName.ToUpperInvariant();
        var binder = NewBinder(template);
        binder.Files.Add(new BinderFile(flags, 300, $@"N:\GR\data\INTERROOT_win64\asset\aeg\{upper[..6]}\{upper}\hkx_L\{upper}_L.hkx", hkx));
        return binder;
    }

    private static BND4 NewBinder(BND4 template) => new()
    {
        Version = template.Version, Format = template.Format, BigEndian = template.BigEndian, BitBigEndian = template.BitBigEndian,
        Unicode = template.Unicode, Extended = template.Extended, Unk04 = template.Unk04, Unk05 = template.Unk05,
    };

    /// <summary>"flver x2, tpf, hkx x3 (o302590_c.hkx ...), anibnd": what an objbnd holds, for the report.</summary>
    private static string Describe(BND4 bnd) => string.Join(", ", bnd.Files
        .GroupBy(f => Path.GetExtension(ItemText.Leaf(f.Name)).TrimStart('.').ToLowerInvariant())
        .Select(g => g.Key == "hkx" ? $"hkx ({string.Join(" ", g.Select(f => ItemText.Leaf(f.Name)).Take(4))})"
            : g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));

    private static double Number(PARAM.Row row, string field) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName == field)?.Value is { } v ? System.Convert.ToDouble(v) : 0;

    private void Write(string archivePath, byte[] bytes)
    {
        File.WriteAllBytes(_ctx.ModPath(MapPorter.ModParts(archivePath)), bytes);
        lock (_written)
            _written.Add(archivePath);
    }

    /// <summary>Hands the objects' own textures to an area's texture binder, and names the ones they use.</summary>
    public void GiveTextures(MapTextures area, MapId anyDs3Map)
    {
        lock (_textures)
            foreach (var (name, texture) in _textures)
                area.Provide(name, texture);
        area.Need(_models.Textures, anyDs3Map);
    }

    // ---- Params ----

    /// <summary>Draw distance by size, JortPob's table (Const.ASSET_LOD_VALUES) with every distance doubled: size, distance, fade.</summary>
    public static readonly (float Size, float Distance, float Fade)[] DrawBySize =
    [
        (1f, 64f, 8f), (3f, 128f, 16f), (7f, 256f, 32f), (14f, 512f, 64f), (22f, 1024f, 64f), (float.MaxValue, 99999f, 0f),
    ];
    /// <summary>The PartsDrawParam rows of <see cref="DrawBySize"/>, after the map pieces' row.</summary>
    public const short FirstDrawRow = MapDrawParams.FirstRow + 10;

    public static short DrawRowFor(float radius)
    {
        for (int i = 0; i < DrawBySize.Length; i++)
            if (radius < DrawBySize[i].Size)
                return (short)(FirstDrawRow + i);
        return (short)(FirstDrawRow + DrawBySize.Length - 1);
    }

    /// <summary>
    /// Adds the draw distance rows and one AssetEnvironmentGeometryParam row per converted object to the regulation
    /// (in memory; the caller saves it). Returns how many asset rows were added.
    /// </summary>
    public int AddRows(PARAM assetParam, PARAM? drawParam)
    {
        bool drawRows = false;
        if (drawParam is not null && (drawParam[MapDrawParams.TemplateRow] ?? drawParam.Rows.FirstOrDefault()) is { } drawTemplate)
        {
            drawParam.Rows.RemoveAll(r => r.ID >= FirstDrawRow && r.ID < FirstDrawRow + DrawBySize.Length);
            for (int i = 0; i < DrawBySize.Length; i++)
            {
                var row = new PARAM.Row(drawTemplate) { ID = FirstDrawRow + i, Name = $"DS3 object, size under {DrawBySize[i].Size:0}" };
                Set(row, "lv01_BorderDist", 99999f);
                Set(row, "lv01_PlayDist", 0f);
                Set(row, "drawDist", DrawBySize[i].Distance);
                Set(row, "drawFadeRange", DrawBySize[i].Fade);
                Set(row, "tex_lv01_BorderDist", 256f);
                Set(row, "tex_lv01_PlayDist", 32f);
                Set(row, "IncludeLodMapLv", (sbyte)0);
                Set(row, "lodType", (byte)1);
                Set(row, "DistantViewModel_BorderDist", 99999f);
                Set(row, "DistantViewModel_PlayDist", 0f);
                Set(row, "isShadowOnly", (sbyte)0);
                Set(row, "drawOnlyReflectCam", (sbyte)0);
                drawParam.Rows.Add(row);
            }
            drawParam.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
            drawRows = true;
        }

        var template = assetParam[TemplateRow] ?? assetParam.Rows.FirstOrDefault()
                       ?? throw new InvalidDataException("AssetEnvironmentGeometryParam has no rows to copy");
        var ours = _assets.Values.Select(a => a.Row).ToHashSet();
        assetParam.Rows.RemoveAll(r => ours.Contains(r.ID));
        int added = 0;
        foreach (var asset in _assets.Values.Where(a => a.Converted).OrderBy(a => a.Row))
        {
            var row = new PARAM.Row(template) { ID = asset.Row, Name = $"[DS3] {asset.Ds3Model}" };
            if (drawRows)
                Set(row, "refDrawParamId", (int)DrawRowFor(asset.Radius));
            Set(row, "hitCreateType", (sbyte)0);
            // Elden Ring's own moving assets (the door 219000, the lift 219020) have collision and behaviorType 0;
            // JortPob's still ones 1 with collision.
            Set(row, "behaviorType", (byte)(asset.Collision && !asset.Moving ? 1 : 0));
            // A lift: the player rides it (DS3's ObjectParam isMoveObj, Elden Ring's lift 219020 has it too).
            Set(row, "isMoveObj", (byte)(asset.Moving && asset.MoveObj ? 1 : 0));
            // DS3's scripts register each ladder by its entity (Register Ladder, converted to Elden Ring's); the
            // asset row says the asset is one, as DS3's ObjectParam row did.
            Set(row, "isLadder", (byte)(asset.Ladder ? 1 : 0));
            assetParam.Rows.Add(row);
            added++;
        }
        assetParam.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return added;
    }

    /// <summary>
    /// Adds every DS3 ObjActParam row at <see cref="ObjActBase"/> + its DS3 ID (ours from an earlier run replaced), as a
    /// copy of Elden Ring's door row with each field DS3 has of the same name (the two layouts differ only in the
    /// case of a few names and Elden Ring's added fields). The player's and object's animation IDs mean the same in
    /// both games (60000 open a door, 60200 pull a lever, 60080 open a chest; object animations 0 and 1). Changed:
    /// <list type="bullet">
    /// <item>an item requirement (spQualifiedType 1: DS3's keys, which aren't ported) is dropped, so locked doors
    /// open; the "only from this side" check (type 2) stays;</item>
    /// <item>DS3's "no flag" (-1) is Elden Ring's 0;</item>
    /// <item>a prompt row Elden Ring doesn't have becomes its door prompt (7200) or lever prompt (8301).</item>
    /// </list>
    /// Returns how many rows were added.
    /// </summary>
    public static int AddObjActRows(PARAM erParam, PARAM ds3Param, PARAM? actionButtons, List<string>? notes = null)
    {
        var template = erParam[ObjActTemplateRow] ?? erParam.Rows.FirstOrDefault() ?? throw new InvalidDataException("ObjActParam has no rows to copy");
        erParam.Rows.RemoveAll(r => r.ID >= ObjActBase && r.ID < ObjActBase + 5_000_000);
        var buttons = actionButtons?.Rows.Select(r => r.ID).ToHashSet();
        int added = 0, keys = 0, prompts = 0;
        foreach (var src in ds3Param.Rows.Where(r => r.ID > 0 && r.ID < 5_000_000))
        {
            var row = new PARAM.Row(template) { ID = (int)ObjActRow(src.ID), Name = $"[DS3] {src.ID} {src.Name}".TrimEnd() };
            foreach (var cell in row.Cells)
            {
                if (cell.Value is byte[])
                    continue;
                var from = src.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(cell.Def.InternalName, StringComparison.OrdinalIgnoreCase));
                if (from?.Value is null or byte[])
                    continue;
                try { cell.Value = System.Convert.ChangeType(from.Value, cell.Value.GetType()); }
                catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException) { }
            }
            foreach (var (type, id) in new[] { ("spQualifiedType", "spQualifiedId"), ("spQualifiedType2", "spQualifiedId2") })
                if (Number(row, type) == 1)
                {
                    Set(row, type, (byte)0);
                    Set(row, id, 0);
                    keys++;
                }
            if (Number(row, "spQualifiedPassEventFlag") < 0)
                Set(row, "spQualifiedPassEventFlag", 0);
            var button = (int)Number(row, "actionButtonParamId");
            if (buttons is not null && button > 0 && !buttons.Contains(button))
            {
                Set(row, "actionButtonParamId", button / 1000 % 10 == 8 ? 8301 : 7200);
                prompts++;
            }
            erParam.Rows.Add(row);
            added++;
        }
        erParam.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        notes?.Add($"ObjActParam: {added} DS3 rows at {ObjActBase:N0} + the DS3 ID ({keys} key requirements dropped: keys aren't ported, so those doors open without one; {prompts} prompts Elden Ring lacks use its door/lever prompt)");
        return added;
    }

    private static void Set(PARAM.Row row, string field, object value)
    {
        var cell = row.Cells.FirstOrDefault(c => c.Def.InternalName == field);
        if (cell is not null)
            cell.Value = System.Convert.ChangeType(value, cell.Value.GetType());
    }

    /// <summary>Writes the rows into the mod's regulation. Throws when it can't: the caller then takes the assets out of the maps.</summary>
    public string WriteParams()
    {
        var regulation = ActionButtons.LoadRegulation(_ctx);
        var assetParam = regulation.Get("AssetEnvironmentGeometryParam") ?? throw new InvalidDataException("Elden Ring has no AssetEnvironmentGeometryParam");
        PARAM? drawParam = null;
        try { drawParam = regulation.Get(MapDrawParams.ParamName); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Notes.Add($"PartsDrawParam unavailable ({ex.Message}); objects keep row {TemplateRow}'s draw distance"); }
        var hadTemplate = assetParam[TemplateRow] is not null;
        var added = AddRows(assetParam, drawParam);
        var objActNotes = new List<string>();
        if (Ds3ObjActParam is not null)
        {
            var objAct = regulation.Get("ObjActParam") ?? throw new InvalidDataException("Elden Ring has no ObjActParam");
            PARAM? buttons = null;
            try { buttons = regulation.Get("ActionButtonParam"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { objActNotes.Add($"ActionButtonParam unreadable ({ex.Message}); ObjAct prompts kept as DS3's"); }
            AddObjActRows(objAct, Ds3ObjActParam, buttons, objActNotes);
        }
        regulation.Save(_ctx.ModPath("regulation.bin"));
        return $"object params: {added} AssetEnvironmentGeometryParam rows (copies of row {(hadTemplate ? TemplateRow : assetParam.Rows[0].ID)}), "
               + (drawParam is null ? "no draw distance rows" : $"draw distance rows {FirstDrawRow}-{FirstDrawRow + DrawBySize.Length - 1} by size")
               + string.Concat(objActNotes.Select(n => "\n  " + n));
    }

    /// <summary>Takes every ported object out of a written map file (when their param rows couldn't be written).</summary>
    public static int Strip(PorterContext ctx, MapId erId)
    {
        var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(erId)));
        if (!File.Exists(path))
            return 0;
        var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
        var removed = Strip(msb);
        if (removed > 0)
            File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
        return removed;
    }

    /// <summary>Removes the asset parts and whatever names them; returns how many parts went.</summary>
    public static int Strip(MSBE msb)
    {
        var gone = msb.Parts.Assets.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        if (gone.Count == 0)
            return 0;
        msb.Parts.Assets.Clear();
        msb.Models.Assets.Clear();
        foreach (var region in msb.Regions.GetEntries().Where(r => r.ActivationPartName is not null && gone.Contains(r.ActivationPartName)))
            region.ActivationPartName = null;
        msb.Events.ObjActs.RemoveAll(e => e.ObjActPartName is not null && gone.Contains(e.ObjActPartName));
        msb.Events.Treasures.RemoveAll(e => e.TreasurePartName is not null && gone.Contains(e.TreasurePartName));
        foreach (var e in msb.Events.GetEntries().Where(e => e.PartName is not null && gone.Contains(e.PartName)))
            e.PartName = null;
        return gone.Count;
    }

    // ---- Files and reports ----

    /// <summary>Deletes the asset files an earlier run wrote (listed in <see cref="ManifestFile"/>).</summary>
    public static void CleanOutputs(PorterContext ctx)
    {
        var manifest = ctx.ModPath(ManifestFile);
        if (!File.Exists(manifest))
            return;
        foreach (var line in File.ReadAllLines(manifest))
        {
            if (!line.StartsWith("/asset/aeg/", StringComparison.OrdinalIgnoreCase) || line.Contains(".."))
                continue;
            try
            {
                var path = ctx.ModPath(MapPorter.ModParts(line));
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"  could not delete {line}: {ex.Message}");
            }
        }
        File.Delete(manifest);
        var table = ctx.ModPath(TableFile);
        if (File.Exists(table))
            File.Delete(table);
    }

    /// <summary>
    /// Writes the file list (for the next run's clean-up), darksign_objects.tsv (DS3 model -> asset, for the F1 menu
    /// and later port steps) and the report Work\reports\object-port.tsv (one line per model: what its objbnd holds,
    /// what was converted and why collision was or wasn't), and returns a summary.
    /// </summary>
    public string WriteReports()
    {
        // A partial run adds to what earlier runs wrote.
        var manifest = _ctx.ModPath(ManifestFile);
        var files = File.Exists(manifest) ? File.ReadAllLines(manifest).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        files.UnionWith(_written);
        File.WriteAllLines(manifest, files.Order(StringComparer.Ordinal));

        var tried = _assets.Values.Where(a => a.Tried).OrderBy(a => a.Index).ToList();
        File.WriteAllLines(_ctx.ModPath(TableFile),
            ["# DS3 object\tElden Ring asset\tasset row\tcollision (written by DarkSignPorter port-maps)",
             .. tried.Where(a => a.Converted).Select(a => $"{a.Ds3Model}\t{a.Name}\t{a.Row}\t{(a.Collision ? 1 : 0)}")]);

        var report = new StringBuilder("DS3 object\tasset\tparts placed\tresult\tmeshes\tdummy points\tradius\tcollision\tanimated\tmoves in Elden Ring\tlift\tbreaks\tladder\tObjAct target\ttextures\tobjbnd holds\n");
        foreach (var a in tried)
            report.Append(string.Join('\t', a.Ds3Model, a.Name, a.Placed, a.Converted ? "ok" : "FAILED: " + Clean(a.Failure), a.Meshes, a.Dummies,
                a.Radius.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), Clean(a.CollisionNote), a.Animated ? 1 : 0,
                a.Moving ? "yes: " + Clean(a.MovingNote) : Clean(a.MovingNote), a.MoveObj ? 1 : 0, a.Breakable ? 1 : 0,
                a.Ladder ? 1 : 0, a.ObjActTarget ? 1 : 0, a.Textures.Count, Clean(a.Contents))).Append('\n');
        File.WriteAllText(_ctx.WorkPath("reports", ReportFile), report.ToString());

        var failed = tried.Where(a => !a.Converted).ToList();
        var summary = $"object port: {tried.Count(a => a.Converted)} of {tried.Count} DS3 object models are Elden Ring assets "
                      + $"{AssetName(FirstGroup, 0)} and up ({tried.Count(a => a.Collision)} with collision, "
                      + $"{tried.Count(a => a.Converted && a.Breakable)} that break have none yet; "
                      + $"{tried.Count(a => a.Moving)} move: doors, levers, lifts ({tried.Count(a => a.Moving && a.MoveObj)} lifts, {tried.Count(a => a.Moving && a.MovingBodies > 0)} with moving collision), "
                      + $"{tried.Count(a => a.Converted && a.Animated && !a.Moving)} animated ones stand still); "
                      + $"report Work\\reports\\{ReportFile}";
        if (failed.Count > 0)
            summary += $"\n  FAIL {failed.Count} object models not converted: "
                       + string.Join("; ", failed.GroupBy(a => a.Failure ?? "?").OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Count()} x {g.Key}"));
        var noCollision = tried.Where(a => a.Converted && !a.Collision && a.CollisionNote.StartsWith("not converted")).ToList();
        if (noCollision.Count > 0)
            summary += $"\n  note: {noCollision.Count} standing objects have no collision (DS3's file didn't convert): e.g. {noCollision[0].Ds3Model}: {noCollision[0].CollisionNote}";
        var still = tried.Where(a => a.Converted && a.Animated && !a.Moving && a.MovingNote.StartsWith("stands still: ") && !a.Breakable && !a.Ladder).ToList();
        if (still.Count > 0)
            summary += $"\n  note: {still.Count} animated objects couldn't be made to move: "
                       + string.Join("; ", still.GroupBy(a => a.MovingNote).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Count()} x {g.Key} (e.g. {g.First().Ds3Model})"));
        foreach (var note in Notes.Distinct())
            summary += "\n  note: " + note;
        return summary;
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
