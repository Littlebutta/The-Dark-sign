using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's object sounds (a lift's motor, a door's creak, a lever's clunk) on the ported moving objects. What each game
/// has (lift-diag on Devin's PC, 2026-11-20):
/// <list type="bullet">
/// <item>DS3 plays them from the object's TAE: types 128 (centre), 129 (held on a dummy poly, for the event's length)
/// and 131 (once, on a dummy poly), sound type 3 = "o": o311500001 is the Undead Settlement lift's motor, o009009010 a
/// shared stop clunk. The samples are in DS3's FMOD banks, named by DS3's event files (<see cref="Fev"/>).</item>
/// <item>Elden Ring plays an asset's sounds from its TAE with Wwise_PlaySound_BySlot (129), sound type 14 = "g":
/// the lift AEG219_020 plays g920001100-105 (the motor held with PlaybackBehaviorType 1, the stops once), the door
/// AEG219_000 g900010000-002. The game's sound index lists those events under banks named aeg920_001 and aeg900_010
/// (bank ID = FNV-1 of the name), so a g sound GGGNNNkkk lives in bank aegGGG_NNN, which the asset loads through its
/// AssetEnvironmentGeometryParam row (soundBankId GGGNNN; Paramdex: "the bank of the given ID, -1 none").</item>
/// </list>
/// So every ported moving object whose DS3 animations play sounds gets a bank of its own, aeg{sound group}_{NNN},
/// named after its asset number with the group moved to one no Elden Ring bank uses (<see cref="SoundGroup"/>: Elden
/// Ring keeps its shared asset sounds in groups like 920, the same numbers the ported assets take); its row names the
/// bank; and its TAE plays them as Elden Ring's own assets do: 129, type 14, the DS3 dummy poly, held sounds
/// (DS3's 129 that last a second or more) with PlaybackBehaviorType 1 so they end with the event, the rest once.
/// The banks are built like the enemy and cutscene banks (<see cref="ObjectSoundBanks"/>). Off with
/// DARKSIGN_OBJECT_SOUNDS=0 (launcher fallback map-doors C): the objects move silently, as up to 1.0.50x.
/// </summary>
public static class ObjectSounds
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DARKSIGN_OBJECT_SOUNDS") != "0";

    /// <summary>DS3's object sound type (o) and Elden Ring's asset sound type (g), in the TAE's first field.</summary>
    public const int Ds3Kind = 3, AssetKind = 14;
    public const char Ds3Letter = 'o', ErLetter = 'g';
    public const int SoundEvent = 129;
    /// <summary>One bank holds sounds GGGNNN000-999.</summary>
    public const int MaxPerAsset = 1000;
    /// <summary>The first sound group tried for the ported objects' banks, counting down.</summary>
    public const int FirstGroupWanted = 990;
    /// <summary>A DS3 129 at least this long holds its sound (a motor); shorter ones are clunks that play out.</summary>
    public const float HeldSeconds = 1f;

    public static string BankName(int bank) => $"aeg{bank / 1000:D3}_{bank % 1000:D3}";
    public static int SoundId(int bank, int n) => bank * 1000 + n;
    public static string ErName(int id) => $"{ErLetter}{id:D9}";
    public static string Ds3Name(int id) => $"{Ds3Letter}{id:D9}";

    /// <summary>
    /// The first of <paramref name="groups"/> sound groups, from <see cref="FirstGroupWanted"/> down, whose banks
    /// (aegGGG_000-999) the game's sound index lists none of; <see cref="FirstGroupWanted"/> without an index.
    /// </summary>
    public static int SoundGroup(Func<uint, bool>? listed, int groups)
    {
        groups = Math.Max(1, groups);
        if (listed is null)
            return FirstGroupWanted;
        bool Free(int g)
        {
            for (int n = 0; n < 1000; n++)
                if (listed(FxSoundExplore.Fnv1(BankName(g * 1000 + n))))
                    return false;
            return true;
        }
        for (int first = FirstGroupWanted - groups + 1; first >= 100; first--)
            if (Enumerable.Range(first, groups).All(Free))
                return first;
        return FirstGroupWanted;
    }

    /// <summary>The bank of an asset (its index among the ported objects) in the sound groups from <paramref name="firstGroup"/>.</summary>
    public static int BankOf(int firstGroup, int assetIndex) => (firstGroup + assetIndex / 1000) * 1000 + assetIndex % 1000;

    private static bool IsSound(TAE.Event e) => e.Type is 128 or 129 or 131;

    /// <summary>The DS3 object sounds (type 3) a TAE plays, in ID order.</summary>
    public static SortedSet<int> Ds3Sounds(TAE tae)
    {
        var ids = new SortedSet<int>();
        foreach (var e in tae.Animations.SelectMany(a => a.Events).Where(IsSound))
            if (Param(e, tae.BigEndian, 0) == Ds3Kind && Param(e, tae.BigEndian, 1) is var id and > 0)
                ids.Add(id);
        return ids;
    }

    private static int Param(TAE.Event e, bool bigEndian, int index)
    {
        var b = e.GetParameterBytes(bigEndian);
        if (b.Length < 4 * index + 4)
            return -1;
        return bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(4 * index)) : BitConverter.ToInt32(b, 4 * index);
    }

    /// <summary>The layout of Elden Ring's asset sound event: the template's first 129 (the door's g900010000).</summary>
    public static (byte[] Bytes, int Unk04)? Layout(TAE template)
    {
        var e = template.Animations.SelectMany(a => a.Events)
            .Where(x => x.Type == SoundEvent && x.GetParameterBytes(template.BigEndian).Length >= 24)
            .OrderBy(x => Param(x, template.BigEndian, 0) == AssetKind ? 0 : 1).FirstOrDefault();
        return e is null ? null : (e.GetParameterBytes(template.BigEndian), e.Unk04);
    }

    /// <summary>
    /// One Elden Ring sound event: the layout with type 14, the new ID, the dummy poly, no slot, no state, and
    /// PlaybackBehaviorType 1 (ends with the event) when <paramref name="held"/>, else 0 (plays out).
    /// </summary>
    public static byte[] EventBytes(byte[] layout, int id, int dummy, bool held)
    {
        var bytes = (byte[])layout.Clone();
        BitConverter.GetBytes(AssetKind).CopyTo(bytes, 0);
        BitConverter.GetBytes(id).CopyTo(bytes, 4);
        BitConverter.GetBytes(dummy).CopyTo(bytes, 8);
        BitConverter.GetBytes(-1).CopyTo(bytes, 12);         // SlotID: none
        if (bytes.Length >= 20)
        {
            BitConverter.GetBytes((short)0).CopyTo(bytes, 16); // StateInfo
            bytes[18] = held ? (byte)1 : (byte)0;             // PlaybackBehaviorType
        }
        return bytes;
    }

    /// <summary>
    /// DS3's object sound events of one animation as Elden Ring's, for the sounds <paramref name="ids"/> names
    /// (DS3 ID -> new g ID). Returns the events and how many DS3 sound events were left out (no new ID).
    /// </summary>
    public static (List<TAE.Event> Events, int Dropped) Events(TAE.Animation ds3, bool bigEndian, (byte[] Bytes, int Unk04) layout,
        IReadOnlyDictionary<int, int> ids)
    {
        var events = new List<TAE.Event>();
        int dropped = 0;
        TAE.EventGroup? group = null;
        foreach (var e in ds3.Events.Where(IsSound))
        {
            if (Param(e, bigEndian, 0) != Ds3Kind)
                continue;
            if (!ids.TryGetValue(Param(e, bigEndian, 1), out var id))
            {
                dropped++;
                continue;
            }
            // 128 plays at the centre (no dummy poly field); 129 and 131 name one.
            int dummy = e.Type == 128 ? -1 : Param(e, bigEndian, 2);
            if (dummy <= 0)
                dummy = -1;
            bool held = e.Type == 129 && e.EndTime - e.StartTime >= HeldSeconds;
            group ??= new TAE.EventGroup(SoundEvent);
            events.Add(new TAE.Event(e.StartTime, e.EndTime, SoundEvent, layout.Unk04, EventBytes(layout.Bytes, id, dummy, held), false) { Group = group });
        }
        return (events, dropped);
    }
}

