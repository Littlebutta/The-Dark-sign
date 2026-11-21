using SoulsFormats;
using static DarkSign.Porter.Maps.TalkPort;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The bonfire menu's "Travel" and "Return to the Lands Between".
/// <para>
/// A DS3 bonfire's talker ran Elden Ring's grace menu (talk script t000001000 in the common talk binder), and Elden
/// Ring travels from its world map, which has no DS3 map on it. So the bonfires get their own copy of that script
/// (<see cref="TalkId"/>, in each slot's talk binder) with two more entries in the main list:
/// </para>
/// <list type="bullet">
/// <item>"Travel": DS3's warp menu as two lists, the areas (DS3's tabs, in DS3's order: BonfireWarpParam's ListId)
/// and then the area's bonfires (in row order, as DS3 lists them). An area shows once one of its bonfires is lit, a
/// bonfire once it is lit (the lit flag "Register Bonfire" sets), as in DS3.</item>
/// <item>"Return to the Lands Between": sets the F1 menu's leave request (<see cref="RunEvents.LeaveDs3Request"/>).</item>
/// </list>
/// Picking a bonfire opens one more small list, "Travel here" / "Back", and turns on the bonfire's preview flag
/// (<see cref="PreviewFlag"/>) while it is open: the test menu DLL (loaded in every run) draws DS3's own picture of
/// that bonfire beside the list, the way DS3's warp menu shows one (<see cref="BonfireImages"/>, darksign_bonfires.tsv).
/// A talk menu can't draw a picture or tell which entry is under the cursor, so the picture comes with the pick, not
/// with the hover.
/// A talk script can't warp, so picking a bonfire sets a request flag and an event in common.emevd warps to the
/// bonfire's standing point, the way the F1 menu's warps work (<see cref="RunEvents"/>).
/// <para>
/// The copy is edited, not rewritten: the list state (the one that adds "Leave", entry 99) gets the two entries, the
/// state that reads the picked entry gets two more branches, and the new lists are new state groups built from the
/// script's own pieces (the list / wait / pick states of one of its sub-menus), so every expression in them is one
/// Elden Ring's script already uses. Read off t000001000, 2026-11-19: command 1:19 adds an entry (number, text, -1),
/// 5:19 the same under a condition, 1:20 clears the list, 1:10 shows it, function 23 is the picked entry, 15 an
/// event flag, command 1:11 sets a flag, 6:N calls state group N and 7:-1 returns from one.
/// </para>
/// DARKSIGN_BONFIRE_TRAVEL=0 (launcher fallback "map-bonfires" D) leaves the bonfires with Elden Ring's own menu.
/// </summary>
public static class BonfireTravel
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_BONFIRE_TRAVEL") != "0";

    /// <summary>The bonfire menu's script. Elden Ring's own are 1xxxxxxxx-8xxxxxxxx; the ported people's 9xxxxxxxx.</summary>
    public const int TalkId = 899_001_000;
    public const string GraceScript = "t000001000", CommonTalk = "/script/talk/m00_00_00_00.talkesdbnd.dcx";

    public const int MaxDestinations = 90;
    public static uint RequestFlag(int index) => RunEvents.FlagBlock + 2110 + (uint)index;
    public static uint EventId(int index) => RunEvents.FlagBlock + 1110 + (uint)index;
    /// <summary>On while a bonfire's "Travel here" list is open (2000-2999: reset on load). The DLL shows its picture.</summary>
    public static uint PreviewFlag(int index) => RunEvents.FlagBlock + 2000 + (uint)index;
    public const string ListFile = "darksign_bonfires.tsv";
    public static bool IsTravelEvent(long id) => id >= RunEvents.FlagBlock + 1110 && id < RunEvents.FlagBlock + 1110 + MaxDestinations;

    /// <summary>Entry numbers in the main list (Elden Ring's use 1-15, 32, 41, 50, 75 and 99).</summary>
    public const int TravelEntry = 61, ReturnEntry = 62, BackEntry = 99;

    /// <summary>One bonfire to travel to.</summary>
    /// <param name="LitFlag">The flag (in Elden Ring's numbers) that is on once the bonfire is lit.</param>
    /// <param name="Point">The standing point's entity (980+n).</param>
    public sealed record Destination(MapId Er, int Number, string Name, string Area, int List, int Order, uint LitFlag, uint Point)
    {
        public int Index { get; init; }
        /// <summary>DS3's BonfireWarpParam PictureId, -1 for none.</summary>
        public int Picture { get; init; } = -1;
    }

    /// <summary>The text numbers of the menu: travel, return, back, then one per area and one per destination, and "Travel here".</summary>
    public sealed record Texts(int Travel, int Return, int Back, IReadOnlyList<int> Areas, IReadOnlyList<int> Bonfires, int Confirm);

    /// <summary>DS3's order: tabs by ListId, a tab's bonfires by row; unlisted ones (99) last, by map.</summary>
    public static List<Destination> Sort(IEnumerable<Destination> all) =>
        all.OrderBy(d => d.List).ThenBy(d => d.Order).ThenBy(d => d.Er.ToString(), StringComparer.Ordinal).ThenBy(d => d.Number)
            .Take(MaxDestinations).Select((d, i) => d with { Index = i }).ToList();

    /// <summary>The areas in list order, each with its destinations.</summary>
    public static List<(string Area, List<Destination> Bonfires)> Areas(IReadOnlyList<Destination> sorted)
    {
        var areas = new List<(string, List<Destination>)>();
        foreach (var d in sorted)
        {
            var at = areas.FindIndex(a => a.Item1 == d.Area);
            if (at < 0)
                areas.Add((d.Area, [d]));
            else
                areas[at].Item2.Add(d);
        }
        return areas;
    }

    // ---- Expressions, in the forms the grace script uses ----

    private static byte[] End(Node n) => Emit([new Op(0xA1, [n])]);
    private static byte[] Number(long v) => End(new Lit(v));
    private static Node FlagOn(uint flag) => new Op(0x95, [new Call(new Lit(15), [new Lit(flag)]), new Lit(1)]);
    private static Node AnyOn(IEnumerable<uint> flags) => flags.Select(FlagOn).Aggregate((a, b) => new Op(0x99, [a, b]));
    private static readonly byte[] Always = Number(1);
    /// <summary>The first branch on the picked entry stores it (function 23, kept by 0xA7); the rest read it back (0xAF).</summary>
    private static byte[] PickedFirst(int entry) => End(new Op(0x95, [new Op(0xA7, [new Call(new Lit(23), [])]), new Lit(entry)]));
    private static byte[] Picked(int entry) => End(new Op(0x95, [new Raw([0xAF]), new Lit(entry)]));
    /// <summary>What a called state group returned (0xB9).</summary>
    private static byte[] Returned(int value) => End(new Op(0x95, [new Raw([0xB9]), new Lit(value)]));

    private static ESD.CommandCall Add(int entry, int text) => new(1, 19, Number(entry), Number(text), Number(-1));
    private static ESD.CommandCall AddIf(Node condition, int entry, int text) => new(5, 19, End(condition), Number(entry), Number(text), Number(-1));
    private static ESD.CommandCall SetFlag(uint flag) => new(1, 11, Number(flag), Number(1));
    private static ESD.CommandCall FlagOff(uint flag) => new(1, 11, Number(flag), Number(0));
    private static ESD.CommandCall Call(long group) => new(6, (int)group);

    private static ESD.State Goto(long target, params ESD.CommandCall[] entry)
    {
        var s = new ESD.State();
        s.EntryCommands.AddRange(entry);
        s.Conditions.Add(new ESD.Condition(target, Always));
        return s;
    }

    private static ESD.State Return(int value)
    {
        var s = new ESD.State();
        var c = new ESD.Condition { Evaluator = Always };
        c.PassCommands.Add(new ESD.CommandCall(7, -1, Number(value)));
        s.Conditions.Add(c);
        return s;
    }

    private static ESD.Condition After(byte[] evaluator, long target)
    {
        var c = new ESD.Condition { Evaluator = evaluator };
        c.Subconditions.Add(new ESD.Condition(target, Always));
        return c;
    }

    private static bool IsCommand(ESD.CommandCall c, int bank, int id) => c.CommandBank == bank && c.CommandID == id;
    private static long? Literal(byte[] expression)
    {
        try { return Parse(expression) is [Op { Code: 0xA1, Kids: [Lit l] }] ? l.Value : null; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }
    private static bool Reads(byte[] expression, long function)
    {
        try { return Parse(expression).SelectMany(Flatten).Any(n => n is Call { Func: Lit f } && f.Value == function); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return false; }
    }
    private static IEnumerable<Node> Flatten(Node n) =>
        (n switch { Call c => c.Args.Prepend(c.Func), Op p => p.Kids, _ => Enumerable.Empty<Node>() }).SelectMany(Flatten).Prepend(n);

    /// <summary>
    /// A list the player picks from, built from the pieces of the grace script's own sub-menu <paramref name="like"/>
    /// (its clear, show and wait states): entries, then "back" (99). Picking entry i runs <paramref name="onPick"/>'s
    /// state and returns what it says; anything else returns 0.
    /// </summary>
    private static Dictionary<long, ESD.State> Menu(SubMenu like, IReadOnlyList<ESD.CommandCall> entries, int back,
        IReadOnlyList<(int Entry, Func<long, long, long, ESD.State> State)> onPick) =>
        Menu(like, entries, back, [.. onPick.Select(p => (p.Entry, (Func<long, long, long, Func<ESD.State, long>, ESD.State>)((l, c, ch, _) => p.State(l, c, ch))))]);

    /// <summary>As above; a pick's state can add states of its own (the last argument adds one and returns its number).</summary>
    private static Dictionary<long, ESD.State> Menu(SubMenu like, IReadOnlyList<ESD.CommandCall> entries, int back,
        IReadOnlyList<(int Entry, Func<long, long, long, Func<ESD.State, long>, ESD.State> State)> onPick)
    {
        // 0 start, 1 clear, 2 list, 3 show and wait, 4 pick, 5 return 0, 6 return 1, 7+ one per entry.
        const long Clear = 1, List = 2, Show = 3, Pick = 4, Cancelled = 5, Chosen = 6;
        var g = new Dictionary<long, ESD.State> { [0] = Goto(Clear) };
        g[Clear] = Goto(List, [.. like.Clear.Select(Copy)]);
        g[List] = Goto(Show, [.. entries, Add(BackEntry, back)]);
        var show = new ESD.State();
        show.EntryCommands.AddRange(like.Show.Select(Copy));
        show.Conditions.Add(new ESD.Condition(Pick, (byte[])like.Wait.Clone()));
        g[Show] = show;
        var pick = new ESD.State();
        long next = 7 + onPick.Count;
        long AddState(ESD.State s) { g[next] = s; return next++; }
        for (int i = 0; i < onPick.Count; i++)
        {
            pick.Conditions.Add(new ESD.Condition(7 + i, i == 0 ? PickedFirst(onPick[i].Entry) : Picked(onPick[i].Entry)));
            g[7 + i] = onPick[i].State(List, Cancelled, Chosen, AddState);
        }
        pick.Conditions.Add(new ESD.Condition(Cancelled, Always));
        g[Pick] = pick;
        g[Cancelled] = Return(0);
        g[Chosen] = Return(1);
        return g;
    }

    private static ESD.CommandCall Copy(ESD.CommandCall c) => new(c.CommandBank, c.CommandID, [.. c.Arguments.Select(a => (byte[])a.Clone())]);

    /// <summary>The clear / show / wait pieces of one of the script's own sub-menus.</summary>
    private sealed record SubMenu(List<ESD.CommandCall> Clear, List<ESD.CommandCall> Show, byte[] Wait);

    /// <summary>
    /// The script's pieces: the main list (the state group whose list has "Leave", entry 99, and a state branching on
    /// the picked entry with ten or more branches), and a sub-menu's show state (command 1:10 with a wait condition).
    /// Throws when the script isn't laid out that way (another game version), so the bonfires keep Elden Ring's menu.
    /// </summary>
    private static (long Group, ESD.State Leave, ESD.State Pick, SubMenu Like) Pieces(ESD esd)
    {
        foreach (var (id, group) in esd.StateGroups.OrderByDescending(g => g.Value.Count))
        {
            var pick = group.Values.FirstOrDefault(s => s.Conditions.Count >= 10 && Reads(s.Conditions[0].Evaluator, 23)
                                                        && s.Conditions[^1].TargetState is not null && Literal(s.Conditions[^1].Evaluator) == 1);
            var leave = group.Values.FirstOrDefault(s => s.EntryCommands.Any(c => IsCommand(c, 1, 19) && c.Arguments.Count == 3 && Literal(c.Arguments[0]) == BackEntry));
            var show = group.Values.FirstOrDefault(s => s.EntryCommands.Any(c => IsCommand(c, 1, 10)) && s.Conditions.Count == 1
                                                        && s.Conditions[0].TargetState is not null && Reads(s.Conditions[0].Evaluator, 59));
            if (pick is null || leave is null || show is null)
                continue;
            var clear = group.Values.FirstOrDefault(s => s.EntryCommands.Count == 1 && IsCommand(s.EntryCommands[0], 1, 20))
                        ?? throw new InvalidDataException("the grace menu has no state that clears its list");
            return (id, leave, pick, new SubMenu(clear.EntryCommands, show.EntryCommands, show.Conditions[0].Evaluator));
        }
        throw new InvalidDataException("the grace menu's main list wasn't found (no state group adds entry 99 and branches on the picked entry)");
    }

    /// <summary>
    /// Elden Ring's grace script with "Travel" and "Return" added. Returns the state groups added.
    /// </summary>
    public static int AddTo(ESD esd, IReadOnlyList<Destination> sorted, Texts texts, uint leaveFlag)
    {
        var (mainId, leave, pick, like) = Pieces(esd);
        var main = esd.StateGroups[mainId];
        var areas = Areas(sorted);
        long nextGroup = esd.StateGroups.Keys.Where(k => k > 0x7000_0000).DefaultIfEmpty(0x7FFF_FFFF).Min() - 1;

        // "Travel here" / "Back": returns 1 for travel, 0 for back or a closed menu.
        long confirmGroup = nextGroup--;
        esd.StateGroups[confirmGroup] = Menu(like, [Add(1, texts.Confirm)], texts.Back,
            [(1, (Func<long, long, long, ESD.State>)((_, _, chosen) => Goto(chosen)))]);

        // One list per area: its lit bonfires. Picking one shows its picture (preview flag on) and asks "Travel here";
        // yes asks for the warp and says so (returns 1), back comes back to this list. Showing the list turns every
        // preview of the area off, so a picture never outlives its question.
        var areaGroups = new List<long>();
        foreach (var (_, bonfires) in areas)
        {
            var entries = bonfires.Select(d => FlagOff(PreviewFlag(d.Index)))
                .Concat(bonfires.Select((d, i) => AddIf(FlagOn(d.LitFlag), i + 1, texts.Bonfires[d.Index]))).ToList();
            var picks = bonfires.Select((d, i) => (i + 1, (Func<long, long, long, Func<ESD.State, long>, ESD.State>)((_, _, chosen, addState) =>
            {
                long go = addState(Goto(chosen, FlagOff(PreviewFlag(d.Index)), SetFlag(RequestFlag(d.Index))));
                var s = new ESD.State();
                s.EntryCommands.Add(SetFlag(PreviewFlag(d.Index)));
                s.EntryCommands.Add(Call(confirmGroup));
                s.Conditions.Add(After(Returned(1), go));
                s.Conditions.Add(After(Returned(0), 1));   // back: this list, cleared and built again
                return s;
            }))).ToList();
            esd.StateGroups[nextGroup] = Menu(like, entries, texts.Back, picks);
            areaGroups.Add(nextGroup--);
        }

        // The areas: one entry per area with a lit bonfire. A bonfire picked inside returns 1 all the way up.
        long travelGroup = nextGroup--;
        {
            var entries = areas.Select((a, i) => AddIf(AnyOn(a.Bonfires.Select(d => d.LitFlag)), i + 1, texts.Areas[i])).ToList();
            var picks = areas.Select((_, i) => (i + 1, (Func<long, long, long, ESD.State>)((list, _, chosen) =>
            {
                var s = new ESD.State();
                s.EntryCommands.Add(Call(areaGroups[i]));
                s.Conditions.Add(After(Returned(1), chosen));
                s.Conditions.Add(After(Returned(0), 1));   // back to this list, cleared and built again
                return s;
            }))).ToList();
            esd.StateGroups[travelGroup] = Menu(like, entries, texts.Back, picks);
        }

        // The main list: the two entries before "Leave", and their branches before the pick state's last one.
        long menuStart = main.First(s => s.Value.EntryCommands.Count == 1 && IsCommand(s.Value.EntryCommands[0], 1, 20)).Key;
        long away = pick.Conditions[^1].TargetState!.Value;   // where an entry that isn't a sub-menu goes: out of the menu
        long travelState = main.Keys.Max() + 1, returnState = travelState + 1;
        int at = leave.EntryCommands.FindIndex(c => IsCommand(c, 1, 19) && Literal(c.Arguments[0]) == BackEntry);
        leave.EntryCommands.Insert(at, Add(ReturnEntry, texts.Return));
        if (sorted.Count > 0)
            leave.EntryCommands.Insert(at, Add(TravelEntry, texts.Travel));
        var travel = new ESD.State();
        travel.EntryCommands.Add(Call(travelGroup));
        travel.Conditions.Add(After(Returned(1), away));
        travel.Conditions.Add(After(Returned(0), menuStart));
        main[travelState] = travel;
        main[returnState] = Goto(away, SetFlag(leaveFlag));
        pick.Conditions.Insert(pick.Conditions.Count - 1, new ESD.Condition(travelState, Picked(TravelEntry)));
        pick.Conditions.Insert(pick.Conditions.Count - 1, new ESD.Condition(returnState, Picked(ReturnEntry)));
        return areaGroups.Count + 2;
    }

    // ---- Events ----

    /// <summary>
    /// Adds (or replaces) one event per destination in common.emevd: wait for the request, turn it off, warp to the
    /// bonfire's standing point. Started from event 0.
    /// </summary>
    public static void AddEvents(EMEVD common, IReadOnlyList<Destination> sorted)
    {
        common.Events.RemoveAll(e => IsTravelEvent(e.ID));
        var constructor = common.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
            common.Events.Insert(0, constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        constructor.Instructions.RemoveAll(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 && IsTravelEvent(BitConverter.ToUInt32(i.ArgData, 4)));
        foreach (var d in sorted)
        {
            var e = new EMEVD.Event(EventId(d.Index), EMEVD.Event.RestBehaviorType.Default);
            e.Instructions.Add(RunEvents.Make(1003, 0, (byte)1, (byte)0, RequestFlag(d.Index)));                 // wait for the flag
            e.Instructions.Add(RunEvents.Make(2003, 66, (byte)0, RequestFlag(d.Index), (byte)0));                // flag off
            e.Instructions.Add(RunEvents.Make(2003, 66, (byte)0, PreviewFlag(d.Index), (byte)0));                // picture off
            e.Instructions.Add(RunEvents.Make(2003, 66, (byte)0, RunEvents.Ds3RunActive, (byte)1));
            e.Instructions.Add(RunEvents.Make(2003, 14, (byte)d.Er.Area, (byte)d.Er.Block, (byte)d.Er.Region, (byte)d.Er.Index, d.Point, 0));
            e.Instructions.Add(RunEvents.Make(1001, 0, 1f));
            e.Instructions.Add(RunEvents.Make(1000, 4, (byte)1));                                                // end and restart
            common.Events.Add(e);
            constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, EventId(d.Index), 0u));
        }
    }

    // ---- The step ----

    private const string CommonScript = "/event/common.emevd.dcx";

    /// <summary>The first run of 1,000 free numbers in the menu text, from a few fixed starts (so reruns reuse it).</summary>
    public static int TextBaseIn(FMG eventText, string marker)
    {
        if (eventText.Entries.FirstOrDefault(e => e.Text == marker) is { } mine)
            return mine.ID;
        var used = eventText.Entries.Select(e => e.ID).ToHashSet();
        foreach (var start in new[] { 98_700_000, 97_700_000, 96_700_000, 88_700_000 })
            if (!Enumerable.Range(start, 1000).Any(used.Contains))
                return start;
        throw new InvalidDataException("no free run of text numbers for the travel menu");
    }

    public const string TravelText = "Travel", ReturnText = "Return to the Lands Between", BackText = "Back", ConfirmText = "Travel here";

    /// <summary>
    /// Writes the menu script into every slot's talk binder that has bonfires, its text, and the warp events.
    /// <paramref name="all"/>: every slot's bonfires (unsorted). Returns the report line; throws when Elden Ring's
    /// script can't be edited (the caller keeps the plain grace menu then).
    /// </summary>
    public static string Write(PorterContext ctx, GameArchives er, IEnumerable<Destination> all, GameArchives? ds3 = null)
    {
        var sorted = Sort(all);
        var areas = Areas(sorted);
        var source = BND4.Read(er.Read(CommonTalk)).Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith(GraceScript, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException($"{ItemText.Leaf(CommonTalk)} has no {GraceScript}");

        // Text first: the script names its numbers.
        var modMenuPath = ctx.ModPath(MapPorter.ModParts(ActionButtons.ModMenu));
        var (vanillaMenu, menuCompression) = er.ReadWithCompression(ActionButtons.ModMenu);
        var menu = BND4.Read(File.Exists(modMenuPath) ? DCX.Decompress(File.ReadAllBytes(modMenuPath)) : vanillaMenu);
        var eventFile = menu.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith("EventTextForTalk", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"{ItemText.Leaf(ActionButtons.ModMenu)} has no EventTextForTalk file");
        var eventText = FMG.Read(eventFile.Bytes);
        int textBase = TextBaseIn(eventText, TravelText);
        eventText.Entries.RemoveAll(e => e.ID >= textBase && e.ID < textBase + 1000);
        var texts = new Texts(textBase, textBase + 1, textBase + 2,
            [.. areas.Select((_, i) => textBase + 10 + i)], [.. sorted.Select(d => textBase + 100 + d.Index)], textBase + 3);
        eventText.Entries.Add(new FMG.Entry(texts.Travel, TravelText));
        eventText.Entries.Add(new FMG.Entry(texts.Confirm, ConfirmText));
        eventText.Entries.Add(new FMG.Entry(texts.Return, ReturnText));
        eventText.Entries.Add(new FMG.Entry(texts.Back, BackText));
        for (int i = 0; i < areas.Count; i++)
            eventText.Entries.Add(new FMG.Entry(texts.Areas[i], areas[i].Area));
        foreach (var d in sorted)
            eventText.Entries.Add(new FMG.Entry(texts.Bonfires[d.Index], d.Name));
        eventText.Entries.Sort((a, b) => a.ID.CompareTo(b.ID));
        eventFile.Bytes = eventText.Write();

        var esd = ESD.Read(source.Bytes);
        int groups = AddTo(esd, sorted, texts, RunEvents.LeaveDs3Request);
        esd.Name = $"t{TalkId}";
        var script = esd.Write();
        ESD.Read(script);

        var templatePath = er.Entries.Select(e => e.Path).Where(p => p?.StartsWith("/script/talk/") == true && p.EndsWith(".talkesdbnd.dcx") && p != CommonTalk)
            .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault() ?? CommonTalk;
        int binders = 0;
        foreach (var map in sorted.Select(d => d.Er).Distinct())
        {
            var modPath = ctx.ModPath(MapPorter.ModParts(TalkPath(map)));
            var (vanilla, compression) = er.ReadWithCompression(er.Contains(TalkPath(map)) ? TalkPath(map) : templatePath!);
            var bnd = BND4.Read(File.Exists(modPath) ? DCX.Decompress(File.ReadAllBytes(modPath)) : vanilla);
            var flags = bnd.Files.FirstOrDefault()?.Flags ?? Binder.FileFlags.Flag1;
            // Another map's binder is only the container: its own scripts don't belong to this map.
            if (!File.Exists(modPath) && !er.Contains(TalkPath(map)))
                bnd.Files.Clear();
            bnd.Files.RemoveAll(f => ScriptId(f.Name) == TalkId);
            bnd.Files.Add(new BinderFile(flags, 0, $@"N:\GR\data\INTERROOT_win64\script\talk\{map}\t{TalkId}.esd", script));
            bnd.Files = bnd.Files.OrderBy(f => ScriptId(f.Name) ?? int.MaxValue).ToList();
            for (int i = 0; i < bnd.Files.Count; i++)
                bnd.Files[i].ID = i;
            Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
            File.WriteAllBytes(modPath, DCX.Compress(bnd.Write(), compression));
            binders++;
        }

        var modCommon = ctx.ModPath(MapPorter.ModParts(CommonScript));
        var (vanillaCommon, commonCompression) = er.ReadWithCompression(CommonScript);
        var common = EMEVD.Read(File.Exists(modCommon) ? DCX.Decompress(File.ReadAllBytes(modCommon)) : vanillaCommon);
        AddEvents(common, sorted);
        Directory.CreateDirectory(Path.GetDirectoryName(modCommon)!);
        File.WriteAllBytes(modCommon, DCX.Compress(common.Write(), commonCompression));
        Directory.CreateDirectory(Path.GetDirectoryName(modMenuPath)!);
        File.WriteAllBytes(modMenuPath, DCX.Compress(menu.Write(), menuCompression));

        // The pictures and the list the DLL reads. A picture that can't be made leaves its bonfire without one.
        string pictures;
        Dictionary<int, string> images = [];
        try { (images, pictures) = BonfireImages.Write(ctx, ds3, sorted.Select(d => d.Picture)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { pictures = $"no pictures ({ex.GetType().Name}: {ex.Message})"; }
        File.WriteAllLines(ctx.ModPath(ListFile), ListLines(sorted, images));

        return $"bonfire travel: the bonfire menu (t{TalkId}, in {binders} talk binders) lists {sorted.Count} bonfires in {areas.Count} areas ("
               + string.Join("; ", areas.Select(a => $"{a.Area}: {string.Join(", ", a.Bonfires.Select(d => d.Name))}"))
               + $"), {groups} lists added to Elden Ring's {GraceScript}; request flags {RequestFlag(0)}-{RequestFlag(Math.Max(0, sorted.Count - 1))}, "
               + $"text {textBase}+; \"{ReturnText}\" sets the F1 menu's leave request {RunEvents.LeaveDs3Request}; "
               + $"\"{ConfirmText}\" previews {PreviewFlag(0)}-{PreviewFlag(Math.Max(0, sorted.Count - 1))}; {pictures} ({ListFile})";
    }

    /// <summary>
    /// darksign_bonfires.tsv, read by the DLL: "preview flag, area, bonfire, picture file (relative to the mod folder,
    /// empty for none)", one line per bonfire in menu order.
    /// </summary>
    public static IEnumerable<string> ListLines(IReadOnlyList<Destination> sorted, IReadOnlyDictionary<int, string> images) =>
        sorted.Select(d => string.Join('\t', PreviewFlag(d.Index).ToString(), Clean(d.Area), Clean(d.Name), images.GetValueOrDefault(d.Picture, "")))
            .Prepend("# preview flag\tarea\tbonfire\tpicture (BonfireTravel / BonfireImages in the porter)");

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
