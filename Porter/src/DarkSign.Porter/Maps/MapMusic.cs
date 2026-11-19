using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's music on the ported maps: each map's area music (Firelink Shrine) and every boss fight's tracks, phase changes
/// included.
/// <para>How DS3 plays music (its map files and scripts, read with SoulsFormats' MSB3 and DarkScript3's ds3-common
/// definitions): a music track is a Sound region of type 6 (BGM) whose SoundID names the FMOD event m&lt;SoundID:D9&gt;;
/// "child" regions (SoundID 0) widen the area it plays in. The map scripts turn those regions on and off by entity ID
/// with "Enable Boss Map Sound" (2010[04]: a boss fight starts, a phase changes, the boss dies), "Set Map Sound State"
/// (2010[03]) and "Enable Map Sound With Fade" (2010[06]), or play a track by its ID with "Play BGM" (2010[01]).</para>
/// <para>Why Elden Ring's music played instead: Elden Ring has none of those four instructions, so the event port made
/// them no-ops and the region port left the music regions out, and DS3 had no music at all. What Devin heard was Elden
/// Ring's own: its battle music, which a character's NpcParam threatLv starts, and the ported enemies' rows are copies
/// of their Elden Ring templates' rows (fixed in the enemy port: <c>ChrPort.MusicThreatLevel</c>).</para>
/// <para>When DS3 switches them (Devin's 1.0.479 test, "map-diag music" on DS3's files): every map's event 50 turns
/// its music regions off at load. A boss's tracks are turned on by DS3's shared boss events (common_func 20005830-33
/// and 20001835-38), which get the regions as values from the map script: off at the start, the first region on when
/// the fight starts (the player is in the arena and the fight's flag is on), the second on when the phase flag turns
/// on, and "Enable Boss Map Sound" for entity -1, off, when the boss dies: the boss music stops, whichever it is.
/// Firelink Shrine's event turns its track on while the player stands on the shrine's floor. The first port left
/// every line with a passed-in region out and took a region no line turned on for one that plays from the start, so
/// every boss track played on entering its arena, the phase-two track first.</para>
/// <para>What this port does, for every ported map: each music region (a <see cref="Source"/>) gets a "playing" flag,
/// and an event in its map's script turns that flag on while the region is switched on and the player stands in it
/// (the region and its children, carried into the map file as script regions by <see cref="RegionPort"/>), and off
/// again. The DS3 script lines that switch a region set its switch flag instead (on: the region is on); where the
/// region is passed in, the line takes the flag from the same value, and the lines that start the event pass the
/// region's switch flag (<see cref="EventPort.Kind.MusicRegion"/>). The test menu DLL watches the playing
/// flags and plays the DS3 track of the last one turned on, decoded from DS3's sound banks to a WAV file
/// (Mods\ER\darksign_music), looping where DS3's sample loops, fading out when the flag goes off. It plays the music
/// itself (an XAudio2 stream, as the DLL sounds of fallback "sounds" D) instead of through Elden Ring's Wwise banks,
/// because an added bank's loading is unproven in this mod (Elden Ring's music is its own music hierarchy, and loose
/// streamed media stayed silent in game, see <see cref="SoundPort"/>).</para>
/// <para>Flags 1039292000-2099 are in JortPob's reset-on-load range of the run block (<see cref="RunEvents"/>), so a
/// death or a map change stops every track and the map's events start the right one again; the zone events are
/// 1039291000-1099. Off with DARKSIGN_MAP_MUSIC=0 (launcher fallback "map-music" B).</para>
/// </summary>
public static class MapMusic
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DARKSIGN_MAP_MUSIC") != "0";

    /// <summary>
    /// DARKSIGN_MAP_MUSIC=arena (launcher fallback "map-music" D): a boss's tracks get no switch flag, so they play
    /// wherever the player stands in their region, as 1.0.479 did, for when a fight's own lines leave it silent.
    /// </summary>
    public static bool BossTracksUnswitched { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_MAP_MUSIC"), "arena", StringComparison.OrdinalIgnoreCase);

    /// <summary>The list the DLL reads (Mods\ER): playing flag, track file, loop points, fade-out, map, region.</summary>
    public const string ListFile = "darksign_music.tsv";
    /// <summary>Where the tracks go (Mods\ER\darksign_music\m&lt;ID&gt;.wav); must match TestMenu/src/music.cpp.</summary>
    public const string Folder = "darksign_music";

    public const uint FirstFlag = RunEvents.FlagBlock + 2000;
    public const int MaxFlags = 100;
    /// <summary>The numbered flags stop before the spare flag (<see cref="Plan.SpareFlag"/>).</summary>
    private const uint SpareFlagLimit = FirstFlag + MaxFlags - 1;
    public const uint FirstEvent = RunEvents.FlagBlock + 1000;
    public const int MaxEvents = 100;

    /// <summary>Music regions without an entity ID get one at the slot's local number 9700 + n (DS3 maps use none there).</summary>
    public const int ZoneLocal = 9700, MaxZones = 100;

    /// <summary>The DS3 instructions that switch music (bank 2010).</summary>
    public const int PlayBgm = 1, PlaySe = 2, SetMapSoundState = 3, EnableBossMapSound = 4, EnableMapSoundWithFade = 6;

    public const uint BgmSoundType = 6;
    public const uint Player = 10000;

    public static bool IsMusic(MSB3.Region region) => region is MSB3.Region.Sound { SoundType: MSB3.Region.Sound.SndType.BGM };

    public static string SoundName(int soundId) => $"m{soundId:D9}";

    /// <summary>A region with room to stand in (a point has none: its music is heard anywhere while it is on).</summary>
    public static bool HasArea(MSB3.Region region) => region.Shape is not MSB.Shape.Point;

    /// <summary>A region the player is checked against: its DS3 name and its entity ID in the slot.</summary>
    public sealed record Zone(string Ds3Name, uint Entity, bool NewEntity);

    /// <summary>
    /// The script regions the music regions of a DS3 map become (the music regions that have an area and their
    /// children), in map order, each with the entity ID the zone events name: its own DS3 one moved to the slot, else
    /// a new one at <see cref="ZoneLocal"/> + n. Region port and music plan both call this, so they agree.
    /// </summary>
    public static List<(MSB3.Region Region, Zone Zone)> ZoneRegions(MSB3 msb, MapId ds3, MapId er)
    {
        var all = msb.Regions.GetEntries();
        var byName = new Dictionary<string, MSB3.Region>(StringComparer.Ordinal);
        foreach (var r in all)
            byName.TryAdd(r.Name, r);
        var wanted = new List<MSB3.Region>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in all.Where(r => IsMusic(r) && r is MSB3.Region.Sound { SoundID: > 0 }))
            foreach (var z in new[] { r }.Concat(Children(r, byName)))
                if (HasArea(z) && seen.Add(z.Name))
                    wanted.Add(z);
        // DS3 local numbers in use: a new entity ID must not land on one of them.
        var takenLocal = all.Select(r => r.EntityID).Concat(msb.Parts.GetEntries().Select(p => p.EntityID)).Concat(msb.Events.GetEntries().Select(e => e.EntityID))
            .Where(id => id > 0 && ds3.OwnsDs3Id(id)).Select(id => ds3.MoveEntity(id, er) - er.EntityBase).ToHashSet();
        var result = new List<(MSB3.Region, Zone)>();
        int next = 0;
        foreach (var r in wanted)
        {
            if (r.EntityID > 0)
            {
                result.Add((r, new Zone(r.Name, (uint)ds3.MoveEntity(r.EntityID, er), false)));
                continue;
            }
            while (next < MaxZones && takenLocal.Contains(ZoneLocal + next))
                next++;
            if (next >= MaxZones)
                continue; // no number left: its source plays wherever it is on
            result.Add((r, new Zone(r.Name, (uint)(er.EntityBase + ZoneLocal + next), true)));
            next++;
        }
        return result;
    }

    private static IEnumerable<MSB3.Region> Children(MSB3.Region region, IReadOnlyDictionary<string, MSB3.Region> byName) =>
        region is MSB3.Region.Sound sound
            ? sound.ChildRegionNames.Where(n => !string.IsNullOrEmpty(n)).Select(n => byName.GetValueOrDefault(n!)).OfType<MSB3.Region>()
            : [];

    /// <summary>One music track a map can play: a DS3 music region, or a track a "Play BGM" line names.</summary>
    public sealed class Source
    {
        public required MapId Ds3 { get; init; }
        public required MapId Er { get; init; }
        /// <summary>The DS3 region's name, or "Play BGM m...".</summary>
        public required string Name { get; init; }
        /// <summary>The region's DS3 entity ID (0: none, or a Play BGM track).</summary>
        public int Ds3Entity { get; init; }
        public required int SoundId { get; init; }
        public string Sound => SoundName(SoundId);
        public List<Zone> Zones { get; } = [];
        /// <summary>Script lines that switch it on, and off.</summary>
        public int OnLines { get; set; }
        public int OffLines { get; set; }
        public bool Scripted => OnLines + OffLines > 0;
        /// <summary>A boss fight's track: a line names it with "Enable Boss Map Sound" (the fight starts, a phase changes).</summary>
        public bool Boss { get; set; }
        /// <summary>On until a script turns it off: a region no script line turns on (the lines of DS3's shared boss
        /// events counted for each region they are started with). A region some line turns on starts off: DS3's
        /// scripts turn those off at load (event 50 of every map).</summary>
        public bool StartsOn => OnLines == 0;
        /// <summary>The flag the scripts set: on while the region is switched on (set at load to <see cref="StartsOn"/>); 0: none.</summary>
        public uint SwitchFlag { get; set; }
        /// <summary>On while the track should play: what the DLL watches.</summary>
        public uint PlayFlag { get; set; }
        public uint EventId { get; set; }
        public override string ToString() =>
            $"{Ds3} {Name}: {Sound}" + (Ds3Entity > 0 ? $" (entity {Ds3Entity})" : "") +
            $", {(Zones.Count == 0 ? "anywhere" : $"{Zones.Count} zones")}, {(StartsOn ? "on" : "off")} at load" +
            (Scripted ? $", {OnLines} lines turn it on, {OffLines} off" : "") + (Boss ? ", a boss track" : "") +
            $"; flags switch {SwitchFlag} play {PlayFlag}, event {EventId}";
    }

    public sealed class Plan
    {
        public List<Source> Sources { get; } = [];
        public Dictionary<int, Source> ByEntity { get; } = [];
        /// <summary>Play BGM tracks by (DS3 map, sound ID).</summary>
        public Dictionary<(MapId, int), Source> Direct { get; } = [];
        public List<string> Notes { get; } = [];
        /// <summary>Lines that switch a region passed in by the caller: they set the switch flag the caller passes.</summary>
        public int PassedIn { get; set; }
        /// <summary>"Enable Boss Map Sound" for entity -1, off: the boss music stops, whichever it is.</summary>
        public int BossStops { get; set; }
        /// <summary>The switch flags of the boss tracks, first to last (0: none): one line turns them all off.</summary>
        public uint FirstBossSwitch { get; set; }
        public uint LastBossSwitch { get; set; }
        /// <summary>The flag a passed-in region that isn't a music track sets (a wind or voice region): nothing reads it.</summary>
        public const uint SpareFlag = FirstFlag + MaxFlags - 1;

        /// <summary>The switch flag a line that starts an event passes for a DS3 region it passed (see <see cref="EventPort.Kind.MusicRegion"/>).</summary>
        public uint SwitchFor(long ds3Entity) =>
            ds3Entity is > 0 and <= int.MaxValue && ByEntity.TryGetValue((int)ds3Entity, out var s) && s.SwitchFlag != 0 ? s.SwitchFlag : SpareFlag;
        /// <summary>Lines converted: switch flags set, Elden Ring music plays left out.</summary>
        public int Converted { get; set; }
        public int MusicPlaysLeftOut { get; set; }

        public IEnumerable<Source> For(MapId er) => Sources.Where(s => s.Er == er);

        public string Summary =>
            $"music: {Sources.Count} DS3 tracks on {Sources.Select(s => s.Er).Distinct().Count()} maps " +
            $"({Sources.Count(s => s.Scripted)} switched by the scripts, {Sources.Count(s => s.Zones.Count > 0)} played where the player stands, " +
            $"{Sources.Select(s => s.Sound).Distinct().Count()} distinct sounds)";

        /// <summary>
        /// A DS3 music line in Elden Ring's form, or null when it isn't one this port handles (the event port then
        /// treats it as before: no Elden Ring form, a no-op). <paramref name="map"/> is the DS3 map whose script it is
        /// in (null in common_func: only region entity IDs, which name their map, are handled there).
        /// <para>A line that switches a region becomes Set Event Flag on the region's switch flag, with the same state.
        /// Where the region (or the state) is passed in, the new line takes it from the same value
        /// (<paramref name="passedRegionIsMusic"/>: the event's value is known to hold a music region, so the lines
        /// that start the event pass a switch flag there; otherwise the line stays a no-op).</para>
        /// </summary>
        public EMEVD.Instruction? Convert(EMEVD.Instruction ins, Emedf ds3, IReadOnlySet<long> paramBytes, MapId? map, bool passedRegionIsMusic = false)
        {
            if (ins.Bank != 2010 || ds3.Get(ins.Bank, ins.ID) is not { } def)
                return null;
            long Arg(string name) => def.Args.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } a ? Emedf.Read(ins.ArgData, a) : -1;
            bool Passed(string name) => def.Args.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } a && paramBytes.Contains(a.Offset);
            bool anyPassed = paramBytes.Count > 0;
            switch (ins.ID)
            {
                case SetMapSoundState or EnableBossMapSound or EnableMapSoundWithFade:
                {
                    // The state byte is written as it is: a passed-in state lands on it.
                    bool on = Arg("Enable/Disable") != 0;
                    if (Passed("Entity ID"))
                    {
                        if (!passedRegionIsMusic)
                            return null;
                        PassedIn++;
                        return SetFlag(SpareFlag, on);
                    }
                    long entity = Arg("Entity ID");
                    // "Enable Boss Map Sound", entity -1, off: the boss music stops, whichever track it is.
                    if (entity <= 0 && ins.ID == EnableBossMapSound && !Passed("Enable/Disable") && !on)
                    {
                        if (FirstBossSwitch == 0)
                            return null;
                        BossStops++;
                        return RunEvents.Make(2003, 22, FirstBossSwitch, LastBossSwitch, (byte)0);
                    }
                    if (entity <= 0 || !ByEntity.TryGetValue((int)entity, out var source) || source.SwitchFlag == 0)
                        return null;
                    Converted++;
                    return SetFlag(source.SwitchFlag, on);
                }
                case PlayBgm:
                {
                    if (anyPassed || Arg("Sound Type") != BgmSoundType)
                        return null;
                    if (map is not { } m || !Direct.TryGetValue((m, (int)Arg("Sound ID")), out var source) || source.SwitchFlag == 0)
                        return null;
                    Converted++;
                    return SetFlag(source.SwitchFlag, Arg("Should Play") != 0);
                }
                case PlaySe when !Passed("Sound Type") && Arg("Sound Type") == BgmSoundType:
                    // Elden Ring would play its own music track of DS3's number.
                    MusicPlaysLeftOut++;
                    return EventPort.NoOp();
                default:
                    return null;
            }
        }

        /// <summary>
        /// The zone events of one slot's sources, and the constructor lines that start them (for event 0). A source's
        /// event waits until it is switched on and the player is in one of its zones, turns its playing flag on, waits
        /// until either stops and turns it off, and starts over.
        /// </summary>
        public (List<EMEVD.Event> Events, List<EMEVD.Instruction> Starts) Events(MapId er)
        {
            var events = new List<EMEVD.Event>();
            var starts = new List<EMEVD.Instruction>();
            foreach (var s in For(er))
            {
                if (s.EventId == 0 || s.PlayFlag == 0)
                    continue;
                var e = new EMEVD.Event(s.EventId, EMEVD.Event.RestBehaviorType.Default);
                bool switched = s.SwitchFlag != 0, zoned = s.Zones.Count > 0;
                if (!switched && !zoned)
                {
                    // Always on: nothing to wait for.
                    e.Instructions.Add(SetFlag(s.PlayFlag, true));
                    e.Instructions.Add(RunEvents.Make(1000, 4, (byte)0));
                }
                else
                {
                    // Off until its conditions hold (after a load too, whatever the flag was).
                    e.Instructions.Add(SetFlag(s.PlayFlag, false));
                    // Start: switched on AND in a zone.
                    if (switched)
                        e.Instructions.Add(IfFlag(1, true, s.SwitchFlag));
                    if (zoned)
                    {
                        foreach (var z in s.Zones)
                            e.Instructions.Add(IfInArea(-1, true, z.Entity));
                        e.Instructions.Add(IfGroup(1, -1));
                    }
                    e.Instructions.Add(IfGroup(0, 1));
                    e.Instructions.Add(SetFlag(s.PlayFlag, true));
                    // Stop: switched off OR out of every zone.
                    if (switched)
                        e.Instructions.Add(IfFlag(-2, false, s.SwitchFlag));
                    if (zoned)
                    {
                        foreach (var z in s.Zones)
                            e.Instructions.Add(IfInArea(3, false, z.Entity));
                        e.Instructions.Add(IfGroup(-2, 3));
                    }
                    e.Instructions.Add(IfGroup(0, -2));
                    e.Instructions.Add(SetFlag(s.PlayFlag, false));
                    e.Instructions.Add(RunEvents.Make(1000, 4, (byte)1)); // end and restart
                }
                events.Add(e);
                starts.Add(RunEvents.Make(2000, 0, 0, s.EventId, 0u));
            }
            return (events, starts);
        }

        /// <summary>
        /// The lines that put a slot's switch flags in their DS3 start state when the map loads: on for a region
        /// nothing turns on, off for the others (so the state never depends on what the flags were before the load).
        /// </summary>
        public List<EMEVD.Instruction> LoadLines(MapId er) =>
            For(er).Where(s => s.SwitchFlag != 0).Select(s => SetFlag(s.SwitchFlag, s.StartsOn)).ToList();

        /// <summary>
        /// Adds the slot's zone events to its converted script and starts them from event 0, whose first lines put the
        /// switch flags in their start state (before the map's own events run). Returns how many events.
        /// </summary>
        public int AddTo(EMEVD script, MapId er)
        {
            var (events, starts) = Events(er);
            if (events.Count == 0)
                return 0;
            var ids = events.Select(e => e.ID).ToHashSet();
            script.Events.RemoveAll(e => ids.Contains(e.ID));
            var constructor = script.Events.FirstOrDefault(e => e.ID == 0);
            if (constructor is null)
                script.Events.Insert(0, constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
            var load = LoadLines(er);
            constructor.Instructions.InsertRange(0, load);
            foreach (var p in constructor.Parameters)
                p.InstructionIndex += load.Count;
            constructor.Instructions.AddRange(starts);
            script.Events.AddRange(events);
            return events.Count;
        }

        /// <summary>The DLL's list: one line per source (playing flag, file, loop start/end in frames, fade-out ms, map, name).</summary>
        public IEnumerable<string> ListLines(IReadOnlyDictionary<string, Track> tracks) =>
            Sources.Where(s => s.PlayFlag != 0 && tracks.ContainsKey(s.Sound)).Select(s =>
            {
                var t = tracks[s.Sound];
                return string.Join('\t', s.PlayFlag, $"{s.Sound}.wav", t.LoopStart, t.LoopEnd, t.FadeOutMs, s.Er, Clean(s.Name));
            });
    }

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static EMEVD.Instruction SetFlag(uint flag, bool on) => RunEvents.Make(2003, 66, (byte)0, flag, (byte)(on ? 1 : 0));
    private static EMEVD.Instruction IfFlag(sbyte group, bool on, uint flag) => RunEvents.Make(3, 0, group, (byte)(on ? 1 : 0), (byte)0, flag);
    private static EMEVD.Instruction IfInArea(sbyte group, bool inside, uint area) => RunEvents.Make(3, 2, group, (byte)(inside ? 1 : 0), Player, area, 1);
    private static EMEVD.Instruction IfGroup(sbyte group, sbyte target) => RunEvents.Make(0, 0, group, (byte)1, target);

    /// <summary>A DS3 script and the map it belongs to (null: common_func).</summary>
    public sealed record Script(MapId? Map, EMEVD Emevd);

    /// <summary>
    /// The music plan of every ported map: its music regions (with their zones) and Play BGM tracks, which script lines
    /// switch them, and their flag and event numbers (in the slots' order, then map order: the same on every PC).
    /// </summary>
    public static Plan Build(IReadOnlyList<(MapSlots.Slot Slot, MSB3 Msb)> maps, IEnumerable<Script> scripts, Emedf ds3)
    {
        var plan = new Plan();
        var lines = scripts.ToList();
        foreach (var (slot, msb) in maps.Where(m => !m.Slot.Probe))
        {
            var zones = ZoneRegions(msb, slot.Ds3, slot.Er).ToDictionary(z => z.Region.Name, z => z.Zone, StringComparer.Ordinal);
            var byName = new Dictionary<string, MSB3.Region>(StringComparer.Ordinal);
            foreach (var r in msb.Regions.GetEntries())
                byName.TryAdd(r.Name, r);
            foreach (var r in msb.Regions.GetEntries().Where(IsMusic).Cast<MSB3.Region.Sound>().Where(r => r.SoundID > 0))
            {
                var source = new Source { Ds3 = slot.Ds3, Er = slot.Er, Name = r.Name, Ds3Entity = r.EntityID, SoundId = r.SoundID };
                foreach (var z in new MSB3.Region[] { r }.Concat(Children(r, byName)))
                    if (zones.TryGetValue(z.Name, out var zone) && !source.Zones.Contains(zone))
                        source.Zones.Add(zone);
                plan.Sources.Add(source);
                if (r.EntityID > 0 && !plan.ByEntity.TryAdd(r.EntityID, source))
                    plan.Notes.Add($"{slot.Ds3}: two music regions share entity {r.EntityID}; {r.Name} is never switched");
            }
            // Play BGM lines of this map's script name tracks by ID.
            foreach (var script in lines.Where(s => s.Map == slot.Ds3))
                foreach (var (ins, paramBytes) in Instructions(script.Emevd))
                    if (ins.Bank == 2010 && ins.ID == PlayBgm && ds3.Get(2010, PlayBgm) is { } def
                        && Value(ins, def, "Sound Type", paramBytes) == BgmSoundType && Value(ins, def, "Sound ID", paramBytes) is > 0 and < int.MaxValue and var id
                        && !plan.Direct.ContainsKey((slot.Ds3, (int)id)))
                    {
                        var source = new Source { Ds3 = slot.Ds3, Er = slot.Er, Name = $"Play BGM {SoundName((int)id)}", SoundId = (int)id };
                        plan.Direct[(slot.Ds3, (int)id)] = source;
                        plan.Sources.Add(source);
                    }
        }
        // Which lines switch each source: the lines that name its region, and the lines of an event that gets the
        // region passed in, once for every line that starts the event with it (DS3's shared boss events).
        void Count(Source source, int instruction, long state)
        {
            if (state == 0) source.OffLines++;
            else source.OnLines++;
            if (instruction == EnableBossMapSound)
                source.Boss = true;
        }
        // Event -> its lines that switch a passed-in region: (instruction, the value's byte, state or -1, the state's byte or -1).
        var passed = new Dictionary<long, List<(int Instruction, long RegionByte, long State, long StateByte)>>();
        foreach (var script in lines)
            foreach (var e in script.Emevd.Events)
                for (int i = 0; i < e.Instructions.Count; i++)
                {
                    var ins = e.Instructions[i];
                    if (ins.Bank != 2010 || ds3.Get(2010, ins.ID) is not { } def)
                        continue;
                    var paramBytes = e.Parameters.Where(p => p.InstructionIndex == i).ToDictionary(p => p.TargetStartByte, p => p.SourceStartByte);
                    IReadOnlySet<long> targets = paramBytes.Keys.ToHashSet();
                    if (ins.ID is SetMapSoundState or EnableBossMapSound or EnableMapSoundWithFade)
                    {
                        var entityArg = def.Args.FirstOrDefault(a => a.Name.Equals("Entity ID", StringComparison.OrdinalIgnoreCase));
                        var stateArg = def.Args.FirstOrDefault(a => a.Name.Equals("Enable/Disable", StringComparison.OrdinalIgnoreCase));
                        var state = Value(ins, def, "Enable/Disable", targets);
                        if (entityArg is not null && paramBytes.TryGetValue(entityArg.Offset, out var regionByte))
                        {
                            if (!passed.TryGetValue(e.ID, out var list))
                                passed[e.ID] = list = [];
                            list.Add(((int)ins.ID, regionByte, state, stateArg is not null && paramBytes.TryGetValue(stateArg.Offset, out var sb) ? sb : -1));
                            continue;
                        }
                        var entity = Value(ins, def, "Entity ID", targets);
                        if (entity > 0 && plan.ByEntity.TryGetValue((int)entity, out var source))
                            Count(source, ins.ID, state);
                    }
                    else if (ins.ID == PlayBgm && script.Map is { } m && Value(ins, def, "Sound Type", targets) == BgmSoundType
                             && plan.Direct.TryGetValue((m, (int)Math.Clamp(Value(ins, def, "Sound ID", targets), 0, int.MaxValue)), out var direct))
                        Count(direct, ins.ID, Value(ins, def, "Should Play", targets));
                }
        foreach (var script in lines)
            foreach (var e in script.Emevd.Events)
                for (int i = 0; i < e.Instructions.Count; i++)
                {
                    var ins = e.Instructions[i];
                    if (ins.Bank != 2000 || ins.ID is not (0 or 6))
                        continue;
                    int head = ins.ID == 0 ? 8 : 4; // DS3's InitializeCommonEvent has no slot
                    if (ins.ArgData.Length < head || !passed.TryGetValue(BitConverter.ToUInt32(ins.ArgData, head - 4), out var switches))
                        continue;
                    var passedOn = e.Parameters.Where(p => p.InstructionIndex == i).Select(p => p.TargetStartByte).ToHashSet();
                    foreach (var (instruction, regionByte, state, stateByte) in switches)
                    {
                        long at = head + regionByte;
                        if (at + 4 > ins.ArgData.Length || passedOn.Contains(at))
                            continue; // passed on from the starting event's own values: not known here
                        if (!plan.ByEntity.TryGetValue(BitConverter.ToInt32(ins.ArgData, (int)at), out var source))
                            continue;
                        long value = stateByte < 0 ? state
                            : head + stateByte < ins.ArgData.Length && !passedOn.Contains(head + stateByte) ? ins.ArgData[head + stateByte] : 1;
                        Count(source, instruction, value);
                    }
                }
        // Numbers: a switch flag for every source a script switches (the boss tracks' first, in one run, so one line
        // turns them all off), then a playing flag and an event for every source.
        uint flag = FirstFlag, eventId = FirstEvent;
        var numbered = plan.Sources.ToList();
        while (numbered.Count > 0 && (FirstFlag + numbered.Count + numbered.Count(s => s.Scripted) > SpareFlagLimit || numbered.Count > MaxEvents))
        {
            var last = numbered[^1];
            numbered.RemoveAt(numbered.Count - 1);
            plan.Notes.Add($"no flag or event number left for {last.Ds3} {last.Name} ({last.Sound}): it stays silent");
        }
        foreach (var s in numbered.Where(s => s.Scripted && s.Boss && !BossTracksUnswitched))
        {
            s.SwitchFlag = flag++;
            if (plan.FirstBossSwitch == 0)
                plan.FirstBossSwitch = s.SwitchFlag;
            plan.LastBossSwitch = s.SwitchFlag;
        }
        foreach (var s in numbered.Where(s => s.Scripted && !s.Boss))
            s.SwitchFlag = flag++;
        foreach (var s in numbered)
        {
            s.PlayFlag = flag++;
            s.EventId = eventId++;
        }
        return plan;
    }

    /// <summary>A literal argument's value, or -1 when it is passed in (or missing).</summary>
    private static long Value(EMEVD.Instruction ins, Emedf.Instr def, string name, IReadOnlySet<long> paramBytes) =>
        def.Args.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } a && !paramBytes.Contains(a.Offset) ? Emedf.Read(ins.ArgData, a) : -1;

    private static IEnumerable<(EMEVD.Instruction Ins, IReadOnlySet<long> ParamBytes)> Instructions(EMEVD script)
    {
        foreach (var e in script.Events)
            for (int i = 0; i < e.Instructions.Count; i++)
                yield return (e.Instructions[i], e.Parameters.Where(p => p.InstructionIndex == i).Select(p => p.TargetStartByte).ToHashSet());
    }

    // ---------------------------------------------------------------- the tracks

    /// <summary>A decoded track: loop points in frames (-1: plays once), DS3's fade-out, and how it was found.</summary>
    public sealed record Track(int LoopStart, int LoopEnd, int FadeOutMs, string How);

    /// <summary>The fade-out when DS3's event has none.</summary>
    public const int DefaultFadeOutMs = 2000;

    /// <summary>
    /// Mixes a track's layers (FMOD plays an event's layers together) into one: layers of another channel count or
    /// rate than the first are left out. <paramref name="gains"/> are linear volumes. Clips at 16 bits.
    /// </summary>
    public static (short[] Pcm, int Dropped) Mix(IReadOnlyList<(short[] Pcm, int Channels, int Rate)> layers, IReadOnlyList<float> gains)
    {
        if (layers.Count == 0)
            return ([], 0);
        int channels = layers[0].Channels, rate = layers[0].Rate, dropped = 0;
        var use = new List<int>();
        for (int i = 0; i < layers.Count; i++)
            if (layers[i].Channels == channels && layers[i].Rate == rate) use.Add(i);
            else dropped++;
        int length = use.Max(i => layers[i].Pcm.Length);
        if (use.Count == 1 && Math.Abs(gains[use[0]] - 1f) < 1e-4f)
            return (layers[use[0]].Pcm, dropped);
        var mixed = new short[length];
        for (int n = 0; n < length; n++)
        {
            float sum = 0;
            foreach (var i in use)
                if (n < layers[i].Pcm.Length)
                    sum += layers[i].Pcm[n] * gains[i];
            mixed[n] = (short)Math.Clamp(MathF.Round(sum), short.MinValue, short.MaxValue);
        }
        return (mixed, dropped);
    }

    /// <summary>
    /// Decodes every distinct track of the plan to Mods\ER\darksign_music\&lt;sound&gt;.wav (DS3's event file says which
    /// samples, level and loop; without an event, the samples named like the sound), and writes the DLL's list.
    /// Returns the report lines.
    /// </summary>
    public static string WriteAudio(PorterContext ctx, GameArchives ds3, Plan plan)
    {
        var folder = ctx.ModPath(Folder);
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        var listPath = ctx.ModPath(ListFile);
        if (File.Exists(listPath))
            File.Delete(listPath);
        var lines = new List<string> { plan.Summary };
        lines.AddRange(plan.Notes.Select(n => "music: " + n));
        lines.Add($"music: {plan.Converted} script lines switch a track by name, {plan.PassedIn} switch the track their event is started with " +
                  $"(DS3's shared boss events), {plan.BossStops} stop the boss music, {plan.MusicPlaysLeftOut} Elden Ring music plays left out; " +
                  $"boss switch flags {plan.FirstBossSwitch}-{plan.LastBossSwitch}");
        // What went wrong in 1.0.479: a track no line turns on plays wherever the player stands in its region.
        if (BossTracksUnswitched)
            lines.Add("music: boss tracks play wherever the player stands in their arena (DARKSIGN_MAP_MUSIC=arena, fallback map-music D)");
        foreach (var s in plan.Sources.Where(s => s.Scripted && s.StartsOn))
            lines.Add($"music: WARN no script line turns on {s.Ds3} {s.Name} ({s.Sound}): it plays from the load until a line turns it off");
        var sounds = plan.Sources.Where(s => s.PlayFlag != 0).Select(s => s.Sound).Distinct().Order(StringComparer.Ordinal).ToList();
        if (sounds.Count == 0)
        {
            File.WriteAllLines(listPath, []);
            return string.Join("\n  ", lines);
        }
        Directory.CreateDirectory(folder);
        var events = SoundPort.Ds3Events(ctx, ds3);
        var samples = new SampleSource(ctx, ds3);
        var tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        var missing = new List<string>();
        long bytes = 0;
        foreach (var sound in sounds)
        {
            try
            {
                var layers = new List<(Fsb5.Sample Sample, float Gain, bool Loops)>();
                int fade = DefaultFadeOutMs;
                string how;
                bool eventLoops = false;
                if (events.TryGetValue(sound, out var found))
                {
                    var (ev, fsb) = found;
                    fade = ev.FadeOutMs > 0 ? ev.FadeOutMs : DefaultFadeOutMs;
                    eventLoops = !ev.OneShot;
                    foreach (var layer in ev.Layers)
                    {
                        var wave = layer.Waves.Select(Fev.SampleName).FirstOrDefault(w => samples.Find(w, fsb) is not null);
                        if (wave is not null)
                            layers.Add((samples.Find(wave, fsb)!, Math.Clamp(ev.Volume * layer.Volume, 0f, 4f), layer.Loops));
                    }
                    how = $"event in {fsb}, {layers.Count}/{ev.Layers.Count} layers";
                }
                else
                    how = "no DS3 event";
                if (layers.Count == 0 && samples.Named(sound) is { } named)
                {
                    layers.Add((named.Sample, 1f, named.Sample.LoopStart >= 0));
                    how += $", sample {named.Sample.Name} in {named.Fsb}";
                }
                if (layers.Count == 0)
                {
                    missing.Add(sound);
                    continue;
                }
                var decoded = layers.Select(l => (Fsb5.DecodePcm(l.Sample), l.Sample.Channels, l.Sample.Rate)).ToList();
                var (pcm, dropped) = Mix(decoded, layers.Select(l => l.Gain).ToList());
                var first = layers[0].Sample;
                int frames = first.Channels > 0 ? pcm.Length / first.Channels : 0;
                bool loops = eventLoops || layers.Any(l => l.Loops);
                var points = SoundPort.LoopPoints(first);
                int loopStart = loops ? Math.Clamp(points?.Start ?? 0, 0, Math.Max(0, frames - 1)) : -1;
                int loopEnd = loops ? Math.Clamp(points?.End ?? frames - 1, loopStart, Math.Max(0, frames - 1)) : -1;
                var wav = SoundPort.Wav(pcm, first.Channels, first.Rate);
                File.WriteAllBytes(Path.Combine(folder, sound + ".wav"), wav);
                bytes += wav.Length;
                tracks[sound] = new Track(loopStart, loopEnd, fade, how + (dropped > 0 ? $", {dropped} layers of another format left out" : ""));
                lines.Add($"music: {sound} {(first.Rate > 0 ? frames / first.Rate : 0)} s {first.Channels} ch {first.Rate} Hz, " +
                          (loops ? $"loops {loopStart}-{loopEnd}" : "plays once") + $", fade-out {fade} ms ({tracks[sound].How})");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                missing.Add($"{sound} ({e.GetType().Name}: {e.Message})");
            }
        }
        var list = plan.ListLines(tracks).ToList();
        File.WriteAllLines(listPath, list);
        lines.Add($"music: {tracks.Count}/{sounds.Count} tracks decoded ({bytes / (1024 * 1024)} MB in Mods\\ER\\{Folder}), {list.Count} lines in {ListFile}" +
                  (missing.Count > 0 ? $"; FAIL no DS3 audio for: {string.Join(", ", missing.Take(12))}" : ""));
        foreach (var s in plan.Sources)
            lines.Add("music: " + s + (tracks.ContainsKey(s.Sound) ? "" : " (no audio)"));
        return string.Join("\n  ", lines);
    }

    /// <summary>DS3's FSB banks, read when first asked for (loose files first, encrypted ones decrypted).</summary>
    private sealed class SampleSource(PorterContext ctx, GameArchives ds3)
    {
        private readonly Dictionary<string, Dictionary<string, Fsb5.Sample>?> _banks = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, (string Fsb, Fsb5.Sample Sample)>? _all;

        private byte[]? Bytes(string fsb)
        {
            var loose = Path.Combine(ctx.GameDir(PorterGame.DarkSouls3), "sound", fsb + ".fsb");
            byte[]? bytes = File.Exists(loose) ? File.ReadAllBytes(loose) : ds3.Contains($"/sound/{fsb}.fsb") ? ds3.Read($"/sound/{fsb}.fsb") : null;
            return bytes is not null && !Fsb5.Is(bytes) ? Fsb5.Decrypt(bytes) : bytes;
        }

        public Fsb5.Sample? Find(string name, string fsb)
        {
            if (!_banks.TryGetValue(fsb, out var bank))
            {
                try
                {
                    bank = Bytes(fsb) is { } b ? Fsb5.Read(b).Samples.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) : null;
                }
                catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
                {
                    bank = null;
                }
                _banks[fsb] = bank;
            }
            return bank?.GetValueOrDefault(name) ?? Named(name)?.Sample;
        }

        /// <summary>The first sample of this name (or with a variant letter) in any DS3 bank.</summary>
        public (string Fsb, Fsb5.Sample Sample)? Named(string name)
        {
            if (_all is null)
            {
                _all = new(StringComparer.OrdinalIgnoreCase);
                var names = new List<string>();
                var loose = Path.Combine(ctx.GameDir(PorterGame.DarkSouls3), "sound");
                if (Directory.Exists(loose))
                    names.AddRange(Directory.EnumerateFiles(loose, "*.fsb").Select(Path.GetFileNameWithoutExtension).OfType<string>());
                names.AddRange(ds3.Entries.Where(e => e.Path?.StartsWith("/sound/") == true && e.Path.EndsWith(".fsb")).Select(e => Path.GetFileNameWithoutExtension(e.Path!)));
                foreach (var fsb in names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Bytes(fsb) is not { } b)
                            continue;
                        // Only music samples are kept (m + digits): the banks' other samples would fill memory.
                        foreach (var s in Fsb5.Read(b, n => n.Length >= 10 && (n[0] is 'm' or 'M') && char.IsDigit(n[1])).Samples)
                            _all.TryAdd(s.Name, (fsb, s));
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
                    {
                    }
                }
            }
            foreach (var suffix in new[] { "", "a", "b", "_a", "_00" })
                if (_all.TryGetValue(name + suffix, out var hit))
                    return hit;
            return null;
        }
    }
}
