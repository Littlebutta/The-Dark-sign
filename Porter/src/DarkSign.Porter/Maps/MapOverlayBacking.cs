using System.Collections.Concurrent;
using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Where a DS3 overlay's faded triangles have something to fade into. <see cref="MapOverlays"/> (PR #152) left out every
/// overlay triangle whose corners are all under half alpha, on the reading that DS3 shows the ground under it there.
/// That holds for the brick laid over the ash at Iudex Gundyr's circle, but not everywhere: measured on m40
/// (2026-11-12, every map piece of the map placed by its MSB part), the ash tiling of Firelink Shrine's floor
/// (m004600, 14% of its vertices under half) has nothing at all under the centre of the floor, and the cliff mesh of
/// m000053 (28% under half) has nothing under its outer slopes. Cut, they left holes the sky showed through (the
/// 1.0.37x playtest: "the floor in the Firelink shrine failed to render"). Under the brick at the circle, by contrast,
/// the ash lies 2 to 30 cm below.
/// <para>
/// So a faded triangle is left out only when a surface the port draws lies right behind it, within <see cref="Reach"/>
/// along its normal, at its centre and near each corner, in every placement of its model; otherwise it is kept, solid.
/// A "surface" is any opaque mesh that is not itself a fading overlay: plain ground, two-layer ground, terrain blends,
/// cliffs and rocks with alpha 1 everywhere.
/// </para>
/// <para>
/// A map is checked in two passes over its models, and nothing but the faded triangles is kept in memory:
/// <see cref="Collect"/> gathers each model's faded triangles and turns them into short test segments in the map
/// (behind each sample point); <see cref="Seal"/> files the segments by cell; <see cref="File"/> runs every surface
/// triangle of every model past the segments in its cells and marks the ones it crosses. The port collects during its
/// first read of each model and converts models with faded triangles after the second pass. Without a map
/// (<see cref="Of"/>: objects, test models) a model is checked against its own surfaces.
/// </para>
/// </summary>
public sealed class MapOverlayBacking
{
    /// <summary>How far behind a faded triangle (along its normal) the surface it fades into may lie, in metres.</summary>
    public const float Reach = 0.5f;

    /// <summary>How far in front of a faded triangle a surface still counts (one laid a hair above it, or coplanar).</summary>
    public const float Lift = 0.05f;

    /// <summary>Sample points per faded triangle: its centre and three points toward its corners.</summary>
    private const int Samples = 4;

    private const float CellSize = 1f;

    /// <summary>Fine cells per side of a coarse cell, the first look for a surface triangle (most are nowhere near a faded one).</summary>
    private const int Coarse = 8;

