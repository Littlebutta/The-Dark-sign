using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// The hold turn for ported shields (fx20, 2026-11-12 17:17 playtest: "shields still don't look right when held in the
/// defensive position"). A weapon model sits on a dummy poly of the player model, in that dummy's frame (forward and
/// up). DS3 hangs a shield in the left hand on dummy 23 on L_Weapon, up +Z; Elden Ring's shield rows hang it on dummy
/// 98 (row 47) or 23 (row 49) on L_Shield, whose up lies in the XY plane: the two frames differ by about 90° about
/// forward. Elden Ring's shields are built for its frame (long along Y, thin along Z: 2208 X0.60 Y1.24 Z0.13), the
/// ported ones for DS3's (Havel's X1.25 Y0.76, Knight Shield X0.82 Y0.51), so every ported shield was held turned 90°
/// about its face. The guard pose is Elden Ring's own animation (the guard states play a000_0190xx), which shows it
/// most. Each ported shield is turned by DS3's dummy frame times the inverse of Elden Ring's (both at the player
/// model's bind pose), so it sits on Elden Ring's dummy as it sat on DS3's. DARKSIGN_SHIELD_HOLD=0 leaves the models
/// as they are.
/// </summary>
public static class WeaponHold
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DARKSIGN_SHIELD_HOLD") != "0";

    /// <summary>Turns under this many degrees are left alone.</summary>
    public const float MinDegrees = 5f;

    public readonly record struct Frame(string Bone, Vector3 Forward, Vector3 Up);

    /// <summary>Each dummy poly of c0000 in model space at the bind pose. The FLVER stores a dummy's forward and up in
    /// model space already: putting them through the bone's world matrix again (bbbf969) turned DS3's dummy 23 "up" onto
    /// its forward, and the ported shields ended 61-68° off DS3's hold (fx20 part 2). As stored, vanilla Elden Ring's
    /// 2208 on dummy 98 and 0503 on dummy 23 sit within 7-13° of DS3's Havel's / Knight Shield on DS3's dummy 23.</summary>
    public static Dictionary<int, Frame> PlayerFrames(GameArchives game)
    {
        var result = new Dictionary<int, Frame>();
        const string path = "/chr/c0000.chrbnd.dcx";
        if (!game.Contains(path))
            return result;
        var file = BND4.Read(game.Read(path)).Files.FirstOrDefault(f => FLVER2.Is(f.Bytes));
        if (file is null)
            return result;
        var flver = FLVER2.Read(file.Bytes);
        foreach (var d in flver.Dummies)
        {
            int bone = d.AttachBoneIndex >= 0 ? d.AttachBoneIndex : d.ParentBoneIndex;
            if (bone < 0 || bone >= flver.Nodes.Count)
                continue;
            result.TryAdd(d.ReferenceID, new Frame(flver.Nodes[bone].Name, d.Forward, d.Upward));
        }
        return result;
    }

    /// <summary>
    /// The rotation a dummy frame puts a model through (row vectors: model point times this = world). Model X goes to
    /// up x forward, model Y to up, model Z to forward (the convention the fx20 hold check was validated with: vanilla
    /// shields on Elden Ring's dummies and DS3's on DS3's agree within 7-13°; not Matrix4x4.CreateWorld, which sends
    /// model Z to -forward).
    /// </summary>
    public static Matrix4x4? Rotation(Frame f)
    {
        if (f.Forward.LengthSquared() < 1e-8f || f.Up.LengthSquared() < 1e-8f)
            return null;
        var forward = Vector3.Normalize(f.Forward);
        var right = Vector3.Cross(f.Up, forward);
        if (right.LengthSquared() < 1e-8f)
            return null;
        right = Vector3.Normalize(right);
        var up = Vector3.Cross(forward, right);
        return new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
    }

    /// <summary>The turn that puts a model held on DS3's dummy frame onto Elden Ring's: DS3's frame times the inverse of Elden Ring's.</summary>
    public static Matrix4x4? Turn(Frame ds3, Frame er)
    {
        if (Rotation(ds3) is not { } d || Rotation(er) is not { } e)
            return null;
        return d * Matrix4x4.Transpose(e); // a rotation's inverse is its transpose
    }

    public static float Degrees(Matrix4x4 m)
    {
        var q = Quaternion.CreateFromRotationMatrix(m);
        return 2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>
    /// The turn per ported shield (by plan), from the left-hand slot: DS3's OneHandDamipolyId1 against the template's
    /// absorb row left_0. Only weapons Elden Ring hangs on a shield bone are turned.
    /// </summary>
    public static Dictionary<WeaponPlan, Matrix4x4> Plan(GameArchives er, GameArchives ds3, ParamStore erParams, ParamStore ds3Params, IReadOnlyList<WeaponPlan> plans)
    {
        var result = new Dictionary<WeaponPlan, Matrix4x4>();
        if (!Enabled)
        {
            Console.WriteLine("models: shield hold turn off (DARKSIGN_SHIELD_HOLD=0)");
            return result;
        }
        var erWeapons = erParams.Get("EquipParamWeapon");
        var ds3Weapons = ds3Params.Get("EquipParamWeapon");
        var erAbsorp = erParams.Get("WepAbsorpPosParam");
        var ds3Absorp = ds3Params.Get("WepAbsorpPosParam") ?? ds3Params.Get("WEP_ABSORP_POS_PARAM_ST");
        if (erWeapons is null || ds3Weapons is null || erAbsorp is null || ds3Absorp is null)
        {
            Console.WriteLine("models: shield hold turn skipped (a param is missing)");
            return result;
        }
        var erFrames = PlayerFrames(er);
        var ds3Frames = PlayerFrames(ds3);
        if (erFrames.Count == 0 || ds3Frames.Count == 0)
        {
            Console.WriteLine("models: shield hold turn skipped (c0000 dummy polys unreadable)");
            return result;
        }
        var byPair = new Dictionary<(int, int), List<string>>();
        // Every ported weapon Elden Ring hangs on a shield bone is a shield (the entry marks it for ShieldShape, even when
        // its turn is nothing). Shields DS3 hangs on the body (the Giant Door Shield, dummy 33 on Spine2) start from the
        // turn of DS3's usual hand-held shield dummy instead, and ShieldShape lays them on their template from there.
        var shields = new List<(WeaponPlan Plan, int Ds3Dummy, int ErDummy, bool Hand)>();
        foreach (var plan in plans.Where(p => p.Status == "port" && p.TemplateId is not null))
        {
            if (ds3Weapons[plan.Ds3Id] is not { } ds3Row || erWeapons[plan.TemplateId!.Value] is not { } template)
                continue;
            var ds3AbsorpRow = ds3Absorp[Convert.ToInt32(ds3Row["wepAbsorpPosId"]!.Value)];
            var erAbsorpRow = erAbsorp[Convert.ToInt32(template["absorpParamId"]!.Value)];
            if (ds3AbsorpRow?["OneHandDamipolyId1"] is not { } ds3Cell || erAbsorpRow?["left_0"] is not { } erCell)
                continue;
            int ds3Dummy = Convert.ToInt32(ds3Cell.Value), erDummy = Convert.ToInt32(erCell.Value);
            if (!erFrames.TryGetValue(erDummy, out var to) || !to.Bone.Contains("Shield", StringComparison.OrdinalIgnoreCase))
                continue;
            shields.Add((plan, ds3Dummy, erDummy, ds3Frames.TryGetValue(ds3Dummy, out var from) && HandBone(from.Bone)));
        }
        int? usualHand = shields.Where(s => s.Hand).GroupBy(s => s.Ds3Dummy).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();
        var body = new List<string>();
        foreach (var (plan, ds3Dummy, erDummy, hand) in shields)
        {
            int from = hand ? ds3Dummy : usualHand ?? ds3Dummy;
            if (!hand)
                body.Add($"{plan.Name} (DS3 dummy {ds3Dummy}{(ds3Frames.TryGetValue(ds3Dummy, out var f) ? " on " + f.Bone : "")})");
            var turn = ds3Frames.TryGetValue(from, out var fromFrame) && Turn(fromFrame, erFrames[erDummy]) is { } t && Degrees(t) >= MinDegrees
                ? t : Matrix4x4.Identity;
            result[plan] = turn;
            if (turn.IsIdentity)
                continue;
            if (!byPair.TryGetValue((from, erDummy), out var names))
                byPair[(from, erDummy)] = names = [];
            names.Add(plan.Name);
        }
        foreach (var ((d, e), names) in byPair)
        {
            var turn = Turn(ds3Frames[d], erFrames[e])!.Value;
            Console.WriteLine($"models: shield hold: DS3 dummy {d} on {ds3Frames[d].Bone} (up {Fmt(ds3Frames[d].Up)}) -> Elden Ring dummy {e} on {erFrames[e].Bone} " +
                              $"(up {Fmt(erFrames[e].Up)}): turned {Degrees(turn):0}° for {names.Count} weapons ({string.Join(", ", names.Take(6))}{(names.Count > 6 ? ", ..." : "")}) [DARKSIGN_SHIELD_HOLD=0 off]");
        }
        if (body.Count > 0)
            Console.WriteLine($"models: shield hold: {body.Count} shields DS3 hangs on the body start from DS3's hand-held shield dummy {usualHand?.ToString() ?? "(none)"}: {string.Join(", ", body)}");
        Console.WriteLine($"models: shield hold: {result.Count} ported shields ({byPair.Values.Sum(n => n.Count)} turned by the hold frames); each is checked against its template's shape (ShieldShape)");
        return result;
    }

    /// <summary>DS3 left-hand hold bones. The Giant Door Shield hangs on the body in DS3 (dummy 33 on Spine2): turning
    /// that pose onto Elden Ring's hand dummy left it 37°/70° off vanilla shields (fx20 part 3), so it starts from DS3's
    /// usual hand-held shield dummy instead (see Plan).</summary>
    internal static bool HandBone(string bone) => bone is not null && (bone.Equals("L_Weapon", StringComparison.OrdinalIgnoreCase)
        || bone.Equals("L_Hand", StringComparison.OrdinalIgnoreCase) || bone.Equals("L_Shield", StringComparison.OrdinalIgnoreCase));

    private static string Fmt(Vector3 v) => $"{v.X:0.##},{v.Y:0.##},{v.Z:0.##}";

    /// <summary>Turns a model's vertices (normals, tangents) and its dummy polys. False when a node isn't at identity
    /// (a skinned model DS3's nodes were kept for), which the turn would tear apart.</summary>
    public static bool Apply(FLVER2 flver, Matrix4x4 turn)
    {
        if (flver.Nodes.Any(n => n.Translation != Vector3.Zero || n.Rotation != Vector3.Zero || n.Scale != Vector3.One))
            return false;
        foreach (var mesh in flver.Meshes)
            foreach (var v in mesh.Vertices)
            {
                for (int p = 0; p < v.Positions.Count; p++)
                    v.Positions[p] = Vector3.Transform(v.Positions[p], turn);
                for (int n = 0; n < v.Normals.Count; n++)
                    v.Normals[n] = Vector3.TransformNormal(v.Normals[n], turn);
                for (int t = 0; t < v.Tangents.Count; t++)
                    v.Tangents[t] = Rotate(v.Tangents[t], turn);
                if (v.Bitangent != Vector4.Zero)
                    v.Bitangent = Rotate(v.Bitangent, turn);
            }
        foreach (var d in flver.Dummies)
        {
            d.Position = Vector3.Transform(d.Position, turn);
            d.Forward = Vector3.TransformNormal(d.Forward, turn);
            d.Upward = Vector3.TransformNormal(d.Upward, turn);
        }
        return true;
    }

    private static Vector4 Rotate(Vector4 t, Matrix4x4 m)
    {
        var xyz = Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), m);
        return new Vector4(xyz, t.W);
    }

    public static int SelfTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;
        // DS3: forward +X, up +Z (dummy 23 on L_Weapon); Elden Ring: same forward, up +Y (a shield dummy on L_Shield).
        var ds3 = new Frame("L_Weapon", Vector3.UnitX, Vector3.UnitZ);
        var er = new Frame("L_Shield", Vector3.UnitX, Vector3.UnitY);
        var turn = Turn(ds3, er)!.Value;
        var dWorld = Rotation(ds3)!.Value;
        var eWorld = Rotation(er)!.Value;
        foreach (var p in new[] { new Vector3(1.25f, 0, 0), new Vector3(0, 0.76f, 0), new Vector3(0.1f, 0.2f, 0.3f) })
            Check(Near(Vector3.Transform(Vector3.Transform(p, turn), eWorld), Vector3.Transform(p, dWorld)),
                $"hold: a point {p} sits where it did on DS3's dummy once turned onto Elden Ring's");
        Check(MathF.Abs(Degrees(turn) - 90f) < 0.01f, "hold: frames 90° apart about forward give a 90° turn");
        Check(Degrees(Turn(ds3, ds3)!.Value) < 0.01f, "hold: the same frame needs no turn");
        Check(Near(Vector3.Transform(Vector3.UnitY, eWorld), Vector3.UnitY) && Near(Vector3.Transform(Vector3.UnitZ, eWorld), Vector3.UnitX),
            "hold: a frame puts model Y on its up and model Z on its forward");
        // Unit-less dummy vectors (c0000 stores them scaled, e.g. forward 0.115, up 0.072) give the same frame.
        Check(Near(Vector3.Transform(new Vector3(0.3f, 0.2f, 0.1f), Rotation(new Frame("L_Shield", 0.3f * Vector3.UnitX, 0.07f * Vector3.UnitY))!.Value),
            Vector3.Transform(new Vector3(0.3f, 0.2f, 0.1f), eWorld)), "hold: dummy vector lengths don't matter");
        Check(!HandBone("Spine2") && HandBone("L_Weapon") && HandBone("L_Shield"), "hold: a shield DS3 hangs on the body (Giant Door Shield, Spine2) is not turned");
        var flver = new FLVER2();
        flver.Nodes.Add(new FLVER.Node { Name = "WP_A_1409", ParentIndex = -1 });
        var mesh = new FLVER2.Mesh { NodeIndex = 0, BoneIndices = [0] };
        mesh.Vertices.Add(new FLVER.Vertex { Positions = [new Vector3(1, 0, 0)], Normals = [new Vector3(0, 0, 1)] });
        flver.Meshes.Add(mesh);
        flver.Dummies.Add(new FLVER.Dummy { Position = new Vector3(1, 0, 0), Forward = Vector3.UnitZ, Upward = Vector3.UnitX });
        Check(Apply(flver, turn), "hold: a model with identity nodes is turned");
        var expected = Vector3.Transform(new Vector3(1, 0, 0), turn);
        Check(Near(mesh.Vertices[0].Position, expected) && Near(flver.Dummies[0].Position, expected), "hold: vertices and dummy polys turn together");
        flver.Nodes[0].Rotation = new Vector3(0, 0, 1);
        Check(!Apply(flver, turn), "hold: a model whose nodes still turn it is left alone");
        Console.WriteLine(failures == 0 ? "weapon-hold selftest: all checks passed" : $"weapon-hold selftest: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
