using System.Numerics;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Solid blocks inside a DS3 collision mesh, as triangles.
///
/// A Havok 2014 compressed mesh can hold whole convex hulls beside its triangles and quads. Dark Souls III builds the
/// collision of its moving objects out of them: the platform of every lift ("ev", "model_EV1"), the frame of a gate,
/// the base of a lever. Such a primitive reads (a, b, b, b): its last three indices are equal, and a is not a vertex
/// but the place of three numbers in the section's shared-vertex index list:
/// <list type="bullet">
/// <item>[a]: the hull's vertex count in the high byte;</item>
/// <item>[a + 1]: the first of its vertices in the shared-vertex list, counted in 64-bit entries;</item>
/// <item>[a + 2]: the hull's convex radius (hkHalf16).</item>
/// </list>
/// The vertices are 32-bit packed ones (11, 11 and 10 bits), two to a 64-bit entry, low half first, and they are
/// measured inside the box of the hull's own leaf in the section's tree, not inside the section. Read from DS3's gate
/// o400340 and lever o300500 (2026-11-19): decoded this way the gate's three hulls are its two posts and its lintel,
/// 1.03 m either side of the middle, with the door's own hull fitting between the posts.
///
/// The porter read these as triangles, found they "pointed past the section's vertices" and left the body out as a
/// placeholder, so every lift had no floor (Devin fell through one, 2026-11-19). Elden Ring's files give no sign of
/// holding hulls in a mesh, so a body with hulls gets a new tree here, made of nothing but triangles and quads: each
/// hull becomes its faces. The tree is laid out as DS3's own triangle meshes are (the door leaf of o300310): sections
/// of packed vertices, one data run per section, a box tree per section and one over the sections.
///
/// The box codecs are Havok's, as read by HKX2 (Katalash and contributors, DSMapStudio, MIT) and Smithbox's
/// navigation tool (vawser and contributors, GPL-3.0): see <see cref="NavFaceTree"/>.
/// </summary>
public static class CollisionHulls
{
    /// <summary>The most primitives a section gets: a leaf names its primitive in 7 bits, its vertices in a byte.</summary>
    public const int SectionPrimitives = 60;

    private const float BoxScale = 226f;

    /// <summary>A triangle (3 corners) or a flat convex quad (4 corners), in the body's space.</summary>
    public sealed record Primitive(Vector3[] Corners)
    {
        public Vector3 Min => Corners.Aggregate(Vector3.Min);
        public Vector3 Max => Corners.Aggregate(Vector3.Max);
    }

    public static bool IsDead(Ds3.hkcdStaticMeshTreeBasePrimitive p) =>
        p.m_indices_0 == 0xDE && p.m_indices_1 == 0xAD && p.m_indices_2 == 0xDE && p.m_indices_3 == 0xAD;

    /// <summary>A primitive that is a hull, not a triangle: its last three indices are equal.</summary>
    public static bool IsHull(Ds3.hkcdStaticMeshTreeBasePrimitive p) =>
        !IsDead(p) && p.m_indices_1 == p.m_indices_2 && p.m_indices_2 == p.m_indices_3;

    public static bool HasHulls(Ds3.hknpCompressedMeshShapeTree? tree) => tree?.m_primitives?.Any(IsHull) == true;

