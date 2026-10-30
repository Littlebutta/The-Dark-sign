using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Gives ported items their Dark Souls III icons. DS3 keeps one 512x512 BC7 image per icon
/// (/menu/knowledge/menu_knowledge_NNNNN.tpf.dcx). Elden Ring shows two images per item: a small inventory icon cut
/// from a sprite sheet (SB_Icon_* textures in menu/{hi,low}/01_common.tpf.dcx, positioned by .layout files in
/// 01_common.sblytbnd.dcx), and a large one (MENU_Knowledge_NNNNN in menu/{hi,low}/00_solo.tpfbhd/.tpfbdt).
/// The large images go into 00_solo as they are. For the inventory icons the DS3 images are scaled down to 160x160,
/// the size of Elden Ring's own, and packed into new sheets (SB_Icon_DS3_00, ...) no bigger than the game's own
/// (4096x2048), each with its own layout.
/// </summary>
public static class IconPort
{
    private const string SheetPrefix = "SB_Icon_DS3_";
    private const int Source = 512, Icon = 160, SheetWidth = 4096, MaxSheetHeight = 2048;
    private const int Columns = SheetWidth / Icon, RowsPerSheet = MaxSheetHeight / Icon, PerSheet = Columns * RowsPerSheet;
    private const int Bc7 = 98; // DXGI_FORMAT_BC7_UNORM
    private const byte TpfBc7 = 102;

    /// <summary>A DS3 icon and the Elden Ring icon ID it gets.</summary>
    public sealed record Entry(int Ds3IconId, int NewIconId);

    public static void Write(PorterContext ctx, GameArchives er, GameArchives ds3, IReadOnlyList<Entry> entries)
    {
        var images = new List<(int NewIconId, byte[] Dds)>();
        var missing = new List<int>();
        foreach (var entry in entries)
        {
            var dds = Ds3Icon(ds3, entry.Ds3IconId);
            if (dds is null)
                missing.Add(entry.Ds3IconId);
            else
                images.Add((entry.NewIconId, dds));
        }

        var sheets = BuildSheets(images);
        foreach (var quality in new[] { "hi", "low" })
        {
            AddSheets(ctx, er, quality, sheets);
            AddLayouts(ctx, er, quality, sheets);
            AddKnowledge(ctx, er, quality, images);
        }
        Console.WriteLine($"icons: {images.Count} added on {sheets.Count} sheet(s)" + (missing.Count > 0 ? $", {missing.Count} DS3 icons not found ({string.Join(", ", missing.Take(10))})" : ""));
    }

    private sealed record Sheet(string Name, byte[] Dds, Dictionary<int, (int X, int Y)> Positions);

    /// <summary>The DS3 icon's DDS, if it is a 512x512 BC7 image as expected.</summary>
    private static byte[]? Ds3Icon(GameArchives ds3, int iconId)
    {
        var path = $"/menu/knowledge/menu_knowledge_{iconId:D5}.tpf.dcx";
        if (iconId <= 0 || !ds3.Contains(path))
            return null;
        var texture = TPF.Read(ds3.Read(path)).Textures.FirstOrDefault();
        if (texture is null)
            return null;
        var (width, height, format) = DdsInfo(texture.Bytes);
        return width == Source && height == Source && format == Bc7 ? texture.Bytes : null;
    }

    /// <summary>Scales each icon to 160x160 and lays them out 25 to a row, 300 to a sheet, BC7 like the game's own.</summary>
    private static List<Sheet> BuildSheets(List<(int NewIconId, byte[] Dds)> images)
    {
        var sheets = new List<Sheet>();
        var decoder = new BcDecoder();
        var encoder = new BcEncoder(CompressionFormat.Bc7);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        for (int first = 0; first < Math.Max(1, images.Count); first += PerSheet)
        {
            var batch = images.Skip(first).Take(PerSheet).ToList();
            int rows = Math.Max(1, (batch.Count + Columns - 1) / Columns);
            int height = 256;
            while (height < rows * Icon)
                height *= 2;
            var rgba = new byte[SheetWidth * height * 4];
            var positions = new Dictionary<int, (int, int)>();
            for (int i = 0; i < batch.Count; i++)
            {
                int x = i % Columns * Icon, y = i / Columns * Icon;
                var pixels = decoder.DecodeRaw(batch[i].Dds[DdsDataOffset..], Source, Source, CompressionFormat.Bc7);
                var small = Downscale(pixels, Source, Icon);
                for (int row = 0; row < Icon; row++)
                    small.AsSpan(row * Icon * 4, Icon * 4).CopyTo(rgba.AsSpan(((y + row) * SheetWidth + x) * 4));
                positions[batch[i].NewIconId] = (x, y);
            }
            var data = encoder.EncodeToRawBytes(rgba, SheetWidth, height, PixelFormat.Rgba32, 0, out _, out _);
            sheets.Add(new Sheet($"{SheetPrefix}{sheets.Count:D2}", Dds(SheetWidth, height, Bc7, data), positions));
        }
        return sheets;
    }

