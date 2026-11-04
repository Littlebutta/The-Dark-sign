using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// A map's collision binders. Both games keep a map's collision in split binders (h = high detail, used for
/// physics; l = low detail), one .hkx.dcx per collision model, named after the map and model
/// (h40_00_00_00_000100.hkx.dcx). Each DS3 file is converted with <see cref="CollisionPort"/>, using the host slot's
/// first vanilla collision as the template for Elden Ring-only settings, renamed to the host map and written into a
/// binder made from the host's own, which keeps its .compendium (the type information its binary files need).
/// </summary>
public static class MapCollisions
{
    public sealed record Result(string Summary, HashSet<string> Ported);

    public static string Ds3BinderPath(MapId id, string kind) => $"/map/{id}/{kind}{id.Digits}.hkxbhd";
    public static string ErBinderPath(MapId id, string kind) => $"/map/{id.AreaFolder}/{id}/{kind}{id.Digits}.hkxbhd";

    public static Result Port(PorterContext ctx, GameArchives ds3, GameArchives er, MapId ds3Id, MapId erId, string kind,
        MapPorter.Options options)
    {
        var ported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = Ds3BinderPath(ds3Id, kind);
        if (!ds3.Contains(source))
            return new Result($"DS3 has no {ItemText.Leaf(source)}; skipped", ported);

        // The host binder of the same kind, else the high-detail one, gives the format and the template.
        var hostPath = ErBinderPath(erId, kind);
        if (!er.Contains(hostPath))
            hostPath = ErBinderPath(erId, "h");
        if (!er.Contains(hostPath))
            return new Result($"Elden Ring has no {ItemText.Leaf(hostPath)} to copy the format from; skipped", ported);
        var host = BXF4.Read(er.Read(hostPath), er.Read(hostPath[..^3] + "bdt"));
        var compendiumFile = host.Files.FirstOrDefault(f => f.Name.Contains(".compendium", StringComparison.OrdinalIgnoreCase));
        var compendium = compendiumFile is null ? null : MaybeDecompress(compendiumFile.Bytes);
        var templateFile = host.Files.FirstOrDefault(f => f.Name.EndsWith(".hkx.dcx", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{hostPath} holds no .hkx.dcx");
        var hostHkx = DCX.Decompress(templateFile.Bytes, out var compression);

        ErCollisionTemplate? template = null;
        var templateNote = "";
        try
        {
            template = ErCollisionTemplate.FromFile(hostHkx, compendium);
        }
        catch (Exception ex)
        {
            templateNote = $"; host template unreadable ({ex.Message}), defaults used";
        }
        templateNote = template is null && templateNote.Length == 0 ? "; no host template body, defaults used" : templateNote;

        var folder = templateFile.Name[..^ItemText.Leaf(templateFile.Name).Length];
        var output = new BXF4
        {
            Version = host.Version, Format = host.Format, BigEndian = host.BigEndian, BitBigEndian = host.BitBigEndian,
            Unicode = host.Unicode, Extended = host.Extended, Unk04 = host.Unk04, Unk05 = host.Unk05,
        };
        if (compendiumFile is not null)
            output.Files.Add(compendiumFile);
        var nextId = output.Files.Count == 0 ? 0 : output.Files.Max(f => f.ID) + 1;

        var ds3Binder = BXF4.Read(ds3.Read(source), ds3.Read(source[..^3] + "bdt"));
        int triangles = 0;
        var failed = new List<string>();
        foreach (var file in ds3Binder.Files.Where(f => f.Name.EndsWith(".hkx.dcx", StringComparison.OrdinalIgnoreCase)))
        {
            var leaf = ItemText.Leaf(file.Name);
            try
            {
                var result = CollisionPort.Convert(DCX.Decompress(file.Bytes), template, !options.BinaryCollision, options.KeepSimdTree);
                var name = leaf.Replace(ds3Id.Digits, erId.Digits, StringComparison.OrdinalIgnoreCase);
                output.Files.Add(new BinderFile(templateFile.Flags, nextId++, folder + name, DCX.Compress(result.Bytes, compression)));
                ported.Add(ModelNumberOf(leaf));
                triangles += result.Triangles;
            }
            catch (Exception ex)
            {
                failed.Add($"{leaf} ({ex.Message})");
            }
        }

        var target = ErBinderPath(erId, kind);
        output.Write(ctx.ModPath(MapPorter.ModParts(target)), ctx.ModPath(MapPorter.ModParts(target[..^3] + "bdt")));
        var summary = $"{ported.Count} files, {triangles} triangles into {ItemText.Leaf(target)}"
                      + $" ({(options.BinaryCollision ? "binary" : "XML")} tagfiles){templateNote}";
        if (failed.Count > 0)
            summary += $"; {failed.Count} failed: {string.Join("; ", failed.Take(5))}{(failed.Count > 5 ? "; ..." : "")}";
        return new Result(summary, ported);
    }

    // "h40_00_00_00_000100.hkx.dcx" -> "000100"
    public static string ModelNumberOf(string leaf)
    {
        var stem = leaf;
        var dot = stem.IndexOf('.');
        if (dot >= 0)
            stem = stem[..dot];
        var underscore = stem.LastIndexOf('_');
        return underscore >= 0 ? stem[(underscore + 1)..] : MapPorter.ModelNumber(stem);
    }

    private static byte[] MaybeDecompress(byte[] bytes) => DCX.Is(bytes) ? DCX.Decompress(bytes) : bytes;
}
