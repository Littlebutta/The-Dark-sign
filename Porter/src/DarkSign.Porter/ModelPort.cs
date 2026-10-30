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
    public sealed record ConvertedModel(BND4 Binder, List<NewMaterial> Materials);

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
        var template = BND4.Read(er.Read(erPath));
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
        return new ConvertedModel(template, materials);
    }

    private static void ConvertFlver(FLVER2 flver, FLVER2 template, string stem, List<NewMaterial> materials)
    {
        var bboxMin = flver.Header.BoundingBoxMin;
        var bboxMax = flver.Header.BoundingBoxMax;
        flver.Header = template.Header;
        flver.Header.BoundingBoxMin = bboxMin;
        flver.Header.BoundingBoxMax = bboxMax;

        // One GX list from the template (Elden Ring's single GX00 v102 entry) for every material.
        flver.GXLists = [template.GXLists[0]];

        var templateMaterial = template.Materials[0];
        var templateMatbin = MatbinName(templateMaterial.MTD);
        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var m = flver.Materials[i];
            var suffix = flver.Materials.Count > 1 ? $"_{i}" : "";
            var newMatbin = templateMatbin.Replace(TagIn(templateMatbin), stem + suffix);
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
    }

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

    // "N:\GR\...\matxml\P[WP_A_0200]_Metal.matxml" -> "P[WP_A_0200]_Metal"
    public static string MatbinName(string mtd) => Path.GetFileNameWithoutExtension(ItemText.Leaf(mtd));

    // The model tag inside a material name: "P[WP_A_0200]_Metal" -> "WP_A_0200".
    private static string TagIn(string matbin)
    {
        int open = matbin.IndexOf('['), close = matbin.IndexOf(']');
        return open >= 0 && close > open ? matbin[(open + 1)..close] : matbin;
    }
}
