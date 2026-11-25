using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// sound-limits [fxr ids...]: a read-only report on how many copies of a sound may play at once, in both games.
/// DS3 (FMOD Designer events): the header ints around the priority (+40) for every event, so the "max playbacks"
/// field (+44) can be told from its neighbours by its values; Elden Ring (Wwise): the advanced settings of every
/// node in its banks (instance limit and its bits). For the effects named (default: the DS3 weapons' resident
/// effects and Friede's ice blade) it lists the sounds they and their child effects play, held or not, with
/// their DS3 events. Report: Work\reports\explore\sound-limits.txt.
/// </summary>
public static class SoundLimits
{
    private static readonly int[] DefaultEffects = [303151, 440501, 440391, 440390, 440800, 440280];

    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var report = new StringBuilder();
        void Log(string line) { report.AppendLine(line); Console.WriteLine(line); }
        var fxrIds = args.Select(a => int.TryParse(a.TrimStart('f'), out var id) ? id : -1).Where(id => id > 0).ToList();
        if (fxrIds.Count == 0)
            fxrIds = [.. DefaultEffects];

        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var events = SoundPort.Ds3Events(ctx, ds3);
        Log($"# sound-limits ({DateTime.Now:yyyy-MM-dd HH:mm}): {events.Count} DS3 events");

        // DS3 header ints +40..+56 over every event: the commonest values of each.
        foreach (var at in new[] { 40, 44, 48, 52, 56 })
        {
            var values = events.Values.Where(e => e.Event.Header.Length >= at + 4).GroupBy(e => BitConverter.ToInt32(e.Event.Header, at))
                .OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key}:{g.Count()}");
            Log($"DS3 event header +{at}: {string.Join(" ", values)}");
        }
        var looping = events.Values.Where(e => !e.Event.OneShot || e.Event.Layers.Any(l => l.Loops)).ToList();
        Log($"DS3 looping events ({looping.Count}) by +44: " + string.Join(" ", looping.GroupBy(e => e.Event.MaxPlaybacks)
            .OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        Log($"DS3 one-shot events by +44: " + string.Join(" ", events.Values.Except(looping).GroupBy(e => e.Event.MaxPlaybacks)
            .OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));

        // The named effects: their sounds and those of their child effects.
        var index = FxrPort.Ds3Index(ds3);
        var js = new FxrPort.FxrScript();
        var seen = new HashSet<int>();
        var queue = new Queue<(int Id, int Depth)>(fxrIds.Select(id => (id, 0)));
        Log("\n# effects (id, depth): sounds held / played");
        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();
            if (!seen.Add(id) || depth > 4)
                continue;
            if (!index.Fxr.TryGetValue(id, out var file))
            {
                Log($"{new string(' ', depth * 2)}f{id}: no DS3 file");
                continue;
            }
            FxrPort.FxrScript.Info info;
            try { info = js.Inspect(file.File.Bytes); }
            catch (Exception e) { Log($"{new string(' ', depth * 2)}f{id}: unreadable ({e.Message.Split('\n')[0]})"); continue; }
            var sounds = info.Sounds.Where(s => s > 0).Distinct().ToList();
            Log($"{new string(' ', depth * 2)}f{id} ({ItemText.Leaf(file.Binder)}): held [{string.Join(" ", info.HeldSounds ?? [])}] " +
                $"unheld [{string.Join(" ", info.UnheldSounds ?? [])}] all [{string.Join(" ", sounds)}] children [{string.Join(" ", info.Sfx.Where(s => s > 0).Distinct())}]");
            foreach (var s in sounds)
                Log($"{new string(' ', depth * 2)}  {Describe($"s{s:D9}", events)}");
            foreach (var child in info.Sfx.Where(s => s > 0).Distinct())
                queue.Enqueue((child, depth + 1));
        }

        // Elden Ring: instance limits on the nodes of its banks.
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var tally = new Dictionary<(bool Limited, byte Bits), int>();
        var maxes = new Dictionary<int, int>();
        int nodes = 0, banks = 0;
        foreach (var path in er.Entries.Select(e => e.Path).Where(p => p?.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) == true).Cast<string>())
        {
            WwiseBank bank;
            try { bank = WwiseBank.Read(er.Read(path)); }
            catch (Exception) { continue; }
            banks++;
            foreach (var o in bank.Objects)
            {
                if (WwiseNode.Read(o) is not { AdvancedAt: >= 0 } node)
                    continue;
                nodes++;
                var (max, bits) = node.Limit(o.Data);
                tally[(max > 0, (byte)(bits & 0x1F))] = tally.GetValueOrDefault((max > 0, (byte)(bits & 0x1F))) + 1;
                if (max > 0)
                    maxes[max] = maxes.GetValueOrDefault(max) + 1;
            }
        }
        Log($"\n# Elden Ring: {nodes} nodes read in {banks} banks; advanced-settings bits (0 kill newest, 1 virtual, 2 ?global, 3 ignore parent, 4 vvoice) by whether a limit is set");
        foreach (var ((limited, bits), count) in tally.OrderBy(t => t.Key.Limited).ThenByDescending(t => t.Value))
            Log($"  {(limited ? "limit" : "none ")} bits 0x{bits:X2}: {count}");
        Log("  limits: " + string.Join(" ", maxes.OrderBy(m => m.Key).Select(m => $"{m.Key}:{m.Value}")));

        var path2 = ctx.WorkPath("reports", "explore", "sound-limits.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path2)!);
        File.WriteAllText(path2, report.ToString());
        Console.WriteLine($"report: {path2}");
        return 0;
    }

    private static string Describe(string name, Dictionary<string, (Fev.Event Event, string Fsb)> events)
    {
        if (!events.TryGetValue(name, out var found))
            return $"{name}: no DS3 event";
        var e = found.Event;
        var h = e.Header;
        string Int(int at) => h.Length >= at + 4 ? BitConverter.ToInt32(h, at).ToString() : "?";
        return $"{name} ({found.Fsb}): volume {Fev.Decibels(e.Volume):0.#} dB, layers {e.Layers.Count} loop modes [{string.Join(" ", e.Layers.Select(l => l.LoopMode))}], " +
               $"one-shot {e.OneShot}, fade-out {e.FadeOutMs} ms, +40 {Int(40)} +44 {Int(44)} +48 {Int(48)} +52 {Int(52)} +56 {Int(56)}, " +
               $"waves {string.Join(" ", e.Layers.SelectMany(l => l.Waves).Take(6))}";
    }
}
