using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Map textures for one Elden Ring area folder. DS3 keeps a map area's textures in split binders next to the area's
/// maps (map/m40/m40_0000.tpfbhd + .tpfbdt, one .tpf.dcx per texture), and a material finds its texture by name
/// alone. Elden Ring has no such binders, but loads map/mXX/common/mXX_0000.tpfbhd when a mod adds one (JortPob's
/// approach), so each texture the ported models use is copied there from DS3, in Elden Ring's TPF header.
/// </summary>
public sealed class MapTextures
{
    private readonly MapId _er;
    private readonly Detail _detail;
    private readonly Dictionary<string, MapId> _needed = new(StringComparer.OrdinalIgnoreCase);
    // Every texture each DS3 map's models name, for the memory lines of the report.
    private readonly SortedDictionary<string, HashSet<string>> _byMap = new(StringComparer.Ordinal);

    /// <summary>
    /// How much of each DS3 texture the game is given (launcher Fallbacks "map-texture-detail").
    /// <list type="bullet">
    /// <item><see cref="Standard"/>: DS3's texture in full, and a real low detail copy at half its width and height
    /// (a quarter of the memory), which is what JortPob's low detail copies are. The game holds the low copy of
    /// every texture of the map and the full one only for map pieces near the player
    /// (<see cref="MapDrawParams.TextureDistance"/>).</item>
    /// <item><see cref="Half"/>: the full copy is half size too and the low copy a quarter size, for a PC whose
    /// graphics memory DS3's textures still don't fit in.</item>
    /// <item><see cref="FullLow"/>: the low detail copy is the full texture again, as every build up to 1.0.299
    /// wrote it: every texture of a map twice in graphics memory at full size.</item>
    /// </list>
    /// </summary>
    public enum Detail { Standard, Half, FullLow }

    /// <summary>The mip levels dropped from the full and the low detail copy.</summary>
    public static (int Full, int Low) LevelsDropped(Detail detail) => detail switch
    {
        Detail.Half => (1, 2),
        Detail.FullLow => (0, 0),
        _ => (0, 1),
    };

    public MapTextures(MapId er, Detail detail = Detail.Standard) => (_er, _detail) = (er, detail);

    public int Count => _needed.Count;

