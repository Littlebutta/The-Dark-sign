using System.Numerics;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using HKLib.Serialization.hk2018.Xml;
using DarkSign.Porter.Havok;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Turns Dark Souls III map collision (Havok 2014 packfile) into Elden Ring collision (Havok 2018 tagfile).
///
/// Both games store map collision the same way: an hknpPhysicsSceneData whose bodies hold an
/// fsnpCustomParamCompressedMeshShape (FromSoftware's compressed mesh with per-triangle hit materials). The compressed
/// mesh tree (sections, quantized vertices, AABB trees) has the same layout in Havok 2014 and 2018; 2018 only splits
/// the packed "count | first &lt;&lt; 8" words into separate fields (as Smithbox's DS3 and Elden Ring collision readers
/// show). So the geometry is copied as-is, nothing is re-quantized, and the converted file can be decoded back to the
/// same triangles (<see cref="Decode(hkRootLevelContainer)"/>), which the porter checks for every file.
///
/// Settings that only Elden Ring has (body motion type, shape type, dispatch type, collision filter, materials) come
/// from a vanilla Elden Ring collision file when one is given (<see cref="ErCollisionTemplate"/>), else from defaults.
/// The DS3 reader is Smithbox's HKX2 (MIT); the writer is HKLib. ER_OBJ2HKX / JortPob write ER collision as XML
/// tagfiles, which the game loads, so XML is the default output.
/// </summary>
public static class CollisionPort
{
    public sealed record Result(byte[] Bytes, int Bodies, int Triangles, IReadOnlyList<string> Notes);

    /// <summary>Reads a DS3 collision .hkx (no DCX) with HKX2.</summary>
    public static Ds3.hkRootLevelContainer ReadDs3(byte[] hkx) =>
        (Ds3.hkRootLevelContainer)new Ds3.PackFileDeserializer().Deserialize(new BinaryReaderEx(false, hkx));

    /// <summary>Converts one DS3 collision file. Throws if any body's geometry doesn't survive the conversion.</summary>
    public static Result Convert(byte[] ds3Hkx, ErCollisionTemplate? template, bool xml = true, bool keepSimdTree = false)
    {
        var notes = new List<string>();
        var ds3 = ReadDs3(ds3Hkx);
        var er = Convert(ds3, template ?? ErCollisionTemplate.Default, keepSimdTree, notes);

        var before = Decode(ds3);
        var after = Decode(er);
        Compare(before, after);

        // HKLib's serializers keep type caches in unsynchronised static dictionaries (see AnimPort.HavokLock): with
        // the map's collision files converting on every core, the first build lost 15 of Cemetery of Ash's 45 files
        // to "An item with the same key has already been added" and corrupted-collection errors. Writing the XML
        // tagfile and reading it back is nearly all of a file's time (a 4.7 MB DS3 file becomes 260 MB of XML), so
        // one file at a time made a map's collision take minutes on one core. After WarmUp has filled the caches with
        // every type a converted collision file holds, serializing only reads them and runs on every core.
        byte[] bytes;
        hkRootLevelContainer reread;
        if (LockFree)
        {
            bytes = Write(er, xml);
            reread = Read(bytes);
        }
        else
            lock (AnimPort.HavokLock)
            {
                bytes = Write(er, xml);
                // Read the written file back, so a serializer problem shows up here and not in the game.
                reread = Read(bytes);
            }
        Compare(before, Decode(reread));
        return new Result(bytes, after.Count, after.Sum(b => b.Triangles.Count), notes);
    }

    /// <summary>
    /// True once <see cref="WarmUp"/> has filled HKLib's caches for collision files: serializing then needs no lock.
    /// Only valid while nothing else uses HKLib on another thread (AnimPort and the enemy port keep the lock and run
    /// before the maps). DARKSIGN_COLLISION_LOCK=1 keeps the lock for every file, as builds before 1.0.23x did.
    /// </summary>
    public static bool LockFree { get; private set; }

