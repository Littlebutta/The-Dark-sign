using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// A map ID Elden Ring doesn't ship has none of the per-map files the game keeps beside a map's layout, pieces,
/// collision and navmesh: the f-collision binder, .mfr, .nva, .btl lights, .fvb, the ivinfo binders, breakgeom, onav
/// and the map's draw params (gparam). A colosseum slot keeps the colosseum's own copies of these; a new-ID slot
/// would have nothing, and the game may wait for them. So every such file of the template map is copied under the
/// new ID (binder entry names renamed too), and the port's own outputs (layout, map pieces, h/l collision, navmesh,
/// textures, event script) are left alone. The copies describe the colosseum, not the DS3 map, exactly as they do in
/// a colosseum slot today.
/// </summary>
public static class NewIdFiles
{
    /// <summary>Archive paths of the template's per-map files that the port doesn't write itself.</summary>
    public static List<string> TemplateFiles(IEnumerable<string> archivePaths, MapId template)
    {
        var id = Regex.Escape(template.Digits);
        var perMap = new Regex($@"/[mhlf]{id}[._]", RegexOptions.IgnoreCase);
        var gparam = new Regex($@"/m{template.Area:D2}_{template.Block:D2}_0000\.gparam", RegexOptions.IgnoreCase);
        var written = new Regex($@"(/mapstudio/|/event/|\.mapbnd\.dcx$|/[hl]{id}\.hkxb[hd][dt]$|\.nvmhktbnd\.dcx$|\.tpf)", RegexOptions.IgnoreCase);
        return archivePaths
            .Where(p => (perMap.IsMatch(p) || gparam.IsMatch(p)) && !written.IsMatch(p))
            .Where(p => !p.EndsWith(".hkxbdt", StringComparison.OrdinalIgnoreCase)) // written with its .hkxbhd
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The same path under the new ID.</summary>
    public static string Renamed(string path, MapId from, MapId to) =>
        path.Replace(from.Digits, to.Digits)
            .Replace($"m{from.Area:D2}_{from.Block:D2}_0000", $"m{to.Area:D2}_{to.Block:D2}_0000")
            .Replace($"/{from.AreaFolder}/", $"/{to.AreaFolder}/");

    /// <summary>The mod-folder paths a slot's copies take (for cleaning), given the template files.</summary>
    public static IEnumerable<string> OutputPaths(IEnumerable<string> templateFiles, MapId from, MapId to)
    {
        foreach (var path in templateFiles)
        {
            var renamed = Renamed(path, from, to);
            yield return renamed;
            if (renamed.EndsWith(".hkxbhd", StringComparison.OrdinalIgnoreCase))
                yield return renamed[..^3] + "bdt";
        }
    }

    /// <summary>port-maps step: copies the template's per-map files under every new-ID slot. Returns the report line.</summary>
    public static string Copy(PorterContext ctx, GameArchives er, IEnumerable<MapSlots.Slot> slots)
    {
        var paths = er.Entries.Select(e => e.Path).Where(p => p is not null).Cast<string>().ToList();
        var lines = new List<string>();
        foreach (var slot in slots.Where(s => s.Host != s.Er).DistinctBy(s => s.Er))
        {
            var files = TemplateFiles(paths, slot.Host);
            var kinds = new List<string>();
            foreach (var path in files)
            {
                var target = Renamed(path, slot.Host, slot.Er);
                if (path.EndsWith(".hkxbhd", StringComparison.OrdinalIgnoreCase))
                {
                    var binder = BXF4.Read(er.Read(path), er.Read(path[..^3] + "bdt"));
                    foreach (var file in binder.Files)
                        file.Name = Renamed(file.Name, slot.Host, slot.Er);
                    binder.Write(ctx.ModPath(MapPorter.ModParts(target)), ctx.ModPath(MapPorter.ModParts(target[..^3] + "bdt")));
                }
                else
                {
                    var raw = er.ReadRaw(path);
                    var compressed = DCX.Is(raw);
                    DCX.CompressionInfo? compression = null;
                    var bytes = compressed ? DCX.Decompress(raw, out compression) : raw;
                    if (BND4.Is(bytes))
                    {
                        var binder = BND4.Read(bytes);
                        foreach (var file in binder.Files)
                            file.Name = Renamed(file.Name, slot.Host, slot.Er);
                        bytes = binder.Write();
                        raw = compressed ? DCX.Compress(bytes, compression!) : bytes;
                    }
                    File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(target)), raw);
                }
                kinds.Add(Path.GetFileName(target));
            }
            lines.Add($"{slot.Er}: {files.Count} per-map files copied from {slot.Host} ({string.Join(", ", kinds)})");
        }
        return lines.Count == 0 ? "new map ID files: every slot is a map Elden Ring ships; nothing to copy" : "new map ID files: " + string.Join("; ", lines);
    }
}
