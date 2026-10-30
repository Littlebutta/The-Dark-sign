using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Converts a DS3 weapon model binder (parts\wp_a_NNNN.partsbnd) into an Elden Ring one under a new model ID.
/// Geometry and textures come from DS3; the binder layout, FLVER header, GX list and material slots come from the
/// Elden Ring template weapon, since those are what Elden Ring's renderer expects.
/// </summary>
public static class ModelPort
{
    public sealed record ConvertedModel(BND4 Binder, DCX.CompressionInfo Compression, List<NewMaterial> Materials);

    /// <summary>A material the converted model needs: a copy of a template MATBIN pointed at the new textures.</summary>
    public sealed record NewMaterial(string TemplateMatbin, string NewMatbin, string TextureBase);

    public static string PartsPath(int modelId, bool lod = false) =>
        $"/parts/wp_a_{modelId:D4}{(lod ? "_l" : "")}.partsbnd.dcx";

    public static ConvertedModel? Convert(GameArchives ds3, GameArchives er, int ds3Model, int templateModel, int newModel, bool lod)
    {
        var ds3Path = PartsPath(ds3Model, lod);
        var erPath = PartsPath(templateModel, lod);
        if (!ds3.Contains(ds3Path) || !er.Contains(erPath))
            return null;

        var source = BND4.Read(ds3.Read(ds3Path));
        var (templateBytes, compression) = er.ReadWithCompression(erPath);
        var template = BND4.Read(templateBytes);
        var templateFlver = template.Files.Where(f => FLVER2.Is(f.Bytes)).Select(f => FLVER2.Read(f.Bytes)).First();
        var templateFlverName = template.Files.First(f => FLVER2.Is(f.Bytes)).Name;
        string oldTag = $"WP_A_{ds3Model:D4}", tmplTag = $"WP_A_{templateModel:D4}", newTag = $"WP_A_{newModel:D4}";
        var folder = templateFlverName[..(templateFlverName.Replace('/', '\\').LastIndexOf('\\') + 1)].Replace(tmplTag, newTag);

        var materials = new List<NewMaterial>();
        var files = new List<BinderFile>();
        foreach (var file in source.Files)
        {
            var leaf = ItemText.Leaf(file.Name).Replace(oldTag, newTag, StringComparison.OrdinalIgnoreCase);
            if (FLVER2.Is(file.Bytes))
            {
                var flver = FLVER2.Read(file.Bytes);
                var stem = Path.GetFileNameWithoutExtension(leaf);
                ConvertFlver(flver, templateFlver, stem, materials);
                files.Add(new BinderFile(file.Flags, file.ID, folder + leaf, flver.Write()));
            }
            else if (TPF.Is(file.Bytes))
            {
                files.Add(new BinderFile(file.Flags, file.ID, folder + leaf, ConvertTpf(TPF.Read(file.Bytes), oldTag, newTag).Write()));
            }
            // Anything else (DS3 cloth physics, Havok 2014) is left out; Elden Ring can't load it.
        }

        template.Files = files;
        return new ConvertedModel(template, compression, materials);
    }

    private static void ConvertFlver(FLVER2 flver, FLVER2 template, string stem, List<NewMaterial> materials)
    {
        var bboxMin = flver.Header.BoundingBoxMin;
        var bboxMax = flver.Header.BoundingBoxMax;
        flver.Header = template.Header;
        flver.Header.BoundingBoxMin = bboxMin;
        flver.Header.BoundingBoxMax = bboxMax;

        // FLVER 0x20017+ stores a skeleton set, which DS3 models (0x20014) lack. Elden Ring weapons use one bone
        // per node with the node's own hierarchy, in both the base and the full skeleton.
        flver.Skeletons = new FLVER2.SkeletonSet
        {
            BaseSkeleton = SkeletonFromNodes(flver),
            AllSkeletons = SkeletonFromNodes(flver),
        };

        // One GX list from the template (Elden Ring's single GX00 v102 entry) for every material.
        flver.GXLists = [template.GXLists[0]];

        var templateMaterial = template.Materials[0];
        var templateMatbin = MatbinName(templateMaterial.MTD);
        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var m = flver.Materials[i];
            var suffix = flver.Materials.Count > 1 ? $"_{i}" : "";
            var newMatbin = templateMatbin.Replace(TagIn(templateMatbin), stem + suffix);
            m.Name = stem + suffix;
            m.MTD = templateMaterial.MTD.Replace(TagIn(templateMatbin), stem + suffix);
            m.GXIndex = 0;
            m.Textures = templateMaterial.Textures
                .Select(t => new FLVER2.Texture(t.ParamName, "", t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C))
                .ToList();
            materials.Add(new NewMaterial(templateMatbin, newMatbin, stem));
        }

