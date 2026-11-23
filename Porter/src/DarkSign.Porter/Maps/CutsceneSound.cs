using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The sound of the converted DS3 cutscenes. What each game's files hold (cutscene-sound-survey on Devin's PC,
/// 2026-11-12):
/// <list type="bullet">
/// <item>Elden Ring plays a cutscene's sound through timeline events of resource kind 135 (seType, seId, dmypolyId,
/// slotNo, IsForceRefLocalPlayerEquipData). The cutscene's own audio is seType 4 (p): Play_p&lt;cutscene ID * 10&gt;
/// (s10_00_0050 plays p100000500), in a bank named after the cutscene (sd\s10_00_0050.bnk or sd\enus\...), which the
/// game's sound index lists. Armour movement is seType 15 with IDs 100/200 (cs_main's f000000100/200).</item>
/// <item>DS3 plays its main cutscene audio through kind 138 ("cutscene SE": seType 4 and seId only), one per cut,
/// p&lt;cutscene ID * 10 + n&gt; from the area's remo bank (fdp_rm45.fsb: p450000002 for s45_00_0000's second cut);
/// other effects through kind 135 with seType 4 from the map bank (fdp_m45: p140000050...); armour movement is
/// seType 9 with IDs 0/100/200/300; speech plays from the subtitle events (kind 27, talkParamId).</item>
/// </list>
/// So each converted cutscene gets a bank of its own, sd\&lt;its Elden Ring name&gt;.bnk, built like an Elden Ring
/// cutscene bank (cloned from one: the game's own cutscene bus, Play and Stop events) with every DS3 sound its
/// timeline plays, decoded to PCM the way the weapon and enemy sound ports do (<see cref="SoundPort"/>: DS3's event
/// file says which samples, level and pitch). Each sound gets a new seType 4 ID (<see cref="IdBase"/> + the cutscene's
/// number * 1000 + n), its events are rewritten to Elden Ring's kind 135 form, armour movement goes to seType 15, and
/// a subtitle event whose talk has a DS3 voice keeps its subtitle and speaks through its TalkParam row
/// (<see cref="CutsceneSubtitles"/>). A DS3 sound
/// that can't be found or decoded leaves its event out, so the game never plays an Elden Ring sound of that number.
/// </summary>
public sealed class CutsceneSound
{
    /// <summary>New cutscene sound IDs: IdBase + the cutscene's number in the port * 1000 + the sound's number. No Elden
    /// Ring sound is numbered this high (its cutscene sounds are p&lt;cutscene ID * 10&gt;, up to 619,000,000).</summary>
    public const int IdBase = 990_000_000;
    public const int MaxPerCutscene = 1000;

    public const int KindSound = 135, KindCutsceneSe = 138, KindSubtitle = 27;
    public const uint SeTypeCutscene = 4, Ds3SeTypeArmour = 9, ErSeTypeArmour = 15;

    /// <summary>DS3's sound type letters by seType (the paramdefs' SOUND_TYPE): a environment, c character, f menu,
    /// o object, p cutscene, s effect, m music, v voice, x floor, b armour, g ghost.</summary>
    public static char? Ds3Letter(uint seType) => seType switch
    {
        0 => 'a', 1 => 'c', 3 => 'o', 4 => 'p', 5 => 's', 6 => 'm', 7 => 'v', 10 => 'g', _ => null,
    };

    public static string BankPath(PorterContext ctx, string erName) => ctx.ModPath("sd", erName + ".bnk");
    public static int NewId(int cutsceneIndex, int n) => IdBase + cutsceneIndex * MaxPerCutscene + n;

    // ---------------------------------------------------------------- the timeline

    /// <summary>What one cutscene's timeline plays, and what was rewritten.</summary>
    public sealed class Plan(string erName, int index)
    {
        public string ErName { get; } = erName;
        public int Index { get; } = index;
        /// <summary>DS3 sound name (p450000002, v004005000) -> new seType 4 ID, in the order first played.</summary>
        public Dictionary<string, int> Sounds { get; } = new(StringComparer.Ordinal);
        /// <summary>Spoken lines kept as subtitles (<see cref="CutsceneSubtitles"/>): DS3 talk -> its voice's new ID. The
        /// bank names these Play_v&lt;ID&gt; (Elden Ring plays a subtitle's TalkParam voiceId from the cutscene's bank).</summary>
        public Dictionary<int, int> VoiceOfTalk { get; } = new();
        public HashSet<int> VoiceIds { get; } = [];
        public int Main, Effects, Voices, Armour, Dropped, Auto;
        public List<string> Missing { get; } = [];

        public int Assign(string ds3Name)
        {
            if (!Sounds.TryGetValue(ds3Name, out var id))
            {
                if (Sounds.Count >= MaxPerCutscene)
                    return -1;
                Sounds[ds3Name] = id = NewId(Index, Sounds.Count);
            }
            return id;
        }

        public override string ToString() =>
            $"{Sounds.Count} sounds ({Main} main audio events{(Auto > 0 ? $" ({Auto} added: DS3 plays them without an event)" : "")}, {Effects} effect events, {Voices} spoken lines, {Armour} armour movements to seType 15" +
            $"{(Dropped > 0 ? $", {Dropped} events left out: no DS3 sound {string.Join(" ", Missing.Distinct().Take(8))}" : "")})";
    }

