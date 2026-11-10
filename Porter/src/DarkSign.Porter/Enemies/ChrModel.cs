using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// A DS3 character model in Elden Ring's FLVER layout, on its Elden Ring template: Elden Ring's header, GX list and
/// skeleton set, DS3's geometry and bones (whose bind pose the ported skeleton shares, see ChrPort), skin indices
/// into the node list instead of DS3's per-mesh palettes, one tangent, a vertex colour where Elden Ring's shaders
/// read one, Elden Ring's node flags and per-mesh culling boxes.
///
/// Materials (launcher Fallbacks "enemy-model"): by default every DS3 material becomes a plain Elden Ring character
/// material (C[AMSN].spx: albedo, metallic, normal) with DS3's textures. DARKSIGN_ENEMY_MATERIALS=template copies the
/// template's matching material instead, as builds up to 1.0.255 did; that drew DS3 knights with the template's
/// own multi-layer shader (a beast skeleton's bone and mud detail layers). DARKSIGN_ENEMY_NODES=0 keeps DS3's node
/// flags and mesh boxes.
/// </summary>
public static class ChrModel
{
    /// <summary>False with DARKSIGN_ENEMY_MATERIALS=template: materials are copies of the template's.</summary>
    public static bool PlainMaterials => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_MATERIALS")?.Trim().ToLowerInvariant() != "template";

    /// <summary>False with DARKSIGN_ENEMY_NODES=0: DS3's node flags and mesh boxes stay.</summary>
    public static bool ErNodes => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_NODES")?.Trim() != "0";

    /// <summary>The samplers of Elden Ring's plain character shader, as its FLVERs and MATBINs name them.</summary>
    public const string PlainAlbedo = "C_AMSN__snp_Texture2D_2_AlbedoMap_0",
        PlainMetallic = "C_AMSN__snp_Texture2D_0_MetallicMap_0",
        PlainNormal = "C_AMSN__snp_Texture2D_7_NormalMap_4";

    public static List<ChrMaterial> Convert(FLVER2 flver, FLVER2 template, int ds3Chr, int newChr) =>
        Convert(flver, template, ds3Chr, newChr, PlainMaterials, ErNodes);

