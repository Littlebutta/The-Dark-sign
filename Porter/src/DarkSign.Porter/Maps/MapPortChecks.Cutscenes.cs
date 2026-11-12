using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>Offline checks of the cutscene port: IDs and names, timeline conversion and the scripts' cutscene lines.</summary>
public static partial class MapPortChecks
{
    public static IEnumerable<(string Name, Action Test)> CutsceneChecks()
    {
        yield return ("cutscenes: DS3 sAA_BB_NNNN moves to the slot's area and block; paths go from remo to cutscene", CutsceneIds);
        yield return ("cutscenes: timelines keep Elden Ring's resource kinds and parameters, rename parts and move the map", CutsceneTimeline);
        yield return ("cutscenes: script lines play converted cutscenes in Elden Ring's forms; others are left out or keep their warp", CutsceneScriptLines);
        yield return ("cutscenes: CutsceneMapIdParam rows place each cutscene on its slot", CutsceneRows);
    }

    private static void CutsceneIds()
    {
        Expect(CutscenePort.IdOf("s45_00_0010") == 45000010, "IdOf");
        Expect(CutscenePort.Name(19700010) == "s19_70_0010", CutscenePort.Name(19700010));
        Expect(CutscenePort.ErId(45000031, MapId.Parse("m19_70_00_00")) == 19700031, "ErId");
        Expect(CutscenePort.PackedMap(MapId.Parse("m19_70_00_00")) == 19700000, "PackedMap");
        var path = CutscenePort.RePath(@"N:\FDP\data\Model\remo\scn\s45_00_0000\cut0010\hkx\a0000.HKT", "s45_00_0000", "s19_70_0000");
        Expect(path == @"N:\GR\data\Model\cutscene\scn\s19_70_0000\cut0010\hkx\a0000.HKT", path);
        var file = CutscenePort.RePath(@"N:\FDP\data\INTERROOT_win64\remo\s45_00_0000\s45_00_0000.mqb", "s45_00_0000", "s19_70_0000");
        Expect(file == @"N:\GR\data\INTERROOT_win64\cutscene\s19_70_0000\s19_70_0000.mqb", file);
        var rename = CutscenePort.Renamer(new Dictionary<string, string> { ["c6020_0000"] = "c6020_9000", ["o459020_0000"] = "AEG099_001_9000" });
        Expect(rename("c6020_0000") == "c6020_9000" && rename("cam0010") is null && rename("c0000_0000") is null, "part renames");
        Expect(rename("o459020") == "AEG099", "a renamed part's model");
    }

    private static MQB.Parameter P(string name, MQB.Parameter.DataType type, object value) => new() { Name = name, Type = type, Value = value, MemberCount = 1 };

    private static MQB CutsceneMqb()
    {
        var mqb = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "NEW_SCENE", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        mqb.Resources.Add(new MQB.Resource { Name = "a0000.HKT", ParentIndex = -1, Unk48 = 266, Path = @"N:\FDP\data\Model\remo\scn\s45_00_0000\cut0010\hkx\a0000.HKT" });
        mqb.Resources.Add(new MQB.Resource { Name = "DS3 only", ParentIndex = -1, Unk48 = 187 });
        mqb.Resources.Add(new MQB.Resource { Name = "c6020_0000", ParentIndex = 0, Unk48 = 1 });
        mqb.Resources.Add(new MQB.Resource { Name = "SE", ParentIndex = -1, Unk48 = 135 });
        var cut = new MQB.Cut { Name = "cut0010", Duration = 100, Timelines = [] };
        var part = new MQB.Timeline { Parameters = [P("areaNo", MQB.Parameter.DataType.Int, 45), P("blockNo", MQB.Parameter.DataType.Int, 0), P("ds3Only", MQB.Parameter.DataType.Int, 1)] };
        part.Events.Add(new MQB.Event { ResourceIndex = 2, Duration = 100, Parameters = [P("ZSortOffset", MQB.Parameter.DataType.Int, 0)] });
        var light = new MQB.Timeline();
        light.Events.Add(new MQB.Event { ResourceIndex = 1, Duration = 10, Parameters = [P("beginLightId", MQB.Parameter.DataType.Int, 1001)] });
        var sound = new MQB.Timeline();
        sound.Events.Add(new MQB.Event { ResourceIndex = 3, Duration = 10, Parameters = [P("seType", MQB.Parameter.DataType.Int, 9), P("seId", MQB.Parameter.DataType.Int, 100)] });
        sound.Events.Add(new MQB.Event { ResourceIndex = 3, StartFrame = 20, Duration = 10, Parameters = [P("seType", MQB.Parameter.DataType.Int, 9), P("ds3Volume", MQB.Parameter.DataType.Float, 1f)] });
        cut.Timelines.AddRange([part, light, sound]);
        mqb.Cuts.Add(cut);
        return mqb;
    }

