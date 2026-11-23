using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// The template's limb IK on a ported enemy. An Elden Ring character's own behaviour graph (Behaviors\cXXXX.hkx in
/// its behbnd) can pin limbs to "target" bones its animations move: Dragonlord Placidusax pins all four feet
/// (hkbHandIkControlsModifier, four hands at full weight, each bound to the handle an hkbGetHandleOnBoneModifier
/// reads off L/R_Foot_Target and L/R_Hand_Target). On a ported enemy every animation is DS3's, and a target bone DS3
/// has no bone of is never moved again: it stays where the template rests it. Father Ariandel (c6010 on Placidusax,
/// 1.0.460) shares the dragon's front-leg bone names for his arms, so Elden Ring stretched both his arms towards the
/// dragon's front feet, 12 m in front of him and 6 m to each side, in every pose ("his pose is broken"); his feet
/// were fine, because DS3 has Foot_Target bones of its own and keeps them on his feet.
/// The rule, for every ported enemy: a limb whose IK target is a bone DS3 doesn't animate is no longer pinned (the
/// limb plays DS3's animation as it is); a limb whose target is a DS3 bone keeps the template's IK. Method (RULE
/// 3): the modifiers, their variable bindings and bone numbers are Elden Ring's own c4520.behbnd read with HKLib;
/// switching a hand off is the modifier's own per-hand enable flag.
/// </summary>
public static class ChrIk
{
    /// <summary>What <see cref="Fit(IHavokObject, IReadOnlyList{string}, IReadOnlySet{string})"/> did: the limbs that had IK and the target bones of those switched off.</summary>
    public readonly record struct Result(int Limbs, IReadOnlyList<string> Off);

    /// <summary>
    /// Every behaviour file of the character's behbnd (see <see cref="Fit(IHavokObject, IReadOnlyList{string}, IReadOnlySet{string})"/>);
    /// a file is rewritten only when a limb was switched off.
    /// </summary>
    /// <param name="bones">The built skeleton's bone names, by number (the template's numbers are kept by the merge).</param>
    /// <param name="ds3Bones">The bones DS3's animations move: the DS3 skeleton's names.</param>
    public static Result Fit(BND4 behbnd, IReadOnlyList<string> bones, IReadOnlySet<string> ds3Bones)
    {
        var compendium = behbnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
        int limbs = 0;
        var off = new List<string>();
        foreach (var file in behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
            lock (AnimPort.HavokLock)
            {
                IHavokObject root;
                try { root = AnimPort.ReadEr(file.Bytes, compendium); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
                var result = Fit(root, bones, ds3Bones);
                limbs += result.Limbs;
                if (result.Off.Count == 0)
                    continue;
                off.AddRange(result.Off);
                using var output = new MemoryStream();
                new HavokBinarySerializer().Write(root, output);
                file.Bytes = output.ToArray();
            }
        return new Result(limbs, off);
    }

    /// <summary>
    /// One graph: each enabled hand of an hkbHandIkControlsModifier whose target handle comes (through the graph
    /// variable both are bound to) from an hkbGetHandleOnBoneModifier on a bone that isn't DS3's is switched off,
    /// with its weights at zero. A hand whose target can't be traced is left as it is.
    /// </summary>
    public static Result Fit(IHavokObject root, IReadOnlyList<string> bones, IReadOnlySet<string> ds3Bones)
    {
        var all = HavokGraph.All(root);
        // Graph variable -> the bone whose handle is written to it.
        var handleBone = new Dictionary<int, int>();
        foreach (var handle in all.OfType<hkbGetHandleOnBoneModifier>())
            foreach (var binding in handle.m_variableBindingSet?.m_bindings ?? [])
                if (binding.m_memberPath == "handleOut" && binding.m_bindingType == hkbVariableBindingSet.Binding.BindingType.BINDING_TYPE_VARIABLE)
                    handleBone.TryAdd(binding.m_variableIndex, handle.m_animationBoneIndex);
        int limbs = 0;
        var off = new List<string>();
        foreach (var controls in all.OfType<hkbHandIkControlsModifier>())
            for (int i = 0; i < controls.m_hands.Count; i++)
            {
                var hand = controls.m_hands[i];
                if (!hand.m_enable)
                    continue;
                limbs++;
                var bound = (controls.m_variableBindingSet?.m_bindings ?? [])
                    .FirstOrDefault(b => b.m_memberPath == $"hands:{i}/controlData/targetHandle" && b.m_bindingType == hkbVariableBindingSet.Binding.BindingType.BINDING_TYPE_VARIABLE);
                if (bound is null || !handleBone.TryGetValue(bound.m_variableIndex, out int bone) || bone < 0 || bone >= bones.Count)
                    continue;
                if (ds3Bones.Contains(bones[bone]))
                    continue;
                hand.m_enable = false;
                hand.m_controlData.m_transformOnFraction = 0;
                hand.m_controlData.m_normalOnFraction = 0;
                off.Add(bones[bone]);
            }
        return new Result(limbs, off);
    }
}
