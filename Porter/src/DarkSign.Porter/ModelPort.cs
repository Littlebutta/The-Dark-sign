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

    /// <summary>
    /// A material the converted model needs: a copy of a template MATBIN pointed at the textures (TPF names) the
    /// DS3 material used.
    /// </summary>
    public sealed record NewMaterial(string TemplateMatbin, string NewMatbin, string Albedo, string Normal, string Metallic,
        string? Emissive = null);

    /// <summary>
    /// An Elden Ring weapon material that glows (its MATBIN samples an emissive map of the weapon's own), with the
    /// FLVER material and GX list its weapon uses for it. DS3 materials that glow (Friede's frozen blade, _em maps)
    /// get a copy of it, since the plain template material has no emissive slot and drops the glow.
    /// </summary>
    public sealed record EmissiveTemplate(string Matbin, FLVER2.Material Material, FLVER2.GXList GX);

    private static readonly object EmissiveLock = new();
    private static EmissiveTemplate? _emissive;
    private static bool _emissiveSearched;

    /// <summary>
    /// Finds the emissive template once per run (DARKSIGN_EMISSIVE=0 turns glowing materials off).
    /// </summary>
    public static EmissiveTemplate? Emissive(GameArchives er)
    {
        lock (EmissiveLock)
        {
            if (_emissiveSearched)
                return _emissive;
            _emissiveSearched = true;
            if (Environment.GetEnvironmentVariable("DARKSIGN_EMISSIVE") is "0" or "off")
                return null;
            var candidates = new List<(string Name, string Tag, int Score)>();
            foreach (var binder in Explore.MaterialBinders)
                foreach (var f in BND4.Read(er.Read(binder)).Files)
                {
                    var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name));
                    if (!name.StartsWith("P[WP_A_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var tag = TagIn(name);
                    if (tag.Length != 9)
                        continue;
                    var own = MATBIN.Read(f.Bytes).Samplers
                        .Where(smp => smp.Path.Contains(tag[..9], StringComparison.OrdinalIgnoreCase)).Select(smp => smp.Type).ToList();
                    bool Has(string kind) => own.Any(t => t.Contains(kind, StringComparison.OrdinalIgnoreCase));
                    // Only the slots the port fills may point at the weapon's own textures.
                    if (Has("Emissive") && Has("Albedo") && Has("Normal")
                        && own.All(t => SlotKind(t) is not null))
                    {
                        // Prefer Elden Ring's plain glowing weapon shader (C[AMSN]_Glow[Int], 2026-11-08 survey: 41
                        // candidates, mostly this one) with a metallic map of its own, over rune/crystal/detail shaders.
                        var shader = Path.GetFileNameWithoutExtension(MATBIN.Read(f.Bytes).ShaderPath);
                        int score = (shader.Equals("C[AMSN]_Glow[Int]", StringComparison.OrdinalIgnoreCase) ? 2 : 0) + (Has("Metallic") ? 1 : 0);
                        candidates.Add((name, tag[..9], score));
                    }
                }
            foreach (var (name, tag, _) in candidates.DistinctBy(c => c.Name).OrderByDescending(c => c.Score).ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                var path = PartsPath(int.Parse(tag[5..]));
                if (!er.Contains(path))
                    continue;
                foreach (var f in BND4.Read(er.Read(path)).Files.Where(f => FLVER2.Is(f.Bytes)))
                {
                    var flver = FLVER2.Read(f.Bytes);
                    var mat = flver.Materials.FirstOrDefault(m => MatbinName(m.MTD).Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (mat is null || mat.GXIndex < 0 || mat.GXIndex >= flver.GXLists.Count)
                        continue;
                    _emissive = new EmissiveTemplate(name, mat, flver.GXLists[mat.GXIndex]);
                    Console.WriteLine($"models: glowing DS3 materials use Elden Ring's {name} ({candidates.Count} candidates)");
                    return _emissive;
                }
            }
            Console.WriteLine("models: no Elden Ring emissive weapon material found; glowing DS3 materials stay plain");
            return null;
        }
    }

    /// <summary>Which of the port's textures a MATBIN sampler type takes, or null if none.</summary>
    public static string? SlotKind(string samplerType) =>
        samplerType.Contains("Emissive", StringComparison.OrdinalIgnoreCase) ? "emissive"
        : samplerType.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ? "albedo"
        : samplerType.Contains("Normal", StringComparison.OrdinalIgnoreCase) ? "normal"
        : samplerType.Contains("Metallic", StringComparison.OrdinalIgnoreCase) ? "metallic"
        : null;

    public static string PartsPath(int modelId, bool lod = false) =>
        $"/parts/wp_a_{modelId:D4}{(lod ? "_l" : "")}.partsbnd.dcx";

    public static ConvertedModel? Convert(GameArchives ds3, GameArchives er, int ds3Model, int templateModel, int newModel, bool lod,
        System.Numerics.Matrix4x4? holdTurn = null)
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
        // Parts DS3 hides with each model's idle animation (the Black Blade's scabbard), which can't come along: see
        // WeaponHiddenParts. A LOD binder has no anibnd of its own; the main binder's says what to hide.
        var hiddenByPart = new Dictionary<int, HashSet<string>>();
        BND4? mainParts = null;
        HashSet<string> HiddenBones(int part)
        {
            if (hiddenByPart.TryGetValue(part, out var known))
                return known;
            var bones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (WeaponHiddenParts.Enabled)
            {
                IBinder parts = source;
                if (WeaponHiddenParts.AnibndFor(source, part) is null && lod && ds3.Contains(PartsPath(ds3Model)))
                    parts = mainParts ??= BND4.Read(ds3.Read(PartsPath(ds3Model)));
                bones = WeaponHiddenParts.IdleHiddenBones(parts, part, out var note);
                if (note.Length > 0)
                    Console.WriteLine($"models: {oldTag}{(lod ? " LOD" : "")} model {part}: hidden parts not read: {note}");
            }
            return hiddenByPart[part] = bones;
        }
        foreach (var file in source.Files)
        {
            var leaf = ItemText.Leaf(file.Name).Replace(oldTag, newTag, StringComparison.OrdinalIgnoreCase);
            if (FLVER2.Is(file.Bytes))
            {
                var flver = FLVER2.Read(file.Bytes);
                var stem = Path.GetFileNameWithoutExtension(leaf);
                var hiddenMeshes = new List<int>();
                var hiddenBones = HiddenBones(WeaponHiddenParts.PartOf(file.Name));
                if (WeaponHiddenParts.Collapse(flver, hiddenBones, hiddenMeshes) is > 0 and var moved)
                    Console.WriteLine($"models: {oldTag} -> {newTag}{(lod ? " LOD" : "")} {ItemText.Leaf(file.Name)}: hid {moved} vertices in meshes " +
                                      $"{string.Join(", ", hiddenMeshes)} on bones DS3's idle animation hides ({string.Join(", ", hiddenBones.Order())})");
                ConvertFlver(flver, templateFlver, stem, oldTag, newTag, materials, Emissive(er), holdTurn);
                FlverMaterialNumbers.Apply(flver);
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

    private static void ConvertFlver(FLVER2 flver, FLVER2 template, string stem, string oldTag, string newTag,
        List<NewMaterial> materials, EmissiveTemplate? emissive, System.Numerics.Matrix4x4? holdTurn = null)
    {
        var bboxMin = flver.Header.BoundingBoxMin;
        var bboxMax = flver.Header.BoundingBoxMax;
        flver.Header = template.Header;
        flver.Header.BoundingBoxMin = bboxMin;
        flver.Header.BoundingBoxMax = bboxMax;

        // Node transforms baked into the meshes, unique node names (WeaponNodes).
        if (WeaponNodes.Bake(flver) is { } baked)
            Console.WriteLine($"models: {stem}: {baked}");
        if (WeaponNodes.UniqueNames(flver) is > 0 and var renamed)
            Console.WriteLine($"models: {stem}: renamed {renamed} nodes whose names repeated ignoring case");
        // Shields: turned from DS3's hold frame onto Elden Ring's (WeaponHold).
        if (holdTurn is { } turn && !WeaponHold.Apply(flver, turn))
            Console.WriteLine($"models: {stem}: shield hold turn skipped (its nodes still place the meshes)");

        // FLVER 0x20017+ stores a skeleton set, which DS3 models (0x20014) lack. Elden Ring weapons use one bone
        // per node with the node's own hierarchy, in both the base and the full skeleton.
        flver.Skeletons = new FLVER2.SkeletonSet
        {
            BaseSkeleton = SkeletonFromNodes(flver),
            AllSkeletons = SkeletonFromNodes(flver),
        };

        // One GX list from the template (Elden Ring's single GX00 v102 entry) for every material.
        flver.GXLists = [template.GXLists[0]];

        SetNodeFlags(flver);

        var templateMaterial = template.Materials[0];
        var templateMatbin = MatbinName(templateMaterial.MTD);
        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var m = flver.Materials[i];
            // "_mat1", not "_1": a second material must not share a name with a model's "_1" part.
            var suffix = flver.Materials.Count > 1 ? $"_mat{i}" : "";
            var newMatbin = templateMatbin.Replace(TagIn(templateMatbin), stem + suffix);

            // The textures this DS3 material really uses (often the main model's, even on a "_1" part).
            // Plain materials name slots g_DiffuseTexture etc.; emissive (_em) ones use SAT_..._AlbedoMap_0 etc.
            string? Ds3Texture(string classic, string sat) => m.Textures.FirstOrDefault(t => t.Path.Length > 0
                    && (t.ParamName == classic || t.ParamName.EndsWith(sat, StringComparison.Ordinal))) is { } t
                ? TextureName(Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path)), oldTag, newTag)
                : null;
            var albedo = Ds3Texture("g_DiffuseTexture", "_AlbedoMap_0") ?? stem + "_a";
            var normal = Ds3Texture("g_BumpmapTexture", "_NormalMap_0") ?? stem + "_n";
            var specular = Ds3Texture("g_SpecularTexture", "_ReflectanceMap_0");
            var metallic = specular is not null && specular.EndsWith("_r", StringComparison.OrdinalIgnoreCase)
                ? specular[..^2] + "_m"
                : stem + "_m";

            // A DS3 glow map of the weapon's own (not a SYSTEX dummy) means the material glows.
            var glow = m.Textures.Where(t => t.Path.Length > 0 && t.ParamName.Contains("Emissive", StringComparison.OrdinalIgnoreCase))
                .Select(t => Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path)))
                .FirstOrDefault(n => !n.StartsWith("SYSTEX", StringComparison.OrdinalIgnoreCase));

            m.Name = stem + suffix;
            m.GXIndex = 0;
            var source = templateMaterial;
            var sourceMatbin = templateMatbin;
            if (glow is not null && emissive is not null)
            {
                source = emissive.Material;
                sourceMatbin = emissive.Matbin;
                newMatbin = sourceMatbin.Replace(TagIn(sourceMatbin), stem + suffix);
                if (flver.GXLists.Count == 1)
                    flver.GXLists.Add(emissive.GX);
                m.GXIndex = 1;
            }
            m.MTD = source.MTD.Replace(TagIn(sourceMatbin), stem + suffix);
            m.Textures = source.Textures
                .Select(t => new FLVER2.Texture(t.ParamName, "", t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C))
                .ToList();
            materials.Add(new NewMaterial(sourceMatbin, newMatbin, albedo, normal, metallic,
                glow is null ? null : TextureName(glow, oldTag, newTag)));
        }

        // Elden Ring vertices carry one tangent; DS3's second tangent (and its layout member) is dropped.
        foreach (var layout in flver.BufferLayouts)
            layout.RemoveAll(m => m.Semantic == FLVER.LayoutSemantic.Tangent && m.Index > 0);
        foreach (var vertex in flver.Meshes.SelectMany(m => m.Vertices))
            if (vertex.Tangents.Count > 1)
                vertex.Tangents.RemoveRange(1, vertex.Tangents.Count - 1);

        // Elden Ring's weapon shader reads VertexColor1; some DS3 layouts have none. Meshes can share layouts, so
        // first add the member once to the first layout of each color-less mesh, then pad the colors of every
        // vertex whose layouts now carry more color members than it has.
        var changed = new HashSet<FLVER2.BufferLayout>();
        foreach (var mesh in flver.Meshes)
        {
            var layouts = mesh.VertexBuffers.Select(b => flver.BufferLayouts[b.LayoutIndex]).ToList();
            if (layouts.Count == 0 || layouts.Any(l => l.Any(m => m.Semantic == FLVER.LayoutSemantic.VertexColor)))
                continue;
            var first = layouts[0];
            int uv = first.FindIndex(m => m.Semantic == FLVER.LayoutSemantic.UV);
            first.Insert(uv < 0 ? first.Count : uv,
                new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor, 1));
            changed.Add(first);
        }
        foreach (var mesh in flver.Meshes)
        {
            var layouts = mesh.VertexBuffers.Select(b => flver.BufferLayouts[b.LayoutIndex]).ToList();
            if (!layouts.Any(changed.Contains))
                continue;
            int colors = layouts.Sum(l => l.Count(m => m.Semantic == FLVER.LayoutSemantic.VertexColor));
            foreach (var vertex in mesh.Vertices)
                while (vertex.Colors.Count < colors)
                    vertex.Colors.Add(new FLVER.VertexColor(1f, 1f, 1f, 1f));
        }

        // Culling boxes in Elden Ring's form, from the (baked) vertices.
        WeaponNodes.SetBounds(flver);

        // Elden Ring's weapon shader blends its detail layers (rust, bone, leather...) by vertex colour, and its own
        // weapons all carry one colour (1,0,0,1). DS3's vertex colours mean something else (most are (1,0,0.41,1)),
        // which mixed a detail layer over the DS3 texture (Friede's scythe came out rust grey). Every ported vertex
        // takes the template weapon's colour (DARKSIGN_VCOLOR=keep keeps DS3's).
        if (Environment.GetEnvironmentVariable("DARKSIGN_VCOLOR") is not ("keep" or "0")
            && template.Meshes.SelectMany(m => m.Vertices).FirstOrDefault(v => v.Colors.Count > 0) is { } tv)
        {
            var color = tv.Colors[0];
            foreach (var vertex in flver.Meshes.SelectMany(m => m.Vertices))
                for (int c = 0; c < vertex.Colors.Count; c++)
                    vertex.Colors[c] = new FLVER.VertexColor(color.A, color.R, color.G, color.B);
        }
    }

    /// <summary>
    /// Elden Ring's FLVER (0x2001A) flags every node with its role: DummyOwner on the nodes dummy polys hang on
    /// (Dummy_Plan, Dummy_SFX), Mesh and Bone on the node the mesh is bound to (its own weapon models: WP_A_0612's
    /// "WP_A_0612" is Mesh | Bone, its dummy nodes DummyOwner). DS3's FLVER (0x20014) left the field 0 on every node
    /// but a Disabled one, and the converted models kept 0, so the game had no owner for the hit spheres (dummy
    /// polys 100-140) and weapon effects: ported weapons hit nothing and their hit effects played off the weapon.
    /// DARKSIGN_NODE_FLAGS=0 keeps DS3's.
    /// </summary>
    internal static void SetNodeFlags(FLVER2 flver)
    {
        if (Environment.GetEnvironmentVariable("DARKSIGN_NODE_FLAGS") is "0" or "off")
            return;
        var dummyOwners = flver.Dummies.SelectMany(d => new[] { (int)d.ParentBoneIndex, (int)d.AttachBoneIndex }).Where(i => i >= 0).ToHashSet();
        var meshNodes = flver.Meshes.Select(m => m.NodeIndex).Where(i => i >= 0).ToHashSet();
        var bones = flver.Meshes.SelectMany(m => m.BoneIndices).Where(i => i >= 0).ToHashSet();
        for (int i = 0; i < flver.Nodes.Count; i++)
        {
            var node = flver.Nodes[i];
            if (node.Flags.HasFlag(FLVER.Node.NodeFlags.Disabled))
                continue;
            var flags = (FLVER.Node.NodeFlags)0;
            if (dummyOwners.Contains(i))
                flags |= FLVER.Node.NodeFlags.DummyOwner;
            if (meshNodes.Contains(i))
                flags |= FLVER.Node.NodeFlags.Mesh | FLVER.Node.NodeFlags.Bone;
            if (bones.Contains(i))
                flags |= FLVER.Node.NodeFlags.Bone;
            node.Flags = flags;
        }
    }

    internal static List<FLVER2.SkeletonSet.Bone> SkeletonFromNodes(FLVER2 flver) =>
        flver.Nodes.Select((n, i) => new FLVER2.SkeletonSet.Bone(i)
        {
            ParentIndex = n.ParentIndex,
            FirstChildIndex = n.FirstChildIndex,
            NextSiblingIndex = n.NextSiblingIndex,
            PreviousSiblingIndex = n.PreviousSiblingIndex,
        }).ToList();

    /// <summary>
    /// Renames textures to the new model and adds an _m (metallic) map for each _r (reflectance) map, since Elden
    /// Ring's weapon shader reads metallic where DS3's read reflectance (see <see cref="WeaponTextures"/>).
    /// </summary>
    private static TPF ConvertTpf(TPF tpf, string oldTag, string newTag)
    {
        foreach (var t in tpf.Textures)
            t.Name = TextureName(t.Name, oldTag, newTag);
        WeaponTextures.AddMetallicMaps(tpf, WeaponTextures.CurrentMode);
        return tpf;
    }

    /// <summary>
    /// A DS3 texture name under the new model: "WP_A_0201_a" -> "WP_A_9020_a". DS3 LOD textures end in the LOD mark
    /// ("WP_A_0201_a_l"); it moves in front of the map letter ("WP_A_9020_L_a") to match the LOD model's stem.
    /// </summary>
    public static string TextureName(string ds3Name, string oldTag, string newTag)
    {
        var name = ds3Name.Replace(oldTag, newTag, StringComparison.OrdinalIgnoreCase);
        if (name.Length > 4 && name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) && name[^4] == '_')
            name = name[..^4] + "_L" + name[^4..^2];
        return name;
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

    /// <summary>A 4x4 DXT1 texture of one colour, (128, 128, 128): a normal map that bends nothing, mid roughness.</summary>
    public static byte[] FlatNormalDds() => SolidDxt1(0x8410);

    /// <summary>A 4x4 DXT1 texture of one colour (R5G6B5).</summary>
    public static byte[] SolidDxt1(ushort rgb565)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("DDS "));
        w.Write(124);                          // header size
        w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); // caps | height | width | pixelformat | linearsize
        w.Write(4); w.Write(4);                // height, width
        w.Write(8);                            // linear size: one DXT1 block
        w.Write(0); w.Write(1);                // depth, mip count
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32); w.Write(0x4);             // pixel format: size, FOURCC
        w.Write(Encoding.ASCII.GetBytes("DXT1"));
        for (int i = 0; i < 5; i++) w.Write(0);
        w.Write(0x1000);                       // caps: texture
        for (int i = 0; i < 4; i++) w.Write(0);
        w.Write(rgb565); w.Write(rgb565);      // both endpoints the colour (0x8410 = R5G6B5 16, 32, 16)
        w.Write(0);                            // every pixel the first endpoint
        return ms.ToArray();
    }

    /// <summary>
    /// Adds a MATBIN for each new material to Elden Ring's DLC02 material binder: a copy of the template weapon's
    /// MATBIN whose own-texture samplers (albedo, normal, metallic) point at the new model's textures. Shared detail
    /// textures (parts\tex\AAT100_*) are kept.
    /// </summary>
    public static void WriteMaterials(PorterContext ctx, GameArchives er, IEnumerable<NewMaterial> materials)
    {
        const string Target = "/material/allmaterial_dlc02.matbinbnd.dcx";
        var (bytes, compression) = er.ReadWithCompression(Target);
        var bnd = BND4.Read(bytes);
        int nextId = bnd.Files.Max(f => f.ID) + 1;
        var built = BuildMaterials(er, materials);
        foreach (var (file, _) in built)
        {
            file.ID = nextId++;
            bnd.Files.Add(file);
        }
        File.WriteAllBytes(ctx.ModPath(["material", ItemText.Leaf(Target)]), DCX.Compress(bnd.Write(), compression));
        Console.WriteLine($"materials: {built.Count} added to {ItemText.Leaf(Target)}");
    }

    /// <summary>The new MATBINs (binder files without IDs yet, and the MATBIN itself) for the ported materials.</summary>
    public static List<(BinderFile File, MATBIN Matbin)> BuildMaterials(GameArchives er, IEnumerable<NewMaterial> materials)
    {
        // Every template MATBIN, by name, from all three binders.
        var templates = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var binder in Explore.MaterialBinders)
            foreach (var f in BND4.Read(er.Read(binder)).Files)
                templates.TryAdd(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)), f);

        var built = new List<(BinderFile, MATBIN)>();
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
                var texture = SlotKind(sampler.Type) switch
                {
                    "emissive" => m.Emissive,
                    "albedo" => m.Albedo,
                    "normal" => m.Normal,
                    "metallic" => m.Metallic,
                    _ => null,
                };
                if (texture is null)
                    continue;
                var leaf = ItemText.Leaf(sampler.Path);
                var folder = sampler.Path[..^leaf.Length].Replace(templateTag, ModelTag(texture), StringComparison.OrdinalIgnoreCase);
                sampler.Path = folder + texture + Path.GetExtension(leaf);
            }
            matbin.SourcePath = matbin.SourcePath.Replace(templateTag, TagIn(m.NewMatbin), StringComparison.OrdinalIgnoreCase);
            var name = templateFile.Name.Replace(m.TemplateMatbin, m.NewMatbin, StringComparison.OrdinalIgnoreCase);
            built.Add((new BinderFile(templateFile.Flags, 0, name, matbin.Write()), matbin));
        }
        return built;
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
