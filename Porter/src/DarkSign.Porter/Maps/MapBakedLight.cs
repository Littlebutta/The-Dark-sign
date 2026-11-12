using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The light a ported DS3 map's pieces get from around them, built from DS3's own baked light (Devin, 2026-11-12: "no
/// real shadows on any of the maps", tunnels as bright as outside). Up to now every DS3 map was lit by one even grey
/// light volume (<see cref="MapEnvironment"/>, PR #150): the same sky light in Firelink's tunnels as on the cliffs.
/// <para>What the two games keep (map-diag lightvol on Devin's PC, 2026-11-12):</para>
/// <list type="bullet">
/// <item>DS3 bakes light into per-piece lightmaps (g_DOLTexture1/2) and into light probes per map (.btpb: 14k to
/// 434k probes 1.5 to 2 m apart, each a first-order spherical harmonic in RGB plus the share of sun reaching it).
/// Elden Ring still has the DS3-style lightmap materials, but none of their shaders is in any shader binder, so DS3's
/// lightmaps cannot be drawn: the probes are what is left.</item>
/// <item>Elden Ring lights map pieces from light volumes (GIIV): per environment map point, four BC7 volume textures
/// W, X, Y and Z spanning the environment box of the same number (the colosseum's 120 x 172 x 156 texels span its
/// 246 x 350 x 316 m box: 2.05, 2.035, 2.026 m a texel; Stormveil's 4500 m box takes 75 m texels). Texture rows go up
/// (Stormveil's alpha rises from 7 in row 0 to 56 at the top). W's colour never drops below 127 and its brightest
/// channel sits at 250 on average while its alpha spreads 0 to 105; Y follows W (alpha correlation 0.87 to 0.99) at
/// about 0.87 of it in open arenas, which is exactly the up-facing first-order term of light from the sky: so each
/// texture holds one spherical harmonic coefficient, its colour as 128 + 127 * c / |c| and its size in alpha
/// (W = order 0, X/Y/Z = the x, y and z terms of order 1).</item>
/// </list>
/// So this spreads DS3's probes over a light volume of the map's own, in Elden Ring's encoding, scaled so DS3's open
/// sky is as bright as the host colosseum's: a new environment point and box (number <see cref="PointNumber"/>) over
/// the area the probes cover, inside the catch-all box <see cref="MapLighting.AddAmbient"/> keeps for the far
/// scenery. Indoors and under roofs the volume is as dark as DS3 baked it; Elden Ring's sun and its shadows do the rest.
/// <para>Assumptions not yet seen in game (fallback "map-baked-light"): texture columns run along +x and slices
/// along +z (C/D mirror them); a smaller box with a higher UnkT34 wins over the catch-all box, as Stormveil's rooms
/// (UnkT34 11 to 22) sit inside its catch-all box (0); alpha is linear in brightness.</para>
/// </summary>
public static class MapBakedLight
{
    /// <summary>The environment point and box number of the baked volume (GIIV0900); no vanilla map uses 900.</summary>
    public const int PointNumber = 900;
    public static string PointName => $"Env_Point{PointNumber:D3}";
    public static string BoxName => $"Env_Box{PointNumber:D3}";
    public static string VolumeTag => $"GIIV{PointNumber:D4}";

    /// <summary>Texels in the high detail volume at most (the colosseum's holds 3.2 million).</summary>
    public const int MaxTexels = 3_300_000;
    public const float MinTexel = 2f;
    public const float Margin = 6f;

    /// <param name="NoOffset">"--no-light-offset" (fallback "map-light-place" B): the probes stay in the map's own
    /// space, as in earlier builds, instead of being moved by DS3's map offset (<see cref="ReadProbes"/>).</param>
    public sealed record Options(bool Flat, bool FlipX, bool FlipY, bool FlipZ, bool NoOffset = false)
    {
        public static Options Default { get; } = new(false, false, false, false);
    }

    /// <summary>One detail level of the baked volume: size in texels and the four textures' pixel data (BC7, slice by slice).</summary>
    public sealed record Level(int W, int H, int D, Dictionary<char, byte[]> Bc7);

    /// <summary>The baked volume of one map: its box and the three detail levels.</summary>
    public sealed class Volume
    {
        public required Vector3 Min { get; init; }
        public required Vector3 Size { get; init; }
        public required Dictionary<string, Level> Levels { get; init; }
        /// <summary>How bright the reflection cube is made, against the host's even grey (the probes' median).</summary>
        public float CubeScale { get; init; } = 1;
        public string Summary { get; init; } = "";
    }

    /// <summary>A DS3 light probe in Elden Ring's terms: position and the four coefficients W, X, Y, Z (RGB each).</summary>
    public readonly record struct Probe(Vector3 Position, float[] Sh, int SunMask);

    /// <summary>The DS3 map's probe file: the first of its .btpb files (m30 has a second, another state of the map).</summary>
    public static string? ProbePath(GameArchives ds3, MapId ds3Id)
    {
        var prefix = $"/map/{ds3Id}/";
        return ds3.Entries.Where(e => e.Path is not null && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                      && e.Path.Contains(".btpb", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path!).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>
    /// DS3's probes with their coefficients put in Elden Ring's order. DS3's file doesn't say which first-order term
    /// is which axis: the one that is largest under open sky (sun mask 768 or more) is up; of the other two the lower
    /// index is taken as x unless up came first (then it is the standard y, z, x order).
    /// <para>
    /// The probe file is in the map's own space, like the map's collisions and light file: DS3's map offset
    /// (<see cref="MapNavmesh.MapOffset"/>) moves it into the layout's (world) space, its first-order terms turned
    /// with it. Measured on DS3's files, enemies and start points with a probe within about 4 m: m40 21 of 110 as
    /// written, 91 moved; m31 0 of 181, 179 moved (m30 has no offset). Up to 1.0.422 the volumes were left in the
    /// map's own space: map 1's turned 28 degrees and 200 m off, map 3's turned 156 degrees and some 600 m off, so
    /// the shade DS3 baked for rooms and cliffs fell on open ground and the other way round.
    /// Positions are then checked against the DS3 layout: if mirroring z holds more than twice as many of the map's
    /// enemies and start points, the probes are mirrored (from 1.0.299's reading of the header box; with the offset
    /// applied no DS3 map needs it).
    /// </para>
    /// </summary>
    public static List<Probe> ReadProbes(BTPB btpb, MSB3? ds3Msb, List<string> notes, bool noOffset = false)
    {
        var raw = btpb.Groups.SelectMany(g => g.Probes).ToList();
        if (raw.Count == 0)
            return [];
        var offset = noOffset ? null : MapNavmesh.MapOffset(ds3Msb);
        var placed = raw.Select(p => offset is { } m ? Vector3.Transform(p.Position, m) : p.Position).ToList();
        if (offset is { } off)
            notes.Add(FormattableString.Invariant($"moved by DS3's map offset ({off.M41:0.#}, {off.M42:0.#}, {off.M43:0.#}, turned {MapLighting.OffsetDegrees(ds3Msb):0.#} degrees)"));
        else if (noOffset && MapNavmesh.MapOffset(ds3Msb) is not null)
            notes.Add("DS3's map offset not applied (--no-light-offset)");
        bool mirrorZ = false;
        if (ds3Msb is not null)
        {
            var places = ds3Msb.Parts.Enemies.Select(e => e.Position).Concat(ds3Msb.Parts.Players.Select(p => p.Position)).ToList();
            var (min, max) = MapLighting.Bounds(placed);
            int Inside(bool mirror) => places.Count(p =>
            {
                var q = mirror ? p with { Z = -p.Z } : p;
                return q.X >= min.X - 10 && q.X <= max.X + 10 && q.Y >= min.Y - 10 && q.Y <= max.Y + 10 && q.Z >= min.Z - 10 && q.Z <= max.Z + 10;
            });
            int straight = Inside(false), mirrored = Inside(true);
            mirrorZ = mirrored > straight * 2 && mirrored > 3;
            notes.Add($"{straight} of {places.Count} enemies and start points inside the probes' box{(mirrorZ ? $", {mirrored} with z mirrored: mirrored" : "")}");
        }

        // Which first-order term points up: the largest mean (against order 0) under open sky.
        var open = raw.Where(p => p.LightMask >= 768).ToList();
        if (open.Count < 20)
            open = raw;
        static float Lum(short[] c, int k) => 0.2126f * c[k * 3] + 0.7152f * c[k * 3 + 1] + 0.0722f * c[k * 3 + 2];
        var means = new double[4];
        foreach (var p in open)
        {
            float l0 = MathF.Max(1, Lum(p.Coefficients, 0));
            for (int k = 1; k < 4; k++)
                means[k] += Lum(p.Coefficients, k) / l0;
        }
        for (int k = 1; k < 4; k++)
            means[k] /= open.Count;
        int up = Enumerable.Range(1, 3).OrderByDescending(k => Math.Abs(means[k])).First();
        int ix = up == 1 ? 3 : 1;
        int iz = up == 1 ? 2 : up == 2 ? 3 : 2;
        float upSign = means[up] < 0 ? -1 : 1;
        // A negative up term is the Condon-Shortley sign, which also turns x around (y and x carry it, z doesn't).
        float xSign = up == 1 && upSign < 0 ? -1 : 1;
        float zSign = mirrorZ ? -1 : 1;
        notes.Add(FormattableString.Invariant($"first-order terms against order 0 under open sky: {means[1]:0.00} {means[2]:0.00} {means[3]:0.00}; up = term {up}{(upSign < 0 ? " (negative)" : "")}, x = term {ix}, z = term {iz}"));

        var probes = new List<Probe>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var p = raw[i];
            var sh = new float[12];
            var c = p.Coefficients;
            for (int ch = 0; ch < 3; ch++)
            {
                sh[ch] = c[ch];
                var v = new Vector3(xSign * c[ix * 3 + ch], upSign * c[up * 3 + ch], zSign * c[iz * 3 + ch]);
                // The first-order terms are a direction: they turn with the map (the offset's translation doesn't apply).
                if (offset is { } m)
                    v = Vector3.TransformNormal(v, m);
                sh[3 + ch] = v.X;
                sh[6 + ch] = v.Y;
                sh[9 + ch] = v.Z;
            }
            var pos = mirrorZ ? placed[i] with { Z = -placed[i].Z } : placed[i];
            probes.Add(new Probe(pos, sh, p.LightMask));
        }
        return probes;
    }

    /// <summary>The brightness (largest channel of order 0) DS3's open sky has: the 75th percentile under open sky.</summary>
    public static float OpenSky(IReadOnlyList<Probe> probes)
    {
        var open = probes.Where(p => p.SunMask >= 768).ToList();
        if (open.Count < 20)
            open = probes.ToList();
        var v = open.Select(p => MathF.Max(p.Sh[0], MathF.Max(p.Sh[1], p.Sh[2]))).Order().ToArray();
        return v.Length == 0 ? 1 : MathF.Max(1, v[(int)(v.Length * 0.75)]);
    }

    /// <summary>A texel count per axis that is a multiple of 4 (BC7 blocks) for width and height.</summary>
    private static int Round4(float v) => Math.Max(4, (int)MathF.Round(v / 4) * 4);

    /// <summary>
    /// Bakes the probes into the three detail levels of a volume over the area they cover. <paramref name="hostAlpha"/>
    /// is the host volume's open-sky alpha (its W alpha at the 95th percentile): DS3's open sky is made that bright.
    /// </summary>
    public static Volume Bake(IReadOnlyList<Probe> probes, float hostAlpha, Options options)
    {
        var (min, max) = MapLighting.Bounds(probes.Select(p => p.Position).ToList());
        min -= new Vector3(Margin);
        max += new Vector3(Margin);
        var size = max - min;
        float texel = MathF.Max(MinTexel, MathF.Cbrt(size.X * size.Y * size.Z / MaxTexels));
        // The high level's texel counts fix the box: the other levels span the same box with fewer texels.
        int hw = Round4(size.X / texel), hh = Round4(size.Y / texel), hd = Math.Max(4, (int)MathF.Round(size.Z / texel));
        size = new Vector3(hw * texel, hh * texel, hd * texel);
        var centre = (min + max) / 2;
        min = centre - size / 2;

        float sky = OpenSky(probes);
        float scale = hostAlpha / sky;
        var points = probes.Select(p => (p.Position, p.Sh)).ToList();
        var levels = new Dictionary<string, Level>();
        foreach (var (name, div) in new[] { ("high", 1), ("middle", 2), ("low", 4) })
        {
            int w = Round4(hw / (float)div), h = Round4(hh / (float)div), d = Math.Max(1, (int)MathF.Round(hd / (float)div));
            var step = new Vector3(size.X / w, size.Y / h, size.Z / d);
            float radius = MathF.Max(3 * MathF.Max(step.X, MathF.Max(step.Y, step.Z)), 6);
            var field = LightVolumeMath.Spread(points, min, step, w, h, d, 12, radius);
            levels[name] = new Level(w, h, d, Encode(field, w, h, d, scale, hostAlpha, options));
        }
        var l0 = probes.Select(p => MathF.Max(p.Sh[0], MathF.Max(p.Sh[1], p.Sh[2]))).Order().ToArray();
        float median = l0.Length == 0 ? sky : l0[l0.Length / 2];
        var high = levels["high"];
        return new Volume
        {
            Min = min,
            Size = size,
            Levels = levels,
            CubeScale = Math.Clamp(median / sky, 0.1f, 1f),
            Summary = FormattableString.Invariant(
                $"{probes.Count:N0} DS3 probes ({probes.Count(p => p.SunMask == 0):N0} without sun) baked into a {size.X:0} x {size.Y:0} x {size.Z:0} m volume at {min.X:0},{min.Y:0},{min.Z:0}, ")
                + FormattableString.Invariant($"{high.W} x {high.H} x {high.D} texels ({size.X / high.W:0.0} m); DS3 open sky {sky:0} = alpha {hostAlpha:0}, median probe at {median / sky:0.00} of it")
                + (options.FlipX || options.FlipY || options.FlipZ ? $"; mirrored along {(options.FlipX ? "x" : "")}{(options.FlipY ? "y" : "")}{(options.FlipZ ? "z" : "")}" : ""),
        };
    }

    /// <summary>
    /// The four textures of a level from the spread coefficients: per texel and coefficient, alpha = its largest
    /// channel's size times <paramref name="scale"/> and colour = 128 + 127 * c / that size. Order 0 never goes below
    /// 3% of open sky, so a room DS3 lit only with lamps isn't pitch black.
    /// </summary>
    private static Dictionary<char, byte[]> Encode(float[] field, int w, int h, int d, float scale, float hostAlpha, Options options)
    {
        var result = new Dictionary<char, byte[]>();
        float floor = MathF.Max(1, hostAlpha * 0.03f);
        for (int k = 0; k < 4; k++)
        {
            var rgba = new byte[w * h * d * 4];
            Parallel.For(0, d, z =>
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        // Mirrored levels read the field from the far side.
                        int sx = options.FlipX ? w - 1 - x : x, sy = options.FlipY ? h - 1 - y : y, sz = options.FlipZ ? d - 1 - z : z;
                        int src = ((sz * h + sy) * w + sx) * 12 + k * 3;
                        float r = field[src] * scale, g = field[src + 1] * scale, b = field[src + 2] * scale;
                        float m = MathF.Max(MathF.Abs(r), MathF.Max(MathF.Abs(g), MathF.Abs(b)));
                        int o = ((z * h + y) * w + x) * 4;
                        if (k == 0 && m < floor)
                        {
                            // Keep the colour, raise the size.
                            if (m <= 0)
                                (r, g, b, m) = (floor, floor, floor, floor);
                            else
                            {
                                float f = floor / m;
                                (r, g, b, m) = (r * f, g * f, b * f, floor);
                            }
                        }
                        if (m <= 1e-6f)
                        {
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = 128;
                            rgba[o + 3] = 0;
                            continue;
                        }
                        rgba[o] = (byte)Math.Clamp(MathF.Round(127.5f + 127.5f * r / m), 0, 255);
                        rgba[o + 1] = (byte)Math.Clamp(MathF.Round(127.5f + 127.5f * g / m), 0, 255);
                        rgba[o + 2] = (byte)Math.Clamp(MathF.Round(127.5f + 127.5f * b / m), 0, 255);
                        rgba[o + 3] = (byte)Math.Clamp(MathF.Round(m), 0, 255);
                    }
            });
            result["WXYZ"[k]] = LightVolumeMath.EncodeBc7Volume(rgba, w, h, d);
        }
        return result;
    }

    /// <summary>The host light volume's open-sky alpha: its W texture's alpha at the 95th percentile (high detail, time 00).</summary>
    public static float HostAlpha(GameArchives er, MapId host)
    {
        var path = MapEnvironment.Binders(er, host).FirstOrDefault(p => p.Contains("_envmap_00_high_", StringComparison.OrdinalIgnoreCase));
        if (path is null)
            return 56;
        var bnd = BND4.Read(er.Read(path));
        foreach (var f in bnd.Files)
        {
            var inner = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
            if (!TPF.Is(inner))
                continue;
            foreach (var t in TPF.Read(inner).Textures)
                if (t.Name.Contains("_GIIV", StringComparison.OrdinalIgnoreCase) && t.Name.EndsWith("_W", StringComparison.OrdinalIgnoreCase)
                    && MapLightDiag.DecodeVolume(t.Bytes) is { } v)
                {
                    var a = v.Px.Select(p => (int)p.a).Order().ToArray();
                    return Math.Clamp(a[(int)(a.Length * 0.95)], 20, 200);
                }
        }
        return 56;
    }

    /// <summary>
    /// port-maps step: bakes the DS3 map's probes and adds the volume's environment point and box to the layout.
    /// Returns the volume (null when there is none) and a line for the log. Runs after <see cref="MapLighting.AddAmbient"/>.
    /// </summary>
    public static (Volume? Volume, string Line) Build(GameArchives ds3, GameArchives er, MapId ds3Id, MapId hostId, MSBE msb, MSB3? ds3Msb, Options options)
    {
        if (options.Flat)
            return (null, "baked light: off (--flat-env-light); the map keeps one even light volume");
        var path = ProbePath(ds3, ds3Id);
        if (path is null)
            return (null, "baked light: the DS3 map has no light probe file; the map keeps one even light volume");
        var hostPoint = msb.Regions.EnvironmentMapPoints.FirstOrDefault();
        var hostBox = msb.Regions.EnvironmentMapEffectBoxes.FirstOrDefault();
        if (hostPoint is null || hostBox is null)
            return (null, "baked light: the layout has no environment region to copy (ambient light step failed?); none written");
        var notes = new List<string>();
        var probes = ReadProbes(BTPB.Read(ds3.Read(path)), ds3Msb, notes, options.NoOffset);
        if (probes.Count < 8)
            return (null, $"baked light: {ItemText.Leaf(path)} has {probes.Count} probes; the map keeps one even light volume");
        float hostAlpha = HostAlpha(er, hostId);
        var volume = Bake(probes, hostAlpha, options);

        var point = (MSBE.Region.EnvironmentMapPoint)hostPoint.DeepCopy();
        point.Name = PointName;
        var positions = probes.Select(p => p.Position).ToList();
        point.Position = new Vector3(Median(positions.Select(p => p.X)), Median(positions.Select(p => p.Y)), Median(positions.Select(p => p.Z)));
        var box = (MSBE.Region.EnvironmentMapEffectBox)hostBox.DeepCopy();
        box.Name = BoxName;
        box.Shape = new MSB.Shape.Box { Width = volume.Size.X, Height = volume.Size.Y, Depth = volume.Size.Z };
        // A box region stands on the centre of its bottom face.
        box.Position = new Vector3(volume.Min.X + volume.Size.X / 2, volume.Min.Y, volume.Min.Z + volume.Size.Z / 2);
        box.Rotation = Vector3.Zero;
        box.TransitionDist = 5;
        box.UnkT34 = 30;
        if (box.UnkT0A >= 0)
            box.UnkT0A = PointNumber;
        msb.Regions.EnvironmentMapPoints.RemoveAll(p => p.Name == PointName);
        msb.Regions.EnvironmentMapEffectBoxes.RemoveAll(b => b.Name == BoxName);
        msb.Regions.EnvironmentMapPoints.Add(point);
        msb.Regions.EnvironmentMapEffectBoxes.Add(box);
        return (volume, $"baked light: {ItemText.Leaf(path)}: {volume.Summary}; {string.Join("; ", notes)}");
    }

    private static float Median(IEnumerable<float> values)
    {
        var v = values.Order().ToArray();
        return v.Length == 0 ? 0 : v[v.Length / 2];
    }

    /// <summary>The level a binder path is for ("high", "middle", "low"), or null.</summary>
    public static string? LevelOf(string binderPath) =>
        new[] { "high", "middle", "low" }.FirstOrDefault(l => binderPath.Contains($"_{l}_", StringComparison.OrdinalIgnoreCase));

    /// <summary>A copy of a host light volume texture (DDS with the DX10 header) holding another volume's BC7 data.</summary>
    public static byte[] VolumeDds(byte[] hostDds, int w, int h, int d, byte[] bc7)
    {
        var (offset, _) = MapEnvironment.Layout(hostDds);
        if (offset < 0)
            throw new InvalidDataException("the host light volume is not a DDS");
        var dds = new byte[offset + bc7.Length];
        Array.Copy(hostDds, dds, offset);
        void I(int at, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(at), v);
        I(12, h);
        I(16, w);
        I(20, (w / 4) * (h / 4) * 16);
        I(24, d);
        // DDSD_DEPTH, and the volume cap.
        I(8, BitConverter.ToInt32(dds, 8) | 0x800000);
        I(112, BitConverter.ToInt32(dds, 112) | 0x200000);
        bc7.CopyTo(dds, offset);
        return dds;
    }

    /// <summary>
    /// Adds the baked volume's textures to one environment map binder (after <see cref="MapEnvironment"/> has made the
    /// host's own even): for each host light volume texture W/X/Y/Z a copy named GIIV0900 holding the baked level, and
    /// a copy of the host's reflection cube, made <see cref="Volume.CubeScale"/> as bright. Returns files added.
    /// </summary>
    public static int AddTo(BND4 bnd, Volume volume, string binderPath, MapId hostId, MapId erId)
    {
        var level = LevelOf(binderPath);
        if (level is null || !volume.Levels.TryGetValue(level, out var lv))
            return 0;
        if (bnd.Files.Any(f => f.Name.Contains(VolumeTag, StringComparison.OrdinalIgnoreCase)))
            return 0;
        var added = new List<BinderFile>();
        foreach (var file in bnd.Files)
        {
            var inner = DCX.Is(file.Bytes) ? GameArchives.SplitCompression(file.Bytes) : (file.Bytes, null!);
            if (!TPF.Is(inner.Item1))
                continue;
            var tpf = TPF.Read(inner.Item1);
            if (tpf.Textures.Count != 1)
                continue;
            var t = tpf.Textures[0];
            int at = t.Name.IndexOf("_GIIV", StringComparison.OrdinalIgnoreCase);
            int cube = t.Name.IndexOf("_GILM", StringComparison.OrdinalIgnoreCase);
            if (at < 0 && cube < 0)
                continue;
            var tag = t.Name.Substring((at >= 0 ? at : cube) + 1, 8);
            if (tag.EndsWith(PointNumber.ToString("D4"), StringComparison.Ordinal))
                continue;
            string Rename(string s) => s.Replace(tag, (at >= 0 ? "GIIV" : "GILM") + PointNumber.ToString("D4"), StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                char letter = char.ToUpperInvariant(t.Name[^1]);
                if (!lv.Bc7.TryGetValue(letter, out var data))
                    continue;
                t.Bytes = VolumeDds(t.Bytes, lv.W, lv.H, lv.D, data);
            }
            else
                ScaleCube(t, volume.CubeScale);
            t.Name = Rename(t.Name);
            var written = tpf.Write();
            added.Add(new BinderFile(file.Flags, 0, Rename(file.Name), inner.Item2 is null ? written : DCX.Compress(written, inner.Item2)));
        }
        int id = bnd.Files.Count == 0 ? 0 : bnd.Files.Max(f => f.ID) + 1;
        foreach (var f in added)
        {
            f.ID = id++;
            bnd.Files.Add(f);
        }
        return added.Count;
    }

    /// <summary>Scales an even BC6H reflection cube (as <see cref="MapEnvironment.NeutraliseCube"/> left it).</summary>
    private static void ScaleCube(TPF.Texture t, float scale)
    {
        var (offset, dxgi) = MapEnvironment.Layout(t.Bytes);
        if (offset < 0 || dxgi != MapEnvironment.DxgiBc6hUf16 || scale >= 0.999f)
            return;
        float grey = MapEnvironment.DecodeGrey(t.Bytes);
        if (grey < 0)
            return;
        var block = MapEnvironment.Bc6hBlock(grey * scale);
        for (int i = offset; i + 16 <= t.Bytes.Length; i += 16)
            block.CopyTo(t.Bytes, i);
    }

    public static string IvInfoPath(MapId map, string level) => $"/map/{map.AreaFolder}/{map}/{map}_{level}.ivinfobnd.dcx";

    /// <summary>
    /// Writes the slot's irradiance volume info binders: the host's, renamed, plus one entry per time of day for the
    /// baked point (a copy of the host point's; 28 bytes, near zero in every host). Each environment point has one per
    /// time in each level (Stormveil: 128 points, 896 entries). Without a volume, removes an earlier build's copies.
    /// </summary>
    public static string WriteIvInfo(PorterContext ctx, GameArchives er, MapId hostId, MapId erId, Volume? volume)
    {
        int written = 0, added = 0;
        foreach (var level in new[] { "high", "middle", "low" })
        {
            var target = ctx.ModPath(MapPorter.ModParts(IvInfoPath(erId, level)));
            var source = IvInfoPath(hostId, level);
            if (volume is null || !er.Contains(source))
            {
                // A new-ID slot gets the host's copy from NewIdFiles again; a colosseum slot reads the game's own.
                if (File.Exists(target))
                    File.Delete(target);
                continue;
            }
            var (bytes, compression) = er.ReadWithCompression(source);
            var bnd = BND4.Read(bytes);
            var copies = new List<BinderFile>();
            foreach (var f in bnd.Files)
            {
                f.Name = NewIdFiles.Renamed(f.Name, hostId, erId);
                int at = f.Name.IndexOf("GIIV", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || at + 8 > f.Name.Length || f.Name.Substring(at + 4, 4) == PointNumber.ToString("D4"))
                    continue;
                // Only the first point's entries are copied (one set per time of day).
                var first = bnd.Files[0].Name;
                int fa = first.IndexOf("GIIV", StringComparison.OrdinalIgnoreCase);
                if (fa < 0 || first.Substring(fa + 4, 4) != f.Name.Substring(at + 4, 4))
                    continue;
                copies.Add(new BinderFile(f.Flags, 0, f.Name[..(at + 4)] + PointNumber.ToString("D4") + f.Name[(at + 8)..], (byte[])f.Bytes.Clone()));
            }
            int id = bnd.Files.Count == 0 ? 0 : bnd.Files.Max(f => f.ID) + 1;
            foreach (var c in copies)
            {
                c.ID = id++;
                bnd.Files.Add(c);
            }
            added += copies.Count;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, DCX.Compress(bnd.Write(), compression));
            written++;
        }
        return volume is null ? "light volume info: the host's" : $"light volume info: {written} binders written with {added} entries for {VolumeTag}";
    }
}