    private static readonly bool KeepLock = Environment.GetEnvironmentVariable("DARKSIGN_COLLISION_LOCK") == "1";

    /// <summary>
    /// Serializes, under the lock, one collision file that holds every kind of object <see cref="Convert(byte[], ErCollisionTemplate?, bool, bool)"/>
    /// can produce (a material, motion properties, a body with hit materials and a mesh tree with every array filled),
    /// so HKLib has cached every type before the real files convert in parallel. Returns a line for the report; null
    /// when the lock has to stay (the caches can't be seen, or DARKSIGN_COLLISION_LOCK=1).
    /// </summary>
    public static string? WarmUp(ErCollisionTemplate? template, bool xml, bool keepSimdTree)
    {
        LockFree = false;
        if (KeepLock || !HavokCaches.Available)
            return null;
        var ds3 = ReadDs3(MapSelfTest.SyntheticDs3Collision(everything: true));
        lock (AnimPort.HavokLock)
        {
            // Both templates: the body and material settings differ between a template from the game and the default.
            foreach (var t in new[] { template ?? ErCollisionTemplate.Default, ErCollisionTemplate.Default })
            {
                var notes = new List<string>();
                var er = Convert(ds3, t, keepSimdTree, notes);
                var written = Write(er, xml);
                Compare(Decode(ds3), Decode(Read(written)));
                // The other format too: MapValidate reads binary files from the game's binders beside ours.
                Compare(Decode(ds3), Decode(Read(Write(er, !xml))));
            }
        }
        LockFree = true;
        return $"HKLib caches warmed ({HavokCaches.Count} types); collision files serialize on every core";
    }

    public static byte[] Write(hkRootLevelContainer root, bool xml)
    {
        using var stream = new MemoryStream();
        if (xml)
            new HavokXmlSerializer().Write(root, stream);
        else
            new HavokBinarySerializer().Write(root, stream);
        return stream.ToArray();
    }

    /// <summary>Reads an Elden Ring collision file, binary or XML tagfile.</summary>
    public static hkRootLevelContainer Read(byte[] bytes, byte[]? compendium = null)
    {
        var isXml = bytes.Length > 0 && (bytes[0] == (byte)'<' || (bytes.Length > 3 && bytes[0] == 0xEF && bytes[3] == (byte)'<'));
        // Only files with a negative value parameter need the rewrite below; scanning the bytes for one is far
        // cheaper than decoding and re-encoding hundreds of MB of XML.
        if (isXml && bytes.AsSpan().IndexOf("<valueparam value=\"-"u8) >= 0)
        {
            // HKLib writes negative template value parameters (e.g. -1) but reads them back as unsigned; give the
            // reader the same 32 bits as an unsigned number.
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            text = System.Text.RegularExpressions.Regex.Replace(text, "<valueparam value=\"(-\\d+)\"",
                m => $"<valueparam value=\"{unchecked((uint)int.Parse(m.Groups[1].Value))}\"");
            bytes = System.Text.Encoding.UTF8.GetBytes(text);
        }
        using var stream = new MemoryStream(bytes);
        if (isXml)
            return (hkRootLevelContainer)new HavokXmlSerializer().Read(stream);
        // Elden Ring's collision binders keep the Havok type information in one .compendium file per binder.
        var binary = new HavokBinarySerializer();
        if (compendium is not null)
            binary.LoadCompendium(new MemoryStream(compendium));
        return (hkRootLevelContainer)binary.Read(stream);
    }

