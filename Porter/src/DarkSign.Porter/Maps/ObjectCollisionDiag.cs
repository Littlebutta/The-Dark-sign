using System.Numerics;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "object-diag collision &lt;objbnd&gt;...": what each collision body of a DS3 object holds, read straight from the
/// objbnd (decompressed or DFLT, so a cloud thread can run it on staged files): the body's name, place and shape,
/// and for a compressed mesh every section's vertex counts and whether its primitives decode. Written to find why
/// the platforms of DS3's lifts were "placeholders" (their primitives pointed past the section's vertices).
/// </summary>
public static class ObjectCollisionDiag
{
    /// <summary>
    /// "object-diag model &lt;binder&gt;...": a DS3 objbnd's or a ported asset binder's (decompressed: "enemy-diag unpack"
    /// first) models, how each mesh hangs on its nodes (NormalW nodes or bone weights), and the skeleton the animation
    /// binder moves. Written for the Undead Settlement tower lift whose floor rose while the platform stayed put.
    /// </summary>
    /// <summary>
    /// "object-diag anim-types &lt;objbnd or folder&gt;...": which Havok animation class each DS3 object animation is
    /// stored as (spline-compressed, interleaved, quantized ...), read from the class names in each file, and what
    /// SoulsAssetPipeline reads of it. Written for the lifts that stood still because their animations were not
    /// spline-compressed ("the DS3 file has no spline-compressed animation" in object-port.tsv).
    /// </summary>
    public static int AnimTypes(IReadOnlyList<string> inputs)
    {
        var files = inputs.SelectMany(i => Directory.Exists(i)
            ? Directory.EnumerateFiles(i, "*.objbnd*", SearchOption.TopDirectoryOnly)
            : [i]).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        return AnimTypes(files.Select(f => (Path.GetFileName(f), (Func<byte[]>)(() => File.ReadAllBytes(f)))).ToList());
    }