    /// <summary>
    /// Rebuilds every mesh body that holds hulls as a mesh of triangles and quads. A body whose hulls can't be read
    /// is left as it was (the caller's own check then decides what happens to it). Returns the hulls rebuilt.
    /// </summary>
    public static int Expand(Ds3.hkRootLevelContainer root, List<string> notes)
    {
        int hulls = 0;
        var done = new HashSet<Ds3.hknpCompressedMeshShape>(ReferenceEqualityComparer.Instance);
        foreach (var system in (root.m_namedVariants ?? []).Select(v => v.m_variant).OfType<Ds3.hknpPhysicsSceneData>().SelectMany(s => s.m_systemDatas ?? []))
            foreach (var body in system.m_bodyCinfos ?? [])
            {
                if (body.m_shape is not Ds3.hknpCompressedMeshShape { m_data.m_meshTree: { } tree } mesh || !HasHulls(tree) || !done.Add(mesh))
                    continue;
                try
                {
                    var primitives = Primitives(tree, out int count, out int flat);
                    if (primitives.Count == 0)
                        throw new InvalidDataException("no hull has a face");
                    var rebuilt = Build(primitives);
                    if (Check(rebuilt) is { } problem)
                        throw new InvalidDataException("the rebuilt mesh is wrong: " + problem);
                    Replace(mesh, rebuilt);
                    hulls += count;
                    notes.Add($"body {body.m_name}: {count} solid blocks (convex hulls inside the mesh) rebuilt as {primitives.Count} faces"
                              + (flat > 0 ? $", {flat} flat ones left out" : ""));
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    notes.Add($"body {body.m_name}: its solid blocks can't be read ({ex.Message})");
                }
            }
        return hulls;
    }

    /// <summary>Every primitive of a tree as triangles and quads: its own, and the faces of its hulls.</summary>
    public static List<Primitive> Primitives(Ds3.hknpCompressedMeshShapeTree tree, out int hulls, out int flat)
    {
        hulls = flat = 0;
        var result = new List<Primitive>();
        var domainMin = V(tree.m_domain.m_min);
        var domainMax = V(tree.m_domain.m_max);
        foreach (var section in tree.m_sections ?? [])
        {
            int numPacked = (int)(section.m_sharedVertices.m_data & 0xFF), firstShared = (int)(section.m_sharedVertices.m_data >> 8);
            int firstPrimitive = (int)(section.m_primitives.m_data >> 8), count = (int)(section.m_primitives.m_data & 0xFF);
            var offset = new Vector3(section.m_codecParms_0, section.m_codecParms_1, section.m_codecParms_2);
            var scale = new Vector3(section.m_codecParms_3, section.m_codecParms_4, section.m_codecParms_5);
            Vector3 Vertex(byte index)
            {
                if (index < numPacked)
                    return Unpack(tree.m_packedVertices[(int)(index + section.m_firstPackedVertex)], offset, scale);
                ulong v = tree.m_sharedVertices[tree.m_sharedVerticesIndex[index + firstShared - numPacked]];
                return domainMin + new Vector3(
                    (float)((v & 0x1FFFFF) * ((domainMax.X - domainMin.X) / 2097151.0)),
                    (float)(((v >> 21) & 0x1FFFFF) * ((domainMax.Y - domainMin.Y) / 2097151.0)),
                    (float)(((v >> 42) & 0x3FFFFF) * ((domainMax.Z - domainMin.Z) / 4194303.0)));
            }
            Dictionary<int, (Vector3 Min, Vector3 Max)>? leaves = null;
            for (int i = 0; i < count; i++)
            {
                var p = tree.m_primitives[firstPrimitive + i];
                if (IsDead(p))
                    continue;
                if (!IsHull(p))
                {
                    result.Add(new Primitive(p.m_indices_2 == p.m_indices_3
                        ? [Vertex(p.m_indices_0), Vertex(p.m_indices_1), Vertex(p.m_indices_2)]
                        : [Vertex(p.m_indices_0), Vertex(p.m_indices_1), Vertex(p.m_indices_2), Vertex(p.m_indices_3)]));
                    continue;
                }
                leaves ??= LeafBoxes(section);
                if (!leaves.TryGetValue(i, out var box))
                    throw new InvalidDataException($"hull primitive {i} has no leaf in its section's tree");
                var faces = HullFaces(HullVertices(tree, firstShared + p.m_indices_0, box.Min, box.Max));
                hulls++;
                if (faces.Count == 0)
                    flat++;
                result.AddRange(faces);
            }
        }
        return result;
    }

