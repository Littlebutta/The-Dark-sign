using System.Text.RegularExpressions;
using DarkSign.Porter.Havok;
using HKLib.hk2018;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// Which behaviour state a scripted move hands over to when it ends, as DS3 has it.
/// <para>
/// A ported character runs its template's behaviour script (<see cref="ChrPort"/> clones action\script\cXXXX.hks with
/// the template's files). Each scripted state there has an EventNNNNN_onUpdate function that fires the state the move
/// hands over to: mostly W_Idle, but the template's own story decides the rest (Elden Ring's c3060/c3061, the template
/// of Iudex Gundyr, sends 20002 on to its own pose 30006). DS3's scripts chain their moves too, and DS3's map scripts
/// count on it: Gundyr's first fight (m40 14005811) forces only 20003, the sword pull; his stand-up 20002 follows it
/// in DS3's behaviour, and 20002's TAE sends event message 40, which 14005810 waits for before it turns his AI on and
/// shows the boss bar. With the template's script 20003 ended in idle: he stood up without attacking and the fight
/// never began (1.0.614).
/// </para>
/// <para>
/// So every state function the two scripts share by name takes DS3's hand-over: where each fires exactly one W_ state
/// and they differ, the template's is replaced with DS3's, when the character's behaviour graph has that event (else
/// the template's stays, and the report names it). The function's condition (when to hand over) stays the template's;
/// only the destination changes. Functions with several hand-overs are compared as lists and changed only when both
/// fire the same number of states, in order.
/// </para>
/// </summary>
public static partial class EventChains
{
    [GeneratedRegex(@"^Event\d+_onUpdate$")]
    private static partial Regex EventFunction();

    public sealed record Change(string Function, string From, string To);

    public sealed class Result
    {
        public List<Change> Changed { get; } = [];
        /// <summary>DS3 hand-overs to an event the character's graph doesn't have.</summary>
        public List<Change> NoState { get; } = [];
        /// <summary>Functions both scripts have whose hand-overs can't be paired (different counts).</summary>
        public List<string> Unpaired { get; } = [];
        public int Shared { get; set; }

        public string Summary() =>
            $"{Changed.Count} of {Shared} scripted states hand over as DS3's do"
            + (Changed.Count > 0 ? $" ({string.Join(", ", Changed.Take(8).Select(c => $"{Short(c.Function)} {c.From}->{c.To}"))}{(Changed.Count > 8 ? ", ..." : "")})" : "")
            + (NoState.Count > 0 ? $"; FAIL {NoState.Count} DS3 hand-overs to states the graph lacks, template's kept ({string.Join(", ", NoState.Take(6).Select(c => $"{Short(c.Function)}->{c.To}"))})" : "")
            + (Unpaired.Count > 0 ? $"; {Unpaired.Count} with hand-overs that don't pair ({string.Join(", ", Unpaired.Take(6).Select(Short))})" : "");

        private static string Short(string function) => function.Replace("_onUpdate", "", StringComparison.Ordinal);
    }

