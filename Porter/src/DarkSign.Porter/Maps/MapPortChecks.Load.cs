using System.Buffers.Binary;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Offline checks for what a ported map costs the game to hold (2026-11-11 playtest of 1.0.299: hitching on the
/// High Wall, the Undead Settlement close to unplayable): the low detail texture copies are real smaller textures,
/// and full textures are only asked for near the player.
/// </summary>
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> LoadChecks()
    {
        yield return ("load: a DDS loses its largest mip levels and nothing else", DropMipLevels);
        yield return ("load: cube maps, uncompressed and unreadable textures", DropMipEdgeCases);
        yield return ("load: the low detail copy of a map texture is half size; fallbacks map-texture-detail B and C", LowDetailCopies);
        yield return ("load: full textures only near the player", TextureDistance);
        yield return ("load: collision and navmeshes are binary tagfiles by default, the size of DS3's; --xml-collision keeps XML", CollisionFormat);
    }

    /// <summary>A DDS whose every mip level is filled with its own level number (level 0 is all zeros, and so on).</summary>
    internal static byte[] SyntheticDds(int width, int height, int mips, string fourCc = "DXT1", int blockBytes = 8, int faces = 1, int dxgi = 0, int pixelBytes = 0)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        long Level(int l)
        {
            long lw = Math.Max(1, width >> l), lh = Math.Max(1, height >> l);
            return pixelBytes > 0 ? lw * lh * pixelBytes : Math.Max(1, (lw + 3) / 4) * Math.Max(1, (lh + 3) / 4) * blockBytes;
        }
        w.Write(System.Text.Encoding.ASCII.GetBytes("DDS "));
        w.Write(124);
        w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | (pixelBytes > 0 ? 0x8 : 0x80000));
        w.Write(height); w.Write(width);
        w.Write(pixelBytes > 0 ? width * pixelBytes : (int)Level(0));
        w.Write(0); w.Write(mips);
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32);
        if (pixelBytes > 0 && dxgi == 0)
        {
            w.Write(0x41); w.Write(0); w.Write(pixelBytes * 8);            // RGB | alpha, no FOURCC, bit count
            w.Write(0x00FF0000); w.Write(0x0000FF00); w.Write(0x000000FF); w.Write(unchecked((int)0xFF000000));
        }
        else
        {
            w.Write(0x4);
            w.Write(System.Text.Encoding.ASCII.GetBytes(dxgi != 0 ? "DX10" : fourCc));
            for (int i = 0; i < 5; i++) w.Write(0);
        }
        w.Write(0x1000 | 0x400008);
        w.Write(faces == 6 && dxgi == 0 ? 0xFE00 : 0);
        for (int i = 0; i < 3; i++) w.Write(0);
        if (dxgi != 0)
        {
            w.Write(dxgi); w.Write(3); w.Write(faces == 6 ? 0x4 : 0); w.Write(1); w.Write(0);
        }
        for (int face = 0; face < faces; face++)
            for (int l = 0; l < mips; l++)
                w.Write(Enumerable.Repeat((byte)(l + 16 * face), (int)Level(l)).ToArray());
        return ms.ToArray();
    }

    private static void DropMipLevels()
    {
        // 256 x 128 BC1 with its whole chain: 9 levels.
        var dds = SyntheticDds(256, 128, 9);
        var info = DdsMips.Read(dds) ?? throw new Exception("a DXT1 texture is readable");
        Expect(info is { Width: 256, Height: 128, Mips: 9, Faces: 1, BlockBytes: 8, DataOffset: 128 }, "header read");
        Expect(info.DataBytes() == dds.Length - 128, "every level accounted for");

        var half = DdsMips.Drop(dds, 1);
        var small = DdsMips.Read(half) ?? throw new Exception("the smaller texture is readable");
        Expect(small is { Width: 128, Height: 64, Mips: 8 }, "half the width and height, one level fewer");
        Expect(half.Length == dds.Length - (int)info.LevelBytes(0), "exactly the first level is gone");
        Expect(half.AsSpan(128).SequenceEqual(dds.AsSpan(128 + (int)info.LevelBytes(0))), "the remaining levels are DS3's own bytes");
        Expect(half[128] == 1, "the new first level is the old second one");
        Expect(BinaryPrimitives.ReadInt32LittleEndian(half.AsSpan(20)) == small.LevelBytes(0), "the header's size of the first level follows");
        Expect(half.AsSpan(32, 96).SequenceEqual(dds.AsSpan(32, 96)), "the pixel format is untouched");
        Expect(small.DataBytes() * 4 <= info.DataBytes() + 64, "a quarter of the memory");

        var quarter = DdsMips.Drop(dds, 2);
        Expect(DdsMips.Read(quarter) is { Width: 64, Height: 32, Mips: 7 } && quarter[128] == 2, "two levels");
        // Never below 32 pixels on the shorter side, never the last level, and never a texture with one level.
        Expect(DdsMips.Read(DdsMips.Drop(dds, 8)) is { Width: 64, Height: 32 }, "64 x 32 is as small as a 256 x 128 texture gets (shorter side 32)");
        var tiny = SyntheticDds(32, 32, 6);
        Expect(ReferenceEquals(DdsMips.Drop(tiny, 1), tiny), "a 32 pixel texture stays as it is");
        var single = SyntheticDds(1024, 1024, 1);
        Expect(ReferenceEquals(DdsMips.Drop(single, 1), single), "a texture with one level has nothing to drop");
        Expect(ReferenceEquals(DdsMips.Drop(dds, 0), dds), "dropping nothing returns the texture itself");
    }

    private static void DropMipEdgeCases()
    {
        // BC7 through the DX10 header (DS3's normal maps), 16 byte blocks.
        var bc7 = SyntheticDds(512, 512, 10, dxgi: 98, blockBytes: 16);
        var bc7Info = DdsMips.Read(bc7) ?? throw new Exception("a BC7 texture is readable");
        Expect(bc7Info is { BlockBytes: 16, DataOffset: 148 }, "DX10 header");
        var bc7Half = DdsMips.Drop(bc7, 1);
        Expect(DdsMips.Read(bc7Half) is { Width: 256, Height: 256, Mips: 9 } && bc7Half[148] == 1, "BC7 halves");
        Expect(bc7Half.AsSpan(128, 20).SequenceEqual(bc7.AsSpan(128, 20)), "the DX10 header is untouched");

        // A cube map: six faces, each with its own chain; every face loses its own first level.
        var cube = SyntheticDds(128, 128, 8, faces: 6);
        var cubeInfo = DdsMips.Read(cube) ?? throw new Exception("a cube map is readable");
        Expect(cubeInfo.Faces == 6, "six faces");
        var cubeHalf = DdsMips.Drop(cube, 1);
        var cubeSmall = DdsMips.Read(cubeHalf) ?? throw new Exception("the smaller cube map is readable");
        Expect(cubeSmall is { Width: 64, Mips: 7, Faces: 6 }, "cube map halves");
        long face = cubeSmall.DataBytes() / 6;
        for (int f = 0; f < 6; f++)
            Expect(cubeHalf[128 + f * face] == 1 + 16 * f, $"face {f} starts with its own second level");
        var cube10 = SyntheticDds(128, 128, 8, faces: 6, dxgi: 71);
        Expect(DdsMips.Read(DdsMips.Drop(cube10, 1)) is { Width: 64, Faces: 6 }, "DX10 cube map halves");

        // Uncompressed 32 bit.
        var rgba = SyntheticDds(64, 64, 7, pixelBytes: 4);
        var rgbaHalf = DdsMips.Drop(rgba, 1);
        Expect(DdsMips.Read(rgbaHalf) is { Width: 32, Height: 32, Mips: 6, PixelBytes: 4 } && rgbaHalf.Length == 128 + (rgba.Length - 128 - 64 * 64 * 4), "uncompressed halves");
        Expect(BinaryPrimitives.ReadInt32LittleEndian(rgbaHalf.AsSpan(20)) == 32 * 4, "the header's row size follows");

        // Whatever can't be sized is returned whole: a wrong copy would be worse than a large one.
        var odd = SyntheticDds(256, 256, 9, fourCc: "XYZ1");
        Expect(DdsMips.Read(odd) is null && ReferenceEquals(DdsMips.Drop(odd, 1), odd), "an unknown format is left alone");
        var cutShort = SyntheticDds(256, 256, 9)[..2000];
        Expect(DdsMips.Read(cutShort) is null && ReferenceEquals(DdsMips.Drop(cutShort, 1), cutShort), "a file shorter than its header says is left alone");
        Expect(DdsMips.Read(new byte[10]) is null && DdsMips.Read("not a dds file, but long enough to hold a header ........................................................................................................"u8) is null, "not a DDS");
    }

    private static void LowDetailCopies()
    {
        var deflate = new DCX.DcxDfltCompressionInfo(DCX.DfltCompressionPreset.DCX_DFLT_11000_44_9);
        var dds = SyntheticDds(1024, 1024, 11);
        TPF.Texture Texture() => new("m31_00_wall", 0, 0, dds, TPF.TPFPlatform.PC) { Mipmaps = 11 };
        (TPF.Texture Full, TPF.Texture Low) Entries(BXF4 bxf) =>
            (TPF.Read(bxf.Files[0].Bytes).Textures.Single(), TPF.Read(bxf.Files[1].Bytes).Textures.Single());

        // The default: DS3's texture in full, and a half size low detail copy named with _l.
        var (full, low) = Entries(MapTextures.BuildBinder([Texture()], deflate));
        Expect(full.Bytes.AsSpan().SequenceEqual(dds) && full.Mipmaps == 11, "the full copy is DS3's texture, untouched");
        Expect(low.Name == "m31_00_wall_l" && DdsMips.Read(low.Bytes) is { Width: 512, Height: 512, Mips: 10 } && low.Mipmaps == 10,
            "the low detail copy is half the width and height");
        Expect(low.Bytes.Length * 3 < full.Bytes.Length, "the low detail copy takes about a quarter of the memory");
        Expect(MapTextures.LevelsDropped(MapTextures.Detail.Standard) == (0, 1), "default levels");

        // B: half size textures, quarter size low detail copies.
        var (fullDropB, lowDropB) = MapTextures.LevelsDropped(MapTextures.Detail.Half);
        var (halfFull, halfLow) = Entries(MapTextures.BuildBinder([Texture()], deflate, fullDropB, lowDropB));
        Expect(DdsMips.Read(halfFull.Bytes) is { Width: 512 } && DdsMips.Read(halfLow.Bytes) is { Width: 256 }, "--half-textures");
        // A full copy read back from an earlier run's binder is already cut once: only the low copy shrinks further.
        var cutOnce = new TPF.Texture("m31_00_wall", 0, 0, halfFull.Bytes, TPF.TPFPlatform.PC);
        var (again, againLow) = Entries(MapTextures.BuildBinder([cutOnce], deflate, fullDropB, lowDropB, new HashSet<string> { "m31_00_wall" }));
        Expect(DdsMips.Read(again.Bytes) is { Width: 512 } && DdsMips.Read(againLow.Bytes) is { Width: 256 }, "a kept texture isn't cut twice");

        // C: as up to 1.0.299, both copies full.
        var (fullDropC, lowDropC) = MapTextures.LevelsDropped(MapTextures.Detail.FullLow);
        var (oldFull, oldLow) = Entries(MapTextures.BuildBinder([Texture()], deflate, fullDropC, lowDropC));
        Expect(oldFull.Bytes.AsSpan().SequenceEqual(dds) && oldLow.Bytes.AsSpan().SequenceEqual(dds), "--full-low-textures");

        // The report's memory figures.
        var (memFull, memLow, large, unread) = MapTextures.Memory(
            [Texture(), new TPF.Texture("big", 0, 0, SyntheticDds(4096, 4096, 13), TPF.TPFPlatform.PC),
             new TPF.Texture("odd", 0, 0, SyntheticDds(64, 64, 1, fourCc: "XYZ1"), TPF.TPFPlatform.PC)], 0, 1);
        Expect(large == 1 && unread == 1, "one 4096 pixel texture, one unreadable");
        Expect(memLow * 3 < memFull && memFull > 11_000_000, "low detail is about a quarter of full detail");

        var options = MapPorter.Options.Parse(["--half-textures"]);
        Expect(options.TextureDetail == MapTextures.Detail.Half, "--half-textures parsed");
        Expect(MapPorter.Options.Parse(["--full-low-textures"]).TextureDetail == MapTextures.Detail.FullLow, "--full-low-textures parsed");
        Expect(MapPorter.Options.Parse([]).TextureDetail == MapTextures.Detail.Standard, "half size low detail copies by default");
    }

    private static void TextureDistance()
    {
        var def = new PARAMDEF { ParamType = "PARTS_DRAW_PARAM_ST", DataVersion = 1 };
        foreach (var name in new[] { "tex_lv01_BorderDist", "tex_lv01_PlayDist", "drawDist" })
            def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.f32, name));
        PARAM.Row Row(bool legacy)
        {
            var param = new PARAM { ParamType = def.ParamType, Rows = [] };
            param.ApplyParamdef(def);
            param.Rows.Add(new PARAM.Row(MapDrawParams.TemplateRow, "", def));
            return param[MapDrawParams.AddRow(param, legacy)]!;
        }
        float Cell(PARAM.Row row, string name) => (float)row.Cells.Single(c => c.Def.InternalName == name).Value;
        var row = Row(false);
        Expect(Cell(row, "tex_lv01_BorderDist") == MapDrawParams.TextureDistance && Cell(row, "tex_lv01_PlayDist") == MapDrawParams.TextureBand,
            "full textures within TextureDistance");
        Expect(MapDrawParams.TextureDistance < MapDrawParams.LegacyTextureDistance && MapDrawParams.TextureDistance >= 64, "nearer than before, never in the player's face");
        Expect(Cell(row, "drawDist") > 9000f, "pieces themselves still draw at any distance");
        var legacy = Row(true);
        Expect(Cell(legacy, "tex_lv01_BorderDist") == 256f && Cell(legacy, "tex_lv01_PlayDist") == 32f, "--full-low-textures keeps JortPob's distances");
    }

    private static void CollisionFormat()
    {
        Expect(MapPorter.Options.Parse([]).BinaryCollision, "binary by default");
        Expect(!MapPorter.Options.Parse(["--xml-collision"]).BinaryCollision, "--xml-collision (fallback map-collision B)");
        Expect(MapPorter.Options.Parse(["--binary-collision"]).BinaryCollision, "older profiles' --binary-collision still accepted");
        var simd = MapPorter.Options.Parse(["--simd"]);
        Expect(simd.BinaryCollision && simd.KeepSimdTree, "--simd alone is fallback C (binary, DS3's tree)");

        // The binary file is about the DS3 file's size; the XML one many times it. Both decode to the same triangles.
        var ds3 = MapSelfTest.SyntheticDs3Collision(everything: true);
        var binary = CollisionPort.Convert(ds3, null, xml: false);
        var xml = CollisionPort.Convert(ds3, null, xml: true);
        Expect(binary.Triangles == xml.Triangles && binary.Triangles > 0, "same triangles either way");
        Expect(xml.Bytes.Length > 5 * binary.Bytes.Length, $"XML {xml.Bytes.Length} bytes is many times binary {binary.Bytes.Length}");
        Expect(binary.Bytes.AsSpan(4, 4).SequenceEqual("TAG0"u8), "a Havok 2018 binary tagfile");
    }
}
