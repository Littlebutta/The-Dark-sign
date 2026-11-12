using System.Globalization;
using System.Numerics;
using System.Text;
using BCnEncoder.Shared;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// "map-diag lightvol [ER map...]": the facts a ported map's baked lighting is built from (RULE 4), read-only, to
/// Work\reports\map-diag\lightvol.txt plus PNG slices in Work\reports\map-diag\lightvol\.
/// <list type="bullet">
/// <item>Elden Ring (the slots' hosts and Stormveil): every environment map point and box (with the box's size),
/// the irradiance volume info binders (ivinfo) byte by byte, and every light volume (GIIV) of time 00 in full: its
/// size in texels, each channel's spread over the whole volume, profiles along each axis and the box whose size
/// matches it, so where a volume sits in the world and what its channels hold can be read off.</item>
/// <item>Elden Ring's legacy lightmap materials (DS3-style g_DOLTexture samplers): which shader each names, whether
/// a shader binder holds it and whether any Stormveil piece uses one.</item>
/// <item>DS3 (each slot's map): the baked light files (.btpb light probes, .btab lightmap atlas), each probe group's
/// box and counts, the sun mask and light spread, a top-down picture of the probes, and where the per-piece
/// lightmaps (_gi_..._dol_) are kept.</item>
/// </list>
/// </summary>
public static class MapLightDiag
{
    public static int Run(PorterContext ctx, List<string> args)
    {
        var dir = ctx.WorkPath("reports", "map-diag");
        var pngDir = Path.Combine(dir, "lightvol");
        Directory.CreateDirectory(pngDir);
        var maps = args.Count > 0 ? args.Select(MapId.Parse).ToList()
            : MapSlots.All.Select(s => s.Host).Distinct().Append(MapId.Parse("m10_00_00_00")).ToList();
        var r = new StringBuilder();
        void Line(string s) => r.AppendLine(s);

        using (var er = ctx.OpenArchives(PorterGame.EldenRing))
        {
            foreach (var map in maps)
                Section(Line, $"ER {map}", () => ErMap(er, map, pngDir, Line));
            Section(Line, "ER lightmap materials", () => ErLightmapMaterials(er, Line));
        }
        using (var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3))
            foreach (var map in MapSlots.All.Select(s => s.Ds3).Distinct())
                Section(Line, $"DS3 {map}", () => Ds3Map(ds3, map, pngDir, Line));

