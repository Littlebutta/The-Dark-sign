using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// cutscene-sound-survey [names] [er:names]: what a cutscene's sound is made of in both games, read from the game
/// files, so the sound port is built on what the files hold rather than on guesses. For each cutscene it lists the
/// timeline events that carry sound parameters (seType/seId and the like) and, per sound, which sound type letter and
/// which bank holds it: DS3's FMOD event files (fdp_sm45.fev is the Painted World's cutscene sound file by its name)
/// and Elden Ring's Wwise banks (sd\sAA_BB_NNNN.bnk / sd\enus\sAA_BB_NNNN.bnk, one per cutscene, by the UXM file
/// list), with the game's sound index (soundbanksinfo.mobnkinfo) entries for those banks.
/// Writes Work\logs\cutscene-sound-survey-*.txt. Reads only.
/// </summary>
public static class CutsceneSoundSurvey
{
    // Sound type letters by seType number, as the paramdefs' SOUND_TYPE enum names them (a environment, c character,
    // f menu, o object, p cutscene, s effect, m BGM, v voice, x floor material, b armour material, g ghost). Which
    // number is which is checked below by looking each sound up under every letter.
    private const string Letters = "acfopsmvxbg";

    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var ds3Names = args.Where(a => !a.StartsWith("er:", StringComparison.OrdinalIgnoreCase)).ToList();
        var erNames = args.Where(a => a.StartsWith("er:", StringComparison.OrdinalIgnoreCase)).Select(a => a[3..]).ToList();
        if (ds3Names.Count == 0)
            ds3Names = ["s45_00_0000", "s45_00_0010", "s45_00_0030", "s40_00_0000", "s30_00_0000"];
        if (erNames.Count == 0)
            erNames = ["s10_00_0050", "s10_00_0010", "s19_00_0010", "s16_00_0010", "s12_02_0000", "s35_00_0000"];

