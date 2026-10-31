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
        if (todo.Count == 0)
            return result;

        var (bankBytes, _) = erArchives.ReadWithCompression(Bank);
        var bank = WwiseBank.Read(bankBytes);
        // The template: the first Elden Ring player sound in the bank whose event plays a Sound through a Play action.
        var template = templateIds.Select(id => bank.Chain(FxSoundExplore.Fnv1($"Play_c{id:D9}"))).FirstOrDefault(c => c is not null)
                       ?? throw new InvalidDataException($"no template sound found in {Bank}");
        Console.WriteLine($"sounds: {Bank} version {bank.Version}, template event {template.Event.Id}, sound {template.Sound.Id}");

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
            var wem = WwiseBank.PcmWem(pcm, sample.Channels, sample.Rate);
            uint soundId = UnusedId(bank, $"darksign_sound_{letter}{id}"), actionId = UnusedId(bank, $"darksign_action_{letter}{id}");
            uint mediaId = UnusedId(bank, $"darksign_media_{letter}{id}");
            bank.AddSound(template, eventId, soundId, actionId, mediaId, wem);
            result.Add(kind, id);
            bytes += wem.Length;
        }
        if (result.Count > 0)
            File.WriteAllBytes(ctx.ModPath(["sd", .. Bank.TrimStart('/').Split('/')]), bank.Write());
        Console.WriteLine($"sounds: {result.Count} DS3 sounds ported ({bytes / 1024} KB of PCM) into sd{Bank}; left out: " +
                          (reasons.Count == 0 ? "none" : string.Join(", ", reasons.Select(r => $"{r.Value} {r.Key}"))));
        return result;
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
