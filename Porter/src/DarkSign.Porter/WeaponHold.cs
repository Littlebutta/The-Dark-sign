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

    /// <summary>Each dummy poly of c0000 in model space at the bind pose (its frame put through its parent bone).</summary>
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
            int space = d.UsesAttachBoneIndex ? d.AttachBoneIndex : d.ParentBoneIndex;
            var m = space >= 0 && space < flver.Nodes.Count ? World(flver.Nodes, space) : Matrix4x4.Identity;
            result.TryAdd(d.ReferenceID, new Frame(flver.Nodes[bone].Name, Vector3.TransformNormal(d.Forward, m), Vector3.TransformNormal(d.Upward, m)));
        }
        return result;
    }

    private static Matrix4x4 World(IList<FLVER.Node> nodes, int i)
    {
        var m = Matrix4x4.Identity;
        for (int guard = 0; i >= 0 && i < nodes.Count && guard < 256; i = nodes[i].ParentIndex, guard++)
            m *= nodes[i].ComputeLocalTransform();
        return m;
    }

    /// <summary>The rotation a dummy frame puts a model through (row vectors: model point times this = world).</summary>
    public static Matrix4x4? Rotation(Frame f)
    {
        if (f.Forward.LengthSquared() < 1e-8f || f.Up.LengthSquared() < 1e-8f)
            return null;
        var forward = Vector3.Normalize(f.Forward);
        var up = f.Up - Vector3.Dot(f.Up, forward) * forward;
        if (up.LengthSquared() < 1e-8f)
            return null;
        var m = Matrix4x4.CreateWorld(Vector3.Zero, forward, Vector3.Normalize(up));
        m.Translation = Vector3.Zero;
        return m;
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
        foreach (var plan in plans.Where(p => p.Status == "port" && p.TemplateId is not null))
        {
            if (ds3Weapons[plan.Ds3Id] is not { } ds3Row || erWeapons[plan.TemplateId!.Value] is not { } template)
                continue;
            var ds3AbsorpRow = ds3Absorp[Convert.ToInt32(ds3Row["wepAbsorpPosId"]!.Value)];
            var erAbsorpRow = erAbsorp[Convert.ToInt32(template["absorpParamId"]!.Value)];
            if (ds3AbsorpRow?["OneHandDamipolyId1"] is not { } ds3Cell || erAbsorpRow?["left_0"] is not { } erCell)
                continue;
            int ds3Dummy = Convert.ToInt32(ds3Cell.Value), erDummy = Convert.ToInt32(erCell.Value);
            if (!ds3Frames.TryGetValue(ds3Dummy, out var from) || !erFrames.TryGetValue(erDummy, out var to)
                || !to.Bone.Contains("Shield", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Turn(from, to) is not { } turn || Degrees(turn) < MinDegrees)
                continue;
            result[plan] = turn;
            if (!byPair.TryGetValue((ds3Dummy, erDummy), out var names))
                byPair[(ds3Dummy, erDummy)] = names = [];
            names.Add(plan.Name);
        }
        foreach (var ((d, e), names) in byPair)
        {
            var turn = Turn(ds3Frames[d], erFrames[e])!.Value;
            Console.WriteLine($"models: shield hold: DS3 dummy {d} on {ds3Frames[d].Bone} (up {Fmt(ds3Frames[d].Up)}) -> Elden Ring dummy {e} on {erFrames[e].Bone} " +
                              $"(up {Fmt(erFrames[e].Up)}): turned {Degrees(turn):0}° for {names.Count} weapons ({string.Join(", ", names.Take(6))}{(names.Count > 6 ? ", ..." : "")}) [DARKSIGN_SHIELD_HOLD=0 off]");
        }
        if (byPair.Count == 0)
            Console.WriteLine("models: shield hold: no ported weapon needed a turn");
        return result;
    }

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
