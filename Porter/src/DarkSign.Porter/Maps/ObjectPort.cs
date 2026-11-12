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
/// Stage 1 (this class as it stands): every object is drawn. Collision is ported only for objects that never move
/// or break (no animations in the objbnd, no hit points, not the target of an ObjAct); doors, levers, lifts and
/// crates can be walked through until their animations and breaking are ported, so none of them can block the way.
/// </summary>
public sealed class ObjectPort
{
    public enum Method { Full, NoCollision, None }

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
        Parallel.ForEach(wanted, Parallelism.Options, asset =>
        {
            asset.Tried = true;
            try
            {
                Convert(asset);
                asset.Converted = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                asset.Failure = ex.Message;
            }
        });

        return PlaceParts(ds3, msb, ds3Id, erId, options, m => _assets.GetValueOrDefault(m));
    }

    /// <summary>Adds an asset part for every DS3 object part whose model <paramref name="lookup"/> has converted.</summary>
    public static Result PlaceParts(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, MsbPort.Options options, Func<string, Asset?> lookup)
    {
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
        if (otherState > 0)
            notes.Add($"{otherState} object parts left out: they belong to another state of the map (off DS3's layer bit 0, e.g. the Untended Graves)");
        return new Result(placed, ds3.Parts.Objects.Select(o => o.ModelName).Distinct(StringComparer.OrdinalIgnoreCase).Count(m => lookup(m)?.Converted == true),
            leftOut + otherState, withCollision, partNames, notes);
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

    private void Convert(Asset asset)
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
        }

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
            asset.CollisionNote = "none yet: the object moves (animations or an ObjAct); it would block the way";
        else if (asset.Breakable || asset.Ladder)
            asset.CollisionNote = asset.Ladder ? "none: a ladder" : "none yet: the object breaks; it would block the way";
        else
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
                    var converted = CollisionPort.Convert(hkx, _collisionTemplate, _xmlCollision, _keepSimd);
                    if (converted.Triangles == 0)
                    {
                        errors.Add($"{ItemText.Leaf(file.Name)}: no mesh shape");
                        continue;
                    }
                    Write(GeomHkxBndPath(asset.Name), DCX.Compress(CollisionBinder(asset.Name, converted.Bytes, _binderTemplate, _fileFlags).Write(), _compression));
                    asset.Collision = true;
                    asset.CollisionNote = $"{converted.Triangles} triangles from {ItemText.Leaf(file.Name)}";
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
    }

    public sealed record Converted(BND4 Binder, FLVER2 Flver, Dictionary<string, TPF.Texture> Textures);

    /// <summary>
    /// A DS3 objbnd's model as an Elden Ring asset binder, and the textures the objbnd carries. The model is the
    /// FLVER named after the object (its other FLVERs are broken pieces and low-detail copies).
    /// </summary>
    public static Converted ConvertBinder(BND4 objbnd, string ds3Model, string assetName, MapModelPort models, BND4 template, Binder.FileFlags flags)
    {
        var file = objbnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(ds3Model + ".flver", StringComparison.OrdinalIgnoreCase))
                   ?? objbnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes))
                   ?? throw new InvalidDataException("the objbnd holds no model");
        var flver = FLVER2.Read(file.Bytes);
        models.ConvertObject(flver, MaterialId);
        if (flver.Nodes.Count > 0 && flver.Nodes.All(n => !n.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)))
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
            Set(row, "behaviorType", (byte)(asset.Collision ? 1 : 0));
            // DS3's scripts register each ladder by its entity (Register Ladder, converted to Elden Ring's); the
            // asset row says the asset is one, as DS3's ObjectParam row did.
            Set(row, "isLadder", (byte)(asset.Ladder ? 1 : 0));
            assetParam.Rows.Add(row);
            added++;
        }
        assetParam.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
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
        regulation.Save(_ctx.ModPath("regulation.bin"));
        return $"object params: {added} AssetEnvironmentGeometryParam rows (copies of row {(hadTemplate ? TemplateRow : assetParam.Rows[0].ID)}), "
               + (drawParam is null ? "no draw distance rows" : $"draw distance rows {FirstDrawRow}-{FirstDrawRow + DrawBySize.Length - 1} by size");
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

        var report = new StringBuilder("DS3 object\tasset\tparts placed\tresult\tmeshes\tdummy points\tradius\tcollision\tmoves\tbreaks\tladder\tObjAct target\ttextures\tobjbnd holds\n");
        foreach (var a in tried)
            report.Append(string.Join('\t', a.Ds3Model, a.Name, a.Placed, a.Converted ? "ok" : "FAILED: " + Clean(a.Failure), a.Meshes, a.Dummies,
                a.Radius.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), Clean(a.CollisionNote), a.Animated ? 1 : 0, a.Breakable ? 1 : 0,
                a.Ladder ? 1 : 0, a.ObjActTarget ? 1 : 0, a.Textures.Count, Clean(a.Contents))).Append('\n');
        File.WriteAllText(_ctx.WorkPath("reports", ReportFile), report.ToString());

        var failed = tried.Where(a => !a.Converted).ToList();
        var summary = $"object port: {tried.Count(a => a.Converted)} of {tried.Count} DS3 object models are Elden Ring assets "
                      + $"{AssetName(FirstGroup, 0)} and up ({tried.Count(a => a.Collision)} with collision, "
                      + $"{tried.Count(a => a.Converted && (a.Animated || a.ObjActTarget))} that move and {tried.Count(a => a.Converted && a.Breakable)} that break have none yet); "
                      + $"report Work\\reports\\{ReportFile}";
        if (failed.Count > 0)
            summary += $"\n  FAIL {failed.Count} object models not converted: "
                       + string.Join("; ", failed.GroupBy(a => a.Failure ?? "?").OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Count()} x {g.Key}"));
        var noCollision = tried.Where(a => a.Converted && !a.Collision && a.CollisionNote.StartsWith("not converted")).ToList();
        if (noCollision.Count > 0)
            summary += $"\n  note: {noCollision.Count} standing objects have no collision (DS3's file didn't convert): e.g. {noCollision[0].Ds3Model}: {noCollision[0].CollisionNote}";
        foreach (var note in Notes.Distinct())
            summary += "\n  note: " + note;
        return summary;
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
