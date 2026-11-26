using System.Text;

namespace DarkSign.Porter.Havok;

/// <summary>
/// The states of a DS3 behaviour graph (Behaviors\cXXXX.hkx in a DS3 behbnd), as far as the port needs them: which
/// animation each state plays, what it does when the animation ends, whether its clip loops, and the states it hands
/// over to by a transition of its own.
/// <para>
/// DS3's graphs are Havok 2014 packfiles, which HKLib (2018 tag files) doesn't read, so this reads the packfile's
/// tables directly: the data section's virtual fixups name each object's class, its local fixups are the pointers
/// inside an object (names, arrays) and its global fixups the pointers between objects. Field offsets are the 64-bit
/// hk_2014.1.0 layouts, read off DS3's own c0000.hkx on 2026-11-26 and checked there against known states:
/// </para>
/// <list type="bullet">
/// <item>every node: name pointer at 56;</item>
/// <item>CustomManualSelectorGenerator: offset type at 152, animation ID at 156, animation end event type at 160
/// (0 fire the next state's event, 1 fire the state end event, 2 fire idle, 3 nothing: the numbers Elden Ring's
/// m_animeEndEventType still uses);</item>
/// <item>hkbClipGenerator: playback mode at 190 (0 single play, 1 looping);</item>
/// <item>hkbStateMachineStateInfo: own transitions at 80, generator at 88, name at 96, state ID at 104;</item>
/// <item>hkbStateMachineTransitionInfoArray: array at 16 of 72-byte entries, event ID at 48, target state ID at 52;</item>
/// <item>hkbBehaviorGraphStringData: event names (array of string pointers) at 16.</item>
/// </list>
/// Checked: Event60750 (Gundyr's sword pull) ends in idle; Event90250 (the level-up kneel) ends with "next state"
/// and has the transition Event90250_to_Event90270 to the kneeling loop, whose clip loops and which ends in nothing.
/// </summary>
public sealed class Ds3BehaviorGraph
{
    /// <summary>Animation end event types (the same numbers in both games).</summary>
    public const int EndNextState = 0, EndStateEnd = 1, EndIdle = 2, EndNothing = 3;

    /// <param name="Next">The states its own transitions lead to: the event's name and the target's animation.</param>
    public sealed record State(string Name, string Machine, int AnimId, int EndType, bool Looping, IReadOnlyList<(string Event, int ToAnim)> Next);

    /// <summary>The first state playing each animation ID (through a CustomManualSelectorGenerator).</summary>
    public IReadOnlyDictionary<int, State> ByAnim { get; private init; } = new Dictionary<int, State>();
    public int States { get; private init; }

    private const int NodeName = 56, SelectorAnim = 156, SelectorEnd = 160, ClipMode = 190,
        StateTransitions = 80, StateGenerator = 88, StateName = 96, StateId = 104,
        TransitionArray = 16, TransitionSize = 72, TransitionEvent = 48, TransitionTo = 52, EventNames = 16;