    private static MQB.Parameter? Param(List<MQB.Parameter> ps, string name) =>
        ps.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static long? Number(MQB.Parameter? p) => p?.Value switch
    {
        int i => i, uint u => u, short h => h, ushort h => h, byte b => b, sbyte b => b, long l => l, _ => null,
    };

    private static MQB.Parameter P(string name, MQB.Parameter.DataType type, object value) =>
        new() { Name = name, Type = type, Value = value, MemberCount = 1 };

    /// <summary>Elden Ring's kind 135 parameters, in its order, keeping what the event has.</summary>
    public static List<MQB.Parameter> SoundParams(uint seType, int seId, List<MQB.Parameter>? old = null)
    {
        int Kept(string name, int fallback) => old is not null && Number(Param(old, name)) is { } v ? (int)v : fallback;
        return
        [
            P("seType", MQB.Parameter.DataType.UInt, seType),
            P("seId", MQB.Parameter.DataType.Int, seId),
            P("dmypolyId", MQB.Parameter.DataType.Int, Kept("dmypolyId", -1)),
            P("slotNo", MQB.Parameter.DataType.Int, Kept("slotNo", -1)),
            P("IsForceRefLocalPlayerEquipData", MQB.Parameter.DataType.Bool,
                old is not null && Param(old, "IsForceRefLocalPlayerEquipData")?.Value is bool b && b),
        ];
    }

    /// <summary>
    /// Rewrites a DS3 timeline's sound events in Elden Ring's form, before the cutscene port filters it (so they are
    /// kept). <paramref name="known"/> says whether DS3 has a sound of that name; <paramref name="voice"/> gives the
    /// voice a DS3 talk plays (or null).
    /// </summary>
    public static Plan Prepare(MQB mqb, string erName, int index, Func<string, bool> known, Func<int, int?> voice)
    {
        var plan = new Plan(erName, index);
        foreach (var r in mqb.Resources.Where(r => r.Unk48 == KindCutsceneSe))
            r.Unk48 = KindSound;
        foreach (var cut in mqb.Cuts)
            foreach (var tl in cut.Timelines)
                tl.Events.RemoveAll(ev =>
                {
                    if (ev.ResourceIndex < 0 || ev.ResourceIndex >= mqb.Resources.Count)
                        return false;
                    var res = mqb.Resources[ev.ResourceIndex];
                    if (res.Unk48 == KindSubtitle)
                    {
                        // Speech: the subtitle's talk -> its DS3 voice, played as a sound of the cutscene's own.
                        if (Number(Param(ev.Parameters, "talkParamId")) is not { } talk || talk <= 0)
                            return false;
                        var name = voice((int)talk) is { } v && v > 0 ? $"v{v:D9}" : null;
                        // Elden Ring's way (s10_00_0010: subtitle talk 2003000 -> TalkParam voiceId 20030000, and
                        // the cutscene's own bank lists Play_v020030000): the subtitle stays and its TalkParam row
                        // names the voice, which the bank holds as Play_v<ID>. CutsceneSubtitles rewrites the event.
                        if (name is not null && known(name) && plan.Assign(name) is var vid && vid >= 0)
                        {
                            plan.VoiceOfTalk[(int)talk] = vid;
                            plan.VoiceIds.Add(vid);
                            plan.Voices++;
                        }
                        else
                            plan.Missing.Add(name ?? $"talk {talk}");
                        return false;
                    }
                    if (res.Unk48 != KindSound || Number(Param(ev.Parameters, "seType")) is not { } type || Number(Param(ev.Parameters, "seId")) is not { } seId)
                        return false;
                    if (type == Ds3SeTypeArmour)
                    {
                        ev.Parameters = SoundParams(ErSeTypeArmour, (int)seId, ev.Parameters);
                        plan.Armour++;
                        return false;
                    }
                    if (Ds3Letter((uint)type) is not { } letter || seId <= 0)
                        return false;
                    var sound = $"{letter}{seId:D9}";
                    if (!known(sound) || plan.Assign(sound) is var newId && newId < 0)
                    {
                        plan.Dropped++;
                        plan.Missing.Add(sound);
                        return true;
                    }
                    ev.Parameters = SoundParams(SeTypeCutscene, newId, ev.Parameters);
                    if (res.Name.Contains("カットシーン", StringComparison.Ordinal) || letter == 'p' && sound.StartsWith($"p{CutscenePort.IdOf(mqb.Name) ?? -1}", StringComparison.Ordinal))
                        plan.Main++;
                    else
                        plan.Effects++;
                    return false;
                });
        if (plan.Main == 0 && CutscenePort.IdOf(mqb.Name) is { } ds3Id)
            AddMainAudio(mqb, plan, ds3Id, known);
        return plan;
    }

