using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Offline checks for the DS3 maps' baked light volume (<see cref="MapBakedLight"/>): the BC7 encoder, the probe
/// spread, DS3's axis order, the encoding of order 0 and 1 in Elden Ring's four textures, the environment region and
/// the binder entries.
/// </summary>
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> BakedLightChecks()
    {
        yield return ("baked light: BC7 volume encoding decodes back within a few steps", BakedBc7RoundTrip);
        yield return ("baked light: probes spread to near texels, the rest take the nearest", BakedSpread);
        yield return ("baked light: indoors darker than open sky, open sky as bright as the host, Y about 0.87 of W", BakedEncoding);
        yield return ("baked light: DS3's up term is found, z mirrored when the layout says so", BakedAxes);
        yield return ("baked light: environment point/box over the probes, GIIV0900 textures added to the binder", BakedRegionAndBinder);
        yield return ("baked light: --flat-env-light and --baked-light-flip are read", BakedOptions);
    }

    /// <summary>A 4 x 4 x 1 host-like light volume DDS (BC7, DX10 header, 3D).</summary>
    private static byte[] HostVolumeDds(byte r, byte g, byte b, byte a)
    {
        var dds = MapEnvironment.TestDds(MapEnvironment.DxgiBc7, MapEnvironment.Bc7Block(r, g, b, a));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(132), 4);
        return dds;
    }

    private static void BakedBc7RoundTrip()
    {
        int w = 8, h = 12, d = 5;
        var rgba = new byte[w * h * d * 4];
        for (int z = 0; z < d; z++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = ((z * h + y) * w + x) * 4;
                    // A light volume's texels change along one gradient at a time: brightness falls into a
                    // room while the colour turns.
                    int s = x * 3 + y * 2 + z * 4;
                    rgba[o] = (byte)(250 - s);
                    rgba[o + 1] = (byte)(200 + s);
                    rgba[o + 2] = (byte)(130 + s / 2);
                    rgba[o + 3] = (byte)(90 - s);
                }
        var bc7 = LightVolumeMath.EncodeBc7Volume(rgba, w, h, d);
        Expect(bc7.Length == w / 4 * (h / 4) * d * 16, "one 16-byte block per 4 x 4 texels per slice");
        var dds = MapBakedLight.VolumeDds(HostVolumeDds(1, 2, 3, 4), w, h, d, bc7);
        var vol = MapLightDiag.DecodeVolume(dds) ?? throw new Exception("not decoded");
        Expect(vol.W == w && vol.H == h && vol.D == d, $"size {vol.W} x {vol.H} x {vol.D}");
        int worst = 0;
        for (int i = 0; i < w * h * d; i++)
        {
            var p = vol.Px[i];
            worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(p.r - rgba[i * 4]), Math.Abs(p.g - rgba[i * 4 + 1])), Math.Max(Math.Abs(p.b - rgba[i * 4 + 2]), Math.Abs(p.a - rgba[i * 4 + 3]))));
        }
        Expect(worst <= 10, $"largest channel error {worst}");
    }

    private static void BakedSpread()
    {
        var points = new List<(Vector3, float[])> { (new Vector3(1, 1, 1), [10f]), (new Vector3(30, 1, 1), [50f]) };
        var field = LightVolumeMath.Spread(points, Vector3.Zero, new Vector3(2), 16, 4, 2, 1, 6);
        Expect(MathF.Abs(field[0] - 10) < 0.01f, $"texel at the first probe {field[0]}");
        Expect(MathF.Abs(field[15] - 50) < 0.01f, $"texel at the second probe {field[15]}");
        // Texel 7 (x 15 m) is 14 m from either probe: out of reach, filled from the nearest known texel.
        Expect(field[7] is 10 or 50, $"texel between them takes a neighbour's value ({field[7]})");
        Expect(field[(1 * 4 + 3) * 16 + 0] == 10, "the far corner above the first probe takes its value");
    }

    /// <summary>Probes on a grid: x below 20 open sky (order 0 1000, up 870, sun 1024), x above 20 a dark room (50, no sun).</summary>
    private static List<MapBakedLight.Probe> SyntheticProbes()
    {
        var probes = new List<MapBakedLight.Probe>();
        for (int x = 0; x <= 40; x += 2)
            for (int z = 0; z <= 10; z += 2)
                for (int y = 0; y <= 4; y += 2)
                {
                    bool open = x < 20;
                    float l0 = open ? 1000 : 50, up = open ? 870 : 10;
                    probes.Add(new MapBakedLight.Probe(new Vector3(x, y, z), [l0, l0 * 0.9f, l0 * 0.8f, 0, 0, 0, up, up, up, 0, 0, 0], open ? 1024 : 0));
                }
        return probes;
    }

    private static void BakedEncoding()
    {
        var vol = MapBakedLight.Bake(SyntheticProbes(), 56, MapBakedLight.Options.Default);
        var high = vol.Levels["high"];
        Expect(vol.Levels.Count == 3 && high.W % 4 == 0 && high.H % 4 == 0, $"three levels, high {high.W} x {high.H} x {high.D}");
        MapLightDiag.Volume Decode(char c) => MapLightDiag.DecodeVolume(MapBakedLight.VolumeDds(HostVolumeDds(1, 2, 3, 4), high.W, high.H, high.D, high.Bc7[c]))!.Value;
        var w = Decode('W');
        var y = Decode('Y');
        var step = vol.Size / new Vector3(high.W, high.H, high.D);
        (int, int, int) At(Vector3 p) => ((int)((p.X - vol.Min.X) / step.X), (int)((p.Y - vol.Min.Y) / step.Y), (int)((p.Z - vol.Min.Z) / step.Z));
        var (ox, oy, oz) = At(new Vector3(6, 2, 5));
        var (ix, iy, iz) = At(new Vector3(36, 2, 5));
        var outside = w.At(ox, oy, oz);
        var inside = w.At(ix, iy, iz);
        Expect(Math.Abs(outside.a - 56) <= 6, $"open sky W alpha {outside.a}, host 56");
        Expect(inside.a < outside.a / 5, $"the room is darker: W alpha {inside.a} against {outside.a}");
        Expect(outside.r >= 245 && outside.b < outside.r && outside.b >= 200, $"W colour 128 + 127 * c / max: {outside.r} {outside.g} {outside.b}");
        Expect(w.Px.All(p => p.r >= 120 && p.g >= 120 && p.b >= 120), "order 0 never goes below the middle grey");
        var up = y.At(ox, oy, oz);
        Expect(Math.Abs(up.a - outside.a * 0.87) <= 6, $"Y alpha {up.a} about 0.87 of W's {outside.a}");
        Expect(Decode('X').At(ox, oy, oz).a <= 2, "no sideways light in X");

        var flipped = MapBakedLight.Bake(SyntheticProbes(), 56, MapBakedLight.Options.Default with { FlipX = true }).Levels["high"];
        var wf = MapLightDiag.DecodeVolume(MapBakedLight.VolumeDds(HostVolumeDds(1, 2, 3, 4), flipped.W, flipped.H, flipped.D, flipped.Bc7['W']))!.Value;
        Expect(wf.At(high.W - 1 - ox, oy, oz).a == outside.a, "--baked-light-flip x mirrors the columns");
    }

    private static BTPB SyntheticBtpb(int upTerm, float upSign, float zSign)
    {
        var btpb = new BTPB();
        var group = new BTPB.Group();
        for (int i = 0; i < 40; i++)
        {
            var p = new BTPB.Probe { LightMask = 1024, Position = new Vector3(i, 0, zSign * (100 + i)) };
            for (int ch = 0; ch < 3; ch++)
            {
                p.Coefficients[ch] = 1000;
                p.Coefficients[upTerm * 3 + ch] = (short)(upSign * 800);
            }
            group.Probes.Add(p);
        }
        btpb.Groups.Add(group);
        return btpb;
    }

    private static void BakedAxes()
    {
        foreach (var (term, sign) in new[] { (1, 1f), (2, 1f), (1, -1f), (3, 1f) })
        {
            var notes = new List<string>();
            var probes = MapBakedLight.ReadProbes(SyntheticBtpb(term, sign, 1), null, notes);
            Expect(probes[0].Sh[6] == 800 && probes[0].Sh[3] == 0 && probes[0].Sh[9] == 0, $"term {term} sign {sign}: up read as Y ({string.Join(",", probes[0].Sh)}); {notes[0]}");
        }
        var msb = new MSB3();
        for (int i = 0; i < 6; i++)
            msb.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = $"c0000_{i:D4}", Position = new Vector3(i * 5, 0, 110 + i) });
        var mirrored = MapBakedLight.ReadProbes(SyntheticBtpb(1, 1, -1), msb, []);
        Expect(mirrored.All(p => p.Position.Z > 0), "probes mirrored onto the layout's z");
        var straight = MapBakedLight.ReadProbes(SyntheticBtpb(1, 1, 1), msb, []);
        Expect(straight.All(p => p.Position.Z > 0), "probes that already match stay as they are");
    }

    private static void BakedRegionAndBinder()
    {
        var vol = MapBakedLight.Bake(SyntheticProbes(), 56, MapBakedLight.Options.Default);
        // The binder: the host's light volume W and reflection cube under their archive names.
        var bnd = new BND4();
        var host = MapId.Parse("m45_00_00_00");
        var target = MapId.Parse("m11_70_00_00");
        TPF Tpf(string name, byte[] dds) => new() { Textures = { new TPF.Texture(name, 102, 0, dds, TPF.TPFPlatform.PC) { Type = TPF.TexType.Volume } } };
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\tex\Envmap\high\00\m45_00_00_00_GIIV0000_00_W.tpf",
            Tpf("m45_00_00_00_GIIV0000_00_W", HostVolumeDds(250, 230, 170, 50)).Write()));
        var cube = MapEnvironment.TestDds(MapEnvironment.DxgiBc6hUf16, MapEnvironment.Bc6hBlock(0.5f));
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 1, @"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\tex\Envmap\high\00\m45_00_00_00_GILM0000_00_rem.tpf",
            new TPF { Textures = { new TPF.Texture("m45_00_00_00_GILM0000_00_rem", 115, 0, cube, TPF.TPFPlatform.PC) { Type = TPF.TexType.Cubemap } } }.Write()));
        int added = MapBakedLight.AddTo(bnd, vol, "/map/m45/m45_00_00_00/m45_00_00_00_envmap_00_high_00.tpfbnd.dcx", host, target);
        Expect(added == 2 && bnd.Files.Count == 4, $"two files added ({added})");
        var w = bnd.Files.Single(f => f.Name.EndsWith("GIIV0900_00_W.tpf"));
        Expect(w.ID == 2, $"new file ID follows the host's ({w.ID})");
        var t = TPF.Read(w.Bytes).Textures[0];
        Expect(t.Name == "m45_00_00_00_GIIV0900_00_W", $"texture named {t.Name}");
        var high = vol.Levels["high"];
        var decoded = MapLightDiag.DecodeVolume(t.Bytes) ?? throw new Exception("baked texture not decoded");
        Expect(decoded.W == high.W && decoded.H == high.H && decoded.D == high.D, "the DDS header holds the baked size");
        Expect(bnd.Files.Any(f => f.Name.EndsWith("GILM0900_00_rem.tpf")), "the reflection cube is copied for the new point");
        Expect(MapBakedLight.AddTo(bnd, vol, "/map/m45/m45_00_00_00/m45_00_00_00_envmap_00_high_00.tpfbnd.dcx", host, target) == 0, "adding again adds nothing");

        // The layout regions: a box over the probes, numbered like the volume.
        var msb = new MSBE();
        msb.Regions.EnvironmentMapPoints.Add(new MSBE.Region.EnvironmentMapPoint { Name = "Env_Point000", UnkT00 = 4 });
        msb.Regions.EnvironmentMapEffectBoxes.Add(new MSBE.Region.EnvironmentMapEffectBox { Name = "Env_Box000", Shape = new MSB.Shape.Box { Width = 4500, Depth = 4500, Height = 3500 }, UnkT0A = -1 });
        var probes = SyntheticProbes();
        var box = new MSBE.Region.EnvironmentMapEffectBox { Name = MapBakedLight.BoxName, Shape = new MSB.Shape.Box { Width = vol.Size.X, Height = vol.Size.Y, Depth = vol.Size.Z } };
        box.Position = new Vector3(vol.Min.X + vol.Size.X / 2, vol.Min.Y, vol.Min.Z + vol.Size.Z / 2);
        Expect(probes.All(p => MapLighting.Holds(box, p.Position)), "the volume's box holds every probe");
        Expect(MapBakedLight.LevelOf("/map/m45/x_envmap_03_middle_00.tpfbnd.dcx") == "middle", "levels read from binder names");
    }

    private static void BakedOptions()
    {
        Expect(MapPorter.Options.Parse(["--flat-env-light"]).BakedLight.Flat && !new MapPorter.Options().BakedLight.Flat, "--flat-env-light is read");
        var o = MapPorter.Options.Parse(["--baked-light-flip", "xz"]).BakedLight;
        Expect(o.FlipX && !o.FlipY && o.FlipZ && !o.Flat, "--baked-light-flip xz is read");
    }
}
