using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's low-detail stand-ins: map pieces that show a far part of the map (a castle wall, a hall, a tower) with a
/// few blocky meshes and one stretched texture named "..._low_...". DS3 draws a stand-in only from the collisions
/// where the detailed pieces it stands for are not drawn, so the player never sees both. The port draws every piece
/// from everywhere, so the stand-in sits on top of the detailed geometry as slabs and steps with stretched textures
/// (map 2 in the 1.0.307 playtest).
/// <para>
/// A piece is left out as a stand-in when all of these hold:
/// <list type="bullet">
/// <item>at least half of its vertices are in meshes whose albedo texture has a "low" word in its name;</item>
/// <item>DS3 draws it from at least one collision;</item>
/// <item>at least <see cref="MinCovered"/> detailed pieces stand inside its box, DS3 never draws any of them from a
/// collision that draws the stand-in, and together they have at least a quarter of its vertices.</item>
/// </list>
/// A stand-in for something the port has no detailed pieces of (Lothric Castle's outside, m980xxx in m30_00; the
/// far castle in m40_00) fails the last test and stays. Measured on the real layouts: m30_00's m980xxx share a
/// collision with every other piece, m40_00's low pieces too, while m30_00's m900xxx have 400 to 1,700 pieces they
/// are never drawn with.
/// </para>
/// </summary>
public static class MapStandIns
{
    public const int MinCovered = 3;

    /// <summary>One judged low-detail piece, for the port log and the piece report.</summary>
    public sealed record Verdict(string Part, bool LeftOut, int Vertices, int Collisions, int Covered, int CoveredVertices, string Reason);

    public sealed record Result(IReadOnlyList<Verdict> Verdicts)
    {
        public HashSet<string> LeftOut { get; } = Verdicts.Where(v => v.LeftOut).Select(v => v.Part).ToHashSet(StringComparer.Ordinal);

        public string Summary => $"low-detail stand-ins: {LeftOut.Count} left out, {Verdicts.Count - LeftOut.Count} kept"
            + (LeftOut.Count > 0 ? $" (left out: {string.Join(", ", Verdicts.Where(v => v.LeftOut).Select(v => v.Part).Take(20))}{(LeftOut.Count > 20 ? ", ..." : "")})" : "");

        public IEnumerable<string> Lines => Verdicts.Select(v =>
            $"stand-in {v.Part}: {(v.LeftOut ? "left out" : "kept")} ({v.Vertices} vertices, drawn from {v.Collisions} collisions, "
            + $"{v.Covered} detailed pieces with {v.CoveredVertices} vertices inside it never drawn with it; {v.Reason})");
    }

    /// <summary>True when a texture name has "low" as one of its words ("m30_00_low_hall_01_a", "o008814_low_tree").</summary>
    public static bool IsLowTexture(string albedo) =>
        albedo.Split('_', '-', ' ', '.').Any(w => w.Equals("low", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Judges every DS3 map piece that is mostly low-detail texture. <paramref name="meshes"/> holds each model's meshes
    /// by model number; <paramref name="skip"/> names pieces already left out for another reason (helpers, no model).
    /// </summary>
    public static Result Find(MSB3 ds3, IReadOnlyDictionary<string, List<MapModelPort.MeshInfo>> meshes, IReadOnlySet<string> skip)
    {
        var collisions = ds3.Parts.Collisions;
        var pieces = new List<(MSB3.Part.MapPiece Part, int Vertices, int LowVertices, Vector3 Min, Vector3 Max, ulong[] Shown)>();
        foreach (var p in ds3.Parts.MapPieces)
        {
            if (skip.Contains(p.Name) || !meshes.TryGetValue(MapPorter.ModelNumber(p.ModelName), out var list) || list.Count == 0)
                continue;
            var drawn = list.Where(m => m.Vertices > 0).ToList();
            if (drawn.Count == 0)
                continue;
            var (min, max) = WorldBox(p, drawn.Select(m => m.Min).Aggregate(Vector3.Min), drawn.Select(m => m.Max).Aggregate(Vector3.Max));
            pieces.Add((p, drawn.Sum(m => m.Vertices), drawn.Where(m => IsLowTexture(m.Albedo)).Sum(m => m.Vertices), min, max, ShownFrom(p, collisions)));
        }

        var verdicts = new List<Verdict>();
        foreach (var s in pieces.Where(x => x.LowVertices * 2 >= x.Vertices && x.LowVertices > 0))
        {
            int shownFrom = Count(s.Shown);
            if (shownFrom == 0)
            {
                verdicts.Add(new Verdict(s.Part.Name, false, s.Vertices, 0, 0, 0, "no collision draws it, so nothing to compare"));
                continue;
            }
            int covered = 0, coveredVertices = 0;
            foreach (var d in pieces)
            {
                if (d.LowVertices * 2 >= d.Vertices || Count(d.Shown) == 0 || Overlaps(d.Shown, s.Shown))
                    continue;
                var centre = (d.Min + d.Max) / 2;
                if (Vector3.Clamp(centre, s.Min - Vector3.One, s.Max + Vector3.One) != centre)
                    continue;
                covered++;
                coveredVertices += d.Vertices;
            }
            bool leftOut = covered >= MinCovered && coveredVertices * 4L >= s.Vertices;
            verdicts.Add(new Verdict(s.Part.Name, leftOut, s.Vertices, shownFrom, covered, coveredVertices,
                leftOut ? "DS3 shows it only where the detailed pieces are not drawn"
                : covered == 0 ? "no detailed piece stands for it, so it is the only geometry there"
                : "too few detailed pieces stand for it"));
        }
        return new Result(verdicts);
    }

    /// <summary>The collisions (bit per collision index) that draw a piece: DS3 shows it when its draw groups meet the collision's display groups.</summary>
    public static ulong[] ShownFrom(MSB3.Part piece, IReadOnlyList<MSB3.Part.Collision> collisions)
    {
        var bits = new ulong[(collisions.Count + 63) / 64];
        for (int i = 0; i < collisions.Count; i++)
            for (int w = 0; w < piece.DrawGroups.Length && w < collisions[i].DispGroups.Length; w++)
                if ((piece.DrawGroups[w] & collisions[i].DispGroups[w]) != 0)
                {
                    bits[i / 64] |= 1UL << (i % 64);
                    break;
                }
        return bits;
    }

    private static int Count(ulong[] bits) => bits.Sum(b => BitOperations.PopCount(b));

    private static bool Overlaps(ulong[] a, ulong[] b)
    {
        for (int i = 0; i < a.Length && i < b.Length; i++)
            if ((a[i] & b[i]) != 0)
                return true;
        return false;
    }

    /// <summary>
    /// A piece's box in the map: its model's box scaled, turned (degrees, X then Z then Y as DSMapStudio places
    /// DS3 parts) and moved to the part's position.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) WorldBox(MSB3.Part piece, Vector3 min, Vector3 max)
    {
        const float rad = MathF.PI / 180;
        var r = piece.Rotation;
        var scale = piece.Scale == Vector3.Zero ? Vector3.One : piece.Scale;
        var m = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(r.X * rad) * Matrix4x4.CreateRotationZ(r.Z * rad)
                * Matrix4x4.CreateRotationY(r.Y * rad) * Matrix4x4.CreateTranslation(piece.Position);
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) == 0 ? min.X : max.X, (c & 2) == 0 ? min.Y : max.Y, (c & 4) == 0 ? min.Z : max.Z);
            var w = Vector3.Transform(corner, m);
            lo = Vector3.Min(lo, w);
            hi = Vector3.Max(hi, w);
        }
        return (lo, hi);
    }
}
