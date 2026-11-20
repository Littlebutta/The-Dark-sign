using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> CoverageChecks()
    {
        yield return ("maps side by side: the steps that share state run in port order, so the numbers given are those of one map at a time", MapsSideBySide);
        yield return ("coverage: a map that loses a share of its pieces, or fails where the others don't, is warned about by name", Coverage);
        yield return ("collision: an empty DS3 collision file (no root object) is reported as empty, not as a failed conversion", EmptyCollision);
        yield return ("collision: a part whose DS3 h file is empty keeps its l collision (Painted World enemies died on load), --drop-empty-collisions leaves it out", EmptySideParts);
    }

    /// <summary>
    /// A stand-in for port-maps' map loop: every map numbers things from two shared counters, one in each turn, the
    /// way real maps number people's armour and object models. Returns what each map was given and the most maps
    /// that were in flight at once.
    /// </summary>
    private static (string Given, int MostAtOnce) TurnsRun(int maps, int atOnce, int failing, (int Map, int After) copy)
    {
        var turns = new MapTurns(maps);
        var given = new string[maps];
        int people = 0, objects = 0, inFlight = 0, most = 0;
        var finished = new bool[maps];
        var random = new Random(7);
        var pauses = Enumerable.Range(0, maps * 3).Select(_ => random.Next(1, 25)).ToArray();
        var run = Task.Run(() => turns.Run(atOnce, map => map == copy.Map ? [copy.After] : [], map =>
        {
            int now = Interlocked.Increment(ref inFlight);
            lock (finished)
                most = Math.Max(most, now);
            try
            {
                if (map == copy.Map && !Volatile.Read(ref finished[copy.After]))
                    throw new InvalidOperationException("a copying map started before the map it copies was finished");
                // Later maps get the shorter pauses, so without the turns they would overtake the earlier ones.
                turns.Take(MapTurns.Prepare, map);
                int firstPerson = people;
                Thread.Sleep(pauses[map * 3] / (map + 1));
                people = firstPerson + map + 1;
                turns.Give(MapTurns.Prepare, map);
                Thread.Sleep(pauses[map * 3 + 1] * (maps - map));
                if (map == failing)
                {
                    given[map] = $"{firstPerson}/failed";
                    return;   // a failed map never takes its second turn; MapTurns.Run gives it back
                }
                turns.Take(MapTurns.Shared, map);
                int firstObject = objects;
                Thread.Sleep(pauses[map * 3 + 2] / (map + 1));
                objects = firstObject + 2 * map + 1;
                turns.Give(MapTurns.Shared, map);
                Thread.Sleep(pauses[map * 3 + 2]);
                given[map] = $"{firstPerson}/{firstObject}";
            }
            finally
            {
                Volatile.Write(ref finished[map], true);
                Interlocked.Decrement(ref inFlight);
            }
        }));
        if (!run.Wait(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException($"{maps} maps, {atOnce} at once: the maps are waiting on each other");
        return (string.Join(" ", given), most);
    }

    private static void MapsSideBySide()
    {
        var serial = TurnsRun(7, 1, failing: 2, copy: (5, 1));
        Expect(serial.MostAtOnce == 1, $"one at a time: {serial.MostAtOnce} in flight");
        Expect(serial.Given == "0/0 1/1 3/failed 6/4 10/11 15/20 21/31", $"one at a time gives the numbers in port order: {serial.Given}");
        foreach (var atOnce in new[] { 2, 3, 4, 7, 12 })
            for (int round = 0; round < 3; round++)
            {
                var side = TurnsRun(7, atOnce, failing: 2, copy: (5, 1));
                Expect(side.Given == serial.Given, $"{atOnce} at once gives the same numbers as one at a time: {side.Given} vs {serial.Given}");
                Expect(side.MostAtOnce <= atOnce, $"{atOnce} at once: {side.MostAtOnce} in flight");
                Expect(side.MostAtOnce > 1, $"{atOnce} at once: maps really run side by side ({side.MostAtOnce} in flight)");
            }
        // A port step that throws past its own handling still gives the turns back, and the run reports it.
        var turns = new MapTurns(3);
        var seen = new List<int>();
        try
        {
            turns.Run(3, _ => [], map =>
            {
                turns.Take(MapTurns.Prepare, map);
                if (map == 0)
                    throw new InvalidDataException("test");
                lock (seen)
                    seen.Add(map);
                turns.Give(MapTurns.Prepare, map);
            });
            Expect(false, "a map that throws is reported");
        }
        catch (AggregateException ex)
        {
            Expect(ex.InnerExceptions.Count == 1 && ex.InnerExceptions[0] is InvalidDataException, "the map's own exception is carried");
        }
        Expect(seen.SequenceEqual([1, 2]), $"the maps after a failed one still port, in order: {string.Join(", ", seen)}");

        Expect(Parallelism.DefaultMapsAtOnce(16, 64L << 30) == 4, "16 threads, 64 GB: 4 maps at once");
        Expect(Parallelism.DefaultMapsAtOnce(32, 128L << 30) == 4, "never more than 4");
        Expect(Parallelism.DefaultMapsAtOnce(8, 32L << 30) == 2, "8 threads, 32 GB: 2");
        Expect(Parallelism.DefaultMapsAtOnce(16, 16L << 30) == 1, "16 GB: one map at a time");
        Expect(Parallelism.DefaultMapsAtOnce(2, 64L << 30) == 1, "2 threads: one map at a time");
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