    /// <summary>A hull's vertices, from the three index-list entries at <paramref name="at"/> (see the class notes).</summary>
    public static List<Vector3> HullVertices(Ds3.hknpCompressedMeshShapeTree tree, int at, Vector3 boxMin, Vector3 boxMax)
    {
        var index = tree.m_sharedVerticesIndex ?? [];
        if (at < 0 || at + 2 >= index.Count)
            throw new InvalidDataException($"hull record {at} lies past the {index.Count} index entries");
        int count = index[at] >> 8, first = index[at + 1];
        var words = tree.m_sharedVertices ?? [];
        if (count < 4 || first + (count + 1) / 2 > words.Count)
            throw new InvalidDataException($"hull record {at} names {count} vertices from entry {first} of {words.Count}");
        var scale = (boxMax - boxMin) / new Vector3(2047, 2047, 1023);
        var vertices = new List<Vector3>(count);
        for (int i = 0; i < count; i++)
        {
            ulong word = words[first + i / 2];
            vertices.Add(Unpack((uint)(i % 2 == 0 ? word : word >> 32), boxMin, scale));
        }
        return vertices;
    }

    private static Vector3 Unpack(uint v, Vector3 offset, Vector3 scale) =>
        offset + new Vector3(v & 0x7FF, (v >> 11) & 0x7FF, (v >> 22) & 0x3FF) * scale;

    private static Vector3 V(Vector4 v) => new(v.X, v.Y, v.Z);

    /// <summary>The box of each leaf of a section's tree, by the primitive (counted within the section) it names.</summary>
    public static Dictionary<int, (Vector3 Min, Vector3 Max)> LeafBoxes(Ds3.hkcdStaticMeshTreeBaseSection section)
    {
        var boxes = new Dictionary<int, (Vector3, Vector3)>();
        var nodes = section.m_nodes ?? [];
        if (nodes.Count == 0)
            return boxes;
        var stack = new Stack<(int Index, Vector3 Min, Vector3 Max)>();
        stack.Push((0, V(section.m_domain.m_min), V(section.m_domain.m_max)));
        int visited = 0;
        while (stack.Count > 0)
        {
            var (i, pmin, pmax) = stack.Pop();
            if (i < 0 || i >= nodes.Count || ++visited > nodes.Count)
                throw new InvalidDataException("the section's tree names a node it doesn't have");
            var node = nodes[i];
            var (min, max) = Decompress(node.m_xyz_0, node.m_xyz_1, node.m_xyz_2, pmin, pmax);
            if ((node.m_data & 1) == 0)
            {
                boxes[node.m_data >> 1] = (min, max);
                continue;
            }
            // An inner node: the left child follows it, the right one is (data - 1) nodes on.
            stack.Push((i + (node.m_data & ~1), min, max));
            stack.Push((i + 1, min, max));
        }
        return boxes;
    }

    private static (Vector3 Min, Vector3 Max) Decompress(byte x, byte y, byte z, Vector3 parentMin, Vector3 parentMax)
    {
        var extent = parentMax - parentMin;
        static float Lo(byte b) => (b >> 4) * (b >> 4) / BoxScale;
        static float Hi(byte b) => (b & 0xF) * (b & 0xF) / BoxScale;
        return (parentMin + new Vector3(Lo(x), Lo(y), Lo(z)) * extent, parentMax - new Vector3(Hi(x), Hi(y), Hi(z)) * extent);
    }

    // ---- A hull's faces ----

