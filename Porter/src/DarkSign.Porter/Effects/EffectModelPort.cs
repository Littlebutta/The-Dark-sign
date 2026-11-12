using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Converts the models DS3 effects draw (sfx\model\sNNNNN.flver: the Moonlight Greatsword's wave, the ice Friede's
/// scythes raise, the Frozen blade around a weapon) into Elden Ring effect models. Until now an effect with a model
/// was left out whole ("uses models"), which took the best known weapon effects with it.
/// <para>The geometry is DS3's. What Elden Ring's renderer needs comes from one of Elden Ring's own effect models
/// that uses the equivalent material: the FLVER header (0x2001A), the material's MATBIN path, its texture slots and
/// its GX list. Elden Ring kept DS3's effect materials under the same names (S[A], S[A]_al, S[A]_add_Soft, S[ARSN],
/// S[ARSN]_em ...; effect-models survey, 2026-11-09: 288 Elden Ring effect models), and its effect models name their
/// own textures in the FLVER as DS3's do, so a DS3 material maps to the Elden Ring material of the same name, or to
/// the nearest one Elden Ring has (see <see cref="Candidates"/>), and its textures go into the slots of the same
/// kind. A model whose material has no Elden Ring equivalent is not converted and its effect stays left out.</para>
/// Method (RULE 3): the FLVER header / skeleton / node flag changes are the weapon model port's (ModelPort); the
/// material mapping is this project's, from the survey of both games' effect models.
/// Fallback "weapon-visuals": DARKSIGN_EFFECT_MODELS=0 leaves effects with models out, as before.
/// </summary>
public sealed partial class EffectModelPort
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_EFFECT_MODELS") != "0";

    /// <summary>An Elden Ring effect model that uses a material, kept as bytes so every use starts from a fresh copy.</summary>
    private sealed record Template(string Model, byte[] Flver, int Material);

    private readonly Dictionary<string, Template> _templates = new(StringComparer.Ordinal);

    /// <summary>DS3 material -> Elden Ring material used for it (or "" when none), for the build log.</summary>
    public SortedDictionary<string, string> Used { get; } = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <param name="erModels">Elden Ring's effect models (name, FLVER bytes), in the order to prefer them.</param>
    public EffectModelPort(IEnumerable<(string Name, byte[] Bytes)> erModels)
    {
        foreach (var (name, bytes) in erModels)
        {
            FLVER2 flver;
            try { flver = FLVER2.Read(bytes); }
            catch (Exception e) when (e is not OutOfMemoryException) { continue; }
            for (int i = 0; i < flver.Materials.Count; i++)
            {
                var key = Key(flver.Materials[i].MTD);
                // Only Elden Ring's general effect materials (S[...]), not the ones made for one model (s80010_S[AMSN]_em).
                if (!key.StartsWith("s[", StringComparison.Ordinal) || flver.Materials[i].Textures.Count == 0)
                    continue;
                // A material whose model also names its textures shows the slots are read from the FLVER.
                bool named = flver.Materials[i].Textures.Any(t => t.Path.Length > 0);
                if (!_templates.TryGetValue(key, out var have) || (named && !Named(have)))
                    _templates[key] = new Template(ItemText.Leaf(name), bytes, i);
            }
        }
    }

    private static bool Named(Template t) => FLVER2.Read(t.Flver).Materials[t.Material].Textures.Any(x => x.Path.Length > 0);

    public int TemplateCount => _templates.Count;

    /// <summary>A material's name as both games' models spell it: the MTD / MATXML leaf, no extension, no spaces, lower case.</summary>
    public static string Key(string mtd) =>
        Path.GetFileNameWithoutExtension(ItemText.Leaf(mtd.Replace('/', '\\'))).Replace(" ", "").ToLowerInvariant();

    /// <summary>
    /// The Elden Ring materials to use for a DS3 effect material, best first: the same name; then without the
    /// variants Elden Ring dropped (_Soft: fades where it meets the scene, _Falloff / _glow: rim light); then the
    /// nearest Elden Ring has of the same blend mode (add / al) and lighting (ARSN lit, A unlit).
    /// </summary>
    public static IEnumerable<string> Candidates(string ds3Mtd)
    {
        var key = Key(ds3Mtd);
        var seen = new HashSet<string>();
        IEnumerable<string> Variants(string k)
        {
            yield return k;
            // DS3's ARSN (albedo, reflectance, shininess, normal) became Elden Ring's AMSN for most models.
            if (k.Contains("[arsn]"))
                yield return k.Replace("[arsn]", "[amsn]");
        }
        var stripped = key.Replace("_soft", "").Replace("_falloff", "").Replace("_glow", "");
        var bases = new List<string> { key, stripped };
        // _e (emissive) is _em in most of Elden Ring's; an additive material can take the GX-driven additive one.
        if (stripped.EndsWith("_e", StringComparison.Ordinal))
            bases.Add(stripped + "m");
        if (stripped.EndsWith("_add", StringComparison.Ordinal))
        {
            bases.Add(stripped + "_gxflver");
            bases.Add(stripped + "_soft");
        }
        foreach (var b in bases)
            foreach (var v in Variants(b))
                if (seen.Add(v))
                    yield return v;
    }

    private Template? Find(string ds3Mtd, out string erKey)
    {
        foreach (var candidate in Candidates(ds3Mtd))
            if (_templates.TryGetValue(candidate, out var t))
            {
                erKey = candidate;
                return t;
            }
        erKey = "";
        return null;
    }

    /// <summary>Why a DS3 effect model can't be converted, or null when it can.</summary>
    public string? Unsupported(byte[] ds3Flver)
    {
        FLVER2 flver;
        try { flver = FLVER2.Read(ds3Flver); }
        catch (Exception e) when (e is not OutOfMemoryException) { return "model unreadable"; }
        foreach (var m in flver.Materials)
            if (Find(m.MTD, out _) is null)
            {
                Used[Key(m.MTD)] = "";
                return $"model material {Path.GetFileNameWithoutExtension(ItemText.Leaf(m.MTD))} has no Elden Ring equivalent";
            }
        return null;
    }

    /// <summary>The DS3 effect texture files (sfx\tex\sNNNNN.tpf IDs) a DS3 effect model's materials name.</summary>
    public static IEnumerable<int> TextureIds(byte[] ds3Flver)
    {
        FLVER2 flver;
        try { flver = FLVER2.Read(ds3Flver); }
        catch (Exception e) when (e is not OutOfMemoryException) { return []; }
        return flver.Materials.SelectMany(m => m.Textures).Select(t => TextureId(t.Path)).Where(id => id > 0).Distinct().ToList();
    }

    /// <summary>The effect texture file a texture path names (N:\FDP\data\Sfx\Tex\s04020_a.tif -> 4020), or -1.</summary>
    public static int TextureId(string path)
    {
        if (!path.Replace('/', '\\').Contains(@"\sfx\tex\", StringComparison.OrdinalIgnoreCase))
            return -1;
        var m = Digits().Match(Path.GetFileNameWithoutExtension(ItemText.Leaf(path)));
        return m.Success && int.TryParse(m.Value, out var id) ? id : -1;
    }

    /// <summary>Which DS3 texture slot fills an Elden Ring one (by what the slot holds), or null for none.</summary>
    internal static string? Ds3Slot(string erSlot)
    {
        bool Has(string s) => erSlot.Contains(s, StringComparison.OrdinalIgnoreCase);
        return Has("Emissive") ? "g_EmissiveTexture"
            : Has("Albedo") || Has("Diffuse") ? "g_DiffuseTexture"
            : Has("Normal") || Has("Bumpmap") ? "g_BumpmapTexture"
            : Has("Reflectance") || Has("Specular") ? "g_SpecularTexture"
            : Has("Shininess") ? "g_ShininessTexture"
            // Metallic has no DS3 counterpart (a reflectance map read as metalness turns the model to chrome).
            : null;
    }

    /// <summary>
    /// Converts a DS3 effect model. <paramref name="newTexture"/> gives the Elden Ring ID a DS3 effect texture file
    /// is ported under (null: not ported, the slot keeps the material's default).
    /// </summary>
    public byte[] Convert(byte[] ds3Flver, Func<int, int?> newTexture)
    {
        var flver = FLVER2.Read(ds3Flver);
        var materials = new List<FLVER2.Material>();
        var gxLists = new List<FLVER2.GXList>();
        var gxOf = new Dictionary<(string Model, int Index), int>();
        FLVER2.FLVERHeader? header = null;
        bool tangents = false;
        foreach (var m in flver.Materials)
        {
            var template = Find(m.MTD, out var erKey) ?? throw new InvalidDataException($"no Elden Ring material for {m.MTD}");
            Used[Key(m.MTD)] = $"{erKey} ({template.Model})";
            var erFlver = FLVER2.Read(template.Flver);
            var source = erFlver.Materials[template.Material];
            header ??= erFlver.Header;
            tangents |= erFlver.BufferLayouts.Any(l => l.Any(x => x.Semantic == FLVER.LayoutSemantic.Tangent));

            var filled = new HashSet<string>();
            var textures = new List<FLVER2.Texture>();
            foreach (var slot in source.Textures)
            {
                string path = "";
                // The first Elden Ring slot of a kind takes the DS3 texture; a second albedo layer stays the default.
                if (Ds3Slot(slot.ParamName) is { } ds3Slot && filled.Add(ds3Slot)
                    && m.Textures.FirstOrDefault(t => t.ParamName == ds3Slot && t.Path.Length > 0) is { } ds3Texture
                    && TextureId(ds3Texture.Path) is > 0 and var old && newTexture(old) is { } newId)
                {
                    var leaf = Path.GetFileNameWithoutExtension(ItemText.Leaf(ds3Texture.Path));
                    // The same rename the texture file gets: the digit run that is the old ID.
                    leaf = Digits().Replace(leaf, d => int.TryParse(d.Value, out var v) && v == old ? newId.ToString() : d.Value);
                    path = $@"N:\GR\data\Sfx\Tex\{leaf}.tif";
                }
                textures.Add(new FLVER2.Texture(slot.ParamName, path, slot.TilingScale, slot.TilingTypeU, slot.TilingTypeV, slot.Unk14, slot.Unk18, slot.Unk1C));
            }
            int gx = -1;
            if (source.GXIndex >= 0 && source.GXIndex < erFlver.GXLists.Count && !gxOf.TryGetValue((template.Model, source.GXIndex), out gx))
            {
                gx = gxOf[(template.Model, source.GXIndex)] = gxLists.Count;
                gxLists.Add(erFlver.GXLists[source.GXIndex]);
            }
            materials.Add(new FLVER2.Material { Name = m.Name, MTD = source.MTD, Textures = textures, GXIndex = gx, Index = source.Index });
        }

        var (min, max) = (flver.Header.BoundingBoxMin, flver.Header.BoundingBoxMax);
        flver.Header = header ?? throw new InvalidDataException("the model has no materials");
        flver.Header.BoundingBoxMin = min;
        flver.Header.BoundingBoxMax = max;
        flver.Materials = materials;
        flver.GXLists = gxLists;
        // FLVER 0x2001A stores a skeleton set and flags each node with its role; DS3's 0x20014 has neither.
        flver.Skeletons = new FLVER2.SkeletonSet { BaseSkeleton = ModelPort.SkeletonFromNodes(flver), AllSkeletons = ModelPort.SkeletonFromNodes(flver) };
        ModelPort.SetNodeFlags(flver);
        // Culling boxes in Elden Ring's form (centre / half size / rotation), from the vertices: DS3's corners were
        // carried as they were, with a zero centre.
        WeaponNodes.SetBounds(flver);

        // Elden Ring vertices carry one tangent, and its lit effect materials read one: DS3's unlit models have none.
        foreach (var layout in flver.BufferLayouts)
            layout.RemoveAll(x => x.Semantic == FLVER.LayoutSemantic.Tangent && x.Index > 0);
        if (tangents)
            foreach (var mesh in flver.Meshes)
            {
                var layouts = mesh.VertexBuffers.Select(b => flver.BufferLayouts[b.LayoutIndex]).ToList();
                if (layouts.Count == 0 || layouts.Any(l => l.Any(x => x.Semantic == FLVER.LayoutSemantic.Tangent)))
                    continue;
                int after = layouts[0].FindIndex(x => x.Semantic == FLVER.LayoutSemantic.Normal);
                layouts[0].Insert(after < 0 ? layouts[0].Count : after + 1, new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.Tangent, 0));
            }
        foreach (var mesh in flver.Meshes)
        {
            int want = mesh.VertexBuffers.Sum(b => flver.BufferLayouts[b.LayoutIndex].Count(x => x.Semantic == FLVER.LayoutSemantic.Tangent));
            foreach (var vertex in mesh.Vertices)
            {
                if (vertex.Tangents.Count > want)
                    vertex.Tangents.RemoveRange(want, vertex.Tangents.Count - want);
                while (vertex.Tangents.Count < want)
                    vertex.Tangents.Add(new System.Numerics.Vector4(1, 0, 0, 1));
            }
        }
        return flver.Write();
    }
}