    /// <summary>Notes the textures one DS3 map's models use.</summary>
    public void Need(IEnumerable<string> textures, MapId ds3)
    {
        if (!_byMap.TryGetValue(ds3.ToString(), out var ofMap))
            _byMap[ds3.ToString()] = ofMap = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in textures)
        {
            _needed.TryAdd(t, ds3);
            ofMap.Add(t);
        }
    }

    private readonly Dictionary<string, TPF.Texture> _provided = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A texture that doesn't live in DS3's map area binders, with its data: DS3 objects carry their textures inside
    /// their own objbnd. It is written under its name like the others (see <see cref="Need"/> for who uses it).
    /// </summary>
    public void Provide(string name, TPF.Texture texture)
    {
        lock (_provided)
            _provided.TryAdd(name, texture);
    }

    /// <summary>The area binders of a DS3 map area: m40_0000 to m40_0009, whichever exist.</summary>
    public static IEnumerable<string> AreaBinders(GameArchives game, string areaFolder) =>
        Enumerable.Range(0, 10)
            .Select(n => $"/map/{areaFolder}/{areaFolder}_{n:D4}.tpfbhd")
            .Where(game.Contains);

    /// <summary>
    /// Where the ported textures go. Elden Ring ships no map area texture binders (its map textures are in the asset
    /// texture archives), but it loads map/mXX/common/mXX_0000.tpfbhd when one exists: JortPob puts its textures there.
    /// </summary>
    public static string CommonBinder(string areaFolder) => $"/map/{areaFolder}/common/{areaFolder}_0000.tpfbhd";

    public string Write(PorterContext ctx, GameArchives ds3, GameArchives er)
    {
        // Every DS3 texture the models need, by name. A texture usually sits in its own map's area; DS3 map
        // textures are named after the area they come from (m40_00_...), so another area is tried by that prefix.
        var sources = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
        var areasRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ReadArea(string area)
        {
            if (!areasRead.Add(area))
                return;
            foreach (var bhd in AreaBinders(ds3, area))
                foreach (var f in BXF4.Read(ds3.Read(bhd), ds3.Read(bhd[..^3] + "bdt")).Files)
                    sources.TryAdd(TextureNameOf(f.Name), f);
        }
        foreach (var ds3Area in _needed.Values.Select(m => m.AreaFolder).Distinct())
            ReadArea(ds3Area);
        foreach (var name in _needed.Keys.Where(n => !sources.ContainsKey(n)).ToList())
            if (name.Length > 3 && name[0] is 'm' or 'M' && char.IsDigit(name[1]) && char.IsDigit(name[2]))
                ReadArea("m" + name[1..3]);

        var area = _er.AreaFolder;
        var target = CommonBinder(area);
        var bhdPath = ctx.ModPath(MapPorter.ModParts(target));
        var bdtPath = ctx.ModPath(MapPorter.ModParts(target[..^3] + "bdt"));
        var textures = new SortedDictionary<string, TPF.Texture>(StringComparer.OrdinalIgnoreCase);
        // Full copies read back from a binder an earlier run wrote: already at that run's size, never cut again.
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Keep(byte[] bhd, byte[] bdt)
        {
            foreach (var f in BXF4.Read(bhd, bdt).Files)
            {
                var name = TextureNameOf(f.Name);
                if (name.EndsWith("_l", StringComparison.OrdinalIgnoreCase))
                    continue;
                var tex = TPF.Read(DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes).Textures.FirstOrDefault();
                if (tex is not null)
                {
                    textures[name] = tex;
                    kept.Add(name);
                }
            }
        }
        // Elden Ring's own binder when it has one (it doesn't today), then what an earlier --only run wrote.
        if (er.Contains(target))
            Keep(er.Read(target), er.Read(target[..^3] + "bdt"));
        if (File.Exists(bhdPath) && File.Exists(bdtPath))
            Keep(File.ReadAllBytes(bhdPath), File.ReadAllBytes(bdtPath));

        int added = 0;
        var missing = new List<string>();
        foreach (var name in _needed.Keys)
        {
            TPF.Texture? texture = null;
            if (_provided.TryGetValue(name, out var given))
                texture = new TPF.Texture(name, given.Format, given.Flags1, given.Bytes, TPF.TPFPlatform.PC) { Type = given.Type, Mipmaps = given.Mipmaps };
            else if (sources.TryGetValue(name, out var source))
            {
                var tpf = TPF.Read(DCX.Is(source.Bytes) ? DCX.Decompress(source.Bytes) : source.Bytes);
                texture = tpf.Textures.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                          ?? tpf.Textures.FirstOrDefault();
            }
            if (texture is null)
            {
                // Not in DS3's map binders (object and character textures some map pieces borrow, system textures).
                // A material must never name a texture that doesn't exist: nothing confirms Elden Ring gives up on
                // one instead of waiting for it while the map loads. It gets a flat mid-grey stand-in under its name
                // (the same 4x4 block serves as a flat normal map), unless an earlier run already wrote the real one.
                missing.Add(name);
                if (!textures.ContainsKey(name))
                    textures[name] = new TPF.Texture(name, 0, 0, ModelPort.FlatNormalDds(), TPF.TPFPlatform.PC);
                continue;
            }
            texture.Name = name;
            textures[name] = texture;
            kept.Remove(name);
            added++;
        }
        // The flat metallic map every ported map material points at (Elden Ring reads metallic, DS3 had none).
        textures[MapModelPort.BlackMetallic] = new TPF.Texture(MapModelPort.BlackMetallic, 103, 0, ModelPort.BlackBc4Dds(), TPF.TPFPlatform.PC);
        textures[MapModelPort.FlatNormal] = new TPF.Texture(MapModelPort.FlatNormal, 0, 0, ModelPort.FlatNormalDds(), TPF.TPFPlatform.PC);

        var (fullDrop, lowDrop) = LevelsDropped(_detail);
        var bxf = BuildBinder(textures.Values, EntryCompression, fullDrop, lowDrop, kept);
        Directory.CreateDirectory(Path.GetDirectoryName(bhdPath)!);
        bxf.Write(bhdPath, bdtPath);
        var summary = $"textures {area}: {added} DS3 textures, {textures.Count} in all, in {target.TrimStart('/')}";
        if (missing.Count > 0)
            summary += $"; {missing.Count} not found in DS3 and given a grey stand-in ({string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? ", ..." : "")})";
        // What the textures take in graphics memory, per DS3 map: the low detail copy of every texture the map's
        // pieces name is held the whole time, the full copy only near the player. A map whose two numbers together
        // pass the graphics card's memory hitches as the card swaps textures in and out.
        summary += $"\n  texture detail: {DetailText(_detail)}";
        foreach (var (map, names) in _byMap)
        {
            var (full, low, large, unread) = Memory(names.Where(textures.ContainsKey).Select(n => textures[n]), fullDrop, lowDrop);
            summary += $"\n  texture memory {map}: {names.Count} textures, {Mb(full)} MB full detail + {Mb(low)} MB low detail"
                       + $" ({large} are 4096 pixels or wider{(unread > 0 ? $"; {unread} in a format the porter can't size, kept whole in both copies" : "")})";
        }
        return summary;
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

    public static string DetailText(Detail detail) => detail switch
    {
        Detail.Half => "half size textures, quarter size low detail copies (--half-textures)",
        Detail.FullLow => "full size textures and full size low detail copies, as up to 1.0.299 (--full-low-textures)",
        _ => "full size textures, half size low detail copies",
    };

    /// <summary>
    /// The pixel bytes of a set of textures as the binder holds them: every full copy, every low detail copy, how
    /// many are 4096 pixels or wider, and how many are in a format <see cref="DdsMips"/> can't size.
    /// </summary>
    public static (long Full, long Low, int Large, int Unread) Memory(IEnumerable<TPF.Texture> textures, int fullDrop, int lowDrop)
    {
        long full = 0, low = 0;
        int large = 0, unread = 0;
        foreach (var t in textures)
        {
            if (DdsMips.Read(t.Bytes) is not { } info)
            {
                unread++;
                full += t.Bytes.Length;
                low += t.Bytes.Length;
                continue;
            }
            full += info.DataBytes(DdsMips.Droppable(info, fullDrop));
            low += info.DataBytes(DdsMips.Droppable(info, lowDrop));
            if (Math.Max(info.Width, info.Height) >= 4096)
                large++;
        }
        return (full, low, large, unread);
    }

    /// <summary>
    /// A texture binder laid out the way JortPob writes the ones Elden Ring loads (its Bind.cs BindTPF and
    /// Model/MaterialContext.cs): BXF4 version 07D7R6, and per texture a "{name}.tpf.dcx" holding one texture named
    /// "{name}" and a low detail "{name}_l.tpf.dcx" holding one named "{name}_l". Each entry is a DCX compressed
    /// TPF, as its ".dcx" name says: JortPob writes them Kraken compressed and its binder keeps them that way.
    /// (Up to 1.0.271 the port wrote bare TPFs under the ".dcx" names, with the low detail copy's inner name
    /// lacking "_l"; every map whose materials named these textures stopped loading while the game waited for them.)
    ///
    /// The low detail copy is a smaller texture, as JortPob's is (its Common/DDS.cs Scale: "exactly halfsize, since
    /// thats what we need for a low detail texture"): the same DDS without its <paramref name="lowDrop"/> largest
    /// mip levels (<see cref="DdsMips"/>). Up to 1.0.299 it was the full texture a second time, so a DS3 map sat in
    /// graphics memory twice at full size (hitching on the High Wall, the Undead Settlement close to unplayable).
    /// <paramref name="fullDrop"/> takes levels off the full copy as well (<see cref="Detail.Half"/>).
    /// </summary>
    public static BXF4 BuildBinder(IEnumerable<TPF.Texture> textures, DCX.CompressionInfo compression, int fullDrop = 0, int lowDrop = 1,
        IReadOnlySet<string>? alreadyCut = null)
    {
        var list = textures.ToList();
        var entries = new (byte[] Full, byte[] Low)[list.Count];
        Parallel.For(0, list.Count, Parallelism.Options, i =>
        {
            var t = list[i];
            byte[] Entry(string name, int drop)
            {
                var bytes = DdsMips.Drop(t.Bytes, drop);
                // The TPF's own level count follows the texture's (0 in a TPF that never set it stays 0).
                int dropped = bytes == t.Bytes || DdsMips.Read(t.Bytes) is not { } whole || DdsMips.Read(bytes) is not { } cut ? 0 : whole.Mips - cut.Mips;
                var tpf = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
                tpf.Textures.Add(new TPF.Texture(name, t.Format, t.Flags1, bytes, TPF.TPFPlatform.PC)
                    { Type = t.Type, Mipmaps = (byte)(t.Mipmaps > dropped ? t.Mipmaps - dropped : t.Mipmaps) });
                return DCX.Compress(tpf.Write(), compression);
            }
            // A full copy kept from an earlier run's binder is already cut: only the low copy's extra levels go.
            bool cut = alreadyCut?.Contains(t.Name) == true;
            entries[i] = (Entry(t.Name, cut ? 0 : fullDrop), Entry(t.Name + "_l", cut ? Math.Max(0, lowDrop - fullDrop) : lowDrop));
        });
        var bxf = new BXF4 { Version = "07D7R6" };
        var id = 0;
        for (int i = 0; i < list.Count; i++)
        {
            bxf.Files.Add(new BinderFile { ID = id++, Name = $"{list[i].Name}.tpf.dcx", Bytes = entries[i].Full });
            bxf.Files.Add(new BinderFile { ID = id++, Name = $"{list[i].Name}_l.tpf.dcx", Bytes = entries[i].Low });
        }
        return bxf;
    }

    /// <summary>Elden Ring's own compression (Kraken), which JortPob's texture entries use.</summary>
    public static readonly DCX.CompressionInfo EntryCompression = new DCX.DcxKrakCompressionInfo(DCX.KrakCompressionPreset.EldenRing);

    // "m40_00_wall_a.tpf.dcx" -> "m40_00_wall_a"
    public static string TextureNameOf(string binderName)
    {
        var leaf = ItemText.Leaf(binderName);
        foreach (var ext in new[] { ".tpf.dcx", ".tpf" })
            if (leaf.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return leaf[..^ext.Length];
        return Path.GetFileNameWithoutExtension(leaf);
    }
}
