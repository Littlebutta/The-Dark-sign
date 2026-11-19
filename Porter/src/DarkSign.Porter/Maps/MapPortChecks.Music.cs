using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> MusicChecks()
    {
        yield return ("music: DS3 music regions become tracks with zones, switch and playing flags; a boss region starts off, Firelink's on", MusicPlan);
        yield return ("music: DS3 music lines set the track's flag (phase change: one off, the next on); Elden Ring music plays are left out", MusicScriptLines);
        yield return ("music: each track's zone event and its start in event 0 have Elden Ring's layouts and survive a write", MusicZoneEvents);
        yield return ("music: the region port carries music regions and their children as script regions with the zone entity IDs", MusicRegions);
        yield return ("music: layers mix into one track, the DLL's list names flag, file and loop points", MusicMixAndList);
        yield return ("music: ported enemies get the threat level most Elden Ring enemies have (no Elden Ring battle music)", MusicThreat);
    }

    // DarkScript3's ds3-common layouts of the music instructions (bank 2010).
    private const string Ds3MusicDefs = """
    { "main_classes": [
      { "index": 2010, "instrs": [
        { "name": "Play BGM", "index": 1, "args": [
          { "name": "Should Play", "type": 0 }, { "name": "Slot Number", "type": 1 }, { "name": "Entity ID", "type": 5 },
          { "name": "Sound Type", "type": 5 }, { "name": "Sound ID", "type": 5 } ] },
        { "name": "Play SE", "index": 2, "args": [
          { "name": "Entity ID", "type": 5 }, { "name": "Sound Type", "type": 5 }, { "name": "Sound ID", "type": 5 } ] },
        { "name": "Set Map Sound State", "index": 3, "args": [ { "name": "Entity ID", "type": 5 }, { "name": "Enable/Disable", "type": 0 } ] },
        { "name": "Enable Boss Map Sound", "index": 4, "args": [ { "name": "Entity ID", "type": 5 }, { "name": "Enable/Disable", "type": 0 } ] },
        { "name": "Enable Map Sound With Fade", "index": 6, "args": [
          { "name": "Entity ID", "type": 5 }, { "name": "Enable/Disable", "type": 0 }, { "name": "Fade Time", "type": 6 } ] }
      ] }
    ] }
    """;

    private const string ErMusicDefs = """
    { "main_classes": [
      { "index": 2010, "instrs": [
        { "name": "Play SE", "index": 2, "args": [
          { "name": "Entity ID", "type": 2 }, { "name": "Sound Type", "type": 5 }, { "name": "Sound ID", "type": 5 } ] }
      ] }
    ] }
    """;

    /// <summary>Iudex Gundyr's two phase regions (boxes with entity IDs), Firelink's region (no entity) widened by a
    /// child region, a point-shaped track, and a wind emitter that isn't music.</summary>
    private static MSB3 Ds3WithMusic()
    {
        var ds3 = SyntheticDs3();
        MSB3.Region.Sound Bgm(string name, int entity, int sound, MSB.Shape shape) =>
            new() { Name = name, EntityID = entity, SoundID = sound, SoundType = MSB3.Region.Sound.SndType.BGM, Shape = shape, Position = new Vector3(1, 2, 3) };
        ds3.Regions.Sounds.Add(Bgm("gundyr 1", 4004800, 400000, new MSB.Shape.Box { Width = 40, Depth = 40, Height = 20 }));
        ds3.Regions.Sounds.Add(Bgm("gundyr 2", 4004801, 400001, new MSB.Shape.Box { Width = 40, Depth = 40, Height = 20 }));
        var shrine = Bgm("firelink", 0, 400100, new MSB.Shape.Box { Width = 30, Depth = 30, Height = 15 });
        shrine.ChildRegionNames[0] = "firelink annex";
        ds3.Regions.Sounds.Add(shrine);
        ds3.Regions.Sounds.Add(Bgm("firelink annex", 0, 0, new MSB.Shape.Sphere { Radius = 8 }));
        ds3.Regions.Sounds.Add(Bgm("anywhere", 0, 400200, new MSB.Shape.Point()));
        ds3.Regions.Sounds.Add(new MSB3.Region.Sound { Name = "wind", SoundID = 5 });
        return ds3;
    }

    private static EMEVD Ds3MusicScript()
    {
        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        var fight = new EMEVD.Event(14005800, EMEVD.Event.RestBehaviorType.Restart);
        fight.Instructions.Add(RunEvents.Make(2010, 4, 4004800, (byte)1));             // the fight starts
        fight.Instructions.Add(RunEvents.Make(2010, 4, 4004800, (byte)0));             // phase 2
        fight.Instructions.Add(RunEvents.Make(2010, 4, 4004801, (byte)1));
        fight.Instructions.Add(RunEvents.Make(2010, 6, 4004801, (byte)0, 3f));         // defeated
        fight.Instructions.Add(RunEvents.Make(2010, 2, 4000800, 6, 400555));           // a music play by ID
        fight.Instructions.Add(RunEvents.Make(2010, 1, (byte)1, (ushort)0, 0, 6, 400300)); // Play BGM by ID
        script.Events.Add(fight);
        // A line that switches a region passed in by the caller stays a no-op.
        var passed = new EMEVD.Event(14005801, EMEVD.Event.RestBehaviorType.Default);
        passed.Instructions.Add(RunEvents.Make(2010, 4, 0, (byte)1));
        passed.Parameters.Add(new EMEVD.Parameter(0, 0, 0, 4));
        script.Events.Add(passed);
        return script;
    }

    private static MapMusic.Plan MusicPlanFor(EMEVD script) =>
        MapMusic.Build([(new MapSlots.Slot(Ds3Id, ErId, "Firelink"), Ds3WithMusic())], [new MapMusic.Script(Ds3Id, script)], Emedf.Parse(Ds3MusicDefs));

    private static void MusicPlan()
    {
        var plan = MusicPlanFor(Ds3MusicScript());
        Expect(plan.Sources.Count == 5, $"four music regions with a sound and one Play BGM track ({plan.Sources.Count}): {string.Join("; ", plan.Sources)}");
        var gundyr1 = plan.ByEntity[4004800];
        var gundyr2 = plan.ByEntity[4004801];
        var shrine = plan.Sources.Single(s => s.Name == "firelink");
        var anywhere = plan.Sources.Single(s => s.Name == "anywhere");
        var direct = plan.Direct[(Ds3Id, 400300)];
        Expect(gundyr1.Sound == "m000400000" && gundyr1.OnLines == 1 && gundyr1.OffLines == 1 && !gundyr1.DefaultOn, gundyr1.ToString());
        Expect(gundyr2.OnLines == 1 && gundyr2.OffLines == 1 && !gundyr2.DefaultOn, gundyr2.ToString());
        Expect(shrine.DefaultOn && !shrine.Scripted && shrine.SwitchFlag == 0, $"no script switches Firelink's music: on at load, no switch flag ({shrine})");
        Expect(gundyr1.Zones.Single().Entity == 45004800 && !gundyr1.Zones[0].NewEntity, "a region with an entity ID keeps it, moved to the slot");
        Expect(shrine.Zones.Count == 2 && shrine.Zones.All(z => z.NewEntity) && shrine.Zones[0].Entity == 45009700 && shrine.Zones[1].Entity == 45009701,
            $"Firelink and its child get new entity IDs at 9700+ ({string.Join(",", shrine.Zones.Select(z => z.Entity))})");
        Expect(anywhere.Zones.Count == 0, "a point-shaped region plays anywhere while on");
        Expect(direct.Sound == "m000400300" && direct.OnLines == 1 && !direct.DefaultOn, direct.ToString());
        var flags = plan.Sources.SelectMany(s => new[] { s.SwitchFlag, s.PlayFlag }).Where(f => f != 0).ToList();
        Expect(flags.Distinct().Count() == flags.Count && flags.All(f => f >= MapMusic.FirstFlag && f < MapMusic.FirstFlag + MapMusic.MaxFlags),
            "every flag its own, in the reset-on-load range 1039292000-2099");
        Expect(MapMusic.FirstFlag == 1039292000 && MapMusic.FirstFlag % 10000 is >= 2000 and < 2100, "music flags sit in JortPob's reset-on-load range");
        Expect(MapMusic.FirstFlag + MapMusic.MaxFlags <= RunEvents.StartDs3Request, "music flags stop before the run's request flags");
        Expect(MapMusic.FirstEvent + MapMusic.MaxEvents <= RunEvents.StartEventId, "music events stop before the run events");
        Expect(plan.Sources.Select(s => s.EventId).Distinct().Count() == 5, "one event each");
        // The same input gives the same numbers (every PC builds the same flags: co-op).
        var again = MusicPlanFor(Ds3MusicScript());
        Expect(again.Sources.Select(s => (s.SwitchFlag, s.PlayFlag, s.EventId)).SequenceEqual(plan.Sources.Select(s => (s.SwitchFlag, s.PlayFlag, s.EventId))), "numbers are stable");
    }

    private static void MusicScriptLines()
    {
        var script = Ds3MusicScript();
        var plan = MusicPlanFor(script);
        var port = new EventPort(Emedf.Parse(Ds3MusicDefs), Emedf.Parse(ErMusicDefs), [(Ds3Id, ErId)], new FlagMap()) { Music = plan, MusicMap = Ds3Id };
        var converted = port.Convert(script, EMEVD.Game.Sekiro, id => Ds3Id.OwnsDs3Id(id) ? Ds3Id.MoveEntity((int)id, ErId) : id);
        var lines = converted.Events[0].Instructions;
        (uint Flag, byte State) SetFlag(EMEVD.Instruction ins)
        {
            Expect(ins.Bank == 2003 && ins.ID == 66 && ins.ArgData.Length >= 9, $"Set Event Flag ({ins.Bank}[{ins.ID}])");
            return (BitConverter.ToUInt32(ins.ArgData, 4), ins.ArgData[8]);
        }
        var g1 = plan.ByEntity[4004800].SwitchFlag;
        var g2 = plan.ByEntity[4004801].SwitchFlag;
        Expect(SetFlag(lines[0]) == (g1, 1), "the fight switches phase 1 on");
        Expect(SetFlag(lines[1]) == (g1, 0) && SetFlag(lines[2]) == (g2, 1), "phase 2: phase 1 off, phase 2 on");
        Expect(SetFlag(lines[3]) == (g2, 0), "fade-out form switches it off too");
        Expect(lines[4].Bank == 1001 && lines[4].ID == 0, "a DS3 music play by ID is left out (Elden Ring would play its own track of that number)");
        Expect(SetFlag(lines[5]) == (plan.Direct[(Ds3Id, 400300)].SwitchFlag, 1), "Play BGM switches its track on");
        var passed = converted.Events[1].Instructions[0];
        Expect(passed.Bank == 1001 && converted.Events[1].Parameters.Count == 0, "a passed-in region stays a no-op, its parameter dropped");
        Expect(plan.Converted == 5 && plan.MusicPlaysLeftOut == 1 && plan.PassedIn == 1, $"counts {plan.Converted}/{plan.MusicPlaysLeftOut}/{plan.PassedIn}");
        // Without a plan the lines stay as before: no Elden Ring form, no-ops (Play SE keeps its by-name mapping).
        var off = new EventPort(Emedf.Parse(Ds3MusicDefs), Emedf.Parse(ErMusicDefs), [(Ds3Id, ErId)], new FlagMap())
            .Convert(script, EMEVD.Game.Sekiro, id => id).Events[0].Instructions;
        Expect(off[0].Bank == 1001 && off[4].Bank == 2010 && off[4].ID == 2, "without a plan nothing changes");
        // A region that starts on (only turned off by a script) gets a "switched off" flag: off -> set, on -> clear.
        var onScript = new EMEVD(EMEVD.Game.DarkSouls3);
        var e = new EMEVD.Event(14000100, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(RunEvents.Make(2010, 3, 4004800, (byte)0));
        onScript.Events.Add(e);
        var onPlan = MusicPlanFor(onScript);
        var src = onPlan.ByEntity[4004800];
        Expect(src.DefaultOn && src.SwitchFlag != 0, "only switched off: starts on");
        var onLines = new EventPort(Emedf.Parse(Ds3MusicDefs), Emedf.Parse(ErMusicDefs), [(Ds3Id, ErId)], new FlagMap()) { Music = onPlan, MusicMap = Ds3Id }
            .Convert(onScript, EMEVD.Game.Sekiro, id => id).Events[0].Instructions;
        Expect(SetFlag(onLines[0]) == (src.SwitchFlag, 1), "turning a start-on region off sets its switched-off flag");
    }

    private static void MusicZoneEvents()
    {
        var plan = MusicPlanFor(Ds3MusicScript());
        var script = new EMEVD(EMEVD.Game.Sekiro);
        script.Events.Add(new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        int added = plan.AddTo(script, ErId);
        Expect(added == 5 && script.Events.Count == 6, $"one event per track ({added})");
        var constructor = script.Events[0].Instructions;
        Expect(constructor.Count == 5 && constructor.All(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length == 12), "event 0 starts each");
        var gundyr = plan.ByEntity[4004800];
        var ev = script.Events.Single(x => x.ID == gundyr.EventId);
        Expect(ev.RestBehavior == EMEVD.Event.RestBehaviorType.Default, "zone events run in the map, not on rest");
        var ins = ev.Instructions;
        // Start: switch flag on (AND 1), in its zone (OR 1 -> AND 1), MAIN; set playing; stop: switch off (OR 2) or out (AND 3 -> OR 2).
        Expect(ins[0].Bank == 3 && ins[0].ID == 0 && (sbyte)ins[0].ArgData[0] == 1 && ins[0].ArgData[1] == 1 && BitConverter.ToUInt32(ins[0].ArgData, 4) == gundyr.SwitchFlag, "waits for the switch");
        Expect(ins[1].Bank == 3 && ins[1].ID == 2 && (sbyte)ins[1].ArgData[0] == -1 && ins[1].ArgData[1] == 1 && BitConverter.ToUInt32(ins[1].ArgData, 4) == 10000
               && BitConverter.ToUInt32(ins[1].ArgData, 8) == 45004800 && BitConverter.ToInt32(ins[1].ArgData, 12) == 1, "and the player in the zone");
        var set = ins.FindIndex(i => i.Bank == 2003 && i.ID == 66);
        Expect(set > 0 && BitConverter.ToUInt32(ins[set].ArgData, 4) == gundyr.PlayFlag && ins[set].ArgData[8] == 1, "turns its playing flag on");
        var clear = ins.FindLastIndex(i => i.Bank == 2003 && i.ID == 66);
        Expect(clear > set && BitConverter.ToUInt32(ins[clear].ArgData, 4) == gundyr.PlayFlag && ins[clear].ArgData[8] == 0, "and off again");
        Expect(ins[^1].Bank == 1000 && ins[^1].ID == 4 && ins[^1].ArgData[0] == 1, "then starts over");
        var anywhere = script.Events.Single(x => x.ID == plan.Sources.Single(s => s.Name == "anywhere").EventId);
        Expect(anywhere.Instructions.Count == 2 && anywhere.Instructions[1].ArgData[0] == 0, "an always-on track just turns its flag on and ends");
        var back = EMEVD.Read(script.Write());
        Expect(back.Events.Count == 6 && back.Events.Sum(x => x.Instructions.Count) == script.Events.Sum(x => x.Instructions.Count), "writes and reads back");
        // A rerun replaces the events instead of adding them twice.
        plan.AddTo(script, ErId);
        Expect(script.Events.Count == 6, "added once");
    }

    private static void MusicRegions()
    {
        var ds3 = Ds3WithMusic();
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result = RegionPort.Convert(ds3, layout.Msb, Ds3Id, ErId, new Dictionary<string, string>());
        var msb = MSBE.Read(layout.Msb.Write());
        var ids = msb.Regions.Others.Select(r => r.EntityID).ToHashSet();
        Expect(ids.SetEquals(new uint[] { 45004800, 45004801, 45009700, 45009701 }), $"the four music zones ({string.Join(",", ids)})");
        Expect(msb.Regions.Sounds.Count == 0 && result.LeftOut.GetValueOrDefault("Sound") == 6, "sound emitters stay out (counted)");
        var annex = msb.Regions.Others.Single(r => r.EntityID == 45009701);
        Expect(annex.Shape is MSB.Shape.Sphere { Radius: 8 }, "the child's shape is kept");
        var zones = MapMusic.ZoneRegions(ds3, Ds3Id, ErId);
        Expect(zones.Select(z => z.Zone.Entity).SequenceEqual(new uint[] { 45004800, 45004801, 45009700, 45009701 }), "region port and plan agree");
        // A DS3 entity at 9700 pushes the new numbers on.
        ds3.Parts.MapPieces[0].EntityID = 4009700;
        Expect(MapMusic.ZoneRegions(ds3, Ds3Id, ErId).Where(z => z.Zone.NewEntity).Select(z => z.Zone.Entity).SequenceEqual(new uint[] { 45009701, 45009702 }), "taken numbers are skipped");
    }

    private static void MusicMixAndList()
    {
        var (one, dropped) = MapMusic.Mix([([1000, -1000, 30000, 30000], 2, 48000), ([500, 500], 2, 48000), ([7], 1, 48000)], [1f, 2f, 1f]);
        Expect(dropped == 1, "a mono layer beside a stereo one is left out");
        Expect(one.SequenceEqual(new short[] { 2000, 0, 30000, 30000 }), $"layers add with their gains, clipped ({string.Join(",", one)})");
        var (same, _) = MapMusic.Mix([([1, 2, 3], 1, 44100)], [1f]);
        Expect(same.SequenceEqual(new short[] { 1, 2, 3 }), "one layer at full level stays as it is");
        var plan = MusicPlanFor(Ds3MusicScript());
        var tracks = new Dictionary<string, MapMusic.Track> { ["m000400000"] = new(4800, 96000, 2000, "test") };
        var list = plan.ListLines(tracks).ToList();
        Expect(list.Count == 1, "only tracks with audio are listed");
        var cols = list[0].Split('\t');
        Expect(cols.Length == 7 && uint.Parse(cols[0]) == plan.ByEntity[4004800].PlayFlag && cols[1] == "m000400000.wav" && cols[2] == "4800" && cols[3] == "96000"
               && cols[4] == "2000" && cols[5] == "m45_00_00_00" && cols[6] == "gundyr 1", list[0]);
    }

    private static void MusicThreat()
    {
        var def = new PARAMDEF { ParamType = "NPC_PARAM_ST", DataVersion = 1, FormatVersion = 203, Unicode = true, Fields = [] };
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.u32, "threatLv"));
        var param = new PARAM { ParamType = def.ParamType, Rows = [] };
        param.ApplyParamdef(def);
        foreach (var (id, threat) in new[] { (1, 1u), (2, 1u), (3, 1u), (4, 3u), (5, 6u) })
        {
            var row = new PARAM.Row(id, "", def);
            row["threatLv"].Value = threat;
            param.Rows.Add(row);
        }
        Expect(Enemies.ChrPort.MusicThreatLevel(param) == 1, "the commonest threat level (ordinary enemies)");
        var none = new PARAM { ParamType = "X", Rows = [] };
        Expect(Enemies.ChrPort.MusicThreatLevel(none) is null, "no rows, no value");
    }
}
