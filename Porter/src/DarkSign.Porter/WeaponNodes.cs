using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Node fixes for ported weapon models (fx19, 2026-11-12 playtest: shields held at the wrong angle, "especially
/// greatshields"; the Immolation Tinder invisible in hand; weapons popping out of view).
/// <list type="bullet">
/// <item>DS3 places a model's meshes with its node transforms: Havel's Greatshield's root node turns it 90° about Z
/// (R -0.349, 0, 1.571), the Knight Shield's about 25° (R 0, -0.262, -0.436), and the vertices lie along X under
/// them. Every vanilla Elden Ring weapon has identity nodes with the model along Y, so the ported shields came out
/// turned by exactly their node rotation (greatshields most). <see cref="Bake"/> moves each rigid mesh's vertices, and
/// the dummy polys, into model space through their node, and leaves every node at identity, so the model looks the
/// same whether the game applies node transforms or not. DARKSIGN_BAKE_NODES=0 keeps DS3's nodes.</item>
/// <item>Five DS3 models (the Immolation Tinder's among them) hold two nodes whose names differ only in case
/// (Dummy_SFX / Dummy_Sfx); no vanilla Elden Ring model does. <see cref="UniqueNames"/> renames the later one.</item>
/// <item><see cref="SetBounds"/>: Elden Ring's mesh culling box (FLVER 0x2001A) is centre / half size / rotation, which the
/// ported weapons carried as DS3's min / max corners with a zero centre; the header box is set from the vertices too.</item>
/// </list>
/// </summary>
public static class WeaponNodes
{
    public static readonly bool BakeEnabled = Environment.GetEnvironmentVariable("DARKSIGN_BAKE_NODES") != "0";

    private static Matrix4x4 World(IList<FLVER.Node> nodes, int i)
    {
        var m = Matrix4x4.Identity;
        for (int guard = 0; i >= 0 && i < nodes.Count && guard < 256; i = nodes[i].ParentIndex, guard++)
            m *= nodes[i].ComputeLocalTransform();
        return m;
    }

    private static bool IsIdentity(FLVER.Node n) =>
        n.Translation == Vector3.Zero && n.Rotation == Vector3.Zero && n.Scale == Vector3.One;

    /// <summary>The node a rigid vertex hangs on (as the game reads it: the mesh's bone list, else the mesh's node).</summary>
    internal static int NodeOf(FLVER2.Mesh mesh, FLVER.Vertex v)
    {
        int local = v.BoneIndices[0];
        return mesh.BoneIndices.Count > 0 ? (local >= 0 && local < mesh.BoneIndices.Count ? mesh.BoneIndices[local] : -1) : mesh.NodeIndex;
    }

    /// <summary>Bakes node transforms into rigid meshes and dummies; returns a note, or null when nothing changed.</summary>
    public static string? Bake(FLVER2 flver)
    {
        if (!BakeEnabled || flver.Nodes.All(IsIdentity))
            return null;
        if (flver.Meshes.Any(m => m.UseBoneWeights))
            return "kept DS3's nodes (a skinned mesh)";
        var worlds = Enumerable.Range(0, flver.Nodes.Count).Select(i => World(flver.Nodes, i)).ToArray();
        int vertices = 0;
        foreach (var mesh in flver.Meshes)
            foreach (var v in mesh.Vertices)
            {
                int node = NodeOf(mesh, v);
                if (node < 0 || node >= worlds.Length)
                    continue;
                var m = worlds[node];
                for (int p = 0; p < v.Positions.Count; p++)
                    v.Positions[p] = Vector3.Transform(v.Positions[p], m);
                for (int n = 0; n < v.Normals.Count; n++)
                    if (v.Normals[n] != Vector3.Zero)
                        v.Normals[n] = Vector3.Normalize(Vector3.TransformNormal(v.Normals[n], m));
                for (int t = 0; t < v.Tangents.Count; t++)
                    v.Tangents[t] = Rotate(v.Tangents[t], m);
                if (v.Bitangent != Vector4.Zero)
                    v.Bitangent = Rotate(v.Bitangent, m);
                vertices++;
            }
        foreach (var d in flver.Dummies)
        {
            if (d.ParentBoneIndex < 0 || d.ParentBoneIndex >= worlds.Length)
                continue;
            var m = worlds[d.ParentBoneIndex];
            d.Position = Vector3.Transform(d.Position, m);
            d.Forward = Vector3.TransformNormal(d.Forward, m);
            d.Upward = Vector3.TransformNormal(d.Upward, m);
        }
        var turned = flver.Nodes.Where(n => !IsIdentity(n)).Select(n => $"{n.Name} R({n.Rotation.X:0.###},{n.Rotation.Y:0.###},{n.Rotation.Z:0.###})").Take(4).ToList();
        foreach (var n in flver.Nodes)
        {
            n.Translation = Vector3.Zero;
            n.Rotation = Vector3.Zero;
            n.Scale = Vector3.One;
        }
        return $"baked node transforms into {vertices} vertices and {flver.Dummies.Count} dummies ({string.Join(", ", turned)})";
    }

    private static Vector4 Rotate(Vector4 t, Matrix4x4 m)
    {
        var xyz = new Vector3(t.X, t.Y, t.Z);
        if (xyz == Vector3.Zero)
            return t;
        xyz = Vector3.Normalize(Vector3.TransformNormal(xyz, m));
        return new Vector4(xyz, t.W);
    }

    /// <summary>Renames nodes whose names repeat ignoring case (the later ones get "_2", "_3"...). Returns how many.</summary>
    public static int UniqueNames(FLVER2 flver)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int renamed = 0;
        foreach (var n in flver.Nodes)
        {
            var name = n.Name ?? "";
            if (seen.Add(name))
                continue;
            int k = 2;
            while (!seen.Add($"{name}_{k}"))
                k++;
            n.Name = $"{name}_{k}";
            renamed++;
        }
        return renamed;
    }

    /// <summary>Every mesh's culling box in Elden Ring's form, and the header box, from the vertices.</summary>
    public static void SetBounds(FLVER2 flver)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in flver.Meshes)
        {
            Maps.MapModelPort.SetBoundingBox(mesh, flver.Header.Version);
            foreach (var v in mesh.Vertices.Where(v => v.Positions.Count > 0))
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
        }
        if (min.X <= max.X)
        {
            flver.Header.BoundingBoxMin = min;
            flver.Header.BoundingBoxMax = max;
        }
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
        var flver = new FLVER2 { Header = { Version = 0x2001A } };
        flver.Nodes.Add(new FLVER.Node { Name = "WP_A_1409", ParentIndex = -1, Rotation = new Vector3(0, 0, MathF.PI / 2) });
        flver.Nodes.Add(new FLVER.Node { Name = "Dummy_SFX", ParentIndex = 0 });
        flver.Nodes.Add(new FLVER.Node { Name = "Dummy_Sfx", ParentIndex = 0 });
        var mesh = new FLVER2.Mesh { NodeIndex = 0, BoneIndices = [0] };
        mesh.Vertices.Add(new FLVER.Vertex { Positions = [new Vector3(1, 0, 0)], Normals = [new Vector3(1, 0, 0)], BoneIndices = new FLVER.VertexBoneIndices { [0] = 0 } });
        mesh.Vertices.Add(new FLVER.Vertex { Positions = [new Vector3(-0.5f, 0, 0.1f)], Normals = [new Vector3(0, 0, 1)], BoneIndices = new FLVER.VertexBoneIndices { [0] = 0 } });
        flver.Meshes.Add(mesh);
        flver.Dummies.Add(new FLVER.Dummy { Position = new Vector3(1, 0, 0), Forward = new Vector3(0, 0, 1), Upward = new Vector3(1, 0, 0), ParentBoneIndex = 0 });
        var note = Bake(flver);
        Check(note is not null && Near(mesh.Vertices[0].Position, new Vector3(0, 1, 0)), "nodes: a shield along X under a 90° Z node ends up along Y in model space (Havel's Greatshield)");
        Check(Near(mesh.Vertices[0].Normal, new Vector3(0, 1, 0)) && Near(mesh.Vertices[1].Normal, new Vector3(0, 0, 1)), "nodes: normals turn with the vertices");
        Check(Near(flver.Dummies[0].Position, new Vector3(0, 1, 0)) && Near(flver.Dummies[0].Upward, new Vector3(0, 1, 0)), "nodes: the hold dummy turns with them (up +X -> +Y)");
        Check(flver.Nodes.All(IsIdentity), "nodes: every node is left at identity, like Elden Ring's weapons");
        Check(Bake(flver) is null, "nodes: a model with identity nodes is left alone");
        Check(UniqueNames(flver) == 1 && flver.Nodes[2].Name == "Dummy_Sfx_2", "nodes: Dummy_SFX / Dummy_Sfx become two names");
        SetBounds(flver);
        var box = mesh.BoundingBox!;
        Check(Near(box.Unk, new Vector3(0, 0.25f, 0.05f)) && Near(box.Min, new Vector3(0, 0.75f, 0.05f)) && box.Max == Vector3.Zero,
            "bounds: the mesh box is centre / half size / no rotation, as in vanilla Elden Ring models");
        Check(Near(flver.Header.BoundingBoxMin, new Vector3(0, -0.5f, 0)) && Near(flver.Header.BoundingBoxMax, new Vector3(0, 1, 0.1f)), "bounds: the header box holds every vertex");
        Console.WriteLine(failures == 0 ? "weapon-nodes selftest: all checks passed" : $"weapon-nodes selftest: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
