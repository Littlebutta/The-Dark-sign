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
/// <para>
/// Far views (<see cref="IsFarView"/>): DS3 also ships low-detail copies of a whole area whose textures are ordinary
/// ones (m31_00's m901xxx mountains and m903xxx settlement roofs and rock walls, "m30_00_m31_00_roof_01_a"). DS3 draws
/// them only from a lookout collision (h023000 in m31_00, which draws none of the settlement itself), so the player
/// never sees them over the real map; the port drew them everywhere, as low-resolution polygons floating in and above
/// map 3 (1.0.366 playtest). The texture name test missed all but two of them. Any other piece is left out as a far
/// view when the detail inside its box that DS3 never draws with it
/// <list type="bullet">
/// <item>counts at least <see cref="MinFarCovered"/> pieces,</item>
/// <item>has at least <see cref="FarDetailRatio"/> times its vertices (it stands for far more detail than it has), and</item>
/// <item>is at least half of all the detail inside its box (the sky dome and the distant cliffs cover the whole map
/// too, but DS3 draws them from 57 to 60 of m31_00's collisions, so only 1 to 3% of the map is never drawn with them).</item>
/// </list>
/// </para>
/// </summary>
public static class MapStandIns
{
    public const int MinCovered = 3;

    /// <summary>A far view stands for at least this many detailed pieces DS3 never draws with it.</summary>
    public const int MinFarCovered = 10;

    /// <summary>... holding at least this many times its own vertices.</summary>
    public const int FarDetailRatio = 10;

    /// <summary>One judged low-detail piece, for the port log and the piece report.</summary>
    public sealed record Verdict(string Part, bool LeftOut, int Vertices, int Collisions, int Covered, int CoveredVertices, string Reason)
    {
        /// <summary>True for a piece judged by the far view test (no "low" texture), false for a low-detail stand-in.</summary>
        public bool FarView { get; init; }

        /// <summary>Vertices of all the detailed pieces inside its box, drawn with it or not.</summary>
        public long InsideVertices { get; init; }
    }

    public sealed record Result(IReadOnlyList<Verdict> Verdicts)
    {
        public HashSet<string> LeftOut { get; } = Verdicts.Where(v => v.LeftOut).Select(v => v.Part).ToHashSet(StringComparer.Ordinal);

        /// <summary>The far views left out (a subset of <see cref="LeftOut"/>).</summary>
        public HashSet<string> FarViews { get; } = Verdicts.Where(v => v.LeftOut && v.FarView).Select(v => v.Part).ToHashSet(StringComparer.Ordinal);

        public string Summary
        {
            get
            {
                var low = Verdicts.Where(v => !v.FarView).ToList();
                var lowOut = low.Where(v => v.LeftOut).Select(v => v.Part).ToList();
                var far = Verdicts.Where(v => v.FarView && v.LeftOut).Select(v => v.Part).ToList();
                return $"low-detail stand-ins: {lowOut.Count} left out, {low.Count - lowOut.Count} kept"
                       + (lowOut.Count > 0 ? $" (left out: {string.Join(", ", lowOut.Take(20))}{(lowOut.Count > 20 ? ", ..." : "")})" : "")
                       + $"; far views: {far.Count} left out"
                       + (far.Count > 0 ? $" ({string.Join(", ", far.Take(20))}{(far.Count > 20 ? ", ..." : "")})" : "");
            }
        }

        public IEnumerable<string> Lines => Verdicts.Select(v =>
            $"{(v.FarView ? "far view" : "stand-in")} {v.Part}: {(v.LeftOut ? "left out" : "kept")} ({v.Vertices} vertices, drawn from {v.Collisions} collisions, "
            + $"{v.Covered} detailed pieces with {v.CoveredVertices} vertices inside it never drawn with it"
            + (v.FarView ? $", of {v.InsideVertices} detailed vertices inside it" : "") + $"; {v.Reason})");
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
        foreach (var s in pieces)
        {
            bool low = IsLowPiece(s.Vertices, s.LowVertices);
            int shownFrom = Count(s.Shown);
            if (shownFrom == 0)
            {
                if (low)
                    verdicts.Add(new Verdict(s.Part.Name, false, s.Vertices, 0, 0, 0, "no collision draws it, so nothing to compare"));
                continue;
            }
            int covered = 0, coveredVertices = 0;
            long insideVertices = 0;
            foreach (var d in pieces)
            {
                if (ReferenceEquals(d.Part, s.Part) || IsLowPiece(d.Vertices, d.LowVertices) || Count(d.Shown) == 0)
                    continue;
                var centre = (d.Min + d.Max) / 2;
                if (Vector3.Clamp(centre, s.Min - Vector3.One, s.Max + Vector3.One) != centre)
                    continue;
                insideVertices += d.Vertices;
                if (Overlaps(d.Shown, s.Shown))
                    continue;
                covered++;
                coveredVertices += d.Vertices;
            }
            if (low)
            {
                bool leftOut = covered >= MinCovered && coveredVertices * 4L >= s.Vertices;
                verdicts.Add(new Verdict(s.Part.Name, leftOut, s.Vertices, shownFrom, covered, coveredVertices,
                    leftOut ? "DS3 shows it only where the detailed pieces are not drawn"
                    : covered == 0 ? "no detailed piece stands for it, so it is the only geometry there"
                    : "too few detailed pieces stand for it"));
                continue;
            }
            var (farView, reason) = IsFarView(s.Vertices, covered, coveredVertices, insideVertices);
            // Ordinary pieces are only listed when they come near the test, so the port log stays readable.
            if (farView || covered >= MinFarCovered)
                verdicts.Add(new Verdict(s.Part.Name, farView, s.Vertices, shownFrom, covered, coveredVertices, reason)
                    { FarView = true, InsideVertices = insideVertices });
        }
        return new Result(verdicts);
    }

    private static bool IsLowPiece(int vertices, int lowVertices) => lowVertices > 0 && lowVertices * 2 >= vertices;

    /// <summary>
    /// The far view test for a piece with no "low" texture: <paramref name="covered"/> detailed pieces holding
    /// <paramref name="coveredVertices"/> vertices stand inside its box and are never drawn with it, out of
    /// <paramref name="insideVertices"/> detailed vertices inside its box in all.
    /// </summary>
    public static (bool FarView, string Reason) IsFarView(int vertices, int covered, long coveredVertices, long insideVertices)
    {
        if (covered < MinFarCovered)
            return (false, $"fewer than {MinFarCovered} detailed pieces it is never drawn with");
        if (coveredVertices < (long)FarDetailRatio * vertices)
            return (false, $"the detail it is never drawn with has less than {FarDetailRatio} times its vertices, so it is not a stand-in for it");
        if (coveredVertices * 2 < insideVertices)
            return (false, "DS3 draws it together with most of the detail inside it");
        return (true, "DS3 draws it only from where the detail it covers is not drawn");
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
