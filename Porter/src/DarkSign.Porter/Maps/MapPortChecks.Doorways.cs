using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> DoorwayChecks()
    {
        yield return ("world: a neighbour shows from each doorway only what DS3's doorway list shows; own stand-ins over the neighbour hide on that doorway only", Doorways);
    }

    /// <summary>
    /// Two DS3 maps in one space. A (m30_00) has doorway collision hA (shows A's group 1) with a connect into B that
    /// shows B's group 5, and collision hA2 far away (shows A's group 2, no doorway). B has a stand-in of A's ground
    /// (group 9, drawn only from B's own hB) and a border piece in group 5. A has a stand-in of B (group 2) standing
    /// over B's floor. With DS3's lists: from hA, B shows its border piece but not its stand-in of A; A's stand-in of B
    /// hides on hA and still shows on hA2; and nothing DS3 shows is hidden.
    /// </summary>
    private static void Doorways()
    {
        static MSB3.Part.MapPiece Piece(string name, int bit)
        {
            var p = new MSB3.Part.MapPiece { Name = name, ModelName = name[..7] };
            Array.Clear(p.DrawGroups);
            Array.Clear(p.DispGroups);
            p.DrawGroups[0] = 1u << bit;
            return p;
        }
        static MSB3.Part.Collision Hit(string name, int bit)
        {
            var c = new MSB3.Part.Collision { Name = name, ModelName = name };
            Array.Clear(c.DrawGroups);
            Array.Clear(c.DispGroups);
            c.DispGroups[0] = 1u << bit;
            return c;
        }
        var a = new MSB3();
        a.Parts.Collisions.Add(Hit("h000100", 1));
        a.Parts.Collisions.Add(Hit("h000200", 2));
        var door = new MSB3.Part.ConnectCollision { Name = "h000100_0000", CollisionName = "h000100" };
        door.MapID[0] = 30; door.MapID[1] = 1;
        Array.Clear(door.DrawGroups);
        Array.Clear(door.DispGroups);
        door.DispGroups[0] = 1u << 5;
        a.Parts.ConnectCollisions.Add(door);
        a.Parts.MapPieces.Add(Piece("m000001_0000", 1));   // A's own ground at the doorway
        a.Parts.MapPieces.Add(Piece("m900001_0000", 2));   // A's stand-in of B
        var b = new MSB3();
        b.Parts.Collisions.Add(Hit("h000300", 9));
        b.Parts.MapPieces.Add(Piece("m900002_0000", 9));   // B's stand-in of A's ground
        b.Parts.MapPieces.Add(Piece("m000003_0000", 5));   // B's border piece
        static MapStandIns.PieceBox Box(string name, float x) => new(name, new Vector3(x - 8, -2, -8), new Vector3(x + 8, 10, 8), 100, false);
        static List<MapWorldFloor.Cell> Floor(string collision, float x) =>
            Enumerable.Range(-1, 3).SelectMany(i => Enumerable.Range(-1, 3).Select(k => new MapWorldFloor.Cell(collision, new Vector3(x + 4 * i, 0, 4 * k)))).ToList();
        var ids = (A: MapId.Parse("m30_00_00_00"), B: MapId.Parse("m30_01_00_00"));
        var maps = new List<MapConnectDisplay.MapData>
        {
            new(ids.A, MapId.Parse("m45_01_00_00"), a, [Box("m000001_0000", 0), Box("m900001_0000", 100)], [.. Floor("h000100", 0), .. Floor("h000200", 500)]),
            new(ids.B, MapId.Parse("m45_03_00_00"), b, [Box("m900002_0000", 0), Box("m000003_0000", 50)], Floor("h000300", 100)),
        };
        var plan = MapConnectDisplay.Build(maps);
        var pa = plan.Maps[MapId.Parse("m45_01_00_00")];
        var pb = plan.Maps[MapId.Parse("m45_03_00_00")];
        var doorShows = pa.ConnectDisplay["h000100_0000"];
        Expect(MapConnectDisplay.Drawn(pb.PieceDraw["m000003_0000"], doorShows), "B's border piece is shown through the doorway");
        Expect(!MapConnectDisplay.Drawn(pb.PieceDraw["m900002_0000"], doorShows), "B's stand-in of A is hidden through the doorway");
        Expect(!MapConnectDisplay.Drawn(pb.PieceDraw["m900002_0000"], pa.CollisionDisplay["h000200"]), "B is hidden from A's collision with no doorway to it");
        Expect(!MapConnectDisplay.Drawn(pa.PieceDraw["m900001_0000"], pa.CollisionDisplay["h000100"]), "A's stand-in of B hides on the doorway collision");
        Expect(MapConnectDisplay.Drawn(pa.PieceDraw["m900001_0000"], pa.CollisionDisplay["h000200"]), "A's stand-in of B still shows away from the doorway");
        Expect(MapConnectDisplay.Drawn(pa.PieceDraw["m000001_0000"], pa.CollisionDisplay["h000200"]), "A's own pieces are drawn from all of A's collisions");
        Expect(MapConnectDisplay.Drawn(pb.PieceDraw["m900002_0000"], pb.CollisionDisplay["h000300"]), "B's own pieces are drawn from B's collisions");
        var check = MapConnectDisplay.Verify(maps, plan);
        Expect(check.NeighbourBefore > 0 && check.NeighbourAfter == 0, $"neighbour clipping before {check.NeighbourBefore}, after {check.NeighbourAfter}");
        Expect(check.HiddenButShown == 0, $"{check.HiddenButShown} cells hide what DS3 shows");

        // The ported layout gets the groups in Elden Ring's fields (a piece's draw groups, a collision's display groups).
        var msb = new MSBE();
        var piece = new MSBE.Part.MapPiece { Name = "m900001_0000" };
        var hit = new MSBE.Part.Collision { Name = "h000100" };
        msb.Parts.MapPieces.Add(piece);
        msb.Parts.Collisions.Add(hit);
        MapConnectDisplay.Apply(msb, pa);
        Expect(!MapDrawGroups.DrawnFrom(MapDrawGroups.Draw(piece.Unk1), MapDrawGroups.Display(hit.Unk1)), "written groups hide the stand-in on the doorway collision");
        Expect(MapDrawGroups.IsEmpty(MapDrawGroups.Draw(hit.Unk1)), "a collision keeps no draw groups of its own");
    }
}
