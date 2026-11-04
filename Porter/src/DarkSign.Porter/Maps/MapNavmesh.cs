using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// A map's navmesh binder (.nvmhktbnd.dcx, a BND4 holding one Havok AI file per navmesh section). Each DS3 entry is
/// converted with <see cref="NavmeshPort"/>, renamed to the host map and written into a binder made from the host
/// slot's own, which keeps its format and its .compendium. Entries that aren't Havok files are copied unchanged.
/// </summary>
public static class MapNavmesh
{
    public static string Ds3BinderPath(MapId id) => $"/map/{id}/{id}.nvmhktbnd.dcx";
    public static string ErBinderPath(MapId id) => $"/map/{id.AreaFolder}/{id}/{id}.nvmhktbnd.dcx";

    public static string Port(PorterContext ctx, GameArchives ds3, GameArchives er, MapId ds3Id, MapId erId, MapPorter.Options options)
    {
        var source = Ds3BinderPath(ds3Id);
        if (!ds3.Contains(source))
            return $"navmesh: DS3 has no {ItemText.Leaf(source)}; skipped";
        var hostPath = ErBinderPath(erId);
        if (!er.Contains(hostPath))
            return $"navmesh: Elden Ring has no {ItemText.Leaf(hostPath)} to copy the format from; skipped";

        var (hostBytes, compression) = er.ReadWithCompression(hostPath);
        var host = BND4.Read(hostBytes);
        var ds3Binder = BND4.Read(ds3.Read(source));
        var result = Convert(ds3Binder, host, ds3Id, erId, !options.BinaryCollision);

        var target = ErBinderPath(erId);
        File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(target)), DCX.Compress(result.Binder.Write(), compression));
        return $"navmesh: {result.Summary}";
    }

    public sealed record Result(BND4 Binder, string Summary, int Converted);

    /// <summary>The DS3 binder's navmeshes in a copy of the host binder (its own navmeshes left out).</summary>
    public static Result Convert(BND4 ds3, BND4 host, MapId ds3Id, MapId erId, bool xml)
    {
        var output = new BND4
        {
            Version = host.Version, Format = host.Format, BigEndian = host.BigEndian, BitBigEndian = host.BitBigEndian,
            Unicode = host.Unicode, Extended = host.Extended, Unk04 = host.Unk04, Unk05 = host.Unk05,
        };
        var hostHkx = host.Files.FirstOrDefault(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase));
        DCX.CompressionInfo? hostEntryCompression = null;
        if (hostHkx is not null && DCX.Is(hostHkx.Bytes))
        {
            DCX.Decompress(hostHkx.Bytes, out var entryCompression);
            hostEntryCompression = entryCompression;
        }
        var folder = hostHkx is null ? "" : hostHkx.Name[..^ItemText.Leaf(hostHkx.Name).Length];
        var flags = hostHkx?.Flags ?? Binder.FileFlags.Flag1;
        foreach (var keep in host.Files.Where(f => f.Name.Contains(".compendium", StringComparison.OrdinalIgnoreCase)))
            output.Files.Add(keep);
        var nextId = output.Files.Count == 0 ? 0 : output.Files.Max(f => f.ID) + 1;

        int converted = 0, faces = 0;
        var failed = new List<string>();
        foreach (var file in ds3.Files)
        {
            var leaf = ItemText.Leaf(file.Name);
            var name = folder + leaf.Replace(ds3Id.Digits, erId.Digits, StringComparison.OrdinalIgnoreCase);
            if (!leaf.Contains(".hkx", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var bytes = DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes;
                var navmesh = NavmeshPort.Convert(bytes, xml);
                var outBytes = hostEntryCompression is null ? navmesh.Bytes : DCX.Compress(navmesh.Bytes, hostEntryCompression);
                output.Files.Add(new BinderFile(flags, nextId++, name, outBytes));
                converted++;
                faces += navmesh.Faces;
            }
            catch (Exception ex)
            {
                failed.Add($"{leaf} ({ex.Message})");
            }
        }
        var summary = $"{converted} navmeshes, {faces} faces ({(xml ? "XML" : "binary")} tagfiles)";
        if (failed.Count > 0)
            summary += $"; {failed.Count} failed: {string.Join("; ", failed.Take(5))}{(failed.Count > 5 ? "; ..." : "")}";
        return new Result(output, summary, converted);
    }
}
