using HKLib.hk2018;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Turns a Dark Souls III navmesh (Havok AI 2014 packfile, one per entry of a map's .nvmhktbnd) into an Elden Ring one
/// (Havok AI 2018 tagfile). Enemies walk and path on the navmesh, so a map without one has enemies that stand still.
///
/// The three objects a FromSoftware navmesh file holds keep their layout between the two Havok versions:
/// hkaiNavMesh (faces, edges, vertices), hkaiDirectedGraphExplicitCost (the coarse cluster graph used for long
/// paths) and hkaiStaticTreeNavMeshQueryMediator (an AABB tree over the faces, 6-byte compressed nodes in both).
/// Costs stored as hkHalf16 in 2014 become floats, and 2014's per-mesh streaming sets ("this UID / opposite UID")
/// become 2018's annotated streaming sets (side A = this mesh). Nothing is re-generated, so paths stay DS3's.
/// </summary>
public static class NavmeshPort
{
    public sealed record Result(byte[] Bytes, int Faces, int Edges, int Vertices, int GraphNodes, IReadOnlyList<string> Notes);

    public static Ds3.hkRootLevelContainer ReadDs3(byte[] hkx) => CollisionPort.ReadDs3(hkx);

    public static Result Convert(byte[] ds3Hkx, bool xml = true)
    {
        var notes = new List<string>();
        var ds3 = ReadDs3(ds3Hkx);
        var er = Convert(ds3, notes);
        var bytes = CollisionPort.Write(er, xml);

        // Read back and compare the counts, so a serializer problem shows up in the porter and not in the game.
        var reread = CollisionPort.Read(bytes);
        var before = Count(ds3);
        var after = Count(reread);
        if (before != after)
            throw new InvalidDataException($"navmesh changed in conversion: {before} -> {after}");
        return new Result(bytes, after.Faces, after.Edges, after.Vertices, after.Nodes, notes);
    }

    public static hkRootLevelContainer Convert(Ds3.hkRootLevelContainer ds3, List<string> notes)
    {
        var root = new hkRootLevelContainer();
        var meshes = new Dictionary<Ds3.hkaiNavMesh, hkaiNavMesh>(ReferenceEqualityComparer.Instance);
        hkaiNavMesh Mesh(Ds3.hkaiNavMesh m)
        {
            if (!meshes.TryGetValue(m, out var converted))
                meshes[m] = converted = ConvertMesh(m);
            return converted;
        }

        foreach (var variant in ds3.m_namedVariants ?? [])
        {
            hkReferencedObject? converted = variant.m_variant switch
            {
                Ds3.hkaiNavMesh mesh => Mesh(mesh),
                Ds3.hkaiDirectedGraphExplicitCost graph => ConvertGraph(graph),
                Ds3.hkaiStaticTreeNavMeshQueryMediator mediator => new hkaiStaticTreeNavMeshQueryMediator
                {
                    m_navMesh = mediator.m_navMesh is null ? null : Mesh(mediator.m_navMesh),
                    m_tree = ConvertTree(mediator.m_tree),
                },
                _ => null,
            };
            if (converted is null)
            {
                notes.Add($"skipped root object {variant.m_className}");
                continue;
            }
            root.m_namedVariants.Add(new hkRootLevelContainer.NamedVariant
            {
                m_name = variant.m_name ?? variant.m_className,
                m_className = variant.m_className,
                m_variant = converted,
            });
        }
        if (!root.m_namedVariants.Any(v => v.m_variant is hkaiNavMesh))
            throw new InvalidDataException("no hkaiNavMesh in the DS3 navmesh file");
        return root;
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

    private static hkaiDirectedGraphExplicitCost ConvertGraph(Ds3.hkaiDirectedGraphExplicitCost g)
    {
        var er = new hkaiDirectedGraphExplicitCost { m_nodeDataStriding = g.m_nodeDataStriding, m_edgeDataStriding = g.m_edgeDataStriding };
        foreach (var n in g.m_nodes ?? [])
            er.m_nodes.Add(new hkaiDirectedGraphExplicitCost.Node { m_startEdgeIndex = n.m_startEdgeIndex, m_numEdges = n.m_numEdges });
        foreach (var e in g.m_edges ?? [])
            er.m_edges.Add(new hkaiDirectedGraphExplicitCost.Edge
            {
                m_cost = Half(e.m_cost),
                m_flags = (hkaiDirectedGraphExplicitCost.EdgeBits)e.m_flags,
                m_target = e.m_target,
            });
        er.m_positions.AddRange(g.m_positions ?? []);
        er.m_nodeData.AddRange(g.m_nodeData ?? []);
        er.m_edgeData.AddRange(g.m_edgeData ?? []);
        foreach (var set in g.m_streamingSets ?? [])
            er.m_streamingSets.Add(StreamingSet(set));
        return er;
    }

    private static hkcdStaticAabbTree? ConvertTree(Ds3.hkcdStaticAabbTree? tree)
    {
        if (tree?.m_treePtr is not { } storage)
            return null;
        var impl = new hkcdStaticAabbTree.Impl();
        impl.m_tree.m_domain = Aabb(storage.m_domain);
        foreach (var n in storage.m_nodes ?? [])
        {
            var node = new HKLib.hk2018.hkcdCompressedAabbCodecs.Aabb6BytesCodec { m_hiData = n.m_hiData, m_loData = n.m_loData };
            node.m_xyz[0] = n.m_xyz_0;
            node.m_xyz[1] = n.m_xyz_1;
            node.m_xyz[2] = n.m_xyz_2;
            impl.m_tree.m_nodes.Add(node);
        }
        return new hkcdStaticAabbTree { m_treePtr = impl };
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
