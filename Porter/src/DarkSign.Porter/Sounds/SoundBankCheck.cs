using System.Text;

namespace DarkSign.Porter;

/// <summary>
/// `check-sound-bank`: read-only checks of Elden Ring's main sound bank, to see why a ported bank can hang loading.
/// Round-trips the original bank through <see cref="WwiseBank"/> (it must come back byte for byte), and reports the
/// sections, how the bank's Sounds store their media (embedded, prefetched or streamed, by codec), and where the
/// streamed media files are. Writes Work\reports\explore\sound-bank.txt.
/// </summary>
public static class SoundBankCheck
{
    private const string Bank = "/enus/cs_main.bnk";

    public static int Run(PorterContext ctx)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var report = new StringBuilder();
        var bytes = er.Read(Bank);
        report.AppendLine($"{Bank}: {bytes.Length} bytes");
        for (int at = 0; at + 8 <= bytes.Length;)
        {
            var tag = Encoding.ASCII.GetString(bytes, at, 4);
            int size = BitConverter.ToInt32(bytes, at + 4);
            report.AppendLine($"  section {tag} at {at} (body at {at + 8}, {(at + 8) % 16} mod 16), {size} bytes, starts {Convert.ToHexString(bytes, at + 8, Math.Min(32, size))}");
            at += 8 + size;
        }

        var bank = WwiseBank.Read(bytes);
        var written = bank.Write();
        int diff = Enumerable.Range(0, Math.Min(bytes.Length, written.Length)).FirstOrDefault(i => bytes[i] != written[i], -1);
        report.AppendLine(diff < 0 && bytes.Length == written.Length
            ? "round trip: identical"
            : $"round trip: DIFFERENT ({written.Length} bytes written), first difference at {diff}");
        report.AppendLine($"media: {bank.MediaCount} embedded, ids sorted in DIDX: {bank.MediaSorted}");

        // Sounds by (plugin, stream type), with examples of streamed source IDs.
        var sounds = bank.Objects.Where(o => o.Type == WwiseBank.SoundType && o.Data.Length >= 18).ToList();
        foreach (var g in sounds.GroupBy(s => (Plugin: BitConverter.ToUInt32(s.Data, 4), Stream: s.Data[8])).OrderByDescending(g => g.Count()))
            report.AppendLine($"  sounds plugin 0x{g.Key.Plugin:X8} stream {g.Key.Stream}: {g.Count()}, e.g. source {string.Join(" ", g.Take(3).Select(s => BitConverter.ToUInt32(s.Data, 9)))}" +
                              $", size {string.Join(" ", g.Take(3).Select(s => BitConverter.ToUInt32(s.Data, 13)))}, bits {string.Join(" ", g.Take(3).Select(s => s.Data[17].ToString("X2")))}");

        // Where streamed media lives: try the usual layouts for a few streamed sources.
        var streamed = sounds.Where(s => s.Data[8] != 0).Select(s => BitConverter.ToUInt32(s.Data, 9)).Distinct().Take(5).ToList();
        foreach (var id in streamed)
        {
            var s = id.ToString();
            var candidates = new[] { $"/enus/wem/{s[..2]}/{s}.wem", $"/wem/{s[..2]}/{s}.wem", $"/enus/wem/{s}.wem", $"/wem/{s}.wem", $"/enus/{s}.wem", $"/{s}.wem" };
            var found = candidates.Where(er.Contains).ToList();
            report.AppendLine($"  streamed {id}: {(found.Count == 0 ? "no file found" : string.Join(", ", found))}" +
                              (found.Count > 0 ? $", starts {Convert.ToHexString(er.Read(found[0]).AsSpan(0, 64))}" : ""));
        }

        // The template the port clones: the first Elden Ring player sound reachable from a Play_c event.
        foreach (var id in new[] { 0, 100, 101, 102, 200, 1000, 2000, 8000, 10000, 100000, 500000, 9000 })
        {
            if (bank.Chain(FxSoundExplore.Fnv1($"Play_c{id:D9}")) is not { } chain)
                continue;
            report.AppendLine($"  chain Play_c{id:D9}: event {Convert.ToHexString(chain.Event.Data)}, action {Convert.ToHexString(chain.Action.Data)}");
            report.AppendLine($"    sound {chain.Sound.Data.Length} bytes {Convert.ToHexString(chain.Sound.Data, 0, Math.Min(80, chain.Sound.Data.Length))}");
            break;
        }
        // An embedded media file, for its header layout.
        if (bank.FirstMedia() is { } media)
            report.AppendLine($"  embedded media {media.Id}: {media.Bytes.Length} bytes, starts {Convert.ToHexString(media.Bytes, 0, Math.Min(96, media.Bytes.Length))}");

        File.WriteAllText(ctx.WorkPath("reports", "explore", "sound-bank.txt"), report.ToString());
        Console.Write(report);
        return 0;
    }
}
