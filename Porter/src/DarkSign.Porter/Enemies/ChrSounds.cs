using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// DS3 sounds for ported enemies, each character's in a sound bank of its own (sd\cs_cXXXX.bnk under the character's
/// new ID), the way Elden Ring keeps its own enemies' sounds: the game loads it with the character (NpcParam
/// SoundBankId) and the template's bank stays loaded beside it (SoundAddBankId). The weapons' sounds all go into
/// cs_main, which is always in memory; ninety enemies' sounds as PCM there would not fit.
/// <para>A sound is ported exactly as the weapon port does it (<see cref="SoundPort"/>): the samples DS3's event of
/// that name plays (<see cref="Fev"/>), decoded to PCM, one Play action per layer, under the event name
/// Play_c/s&lt;900000000 + DS3 ID&gt;. The bank itself is the weapon port's "bank of our own" (launcher fallback sounds
/// E, <see cref="WwiseBank.WriteOwnBank"/>): the new sounds with unchanged copies of cs_main's actor-mixers above
/// them. Samples come from the bank DS3's event file belongs to (fdp_c2230.fev -> fdp_c2230.fsb), encrypted ones
/// included.</para>
/// The game's sound index and the loose copies of the media are written by <see cref="Finish"/>, after port-weapons
/// (which rewrites the index and empties sd\wem) is done.
/// </summary>
public sealed class ChrSounds
{
    private static readonly bool Mirror = Environment.GetEnvironmentVariable("DARKSIGN_SOUND_MIRROR") != "0";
    private static readonly bool Register = Environment.GetEnvironmentVariable("DARKSIGN_SOUND_INDEX") != "0";
    private static readonly string[] Suffixes = ["", "b", "c", "d", "e", "f", "g", "h"];
    /// <summary>Banks several characters play from stay in memory up to this many bytes; the rest are read again.</summary>
    private const long CacheLimit = 512L << 20;

    private readonly PorterContext _ctx;
    private readonly GameArchives _er, _ds3;
    private readonly byte[] _main;
    private readonly uint _mainBankId;
    private readonly List<int> _templateIds;
    private readonly Dictionary<string, (Fev.Event Event, string Fsb)> _events;
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingBanks = new(StringComparer.OrdinalIgnoreCase);
    private long _cached;
    private readonly object _lock = new();

    public sealed class Result
    {
        public string BankName = "";
        public uint BankId;
        /// <summary>The bank's file in the mod folder (written as soon as it is built: the PCM of ninety banks does not fit in memory), null when the character has none.</summary>
        public string? BankFile;
        public long BankBytes;
        public int Wanted, Encrypted;
        public HashSet<(int Kind, int Id)> Ported = [];
        public List<uint> Events = [];
        public List<string> Media = [];
        public Dictionary<string, int> Reasons = [];

        public int? Map(int kind, int id) => Ported.Contains((kind, id)) ? SoundPort.IdBase + id : null;

        /// <summary>The ported sounds that loop until their Stop event is posted (see SoundPort.Result.Loops).</summary>
        public HashSet<(int Kind, int Id)> Looping = [];
        public bool Loops(int kind, int id) => Looping.Contains((kind, id));
    }

    private ChrSounds(PorterContext ctx, GameArchives er, GameArchives ds3, byte[] main, uint mainBankId, List<int> templateIds,
        Dictionary<string, (Fev.Event Event, string Fsb)> events)
    {
        (_ctx, _er, _ds3, _main, _mainBankId, _templateIds, _events) = (ctx, er, ds3, main, mainBankId, templateIds, events);
    }

    private static string Staging(PorterContext ctx) => ctx.WorkPath("enemy-sounds", "wem");

    public static ChrSounds Create(PorterContext ctx, GameArchives er, GameArchives ds3, List<int> templateIds)
    {
        var main = er.Read(SoundPort.Bank);
        var bank = WwiseBank.Read(main);
        if (Template(bank, templateIds) is null)
            throw new InvalidDataException($"no template sound found in {SoundPort.Bank}");
        if (Directory.Exists(Staging(ctx)))
            Directory.Delete(Staging(ctx), true);
        return new ChrSounds(ctx, er, ds3, main, bank.BankId, templateIds, SoundPort.Ds3Events(ctx, ds3));
    }

    private static (WwiseBank.HircObject Event, WwiseBank.HircObject Action, WwiseBank.HircObject Sound)? Template(WwiseBank bank, List<int> ids) =>
        ids.Select(id => bank.Chain(FxSoundExplore.Fnv1($"Play_c{id:D9}"))).FirstOrDefault(c => c is not null);

