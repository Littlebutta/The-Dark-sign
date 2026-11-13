using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using HKLib.hk2018;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// "enemy-diag bones &lt;DS3 chr&gt; [template chr] [built chr]" (RULE 4): how a DS3 character's model hangs on its
/// skeleton, for parts that sit at the wrong angle (the Lothric Knight's shield held sideways, 2026-11-13). Written to
/// Work\reports\enemy-diag\bones-cXXXX.txt:
/// <list type="bullet">
/// <item>every mesh of the DS3 model: skinned or rigid (a rigid mesh's vertices lie in the space of one node, named
/// by each vertex's NormalW), its material, the nodes it hangs on;</item>
/// <item>every node a mesh uses: its rest place in the model (FLVER) against the same bone's rest in DS3's animation
/// skeleton, and its parent in both;</item>
/// <item>for those bones, how far DS3's animations turn them from rest (a part DS3 animates on its own);</item>
/// <item>the template's skeleton and model (rigid meshes, the same bones' rest and parent);</item>
/// <item>the built character in the mod folder: its skeleton's rest against its model's nodes, and its rigid meshes.</item>
/// </list>
/// "enemy-diag bones all" lists, for every DS3 character, its rigid meshes and the nodes whose model rest differs
/// from the animation skeleton's (bones-all.tsv).
/// </summary>
public static class EnemyBonesDiag
{
    private static readonly Regex ItemBone = new("weapon|shield|sword|wep|buki|tate|axe|spear|lance|staff|bow", RegexOptions.IgnoreCase);

