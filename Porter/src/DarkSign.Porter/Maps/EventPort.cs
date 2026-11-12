using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Converts Dark Souls III event scripts (EMEVD) into Elden Ring ones: the doors, levers, elevators, warps, boss
/// fights and triggers of a map. Both games run the same event system, and DarkScript3 documents every instruction
/// of each by name, so the conversion is table-driven: each DS3 instruction is matched to the Elden Ring instruction
/// of the same name (or a known rename: "Object" became "Asset"), and its arguments are carried over by name, with
/// Elden Ring-only arguments at their defaults.
///
/// IDs move with the map: the map's entities, events and flags become the slot's (<see cref="MapId.MoveEntity"/>),
/// DS3's global flags get new Elden Ring flags (<see cref="FlagMap"/>), and warps to a ported DS3 map go to its slot.
///
/// What can't convert is handled so the script still runs: a command with no Elden Ring equivalent becomes a no-op
/// on the same line (so SKIP counts and labels stay right); an event whose conditions or flow can't convert is
/// left out, along with the lines that start it. A cutscene the cutscene port converted (<see cref="Cutscenes"/>)
/// plays under its Elden Ring ID; one it didn't is left out: when it warps it becomes the warp alone. The report
/// counts all of it.
/// </summary>
public sealed class EventPort
{
    /// <summary>
    /// What an instruction argument (or an event parameter) holds. WarpPoint: an entity the player is warped to or
    /// respawns at (moved like any entity; the map port makes each one a spawn point, <see cref="WarpPoints"/>).
    /// ActionButton: an ActionButtonParam row ID (DS3's rows get Elden Ring copies, <see cref="ActionButtons.RowForDs3"/>).
    /// </summary>
    public enum Kind { Other, Entity, Flag, FlagRangeStart, FlagRangeEnd, Event, Area, Block, PackedMap, Cutscene, SpEffect, WarpPoint, ActionButton, ObjAct }

    private readonly Emedf _ds3, _er;
    private readonly IReadOnlyList<(MapId Ds3, MapId Er)> _maps;
    private readonly FlagMap _flags;

    private readonly Dictionary<(int, int), Mapping?> _mappings = new();
    private readonly Dictionary<long, Dictionary<long, Kind>> _paramKinds = new();
    private readonly HashSet<long> _commonEvents = new();

    public Report Stats { get; } = new();

    /// <summary>
    /// The Elden Ring ID of a DS3 SpEffect, or null to keep the DS3 ID. A ported enemy's DS3 SpEffects live at
    /// 700,000,000 + the DS3 ID (<see cref="Enemies.ChrEffects.SpEffectId"/>), so "Set SpEffect", "Clear SpEffect" and
    /// "IF Character Has SpEffect" must name those rows (Pontiff's sword auras, the Jailer's eye glow swap); an ID
    /// with no ported row stays as it was. Null: every SpEffect ID stays (no regulation to look in).
    /// </summary>
    public Func<long, long?>? SpEffects { get; init; }

    /// <summary>
    /// A move that comes before the usual ones, or null to move the usual way: the boss kits
    /// (<see cref="BossKits"/>) send a boss's characters, flags and events to their own IDs.
    /// </summary>
    public Func<long, Kind, long?>? Override { get; init; }

    /// <summary>
    /// The Elden Ring ID of a DS3 cutscene the cutscene port converted (<see cref="CutscenePort"/>), or null when it
    /// wasn't. Null delegate: no cutscenes were converted, and every cutscene line is left out (a warping one keeps
    /// its warp), as before the cutscene port.
    /// </summary>
    public Func<long, long?>? Cutscenes { get; init; }

    /// <summary>Where DS3 action button prompts get their ActionButtonParam rows; without it they don't convert.</summary>
    public ActionButtons? Buttons { get; init; }

    // DS3's "IF Action Button" family (all start Result Condition Group, Target Entity Type, Target Entity ID, ...).
    private static readonly HashSet<int> ActionButtonIds = [5, 13, 18, 19];

    public sealed class Report
    {
        public int Events, EventsDropped, Instructions, Converted, NoOps;
        /// <summary>SpEffect IDs pointed at a ported row, and ones left at their DS3 ID (no ported row).</summary>
        public int SpEffectsMoved, SpEffectsKept;
        /// <summary>DS3 ActionButtonParam IDs pointed at their Elden Ring copies.</summary>
        public int ActionButtonsMoved;
        /// <summary>Cutscene lines that play a converted cutscene, and ones left out (or kept as their warp).</summary>
        public int CutscenesPlayed, CutscenesLeftOut;
        public SortedSet<long> CutscenesLeftOutIds { get; } = [];
        public SortedSet<long> SpEffectsKeptIds { get; } = [];
        public Dictionary<string, int> Unconverted { get; } = new();
        public List<string> Notes { get; } = [];

        public void Miss(string what) => Unconverted[what] = Unconverted.GetValueOrDefault(what) + 1;

        public override string ToString() =>
            $"{Events - EventsDropped}/{Events} events, {Converted}/{Instructions} instructions converted, {NoOps} no-ops"
            + (ActionButtonsMoved == 0 ? "" : $", {ActionButtonsMoved} DS3 action button rows pointed at their copies")
            + (SpEffectsMoved + SpEffectsKept == 0 ? "" : $", SpEffects: {SpEffectsMoved} to ported rows, {SpEffectsKept} kept"
                + (SpEffectsKeptIds.Count == 0 ? "" : $" ({string.Join(" ", SpEffectsKeptIds.Take(12))})"))
            + (CutscenesPlayed + CutscenesLeftOut == 0 ? "" : $", cutscenes: {CutscenesPlayed} lines play a ported cutscene, {CutscenesLeftOut} left out"
                + (CutscenesLeftOutIds.Count == 0 ? "" : $" ({string.Join(" ", CutscenesLeftOutIds.Take(12))})"))
            + (Unconverted.Count == 0 ? "" : "; not converted: " + string.Join(", ",
                Unconverted.OrderByDescending(u => u.Value).Take(12).Select(u => $"{u.Key} x{u.Value}")));
    }

