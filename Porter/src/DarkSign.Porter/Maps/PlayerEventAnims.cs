using DarkSign.Porter.Enemies;
using DarkSign.Porter.Havok;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's player event animations: the moves a DS3 map script plays on the player (the kneel before the Fire Keeper,
/// the walk up to her, the coiled sword thrust into Firelink's bonfire, standing up again).
/// <para>
/// The event port passes the animation numbers through, and Elden Ring's player plays a scripted animation N by
/// firing the behaviour event "W_Event" + N (c0000.hks, ExecMovableEventAnim and the engine's own request). Where
/// Elden Ring's graph has no state for N nothing plays at all: of the 15 animations Firelink's and the common scripts
/// play on the player, 60430, 69040, 69050, 69070, 90250, 90260, 90280 and 90291 have none (1.0.667: "the character
/// does not move in a way that places the sword... the sword just floating into position", and no kneel to level up).
/// </para>
/// <para>
/// For every such animation, and the states DS3 chains it into (the kneel 90250 ends by handing over to the kneeling
/// loop 90270, read from DS3's own graph with <see cref="Ds3BehaviorGraph"/>), the same way for all of them:
/// </para>
/// <list type="bullet">
/// <item>animation: DS3's clip, converted onto Elden Ring's skeleton (<see cref="AnimPort"/>), in a section of its
/// own in c0000_a9x (the binder the game takes new sections from, as the ported movesets do);</item>
/// <item>events: a TAE entry N in the player's a00.tae that plays that file, with DS3's events where both games read
/// them the same way (cancel and state flags, blends, weapon hiding, the Havok variables that put the weapons back in
/// the hands, and the SpEffects DS3's scripts check, moved to their ported rows). DS3's sounds and effects are left
/// out (their IDs aren't Elden Ring's);</item>
/// <item>behaviour: a state in Event_SM cloned from an Elden Ring event state whose script functions do what DS3's do
/// for this state (compared as compiled code), reached by the wildcard W_Event + N, with DS3's end-of-animation
/// behaviour (idle, nothing, or the next state by a transition of its own) and DS3's clip mode (single or looping).
/// The clone keeps the donor's state name: Elden Ring's script is found by state name (Event60750_onUpdate), and a
/// state under a new name would run no script, so the player could not be hit or moved out of it.</item>
/// </list>
/// An animation Elden Ring's graph has a state for keeps Elden Ring's own version (its doors, ladders, item pick-up
/// and fog gate walk carry Elden Ring's script logic). Community method (RULE 3): cloning a state, its selector and
/// its wildcard event is how new player animations are registered in Elden Ring (The12thAvenger's ERClipGeneratorTool;
/// Themyys, Introduction to Moveset Modding); the hk_2014 layouts were read off DS3's file.
/// </summary>
public static partial class PlayerEventAnims
{
    public const string Machine = "Event_SM";
    private const string MainBinder = "/chr/c0000.anibnd.dcx", BehaviorBinder = "/chr/c0000.behbnd.dcx", Script = "/action/script/c0000.hks";

    /// <summary>A DS3 animation to bring over: its DS3 state, the clip file it plays (an entry may borrow another's), its entry.</summary>
    public sealed record Item(int Id, Ds3BehaviorGraph.State State, TAE.Animation Entry, int SourceId);

