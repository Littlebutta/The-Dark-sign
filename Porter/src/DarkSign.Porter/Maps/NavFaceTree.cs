using System.Numerics;
using HKLib.hk2018;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The face tree of an Elden Ring navmesh file: the AABB tree its hkaiStaticTreeNavMeshQueryMediator holds, which the
/// game finds faces through (the face under a character, the faces along a path). Dark Souls III keeps a tree beside
/// its mesh instead, in the mesh's own space; the port moves every mesh into map space, so its tree is built anew.
///
/// The codec and the builder follow Smithbox's Navigation tool (vawser and contributors, GPL-3.0,
/// src/Smithbox.Program/Editors/Map Editor/Tools/Navigation/NavigationTree.cs), which checked its trees against every
/// navmesh of the game:
/// - a node's box is stored relative to its parent's: one byte per axis, high nibble h and low nibble l, with
///   min = parent min + h² / 226 × extent and max = parent max − l² / 226 × extent. The root is all zeros (the domain);
/// - an inner node has bit 0x80 of m_hiData set; its left child is the next node, its right child is
///   ((m_hiData &amp; 0x7F) &lt;&lt; 16 | m_loData) × 2 nodes further on;
/// - a leaf holds its face index in the same 23 bits.
/// </summary>
public static class NavFaceTree
{
    private const float Scale = 226f;

    private sealed class Node
    {
        public Vector3 Min, Max;
        public Node? Left, Right;
        public int Face = -1;
    }

    /// <summary>The corners of a face, in order.</summary>
    public static IEnumerable<Vector3> Corners(hkaiNavMesh mesh, int face)
    {
        var f = mesh.m_faces[face];
        for (int e = f.m_startEdgeIndex; e < f.m_startEdgeIndex + f.m_numEdges; e++)
        {
            var v = mesh.m_vertices[mesh.m_edges[e].m_a];
            yield return new Vector3(v.X, v.Y, v.Z);
        }
    }