    public static List<ChrMaterial> Convert(FLVER2 flver, FLVER2 template, int ds3Chr, int newChr, bool plainMaterials, bool erNodes)
    {
        if (!ChrPort.HasBody(template) || flver.Materials.Count == 0)
            throw new InvalidDataException("the template or the DS3 model has nothing to draw");
        var bboxMin = flver.Header.BoundingBoxMin;
        var bboxMax = flver.Header.BoundingBoxMax;
        flver.Header = template.Header;
        flver.Header.BoundingBoxMin = bboxMin;
        flver.Header.BoundingBoxMax = bboxMax;

        ChrPort.GlobalBoneIndices(flver);
        if (erNodes)
        {
            SetNodeFlags(flver);
            foreach (var mesh in flver.Meshes)
                Maps.MapModelPort.SetBoundingBox(mesh, flver.Header.Version);
        }
        flver.Skeletons = new FLVER2.SkeletonSet { BaseSkeleton = SkeletonFromNodes(flver), AllSkeletons = SkeletonFromNodes(flver) };
        flver.GXLists = [template.GXLists[0]];

        var materials = new List<ChrMaterial>();
        var plain = new Dictionary<(string?, string?, string?, bool), string>();
        string oldTag = $"c{ds3Chr:D4}", newTag = $"c{newChr:D4}";
        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var m = flver.Materials[i];
            string? Ds3Texture(params string[] names) => m.Textures.FirstOrDefault(t => t.Path.Length > 0
                    && names.Any(n => t.ParamName.Equals(n, StringComparison.Ordinal) || t.ParamName.EndsWith(n, StringComparison.Ordinal))) is { } t
                ? TextureName(Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path)), oldTag, newTag)
                : null;
            var albedo = Ds3Texture("g_DiffuseTexture", "_AlbedoMap_0");
            var normal = Ds3Texture("g_BumpmapTexture", "_NormalMap_0");
            var specular = Ds3Texture("g_SpecularTexture", "_ReflectanceMap_0");
            var metallic = specular is not null && specular.EndsWith("_r", StringComparison.OrdinalIgnoreCase) ? specular[..^2] + "_m" : null;
            bool alphaTest = IsAlphaTested(m.MTD);
            // "#01#": the display mask the mesh belongs to (NpcParam modelDispMask1), the same in both games.
            m.Name = $"{MaskPrefix(m.Name)}{newTag}_{i}";
            m.GXIndex = 0;
            if (plainMaterials)
            {
                // Meshes with the same textures share one material file.
                var key = (albedo, normal, metallic, alphaTest);
                if (!plain.TryGetValue(key, out var name))
                {
                    plain[key] = name = $"C[{newTag}]_ds3_{plain.Count}";
                    materials.Add(new ChrMaterial(ChrMaterial.PlainSource, name, albedo, normal, metallic)
                        { Plain = true, AlphaTest = alphaTest, TextureFolder = $@"N:\GR\data\Model\chr\{newTag}\tex\" });
                }
                m.MTD = $@"N:\GR\data\Material\mtd\Chr\matxml\{name}.matxml";
                m.Textures = new[] { PlainAlbedo, PlainMetallic, PlainNormal }
                    .Select(n => new FLVER2.Texture(n, "", System.Numerics.Vector2.One, 0, 0, 0, 0, 0)).ToList();
                continue;
            }
            // The template material whose name shares the most with this one (body, cloth, hair...), else its first.
            var templateMaterial = template.Materials.MaxBy(t => SharedWords(t.Name, m.Name)) ?? template.Materials[0];
            var templateMatbin = ModelPort.MatbinName(templateMaterial.MTD);
            var newMatbin = $"{templateMatbin}_{newTag}_{i}";
            m.MTD = templateMaterial.MTD.Replace(templateMatbin, newMatbin, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>The "#NN#" a material name starts with (its display mask), or "".</summary>
    public static string MaskPrefix(string materialName) =>
        System.Text.RegularExpressions.Regex.Match(materialName, @"^\s*(#\d+#)").Groups[1].Value;

    /// <summary>
    /// A DS3 material file that cuts out by alpha: "_e" (edge), "_Edge", "_Alp" or "_al" after the shader's name
    /// ("C[ARSN]_e_Decal.mtd", hair, fur, cloth edges). Elden Ring's plain shader cuts out when its g_AlphaRef is set.
    /// </summary>
    public static bool IsAlphaTested(string mtd)
    {
        var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(mtd));
        int close = name.IndexOf(']');
        return name[(close < 0 ? 0 : close + 1)..].Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Any(w => w.ToLowerInvariant() is "e" or "edge" or "alp" or "al");
    }

    /// <summary>
    /// Node flags as Elden Ring's character models carry them (read off vanilla c2100, c3060, c4300; DS3 leaves them
    /// 0): a node vertices are skinned to is a Bone (8), a node a dummy point hangs on a DummyOwner (2, both = 10),
    /// a mesh's own node a Mesh (4), and what nothing uses is Disabled (1). A node that only carries used nodes
    /// below it counts as a Bone. Ported weapons with flags 0 took no part in hits and showed their dummy points at
    /// the origin (ModelPort.SetNodeFlags); characters had the same flags until 1.0.255.
    /// </summary>
    public static void SetNodeFlags(FLVER2 flver)
    {
        var flags = new FLVER.Node.NodeFlags[flver.Nodes.Count];
        void Add(int node, FLVER.Node.NodeFlags flag)
        {
            if (node >= 0 && node < flags.Length)
                flags[node] |= flag;
        }
        foreach (var mesh in flver.Meshes)
        {
            Add(mesh.NodeIndex, FLVER.Node.NodeFlags.Mesh);
            foreach (var palette in mesh.BoneIndices)
                Add(palette, FLVER.Node.NodeFlags.Bone);
            foreach (var v in mesh.Vertices)
                for (int i = 0; i < 4; i++)
                    if (i == 0 || v.BoneWeights[i] > 0)
                        Add(v.BoneIndices[i], FLVER.Node.NodeFlags.Bone);
        }
        foreach (var dummy in flver.Dummies)
        {
            Add(dummy.ParentBoneIndex, FLVER.Node.NodeFlags.DummyOwner);
            Add(dummy.AttachBoneIndex, FLVER.Node.NodeFlags.DummyOwner);
        }
        for (int i = 0; i < flags.Length; i++)
        {
            if (flags[i] == 0)
                continue;
            for (int p = flver.Nodes[i].ParentIndex, guard = 0; p >= 0 && p < flags.Length && guard < flags.Length; p = flver.Nodes[p].ParentIndex, guard++)
                if (flags[p] == 0)
                    flags[p] = FLVER.Node.NodeFlags.Bone;
        }
        for (int i = 0; i < flags.Length; i++)
            flver.Nodes[i].Flags = flags[i] == 0 ? FLVER.Node.NodeFlags.Disabled : flags[i];
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
/// A material a ported character needs: a copy of an Elden Ring MATBIN whose albedo, normal and metallic samplers
/// name DS3's textures. Plain: the copy is of a plain character material (C[AMSN].spx) with neutral values and all
/// three samplers set (none = no texture). Otherwise it is the template's material, where null keeps the template's
/// texture for that sampler and shared detail maps stay.
/// </summary>
public sealed record ChrMaterial(string TemplateMatbin, string NewMatbin, string? Albedo, string? Normal, string? Metallic)
{
    private const string Target = "/material/allmaterial_dlc02.matbinbnd.dcx";

    /// <summary>The vanilla material plain ones are copied from (Torrent's body: C[AMSN].spx, three own textures, neutral values).</summary>
    public const string PlainSource = "C[c3250]_Horse_Body";
    private const string PlainShader = @"\C[AMSN].spx";

    public bool Plain { get; init; }
    public bool AlphaTest { get; init; }
    /// <summary>Where a plain material's samplers say its textures are (the game finds them by file name).</summary>
    public string TextureFolder { get; init; } = "";

    /// <summary>The new material, made from <paramref name="matbin"/> (changed in place), and how many samplers now name a DS3 texture.</summary>
    public int Apply(MATBIN matbin)
    {
        int swapped = 0;
        foreach (var sampler in matbin.Samplers)
        {
            var texture = sampler.Type.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ? Albedo
                : sampler.Type.Contains("Normal", StringComparison.OrdinalIgnoreCase) ? Normal
                : sampler.Type.Contains("Metallic", StringComparison.OrdinalIgnoreCase) ? Metallic
                : null;
            if (Plain)
            {
                sampler.Path = texture is null ? "" : $"{TextureFolder}{texture}.tif";
                swapped += texture is null ? 0 : 1;
                continue;
            }
            // Only the template's own textures are swapped; shared detail maps (Model\parts\tex) stay. Until 1.0.255
            // this skipped every path with "\tex\" in it, which is all of them: ported enemies wore their template's textures.
            if (texture is null || sampler.Path.Length == 0 || !IsOwnTexture(sampler.Path))
                continue;
            var leaf = ItemText.Leaf(sampler.Path);
            sampler.Path = sampler.Path[..^leaf.Length] + texture + Path.GetExtension(leaf);
            swapped++;
        }
        if (Plain)
            foreach (var p in matbin.Params)
                switch (p.Name)
                {
                    case "g_AlphaRef": p.Value = new[] { AlphaTest ? 50 : 0, 0 }; break;
                    case "g_BlendMode": p.Value = 0; break;
                    case "C_AMSN__snp_0_color_0": p.Value = new[] { 1f, 1f, 1f, 1f, 1f }; break;
                    case "C_AMSN__snp_0_float_0" or "C_AMSN__snp_0_float_1": p.Value = 0f; break;
                    case "group_1_CommonUV-UVParam": p.Value = new[] { 1f, 1f }; break;
                }
        matbin.SourcePath = matbin.SourcePath.Replace(ModelPort.MatbinName(matbin.SourcePath), NewMatbin, StringComparison.OrdinalIgnoreCase);
        return swapped;
    }

    /// <summary>A character's own texture (Model\chr\cNNNN\tex), not a shared detail map (Model\parts\tex).</summary>
    public static bool IsOwnTexture(string samplerPath) =>
        !samplerPath.Contains(@"\parts\tex\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds the materials to the DLC02 material binder, starting from the one port-weapons already wrote to the mod
    /// folder (its weapon materials stay; a material of the same name from an earlier build is replaced), and returns
    /// the file written.
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
        // Vanilla binders hold a few names twice (C[Ghost]); the first one counts.
        var existing = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in bnd.Files)
            existing.TryAdd(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)), f);
        int nextId = bnd.Files.Max(f => f.ID) + 1, added = 0, replaced = 0, missing = 0, plain = 0, textures = 0;
        BinderFile? plainSource = null;
        foreach (var m in materials.DistinctBy(m => m.NewMatbin, StringComparer.OrdinalIgnoreCase))
        {
            if (!templates.TryGetValue(m.TemplateMatbin, out var templateFile) && m.Plain)
                // Not this game version's name for it: any plain character material will do, its values are reset.
                templateFile = plainSource ??= templates.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Value)
                    .FirstOrDefault(f => MATBIN.Read(f.Bytes) is var mb && mb.ShaderPath.EndsWith(PlainShader, StringComparison.OrdinalIgnoreCase) && mb.Samplers.Count == 3);
            if (templateFile is null)
            {
                missing++;
                continue;
            }
            var matbin = MATBIN.Read(templateFile.Bytes);
            textures += m.Apply(matbin);
            plain += m.Plain ? 1 : 0;
            var sourceName = Path.GetFileNameWithoutExtension(ItemText.Leaf(templateFile.Name));
            var name = templateFile.Name.Replace(sourceName, m.NewMatbin, StringComparison.OrdinalIgnoreCase);
            if (existing.TryGetValue(m.NewMatbin, out var old))
            {
                old.Bytes = matbin.Write();
                replaced++;
                continue;
            }
            var file = new BinderFile(templateFile.Flags, nextId++, name, matbin.Write());
            bnd.Files.Add(file);
            existing[m.NewMatbin] = file;
            added++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
        File.WriteAllBytes(modPath, DCX.Compress(bnd.Write(), compression));
        Console.WriteLine($"enemies: materials: {added} added to {ItemText.Leaf(Target)}, {replaced} replaced; {plain} plain character materials, {textures} DS3 textures named"
            + (missing > 0 ? $"; FAIL: {missing} source materials not found, those meshes will not draw right" : ""));
        return modPath;
    }
}
