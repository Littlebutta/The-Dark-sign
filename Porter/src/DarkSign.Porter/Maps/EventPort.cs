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
    /// <remarks>MusicRegion: a value an event uses only as the region of DS3's music lines (the shared boss events'
    /// track regions). The lines that start the event pass the region's switch flag there instead
    /// (<see cref="MapMusic.Plan.SwitchFor"/>), and the event's music lines set that flag.</remarks>
    public enum Kind { Other, Entity, Flag, FlagRangeStart, FlagRangeEnd, Event, Area, Block, PackedMap, Cutscene, SpEffect, WarpPoint, ActionButton, ObjAct, MusicRegion, Message, PlaceName, NpcName, ItemLot, Think, Behavior, Sfx, Camera }

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

    /// <summary>
    /// DS3's music lines (<see cref="MapMusic"/>): a line that switches a music region sets its flag. Null: they stay
    /// no-ops (Elden Ring has none of DS3's music instructions).
    /// </summary>
    public MapMusic.Plan? Music { get; init; }

    /// <summary>The DS3 map whose script is being converted (null: common_func), for the music lines that name a track by ID.</summary>
    public MapId? MusicMap { get; set; }

    /// <summary>Where DS3 action button prompts get their ActionButtonParam rows; without it they don't convert.</summary>
    public ActionButtons? Buttons { get; init; }

    /// <summary>
    /// Collects the DS3 text numbers the scripts name (<see cref="MapText"/>). The numbers move with or without it:
    /// a text's Elden Ring number follows from its DS3 number alone.
    /// </summary>
    public MapText.Used? Texts { get; init; }

    /// <summary>Where item lots, AI rows and items move (<see cref="Ds3Ids"/>); null: the run's (<see cref="Ds3Ids.Current"/>).</summary>
    public Ds3Ids? Ids { get; init; }

    // DS3's "IF Action Button" family (all start Result Condition Group, Target Entity Type, Target Entity ID, ...).
    private static readonly HashSet<int> ActionButtonIds = [5, 13, 18, 19];

    public sealed class Report
    {
        public int Events, EventsDropped, Instructions, Converted, NoOps;
        /// <summary>SpEffect IDs pointed at a ported row, and ones left at their DS3 ID (no ported row).</summary>
        public int SpEffectsMoved, SpEffectsKept;
        /// <summary>DS3 ActionButtonParam IDs pointed at their Elden Ring copies.</summary>
        public int ActionButtonsMoved;
        /// <summary>Map events started in several slots whose run-once flags were placed together in the saved ranges.</summary>
        public int EventSlotRanges;
        /// <summary>
        /// Range lines whose Elden Ring flags aren't the DS3 range's flags one after another (<see cref="PlaceRanges"/>
        /// couldn't place them together). One wider than a flag block is cut to the DS3 range's length, so a line never
        /// sets or checks flags across blocks (1.0.630's million-flag line closed the game).
        /// </summary>
        public int BrokenRanges;
        /// <summary>Event message and area name numbers pointed at DS3's own text (<see cref="MapText"/>).</summary>
        public int TextsMoved;
        /// <summary>Item lots, AI rows and items pointed at their Elden Ring rows (<see cref="Ds3Ids"/>), and ones left as DS3's.</summary>
        public int IdsMoved, IdsKept;
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
            + (TextsMoved == 0 ? "" : $", {TextsMoved} messages and area names pointed at DS3's text")
            + (IdsMoved + IdsKept == 0 ? "" : $", item lots, AI rows, items, behaviours, effects and cameras: {IdsMoved} to ported rows, {IdsKept} kept")
            + (SpEffectsMoved + SpEffectsKept == 0 ? "" : $", SpEffects: {SpEffectsMoved} to ported rows, {SpEffectsKept} kept"
                + (SpEffectsKeptIds.Count == 0 ? "" : $" ({string.Join(" ", SpEffectsKeptIds.Take(12))})"))
            + (CutscenesPlayed + CutscenesLeftOut == 0 ? "" : $", cutscenes: {CutscenesPlayed} lines play a ported cutscene, {CutscenesLeftOut} left out"
                + (CutscenesLeftOutIds.Count == 0 ? "" : $" ({string.Join(" ", CutscenesLeftOutIds.Take(12))})"))
            + (BrokenRanges == 0 ? "" : $", {BrokenRanges} flag range lines NOT consecutive")
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
    /// DS3's Playback Method flags in Elden Ring's: bit 1 (unskippable) is the same; DS3's fade-out (8) is left out.
    /// DarkScript3: DS3 0/2/8/10, Elden Ring 0/2/16/18. Up to 1.0.47x DS3's 8 became Elden Ring's 16, and the Dancer's
    /// intro (the one boss intro DS3 plays with 8 for the host) left the screen black when it ended: DS3 fades back
    /// in by itself after the cutscene's warp, Elden Ring stays faded out until the script does something about it.
    /// Elden Ring's own scripts (14 read on 2026-11-19) use 16 only where the script then moves the player far away
    /// or loads another map, never with a boss intro, which they all play with 0.
    /// </summary>
    public static long PlaybackMethod(long ds3) => ds3 & 2;

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
    // clients (1003[09], [109], [10]). Client type 0 (Co-op) becomes that. Type 1 is the host (see HostCount), always
    // one in a world the player owns; type 2 is never in a ported world. Type 1 compared equal to 0 (DS3's only form) becomes
    // Elden Ring's host check (HostChecks); anything else is decided here: an unconditional skip, goto or end, or a no-op.
    private static readonly Dictionary<int, (int CoopId, int AlwaysId)> ClientChecks = new()
    {
        [14] = (9, 3), [15] = (109, 103), [16] = (10, 4),
    };

    // DS3 SKIP/GOTO/END IF Number of Clients of Type (14/15/16) -> Elden Ring SKIP/GOTO/END IF Map Has Permission
    // To Update (203/204/205), for DS3's host-only lines (see ClientCheck).
    private static readonly Dictionary<int, int> HostChecks = new() { [14] = 203, [15] = 204, [16] = 205 };

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
        // Text by number (MapText): the "Message ID" of the dialog, status and message lines is event text; DS3's
        // "Area Name Message ID" (Display Area Welcome Message) is a place name. Not "Help Message ID" (an action
        // button's prompt: ActionButtons) or "Event Message ID" (a message a character carries, not text).
        if (n == "message id")
            return Kind.Message;
        if (n == "area name message id")
            return Kind.PlaceName;
        // A boss health bar's name (Display Boss Health Bar): an NpcName number (MapText.Name).
        if (n == "name id")
            return Kind.NpcName;
        // Lots the scripts award (Award Item Lot, Spawn Snuggly Item, Initialize Crow Trade) and the NpcThinkParam row
        // "Set Character AI ID" switches a character to: ported rows (Ds3Ids). Item IDs move with their type (MoveItem).
        if (n == "item lot id")
            return Kind.ItemLot;
        if (n == "ai id")
            return Kind.Think;
        // The behaviour row "Shoot Bullet" and "Create Damaging Object/Asset" fire, by row ID (AttackParamPort.PortScriptRow).
        if (n == "behavior id")
            return Kind.Behavior;
        // An effect the script spawns by ID ("Spawn Oneshot SFX", "Create Object-following SFX"): converted by
        // port-weapons into the common binder with the map effects (MapSfx table, Ds3Ids.Sfx).
        if (n == "sfx id")
            return Kind.Sfx;
        // "Change Camera": LockCamParam rows (Enemies.LockCams).
        if (n is "normal camera id" or "locked camera id")
            return Kind.Camera;
        // Where the player lands: Warp Player's "Initial Area (Entity) ID", the respawn point, and the point a
        // cutscene warps the player to (DS3's "Play Cutscene and Warp Player").
        // "IF ObjAct Event Flag" (5[02], both games) names the ObjAct map event by its entity ID (DS3's lever
        // events wait on 3004202, the ObjAct's own entity; its flag field is 0 or another flag), not by a flag.
        // Moved as a flag it only matched the moved ObjAct while the slot kept the map's flag block: in the
        // Painted World slot (m19_70 keeps none) every lever and lever door waited on a flag nothing set, so
        // the Ariandel village lift and the doors and gates its levers open never moved (2026-11-20).
        if (n == "objact event flag")
            return Kind.Entity;
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
            // Elden Ring calls an area name a "Message ID" too: what DS3 says the value is decides.
            if (source[i] >= 0 && (kinds[i] == Kind.Other || KindOf(from.Args[source[i]].Name) == Kind.PlaceName))
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
                if (arg < 0)
                    continue;
                var kind = KindOf(def!.Args[arg].Name);
                // A region passed to DS3's music lines (the 2010[07] that readies the next phase's track included).
                if (Music is not null && ins.Bank == 2010 && ins.ID is MapMusic.SetMapSoundState or MapMusic.EnableBossMapSound or MapMusic.EnableMapSoundWithFade or 7
                    && def.Args[arg].Name.Equals("Entity ID", StringComparison.OrdinalIgnoreCase))
                    kind = Kind.MusicRegion;
                // A use that says nothing about what the value is (a comparison's side, a count) gives way to one that
                // does, in whichever order they come: DS3's 20005800 first asks "X24 == 0?" (1000[105]) and only then
                // reads X24 as the first-encounter flag, so up to 1.0.640 every boss's first-encounter flag (and
                // 20005800's arena area X28) was passed on in DS3's numbering, which nothing in the slot sets, and the
                // entry flag the fight's music waits for never turned on (Darkeater Midir, Vordt, Friede...).
                if (kind != Kind.Other && kinds.TryGetValue(p.SourceStartByte, out var had) && had == Kind.Other)
                    kinds[p.SourceStartByte] = kind;
                else if (!kinds.TryAdd(p.SourceStartByte, kind) && kinds[p.SourceStartByte] != kind
                    && (kind == Kind.MusicRegion || kinds[p.SourceStartByte] == Kind.MusicRegion))
                {
                    // Used as a music region and as something else: it moves as the something else, and the music
                    // lines it reaches stay no-ops.
                    kinds[p.SourceStartByte] = kind == Kind.MusicRegion ? kinds[p.SourceStartByte] : kind;
                    Stats.Notes.Add($"event {e.ID}: value {p.SourceStartByte} is a music region and a {kinds[p.SourceStartByte]}; its music lines are left out");
                }
            }
            CollectRanges(e);
        }
        _learned.Add((script, !common));
        _rangesPlaced = false; // a script learned after a move: its ranges are placed on the next one
    }

    /// <summary>
    /// Learns only the flag ranges of a script that is converted without being learned (DS3's common.emevd, whose
    /// new-game events <see cref="Ds3Setup"/> ports), so they are placed with every other range.
    /// </summary>
    public void LearnRanges(EMEVD script)
    {
        foreach (var e in script.Events)
            CollectRanges(e);
        _learned.Add((script, false));
        _rangesPlaced = false;
    }

    // Flag ranges (batch set/check of start..end) are only collected as scripts are learned, written in the line or
    // passed in; PlaceRanges gives them their numbers once every script is learned (see there for why).
    private void CollectRanges(EMEVD.Event e)
    {
        for (int i = 0; i < e.Instructions.Count; i++)
        {
            var ins = e.Instructions[i];
            if (RangeArgs(_ds3.Get(ins.Bank, ins.ID)) is not { } range)
                continue;
            var (_, start, end) = range;
            long? Passed(Emedf.Arg a) => e.Parameters.FirstOrDefault(p => p.InstructionIndex == i && p.TargetStartByte == a.Offset)?.SourceStartByte;
            var (ps, pe) = (Passed(start), Passed(end));
            if (ps is null && pe is null)
                _literalRanges.Add((Emedf.Read(ins.ArgData, start), Emedf.Read(ins.ArgData, end)));
            else if (ps is { } sb && pe is { } eb)
            {
                var list = _paramRanges.TryGetValue(e.ID, out var l) ? l : _paramRanges[e.ID] = [];
                if (!list.Contains((sb, eb)))
                    list.Add((sb, eb));
            }
        }
    }

    // Collected by Learn, placed by PlaceRanges.
    private readonly List<(long Start, long End)> _literalRanges = [];
    private readonly Dictionary<long, List<(long Start, long End)>> _paramRanges = new();
    private readonly List<(EMEVD Script, bool EventSlots)> _learned = [];
    private bool _rangesPlaced;

    /// <summary>The range start and end arguments of an instruction that has both, or null.</summary>
    private static (Emedf.Instr Def, Emedf.Arg Start, Emedf.Arg End)? RangeArgs(Emedf.Instr? def)
    {
        if (def is null)
            return null;
        var start = def.Args.FirstOrDefault(a => KindOf(a.Name) == Kind.FlagRangeStart);
        var end = def.Args.FirstOrDefault(a => KindOf(a.Name) == Kind.FlagRangeEnd);
        return start is not null && end is not null ? (def, start, end) : null;
    }

    /// <summary>
    /// Every DS3 flag range the learned scripts use (written in a line, or passed to an event that uses two of its
    /// values as a range's start and end), as the ranges <see cref="FlagMap.Range"/> must place.
    /// </summary>
    public IReadOnlyList<(long Start, long End)> LearnedRanges()
    {
        var all = new List<(long, long)>(_literalRanges);
        foreach (var (script, _) in _learned)
            foreach (var ins in script.Events.SelectMany(e => e.Instructions))
            {
                if (ins.Bank != 2000 || ins.ID is not (0 or 6))
                    continue;
                int head = ins.ID == 0 ? 8 : 4;
                if (ins.ArgData.Length < head || !_paramRanges.TryGetValue(BitConverter.ToUInt32(ins.ArgData, head - 4), out var pairs))
                    continue;
                foreach (var (sb, eb) in pairs)
                    if (head + Math.Max(sb, eb) + 4 <= ins.ArgData.Length)
                        all.Add((BitConverter.ToUInt32(ins.ArgData, head + (int)sb), BitConverter.ToUInt32(ins.ArgData, head + (int)eb)));
            }
        return all;
    }

    /// <summary>
    /// Gives every flag range of the learned scripts consecutive Elden Ring flags, before any single flag is moved.
    /// A range only means "these flags" while its flags are numbered one after another; it was placed per script as it
    /// was learned, so a smaller range seen first (DS3's NPC events check 1475-1477, then set 1475-1479) or a value
    /// moved on its own took some of a bigger range's flags, the bigger range couldn't be placed, and its other flags
    /// went wherever came next: a block away. 1.0.630's Cathedral of the Deep script then set 1475..1479 as
    /// 1030290135..1031290275, a million flags in one line, and the game panicked ("Transaction failed") flushing
    /// its flag-change queue the moment the map's NPC events ran. Here ranges that overlap are joined, each join is
    /// placed as one range, and ranges passed in (Deacons' 13504810-13504842 crowd) are placed as well. Runs once,
    /// by itself on the first move. Then the map events started in several slots get their slot flags (as before).
    /// </summary>
    public void PlaceRanges()
    {
        if (_rangesPlaced)
            return;
        _rangesPlaced = true;
        var global = new List<(long, long)>();
        var local = new Dictionary<(MapId, MapId), List<(long, long)>>();
        foreach (var (s, en) in LearnedRanges())
        {
            if (s <= 0 || en < s || en - s >= FlagMap.PerTile)
                continue;
            if (!OwnedByPortedMap(s))
                global.Add((s, en));
            else if (_maps.First(m => m.Ds3.OwnsDs3Id(s)) is var owner)
                (local.TryGetValue(owner, out var l) ? l : local[owner] = []).Add((s, en));
        }
        foreach (var (s, en) in Join(global))
            if (_flags.Range(s, en) < 0)
                Stats.Notes.Add($"flag range {s}-{en}: couldn't be placed together (some of its flags already have numbers)");
        foreach (var ((ds3, er), ranges) in local.OrderBy(l => l.Key.Item1.ToString(), StringComparer.Ordinal))
            foreach (var (s, en) in Join(ranges))
                if (!MapFlags.RangeKept(ds3, er, s, en) && _flags.LocalRange(s, en) < 0)
                    Stats.Notes.Add($"flag range {s}-{en}: couldn't be placed together (some of its flags already have numbers)");
        foreach (var (script, eventSlots) in _learned)
            if (eventSlots)
                PlaceEventSlots(script);
    }

    /// <summary>Ranges joined where they overlap, in order (1475-1477 and 1475-1479 become 1475-1479).</summary>
    public static List<(long Start, long End)> Join(IEnumerable<(long Start, long End)> ranges)
    {
        var joined = new List<(long Start, long End)>();
        foreach (var (s, e) in ranges.Distinct().OrderBy(r => r.Start).ThenBy(r => r.End))
            if (joined.Count > 0 && s <= joined[^1].End)
                joined[^1] = (joined[^1].Start, Math.Max(joined[^1].End, e));
            else
                joined.Add((s, e));
        return joined;
    }

    /// <summary>
    /// A map event started in several slots (InitializeEvent slot N) keeps one run-once flag per slot, ID + N. Where the
    /// map's block isn't kept and the event moves to a saved number (<see cref="MapFlags.MoveEvent"/>), its slots' flags
    /// get the numbers right after it, as one range, before anything else takes them.
    /// </summary>
    private void PlaceEventSlots(EMEVD script)
    {
        var lastSlot = new Dictionary<long, int>();
        foreach (var ins in script.Events.SelectMany(e => e.Instructions))
            if (ins.Bank == 2000 && ins.ID == 0 && ins.ArgData.Length >= 8)
            {
                int slot = BitConverter.ToInt32(ins.ArgData, 0);
                long callee = BitConverter.ToUInt32(ins.ArgData, 4);
                if (slot > 0 && slot < FlagMap.PerTile)
                    lastSlot[callee] = Math.Max(slot, lastSlot.GetValueOrDefault(callee));
            }
        foreach (var (callee, last) in lastSlot.OrderBy(k => k.Key))
        {
            var owners = _maps.Where(m => m.Ds3.OwnsDs3Id(callee)).Take(1).ToList();
            if (owners.Count == 0)
                continue;
            var (ds3, er) = owners[0];
            if (callee < ds3.Ds3FlagBase || callee + last >= ds3.Ds3FlagBase + 10_000 || MapFlags.RangeKept(ds3, er, callee, callee + last))
                continue;
            if (_flags.LocalRange(callee, callee + last) < 0)
                Stats.Notes.Add($"event {callee}: its {last + 1} slot flags couldn't be placed together (some already have numbers)");
            else
                Stats.EventSlotRanges++;
        }
    }

    private bool OwnedByPortedMap(long id) => _maps.Any(m => m.Ds3.OwnsDs3Id(id));

    /// <summary>A DS3 ID moved into Elden Ring by what it is.</summary>
    public long Move(long value, Kind kind)
    {
        PlaceRanges();
        if (kind == Kind.MusicRegion)
            return Music?.SwitchFor(value) ?? value;
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
                        // A map event's ID is also its run-once flag: it moves like the flag of the same number.
                        return kind == Kind.Event ? MapFlags.MoveEvent(_flags, ds3, er, value) : ds3.MoveEntity((int)value, er);
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
            case Kind.ItemLot:
                if ((Ids ?? Ds3Ids.Current)?.ItemLot(value) is { } lot)
                {
                    Stats.IdsMoved++;
                    return lot;
                }
                Stats.IdsKept++;
                return value;
            case Kind.Think:
                if ((Ids ?? Ds3Ids.Current)?.Think(value) is { } think)
                {
                    Stats.IdsMoved++;
                    return think;
                }
                Stats.IdsKept++;
                return value;
            case Kind.Behavior:
                if ((Ids ?? Ds3Ids.Current)?.Behavior(value) is { } behavior)
                {
                    Stats.IdsMoved++;
                    return behavior;
                }
                Stats.IdsKept++;
                return value;
            case Kind.Sfx:
                if ((Ids ?? Ds3Ids.Current)?.Sfx(value) is { } sfx)
                {
                    Stats.IdsMoved++;
                    return sfx;
                }
                Stats.IdsKept++;
                return value;
            case Kind.Camera:
                if ((Ids ?? Ds3Ids.Current)?.Camera(value) is { } camera)
                {
                    Stats.IdsMoved++;
                    return camera;
                }
                Stats.IdsKept++;
                return value;
            case Kind.NpcName:
                var name = MapText.Name(value);
                if (name != value)
                {
                    Stats.TextsMoved++;
                    if (value < int.MaxValue)
                        Texts?.Names.Add((int)value);
                }
                return name;
            case Kind.Message:
            case Kind.PlaceName:
                // DS3's text numbers name nothing (or other words) in Elden Ring: each names a copy of DS3's text.
                var text = kind == Kind.Message ? MapText.Message(value) : MapText.Place(value);
                if (text != value)
                {
                    Stats.TextsMoved++;
                    if (value < int.MaxValue)
                        (kind == Kind.Message ? Texts?.Messages : Texts?.Places)?.Add((int)value);
                }
                return text;
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
            _event = e;
            _line = i;
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
                if (_paramRanges.TryGetValue(callee, out var pairs))
                    foreach (var (sb, eb) in pairs)
                        if (8 + Math.Max(sb, eb) + 4 <= data.Length)
                            CheckPassedRange(data, 8 + (int)sb, 8 + (int)eb, BitConverter.ToUInt32(ins.ArgData, (ins.ID == 6 ? 4 : 8) + (int)sb),
                                BitConverter.ToUInt32(ins.ArgData, (ins.ID == 6 ? 4 : 8) + (int)eb));
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
        if (ins.Bank == 2010 && Music is not null && MusicLine(ins, paramBytes) is var (music, musicMap))
            return (music, musicMap);
        if (ins.Bank == 3 && ActionButtonIds.Contains(ins.ID))
            return ActionButton(ins);
        if (ins.Bank == 1003 && ClientChecks.ContainsKey(ins.ID))
            return ClientCheck(ins, parameterized);
        if (ins.Bank == 3 && SoloConditions.Contains(ins.ID))
            return SoloCondition(ins, parameterized);
        if (ins.Bank == 4 && ins.ID == 5 && PlayerPose(ins, paramBytes) is { } held)
            return (held, null);
        if (ins.Bank == 2003 && ins.ID == PlayerSceneWeapons.PairedAnimId && PairedPlayerAnimation(ins, parameterized) is { } paired)
            return (paired, null);
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
        if (RangeArgs(from) is (_, var fs, var fe) && RangeArgs(to) is (_, var ts, var te)
            && !paramBytes.Contains(fs.Offset) && !paramBytes.Contains(fe.Offset))
            CheckRange(output, ts, te, Emedf.Read(ins.ArgData, fs), Emedf.Read(ins.ArgData, fe));
        MoveItem(ins, from, to, output, paramBytes);
        return (new EMEVD.Instruction(to.Bank, to.Id, output), map);
    }

    /// <summary>
    /// The guard behind <see cref="PlaceRanges"/>: a converted range line whose Elden Ring flags aren't the DS3 range's
    /// one after another is counted, and one that would reach past a flag block is cut to the DS3 range's length.
    /// </summary>
    private void CheckRange(byte[] output, Emedf.Arg start, Emedf.Arg end, long ds3Start, long ds3End)
    {
        long s = Emedf.Read(output, start), e = Emedf.Read(output, end);
        if (FixRange(ds3Start, ds3End, s, e) is { } fixedEnd)
            Emedf.Write(output, end, fixedEnd);
    }

    private void CheckPassedRange(byte[] data, int startAt, int endAt, long ds3Start, long ds3End)
    {
        long s = BitConverter.ToUInt32(data, startAt), e = BitConverter.ToUInt32(data, endAt);
        if (FixRange(ds3Start, ds3End, s, e) is { } fixedEnd)
            BitConverter.TryWriteBytes(data.AsSpan(endAt), (uint)fixedEnd);
    }

    /// <summary>The end to write instead (null: keep it) for a DS3 range ds3Start..ds3End moved to start..end.</summary>
    private long? FixRange(long ds3Start, long ds3End, long start, long end)
    {
        if (ds3Start <= 0 || ds3End < ds3Start || end - start == ds3End - ds3Start)
            return null;
        Stats.BrokenRanges++;
        bool tooWide = end < start || end - start >= FlagMap.PerTile;
        Stats.Notes.Add($"flag range {ds3Start}-{ds3End} became {start}-{end}" + (tooWide ? $"; cut to {start}-{start + (ds3End - ds3Start)}" : ""));
        return tooWide ? start + Math.Min(ds3End - ds3Start, FlagMap.PerTile - 1) : null;
    }

    /// <summary>
    /// The item of a line that names one by type and ID ("IF Player Has/Doesn't Have Item", "Remove Item From Player",
    /// "Award Gesture Item"...): DS3's item numbers are other items in Elden Ring (the Coiled Sword 2137 is none, the
    /// Homeward Bone 350 is the Holy Water Pot), so the ID becomes the Elden Ring item <see cref="Ds3Ids.Item"/> gives;
    /// one it has none for stays. Only when both values are written in the line (not passed in).
    /// </summary>
    private void MoveItem(EMEVD.Instruction ins, Emedf.Instr from, Emedf.Instr to, byte[] output, IReadOnlySet<long> paramBytes)
    {
        var ids = Ids ?? Ds3Ids.Current;
        if (ids is null)
            return;
        var fromType = from.Args.FirstOrDefault(a => a.Name.Equals("Item Type", StringComparison.OrdinalIgnoreCase));
        var fromId = from.Args.FirstOrDefault(a => a.Name.Equals("Item ID", StringComparison.OrdinalIgnoreCase));
        var toId = to.Args.FirstOrDefault(a => a.Name.Equals("Item ID", StringComparison.OrdinalIgnoreCase));
        if (fromType is null || fromId is null || toId is null || paramBytes.Contains(fromType.Offset) || paramBytes.Contains(fromId.Offset))
            return;
        if (ids.Item(Emedf.Read(ins.ArgData, fromType), Emedf.Read(ins.ArgData, fromId)) is { } item)
        {
            Emedf.Write(output, toId, item);
            Stats.IdsMoved++;
        }
        else
            Stats.IdsKept++;
    }

    // The event and line being converted (for what the line's passed-in values hold).
    private EMEVD.Event? _event;
    private int _line;

    /// <summary>
    /// A DS3 music line as the flag line the music port makes of it (<see cref="MapMusic.Plan.Convert"/>), or null
    /// when it stays a no-op. A line with passed-in values comes with the mapping that puts them in the new line's
    /// bytes: the region's value fills the flag (the lines that start the event pass the switch flag there), the
    /// state fills the state, and a value with no place in the new line (a fade time) is dropped.
    /// </summary>
    private (EMEVD.Instruction Instruction, Mapping? Map)? MusicLine(EMEVD.Instruction ins, IReadOnlySet<long> paramBytes)
    {
        if (_ds3.Get(ins.Bank, ins.ID) is not { } def)
            return null;
        int entityArg = def.Args.ToList().FindIndex(a => a.Name.Equals("Entity ID", StringComparison.OrdinalIgnoreCase));
        int stateArg = def.Args.ToList().FindIndex(a => a.Name.Equals("Enable/Disable", StringComparison.OrdinalIgnoreCase));
        // The region is passed in, and that value of the event holds nothing but music regions.
        bool passedMusic = entityArg >= 0 && _event is { } e && _paramKinds.TryGetValue(e.ID, out var kinds)
            && e.Parameters.Any(p => p.InstructionIndex == _line && p.TargetStartByte == def.Args[entityArg].Offset
                                     && kinds.GetValueOrDefault(p.SourceStartByte) == Kind.MusicRegion);
        if (Music!.Convert(ins, _ds3, paramBytes, MusicMap, passedMusic) is not { } line)
            return null;
        if (paramBytes.Count == 0)
            return (line, null);
        if (_er.Get((int)line.Bank, (int)line.ID) is not { } to)
            return null; // no layout to place the passed-in values by
        var source = new int[to.Args.Count];
        Array.Fill(source, -1);
        // Set Event Flag (flag type, flag, state): the region's value is the flag, the state the state.
        if (line.Bank == 2003 && line.ID == 66 && to.Args.Count >= 3)
        {
            source[1] = entityArg;
            source[2] = stateArg;
        }
        return (line, new Mapping(def, to, source, new Kind[to.Args.Count], false));
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
    /// How many clients of DS3's client type 1 a ported world has: 1, the player who owns it (the host).
    /// <para>
    /// darkscript3's DS3 definitions call type 1 "Invader", and up to 1.0.567 it was read that way (never present, so
    /// "SKIP IF type 1 == 0" always skipped). DS3's own scripts show it is the host: every one of DS3's 219 checks is
    /// "SKIP IF type 1 == 0" in front of what only the world's owner should do (Set Network-Connected Event Flag, Set
    /// Network Update Authority, Send Invading Phantoms Home, the warping form of a boss cutscene), and Iudex Gundyr's
    /// sword-pull flag 14000802, which 14005810 waits on before the fight starts, is set in exactly one place in all of
    /// DS3: m40 14005811 line 20, right behind such a check. DS3's solo Gundyr works, so the check can't skip for a solo
    /// player. Read as invaders, 157 of the 219 skipped lines and 99 network flag sets never ran: the sword pull never
    /// started Gundyr's fight, and boss, door and lever flags across every ported map were never set.
    /// </para>
    /// </summary>
    public const int HostCount = 1;

    /// <summary>
    /// DS3 "SKIP/GOTO/END IF Number of Clients of Type" (first argument: lines, label or end type; then Client Type,
    /// Comparison Type, Target Number of Clients). See <see cref="ClientChecks"/>.
    /// </summary>
    /// <summary>
    /// DS3 2003[72] (object, animation, on): the player plays that animation with the object. DS3 uses it once, in
    /// Firelink Shrine's coiled sword event (m40 14005101), right after it plays 60430 on the bonfire object (the
    /// sword sliding into the ash): 60430 is also the player's thrust (DS3 c0000 a000_060430, which hides both
    /// weapons). Elden Ring has no such instruction (its 2003[72] is another one), so the line was a no-op and the
    /// sword floated into the bonfire with the player standing by (1.0.667 playtest, 2026-11-26). It becomes "Force
    /// Animation Playback" on the player; Elden Ring has an entry of that number in its player's a000. A line that
    /// takes a value from its event's parameters, or turns the state off, stays a no-op.
    /// </summary>
    private EMEVD.Instruction? PairedPlayerAnimation(EMEVD.Instruction ins, bool parameterized)
    {
        if (parameterized || ins.ArgData.Length < 9 || ins.ArgData[8] == 0 || _er.Get("Force Animation Playback") is not { } play)
            return null;
        int anim = BitConverter.ToInt32(ins.ArgData, 4);
        if (anim <= 0)
            return null;
        PairedPlayerAnimations++;
        var output = new byte[play.Length];
        foreach (var arg in play.Args)
            Emedf.Write(output, arg, arg.Name.Contains("Entity", StringComparison.OrdinalIgnoreCase) ? PlayerSceneWeapons.Player
                : arg.Name.Contains("Animation", StringComparison.OrdinalIgnoreCase) ? anim
                : arg.Type == 6 ? BitConverter.SingleToInt32Bits(1f) : 0);
        return new EMEVD.Instruction(play.Bank, play.Id, output);
    }

    /// <summary>DS3 2003[72] lines turned into the player's animation (<see cref="PairedPlayerAnimation"/>).</summary>
    public int PairedPlayerAnimations { get; private set; }

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
        // DS3's host-only lines: "SKIP IF Number of Clients of Type(n, Invader, Equal, 0)" guards every line that
        // sets a network-connected flag, an ObjAct state or update authority (219 uses in all 31 DS3 scripts, the
        // only form of type 1 or 2 DS3 has; GOTO/END forms are unused; type 1 is the host, see HostCount). Read as
        // "no invaders" it skipped always,
        // and DS3's lever lifts never armed their levers or set "the lift is up", so High Wall's lift went back down
        // as soon as the player stepped off and its levers did nothing (2026-11-21). Elden Ring's own lever lift
        // (common_func 90005500, made from DS3's 20005621) puts "SKIP IF Map Has Permission To Update(0)" at the
        // same lines: skip unless this world is the host's. That form is used here, so single player and the host
        // of a co-op session run them and joined players don't (Seamless Co-op keeps the host in charge of the map).
        if (clientType == 1 && comparison == 0 && count == 0 && HostChecks.TryGetValue(ins.ID, out var hostId) && _er.Get(1003, hostId) is { } hostCheck)
        {
            var data = new byte[hostCheck.Length]; // Elden Ring's own: Has Permission 0, Unknown 0, map bytes 0
            Emedf.Write(data, hostCheck.Args[0], first);
            return (new EMEVD.Instruction(1003, hostId, data), null);
        }
        int present = clientType == 1 ? HostCount : 0;
        bool holds = comparison switch
        {
            0 => present == count, 1 => present != count, 2 => present > count, 3 => present < count,
            4 => present >= count, 5 => present <= count, _ => false,
        };
        return (holds ? RunEvents.Make(1000, alwaysId, first) : NoOp(), null);
    }

    /// <summary>
    /// DS3 conditions on the online session that Elden Ring has no form of, but that a ported world answers the same
    /// way every time (bank 3): 39 "IF Friendly Phantom Count Comparison" (no summoned phantoms: 0), 21 "IF Owns DLC"
    /// (the DLC maps are ported: owned) and 29 "IF Multiplayer Network Penalized" (never).
    /// <para>
    /// Up to 1.0.640 they had no Elden Ring form, and a condition without one drops its whole event. Demon Prince
    /// (m50 15005812) asks for 2+ phantoms before giving the survivor its co-op damage cut, so the event that ends the
    /// first phase was dropped: nothing turned the demons' immortality off, they never died at zero health and the
    /// second phase never began (Devin's 1.0.640 playtest). Halflight's start (m51 15105812, 15105814, 15105920) counts
    /// phantoms the same way to pick its SpEffect. Each becomes <see cref="Constant"/> with the single-player answer.
    /// </para>
    /// </summary>
    private static readonly HashSet<int> SoloConditions = [21, 29, 39];

    /// <summary>How many friendly phantoms a ported world has: none (Elden Ring's co-op is not DS3's summon signs).</summary>
    public const int SoloPhantoms = 0;

    /// <summary>A condition in <see cref="SoloConditions"/> as one with the same fixed answer.</summary>
    private (EMEVD.Instruction?, Mapping?) SoloCondition(EMEVD.Instruction ins, bool parameterized)
    {
        if (parameterized || _ds3.Get(ins.Bank, ins.ID) is not { } from || from.Args.Count == 0)
            return (null, null); // a value passed in decides it: can't be settled here
        long Value(int i) => i < from.Args.Count ? Emedf.Read(ins.ArgData, from.Args[i]) : 0;
        var group = (sbyte)Value(0);
        bool holds = ins.ID switch
        {
            // Result group, Desired Comparison State, Comparison Type, Target Count.
            39 => Compare(SoloPhantoms, (int)Value(2), Value(3)) == (Value(1) != 0),
            // Result group, Owns DLC (1: owned).
            21 => Value(1) != 0,
            _ => false,
        };
        return (Constant(group, holds), null);
    }

    /// <summary>
    /// The SpEffects only DS3's people event animations cast (<see cref="TalkPort.PoseEffects"/>: 150 and its like, on
    /// for as long as a kneel, a seat or a walk into place plays). Null: none known.
    /// <para>
    /// DS3's events ask whether the player still has one to tell that a scripted pose wasn't broken off: the level-up
    /// kneel (common_func 20006005, started by the Fire Keeper's talk flag 74000135) cancels the request the moment
    /// "player lacks 150" holds. Elden Ring's player has none of DS3's event animations, so never has the effect, and
    /// the ported event turned the request flag off again the frame after the talk script turned it on (1.0.667,
    /// "leveling up from the firekeeper did not work, a window did not appear"). A ported world's player counts as
    /// holding the pose: "has" always holds and "lacks" never does (<see cref="Constant"/>).
    /// </para>
    /// </summary>
    public IReadOnlySet<long>? PlayerPoses { get; init; }

    /// <summary>The player's entity ID in both games' scripts.</summary>
    public const long Player = 10000;

    /// <summary>How many "IF Character Has SpEffect" lines about the player and a DS3 pose got a fixed answer.</summary>
    public int PlayerPoseChecks { get; private set; }

    /// <summary>
    /// "IF Character Has SpEffect" (4[05]: group, entity, SpEffect, should have) asked of the player about one of
    /// <see cref="PlayerPoses"/>, as a condition with the fixed answer; null for any other line (an entity, effect or
    /// answer passed into the event isn't known here and stays as written).
    /// </summary>
    private EMEVD.Instruction? PlayerPose(EMEVD.Instruction ins, IReadOnlySet<long> paramBytes)
    {
        if (PlayerPoses is null || _ds3.Get(4, 5) is not { } from || from.Args.Count < 4
            || from.Args.Take(4).Any(a => paramBytes.Contains(a.Offset)))
            return null;
        if (Emedf.Read(ins.ArgData, from.Args[1]) != Player || !PlayerPoses.Contains(Emedf.Read(ins.ArgData, from.Args[2])))
            return null;
        var line = Constant((sbyte)Emedf.Read(ins.ArgData, from.Args[0]), Emedf.Read(ins.ArgData, from.Args[3]) != 0);
        if (line is not null)
            PlayerPoseChecks++;
        return line;
    }

    /// <summary>DS3/Elden Ring comparison types: 0 ==, 1 !=, 2 &gt;, 3 &lt;, 4 &gt;=, 5 &lt;=.</summary>
    public static bool Compare(long left, int comparison, long right) => comparison switch
    {
        0 => left == right, 1 => left != right, 2 => left > right, 3 => left < right, 4 => left >= right, 5 => left <= right, _ => false,
    };

    /// <summary>
    /// A condition that always holds, or never does, in group <paramref name="group"/>: Elden Ring's "IF Game Cycle"
    /// (3[17]) asking for at least 0 cycles (always) or fewer than 0 (never). Null when Elden Ring's definitions lack it.
    /// </summary>
    public EMEVD.Instruction? Constant(sbyte group, bool holds)
    {
        if (_er.Get(3, 17) is not { } cycle || cycle.Args.Count < 3)
            return null;
        var data = Emedf.Defaults(cycle);
        Emedf.Write(data, cycle.Args[0], group);
        Emedf.Write(data, cycle.Args[1], holds ? 4 : 3);
        Emedf.Write(data, cycle.Args[2], 0);
        return new EMEVD.Instruction(3, 17, data);
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
