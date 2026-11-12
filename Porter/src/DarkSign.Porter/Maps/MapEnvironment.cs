using System.Buffers.Binary;
using System.Globalization;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The colour of the light a ported map's pieces get from around them. Elden Ring lights map pieces with what was
/// baked for the map they stand in: per environment map point, a light volume (GIIV: four BC7 volume textures W, X, Y
/// and Z, the light arriving from four directions at every place inside it) and a reflection cube (GILM, BC6H), one
/// set per time of day (00-06) and detail level, in map/mAA/mAA_BB_00_00/mAA_BB_00_00_envmap_NN_{high,middle,low}_00.tpfbnd.
/// <see cref="MapLighting.AddAmbient"/> (PR #124) applies the host colosseum's environment point over the whole DS3
/// map, so every DS3 map piece was lit by light baked inside an Elden Ring arena (map-diag envmap, 2026-11-12):
/// <list type="bullet">
/// <item>m45_01 (Caelid, map 2): light volume mean 252 136 132, reflections 0.054 0.021 0.019: the red map surfaces.</item>
/// <item>m45_00 (Leyndell, map 1): 252 227 171, golden.</item>
/// <item>m45_02 (Limgrave, map 3): 228 248 251 and bright bluish reflections (0.116 0.141 0.148): washed out, pale grass.</item>
/// </list>
/// Characters are lit from a separate irradiance volume (ivinfo), which is why the player looked right on red ground.
/// The baked light belongs to the colosseum's walls, not to the DS3 map's, so its shape is meaningless there; this
/// writes the slot's own environment map binders as the host's, every name and size the same, with each light volume
/// one even grey (its brightest channel's mean, alpha its mean) and each reflection cube one even grey of its mean
/// brightness: the host's strength, without its colour or its arena. DS3's textures, sun and fog then give the colour.
/// Fallback "map-env-light" B (--host-env-light) keeps the host's files.
/// </summary>
public static class MapEnvironment
{
    public const int DxgiBc7 = 98, DxgiBc7Srgb = 99, DxgiBc6hUf16 = 95;

