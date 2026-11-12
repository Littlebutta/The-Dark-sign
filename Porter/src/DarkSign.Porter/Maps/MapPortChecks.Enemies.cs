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
        yield return ("people: people without body armour stay out of the pools; DS3 summons and invaders aren't placed", PeopleLooks);
        yield return ("enemies, objects: another state of the map (off DS3's layer bit 0, m40's Untended Graves) is left out, with a switch to keep it", OtherStateLeftOut);
        yield return ("people: those DS3 parks at a holding spot stand where its scripts warp them, parameterised events included", PeopleSeated);
        yield return ("people: DS3 humans with a DS3 look stand as an Elden Ring person wearing it; the rest stay out without --er-people", PeopleArmourPlacement);
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
        // DS3 places the Cemetery of Ash hollows (c1430) with the Hollow Soldiers' AI rows (110005): ported with c1100.
        var thinks = EnemyPort.ChrMap.Parse([
            "chr\tc1430\tc7042\t70420000\t70429010\tHollow (Cemetery of Ash)",
            "think\t143030\tc7042\t70429030\tHollow (Cemetery of Ash)",
            "think\t110005\tc7006\t70069005\tHollow Soldier",
        ]);
        var thinkChooser = new EnemyPort.Chooser(SyntheticCatalog(), thinks, EnemyPort.Method.Auto);
        var hollow = thinkChooser.Choose(new("c1430", 143000, "Hollow", false, false))!;
        Expect(thinkChooser.ThinkFor(hollow, 143030) == 70429030 && thinkChooser.ThinkFor(hollow, 110005) == 70069005
               && thinkChooser.ThinkFor(hollow, 999999) == 70429010 && thinkChooser.BorrowedThinks == 1,
            "a placement keeps its own model's ported think row, borrows one ported with another character, else gets the character's");
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

    private static void PeopleLooks()
    {
        static EnemyPort.ErEnemy Human(int npc, string name) => new("c0000", npc, npc + 1, name, false, "m10_00_00_00",
            new MSBE.Part.Enemy { Name = $"c0000_{npc}", ModelName = "c0000", NPCParamID = npc, CharaInitID = npc + 5, TalkID = 1 });
        var notes = new List<string>();
        var people = EnemyPort.SortPeople(
            [(Human(100, "Merchant"), true), (Human(200, "Boc"), true), (Human(300, "Nobody"), true)],
            SyntheticCatalog().Where(c => !c.Boss), _ => 6, notes,
            who => who.NpcParam == 200 ? "no body armour" : who.NpcParam == 300 ? "no CharaInitParam row" : null);
        Expect(people.Friendly.Count == 1 && people.Friendly[0].NpcParam == 100, $"pool: {string.Join(", ", people.Friendly.Select(f => f.Name))}");
        Expect(notes.Any(n => n.Contains("no body armour x1 (Boc (200/205))") && n.Contains("no CharaInitParam row x1")), "the left-out people are logged: " + string.Join(" | ", notes));
        foreach (var (name, phantom) in new[] { ("Hawkwood the Deserter - White Phantom", true), ("Holy Knight Hodrick - Mad Phantom - Undead Settlement", true),
                     ("Dark Spirit Ringfinger Leonhard", true), ("Fire Keeper - Firelink Shrine", false), ("Patrol Leader - Collective Patrol", false) })
            Expect(EnemyPort.People.IsPhantom(name) == phantom, $"{name}: phantom {EnemyPort.People.IsPhantom(name)}");
        // No CharaInitParam to look in: nothing rejected (no data, no guess).
        Expect(EnemyPort.PersonLooksWrong(null, 23420) is null, "no params, nothing rejected");
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
        Expect(result.Placed == 4 && result.People == 3 && result.Npcs == 0, $"placed {result.Placed}, people {result.People}, left out {result.Npcs}; expected 4, 3 and 0 (the unported Fire Keeper gets a person)");
        Expect(placed.First(e => e.EntityID == 45000720).ModelName == "c0000", "the unported Fire Keeper stands in as an Elden Ring person");
        var talker = placed.First(e => e.EntityID == 45000700);
        Expect(talker.ModelName == "c0000" && talker.TalkID == 0 && talker.CharaInitID is 23000 or 23010 && talker.BackupEventAnimID == -1,
            $"the talker: model {talker.ModelName}, talk {talker.TalkID}, look {talker.CharaInitID}");
        Expect(talker.Position == new Vector3(4, 5, 6) && talker.CollisionPartName == collision, "the talker isn't where DS3 had them");
        var fighter = placed.First(e => e.EntityID == 45000710);
        Expect(fighter.CharaInitID is 23020 or 23040 && fighter.NPCParamID is 300 or 500, $"the hostile human got look {fighter.CharaInitID}");
        Expect(placed.First(e => e.EntityID == 45000100).CharaInitID == -1, "an ordinary enemy has no look");
        Expect(placed.First(e => e.EntityID == 45000720).CharaInitID is 23000 or 23010, "a person standing in for an own-body talker keeps their look");
        Expect(layout.Msb.Models.Enemies.Any(m => m.Name == "c0000"), "the human body needs a model entry");
        Expect(table.Count == 1, "darksign_enemies.tsv is the randomizer's list of enemy spots: people must not be in it");
        Expect(result.Notes.Any(n => n.StartsWith("people: ") && n.Contains("(silent)") && n.Contains("(hostile)")), "the report must name the people");
        var back = MSBE.Read(layout.Msb.Write());
        Expect(back.Parts.Enemies.Count == 4 && back.Parts.Enemies.Any(e => e.EntityID == 45000700 && e.CharaInitID == talker.CharaInitID), "people don't read back");

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

        // The default (no Elden Ring stand-ins): DS3's own people only, the ported Fire Keeper stands, the humans don't.
        var layout4 = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result4 = EnemyPort.Place(ds3, layout4.Msb, Ds3Id, ErId, new MsbPort.Options(), new EnemyPort.Chooser(SyntheticCatalog(), ported, EnemyPort.Method.Auto), Describe);
        Expect(result4.People == 1 && result4.Npcs == 2 && layout4.Msb.Parts.Enemies.All(e => e.ModelName != "c0000")
               && layout4.Msb.Parts.Enemies.Any(e => e.EntityID == 45000720 && e.ModelName == "c7001"),
            $"default: people {result4.People}, left out {result4.Npcs}; only the ported Fire Keeper should stand");
        // --no-people: not even the ported Fire Keeper.
        var layout5 = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result5 = EnemyPort.Place(ds3, layout5.Msb, Ds3Id, ErId, new MsbPort.Options(), new EnemyPort.Chooser(SyntheticCatalog(), ported, EnemyPort.Method.Auto), Describe,
            placePeople: false);
        Expect(result5.People == 0 && result5.Npcs == 3, "--no-people leaves out ported people too");

        // DS3's logic characters (platoon leader c0100, target dummies) get no Elden Ring enemy.
        Expect(EnemyPort.People.IsLogic("c0100", "Patrol Leader - Collective Patrol") && EnemyPort.People.IsLogic("c1240", "City Map Sniper Platoon target dummy character")
               && EnemyPort.People.IsLogic("c1000", "x") && !EnemyPort.People.IsLogic("c1240", "Lothric Knight"), "logic characters");
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c0100_0000", ModelName = "c0100", EntityID = 4000730, NPCParamID = 10000, CollisionName = collision });
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c0100" });
        var layout6 = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result6 = EnemyPort.Place(ds3, layout6.Msb, Ds3Id, ErId, new MsbPort.Options(), chooser, Describe);
        Expect(layout6.Msb.Parts.Enemies.All(e => e.EntityID != 45000730) && result6.Notes.Any(n => n.Contains("logic characters left out")),
            "the invisible platoon leader must not become a visible enemy");
    }

    private static void OtherStateLeftOut()
    {
        var ds3 = SyntheticDs3();
        foreach (var piece in ds3.Parts.MapPieces)
            piece.MapStudioLayer = 0xFFFFFFFF;
        var collision = ds3.Parts.Collisions[0].Name;
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c1240" });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1240_0000", ModelName = "c1240", EntityID = 4000100, NPCParamID = 124000, CollisionName = collision, MapStudioLayer = 0xFFFFFFF9 });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c1240_0001", ModelName = "c1240", EntityID = 4000300, NPCParamID = 124000, CollisionName = collision, MapStudioLayer = 0xFFFFFFFE });
        ds3.Models.Objects.Add(new MSB3.Model.Object { Name = "o302590" });
        ds3.Parts.Objects.Add(new MSB3.Part.Object { Name = "o302590_0000", ModelName = "o302590", EntityID = 4001500, MapStudioLayer = 1 });
        ds3.Parts.Objects.Add(new MSB3.Part.Object { Name = "o302590_0001", ModelName = "o302590", EntityID = 4001501, MapStudioLayer = 2 });
        EnemyPort.Ds3Enemy Describe(MSB3.Part.Enemy e) => new(e.ModelName, e.NPCParamID, e.EntityID == 4000300 ? "Black Knight" : "Lothric Knight", false, false);
        var assets = new Dictionary<string, ObjectPort.Asset>(StringComparer.OrdinalIgnoreCase)
            { ["o302590"] = new() { Ds3Model = "o302590", Index = 7, Name = "AEG920_007", Converted = true } };

        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var enemies = EnemyPort.Place(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto), Describe);
        var objects = ObjectPort.PlaceParts(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), m => assets.GetValueOrDefault(m));
        Expect(enemies.Placed == 1 && layout.Msb.Parts.Enemies.Single().EntityID == 45000100, $"{enemies.Placed} enemies placed; the other state's Black Knight must be left out");
        Expect(enemies.Notes.Any(n => n.Contains("another state") && n.Contains("Black Knight x1")), "the report names what was left out");
        Expect(objects.Placed == 1 && layout.Msb.Parts.Assets.Single().EntityID == 45001500 && objects.Notes.Any(n => n.Contains("another state")), $"objects: {objects.Summary}");

        var all = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var keepAll = new MsbPort.Options { AllLayers = true };
        Expect(EnemyPort.Place(ds3, all.Msb, Ds3Id, ErId, keepAll, new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto), Describe).Placed == 2
               && ObjectPort.PlaceParts(ds3, all.Msb, Ds3Id, ErId, keepAll, m => assets.GetValueOrDefault(m)).Placed == 2, "--all-layers keeps both states");

        // A map whose pieces don't use bit 0 for the base state keeps everything.
        foreach (var piece in ds3.Parts.MapPieces)
            piece.MapStudioLayer = 2;
        var other = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        Expect(EnemyPort.Place(ds3, other.Msb, Ds3Id, ErId, new MsbPort.Options(), new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto), Describe).Placed == 2,
            "layers are only read where bit 0 marks the base state");
    }

    private static void PeopleSeated()
    {
        var ds3 = SyntheticDs3();
        var collision = ds3.Parts.Collisions[0].Name;
        var hold = new Vector3(273, -98, 603);
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c0000" });
        foreach (var (name, entity) in new[] { ("c0000_0025", 4000750), ("c0000_0024", 4000745), ("c0000_0018", 4000735) })
            ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = name, ModelName = "c0000", EntityID = entity, NPCParamID = 25000, TalkID = 400140, CollisionName = "h006100_0000", Position = hold });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c0000_0030", ModelName = "c0000", EntityID = 4000785, NPCParamID = 25300, TalkID = 400190, CollisionName = collision, Position = new Vector3(286, -48, 607) });
        ds3.Regions.Events.Add(new MSB3.Region.Event { Name = "handmaid seat", EntityID = 4004750, Position = new Vector3(270, -56, 600), Rotation = new Vector3(0, 45, 0) });
        ds3.Regions.Events.Add(new MSB3.Region.Event { Name = "seat 745", EntityID = 4004745, Position = new Vector3(260, -56, 605) });
        ds3.Regions.Events.Add(new MSB3.Region.Event { Name = "seat 785", EntityID = 4004785, Position = new Vector3(0, 0, 0) });
        // A second version of the handmaid, warped to the same seat.
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c0000_0026", ModelName = "c0000", EntityID = 4000751, NPCParamID = 25001, TalkID = 400141, CollisionName = "h006100_0000", Position = hold });

        // Map script: one direct warp (handmaid), one parameterised event started for 4000745, one for the person not held.
        var script = new EMEVD { Format = EMEVD.Game.DarkSouls3 };
        var direct = new EMEVD.Event(14005520, EMEVD.Event.RestBehaviorType.Default);
        direct.Instructions.Add(new EMEVD.Instruction(2004, 42, new object[] { 4000750, (byte)1, (byte)0, (short)0, 4004750, -1, 4000750 }));
        direct.Instructions.Add(new EMEVD.Instruction(2004, 42, new object[] { 4000751, (byte)1, (byte)0, (short)0, 4004750, -1, 4000751 }));
        var shared = new EMEVD.Event(14005500, EMEVD.Event.RestBehaviorType.Default);
        shared.Instructions.Add(new EMEVD.Instruction(2004, 42, new object[] { 0, (byte)1, (byte)0, (short)0, 0, -1, 0 }));
        shared.Parameters.Add(new EMEVD.Parameter(0, 0, 0, 4));
        shared.Parameters.Add(new EMEVD.Parameter(0, 8, 4, 4));
        var constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        constructor.Instructions.Add(new EMEVD.Instruction(2000, 0, new object[] { 0, 14005500u, 4000745, 4004745 }));
        constructor.Instructions.Add(new EMEVD.Instruction(2000, 0, new object[] { 1, 14005500u, 4000785, 4004785 }));
        script.Events.AddRange([constructor, direct, shared]);
        var warps = EnemyPort.EventWarps([EMEVD.Read(script.Write())]);
        Expect(warps.GetValueOrDefault(4000750) == 4004750 && warps.GetValueOrDefault(4000745) == 4004745 && warps.GetValueOrDefault(4000785) == 4004785,
            $"warps read: {string.Join(", ", warps.Select(w => $"{w.Key}->{w.Value}"))}");

        var seats = EnemyPort.Seats(ds3, warps);
        Expect(seats.Count == 3 && !seats.ContainsKey("c0000_0030"), "only people at the holding spot get a seat");
        Expect(seats["c0000_0025"].Position == new Vector3(270, -56, 600) && seats["c0000_0025"].Rotation == new Vector3(0, 45, 0), "the seat is the warp destination");
        Expect(seats["c0000_0025"].Collision == collision, $"the seat stands on the collision of a character near it ({seats["c0000_0025"].Collision})");

        var people = new EnemyPort.People([new("c0000", 100, 101, "Merchant", false, "m10_00_00_00", new MSBE.Part.Enemy { ModelName = "c0000", CharaInitID = 23000 })], []);
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result = EnemyPort.Place(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto),
            e => new(e.ModelName, e.NPCParamID, e.Name, false, true), null, null, null, people, seats);
        var handmaid = layout.Msb.Parts.Enemies.Single(e => e.EntityID == 45000750);
        Expect(handmaid.Position == new Vector3(270, -56, 600) && handmaid.CollisionPartName == collision, $"seated at {handmaid.Position}");
        Expect(layout.Msb.Parts.Enemies.Single(e => e.EntityID == 45000735).Position == hold, "a held person with no warp stays where DS3 put them");
        Expect(result.Notes.Any(n => n.Contains("2 moved from DS3's holding spot") && n.Contains("1 other versions")), $"the report counts the seated people: {string.Join(" | ", result.Notes)}");
        Expect(layout.Msb.Parts.Enemies.All(e => e.EntityID != 45000751), "the second version of a seated person is left out");
        Expect(MSBE.Read(layout.Msb.Write()).Parts.Enemies.Count == layout.Msb.Parts.Enemies.Count, "reads back");
    }

    private static void PeopleArmourPlacement()
    {
        var ds3 = SyntheticDs3();
        ds3.Models.Enemies.Add(new MSB3.Model.Enemy { Name = "c0000" });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c0000_0033", ModelName = "c0000", EntityID = 4000760, NPCParamID = 25100, CharaInitID = 25100, TalkID = 400170, CollisionName = "h000100_0000" });
        ds3.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = "c0000_0012", ModelName = "c0000", EntityID = 4000500, NPCParamID = 27300, CharaInitID = 27300, CollisionName = "h000100_0000" });
        var armour = PeopleArmour.WithModels((5800, 5000), (7000, 5001));
        armour.AddLook(new PeopleArmour.Look(25100, "Hawkwood", 1, [new(70000000, 5, 7000, "Chain Helm"), new(58001000, 2, 5800, "Undead Legion Armor"), null, null]));
        Expect(armour.LookFor(25100) == PeopleArmour.CharaInitBase + 25100 && armour.LookFor(27300) is null, "only planned rows have a look");
        Expect(armour.Rename("BD_M_5800_a") == "BD_M_5000_a" && armour.Rename("hd_f_7000_n") == "HD_F_5001_n" && armour.Rename("BD_M_1234_a") == "BD_M_1234_a",
            "DS3 set names move to their new numbers, others stay");
        Expect(PeopleArmour.WithModels((5800, 5000), (5000, 5001)).Rename("BD_M_5800_a") == "BD_M_5000_a", "a new number that is another DS3 set's isn't renamed twice");
        Expect(PeopleArmour.PartsPath("bd", 'm', 5000, true) == "/parts/bd_m_5000_l.partsbnd.dcx", "Elden Ring parts path");
        Expect(PeopleArmour.PeopleIn([ds3]).Order().SequenceEqual([25100, 27300]), "the c0000 people's CharaInit rows are read from the map");

        var people = new EnemyPort.People([new("c0000", 100, 101, "Merchant", false, "m10_00_00_00", new MSBE.Part.Enemy { ModelName = "c0000", CharaInitID = 23000 })],
            [new("c0000", 200, 201, "Bandit", false, "m10_00_00_00", new MSBE.Part.Enemy { ModelName = "c0000", CharaInitID = 24000 })]);
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var result = EnemyPort.Place(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto),
            e => new(e.ModelName, e.NPCParamID, e.Name, false, true), null, null, null, people, null, armour: armour, erStandIns: false);
        var hawkwood = layout.Msb.Parts.Enemies.Single(e => e.EntityID == 45000760);
        Expect(hawkwood.CharaInitID == PeopleArmour.CharaInitBase + 25100 && hawkwood.NPCParamID == 100 && hawkwood.TalkID == 0,
            $"Hawkwood wears his DS3 look on the Elden Ring person (CharaInit {hawkwood.CharaInitID}, NpcParam {hawkwood.NPCParamID})");
        Expect(armour.StandIns.GetValueOrDefault(PeopleArmour.CharaInitBase + 25100) == 23000, "the look remembers the stand-in row it copies");
        Expect(layout.Msb.Parts.Enemies.All(e => e.EntityID != 45000500), "a human with no DS3 look stays out (no Elden Ring stand-in by default)");
        Expect(result.Notes.Any(n => n.Contains("1 DS3 people wear their DS3 armour")), $"reported: {string.Join(" | ", result.Notes)}");

        // DS3's weapons in the person's row (the Champion's Gravetender held Inquisitor Ghiza's up to 1.0.403).
        var charaDef = new PARAMDEF { ParamType = "CHARA_INIT_PARAM", Fields = [] };
        foreach (var (field, erType, _) in PeopleArmour.WeaponSlots)
        {
            charaDef.Fields.Add(new PARAMDEF.Field(charaDef, PARAMDEF.DefType.s32, field) { Default = -1 });
            if (erType is not null)
                charaDef.Fields.Add(new PARAMDEF.Field(charaDef, PARAMDEF.DefType.u8, erType));
        }
        foreach (var (field, erType) in PeopleArmour.ErOnlySlots)
        {
            charaDef.Fields.Add(new PARAMDEF.Field(charaDef, PARAMDEF.DefType.s32, field) { Default = -1 });
            charaDef.Fields.Add(new PARAMDEF.Field(charaDef, PARAMDEF.DefType.u8, erType));
        }
        var standIn = new PARAM.Row(PeopleArmour.CharaInitBase + 27200, "Gravetender", charaDef);
        void Put(string f, int v) => standIn.Cells.First(c => c.Def.InternalName == f).Value = Convert.ChangeType(v, standIn.Cells.First(c => c.Def.InternalName == f).Value.GetType());
        int Get(string f) => Convert.ToInt32(standIn.Cells.First(c => c.Def.InternalName == f).Value);
        Put("equip_Wep_Right", 9000000); Put("wepParamType_Right1", 1); Put("equip_Subwep_Right", 9010000); Put("equip_Wep_Left", 9020000);
        Put("equip_Subwep_Left", 9030000); Put("equip_Subwep_Right3", 9040000); Put("equip_Arrow", 50000000);
        var gravetender = new PeopleArmour.Look(27200, "Champion's Gravetender", 0, [null, null, null, null],
            [6260000, -1, 21290000 + 5, 2000000, 400000, -1, -1, -1]);
        var mapped = new Dictionary<int, int> { [6260000] = 80010000, [21290005] = 30100000 };
        var arming = PeopleArmour.WithModels();
        arming.Arm(standIn, gravetender, ds3 => mapped.TryGetValue(ds3, out var er) ? er : null);
        Expect(Get("equip_Wep_Right") == 80010000 && Get("wepParamType_Right1") == 0, "a mapped DS3 weapon is held as a plain weapon row");
        Expect(Get("equip_Subwep_Right") == -1, "a slot DS3 leaves empty is emptied");
        Expect(Get("equip_Wep_Left") == 30100000, "the mapper gets DS3's own ID (level and infusion included)");
        Expect(Get("equip_Subwep_Left") == 9030000, "a DS3 weapon with no Elden Ring match keeps the stand-in's");
        Expect(Get("equip_Subwep_Right3") == -1, "Elden Ring's third slots are emptied (DS3 has none)");
        Expect(Get("equip_Arrow") == 50000000, "unmapped ammunition keeps the stand-in's");
        Expect(arming.WeaponCounts == (2, 1, 1) && arming.Notes.Any(n => n.Contains("2000000") && n.Contains("Gravetender")), $"counted and named: {arming.WeaponCounts} {string.Join(" | ", arming.Notes)}");
        var unarmed = new PARAM.Row(1, "", charaDef);
        PeopleArmour.WithModels().Arm(unarmed, armour.Looks[25100], _ => 1);
        Expect(unarmed.Cells.All(c => Convert.ToInt32(c.Value) is -1 or 0), "a look whose DS3 weapons weren't read leaves the row alone");

        // Which body parts a piece hides: the nearest Elden Ring row's flags, not the slot's most common (a closed helm's),
        // which hid Hawkwood's head under his open Chain Helm in 1.0.417.
        var protDef = new PARAMDEF { ParamType = "EQUIP_PARAM_PROTECTOR_ST", Fields = [] };
        protDef.Fields.Add(new PARAMDEF.Field(protDef, PARAMDEF.DefType.u8, "equipModelCategory"));
        protDef.Fields.Add(new PARAMDEF.Field(protDef, PARAMDEF.DefType.u16, "equipModelId"));
        for (int i = 0; i < PeopleArmour.HideFlags; i++)
            protDef.Fields.Add(new PARAMDEF.Field(protDef, PARAMDEF.DefType.u8, $"invisibleFlag_SexVer{i:D2}"));
        var ds3Def = new PARAMDEF { ParamType = "EQUIP_PARAM_PROTECTOR_ST", Fields = [] };
        for (int i = 0; i < 98; i++)
            ds3Def.Fields.Add(new PARAMDEF.Field(ds3Def, PARAMDEF.DefType.u8, $"Mask{i:D2}"));
        PARAM.Row Prot(int id, string name, int category, params int[] hides)
        {
            var row = new PARAM.Row(id, name, protDef);
            row.Cells.First(c => c.Def.InternalName == "equipModelCategory").Value = (byte)category;
            row.Cells.First(c => c.Def.InternalName == "equipModelId").Value = (ushort)(category == 5 ? 1000 + id % 1000 : 1);
            foreach (var h in hides)
                row.Cells.First(c => c.Def.InternalName == $"invisibleFlag_SexVer{h:D2}").Value = (byte)1;
            return row;
        }
        int[] closed = [0, 1, 2, 3, 4, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69], coif = [3, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69];
        var erProt = new PARAM { ParamType = "EQUIP_PARAM_PROTECTOR_ST", Rows =
            [Prot(40000, "Iron Helmet", 5, closed), Prot(50000, "Kaiden Helm", 5, closed), Prot(60000, "Drake Knight Helm", 5, closed),
             Prot(1100000, "Chain Coif", 5, [.. coif, 23]), Prot(1200000, "Crown", 5, 60), Prot(40100, "Leather Armor", 2, 7, 11)] };
        var chainHelm = new PARAM.Row(70000000, "Chain Helm", ds3Def);
        foreach (var h in coif.Append(31).Append(32).Append(35))
            chainHelm.Cells.First(c => c.Def.InternalName == $"Mask{h:D2}").Value = (byte)1;
        var ds3Hides = PeopleArmour.Ds3Hides(chainHelm);
        Expect(ds3Hides is { Length: PeopleArmour.HideFlags } && ds3Hides[3] == 1 && ds3Hides[0] == 0 && ds3Hides[60] == 1, "DS3's Mask flags are read");
        Expect(PeopleArmour.ProtectorTemplate(erProt, 5)?.ID == 40000, "without DS3 flags: the slot's most common pattern, lowest ID");
        Expect(PeopleArmour.ProtectorTemplate(erProt, 5, ds3Hides, out var off)?.ID == 1100000 && off == 4,
            $"an open DS3 helm hides the body as the nearest Elden Ring row does (got {PeopleArmour.ProtectorTemplate(erProt, 5, ds3Hides)?.Name}, {off} off)");
        Expect(PeopleArmour.ProtectorTemplate(erProt, 5, PeopleArmour.ErHides(Prot(1, "", 5, closed)))?.ID == 40000, "a closed helm still gets the closed pattern");
        Expect(PeopleArmour.ProtectorTemplate(erProt, 2, ds3Hides)?.ID == 40100, "only rows of the piece's own slot");
        erProt.Rows.Add(Prot(PeopleArmour.ProtectorBase + 1, "ours", 5, coif));
        Expect(PeopleArmour.ProtectorTemplate(erProt, 5, ds3Hides)?.ID == 1100000, "rows the port added are never a template");

        // --er-people: the stand-ins' own looks, no DS3 armour.
        var erLayout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        EnemyPort.Place(ds3, erLayout.Msb, Ds3Id, ErId, new MsbPort.Options(), new EnemyPort.Chooser(SyntheticCatalog(), null, EnemyPort.Method.Auto),
            e => new(e.ModelName, e.NPCParamID, e.Name, false, true), null, null, null, people, null);
        Expect(erLayout.Msb.Parts.Enemies.Count(e => e.CharaInitID is 23000 or 24000) == 2, "with Elden Ring stand-ins both people stand in their own looks");
    }
}