    public static hkRootLevelContainer Convert(Ds3.hkRootLevelContainer ds3, ErCollisionTemplate template, bool keepSimdTree, List<string> notes)
    {
        var root = new hkRootLevelContainer();
        foreach (var variant in ds3.m_namedVariants ?? [])
        {
            if (variant.m_variant is not Ds3.hknpPhysicsSceneData scene)
            {
                notes.Add($"skipped root object {variant.m_className}");
                continue;
            }
            var erScene = new hknpPhysicsSceneData();
            foreach (var system in scene.m_systemDatas ?? [])
                erScene.m_systemDatas.Add(ConvertSystem(system, template, keepSimdTree, notes));
            root.m_namedVariants.Add(new hkRootLevelContainer.NamedVariant
            {
                m_name = variant.m_name ?? "Physics Scene Data",
                m_className = "hknpPhysicsSceneData",
                m_variant = erScene,
            });
        }
        if (root.m_namedVariants.Count == 0)
            throw new InvalidDataException("no hknpPhysicsSceneData in the DS3 collision file");
        return root;
    }

    private static hknpPhysicsSystemData ConvertSystem(Ds3.hknpPhysicsSystemData ds3, ErCollisionTemplate template,
        bool keepSimdTree, List<string> notes)
    {
        var system = new hknpPhysicsSystemData
        {
            m_name = ds3.m_name ?? template.SystemName,
            m_microStepMultiplier = template.MicroStepMultiplier,
        };
        foreach (var material in ds3.m_materials ?? [])
            system.m_materials.Add(ConvertMaterial(material));
        if (system.m_materials.Count == 0)
            system.m_materials.AddRange(template.Materials());
        foreach (var motion in ds3.m_motionProperties ?? [])
            system.m_motionProperties.Add(ConvertMotionProperties(motion));

        foreach (var body in ds3.m_bodyCinfos ?? [])
        {
            var shape = ConvertShape(body.m_shape, template, keepSimdTree, notes);
            if (shape is null)
            {
                notes.Add($"body {body.m_name}: unsupported shape {body.m_shape?.GetType().Name ?? "null"}, skipped");
                continue;
            }
            var erBody = template.NewBody();
            erBody.m_shape = shape;
            erBody.m_name = body.m_name;
            erBody.m_userData = body.m_userData;
            erBody.m_position = body.m_position;
            erBody.m_orientation = body.m_orientation;
            erBody.m_materialId = body.m_materialId;
            erBody.m_collisionLookAheadDistance = body.m_collisionLookAheadDistance;
            if (!template.FromGame)
            {
                erBody.m_flags = body.m_flags;
                erBody.m_collisionFilterInfo = body.m_collisionFilterInfo;
                erBody.m_qualityId = body.m_qualityId;
            }
            system.m_bodyCinfos.Add(erBody);
        }
        return system;
    }

    // Havok stores these as hkHalf16: the top 16 bits of a float.
    private static float Half(short value) => BitConverter.Int32BitsToSingle(value << 16);

    private static hknpMaterial ConvertMaterial(Ds3.hknpMaterial m) => new()
    {
        m_name = m.m_name,
        m_isExclusive = m.m_isExclusive,
        m_flags = m.m_flags,
        m_triggerType = (hknpMaterial.TriggerType)(int)m.m_triggerType,
        m_triggerManifoldTolerance = new hkUFloat8 { m_value = m.m_triggerManifoldTolerance?.m_value ?? 255 },
        m_dynamicFriction = Half(m.m_dynamicFriction),
        m_staticFriction = Half(m.m_staticFriction),
        m_restitution = Half(m.m_restitution),
        m_frictionCombinePolicy = (hknpMaterial.CombinePolicy)(int)m.m_frictionCombinePolicy,
        m_restitutionCombinePolicy = (hknpMaterial.CombinePolicy)(int)m.m_restitutionCombinePolicy,
        m_weldingTolerance = Half(m.m_weldingTolerance),
        m_maxContactImpulse = m.m_maxContactImpulse,
        m_fractionOfClippedImpulseToApply = m.m_fractionOfClippedImpulseToApply,
        m_massChangerCategory = (hknpMaterial.MassChangerCategory)(int)m.m_massChangerCategory,
        m_massChangerHeavyObjectFactor = Half(m.m_massChangerHeavyObjectFactor),
        m_softContactForceFactor = Half(m.m_softContactForceFactor),
        m_softContactDampFactor = Half(m.m_softContactDampFactor),
        m_softContactSeparationVelocity = new hkUFloat8 { m_value = m.m_softContactSeperationVelocity?.m_value ?? 0 },
        m_disablingCollisionsBetweenCvxCvxDynamicObjectsDistance = Half(m.m_disablingCollisionsBetweenCvxCvxDynamicObjectsDistance),
        m_userData = m.m_userData,
    };

