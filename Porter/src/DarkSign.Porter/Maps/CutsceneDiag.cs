using System.Text;
using HKLib.hk2018;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// `cutscene-diag [ds3 names...]`: compares converted DS3 cutscenes with Elden Ring's own, for cutscenes that hang,
/// never end or don't play. Read-only (nothing is written to the mod). It reports:
/// <list type="bullet">
/// <item>Elden Ring's timeline shape (<see cref="CutsceneShape"/>): for each resource kind, how its events fill their
/// unnamed fields, its timelines' Unk10, parent kind and full parameter lists.</item>
/// <item>For each DS3 cutscene (default: every one on a ported map): where the converted timeline differs from that
/// shape, before and after <see cref="CutsceneShape.Conform"/>; and per cut, the Havok file the cut plays and how
/// its animations' lengths compare with the cut's (an Elden Ring cut's animations run its length + 1 frames).</item>
/// <item>The same per-cut length check on a few Elden Ring cutscenes, as the control.</item>
/// </list>
/// Writes Work\logs\cutscene-diag-*.txt.
/// </summary>
public static class CutsceneDiag
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var r = new StringBuilder();
        var notes = new List<string>();
        var side = CutscenePort.ReadEr(er, notes);
        var shape = side.Shape!;
        r.AppendLine("=== Elden Ring timeline shape (rules marked ? hold for under 97% of events; only the others are checked)");
        foreach (var line in shape.Describe())
            r.AppendLine("  " + line);
        foreach (var n in notes)
            r.AppendLine("  note: " + n);

        r.AppendLine("=== Elden Ring control: cut lengths against their Havok files");
        foreach (var name in new[] { "s10_00_0010", "s10_00_0050", "s14_00_0000", "s19_00_0010" })
        {
            try
            {
                var bnd = BND4.Read(er.Read(CutscenePort.ErPath(name)));
                foreach (var line in Cuts(bnd))
                    r.AppendLine($"  {name} {line}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                r.AppendLine($"  {name}: {ex.Message}");
            }
        }

        var names = args.Count > 0 ? args.ToList()
            : MapSlots.All.Where(s => !s.Probe).SelectMany(s => CutscenePort.CutscenesOf(ds3, s.Ds3)).Distinct().ToList();
        foreach (var name in names)
        {
            r.AppendLine($"=== {name}");
            try
            {
                var ds3Id = CutscenePort.IdOf(name) ?? throw new FormatException($"not a cutscene name: {name}");
                var slot = MapSlots.All.FirstOrDefault(s => !s.Probe && s.Ds3.Area == ds3Id / 1_000_000 && s.Ds3.Block == ds3Id / 10_000 % 100);
                var erMap = slot?.Er ?? MapId.Parse("m19_70_00_00");
                var ds3Map = slot?.Ds3 ?? new MapId(ds3Id / 1_000_000, ds3Id / 10_000 % 100, 0, 0);
                var erName = CutscenePort.Name(CutscenePort.ErId(ds3Id, erMap));
                var source = BND4.Read(ds3.Read(CutscenePort.Ds3Path(name)));
                var mqbFile = source.Files.First(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase));
                var mqb = MQB.Read(mqbFile.Bytes);
                r.AppendLine($"  {erName}: {mqb.Framerate} fps, {mqb.Cuts.Count} cuts ({string.Join(" ", mqb.Cuts.Select(c => $"{c.Name}:{c.Duration}f"))}), "
                             + $"binder files {string.Join(" ", source.Files.Select(f => $"[{f.ID}]{ItemText.Leaf(f.Name)}"))}");
                var stats = CutscenePort.ConvertMqb(mqb, name, erName, _ => null, ds3Map, erMap, side.Catalog);
                r.AppendLine($"  converted as the port did before: {stats.Events - stats.EventsDropped}/{stats.Events} events, {stats.ParamsDropped} parameters left out");
                foreach (var line in shape.Check(mqb))
                    r.AppendLine("    " + line);
                int changed = shape.Conform(mqb);
                var after = shape.Check(mqb);
                r.AppendLine($"  after conforming ({changed} changes): {(after.Count == 0 ? "matches Elden Ring's shape" : $"{after.Count} differences left")}");
                foreach (var line in after)
                    r.AppendLine("    " + line);
                // The converted binder, for the cut length check.
                var (bnd, _) = CutscenePort.ConvertBinder(BND4.Read(ds3.Read(CutscenePort.Ds3Path(name))), name, erName, _ => null, ds3Map, erMap, side);
                foreach (var line in Cuts(bnd))
                    r.AppendLine("  " + line);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                r.AppendLine($"  FAILED: {ex}");
            }
        }
        var log = ctx.WorkPath("logs", $"cutscene-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, r.ToString());
        Console.WriteLine(r.ToString());
        Console.WriteLine($"report written to {log}");
        return 0;
    }

    /// <summary>Per cut of an Elden Ring-format binder: its HKT resource, its Havok file and the animations' frame counts.</summary>
    public static IEnumerable<string> Cuts(BND4 bnd)
    {
        var mqbFile = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase));
        if (mqbFile is null)
        {
            yield return "no timeline in the binder";
            yield break;
        }
        var mqb = MQB.Read(mqbFile.Bytes);
        foreach (var cut in mqb.Cuts)
        {
            var hkts = cut.Timelines.SelectMany(t => t.Events).Where(e => e.ResourceIndex >= 0 && e.ResourceIndex < mqb.Resources.Count && mqb.Resources[e.ResourceIndex].Unk48 == 266)
                .Select(e => mqb.Resources[e.ResourceIndex]).ToList();
            var file = bnd.Files.FirstOrDefault(f => f.Name.Replace('/', '\\').Contains($"\\{cut.Name}\\", StringComparison.OrdinalIgnoreCase)
                                                     && f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase));
            string frames;
            if (file is null)
                frames = "NO HAVOK FILE";
            else
            {
                try
                {
                    List<int> counts;
                    lock (AnimPort.HavokLock)
                        counts = AnimPort.Container((hkRootLevelContainer)AnimPort.ReadEr(file.Bytes, null)).m_animations
                            .Select(a => a is hkaSplineCompressedAnimation s ? s.m_numFrames : -1).ToList();
                    var distinct = counts.Distinct().Order().ToList();
                    frames = $"[{file.ID}] {counts.Count} animations, frames {string.Join("/", distinct)}"
                             + (distinct.Any(c => c >= 0 && c != cut.Duration + 1) ? $" (cut + 1 = {cut.Duration + 1})" : "");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    frames = $"[{file.ID}] unreadable: {ex.Message}";
                }
            }
            yield return $"{cut.Name}: {cut.Duration} frames, unk44 {cut.Unk44}, HKT {string.Join(" ", hkts.Select(h => h.Path))}{(hkts.Count == 0 ? "NONE" : "")}; {frames}";
        }
    }
}