        var r = new StringBuilder();
        void Section(string title, Func<IEnumerable<string>> lines)
        {
            r.AppendLine($"--- {title}");
            try
            {
                foreach (var line in lines())
                    r.AppendLine("  " + line);
            }
            catch (Exception ex)
            {
                r.AppendLine($"  error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Section("ER sound files", () => ErSoundFiles(er));
        Section("ER sound index", () => IndexSummary(er));
        foreach (var name in erNames)
            Section($"ER {name}", () => ErCutscene(er, name));
        Section("DS3 sound files", () => Ds3SoundFiles(ctx, ds3));
        var ds3Events = SoundPort.Ds3Events(ctx, ds3);
        Section("DS3 cutscene sound event files", () => Ds3EventFiles(ctx, ds3));
        Section("DS3 events by letter (all event files)", () => ByLetter(ds3Events));
        foreach (var name in ds3Names)
            Section($"DS3 {name}", () => Ds3Cutscene(ctx, ds3, name, ds3Events));

        var log = ctx.WorkPath("logs", $"cutscene-sound-survey-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, r.ToString());
        Console.WriteLine(r.ToString());
        Console.WriteLine($"report written to {log}");
        return 0;
    }

    private static IEnumerable<string> ErSoundFiles(GameArchives er)
    {
        var banks = er.Entries.Where(e => e.Path is not null && e.Path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path!).ToList();
        yield return $"{banks.Count} named banks; cutscene-named: " +
                     string.Join(" ", banks.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"/s\d\d_\d\d_\d{4}\.bnk$")).Order());
        yield return "first paths: " + string.Join(" ", banks.Order().Take(10));
    }

    private static IEnumerable<string> IndexSummary(GameArchives er)
    {
        if (!er.Contains(SoundIndex.ArchivePath))
        {
            yield return "no " + SoundIndex.ArchivePath;
            yield break;
        }
        var index = SoundIndex.Read(er.Read(SoundIndex.ArchivePath));
        yield return $"{index.SectionCount} sections";
        foreach (var line in index.Describe())
            yield return line;
    }

    private static string? BankPath(GameArchives er, string name) =>
        new[] { $"/{name}.bnk", $"/enus/{name}.bnk" }.FirstOrDefault(er.Contains);

    private static IEnumerable<string> ErCutscene(GameArchives er, string name)
    {
        var path = CutscenePort.ErPath(name);
        if (!er.Contains(path))
        {
            yield return $"no {path}";
            yield break;
        }
        var mqb = Mqb(BND4.Read(er.Read(path)));
        var sounds = new SortedSet<(int Type, int Id)>();
        foreach (var line in SoundEvents(mqb, sounds))
            yield return line;

        var bankPath = BankPath(er, name);
        WwiseBank? bank = null;
        if (bankPath is null)
            yield return "bank: none named like the cutscene";
        else
        {
            var bytes = er.Read(bankPath);
            bank = WwiseBank.Read(bytes);
            var types = bank.Objects.GroupBy(o => o.Type).OrderBy(g => g.Key).Select(g => $"type {g.Key} x{g.Count()}");
            yield return $"bank {bankPath}: {bytes.Length / 1024} KB, version {bank.Version}, bank ID {bank.BankId} " +
                         $"(FNV-1 of \"{name}\" = {FxSoundExplore.Fnv1(name)}), {bank.MediaCount} embedded media, objects: {string.Join(", ", types)}";
            var events = bank.Objects.Where(o => o.Type == WwiseBank.EventType).ToList();
            yield return $"bank events ({events.Count}): {string.Join(" ", events.Take(40).Select(e => e.Id))}";
            // Which names the events have: every sound of the timeline under every letter, then the cutscene's own
            // number ranges (p + cutscene ID * 10 + n, and * 100 + n, and the cutscene name itself).
            var names = new Dictionary<uint, string>();
            foreach (var (type, id) in sounds)
                foreach (var letter in Letters)
                    foreach (var verb in new[] { "Play_", "Stop_" })
                        names.TryAdd(FxSoundExplore.Fnv1($"{verb}{letter}{id:D9}"), $"{verb}{letter}{id:D9} (seType {type})");
            int cut = CutscenePort.IdOf(name) ?? 0;
            foreach (var letter in Letters)
                foreach (long baseId in new long[] { cut * 10L, cut * 100L, cut })
                    for (long n = 0; n < 10_000; n++)
                    {
                        long id = baseId + n;
                        if (id > 999_999_999) break;
                        names.TryAdd(FxSoundExplore.Fnv1($"Play_{letter}{id:D9}"), $"Play_{letter}{id:D9}");
                    }
            foreach (var extra in new[] { $"Play_{name}", name, $"Play_cs_{name}", $"Play_{cut}", $"Play_p{cut}" })
                names.TryAdd(FxSoundExplore.Fnv1(extra), extra);
            var named = events.Select(e => names.TryGetValue(e.Id, out var n) ? n : null).OfType<string>().ToList();
            yield return $"bank events with a known name: {named.Count} of {events.Count}: {string.Join(", ", named.Take(60))}";
            foreach (var e in events.Take(6))
            {
                var plays = bank.Plays(e.Id);
                yield return $"  event {e.Id}: {e.Data.Length} bytes {Convert.ToHexString(e.Data.Take(24).ToArray())}; plays {plays.Count}: " +
                             string.Join(", ", plays.Select(p => $"action {p.Action.Id} -> type {p.Sound.Type} {p.Sound.Id} ({p.Sound.Data.Length} bytes {Convert.ToHexString(p.Sound.Data.Take(20).ToArray())})"));
            }
        }

        // Where the timeline's sounds are, if not in the cutscene's bank: the main banks.
        foreach (var main in new[] { "/enus/cs_main.bnk", "/cs_main.bnk", "/cs_smain.bnk" }.Where(er.Contains))
        {
            var mainBank = WwiseBank.Read(er.Read(main));
            var found = sounds.SelectMany(s => Letters.Select(l => (s, l))).Where(x => mainBank.Has(FxSoundExplore.Fnv1($"Play_{x.l}{x.s.Id:D9}")))
                .Select(x => $"{x.l}{x.s.Id:D9} (seType {x.s.Type})").ToList();
            yield return $"in {main}: {found.Count}: {string.Join(", ", found.Take(30))}";
        }
        if (er.Contains(SoundIndex.ArchivePath))
        {
            var index = SoundIndex.Read(er.Read(SoundIndex.ArchivePath));
            uint id = FxSoundExplore.Fnv1(name);
            yield return $"index lists bank {name} ({id}): {index.HasBank(id)}{(bank is not null && bank.BankId != id ? $"; bank ID {bank.BankId}: {index.HasBank(bank.BankId)}" : "")}";
            foreach (var line in index.Describe(bank?.BankId ?? id))
                yield return line;
        }
    }

    private static MQB Mqb(BND4 bnd) =>
        MQB.Read(bnd.Files.First(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase)).Bytes);

