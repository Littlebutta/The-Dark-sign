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
        yield return ("events: one warp event per ported map, started from the constructor, replaced on a rerun", PerMapWarps);
        yield return ("slots: the new-map-ID test slot uses a template map and doesn't hijack the real m40 slot", ProbeSlot);
        yield return ("events: the return point prefers a Roundtable Hold warp", ReturnPoint);
        yield return ("fallbacks: auto-start adds one start request per load outside a run, and a rerun without it removes it", AutoStartRun);
        yield return ("fallbacks: the new-map-ID slots give each DS3 map its own unshipped ID, built from a colosseum", NewIdSlots);
        yield return ("names: collision and texture binder entries", Names);
        yield return ("navmesh: DS3 Havok AI 2014 -> Elden Ring 2018 keeps faces, edges, graph and tree (XML)", () => Navmesh(xml: true));
        yield return ("navmesh: DS3 Havok AI 2014 -> Elden Ring 2018 keeps faces, edges, graph and tree (binary)", () => Navmesh(xml: false));
        yield return ("models: a DS3 map piece converts into a valid Elden Ring FLVER with its own material", ModelEndToEnd);
        yield return ("validate: layout checks catch a missing start point and parts without models", ValidateLayout);
        yield return ("validate: run event checks pass on ported events and catch a missing one", ValidateEvents);
        yield return ("navmesh: binder entries are renamed to the host map and keep the host's compendium", NavmeshBinder);
        yield return ("rebuild: a full run deletes port-maps' own old outputs (both slot methods) and keeps the other steps' files", CleanStale);
        yield return ("flags: the allocation probe finds block numbers and lists every block the port uses", FlagAllocProbe);
    }

    private static void CleanStale()
    {
        var root = Path.Combine(Path.GetTempPath(), "dks-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var ctx = new PorterContext(root);
            string[] ours = [ctx.ModPath("map", "mapstudio", "m50_00_00_00.msb.dcx"), ctx.ModPath("map", "m45", "m45_01_00_00", "h45_01_00_00.hkxbhd"),
                ctx.ModPath("map", "m45", "m45_0000.tpfbhd"), ctx.ModPath("event", "common.emevd.dcx"), ctx.ModPath("event", "common_func.emevd.dcx"),
                ctx.ModPath("event", "m51_00_00_00.emevd.dcx"), ctx.ModPath("material", "allmaterial_dlc01.matbinbnd.dcx"),
                ctx.ModPath("msg", "engus", "menu_dlc02.msgbnd.dcx"), ctx.ModPath("darksign_flags.tsv"), ctx.ModPath("darksign_maps.tsv")];
            string[] theirs = [ctx.ModPath("regulation.bin"), ctx.ModPath("darksign_chr_map.tsv"), ctx.ModPath("material", "allmaterial_dlc02.matbinbnd.dcx"),
                ctx.ModPath("msg", "engus", "item_dlc02.msgbnd.dcx"), ctx.ModPath("event", "m10_00_00_00.emevd.dcx")];
            foreach (var f in ours.Concat(theirs))
                File.WriteAllText(f, "x");
            MapPorter.CleanOutputs(ctx);
            Expect(ours.All(f => !File.Exists(f)), "stale port-maps output left: " + string.Join(", ", ours.Where(File.Exists)));
            Expect(theirs.All(File.Exists), "deleted another step's file: " + string.Join(", ", theirs.Where(f => !File.Exists(f))));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void FlagAllocProbe()
    {
        var data = new byte[64];
        BitConverter.TryWriteBytes(data.AsSpan(13), 1039290u);
        Expect(FlagAlloc.Mentions(data, 1039290) && !FlagAlloc.Mentions(data, 1038290), "block found by its 32-bit value, others not");
        var blocks = FlagAlloc.UsedBlocks([ErId, ErId], [1030290005, 1030290006, 1038291000]);
        Expect(blocks.Keys.SequenceEqual(new long[] { 45000, 1030290, 1038291, 1039290, 1039291, 1039292 }),
            "blocks: " + string.Join(",", blocks.Keys));
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
        Expect(id.EntityBase == 40_000_000 && id.Ds3EntityBase == 4_000_000 && id.Ds3FlagBase == 14_000_000, $"entity base {id.EntityBase}");
        Expect(id.MoveEntity(4000800, ErId) == 45000800, "DS3 entity move (7 digits)");
        Expect(id.MoveEntity(14000800, ErId) == 45000800, "DS3 flag/event move (1AABLLLL)");
        Expect(id.MoveEntity(40001234, ErId) == 45001234, "8-digit move");
        Expect(id.MoveEntity(3000800, ErId) == 3000800 && id.MoveEntity(13000800, ErId) == 13000800, "another map's IDs must stay");
        Expect(MapId.Parse("m30_01_00_00").Ds3EntityBase == 3_010_000, "block digit");
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
        var piece = new MSB3.Part.MapPiece { Name = "m000100_0000", ModelName = "m000100", Position = new Vector3(1, 2, 3), EntityID = 4000100 };
        piece.DrawGroups[0] = 0x11;
        piece.DispGroups[0] = 0x22;
        piece.BackreadGroups[0] = 0x33;
        msb.Parts.MapPieces.Add(piece);
        msb.Parts.Collisions.Add(new MSB3.Part.Collision { Name = "h000100_0000", ModelName = "h000100", HitFilterID = 8, Rotation = new Vector3(0, 90, 0) });
        msb.Parts.Players.Add(new MSB3.Part.Player { Name = "c0000_0000", ModelName = "c0000", EntityID = 4000500 });
        msb.Parts.Players.Add(new MSB3.Part.Player { Name = "c0000_0001", ModelName = "c0000", EntityID = 4000990, Position = new Vector3(7, 8, 9) });
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
               && BitConverter.ToUInt32(warp.ArgData, 4) == 45000990 && BitConverter.ToInt32(warp.ArgData, 8) == 0,
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

    private static FLVER2 SyntheticFlver(int version, string mtd, params (string Param, string Path)[] textures)
    {
        var flver = new FLVER2();
        flver.Header.Version = version;
        flver.Header.Unicode = true;
        flver.Header.BoundingBoxMin = new Vector3(0, 0, 0);
        flver.Header.BoundingBoxMax = new Vector3(10, 1, 10);
        flver.Nodes.Add(new FLVER.Node { Name = "root", ParentIndex = -1, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1 });
        var material = new FLVER2.Material("mat", mtd, 0) { GXIndex = 0 };
        foreach (var (param, path) in textures)
            material.Textures.Add(new FLVER2.Texture(param, path, Vector2.One, 0, 0, 0, 0, 0));
        flver.Materials.Add(material);
        var gx = new FLVER2.GXList();
        gx.Add(new FLVER2.GXItem { ID = "GXMD", Unk04 = 162, Data = new byte[4] });
        flver.GXLists.Add(gx);
        var layout = new FLVER2.BufferLayout
        {
            new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Normal),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Tangent),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Tangent, 1),
            new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV),
        };
        flver.BufferLayouts.Add(layout);
        var mesh = new FLVER2.Mesh { MaterialIndex = 0, NodeIndex = 0 };
        foreach (var p in new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 0, 10) })
        {
            var v = new FLVER.Vertex();
            v.Positions.Add(p);
            v.Normals.Add(Vector3.UnitY);
            v.NormalWs.Add(127);
            v.Tangents.Add(new Vector4(1, 0, 0, 1));
            v.Tangents.Add(new Vector4(1, 0, 0, 1));
            v.UVs.Add(new Vector3(p.X / 10, p.Z / 10, 0));
            mesh.Vertices.Add(v);
        }
        mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
        mesh.FaceSets.Add(new FLVER2.FaceSet(FLVER2.FaceSet.FSFlags.None, false, true, 0, [0, 1, 2]));
        flver.Meshes.Add(mesh);
        if (version >= 0x20017)
            flver.Skeletons = new FLVER2.SkeletonSet
            {
                BaseSkeleton = [new FLVER2.SkeletonSet.Bone(0) { ParentIndex = -1, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1 }],
                AllSkeletons = [new FLVER2.SkeletonSet.Bone(0) { ParentIndex = -1, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1 }],
            };
        return FLVER2.Read(flver.Write());
    }

    private static MATBIN SyntheticMatbin(string name)
    {
        var matbin = new MATBIN { ShaderPath = @"N:\GR\data\Material\mtd\M[ARSN].spx", SourcePath = $@"N:\GR\data\Material\matxml\{name}.matxml" };
        matbin.Samplers.Add(new MATBIN.Sampler { Type = "C_DetailBlend__snp_Texture2D_0_AlbedoMap_0", Path = @"N:\GR\data\Model\map\m45\tex\m45_00_wall_a.tif" });
        matbin.Samplers.Add(new MATBIN.Sampler { Type = "C_DetailBlend__snp_Texture2D_1_NormalMap_0", Path = @"N:\GR\data\Model\map\m45\tex\m45_00_wall_n.tif" });
        matbin.Samplers.Add(new MATBIN.Sampler { Type = "C_DetailBlend__snp_Texture2D_2_MetallicMap_0", Path = @"N:\GR\data\Model\map\m45\tex\m45_00_wall_m.tif" });
        return MATBIN.Read(matbin.Write());
    }

    private static void ModelEndToEnd()
    {
        const string Template = "M_test_opaque";
        var host = SyntheticFlver(0x2001A, $@"N:\GR\data\Material\matxml\{Template}.matxml");
        var matbins = new Dictionary<string, BinderFile>
        {
            [Template] = new(Binder.FileFlags.Flag1, 0, $@"N:\GR\data\INTERROOT_win64\material\matbin\Map_m45\{Template}.matbin", SyntheticMatbin(Template).Write()),
        };
        var port = MapModelPort.Create([host], matbins, ErId);
        var ds3Flver = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN].mtd",
            ("g_DiffuseTexture", @"N:\FDP\data\Model\map\m40\tex\m40_00_stone_a.tga"),
            ("g_BumpmapTexture", @"N:\FDP\data\Model\map\m40\tex\m40_00_stone_n.tga"));
        var ds3Bnd = new BND4();
        ds3Bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\FDP\data\Model\map\m40_00_00_00\sib\m40_00_00_00_000100.flver", ds3Flver.Write()));
        var hostBnd = new BND4();
        hostBnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\GR\data\Model\map\m45_00_00_00\m45_00_00_00_001000\sib\m45_00_00_00_001000.flver", host.Write()));

        var binder = BND4.Read(port.ConvertBinder(ds3Bnd, hostBnd, Ds3Id, ErId, "000100").Write());
        var file = binder.Files.Single();
        Expect(ItemText.Leaf(file.Name) == "m45_00_00_00_000100.flver" && file.Name.Contains(@"m45_00_00_00_000100\sib"), $"binder entry {file.Name}");
        var flver = FLVER2.Read(file.Bytes);
        var issues = MapValidate.CheckFlver(flver).ToList();
        Expect(issues.Count == 0, string.Join("; ", issues));
        Expect(flver.Header.Version == 0x2001A, $"FLVER version {flver.Header.Version:X}");
        Expect(flver.Header.BoundingBoxMax == new Vector3(10, 1, 10), "bounding box from DS3");
        Expect(host.Header.BoundingBoxMax == new Vector3(10, 1, 10) && !ReferenceEquals(host.Header, flver.Header), "template header must not be shared");
        var material = flver.Materials.Single();
        Expect(material.Name.StartsWith("DKS_40_00_00_00_") && ModelPort.MatbinName(material.MTD) == material.Name, $"material {material.Name} / {material.MTD}");
        Expect(flver.Meshes[0].Vertices.All(v => v.Tangents.Count == 1 && v.Colors.Count >= 1), "one tangent and a vertex color per vertex");
        Expect(port.Textures.SetEquals(["m40_00_stone_a", "m40_00_stone_n"]), $"textures {string.Join(",", port.Textures)}");

        var newMatbin = port.MatbinFiles(100).Single();
        Expect(Path.GetFileNameWithoutExtension(ItemText.Leaf(newMatbin.Name)) == material.Name, $"matbin file {newMatbin.Name}");
        var samplers = MATBIN.Read(newMatbin.Bytes).Samplers.Select(s => Path.GetFileNameWithoutExtension(ItemText.Leaf(s.Path))).ToList();
        Expect(samplers.SequenceEqual(["m40_00_stone_a", "m40_00_stone_n", MapModelPort.BlackMetallic]), $"samplers {string.Join(",", samplers)}");
    }

    private static void ValidateLayout()
    {
        var good = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId).Msb;
        var report = new MapValidate.Report();
        MapValidate.CheckLayout(report, MSBE.Read(good.Write()), ErId);
        Expect(report.Fails == 0, report.ToString());

        var noStart = SyntheticDs3();
        noStart.Parts.Players.Clear();
        var bad = MsbPort.Convert(noStart, SyntheticHost(), Ds3Id, ErId).Msb;
        bad.Parts.MapPieces[0].ModelName = "m999999";
        report = new MapValidate.Report();
        MapValidate.CheckLayout(report, bad, ErId);
        Expect(report.Fails == 2, report.ToString());
    }

    private static void ValidateEvents()
    {
        var common = SyntheticCommon();
        RunEvents.AddTo(common, ErId, 45000990, Back);
        var issues = MapValidate.CheckRunEvents(EMEVD.Read(common.Write()), ErId).ToList();
        Expect(issues.Count == 0, string.Join("; ", issues));
        common.Events.RemoveAll(e => e.ID == RunEvents.LeaveEventId);
        Expect(MapValidate.CheckRunEvents(common, ErId).Any(i => i.Contains("missing")), "missing event not reported");
        var wrongHost = SyntheticCommon();
        RunEvents.AddTo(wrongHost, MapId.Parse("m45_01_00_00"), 45010990, Back);
        Expect(MapValidate.CheckRunEvents(wrongHost, ErId).Any(i => i.Contains("warps to")), "wrong warp target not reported");
    }

    private static void PerMapWarps()
    {
        var common = SyntheticCommon();
        RunEvents.Warp[] warps = [new(ErId, 45000990, 0), new(MapId.Parse("m50_00_00_00"), 50000990, 1)];
        RunEvents.AddWarps(common, warps);
        RunEvents.AddWarps(common, warps);
        common = EMEVD.Read(common.Write());
        var events = common.Events.Where(e => e.ID == RunEvents.WarpEventId(0) || e.ID == RunEvents.WarpEventId(1)).ToList();
        Expect(events.Count == 2, $"{events.Count} warp events");
        var warp = events[1].Instructions.Single(i => i.Bank == 2003 && i.ID == 14);
        Expect(warp.ArgData[0] == 50 && BitConverter.ToUInt32(warp.ArgData, 4) == 50000990 && warp.Layer is null, "second warp target");
        var wait = events[1].Instructions[0];
        Expect(BitConverter.ToUInt32(wait.ArgData, 4) == RunEvents.WarpRequestFlag(1), "waits on its own request flag");
        var inits = common.Events.Single(e => e.ID == 0).Instructions.Count(i => i.Bank == 2000 && BitConverter.ToUInt32(i.ArgData, 4) >= RunEvents.WarpEventId(0));
        Expect(inits == 2, $"{inits} constructor inits");
        Expect(RunEvents.WarpRequestFlag(0) / 10000 == RunEvents.FlagBlock / 10000 && RunEvents.WarpRequestFlag(0) % 10000 is >= 2000 and < 3000,
            "request flags must be in the block's temporary range");
    }

    private static void AutoStartRun()
    {
        var common = SyntheticCommon();
        RunEvents.AddTo(common, ErId, 45000990, Back, autoStart: true);
        common = EMEVD.Read(common.Write());
        var auto = common.Events.Single(e => e.ID == RunEvents.AutoStartEventId);
        Expect(auto.Instructions.Count == 3, $"{auto.Instructions.Count} instructions");
        var end = auto.Instructions[0];
        Expect(end.Bank == 1003 && end.ID == 2 && BitConverter.ToUInt32(end.ArgData, 4) == RunEvents.Ds3RunActive, "ends when the run is active");
        var set = auto.Instructions[2];
        Expect(set.Bank == 2003 && set.ID == 66 && BitConverter.ToUInt32(set.ArgData, 4) == RunEvents.StartDs3Request, "requests the start");
        int Inits(EMEVD c) => c.Events.Single(e => e.ID == 0).Instructions.Count(i => i.Bank == 2000 && BitConverter.ToUInt32(i.ArgData, 4) == RunEvents.AutoStartEventId);
        Expect(Inits(common) == 1, "started once from the constructor");
        RunEvents.AddTo(common, ErId, 45000990, Back);
        Expect(common.Events.All(e => e.ID != RunEvents.AutoStartEventId) && Inits(common) == 0, "a build without it removes it");
        Expect(MapValidate.CheckRunEvents(common, ErId).Count() == 0, "run events still valid");
    }

    private static void NewIdSlots()
    {
        var slots = MapSlots.NewIdSlots();
        Expect(slots.Select(s => s.Ds3).SequenceEqual(MapSlots.Colosseums.Where(s => !s.Probe).Select(s => s.Ds3)), "same DS3 maps");
        Expect(slots.All(s => s.Template is { Area: 45 } && s.Er.Area >= 50 && !s.Probe), "colosseum templates, IDs from m50");
        Expect(slots.Select(s => s.Er).Distinct().Count() == slots.Count, "one ID each");
    }

    private static void ProbeSlot()
    {
        var probe = MapSlots.All.Single(s => s.Probe);
        Expect(probe.Host == ErId && probe.Er != ErId, $"probe host {probe.Host}");
        Expect(MapSlots.ForDs3(Ds3Id)?.Er == ErId, "ForDs3 must return the real slot");
        Expect(MapSlots.All.Select(s => s.Er).Distinct().Count() == MapSlots.All.Count, "slots must not share an Elden Ring map");
    }
}
