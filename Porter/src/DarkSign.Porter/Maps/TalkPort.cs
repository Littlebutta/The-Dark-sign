using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's people talk. Up to 1.0.47x every ported person stood silent (TalkID 0): DS3's talk scripts were never
/// brought over. A talk script (script/talk/mAA_BB_CC_DD.talkesdbnd, one tNNNNNN.esd per person) is a state machine
/// of numbered commands and functions, and Elden Ring's engine kept DS3's numbering: of the 105 commands and 62
/// functions DS3's scripts for the ported maps use, all but a few exist in Elden Ring's scripts under the same
/// number with the same argument count (measured on both games' files, 2026-11-19). So each DS3 script is converted
/// rather than replaced:
/// <list type="bullet">
/// <item>Every command and function is checked against the ones Elden Ring's own talk scripts use
/// (<see cref="Vocabulary"/>, read from the game's files at port time, not a hand-written list). A command Elden
/// Ring doesn't have is left out; one with another argument count gets Elden Ring's (missing arguments take the
/// values Elden Ring's scripts pass most often); a function Elden Ring doesn't have reads as 0.</item>
/// <item>Event flags move exactly as the converted event scripts moved them (<see cref="EventPort.Move"/>), so a
/// talk script and the map's events still read each other's flags.</item>
/// <item>Dialogue lines: DS3's TalkParam rows and their text are added to Elden Ring's TalkParam and TalkMsg text
/// under <see cref="TextBase"/> + the DS3 number; menu and dialog text (DS3's event text) to Elden Ring's
/// EventTextForTalk the same way. There is no voice yet: the lines are subtitles only.</item>
/// <item>Shops: every lineup range a script opens is added to ShopLineupParam at <see cref="ShopBase"/> + the DS3
/// number, selling the ported weapon or the Elden Ring item of the same name (the drops' rule); items with
/// neither are left off the shelf.</item>
/// <item>Gifts (DS3 item lots) and effects cast on the player (DS3 SpEffects) are left out: the same numbers are
/// other things in Elden Ring.</item>
/// </list>
/// What a value means (flag, line, text, shop) is known where it is used, and followed back through the arguments
/// the script's state groups pass each other (<see cref="Kinds"/>). The converted script is t(<see cref="TalkIdBase"/>
/// + DS3's number) in the ported map's own talk binder, and the placed person names it.
/// DARKSIGN_PEOPLE_TALK=0 (fallback map-people G) leaves everyone silent, as before. Unproven in game (2026-11-19):
/// that Elden Ring shows a line with no voice for a readable time.
/// </summary>
public sealed class TalkPort
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_PEOPLE_TALK") != "0";

    /// <summary>DARKSIGN_TALK_ENGINE_FLAGS=0 (fallback map-people K) keeps waiting on the flags only DS3's characters set.</summary>
    public static bool EngineFlagsOn => Environment.GetEnvironmentVariable("DARKSIGN_TALK_ENGINE_FLAGS") != "0";

    /// <summary>Off (fallback map-people J, DARKSIGN_PEOPLE_VOICES=0): no voice banks; lines are subtitles only, as
    /// up to 1.0.501, where Elden Ring moved on from each line at once.</summary>
    public static bool Voices => Environment.GetEnvironmentVariable("DARKSIGN_PEOPLE_VOICES") != "0";

    /// <summary>
    /// Elden Ring finds an NPC's spoken lines in the voice bank named after the first three digits of its talk script
    /// (t204101000 speaks from vc204: every voiceId of Elden Ring's TalkParam starts with its bank's number, measured
    /// on soundbanksinfo, 2026-11-19), the line being Play_v&lt;voiceId, nine digits&gt;. So each converted script gets
    /// a prefix of its own from 900 up (t9PP + DS3's six-digit number), and its lines a voice bank vc9PP. Elden Ring
    /// has a bank vc999, so 999 is never used; past 99 scripts the prefixes are shared (their banks hold both).
    /// </summary>
    public const int FirstPrefix = 900, PrefixCount = 99;

    /// <summary>Elden Ring's talk scripts are 9 digits from 1xxxxxxxx to 8xxxxxxxx; DS3's are 6 digits.</summary>
    public const int TalkIdBase = 900_000_000;
    /// <summary>TalkParam rows and talk text: Elden Ring's stay below a thousand million, DS3's below a hundred million.</summary>
    public const int TextBase = 1_000_000_000;
    /// <summary>ShopLineupParam rows: Elden Ring's end at 9,999,002.</summary>
    public const int ShopBase = 20_000_000;
    public const string ReportFile = "people-talk.tsv";

    public static string TalkPath(MapId id) => $"/script/talk/{id}.talkesdbnd.dcx";
    public static bool IsOurs(int talkId) => talkId >= TalkIdBase && talkId < TextBase;

    // ---- Expressions ----

    /// <summary>One node of a script expression (the bytecode is a stack machine; a node is what one stack slot holds).</summary>
    public abstract class Node;
    /// <summary>A whole number.</summary>
    public sealed class Lit(long value) : Node { public long Value = value; }
    /// <summary>Anything else that pushes one value and is kept byte for byte (a float, a string, a register read...).</summary>
    public sealed class Raw(byte[] bytes) : Node { public byte[] Bytes = bytes; }
    /// <summary>A function call: the function number, then its arguments.</summary>
    public sealed class Call(Node func, List<Node> args) : Node { public Node Func = func; public List<Node> Args = args; }
    /// <summary>An operator over the nodes before it, or a marker that follows a node and leaves the stack as it is.</summary>
    public sealed class Op(byte code, List<Node> kids) : Node { public byte Code = code; public List<Node> Kids = kids; }

    private const byte StateArg = 0xB8;

    /// <summary>Reads an expression into its nodes (bottom of the stack first). Throws on a byte it doesn't know.</summary>
    public static List<Node> Parse(byte[] b)
    {
        var stack = new List<Node>();
        Node Pop()
        {
            if (stack.Count == 0)
                throw new InvalidDataException("expression underflow");
            var n = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return n;
        }
        int i = 0;
        while (i < b.Length)
        {
            byte o = b[i];
            if (o <= 0x7F)
            {
                stack.Add(new Lit(o - 64));
                i++;
            }
            else if (o == 0x82)
            {
                stack.Add(new Lit(BitConverter.ToInt32(b, i + 1)));
                i += 5;
            }
            else if (o is 0x80 or 0x81)
            {
                int len = o == 0x80 ? 5 : 9;
                stack.Add(new Raw(b[i..(i + len)]));
                i += len;
            }
            else if (o == 0xA5)
            {
                int j = i + 1;
                while (b[j] != 0 || b[j + 1] != 0)
                    j += 2;
                stack.Add(new Raw(b[i..(j + 2)]));
                i = j + 2;
            }
            else if (o is >= 0x84 and <= 0x8A)
            {
                int n = o - 0x84;
                var args = new Node[n];
                for (int k = n - 1; k >= 0; k--)
                    args[k] = Pop();
                stack.Add(new Call(Pop(), args.ToList()));
                i++;
            }
            else if (o is 0x8D or 0x9A or StateArg)
            {
                stack.Add(new Op(o, [Pop()])); // negate, not, state group argument
                i++;
            }
            else if (o is >= 0x8C and <= 0x99)
            {
                var r = Pop();
                var l = Pop();
                stack.Add(new Op(o, [l, r]));
                i++;
            }
            else if (o is 0xA1 or 0xA6 or 0xB7 or (>= 0xA7 and <= 0xAE))
            {
                // end, continuation, abort-if-false, store in a register: the value before them stays where it is
                stack.Add(stack.Count > 0 ? new Op(o, [Pop()]) : new Op(o, []));
                i++;
            }
            else if (o is (>= 0xAF and <= 0xB6) or 0xB9 or 0xBA or 0xBB)
            {
                stack.Add(new Raw([o])); // register read, call result, call ongoing
                i++;
            }
            else
                throw new InvalidDataException($"unknown expression byte 0x{o:X2}");
        }
        return stack;
    }

    public static byte[] Emit(IEnumerable<Node> nodes)
    {
        var o = new List<byte>();
        foreach (var n in nodes)
            Emit(n, o);
        return o.ToArray();
    }

    private static void Emit(Node n, List<byte> o)
    {
        switch (n)
        {
            case Lit l when l.Value is >= -64 and <= 63:
                o.Add((byte)(l.Value + 64));
                break;
            case Lit l:
                o.Add(0x82);
                o.AddRange(BitConverter.GetBytes((int)l.Value));
                break;
            case Raw r:
                o.AddRange(r.Bytes);
                break;
            case Call c:
                Emit(c.Func, o);
                foreach (var a in c.Args)
                    Emit(a, o);
                o.Add((byte)(0x84 + c.Args.Count));
                break;
            case Op p:
                foreach (var k in p.Kids)
                    Emit(k, o);
                o.Add(p.Code);
                break;
        }
    }

    /// <summary>The value node of an argument or condition, under its trailing markers (end, continuation...).</summary>
    private static Node Core(Node n)
    {
        while (n is Op { Kids.Count: 1 } p && p.Code is 0xA1 or 0xA6 or 0xB7 or (>= 0xA7 and <= 0xAE))
            n = ((Op)n).Kids[0];
        return n;
    }

    /// <summary>The state group argument number a node reads ("arg[2]"), or -1.</summary>
    private static int ArgIndex(Node n) => Core(n) is Op { Code: StateArg } p && p.Kids[0] is Lit l ? (int)l.Value : -1;

    private static IEnumerable<Node> Walk(Node n)
    {
        yield return n;
        var kids = n switch { Call c => c.Args.Prepend(c.Func), Op p => p.Kids, _ => Enumerable.Empty<Node>() };
        foreach (var k in kids)
            foreach (var d in Walk(k))
                yield return d;
    }

    // ---- What Elden Ring's scripts use ----

    /// <summary>The commands and functions Elden Ring's own talk scripts call, with the argument counts they use.</summary>
    public sealed class Vocabulary
    {
        /// <summary>(bank, command) -> argument count -> the constant argument lists seen, by how often.</summary>
        private readonly Dictionary<(int, int), Dictionary<int, Dictionary<string, int>>> _commands = [];
        private readonly HashSet<long> _functions = [];
        public int Scripts { get; private set; }
        public int CommandCount => _commands.Count;
        public int FunctionCount => _functions.Count;

        public void Add(ESD esd)
        {
            Scripts++;
            foreach (var state in esd.StateGroups.Values.SelectMany(g => g.Values))
            {
                foreach (var c in Commands(state))
                {
                    if (!_commands.TryGetValue((c.CommandBank, c.CommandID), out var byCount))
                        _commands[(c.CommandBank, c.CommandID)] = byCount = [];
                    if (!byCount.TryGetValue(c.Arguments.Count, out var seen))
                        byCount[c.Arguments.Count] = seen = [];
                    var key = string.Join(' ', c.Arguments.Select(System.Convert.ToHexString));
                    seen[key] = seen.GetValueOrDefault(key) + 1;
                    foreach (var a in c.Arguments)
                        AddFunctions(a);
                }
                foreach (var cond in state.Conditions.SelectMany(Flatten))
                    AddFunctions(cond.Evaluator);
            }
        }

        private void AddFunctions(byte[] expression)
        {
            try
            {
                foreach (var call in Parse(expression).SelectMany(Walk).OfType<Call>())
                    if (call.Func is Lit f)
                        _functions.Add(f.Value);
            }
            catch (InvalidDataException)
            {
                // an Elden Ring expression this reader doesn't know adds nothing
            }
        }

        public bool HasFunction(long id) => _functions.Contains(id);
        public bool HasCommand(int bank, int id) => _commands.ContainsKey((bank, id));

        /// <summary>
        /// The argument count Elden Ring wants for a command given DS3's count: the same if Elden Ring uses it, else
        /// the nearest larger one, else the largest; with the constant arguments Elden Ring passes most often at that count.
        /// </summary>
        public (int Count, List<byte[]>? Usual) Shape(int bank, int id, int ds3Count)
        {
            var byCount = _commands[(bank, id)];
            if (byCount.ContainsKey(ds3Count))
                return (ds3Count, null);
            var larger = byCount.Keys.Where(k => k > ds3Count).ToList();
            int count = larger.Count > 0 ? larger.Min() : byCount.Keys.Max();
            var usual = byCount[count].Where(kv => kv.Key.Length > 0 || count == 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(System.Convert.FromHexString).ToList())
                .FirstOrDefault(l => l.Count == count && l.All(IsConstant));
            return (count, usual);
        }

        private static bool IsConstant(byte[] expression)
        {
            try
            {
                var nodes = Parse(expression);
                return nodes.Count == 1 && Core(nodes[0]) is Lit or Raw { Bytes.Length: > 1 };
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        public static Vocabulary Scan(GameArchives er)
        {
            var v = new Vocabulary();
            foreach (var path in er.Entries.Select(e => e.Path).Where(p => p?.StartsWith("/script/talk/") == true && p.EndsWith(".talkesdbnd.dcx")).OrderBy(p => p, StringComparer.Ordinal))
                foreach (var file in BND4.Read(er.Read(path!)).Files)
                    v.Add(ESD.Read(file.Bytes));
            return v;
        }
    }

    public static IEnumerable<ESD.CommandCall> Commands(ESD.State state) =>
        state.EntryCommands.Concat(state.ExitCommands).Concat(state.WhileCommands).Concat(state.Conditions.SelectMany(Flatten).SelectMany(c => c.PassCommands));

    private static IEnumerable<List<ESD.CommandCall>> CommandLists(ESD.State state) =>
        new[] { state.EntryCommands, state.ExitCommands, state.WhileCommands }.Concat(state.Conditions.SelectMany(Flatten).Select(c => c.PassCommands));

    public static IEnumerable<ESD.Condition> Flatten(ESD.Condition c) => c.Subconditions.SelectMany(Flatten).Prepend(c);

    // ---- What a value means ----

    public enum Kind { None, Flag, Line, Text, ShopStart, ShopEnd, Button }

    private const int GroupBank = 6;

    /// <summary>Where DS3's talk commands take a flag, a dialogue line (TalkParam), menu text or a shop range.</summary>
    private static readonly Dictionary<(int Bank, int Id), Kind[]> CommandKinds = new()
    {
        [(1, 1)] = [Kind.Line],                           // say a line
        [(1, 11)] = [Kind.Flag],                          // set a flag
        [(1, 17)] = [Kind.None, Kind.Text],               // open a dialog
        [(1, 19)] = [Kind.None, Kind.Text],               // add a menu entry
        [(5, 19)] = [Kind.None, Kind.None, Kind.Text],    // add a menu entry if
        [(5, 11)] = [Kind.None, Kind.Flag],               // set a flag if
        [(1, 22)] = [Kind.ShopStart, Kind.ShopEnd],       // open a shop
        [(1, 111)] = [Kind.ShopStart, Kind.ShopEnd],      // open a shop (second kind)
    };

    private static readonly Dictionary<long, Kind[]> FunctionKinds = new()
    {
        [15] = [Kind.Flag],   // flag state
        [101] = [Kind.Flag],  // flag value over several bits
        [56] = [Kind.Button], // inside an action button's area
    };

    /// <summary>
    /// DS3 commands left out although Elden Ring has the number: 1:104 gives an item lot and 1:62 casts a SpEffect on
    /// the player, and DS3's lot and effect numbers are other lots and effects in Elden Ring.
    /// </summary>
    private static readonly HashSet<(int, int)> LeftOut = [(1, 104), (1, 62)];

    /// <summary>What each state group's arguments mean, followed back from where they end up being used.</summary>
    public sealed class Kinds
    {
        private readonly Dictionary<(long Group, int Arg), Kind> _kinds = [];
        public Kind Of(long group, int arg) => _kinds.GetValueOrDefault((group, arg));
        public int Count => _kinds.Count;

        public static Kinds Infer(Dictionary<long, List<(ESD.CommandCall? Command, List<Node> Nodes)>> expressions)
        {
            var kinds = new Kinds();
            bool changed = true;
            for (int pass = 0; changed && pass < 32; pass++)
            {
                changed = false;
                foreach (var (group, list) in expressions)
                    foreach (var (command, nodes) in list)
                    {
                        void Mark(Node n, Kind kind)
                        {
                            int arg = ArgIndex(n);
                            if (kind != Kind.None && arg >= 0 && kinds._kinds.TryAdd((group, arg), kind))
                                changed = true;
                        }
                        foreach (var call in nodes.SelectMany(Walk).OfType<Call>())
                            if (call.Func is Lit f && FunctionKinds.TryGetValue(f.Value, out var fk))
                                for (int i = 0; i < Math.Min(fk.Length, call.Args.Count); i++)
                                    Mark(call.Args[i], fk[i]);
                    }
                foreach (var (group, list) in expressions)
                {
                    // a command's arguments are its expressions in order
                    foreach (var byCommand in list.Where(e => e.Command is not null).GroupBy(e => e.Command!))
                    {
                        var c = byCommand.Key;
                        var args = byCommand.Select(e => e.Nodes).ToList();
                        for (int i = 0; i < args.Count; i++)
                        {
                            var kind = c.CommandBank == GroupBank ? kinds.Of(c.CommandID, i)
                                : CommandKinds.TryGetValue((c.CommandBank, c.CommandID), out var ck) && i < ck.Length ? ck[i] : Kind.None;
                            int arg = args[i].Count == 1 ? ArgIndex(args[i][0]) : -1;
                            if (kind != Kind.None && arg >= 0 && kinds._kinds.TryAdd((group, arg), kind))
                                changed = true;
                        }
                    }
                }
            }
            return kinds;
        }
    }

    // ---- One script ----

    public sealed class Stats
    {
        public int Flags, Lines, Texts, Shops, Buttons, CommandsLeftOut, CommandsReshaped, FunctionsZeroed, EngineFlags;
        public SortedDictionary<string, int> LeftOutCommands = new(StringComparer.Ordinal), ZeroedFunctions = new(StringComparer.Ordinal);
    }

    /// <summary>What a converted script needs written beside it.</summary>
    public sealed class Needs
    {
        public SortedSet<int> Lines = [], Texts = [];
        public SortedSet<(int Start, int End)> Shops = [];
    }

    /// <summary>
    /// Converts one DS3 talk script in place. <paramref name="moveFlag"/> moves a DS3 flag; <paramref name="hasButton"/>
    /// says whether Elden Ring has an action button row. Throws when an expression can't be read (the script is then
    /// left out and its person stays silent).
    /// </summary>
    /// <param name="engineDone">DS3 flags that only DS3's engine turns on (<see cref="EngineFlags"/>): read as on.</param>
    public static Stats Convert(ESD esd, Vocabulary er, Func<long, long> moveFlag, Func<int, bool> hasButton, Needs needs,
        Func<long, bool>? engineDone = null)
    {
        var stats = new Stats();
        // Every expression of every group, parsed once: (the command it is an argument of, its nodes).
        var parsed = new Dictionary<long, List<(ESD.CommandCall? Command, List<Node> Nodes)>>();
        var conditions = new List<(ESD.Condition Condition, List<Node> Nodes)>();
        foreach (var (group, states) in esd.StateGroups)
        {
            var list = parsed[group] = [];
            foreach (var state in states.Values)
            {
                foreach (var c in Commands(state))
                    foreach (var a in c.Arguments)
                        list.Add((c, Parse(a)));
                foreach (var cond in state.Conditions.SelectMany(Flatten))
                {
                    var nodes = Parse(cond.Evaluator);
                    list.Add((null, nodes));
                    conditions.Add((cond, nodes));
                }
            }
        }
        var kinds = Kinds.Infer(parsed);

        void Rewrite(Node n, Kind kind)
        {
            if (Core(n) is not Lit lit || lit.Value <= 0)
                return;
            int v = (int)Math.Min(lit.Value, int.MaxValue);
            switch (kind)
            {
                case Kind.Flag:
                    lit.Value = moveFlag(lit.Value);
                    stats.Flags++;
                    break;
                case Kind.Line when v < TextBase:
                    needs.Lines.Add(v);
                    lit.Value = TextBase + v;
                    stats.Lines++;
                    break;
                case Kind.Text when v < TextBase:
                    needs.Texts.Add(v);
                    lit.Value = TextBase + v;
                    stats.Texts++;
                    break;
                case Kind.ShopStart or Kind.ShopEnd when v < ShopBase:
                    lit.Value = ShopBase + v;
                    break;
                case Kind.Button when !hasButton(v):
                    lit.Value = 6000; // "Talk" in both games
                    stats.Buttons++;
                    break;
            }
        }

        Node Engine(Node n)
        {
            switch (n)
            {
                case Call { Func: Lit { Value: 15 }, Args: [var a] } when engineDone is not null && Core(a) is Lit l && engineDone(l.Value):
                    stats.EngineFlags++;
                    return new Lit(1);
                case Call c:
                    for (int i = 0; i < c.Args.Count; i++)
                        c.Args[i] = Engine(c.Args[i]);
                    return c;
                case Op p:
                    for (int i = 0; i < p.Kids.Count; i++)
                        p.Kids[i] = Engine(p.Kids[i]);
                    return p;
                default:
                    return n;
            }
        }

        void Functions(List<Node> nodes)
        {
            for (int i = 0; i < nodes.Count; i++)
                nodes[i] = Engine(nodes[i]);
            foreach (var call in nodes.SelectMany(Walk).OfType<Call>())
                if (call.Func is Lit f && FunctionKinds.TryGetValue(f.Value, out var fk))
                    for (int i = 0; i < Math.Min(fk.Length, call.Args.Count); i++)
                        Rewrite(call.Args[i], fk[i]);
            // A function Elden Ring doesn't have reads as 0 (DS3's "is something in the way of talking" checks).
            for (int i = 0; i < nodes.Count; i++)
                nodes[i] = Zero(nodes[i]);
        }

        Node Zero(Node n)
        {
            switch (n)
            {
                case Call { Func: Lit f } when !er.HasFunction(f.Value):
                    stats.FunctionsZeroed++;
                    var name = $"f{f.Value}";
                    stats.ZeroedFunctions[name] = stats.ZeroedFunctions.GetValueOrDefault(name) + 1;
                    return new Lit(0);
                case Call c:
                    for (int i = 0; i < c.Args.Count; i++)
                        c.Args[i] = Zero(c.Args[i]);
                    return c;
                case Op p:
                    for (int i = 0; i < p.Kids.Count; i++)
                        p.Kids[i] = Zero(p.Kids[i]);
                    return p;
                default:
                    return n;
            }
        }

        foreach (var (cond, nodes) in conditions)
        {
            Functions(nodes);
            cond.Evaluator = Emit(nodes);
        }
        foreach (var (group, states) in esd.StateGroups)
            foreach (var state in states.Values)
                foreach (var list in CommandLists(state))
                    for (int n = list.Count - 1; n >= 0; n--)
                    {
                        var c = list[n];
                        bool engine = c.CommandBank is not (GroupBank or 7);
                        if (engine && (LeftOut.Contains((c.CommandBank, c.CommandID)) || !er.HasCommand(c.CommandBank, c.CommandID)))
                        {
                            list.RemoveAt(n);
                            stats.CommandsLeftOut++;
                            var name = $"c{c.CommandBank}_{c.CommandID}";
                            stats.LeftOutCommands[name] = stats.LeftOutCommands.GetValueOrDefault(name) + 1;
                            continue;
                        }
                        var args = c.Arguments.Select(Parse).ToList();
                        Kind KindOf(int i) => c.CommandBank == GroupBank ? kinds.Of(c.CommandID, i)
                            : CommandKinds.TryGetValue((c.CommandBank, c.CommandID), out var ck) && i < ck.Length ? ck[i] : Kind.None;
                        // A shop range, given here or handed on to the state group that opens it.
                        for (int i = 0; i + 1 < args.Count; i++)
                            if (KindOf(i) == Kind.ShopStart && KindOf(i + 1) == Kind.ShopEnd && args[i].Count == 1 && args[i + 1].Count == 1
                                && Core(args[i][0]) is Lit { Value: > 0 and < ShopBase } s && Core(args[i + 1][0]) is Lit { Value: > 0 and < ShopBase } e)
                            {
                                needs.Shops.Add(((int)s.Value, (int)e.Value));
                                stats.Shops++;
                            }
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (args[i].Count == 1)
                                Rewrite(args[i][0], KindOf(i));
                            Functions(args[i]);
                        }
                        c.Arguments = args.Select(a => Emit(a)).ToList();
                        if (engine)
                        {
                            var (count, usual) = er.Shape(c.CommandBank, c.CommandID, c.Arguments.Count);
                            if (count != c.Arguments.Count)
                            {
                                stats.CommandsReshaped++;
                                while (c.Arguments.Count > count)
                                    c.Arguments.RemoveAt(c.Arguments.Count - 1);
                                while (c.Arguments.Count < count)
                                    c.Arguments.Add(usual is not null ? usual[c.Arguments.Count] : Emit([new Op(0xA1, [new Lit(0)])]));
                            }
                        }
                    }
        return stats;
    }

    /// <summary>
    /// The flags DS3's engine sets for a map's talk scripts, which nothing ported sets: a talk script waits for them,
    /// no talk script of the map turns them on or off, the map's events never turn them on but do reset them, and the
    /// state group that waits for one turns on a flag within 3 of it (the request it answers). DS3's Fire Keeper is the
    /// case found (1.0.479, "can't level up at the firekeeper"): her level-up entry sets 74000135/136 and waits for
    /// 74000137 and 74000138, which her character's own behaviour sets when her bow is done (no script does; m40's
    /// event 14005600 only resets 135-138), so the menu waited forever on a body whose behaviour is Elden Ring's.
    /// Read as on, the menu opens once she is asked. Firelink has two more of the same handshake (74000391/392 and
    /// 74000432/433, read off m40's talk binder 2026-11-20).
    /// </summary>
    public static HashSet<long> EngineFlags(IEnumerable<ESD> scripts, IEnumerable<EMEVD> events)
    {
        var read = new HashSet<long>();
        var on = new HashSet<long>();
        var off = new HashSet<long>();
        static long? Lit1(byte[] e)
        {
            try { return Core(Parse(e).LastOrDefault() ?? new Lit(0)) is Lit l ? l.Value : null; }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
        }
        // Candidates per state group: flags it waits for next to a flag it turns on (the request they answer).
        var waited = new HashSet<long>();
        var talkOff = new HashSet<long>();
        foreach (var esd in scripts)
            foreach (var group in esd.StateGroups.Values)
            {
                var groupRead = new HashSet<long>();
                var groupOn = new HashSet<long>();
                foreach (var state in group.Values)
                {
                    foreach (var cond in state.Conditions.SelectMany(Flatten))
                        try
                        {
                            foreach (var call in Parse(cond.Evaluator).SelectMany(Walk).OfType<Call>())
                                if (call.Func is Lit { Value: 15 } && call.Args.Count == 1 && Core(call.Args[0]) is Lit f)
                                    groupRead.Add(f.Value);
                        }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { _ = ex; }
                    foreach (var c in Commands(state))
                    {
                        int flagAt = (c.CommandBank, c.CommandID) switch { (1, 11) => 0, (5, 11) => 1, _ => -1 };
                        if (flagAt < 0 || c.Arguments.Count < flagAt + 2 || Lit1(c.Arguments[flagAt]) is not long flag)
                            continue;
                        // A value that isn't a plain number (an argument, an expression) may be on.
                        bool isOff = Lit1(c.Arguments[flagAt + 1]) == 0;
                        (isOff ? talkOff : on).Add(flag);
                        if (!isOff)
                            groupOn.Add(flag);
                    }
                }
                waited.UnionWith(groupRead.Where(f => groupOn.Any(r => r != f && Math.Abs(r - f) <= 3)));
                read.UnionWith(groupRead);
            }
        foreach (var e in events.SelectMany(s => s.Events).SelectMany(e => e.Instructions))
        {
            var a = e.ArgData;
            if (e.Bank == 2003 && e.ID == 66 && a.Length >= 9)         // Set Event Flag (type, flag, state)
                (a[8] == 0 ? off : on).Add(BitConverter.ToUInt32(a, 4));
            else if (e.Bank == 2003 && e.ID == 22 && a.Length >= 9)    // Batch Set Event Flags (start, end, state)
            {
                uint from = BitConverter.ToUInt32(a, 0), to = BitConverter.ToUInt32(a, 4);
                if (to >= from && to - from < 1000)
                    for (uint f = from; f <= to; f++)
                        (a[8] == 0 ? off : on).Add(f);
            }
            else if (e.Bank == 2003 && e.ID is 69 or 41 && a.Length >= 8)   // flag changes by other means: may be on
                on.Add(BitConverter.ToUInt32(a, a.Length >= 12 ? 4 : 0));
        }
        read.IntersectWith(off);
        read.ExceptWith(on);
        read.IntersectWith(waited);
        // A flag a talk script turns off itself is one it takes as a one-time signal: left as it is.
        read.ExceptWith(talkOff);
        read.RemoveWhere(f => f <= 0);
        return read;
    }

    // ---- The plan and the files ----

    /// <summary>DS3 map -> the talk scripts its binder holds that can be read.</summary>
    private readonly Dictionary<MapId, HashSet<int>> _readable = [];
    /// <summary>DS3 map -> the talk scripts placed people name.</summary>
    private readonly Dictionary<MapId, SortedSet<int>> _used = [];
    /// <summary>DS3 talk script -> its prefix (<see cref="FirstPrefix"/>), in the order people are placed.</summary>
    private readonly Dictionary<int, int> _prefix = [];

    /// <summary>The Elden Ring script number for a DS3 script: t + prefix (900-998) + DS3's six digits.</summary>
    public int IdFor(int ds3Talk)
    {
        if (!_prefix.TryGetValue(ds3Talk, out var prefix))
            _prefix[ds3Talk] = prefix = FirstPrefix + _prefix.Count % PrefixCount;
        return prefix * 1_000_000 + ds3Talk % 1_000_000;
    }
    public List<string> Notes { get; } = [];
    public int UsedCount => _used.Values.Sum(u => u.Count);

    /// <summary>Reads which DS3 talk scripts of the given maps can be converted (before the people are placed).</summary>
    public static TalkPort Plan(GameArchives ds3, IEnumerable<MapId> ds3Maps)
    {
        var plan = new TalkPort();
        foreach (var map in ds3Maps.Distinct())
        {
            if (!ds3.Contains(TalkPath(map)))
            {
                plan.Notes.Add($"{map}: DS3 has no talk binder");
                continue;
            }
            var ok = plan._readable[map] = [];
            int unreadable = 0;
            foreach (var file in BND4.Read(ds3.Read(TalkPath(map))).Files)
            {
                if (ScriptId(file.Name) is not int id)
                    continue;
                try
                {
                    var esd = ESD.Read(file.Bytes);
                    foreach (var state in esd.StateGroups.Values.SelectMany(g => g.Values))
                    {
                        foreach (var a in Commands(state).SelectMany(c => c.Arguments))
                            Parse(a);
                        foreach (var cond in state.Conditions.SelectMany(Flatten))
                            Parse(cond.Evaluator);
                    }
                    ok.Add(id);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    unreadable++;
                    plan.Notes.Add($"{map} t{id}: not readable ({ex.Message}); its person stays silent");
                }
            }
            plan.Notes.Add($"{map}: {ok.Count} DS3 talk scripts readable{(unreadable > 0 ? $", {unreadable} not" : "")}");
        }
        return plan;
    }

    /// <summary>"...\t400170.esd" -> 400170.</summary>
    public static int? ScriptId(string name)
    {
        var leaf = Path.GetFileNameWithoutExtension(ItemText.Leaf(name));
        return leaf.Length > 1 && leaf[0] is 't' or 'T' && int.TryParse(leaf.AsSpan(1), out var id) ? id : null;
    }

    /// <summary>The TalkID a placed person gets for DS3's, or 0 when DS3's script can't be converted.</summary>
    public int For(MapId ds3Map, int ds3Talk)
    {
        if (ds3Talk <= 0 || !_readable.TryGetValue(ds3Map, out var ok) || !ok.Contains(ds3Talk))
            return 0;
        (_used.TryGetValue(ds3Map, out var used) ? used : _used[ds3Map] = []).Add(ds3Talk);
        return IdFor(ds3Talk);
    }

    /// <summary>
    /// Converts and writes every used script, its lines, text and shops; returns the report lines.
    /// <paramref name="items"/> maps a DS3 shop row's (equipType, item) to the Elden Ring item, or null.
    /// </summary>
    public string Write(PorterContext ctx, GameArchives ds3, GameArchives er, IReadOnlyList<MapSlots.Slot> slots, ParamStore regulation,
        ParamStore? ds3Params, Func<long, long> moveFlag, Func<int, int, int?> items)
    {
        var lines = new List<string>();
        var vocabulary = Vocabulary.Scan(er);
        lines.Add($"people talk: Elden Ring's {vocabulary.Scripts} talk scripts use {vocabulary.CommandCount} commands and {vocabulary.FunctionCount} functions");
        var buttons = regulation.Get("ActionButtonParam")?.Rows.Select(r => r.ID).ToHashSet() ?? [];
        var needs = new Needs();
        // Which voice bank (talk script prefix) speaks each line: the first script that says it.
        var lineBank = new Dictionary<int, int>();
        var report = new StringBuilder("DS3 map\tDS3 talk script\tElden Ring map\tElden Ring talk script\tstate groups\tflags moved\tlines\tmenu texts\tshops\tcommands left out\tcommands reshaped\tfunctions read as 0\n");
        int written = 0, failed = 0;
        var leftOut = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var zeroed = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var templatePath = er.Entries.Select(e => e.Path).Where(p => p?.StartsWith("/script/talk/") == true && p.EndsWith(".talkesdbnd.dcx"))
            .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault() ?? throw new InvalidDataException("Elden Ring has no talk binders");
        foreach (var slot in slots)
        {
            if (!_used.TryGetValue(slot.Ds3, out var used) || used.Count == 0)
                continue;
            var allScripts = BND4.Read(ds3.Read(TalkPath(slot.Ds3))).Files;
            var source = allScripts.Where(f => ScriptId(f.Name) is int id && used.Contains(id)).ToList();
            HashSet<long> engine = [];
            if (EngineFlagsOn)
                try
                {
                    var events = new[] { MapEvents.ScriptPath(slot.Ds3), "/event/common.emevd.dcx" }.Where(ds3.Contains).Select(p => EMEVD.Read(ds3.Read(p)));
                    engine = EngineFlags(allScripts.Select(f => { try { return ESD.Read(f.Bytes); } catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return null; } })
                        .OfType<ESD>(), events);
                    if (engine.Count > 0)
                        lines.Add($"  people talk: {slot.Ds3}: {engine.Count} flags only DS3's characters set, read as on ({string.Join(" ", engine.Order().Take(12))})");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lines.Add($"  people talk: {slot.Ds3}: engine flags not worked out ({ex.Message}); menus that wait on a character may hang");
                }
            var modPath = ctx.ModPath(MapPorter.ModParts(TalkPath(slot.Er)));
            var (vanilla, compression) = er.ReadWithCompression(er.Contains(TalkPath(slot.Er)) ? TalkPath(slot.Er) : templatePath!);
            var bnd = BND4.Read(File.Exists(modPath) ? DCX.Decompress(File.ReadAllBytes(modPath)) : vanilla);
            var flags = bnd.Files.FirstOrDefault()?.Flags ?? Binder.FileFlags.Flag1;
            // Another map's binder is only the container: its own scripts don't belong to this map.
            if (!File.Exists(modPath) && !er.Contains(TalkPath(slot.Er)))
                bnd.Files.Clear();
            bnd.Files.RemoveAll(f => ScriptId(f.Name) is int id && IsOurs(id));
            foreach (var file in source.OrderBy(f => ScriptId(f.Name)))
            {
                int ds3Id = ScriptId(file.Name)!.Value, id = IdFor(ds3Id);
                try
                {
                    var esd = ESD.Read(file.Bytes);
                    var before = needs.Lines.ToHashSet();
                    var stats = Convert(esd, vocabulary, moveFlag, buttons.Contains, needs, engine.Contains);
                    foreach (var line in needs.Lines.Where(l => !before.Contains(l)))
                        lineBank.TryAdd(line, id / 1_000_000);
                    esd.Name = $"t{id}";
                    bnd.Files.Add(new BinderFile(flags, 0, $@"N:\GR\data\INTERROOT_win64\script\talk\{slot.Er}\t{id}.esd", esd.Write()));
                    written++;
                    foreach (var (k, v) in stats.LeftOutCommands)
                        leftOut[k] = leftOut.GetValueOrDefault(k) + v;
                    foreach (var (k, v) in stats.ZeroedFunctions)
                        zeroed[k] = zeroed.GetValueOrDefault(k) + v;
                    report.AppendLine(string.Join('\t', slot.Ds3, $"t{ds3Id}", slot.Er, $"t{id}", esd.StateGroups.Count, stats.Flags, stats.Lines, stats.Texts,
                        stats.Shops, stats.CommandsLeftOut, stats.CommandsReshaped, stats.FunctionsZeroed));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed++;
                    lines.Add($"  people talk: {slot.Ds3} t{ds3Id}: not converted ({ex.GetType().Name}: {ex.Message}); its person has nothing to say");
                }
            }
            bnd.Files = bnd.Files.OrderBy(f => ScriptId(f.Name) ?? int.MaxValue).ToList();
            for (int i = 0; i < bnd.Files.Count; i++)
                bnd.Files[i].ID = i;
            Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
            File.WriteAllBytes(modPath, DCX.Compress(bnd.Write(), compression));
        }
        lines.Insert(1, $"people talk: {written} DS3 talk scripts converted for {_used.Count(u => u.Value.Count > 0)} maps{(failed > 0 ? $", {failed} failed" : "")}"
                        + (leftOut.Count > 0 ? $"; commands left out: {string.Join(", ", leftOut.Select(kv => $"{kv.Key} x{kv.Value}"))}" : "")
                        + (zeroed.Count > 0 ? $"; functions Elden Ring lacks read as 0: {string.Join(", ", zeroed.Select(kv => $"{kv.Key} x{kv.Value}"))}" : ""));
        lines.AddRange(WriteLines(ctx, ds3, er, regulation, ds3Params, needs, lineBank));
        lines.Add(WriteShops(regulation, ds3Params, needs, items));
        regulation.Save(ctx.ModPath("regulation.bin"));
        Directory.CreateDirectory(ctx.WorkPath("reports"));
        File.WriteAllText(ctx.WorkPath("reports", ReportFile), report.ToString());
        return string.Join(Environment.NewLine, lines);
    }

    // ---- Lines and text ----

    private const string Ds3Menu = "/msg/engus/menu_dlc2.msgbnd.dcx", Ds3MenuBase = "/msg/engus/menu.msgbnd.dcx";

    /// <summary>DS3's text by number: the files whose name has one of <paramref name="first"/> win, then any other file.</summary>
    public static Dictionary<int, string> LoadDs3Text(GameArchives ds3, params string[] first)
    {
        var all = new Dictionary<int, string>();
        foreach (var pass in new[] { true, false })
            foreach (var path in new[] { Ds3Menu, Ds3MenuBase }.Where(ds3.Contains))
                foreach (var f in BND4.Read(ds3.Read(path)).Files.Where(f => f.Name.EndsWith(".fmg", StringComparison.OrdinalIgnoreCase)
                                                                             && first.Any(n => ItemText.Leaf(f.Name).Contains(n, StringComparison.OrdinalIgnoreCase)) == pass))
                    foreach (var e in FMG.Read(f.Bytes).Entries)
                        if (!string.IsNullOrWhiteSpace(e.Text))
                            all.TryAdd(e.ID, e.Text);
        return all;
    }

    private static IEnumerable<string> WriteLines(PorterContext ctx, GameArchives ds3, GameArchives er, ParamStore regulation, ParamStore? ds3Params, Needs needs,
        IReadOnlyDictionary<int, int> lineBank)
    {
        var voiceLog = new List<string>();
        var modMenuPath = ctx.ModPath(MapPorter.ModParts(ActionButtons.ModMenu));
        var (vanillaMenu, compression) = er.ReadWithCompression(ActionButtons.ModMenu);
        var menu = BND4.Read(File.Exists(modMenuPath) ? DCX.Decompress(File.ReadAllBytes(modMenuPath)) : vanillaMenu);
        BinderFile Fmg(string stem) =>
            menu.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{ItemText.Leaf(ActionButtons.ModMenu)} has no {stem} file");
        var talkFile = Fmg("TalkMsg");
        var eventFile = Fmg("EventTextForTalk");
        var talkMsg = FMG.Read(talkFile.Bytes);
        var eventText = FMG.Read(eventFile.Bytes);
        talkMsg.Entries.RemoveAll(e => e.ID >= TextBase);
        eventText.Entries.RemoveAll(e => e.ID >= TextBase);

        // DS3 names its text files in Japanese: 会話 = conversation, イベントテキスト = event text.
        var ds3Talk = LoadDs3Text(ds3, "会話", "Conversation", "TalkMsg");
        var ds3Event = LoadDs3Text(ds3, "イベントテキスト", "EventText");
        int texts = 0, noText = 0;
        foreach (var id in needs.Texts)
            if (ds3Event.TryGetValue(id, out var words))
            {
                eventText.Entries.Add(new FMG.Entry(TextBase + id, words));
                texts++;
            }
            else
                noText++;

        var talkParam = regulation.Get("TalkParam") ?? throw new InvalidDataException("Elden Ring has no TalkParam");
        var ds3TalkParam = ds3Params?.Get("TalkParam");
        talkParam.Rows.RemoveAll(r => r.ID >= TextBase);
        // The row to copy: one of Elden Ring's with a line and, if there is one, no voice.
        var template = talkParam.Rows.Where(r => PeopleArmour.Cell(r, "msgId") > 0).OrderBy(r => PeopleArmour.Cell(r, "voiceId") > 0 ? 1 : 0).ThenBy(r => r.ID).FirstOrDefault()
                       ?? throw new InvalidDataException("Elden Ring's TalkParam has no row with a line");
        int rows = 0, noRow = 0, noWords = 0;
        // Each bank's lines: (row, voiceId, DS3 line number, reading time).
        var banks = new SortedDictionary<int, List<(PARAM.Row Row, int Voice, int Ds3Line, float Seconds)>>();
        foreach (var id in needs.Lines)
        {
            // DS3's row names the text; without DS3's params the line's number is the text's (as DS3's rows have it).
            int message = id;
            if (ds3TalkParam is not null)
            {
                if (ds3TalkParam[id] is not { } source)
                {
                    noRow++;
                    continue;
                }
                message = PeopleArmour.Cell(source, "msgId");
                if (message <= 0)
                    message = PeopleArmour.Cell(source, "PcGenderFemale1");
            }
            if (message <= 0 || !ds3Talk.TryGetValue(message, out var words))
            {
                noWords++;
                continue;
            }
            var row = new PARAM.Row(TextBase + id, "", talkParam.AppliedParamdef);
            for (int i = 0; i < row.Cells.Count; i++)
                row.Cells[i].Value = template.Cells[i].Value is byte[] b ? b.Clone() : template.Cells[i].Value;
            PeopleArmour.SetCell(row, "msgId", TextBase + id);
            PeopleArmour.SetCell(row, "msgId_female", TextBase + id);
            PeopleArmour.SetCell(row, "voiceId", -1);
            PeopleArmour.SetCell(row, "voiceId_female", -1);
            // Elden Ring's one voiceless row is a disabled one; ours are live, and stay up long enough to read.
            PeopleArmour.SetCell(row, "disableParam_NT", 0);
            PeopleArmour.SetCell(row, "isForceDisp", 1);
            if (row.Cells.FirstOrDefault(c => c.Def.InternalName == "timeout") is { } timeout)
                timeout.Value = ReadingTime(words);
            talkParam.Rows.Add(row);
            talkMsg.Entries.Add(new FMG.Entry(TextBase + id, words));
            rows++;
            if (Voices && lineBank.TryGetValue(id, out var prefix))
            {
                var list = banks.TryGetValue(prefix, out var l) ? l : banks[prefix] = [];
                list.Add((row, prefix * 1_000_000 + list.Count + 1, id, ReadingTime(words)));
            }
        }
        voiceLog.AddRange(WriteVoices(ctx, ds3, er, banks));
        talkParam.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        talkFile.Bytes = talkMsg.Write();
        eventFile.Bytes = eventText.Write();
        Directory.CreateDirectory(Path.GetDirectoryName(modMenuPath)!);
        File.WriteAllBytes(modMenuPath, DCX.Compress(menu.Write(), compression));
        yield return $"people talk: {rows} dialogue lines (TalkParam {TextBase}+){(noRow > 0 ? $", {noRow} without a DS3 row" : "")}"
               + $"{(noWords > 0 ? $", {noWords} without DS3 text" : "")}{(ds3TalkParam is null ? " (DS3's TalkParam unreadable: line numbers used as text numbers)" : "")}; "
               + $"{texts} menu and dialog texts{(noText > 0 ? $", {noText} without DS3 text" : "")}";
        foreach (var v in voiceLog)
            yield return v;
    }

    /// <summary>
    /// Builds each talk script prefix's voice bank (vc9PP, <see cref="FirstPrefix"/>) and points the lines' rows at it.
    /// A line DS3 has no voice for, or whose voice doesn't decode, gets silence of its reading time: Elden Ring ends a
    /// line when its voice ends, and at once when there is none (1.0.501). A bank that can't be built leaves its lines
    /// voiceless (subtitles for their timeout, as before).
    /// </summary>
    private static List<string> WriteVoices(PorterContext ctx, GameArchives ds3, GameArchives er,
        SortedDictionary<int, List<(PARAM.Row Row, int Voice, int Ds3Line, float Seconds)>> banks)
    {
        var log = new List<string>();
        // Banks of an earlier run with more prefixes are taken away first.
        for (int p = FirstPrefix; p < FirstPrefix + PrefixCount; p++)
            foreach (var path in CutsceneSound.VoiceBankPaths(ctx, $"vc{p}"))
                if (!banks.ContainsKey(p) && File.Exists(path))
                    File.Delete(path);
        if (!Voices)
            return ["people talk: voices off (DARKSIGN_PEOPLE_VOICES=0): subtitles only"];
        if (banks.Count == 0)
            return log;
        var notes = new List<string>();
        CutsceneSound? sound = null;
        try { sound = CutsceneSound.Create(ctx, ds3, er, notes); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { notes.Add($"{ex.GetType().Name}: {ex.Message}"); }
        if (sound is null)
            return [$"people talk: no voice banks ({string.Join("; ", notes)}); lines are subtitles only"];
        int spokenBanks = 0, voiced = 0;
        var built = new List<string>();
        foreach (var (prefix, lines) in banks)
        {
            try
            {
                var voiceLines = lines.Select(l => new CutsceneSound.VoiceLine(l.Voice, sound.Voice(l.Ds3Line) is int v ? $"v{v:D9}" : null, l.Seconds)).ToList();
                var (summary, done) = sound.BuildVoiceBank($"vc{prefix}", voiceLines);
                foreach (var l in lines.Where(l => done.Contains(l.Voice)))
                {
                    PeopleArmour.SetCell(l.Row, "voiceId", l.Voice);
                    PeopleArmour.SetCell(l.Row, "voiceId_female", l.Voice);
                    voiced++;
                }
                spokenBanks++;
                built.Add(summary);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                built.Add($"vc{prefix} FAILED ({ex.GetType().Name}: {ex.Message}): its lines are subtitles only");
            }
        }
        string index;
        try { index = sound.WriteIndex(er); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { index = $"index FAILED ({ex.Message})"; }
        log.Add($"people talk: voices: {voiced} lines in {spokenBanks} voice banks (sd\\vc9PP.bnk, Play_v<voiceId>; a line DS3 has no voice for is silence of its reading time); {index}");
        log.Add("  people talk: voice banks " + sound.VoiceTemplateNote);
        log.AddRange(built.Select(b => "  people talk: voice bank " + b));
        return log;
    }

    /// <summary>Seconds a voiceless line stays up (TalkParam timeout): about 15 characters a second, 3 to 14 seconds.</summary>
    public static float ReadingTime(string words) => Math.Clamp(1.5f + words.Length / 15f, 3f, 14f);

    // ---- Shops ----

    private static string WriteShops(ParamStore regulation, ParamStore? ds3Params, Needs needs, Func<int, int, int?> items)
    {
        if (needs.Shops.Count == 0)
            return "people talk: no shops opened by the converted scripts";
        var shop = regulation.Get("ShopLineupParam") ?? throw new InvalidDataException("Elden Ring has no ShopLineupParam");
        shop.Rows.RemoveAll(r => r.ID >= ShopBase);
        if (ds3Params?.Get("ShopLineupParam") is not { } ds3Shop)
            return $"people talk: {needs.Shops.Count} shops opened but DS3's ShopLineupParam is unreadable: their shelves are empty";
        // The row to copy: a plain goods row sold for runes.
        var template = shop.Rows.Where(r => PeopleArmour.Cell(r, "equipType") == 3 && PeopleArmour.Cell(r, "costType") <= 0 && PeopleArmour.Cell(r, "mtrlId") <= 0)
                           .OrderBy(r => r.ID).FirstOrDefault() ?? shop.Rows.OrderBy(r => r.ID).First();
        int sold = 0, noItem = 0;
        var done = new HashSet<int>();
        foreach (var (start, end) in needs.Shops)
            foreach (var source in ds3Shop.Rows.Where(r => r.ID >= start && r.ID <= end))
            {
                if (!done.Add(source.ID))
                    continue;
                int type = PeopleArmour.Cell(source, "equipType"), item = PeopleArmour.Cell(source, "equipId");
                if (type is < 0 or > 3 || items(type, item) is not int erItem)
                {
                    noItem++;
                    continue;
                }
                var row = new PARAM.Row(ShopBase + source.ID, source.Name ?? "", shop.AppliedParamdef);
                for (int i = 0; i < row.Cells.Count; i++)
                    row.Cells[i].Value = template.Cells[i].Value is byte[] b ? b.Clone() : template.Cells[i].Value;
                PeopleArmour.SetCell(row, "equipId", erItem);
                PeopleArmour.SetCell(row, "equipType", type);
                PeopleArmour.SetCell(row, "value", PeopleArmour.Cell(source, "value"));
                PeopleArmour.SetCell(row, "mtrlId", -1);
                PeopleArmour.SetCell(row, "sellQuantity", -1);
                PeopleArmour.SetCell(row, "eventFlag_forStock", 0);
                PeopleArmour.SetCell(row, "eventFlag_forRelease", 0);
                shop.Rows.Add(row);
                sold++;
            }
        shop.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return $"people talk: {needs.Shops.Count} shops, {sold} wares on the shelves (ShopLineupParam {ShopBase}+: ported weapons and Elden Ring items of the same name), "
               + $"{noItem} DS3 wares with no Elden Ring item yet left off";
    }

    /// <summary>Takes the talk scripts off every placed person (the scripts couldn't be written).</summary>
    public static int Revert(PorterContext ctx, IEnumerable<MapSlots.Slot> slots)
    {
        int reverted = 0;
        foreach (var slot in slots)
        {
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            if (!File.Exists(path))
                continue;
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            int here = 0;
            foreach (var e in msb.Parts.Enemies.Where(e => IsOurs(e.TalkID)))
            {
                e.TalkID = 0;
                here++;
            }
            if (here > 0)
                File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
            reverted += here;
        }
        return reverted;
    }
}
