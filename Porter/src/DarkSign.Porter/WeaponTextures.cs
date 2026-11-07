using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// DS3 weapons are drawn in a specular workflow: a diffuse map (_a) that is near black wherever the surface is metal,
/// and a reflectance map (_r) that carries the metal's colour. Elden Ring's weapon shader is metallic/roughness: it
/// reads base colour from _a and how metallic each pixel is from _m. Giving it DS3's _a with a flat "not metal" _m
/// draws every metal blade in DS3's near-black diffuse colour, the dark grey, untextured look. This converts each
/// _a/_r pair the standard way: metallic from how strong the reflectance is, and base colour taken from the
/// reflectance where the surface is metal (from the diffuse elsewhere).
///
/// Method from the launcher's Fallbacks window (DARKSIGN_WEAPON_PBR): unset = convert both maps (A), "metal" = make
/// _m from _r but keep DS3's _a (B), "0" = the old flat black _m (off).
/// </summary>
public static class WeaponTextures
{
    public enum Mode { Full, MetalOnly, Flat }

    public static Mode CurrentMode => Environment.GetEnvironmentVariable("DARKSIGN_WEAPON_PBR")?.Trim().ToLowerInvariant() switch
    {
        "metal" or "b" => Mode.MetalOnly,
        "0" or "off" or "flat" => Mode.Flat,
        _ => Mode.Full,
    };

    // Reflectance (linear luminance) at and below which a pixel is plain dielectric (DS3 paints non-metals ~0.04),
    // and at and above which it is fully metal.
    private const float DielectricMax = 0.06f, MetalMin = 0.25f;

    private const int DxgiBc4Unorm = 80;

    /// <summary>
    /// Adds an _m for every _r (names already renamed) and, in the full mode, rewrites the matching _a. A texture
    /// that can't be decoded falls back to the old flat black _m so the model still loads.
    /// </summary>
    public static void AddMetallicMaps(TPF tpf, Mode mode)
    {
        var byName = tpf.Textures.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var added = new List<TPF.Texture>();
        foreach (var r in tpf.Textures.Where(t => t.Name.EndsWith("_r", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var stem = r.Name[..^2];
            if (byName.ContainsKey(stem + "_m"))
                continue;
            byte[] metallic = ModelPort.BlackBc4Dds();
            if (mode != Mode.Flat)
            {
                try
                {
                    var (spec, sw, sh) = Decode(r.Bytes);
                    var metal = new float[spec.Length];
                    var mPixels = new byte[spec.Length * 4];
                    for (int i = 0; i < spec.Length; i++)
                    {
                        metal[i] = Metalness(spec[i]);
                        byte v = (byte)Math.Clamp(MathF.Round(metal[i] * 255f), 0, 255);
                        mPixels[i * 4] = v; mPixels[i * 4 + 1] = v; mPixels[i * 4 + 2] = v; mPixels[i * 4 + 3] = 255;
                    }
                    metallic = Encode(mPixels, sw, sh, CompressionFormat.Bc4, DxgiBc4Unorm);

                    if (mode == Mode.Full && byName.TryGetValue(stem + "_a", out var albedo)
                        && AlbedoFormat(albedo.Bytes) is { } fmt)
                    {
                        var (diff, aw, ah) = Decode(albedo.Bytes);
                        var px = new byte[diff.Length * 4];
                        for (int y = 0; y < ah; y++)
                            for (int x = 0; x < aw; x++)
                            {
                                int i = y * aw + x;
                                // The two maps can differ in size: sample the reflectance at the same UV.
                                int j = Math.Min(sh - 1, y * sh / ah) * sw + Math.Min(sw - 1, x * sw / aw);
                                var d = diff[i];
                                var s = spec[j];
                                float m = metal[j];
                                px[i * 4] = Mix(d.r, s.r, m);
                                px[i * 4 + 1] = Mix(d.g, s.g, m);
                                px[i * 4 + 2] = Mix(d.b, s.b, m);
                                px[i * 4 + 3] = d.a;
                            }
                        bool alpha = fmt.Format == CompressionFormat.Bc1 && diff.Any(c => c.a < 128);
                        albedo.Bytes = Encode(px, aw, ah, alpha ? CompressionFormat.Bc1WithAlpha : fmt.Format, fmt.Dxgi);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"warning: {r.Name}: kept a flat metallic map ({ex.GetType().Name}: {ex.Message})");
                    metallic = ModelPort.BlackBc4Dds();
                }
            }
            added.Add(new TPF.Texture(stem + "_m", 103, 0, metallic, TPF.TPFPlatform.PC));
        }
        tpf.Textures.AddRange(added);
    }

    /// <summary>0 (dielectric) to 1 (metal) from a DS3 reflectance texel (sRGB).</summary>
    public static float Metalness(ColorRgba32 spec)
    {
        float lum = 0.2126f * ToLinear(spec.r) + 0.7152f * ToLinear(spec.g) + 0.0722f * ToLinear(spec.b);
        float t = Math.Clamp((lum - DielectricMax) / (MetalMin - DielectricMax), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Blend two sRGB channel values in linear light.
    private static byte Mix(byte diffuse, byte specular, float m) =>
        m <= 0f ? diffuse : (byte)Math.Clamp(MathF.Round(ToSrgb(ToLinear(diffuse) * (1f - m) + ToLinear(specular) * m) * 255f), 0, 255);

    private static float ToLinear(byte c)
    {
        float v = c / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    private static float ToSrgb(float v) =>
        v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    private static (ColorRgba32[] Pixels, int Width, int Height) Decode(byte[] ddsBytes)
    {
        using var ms = new MemoryStream(ddsBytes);
        var dds = DdsFile.Load(ms);
        var pixels = new BcDecoder().Decode(dds);
        return (pixels, (int)dds.header.dwWidth, (int)dds.header.dwHeight);
    }

    /// <summary>The block format to write a rewritten albedo in, and its DXGI number, from the original.</summary>
    private static (CompressionFormat Format, int Dxgi)? AlbedoFormat(byte[] dds)
    {
        if (dds.Length < 128)
            return null;
        var fourCc = Encoding.ASCII.GetString(dds, 84, 4);
        int dxgi = fourCc == "DX10" && dds.Length >= 148 ? BitConverter.ToInt32(dds, 128) : 0;
        return (fourCc, dxgi) switch
        {
            ("DXT1", _) => (CompressionFormat.Bc1, 71),
            ("DXT5", _) => (CompressionFormat.Bc3, 77),
            ("DX10", 71 or 72) => (CompressionFormat.Bc1, dxgi),
            ("DX10", 77 or 78) => (CompressionFormat.Bc3, dxgi),
            ("DX10", 98 or 99) => (CompressionFormat.Bc7, dxgi),
            _ => null, // anything else is left as DS3 made it
        };
    }

    /// <summary>A DX10 DDS with a full mip chain, from RGBA32 pixels.</summary>
    public static byte[] Encode(byte[] rgba, int width, int height, CompressionFormat format, int dxgi)
    {
        var encoder = new BcEncoder(format);
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.Quality = format == CompressionFormat.Bc7 ? CompressionQuality.Fast : CompressionQuality.Balanced;
        int mips = encoder.CalculateNumberOfMipLevels(width, height);
        using var data = new MemoryStream();
        for (int level = 0; level < mips; level++)
            data.Write(encoder.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32, level, out _, out _));
        return Dds(width, height, mips, dxgi, data.ToArray());
    }

    private static byte[] Dds(int width, int height, int mips, int dxgi, byte[] data)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("DDS "));
        w.Write(124);                                          // header size
        w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000); // caps | height | width | pixelformat | mipcount | linearsize
        w.Write(height); w.Write(width);
        w.Write(data.Length);                                  // linear size
        w.Write(0); w.Write(mips);                             // depth, mip count
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32); w.Write(0x4);                             // pixel format: size, FOURCC
        w.Write(Encoding.ASCII.GetBytes("DX10"));
        for (int i = 0; i < 5; i++) w.Write(0);
        w.Write(0x1000 | (mips > 1 ? 0x400008 : 0));           // caps: texture (| mipmap | complex)
        for (int i = 0; i < 4; i++) w.Write(0);
        w.Write(dxgi);
        w.Write(3);                                            // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        w.Write(0); w.Write(1); w.Write(0);                    // misc, array size, misc2
        w.Write(data);
        return ms.ToArray();
    }