    // A DS3 sound bank's plain bytes: a loose file in the game's sound folder first, then the archives.
    private byte[]? BankBytes(string fsb, bool keep, Result result)
    {
        if (_cache.TryGetValue(fsb, out var cached))
            return cached;
        if (_missingBanks.Contains(fsb))
            return null;
        byte[]? bytes = null;
        var loose = Path.Combine(_ctx.GameDir(PorterGame.DarkSouls3), "sound", fsb + ".fsb");
        try
        {
            if (File.Exists(loose))
                bytes = File.ReadAllBytes(loose);
            else if (_ds3.Contains($"/sound/{fsb}.fsb"))
                bytes = _ds3.Read($"/sound/{fsb}.fsb");
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
        if (bytes is not null && !Fsb5.Is(bytes))
        {
            bytes = Fsb5.Decrypt(bytes);
            if (bytes is not null)
                result.Encrypted++;
        }
        if (bytes is null)
        {
            _missingBanks.Add(fsb);
            return null;
        }
        if (keep && _cached + bytes.Length <= CacheLimit)
        {
            _cache[fsb] = bytes;
            _cached += bytes.Length;
        }
        return bytes;
    }

    /// <summary>
    /// Builds one character's bank from the DS3 sounds its moves and effects play. One character at a time (the
    /// decoder and the bank cache are shared). A character with no sound that ports gets no bank.
    /// </summary>
    /// <param name="unheld">Effect sounds the character's effects play without holding them: they never loop
    /// (see SoundPort.Apply).</param>
    public Result Build(ChrPort.Plan plan, IReadOnlyList<(int Kind, int Id)> wanted, IReadOnlySet<int>? unheld = null)
    {
        lock (_lock)
            return BuildLocked(plan, wanted, unheld);
    }

    private Result BuildLocked(ChrPort.Plan plan, IReadOnlyList<(int Kind, int Id)> wanted, IReadOnlySet<int>? unheld)
    {
        var result = new Result { BankName = $"cs_c{plan.NewChr:D4}", Wanted = wanted.Count };
        result.BankId = FxSoundExplore.Fnv1(result.BankName);
        if (wanted.Count == 0)
            return result;
        void Left(string why) => result.Reasons[why] = result.Reasons.GetValueOrDefault(why) + 1;

        // Which bank holds which sample names: the bank of the sound's DS3 event file, else (no event, the name
        // rule) the character's own banks.
        var own = ChrPort.Ds3AnimationSources(plan.Ds3.Chr).Select(c => $"fdp_c{c:D4}").ToList();
        var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Want(string fsb, string name)
        {
            if (!names.TryGetValue(fsb, out var set))
                names[fsb] = set = [];
            set.Add(name);
        }
        foreach (var (kind, id) in wanted)
        {
            var name = $"{SoundPort.Letter(kind)}{id:D9}";
            if (_events.TryGetValue(name, out var known))
                foreach (var wave in known.Event.Layers.SelectMany(l => l.Waves))
                    Want(known.Fsb, Fev.SampleName(wave));
            foreach (var fsb in own.Append(known.Fsb).Where(f => f is not null).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var suffix in Suffixes)
                    Want(fsb!, name + suffix);
        }
        var samples = new Dictionary<(string Fsb, string Name), Fsb5.Sample>();
        foreach (var (fsb, set) in names)
        {
            // A character's own bank is read for it alone; a shared one (the main banks) is kept for the next.
            if (BankBytes(fsb, keep: !own.Contains(fsb, StringComparer.OrdinalIgnoreCase), result) is not { } bytes)
                continue;
            try
            {
                foreach (var s in Fsb5.Read(bytes, set.Contains).Samples)
                    samples.TryAdd((fsb.ToLowerInvariant(), s.Name), s);
            }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        }
        Fsb5.Sample? Pick(string name, IEnumerable<string?> banks) =>
            banks.Where(b => b is not null).Select(b => samples.GetValueOrDefault((b!.ToLowerInvariant(), name))).FirstOrDefault(s => s is not null);

        var bank = WwiseBank.Read(_main);
        var template = Template(bank, _templateIds)!.Value;
        int templateId = _templateIds.FirstOrDefault(t => bank.Chain(FxSoundExplore.Fnv1($"Play_c{t:D9}")) is not null, -1);
        var stopTemplate = bank.StopTemplate(FxSoundExplore.Fnv1($"Stop_c{templateId:D9}"));
        // As the weapon port: DS3's level, pitch and loops per layer, a looping layer's sample embedded apart with
        // its loop points, and a Stop event for every sound (SoundPort.TuningOf, LoopPoints, StopOf).
        var media = new Dictionary<Fsb5.Sample, (uint Id, byte[] Bytes)>(ReferenceEqualityComparer.Instance);
        var loopMedia = new Dictionary<Fsb5.Sample, (uint Id, byte[] Bytes)>(ReferenceEqualityComparer.Instance);
        var failed = new HashSet<Fsb5.Sample>(ReferenceEqualityComparer.Instance);
        HashSet<uint> taken = [], written = [];
        var wems = new List<(uint Id, byte[] Bytes)>();
        foreach (var (kind, id) in wanted)
        {
            char letter = SoundPort.Letter(kind)!.Value;
            var name = $"{letter}{id:D9}";
            List<(List<Fsb5.Sample> Samples, WwiseBank.Tuning? Tuning)> layers = [];
            string? eventBank = null;
            if (_events.TryGetValue(name, out var ds3Event))
            {
                eventBank = ds3Event.Fsb;
                layers = ds3Event.Event.Layers
                    .Select(l => (Samples: l.Waves.Select(Fev.SampleName).Distinct().Select(w => Pick(w, [eventBank])).OfType<Fsb5.Sample>().ToList(),
                        Tuning: SoundPort.TuningOf(ds3Event.Event, l)))
                    .Where(l => l.Samples.Count > 0).ToList();
            }
            if (layers.Count == 0 && Suffixes.Select(v => Pick(name + v, own.Prepend(eventBank))).OfType<Fsb5.Sample>().ToList() is { Count: > 0 } named)
                layers.Add((named, null));
            if (layers.Count == 0) { Left(eventBank is null ? "no DS3 event or sample" : "no DS3 sample"); continue; }
            uint eventId = FxSoundExplore.Fnv1($"Play_{letter}{SoundPort.IdBase + id:D9}");
            if (bank.Has(eventId)) { Left("event ID taken"); continue; }
            // A sound that can't get a Stop event must not loop: nothing could end it.
            var stop = SoundPort.StopEvents ? SoundPort.StopOf(letter, id, ds3Event.Event, stopTemplate) : null;
            if (stop is not null && bank.Has(stop.EventId))
                stop = null;
            // Nor must a sound an effect plays without holding it.
            if (stop is null || (kind == SoundPort.EffectSoundKind && unheld?.Contains(id) == true))
                layers = [.. layers.Select(l => (l.Samples, l.Tuning is { Loop: true } t ? t with { Loop = false } : l.Tuning))];
            var built = new List<(List<(uint Id, byte[] Bytes)> Media, WwiseBank.Tuning? Tuning)>();
            foreach (var (layer, tuning) in layers)
            {
                var sounds = new List<(uint Id, byte[] Bytes)>();
                foreach (var s in layer)
                {
                    bool looped = tuning?.Loop == true && SoundPort.LoopPoints(s) is not null;
                    var store = looped ? loopMedia : media;
                    if (!store.ContainsKey(s) && !failed.Contains(s))
                        try
                        {
                            store[s] = looped
                                ? (SoundPort.UnusedId(bank, $"darksign_media_{s.Name}_loop", taken), WwiseBank.PcmWem(Fsb5.DecodePcm(s), s.Channels, s.Rate, SoundPort.LoopPoints(s)))
                                : (SoundPort.UnusedId(bank, $"darksign_media_{s.Name}", taken), WwiseBank.PcmWem(Fsb5.DecodePcm(s), s.Channels, s.Rate));
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            failed.Add(s); // one undecodable sample must not cost the others
                        }
                    if (store.TryGetValue(s, out var m))
                        sounds.Add(m);
                }
                if (sounds.Count > 0)
                    built.Add((sounds, tuning));
            }
            if (built.Count == 0) { Left("not decodable"); continue; }
            // The same object names the weapon port gives a sound, so one DS3 sound is the same Wwise objects in
            // every bank that holds it (Wwise keeps the first loaded copy of an ID).
            var newLayers = new List<WwiseBank.Layer>();
            for (int l = 0; l < built.Count; l++)
            {
                string tag = $"{letter}{id}{(l == 0 ? "" : $"_layer{l}")}";
                uint actionId = SoundPort.UnusedId(bank, $"darksign_action_{tag}", taken), containerId = SoundPort.UnusedId(bank, $"darksign_container_{tag}", taken);
                newLayers.Add(new WwiseBank.Layer(actionId, containerId,
                    built[l].Media.Select((m, v) => (SoundPort.UnusedId(bank, $"darksign_sound_{tag}{(v == 0 ? "" : $"_{v}")}", taken), m.Id, m.Bytes)).ToList())
                {
                    Tuning = built[l].Tuning,
                    StopActionId = stop is not null ? SoundPort.UnusedId(bank, $"darksign_stop_{tag}", taken) : 0,
                });
            }
            wems.AddRange(built.SelectMany(l => l.Media).Where(m => written.Add(m.Id)));
            bank.AddSound(template, eventId, newLayers, streamed: false, stop);
            result.Events.Add(eventId);
            if (stop is not null)
                result.Events.Add(stop.EventId);
            result.Ported.Add((kind, id));
            if (built.Any(b => b.Tuning?.Loop == true))
                result.Looping.Add((kind, id));
        }
        if (result.Ported.Count == 0)
            return result;
        var bankBytes = bank.WriteOwnBank(result.BankId);
        if (bankBytes is null)
        {
            // The new sounds don't hang under actor-mixers that can be copied: no bank, the template's sounds stay.
            result.Reasons["bank not writable"] = result.Ported.Count;
            result.Ported.Clear();
            result.Events.Clear();
            return result;
        }
        // Not localized (SFX), so it sits in sd\ like the game's own character banks.
        result.BankFile = _ctx.ModPath("sd", result.BankName + ".bnk");
        result.BankBytes = bankBytes.Length;
        Directory.CreateDirectory(Path.GetDirectoryName(result.BankFile)!);
        File.WriteAllBytes(result.BankFile, bankBytes);
        // Loose copies of the media, as the weapon port writes them (SoundPort.Mirror); they wait in the work folder
        // until port-weapons, which empties sd\wem, is done.
        if (Mirror)
            foreach (var (id, wem) in wems)
            {
                var file = id.ToString();
                var path = Path.Combine(Staging(_ctx), file[..2], file + ".wem");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, wem);
                result.Media.Add(Path.Combine(file[..2], file + ".wem"));
            }
        return result;
    }