    private readonly IReadOnlyDictionary<string, Matrix4x4[]> _placements;
    private readonly ConcurrentDictionary<string, List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)>> _collected = new(StringComparer.OrdinalIgnoreCase);

    // After Seal: one entry per faded triangle per model, its segments (Samples per placement) from First on.
    private Dictionary<string, Dictionary<(Vector3, Vector3, Vector3), int>>? _first;
    private Vector3[] _from = [], _to = [];
    private int[] _hit = [];
    private Dictionary<long, int[]> _cells = new();
    private HashSet<long> _coarse = new();
    private long _surfaces;

    public MapOverlayBacking(IReadOnlyDictionary<string, Matrix4x4[]> placements) => _placements = placements;

    /// <summary>The models placed in the map (what <see cref="File"/> must see).</summary>
    public IEnumerable<string> Models => _placements.Keys;

    /// <summary>Faded overlay triangles collected (once per model).</summary>
    public int Faded => _first?.Values.Sum(d => d.Count) ?? _collected.Values.Sum(l => l.Count);

    /// <summary>Faded triangles that have a surface behind them in every placement (left out by <see cref="MapOverlays"/>).</summary>
    public int Backed => _first is null ? 0 : _first.Sum(d => { int n = SegmentCount(d.Key); return d.Value.Values.Count(f => AllHit(f, n)); });

    public string Summary =>
        $"overlay backing: {Faded:N0} faded overlay triangles in {_collected.Count(c => c.Value.Count > 0)} models, {Backed:N0} with a surface "
        + $"behind them (left out), {Faded - Backed:N0} without (kept solid); {_surfaces:N0} surface triangles checked";

    /// <summary>True when the mesh is a surface a faded overlay can fade into: drawn opaque, and not an overlay that fades itself.</summary>
    public static bool IsSurface(FLVER2.Material material, FLVER2.Mesh mesh) =>
        MapModelPort.Ds3Kind(material) == "opaque" && !IsFading(material, mesh);

    /// <summary>True for an overlay mesh whose vertex alpha fades it (what <see cref="MapOverlays.Apply"/> may cut).</summary>
    public static bool IsFading(FLVER2.Material material, FLVER2.Mesh mesh) =>
        MapOverlays.KindOf(material) == MapOverlays.Kind.Overlay && mesh.Vertices.Any(v => MapOverlays.Alpha(v) < 0.999f);

    /// <summary>True when a DS3 model has a fading overlay mesh.</summary>
    public static bool HasFading(FLVER2 flver) =>
        flver.Meshes.Any(m => m.MaterialIndex >= 0 && m.MaterialIndex < flver.Materials.Count && IsFading(flver.Materials[m.MaterialIndex], m));

    /// <summary>A part's place in the map: scaled, turned (degrees, X then Z then Y, as DSMapStudio places DS3 parts) and moved.</summary>
    public static Matrix4x4 Placement(MSB3.Part part)
    {
        const float rad = MathF.PI / 180;
        var r = part.Rotation;
        var scale = part.Scale == Vector3.Zero ? Vector3.One : part.Scale;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(r.X * rad) * Matrix4x4.CreateRotationZ(r.Z * rad)
               * Matrix4x4.CreateRotationY(r.Y * rad) * Matrix4x4.CreateTranslation(part.Position);
    }

    /// <summary>A model checked against its own surfaces only (no map: objects, test models). Key "".</summary>
    public static MapOverlayBacking Of(FLVER2 flver)
    {
        var backing = new MapOverlayBacking(new Dictionary<string, Matrix4x4[]> { [""] = [Matrix4x4.Identity] });
        backing.Collect("", [flver]);
        backing.Seal();
        backing.File("", [flver]);
        return backing;
    }

    /// <summary>
    /// The whole check for a map in one call: <paramref name="read"/> gives a model's DS3 FLVERs (read twice). The port
    /// itself calls <see cref="Collect"/> during its own first read instead.
    /// </summary>
    public static MapOverlayBacking Build(IReadOnlyDictionary<string, Matrix4x4[]> placements, Func<string, IEnumerable<FLVER2>> read, int threads = 0)
    {
        var backing = new MapOverlayBacking(placements);
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount };
        Parallel.ForEach(placements.Keys, options, model => backing.Collect(model, read(model)));
        backing.Seal();
        Parallel.ForEach(placements.Keys, options, model => backing.File(model, read(model)));
        return backing;
    }

    /// <summary>
    /// Pass 1 (thread-safe): gathers a model's faded triangles, before any conversion changes it. Returns true when it
    /// has any (the port converts such a model only after <see cref="File"/> has seen every model).
    /// </summary>
    public bool Collect(string model, IEnumerable<FLVER2> flvers)
    {
        if (_first is not null)
            throw new InvalidOperationException("collect before Seal");
        var found = new List<(Vector3, Vector3, Vector3, Vector3)>();
        foreach (var flver in flvers)
            found.AddRange(FadedTriangles(flver));
        if (found.Count > 0)
            _collected.AddOrUpdate(model, found, (_, known) => { lock (known) known.AddRange(found); return known; });
        return found.Count > 0;
    }

    /// <summary>Ends pass 1: every faded triangle becomes test segments in the map, filed by cell.</summary>
    public void Seal()
    {
        var first = new Dictionary<string, Dictionary<(Vector3, Vector3, Vector3), int>>(StringComparer.OrdinalIgnoreCase);
        var from = new List<Vector3>();
        var to = new List<Vector3>();
        foreach (var (model, triangles) in _collected)
        {
            if (!_placements.TryGetValue(model, out var at) || at.Length == 0)
                continue;
            var mine = first[model] = new Dictionary<(Vector3, Vector3, Vector3), int>();
            foreach (var (a, b, c, normal) in triangles)
            {
                if (mine.ContainsKey((a, b, c)))
                    continue;
                mine[(a, b, c)] = from.Count;
                foreach (var m in at)
                    foreach (var (f, t) in Segments(Vector3.Transform(a, m), Vector3.Transform(b, m), Vector3.Transform(c, m), Vector3.TransformNormal(normal, m)))
                    {
                        from.Add(f);
                        to.Add(t);
                    }
            }
        }
        _from = from.ToArray();
        _to = to.ToArray();
        _hit = new int[_from.Length];
        var cells = new Dictionary<long, List<int>>();
        for (int s = 0; s < _from.Length; s++)
        {
            if (!float.IsFinite(_from[s].X + _from[s].Y + _from[s].Z + _to[s].X + _to[s].Y + _to[s].Z))
                continue;   // a degenerate triangle: its segments never hit, so it is kept
            var key = Key((_from[s] + _to[s]) / 2);
            if (!cells.TryGetValue(key, out var list))
                cells[key] = list = [];
            list.Add(s);
        }
        _cells = cells.ToDictionary(c => c.Key, c => c.Value.ToArray());
        _coarse = new HashSet<long>();
        for (int s = 0; s < _from.Length; s++)
            if (float.IsFinite(_from[s].X + _from[s].Y + _from[s].Z + _to[s].X + _to[s].Y + _to[s].Z))
            {
                var mid = (_from[s] + _to[s]) / 2;
                _coarse.Add(Key(Up(Cell(mid.X)), Up(Cell(mid.Y)), Up(Cell(mid.Z))));
            }
        _first = first;
    }

    /// <summary>Pass 2 (thread-safe): runs a model's surface triangles, in every placement, past the segments near them.</summary>
    public void File(string model, IEnumerable<FLVER2> flvers)
    {
        if (_first is null)
            throw new InvalidOperationException("File after Seal");
        if (_cells.Count == 0 || !_placements.TryGetValue(model, out var at))
            return;
        // A segment is at most Reach + Lift long (Reach both ways when the normal is unknown) and filed by its middle.
        var pad = new Vector3(Reach + CellSize * 0.01f);
        long checkedHere = 0;
        foreach (var flver in flvers)
            foreach (var mesh in flver.Meshes)
            {
                if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count || mesh.Vertices.Count == 0
                    || !IsSurface(flver.Materials[mesh.MaterialIndex], mesh))
                    continue;
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var v in mesh.Vertices)
                {
                    min = Vector3.Min(min, v.Position);
                    max = Vector3.Max(max, v.Position);
                }
                Vector3[]? world = null;
                List<(int, int, int)>? triangles = null;
                foreach (var m in at)
                {
                    // The whole mesh first: most placements are nowhere near a faded triangle.
                    var (lo, hi) = Bounds(min, max, m);
                    if (!Near(lo - pad, hi + pad))
                        continue;
                    world ??= new Vector3[mesh.Vertices.Count];
                    for (int v = 0; v < world.Length; v++)
                        world[v] = Vector3.Transform(mesh.Vertices[v].Position, m);
                    triangles ??= Triangles(mesh).ToList();
                    foreach (var (i, j, k) in triangles)
                    {
                        checkedHere++;
                        var (a, b, c) = (world[i], world[j], world[k]);
                        Check(a, b, c, Vector3.Min(a, Vector3.Min(b, c)) - pad, Vector3.Max(a, Vector3.Max(b, c)) + pad);
                    }
                }
            }
        Interlocked.Add(ref _surfaces, checkedHere);
    }

    // Marks the segments in the fine cells of [lo, hi] that cross the triangle, visiting only coarse cells with segments.
    private void Check(Vector3 a, Vector3 b, Vector3 c, Vector3 lo, Vector3 hi)
    {
        if (!float.IsFinite(lo.X + lo.Y + lo.Z + hi.X + hi.Y + hi.Z))
            return;
        int x0 = Cell(lo.X), x1 = Cell(hi.X), y0 = Cell(lo.Y), y1 = Cell(hi.Y), z0 = Cell(lo.Z), z1 = Cell(hi.Z);
        for (int cx = Up(x0); cx <= Up(x1); cx++)
            for (int cy = Up(y0); cy <= Up(y1); cy++)
                for (int cz = Up(z0); cz <= Up(z1); cz++)
                {
                    if (!_coarse.Contains(Key(cx, cy, cz)))
                        continue;
                    int xa = Math.Max(x0, cx * Coarse), xb = Math.Min(x1, cx * Coarse + Coarse - 1);
                    int ya = Math.Max(y0, cy * Coarse), yb = Math.Min(y1, cy * Coarse + Coarse - 1);
                    int za = Math.Max(z0, cz * Coarse), zb = Math.Min(z1, cz * Coarse + Coarse - 1);
                    for (int x = xa; x <= xb; x++)
                        for (int y = ya; y <= yb; y++)
                            for (int z = za; z <= zb; z++)
                            {
                                if (!_cells.TryGetValue(Key(x, y, z), out var list))
                                    continue;
                                foreach (int s in list)
                                    if (Volatile.Read(ref _hit[s]) == 0 && Crosses(_from[s], _to[s] - _from[s], a, b, c))
                                        Volatile.Write(ref _hit[s], 1);
                            }
                }
    }

    // True when a box (map space) reaches a coarse cell with segments in it.
    private bool Near(Vector3 lo, Vector3 hi)
    {
        if (!float.IsFinite(lo.X + lo.Y + lo.Z + hi.X + hi.Y + hi.Z))
            return false;
        int x0 = Up(Cell(lo.X)), x1 = Up(Cell(hi.X)), y0 = Up(Cell(lo.Y)), y1 = Up(Cell(hi.Y)), z0 = Up(Cell(lo.Z)), z1 = Up(Cell(hi.Z));
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > _coarse.Count)
            return true;    // a big mesh: cheaper to look triangle by triangle
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    if (_coarse.Contains(Key(x, y, z)))
                        return true;
        return false;
    }

    // The map-space box around a model-space box.
    private static (Vector3 Lo, Vector3 Hi) Bounds(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var p = Vector3.Transform(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z), m);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        return (lo, hi);
    }

    private static int Up(int cell) => (int)Math.Floor(cell / (double)Coarse);

    /// <summary>
    /// The model-space test <see cref="MapOverlays.Apply"/> takes, for one model (its number, e.g. "000080"; "" for
    /// <see cref="Of"/>): true when a collected faded triangle has a surface behind it in every placement. A triangle
    /// that wasn't collected (not placed, or a lower detail list) has nothing known behind it and is kept.
    /// </summary>
    public Func<Vector3, Vector3, Vector3, Vector3, bool> For(string model)
    {
        if (_first is null || !_first.TryGetValue(model, out var mine))
            return (_, _, _, _) => false;
        int count = SegmentCount(model);
        return (a, b, c, _) => mine.TryGetValue((a, b, c), out int first) && AllHit(first, count);
    }

    // A triangle's segments: Samples per placement of its model, from its first on.
    private bool AllHit(int first, int count)
    {
        for (int s = first; s < first + count; s++)
            if (Volatile.Read(ref _hit[s]) == 0)
                return false;
        return count > 0;
    }

    private int SegmentCount(string model) => _placements.TryGetValue(model, out var at) ? at.Length * Samples : 0;

    /// <summary>
    /// The test segments behind a faded triangle in the map: from just in front of each sample point to
    /// <see cref="Reach"/> behind it along the normal (its vertices' mean normal; both ways when that is zero).
    /// </summary>
    private static IEnumerable<(Vector3 From, Vector3 To)> Segments(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        bool oneSided = normal.LengthSquared() > 1e-8f;
        var n = oneSided ? normal : Vector3.Cross(b - a, c - a);
        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : new Vector3(float.NaN);
        var centre = (a + b + c) / 3;
        foreach (var p in new[] { centre, Vector3.Lerp(centre, a, 0.6f), Vector3.Lerp(centre, b, 0.6f), Vector3.Lerp(centre, c, 0.6f) })
            yield return (p + n * (oneSided ? Lift : Reach), p - n * Reach);
    }

    // Möller–Trumbore, both faces, within the segment (t in 0..1).
    private static bool Crosses(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
            return false;
        float inv = 1 / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1)
            return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1)
            return false;
        float t = Vector3.Dot(e2, q) * inv;
        return t >= 0 && t <= 1;
    }

    /// <summary>
    /// The faded triangles of a DS3 model (model space): full-detail triangles of a fading overlay mesh whose corners
    /// are all under <see cref="MapOverlays.OverlayCut"/>, with the sum of their vertex normals.
    /// </summary>
    public static IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> FadedTriangles(FLVER2 flver)
    {
        foreach (var mesh in flver.Meshes)
        {
            if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count || !IsFading(flver.Materials[mesh.MaterialIndex], mesh))
                continue;
            foreach (var (i, j, k) in Triangles(mesh))
            {
                var (va, vb, vc) = (mesh.Vertices[i], mesh.Vertices[j], mesh.Vertices[k]);
                if (MapOverlays.Alpha(va) < MapOverlays.OverlayCut && MapOverlays.Alpha(vb) < MapOverlays.OverlayCut && MapOverlays.Alpha(vc) < MapOverlays.OverlayCut)
                    yield return (va.Position, vb.Position, vc.Position, va.Normal + vb.Normal + vc.Normal);
            }
        }
    }

    /// <summary>The full-detail triangles of a mesh, as vertex index triples.</summary>
    public static IEnumerable<(int, int, int)> Triangles(FLVER2.Mesh mesh)
    {
        bool restarts = mesh.Vertices.Count < 0xFFFF;
        foreach (var set in mesh.FaceSets.Where(f => f.Flags == FLVER2.FaceSet.FSFlags.None))
        {
            var tris = set.Triangulate(restarts);
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= mesh.Vertices.Count || b >= mesh.Vertices.Count || c >= mesh.Vertices.Count)
                    continue;
                yield return (a, b, c);
            }
        }
    }

    private static int Cell(float v) => (int)MathF.Floor(v / CellSize);

    private static long Key(Vector3 p) => Key(Cell(p.X), Cell(p.Y), Cell(p.Z));

    private static long Key(int x, int y, int z) =>
        ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);
}
