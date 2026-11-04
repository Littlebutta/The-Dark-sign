using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Converts Dark Souls III map piece models (mapbnd FLVERs) into Elden Ring ones. Built on the weapon model port
/// (<see cref="ModelPort"/>), which already runs in game: Elden Ring's FLVER header, GX list and material slots come
/// from a vanilla Elden Ring model of the same kind (here a map piece of the host map), geometry from DS3, and each
/// DS3 material becomes a copy of a vanilla Elden Ring MATBIN whose texture samplers point at the DS3 textures.
/// </summary>
public sealed class MapModelPort
{
    /// <summary>One new MATBIN: a copy of <see cref="Template"/> with its own textures.</summary>
    public sealed record NewMaterial(string Name, MaterialTemplate Template, string Albedo, string Normal, string? Albedo2,
        string? Normal2);

    /// <summary>A vanilla Elden Ring map material used as a pattern, with an example of it in a FLVER.</summary>
    public sealed record MaterialTemplate(string Kind, string Matbin, BinderFile File, FLVER2.Material Material, FLVER2.GXList GxList);

    public const string BlackMetallic = "DKS_black_m";

    private readonly Dictionary<string, MaterialTemplate> _templates;
    private readonly FLVER2 _templateFlver;
    private readonly string _textureFolder;
    private readonly Dictionary<string, NewMaterial> _materials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every DS3 texture (by name) some converted material uses.</summary>
    public HashSet<string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<NewMaterial> Materials => _materials.Values;
    public List<string> Notes { get; } = [];

    private MapModelPort(Dictionary<string, MaterialTemplate> templates, FLVER2 templateFlver, string textureFolder)
    {
        _templates = templates;
        _templateFlver = templateFlver;
        _textureFolder = textureFolder;
    }

    public string TemplateSummary =>
        string.Join(", ", _templates.Select(t => $"{t.Key}={t.Value.Matbin}")) + $"; textures in {_textureFolder}";

