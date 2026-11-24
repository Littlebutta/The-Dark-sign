using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> BonfireChecks()
    {
        yield return ("bonfires: DS3's rows with a talker in the base state are the map's bonfires (leftover rows, the other state and other maps are not)", BonfiresFound);
        yield return ("bonfires: each gets Elden Ring's talker and standing point under Elden Ring's numbers; DS3's sword is the registered asset (a grace asset where there is none)", BonfiresPlaced);
        yield return ("bonfires: the script registers what DS3's lines don't and lights the sword's flame from the lit flag", BonfireEvents);
        yield return ("bonfires: a death respawns at the nearest bonfire until the player rests, and every warp into a ported map clears the respawn flag first", BonfireRespawnEvents);
    }

    private static readonly MapId BonfireDs3 = MapId.Parse("m30_00_00_00"), BonfireEr = MapId.Parse("m45_01_00_00");

    // The High Wall's layout in small: bonfire 0 with its sword, talker and point; bonfire 1 with a talker and no sword
    // (its sword failed to convert); bonfire 3 in the map's other state; rows naming nothing in the map.
    private static (MSB3 Msb, List<Bonfires.Ds3Row> Rows) BonfireMap()
    {
        var msb = new MSB3();
        msb.Parts.MapPieces.Add(new MSB3.Part.MapPiece { Name = "m000000_0000", ModelName = "m000000", MapStudioLayer = 1 });
        msb.Parts.Objects.Add(new MSB3.Part.Object { Name = "o000100_0001", ModelName = "o000100", EntityID = 3001950, MapStudioLayer = 0xFFFFE7FF, Position = new Vector3(-19, 41.2f, -15), Rotation = new Vector3(0, -60, 0) });
        msb.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1000_0002", ModelName = "c1000", EntityID = 3000950, TalkID = 300000, MapStudioLayer = 0xFFFFFFFF, Position = new Vector3(-19, 40.7f, -15), CollisionName = "h000201" });
        msb.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1000_0000", ModelName = "c1000", EntityID = 3000951, TalkID = 300001, MapStudioLayer = 0xFFFFFFFF, Position = new Vector3(-89, -49, 429), Rotation = new Vector3(0, 90, 0) });
        msb.Parts.Objects.Add(new MSB3.Part.Object { Name = "o000100_0003", ModelName = "o000100", EntityID = 3001953, MapStudioLayer = 2, Position = new Vector3(5, 5, 5) });
        msb.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1000_0003", ModelName = "c1000", EntityID = 3000953, MapStudioLayer = 2, Position = new Vector3(5, 5, 5) });
        msb.Parts.Objects.Add(new MSB3.Part.Object { Name = "o000100_0007", ModelName = "o000100", EntityID = 3001960, MapStudioLayer = 0xFFFFE7FF, Position = new Vector3(-19, -999, -15) });
        var rows = new List<Bonfires.Ds3Row>
        {
            new(4001950, 14000000, 211200, "Firelink Shrine"),
            new(3001950, 13000009, 211210, "High Wall of Lothric - High Wall of Lothric"),
            new(3001960, 13000000, 211210, "High Wall of Lothric - High Wall of Lothric - Initial Warp"),
            new(3001951, 13000001, 211217, "High Wall of Lothric - Consumed King's Garden"),
            new(3001953, 13000003, 211211, "High Wall of Lothric - Other state"),
            new(3001959, 13000003, 211210, "High Wall of Lothric - High Wall of Lothric"),
            new(3001950, 13000009, 211210, "High Wall of Lothric - again"),
        };
        return (msb, rows);
    }

    private static void BonfiresFound()
    {
        var (msb, rows) = BonfireMap();
        var found = Bonfires.Find(rows, msb, BonfireDs3);
        Expect(found.Select(b => b.Ds3Entity).SequenceEqual([3001950, 3001951]), "found " + string.Join(" ", found.Select(b => b.Ds3Entity)));
        Expect(found[0] is { Number: 0, Ds3Flag: 13000009, Name: "High Wall of Lothric", Turn: -60, Collision: "h000201" } && found[0].Position.Y == 41.2f
               && found[0].TalkerPosition.Y == 40.7f, $"bonfire 0: {found[0]}");
        Expect(found[1] is { Number: 1, Name: "Consumed King's Garden", Turn: 90, Collision: null } && found[1].Position == found[1].TalkerPosition,
            $"a bonfire without its sword stands at its talker: {found[1]}");
        Expect(Bonfires.Find(rows, msb, MapId.Parse("m31_00_00_00")).Count == 0, "another map has none of these");
        Expect(found[0] is { List: 99, Order: 0 } && Bonfires.AreaOf("High Wall of Lothric - Knight's Rest") == "High Wall of Lothric" && Bonfires.AreaOf("Firelink Shrine") == "Firelink Shrine",
            "rows without a warp-list tab sort last; the area is the row name's first part");
        var listed = Bonfires.Find([new(3001950, 13000009, 211210, "High Wall of Lothric - High Wall of Lothric", 12, 99), new(3001950, 13000009, 211210, "High Wall of Lothric - High Wall of Lothric", 6, 1)],
            msb, BonfireDs3);
        Expect(listed[0] is { List: 1, Order: 6, Area: "High Wall of Lothric" }, $"a bonfire takes the place of its listed row: {listed[0].List}/{listed[0].Order}");
        Expect(Bonfires.RowId(BonfireEr, 5) == 450105 && Bonfires.RowId(MapId.Parse("m19_70_00_00"), 0) == 197000 && Bonfires.RowId(MapId.Parse("m10_00_00_00"), 8) == 100008,
            "row numbers follow Elden Ring's (Stormveil's ninth grace is row 100008)");
    }

    // The slot's map as the earlier steps leave it: the sword as an asset under DS3's moved entity, DS3's points as player parts.
    private static MSBE BonfireSlot()
    {
        var msb = new MSBE();
        var sword = ObjectPort.NewPart();
        sword.Name = "AEG920_000_9000";
        sword.ModelName = "AEG920_000";
        sword.EntityID = 45011950;
        msb.Parts.Assets.Add(sword);
        msb.Models.Assets.Add(new MSBE.Model.Asset { Name = "AEG920_000" });
        msb.Parts.Collisions.Add(new MSBE.Part.Collision { Name = "h000201_0000", ModelName = "h000201" });
        msb.Models.Collisions.Add(new MSBE.Model.Collision { Name = "h000201" });
        msb.Parts.Players.Add(new MSBE.Part.Player { Name = "c0000_9990", ModelName = "c0000", EntityID = (uint)MsbPort.StartEntityId(BonfireEr) });
        msb.Parts.Players.Add(new MSBE.Part.Player { Name = "c0000_0002", ModelName = "c0000", EntityID = 45010970, Position = new Vector3(-16.7f, 41.2f, -13), Rotation = new Vector3(0, 94, 0) });
        msb.Parts.Players.Add(new MSBE.Part.Player { Name = "c0000_0005", ModelName = "c0000", EntityID = 45010980, Position = new Vector3(-21, 49, -55) });
        msb.Models.Players.Add(new MSBE.Model.Player { Name = "c0000" });
        return msb;
    }

    private static void BonfiresPlaced()
    {
        var (ds3, rows) = BonfireMap();
        var found = Bonfires.Find(rows, ds3, BonfireDs3);
        var partNames = new Dictionary<string, string> { ["h000201"] = "h000201_0000" };

        var msb = BonfireSlot();
        var notes = new List<string>();
        var placed = Bonfires.Place(msb, BonfireDs3, BonfireEr, found, partNames, notes);
        msb = MSBE.Read(msb.Write());
        Expect(placed.Count == 2, $"placed {placed.Count}");
        // Stormveil's first grace: asset 10001950, talker 10000950, point 10000980.
        Expect(placed[0] is { Entity: 45011950, Talker: 45010950, Point: 45010980, Sword: 45011950, GraceAsset: false }, $"numbers: {placed[0]}");
        Expect(msb.Parts.Assets.Single(a => a.EntityID == 45011950).ModelName == "AEG920_000", "DS3's sword is the registered asset");
        var talker = msb.Parts.Enemies.Single(e => e.EntityID == 45010950);
        Expect(talker is { ModelName: "c1000", TalkID: Bonfires.GraceTalk, NPCParamID: Bonfires.GraceNpc, ThinkParamID: Bonfires.GraceThink, CollisionPartName: "h000201_0000" }
               && talker.Position.Y == 40.7f && msb.Models.Enemies.Any(m => m.Name == "c1000"), "the talker is Elden Ring's grace talker on DS3's floor");
        var point = msb.Parts.Players.Single(p => p.EntityID == 45010980);
        Expect(point.Position == new Vector3(-16.7f, 41.2f, -13) && point.Rotation.Y == 94, "the standing point is DS3's point for this bonfire");
        Expect(msb.Parts.Players.Single(p => p.Name == "c0000_0005").EntityID == 0 && notes.Any(n => n.Contains("c0000_0005")), "a DS3 point with the standing point's number gives it up, and the log says so");
        // No sword and no DS3 point: a grace asset anyway, the point a step in front of it.
        Expect(placed[1] is { Entity: 45011951, Sword: 0, GraceAsset: true } && Vector3.Distance(placed[1].PointPosition, found[1].Position) is > 1.4f and < 1.6f,
            $"bonfire without a sword: {placed[1]}");
        var ids = msb.Parts.GetEntries().Where(p => p.EntityID != 0).GroupBy(p => p.EntityID).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Expect(ids.Count == 0, "entities named twice: " + string.Join(" ", ids));

        var grace = msb.Parts.Assets.Single(a => a.EntityID == 45011951);
        Expect(grace.ModelName == Bonfires.GraceModel && msb.Parts.Assets.Count(a => a.ModelName == Bonfires.GraceModel) == 1
               && msb.Models.Assets.Any(m => m.Name == Bonfires.GraceModel), "only the bonfire without a sword gets the grace asset");


        // The run's start point (990) is never taken: bonfire 10 is left out.
        var tenth = found[0] with { Number = 10, Ds3Entity = 3001960 };
        var skipNotes = new List<string>();
        Expect(Bonfires.Place(BonfireSlot(), BonfireDs3, BonfireEr, [tenth], partNames, skipNotes).Count == 0 && skipNotes.Count == 1,
            "a bonfire whose point would be the start point is left out");
    }

    private static void BonfireEvents()
    {
        var (ds3, rows) = BonfireMap();
        var found = Bonfires.Find(rows, ds3, BonfireDs3);
        var placed = Bonfires.Place(BonfireSlot(), BonfireDs3, BonfireEr, found, null, []);
        long Flag(long f) => f - 13000000 + 45010000;

        // The converted script: DS3's literal line for bonfire 0; bonfire 1 passed to a common event that registers it.
        var script = new EMEVD();
        var zero = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        zero.Instructions.Add(RunEvents.Make(2009, 3, 45010009u, 45011950u, 5f, 180f, 0, 0f));
        script.Events.Add(zero);
        var common = new EMEVD();
        var boss = new EMEVD.Event(1038291000, EMEVD.Event.RestBehaviorType.Default);
        boss.Instructions.Add(RunEvents.Make(2009, 3, 0u, 0u, 5f, 180f, 0, 0f));
        boss.Parameters.Add(new EMEVD.Parameter(0, 4, 8, 4));
        common.Events.Add(boss);
        Expect(Bonfires.Registers(script, common, 45011950) && !Bonfires.Registers(script, common, 45011951), "only bonfire 0 is registered so far");
        Expect(!Bonfires.Registers(common, null, 0), "a line filled in from a parameter registers nothing by itself");
        var withCall = new EMEVD();
        var start = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        start.Instructions.Add(RunEvents.Make(2000, 6, 0, 1038291000u, 45010800u, 45010001u, 45011951u, 45010951u));
        withCall.Events.Add(start);
        Expect(Bonfires.Registers(withCall, common, 45011951) && !Bonfires.Registers(withCall, null, 45011951), "a call to a registering common event registers its bonfire");

        var (registered, flames) = Bonfires.AddEvents(script, common, BonfireEr, placed, Flag, 900800061);
        script = EMEVD.Read(script.Write());
        Expect(registered == 1 && flames == 1, $"registered {registered}, flames {flames} (bonfire 1 has no sword)");
        var added = script.Events.Single(e => e.ID == 0).Instructions.Single(i => i.Bank == 2009 && BitConverter.ToUInt32(i.ArgData, 4) == 45011951);
        Expect(BitConverter.ToUInt32(added.ArgData, 0) == 45010001 && added.ArgData.Length == 24, "the added line names the bonfire's lit flag in Elden Ring's six-argument layout");
        var flame = script.Events.Single(e => e.ID == BonfireEr.EntityBase + Bonfires.FirstEvent);
        Expect(flame.Instructions.Count == 2 && flame.Instructions[0] is { Bank: 3, ID: 0 } && BitConverter.ToUInt32(flame.Instructions[0].ArgData, 4) == 45010009
               && flame.Instructions[1] is { Bank: 2006, ID: 4 } && BitConverter.ToUInt32(flame.Instructions[1].ArgData, 0) == placed[0].Sword
               && BitConverter.ToInt32(flame.Instructions[1].ArgData, 4) == Bonfires.FlameDummy && BitConverter.ToInt32(flame.Instructions[1].ArgData, 8) == 900800061,
            "the flame event waits for the lit flag and puts the fire on the sword's dummy poly 200");
        Expect(script.Events.Single(e => e.ID == 0).Instructions.Any(i => i.Bank == 2000 && i.ID == 0 && BitConverter.ToUInt32(i.ArgData, 4) == flame.ID), "event 0 starts it");
        var (again, noFlame) = Bonfires.AddEvents(script, common, BonfireEr, placed, Flag, 0);
        Expect(again == 0 && noFlame == 0, "nothing is registered twice, and an unconverted effect adds no flame event");
    }

    private static void BonfireRespawnEvents()
    {
        uint flag = BonfireRespawn.FollowsFlag;
        Expect(flag == 1039290101, $"the respawn flag is saved (block 0-999): {flag}");
        static uint U(EMEVD.Instruction i, int at) => BitConverter.ToUInt32(i.ArgData, at);

        // Nine points: two groups of seven at most per ring.
        var script = new EMEVD();
        script.Events.Add(new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        script.Events.Add(new EMEVD.Event(BonfireEr.EntityBase + BonfireRespawn.FirstEvent, EMEVD.Event.RestBehaviorType.Default));
        var points = Enumerable.Range(0, 9).Select(n => (uint)(BonfireEr.EntityBase + Bonfires.PointNumber + n)).ToList();
        int added = BonfireRespawn.AddEvents(script, BonfireEr, points);
        script = EMEVD.Read(script.Write());
        Expect(added == 1 + BonfireRespawn.Rings.Length * 2, $"arrival + 2 per ring: {added}");
        var ours = script.Events.Where(e => e.ID > BonfireEr.EntityBase + BonfireRespawn.FirstEvent).OrderBy(e => e.ID).ToList();
        Expect(ours.Count == added, "a taken event number is skipped, not replaced");
        var arrival = ours[0];
        Expect(script.Events.Single(e => e.ID == 0).Instructions.Any(i => i is { Bank: 2000, ID: 0 } && U(i, 4) == arrival.ID), "event 0 starts the arrival event");
        Expect(arrival.Instructions[0] is { Bank: 1003, ID: 2 } && arrival.Instructions[0].ArgData[1] == 1 && U(arrival.Instructions[0], 4) == flag,
            "it ends when the flag is on (the respawn point was already set since the last warp in)");
        Expect(arrival.Instructions[2] is { Bank: 1003, ID: 8 } && arrival.Instructions[2].ArgData[1] == 0 && arrival.Instructions[2].ArgData[2] == 45
               && arrival.Instructions[2].ArgData[3] == 1, "and when the player is outside this map (a neighbour map loading)");
        for (int k = 0; k < ours.Count - 1; k++)
            Expect(ours[k].Instructions[^1] is { Bank: 2000, ID: 0 } && U(ours[k].Instructions[^1], 4) == ours[k + 1].ID, $"step {k} starts step {k + 1}");
        var first = ours[1];
        Expect(first.Instructions.Count == 7 * 5 + 1, $"the first step checks seven points: {first.Instructions.Count} lines");
        var check = first.Instructions[0];
        Expect(check is { Bank: 3, ID: 3 } && (sbyte)check.ArgData[0] == 1 && check.ArgData[1] == 1 && U(check, 4) == 10000 && U(check, 8) == points[0]
               && BitConverter.ToSingle(check.ArgData, 12) == BonfireRespawn.Rings[0] && check.ArgData.Length == 20,
            "player within the first ring of point 0, in AND group 1");
        Expect(first.Instructions[1] is { Bank: 1000, ID: 1 } && first.Instructions[1].ArgData[0] == 3 && first.Instructions[1].ArgData[1] == 0
               && (sbyte)first.Instructions[1].ArgData[2] == 1, "skips the three lines when it isn't");
        Expect(first.Instructions[2] is { Bank: 2003, ID: 23 } && U(first.Instructions[2], 0) == points[0]
               && first.Instructions[3] is { Bank: 2003, ID: 66 } && U(first.Instructions[3], 4) == flag && first.Instructions[3].ArgData[8] == 1
               && first.Instructions[4] is { Bank: 1000, ID: 4 }, "else sets the respawn point, turns the flag on and ends");
        Expect((sbyte)first.Instructions[30].ArgData[0] == 7 && (sbyte)ours[2].Instructions[0].ArgData[0] == 1 && U(ours[2].Instructions[0], 8) == points[7],
            "groups 1-7 per event; points 7-8 go in the next event");
        var last = ours[^1];
        Expect(BitConverter.ToSingle(last.Instructions[0].ArgData, 12) == BonfireRespawn.Rings[^1]
               && last.Instructions[^2] is { Bank: 2003, ID: 23 } && U(last.Instructions[^2], 0) == points[0] && U(last.Instructions[^1], 4) == flag,
            "past the last ring: the map's first bonfire");

        // A map with only its start point needs no search.
        var lone = new EMEVD();
        Expect(BonfireRespawn.AddEvents(lone, BonfireEr, [45010990u]) == 1 && lone.Events.Single(e => e.ID != 0).Instructions.Count == 5
               && BonfireRespawn.AddEvents(new EMEVD(), BonfireEr, []) == 0, "one point: one event; no point: none");

        // common.emevd: warps into ported maps get the clear line before them, once; other warps and parameter targets don't.
        var common = new EMEVD();
        var travel = new EMEVD.Event(1039291110, EMEVD.Event.RestBehaviorType.Default);
        travel.Instructions.Add(RunEvents.Make(2003, 66, (byte)0, 1039292110u, (byte)0));
        travel.Instructions.Add(RunEvents.Make(2003, 14, (byte)45, (byte)1, (byte)0, (byte)0, 45010980u, 0));
        travel.Instructions.Add(RunEvents.Make(2003, 14, (byte)11, (byte)10, (byte)0, (byte)0, 11102020u, 0));
        travel.Instructions.Add(RunEvents.Make(2003, 14, (byte)0, (byte)0, (byte)0, (byte)0, 0u, 0));
        travel.Instructions.Add(RunEvents.Make(1001, 0, 1f));
        travel.Parameters.Add(new EMEVD.Parameter(3, 0, 0, 4));
        travel.Parameters.Add(new EMEVD.Parameter(4, 0, 0, 4));
        common.Events.Add(travel);
        var maps = new HashSet<MapId> { BonfireEr, MapId.Parse("m00_00_00_00") };
        int covered = BonfireRespawn.ClearBeforeWarps(common, maps);
        Expect(covered == 1 && travel.Instructions.Count == 6 && travel.Instructions[1] is { Bank: 2003, ID: 66 } && U(travel.Instructions[1], 4) == flag
               && travel.Instructions[1].ArgData[8] == 0 && travel.Instructions[2] is { Bank: 2003, ID: 14 },
            $"the warp into the ported map is preceded by flag off ({covered} covered, {travel.Instructions.Count} lines)");
        Expect(travel.Parameters[0].InstructionIndex == 4 && travel.Parameters[1].InstructionIndex == 5, "parameters after the new line move with their lines");
        Expect(BonfireRespawn.ClearBeforeWarps(common, maps) == 1 && travel.Instructions.Count == 6, "running again adds nothing");
    }
}