    private static hknpMotionProperties ConvertMotionProperties(Ds3.hknpMotionProperties m) => new()
    {
        m_isExclusive = m.m_isExclusive,
        m_flags = m.m_flags,
        m_gravityFactor = m.m_gravityFactor,
        m_timeFactor = m.m_timeFactor,
        m_maxLinearSpeed = m.m_maxLinearSpeed,
        m_maxAngularSpeed = m.m_maxAngularSpeed,
        m_linearDamping = m.m_linearDamping,
        m_angularDamping = m.m_angularDamping,
        m_solverStabilizationSpeedThreshold = m.m_solverStabilizationSpeedThreshold,
        m_solverStabilizationSpeedReduction = m.m_solverStabilizationSpeedReduction,
        m_deactivationSettings = new hknpMotionProperties.DeactivationSettings
        {
            m_maxDistSqrd = m.m_maxDistSqrd,
            m_maxRotSqrd = m.m_maxRotSqrd,
            m_invBlockSize = m.m_invBlockSize,
            m_pathingUpperThreshold = m.m_pathingUpperThreshold,
            m_pathingLowerThreshold = m.m_pathingLowerThreshold,
            m_numDeactivationFrequencyPasses = m.m_numDeactivationFrequencyPasses,
            m_deactivationVelocityScaleSquare = m.m_deactivationVelocityScaleSquare,
            m_minimumPathingVelocityScaleSquare = m.m_minimumPathingVelocityScaleSquare,
            m_spikingVelocityScaleThresholdSquared = m.m_spikingVelocityScaleThresholdSquared,
            m_minimumSpikingVelocityScaleSquared = m.m_minimumSpikingVelocityScaleSquared,
        },
    };

    private static hknpShape? ConvertShape(Ds3.hknpShape? shape, ErCollisionTemplate template, bool keepSimdTree, List<string> notes)
    {
        if (shape is not Ds3.hknpCompressedMeshShape mesh || mesh.m_data is null)
            return null;

        var er = new fsnpCustomParamCompressedMeshShape
        {
            m_flags = template.FromGame ? template.ShapeFlags : (hknpShape.FlagsEnum)mesh.m_flags,
            m_type = hknpShapeType.Enum.COMPRESSED_MESH,
            m_numShapeKeyBits = mesh.m_numShapeKeyBits,
            m_dispatchType = template.DispatchType,
            m_convexRadius = mesh.m_convexRadius,
            m_userData = mesh.m_userData,
            m_shapeTagCodecInfo = mesh.m_shapeTagCodecInfo,
            m_data = ConvertData(mesh.m_data, keepSimdTree),
            m_triangleIsInterior = ConvertBitField(mesh.m_triangleIsInterior),
        };
        if (mesh is Ds3.fsnpCustomParamCompressedMeshShape fsnp)
        {
            er.m_pParam = ConvertParam(fsnp.m_pParam);
            if (fsnp.m_triangleIndexToShapeKey is not null)
                er.m_triangleIndexToShapeKey.AddRange(fsnp.m_triangleIndexToShapeKey);
        }
        else
        {
            notes.Add("plain hknpCompressedMeshShape (no hit materials) converted as fsnpCustomParamCompressedMeshShape");
        }
        return er;
    }