    public static int Run(PorterContext ctx, List<string> args)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        if (args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            return All(ctx, ds3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        int Chr(string s) => int.Parse(s.TrimStart('c', 'C'));
        int x = Chr(args[0]);
        int template = args.Count > 1 ? Chr(args[1]) : -1;
        int built = args.Count > 2 ? Chr(args[2]) : -1;
        var o = new StringBuilder();
        try { Report(ctx, ds3, er, x, template, built, o); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { o.AppendLine($"FAILED: {ChrPort.Describe(ex)}"); }
        var path = ctx.WorkPath("reports", "enemy-diag", $"bones-c{x:D4}.txt");
        File.WriteAllText(path, o.ToString());
        Console.Write(o.ToString());
        Console.WriteLine($"wrote {path}");
        return 0;
    }

    private static FLVER2? Ds3Flver(GameArchives ds3, int x)
    {
        var path = $"/chr/c{x:D4}.chrbnd.dcx";
        if (!ds3.Contains(path))
            return null;
        var file = BND4.Read(ds3.Read(path)).Files.FirstOrDefault(f => FLVER2.Is(f.Bytes));
        return file is null ? null : FLVER2.Read(file.Bytes);
    }

    private static (AnimPort.Ds3Skeleton Skeleton, List<BinderFile> Files)? Ds3Anims(GameArchives ds3, int x)
    {
        var files = ChrPort.Ds3AnimationSources(x).SelectMany(chr => ds3.Entries
                .Where(e => e.Path is { } p && Regex.IsMatch(ItemText.Leaf(p), $@"^c{chr:D4}(_.*)?\.anibnd\.dcx$", RegexOptions.IgnoreCase))
                .OrderBy(e => e.Path, StringComparer.Ordinal)
                .SelectMany(e => BND4.Read(ds3.Read(e.Path!)).Files))
            .ToList();
        var sk = files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
        return sk is null ? null : (AnimPort.ReadDs3Skeleton(sk.Bytes), files);
    }

    /// <summary>Each node's rest world rotation and position in the model.</summary>
    internal static (Quaternion R, Vector3 T)[] NodeWorld(FLVER2 flver) =>
        Enumerable.Range(0, flver.Nodes.Count).Select(i =>
        {
            Matrix4x4.Decompose(WeaponNodes.World(flver.Nodes, i), out _, out var r, out var t);
            return (Quaternion.Normalize(r), t);
        }).ToArray();

    /// <summary>Each skeleton bone's rest world rotation and position.</summary>
    internal static (Quaternion R, Vector3 T)[] SkeletonWorld(IReadOnlyList<short> parents, IReadOnlyList<(Vector3 T, Quaternion R)> local)
    {
        var w = new (Quaternion R, Vector3 T)[local.Count];
        for (int i = 0; i < local.Count; i++)
        {
            int p = i < parents.Count ? parents[i] : -1;
            var r = Quaternion.Normalize(local[i].R);
            w[i] = p < 0 || p >= i ? (r, local[i].T) : (Quaternion.Normalize(w[p].R * r), w[p].T + Vector3.Transform(local[i].T, w[p].R));
        }
        return w;
    }

    private static string F(Vector3 v) => FormattableString.Invariant($"({v.X:0.###},{v.Y:0.###},{v.Z:0.###})");
    private static string Deg(float d) => FormattableString.Invariant($"{d:0.#}°");

    /// <summary>The nodes a mesh hangs on, by vertex count: NormalW for a rigid mesh, the heaviest weight for a skinned one.</summary>
    internal static List<(int Node, int Count)> MeshNodes(FLVER2 flver, FLVER2.Mesh mesh)
    {
        var count = new Dictionary<int, int>();
        foreach (var v in mesh.Vertices)
        {
            int node;
            if (!mesh.UseBoneWeights)
                node = mesh.BoneIndices.Count > 0 ? (v.NormalW >= 0 && v.NormalW < mesh.BoneIndices.Count ? mesh.BoneIndices[v.NormalW] : -1) : v.NormalW;
            else
            {
                int best = 0;
                for (int k = 1; k < 4; k++)
                    if (v.BoneWeights[k] > v.BoneWeights[best])
                        best = k;
                int local = v.BoneIndices[best];
                node = mesh.BoneIndices.Count > 0 ? (local >= 0 && local < mesh.BoneIndices.Count ? mesh.BoneIndices[local] : -1) : local;
            }
            count[node] = count.GetValueOrDefault(node) + 1;
        }
        return count.OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList();
    }

    private static string NodeName(FLVER2 flver, int i) => i >= 0 && i < flver.Nodes.Count ? flver.Nodes[i].Name : $"#{i}";

    private static void Meshes(FLVER2 flver, StringBuilder o, string label)
    {
        int rigid = flver.Meshes.Count(m => !m.UseBoneWeights);
        o.AppendLine($"{label}: version 0x{flver.Header.Version:X}, {flver.Nodes.Count} nodes, {flver.Meshes.Count} meshes ({rigid} rigid), {flver.Dummies.Count} dummies");
        for (int i = 0; i < flver.Meshes.Count; i++)
        {
            var m = flver.Meshes[i];
            var mat = m.MaterialIndex >= 0 && m.MaterialIndex < flver.Materials.Count ? flver.Materials[m.MaterialIndex] : null;
            var nodes = MeshNodes(flver, m);
            o.AppendLine($"  mesh {i}: {(m.UseBoneWeights ? "skinned" : "RIGID")} node {NodeName(flver, m.NodeIndex)}, palette {m.BoneIndices.Count}, {m.Vertices.Count} verts, material \"{mat?.Name}\" {ItemText.Leaf(mat?.MTD ?? "")}; on {string.Join(", ", nodes.Take(4).Select(n => $"{NodeName(flver, n.Node)} x{n.Count}"))}{(nodes.Count > 4 ? $" (+{nodes.Count - 4})" : "")}");
        }
    }

    private static void Report(PorterContext ctx, GameArchives ds3, GameArchives er, int x, int template, int built, StringBuilder o)
    {
        o.AppendLine($"== DS3 c{x:D4}");
        var flver = Ds3Flver(ds3, x) ?? throw new FileNotFoundException($"DS3 has no c{x:D4} model");
        Meshes(flver, o, "DS3 model");
        var anims = Ds3Anims(ds3, x);
        if (anims is null)
        {
            o.AppendLine("DS3 has no skeleton for it");
            return;
        }
        var sk = anims.Value.Skeleton;
        var skWorld = SkeletonWorld(sk.Parents, sk.ReferencePose.Select(t => (t.Translation, t.Rotation)).ToList());
        var skIndex = sk.Names.Select((n, i) => (n, i)).GroupBy(p => p.n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var nodeWorld = NodeWorld(flver);

        // The nodes meshes hang on (and item-looking bones), with their model rest against the skeleton's.
        var used = flver.Meshes.SelectMany(m => MeshNodes(flver, m).Select(n => n.Node)).Where(n => n >= 0 && n < flver.Nodes.Count).ToHashSet();
        var watch = used.Concat(Enumerable.Range(0, flver.Nodes.Count).Where(i => ItemBone.IsMatch(flver.Nodes[i].Name ?? ""))).Distinct().OrderBy(i => i).ToList();
        o.AppendLine($"DS3 skeleton: {sk.Names.Count} bones. Nodes the meshes use (and weapon/shield-named ones), model rest vs skeleton rest:");
        foreach (int i in watch)
        {
            var n = flver.Nodes[i];
            string parent = n.ParentIndex >= 0 ? NodeName(flver, n.ParentIndex) : "-";
            if (!skIndex.TryGetValue(n.Name ?? "", out var b))
            {
                o.AppendLine($"  {n.Name} (model parent {parent}): NOT IN THE SKELETON; model local R{F(n.Rotation)} T{F(n.Translation)}");
                continue;
            }
            string skParent = sk.Parents.Count > b && sk.Parents[b] >= 0 ? sk.Names[sk.Parents[b]] : "-";
            float angle = AnimPort.Degrees(nodeWorld[i].R, skWorld[b].R);
            float dist = Vector3.Distance(nodeWorld[i].T, skWorld[b].T);
            o.AppendLine($"  {n.Name}: parent {parent}{(skParent.Equals(parent, StringComparison.OrdinalIgnoreCase) ? "" : $" (skeleton: {skParent})")}; model vs skeleton rest {Deg(angle)}, {dist:0.###} m{(angle > 2 || dist > 0.01 ? "  << DIFFERS" : "")}; model local R{F(n.Rotation)}, skeleton local {Deg(AnimPort.Degrees(sk.ReferencePose[b].Rotation))}");
        }

        // How DS3's animations turn those bones away from rest.
        var watchBones = watch.Select(i => flver.Nodes[i].Name ?? "").Where(skIndex.ContainsKey).ToList();
        var animated = watchBones.ToDictionary(n => n, _ => (Clips: 0, Max: 0f, Example: ""), StringComparer.OrdinalIgnoreCase);
        int clips = 0;
        foreach (var file in anims.Value.Files.Where(f => Regex.IsMatch(ItemText.Leaf(f.Name), @"^a\d{3}_\d{6}\.hkx$", RegexOptions.IgnoreCase)))
            try
            {
                var hkx = HKX.Read(file.Bytes, HKX.HKXVariation.HKXDS3, false);
                var a = hkx.DataSection.Objects.OfType<HKX.HKASplineCompressedAnimation>().FirstOrDefault();
                if (a is null)
                    continue;
                var binding = hkx.DataSection.Objects.OfType<HKX.HKAAnimationBinding>().FirstOrDefault();
                if (binding?.BlendHint.ToString() is { } hint && hint != "NORMAL")
                    continue;
                clips++;
                int tracks = a.TransformTrackCount;
                var trackBone = binding is not null && binding.TransformTrackToBoneIndices.Size > 0
                    ? Enumerable.Range(0, tracks).Select(t => (int)binding.TransformTrackToBoneIndices[t].data).ToArray()
                    : Enumerable.Range(0, tracks).ToArray();
                var samples = SplineCompressedAnimation.ReadSplCmpAnimBytesAndSampleToUncomp(a.GetData(), tracks, a.BlockCount, a.FrameCount, a.FramesPerBlock);
                foreach (var bone in watchBones)
                {
                    int b = skIndex[bone];
                    int t = Array.IndexOf(trackBone, b);
                    if (t < 0)
                        continue;
                    float max = 0;
                    for (int f = 0; f < a.FrameCount; f++)
                        max = Math.Max(max, AnimPort.Degrees(samples[f * tracks + t].Rotation, sk.ReferencePose[b].Rotation));
                    var cur = animated[bone];
                    animated[bone] = (cur.Clips + 1, Math.Max(cur.Max, max), max > cur.Max ? ItemText.Leaf(file.Name) : cur.Example);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        o.AppendLine($"DS3 animations ({clips} whole-pose clips): how far they turn those bones from rest:");
        foreach (var (bone, (c, max, example)) in animated)
            o.AppendLine($"  {bone}: tracked in {c} clips, up to {Deg(max)} from rest{(example.Length > 0 ? $" ({example})" : "")}");

        if (template > 0)
            Template(er, template, flver, sk, skWorld, watch, o);
        if (built > 0)
            Built(ctx, built, flver, watch, o);
    }

    private static void Template(GameArchives er, int t, FLVER2 ds3Flver, AnimPort.Ds3Skeleton ds3, (Quaternion R, Vector3 T)[] ds3World, List<int> watch, StringBuilder o)
    {
        o.AppendLine($"== Elden Ring template c{t:D4}");
        var chrbnd = $"/chr/c{t:D4}.chrbnd.dcx";
        if (er.Contains(chrbnd))
        {
            var bnd = BND4.Read(er.Read(chrbnd));
            o.AppendLine($"chrbnd files: {string.Join(", ", bnd.Files.Select(f => $"{ItemText.Leaf(f.Name)} {f.Bytes.Length}"))}");
            if (bnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes)) is { } ff)
                Meshes(FLVER2.Read(ff.Bytes), o, "template model");
        }
        var anibnd = $"/chr/c{t:D4}.anibnd.dcx";
        if (!er.Contains(anibnd))
        {
            o.AppendLine("no anibnd");
            return;
        }
        var abnd = BND4.Read(er.Read(anibnd));
        var skFile = abnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
        if (skFile is null)
            return;
        AnimPort.ErSkeleton s;
        lock (AnimPort.HavokLock)
            s = AnimPort.ReadErSkeleton(skFile.Bytes, abnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes);
        var w = SkeletonWorld(s.Parents, s.ReferencePose.Select(p => (new Vector3(p.m_translation.X, p.m_translation.Y, p.m_translation.Z), p.m_rotation)).ToList());
        var idx = s.Names.Select((n, i) => (n, i)).GroupBy(p => p.n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var dIdx = ds3.Names.Select((n, i) => (n, i)).GroupBy(p => p.n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        o.AppendLine($"template skeleton: {s.Names.Count} bones; item-looking: {string.Join(", ", s.Names.Where(n => ItemBone.IsMatch(n)))}");
        foreach (int i in watch)
        {
            var name = ds3Flver.Nodes[i].Name ?? "";
            if (!idx.TryGetValue(name, out var b))
            {
                o.AppendLine($"  {name}: not in the template (added from DS3)");
                continue;
            }
            string p = s.Parents[b] >= 0 ? s.Names[s.Parents[b]] : "-";
            string dp = dIdx.TryGetValue(name, out var d) && ds3.Parents[d] >= 0 ? ds3.Names[ds3.Parents[d]] : "-";
            o.AppendLine($"  {name}: template parent {p}{(p.Equals(dp, StringComparison.OrdinalIgnoreCase) ? "" : $" (DS3: {dp})")}; template vs DS3 rest: local {Deg(AnimPort.Degrees(s.ReferencePose[b].m_rotation, d >= 0 ? ds3.ReferencePose[d].Rotation : Quaternion.Identity))}, world {Deg(AnimPort.Degrees(w[b].R, ds3World[d].R))}");
        }
    }

    private static void Built(PorterContext ctx, int n, FLVER2 ds3Flver, List<int> watch, StringBuilder o)
    {
        o.AppendLine($"== built c{n:D4} (mod folder)");
        var chrbnd = ctx.ModPath("chr", $"c{n:D4}.chrbnd.dcx");
        var anibnd = ctx.ModPath("chr", $"c{n:D4}.anibnd.dcx");
        if (!File.Exists(chrbnd) || !File.Exists(anibnd))
        {
            o.AppendLine($"not built ({chrbnd})");
            return;
        }
        var cbnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(chrbnd)));
        o.AppendLine($"chrbnd files: {string.Join(", ", cbnd.Files.Select(f => $"{ItemText.Leaf(f.Name)} {f.Bytes.Length}"))}");
        var flver = FLVER2.Read(cbnd.Files.First(f => FLVER2.Is(f.Bytes)).Bytes);
        Meshes(flver, o, "built model");
        var abnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(anibnd)));
        var skFile = abnd.Files.First(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
        AnimPort.ErSkeleton s;
        lock (AnimPort.HavokLock)
            s = AnimPort.ReadErSkeleton(skFile.Bytes, abnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes);
        var w = SkeletonWorld(s.Parents, s.ReferencePose.Select(p => (new Vector3(p.m_translation.X, p.m_translation.Y, p.m_translation.Z), p.m_rotation)).ToList());
        var idx = s.Names.Select((nm, i) => (nm, i)).GroupBy(p => p.nm, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var nodeWorld = NodeWorld(flver);
        int differs = 0, missing = 0;
        var lines = new List<string>();
        for (int i = 0; i < flver.Nodes.Count; i++)
        {
            var name = flver.Nodes[i].Name ?? "";
            if (!idx.TryGetValue(name, out var b))
            {
                missing++;
                lines.Add($"  {name}: node flags {flver.Nodes[i].Flags}, NOT IN THE BUILT SKELETON");
                continue;
            }
            float angle = AnimPort.Degrees(nodeWorld[i].R, w[b].R);
            float dist = Vector3.Distance(nodeWorld[i].T, w[b].T);
            bool bad = angle > 2 || dist > 0.01;
            if (bad)
                differs++;
            if (bad || ItemBone.IsMatch(name) || watch.Any(j => (ds3Flver.Nodes[j].Name ?? "").Equals(name, StringComparison.OrdinalIgnoreCase)))
                lines.Add($"  {name}: flags {flver.Nodes[i].Flags}, skeleton #{b} parent {(s.Parents[b] >= 0 ? s.Names[s.Parents[b]] : "-")}; model vs skeleton rest {Deg(angle)}, {dist:0.###} m{(bad ? "  << DIFFERS" : "")}");
        }
        o.AppendLine($"built skeleton {s.Names.Count} bones; {flver.Nodes.Count} model nodes, {missing} not in the skeleton, {differs} rest differently:");
        foreach (var l in lines)
            o.AppendLine(l);
        foreach (var f in cbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
            try
            {
                lock (AnimPort.HavokLock)
                {
                    var root = (hkRootLevelContainer)AnimPort.ReadEr(f.Bytes, cbnd.Files.FirstOrDefault(c => c.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes);
                    foreach (var c in root.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>())
                        foreach (var sk in c.m_skeletons)
                            o.AppendLine($"  chrbnd {ItemText.Leaf(f.Name)} skeleton \"{sk!.m_name}\": {sk.m_bones.Count} bones; item-looking: {string.Join(", ", sk.m_bones.Select(b => b.m_name ?? "").Where(nm => ItemBone.IsMatch(nm)))}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                o.AppendLine($"  chrbnd {ItemText.Leaf(f.Name)}: unreadable ({ex.GetType().Name})");
            }
    }

    /// <summary>Every DS3 character: rigid meshes and the mesh nodes whose model rest differs from the skeleton's.</summary>
    private static int All(PorterContext ctx, GameArchives ds3)
    {
        var lines = new List<string> { "chr\tmeshes\trigid meshes\trigid nodes\tmesh nodes\tnot in skeleton\trest differs (>2° or >1 cm)" };
        var chrs = ds3.Entries.Select(e => e.Path is { } p ? Regex.Match(ItemText.Leaf(p), @"^c(\d{4})\.chrbnd\.dcx$", RegexOptions.IgnoreCase) : Match.Empty)
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(c => c).ToList();
        foreach (int x in chrs)
            try
            {
                var flver = Ds3Flver(ds3, x);
                if (flver is null || flver.Meshes.Count == 0)
                    continue;
                var anims = Ds3Anims(ds3, x);
                var rigid = flver.Meshes.Where(m => !m.UseBoneWeights).ToList();
                var rigidNodes = rigid.SelectMany(m => MeshNodes(flver, m).Select(n => NodeName(flver, n.Node))).Distinct().ToList();
                var used = flver.Meshes.SelectMany(m => MeshNodes(flver, m).Select(n => n.Node)).Where(n => n >= 0 && n < flver.Nodes.Count).Distinct().OrderBy(n => n).ToList();
                var missing = new List<string>();
                var differs = new List<string>();
                if (anims is { } a)
                {
                    var sk = a.Skeleton;
                    var skWorld = SkeletonWorld(sk.Parents, sk.ReferencePose.Select(t => (t.Translation, t.Rotation)).ToList());
                    var skIndex = sk.Names.Select((nm, i) => (nm, i)).GroupBy(p => p.nm, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
                    var nodeWorld = NodeWorld(flver);
                    foreach (int i in used)
                    {
                        var name = flver.Nodes[i].Name ?? "";
                        if (!skIndex.TryGetValue(name, out var b))
                        {
                            missing.Add(name);
                            continue;
                        }
                        float angle = AnimPort.Degrees(nodeWorld[i].R, skWorld[b].R);
                        float dist = Vector3.Distance(nodeWorld[i].T, skWorld[b].T);
                        if (angle > 2 || dist > 0.01)
                            differs.Add(FormattableString.Invariant($"{name} {angle:0}° {dist:0.00}m"));
                    }
                }
                lines.Add(string.Join('\t', $"c{x:D4}", flver.Meshes.Count, rigid.Count, string.Join(" ", rigidNodes), used.Count,
                    anims is null ? "no skeleton" : string.Join(" ", missing), string.Join(" ", differs)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"c{x:D4}\tFAILED {ChrPort.Describe(ex)}");
            }
        var path = ctx.WorkPath("reports", "enemy-diag", "bones-all.tsv");
        File.WriteAllLines(path, lines);
        foreach (var l in lines)
            Console.WriteLine(l);
        Console.WriteLine($"wrote {path}");
        return 0;
    }
}
