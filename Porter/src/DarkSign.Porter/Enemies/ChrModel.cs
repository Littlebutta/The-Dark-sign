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

    /// <summary>
    /// False with DARKSIGN_ENEMY_COLOURS=ds3: the model keeps DS3's vertex colours (and white where DS3 has none),
    /// and a two-layer material shows its first layer, as builds up to 1.0.299 did.
    /// </summary>
    public static bool ErVertexColours => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_COLOURS")?.Trim().ToLowerInvariant() != "ds3";

    /// <summary>From the last Convert on this thread: vertex colours changed to Elden Ring's, materials shown by their second layer.</summary>
    [ThreadStatic] public static int VertexColoursSet;
    [ThreadStatic] public static int SecondLayers;

    public static List<ChrMaterial> Convert(FLVER2 flver, FLVER2 template, int ds3Chr, int newChr, Func<string, long>? textureArea = null,
        ChrGlow.Template? glow = null) =>
        Convert(flver, template, ds3Chr, newChr, PlainMaterials, ErNodes, textureArea, ErVertexColours, glow);

    /// <summary>From the last Convert on this thread: DS3 materials drawn on the glowing material.</summary>
    [ThreadStatic] public static int Glowing;

    /// <param name="textureArea">Pixels of a texture by its new name (0 when unknown); picks the layer a two-layer material shows.</param>
    public static List<ChrMaterial> Convert(FLVER2 flver, FLVER2 template, int ds3Chr, int newChr, bool plainMaterials, bool erNodes,
        Func<string, long>? textureArea = null, bool erColours = true, ChrGlow.Template? glow = null)
    {
        string oldTag = $"c{ds3Chr:D4}", newTag = $"c{newChr:D4}";
        return Convert(flver, template, newTag, n => TextureName(n, oldTag, newTag), $@"N:\GR\data\Model\chr\{newTag}\tex\",
            plainMaterials, erNodes, textureArea, erColours, glow);
    }

    /// <summary>
    /// The same conversion for any skinned model (DS3 armour too): <paramref name="newTag"/> names the materials,
    /// <paramref name="rename"/> gives a DS3 texture name its new name, and plain materials say their textures are
    /// in <paramref name="textureFolder"/>.
    /// </summary>
    public static List<ChrMaterial> Convert(FLVER2 flver, FLVER2 template, string newTag, Func<string, string> rename, string textureFolder,
        bool plainMaterials, bool erNodes, Func<string, long>? textureArea = null, bool erColours = true, ChrGlow.Template? glow = null)
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
        int secondLayers = 0;
        var plain = new Dictionary<(string?, string?, string?, bool, string?, System.Numerics.Vector2), string>();
        int glowing = 0, glowGx = -1;
        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var m = flver.Materials[i];
            FLVER2.Texture? Ds3Slot(params string[] names) => m.Textures.FirstOrDefault(t => t.Path.Length > 0
                && names.Any(n => t.ParamName.Equals(n, StringComparison.Ordinal) || t.ParamName.EndsWith(n, StringComparison.Ordinal)));
            string? Ds3Texture(params string[] names) => Ds3Slot(names) is { } t
                ? rename(Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path)))
                : null;
            // DS3's special shaders name their slots without the layer number (the Pus of Man's body,
            // C[FlowVertexOffset]: FlowmapVertexOffset_snp_Texture2D_..._AlbedoMap), and tile their textures: that
            // body's 128x128 albedo repeats 35 x 15 times. The plain material's UV scale takes the albedo's tiling;
            // up to 1.0.368 it was 1, so the texture stretched once over the whole body.
            var albedoSlot = Ds3Slot("g_DiffuseTexture", "_AlbedoMap_0", "_AlbedoMap");
            var albedo = Ds3Texture("g_DiffuseTexture", "_AlbedoMap_0", "_AlbedoMap");
            var normal = Ds3Texture("g_BumpmapTexture", "_NormalMap_0", "_NormalMap");
            var specular = Ds3Texture("g_SpecularTexture", "_ReflectanceMap_0", "_ReflectanceMap");
            // A two-layer DS3 material (C[ARSN]_m: Pontiff Sulyvahn's body is a 512 "Corrosion" layer over the 2048
            // skin) blends by vertex alpha; the plain shader has one layer. The layer with the larger colour texture
            // is the surface itself, the smaller one a tiling overlay: up to 1.0.299 the first was taken whatever it was.
            if (erColours && textureArea is not null && Ds3Texture("g_DiffuseTexture2", "_AlbedoMap_1") is { } albedo2
                && (albedo is null || textureArea(albedo2) > textureArea(albedo)))
            {
                albedo = albedo2;
                albedoSlot = Ds3Slot("g_DiffuseTexture2", "_AlbedoMap_1");
                normal = Ds3Texture("g_BumpmapTexture2", "_NormalMap_1") ?? normal;
                specular = Ds3Texture("g_SpecularTexture2", "_ReflectanceMap_1") ?? specular;
                secondLayers++;
            }
            var metallic = specular is not null && specular.EndsWith("_r", StringComparison.OrdinalIgnoreCase) ? specular[..^2] + "_m" : null;
            var uvScale = albedoSlot?.TilingScale is { X: > 0, Y: > 0 } tiling ? tiling : System.Numerics.Vector2.One;
            // DS3's glow map (g_EmissiveTexture: the Jailer's lantern, the Soul of Cinder's body and sword); SYSTEX
            // dummies are the "no glow" placeholder.
            // The Soul of Cinder's body names two: EmissiveMap_0 is a SYSTEX dummy (or, on the mantle, c5280_mantle_e,
            // which is in no DS3 binder: 1.0.353 drew it as a plain white strip) and EmissiveMap_1 his own _em ember
            // map. The first that is a texture the character has is taken.
            var emissive = m.Textures
                .Where(t => t.Path.Length > 0 && (t.ParamName.Equals("g_EmissiveTexture", StringComparison.Ordinal) || t.ParamName.Contains("_EmissiveMap_", StringComparison.Ordinal)))
                .Select(t => rename(Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path))))
                .FirstOrDefault(e => !e.StartsWith("SYSTEX", StringComparison.OrdinalIgnoreCase) && (textureArea is null || textureArea(e) > 0));
            if (glow is null)
                emissive = null;
            bool alphaTest = IsAlphaTested(m.MTD);
            // "#01#": the display mask the mesh belongs to (NpcParam modelDispMask1), the same in both games.
            m.Name = $"{MaskPrefix(m.Name)}{newTag}_{i}";
            m.GXIndex = 0;
            if (plainMaterials)
            {
                // Meshes with the same textures share one material file.
                var key = (albedo, normal, metallic, alphaTest, emissive, uvScale);
                if (!plain.TryGetValue(key, out var name))
                {
                    plain[key] = name = $"C[{newTag}]_ds3_{plain.Count}";
                    materials.Add(new ChrMaterial(emissive is null ? ChrMaterial.PlainSource : glow!.Matbin, name, albedo, normal, metallic)
                        { Plain = true, AlphaTest = alphaTest, TextureFolder = textureFolder, Emissive = emissive, UvScale = uvScale });
                }
                m.MTD = $@"N:\GR\data\Material\mtd\Chr\matxml\{name}.matxml";
                if (emissive is not null)
                {
                    // The glowing material is drawn with the GX list its own character uses for it.
                    if (glowGx < 0)
                    {
                        glowGx = flver.GXLists.Count;
                        flver.GXLists.Add(glow!.GX);
                    }
                    m.GXIndex = glowGx;
                    m.Textures = glow!.Samplers.Select(n => new FLVER2.Texture(n, "", System.Numerics.Vector2.One, 0, 0, 0, 0, 0)).ToList();
                    glowing++;
                    continue;
                }
                m.Textures = new[] { PlainAlbedo, PlainMetallic, PlainNormal }
                    .Select(n => new FLVER2.Texture(n, "", uvScale, 0, 0, 0, 0, 0)).ToList();
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
        // Elden Ring's character meshes carry the vertex colour 1 0 0 1 (red, opaque) on every material but fur
        // (read off c3250, c3570, c3900, c4340, c4353). DS3's carry 1 0 1 1, blend materials their layer weight in
        // alpha (Pontiff's body goes down to 0), and meshes without colours (all of the Soul of Cinder's body) were
        // given white: on Elden Ring's shader those drew blank. Every ported vertex now carries Elden Ring's value.
        VertexColoursSet = 0;
        if (erColours)
            foreach (var vertex in flver.Meshes.SelectMany(mesh => mesh.Vertices))
                for (int c = 0; c < vertex.Colors.Count; c++)
                {
                    var old = vertex.Colors[c];
                    if (old.A != 1f || old.R != 1f || old.G != 0f || old.B != 0f)
                        VertexColoursSet++;
                    vertex.Colors[c] = new FLVER.VertexColor(1f, 1f, 0f, 0f);
                }
        SecondLayers = secondLayers;
        Glowing = glowing;
        return materials;
    }

    /// <summary>The texture names (no folder, no extension) a DS3 model's materials use.</summary>
    public static HashSet<string> TextureNames(FLVER2 flver) =>
        flver.Materials.SelectMany(m => m.Textures).Where(t => t.Path.Length > 0)
            .Select(t => Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The other characters whose textures a DS3 model uses, from its texture paths (chr\c2029\tex) and names (c6089_n).</summary>
    public static List<int> TextureOwners(FLVER2 flver, int ownChr)
    {
        var owners = new SortedSet<int>();
        foreach (var t in flver.Materials.SelectMany(m => m.Textures).Where(t => t.Path.Length > 0))
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(t.Path, @"(?:^|[\\/])c(\d{4})[\\/_]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                if (int.Parse(match.Groups[1].Value) is var chr && chr != ownChr)
                    owners.Add(chr);
        return owners.ToList();
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

    /// <summary>
    /// False with DARKSIGN_ENEMY_TEXTURES=added: DS3's textures go into a second file next to the template's in the
    /// high-quality texture binder, with flat "not metal" maps, as builds up to 1.0.271 did.
    /// </summary>
    public static bool OwnTextureFile => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_TEXTURES")?.Trim().ToLowerInvariant() != "added";

    /// <summary>DS3 textures renamed to the new ID; with <paramref name="flatMetallic"/> plus a flat _m (metallic) map per _r (reflectance) map.</summary>
    public static bool IsDds(byte[]? bytes) => bytes is { Length: >= 128 } && bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S' && bytes[3] == ' ';

    public static TPF ConvertTpf(TPF tpf, int ds3Chr, int newChr, bool flatMetallic = false, List<string>? leftOut = null)
    {
        string oldTag = $"c{ds3Chr:D4}", newTag = $"c{newChr:D4}";
        return ConvertTpf(tpf, n => TextureName(n, oldTag, newTag), flatMetallic, leftOut);
    }

    /// <summary>The same for any texture binder: <paramref name="rename"/> gives each texture its new name.</summary>
    public static TPF ConvertTpf(TPF tpf, Func<string, string> rename, bool flatMetallic = false, List<string>? leftOut = null)
    {
        var added = new List<TPF.Texture>();
        // A texture that isn't a DDS file stops the whole binder's write (SoulsFormats reads each PC texture's DDS
        // header): 1.0.347, c6330 Big Locust ("%)!") and c6331 Small Locust ("??|") built on Elden Ring bodies.
        // A console texture gets its DDS header back; anything else is left out and named.
        foreach (var t in tpf.Textures.ToList())
        {
            if (IsDds(t.Bytes))
                continue;
            byte[]? headed = null;
            if (t.Platform != TPF.TPFPlatform.PC)
                try { headed = t.Headerize(); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            if (headed is not null && IsDds(headed))
            {
                t.Bytes = headed;
                continue;
            }
            tpf.Textures.Remove(t);
            leftOut?.Add($"{t.Name} ({tpf.Platform}, format {t.Format}, flags {t.Flags1}, {t.Bytes.Length} bytes, starts {System.Convert.ToHexString(t.Bytes.AsSpan(0, Math.Min(8, t.Bytes.Length)))})");
        }
        foreach (var t in tpf.Textures)
        {
            t.Name = rename(t.Name);
            t.Platform = TPF.TPFPlatform.PC;
            if (flatMetallic && t.Name.EndsWith("_r", StringComparison.OrdinalIgnoreCase))
                added.Add(new TPF.Texture(t.Name[..^2] + "_m", 103, 0, ModelPort.BlackBc4Dds(), TPF.TPFPlatform.PC));
        }
        tpf.Textures.AddRange(added);
        return tpf;
    }

    /// <summary>
    /// The low-quality copy of a character's textures, as Elden Ring keeps it in cXXXX_l.texbnd: every texture under
    /// its name + "_l", two sizes down (the Imp's 1024 body is 256 there).
    /// </summary>
    public static TPF LowTpf(TPF high)
    {
        var low = new TPF { Platform = high.Platform, Encoding = high.Encoding, Flag2 = high.Flag2 };
        foreach (var t in high.Textures)
            low.Textures.Add(new TPF.Texture(t.Name + "_l", t.Format, t.Flags1, DropMips(t.Bytes, 2), TPF.TPFPlatform.PC));
        return low;
    }

    /// <summary>Bytes per 4x4 block of a block-compressed DDS (8 or 16), 0 for anything else.</summary>
    private static int BlockBytes(byte[] dds)
    {
        var fourCc = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
        if (fourCc == "DX10")
        {
            if (dds.Length < 148 || BitConverter.ToInt32(dds, 140) > 1 || (BitConverter.ToInt32(dds, 136) & 0x4) != 0)
                return 0; // texture arrays and cube maps are left alone
            return BitConverter.ToInt32(dds, 128) switch
            {
                >= 70 and <= 72 => 8,   // BC1
                >= 73 and <= 78 => 16,  // BC2, BC3
                >= 79 and <= 81 => 8,   // BC4
                >= 82 and <= 84 => 16,  // BC5
                >= 94 and <= 99 => 16,  // BC6H, BC7
                _ => 0,
            };
        }
        return fourCc switch
        {
            "DXT1" or "ATI1" or "BC4U" or "BC4S" => 8,
            "DXT2" or "DXT3" or "DXT4" or "DXT5" or "ATI2" or "BC5U" or "BC5S" => 16,
            _ => 0,
        };
    }

    /// <summary>
    /// A block-compressed DDS without its <paramref name="drop"/> largest mip levels (fewer when it has too few, or
    /// would get smaller than 4 pixels). Anything else (uncompressed, cube map, a length that doesn't add up) is
    /// returned as it is.
    /// </summary>
    public static byte[] DropMips(byte[] dds, int drop)
    {
        if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || BlockBytes(dds) is not (> 0 and var block))
            return dds;
        if ((BitConverter.ToInt32(dds, 112) & 0x200) != 0)
            return dds; // legacy cube map
        int header = System.Text.Encoding.ASCII.GetString(dds, 84, 4) == "DX10" ? 148 : 128;
        int height = BitConverter.ToInt32(dds, 12), width = BitConverter.ToInt32(dds, 16), mips = Math.Max(1, BitConverter.ToInt32(dds, 28));
        long MipSize(int level) => (long)Math.Max(1, ((width >> level) + 3) / 4) * Math.Max(1, ((height >> level) + 3) / 4) * block;
        long total = 0;
        for (int m = 0; m < mips; m++)
            total += MipSize(m);
        if (header + total != dds.Length)
            return dds;
        int n = 0;
        while (n < drop && n + 1 < mips && (width >> (n + 1)) >= 4 && (height >> (n + 1)) >= 4)
            n++;
        if (n == 0)
            return dds;
        long skip = 0;
        for (int m = 0; m < n; m++)
            skip += MipSize(m);
        var result = new byte[dds.Length - skip];
        Array.Copy(dds, 0, result, 0, header);
        Array.Copy(dds, header + skip, result, header, dds.Length - header - skip);
        BitConverter.GetBytes(height >> n).CopyTo(result, 12);
        BitConverter.GetBytes(width >> n).CopyTo(result, 16);
        BitConverter.GetBytes((int)MipSize(n)).CopyTo(result, 20);
        BitConverter.GetBytes(mips - n).CopyTo(result, 28);
        return result;
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
    /// <summary>DS3's glow map: the material is a copy of Elden Ring's glowing character material (<see cref="ChrGlow"/>).</summary>
    public string? Emissive { get; init; }
    /// <summary>Where a plain material's samplers say its textures are (the game finds them by file name).</summary>
    public string TextureFolder { get; init; } = "";
    /// <summary>How many times a plain material's textures repeat across the UVs (DS3's albedo tiling; 1 1 for most).</summary>
    public System.Numerics.Vector2 UvScale { get; init; } = System.Numerics.Vector2.One;

    /// <summary>The new material, made from <paramref name="matbin"/> (changed in place), and how many samplers now name a DS3 texture.</summary>
    public int Apply(MATBIN matbin)
    {
        int swapped = 0;
        foreach (var sampler in matbin.Samplers)
        {
            var texture = sampler.Type.Contains("Emissive", StringComparison.OrdinalIgnoreCase) ? Emissive
                : sampler.Type.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ? Albedo
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
        if (Plain && Emissive is not null)
            ChrGlow.Whiten(matbin);
        if (Plain)
            foreach (var p in matbin.Params)
                switch (p.Name)
                {
                    case "g_AlphaRef": p.Value = new[] { AlphaTest ? 50 : 0, 0 }; break;
                    case "g_BlendMode": p.Value = 0; break;
                    case "C_AMSN__snp_0_color_0": p.Value = new[] { 1f, 1f, 1f, 1f, 1f }; break;
                    case "C_AMSN__snp_0_float_0" or "C_AMSN__snp_0_float_1": p.Value = 0f; break;
                    case "group_1_CommonUV-UVParam": p.Value = new[] { UvScale.X, UvScale.Y }; break;
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
