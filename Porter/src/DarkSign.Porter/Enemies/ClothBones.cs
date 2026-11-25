using System.Numerics;
using HKLib.hk2018;
using Ds3 = HKX2;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// DS3 armour pieces that its cloth simulation moves, kept on the body that simulation followed.
///
/// A DS3 character's chrbnd carries a Havok Cloth file (cXXXX_c.hkx) whose hclSimpleMeshBoneDeformOperators write the
/// transforms of some skeleton bones each frame from a simulated mesh: Iudex Gundyr's shoulder plates
/// (XXX_L_/XXX_R_ shoulder001/002), his waist plates (XXX_*_kosiarmor) and the Pus of Man's arm (XXX_jyude...). Those
/// bones hang off a helper (XXX_cloth) right under the root, and no DS3 animation has a track for them: the cloth is
/// all that moves them. The port has no cloth (the template's simulates the template's meshes and is left out, and
/// Havok Cloth 2014 does not convert), so these bones stood where they rest under the root while the body knelt,
/// rolled and swung: Gundyr's shoulder plates hovered above him (1.0.640 playtest, 2026-11-25).
///
/// Each bone the cloth drives (its operator's triangle-bone pair names the bone by byte offset, 64 bytes per
/// transform, into the cloth's transform set, which is the character's whole skeleton) is moved under the bone of
/// that cloth's skin operators (the bones its simulated mesh is skinned to: Gundyr's shoulder cloth is skinned to the
/// clavicles and upper arms) that rests nearest to it, keeping its rest place. The piece then moves rigidly with that
/// bone instead of swinging, but stays on the body. Bones below a moved bone keep their parent. Reading the cloth file
/// uses Smithbox's HKX2 (MIT).
/// </summary>
public static class ClothBones
{
    public sealed record Move(string Bone, string From, string To, float Distance);

    /// <summary>The bones each cloth drives and the bones that cloth's mesh is skinned to, by name.</summary>
    public static List<(string Cloth, HashSet<string> Driven, List<string> Skin)> Read(byte[] ds3Cloth)
    {
        var root = Maps.CollisionPort.ReadDs3(ds3Cloth);
        var skeleton = root.m_namedVariants?.Select(v => v.m_variant).OfType<Ds3.hkaAnimationContainer>().FirstOrDefault()?.m_skeletons?.FirstOrDefault();
        var result = new List<(string, HashSet<string>, List<string>)>();
        if (skeleton?.m_bones is null)
            return result;
        string? Name(int i) => i >= 0 && i < skeleton.m_bones.Count ? skeleton.m_bones[i].m_name : null;
        foreach (var cloth in root.m_namedVariants!.Select(v => v.m_variant).OfType<Ds3.hclClothContainer>().SelectMany(c => c.m_clothDatas ?? []))
        {
            var driven = (cloth.m_operators ?? []).OfType<Ds3.hclSimpleMeshBoneDeformOperator>()
                .SelectMany(o => o.m_triangleBonePairs ?? []).Select(p => Name(p.m_boneOffset / 64)).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (driven.Count == 0)
                continue;
            var skin = (cloth.m_operators ?? []).OfType<Ds3.hclObjectSpaceSkinOperator>().SelectMany(o => o.m_transformSubset ?? [])
                .Select(i => Name(i)).OfType<string>().Where(n => !driven.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Add((cloth.m_name ?? "", driven, skin));
        }
        return result;
    }

    /// <summary>
    /// Moves each cloth-driven bone of <paramref name="skeleton"/> (whose parent the cloth doesn't drive too) under the
    /// nearest bone its cloth is skinned to, keeping its rest place. Only moves under a bone earlier in the list
    /// (Havok wants parents first).
    /// </summary>
    public static List<Move> Reparent(hkaSkeleton skeleton, IEnumerable<(string Cloth, HashSet<string> Driven, List<string> Skin)> cloths)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < skeleton.m_bones.Count; b++)
            index.TryAdd(skeleton.m_bones[b].m_name ?? "", b);
        var world = World(skeleton);
        var moves = new List<Move>();
        foreach (var (_, driven, skin) in cloths)
        {
            var candidates = skin.Where(index.ContainsKey).Select(n => index[n]).ToList();
            if (candidates.Count == 0)
                continue;
            foreach (var name in driven.Where(index.ContainsKey).Order(StringComparer.Ordinal))
            {
                int b = index[name], parent = skeleton.m_parentIndices[b];
                if (parent >= 0 && driven.Contains(skeleton.m_bones[parent].m_name ?? ""))
                    continue; // follows its driven parent
                var at = world[b].Translation;
                int to = candidates.Where(c => c < b).OrderBy(c => (world[c].Translation - at).Length()).DefaultIfEmpty(-1).First();
                if (to < 0 || to == parent)
                    continue;
                Matrix4x4.Invert(world[to], out var inverse);
                Matrix4x4.Decompose(world[b] * inverse, out var scale, out var rotation, out var translation);
                skeleton.m_parentIndices[b] = (short)to;
                skeleton.m_referencePose[b] = new hkQsTransform
                {
                    m_translation = new Vector4(translation, 0),
                    m_rotation = Quaternion.Normalize(rotation),
                    m_scale = new Vector4(scale, 0),
                };
                moves.Add(new Move(name, parent >= 0 ? skeleton.m_bones[parent].m_name ?? "" : "", skeleton.m_bones[to].m_name ?? "", (world[to].Translation - at).Length()));
            }
        }
        return moves;
    }

    public static Matrix4x4[] World(hkaSkeleton s)
    {
        var w = new Matrix4x4[s.m_bones.Count];
        for (int i = 0; i < w.Length && i < s.m_referencePose.Count; i++)
        {
            var t = s.m_referencePose[i];
            var m = Matrix4x4.CreateScale(t.m_scale.X, t.m_scale.Y, t.m_scale.Z) * Matrix4x4.CreateFromQuaternion(t.m_rotation)
                    * Matrix4x4.CreateTranslation(t.m_translation.X, t.m_translation.Y, t.m_translation.Z);
            int p = s.m_parentIndices[i];
            w[i] = p >= 0 && p < i ? m * w[p] : m;
        }
        return w;
    }
}