    /// <summary>"map-diag anim-types [oNNNNNN...]": the same, read from DS3's game archives (default: every objbnd).</summary>
    public static int AnimTypes(PorterContext ctx, IReadOnlyList<string> models)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var paths = models.Count > 0
            ? models.Select(m => $"/obj/{m.ToLowerInvariant()}.objbnd.dcx").Where(ds3.Contains).ToList()
            : ds3.Entries.Select(e => e.Path).Where(p => p is not null && p.StartsWith("/obj/", StringComparison.OrdinalIgnoreCase)
                && p.EndsWith(".objbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(p => p!).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return AnimTypes(paths.Select(p => (Path.GetFileName(p), (Func<byte[]>)(() => ds3.Read(p)))).ToList());
    }

    private static int AnimTypes(IReadOnlyList<(string Name, Func<byte[]> Read)> files)
    {
        var totals = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var notSpline = new List<string>();
        foreach (var (path, load) in files)
        {
            try
            {
                var bytes = load();
                var bnd = BND4.Read(DCX.Is(bytes) ? DCX.Decompress(bytes) : bytes);
                var anibnd = bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase) || f.ID == 400);
                if (anibnd is null)
                    continue;
                var inner = BND4.Read(DCX.Is(anibnd.Bytes) ? DCX.Decompress(anibnd.Bytes) : anibnd.Bytes);
                var line = new List<string>();
                bool anyOther = false;
                foreach (var f in inner.Files.Where(f => System.Text.RegularExpressions.Regex.IsMatch(ItemText.Leaf(f.Name), @"^a\d+_\d+\.hkx$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                {
                    var kind = AnimationClass(f.Bytes);
                    totals[kind] = totals.GetValueOrDefault(kind) + 1;
                    string read;
                    try
                    {
                        var hkx = SoulsAssetPipeline.Animation.HKX.Read(f.Bytes, SoulsAssetPipeline.Animation.HKX.HKXVariation.HKXDS3, false);
                        read = string.Join("+", hkx.DataSection.Objects.Select(o => o.GetType().Name).Where(n => n.StartsWith("HKA", StringComparison.Ordinal)).Distinct());
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        read = "unreadable: " + ex.GetType().Name;
                    }
                    if (kind != "hkaSplineCompressedAnimation")
                        anyOther = true;
                    line.Add($"{ItemText.Leaf(f.Name)}={kind}" + (kind == "hkaSplineCompressedAnimation" ? "" : $" [{read}, {f.Bytes.Length:N0} bytes]"));
                }
                if (anyOther)
                {
                    notSpline.Add(Path.GetFileName(path));
                    Console.WriteLine($"== {Path.GetFileName(path)}: {string.Join("; ", line)}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"== {Path.GetFileName(path)}: FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{files.Count} binders; animation classes: {string.Join(", ", totals.Select(t => $"{t.Key} x{t.Value}"))}");
        Console.WriteLine($"{notSpline.Count} binders hold an animation that is not spline-compressed: {string.Join(" ", notSpline)}");
        return 0;
    }

    /// <summary>The Havok animation class a packfile names (from its class-name strings), or "none".</summary>
    public static string AnimationClass(byte[] hkx)
    {
        var text = System.Text.Encoding.ASCII.GetString(hkx);
        foreach (var name in new[] { "hkaSplineCompressedAnimation", "hkaInterleavedUncompressedAnimation", "hkaQuantizedAnimation",
                     "hkaPredictiveCompressedAnimation", "hkaDeltaCompressedAnimation", "hkaWaveletCompressedAnimation" })
            if (text.Contains(name, StringComparison.Ordinal))
                return name;
        return "none";
    }

    public static int Model(IReadOnlyList<string> files)
    {
        int failed = 0;
        foreach (var path in files)
        {
            Console.WriteLine($"== {Path.GetFileName(path)}");
            try
            {
                var bytes = File.ReadAllBytes(path);
                var bnd = BND4.Read(DCX.Is(bytes) ? DCX.Decompress(bytes) : bytes);
                foreach (var f in bnd.Files)
                    Console.WriteLine($"   file {f.ID}: {ItemText.Leaf(f.Name)} ({f.Bytes.Length:N0} bytes)");
                foreach (var f in bnd.Files.Where(f => FLVER2.Is(f.Bytes)))
                    foreach (var line in DescribeFlver(FLVER2.Read(f.Bytes), ItemText.Leaf(f.Name)))
                        Console.WriteLine("   " + line);
                var anibnd = bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase) || f.ID == 400);
                if (anibnd is not null)
                {
                    var inner = BND4.Read(DCX.Is(anibnd.Bytes) ? DCX.Decompress(anibnd.Bytes) : anibnd.Bytes);
                    var skeleton = inner.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
                    if (skeleton is not null)
                    {
                        try
                        {
                            var s = AnimPort.ReadDs3Skeleton(skeleton.Bytes);
                            Console.WriteLine($"   skeleton (DS3): {s.Names.Count} bones: " + string.Join(" ", s.Names.Select((n, i) => $"{i}:{n}<{(i < s.Parents.Count ? s.Parents[i] : -9)}")));
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            Console.WriteLine($"   skeleton: not a DS3 one ({ex.GetType().Name})");
                        }
                    }
                    foreach (var line in TaeEvents(bnd))
                        Console.WriteLine("   " + line);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                Console.WriteLine($"   FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    public static IEnumerable<string> DescribeFlver(FLVER2 flver, string name)
    {
        yield return $"flver {name}: version 0x{flver.Header.Version:X}, {flver.Nodes.Count} nodes, {flver.Meshes.Count} meshes, {flver.Dummies.Count} dummies";
        yield return "  nodes: " + string.Join(" ", flver.Nodes.Select((n, i) => $"{i}:{n.Name}<{n.ParentIndex}"));
        for (int m = 0; m < flver.Meshes.Count; m++)
        {
            var mesh = flver.Meshes[m];
            var mat = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < flver.Materials.Count ? flver.Materials[mesh.MaterialIndex] : null;
            var normalWs = mesh.Vertices.GroupBy(v => v.NormalWs.Count > 0 ? v.NormalWs[0] : -1).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            var bones = mesh.Vertices.SelectMany(v => Enumerable.Range(0, 4).Where(i => v.BoneWeights[i] > 0).Select(i => v.BoneIndices[i])).GroupBy(b => b).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            var layout = mesh.VertexBuffers.Count > 0 && mesh.VertexBuffers[0].LayoutIndex < flver.BufferLayouts.Count
                ? string.Join(",", flver.BufferLayouts[mesh.VertexBuffers[0].LayoutIndex].Select(l => $"{l.Semantic}{l.Index}:{l.Type}")) : "?";
            yield return $"  mesh {m}: {mesh.Vertices.Count} vertices, mtd {mat?.MTD}, node {mesh.NodeIndex}, skinned {mesh.UseBoneWeights}, bone list [{string.Join(" ", mesh.BoneIndices)}], "
                         + $"NormalW {string.Join(" ", normalWs.Take(12))}, weighted bones {string.Join(" ", bones.Take(12))}, box {mesh.BoundingBox?.Min} .. {mesh.BoundingBox?.Max}, layout {layout}";
        }
    }

    public static int Run(IReadOnlyList<string> files)
    {
        int failed = 0;
        foreach (var path in files)
        {
            Console.WriteLine($"== {Path.GetFileName(path)}");
            try
            {
                var bytes = File.ReadAllBytes(path);
                var bnd = BND4.Read(DCX.Is(bytes) ? DCX.Decompress(bytes) : bytes);
                foreach (var line in TaeEvents(bnd))
                    Console.WriteLine("   " + line);
                foreach (var file in bnd.Files.Where(f => ItemText.Leaf(f.Name).EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"-- {ItemText.Leaf(file.Name)} ({file.Bytes.Length:N0} bytes)");
                    var hkx = DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes;
                    foreach (var line in Describe(CollisionPort.ReadDs3(hkx)))
                        Console.WriteLine("   " + line);
                    try
                    {
                        var converted = CollisionPort.Convert(hkx, null, xml: false, keepSimdTree: false, null, dropBroken: true);
                        Console.WriteLine($"   converts: {converted.Triangles} triangles, {converted.Convex} convex bodies" + string.Concat(converted.Notes.Select(n => "\n     " + n)));
                        foreach (var b in CollisionPort.Decode(CollisionPort.Read(converted.Bytes)))
                        {
                            var (min, max) = Box(b.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }));
                            Console.WriteLine($"     Elden Ring body {b.Name}: {b.Triangles.Count} triangles, box {Text(min)} .. {Text(max)}, {Outward(b.Triangles)} face away from the middle");
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Console.WriteLine($"   does NOT convert: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                Console.WriteLine($"   FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Each animation's TAE events in an objbnd's animation binder: type, time, and the first parameters.</summary>
    public static IEnumerable<string> TaeEvents(BND4 objbnd)
    {
        var anibndFile = objbnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase));
        if (anibndFile is null)
            yield break;
        var anibnd = BND4.Read(DCX.Is(anibndFile.Bytes) ? DCX.Decompress(anibndFile.Bytes) : anibndFile.Bytes);
        foreach (var f in anibnd.Files)
            yield return $"anibnd file {f.ID}: {ItemText.Leaf(f.Name)} ({f.Bytes.Length:N0} bytes)";
        foreach (var file in anibnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
        {
            var tae = TAE.Read(file.Bytes);
            yield return $"tae {ItemText.Leaf(file.Name)}: {tae.Animations.Count} animations, format {tae.Format}, big endian {tae.BigEndian}";
            foreach (var anim in tae.Animations)
                foreach (var e in anim.Events)
                {
                    var b = e.GetParameterBytes(tae.BigEndian);
                    var ints = Enumerable.Range(0, Math.Min(b.Length / 4, 6)).Select(i => BitConverter.ToInt32(b, i * 4));
                    yield return $"  anim {anim.ID}: event {e.Type} at {e.StartTime:0.###}-{e.EndTime:0.###}: {string.Join(" ", ints)}";
                }
        }
    }

    public static IEnumerable<string> Describe(Ds3.hkRootLevelContainer root)
    {
        foreach (var variant in root.m_namedVariants ?? [])
        {
            if (variant.m_variant is not Ds3.hknpPhysicsSceneData scene)
            {
                yield return $"root object {variant.m_className}: not collision";
                continue;
            }
            foreach (var system in scene.m_systemDatas ?? [])
            {
                yield return $"system {system.m_name}: {system.m_bodyCinfos?.Count ?? 0} bodies, {system.m_motionProperties?.Count ?? 0} motion properties, {system.m_materials?.Count ?? 0} materials";
                foreach (var body in system.m_bodyCinfos ?? [])
                {
                    var p = body.m_position;
                    yield return $"body {body.m_name}: {body.m_shape?.GetType().Name ?? "no shape"}, at {p.X:0.##} {p.Y:0.##} {p.Z:0.##}, flags {body.m_flags}, filter 0x{body.m_collisionFilterInfo:X}";
                    if (body.m_shape is Ds3.hknpConvexPolytopeShape convex)
                    {
                        var (min, max) = Box((convex.m_vertices ?? []).Select(v => new Vector3(v.X, v.Y, v.Z)));
                        yield return $"  hull: {convex.m_vertices?.Count ?? 0} vertices, {convex.m_faces?.Count ?? 0} faces, box {Text(min)} .. {Text(max)}";
                    }
                    if (body.m_shape is not Ds3.hknpCompressedMeshShape { m_data: { } data })
                        continue;
                    var tree = data.m_meshTree;
                    var cm = (Ds3.hknpCompressedMeshShape)body.m_shape;
                    yield return $"  shape: flags {cm.m_flags}, key bits {cm.m_numShapeKeyBits}, dispatch {cm.m_dispatchType}, radius {cm.m_convexRadius}, quadIsFlat {cm.m_quadIsFlat?.m_storage?.m_numBits} bits [{string.Join(" ", (cm.m_quadIsFlat?.m_storage?.m_words ?? []).Select(w => w.ToString("X")))}], "
                                 + $"triangleIsInterior {cm.m_triangleIsInterior?.m_storage?.m_numBits} bits [{string.Join(" ", (cm.m_triangleIsInterior?.m_storage?.m_words ?? []).Select(w => w.ToString("X")))}], simd nodes {data.m_simdTree?.m_nodes?.Count ?? 0}, motion {body.m_motionId}, quality {body.m_qualityId}";
                    yield return $"  mesh: {tree.m_sections?.Count ?? 0} sections, {tree.m_primitives?.Count ?? 0} primitives, {tree.m_packedVertices?.Count ?? 0} packed vertices, "
                                 + $"{tree.m_sharedVertices?.Count ?? 0} shared vertices, {tree.m_sharedVerticesIndex?.Count ?? 0} shared indices, domain {Text(tree.m_domain?.m_min)} .. {Text(tree.m_domain?.m_max)}";
                    int at = 0;
                    foreach (var s in tree.m_sections ?? [])
                    {
                        int first = (int)(s.m_primitives.m_data >> 8), count = (int)(s.m_primitives.m_data & 0xFF);
                        int highest = -1, dead = 0;
                        for (int i = 0; i < count && first + i < tree.m_primitives!.Count; i++)
                        {
                            var q = tree.m_primitives[first + i];
                            if (q.m_indices_0 == 0xDE && q.m_indices_1 == 0xAD && q.m_indices_2 == 0xDE && q.m_indices_3 == 0xAD)
                            {
                                dead++;
                                continue;
                            }
                            highest = Math.Max(highest, Math.Max(Math.Max(q.m_indices_0, q.m_indices_1), Math.Max(q.m_indices_2, q.m_indices_3)));
                        }
                        yield return $"  section {at++}: packed first {s.m_firstPackedVertex} count field {s.m_numPackedVertices}; shared word 0x{s.m_sharedVertices.m_data:X} (first {s.m_sharedVertices.m_data >> 8}, low byte {s.m_sharedVertices.m_data & 0xFF}), "
                                     + $"shared-index count field {s.m_numSharedIndices}; primitives {first}+{count} ({dead} dead), highest vertex index {highest}; data runs 0x{s.m_dataRuns.m_data:X}; flags {s.m_flags}";
                    }
                    foreach (var s in tree.m_sections ?? [])
                    {
                        yield return $"  section codec: offset ({s.m_codecParms_0:0.####} {s.m_codecParms_1:0.####} {s.m_codecParms_2:0.####}) scale ({s.m_codecParms_3:0.######} {s.m_codecParms_4:0.######} {s.m_codecParms_5:0.######}), domain {Text(s.m_domain?.m_min)} .. {Text(s.m_domain?.m_max)}, page {s.m_page}, leaf {s.m_leafIndex}, layer {s.m_layerData}";
                        if ((s.m_nodes?.Count ?? 0) <= 16)
                            yield return "  section nodes: " + string.Join(" | ", (s.m_nodes ?? []).Select(n => $"{n.m_xyz_0:X2} {n.m_xyz_1:X2} {n.m_xyz_2:X2} data {n.m_data:X2}"));
                    }
                    if ((tree.m_nodes?.Count ?? 0) <= 16)
                        yield return "  tree nodes: " + string.Join(" | ", (tree.m_nodes ?? []).Select(n => $"{n.m_xyz_0:X2} {n.m_xyz_1:X2} {n.m_xyz_2:X2} hi {n.m_hiData:X2} lo {n.m_loData:X4}"));
                    yield return $"  keys: {tree.m_numPrimitiveKeys} primitive keys, {tree.m_bitsPerKey} bits per key, max {tree.m_maxKeyValue}; data runs: " + string.Join(" ", (tree.m_primitiveDataRuns ?? []).Select(r => $"[{r.m_value?.m_data} at {r.m_index} x{r.m_count}]"));
                    if ((tree.m_primitives?.Count ?? 0) <= 12)
                    {
                        yield return "  primitives: " + string.Join(" | ", tree.m_primitives!.Select(q => $"{q.m_indices_0} {q.m_indices_1} {q.m_indices_2} {q.m_indices_3}"));
                        yield return "  shared indices: " + string.Join(" ", tree.m_sharedVerticesIndex ?? []);
                        yield return "  shared vertices: " + string.Join(" ", (tree.m_sharedVertices ?? []).Select(v => $"0x{v:X}"));
                        yield return "  packed vertices: " + string.Join(" ", (tree.m_packedVertices ?? []).Select(v => $"0x{v:X}"));
                    }
                    string decoded;
                    try
                    {
                        var triangles = CollisionPort.DecodeDs3(tree);
                        var (min, max) = Box(triangles.SelectMany(t => new[] { t.A, t.B, t.C }));
                        decoded = $"decodes: {triangles.Count} triangles, box {Text(min)} .. {Text(max)}, {Outward(triangles)} face away from the middle";
                    }
                    catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                    {
                        decoded = $"does NOT decode ({ex.GetType().Name})";
                    }
                    yield return "  " + decoded;
                }
            }
        }
    }

    /// <summary>How many triangles' normals ((b - a) x (c - a)) point away from the middle of them all.</summary>
    private static int Outward(List<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        if (triangles.Count == 0)
            return 0;
        var (min, max) = Box(triangles.SelectMany(t => new[] { t.A, t.B, t.C }));
        var mid = (min + max) / 2;
        return triangles.Count(t => Vector3.Dot(Vector3.Cross(t.B - t.A, t.C - t.A), (t.A + t.B + t.C) / 3 - mid) > 0);
    }

    private static (Vector3 Min, Vector3 Max) Box(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            any = true;
        }
        return any ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }

    private static string Text(Vector3 v) => $"({v.X:0.##} {v.Y:0.##} {v.Z:0.##})";
    private static string Text(Vector4? v) => v is { } x ? $"({x.X:0.##} {x.Y:0.##} {x.Z:0.##})" : "(none)";
}
