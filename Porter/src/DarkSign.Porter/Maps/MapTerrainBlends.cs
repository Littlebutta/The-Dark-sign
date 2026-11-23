using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's terrain blends (M[ARSN]_2m/_3m/_4m: MultiBlend2/3/4_et1, "GSBlendMap" textures) paint two to four ground
/// textures across one mesh. Up to 1.0.353 every such mesh showed only its first layer, and that layer is the dark
/// brick under the ash around Firelink Shrine: a test build that coloured each family (port-maps --tint-materials,
/// 2026-11-12) showed every dark shard of map 1 in the terrain-blend colour, between the ash overlays.
/// <para>
/// Measured on m40 (m000051, m000053, m000080, m000091, m004001, m900285; dump read with SoulsFormats):
/// <list type="bullet">
/// <item>Every layer samples the first UV with its own tiling (the MTDs give AlbedoMap_0..3 "UV 1").</item>
/// <item>The weights are the third UV member, four unsigned normalized bytes (SoulsFormats reads it as two UV entries:
/// c0, c1 and c2, c3). A mesh with N layers uses N - 1 of them: the 3m wall m900285 keeps c2 at 0 on all but 1% of its
/// vertices, every mesh keeps c3 at 0, and the bytes don't add up to one (sums run from 0 to 3), so they are not
/// shares of a whole.</item>
/// <item>So layer k (from 1) is painted over the layers below it with weight c[k-1], in order: what m900285's
/// (1, 1, 0, 0) corners show is layer 2. The edge texture (BlendEdgeTexture) sharpens each step around half
/// strength, so a layer with weight of at least <see cref="LayerFrom"/> covers the ones below it.</item>
/// </list>
/// Elden Ring has no matching shader in use (one mesh in all of Stormveil names [Mb2], none [Mb3] or [Mb4], and
/// those blend by a mask texture and a different per-vertex value), so each spot is drawn with the one layer its
/// weights show: the mesh is split into one mesh per layer, each with that layer's albedo, normal map and tiling in
/// the first layer's texture slots. A triangle whose corners disagree is cut along the line where the weight passes
/// <see cref="LayerFrom"/> (<see cref="MapLayerContours"/>); up to 1.0.46x it went whole to one layer, which drew the
/// edges of painted patches as jagged shards (the Undead Settlement's sand paths).
/// </para>
/// </summary>
public static class MapTerrainBlends
{
    /// <summary>A layer covers the layers below it where its weight is at least this.</summary>
    public const float LayerFrom = 0.5f;

    /// <summary>The name suffix of a terrain blend's copy for layer k (from 1): "_L1".</summary>
    public const string LayerSuffix = "_L";

    // "M[ARSN]_4m_l", "MultiBlend\M[ARSN]_2m_et1_l", "M[ARSN]_3m": the digit is the number of layers.
    private static readonly Regex BlendMtd = new(@"_([2-4])m(?![a-z])", RegexOptions.IgnoreCase);
    // Unlit or scrolling "2m" materials are sky domes (m009000 of m31 and m40: "M[A]_2m_NoLight_scroll"), whose two
    // layers are cloud textures the sky shader cross-fades and scrolls, not ground painted per vertex. Splitting one by
    // its blend bytes drew the 1.0.366 dome as a patchwork of sky and cloud triangles (blue shards over map 3's church).
    private static readonly Regex NotGround = new(@"NoLight|scroll", RegexOptions.IgnoreCase);
    private static readonly Regex FirstLayerSlot = new(@"(GSBlendMap_[A-Za-z]+Map_)0$", RegexOptions.IgnoreCase);