    private static hkBitField ConvertBitField(Ds3.hkBitField? bits)
    {
        var storage = new hkBitFieldStorage<List<uint>> { m_words = [], m_numBits = 0 };
        if (bits?.m_storage is { } s)
        {
            storage.m_words.AddRange(s.m_words ?? []);
            storage.m_numBits = s.m_numBits;
        }
        return new hkBitField { m_storage = storage };
    }

    private static fsnpCustomMeshParameter? ConvertParam(Ds3.fsnpCustomMeshParameter? p)
    {
        if (p is null)
            return null;
        var er = new fsnpCustomMeshParameter
        {
            m_vertexDataStride = p.m_vertexDataStride,
            m_triangleDataStride = p.m_triangleDataStride,
            m_version = p.m_version,
        };
        foreach (var t in p.m_triangleDataArray ?? [])
            er.m_triangleDataArray.Add(new fsnpCustomMeshParameter.TriangleData
            {
                m_primitiveDataIndex = t.m_primitiveDataIndex,
                m_triangleDataIndex = t.m_triangleDataIndex,
                m_vertexIndexA = t.m_vertexIndexA,
                m_vertexIndexB = t.m_vertexIndexB,
                m_vertexIndexC = t.m_vertexIndexC,
            });
        foreach (var d in p.m_primitiveDataArray ?? [])
        {
            var data = new fsnpCustomMeshParameter.PrimitiveData { m_materialNameData = d.m_materialNameData };
            data.m_vertexData.AddRange(d.m_vertexData ?? []);
            data.m_triangleData.AddRange(d.m_triangleData ?? []);
            data.m_primitiveData.AddRange(d.m_primitiveData ?? []);
            er.m_primitiveDataArray.Add(data);
        }
        return er;
    }

