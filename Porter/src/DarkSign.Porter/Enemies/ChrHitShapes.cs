using System.Numerics;
using System.Reflection;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using HKLib.ValueTypes;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// A ported enemy's hit shapes: DS3's own, not the Elden Ring template's.
///
/// Both games keep a character's hit shapes in its chrbnd's cXXXX.hkx: the "ragdoll" (an hknpRagdollData of keyframed
/// capsules, one per body part, that follow the animated bones while the character lives and fall as a ragdoll when
/// it dies), two hkaSkeletonMappers between the ragdoll's skeleton and the animation skeleton, and a copy of the
/// animation skeleton itself; cXXXX.hkxpwv holds a 16-byte record per body. Until this the port kept the template's
/// file byte for byte, so every DS3 enemy was hit through the template's capsules: Iudex Gundyr on c3060 had 22 bodies
/// of a 2.3 m knight (pelvis radius 0.16 m, thighs 0.08 m, head 0.12 m) inside a 3 m armoured body whose DS3 capsules are
/// 0.36, 0.25 and 0.21 m, and none at all for his Pus of Man form (DS3's 30 "Ragdoll_Soul" bodies, radius up to 1.5 m):
/// swings through his armour met nothing, only the head was ever hit (1.0.640 playtest, 2026-11-25).
///
/// DS3 (Havok 2014) and Elden Ring (2018) both use hknp physics, so DS3's ragdoll converts almost field for field:
/// <list type="bullet">
/// <item>bodies: DS3's capsules and hulls (<see cref="Maps.CollisionPort.ConvertConvex"/>), positions, collision filter
/// info (both games use the same layer scheme: Gundyr's pelvis 0x8020 = c3060's), materials, motion properties and
/// mass; every other body field as the template's first body has it (keyframed, no attachment);</item>
/// <item>constraints: each DS3 joint on a clone of the template's joint of the same kind (ragdoll / limited hinge,
/// wrapped as breakable as the template wraps it), with DS3's pivots, limits and motor targets;</item>
/// <item>skeletons: the animation skeleton copy becomes the ported one (the merged skeleton the anibnd carries, so the
/// mappers' bone numbers are the numbers the animation pose uses), the ragdoll skeleton DS3's;</item>
/// <item>mappers: DS3's two, bones renumbered by name onto the merged skeleton (whose DS3 bones rest where DS3 rests
/// them, so DS3's bone-to-body transforms hold);</item>
/// <item>hkxpwv: written in Elden Ring's version 2 layout for DS3's body and bone counts (all but the per-body
/// records are fixed fill values in both games), each body's record from DS3 with the part flag Elden Ring adds
/// (0x80 trunk and thighs, 0x20 calves and feet, 0x10 arms and head, as c3060 sets them).</item>
/// </list>
/// The DS3 reader is Smithbox's HKX2 (MIT), the writer HKLib (see CREDITS.md).
/// </summary>
public static class ChrHitShapes
{
    public sealed record Result(byte[] Hkx, byte[] Pwv, int Bodies, int Constraints, int Mapped, string Note);