    /// <summary>
    /// How many layers a DS3 terrain blend material type has, or 0 for any other material (sky domes included: their
    /// layers keep the first one, as every build up to 1.0.353 drew them).
    /// </summary>
    public static int LayerCount(string mtd)
    {
        var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(mtd ?? ""));
        var m = BlendMtd.Match(name);
        return m.Success && !NotGround.IsMatch(name) ? m.Groups[1].Value[0] - '0' : 0;
    }

    /// <summary>The layer (from 1) a material is the copy for, or 0 for a terrain blend's own material.</summary>
    public static int LayerOf(FLVER2.Material m)
    {
        int at = m.Name.LastIndexOf(LayerSuffix, StringComparison.Ordinal);
        return at >= 0 && int.TryParse(m.Name[(at + LayerSuffix.Length)..], out int k) && k is > 0 and < 4 ? k : 0;
    }

    /// <summary>What <see cref="Apply"/> did, for the port log.</summary>
    public sealed class Stats
    {
        public int Meshes, Triangles, NoWeights, Cut;
        public readonly int[] LayerTriangles = new int[4];

        public void Add(Stats other)
        {
            Interlocked.Add(ref Meshes, other.Meshes);
            Interlocked.Add(ref Triangles, other.Triangles);
            Interlocked.Add(ref NoWeights, other.NoWeights);
            Interlocked.Add(ref Cut, other.Cut);
            for (int i = 0; i < LayerTriangles.Length; i++)
                Interlocked.Add(ref LayerTriangles[i], other.LayerTriangles[i]);
        }

        public string Summary =>
            $"terrain blends: {Meshes} meshes drawn by layer, {Triangles:N0} triangles: "
            + string.Join(", ", LayerTriangles.Select((n, i) => $"layer {i + 1} {n:N0}"))
            + $"; {Cut:N0} DS3 triangles cut along a layer's edge"
            + (NoWeights > 0 ? $"; {NoWeights} without blend weights show their first layer" : "");
    }

    /// <summary>
    /// Splits every terrain blend mesh of a DS3 FLVER (still with DS3's vertex layouts and materials) into one mesh
    /// per layer it shows. Layer 0 stays in the mesh itself; the others are copies with their own material.
    /// </summary>
    public static void Apply(FLVER2 flver, Stats stats)
    {
        var layerMaterials = new Dictionary<(int Material, int Layer), int>();
        foreach (var mesh in flver.Meshes.ToList())
        {
            if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count || mesh.Vertices.Count == 0)
                continue;
            var material = flver.Materials[mesh.MaterialIndex];
            int layers = LayerCount(material.MTD);
            if (layers < 2)
                continue;
            int entry = WeightEntry(flver, mesh);
            if (entry < 0 || mesh.Vertices.Any(v => v.UVs.Count < entry + 2))
            {
                stats.NoWeights++;
                continue;
            }
            // Layers with no albedo can't be drawn; their triangles go to the layer under them.
            var drawable = Enumerable.Range(0, layers).Select(k => k == 0 || Slot(material, "AlbedoMap", k) is not null).ToArray();
            // Each layer's share of the mesh, cut along the lines where a weight crosses half (MapLayerContours).
            var split = MapLayerContours.Split(mesh, layers, (v, k) => Weight(v, entry, k - 1), k => drawable[k], LayerFrom, true, out int cut);
            var copies = new List<FLVER2.Mesh>();
            for (int k = 1; k < layers; k++)
            {
                if (split[k].All(f => f.Count == 0))
                    continue;
                if (!layerMaterials.TryGetValue((mesh.MaterialIndex, k), out int index))
                {
                    flver.Materials.Add(Layer(material, k));
                    layerMaterials[(mesh.MaterialIndex, k)] = index = flver.Materials.Count - 1;
                }
                stats.LayerTriangles[k] += MapLayerContours.Triangles(mesh, split[k]);
                var copy = MapOverlays.Copy(mesh);
                MapOverlays.Use(copy, split[k]);
                copy.MaterialIndex = index;
                copies.Add(copy);
            }
            stats.Meshes++;
            stats.Cut += cut;
            stats.LayerTriangles[0] += MapLayerContours.Triangles(mesh, split[0]);
            stats.Triangles += split.Sum(part => MapLayerContours.Triangles(mesh, part));
            MapOverlays.Use(mesh, split[0]);
            flver.Meshes.InsertRange(flver.Meshes.IndexOf(mesh) + 1, copies);
            if (mesh.FaceSets.All(f => f.Indices.Count == 0))
                flver.Meshes.Remove(mesh);
        }
    }

    /// <summary>Whether a DS3 material type is a two-layer sky dome ("M[A]_2m_NoLight_scroll"), not ground.</summary>
    public static bool IsSkyBlend(string mtd)
    {
        var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(mtd ?? ""));
        return BlendMtd.Match(name) is { Success: true } m && m.Groups[1].Value == "2" && NotGround.IsMatch(name);
    }

    /// <summary>What <see cref="ApplySky"/> did, for the port log.</summary>
    public sealed class SkyStats
    {
        public int Meshes, SecondLayer, OwnUv;
        public readonly List<string> Shown = [];

        public void Add(SkyStats other)
        {
            Interlocked.Add(ref Meshes, other.Meshes);
            Interlocked.Add(ref SecondLayer, other.SecondLayer);
            Interlocked.Add(ref OwnUv, other.OwnUv);
            lock (Shown)
                Shown.AddRange(other.Shown);
        }

        public string Summary =>
            $"sky domes: {Meshes} two-layer meshes, {SecondLayer} show their second layer ({OwnUv} on its own texture coordinates, DS3's UV 2)"
            + (Shown.Count > 0 ? " (" + string.Join(", ", Shown.Distinct().Take(12)) + ")" : "");
    }

    /// <summary>
    /// A DS3 sky dome ("M[A]_2m_NoLight_scroll") cross-fades two textures by the same per-vertex weight c0 a terrain
    /// blend uses (layer 2 covers layer 1 where c0 is at least half), and its sky shader scrolls them. Elden Ring has
    /// no such shader in use, and splitting the dome by weight drew it as shards (1.0.366, see <see cref="NotGround"/>),
    /// so the whole mesh shows one texture. Up to 1.0.50x that was always the first. That suits m31 (sky_a under
    /// cloud_04, c0 under half on most of it) and m40 (sky_53 under sky_54), but the Painted World's dome (m45
    /// m090000) has its clouds first (kumo_30) and its sky second (sky_01, c0 0.64 on average): drawn unlit over the
    /// whole sky, the cloud picture's shapes stood above the map as huge grey blurs (Devin, 2026-11-19, "a large cloud
    /// looking building hovering above the map"). So each dome mesh now shows the layer that covers most of its
    /// vertices: the second where c0 is at least half on more than half of them.
    /// <para>
    /// The second picture has texture coordinates of its own: DS3's UV 2 (the second UV entry; c0 sits in the entry
    /// after it). The Painted World's dome (m090000, measured 2026-11-20): UV 1 is the cloud picture's scrolling tile
    /// (its v stays 0.50 from the bottom of the dome to the top), UV 2 runs the panorama sky_01 (4096 x 1024: the
    /// mountains at the bottom, the starry dark at the top) from v 1.00 at the horizon to 0.18 at 2.8 km up. 1.0.52x
    /// drew sky_01 on UV 1, so the whole dome showed one smeared middle row of it: the streaky cyan sky under a
    /// starry cap in Devin's 2026-11-20 screenshot.
    /// </para>
    /// </summary>
    public static void ApplySky(FLVER2 flver, SkyStats stats)
    {
        var layerMaterials = new Dictionary<int, int>();
        foreach (var mesh in flver.Meshes)
        {
            if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count || mesh.Vertices.Count == 0)
                continue;
            var material = flver.Materials[mesh.MaterialIndex];
            if (!IsSkyBlend(material.MTD))
                continue;
            stats.Meshes++;
            int entry = WeightEntry(flver, mesh);
            if (entry < 0 || mesh.Vertices.Any(v => v.UVs.Count < entry + 1) || Slot(material, "AlbedoMap", 1) is null)
                continue;
            int covered = mesh.Vertices.Count(v => Weight(v, entry, 0) >= LayerFrom);
            if (covered * 2 <= mesh.Vertices.Count)
                continue;
            if (!layerMaterials.TryGetValue(mesh.MaterialIndex, out int index))
            {
                flver.Materials.Add(Layer(material, 1));
                layerMaterials[mesh.MaterialIndex] = index = flver.Materials.Count - 1;
            }
            mesh.MaterialIndex = index;
            // The second picture's own texture coordinates (DS3's UV 2) take the first's place.
            if (entry >= 2)
            {
                foreach (var v in mesh.Vertices)
                    v.UVs[0] = v.UVs[1];
                stats.OwnUv++;
            }
            stats.SecondLayer++;
            stats.Shown.Add(Path.GetFileNameWithoutExtension(ItemText.Leaf(Slot(material, "AlbedoMap", 1)!.Path)));
        }
    }

    /// <summary>
    /// The index of the vertex UV entry that holds c0 and c1 (c2 is the entry after it): the first UV member of four
    /// normalized bytes in the mesh's DS3 vertex layouts, counted in UV entries as SoulsFormats reads them. -1 if none.
    /// </summary>
    public static int WeightEntry(FLVER2 flver, FLVER2.Mesh mesh)
    {
        int entry = 0;
        foreach (var buffer in mesh.VertexBuffers)
        {
            if (buffer.LayoutIndex < 0 || buffer.LayoutIndex >= flver.BufferLayouts.Count)
                return -1;
            foreach (var member in flver.BufferLayouts[buffer.LayoutIndex].Where(m => m.Semantic == FLVER.LayoutSemantic.UV))
            {
                if (member.Type == FLVER.LayoutType.UByte4Norm)
                    return entry;
                entry += MapModelPort.UvCount(member.Type);
            }
        }
        return -1;
    }

    /// <summary>Weight c[j] of a vertex: c0, c1 in one UV entry, c2, c3 in the next.</summary>
    public static float Weight(FLVER.Vertex v, int entry, int j)
    {
        var uv = v.UVs[entry + j / 2];
        return j % 2 == 0 ? uv.X : uv.Y;
    }

    private static FLVER2.Texture? Slot(FLVER2.Material m, string role, int layer) =>
        m.Textures.FirstOrDefault(t => t.ParamName.EndsWith($"GSBlendMap_{role}_{layer}", StringComparison.OrdinalIgnoreCase) && t.Path.Length > 0);

    /// <summary>A copy of a terrain blend material that shows layer k through the first layer's texture slots.</summary>
    public static FLVER2.Material Layer(FLVER2.Material m, int layer)
    {
        var copy = new FLVER2.Material { Name = m.Name + LayerSuffix + layer, MTD = m.MTD, GXIndex = m.GXIndex, Index = m.Index };
        foreach (var t in m.Textures)
        {
            var first = FirstLayerSlot.Match(t.ParamName);
            var own = first.Success
                ? m.Textures.FirstOrDefault(o => o.ParamName.EndsWith(first.Groups[1].Value + layer, StringComparison.OrdinalIgnoreCase) && o.Path.Length > 0)
                : null;
            copy.Textures.Add(MapOverlays.Clone(own ?? t, t.ParamName));
        }
        return copy;
    }
}