    /// <summary>
    /// After port-weapons: lists every character bank and its events in the game's sound index (the one port-weapons
    /// wrote, else the game's own), moves the loose media into sd\wem, and adds what it wrote to the enemy port's
    /// manifest so the next build removes it.
    /// </summary>
    public string Finish(IReadOnlyList<Result> results)
    {
        var banks = results.Where(r => r.BankFile is not null).ToList();
        if (banks.Count == 0)
            return "sounds: no character has a DS3 sound bank";
        string index = "index not edited";
        if (Register)
        {
            var path = SoundPort.IndexPath(_ctx);
            byte[]? bytes = File.Exists(path) ? File.ReadAllBytes(path) : _er.Contains(SoundIndex.ArchivePath) ? _er.Read(SoundIndex.ArchivePath) : null;
            if (bytes is not null)
            {
                var soundIndex = SoundIndex.Read(bytes);
                int added = banks.Sum(r => soundIndex.Register(r.BankId, r.Events, sectionOf: _mainBankId));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, soundIndex.Write());
                index = $"{added} events of {banks.Count} banks listed in the game's sound index";
            }
            else
                index = "the game has no sound index to list them in";
        }
        var moved = new List<string>();
        foreach (var rel in banks.SelectMany(r => r.Media).Distinct())
        {
            var from = Path.Combine(Staging(_ctx), rel);
            var to = _ctx.ModPath(["sd", "wem", .. rel.Split(Path.DirectorySeparatorChar)]);
            if (!File.Exists(from))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
            moved.Add(to);
        }
        if (moved.Count > 0)
            File.AppendAllLines(_ctx.ModPath(ChrPort.ManifestFile), moved.Select(m => Path.GetRelativePath(_ctx.ModPath(), m)));
        int encrypted = results.Sum(r => r.Encrypted);
        return $"sounds: {banks.Sum(r => r.Ported.Count)} DS3 sounds in {banks.Count} character banks ({banks.Sum(r => r.BankBytes) >> 20} MB), {index}, " +
               $"{moved.Count} loose media files" + (encrypted > 0 ? $", {encrypted} encrypted DS3 banks read" : "");
    }
}