        // Elden Ring vertices carry one tangent; DS3's second tangent (and its layout member) is dropped.
        foreach (var layout in flver.BufferLayouts)
            layout.RemoveAll(m => m.Semantic == FLVER.LayoutSemantic.Tangent && m.Index > 0);
        foreach (var vertex in flver.Meshes.SelectMany(m => m.Vertices))
            if (vertex.Tangents.Count > 1)
                vertex.Tangents.RemoveRange(1, vertex.Tangents.Count - 1);

        // Elden Ring's weapon shader reads VertexColor1; some DS3 layouts have none, so add it as opaque white.
        foreach (var mesh in flver.Meshes)
        {
            var layouts = mesh.VertexBuffers.Select(b => flver.BufferLayouts[b.LayoutIndex]).ToList();
            if (layouts.Any(l => l.Any(m => m.Semantic == FLVER.LayoutSemantic.VertexColor)))
                continue;
            foreach (var layout in layouts.Distinct())
            {
                int uv = layout.FindIndex(m => m.Semantic == FLVER.LayoutSemantic.UV);
                layout.Insert(uv < 0 ? layout.Count : uv,
                    new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor, 1));
            }
            foreach (var vertex in mesh.Vertices)
            {
                // The layout's color member has index 1, so colors[0] is a placeholder for index 0.
                while (vertex.Colors.Count < 2)
                    vertex.Colors.Add(new FLVER.VertexColor(1f, 1f, 1f, 1f));
            }
        }
    }

    private static List<FLVER2.SkeletonSet.Bone> SkeletonFromNodes(FLVER2 flver) =>
        flver.Nodes.Select((n, i) => new FLVER2.SkeletonSet.Bone(i)
        {
            ParentIndex = n.ParentIndex,
            FirstChildIndex = n.FirstChildIndex,
            NextSiblingIndex = n.NextSiblingIndex,
            PreviousSiblingIndex = n.PreviousSiblingIndex,
        }).ToList();

    /// <summary>
    /// Renames textures to the new model and adds a flat, non-metallic _m map for each _r map, since Elden Ring's
    /// weapon shader reads metallic (_m) where DS3's read reflectance (_r).
    /// </summary>
    private static TPF ConvertTpf(TPF tpf, string oldTag, string newTag)
    {
        var added = new List<TPF.Texture>();
        foreach (var t in tpf.Textures)
        {
            t.Name = t.Name.Replace(oldTag, newTag, StringComparison.OrdinalIgnoreCase);
            // DS3 LOD textures end in the LOD mark ("WP_A_9000_a_l"); the converted LOD materials name them by the
            // LOD model's stem ("WP_A_9000_L" + "_a"), so move the mark in front of the map letter.
            if (t.Name.Length > 4 && t.Name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) && t.Name[^4] == '_')
                t.Name = t.Name[..^4] + "_L" + t.Name[^4..^2];
            if (t.Name.EndsWith("_r", StringComparison.OrdinalIgnoreCase))
                added.Add(new TPF.Texture(t.Name[..^2] + "_m", 103, 0, BlackBc4Dds(), TPF.TPFPlatform.PC));
        }
        tpf.Textures.AddRange(added);
        return tpf;
    }

    /// <summary>A 4x4, single-mip BC4 DDS that decodes to all zero (black).</summary>
    public static byte[] BlackBc4Dds()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("DDS "));
        w.Write(124);                          // header size
        w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); // caps | height | width | pixelformat | linearsize
        w.Write(4); w.Write(4);                // height, width
        w.Write(8);                            // linear size: one BC4 block
        w.Write(0); w.Write(1);                // depth, mip count
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32); w.Write(0x4);             // pixel format: size, FOURCC
        w.Write(Encoding.ASCII.GetBytes("DX10"));
        for (int i = 0; i < 5; i++) w.Write(0);
        w.Write(0x1000);                       // caps: texture
        for (int i = 0; i < 4; i++) w.Write(0);
        w.Write(80);                           // DXGI_FORMAT_BC4_UNORM
        w.Write(3);                            // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        w.Write(0); w.Write(1); w.Write(0);    // misc, array size, misc2
        w.Write(new byte[8]);                  // the block: both endpoints 0
        return ms.ToArray();
    }

    /// <summary>
    /// Adds a MATBIN for each new material to Elden Ring's DLC02 material binder: a copy of the template weapon's
    /// MATBIN whose own-texture samplers (albedo, normal, metallic) point at the new model's textures. Shared detail
    /// textures (parts\tex\AAT100_*) are kept.
    /// </summary>
    public static void WriteMaterials(PorterContext ctx, GameArchives er, IEnumerable<NewMaterial> materials)
    {
        // Every template MATBIN, by name, from all three binders.
        var templates = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var binder in Explore.MaterialBinders)
            foreach (var f in BND4.Read(er.Read(binder)).Files)
                templates.TryAdd(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)), f);

        const string Target = "/material/allmaterial_dlc02.matbinbnd.dcx";
        var (bytes, compression) = er.ReadWithCompression(Target);
        var bnd = BND4.Read(bytes);
        int nextId = bnd.Files.Max(f => f.ID) + 1;
        int added = 0;
        foreach (var m in materials.DistinctBy(m => m.NewMatbin, StringComparer.OrdinalIgnoreCase))
        {
            if (!templates.TryGetValue(m.TemplateMatbin, out var templateFile))
            {
                Console.Error.WriteLine($"warning: no MATBIN {m.TemplateMatbin}; {m.NewMatbin} will render untextured");
                continue;
            }
            var matbin = MATBIN.Read(templateFile.Bytes);
            var templateTag = TagIn(m.TemplateMatbin);
            foreach (var sampler in matbin.Samplers.Where(smp => smp.Path.Contains(templateTag, StringComparison.OrdinalIgnoreCase)))
            {
                var leaf = ItemText.Leaf(sampler.Path);
                var suffix = Path.GetFileNameWithoutExtension(leaf)[^2..]; // _a, _n, _m
                var folder = sampler.Path[..^leaf.Length].Replace(templateTag, ModelTag(m.TextureBase), StringComparison.OrdinalIgnoreCase);
                sampler.Path = folder + m.TextureBase + suffix + Path.GetExtension(leaf);
            }
            matbin.SourcePath = matbin.SourcePath.Replace(templateTag, TagIn(m.NewMatbin), StringComparison.OrdinalIgnoreCase);
            var name = templateFile.Name.Replace(m.TemplateMatbin, m.NewMatbin, StringComparison.OrdinalIgnoreCase);
            bnd.Files.Add(new BinderFile(templateFile.Flags, nextId++, name, matbin.Write()));
            added++;
        }
        File.WriteAllBytes(ctx.ModPath(["material", ItemText.Leaf(Target)]), DCX.Compress(bnd.Write(), compression));
        Console.WriteLine($"materials: {added} added to {ItemText.Leaf(Target)}");
    }

    // "N:\GR\...\matxml\P[WP_A_0200]_Metal.matxml" -> "P[WP_A_0200]_Metal"
    public static string MatbinName(string mtd) => Path.GetFileNameWithoutExtension(ItemText.Leaf(mtd));

    // "WP_A_9000_1_L" -> "WP_A_9000"
    private static string ModelTag(string stem) => stem.Length >= 9 ? stem[..9] : stem;

    // The model tag inside a material name: "P[WP_A_0200]_Metal" -> "WP_A_0200".
    private static string TagIn(string matbin)
    {
        int open = matbin.IndexOf('['), close = matbin.IndexOf(']');
        return open >= 0 && close > open ? matbin[(open + 1)..close] : matbin;
    }
}
