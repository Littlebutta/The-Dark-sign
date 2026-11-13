using System.Collections.Concurrent;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// What a ported DS3 enemy shows, sounds like and can do beyond its attacks' damage, for every enemy the same way
/// (Devin, 1.0.299 playtest: the Jailer has no lantern glow or weapon effects, no laugh and no health drain, and
/// "this kind of deep work shouldn't need to be done for each enemy"). Until now a ported enemy kept its Elden Ring
/// template's effect and sound events on DS3's animations, none of DS3's SpEffects, and its behaviour rows that fire
/// a projectile or cast an effect were the template's or left out.
/// <list type="bullet">
/// <item>Abilities: the SpEffects DS3's NpcParam row gives the enemy (the Jailer's lantern glow is one, 11900), the
/// ones its moves cast and its hits apply, and the projectiles its moves fire (the Jailer's health drain is a mist
/// bullet, behaviours 222300200 / 210, whose hit applies "HP Reduction" 11880), with the effects those chain to.
/// The weapon port's SpEffect, visual row and bullet ports do the rows; an enemy effect's new ID is
/// 700,000,000 + its DS3 ID, so a move can name it before the row exists.</item>
/// <item>Visual effects: the effect events of DS3's moves, and every effect the rows above show, converted into
/// the character's own effect binder (<see cref="FxrPort.ChrConverter"/>).</item>
/// <item>Sounds: the character and effect sounds DS3's moves and effects play, in a sound bank of the character's
/// own (<see cref="ChrSounds"/>).</item>
/// </list>
/// In a move that plays a DS3 animation the effect events are DS3's alone (the template's, stretched onto another
/// animation, are dropped), and so are the character's own sounds once it has a bank.
/// Fallbacks "enemy-effects": DARKSIGN_ENEMY_EFFECTS=0 (all of this off, as builds up to 1.0.299),
/// DARKSIGN_ENEMY_SOUNDS=0 (no DS3 sounds: the template's stay). Report: Work\reports\enemy-effects.tsv.
/// Method (RULE 3): the event layouts are DSAnimStudio's TAE templates for both games, the param relations
/// sn0wsh00's FXR / bullet articles and Paramdex (see VfxPort, BulletPort), the bank layout the community's custom
/// sound banks (see WwiseBank.WriteOwnBank).
/// </summary>
public sealed class ChrEffects
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_EFFECTS") != "0";
    public static bool SoundsEnabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_SOUNDS") != "0";
    private static readonly bool OneShot = Environment.GetEnvironmentVariable("DARKSIGN_SOUND_ONESHOT") != "0";

    public const int SpEffectBase = 700_000_000;
    public const string ReportFile = "enemy-effects.tsv";
    private const int BulletEvent = 2;

    /// <summary>The Elden Ring ID of a DS3 SpEffect an enemy uses (DS3's go up to 160,000,000; Elden Ring's own stay below 100,000,000).</summary>
    public static int SpEffectId(int ds3Id) => ds3Id is > 0 and < 300_000_000 ? SpEffectBase + ds3Id : -1;

    /// <summary>TAE events that cast SpEffects: Elden Ring's parameter length and where the IDs are (see MovesetPort.SpEffectEvents).</summary>
    internal static readonly Dictionary<int, (int Length, int[] Ids)> SpEffectEvents = new()
    {
        [66] = (8, [0]), [67] = (8, [0]), [302] = (16, [0]), [331] = (16, [0, 4]), [401] = (8, [0]),
    };

    /// <summary>The SpawnFFX family (see <see cref="EffectEvents"/>): 112 spawns by floor material and 113 has no ID.</summary>
    public static bool IsEffectType(int type) => type is >= 95 and <= 121 and not 112 and not 113;

    public static bool IsSoundType(int type) => type is >= 128 and <= 131;

    /// <summary>Sound kinds that belong to a creature: its own sounds (1 c), effect sounds (5 s) and voice (7 v).</summary>
    public static bool IsOwnSoundKind(int kind) => kind is 1 or 5 or 7;

    private readonly PorterContext _ctx;
    private readonly GameArchives? _er;
    private readonly PARAM? _ds3Npc, _erNpc, _ds3SpEffects;
    private readonly Ds3EffectRefs? _refs;
    private readonly EffectEvents _events;
    private readonly FxrPort.ChrConverter _converter;
    private readonly FxrPort.ChrBinderLayout _layout;
    private readonly SpEffectPort? _planned;
    private readonly ChrSounds? _sounds;
    private readonly ScriptEffects? _scripts;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<int, Built> _built = new();
    private readonly ConcurrentDictionary<int, int> _ported = new();

    /// <summary>An event group of each type and the parameter lengths Elden Ring uses for it, from the player's and the templates' TAEs.</summary>
    internal Dictionary<int, TAE.EventGroup> Groups { get; } = [];
    internal Dictionary<int, HashSet<int>> Lengths { get; } = [];
    private readonly byte[]? _swingSample, _soundSample;

    /// <summary>What one character got, for the params step and the report.</summary>
    public sealed class Built
    {
        public required ChrPort.Plan Plan { get; init; }
        public bool HasBinder, BinderCreated;
        public int EffectsWanted, EffectsPorted;
        public Dictionary<string, int> EffectReasons = [];
        public HashSet<int> Cast = [];
        public int CastPorted;
        public ChrSounds.Result? Sounds;
        public int EventsAdded, EventsDropped, TemplateDropped;
        public Dictionary<string, int> Events = [];
        public int NpcEffects, NpcEffectsSkipped;
        /// <summary>DS3 effects its row names that DS3's scripts only take off (left out), and ones they only put on (added).</summary>
        public List<int> ScriptDropped = [], ScriptAdded = [], CategoryDropped = [];
        /// <summary>AI state markers DS3's scripts put on, left out of the row (<see cref="ScriptMarkersLeftOut"/>).</summary>
        public List<int> ScriptMarkers = [];
        public string Note = "";
    }

    public IReadOnlyDictionary<int, Built> Characters => _built;

    /// <summary>Files written straight into the mod folder (the sound banks), for the enemy port's manifest.</summary>
    public ConcurrentBag<string> Written { get; } = [];

    private ChrEffects(PorterContext ctx, GameArchives? er, PARAM? erNpc, PARAM? ds3Npc, PARAM? ds3SpEffects, Ds3EffectRefs? refs, EffectEvents events,
        FxrPort.ChrConverter converter, FxrPort.ChrBinderLayout layout, SpEffectPort? planned, ChrSounds? sounds, byte[]? swingSample, byte[]? soundSample,
        ScriptEffects? scripts = null)
    {
        _scripts = scripts;
        _ctx = ctx;
        _er = er;
        _erNpc = erNpc;
        _ds3Npc = ds3Npc;
        _ds3SpEffects = ds3SpEffects;
        _refs = refs;
        _events = events;
        _converter = converter;
        _layout = layout;
        _planned = planned;
        _sounds = sounds;
        _swingSample = swingSample;
        _soundSample = soundSample;
    }

    /// <summary>
    /// Reads what every character's port shares: which event parameters hold effect IDs (from the player's and every
    /// planned enemy's TAE of both games), Elden Ring's event groups and lengths, DS3's effect files, the layout of
    /// Elden Ring's character effect binders, and the sound bank reader. Null (with a note) when it is switched off
    /// or something it needs is missing: the enemies then keep their template's effects and sounds.
    /// </summary>
    public static ChrEffects? Create(PorterContext ctx, GameArchives er, GameArchives ds3, IReadOnlyList<ChrPort.Plan> plans, List<string> notes)
    {
        if (!Enabled)
        {
            Console.WriteLine("enemies: DS3 effects, sounds and abilities are off (DARKSIGN_ENEMY_EFFECTS=0): the templates' stay");
            return null;
        }
        try
        {
            TAE.ValidateEventBank = false;
            var full = plans.Where(p => p.Method == ChrPort.Method.Full).ToList();
            BND4 Taes(GameArchives archives, IEnumerable<string> paths)
            {
                var all = new ConcurrentBag<BinderFile>();
                Parallel.ForEach(paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(archives.Contains), Parallelism.Options, path =>
                {
                    try
                    {
                        foreach (var f in BND4.Read(archives.Read(path)).Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
                            all.Add(new BinderFile(f.Flags, 0, $"{path}|{f.Name}", f.Bytes));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { }
                });
                var bnd = new BND4();
                bnd.Files.AddRange(all.OrderBy(f => f.Name, StringComparer.Ordinal));
                return bnd;
            }
            var ds3Paths = full.SelectMany(p => ChrPort.Ds3AnimationSources(p.Ds3.Chr)).Select(c => $"/chr/c{c:D4}.anibnd.dcx").Prepend("/chr/c0000.anibnd.dcx");
            var erPaths = full.Select(p => $"/chr/c{p.TemplateChr:D4}.anibnd.dcx").Prepend("/chr/c0000.anibnd.dcx");
            var ds3Taes = Taes(ds3, ds3Paths);
            var erTaes = Taes(er, erPaths);
            var ds3Fx = FxrPort.Ds3Index(ds3);
            var events = EffectEvents.Find(ds3Taes, erTaes, ds3Fx);
            var layout = new FxrPort.ChrBinderLayout(FxrPort.ErChrBinders(er));
            if (!layout.CanCreate)
                throw new InvalidDataException("Elden Ring has no character effect binder to take the layout from");
            var erEvents = new List<TAE.Event>();
            foreach (var f in erTaes.Files)
                try { erEvents.AddRange(TAE.Read(f.Bytes).Animations.SelectMany(a => a.Events)); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            var erParams = ctx.LoadParams(PorterGame.EldenRing);
            var ds3Params = ctx.LoadParams(PorterGame.DarkSouls3);
            SpEffectPort? planned = null;
            if (SpEffectPort.Enabled)
                planned = new SpEffectPort(erParams, ds3Params, SpEffectId, full: true);
            // The layouts a ported sound event takes: Elden Ring's most common one-shot character sound (see
            // MovesetPort.SoundEvents) and its most common 128.
            var swing = erEvents.Where(e => e.Type == 129 && SoundPort.Sound(e) is { Kind: 1 }).Select(e => e.GetParameterBytes(false))
                .Where(b => b.Length >= 32).GroupBy(Convert.ToHexString).MaxBy(g => g.Count())?.First();
            var sound = erEvents.Where(e => e.Type == 128).Select(e => e.GetParameterBytes(false)).GroupBy(Convert.ToHexString).MaxBy(g => g.Count())?.First();
            ChrSounds? sounds = null;
            if (SoundsEnabled && (swing ?? sound) is not null)
                try
                {
                    var templateIds = erEvents.Select(SoundPort.Sound).Where(s => s is { Kind: 1 }).GroupBy(s => s!.Value.Id)
                        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).Take(50).ToList();
                    sounds = ChrSounds.Create(ctx, er, ds3, templateIds);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    notes.Add($"enemy sounds: off, the templates' sounds stay ({ChrPort.Describe(e)})");
                }
            else if (!SoundsEnabled)
                Console.WriteLine("enemies: DS3 sounds are off (DARKSIGN_ENEMY_SOUNDS=0): the templates' sounds stay");
            var scripts = ScriptEffects.Read(ds3, notes);
            var fx = new ChrEffects(ctx, er, erParams.Get("NpcParam"), ds3Params.Get("NpcParam"), ds3Params.Get("SpEffectParam"),
                new Ds3EffectRefs(ds3Params, "BehaviorParam", "AtkParam_Npc"), events, new FxrPort.ChrConverter(ds3Fx, er), layout, planned, sounds, swing, sound, scripts);
            foreach (var g in erEvents.Where(e => e.Group is not null).GroupBy(e => e.Type))
                fx.Groups[g.Key] = g.First().Group!;
            foreach (var g in erEvents.GroupBy(e => e.Type))
                fx.Lengths[g.Key] = g.Select(e => e.GetParameterBytes(false).Length).ToHashSet();
            Console.WriteLine($"enemies: effects: {ds3Taes.Files.Count} DS3 and {erTaes.Files.Count} Elden Ring TAE files read for event layouts; " +
                              $"SpEffects {(planned is null ? "off" : "on")}, sounds {(sounds is null ? "off" : "on")}");
            return fx;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            notes.Add($"enemy effects: off, the templates' effects and sounds stay ({ChrPort.Describe(e)})");
            return null;
        }
    }

    /// <summary>For the self-tests: the port over params and effect files built in memory, with no game behind it.</summary>
    internal static ChrEffects ForTest(PorterContext ctx, PARAM? ds3Npc, PARAM? ds3SpEffects, EffectEvents events, FxrPort.ChrConverter converter,
        FxrPort.ChrBinderLayout layout, SpEffectPort? planned, byte[]? swingSample, IEnumerable<TAE.Event> erEvents, ScriptEffects? scripts = null)
    {
        var fx = new ChrEffects(ctx, null, null, ds3Npc, ds3SpEffects, null, events, converter, layout, planned, null, swingSample, null, scripts);
        foreach (var g in erEvents.GroupBy(e => e.Type))
            fx.Lengths[g.Key] = g.Select(e => e.GetParameterBytes(false).Length).ToHashSet();
        return fx;
    }

    /// <summary>For the self-tests: the params step with a given SpEffect port.</summary>
    internal void UseForTest(SpEffectPort sp) => _sp = sp;

    // ---- Files (port-maps step 1, beside port-weapons) ----

    /// <summary>One character's effects: converts and files them, then converts its moves' events.</summary>
    public sealed class Session
    {
        private readonly ChrEffects _fx;
        public Built Built { get; }
        public FxrPort.Result Effects { get; }
        private readonly Dictionary<int, int> _cast;
        private readonly ChrSounds.Result? _sounds;
        internal Session(ChrEffects fx, Built built, FxrPort.Result effects, Dictionary<int, int> cast, ChrSounds.Result? sounds)
        {
            _fx = fx;
            Built = built;
            Effects = effects;
            _cast = cast;
            _sounds = sounds;
        }

        /// <summary>Whether the character has a bank of DS3 sounds (then its own sounds are DS3's alone).</summary>
        public bool HasSounds => _sounds is { Ported.Count: > 0 };

        public enum Fate { Other, Drop, Add }

        /// <summary>Whether a template event is dropped from a move that now plays a DS3 animation.</summary>
        public bool DropsTemplate(TAE.Event e)
        {
            bool drop = IsEffectType(e.Type) || (HasSounds && IsSoundType(e.Type) && SoundPort.Sound(e) is { } s && IsOwnSoundKind(s.Kind));
            if (drop)
                Built.TemplateDropped++;
            return drop;
        }

        /// <summary>
        /// A DS3 event for Elden Ring: an effect event with its converted effects, a SpEffect event with the ported
        /// effects' IDs, a projectile event in Elden Ring's layout, a sound event playing the ported sound. Drop: it
        /// is one of those and can't be carried (a DS3 ID left in would name something unrelated). Other: not this
        /// port's business.
        /// </summary>
        public (Fate Fate, int Type, byte[]? Bytes) Convert(TAE.Event e)
        {
            var result = ConvertEvent(e);
            if (result.Fate != Fate.Other)
            {
                var what = e.Type == BulletEvent ? "projectile" : IsEffectType(e.Type) ? "effect" : IsSoundType(e.Type) ? "sound" : "SpEffect";
                var key = $"{what} {(result.Fate == Fate.Add ? "added" : "dropped")}";
                Built.Events[key] = Built.Events.GetValueOrDefault(key) + 1;
                if (result.Fate == Fate.Add) Built.EventsAdded++;
                else Built.EventsDropped++;
            }
            return result;
        }

        private (Fate Fate, int Type, byte[]? Bytes) ConvertEvent(TAE.Event e)
        {
            var bytes = e.GetParameterBytes(false);
            if (IsEffectType(e.Type))
                return _fx._events.Convert(e, Effects) is { } fxBytes ? (Fate.Add, e.Type, fxBytes) : (Fate.Drop, e.Type, null);
            if (SpEffectEvents.TryGetValue(e.Type, out var layout))
            {
                var er = Resize(bytes, layout.Length);
                if (er is null)
                    return (Fate.Drop, e.Type, null);
                bool any = false;
                foreach (var offset in layout.Ids)
                {
                    if (offset + 4 > bytes.Length || BitConverter.ToInt32(bytes, offset) is not (> 0 and var id))
                        continue;
                    if (!_cast.TryGetValue(id, out var newId) || newId <= 0)
                        return (Fate.Drop, e.Type, null);
                    BitConverter.GetBytes(newId).CopyTo(er, offset);
                    any = true;
                }
                return any ? (Fate.Add, e.Type, er) : (Fate.Drop, e.Type, null);
            }
            if (e.Type == BulletEvent)
            {
                // The behaviour judge it names is kept; the row it finds is ported with the character's variation.
                if (!BulletPort.Enabled || !_fx.Lengths.TryGetValue(BulletEvent, out var lengths) || !lengths.Contains(bytes.Length))
                    return (Fate.Drop, e.Type, null);
                return (Fate.Add, e.Type, BulletPort.ConvertEvent(bytes));
            }
            if (IsSoundType(e.Type) && SoundPort.Sound(e) is { } s && IsOwnSoundKind(s.Kind))
            {
                if (!HasSounds)
                    return (Fate.Other, e.Type, null); // the template's sound events stay as they are
                if (_sounds!.Map(s.Kind, s.Id) is not { } id)
                    return (Fate.Drop, e.Type, null);
                return _fx.SoundEvent(s.Kind, id, _sounds.Loops(s.Kind, s.Id)) is { } sound ? (Fate.Add, sound.Type, sound.Bytes) : (Fate.Drop, e.Type, null);
            }
            return (Fate.Other, e.Type, null);
        }

        // DS3's parameters at Elden Ring's length: zero padded, or cut when only zeros go.
        private static byte[]? Resize(byte[] bytes, int length)
        {
            if (bytes.Length == length)
                return (byte[])bytes.Clone();
            if (bytes.Length < length)
                return [.. bytes, .. new byte[length - bytes.Length]];
            return bytes.AsSpan(length).IndexOfAnyExcept((byte)0) < 0 ? bytes[..length] : null;
        }
    }

    /// <summary>
    /// A ported sound as an Elden Ring sound event: the game's own one-shot Wwise_PlaySound_BySlot (129) layout the
    /// weapon port found to play new sounds (see MovesetPort.SoundEvents), else the most common 128. loops: the
    /// sound loops until it is stopped, so it ends with the event as Elden Ring's own long sound events do.
    /// </summary>
    internal (int Type, byte[] Bytes)? SoundEvent(int kind, int id, bool loops = false)
    {
        int type = OneShot && _swingSample is not null ? 129 : 128;
        var layout = type == 129 ? _swingSample : _soundSample;
        if (layout is null || layout.Length < 8)
            return null;
        var bytes = (byte[])layout.Clone();
        BitConverter.GetBytes(kind).CopyTo(bytes, 0);
        BitConverter.GetBytes(id).CopyTo(bytes, 4);
        if (type == 129 && bytes.Length >= 32)
        {
            BitConverter.GetBytes((short)0).CopyTo(bytes, 16); // StateInfo
            BitConverter.GetBytes(kind).CopyTo(bytes, 20);     // override sound: the ported one itself
            BitConverter.GetBytes(id).CopyTo(bytes, 24);
            bytes[28] = unchecked((byte)-1);                   // no weapon offset
            BitConverter.GetBytes(-1).CopyTo(bytes, 12);       // SlotID: none
            bytes[18] = loops ? (byte)1 : (byte)0;             // PlaybackBehaviorType: until the sound ends (a loop: with the event)
        }
        return (type, bytes);
    }

    private static string SfxBinder(int chr) => $"/sfx/sfxbnd_c{chr:D4}.ffxbnd.dcx";

    /// <summary>
    /// Everything one fully ported character shows and sounds like, before its moves are merged: follows DS3's
    /// params from its NpcParam rows and its moves' events to the effects it needs, files them in its effect binder
    /// (the template's, cloned; a new one when the template has none), decides which SpEffects its moves cast are
    /// ported, and builds its sound bank.
    /// </summary>
    public Session Open(ChrPort.Plan plan, Dictionary<string, (BND4 Bnd, DCX.CompressionInfo Compression)> binders, IReadOnlyList<TAE.Event> ds3Events,
        List<string> notes)
    {
        var built = new Built { Plan = plan };
        var effects = new FxrPort.Result();
        var cast = new Dictionary<int, int>();
        var refs = _refs?.Fresh();
        foreach (var npcId in plan.Ds3.NpcParams)
            if (refs is not null && _ds3Npc?[npcId] is { } row)
            {
                for (int k = 0; k < 32; k++)
                    refs.AddSpEffect(ParamDefaults.Int(row, $"spEffectId{k}"));
                // Effects DS3's map scripts put on it (Pontiff's sword auras): their effects go in its binder too.
                foreach (var id in _scripts?.Set(npcId) ?? new HashSet<int>())
                    refs.AddSpEffect(id);
                refs.AddVariation(ParamDefaults.Int(row, "behaviorVariationId"));
            }
        built.Cast = MovesetPort.TaeSpEffects(ds3Events).ToHashSet();
        foreach (var id in built.Cast)
            refs?.AddSpEffect(id);
        var wanted = _events.Ids(ds3Events).Concat(refs?.Fxr ?? []).Where(id => id > 0).ToHashSet();
        built.EffectsWanted = wanted.Count;
        lock (_lock)
        {
            if (wanted.Count > 0)
            {
                var key = SfxBinder(plan.NewChr);
                bool had = binders.TryGetValue(key, out var binder);
                if (!had)
                    binder = OwnBinder(plan);
                effects = _converter.AddTo(binder.Bnd, wanted, _layout);
                built.EffectsPorted = wanted.Count(effects.Effects.ContainsKey); // not counting the effects those play
                foreach (var g in effects.Reasons.Values.GroupBy(r => r.StartsWith("plays f", StringComparison.Ordinal) ? "plays an effect that was left out" : r))
                    built.EffectReasons[g.Key] = g.Count();
                if (effects.Effects.Count > 0)
                {
                    binders[key] = binder;
                    built.HasBinder = true;
                    built.BinderCreated = !had;
                    foreach (var (ds3Id, newId) in effects.Effects)
                        _ported[ds3Id] = newId;
                }
            }
            // The SpEffects its moves cast: a script marker ("[HKS] ...") means nothing to Elden Ring's scripts.
            foreach (var id in built.Cast)
            {
                var name = (_ds3SpEffects?[id]?.Name ?? "").TrimStart();
                cast[id] = name.StartsWith("[HKS]", StringComparison.OrdinalIgnoreCase) ? -1 : _planned?.Port(id) ?? -1;
            }
            built.CastPorted = cast.Count(c => c.Value > 0);
        }
        ChrSounds.Result? sounds = null;
        if (_sounds is not null)
        {
            var wantedSounds = ds3Events.Where(e => IsSoundType(e.Type)).Select(SoundPort.Sound).OfType<(int Kind, int Id)>()
                .Concat(effects.Sounds.Select(id => (5, id))).Where(s => SoundPort.Letter(s.Item1) is not null).Distinct().ToList();
            try
            {
                sounds = _sounds.Build(plan, wantedSounds, effects.UnheldSounds);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                notes.Add($"sounds: not ported, the template's stay ({ChrPort.Describe(e)})");
            }
        }
        built.Sounds = sounds;
        var session = new Session(this, built, effects, cast, sounds);
        if (sounds?.BankFile is { } bankFile)
            Written.Add(bankFile);
        notes.Add($"effects: {built.EffectsPorted} of {built.EffectsWanted} DS3 effects in {(built.HasBinder ? built.BinderCreated ? "a new effect binder" : "the template's effect binder" : "no binder")}"
                  + $", {built.CastPorted} of {built.Cast.Count} SpEffects its moves cast"
                  + (sounds is null ? "" : $", {sounds.Ported.Count} of {sounds.Wanted} DS3 sounds in {(sounds.BankFile is null ? "no bank" : sounds.BankName)}"));
        _built[plan.NewChr] = built;
        return session;
    }

    // An effect binder for a character whose template has none under its own ID: the one its NpcParam row names
    // (SfxResBankId, another character's), else an empty one in Elden Ring's layout.
    private (BND4 Bnd, DCX.CompressionInfo Compression) OwnBinder(ChrPort.Plan plan)
    {
        int named = _erNpc?[plan.Template.NpcParam] is { } row ? ParamDefaults.Int(row, "SfxResBankId") : -1;
        if (named > 0 && _er is not null && _er.Contains(SfxBinder(named)))
            try
            {
                var (bytes, compression) = _er.ReadWithCompression(SfxBinder(named));
                return (BND4.Read(bytes), compression);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        return _layout.Create();
    }

    // ---- Params (port-maps step 2, on port-weapons' regulation.bin) ----

    private SpEffectPort? _sp;
    private BulletPort? _bullets;

    /// <summary>
    /// DS3 SpEffects the ported maps' event scripts set, clear or check on characters (<see cref="Maps.MapEvents.ScriptSpEffects"/>):
    /// ported too, so a scripted effect change (Pontiff's sword auras, the Jailer's eye glow swap) finds its row at
    /// 700,000,000 + the DS3 ID even when no move casts it. The events step points the scripts there.
    /// </summary>
    public IReadOnlyCollection<int> ScriptSpEffectIds { get; set; } = [];

    /// <summary>
    /// DS3 SpEffects the ported AI scripts check (<see cref="AiPort.RenumberSpEffects"/>): ported too, and kept on an
    /// NpcParam row that names them even when they show nothing (a state marker such as the wolf's 5020, which the
    /// script reads to choose its attacks).
    /// </summary>
    public IReadOnlySet<int> AiSpEffectIds { get; set; } = new HashSet<int>();

    /// <summary>
    /// An effect DS3's map scripts put on an enemy that shows nothing and only tells its AI what is going on (a state
    /// marker) stays off the ported row: the scripts set it when something happens on the map, while on the row it is
    /// on from the start. Ariandel's wolf pack gets 13032 from m45's event 14505200 once its leader is gone; that
    /// branch of DS3's wolf AI (604000_battle) only circles, guards and backs off, and with 13032 on its row since
    /// #183 ported the wolf's markers, the 1.0.442 wolf circled and never attacked. The ported map scripts still set
    /// the marker on placed enemies. Fallback enemy-ai I (DARKSIGN_ENEMY_SCRIPT_MARKERS=keep) puts them back on the row.
    /// </summary>
    public static bool ScriptMarkersLeftOut => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_SCRIPT_MARKERS")?.Trim().ToLowerInvariant() != "keep";
    private AttackParamPort? _attacks;

    /// <summary>
    /// The attack port for the enemies' behaviour variations, with DS3's SpEffects (cast by a behaviour, applied by a
    /// hit, in full) and projectiles ported along, and every row that shows an effect pointed at the converted ones.
    /// </summary>
    public AttackParamPort Attacks(ParamStore regulation, ParamStore ds3, List<string> notes)
    {
        var effects = new FxrPort.Result();
        foreach (var (ds3Id, newId) in _ported)
            effects.Effects[ds3Id] = newId;
        if (SpEffectPort.Enabled)
            try
            {
                _sp = new SpEffectPort(regulation, ds3, SpEffectId, full: true);
                _sp.BindVisuals(effects);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                notes.Add($"enemy effects: SpEffects not ported ({ChrPort.Describe(e)})");
                _sp = null;
            }
        _attacks = new AttackParamPort(regulation, ds3, "BehaviorParam", "AtkParam_Npc") { Effects = _sp, FullHitEffects = true };
        if (BulletPort.Enabled)
            try
            {
                _bullets = _attacks.Bullets = new BulletPort(regulation, ds3, _sp, _attacks.PortBulletAttack);
                _bullets.Bind(effects);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                notes.Add($"enemy effects: projectiles not ported ({ChrPort.Describe(e)})");
            }
        // The effects the moves cast were given their IDs when the files were built; their rows are added here.
        if (_sp is not null)
            foreach (var built in _built.Values.OrderBy(b => b.Plan.NewChr))
                foreach (var id in built.Cast.Order())
                    if (!((_ds3SpEffects?[id]?.Name ?? "").TrimStart().StartsWith("[HKS]", StringComparison.OrdinalIgnoreCase)))
                        _sp.Port(id);
        if (_sp is not null && ScriptSpEffectIds.Count > 0)
        {
            int ported = 0, missing = 0;
            foreach (var id in ScriptSpEffectIds.Order())
            {
                if (_ds3SpEffects?[id] is not { } row)
                {
                    missing++;
                    continue;
                }
                if (row.Name is { } name && name.TrimStart().StartsWith("[HKS]", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_sp.Port(id) > 0)
                    ported++;
            }
            notes.Add($"enemy effects: {ported} of {ScriptSpEffectIds.Count} SpEffects the map scripts use ported ({missing} not in DS3's SpEffectParam)");
        }
        if (_sp is not null && AiSpEffectIds.Count > 0)
        {
            int ported = AiSpEffectIds.Order().Count(id => _sp.Port(id) > 0);
            notes.Add($"enemy effects: {ported} of {AiSpEffectIds.Count} SpEffects the AI scripts check ported");
        }
        return _attacks;
    }

    /// <summary>
    /// Whether an effect on a DS3 NpcParam row is carried over: one that shows something, chains to another effect
    /// or changes health over time. Effects that only scale the enemy's stats (DS3's area and new game plus
    /// scaling) are left to the template's own, which would otherwise scale it twice.
    /// </summary>
    public static bool NpcEffectWanted(PARAM.Row ds3Effect) =>
        Enumerable.Range(0, 8).Any(k => ParamDefaults.Int(ds3Effect, $"EffectVfx{k}") > 0)
        || new[] { "replaceSpEffectId", "cycleOccurenceSpEffectId", "atkOccurenceSpEffectId" }.Any(f => ParamDefaults.Int(ds3Effect, f) > 0)
        || ParamDefaults.Int(ds3Effect, "changeHpPoint") != 0
        || (ParamDefaults.Cell(ds3Effect, "changeHpRate")?.Value is { } rate and not byte[] && Convert.ToDouble(rate) != 0)
        // The damage it takes: DS3 gives phantoms and a few bosses their toughness this way (Gael's phantom 0.57).
        || ChrPort.DamageRateFields.Any(f => ParamDefaults.Cell(ds3Effect, f)?.Value is float r && r > 0 && r != 1);

    /// <summary>
    /// A ported character's NpcParam row: DS3's own effects in the slots the template leaves free, and the row
    /// pointed at the character's own effect binder and sound bank (the template's bank stays as the second one).
    /// </summary>
    public static bool CategoriesEnabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_CATEGORIES")?.Trim() != "0";

    /// <summary>
    /// DS3's rule that a character shows one effect of a category (spCategory), the one of highest categoryPriority,
    /// applied when the row is built. The 1.0.340 test spawn of the Jailer (test menu "fx?" watch, the game's live
    /// SpEffect list) carried all three of its category-100 lights at once, 11900 cyan (priority 1), 11899 orange (4)
    /// and 11902 blue (2), although the ported rows name their category: Elden Ring doesn't sort out effects an
    /// NpcParam row starts with by category. So only the winner of each category goes into the ported row; effects
    /// without a category all stay, and of equal priorities the first named stays. Off with DARKSIGN_ENEMY_CATEGORIES=0
    /// (fallback enemy-glow D).
    /// </summary>
    public static (List<int> Kept, List<int> Dropped) OnePerCategory(IReadOnlyList<int> ids, Func<int, PARAM.Row?> row)
    {
        int Category(int id) => row(id) is { } r ? ParamDefaults.Int(r, "spCategory") : 0;
        var winners = new Dictionary<int, (int Id, int Priority)>();
        foreach (var id in ids.Distinct())
        {
            int category = Category(id);
            if (category <= 0)
                continue;
            int priority = ParamDefaults.Int(row(id)!, "categoryPriority");
            if (!winners.TryGetValue(category, out var best) || priority > best.Priority)
                winners[category] = (id, priority);
        }
        var dropped = ids.Distinct().Where(id => Category(id) is > 0 and var c && winners[c].Id != id).ToList();
        return ([.. ids.Distinct().Except(dropped)], dropped);
    }

    public void ApplyNpc(PARAM.Row row, PARAM.Row? ds3Row, ChrPort.Plan plan)
    {
        if (!_built.TryGetValue(plan.NewChr, out var built))
            return;
        if (_sp is not null && ds3Row is not null)
        {
            var have = Enumerable.Range(0, 32).Select(k => ParamDefaults.Int(row, $"spEffectID{k}")).ToHashSet();
            var own = Enumerable.Range(0, 32).Select(k => ParamDefaults.Int(ds3Row, $"spEffectId{k}")).Where(id => id > 0).ToList();
            var ds3Effects = own;
            var scriptAdded = new HashSet<int>();
            if (_scripts is not null)
            {
                (ds3Effects, var dropped, var added) = _scripts.Apply(ds3Row.ID, own);
                built.ScriptDropped = [.. built.ScriptDropped.Union(dropped)];
                built.ScriptAdded = [.. built.ScriptAdded.Union(added)];
                scriptAdded.UnionWith(added);
            }
            if (CategoriesEnabled && _ds3SpEffects is { } ds3Sp)
            {
                (ds3Effects, var lost) = OnePerCategory(ds3Effects, id => ds3Sp[id]);
                built.CategoryDropped = [.. built.CategoryDropped.Union(lost)];
            }
            foreach (var id in ds3Effects)
            {
                if (_ds3SpEffects?[id] is not { } effect)
                    continue;
                bool shows = NpcEffectWanted(effect);
                if (!shows && scriptAdded.Contains(id) && AiSpEffectIds.Contains(id) && ScriptMarkersLeftOut)
                {
                    // A state the map scripts switch on later, not part of how the enemy starts (see ScriptMarkersLeftOut).
                    if (!built.ScriptMarkers.Contains(id))
                        built.ScriptMarkers.Add(id);
                    continue;
                }
                if (!(shows || AiSpEffectIds.Contains(id)) || _sp.Port(id) is not (> 0 and var newId))
                {
                    built.NpcEffectsSkipped++;
                    continue;
                }
                if (!have.Add(newId))
                    continue;
                int free = Enumerable.Range(0, 32).FirstOrDefault(j => ParamDefaults.Cell(row, $"spEffectID{j}") is not null && ParamDefaults.Int(row, $"spEffectID{j}") <= 0, -1);
                if (free < 0)
                    break;
                ParamDefaults.Set(row, $"spEffectID{free}", newId);
                built.NpcEffects++;
            }
        }
        if (built.HasBinder)
            ParamDefaults.Set(row, "SfxResBankId", plan.NewChr);
        if (built.Sounds is { BankFile: not null })
        {
            // The row's bank becomes the character's own; the bank it had (the template's) stays as the second.
            int bank = ParamDefaults.Int(row, "SoundBankId"), add = ParamDefaults.Int(row, "SoundAddBankId");
            if (bank <= 0 || add <= 0 || bank == add)
                ParamDefaults.Set(row, "SoundAddBankId", bank > 0 ? bank : add > 0 ? add : plan.TemplateChr);
            else
                // A template with two banks of its own (Pontiff's Godskin Noble: 3560 and 3570) keeps its second one;
                // up to 1.0.326 the DS3 bank was left out here and Pontiff played no DS3 sound.
                built.Note = $"NpcParam {row.ID}: the DS3 bank replaces the template's first sound bank {bank} (its second, {add}, stays)";
            ParamDefaults.Set(row, "SoundBankId", plan.NewChr);
        }
    }

    /// <summary>Once every row is in: lists the characters' sound banks in the game's index and puts their loose media in place.</summary>
    public string Finish(List<string> notes)
    {
        string sounds = "";
        if (_sounds is not null)
            try
            {
                sounds = "; " + _sounds.Finish(_built.Values.Select(b => b.Sounds).OfType<ChrSounds.Result>().ToList());
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                notes.Add($"enemy sounds: index or loose media not written ({ChrPort.Describe(e)})");
            }
        WriteReport();
        return $"effects: {_ported.Count} DS3 effects in {_built.Values.Count(b => b.HasBinder)} character effect binders ({_built.Values.Count(b => b.BinderCreated)} new), " +
               $"{_built.Values.Sum(b => b.NpcEffects)} NpcParam effects" +
               (_sp is null ? "" : $", {_sp.Summary}") + (_bullets is null ? "" : $", {_bullets.Summary}") + sounds +
               $" (Work\\reports\\{ReportFile})";
    }

    public void WriteReport()
    {
        var lines = new List<string>
        {
            "new chr\tDS3 chr\tname\teffects ported\teffects wanted\teffect binder\tleft out (reason: count)\tSpEffects cast by moves ported\tcast\tNpcParam effects\tNpcParam effects skipped\t" +
            "sounds ported\tsounds wanted\tsound bank\tsounds left out\tevents added\tevents dropped\ttemplate events dropped\tevents by kind\tnote\t" +
            "NpcParam effects DS3 scripts only take off (left out)\teffects DS3 scripts put on (added)\teffects that lose to another of their category (left out)\tAI state markers DS3 scripts put on (left out of the row)",
        };
        foreach (var b in _built.Values.OrderBy(b => b.Plan.NewChr))
            lines.Add(string.Join('\t', $"c{b.Plan.NewChr:D4}", $"c{b.Plan.Ds3.Chr:D4}", b.Plan.Ds3.Name.Replace('\t', ' '), b.EffectsPorted, b.EffectsWanted,
                b.HasBinder ? b.BinderCreated ? "new" : "template's" : "none",
                string.Join("; ", b.EffectReasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key}: {r.Value}")),
                b.CastPorted, b.Cast.Count, b.NpcEffects, b.NpcEffectsSkipped,
                b.Sounds?.Ported.Count ?? 0, b.Sounds?.Wanted ?? 0, b.Sounds?.BankFile is null ? "none" : b.Sounds.BankName,
                b.Sounds is null ? "" : string.Join("; ", b.Sounds.Reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key}: {r.Value}")),
                b.EventsAdded, b.EventsDropped, b.TemplateDropped,
                string.Join("; ", b.Events.OrderBy(e => e.Key).Select(e => $"{e.Key}: {e.Value}")), b.Note,
                string.Join(" ", b.ScriptDropped.Order()), string.Join(" ", b.ScriptAdded.Order()),
                string.Join(" ", b.CategoryDropped.Order()), string.Join(" ", b.ScriptMarkers.Order())));
        foreach (var (id, reason) in _converter.Reasons.OrderBy(r => r.Key).Take(400))
            lines.Add($"# f{id}: {reason}");
        _sp?.WriteReport(_ctx, "enemy-speffects.tsv");
        var path = _ctx.WorkPath("reports", ReportFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