    /// <summary>
    /// The DS3 animations to port: the scripted ones and every state DS3 chains them into, that DS3 has a state and a
    /// TAE entry for and Elden Ring's graph has no "W_Event" + N for. <paramref name="notes"/> gets a line for each
    /// scripted animation left alone.
    /// </summary>
    public static List<Item> Plan(IEnumerable<int> scripted, Ds3BehaviorGraph ds3Graph, TAE ds3Tae, Func<string, bool> erHasEvent, List<string> notes)
    {
        var plan = new List<Item>();
        var seen = new HashSet<int>();
        var queue = new Queue<(int Id, int From)>(scripted.Distinct().Order().Select(id => (id, 0)));
        var kept = new List<int>();
        while (queue.Count > 0)
        {
            var (id, from) = queue.Dequeue();
            if (!seen.Add(id))
                continue;
            string what = from == 0 ? $"{id}" : $"{id} (follows {from})";
            if (erHasEvent("W_Event" + id))
            {
                kept.Add(id);
                continue;
            }
            if (!ds3Graph.ByAnim.TryGetValue(id, out var state))
            {
                notes.Add($"  {what}: DS3's graph has no state for it; left alone");
                continue;
            }
            if (Source(ds3Tae, id) is not { } source)
            {
                notes.Add($"  {what}: DS3 has no TAE entry for it; left alone");
                continue;
            }
            plan.Add(new Item(id, state, source.Entry, source.FileId));
            foreach (var (_, to) in state.Next)
                queue.Enqueue((to, id));
        }
        if (kept.Count > 0)
            notes.Add($"  Elden Ring has its own state for {kept.Count}, kept: {string.Join(", ", kept.Order())}");
        return plan;
    }

