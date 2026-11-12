using System.Numerics;
using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// What DS3 keeps in a map piece's vertex colour alpha, and how a ported mesh keeps it. Up to 1.0.340 every ported
/// vertex was written fully solid and every two-layer material showed its first layer, which drew dark brick shards
/// across the ash around Firelink Shrine (map 1, Iudex Gundyr's circle and the steps up to the shrine).
/// <para>
/// Measured on m40's pieces (m000052, m000071, m000079, m000080; 2026-11-12):
/// <list type="bullet">
/// <item><b>Overlays.</b> One-layer map materials (M[ARSN]_l and the like) draw solid geometry with alpha 1 on every
/// vertex (cliffs, rocks). A paving or rubble layer laid over the ground is a separate mesh whose alpha fades it into
/// what lies under it: m000079's brick meshes have 37% of their vertices under 0.1 and a mean of 0.28. Elden Ring's
/// own map pieces keep the same channel (alpha alone varies, fading edges out), so the alpha is passed on, and the
/// triangles whose three corners are all under <see cref="OverlayCut"/> (at most half visible in DS3) are left out,
/// so an overlay can't stand solid where DS3 shows the ground under it even if the Elden Ring shader ignores the
/// channel.</item>
/// <item><b>Two-layer materials</b> (M[ARSN]_m, _l_m, _l_p_m, _p_m: GXFlver_ColDifSpcBumpMulMask): alpha is the
/// weight of the first layer (g_DiffuseTexture on UV 1) over the second (g_DiffuseTexture2 on UV 2, its own tiling).
/// m000080's ash-covered brick has alpha 0 on all 55,785 vertices: it is ash (the second layer, m40_00_o329222_332)
/// with the plain brick overlay above it fading in. Elden Ring has no matching two-layer map shader in use (one mesh
/// in all of Stormveil names [Mb2], and it blends by mask), so each triangle takes the layer its corners mostly
/// show: the mesh is split in two, the second half drawn with the second layer's textures, UVs and tangents.</item>
/// </list>
/// Terrain blends (_2m/_3m/_4m, GSBlendMap: alpha 1 everywhere), grass (vertex colour sways it), snow, water and
/// scrolling materials keep their alpha meaning to themselves and are not touched.
/// </para>
/// </summary>
public static class MapOverlays
{
    /// <summary>A triangle whose three corners are all below this alpha is left out of an overlay mesh.</summary>
    public const float OverlayCut = 0.5f;

    /// <summary>A two-layer triangle whose corners average at least this alpha shows the first layer.</summary>
    public const float FirstLayerFrom = 0.5f;

    public enum Kind { None, Overlay, TwoLayer }

    /// <summary>The name suffix of a two-layer material's second-layer copy.</summary>
    public const string SecondLayerSuffix = "_2";

    /// <summary>True for a DS3 two-layer map material type ("M[ARSN]_l_m").</summary>
    public static bool IsTwoLayerMtd(string mtd) => TwoLayerMtd.IsMatch(mtd);

    // DS3's two-layer shaders (ColDifSpcBumpMulMask): "M[ARSN]_m", "M[ARSN]_l_m", "M[ARSN]_l_p_m", "M[ARSN]_l_m_snow".
    private static readonly Regex TwoLayerMtd = new(@"_m(?![a-z0-9])", RegexOptions.IgnoreCase);
    // Alpha means something else (terrain blends 2m/3m/4m, grass sway, snow, water, scrolling, parallax, translucency).
    private static readonly Regex OtherAlpha = new(@"\dm|grass|snow|water|scroll|_p(?![a-z])|_tr(?![a-z])|NoLight", RegexOptions.IgnoreCase);

    /// <summary>What a DS3 map material keeps in vertex alpha.</summary>
    public static Kind KindOf(FLVER2.Material material)
    {
        var mtd = Path.GetFileNameWithoutExtension(ItemText.Leaf(material.MTD ?? ""));
        if (TwoLayerMtd.IsMatch(mtd))
            return material.Textures.Any(t => t.ParamName == "g_DiffuseTexture2" && t.Path.Length > 0) ? Kind.TwoLayer : Kind.None;
        if (OtherAlpha.IsMatch(mtd) || !mtd.StartsWith("M[", StringComparison.OrdinalIgnoreCase))
            return Kind.None;
        return Kind.Overlay;
    }

