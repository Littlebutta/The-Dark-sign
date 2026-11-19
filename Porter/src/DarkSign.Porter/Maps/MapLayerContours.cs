using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Cuts a DS3 mesh that paints several textures by a per-vertex weight along the line where a weight crosses its
/// threshold, so each side of the line can be drawn with its own texture.
/// <para>
/// From 1.0.354 a terrain blend (<see cref="MapTerrainBlends"/>) or two-layer material (<see cref="MapOverlays"/>)
/// gave each whole triangle the layer its three corners showed on average. DS3's ground triangles are one to two
/// metres wide, so the edge of a painted patch (a sand path across earth in the Undead Settlement, 1.0.46x) followed
/// triangle edges: pale, jagged, straight-sided shards on the floor. DS3 itself changes layer where the weight,
/// interpolated across the triangle, passes about half (its edge texture sharpens the step there), so the port now
/// does the same in geometry: a triangle the boundary crosses is cut along the line where the interpolated weight
/// equals the threshold, and each part goes to its layer.
/// </para>
/// <para>
/// The cut is watertight: a cut point on an edge depends only on that edge's two ends (taken in a fixed order), so
/// the triangles on both sides of the edge get the same point, and no part ends up with a vertex in the middle of
/// one of its sides.
/// </para>
/// </summary>
internal static class MapLayerContours
{
    /// <summary>A cut closer to an end of an edge than this share of its length uses that end's vertex.</summary>
    public const float Snap = 1e-3f;

    /// <summary>
    /// Sorts every triangle of every face set of a mesh into <paramref name="parts"/> lists: part k (from the highest
    /// down to 1) takes what <paramref name="usable"/> allows and where <paramref name="level"/>(vertex, k) is at
    /// least <paramref name="from"/>; part 0 takes the rest. New vertices on the boundaries are added to the mesh.
    /// </summary>
    /// <param name="contours">False: no triangle is cut; each goes whole to the part its corners show on average
    /// (as 1.0.354 to 1.0.46x).</param>
    /// <param name="cut">How many full-detail triangles a boundary crossed.</param>
    /// <returns>Per part, the triangle list of each face set (indices into the mesh's vertices).</returns>
    public static List<int>[][] Split(FLVER2.Mesh mesh, int parts, Func<FLVER.Vertex, int, float> level, Func<int, bool> usable,
                                      float from, bool contours, out int cut)
    {
        var result = new List<int>[parts][];
        for (int k = 0; k < parts; k++)
            result[k] = mesh.FaceSets.Select(_ => new List<int>()).ToArray();
        // Each vertex's level per part. DS3 often has several vertices at one spot (seams) whose weights differ a
        // little; cut on their own values, the two sides of a seam would get different points on the same edge, so
        // they share the mean.
        var levels = new List<float>?[parts];
        if (contours)
        {
            var twins = mesh.Vertices.Select((v, i) => (v.Position, i)).GroupBy(x => x.Position, x => x.i).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
            for (int k = 1; k < parts; k++)
            {
                if (!usable(k))
                    continue;
                int part = k;
                var values = levels[k] = mesh.Vertices.Select(v => level(v, part)).ToList();
                foreach (var group in twins)
                {
                    float mean = group.Average(i => values[i]);
                    foreach (int i in group)
                        values[i] = mean;
                }
            }
        }
        var cuts = new Dictionary<(int, int, int), int>();
        bool restarts = mesh.Vertices.Count < 0xFFFF;
        int count = mesh.Vertices.Count;
        cut = 0;

        // The points cut into each edge, by how far along it they lie: an edge two parts share (a side of a DS3
        // triangle, or the line an upper layer was cut along) is cut again where a lower layer's boundary meets it,
        // and both parts must get that point.
        var mids = new Dictionary<(int, int), List<(float T, int At)>>();
        var pending = new List<(List<int> Into, List<int> Poly)>();

        (int Lo, int Hi) Ordered(int p, int q) =>
            Before(mesh.Vertices[p].Position, p, mesh.Vertices[q].Position, q) ? (p, q) : (q, p);

        int Cut(int p, int q, int k)
        {
            // The same two ends in the same order from either side of the edge (and for a seam's twin vertices).
            var (lo, hi) = Ordered(p, q);
            if (cuts.TryGetValue((lo, hi, k), out int at))
                return at;
            float a = levels[k]![lo], b = levels[k]![hi];
            float t = (from - a) / (b - a);
            if (!(t > Snap))
                at = lo;
            else if (!(t < 1 - Snap))
                at = hi;
            else
            {
                at = mesh.Vertices.Count;
                mesh.Vertices.Add(Lerp(mesh.Vertices[lo], mesh.Vertices[hi], t));
                foreach (var values in levels)
                    values?.Add(values[lo] + (values[hi] - values[lo]) * t);
                if (!mids.TryGetValue((lo, hi), out var list))
                    mids[(lo, hi)] = list = [];
                list.Add((t, at));
            }
            return cuts[(lo, hi, k)] = at;
        }

        // The cut points strictly between two ends of an edge, in order from p to q.
        void Between(int p, int q, List<int> into)
        {
            var (lo, hi) = Ordered(p, q);
            if (!mids.TryGetValue((lo, hi), out var list))
                return;
            var chain = new List<int> { lo };
            chain.AddRange(list.OrderBy(m => m.T).Select(m => m.At));
            chain.Add(hi);
            if (p != lo)
                chain.Reverse();
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                if (i > 0)
                    into.Add(chain[i]);
                Between(chain[i], chain[i + 1], into);
            }
        }

