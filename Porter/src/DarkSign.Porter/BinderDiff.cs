using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// `binder-diff &lt;a&gt; &lt;b&gt;`: compares two BND4 files on disk (DCX or not): compression, header, which files differ, and
/// for differing TAEs, which animations' events differ. Read-only; for finding what changed between a mod build that
/// loads and one that doesn't.
/// </summary>
public static class BinderDiff
{
    public static int Run(string pathA, string pathB)
    {
        var (a, ca) = Load(pathA);
        var (b, cb) = Load(pathB);
        Console.WriteLine($"A {pathA}: {ca}, {a.Files.Count} files, format {a.Format}, version {a.Version}, unicode {a.Unicode}, extended {a.Extended}, unk04 {a.Unk04}, unk05 {a.Unk05}, ids ascending {Ascending(a)}");
        Console.WriteLine($"B {pathB}: {cb}, {b.Files.Count} files, format {b.Format}, version {b.Version}, unicode {b.Unicode}, extended {b.Extended}, unk04 {b.Unk04}, unk05 {b.Unk05}, ids ascending {Ascending(b)}");

        var byNameA = a.Files.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
        var byNameB = b.Files.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
        var onlyA = byNameA.Keys.Except(byNameB.Keys).ToList();
        var onlyB = byNameB.Keys.Except(byNameA.Keys).ToList();
        Console.WriteLine($"only in A: {onlyA.Count} {string.Join(" ", onlyA.Take(10).Select(ItemText.Leaf))}");
        Console.WriteLine($"only in B: {onlyB.Count} {string.Join(" ", onlyB.Take(10).Select(ItemText.Leaf))}");

        int idDiff = 0, flagDiff = 0, sizeDiff = 0, byteDiff = 0;
        var changed = new List<string>();
        foreach (var name in byNameA.Keys.Intersect(byNameB.Keys))
        {
            var (fa, fb) = (byNameA[name], byNameB[name]);
            if (fa.ID != fb.ID) { if (idDiff++ < 5) Console.WriteLine($"  id {ItemText.Leaf(name)}: {fa.ID} -> {fb.ID}"); }
            if (fa.Flags != fb.Flags) { if (flagDiff++ < 5) Console.WriteLine($"  flags {ItemText.Leaf(name)}: {fa.Flags} -> {fb.Flags}"); }
            if (fa.Bytes.Length != fb.Bytes.Length) sizeDiff++;
            if (!fa.Bytes.AsSpan().SequenceEqual(fb.Bytes)) { byteDiff++; changed.Add(name); }
        }
        Console.WriteLine($"in both: {byNameA.Keys.Intersect(byNameB.Keys).Count()}, different id {idDiff}, flags {flagDiff}, size {sizeDiff}, bytes {byteDiff}");
        var orderA = a.Files.Select(f => f.Name).Where(byNameB.ContainsKey).ToList();
        var orderB = b.Files.Select(f => f.Name).Where(byNameA.ContainsKey).ToList();
        Console.WriteLine($"same order for shared files: {orderA.SequenceEqual(orderB)}");

        foreach (var name in changed.Take(40))
        {
            var (fa, fb) = (byNameA[name], byNameB[name]);
            var line = $"  changed {ItemText.Leaf(name)} ({fa.Bytes.Length} -> {fb.Bytes.Length})";
            if (name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
                line += TaeDiff(fa.Bytes, fb.Bytes);
            Console.WriteLine(line);
        }
        if (changed.Count > 40)
            Console.WriteLine($"  ... {changed.Count - 40} more changed");
        return 0;
    }

    private static string TaeDiff(byte[] a, byte[] b)
    {
        try
        {
            TAE.ValidateEventBank = false;
            var ta = TAE.Read(a);
            var tb = TAE.Read(b);
            var animsA = ta.Animations.ToDictionary(x => x.ID);
            var animsB = tb.Animations.ToDictionary(x => x.ID);
            var diffs = new List<string>();
            foreach (var id in animsA.Keys.Union(animsB.Keys).Order())
            {
                if (!animsA.TryGetValue(id, out var x)) { diffs.Add($"+{id}"); continue; }
                if (!animsB.TryGetValue(id, out var y)) { diffs.Add($"-{id}"); continue; }
                string Sig(TAE.Animation an) => string.Join(";", an.Events.Select(e => $"{e.Type}@{e.StartTime:F3}-{e.EndTime:F3}:{Convert.ToHexString(e.GetParameterBytes(false))}:{an.EventGroups.IndexOf(e.Group)}"));
                if (Sig(x) != Sig(y))
                {
                    var typesA = x.Events.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
                    var typesB = y.Events.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
                    var typeDiff = typesA.Keys.Union(typesB.Keys).Where(t => typesA.GetValueOrDefault(t) != typesB.GetValueOrDefault(t))
                        .Select(t => $"{t}:{typesA.GetValueOrDefault(t)}->{typesB.GetValueOrDefault(t)}");
                    int nullA = x.Events.Count(e => e.Group is null), nullB = y.Events.Count(e => e.Group is null);
                    diffs.Add($"{id}[{x.Events.Count}->{y.Events.Count} ev, groups {x.EventGroups.Count}->{y.EventGroups.Count}, ungrouped {nullA}->{nullB}, {string.Join(",", typeDiff)}]");
                }
            }
            return $": {ta.Animations.Count}->{tb.Animations.Count} anims, format {ta.Format}/{tb.Format}, {diffs.Count} differ, e.g. {string.Join(" ", diffs.Take(4))}";
        }
        catch (Exception e)
        {
            return $": TAE read failed ({e.GetType().Name}: {e.Message})";
        }
    }

    private static (BND4 Bnd, string Compression) Load(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (!DCX.Is(raw))
            return (BND4.Read(raw), "no DCX");
        var bytes = DCX.Decompress(raw, out var compression);
        var level = compression is DCX.DcxKrakCompressionInfo k ? $" level {k.CompressionLevel}" : "";
        return (BND4.Read(bytes), $"{compression.Type}{level}");
    }

    private static bool Ascending(BND4 bnd) => bnd.Files.Zip(bnd.Files.Skip(1)).All(p => p.First.ID < p.Second.ID);
}
