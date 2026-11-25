using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's fog walls (boss fog gates and the area fogs) as Elden Ring's own fog wall assets.
/// <para>
/// Both games build a fog wall the same way (read from the game files, 2026-11-25): an object/asset whose model has
/// no mesh at all, only dummy points, and one thin collision wall that stops the player. Its look is an effect the
/// script hangs on dummy poly 101 while the wall is up, and its prompt is an action button on the same entity:
/// </para>
/// <list type="table">
/// <item><term>DS3</term><description>o000400 (2.5 x 4 m), o000401 (5 x 8 m), o000402 (15 x 15 m): FLVER with no
/// mesh, dummy 101 (and 110 on o000401), a 12-triangle collision box 0.15 m deep. common_func 20005820 turns the
/// object on and creates effect 2/3/4/5 on dummy 101 while the boss lives; 20005800 waits for the fog prompt
/// ("IF Action Button in Area" on the object) and walks the player through with animation 60060.</description></item>
/// <item><term>Elden Ring</term><description>AEG099_001 (5 x 8 m, the same dummies 101 and 110 as o000401 at the same
/// spots), AEG099_002 (14.9 x 8.3 m), AEG099_003 (6 x 6 m): FLVER with no mesh, a collision wall 0.13-0.15 m deep,
/// a row 99001-99003 in the vanilla AssetEnvironmentGeometryParam. common_func 9005811 / 9005800 are 20005820 /
/// 20005800 instruction for instruction (Change Asset Enable State, Create Asset-following SFX on 101, Action
/// Button in Area, Rotate Character 60060).</description></item>
/// </list>
/// Up to 1.0.640 the object port tried to draw DS3's fog models like any object, found no mesh it could draw
/// ("only effect, decal, water or shadow meshes") and left every fog wall out: no wall, no prompt, so the walk-in
/// fight starts that wait for the fog prompt (Deacons of the Deep, most bosses with no first-encounter flag) never
/// began and the player could walk out of every arena.
/// <para>
/// Now every DS3 object model that is a fog wall by that shape (no mesh, dummy 101, one thin wall of collision) is
/// placed as the Elden Ring fog asset of the same proportions, scaled uniformly to DS3's wall size (Elden Ring scales
/// assets uniformly in its own maps: Stormveil places 561 scaled assets). Its look stays DS3's fog effect, which the
/// converted script hangs on dummy 101 as before, and the asset brings Elden Ring's own collision and param row.
/// </para>
/// </summary>
public static class FogWalls
{
    /// <summary>The dummy poly both games hang the fog's effect on (and the prompt's position).</summary>
    public const int EffectDummy = 101;

    /// <summary>A wall no thicker than this (m) and at least this wide and high is a fog wall's collision.</summary>
    public const float MaxDepth = 0.5f, MinSide = 1f;

    /// <summary>Larger FLVER files hold meshes, so they are not read for this check.</summary>
    public const int MaxMeshlessBytes = 16 * 1024;

    /// <summary>
    /// One of Elden Ring's fog wall assets: its collision wall's width and height at scale 1 (centred on x = 0,
    /// standing on y = 0), and the dummy polys its model has. Measured from the vanilla geombnd/_l.geomhkxbnd files
    /// (AEG099_001: compressed mesh -2.5..2.5 x 0..8; AEG099_002: box -7.466..7.459 x 0.01..8.343; AEG099_003: box
    /// -2.99..2.99 x 0.01..5.99).
    /// </summary>
    public sealed record ErFog(string Model, float Width, float Height, int[] Dummies)
    {
        public float Aspect => Width / Height;
        public int Row => int.Parse(Model[3..6]) * 1000 + int.Parse(Model[7..10]);
    }

    public static readonly ErFog[] ErFogs =
    [
        new("AEG099_001", 5.0f, 8.0f, [101, 110, 180, 181, 182, 183, 184, 185, 186, 187]),
        new("AEG099_002", 14.93f, 8.33f, [101, 180, 181, 182, 183, 184, 185, 186, 187]),
        new("AEG099_003", 5.98f, 5.98f, [101, 180, 181, 182, 183, 184, 185, 186, 187]),
    ];