        float Corner(int a, int b, int c)
        {
            Vector3 u = mesh.Vertices[b].Position - mesh.Vertices[a].Position, w = mesh.Vertices[c].Position - mesh.Vertices[b].Position;
            float scale = u.Length() * w.Length();
            return scale > 0 ? Vector3.Cross(u, w).Length() / scale : 0;
        }

        void Emit(List<int> into, List<int> poly)
        {
            // Every point cut into one of its sides joins the polygon; twins from snapped cuts go.
            var p = new List<int>(poly.Count + 4);
            for (int i = 0; i < poly.Count; i++)
            {
                int next = poly[(i + 1) % poly.Count];
                if (poly[i] == next)
                    continue;
                p.Add(poly[i]);
                Between(poly[i], next, p);
            }
            // It is convex, with some points in the middle of straight sides: cut off real corners one by one, so no
            // triangle is flat and none has a point in the middle of a side.
            while (p.Count >= 3)
            {
                int best = 0;
                float sharpest = -1;
                for (int i = 0; i < p.Count && p.Count > 3; i++)
                {
                    float corner = Corner(p[(i + p.Count - 1) % p.Count], p[i], p[(i + 1) % p.Count]);
                    if (corner > sharpest)
                        (best, sharpest) = (i, corner);
                }
                int a = p[(best + p.Count - 1) % p.Count], b = p[best], c = p[(best + 1) % p.Count];
                if (a != b && b != c && a != c)
                    into.AddRange([a, b, c]);
                p.RemoveAt(best);
            }
        }