    public static Ds3BehaviorGraph Read(byte[] d)
    {
        if (d.Length < 64 + 3 * 64 || BitConverter.ToUInt32(d, 0) != 0x57E0E057 || BitConverter.ToUInt32(d, 4) != 0x10C0C010)
            throw new InvalidDataException("not a Havok packfile");
        if (d[16] != 8 || d[17] != 1)
            throw new InvalidDataException($"packfile layout {d[16]}/{d[17]} is not 64-bit little-endian");
        var version = Ascii(d, 40);
        if (!version.StartsWith("hk_2014", StringComparison.Ordinal))
            throw new InvalidDataException($"packfile version {version} is not hk_2014");
        int sections = BitConverter.ToInt32(d, 20);
        (string Name, int Start, int Local, int Global, int Virtual, int Exports) Section(int i)
        {
            int o = 64 + i * 64;
            return (Ascii(d, o), BitConverter.ToInt32(d, o + 20), BitConverter.ToInt32(d, o + 24), BitConverter.ToInt32(d, o + 28),
                BitConverter.ToInt32(d, o + 32), BitConverter.ToInt32(d, o + 36));
        }
        var all = Enumerable.Range(0, sections).Select(Section).ToList();
        var names = all.FirstOrDefault(s => s.Name == "__classnames__");
        var data = all.FirstOrDefault(s => s.Name == "__data__");
        if (names.Name is null || data.Name is null)
            throw new InvalidDataException("packfile has no class name or data section");
        int at = data.Start;

        // Objects and their classes, in address order (an object runs to the next one, with the arrays it owns).
        var objects = new List<(int Offset, string Class)>();
        for (int p = at + data.Virtual; p + 12 <= at + data.Exports && p + 12 <= d.Length; p += 12)
        {
            uint offset = BitConverter.ToUInt32(d, p);
            if (offset == uint.MaxValue)
                break;
            objects.Add(((int)offset, Ascii(d, names.Start + BitConverter.ToInt32(d, p + 8))));
        }
        objects.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var starts = objects.Select(o => o.Offset).ToArray();
        var local = new Dictionary<int, int>();
        for (int p = at + data.Local; p + 8 <= at + data.Global; p += 8)
            if (BitConverter.ToUInt32(d, p) != uint.MaxValue)
                local[BitConverter.ToInt32(d, p)] = BitConverter.ToInt32(d, p + 4);
        var global = new Dictionary<int, int>();
        for (int p = at + data.Global; p + 12 <= at + data.Virtual; p += 12)
            if (BitConverter.ToUInt32(d, p) != uint.MaxValue)
                global[BitConverter.ToInt32(d, p)] = BitConverter.ToInt32(d, p + 8);

        int IndexOf(int offset)
        {
            int i = Array.BinarySearch(starts, offset);
            return i >= 0 ? i : ~i - 1;
        }
        string ClassAt(int offset) => IndexOf(offset) is >= 0 and var i && starts[i] == offset ? objects[i].Class : "";
        int End(int index) => index + 1 < starts.Length ? starts[index + 1] : data.Local;
        int Int(int offset) => at + offset + 4 <= d.Length ? BitConverter.ToInt32(d, at + offset) : 0;
        string? Text(int pointerAt) => local.TryGetValue(pointerAt, out var to) && at + to < d.Length ? Ascii(d, at + to) : null;
        IEnumerable<int> Pointers(int index)
        {
            for (int k = starts[index]; k < End(index); k += 8)
                if (global.TryGetValue(k, out var to))
                    yield return to;
        }

        var events = new List<string?>();
        int strings = objects.FindIndex(o => o.Class == "hkbBehaviorGraphStringData");
        if (strings >= 0 && local.TryGetValue(starts[strings] + EventNames, out var eventArray))
            for (int i = 0, n = Int(starts[strings] + EventNames + 8); i < n; i++)
                events.Add(Text(eventArray + 8 * i));

        // Each state's machine: a machine points at its states.
        var machineOf = new Dictionary<int, string>();
        for (int i = 0; i < objects.Count; i++)
            if (objects[i].Class == "hkbStateMachine")
                foreach (var to in Pointers(i).Where(to => ClassAt(to) == "hkbStateMachineStateInfo"))
                    machineOf.TryAdd(to, Text(starts[i] + NodeName) ?? "");

        // (machine, state ID) -> the animation its selector plays.
        var animOf = new Dictionary<(string, int), int>();
        var found = new List<(int Offset, string Machine, int Anim)>();
        foreach (var (offset, cls) in objects)
        {
            if (cls != "hkbStateMachineStateInfo" || !global.TryGetValue(offset + StateGenerator, out var generator) || ClassAt(generator) != "CustomManualSelectorGenerator")
                continue;
            var machine = machineOf.GetValueOrDefault(offset, "");
            int anim = Int(generator + SelectorAnim);
            animOf.TryAdd((machine, Int(offset + StateId)), anim);
            found.Add((offset, machine, anim));
        }
        var byAnim = new Dictionary<int, State>();
        foreach (var (offset, machine, anim) in found)
        {
            int generator = global[offset + StateGenerator];
            bool looping = Pointers(IndexOf(generator)).Where(to => ClassAt(to) == "hkbClipGenerator").Any(clip => at + clip + ClipMode < d.Length && d[at + clip + ClipMode] == 1);
            var next = new List<(string, int)>();
            if (global.TryGetValue(offset + StateTransitions, out var transitions) && ClassAt(transitions) == "hkbStateMachineTransitionInfoArray"
                && local.TryGetValue(transitions + TransitionArray, out var entries))
                for (int i = 0, n = Int(transitions + TransitionArray + 8); i < n; i++)
                {
                    int entry = entries + TransitionSize * i;
                    int eventId = Int(entry + TransitionEvent);
                    if (eventId >= 0 && eventId < events.Count && events[eventId] is { } eventName && animOf.TryGetValue((machine, Int(entry + TransitionTo)), out var toAnim))
                        next.Add((eventName, toAnim));
                }
            byAnim.TryAdd(anim, new State(Text(offset + StateName) ?? "", machine, anim, Int(generator + SelectorEnd), looping, next));
        }
        return new Ds3BehaviorGraph { ByAnim = byAnim, States = found.Count };
    }

    private static string Ascii(byte[] d, int offset)
    {
        if (offset < 0 || offset >= d.Length)
            return "";
        int end = Array.IndexOf(d, (byte)0, offset);
        return Encoding.ASCII.GetString(d, offset, (end < 0 ? d.Length : end) - offset);
    }
}