    /// <summary>The host's environment map binders.</summary>
    public static List<string> Binders(GameArchives er, MapId host)
    {
        var prefix = $"/map/{host.AreaFolder}/{host}/{host}_envmap_";
        return er.Entries.Where(e => e.Path is not null && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                     && e.Path.EndsWith(".tpfbnd.dcx", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path!).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>port-maps step. Returns a line for the log.</summary>
    public static string Write(PorterContext ctx, GameArchives er, MapId erId, MapId hostId, bool hostEnvLight, MapBakedLight.Volume? baked = null)
    {
        var binders = Binders(er, hostId);
        string Target(string path) => ctx.ModPath(MapPorter.ModParts(path.Replace(hostId.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase)));
        if (hostEnvLight)
        {
            // An earlier default build's neutral files would otherwise stay in the mod folder.
            int removed = 0;
            foreach (var path in binders)
                if (File.Exists(Target(path)))
                {
                    File.Delete(Target(path));
                    removed++;
                }
            return $"environment light: the host's own (--host-env-light){(removed > 0 ? $"; {removed} neutral binders from an earlier build removed" : "")}";
        }
        if (binders.Count == 0)
            return $"environment light: the host {hostId} has no environment map binders; nothing written";

        var volumes = new List<string>();
        var cubes = new List<string>();
        int volumeCount = 0, cubeCount = 0, bakedFiles = 0;
        var skipped = new List<string>();
        foreach (var path in binders)
        {
            var (bytes, compression) = er.ReadWithCompression(path);
            var bnd = BND4.Read(bytes);
            foreach (var file in bnd.Files)
            {
                (byte[] inner, DCX.CompressionInfo? innerCompression) = DCX.Is(file.Bytes) ? GameArchives.SplitCompression(file.Bytes) : (file.Bytes, null);
                if (!TPF.Is(inner))
                    continue;
                var tpf = TPF.Read(inner);
                foreach (var t in tpf.Textures)
                {
                    var name = t.Name ?? "";
                    var result = name.Contains("_GIIV", StringComparison.OrdinalIgnoreCase) ? NeutraliseVolume(t)
                        : name.Contains("_GILM", StringComparison.OrdinalIgnoreCase) ? NeutraliseCube(t)
                        : null;
                    if (result is null)
                        continue;
                    if (result.StartsWith('!'))
                    {
                        skipped.Add($"{name} {result[1..]}");
                        continue;
                    }
                    if (name.Contains("_GIIV", StringComparison.OrdinalIgnoreCase))
                    {
                        volumeCount++;
                        if (volumes.Count < 1 && name.EndsWith("_W", StringComparison.OrdinalIgnoreCase))
                            volumes.Add($"{name}: {result}");
                    }
                    else
                    {
                        cubeCount++;
                        if (cubes.Count < 1)
                            cubes.Add($"{name}: {result}");
                    }
                    t.Name = name.Replace(hostId.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);
                }
                var written = tpf.Write();
                file.Bytes = innerCompression is null ? written : DCX.Compress(written, innerCompression);
                file.Name = file.Name.Replace(hostId.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            if (baked is not null)
                bakedFiles += MapBakedLight.AddTo(bnd, baked, path, hostId, erId);
            var target = Target(path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, DCX.Compress(bnd.Write(), compression));
        }
        var line = $"environment light: {binders.Count} environment map binders written from {hostId}'s with {volumeCount} light volume textures and "
                   + $"{cubeCount} reflection cubes made one even grey (the colosseum's colour and arena taken out)";
        if (volumes.Count > 0)
            line += $"; e.g. {volumes[0]}";
        if (cubes.Count > 0)
            line += $"; {cubes[0]}";
        if (baked is not null)
            line += $"; {bakedFiles} baked light textures ({MapBakedLight.VolumeTag}) added";
        if (skipped.Count > 0)
            line += $"; {skipped.Count} kept as they were ({string.Join(", ", skipped.Take(4))})";
        return line;
    }

    /// <summary>Where a DDS file's pixel data starts and its DXGI format (0 for a file without the DX10 header).</summary>
    public static (int Offset, int Dxgi) Layout(byte[] dds)
    {
        if (dds.Length < 128 || BinaryPrimitives.ReadInt32LittleEndian(dds) != 0x20534444)
            return (-1, 0);
        bool dx10 = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(84)) == 0x30315844;
        return dx10 && dds.Length >= 148 ? (148, BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(128))) : (128, 0);
    }

    /// <summary>
    /// Makes a BC7 light volume one even grey: every block becomes the same one-colour block, so every slice and
    /// size stays as it was. Returns "mean r g b a -> grey v alpha a", or "!reason" when the texture isn't BC7.
    /// </summary>
    public static string NeutraliseVolume(TPF.Texture t)
    {
        var (offset, dxgi) = Layout(t.Bytes);
        if (offset < 0 || dxgi is not (DxgiBc7 or DxgiBc7Srgb) || (t.Bytes.Length - offset) % 16 != 0)
            return $"!(format {dxgi}, not BC7)";
        var (px, _, _) = WeaponTextures.Decode(t.Bytes);
        double r = 0, g = 0, b = 0, a = 0;
        foreach (var c in px) { r += c.r; g += c.g; b += c.b; a += c.a; }
        double n = Math.Max(1, px.Length);
        byte grey = (byte)Math.Clamp(Math.Round(Math.Max(r, Math.Max(g, b)) / n), 0, 255);
        byte alpha = (byte)Math.Clamp(Math.Round(a / n), 0, 255);
        Fill(t.Bytes, offset, Bc7Block(grey, grey, grey, alpha));
        return FormattableString.Invariant($"mean {r / n:0} {g / n:0} {b / n:0} {a / n:0} -> grey {grey} alpha {alpha}");
    }

    /// <summary>Makes a BC6H reflection cube one even grey of its mean brightness (every face and level).</summary>
    public static string NeutraliseCube(TPF.Texture t)
    {
        var (offset, dxgi) = Layout(t.Bytes);
        if (offset < 0 || dxgi != DxgiBc6hUf16 || (t.Bytes.Length - offset) % 16 != 0)
            return $"!(format {dxgi}, not BC6H)";
        using var ms = new MemoryStream(t.Bytes);
        var px = new BCnEncoder.Decoder.BcDecoder().DecodeHdr(BCnEncoder.Shared.ImageFiles.DdsFile.Load(ms));
        double r = 0, g = 0, b = 0;
        foreach (var c in px) { r += c.r; g += c.g; b += c.b; }
        double n = Math.Max(1, px.Length);
        float grey = (float)((0.2126 * r + 0.7152 * g + 0.0722 * b) / n);
        Fill(t.Bytes, offset, Bc6hBlock(grey));
        return FormattableString.Invariant($"mean {r / n:0.###} {g / n:0.###} {b / n:0.###} -> grey {grey:0.###}");
    }

    private static void Fill(byte[] dds, int offset, byte[] block)
    {
        for (int i = offset; i + 16 <= dds.Length; i += 16)
            block.CopyTo(dds, i);
    }

    private static void SetBits(byte[] block, ref int at, int value, int count)
    {
        for (int i = 0; i < count; i++, at++)
            if (((value >> i) & 1) != 0)
                block[at >> 3] |= (byte)(1 << (at & 7));
    }

    /// <summary>
    /// A BC7 block of one colour: mode 6 (7-bit RGBA endpoints with a shared low bit each), both endpoints the
    /// colour and every index 0. The four channels share the low bit, so alpha takes the colour's (off by one at most).
    /// </summary>
    public static byte[] Bc7Block(byte r, byte g, byte b, byte a)
    {
        int p = r & 1;
        var block = new byte[16];
        int at = 0;
        SetBits(block, ref at, 1 << 6, 7);
        foreach (var c in new[] { r, g, b, (byte)((a & ~1) | p) })
        {
            SetBits(block, ref at, c >> 1, 7);
            SetBits(block, ref at, c >> 1, 7);
        }
        SetBits(block, ref at, p, 1);
        SetBits(block, ref at, p, 1);
        return block;
    }

    /// <summary>
    /// A BC6H (unsigned) block of one grey: mode 11 (10-bit endpoints), both endpoints the same, every index 0.
    /// Unquantised 10-bit c becomes ((c &lt;&lt; 16) + 0x8000) &gt;&gt; 10, finished as that * 31 / 64 half-float bits.
    /// </summary>
    public static byte[] Bc6hBlock(float grey)
    {
        int half = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(grey, 0f, 65000f));
        int c = Math.Clamp((int)Math.Round((half - 15.5) / 31.0), 0, 1022);
        if (half == 0)
            c = 0;
        var block = new byte[16];
        int at = 0;
        SetBits(block, ref at, 0b00011, 5);
        for (int i = 0; i < 6; i++)
            SetBits(block, ref at, c, 10);
        return block;
    }

    /// <summary>The colour a one-block BC6H texture decodes to, for the self-checks.</summary>
    public static float DecodeGrey(byte[] dds)
    {
        using var ms = new MemoryStream(dds);
        var px = new BCnEncoder.Decoder.BcDecoder().DecodeHdr(BCnEncoder.Shared.ImageFiles.DdsFile.Load(ms));
        return px.Length == 0 ? -1 : px[0].g;
    }

    /// <summary>A 4x4 DDS with the DX10 header, for the self-checks.</summary>
    public static byte[] TestDds(int dxgi, byte[] block)
    {
        var dds = new byte[148 + 16];
        void I(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(at), v);
        I(0, 0x20534444); I(4, 124); I(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); I(12, 4); I(16, 4); I(20, 16); I(28, 1);
        I(76, 32); I(80, 0x4); I(84, 0x30315844); I(108, 0x1000);
        I(128, dxgi); I(132, 3); I(140, 1);
        block.CopyTo(dds, 148);
        return dds;
    }

    public static string Text(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
