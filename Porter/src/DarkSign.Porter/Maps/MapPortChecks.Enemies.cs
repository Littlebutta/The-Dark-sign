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
}
