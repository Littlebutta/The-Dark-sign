using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Ports DS3 weapon sounds into Elden Ring under new IDs. A DS3 TAE sound event plays (kind, ID): kind 1 is a
/// character sound (samples c&lt;ID:D9&gt;, variants b, c, d...), kind 5 an effect sound (s&lt;ID:D9&gt;), in DS3's FMOD banks
/// (/sound/*.fsb). Elden Ring plays Wwise events named Play_c&lt;ID:D9&gt; / Play_s&lt;ID:D9&gt; (IDs are FNV-1 hashes of the
/// names). Each DS3 sound is decoded to PCM and added to Elden Ring's main bank (sd/enus/cs_main.bnk) as a new event
/// with ID 900000000 + the DS3 ID, cloned from the event an Elden Ring weapon swing plays.
/// Which samples a DS3 sound plays is read from DS3's event files (<see cref="Fev"/>): an event's waves can be named
/// differently from it, and each of its layers becomes a Play action of the new event.
/// </summary>
public static class SoundPort
{
    public const int IdBase = 900_000_000;
    private static readonly bool StreamSounds = Environment.GetEnvironmentVariable("DARKSIGN_STREAM_SOUNDS") == "1";

    /// <summary>
    /// DARKSIGN_SOUND_METHOD=dll (launcher fallback "sounds" D): Wwise is left alone. Each DS3 sample is written as a
    /// plain WAV to Mods\ER\darksign_sounds\&lt;c|s&gt;&lt;ID&gt;.wav, the TAE events still name the new IDs, and the test
    /// menu DLL plays the WAV on its own audio stream when the game runs such an event (DOOM RING's approach: its own
    /// stream, no Wwise). Elden Ring's bank isn't changed, so a bank problem can't silence the game.
    /// </summary>
    public static readonly bool DllSounds =
        string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_SOUND_METHOD"), "dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DARKSIGN_SOUND_BANK=own (launcher fallback "sounds" E): cs_main is left alone; the ported sounds go into a bank
    /// of their own, sd/cs_c9990.bnk, which Torrent's NpcParam rows load (SoundAddBankId), as Yonder's "Custom
    /// Soundbanks" guide keeps a mod bank always loaded. The game's index lists the bank and its events.
    /// </summary>
    public static readonly bool OwnBank =
        string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_SOUND_BANK"), "own", StringComparison.OrdinalIgnoreCase);

    /// <summary>The own bank's number (cs_c9990): unused by Elden Ring, and fits NpcParam's s16 bank fields.</summary>
    public const int OwnBankNumber = 9990;
    public static string OwnBankName => $"cs_c{OwnBankNumber:D4}";

    /// <summary>
    /// The game's own event index (/sound/soundbanksinfo.mobnkinfo) lists the new events under their bank unless
    /// DARKSIGN_SOUND_INDEX=0. Every event Elden Ring ships is in it; new ones weren't, and new sound numbers stayed
    /// silent while the same audio played under an existing number.
    /// </summary>
    private static readonly bool Register = Environment.GetEnvironmentVariable("DARKSIGN_SOUND_INDEX") != "0";

    /// <summary>
    /// Embedded media also gets a loose copy at sd\wem\NN\&lt;ID&gt;.wem unless DARKSIGN_SOUND_MIRROR=0: Nightreign
    /// Ultimate Voices found that Wwise's PrepareEvent loading fails with the media only embedded and works with a
    /// byte-identical loose copy, and which way the game loads isn't known.
    /// </summary>
    private static readonly bool Mirror = Environment.GetEnvironmentVariable("DARKSIGN_SOUND_MIRROR") != "0";

    /// <summary>
    /// A DS3 sound's samples are the waves its event plays, read from DS3's event files (<see cref="Fev"/>), unless
    /// DARKSIGN_SOUND_FEV=0 (launcher fallback "sounds" G): then only samples named like the sound are taken, as
    /// builds before the event reader did. That misses sounds whose waves are named apart and every layer but one.
    /// </summary>
    private static readonly bool UseFev = Environment.GetEnvironmentVariable("DARKSIGN_SOUND_FEV") != "0";

    /// <summary>Where the edited index goes (me3 serves it in place of the game's).</summary>
    public static string IndexPath(PorterContext ctx) => ctx.ModPath("sound", "soundbanksinfo.mobnkinfo");

