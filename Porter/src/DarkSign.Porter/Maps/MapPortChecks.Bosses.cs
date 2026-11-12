using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>Offline checks of boss fights: talking bosses, the arenas the F1 menu warps to, and their warp events.</summary>
public static partial class MapPortChecks
{
    public static IEnumerable<(string Name, Action Test)> BossChecks()
    {
        yield return ("bosses: a talking boss and its other stages in the boss group are bosses, the same body elsewhere is a person", BossTalkers);
        yield return ("boss arenas: found from DS3's boss fight calls, named, landing in the arena, with the fight's own events", BossArenaFind);
        yield return ("boss arenas: placed only where the boss was placed, with a fresh arrival point, IDs moved to the slot", BossArenaPlace);
        yield return ("boss arenas: the warp event resets the fight, warps, waits for the map and begins the fight", BossArenaEvents);
    }

    // A Friede-like fight: two stages of one body in boss group 4505800 (first stage with the health bar), a second
    // body that only sits in the group, the first body again in a chapel with no group, and an ordinary enemy.
    private static MSB3 FriedeLike()
    {
        var ds3 = SyntheticDs3();
        var collision = ds3.Parts.Collisions[0].Name;
        MSB3.Part.Enemy Enemy(string name, int entity, int npc, int talk, params int[] groups)
        {
            var e = new MSB3.Part.Enemy { Name = name, ModelName = name[..5], EntityID = entity, NPCParamID = npc, TalkID = talk, CollisionName = collision };
            for (int i = 0; i < groups.Length; i++)
                e.EntityGroups[i] = groups[i];
            return e;
        }
        foreach (var model in new[] { "c6020", "c6010", "c1240" })
            ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = model });
        ds3.Parts.Enemies.Add(Enemy("c6020_0001", 4000801, 602000, 450551, 4005800, 4005801));
        ds3.Parts.Enemies.Add(Enemy("c6020_0000", 4000800, 602001, 450552, 4005800));
        ds3.Parts.Enemies.Add(Enemy("c6010_0001", 4000803, 601000, 450560, 4005800));
        ds3.Parts.Enemies.Add(Enemy("c6020_0002", 4000710, 602002, 450500));
        ds3.Parts.Enemies.Add(Enemy("c1240_0000", 4000100, 124000, 0));
        var area = new MSB3.Region.Event { Name = "boss area", EntityID = 4002800, Position = new Vector3(-200, 26, -230) };
        area.Shape = new MSB.Shape.Box(18.9f, 12.6f, 4f);
        ds3.Regions.Add(area);
        return ds3;
    }

    private static EMEVD FriedeLikeScript()
    {
        var script = new EMEVD(EMEVD.Game.DarkSouls3);
        var start = new EMEVD.Event(14005829, EMEVD.Event.RestBehaviorType.Default);
        // DS3 InitializeCommonEvent(20005800, defeated, fog, area, entry, fog action, boss group, first encounter, 0)
        start.Instructions.Add(RunEvents.Make(2000, 6, BossArenas.Ds3BossFight, 14000800, 4001800, 4002800, 14005805, 4001800, 4005800, 14000801, 0));
        script.Events.Add(start);
        var bar = new EMEVD.Event(14005813, EMEVD.Event.RestBehaviorType.Default);
        // Display Boss Health Bar: state, entity, slot, name. The first stage's bar comes after a second-slot one.
        bar.Instructions.Add(RunEvents.Make(2003, 11, (byte)1, 4000803, (short)1, 906021));
        bar.Instructions.Add(RunEvents.Make(2003, 11, (byte)1, 4000801, (short)0, 906020));
        script.Events.Add(bar);
        foreach (var id in new long[] { 14005811, 14005812, 14000830, 14005870, 14000100 })
            script.Events.Add(new EMEVD.Event(id, EMEVD.Event.RestBehaviorType.Default));
        return script;
    }

    private static readonly Dictionary<int, string> FriedeNames = new()
    {
        [602000] = "Sister Friede - First Stage", [602001] = "Sister Friede - Second/Final Stage", [601000] = "Father Ariandel",
    };

    private static void BossTalkers()
    {
        var ds3 = FriedeLike();
        var encounter = EnemyPort.BossEncounter(ds3, new HashSet<long> { 4000801, 4000800 });
        Expect(encounter.SetEquals(new long[] { 4000801, 4000800 }), "the boss group holds both stages: " + string.Join(" ", encounter));
        Expect(!encounter.Contains(4000710), "the same body without the boss group (the chapel) is not in the fight");
        var withAriandel = EnemyPort.BossEncounter(ds3, new HashSet<long> { 4000801, 4000800, 4000803 });
        Expect(withAriandel.Contains(4000803), "a boss given a health bar is in the fight");
        Expect(!EnemyPort.IsPerson(ds3.Parts.Enemies.First(e => e.EntityID == 4000801), encounter), "a talking boss is not a person");
        Expect(EnemyPort.IsPerson(ds3.Parts.Enemies.First(e => e.EntityID == 4000710), encounter), "the chapel placement is a person");
        Expect(!EnemyPort.IsPerson(ds3.Parts.Enemies.First(e => e.EntityID == 4000100), encounter), "an ordinary enemy is not a person");

        // The chapel placement doesn't borrow the boss stages' ported rows; a model ported without NpcParam rows lends its row.
        var map = EnemyPort.ChrMap.Parse(["npc\t602000\tc6100\t61000010\t61000000\tSister Friede", "npc\t602001\tc6100\t61000011\t61000000\tSister Friede",
            "chr\tc6020\tc6100\t61000010\t61000000\tSister Friede", "chr\tc1400\tc7001\t70010000\t70010000\tFire Keeper"]);
        Expect(map.FindPerson(new("c6020", 602002, "Sister Friede", false, true)) is null, "the chapel Friede must not become the boss row");
        Expect(map.FindPerson(new("c6020", 602000, "Sister Friede", true, true))?.NpcParam == 61000010, "a boss stage finds its own row");
        Expect(map.FindPerson(new("c1400", 140000, "Fire Keeper", false, true))?.Model == "c7001", "a model-only row still serves a person");
    }

    private static void BossArenaFind()
    {
        var ds3 = FriedeLike();
        var fights = BossArenas.Find(ds3, FriedeLikeScript(), Ds3Id, e => FriedeNames.GetValueOrDefault(e.NPCParamID));
        Expect(fights.Count == 1, $"{fights.Count} fights");
        var f = fights[0];
        Expect(f.Name == "Sister Friede", $"named after the first main health bar without its stage: '{f.Name}'");
        Expect(f.Defeated == 14000800 && f.Entry == 14005805 && f.FirstEncounter == 14000801 && f.Area == 4002800 && f.Boss == 4005800, "the call's arguments");
        Expect(f.Point == new Vector3(-200, 27.5f, -230), $"lands 1.5 m above the area's bottom: {f.Point}");
        Expect(f.Events.Order().SequenceEqual(new uint[] { 14000830, 14005811, 14005812, 14005813, 14005829 }),
            "the fight's events are 800-849 and 5800-5849 of the map, not the defeated flag or other numbers: " + string.Join(" ", f.Events));

        // A player point inside the area is where FromSoftware put the player on the floor.
        ds3.Parts.Players.Add(new MSB3.Part.Player { Name = "c0000_0010", ModelName = "c0000", Position = new Vector3(-195, 27.4f, -231) });
        Expect(BossArenas.Find(ds3, FriedeLikeScript(), Ds3Id)[0].Point == new Vector3(-195, 27.4f, -231), "a player point inside the area wins");
        var unnamed = BossArenas.Find(ds3, FriedeLikeScript(), Ds3Id);
        Expect(unnamed[0].Name == "Boss 14000800", $"without names: '{unnamed[0].Name}'");
        // A second fight starting at 830 cuts the first fight's events there.
        var cut = BossArenas.FightEvents(FriedeLikeScript(), Ds3Id, 14000800, [800, 830]);
        Expect(!cut.Contains(14000830) && cut.Contains(14005829), "the next fight's numbers belong to the next fight");
        // The area's rotation is honoured: a 90-degree box is long along Z.
        var turned = new MSB3.Region.Event { Position = Vector3.Zero, Rotation = new Vector3(0, 90, 0), Shape = new MSB.Shape.Box(10, 2, 4) };
        Expect(BossArenas.Contains(turned, new Vector3(0.5f, 1, 4)) && !BossArenas.Contains(turned, new Vector3(4, 1, 0.5f)), "rotated box test");
    }

    private static void BossArenaPlace()
    {
        var ds3 = FriedeLike();
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var fights = BossArenas.Find(ds3, FriedeLikeScript(), Ds3Id, e => FriedeNames.GetValueOrDefault(e.NPCParamID));
        var slot = new MapSlots.Slot(Ds3Id, ErId, "Test map");
        var notes = new List<string>();
        Expect(BossArenas.Place(layout.Msb, ds3, fights, slot, notes).Count == 0 && notes.Any(n => n.Contains("left out")),
            "no warp when the boss wasn't placed");

        layout.Msb.Models.Enemies.Add(new MSBE.Model.Enemy { Name = "c6020" });
        layout.Msb.Parts.Enemies.Add(new MSBE.Part.Enemy { Name = "c6020_0001", ModelName = "c6020", EntityID = (uint)(ErId.EntityBase + 801) });
        // An entity already using the first point's number pushes the point to the next one.
        var players = layout.Msb.Parts.Players.Count;
        var taken = (MSBE.Part.Player)layout.Msb.Parts.Players[0].DeepCopy();
        taken.EntityID = (uint)(ErId.EntityBase + BossArenas.FirstPointNumber);
        taken.Name = "c0000_0500";
        layout.Msb.Parts.Players.Add(taken);
        var arenas = BossArenas.Place(layout.Msb, ds3, fights, slot, notes);
        Expect(arenas.Count == 1, $"{arenas.Count} arenas");
        var a = arenas[0];
        Expect(a.PointEntity == ErId.EntityBase + BossArenas.FirstPointNumber + 1, $"arrival entity {a.PointEntity}");
        Expect(a.Defeated == ErId.EntityBase + 800 && a.Entry == ErId.EntityBase + 5805 && a.FirstEncounter == ErId.EntityBase + 801,
            "flags move to the slot like the map's own events");
        Expect(a.Events.All(id => id / 10_000 == ErId.EntityBase / 10_000), "event IDs move to the slot");
        Expect(layout.Msb.Parts.Players.Count == players + 2, "one new player point");
        var point = layout.Msb.Parts.Players.Single(p => p.EntityID == a.PointEntity);
        Expect(point.Position == fights[0].Point && point.ModelName == "c0000", "the point stands where the fight lands");
        var back = MSBE.Read(layout.Msb.Write());
        Expect(back.Parts.Players.Count(p => p.EntityID == a.PointEntity) == 1, "the point reads back");
        Expect(BossArenas.ListLines([a with { Index = 3 }]).Last() == $"{Ds3Id}\t{ErId}\tTest map\tSister Friede\t{BossArenas.RequestFlag(3)}", "list line");
    }

    private static void BossArenaEvents()
    {
        var common = SyntheticCommon();
        var arena = new BossArenas.Arena(Ds3Id, ErId, "Test map", "Sister Friede", 45000800, 45005805, 45000801, [45005811, 45005812], 45009970) { Index = 2 };
        var noFirst = arena with { Name = "Vordt", Defeated = 45000830, Entry = 45005835, FirstEncounter = 0, Events = [], Index = 3 };
        BossArenas.AddWarps(common, [arena, noFirst]);
        BossArenas.AddWarps(common, [arena, noFirst]);
        common = EMEVD.Read(common.Write());
        Expect(common.Events.Count(e => BossArenas.IsArenaEvent(e.ID)) == 2, "one event per arena after a rerun");
        int Inits(long id) => common.Events.Single(e => e.ID == 0).Instructions.Count(i => i.Bank == 2000 && BitConverter.ToUInt32(i.ArgData, 4) == id);
        Expect(Inits(BossArenas.EventId(2)) == 1 && Inits(BossArenas.EventId(3)) == 1, "each started once from the constructor");

        var ins = common.Events.Single(e => e.ID == BossArenas.EventId(2)).Instructions;
        uint U(EMEVD.Instruction i, int at) => BitConverter.ToUInt32(i.ArgData, at);
        int Flag(uint flag, byte state) => ins.FindIndex(i => i.Bank == 2003 && i.ID == 66 && U(i, 4) == flag && i.ArgData[8] == state);
        int warp = ins.FindIndex(i => i.Bank == 2003 && i.ID == 14);
        int inMap = ins.FindIndex(i => i.Bank == 3 && i.ID == 8);
        Expect(ins[0].Bank == 1003 && ins[0].ID == 0 && U(ins[0], 4) == BossArenas.RequestFlag(2), "waits for its request flag");
        Expect(Flag(BossArenas.RequestFlag(2), 0) > 0 && Flag(RunEvents.Ds3RunActive, 1) > 0, "clears the request and marks the run active");
        Expect(warp > 0 && ins[warp].ArgData[0] == ErId.Area && ins[warp].ArgData[1] == ErId.Block && U(ins[warp], 4) == 45009970, "warps to the arrival point");
        foreach (var off in new uint[] { 45000800, 45005805, 45000801, 45005811, 45005812 })
            Expect(Flag(off, 0) is >= 0 and var at && at < warp, $"flag {off} is cleared before the warp");
        Expect(inMap > warp && ins[inMap].ArgData[1] == 1 && ins[inMap].ArgData[2] == ErId.Area && ins[inMap].ArgData[3] == ErId.Block, "waits for the player in the map");
        Expect(Flag(45000801, 1) > inMap && Flag(45005805, 1) > inMap, "begins the fight once in the map: first encounter, then entry");
        Expect(ins[^1].Bank == 1000 && ins[^1].ID == 4 && ins[^1].ArgData[0] == 1, "ends and restarts, so it can be used again");

        var plain = common.Events.Single(e => e.ID == BossArenas.EventId(3)).Instructions;
        Expect(plain.Count(i => i.Bank == 2003 && i.ID == 66 && BitConverter.ToUInt32(i.ArgData, 4) == 0) == 0, "no flag 0 for a fight without a first encounter");
        Expect(plain.Any(i => i.Bank == 2003 && i.ID == 66 && BitConverter.ToUInt32(i.ArgData, 4) == 45005835 && i.ArgData[8] == 1), "still sets the entry flag");
    }
}
