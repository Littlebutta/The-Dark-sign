using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// A DS3 character model in Elden Ring's FLVER layout, on its Elden Ring template: Elden Ring's header, GX list and
/// skeleton set, DS3's geometry and bones (whose bind pose the ported skeleton shares, see ChrPort), skin indices
/// into the node list instead of DS3's per-mesh palettes, one tangent, and a vertex colour where Elden Ring's shaders
/// read one. Every DS3 material becomes a copy of the template's matching material pointed at DS3's textures.
/// </summary>
public static class ChrModel
{
    public static List<ChrMaterial> Convert(FLVER2 flver, FLVER2 template, int ds3Chr, int newChr)
    {
        if (!ChrPort.HasBody(template) || flver.Materials.Count == 0)
            throw new InvalidDataException("the template or the DS3 model has nothing to draw");
        var bboxMin = flver.Header.BoundingBoxMin;
        var bboxMax = flver.Header.BoundingBoxMax;
        flver.Header = template.Header;
        flver.Header.BoundingBoxMin = bboxMin;
        flver.Header.BoundingBoxMax = bboxMax;

        ChrPort.GlobalBoneIndices(flver);
        flver.Skeletons = new FLVER2.SkeletonSet { BaseSkeleton = SkeletonFromNodes(flver), AllSkeletons = SkeletonFromNodes(flver) };
        flver.GXLists = [template.GXLists[0]];

        var materials = new List<ChrMaterial>();
        string oldTag = $"c{ds3Chr:D4}", newTag = $"c{newChr:D4}";
        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var m = flver.Materials[i];
            // The template material whose name shares the most with this one (body, cloth, hair...), else its first.
            var templateMaterial = template.Materials.MaxBy(t => SharedWords(t.Name, m.Name)) ?? template.Materials[0];
            string? Ds3Texture(params string[] names) => m.Textures.FirstOrDefault(t => t.Path.Length > 0
                    && names.Any(n => t.ParamName.Equals(n, StringComparison.Ordinal) || t.ParamName.EndsWith(n, StringComparison.Ordinal))) is { } t
                ? TextureName(Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path)), oldTag, newTag)
                : null;
            var albedo = Ds3Texture("g_DiffuseTexture", "_AlbedoMap_0");
            var normal = Ds3Texture("g_BumpmapTexture", "_NormalMap_0");
            var specular = Ds3Texture("g_SpecularTexture", "_ReflectanceMap_0");
            var metallic = specular is not null && specular.EndsWith("_r", StringComparison.OrdinalIgnoreCase) ? specular[..^2] + "_m" : null;
            var templateMatbin = ModelPort.MatbinName(templateMaterial.MTD);
            var newMatbin = $"{templateMatbin}_{newTag}_{i}";
            m.Name = $"{newTag}_{i}";
            m.MTD = templateMaterial.MTD.Replace(templateMatbin, newMatbin, StringComparison.OrdinalIgnoreCase);
            m.GXIndex = 0;
            m.Textures = templateMaterial.Textures
                .Select(t => new FLVER2.Texture(t.ParamName, "", t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C))
                .ToList();
            materials.Add(new ChrMaterial(templateMatbin, newMatbin, albedo, normal, metallic));
        }

        foreach (var layout in flver.BufferLayouts)
            layout.RemoveAll(lm => lm.Semantic == FLVER.LayoutSemantic.Tangent && lm.Index > 0);
        foreach (var vertex in flver.Meshes.SelectMany(mesh => mesh.Vertices))
            if (vertex.Tangents.Count > 1)
                vertex.Tangents.RemoveRange(1, vertex.Tangents.Count - 1);
        var changed = new HashSet<FLVER2.BufferLayout>();
        foreach (var mesh in flver.Meshes)
        {
            var layouts = mesh.VertexBuffers.Select(b => flver.BufferLayouts[b.LayoutIndex]).ToList();
            if (layouts.Count == 0 || layouts.Any(l => l.Any(lm => lm.Semantic == FLVER.LayoutSemantic.VertexColor)))
                continue;
            var first = layouts[0];
            int uv = first.FindIndex(lm => lm.Semantic == FLVER.LayoutSemantic.UV);
            first.Insert(uv < 0 ? first.Count : uv, new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor, 1));
            changed.Add(first);
        }
        foreach (var mesh in flver.Meshes)
        {
            var layouts = mesh.VertexBuffers.Select(b => flver.BufferLayouts[b.LayoutIndex]).ToList();
            if (!layouts.Any(changed.Contains))
                continue;
            int colors = layouts.Sum(l => l.Count(lm => lm.Semantic == FLVER.LayoutSemantic.VertexColor));
            foreach (var vertex in mesh.Vertices)
                while (vertex.Colors.Count < colors)
                    vertex.Colors.Add(new FLVER.VertexColor(1f, 1f, 1f, 1f));
        }
        return materials;
    }

    private static int SharedWords(string a, string b)
    {
        var words = (string s) => s.ToLowerInvariant().Split(['_', ' ', '[', ']', '#', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !(w[0] == 'c' && w.Skip(1).All(char.IsDigit))).ToHashSet();
        return words(a).Intersect(words(b)).Count();
    }

    private static List<FLVER2.SkeletonSet.Bone> SkeletonFromNodes(FLVER2 flver) =>
        flver.Nodes.Select((node, i) => new FLVER2.SkeletonSet.Bone(i)
        {
            ParentIndex = node.ParentIndex,
            FirstChildIndex = node.FirstChildIndex,
            NextSiblingIndex = node.NextSiblingIndex,
            PreviousSiblingIndex = node.PreviousSiblingIndex,
        }).ToList();

    /// <summary>A DS3 texture name under the new character ID ("c1020_body_a" -> "c7003_body_a").</summary>
    public static string TextureName(string name, string oldTag, string newTag) =>
        name.Replace(oldTag, newTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>DS3 textures renamed to the new ID, plus a flat _m (metallic) map per _r (reflectance) map.</summary>
    public static TPF ConvertTpf(TPF tpf, int ds3Chr, int newChr)
    {
        string oldTag = $"c{ds3Chr:D4}", newTag = $"c{newChr:D4}";
        var added = new List<TPF.Texture>();
        foreach (var t in tpf.Textures)
        {
            t.Name = TextureName(t.Name, oldTag, newTag);
            t.Platform = TPF.TPFPlatform.PC;
            if (t.Name.EndsWith("_r", StringComparison.OrdinalIgnoreCase))
                added.Add(new TPF.Texture(t.Name[..^2] + "_m", 103, 0, ModelPort.BlackBc4Dds(), TPF.TPFPlatform.PC));
        }
        tpf.Textures.AddRange(added);
        return tpf;
    }
}

/// <summary>
/// A material a ported character needs: a copy of the template's MATBIN whose albedo, normal and metallic samplers
/// name DS3's textures (null keeps the template's texture for that sampler).
/// </summary>
public sealed record ChrMaterial(string TemplateMatbin, string NewMatbin, string? Albedo, string? Normal, string? Metallic)
{
    private const string Target = "/material/allmaterial_dlc02.matbinbnd.dcx";

    /// <summary>
    /// Adds the materials to the DLC02 material binder, starting from the one port-weapons already wrote to the mod
    /// folder (its weapon materials stay), and returns the file written.
    /// </summary>
    public static string Write(PorterContext ctx, GameArchives er, IEnumerable<ChrMaterial> materials)
    {
        var templates = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var binder in Explore.MaterialBinders)
            foreach (var f in BND4.Read(er.Read(binder)).Files)
                templates.TryAdd(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)), f);
        var modPath = ctx.ModPath("material", ItemText.Leaf(Target));
        var (_, compression) = er.ReadWithCompression(Target);
        var bnd = BND4.Read(File.Exists(modPath) ? DCX.Decompress(File.ReadAllBytes(modPath)) : er.Read(Target));
        var existing = bnd.Files.Select(f => Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int nextId = bnd.Files.Max(f => f.ID) + 1, added = 0, missing = 0;
        foreach (var m in materials.DistinctBy(m => m.NewMatbin, StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Contains(m.NewMatbin))
                continue;
            if (!templates.TryGetValue(m.TemplateMatbin, out var templateFile))
            {
                missing++;
                continue;
            }
            var matbin = MATBIN.Read(templateFile.Bytes);
            foreach (var sampler in matbin.Samplers)
            {
                var texture = sampler.Type.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ? m.Albedo
                    : sampler.Type.Contains("Normal", StringComparison.OrdinalIgnoreCase) ? m.Normal
                    : sampler.Type.Contains("Metallic", StringComparison.OrdinalIgnoreCase) ? m.Metallic
                    : null;
                // Only the template's own textures are swapped; shared detail maps stay.
                if (texture is null || sampler.Path.Length == 0 || sampler.Path.Contains("\\tex\\", StringComparison.OrdinalIgnoreCase))
                    continue;
                var leaf = ItemText.Leaf(sampler.Path);
                sampler.Path = sampler.Path[..^leaf.Length] + texture + Path.GetExtension(leaf);
            }
            matbin.SourcePath = matbin.SourcePath.Replace(m.TemplateMatbin, m.NewMatbin, StringComparison.OrdinalIgnoreCase);
            var name = templateFile.Name.Replace(m.TemplateMatbin, m.NewMatbin, StringComparison.OrdinalIgnoreCase);
            bnd.Files.Add(new BinderFile(templateFile.Flags, nextId++, name, matbin.Write()));
            existing.Add(m.NewMatbin);
            added++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
        File.WriteAllBytes(modPath, DCX.Compress(bnd.Write(), compression));
        Console.WriteLine($"enemies: materials: {added} added to {ItemText.Leaf(Target)}{(missing > 0 ? $" ({missing} template materials not found)" : "")}");
        return modPath;
    }
}
