using System.Numerics;
using SoulsFormats;

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
}