    private static hknpCompressedMeshShapeData ConvertData(Ds3.hknpCompressedMeshShapeData ds3, bool keepSimdTree)
    {
        var src = ds3.m_meshTree;
        var tree = new hknpCompressedMeshShapeTree
        {
            m_numPrimitiveKeys = src.m_numPrimitiveKeys,
            m_bitsPerKey = src.m_bitsPerKey,
            m_maxKeyValue = src.m_maxKeyValue,
            m_domain = ConvertAabb(src.m_domain),
        };
        foreach (var node in src.m_nodes ?? [])
        {
            var n = new HKLib.hk2018.hkcdCompressedAabbCodecs.Aabb5BytesCodec { m_hiData = node.m_hiData, m_loData = node.m_loData };
            n.m_xyz[0] = node.m_xyz_0;
            n.m_xyz[1] = node.m_xyz_1;
            n.m_xyz[2] = node.m_xyz_2;
            tree.m_nodes.Add(n);
        }
        foreach (var s in src.m_sections ?? [])
        {
            var section = new HKLib.hk2018.hkcdStaticMeshTree.Section
            {
                m_domain = ConvertAabb(s.m_domain),
                m_firstPackedVertexIndex = s.m_firstPackedVertex,
                // 2014 packs "count | first << 8" into one word; 2018 has separate fields. The low byte of the
                // shared-vertex word is the index at which a section's indices switch from packed to shared vertices.
                m_numPackedVertices = (byte)(s.m_sharedVertices.m_data & 0xFF),
                m_firstSharedVertexIndex = s.m_sharedVertices.m_data >> 8,
                m_numPrimitives = (byte)(s.m_primitives.m_data & 0xFF),
                m_firstPrimitiveIndex = s.m_primitives.m_data >> 8,
                m_numDataRuns = (byte)(s.m_dataRuns.m_data & 0xFF),
                m_firstDataRunIndex = s.m_dataRuns.m_data >> 8,
                m_page = s.m_page,
                m_leafIndex = s.m_leafIndex,
                m_layerData = s.m_layerData,
                m_flags = s.m_flags,
            };
            section.m_codecParms[0] = s.m_codecParms_0;
            section.m_codecParms[1] = s.m_codecParms_1;
            section.m_codecParms[2] = s.m_codecParms_2;
            section.m_codecParms[3] = s.m_codecParms_3;
            section.m_codecParms[4] = s.m_codecParms_4;
            section.m_codecParms[5] = s.m_codecParms_5;
            foreach (var node in s.m_nodes ?? [])
            {
                var n = new HKLib.hk2018.hkcdCompressedAabbCodecs.Aabb4BytesCodec { m_data = node.m_data };
                n.m_xyz[0] = node.m_xyz_0;
                n.m_xyz[1] = node.m_xyz_1;
                n.m_xyz[2] = node.m_xyz_2;
                section.m_nodes.Add(n);
            }
            tree.m_sections.Add(section);
        }
        foreach (var p in src.m_primitives ?? [])
        {
            var primitive = new HKLib.hk2018.hkcdStaticMeshTree.Primitive();
            primitive.m_indices[0] = p.m_indices_0;
            primitive.m_indices[1] = p.m_indices_1;
            primitive.m_indices[2] = p.m_indices_2;
            primitive.m_indices[3] = p.m_indices_3;
            tree.m_primitives.Add(primitive);
        }
        tree.m_sharedVerticesIndex.AddRange(src.m_sharedVerticesIndex ?? []);
        tree.m_packedVertices.AddRange(src.m_packedVertices ?? []);
        tree.m_sharedVertices.AddRange(src.m_sharedVertices ?? []);
        foreach (var run in src.m_primitiveDataRuns ?? [])
            tree.m_primitiveDataRuns.Add(new hkcdDefaultStaticMeshTree.PrimitiveDataRun
            {
                m_value = run.m_value?.m_data ?? 0xFFFF,
                m_index = run.m_index,
                m_count = run.m_count,
            });

        var data = new hknpCompressedMeshShapeData { m_meshTree = tree };
        // Havok answers queries from the mesh tree above when there is no SIMD tree. Whether DS3's 2014 SIMD
        // node layout means the same in 2018 is unconfirmed, so it is only copied on request (--simd keep).
        if (keepSimdTree && ds3.m_simdTree?.m_nodes is { Count: > 0 } simd)
        {
            foreach (var node in simd)
            {
                var n = new hkcdSimdTree.Node
                {
                    m_lx = node.m_lx, m_hx = node.m_hx, m_ly = node.m_ly, m_hy = node.m_hy, m_lz = node.m_lz, m_hz = node.m_hz,
                };
                n.m_data[0] = node.m_data_0;
                n.m_data[1] = node.m_data_1;
                n.m_data[2] = node.m_data_2;
                n.m_data[3] = node.m_data_3;
                data.m_simdTree.m_nodes.Add(n);
            }
            data.m_hasSimdTree = true;
        }
        return data;
    }

    private static hkAabb ConvertAabb(Ds3.hkAabb? aabb) =>
        aabb is null ? new hkAabb() : new hkAabb { m_min = aabb.m_min, m_max = aabb.m_max };

    // ---- Decoding, for checks and surveys ----

    public sealed record BodyTriangles(string? Name, List<(Vector3 A, Vector3 B, Vector3 C)> Triangles);

    public static List<BodyTriangles> Decode(Ds3.hkRootLevelContainer root)
    {
        var result = new List<BodyTriangles>();
        foreach (var variant in root.m_namedVariants ?? [])
            if (variant.m_variant is Ds3.hknpPhysicsSceneData scene)
                foreach (var system in scene.m_systemDatas ?? [])
                    foreach (var body in system.m_bodyCinfos ?? [])
                        if (body.m_shape is Ds3.hknpCompressedMeshShape { m_data: { } data })
                            result.Add(new BodyTriangles(body.m_name, DecodeTree(Mirror(data.m_meshTree))));
        return result;
    }

