using System.Numerics;
using System.Text.RegularExpressions;
using DarkSign.Porter.Maps;
using HKLib.hk2018;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// Diagnostics for the enemy port (RULE 4), written to Work\reports\enemy-diag:
/// "enemy-diag survey" lists every Elden Ring and DS3 character's animation skeleton (bones with parents, height)
/// and animation IDs, the facts the template choice and the animation fallbacks are made from;
/// "enemy-diag unpack &lt;file&gt;..." writes built or game files decompressed to Work\extract\MOD, to look at elsewhere.
/// </summary>
public static partial class EnemyDiag
{
    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimLeaf();

    [GeneratedRegex(@"^/chr/c(\d{4})\.anibnd\.dcx$", RegexOptions.IgnoreCase)]
    private static partial Regex MainAnibnd();

    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && args[0].Equals("unpack", StringComparison.OrdinalIgnoreCase))
            return Unpack(ctx, args.Skip(1).ToList());
        if (args.Count == 1 && args[0].Equals("survey", StringComparison.OrdinalIgnoreCase))
            return Survey(ctx);
        Console.Error.WriteLine("enemy-diag survey | enemy-diag unpack <file>...");
        return 1;
    }

    private static int Unpack(PorterContext ctx, List<string> paths)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing); // makes Oodle available
        int done = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"not found: {path}");
                continue;
            }
            var raw = File.ReadAllBytes(path);
            var bytes = DCX.Is(raw) ? DCX.Decompress(raw) : raw;
            var name = Path.GetFileName(path);
            if (name.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            var outPath = ctx.WorkPath("extract", "MOD", Path.GetFileName(Path.GetDirectoryName(path)!) ?? "x", name);
            File.WriteAllBytes(outPath, bytes);
            Console.WriteLine(outPath);
            done++;
        }
        return done > 0 ? 0 : 1;
    }

    /// <summary>World positions of a skeleton's bones in its rest pose.</summary>
    public static Vector3[] WorldPositions(IReadOnlyList<short> parents, IReadOnlyList<(Vector3 T, Quaternion R)> local)
    {
        var pos = new Vector3[local.Count];
        var rot = new Quaternion[local.Count];
        for (int i = 0; i < local.Count; i++)
        {
            int p = i < parents.Count ? parents[i] : -1;
            if (p < 0 || p >= i)
            {
                pos[i] = local[i].T;
                rot[i] = local[i].R;
            }
            else
            {
                pos[i] = pos[p] + Vector3.Transform(local[i].T, rot[p]);
                rot[i] = rot[p] * local[i].R;
            }
        }
        return pos;
    }

    private static string Bones(IReadOnlyList<string> names, IReadOnlyList<short> parents) =>
        string.Join("|", names.Select((n, i) => $"{n}>{(i < parents.Count ? parents[i] : -1)}"));

    private static string Size(Vector3[] world)
    {
        if (world.Length == 0)
            return "0\t0\t0";
        var min = world.Aggregate(Vector3.Min);
        var max = world.Aggregate(Vector3.Max);
        var s = max - min;
        return FormattableString.Invariant($"{s.X:0.00}\t{s.Y:0.00}\t{s.Z:0.00}");
    }

    private static string Ids(IEnumerable<string> leaves) =>
        string.Join(",", leaves.Select(l => AnimLeaf().Match(l)).Where(m => m.Success)
            .Select(m => (S: int.Parse(m.Groups[1].Value), I: int.Parse(m.Groups[2].Value))).Distinct().OrderBy(k => k)
            .Select(k => $"{k.S}:{k.I}"));

    private static int Survey(PorterContext ctx)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var notes = new List<string>();
        var erNpc = ctx.LoadParams(PorterGame.EldenRing).Get("NpcParam");
        var catalog = EnemyPort.BuildCatalog(er, erNpc, notes);
        var byModel = catalog.GroupBy(c => ChrPort.ChrId(c.Model)).ToDictionary(g => g.Key, g => g.ToList());

        var lines = new List<string> { "chr\tin catalog\tkind\tboss\tnpc\tthink\tnames\tbehbnd\ttae\tbones\tsize x\tsize y\tsize z\tanimations\tbone list\tanimation ids" };
        foreach (var entry in er.Entries.Where(e => e.Path is { } p && MainAnibnd().IsMatch(p)).OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            int chr = int.Parse(MainAnibnd().Match(entry.Path!).Groups[1].Value);
            try
            {
                var bnd = BND4.Read(er.Read(entry.Path!));
                var skeletonFile = bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
                bool tae = bnd.Files.Any(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase));
                var leaves = new List<string>();
                foreach (var a in er.Entries.Where(e => e.Path is { } p && Regex.IsMatch(ItemText.Leaf(p), $@"^c{chr:D4}(_div\d+)?\.anibnd\.dcx$", RegexOptions.IgnoreCase)))
                    leaves.AddRange((a.Path == entry.Path ? bnd : BND4.Read(er.Read(a.Path!))).Files.Select(f => ItemText.Leaf(f.Name)));
                string bones = "", size = "0\t0\t0";
                int count = 0;
                if (skeletonFile is not null)
                    lock (AnimPort.HavokLock)
                    {
                        var compendium = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
                        var s = AnimPort.ReadErSkeleton(skeletonFile.Bytes, compendium);
                        count = s.Names.Count;
                        bones = Bones(s.Names, s.Parents);
                        size = Size(WorldPositions(s.Parents, s.ReferencePose.Select(t =>
                            (new Vector3(t.m_translation.X, t.m_translation.Y, t.m_translation.Z), t.m_rotation)).ToList()));
                    }
                var cat = byModel.GetValueOrDefault(chr) ?? [];
                var first = cat.FirstOrDefault();
                lines.Add(string.Join('\t', $"c{chr:D4}", cat.Count, first?.Kind ?? "", cat.Any(c => c.Boss) ? 1 : 0, first?.NpcParam ?? 0, first?.ThinkParam ?? 0,
                    string.Join(" / ", cat.Select(c => c.Name.Replace('\t', ' ')).Distinct().Take(4)),
                    er.Contains($"/chr/c{chr:D4}.behbnd.dcx") ? 1 : 0, tae ? 1 : 0, count, size,
                    leaves.Count(l => AnimLeaf().IsMatch(l)), bones, Ids(leaves)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"c{chr:D4}\tFAIL {ex.GetType().Name}: {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}");
            }
        }
        var erPath = ctx.WorkPath("reports", "enemy-diag", "er-chr.tsv");
        File.WriteAllLines(erPath, lines);
        Console.WriteLine($"{erPath}: {lines.Count - 1} Elden Ring characters");

        var ds3Npc = ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcParam");
        lines = ["chr\tname\tkind\tboss\tmodel nodes\tbones\tsize x\tsize y\tsize z\tanimations\tbone list\tanimation ids"];
        foreach (var c in ChrPort.Ds3Characters(ds3, ds3Npc))
        {
            try
            {
                var files = ChrPort.Ds3AnimationSources(c.Chr).SelectMany(chr => ds3.Entries
                        .Where(e => e.Path is { } p && Regex.IsMatch(ItemText.Leaf(p), $@"^c{chr:D4}(_.*)?\.anibnd\.dcx$", RegexOptions.IgnoreCase))
                        .OrderBy(e => e.Path, StringComparer.Ordinal)
                        .SelectMany(e => BND4.Read(ds3.Read(e.Path!)).Files))
                    .ToList();
                var skeletonFile = files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
                string bones = "", size = "0\t0\t0";
                int count = 0;
                if (skeletonFile is not null)
                {
                    var s = AnimPort.ReadDs3Skeleton(skeletonFile.Bytes);
                    count = s.Names.Count;
                    bones = Bones(s.Names, s.Parents);
                    size = Size(WorldPositions(s.Parents, s.ReferencePose.Select(t => (t.Translation, t.Rotation)).ToList()));
                }
                var leaves = files.Select(f => ItemText.Leaf(f.Name)).ToList();
                lines.Add(string.Join('\t', $"c{c.Chr:D4}", c.Name.Replace('\t', ' '), EnemyPort.KindOf(c.Name), c.Boss ? 1 : 0,
                    ChrPort.FlverBones(ds3, c.Chr)?.Count ?? 0, count, size, leaves.Count(l => AnimLeaf().IsMatch(l)), bones, Ids(leaves)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"c{c.Chr:D4}\tFAIL {ex.GetType().Name}: {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}");
            }
        }
        var ds3Path = ctx.WorkPath("reports", "enemy-diag", "ds3-chr.tsv");
        File.WriteAllLines(ds3Path, lines);
        Console.WriteLine($"{ds3Path}: {lines.Count - 1} DS3 characters");
        foreach (var n in notes)
            Console.WriteLine($"note: {n}");
        return 0;
    }
}