    // Timeline events with sound parameters, and the resources they belong to.
    private static IEnumerable<string> SoundEvents(MQB mqb, SortedSet<(int Type, int Id)> sounds)
    {
        static bool IsSound(MQB.Parameter p) =>
            p.Name.Contains("se", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("sound", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("bgm", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("voice", StringComparison.OrdinalIgnoreCase);
        var kinds = new SortedDictionary<int, int>();
        int shown = 0, total = 0;
        foreach (var (res, i) in mqb.Resources.Select((x, i) => (x, i)))
            if (res.Unk48 is 135 or 138 or 137 or 139 or 140 || res.Parameters.Any(IsSound))
                yield return $"res {i} \"{res.Name}\" kind {res.Unk48} | {string.Join("; ", res.Parameters.Select(p => $"{p.Name}:{p.Type}={CutsceneSurvey.Value(p)}"))}";
        foreach (var cut in mqb.Cuts)
            foreach (var tl in cut.Timelines)
                foreach (var ev in tl.Events)
                {
                    if (!ev.Parameters.Any(IsSound))
                        continue;
                    total++;
                    int kind = ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count ? mqb.Resources[ev.ResourceIndex].Unk48 : -1;
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                    int? type = null, id = null;
                    foreach (var p in ev.Parameters)
                    {
                        if (p.Name.Equals("seType", StringComparison.OrdinalIgnoreCase)) type = Int(p);
                        if (p.Name.Equals("seId", StringComparison.OrdinalIgnoreCase)) id = Int(p);
                    }
                    if (type is not null && id is not null && id > 0)
                        sounds.Add((type.Value, id.Value));
                    if (shown++ < 40)
                        yield return $"{cut.Name} event res {ev.ResourceIndex} kind {kind} start {ev.StartFrame} dur {ev.Duration} | " +
                                     string.Join("; ", ev.Parameters.Select(p => $"{p.Name}:{p.Type}={CutsceneSurvey.Value(p)}"));
                }
        yield return $"{total} sound events by resource kind: {string.Join(", ", kinds.Select(k => $"{k.Key}:{k.Value}"))}";
        yield return $"distinct sounds (seType, seId): {string.Join(" ", sounds.Select(s => $"{s.Type}/{s.Id}"))}";
    }

    private static int? Int(MQB.Parameter p) => p.Value switch
    {
        int i => i,
        uint u => (int)u,
        short h => h,
        ushort h => h,
        byte b => b,
        sbyte b => b,
        long l => (int)l,
        _ => null,
    };

    private static IEnumerable<string> Ds3SoundFiles(PorterContext ctx, GameArchives ds3)
    {
        var loose = Path.Combine(ctx.GameDir(PorterGame.DarkSouls3), "sound");
        if (Directory.Exists(loose))
            yield return $"loose: {string.Join(" ", Directory.EnumerateFiles(loose).Select(Path.GetFileName).Where(n => n!.Contains("sm") || n.Contains("vm")).Order())}";
        foreach (var path in ds3.Entries.Select(e => e.Path).OfType<string>()
                     .Where(p => p.StartsWith("/sound/fdp_sm", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/sound/fdp_vm45", StringComparison.OrdinalIgnoreCase)
                                 || p.StartsWith("/sound/fdp_smain", StringComparison.OrdinalIgnoreCase))
                     .Where(p => p.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var bytes = ds3.Read(path);
            bool plain = Fsb5.Is(bytes);
            if (!plain && Fsb5.Decrypt(bytes) is { } dec)
                bytes = dec;
            if (!Fsb5.Is(bytes))
            {
                yield return $"{path}: {bytes.Length / 1024} KB, not readable";
                continue;
            }
            var all = Fsb5.Read(bytes);
            double seconds = all.Samples.Sum(s => s.Rate > 0 ? (double)s.Frames / s.Rate : 0);
            yield return $"{path}: {bytes.Length / 1024} KB{(plain ? "" : " (encrypted)")}, {all.Samples.Count} samples, {seconds:0} s; " +
                         string.Join(" ", all.Samples.Take(30).Select(s => $"{s.Name}({s.Channels}ch {s.Rate} {(s.Rate > 0 ? (double)s.Frames / s.Rate : 0):0.0}s c{s.Codec})"));
        }
    }

    private static IEnumerable<string> Ds3EventFiles(PorterContext ctx, GameArchives ds3)
    {
        foreach (var name in new[] { "fdp_sm45", "fdp_vm45", "fdp_sm40", "fdp_smain", "fdp_smain_dlc1", "fdp_xm45" })
        {
            byte[]? bytes = null;
            var loose = Path.Combine(ctx.GameDir(PorterGame.DarkSouls3), "sound", name + ".fev");
            if (File.Exists(loose)) bytes = File.ReadAllBytes(loose);
            else if (ds3.Contains($"/sound/{name}.fev")) bytes = ds3.Read($"/sound/{name}.fev");
            if (bytes is null)
            {
                yield return $"{name}.fev: not found";
                continue;
            }
            var fev = Fev.Read(bytes);
            yield return $"{name}.fev: {fev.Events.Count} events, {fev.Definitions} definitions, {fev.Skipped} skipped; " +
                         string.Join(" ", fev.Events.Values.Take(40).Select(e =>
                             $"{e.Name}[{e.Layers.Count}L {string.Join("+", e.Layers.Select(l => l.Waves.Count))}w{(e.OneShot ? "" : " loop")} {Fev.Decibels(e.Volume):0.#}dB]"));
        }
    }

    // How many DS3 events each sound letter has, and the cutscene-type (p) ones and the Painted World's (45) by name.
    private static IEnumerable<string> ByLetter(Dictionary<string, (Fev.Event Event, string Fsb)> events)
    {
        foreach (var g in events.GroupBy(e => e.Key[0]).OrderBy(g => g.Key))
            yield return $"{g.Key}: {g.Count()} events in {string.Join(" ", g.Select(e => e.Value.Fsb).Distinct().Order().Take(12))}; e.g. " +
                         string.Join(" ", g.Select(e => e.Key).Order().Take(8));
        yield return "p events: " + string.Join(" ", events.Keys.Where(k => k[0] == 'p').Order().Take(80));
        yield return "events with 45 or 40 after the letter (first 60): " +
                     string.Join(" ", events.Keys.Where(k => k.Length >= 3 && (k[1..3] == "45" || k[1..3] == "40")).Order().Take(60).Select(k => $"{k}@{events[k].Fsb}"));
    }

    private static IEnumerable<string> Ds3Cutscene(PorterContext ctx, GameArchives ds3, string name, Dictionary<string, (Fev.Event Event, string Fsb)> events)
    {
        var path = CutscenePort.Ds3Path(name);
        if (!ds3.Contains(path))
        {
            yield return $"no {path}";
            yield break;
        }
        var mqb = Mqb(BND4.Read(ds3.Read(path)));
        var sounds = new SortedSet<(int Type, int Id)>();
        foreach (var line in SoundEvents(mqb, sounds))
            yield return line;
        foreach (var (type, id) in sounds)
        {
            var hits = Letters.Select(l => $"{l}{id:D9}").Where(events.ContainsKey).ToList();
            yield return $"sound {type}/{id}: DS3 events " + (hits.Count == 0 ? "none" : string.Join(", ", hits.Select(h =>
            {
                var (e, fsb) = events[h];
                var waves = e.Layers.SelectMany(l => l.Waves).Select(Fev.SampleName).Distinct().ToList();
                return $"{h} in {fsb} ({e.Layers.Count} layers, waves {string.Join("/", waves.Take(4))}{(waves.Count > 4 ? "..." : "")}, " +
                       $"{Fev.Decibels(e.Volume):0.#} dB, {(e.OneShot ? "one-shot" : "loops")}, fade-out {e.FadeOutMs} ms)";
            })));
        }
    }
}