    public static List<BodyTriangles> Decode(hkRootLevelContainer root)
    {
        var result = new List<BodyTriangles>();
        foreach (var variant in root.m_namedVariants)
            if (variant.m_variant is hknpPhysicsSceneData scene)
                foreach (var system in scene.m_systemDatas)
                    foreach (var body in system?.m_bodyCinfos ?? [])
                        if (body.m_shape is hknpCompressedMeshShape { m_data: hknpCompressedMeshShapeData data })
                            result.Add(new BodyTriangles(body.m_name, DecodeTree(data.m_meshTree)));
        return result;
    }

    // A 2018 tree with the 2014 tree's data, so both games decode through one routine.
    private static hknpCompressedMeshShapeTree Mirror(Ds3.hknpCompressedMeshShapeTree src) =>
        ConvertData(new Ds3.hknpCompressedMeshShapeData { m_meshTree = src }, keepSimdTree: false).m_meshTree;

    /// <summary>Every triangle of a compressed mesh tree (quads count as two), in the mesh's local space.</summary>
    public static List<(Vector3 A, Vector3 B, Vector3 C)> DecodeTree(hknpCompressedMeshShapeTree tree)
    {
        var triangles = new List<(Vector3, Vector3, Vector3)>();
        if (tree.m_packedVertices.Count == 0 && tree.m_sharedVertices.Count == 0)
            return triangles;
        foreach (var section in tree.m_sections)
        {
            var offset = new Vector3(section.m_codecParms[0], section.m_codecParms[1], section.m_codecParms[2]);
            var scale = new Vector3(section.m_codecParms[3], section.m_codecParms[4], section.m_codecParms[5]);
            Vector3 Vertex(byte index)
            {
                if (index < section.m_numPackedVertices)
                    return UnpackPacked(tree.m_packedVertices[(int)(index + section.m_firstPackedVertexIndex)], scale, offset);
                var shared = tree.m_sharedVerticesIndex[(int)(index + section.m_firstSharedVertexIndex - section.m_numPackedVertices)];
                return UnpackShared(tree.m_sharedVertices[shared], tree.m_domain);
            }
            for (var i = 0; i < section.m_numPrimitives; i++)
            {
                var p = tree.m_primitives[(int)(section.m_firstPrimitiveIndex + i)].m_indices;
                // Havok pads a section's primitive list with 0xDE 0xAD 0xDE 0xAD ("dead") primitives, which point at
                // no vertex of the section. The game skips them; decoding them read past the section's vertices in
                // one or two files per DS3 map (h003000 in the Cemetery of Ash), which were then left out.
                if (p[0] == 0xDE && p[1] == 0xAD && p[2] == 0xDE && p[3] == 0xAD)
                    continue;
                triangles.Add((Vertex(p[0]), Vertex(p[1]), Vertex(p[2])));
                if (p[2] != p[3])
                    triangles.Add((Vertex(p[0]), Vertex(p[2]), Vertex(p[3])));
            }
        }
        return triangles;
    }

    private static Vector3 UnpackShared(ulong v, hkAabb domain)
    {
        var min = domain.m_min;
        var max = domain.m_max;
        return new Vector3(
            (float)((v & 0x1FFFFF) * ((max.X - min.X) / 2097151.0) + min.X),
            (float)(((v >> 21) & 0x1FFFFF) * ((max.Y - min.Y) / 2097151.0) + min.Y),
            (float)(((v >> 42) & 0x3FFFFF) * ((max.Z - min.Z) / 4194303.0) + min.Z));
    }

    private static Vector3 UnpackPacked(uint v, Vector3 scale, Vector3 offset) => new(
        (v & 0x7FF) * scale.X + offset.X,
        ((v >> 11) & 0x7FF) * scale.Y + offset.Y,
        ((v >> 22) & 0x3FF) * scale.Z + offset.Z);