/// <summary>
/// Builds the ported objects' sound banks (<see cref="ObjectSounds"/>): each a copy of an Elden Ring asset bank
/// (aeg920_001, the lift's) with only the new sounds, decoded from DS3's samples as the weapon, enemy and cutscene
/// sound ports do (<see cref="SoundPort"/>: DS3's event file names the samples, level, pitch and loops), one
/// Play_g/Stop_g pair per sound; then lists them in the game's sound index beside the template bank, looping sounds
/// flagged as Elden Ring flags its own.
/// </summary>
public sealed class ObjectSoundBanks
{
    public const string ManifestFile = "darksign_object_sounds.txt";
    private static readonly string[] Suffixes = ["", "b", "c", "d", "e", "f", "g", "h"];

    /// <summary>Elden Ring asset banks tried as the template, with the bank number their g sounds carry.</summary>
    public static readonly (string Path, int Bank)[] Templates =
    [
        ("/aeg920_001.bnk", 920001), ("/enus/aeg920_001.bnk", 920001), ("/aeg900_010.bnk", 900010), ("/enus/aeg900_010.bnk", 900010),
    ];

    private readonly PorterContext _ctx;
    private readonly GameArchives _ds3;
    private readonly Dictionary<string, (Fev.Event Event, string Fsb)> _events;
    private readonly byte[] _template;
    private readonly string _templatePath;
    private readonly uint _templatePlay, _templateStop, _templateBankId;
    private readonly Dictionary<string, Dictionary<string, Fsb5.Sample>?> _fsbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(uint BankId, List<uint> Events, HashSet<uint> Looping)> _written = [];
    private readonly List<string> _files = [];

