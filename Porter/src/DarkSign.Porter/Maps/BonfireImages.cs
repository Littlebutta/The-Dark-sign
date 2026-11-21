using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's bonfire pictures for the bonfire menu's "Travel here" preview (<see cref="BonfireTravel"/>).
/// <para>
/// DS3's warp menu shows a picture of the bonfire under the cursor. The picture is BonfireWarpParam's PictureId, a
/// texture in one of DS3's menu/mapimage/NN.tpf.dcx files (UXM's DS3 file list names 00-12, 98 and 99). A texture is
/// taken for a picture when the digits its name ends with are the PictureId ("..._10011" for 10011); the report line
/// names a few textures when none matched, so another naming shows up in the first port log.
/// </para>
/// Each picture is decoded (BCnEncoder.Net, already used for the light probes) to 8-bit RGBA, halved until it is at
/// most <see cref="MaxWidth"/> wide, and written as darksign_bonfires\&lt;PictureId&gt;.dkimg: "DKIM", width and
/// height (little-endian 32-bit), then the rows top to bottom. The test menu DLL uploads it as a texture as it is.
/// </summary>
public static class BonfireImages
{
    public const string Folder = "darksign_bonfires";
    public const string Extension = ".dkimg";
    public const int MaxWidth = 640;
    public static readonly byte[] Magic = "DKIM"u8.ToArray();

    /// <summary>The DS3 files that hold the pictures (the ones a game version lacks are skipped).</summary>
    public static IEnumerable<string> Sources() => Enumerable.Range(0, 100).Select(n => $"/menu/mapimage/{n:D2}.tpf.dcx");

    /// <summary>The number a texture name ends with ("MENU_MapImage_10011" -> 10011), or null.</summary>
    public static int? PictureOf(string textureName)
    {
        int end = textureName.Length;
        while (end > 0 && !char.IsDigit(textureName[end - 1]))
            end--;
        int start = end;
        while (start > 0 && char.IsDigit(textureName[start - 1]))
            start--;
        return end > start && end - start <= 9 ? int.Parse(textureName[start..end]) : null;
    }

    /// <summary>Halves the picture (2x2 averages) until it is at most <paramref name="maxWidth"/> wide.</summary>
    public static (int Width, int Height, byte[] Rgba) Shrink(int width, int height, byte[] rgba, int maxWidth = MaxWidth)
    {
        while (width > maxWidth && width >= 2 && height >= 2)
        {
            int w = width / 2, h = height / 2;
            var half = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int Px(int dx, int dy) => rgba[((2 * y + dy) * width + 2 * x + dx) * 4 + c];
                        half[(y * w + x) * 4 + c] = (byte)((Px(0, 0) + Px(1, 0) + Px(0, 1) + Px(1, 1) + 2) / 4);
                    }
            (width, height, rgba) = (w, h, half);
        }
        return (width, height, rgba);
    }

    public static byte[] Pack(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"{rgba.Length} bytes for {width}x{height}");
        var file = new byte[12 + rgba.Length];
        Magic.CopyTo(file, 0);
        BitConverter.GetBytes(width).CopyTo(file, 4);
        BitConverter.GetBytes(height).CopyTo(file, 8);
        rgba.CopyTo(file, 12);
        return file;
    }

    public static (int Width, int Height, byte[] Rgba) Unpack(byte[] file)
    {
        if (file.Length < 12 || !file.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("not a picture file");
        int w = BitConverter.ToInt32(file, 4), h = BitConverter.ToInt32(file, 8);
        if (w <= 0 || h <= 0 || file.Length != 12 + (long)w * h * 4)
            throw new InvalidDataException($"picture file size doesn't match {w}x{h}");
        return (w, h, file[12..]);
    }

    /// <summary>A DS3 texture (a DDS file) as 8-bit RGBA, top row first.</summary>
    public static (int Width, int Height, byte[] Rgba) Decode(byte[] dds)
    {
        using var ms = new MemoryStream(dds);
        var file = DdsFile.Load(ms);
        var pixels = new BcDecoder().Decode(file);
        int w = (int)file.header.dwWidth, h = (int)file.header.dwHeight;
        if (pixels.Length < w * h)
            throw new InvalidDataException($"{pixels.Length} pixels decoded for {w}x{h}");
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            ColorRgba32 p = pixels[i];
            rgba[i * 4] = p.r;
            rgba[i * 4 + 1] = p.g;
            rgba[i * 4 + 2] = p.b;
            rgba[i * 4 + 3] = p.a;
        }
        return (w, h, rgba);
    }

    /// <summary>
    /// Writes the pictures named in <paramref name="wanted"/> (PictureIds; -1 = none). Returns PictureId -> file
    /// (relative to the mod folder, with forward slashes) and the report.
    /// </summary>
    public static (Dictionary<int, string> Files, string Report) Write(PorterContext ctx, GameArchives? ds3, IEnumerable<int> wanted)
    {
        var want = wanted.Where(p => p > 0).ToHashSet();
        var dir = ctx.ModPath(Folder);
        if (Directory.Exists(dir))
            foreach (var old in Directory.GetFiles(dir, "*" + Extension))
                File.Delete(old);
        if (ds3 is null || want.Count == 0)
            return ([], ds3 is null ? "no pictures (no DS3 files given)" : "no pictures (no bonfire names one)");
        Directory.CreateDirectory(dir);
        var found = new Dictionary<int, string>();
        var names = new List<string>();
        var failed = new List<string>();
        int files = 0;
        foreach (var path in Sources().Where(ds3.Contains))
        {
            files++;
            foreach (var texture in TPF.Read(ds3.Read(path)).Textures)
            {
                names.Add(texture.Name);
                if (PictureOf(texture.Name) is not int id || !want.Contains(id) || found.ContainsKey(id))
                    continue;
                try
                {
                    var (w, h, rgba) = Decode(texture.Bytes);
                    (w, h, rgba) = Shrink(w, h, rgba);
                    File.WriteAllBytes(Path.Combine(dir, id + Extension), Pack(w, h, rgba));
                    found[id] = $"{Folder}/{id}{Extension}";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed.Add($"{texture.Name} ({ex.Message})");
                }
            }
        }
        var missing = want.Where(p => !found.ContainsKey(p)).Order().ToList();
        var report = $"pictures: {found.Count} of {want.Count} from DS3's menu/mapimage ({files} files, {names.Count} textures)";
        if (missing.Count > 0)
            report += $"; none for {string.Join(" ", missing.Take(12))}{(missing.Count > 12 ? " ..." : "")}";
        if (found.Count == 0 && names.Count > 0)
            report += $"; WARN no texture name ends with a PictureId, e.g. {string.Join(", ", names.Take(6))}";
        if (failed.Count > 0)
            report += $"; WARN not decoded: {string.Join(", ", failed.Take(4))}";
        return (found, report);
    }
}
