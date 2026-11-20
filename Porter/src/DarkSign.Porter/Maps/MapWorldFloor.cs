using System.Numerics;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Where the player can walk in a DS3 map: the upward faces of its h (hit) collision, in DS3's world space, sampled
/// one point per 4 m cell per collision part. Each sample names the collision part it lies on, so "what DS3 draws
/// here" is that collision's display groups (and its connect collisions' for the next map). Used by
/// <see cref="MapConnectDisplay"/> and the "map-diag world" report.
/// </summary>
public static class MapWorldFloor
{
    public const float Grid = 4f;

    public readonly record struct Cell(string Collision, Vector3 At);

    /// <summary>Every walkable cell of <paramref name="map"/>; empty when the map has no h binder.</summary>
    public static List<Cell> Sample(GameArchives ds3, MapId map, MSB3 msb)
    {
        var result = new List<Cell>();
        var hPath = MapCollisions.Ds3BinderPath(map, "h");
        if (!ds3.Contains(hPath))
            return result;
        var bxf = BXF4.Read(ds3.Read(hPath), ds3.Read(hPath[..^3] + "bdt"));
        foreach (var f in bxf.Files.Where(f => f.Name.EndsWith(".hkx.dcx", StringComparison.OrdinalIgnoreCase)))
        {
            Ds3.hkRootLevelContainer root;
            try { root = CollisionPort.ReadDs3(DCX.Decompress(f.Bytes)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            var number = MapCollisions.ModelNumberOf(ItemText.Leaf(f.Name));
            var parts = msb.Parts.Collisions.Where(c => MapPorter.ModelNumber(c.ModelName) == number).ToList();
            if (parts.Count == 0)
                continue;
            var bodies = (root.m_namedVariants ?? []).Select(v => v.m_variant).OfType<Ds3.hknpPhysicsSceneData>()
                .SelectMany(s => s.m_systemDatas ?? []).SelectMany(s => s.m_bodyCinfos ?? []).ToList();
            var decoded = CollisionPort.Decode(root);
            foreach (var part in parts)
            {
                const float rad = MathF.PI / 180;
                var r = part.Rotation;
                var scale = part.Scale == Vector3.Zero ? Vector3.One : part.Scale;
                var partM = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(r.X * rad) * Matrix4x4.CreateRotationZ(r.Z * rad)
                            * Matrix4x4.CreateRotationY(r.Y * rad) * Matrix4x4.CreateTranslation(part.Position);
                var cells = new HashSet<(int, int, int)>();
                for (int b = 0; b < decoded.Count; b++)
                {
                    var body = bodies.FirstOrDefault(x => x.m_name == decoded[b].Name) ?? (b < bodies.Count ? bodies[b] : null);
                    var bodyM = body is null ? Matrix4x4.Identity
                        : Matrix4x4.CreateFromQuaternion(body.m_orientation == default ? Quaternion.Identity : body.m_orientation)
                          * Matrix4x4.CreateTranslation(body.m_position.X, body.m_position.Y, body.m_position.Z);
                    var m = bodyM * partM;
                    foreach (var (a0, b0, c0) in decoded[b].Triangles)
                    {
                        var a = Vector3.Transform(a0, m);
                        var bb = Vector3.Transform(b0, m);
                        var c = Vector3.Transform(c0, m);
                        var n = Vector3.Cross(bb - a, c - a);
                        if (n.Length() < 1e-6f || Math.Abs(Vector3.Normalize(n).Y) < 0.7f)
                            continue;
                        // Sampled finer than the cells, so a large floor triangle covers every cell it spans.
                        int steps = Math.Clamp((int)MathF.Ceiling(MathF.Max((bb - a).Length(), MathF.Max((c - bb).Length(), (a - c).Length())) / (Grid / 2)), 1, 200);
                        for (int i = 0; i <= steps; i++)
                            for (int j = 0; i + j <= steps; j++)
                            {
                                var q = a + (bb - a) * (i / (float)steps) + (c - a) * (j / (float)steps);
                                if (cells.Add(((int)MathF.Floor(q.X / Grid), (int)MathF.Floor(q.Y / Grid), (int)MathF.Floor(q.Z / Grid))))
                                    result.Add(new Cell(part.Name, q));
                            }
                    }
                }
            }
        }
        return result;
    }
}
