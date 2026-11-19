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