    /// <summary>
    /// A DS3 cutscene whose timeline names no main audio (s40_00_0000, s30_00_0000: no sound events at all) still has
    /// it in DS3's event files as p&lt;cutscene ID * 10 + n&gt;, which DS3 plays by itself. Elden Ring only plays what
    /// the timeline names, so each such sound (n 0-9, not played by the timeline) gets an event at the start of cut n
    /// (cut n - 1 when the numbers start at 1; the last cut when there are fewer cuts). Its fields copy the cut's first event.
    /// </summary>
    public static void AddMainAudio(MQB mqb, Plan plan, int ds3Id, Func<string, bool> known)
    {
        if (mqb.Cuts.Count == 0)
            return;
        var numbers = Enumerable.Range(0, 10).Where(n => known($"p{ds3Id * 10L + n:D9}") && !plan.Sounds.ContainsKey($"p{ds3Id * 10L + n:D9}")).ToList();
        if (numbers.Count == 0)
            return;
        int first = numbers[0] == 0 ? 0 : 1;
        int resource = mqb.Resources.FindIndex(r => r.Unk48 == KindSound && r.Name.Contains("カットシーン", StringComparison.Ordinal));
        if (resource < 0)
        {
            resource = mqb.Resources.Count;
            mqb.Resources.Add(new MQB.Resource { Name = "SE_main", ParentIndex = -1, Unk48 = KindSound, Path = "", Parameters = [] });
        }
        int nextId = mqb.Cuts.SelectMany(c => c.Timelines).SelectMany(t => t.Events).Select(e => e.ID).DefaultIfEmpty(-1).Max() + 1;
        foreach (var n in numbers)
        {
            var id = plan.Assign($"p{ds3Id * 10L + n:D9}");
            if (id < 0)
                break;
            var cut = mqb.Cuts[Math.Clamp(n - first, 0, mqb.Cuts.Count - 1)];
            var like = cut.Timelines.SelectMany(t => t.Events).FirstOrDefault();
            var tl = new MQB.Timeline { Unk10 = cut.Timelines.FirstOrDefault()?.Unk10 ?? 0 };
            tl.Events.Add(new MQB.Event
            {
                ID = nextId++, ResourceIndex = resource, StartFrame = 0, Duration = Math.Max(1, cut.Duration),
                Unk08 = like?.Unk08 ?? 0, Unk14 = like?.Unk14 ?? 0, Unk18 = like?.Unk18 ?? 0, Unk1C = like?.Unk1C ?? 0,
                Unk20 = like?.Unk20 ?? 0, Unk28 = like?.Unk28 ?? 0, Parameters = SoundParams(SeTypeCutscene, id),
            });
            cut.Timelines.Add(tl);
            plan.Main++;
            plan.Auto++;
        }
    }

    /// <summary>Removes the cutscene sound events (kind 135, seType 4) that play one of <paramref name="ids"/>: sounds
    /// the bank couldn't build, so the game never looks for them. Returns how many were removed.</summary>
    public static int Strip(MQB mqb, IReadOnlySet<int> ids)
    {
        if (ids.Count == 0)
            return 0;
        return Strip(mqb, ids, out _);
    }

    /// <summary><see cref="Strip(MQB, IReadOnlySet{int})"/>, also giving the timelines it left with no events.</summary>
    public static int Strip(MQB mqb, IReadOnlySet<int> ids, out HashSet<MQB.Timeline> emptied)
    {
        emptied = new HashSet<MQB.Timeline>(ReferenceEqualityComparer.Instance);
        if (ids.Count == 0)
            return 0;
        int removed = 0;
        foreach (var tl in mqb.Cuts.SelectMany(c => c.Timelines))
        {
            int n = tl.Events.RemoveAll(ev => ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count && mqb.Resources[ev.ResourceIndex].Unk48 == KindSound
                && Number(Param(ev.Parameters, "seType")) == SeTypeCutscene && Number(Param(ev.Parameters, "seId")) is { } id && id <= int.MaxValue && ids.Contains((int)id));
            if (n > 0 && tl.Events.Count == 0)
                emptied.Add(tl);
            removed += n;
        }
        return removed;
    }