    /// <summary>
    /// The ported character's physics file and its hkxpwv: DS3's ragdoll (<paramref name="ds3Hkx"/>) on the template's
    /// file (<paramref name="templateHkx"/>), mapped onto <paramref name="merged"/>. Throws when either file lacks what
    /// it needs; the caller then keeps the template's.
    /// </summary>
    public static Result Convert(byte[] templateHkx, byte[] ds3Hkx, hkaSkeleton merged, byte[]? ds3Pwv = null)
    {
        var root = (hkRootLevelContainer)new HavokBinarySerializer().Read(new MemoryStream(templateHkx));
        var container = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>().FirstOrDefault()
                        ?? throw new InvalidDataException("template physics file has no animation container");
        var ragdoll = root.m_namedVariants.Select(v => v.m_variant).OfType<hknpRagdollData>().FirstOrDefault()
                      ?? root.m_namedVariants.Select(v => v.m_variant).OfType<hknpPhysicsSceneData>().SelectMany(s => s.m_systemDatas).OfType<hknpRagdollData>().FirstOrDefault()
                      ?? throw new InvalidDataException("template physics file has no ragdoll");
        if (ragdoll.m_bodyCinfos.Count == 0)
            throw new InvalidDataException("template ragdoll has no bodies");
        var templateMappers = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaSkeletonMapper>().ToList();
        if (templateMappers.Count == 0)
            throw new InvalidDataException("template physics file has no skeleton mapper");

        var ds3Root = Maps.CollisionPort.ReadDs3(ds3Hkx);
        var ds3Rag = ds3Root.m_namedVariants.Select(v => v.m_variant).OfType<Ds3.hknpPhysicsSceneData>().SelectMany(s => s.m_systemDatas ?? [])
                         .OfType<Ds3.hknpRagdollData>().FirstOrDefault()
                     ?? throw new InvalidDataException("DS3 physics file has no ragdoll");
        if (ds3Rag.m_skeleton is null || ds3Rag.m_bodyCinfos is not { Count: > 0 })
            throw new InvalidDataException("DS3 ragdoll has no skeleton or bodies");
        var ds3Mappers = ds3Root.m_namedVariants.Select(v => v.m_variant).OfType<Ds3.hkaSkeletonMapper>().ToList();
        var ds3Master = ds3Root.m_namedVariants.Select(v => v.m_variant).OfType<Ds3.hkaAnimationContainer>().FirstOrDefault()?.m_skeletons?
                            .FirstOrDefault(s => s != ds3Rag.m_skeleton)
                        ?? throw new InvalidDataException("DS3 physics file has no animation skeleton");

        // Skeletons: the merged animation skeleton and DS3's ragdoll skeleton.
        var ragSkeleton = Skeleton(ds3Rag.m_skeleton);
        var templateMaster = container.m_skeletons.FirstOrDefault(s => s != ragdoll.m_skeleton) ?? container.m_skeletons[0];
        container.m_skeletons.Clear();
        container.m_skeletons.Add(merged);
        container.m_skeletons.Add(ragSkeleton);

        // Bodies.
        var proto = ragdoll.m_bodyCinfos[0];
        var bodies = new List<hknpPhysicsSystemData.bodyCinfoWithAttachment>();
        foreach (var b in ds3Rag.m_bodyCinfos)
        {
            var body = Clone(proto);
            body.m_shape = Shape(b.m_shape) ?? throw new InvalidDataException($"DS3 body {b.m_name}: unsupported shape {b.m_shape?.GetType().Name}");
            body.m_name = b.m_name;
            body.m_position = b.m_position;
            body.m_orientation = b.m_orientation;
            body.m_collisionFilterInfo = b.m_collisionFilterInfo;
            body.m_materialId = b.m_materialId;
            body.m_motionPropertiesId = (ushort)b.m_motionId;
            body.m_userData = b.m_userData;
            body.m_collisionLookAheadDistance = b.m_collisionLookAheadDistance;
            if (MassOf(b.m_shape) is > 0 and var mass)
                body.m_mass = mass;
            body.m_massDistribution = null;
            body.m_attachedBody = -1;
            bodies.Add(body);
        }

        // Constraints on clones of the template's of the same kind.
        var protos = ragdoll.m_constraintCinfos.Where(c => c.m_constraintData is not null)
            .GroupBy(c => Inner(c.m_constraintData!).GetType()).ToDictionary(g => g.Key, g => g.First());
        var constraints = new List<hknpConstraintCinfo>();
        int dropped = 0;
        foreach (var c in ds3Rag.m_constraintCinfos ?? [])
        {
            Type? kind = c.m_constraintData switch
            {
                Ds3.hkpRagdollConstraintData => typeof(hkpRagdollConstraintData),
                Ds3.hkpLimitedHingeConstraintData => typeof(hkpLimitedHingeConstraintData),
                _ => null,
            };
            // A kind the template has none of borrows the other kind's wrapping.
            if (kind is null || !protos.TryGetValue(kind, out var cp) && !protos.TryGetValue(typeof(hkpRagdollConstraintData), out cp))
            {
                dropped++;
                continue;
            }
            var made = Clone(cp);
            made.m_bodyA = new hknpBodyId { m_serialAndIndex = c.m_bodyA };
            made.m_bodyB = new hknpBodyId { m_serialAndIndex = c.m_bodyB };
            made.m_name = cp.m_name;
            var data = Inner(made.m_constraintData!);
            switch (c.m_constraintData)
            {
                case Ds3.hkpRagdollConstraintData r when data is hkpRagdollConstraintData er:
                    Joint(r.m_atoms, er);
                    break;
                case Ds3.hkpLimitedHingeConstraintData h when data is hkpLimitedHingeConstraintData eh:
                    Hinge(h.m_atoms, eh);
                    break;
                case Ds3.hkpLimitedHingeConstraintData h when data is hkpRagdollConstraintData er:
                    // No hinge in the template: a ragdoll joint at the hinge's pivots, cone shut, twist the hinge's range.
                    er.m_atoms.m_transforms.m_transformA = h.m_atoms.m_transforms.m_transformA;
                    er.m_atoms.m_transforms.m_transformB = h.m_atoms.m_transforms.m_transformB;
                    er.m_atoms.m_twistLimit.m_minAngle = h.m_atoms.m_angLimit.m_minAngle;
                    er.m_atoms.m_twistLimit.m_maxAngle = h.m_atoms.m_angLimit.m_maxAngle;
                    er.m_atoms.m_coneLimit.m_maxAngle = 0.01f;
                    break;
            }
            constraints.Add(made);
        }

        ragdoll.m_skeleton = ragSkeleton;
        ragdoll.m_boneToBodyMap.Clear();
        ragdoll.m_boneToBodyMap.AddRange(ds3Rag.m_boneToBodyMap ?? Enumerable.Range(0, bodies.Count).ToList());
        ragdoll.m_bodyTags.Clear();
        ragdoll.m_materials.Clear();
        foreach (var m in ds3Rag.m_materials ?? [])
            ragdoll.m_materials.Add(Maps.CollisionPort.ConvertMaterial(m));
        ragdoll.m_motionProperties.Clear();
        foreach (var m in ds3Rag.m_motionProperties ?? [])
            ragdoll.m_motionProperties.Add(Maps.CollisionPort.ConvertMotionProperties(m));
        ragdoll.m_bodyCinfos.Clear();
        ragdoll.m_bodyCinfos.AddRange(bodies);
        ragdoll.m_constraintCinfos.Clear();
        ragdoll.m_constraintCinfos.AddRange(constraints);
        var fixes = new List<string>();
        Maps.CollisionPort.ReferenceIds(ragdoll, fixes);

        // Mappers: DS3's, renumbered onto the merged skeleton.
        var index = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);
        for (short i = 0; i < merged.m_bones.Count; i++)
            index.TryAdd(merged.m_bones[i].m_name ?? "", i);
        int mapped = 0, lost = 0;
        foreach (var tm in templateMappers)
        {
            bool masterIsA = tm.m_mapping.m_skeletonA != ragdoll.m_skeleton && tm.m_mapping.m_skeletonA?.m_name == templateMaster.m_name
                             || tm.m_mapping.m_skeletonA == templateMaster;
            var dm = ds3Mappers.FirstOrDefault(m => (m.m_mapping.m_skeletonA == ds3Master) == masterIsA)
                     ?? throw new InvalidDataException("DS3 physics file lacks a mapper direction the template has");
            var map = tm.m_mapping;
            map.m_skeletonA = masterIsA ? merged : ragSkeleton;
            map.m_skeletonB = masterIsA ? ragSkeleton : merged;
            map.m_simpleMappings.Clear();
            map.m_chainMappings.Clear();
            map.m_partitionMap.Clear();
            map.m_simpleMappingPartitionRanges.Clear();
            map.m_chainMappingPartitionRanges.Clear();
            short? Master(short ds3Bone) => ds3Bone >= 0 && ds3Bone < ds3Master.m_bones.Count && index.TryGetValue(ds3Master.m_bones[ds3Bone].m_name ?? "", out var i) ? i : null;
            foreach (var s in dm.m_mapping.m_simpleMappings ?? [])
            {
                short? a = masterIsA ? Master(s.m_boneA) : s.m_boneA, b = masterIsA ? s.m_boneB : Master(s.m_boneB);
                if (a is null || b is null)
                {
                    lost++;
                    continue;
                }
                map.m_simpleMappings.Add(new hkaSkeletonMapperData.SimpleMapping { m_boneA = a.Value, m_boneB = b.Value, m_aFromBTransform = Qs(s.m_aFromBTransform) });
                mapped++;
            }
            map.m_unmappedBones.Clear();
            foreach (var u in dm.m_mapping.m_unmappedBones ?? [])
                if ((masterIsA ? (short?)u : Master(u)) is { } ub)
                    map.m_unmappedBones.Add(ub);
            map.m_extractedMotionMapping = Qs(dm.m_mapping.m_extractedMotionMapping);
            map.m_keepUnmappedLocal = dm.m_mapping.m_keepUnmappedLocal;
        }

