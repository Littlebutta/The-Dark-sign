using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Checks every ported shield against the shape of the Elden Ring shield it is built on, and turns it to match (2026-11-12
/// 19:42 playtest: "guarding with the dragonhead greatshield does not look right as well as with others. The shields
/// look almost 90 degrees rotated clockwise"). Two earlier fixes worked from how each game is thought to place a model
/// on a dummy poly (#175 baked DS3's node turns, #184 turned DS3's hold frame onto Elden Ring's); neither could be
/// checked against what the game shows, and #184 left the Giant Door Shield out because DS3 hangs it on the body.
/// This check needs no assumption about the engine: the template shield is a vanilla Elden Ring shield on the same
/// absorb row, so the game holds it right, and a ported shield held the same way sits right when its own shape lies
/// along the same model axes.
/// <para>A shield's shape gives three axes from its vertices (principal axes of their spread): the thin one (the face
/// normal), the long one and the width. The thin axis is signed by where the plate lies from the grip (the model's
/// origin is the hand, behind the plate). Only the face is laid on the template's, by the shortest turn (a shield
/// facing the wrong way is turned round about its own length): the spin about the face stays the hold frames', since
/// a shield's long axis says nothing about which way is up across two designs (2026-11-13: matching it turned the
/// small shields on the near-round Buckler template onto their side). Shields with several models keep DS3's
/// positions on a row of their own and are not checked here (see WeaponHold.Plan).</para>
/// <para>DARKSIGN_SHIELD_SHAPE=0 turns the check off (the 1.0.420 hold: the dummy-frame turn alone).</para>
/// </summary>
public static class ShieldShape
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DARKSIGN_SHIELD_SHAPE") != "0";

    /// <summary>Turns smaller than this are measurement noise between two different shield designs: left alone.</summary>
    public const float MinDegrees = 15f;

    /// <summary>Principal axes of a model's vertices: Axes[0] long, [1] wide, [2] thin; Sizes are standard deviations
    /// along them. Signs: thin axis points from the grip to the plate when FrontKnown, long axis towards the side of the
    /// grip the plate's centre lies on when LengthKnown.</summary>
    public sealed record Shape(Vector3 Centre, Vector3[] Axes, float[] Sizes, bool FrontKnown, bool LengthKnown, int Vertices)
    {
        public bool Flat => Sizes[2] < 0.6f * Sizes[1];
        public bool Elongated => Sizes[0] > 1.12f * Sizes[1];
        public override string ToString() =>
            $"sizes {Sizes[0]:0.###}/{Sizes[1]:0.###}/{Sizes[2]:0.###}, long {Fmt(Axes[0])}, face {Fmt(Axes[2])}{(FrontKnown ? "" : " (front unknown)")}";
    }

    public sealed record Result(Matrix4x4? Turn, float Degrees, string Note);

    /// <summary>The model-space vertex positions (through each rigid vertex's node, for a template with node transforms).</summary>
    internal static List<Vector3> Points(FLVER2 flver)
    {
        var worlds = Enumerable.Range(0, flver.Nodes.Count).Select(i => WeaponNodes.World(flver.Nodes, i)).ToArray();
        bool identity = flver.Nodes.All(n => n.Translation == Vector3.Zero && n.Rotation == Vector3.Zero && n.Scale == Vector3.One);
        var points = new List<Vector3>();
        foreach (var mesh in flver.Meshes)
            foreach (var v in mesh.Vertices.Where(v => v.Positions.Count > 0))
            {
                if (identity || mesh.UseBoneWeights)
                {
                    points.Add(v.Position);
                    continue;
                }
                int node = WeaponNodes.NodeOf(mesh, v);
                points.Add(node >= 0 && node < worlds.Length ? Vector3.Transform(v.Position, worlds[node]) : v.Position);
            }
        return points;
    }

    public static Shape? Measure(IReadOnlyList<Vector3> points)
    {
        if (points.Count < 8)
            return null;
        var c = Vector3.Zero;
        foreach (var p in points)
            c += p;
        c /= points.Count;
        var m = new double[3, 3];
        foreach (var p in points)
        {
            var d = p - c;
            double[] e = [d.X, d.Y, d.Z];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] += e[i] * e[j];
        }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i, j] /= points.Count;
        var (values, vectors) = Eigen(m);
        var order = Enumerable.Range(0, 3).OrderByDescending(i => values[i]).ToArray();
        var axes = order.Select(i => Vector3.Normalize(new Vector3((float)vectors[0, i], (float)vectors[1, i], (float)vectors[2, i]))).ToArray();
        var sizes = order.Select(i => (float)Math.Sqrt(Math.Max(values[i], 0))).ToArray();
        // The grip (the model's origin) sits behind the plate: point the thin axis at the plate.
        float front = Vector3.Dot(c, axes[2]);
        bool frontKnown = MathF.Abs(front) > 0.5f * sizes[2] && sizes[2] > 1e-5f;
        if (front < 0)
            axes[2] = -axes[2];
        float along = Vector3.Dot(c, axes[0]);
        bool lengthKnown = MathF.Abs(along) > 0.15f * sizes[0] && sizes[0] > 1e-5f;
        if (along < 0)
            axes[0] = -axes[0];
        axes[1] = Vector3.Normalize(Vector3.Cross(axes[2], axes[0])); // right-handed: long x wide = thin
        return new Shape(c, axes, sizes, frontKnown, lengthKnown, points.Count);
    }

    /// <summary>The turn (row vectors: point times turn) that lays the ported shield's shape on the template's.</summary>
    public static Result Match(Shape ported, Shape template)
    {
        if (!ported.Flat || !template.Flat)
            return new Result(null, 0, $"not a flat shield shape (ported {ported}; template {template}), left as it is");
        // Only the face is laid on the template's, by the shortest turn; the spin about the face stays the hold
        // frames'. Matching the long axes too (1.0.4xx, #186) turned 7 shields on the near-round Buckler template
        // (sizes 0.091/0.076, its "long" axis sideways) about 75° onto their side, and would stand a shield that is wider
        // than tall on its edge: a shield's length says nothing about which way is up across two different designs.
        var target = template.Axes[2];
        if (!(ported.FrontKnown && template.FrontKnown) && Vector3.Dot(ported.Axes[2], target) < 0)
            target = -target;
        // Facing the wrong way: turned round about its own length, so its top stays up.
        var arc = ShortestArc(ported.Axes[2], target, ported.Axes[0]);
        float degrees = WeaponHold.Degrees(arc);
        return degrees < MinDegrees
            ? new Result(null, degrees, $"its face matches the template's within {degrees:0}°")
            : new Result(arc, degrees, $"face turned {degrees:0}° onto the template's");
    }

    /// <summary>The smallest rotation taking unit vector a onto unit vector b (row vectors).</summary>
    private static Matrix4x4 ShortestArc(Vector3 a, Vector3 b, Vector3 halfTurnAxis)
    {
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        var axis = Vector3.Cross(a, b);
        if (dot < -0.98f)
        {
            // Nearly opposite: a half turn about the given axis, then the small rest.
            var half = halfTurnAxis - Vector3.Dot(halfTurnAxis, a) * a;
            if (half.LengthSquared() > 1e-8f)
            {
                var flip = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(half), MathF.PI);
                var flipped = Vector3.Normalize(Vector3.TransformNormal(a, flip));
                return flip * ShortestArc(flipped, b, halfTurnAxis);
            }
        }
        if (axis.LengthSquared() < 1e-10f)
        {
            if (dot > 0)
                return Matrix4x4.Identity;
            // Opposite: a half turn about the given axis (made square to a).
            axis = halfTurnAxis - Vector3.Dot(halfTurnAxis, a) * a;
            if (axis.LengthSquared() < 1e-8f)
                axis = Vector3.Cross(a, MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
        }
        return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    /// <summary>Jacobi eigen decomposition of a symmetric 3x3 matrix: eigenvalues and eigenvectors (columns).</summary>
    private static (double[] Values, double[,] Vectors) Eigen(double[,] input)
    {
        var a = (double[,])input.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 64; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-15)
                break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-18)
                        continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0)
                        t = 1;
                    double cos = 1 / Math.Sqrt(t * t + 1), sin = t * cos;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = cos * akp - sin * akq;
                        a[k, q] = sin * akp + cos * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = cos * apk - sin * aqk;
                        a[q, k] = sin * apk + cos * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = cos * vkp - sin * vkq;
                        v[k, q] = sin * vkp + cos * vkq;
                    }
                }
        }
        return ([a[0, 0], a[1, 1], a[2, 2]], v);
    }

    private static string Fmt(Vector3 v) => $"({v.X:0.##},{v.Y:0.##},{v.Z:0.##})";

    public static int SelfTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        static bool Near(Vector3 a, Vector3 b, float tol = 2e-3f) => Vector3.Distance(a, b) < tol;

        // A kite-ish shield plate: long along `up`, wide along `side`, plate `front` of the grip at the origin, the plate's
        // centre a little towards `up` of the grip; a small handle behind it at the origin.
        static List<Vector3> Shield(Vector3 up, Vector3 side, Vector3 front, float length, float width, float lift = 0.15f)
        {
            var pts = new List<Vector3>();
            for (int i = 0; i <= 20; i++)
                for (int j = 0; j <= 10; j++)
                {
                    float u = (i / 20f - 0.5f) * length + lift, w = (j / 10f - 0.5f) * width;
                    pts.Add(up * u + side * w + front * 0.10f);
                    pts.Add(up * u + side * w + front * 0.13f);
                }
            for (int k = 0; k < 6; k++)
                pts.Add(side * (k / 5f - 0.5f) * 0.12f + front * 0.02f);
            return pts;
        }

        // Elden Ring template convention (vanilla 2208: long along Y, thin along Z).
        var template = Measure(Shield(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 1.24f, 0.60f))!;
        Check(template.Flat && template.Elongated && template.FrontKnown, "shape: a greatshield plate is flat, long, and its front is known");
        Check(Near(template.Axes[2], Vector3.UnitZ) && Near(template.Axes[0], Vector3.UnitY), "shape: thin axis points at the plate, long axis at its centre");

        var same = Match(Measure(Shield(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 1.10f, 0.70f))!, template);
        Check(same.Turn is null && same.Degrees < 1, $"match: a shield already facing like the template is left alone ({same.Note})");

        // Spin about the face is the hold frames' business: a shield lying across its face (or a wide one) is left as
        // it is, and so is a tall shield on the near-round Buckler template whose "long" axis is sideways.
        var across = Match(Measure(Shield(-Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 1.25f, 0.76f))!, template);
        Check(across.Turn is null, $"match: a spin about the face is not changed ({across.Note})");
        var buckler = Measure(Shield(Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ, 0.091f * 3.4f, 0.076f * 3.4f, lift: 0f))!;
        var tall = Match(Measure(Shield(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 0.45f, 0.35f))!, buckler);
        Check(tall.Turn is null, $"match: a tall small shield on the Buckler template stays upright ({tall.Note})");

        // Face turned towards the player: a half turn about its length, so its top stays up.
        var backwards = Shield(Vector3.UnitY, -Vector3.UnitX, -Vector3.UnitZ, 1.2f, 0.6f);
        var b = Match(Measure(backwards)!, template);
        Check(b.Turn is { } bt && Near(Measure(backwards.Select(p => Vector3.Transform(p, bt)).ToList())!.Axes[2], Vector3.UnitZ),
            $"match: a shield facing the wrong way is turned to face out ({b.Note})");
        Check(b.Turn is { } bt2 && Vector3.TransformNormal(Vector3.UnitY, bt2).Y > 0.99f, "match: ...about its length, so its top stays up");

        // Held edge-on (face along X): turned to face out.
        var edge = Shield(Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitX, 1.2f, 0.6f);
        var e = Match(Measure(edge)!, template);
        Check(e.Turn is { } et && Near(Measure(edge.Select(p => Vector3.Transform(p, et)).ToList())!.Axes[2], Vector3.UnitZ)
              && Vector3.TransformNormal(Vector3.UnitY, et).Y > 0.99f, $"match: a shield held edge-on is turned to face out, top still up ({e.Note})");

        // A small tilt (two different designs) is not worth a turn.
        var tilt = Matrix4x4.CreateRotationX(10f * MathF.PI / 180f);
        var t = Match(Measure(Shield(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 1.1f, 0.7f).Select(p => Vector3.Transform(p, tilt)).ToList())!, template);
        Check(t.Turn is null, $"match: a 10° difference is left alone ({t.Note})");

        // A round shield facing sideways.
        var roundTemplate = Measure(Shield(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 0.6f, 0.6f, lift: 0f))!;
        var round = Match(Measure(Shield(Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitX, 0.6f, 0.6f, lift: 0f))!, roundTemplate);
        Check(round.Turn is { } rt && Near(Vector3.TransformNormal(-Vector3.UnitX, rt), Vector3.UnitZ, 0.05f),
            $"match: a round shield facing sideways is turned to face out ({round.Note})");

        var cube = new List<Vector3>();
        for (int i = 0; i < 27; i++)
            cube.Add(new Vector3(i % 3, i / 3 % 3, i / 9) * 0.1f);
        Check(Match(Measure(cube)!, template).Turn is null, "match: a model that isn't flat is left alone");

        // Hold per model: a part with no turn of its own takes model 0's.
        var hold = new WeaponHold.Hold(new Dictionary<int, Matrix4x4> { [0] = tilt, [1] = Matrix4x4.Identity }, false);
        Check(hold.TurnFor(0) == tilt && hold.TurnFor(1).IsIdentity && hold.TurnFor(2) == tilt, "hold: each model takes its own part's turn, else model 0's");
        Console.WriteLine(failures == 0 ? "shield-shape selftest: all checks passed" : $"shield-shape selftest: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
