using System.Numerics;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>Offline checks of the map layout, model and event steps (run by `map-selftest`).</summary>
public static partial class MapPortChecks
{
    private static readonly MapId Ds3Id = MapId.Parse("m40_00_00_00");
    private static readonly MapId ErId = MapId.Parse("m45_00_00_00");

    public static IEnumerable<(string Name, Action Test)> All()
    {
        yield return ("map ids: parse, names and entity moves", MapIds);
        yield return ("layout: DS3 map pieces, collisions and players become Elden Ring parts that write and read back", Layout);
        yield return ("layout: the run start is the DS3 arrival point (entity x990-x999) with entity 45000990", LayoutStart);
        yield return ("layout: DS3 group masks are copied by position when asked", LayoutGroups);
        yield return ("events: run events have DarkScript3's Elden Ring layouts and survive a write", RunEventLayout);
        yield return ("events: adding the run events twice leaves one copy", RunEventsIdempotent);
        yield return ("events: the return point prefers a Roundtable Hold warp", ReturnPoint);
        yield return ("names: collision and texture binder entries", Names);
        yield return ("navmesh: DS3 Havok AI 2014 -> Elden Ring 2018 keeps faces, edges, graph and tree (XML)", () => Navmesh(xml: true));
        yield return ("navmesh: DS3 Havok AI 2014 -> Elden Ring 2018 keeps faces, edges, graph and tree (binary)", () => Navmesh(xml: false));
        yield return ("navmesh: binder entries are renamed to the host map and keep the host's compendium", NavmeshBinder);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void MapIds()
    {
        var id = MapId.Parse("m40_00_00_00");
        Expect(id.ToString() == "m40_00_00_00" && id.Digits == "40_00_00_00" && id.AreaFolder == "m40", $"names: {id}");
        Expect(id.EntityBase == 40_000_000, $"entity base {id.EntityBase}");
        Expect(id.MoveEntity(40001234, ErId) == 45001234, "entity move");
        Expect(id.MoveEntity(31001234, ErId) == 31001234, "another map's entity must stay");
        Expect(id.MoveEntity(0, ErId) == 0 && id.MoveEntity(-1, ErId) == -1, "unset entity must stay");
        Expect(MapSlots.ForDs3(id)?.Er == ErId, "m40 slot");
        Expect(MapPorter.ErMapBndPath(ErId, "000100") == "/map/m45/m45_00_00_00/m45_00_00_00_000100.mapbnd.dcx", "ER mapbnd path");
        Expect(MapPorter.Ds3MapBndPath(id, "000100") == "/map/m40_00_00_00/m40_00_00_00_000100.mapbnd.dcx", "DS3 mapbnd path");
        Expect(MapCollisions.ErBinderPath(ErId, "h") == "/map/m45/m45_00_00_00/h45_00_00_00.hkxbhd", "ER collision path");
        Expect(MapCollisions.Ds3BinderPath(id, "l") == "/map/m40_00_00_00/l40_00_00_00.hkxbhd", "DS3 collision path");
    }

    private static MSB3 SyntheticDs3()
    {
        var msb = new MSB3();
        msb.Models.MapPieces.Add(new MSB3.Model.MapPiece { Name = "m000100" });
        msb.Models.Collisions.Add(new MSB3.Model.Collision { Name = "h000100" });
        msb.Models.Players.Add(new MSB3.Model.Player { Name = "c0000" });
        var piece = new MSB3.Part.MapPiece { Name = "m000100_0000", ModelName = "m000100", Position = new Vector3(1, 2, 3), EntityID = 40000100 };
        piece.DrawGroups[0] = 0x11;
        piece.DispGroups[0] = 0x22;
        piece.BackreadGroups[0] = 0x33;
        msb.Parts.MapPieces.Add(piece);
        msb.Parts.Collisions.Add(new MSB3.Part.Collision { Name = "h000100_0000", ModelName = "h000100", HitFilterID = 8, Rotation = new Vector3(0, 90, 0) });
        msb.Parts.Players.Add(new MSB3.Part.Player { Name = "c0000_0000", ModelName = "c0000", EntityID = 40000500 });
        msb.Parts.Players.Add(new MSB3.Part.Player { Name = "c0000_0001", ModelName = "c0000", EntityID = 40000990, Position = new Vector3(7, 8, 9) });
        return msb;
    }

    private static MSBE SyntheticHost()
    {
        var host = new MSBE();
        host.Models.MapPieces.Add(new MSBE.Model.MapPiece { Name = "m001000", SibPath = @"N:\GR\data\Model\map\m45_00_00_00\sib\m001000.sib" });
        host.Models.Collisions.Add(new MSBE.Model.Collision { Name = "h001000", SibPath = "" });
        host.Models.Players.Add(new MSBE.Model.Player { Name = "c0000", SibPath = "" });
        host.Parts.MapPieces.Add(new MSBE.Part.MapPiece { Name = "m001000_0000", ModelName = "m001000" });
        host.Parts.Collisions.Add(new MSBE.Part.Collision { Name = "h001000_0000", ModelName = "h001000" });
        host.Parts.Players.Add(new MSBE.Part.Player { Name = "c0000_0000", ModelName = "c0000" });
        return host;
    }

    private static void Layout()
    {
        var result = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId);
        Expect(result.MapPieces == 1 && result.Collisions == 1 && result.Players == 2, $"counts {result.MapPieces}/{result.Collisions}/{result.Players}");
        var msb = MSBE.Read(result.Msb.Write());
        var piece = msb.Parts.MapPieces.Single();
        Expect(piece.Name == "m000100_0000" && piece.ModelName == "m000100" && piece.Position == new Vector3(1, 2, 3), "map piece fields");
        Expect(piece.EntityID == 45000100, $"map piece entity {piece.EntityID}");
        Expect(piece.Unk1.DisplayGroups.All(g => g == 0xFFFFFFFF), "AllVisible must show every group");
        var collision = msb.Parts.Collisions.Single();
        Expect((int)collision.HitFilterID == 8 && collision.Rotation == new Vector3(0, 90, 0), "collision fields");
        Expect(msb.Models.MapPieces.Single().SibPath.Contains("m000100") && !msb.Models.MapPieces.Single().SibPath.Contains("m001000"),
            $"sib path {msb.Models.MapPieces.Single().SibPath}");
    }