    /// <summary>
    /// The faces of the convex hull of some points, as triangles and flat quads wound so that their normals
    /// ((b - a) x (c - a)) point out of the hull. Empty when the points have no volume. Every plane through three of
    /// the points that has all the others on one side is a face; a hull has at most a few dozen vertices.
    /// </summary>
    public static List<Primitive> HullFaces(IReadOnlyList<Vector3> input)
    {
        var faces = new List<Primitive>();
        var points = new List<Vector3>();
        var min = input.Aggregate(Vector3.Min);
        var max = input.Aggregate(Vector3.Max);
        float size = (max - min).Length();
        if (size <= 0)
            return faces;
        float eps = MathF.Max(size * 2e-4f, 1e-6f);
        foreach (var p in input)
            if (!points.Any(q => Vector3.DistanceSquared(p, q) < eps * eps))
                points.Add(p);
        int n = points.Count;
        if (n < 4)
            return faces;

        var planes = new List<(Vector3 Normal, float D)>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                for (int k = j + 1; k < n; k++)
                {
                    var normal = Vector3.Cross(points[j] - points[i], points[k] - points[i]);
                    float length = normal.Length();
                    if (length < eps * eps)
                        continue; // three points in a line
                    normal /= length;
                    float d = Vector3.Dot(normal, points[i]);
                    bool above = false, below = false;
                    foreach (var p in points)
                    {
                        float side = Vector3.Dot(normal, p) - d;
                        above |= side > eps;
                        below |= side < -eps;
                        if (above && below)
                            break;
                    }
                    if (above == below)
                        continue; // cuts through the hull, or every point lies in it (no volume)
                    if (above)
                    {
                        normal = -normal;
                        d = -d;
                    }
                    if (planes.Any(q => Vector3.Dot(q.Normal, normal) > 0.9999f && MathF.Abs(q.D - d) <= 2 * eps))
                        continue;
                    planes.Add((normal, d));

                    // The face: the points in the plane, in order round its middle, anticlockwise seen from outside.
                    var on = points.Where(p => MathF.Abs(Vector3.Dot(normal, p) - d) <= eps).ToList();
                    var mid = on.Aggregate(Vector3.Zero, (a, b) => a + b) / on.Count;
                    var u = Vector3.Normalize(on[0] - mid);
                    var w = Vector3.Cross(normal, u);
                    on = on.OrderBy(p => MathF.Atan2(Vector3.Dot(p - mid, w), Vector3.Dot(p - mid, u))).ToList();
                    // Points along an edge add nothing.
                    for (int c = 0; c < on.Count && on.Count > 3;)
                    {
                        var a = on[(c + on.Count - 1) % on.Count];
                        var b = on[(c + 1) % on.Count];
                        if (Vector3.Cross(on[c] - a, b - on[c]).Length() <= eps * (b - a).Length())
                            on.RemoveAt(c);
                        else
                            c++;
                    }
                    if (on.Count < 3)
                        continue;
                    for (int c = 1; c + 1 < on.Count; c += 2)
                        faces.Add(new Primitive(c + 2 < on.Count ? [on[0], on[c], on[c + 1], on[c + 2]] : [on[0], on[c], on[c + 1]]));
                }
        // A real hull closes: fewer than four faces means the points were flat after all.
        return planes.Count >= 4 ? faces : [];
    }

    // ---- A new tree ----

    /// <summary>Gives a mesh a new tree (<see cref="Build"/>) and the shape settings that go with it.</summary>
    public static void Replace(Ds3.hknpCompressedMeshShape mesh, Ds3.hknpCompressedMeshShapeTree tree)
    {
        mesh.m_data.m_meshTree = tree;
        if (mesh.m_data.m_simdTree?.m_nodes is { } simd)
            simd.Clear(); // it described the old tree; Havok answers from the mesh tree without one
        mesh.m_numShapeKeyBits = (byte)tree.m_bitsPerKey;
        // Bit 512 marks a mesh that holds hulls (DS3's gate and lever have 516, its triangle meshes 4).
        mesh.m_flags &= unchecked((ushort)~512);
        mesh.m_quadIsFlat = Bits(tree.m_primitives.Count, i => tree.m_primitives[i].m_indices_2 != tree.m_primitives[i].m_indices_3);
        mesh.m_triangleIsInterior = Bits((int)tree.m_maxKeyValue + 1, _ => false);
        if (mesh is Ds3.fsnpCustomParamCompressedMeshShape fsnp)
            fsnp.m_triangleIndexToShapeKey = [];
    }

