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

    /// <summary>The folder the DLL reads (Mods\ER\darksign_sounds); must match TestMenu/src/sound.cpp.</summary>
    public const string DllSoundFolder = "darksign_sounds";
    private const string Bank = "/enus/cs_main.bnk";

    public sealed class Result
    {
        private readonly HashSet<(int Kind, int Id)> _ported = [];

        public int? Map(int kind, int id) => _ported.Contains((kind, id)) ? IdBase + id : null;

        internal void Add(int kind, int id) => _ported.Add((kind, id));

        public int Count => _ported.Count;
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
        if (DllSounds && File.Exists(bankPath))
            File.Delete(bankPath); // a bank from an earlier build would still be loaded
        if (todo.Count == 0)
            return result;

        var bankBytes = erArchives.Read(Bank); // not DCX-compressed
        var bank = WwiseBank.Read(bankBytes);
        // The template: the first Elden Ring player sound in the bank whose event plays a Sound through a Play action.
        var template = templateIds.Select(id => bank.Chain(FxSoundExplore.Fnv1($"Play_c{id:D9}"))).FirstOrDefault(c => c is not null)
                       ?? throw new InvalidDataException($"no template sound found in {Bank}");
        Console.WriteLine($"sounds: {Bank} version {bank.Version}, template event {template.Event.Id}, sound {template.Sound.Id}; {Tuning(bank, template.Sound)}");

        // How well the node reader knows this bank (Spellforge reads every node of the game's banks to its exact size;
        // a type that falls short here means the new layout falls back to the older one for it).
        Console.WriteLine("sounds: nodes read exactly: " + string.Join(", ", bank.Objects
            .Where(o => o.Type is WwiseBank.SoundType or WwiseBank.RanSeqCntrType or WwiseBank.SwitchCntrType or WwiseBank.ActorMixerType or WwiseBank.LayerCntrType)
            .GroupBy(o => o.Type).OrderBy(g => g.Key).Select(g => $"type {g.Key} {g.Count(o => WwiseNode.Read(o) is not null)}/{g.Count()}")));

        var names = todo.SelectMany(w => new[] { "", "b", "c", "d", "e", "f", "g", "h" }.Select(v => $"{Letter(w.Kind)}{w.Id:D9}{v}")).ToHashSet();
        var samples = Ds3Samples(ctx, ds3Archives, names);
        var reasons = new Dictionary<string, int>();
        int bytes = 0;
        foreach (var (kind, id) in todo)
        {
            char letter = Letter(kind)!.Value;
            var name = $"{letter}{id:D9}";
            var sample = samples.GetValueOrDefault(name) ?? "bcdefgh".Select(v => samples.GetValueOrDefault(name + v)).FirstOrDefault(s => s is not null);
            uint eventId = FxSoundExplore.Fnv1($"Play_{letter}{IdBase + id:D9}");
            if (sample is null) { reasons["no DS3 sample"] = reasons.GetValueOrDefault("no DS3 sample") + 1; continue; }
            if (bank.Has(eventId)) { reasons["event ID taken"] = reasons.GetValueOrDefault("event ID taken") + 1; continue; }
            short[] pcm;
            try { pcm = Fsb5.DecodePcm(sample); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
            {
                var reason = $"decode: {e.Message.Split(' ').Take(4).Aggregate((a, b) => a + " " + b)}";
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                continue;
            }
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
            // Embedded in the bank like nearly all of Elden Ring's own sounds (17,018 embedded, 3 streamed): the
            // streamed loose wems (sd\wem\NN\<ID>.wem) stayed silent in game. DARKSIGN_STREAM_SOUNDS=1 streams them.
            if (StreamSounds)
            {
                var mediaName = mediaId.ToString();
                File.WriteAllBytes(ctx.ModPath("sd", "wem", mediaName[..2], mediaName + ".wem"), wem); // me3: sd/wem/NN/<id>.wem
            }
            bank.AddSound(template, eventId, soundId, actionId, containerId, mediaId, wem, streamed: StreamSounds);
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
        if (result.Count > 0)
            File.WriteAllBytes(bankPath, bank.Write());
        Console.WriteLine($"sounds: {result.Count} DS3 sounds ported ({bytes / 1024} KB of PCM {(StreamSounds ? "streamed from sd/wem" : "embedded")}, {bank.OwnContainerCount} in a container of their own, {bank.ChildrenAdded} added to their parent) " +
                          $"into sd{Bank}; left out: " +
                          (reasons.Count == 0 ? "none" : string.Join(", ", reasons.Select(r => $"{r.Value} {r.Key}"))));
        return result;
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

    private static uint UnusedId(WwiseBank bank, string name)
    {
        uint id = FxSoundExplore.Fnv1(name);
        while (bank.Has(id))
            id++;
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
            foreach (var file in Directory.EnumerateFiles(loose, "*.fsb").Order())
                AddFile(File.ReadAllBytes(file));
        foreach (var entry in ds3.Entries.Where(e => e.Path?.StartsWith("/sound/") == true && e.Path.EndsWith(".fsb")).OrderBy(e => e.Path))
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