    /// <summary>The W_ states a function fires, in constant order (each once).</summary>
    public static List<string> HandOvers(BehaviourScript.Function f) =>
        f.Constants.Where(c => c.Type == 4 && c.Text is { } t && t.StartsWith("W_", StringComparison.Ordinal))
            .Select(c => c.Text!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Gives <paramref name="clone"/>'s state functions DS3's hand-overs (in place).</summary>
    /// <param name="hasEvent">Whether the character's behaviour graph has an event of that name.</param>
    public static Result Port(BehaviourScript clone, BehaviourScript ds3, Func<string, bool> hasEvent)
    {
        var result = new Result();
        var ds3Named = ds3.Named();
        foreach (var (name, f) in clone.Named().Where(p => EventFunction().IsMatch(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!ds3Named.TryGetValue(name, out var d))
                continue;
            result.Shared++;
            var ours = HandOvers(f);
            var theirs = HandOvers(d);
            if (ours.SequenceEqual(theirs, StringComparer.Ordinal))
                continue;
            if (ours.Count != theirs.Count)
            {
                result.Unpaired.Add(name);
                continue;
            }
            // Constants by their text as read, so a swap (A->B, B->A) doesn't change a constant twice.
            var byText = f.Constants.Where(c => c.Type == 4).GroupBy(c => c.Text!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            for (int i = 0; i < ours.Count; i++)
            {
                if (ours[i] == theirs[i])
                    continue;
                if (!hasEvent(theirs[i]))
                {
                    result.NoState.Add(new Change(name, ours[i], theirs[i]));
                    continue;
                }
                foreach (var c in byText[ours[i]])
                    c.Text = theirs[i];
                result.Changed.Add(new Change(name, ours[i], theirs[i]));
            }
        }
        return result;
    }

    /// <summary>The event names of the behaviour graphs in a character's behbnd (W_Idle, W_Event20002...).</summary>
    public static HashSet<string> GraphEvents(BND4 behbnd)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var compendium = behbnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
        foreach (var file in behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
            lock (AnimPort.HavokLock)
            {
                IHavokObject root;
                try { root = AnimPort.ReadEr(file.Bytes, compendium); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
                foreach (var strings in HavokGraph.All(root).OfType<hkbBehaviorGraphStringData>())
                    names.UnionWith(strings.m_eventNames.OfType<string>());
            }
        return names;
    }

    /// <summary>
    /// The clone's behaviour script with DS3's hand-overs (<see cref="Port"/>), or null when nothing changes (no
    /// script, no DS3 script, or the same hand-overs).
    /// </summary>
    public static (byte[]? Bytes, string Note) Apply(byte[] clone, byte[]? ds3, Func<HashSet<string>> graphEvents)
    {
        if (ds3 is null)
            return (null, "DS3 has no behaviour script for it: the template's hand-overs kept");
        BehaviourScript ours, theirs;
        try
        {
            ours = BehaviourScript.Read(clone);
            theirs = BehaviourScript.Read(ds3);
        }
        catch (InvalidDataException ex)
        {
            return (null, $"FAIL behaviour script hand-overs not ported ({ex.Message})");
        }
        HashSet<string>? events = null;
        var result = Port(ours, theirs, name => (events ??= graphEvents()) is null || events.Contains(name));
        return (result.Changed.Count > 0 ? ours.Write() : null, result.Summary());
    }

    // ---- Self-test ----

    /// <summary>A small script in the file layout: main + one function per (name, constants), nested in main.</summary>
    public static byte[] Synthetic(params (string Name, string[] Strings)[] functions)
    {
        static byte[] BeString(string? s)
        {
            if (s is null)
                return new byte[8];
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            var o = new byte[8 + b.Length + 1];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(o, (ulong)b.Length + 1);
            b.CopyTo(o, 8);
            return o;
        }
        BehaviourScript.Function Make(string? name, string[] strings, uint instructions) => new()
        {
            Name = name,
            Head = new byte[17],
            Instructions = instructions,
            Code = new byte[instructions * 4],
            Constants = strings.Select(t => new BehaviourScript.Constant(4, [], t)).Append(new BehaviourScript.Constant(3, [0x3F, 0x80, 0, 0], null)).ToList(),
            // two u32, no locals, no upvalues, two line numbers (u32 each), path, name, a line number per instruction
            Tail = [.. new byte[24], .. BeString("@c9999.hks"), .. BeString(name), .. new byte[instructions * 4]],
        };
        var main = Make(null, ["EventCommonFunction"], 3);
        main.Functions.AddRange(functions.Select((f, i) => Make(f.Name, f.Strings, (uint)(i % 3 + 1))));
        var header = new byte[18];
        new byte[] { 0x1B, (byte)'L', (byte)'u', (byte)'a', 0x51, 0x0E, 0, 4, 8, 4, 4, 0, 3, 0 }.CopyTo(header, 0);
        return new BehaviourScript { Header = header, Main = main, Footer = [0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0] }.Write();
    }

    public static void SelfTest(Action<string, bool> check)
    {
        // The template (Elden Ring c3060's script): 20003 ends in idle, 20002 goes on to the template's own pose 30006.
        var template = Synthetic(("Event20002_onUpdate", ["env", "TRUE", "Fire", "W_Event30006"]),
            ("Event20003_onUpdate", ["EventCommonFunction", "TRUE", "env", "Fire", "W_Idle"]),
            ("Event20004_onUpdate", ["EventCommonFunction", "Fire", "W_Idle"]),
            ("Event20005_onUpdate", ["Fire", "W_Idle", "W_Event30001"]),
            ("Event30002_onUpdate", ["EventCommonFunction", "TRUE"]));
        // DS3 c5110: the sword pull 20003 hands over to the stand-up 20002, 20002 to idle; 20004 to a state the graph lacks.
        var ds3 = Synthetic(("Event20002_onUpdate", ["EventCommonFunction", "Fire", "W_Idle"]),
            ("Event20003_onUpdate", ["EventCommonFunction", "Fire", "W_Event20002"]),
            ("Event20004_onUpdate", ["Fire", "W_Event20099"]),
            ("Event20005_onUpdate", ["Fire", "W_Idle"]),
            ("Event30002_onUpdate", ["EventCommonFunction"]));
        var read = BehaviourScript.Read(template);
        check("behaviour script: read and written back byte for byte (with the '_' padding before the instructions)", read.Write().AsSpan().SequenceEqual(template));
        var graph = new HashSet<string> { "W_Idle", "W_Event20002", "W_Event30006" };
        var (bytes, note) = Apply(template, ds3, () => graph);
        var named = bytes is null ? new Dictionary<string, BehaviourScript.Function>() : BehaviourScript.Read(bytes).Named();
        string To(string f) => named.TryGetValue(f, out var fn) ? string.Join(" ", HandOvers(fn)) : "?";
        check($"behaviour script: the sword pull hands over to the stand-up and the stand-up to idle, as in DS3 ({note})",
            To("Event20003_onUpdate") == "W_Event20002" && To("Event20002_onUpdate") == "W_Idle");
        check("behaviour script: a DS3 hand-over to a state the graph lacks keeps the template's and is reported",
            To("Event20004_onUpdate") == "W_Idle" && note.Contains("FAIL 1 DS3 hand-overs", StringComparison.Ordinal) && note.Contains("Event20004->W_Event20099", StringComparison.Ordinal));
        check("behaviour script: hand-overs that don't pair are left and named",
            To("Event20005_onUpdate") == "W_Idle W_Event30001" && note.Contains("1 with hand-overs that don't pair (Event20005)", StringComparison.Ordinal));
        check("behaviour script: the other constants stay (condition kept, only the destination changes)",
            named.TryGetValue("Event20003_onUpdate", out var f3) && f3.Constants.Select(c => c.Text ?? "").SequenceEqual(["EventCommonFunction", "TRUE", "env", "Fire", "W_Event20002", ""]));
        check("behaviour script: swapped hand-overs change each constant once",
            Port(BehaviourScript.Read(Synthetic(("Event1_onUpdate", ["W_A", "W_B"]))), BehaviourScript.Read(Synthetic(("Event1_onUpdate", ["W_B", "W_A"]))), _ => true) is var swap
            && swap.Changed.Count == 2);
        check("behaviour script: no DS3 script keeps the template's", Apply(template, null, () => graph).Bytes is null);
        check("behaviour script: the same hand-overs change nothing", Apply(ds3, ds3, () => graph).Bytes is null);
    }
}
