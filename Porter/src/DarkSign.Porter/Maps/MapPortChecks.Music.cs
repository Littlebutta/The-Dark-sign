using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> MusicChecks()
    {
        yield return ("music: DS3 music regions become tracks with zones, switch and playing flags; a boss's tracks start off (DS3's shared boss event turns them on)", MusicPlan);
        yield return ("music: DS3 music lines set the track's flag; Elden Ring music plays are left out; a region only turned off starts on", MusicScriptLines);
        yield return ("music: DS3's shared boss event sets the flags of the regions it is started with (fight start, phase 2, boss dead: every boss track off)", MusicSharedBossEvent);
        yield return ("music: each track's zone event and its start in event 0 have Elden Ring's layouts and survive a write", MusicZoneEvents);
        yield return ("music: the region port carries music regions and their children as script regions with the zone entity IDs", MusicRegions);
        yield return ("music: layers mix into one track, the DLL's list names flag, file and loop points", MusicMixAndList);
        yield return ("music: ported enemies get the threat level most Elden Ring enemies have (no Elden Ring battle music)", MusicThreat);
        yield return ("music: stand-ins and plain fallback rows get the quiet threat level too, on copies of Elden Ring's rows", MusicQuietStandIns);
        yield return ("people: a DS3 person stands on a copy of its Elden Ring row with DS3's team, quiet, and DS3's senses", PeopleOnDs3Team);
        yield return ("music: every templated map gets its template's field music and ambience area (no row-0 Limgrave music)", MusicSoundPlaces);
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
          { "name": "Entity ID", "type": 5 }, { "name": "Enable/Disable", "type": 0 }, { "name": "Fade Time", "type": 6 } ] },
        { "name": "UNKNOWN 2010[07]", "index": 7, "args": [ { "name": "Entity ID", "type": 5 } ] }
      ] }
    ] }
    """;

    private const string ErMusicDefs = """
    { "main_classes": [
      { "index": 2003, "instrs": [
        { "name": "Batch Set Event Flags", "index": 22, "args": [
          { "name": "Event Flag ID Start", "type": 2 }, { "name": "Event Flag ID End", "type": 2 }, { "name": "Flag State", "type": 0 } ] },
        { "name": "Set Event Flag", "index": 66, "args": [
          { "name": "Target Event Flag Type", "type": 0 }, { "name": "Target Event Flag ID", "type": 2 }, { "name": "Desired Flag State", "type": 0 } ] }
      ] },
      { "index": 2010, "instrs": [
        { "name": "Play SE", "index": 2, "args": [
          { "name": "Entity ID", "type": 2 }, { "name": "Sound Type", "type": 5 }, { "name": "Sound ID", "type": 5 } ] }
      ] }
    ] }
    """;

    /// <summary>Iudex Gundyr's two phase regions (boxes with entity IDs), the shrine's song (a region a map event
    /// switches), a region no script names (no entity) widened by a child region, a point-shaped track, and a wind
    /// emitter that isn't music.</summary>
    private static MSB3 Ds3WithMusic()
    {
        var ds3 = SyntheticDs3();
        MSB3.Region.Sound Bgm(string name, int entity, int sound, MSB.Shape shape) =>
            new() { Name = name, EntityID = entity, SoundID = sound, SoundType = MSB3.Region.Sound.SndType.BGM, Shape = shape, Position = new Vector3(1, 2, 3) };
        ds3.Regions.Sounds.Add(Bgm("gundyr 1", 4004800, 400000, new MSB.Shape.Box { Width = 40, Depth = 40, Height = 20 }));
        ds3.Regions.Sounds.Add(Bgm("gundyr 2", 4004801, 400001, new MSB.Shape.Box { Width = 40, Depth = 40, Height = 20 }));
        ds3.Regions.Sounds.Add(Bgm("shrine song", 4004450, 400150, new MSB.Shape.Box { Width = 60, Depth = 77, Height = 35 }));
        var shrine = Bgm("firelink", 0, 400100, new MSB.Shape.Box { Width = 30, Depth = 30, Height = 15 });
        shrine.ChildRegionNames[0] = "firelink annex";
        ds3.Regions.Sounds.Add(shrine);
        ds3.Regions.Sounds.Add(Bgm("firelink annex", 0, 0, new MSB.Shape.Sphere { Radius = 8 }));
        ds3.Regions.Sounds.Add(Bgm("anywhere", 0, 400200, new MSB.Shape.Point()));
        ds3.Regions.Sounds.Add(new MSB3.Region.Sound { Name = "wind", SoundID = 5 });
        return ds3;
    }

    /// <summary>DS3's shared boss event with two phases (common_func 20005831, its music lines): the values at bytes
    /// 16 and 20 are the two phases' regions.</summary>
    private const long SharedBossEvent = 20005831;

    private static EMEVD Ds3MusicCommon()
    {
        var common = new EMEVD(EMEVD.Game.DarkSouls3);
        var e = new EMEVD.Event(SharedBossEvent, EMEVD.Event.RestBehaviorType.Restart);
        e.Instructions.Add(RunEvents.Make(2010, 3, 0, (byte)0));   // 0: both regions off
        e.Instructions.Add(RunEvents.Make(2010, 3, 0, (byte)0));   // 1
        e.Instructions.Add(RunEvents.Make(2010, 4, 0, (byte)1));   // 2: the fight starts: phase 1 on
        e.Instructions.Add(RunEvents.Make(2010, 7, 0));            // 3: phase 2's track readied
        e.Instructions.Add(RunEvents.Make(2010, 4, 0, (byte)1));   // 4: phase 2 on
        e.Instructions.Add(RunEvents.Make(2010, 4, -1, (byte)0));  // 5: the boss died: the boss music stops
        e.Instructions.Add(RunEvents.Make(2010, 6, 0, (byte)0, 5f)); // 6: region, state and fade time all passed in
        foreach (var (line, from) in new[] { (0, 16), (1, 20), (2, 16), (3, 20), (4, 20), (6, 16) })
            e.Parameters.Add(new EMEVD.Parameter(line, 0, from, 4));
        e.Parameters.Add(new EMEVD.Parameter(6, 4, 24, 1));
        e.Parameters.Add(new EMEVD.Parameter(6, 8, 28, 4));
        common.Events.Add(e);
        return common;
    }

    private static EMEVD Ds3MusicScript()
    {
        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        // Event 50 turns the boss regions off at load, as every DS3 map's does (and a voice region that isn't music).
        var load = new EMEVD.Event(50, EMEVD.Event.RestBehaviorType.Default);
        load.Instructions.Add(RunEvents.Make(2010, 3, 4004800, (byte)0));
        load.Instructions.Add(RunEvents.Make(2010, 3, 4004801, (byte)0));
        load.Instructions.Add(RunEvents.Make(2010, 3, 4003700, (byte)0));
        script.Events.Add(load);
        var shrine = new EMEVD.Event(14005450, EMEVD.Event.RestBehaviorType.Default);
        shrine.Instructions.Add(RunEvents.Make(2010, 6, 4004450, (byte)0, 5f));        // off, on the shrine's floor: on
        shrine.Instructions.Add(RunEvents.Make(2010, 6, 4004450, (byte)1, 5f));
        shrine.Instructions.Add(RunEvents.Make(2010, 2, 4000800, 6, 400555));           // a music play by ID
        shrine.Instructions.Add(RunEvents.Make(2010, 1, (byte)1, (ushort)0, 0, 6, 400300)); // Play BGM by ID
        script.Events.Add(shrine);
        // The boss's fight event starts DS3's shared boss event with its flags, arena and the two phases' regions
        // (DS3's Initialize Common Event: the event, then its values).
        var fight = new EMEVD.Event(14005820, EMEVD.Event.RestBehaviorType.Default);
        fight.Instructions.Add(RunEvents.Make(2000, 6, (uint)SharedBossEvent, 14000800, 14005805, 14005822, 4002800, 4004800, 4004801, 1, 0));
        script.Events.Add(fight);
        return script;
    }

    private static MapMusic.Plan MusicPlanFor(EMEVD script, EMEVD? common = null) =>
        MapMusic.Build([(new MapSlots.Slot(Ds3Id, ErId, "Firelink"), Ds3WithMusic())],
            common is null ? [new MapMusic.Script(Ds3Id, script)] : [new MapMusic.Script(Ds3Id, script), new MapMusic.Script(null, common)], Emedf.Parse(Ds3MusicDefs));

    private static void MusicPlan()
    {
        var plan = MusicPlanFor(Ds3MusicScript(), Ds3MusicCommon());
        Expect(plan.Sources.Count == 6, $"five music regions with a sound and one Play BGM track ({plan.Sources.Count}): {string.Join("; ", plan.Sources)}");
        var gundyr1 = plan.ByEntity[4004800];
        var gundyr2 = plan.ByEntity[4004801];
        var song = plan.ByEntity[4004450];
        var shrine = plan.Sources.Single(s => s.Name == "firelink");
        var anywhere = plan.Sources.Single(s => s.Name == "anywhere");
        var direct = plan.Direct[(Ds3Id, 400300)];
        // Phase 1: off in event 50, off and on in the shared event, and its passed-in state (1) once more.
        Expect(gundyr1.Sound == "m000400000" && gundyr1.OnLines == 2 && gundyr1.OffLines == 2 && !gundyr1.StartsOn && gundyr1.Boss, gundyr1.ToString());
        Expect(gundyr2.OnLines == 1 && gundyr2.OffLines == 2 && !gundyr2.StartsOn && gundyr2.Boss, gundyr2.ToString());
        Expect(song.OnLines == 1 && song.OffLines == 1 && !song.StartsOn && !song.Boss, song.ToString());
        Expect(shrine.StartsOn && !shrine.Scripted && shrine.SwitchFlag == 0, $"no script switches this region: on at load, no switch flag ({shrine})");
        Expect(gundyr1.Zones.Single().Entity == 45004800 && !gundyr1.Zones[0].NewEntity, "a region with an entity ID keeps it, moved to the slot");
        Expect(shrine.Zones.Count == 2 && shrine.Zones.All(z => z.NewEntity) && shrine.Zones[0].Entity == 45009700 && shrine.Zones[1].Entity == 45009701,
            $"the region without an ID and its child get new entity IDs at 9700+ ({string.Join(",", shrine.Zones.Select(z => z.Entity))})");
        Expect(anywhere.Zones.Count == 0, "a point-shaped region plays anywhere while on");
        Expect(direct.Sound == "m000400300" && direct.OnLines == 1 && !direct.StartsOn && !direct.Boss, direct.ToString());
        var flags = plan.Sources.SelectMany(s => new[] { s.SwitchFlag, s.PlayFlag }).Where(f => f != 0).ToList();
        Expect(flags.Distinct().Count() == flags.Count && flags.All(f => f >= MapMusic.FirstFlag && f < MapMusic.Plan.SpareFlag),
            "every flag its own, in the reset-on-load range 1039295000-5997 (5998 is the spare, 5999 the block's probe)");
        Expect(plan.FirstBossSwitch == MapMusic.FirstFlag && plan.LastBossSwitch == MapMusic.FirstFlag + 1
               && gundyr1.SwitchFlag == plan.FirstBossSwitch && gundyr2.SwitchFlag == plan.LastBossSwitch,
            $"the boss tracks' switch flags come first, in one run ({plan.FirstBossSwitch}-{plan.LastBossSwitch})");
        Expect(song.SwitchFlag > plan.LastBossSwitch && direct.SwitchFlag > plan.LastBossSwitch, "the other tracks' switch flags are outside that run");
        Expect(plan.SwitchFor(4004800) == gundyr1.SwitchFlag && plan.SwitchFor(4003700) == MapMusic.Plan.SpareFlag && plan.SwitchFor(0) == MapMusic.Plan.SpareFlag
               && plan.SwitchFor(-1) == MapMusic.Plan.SpareFlag, "a passed region that isn't a music track gets the spare flag");
        Expect(MapMusic.FirstFlag == 1039295000 && DarkSign.Core.Preflight.JortPobLayout(MapMusic.FirstFlag) == "temporary"
               && DarkSign.Core.Preflight.JortPobLayout(MapMusic.Plan.SpareFlag) == "temporary" && MapMusic.Plan.SpareFlag % 1000 != 999, "music flags sit in JortPob's reset-on-load range 5000-5999");
        Expect(MapMusic.FirstFlag > BossKits.FirstFlag + BossKits.FlagCount, "music flags are clear of the boss kits' 2500-2999");
        Expect(MapMusic.MaxFlags >= 2 * MapMusic.MaxEvents, "room for every track the events allow, each switched one taking two flags");
        Expect(MapMusic.FirstEvent + MapMusic.MaxEvents <= RunEvents.StartEventId, "music events stop before the run events");
        Expect(plan.Sources.Select(s => s.EventId).Distinct().Count() == 6, "one event each");
        // The same input gives the same numbers (every PC builds the same flags: co-op).
        var again = MusicPlanFor(Ds3MusicScript(), Ds3MusicCommon());
        Expect(again.Sources.Select(s => (s.SwitchFlag, s.PlayFlag, s.EventId)).SequenceEqual(plan.Sources.Select(s => (s.SwitchFlag, s.PlayFlag, s.EventId))), "numbers are stable");
        // What 1.0.479 did: without the shared event's lines a boss region looked like one that plays from the start.
        var alone = MusicPlanFor(Ds3MusicScript());
        Expect(alone.ByEntity[4004800].StartsOn && !alone.ByEntity[4004800].Boss, "(the map script alone has no line that turns a boss track on)");
    }

    private static (uint Flag, byte State) FlagLine(EMEVD.Instruction ins)
    {
        Expect(ins.Bank == 2003 && ins.ID == 66 && ins.ArgData.Length >= 9, $"Set Event Flag ({ins.Bank}[{ins.ID}])");
        return (BitConverter.ToUInt32(ins.ArgData, 4), ins.ArgData[8]);
    }

    private static EventPort MusicPort(MapMusic.Plan? plan, EMEVD script, EMEVD? common = null)
    {
        var port = new EventPort(Emedf.Parse(Ds3MusicDefs), Emedf.Parse(ErMusicDefs), [(Ds3Id, ErId)], new FlagMap()) { Music = plan, MusicMap = Ds3Id };
        if (common is not null)
            port.Learn(common, common: true);
        port.Learn(script);
        return port;
    }

    private static void MusicScriptLines()
    {
        var script = Ds3MusicScript();
        var plan = MusicPlanFor(script, Ds3MusicCommon());
        var converted = MusicPort(plan, script, Ds3MusicCommon()).Convert(script, EMEVD.Game.Sekiro, id => id is 0 or 50 || !Ds3Id.OwnsDs3Id(id) ? id : Ds3Id.MoveEntity((int)id, ErId));
        var load = converted.Events[0].Instructions;
        var g1 = plan.ByEntity[4004800].SwitchFlag;
        var g2 = plan.ByEntity[4004801].SwitchFlag;
        var song = plan.ByEntity[4004450].SwitchFlag;
        Expect(FlagLine(load[0]) == (g1, 0) && FlagLine(load[1]) == (g2, 0), "event 50 turns the boss tracks' flags off");
        Expect(load[2].Bank == 1001 && load[2].ID == 0, "a voice region's line stays a no-op");
        var lines = converted.Events[1].Instructions;
        Expect(FlagLine(lines[0]) == (song, 0) && FlagLine(lines[1]) == (song, 1), "the fade form switches the shrine's song off and on");
        Expect(lines[2].Bank == 1001 && lines[2].ID == 0, "a DS3 music play by ID is left out (Elden Ring would play its own track of that number)");
        Expect(FlagLine(lines[3]) == (plan.Direct[(Ds3Id, 400300)].SwitchFlag, 1), "Play BGM switches its track on");
        Expect(plan.Converted == 5 && plan.MusicPlaysLeftOut == 1, $"counts {plan.Converted}/{plan.MusicPlaysLeftOut}");
        // Without a plan the lines stay as before: no Elden Ring form, no-ops (Play SE keeps its by-name mapping).
        var off = MusicPort(null, script).Convert(script, EMEVD.Game.Sekiro, id => id).Events[1].Instructions;
        Expect(off[0].Bank == 1001 && off[2].Bank == 2010 && off[2].ID == 2, "without a plan nothing changes");
        // A region only ever turned off starts on: its flag is set on when the map loads, and the line turns it off.
        var onScript = new EMEVD(EMEVD.Game.DarkSouls3);
        var e = new EMEVD.Event(14000100, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(RunEvents.Make(2010, 3, 4004800, (byte)0));
        onScript.Events.Add(e);
        var onPlan = MusicPlanFor(onScript);
        var src = onPlan.ByEntity[4004800];
        Expect(src.StartsOn && src.SwitchFlag != 0 && !src.Boss, "only switched off: starts on");
        var onLines = MusicPort(onPlan, onScript).Convert(onScript, EMEVD.Game.Sekiro, id => id).Events[0].Instructions;
        Expect(FlagLine(onLines[0]) == (src.SwitchFlag, 0), "turning it off clears its flag");
        Expect(onPlan.LoadLines(ErId).Select(FlagLine).SequenceEqual([(src.SwitchFlag, (byte)1)]), "and the map's load turns its flag on");
    }

    private static void MusicSharedBossEvent()
    {
        var script = Ds3MusicScript();
        var common = Ds3MusicCommon();
        var plan = MusicPlanFor(script, common);
        var g1 = plan.ByEntity[4004800].SwitchFlag;
        var g2 = plan.ByEntity[4004801].SwitchFlag;
        var port = MusicPort(plan, script, common);
        // The line that starts the shared event passes the regions' switch flags where it passed the regions.
        var start = port.Convert(script, EMEVD.Game.Sekiro, id => id).Events[2].Instructions[0];
        Expect(start.Bank == 2000 && start.ID == 6 && start.ArgData.Length == 8 + 9 * 4 - 4, $"Initialize Common Event with Elden Ring's slot ({start.ArgData.Length} bytes)");
        uint Value(int at) => BitConverter.ToUInt32(start.ArgData, 8 + at);
        Expect(Value(16) == g1 && Value(20) == g2, $"the two regions became their switch flags ({Value(16)}, {Value(20)})");
        Expect(Value(12) == 4002800 && Value(24) == 1 && Value(28) == 0, "values the music lines don't take stay as they were");
        // The shared event: each music line sets the flag it is passed.
        port.MusicMap = null;
        var e = port.Convert(common, EMEVD.Game.Sekiro, id => id).Events[0];
        var lines = e.Instructions;
        long ParamAt(int line, int target) => e.Parameters.SingleOrDefault(p => p.InstructionIndex == line && p.TargetStartByte == target)?.SourceStartByte ?? -1;
        Expect(e.Instructions.Count == 7, "line for line (skips and labels keep their place)");
        Expect(FlagLine(lines[0]).State == 0 && ParamAt(0, 4) == 16 && FlagLine(lines[1]).State == 0 && ParamAt(1, 4) == 20, "at the start both tracks' flags go off");
        Expect(FlagLine(lines[2]).State == 1 && ParamAt(2, 4) == 16, "the fight starts: the first value's flag on");
        Expect(lines[3].Bank == 1001 && !e.Parameters.Any(p => p.InstructionIndex == 3), "readying the next track has no Elden Ring form: a no-op without its value");
        Expect(FlagLine(lines[4]).State == 1 && ParamAt(4, 4) == 20, "phase 2: the second value's flag on (the DLL plays the flag turned on last)");
        Expect(lines[5].Bank == 2003 && lines[5].ID == 22 && BitConverter.ToUInt32(lines[5].ArgData, 0) == plan.FirstBossSwitch
               && BitConverter.ToUInt32(lines[5].ArgData, 4) == plan.LastBossSwitch && lines[5].ArgData[8] == 0 && !e.Parameters.Any(p => p.InstructionIndex == 5),
            "the boss died (entity -1, off): every boss track's flag off in one line");
        Expect(lines[6].Bank == 2003 && lines[6].ID == 66 && ParamAt(6, 4) == 16 && ParamAt(6, 8) == 24
               && e.Parameters.Single(p => p.InstructionIndex == 6 && p.TargetStartByte == 8).ByteCount == 1
               && e.Parameters.Count(p => p.InstructionIndex == 6) == 2,
            "a passed-in state fills the flag state; a fade time has no place and is dropped");
        Expect(e.Parameters.All(p => p.TargetStartByte != 0), "no passed-in value lands on the flag type");
        Expect(plan.PassedIn == 5 && plan.BossStops == 1, $"counts {plan.PassedIn}/{plan.BossStops}");
        // A value an event uses as a music region and as a character moves as the character: the music line is left out.
        var mixed = new EMEVD(EMEVD.Game.DarkSouls3);
        var m = new EMEVD.Event(20009999, EMEVD.Event.RestBehaviorType.Default);
        m.Instructions.Add(RunEvents.Make(2010, 4, 0, (byte)1));
        m.Instructions.Add(RunEvents.Make(2010, 2, 0, 5, 777));
        m.Parameters.Add(new EMEVD.Parameter(0, 0, 0, 4));
        m.Parameters.Add(new EMEVD.Parameter(1, 0, 0, 4));
        mixed.Events.Add(m);
        var mixedPort = MusicPort(plan, script, mixed);
        mixedPort.MusicMap = null;
        var mixedEvent = mixedPort.Convert(mixed, EMEVD.Game.Sekiro, id => id).Events[0];
        Expect(mixedEvent.Instructions[0].Bank == 1001 && mixedEvent.Instructions[1].Bank == 2010 && mixedEvent.Parameters.Count == 1
               && mixedEvent.Parameters[0].InstructionIndex == 1, "a value used two ways keeps its other use; its music line stays a no-op");
        // Without the music port the shared event's lines are no-ops and the regions move as entities, as before.
        var plain = MusicPort(null, script, common);
        Expect(BitConverter.ToUInt32(plain.Convert(script, EMEVD.Game.Sekiro, id => id).Events[2].Instructions[0].ArgData, 8 + 16) == 45004800, "music off: the regions stay entities");
    }

    private static void MusicZoneEvents()
    {
        var plan = MusicPlanFor(Ds3MusicScript(), Ds3MusicCommon());
        var script = new EMEVD(EMEVD.Game.Sekiro);
        var first = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        first.Instructions.Add(RunEvents.Make(2000, 0, 0, 45005820u, 0u)); // the map's own start line
        script.Events.Add(first);
        int added = plan.AddTo(script, ErId);
        Expect(added == 6 && script.Events.Count == 7, $"one event per track ({added})");
        var constructor = script.Events[0].Instructions;
        var gundyr = plan.ByEntity[4004800];
        // Event 0: the switch flags' start state first (before the map's own events run), then the map's lines, then the starts.
        Expect(constructor.Count == 4 + 1 + 6 && constructor.Take(4).All(i => i.Bank == 2003 && i.ID == 66 && i.ArgData[8] == 0),
            $"event 0 begins by turning the four switch flags off ({constructor.Count} lines)");
        Expect(constructor.Take(4).Select(i => BitConverter.ToUInt32(i.ArgData, 4)).ToHashSet().SetEquals(plan.Sources.Where(s => s.SwitchFlag != 0).Select(s => s.SwitchFlag)),
            "each switch flag once");
        Expect(BitConverter.ToUInt32(constructor[4].ArgData, 4) == 45005820, "the map's own lines follow");
        Expect(constructor.Skip(5).All(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length == 12), "then event 0 starts each zone event");
        var ev = script.Events.Single(x => x.ID == gundyr.EventId);
        Expect(ev.RestBehavior == EMEVD.Event.RestBehaviorType.Default, "zone events run in the map, not on rest");
        var ins = ev.Instructions;
        // Playing flag off; start: switch flag on (AND 1), in its zone (OR 1 -> AND 1), MAIN; set playing; stop: switch off (OR 2) or out (AND 3 -> OR 2).
        Expect(FlagLine(ins[0]) == (gundyr.PlayFlag, 0), "starts with its playing flag off (whatever it was before the load)");
        Expect(ins[1].Bank == 3 && ins[1].ID == 0 && (sbyte)ins[1].ArgData[0] == 1 && ins[1].ArgData[1] == 1 && BitConverter.ToUInt32(ins[1].ArgData, 4) == gundyr.SwitchFlag, "waits for the switch to be on");
        Expect(ins[2].Bank == 3 && ins[2].ID == 2 && (sbyte)ins[2].ArgData[0] == -1 && ins[2].ArgData[1] == 1 && BitConverter.ToUInt32(ins[2].ArgData, 4) == 10000
               && BitConverter.ToUInt32(ins[2].ArgData, 8) == 45004800 && BitConverter.ToInt32(ins[2].ArgData, 12) == 1, "and the player in the zone");
        var set = ins.FindIndex(i => i.Bank == 2003 && i.ID == 66 && i.ArgData[8] == 1);
        Expect(set > 2 && BitConverter.ToUInt32(ins[set].ArgData, 4) == gundyr.PlayFlag, "turns its playing flag on");
        Expect(ins[set + 1].Bank == 3 && ins[set + 1].ID == 0 && (sbyte)ins[set + 1].ArgData[0] == -2 && ins[set + 1].ArgData[1] == 0
               && BitConverter.ToUInt32(ins[set + 1].ArgData, 4) == gundyr.SwitchFlag, "then waits for the switch to go off (or the player to leave)");
        var clear = ins.FindLastIndex(i => i.Bank == 2003 && i.ID == 66);
        Expect(clear > set && FlagLine(ins[clear]) == (gundyr.PlayFlag, 0), "and turns it off again");
        Expect(ins[^1].Bank == 1000 && ins[^1].ID == 4 && ins[^1].ArgData[0] == 1, "then starts over");
        var anywhere = script.Events.Single(x => x.ID == plan.Sources.Single(s => s.Name == "anywhere").EventId);
        Expect(anywhere.Instructions.Count == 2 && anywhere.Instructions[1].ArgData[0] == 0, "an always-on track just turns its flag on and ends");
        var back = EMEVD.Read(script.Write());
        Expect(back.Events.Count == 7 && back.Events.Sum(x => x.Instructions.Count) == script.Events.Sum(x => x.Instructions.Count), "writes and reads back");
    }

    private static void MusicRegions()
    {
        var ds3 = Ds3WithMusic();
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result = RegionPort.Convert(ds3, layout.Msb, Ds3Id, ErId, new Dictionary<string, string>());
        var msb = MSBE.Read(layout.Msb.Write());
        var ids = msb.Regions.Others.Select(r => r.EntityID).ToHashSet();
        Expect(ids.SetEquals(new uint[] { 45004800, 45004801, 45004450, 45009700, 45009701 }), $"the five music zones ({string.Join(",", ids)})");
        Expect(msb.Regions.Sounds.Count == 0 && result.LeftOut.GetValueOrDefault("Sound") == 7, "sound emitters stay out (counted)");
        var annex = msb.Regions.Others.Single(r => r.EntityID == 45009701);
        Expect(annex.Shape is MSB.Shape.Sphere { Radius: 8 }, "the child's shape is kept");
        var zones = MapMusic.ZoneRegions(ds3, Ds3Id, ErId);
        Expect(zones.Select(z => z.Zone.Entity).SequenceEqual(new uint[] { 45004800, 45004801, 45004450, 45009700, 45009701 }), "region port and plan agree");
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
        var watch = plan.WatchLines(tracks).ToList();
        Expect(watch.Count == 1 && watch[0].Split('\t') is var w && uint.Parse(w[0]) == plan.ByEntity[4004800].SwitchFlag && w[1] == "-",
            "the DLL watches the listed track's switch flag: " + string.Join("|", watch));
        var cols = list[0].Split('\t');
        Expect(cols.Length == 7 && uint.Parse(cols[0]) == plan.ByEntity[4004800].PlayFlag && cols[1] == "m000400000.wav" && cols[2] == "4800" && cols[3] == "96000"
               && cols[4] == "2000" && cols[5] == "m45_00_00_00" && cols[6] == "gundyr 1", list[0]);
    }

    private static PARAM ThreatParam(params (int Id, uint Threat)[] rows)
    {
        var def = new PARAMDEF { ParamType = "NPC_PARAM_ST", DataVersion = 1, FormatVersion = 203, Unicode = true, Fields = [] };
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.u32, "threatLv"));
        var param = new PARAM { ParamType = def.ParamType, Rows = [] };
        param.ApplyParamdef(def);
        foreach (var (id, threat) in rows)
        {
            var row = new PARAM.Row(id, $"row {id}", def);
            row["threatLv"].Value = threat;
            param.Rows.Add(row);
        }
        return param;
    }

    private static void MusicQuietStandIns()
    {
        // Elden Ring's rows: three ordinary enemies, a strong one (3) and a field boss (6).
        var er = ThreatParam((1, 1u), (2, 1u), (3, 1u), (4, 3u), (5, 6u));
        var quiet = QuietStandIns.Create(er);
        Expect(quiet is not null && quiet.QuietLevel == 1, "made from Elden Ring's rows, quiet level 1");
        Expect(quiet!.For(1) == 1 && quiet.For(77) == 77 && quiet.For(-1) == -1, "an ordinary or unknown row is kept");
        Expect(quiet.For(5) == QuietStandIns.RowBase + 5 && quiet.For(5) == QuietStandIns.RowBase + 5 && quiet.For(4) == QuietStandIns.RowBase + 4,
            "a louder row is placed on its copy, the same ID every time");
        Expect(quiet.Copies.Count == 2, "two copies: " + string.Join(",", quiet.Copies));
        // The mod's regulation: the same rows; the copies go in with the quiet level, the originals unchanged.
        var mod = ThreatParam((1, 1u), (2, 1u), (3, 1u), (4, 3u), (5, 6u));
        var line = quiet.Write(mod);
        Expect(mod[QuietStandIns.RowBase + 5] is { } boss && Convert.ToInt64(boss["threatLv"].Value) == 1 && boss.Name!.Contains("row 5")
               && mod[QuietStandIns.RowBase + 4] is { } strong && Convert.ToInt64(strong["threatLv"].Value) == 1, line);
        Expect(Convert.ToInt64(mod[5]!["threatLv"].Value) == 6 && Convert.ToInt64(mod[4]!["threatLv"].Value) == 3, "Elden Ring's own rows keep their levels");
        Expect(line.Contains("2 quiet NpcParam copies"), line);
        Expect(quiet.Write(mod).Contains("0 quiet NpcParam copies") , "written once");
        // A copy that can't be written: the parts go back on Elden Ring's rows.
        var msb = new MSBE();
        msb.Parts.Enemies.Add(new MSBE.Part.Enemy { Name = "c1000_9000", NPCParamID = QuietStandIns.RowBase + 5 });
        msb.Parts.Enemies.Add(new MSBE.Part.Enemy { Name = "c1000_9001", NPCParamID = 2 });
        Expect(QuietStandIns.Revert(msb, quiet.Copies.ToDictionary(c => c.Value, c => c.Key)) == 1 && msb.Parts.Enemies[0].NPCParamID == 5
               && msb.Parts.Enemies[1].NPCParamID == 2, "revert puts the stand-in back on row 5");
        // The enemy port's last safety net (plain copies of a template's row) gets the quiet level as well.
        var npc = ThreatParam((1, 1u), (2, 1u), (3, 1u), (5, 6u));
        Expect(Enemies.ChrPort.EnsureNpcRows(npc, [(5, 50010000)]) == 1 && Convert.ToInt64(npc[50010000]!["threatLv"].Value) == 1,
            "a plain copy of a field boss's row is quiet");
    }

    private static PARAM ThinkParam(params (int Id, float Eye, int Logic)[] rows)
    {
        var def = new PARAMDEF { ParamType = "NPC_THINK_PARAM_ST", DataVersion = 1, FormatVersion = 203, Unicode = true, Fields = [] };
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.f32, "eye_dist"));
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.s32, "logicId"));
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.f32, "searchEye_dist"));
        var param = new PARAM { ParamType = def.ParamType, Rows = [] };
        param.ApplyParamdef(def);
        foreach (var (id, eye, logic) in rows)
        {
            var row = new PARAM.Row(id, $"think {id}", def);
            row["eye_dist"].Value = eye;
            row["logicId"].Value = logic;
            row["searchEye_dist"].Value = 35f;
            param.Rows.Add(row);
        }
        return param;
    }

    private static void PeopleOnDs3Team()
    {
        // Elden Ring: a person on team 27 (hostile NPC, as Witch-Hunter Jerren) and an invader on team 6 with a far sight.
        PARAM Npc()
        {
            var p = ThreatParam((523120024, 3u), (523600034, 3u));
            var def = p.AppliedParamdef;
            def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.u8, "teamType"));
            var q = new PARAM { ParamType = def.ParamType, Rows = [] };
            q.ApplyParamdef(def);
            foreach (var (id, team) in new[] { (523120024, (byte)27), (523600034, (byte)6), (1, (byte)6) })
            {
                var row = new PARAM.Row(id, $"row {id}", def);
                row["threatLv"].Value = id == 1 ? 1u : 3u;
                row["teamType"].Value = team;
                q.Rows.Add(row);
            }
            return q;
        }
        var quiet = QuietStandIns.Create(Npc())!;
        quiet.Ds3Think = ThinkParam((27300, 8f, 999));
        // Horace (DS3 26301, team 26) on Jerren's row; the Sword Master (27300, team 6) on Vargram's, with DS3's think row.
        int horace = quiet.ForPerson(523120024, 26301, 26), sword = quiet.ForPerson(523600034, 27300, 6);
        Expect(horace == QuietStandIns.PersonRowBase + 26301 && sword == QuietStandIns.PersonRowBase + 27300, $"copy IDs {horace}, {sword}");
        Expect(quiet.ForPerson(523120024, 26301, 26) == horace, "the same DS3 row on the same stand-in: the same copy");
        Expect(quiet.ForPerson(523600034, 26301, 26) == horace + QuietStandIns.PersonStep, "one DS3 row on another stand-in: the next copy ID");
        int think = quiet.ForPersonThink(523600000, 27300);
        Expect(think == QuietStandIns.PersonRowBase + 27300 && quiet.ForPersonThink(523120000, 99999) == 523120000,
            "DS3's think row gets a copy; one DS3 doesn't have keeps the stand-in's");
        var npc = Npc();
        var erThink = ThinkParam((523600000, 200f, 523600000), (523120000, 30f, 523120000));
        var line = quiet.WritePeople(npc, erThink);
        Expect(npc[horace] is { } h && Convert.ToInt32(h["teamType"].Value) == 26 && Convert.ToInt64(h["threatLv"].Value) == quiet.QuietLevel
               && npc[sword] is { } s && Convert.ToInt32(s["teamType"].Value) == 6, line);
        Expect(Convert.ToInt32(npc[523120024]!["teamType"].Value) == 27, "Elden Ring's own row keeps its team");
        Expect(erThink[think] is { } t && (float)t["eye_dist"].Value == 8f && (int)t["logicId"].Value == 523600000 && (float)t["searchEye_dist"].Value == 0f,
            "the think copy has DS3's sight, no Elden Ring search sight, and keeps the stand-in's AI script");
        Expect(line.Contains("27->26 x1") && line.Contains("1 think copies"), line);
        var msb = new MSBE();
        msb.Parts.Enemies.Add(new MSBE.Part.Enemy { Name = "c0000_9000", NPCParamID = horace, ThinkParamID = think });
        Expect(QuietStandIns.Revert(msb, new Dictionary<int, int> { [horace] = 523120024 }) == 1 && msb.Parts.Enemies[0].NPCParamID == 523120024, "revert");
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

    private static void MusicSoundPlaces()
    {
        var def = new PARAMDEF { ParamType = "MAP_DEFAULT_INFO_PARAM_ST", DataVersion = 1, FormatVersion = 203, Unicode = true, Fields = [] };
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.u32, "EnableFastTravelEventFlagId"));
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.s16, "BgmPlaceInfo"));
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.s16, "EnvPlaceInfo"));
        var param = new PARAM { ParamType = def.ParamType, Rows = [] };
        param.ApplyParamdef(def);
        void Add(int id, uint travel, short bgm, short env)
        {
            var row = new PARAM.Row(id, $"row {id}", def);
            row["EnableFastTravelEventFlagId"].Value = travel;
            row["BgmPlaceInfo"].Value = bgm;
            row["EnvPlaceInfo"].Value = env;
            param.Rows.Add(row);
        }
        Add(0, 0, 0, 0);               // the default: Limgrave's music
        Add(11700000, 77, 999, 9);     // a slot Elden Ring has a row for
        Add(45000000, 55, 180, 18);    // the template colosseum
        var template = MapId.Parse("m45_00_00_00");
        MapSlots.Slot S(string ds3, string er, MapId? t) => new(MapId.Parse(ds3), MapId.Parse(er), ds3, Template: t);
        var slots = new[]
        {
            S("m40_00_00_00", "m45_00_00_00", null),      // the colosseum itself: left alone
            S("m45_00_00_00", "m19_70_00_00", template),  // no row: added
            S("m50_00_00_00", "m11_70_00_00", template),  // has a row: two fields changed
            S("m30_01_00_00", "m45_03_00_00", template),
            new MapSlots.Slot(MapId.Parse("m45_00_00_00"), MapId.Parse("m38_13_00_00"), "probe", template, Probe: true),
        };
        Expect(MapSoundPlace.RowId(MapId.Parse("m19_70_00_00")) == 19700000 && MapSoundPlace.RowId(MapId.Parse("m45_03_00_00")) == 45030000, "row IDs");
        var line = MapSoundPlace.Apply(param, slots);
        Expect(line.Contains("2 MapDefaultInfoParam rows added, 1 changed"), line);
        Expect(param[19700000] is { } pw && Convert.ToInt32(pw["BgmPlaceInfo"].Value) == 180 && Convert.ToInt32(pw["EnvPlaceInfo"].Value) == 18
               && Convert.ToUInt32(pw["EnableFastTravelEventFlagId"].Value) == 0, "a new row is row 0 with the template's music and ambience: " + line);
        Expect(param[11700000] is { } dreg && Convert.ToInt32(dreg["BgmPlaceInfo"].Value) == 180 && Convert.ToUInt32(dreg["EnableFastTravelEventFlagId"].Value) == 77,
            "an existing row keeps everything but the two fields");
        Expect(Convert.ToInt32(param[45000000]!["BgmPlaceInfo"].Value) == 180 && Convert.ToInt32(param[0]!["BgmPlaceInfo"].Value) == 0, "template and row 0 unchanged");
        Expect(param[38130000] is null, "no row for a probe slot");
        Expect(param.Rows.Select(r => r.ID).SequenceEqual(param.Rows.Select(r => r.ID).Order()), "rows stay sorted");
        Expect(MapSoundPlace.Apply(param, slots).Contains("0 MapDefaultInfoParam rows added, 0 changed, 3 already right"), "a second run changes nothing");
        // The sizes the game reads survive a write.
        var back = PARAM.Read(param.Write());
        back.ApplyParamdef(def);
        Expect(back[19700000] is { } r && Convert.ToInt32(r["BgmPlaceInfo"].Value) == 180, "the added row survives a write");
    }
}