    /// <summary>
    /// Picks material templates from the host map's own map pieces: an opaque one-layer material, and where the host
    /// has them, an alpha-tested one and a two-layer (blended) one.
    /// </summary>
    public static MapModelPort Create(IReadOnlyList<FLVER2> hostFlvers, IReadOnlyDictionary<string, BinderFile> matbins, MapId er)
    {
        if (hostFlvers.Count == 0)
            throw new InvalidDataException("the host map has no map piece model to copy");
        var templates = new Dictionary<string, MaterialTemplate>();
        string? folder = null;
        foreach (var flver in hostFlvers)
            foreach (var material in flver.Materials)
            {
                var name = ModelPort.MatbinName(material.MTD);
                if (!matbins.TryGetValue(name, out var file) || material.GXIndex < 0 || material.GXIndex >= flver.GXLists.Count)
                    continue;
                var matbin = MATBIN.Read(file.Bytes);
                folder ??= matbin.Samplers.Select(s => MapTextureFolder(s.Path)).FirstOrDefault(f => f is not null);
                var kind = Classify(matbin);
                if (kind is null || templates.ContainsKey(kind))
                    continue;
                templates[kind] = new MaterialTemplate(kind, name, file, material, flver.GXLists[material.GXIndex]);
            }
        if (!templates.ContainsKey("opaque"))
            throw new InvalidDataException("no plain albedo+normal material among the host map's pieces to use as a template");
        folder = folder is null
            ? $@"N:\GR\data\Model\map\{er.AreaFolder}\tex\"
            : Regex.Replace(folder, @"\\m\d\d\\tex\\", $@"\{er.AreaFolder}\tex\", RegexOptions.IgnoreCase);
        return new MapModelPort(templates, hostFlvers[0], folder);
    }

    // "N:\GR\data\Model\map\m10\tex\m10_00_x_a.tif" -> "N:\GR\data\Model\map\m10\tex\"
    private static string? MapTextureFolder(string path)
    {
        var m = Regex.Match(path ?? "", @"^(.*\\map\\m\d\d\\tex\\)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>"opaque", "alpha" or "twolayer" for a usable Elden Ring map MATBIN, null for anything else.</summary>
    public static string? Classify(MATBIN matbin)
    {
        bool Has(string type) => matbin.Samplers.Any(s => s.Type.Contains(type, StringComparison.OrdinalIgnoreCase));
        if (!Has("AlbedoMap_0") || !Has("NormalMap_0"))
            return null;
        var shader = (matbin.ShaderPath ?? "") + "|" + (matbin.SourcePath ?? "");
        if (Regex.IsMatch(shader, "water|fog|light|decal|glass|emissive|cloth|grass|tree|leaf|leaves", RegexOptions.IgnoreCase))
            return null;
        if (Has("AlbedoMap_1"))
            return "twolayer";
        if (Regex.IsMatch(shader, "_Alp|Alpha", RegexOptions.IgnoreCase))
            return "alpha";
        return "opaque";
    }

    /// <summary>The kind of Elden Ring material a DS3 material maps to, from its MTD name and texture slots.</summary>
    public static string Ds3Kind(FLVER2.Material material)
    {
        var mtd = ItemText.Leaf(material.MTD);
        if (material.Textures.Any(t => t.ParamName == "g_DiffuseTexture2" && t.Path.Length > 0))
            return "twolayer";
        if (Regex.IsMatch(mtd, "_Alp|Edge", RegexOptions.IgnoreCase))
            return "alpha";
        return "opaque";
    }

    /// <summary>Converts every FLVER in a DS3 mapbnd; the binder layout comes from the host's template binder.</summary>
    public BND4 ConvertBinder(BND4 ds3, BND4 hostTemplate, MapId ds3Id, MapId erId, string modelNumber)
    {
        var templateFile = hostTemplate.Files.First(f => FLVER2.Is(f.Bytes));
        var templateLeaf = ItemText.Leaf(templateFile.Name);
        var folder = templateFile.Name[..^templateLeaf.Length];
        var templateStem = templateLeaf[..templateLeaf.IndexOf('.')];
        var newStem = $"{erId}_{modelNumber}";
        // The folder can itself name the template model ("...\m45_00_00_00_000100\sib\").
        folder = folder.Replace(templateStem, newStem, StringComparison.OrdinalIgnoreCase);

        var files = new List<BinderFile>();
        foreach (var file in ds3.Files.Where(f => FLVER2.Is(f.Bytes)))
        {
            var flver = FLVER2.Read(file.Bytes);
            var ds3Leaf = ItemText.Leaf(file.Name);
            var leaf = ds3Leaf.Replace(ds3Id.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);
            ConvertFlver(flver, ds3Id);
            files.Add(new BinderFile(templateFile.Flags, files.Count, folder + leaf, flver.Write()));
        }
        var binder = new BND4
        {
            Version = hostTemplate.Version,
            Format = hostTemplate.Format,
            BigEndian = hostTemplate.BigEndian,
            BitBigEndian = hostTemplate.BitBigEndian,
            Unicode = hostTemplate.Unicode,
            Extended = hostTemplate.Extended,
            Unk04 = hostTemplate.Unk04,
            Unk05 = hostTemplate.Unk05,
            Files = files,
        };
        return binder;
    }

    private void ConvertFlver(FLVER2 flver, MapId ds3Id)
    {
        var bboxMin = flver.Header.BoundingBoxMin;
        var bboxMax = flver.Header.BoundingBoxMax;
        // A copy of the template's header: sharing the object would give every model the last one's bounding box.
        var h = _templateFlver.Header;
        flver.Header = new FLVER2.FLVERHeader
        {
            BigEndian = h.BigEndian, Version = h.Version, Unicode = h.Unicode, Unk4A = h.Unk4A, Unk4B = h.Unk4B,
            Unk4C = h.Unk4C, Unk5C = h.Unk5C, Unk5D = h.Unk5D, Unk68 = h.Unk68, SpecialModifier = h.SpecialModifier,
            Unk74 = h.Unk74, BoundingBoxMin = bboxMin, BoundingBoxMax = bboxMax,
        };
        flver.Skeletons = new FLVER2.SkeletonSet { BaseSkeleton = Bones(flver), AllSkeletons = Bones(flver) };

        // One GX list per template kind, in a fixed order.
        var kinds = _templates.Keys.OrderBy(k => k).ToList();
        flver.GXLists = kinds.Select(k => _templates[k].GxList).ToList();

        foreach (var m in flver.Materials)
        {
            var kind = Ds3Kind(m);
            if (!_templates.TryGetValue(kind, out var template))
                template = _templates[kind = "opaque"];

            string? Texture(string param)
            {
                var path = m.Textures.FirstOrDefault(t => t.ParamName == param && t.Path.Length > 0)?.Path;
                return path is null ? null : Path.GetFileNameWithoutExtension(ItemText.Leaf(path));
            }
            var albedo = Texture("g_DiffuseTexture");
            var normal = Texture("g_BumpmapTexture");
            var albedo2 = Texture("g_DiffuseTexture2");
            var normal2 = Texture("g_BumpmapTexture2");
            if (albedo is null)
                Notes.Add($"{ItemText.Leaf(m.MTD)} ({m.Name}) has no albedo texture; it keeps the template's");
            foreach (var t in new[] { albedo, normal, albedo2, normal2 })
                if (t is not null)
                    Textures.Add(t);

            var key = string.Join("|", template.Matbin, albedo, normal, albedo2, normal2);
            if (!_materials.TryGetValue(key, out var material))
            {
                material = new NewMaterial($"DKS_{ds3Id.Digits}_{_materials.Count:D4}", template, albedo ?? "", normal ?? "",
                    albedo2, normal2);
                _materials[key] = material;
            }

            var tm = template.Material;
            m.Name = material.Name;
            m.MTD = ReplaceStem(tm.MTD, template.Matbin, material.Name);
            m.GXIndex = kinds.IndexOf(kind);
            m.Textures = tm.Textures
                .Select(t => new FLVER2.Texture(t.ParamName, "", t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C))
                .ToList();
        }

        // Same vertex fixes as the weapon port: one tangent, and a vertex color on every mesh.
        foreach (var layout in flver.BufferLayouts)
            layout.RemoveAll(member => member.Semantic == FLVER.LayoutSemantic.Tangent && member.Index > 0);
        foreach (var vertex in flver.Meshes.SelectMany(mesh => mesh.Vertices))
            if (vertex.Tangents.Count > 1)
                vertex.Tangents.RemoveRange(1, vertex.Tangents.Count - 1);
        var changed = new HashSet<FLVER2.BufferLayout>();
        foreach (var mesh in flver.Meshes)
        {
            var layouts = mesh.VertexBuffers.Select(b => flver.BufferLayouts[b.LayoutIndex]).ToList();
            if (layouts.Count == 0 || layouts.Any(l => l.Any(member => member.Semantic == FLVER.LayoutSemantic.VertexColor)))
                continue;
            var first = layouts[0];
            int uv = first.FindIndex(member => member.Semantic == FLVER.LayoutSemantic.UV);
            first.Insert(uv < 0 ? first.Count : uv, new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor, 1));
            changed.Add(first);
        }
        foreach (var mesh in flver.Meshes)
        {
            var layouts = mesh.VertexBuffers.Select(b => flver.BufferLayouts[b.LayoutIndex]).ToList();
            if (!layouts.Any(changed.Contains))
                continue;
            int colors = layouts.Sum(l => l.Count(member => member.Semantic == FLVER.LayoutSemantic.VertexColor));
            foreach (var vertex in mesh.Vertices)
                while (vertex.Colors.Count < colors)
                    vertex.Colors.Add(new FLVER.VertexColor(1f, 1f, 1f, 1f));
        }
    }

    private static List<FLVER2.SkeletonSet.Bone> Bones(FLVER2 flver) =>
        flver.Nodes.Select((n, i) => new FLVER2.SkeletonSet.Bone(i)
        {
            ParentIndex = n.ParentIndex,
            FirstChildIndex = n.FirstChildIndex,
            NextSiblingIndex = n.NextSiblingIndex,
            PreviousSiblingIndex = n.PreviousSiblingIndex,
        }).ToList();

    private static string ReplaceStem(string path, string oldStem, string newStem)
    {
        var leaf = ItemText.Leaf(path);
        var newLeaf = leaf.Replace(oldStem, newStem, StringComparison.OrdinalIgnoreCase);
        return path[..^leaf.Length] + newLeaf;
    }

    /// <summary>
    /// The MATBIN files for every new material: copies of their templates with the albedo and normal samplers
    /// (both layers) pointed at the DS3 textures and the metallic sampler at a black texture (DS3 has none; its
    /// reflectance maps don't mean the same thing).
    /// </summary>
    public IEnumerable<BinderFile> MatbinFiles(int firstId)
    {
        var id = firstId;
        foreach (var material in _materials.Values)
        {
            var matbin = MATBIN.Read(material.Template.File.Bytes);
            foreach (var sampler in matbin.Samplers)
            {
                var texture = sampler.Type switch
                {
                    var t when t.Contains("AlbedoMap_0", StringComparison.OrdinalIgnoreCase) => material.Albedo,
                    var t when t.Contains("NormalMap_0", StringComparison.OrdinalIgnoreCase) => material.Normal,
                    var t when t.Contains("AlbedoMap_1", StringComparison.OrdinalIgnoreCase) => material.Albedo2 ?? material.Albedo,
                    var t when t.Contains("NormalMap_1", StringComparison.OrdinalIgnoreCase) => material.Normal2 ?? material.Normal,
                    var t when t.Contains("MetallicMap", StringComparison.OrdinalIgnoreCase) => BlackMetallic,
                    _ => null,
                };
                if (string.IsNullOrEmpty(texture))
                    continue;
                var extension = Path.GetExtension(ItemText.Leaf(sampler.Path));
                sampler.Path = _textureFolder + texture + (extension.Length > 0 ? extension : ".tif");
            }
            matbin.SourcePath = ReplaceStem(matbin.SourcePath, material.Template.Matbin, material.Name);
            var name = ReplaceStem(material.Template.File.Name, material.Template.Matbin, material.Name);
            yield return new BinderFile(material.Template.File.Flags, id++, name, matbin.Write());
        }
    }
}
