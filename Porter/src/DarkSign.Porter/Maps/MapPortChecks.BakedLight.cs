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
        yield return ("baked light: probes move and turn by DS3's map offset", BakedOffset);
        yield return ("baked light: environment point/box over the probes, GIIV0900 textures added to the binder", BakedRegionAndBinder);
        yield return ("baked light: --flat-env-light and --baked-light-flip are read", BakedOptions);
        yield return ("light areas: each DS3 room gets its own volume and darker reflection cube, in box order, shared points kept", LightAreas);
        yield return ("light areas: every volume's textures go into the binder under its own number", LightAreasBinder);
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

    private static void BakedOffset()
    {
        // m31's offset: turned -156 degrees and moved 600 m; one probe lit from +x in map space.
        var msb = new MSB3();
        msb.Events.MapOffsets.Add(new MSB3.Event.MapOffset { Name = "map offset", Position = new Vector3(50, -155, -580), Degree = -156 });
        var btpb = SyntheticBtpb(2, 1, 1);
        foreach (var p in btpb.Groups[0].Probes)
            for (int ch = 0; ch < 3; ch++)
                p.Coefficients[3 + ch] = 300; // term 1 = x (up is term 2)
        var raw = btpb.Groups[0].Probes[5].Position;
        var moved = MapBakedLight.ReadProbes(btpb, msb, []);
        var m = Matrix4x4.CreateRotationY(-156 * MathF.PI / 180) * Matrix4x4.CreateTranslation(50, -155, -580);
        Expect(Vector3.Distance(moved[5].Position, Vector3.Transform(raw, m)) < 0.01f, $"position moved by the offset: {moved[5].Position}");
        var dir = new Vector3(moved[5].Sh[3], moved[5].Sh[6], moved[5].Sh[9]);
        var want = Vector3.TransformNormal(new Vector3(300, 800, 0), m);
        Expect(Vector3.Distance(dir, want) < 0.5f, $"first-order terms turn with the map: {dir} (want {want})");
        Expect(moved[5].Sh[0] == 1000, "order 0 unchanged");
        var kept = MapBakedLight.ReadProbes(SyntheticBtpb(2, 1, 1), msb, [], noOffset: true);
        Expect(kept[5].Position == raw, "--no-light-offset leaves them in the map's space");
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

    /// <summary>
    /// A layout like m40's Firelink: a dark room (box 100, turned 90 degrees, 10 wide and 30 deep) and a nested
    /// alcove (101, higher order) inside the dark half of <see cref="SyntheticProbes"/>, a courtyard box (200) on the
    /// open half, a box naming 100's point (102), and a box with no probes (500).
    /// </summary>
    private static MSB3 AreaLayout()
    {
        var ds3 = new MSB3();
        void Box(string name, Vector3 pos, float yaw, float w, float h, float d, byte order, short point = -1) =>
            ds3.Regions.EnvironmentMapEffectBoxes.Add(new MSB3.Region.EnvironmentMapEffectBox
            {
                Name = name, Position = pos, Rotation = new Vector3(0, yaw, 0),
                Shape = new MSB.Shape.Box { Width = w, Height = h, Depth = d }, UnkT09 = order, UnkT0A = point,
            });
        // Turned 90 degrees: its depth (14) runs along x, from 25 to 39.
        Box("Env_Box100", new Vector3(32, 0, 5), 90, 10, 5, 14, 20);
        Box("Env_Box101", new Vector3(34, 0, 5), 0, 4, 4, 4, 30);
        Box("Env_Box102", new Vector3(30, 0, 5), 0, 4, 4, 4, 31, 100);
        Box("Env_Box200", new Vector3(8, 0, 5), 0, 14, 5, 10, 10);
        Box("Env_Box500", new Vector3(500, 0, 500), 0, 10, 10, 10, 10);
        ds3.Regions.EnvironmentMapPoints.Add(new MSB3.Region.EnvironmentMapPoint { Name = "Env_Point100", Position = new Vector3(31, 1, 5) });
        return ds3;
    }

    private static (List<MapBakedLight.Volume> Volumes, string Line, MSBE Msb) BuildAreas()
    {
        var msb = new MSBE();
        var point = new MSBE.Region.EnvironmentMapPoint { Name = "Env_Point000", UnkT00 = 4 };
        var box = new MSBE.Region.EnvironmentMapEffectBox { Name = "Env_Box000", Shape = new MSB.Shape.Box { Width = 4500, Depth = 4500, Height = 3500 }, UnkT0A = -1 };
        msb.Regions.EnvironmentMapPoints.Add(point);
        msb.Regions.EnvironmentMapEffectBoxes.Add(box);
        var probes = SyntheticProbes();
        var (volumes, line) = MapLightAreas.Build(AreaLayout(), probes, msb, point, box, 56, MapBakedLight.OpenSky(probes), MapBakedLight.Options.Default);
        return (volumes, line, msb);
    }

    private static void LightAreas()
    {
        var (volumes, line, msb) = BuildAreas();
        Expect(volumes.Select(v => v.Number).Order().SequenceEqual([100, 101, 200]), $"rooms 100, 101 and 200 baked ({string.Join(",", volumes.Select(v => v.Number))}); {line}");
        Expect(line.Contains("500"), "the box without probes is reported");
        var room = volumes.Single(v => v.Number == 100);
        var yard = volumes.Single(v => v.Number == 200);
        Expect(room.CubeScale < 0.1f && yard.CubeScale > 0.9f, $"the room's reflection is dark ({room.CubeScale:0.000}), the courtyard's as bright as open sky ({yard.CubeScale:0.000})");
        Expect(room.Size == new Vector3(10, 5, 14) && room.Min == new Vector3(-5, 0, -7), "the volume spans the box in its own axes");
        var high = room.Levels["high"];
        Expect(high.W % 4 == 0 && high.H % 4 == 0 && high.W >= 8 && high.D >= 12, $"texels follow the box's width, height and depth ({high.W} x {high.H} x {high.D})");
        var w = MapLightDiag.DecodeVolume(MapBakedLight.VolumeDds(HostVolumeDds(1, 2, 3, 4), high.W, high.H, high.D, high.Bc7['W']))!.Value;
        var yh = yard.Levels["high"];
        var wy = MapLightDiag.DecodeVolume(MapBakedLight.VolumeDds(HostVolumeDds(1, 2, 3, 4), yh.W, yh.H, yh.D, yh.Bc7['W']))!.Value;
        Expect(w.Px.Max(p => p.a) * 5 < wy.Px.Min(p => p.a), $"the room's volume is darker than the courtyard's ({w.Px.Max(p => p.a)} against {wy.Px.Min(p => p.a)})");

        var boxes = msb.Regions.EnvironmentMapEffectBoxes.ToDictionary(b => b.Name);
        Expect(boxes.ContainsKey("Env_Box102") && boxes["Env_Box102"].UnkT0A == 100, "the box naming 100's point shares it");
        Expect(!boxes.ContainsKey("Env_Box500"), "no box for the room without probes");
        Expect(boxes["Env_Box200"].UnkT34 < boxes["Env_Box100"].UnkT34 && boxes["Env_Box100"].UnkT34 < boxes["Env_Box101"].UnkT34
               && boxes["Env_Box200"].UnkT34 > MapBakedLight.MapAreaPriority && boxes["Env_Box101"].UnkT34 <= MapLightAreas.MaxPriority,
            $"DS3's order kept inside Elden Ring's range: {string.Join(", ", boxes.Values.Select(b => $"{b.Name} {b.UnkT34}"))}");
        Expect(boxes["Env_Box100"].Rotation.Y == 90 && boxes["Env_Box100"].Shape is MSB.Shape.Box { Width: 10, Depth: 14 }, "box size and turn as DS3's");
        var points = msb.Regions.EnvironmentMapPoints.ToDictionary(p => p.Name);
        Expect(points.ContainsKey("Env_Point100") && points["Env_Point100"].Position == new Vector3(31, 1, 5), "DS3's point is used where there is one");
        Expect(points.ContainsKey("Env_Point200") && points["Env_Point200"].Position.Y == 2.5f, "a room without a point gets one in the middle of its box");
        Expect(!points.ContainsKey("Env_Point102"), "a box sharing a point gets none of its own");

        // Numbers the layout already uses are left alone.
        var again = MapLightAreas.Build(AreaLayout(), SyntheticProbes(), msb, msb.Regions.EnvironmentMapPoints[0], msb.Regions.EnvironmentMapEffectBoxes[0], 56, 1000, MapBakedLight.Options.Default);
        Expect(again.Volumes.Count == 0 && again.Line.Contains("already used"), $"a second run adds nothing: {again.Line}");
        Expect(MapLightAreas.NumberOf("Env_Box104") == 104 && MapLightAreas.NumberOf("Env_Box") == -1, "numbers read from names");
        Expect(MapPorter.Options.Parse(["--no-light-areas"]).BakedLight.NoAreas && !new MapPorter.Options().BakedLight.NoAreas, "--no-light-areas is read");
    }

    private static void LightAreasBinder()
    {
        var (volumes, _, _) = BuildAreas();
        var all = new List<MapBakedLight.Volume> { MapBakedLight.Bake(SyntheticProbes(), 56, MapBakedLight.Options.Default) };
        all.AddRange(volumes);
        var bnd = new BND4();
        var host = MapId.Parse("m45_02_00_00");
        var target = MapId.Parse("m11_70_00_00");
        TPF Tpf(string name, byte[] dds) => new() { Textures = { new TPF.Texture(name, 102, 0, dds, TPF.TPFPlatform.PC) { Type = TPF.TexType.Volume } } };
        foreach (var c in "WXYZ")
            bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, bnd.Files.Count, $@"N:\GR\data\INTERROOT_win64\map\m45_02_00_00\tex\Envmap\middle\00\m45_02_00_00_GIIV0010_00_{c}.tpf",
                Tpf($"m45_02_00_00_GIIV0010_00_{c}", HostVolumeDds(250, 230, 170, 50)).Write()));
        var cube = MapEnvironment.TestDds(MapEnvironment.DxgiBc6hUf16, MapEnvironment.Bc6hBlock(0.5f));
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 4, @"N:\GR\data\INTERROOT_win64\map\m45_02_00_00\tex\Envmap\middle\00\m45_02_00_00_GILM0010_00_rem.tpf",
            new TPF { Textures = { new TPF.Texture("m45_02_00_00_GILM0010_00_rem", 115, 0, cube, TPF.TPFPlatform.PC) { Type = TPF.TexType.Cubemap } } }.Write()));
        int added = MapBakedLight.AddTo(bnd, all, "/map/m45/m45_02_00_00/m45_02_00_00_envmap_00_middle_00.tpfbnd.dcx", host, target);
        Expect(added == all.Count * 5, $"four volume textures and a cube per volume ({added} for {all.Count})");
        foreach (var v in all)
            Expect(bnd.Files.Count(f => f.Name.Contains(v.Tag)) == 4 && bnd.Files.Any(f => f.Name.Contains($"GILM{v.Number:D4}_00_rem")), $"{v.Tag}'s files");
        Expect(bnd.Files.Select(f => f.ID).Distinct().Count() == bnd.Files.Count, "binder IDs stay unique");
        var roomCube = TPF.Read(bnd.Files.Single(f => f.Name.Contains("GILM0100")).Bytes).Textures[0];
        var yardCube = TPF.Read(bnd.Files.Single(f => f.Name.Contains("GILM0200")).Bytes).Textures[0];
        float rg = MapEnvironment.DecodeGrey(roomCube.Bytes), yg = MapEnvironment.DecodeGrey(yardCube.Bytes);
        Expect(rg < yg * 0.2f, $"the room's cube is darker than the courtyard's ({rg:0.###} against {yg:0.###})");
        var room = volumes.Single(v => v.Number == 100).Levels["middle"];
        var w = MapLightDiag.DecodeVolume(TPF.Read(bnd.Files.Single(f => f.Name.EndsWith("GIIV0100_00_W.tpf")).Bytes).Textures[0].Bytes)!.Value;
        Expect(w.W == room.W && w.H == room.H && w.D == room.D, "the room's middle level in the middle binder");
        Expect(MapBakedLight.AddTo(bnd, all, "/map/m45/m45_02_00_00/m45_02_00_00_envmap_00_middle_00.tpfbnd.dcx", host, target) == 0, "adding again adds nothing");
    }
}