    private static CutscenePort.Catalog CutsceneCatalog()
    {
        // What Elden Ring's own cutscenes have: HKT, parts and SE resources, no 187.
        var er = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "s10_00_0050", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        er.Resources.Add(new MQB.Resource { Name = "a0000.HKT", ParentIndex = -1, Unk48 = 266 });
        er.Resources.Add(new MQB.Resource { Name = "d5000_0010", ParentIndex = 0, Unk48 = 1 });
        er.Resources.Add(new MQB.Resource { Name = "SE_1", ParentIndex = -1, Unk48 = 135 });
        var cut = new MQB.Cut { Name = "cut0010", Duration = 150, Timelines = [] };
        var part = new MQB.Timeline { Parameters = [P("areaNo", MQB.Parameter.DataType.Int, -1), P("blockNo", MQB.Parameter.DataType.Int, -1)] };
        part.Events.Add(new MQB.Event { ResourceIndex = 1, Parameters = [P("ZSortOffset", MQB.Parameter.DataType.Int, 0)] });
        var se = new MQB.Timeline();
        se.Events.Add(new MQB.Event { ResourceIndex = 2, Parameters = [P("seType", MQB.Parameter.DataType.Int, 4), P("seId", MQB.Parameter.DataType.Int, 1)] });
        cut.Timelines.AddRange([part, se]);
        er.Cuts.Add(cut);
        var catalog = new CutscenePort.Catalog();
        catalog.Add(MQB.Read(er.Write()));
        return catalog;
    }

    private static void CutsceneTimeline()
    {
        var mqb = MQB.Read(CutsceneMqb().Write());
        var rename = CutscenePort.Renamer(new Dictionary<string, string> { ["c6020_0000"] = "c6020_9000" });
        var stats = CutscenePort.ConvertMqb(mqb, "s45_00_0000", "s19_70_0000", rename, MapId.Parse("m45_00_00_00"), MapId.Parse("m19_70_00_00"), CutsceneCatalog());
        var back = MQB.Read(mqb.Write());
        Expect(back.Name == "s19_70_0000", $"name {back.Name}");
        Expect(back.Resources.Count == 3 && back.Resources.All(r => r.Unk48 != 187), "the kind Elden Ring doesn't use is left out");
        Expect(back.Resources[0].Path == @"N:\GR\data\Model\cutscene\scn\s19_70_0000\cut0010\hkx\a0000.HKT", back.Resources[0].Path ?? "null");
        Expect(back.Resources[1].Name == "c6020_9000" && back.Resources[1].ParentIndex == 0, $"part renamed ({back.Resources[1].Name}, parent {back.Resources[1].ParentIndex})");
        var timelines = back.Cuts[0].Timelines;
        Expect(timelines.Count == 2, $"the light timeline goes with its only event ({timelines.Count} timelines)");
        Expect(timelines[0].Events[0].ResourceIndex == 1, "part event points at the moved resource");
        Expect(timelines[1].Events.Count == 1 && timelines[1].Events[0].ResourceIndex == 2, "the event with a DS3-only parameter is left out, the other kept and re-pointed");
        var area = timelines[0].Parameters.FirstOrDefault(p => p.Name == "areaNo");
        var block = timelines[0].Parameters.FirstOrDefault(p => p.Name == "blockNo");
        Expect(area?.Value is 19 && block?.Value is 70, $"map moved to the slot (area {area?.Value}, block {block?.Value})");
        Expect(timelines[0].Parameters.All(p => p.Name != "ds3Only"), "timeline parameter Elden Ring doesn't have is left out");
        Expect(stats.ResourcesDropped == 1 && stats.EventsDropped == 2 && stats.Renamed >= 1 && stats.MapsMoved == 2, $"stats {stats.ResourcesDropped} {stats.EventsDropped} {stats.Renamed} {stats.MapsMoved}");
    }

