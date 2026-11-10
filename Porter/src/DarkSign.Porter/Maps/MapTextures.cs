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
    private readonly Dictionary<string, MapId> _needed = new(StringComparer.OrdinalIgnoreCase);

    public MapTextures(MapId er) => _er = er;

    public int Count => _needed.Count;

    /// <summary>Notes the textures one DS3 map's models use.</summary>
    public void Need(IEnumerable<string> textures, MapId ds3)
    {
        foreach (var t in textures)
            _needed.TryAdd(t, ds3);
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
        void Keep(byte[] bhd, byte[] bdt)
        {
            foreach (var f in BXF4.Read(bhd, bdt).Files)
            {
                var name = TextureNameOf(f.Name);
                if (name.EndsWith("_l", StringComparison.OrdinalIgnoreCase))
                    continue;
                var tex = TPF.Read(DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes).Textures.FirstOrDefault();
                if (tex is not null)
                    textures[name] = tex;
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
            if (!sources.TryGetValue(name, out var source))
            {
                missing.Add(name);
                continue;
            }
            var tpf = TPF.Read(DCX.Is(source.Bytes) ? DCX.Decompress(source.Bytes) : source.Bytes);
            var texture = tpf.Textures.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                          ?? tpf.Textures.FirstOrDefault();
            if (texture is null)
            {
                missing.Add(name);
                continue;
            }
            texture.Name = name;
            textures[name] = texture;
            added++;
        }
        // The flat metallic map every ported map material points at (Elden Ring reads metallic, DS3 had none).
        textures[MapModelPort.BlackMetallic] = new TPF.Texture(MapModelPort.BlackMetallic, 103, 0, ModelPort.BlackBc4Dds(), TPF.TPFPlatform.PC);
        textures[MapModelPort.FlatNormal] = new TPF.Texture(MapModelPort.FlatNormal, 0, 0, ModelPort.FlatNormalDds(), TPF.TPFPlatform.PC);

        var bxf = BuildBinder(textures.Values);
        Directory.CreateDirectory(Path.GetDirectoryName(bhdPath)!);
        bxf.Write(bhdPath, bdtPath);
        var summary = $"textures {area}: {added} DS3 textures, {textures.Count} in all, in {target.TrimStart('/')}";
        if (missing.Count > 0)
            summary += $"; {missing.Count} not found in DS3 ({string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? ", ..." : "")})";
        return summary;
    }

    /// <summary>
    /// A texture binder laid out the way JortPob writes the ones Elden Ring loads: BXF4 version 07D7R6, one
    /// uncompressed TPF per texture named "{name}.tpf.dcx", and the same texture again as its low detail "{name}_l".
    /// </summary>
    public static BXF4 BuildBinder(IEnumerable<TPF.Texture> textures)
    {
        var bxf = new BXF4 { Version = "07D7R6" };
        var id = 0;
        foreach (var texture in textures)
        {
            var tpf = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
            tpf.Textures.Add(texture);
            var bytes = tpf.Write();
            bxf.Files.Add(new BinderFile { ID = id++, Name = $"{texture.Name}.tpf.dcx", Bytes = bytes });
            bxf.Files.Add(new BinderFile { ID = id++, Name = $"{texture.Name}_l.tpf.dcx", Bytes = bytes });
        }
        return bxf;
    }

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