        using var output = new MemoryStream();
        new HavokBinarySerializer().Write(root, output);
        // The game resolves each capsule's points through PTCH and a u16 distance (Havok.HavokRelArrays): a file it
        // can't resolve crashes the map load (+0x188434b), so such a file is never shipped; the template's is kept.
        if (Havok.HavokRelArrays.Check(output.ToArray(), Maps.CollisionPort.RelArrayFields(root)) is { Count: > 0 } relProblems)
            throw new InvalidDataException("hit shapes the game can't load: " + string.Join("; ", relProblems.Take(3)));
        var note = $"hit shapes: DS3's {bodies.Count} bodies and {constraints.Count} joints on {merged.m_bones.Count} bones ({mapped} bone links"
                   + (lost > 0 ? $", {lost} to bones the port has not" : "") + (dropped > 0 ? $", {dropped} joints of a kind left out" : "")
                   + (fixes.Count > 0 ? "; " + string.Join("; ", fixes) : "") + ")";
        return new Result(output.ToArray(), Pwv(ds3Rag, merged.m_bones.Count, ds3Pwv), bodies.Count, constraints.Count, mapped, note);
    }

    /// <summary>
    /// The hkxpwv for <paramref name="rag"/>'s bodies on <paramref name="bones"/> bones, in Elden Ring's version 2
    /// layout: header (version, 0, 0, bodies, bones, bodies, 0, 0 as u16), a 4-byte fill per body (FFFF0000), an
    /// 8-byte fill per bone (FFFF000000000000), then a 16-byte record per body: DS3's first six bytes, Elden Ring's
    /// part flag (u16) and 0xFF.
    /// </summary>
    public static byte[] Pwv(Ds3.hknpRagdollData rag, int bones, byte[]? ds3Pwv = null)
    {
        int count = rag.m_bodyCinfos.Count;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var h in new ushort[] { 2, 0, 0, (ushort)count, (ushort)bones, (ushort)count, 0, 0 })
            w.Write(h);
        for (int i = 0; i < 4; i++) w.Write(0); // header is 32 bytes
        for (int i = 0; i < count; i++) w.Write(0x0000FFFFu);
        for (int i = 0; i < bones; i++) { w.Write(0x0000FFFFu); w.Write(0u); }
        for (int i = 0; i < count; i++)
        {
            var record = new byte[16];
            record[0] = record[1] = record[2] = record[3] = 0xFF;
            if (ds3Pwv is not null && Ds3Record(ds3Pwv, i) is { } own)
                Array.Copy(own, record, 6);
            BitConverter.GetBytes(PartFlag(rag.m_bodyCinfos[i].m_name ?? "")).CopyTo(record, 6);
            record[8] = 0xFF;
            w.Write(record);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A DS3 hkxpwv's record for body <paramref name="i"/> (version 1 has the same layout as Elden Ring's 2).</summary>
    public static byte[]? Ds3Record(byte[] pwv, int i)
    {
        if (pwv.Length < 32) return null;
        int bodies = BitConverter.ToUInt16(pwv, 6), bones = BitConverter.ToUInt16(pwv, 8);
        int at = 32 + bodies * 4 + bones * 8 + i * 16;
        return i < bodies && at + 16 <= pwv.Length ? pwv[at..(at + 16)] : null;
    }

    /// <summary>The body-part flag Elden Ring's hkxpwv carries (c3060: trunk, neck and thighs 0x80, calves and feet 0x20, arms, hands and head 0x10).</summary>
    public static ushort PartFlag(string body)
    {
        var n = body.ToLowerInvariant();
        if (n.Contains("calf") || n.Contains("foot") || n.Contains("toe"))
            return 0x20;
        if (n.Contains("arm") || n.Contains("hand") || n.Contains("finger") || n.Contains("head") || n.Contains("jaw"))
            return 0x10;
        return 0x80;
    }

    private static hkpConstraintData Inner(hkpConstraintData d) => d is hkpWrappedConstraintData { m_constraintData: { } inner } ? Inner(inner) : d;

    private static void Joint(Ds3.hkpRagdollConstraintDataAtoms ds3, hkpRagdollConstraintData er)
    {
        var a = er.m_atoms;
        a.m_transforms.m_transformA = ds3.m_transforms.m_transformA;
        a.m_transforms.m_transformB = ds3.m_transforms.m_transformB;
        a.m_twistLimit.m_isEnabled = ds3.m_twistLimit.m_isEnabled;
        a.m_twistLimit.m_twistAxis = ds3.m_twistLimit.m_twistAxis;
        a.m_twistLimit.m_refAxis = ds3.m_twistLimit.m_refAxis;
        a.m_twistLimit.m_minAngle = ds3.m_twistLimit.m_minAngle;
        a.m_twistLimit.m_maxAngle = ds3.m_twistLimit.m_maxAngle;
        Cone(ds3.m_coneLimit, a.m_coneLimit);
        Cone(ds3.m_planesLimit, a.m_planesLimit);
        a.m_ragdollMotors.m_target_bRca = (Matrix3x3)ds3.m_ragdollMotors.m_target_bRca;
    }

    private static void Cone(Ds3.hkpConeLimitConstraintAtom ds3, hkpConeLimitConstraintAtom er)
    {
        er.m_isEnabled = ds3.m_isEnabled;
        er.m_twistAxisInA = ds3.m_twistAxisInA;
        er.m_refAxisInB = ds3.m_refAxisInB;
        er.m_angleMeasurementMode = (hkpConeLimitConstraintAtom.MeasurementMode)(int)ds3.m_angleMeasurementMode;
        er.m_minAngle = ds3.m_minAngle;
        er.m_maxAngle = ds3.m_maxAngle;
    }

    private static void Hinge(Ds3.hkpLimitedHingeConstraintDataAtoms ds3, hkpLimitedHingeConstraintData er)
    {
        var a = er.m_atoms;
        a.m_transforms.m_transformA = ds3.m_transforms.m_transformA;
        a.m_transforms.m_transformB = ds3.m_transforms.m_transformB;
        a.m_angLimit.m_isEnabled = ds3.m_angLimit.m_isEnabled;
        a.m_angLimit.m_limitAxis = ds3.m_angLimit.m_limitAxis;
        a.m_angLimit.m_minAngle = ds3.m_angLimit.m_minAngle;
        a.m_angLimit.m_maxAngle = ds3.m_angLimit.m_maxAngle;
        a.m_2dAng.m_freeRotationAxis = ds3.m_2dAng.m_freeRotationAxis;
    }

    private static hknpShape? Shape(Ds3.hknpShape? shape)
    {
        hknpConvexPolytopeShape? made = shape switch
        {
            Ds3.hknpCapsuleShape cap => Capsule(cap),
            Ds3.hknpConvexPolytopeShape hull => Maps.CollisionPort.ConvertConvex(hull),
            _ => null,
        };
        if (made is not null && MassProperties(shape) is { } props)
        {
            made.m_properties = new hkRefCountedProperties();
            made.m_properties.m_entries.Add(new hkRefCountedProperties.Entry { m_key = props.Key, m_flags = props.Flags, m_object = props.Object });
        }
        return made;
    }

    private static hknpCapsuleShape Capsule(Ds3.hknpCapsuleShape ds3)
    {
        var hull = Maps.CollisionPort.ConvertConvex(ds3);
        return new hknpCapsuleShape
        {
            m_flags = hull.m_flags,
            m_type = hknpShapeType.Enum.CAPSULE,
            m_numShapeKeyBits = hull.m_numShapeKeyBits,
            m_dispatchType = hull.m_dispatchType,
            m_convexRadius = hull.m_convexRadius,
            m_userData = hull.m_userData,
            m_maxAllowedPenetration = hull.m_maxAllowedPenetration,
            m_vertices = hull.m_vertices,
            m_planes = hull.m_planes,
            m_faces = hull.m_faces,
            m_indices = hull.m_indices,
            m_a = ds3.m_a,
            m_b = ds3.m_b,
        };
    }

    private static (ushort Key, ushort Flags, hknpShapeMassProperties Object)? MassProperties(Ds3.hknpShape? shape)
    {
        var entry = shape?.m_properties?.m_entries?.FirstOrDefault(e => e.m_object is Ds3.hknpShapeMassProperties);
        if (entry?.m_object is not Ds3.hknpShapeMassProperties { m_compressedMassProperties: { } c })
            return null;
        var er = new hknpShapeMassProperties();
        var m = er.m_compressedMassProperties;
        m.m_centerOfMass.m_values[0] = c.m_centerOfMass_0; m.m_centerOfMass.m_values[1] = c.m_centerOfMass_1;
        m.m_centerOfMass.m_values[2] = c.m_centerOfMass_2; m.m_centerOfMass.m_values[3] = c.m_centerOfMass_3;
        m.m_inertia.m_values[0] = c.m_inertia_0; m.m_inertia.m_values[1] = c.m_inertia_1;
        m.m_inertia.m_values[2] = c.m_inertia_2; m.m_inertia.m_values[3] = c.m_inertia_3;
        m.m_majorAxisSpace[0] = c.m_majorAxisSpace_0; m.m_majorAxisSpace[1] = c.m_majorAxisSpace_1;
        m.m_majorAxisSpace[2] = c.m_majorAxisSpace_2; m.m_majorAxisSpace[3] = c.m_majorAxisSpace_3;
        m.m_mass = c.m_mass;
        m.m_volume = c.m_volume;
        return (entry.m_key, entry.m_flags, er);
    }

    private static float MassOf(Ds3.hknpShape? shape) =>
        shape?.m_properties?.m_entries?.Select(e => e.m_object).OfType<Ds3.hknpShapeMassProperties>().FirstOrDefault()?.m_compressedMassProperties?.m_mass ?? 0;

    private static hkaSkeleton Skeleton(Ds3.hkaSkeleton ds3)
    {
        var s = new hkaSkeleton { m_name = ds3.m_name };
        foreach (var b in ds3.m_bones ?? [])
            s.m_bones.Add(new hkaBone { m_name = b.m_name, m_lockTranslation = b.m_lockTranslation });
        s.m_parentIndices.AddRange(ds3.m_parentIndices ?? []);
        foreach (var t in ds3.m_referencePose ?? [])
            s.m_referencePose.Add(Qs(t));
        return s;
    }

    /// <summary>HKX2 reads an hkQsTransform as a matrix: row 1 translation, row 2 rotation (xyzw), row 3 scale.</summary>
    public static hkQsTransform Qs(Matrix4x4 m) => new()
    {
        m_translation = new Vector4(m.M11, m.M12, m.M13, m.M14),
        m_rotation = new Quaternion(m.M21, m.M22, m.M23, m.M24),
        m_scale = new Vector4(m.M31, m.M32, m.M33, m.M34),
    };

    /// <summary>A deep copy of a HKLib object graph (fields, lists and arrays; strings and value types shared).</summary>
    public static T Clone<T>(T source) where T : class => (T)Copy(source, new Dictionary<object, object>(ReferenceEqualityComparer.Instance))!;

    private static object? Copy(object? o, Dictionary<object, object> seen)
    {
        if (o is null || o is string || o.GetType().IsValueType && o.GetType().IsPrimitive || o.GetType().IsEnum)
            return o;
        if (seen.TryGetValue(o, out var done))
            return done;
        var type = o.GetType();
        if (o is Array array)
        {
            var copy = Array.CreateInstance(type.GetElementType()!, array.Length);
            seen[o] = copy;
            for (int i = 0; i < array.Length; i++)
                copy.SetValue(Copy(array.GetValue(i), seen), i);
            return copy;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (System.Collections.IList)Activator.CreateInstance(type)!;
            seen[o] = list;
            foreach (var item in (System.Collections.IList)o)
                list.Add(Copy(item, seen));
            return list;
        }
        if (type.IsValueType)
            return o; // vectors, matrices, quaternions
        var made = Activator.CreateInstance(type)!;
        seen[o] = made;
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var value = Copy(f.GetValue(o), seen);
                if (f.IsInitOnly && value is Array src && f.GetValue(made) is Array dst && dst.Length == src.Length)
                    Array.Copy(src, dst, src.Length); // readonly fixed arrays (m_motors, m_values)
                else
                    f.SetValue(made, value);
            }
        return made;
    }
}