    private const string Ds3CutsceneDefs = """
    { "main_classes": [
      { "index": 2002, "instrs": [
        { "name": "Play Cutscene to All", "index": 1, "args": [ { "name": "Cutscene ID", "type": 5 }, { "name": "Playback Method", "type": 2 } ] },
        { "name": "Play Cutscene to Player", "index": 3, "args": [
          { "name": "Cutscene ID", "type": 5 }, { "name": "Playback Method", "type": 2 }, { "name": "Player Entity ID", "type": 5 } ] },
        { "name": "Play Cutscene and Warp Player", "index": 4, "args": [
          { "name": "Cutscene ID", "type": 5 }, { "name": "Playback Method", "type": 2 }, { "name": "Point Entity ID", "type": 5 },
          { "name": "Area ID", "type": 0 }, { "name": "Block ID", "type": 0 }, { "name": "Player Entity ID", "type": 5 } ] },
      ] },
    ] }
    """;

    private const string ErCutsceneDefs = """
    { "main_classes": [
      { "index": 2002, "instrs": [
        { "name": "Play Cutscene To All", "index": 1, "args": [ { "name": "Cutscene ID", "type": 5 }, { "name": "Playback Method", "type": 2 } ] },
        { "name": "Play Cutscene to Player", "index": 3, "args": [
          { "name": "Cutscene ID", "type": 5 }, { "name": "Playback Method", "type": 2 }, { "name": "Player Entity ID", "type": 2 } ] },
        { "name": "Play Cutscene to Player and Warp", "index": 11, "args": [
          { "name": "Cutscene ID", "type": 5 }, { "name": "Playback Method", "type": 2 }, { "name": "Area Entity ID", "type": 2 },
          { "name": "Map ID", "type": 5 }, { "name": "Player Entity ID", "type": 2 }, { "name": "Unknown14", "type": 5 }, { "name": "Unknown18", "type": 0 } ] },
      ] },
      { "index": 2003, "instrs": [
        { "name": "Warp Player", "index": 14, "args": [
          { "name": "Area ID", "type": 0 }, { "name": "Block ID", "type": 0 }, { "name": "Region ID", "type": 0 }, { "name": "Index ID", "type": 0 },
          { "name": "Initial Area Entity ID", "type": 2 }, { "name": "Subarea Name Popup Message ID", "type": 5 } ] },
      ] },
    ] }
    """;