    private static Ds3.hkBitField Bits(int count, Func<int, bool> set)
    {
        var words = new uint[(count + 31) / 32];
        for (int i = 0; i < count; i++)
            if (set(i))
                words[i / 32] |= 1u << (i % 32);
        return new Ds3.hkBitField
        {
            m_storage = new Ds3.hkBitFieldStoragehkArrayunsignedinthkContainerHeapAllocator { m_words = [.. words], m_numBits = count },
        };
    }

    private sealed class Node
    {
        public Vector3 Min, Max;
        public Node? Left, Right;
        public int Item = -1;
    }

    /// <summary>
    /// A DS3 mesh tree of triangles and quads: sections of at most <see cref="SectionPrimitives"/> primitives with
    /// their own packed vertices, split at the median of the longest axis.
    /// </summary>
    public static Ds3.hknpCompressedMeshShapeTree Build(IReadOnlyList<Primitive> primitives)
    {
        if (primitives.Count == 0)
            throw new InvalidDataException("a mesh needs a primitive");
        var groups = new List<List<Primitive>>();
        Group(primitives.ToList(), groups);
        if (groups.Count > 0x7FFF)
            throw new InvalidDataException($"{groups.Count} sections are more than a mesh tree can name");

        var tree = new Ds3.hknpCompressedMeshShapeTree
        {
            m_nodes = [], m_sections = [], m_primitives = [], m_sharedVerticesIndex = [], m_packedVertices = [], m_sharedVertices = [], m_primitiveDataRuns = [],
        };
        var sectionBoxes = new List<(Vector3 Min, Vector3 Max)>();
        uint maxKey = 0;
        int keys = 0;
        foreach (var group in groups)
        {
            var min = group.Select(p => p.Min).Aggregate(Vector3.Min);
            var max = group.Select(p => p.Max).Aggregate(Vector3.Max);
            var scale = (max - min) / new Vector3(2047, 2047, 1023);
            uint Pack(Vector3 v)
            {
                static uint Q(float value, float lo, float step, uint top) => step <= 0 ? 0 : Math.Min(top, (uint)MathF.Round((value - lo) / step));
                return Q(v.X, min.X, scale.X, 2047) | (Q(v.Y, min.Y, scale.Y, 2047) << 11) | (Q(v.Z, min.Z, scale.Z, 1023) << 22);
            }
            var packed = new List<uint>();
            byte Index(Vector3 v)
            {
                uint p = Pack(v);
                int at = packed.IndexOf(p);
                if (at < 0)
                {
                    at = packed.Count;
                    packed.Add(p);
                }
                return (byte)at;
            }
            int sectionIndex = tree.m_sections.Count, firstPrimitive = tree.m_primitives.Count;
            var boxes = new List<(Vector3 Min, Vector3 Max)>();
            foreach (var p in group)
            {
                var c = p.Corners;
                byte i0 = Index(c[0]), i1 = Index(c[1]), i2 = Index(c[2]), i3 = c.Length == 4 ? Index(c[3]) : i2;
                // The boxes hold the vertices as stored (packing moves each by up to half a step).
                var stored = new[] { i0, i1, i2, i3 }.Select(i => Unpack(packed[i], min, scale)).ToList();
                // Packing can fold a sliver's corners together; a quad whose last two corners meet is its triangle.
                tree.m_primitives.Add(new Ds3.hkcdStaticMeshTreeBasePrimitive { m_indices_0 = i0, m_indices_1 = i1, m_indices_2 = i2, m_indices_3 = i3 });
                boxes.Add((stored.Aggregate(Vector3.Min), stored.Aggregate(Vector3.Max)));
                bool quad = i2 != i3;
                keys += quad ? 2 : 1;
                maxKey = Math.Max(maxKey, (uint)((sectionIndex << 8) | ((boxes.Count - 1) << 1) | (quad ? 1 : 0)));
            }
            if (packed.Count > 255)
                throw new InvalidDataException($"a section of {group.Count} primitives has {packed.Count} vertices");

            var section = new Ds3.hkcdStaticMeshTreeBaseSection
            {
                m_nodes = [],
                m_domain = new Ds3.hkAabb { m_min = new Vector4(min, 1), m_max = new Vector4(max, 1) },
                m_codecParms_0 = min.X, m_codecParms_1 = min.Y, m_codecParms_2 = min.Z,
                m_codecParms_3 = scale.X, m_codecParms_4 = scale.Y, m_codecParms_5 = scale.Z,
                m_firstPackedVertex = (uint)tree.m_packedVertices.Count,
                // The low byte is where a primitive's indices pass from packed vertices to shared ones.
                m_sharedVertices = new Ds3.hkcdStaticMeshTreeBaseSectionSharedVertices { m_data = (uint)packed.Count },
                m_primitives = new Ds3.hkcdStaticMeshTreeBaseSectionPrimitives { m_data = (uint)group.Count | ((uint)firstPrimitive << 8) },
                m_dataRuns = new Ds3.hkcdStaticMeshTreeBaseSectionDataRuns { m_data = 1u | ((uint)tree.m_primitiveDataRuns.Count << 8) },
                m_numPackedVertices = (byte)packed.Count,
                m_numSharedIndices = 0,
            };
            var items = Enumerable.Range(0, group.Count).ToArray();
            var root = Split(boxes, items, 0, items.Length);
            EncodeSection(section.m_nodes, root, min, max, isRoot: true);
            tree.m_sections.Add(section);
            tree.m_packedVertices.AddRange(packed);
            tree.m_primitiveDataRuns.Add(new Ds3.hknpCompressedMeshShapeTreeDataRun
            {
                m_value = new Ds3.hknpCompressedMeshShapeTreeDataRunData { m_data = 0xFFFF }, m_index = 0, m_count = (byte)group.Count,
            });
            sectionBoxes.Add((min, max));
        }

        var all = Enumerable.Range(0, sectionBoxes.Count).ToArray();
        var top = Split(sectionBoxes, all, 0, all.Length);
        tree.m_domain = new Ds3.hkAabb { m_min = new Vector4(top.Min, 1), m_max = new Vector4(top.Max, 1) };
        EncodeTop(tree, top, top.Min, top.Max, isRoot: true);
        tree.m_numPrimitiveKeys = keys;
        tree.m_maxKeyValue = maxKey;
        tree.m_bitsPerKey = Math.Max(1, 32 - BitOperations.LeadingZeroCount(maxKey));
        return tree;
    }

