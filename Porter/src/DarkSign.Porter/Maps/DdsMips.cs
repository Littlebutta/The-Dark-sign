using System.Buffers.Binary;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Makes a smaller copy of a DDS texture by dropping its largest mip levels. Nothing is decoded or re-encoded: a
/// DDS file stores every mip level after the header, largest first, so the half size texture is the same file
/// without its first level and with the header's size and level count changed. That keeps DS3's own (offline,
/// high quality) mip levels and costs no build time, where JortPob's low detail copies (Common/DDS.cs Scale) are
/// decoded, resized and re-encoded as BC1.
/// </summary>
public static class DdsMips
{
    /// <summary>What a DDS file holds, as far as dropping levels needs to know.</summary>
    public sealed record Info(int Width, int Height, int Mips, int Faces, int BlockBytes, int PixelBytes, int DataOffset)
    {
        /// <summary>The bytes of one face's mip level.</summary>
        public long LevelBytes(int level)
        {
            long w = Math.Max(1, Width >> level), h = Math.Max(1, Height >> level);
            return BlockBytes > 0 ? Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * BlockBytes : w * h * PixelBytes;
        }

        /// <summary>The bytes of every face's levels from <paramref name="first"/> on: what the texture takes in memory.</summary>
        public long DataBytes(int first = 0)
        {
            long sum = 0;
            for (int level = first; level < Mips; level++)
                sum += LevelBytes(level);
            return sum * Faces;
        }
    }

    private const int Magic = 0x20534444; // "DDS "
    private const int FlagPitch = 0x8, FlagLinearSize = 0x80000;
    private const int Caps2Cubemap = 0x200, Caps2Volume = 0x200000;
    private const int FourCcDx10 = 0x30315844; // "DX10"

    /// <summary>Reads the header; null for anything this class doesn't handle (volume textures, formats it can't size).</summary>
    public static Info? Read(ReadOnlySpan<byte> dds)
    {
        if (dds.Length < 128 || BinaryPrimitives.ReadInt32LittleEndian(dds) != Magic)
            return null;
        int I(int offset, ReadOnlySpan<byte> d) => BinaryPrimitives.ReadInt32LittleEndian(d[offset..]);
        int height = I(12, dds), width = I(16, dds), depth = I(24, dds), mips = Math.Max(1, I(28, dds));
        int pfFlags = I(80, dds), fourCc = I(84, dds), bitCount = I(88, dds), caps2 = I(112, dds);
        if (width <= 0 || height <= 0 || (caps2 & Caps2Volume) != 0 || depth > 1)
            return null;
        int faces = (caps2 & Caps2Cubemap) != 0 ? System.Numerics.BitOperations.PopCount((uint)(caps2 & 0xFC00)) : 1;
        int dataOffset = 128, block = 0, pixel = 0;
        if ((pfFlags & 0x4) != 0 && fourCc == FourCcDx10)
        {
            if (dds.Length < 148)
                return null;
            dataOffset = 148;
            int dxgi = I(128, dds), dimension = I(132, dds), misc = I(136, dds), arraySize = Math.Max(1, I(140, dds));
            if (dimension != 3) // only 2D textures
                return null;
            faces = arraySize * ((misc & 0x4) != 0 ? 6 : 1);
            (block, pixel) = dxgi switch
            {
                >= 70 and <= 72 => (8, 0),   // BC1
                >= 73 and <= 78 => (16, 0),  // BC2, BC3
                >= 79 and <= 81 => (8, 0),   // BC4
                >= 82 and <= 84 => (16, 0),  // BC5
                >= 94 and <= 99 => (16, 0),  // BC6H, BC7
                >= 27 and <= 32 => (0, 4),   // R8G8B8A8
                >= 87 and <= 93 => (0, 4),   // B8G8R8A8, B8G8R8X8
                >= 9 and <= 14 => (0, 8),    // R16G16B16A16
                >= 1 and <= 4 => (0, 16),    // R32G32B32A32
                >= 60 and <= 64 => (0, 1),   // R8, A8
                >= 48 and <= 52 => (0, 2),   // R8G8
                _ => (0, 0),
            };
        }
        else if ((pfFlags & 0x4) != 0)
        {
            block = fourCc switch
            {
                0x31545844 => 8,                    // DXT1
                0x32545844 or 0x33545844 => 16,     // DXT2, DXT3
                0x34545844 or 0x35545844 => 16,     // DXT4, DXT5
                0x31495441 or 0x55344342 or 0x53344342 => 8,   // ATI1, BC4U, BC4S
                0x32495441 or 0x55354342 or 0x53354342 => 16,  // ATI2, BC5U, BC5S
                _ => 0,
            };
        }
        else if (bitCount is 8 or 16 or 24 or 32)
            pixel = bitCount / 8;
        if (faces <= 0 || (block == 0 && pixel == 0))
            return null;
        var info = new Info(width, height, mips, faces, block, pixel, dataOffset);
        // A file shorter than its header says is not one to cut levels out of.
        return dds.Length - dataOffset >= info.DataBytes() ? info : null;
    }

    /// <summary>
    /// How many levels <see cref="Drop"/> would take off: at most <paramref name="levels"/>, never the last level,
    /// and never below <paramref name="minSize"/> pixels on the shorter side (small textures stay as they are).
    /// </summary>
    public static int Droppable(Info info, int levels, int minSize = 32)
    {
        int drop = 0;
        while (drop < levels && drop + 1 < info.Mips && Math.Min(info.Width, info.Height) >> (drop + 1) >= minSize)
            drop++;
        return drop;
    }

    /// <summary>
    /// The texture without its <paramref name="levels"/> largest mip levels (each one halves width and height).
    /// Returns the input unchanged when it can't be read or has nothing to drop (see <see cref="Droppable"/>).
    /// </summary>
    public static byte[] Drop(byte[] dds, int levels, int minSize = 32)
    {
        if (levels <= 0 || Read(dds) is not { } info)
            return dds;
        int drop = Droppable(info, levels, minSize);
        if (drop == 0)
            return dds;
        long faceBytes = info.DataBytes() / info.Faces, kept = info.DataBytes(drop) / info.Faces;
        var result = new byte[info.DataOffset + kept * info.Faces];
        dds.AsSpan(0, info.DataOffset).CopyTo(result);
        for (int face = 0; face < info.Faces; face++)
            dds.AsSpan((int)(info.DataOffset + face * faceBytes + (faceBytes - kept)), (int)kept)
                .CopyTo(result.AsSpan((int)(info.DataOffset + face * kept)));

        int width = Math.Max(1, info.Width >> drop), height = Math.Max(1, info.Height >> drop);
        void Set(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), value);
        Set(12, height);
        Set(16, width);
        Set(28, info.Mips - drop);
        int flags = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(8));
        var small = info with { Width = width, Height = height, Mips = info.Mips - drop };
        if ((flags & FlagLinearSize) != 0)
            Set(20, (int)small.LevelBytes(0));
        else if ((flags & FlagPitch) != 0 && info.PixelBytes > 0)
            Set(20, width * info.PixelBytes);
        return result;
    }
}
