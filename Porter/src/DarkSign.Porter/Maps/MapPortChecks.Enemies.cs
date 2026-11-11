using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>Offline checks of DS3 enemy and boss placement.</summary>
public static partial class MapPortChecks
{
    public static IEnumerable<(string Name, Action Test)> EnemyChecks()
    {
        yield return ("enemies: DS3 names sort into kinds (knight, soldier, dog, dragon...)", EnemyKinds);
        yield return ("enemies: stand-ins by kind, bosses spread over bosses, ported characters first, NPCs and 'none' left out", EnemyChooser);
        yield return ("enemies: boss entities come from boss health bars, given directly or passed to the event", EnemyBosses);
        yield return ("people: DS3 talkers get silent Elden Ring people, DS3 hostile humans get hostile ones, the same person every time", PeoplePlacement);
        yield return ("enemies: placed at DS3's spot with the DS3 entity moved to the slot, on its collision, and read back", EnemyPlacement);
    }

    private static void EnemyKinds()
    {
        foreach (var (name, kind) in new[]
                 {
                     ("Lothric Knight", "knight"), ("Hollow Soldier", "soldier"), ("Starved Hound", "dog"), ("Ancient Wyvern", "dragon"),
                     ("Ravenous Crystal Lizard", "lizard"), ("Cathedral Evangelist", "mage"), ("Skeleton Swordsman", "skeleton"),
                     ("Banished Knight", "knight"), ("Godrick Soldier", "soldier"), ("Mimic", ""),
                 })
            Expect(EnemyPort.KindOf(name) == kind, $"{name} is '{EnemyPort.KindOf(name)}', expected '{kind}'");
    }

    private static List<EnemyPort.ErEnemy> SyntheticCatalog() =>
    [
        new("c4300", 43000010, 43000000, "Godrick Soldier", false, "m10_00_00_00"),
        new("c4300", 43000010, 43000000, "Godrick Soldier", false, "m10_00_00_00"),
        new("c3100", 31000010, 31000000, "Banished Knight", false, "m10_00_00_00"),
        new("c4500", 45000010, 45000000, "Wolf", false, "m10_00_00_00"),
        new("c4810", 48100010, 48100000, "Margit, the Fell Omen", true, "m10_00_00_00"),
        new("c4380", 43800010, 43800000, "Godrick the Grafted", true, "m10_00_00_00"),
        new("c4740", 47400010, 47400000, "Crucible Knight", true, "m10_00_00_00"),
    ];

    private static void EnemyChooser()
    {
        var chooser = new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto);
        Expect(chooser.Choose(new("c1240", 124000, "Lothric Knight", false, false))?.Model == "c3100", "a DS3 knight should get the Elden Ring knight");
        Expect(chooser.Choose(new("c1105", 110500, "Hollow Soldier", false, false))?.Model == "c4300", "a DS3 soldier should get the soldier");
        Expect(chooser.Choose(new("c2190", 219000, "Mimic", false, false))?.Model == "c4300", "an unknown kind should get the most common enemy");
        Expect(chooser.Choose(new("c0000", 1, "Greirat", false, true)) is null, "NPCs are left out");
        var b1 = chooser.Choose(new("c5110", 511000, "Iudex Gundyr", true, false));
        var b2 = chooser.Choose(new("c5120", 512000, "Vordt", true, false));
        Expect(b1?.Boss == true && b2?.Boss == true && b1.NpcParam != b2.NpcParam, "two DS3 bosses should get two different Elden Ring bosses");
        Expect(chooser.Choose(new("c5300", 530000, "Dancer of the Boreal Valley", true, false))?.Boss == true, "bosses get bosses");
        Expect(chooser.Choose(new("c5400", 540000, "Champion Knight", true, false))?.Model == "c4740", "a knight boss should get the knight boss");

