using System.Numerics;
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
        string? Normal2, Vector2 Scale = default, bool TemplateTextures = false);

    /// <summary>
    /// A vanilla Elden Ring map material used as a pattern, with an example of it in a FLVER: the material slot, its
    /// GX list and the vertex layout of a vanilla static mesh drawn with it. Elden Ring only draws a mesh whose
    /// vertex layout is one its material's shader accepts (JortPob ships the accepted layouts of every material as
    /// MaterialInfo.xml and builds each mesh in its material's layout), so every ported mesh is rebuilt in
    /// <see cref="Layout"/>.
    /// </summary>
    public sealed record MaterialTemplate(string Kind, string Matbin, BinderFile File, FLVER2.Material Material, FLVER2.GXList GxList,
        FLVER2.BufferLayout Layout, int Rank = 0);

    /// <summary>The kinds of template, in the order their GX lists and layouts are written.</summary>
    public static readonly string[] Kinds = ["opaque", "edge", "alpha", "nolight", "water"];

    /// <summary>The kinds the template search looks for in the template maps (water comes from <see cref="WaterBinders"/>).</summary>
    public static readonly string[] SearchedKinds = ["opaque", "edge", "alpha", "nolight"];

    /// <summary>
    /// Elden Ring map binders holding a plain water surface, tried in order for the "water" template. Elden Ring's
    /// small still water (M_MuddyWater: m22_00_110 in Volcano Manor's 900004, m35_00_400 in the Shunning-Grounds'
    /// 000007) takes the same vertex layout as the port's other materials (position, normal, tangent, colour, UV0
    /// short, UV1) and no textures of its own (JortPob's MaterialInfo.xml); M_Water_Cheap (m12_03's 000004) is the
    /// fallback. The sea shaders (Field_sea_05, JortPob's choice for Morrowind's ocean) need eight UV channels laid
    /// out as a shoreline ring, which DS3's pools don't have.
    /// </summary>
    public static readonly (MapId Map, string Model)[] WaterBinders =
        [(new MapId(22, 0, 0, 0), "900004"), (new MapId(35, 0, 0, 0), "000007"), (new MapId(12, 3, 0, 0), "000004")];

    /// <summary>Elden Ring water shaders a DS3 water surface can be drawn with (the MATBIN's shader file name).</summary>
    private static readonly Regex WaterShader = new(@"^M_(MuddyWater|Water_Cheap)$", RegexOptions.IgnoreCase);

    /// <summary>Fallback "map-water" B: DARKSIGN_MAP_WATER=0 leaves DS3's water out again (builds up to 1.0.42x).</summary>
    public static bool WaterEnabled => Environment.GetEnvironmentVariable("DARKSIGN_MAP_WATER") != "0";

    /// <summary>True when a water template was found.</summary>
    public bool HasWater => _templates.ContainsKey("water");

    public const string BlackMetallic = "DKS_black_m";

    /// <summary>The flat normal map a ported material uses when its DS3 material had none.</summary>
    public const string FlatNormal = "DKS_flat_n";

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
    public MapModelPort Fresh() => new(_templates, _templateFlver)
        { VanillaMaterials = VanillaMaterials, TemplateTextures = TemplateTextures, Ds3Alpha = Ds3Alpha, TerrainLayers = TerrainLayers, TintMaterials = TintMaterials,
          OverlayBacking = OverlayBacking, CutFadedOverlays = CutFadedOverlays, FadeOverlays = FadeOverlays };

    /// <summary>
    /// Diagnostic "--tint-materials": the albedo of each DS3 material family that keeps something in vertex alpha is a
    /// solid colour (<see cref="Tints"/>), so one screenshot tells which family a wrong surface belongs to. Every
    /// other material keeps its real texture. Not a launcher fallback: for test builds only.
    /// </summary>
    public bool TintMaterials { get; set; }

    /// <summary>The tint textures: name, what they mark, colour (R5G6B5).</summary>
    public static readonly (string Name, string Marks, ushort Rgb565)[] Tints =
    [
        ("DKS_tint_red", "two-layer material (_m), first layer", 0xF800),
        ("DKS_tint_magenta", "two-layer material (_m), second layer", 0xF81F),
        ("DKS_tint_green", "overlay that keeps DS3's vertex alpha fade", 0x07E0),
        ("DKS_tint_blue", "terrain blend (2m/3m/4m), first layer", 0x001F),
        ("DKS_tint_orange", "terrain blend, second layer", 0xFC00),
        ("DKS_tint_white", "terrain blend, third layer", 0xFFFF),
        ("DKS_tint_purple", "terrain blend, fourth layer", 0x801F),
        ("DKS_tint_cyan", "snow material", 0x07FF),
        ("DKS_tint_yellow", "one-layer material with vertex alpha the port ignores (grass, water, parallax...)", 0xFFE0),
    ];

    /// <summary>The tint a converted DS3 material gets under <see cref="TintMaterials"/>, or null to keep its texture.</summary>
    public static string? TintFor(FLVER2.Material m)
    {
        var mtd = Path.GetFileNameWithoutExtension(ItemText.Leaf(m.MTD ?? ""));
        if (MapOverlays.IsTwoLayerMtd(mtd))
            return m.Name.EndsWith(MapOverlays.SecondLayerSuffix, StringComparison.Ordinal) ? "DKS_tint_magenta" : "DKS_tint_red";
        if (m.Name.EndsWith(FadeSuffix, StringComparison.Ordinal))
            return "DKS_tint_green";
        if (MapTerrainBlends.LayerCount(mtd) > 0)
            return MapTerrainBlends.LayerOf(m) switch { 1 => "DKS_tint_orange", 2 => "DKS_tint_white", 3 => "DKS_tint_purple", _ => "DKS_tint_blue" };
        if (mtd.Contains("snow", StringComparison.OrdinalIgnoreCase))
            return "DKS_tint_cyan";
        if (Regex.IsMatch(mtd, @"grass|water|_p(?![a-z])|_tr(?![a-z])", RegexOptions.IgnoreCase))
            return "DKS_tint_yellow";
        return null;
    }

    private const string FadeSuffix = "_fade";

    /// <summary>
    /// True (the default): DS3's vertex alpha keeps its meaning (<see cref="MapOverlays"/>): overlays fade, two-layer
    /// materials show the layer each triangle mostly shows. False (fallback "map-overlays" B, --solid-overlays): every
    /// vertex is solid and a two-layer material shows its first layer, as every build up to 1.0.340.
    /// </summary>
    public bool Ds3Alpha { get; set; } = true;

    /// <summary>
    /// True (the default): each triangle of a DS3 terrain blend (2m/3m/4m) is drawn with the layer DS3 shows there
    /// (<see cref="MapTerrainBlends"/>). False (fallback "map-blend-layers" B, --first-blend-layer): the whole mesh shows
    /// its first layer, as every build up to 1.0.353.
    /// </summary>
    public bool TerrainLayers { get; set; } = true;

    /// <summary>
    /// The map's index of surfaces under faded overlay triangles (<see cref="MapOverlayBacking"/>): a faded triangle is
    /// left out only where one lies behind it. Null: each model is tested against its own surfaces.
    /// </summary>
    public MapOverlayBacking? OverlayBacking { get; set; }

    /// <summary>
    /// True (fallback "map-overlays" D, --cut-faded-overlays): every faded overlay triangle is left out whatever lies
    /// behind it, as 1.0.353 to 1.0.383, which left holes in Firelink Shrine's floor. Only with <see cref="FadeOverlays"/>.
    /// </summary>
    public bool CutFadedOverlays { get; set; }

    /// <summary>
    /// False (the default): a one-layer DS3 map mesh (M[ARSN]_l and the like) is drawn solid whatever its vertex alpha
    /// says. That alpha is not a fade: Firelink Shrine's tunnel walls (m004001: 32,705 vertices, m004520: 3,016, both
    /// wall_103) have alpha 0 on every vertex next to a mesh of the same wall at alpha 1, and Undead Settlement's
    /// wooden houses (m000120) and the Painted World's stone walls lost every triangle to the overlay cut. DS3 draws
    /// all of them. True (fallback "map-overlays" C, --fade-overlays): the overlay fade of 1.0.353 to 1.0.383
    /// (<see cref="MapOverlays"/>, <see cref="MapOverlayBacking"/>). Two-layer materials are split by layer either way.
    /// </summary>
    public bool FadeOverlays { get; set; }

    /// <summary>
    /// FLVERs left out of a binder that kept another, drawable FLVER: they held only meshes the port can't draw
    /// (shadow casters, water, effects). For the port log.
    /// </summary>
    public int EmptyFlvers { get; private set; }

    /// <summary>What <see cref="MapTerrainBlends"/> did to this port's models, for the port log.</summary>
    public MapTerrainBlends.Stats Blends { get; } = new();

    /// <summary>What <see cref="MapOverlays"/> did to this port's models, for the port log.</summary>
    public MapOverlays.Stats Overlays { get; } = new();

    /// <summary>
    /// Piece test "vanilla-material" (see MapPorter.PieceTest): every converted mesh keeps its template's own vanilla
    /// material, untouched (name, MATBIN and textures are Elden Ring's), so the model depends on nothing the port
    /// writes but its geometry. No MATBIN or texture is added for such a model.
    /// </summary>
    public bool VanillaMaterials { get; private init; }

    /// <summary>This port's templates with <see cref="VanillaMaterials"/> on.</summary>
    public MapModelPort WithVanillaMaterials() => new(_templates, _templateFlver) { VanillaMaterials = true };

    /// <summary>
    /// Piece test "ported-material" (see MapPorter.PieceTest): every DS3 material still becomes a MATBIN of its own
    /// (a renamed copy of its template, added to the material binder), but the copy keeps the template's vanilla
    /// textures. The model then depends on the port's MATBINs and on none of its textures.
    /// </summary>
    public bool TemplateTextures { get; private init; }

    /// <summary>This port's templates with <see cref="TemplateTextures"/> on.</summary>
    public MapModelPort WithTemplateTextures() => new(_templates, _templateFlver) { TemplateTextures = true };

    /// <summary>
    /// False (the default): the MATBINs of unlit materials are told not to cast shadows. DS3's sky is a map piece,
    /// an unlit dome kilometres across around the whole map, and Elden Ring lets every map piece shadow the sun
    /// unless its material says otherwise (g_GX_bDisableShadowCasting, on in 857 vanilla materials): up to 1.0.284
    /// the dome kept the sun off the entire map. True ("--host-lighting") leaves the template's own setting.
    /// </summary>
    public bool UnlitCastShadows { get; set; }

    /// <summary>The MATBIN switch that keeps a material's meshes out of the shadow maps.</summary>
    public const string NoShadowParam = "g_GX_bDisableShadowCasting";

    /// <summary>"Position0:Float3 Normal0:UByte4 ..." for a vertex layout.</summary>
    public static string LayoutText(IEnumerable<FLVER.LayoutMember> layout) =>
        string.Join(" ", layout.Select(m => $"{m.Semantic}{m.Index}:{m.Type}"));

    /// <summary>The vertex layouts the ported meshes are written in (one per template), as <see cref="LayoutText"/>.</summary>
    public HashSet<string> TemplateLayouts() => _templates.Values.Select(t => LayoutText(t.Layout)).ToHashSet();

    public string TemplateSummary => string.Join(", ", _templates.Select(t => $"{t.Key}={t.Value.Matbin}"));

    /// <summary>
    /// Picks material templates from vanilla Elden Ring models, earlier ones first, one per kind: "opaque" (lit, one
    /// layer), "edge" (alpha tested: foliage, grates), "alpha" (blended) and "nolight" (unlit: DS3's sky domes). Only
    /// "opaque" is required. A candidate needs a vanilla static mesh drawn with it, whose vertex layout the ported
    /// meshes take. Within a kind the lowest <see cref="Rank"/> wins (a normal-mapped shader over an albedo-only one).
    /// <paramref name="plain"/> (fallback "map-materials" B) only takes the albedo-only opaque shader JortPob uses.
    /// <paramref name="headerFlver"/> gives the FLVER header (a map piece); the first model when null.
    /// </summary>
    public static MapModelPort Create(IReadOnlyList<FLVER2> flvers, IReadOnlyDictionary<string, BinderFile> matbins,
        FLVER2? headerFlver = null, bool plain = false)
    {
        if (flvers.Count == 0)
            throw new InvalidDataException("no Elden Ring model to copy materials from");
        var templates = new Dictionary<string, MaterialTemplate>();
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var flver in flvers)
            foreach (var mesh in flver.Meshes)
            {
                if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count)
                    continue;
                var material = flver.Materials[mesh.MaterialIndex];
                var name = ModelPort.MatbinName(material.MTD);
                if (rejected.Contains(name) || templates.Values.Any(t => t.Matbin.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!matbins.TryGetValue(name, out var file) || material.GXIndex < 0 || material.GXIndex >= flver.GXLists.Count
                    || StaticLayout(flver, mesh) is not { } layout)
                    continue;
                MATBIN matbin;
                try { matbin = MATBIN.Read(file.Bytes); }
                catch (Exception) { rejected.Add(name); continue; }
                if (ClassifyRanked(matbin) is not { } c || (plain && (c.Kind != "opaque" || c.Rank < PlainRank)))
                {
                    rejected.Add(name);
                    continue;
                }
                if (templates.TryGetValue(c.Kind, out var known) && known.Rank <= c.Rank)
                    continue;
                templates[c.Kind] = new MaterialTemplate(c.Kind, name, file, material, flver.GXLists[material.GXIndex],
                    CopyLayout(layout), c.Rank);
            }
        if (!templates.ContainsKey("opaque"))
            throw new InvalidDataException("no plain one-layer material among these models to use as a template");
        return new MapModelPort(templates, headerFlver ?? flvers[0]);
    }

    /// <summary>True when every kind has its best template (nothing more to look for in further maps).</summary>
    public bool Complete(bool plain) =>
        plain ? _templates["opaque"].Rank == PlainRank : SearchedKinds.All(_templates.ContainsKey) && _templates["opaque"].Rank == 0;

    private const int PlainRank = 2;

    /// <summary>
    /// The layout of a vanilla mesh a ported static mesh can take: one vertex buffer holding only position, normal,
    /// tangents, bitangent, colours and UVs (no bone members). Null for anything else.
    /// </summary>
    public static FLVER2.BufferLayout? StaticLayout(FLVER2 flver, FLVER2.Mesh mesh)
    {
        if (mesh.VertexBuffers.Count != 1 || mesh.VertexBuffers[0].LayoutIndex < 0 || mesh.VertexBuffers[0].LayoutIndex >= flver.BufferLayouts.Count)
            return null;
        var layout = flver.BufferLayouts[mesh.VertexBuffers[0].LayoutIndex];
        bool ok = layout.Count > 0 && layout.All(m => m.Semantic is FLVER.LayoutSemantic.Position or FLVER.LayoutSemantic.Normal
            or FLVER.LayoutSemantic.Tangent or FLVER.LayoutSemantic.Bitangent or FLVER.LayoutSemantic.VertexColor or FLVER.LayoutSemantic.UV)
            && layout.Count(m => m.Semantic == FLVER.LayoutSemantic.Position) == 1
            && layout.Count(m => m.Semantic == FLVER.LayoutSemantic.Normal) == 1;
        return ok ? layout : null;
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

    // Elden Ring's plain one-layer map shaders: M[A] (albedo), M[AMSN] / M[AMSN_V] (albedo, normal, with or without a
    // metallic map), each also as _Edge (alpha test), _Alpha (blend) and _NoLight. Everything else (terrain blends,
    // overlays, snow, water, grass sway, flags...) reads extra vertex channels or masks DS3 has nothing for.
    private static readonly Regex PlainShader = new(@"^M\[(A|AMSN|AMSN_V|ARSN)\](_Edge|_Alpha|_NoLight)?$", RegexOptions.IgnoreCase);

    /// <summary>"opaque", "edge", "alpha" or "nolight" for a usable Elden Ring MATBIN, null for anything else.</summary>
    public static string? Classify(MATBIN matbin) => ClassifyRanked(matbin)?.Kind;

    /// <summary>The kind and how good a template it is: 0 normal-mapped, 1 with a metallic map too, 2 albedo only.</summary>
    public static (string Kind, int Rank)? ClassifyRanked(MATBIN matbin)
    {
        // Water: its textures live in the MATBIN, so the material is used as it is (no samplers to fill).
        if (WaterShader.Match(Path.GetFileNameWithoutExtension(ItemText.Leaf(matbin.ShaderPath ?? ""))) is { Success: true } w)
            return WaterEnabled ? ("water", w.Groups[1].Value.Equals("MuddyWater", StringComparison.OrdinalIgnoreCase) ? 0 : 1) : null;
        var roles = matbin.Samplers.Select(x => Role(x.Type)).Where(r => r is not null).Select(r => r!.Value).ToList();
        // Terrain blend materials (most of Elden Ring's legacy dungeon pieces) mix layers through a mask: not usable.
        if (roles.Any(r => r.Role == "blend" || r.Layer > 0) || roles.Count(r => r == ("albedo", 0)) != 1
            || roles.Count(r => r == ("normal", 0)) > 1)
            return null;
        var shader = Path.GetFileNameWithoutExtension(ItemText.Leaf(matbin.ShaderPath ?? ""));
        var m = PlainShader.Match(shader);
        if (!m.Success)
            return null;
        var kind = m.Groups[2].Value.ToLowerInvariant() switch { "_edge" => "edge", "_alpha" => "alpha", "_nolight" => "nolight", _ => "opaque" };
        int rank = !roles.Contains(("normal", 0)) ? PlainRank : roles.Any(r => r.Role == "metallic") ? 1 : 0;
        return (kind, kind == "nolight" ? 0 : rank);
    }

    /// <summary>
    /// The kind of Elden Ring material a DS3 material maps to, from its MTD name: "opaque", "edge" (alpha tested:
    /// _e, _Alp, Edge), "alpha" (blended: _al), "nolight" (unlit: sky domes), or "none" for what an opaque surface
    /// can't stand in for (additive glows, decals, fog cards, shadow-only casters, water, multiply layers, unlit
    /// blended cloud layers). "none" meshes are left out of the model until those materials are ported.
    /// </summary>
    public static string Ds3Kind(FLVER2.Material material)
    {
        var mtd = Path.GetFileNameWithoutExtension(ItemText.Leaf(material.MTD ?? ""));
        bool noLight = mtd.Contains("NoLight", StringComparison.OrdinalIgnoreCase);
        // A water surface (M[ARSN]_Water_3normaltet in Firelink, M[ARSN]_Water_DL on the High Wall); not waterfalls,
        // which are flowing cards with their own textures.
        if (Regex.IsMatch(mtd, @"water(?!fall)", RegexOptions.IgnoreCase) && !Regex.IsMatch(mtd, @"_add|decal|_mul|sfx", RegexOptions.IgnoreCase))
            return "water";
        if (Regex.IsMatch(mtd, @"_add|decal|fog|shadow|_mul|water|sfx|ghost", RegexOptions.IgnoreCase))
            return "none";
        if (Regex.IsMatch(mtd, @"_al(?![a-z])", RegexOptions.IgnoreCase))
            return noLight ? "none" : "alpha";
        if (noLight)
            return "nolight";
        if (Regex.IsMatch(mtd, @"_e(?![a-z])|_alp|edge", RegexOptions.IgnoreCase))
            return "edge";
        return "opaque";
    }

    /// <summary>The template a DS3 kind is drawn with: its own, else opaque; a blended one with no template is left out.</summary>
    private MaterialTemplate? TemplateFor(string kind) =>
        kind == "none" ? null
        : _templates.TryGetValue(kind, out var t) ? t
        : kind is "alpha" or "water" ? null
        : _templates["opaque"];

    /// <summary>
    /// A DS3 material's texture for a role. Plain materials name them g_DiffuseTexture / g_BumpmapTexture; multi-layer
    /// ones (M[ARSN]_2m/3m/4m: most ground and walls) name them "..._GSBlendMap_AlbedoMap_0" / "..._NormalMap_0", and
    /// their first layer is what the one-layer Elden Ring material shows.
    /// </summary>
    public static FLVER2.Texture? Ds3Texture(FLVER2.Material m, string role)
    {
        var (plain, layered) = role == "albedo" ? ("g_DiffuseTexture", "AlbedoMap_0") : ("g_BumpmapTexture", "NormalMap_0");
        return m.Textures.FirstOrDefault(t => t.ParamName == plain && t.Path.Length > 0)
               ?? m.Textures.FirstOrDefault(t => t.ParamName.EndsWith(layered, StringComparison.OrdinalIgnoreCase) && t.Path.Length > 0);
    }

    // What the converted models of this map hold, for the report's "load:" line (see LoadSummary).
    private long _models, _meshes, _vertices, _triangles, _lodFaceSets, _modelBytes;

    /// <summary>
    /// What the game has to hold and draw for this map's pieces: models, meshes, vertices, full detail triangles,
    /// and how many lower detail triangle lists DS3 shipped inside the models (drawn where the PartsDrawParam row
    /// switches detail by distance, <see cref="MapDrawParams.LodRow"/>; see <see cref="Lods"/>).
    /// </summary>
    public string LoadSummary =>
        $"{_models} models, {_meshes} meshes, {_vertices:N0} vertices, {_triangles:N0} triangles at full detail "
        + $"({_lodFaceSets} lower detail triangle lists from DS3), {_modelBytes / 1048576.0:F0} MB of model data";

    private void Count(FLVER2 flver, int bytes)
    {
        long vertices = 0, triangles = 0, lods = 0;
        foreach (var mesh in flver.Meshes)
        {
            vertices += mesh.Vertices.Count;
            foreach (var set in mesh.FaceSets)
            {
                if (set.Flags == FLVER2.FaceSet.FSFlags.None)
                    triangles += set.TriangleStrip ? Math.Max(0, set.Indices.Count - 2) : set.Indices.Count / 3;
                else if ((set.Flags & (FLVER2.FaceSet.FSFlags.LodLevel1 | FLVER2.FaceSet.FSFlags.LodLevel2)) != 0)
                    lods++;
            }
        }
        Interlocked.Increment(ref _models);
        Interlocked.Add(ref _meshes, flver.Meshes.Count);
        Interlocked.Add(ref _vertices, vertices);
        Interlocked.Add(ref _triangles, triangles);
        Interlocked.Add(ref _lodFaceSets, lods);
        Interlocked.Add(ref _modelBytes, bytes);
    }

    /// <summary>Models (DS3 model numbers) whose every FLVER carries DS3's lower detail lists in every mesh (<see cref="MapLod"/>).</summary>
    public HashSet<string> LodModels { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="MapLod.Complete"/> found in this map's models.</summary>
    public MapLod.Stats Lods { get; } = new();

    /// <summary>Meshes left out of the converted models, by DS3 material type ("M[A]_Add_NoLight" -> 12).</summary>
    public Dictionary<string, int> LeftOut { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One mesh of a DS3 map piece model, for the piece report: its DS3 material type, the kind it was drawn as
    /// ("none" when left out), its size and the albedo texture it shows. Min and Max are its box in the model's own
    /// space (zero for a mesh with no vertices), which <see cref="MapStandIns"/> places in the map.
    /// </summary>
    public sealed record MeshInfo(string Mtd, string Kind, int Vertices, Vector3 Size, string Albedo)
    {
        public Vector3 Min { get; init; }
        public Vector3 Max { get; init; }
    }

    /// <summary>Every converted model's meshes by model number ("000052"), left-out meshes included.</summary>
    public Dictionary<string, List<MeshInfo>> ModelMeshes { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal static List<MeshInfo> Describe(FLVER2 flver)
    {
        var list = new List<MeshInfo>();
        foreach (var mesh in flver.Meshes)
        {
            var m = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < flver.Materials.Count ? flver.Materials[mesh.MaterialIndex] : null;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in mesh.Vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            var albedo = m is null ? null : Ds3Texture(m, "albedo");
            list.Add(new MeshInfo(
                m is null ? "(no material)" : Path.GetFileNameWithoutExtension(ItemText.Leaf(m.MTD ?? "")),
                m is null ? "none" : Ds3Kind(m), mesh.Vertices.Count, mesh.Vertices.Count == 0 ? Vector3.Zero : max - min,
                albedo is null ? "" : Path.GetFileNameWithoutExtension(ItemText.Leaf(albedo.Path)))
            {
                Min = mesh.Vertices.Count == 0 ? Vector3.Zero : min,
                Max = mesh.Vertices.Count == 0 ? Vector3.Zero : max,
            });
        }
        return list;
    }

    /// <summary>Converts every FLVER in a DS3 mapbnd; the binder layout comes from the host's template binder.</summary>
    /// <param name="parsed">The binder's FLVERs already read, in binder order (so they aren't read twice).</param>
    public BND4 ConvertBinder(BND4 ds3, BND4 hostTemplate, MapId ds3Id, MapId erId, string modelNumber, MapId? hostId = null,
                              IReadOnlyList<FLVER2>? parsed = null)
    {
        var templateFile = hostTemplate.Files.First(f => FLVER2.Is(f.Bytes));
        var folder = ModelFolder(templateFile, erId, modelNumber, hostId);

        var files = new List<BinderFile>();
        var emptyFlvers = new List<string>();
        bool allLod = true;
        int index = 0;
        foreach (var file in ds3.Files.Where(f => FLVER2.Is(f.Bytes)))
        {
            var flver = parsed is not null && index < parsed.Count ? parsed[index] : FLVER2.Read(file.Bytes);
            index++;
            var described = Describe(flver);
            lock (ModelMeshes)
            {
                if (!ModelMeshes.TryGetValue(modelNumber, out var known))
                    ModelMeshes[modelNumber] = known = [];
                known.AddRange(described);
            }
            var ds3Leaf = ItemText.Leaf(file.Name);
            var leaf = ds3Leaf.Replace(ds3Id.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);
            ConvertFlver(flver, ds3Id, modelNumber);
            if (flver.Meshes.Count == 0)
            {
                // A FLVER of its own that holds only what the port can't draw (DS3 gives many trees, cages and
                // hanging corpses a second FLVER with just their M[Shadow] caster): that FLVER is left out, not the
                // whole binder. Up to 1.0.383 it took the drawable FLVER with it (354 trees in Undead Settlement).
                emptyFlvers.Add(ds3Leaf);
                continue;
            }
            // As vanilla map pieces have them: the one node named after the model under its Elden Ring map ID, and
            // the materials numbered as Elden Ring numbers them (a repeated material keeps its first number).
            if (flver.Nodes.Count == 1)
                flver.Nodes[0].Name = leaf[..leaf.IndexOf('.')];
            if (FlverMaterialNumbers.Apply(flver) == 0 && !FlverMaterialNumbers.Enabled)
                for (int i = 0; i < flver.Materials.Count; i++)
                    flver.Materials[i].Index = i;
            // DS3's lower detail lists, completed so the model can switch to them (MapLod).
            var lod = MapLod.Complete(flver);
            Lods.Add(lod.HasLod, lod);
            allLod &= lod.HasLod;
            // The template's file ID (200 in every vanilla map piece binder), not a count from 0.
            var written = flver.Write();
            Count(flver, written.Length);
            files.Add(new BinderFile(templateFile.Flags, templateFile.ID + files.Count, folder + leaf, written));
        }
        if (files.Count == 0)
            throw new InvalidDataException("only effect, decal, water or shadow meshes, which have no Elden Ring material yet");
        if (allLod)
            lock (LodModels)
                LodModels.Add(modelNumber);
        if (emptyFlvers.Count > 0)
            lock (_materials)
                EmptyFlvers += emptyFlvers.Count;
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

    /// <summary>
    /// Converts a DS3 object model (obj/oNNNNNN.objbnd) in place into a model an Elden Ring asset can draw, with the
    /// same materials, textures and vertex layouts as the ported map pieces. Objects differ from map pieces in one
    /// way: their meshes hang off several nodes (a chandelier's arms, a door's leaves). A DS3 mesh that isn't
    /// skinned keeps each vertex in the space of the node its NormalW names, so every vertex is first moved into
    /// model space (<see cref="BakeNodes"/>) and the mesh then sits on the root like a map piece's. Skinned meshes
    /// (cloth, chains) are already in model space and are drawn in their rest pose. Nodes and dummy points stay, so
    /// effects, prompts and ladders still find their dummy polys.
    /// </summary>
    /// <param name="keepNodes">For an object that moves (a door, a lift, a lever, see <see cref="ObjectPort"/>): its
    /// meshes stay on DS3's nodes, so each part follows the bone of the same name when the object's animation plays
    /// (<see cref="KeepNodes"/>), instead of being baked into one rigid model.</param>
    /// <returns>A note when <paramref name="keepNodes"/> could not keep every vertex on its node, else null.</returns>
    public string? ConvertObject(FLVER2 flver, MapId ds3Id, bool keepNodes = false)
    {
        Dictionary<FLVER.Vertex, int>? nodes = keepNodes ? KeepNodes(flver) : null;
        if (!keepNodes)
            BakeNodes(flver);
        ConvertFlver(flver, ds3Id);
        if (flver.Meshes.Count == 0)
            throw new InvalidDataException("only effect, decal, water or shadow meshes, which have no Elden Ring material yet");
        for (int i = 0; i < flver.Materials.Count; i++)
            flver.Materials[i].Index = i;
        return nodes is null ? null : RestoreNodes(flver, nodes);
    }

    /// <summary>
    /// The node each vertex of an object hangs off, before the conversion flattens the meshes. A DS3 object mesh that
    /// isn't skinned keeps each vertex in the space of the node its NormalW names (through the mesh's bone list when
    /// it has one), and Elden Ring's own moving assets do the same: the door AEG219_000 is one mesh with no bone list
    /// whose leaf's 5,600 vertices carry NormalW 1, its node "Model_AEG219_000_01", the skeleton bone the open/close
    /// animation turns. So every mesh becomes a plain one on node 0 with NormalW = the vertex's node, which
    /// <see cref="RestoreNodes"/> writes back after the vertex layouts are rebuilt. A skinned mesh (cloth, chains) is
    /// in model space; it is moved into the root's space and stays on the root, drawn in its rest pose.
    /// </summary>
    public static Dictionary<FLVER.Vertex, int> KeepNodes(FLVER2 flver)
    {
        var result = new Dictionary<FLVER.Vertex, int>(ReferenceEqualityComparer.Instance);
        var root = flver.Nodes.Count > 0 ? WorldTransforms(flver)[0] : Matrix4x4.Identity;
        if (root.IsIdentity || !Matrix4x4.Invert(root, out var rootInverse))
            rootInverse = Matrix4x4.Identity;
        foreach (var mesh in flver.Meshes)
        {
            foreach (var v in mesh.Vertices)
            {
                int node = 0;
                if (mesh.UseBoneWeights)
                {
                    if (!rootInverse.IsIdentity)
                    {
                        v.Position = Vector3.Transform(v.Position, rootInverse);
                        v.Normal = SafeNormal(Vector3.TransformNormal(v.Normal, rootInverse), v.Normal);
                    }
                }
                else
                {
                    int index = v.NormalWs.Count > 0 ? v.NormalWs[0] : 0;
                    node = index >= 0 && index < mesh.BoneIndices.Count ? mesh.BoneIndices[index] : index;
                    if (node < 0 || node >= flver.Nodes.Count)
                        node = mesh.NodeIndex >= 0 && mesh.NodeIndex < flver.Nodes.Count ? mesh.NodeIndex : 0;
                }
                result[v] = node;
            }
            mesh.UseBoneWeights = false;
            mesh.NodeIndex = 0;
            mesh.BoneIndices = [];
        }
        return result;
    }

    /// <summary>
    /// Puts back the node <see cref="KeepNodes"/> recorded on every vertex and sizes each mesh's culling box from the
    /// rest pose (the vertices are in their nodes' spaces, not the model's). Returns a note when a vertex was lost
    /// track of (a mesh split into copies) or the layout can't hold the node number.
    /// </summary>
    public static string? RestoreNodes(FLVER2 flver, Dictionary<FLVER.Vertex, int> nodes)
    {
        int lost = 0;
        var world = WorldTransforms(flver);
        foreach (var mesh in flver.Meshes)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in mesh.Vertices)
            {
                if (!nodes.TryGetValue(v, out var node))
                {
                    lost++;
                    node = 0;
                }
                if (node > 255)
                    throw new InvalidDataException($"node {node}: a vertex can only name nodes 0-255");
                if (v.NormalWs.Count == 0)
                    v.NormalWs.Add(node);
                else
                    v.NormalWs[0] = node;
                var p = node < world.Length ? Vector3.Transform(v.Positions[0], world[node]) : v.Positions[0];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            if (mesh.Vertices.Count > 0)
                mesh.BoundingBox = flver.Header.Version >= 0x2001A
                    ? new FLVER2.Mesh.BoundingBoxes { Min = (max - min) / 2, Max = Vector3.Zero, Unk = (max + min) / 2 }
                    : new FLVER2.Mesh.BoundingBoxes { Min = min, Max = max };
        }
        return lost == 0 ? null : $"{lost} vertices lost their node (copied when a mesh was split); they stay on the root";
    }

    /// <summary>Each node's rest transform in model space.</summary>
    public static Matrix4x4[] WorldTransforms(FLVER2 flver)
    {
        var world = new Matrix4x4[flver.Nodes.Count];
        var done = new bool[flver.Nodes.Count];
        Matrix4x4 World(int i, int depth)
        {
            if (i < 0 || i >= world.Length || depth > 64)
                return Matrix4x4.Identity;
            if (done[i])
                return world[i];
            var local = flver.Nodes[i].ComputeLocalTransform();
            var parent = flver.Nodes[i].ParentIndex;
            world[i] = parent >= 0 && parent < world.Length && parent != i ? local * World(parent, depth + 1) : local;
            done[i] = true;
            return world[i];
        }
        for (int i = 0; i < world.Length; i++)
            World(i, 0);
        return world;
    }

    /// <summary>
    /// Moves every vertex of the model into model space and makes every mesh a plain unskinned one. Returns how many
    /// meshes had vertices on a node other than an untransformed one.
    /// </summary>
    public static int BakeNodes(FLVER2 flver)
    {
        var world = new Matrix4x4[flver.Nodes.Count];
        var done = new bool[flver.Nodes.Count];
        Matrix4x4 World(int i, int depth = 0)
        {
            if (i < 0 || i >= world.Length || depth > 64)
                return Matrix4x4.Identity;
            if (done[i])
                return world[i];
            var local = flver.Nodes[i].ComputeLocalTransform();
            var parent = flver.Nodes[i].ParentIndex;
            world[i] = parent >= 0 && parent < world.Length && parent != i ? local * World(parent, depth + 1) : local;
            done[i] = true;
            return world[i];
        }
        // The root is where an unskinned mesh ends up; were it transformed, everything is expressed relative to it.
        var rootInverse = Matrix4x4.Identity;
        if (flver.Nodes.Count > 0 && !World(0).IsIdentity && !Matrix4x4.Invert(World(0), out rootInverse))
            rootInverse = Matrix4x4.Identity;

        int moved = 0;
        foreach (var mesh in flver.Meshes)
        {
            if (!mesh.UseBoneWeights)
            {
                bool any = false;
                foreach (var v in mesh.Vertices)
                {
                    int index = v.NormalWs.Count > 0 ? v.NormalWs[0] : 0;
                    int node = index >= 0 && index < mesh.BoneIndices.Count ? mesh.BoneIndices[index] : index;
                    if (node < 0 || node >= flver.Nodes.Count)
                        node = mesh.NodeIndex;
                    var m = World(node) * rootInverse;
                    if (m.IsIdentity)
                        continue;
                    any = true;
                    v.Position = Vector3.Transform(v.Position, m);
                    v.Normal = SafeNormal(Vector3.TransformNormal(v.Normal, m), v.Normal);
                    for (int t = 0; t < v.Tangents.Count; t++)
                    {
                        var tangent = v.Tangents[t];
                        var xyz = SafeNormal(Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), m), new Vector3(tangent.X, tangent.Y, tangent.Z));
                        v.Tangents[t] = new Vector4(xyz, tangent.W);
                    }
                    var b = SafeNormal(Vector3.TransformNormal(new Vector3(v.Bitangent.X, v.Bitangent.Y, v.Bitangent.Z), m), new Vector3(v.Bitangent.X, v.Bitangent.Y, v.Bitangent.Z));
                    v.Bitangent = new Vector4(b, v.Bitangent.W);
                }
                if (any)
                    moved++;
            }
            // Either way the mesh is now a rigid one on the root (MakeStatic then clears its node references).
            mesh.UseBoneWeights = false;
            mesh.NodeIndex = 0;
            mesh.BoneIndices = [];
            foreach (var v in mesh.Vertices)
                for (int i = 0; i < v.NormalWs.Count; i++)
                    v.NormalWs[i] = 0;
        }
        return moved;
    }

    private static Vector3 SafeNormal(Vector3 v, Vector3 fallback)
    {
        var length = v.Length();
        return length > 1e-6f && float.IsFinite(length) ? v / length : fallback;
    }

    /// <summary>The folder a model's FLVER sits in inside its binder: the template's, renamed to the new model.</summary>
    private static string ModelFolder(BinderFile templateFile, MapId erId, string modelNumber, MapId? hostId)
    {
        var templateLeaf = ItemText.Leaf(templateFile.Name);
        var folder = templateFile.Name[..^templateLeaf.Length];
        var templateStem = templateLeaf[..templateLeaf.IndexOf('.')];
        // The folder can itself name the template model ("...\m45_00_00_00_000100\sib\").
        folder = folder.Replace(templateStem, $"{erId}_{modelNumber}", StringComparison.OrdinalIgnoreCase);
        if (hostId is { } host && host != erId)
            folder = folder.Replace(host.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);
        return folder;
    }

    /// <summary>
    /// Piece test "vanilla-model" (see MapPorter.PieceTest): the host's own vanilla map piece binder under another
    /// model's name. The FLVER's bytes are Elden Ring's, untouched; only the file's name inside the binder changes.
    /// </summary>
    public static BND4 VanillaBinder(BND4 hostTemplate, MapId erId, string modelNumber, MapId? hostId = null)
    {
        var templateFile = hostTemplate.Files.First(f => FLVER2.Is(f.Bytes));
        var name = ModelFolder(templateFile, erId, modelNumber, hostId) + $"{erId}_{modelNumber}.flver";
        return new BND4
        {
            Version = hostTemplate.Version,
            Format = hostTemplate.Format,
            BigEndian = hostTemplate.BigEndian,
            BitBigEndian = hostTemplate.BitBigEndian,
            Unicode = hostTemplate.Unicode,
            Extended = hostTemplate.Extended,
            Unk04 = hostTemplate.Unk04,
            Unk05 = hostTemplate.Unk05,
            Files = [new BinderFile(templateFile.Flags, templateFile.ID, name, templateFile.Bytes)],
        };
    }

    /// <param name="model">The map piece's model number ("000080"), to find its placements in <see cref="OverlayBacking"/>; null for objects.</param>
    private void ConvertFlver(FLVER2 flver, MapId ds3Id, string? model = null)
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
        // Overlays and two-layer materials, while the DS3 materials and vertex colours are still there.
        var keepAlpha = new HashSet<FLVER2.Mesh>();
        if (Ds3Alpha)
        {
            var stats = new MapOverlays.Stats();
            var behind = CutFadedOverlays ? MapOverlays.CutEverywhere
                : model is not null && OverlayBacking is { } backing ? backing.For(model)
                : null;
            keepAlpha = MapOverlays.Apply(flver, stats, behind, FadeOverlays);
            Overlays.Add(stats);
        }
        // Terrain blends: one mesh per layer it shows (the weights are still in DS3's vertex layout here).
        if (TerrainLayers)
        {
            var stats = new MapTerrainBlends.Stats();
            MapTerrainBlends.Apply(flver, stats);
            Blends.Add(stats);
        }
        if (TintMaterials)
            // A material shared by a faded overlay and a solid mesh: the overlay gets its own copy, so it can be told apart.
            foreach (var mesh in keepAlpha)
            {
                var m = flver.Materials[mesh.MaterialIndex];
                var name = m.Name + FadeSuffix;
                int index = flver.Materials.FindIndex(x => x.Name == name);
                if (index < 0)
                {
                    flver.Materials.Add(new FLVER2.Material { Name = name, MTD = m.MTD, GXIndex = m.GXIndex, Index = m.Index,
                        Textures = m.Textures.Select(t => new FLVER2.Texture(t.ParamName, t.Path, t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C)).ToList() });
                    index = flver.Materials.Count - 1;
                }
                mesh.MaterialIndex = index;
            }
        var ds3Layouts = flver.BufferLayouts.ToList();
        flver.Skeletons = new FLVER2.SkeletonSet { BaseSkeleton = Bones(flver), AllSkeletons = Bones(flver) };

        // One GX list and one vertex layout per template kind, in a fixed order.
        var kinds = Kinds.Where(_templates.ContainsKey).ToList();
        flver.GXLists = kinds.Select(k => _templates[k].GxList).ToList();
        flver.BufferLayouts = kinds.Select(k => CopyLayout(_templates[k].Layout)).ToList();

        // Meshes whose DS3 material nothing here can draw are left out (see Ds3Kind).
        var meshTemplates = new Dictionary<FLVER2.Mesh, MaterialTemplate>();
        var dropped = new List<string>();
        foreach (var mesh in flver.Meshes.ToList())
        {
            var m = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < flver.Materials.Count ? flver.Materials[mesh.MaterialIndex] : null;
            if (m is not null && TemplateFor(Ds3Kind(m)) is { } t)
                meshTemplates[mesh] = t;
            else
            {
                flver.Meshes.Remove(mesh);
                dropped.Add(m is null ? "(no material)" : Path.GetFileNameWithoutExtension(ItemText.Leaf(m.MTD ?? "")));
            }
        }

        // Only the materials a remaining mesh uses are kept, so a left-out mesh adds no MATBIN and no texture.
        var used = flver.Meshes.Select(mesh => mesh.MaterialIndex).Distinct().OrderBy(i => i).ToList();
        flver.Materials = used.Select(i => flver.Materials[i]).ToList();
        foreach (var mesh in flver.Meshes)
            mesh.MaterialIndex = used.IndexOf(mesh.MaterialIndex);

        // Binders convert in parallel; the shared material table, texture list and notes are changed under a lock.
        lock (_materials)
        {
        foreach (var d in dropped)
            LeftOut[d] = LeftOut.GetValueOrDefault(d) + 1;
        foreach (var m in flver.Materials)
        {
            var template = TemplateFor(Ds3Kind(m)) ?? _templates["opaque"];
            var kind = template.Kind;
            // Water is Elden Ring's own material as it is: its look (normal maps, colour) lives in its MATBIN.
            if (VanillaMaterials || kind == "water")
            {
                var vanilla = template.Material;
                m.Name = vanilla.Name;
                m.MTD = vanilla.MTD;
                m.GXIndex = kinds.IndexOf(kind);
                m.Textures = vanilla.Textures
                    .Select(t => new FLVER2.Texture(t.ParamName, t.Path, t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C))
                    .ToList();
                continue;
            }

            static string? NameOf(FLVER2.Texture? t) => t is null ? null : Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path));
            var albedoTexture = Ds3Texture(m, "albedo");
            var albedo = NameOf(albedoTexture);
            if (TintMaterials && TintFor(m) is { } tint)
                albedo = tint;
            var normal = NameOf(Ds3Texture(m, "normal"));
            var scale = albedoTexture?.TilingScale ?? Vector2.One;
            if (scale.X <= 0 || scale.Y <= 0)
                scale = Vector2.One;
            if (albedo is null)
                Notes.Add($"{ItemText.Leaf(m.MTD)} ({m.Name}) has no albedo texture; it keeps the template's");
            if (!TemplateTextures)
                foreach (var t in new[] { albedo, normal })
                    if (t is not null)
                        Textures.Add(t);

            var key = string.Join("|", template.Matbin, albedo, normal, scale.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                scale.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            if (!_materials.TryGetValue(key, out var material))
            {
                // Named from what it holds, not from the order binders converted in, so every build names it the same.
                // A DS3 material with no normal map (sky domes, some foliage) gets a flat one, not the template's own.
                material = new NewMaterial($"DKS_{ds3Id.Digits}_{StableHash(key)}", template, albedo ?? "", normal ?? FlatNormal,
                    null, null, scale, TemplateTextures);
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
        }

        // Every mesh in its material's vertex layout, with the culling box Elden Ring reads.
        foreach (var mesh in flver.Meshes)
        {
            var template = meshTemplates[mesh];
            var sourceLayouts = mesh.VertexBuffers.Where(b => b.LayoutIndex >= 0 && b.LayoutIndex < ds3Layouts.Count)
                .SelectMany(b => ds3Layouts[b.LayoutIndex]).ToList();
            Conform(mesh, template.Layout, kinds.IndexOf(template.Kind), sourceLayouts, keepAlpha.Contains(mesh));
            SetBoundingBox(mesh, flver.Header.Version);
        }
    }

    private static FLVER2.BufferLayout CopyLayout(IEnumerable<FLVER.LayoutMember> members)
    {
        var layout = new FLVER2.BufferLayout();
        layout.AddRange(members.Select(m => new FLVER.LayoutMember(m.Type, m.Semantic, m.Index, 0, 0)));
        return layout;
    }

    /// <summary>How many UVs of a vertex one layout member holds (SoulsFormats writes two for the four-component types).</summary>
    public static int UvCount(FLVER.LayoutType type) =>
        type is FLVER.LayoutType.Float4 or FLVER.LayoutType.Short4 or FLVER.LayoutType.Half4 or FLVER.LayoutType.UByte4Norm ? 2 : 1;

    // Short UVs hold +-16 (32768 / 2048) in Elden Ring's FLVER version.
    private const float ShortUvLimit = 15.99f;

    /// <summary>
    /// Rebuilds a mesh's vertices for one vertex buffer in <paramref name="layout"/>: the tangent, colour and UV
    /// counts the layout's members write, vertex colours white (the way JortPob writes them; DS3 keeps blend weights
    /// there, which Elden Ring's one-layer shaders would read as a tint), and the first UV channel brought into the
    /// range a short UV holds. DS3's float UVs on multi-layer ground are in short units (divided by 2048 here); a
    /// channel still out of range is moved by whole tiles, which a tiling texture doesn't show.
    /// </summary>
    /// <param name="keepAlpha">Keep the vertex's own colour alpha (a DS3 overlay's fade, see <see cref="MapOverlays"/>).</param>
    public static void Conform(FLVER2.Mesh mesh, FLVER2.BufferLayout layout, int layoutIndex, IReadOnlyList<FLVER.LayoutMember>? source = null,
        bool keepAlpha = false)
    {
        int uvs = layout.Where(m => m.Semantic == FLVER.LayoutSemantic.UV).Sum(m => UvCount(m.Type));
        int tangents = layout.Count(m => m.Semantic == FLVER.LayoutSemantic.Tangent);
        int colors = layout.Count(m => m.Semantic == FLVER.LayoutSemantic.VertexColor);
        bool shortUv = layout.Any(m => m.Semantic == FLVER.LayoutSemantic.UV && m.Type is not (FLVER.LayoutType.Float2 or FLVER.LayoutType.Float3 or FLVER.LayoutType.Float4));
        bool floatSource = source?.FirstOrDefault(m => m.Semantic == FLVER.LayoutSemantic.UV)?.Type
            is FLVER.LayoutType.Float2 or FLVER.LayoutType.Float3 or FLVER.LayoutType.Float4;

        foreach (var v in mesh.Vertices)
        {
            if (v.Positions.Count == 0)
                v.Positions.Add(Vector3.Zero);
            if (v.Positions.Count > 1)
                v.Positions.RemoveRange(1, v.Positions.Count - 1);
            if (v.Normals.Count == 0)
                v.Normals.Add(Vector3.UnitY);
            if (v.Normals.Count > 1)
                v.Normals.RemoveRange(1, v.Normals.Count - 1);
            v.NormalWs.Clear();
            v.NormalWs.Add(0);
            if (v.Tangents.Count == 0)
            {
                // Any unit vector across the normal: the meshes that lack one in DS3 are unlit (sky domes).
                var n = v.Normals[0];
                var t = Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
                t = t.LengthSquared() > 1e-8f ? Vector3.Normalize(t) : Vector3.UnitX;
                v.Tangents.Add(new Vector4(t, 1));
            }
            while (v.Tangents.Count < tangents)
                v.Tangents.Add(v.Tangents[0]);
            if (v.Tangents.Count > tangents)
                v.Tangents.RemoveRange(tangents, v.Tangents.Count - tangents);
            float alpha = keepAlpha ? Math.Clamp(MapOverlays.Alpha(v), 0f, 1f) : 1f;
            v.Colors.Clear();
            for (int i = 0; i < colors; i++)
                // What vanilla map piece vertices hold in this channel: red 1, green and blue 0, alpha 1 (all 67,000
                // vertices of two Stormveil pieces and the colosseum's; alpha alone varies, fading edges out).
                v.Colors.Add(new FLVER.VertexColor(alpha, 1f, 0f, 0f));
            if (v.UVs.Count == 0)
                v.UVs.Add(Vector3.Zero);
            // Only the first channel means the same in both games (DS3's others are lightmap and blend channels).
            if (v.UVs.Count > 1)
                v.UVs.RemoveRange(1, v.UVs.Count - 1);
            while (v.UVs.Count < uvs)
                v.UVs.Add(Vector3.Zero);
        }
        if (shortUv && mesh.Vertices.Count > 0)
        {
            float Max() => mesh.Vertices.Max(v => MathF.Max(MathF.Abs(v.UVs[0].X), MathF.Abs(v.UVs[0].Y)));
            if (floatSource && Max() > ShortUvLimit)
                foreach (var v in mesh.Vertices)
                    v.UVs[0] = new Vector3(v.UVs[0].X / 2048f, v.UVs[0].Y / 2048f, 0);
            if (Max() > ShortUvLimit)
            {
                var shift = new Vector2(
                    MathF.Round((mesh.Vertices.Min(v => v.UVs[0].X) + mesh.Vertices.Max(v => v.UVs[0].X)) / 2),
                    MathF.Round((mesh.Vertices.Min(v => v.UVs[0].Y) + mesh.Vertices.Max(v => v.UVs[0].Y)) / 2));
                foreach (var v in mesh.Vertices)
                    v.UVs[0] = new Vector3(
                        Math.Clamp(v.UVs[0].X - shift.X, -ShortUvLimit, ShortUvLimit),
                        Math.Clamp(v.UVs[0].Y - shift.Y, -ShortUvLimit, ShortUvLimit), 0);
            }
        }
        mesh.VertexBuffers = [new FLVER2.VertexBuffer(layoutIndex)];
    }

    /// <summary>
    /// The mesh's culling box. DS3 stores a mesh's minimum and maximum corner; Elden Ring's FLVERs (version 0x2001A)
    /// store half the box's size, its rotation and its centre in the same place (read off vanilla map pieces: the
    /// colosseum's and Stormveil's meshes all hold extent, ~0, centre). SoulsFormats names the three Min, Max, Unk.
    /// </summary>
    public static void SetBoundingBox(FLVER2.Mesh mesh, int version)
    {
        if (mesh.Vertices.Count == 0)
            return;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in mesh.Vertices)
        {
            min = Vector3.Min(min, v.Positions[0]);
            max = Vector3.Max(max, v.Positions[0]);
        }
        mesh.BoundingBox = version >= 0x2001A
            ? new FLVER2.Mesh.BoundingBoxes { Min = (max - min) / 2, Max = Vector3.Zero, Unk = (max + min) / 2 }
            : new FLVER2.Mesh.BoundingBoxes { Min = min, Max = max };
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
            foreach (var sampler in material.TemplateTextures ? [] : matbin.Samplers)
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
                // DS3 tiles a texture per material (a sky dome's 3 x 1); Elden Ring's sampler carries the same scale
                // (JortPob sets it for its terrain). Left alone at 1 x 1, as vanilla materials have it.
                if (Role(sampler.Type) is ("albedo" or "normal", 0) && material.Scale != default && material.Scale != Vector2.One)
                    sampler.Unk14 = material.Scale;
            }
            if (material.Template.Kind == "nolight" && !UnlitCastShadows)
            {
                if (matbin.Params.FirstOrDefault(p => p.Name == NoShadowParam && p.Value is bool) is { } noShadow)
                    noShadow.Value = true;
                else
                    Notes.Add($"{material.Template.Matbin} has no {NoShadowParam} switch: {material.Name} (unlit, a sky dome) still casts shadows");
            }
            matbin.SourcePath = ReplaceStem(matbin.SourcePath, material.Template.Matbin, material.Name);
            var name = ReplaceStem(material.Template.File.Name, material.Template.Matbin, material.Name);
            yield return new BinderFile(material.Template.File.Flags, id++, name, matbin.Write());
        }
    }
}