    private static void Compare(List<BodyTriangles> before, List<BodyTriangles> after)
    {
        if (before.Count != after.Count)
            throw new InvalidDataException($"collision conversion kept {after.Count} of {before.Count} mesh bodies");
        for (var b = 0; b < before.Count; b++)
        {
            var x = before[b].Triangles;
            var y = after[b].Triangles;
            if (x.Count != y.Count)
                throw new InvalidDataException($"body {before[b].Name}: {x.Count} triangles became {y.Count}");
            for (var i = 0; i < x.Count; i++)
                if (Vector3.DistanceSquared(x[i].A, y[i].A) > 1e-6f || Vector3.DistanceSquared(x[i].B, y[i].B) > 1e-6f
                    || Vector3.DistanceSquared(x[i].C, y[i].C) > 1e-6f)
                    throw new InvalidDataException($"body {before[b].Name}: triangle {i} moved in conversion");
        }
    }
}

/// <summary>
/// Elden Ring-only settings for converted collision bodies, read from a vanilla Elden Ring collision file of the host
/// map so the converted bodies look like the game's own.
/// </summary>
public sealed class ErCollisionTemplate
{
    public bool FromGame { get; private init; }
    public string SystemName { get; private init; } = "Default Physics System Data";
    public byte MicroStepMultiplier { get; private init; }
    public hknpShape.FlagsEnum ShapeFlags { get; private init; } =
        hknpShape.FlagsEnum.IS_COMPOSITE_SHAPE | hknpShape.FlagsEnum.IS_TRIANGLE_OR_QUAD_SHAPE;
    public hknpCollisionDispatchType.Enum DispatchType { get; private init; } = hknpCollisionDispatchType.Enum.COMPOSITE;
    private hknpPhysicsSystemData.bodyCinfoWithAttachment? _body;
    private List<hknpMaterial> _materials = [];

    public static ErCollisionTemplate Default { get; } = new();

    /// <summary>Settings of the first compressed-mesh body in a vanilla Elden Ring collision file.</summary>
    public static ErCollisionTemplate? FromFile(byte[] erHkx, byte[]? compendium = null)
    {
        var root = CollisionPort.Read(erHkx, compendium);
        foreach (var variant in root.m_namedVariants)
            if (variant.m_variant is hknpPhysicsSceneData scene)
                foreach (var system in scene.m_systemDatas)
                    foreach (var body in system?.m_bodyCinfos ?? [])
                        if (body.m_shape is hknpCompressedMeshShape shape)
                            return new ErCollisionTemplate
                            {
                                FromGame = true,
                                SystemName = system!.m_name ?? "Default Physics System Data",
                                MicroStepMultiplier = system.m_microStepMultiplier,
                                ShapeFlags = shape.m_flags,
                                DispatchType = shape.m_dispatchType,
                                _body = body,
                                _materials = system.m_materials,
                            };
        return null;
    }

    public IEnumerable<hknpMaterial> Materials() => _materials;

    public hknpPhysicsSystemData.bodyCinfoWithAttachment NewBody()
    {
        var t = _body;
        return new hknpPhysicsSystemData.bodyCinfoWithAttachment
        {
            m_attachedBody = t?.m_attachedBody ?? -1,
            m_flags = t?.m_flags ?? 1,
            m_collisionCntrl = t?.m_collisionCntrl ?? 0,
            m_collisionFilterInfo = t?.m_collisionFilterInfo ?? 0,
            m_qualityId = t?.m_qualityId ?? 255,
            m_motionType = t?.m_motionType ?? 0,
            m_mass = t?.m_mass ?? 0,
            m_motionPropertiesId = t?.m_motionPropertiesId ?? 0xFFFF,
            m_desiredBodyId = new hknpBodyId { m_serialAndIndex = t?.m_desiredBodyId?.m_serialAndIndex ?? 0x7FFFFFFF },
            m_motionId = t?.m_motionId ?? 0x7FFFFFFF,
            m_activationPriority = t?.m_activationPriority ?? 0,
        };
    }
}