    /// <summary>Area-averaged downscale of a square RGBA image, weighting colour by alpha so edges don't darken.</summary>
    internal static byte[] Downscale(ColorRgba32[] pixels, int from, int to)
    {
        var result = new byte[to * to * 4];
        double scale = (double)from / to;
        for (int dy = 0; dy < to; dy++)
        for (int dx = 0; dx < to; dx++)
        {
            double r = 0, g = 0, b = 0, a = 0, weight = 0;
            double y0 = dy * scale, y1 = y0 + scale, x0 = dx * scale, x1 = x0 + scale;
            for (int sy = (int)y0; sy < Math.Min(from, (int)Math.Ceiling(y1)); sy++)
            {
                double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                for (int sx = (int)x0; sx < Math.Min(from, (int)Math.Ceiling(x1)); sx++)
                {
                    double w = wy * (Math.Min(sx + 1, x1) - Math.Max(sx, x0));
                    var p = pixels[sy * from + sx];
                    double pa = p.a / 255.0;
                    r += p.r * pa * w; g += p.g * pa * w; b += p.b * pa * w; a += pa * w;
                    weight += w;
                }
            }
            int o = (dy * to + dx) * 4;
            if (a > 0)
            {
                result[o] = (byte)Math.Round(Math.Clamp(r / a, 0, 255));
                result[o + 1] = (byte)Math.Round(Math.Clamp(g / a, 0, 255));
                result[o + 2] = (byte)Math.Round(Math.Clamp(b / a, 0, 255));
            }
            result[o + 3] = (byte)Math.Round(Math.Clamp(a / weight * 255, 0, 255));
        }
        return result;
    }

    private static void AddSheets(PorterContext ctx, GameArchives er, string quality, List<Sheet> sheets)
    {
        var path = $"/menu/{quality}/01_common.tpf.dcx";
        var (bytes, compression) = er.ReadWithCompression(path);
        var tpf = TPF.Read(bytes);
        tpf.Textures.RemoveAll(t => t.Name.StartsWith(SheetPrefix, StringComparison.Ordinal));
        foreach (var sheet in sheets)
            tpf.Textures.Add(new TPF.Texture(sheet.Name, TpfBc7, 0, sheet.Dds, TPF.TPFPlatform.PC));
        File.WriteAllBytes(ctx.ModPath("menu", quality, "01_common.tpf.dcx"), DCX.Compress(tpf.Write(), compression));
    }

