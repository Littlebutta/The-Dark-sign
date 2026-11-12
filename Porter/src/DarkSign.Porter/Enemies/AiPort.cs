using System.Text;
using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// DS3's AI for the ported enemies, so they choose DS3's attacks (Devin, 2026-11-12 playtest: "many of the ported
/// enemies use elden ring attacks and movesets"). Until now a ported enemy kept its Elden Ring template's NpcThinkParam
/// row, so the template's battle goal picked the moves by the template's animation IDs.
///
/// DS3 and Elden Ring run the same compiled Havok Script files (<see cref="HksChunk"/>), the same way: an
/// NpcThinkParam row names a battle goal and a logic by number, the goal scripts register themselves under a
/// GOAL_ global that aicommon's goal_list.lua sets to that number, and they call aicommon's shared functions. The port,
/// the same for every enemy:
/// <list type="bullet">
/// <item>Each DS3 NpcThinkParam row of a ported character (its ID / 100 is the character ID, as in both games) gets a
/// row of its own: the template's row with DS3's senses and distances (fields of the same name and kind) and DS3's
/// battle goal and logic.</item>
/// <item>The DS3 script that registers the battle goal (found in DS3's aicommon and map luabnds) goes into a binder
/// of its own, /script/NNNNNN_battle.luabnd.dcx, as Elden Ring ships one per goal: the script, its .luagnl (the
/// globals it assigns) and its .luainfo entries. Logics likewise as NNNNNN_logic. A goal keeps DS3's number when
/// Elden Ring doesn't use it, else it takes a free one from 990000 up; goal_list.lua and logic_list.lua learn the
/// new names.</item>
/// <item>DS3 aicommon files a ported script needs that Elden Ring lacks (DS3's enemy step, multi-attack and
/// keep-distance goals...) are added to Elden Ring's aicommon.luabnd, but only those that don't assign a global
/// Elden Ring's own common scripts assign: Elden Ring's common functions are never replaced, so its own enemies
/// can't change. A script that would replace one is left out and its character keeps the template's AI.</item>
/// </list>
/// What a script needs that can't come along (a function only another map's script defines, an engine goal Elden
/// Ring may not have) is listed in Work\reports\enemy-ai.tsv, with the engine names no Elden Ring common script uses.
/// Fallbacks "enemy-ai": DARKSIGN_ENEMY_AI=template keeps the templates' AI (builds before this option).
/// Method (RULE 3): the file layout follows katalash's DSLuaDecompiler (HKS reader for DS3, Sekiro and Elden Ring)
/// and SoulsFormats' LUAGNL / LUAINFO; the per-goal binder layout is Elden Ring's own (300000_battle.luabnd).
/// </summary>
public sealed class AiPort
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_AI")?.Trim().ToLowerInvariant() is not ("template" or "0" or "off");

    /// <summary>
    /// DS3's goal scripts ask which NpcThinkParam row they run under (self:GetNpcThinkParamID() == 110054: the High
    /// Wall's transforming Hollow Soldiers only walk up to you) and compare it with DS3's row numbers, which a ported
    /// row doesn't have (it is NewChr*10000 + 9000 + DS3 row % 100). Such a number is renumbered to the ported row in
    /// the functions that make the call. DARKSIGN_ENEMY_THINK_IDS=ds3 keeps DS3's numbers, as up to 1.0.383.
    /// </summary>
    public static bool RenumberThinks => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_THINK_IDS")?.Trim().ToLowerInvariant() != "ds3";

    public const string ThinkCall = "GetNpcThinkParamID";

    public const string ReportFile = "enemy-ai.tsv";
    public const string CommonPath = "/script/aicommon.luabnd.dcx";
    /// <summary>Where goals and logics Elden Ring already uses the DS3 number of are moved to (six digits, as the binder names are).</summary>
    public const int FirstRemapped = 990000, LastRemapped = 999999;
    public const int ThinkOffset = 9000;

    public static string BattlePath(int goal) => $"/script/{goal:D6}_battle.luabnd.dcx";
    public static string LogicPath(int logic) => $"/script/{logic:D6}_logic.luabnd.dcx";

    /// <summary>One compiled script in one of DS3's luabnds.</summary>
    public sealed class Script
    {
        public required string Binder;
        public required BinderFile File;
        public required HksChunk Chunk;
        public string Leaf => ItemText.Leaf(File.Name);
        public HashSet<string> Assigns = new(StringComparer.Ordinal);
        public List<string> AssignOrder = [];
        public HashSet<string> Reads = new(StringComparer.Ordinal);
        /// <summary>The GOAL_ names the main chunk registers (RegisterTableGoal / REGISTER_GOAL*(GOAL_X, ...)).</summary>
        public List<string> Registers = [];
        public List<LUAINFO.Goal> Info = [];
        public bool Common;
    }

    /// <summary>One ported NpcThinkParam row. Battle / Logic null: the template's stays.</summary>
    public sealed record Think(int Ds3Think, int ErThink, int? Battle, int? Logic, string Note);

    /// <summary>A ported character's AI: its rows, and the one its NpcParam rows and the F1 spawn list use.</summary>
    public sealed record ChrAi(int Ds3Chr, IReadOnlyList<Think> Rows, int Primary);

    // ---- Elden Ring ----
    private readonly BND4 _erCommon;
    private readonly DCX.CompressionInfo _compression;
    private readonly Func<string, bool> _erHas;
    private readonly HksChunk _erGoalList, _erLogicList;
    private readonly Dictionary<string, double> _erGoals, _erLogics;
    private readonly HashSet<int> _takenGoals = [], _takenLogics = [];
    private readonly HashSet<string> _erCommonGlobals = new(StringComparer.Ordinal);
    /// <summary>What Elden Ring's common scripts define when they load (main chunk assignments): the globals a port must not replace.</summary>
    private readonly HashSet<string> _erCommonDefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HksChunk.Function> _erFunctions = new(StringComparer.Ordinal);
    /// <summary>Globals Elden Ring's common scripts assign in more than one file (per-file scratch such as GetWellSpace_Odds): reassigning them replaces nothing.</summary>
    private readonly HashSet<string> _erScratch = new(StringComparer.Ordinal) { "Goal" };
    private readonly HashSet<string> _erStrings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _erLeaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly LUAINFO? _erInfo;

    // ---- DS3 ----
    private readonly List<Script> _ds3 = [];
    private readonly Dictionary<string, double> _ds3Goals = new(StringComparer.Ordinal), _ds3Logics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Script> _ds3Library = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ds3Assigned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LUAINFO.Goal> _ds3CommonInfo = new(StringComparer.Ordinal);

    // ---- The port ----
    private readonly Dictionary<string, int> _newGoals = new(StringComparer.Ordinal), _newLogics = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int? Er, string Note)> _battles = [], _logics = [];
    private readonly List<Script> _libraries = [];
    private readonly HashSet<Script> _libraryTried = [];
    private readonly Dictionary<int, int> _libraryGoals = [];
    private readonly Dictionary<string, (Script Script, HksChunk.Function Function)> _ds3Functions = new(StringComparer.Ordinal);
    private readonly Dictionary<Script, List<(string Name, HksChunk.Function Function)>> _lifted = [];
    private readonly HashSet<string> _liftedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _ds3Constants = new(StringComparer.Ordinal), _liftedConstants = new(StringComparer.Ordinal);
    /// <summary>The per-goal binders, made in <see cref="Finish"/> once every character's think rows are known.</summary>
    private readonly List<(string Path, Func<byte[]> Bytes)> _outputs = [];
    /// <summary>DS3 NpcThinkParam row -> its ported Elden Ring row, for the scripts that check which row they run under.</summary>
    private readonly Dictionary<int, int> _thinkIds = [];
    private readonly SortedDictionary<string, int> _thinkRewrites = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Report = [];
    public readonly SortedDictionary<string, SortedSet<string>> Missing = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, SortedSet<string>> Unknown = new(StringComparer.Ordinal);

    public IReadOnlyList<Script> Libraries => _libraries;
    public IReadOnlyDictionary<string, int> NewGoals => _newGoals;
    public IReadOnlyDictionary<string, int> NewLogics => _newLogics;

    /// <param name="erCommon">Elden Ring's aicommon.luabnd.</param>
    /// <param name="erHas">Whether Elden Ring's archives hold this path (for free goal numbers).</param>
    /// <param name="ds3Binders">DS3's luabnds, aicommon first, then in the order a script is looked for.</param>
    /// <param name="erGoalsInUse">
    /// The battle goals Elden Ring's NpcThinkParam rows use: a goal script in aicommon no row uses (920090_battle.lua,
    /// which holds copies of DS3's hollow soldier functions) is never run, so replacing what it defines costs nothing.
    /// </param>
    public AiPort(BND4 erCommon, DCX.CompressionInfo compression, Func<string, bool> erHas, IEnumerable<(string Name, BND4 Bnd)> ds3Binders,
        IReadOnlySet<int>? erGoalsInUse = null)
    {
        _erCommon = erCommon;
        _compression = compression;
        _erHas = erHas;
        _erGoalList = HksChunk.Read(erCommon.Files.First(f => ItemText.Leaf(f.Name).Equals("goal_list.lua", StringComparison.OrdinalIgnoreCase)).Bytes);
        _erLogicList = HksChunk.Read(erCommon.Files.First(f => ItemText.Leaf(f.Name).Equals("logic_list.lua", StringComparison.OrdinalIgnoreCase)).Bytes);
        _erGoals = _erGoalList.NumericGlobals();
        _erLogics = _erLogicList.NumericGlobals();
        _takenGoals.UnionWith(_erGoals.Values.Select(v => (int)v));
        _takenLogics.UnionWith(_erLogics.Values.Select(v => (int)v));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in erCommon.Files)
        {
            _erLeaves.Add(ItemText.Leaf(f.Name));
            if (ItemText.Leaf(f.Name) is var leaf && Regex.Match(leaf, @"^(\d{6})_(battle|logic)\.lua$") is { Success: true } m)
                (m.Groups[2].Value == "battle" ? _takenGoals : _takenLogics).Add(int.Parse(m.Groups[1].Value));
            if (!HksChunk.Is(f.Bytes))
                continue;
            var chunk = HksChunk.Read(f.Bytes);
            _erCommonGlobals.UnionWith(chunk.AssignedGlobals());
            if (erGoalsInUse is not null && Regex.Match(ItemText.Leaf(f.Name), @"^(\d{6})_battle\.lua$") is { Success: true } unused
                && !erGoalsInUse.Contains(int.Parse(unused.Groups[1].Value)))
                continue;
            foreach (var g in chunk.DefinedGlobals())
            {
                _erCommonDefs.Add(g);
                counts[g] = counts.GetValueOrDefault(g) + 1;
            }
            foreach (var (name, fn) in chunk.GlobalFunctions())
                _erFunctions[name] = fn;
            _erStrings.UnionWith(chunk.Strings());
        }
        _erScratch.UnionWith(counts.Where(c => c.Value > 1).Select(c => c.Key));
        if (erCommon.Files.FirstOrDefault(f => f.Name.EndsWith(".luainfo", StringComparison.OrdinalIgnoreCase)) is { } info)
        {
            _erInfo = LUAINFO.Read(info.Bytes);
            _takenGoals.UnionWith(_erInfo.Goals.Select(g => g.ID));
        }

        foreach (var (name, bnd) in ds3Binders)
        {
            bool common = name.Contains("aicommon", StringComparison.OrdinalIgnoreCase);
            var goals = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".luainfo", StringComparison.OrdinalIgnoreCase)) is { } i ? LUAINFO.Read(i.Bytes).Goals : [];
            foreach (var f in bnd.Files.OrderBy(f => f.ID))
            {
                if (!f.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || !HksChunk.Is(f.Bytes))
                    continue;
                var s = new Script { Binder = name, File = f, Chunk = HksChunk.Read(f.Bytes), Common = common };
                foreach (var g in s.Chunk.AssignedGlobals())
                    if (s.Assigns.Add(g))
                        s.AssignOrder.Add(g);
                s.Reads.UnionWith(s.Chunk.ReadGlobals());
                s.Registers.AddRange(Registered(s.Chunk));
                s.Info.AddRange(goals.Where(g => Owns(s, g.Name)));
                _ds3.Add(s);
                _ds3Assigned.UnionWith(s.Assigns);
                foreach (var (fname, fn) in s.Chunk.GlobalFunctions())
                    _ds3Functions.TryAdd(fname, (s, fn));
                if (common)
                    foreach (var (cname, value) in Constants(s.Chunk))
                        _ds3Constants.TryAdd(cname, value);
                if (common)
                {
                    if (s.Leaf.Equals("goal_list.lua", StringComparison.OrdinalIgnoreCase))
                        foreach (var kv in s.Chunk.NumericGlobals()) _ds3Goals[kv.Key] = kv.Value;
                    else if (s.Leaf.Equals("logic_list.lua", StringComparison.OrdinalIgnoreCase))
                        foreach (var kv in s.Chunk.NumericGlobals()) _ds3Logics[kv.Key] = kv.Value;
                    if (!s.Leaf.EndsWith("_list.lua", StringComparison.OrdinalIgnoreCase))
                        foreach (var g in s.Assigns)
                            _ds3Library.TryAdd(g, s);
                }
            }
            if (common)
                foreach (var g in goals)
                    _ds3CommonInfo.TryAdd(g.Name, g);
        }
        // Goals written as functions (Name_Activate, Name_Update...) are registered by their .luainfo entry, not by a
        // call: the script that owns the entry registers every GOAL_ name with the entry's number.
        // The functions are named after the goal (GOAL_COMMON_ApproachTarget_AlwaysGuard: ApproachTarget_AlwaysGuard_Activate).
        var byValue = _ds3Goals.GroupBy(g => (int)g.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());
        var byActivate = _ds3Goals.Keys.GroupBy(n => GoalStem(n) + "_Activate", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var s in _ds3)
            foreach (var name in s.Assigns.SelectMany(a => byActivate.GetValueOrDefault(a, [])))
                if (!s.Registers.Contains(name))
                    s.Registers.Add(name);
        // Failing that, the entry's number (an entry's name can be shorter than the goal's: 2014 "ApproachTarget").
        var registered = _ds3.SelectMany(s => s.Registers).ToHashSet(StringComparer.Ordinal);
        foreach (var s in _ds3)
            foreach (var name in s.Info.Where(g => !g.LogicInterrupt).SelectMany(g => byValue.GetValueOrDefault(g.ID, [])))
                if (!registered.Contains(name) && !s.Registers.Contains(name))
                    s.Registers.Add(name);
        foreach (var s in _ds3.Where(s => s.Common))
            foreach (var name in s.Registers)
                _ds3Library.TryAdd(name, s);
    }

    /// <summary>Reads Elden Ring's aicommon and every DS3 luabnd from the archives (aicommon, DS3's maps m30-m51, then the rest).</summary>
    public static AiPort Create(GameArchives er, GameArchives ds3, PARAM? erThink)
    {
        var (bytes, compression) = er.ReadWithCompression(CommonPath);
        var erCommon = BND4.Read(bytes);
        var binders = ds3.Entries.Select(e => e.Path).OfType<string>()
            .Where(p => p.StartsWith("/script/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".luabnd.dcx", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(BinderOrder).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p, BND4.Read(ds3.Read(p)))).ToList();
        if (!binders.Any(b => b.p.Contains("aicommon", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("DS3's /script/aicommon.luabnd.dcx was not found");
        var inUse = erThink?.Rows.Select(r => IntCell(r, "battleGoalID")).Where(g => g > 0).ToHashSet();
        return new AiPort(erCommon, compression, er.Contains, binders, inUse);
    }

    /// <summary>aicommon first, DS3's own maps (m30-m51) next, test maps last.</summary>
    public static int BinderOrder(string path)
    {
        var leaf = ItemText.Leaf(path);
        if (leaf.StartsWith("aicommon", StringComparison.OrdinalIgnoreCase))
            return 0;
        return Regex.Match(leaf, @"^m(\d\d)_") is { Success: true } m && int.Parse(m.Groups[1].Value) is >= 30 and < 60 ? 1 : 2;
    }

    /// <summary>The GOAL_ globals a main chunk registers: a GETGLOBAL of a Register function followed by one of GOAL_X.</summary>
    public static IEnumerable<string> Registered(HksChunk chunk)
    {
        var f = chunk.Main;
        string? last = null;
        foreach (var i in f.Instructions)
        {
            if (HksChunk.Op(i) != 5 || HksChunk.Bx(i) >= f.Constants.Count || f.Constants[HksChunk.Bx(i)].AsString is not { } name)
                continue;
            if (last is not null && name.StartsWith("GOAL_", StringComparison.Ordinal) &&
                (last.Equals("RegisterTableGoal", StringComparison.Ordinal) || last.StartsWith("REGISTER_GOAL", StringComparison.Ordinal)))
                yield return name;
            last = name;
        }
    }

    /// <summary>The numbers a main chunk sets globals to (LOADK R, K(number); SETGLOBAL R, K(name)), as ai_define.lua does.</summary>
    public static IEnumerable<(string Name, double Value)> Constants(HksChunk chunk)
    {
        var f = chunk.Main;
        for (int i = 0; i + 1 < f.Instructions.Count; i++)
        {
            uint a = f.Instructions[i], b = f.Instructions[i + 1];
            if (HksChunk.Op(a) == 1 && HksChunk.Op(b) == 7 && HksChunk.A(a) == HksChunk.A(b) && HksChunk.Bx(a) < f.Constants.Count && HksChunk.Bx(b) < f.Constants.Count
                && f.Constants[HksChunk.Bx(a)].AsNumber is { } value && f.Constants[HksChunk.Bx(b)].AsString is { } name)
                yield return (name, value);
        }
    }

    /// <summary>Whether a .luainfo entry is this script's: it names a global the script assigns (a logic), or the stem of its goal functions.</summary>
    private static bool Owns(Script s, string infoName) =>
        s.Assigns.Contains(infoName) || s.Assigns.Contains(infoName + "_Activate") || s.Assigns.Contains(infoName + "_Update");

    /// <summary>The stem of a goal's function names: GOAL_COMMON_X and GOAL_X are X.</summary>
    public static string GoalStem(string goal) =>
        goal.StartsWith("GOAL_COMMON_", StringComparison.Ordinal) ? goal["GOAL_COMMON_".Length..]
        : goal.StartsWith("GOAL_", StringComparison.Ordinal) ? goal["GOAL_".Length..] : goal;

    // ---- One character ----

    /// <summary>
    /// The AI of one fully ported character: a row for each of DS3's NpcThinkParam rows of the character, with the
    /// goal and logic scripts ported (once each, however many characters share them). <paramref name="thinkTaken"/>
    /// says whether Elden Ring's NpcThinkParam already has a row ID.
    /// </summary>
    public ChrAi? Port(int ds3Chr, int newChr, string name, PARAM ds3Think, Func<int, bool> thinkTaken)
    {
        var rows = new List<Think>();
        foreach (var row in ds3Think.Rows.Where(r => r.ID / 100 == ds3Chr).OrderBy(r => r.ID))
        {
            int erThink = newChr * 10000 + ThinkOffset + row.ID % 100;
            if (thinkTaken(erThink))
            {
                Report.Add(Line(ds3Chr, name, row.ID, -1, "-", "-", "Elden Ring already has NpcThinkParam " + erThink));
                continue;
            }
            _thinkIds[row.ID] = erThink;
            int battle = IntCell(row, "battleGoalId"), logic = IntCell(row, "logicId");
            var (erBattle, battleNote) = battle > 0 ? PortBattle(battle) : (battle, "");
            var (erLogic, logicNote) = logic > 0 ? PortLogic(logic) : (logic, "");
            var note = string.Join("; ", new[] { battleNote, logicNote }.Where(n => n.Length > 0));
            rows.Add(new Think(row.ID, erThink, erBattle, erLogic, note));
            Report.Add(Line(ds3Chr, name, row.ID, erThink, $"{battle}->{erBattle?.ToString() ?? "template"}", $"{logic}->{erLogic?.ToString() ?? "template"}", note));
        }
        if (rows.Count == 0)
            return null;
        var primary = rows.FirstOrDefault(r => r.Battle is > 0) ?? rows[0];
        return new ChrAi(ds3Chr, rows, primary.ErThink);
    }

    private static string Line(int chr, string name, int ds3Think, int erThink, string battle, string logic, string note) =>
        string.Join('\t', $"c{chr:D4}", name.Replace('\t', ' '), ds3Think, erThink, battle, logic, note);

    /// <summary>A DS3 battle goal in Elden Ring: its new number, or null with the reason (the template's stays).</summary>
    public (int? Er, string Note) PortBattle(int ds3Goal)
    {
        if (_battles.TryGetValue(ds3Goal, out var done))
            return done;
        var result = PortBattleUncached(ds3Goal);
        _battles[ds3Goal] = result;
        return result;
    }

    private (int? Er, string Note) PortBattleUncached(int ds3Goal)
    {
        var names = _ds3Goals.Where(g => (int)g.Value == ds3Goal).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var script = _ds3.FirstOrDefault(s => s.Registers.Any(names.Contains))
                     ?? _ds3.FirstOrDefault(s => s.Leaf.Equals($"{ds3Goal:D6}_battle.lua", StringComparison.OrdinalIgnoreCase));
        if (script is null)
            return (null, $"no DS3 script registers goal {ds3Goal}");
        // Elden Ring has this very goal (same name, same number, its own script): use Elden Ring's.
        var mine = script.Registers.Where(names.Contains).ToList();
        if (mine.Count > 0 && mine.All(n => _erGoals.TryGetValue(n, out var v) && (int)v == ds3Goal)
            && (_erHas(BattlePath(ds3Goal)) || _erLeaves.Contains($"{ds3Goal:D6}_battle.lua")))
            return (ds3Goal, "Elden Ring's goal of the same name");
        if (Clash(script) is { } clash)
            return (null, $"{script.Leaf} would replace Elden Ring's {clash}");
        var registered = Allocate(script, out var conflict);
        if (conflict is not null)
            return (null, conflict);
        int er = mine.Count > 0 && registered.TryGetValue(mine[0], out var v2) ? v2 : AllocateGoal(ds3Goal);
        Needs(script);
        var ids = registered.Select(r => ((int)_ds3Goals[r.Key], r.Value)).Append((ds3Goal, er)).ToList();
        _outputs.Add((BattlePath(er), () => MakeBinder(script, $"{er:D6}_battle", ids)));
        return (er, er == ds3Goal ? "" : $"goal {ds3Goal} is {er} in Elden Ring");
    }

    /// <summary>A DS3 logic in Elden Ring: its new number, or null with the reason (the template's stays).</summary>
    public (int? Er, string Note) PortLogic(int ds3Logic)
    {
        if (_logics.TryGetValue(ds3Logic, out var done))
            return done;
        var result = PortLogicUncached(ds3Logic);
        _logics[ds3Logic] = result;
        return result;
    }

    private (int? Er, string Note) PortLogicUncached(int ds3Logic)
    {
        var name = _ds3Logics.FirstOrDefault(l => (int)l.Value == ds3Logic).Key;
        if (name is not null && _erLogics.TryGetValue(name, out var same) && (int)same == ds3Logic)
            return (ds3Logic, "");
        var script = _ds3.FirstOrDefault(s => s.Leaf.Equals($"{ds3Logic:D6}_logic.lua", StringComparison.OrdinalIgnoreCase))
                     ?? _ds3.FirstOrDefault(s => s.Info.Any(g => g.ID == ds3Logic && g.LogicInterrupt));
        if (script is null)
            return (null, $"no DS3 script for logic {ds3Logic}");
        if (Clash(script) is { } clash)
            return (null, $"{script.Leaf} would replace Elden Ring's {clash}");
        if (name is not null && _erLogics.ContainsKey(name))
            return (null, $"Elden Ring's {name} is another logic");
        int er = ds3Logic;
        if (_takenLogics.Contains(er) || _erHas(LogicPath(er)))
            er = Enumerable.Range(FirstRemapped, LastRemapped - FirstRemapped + 1).First(n => !_takenLogics.Contains(n) && !_erHas(LogicPath(n)));
        _takenLogics.Add(er);
        if (name is not null)
            _newLogics[name] = er;
        Needs(script);
        _outputs.Add((LogicPath(er), () => MakeBinder(script, $"{er:D6}_logic", [(ds3Logic, er)])));
        return (er, er == ds3Logic ? "" : $"logic {ds3Logic} is {er} in Elden Ring");
    }

    /// <summary>
    /// The first global the script assigns that one of Elden Ring's common scripts defines when it loads, unless it is
    /// per-file scratch (defined by several of them) or the script defines it as the very same function (Elden Ring
    /// kept a few DS3 functions, such as the hollow soldiers' MoujaSoldier110000_Act15 in its 920090_battle.lua).
    /// </summary>
    private string? Clash(Script s)
    {
        var mine = s.Chunk.GlobalFunctions();
        return s.AssignOrder.FirstOrDefault(g => _erCommonDefs.Contains(g) && !_erScratch.Contains(g)
            && !(mine.TryGetValue(g, out var f) && _erFunctions.TryGetValue(g, out var e) && HksChunk.SameCode(f, e)));
    }

    /// <summary>
    /// Numbers for the goals a script registers: Elden Ring's where it has the same name and number, DS3's where Elden
    /// Ring leaves it free, else a free one. A name Elden Ring gives another number is a conflict (re-registering it
    /// would take the goal from Elden Ring's own enemies).
    /// </summary>
    private Dictionary<string, int> Allocate(Script s, out string? conflict)
    {
        conflict = null;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in s.Registers.Distinct())
        {
            if (!_ds3Goals.TryGetValue(name, out var ds3Value))
                continue;
            if (_erGoals.TryGetValue(name, out var erValue))
            {
                if ((int)erValue != (int)ds3Value)
                {
                    conflict = $"{s.Leaf} registers {name}, which is goal {(int)erValue} in Elden Ring and {(int)ds3Value} in DS3";
                    return result;
                }
                result[name] = (int)erValue;
                continue;
            }
            if (!_newGoals.TryGetValue(name, out var v))
                _newGoals[name] = v = AllocateGoal((int)ds3Value);
            result[name] = v;
        }
        return result;
    }

    private int AllocateGoal(int ds3Value)
    {
        int v = ds3Value;
        if (_takenGoals.Contains(v) || _erHas(BattlePath(v)))
            v = Enumerable.Range(FirstRemapped, LastRemapped - FirstRemapped + 1).First(n => !_takenGoals.Contains(n) && !_erHas(BattlePath(n)));
        _takenGoals.Add(v);
        return v;
    }

    /// <summary>
    /// What a ported script reads that Elden Ring doesn't define: DS3 common files that can come along are added to
    /// aicommon (and what they read in turn), goal and logic numbers to the lists, the rest goes in the report.
    /// </summary>
    private void Needs(Script s) => Needs(s.Reads, s.Assigns, s.Chunk.Strings(), s.Leaf, s);

    private void Needs(IEnumerable<string> reads, IReadOnlySet<string> own, IEnumerable<string> strings, string by, Script? self)
    {
        foreach (var g in reads.Order(StringComparer.Ordinal))
        {
            if (own.Contains(g) || Defined(g))
                continue;
            if (_ds3Library.TryGetValue(g, out var lib) && lib != self && AddLibrary(lib))
                continue;
            if (_ds3Goals.TryGetValue(g, out var goal))
            {
                // A goal no DS3 script registers is the engine's own (C++): it keeps its number or doesn't exist.
                if (!_takenGoals.Contains((int)goal))
                {
                    _newGoals[g] = (int)goal;
                    _takenGoals.Add((int)goal);
                    Note(Unknown, $"engine goal {g} ({(int)goal})", by);
                }
                else
                    Note(Missing, $"{g} (DS3 goal {(int)goal}, a number Elden Ring uses for another goal)", by);
                continue;
            }
            if (_ds3Logics.TryGetValue(g, out var logic) && !_takenLogics.Contains((int)logic))
            {
                _newLogics[g] = (int)logic;
                _takenLogics.Add((int)logic);
                continue;
            }
            if (Lift(g))
                continue;
            if (_ds3Assigned.Contains(g))
                Note(Missing, g + (_ds3Library.TryGetValue(g, out var l) ? $" (in DS3's {l.Leaf}, {WhyNot(l)})" : " (only another DS3 script defines it)"), by);
        }
        foreach (var name in strings.Distinct())
            if (Regex.IsMatch(name, "^[A-Z][A-Za-z0-9_]{3,}$") && !_erStrings.Contains(name) && !_ds3Assigned.Contains(name) && !Defined(name)
                && !name.StartsWith("GOAL_", StringComparison.Ordinal))
                Note(Unknown, name, by);
    }

    /// <summary>
    /// A function a ported script calls that only a file that can't come along defines (DS3's common_func.lua would
    /// replace Elden Ring's functions of the same names): the function alone, lifted into a file of its own
    /// (<see cref="HksChunk.Lifted"/>), with what it needs in turn.
    /// </summary>
    private bool Lift(string name)
    {
        if (_ds3Constants.TryGetValue(name, out var constant))
        {
            _liftedConstants[name] = constant;
            _liftedNames.Add(name);
            return true;
        }
        if (!_ds3Functions.TryGetValue(name, out var found) || HksChunk.Upvalues(found.Function) != 0)
            return false;
        if (!_lifted.TryGetValue(found.Script, out var list))
            _lifted[found.Script] = list = [];
        list.Add((name, found.Function));
        _liftedNames.Add(name);
        Needs(HksChunk.ReadGlobals(found.Function), new HashSet<string>(StringComparer.Ordinal) { name },
            found.Function.All().SelectMany(f => f.Constants).Select(c => c.AsString).OfType<string>(), $"{found.Script.Leaf} ({name})", null);
        return true;
    }

    private string WhyNot(Script lib) =>
        _erLeaves.Contains(lib.Leaf) ? "a file name Elden Ring's aicommon has"
        : Clash(lib) is { } clash ? $"which would replace Elden Ring's {clash}"
        : "which registers a goal Elden Ring numbers otherwise";

    private bool Defined(string g) =>
        _erCommonGlobals.Contains(g) || _erGoals.ContainsKey(g) || _erLogics.ContainsKey(g) || _newGoals.ContainsKey(g) || _newLogics.ContainsKey(g)
        || _liftedNames.Contains(g) || _libraries.Any(l => l.Assigns.Contains(g));

    private static void Note(SortedDictionary<string, SortedSet<string>> into, string what, string by)
    {
        if (!into.TryGetValue(what, out var set))
            into[what] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(by);
    }

    /// <summary>Adds a DS3 common file to Elden Ring's aicommon, if it replaces nothing of Elden Ring's.</summary>
    private bool AddLibrary(Script lib)
    {
        if (_libraries.Contains(lib))
            return true;
        if (!_libraryTried.Add(lib) || _erLeaves.Contains(lib.Leaf) || Clash(lib) is not null)
            return false;
        var registered = Allocate(lib, out var conflict);
        if (conflict is not null)
        {
            Note(Missing, conflict, lib.Leaf);
            return false;
        }
        foreach (var name in lib.Registers)
            if (_ds3Goals.TryGetValue(name, out var d) && registered.TryGetValue(name, out var e))
                _libraryGoals[(int)d] = e;
        _libraries.Add(lib);
        Needs(lib);
        return true;
    }

    /// <summary>A per-goal binder as Elden Ring ships them: the script (ID 1000), its .luagnl and, if it has any, its .luainfo entries.</summary>
    private byte[] MakeBinder(Script s, string stem, IEnumerable<(int Ds3, int Er)> ids)
    {
        var map = ids.GroupBy(i => i.Ds3).ToDictionary(g => g.Key, g => g.First().Er);
        string dir = $@"N:\GR\data\INTERROOT_win64\script\ai\out\each\{stem}\{stem}";
        var bnd = NewBinder();
        bnd.Files.Add(new BinderFile(SoulsFormats.Binder.FileFlags.Flag1, 1000, dir + ".lua", ThinksRenumbered(s.File.Bytes, s.Leaf)));
        var gnl = new LUAGNL(false, true);
        gnl.Globals.AddRange(s.AssignOrder);
        bnd.Files.Add(new BinderFile(SoulsFormats.Binder.FileFlags.Flag1, 1000000, dir + ".luagnl", gnl.Write()));
        if (s.Info.Count > 0)
        {
            var info = new LUAINFO(false, true);
            info.Goals.AddRange(s.Info.Select(g => new LUAINFO.Goal(map.GetValueOrDefault(g.ID, g.ID), g.Name, g.BattleInterrupt, g.LogicInterrupt, g.LogicInterruptName)));
            bnd.Files.Add(new BinderFile(SoulsFormats.Binder.FileFlags.Flag1, 1000001, dir + ".luainfo", info.Write()));
        }
        return Compress(bnd.Write());
    }

    /// <summary>A compiled script with the DS3 think rows it checks for renumbered (see <see cref="RenumberThinks"/>); the same bytes when none.</summary>
    private byte[] ThinksRenumbered(byte[] bytes, string leaf)
    {
        if (!RenumberThinks || _thinkIds.Count == 0 || !HksChunk.Is(bytes))
            return bytes;
        var chunk = HksChunk.Read(bytes);
        int n = RenumberThinkIds(chunk, _thinkIds);
        if (n == 0)
            return bytes;
        _thinkRewrites[leaf] = n;
        return chunk.Write();
    }

    /// <summary>
    /// In every function of the chunk that calls <see cref="ThinkCall"/> (the method name is one of its constants),
    /// each number constant that is a ported DS3 think row becomes its Elden Ring row. Returns how many changed.
    /// </summary>
    public static int RenumberThinkIds(HksChunk chunk, IReadOnlyDictionary<int, int> thinkIds)
    {
        int n = 0;
        foreach (var f in chunk.Main.All().Where(f => f.Constants.Any(c => c.AsString == ThinkCall)))
            for (int i = 0; i < f.Constants.Count; i++)
                if (f.Constants[i].AsNumber is { } d && d == Math.Floor(d) && d is > 0 and < int.MaxValue && thinkIds.TryGetValue((int)d, out var er))
                {
                    f.Constants[i] = HksChunk.Constant.Number(er);
                    n++;
                }
        return n;
    }

    private BND4 NewBinder() => new()
    {
        Version = _erCommon.Version, Format = _erCommon.Format, Unicode = _erCommon.Unicode, Extended = _erCommon.Extended,
        BigEndian = _erCommon.BigEndian, BitBigEndian = _erCommon.BitBigEndian, Unk04 = _erCommon.Unk04, Unk05 = _erCommon.Unk05,
    };

    private byte[] Compress(byte[] bytes) => _compression.Type == DCX.Type.None ? bytes : DCX.Compress(bytes, _compression);

    // ---- The shared files ----

    /// <summary>
    /// The per-goal binders and, when anything was added to it, Elden Ring's aicommon.luabnd with the new goal and
    /// logic numbers and the DS3 common files (after Elden Ring's own, in DS3's order).
    /// </summary>
    public List<(string Path, byte[] Bytes)> Finish()
    {
        var outputs = _outputs.Select(o => (o.Path, o.Bytes())).ToList();
        if (_newGoals.Count == 0 && _newLogics.Count == 0 && _libraries.Count == 0 && _liftedNames.Count == 0)
            return outputs;
        var bnd = _erCommon;
        var goalFile = bnd.Files.First(f => ItemText.Leaf(f.Name).Equals("goal_list.lua", StringComparison.OrdinalIgnoreCase));
        var logicFile = bnd.Files.First(f => ItemText.Leaf(f.Name).Equals("logic_list.lua", StringComparison.OrdinalIgnoreCase));
        var goals = HksChunk.Read(goalFile.Bytes);
        goals.AddNumericGlobals(_newGoals.OrderBy(g => g.Value).Select(g => (g.Key, (double)g.Value)));
        goalFile.Bytes = goals.Write();
        var logics = HksChunk.Read(logicFile.Bytes);
        logics.AddNumericGlobals(_newLogics.OrderBy(g => g.Value).Select(g => (g.Key, (double)g.Value)));
        logicFile.Bytes = logics.Write();
        string dir = goalFile.Name[..(goalFile.Name.Length - ItemText.Leaf(goalFile.Name).Length)];
        int id = bnd.Files.Where(f => f.ID < 1000000).Max(f => f.ID) + 1;
        var added = new List<string>();
        if (_liftedConstants.Count > 0)
        {
            // DS3's constants first: the DS3 files after them may use them as they load.
            var list = new HksChunk { Header = goals.Header, Main = new HksChunk.Function { Name = goals.Main.Name, Fixed = goals.Main.Fixed, Instructions = [HksChunk.Return] } };
            list.AddNumericGlobals(_liftedConstants.Select(c => (c.Key, c.Value)));
            bnd.Files.Add(new BinderFile(goalFile.Flags, id++, dir + "ai_define_ds3.lua", list.Write()));
            added.AddRange(_liftedConstants.Keys);
        }
        foreach (var lib in _libraries.OrderBy(l => l.File.ID))
        {
            bnd.Files.Add(new BinderFile(goalFile.Flags, id++, dir + lib.Leaf, ThinksRenumbered(lib.File.Bytes, lib.Leaf)));
            added.AddRange(lib.AssignOrder);
        }
        foreach (var (source, functions) in _lifted.OrderBy(l => l.Key.Common ? 0 : 1).ThenBy(l => l.Key.Leaf, StringComparer.OrdinalIgnoreCase))
        {
            var leaf = Path.GetFileNameWithoutExtension(source.Leaf) + "_ds3.lua";
            bnd.Files.Add(new BinderFile(goalFile.Flags, id++, dir + leaf, ThinksRenumbered(HksChunk.Lifted(source.Chunk, functions).Write(), leaf)));
            added.AddRange(functions.Select(f => f.Name));
        }
        bnd.Files.Sort((a, b) => a.ID.CompareTo(b.ID));
        if (bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".luagnl", StringComparison.OrdinalIgnoreCase)) is { } gnlFile)
        {
            var gnl = LUAGNL.Read(gnlFile.Bytes);
            var have = gnl.Globals.ToHashSet(StringComparer.Ordinal);
            gnl.Globals.AddRange(_newGoals.OrderBy(g => g.Value).Select(g => g.Key).Concat(_newLogics.OrderBy(g => g.Value).Select(g => g.Key))
                .Concat(added).Where(have.Add));
            gnlFile.Bytes = gnl.Write();
        }
        if (_erInfo is not null && bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".luainfo", StringComparison.OrdinalIgnoreCase)) is { } infoFile)
        {
            var have = _erInfo.Goals.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var lib in _libraries.OrderBy(l => l.File.ID))
                foreach (var g in _ds3CommonInfo.Values.Where(g => Owns(lib, g.Name) && have.Add(g.Name)))
                    _erInfo.Goals.Add(new LUAINFO.Goal(_libraryGoals.GetValueOrDefault(g.ID, g.ID), g.Name, g.BattleInterrupt, g.LogicInterrupt, g.LogicInterruptName));
            infoFile.Bytes = _erInfo.Write();
        }
        outputs.Add((CommonPath, Compress(bnd.Write())));
        return outputs;
    }

    /// <summary>Work\reports\enemy-ai.tsv: one line per ported NpcThinkParam row, then what was added and what is missing.</summary>
    public IEnumerable<string> ReportLines()
    {
        yield return "# DS3 chr\tname\tDS3 think\tER think\tbattle goal (DS3->ER)\tlogic (DS3->ER)\tnote";
        foreach (var l in Report)
            yield return l;
        yield return $"# {_libraries.Count} DS3 common files added to aicommon: {string.Join(", ", _libraries.Select(l => l.Leaf))}";
        yield return $"# {_liftedNames.Count} DS3 functions lifted out of files that can't come along: {string.Join(", ", _lifted.SelectMany(l => l.Value.Select(f => $"{f.Name} ({l.Key.Leaf})")).Concat(_liftedConstants.Keys))}";
        yield return $"# {_newGoals.Count} goal numbers added to goal_list: {string.Join(", ", _newGoals.OrderBy(g => g.Value).Select(g => $"{g.Key}={g.Value}"))}";
        yield return $"# {_newLogics.Count} logic numbers added to logic_list: {string.Join(", ", _newLogics.OrderBy(g => g.Value).Select(g => $"{g.Key}={g.Value}"))}";
        yield return RenumberThinks
            ? $"# DS3 think rows the scripts check for ({ThinkCall}), renumbered to the ported rows in {_thinkRewrites.Count} scripts: {string.Join(", ", _thinkRewrites.Select(r => $"{r.Key} ({r.Value})"))}"
            : $"# DS3 think rows the scripts check for ({ThinkCall}) kept as DS3's (DARKSIGN_ENEMY_THINK_IDS=ds3)";
        foreach (var (what, by) in Missing)
            yield return $"missing\t{what}\tneeded by {string.Join(", ", by)}";
        foreach (var (what, by) in Unknown)
            yield return $"unknown\t{what}\tnamed by {string.Join(", ", by.Take(8))}{(by.Count > 8 ? $" and {by.Count - 8} more" : "")}";
    }

    public string Summary =>
        $"AI: {Report.Count(l => !l.StartsWith('#'))} DS3 NpcThinkParam rows, {_battles.Values.Count(b => b.Er is not null)} of {_battles.Count} battle goals and " +
        $"{_logics.Values.Count(b => b.Er is not null)} of {_logics.Count} logics ported, {_libraries.Count} DS3 common files added, {Missing.Count} missing names";

    // ---- Params ----

    /// <summary>
    /// The NpcThinkParam rows: the template's row with DS3's values in the fields both games have under the same name
    /// and kind (whole numbers or decimals), and the ported battle goal and logic. Returns how many were added.
    /// </summary>
    public static int ApplyParams(PARAM erThink, PARAM? ds3Think, int templateThink, ChrAi ai, List<string>? notes = null)
    {
        if (erThink[templateThink] is not { } template)
        {
            notes?.Add($"c{ai.Ds3Chr:D4}: template NpcThinkParam {templateThink} is missing; the template's AI stays");
            return 0;
        }
        static string Norm(string s) => s.Replace("_", "").ToLowerInvariant();
        var targets = erThink.AppliedParamdef.Fields.Select((f, i) => (f, i)).GroupBy(x => Norm(x.f.InternalName)).ToDictionary(g => g.Key, g => g.First().i);
        int added = 0;
        foreach (var t in ai.Rows)
        {
            if (erThink[t.ErThink] is not null)
                continue;
            var row = new PARAM.Row(t.ErThink, template.Name ?? "", erThink.AppliedParamdef);
            for (int i = 0; i < row.Cells.Count; i++)
                row.Cells[i].Value = template.Cells[i].Value is byte[] b ? b.Clone() : template.Cells[i].Value;
            if (ds3Think?[t.Ds3Think] is { } source)
            {
                row.Name = $"[DS3] {source.Name}";
                foreach (var cell in source.Cells)
                {
                    var key = Norm(cell.Def.InternalName);
                    if (key is "logicid" or "battlegoalid" || key.StartsWith("pad") || key.Contains("reserve") || !targets.TryGetValue(key, out var at))
                        continue;
                    var to = row.Cells[at];
                    if (cell.Value is byte[] || to.Value is byte[] || IsDecimal(cell.Value) != IsDecimal(to.Value))
                        continue;
                    try { to.Value = Convert.ChangeType(cell.Value, to.Value.GetType()); }
                    catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException) { }
                }
            }
            if (t.Battle is { } battle)
                SetInt(row, "battleGoalID", battle);
            if (t.Logic is { } logic)
                SetInt(row, "logicId", logic);
            erThink.Rows.Add(row);
            added++;
        }
        erThink.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return added;
    }

    private static bool IsDecimal(object? v) => v is float or double;

    private static int IntCell(PARAM.Row row, string name) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value: { } v } ? Convert.ToInt32(v) : -1;

    private static void SetInt(PARAM.Row row, string name, int value)
    {
        if (row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value: { } v } cell)
            cell.Value = Convert.ChangeType(value, v.GetType());
    }
}
