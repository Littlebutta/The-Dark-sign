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
/// <para>
/// Backdrops (<see cref="IsBackdrop"/>): a piece that passes the far view test but that DS3 draws only from
/// collisions whose floor lies inside its own box is not a far copy of the map but the surroundings of a closed room
/// seen from inside it: Darkeater Midir's arena (m51_00, m040001_7000: the abyss rock wall, the aurora and the
/// waterfalls, drawn only from the arena's 6 collisions, none of the city). Left out, the arena showed holes onto the
/// sky (1.0.640 playtest). A backdrop is kept, and <see cref="MapConnectDisplay"/> shows it only from those
/// collisions, which in turn show only what DS3 shows from them (the city's stand-ins no longer float in the arena).
/// m31_00's lookout far views (one collision, h023000, standing inside them) are backdrops by the same test: DS3 shows
/// them from the lookout instead of the settlement, and the port now does the same.
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

        /// <summary>A far view kept as a room's surroundings: the collisions DS3 draws it from (all standing inside it).</summary>
        public IReadOnlyList<string> BackdropFrom { get; init; } = [];
    }

    /// <summary>A drawn DS3 map piece's box in DS3's world space and its vertex count; Low = mostly "low" textures.</summary>
    public sealed record PieceBox(string Name, Vector3 Min, Vector3 Max, int Vertices, bool Low);

    public sealed record Result(IReadOnlyList<Verdict> Verdicts)
    {
        /// <summary>Every judged piece's box (the world step's doorway lists use them).</summary>
        public IReadOnlyList<PieceBox> Pieces { get; init; } = [];

        /// <summary>Low-detail stand-ins kept because nothing in their own map stands for them.</summary>
        public IEnumerable<string> KeptStandIns => Verdicts.Where(v => !v.FarView && !v.LeftOut).Select(v => v.Part);

        public HashSet<string> LeftOut { get; } = Verdicts.Where(v => v.LeftOut).Select(v => v.Part).ToHashSet(StringComparer.Ordinal);

        /// <summary>The far views left out (a subset of <see cref="LeftOut"/>).</summary>
        public HashSet<string> FarViews { get; } = Verdicts.Where(v => v.LeftOut && v.FarView).Select(v => v.Part).ToHashSet(StringComparer.Ordinal);

        /// <summary>Backdrops (kept): each with the collisions DS3 draws it from, the only ones that should show it.</summary>
        public Dictionary<string, IReadOnlyList<string>> Backdrops { get; } = Verdicts.Where(v => v.BackdropFrom.Count > 0)
            .ToDictionary(v => v.Part, v => v.BackdropFrom, StringComparer.Ordinal);

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
                       + (far.Count > 0 ? $" ({string.Join(", ", far.Take(20))}{(far.Count > 20 ? ", ..." : "")})" : "")
                       + (Backdrops.Count > 0 ? $"; backdrops kept: {Backdrops.Count} ({string.Join(", ", Backdrops.Select(b => $"{b.Key} from {string.Join("/", b.Value)}"))})" : "");
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
    /// <paramref name="floor"/> is the map's walkable floor (<see cref="MapWorldFloor"/>); without it no backdrop is found
    /// and every far view is left out.
    /// </summary>
    public static Result Find(MSB3 ds3, IReadOnlyDictionary<string, List<MapModelPort.MeshInfo>> meshes, IReadOnlySet<string> skip,
        IReadOnlyList<MapWorldFloor.Cell>? floor = null)
    {
        var cellsOf = (floor ?? []).GroupBy(c => c.Collision, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.At).ToList(), StringComparer.Ordinal);
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
            IReadOnlyList<string> backdrop = [];
            if (farView)
            {
                var from = Enumerable.Range(0, collisions.Count).Where(i => (s.Shown[i / 64] & (1UL << (i % 64))) != 0)
                    .Select(i => collisions[i].Name).ToList();
                var inside = from.Select(c => cellsOf.TryGetValue(c, out var cells) ? (cells.Count(at => Vector3.Clamp(at, s.Min, s.Max) == at), cells.Count) : (0, 0)).ToList();
                if (IsBackdrop(inside))
                {
                    backdrop = from;
                    farView = false;
                    reason = $"DS3 draws it only from {from.Count} collisions standing inside it ({inside.Sum(x => x.Item1)} of their {inside.Sum(x => x.Item2)} floor cells): "
                             + "a room's surroundings, kept and shown only from them";
                }
            }
            // Ordinary pieces are only listed when they come near the test, so the port log stays readable.
            if (farView || backdrop.Count > 0 || covered >= MinFarCovered)
                verdicts.Add(new Verdict(s.Part.Name, farView, s.Vertices, shownFrom, covered, coveredVertices, reason)
                    { FarView = true, InsideVertices = insideVertices, BackdropFrom = backdrop });
        }
        return new Result(verdicts)
        {
            Pieces = pieces.Select(p => new PieceBox(p.Part.Name, p.Min, p.Max, p.Vertices, IsLowPiece(p.Vertices, p.LowVertices))).ToList(),
        };
    }

    private static bool Meets(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X <= bMax.X && bMin.X <= aMax.X && aMin.Y <= bMax.Y && bMin.Y <= aMax.Y && aMin.Z <= bMax.Z && bMin.Z <= aMax.Z;

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

    /// <summary>
    /// The backdrop test for a far view: <paramref name="floorInside"/> holds, for each collision DS3 draws it from, its
    /// floor cells inside the piece's box and all its floor cells. A backdrop stands around every one of them: each has
    /// floor, and at least half of it is inside. Measured on Devin's files (map-diag world, 15 maps): Midir's arena
    /// m040001_7000 6 of 6 collisions, 40,955 of 40,955 cells; the Dreg Heap's m040001_3000 2 of 2; m31_00's lookout 1
    /// of 1 (10 cells); far views over the map such as m51_00's m000010_9092 0 of 29.
    /// </summary>
    public static bool IsBackdrop(IReadOnlyList<(int Inside, int Cells)> floorInside) =>
        floorInside.Count > 0 && floorInside.All(c => c.Cells > 0 && c.Inside * 2 >= c.Cells);

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