    private static void CutsceneScriptLines()
    {
        var ds3Map = MapId.Parse("m45_00_00_00");
        var erMap = MapId.Parse("m19_70_00_00");
        EventPort Port(Func<long, long?>? cutscenes) =>
            new(Emedf.Parse(Ds3CutsceneDefs), Emedf.Parse(ErCutsceneDefs), [(ds3Map, erMap)], new FlagMap()) { Cutscenes = cutscenes };
        var er = Emedf.Parse(ErCutsceneDefs);
        // The Painted World's lines, as DS3's m45_00 has them (cutscene-survey, 2026-11-12).
        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        var e = new EMEVD.Event(14505810, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(RunEvents.Make(2002, 4, 45000000, 10u, 4502810, (byte)45, (byte)0, 10000));
        e.Instructions.Add(RunEvents.Make(2002, 3, 45000000, 2u, 10000));
        e.Instructions.Add(RunEvents.Make(2002, 3, 45000090, 0u, 10000));
        script.Events.Add(e);
        Func<long, long> ids = id => ds3Map.OwnsDs3Id(id) ? ds3Map.MoveEntity((int)id, erMap) : id;

        var played = Port(id => id == 45000000 ? 19700000 : null);
        var lines = played.Convert(script, EMEVD.Game.Sekiro, ids).Events[0].Instructions;
        var warp = lines[0];
        Expect(warp.Bank == 2002 && warp.ID == 11, $"warping line becomes Play Cutscene to Player and Warp ({warp.Bank}[{warp.ID}])");
        long Arg(EMEVD.Instruction ins, string name) => Emedf.Read(ins.ArgData, er.Get(ins.Bank, ins.ID)!.Args.First(a => a.Name == name));
        Expect(Arg(warp, "Cutscene ID") == 19700000, $"cutscene {Arg(warp, "Cutscene ID")}");
        Expect(Arg(warp, "Playback Method") == 18, $"DS3 unskippable with fade-out (10) is Elden Ring's 18 ({Arg(warp, "Playback Method")})");
        Expect(Arg(warp, "Area Entity ID") == 19702810, $"point moved to the slot ({Arg(warp, "Area Entity ID")})");
        Expect(Arg(warp, "Map ID") == 19700000, $"map ID is the slot ({Arg(warp, "Map ID")})");
        Expect(Arg(warp, "Player Entity ID") == 10000, "player");
        Expect(lines[1].Bank == 2002 && lines[1].ID == 3 && Arg(lines[1], "Playback Method") == 2, "Play Cutscene to Player stays, unskippable kept");
        Expect(lines[2].Bank == 1001, "a cutscene that wasn't converted is left out (no-op)");
        Expect(played.Stats.CutscenesPlayed == 2 && played.Stats.CutscenesLeftOut == 1, $"stats {played.Stats.CutscenesPlayed}/{played.Stats.CutscenesLeftOut}");

        var off = Port(null).Convert(script, EMEVD.Game.Sekiro, ids).Events[0].Instructions;
        Expect(off[0].Bank == 2003 && off[0].ID == 14, $"without converted cutscenes the warping line keeps its warp ({off[0].Bank}[{off[0].ID}])");
        Expect(off[1].Bank == 1001 && off[2].Bank == 1001, "and the others are left out");
        Expect(EventPort.PlaybackMethod(8) == 16 && EventPort.PlaybackMethod(0) == 0 && EventPort.PlaybackMethod(2) == 2, "playback method flags");
    }

    private static void CutsceneRows()
    {
        var def = new PARAMDEF { ParamType = "CUTSCENE_MAP_ID_PARAM_ST", DataVersion = 2, FormatVersion = 203, Unicode = true, Fields = [] };
        foreach (var (name, type) in new[] { ("PlayMapId", PARAMDEF.DefType.u32), ("RequireMapId0", PARAMDEF.DefType.u32), ("RefCamPosHitPartsID", PARAMDEF.DefType.s32), ("HitParts_0", PARAMDEF.DefType.s32), ("HitParts_1", PARAMDEF.DefType.s32) })
            def.Fields.Add(new PARAMDEF.Field(def, type, name));
        var param = new PARAM { ParamType = def.ParamType, Rows = [] };
        param.ApplyParamdef(def);
        var template = new PARAM.Row(CutscenePort.TemplateRow, "s10_00_0050", def);
        template["PlayMapId"].Value = 10000000u;
        template["RefCamPosHitPartsID"].Value = 1200;
        param.Rows.Add(template);
        var rows = new List<(int, MapId)> { (19700000, MapId.Parse("m19_70_00_00")), (45000010, MapId.Parse("m45_00_00_00")) };
        CutscenePort.WriteRows(param, rows);
        var r = param[19700000];
        Expect(r is not null && (uint)r["PlayMapId"].Value == 19700000u && (int)r["RefCamPosHitPartsID"].Value == -1, "row placed on m19_70 with no hit parts");
        Expect(param[45000010] is { } r2 && (uint)r2["PlayMapId"].Value == 45000000u, "second slot");
        Expect(param.Rows.Select(x => x.ID).SequenceEqual(param.Rows.Select(x => x.ID).Order()), "rows sorted");
    }
}
