using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Converts Dark Souls III map piece models (mapbnd FLVERs) into Elden Ring ones. Built on the weapon model port
/// (<see cref="ModelPort"/>), which already runs in game: Elden Ring's FLVER header, GX list and material slots come
/// from a vanilla Elden Ring model of the same kind (a legacy dungeon's map pieces, or a plain asset), geometry from
/// DS3, and each DS3 material becomes a copy of a vanilla Elden Ring MATBIN whose texture samplers name the DS3
/// textures (by bare name, as JortPob does; <see cref="MapTextures"/> puts them where Elden Ring loads them).
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
    private readonly Dictionary<string, NewMaterial> _materials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every DS3 texture (by name) some converted material uses.</summary>
    public HashSet<string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<NewMaterial> Materials => _materials.Values;
    public List<string> Notes { get; } = [];

    private MapModelPort(Dictionary<string, MaterialTemplate> templates, FLVER2 templateFlver)
    {
        _templates = templates;
        _templateFlver = templateFlver;
    }

    /// <summary>A new port with the same templates and none of this one's materials (one per map).</summary>
    public MapModelPort Fresh() => new(_templates, _templateFlver);

    public string TemplateSummary => string.Join(", ", _templates.Select(t => $"{t.Key}={t.Value.Matbin}"));

    /// <summary>
    /// Picks material templates from vanilla Elden Ring models, earlier ones first: an opaque one-layer material, and
    /// where there are any, an alpha-tested one and a two-layer one. A template with a normal map wins over one
    /// without. <paramref name="headerFlver"/> gives the FLVER header (a map piece); the first model when null.
    /// </summary>
    public static MapModelPort Create(IReadOnlyList<FLVER2> flvers, IReadOnlyDictionary<string, BinderFile> matbins, FLVER2? headerFlver = null)
    {
        if (flvers.Count == 0)
            throw new InvalidDataException("no Elden Ring model to copy materials from");
        var templates = new Dictionary<string, MaterialTemplate>();
        var withNormal = new HashSet<string>();
        foreach (var flver in flvers)
            foreach (var material in flver.Materials)
            {
                var name = ModelPort.MatbinName(material.MTD);
                if (!matbins.TryGetValue(name, out var file) || material.GXIndex < 0 || material.GXIndex >= flver.GXLists.Count)
                    continue;
                MATBIN matbin;
                try { matbin = MATBIN.Read(file.Bytes); }
                catch (Exception) { continue; }
                var kind = Classify(matbin);
                if (kind is null || withNormal.Contains(kind))
                    continue;
                var normal = matbin.Samplers.Any(x => Role(x.Type) is ("normal", 0));
                if (templates.ContainsKey(kind) && !normal)
                    continue;
                templates[kind] = new MaterialTemplate(kind, name, file, material, flver.GXLists[material.GXIndex]);
                if (normal)
                    withNormal.Add(kind);
            }
        if (!templates.ContainsKey("opaque"))
            throw new InvalidDataException("no plain one-layer material among these models to use as a template");
        return new MapModelPort(templates, headerFlver ?? flvers[0]);
    }

    private static readonly Regex SamplerRole = new(@"(GSBlendMap_)?(Albedo|Normal|Metallic)Map(?:_(\d))?$", RegexOptions.IgnoreCase);

    /// <summary>
    /// What an Elden Ring sampler holds: ("albedo"|"normal"|"metallic", layer), or ("blend", n) for a terrain blend
    /// layer (GSBlendMap), null for anything else. "M_A__snp_Texture2D_0_AlbedoMap_0" is ("albedo", 0).
    /// </summary>
    public static (string Role, int Layer)? Role(string samplerType)
    {
        var m = SamplerRole.Match(samplerType ?? "");
        if (!m.Success)
            return null;
        var layer = m.Groups[3].Success ? m.Groups[3].Value[0] - '0' : 0;
        return m.Groups[1].Success ? ("blend", layer) : (m.Groups[2].Value.ToLowerInvariant(), layer);
    }

    /// <summary>"opaque", "alpha" or "twolayer" for a usable Elden Ring MATBIN, null for anything else.</summary>
    public static string? Classify(MATBIN matbin)
    {
        var roles = matbin.Samplers.Select(x => Role(x.Type)).Where(r => r is not null).Select(r => r!.Value).ToList();
        // Terrain blend materials (most of Elden Ring's legacy dungeon pieces) mix layers through a mask: not usable.
        if (roles.Any(r => r.Role == "blend") || !roles.Contains(("albedo", 0)))
            return null;
        var shader = (matbin.ShaderPath ?? "") + "|" + (matbin.SourcePath ?? "");
        if (Regex.IsMatch(shader, "water|fog|light|decal|glass|emissive|cloth|grass|tree|leaf|leaves|lava|swamp|sea_", RegexOptions.IgnoreCase))
            return null;
        if (roles.Contains(("albedo", 1)))
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
    public BND4 ConvertBinder(BND4 ds3, BND4 hostTemplate, MapId ds3Id, MapId erId, string modelNumber, MapId? hostId = null)
    {
        var templateFile = hostTemplate.Files.First(f => FLVER2.Is(f.Bytes));
        var templateLeaf = ItemText.Leaf(templateFile.Name);
        var folder = templateFile.Name[..^templateLeaf.Length];
        var templateStem = templateLeaf[..templateLeaf.IndexOf('.')];
        var newStem = $"{erId}_{modelNumber}";
        // The folder can itself name the template model ("...\m45_00_00_00_000100\sib\").
        folder = folder.Replace(templateStem, newStem, StringComparison.OrdinalIgnoreCase);
        if (hostId is { } host && host != erId)
            folder = folder.Replace(host.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);

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
        MakeStatic(flver);
        flver.Skeletons = new FLVER2.SkeletonSet { BaseSkeleton = Bones(flver), AllSkeletons = Bones(flver) };

        // One GX list per template kind, in a fixed order.
        var kinds = _templates.Keys.OrderBy(k => k).ToList();
        flver.GXLists = kinds.Select(k => _templates[k].GxList).ToList();

        // Binders convert in parallel; the shared material table, texture list and notes are changed under a lock.
        lock (_materials)
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
                // Named from what it holds, not from the order binders converted in, so every build names it the same.
                material = new NewMaterial($"DKS_{ds3Id.Digits}_{StableHash(key)}", template, albedo ?? "", normal ?? "",
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

    /// <summary>
    /// Puts every unskinned mesh on the model's one root node, the way JortPob builds Elden Ring map pieces
    /// (NIFToFLVER.cs: NodeIndex 0, no per-mesh bone list, NormalW 0). DS3's exporter left map piece meshes pointing
    /// at Maya nodes the FLVER never held (mesh.NodeIndex up to 12 in one-node files: 6530 meshes, 2098 of the first
    /// build's 14,000 models), which Dark Souls III ignores but Elden Ring reads past its node table for. Dummies on a
    /// missing node lose their parent the same way. Returns how many meshes pointed past the node table.
    /// </summary>
    public static int MakeStatic(FLVER2 flver)
    {
        if (flver.Nodes.Count == 0)
            flver.Nodes.Add(new FLVER.Node { Name = "root", ParentIndex = -1, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1 });
        int past = 0;
        foreach (var mesh in flver.Meshes)
        {
            if (mesh.UseBoneWeights)
                continue;
            if (mesh.NodeIndex < 0 || mesh.NodeIndex >= flver.Nodes.Count)
                past++;
            mesh.NodeIndex = 0;
            mesh.BoneIndices = [];
            foreach (var vertex in mesh.Vertices)
                for (int i = 0; i < vertex.NormalWs.Count; i++)
                    vertex.NormalWs[i] = 0;
        }
        foreach (var dummy in flver.Dummies)
        {
            if (dummy.ParentBoneIndex >= flver.Nodes.Count)
                dummy.ParentBoneIndex = (short)-1;
            if (dummy.AttachBoneIndex >= flver.Nodes.Count)
                dummy.AttachBoneIndex = (short)-1;
        }
        return past;
    }

    // Eight hex digits of SHA-1: two different materials of one map sharing them is a one in billions chance.
    private static string StableHash(string key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..8];

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
        foreach (var material in _materials.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var matbin = MATBIN.Read(material.Template.File.Bytes);
            foreach (var sampler in matbin.Samplers)
            {
                var texture = Role(sampler.Type) switch
                {
                    ("albedo", 0) => material.Albedo,
                    ("normal", 0) => material.Normal,
                    ("albedo", 1) => material.Albedo2 ?? material.Albedo,
                    ("normal", 1) => material.Normal2 ?? material.Normal,
                    ("metallic", _) => BlackMetallic,
                    _ => null,
                };
                // The bare texture name, the way JortPob's materials name the textures it adds.
                if (!string.IsNullOrEmpty(texture))
                    sampler.Path = texture;
            }
            matbin.SourcePath = ReplaceStem(matbin.SourcePath, material.Template.Matbin, material.Name);
            var name = ReplaceStem(material.Template.File.Name, material.Template.Matbin, material.Name);
            yield return new BinderFile(material.Template.File.Flags, id++, name, matbin.Write());
        }
    }
}
