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
/// show: the mesh is split in two, the second half drawn with the second layer's textures, UVs and tangents. A triangle
/// whose corners disagree is cut along the line where the first layer's weight passes <see cref="FirstLayerFrom"/>
/// (<see cref="MapLayerContours"/>).
/// <para>Up to 1.0.681 the weight was the vertex alpha alone. But DS3 mixes the layers per pixel by a blend mask
/// texture (<see cref="MapMasks"/>), and most two-layer meshes have one alpha on every vertex (0 on the Road of
/// Sacrifices' floor), so a whole floor showed its second layer: moss with none of DS3's brick. The weight is now
/// <see cref="FirstLayerWeight"/>, the mask read where each vertex lies.</para></item>
/// </list>
/// Terrain blends (_2m/_3m/_4m, GSBlendMap: alpha 1 everywhere) are drawn by layer in <see cref="MapTerrainBlends"/>; grass (vertex colour sways it), snow, water and
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
        public int TwoLayerMeshes, SecondLayerTriangles, TwoLayerTriangles;
        /// <summary>Two-layer meshes whose blend mask was not found: split by vertex alpha alone.</summary>
        public int NoMaskMeshes;
        /// <summary>Triangles added to masked meshes so the cut can follow the mask (<see cref="Refine"/>).</summary>
        public int RefinedTriangles;
        /// <summary>One-layer meshes with vertex alpha under 1 drawn solid.</summary>
        public int SolidOverlayMeshes;

        public void Add(Stats other)
        {
            Interlocked.Add(ref SolidOverlayMeshes, other.SolidOverlayMeshes);
            Interlocked.Add(ref TwoLayerMeshes, other.TwoLayerMeshes);
            Interlocked.Add(ref SecondLayerTriangles, other.SecondLayerTriangles);
            Interlocked.Add(ref TwoLayerTriangles, other.TwoLayerTriangles);
            Interlocked.Add(ref NoMaskMeshes, other.NoMaskMeshes);
            Interlocked.Add(ref RefinedTriangles, other.RefinedTriangles);
        }

        public string Summary =>
            $"vertex alpha: {SolidOverlayMeshes} one-layer meshes with vertex alpha under 1 drawn solid (DS3 draws them); "
            + $"{TwoLayerMeshes} two-layer meshes split by layer ({SecondLayerTriangles:N0} of {TwoLayerTriangles:N0} triangles show the second layer; "
            + $"{TwoLayerMeshes - NoMaskMeshes} by DS3's blend mask, {NoMaskMeshes} by vertex alpha alone, their mask not found; "
            + $"{RefinedTriangles:N0} triangles added so the cuts follow the masks)";
    }

    /// <summary>
    /// Rewrites a DS3 FLVER's two-layer meshes in place (before the materials are converted): each triangle takes the
    /// layer its corners show, cut along the line between them (<see cref="MapLayerContours"/>). One-layer meshes are
    /// left as they are, drawn solid: their vertex alpha is not a fade (Firelink Shrine's tunnel walls, Undead
    /// Settlement's houses). Returns the meshes whose vertex alpha the converted vertices should keep (none here).
    /// </summary>
    public static HashSet<FLVER2.Mesh> Apply(FLVER2 flver, Stats stats, Func<string, MapMasks.Mask?>? masks = null)
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
                    if (mesh.Vertices.Any(v => Alpha(v) < 0.999f))
                        stats.SolidOverlayMeshes++;
                    break;

                case Kind.TwoLayer:
                    if (!secondLayer.TryGetValue(mesh.MaterialIndex, out var second))
                    {
                        flver.Materials.Add(SecondLayer(material));
                        secondLayer[mesh.MaterialIndex] = second = flver.Materials.Count - 1;
                    }
                    // Part 1 is the first layer (its weight at least half), part 0 the second; cut along the line between them.
                    var mask = material.Textures.FirstOrDefault(t => t.ParamName == MaskParam && t.Path.Length > 0) is { } maskTexture
                        ? masks?.Invoke(maskTexture.Path) : null;
                    if (mask is null)
                        stats.NoMaskMeshes++;
                    // The mask can only be followed where the mesh has vertices: a slab top of two triangles would
                    // be cut into one long wedge, so sides the mask could change along are halved first.
                    if (mask is not null && mesh.Vertices.Any(v => Alpha(v) < FirstLayerFrom))
                        stats.RefinedTriangles += Refine(mesh, MaskSide);
                    int level = mask?.LevelFor(MaskStep(mesh)) ?? 0;
                    var split = MapLayerContours.Split(mesh, 2, (v, _) => FirstLayerWeight(v, mask, level), _ => true, FirstLayerFrom, true, out _);
                    int toSecond = MapLayerContours.Triangles(mesh, split[0]);
                    var copy = Copy(mesh);
                    stats.TwoLayerMeshes++;
                    stats.SecondLayerTriangles += toSecond;
                    stats.TwoLayerTriangles += toSecond + MapLayerContours.Triangles(mesh, split[1]);
                    Use(mesh, split[1]);
                    Use(copy, split[0]);
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

    /// <summary>The texture slot of a two-layer material's blend mask.</summary>
    public const string MaskParam = "g_BlendMaskTexture";

    /// <summary>
    /// The UV a two-layer vertex reads its blend mask at: the last set (after both layers' and, on _l_ materials, the
    /// lightmap's), or the first when the vertex has no set to itself.
    /// </summary>
    public static Vector2 MaskUv(FLVER.Vertex v)
    {
        if (v.UVs.Count == 0)
            return default;
        var uv = v.UVs[v.UVs.Count >= 3 ? v.UVs.Count - 1 : 0];
        return new Vector2(uv.X, uv.Y);
    }

    /// <summary>
    /// How much of a two-layer vertex is its first layer: one minus the second's, which is the blend mask there
    /// held back by the vertex alpha (alpha 1 = first layer whatever the mask says). Without a mask the vertex alpha
    /// alone decides, as it did up to 1.0.681.
    /// </summary>
    public static float FirstLayerWeight(FLVER.Vertex v, MapMasks.Mask? mask, int level) =>
        1 - (mask?.Sample(MaskUv(v), level) ?? 1f) * (1 - Alpha(v));

    /// <summary>
    /// The detail a mesh can carry of its blend mask: half the median length of its triangles' sides, in mask UV.
    /// </summary>
    public static float MaskStep(FLVER2.Mesh mesh)
    {
        var set = mesh.FaceSets.FirstOrDefault(f => f.Flags == FLVER2.FaceSet.FSFlags.None) ?? mesh.FaceSets.FirstOrDefault();
        if (set is null)
            return 0;
        var tris = set.Triangulate(mesh.Vertices.Count < 0xFFFF);
        var lengths = new List<float>();
        int stride = Math.Max(1, tris.Count / 3 / 2000) * 3;
        for (int i = 0; i + 2 < tris.Count; i += stride)
            for (int e = 0; e < 3; e++)
            {
                int a = tris[i + e], b = tris[i + (e + 1) % 3];
                if (a < 0 || b < 0 || a >= mesh.Vertices.Count || b >= mesh.Vertices.Count || a == b)
                    continue;
                lengths.Add(Vector2.Distance(MaskUv(mesh.Vertices[a]), MaskUv(mesh.Vertices[b])));
            }
        if (lengths.Count == 0)
            return 0;
        lengths.Sort();
        return lengths[lengths.Count / 2] / 2;
    }

    /// <summary>A masked two-layer triangle's sides are halved until none is longer than this (metres).</summary>
    public const float MaskSide = 0.5f;

    /// <summary>
    /// Halves every triangle side longer than <paramref name="side"/>, in every face set, until none is left (at
    /// most four times, and not past four times the triangles of a large mesh). Whether a side is halved depends on its length
    /// alone, so the triangles on both sides of it agree and no gap opens. Returns the full-detail triangles added.
    /// </summary>
    public static int Refine(FLVER2.Mesh mesh, float side)
    {
        bool restarts = mesh.Vertices.Count < 0xFFFF;
        var lists = mesh.FaceSets.Select(f => f.Triangulate(restarts).ToList()).ToList();
        int valid = mesh.Vertices.Count;
        for (int s = 0; s < lists.Count; s++)
        {
            var clean = new List<int>(lists[s].Count);
            for (int i = 0; i + 2 < lists[s].Count; i += 3)
                if (lists[s][i] >= 0 && lists[s][i + 1] >= 0 && lists[s][i + 2] >= 0 && lists[s][i] < valid && lists[s][i + 1] < valid && lists[s][i + 2] < valid)
                    clean.AddRange([lists[s][i], lists[s][i + 1], lists[s][i + 2]]);
            lists[s] = clean;
        }
        int Full() => mesh.FaceSets.Select((f, s) => f.Flags == FLVER2.FaceSet.FSFlags.None ? lists[s].Count / 3 : 0).Sum();
        int before = Full(), limit = Math.Max(before * 4, 4096);
        var mids = new Dictionary<(int, int), int>();
        float sq = side * side;
        int Mid(int a, int b)
        {
            if (Vector3.DistanceSquared(mesh.Vertices[a].Position, mesh.Vertices[b].Position) <= sq)
                return -1;
            var key = a < b ? (a, b) : (b, a);
            if (!mids.TryGetValue(key, out int at))
            {
                mids[key] = at = mesh.Vertices.Count;
                mesh.Vertices.Add(MapLayerContours.Lerp(mesh.Vertices[key.Item1], mesh.Vertices[key.Item2], 0.5f));
            }
            return at;
        }
        bool changed = false;
        for (int pass = 0; pass < 4 && Full() < limit; pass++)
        {
            bool any = false;
            for (int s = 0; s < lists.Count; s++)
            {
                var tris = lists[s];
                var next = new List<int>(tris.Count * 2);
                for (int i = 0; i + 2 < tris.Count; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    int n = (ab >= 0 ? 1 : 0) + (bc >= 0 ? 1 : 0) + (ca >= 0 ? 1 : 0);
                    if (n == 0)
                    {
                        next.AddRange([a, b, c]);
                        continue;
                    }
                    any = true;
                    // Turn the triangle so the halved sides come first.
                    if (n == 1 && bc >= 0 || n == 2 && ab < 0)
                        (a, b, c, ab, bc, ca) = (b, c, a, bc, ca, ab);
                    else if (n == 1 && ca >= 0 || n == 2 && bc < 0)
                        (a, b, c, ab, bc, ca) = (c, a, b, ca, ab, bc);
                    if (n == 3)
                        next.AddRange([a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca]);
                    else if (n == 2)
                        next.AddRange([ab, b, bc, a, ab, bc, a, bc, c]);
                    else
                        next.AddRange([a, ab, c, ab, b, c]);
                }
                lists[s] = next;
            }
            if (!any)
                break;
            changed = true;
        }
        if (!changed)
            return 0;
        for (int s = 0; s < lists.Count; s++)
        {
            mesh.FaceSets[s].Indices = lists[s];
            mesh.FaceSets[s].TriangleStrip = false;
        }
        return Full() - before;
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

    internal static FLVER2.Texture Clone(FLVER2.Texture t, string param) =>
        new(param, t.Path, t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C);

    /// <summary>
    /// Keeps only the triangles <paramref name="keep"/> accepts in every face set (as triangle lists) and drops the
    /// vertices no face set uses any more. Returns (triangles left out, triangles before) for the full detail sets.
    /// </summary>
    internal static (int Cut, int Total) Keep(FLVER2.Mesh mesh, Func<int[], bool> keep)
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

    /// <summary>Gives each face set its new triangle list and drops the vertices none of them uses.</summary>
    internal static void Use(FLVER2.Mesh mesh, List<int>[] triangles)
    {
        for (int s = 0; s < mesh.FaceSets.Count; s++)
        {
            mesh.FaceSets[s].Indices = [.. triangles[s]];
            mesh.FaceSets[s].TriangleStrip = false;
        }
        Compact(mesh);
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

    internal static FLVER2.Mesh Copy(FLVER2.Mesh mesh) => new()
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