    /// <summary>Adds a layout per sheet, written like Elden Ring's own SB_Icon_00.layout.</summary>
    private static void AddLayouts(PorterContext ctx, GameArchives er, string quality, List<Sheet> sheets)
    {
        var path = $"/menu/{quality}/01_common.sblytbnd.dcx";
        var (bytes, compression) = er.ReadWithCompression(path);
        var bnd = BND4.Read(bytes);
        var template = bnd.Files.First(f => ItemText.Leaf(f.Name).Equals("SB_Icon_00.layout", StringComparison.OrdinalIgnoreCase));
        var templateText = Encoding.UTF8.GetString(template.Bytes);
        var newline = templateText.Contains("\r\n") ? "\r\n" : "\n";
        var bom = template.Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var folder = template.Name[..^"SB_Icon_00.layout".Length];
        bnd.Files.RemoveAll(f => ItemText.Leaf(f.Name).StartsWith(SheetPrefix, StringComparison.Ordinal));

        foreach (var sheet in sheets)
        {
            var text = new StringBuilder();
            text.Append($"<TextureAtlas imagePath=\"{sheet.Name}.png\">{newline}");
            foreach (var (iconId, (x, y)) in sheet.Positions.OrderBy(p => p.Key))
                text.Append($"\t<SubTexture name=\"MENU_ItemIcon_{iconId:D5}.png\" x=\"{x}\" y=\"{y}\" width=\"{Icon}\" height=\"{Icon}\" half=\"0\"/>{newline}");
            text.Append($"</TextureAtlas>{newline}");
            var layout = Encoding.UTF8.GetBytes(text.ToString());
            if (bom)
                layout = [.. Encoding.UTF8.Preamble, .. layout];
            bnd.Files.Add(new BinderFile(template.Flags, bnd.Files.Max(f => f.ID) + 1, folder + sheet.Name + ".layout", layout));
        }
        File.WriteAllBytes(ctx.ModPath("menu", quality, "01_common.sblytbnd.dcx"), DCX.Compress(bnd.Write(), compression));
    }

    /// <summary>Adds MENU_Knowledge_NNNNN (the large item image) for each new icon to 00_solo.</summary>
    private static void AddKnowledge(PorterContext ctx, GameArchives er, string quality, List<(int NewIconId, byte[] Dds)> images)
    {
        var bhdPath = $"/menu/{quality}/00_solo.tpfbhd";
        var bxf = BXF4.Read(er.Read(bhdPath), er.Read(bhdPath[..^3] + "bdt"));
        var template = bxf.Files.First(f => ItemText.Leaf(f.Name).StartsWith("MENU_Knowledge_", StringComparison.OrdinalIgnoreCase));
        var templateTpf = TPF.Read(DCX.Decompress(template.Bytes, out var compression));
        var folder = template.Name[..^ItemText.Leaf(template.Name).Length];
        int nextId = bxf.Files.Max(f => f.ID) + 1;

        foreach (var (iconId, dds) in images)
        {
            var name = $"MENU_Knowledge_{iconId:D5}";
            var tpf = new TPF { Platform = templateTpf.Platform, Encoding = templateTpf.Encoding, Flag2 = templateTpf.Flag2 };
            tpf.Textures.Add(new TPF.Texture(name, TpfBc7, 0, dds, TPF.TPFPlatform.PC));
            var fileName = folder + name + ".tpf.dcx";
            bxf.Files.RemoveAll(f => f.Name == fileName);
            bxf.Files.Add(new BinderFile(template.Flags, nextId++, fileName, DCX.Compress(tpf.Write(), compression)));
        }
        bxf.Write(ctx.ModPath("menu", quality, "00_solo.tpfbhd"), ctx.ModPath("menu", quality, "00_solo.tpfbdt"));
    }

    // A DDS with a DX10 header: 4 magic + 124 header + 20 DX10 bytes before the data.
    private const int DdsDataOffset = 148;

    private static (int Width, int Height, int Dxgi) DdsInfo(byte[] dds) =>
        dds.Length > DdsDataOffset && Encoding.ASCII.GetString(dds, 84, 4) == "DX10"
            ? (BitConverter.ToInt32(dds, 16), BitConverter.ToInt32(dds, 12), BitConverter.ToInt32(dds, 128))
            : (0, 0, 0);

    private static byte[] Dds(int width, int height, int dxgi, byte[] data)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("DDS "));
        w.Write(124);                                // header size
        w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); // caps | height | width | pixelformat | linearsize
        w.Write(height); w.Write(width);
        w.Write(data.Length);                        // linear size
        w.Write(0); w.Write(1);                      // depth, mip count
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32); w.Write(0x4);                   // pixel format: size, FOURCC
        w.Write(Encoding.ASCII.GetBytes("DX10"));
        for (int i = 0; i < 5; i++) w.Write(0);
        w.Write(0x1000);                             // caps: texture
        for (int i = 0; i < 4; i++) w.Write(0);
        w.Write(dxgi);
        w.Write(3);                                  // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        w.Write(0); w.Write(1); w.Write(0);          // misc, array size, misc2
        w.Write(data);
        return ms.ToArray();
    }
}
