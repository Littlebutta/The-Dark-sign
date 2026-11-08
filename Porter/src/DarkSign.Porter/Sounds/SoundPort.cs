using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Ports DS3 weapon sounds into Elden Ring under new IDs. A DS3 TAE sound event plays (kind, ID): kind 1 is a
/// character sound (samples c&lt;ID:D9&gt;, variants b, c, d...), kind 5 an effect sound (s&lt;ID:D9&gt;), in DS3's FMOD banks
/// (/sound/*.fsb). Elden Ring plays Wwise events named Play_c&lt;ID:D9&gt; / Play_s&lt;ID:D9&gt; (IDs are FNV-1 hashes of the
/// names). Each DS3 sound is decoded to PCM and added to Elden Ring's main bank (sd/enus/cs_main.bnk) as a new event
/// with ID 900000000 + the DS3 ID, cloned from the event an Elden Ring weapon swing plays.
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

    /// <summary>Where the edited index goes (me3 serves it in place of the game's).</summary>
    public static string IndexPath(PorterContext ctx) => ctx.ModPath("sound", "soundbanksinfo.mobnkinfo");

    /// <summary>The folder the DLL reads (Mods\ER\darksign_sounds); must match TestMenu/src/sound.cpp.</summary>
    public const string DllSoundFolder = "darksign_sounds";
    private const string Bank = "/enus/cs_main.bnk";

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

        var names = todo.SelectMany(w => new[] { "", "b", "c", "d", "e", "f", "g", "h" }.Select(v => $"{Letter(w.Kind)}{w.Id:D9}{v}")).ToHashSet();
        var samples = Ds3Samples(ctx, ds3Archives, names);
        var reasons = new Dictionary<string, int>();
        var events = new List<uint>();
        int bytes = 0, variantsLost = 0;
        foreach (var (kind, id) in todo)
        {
            char letter = Letter(kind)!.Value;
            var name = $"{letter}{id:D9}";
            // DS3 keeps a sound's random variants as separate samples (c000001043, c000001043b, ...c, ...d): every one
            // present is ported, and Elden Ring's random container picks between them like the DS3 event did.
            var present = new[] { "", "b", "c", "d", "e", "f", "g", "h" }
                .Select(v => (Suffix: v, Sample: samples.GetValueOrDefault(name + v))).Where(x => x.Sample is not null).ToList();
            uint eventId = FxSoundExplore.Fnv1($"Play_{letter}{IdBase + id:D9}");
            if (present.Count == 0) { reasons["no DS3 sample"] = reasons.GetValueOrDefault("no DS3 sample") + 1; continue; }
            if (bank.Has(eventId)) { reasons["event ID taken"] = reasons.GetValueOrDefault("event ID taken") + 1; continue; }
            var decoded = new List<(string Suffix, Fsb5.Sample Sample, short[] Pcm)>();
            string? failure = null;
            foreach (var (suffix, s) in present)
            {
                try { decoded.Add((suffix, s!, Fsb5.DecodePcm(s!))); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // One undecodable sample (NVorbis throws its own types too) must not cost every other sound.
                    failure ??= $"decode: {e.Message.Split(' ').Take(4).Aggregate((a, b) => a + " " + b)}";
                }
            }
            if (decoded.Count == 0) { reasons[failure!] = reasons.GetValueOrDefault(failure!) + 1; continue; }
            if (failure is not null) variantsLost++;
            var (_, sample, pcm) = decoded[0];
            if (DllSounds)
            {
                var wav = Wav(pcm, sample.Channels, sample.Rate);
                File.WriteAllBytes(ctx.ModPath(DllSoundFolder, $"{letter}{IdBase + id:D9}.wav"), wav);
                result.Add(kind, id);
                bytes += wav.Length;
                continue;
            }
            var wem = WwiseBank.PcmWem(pcm, sample.Channels, sample.Rate);
            uint soundId = UnusedId(bank, $"darksign_sound_{letter}{id}"), actionId = UnusedId(bank, $"darksign_action_{letter}{id}");
            uint mediaId = UnusedId(bank, $"darksign_media_{letter}{id}"), containerId = UnusedId(bank, $"darksign_container_{letter}{id}");
            var variants = new List<(uint SoundId, uint MediaId, byte[] Wem)>();
            var taken = new HashSet<uint> { soundId, actionId, mediaId, containerId };
            foreach (var (suffix, s, p) in decoded.Skip(1))
            {
                uint vs = UnusedId(bank, $"darksign_sound_{letter}{id}{suffix}", taken), vm = UnusedId(bank, $"darksign_media_{letter}{id}{suffix}", taken);
                variants.Add((vs, vm, WwiseBank.PcmWem(p, s.Channels, s.Rate)));
            }
            // Embedded in the bank like nearly all of Elden Ring's own sounds (17,018 embedded, 3 streamed): the
            // streamed loose wems (sd\wem\NN\<ID>.wem) stayed silent in game. DARKSIGN_STREAM_SOUNDS=1 streams them.
            if (StreamSounds || Mirror)
                foreach (var (m, w) in variants.Select(v => (v.MediaId, v.Wem)).Prepend((mediaId, wem)))
                {
                    var mediaName = m.ToString();
                    File.WriteAllBytes(ctx.ModPath("sd", "wem", mediaName[..2], mediaName + ".wem"), w); // me3: sd/wem/NN/<id>.wem
                }
            bank.AddSound(template, eventId, soundId, actionId, containerId, mediaId, wem, streamed: StreamSounds, variants: variants);
            bytes += variants.Sum(v => v.Wem.Length);
            events.Add(eventId);
            result.Add(kind, id);
            bytes += wem.Length;
        }
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
        Console.WriteLine($"sounds: {result.Count} DS3 sounds ported ({bytes / 1024} KB of PCM {(StreamSounds ? "streamed from sd/wem" : "embedded")}, {bank.OwnContainerCount} in a container of their own, {bank.ChildrenAdded} added to their parent, {bank.VariantsAdded} DS3 random variants alongside them{(variantsLost > 0 ? $", {variantsLost} sounds lost a variant that didn't decode" : "")}) " +
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

    private static uint UnusedId(WwiseBank bank, string name, HashSet<uint>? taken = null)
    {
        uint id = FxSoundExplore.Fnv1(name);
        while (bank.Has(id) || taken?.Contains(id) == true)
            id++;
        taken?.Add(id);
        return id;
    }

    // Every DS3 sample by name; loose files in the game's sound folder win over archived ones of the same name.
    private static Dictionary<string, Fsb5.Sample> Ds3Samples(PorterContext ctx, GameArchives ds3, HashSet<string> names)
    {
        var samples = new Dictionary<string, Fsb5.Sample>();
        void AddFile(byte[] bytes)
        {
            if (!Fsb5.Is(bytes))
                return;
            foreach (var s in Fsb5.Read(bytes, names.Contains).Samples)
                samples.TryAdd(s.Name, s);
        }
        var loose = Path.Combine(ctx.GameDir(PorterGame.DarkSouls3), "sound");
        if (Directory.Exists(loose))
            foreach (var file in Directory.EnumerateFiles(loose, "*.fsb").Order(StringComparer.Ordinal))
            {
                try { AddFile(File.ReadAllBytes(file)); }
                catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException)
                {
                    Console.WriteLine($"sounds: skipped {Path.GetFileName(file)} ({e.GetType().Name}: {e.Message})");
                }
            }
        foreach (var entry in ds3.Entries.Where(e => e.Path?.StartsWith("/sound/") == true && e.Path.EndsWith(".fsb")).OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            try { AddFile(ds3.Read(entry.Path!)); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        }
        return samples;
    }

    /// <summary>The (kind, ID) a TAE sound event plays, for the types that start with them.</summary>
    public static (int Kind, int Id)? Sound(TAE.Event e) =>
        e.Type is 128 or 129 or 130 or 131 && e.GetParameterBytes(false) is { Length: >= 8 } b
            ? (BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4))
            : null;
}