    /// <summary>The folder the DLL reads (Mods\ER\darksign_sounds); must match TestMenu/src/sound.cpp.</summary>
    public const string DllSoundFolder = "darksign_sounds";
    internal const string Bank = "/enus/cs_main.bnk";

    public sealed class Result
    {
        private readonly HashSet<(int Kind, int Id)> _ported = [];

        public int? Map(int kind, int id) => _ported.Contains((kind, id)) ? IdBase + id : null;

        internal void Add(int kind, int id) => _ported.Add((kind, id));

        public int Count => _ported.Count;

        /// <summary>The sounds went into their own bank (method E): Torrent's NpcParam rows must load it.</summary>
        public bool InOwnBank { get; internal set; }
    }

    /// <summary>The sample-name letter for a TAE sound kind, or null for kinds that aren't ported.</summary>
    public static char? Letter(int kind) => kind switch { 1 => 'c', 5 => 's', _ => null };

    public static Result Apply(PorterContext ctx, GameArchives erArchives, GameArchives ds3Archives, IEnumerable<(int Kind, int Id)> wanted,
        IEnumerable<int> templateIds)
    {
        var result = new Result();
        var todo = wanted.Where(w => Letter(w.Kind) is not null && w.Id > 0 && w.Id < 100_000_000).Distinct().ToList();
        // Streamed wems live in sd\wem\NN: me3 looks a wem up as sd/wem/<id>.wem, then sd/wem/<first two digits>/<id>.wem
        // (garyttierney/me3 crates/mod-host-assets/src/wwise.rs). Mods\ER\wem is from a build that put them outside sd.
        foreach (var wemDir in new[] { ctx.ModPath("sd", "wem"), ctx.ModPath("wem"), ctx.ModPath(DllSoundFolder) })
            if (Directory.Exists(wemDir))
                Directory.Delete(wemDir, true);
        var bankPath = ctx.ModPath(["sd", .. Bank.TrimStart('/').Split('/')]);
        // A bank from an earlier build would still be loaded (stale events, or a bank cloned from an older game
        // patch); it is written again below whenever this build ports a sound into it.
        foreach (var stale in new[] { bankPath, IndexPath(ctx), ctx.ModPath("sd", OwnBankName + ".bnk"), ctx.ModPath("sd", "enus", OwnBankName + ".bnk") })
            if (File.Exists(stale))
                File.Delete(stale);
        if (todo.Count == 0)
            return result;

        // The DLL method (D) never touches Wwise: no bank, no template needed (a missing template must not cost it
        // its sounds).
        WwiseBank bank = new();
        (WwiseBank.HircObject Event, WwiseBank.HircObject Action, WwiseBank.HircObject Sound) template = default;
        if (!DllSounds)
        {
            var bankBytes = erArchives.Read(Bank); // not DCX-compressed
            bank = WwiseBank.Read(bankBytes);
            // The template: the first Elden Ring player sound in the bank whose event plays a Sound through a Play action.
            template = templateIds.Select(id => bank.Chain(FxSoundExplore.Fnv1($"Play_c{id:D9}"))).FirstOrDefault(c => c is not null)
                       ?? throw new InvalidDataException($"no template sound found in {Bank}");
            Console.WriteLine($"sounds: {Bank} version {bank.Version}, template event {template.Event.Id}, sound {template.Sound.Id}; {Tuning(bank, template.Sound)}");

            // How well the node reader knows this bank (Spellforge reads every node of the game's banks to its exact size;
            // a type that falls short here means the new layout falls back to the older one for it).
            Console.WriteLine("sounds: nodes read exactly: " + string.Join(", ", bank.Objects
                .Where(o => o.Type is WwiseBank.SoundType or WwiseBank.RanSeqCntrType or WwiseBank.SwitchCntrType or WwiseBank.ActorMixerType or WwiseBank.LayerCntrType)
                .GroupBy(o => o.Type).OrderBy(g => g.Key).Select(g => $"type {g.Key} {g.Count(o => WwiseNode.Read(o) is not null)}/{g.Count()}")));
        }

        // Which samples a sound plays. DS3's event of that name says (Fev): each of its layers plays one sound
        // definition, whose waves are random variants and can be named anything. Without an event, or when none of its
        // waves is a sample, the name rule: samples named like the sound, with variants b..h (c000001043, c000001043b...).
        string[] suffixes = ["", "b", "c", "d", "e", "f", "g", "h"];
        var ds3Events = UseFev ? Ds3Events(ctx, ds3Archives) : [];
        if (!UseFev)
            Console.WriteLine("sounds: DS3's event files are not read (DARKSIGN_SOUND_FEV=0): samples are found by the sound's name only");
        var names = todo.SelectMany(w => suffixes.Select(v => $"{Letter(w.Kind)}{w.Id:D9}{v}")).ToHashSet();
        foreach (var w in todo)
            if (ds3Events.TryGetValue($"{Letter(w.Kind)}{w.Id:D9}", out var known))
                names.UnionWith(known.Event.Layers.SelectMany(l => l.Waves).Select(Fev.SampleName));
        var samples = Ds3Samples(ctx, ds3Archives, names);
        // A wave is the sample of its name in the event file's own bank (fdp_main.fev's waves are in fdp_main.fsb),
        // else the first of that name anywhere, as the name rule takes it.
        Fsb5.Sample? Pick(string sample, string? fsb) =>
            samples.TryGetValue(sample, out var found) ? found.FirstOrDefault(s => s.Fsb == fsb, found[0]).Sample : null;

        var reasons = new Dictionary<string, int>();
        var events = new List<uint>();
        int bytes = 0, variantsLost = 0, withEvent = 0, byEvent = 0, onlyByEvent = 0, layered = 0, dllLayersDropped = 0;
        List<string> missing = [], undecodable = [];
        // One sample is decoded and embedded once, however many sounds play it (DS3 events share waves).
        var media = new Dictionary<Fsb5.Sample, (uint Id, byte[] Bytes)>(ReferenceEqualityComparer.Instance);
        var failures = new Dictionary<Fsb5.Sample, string>(ReferenceEqualityComparer.Instance);
        HashSet<uint> taken = [], written = [];
        foreach (var (kind, id) in todo)
        {
            char letter = Letter(kind)!.Value;
            var name = $"{letter}{id:D9}";
            var ruleNames = suffixes.Select(v => name + v).ToList();
            List<List<Fsb5.Sample>> layers = [];
            if (ds3Events.TryGetValue(name, out var ds3Event))
            {
                withEvent++;
                layers = ds3Event.Event.Layers
                    .Select(l => l.Waves.Select(Fev.SampleName).Distinct().Select(w => Pick(w, ds3Event.Fsb)).OfType<Fsb5.Sample>().ToList())
                    .Where(l => l.Count > 0).ToList();
                if (layers.Count > 0)
                {
                    byEvent++;
                    if (!ruleNames.Any(samples.ContainsKey)) onlyByEvent++;
                }
            }
            if (layers.Count == 0 && ruleNames.Select(n => Pick(n, null)).OfType<Fsb5.Sample>().ToList() is { Count: > 0 } named)
                layers.Add(named);
            uint eventId = FxSoundExplore.Fnv1($"Play_{letter}{IdBase + id:D9}");
            if (layers.Count == 0) { reasons["no DS3 sample"] = reasons.GetValueOrDefault("no DS3 sample") + 1; missing.Add(name); continue; }
            if (bank.Has(eventId)) { reasons["event ID taken"] = reasons.GetValueOrDefault("event ID taken") + 1; continue; }

            // Each layer's samples as media (PCM wems; WAV files for the DLL). A layer none of whose samples decode
            // is left out; a sound none of whose layers decode is not ported.
            var built = new List<List<(uint Id, byte[] Bytes)>>();
            string? failure = null;
            foreach (var layer in layers)
            {
                var sounds = new List<(uint Id, byte[] Bytes)>();
                foreach (var s in layer)
                {
                    if (!media.ContainsKey(s) && !failures.ContainsKey(s))
                    {
                        try
                        {
                            var pcm = Fsb5.DecodePcm(s);
                            media[s] = DllSounds
                                ? (0, Wav(pcm, s.Channels, s.Rate))
                                : (UnusedId(bank, $"darksign_media_{s.Name}", taken), WwiseBank.PcmWem(pcm, s.Channels, s.Rate));
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            // One undecodable sample (NVorbis throws its own types too) must not cost every other sound.
                            failures[s] = $"decode: {e.Message.Split(' ').Take(4).Aggregate((a, b) => a + " " + b)}"
                                          + (e.Message.Contains("CRC") ? $" {s.SetupCrc} ({s.Channels} ch, {s.Rate} Hz)" : "");
                        }
                    }
                    if (media.TryGetValue(s, out var m)) sounds.Add(m);
                    else failure ??= failures[s];
                }
                if (sounds.Count > 0)
                    built.Add(sounds);
            }
            if (built.Count == 0) { reasons[failure!] = reasons.GetValueOrDefault(failure!) + 1; undecodable.Add($"{name} ({failure})"); continue; }
            if (failure is not null) variantsLost++;
            if (DllSounds)
            {
                // The DLL plays one WAV per sound: the first layer's first sample.
                var wav = built[0][0].Bytes;
                File.WriteAllBytes(ctx.ModPath(DllSoundFolder, $"{letter}{IdBase + id:D9}.wav"), wav);
                result.Add(kind, id);
                bytes += wav.Length;
                dllLayersDropped += built.Count - 1;
                continue;
            }
            // Layer 0 keeps the IDs a single-layer sound always had; further layers and variants get their own.
            var newLayers = new List<WwiseBank.Layer>();
            for (int l = 0; l < built.Count; l++)
            {
                string tag = $"{letter}{id}{(l == 0 ? "" : $"_layer{l}")}";
                uint actionId = UnusedId(bank, $"darksign_action_{tag}", taken), containerId = UnusedId(bank, $"darksign_container_{tag}", taken);
                var sounds = built[l].Select((m, v) => (UnusedId(bank, $"darksign_sound_{tag}{(v == 0 ? "" : $"_{v}")}", taken), m.Id, m.Bytes)).ToList();
                newLayers.Add(new WwiseBank.Layer(actionId, containerId, sounds));
            }
            foreach (var (mediaId, wem) in built.SelectMany(l => l).Where(m => written.Add(m.Id)))
            {
                bytes += wem.Length;
                // Embedded in the bank like nearly all of Elden Ring's own sounds (17,018 embedded, 3 streamed): the
                // streamed loose wems (sd\wem\NN\<ID>.wem) stayed silent in game. DARKSIGN_STREAM_SOUNDS=1 streams them.
                var mediaName = mediaId.ToString();
                if (StreamSounds || Mirror)
                    File.WriteAllBytes(ctx.ModPath("sd", "wem", mediaName[..2], mediaName + ".wem"), wem); // me3: sd/wem/NN/<id>.wem
            }
            bank.AddSound(template, eventId, newLayers, streamed: StreamSounds);
            if (newLayers.Count > 1) layered++;
            events.Add(eventId);
            result.Add(kind, id);
        }
        if (UseFev)
            Console.WriteLine($"sounds: {withEvent} of {todo.Count} wanted sounds have a DS3 event; {byEvent} take their samples from its waves " +
                              $"({onlyByEvent} of them have no sample named like the sound)");
        if (bank.LayersAdded > 0)
            Console.WriteLine($"sounds: {bank.LayersAdded} extra layers added to {layered} sounds (one Play action per layer, all starting together)");
        if (dllLayersDropped > 0)
            Console.WriteLine($"sounds: {dllLayersDropped} extra layers left out: the DLL plays one WAV per sound (the first layer)");
        // Which ones, so the next pass can look for them elsewhere.
        if (missing.Count > 0)
            Console.WriteLine($"sounds: no DS3 sample ({(UseFev ? "none of the sound's event waves, and none " : "none ")}named like the sound): {string.Join(", ", missing)}");
        if (undecodable.Count > 0)
            Console.WriteLine($"sounds: not decodable: {string.Join(", ", undecodable)}");
        if (DllSounds)
        {
            Console.WriteLine($"sounds: {result.Count} DS3 sounds written as WAV files to {DllSoundFolder} for the DLL to play " +
                              $"({bytes / 1024} KB; Elden Ring's bank unchanged); left out: " +
                              (reasons.Count == 0 ? "none" : string.Join(", ", reasons.Select(r => $"{r.Value} {r.Key}"))));
            return result;
        }
        uint eventBank = bank.BankId;
        if (result.Count > 0)
        {
            byte[]? own = OwnBank ? bank.WriteOwnBank(FxSoundExplore.Fnv1(OwnBankName)) : null;
            if (own is not null)
            {
                // Not localized (SFX), so it sits in sd\ like the game's other character banks.
                File.WriteAllBytes(ctx.ModPath("sd", OwnBankName + ".bnk"), own);
                eventBank = FxSoundExplore.Fnv1(OwnBankName);
                result.InOwnBank = true;
                Console.WriteLine($"sounds: method E: {result.Count} sounds in sd\\{OwnBankName}.bnk ({own.Length / 1024} KB), cs_main left as the game has it");
            }
            else
            {
                if (OwnBank)
                    Console.WriteLine("sounds: method E: the new sounds don't hang under actor-mixers that can be copied; they go into cs_main instead");
                File.WriteAllBytes(bankPath, bank.Write());
            }
            RegisterEvents(ctx, erArchives, eventBank, bank.BankId, events);
        }
        Console.WriteLine($"sounds: {result.Count} DS3 sounds ported ({bytes / 1024} KB of PCM {(StreamSounds ? "streamed from sd/wem" : "embedded")}, {bank.OwnContainerCount} in a container of their own, {bank.ChildrenAdded} added to their parent, {bank.VariantsAdded} DS3 random variants alongside them, {bank.LayersAdded} extra layers{(variantsLost > 0 ? $", {variantsLost} sounds lost a variant that didn't decode" : "")}) " +
                          $"into sd{Bank}; left out: " +
                          (reasons.Count == 0 ? "none" : string.Join(", ", reasons.Select(r => $"{r.Value} {r.Key}"))));
        return result;
    }

    // Lists the new events in the game's own index (see SoundIndex), under the bank that holds them.
    private static void RegisterEvents(PorterContext ctx, GameArchives erArchives, uint bankId, uint mainBankId, List<uint> events)
    {
        if (erArchives.Contains("/sound/eventinfo.eib"))
            Console.WriteLine("sounds: index: this game also has /sound/eventinfo.eib (Nightreign's second index); it isn't edited");
        if (!Register)
        {
            Console.WriteLine("sounds: index: not edited (DARKSIGN_SOUND_INDEX=0)");
            return;
        }
        if (!erArchives.Contains(SoundIndex.ArchivePath))
        {
            Console.WriteLine($"sounds: index: {SoundIndex.ArchivePath} not found in the game's archives; not edited");
            return;
        }
        try
        {
            var index = SoundIndex.Read(erArchives.Read(SoundIndex.ArchivePath));
            bool knewBank = index.HasBank(bankId);
            int added = index.Register(bankId, events, sectionOf: mainBankId);
            var path = IndexPath(ctx);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, index.Write());
            Console.WriteLine($"sounds: index: {added} new events listed under bank {bankId}{(knewBank ? "" : " (bank added)")} in sound\\soundbanksinfo.mobnkinfo " +
                              $"({index.SectionCount} sections)");
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            // An index this reader doesn't know stays the game's own.
            Console.WriteLine($"sounds: index: left as the game has it ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>A plain 16-bit PCM WAV file.</summary>
    public static byte[] Wav(short[] pcm, int channels, int rate)
    {
        var w = new MemoryStream();
        void U16(int v) => w.Write(BitConverter.GetBytes((ushort)v));
        void U32(int v) => w.Write(BitConverter.GetBytes(v));
        w.Write("RIFF"u8);
        U32(36 + pcm.Length * 2);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        U32(16);
        U16(1);
        U16(channels);
        U32(rate);
        U32(rate * channels * 2);
        U16(channels * 2);
        U16(16);
        w.Write("data"u8);
        U32(pcm.Length * 2);
        var data = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, data, 0, data.Length);
        w.Write(data);
        return w.ToArray();
    }

    // The template's volume, pitch and filters up its parents (they add up in Wwise), for the log: the port keeps the
    // volume and leaves pitch and filters out of the new sounds.
    private static string Tuning(WwiseBank bank, WwiseBank.HircObject sound)
    {
        var parts = new List<string>();
        var obj = sound;
        for (int depth = 0; depth < 8 && WwiseNode.Read(obj) is { } node; depth++)
        {
            string P(byte id, string unit) => node.Props.TryGetValue(id, out var v) ? $" {WwiseNode.AsFloat(v):0.#}{unit}" : "";
            parts.Add($"{obj.Type}:{obj.Id}{P(WwiseNode.PropVolume, " dB")}{P(WwiseNode.PropPitch, " cents")}{P(WwiseNode.PropLowPass, " lpf")}{P(WwiseNode.PropHighPass, " hpf")}");
            if (bank.Get(node.Parent(obj.Data)) is not { } up)
                break;
            obj = up;
        }
        return parts.Count == 0 ? "template nodes not readable (older layout used)" : "template chain " + string.Join(" < ", parts);
    }

    internal static uint UnusedId(WwiseBank bank, string name, HashSet<uint>? taken = null)
    {
        uint id = FxSoundExplore.Fnv1(name);
        while (bank.Has(id) || taken?.Contains(id) == true)
            id++;
        taken?.Add(id);
        return id;
    }

    // The wanted DS3 samples by name, each with the FSB it is in (file name without extension). The first of a name
    // is the one the name rule takes: loose files in the game's sound folder come before archived ones.
    private static Dictionary<string, List<(string Fsb, Fsb5.Sample Sample)>> Ds3Samples(PorterContext ctx, GameArchives ds3, HashSet<string> names)
    {
        var samples = new Dictionary<string, List<(string Fsb, Fsb5.Sample Sample)>>();
        int encryptedFiles = 0, encryptedSamples = 0;
        void AddFile(string path, byte[] bytes)
        {
            if (!Fsb5.Is(bytes))
            {
                // Encrypted FSBs are not read; how much sits in them is logged so a missing sound can be looked for there.
                if (Fsb5.EncryptedSampleCount(bytes) is > 0 and var count)
                {
                    encryptedFiles++;
                    encryptedSamples += count;
                }
                return;
            }
            var fsb = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            foreach (var s in Fsb5.Read(bytes, names.Contains).Samples)
            {
                if (!samples.TryGetValue(s.Name, out var list))
                    samples[s.Name] = list = [];
                list.Add((fsb, s));
            }
        }
        var loose = Path.Combine(ctx.GameDir(PorterGame.DarkSouls3), "sound");
        if (Directory.Exists(loose))
            foreach (var file in Directory.EnumerateFiles(loose, "*.fsb").Order(StringComparer.Ordinal))
            {
                try { AddFile(file, File.ReadAllBytes(file)); }
                catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException)
                {
                    Console.WriteLine($"sounds: skipped {Path.GetFileName(file)} ({e.GetType().Name}: {e.Message})");
                }
            }
        foreach (var entry in ds3.Entries.Where(e => e.Path?.StartsWith("/sound/") == true && e.Path.EndsWith(".fsb")).OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            try { AddFile(entry.Path!, ds3.Read(entry.Path!)); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        }
        if (encryptedSamples > 0)
            Console.WriteLine($"sounds: {encryptedSamples} DS3 samples sit in {encryptedFiles} encrypted FSB files, which are not read");
        return samples;
    }

    // Every DS3 event by name, with the bank its waves are in (the event file's name: fdp_main.fev goes with
    // fdp_main.fsb). Loose files in the game's sound folder come before archived ones of the same name. The player's
    // sounds are in the main banks, so those are read first (an assumption: 985 DS3 sound names are in more than one
    // event file, 53 of them with different waves, e.g. a character's bank with its own waves under a shared name).
    internal static Dictionary<string, (Fev.Event Event, string Fsb)> Ds3Events(PorterContext ctx, GameArchives ds3)
    {
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        var loose = Path.Combine(ctx.GameDir(PorterGame.DarkSouls3), "sound");
        if (Directory.Exists(loose))
            foreach (var file in Directory.EnumerateFiles(loose, "*.fev"))
                files.TryAdd(Path.GetFileNameWithoutExtension(file), () => File.ReadAllBytes(file));
        foreach (var entry in ds3.Entries.Where(e => e.Path?.StartsWith("/sound/") == true && e.Path.EndsWith(".fev")))
            files.TryAdd(Path.GetFileNameWithoutExtension(entry.Path!), () => ds3.Read(entry.Path!));

        var events = new Dictionary<string, (Fev.Event Event, string Fsb)>();
        int read = 0, records = 0, skipped = 0, unreadable = 0;
        foreach (var (name, bytes) in files.OrderBy(f => f.Key.StartsWith("fdp_main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            Fev fev;
            try { fev = Fev.Read(bytes()); }
            catch (Exception e) when (e is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException)
            {
                unreadable++;
                continue;
            }
            read++;
            records += fev.Events.Count;
            skipped += fev.Skipped;
            foreach (var ev in fev.Events.Values)
                events.TryAdd(ev.Name, (ev, name.ToLowerInvariant()));
        }
        Console.WriteLine($"sounds: {read} DS3 event files (.fev) read: {records} events under {events.Count} names, {skipped} records skipped" +
                          (unreadable > 0 ? $", {unreadable} files could not be read" : ""));
        return events;
    }

    /// <summary>The (kind, ID) a TAE sound event plays, for the types that start with them.</summary>
    public static (int Kind, int Id)? Sound(TAE.Event e) =>
        e.Type is 128 or 129 or 130 or 131 && e.GetParameterBytes(false) is { Length: >= 8 } b
            ? (BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4))
            : null;
}
