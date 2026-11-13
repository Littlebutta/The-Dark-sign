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
        yield return ("cutscenes: sound events become Elden Ring's kind 135 form with the cutscene's own IDs; armour to seType 15, speech to its voice", CutsceneSoundEvents);
        yield return ("cutscenes: a timeline with no main audio gets DS3's p<ID*10+n> per cut; events of sounds the bank lacks are stripped", CutsceneSoundAutoAndStrip);
        yield return ("cutscenes: converted timelines take Elden Ring's shape: event fields, timeline Unk10 and full parameter lists", CutsceneShapeConform);
        yield return ("cutscenes: the F1 Cutscenes tab warps to the map in one event and plays the pending cutscene after the load in another; its list names the characters", CutsceneMenuEvents);
        yield return ("cutscenes: the cast resolves to Elden Ring models (map names, enemy models, assets); parts with none, and DS3 draw settings, are left out", CutsceneCastResolve);
        yield return ("cutscenes: each cut's Havok file becomes cutNNNN\\a0000.hkx at ID NNNN*10000; files no cut plays are left out", CutsceneCutFiles);
    }

    private static void CutsceneSoundEvents()
    {
        var mqb = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "s45_00_0000", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        mqb.Resources.Add(new MQB.Resource { Name = "a0000.HKT", ParentIndex = -1, Unk48 = 266 });
        mqb.Resources.Add(new MQB.Resource { Name = "カットシーンSE", ParentIndex = -1, Unk48 = 138 });
        mqb.Resources.Add(new MQB.Resource { Name = "SE", ParentIndex = -1, Unk48 = 135 });
        mqb.Resources.Add(new MQB.Resource { Name = "字幕", ParentIndex = -1, Unk48 = 27 });
        mqb.Resources.Add(new MQB.Resource { Name = "動作音　中", ParentIndex = -1, Unk48 = 135 });
        var cut = new MQB.Cut { Name = "cut0010", Duration = 300, Timelines = [] };
        var main = new MQB.Timeline();
        main.Events.Add(new MQB.Event { ResourceIndex = 1, Duration = 300, Parameters = [P("seType", MQB.Parameter.DataType.UInt, 4u), P("seId", MQB.Parameter.DataType.Int, 450000002)] });
        var fx = new MQB.Timeline();
        fx.Events.Add(new MQB.Event { ResourceIndex = 2, StartFrame = 10, Duration = 8, Parameters = [P("seType", MQB.Parameter.DataType.UInt, 4u), P("seId", MQB.Parameter.DataType.Int, 140000050), P("dmypolyId", MQB.Parameter.DataType.Int, 3), P("slotNo", MQB.Parameter.DataType.Int, -1)] });
        fx.Events.Add(new MQB.Event { ResourceIndex = 2, StartFrame = 20, Duration = 8, Parameters = [P("seType", MQB.Parameter.DataType.UInt, 4u), P("seId", MQB.Parameter.DataType.Int, 140000099), P("dmypolyId", MQB.Parameter.DataType.Int, -1), P("slotNo", MQB.Parameter.DataType.Int, -1)] });
        fx.Events.Add(new MQB.Event { ResourceIndex = 2, StartFrame = 30, Duration = 8, Parameters = [P("seType", MQB.Parameter.DataType.UInt, 4u), P("seId", MQB.Parameter.DataType.Int, 140000050), P("dmypolyId", MQB.Parameter.DataType.Int, -1), P("slotNo", MQB.Parameter.DataType.Int, -1)] });
        var talk = new MQB.Timeline();
        talk.Events.Add(new MQB.Event { ResourceIndex = 3, StartFrame = 40, Duration = 90, Parameters = [P("talkParamId", MQB.Parameter.DataType.Int, 45001000), P("dmypolyId", MQB.Parameter.DataType.Int, -1)] });
        talk.Events.Add(new MQB.Event { ResourceIndex = 3, StartFrame = 140, Duration = 90, Parameters = [P("talkParamId", MQB.Parameter.DataType.Int, 777), P("dmypolyId", MQB.Parameter.DataType.Int, -1)] });
        var armour = new MQB.Timeline();
        armour.Events.Add(new MQB.Event { ResourceIndex = 4, StartFrame = 5, Duration = 20, Parameters = [P("seType", MQB.Parameter.DataType.UInt, 9u), P("seId", MQB.Parameter.DataType.Int, 100), P("dmypolyId", MQB.Parameter.DataType.Int, -1), P("slotNo", MQB.Parameter.DataType.Int, -1)] });
        cut.Timelines.AddRange([main, fx, talk, armour]);
        mqb.Cuts.Add(cut);

        var known = new HashSet<string> { "p450000002", "p140000050", "v004005000" };
        var plan = CutsceneSound.Prepare(mqb, "s19_70_0000", 2, known.Contains, t => t == 45001000 ? 4005000 : null);
        int Id(MQB.Event e) => (int)e.Parameters.First(p => p.Name == "seId").Value;
        uint Type(MQB.Event e) => (uint)e.Parameters.First(p => p.Name == "seType").Value;
        Expect(mqb.Resources[1].Unk48 == 135, "cutscene SE resource becomes kind 135");
        Expect(Id(main.Events[0]) == 990_002_000 && main.Events[0].Parameters.Count == 5 && main.Events[0].Parameters[4].Name == "IsForceRefLocalPlayerEquipData",
            "main audio: first new ID of cutscene 2, Elden Ring's five parameters");
        Expect(fx.Events.Count == 2 && Id(fx.Events[0]) == 990_002_001 && Id(fx.Events[1]) == 990_002_001 && (int)fx.Events[0].Parameters[2].Value == 3,
            "an effect sound keeps its dummy poly; the same sound twice gets one ID; an unknown one is left out");
        Expect(talk.Events.Count == 1 && talk.Events[0].ResourceIndex == 5 && mqb.Resources[5].Unk48 == 135 && Id(talk.Events[0]) == 990_002_002 && Type(talk.Events[0]) == 4,
            "a talk with a voice plays it through a new sound resource; one without is left out");
        Expect(Type(armour.Events[0]) == 15 && Id(armour.Events[0]) == 100, "armour movement: DS3 seType 9 -> 15");
        Expect(plan.Sounds.Count == 3 && plan.Main == 1 && plan.Effects == 2 && plan.Voices == 1 && plan.Armour == 1 && plan.Dropped == 2,
            plan.ToString());
        Expect(plan.Sounds["v004005000"] == 990_002_002, "voice name");

        // The rewritten timeline survives the cutscene port's filter of Elden Ring kinds and parameters.
        var catalog = new CutscenePort.Catalog();
        var er = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "s10_00_0050", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        er.Resources.Add(new MQB.Resource { Name = "a0000.HKT", ParentIndex = -1, Unk48 = 266 });
        er.Resources.Add(new MQB.Resource { Name = "SE_1", ParentIndex = -1, Unk48 = 135 });
        er.Resources.Add(new MQB.Resource { Name = "字幕", ParentIndex = -1, Unk48 = 27 });
        var erCut = new MQB.Cut { Name = "cut0010", Duration = 100, Timelines = [] };
        var erTl = new MQB.Timeline();
        erTl.Events.Add(new MQB.Event { ResourceIndex = 1, Duration = 100, Parameters = CutsceneSound.SoundParams(4, 100000500) });
        erCut.Timelines.Add(erTl);
        er.Cuts.Add(erCut);
        catalog.Add(er);
        var stats = CutscenePort.ConvertMqb(mqb, "s45_00_0000", "s19_70_0000", _ => null, MapId.Parse("m45_00_00_00"), MapId.Parse("m19_70_00_00"), catalog);
        Expect(stats.EventsDropped == 0, $"sound events kept by the filter ({stats.EventsDropped} dropped)");
    }

    private static void CutsceneSoundAutoAndStrip()
    {
        // s40_00_0000: no sound events at all; DS3 has p400000001 and p400000002 (numbers from 1: cuts 0 and 1).
        var mqb = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "s40_00_0000", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        mqb.Resources.Add(new MQB.Resource { Name = "a0000.HKT", ParentIndex = -1, Unk48 = 266 });
        foreach (var (name, frames) in new[] { ("cut0010", 200), ("cut0020", 150) })
        {
            var cut = new MQB.Cut { Name = name, Duration = frames, Timelines = [] };
            var tl = new MQB.Timeline { Unk10 = 7 };
            tl.Events.Add(new MQB.Event { ID = 4, ResourceIndex = 0, Duration = frames, Unk20 = 1 });
            cut.Timelines.Add(tl);
            mqb.Cuts.Add(cut);
        }
        var known = new HashSet<string> { "p400000001", "p400000002", "p400000010" };
        var plan = CutsceneSound.Prepare(mqb, "s45_01_0000", 9, known.Contains, _ => null);
        Expect(plan.Main == 2 && plan.Auto == 2 && plan.Sounds.Count == 2 && plan.Sounds["p400000001"] == 990_009_000, plan.ToString());
        var added = mqb.Cuts.Select(c => c.Timelines.Last().Events.Single()).ToList();
        Expect(mqb.Resources[^1].Name == "SE_main" && mqb.Resources[^1].Unk48 == 135 && added.All(e => e.ResourceIndex == mqb.Resources.Count - 1),
            "a new SE_main sound resource");
        Expect(added[0].StartFrame == 0 && added[0].Duration == 200 && added[1].Duration == 150 && added[0].ID == 5 && added[1].ID == 6 && added[0].Unk20 == 1
               && mqb.Cuts[0].Timelines.Last().Unk10 == 7, "one event per cut from its start, new IDs, fields like the cut's first event");
        Expect((int)added[0].Parameters[1].Value == 990_009_000 && (int)added[1].Parameters[1].Value == 990_009_001, "p...1 on cut 0, p...2 on cut 1");

        // A timeline that names its main audio gets nothing added.
        var named = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "s40_00_0000", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        named.Resources.Add(new MQB.Resource { Name = "カットシーンSE", ParentIndex = -1, Unk48 = 138 });
        var namedCut = new MQB.Cut { Name = "cut0010", Duration = 100, Timelines = [new MQB.Timeline()] };
        namedCut.Timelines[0].Events.Add(new MQB.Event { ResourceIndex = 0, Duration = 100, Parameters = [P("seType", MQB.Parameter.DataType.UInt, 4u), P("seId", MQB.Parameter.DataType.Int, 400000001)] });
        named.Cuts.Add(namedCut);
        var namedPlan = CutsceneSound.Prepare(named, "s45_01_0000", 9, known.Contains, _ => null);
        Expect(namedPlan.Auto == 0 && namedPlan.Main == 1 && named.Cuts[0].Timelines.Count == 1, "no main audio added when the timeline has its own");

        // Strip: a sound the bank couldn't build loses its events; the others stay.
        var bnd = new BND4();
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, "s45_01_0000.mqb", mqb.Write()));
        int removed = CutsceneSound.Strip(bnd, new HashSet<int> { 990_009_001 });
        var back = MQB.Read(bnd.Files[0].Bytes);
        var left = back.Cuts.SelectMany(c => c.Timelines).SelectMany(t => t.Events).Where(e => e.Parameters.Any(p => p.Name == "seId")).ToList();
        Expect(removed == 1 && left.Count == 1 && (int)left[0].Parameters[1].Value == 990_009_000, $"strip removed {removed}, {left.Count} left");
    }

    private static MQB ShapeSample(string name, int cutFrames, bool er)
    {
        var mqb = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = name, Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        mqb.Resources.Add(new MQB.Resource { Name = "offset.sib", ParentIndex = -1, Unk48 = 265 });
        mqb.Resources.Add(new MQB.Resource { Name = "c1000_0000", ParentIndex = -1, Unk48 = 1 });
        var cut = new MQB.Cut { Name = "cut0010", Duration = cutFrames, Timelines = [] };
        var off = new MQB.Timeline();
        off.Events.Add(new MQB.Event { ResourceIndex = 0, Duration = cutFrames + 1, Unk18 = er ? cutFrames + 1 : 0, Unk1C = er ? cutFrames + 1 : 0, Unk28 = er ? -1 : 0 });
        var part = new MQB.Timeline
        {
            Unk10 = er ? 1 : 0,
            Parameters = er
                ? [P("areaNo", MQB.Parameter.DataType.Int, -1), P("blockNo", MQB.Parameter.DataType.Int, -1), P("mapNo", MQB.Parameter.DataType.Int, -1), P("equipDispType", MQB.Parameter.DataType.Int, 0)]
                : [P("areaNo", MQB.Parameter.DataType.Int, 45), P("blockNo", MQB.Parameter.DataType.Int, 0)],
        };
        part.Events.Add(new MQB.Event { ResourceIndex = 1, Duration = cutFrames + 1, Parameters = [P("ZSortOffset", MQB.Parameter.DataType.Float, 0f)] });
        if (!er)
        {
            // DS3 events that run past the cut's end, or start after it, and a cut Unk44 Elden Ring never uses.
            part.Events.Add(new MQB.Event { ResourceIndex = 1, StartFrame = cutFrames - 5, Duration = 30, Parameters = [P("ZSortOffset", MQB.Parameter.DataType.Float, 0f)] });
            part.Events.Add(new MQB.Event { ResourceIndex = 1, StartFrame = cutFrames + 4, Duration = 10, Parameters = [P("ZSortOffset", MQB.Parameter.DataType.Float, 0f)] });
            cut.Unk44 = 2;
        }
        cut.Timelines.AddRange([off, part]);
        mqb.Cuts.Add(cut);
        return mqb;
    }

    private static void CutsceneShapeConform()
    {
        var shape = CutsceneShape.Learn(Enumerable.Range(0, 8).Select(i => ShapeSample($"s10_00_00{i}0", 100 + i * 7, er: true)));
        var ds3 = ShapeSample("s45_00_0000", 300, er: false);
        var before = shape.Check(ds3);
        Expect(shape.NeverPastCut(1) && shape.CutUnk44 is { Value: 0, Holds: true }, "learned: Elden Ring's kind 1 events never end after their cut; cut Unk44 0");
        Expect(before.Any(l => l.Contains("ends after its cut")) && before.Any(l => l.Contains("cut Unk44 2")), string.Join(" | ", before));
        Expect(before.Any(l => l.Contains("kind 265 event Unk18")) && before.Any(l => l.Contains("kind 1 timeline Unk10")) && before.Any(l => l.Contains("kind 1 timeline params")),
            string.Join(" | ", before));
        int changed = shape.Conform(ds3);
        var after = shape.Check(ds3);
        Expect(after.Count == 0, $"{changed} changes, left: {string.Join(" | ", after)}");
        var off = ds3.Cuts[0].Timelines[0].Events[0];
        Expect(off.Unk18 == 301 && off.Unk1C == 301 && off.Unk28 == -1, $"offset event fields follow the cut ({off.Unk18} {off.Unk1C} {off.Unk28})");
        var tl = ds3.Cuts[0].Timelines[1];
        Expect(tl.Unk10 == 1 && string.Join(",", tl.Parameters.Select(p => p.Name)) == "areaNo,blockNo,mapNo,equipDispType"
               && (int)tl.Parameters[0].Value == 45 && (int)tl.Parameters[2].Value == -1 && (int)tl.Parameters[3].Value == 0,
            "part timeline: DS3's map kept, Elden Ring's other parameters added at its usual values, in its order");
        Expect(ds3.Cuts[0].Unk44 == 0, "cut Unk44 set to Elden Ring's 0");
        Expect(tl.Events.Count == 2 && tl.Events[1].StartFrame == 295 && tl.Events[1].Duration == 6,
            $"an event past the cut is cut short to cut + 1 (301), one starting after it is left out ({string.Join(" ", tl.Events.Select(x => $"{x.StartFrame}+{x.Duration}"))})");
    }

    private static void CutsceneMenuEvents()
    {
        var slot = MapId.Parse("m19_70_00_00");
        var infos = new[]
        {
            new CutscenePort.Info("s45_00_0010", "s19_70_0010", slot, [], 12.5),
            new CutscenePort.Info("s45_00_0000", "s19_70_0000", slot, ["c6010", "c6020"], 46),
            new CutscenePort.Info("s30_00_0000", "s45_01_0000", MapId.Parse("m45_01_00_00"), [], 10), // map not ported: left out
        };
        var maps = new Dictionary<MapId, (string Name, uint StartEntity)> { [slot] = ("Painted World of Ariandel", 19700990u) };
        var entries = CutsceneMenu.Entries(infos, maps, m => m == "c6020" ? "Sister Friede" : null);
        Expect(entries.Count == 2 && entries[0].ErName == "s19_70_0000" && entries[0].Index == 0 && entries[1].Index == 1, "entries in name order, unported maps left out");
        Expect(entries[0].Label == "Sister Friede, c6010" && entries[1].Label == "cutscene 0010", entries[0].Label + " / " + entries[1].Label);

        var common = new EMEVD { Format = EMEVD.Game.Sekiro };
        common.Events.Add(new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        CutsceneMenu.AddEvents(common, entries);
        CutsceneMenu.AddEvents(common, entries); // again: replaced, not doubled
        var e = common.Events.Single(x => x.ID == CutsceneMenu.EventId(0));
        var p = common.Events.Single(x => x.ID == CutsceneMenu.PlayEventId(0));
        Expect(common.Events.Count(x => CutsceneMenu.IsMenuEvent(x.ID)) == 4, "a request and a play event per cutscene");
        Expect(common.Events[0].Instructions.Count(i => i.Bank == 2000) == 4, "started once each from the constructor");
        var warp = e.Instructions.Single(i => i.Bank == 2003 && i.ID == 14);
        Expect(warp.ArgData[0] == 19 && warp.ArgData[1] == 70 && BitConverter.ToUInt32(warp.ArgData, 4) == 19700990, "warps to the map's start point");
        Expect(e.Instructions.All(i => i.Bank != 2002) && e.Instructions.Any(i => i.Bank == 2003 && i.ID == 66 && BitConverter.ToUInt32(i.ArgData, 4) == CutsceneMenu.PendingFlag(0) && i.ArgData[8] == 1),
            "the request marks the cutscene pending and plays nothing itself");
        Expect(e.Instructions.FindIndex(i => i.Bank == 2003 && i.ID == 66 && BitConverter.ToUInt32(i.ArgData, 4) == CutsceneMenu.PendingFlag(0)) < e.Instructions.IndexOf(warp), "pending before the warp");
        var play = p.Instructions.Single(i => i.Bank == 2002);
        Expect(play.ID == 11 && play.ArgData.Length == 28 && BitConverter.ToInt32(play.ArgData, 0) == 19700000 && BitConverter.ToUInt32(play.ArgData, 4) == 0
               && BitConverter.ToUInt32(play.ArgData, 8) == 19700990 && BitConverter.ToInt32(play.ArgData, 12) == 19700000 && BitConverter.ToUInt32(play.ArgData, 16) == 10000,
            "plays the cutscene to the player and warps back to the start point (2002[11], Elden Ring's own form)");
        var loaded = p.Instructions.Single(i => i.Bank == 3 && i.ID == 30);
        Expect(loaded.ArgData[0] == 0 && loaded.ArgData[1] == 19 && loaded.ArgData[2] == 70, "waits for the map to load");
        Expect(BitConverter.ToUInt32(p.Instructions[0].ArgData, 4) == CutsceneMenu.PendingFlag(0) && p.Instructions[0].Bank == 1003, "the play event waits for the pending flag");
        var clear = p.Instructions.FindIndex(i => i.Bank == 2003 && i.ID == 66 && BitConverter.ToUInt32(i.ArgData, 4) == CutsceneMenu.PendingFlag(0) && i.ArgData[8] == 0);
        Expect(p.Instructions.IndexOf(loaded) < clear && clear < p.Instructions.IndexOf(play), "map loaded, pending cleared, then play");
        Expect(BitConverter.ToUInt32(e.Instructions[0].ArgData, 4) == CutsceneMenu.RequestFlag(0), "waits for its request flag");
        Expect(CutsceneMenu.PendingFlag(0) == 1039290300 && CutsceneMenu.PlayEventId(0) == 1039291700 && CutsceneMenu.IsMenuEvent(1039291700) && !CutsceneMenu.IsMenuEvent(1039291600),
            "pending flag in the saved range, play event IDs");
        var lines = CutsceneMenu.ListLines(entries).ToList();
        Expect(lines.Count == 3 && lines[1] == $"s45_00_0000\ts19_70_0000\tPainted World of Ariandel\tSister Friede, c6010\t46\t{CutsceneMenu.RequestFlag(0)}", lines[1]);
        Expect(CutsceneMenu.RequestFlag(0) == 1039292300 && CutsceneMenu.EventId(0) == 1039291300, "flag and event IDs");
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
        Expect(CutsceneCast.CutFileId("cut0145") == 1_450_000 && CutsceneCast.CutFileId("cut0011") == 110_000 && CutsceneCast.CutFileId("standby") == -1, "Elden Ring cut file IDs");
    }

    private static void CutsceneCastResolve()
    {
        // Friede's intro as DS3 names it (cutscene-survey 2026-11-12): placed parts, an object in another state, a collision.
        var partNames = new Dictionary<string, string> { ["c6020_0000"] = "c7120_9000", ["o459020_0000"] = "AEG920_001_9000", ["c3000_0001"] = "c7000_9000" };
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "c7120", "c7000", "c7010", "AEG920_001", "c4750" };
        var resolve = CutsceneCast.Resolver(partNames, m => m == "c6010" ? "c7010" : null, models.Contains);
        Expect(resolve("c6020_0000") == "c7120_9000", "a placed part takes its map name");
        Expect(resolve("o459020_8000") == "AEG920_001_8000", "an object part the map doesn't place takes its model's asset, its own instance");
        Expect(resolve("c6010_0001") == "c7010_0001", "a character the map doesn't place takes the enemy port's model");
        Expect(resolve("c3000_0005") == "c7000_0005", "another instance of a placed character's model");
        Expect(resolve("h45_00_00_00_008100") is null && resolve("o459200_8000") is null && resolve("c9999_0000") is null, "a collision, an unported object or character: no model");
        Expect(resolve("c0000_0000") == "c0000_0000" && resolve("cam0010") == "cam0010" && resolve("d0003_0004") == "d0003_0004", "player, camera and dummies keep their names");
        Expect(resolve("c4750_0000") == "c4750_0000", "an Elden Ring model's name stays");
        Expect(CutsceneCast.ModelOf("AEG920_001_9000") == "AEG920_001" && CutsceneCast.ModelOf("c7120_9000") == "c7120" && CutsceneCast.ModelOf("o459020_0000") is null, "models of names");

        var mqb = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "NEW_SCENE", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        mqb.Resources.Add(new MQB.Resource { Name = "a0000.HKT", ParentIndex = -1, Unk48 = 266, Path = @"N:\FDP\data\Model\remo\scn\s45_00_0000\cut0010\hkx\a0000.HKT" }); // 0
        mqb.Resources.Add(new MQB.Resource { Name = "c6020_0000", ParentIndex = 0, Unk48 = 1 });                  // 1 kept, renamed
        mqb.Resources.Add(new MQB.Resource { Name = "h45_00_00_00_008100", ParentIndex = 0, Unk48 = 1 });         // 2 left out
        mqb.Resources.Add(new MQB.Resource { Name = "under the collision", ParentIndex = 2, Unk48 = 103 });       // 3 left out with it
        mqb.Resources.Add(new MQB.Resource { Name = "インゲーム描画", ParentIndex = -1, Unk48 = 239 });             // 4 DS3 draw setting
        mqb.Resources.Add(new MQB.Resource { Name = "SE", ParentIndex = -1, Unk48 = 135 });                        // 5
        var cut = new MQB.Cut { Name = "cut0010", Duration = 100, Timelines = [] };
        MQB.Timeline Tl(params int[] res)
        {
            var tl = new MQB.Timeline();
            foreach (var r in res)
                tl.Events.Add(new MQB.Event { ResourceIndex = r, Duration = 100 });
            return tl;
        }
        cut.Timelines.AddRange([Tl(0), Tl(1), Tl(2), Tl(3), Tl(4), Tl(5, 5)]);
        mqb.Cuts.Add(cut);
        var stats = new CutsceneCast.CastStats();
        var drop = CutsceneCast.Unplayable(mqb, resolve, stats);
        Expect(drop.SetEquals([2, 3, 4]), $"left out: the collision, what hangs under it, the draw setting ({string.Join(",", drop)})");
        Expect(stats.Kept == 1 && stats.Renamed == 1 && stats.LeftOut.Single() == "h45_00_00_00_008100" && stats.DrawLeftOut == 1, stats.ToString());
        CutsceneCast.Remove(mqb, drop);
        Expect(mqb.Resources.Count == 3 && mqb.Resources[2].Name == "SE" && mqb.Resources[1].ParentIndex == 0, "resources re-indexed");
        Expect(cut.Timelines.Count == 3 && cut.Timelines[2].Events.All(e => e.ResourceIndex == 2), "their timelines go, the others point at the moved indices");

        // Through the whole binder conversion with an Elden Ring catalog that knows every kind.
        var catalog = new CutscenePort.Catalog();
        foreach (var k in new[] { 1, 103, 135, 239, 266 })
            catalog.Kinds.Add(k);
        var ds3 = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "NEW_SCENE", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        ds3.Resources.Add(new MQB.Resource { Name = "a0000.HKT", ParentIndex = -1, Unk48 = 266, Path = @"N:\FDP\data\Model\remo\scn\s45_00_0000\cut0010\hkx\a0000.HKT" });
        ds3.Resources.Add(new MQB.Resource { Name = "c6020_0000", ParentIndex = 0, Unk48 = 1 });
        ds3.Resources.Add(new MQB.Resource { Name = "h45_00_00_00_008100", ParentIndex = 0, Unk48 = 1 });
        var c2 = new MQB.Cut { Name = "cut0010", Duration = 100, Timelines = [Tl(0), Tl(1), Tl(2)] };
        ds3.Cuts.Add(c2);
        var bnd = new BND4();
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 100_000_000, @"N:\FDP\data\INTERROOT_win64\remo\s45_00_0000\s45_00_0000.mqb", ds3.Write()));
        var side = new CutscenePort.ErSide { Layout = new BND4(), Compression = new DCX.DcxDfltCompressionInfo(DCX.DfltCompressionPreset.DCX_DFLT_11000_44_9), Havok = new CutscenePort.HavokTemplate { Hkx = [] }, Catalog = catalog };
        var (outBnd, outStats) = CutscenePort.ConvertBinder(bnd, "s45_00_0000", "s19_70_0000", _ => null, MapId.Parse("m45_00_00_00"), MapId.Parse("m19_70_00_00"), side, null, resolve);
        var outMqb = MQB.Read(outBnd.Files.Single().Bytes);
        Expect(outMqb.Resources.Select(r => r.Name).SequenceEqual(["a0000.HKT", "c7120_9000"]), string.Join(" ", outMqb.Resources.Select(r => r.Name)));
        Expect(outStats.Cast.LeftOut.Count == 1 && outMqb.Cuts[0].Timelines.Count == 2, outStats.Cast.ToString());
    }

    private static void CutsceneCutFiles()
    {
        // Dancer's layout: cut0011 played from a0001 at ID 110001; Friede's: cut0000 standby files no cut plays.
        var mqb = new MQB { Version = MQB.MQBVersion.DarkSouls3, Name = "s45_01_0010", Framerate = 30, ResourceDirectory = "", Resources = [], Cuts = [] };
        mqb.Resources.Add(new MQB.Resource { Name = "a0001.HKT", ParentIndex = -1, Unk48 = 266, Path = @"N:\GR\data\Model\cutscene\scn\s45_01_0010\cut0011\hkx\a0001.HKT" });
        mqb.Resources.Add(new MQB.Resource { Name = "a0000.HKT", ParentIndex = -1, Unk48 = 266, Path = @"N:\GR\data\Model\cutscene\scn\s45_01_0010\cut0020\hkx\a0000.HKT" });
        var c1 = new MQB.Cut { Name = "cut0011", Duration = 10, Timelines = [] };
        var t1 = new MQB.Timeline();
        t1.Events.Add(new MQB.Event { ResourceIndex = 0, Duration = 10 });
        c1.Timelines.Add(t1);
        var c2 = new MQB.Cut { Name = "cut0020", Duration = 10, Timelines = [] };
        var t2 = new MQB.Timeline();
        t2.Events.Add(new MQB.Event { ResourceIndex = 1, Duration = 10 });
        c2.Timelines.Add(t2);
        mqb.Cuts.AddRange([c1, c2]);
        var files = CutsceneCast.CutFiles(mqb);
        Expect(files["cut0011"] == "a0001" && files["cut0020"] == "a0000", string.Join(" ", files));
        Expect(CutsceneCast.RenameHkts(mqb) == 1 && mqb.Resources[0].Name == "a0000.HKT" && mqb.Resources[0].Path!.EndsWith(@"\cut0011\hkx\a0000.HKT"), mqb.Resources[0].Path ?? "");

        const string dir = @"N:\GR\data\INTERROOT_win64\cutscene\s45_01_0010\";
        var bnd = new BND4();
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, dir + @"cut0000\a0000.hkx", [1]));
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 110_001, dir + @"cut0011\a0001.hkx", [2]));
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 200_000, dir + @"cut0020\a0000.hkx", [3]));
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 100_000_000, dir + "s45_01_0010.mqb", [4]));
        var stats = new CutsceneCast.CastStats();
        CutsceneCast.LayOutCutFiles(bnd, files, stats);
        Expect(bnd.Files.Count == 3 && bnd.Files[0].ID == 110_000 && bnd.Files[0].Name == dir + @"cut0011\a0000.hkx" && bnd.Files[1].ID == 200_000 && bnd.Files[2].ID == 100_000_000,
            string.Join(" ", bnd.Files.Select(f => $"[{f.ID}]{f.Name}")));
        Expect(stats.FilesRenamed == 1 && stats.FilesLeftOut == 1 && stats.Notes.Count == 0, stats.ToString());
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
