using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// attack-events &lt;game&gt; [bullets] [section:animation ...]: read-only survey of the events that look a behaviour row up in the
/// player's TAE: attacks (type 1), bullets (type 2), common behaviours (type 5) and throws (type 304). A ported move's
/// hit or projectile only happens when the game finds a BehaviorParam_PC row for the event, and the row ID it builds
/// (prefix * 100,000,000 + variation * 1000 + judge) depends on fields of the event, so this lists every combination
/// of the non-judge fields with the range of judges seen under it and an example move. Animations named on the
/// command line (a650:40010, section number as the game writes it) are printed in full; "bullets" lists every bullet
/// event with its move, dummy poly and judge.
/// Writes Work\reports\explore\movesets\attack-events-&lt;game&gt;.txt.
/// </summary>
public static class AttackEventSurvey
{
    private static readonly int[] Types = [1, 2, 5, 304];

    public static int Run(PorterContext ctx, PorterGame game, IReadOnlyList<string> wanted)
    {
        TAE.ValidateEventBank = false;
        using var archives = ctx.OpenArchives(game);
        var full = wanted.Select(w => w.TrimStart('a', 'A').Split(':')).Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .Select(p => (Section: int.Parse(p[0]), Anim: int.Parse(p[1]))).ToHashSet();
        bool listBullets = wanted.Contains("bullets", StringComparer.OrdinalIgnoreCase);
        var bulletLines = new StringBuilder();
        var text = new StringBuilder();
        // type, parameter length, the ints around the judge -> count, judge range, example
        var shapes = new SortedDictionary<string, (int Count, int MinJudge, int MaxJudge, string Example)>(StringComparer.Ordinal);
        var dumps = new StringBuilder();
        int taes = 0;
        foreach (var entry in archives.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/chr/c0000", StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path))
        {
            BND4 bnd;
            try { bnd = BND4.Read(archives.Read(entry.Path!)); }
            catch (Exception e) { text.AppendLine($"# {entry.Path}: unreadable ({e.Message})"); continue; }
            foreach (var file in bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
            {
                TAE tae;
                try { tae = TAE.Read(file.Bytes); }
                catch { continue; }
                taes++;
                string leaf = Path.GetFileNameWithoutExtension(ItemText.Leaf(file.Name));
                int section = int.TryParse(leaf.TrimStart('a'), out var s) ? s : -1;
                foreach (var anim in tae.Animations)
                {
                    if (full.Contains((section, (int)anim.ID)))
                    {
                        dumps.AppendLine($"\n## {game.ShortName} {leaf}:{anim.ID} ({ItemText.Leaf(entry.Path!)}), {anim.Events.Count} events");
                        foreach (var ev in anim.Events.OrderBy(e => e.StartTime))
                            dumps.AppendLine($"  {ev.Type,4} {ev.StartTime,7:0.000}-{ev.EndTime,7:0.000}  {Convert.ToHexString(ev.GetParameterBytes(false))}");
                    }
                    foreach (var ev in anim.Events.Where(e => Types.Contains(e.Type)))
                    {
                        var bytes = ev.GetParameterBytes(false);
                        // The judge is the third int of an attack, the second of a bullet, the first of the others.
                        int judgeAt = ev.Type == 1 ? 8 : ev.Type == 2 ? 4 : 0;
                        if (bytes.Length < judgeAt + 4)
                            continue;
                        int judge = BitConverter.ToInt32(bytes, judgeAt);
                        if (listBullets && ev.Type == 2)
                            bulletLines.AppendLine($"  {leaf}:{anim.ID}\t{ev.StartTime:0.000}\tdummy {BitConverter.ToInt32(bytes, 0)}\tjudge {judge}\t{Convert.ToHexString(bytes)}");
                        var rest = new List<string>();
                        for (int at = 0; at + 4 <= bytes.Length; at += 4)
                            // A bullet's first int is its dummy poly, which varies per move like the judge does.
                            rest.Add(at == judgeAt ? "J" : ev.Type == 2 && at == 0 ? "D" : BitConverter.ToInt32(bytes, at).ToString());
                        string key = $"{ev.Type,4}\tlen {bytes.Length,3}\t{string.Join(' ', rest)}";
                        shapes[key] = shapes.TryGetValue(key, out var seen)
                            ? (seen.Count + 1, Math.Min(seen.MinJudge, judge), Math.Max(seen.MaxJudge, judge), seen.Example)
                            : (1, judge, judge, $"{leaf}:{anim.ID}");
                    }
                }
            }
        }
        text.AppendLine($"# {game.ShortName} player TAE: {taes} files. Events that look up a behaviour row, by every field but the judge (J) and a bullet's dummy poly (D)");
        text.AppendLine("type\tlength\tfields as ints\tcount\tjudges\texample");
        foreach (var (key, v) in shapes)
            text.AppendLine($"{key}\t{v.Count}\t{v.MinJudge}..{v.MaxJudge}\t{v.Example}");
        if (listBullets)
            text.AppendLine("\n# every bullet event").Append(bulletLines);
        text.Append(dumps);
        var path = ctx.WorkPath("reports", "explore", "movesets", $"attack-events-{game.ShortName}.txt");
        File.WriteAllText(path, text.ToString());
        Console.WriteLine($"attack events: {shapes.Count} field combinations over {shapes.Values.Sum(v => v.Count)} events in {taes} TAE files -> {path}");
        return 0;
    }
}
