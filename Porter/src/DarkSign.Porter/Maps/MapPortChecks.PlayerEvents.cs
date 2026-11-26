using System.Text;
using DarkSign.Porter.Enemies;
using DarkSign.Porter.Havok;
using HKLib.hk2018;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> PlayerEventChecks()
    {
        yield return ("player event animations: animations passed into an event are read from the lines that start it (the level-up kneel 90250, stand-up 90260); other entities aren't", PlayerEventScripted);
        yield return ("player event animations: DS3's graph gives each state's end behaviour, clip mode and hand-over (kneel 90250 -> kneeling loop 90270)", PlayerEventDs3Graph);
        yield return ("player event animations: the plan follows DS3's hand-overs and leaves alone what Elden Ring has a state for", PlayerEventPlan);
        yield return ("player event animations: DS3's events come over where both games read them alike; SpEffects move to their ported rows; sounds stay out", PlayerEventEvents);
        yield return ("player event animations: each animation gets a state cloned from the Elden Ring state that runs DS3's script, with DS3's ending and hand-over", PlayerEventStates);
    }

    private const string Ds3RotateDefs = """
    { "main_classes": [
      { "index": 2003, "instrs": [
        { "name": "Force Animation Playback", "index": 18, "args": [
          { "name": "Entity ID", "type": 5 }, { "name": "Animation ID", "type": 5 }, { "name": "Should Loop", "type": 0 },
          { "name": "Should Wait For Completion", "type": 0 }, { "name": "Ignore Wait For Transition", "type": 0 }, { "name": "Unknown", "type": 0 }, { "name": "Unknown", "type": 6 } ] } ] },
      { "index": 2004, "instrs": [
        { "name": "Rotate Character", "index": 14, "args": [
          { "name": "Entity ID", "type": 5 }, { "name": "Relative Entity ID", "type": 5 }, { "name": "Play Animation ID", "type": 5 }, { "name": "Should Wait For Completion", "type": 0 } ] } ] }
    ] }
    """;

    private static void PlayerEventScripted()
    {
        // DS3 common_func 20006005 (read 2026-11-26): Rotate Character(player, X0, 69070), Rotate Character(player, X0, X20),
        // Force Animation Playback(player, X24); m40 starts it with (4000700, ..., 90250, 90260, 155).
        var common = new EMEVD();
        var kneel = new EMEVD.Event(20006005, EMEVD.Event.RestBehaviorType.Default);
        kneel.Instructions.Add(RunEvents.Make(2004, 14, 10000, 0, 69070, (byte)0));
        kneel.Parameters.Add(new EMEVD.Parameter(0, 4, 0, 4));
        kneel.Instructions.Add(RunEvents.Make(2004, 14, 10000, 0, 0, (byte)1));
        kneel.Parameters.Add(new EMEVD.Parameter(1, 4, 0, 4));
        kneel.Parameters.Add(new EMEVD.Parameter(1, 8, 20, 4));
        kneel.Instructions.Add(RunEvents.Make(2003, 18, 10000, -1, (byte)0, (byte)1, (byte)0, (byte)0, 1f));
        kneel.Parameters.Add(new EMEVD.Parameter(2, 4, 24, 4));
        common.Events.Add(kneel);
        // An event that plays a passed-in animation on a passed-in entity: only the run on the player counts.
        var any = new EMEVD.Event(20005000, EMEVD.Event.RestBehaviorType.Default);
        any.Instructions.Add(RunEvents.Make(2003, 18, 0, 0, (byte)0, (byte)0, (byte)0, (byte)0, 1f));
        any.Parameters.Add(new EMEVD.Parameter(0, 0, 0, 4));
        any.Parameters.Add(new EMEVD.Parameter(0, 4, 4, 4));
        common.Events.Add(any);
        // Nothing starts this one.
        var orphan = new EMEVD.Event(20005001, EMEVD.Event.RestBehaviorType.Default);
        orphan.Instructions.Add(RunEvents.Make(2003, 18, 10000, 0, (byte)0, (byte)0, (byte)0, (byte)0, 1f));
        orphan.Parameters.Add(new EMEVD.Parameter(0, 4, 0, 4));
        common.Events.Add(orphan);
        var map = new EMEVD();
        var start = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        start.Instructions.Add(RunEvents.Make(2000, 6, 20006005, 4000700, 74000135, 74000137, 0, 1.2f, 90250, 90260, 155));
        start.Instructions.Add(RunEvents.Make(2000, 6, 20006005, 4000725, 74000383, 74000384, 0, 1f, 90280, 90291, -1));
        start.Instructions.Add(RunEvents.Make(2000, 0, 0, 20005000, 4000800, 20003));
        start.Instructions.Add(RunEvents.Make(2000, 0, 1, 20005000, 10000, 60750));
        start.Instructions.Add(RunEvents.Make(2003, 18, 4000800, 3000, (byte)0, (byte)0, (byte)0, (byte)0, 1f));
        map.Events.Add(start);
        var uses = PlayerEventAnims.Scripted([EMEVD.Read(common.Write()), EMEVD.Read(map.Write())], Emedf.Parse(Ds3RotateDefs), out int unresolved);
        var found = uses.Select(u => u.Anim).Distinct().Order().ToList();
        Expect(found.SequenceEqual([60750, 69070, 90250, 90260, 90280, 90291]) && unresolved == 1, $"found {string.Join(" ", found)}, {unresolved} unread");
        Expect(uses.First(u => u.Anim == 90250) is { PassedIn: true, Event: 20006005, Instruction: "Rotate Character" } && !uses.First(u => u.Anim == 69070).PassedIn,
            "each use names its event and instruction");
    }

    /// <summary>A Havok 2014 packfile with the level-up states as DS3's c0000.hkx has them (see <see cref="Ds3BehaviorGraph"/>).</summary>
    private static byte[] Ds3LevelUpGraph()
    {
        var data = new List<byte>();
        var local = new List<(int, int)>();
        var global = new List<(int, int)>();
        var objects = new List<(int Offset, string Class)>();
        int New(string cls, int size)
        {
            objects.Add((data.Count, cls));
            data.AddRange(new byte[size]);
            return objects[^1].Offset;
        }
        void Int(int at, int value) => BitConverter.GetBytes(value).CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data)[at..]);
        var texts = new List<(int At, string Text)>();
        void Name(int at, string text) => texts.Add((at, text));

        int strings = New("hkbBehaviorGraphStringData", 64 + 16);
        local.Add((strings + 16, strings + 64));
        Int(strings + 24, 2);
        Name(strings + 64, "W_Idle");
        Name(strings + 72, "Event90250_to_Event90270");
        int machine = New("hkbStateMachine", 112);
        Name(machine + 56, "LevelUp_SM");
        int kneel = New("hkbStateMachineStateInfo", 112), loop = New("hkbStateMachineStateInfo", 112);
        global.Add((machine + 96, kneel));
        global.Add((machine + 104, loop));
        int Selector(int anim, int end, bool looping)
        {
            int s = New("CustomManualSelectorGenerator", 176);
            Int(s + 152, 11);
            Int(s + 156, anim);
            Int(s + 160, end);
            int clip = New("hkbClipGenerator", 192);
            data[clip + 190] = (byte)(looping ? 1 : 0);
            global.Add((s + 168, clip));
            return s;
        }
        global.Add((kneel + 88, Selector(90250, 0, false)));
        Name(kneel + 96, "Event90250");
        Int(kneel + 104, 87);
        global.Add((loop + 88, Selector(90270, 3, true)));
        Name(loop + 96, "Event90270");
        Int(loop + 104, 89);
        int transitions = New("hkbStateMachineTransitionInfoArray", 32 + 72);
        global.Add((kneel + 80, transitions));
        local.Add((transitions + 16, transitions + 32));
        Int(transitions + 24, 1);
        Int(transitions + 32 + 48, 1);
        Int(transitions + 32 + 52, 89);
        foreach (var (at, text) in texts)
        {
            local.Add((at, data.Count));
            data.AddRange(Encoding.ASCII.GetBytes(text + "\0"));
        }
        while (data.Count % 16 != 0)
            data.Add(0);

        var names = new List<byte>();
        var nameAt = new Dictionary<string, int>();
        foreach (var cls in objects.Select(o => o.Class).Distinct())
        {
            names.AddRange(new byte[5]);
            nameAt[cls] = names.Count;
            names.AddRange(Encoding.ASCII.GetBytes(cls + "\0"));
        }
        while (names.Count % 16 != 0)
            names.Add(0xFF);

        var file = new List<byte>(new byte[64 + 3 * 64]);
        void Put(int at, byte[] bytes) => bytes.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(file)[at..]);
        Put(0, BitConverter.GetBytes(0x57E0E057u));
        Put(4, BitConverter.GetBytes(0x10C0C010u));
        file[16] = 8;
        file[17] = 1;
        Put(20, BitConverter.GetBytes(3));
        Put(40, Encoding.ASCII.GetBytes("hk_2014.1.0-r1\0"));
        void Section(int index, string name, int start, int localAt, int globalAt, int virtualAt, int end)
        {
            int o = 64 + index * 64;
            Put(o, Encoding.ASCII.GetBytes(name + "\0"));
            foreach (var (k, v) in new[] { (20, start), (24, localAt), (28, globalAt), (32, virtualAt), (36, end), (40, end), (44, end) })
                Put(o + k, BitConverter.GetBytes(v));
        }
        int namesStart = file.Count;
        file.AddRange(names);
        Section(0, "__classnames__", namesStart, names.Count, names.Count, names.Count, names.Count);
        Section(1, "__types__", file.Count, 0, 0, 0, 0);
        int dataStart = file.Count;
        file.AddRange(data);
        int localAt = data.Count;
        foreach (var (from, to) in local)
        {
            file.AddRange(BitConverter.GetBytes(from));
            file.AddRange(BitConverter.GetBytes(to));
        }
        int globalAt = file.Count - dataStart;
        foreach (var (from, to) in global)
        {
            file.AddRange(BitConverter.GetBytes(from));
            file.AddRange(BitConverter.GetBytes(2));
            file.AddRange(BitConverter.GetBytes(to));
        }
        int virtualAt = file.Count - dataStart;
        foreach (var (offset, cls) in objects)
        {
            file.AddRange(BitConverter.GetBytes(offset));
            file.AddRange(BitConverter.GetBytes(0));
            file.AddRange(BitConverter.GetBytes(nameAt[cls]));
        }
        Section(2, "__data__", dataStart, localAt, globalAt, virtualAt, file.Count - dataStart);
        return [.. file];
    }

    private static void PlayerEventDs3Graph()
    {
        var graph = Ds3BehaviorGraph.Read(Ds3LevelUpGraph());
        Expect(graph.States == 2 && graph.ByAnim.Count == 2, $"{graph.States} states, {graph.ByAnim.Count} animations");
        var kneel = graph.ByAnim[90250];
        Expect(kneel is { Name: "Event90250", Machine: "LevelUp_SM", EndType: Ds3BehaviorGraph.EndNextState, Looping: false }
               && kneel.Next.SequenceEqual([("Event90250_to_Event90270", 90270)]), $"kneel: {kneel}, next {string.Join(" ", kneel.Next)}");
        Expect(graph.ByAnim[90270] is { Name: "Event90270", EndType: Ds3BehaviorGraph.EndNothing, Looping: true, Next.Count: 0 }, $"loop: {graph.ByAnim[90270]}");
        bool refused = false;
        try { Ds3BehaviorGraph.Read(new byte[400]); } catch (InvalidDataException) { refused = true; }
        Expect(refused, "a file that isn't a packfile is refused");
    }

    private static TAE.Animation Ds3Entry(int id, params (int Type, float Start, float End, int[] Values)[] events)
    {
        var entry = new TAE.Animation(id, new TAE.Animation.AnimMiniHeader.Standard(), $"a000_{id:D6}.hkt");
        foreach (var (type, start, end, values) in events)
            entry.Events.Add(new TAE.Event(start, end, type, 0, values.SelectMany(BitConverter.GetBytes).ToArray(), false));
        return entry;
    }

    private static void PlayerEventPlan()
    {
        var tae = new TAE { Format = TAE.TAEFormat.DS3, Animations = [] };
        tae.Animations.Add(Ds3Entry(90250));
        tae.Animations.Add(Ds3Entry(90270));
        tae.Animations.Add(Ds3Entry(20000));
        // DS3's 69070 plays the walk's file (a000_020000), as its entry says.
        tae.Animations.Add(new TAE.Animation(69070, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = 20000 }, "a000_020000.hkt"));
        tae.Animations.Add(Ds3Entry(60750));
        var states = new Dictionary<int, Ds3BehaviorGraph.State>
        {
            [90250] = new("Event90250", "LevelUp_SM", 90250, 0, false, [("Event90250_to_Event90270", 90270)]),
            [90270] = new("Event90270", "LevelUp_SM", 90270, 3, true, []),
            [69070] = new("Event69070", "EventRightArmAdd_SM", 69070, 3, true, []),
            [60750] = new("Event60750", "EventRightArmAdd_SM", 60750, 2, false, []),
            [60999] = new("Event60999", "EventRightArmAdd_SM", 60999, 2, false, []),
        };
        var graph = Ds3BehaviorGraph.Read(Ds3LevelUpGraph());
        var notes = new List<string>();
        var plan = PlayerEventAnims.Plan([60750, 90250], graph, tae, e => e == "W_Event60750", notes);
        Expect(plan.Select(p => p.Id).SequenceEqual([90250, 90270]) && notes.Count == 1 && notes[0].Contains("60750"),
            $"plan {string.Join(" ", plan.Select(p => p.Id))}; {string.Join(" / ", notes)}");
        _ = states;
        Expect(PlayerEventAnims.Source(tae, 69070) is { FileId: 20000 } && PlayerEventAnims.Source(tae, 90250) is { FileId: 90250 } && PlayerEventAnims.Source(tae, 1) is null,
            "an entry that plays another's file names that file");
        notes.Clear();
        Expect(PlayerEventAnims.Plan([69070, 12345], graph, tae, _ => false, notes).Count == 0 && notes.Count == 2, "an animation DS3's graph has no state for is left alone and named");
    }

    private static void PlayerEventEvents()
    {
        // DS3's a000_090250 (read 2026-11-26), shortened.
        var ds3 = Ds3Entry(90250,
            (0, 0f, 2.1f, [54, 0, -1, 6, 0, 0]), (0, 0f, 2.1f, [8, 0, -1, 6]), (0, 0f, 1f, [47, 0, -1, 6]), (16, 0f, 0.267f, []),
            (66, 0f, 2.1f, [150, 0, 0, 0]), (66, 1.5f, 2.1f, [155, 0, 0, 0]), (67, 0f, 1f, [999, 0, 0, 0]),
            (128, 0.167f, 0.267f, [9, 400, 0, 0]), (710, 0f, 2.1f, [257, 0, 0, 0]), (792, 0.7f, 0.8f, [1, 6, -1, 65793]),
            (605, 0f, 0.5f, [0, 3, 0, 1065353216, 0, 0, 0, 0]));
        var lengths = new Dictionary<int, HashSet<int>> { [0] = [16], [16] = [0], [66] = [8], [67] = [8], [605] = [32], [710] = [16], [128] = [16] };
        var proto = new TAE.EventGroup(710);
        var groups = new List<TAE.EventGroup>();
        var dropped = new Dictionary<string, int>();
        var events = PlayerEventAnims.ConvertEvents(ds3, lengths, new Dictionary<int, TAE.EventGroup> { [710] = proto }, id => id is 150 or 155 ? 700_000_000 + id : null, groups, dropped);
        Expect(events.Select(e => e.Type).SequenceEqual([0, 0, 16, 66, 66, 710, 605]), "kept: " + string.Join(" ", events.Select(e => e.Type)));
        Expect(events[0].GetParameterBytes(false).Length == 16 && BitConverter.ToInt32(events[0].GetParameterBytes(false), 0) == 54, "a DS3 flag event takes Elden Ring's length");
        Expect(events[3].GetParameterBytes(false) is { Length: 8 } cast && BitConverter.ToInt32(cast, 0) == 700_000_150
               && BitConverter.ToInt32(events[4].GetParameterBytes(false), 0) == 700_000_155 && events[4].StartTime == 1.5f, "SpEffects are cast from their ported rows, at DS3's times");
        Expect(dropped.GetValueOrDefault("flag 47") == 1 && dropped.GetValueOrDefault("effect 999 (no ported row)") == 1 && dropped.GetValueOrDefault("type 128") == 1
               && dropped.GetValueOrDefault("type 792") == 1, "left out: " + string.Join(", ", dropped.Select(d => $"{d.Value} {d.Key}")));
        Expect(groups.Count == 5 && events.All(e => e.Group is not null && groups.Contains(e.Group)) && !groups.Contains(proto)
               && ReferenceEquals(events[3].Group, events[4].Group), "every event sits in a group of the entry's own, one per type");
        Expect(PlayerEventAnims.CastEffects([ds3]).SequenceEqual([150, 155, 999]), "the effects an entry casts are listed for the enemy port");
    }

    private static BehaviourScript.Function ScriptFunction(string name, byte[] code, params string[] constants)
    {
        var f = new BehaviourScript.Function { Name = name, Code = code, Instructions = (uint)(code.Length / 4) };
        foreach (var c in constants)
            f.Constants.Add(new BehaviourScript.Constant(4, [], c));
        return f;
    }

    private static void PlayerEventStates()
    {
        var strings = new hkbBehaviorGraphStringData
        {
            m_eventNames = ["W_Event60010", "W_Event60000", "W_Event60510", "Event60510_to_Event60511", "W_Event60511"],
            m_animationNames = [@"..\Model\chr\c0000\hkx\a000\a000_060010.hkx"],
        };
        var data = new hkbBehaviorGraphData { m_stringData = strings, m_eventInfos = [new(), new(), new(), new(), new()] };
        hkbStateMachine.StateInfo State(int id, int anim, short internalId) => new()
        {
            m_name = $"Event{anim}", m_stateId = id,
            m_generator = new CustomManualSelectorGenerator
            {
                m_name = $"Event{anim}_CMSG", m_animId = anim, m_animeEndEventType = CustomManualSelectorGenerator.AnimeEndEventType.FireIdleEvent,
                m_generators = [new hkbClipGenerator { m_animationName = $"a000_{anim:D6}", m_name = $"a000_{anim:D6}_hkx_AutoSet_00", m_animationInternalId = internalId }],
            },
        };
        var chained = State(2, 60510, 2);
        chained.m_transitions = new hkbStateMachine.TransitionInfoArray { m_transitions = [new hkbStateMachine.TransitionInfo { m_eventId = 3, m_toStateId = 3 }] };
        var machine = new hkbStateMachine
        {
            m_name = PlayerEventAnims.Machine,
            m_states = [State(0, 60010, 0), State(1, 60000, 1), chained, State(3, 60511, 3)],
            m_wildcardTransitions = new hkbStateMachine.TransitionInfoArray
            {
                m_transitions =
                [
                    new hkbStateMachine.TransitionInfo { m_eventId = 0, m_toStateId = 0 }, new hkbStateMachine.TransitionInfo { m_eventId = 1, m_toStateId = 1 },
                    new hkbStateMachine.TransitionInfo { m_eventId = 2, m_toStateId = 2 }, new hkbStateMachine.TransitionInfo { m_eventId = 4, m_toStateId = 3 },
                ],
            },
        };
        var root = new hkbBehaviorGraph { m_rootGenerator = machine, m_data = data };

        // Donors: plain one-clip states with their wildcard and no transition of their own (60510 hands over: not one).
        var donorStates = PlayerEventAnims.DonorStates(machine, strings.m_eventNames);
        Expect(donorStates.Select(s => s.m_name).SequenceEqual(["Event60010", "Event60000", "Event60511"]), "donors: " + string.Join(" ", donorStates.Select(s => s.m_name)));
        byte[] commonCode = [1, 2, 3, 4], guardedCode = [5, 6, 7, 8, 9, 9, 9, 9];
        var er = new Dictionary<string, BehaviourScript.Function>
        {
            ["Event60010_onUpdate"] = ScriptFunction("Event60010_onUpdate", commonCode, "EventCommonFunction", "TRUE"),
            ["Event60000_onUpdate"] = ScriptFunction("Event60000_onUpdate", guardedCode, "act", "EventCommonFunction"),
            ["Event60000_onDeactivate"] = ScriptFunction("Event60000_onDeactivate", [7, 7, 7, 7], "act"),
        };
        var ds3 = new Dictionary<string, BehaviourScript.Function>
        {
            ["Event90250_onUpdate"] = ScriptFunction("Event90250_onUpdate", commonCode, "EventCommonFunction", "TRUE"),
            ["Event90291_onUpdate"] = ScriptFunction("Event90291_onUpdate", guardedCode, "act", "EventCommonFunction"),
            ["Event90291_onDeactivate"] = ScriptFunction("Event90291_onDeactivate", [7, 7, 7, 7], "act"),
            ["Event69070_onUpdate"] = ScriptFunction("Event69070_onUpdate", [4, 4, 4, 4], "act"),
        };
        var donors = donorStates.Select(s => new PlayerEventAnims.Donor(s.m_name!, PlayerEventAnims.ShapeOf(er, s.m_name!))).ToList();
        var common = PlayerEventAnims.ShapeOf(er, "Event60010");
        Expect(PlayerEventAnims.DonorFor(PlayerEventAnims.ShapeOf(ds3, "Event90250"), donors, common, out bool exactKneel)!.Name == "Event60010" && exactKneel
               && PlayerEventAnims.DonorFor(PlayerEventAnims.ShapeOf(ds3, "Event90291"), donors, common, out bool exactStand)!.Name == "Event60000" && exactStand
               && PlayerEventAnims.DonorFor(PlayerEventAnims.ShapeOf(ds3, "Event90270"), donors, common, out bool exactLoop)!.Name == "Event60511" && exactLoop
               && PlayerEventAnims.DonorFor(PlayerEventAnims.ShapeOf(ds3, "Event69070"), donors, common, out bool exactWalk)!.Name == "Event60010" && !exactWalk,
            "the donor runs the same compiled script as DS3's state; with none, the common one, said so");

        var lines = new List<string>();
        int added = PlayerEventAnims.AddStates(root,
        [
            new(90250, new("Event90250", "LevelUp_SM", 90250, Ds3BehaviorGraph.EndNextState, false, [("Event90250_to_Event90270", 90270)]), "Event60010"),
            new(90270, new("Event90270", "LevelUp_SM", 90270, Ds3BehaviorGraph.EndNothing, true, []), "Event60511"),
            new(90280, new("Event90280", "EventRightArmAdd_SM", 90280, Ds3BehaviorGraph.EndNextState, false, [("Event90280_to_Event90290", 90290)]), "Event60010"),
            new(60430, new("Event60430", "EventRightArmAdd_SM", 60430, Ds3BehaviorGraph.EndIdle, false, []), "Event99999"),
        ], lines);
        Expect(added == 3 && machine.m_states.Count == 7 && lines.Any(l => l.Contains("FAIL 60430")), $"{added} added; {string.Join(" / ", lines)}");
        var kneel = machine.m_states.First(s => s!.m_generator is CustomManualSelectorGenerator { m_animId: 90250 })!;
        var loop = machine.m_states.First(s => s!.m_generator is CustomManualSelectorGenerator { m_animId: 90270 })!;
        var kneelSelector = (CustomManualSelectorGenerator)kneel.m_generator!;
        var kneelClip = (hkbClipGenerator)kneelSelector.m_generators[0]!;
        Expect(kneel.m_name == "Event60010" && kneel.m_stateId == 4 && kneelSelector.m_name == "Event90250_CMSG", "the state keeps the donor's name (its script), under a new ID");
        Expect(kneelClip is { m_animationName: "a000_090250", m_animationInternalId: 4, m_mode: hkbClipGenerator.PlaybackMode.MODE_SINGLE_PLAY, m_name: "a000_090250_hkx_AutoSet_00" }
               && ((hkbClipGenerator)((CustomManualSelectorGenerator)loop.m_generator!).m_generators[0]!) is { m_mode: hkbClipGenerator.PlaybackMode.MODE_LOOPING, m_animationInternalId: 5 },
            "each plays its own clip under a new internal ID, looping where DS3's does");
        Expect(kneelSelector.m_animeEndEventType == CustomManualSelectorGenerator.AnimeEndEventType.FireNextStateEvent
               && kneel.m_transitions!.m_transitions is [{ } handOver] && handOver.m_toStateId == loop.m_stateId && strings.m_eventNames[handOver.m_eventId] == "Event90250_to_Event90270"
               && ((CustomManualSelectorGenerator)loop.m_generator!).m_animeEndEventType == CustomManualSelectorGenerator.AnimeEndEventType.None && loop.m_transitions is null,
            "the kneel hands over to the kneeling loop, which stays");
        Expect(machine.m_wildcardTransitions.m_transitions.Any(t => t.m_toStateId == kneel.m_stateId && strings.m_eventNames[t.m_eventId] == "W_Event90250")
               && machine.m_wildcardTransitions.m_transitions.Count == 7 && data.m_eventInfos.Count == strings.m_eventNames.Count, "each is reached by W_Event + its number");
        var stray = (CustomManualSelectorGenerator)machine.m_states.First(s => s!.m_generator is CustomManualSelectorGenerator { m_animId: 90280 })!.m_generator!;
        Expect(stray.m_animeEndEventType == CustomManualSelectorGenerator.AnimeEndEventType.FireIdleEvent && lines.Any(l => l.Contains("WARN 90280")),
            "a hand-over to a state that isn't there ends in idle instead of holding the last frame for good");
        var donor = machine.m_states[0]!;
        Expect(donor.m_transitions is null && ((CustomManualSelectorGenerator)donor.m_generator!) is { m_animId: 60010, m_generators.Count: 1 }, "the donor is untouched");
    }
}