    private static void LayoutStart()
    {
        var result = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId);
        Expect(result.StartEntityId == 45000990, $"start entity {result.StartEntityId}");
        var start = result.Msb.Parts.Players.Single(p => p.EntityID == 45000990);
        Expect(start.Position == new Vector3(7, 8, 9) && start.Name == "c0000_9990", $"start {start.Name} {start.Position}");
        Expect(result.Msb.Parts.Players.Any(p => p.EntityID == 45000500), "other player point keeps its moved entity");
    }

    private static void LayoutGroups()
    {
        var result = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId, new MsbPort.Options { AllVisible = false });
        var unk1 = result.Msb.Parts.MapPieces.Single().Unk1;
        Expect(unk1.DisplayGroups[0] == 0x11 && unk1.DrawGroups[0] == 0x22 && unk1.CollisionMask[0] == 0x33,
            $"groups {unk1.DisplayGroups[0]:X} {unk1.DrawGroups[0]:X} {unk1.CollisionMask[0]:X}");
    }

    private static EMEVD SyntheticCommon()
    {
        var common = new EMEVD(EMEVD.Game.Sekiro);
        var constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, 1234u, 0u));
        common.Events.Add(constructor);
        return common;
    }

    private static readonly RunEvents.ReturnPoint Back = new(MapId.Parse("m11_10_00_00"), 11102020);

    private static void RunEventLayout()
    {
        var common = SyntheticCommon();
        RunEvents.AddTo(common, ErId, 45000990, Back);
        common = EMEVD.Read(common.Write());
        var start = common.Events.Single(e => e.ID == RunEvents.StartEventId);
        Expect(start.Instructions.Count == 6, $"start has {start.Instructions.Count} instructions");
        var wait = start.Instructions[0];
        Expect(wait.Bank == 1003 && wait.ID == 0 && wait.ArgData.Length == 8 && wait.ArgData[0] == 1
               && BitConverter.ToUInt32(wait.ArgData, 4) == RunEvents.StartDs3Request, "WaitForEventFlag layout (u8 state, u8 type, u32 flag)");
        var clear = start.Instructions[1];
        Expect(clear.Bank == 2003 && clear.ID == 66 && BitConverter.ToUInt32(clear.ArgData, 4) == RunEvents.StartDs3Request
               && clear.ArgData[8] == 0, "SetEventFlag layout (u8 type, u32 flag, u8 state)");
        var warp = start.Instructions[3];
        Expect(warp.Bank == 2003 && warp.ID == 14 && warp.ArgData[0] == 45 && warp.ArgData[1] == 0
               && BitConverter.ToUInt32(warp.ArgData, 4) == 45000990 && BitConverter.ToInt32(warp.ArgData, 8) == -1,
            "WarpPlayer layout (4 x u8 map, u32 entity, s32 popup)");
        var end = start.Instructions[5];
        Expect(end.Bank == 1000 && end.ID == 4 && end.ArgData[0] == 1, "EndUnconditionally(Restart)");
        Expect(start.Instructions.All(i => i.Layer is null), "no instruction may carry a layer mask");
        var leave = common.Events.Single(e => e.ID == RunEvents.LeaveEventId);
        Expect(leave.Instructions[3].ArgData[0] == 11 && leave.Instructions[3].ArgData[1] == 10
               && BitConverter.ToUInt32(leave.Instructions[3].ArgData, 4) == 11102020, "leave warps to the return point");
        var inits = common.Events.Single(e => e.ID == 0).Instructions.Where(i => i.Bank == 2000 && i.ID == 0).ToList();
        Expect(inits.Count == 3 && inits.Any(i => BitConverter.ToUInt32(i.ArgData, 4) == 1234), "constructor keeps its own events and starts ours");
    }

    private static void RunEventsIdempotent()
    {
        var common = SyntheticCommon();
        RunEvents.AddTo(common, ErId, 45000990, Back);
        RunEvents.AddTo(common, ErId, 45000990, Back);
        Expect(common.Events.Count(e => e.ID == RunEvents.StartEventId) == 1, "start event duplicated");
        Expect(common.Events.Single(e => e.ID == 0).Instructions.Count == 3, "constructor inits duplicated");
    }

    private static void ReturnPoint()
    {
        EMEVD Script(params (byte Area, byte Block, uint Entity)[] warps)
        {
            var s = new EMEVD(EMEVD.Game.Sekiro);
            var e = new EMEVD.Event(100, EMEVD.Event.RestBehaviorType.Default);
            foreach (var (area, block, entity) in warps)
                e.Instructions.Add(RunEvents.Make(2003, 14, area, block, (byte)0, (byte)0, entity, -1));
            s.Events.Add(e);
            return s;
        }
        var point = RunEvents.FindReturnPoint([Script((0, 0, 0), (60, 42, 1042360000)), Script((11, 10, 11102020))]);
        Expect(point?.Map.Area == 11 && point.EntityId == 11102020, $"return point {point}");
        var fallback = RunEvents.FindReturnPoint([Script((60, 42, 1042360000))]);
        Expect(fallback?.EntityId == 1042360000, "fallback to any warp");
    }

    private static void Names()
    {
        Expect(MapCollisions.ModelNumberOf("h40_00_00_00_000100.hkx.dcx") == "000100", "collision model number");
        Expect(MapPorter.ModelNumber("h000100") == "000100" && MapPorter.ModelNumber("m000100") == "000100", "msb model number");
        Expect(MapTextures.TextureNameOf(@"N:\x\m40_00_wall_a.tpf.dcx") == "m40_00_wall_a", "texture name");
        Expect(MapPorter.ModParts("/map/m45/x.tpfbhd").SequenceEqual(["map", "m45", "x.tpfbhd"]), "mod parts");
    }

    internal static byte[] SyntheticDs3Navmesh()
    {
        Ds3.hkaiNavMeshEdge Edge(int a, int b, uint oppositeEdge = 0xFFFFFFFF, uint oppositeFace = 0xFFFFFFFF) => new()
        {
            m_a = a, m_b = b, m_oppositeEdge = oppositeEdge, m_oppositeFace = oppositeFace, m_flags = 0,
            m_userEdgeCost = (short)(BitConverter.SingleToInt32Bits(1.5f) >> 16),
        };
        var mesh = new Ds3.hkaiNavMesh
        {
            m_faces =
            [
                new Ds3.hkaiNavMeshFace { m_startEdgeIndex = 0, m_startUserEdgeIndex = -1, m_numEdges = 3, m_clusterIndex = 0 },
                new Ds3.hkaiNavMeshFace { m_startEdgeIndex = 3, m_startUserEdgeIndex = -1, m_numEdges = 3, m_clusterIndex = 0 },
            ],
            m_edges = [Edge(0, 1), Edge(1, 2, 3, 1), Edge(2, 0), Edge(2, 1, 1, 0), Edge(1, 3), Edge(3, 2)],
            m_vertices = [new(0, 0, 0, 0), new(10, 0, 0, 0), new(0, 0, 10, 0), new(10, 0, 10, 0)],
            m_streamingSets = [],
            m_faceData = [7, 7],
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
        var tree = new Ds3.hkcdStaticTreeDefaultTreeStorage6
        {
            m_domain = new Ds3.hkAabb { m_min = new(0, 0, 0, 0), m_max = new(10, 0, 10, 0) },
            m_nodes =
            [
                new Ds3.hkcdStaticTreeCodec3Axis6 { m_xyz_0 = 1, m_xyz_1 = 2, m_xyz_2 = 3, m_hiData = 0x80, m_loData = 1 },
                new Ds3.hkcdStaticTreeCodec3Axis6 { m_xyz_0 = 4, m_xyz_1 = 5, m_xyz_2 = 6, m_hiData = 0, m_loData = 0 },
                new Ds3.hkcdStaticTreeCodec3Axis6 { m_xyz_0 = 7, m_xyz_1 = 8, m_xyz_2 = 9, m_hiData = 0, m_loData = 1 },
            ],
        };
        var root = new Ds3.hkRootLevelContainer
        {
            m_namedVariants =
            [
                new Ds3.hkRootLevelContainerNamedVariant { m_name = "hkaiNavMesh", m_className = "hkaiNavMesh", m_variant = mesh },
                new Ds3.hkRootLevelContainerNamedVariant { m_name = "hkaiDirectedGraphExplicitCost", m_className = "hkaiDirectedGraphExplicitCost", m_variant = graph },
                new Ds3.hkRootLevelContainerNamedVariant
                {
                    m_name = "hkaiStaticTreeNavMeshQueryMediator", m_className = "hkaiStaticTreeNavMeshQueryMediator",
                    m_variant = new Ds3.hkaiStaticTreeNavMeshQueryMediator { m_navMesh = mesh, m_tree = new Ds3.hkcdStaticAabbTree { m_treePtr = tree } },
                },
            ],
        };
        using var stream = new MemoryStream();
        var bw = new BinaryWriterEx(false, stream);
        new Ds3.PackFileSerializer().Serialize(root, bw);
        bw.Finish();
        return stream.ToArray();
    }

    private static void Navmesh(bool xml)
    {
        var result = NavmeshPort.Convert(SyntheticDs3Navmesh(), xml);
        Expect(result.Faces == 2 && result.Edges == 6 && result.Vertices == 4 && result.GraphNodes == 1,
            $"counts {result.Faces}/{result.Edges}/{result.Vertices}/{result.GraphNodes}");
        var root = CollisionPort.Read(result.Bytes);
        var mesh = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiNavMesh>().Single();
        Expect(Math.Abs(mesh.m_edges[0].m_userEdgeCost - 1.5f) < 1e-3f, $"edge cost {mesh.m_edges[0].m_userEdgeCost}");
        Expect(mesh.m_edges[1].m_oppositeEdge == 3 && mesh.m_edges[1].m_oppositeFace == 1, "edge links");
        Expect(mesh.m_vertices[3] == new Vector4(10, 0, 10, 0) && mesh.m_faceData.SequenceEqual([7, 7]), "vertices or face data");
        var graph = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiDirectedGraphExplicitCost>().Single();
        Expect(graph.m_positions.Count == 1 && graph.m_positions[0] == new Vector4(5, 0, 5, 0), "graph positions");
        var mediator = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiStaticTreeNavMeshQueryMediator>().Single();
        var nodes = mediator.m_tree!.m_treePtr!.m_tree.m_nodes;
        Expect(nodes.Count == 3 && nodes[0].m_xyz[2] == 3 && nodes[0].m_hiData == 0x80 && nodes[2].m_loData == 1, "tree nodes");
        Expect(ReferenceEquals(mediator.m_navMesh, mesh) || mediator.m_navMesh?.m_faces.Count == 2, "mediator mesh");
    }

    private static void NavmeshBinder()
    {
        var ds3 = new BND4();
        ds3.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\FDP\data\Model\map\m40_00_00_00\navimesh\n40_00_00_00_000001.hkx", SyntheticDs3Navmesh()));
        var host = new BND4();
        host.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\navimesh\n45_00_00_00_000100.hkx", [1, 2, 3]));
        host.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 1, @"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\navimesh\n45_00_00_00.compendium", [4, 5, 6]));
        var result = MapNavmesh.Convert(ds3, host, Ds3Id, ErId, xml: true);
        Expect(result.Converted == 1, result.Summary);
        var names = result.Binder.Files.Select(f => f.Name).ToList();
        Expect(names.Any(n => n.EndsWith(@"\n45_00_00_00_000001.hkx") && n.Contains("INTERROOT_win64")), string.Join(", ", names));
        Expect(names.Any(n => n.EndsWith(".compendium")) && !names.Any(n => n.EndsWith("000100.hkx")), "compendium kept, host navmesh left out");
    }
}