    public static (Vector3 Min, Vector3 Max)[] FaceBounds(hkaiNavMesh mesh)
    {
        var bounds = new (Vector3, Vector3)[mesh.m_faces.Count];
        for (int f = 0; f < bounds.Length; f++)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var c in Corners(mesh, f))
            {
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }
            bounds[f] = (min, max);
        }
        return bounds;
    }

    /// <summary>
    /// A tree with one face per leaf, split at the median of the longest axis of the face centres; every box is
    /// rounded outwards, so it always holds its faces. Null for a mesh without faces (such files have no mediator).
    /// </summary>
    public static hkcdStaticAabbTree? Build(hkaiNavMesh mesh)
    {
        var bounds = FaceBounds(mesh);
        if (bounds.Length == 0)
            return null;
        var faces = Enumerable.Range(0, bounds.Length).ToArray();
        var root = Split(bounds, faces, 0, faces.Length);

        var impl = new hkcdStaticAabbTree.Impl();
        impl.m_tree.m_domain = new hkAabb { m_min = new Vector4(root.Min, 1), m_max = new Vector4(root.Max, 1) };
        Encode(impl.m_tree.m_nodes, root, root.Min, root.Max, isRoot: true);
        return new hkcdStaticAabbTree { m_treePtr = impl };
    }

    private static Node Split((Vector3 Min, Vector3 Max)[] bounds, int[] faces, int start, int count)
    {
        var node = new Node { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        for (int i = start; i < start + count; i++)
        {
            node.Min = Vector3.Min(node.Min, bounds[faces[i]].Min);
            node.Max = Vector3.Max(node.Max, bounds[faces[i]].Max);
        }
        if (count == 1)
        {
            node.Face = faces[start];
            return node;
        }

        var cmin = new Vector3(float.MaxValue);
        var cmax = new Vector3(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            var c = (bounds[faces[i]].Min + bounds[faces[i]].Max) / 2;
            cmin = Vector3.Min(cmin, c);
            cmax = Vector3.Max(cmax, c);
        }
        var extent = cmax - cmin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        float Centre(int f) => axis switch
        {
            0 => bounds[f].Min.X + bounds[f].Max.X,
            1 => bounds[f].Min.Y + bounds[f].Max.Y,
            _ => bounds[f].Min.Z + bounds[f].Max.Z,
        };
        // Ties are broken by face index, so the same mesh always gives the same tree.
        Array.Sort(faces, start, count, Comparer<int>.Create((a, b) =>
        {
            int byCentre = Centre(a).CompareTo(Centre(b));
            return byCentre != 0 ? byCentre : a.CompareTo(b);
        }));

        int half = count / 2;
        node.Left = Split(bounds, faces, start, half);
        node.Right = Split(bounds, faces, start + half, count - half);
        return node;
    }

    /// <summary>One axis of a box within its parent's, rounded outwards: min down, max up.</summary>
    private static byte CompressAxis(float min, float max, float parentMin, float parentMax)
    {
        float extent = parentMax - parentMin;
        if (extent <= 0)
            return 0;
        float norm = Scale / extent;
        int h = (int)MathF.Floor(MathF.Sqrt(MathF.Max((min - parentMin) * norm, 0)));
        int l = (int)MathF.Floor(MathF.Sqrt(MathF.Max((parentMax - max) * norm, 0)));
        // The square root can land a hair above the true value; step back until the stored box holds the real one.
        while (h > 0 && parentMin + h * h / Scale * extent > min)
            h--;
        while (l > 0 && parentMax - l * l / Scale * extent < max)
            l--;
        return (byte)((Math.Min(15, h) << 4) | Math.Min(15, l));
    }

    private static (Vector3 Min, Vector3 Max) Decompress(byte x, byte y, byte z, Vector3 parentMin, Vector3 parentMax)
    {
        var extent = parentMax - parentMin;
        static float Lo(byte b) => (b >> 4) * (b >> 4) / Scale;
        static float Hi(byte b) => (b & 0xF) * (b & 0xF) / Scale;
        return (parentMin + new Vector3(Lo(x), Lo(y), Lo(z)) * extent, parentMax - new Vector3(Hi(x), Hi(y), Hi(z)) * extent);
    }

    private static void Encode(List<HKLib.hk2018.hkcdCompressedAabbCodecs.Aabb6BytesCodec> nodes, Node node, Vector3 parentMin, Vector3 parentMax, bool isRoot)
    {
        int index = nodes.Count;
        var codec = new HKLib.hk2018.hkcdCompressedAabbCodecs.Aabb6BytesCodec();
        nodes.Add(codec);
        if (!isRoot)
        {
            codec.m_xyz[0] = CompressAxis(node.Min.X, node.Max.X, parentMin.X, parentMax.X);
            codec.m_xyz[1] = CompressAxis(node.Min.Y, node.Max.Y, parentMin.Y, parentMax.Y);
            codec.m_xyz[2] = CompressAxis(node.Min.Z, node.Max.Z, parentMin.Z, parentMax.Z);
        }
        // The children are encoded within the box as stored, not as built.
        var (min, max) = Decompress(codec.m_xyz[0], codec.m_xyz[1], codec.m_xyz[2], parentMin, parentMax);

        if (node.Face >= 0)
        {
            codec.m_loData = (ushort)(node.Face & 0xFFFF);
            codec.m_hiData = (byte)((node.Face >> 16) & 0x7F);
            return;
        }

        Encode(nodes, node.Left!, min, max, false);
        // Stored halved: the left subtree, a binary tree with one face per leaf, has an odd number of nodes (2n - 1),
        // so the right child always sits an even number of nodes on.
        int offset = (nodes.Count - index) / 2;
        codec.m_loData = (ushort)(offset & 0xFFFF);
        codec.m_hiData = (byte)(((offset >> 16) & 0x7F) | 0x80);
        Encode(nodes, node.Right!, min, max, false);
    }

    /// <summary>
    /// Whether a tree holds every face of a mesh exactly once, each inside its leaf's box. Returns the first problem, or
    /// null. The porter runs it on every tree it builds, so a bad tree stops the navmesh there and not in the game.
    /// </summary>
    public static string? Check(hkcdStaticAabbTree? tree, hkaiNavMesh mesh, float tolerance = 1e-3f)
    {
        var bounds = FaceBounds(mesh);
        var nodes = tree?.m_treePtr?.m_tree.m_nodes;
        if (nodes is null || nodes.Count == 0)
            return bounds.Length == 0 ? null : "no tree";
        if (nodes.Count != bounds.Length * 2 - 1)
            return $"{nodes.Count} nodes for {bounds.Length} faces";

        var domain = tree!.m_treePtr!.m_tree.m_domain;
        var seen = new int[bounds.Length];
        int reached = 0;
        var stack = new Stack<(int Index, Vector3 Min, Vector3 Max)>();
        stack.Push((0, new Vector3(domain.m_min.X, domain.m_min.Y, domain.m_min.Z), new Vector3(domain.m_max.X, domain.m_max.Y, domain.m_max.Z)));
        var t = new Vector3(tolerance);
        while (stack.Count > 0)
        {
            var (i, pmin, pmax) = stack.Pop();
            if (i < 0 || i >= nodes.Count)
                return $"node {i} of {nodes.Count} named";
            reached++;
            var codec = nodes[i];
            var (min, max) = Decompress(codec.m_xyz[0], codec.m_xyz[1], codec.m_xyz[2], pmin, pmax);
            int data = ((codec.m_hiData & 0x7F) << 16) | codec.m_loData;
            if ((codec.m_hiData & 0x80) != 0)
            {
                stack.Push((i + data * 2, min, max));
                stack.Push((i + 1, min, max));
                continue;
            }
            if (data >= bounds.Length)
                return $"leaf {i} names face {data} of {bounds.Length}";
            seen[data]++;
            var (fmin, fmax) = bounds[data];
            if (Vector3.Min(fmin, min - t) != min - t || Vector3.Max(fmax, max + t) != max + t)
                return $"face {data} sticks out of its leaf {i}";
        }
        if (reached != nodes.Count)
            return $"{reached} of {nodes.Count} nodes reached";
        int missing = seen.Count(s => s == 0), twice = seen.Count(s => s > 1);
        return missing > 0 || twice > 0 ? $"{missing} faces in no leaf, {twice} in more than one" : null;
    }
}
