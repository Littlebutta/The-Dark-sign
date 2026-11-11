using DarkSign.Porter.Havok;
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
    /// <summary>Peak memory of converting a DS3 collision file, per byte of the file (measured: 4.7 MB peaked at 3.8 GB).</summary>
    private const long BytesPerDs3Byte = 1000;
    /// <summary>The same for a binary tagfile, which never holds the text: the converted object graph alone (an estimate).</summary>
    private const long BinaryBytesPerDs3Byte = 100;

    public sealed record Result(string Summary, HashSet<string> Ported);

    /// <summary>The Elden Ring collision settings the last map's collision was converted with, for the object port.</summary>
    public static ErCollisionTemplate? LastTemplate { get; private set; }

    public static string Ds3BinderPath(MapId id, string kind) => $"/map/{id}/{kind}{id.Digits}.hkxbhd";
    public static string ErBinderPath(MapId id, string kind) => $"/map/{id.AreaFolder}/{id}/{kind}{id.Digits}.hkxbhd";

    public static Result Port(PorterContext ctx, GameArchives ds3, GameArchives er, MapId ds3Id, MapId erId, string kind,
        MapPorter.Options options, MapId? hostId = null)
    {
        var host0 = hostId ?? erId;
        var ported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = Ds3BinderPath(ds3Id, kind);
        if (!ds3.Contains(source))
            return new Result($"DS3 has no {ItemText.Leaf(source)}; skipped", ported);

        // The host binder of the same kind, else the high-detail one, gives the format and the template.
        var hostPath = ErBinderPath(host0, kind);
        if (!er.Contains(hostPath))
            hostPath = ErBinderPath(host0, "h");
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
        if (template is not null)
            LastTemplate = template;

        var folder = templateFile.Name[..^ItemText.Leaf(templateFile.Name).Length]
            .Replace(host0.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);
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
        // Each collision file converts on its own thread; they're added in DS3's order, so every build is the same.
        var files = ds3Binder.Files.Where(f => f.Name.EndsWith(".hkx.dcx", StringComparison.OrdinalIgnoreCase)).ToList();
        var results = new (byte[]? Bytes, int Triangles, string? Error, long Written)[files.Count];
        // Serializing is the slow part and HKLib can only do it on every core once its caches are warm (see
        // CollisionPort.WarmUp). Big files go first, so the end of the stage isn't one core finishing a large file
        // alone, and the memory budget keeps the XML of the files in flight (about 800 times the DS3 file's size)
        // within a share of the PC's memory.
        var warmNote = CollisionPort.WarmUp(template, !options.BinaryCollision, options.KeepSimdTree);
        var cachesBefore = HavokCaches.Count;
        var order = Enumerable.Range(0, files.Count).OrderByDescending(i => files[i].Bytes.Length).ToList();
        var budget = MemoryBudget.Share();
        Parallel.ForEach(order, Parallelism.Options, i =>
        {
            try
            {
                var raw = DCX.Decompress(files[i].Bytes);
                using var _ = budget.Take(raw.LongLength * (options.BinaryCollision ? BinaryBytesPerDs3Byte : BytesPerDs3Byte));
                var result = CollisionPort.Convert(raw, template, !options.BinaryCollision, options.KeepSimdTree);
                results[i] = (DCX.Compress(result.Bytes, compression), result.Triangles, null, result.Bytes.LongLength);
            }
            catch (Exception ex)
            {
                results[i] = (null, 0, ex.Message, 0);
            }
        });
        // A cache that grew while files serialized on every core means a type the warm-up missed: the files still
        // read back and compared, but the race is possible, so say so.
        var cachesAfter = HavokCaches.Count;
        if (warmNote is not null && cachesAfter != cachesBefore)
            Console.WriteLine($"collision: WARN HKLib cached {cachesAfter - cachesBefore} more types while converting {ItemText.Leaf(source)} in parallel; "
                              + "the warm-up (CollisionPort.WarmUp) misses a type. Rebuild with DARKSIGN_COLLISION_LOCK=1 if a map's collision misbehaves.");
        for (int i = 0; i < files.Count; i++)
        {
            var leaf = ItemText.Leaf(files[i].Name);
            if (results[i].Bytes is not { } bytes)
            {
                failed.Add($"{leaf} ({results[i].Error})");
                continue;
            }
            var name = leaf.Replace(ds3Id.Digits, erId.Digits, StringComparison.OrdinalIgnoreCase);
            output.Files.Add(new BinderFile(templateFile.Flags, nextId++, folder + name, bytes));
            ported.Add(ModelNumberOf(leaf));
            triangles += results[i].Triangles;
        }

        var target = ErBinderPath(erId, kind);
        output.Write(ctx.ModPath(MapPorter.ModParts(target)), ctx.ModPath(MapPorter.ModParts(target[..^3] + "bdt")));
        var summary = $"{ported.Count} files, {triangles} triangles into {ItemText.Leaf(target)}"
                      + $" ({(options.BinaryCollision ? "binary" : "XML")} tagfiles, {(warmNote is null ? "serialized one at a time" : $"on {Parallelism.Threads} threads")}){templateNote}";
        // The size the game reads: a binary tagfile is about its DS3 file's size, an XML one some 55 times it.
        var written = results.Where(r => r.Bytes is not null).Select(r => r.Written).ToList();
        if (written.Count > 0)
            summary += $"; {written.Sum() / 1048576.0:F0} MB for the game to read, the largest file {written.Max() / 1048576.0:F0} MB";
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
