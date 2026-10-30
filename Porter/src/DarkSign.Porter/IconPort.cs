using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Gives ported items their Dark Souls III icons. DS3 keeps one 512x512 BC7 image per icon
/// (/menu/knowledge/menu_knowledge_NNNNN.tpf.dcx). Elden Ring shows two images per item: a small inventory icon cut
/// from a sprite sheet (SB_Icon_* textures in menu/{hi,low}/01_common.tpf.dcx, positioned by .layout files in
/// 01_common.sblytbnd.dcx), and a large one (MENU_Knowledge_NNNNN in menu/{hi,low}/00_solo.tpfbhd/.tpfbdt).
/// The DS3 images go into a new sheet, SB_Icon_DS3, with its own layout, and into 00_solo under the new icon IDs.
/// BC7 blocks are copied as they are, so no image is re-encoded.
/// </summary>
public static class IconPort
{
    private const string Sheet = "SB_Icon_DS3";
    private const int Icon = 512, Columns = 16, BlockBytes = 16;
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

        var (sheet, positions) = BuildSheet(images);
        foreach (var quality in new[] { "hi", "low" })
        {
            AddSheet(ctx, er, quality, sheet);
            AddLayout(ctx, er, quality, positions);
            AddKnowledge(ctx, er, quality, images);
        }
        Console.WriteLine($"icons: {images.Count} added" + (missing.Count > 0 ? $", {missing.Count} DS3 icons not found ({string.Join(", ", missing.Take(10))})" : ""));
    }

    /// <summary>The DS3 icon's DDS, if it is a 512x512 single-mip BC7 image as expected.</summary>
    private static byte[]? Ds3Icon(GameArchives ds3, int iconId)
    {
        var path = $"/menu/knowledge/menu_knowledge_{iconId:D5}.tpf.dcx";
        if (iconId <= 0 || !ds3.Contains(path))
            return null;
        var texture = TPF.Read(ds3.Read(path)).Textures.FirstOrDefault();
        if (texture is null)
            return null;
        var (width, height, format) = DdsInfo(texture.Bytes);
        return width == Icon && height == Icon && format == Bc7 ? texture.Bytes : null;
    }

    /// <summary>Lays the icons out 16 to a row in one BC7 texture and returns where each one went.</summary>
    private static (byte[] Dds, Dictionary<int, (int X, int Y)> Positions) BuildSheet(List<(int NewIconId, byte[] Dds)> images)
    {
        int rows = Math.Max(1, (images.Count + Columns - 1) / Columns);
        int width = Columns * Icon, height = rows * Icon;
        int sheetBlocksPerRow = width / 4, iconBlocksPerRow = Icon / 4;
        var data = new byte[sheetBlocksPerRow * (height / 4) * BlockBytes];
        var positions = new Dictionary<int, (int, int)>();
        for (int i = 0; i < images.Count; i++)
        {
            int col = i % Columns, row = i / Columns;
            var source = images[i].Dds.AsSpan(DdsDataOffset);
            for (int by = 0; by < iconBlocksPerRow; by++)
            {
                var from = source.Slice(by * iconBlocksPerRow * BlockBytes, iconBlocksPerRow * BlockBytes);
                int to = ((row * iconBlocksPerRow + by) * sheetBlocksPerRow + col * iconBlocksPerRow) * BlockBytes;
                from.CopyTo(data.AsSpan(to));
            }
            positions[images[i].NewIconId] = (col * Icon, row * Icon);
        }
        return (Dds(width, height, Bc7, data), positions);
    }

    private static void AddSheet(PorterContext ctx, GameArchives er, string quality, byte[] sheet)
    {
        var path = $"/menu/{quality}/01_common.tpf.dcx";
        var (bytes, compression) = er.ReadWithCompression(path);
        var tpf = TPF.Read(bytes);
        tpf.Textures.RemoveAll(t => t.Name == Sheet);
        tpf.Textures.Add(new TPF.Texture(Sheet, TpfBc7, 0, sheet, TPF.TPFPlatform.PC));
        File.WriteAllBytes(ctx.ModPath("menu", quality, "01_common.tpf.dcx"), DCX.Compress(tpf.Write(), compression));
    }

    /// <summary>Adds SB_Icon_DS3.layout, written like Elden Ring's own SB_Icon_00.layout.</summary>
    private static void AddLayout(PorterContext ctx, GameArchives er, string quality, Dictionary<int, (int X, int Y)> positions)
    {
        var path = $"/menu/{quality}/01_common.sblytbnd.dcx";
        var (bytes, compression) = er.ReadWithCompression(path);
        var bnd = BND4.Read(bytes);
        var template = bnd.Files.First(f => ItemText.Leaf(f.Name).Equals("SB_Icon_00.layout", StringComparison.OrdinalIgnoreCase));
        var templateText = Encoding.UTF8.GetString(template.Bytes);
        var newline = templateText.Contains("\r\n") ? "\r\n" : "\n";
        var bom = template.Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);

        var text = new StringBuilder();
        text.Append($"<TextureAtlas imagePath=\"{Sheet}.png\">{newline}");
        foreach (var (iconId, (x, y)) in positions.OrderBy(p => p.Key))
            text.Append($"\t<SubTexture name=\"MENU_ItemIcon_{iconId:D5}.png\" x=\"{x}\" y=\"{y}\" width=\"{Icon}\" height=\"{Icon}\" half=\"0\"/>{newline}");
        text.Append($"</TextureAtlas>{newline}");
        var layout = Encoding.UTF8.GetBytes(text.ToString());
        if (bom)
            layout = [.. Encoding.UTF8.Preamble, .. layout];

        var name = template.Name[..^"SB_Icon_00.layout".Length] + Sheet + ".layout";
        bnd.Files.RemoveAll(f => f.Name == name);
        bnd.Files.Add(new BinderFile(template.Flags, bnd.Files.Max(f => f.ID) + 1, name, layout));
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
