using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Map textures for one Elden Ring area folder. Both games keep a map area's textures in split binders next to the
/// area's maps (DS3: map/m40/m40_0000.tpfbhd + .tpfbdt, one .tpf.dcx per texture), and a material finds its texture
/// by name alone. So porting a map's textures is copying each texture its models use from DS3's area binders into the
/// Elden Ring area's binder, re-wrapped in Elden Ring's TPF header and DCX.
///
/// The vanilla binder's own textures stay, so the area's other (not ported) maps still draw.
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

    /// <summary>The area binders of a map area: m40_0000 to m40_0009, whichever exist.</summary>
    public static IEnumerable<string> AreaBinders(GameArchives game, string areaFolder) =>
        Enumerable.Range(0, 10)
            .Select(n => $"/map/{areaFolder}/{areaFolder}_{n:D4}.tpfbhd")
            .Where(game.Contains);

    // Areas whose binder is used for its format when the host area has none.
    private static readonly string[] FallbackAreas = ["m45", "m11", "m10", "m60", "m12", "m13"];

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

        // The Elden Ring binder to add to, or one of another area to copy the format from.
        var area = _er.AreaFolder;
        var target = AreaBinders(er, area).FirstOrDefault();
        var templatePath = target ?? FallbackAreas.SelectMany(a => AreaBinders(er, a)).FirstOrDefault()
            ?? throw new InvalidDataException("no Elden Ring map texture binder found to use as a template");
        var bxf = BXF4.Read(er.Read(templatePath), er.Read(templatePath[..^3] + "bdt"));
        var template = bxf.Files.FirstOrDefault(f => f.Name.EndsWith(".tpf.dcx", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{templatePath} holds no .tpf.dcx to use as a template");
        var templateTpf = TPF.Read(DCX.Decompress(template.Bytes, out var compression));
        var folder = template.Name[..^ItemText.Leaf(template.Name).Length];
        if (target is null)
        {
            // A new binder for the area: keep the template's format, none of its textures.
            bxf.Files.Clear();
            folder = folder.Replace(ItemText.Leaf(templatePath.TrimEnd('/'))[..3], area, StringComparison.OrdinalIgnoreCase);
            target = $"/map/{area}/{area}_0000.tpfbhd";
        }

        var existing = bxf.Files.ToDictionary(f => TextureNameOf(f.Name), StringComparer.OrdinalIgnoreCase);
        var nextId = bxf.Files.Count == 0 ? 0 : bxf.Files.Max(f => f.ID) + 1;
        int added = 0, replaced = 0;
        var missing = new List<string>();

        void Add(string name, TPF.Texture texture)
        {
            var tpf = new TPF { Platform = templateTpf.Platform, Encoding = templateTpf.Encoding, Flag2 = templateTpf.Flag2 };
            texture.Name = name;
            tpf.Textures.Add(texture);
            var bytes = DCX.Compress(tpf.Write(), compression);
            if (existing.TryGetValue(name, out var old))
            {
                old.Bytes = bytes;
                replaced++;
                return;
            }
            var file = new BinderFile(template.Flags, nextId++, folder + name + ".tpf.dcx", bytes);
            bxf.Files.Add(file);
            existing[name] = file;
            added++;
        }

        foreach (var name in _needed.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (!sources.TryGetValue(name, out var source))
            {
                missing.Add(name);
                continue;
            }
            var tpf = TPF.Read(DCX.Decompress(source.Bytes));
            var texture = tpf.Textures.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                          ?? tpf.Textures.FirstOrDefault();
            if (texture is null)
            {
                missing.Add(name);
                continue;
            }
            Add(name, texture);
        }
        // The flat metallic map every ported map material points at (Elden Ring reads metallic, DS3 had none).
        Add(MapModelPort.BlackMetallic, new TPF.Texture(MapModelPort.BlackMetallic, 103, 0, ModelPort.BlackBc4Dds(), TPF.TPFPlatform.PC));

        var parts = MapPorter.ModParts(target);
        bxf.Write(ctx.ModPath(parts), ctx.ModPath(MapPorter.ModParts(target[..^3] + "bdt")));
        var summary = $"textures {area}: {added} added, {replaced} replaced in {ItemText.Leaf(target)}";
        if (missing.Count > 0)
            summary += $"; {missing.Count} not found in DS3 ({string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? ", ..." : "")})";
        return summary;
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
