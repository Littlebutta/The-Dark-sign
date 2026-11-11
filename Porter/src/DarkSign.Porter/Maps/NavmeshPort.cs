using System.Numerics;
using HKLib.hk2018;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Turns a Dark Souls III navmesh (Havok AI 2014 packfile, one per entry of a map's .nvmhktbnd) into an Elden Ring one
/// (Havok AI 2018 tagfile). Enemies walk and path on the navmesh, so a map without one has enemies that run straight
/// at the player, into walls and off ledges.
///
/// What the two games keep in a navmesh file differs (Smithbox's Navigation tool surveyed every file of both):
/// - DS3: the hkaiNavMesh in its own space (the map's navmesh list, the .nva, places it with a position, rotation and
///   scale), a bare hkcdStaticAabbTree over its faces, a cluster graph (hkaiDirectedGraphExplicitCost), and jump and
///   drop links as hkaiUserEdgeSetupArray, which the game turns into edges when it loads the mesh.
/// - Elden Ring: the hkaiNavMesh in map space (list entries sit at the identity), and an
///   hkaiStaticTreeNavMeshQueryMediator holding the face tree. No cluster graph; links are user edges in the mesh.
///
/// So the mesh keeps its faces and edges (the list's connections between navmeshes name them by index), its vertices
/// are moved into map space by the list entry's placement, the face tree is built anew over the moved faces
/// (<see cref="NavFaceTree"/>) and put in a mediator, and the cluster graph, the streaming sets and the link setups
/// are left out. Face data is cleared unless asked for: Elden Ring reads its bits as impassable, gated, lava and so
/// on (Smithbox: CS::CSChrCostModifier), and what DS3's bits mean to Elden Ring is not known.
/// </summary>
public static class NavmeshPort
{
    /// <param name="FaceData">How many faces carry each face data value in the DS3 file (before any clearing).</param>
    /// <param name="Mesh">The converted mesh as read back from <paramref name="Bytes"/>, in map space.</param>
    public sealed record Result(byte[] Bytes, int Faces, int Edges, int Vertices, IReadOnlyDictionary<int, int> FaceData,
        int LinkSetups, IReadOnlyList<string> Notes, hkaiNavMesh Mesh);

    /// <summary>
    /// How many ints of data each face and edge of the host map's own navmeshes carry. The game reads a face's data
    /// to price and gate it, so a ported mesh carries at least as many (zeros), and never fewer than one per face.
    /// </summary>
    public sealed record Layout(int FaceDataStriding, int EdgeDataStriding)
    {
        public static readonly Layout Minimum = new(1, 0);

        public static Layout? Of(hkaiNavMesh? mesh) => mesh is null ? null : new Layout(mesh.m_faceDataStriding, mesh.m_edgeDataStriding);
    }

    public static Ds3.hkRootLevelContainer ReadDs3(byte[] hkx) => CollisionPort.ReadDs3(hkx);

    /// <param name="placement">Where the navmesh list puts the mesh in the ported map; null leaves it where it is.</param>
    /// <param name="keepFaceData">Keep DS3's face data values instead of clearing them.</param>
    /// <param name="layout">The host map's face and edge data layout; null for the minimum.</param>
    public static Result Convert(byte[] ds3Hkx, bool xml = true, Matrix4x4? placement = null, bool keepFaceData = false, Layout? layout = null)
    {
        var notes = new List<string>();
        var ds3 = ReadDs3(ds3Hkx);
        var source = ds3.m_namedVariants?.Select(v => v.m_variant).OfType<Ds3.hkaiNavMesh>().FirstOrDefault()
                     ?? throw new InvalidDataException("no hkaiNavMesh in the DS3 navmesh file");
        var faceData = new Dictionary<int, int>();
        int striding = Math.Max(1, source.m_faceDataStriding);
        for (int f = 0; f < (source.m_faces?.Count ?? 0); f++)
        {
            int value = source.m_faceData is { } data && f * striding < data.Count ? data[f * striding] : 0;
            faceData[value] = faceData.GetValueOrDefault(value) + 1;
        }
        int links = ds3.m_namedVariants?.Select(v => v.m_variant).OfType<Ds3.hkaiUserEdgeSetupArray>().Sum(a => a.m_edgeSetups?.Count ?? 0) ?? 0;

        var er = Convert(ds3, placement ?? Matrix4x4.Identity, keepFaceData, notes, layout);
        var bytes = CollisionPort.Write(er, xml);

        // Read back and compare, so a serializer problem shows up in the porter and not in the game.
        var reread = CollisionPort.Read(bytes);
        var before = Count(ds3);
        var after = Count(reread);
        if (before.Faces != after.Faces || before.Edges != after.Edges || before.Vertices != after.Vertices)
            throw new InvalidDataException($"navmesh changed in conversion: {before} -> {after}");
        var mesh = reread.m_namedVariants.Select(v => v.m_variant).OfType<hkaiNavMesh>().First();
        var mediator = reread.m_namedVariants.Select(v => v.m_variant).OfType<hkaiStaticTreeNavMeshQueryMediator>().FirstOrDefault();
        if (NavFaceTree.Check(mediator?.m_tree, mesh) is { } problem)
            throw new InvalidDataException($"face tree: {problem}");
        return new Result(bytes, after.Faces, after.Edges, after.Vertices, faceData, links, notes, mesh);
    }

    public static hkRootLevelContainer Convert(Ds3.hkRootLevelContainer ds3, Matrix4x4 placement, bool keepFaceData, List<string> notes, Layout? layout = null)
    {
        var source = ds3.m_namedVariants?.Select(v => v.m_variant).OfType<Ds3.hkaiNavMesh>().FirstOrDefault()
                     ?? throw new InvalidDataException("no hkaiNavMesh in the DS3 navmesh file");
        if ((ds3.m_namedVariants?.Count(v => v.m_variant is Ds3.hkaiNavMesh) ?? 0) > 1)
            notes.Add("more than one hkaiNavMesh in the file; the first is used");
        var mesh = ConvertMesh(source);
        if ((source.m_streamingSets?.Count ?? 0) > 0)
            notes.Add($"{source.m_streamingSets!.Count} streaming sets left out (the navmesh list holds the connections)");
        mesh.m_streamingSets.Clear();
        if (!keepFaceData)
            for (int i = 0; i < mesh.m_faceData.Count; i++)
                mesh.m_faceData[i] = 0;
        layout ??= Layout.Minimum;
        (mesh.m_faceData, mesh.m_faceDataStriding) = Restride(mesh.m_faceData, mesh.m_faceDataStriding, Math.Max(1, layout.FaceDataStriding), mesh.m_faces.Count);
        (mesh.m_edgeData, mesh.m_edgeDataStriding) = Restride(mesh.m_edgeData, mesh.m_edgeDataStriding, layout.EdgeDataStriding, mesh.m_edges.Count);
        Place(mesh, placement, notes);

        var root = new hkRootLevelContainer();
        root.m_namedVariants.Add(new hkRootLevelContainer.NamedVariant { m_name = "hkaiNavMesh", m_className = "hkaiNavMesh", m_variant = mesh });
        // A file without faces holds only its mesh, as the game's own empty files do.
        if (NavFaceTree.Build(mesh) is { } tree)
            root.m_namedVariants.Add(new hkRootLevelContainer.NamedVariant
            {
                m_name = "hkaiStaticTreeNavMeshQueryMediator",
                m_className = "hkaiStaticTreeNavMeshQueryMediator",
                m_variant = new hkaiStaticTreeNavMeshQueryMediator { m_navMesh = mesh, m_tree = tree },
            });
        return root;
    }

    /// <summary>Per-item data widened to at least <paramref name="atLeast"/> ints an item, the new ints zero.</summary>
    private static (List<int> Data, int Striding) Restride(List<int> data, int striding, int atLeast, int items)
    {
        if (striding >= atLeast && data.Count == items * striding)
            return (data, striding);
        int wide = Math.Max(striding, atLeast);
        var result = new List<int>(new int[items * wide]);
        for (int i = 0; i < items; i++)
            for (int k = 0; k < striding && i * striding + k < data.Count; k++)
                result[i * wide + k] = data[i * striding + k];
        return (result, wide);
    }

    /// <summary>Moves a mesh's vertices by a placement and sets its bounds from them.</summary>
    private static void Place(hkaiNavMesh mesh, Matrix4x4 placement, List<string> notes)
    {
        if (!placement.IsIdentity)
        {
            if (placement.GetDeterminant() < 0)
                notes.Add("the placement mirrors the mesh; its faces now wind the other way");
            for (int i = 0; i < mesh.m_vertices.Count; i++)
            {
                var v = mesh.m_vertices[i];
                mesh.m_vertices[i] = new Vector4(Vector3.Transform(new Vector3(v.X, v.Y, v.Z), placement), v.W);
            }
        }
        if (mesh.m_vertices.Count == 0)
            return;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in mesh.m_vertices)
        {
            min = Vector3.Min(min, new Vector3(v.X, v.Y, v.Z));
            max = Vector3.Max(max, new Vector3(v.X, v.Y, v.Z));
        }
        mesh.m_aabb = new hkAabb { m_min = new Vector4(min, mesh.m_aabb.m_min.W), m_max = new Vector4(max, mesh.m_aabb.m_max.W) };
    }

    private static float Half(short value) => BitConverter.Int32BitsToSingle(value << 16);

    private static hkAabb Aabb(Ds3.hkAabb? a) => a is null ? new hkAabb() : new hkAabb { m_min = a.m_min, m_max = a.m_max };

    private static hkaiNavMesh ConvertMesh(Ds3.hkaiNavMesh m)
    {
        var er = new hkaiNavMesh
        {
            m_faceDataStriding = m.m_faceDataStriding,
            m_edgeDataStriding = m.m_edgeDataStriding,
            m_flags = m.m_flags,
            m_aabb = Aabb(m.m_aabb),
            m_erosionRadius = m.m_erosionRadius,
            m_userData = m.m_userData,
        };
        foreach (var f in m.m_faces ?? [])
            er.m_faces.Add(new hkaiNavMesh.Face
            {
                m_startEdgeIndex = f.m_startEdgeIndex,
                m_startUserEdgeIndex = f.m_startUserEdgeIndex,
                m_numEdges = f.m_numEdges,
                m_numUserEdges = f.m_numUserEdges,
                m_clusterIndex = f.m_clusterIndex,
                m_padding = f.m_padding,
            });
        foreach (var e in m.m_edges ?? [])
            er.m_edges.Add(new hkaiNavMesh.Edge
            {
                m_a = e.m_a,
                m_b = e.m_b,
                m_oppositeEdge = e.m_oppositeEdge,
                m_oppositeFace = e.m_oppositeFace,
                m_flags = (hkaiNavMesh.EdgeFlagBits)e.m_flags,
                m_userEdgeCost = Half(e.m_userEdgeCost),
            });
        er.m_vertices.AddRange(m.m_vertices ?? []);
        er.m_faceData.AddRange(m.m_faceData ?? []);
        er.m_edgeData.AddRange(m.m_edgeData ?? []);
        foreach (var set in m.m_streamingSets ?? [])
            er.m_streamingSets.Add(StreamingSet(set));
        return er;
    }

    private static hkaiAnnotatedStreamingSet StreamingSet(Ds3.hkaiStreamingSet s)
    {
        var er = new hkaiStreamingSet { m_aSectionUid = s.m_thisUid, m_bSectionUid = s.m_oppositeUid };
        foreach (var c in s.m_meshConnections ?? [])
            er.m_meshConnections.Add(new hkaiStreamingSet.NavMeshConnection
            {
                m_aFaceEdgeIndex = new hkaiFaceEdgeIndexPair { m_faceIndex = c.m_faceIndex, m_edgeIndex = c.m_edgeIndex },
                m_bFaceEdgeIndex = new hkaiFaceEdgeIndexPair { m_faceIndex = c.m_oppositeFaceIndex, m_edgeIndex = c.m_oppositeEdgeIndex },
            });
        foreach (var c in s.m_graphConnections ?? [])
            er.m_graphConnections.Add(new hkaiStreamingSet.GraphConnection
            {
                m_aNodeIndex = c.m_nodeIndex,
                m_bNodeIndex = c.m_oppositeNodeIndex,
                m_aEdgeData = c.m_edgeData,
                m_bEdgeData = c.m_edgeData,
                m_aEdgeCost = Half(c.m_edgeCost),
                m_bEdgeCost = Half(c.m_edgeCost),
            });
        foreach (var c in s.m_volumeConnections ?? [])
            er.m_volumeConnections.Add(new hkaiStreamingSet.VolumeConnection { m_aCellIndex = c.m_cellIndex, m_bCellIndex = c.m_oppositeCellIndex });
        return new hkaiAnnotatedStreamingSet { m_side = hkaiAnnotatedStreamingSet.Side.SIDE_A, m_streamingSet = er };
    }

    public readonly record struct Counts(int Faces, int Edges, int Vertices, int Nodes, int TreeNodes);

    public static Counts Count(Ds3.hkRootLevelContainer root)
    {
        var mesh = root.m_namedVariants?.Select(v => v.m_variant).OfType<Ds3.hkaiNavMesh>().FirstOrDefault();
        var graph = root.m_namedVariants?.Select(v => v.m_variant).OfType<Ds3.hkaiDirectedGraphExplicitCost>().FirstOrDefault();
        var mediator = root.m_namedVariants?.Select(v => v.m_variant).OfType<Ds3.hkaiStaticTreeNavMeshQueryMediator>().FirstOrDefault();
        return new Counts(mesh?.m_faces?.Count ?? 0, mesh?.m_edges?.Count ?? 0, mesh?.m_vertices?.Count ?? 0,
            graph?.m_nodes?.Count ?? 0, mediator?.m_tree?.m_treePtr?.m_nodes?.Count ?? 0);
    }

    public static Counts Count(hkRootLevelContainer root)
    {
        var mesh = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaiNavMesh>().FirstOrDefault();
        var graph = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaiDirectedGraphExplicitCost>().FirstOrDefault();
        var mediator = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaiStaticTreeNavMeshQueryMediator>().FirstOrDefault();
        return new Counts(mesh?.m_faces.Count ?? 0, mesh?.m_edges.Count ?? 0, mesh?.m_vertices.Count ?? 0,
            graph?.m_nodes.Count ?? 0, mediator?.m_tree?.m_treePtr?.m_tree.m_nodes.Count ?? 0);
    }
}
