using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> DoorwayChecks()
    {
        yield return ("world: a neighbour shows from each doorway only what DS3's doorway list shows; own stand-ins over the neighbour hide on that doorway only", Doorways);
        yield return ("neighbour copies: a map's low-detail copy of a joined neighbour becomes an asset hidden while that neighbour is loaded (Lothric Castle black slab)", NeighbourCopies);
        yield return ("rooms: a far view standing around every collision that shows it (Midir's arena walls) is kept and drawn only there, and the room shows only what DS3 shows from it", Backdrops);
    }

    /// <summary>
    /// Darkeater Midir's arena as m51_00 has it, in small: arena collision h012100 (group 1) and city collision h001000
    /// (group 2). The arena surround m040001 (group 1) is a box around the whole map; inside it stand 12 city pieces
    /// (group 2), 3 arena pieces (group 1) and a city stand-in m900100 (group 3, drawn from a second city collision
    /// h002000). The surround passes the far view test (the city inside it is never drawn with it), but its only
    /// collision stands inside it, so it is a backdrop: kept, drawn from the arena only; and from the arena the city
    /// and its stand-in are hidden, as in DS3. A far view whose collision stands outside it is still left out.
    /// </summary>
    private static void Backdrops()
    {
        var ds3 = new MSB3();
        MSB3.Part.Collision Hit(string name, int bit)
        {
            var c = new MSB3.Part.Collision { Name = name, ModelName = name };
            Array.Clear(c.DrawGroups);
            Array.Clear(c.DispGroups);
            c.DispGroups[0] = 1u << bit;
            ds3.Parts.Collisions.Add(c);
            return c;
        }
        Hit("h012100", 1);
        Hit("h001000", 2);
        Hit("h002000", 3);
        Hit("h003000", 4);
        var meshes = new Dictionary<string, List<MapModelPort.MeshInfo>>(StringComparer.OrdinalIgnoreCase);
        var boxes = new List<MapStandIns.PieceBox>();
        void Piece(string model, Vector3 at, float half, int vertices, uint groups)
        {
            var p = new MSB3.Part.MapPiece { Name = model + "_7000", ModelName = model, Position = at };
            Array.Clear(p.DrawGroups);
            p.DrawGroups[0] = groups;
            ds3.Parts.MapPieces.Add(p);
            var min = new Vector3(-half, -half / 4, -half);
            var max = new Vector3(half, half / 4, half);
            meshes[MapPorter.ModelNumber(model)] = [new MapModelPort.MeshInfo("M[ARSN]_l", "opaque", vertices, max - min, "m51_00_rock_wall_00_a") { Min = min, Max = max }];
            boxes.Add(new MapStandIns.PieceBox(p.Name, at + min, at + max, vertices, false));
        }
        Piece("m040001", Vector3.Zero, 900, 100, 0b0010);
        for (int i = 0; i < 12; i++)
            Piece($"m0010{i:00}", new Vector3(-300 + i * 50, 0, 200), 10, 1000, 0b0100);
        for (int i = 0; i < 3; i++)
            Piece($"m04010{i}", new Vector3(i * 20, -150, 0), 10, 500, 0b0010);
        Piece("m900100", new Vector3(0, 0, 300), 100, 1000, 0b1000);
        // A far view of the same city drawn only from a lookout far outside it (h003000, group 4).
        Piece("m901000", new Vector3(0, 0, 200), 350, 100, 0b10000);
        static IEnumerable<MapWorldFloor.Cell> Floor(string collision, Vector3 at) =>
            Enumerable.Range(-1, 3).SelectMany(i => Enumerable.Range(-1, 3).Select(k => new MapWorldFloor.Cell(collision, at + new Vector3(4 * i, 0, 4 * k))));
        var floor = Floor("h012100", new Vector3(0, -155, 0)).Concat(Floor("h001000", new Vector3(0, -2, 200)))
            .Concat(Floor("h002000", new Vector3(0, -2, 260))).Concat(Floor("h003000", new Vector3(3000, 0, 0))).ToList();

        var noFloor = MapStandIns.Find(ds3, meshes, new HashSet<string>());
        Expect(noFloor.FarViews.Contains("m040001_7000") && noFloor.Backdrops.Count == 0, "without the floor the surround is a far view, as up to 1.0.640: " + string.Join(" | ", noFloor.Lines));
        var judged = MapStandIns.Find(ds3, meshes, new HashSet<string>(), floor);
        Expect(!judged.LeftOut.Contains("m040001_7000") && judged.Backdrops.TryGetValue("m040001_7000", out var from) && from.SequenceEqual(["h012100"]),
            "the arena surround is a backdrop shown from the arena: " + string.Join(" | ", judged.Lines));
        Expect(judged.FarViews.Contains("m901000_7000"), "a far view whose collision stands outside it is still left out: " + string.Join(" | ", judged.Lines));
        Expect(MapStandIns.IsBackdrop([(10, 10)]) && MapStandIns.IsBackdrop([(5, 10), (12, 12)]) && !MapStandIns.IsBackdrop([(10, 10), (0, 30)])
               && !MapStandIns.IsBackdrop([(0, 0)]) && !MapStandIns.IsBackdrop([]), "the backdrop thresholds");

        var maps = new List<MapConnectDisplay.MapData>
        {
            new(MapId.Parse("m51_00_00_00"), MapId.Parse("m11_72_00_00"), ds3, boxes, floor) { Backdrops = judged.Backdrops },
        };
        var plan = MapConnectDisplay.Build(maps);
        var mp = plan.Maps[MapId.Parse("m11_72_00_00")];
        bool From(string piece, string collision) => MapConnectDisplay.Drawn(mp.PieceDraw[piece], mp.CollisionDisplay[collision]);
        Expect(From("m040001_7000", "h012100") && !From("m040001_7000", "h001000") && !From("m040001_7000", "h002000"), "the backdrop is drawn from its room only");
        Expect(From("m040100_7000", "h012100") && From("m040100_7000", "h001000"), "the arena's own pieces are drawn from the arena and, as before, from everywhere else");
        Expect(!From("m001000_7000", "h012100") && !From("m900100_7000", "h012100"), "from the arena the city and its stand-in are hidden, as in DS3");
        Expect(From("m001000_7000", "h001000") && From("m900100_7000", "h001000"), "away from the arena every own piece is drawn, as before");
        Expect(mp.Rooms.SequenceEqual(["h012100"]), "the room is reported: " + string.Join(",", mp.Rooms));
        var check = MapConnectDisplay.Verify(maps, plan);
        Expect(check.HiddenButShown == 0, $"{check.HiddenButShown} cells hide what DS3 shows: {string.Join(" | ", check.WronglyHidden)}");
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

        // Elden Ring finds collisions by number (Unk2.Condition): every collision is numbered, and A's connect into B
        // names B's stand-in collision for that doorway, which shows exactly the doorway's list.
        var connect = new MSBE.Part.ConnectCollision { Name = "h000100_0000", CollisionName = "h000100", MapID = [45, 3, 0, 0] };
        msb.Parts.ConnectCollisions.Add(connect);
        var away = new MSBE.Part.Collision { Name = "h000200", ModelName = "h000200" };
        msb.Parts.Collisions.Add(away);
        MapConnectDisplay.Apply(msb, pa);
        var msbB = new MSBE();
        var hitB = new MSBE.Part.Collision { Name = "h000300", ModelName = "h000300" };
        msbB.Parts.Collisions.Add(hitB);
        MapConnectDisplay.Apply(msbB, pb);
        MapConnectDisplay.Apply(msbB, pb);  // applying twice adds no second stand-in
        Expect(hit.Unk2.DispGroups.AsSpan(0, 8).SequenceEqual(MapDrawGroups.Display(hit.Unk1)) && hitB.Unk2.DispGroups.AsSpan(0, 8).SequenceEqual(MapDrawGroups.Display(hitB.Unk1)),
            "a collision's Unk2 groups are exactly its display groups (proven config V1; no collision-number bit beside them)");
        Expect(hit.Unk2.Condition >= 0 && (MapDrawGroups.Display(hit.Unk1)[hit.Unk2.Condition / 32] & (1u << (hit.Unk2.Condition % 32))) != 0,
            $"a collision's number is one of its own display bits ({hit.Unk2.Condition})");
        Expect(MapConnectDisplay.NumberClashes(msb).Count == 0 && MapConnectDisplay.NumberClashes(msbB).Count == 0,
            $"no collision number switches on a group of the bit layout: {string.Join(", ", MapConnectDisplay.NumberClashes(msb).Concat(MapConnectDisplay.NumberClashes(msbB)))}");
        // The #315 clash: a map with 41 collisions all showing the own bit; number 10 or 32-40 must not reach another map's bits.
        var many = new MSBE();
        for (int i = 0; i < 41; i++)
            many.Parts.Collisions.Add(new MSBE.Part.Collision { Name = $"h{i:D4}00", ModelName = $"h{i:D4}00" });
        var manyPlan = new MapConnectDisplay.MapPlan();
        var ownOnly = new uint[8];
        ownOnly[0] = 1u << 3;
        foreach (var c in many.Parts.Collisions)
            manyPlan.CollisionDisplay[c.Name] = ownOnly;
        MapConnectDisplay.Apply(many, manyPlan);
        Expect(many.Parts.Collisions.All(c => c.Unk2.Condition == 3 && c.Unk2.DispGroups.AsSpan(0, 8).SequenceEqual(ownOnly)),
            "41 collisions showing only the own bit carry only the own bit (no High Wall bit 10, no border bits 32-40)");
        var standIn = msbB.Parts.Collisions.Where(c => c != hitB).ToList();
        Expect(standIn.Count == pb.DoorwayLists && pb.DoorwayLists == 1, $"one stand-in per doorway list into B ({standIn.Count})");
        // 1.0.715-1.0.725 High Wall slabs: a connect loads the map it names, so a collision with no DS3 doorway into B must
        // get no connect into B (#321 gave it one, B loaded everywhere in A and drew its copies of A over A).
        Expect(msb.Parts.ConnectCollisions.Count == 1 && msb.Parts.ConnectCollisions[0] == connect,
            $"only DS3's own doorways connect into a neighbour; no added connects ({string.Join(", ", msb.Parts.ConnectCollisions.Select(c => c.Name))})");
        var stale = (MSBE.Part.ConnectCollision)connect.DeepCopy();
        stale.Name = "h000200_9700";
        stale.InstanceID = 9700;
        stale.CollisionName = "h000200";
        msb.Parts.ConnectCollisions.Add(stale);
        MapConnectDisplay.Apply(msb, pa);
        Expect(msb.Parts.ConnectCollisions.Count == 1, "an old show-nothing connect (_97xx) is removed");
        Expect(connect.Unk2.Condition == standIn[0].Unk2.Condition && connect.Unk2.Condition == MapConnectDisplay.DoorwayNumber(0),
            $"the connect names B's stand-in ({connect.Unk2.Condition} vs {standIn[0].Unk2.Condition})");
        Expect(MapDrawGroups.Display(standIn[0].Unk1).ToArray().SequenceEqual(doorShows), "the stand-in shows the doorway's list");
        Expect((doorShows[connect.Unk2.Condition / 32] & (1u << (connect.Unk2.Condition % 32))) != 0, "the connect's number is the doorway bit it shows");
        Expect(standIn[0].Position.Y <= hitB.Position.Y - MapConnectDisplay.DoorwayStandInDrop + 1, "the stand-in is far below");
        Expect(!MapConnectDisplay.Drawn(pb.PieceDraw["m900002_0000"], MapDrawGroups.Display(standIn[0].Unk1).ToArray())
               && MapConnectDisplay.Drawn(pb.PieceDraw["m000003_0000"], MapDrawGroups.Display(standIn[0].Unk1).ToArray()),
            "through the stand-in B shows its border piece and hides its stand-in of A");
    }

    /// <summary>
    /// High Wall (A, m30_00 -> m45_01) holds a low-detail shell of Lothric Castle (B, m30_01 -> m45_03), joined by a
    /// doorway; the castle's detail fills the shell's box. A second low piece of A stands over A's own detail, and a third
    /// over the detail of Painted World (C), which is not joined. Only the first is a copy; placed, it becomes an asset
    /// of its model, disabled while m45_03 is loaded, and the map piece is gone.
    /// </summary>
    private static void NeighbourCopies()
    {
        static MSB3.Part.MapPiece Piece(string name)
        {
            var p = new MSB3.Part.MapPiece { Name = name, ModelName = name[..7] };
            Array.Clear(p.DrawGroups);
            return p;
        }
        var a = new MSB3();
        var door = new MSB3.Part.ConnectCollision { Name = "h000100_0000", CollisionName = "h000100" };
        door.MapID[0] = 30; door.MapID[1] = 1;
        a.Parts.ConnectCollisions.Add(door);
        foreach (var n in new[] { "m980010_9500", "m980020_9500", "m980030_9500" })
            a.Parts.MapPieces.Add(Piece(n));
        var b = new MSB3();
        var c = new MSB3();
        static MapStandIns.PieceBox Box(string name, Vector3 at, float half, int vertices, bool low) =>
            new(name, at - new Vector3(half), at + new Vector3(half), vertices, low);
        var aBoxes = new List<MapStandIns.PieceBox>
        {
            Box("m980010_9500", new(500, 0, 0), 50, 80_000, true),
            Box("m980020_9500", new(0, 0, 0), 50, 80_000, true),
            Box("m980030_9500", new(-900, 0, 0), 50, 80_000, true),
        };
        for (int i = 0; i < 5; i++)
            aBoxes.Add(Box($"m00010{i}_0000", new(i * 10, 0, 0), 5, 30_000, false));
        var bBoxes = Enumerable.Range(0, 6).Select(i => Box($"m00020{i}_0000", new(480 + i * 8, 0, 0), 5, 40_000, false)).ToList();
        bBoxes.Add(Box("m900000_0000", new(500, 0, 0), 20, 99_000, true));   // B's own low piece does not count as detail
        var cBoxes = Enumerable.Range(0, 6).Select(i => Box($"m00030{i}_0000", new(-900 + i * 8, 0, 0), 5, 40_000, false)).ToList();
        var maps = new List<MapConnectDisplay.MapData>
        {
            new(MapId.Parse("m30_00_00_00"), MapId.Parse("m45_01_00_00"), a, aBoxes, []),
            new(MapId.Parse("m30_01_00_00"), MapId.Parse("m45_03_00_00"), b, bBoxes, []),
            new(MapId.Parse("m45_00_00_00"), MapId.Parse("m45_00_00_00"), c, cBoxes, []),
        };
        var copies = MapNeighbourCopies.Find(maps);
        Expect(copies.Count == 1 && copies[0] is { Piece: "m980010_9500", Covered: 6, CoveredVertices: 240_000 } && copies[0].NeighbourEr == MapId.Parse("m45_03_00_00"),
            "only the shell over the joined castle's detail is a copy: " + string.Join(" | ", copies.Select(x => x.Line)));
        Expect(MapNeighbourCopies.ModelKey(copies[0].Ds3, "m980010") == "m30_00_00_00_980010", "the model key reads like a cutscene map piece's");

        var er = new MSBE();
        er.Models.MapPieces.Add(new MSBE.Model.MapPiece { Name = "m980010" });
        er.Parts.MapPieces.Add(new MSBE.Part.MapPiece { Name = "m980010_9500", ModelName = "m980010", Position = new Vector3(1, 2, 3) });
        var asset = new ObjectPort.Asset { Ds3Model = "m30_00_00_00_980010", Index = 0, Name = "AEG920_000", Converted = true };
        var notes = new List<string>();
        int replaced = MapNeighbourCopies.Place(er, copies, _ => asset, notes);
        var part = er.Parts.Assets.SingleOrDefault();
        Expect(replaced == 1 && er.Parts.MapPieces.Count == 0 && er.Models.MapPieces.Count == 0 && part is not null
               && part.ModelName == "AEG920_000" && part.Name == "AEG920_000_9000" && part.Position == new Vector3(1, 2, 3)
               && part.AssetUnk3.DisableWhenMapLoadedMapID.SequenceEqual(new sbyte[] { 45, 3, 0, 0 })
               && er.Models.Assets.Any(m => m.Name == "AEG920_000") && asset.Placed == 1,
            "the shell is an asset in its place, disabled while m45_03 is loaded, and the map piece is gone");
        var written = MSBE.Read(er.Write());
        Expect(written.Parts.Assets.Single().AssetUnk3.DisableWhenMapLoadedMapID.SequenceEqual(new sbyte[] { 45, 3, 0, 0 }), "the field survives writing the layout");
    }
}