    /// <summary>What <see cref="Apply"/> did to one model, for the port log.</summary>
    public sealed class Stats
    {
        public int OverlayMeshes, OverlayTrianglesCut, OverlayTriangles, TwoLayerMeshes, SecondLayerTriangles, TwoLayerTriangles;

        public void Add(Stats other)
        {
            Interlocked.Add(ref OverlayMeshes, other.OverlayMeshes);
            Interlocked.Add(ref OverlayTrianglesCut, other.OverlayTrianglesCut);
            Interlocked.Add(ref OverlayTriangles, other.OverlayTriangles);
            Interlocked.Add(ref TwoLayerMeshes, other.TwoLayerMeshes);
            Interlocked.Add(ref SecondLayerTriangles, other.SecondLayerTriangles);
            Interlocked.Add(ref TwoLayerTriangles, other.TwoLayerTriangles);
        }

        public string Summary =>
            $"vertex alpha: {OverlayMeshes} overlay meshes keep DS3's fade ({OverlayTrianglesCut:N0} of {OverlayTriangles:N0} triangles under half visible left out); "
            + $"{TwoLayerMeshes} two-layer meshes split by layer ({SecondLayerTriangles:N0} of {TwoLayerTriangles:N0} triangles show the second layer)";
    }

    /// <summary>
    /// Rewrites a DS3 FLVER's overlay and two-layer meshes in place (before the materials are converted) and returns
    /// the meshes whose vertex alpha is a fade the converted vertices should keep.
    /// </summary>
    public static HashSet<FLVER2.Mesh> Apply(FLVER2 flver, Stats stats)
    {
        var keepAlpha = new HashSet<FLVER2.Mesh>();
        var secondLayer = new Dictionary<int, int>();   // material index -> its second-layer copy
        foreach (var mesh in flver.Meshes.ToList())
        {
            if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count || mesh.Vertices.Count == 0)
                continue;
            var material = flver.Materials[mesh.MaterialIndex];
            switch (KindOf(material))
            {
                case Kind.Overlay:
                    if (mesh.Vertices.All(v => Alpha(v) >= 0.999f))
                        break;
                    var (cut, total) = Keep(mesh, tri => tri.Any(i => Alpha(mesh.Vertices[i]) >= OverlayCut));
                    if (mesh.Vertices.Count == 0)
                        flver.Meshes.Remove(mesh);   // nothing of it is more than half visible in DS3
                    else
                        keepAlpha.Add(mesh);
                    stats.OverlayMeshes++;
                    stats.OverlayTrianglesCut += cut;
                    stats.OverlayTriangles += total;
                    break;

                case Kind.TwoLayer:
                    if (!secondLayer.TryGetValue(mesh.MaterialIndex, out var second))
                    {
                        flver.Materials.Add(SecondLayer(material));
                        secondLayer[mesh.MaterialIndex] = second = flver.Materials.Count - 1;
                    }
                    static bool First(FLVER2.Mesh m, int[] tri) => tri.Average(i => Alpha(m.Vertices[i])) >= FirstLayerFrom;
                    var copy = Copy(mesh);
                    var (toSecond, all) = Keep(mesh, tri => First(mesh, tri));
                    Keep(copy, tri => !First(copy, tri));
                    stats.TwoLayerMeshes++;
                    stats.SecondLayerTriangles += toSecond;
                    stats.TwoLayerTriangles += all;
                    if (copy.FaceSets.Any(f => f.Indices.Count > 0))
                    {
                        copy.MaterialIndex = second;
                        foreach (var v in copy.Vertices)
                        {
                            // The second layer's UV (DS3's UV 2) and tangent take the first's place.
                            if (v.UVs.Count > 1)
                                v.UVs[0] = v.UVs[1];
                            if (v.Tangents.Count > 1)
                                v.Tangents[0] = v.Tangents[1];
                        }
                        flver.Meshes.Insert(flver.Meshes.IndexOf(mesh) + 1, copy);
                    }
                    if (mesh.FaceSets.All(f => f.Indices.Count == 0))
                        flver.Meshes.Remove(mesh);
                    break;
            }
        }
        return keepAlpha;
    }

    public static float Alpha(FLVER.Vertex v) => v.Colors.Count > 0 ? v.Colors[0].A : 1f;

    /// <summary>A copy of a two-layer material that shows its second layer through the first layer's texture slots.</summary>
    public static FLVER2.Material SecondLayer(FLVER2.Material m)
    {
        var copy = new FLVER2.Material { Name = m.Name + SecondLayerSuffix, MTD = m.MTD, GXIndex = m.GXIndex, Index = m.Index };
        foreach (var t in m.Textures)
        {
            // "g_DiffuseTexture2" is now the first slot; "g_DOLTexture2" has no "g_DOLTexture" and stays as it is.
            if (t.ParamName.EndsWith('2') && m.Textures.Any(o => o.ParamName == t.ParamName[..^1]))
                continue;
            var second = m.Textures.FirstOrDefault(o => o.ParamName == t.ParamName + "2" && o.Path.Length > 0);
            copy.Textures.Add(Clone(second ?? t, t.ParamName));
        }
        return copy;
    }

    private static FLVER2.Texture Clone(FLVER2.Texture t, string param) =>
        new(param, t.Path, t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C);

    /// <summary>
    /// Keeps only the triangles <paramref name="keep"/> accepts in every face set (as triangle lists) and drops the
    /// vertices no face set uses any more. Returns (triangles left out, triangles before) for the full detail sets.
    /// </summary>
    private static (int Cut, int Total) Keep(FLVER2.Mesh mesh, Func<int[], bool> keep)
    {
        int cut = 0, total = 0;
        bool restarts = mesh.Vertices.Count < 0xFFFF;
        foreach (var set in mesh.FaceSets)
        {
            var tris = set.Triangulate(restarts);
            var kept = new List<int>(tris.Count);
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                int[] tri = [tris[i], tris[i + 1], tris[i + 2]];
                if (tri.Any(x => x < 0 || x >= mesh.Vertices.Count))
                    continue;
                bool ok = keep(tri);
                if (set.Flags == FLVER2.FaceSet.FSFlags.None)
                {
                    total++;
                    if (!ok)
                        cut++;
                }
                if (ok)
                    kept.AddRange(tri);
            }
            set.Indices = kept;
            set.TriangleStrip = false;
        }
        Compact(mesh);
        return (cut, total);
    }

    /// <summary>Drops vertices no face set names and renumbers the indices.</summary>
    private static void Compact(FLVER2.Mesh mesh)
    {
        var map = new int[mesh.Vertices.Count];
        Array.Fill(map, -1);
        var vertices = new List<FLVER.Vertex>();
        foreach (var set in mesh.FaceSets)
            for (int i = 0; i < set.Indices.Count; i++)
            {
                int old = set.Indices[i];
                if (map[old] < 0)
                {
                    map[old] = vertices.Count;
                    vertices.Add(mesh.Vertices[old]);
                }
                set.Indices[i] = map[old];
            }
        mesh.Vertices = vertices;
    }

    private static FLVER2.Mesh Copy(FLVER2.Mesh mesh) => new()
    {
        UseBoneWeights = mesh.UseBoneWeights,
        MaterialIndex = mesh.MaterialIndex,
        NodeIndex = mesh.NodeIndex,
        BoneIndices = [.. mesh.BoneIndices],
        BoundingBox = mesh.BoundingBox,
        VertexBuffers = mesh.VertexBuffers.Select(b => new FLVER2.VertexBuffer(b.LayoutIndex)).ToList(),
        FaceSets = mesh.FaceSets.Select(f => new FLVER2.FaceSet(f.Flags, f.TriangleStrip, f.CullBackfaces, f.Unk06, [.. f.Indices])).ToList(),
        Vertices = mesh.Vertices.Select(v => new FLVER.Vertex(v)).ToList(),
    };
}