    /// <summary><see cref="Strip(MQB, IReadOnlySet{int})"/> on every timeline file of a converted binder.</summary>
    public static int Strip(BND4 bnd, IReadOnlySet<int> ids)
    {
        if (ids.Count == 0)
            return 0;
        int removed = 0;
        foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase)))
        {
            var mqb = MQB.Read(f.Bytes);
            int n = Strip(mqb, ids, out var emptied);
            if (n > 0)
            {
                // A timeline the strip left empty goes and the rest are renumbered (CutsceneTimelineLinks.Compact).
                CutsceneTimelineLinks.Compact(mqb, emptied);
                f.Bytes = mqb.Write();
            }
            removed += n;
        }
        return removed;
    }

    // ---------------------------------------------------------------- the banks

    private readonly PorterContext _ctx;
    private readonly GameArchives _ds3;
    private readonly GameArchives _er;
    private readonly Dictionary<string, (Fev.Event Event, string Fsb)> _events;
    private readonly Dictionary<int, int> _voices;
    private readonly byte[] _template;
    private readonly uint _templatePlay, _templateStop, _templateBankId;
    private readonly string _templateName;
    private readonly Dictionary<string, Dictionary<string, Fsb5.Sample>?> _fsbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(uint BankId, List<uint> Events)> _written = [];
    private static readonly string[] Suffixes = ["", "b", "c", "d", "e", "f", "g", "h"];

    private CutsceneSound(PorterContext ctx, GameArchives ds3, GameArchives er, Dictionary<string, (Fev.Event, string)> events, Dictionary<int, int> voices,
        byte[] template, string templateName, uint play, uint stop, uint bankId)
    {
        _er = er;
        (_ctx, _ds3, _events, _voices, _template, _templateName, _templatePlay, _templateStop, _templateBankId) =
            (ctx, ds3, events, voices, template, templateName, play, stop, bankId);
    }

    public string TemplateName => _templateName;
    public int EventCount => _events.Count;
    public int VoiceCount => _voices.Count;

    /// <summary>The DS3 sound names this port can build (an event of that name in DS3's event files).</summary>
    public bool Known(string name) => _events.ContainsKey(name);

    /// <summary>The voice a DS3 talk plays: TalkParam's EventID_Male (the field Paramdex names so; DS3 voice names are
    /// v + that number), else the talk's own number when DS3 has a voice of that name.</summary>
    public int? Voice(int talk) =>
        _voices.TryGetValue(talk, out var v) && Known($"v{v:D9}") ? v : Known($"v{talk:D9}") ? talk : null;

    /// <summary>
    /// The port's template: an Elden Ring cutscene bank that isn't localized and plays its cutscene's sound through
    /// Play_p/Stop_p events (the smallest that reads), and DS3's events and talks. Null, with the reason in notes,
    /// when either game lacks what it needs.
    /// </summary>
    public static CutsceneSound? Create(PorterContext ctx, GameArchives ds3, GameArchives er, List<string> notes)
    {
        var candidates = er.Entries.Where(e => e.Path is not null && Regex.IsMatch(e.Path, @"^/s\d\d_\d\d_\d{4}\.bnk$", RegexOptions.IgnoreCase))
            .Select(e => (Path: e.Path!, Size: e.Header.PaddedFileSize)).OrderBy(c => c.Size).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
        foreach (var (path, _) in candidates)
        {
            try
            {
                var bytes = er.Read(path);
                var bank = WwiseBank.Read(bytes);
                var name = Path.GetFileNameWithoutExtension(path);
                if (CutscenePort.IdOf(name) is not { } id)
                    continue;
                uint play = FxSoundExplore.Fnv1($"Play_p{id * 10L:D9}"), stop = FxSoundExplore.Fnv1($"Stop_p{id * 10L:D9}");
                if (bank.Chain(play) is not { } chain || WwiseNode.Read(chain.Sound) is null || !Builds(bytes, play))
                    continue;
                var events = SoundPort.Ds3Events(ctx, ds3);
                var voices = new Dictionary<int, int>();
                try
                {
                    var talk = ctx.LoadParams(PorterGame.DarkSouls3).Get("TalkParam");
                    if (talk is not null)
                        foreach (var row in talk.Rows)
                            if (row["EventID_Male"]?.Value is int v && v > 0)
                                voices[row.ID] = v;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    notes.Add($"DS3 TalkParam not read ({e.Message}); spoken lines use the talk's own number");
                }
                return new CutsceneSound(ctx, ds3, er, events, voices, bytes, name, play, stop, bank.BankId);
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
            }
        }
        notes.Add($"cutscene sound: no Elden Ring cutscene bank to copy ({candidates.Count} looked at); cutscenes stay silent");
        return null;
    }

    /// <summary>Whether a bank of our own can be built on this template: one short silent sound added and written.</summary>
    public static bool Builds(byte[] template, uint play)
    {
        try
        {
            var bank = WwiseBank.Read(template);
            var chain = bank.Chain(play)!.Value;
            var wem = WwiseBank.PcmWem(new short[480], 1, 48000);
            bank.AddSound(chain, 0x7FFF0001, [new WwiseBank.Layer(0x7FFF0002, 0x7FFF0003, [(0x7FFF0004u, 0x7FFF0005u, wem)])], streamed: false);
            return bank.WriteOwnBank(0x7FFF0006) is not null;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Above this share of samples at full scale a decode is noise, not sound (Friede's spoken lines, the
    /// loudest real ones measured, have under 0.03%; the broken 8-channel tracks 0.17% and 2.4%).</summary>
    public const double MaxClippedShare = 0.001;

    public static double ClippedShare(short[] pcm)
    {
        if (pcm.Length == 0)
            return 0;
        long clipped = 0;
        foreach (var v in pcm)
            if (v >= 32760 || v <= -32760)
                clipped++;
        return (double)clipped / pcm.Length;
    }

    // A DS3 sound bank's samples by name (encrypted banks decrypted, loose files first), kept for the next cutscene.
    private Dictionary<string, Fsb5.Sample>? Samples(string fsb)
    {
        if (_fsbs.TryGetValue(fsb, out var cached))
            return cached;
        byte[]? bytes = null;
        var loose = Path.Combine(_ctx.GameDir(PorterGame.DarkSouls3), "sound", fsb + ".fsb");
        try
        {
            if (File.Exists(loose)) bytes = File.ReadAllBytes(loose);
            else if (_ds3.Contains($"/sound/{fsb}.fsb")) bytes = _ds3.Read($"/sound/{fsb}.fsb");
            if (bytes is not null && !Fsb5.Is(bytes))
                bytes = Fsb5.Decrypt(bytes);
            var samples = bytes is null ? null : Fsb5.Read(bytes).Samples.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return _fsbs[fsb] = samples;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return _fsbs[fsb] = null;
        }
    }

    /// <summary>
    /// Builds and writes the cutscene's bank (sd\&lt;name&gt;.bnk) with every sound its plan names. Returns the log line
    /// and the IDs of the sounds that couldn't be decoded (or all of them, when no bank was written): the caller strips
    /// their events (<see cref="Strip(BND4, IReadOnlySet{int})"/>) so the timeline plays only what the bank has.
    /// </summary>
    /// <param name="folder">Where the bank goes instead of the mod's sd folder (cutscene-test writes to Work\scratch).</param>
    public (string Log, HashSet<int> Silent) Build(Plan plan, string? folder = null)
    {
        var silentIds = new HashSet<int>();
        var log = BuildBank(plan, folder, silentIds);
        return (log, silentIds);
    }

    private string BuildBank(Plan plan, string? folder, HashSet<int> silentIds)
    {
        var path = folder is null ? BankPath(_ctx, plan.ErName) : Path.Combine(folder, plan.ErName + ".bnk");
        if (File.Exists(path))
            File.Delete(path);
        if (plan.Sounds.Count == 0)
            return "no sound";
        var bank = WwiseBank.Read(_template);
        var template = bank.Chain(_templatePlay)!.Value;
        var stopTemplate = bank.StopTemplate(_templateStop);
        HashSet<uint> taken = [];
        var events = new List<uint>();
        var silent = new List<string>();
        long bytes = 0;
        double seconds = 0;
        foreach (var (name, id) in plan.Sounds)
        {
            var (ev, fsb) = _events[name];
            var samples = Samples(fsb);
            var layers = new List<WwiseBank.Layer>();
            for (int l = 0; l < ev.Layers.Count; l++)
            {
                var layer = ev.Layers[l];
                var wanted = layer.Waves.Select(Fev.SampleName).Distinct().ToList();
                if (wanted.Count == 0 && l == 0)
                    wanted = [.. Suffixes.Select(s => name + s)];
                var found = wanted.Select(w => samples?.GetValueOrDefault(w)).OfType<Fsb5.Sample>().ToList();
                var media = new List<(uint, uint, byte[])>();
                foreach (var s in found)
                {
                    try
                    {
                        var pcm = Fsb5.DecodePcm(s);
                        // DS3's surround cutscene tracks (8 channels: Friede's p450000002/3) come out of the decoder
                        // as noise pinned at full scale, whatever block sizes are tried (measured 2026-11-19: 0.17% and
                        // 2.4% of all samples clipped, some channels a third exact zeros). Up to 1.0.47x they went into the bank
                        // and played as loud crackle over the cutscene. Such a decode is left out: the sound counts as
                        // not decoded and its events are stripped.
                        if (ClippedShare(pcm) > MaxClippedShare)
                            throw new InvalidDataException($"{s.Name}: the decoded sound is noise ({s.Channels} channels)");
                        var wem = WwiseBank.PcmWem(pcm, s.Channels, s.Rate);
                        uint mediaId = SoundPort.UnusedId(bank, $"darksign_{plan.ErName}_media_{s.Name}", taken);
                        uint soundId = SoundPort.UnusedId(bank, $"darksign_{plan.ErName}_sound_{name}_{l}_{media.Count}", taken);
                        media.Add((soundId, mediaId, wem));
                        bytes += wem.Length;
                        seconds = Math.Max(seconds, s.Rate > 0 ? (double)s.Frames / s.Rate : 0);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                    }
                }
                if (media.Count == 0)
                    continue;
                // A cutscene sound plays once: nothing would stop a loop when the cutscene ends.
                var tuning = SoundPort.TuningOf(ev, layer) is { } t ? t with { Loop = false } : null;
                layers.Add(new WwiseBank.Layer(SoundPort.UnusedId(bank, $"darksign_{plan.ErName}_action_{name}_{l}", taken),
                    SoundPort.UnusedId(bank, $"darksign_{plan.ErName}_container_{name}_{l}", taken), media)
                {
                    Tuning = tuning,
                    StopActionId = SoundPort.UnusedId(bank, $"darksign_{plan.ErName}_stop_{name}_{l}", taken),
                });
            }
            if (layers.Count == 0)
            {
                silent.Add(name);
                silentIds.Add(id);
                continue;
            }
            // A subtitle's voice is played by its TalkParam row (voiceId): Play_v; every other sound by a seType 4 event: Play_p.
            char kind = plan.VoiceIds.Contains(id) ? 'v' : 'p';
            uint play = FxSoundExplore.Fnv1($"Play_{kind}{id:D9}"), stop = FxSoundExplore.Fnv1($"Stop_{kind}{id:D9}");
            bank.AddSound(template, play, layers, streamed: false, new WwiseBank.StopEvent(stop, ev.FadeOutMs, stopTemplate));
            events.Add(play);
            events.Add(stop);
        }
        if (events.Count == 0)
        {
            silentIds.UnionWith(plan.Sounds.Values);
            return $"no sound decoded ({string.Join(" ", silent.Take(8))})";
        }
        uint bankId = FxSoundExplore.Fnv1(plan.ErName);
        var written = bank.WriteOwnBank(bankId) ?? throw new InvalidDataException($"the template bank {_templateName}'s sounds don't hang under an actor-mixer this port can copy");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, written);
        if (folder is null)
            _written.Add((bankId, events));
        return $"bank {(folder is null ? "sd\\" : "")}{plan.ErName}.bnk {written.Length / 1024} KB, {events.Count / 2} sounds, longest {seconds:0} s" +
               (silent.Count > 0 ? $"; not decoded: {string.Join(" ", silent.Take(8))}" : "");
    }

    // ---------------------------------------------------------------- spoken lines (TalkPort)

    /// <summary>A spoken line for a voice bank: the number Elden Ring's TalkParam names (voiceId: the game plays
    /// Play_v&lt;number, nine digits&gt;), DS3's sound name (v&lt;DS3 voice&gt;) or null, and how long a silent stand-in lasts.</summary>
    public sealed record VoiceLine(int VoiceId, string? Ds3Name, float Seconds);

    // An Elden Ring NPC voice bank to build voice banks on: its bytes, name, a Play event and a Stop event of it.
    private sealed record VoiceTemplate(byte[] Bytes, string Name, uint Play, uint Stop, uint BankId);
    private VoiceTemplate? _voiceTemplate;
    private bool _voiceTemplateLooked;
    private string _voiceTemplateNote = "";

    /// <summary>Which template the voice banks are built on (for the log).</summary>
    public string VoiceTemplateNote
    {
        get
        {
            VoiceTemplateOf();
            return _voiceTemplateNote;
        }
    }

    /// <summary>An Elden Ring NPC voice bank's archive path. Measured on Devin's PC (Work\\index\\ER.tsv, 2026-11-20): they sit
    /// at "/enus/vc101.bnk" in the sd archive (no /sd/ folder in the path); #236 looked for "/sd/..." and so found none
    /// (port log: "no Elden Ring voice bank to copy (0 found)") and still built on the cutscene template.</summary>
    public static bool IsVoiceBank(string path) =>
        Regex.IsMatch(path, @"^/(sd/)?([a-z]{2,6}/)?vc\d{3}\.bnk$", RegexOptions.IgnoreCase);

    /// <summary>The smallest Elden Ring NPC voice bank (/sd/[enus/]vcNNN.bnk) with an event whose Sound hangs under
    /// actor-mixers a bank of our own can copy (<see cref="Builds"/>); null (cutscene template) when none.</summary>
    /// <summary>voice-template-check: which Elden Ring voice bank the voice banks would be built on, and why the others
    /// weren't (needs both games; no files written).</summary>
    public static int CheckVoiceTemplate(PorterContext ctx)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var notes = new List<string>();
        var sound = Create(ctx, ds3, er, notes);
        if (sound is null)
        {
            Console.WriteLine($"voice-template-check: no cutscene template ({string.Join("; ", notes)})");
            return 1;
        }
        Console.WriteLine($"voice-template-check: {er.Entries.Count(e => e.Path is not null && IsVoiceBank(e.Path))} Elden Ring voice banks in the archives");
        Console.WriteLine("voice-template-check: voice banks " + sound.VoiceTemplateNote);
        foreach (var line in sound.VoiceTemplateTrace)
            Console.WriteLine("  " + line);
        return sound._voiceTemplate is null ? 1 : 0;
    }

    /// <summary>Why each Elden Ring voice bank looked at was not used (for voice-template-check and the log).</summary>
    public List<string> VoiceTemplateTrace { get; } = [];

    private VoiceTemplate? VoiceTemplateOf()
    {
        if (_voiceTemplateLooked)
            return _voiceTemplate;
        _voiceTemplateLooked = true;
        var candidates = _er.Entries.Where(e => e.Path is not null && IsVoiceBank(e.Path))
            .Select(e => (Path: e.Path!, Size: e.Header.PaddedFileSize)).OrderBy(c => c.Size).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
        int looked = 0;
        foreach (var (path, _) in candidates.Take(40))
        {
            looked++;
            try
            {
                var bytes = _er.Read(path);
                var bank = WwiseBank.Read(bytes);
                uint stop = bank.Objects.Where(o => o.Type == WwiseBank.EventType).Select(o => o.Id)
                    .FirstOrDefault(id => bank.StopTemplate(id) is not null);
                int events = 0, chains = 0, readable = 0;
                string parentNote = "";
                foreach (var ev in bank.Objects.Where(o => o.Type == WwiseBank.EventType).Take(64))
                {
                    events++;
                    if (bank.Chain(ev.Id) is not { } chain)
                        continue;
                    chains++;
                    if (WwiseNode.Read(chain.Sound) is not { } node)
                        continue;
                    readable++;
                    if (parentNote.Length == 0)
                    {
                        uint parent = node.Parent(chain.Sound.Data);
                        parentNote = $", first Sound's parent {parent} " + (bank.Get(parent) is { } up ? $"(type {up.Type} in the bank)" : "(not in the bank)");
                    }
                    if (!Builds(bytes, ev.Id))
                        continue;
                    _voiceTemplate = new VoiceTemplate(bytes, Path.GetFileNameWithoutExtension(path), ev.Id, stop, bank.BankId);
                    _voiceTemplateNote = $"built on Elden Ring's NPC voice bank {path} (its voice actor-mixers: room reverb, distance, position; " +
                                         $"{(stop != 0 ? "its Stop action" : "the cutscene Stop action")})";
                    return _voiceTemplate;
                }
                VoiceTemplateTrace.Add($"{path}: {events} events, {chains} with a Play chain, {readable} with a readable Sound, none builds{parentNote}");
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                VoiceTemplateTrace.Add($"{path}: unreadable ({e.GetType().Name}: {e.Message})");
            }
        }
        _voiceTemplateNote = $"built on the cutscene template {_templateName}: no Elden Ring voice bank to copy ({candidates.Count} found, {looked} looked at)";
        return null;
    }

    private readonly List<(uint BankId, List<uint> Events)> _voiceBanks = [];

    /// <summary>Halves the sample rate of 44.1 kHz and up (speech loses nothing it needs; a bank is held in memory whole).</summary>
    public static (short[] Pcm, int Rate) Halve(short[] pcm, int channels, int rate)
    {
        if (rate < 44100 || channels < 1)
            return (pcm, rate);
        int frames = pcm.Length / channels / 2;
        var half = new short[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                half[f * channels + c] = (short)((pcm[2 * f * channels + c] + pcm[(2 * f + 1) * channels + c]) / 2);
        return (half, rate / 2);
    }

    /// <summary>
    /// Builds a bank of spoken lines named as Elden Ring names an NPC's voice bank (vcNNN: NNN is the first three
    /// digits of the NPC's talk script number) and writes it to sd\ and sd\enus\ (Elden Ring's own voice banks are
    /// localized; ours is built on a bank that is not, so it is put where either lookup finds it). Each line is
    /// Play_v/Stop_v&lt;voiceId&gt;: DS3's voice decoded, or silence of the line's reading time when DS3 has no sound for
    /// it or it doesn't decode: Elden Ring ends a line when its voice ends, and at once when it has none (1.0.501:
    /// "the game doesn't wait for their dialogue to finish"). Returns the log and the voice IDs the bank plays.
    /// </summary>
    public (string Log, HashSet<int> Done) BuildVoiceBank(string bankName, IReadOnlyList<VoiceLine> lines)
    {
        var done = new HashSet<int>();
        if (lines.Count == 0)
            return ("no lines", done);
        var voice = VoiceTemplateOf();
        var bank = WwiseBank.Read(voice?.Bytes ?? _template);
        var template = bank.Chain(voice?.Play ?? _templatePlay)!.Value;
        var stopTemplate = voice is { Stop: not 0 } ? bank.StopTemplate(voice.Stop) : voice is null ? bank.StopTemplate(_templateStop) : null;
        if (voice is not null && stopTemplate is null)
            stopTemplate = WwiseBank.Read(_template).StopTemplate(_templateStop);
        HashSet<uint> taken = [];
        var events = new List<uint>();
        int spoken = 0, silent = 0;
        long bytes = 0;
        foreach (var line in lines.DistinctBy(l => l.VoiceId))
        {
            byte[]? wem = null;
            int fade = 0;
            if (line.Ds3Name is { } name && _events.TryGetValue(name, out var found))
            {
                var (ev, fsb) = found;
                fade = ev.FadeOutMs;
                var samples = Samples(fsb);
                var wanted = ev.Layers.SelectMany(l => l.Waves).Select(Fev.SampleName).Concat(Suffixes.Select(x => name + x)).Distinct();
                foreach (var s in wanted.Select(w => samples?.GetValueOrDefault(w)).OfType<Fsb5.Sample>())
                {
                    try
                    {
                        var pcm = Fsb5.DecodePcm(s);
                        if (pcm.Length == 0 || ClippedShare(pcm) > MaxClippedShare)
                            continue;
                        var (half, rate) = Halve(pcm, s.Channels, s.Rate);
                        wem = WwiseBank.PcmWem(half, s.Channels, rate);
                        break;
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                    }
                }
            }
            if (wem is not null)
                spoken++;
            else
            {
                // Silence the length of the reading time, so the line still stays up.
                wem = WwiseBank.PcmWem(new short[(int)(8000 * Math.Clamp(line.Seconds, 1f, 20f))], 1, 8000);
                silent++;
            }
            bytes += wem.Length;
            string tag = $"darksign_{bankName}_{line.VoiceId}";
            var layer = new WwiseBank.Layer(SoundPort.UnusedId(bank, tag + "_action", taken), SoundPort.UnusedId(bank, tag + "_container", taken),
                [(SoundPort.UnusedId(bank, tag + "_sound", taken), SoundPort.UnusedId(bank, tag + "_media", taken), wem)])
            {
                StopActionId = SoundPort.UnusedId(bank, tag + "_stop", taken),
            };
            uint play = FxSoundExplore.Fnv1($"Play_v{line.VoiceId:D9}"), stop = FxSoundExplore.Fnv1($"Stop_v{line.VoiceId:D9}");
            bank.AddSound(template, play, [layer], streamed: false, new WwiseBank.StopEvent(stop, fade, stopTemplate));
            events.Add(play);
            events.Add(stop);
            done.Add(line.VoiceId);
        }
        uint bankId = FxSoundExplore.Fnv1(bankName);
        var written = bank.WriteOwnBank(bankId) ?? throw new InvalidDataException($"the template bank {voice?.Name ?? _templateName}'s sounds don't hang under an actor-mixer this port can copy");
        foreach (var path in VoiceBankPaths(_ctx, bankName))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, written);
        }
        _voiceBanks.Add((bankId, events));
        return ($"{bankName}.bnk {written.Length / 1024} KB, {spoken} spoken, {silent} silent", done);
    }

    public static string[] VoiceBankPaths(PorterContext ctx, string bankName) =>
        [ctx.ModPath("sd", bankName + ".bnk"), ctx.ModPath("sd", "enus", bankName + ".bnk")];

    /// <summary>Lists every bank written in the game's sound index (the mod's, which port-weapons wrote, else the game's),
    /// in the section that holds the template cutscene bank.</summary>
    public string WriteIndex(GameArchives er)
    {
        if (_written.Count == 0 && _voiceBanks.Count == 0)
            return "index: nothing to list";
        var path = SoundPort.IndexPath(_ctx);
        byte[]? bytes = File.Exists(path) ? File.ReadAllBytes(path) : er.Contains(SoundIndex.ArchivePath) ? er.Read(SoundIndex.ArchivePath) : null;
        if (bytes is null)
            return "index: the game has none";
        var index = SoundIndex.Read(bytes);
        int added = _written.Sum(w => index.Register(w.BankId, w.Events, sectionOf: _templateBankId));
        // Voice banks go where Elden Ring lists its own (vc101, vc102...), else beside the template.
        uint? voiceSection = Enumerable.Range(100, 900).Select(n => FxSoundExplore.Fnv1($"vc{n}")).Where(index.HasBank).Select(id => (uint?)id).FirstOrDefault();
        int voices = _voiceBanks.Sum(w => index.Register(w.BankId, w.Events, sectionOf: voiceSection ?? _templateBankId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, index.Write());
        return $"index: {added} events of {_written.Count} cutscene banks listed (beside {_templateName})"
               + (_voiceBanks.Count > 0 ? $", {voices} events of {_voiceBanks.Count} voice banks listed ({(voiceSection is null ? "beside the template: the index has no voice bank" : "with Elden Ring's voice banks")})" : "");
    }
}

public static class CutsceneSoundCheck
{
    /// <summary>For cutscene-test: every seType 4 event of the converted timeline has its Play event in the bank, and
    /// the bank reads back. Returns the report lines and how many checks failed.</summary>
    public static (List<string> Lines, int Failures) Check(MQB converted, string bankFile)
    {
        var lines = new List<string>();
        int failures = 0;
        var bank = File.Exists(bankFile) ? WwiseBank.Read(File.ReadAllBytes(bankFile)) : null;
        var played = new SortedSet<int>();
        foreach (var cut in converted.Cuts)
            foreach (var ev in cut.Timelines.SelectMany(t => t.Events))
                if (ev.ResourceIndex >= 0 && ev.ResourceIndex < converted.Resources.Count && converted.Resources[ev.ResourceIndex].Unk48 == CutsceneSound.KindSound
                    && ev.Parameters.FirstOrDefault(p => p.Name == "seType")?.Value is uint t && t == CutsceneSound.SeTypeCutscene
                    && ev.Parameters.FirstOrDefault(p => p.Name == "seId")?.Value is int id)
                {
                    played.Add(id);
                    if (lines.Count < 12)
                        lines.Add($"{cut.Name} frame {ev.StartFrame}: seType 4 seId {id} ({ev.Parameters.Count} parameters)");
                }
        int missing = played.Count(id => bank?.Chain(FxSoundExplore.Fnv1($"Play_p{id:D9}")) is null);
        failures += missing;
        lines.Add(bank is null
            ? $"{played.Count} cutscene sounds played, no bank{(played.Count > 0 ? " (FAILED)" : "")}"
            : $"bank {new FileInfo(bankFile).Length / 1024} KB, ID {bank.BankId}, {bank.MediaCount} media, {bank.Objects.Count} objects; " +
              $"{played.Count} sounds played by the timeline, {missing} without a Play event in the bank{(missing > 0 ? " (FAILED)" : "")}");
        if (bank is null && played.Count > 0)
            failures++;
        return (lines, failures);
    }
}