        for (int s = 0; s < mesh.FaceSets.Count; s++)
        {
            var set = mesh.FaceSets[s];
            var tris = set.Triangulate(restarts);
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                int[] tri = [tris[i], tris[i + 1], tris[i + 2]];
                if (tri.Any(x => x < 0 || x >= count))
                    continue;
                if (!contours)
                {
                    int shown = 0;
                    for (int k = parts - 1; k > 0 && shown == 0; k--)
                        if (usable(k) && tri.Average(x => level(mesh.Vertices[x], k)) >= from)
                            shown = k;
                    result[shown][s].AddRange(tri);
                    continue;
                }
                var poly = new List<int>(tri);
                bool crossed = false;
                for (int k = parts - 1; k > 0 && poly.Count >= 3; k--)
                {
                    if (!usable(k))
                        continue;
                    var inside = poly.Select(x => levels[k]![x] >= from).ToList();
                    if (inside.All(x => x))
                    {
                        pending.Add((result[k][s], poly));
                        poly = [];
                        break;
                    }
                    if (!inside.Any(x => x))
                        continue;
                    List<int> over = [], under = [];
                    for (int j = 0; j < poly.Count; j++)
                    {
                        int next = (j + 1) % poly.Count;
                        (inside[j] ? over : under).Add(poly[j]);
                        if (inside[j] != inside[next])
                        {
                            int x = Cut(poly[j], poly[next], k);
                            over.Add(x);
                            under.Add(x);
                        }
                    }
                    pending.Add((result[k][s], over));
                    poly = under;
                    crossed = true;
                }
                if (poly.Count >= 3)
                    pending.Add((result[0][s], poly));
                if (crossed && set.Flags == FLVER2.FaceSet.FSFlags.None)
                    cut++;
            }
        }
        // Only now, when every cut on every edge is known.
        foreach (var (into, poly) in pending)
            Emit(into, poly);
        return result;
    }

    /// <summary>How many triangles a part has in the mesh's full-detail face sets.</summary>
    public static int Triangles(FLVER2.Mesh mesh, List<int>[] part) =>
        mesh.FaceSets.Select((f, s) => f.Flags == FLVER2.FaceSet.FSFlags.None ? part[s].Count / 3 : 0).Sum();

    private static bool Before(Vector3 a, int ia, Vector3 b, int ib) =>
        a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z != b.Z ? a.Z < b.Z : ia < ib;

    /// <summary>The vertex a share <paramref name="t"/> of the way from one vertex to another, in DS3's own layout.</summary>
    public static FLVER.Vertex Lerp(FLVER.Vertex a, FLVER.Vertex b, float t)
    {
        var near = t < 0.5f ? a : b;
        var v = new FLVER.Vertex(a)
        {
            BoneIndices = near.BoneIndices,
            BoneWeights = near.BoneWeights,
        };
        for (int i = 0; i < v.Positions.Count && i < b.Positions.Count; i++)
            v.Positions[i] = Vector3.Lerp(a.Positions[i], b.Positions[i], t);
        for (int i = 0; i < v.Normals.Count && i < b.Normals.Count; i++)
            v.Normals[i] = Unit(Vector3.Lerp(a.Normals[i], b.Normals[i], t), near.Normals[i]);
        for (int i = 0; i < v.NormalWs.Count && i < near.NormalWs.Count; i++)
            v.NormalWs[i] = near.NormalWs[i];
        for (int i = 0; i < v.UVs.Count && i < b.UVs.Count; i++)
            v.UVs[i] = Vector3.Lerp(a.UVs[i], b.UVs[i], t);
        for (int i = 0; i < v.Tangents.Count && i < b.Tangents.Count; i++)
            v.Tangents[i] = Direction(a.Tangents[i], b.Tangents[i], near.Tangents[i], t);
        v.Bitangent = Direction(a.Bitangent, b.Bitangent, near.Bitangent, t);
        for (int i = 0; i < v.Colors.Count && i < b.Colors.Count; i++)
        {
            var (x, y) = (a.Colors[i], b.Colors[i]);
            v.Colors[i] = new FLVER.VertexColor(x.A + (y.A - x.A) * t, x.R + (y.R - x.R) * t, x.G + (y.G - x.G) * t, x.B + (y.B - x.B) * t);
        }
        return v;
    }

    private static Vector3 Unit(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;

    // A tangent: a unit direction, and a handedness that is the nearer end's.
    private static Vector4 Direction(Vector4 a, Vector4 b, Vector4 near, float t)
    {
        var d = Unit(Vector3.Lerp(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), t), new Vector3(near.X, near.Y, near.Z));
        return new Vector4(d, near.W);
    }
}
