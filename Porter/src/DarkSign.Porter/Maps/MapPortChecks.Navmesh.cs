using System.Numerics;
using SoulsFormats;
using Ds3 = HKX2;
using Nva = SmithboxFormats.NVA;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Offline checks for what enemies find their way on (2026-11-11 playtest of 1.0.299: enemies don't pathfind on the
/// DS3 maps): navmesh files laid out as Elden Ring's (mesh in map space, face tree in a query mediator), and a navmesh
/// list that names them, in both of Elden Ring's sets. See <see cref="MapNavmesh"/>, <see cref="NavmeshList"/>.
/// </summary>
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> NavmeshChecks()
    {
        yield return ("navmesh: a DS3 file becomes an Elden Ring one: faces and edges kept, face tree in a mediator, no cluster graph (XML)", () => NavmeshFile(xml: true));
        yield return ("navmesh: a DS3 file becomes an Elden Ring one: faces and edges kept, face tree in a mediator, no cluster graph (binary)", () => NavmeshFile(xml: false));
        yield return ("navmesh: the list entry's turn and move are applied to the mesh, and the tree holds the moved faces", NavmeshPlaced);
        yield return ("navmesh: face data is cleared by default and kept when asked", NavmeshFaceData);
        yield return ("navmesh: a face tree over a large mesh holds every face once, inside its leaf", NavmeshTree);
        yield return ("navmesh list: the Elden Ring and DS3 layouts write and read back", NavmeshListRoundTrip);
        yield return ("navmesh list: DS3 entries get Elden Ring's NameIDs, an o set, their gates and renumbered connections", NavmeshListConvert);
        yield return ("navmesh list: connections and gates that lead to a navmesh without faces are left out", NavmeshListMissing);
        yield return ("navmesh: the binder holds every listed navmesh as an n and an o file, named by ModelID, with the host's compendium", NavmeshBinder);
        yield return ("navmesh: without a DS3 list the files are converted as before and no list is made", NavmeshBinderNoList);
        yield return ("navmesh: port-maps knows the fallback arguments", NavmeshArguments);
    }

    /// <summary>
    /// A DS3 navmesh file as the game's are (Smithbox's survey): the mesh, a cluster graph, a bare AABB tree beside the
    /// mesh, and jump/drop link setups. Two triangles over a 10 m square in the mesh's own space.
    /// </summary>
    internal static byte[] SyntheticDs3Navmesh(bool withFaces = true)
    {
        Ds3.hkaiNavMeshEdge Edge(int a, int b, uint oppositeEdge = 0xFFFFFFFF, uint oppositeFace = 0xFFFFFFFF) => new()
        {
            m_a = a, m_b = b, m_oppositeEdge = oppositeEdge, m_oppositeFace = oppositeFace, m_flags = 0,
            m_userEdgeCost = (short)(BitConverter.SingleToInt32Bits(1.5f) >> 16),
        };
        var mesh = new Ds3.hkaiNavMesh
        {
            m_faces = !withFaces ? [] :
            [
                new Ds3.hkaiNavMeshFace { m_startEdgeIndex = 0, m_startUserEdgeIndex = -1, m_numEdges = 3, m_clusterIndex = 0 },
                new Ds3.hkaiNavMeshFace { m_startEdgeIndex = 3, m_startUserEdgeIndex = -1, m_numEdges = 3, m_clusterIndex = 0 },
            ],
            m_edges = !withFaces ? [] : [Edge(0, 1), Edge(1, 2, 3, 1), Edge(2, 0), Edge(2, 1, 1, 0), Edge(1, 3), Edge(3, 2)],
            m_vertices = !withFaces ? [] : [new(0, 0, 0, 0), new(10, 0, 0, 0), new(0, 0, 10, 0), new(10, 0, 10, 0)],
            m_streamingSets = [],
            m_faceData = !withFaces ? [] : [7, 0x10000],
            m_edgeData = [],
            m_faceDataStriding = 1,
            m_edgeDataStriding = 0,
            m_aabb = new Ds3.hkAabb { m_min = new(0, 0, 0, 0), m_max = new(10, 0, 10, 0) },
            m_erosionRadius = 0.3f,
        };
        var graph = new Ds3.hkaiDirectedGraphExplicitCost
        {
            m_positions = [new(5, 0, 5, 0)],
            m_nodes = [new Ds3.hkaiDirectedGraphExplicitCostNode { m_startEdgeIndex = 0, m_numEdges = 0 }],
            m_edges = [],
            m_nodeData = [],
            m_edgeData = [],
            m_streamingSets = [],
        };
        var tree = new Ds3.hkcdStaticAabbTree
        {
            m_treePtr = new Ds3.hkcdStaticTreeDefaultTreeStorage6
            {
                m_domain = new Ds3.hkAabb { m_min = new(0, 0, 0, 0), m_max = new(10, 0, 10, 0) },
                m_nodes =
                [
                    new Ds3.hkcdStaticTreeCodec3Axis6 { m_hiData = 0x80, m_loData = 1 },
                    new Ds3.hkcdStaticTreeCodec3Axis6 { m_hiData = 0, m_loData = 0 },
                    new Ds3.hkcdStaticTreeCodec3Axis6 { m_hiData = 0, m_loData = 1 },
                ],
            },
        };
        var links = new Ds3.hkaiUserEdgeSetupArray
        {
            m_edgeSetups =
            [
                new Ds3.hkaiUserEdgeUtilsUserEdgeSetup
                {
                    m_obbA = new Ds3.hkaiUserEdgeUtilsObb { m_transform = Matrix4x4.Identity, m_halfExtents = new(1, 1, 1, 0) },
                    m_obbB = new Ds3.hkaiUserEdgeUtilsObb { m_transform = Matrix4x4.Identity, m_halfExtents = new(1, 1, 1, 0) },
                    m_worldUpA = new(0, 1, 0, 0), m_worldUpB = new(0, 1, 0, 0),
                },
            ],
        };
        var root = new Ds3.hkRootLevelContainer
        {
            m_namedVariants =
            [
                new Ds3.hkRootLevelContainerNamedVariant { m_name = "hkaiNavMesh", m_className = "hkaiNavMesh", m_variant = mesh },
                new Ds3.hkRootLevelContainerNamedVariant { m_name = "hkaiDirectedGraphExplicitCost", m_className = "hkaiDirectedGraphExplicitCost", m_variant = graph },
                new Ds3.hkRootLevelContainerNamedVariant { m_name = "hkcdStaticAabbTree", m_className = "hkcdStaticAabbTree", m_variant = tree },
                new Ds3.hkRootLevelContainerNamedVariant { m_name = "hkaiUserEdgeSetupArray", m_className = "hkaiUserEdgeSetupArray", m_variant = links },
            ],
        };
        using var stream = new MemoryStream();
        var bw = new BinaryWriterEx(false, stream);
        new Ds3.PackFileSerializer().Serialize(root, bw);
        bw.Finish();
        return stream.ToArray();
    }

    private static void NavmeshFile(bool xml)
    {
        var result = NavmeshPort.Convert(SyntheticDs3Navmesh(), xml);
        Expect(result.Faces == 2 && result.Edges == 6 && result.Vertices == 4, $"counts {result.Faces}/{result.Edges}/{result.Vertices}");
        Expect(result.LinkSetups == 1, $"link setups {result.LinkSetups}");
        var root = CollisionPort.Read(result.Bytes);
        var classes = root.m_namedVariants.Select(v => v.m_className).ToList();
        Expect(classes.SequenceEqual(["hkaiNavMesh", "hkaiStaticTreeNavMeshQueryMediator"]), "root objects: " + string.Join(", ", classes));
        var mesh = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiNavMesh>().Single();
        Expect(Math.Abs(mesh.m_edges[0].m_userEdgeCost - 1.5f) < 1e-3f, $"edge cost {mesh.m_edges[0].m_userEdgeCost}");
        Expect(mesh.m_edges[1].m_oppositeEdge == 3 && mesh.m_edges[1].m_oppositeFace == 1, "edge links");
        Expect(mesh.m_vertices[3] == new Vector4(10, 0, 10, 0), "vertices");
        Expect(mesh.m_streamingSets.Count == 0 && mesh.m_faceDataStriding == 1 && mesh.m_faceData.Count == 2, "streaming sets or face data layout");
        var mediator = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiStaticTreeNavMeshQueryMediator>().Single();
        Expect(mediator.m_navMesh is not null && mediator.m_navMesh.m_faces.Count == 2, "mediator mesh");
        Expect(mediator.m_tree!.m_treePtr!.m_tree.m_nodes.Count == 3, "tree nodes");
        Expect(NavFaceTree.Check(mediator.m_tree, mesh) is null, "tree: " + NavFaceTree.Check(mediator.m_tree, mesh));

        // A file without faces holds only its mesh, as the game's empty files do.
        var empty = CollisionPort.Read(NavmeshPort.Convert(SyntheticDs3Navmesh(withFaces: false), xml).Bytes);
        Expect(empty.m_namedVariants.Select(v => v.m_className).SequenceEqual(["hkaiNavMesh"]), "empty file's root objects");
    }

    private static void NavmeshPlaced()
    {
        // A quarter turn about Y, then a move: (10, 0, 0) -> (0, 0, -10) -> (100, 5, -210).
        var entry = new Nva.Navmesh { Position = new Vector3(100, 5, -200), Rotation = new Vector3(0, MathF.PI / 2, 0) };
        var result = NavmeshPort.Convert(SyntheticDs3Navmesh(), true, NavmeshList.Placement(entry));
        var v = result.Mesh.m_vertices[1];
        Expect(Vector3.Distance(new Vector3(v.X, v.Y, v.Z), new Vector3(100, 5, -210)) < 1e-3f, $"vertex 1 at {v}");
        var box = result.Mesh.m_aabb;
        Expect(Math.Abs(box.m_min.X - 100) < 1e-3f && Math.Abs(box.m_max.X - 110) < 1e-3f && Math.Abs(box.m_min.Z + 210) < 1e-3f && Math.Abs(box.m_max.Z + 200) < 1e-3f,
            $"bounds {box.m_min} .. {box.m_max}");
        var root = CollisionPort.Read(result.Bytes);
        var mediator = root.m_namedVariants.Select(m => m.m_variant).OfType<HKLib.hk2018.hkaiStaticTreeNavMeshQueryMediator>().Single();
        var domain = mediator.m_tree!.m_treePtr!.m_tree.m_domain;
        Expect(Math.Abs(domain.m_min.X - 100) < 1e-3f && Math.Abs(domain.m_max.Z + 200) < 1e-3f, $"tree domain {domain.m_min} .. {domain.m_max}");

        // Scale comes first: a mesh scaled by 2 and then moved.
        var scaled = new Nva.Navmesh { Position = new Vector3(1, 0, 0), Scale = new Vector3(2, 2, 2) };
        var s = NavmeshPort.Convert(SyntheticDs3Navmesh(), true, NavmeshList.Placement(scaled)).Mesh.m_vertices[3];
        Expect(Vector3.Distance(new Vector3(s.X, s.Y, s.Z), new Vector3(21, 0, 20)) < 1e-3f, $"scaled vertex 3 at {s}");
    }

    private static void NavmeshFaceData()
    {
        var cleared = NavmeshPort.Convert(SyntheticDs3Navmesh());
        Expect(cleared.Mesh.m_faceData.SequenceEqual([0, 0]), "cleared: " + string.Join(",", cleared.Mesh.m_faceData));
        Expect(cleared.FaceData.Count == 2 && cleared.FaceData[7] == 1 && cleared.FaceData[0x10000] == 1, "DS3 values reported");
        var kept = NavmeshPort.Convert(SyntheticDs3Navmesh(), true, null, keepFaceData: true);
        Expect(kept.Mesh.m_faceData.SequenceEqual([7, 0x10000]), "kept: " + string.Join(",", kept.Mesh.m_faceData));
        // A host whose meshes carry two ints a face and one an edge: the ported mesh is widened with zeros.
        var wide = NavmeshPort.Convert(SyntheticDs3Navmesh(), true, null, keepFaceData: true, new NavmeshPort.Layout(2, 1)).Mesh;
        Expect(wide.m_faceDataStriding == 2 && wide.m_faceData.SequenceEqual([7, 0, 0x10000, 0]), "widened face data: " + string.Join(",", wide.m_faceData));
        Expect(wide.m_edgeDataStriding == 1 && wide.m_edgeData.Count == 6 && wide.m_edgeData.All(v => v == 0), "widened edge data");
    }

    private static void NavmeshTree()
    {
        // A 30 x 30 grid of quads on a slope, far from the origin, with a few degenerate (zero width) boxes.
        var mesh = new HKLib.hk2018.hkaiNavMesh();
        var random = new Random(7);
        for (int z = 0; z < 30; z++)
            for (int x = 0; x < 30; x++)
            {
                int first = mesh.m_vertices.Count;
                float y = 40 + x * 0.25f + (float)random.NextDouble();
                float size = (x + z) % 11 == 0 ? 0 : 1.5f;
                mesh.m_vertices.AddRange([new(900 + x * 1.5f, y, -700 + z * 1.5f, 1), new(900 + x * 1.5f + size, y, -700 + z * 1.5f, 1),
                    new(900 + x * 1.5f + size, y + 0.1f, -700 + z * 1.5f + 1.5f, 1), new(900 + x * 1.5f, y + 0.1f, -700 + z * 1.5f + 1.5f, 1)]);
                mesh.m_faces.Add(new HKLib.hk2018.hkaiNavMesh.Face { m_startEdgeIndex = mesh.m_edges.Count, m_numEdges = 4, m_startUserEdgeIndex = -1 });
                for (int e = 0; e < 4; e++)
                    mesh.m_edges.Add(new HKLib.hk2018.hkaiNavMesh.Edge { m_a = first + e, m_b = first + (e + 1) % 4, m_oppositeEdge = uint.MaxValue, m_oppositeFace = uint.MaxValue });
            }
        var tree = NavFaceTree.Build(mesh);
        Expect(tree is not null && tree.m_treePtr!.m_tree.m_nodes.Count == 900 * 2 - 1, "node count");
        Expect(NavFaceTree.Check(tree, mesh, 1e-4f) is null, "tree: " + NavFaceTree.Check(tree, mesh, 1e-4f));
        // The check itself must fail on a tree that doesn't fit: move a vertex out of its leaf.
        mesh.m_vertices[0] = new Vector4(0, 0, 0, 1);
        Expect(NavFaceTree.Check(tree, mesh) is not null, "a face outside its leaf goes unnoticed");
        Expect(NavFaceTree.Build(new HKLib.hk2018.hkaiNavMesh()) is null, "an empty mesh has no tree");
    }

    /// <summary>A DS3 list: three navmeshes (the second turned, the third without a file), joined 0-1 and 1-2, gates between 0 and 1.</summary>
    private static Nva SyntheticDs3List()
    {
        var list = new Nva { Version = Nva.NVAVersion.DarkSouls3, Entries11 = null, Entries9 = null, Entries10 = null, Entries13 = null, HasSection12 = false };
        list.Navmeshes.Version = 2;
        Nva.Navmesh Entry(int model, Vector3 position, float turn = 0) => new()
        {
            NameID = NavmeshList.Ds3NameId(Ds3Id, model), ModelID = model, Position = position, PositionW = 1, Rotation = new Vector3(0, turn, 0),
            RotationW = 0, Scale = Vector3.One, ScaleW = 0, FaceCount = 2, Unk44 = 0, Unk50 = 0,
        };
        var a = Entry(1, new Vector3(0, 0, 0));
        var b = Entry(200, new Vector3(20, 0, 0), MathF.PI / 2);
        var c = Entry(300, new Vector3(0, 0, 50));
        // Gate nodes are in map space: one on the first mesh's x = 10 edge, its partner at the same point on the second.
        a.GateNodes.Add(new Nva.GateNode { Position = new Vector3(10, 0, 5), ConnectedNavmeshIndex = 1, NodeSubID = 0, NeighbourGateNodeCosts = Costs(0) });
        b.GateNodes.Add(new Nva.GateNode { Position = new Vector3(20, 0, -5), ConnectedNavmeshIndex = 0, NodeSubID = 0, NeighbourGateNodeCosts = Costs(0) });
        list.Navmeshes.AddRange([a, b, c]);
        var ab = new Nva.Connector { MainNameID = a.NameID, TargetNameID = b.NameID };
        ab.NavMeshConnections.Add(new Nva.NavMeshConnection { FaceIndex = 1, EdgeIndex = 4, OppositeFaceIndex = 0, OppositeEdgeIndex = 2 });
        ab.NavMeshConnections.Add(new Nva.NavMeshConnection { FaceIndex = 5, EdgeIndex = 4, OppositeFaceIndex = 0, OppositeEdgeIndex = 2 });
        ab.GraphConnections.Add(new Nva.GraphConnection { NodeIndex = 0, OppositeNodeIndex = 0 });
        var bc = new Nva.Connector { MainNameID = b.NameID, TargetNameID = c.NameID };
        bc.NavMeshConnections.Add(new Nva.NavMeshConnection { FaceIndex = 0, EdgeIndex = 0, OppositeFaceIndex = 0, OppositeEdgeIndex = 0 });
        list.Connectors.AddRange([ab, bc]);
        list.NodeBanks.Add(new Nva.NodeBank { BankID = 0, EntityID = -1, Faces = [new Nva.NodeBankFace { FaceIndex = 0, CollisionID = a.NameID }] });
        return list;

        static List<short> Costs(short own) => [own, .. Enumerable.Repeat((short)-1, 15)];
    }

    private static Nva SyntheticHostList()
    {
        var host = new Nva();
        host.Entries11.Add(new Nva.Entry11 { Unk00 = 0x1234567, Unk04 = 2 });
        host.Navmeshes.Add(new Nva.Navmesh { NameID = 450000000, ModelID = 100, PositionW = 1, RotationW = 0, ScaleW = 0, FaceCount = 9, Unk44 = 2.0f });
        host.Navmeshes.Add(new Nva.Navmesh { NameID = NavmeshList.OSet(450000000), ModelID = 100, PositionW = 1, RotationW = 0, ScaleW = 0, FaceCount = 9, Unk44 = 2.4f });
        return host;
    }

    private static void NavmeshListRoundTrip()
    {
        var host = Nva.Read(SyntheticHostList().Write());
        Expect(host.Version == Nva.NVAVersion.EldenRing && host.Navmeshes.Count == 2 && host.Entries11.Single().Unk00 == 0x1234567, "Elden Ring list");
        Expect((uint)host.Navmeshes[1].NameID == 2_450_000_000u && host.Navmeshes[1].Unk44 == 2.4f && host.Navmeshes[0].Unk50 == 0xFF01, "Elden Ring entry");
        var ds3 = Nva.Read(SyntheticDs3List().Write());
        Expect(ds3.Version == Nva.NVAVersion.DarkSouls3 && ds3.Navmeshes.Count == 3 && ds3.Connectors.Count == 2, "DS3 list");
        Expect(ds3.Navmeshes[0].NameID == 400_000_001 && ds3.Navmeshes[1].GateNodes.Single().ConnectedNavmeshIndex == 0, "DS3 entry");
        Expect(ds3.Connectors[0].NavMeshConnections.Count == 2 && ds3.Connectors[0].GraphConnections.Count == 1 && ds3.NodeBanks.Count == 1, "DS3 connections");
    }

    private static void NavmeshListConvert()
    {
        var ds3 = Nva.Read(SyntheticDs3List().Write());
        NavmeshList.Entry[] entries = [new(1, 2, 6), new(200, 2, 6), new(300, 2, 6)];
        var result = NavmeshList.Convert(ds3, ErId, entries, Matrix4x4.CreateTranslation(0, 100, 0), SyntheticHostList());
        var list = Nva.Read(result.List.Write());
        Expect(list.Version == Nva.NVAVersion.EldenRing && list.Navmeshes.Count == 6, $"{list.Navmeshes.Count} entries");
        Expect(list.Entries11.Single().Unk00 == 0x1234567 && list.Entries11.Single().Unk04 == 2, "map UID is the host's");
        Expect(list.Navmeshes.Select(e => (uint)e.NameID).SequenceEqual([450000000u, 450000001u, 450000002u, 2450000000u, 2450000001u, 2450000002u]),
            "NameIDs: " + string.Join(",", list.Navmeshes.Select(e => (uint)e.NameID)));
        Expect(list.Navmeshes.Select(e => e.ModelID).SequenceEqual([1, 200, 300, 1, 200, 300]), "ModelIDs");
        Expect(list.Navmeshes.Select(e => e.FaceDataIndex).SequenceEqual([0, 2, 4, 6, 8, 10]) && list.Navmeshes.All(e => e.FaceCount == 2 && e.Unk4C == 0), "face counts");
        Expect(list.Navmeshes.All(e => e.Position == Vector3.Zero && e.Rotation == Vector3.Zero && e.Scale == Vector3.One && e.PositionW == 1 && e.ScaleW == 0),
            "entries sit at the identity");
        Expect(list.Navmeshes[0].Unk44 == 2.0f && list.Navmeshes[3].Unk44 == 2.4f && list.Navmeshes.All(e => e.Unk50 == 0xFF01), "host's entry values");
        var gate = list.Navmeshes[0].GateNodes.Single();
        Expect(gate.Position == new Vector3(10, 100, 5) && gate.ConnectedNavmeshIndex == 1 && gate.NeighbourGateNodeCosts.Count == 16, $"n gate {gate}");
        Expect(list.Navmeshes[4].GateNodes.Single().ConnectedNavmeshIndex == 3 && list.Navmeshes[3].GateNodes.Single().ConnectedNavmeshIndex == 4, "o gates point at the o half");
        Expect(list.Connectors.Count == 4, $"{list.Connectors.Count} connections");
        Expect(list.Connectors[0].MainNameID == 450000000 && list.Connectors[0].TargetNameID == 450000001 && (uint)list.Connectors[3].TargetNameID == 2450000002u, "connection names");
        // The joined edge that names face 5 of a 2-face mesh is left out; cluster graph connections are not carried.
        Expect(list.Connectors[0].NavMeshConnections.Count == 1 && list.Connectors[0].NavMeshConnections[0].EdgeIndex == 4 && list.Connectors[0].GraphConnections.Count == 0, "joined edges");
        Expect(list.NodeBanks.Count == 0 && list.FaceDatas.Count == 0, "node banks left out");
        Expect(result.Notes.Any(n => n.Contains("1 joined edges name a face")) && result.Notes.Any(n => n.Contains("node banks")), string.Join(" | ", result.Notes));

        // Without a host list the map UID is JortPob's and the unknowns take the values every Elden Ring entry has.
        var bare = NavmeshList.Convert(ds3, ErId, entries, Matrix4x4.Identity, null).List;
        Expect(bare.Entries11.Single().Unk00 == NavmeshList.FallbackMapUid && bare.Navmeshes[0].Unk44 == 2.4f && bare.Navmeshes[0].PositionW == 1, "no host list");
        Expect(NavmeshList.NameId(MapId.Parse("m11_70_00_00"), 12) == 117000012, "NameID of another slot");
    }

    private static void NavmeshListMissing()
    {
        var ds3 = Nva.Read(SyntheticDs3List().Write());
        // The second navmesh has no file: its connections go, and with a gate lost the list has no gates at all.
        var result = NavmeshList.Convert(ds3, ErId, [new(1, 2, 6), new(200, 0, 0), new(300, 2, 6)], Matrix4x4.Identity, null);
        var list = Nva.Read(result.List.Write());
        Expect(list.Navmeshes.Count == 6 && list.Navmeshes[1].Unk4C == 1 && list.Navmeshes[4].Unk4C == 1 && list.Navmeshes[0].Unk4C == 0, "entries without faces are marked");
        Expect(list.Connectors.Count == 0 && list.Navmeshes.All(e => e.GateNodes.Count == 0), "connections and gates");
        Expect(list.Navmeshes.Select(e => e.FaceDataIndex).SequenceEqual([0, 2, 2, 4, 6, 6]), "face data index");
        Expect(result.Notes.Any(n => n.Contains("without gate nodes")) && result.Notes.Any(n => n.Contains("2 connections")), string.Join(" | ", result.Notes));
    }

    private static (BND4 Ds3, BND4 Host) SyntheticNavmeshBinders()
    {
        var ds3 = new BND4();
        ds3.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\FDP\data\Model\map\m40_00_00_00\navimesh\n40_00_00_00_000001.hkx", SyntheticDs3Navmesh()));
        ds3.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 1, @"N:\FDP\data\Model\map\m40_00_00_00\navimesh\n40_00_00_00_000200.hkx", SyntheticDs3Navmesh()));
        ds3.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 2, @"N:\FDP\data\Model\map\m40_00_00_00\navimesh\n40_00_00_00_000999.hkx", SyntheticDs3Navmesh()));
        var host = new BND4();
        host.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 10000, @"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\navimesh\bind6\n45_00_00_00_000100.hkx", [1, 2, 3]));
        host.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 1, @"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\navimesh\bind6\n45_00_00_00.compendium", [4, 5, 6]));
        return (ds3, host);
    }

    private static void NavmeshBinder()
    {
        var (ds3, host) = SyntheticNavmeshBinders();
        var list = Nva.Read(SyntheticDs3List().Write());
        // Characters: one on the first mesh, one on the turned second mesh ((20,0,0) to (30,0,-10)), one nowhere.
        var settings = new MapNavmesh.Settings(Xml: true, Points: [new Vector3(3, 0.5f, 3), new Vector3(27, 0, -8), new Vector3(500, 0, 500)]);
        var result = MapNavmesh.Convert(ds3, host, Ds3Id, ErId, settings, list, SyntheticHostList());
        Expect(result.Converted == 2 && result.List is not null, result.Summary);
        var files = result.Binder.Files.Select(f => (f.ID, Name: ItemText.Leaf(f.Name))).ToList();
        Expect(files.SequenceEqual([(1, "n45_00_00_00.compendium"), (10000, "n45_00_00_00_000001.hkx"), (10001, "n45_00_00_00_000200.hkx"),
            (20000, "o45_00_00_00_000001.hkx"), (20001, "o45_00_00_00_000200.hkx")]), string.Join(", ", files));
        Expect(result.Binder.Files.All(f => f.Name.StartsWith(@"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\navimesh\bind6\")), "folder is the host's");
        Expect(result.Binder.Files[1].Bytes.SequenceEqual(result.Binder.Files[3].Bytes), "the o file is the n file");
        // The second file is turned by its entry: its (10, 0, 0) corner lands at (20, 0, -10).
        var turned = CollisionPort.Read(result.Binder.Files[2].Bytes).m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiNavMesh>().Single();
        Expect(Vector3.Distance(new Vector3(turned.m_vertices[1].X, turned.m_vertices[1].Y, turned.m_vertices[1].Z), new Vector3(20, 0, -10)) < 1e-3f, $"turned vertex {turned.m_vertices[1]}");
        Expect(result.List!.Navmeshes.Count == 6 && result.List.Navmeshes[2].FaceCount == 0 && result.List.Navmeshes[2].Unk4C == 1, "the entry without a file has no faces");
        Expect(result.List.Navmeshes.Take(2).All(e => e.FaceCount == 2) && result.List.Connectors.Count == 2, "list entries and connections");
        Expect(result.Covered == 2, $"{result.Covered} characters over a face");
        var notes = string.Join(" | ", result.Notes);
        Expect(notes.Contains("2 of 2 gate nodes lie on their navmesh") && notes.Contains("2 of 3 DS3 characters"), notes);
        Expect(notes.Contains("1 DS3 navmesh files aren't in DS3's list") && notes.Contains("1 list entries have no file") && notes.Contains("1 of 2 navmeshes were turned"), notes);
        Expect(notes.Contains("0x7 x2") && notes.Contains("2 DS3 jump and drop links"), notes);

        // map-validate's check of the pair passes, and catches a list that names a file the binder lacks or misnames an entry.
        Expect(MapNavmesh.Check(result.Binder, result.List, ErId).Count == 0, string.Join(" | ", MapNavmesh.Check(result.Binder, result.List, ErId)));
        var broken = Nva.Read(result.List.Write());
        broken.Navmeshes[1].ModelID = 777;
        broken.Navmeshes[0].NameID = 5;
        var problems = string.Join(" | ", MapNavmesh.Check(result.Binder, broken, ErId));
        Expect(problems.Contains("1 entries with faces have no file") && problems.Contains("1 entries aren't named") && problems.Contains("another ModelID"), problems);

        // The map offset taken off moves meshes and gates together.
        var moved = MapNavmesh.Convert(ds3, host, Ds3Id, ErId, settings, list, null, Matrix4x4.CreateTranslation(0, 0, 1000));
        Expect(moved.Covered == 0 && moved.List!.Navmeshes[0].GateNodes.Single().Position == new Vector3(10, 0, 1005), "moved list");
        Expect(string.Join(" | ", moved.Notes).Contains("2 of 2 gate nodes lie on their navmesh"), "moved gates stay on their meshes");

        // Two entries that share a ModelID are two placements: each gets its own file.
        list.Navmeshes[2].ModelID = 1;
        var twice = MapNavmesh.Convert(ds3, host, Ds3Id, ErId, settings, list);
        Expect(twice.Converted == 3 && twice.List!.Navmeshes[2].ModelID == 201 && twice.Binder.Files.Any(f => f.Name.EndsWith("o45_00_00_00_000201.hkx")),
            string.Join(", ", twice.Binder.Files.Select(f => ItemText.Leaf(f.Name))));
    }

    private static void NavmeshBinderNoList()
    {
        var (ds3, host) = SyntheticNavmeshBinders();
        var result = MapNavmesh.Convert(ds3, host, Ds3Id, ErId, new MapNavmesh.Settings(Xml: true));
        Expect(result.Converted == 3 && result.List is null, result.Summary);
        var names = result.Binder.Files.Select(f => f.Name).ToList();
        Expect(names.Any(n => n.EndsWith(@"\n45_00_00_00_000001.hkx") && n.Contains("INTERROOT_win64")), string.Join(", ", names));
        Expect(names.Any(n => n.EndsWith(".compendium")) && !names.Any(n => n.EndsWith("000100.hkx")), "compendium kept, host navmesh left out");
    }

    private static void NavmeshArguments()
    {
        var plain = MapPorter.Options.Parse([]);
        Expect(!plain.HostNavmeshList && !plain.Ds3NavmeshFlags, "defaults");
        var o = MapPorter.Options.Parse(["--host-navmesh-list", "--navmesh-flags", "ds3"]);
        Expect(o.HostNavmeshList && o.Ds3NavmeshFlags, "arguments");
    }
}
