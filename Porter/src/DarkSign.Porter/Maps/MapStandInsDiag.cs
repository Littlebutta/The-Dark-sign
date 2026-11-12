using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "map-diag stand-ins [DS3 map ID...]" (RULE 4): judges every slot's DS3 map pieces with <see cref="MapStandIns"/>
/// straight from DS3's files, without porting anything, and writes Work\reports\map-diag\stand-ins-&lt;slot&gt;.txt:
/// every low-detail stand-in and far view with its verdict, and every piece the far view test came near. Read-only:
/// it reads the game archives and writes reports, so it can be run before a build to see what a change to the test
/// would leave out.
/// </summary>
public static class MapStandInsDiag
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var maps = args.Select(MapId.Parse).ToList();
        var slots = MapSlots.All.Where(s => maps.Count == 0 || maps.Contains(s.Ds3)).DistinctBy(s => s.Ds3).ToList();
        var dir = ctx.WorkPath("reports", "map-diag");
        Directory.CreateDirectory(dir);
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        foreach (var slot in slots)
        {
            var report = new StringBuilder();
            void Line(string text)
            {
                Console.WriteLine(text);
                report.AppendLine(text);
            }
            Line($"=== {slot.Ds3} -> {slot.Er}");
            try
            {
                Report(ds3, slot.Ds3, Line);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Line($"FAILED: {ex}");
            }
            var path = Path.Combine(dir, $"stand-ins-{slot.Er}.txt");
            File.WriteAllText(path, report.ToString());
            Console.WriteLine($"report: {path}");
        }
        return 0;
    }

    private static void Report(GameArchives ds3, MapId map, Action<string> line)
    {
        var msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(map)));
        var meshes = new Dictionary<string, List<MapModelPort.MeshInfo>>(StringComparer.OrdinalIgnoreCase);
        var unread = new List<string>();
        foreach (var model in msb.Parts.MapPieces.Select(p => p.ModelName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var number = MapPorter.ModelNumber(model);
            var path = MapPorter.Ds3MapBndPath(map, number);
            if (!ds3.Contains(path))
            {
                unread.Add(model);
                continue;
            }
            var list = new List<MapModelPort.MeshInfo>();
            foreach (var file in BND4.Read(ds3.Read(path)).Files.Where(f => FLVER2.Is(f.Bytes)))
                list.AddRange(MapModelPort.Describe(FLVER2.Read(file.Bytes)));
            meshes[number] = list;
        }
        bool layerOk = MsbPort.BaseLayerMeans(msb);
        var skip = msb.Parts.MapPieces.Where(p => MsbPort.HelperKind(p, layerOk) is not null).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        line($"{msb.Parts.MapPieces.Count} map pieces, {meshes.Count} models read, {unread.Count} not in the archives"
             + (unread.Count > 0 ? $" ({string.Join(", ", unread.Take(10))})" : "") + $", {skip.Count} helper pieces not judged");
        var result = MapStandIns.Find(msb, meshes, skip);
        line(result.Summary);
        foreach (var l in result.Lines)
            line("  " + l);
    }
}