    /// <param name="maps">Each ported DS3 map and the Elden Ring map it was ported to (first slot wins).</param>
    public EventPort(Emedf ds3, Emedf er, IReadOnlyList<(MapId Ds3, MapId Er)> maps, FlagMap flags)
    {
        _ds3 = ds3;
        _er = er;
        _maps = maps;
        _flags = flags;
    }

    private sealed record Mapping(Emedf.Instr From, Emedf.Instr To, int[] Source, Kind[] Kinds, bool WarpOnly);

    /// <summary>
    /// The Elden Ring instruction each DS3 cutscene instruction plays a converted cutscene with. Elden Ring has one
    /// "and Warp" form (Area Entity ID = DS3's Point Entity ID, Map ID = the slot), so DS3's time-period, ceremony and
    /// second-point variants become it; the ones that don't warp become "Play Cutscene to Player". What DS3 does on
    /// top (change the time period or ceremony, turn the player) has no Elden Ring form here and is left out.
    /// </summary>
    private static readonly Dictionary<string, string> CutsceneForms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Play Cutscene to All"] = "Play Cutscene To All",
        ["Play Cutscene to Player"] = "Play Cutscene to Player",
        ["Play Cutscene and Rotate Player Around Vertical Axis"] = "Play Cutscene to Player",
        ["Play Cutscene and Change Time Period"] = "Play Cutscene to Player",
        ["Play Cutscene to Player and Change Current Map Ceremony"] = "Play Cutscene to Player",
        ["Play Cutscene and Warp All Players"] = "Play Cutscene to Player and Warp",
        ["Play Cutscene and Warp Player"] = "Play Cutscene to Player and Warp",
        ["Play Cutscene, Change Time Period and Warp Player"] = "Play Cutscene to Player and Warp",
        ["Play Cutscene, Change Map Ceremony and Warp Player"] = "Play Cutscene to Player and Warp",
        ["Play Ongoing Cutscene and Warp Player"] = "Play Cutscene to Player and Warp",
        ["Play Cutscene and Warp Player + UNKNOWN 2002[12]"] = "Play Cutscene to Player and Warp",
    };

    private readonly Dictionary<(int, int), Mapping?> _cutsceneMappings = new();

    /// <summary>The mapping that plays the cutscene (<see cref="CutsceneForms"/>), or null for any other instruction.</summary>
    private Mapping? CutsceneMappingFor(int bank, int id)
    {
        if (_cutsceneMappings.TryGetValue((bank, id), out var cached))
            return cached;
        Mapping? m = null;
        if (_ds3.Get(bank, id) is { } from && CutsceneForms.TryGetValue(from.Name, out var toName) && _er.Get(toName) is { } to)
            m = Build(from, to, warpOnly: false, cutscene: true);
        return _cutsceneMappings[(bank, id)] = m;
    }

    /// <summary>
    /// DS3's Playback Method flags in Elden Ring's: bit 1 (unskippable) is the same; DS3's fade-out (8) is Elden
    /// Ring's fade-out (16). DarkScript3: DS3 0/2/8/10, Elden Ring 0/2/16/18 (Skippable With Fade-Out Skip).
    /// </summary>
    public static long PlaybackMethod(long ds3) => (ds3 & 2) | ((ds3 & 8) != 0 ? 16 : 0);

    // DS3 instructions whose Elden Ring counterpart has another name (checked by hand against both definitions).
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["(De)activate Object"] = "Change Asset Enable State",
        ["(De)activate Map Hit"] = "Change Map Hit Enable State",
        ["(De)activate Generator"] = "Set Generator State",
        ["Start Enemy Generator"] = "Invoke Enemy Generator",
        ["Set Temporary Player Respawn Point"] = "Set Player Respawn Point",
        ["(De)activate ObjAct (Assign IDx)"] = "Set ObjAct State (Assign IDx)",
        ["Display Decorated Boss Health Bar"] = "Display Boss Health Bar",
        ["Handle Boss Defeat"] = "Handle Boss Defeat and Display Banner",
        ["Change Character HitMask"] = "Change Character Hitmask",
        ["Request Animation Playback"] = "Force Animation Playback",
        ["Character Warp Request"] = "Issue Short Warp Request",
        ["Display Message"] = "Display Blinking Message",
        // DS3's online-world check is Elden Ring's world-type check: DS3's "Is Not In" (0 = in your own world, 1 =
        // in someone else's) holds the same values as Elden Ring's World Type (0 Own World, 1 Other World), and the
        // one remaining byte argument pairs with the other. Nearly every DS3 boss fight, door and NPC event asks it.
        ["IF Player Is Not In Own World (Excludes Arena)"] = "IF Player Is In World Type",
        ["GOTO IF Player Is Not In Own World (Excludes Arena)"] = "GOTO IF Player Is In World Type",
        ["END IF Player Is Not In Own World (Excludes Arena)"] = "END IF Player Is In World Type",
        // DS3 keeps both forms; Elden Ring only "IF Damage Type", whose Damage Type 0 (Unspecified, its default) is
        // any hit: what "Damaged By" asks. NPCs that turn hostile when hit and bosses woken by a hit wait on it.
        ["IF Character Damaged By"] = "IF Damage Type",
    };

    // DS3 "Number of Clients of Type" (SKIP 1003[14], GOTO 1003[15], END 1003[16]): Elden Ring only counts co-op
    // clients (1003[09], [109], [10]). Client type 0 (Co-op) becomes that; invaders (types 1 and 2) are never in a
    // ported world, so the check is decided here with a count of 0: an unconditional skip, goto or end, or a no-op.
    private static readonly Dictionary<int, (int CoopId, int AlwaysId)> ClientChecks = new()
    {
        [14] = (9, 3), [15] = (109, 103), [16] = (10, 4),
    };

    // DS3 cutscene instructions that also warp the player: ported as the warp alone until cutscenes are.
    private static readonly HashSet<string> CutsceneWarps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Play Cutscene and Warp Player", "Play Cutscene and Warp All Players", "Play Cutscene, Change Time Period and Warp Player",
        "Play Cutscene, Change Map Ceremony and Warp Player", "Play Ongoing Cutscene and Warp Player",
        "Play Cutscene and Warp Player + UNKNOWN 2002[12]", "[Dummy] Play Cutscene and Warp Player",
    };

    public static string Normalize(string name)
    {
        var n = name.ToLowerInvariant().Replace("objects", "assets").Replace("object", "asset").Replace("entity", " ");
        return string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string RenameRules(string name) =>
        name.Replace("Objects", "Assets").Replace("Object", "Asset").Replace("Hollow Arena", "Arena")
            .Replace("Player In/Outside Area", "In/Outside Area");

    /// <summary>What an argument holds, from its name in the instruction definitions.</summary>
    public static Kind KindOf(string argName)
    {
        var n = argName.ToLowerInvariant();
        if (n.Contains("cutscene id"))
            return Kind.Cutscene;
        if (n.Contains("action button param"))
            return Kind.ActionButton;
        // Where the player lands: Warp Player's "Initial Area (Entity) ID", the respawn point, and the point a
        // cutscene warps the player to (DS3's "Play Cutscene and Warp Player").
        if (n.Contains("initial area") || n.Contains("respawn point") || n.Contains("respawn area") || n == "point entity id")
            return Kind.WarpPoint;
        if (n.Contains("flag") && !n.Contains("state") && !n.Contains("type") && !n.Contains("bit") && !n.Contains("comparison")
            && !n.Contains("number") && !n.Contains("operation"))
            return n.Contains("start") || n.Contains("first") ? Kind.FlagRangeStart
                : n.Contains("end") || n.Contains("last") ? Kind.FlagRangeEnd
                : Kind.Flag;
        if (n == "event id" || n.EndsWith(" event id"))
            return Kind.Event;
        if (n == "area id")
            return Kind.Area;
        if (n == "block id")
            return Kind.Block;
        if (n == "speffect id")
            return Kind.SpEffect;
        // DS3 "Object Parameter ID" / Elden Ring "ObjAct Param ID" (Initialize ObjAct, Set ObjAct State): an
        // ObjActParam row, which the object port moves (ObjectPort.ObjActRow).
        if (n.Contains("objact param") || n == "object parameter id")
            return Kind.ObjAct;
        if (n == "map id")
            return Kind.PackedMap;
        if (n.Contains("entity"))
            return Kind.Entity;
        return Kind.Other;
    }

    private Mapping? MappingFor(int bank, int id)
    {
        if (_mappings.TryGetValue((bank, id), out var cached))
            return cached;
        Mapping? m = null;
        var from = _ds3.Get(bank, id);
        if (from is not null)
        {
            var warpOnly = CutsceneWarps.Contains(from.Name);
            var to = warpOnly ? _er.Get(2003, 14)
                : _er.Get(Renamed.GetValueOrDefault(from.Name) ?? from.Name) ?? _er.Get(RenameRules(from.Name));
            if (to is not null)
                m = Build(from, to, warpOnly);
        }
        return _mappings[(bank, id)] = m;
    }

    private static readonly HashSet<string> Trivial = ["id", "the", "of", "to", "type", "number", "target", "(s)", "(m)", "(deg)"];

    private static bool IsTypeArg(string normalized) => normalized.Split(' ').Contains("type");
    private static bool IsStateArg(string normalized) => normalized.Split(' ').Contains("state");

    private static HashSet<string> Tokens(string normalized) =>
        normalized.Replace("parameter", "param").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Trivial.Contains(t)).ToHashSet();

    /// <summary>
    /// Pairs each Elden Ring argument with the DS3 argument that fills it: same name first; then, for arguments that
    /// hold an ID of the same kind (entity, flag...), the only one of that kind left; then the best shared words
    /// ("Desired Flag State" / "State"). Arguments with no partner keep Elden Ring's default.
    /// </summary>
    private static Mapping Build(Emedf.Instr from, Emedf.Instr to, bool warpOnly, bool cutscene = false)
    {
        var used = new bool[from.Args.Count];
        var source = Enumerable.Repeat(-1, to.Args.Count).ToArray();
        var kinds = new Kind[to.Args.Count];
        string Want(int i)
        {
            var w = Normalize(to.Args[i].Name);
            // A warp's arrival point: DS3 says Point Entity ID; Elden Ring's warp and cutscene warp say Area (Entity) ID,
            // which without "entity" would read as DS3's Area ID (the map's area number).
            return warpOnly && w == "initial area id" || cutscene && to.Args[i].Name.Equals("Area Entity ID", StringComparison.OrdinalIgnoreCase) ? "point id" : w;
        }
        // 1. Same name.
        for (int i = 0; i < to.Args.Count; i++)
            for (int j = 0; j < from.Args.Count; j++)
                if (!used[j] && Normalize(from.Args[j].Name) == Want(i))
                {
                    source[i] = j;
                    used[j] = true;
                    break;
                }
        // 2. The only remaining argument of the same ID kind.
        for (int i = 0; i < to.Args.Count; i++)
        {
            if (source[i] >= 0)
                continue;
            var kind = KindOf(to.Args[i].Name);
            if (kind is Kind.Other or Kind.Area or Kind.Block or Kind.PackedMap)
                continue;
            var free = Enumerable.Range(0, from.Args.Count).Where(j => !used[j] && KindOf(from.Args[j].Name) == kind).ToList();
            var wanting = Enumerable.Range(0, to.Args.Count).Count(k => source[k] < 0 && KindOf(to.Args[k].Name) == kind);
            if (free.Count == 1 && wanting == 1)
            {
                source[i] = free[0];
                used[free[0]] = true;
            }
        }
        // 3. Most shared words, best pair first across all the arguments left (so "Desired Flag State" takes "Flag
        // State" before "Target Event Flag Type" can). A "type" argument never takes a "state" one, or the reverse.
        while (true)
        {
            int bi = -1, bj = -1, bestScore = 0;
            for (int i = 0; i < to.Args.Count; i++)
            {
                if (source[i] >= 0)
                    continue;
                var wantName = Want(i);
                var want = Tokens(wantName);
                for (int j = 0; j < from.Args.Count; j++)
                {
                    if (used[j] || Emedf.SizeOf(from.Args[j].Type) != Emedf.SizeOf(to.Args[i].Type) && from.Args[j].Type != 6 && to.Args[i].Type != 6)
                        continue;
                    var name = Normalize(from.Args[j].Name);
                    if (IsTypeArg(wantName) != IsTypeArg(name) && (IsStateArg(wantName) || IsStateArg(name)))
                        continue;
                    var score = Tokens(name).Intersect(want).Count();
                    if (score > bestScore)
                        (bi, bj, bestScore) = (i, j, score);
                }
            }
            if (bi < 0)
                break;
            source[bi] = bj;
            used[bj] = true;
        }
        // 4. One argument left on each side, of the same size ("CharInitParam ID" / "Character Param ID").
        var openTo = Enumerable.Range(0, to.Args.Count).Where(i => source[i] < 0).ToList();
        var openFrom = Enumerable.Range(0, from.Args.Count).Where(j => !used[j]).ToList();
        if (openTo.Count == 1 && openFrom.Count == 1 && from.Args[openFrom[0]].Type == to.Args[openTo[0]].Type)
        {
            source[openTo[0]] = openFrom[0];
            used[openFrom[0]] = true;
        }
        for (int i = 0; i < to.Args.Count; i++)
        {
            kinds[i] = KindOf(to.Args[i].Name);
            if (source[i] >= 0 && kinds[i] == Kind.Other)
                kinds[i] = KindOf(from.Args[source[i]].Name);
        }
        return new Mapping(from, to, source, kinds, warpOnly);
    }

    /// <summary>
    /// Learns what each event parameter holds (an entity, a flag...) from the instruction arguments it fills, so the
    /// values passed where the event is started can be moved the same way. Call for every script first.
    /// </summary>
    public void Learn(EMEVD script, bool common = false)
    {
        foreach (var e in script.Events)
        {
            if (common)
                _commonEvents.Add(e.ID);
            var kinds = _paramKinds.TryGetValue(e.ID, out var k) ? k : _paramKinds[e.ID] = new();
            foreach (var p in e.Parameters)
            {
                if (p.InstructionIndex < 0 || p.InstructionIndex >= e.Instructions.Count)
                    continue;
                var ins = e.Instructions[(int)p.InstructionIndex];
                var def = _ds3.Get(ins.Bank, ins.ID);
                var arg = def?.ArgAt(p.TargetStartByte) ?? -1;
                if (arg >= 0)
                    kinds.TryAdd(p.SourceStartByte, KindOf(def!.Args[arg].Name));
            }
            // Flag ranges are placed first, so they stay consecutive.
            foreach (var ins in e.Instructions)
                if (_ds3.Get(ins.Bank, ins.ID) is { } def)
                {
                    var start = def.Args.FirstOrDefault(a => KindOf(a.Name) == Kind.FlagRangeStart);
                    var end = def.Args.FirstOrDefault(a => KindOf(a.Name) == Kind.FlagRangeEnd);
                    if (start is not null && end is not null)
                    {
                        long s = Emedf.Read(ins.ArgData, start), en = Emedf.Read(ins.ArgData, end);
                        if (s > 0 && en >= s && !OwnedByPortedMap(s))
                            _flags.Range(s, en);
                        else if (s > 0 && en >= s && _maps.First(m => m.Ds3.OwnsDs3Id(s)) is var (rd, re) && !MapFlags.RangeKept(rd, re, s, en))
                            _flags.LocalRange(s, en);
                    }
                }
        }
    }

    private bool OwnedByPortedMap(long id) => _maps.Any(m => m.Ds3.OwnsDs3Id(id));

    /// <summary>A DS3 ID moved into Elden Ring by what it is.</summary>
    public long Move(long value, Kind kind)
    {
        if (value <= 0)
            return value;
        if (Override?.Invoke(value, kind) is { } over)
            return over;
        switch (kind)
        {
            case Kind.Entity:
            case Kind.WarpPoint:
            case Kind.Event:
                foreach (var (ds3, er) in _maps)
                    if (ds3.OwnsDs3Id(value))
                        return ds3.MoveEntity((int)value, er);
                if (kind == Kind.Event && _commonEvents.Contains(value))
                    return _flags.Event(value);
                return value;
            case Kind.Flag:
            case Kind.FlagRangeStart:
            case Kind.FlagRangeEnd:
                foreach (var (ds3, er) in _maps)
                    if (ds3.OwnsDs3Id(value))
                        return MapFlags.Move(_flags, ds3, er, value);
                return _flags.Flag(value);
            case Kind.ActionButton:
                // DS3's ActionButtonParam rows mean other prompts (or nothing) in Elden Ring: each gets a copy.
                if (Buttons?.RowForDs3((int)Math.Min(value, int.MaxValue)) is { } row)
                {
                    Stats.ActionButtonsMoved++;
                    return row;
                }
                return value;
            case Kind.Cutscene:
                return Cutscenes?.Invoke(value) ?? value;
            case Kind.SpEffect:
                if (SpEffects?.Invoke(value) is { } moved)
                {
                    Stats.SpEffectsMoved++;
                    return moved;
                }
                Stats.SpEffectsKept++;
                Stats.SpEffectsKeptIds.Add(value);
                return value;
            case Kind.ObjAct:
                return ObjectPort.ObjActRow(value);
            default:
                return value;
        }
    }

    /// <summary>
    /// Every DS3 SpEffect ID these scripts set, clear or check: written in an instruction, or passed to an event
    /// that uses it as one (call <see cref="Learn"/> for every script first). The enemy port ports these rows too,
    /// so a script's effect exists at its new ID even when no enemy move casts it.
    /// </summary>
    public SortedSet<int> ScriptSpEffects(IEnumerable<EMEVD> scripts) => ScriptIds(scripts, Kind.SpEffect);

    /// <summary>
    /// Every DS3 entity these scripts warp the player to or make a respawn point (DS3 IDs; call <see cref="Learn"/>
    /// first). In DS3 most of them are map regions (Vordt's arena point 3002810, Friede's 4502810); Elden Ring warps
    /// land only on player parts, so the map port adds one at each (<see cref="RegionPort.AddWarpPoints"/>). A warp
    /// to a region drops the player at the map's default start instead (Devin's 1.0.383 test: Vordt's door).
    /// </summary>
    public SortedSet<int> WarpPoints(IEnumerable<EMEVD> scripts) => ScriptIds(scripts, Kind.WarpPoint);

    /// <summary>The DS3 values of one kind these scripts use: written in an instruction or passed to an event.</summary>
    public SortedSet<int> ScriptIds(IEnumerable<EMEVD> scripts, Kind wanted)
    {
        var ids = new SortedSet<int>();
        void Add(long v)
        {
            if (v is > 0 and < int.MaxValue)
                ids.Add((int)v);
        }
        foreach (var e in scripts.SelectMany(s => s.Events))
            for (int i = 0; i < e.Instructions.Count; i++)
            {
                var ins = e.Instructions[i];
                if (ins.Bank == 2000 && ins.ID is 0 or 6)
                {
                    int head = ins.ID == 0 ? 8 : 4;
                    if (ins.ArgData.Length < head)
                        continue;
                    var callee = BitConverter.ToUInt32(ins.ArgData, head - 4);
                    if (_paramKinds.TryGetValue(callee, out var kinds))
                        foreach (var (source, kind) in kinds)
                            if (kind == wanted && head + source + 4 <= ins.ArgData.Length)
                                Add(BitConverter.ToInt32(ins.ArgData, head + (int)source));
                    continue;
                }
                if (_ds3.Get(ins.Bank, ins.ID) is not { } def)
                    continue;
                foreach (var arg in def.Args)
                    if (KindOf(arg.Name) == wanted
                        && !e.Parameters.Any(p => p.InstructionIndex == i && p.TargetStartByte == arg.Offset))
                        Add(Emedf.Read(ins.ArgData, arg));
            }
        return ids;
    }

    private MapId? SlotFor(long area, long block) =>
        _maps.FirstOrDefault(m => m.Ds3.Area == area && m.Ds3.Block == block) is var (ds3, er) && ds3.Area == area && ds3.Block == block
            ? er : null;

    /// <summary>Converts one script. <paramref name="eventId"/> moves the IDs of the script's own events.</summary>
    public EMEVD Convert(EMEVD ds3, EMEVD.Game format, Func<long, long> eventId)
    {
        var er = new EMEVD(format);
        var dropped = new HashSet<long>();
        foreach (var e in ds3.Events)
        {
            Stats.Events++;
            var converted = ConvertEvent(e, eventId);
            if (converted is null)
            {
                dropped.Add(e.ID);
                Stats.EventsDropped++;
                continue;
            }
            er.Events.Add(converted);
        }
        // Lines that start a dropped event would start nothing; make them no-ops.
        DisableCalls(er, dropped.Select(eventId).ToHashSet());
        return er;
    }

    /// <summary>An Elden Ring instruction that does nothing (wait 0 seconds), to keep a line in place.</summary>
    public static EMEVD.Instruction NoOp() => RunEvents.Make(1001, 0, 0f);

    /// <summary>
    /// Turns lines that start any of these (Elden Ring) events into no-ops: InitializeEvent and
    /// InitializeCommonEvent both hold the event ID after the slot. Returns how many lines changed.
    /// </summary>
    public static int DisableCalls(EMEVD script, IReadOnlySet<long> eventIds)
    {
        int count = 0;
        foreach (var e in script.Events)
            for (int i = 0; i < e.Instructions.Count; i++)
            {
                var ins = e.Instructions[i];
                if (ins.Bank == 2000 && ins.ID is 0 or 6 && ins.ArgData.Length >= 8 && eventIds.Contains(BitConverter.ToUInt32(ins.ArgData, 4)))
                {
                    e.Instructions[i] = NoOp();
                    e.Parameters.RemoveAll(p => p.InstructionIndex == i);
                    count++;
                }
            }
        return count;
    }

    private EMEVD.Event? ConvertEvent(EMEVD.Event e, Func<long, long> eventId)
    {
        var result = new EMEVD.Event(eventId(e.ID), e.RestBehavior);
        var paramMap = new Dictionary<int, (Emedf.Instr? From, Mapping? Map)>();
        for (int i = 0; i < e.Instructions.Count; i++)
        {
            var ins = e.Instructions[i];
            Stats.Instructions++;
            var (converted, map) = ConvertInstruction(ins, e.Parameters.Where(p => p.InstructionIndex == i).Select(p => p.TargetStartByte).ToHashSet());
            if (converted is null)
            {
                var name = _ds3.Get(ins.Bank, ins.ID)?.Name ?? $"{ins.Bank}[{ins.ID}]";
                Stats.Miss(name);
                // Conditions and flow decide what the event does; without them it would do something else. A
                // condition Elden Ring has no form of that only narrows a group which other conditions also fill
                // counts as met (AssumedMet: Pontiff's phase 2 waits for "not in a draw group", 13705880).
                if (ins.Bank < 2000 && !(AssumedMet.Contains(name) && SharesGroup(e, i)))
                    return null;
                converted = NoOp();
                Stats.NoOps++;
            }
            else
                Stats.Converted++;
            result.Instructions.Add(converted);
            paramMap[i] = (_ds3.Get(ins.Bank, ins.ID), map);
        }

        foreach (var p in e.Parameters)
        {
            if (!paramMap.TryGetValue((int)p.InstructionIndex, out var pm))
                continue;
            var target = result.Instructions[(int)p.InstructionIndex];
            if (target.Bank == 1001 && target.ID == 0 && (pm.From?.Bank != 1001 || pm.From?.Id != 0))
                continue; // a no-op now
            long targetByte = p.TargetStartByte;
            int byteCount = p.ByteCount;
            if (target.Bank == 2000 && target.ID == 6 && pm.From?.Bank == 2000)
                targetByte += 4; // Elden Ring's InitializeCommonEvent has a slot argument first
            if (pm.Map is { } m)
            {
                var fromArg = m.From.ArgAt(p.TargetStartByte);
                var toArg = fromArg < 0 ? -1 : Array.IndexOf(m.Source, fromArg);
                if (toArg < 0)
                {
                    Stats.Notes.Add($"event {e.ID}: a parameter of {m.From.Name} has no place in {m.To.Name}");
                    continue;
                }
                targetByte = m.To.Args[toArg].Offset;
                byteCount = Math.Min(byteCount, m.To.Args[toArg].Size);
            }
            result.Parameters.Add(new EMEVD.Parameter(p.InstructionIndex, targetByte, p.SourceStartByte, byteCount));
        }
        return result;
    }

    /// <summary>
    /// DS3 conditions Elden Ring has no form of that only narrow a group: "IF Character DrawGroup State" waits until
    /// a character is (not) in a draw group, which Elden Ring scripts don't ask. DS3's Pontiff waits for it next to
    /// the event message that starts his second phase; without the event his phase change never happened.
    /// </summary>
    private static readonly HashSet<string> AssumedMet = new(StringComparer.OrdinalIgnoreCase) { "IF Character DrawGroup State" };

    /// <summary>Whether a condition (first argument: its group) adds to a group another condition of the event also fills.</summary>
    private static bool SharesGroup(EMEVD.Event e, int index)
    {
        var ins = e.Instructions[index];
        if (ins.ArgData.Length < 1 || e.Parameters.Any(p => p.InstructionIndex == index && p.TargetStartByte == 0))
            return false;
        var group = (sbyte)ins.ArgData[0];
        if (group == 0)
            return false;
        for (int k = 0; k < e.Instructions.Count; k++)
        {
            var other = e.Instructions[k];
            // Bank 0-4 conditions start with their result group; 0[00] adds a whole group.
            if (k != index && other.Bank is >= 0 and <= 5 && other.ArgData.Length > 0 && (sbyte)other.ArgData[0] == group)
                return true;
        }
        return false;
    }

    /// <summary>One instruction in Elden Ring's form, or null when it has none.</summary>
    private (EMEVD.Instruction? Instruction, Mapping? Map) ConvertInstruction(EMEVD.Instruction ins, IReadOnlySet<long> paramBytes)
    {
        bool parameterized = paramBytes.Count > 0;
        // Starting events: the event ID moves, and so does each value passed in, by what the event uses it for.
        // DS3's InitializeCommonEvent has no slot argument; Elden Ring's does, so its arguments shift by 4 bytes.
        if (ins.Bank == 2000 && (ins.ID == 0 && ins.ArgData.Length >= 8 || ins.ID == 6 && ins.ArgData.Length >= 4))
        {
            var data = ins.ID == 6 ? [0, 0, 0, 0, .. ins.ArgData] : (byte[])ins.ArgData.Clone();
            var callee = BitConverter.ToUInt32(data, 4);
            BitConverter.TryWriteBytes(data.AsSpan(4), (uint)Move(callee, Kind.Event));
            if (_paramKinds.TryGetValue(callee, out var kinds))
            {
                foreach (var (source, kind) in kinds)
                {
                    var offset = 8 + (int)source;
                    if (kind is Kind.Area or Kind.Block || offset + 4 > data.Length)
                        continue;
                    BitConverter.TryWriteBytes(data.AsSpan(offset), (int)Move(BitConverter.ToInt32(data, offset), kind));
                }
                // A map passed in as area and block bytes moves as a pair.
                var areaAt = kinds.FirstOrDefault(k => k.Value == Kind.Area).Key;
                var blockAt = kinds.FirstOrDefault(k => k.Value == Kind.Block).Key;
                if (kinds.ContainsValue(Kind.Area) && kinds.ContainsValue(Kind.Block) && 8 + Math.Max(areaAt, blockAt) < data.Length
                    && SlotFor(data[8 + areaAt], data[8 + blockAt]) is { } passedSlot)
                {
                    data[8 + areaAt] = (byte)passedSlot.Area;
                    data[8 + blockAt] = (byte)passedSlot.Block;
                }
            }
            return (new EMEVD.Instruction(2000, ins.ID, data), null);
        }
        if (ins.Bank == 3 && ActionButtonIds.Contains(ins.ID))
            return ActionButton(ins);
        if (ins.Bank == 1003 && ClientChecks.ContainsKey(ins.ID))
            return ClientCheck(ins, parameterized);
        // "WAIT For Network Approval": nothing to wait for outside an online session.
        if (ins.Bank == 1000 && ins.ID == 9)
            return (NoOp(), null);
        // Same layout in both games and nothing to move (waits, labels, condition groups, plain flow).
        Mapping? map;
        if (ins.Bank == 2002 && _ds3.Get(ins.Bank, ins.ID) is { } cutsceneDef && CutsceneForms.ContainsKey(cutsceneDef.Name))
        {
            var (cutsceneMap, keep) = CutsceneLine(ins, cutsceneDef, paramBytes);
            if (!keep)
                return (NoOp(), null);
            map = cutsceneMap;
        }
        else
            map = MappingFor(ins.Bank, ins.ID);
        if (map is null)
            return (null, null);

        var from = map.From;
        var to = map.To;
        var output = Emedf.Defaults(to);
        long area = -1, block = -1;
        for (int i = 0; i < from.Args.Count; i++)
        {
            if (KindOf(from.Args[i].Name) == Kind.Area) area = Emedf.Read(ins.ArgData, from.Args[i]);
            if (KindOf(from.Args[i].Name) == Kind.Block) block = Emedf.Read(ins.ArgData, from.Args[i]);
        }
        MapId? slot = null;
        if (area >= 0 && block >= 0)
        {
            slot = SlotFor(area, block);
            if (slot is null && !parameterized)
                return (null, map); // a warp or check on a DS3 map that has no slot
        }

        for (int i = 0; i < to.Args.Count; i++)
        {
            var arg = to.Args[i];
            long value;
            switch (map.Kinds[i])
            {
                case Kind.Area when slot is { } s: value = s.Area; break;
                case Kind.Block when slot is { } s: value = s.Block; break;
                case Kind.PackedMap when slot is { } s: value = s.Area * 1_000_000L + s.Block * 10_000L + s.Region * 100L + s.Index; break;
                default:
                    if (arg.Name.Equals("Region ID", StringComparison.OrdinalIgnoreCase) && slot is { } r) { value = r.Region; break; }
                    if (arg.Name.Equals("Index ID", StringComparison.OrdinalIgnoreCase) && slot is { } x) { value = x.Index; break; }
                    if (map.Source[i] < 0)
                        continue; // Elden Ring-only: stays at its default
                    var src = from.Args[map.Source[i]];
                    value = Emedf.Read(ins.ArgData, src);
                    if (src.Type == 6 && arg.Type != 6)
                        value = (long)BitConverter.Int32BitsToSingle((int)value);
                    else if (src.Type != 6 && arg.Type == 6)
                        value = BitConverter.SingleToInt32Bits(value);
                    value = Move(value, map.Kinds[i]);
                    break;
            }
            if (to.Bank == 2002 && arg.Name.Equals("Playback Method", StringComparison.OrdinalIgnoreCase) && map.Source[i] >= 0
                && !paramBytes.Contains(from.Args[map.Source[i]].Offset))
                value = PlaybackMethod(value);
            Emedf.Write(output, arg, value);
        }
        return (new EMEVD.Instruction(to.Bank, to.Id, output), map);
    }

    /// <summary>
    /// How a DS3 cutscene line converts: the cutscene form (<see cref="CutsceneForms"/>) when its cutscene was converted
    /// (or is passed in, so the caller's value moves), else the warp alone for a warping line (Keep, with the warp
    /// mapping) or nothing (Keep false: a no-op).
    /// </summary>
    private (Mapping? Map, bool Keep) CutsceneLine(EMEVD.Instruction ins, Emedf.Instr def, IReadOnlySet<long> paramBytes)
    {
        var idArg = def.Args.FirstOrDefault(a => KindOf(a.Name) == Kind.Cutscene);
        bool passedIn = idArg is not null && paramBytes.Contains(idArg.Offset);
        long id = idArg is null ? 0 : Emedf.Read(ins.ArgData, idArg);
        if (Cutscenes is not null && (passedIn || Cutscenes(id) is not null) && CutsceneMappingFor(ins.Bank, ins.ID) is { } play)
        {
            Stats.CutscenesPlayed++;
            return (play, true);
        }
        Stats.CutscenesLeftOut++;
        if (!passedIn && id > 0)
            Stats.CutscenesLeftOutIds.Add(id);
        return CutsceneWarps.Contains(def.Name) ? (MappingFor(ins.Bank, ins.ID), true) : (null, false);
    }

    /// <summary>
    /// DS3 "IF Action Button ..." -> Elden Ring "IF Action Button in Area" with an ActionButtonParam row that holds
    /// the prompt's shape and text. The condition group and target entity carry over (and so do parameters for them).
    /// </summary>
    private (EMEVD.Instruction?, Mapping?) ActionButton(EMEVD.Instruction ins)
    {
        var from = _ds3.Get(ins.Bank, ins.ID);
        var to = _er.Get(3, 24);
        if (Buttons is null || from is null || to is null || to.Args.Count < 3)
            return (null, null);
        Emedf.Arg? Find(string name) => from.Args.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        long Value(string name, long fallback) => Find(name) is { } a ? Emedf.Read(ins.ArgData, a) : fallback;
        float Float(string name, float fallback) => Find(name) is { } a ? BitConverter.Int32BitsToSingle((int)Emedf.Read(ins.ArgData, a)) : fallback;

        var button = new ActionButtons.Button(
            (int)Value("Dummypoly ID", -1), Float("Reaction Distance", 1.5f), (int)MathF.Round(Float("Reaction Angle (deg)", 180f)),
            (int)Value("Help Message ID", -1), from.Name.Contains("Boss", StringComparison.OrdinalIgnoreCase));
        var row = Buttons.RowFor(button);
        var group = from.Args.ToList().FindIndex(a => a.Name == "Result Condition Group");
        var entity = from.Args.ToList().FindIndex(a => a.Name == "Target Entity ID");
        if (group < 0 || entity < 0)
            return (null, null);
        var output = Emedf.Defaults(to);
        Emedf.Write(output, to.Args[0], Emedf.Read(ins.ArgData, from.Args[group]));
        Emedf.Write(output, to.Args[1], row);
        Emedf.Write(output, to.Args[2], Move(Emedf.Read(ins.ArgData, from.Args[entity]), Kind.Entity));
        return (new EMEVD.Instruction(to.Bank, to.Id, output),
            new Mapping(from, to, [group, -1, entity], [Kind.Other, Kind.Other, Kind.Entity], false));
    }

    /// <summary>
    /// DS3 "SKIP/GOTO/END IF Number of Clients of Type" (first argument: lines, label or end type; then Client Type,
    /// Comparison Type, Target Number of Clients). See <see cref="ClientChecks"/>.
    /// </summary>
    private (EMEVD.Instruction?, Mapping?) ClientCheck(EMEVD.Instruction ins, bool parameterized)
    {
        var (coopId, alwaysId) = ClientChecks[ins.ID];
        if (ins.ArgData.Length < 4)
            return (null, null);
        byte first = ins.ArgData[0], clientType = ins.ArgData[1], comparison = ins.ArgData[2], count = ins.ArgData[3];
        if (parameterized)
            return (null, null); // a value passed in decides it: can't be settled here
        if (clientType == 0)
            return (RunEvents.Make(1003, coopId, first, comparison, count), null);
        bool holds = comparison switch
        {
            0 => count == 0, 1 => count != 0, 2 => false, 3 => count > 0, 4 => count == 0, 5 => true, _ => false,
        };
        return (holds ? RunEvents.Make(1000, alwaysId, first) : NoOp(), null);
    }

    /// <summary>`event-coverage [ds3.emedf.json er.emedf.json]`: which DS3 instructions convert, and to what.</summary>
    public static int CoverageReport(string ds3Json, string erJson)
    {
        var port = new EventPort(Emedf.Parse(File.ReadAllText(ds3Json)), Emedf.Parse(File.ReadAllText(erJson)), [], new FlagMap());
        var missing = new List<string>();
        foreach (var i in port._ds3.All.OrderBy(i => i.Bank).ThenBy(i => i.Id))
        {
            var m = port.MappingFor(i.Bank, i.Id);
            if (m is null)
                missing.Add(i.ToString());
            else if (m.From.Name != m.To.Name || m.Source.Any(x => x < 0) || m.Source.Length != m.From.Args.Count)
                Console.WriteLine($"{i} -> {m.To}: {string.Join(", ", m.To.Args.Select((a, k) => m.Source[k] < 0 ? $"{a.Name}=default" : $"{a.Name}<-{m.From.Args[m.Source[k]].Name}"))}");
        }
        var (mapped, total) = port.Coverage();
        Console.WriteLine($"no Elden Ring form ({missing.Count}): {string.Join("; ", missing)}");
        Console.WriteLine($"event-coverage: {mapped} of {total} DS3 instructions convert");
        return 0;
    }

    /// <summary>How many DS3 instructions have an Elden Ring form, for the report.</summary>
    public (int Mapped, int Total) Coverage()
    {
        int mapped = 0, total = 0;
        foreach (var i in _ds3.All)
        {
            total++;
            if (MappingFor(i.Bank, i.Id) is not null)
                mapped++;
        }
        return (mapped, total);
    }
}
