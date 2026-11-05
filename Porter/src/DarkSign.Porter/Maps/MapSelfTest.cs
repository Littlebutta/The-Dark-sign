using System.Numerics;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>
/// `map-selftest`: offline checks of the map porter that need no game files, so they run in CI and in the cloud.
/// Each check builds a small Dark Souls III-style file in memory, ports it and checks the result.
/// </summary>
public static class MapSelfTest
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"ok    {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("DKSIGN_SELFTEST_TRACE") == "1")
                    Console.WriteLine(ex);
            }
        }

        Check("collision: DS3 packfile -> Elden Ring XML tagfile keeps every triangle", () => CollisionRoundTrip(xml: true));
        Check("collision: DS3 packfile -> Elden Ring binary tagfile keeps every triangle", () => CollisionRoundTrip(xml: false));
        Check("collision: template settings from an Elden Ring file are applied", CollisionTemplate);
        foreach (var (name, test) in MapPortChecks.All().Concat(MapPortChecks.EventChecks()).Concat(MapPortChecks.EnemyChecks()))
            Check(name, test);

        Console.WriteLine(failures == 0 ? "map-selftest: all checks passed" : $"map-selftest: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private static void CollisionRoundTrip(bool xml)
    {
        var ds3 = SyntheticDs3Collision();
        var result = CollisionPort.Convert(ds3, null, xml);
        if (result.Bodies != 1 || result.Triangles != 3)
            throw new Exception($"expected 1 body with 3 triangles, got {result.Bodies} with {result.Triangles}");
        var er = CollisionPort.Read(result.Bytes);
        var tri = CollisionPort.Decode(er)[0].Triangles;
        // The quad (0,1,2,3) must come back as two triangles in the right places.
        if (Vector3.Distance(tri[0].A, new Vector3(0, 0, 0)) > 0.01f || Vector3.Distance(tri[2].B, new Vector3(10, 0, 10)) > 0.01f)
            throw new Exception($"vertices moved: {tri[0].A} {tri[2].B}");
        var body = ((HKLib.hk2018.hknpPhysicsSceneData)er.m_namedVariants[0].m_variant!).m_systemDatas[0]!.m_bodyCinfos[0];
        var shape = (HKLib.hk2018.fsnpCustomParamCompressedMeshShape)body.m_shape!;
        if (shape.m_pParam?.m_primitiveDataArray.Count != 1 || shape.m_pParam.m_primitiveDataArray[0].m_materialNameData != 7)
            throw new Exception("hit material lost");
        if (body.m_name != "h_test" || Vector3.Distance(new Vector3(body.m_position.X, body.m_position.Y, body.m_position.Z), new Vector3(5, 1, 2)) > 1e-4f)
            throw new Exception("body name or position lost");
    }

    private static void CollisionTemplate()
    {
        // An "Elden Ring file" made by the converter itself, with settings changed the way a game file would have them.
        var er = CollisionPort.Read(CollisionPort.Convert(SyntheticDs3Collision(), null, xml: false).Bytes);
        var body = ((HKLib.hk2018.hknpPhysicsSceneData)er.m_namedVariants[0].m_variant!).m_systemDatas[0]!.m_bodyCinfos[0];
        body.m_collisionFilterInfo = 0x1234;
        body.m_motionPropertiesId = 3;
        var template = ErCollisionTemplate.FromFile(CollisionPort.Write(er, xml: false))
                       ?? throw new Exception("template not found");
        var ported = CollisionPort.Read(CollisionPort.Convert(SyntheticDs3Collision(), template).Bytes);
        var portedBody = ((HKLib.hk2018.hknpPhysicsSceneData)ported.m_namedVariants[0].m_variant!).m_systemDatas[0]!.m_bodyCinfos[0];
        if (portedBody.m_collisionFilterInfo != 0x1234 || portedBody.m_motionPropertiesId != 3)
            throw new Exception("template body settings were not applied");
    }

    /// <summary>
    /// A DS3-style collision file: one body, one section with a triangle and a quad, three packed vertices and two
    /// shared ones, written as a Havok 2014 packfile by HKX2 the way DS3's own files are laid out.
    /// </summary>
    public static byte[] SyntheticDs3Collision()
    {
        var min = new Vector3(0, 0, 0);
        var max = new Vector3(10, 4, 10);
        var verts = new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 4, 0), new Vector3(0, 0, 10), new Vector3(10, 0, 10) };

        // Section codec: offset = min, scale = extent / 2047 (x, y) and / 1023 (z).
        var scale = (max - min) / new Vector3(2047, 2047, 1023);
        uint Pack(Vector3 v)
        {
            var q = (v - min) / scale;
            return ((uint)MathF.Round(q.X) & 0x7FF) | (((uint)MathF.Round(q.Y) & 0x7FF) << 11) | (((uint)MathF.Round(q.Z) & 0x3FF) << 22);
        }
        ulong Shared(Vector3 v)
        {
            var q = (v - min) / (max - min) * new Vector3(2097151, 2097151, 4194303);
            return ((ulong)MathF.Round(q.X) & 0x1FFFFF) | (((ulong)MathF.Round(q.Y) & 0x1FFFFF) << 21) | (((ulong)MathF.Round(q.Z) & 0x3FFFFF) << 42);
        }

        var aabb = new Ds3.hkAabb { m_min = new Vector4(min, 1), m_max = new Vector4(max, 1) };
        var section = new Ds3.hkcdStaticMeshTreeBaseSection
        {
            m_codecParms_0 = min.X, m_codecParms_1 = min.Y, m_codecParms_2 = min.Z,
            m_codecParms_3 = scale.X, m_codecParms_4 = scale.Y, m_codecParms_5 = scale.Z,
            m_firstPackedVertex = 0,
            // Indices 0-2 are packed vertices, 3-4 the section's first two shared ones.
            m_sharedVertices = new Ds3.hkcdStaticMeshTreeBaseSectionSharedVertices { m_data = 3u | (0u << 8) },
            m_primitives = new Ds3.hkcdStaticMeshTreeBaseSectionPrimitives { m_data = 2u | (0u << 8) },
            m_dataRuns = new Ds3.hkcdStaticMeshTreeBaseSectionDataRuns { m_data = 1u | (0u << 8) },
            m_numPackedVertices = 3,
            m_numSharedIndices = 2,
            m_domain = aabb,
            m_nodes = [new Ds3.hkcdStaticTreeCodec3Axis4 { m_xyz_0 = 0, m_xyz_1 = 0, m_xyz_2 = 0, m_data = 0 }],
        };
        var tree = new Ds3.hknpCompressedMeshShapeTree
        {
            m_domain = aabb,
            m_nodes = [new Ds3.hkcdStaticTreeCodec3Axis5 { m_xyz_0 = 0, m_xyz_1 = 0, m_xyz_2 = 0, m_hiData = 0, m_loData = 0 }],
            m_numPrimitiveKeys = 2,
            m_bitsPerKey = 9,
            m_maxKeyValue = 0x1FF,
            m_sections = [section],
            m_primitives =
            [
                new Ds3.hkcdStaticMeshTreeBasePrimitive { m_indices_0 = 0, m_indices_1 = 1, m_indices_2 = 2, m_indices_3 = 2 },
                new Ds3.hkcdStaticMeshTreeBasePrimitive { m_indices_0 = 0, m_indices_1 = 1, m_indices_2 = 4, m_indices_3 = 3 },
            ],
            m_sharedVerticesIndex = [0, 1],
            m_packedVertices = [Pack(verts[0]), Pack(verts[1]), Pack(verts[2])],
            m_sharedVertices = [Shared(verts[3]), Shared(verts[4])],
            m_primitiveDataRuns =
            [
                new Ds3.hknpCompressedMeshShapeTreeDataRun
                {
                    m_value = new Ds3.hknpCompressedMeshShapeTreeDataRunData { m_data = 0 }, m_index = 0, m_count = 2,
                },
            ],
        };
        var shape = new Ds3.fsnpCustomParamCompressedMeshShape
        {
            m_flags = 516,
            m_numShapeKeyBits = 9,
            m_dispatchType = Ds3.Enum.COMPOSITE,
            m_convexRadius = 0.01f,
            m_userData = 0,
            m_properties = null,
            m_edgeWeldingMap = new Ds3.hknpSparseCompactMapunsignedshort { m_primaryKeyToIndex = [], m_valueAndSecondaryKeys = [] },
            m_shapeTagCodecInfo = 0xFFFFFFFF,
            m_data = new Ds3.hknpCompressedMeshShapeData { m_meshTree = tree, m_simdTree = new Ds3.hkcdSimdTree { m_nodes = [] } },
            m_quadIsFlat = EmptyBits(),
            m_triangleIsInterior = EmptyBits(),
            m_pParam = new Ds3.fsnpCustomMeshParameter
            {
                m_triangleDataArray = [],
                m_primitiveDataArray =
                [
                    new Ds3.fsnpCustomMeshParameterPrimitiveData
                    {
                        m_vertexData = [], m_triangleData = [], m_primitiveData = [], m_materialNameData = 7,
                    },
                ],
                m_vertexDataStride = 0,
                m_triangleDataStride = 0,
                m_version = 2,
            },
            m_triangleIndexToShapeKey = [],
        };
        var body = new Ds3.hknpBodyCinfo
        {
            m_shape = shape,
            m_reservedBodyId = 0x7FFFFFFF,
            m_motionId = 0x7FFFFFFF,
            m_qualityId = 255,
            m_materialId = 0,
            m_collisionFilterInfo = 0,
            m_flags = 1,
            m_name = "h_test",
            m_position = new Vector4(5, 1, 2, 1),
            m_orientation = Quaternion.Identity,
        };
        var system = new Ds3.hknpPhysicsSystemData
        {
            m_materials = [],
            m_motionProperties = [],
            m_motionCinfos = [],
            m_bodyCinfos = [body],
            m_constraintCinfos = [],
            m_referencedObjects = [],
            m_name = "Default Physics System Data",
        };
        var root = new Ds3.hkRootLevelContainer
        {
            m_namedVariants =
            [
                new Ds3.hkRootLevelContainerNamedVariant
                {
                    m_name = "Physics Scene Data",
                    m_className = "hknpPhysicsSceneData",
                    m_variant = new Ds3.hknpPhysicsSceneData { m_systemDatas = [system], m_worldCinfo = null },
                },
            ],
        };
        using var stream = new MemoryStream();
        var bw = new BinaryWriterEx(false, stream);
        new Ds3.PackFileSerializer().Serialize(root, bw);
        bw.Finish();
        return stream.ToArray();
    }

    private static Ds3.hkBitField EmptyBits() => new()
    {
        m_storage = new Ds3.hkBitFieldStoragehkArrayunsignedinthkContainerHeapAllocator { m_words = [], m_numBits = 0 },
    };
}