        var ported = EnemyPort.ChrMap.Parse([
            "# kind\tDS3\tER model\tER NpcParam\tER ThinkParam\tname",
            "chr\tc1240\tc9124\t91240000\t91240000\tLothric Knight (DS3)",
            "npc\t110500\tc9110\t91105000\t91105000",
        ]);
        var withPorted = new EnemyPort.Chooser(SyntheticCatalog(), ported, EnemyPort.Method.Auto);
        Expect(withPorted.Choose(new("c1240", 124001, "Lothric Knight", false, false))?.Model == "c9124", "a ported model should be used for any of its rows");
        Expect(withPorted.Choose(new("c1105", 110500, "Hollow Soldier", false, false))?.NpcParam == 91105000, "a ported NpcParam row should be used");
        var standIns = new EnemyPort.Chooser(SyntheticCatalog(), ported, EnemyPort.Method.StandIn);
        Expect(standIns.Choose(new("c1240", 124001, "Lothric Knight", false, false))?.Model == "c3100", "method B should ignore ported characters");
        var none = new EnemyPort.Chooser(SyntheticCatalog(), ported, EnemyPort.Method.None);
        Expect(none.Choose(new("c1240", 124001, "Lothric Knight", false, false)) is null, "method C places nothing");
        Expect(EnemyPort.ParseMethod("standin") == EnemyPort.Method.StandIn && EnemyPort.ParseMethod("none") == EnemyPort.Method.None
               && EnemyPort.ParseMethod(null) == EnemyPort.Method.Auto, "method names");
    }

    private static void EnemyBosses()
    {
        // Direct: Display Boss Health Bar (on, entity 4000800, slot 0, name).
        var map = OneEvent(14000800, Ins(2003, 11, (sbyte)1, 4000800, (short)0, 904000000));
        // Through a common event: entity passed as the event's first parameter.
        var common = new EMEVD(EMEVD.Game.DarkSouls3);
        var shows = new EMEVD.Event(20005800, EMEVD.Event.RestBehaviorType.Default);
        shows.Instructions.Add(Ins(2003, 11, (sbyte)1, 0, (short)0, 904010000));
        shows.Parameters.Add(new EMEVD.Parameter(0, 4, 0, 4));
        common.Events.Add(shows);
        map.Events[0].Instructions.Add(Ins(2000, 6, 20005800u, 4000801));
        var bosses = EnemyPort.BossEntities([map, common], ds3Layout: true);
        Expect(bosses.SetEquals(new long[] { 4000800, 4000801 }), $"bosses {string.Join(",", bosses)}");

        // Elden Ring's InitializeCommonEvent has a slot first.
        var er = new EMEVD(EMEVD.Game.Sekiro);
        var e = new EMEVD.Event(10000800, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(Ins(2000, 6, 0, 20005800u, 10000850));
        er.Events.Add(e);
        Expect(EnemyPort.BossEntities([er, common], ds3Layout: false).Contains(10000850), "Elden Ring layout: boss passed after the slot");
    }

    private static void EnemyPlacement()
    {
        var ds3 = SyntheticDs3();
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c1240" });
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c5110" });
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c0000" });
        var collision = ds3.Parts.Collisions[0].Name;
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1240_0000", ModelName = "c1240", EntityID = 4000100, NPCParamID = 124000, CollisionName = collision, Position = new Vector3(1, 2, 3) });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1240_0001", ModelName = "c1240", EntityID = 0, NPCParamID = 124000, CollisionName = "h999999_0000" });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c5110_0000", ModelName = "c5110", EntityID = 4000800, NPCParamID = 511000, CollisionName = collision });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c0000_0000", ModelName = "c0000", EntityID = 4000700, NPCParamID = 1, TalkID = 100, CollisionName = collision });
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var names = new Dictionary<int, string> { [124000] = "Lothric Knight", [511000] = "Iudex Gundyr" };
        var bosses = new HashSet<long> { 4000800 };
        var chooser = new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto);

        var result = EnemyPort.Place(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), chooser,
            e => new EnemyPort.Ds3Enemy(e.ModelName, e.NPCParamID, names.GetValueOrDefault(e.NPCParamID, e.ModelName), bosses.Contains(e.EntityID),
                e.ModelName == "c0000" || e.TalkID > 0));
        var enemies = layout.Msb.Parts.Enemies;
        Expect(result.Placed == 3 && result.Npcs == 1 && enemies.Count == 3, $"placed {result.Placed}, NPCs {result.Npcs}");
        var knight = enemies.First(e => e.EntityID == 45000100);
        Expect(knight.ModelName == "c3100" && knight.NPCParamID == 31000010 && knight.ThinkParamID == 31000000, "the knight's stand-in params are wrong");
        Expect(knight.Position == new Vector3(1, 2, 3) && knight.CollisionPartName == collision, "the knight isn't where DS3 had it");
        Expect(enemies.Select(e => e.Name).Distinct().Count() == 3 && enemies.All(e => e.Name.StartsWith(e.ModelName + "_")), "part names must be unique model_instance");
        Expect(enemies.Any(e => e.EntityID == 0 && e.CollisionPartName == ""), "a collision that isn't in the map should be cleared");
        var boss = enemies.First(e => e.EntityID == 45000800);
        Expect(boss.ModelName is "c4810" or "c4380" or "c4740", $"the DS3 boss got {boss.ModelName}, not a boss");
        Expect(layout.Msb.Models.Enemies.Select(m => m.Name).OrderBy(n => n).SequenceEqual(enemies.Select(e => e.ModelName).Distinct().OrderBy(n => n)),
            "every placed model needs a model entry");
        var back = MSBE.Read(layout.Msb.Write());
        Expect(back.Parts.Enemies.Count == 3 && back.Parts.Enemies.Any(e => e.EntityID == 45000800 && e.NPCParamID == boss.NPCParamID), "enemies don't read back");
    }

    private static void PeoplePlacement()
    {
        static EnemyPort.ErEnemy Human(int npc, string name, int look, int talk) => new("c0000", npc, npc + 1, name, false, "m10_00_00_00",
            new MSBE.Part.Enemy { Name = $"c0000_{npc}", ModelName = "c0000", NPCParamID = npc, ThinkParamID = npc + 1, CharaInitID = look, TalkID = talk, BackupEventAnimID = 30000 });
        var teams = new Dictionary<int, int> { [43000010] = 6, [31000010] = 6, [45000010] = 6, [100] = 8, [200] = 8, [300] = 6, [400] = 2, [500] = 6 };
        var notes = new List<string>();
        var people = EnemyPort.SortPeople(
            [(Human(100, "Merchant", 23000, 100000), true), (Human(200, "Sorcerer", 23010, 200000), true), (Human(300, "Carian Knight", 23020, 0), false),
             (Human(400, "Summoned Spirit", 23030, 0), false), (Human(500, "Exile", 23040, 0), false)],
            SyntheticCatalog().Where(c => !c.Boss), id => teams.GetValueOrDefault(id, -1), notes);
        Expect(people.Friendly.Count == 2 && people.Hostile.Count == 2, $"{people.Friendly.Count} friendly and {people.Hostile.Count} hostile, expected 2 and 2");
        Expect(people.Hostile.All(h => h.Name != "Summoned Spirit"), "a human who isn't on an enemy team must not stand in for a hostile one");
        var greirat = new EnemyPort.Ds3Enemy("c0000", 22000, "Greirat", false, true);
        Expect(people.Choose(greirat, talks: true) is { } a && ReferenceEquals(a, people.Choose(greirat, talks: true)) && people.Friendly.Contains(a), "a talker gets the same friendly person every time");
        Expect(people.Choose(greirat, talks: false) is { } h && people.Hostile.Contains(h), "a human without a talk script gets a hostile one");
        Expect(people.Choose(new("c1400", 1, "Fire Keeper", false, true), true) is null, "only the human body gets a human");
        Expect(Enumerable.Range(0, 40).Select(i => people.Choose(new("c0000", 22000 + i * 10, "x", false, true), true)!.NpcParam).Distinct().Count() == 2, "both friendly people are used");
        Expect(new EnemyPort.People([], []).Choose(greirat, true) is null, "no people, nobody placed");

        var ds3 = SyntheticDs3();
        var collision = ds3.Parts.Collisions[0].Name;
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c0000" });
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c1400" });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c0000_0000", ModelName = "c0000", EntityID = 4000700, NPCParamID = 22000, TalkID = 22000, CollisionName = collision, Position = new Vector3(4, 5, 6) });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c0000_0001", ModelName = "c0000", EntityID = 4000710, NPCParamID = 23000, CollisionName = collision });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1400_0000", ModelName = "c1400", EntityID = 4000720, NPCParamID = 140000, TalkID = 14000, CollisionName = collision });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1240_0000", ModelName = "c1240", EntityID = 4000100, NPCParamID = 124000, CollisionName = collision });
        EnemyPort.Ds3Enemy Describe(MSB3.Part.Enemy e) => new(e.ModelName, e.NPCParamID, e.ModelName == "c1240" ? "Lothric Knight" : e.Name, false, e.ModelName == "c0000" || e.TalkID > 0);
        var chooser = new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto);

        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var table = new List<string>();
        var result = EnemyPort.Place(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), chooser, Describe, null, table, null, people);
        var placed = layout.Msb.Parts.Enemies;
        Expect(result.Placed == 3 && result.People == 2 && result.Npcs == 1, $"placed {result.Placed}, people {result.People}, left out {result.Npcs}; expected 3, 2 and 1 (the Fire Keeper isn't ported)");
        var talker = placed.First(e => e.EntityID == 45000700);
        Expect(talker.ModelName == "c0000" && talker.TalkID == 0 && talker.CharaInitID is 23000 or 23010 && talker.BackupEventAnimID == -1,
            $"the talker: model {talker.ModelName}, talk {talker.TalkID}, look {talker.CharaInitID}");
        Expect(talker.Position == new Vector3(4, 5, 6) && talker.CollisionPartName == collision, "the talker isn't where DS3 had them");
        var fighter = placed.First(e => e.EntityID == 45000710);
        Expect(fighter.CharaInitID is 23020 or 23040 && fighter.NPCParamID is 300 or 500, $"the hostile human got look {fighter.CharaInitID}");
        Expect(placed.First(e => e.EntityID == 45000100).CharaInitID == -1, "an ordinary enemy has no look");
        Expect(layout.Msb.Models.Enemies.Any(m => m.Name == "c0000"), "the human body needs a model entry");
        Expect(table.Count == 1, "darksign_enemies.tsv is the randomizer's list of enemy spots: people must not be in it");
        Expect(result.Notes.Any(n => n.StartsWith("people: ") && n.Contains("(silent)") && n.Contains("(hostile)")), "the report must name the people");
        var back = MSBE.Read(layout.Msb.Write());
        Expect(back.Parts.Enemies.Count == 3 && back.Parts.Enemies.Any(e => e.EntityID == 45000700 && e.CharaInitID == talker.CharaInitID), "people don't read back");

        // Ported DS3 characters with a talk script stand where DS3 had them, silent.
        var ported = EnemyPort.ChrMap.Parse(["chr\tc1400\tc7001\t70010000\t70010000\tFire Keeper"]);
        var layout2 = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result2 = EnemyPort.Place(ds3, layout2.Msb, Ds3Id, ErId, new MsbPort.Options(), new EnemyPort.Chooser(SyntheticCatalog(), ported, EnemyPort.Method.Auto), Describe, null, null, null, people);
        Expect(result2.People == 3 && result2.Npcs == 0 && layout2.Msb.Parts.Enemies.Any(e => e.EntityID == 45000720 && e.ModelName == "c7001" && e.TalkID == 0 && e.CharaInitID == -1),
            "a ported talker (Fire Keeper) should be placed, silent");

        // Off: nobody.
        var layout3 = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result3 = EnemyPort.Place(ds3, layout3.Msb, Ds3Id, ErId, new MsbPort.Options(), chooser, Describe);
        Expect(result3.People == 0 && result3.Npcs == 3 && layout3.Msb.Parts.Enemies.Count == 1, "without people, DS3's people are left out as before");
    }
}
