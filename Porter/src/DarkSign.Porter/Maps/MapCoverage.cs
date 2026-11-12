using System.Globalization;
using System.Text;

namespace DarkSign.Porter.Maps;

/// <summary>
/// How much of each DS3 map made it into its Elden Ring slot, side by side, so a converter rule that holds on one map
/// and fails on another shows up in every build's port log instead of in the next playtest (RULE 1 and RULE 4).
/// <para>
/// Why it exists (2026-11-12, 1.0.383): every fix was checked on map 1, where it worked, while the same build's own
/// piece reports said Undead Settlement had lost 714 map pieces and the Painted World 103 to one-layer vertex alpha
/// read as a fade (#152, fixed for every map in #171). No step failed and no line said FAILED: the losses were only
/// visible by reading each map's report on its own. The table puts the maps next to each other, and the warnings name
/// a map that loses a share of its pieces or has failures the other maps don't.
/// </para>
/// </summary>
public static class MapCoverage
{
    public const string ReportFile = "map-coverage.tsv";

    /// <summary>A map that leaves out at least this share of DS3's map piece parts (no drawable model) is warned about.</summary>
    public const double PieceLossWarn = 0.05;

    /// <summary>One ported map: what DS3 had and what was written.</summary>
    public sealed record Row(MapId Ds3, MapId Er, string Name)
    {
        public int PiecesDs3 { get; init; }
        public int PiecesWritten { get; init; }
        /// <summary>Map piece parts left out because their model had nothing the port can draw (or didn't convert).</summary>
        public int PiecesNoModel { get; init; }
        public int Models { get; init; }
        public int ModelsLeftOut { get; init; }
        public int MeshesLeftOut { get; init; }
        public int FadedCut { get; init; }
        public int FadedTotal { get; init; }
        public int CollisionsDs3 { get; init; }
        public int CollisionsWritten { get; init; }
        public int CollisionFilesEmpty { get; init; }
        public int CollisionFilesFailed { get; init; }
        public int ObjectsDs3 { get; init; }
        public int ObjectsPlaced { get; init; }
        public int EnemiesPlaced { get; init; }
        public int EnemiesPorted { get; init; }
        public int EnemyStandIns { get; init; }
        public int EnemiesNothing { get; init; }
        public int BossFights { get; init; }
        public int BossWarps { get; init; }
        /// <summary>The map's steps that failed (the port log's "FAILED" lines, without the leading spaces).</summary>
        public IReadOnlyList<string> Failed { get; init; } = [];
    }

    private static string Pct(int part, int whole) =>
        whole <= 0 ? "-" : (100.0 * part / whole).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private static readonly string[] Columns =
    [
        "DS3 map", "Elden Ring slot", "name", "map pieces in DS3", "written", "left out: no drawable model", "share",
        "models", "models left out", "meshes left out", "faded triangles cut", "of", "collisions in DS3", "written",
        "collision files empty in DS3", "collision files failed", "objects in DS3", "placed", "enemies placed",
        "ported DS3", "Elden Ring stand-ins", "nothing to stand in", "boss fights", "F1 warps", "steps failed",
    ];

    /// <summary>The table as TSV (Work\reports\map-coverage.tsv).</summary>
    public static string Tsv(IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder(string.Join('\t', Columns)).Append('\n');
        foreach (var r in rows)
            sb.AppendJoin('\t', new object[]
            {
                r.Ds3, r.Er, r.Name, r.PiecesDs3, r.PiecesWritten, r.PiecesNoModel, Pct(r.PiecesNoModel, r.PiecesDs3),
                r.Models, r.ModelsLeftOut, r.MeshesLeftOut, r.FadedCut, r.FadedTotal, r.CollisionsDs3, r.CollisionsWritten,
                r.CollisionFilesEmpty, r.CollisionFilesFailed, r.ObjectsDs3, r.ObjectsPlaced, r.EnemiesPlaced, r.EnemiesPorted,
                r.EnemyStandIns, r.EnemiesNothing, r.BossFights, r.BossWarps, r.Failed.Count == 0 ? "" : string.Join(" | ", r.Failed),
            }).Append('\n');
        return sb.ToString();
    }

    /// <summary>The port log's summary: one line per map, then the warnings.</summary>
    public static IEnumerable<string> Summary(IReadOnlyList<Row> rows)
    {
        yield return $"map coverage (every ported map side by side; Work\\reports\\{ReportFile}):";
        foreach (var r in rows)
            yield return $"  {r.Er} {r.Name}: map pieces {r.PiecesWritten} of {r.PiecesDs3} ({r.PiecesNoModel} left out with no drawable model, {Pct(r.PiecesNoModel, r.PiecesDs3)}); "
                         + $"models {r.Models - r.ModelsLeftOut} of {r.Models}; meshes left out {r.MeshesLeftOut}; faded triangles cut {r.FadedCut:N0} of {r.FadedTotal:N0}; "
                         + $"collisions {r.CollisionsWritten} of {r.CollisionsDs3} ({r.CollisionFilesEmpty} empty in DS3, {r.CollisionFilesFailed} failed); "
                         + $"objects {r.ObjectsPlaced} of {r.ObjectsDs3}; enemies {r.EnemiesPlaced} ({r.EnemiesPorted} ported, {r.EnemyStandIns} stand-ins, {r.EnemiesNothing} with nothing); "
                         + $"boss warps {r.BossWarps} of {r.BossFights}; steps failed {r.Failed.Count}";
        foreach (var w in Warnings(rows))
            yield return "  WARN " + w;
    }

    /// <summary>
    /// What one map lost that the others didn't: a share of its map pieces, collision files that failed, steps that
    /// failed. Each is a converter rule to check on that map's files, not on the map the last fix was tried on.
    /// </summary>
    public static List<string> Warnings(IReadOnlyList<Row> rows)
    {
        var warnings = new List<string>();
        foreach (var r in rows.Where(r => r.PiecesDs3 > 0 && (double)r.PiecesNoModel / r.PiecesDs3 >= PieceLossWarn))
            warnings.Add($"{r.Er} ({r.Name}): {r.PiecesNoModel} of {r.PiecesDs3} DS3 map pieces ({Pct(r.PiecesNoModel, r.PiecesDs3)}) were left out because their model had nothing to draw; "
                         + $"the reasons per piece are in Work\\reports\\map-pieces-{r.Er}.tsv");
        Uneven("collision files that hold collision but didn't convert", r => r.CollisionFilesFailed);
        Uneven("steps that FAILED", r => r.Failed.Count);
        Uneven("enemies with nothing to stand in", r => r.EnemiesNothing);
        foreach (var r in rows.Where(r => r.Failed.Count > 0))
            foreach (var line in r.Failed.Take(5))
                warnings.Add($"{r.Er} ({r.Name}): {line}");
        return warnings;

        // A count that is above zero on some maps and zero on the others points at something only those maps have.
        void Uneven(string what, Func<Row, int> count)
        {
            if (rows.Count < 2)
                return;
            var bad = rows.Where(r => count(r) > 0).ToList();
            if (bad.Count == 0 || bad.Count == rows.Count)
                return;
            warnings.Add($"{what}: {string.Join(", ", bad.Select(r => $"{r.Er} ({r.Name}) {count(r)}"))}; "
                         + $"0 on {string.Join(", ", rows.Except(bad).Select(r => r.Er))}");
        }
    }

    /// <summary>The "FAILED" lines of one map's part of the port log.</summary>
    public static List<string> FailedLines(string mapReport) =>
        mapReport.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("FAILED", StringComparison.Ordinal)).ToList();
}