    private ObjectSoundBanks(PorterContext ctx, GameArchives ds3, Dictionary<string, (Fev.Event, string)> events, byte[] template,
        string templatePath, uint play, uint stop, uint bankId)
    {
        (_ctx, _ds3, _events, _template, _templatePath, _templatePlay, _templateStop, _templateBankId) =
            (ctx, ds3, events, template, templatePath, play, stop, bankId);
    }

    public string TemplateName => Path.GetFileNameWithoutExtension(_templatePath);

    /// <summary>Whether a template bank's folder is the localized one (sd\enus\): the new banks go beside it.</summary>
    private bool Localized => _templatePath.StartsWith("/enus/", StringComparison.OrdinalIgnoreCase);

    public string BankPath(string name) => Localized ? _ctx.ModPath("sd", "enus", name + ".bnk") : _ctx.ModPath("sd", name + ".bnk");

    /// <summary>Deletes the banks an earlier run wrote (listed in <see cref="ManifestFile"/>).</summary>
    public static void Clean(PorterContext ctx)
    {
        var manifest = ctx.ModPath(ManifestFile);
        if (!File.Exists(manifest))
            return;
        foreach (var line in File.ReadAllLines(manifest))
        {
            if (line.Contains("..") || !line.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) || !line.StartsWith("sd", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var path = ctx.ModPath(line.Split('\\', '/'));
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        File.Delete(manifest);
    }

    /// <summary>
    /// The first template bank that reads, has a g sound whose chain this port can copy, and builds a bank of our own;
    /// null (with the reason in notes) when none does.
    /// </summary>
    public static ObjectSoundBanks? Create(PorterContext ctx, GameArchives ds3, GameArchives er, List<string> notes)
    {
        var tried = new List<string>();
        foreach (var (path, number) in Templates)
        {
            if (!er.Contains(path))
                continue;
            try
            {
                var bytes = er.Read(path);
                var bank = WwiseBank.Read(bytes);
                for (int n = 0; n < ObjectSounds.MaxPerAsset; n++)
                {
                    var name = ObjectSounds.ErName(ObjectSounds.SoundId(number, n));
                    uint play = FxSoundExplore.Fnv1("Play_" + name);
                    if (bank.Chain(play) is not { } chain || WwiseNode.Read(chain.Sound) is null || !CutsceneSound.Builds(bytes, play))
                        continue;
                    return new ObjectSoundBanks(ctx, ds3, SoundPort.Ds3Events(ctx, ds3), bytes, path, play, FxSoundExplore.Fnv1("Stop_" + name), bank.BankId);
                }
                tried.Add($"{path}: no g sound to copy");
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                tried.Add($"{path}: {e.Message}");
            }
        }
        notes.Add("object sounds: no Elden Ring asset bank to copy" + (tried.Count > 0 ? $" ({string.Join("; ", tried)})" : $" (none of {string.Join(" ", Templates.Select(t => t.Path))} in the game's archives)") +
                  "; objects move silently");
        return null;
    }

    public bool Known(int ds3Id) => _events.ContainsKey(ObjectSounds.Ds3Name(ds3Id));

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

    public sealed record BankResult(string Log, HashSet<int> Ported, HashSet<int> Looping, string? File);

    /// <summary>
    /// Builds one object's bank with its sounds (DS3 ID, new g ID) and writes it. A sound DS3 has no sample for, or
    /// whose samples don't decode, is left out; no bank is written when none is left.
    /// </summary>
    /// <param name="folder">Where the bank goes instead of the mod's sd folder (object-sound-test: Work\scratch); it is then not listed in the index.</param>
    public BankResult Build(int bankNumber, IReadOnlyList<(int Ds3Id, int ErId)> sounds, string? folder = null)
    {
        var name = ObjectSounds.BankName(bankNumber);
        var path = folder is null ? BankPath(name) : Path.Combine(folder, name + ".bnk");
        var ported = new HashSet<int>();
        var looping = new HashSet<int>();
        if (sounds.Count == 0)
            return new BankResult("no sounds", ported, looping, null);
        var bank = WwiseBank.Read(_template);
        var template = bank.Chain(_templatePlay)!.Value;
        var stopTemplate = bank.StopTemplate(_templateStop);
        HashSet<uint> taken = [];
        var events = new List<uint>();
        var loopEvents = new HashSet<uint>();
        var missing = new List<string>();
        foreach (var (ds3Id, erId) in sounds)
        {
            var ds3Name = ObjectSounds.Ds3Name(ds3Id);
            var erName = ObjectSounds.ErName(erId);
            if (!_events.TryGetValue(ds3Name, out var found))
            {
                missing.Add(ds3Name);
                continue;
            }
            var (ev, fsb) = found;
            // The event file's own bank first (fdp_m31.fev -> fdp_m31.fsb), then the banks DS3 splits a map's sounds
            // into (fdp_sm31) and the main ones.
            var banks = new[] { fsb, fsb.StartsWith("fdp_m", StringComparison.OrdinalIgnoreCase) ? "fdp_s" + fsb[4..] : null, "fdp_main", "fdp_smain" }
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Fsb5.Sample? Find(string sample) => banks.Select(b => Samples(b)?.GetValueOrDefault(sample)).FirstOrDefault(x => x is not null);
            var layers = new List<WwiseBank.Layer>();
            bool loops = false;
            for (int l = 0; l < ev.Layers.Count; l++)
            {
                var layer = ev.Layers[l];
                var wanted = layer.Waves.Select(Fev.SampleName).Distinct().ToList();
                if (wanted.Count == 0 && l == 0)
                    wanted = [.. Suffixes.Select(s => ds3Name + s)];
                var tuning = SoundPort.TuningOf(ev, layer);
                var media = new List<(uint, uint, byte[])>();
                foreach (var s in wanted.Select(Find).OfType<Fsb5.Sample>())
                {
                    try
                    {
                        var pcm = Fsb5.DecodePcm(s);
                        if (pcm.Length == 0 || CutsceneSound.ClippedShare(pcm) > CutsceneSound.MaxClippedShare)
                            continue;
                        var points = tuning?.Loop == true ? SoundPort.LoopPoints(s) : null;
                        var wem = WwiseBank.PcmWem(pcm, s.Channels, s.Rate, points);
                        media.Add((SoundPort.UnusedId(bank, $"darksign_{name}_sound_{erName}_{l}_{media.Count}", taken),
                            SoundPort.UnusedId(bank, $"darksign_{name}_media_{s.Name}{(points is null ? "" : "_loop")}", taken), wem));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // One undecodable sample must not cost the others.
                    }
                }
                if (media.Count == 0)
                    continue;
                loops |= tuning?.Loop == true;
                layers.Add(new WwiseBank.Layer(SoundPort.UnusedId(bank, $"darksign_{name}_action_{erName}_{l}", taken),
                    SoundPort.UnusedId(bank, $"darksign_{name}_container_{erName}_{l}", taken), media)
                {
                    Tuning = tuning,
                    StopActionId = SoundPort.UnusedId(bank, $"darksign_{name}_stop_{erName}_{l}", taken),
                });
            }
            if (layers.Count == 0)
            {
                missing.Add(ds3Name);
                continue;
            }
            uint play = FxSoundExplore.Fnv1("Play_" + erName), stop = FxSoundExplore.Fnv1("Stop_" + erName);
            if (bank.Has(play) || bank.Has(stop))
            {
                missing.Add(ds3Name);
                continue;
            }
            bank.AddSound(template, play, layers, streamed: false, new WwiseBank.StopEvent(stop, ev.FadeOutMs, stopTemplate));
            events.Add(play);
            events.Add(stop);
            if (loops)
            {
                loopEvents.Add(play);
                looping.Add(ds3Id);
            }
            ported.Add(ds3Id);
        }
        if (ported.Count == 0)
            return new BankResult($"no sound ported ({string.Join(" ", missing.Take(6))})", ported, looping, null);
        uint bankId = FxSoundExplore.Fnv1(name);
        var written = bank.WriteOwnBank(bankId);
        if (written is null)
            return new BankResult($"{TemplateName}'s sounds don't hang under an actor-mixer this port can copy", [], [], null);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, written);
        if (folder is null)
        lock (_written)
        {
            _written.Add((bankId, events, loopEvents));
            _files.Add(Path.GetRelativePath(Path.GetDirectoryName(_ctx.ModPath("x"))!, path));
        }
        return new BankResult($"{name}.bnk {written.Length / 1024} KB, {ported.Count} sounds ({looping.Count} looping)" +
                              (missing.Count > 0 ? $", left out {string.Join(" ", missing.Take(6))}" : ""), ported, looping, path);
    }

    /// <summary>
    /// Lists every bank written in the game's sound index (the mod's, which port-weapons wrote, else the game's), in
    /// the section that holds the template bank, and records the files for the next run's clean-up.
    /// </summary>
    public string Finish(GameArchives er)
    {
        File.WriteAllLines(_ctx.ModPath(ManifestFile), _files.Order(StringComparer.OrdinalIgnoreCase));
        if (_written.Count == 0)
            return "index: nothing to list";
        if (Environment.GetEnvironmentVariable("DARKSIGN_SOUND_INDEX") == "0")
            return "index: not edited (DARKSIGN_SOUND_INDEX=0)";
        var path = SoundPort.IndexPath(_ctx);
        byte[]? bytes = File.Exists(path) ? File.ReadAllBytes(path) : er.Contains(SoundIndex.ArchivePath) ? er.Read(SoundIndex.ArchivePath) : null;
        if (bytes is null)
            return "index: the game has none";
        var index = SoundIndex.Read(bytes);
        int added = _written.Sum(w => index.Register(w.BankId, w.Events, sectionOf: _templateBankId, looping: w.Looping));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, index.Write());
        return $"index: {added} events of {_written.Count} object banks listed beside {TemplateName}";
    }

