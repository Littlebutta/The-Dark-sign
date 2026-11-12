using System.Numerics;
using System.Text.RegularExpressions;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's posed objects: the corpses and skeletons lying around its maps.
///
/// A DS3 corpse is an object whose model is a skinned body in its bind pose (o000250: one skinned mesh on a 61-bone
/// skeleton) with a binder of poses (o000250 has 36: 0, 1, 10, 20 ... 312). Each placement picks its pose in the map
/// file: MSB3 object AnimIDs[0], "automatically playing animations; only the first is used" (SoulsFormats), e.g. 10
/// for a corpse slumped against a wall. Measured on the ported maps (2026-11-12): 148 o000250 placements and 75
/// o302590 ones carry a pose, against 5,407 objects with none.
///
/// Elden Ring's own corpses work the same way (AEG099_610, posed by the asset part's UnkModelMaskAndAnimID: 0, 1, 10,
/// 20 ... on Stormveil), but the port draws objects on map piece materials, which don't skin: a skinned mesh was made
/// rigid on the root in its bind pose, so every corpse stood in a T-pose (Devin's 1.0.417 playtest). So the pose is
/// baked: the animation's last frame is read (DS3's spline-compressed Havok, decoded by SoulsAssetPipeline as the
/// weapon port does), every bone's pose computed down the model's node tree, and every vertex moved by its bone
/// weights from the bind pose into the posed one (linear blend skinning, as the game does it). The result is an
/// ordinary rigid model, one asset per model and pose (<see cref="ObjectPort"/> numbers them after the plain ones).
///
/// Off (fallback map-objects-poses B, DARKSIGN_OBJECT_POSES=0): corpses in their bind pose, as up to 1.0.42x.
/// </summary>
public static partial class ObjectPoses
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_OBJECT_POSES") != "0";

    /// <summary>The pose a DS3 object part asks for (its first auto-played animation), or -1.</summary>
    public static int PoseOf(MSB3.Part.Object part) => part.AnimIDs is { Length: > 0 } ids && ids[0] >= 0 ? ids[0] : -1;

    /// <summary>The key of a posed copy of a model in the object port's asset table: "o000250@10".</summary>
    public static string Key(string model, int pose) => $"{model.ToLowerInvariant()}@{pose}";

    public readonly record struct Local(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
    {
        public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
    }

    /// <summary>Whether the model has a skinned mesh (one that a pose moves vertex by vertex).</summary>
    public static bool Skinned(FLVER2 flver) => flver.Meshes.Any(m => m.UseBoneWeights && m.Vertices.Count > 0);

    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimLeaf();

    /// <summary>
    /// The local transform of every bone the animation moves, by bone name, at the animation's last frame (a pose
    /// clip holds still; one that settles into its pose ends there). Throws when the objbnd has no such animation.
    /// </summary>
    public static Dictionary<string, Local> Sample(BND4 objbnd, int animId)
    {
        var anibndFile = objbnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidDataException("the objbnd has no animation binder");
        var anibnd = BND4.Read(DCX.Is(anibndFile.Bytes) ? DCX.Decompress(anibndFile.Bytes) : anibndFile.Bytes);
        var skeletonFile = anibnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidDataException("the animation binder has no skeleton.hkx");
        var animFile = anibnd.Files.FirstOrDefault(f => AnimLeaf().Match(ItemText.Leaf(f.Name)) is { Success: true } m
                                                        && int.Parse(m.Groups[1].Value) * 1_000_000 + int.Parse(m.Groups[2].Value) == animId)
                       ?? throw new InvalidDataException($"the animation binder has no animation {animId}");
        var skeleton = AnimPort.ReadDs3Skeleton(skeletonFile.Bytes);
        var hkx = HKX.Read(animFile.Bytes, HKX.HKXVariation.HKXDS3, false);
        var anim = hkx.DataSection.Objects.OfType<HKX.HKASplineCompressedAnimation>().FirstOrDefault()
                   ?? throw new InvalidDataException($"animation {animId} isn't spline-compressed");
        var binding = hkx.DataSection.Objects.OfType<HKX.HKAAnimationBinding>().FirstOrDefault();
        int tracks = anim.TransformTrackCount, frames = Math.Max(1, anim.FrameCount);
        var samples = SplineCompressedAnimation.ReadSplCmpAnimBytesAndSampleToUncomp(anim.GetData(), tracks, anim.BlockCount, anim.FrameCount, anim.FramesPerBlock);
        int frame = Math.Min(frames - 1, samples.Count / Math.Max(1, tracks) - 1);
        if (frame < 0)
            throw new InvalidDataException($"animation {animId} has no frames");
        var result = new Dictionary<string, Local>(StringComparer.OrdinalIgnoreCase);
        for (int t = 0; t < tracks; t++)
        {
            int bone = binding is not null && binding.TransformTrackToBoneIndices.Size > 0 ? binding.TransformTrackToBoneIndices[t].data : t;
            if (bone < 0 || bone >= skeleton.Names.Count)
                continue;
            var s = samples[frame * tracks + t];
            result.TryAdd(skeleton.Names[bone], new Local(s.Translation, Quaternion.Normalize(s.Rotation), s.Scale));
        }
        return result;
    }

    /// <summary>
    /// Moves the model into a pose: each node takes the pose's local transform when the pose names it (else its own
    /// rest transform), skinned vertices follow their bone weights from the rest pose (the FLVER's node tree, which
    /// DS3 skins against) to the posed one, and vertices of unskinned meshes follow the node they hang off. Every mesh
    /// ends rigid on the root, in the root's rest space (as <see cref="MapModelPort.BakeNodes"/> leaves objects), so
    /// the rest of the object conversion treats it as a still model. Returns how many vertices moved.
    /// </summary>
    public static int Apply(FLVER2 flver, IReadOnlyDictionary<string, Local> pose)
    {
        int count = flver.Nodes.Count;
        if (count == 0)
            return 0;
        var rest = MapModelPort.WorldTransforms(flver);
        var posed = new Matrix4x4[count];
        var done = new bool[count];
        Matrix4x4 Posed(int i, int depth)
        {
            if (i < 0 || i >= count || depth > 64)
                return Matrix4x4.Identity;
            if (done[i])
                return posed[i];
            var node = flver.Nodes[i];
            var local = node.Name is not null && pose.TryGetValue(node.Name, out var p) ? p.Matrix : node.ComputeLocalTransform();
            var parent = node.ParentIndex;
            posed[i] = parent >= 0 && parent < count && parent != i ? local * Posed(parent, depth + 1) : local;
            done[i] = true;
            return posed[i];
        }
        if (!Matrix4x4.Invert(rest[0], out var rootInverse))
            rootInverse = Matrix4x4.Identity;
        // Bind pose -> posed, per node, then into the root's rest space.
        var skin = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
            skin[i] = (Matrix4x4.Invert(rest[i], out var inverse) ? inverse : Matrix4x4.Identity) * Posed(i, 0) * rootInverse;

        int moved = 0;
        foreach (var mesh in flver.Meshes)
        {
            int Node(int index) => mesh.BoneIndices.Count > 0
                ? index >= 0 && index < mesh.BoneIndices.Count ? mesh.BoneIndices[index] : -1
                : index;
            foreach (var v in mesh.Vertices)
            {
                Matrix4x4 m;
                if (mesh.UseBoneWeights)
                {
                    m = default;
                    float total = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = v.BoneWeights[k];
                        int node = Node(v.BoneIndices[k]);
                        if (w <= 0 || node < 0 || node >= count)
                            continue;
                        m += skin[node] * w;
                        total += w;
                    }
                    if (total <= 1e-6f)
                    {
                        int node = mesh.NodeIndex >= 0 && mesh.NodeIndex < count ? mesh.NodeIndex : 0;
                        m = skin[node];
                    }
                    else if (MathF.Abs(total - 1) > 1e-4f)
                        m *= 1 / total;
                }
                else
                {
                    // Unskinned: the vertex is in the space of the node its NormalW names (see MapModelPort.BakeNodes).
                    int index = v.NormalWs.Count > 0 ? v.NormalWs[0] : 0;
                    int node = Node(index);
                    if (node < 0 || node >= count)
                        node = mesh.NodeIndex >= 0 && mesh.NodeIndex < count ? mesh.NodeIndex : 0;
                    m = Posed(node, 0) * rootInverse;
                }
                if (m.IsIdentity)
                    continue;
                moved++;
                v.Position = Vector3.Transform(v.Position, m);
                v.Normal = Unit(Vector3.TransformNormal(v.Normal, m), v.Normal);
                for (int t = 0; t < v.Tangents.Count; t++)
                {
                    var tangent = v.Tangents[t];
                    v.Tangents[t] = new Vector4(Unit(Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), m), new Vector3(tangent.X, tangent.Y, tangent.Z)), tangent.W);
                }
                var b = new Vector3(v.Bitangent.X, v.Bitangent.Y, v.Bitangent.Z);
                v.Bitangent = new Vector4(Unit(Vector3.TransformNormal(b, m), b), v.Bitangent.W);
            }
            mesh.UseBoneWeights = false;
            mesh.NodeIndex = 0;
            mesh.BoneIndices = [];
            foreach (var v in mesh.Vertices)
                for (int i = 0; i < v.NormalWs.Count; i++)
                    v.NormalWs[i] = 0;
        }
        return moved;
    }

    private static Vector3 Unit(Vector3 v, Vector3 fallback)
    {
        var length = v.Length();
        return length > 1e-6f && float.IsFinite(length) ? v / length : fallback;
    }
}
