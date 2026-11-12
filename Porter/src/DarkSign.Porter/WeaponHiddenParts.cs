using System.Numerics;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Weapon parts DS3 hides with the weapon's own animation. Some DS3 weapons keep a whole scabbard in model 0 (the Black
/// Blade's WP_A_0503.flver, on bone "Case") and hide it with the idle animation in their parts .anibnd
/// (a999_000000.hkx scales Case to 0.0001); only the Hold stance scales it back. Elden Ring can't load that Havok 2014
/// anibnd, so the port leaves it out, and the scabbard showed along the blade ("hitting them with a stick"). Here every
/// bone the idle animation scales to almost nothing (and the bones under it) is read, and the vertices on those bones
/// are pulled to the bone, as the scale does in DS3: the part is still there, but draws nothing.
/// DARKSIGN_HIDDEN_PARTS=0 keeps the parts as DS3's model draws them without the animation.
/// </summary>
public static class WeaponHiddenParts
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DARKSIGN_HIDDEN_PARTS") != "0";

    /// <summary>A bone counts as hidden when every axis of its idle scale is below this.</summary>
    public const float HiddenScale = 0.01f;

    /// <summary>The names of the bones a DS3 parts binder's idle animation hides, or none.</summary>
    public static HashSet<string> IdleHiddenBones(IBinder parts, out string note)
    {
        note = "";
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anibndFile = parts.Files.FirstOrDefault(f => f.Name.EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase));
        if (anibndFile is null)
            return hidden;
        try
        {
            var bytes = DCX.Is(anibndFile.Bytes) ? DCX.Decompress(anibndFile.Bytes).ToArray() : anibndFile.Bytes;
            if (!BND4.Is(bytes))
            {
                note = "anibnd isn't a BND4";
                return hidden;
            }
            var anibnd = BND4.Read(bytes);
            var idle = anibnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith("a999_000000", StringComparison.OrdinalIgnoreCase))
                       ?? anibnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith("a000_000000", StringComparison.OrdinalIgnoreCase));
            if (idle is null)
            {
                note = "no idle animation in the anibnd";
                return hidden;
            }
            List<string>? boneNames = null;
            foreach (var f in anibnd.Files.Where(f => f != idle && f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var skeleton = HKX.Read(f.Bytes, HKX.HKXVariation.HKXDS3, false).DataSection.Objects.OfType<HKX.HKASkeleton>().FirstOrDefault();
                    if (skeleton is null)
                        continue;
                    boneNames = Enumerable.Range(0, (int)skeleton.Bones.Size).Select(i => skeleton.Bones[i].Name.GetString()).ToList();
                    break;
                }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            }
            if (boneNames is null)
            {
                note = "no skeleton in the anibnd";
                return hidden;
            }
            var hkx = HKX.Read(idle.Bytes, HKX.HKXVariation.HKXDS3, false);
            var anim = hkx.DataSection.Objects.OfType<HKX.HKASplineCompressedAnimation>().FirstOrDefault();
            if (anim is null)
            {
                note = "the idle animation isn't spline-compressed";
                return hidden;
            }
            var binding = hkx.DataSection.Objects.OfType<HKX.HKAAnimationBinding>().FirstOrDefault();
            int tracks = anim.TransformTrackCount;
            var samples = SplineCompressedAnimation.ReadSplCmpAnimBytesAndSampleToUncomp(anim.GetData(), tracks, anim.BlockCount, anim.FrameCount, anim.FramesPerBlock);
            for (int t = 0; t < tracks && t < samples.Count; t++)
            {
                int bone = binding is not null && binding.TransformTrackToBoneIndices.Size > 0 ? binding.TransformTrackToBoneIndices[t].data : t;
                var s = samples[t].Scale; // frame 0
                if (bone >= 0 && bone < boneNames.Count && MathF.Abs(s.X) < HiddenScale && MathF.Abs(s.Y) < HiddenScale && MathF.Abs(s.Z) < HiddenScale)
                    hidden.Add(boneNames[bone]);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            note = $"anibnd unreadable ({e.GetType().Name}: {e.Message})";
        }
        return hidden;
    }

    /// <summary>
    /// Pulls every vertex on a hidden bone (or a bone under one) to that bone's rest position. Returns how many
    /// vertices moved; the meshes they belong to are listed in <paramref name="meshes"/>.
    /// </summary>
    public static int Collapse(FLVER2 flver, IReadOnlySet<string> hiddenBones, List<int> meshes)
    {
        if (hiddenBones.Count == 0)
            return 0;
        var nodes = flver.Nodes;
        bool Hidden(int i)
        {
            for (int guard = 0; i >= 0 && i < nodes.Count && guard < 256; i = nodes[i].ParentIndex, guard++)
                if (hiddenBones.Contains(nodes[i].Name))
                    return true;
            return false;
        }
        Vector3 World(int i)
        {
            var m = Matrix4x4.Identity;
            for (int guard = 0; i >= 0 && i < nodes.Count && guard < 256; i = nodes[i].ParentIndex, guard++)
                m *= nodes[i].ComputeLocalTransform();
            return m.Translation;
        }
        var hidden = Enumerable.Range(0, nodes.Count).Where(Hidden).ToHashSet();
        if (hidden.Count == 0)
            return 0;
        var worldOf = new Dictionary<int, Vector3>();
        int moved = 0;
        for (int mi = 0; mi < flver.Meshes.Count; mi++)
        {
            var mesh = flver.Meshes[mi];
            int before = moved;
            foreach (var v in mesh.Vertices)
            {
                int local = 0;
                if (mesh.UseBoneWeights)
                {
                    float best = -1;
                    for (int k = 0; k < 4; k++)
                        if (v.BoneWeights[k] > best)
                            (best, local) = (v.BoneWeights[k], v.BoneIndices[k]);
                }
                else
                    local = v.BoneIndices[0];
                int node = mesh.BoneIndices.Count > 0 ? (local >= 0 && local < mesh.BoneIndices.Count ? mesh.BoneIndices[local] : -1) : mesh.NodeIndex;
                if (node < 0 || !hidden.Contains(node) || v.Positions.Count == 0)
                    continue;
                if (!worldOf.TryGetValue(node, out var at))
                    worldOf[node] = at = World(node);
                v.Position = at;
                moved++;
            }
            if (moved > before)
                meshes.Add(mi);
        }
        return moved;
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
        var flver = new FLVER2();
        flver.Nodes.Add(new FLVER.Node { Name = "WP_A_0503", ParentIndex = -1, FirstChildIndex = 1 });
        flver.Nodes.Add(new FLVER.Node { Name = "Case", ParentIndex = 0, FirstChildIndex = 2, Translation = new Vector3(0, 0.1f, 0) });
        flver.Nodes.Add(new FLVER.Node { Name = "Case_Tip", ParentIndex = 1, Translation = new Vector3(0, 0.5f, 0) });
        var blade = new FLVER2.Mesh { NodeIndex = 0, BoneIndices = [0] };
        var sheath = new FLVER2.Mesh { NodeIndex = 1, BoneIndices = [0, 1, 2] };
        FLVER.Vertex V(float y, int bone) => new() { Positions = [new Vector3(0.02f, y, 0)], BoneIndices = new FLVER.VertexBoneIndices { [0] = bone } };
        blade.Vertices.AddRange([V(0.3f, 0), V(0.9f, 0)]);
        sheath.Vertices.AddRange([V(0.2f, 1), V(0.8f, 2), V(0.4f, 0)]);
        flver.Meshes.AddRange([blade, sheath]);
        var meshes = new List<int>();
        int moved = Collapse(flver, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "case" }, meshes);
        Check(moved == 2, "hidden parts: the vertices on Case and on the bone under it move (2), the blade's don't");
        Check(meshes.SequenceEqual([1]), "hidden parts: only the scabbard mesh is listed");
        Check(sheath.Vertices[0].Position == new Vector3(0, 0.1f, 0) && Vector3.Distance(sheath.Vertices[1].Position, new Vector3(0, 0.6f, 0)) < 1e-5f,
            "hidden parts: a vertex goes to its own bone's rest position (Case_Tip at 0.6 under Case)");
        Check(blade.Vertices[1].Position == new Vector3(0.02f, 0.9f, 0) && sheath.Vertices[2].Position == new Vector3(0.02f, 0.4f, 0),
            "hidden parts: vertices on visible bones keep their place, even in the scabbard's mesh");
        Check(Collapse(flver, new HashSet<string>(), meshes) == 0, "hidden parts: nothing hidden, nothing moves");
        Console.WriteLine(failures == 0 ? "hidden-parts selftest: all checks passed" : $"hidden-parts selftest: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