    /// <summary>
    /// object-sound-test o311500 ...: builds each DS3 object's sound bank into Work\scratch\object-sounds (not the mod)
    /// as port-maps would, reads it back and reports every sound: its DS3 event, the samples found, and whether the
    /// new Play event chains to a sound. Exit code 1 when an object that plays sounds got no bank.
    /// </summary>
    public static int Test(PorterContext ctx, IReadOnlyList<string> objects)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var notes = new List<string>();
        var banks = Create(ctx, ds3, er, notes);
        foreach (var n in notes)
            Console.WriteLine(n);
        if (banks is null)
            return 1;
        Console.WriteLine($"template: {banks._templatePath} (bank {banks._templateBankId}), {banks._events.Count} DS3 events known");
        var folder = ctx.WorkPath("scratch", "object-sounds");
        int failures = 0, index = 0;
        foreach (var o in objects)
        {
            var model = o.StartsWith('o') ? o : "o" + o;
            var path = $"/obj/{model}.objbnd.dcx";
            if (!ds3.Contains(path))
            {
                Console.WriteLine($"{model}: not in DS3's archives");
                failures++;
                continue;
            }
            var tae = ObjectAnims.Ds3Tae(BND4.Read(ds3.Read(path)));
            var played = tae is null ? new SortedSet<int>() : ObjectSounds.Ds3Sounds(tae);
            Console.WriteLine($"{model}: {played.Count} DS3 object sounds: {string.Join(" ", played.Select(ObjectSounds.Ds3Name))}");
            if (played.Count == 0)
                continue;
            int bank = ObjectSounds.BankOf(ObjectSounds.FirstGroupWanted, 900 + index++);
            var list = played.Select((id, n) => (id, ObjectSounds.SoundId(bank, n))).ToList();
            foreach (var (id, _) in list)
                Console.WriteLine($"  {ObjectSounds.Ds3Name(id)}: " + (banks._events.TryGetValue(ObjectSounds.Ds3Name(id), out var ev)
                    ? $"event in {ev.Fsb}.fev, {ev.Event.Layers.Count} layers, waves {string.Join(" ", ev.Event.Layers.SelectMany(l => l.Waves).Select(Fev.SampleName).Distinct().Take(6))}, loops {ev.Event.Layers.Any(l => l.Loops)}"
                    : "no DS3 event of that name"));
            var result = banks.Build(bank, list, folder);
            Console.WriteLine($"  bank: {result.Log}");
            if (result.File is null)
            {
                failures++;
                continue;
            }
            var back = WwiseBank.Read(File.ReadAllBytes(result.File));
            foreach (var (id, erId) in list.Where(l => result.Ported.Contains(l.id)))
            {
                var chain = back.Chain(FxSoundExplore.Fnv1("Play_" + ObjectSounds.ErName(erId)));
                bool stop = back.Has(FxSoundExplore.Fnv1("Stop_" + ObjectSounds.ErName(erId)));
                Console.WriteLine($"  Play_{ObjectSounds.ErName(erId)} <- {ObjectSounds.Ds3Name(id)}: {(chain is null ? "NO CHAIN" : "chains to a sound")}, stop event {(stop ? "yes" : "NO")}{(result.Looping.Contains(id) ? ", loops" : "")}");
                if (chain is null || !stop)
                    failures++;
            }
        }
        Console.WriteLine($"object-sound-test: {failures} problem(s); banks in {folder}");
        return failures == 0 ? 0 : 1;
    }
}
