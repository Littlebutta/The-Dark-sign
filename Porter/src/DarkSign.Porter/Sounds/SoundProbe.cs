namespace DarkSign.Porter;

/// <summary>
/// sound-probe: a one-launch test of why the ported DS3 sounds are silent, on the BUILT mod bank
/// (Mods\ER\sd\enus\cs_main.bnk; the original is kept in Work\backup and put back by "sound-probe undo").
/// It reports each ported event's media (embedded or not, PCM peak and loudness), then points every ported event
/// (Play_c9000xxxxx / Play_s9000xxxxx) at an Elden Ring swing sound that the game plays fine. In game: if Friede's
/// light attacks now whoosh, the bank and events work and the DS3 audio itself is at fault; if they stay silent,
/// the game never reaches our events (bank not loaded, or the TAE events don't resolve).
/// </summary>
public static class SoundProbe
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var bankPath = ctx.ModPath("sd", "enus", "cs_main.bnk");
        var backup = ctx.WorkPath("backup", "cs_main.bnk");
        if (args.Count == 1 && args[0].Equals("undo", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(backup)) { Console.WriteLine("no probe to undo"); return 0; }
            File.Copy(backup, bankPath, overwrite: true);
            File.Delete(backup);
            Console.WriteLine($"restored {bankPath}");
            return 0;
        }
        if (!File.Exists(bankPath)) { Console.Error.WriteLine($"no mod bank at {bankPath}"); return 1; }
        if (File.Exists(backup)) { Console.Error.WriteLine("a probe is already applied; run 'sound-probe undo' first"); return 1; }

        var bytes = File.ReadAllBytes(bankPath);
        var bank = WwiseBank.Read(bytes);
        // An Elden Ring swing sound the player hears on every scythe R1 (a050_030000: c105060900, c105060000).
        var target = new[] { 105060900, 105060000, 105000000, 100000000 }
            .Select(id => (Id: id, Chain: bank.Chain(FxSoundExplore.Fnv1($"Play_c{id:D9}")))).FirstOrDefault(t => t.Chain is not null);
        if (target.Chain is not { } vanilla) { Console.Error.WriteLine("no Elden Ring swing sound found"); return 1; }
        Console.WriteLine($"probe target: Play_c{target.Id:D9} -> sound {vanilla.Sound.Id}");

        int found = 0;
        foreach (var letter in new[] { 'c', 's' })
            for (int id = 0; id < 100_000; id++)
            {
                var name = $"Play_{letter}{SoundPort.IdBase + id:D9}";
                if (bank.Chain(FxSoundExplore.Fnv1(name)) is not { } chain)
                    continue;
                found++;
                var d = chain.Sound.Data;
                uint media = BitConverter.ToUInt32(d, 9);
                var wem = bank.Media(media);
                if (found <= 12)
                    Console.WriteLine($"  {name}: sound {chain.Sound.Id} plugin 0x{BitConverter.ToUInt32(d, 4):X8} stream {d[8]} media {media} " +
                                      (wem is null ? "NOT EMBEDDED" : Pcm(wem)));
                // Play the vanilla sound instead.
                var action = (byte[])chain.Action.Data.Clone();
                BitConverter.GetBytes(vanilla.Sound.Id).CopyTo(action, 6);
                chain.Action.Data = action;
            }
        Console.WriteLine($"{found} ported events now play Play_c{target.Id:D9}'s sound");
        if (found == 0)
            return 1;
        File.Copy(bankPath, backup);
        File.WriteAllBytes(bankPath, bank.Write());
        Console.WriteLine($"wrote {bankPath} (original in {backup}; undo with: sound-probe undo)");
        return 0;
    }

    // RIFF wem: the fmt chunk's channels and rate, and the 16-bit data's peak and RMS (0 = silent, 32767 = full scale).
    private static string Pcm(byte[] wem)
    {
        int at = 12, channels = 0, rate = 0;
        while (at + 8 <= wem.Length)
        {
            var tag = System.Text.Encoding.ASCII.GetString(wem, at, 4);
            int size = BitConverter.ToInt32(wem, at + 4);
            if (tag == "fmt ") { channels = BitConverter.ToUInt16(wem, at + 10); rate = BitConverter.ToInt32(wem, at + 12); }
            if (tag == "data")
            {
                int n = Math.Min(size, wem.Length - at - 8) / 2, peak = 0;
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    int v = BitConverter.ToInt16(wem, at + 8 + 2 * i);
                    peak = Math.Max(peak, Math.Abs(v));
                    sum += (double)v * v;
                }
                return $"{wem.Length} b, fmt 0x{BitConverter.ToUInt16(wem, 20):X4} {channels} ch {rate} Hz, {n / Math.Max(1, channels) / (double)Math.Max(1, rate):0.00} s, peak {peak}, rms {Math.Sqrt(sum / Math.Max(1, n)):0}";
            }
            at += 8 + size + (size & 1);
        }
        return $"{wem.Length} b, no data chunk";
    }
}
