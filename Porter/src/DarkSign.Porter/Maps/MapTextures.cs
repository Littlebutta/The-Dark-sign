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
    // Every texture each DS3 map's models name, for the memory lines of the report.
    private readonly SortedDictionary<string, HashSet<string>> _byMap = new(StringComparer.Ordinal);

    /// <summary>
    /// DS3's texture goes in full, and a real low detail copy at half its width and height (a quarter of the memory),
    /// which is what JortPob's low detail copies are. The game holds the low copy of every texture of the map and the
    /// full one only for map pieces near the player (<see cref="MapDrawParams.TextureDistance"/>).
    /// </summary>
    public MapTextures(MapId er) => _er = er;

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
        // Textures a map piece or object borrows from a character or an object (Father Ariandel's bowl o459050 has no
        // textures of its own in its objbnd; the Painted World's corpses wear c1399's): looked up in that binder.
        var borrowed = Borrowed(ds3, _needed.Keys.Where(n => !sources.ContainsKey(n) && !_provided.ContainsKey(n)).ToList());

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

        // A DS3 texture by name, for the sky layers' baked textures (MapSkyFades), which are made from one or three.
        TPF.Texture? FindSource(string n)
        {
            if (_provided.TryGetValue(n, out var g) || borrowed.TryGetValue(n, out g))
                return g;
            if (!sources.ContainsKey(n) && n.Length > 3 && (n[0] is 'm' or 'M') && char.IsDigit(n[1]) && char.IsDigit(n[2]))
                ReadArea("m" + n[1..3]);
            if (!sources.TryGetValue(n, out var s))
                return null;
            var tpf = TPF.Read(DCX.Is(s.Bytes) ? DCX.Decompress(s.Bytes) : s.Bytes);
            return tpf.Textures.FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ?? tpf.Textures.FirstOrDefault();
        }

        int added = 0, baked = 0;
        var missing = new List<string>();
        var bakeFailed = new List<string>();
        foreach (var name in _needed.Keys)
        {
            TPF.Texture? texture = null;
            if (MapSkyFades.Recipes.TryGetValue(name, out var recipe))
            {
                texture = MapSkyFades.Build(name, recipe, FindSource);
                if (texture is null)
                {
                    // Its DS3 albedo wasn't found: a fully see-through stand-in, never a solid grey sheet in the sky.
                    bakeFailed.Add(name);
                    textures[name] = new TPF.Texture(name, MapSkyFades.TpfAlphaFormat, 0, WeaponTextures.Encode(new byte[4 * 4 * 4], 4, 4,
                        BCnEncoder.Shared.CompressionFormat.Bc7, 98), TPF.TPFPlatform.PC);
                    continue;
                }
                baked++;
            }
            else if (_provided.TryGetValue(name, out var given) || borrowed.TryGetValue(name, out given))
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

        var bxf = BuildBinder(textures.Values, EntryCompression, 0, 1, kept);
        Directory.CreateDirectory(Path.GetDirectoryName(bhdPath)!);
        bxf.Write(bhdPath, bdtPath);
        var summary = $"textures {area}: {added} DS3 textures, {textures.Count} in all, in {target.TrimStart('/')}";
        if (borrowed.Count > 0)
            summary += $"; {borrowed.Count} borrowed from DS3 character or object binders";
        if (baked > 0 || bakeFailed.Count > 0)
            summary += $"; {baked} sky layer textures with DS3's fade baked in"
                       + (bakeFailed.Count > 0 ? $" ({bakeFailed.Count} whose DS3 albedo wasn't found, written see-through: {string.Join(", ", bakeFailed.Take(20))})" : "");
        if (missing.Count > 0)
            summary += $"; {missing.Count} not found in DS3 and given a grey stand-in ({string.Join(", ", missing.Take(60))}{(missing.Count > 60 ? ", ..." : "")})";
        // What the textures take in graphics memory, per DS3 map: the low detail copy of every texture the map's
        // pieces name is held the whole time, the full copy only near the player. A map whose two numbers together
        // pass the graphics card's memory hitches as the card swaps textures in and out.
        summary += "\n  texture detail: full size textures, half size low detail copies";
        foreach (var (map, names) in _byMap)
        {
            var (full, low, large, unread) = Memory(names.Where(textures.ContainsKey).Select(n => textures[n]), 0, 1);
            summary += $"\n  texture memory {map}: {names.Count} textures, {Mb(full)} MB full detail + {Mb(low)} MB low detail"
                       + $" ({large} are 4096 pixels or wider{(unread > 0 ? $"; {unread} in a format the porter can't size, kept whole in both copies" : "")})";
        }
        return summary;
    }

    /// <summary>
    /// The DS3 binders a texture name points at outside the map area binders: a character's (c1399_a: chr/c1399's
    /// texture and character binders) and an object's (o459050_..., m30_00_o309100_...: obj/o459050.objbnd).
    /// </summary>
    public static IEnumerable<string> BorrowSources(string name, Func<string, bool> exists, IEnumerable<string> paths)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(?:m\d\d_\d\d_)?([co])(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            yield break;
        var digits = m.Groups[2].Value;
        if (char.ToLowerInvariant(m.Groups[1].Value[0]) == 'c' && digits.Length >= 4)
        {
            var chr = $"/chr/c{digits[..4]}";
            foreach (var p in paths.Where(p => p.StartsWith(chr, StringComparison.OrdinalIgnoreCase)
                                               && p.EndsWith(".texbnd.dcx", StringComparison.OrdinalIgnoreCase)
                                               && !p.EndsWith("_l.texbnd.dcx", StringComparison.OrdinalIgnoreCase)).Order())
                yield return p;
            if (exists(chr + ".chrbnd.dcx"))
                yield return chr + ".chrbnd.dcx";
        }
        else if (digits.Length is >= 4 and <= 6)
        {
            var obj = ObjectPort.ObjBndPath("o" + digits.PadLeft(6, '0'));
            if (exists(obj))
                yield return obj;
        }
    }

    // Reads each borrowed-from binder once and keeps only the textures asked for.
    private static Dictionary<string, TPF.Texture> Borrowed(GameArchives ds3, IReadOnlyCollection<string> names)
    {
        var found = new Dictionary<string, TPF.Texture>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
            return found;
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var paths = ds3.Entries.Select(e => e.Path).OfType<string>().ToList();
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            foreach (var path in BorrowSources(name, ds3.Contains, paths))
            {
                if (found.ContainsKey(name))
                    break;
                if (!read.Add(path))
                    continue;
                try
                {
                    foreach (var f in BND4.Read(ds3.Read(path)).Files)
                    {
                        var bytes = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
                        if (!TPF.Is(bytes))
                            continue;
                        foreach (var t in TPF.Read(bytes).Textures)
                            if (wanted.Contains(t.Name))
                                found.TryAdd(t.Name, t);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // An unreadable binder leaves its textures to the grey stand-in.
                }
            }
        return found;
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

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
    /// <paramref name="fullDrop"/> takes levels off the full copy as well.
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