    public static bool IsErFogModel(string? model) =>
        model is not null && ErFogs.Any(f => f.Model.Equals(model, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Elden Ring fog asset a DS3 fog wall becomes, at what uniform scale, and why.</summary>
    public sealed record Match(ErFog Fog, float Scale, float Ds3Width, float Ds3Height)
    {
        public string Note => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"a fog wall ({Ds3Width:0.##} x {Ds3Height:0.##} m): Elden Ring's own {Fog.Model} at scale {Scale:0.###} (its collision and row)");
    }

    /// <summary>
    /// Whether a DS3 objbnd is a fog wall, and which Elden Ring fog asset it becomes. Null when it isn't one: the
    /// model has a mesh, has no dummy 101, or its collision isn't one thin wall.
    /// </summary>
    public static Match? Recognise(BND4 ds3Bnd, out string why)
    {
        var files = ds3Bnd.Files.Where(f => FLVER2.Is(f.Bytes)).ToList();
        // A FLVER with no mesh is well under a kilobyte (DS3's fog models 496-560 bytes): don't parse drawn models twice.
        if (files.Any(f => f.Bytes.Length > MaxMeshlessBytes)) { why = "it has meshes"; return null; }
        var flvers = files.Select(f => FLVER2.Read(f.Bytes)).ToList();
        if (flvers.Count == 0) { why = "no model"; return null; }
        // Cheap tests first: only a mesh-less model with dummy 101 has its collision read.
        if (Recognise(flvers, null, out why) is null && why != NeedsCollision)
            return null;
        var hkx = ds3Bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".hkx", StringComparison.OrdinalIgnoreCase));
        if (hkx is null) { why = "no collision"; return null; }
        try
        {
            var points = CollisionPort.Decode(CollisionPort.ReadDs3(hkx.Bytes))
                .SelectMany(b => b.Triangles).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
            if (points.Count == 0) { why = "empty collision"; return null; }
            return Recognise(flvers, points.Aggregate(Vector3.Max) - points.Aggregate(Vector3.Min), out why);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            why = $"collision unreadable ({ex.Message})";
            return null;
        }
    }

    private const string NeedsCollision = "collision not read";

    /// <summary>
    /// The test itself, on the model's FLVERs and the size of its collision (null: not read yet, so a model that
    /// passes the other tests answers null with <see cref="NeedsCollision"/>).
    /// </summary>
    public static Match? Recognise(IReadOnlyList<FLVER2> flvers, Vector3? collisionSize, out string why)
    {
        if (flvers.Count == 0) { why = "no model"; return null; }
        if (flvers.Any(f => f.Meshes.Count > 0)) { why = "it has meshes"; return null; }
        var dummies = flvers.SelectMany(f => f.Dummies).Select(d => (int)d.ReferenceID).ToHashSet();
        if (!dummies.Contains(EffectDummy)) { why = $"no dummy {EffectDummy}"; return null; }
        if (collisionSize is not { } size) { why = NeedsCollision; return null; }
        return Choose(size, dummies, out why);
    }

    /// <summary>
    /// The Elden Ring fog asset for a DS3 wall of this size (x width, y height, z depth): the one whose proportions are
    /// closest among those that have every dummy poly DS3's model has, scaled uniformly to DS3's width.
    /// </summary>
    public static Match? Choose(Vector3 size, IReadOnlySet<int> ds3Dummies, out string why)
    {
        if (size.Z > MaxDepth || size.X < MinSide || size.Y < MinSide)
        {
            why = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"collision is not one thin wall ({size.X:0.##} x {size.Y:0.##} x {size.Z:0.##} m)");
            return null;
        }
        float aspect = size.X / size.Y;
        var fog = ErFogs.Where(f => ds3Dummies.All(d => f.Dummies.Contains(d)))
            .OrderBy(f => MathF.Abs(MathF.Log(f.Aspect / aspect))).FirstOrDefault();
        if (fog is null)
        {
            why = $"no Elden Ring fog asset has its dummy polys ({string.Join(" ", ds3Dummies.Order())})";
            return null;
        }
        why = "";
        return new Match(fog, size.X / fog.Width, size.X, size.Y);
    }
}
