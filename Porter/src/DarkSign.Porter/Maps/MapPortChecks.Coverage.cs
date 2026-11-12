using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> CoverageChecks()
    {
        yield return ("coverage: a map that loses a share of its pieces, or fails where the others don't, is warned about by name", Coverage);
        yield return ("collision: an empty DS3 collision file (no root object) is reported as empty, not as a failed conversion", EmptyCollision);
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