    private static void Group(List<Primitive> primitives, List<List<Primitive>> groups)
    {
        if (primitives.Count <= SectionPrimitives)
        {
            groups.Add(primitives);
            return;
        }
        var centres = primitives.Select(p => (p.Min + p.Max) / 2).ToList();
        var extent = centres.Aggregate(Vector3.Max) - centres.Aggregate(Vector3.Min);
        Func<Vector3, float> axis = extent.X >= extent.Y && extent.X >= extent.Z ? v => v.X : extent.Y >= extent.Z ? v => v.Y : v => v.Z;
        var order = Enumerable.Range(0, primitives.Count).OrderBy(i => axis(centres[i])).ThenBy(i => i).Select(i => primitives[i]).ToList();
        int half = order.Count / 2;
        Group(order.GetRange(0, half), groups);
        Group(order.GetRange(half, order.Count - half), groups);
    }

    private static Node Split(IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes, int[] items, int start, int count)
    {
        var node = new Node { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        for (int i = start; i < start + count; i++)
        {
            node.Min = Vector3.Min(node.Min, boxes[items[i]].Min);
            node.Max = Vector3.Max(node.Max, boxes[items[i]].Max);
        }
        if (count == 1)
        {
            node.Item = items[start];
            return node;
        }
        var cmin = new Vector3(float.MaxValue);
        var cmax = new Vector3(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            var c = boxes[items[i]].Min + boxes[items[i]].Max;
            cmin = Vector3.Min(cmin, c);
            cmax = Vector3.Max(cmax, c);
        }
        var extent = cmax - cmin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        float Centre(int item)
        {
            var c = boxes[item].Min + boxes[item].Max;
            return axis == 0 ? c.X : axis == 1 ? c.Y : c.Z;
        }
        Array.Sort(items, start, count, Comparer<int>.Create((a, b) =>
        {
            int byCentre = Centre(a).CompareTo(Centre(b));
            return byCentre != 0 ? byCentre : a.CompareTo(b);
        }));
        int half = count / 2;
        node.Left = Split(boxes, items, start, half);
        node.Right = Split(boxes, items, start + half, count - half);
        return node;
    }

    /// <summary>One axis of a box within its parent's, rounded outwards (as <see cref="NavFaceTree"/> does).</summary>
    private static byte CompressAxis(float min, float max, float parentMin, float parentMax)
    {
        float extent = parentMax - parentMin;
        if (extent <= 0)
            return 0;
        float norm = BoxScale / extent;
        int h = Math.Min(15, (int)MathF.Floor(MathF.Sqrt(MathF.Max((min - parentMin) * norm, 0))));
        int l = Math.Min(15, (int)MathF.Floor(MathF.Sqrt(MathF.Max((parentMax - max) * norm, 0))));
        while (h > 0 && parentMin + h * h / BoxScale * extent > min)
            h--;
        while (l > 0 && parentMax - l * l / BoxScale * extent < max)
            l--;
        return (byte)((h << 4) | l);
    }

    private static (byte X, byte Y, byte Z) Compress(Node node, Vector3 parentMin, Vector3 parentMax, bool isRoot) => isRoot
        ? ((byte)0, (byte)0, (byte)0)
        : (CompressAxis(node.Min.X, node.Max.X, parentMin.X, parentMax.X), CompressAxis(node.Min.Y, node.Max.Y, parentMin.Y, parentMax.Y),
            CompressAxis(node.Min.Z, node.Max.Z, parentMin.Z, parentMax.Z));

    // A section's tree: a leaf holds its primitive doubled; an inner node the distance to its right child plus one.
    private static void EncodeSection(List<Ds3.hkcdStaticTreeCodec3Axis4> nodes, Node node, Vector3 parentMin, Vector3 parentMax, bool isRoot)
    {
        int index = nodes.Count;
        var (x, y, z) = Compress(node, parentMin, parentMax, isRoot);
        var codec = new Ds3.hkcdStaticTreeCodec3Axis4 { m_xyz_0 = x, m_xyz_1 = y, m_xyz_2 = z };
        nodes.Add(codec);
        var (min, max) = Decompress(x, y, z, parentMin, parentMax);
        if (node.Item >= 0)
        {
            codec.m_data = (byte)(node.Item << 1);
            return;
        }
        EncodeSection(nodes, node.Left!, min, max, false);
        codec.m_data = checked((byte)((nodes.Count - index) | 1));
        EncodeSection(nodes, node.Right!, min, max, false);
    }

    // The tree over the sections: a leaf holds its section in 15 bits; an inner node has the top bit set and half the
    // distance to its right child (HKX2's hknpCompressedMeshShapeData.buildBVHTree reads it so).
    private static void EncodeTop(Ds3.hknpCompressedMeshShapeTree tree, Node node, Vector3 parentMin, Vector3 parentMax, bool isRoot)
    {
        var nodes = tree.m_nodes;
        int index = nodes.Count;
        var (x, y, z) = Compress(node, parentMin, parentMax, isRoot);
        var codec = new Ds3.hkcdStaticTreeCodec3Axis5 { m_xyz_0 = x, m_xyz_1 = y, m_xyz_2 = z };
        nodes.Add(codec);
        var (min, max) = Decompress(x, y, z, parentMin, parentMax);
        if (node.Item >= 0)
        {
            codec.m_hiData = (byte)((node.Item >> 8) & 0x7F);
            codec.m_loData = (byte)(node.Item & 0xFF);
            tree.m_sections[node.Item].m_leafIndex = (ushort)index;
            return;
        }
        EncodeTop(tree, node.Left!, min, max, false);
        int offset = (nodes.Count - index) / 2;
        codec.m_hiData = (byte)(((offset >> 8) & 0x7F) | 0x80);
        codec.m_loData = (byte)(offset & 0xFF);
        EncodeTop(tree, node.Right!, min, max, false);
    }

    /// <summary>
    /// Whether a tree's boxes hold its primitives: every primitive of every section sits in exactly one leaf, inside
    /// that leaf's box, and every section in one leaf of the tree over them. Returns the first problem, or null.
    /// </summary>
    public static string? Check(Ds3.hknpCompressedMeshShapeTree tree, float tolerance = 1e-3f)
    {
        var t = new Vector3(tolerance);
        var seen = new int[tree.m_sections.Count];
        var stack = new Stack<(int Index, Vector3 Min, Vector3 Max)>();
        stack.Push((0, V(tree.m_domain.m_min), V(tree.m_domain.m_max)));
        int reached = 0;
        while (stack.Count > 0)
        {
            var (i, pmin, pmax) = stack.Pop();
            if (i < 0 || i >= tree.m_nodes.Count || ++reached > tree.m_nodes.Count)
                return $"the tree names node {i} of {tree.m_nodes.Count}";
            var node = tree.m_nodes[i];
            var (min, max) = Decompress(node.m_xyz_0, node.m_xyz_1, node.m_xyz_2, pmin, pmax);
            int data = ((node.m_hiData & 0x7F) << 8) | node.m_loData;
            if ((node.m_hiData & 0x80) != 0)
            {
                stack.Push((i + data * 2, min, max));
                stack.Push((i + 1, min, max));
                continue;
            }
            if (data >= seen.Length)
                return $"leaf {i} names section {data} of {seen.Length}";
            seen[data]++;
            var s = tree.m_sections[data];
            if (s.m_leafIndex != i)
                return $"section {data} says its leaf is {s.m_leafIndex}, not {i}";
            if (Vector3.Min(V(s.m_domain.m_min), min - t) != min - t || Vector3.Max(V(s.m_domain.m_max), max + t) != max + t)
                return $"section {data} sticks out of its leaf";
        }
        if (seen.Any(c => c != 1))
            return $"{seen.Count(c => c == 0)} sections in no leaf, {seen.Count(c => c > 1)} in more than one";

        var primitives = Primitives(tree, out _, out _);
        int at = 0;
        for (int si = 0; si < tree.m_sections.Count; si++)
        {
            var section = tree.m_sections[si];
            int count = (int)(section.m_primitives.m_data & 0xFF);
            var leaves = LeafBoxes(section);
            if (leaves.Count != count || section.m_nodes.Count != count * 2 - 1)
                return $"section {si}: {leaves.Count} leaves and {section.m_nodes.Count} nodes for {count} primitives";
            for (int p = 0; p < count; p++, at++)
            {
                if (!leaves.TryGetValue(p, out var box))
                    return $"section {si}: primitive {p} is in no leaf";
                var prim = primitives[at];
                if (Vector3.Min(prim.Min, box.Min - t) != box.Min - t || Vector3.Max(prim.Max, box.Max + t) != box.Max + t)
                    return $"section {si}: primitive {p} sticks out of its leaf";
            }
        }
        return null;
    }
}
