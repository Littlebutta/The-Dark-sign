using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Offline checks for what the map file carries besides geometry and enemies: DS3's regions and map-file events
/// (<see cref="RegionPort"/>) and DS3's objects as Elden Ring assets (<see cref="ObjectPort"/>).
/// </summary>
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> ObjectChecks()
    {
        yield return ("regions: DS3 script regions, spawn points and patrol points are carried with their entity IDs; emitters are left out", Regions);
        yield return ("regions: patrol routes, platoons and generators name the ported enemies and points", MapFileEvents);
        yield return ("map effects: DS3 SFX regions (Firelink's candles) become Elden Ring SFX regions naming the converted effect", MapEffects);
        yield return ("objects: models are numbered in name order into free asset groups", ObjectNumbers);
        yield return ("objects: a DS3 object model becomes an asset binder with its vertices in model space and its textures", ObjectModel);
        yield return ("objects: every DS3 object part becomes an asset part with its entity ID, and can be taken out again", ObjectParts);
        yield return ("objects: asset rows are copies of the template row with a draw distance by size", ObjectRows);
        yield return ("objects: a param def that differs from the game's only in its data version number still applies (1.0.326 lost every object to it)", DefVersionFit);
    }

    private static MSB3 Ds3WithRegions()
    {
        var ds3 = SyntheticDs3();
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c1240" });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1240_0000", ModelName = "c1240", EntityID = 4000100, NPCParamID = 124000, WalkRouteName = "route A", CollisionName = "h000100_0000" });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1240_0001", ModelName = "c1240", EntityID = 4000101, NPCParamID = 124000, CollisionName = "h000100_0000" });
        ds3.Regions.Events.Add(new MSB3.Region.Event { Name = "boss room", EntityID = 4002800, Position = new Vector3(5, 6, 7), Rotation = new Vector3(0, 45, 0), Shape = new MSB.Shape.Box { Width = 10, Depth = 20, Height = 5 } });
        ds3.Regions.Events.Add(new MSB3.Region.Event { Name = "boss room", EntityID = 4002801, Shape = new MSB.Shape.Sphere { Radius = 3 } });
        ds3.Regions.WarpPoints.Add(new MSB3.Region.WarpPoint { Name = "arrival", EntityID = 4002990 });
        ds3.Regions.SpawnPoints.Add(new MSB3.Region.SpawnPoint { Name = "spawn 1" });
        ds3.Regions.PatrolRoutes.Add(new MSB3.Region.PatrolRoute { Name = "p1", Position = new Vector3(1, 0, 0) });
        ds3.Regions.PatrolRoutes.Add(new MSB3.Region.PatrolRoute { Name = "p2", Position = new Vector3(2, 0, 0) });
        ds3.Regions.SFX.Add(new MSB3.Region.SFX { Name = "flame", EffectID = 30010 });
        ds3.Regions.Sounds.Add(new MSB3.Region.Sound { Name = "wind", SoundID = 5 });
        var patrol = new MSB3.Event.PatrolInfo { Name = "route A", UnkT00 = 1 };
        patrol.WalkPointNames[0] = "p1";
        patrol.WalkPointNames[1] = "p2";
        ds3.Events.PatrolInfo.Add(patrol);
        var empty = new MSB3.Event.PatrolInfo { Name = "route B" };
        empty.WalkPointNames[0] = "nowhere";
        ds3.Events.PatrolInfo.Add(empty);
        var platoon = new MSB3.Event.PlatoonInfo { Name = "guards", PlatoonIDScriptActivate = 7, State = 1 };
        platoon.GroupPartsNames[0] = "c1240_0000";
        platoon.GroupPartsNames[1] = "c1240_0001";
        ds3.Events.PlatoonInfo.Add(platoon);
        var generator = new MSB3.Event.Generator { Name = "rats", EntityID = 4003100, MaxNum = 3, LimitNum = -1, MinInterval = 2, MaxInterval = 4 };
        generator.SpawnPartNames[0] = "c1240_0001";
        generator.SpawnPointNames[0] = "spawn 1";
        ds3.Events.Generators.Add(generator);
        var nobody = new MSB3.Event.Generator { Name = "ghosts" };
        nobody.SpawnPartNames[0] = "c9999_0000";
        ds3.Events.Generators.Add(nobody);
        return ds3;
    }

    private static (MSBE Msb, RegionPort.Result Result) ConvertWithRegions()
    {
        var ds3 = Ds3WithRegions();
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var partNames = new Dictionary<string, string>();
        var chooser = new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto);
        EnemyPort.Place(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), chooser,
            e => new EnemyPort.Ds3Enemy(e.ModelName, e.NPCParamID, "Lothric Knight", false, false), null, null, partNames);
        Expect(partNames.Count == 2, $"both enemies placed and named ({partNames.Count})");
        var result = RegionPort.Convert(ds3, layout.Msb, Ds3Id, ErId, partNames);
        return (MSBE.Read(layout.Msb.Write()), result);
    }

    private static void Regions()
    {
        var (msb, result) = ConvertWithRegions();
        Expect(result.Regions == 6 && result.WithEntity == 3 && result.RoutePoints == 2, result.Summary);
        Expect(result.LeftOut.GetValueOrDefault("SFX") == 1 && result.LeftOut.GetValueOrDefault("Sound") == 1, "effect and sound emitters are counted as left out");
        var room = msb.Regions.Others.Single(r => r.EntityID == 45002800);
        Expect(room.Position == new Vector3(5, 6, 7) && room.Rotation == new Vector3(0, 45, 0), "spot and rotation kept");
        Expect(room.Shape is MSB.Shape.Box { Width: 10, Depth: 20, Height: 5 }, "shape kept");
        Expect(room.MapID == -1 && room.UnkE08 == 255 && room.UnkS0C == -1 && room.MapStudioLayer == 0xFFFFFFFF, "JortPob's script region settings");
        // Two DS3 regions with one name: references are by name, so the second gets its own.
        Expect(msb.Regions.Others.Single(r => r.EntityID == 45002801).Name != room.Name, "duplicate names are made unique");
        Expect(msb.Regions.Others.Any(r => r.EntityID == 45002990), "the warp point is a script region with its entity ID");
        Expect(msb.Regions.SpawnPoints.Count == 1 && msb.Regions.PatrolRoutes.Count == 2, "spawn and patrol points keep their kinds");
        var ids = msb.Regions.GetEntries().Select(r => r.RegionID).ToList();
        Expect(ids.Distinct().Count() == ids.Count, "every region has its own RegionID");
        Expect(msb.Regions.SFX.Count == 0 && msb.Regions.Sounds.Count == 0, "no emitter naming a DS3 effect or sound ID");

        RegionPort.Strip(msb);
        Expect(msb.Regions.GetEntries().Count == 0 && msb.Events.GetEntries().Count == 0 && msb.Parts.Enemies.All(e => string.IsNullOrEmpty(e.WalkRouteName)),
            "Strip takes the regions, events and routes out again");
        MSBE.Read(msb.Write());
        Expect(MapPorter.Options.Parse(["--no-regions"]).NoRegions && !new MapPorter.Options().NoRegions, "--no-regions is read");
    }

    private static void MapEffects()
    {
        var ds3 = Ds3WithRegions();
        ds3.Regions.SFX.Add(new MSB3.Region.SFX { Name = "candle", EffectID = 840060, EntityID = 4003500, Position = new Vector3(273, -49, 615), Rotation = new Vector3(0, 30, 0),
            Shape = new MSB.Shape.Point(), StartDisabled = true });
        ds3.Regions.SFX.Add(new MSB3.Region.SFX { Name = "candle", EffectID = 840060, EntityID = -1 });
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var converted = new Dictionary<int, int> { [840060] = FxrPort.IdBase + 840060 };
        var result = MapSfx.Convert(ds3, layout.Msb, Ds3Id, ErId, converted);
        Expect(result.Added == 2 && result.Missing == 1 && result.MissingByEffect.GetValueOrDefault(30010) == 1, $"two candles added, the unconverted flame left out ({result})");
        var msb = MSBE.Read(layout.Msb.Write());
        var candle = msb.Regions.SFX.Single(r => r.EntityID == 45003500);
        Expect(candle.EffectID == 900_840_060 && candle.StartDisabled == 1, $"effect {candle.EffectID} and start state kept");
        Expect(candle.Position == new Vector3(273, -49, 615) && candle.Rotation == new Vector3(0, 30, 0), "spot and rotation kept");
        Expect(candle.MapID == -1 && candle.UnkE08 == 255 && candle.UnkS0C == -1 && candle.MapStudioLayer == 0xFFFFFFFF, "RegionPort's region settings");
        Expect(msb.Regions.SFX.Count(r => r.EntityID == 0 && r.StartDisabled == 0) == 1, "an SFX region with no DS3 entity ID gets none");
        Expect(msb.Regions.SFX.Select(r => r.Name).Distinct().Count() == 2, "duplicate names are made unique");
        var ids = msb.Regions.GetEntries().Select(r => r.RegionID).ToList();
        Expect(ids.Distinct().Count() == ids.Count, "every region has its own RegionID");
        // A rerun replaces the regions it added instead of adding them twice.
        MapSfx.Convert(ds3, msb, Ds3Id, ErId, converted);
        Expect(msb.Regions.SFX.Count == 2, $"a rerun keeps two SFX regions ({msb.Regions.SFX.Count})");
        Expect(MapSfx.Convert(ds3, msb, Ds3Id, ErId, new Dictionary<int, int>()).Added == 0 && msb.Regions.SFX.Count == 0, "with no converted effects none are added");
    }

    private static void MapFileEvents()
    {
        var (msb, result) = ConvertWithRegions();
        Expect(result.Patrols == 1 && result.EnemiesOnPatrol == 1 && result.Platoons == 1 && result.Generators == 1, result.Summary);
        Expect(result.Notes.Any(n => n.Contains("1 patrol routes left out")), "a route whose points weren't carried is left out and reported");
        var patrol = msb.Events.PatrolInfo.Single();
        Expect(patrol.WalkRegionNames[0] == "p1" && patrol.WalkRegionNames[1] == "p2" && patrol.WalkRegionNames[2] is null, "the route's points, in order");
        Expect(patrol.PatrolType == 1, "DS3's route kind is kept");
        var walker = msb.Parts.Enemies.Single(e => e.EntityID == 45000100);
        Expect(walker.WalkRouteName == patrol.Name, "the enemy walks its DS3 route");
        Expect(msb.Parts.Enemies.Single(e => e.EntityID == 45000101).WalkRouteName is null or "", "an enemy without a route has none");
        var platoon = msb.Events.PlatoonInfo.Single();
        Expect(platoon.GroupPartsNames[0] == walker.Name && platoon.GroupPartsNames[1] is not null && platoon.PlatoonIDScriptActivate == 7, "the platoon names the ported enemy parts");
        var generator = msb.Events.Generators.Single();
        Expect(generator.EntityID == 45003100 && generator.MaxNum == 3 && generator.MaxInterval == 4, "the generator keeps its entity ID and numbers");
        Expect(generator.SpawnRegionNames[0] == "spawn 1" && generator.SpawnPartNames[0] == msb.Parts.Enemies.Single(e => e.EntityID == 45000101).Name, "and names the ported point and enemy");
        var eventIds = msb.Events.GetEntries().Select(e => e.EventID).ToList();
        Expect(eventIds.Distinct().Count() == eventIds.Count, "every map-file event has its own ID");
    }

    private static void ObjectNumbers()
    {
        var plan = ObjectPort.Plan(["o302590", "O000100", "o000100", "", "o400300"]);
        Expect(plan.Count == 3 && plan[0] == ("o000100", 0) && plan[2] == ("o400300", 2), "distinct, lower case, in name order");
        Expect(ObjectPort.AssetName(920, 0) == "AEG920_000" && ObjectPort.AssetName(920, 1001) == "AEG921_001", "1000 models per asset group");
        Expect(ObjectPort.FreeGroup(["/asset/aeg/aeg099/aeg099_001.geombnd.dcx", null, "/map/x"], 2) == 920, "920 when it is free");
        Expect(ObjectPort.FreeGroup(["/asset/aeg/aeg920/aeg920_001.geombnd.dcx", "/asset/aeg/aeg922/aeg922_000.geombnd.dcx"], 2) == 923, "the first run of free groups");
        var asset = new ObjectPort.Asset { Ds3Model = "o000100", Index = 1001, Name = ObjectPort.AssetName(920, 1001) };
        Expect(asset.Row == 921001 && asset.Group == "AEG921", $"row {asset.Row} is the asset's group and number");
        Expect(ObjectPort.GeomBndPath("AEG921_001") == "/asset/aeg/aeg921/aeg921_001.geombnd.dcx"
               && ObjectPort.GeomHkxBndPath("AEG921_001") == "/asset/aeg/aeg921/aeg921_001_l.geomhkxbnd.dcx", "file paths as JortPob writes them");
        Expect(ObjectPort.ParseMethod("none") == ObjectPort.Method.None && ObjectPort.ParseMethod("nocollision") == ObjectPort.Method.NoCollision
               && MapPorter.Options.Parse(["--objects", "none"]).Objects == ObjectPort.Method.None && new MapPorter.Options().Objects == ObjectPort.Method.Full, "--objects is read");
    }

    /// <summary>A DS3 object: the root, a node 5 m up with one triangle bound to it, and a dummy point on that node.</summary>
    private static FLVER2 SyntheticObject()
    {
        var flver = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\obj\O[ARSN].mtd", ("g_DiffuseTexture", @"N:\FDP\data\Model\obj\o30\o302590\tex\o302590_a.tga"),
            ("g_BumpmapTexture", @"N:\FDP\data\Model\obj\o30\o302590\tex\o302590_n.tga"));
        flver.Nodes.Add(new FLVER.Node { Name = "arm", ParentIndex = 0, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1, Translation = new Vector3(0, 5, 0), Scale = Vector3.One });
        flver.Nodes[0].FirstChildIndex = 1;
        flver.Nodes[0].Scale = Vector3.One;
        var mesh = flver.Meshes[0];
        mesh.NodeIndex = 1;
        foreach (var v in mesh.Vertices)
            v.NormalWs[0] = 1;
        flver.Dummies.Add(new FLVER.Dummy { ReferenceID = 200, ParentBoneIndex = 1, AttachBoneIndex = -1, Position = new Vector3(0, 1, 0) });
        return flver;
    }

    private static void ObjectModel()
    {
        var (port, _) = VisualPort();
        var source = SyntheticObject();
        var before = source.Meshes[0].Vertices.Select(v => v.Position).ToList();
        var objbnd = new BND4();
        objbnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 100, @"N:\FDP\data\INTERROOT_win64\obj\o302590\o302590.tpf", ObjectTpf().Write()));
        objbnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 201, @"N:\FDP\data\INTERROOT_win64\obj\o302590\o302590_1.flver", SyntheticFlver(0x20014, @"N:\x\O[ARSN].mtd").Write()));
        objbnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 200, @"N:\FDP\data\INTERROOT_win64\obj\o302590\o302590.flver", source.Write()));
        var result = ObjectPort.ConvertBinder(objbnd, "o302590", "AEG920_007", port, new BND4 { Version = "07D7R6" }, Binder.FileFlags.Flag1);

        var file = BND4.Read(result.Binder.Write()).Files.Single();
        Expect(file.ID == 200 && file.Name == @"N:\GR\data\INTERROOT_win64\asset\aeg\aeg920\aeg920_007\sib\aeg920_007.flver", $"the model's binder entry: {file.ID} {file.Name}");
        var flver = FLVER2.Read(file.Bytes);
        Expect(flver.Header.Version == 0x2001A, "written in Elden Ring's FLVER version");
        var after = flver.Meshes.Single().Vertices.Select(v => v.Position).ToList();
        Expect(before.Zip(after).All(p => Vector3.Distance(p.First + new Vector3(0, 5, 0), p.Second) < 1e-3f), "vertices bound to the raised node are moved up into model space");
        Expect(flver.Meshes[0].NodeIndex == 0 && !flver.Meshes[0].UseBoneWeights && flver.Meshes[0].Vertices.All(v => v.NormalWs[0] == 0), "the mesh sits on the root, like a map piece's");
        Expect(flver.Nodes.Count == 2 && flver.Dummies.Single() is { ReferenceID: 200, ParentBoneIndex: 1 }, "nodes and dummy points are kept (effects, prompts and ladders use them)");
        Expect(flver.Nodes[0].Name == "AEG920_007", "the root is named after the asset");
        var material = flver.Materials.Single();
        Expect(material.Name.StartsWith("DKS_00_00_00_00_") && port.Textures.SetEquals(["o302590_a", "o302590_n"]), $"its own material naming the object's textures ({material.Name}; {string.Join(", ", port.Textures)})");
        Expect(result.Textures.Keys.Order().SequenceEqual(["o302590_a", "o302590_n"]), "the objbnd's textures come along");

        // The textures reach the map's texture binder under their names.
        var area = new MapTextures(ErId);
        foreach (var (name, texture) in result.Textures)
            area.Provide(name, texture);
        area.Need(port.Textures, Ds3Id);
        Expect(area.Count == 2, "and are named as needed");

        var collision = BND4.Read(ObjectPort.CollisionBinder("AEG920_007", [1, 2, 3], new BND4 { Version = "07D7R6" }, Binder.FileFlags.Flag1).Write()).Files.Single();
        Expect(collision.ID == 300 && collision.Name == @"N:\GR\data\INTERROOT_win64\asset\aeg\AEG920\AEG920_007\hkx_L\AEG920_007_L.hkx", $"the collision entry: {collision.ID} {collision.Name}");

        // A skinned mesh is already in model space: it is drawn in its rest pose and nothing moves.
        var skinned = SyntheticObject();
        skinned.Meshes[0].UseBoneWeights = true;
        var rest = skinned.Meshes[0].Vertices.Select(v => v.Position).ToList();
        Expect(MapModelPort.BakeNodes(skinned) == 0 && skinned.Meshes[0].Vertices.Select(v => v.Position).SequenceEqual(rest) && !skinned.Meshes[0].UseBoneWeights, "skinned meshes keep their vertices");
    }

    private static TPF ObjectTpf()
    {
        var tpf = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
        tpf.Textures.Add(new TPF.Texture("o302590_a", 0, 0, ModelPort.FlatNormalDds(), TPF.TPFPlatform.PC));
        tpf.Textures.Add(new TPF.Texture("o302590_n", 0, 0, ModelPort.FlatNormalDds(), TPF.TPFPlatform.PC));
        return tpf;
    }

    private static void ObjectParts()
    {
        var ds3 = Ds3WithRegions();
        ds3.Models.Objects.Add(new MSB3.Model.Object { Name = "o302590" });
        ds3.Parts.Objects.Add(new MSB3.Part.Object { Name = "o302590_0000", ModelName = "o302590", EntityID = 4001500, Position = new Vector3(3, 4, 5), Rotation = new Vector3(0, 90, 0), Scale = new Vector3(2, 2, 2) });
        ds3.Parts.Objects.Add(new MSB3.Part.Object { Name = "o302590_0001", ModelName = "o302590" });
        ds3.Parts.Objects.Add(new MSB3.Part.Object { Name = "o000404_0000", ModelName = "o000404", EntityID = 4001501 });
        ds3.Regions.Events.Add(new MSB3.Region.Event { Name = "by the door", EntityID = 4002810, ActivationPartName = "o302590_0000" });
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var assets = new Dictionary<string, ObjectPort.Asset>(StringComparer.OrdinalIgnoreCase)
        {
            ["o302590"] = new() { Ds3Model = "o302590", Index = 7, Name = "AEG920_007", Converted = true, Collision = true },
            ["o000404"] = new() { Ds3Model = "o000404", Index = 0, Name = "AEG920_000", Failure = "DS3 has no /obj/o000404.objbnd.dcx" },
        };
        var placed = ObjectPort.PlaceParts(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), m => assets.GetValueOrDefault(m));
        Expect(placed.Placed == 2 && placed.LeftOut == 1 && placed.WithCollision == 2 && placed.Models == 1, placed.Summary);
        Expect(placed.Notes.Any(n => n.Contains("o000404.objbnd")), "an object that didn't convert is left out and the report says why");
        var regions = RegionPort.Convert(ds3, layout.Msb, Ds3Id, ErId, placed.PartNames);

        var msb = MSBE.Read(layout.Msb.Write());
        var part = msb.Parts.Assets.Single(a => a.EntityID == 45001500);
        Expect(part.Name == "AEG920_007_9000" && part.ModelName == "AEG920_007" && part.InstanceID == 9000, $"named after its asset ({part.Name})");
        Expect(part.Position == new Vector3(3, 4, 5) && part.Rotation == new Vector3(0, 90, 0) && part.Scale == new Vector3(2, 2, 2), "spot, rotation and scale kept");
        Expect(part.UnkT54PartName == part.Name && part.UnkPartNames[4] == part.Name && part.UnkPartNames[5] == part.Name, "names itself where JortPob's assets do");
        Expect(part.AssetSfxParamRelativeID == -1 && part.UnkE0F == 1 && part.TileLoad.MapID.All(b => b == 255) && part.AssetUnk2.Unk04 == 100
               && part.AssetUnk3.DisableWhenMapLoadedMapID.All(b => b == -1), "JortPob's generic asset settings");
        Expect(part.Unk1.DisplayGroups.All(g => g == 0), "no group masks, like the colosseum's own assets");
        Expect(msb.Parts.Assets.Count == 2 && msb.Parts.Assets[1].Name == "AEG920_007_9001", "the second instance gets the next number");
        var model = msb.Models.Assets.Single();
        Expect(model.Name == "AEG920_007" && model.SibPath == @"N:\GR\data\Asset\Environment\geometry\AEG920\AEG920_007\sib\AEG920_007.sib", $"one model entry ({model.SibPath})");
        Expect(msb.Regions.Others.Single(r => r.EntityID == 45002810).ActivationPartName == part.Name, "a region tied to the object names the asset part");
        Expect(regions.Regions > 0, "regions carried");

        Expect(ObjectPort.Strip(msb) == 2 && msb.Parts.Assets.Count == 0 && msb.Models.Assets.Count == 0, "Strip takes the assets out");
        Expect(msb.Regions.Others.Single(r => r.EntityID == 45002810).ActivationPartName is null, "and unties what named them");
        MSBE.Read(msb.Write());
    }

    private static void DefVersionFit()
    {
        static PARAMDEF Def(short version, int fields)
        {
            var def = new PARAMDEF { ParamType = "ASSET_GEOMETORY_PARAM_ST", DataVersion = version, FormatVersion = 203, Unicode = true };
            for (int i = 0; i < fields; i++)
                def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.s32, $"f{i}"));
            return def;
        }
        var game = Def(5, 3);
        var param = new PARAM { ParamType = game.ParamType, ParamdefDataVersion = 5, ParamdefFormatVersion = 203, Rows = [new PARAM.Row(7077, "", game), new PARAM.Row(7078, "", game)] };
        param.ApplyParamdef(game);
        var back = PARAM.Read(param.Write());
        Expect(back.DetectedSize == 12, $"row size read back {back.DetectedSize}");
        Expect(!back.ApplyParamdefCarefully(Def(4, 3)) && ParamStore.FitsButForVersion(back, Def(4, 3)), "same type and size, other data version: fits");
        Expect(!ParamStore.FitsButForVersion(back, Def(4, 4)), "another row size must not fit");
        Expect(!ParamStore.FitsButForVersion(back, Def(5, 3)), "the same version is the careful path's job");
        back.ApplyParamdef(Def(4, 3));
        Expect(back.Rows.Count == 2 && back[7077] is not null, "rows readable after applying");
    }

    private static void ObjectRows()
    {
        var assetDef = new PARAMDEF { ParamType = "ASSET_GEOMETORY_PARAM_ST", DataVersion = 1 };
        foreach (var (type, name) in new[] { (PARAMDEF.DefType.s32, "soundBankId"), (PARAMDEF.DefType.s32, "refDrawParamId"), (PARAMDEF.DefType.s8, "hitCreateType"), (PARAMDEF.DefType.u8, "behaviorType") })
            assetDef.Fields.Add(new PARAMDEF.Field(assetDef, type, name));
        assetDef.Fields.Add(new PARAMDEF.Field(assetDef, PARAMDEF.DefType.u8, "isLadder") { BitSize = 1 });
        assetDef.Fields.Add(new PARAMDEF.Field(assetDef, PARAMDEF.DefType.u8, "isMoveObj") { BitSize = 1 });
        var assetParam = new PARAM { ParamType = assetDef.ParamType, Rows = [] };
        var template = new PARAM.Row(ObjectPort.TemplateRow, "stone ruin", assetDef);
        template["soundBankId"].Value = 42;
        template["refDrawParamId"].Value = 2046;
        template["hitCreateType"].Value = (sbyte)2;
        assetParam.Rows.Add(template);
        assetParam.Rows.Add(new PARAM.Row(920000, "stale", assetDef));

        var drawDef = new PARAMDEF { ParamType = "PARTS_DRAW_PARAM_ST", DataVersion = 1 };
        foreach (var (type, name) in new[] { (PARAMDEF.DefType.f32, "lv01_BorderDist"), (PARAMDEF.DefType.f32, "drawDist"), (PARAMDEF.DefType.f32, "drawFadeRange"), (PARAMDEF.DefType.u8, "lodType") })
            drawDef.Fields.Add(new PARAMDEF.Field(drawDef, type, name));
        var drawParam = new PARAM { ParamType = drawDef.ParamType, Rows = [new PARAM.Row(MapDrawParams.TemplateRow, "", drawDef)] };

        var port = ObjectPort.ForRows(
        [
            new ObjectPort.Asset { Ds3Model = "o000100", Index = 0, Name = "AEG920_000", Converted = true, Radius = 0.6f, Ladder = true },
            new ObjectPort.Asset { Ds3Model = "o302590", Index = 1, Name = "AEG920_001", Converted = true, Collision = true, Radius = 9f },
            new ObjectPort.Asset { Ds3Model = "o999999", Index = 2, Name = "AEG920_002", Failure = "no model" },
        ]);
        Expect(port.AddRows(assetParam, drawParam) == 2, "one row per converted object");
        Expect(assetParam.Rows.Select(r => r.ID).SequenceEqual([ObjectPort.TemplateRow, 920000, 920001]), "rows in ID order, the stale one replaced, none for a failed model");
        var small = assetParam[920000];
        var big = assetParam[920001];
        Expect(small.Name == "[DS3] o000100" && (int)small["soundBankId"].Value == 42, "a copy of the template row, named after the DS3 object");
        Expect((byte)small["isLadder"].Value == 1 && (byte)big["isLadder"].Value == 0, "a DS3 ladder's row says it is a ladder");
        Expect((sbyte)small["hitCreateType"].Value == 0 && (byte)small["behaviorType"].Value == 0 && (byte)big["behaviorType"].Value == 1, "low collision only; behaviorType 1 only with collision");
        Expect((int)small["refDrawParamId"].Value == ObjectPort.FirstDrawRow && (int)big["refDrawParamId"].Value == ObjectPort.FirstDrawRow + 3, "draw distance row by size");
        var rows = drawParam.Rows.Where(r => r.ID >= ObjectPort.FirstDrawRow).ToList();
        Expect(rows.Count == ObjectPort.DrawBySize.Length && (float)rows[0]["drawDist"].Value == 64f && (float)rows[^1]["drawDist"].Value == 99999f, "the draw distance rows");
        Expect(!MapDrawParams.IsOurs(ObjectPort.FirstDrawRow) || ObjectPort.FirstDrawRow > MapDrawParams.FirstRow, "they don't collide with the map pieces' row");
        // Again: nothing doubles.
        port.AddRows(assetParam, drawParam);
        Expect(assetParam.Rows.Count == 3 && drawParam.Rows.Count == 1 + ObjectPort.DrawBySize.Length, "a second pass replaces the rows");
        // Without PartsDrawParam the objects keep the template's row.
        var alone = new PARAM { ParamType = assetDef.ParamType, Rows = [new PARAM.Row(template)] };
        port.AddRows(alone, null);
        Expect((int)alone[920000]["refDrawParamId"].Value == 2046, "no draw rows: the template's draw distance stays");
    }
}