    /// <summary>Offline check (no game files): a bright reflectance becomes metal and lifts the albedo; a dull one doesn't.</summary>
    public static int SelfTest()
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }
        Check(Metalness(new ColorRgba32(56, 56, 56, 255)) == 0f, "DS3 dielectric reflectance (56) is not metal");
        Check(Metalness(new ColorRgba32(164, 159, 145, 255)) > 0.95f, "Lothric blade reflectance (164) is metal");

        // 8x8 albedo: left half dark diffuse over bright reflectance (metal), right half coloured diffuse over dull.
        const int n = 8;
        var a = new byte[n * n * 4];
        var r = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                bool metal = x < n / 2;
                a[i] = a[i + 1] = a[i + 2] = metal ? (byte)25 : (byte)0; if (!metal) a[i] = 180; a[i + 3] = 255;
                r[i] = r[i + 1] = r[i + 2] = metal ? (byte)190 : (byte)50; r[i + 3] = 255;
            }
        var tpf = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
        tpf.Textures.Add(new TPF.Texture("WP_A_9999_a", 0, 0, Encode(a, n, n, CompressionFormat.Bc1, 72), TPF.TPFPlatform.PC));
        tpf.Textures.Add(new TPF.Texture("WP_A_9999_r", 0, 0, Encode(r, n, n, CompressionFormat.Bc1, 72), TPF.TPFPlatform.PC));
        AddMetallicMaps(tpf, Mode.Full);
        var m = tpf.Textures.FirstOrDefault(t => t.Name == "WP_A_9999_m");
        Check(m is not null && m.Format == 103, "an _m (BC4, format 103) is added");
        if (m is not null)
        {
            var (mp, _, _) = Decode(m.Bytes);
            Check(mp[0].r > 240 && mp[n - 1].r < 15, $"metal half is metallic ({mp[0].r}), dull half isn't ({mp[n - 1].r})");
        }
        var (ap, _, _) = Decode(tpf.Textures.First(t => t.Name == "WP_A_9999_a").Bytes);
        Check(ap[0].r > 150, $"metal albedo lifted to the reflectance colour ({ap[0].r})");
        Check(ap[n - 1].r > 160 && ap[n - 1].g < 30, $"dielectric albedo kept ({ap[n - 1].r},{ap[n - 1].g})");
        var reread = TPF.Read(tpf.Write());
        Check(reread.Textures.Count == 3, "TPF round-trips with 3 textures");

        var flat = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
        flat.Textures.Add(new TPF.Texture("X_r", 0, 0, Encode(r, n, n, CompressionFormat.Bc1, 72), TPF.TPFPlatform.PC));
        AddMetallicMaps(flat, Mode.Flat);
        Check(flat.Textures.Any(t => t.Name == "X_m" && t.Bytes.SequenceEqual(ModelPort.BlackBc4Dds())), "off mode keeps the flat black _m");
        Console.WriteLine(fails == 0 ? "texture-selftest: all passed" : $"texture-selftest: {fails} failed");
        return fails == 0 ? 0 : 1;
    }
}