        var path = Path.Combine(dir, "lightvol.txt");
        File.WriteAllText(path, r.ToString());
        Console.WriteLine(r.Length > 4000 ? r.ToString()[..4000] + "\n..." : r.ToString());
        Console.WriteLine($"report: {path}; pictures in {pngDir}");
        return 0;
    }

    private static void Section(Action<string> line, string title, Action body)
    {
        line($"=== {title}");
        try { body(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            line($"  failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    private static string ShapeText(MSB.Shape s) => s switch
    {
        MSB.Shape.Box b => $"box {F(b.Width)} x {F(b.Depth)} x {F(b.Height)} (w x d x h)",
        MSB.Shape.Sphere sp => $"sphere r {F(sp.Radius)}",
        MSB.Shape.Cylinder c => $"cylinder r {F(c.Radius)} h {F(c.Height)}",
        _ => s.GetType().Name,
    };

    private static string Fields(object o) => string.Join(" ", o.GetType().GetProperties()
        .Where(p => p.DeclaringType == o.GetType() && p.CanRead && p.GetIndexParameters().Length == 0)
        .Select(p =>
        {
            var v = p.GetValue(o);
            return $"{p.Name}={v switch { null => "null", float f => F(f), byte[] b => Convert.ToHexString(b), _ => Convert.ToString(v, CultureInfo.InvariantCulture) }}";
        }));

    private static void ErMap(GameArchives er, MapId map, string pngDir, Action<string> line)
    {
        var msb = MSBE.Read(er.Read(MapPorter.MsbPath(map)));
        var boxes = msb.Regions.EnvironmentMapEffectBoxes;
        foreach (var p in msb.Regions.EnvironmentMapPoints)
            line($"  point {p.Name} at {V(p.Position)} rot {V(p.Rotation)} {ShapeText(p.Shape)} {Fields(p)}");
        foreach (var b in boxes)
            line($"  box {b.Name} at {V(b.Position)} rot {V(b.Rotation)} {ShapeText(b.Shape)} {Fields(b)}");

        // Irradiance volume info: unknown layout, so bytes, floats and ints.
        foreach (var lod in new[] { "high", "middle", "low" })
        {
            var path = $"/map/{map.AreaFolder}/{map}/{map}_{lod}.ivinfobnd.dcx";
            if (!er.Contains(path))
            {
                line($"  {lod}.ivinfobnd: none");
                continue;
            }
            var bnd = BND4.Read(er.Read(path));
            line($"  {lod}.ivinfobnd: {bnd.Files.Count} files");
            foreach (var f in bnd.Files.Take(lod == "high" ? 12 : 3))
            {
                var b = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
                line($"    [{f.ID}] {f.Name} {b.Length} bytes magic {Encoding.ASCII.GetString(b, 0, Math.Min(4, b.Length)).Replace('\0', '.')}");
                line($"      hex {Convert.ToHexString(b, 0, Math.Min(b.Length, lod == "high" ? 256 : 64))}");
                if (lod != "high")
                    continue;
                int n = Math.Min(b.Length / 4, 96);
                line($"      floats {string.Join(" ", Enumerable.Range(0, n).Select(i => F(BitConverter.ToSingle(b, i * 4))))}");
                line($"      ints {string.Join(" ", Enumerable.Range(0, n).Select(i => BitConverter.ToInt32(b, i * 4)))}");
            }
        }

        // The light volumes of time 00, high detail.
        var envPath = $"/map/{map.AreaFolder}/{map}/{map}_envmap_00_high_00.tpfbnd.dcx";
        if (!er.Contains(envPath))
        {
            line("  no envmap_00_high binder");
            return;
        }
        var env = BND4.Read(er.Read(envPath));
        var volumes = new SortedDictionary<string, Dictionary<char, TPF.Texture>>(StringComparer.Ordinal);
        foreach (var f in env.Files)
        {
            var inner = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
            if (!TPF.Is(inner))
                continue;
            foreach (var t in TPF.Read(inner).Textures)
            {
                int at = t.Name.IndexOf("_GIIV", StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    continue;
                var number = t.Name.Substring(at + 5, 4);
                if (!volumes.TryGetValue(number, out var set))
                    volumes[number] = set = [];
                set[char.ToUpperInvariant(t.Name[^1])] = t;
            }
        }
        line($"  {volumes.Count} light volumes in {ItemText.Leaf(envPath)}");
        int pictures = 0;
        foreach (var (number, set) in volumes)
        {
            if (!set.TryGetValue('W', out var w))
                continue;
            var vol = DecodeVolume(w.Bytes);
            if (vol is null)
            {
                line($"  GIIV{number}: not decoded");
                continue;
            }
            var (vw, vh, vd) = (vol.Value.W, vol.Value.H, vol.Value.D);
            var match = boxes.Where(b => b.Shape is MSB.Shape.Box).Select(b => (b, s: (MSB.Shape.Box)b.Shape))
                .Select(x => (x.b, x.s, err: MathF.Abs(MathF.Log(x.s.Width / vw / (x.s.Height / vh))) + MathF.Abs(MathF.Log(x.s.Width / vw / (x.s.Depth / vd)))))
                .OrderBy(x => x.err).FirstOrDefault();
            var matchText = match.b is null ? "no box" : $"closest box {match.b.Name} texel {F(match.s.Width / vw)} {F(match.s.Height / vh)} {F(match.s.Depth / vd)} m (w,h,d)";
            line($"  GIIV{number}: {vw} x {vh} x {vd} texels; {matchText}");
            foreach (var c in "WXYZ")
            {
                if (!set.TryGetValue(c, out var t) || DecodeVolume(t.Bytes) is not { } v)
                    continue;
                line($"    {c}: {Stats(v.Px)}");
            }
            // How W relates to X, Y and Z per texel, and what its alpha does.
            var wl = vol.Value.Px.Select(p => (double)Math.Max(p.r, Math.Max(p.g, p.b))).ToArray();
            var wa = vol.Value.Px.Select(p => (double)p.a).ToArray();
            line($"    corr(W max rgb, W alpha) {Corr(wl, wa):0.00}");
            foreach (var c in "XYZ")
                if (set.TryGetValue(c, out var t) && DecodeVolume(t.Bytes) is { } v && v.Px.Length == wl.Length)
                {
                    var xl = v.Px.Select(p => (double)Math.Max(p.r, Math.Max(p.g, p.b))).ToArray();
                    var xa = v.Px.Select(p => (double)p.a).ToArray();
                    line($"    corr(W max, {c} max) {Corr(wl, xl):0.00}  corr(W alpha, {c} alpha) {Corr(wa, xa):0.00}  corr(W alpha, {c} max) {Corr(wa, xl):0.00}");
                }
            line($"    W max-rgb profile along x: {Profile(vol.Value, 0, false)}");
            line($"    W max-rgb profile along y (texture rows): {Profile(vol.Value, 1, false)}");
            line($"    W max-rgb profile along z (slices): {Profile(vol.Value, 2, false)}");
            line($"    W alpha profile along x: {Profile(vol.Value, 0, true)}");
            line($"    W alpha profile along y: {Profile(vol.Value, 1, true)}");
            line($"    W alpha profile along z: {Profile(vol.Value, 2, true)}");
            if (pictures++ < 4)
                foreach (var c in "WZ")
                    if (set.TryGetValue(c, out var t) && DecodeVolume(t.Bytes) is { } v)
                        Pictures(v, Path.Combine(pngDir, $"{map}_GIIV{number}_{c}"));
        }
    }

    public readonly record struct Volume(int W, int H, int D, ColorRgba32[] Px)
    {
        public ColorRgba32 At(int x, int y, int z) => Px[(z * H + y) * W + x];
    }

    /// <summary>Decodes a BC7 volume texture slice by slice (BCnEncoder's DDS loader reads the first slice only).</summary>
    public static Volume? DecodeVolume(byte[] dds)
    {
        var (offset, dxgi) = MapEnvironment.Layout(dds);
        if (offset < 0 || dxgi is not (MapEnvironment.DxgiBc7 or MapEnvironment.DxgiBc7Srgb))
            return null;
        int h = BitConverter.ToInt32(dds, 12), w = BitConverter.ToInt32(dds, 16);
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        int slice = bw * bh * 16;
        int d = (dds.Length - offset) / slice;
        if (d <= 0)
            return null;
        var decoder = new BCnEncoder.Decoder.BcDecoder();
        var px = new ColorRgba32[w * h * d];
        for (int z = 0; z < d; z++)
        {
            var s = decoder.DecodeRaw(dds.AsSpan(offset + z * slice, slice).ToArray(), w, h, BCnEncoder.Shared.CompressionFormat.Bc7);
            Array.Copy(s, 0, px, z * w * h, w * h);
        }
        return new Volume(w, h, d, px);
    }

    private static string Stats(ColorRgba32[] px)
    {
        string Ch(Func<ColorRgba32, int> pick)
        {
            var v = px.Select(pick).Order().ToArray();
            int P(double q) => v[(int)Math.Clamp(q * (v.Length - 1), 0, v.Length - 1)];
            return $"{v.Average():0} [{P(0)} {P(0.05)} {P(0.5)} {P(0.95)} {P(1)}]";
        }
        return $"r {Ch(p => p.r)} g {Ch(p => p.g)} b {Ch(p => p.b)} a {Ch(p => p.a)} (mean [min p5 median p95 max])";
    }

    private static double Corr(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sab += (a[i] - ma) * (b[i] - mb);
            saa += (a[i] - ma) * (a[i] - ma);
            sbb += (b[i] - mb) * (b[i] - mb);
        }
        return saa <= 0 || sbb <= 0 ? 0 : sab / Math.Sqrt(saa * sbb);
    }

    /// <summary>Mean along one axis (0 x, 1 y, 2 z) over the other two, at up to 24 evenly spaced positions.</summary>
    private static string Profile(Volume v, int axis, bool alpha)
    {
        int n = axis switch { 0 => v.W, 1 => v.H, _ => v.D };
        var sums = new double[n];
        var counts = new int[n];
        for (int z = 0; z < v.D; z++)
            for (int y = 0; y < v.H; y++)
                for (int x = 0; x < v.W; x++)
                {
                    var p = v.At(x, y, z);
                    int i = axis switch { 0 => x, 1 => y, _ => z };
                    sums[i] += alpha ? p.a : Math.Max(p.r, Math.Max(p.g, p.b));
                    counts[i]++;
                }
        int steps = Math.Min(24, n);
        return string.Join(" ", Enumerable.Range(0, steps).Select(k => (int)Math.Round((double)k * (n - 1) / Math.Max(1, steps - 1)))
            .Select(i => (sums[i] / Math.Max(1, counts[i])).ToString("0", CultureInfo.InvariantCulture)));
    }

    /// <summary>Three middle slices (rgb and alpha side by side): front (x by y at middle z), top (x by z at middle y), side (z by y at middle x).</summary>
    private static void Pictures(Volume v, string stem)
    {
        void Save(string name, int pw, int ph, Func<int, int, ColorRgba32> at)
        {
            int scale = Math.Max(1, 256 / Math.Max(pw, ph));
            int iw = pw * scale * 2 + 4, ih = ph * scale;
            var lines = new byte[(iw * 3 + 1) * ih];
            for (int y = 0; y < ih; y++)
            {
                lines[y * (iw * 3 + 1)] = 0;
                for (int x = 0; x < iw; x++)
                {
                    int o = y * (iw * 3 + 1) + 1 + x * 3;
                    int half = pw * scale;
                    if (x >= half && x < half + 4)
                        continue;
                    bool alphaSide = x >= half + 4;
                    int sx = (alphaSide ? x - half - 4 : x) / scale, sy = y / scale;
                    var c = at(sx, sy);
                    if (alphaSide)
                        lines[o] = lines[o + 1] = lines[o + 2] = c.a;
                    else
                    {
                        lines[o] = c.r;
                        lines[o + 1] = c.g;
                        lines[o + 2] = c.b;
                    }
                }
            }
            File.WriteAllBytes($"{stem}_{name}.png", Png(lines, iw, ih));
        }
        Save("front", v.W, v.H, (x, y) => v.At(x, y, v.D / 2));
        Save("top", v.W, v.D, (x, z) => v.At(x, v.H / 2, z));
        Save("side", v.D, v.H, (z, y) => v.At(v.W / 2, y, z));
    }

    /// <summary>An 8-bit RGB PNG from scanlines that each start with a 0 filter byte.</summary>
    public static byte[] Png(byte[] scanlines, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var head = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
            ms.Write(head);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(body);
            uint crc = 0xFFFFFFFF;
            foreach (byte b in body)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, ~crc);
            ms.Write(head);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var deflate = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true))
                deflate.Write(scanlines);
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", []);
        return ms.ToArray();
    }

    /// <summary>Elden Ring's matbins with a DS3-style lightmap sampler, their shaders, and whether anything ships or uses them.</summary>
    private static void ErLightmapMaterials(GameArchives er, Action<string> line)
    {
        var matbins = MapPorter.AllMatbins(er);
        var withDol = new List<(string Name, string Shader, string Samplers)>();
        foreach (var (name, file) in matbins)
        {
            MATBIN m;
            try { m = MATBIN.Read(file.Bytes); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (m.Samplers.Any(s => s.Type.Contains("DOL", StringComparison.OrdinalIgnoreCase) || s.Type.Contains("Lightmap", StringComparison.OrdinalIgnoreCase)))
                withDol.Add((name, ItemText.Leaf(m.ShaderPath ?? ""), string.Join(",", m.Samplers.Select(s => s.Type))));
        }
        line($"  {withDol.Count} of {matbins.Count} matbins have a lightmap sampler");
        foreach (var x in withDol.Take(40))
            line($"    {x.Name}: shader {x.Shader}; samplers {x.Samplers}");

        // Shader binders: entry names (shaders are named after the matbin's ShaderPath stem).
        var shaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shaderBinders = er.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/shader/", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).Order().ToList();
        line($"  {shaderBinders.Count} files under /shader/: {string.Join(", ", shaderBinders.Take(30).Select(ItemText.Leaf))}");
        foreach (var path in shaderBinders.Where(p => p.EndsWith(".shaderbnd.dcx", StringComparison.OrdinalIgnoreCase)))
            try
            {
                var bytes = er.Read(path);
                IEnumerable<string> names = BND4.Is(bytes) ? BND4.Read(bytes).Files.Select(f => f.Name) : [];
                foreach (var n in names)
                    shaderNames.Add(Path.GetFileNameWithoutExtension(ItemText.Leaf(n)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                line($"    {ItemText.Leaf(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        line($"  {shaderNames.Count} shader entries; ones with 'lightmap' or '_l' in the name: {string.Join(", ", shaderNames.Where(n => n.Contains("lightmap", StringComparison.OrdinalIgnoreCase) || n.Contains("_L_", StringComparison.OrdinalIgnoreCase) || n.EndsWith("_L", StringComparison.OrdinalIgnoreCase)).Take(40))}");
        foreach (var shader in withDol.Select(x => Path.GetFileNameWithoutExtension(x.Shader)).Distinct(StringComparer.OrdinalIgnoreCase))
            line($"    shader {shader}: {(shaderNames.Contains(shader) ? "in a shader binder" : "NOT in any shader binder")}");

        // Does any vanilla Stormveil piece use them?
        var dolNames = withDol.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var m10 = MapId.Parse("m10_00_00_00");
        var msb = MSBE.Read(er.Read(MapPorter.MsbPath(m10)));
        int pieces = 0, using_ = 0;
        var examples = new List<string>();
        foreach (var path in msb.Models.MapPieces.Select(p => MapPorter.ErMapBndPath(m10, MapPorter.ModelNumber(p.Name))).Where(er.Contains).Distinct())
        {
            pieces++;
            foreach (var flver in BND4.Read(er.Read(path)).Files.Where(f => FLVER2.Is(f.Bytes)).Select(f => FLVER2.Read(f.Bytes)))
                foreach (var mat in flver.Materials)
                    if (dolNames.Contains(ModelPort.MatbinName(mat.MTD)) || mat.Textures.Any(t => t.Path.Contains("_dol", StringComparison.OrdinalIgnoreCase)))
                    {
                        using_++;
                        if (examples.Count < 8)
                            examples.Add($"{ItemText.Leaf(path)} {mat.Name} {ItemText.Leaf(mat.MTD)} [{string.Join(",", mat.Textures.Select(t => t.ParamName + "=" + ItemText.Leaf(t.Path)))}]");
                    }
        }
        line($"  Stormveil: {using_} materials in {pieces} piece binders use a lightmap matbin or _dol texture");
        foreach (var e in examples)
            line($"    {e}");
        var dolTex = er.Entries.Where(e => e.Path is not null && (e.Path.Contains("_dol", StringComparison.OrdinalIgnoreCase) || e.Path.Contains("_gi_", StringComparison.OrdinalIgnoreCase))).Select(e => e.Path!).Take(10).ToList();
        line($"  ER archive paths with _dol / _gi_: {(dolTex.Count == 0 ? "none" : string.Join(", ", dolTex))}");
    }

    private static void Ds3Map(GameArchives ds3, MapId map, string pngDir, Action<string> line)
    {
        var id = map.ToString();
        var files = ds3.Entries.Where(e => e.Path is not null && e.Path.Contains(id, StringComparison.OrdinalIgnoreCase)
                && !e.Path.EndsWith(".mapbnd.dcx", StringComparison.OrdinalIgnoreCase) && !e.Path.EndsWith(".hkxbhd", StringComparison.OrdinalIgnoreCase)
                && !e.Path.EndsWith(".hkxbdt", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path!).Order().ToList();
        line($"  {files.Count} named files under the map besides piece binders: {string.Join(", ", files.Take(60).Select(ItemText.Leaf))}");
        foreach (var path in files.Where(p => p.Contains(".btpb", StringComparison.OrdinalIgnoreCase)))
            try { Btpb(BTPB.Read(ds3.Read(path)), ItemText.Leaf(path), Path.Combine(pngDir, $"{id}_{ItemText.Leaf(path).Split('.')[0]}"), line); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { line($"  {ItemText.Leaf(path)}: {ex.GetType().Name}: {ex.Message}"); }
        foreach (var path in files.Where(p => p.Contains(".btab", StringComparison.OrdinalIgnoreCase)))
            try
            {
                var btab = BTAB.Read(ds3.Read(path));
                line($"  {ItemText.Leaf(path)}: {btab.Entries.Count} entries, {btab.Entries.Select(e => e.AtlasID).Distinct().Count()} atlases");
                foreach (var e in btab.Entries.Take(6))
                    line($"    {e.PartName} / {e.MaterialName}: atlas {e.AtlasID} offset {F(e.UVOffset.X)},{F(e.UVOffset.Y)} scale {F(e.UVScale.X)},{F(e.UVScale.Y)}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { line($"  {ItemText.Leaf(path)}: {ex.GetType().Name}: {ex.Message}"); }

        // Where the per-piece lightmaps live: archive paths, else inside the map's texture binders.
        var gi = ds3.Entries.Where(e => e.Path is not null && e.Path.Contains(id, StringComparison.OrdinalIgnoreCase)
            && (e.Path.Contains("_gi", StringComparison.OrdinalIgnoreCase) || e.Path.Contains("dol", StringComparison.OrdinalIgnoreCase) || e.Path.Contains("light", StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.Path!).Take(20).ToList();
        line($"  paths with _gi / dol / light: {(gi.Count == 0 ? "none" : string.Join(", ", gi))}");
        var bhd = files.Where(p => p.EndsWith(".tpfbhd", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var path in bhd)
            try
            {
                var bdtPath = path[..^".tpfbhd".Length] + ".tpfbdt";
                if (!ds3.Contains(bdtPath))
                    continue;
                var bxf = BXF4.Read(ds3.Read(path), ds3.Read(bdtPath));
                var dol = bxf.Files.Where(f => f.Name.Contains("_dol", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("_gi_", StringComparison.OrdinalIgnoreCase)).ToList();
                line($"  {ItemText.Leaf(path)}: {bxf.Files.Count} texture files, {dol.Count} lightmaps{(dol.Count > 0 ? $" e.g. {string.Join(", ", dol.Take(3).Select(f => ItemText.Leaf(f.Name)))}" : "")}");
                foreach (var f in dol.Take(2))
                {
                    var b = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
                    if (!TPF.Is(b))
                        continue;
                    foreach (var t in TPF.Read(b).Textures.Take(2))
                    {
                        var (_, dxgi) = MapEnvironment.Layout(t.Bytes);
                        line($"    {t.Name}: format {t.Format} dxgi {dxgi} {BitConverter.ToInt32(t.Bytes, 16)}x{BitConverter.ToInt32(t.Bytes, 12)} {t.Bytes.Length} bytes");
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { line($"  {ItemText.Leaf(path)}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Btpb(BTPB btpb, string name, string stem, Action<string> line)
    {
        var probes = btpb.Groups.SelectMany(g => g.Probes).ToList();
        line($"  {name}: {btpb.Version}, box {V(btpb.Unk1C)} .. {V(btpb.Unk28)}, {btpb.Groups.Count} groups, {probes.Count} probes");
        foreach (var g in btpb.Groups.Take(40))
        {
            var lm = g.Probes.Select(p => (int)p.LightMask).ToList();
            var l0 = g.Probes.Select(p => (p.Coefficients[0] + p.Coefficients[1] + p.Coefficients[2]) / 3.0).ToList();
            line($"    group '{g.Name}' flags {g.Flags08} probes {g.Probes.Count} box {V(g.Unk28)} .. {V(g.Unk34)} unk {g.Unk10} {g.Unk14} {g.Unk18} {F(g.Unk1C)} {F(g.Unk20)} {F(g.Unk24)} {F(g.Unk48)} {F(g.Unk4C)} {F(g.Unk50)} {g.Unk94} {g.Unk95} {g.Unk96}"
                 + (g.Probes.Count > 0 ? $"; sun mask mean {lm.Average():0} min {lm.Min()} max {lm.Max()}; L0 mean {l0.Average():0} min {l0.Min():0} max {l0.Max():0}" : ""));
        }
        if (probes.Count == 0)
            return;
        for (int i = 0; i < 12; i++)
        {
            var c = probes.Select(p => (int)p.Coefficients[i]).Order().ToArray();
            line($"    coefficient {i}: mean {c.Average():0} [min {c[0]} p5 {c[c.Length / 20]} median {c[c.Length / 2]} p95 {c[c.Length * 19 / 20]} max {c[^1]}]");
        }
        var masks = probes.GroupBy(p => p.LightMask switch { 0 => "0", < 256 => "1-255", < 768 => "256-767", < 1024 => "768-1023", 1024 => "1024", _ => ">1024" })
            .Select(g => $"{g.Key}: {g.Count()}");
        line($"    sun mask spread: {string.Join(", ", masks)}; Unk1A non-zero {probes.Count(p => p.Unk1A != 0)}");
        // Nearest-neighbour spacing, to know how dense the probes are.
        var sample = probes.Where((_, i) => i % Math.Max(1, probes.Count / 400) == 0).Take(400).ToList();
        var spacing = sample.Select(p => probes.Where(q => q != p).Min(q => Vector3.Distance(q.Position, p.Position))).Order().ToArray();
        line($"    probe spacing (nearest neighbour, m): p5 {F(spacing[spacing.Length / 20])} median {F(spacing[spacing.Length / 2])} p95 {F(spacing[spacing.Length * 19 / 20])}");
        line($"    first probes: {string.Join("; ", probes.Take(4).Select(p => $"{V(p.Position)} mask {p.LightMask} sh {string.Join(",", p.Coefficients)}"))}");

        // Top-down picture: left the L0 colour (scaled to the 99th percentile), right the sun mask.
        var min = new Vector3(probes.Min(p => p.Position.X), 0, probes.Min(p => p.Position.Z));
        var max = new Vector3(probes.Max(p => p.Position.X), 0, probes.Max(p => p.Position.Z));
        float span = MathF.Max(1, MathF.Max(max.X - min.X, max.Z - min.Z));
        int size = 512;
        var l0s = probes.Select(p => (double)Math.Max(p.Coefficients[0], Math.Max(p.Coefficients[1], p.Coefficients[2]))).Order().ToArray();
        double top = Math.Max(1, l0s[(int)(l0s.Length * 0.99)]);
        int iw = size * 2 + 4;
        var lines = new byte[(iw * 3 + 1) * size];
        foreach (var p in probes.OrderBy(p => p.Position.Y))
        {
            int x = (int)((p.Position.X - min.X) / span * (size - 1));
            int y = size - 1 - (int)((p.Position.Z - min.Z) / span * (size - 1));
            void Put(int px, byte r, byte g, byte b)
            {
                int o = y * (iw * 3 + 1) + 1 + px * 3;
                lines[o] = r; lines[o + 1] = g; lines[o + 2] = b;
            }
            byte S(short c) => (byte)Math.Clamp(c / top * 255, 0, 255);
            Put(x, S(p.Coefficients[0]), S(p.Coefficients[1]), S(p.Coefficients[2]));
            byte m = (byte)Math.Clamp(p.LightMask / 4, 0, 255);
            Put(x + size + 4, m, m, m);
        }
        File.WriteAllBytes($"{stem}_topdown.png", Png(lines, iw, size));
        line($"    picture {Path.GetFileName(stem)}_topdown.png: x {F(min.X)}..{F(min.X + span)}, z {F(min.Z)}..{F(min.Z + span)} (north up = +z), left L0 colour, right sun mask");
    }
}
