using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> CoverageChecks()
    {
        yield return ("coverage: a map that loses a share of its pieces, or fails where the others don't, is warned about by name", Coverage);
        yield return ("collision: an empty DS3 collision file (no root object) is reported as empty, not as a failed conversion", EmptyCollision);
        yield return ("collision: a part whose DS3 h file is empty keeps its l collision (Painted World enemies died on load), --drop-empty-collisions leaves it out", EmptySideParts);
    }

    private static void Coverage()
    {
        // The 1.0.383 numbers: Undead Settlement lost 714 of 3782 pieces, the Painted World 103 of 5889, map 1 none.
        var map1 = new MapCoverage.Row(MapId.Parse("m40_00_00_00"), MapId.Parse("m45_00_00_00"), "Firelink Shrine / Cemetery of Ash")
            { PiecesDs3 = 4055, PiecesWritten = 4043, PiecesNoModel = 12, CollisionsDs3 = 45, CollisionsWritten = 45 };
        var map3 = new MapCoverage.Row(MapId.Parse("m31_00_00_00"), MapId.Parse("m45_02_00_00"), "Undead Settlement")
            { PiecesDs3 = 3782, PiecesWritten = 3063, PiecesNoModel = 714, CollisionsDs3 = 93, CollisionsWritten = 93 };
        var painted = new MapCoverage.Row(MapSlots.PaintedWorld, MapId.Parse("m19_70_00_00"), MapSlots.PaintedWorldName)
            { PiecesDs3 = 5889, PiecesWritten = 5786, PiecesNoModel = 103, CollisionsDs3 = 187, CollisionsWritten = 159, CollisionFilesEmpty = 29,
              Failed = ["objects: FAILED (test); the map has no objects"] };
        var warnings = MapCoverage.Warnings([map1, map3, painted]);
        Expect(warnings.Any(w => w.StartsWith("m45_02_00_00 (Undead Settlement): 714 of 3782") && w.Contains("19%")),
            "a map losing 19% of its pieces is named: " + string.Join(" / ", warnings));
        Expect(!warnings.Any(w => w.StartsWith("m45_00_00_00")), "map 1's 12 of 4055 (0.3%) is not warned about");
        Expect(!warnings.Any(w => w.StartsWith("m19_70_00_00 (Painted World of Ariandel): 103")), "the Painted World's 103 of 5889 (1.7%) is under the 5% line");
        Expect(!warnings.Any(w => w.Contains("collision files")), "empty DS3 collision files are not a warning");
        Expect(warnings.Any(w => w.StartsWith("steps that FAILED: m19_70_00_00") && w.Contains("0 on m45_00_00_00, m45_02_00_00")),
            "a step that failed on one map only is named with the maps it didn't fail on");
        Expect(warnings.Any(w => w.Contains("objects: FAILED (test)")), "the failed line itself is carried");

        var tsv = MapCoverage.Tsv([map1, map3, painted]).TrimEnd('\n').Split('\n');
        Expect(tsv.Length == 4 && tsv.All(l => l.Split('\t').Length == tsv[0].Split('\t').Length), "one TSV row per map, every row as wide as the header");
        Expect(tsv[2].Split('\t')[6] == "19%", $"share column: {tsv[2]}");
        var summary = MapCoverage.Summary([map1, map3, painted]).ToList();
        Expect(summary.Count(l => l.StartsWith("  m")) == 3 && summary.Any(l => l.StartsWith("  WARN ")), "summary: a line per map, then the warnings");
        Expect(MapCoverage.FailedLines("m31 -> m45_02\n  regions: FAILED (x); no regions\n  objects: 5 placed\n").SequenceEqual(["regions: FAILED (x); no regions"]), "FAILED lines are picked out");
        Expect(MapCoverage.Warnings([map3]).Count == 1, "one map alone: only the share warning (nothing to compare with)");
    }

    private static void EmptySideParts()
    {
        // The Painted World's shape: h502300's h file is an empty placeholder, its l file holds the collision; h510300 is
        // empty in both; h000100 is ordinary.
        var ported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "000100", "502300" };
        var copy = new Dictionary<string, MapCollisions.Result>
        {
            ["h"] = new("h", ported, Empty: new(StringComparer.OrdinalIgnoreCase) { "510300" }, Borrowed: new(StringComparer.OrdinalIgnoreCase) { "502300" }),
            ["l"] = new("l", new(ported, StringComparer.OrdinalIgnoreCase), Empty: new(StringComparer.OrdinalIgnoreCase) { "510300" }),
        };
        var models = new[] { "h000100", "h502300", "h510300" };
        var left = MapPorter.CollisionModelsLeftOut(models, copy, MapCollisions.EmptySideMethod.Copy).ToList();
        Expect(left.Count == 1 && left[0] == ("h510300", true), $"copy: only the model empty on both sides is left out: {string.Join(", ", left)}");

        // --empty-collision-alone: h has nothing for 502300, l does; the part still stays.
        var alone = new Dictionary<string, MapCollisions.Result>
        {
            ["h"] = new("h", new(StringComparer.OrdinalIgnoreCase) { "000100" }, Empty: new(StringComparer.OrdinalIgnoreCase) { "502300", "510300" }),
            ["l"] = new("l", new(ported, StringComparer.OrdinalIgnoreCase), Empty: new(StringComparer.OrdinalIgnoreCase) { "510300" }),
        };
        left = MapPorter.CollisionModelsLeftOut(models, alone, MapCollisions.EmptySideMethod.Alone).ToList();
        Expect(left.Count == 1 && left[0].Model == "h510300", $"alone: the part with l collision stays: {string.Join(", ", left)}");
        left = MapPorter.CollisionModelsLeftOut(models, alone, MapCollisions.EmptySideMethod.Drop).ToList();
        Expect(left.Select(l => l.Model).SequenceEqual(["h502300", "h510300"]) && left.All(l => l.Empty), $"drop: as up to 1.0.460, only h counts: {string.Join(", ", left)}");

        // The port log names the enemies that stand on such a part.
        var msb = new SoulsFormats.MSB3();
        msb.Parts.Collisions.Add(new SoulsFormats.MSB3.Part.Collision { Name = "h502300_0000", ModelName = "h502300" });
        msb.Parts.Collisions.Add(new SoulsFormats.MSB3.Part.Collision { Name = "h000100_0000", ModelName = "h000100" });
        msb.Parts.Enemies.Add(new SoulsFormats.MSB3.Part.Enemy { Name = "c6040_0000", ModelName = "c6040", EntityID = 4500300, CollisionName = "h502300_0000" });
        msb.Parts.Enemies.Add(new SoulsFormats.MSB3.Part.Enemy { Name = "c1280_0000", ModelName = "c1280", EntityID = 4500301, CollisionName = "h000100_0000" });
        foreach (var (results, method) in new[] { (copy, MapCollisions.EmptySideMethod.Copy), (alone, MapCollisions.EmptySideMethod.Alone) })
        {
            var notes = MapPorter.EmptySideNotes(msb, results, method).ToList();
            Expect(notes.Count == 1 && notes[0].Contains("1 models, 1 collision parts") && notes[0].Contains("1 DS3 enemies") && notes[0].Contains("c6040_0000 4500300 on h502300_0000"),
                $"{method}: note: {string.Join(" / ", notes)}");
        }
        Expect(!MapPorter.EmptySideNotes(msb, new Dictionary<string, MapCollisions.Result>
        {
            ["h"] = new("h", new(StringComparer.OrdinalIgnoreCase) { "000100" }),
            ["l"] = new("l", new(StringComparer.OrdinalIgnoreCase) { "000100" }),
        }, MapCollisions.EmptySideMethod.Copy).Any(), "no note on a map without one-sided collision");

        Expect(MapPorter.Options.Parse(["--empty-collision-alone"]).EmptyCollision == MapCollisions.EmptySideMethod.Alone
               && MapPorter.Options.Parse(["--drop-empty-collisions"]).EmptyCollision == MapCollisions.EmptySideMethod.Drop
               && new MapPorter.Options().EmptyCollision == MapCollisions.EmptySideMethod.Copy, "the empty-collision arguments are read");
    }

    private static void EmptyCollision()
    {
        var empty = new Ds3.hkRootLevelContainer { m_namedVariants = [] };
        try
        {
            CollisionPort.Convert(empty, ErCollisionTemplate.Default, keepSimdTree: false, []);
            Expect(false, "an empty DS3 collision file converted");
        }
        catch (EmptyCollisionException)
        {
        }
        // A file with a root object of another kind still fails as before (it holds something the port can't read).
        var other = new Ds3.hkRootLevelContainer { m_namedVariants = [new Ds3.hkRootLevelContainerNamedVariant { m_name = "x", m_className = "hkxScene", m_variant = null }] };
        try
        {
            CollisionPort.Convert(other, ErCollisionTemplate.Default, keepSimdTree: false, []);
            Expect(false, "a file without physics scene data converted");
        }
        catch (EmptyCollisionException)
        {
            Expect(false, "a file with a root object was called empty");
        }
        catch (InvalidDataException)
        {
        }
    }
}