    /// <summary>A DS3 entry and the animation whose file it plays, following "plays another entry's file" and whole imports.</summary>
    public static (TAE.Animation Entry, int FileId)? Source(TAE tae, int id)
    {
        var entry = tae.Animations.FirstOrDefault(a => a.ID == id);
        for (int hops = 0; hops < 4 && entry?.MiniHeader is TAE.Animation.AnimMiniHeader.ImportOtherAnim import; hops++)
        {
            id = import.ImportFromAnimID;
            entry = id is >= 0 and < 1_000_000 ? tae.Animations.FirstOrDefault(a => a.ID == id) : null;
        }
        if (entry is null)
            return null;
        int file = entry.MiniHeader is TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: true, ImportHKXSourceAnimID: >= 0 and < 1_000_000 } s ? s.ImportHKXSourceAnimID : (int)entry.ID;
        return (entry, file);
    }

    /// <summary>
    /// ChrActionFlag (TAE event 0) flags that mean the same in both games (DSAnimStudio's DS3 and ER templates) and
    /// that Elden Ring's own event animations use the same way (its a000_060060: 7, 8, 19, 27, 39, then the cancels
    /// 4, 11, 16, 22, 26, 29, 31, 32 and 87 near the end).
    /// </summary>
    public static readonly IReadOnlySet<int> SharedFlags = new HashSet<int> { 4, 7, 8, 11, 16, 19, 22, 26, 27, 28, 29, 31, 32, 39, 49, 50, 51, 54, 87, 104, 106, 110 };

    /// <summary>Event types copied as they are: 16 Blend, 224 SetTurnSpeed, 605 SetTimeActEditorHavokVariable, 710 HideEquippedWeapon.</summary>
    public static readonly IReadOnlySet<int> SharedTypes = new HashSet<int> { 16, 224, 605, 710 };

    /// <summary>
    /// A DS3 entry's events as Elden Ring reads them (see the class summary). <paramref name="dropped"/> counts the
    /// events left out, by type ("67 no row" for a SpEffect with no ported row).
    /// </summary>
    /// <param name="lengths">Elden Ring's parameter lengths per event type (from its own a00.tae).</param>
    /// <param name="groups">An Elden Ring event group per type, to clone.</param>
    public static List<TAE.Event> ConvertEvents(TAE.Animation ds3, IReadOnlyDictionary<int, HashSet<int>> lengths, IReadOnlyDictionary<int, TAE.EventGroup> groups,
        Func<long, long?>? spEffect, List<TAE.EventGroup> entryGroups, Dictionary<string, int> dropped)
    {
        var events = new List<TAE.Event>();
        var mine = new Dictionary<int, TAE.EventGroup>();
        void Drop(string why) => dropped[why] = dropped.GetValueOrDefault(why) + 1;
        foreach (var e in ds3.Events)
        {
            var bytes = (byte[])e.GetParameterBytes(false).Clone();
            if (e.Type == 0)
            {
                if (bytes.Length < 4 || !SharedFlags.Contains(BitConverter.ToInt32(bytes, 0)))
                {
                    Drop(bytes.Length < 4 ? "0" : $"flag {BitConverter.ToInt32(bytes, 0)}");
                    continue;
                }
            }
            else if (e.Type is 66 or 67)
            {
                if (bytes.Length < 4 || spEffect?.Invoke(BitConverter.ToInt32(bytes, 0)) is not { } row || row > int.MaxValue)
                {
                    Drop(bytes.Length < 4 ? $"{e.Type}" : $"effect {BitConverter.ToInt32(bytes, 0)} (no ported row)");
                    continue;
                }
                BitConverter.GetBytes((int)row).CopyTo(bytes, 0);
            }
            else if (!SharedTypes.Contains(e.Type))
            {
                Drop($"type {e.Type}");
                continue;
            }
            // Elden Ring's length for the type: the DS3 length when Elden Ring uses it too, else its shortest that
            // holds the fields (DS3's SpEffect events carry 8 more zero bytes).
            if (!lengths.TryGetValue(e.Type, out var known) || known.Count == 0)
            {
                Drop($"type {e.Type} (Elden Ring's player never uses it)");
                continue;
            }
            if (!known.Contains(bytes.Length))
                Array.Resize(ref bytes, known.Where(l => l <= bytes.Length).DefaultIfEmpty(known.Min()).Max());
            if (!mine.TryGetValue(e.Type, out var group))
            {
                group = mine[e.Type] = groups.TryGetValue(e.Type, out var proto) ? proto.GetClone() : new TAE.EventGroup(e.Type);
                entryGroups.Add(group);
            }
            events.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, bytes, false) { Group = group });
        }
        return events;
    }

    /// <summary>The SpEffects the entries' events cast (TAE 66/67), for the enemy port to give rows.</summary>
    public static SortedSet<int> CastEffects(IEnumerable<TAE.Animation> entries)
    {
        var ids = new SortedSet<int>();
        foreach (var e in entries.SelectMany(a => a.Events).Where(e => e.Type is 66 or 67))
            if (e.GetParameterBytes(false) is { Length: >= 4 } p && BitConverter.ToInt32(p, 0) > 0)
                ids.Add(BitConverter.ToInt32(p, 0));
        return ids;
    }

    /// <summary>What a state's script does: its update and deactivate functions' compiled code (null: no function).</summary>
    public readonly record struct Shape(string? Update, string? Deactivate);

    public static Shape ShapeOf(IReadOnlyDictionary<string, BehaviourScript.Function> functions, string state) =>
        new(Code(functions.GetValueOrDefault(state + "_onUpdate")), Code(functions.GetValueOrDefault(state + "_onDeactivate")));

    private static string? Code(BehaviourScript.Function? f) => f is null ? null
        : Convert.ToHexString(f.Code) + "|" + string.Join("|", f.Constants.Select(c => c.Type == 4 ? "s:" + c.Text : $"{c.Type}:{Convert.ToHexString(c.Raw)}"));

    /// <summary>An Elden Ring state new states can be cloned from: a plain one-clip state of <see cref="Machine"/> with its wildcard.</summary>
    public sealed record Donor(string Name, Shape Shape);

    /// <summary>
    /// The donor for a DS3 state: the first Elden Ring donor whose script functions are the same compiled code as
    /// DS3's for that state, else (with <paramref name="exact"/> false) the first that does what most DS3 event states
    /// do, calling EventCommonFunction and nothing else.
    /// </summary>
    public static Donor? DonorFor(Shape ds3, IReadOnlyList<Donor> donors, Shape common, out bool exact)
    {
        exact = true;
        if (donors.FirstOrDefault(d => d.Shape == ds3) is { } same)
            return same;
        exact = false;
        return donors.FirstOrDefault(d => d.Shape == common) ?? donors.FirstOrDefault();
    }

    /// <summary>The plain event states of Elden Ring's graph (see <see cref="Donor"/>), in state order.</summary>
    public static List<hkbStateMachine.StateInfo> DonorStates(hkbStateMachine machine, IReadOnlyList<string> eventNames)
    {
        var wild = (machine.m_wildcardTransitions?.m_transitions ?? []).Where(t => t.m_eventId >= 0 && t.m_eventId < eventNames.Count)
            .ToLookup(t => t.m_toStateId, t => eventNames[t.m_eventId]);
        return machine.m_states.OfType<hkbStateMachine.StateInfo>()
            .Where(s => s.m_name is not null && s.m_generator is CustomManualSelectorGenerator c
                        && c.m_generators.Count == 1 && c.m_generators[0] is hkbClipGenerator clip && clip.m_animationName == $"a000_{c.m_animId:D6}"
                        && (s.m_transitions?.m_transitions.Count ?? 0) == 0 && wild[s.m_stateId].Contains("W_Event" + c.m_animId))
            .OrderBy(s => s.m_stateId).ToList();
    }

    /// <summary>A state to add: the animation, DS3's state, and the Elden Ring state it is cloned from.</summary>
    public sealed record NewState(int Id, Ds3BehaviorGraph.State Ds3, string Donor);

    /// <summary>
    /// Adds the states to <see cref="Machine"/> (see the class summary). Returns how many were added; a line per state
    /// goes to <paramref name="lines"/>.
    /// </summary>
    public static int AddStates(IHavokObject root, IReadOnlyList<NewState> states, List<string> lines)
    {
        var all = HavokGraph.All(root);
        var machine = all.OfType<hkbStateMachine>().FirstOrDefault(m => m.m_name == Machine);
        var strings = all.OfType<hkbBehaviorGraphStringData>().FirstOrDefault();
        var data = all.OfType<hkbBehaviorGraphData>().FirstOrDefault();
        if (machine is null || strings is null || data is null)
        {
            lines.Add($"  FAIL the behaviour has no {Machine}");
            return 0;
        }
        machine.m_wildcardTransitions ??= new hkbStateMachine.TransitionInfoArray();
        var wildcards = machine.m_wildcardTransitions.m_transitions;
        int nextState = machine.m_states.Max(s => s!.m_stateId) + 1;
        int nextInternal = all.OfType<hkbClipGenerator>().Max(c => (int)c.m_animationInternalId) + 1;
        // A transition of a state's own, as Elden Ring writes them (Event60510_to_Event60511), to clone.
        var ownProto = machine.m_states.SelectMany(s => s!.m_transitions?.m_transitions ?? [])
            .FirstOrDefault(t => t.m_eventId >= 0 && t.m_eventId < strings.m_eventNames.Count && strings.m_eventNames[t.m_eventId].Contains("_to_", StringComparison.Ordinal));
        int Event(string name, int like)
        {
            int id = strings.m_eventNames.IndexOf(name);
            if (id >= 0)
                return id;
            strings.m_eventNames.Add(name);
            if (like >= 0 && like < data.m_eventInfos.Count)
                data.m_eventInfos.Add(HavokGraph.ShallowCopy(data.m_eventInfos[like]));
            return strings.m_eventNames.Count - 1;
        }

        var added = new Dictionary<int, hkbStateMachine.StateInfo>();
        foreach (var s in states)
        {
            var donor = machine.m_states.FirstOrDefault(x => x!.m_name == s.Donor && x.m_generator is CustomManualSelectorGenerator);
            var wildcard = donor is null ? null : wildcards.FirstOrDefault(t => t.m_toStateId == donor.m_stateId && t.m_eventId >= 0 && t.m_eventId < strings.m_eventNames.Count
                                                                                 && strings.m_eventNames[t.m_eventId].StartsWith("W_Event", StringComparison.Ordinal));
            if (donor?.m_generator is not CustomManualSelectorGenerator donorSelector || donorSelector.m_generators.OfType<hkbClipGenerator>().FirstOrDefault() is not { } donorClip || wildcard is null)
            {
                lines.Add($"  FAIL {s.Id}: Elden Ring's {Machine} has no state {s.Donor} to clone");
                continue;
            }
            if (nextInternal > ClipBudget.Limit)
            {
                lines.Add($"  FAIL {s.Id}: the behaviour's animation IDs are used up ({ClipBudget.Limit})");
                continue;
            }
            var name = $"a000_{s.Id:D6}";
            var clip = HavokGraph.ShallowCopy(donorClip);
            clip.m_name = donorClip.m_name is { } clipName && donorClip.m_animationName is { } oldName ? clipName.Replace(oldName, name, StringComparison.Ordinal) : name;
            clip.m_animationName = name;
            // A clip's internal ID is its own number, max + 1 (as the ported movesets number theirs); the animation
            // list is only extended while it is still as long as the IDs.
            if (strings.m_animationNames.Count == nextInternal)
                strings.m_animationNames.Add(strings.m_animationNames.FirstOrDefault(n => n is not null && n.EndsWith(donorClip.m_animationName + ".hkx", StringComparison.OrdinalIgnoreCase)) is { } path
                    ? path[..^(donorClip.m_animationName!.Length + 4)] + name + ".hkx" : name + ".hkx");
            clip.m_animationInternalId = checked((short)nextInternal++);
            clip.m_mode = s.Ds3.Looping ? hkbClipGenerator.PlaybackMode.MODE_LOOPING : hkbClipGenerator.PlaybackMode.MODE_SINGLE_PLAY;
            var selector = HavokGraph.ShallowCopy(donorSelector);
            selector.m_name = $"Event{s.Id}_CMSG";
            selector.m_animId = s.Id;
            selector.m_generators = [clip];
            selector.m_animeEndEventType = Enum.IsDefined(typeof(CustomManualSelectorGenerator.AnimeEndEventType), s.Ds3.EndType)
                ? (CustomManualSelectorGenerator.AnimeEndEventType)s.Ds3.EndType : donorSelector.m_animeEndEventType;
            var state = HavokGraph.ShallowCopy(donor);
            state.m_stateId = nextState++;
            // The donor's name stays: the script functions are found by it (see the class summary).
            state.m_generator = selector;
            state.m_listeners = [.. donor.m_listeners];
            state.m_transitions = null;
            machine.m_states.Add(state);
            var entry = HavokGraph.ShallowCopy(wildcard);
            entry.m_eventId = Event("W_Event" + s.Id, wildcard.m_eventId);
            entry.m_toStateId = state.m_stateId;
            wildcards.Add(entry);
            added[s.Id] = state;
        }

        // DS3's hand-overs, once every state exists.
        foreach (var s in states)
        {
            if (!added.TryGetValue(s.Id, out var state))
                continue;
            var selector = (CustomManualSelectorGenerator)state.m_generator!;
            var made = new List<string>();
            foreach (var (_, toAnim) in s.Ds3.Next)
            {
                var target = added.GetValueOrDefault(toAnim)
                             ?? machine.m_states.FirstOrDefault(x => x!.m_generator is CustomManualSelectorGenerator c && c.m_animId == toAnim);
                if (target is null || ownProto is null)
                    continue;
                var t = HavokGraph.ShallowCopy(ownProto);
                t.m_eventId = Event($"Event{s.Id}_to_Event{toAnim}", ownProto.m_eventId);
                t.m_toStateId = target.m_stateId;
                state.m_transitions ??= new hkbStateMachine.TransitionInfoArray();
                state.m_transitions.m_transitions.Add(t);
                made.Add($"{toAnim}");
            }
            // "Next state" with no state to go to would hold the last frame for good: end in idle instead.
            if (selector.m_animeEndEventType == CustomManualSelectorGenerator.AnimeEndEventType.FireNextStateEvent && made.Count == 0)
            {
                selector.m_animeEndEventType = CustomManualSelectorGenerator.AnimeEndEventType.FireIdleEvent;
                lines.Add($"  WARN {s.Id}: DS3 hands over to a state that isn't here; it ends in idle");
            }
            lines.Add($"  {s.Id}: state #{state.m_stateId} from Elden Ring's {s.Donor} (DS3 {s.Ds3.Name} in {s.Ds3.Machine}), "
                      + $"{(s.Ds3.Looping ? "looping" : "single play")}, at the end: {EndName(selector.m_animeEndEventType)}"
                      + (made.Count > 0 ? $" {string.Join(", ", made)}" : ""));
        }
        return added.Count;
    }

    private static string EndName(CustomManualSelectorGenerator.AnimeEndEventType end) => end switch
    {
        CustomManualSelectorGenerator.AnimeEndEventType.FireNextStateEvent => "on to",
        CustomManualSelectorGenerator.AnimeEndEventType.FireIdleEvent => "idle",
        CustomManualSelectorGenerator.AnimeEndEventType.FireStateEndEvent => "state end event",
        _ => "stays (until a script or the player moves on)",
    };

    /// <summary>DS3's player TAE (a00.tae) and behaviour graph, and the scripted animations with what DS3 chains them into.</summary>
    private static (TAE Tae, Ds3BehaviorGraph Graph, List<Use> Uses, int Unresolved) ReadDs3(PorterContext ctx, GameArchives ds3, IReadOnlyList<MapSlots.Slot> slots)
    {
        var (maps, common) = MapEvents.ReadDs3(ds3, slots);
        var scripts = maps.Values.Concat(common is null ? [] : [common]).ToList();
        var defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var uses = Scripted(scripts, defs, out int unresolved);
        TAE.ValidateEventBank = false;
        var main = BND4.Read(ds3.Read(MainBinder));
        var tae = TAE.Read((MovesetPort.BinderLeaf(main, "a00.tae") ?? throw new FileNotFoundException("DS3's c0000.anibnd has no a00.tae")).Bytes);
        var behbnd = BND4.Read(ds3.Read(BehaviorBinder));
        var graph = Ds3BehaviorGraph.Read(behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!.Bytes);
        return (tae, graph, uses, unresolved);
    }

    /// <summary>
    /// The DS3 SpEffects the scripted player animations (and the states DS3 chains them into) cast, whatever Elden
    /// Ring's graph holds: the enemy port gives them rows with the scripts' own (<see cref="MapEvents.ScriptSpEffects"/>),
    /// so the events can cast them and the scripts' checks see them (the kneel casts 155 once the player is down, and
    /// common event 20006005 waits for it).
    /// </summary>
    public static SortedSet<int> ScriptedEffects(PorterContext ctx, GameArchives ds3, IReadOnlyList<MapSlots.Slot> slots)
    {
        if (!ds3.Contains(MainBinder) || !ds3.Contains(BehaviorBinder))
            return [];
        var (tae, graph, uses, _) = ReadDs3(ctx, ds3, slots);
        return CastEffects(Plan(uses.Select(u => u.Anim), graph, tae, _ => false, []).Select(i => i.Entry));
    }

    /// <summary>The port-maps step, after port-weapons has written the player's files and before <see cref="PlayerSceneWeapons"/>.</summary>
    public static string Run(PorterContext ctx, GameArchives ds3, GameArchives er, IReadOnlyList<MapSlots.Slot> slots, Func<long, long?>? spEffect)
    {
        const string head = "player event animations";
        string mainPath = ctx.ModPath("chr", "c0000.anibnd.dcx"), animPath = ctx.ModPath("chr", "c0000_a9x.anibnd.dcx"), behPath = ctx.ModPath("chr", "c0000.behbnd.dcx");
        if (new[] { mainPath, animPath, behPath }.FirstOrDefault(p => !File.Exists(p)) is { } missing)
            return $"{head}: the mod has no {Path.GetFileName(missing)} (port-weapons not run); DS3's scripted player animations don't play";
        if (!ds3.Contains(MainBinder) || !ds3.Contains(BehaviorBinder))
            return $"{head}: DS3 has no player animation or behaviour binder; nothing to port";

        var (ds3Tae, ds3Graph, uses, unresolved) = ReadDs3(ctx, ds3, slots);
        var scripted = uses.Select(u => u.Anim).Distinct().Order().ToList();
        var lines = new List<string>();

        var behbnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(behPath), out var behCompression));
        var behaviorFile = behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!;
        hkRootLevelContainer root;
        lock (AnimPort.HavokLock)
            root = (hkRootLevelContainer)new HavokBinarySerializer().Read(new MemoryStream(behaviorFile.Bytes));
        var graph = HavokGraph.All(root);
        var strings = graph.OfType<hkbBehaviorGraphStringData>().First();
        var machine = graph.OfType<hkbStateMachine>().FirstOrDefault(m => m.m_name == Machine);
        if (machine is null)
            return $"{head}: FAIL the mod's player behaviour has no {Machine}";
        var eventNames = strings.m_eventNames.ToHashSet(StringComparer.Ordinal);
        var plan = Plan(scripted, ds3Graph, ds3Tae, eventNames.Contains, lines);
        string summary = $"{head}: the ported scripts play {scripted.Count} animation(s) on the player ({string.Join(", ", scripted)}), {unresolved} line(s) unread";
        if (plan.Count == 0)
            return summary + "; none to port" + Tail(lines);

        // The script functions: DS3's for each state, Elden Ring's for the states a new one can be cloned from.
        var donors = new List<Donor>();
        var common = default(Shape);
        var ds3Functions = new Dictionary<string, BehaviourScript.Function>();
        try
        {
            var erPath = ctx.ModPath("action", "script", "c0000.hks");
            var erFunctions = BehaviourScript.Read(File.Exists(erPath) ? File.ReadAllBytes(erPath) : er.Read(Script)).Named();
            ds3Functions = BehaviourScript.Read(ds3.Read(Script)).Named();
            donors = DonorStates(machine, strings.m_eventNames).Select(s => new Donor(s.m_name!, ShapeOf(erFunctions, s.m_name!))).ToList();
            common = donors.Where(d => d.Shape.Update is not null).GroupBy(d => d.Shape).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"  WARN the behaviour scripts were not read ({ex.Message}); every state is cloned from Elden Ring's first plain event state");
            donors = DonorStates(machine, strings.m_eventNames).Select(s => new Donor(s.m_name!, default)).ToList();
        }
        if (donors.Count == 0)
            return summary + $"; FAIL Elden Ring's {Machine} has no plain event state to clone" + Tail(lines);

        // Animations: DS3's clips into a section of their own.
        var main = BND4.Read(DCX.Decompress(File.ReadAllBytes(mainPath), out var mainCompression));
        var anims = BND4.Read(DCX.Decompress(File.ReadAllBytes(animPath), out var animCompression));
        var mainTaeFile = MovesetPort.BinderLeaf(main, "a00.tae") ?? throw new FileNotFoundException("the mod's c0000.anibnd has no a00.tae");
        TAE.ValidateEventBank = false;
        var erTae = TAE.Read(mainTaeFile.Bytes);
        var usedSections = graph.OfType<hkbClipGenerator>().Select(c => c.m_animationName).OfType<string>().Where(n => n.Length >= 4 && n[0] == 'a' && int.TryParse(n.AsSpan(1, 3), out _))
            .Select(n => int.Parse(n.AsSpan(1, 3))).ToHashSet();
        usedSections.UnionWith(main.Files.Select(f => ItemText.Leaf(f.Name)).Where(n => n.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            .Select(n => int.TryParse(n[1..^4], out var s) ? s : -1));
        usedSections.UnionWith(anims.Files.Select(f => ItemText.Leaf(f.Name)).Where(n => n.Length >= 4 && n[0] == 'a' && int.TryParse(n.AsSpan(1, 3), out _)).Select(n => int.Parse(n.AsSpan(1, 3))));
        // From the top of the moveset range down: the ported movesets take theirs from the bottom up.
        int section = Enumerable.Range(90, 310).Reverse().FirstOrDefault(s => !usedSections.Contains(s), -1);
        if (section < 0)
            return summary + "; FAIL no free animation section for DS3's clips" + Tail(lines);

        var ds3Main = BND4.Read(ds3.Read(MainBinder));
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(MovesetPort.BinderLeaf(ds3Main, "skeleton.hkx")!.Bytes);
        var erSkeleton = AnimPort.ReadErSkeleton(MovesetPort.BinderLeaf(main, "skeleton.hkx")!.Bytes, MovesetPort.Compendium(main));
        AnimPort.PlanRetarget(ds3Skeleton, erSkeleton);
        var ds3Files = new MovesetPort.Ds3AnimFiles(ds3);
        var template = MovesetPort.ErAnimFile(er, "a000_000000") ?? throw new FileNotFoundException("Elden Ring has no a000_000000 to lay the converted animations out like");

        var lengths = MovesetPort.EventLengths(erTae.Animations.SelectMany(a => a.Events));
        var groups = erTae.Animations.SelectMany(a => a.Events).Where(e => e.Group is not null).GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First().Group!);
        var sectionTae = TAE.Read(mainTaeFile.Bytes);
        sectionTae.ID = 2000 + section;
        sectionTae.Animations.Clear();
        var newStates = new List<NewState>();
        var files = new HashSet<int>();
        var dropped = new Dictionary<string, int>();
        int room = ClipBudget.AnimFileLimit - anims.Files.Count;
        foreach (var item in plan)
        {
            string fileName = $"a{section:D3}_{item.SourceId:D6}";
            if (!files.Contains(item.SourceId))
            {
                if (ds3Files.Read($"a000_{item.SourceId:D6}.hkx") is not { } ds3Clip)
                {
                    lines.Add($"  FAIL {item.Id}: DS3 has no animation file a000_{item.SourceId:D6}; not ported");
                    continue;
                }
                if (files.Count >= room)
                {
                    lines.Add($"  FAIL {item.Id}: the player's animation binder is full ({anims.Files.Count} files, the game takes {ClipBudget.AnimFileLimit}); not ported");
                    continue;
                }
                byte[] hkx;
                try { hkx = AnimPort.ConvertTimed(ds3Clip, ds3Skeleton, erSkeleton, template.Bytes, template.Compendium, true).Hkx; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lines.Add($"  FAIL {item.Id}: a000_{item.SourceId:D6} not converted ({ex.Message}); not ported");
                    continue;
                }
                MovesetPort.AddFile(anims, fileName + ".hkx", hkx);
                files.Add(item.SourceId);
                // The section's own entry for the file (the section's TAE is what makes the game take the section).
                sectionTae.Animations.Add(new TAE.Animation(item.SourceId, new TAE.Animation.AnimMiniHeader.Standard(), fileName + ".hkt"));
            }
            var entry = new TAE.Animation(item.Id, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = section * 1_000_000 + item.SourceId }, fileName + ".hkt");
            entry.Events = ConvertEvents(item.Entry, lengths, groups, spEffect, entry.EventGroups, dropped);
            erTae.Animations.RemoveAll(a => a.ID == item.Id);
            erTae.Animations.Add(entry);
            var shape = ShapeOf(ds3Functions, item.State.Name);
            var donor = DonorFor(shape, donors, common, out bool exact)!;
            if (!exact)
                lines.Add($"  WARN {item.Id}: no Elden Ring event state runs the same script as DS3's {item.State.Name}; cloned from {donor.Name}");
            newStates.Add(new NewState(item.Id, item.State, donor.Name));
        }
        if (newStates.Count == 0)
            return summary + "; FAIL none ported" + Tail(lines);
        // A state DS3 chains into only counts when it came along.
        int states;
        lock (AnimPort.HavokLock)
        {
            states = AddStates(root, newStates, lines);
            using var output = new MemoryStream();
            new HavokBinarySerializer().Write(root, output);
            behaviorFile.Bytes = output.ToArray();
        }
        erTae.Animations.Sort((x, y) => x.ID.CompareTo(y.ID));
        sectionTae.Animations.Sort((x, y) => x.ID.CompareTo(y.ID));
        mainTaeFile.Bytes = erTae.Write();
        MovesetPort.AddFile(main, $"a{section}.tae", sectionTae.Write(), mainTaeFile);

        File.WriteAllBytes(animPath, DCX.Compress(anims.Write(), animCompression));
        File.WriteAllBytes(mainPath, DCX.Compress(main.Write(), mainCompression));
        File.WriteAllBytes(behPath, DCX.Compress(behbnd.Write(), behCompression));
        return summary + $"; {states} DS3 animation(s) ported with a state each ({files.Count} clip file(s) in section a{section}, {anims.Files.Count} of {ClipBudget.AnimFileLimit} files in the binder)"
               + (dropped.Count > 0 ? $"; DS3 events left out: {string.Join(", ", dropped.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Value} {d.Key}"))}" : "")
               + Tail(lines);
    }

    private static string Tail(List<string> lines) => lines.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, lines) : "";
}
