using System.Numerics;
using HKLib.hk2018;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

// Offline checks for the objects that move (doors, lifts, levers): models on their nodes, convex collision, moving
// bodies, ObjActParam rows, ObjAct events and their flags.
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> DoorChecks()
    {
        yield return ("doors: a moving object keeps its meshes on their nodes (each part follows its bone) and names a node after the asset", MovingModel);
        yield return ("doors: a DS3 convex hull (door leaf, lever) becomes an Elden Ring convex polytope with one plane per face", ConvexHull);
        yield return ("doors: a written convex hull lists its vertices, planes, faces and indices in the patch list (the game resolves only those; 1.0.410 crashed without)", ConvexHullPatches);
        yield return ("lifts: solid blocks inside a DS3 mesh (a lift's platform, a gate's frame) are read and rebuilt as faces, not left out as a placeholder", MeshHulls);
        yield return ("lifts: a hull's faces point outwards, flat points give none, and a large mesh is split into sections whose boxes hold their faces", HullFacesAndSections);
        yield return ("doors: bodies named after a moving bone get the door leaf's motion settings; the frame stays put", MovingBodies);
        yield return ("doors: DS3 ObjActParam rows are copied at 70,000,000 + the DS3 ID, keys dropped, prompts Elden Ring lacks replaced", ObjActRows);
        yield return ("doors: DS3 ObjAct events are placed for moving objects only, and get their flags when the scripts are converted", ObjActEvents);
    }

    private static void MovingModel()
    {
        var (port, _) = VisualPort();
        var source = SyntheticObject();
        source.Nodes[0].Name = "o302590";
        var before = source.Meshes[0].Vertices.Select(v => v.Position).ToList();
        var objbnd = new BND4();
        objbnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 200, @"N:\FDP\data\INTERROOT_win64\obj\o302590\o302590.flver", source.Write()));
        var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["o302590"] = "AEG920_007" };
        var result = ObjectPort.ConvertBinder(objbnd, "o302590", "AEG920_007", port, new BND4 { Version = "07D7R6" }, Binder.FileFlags.Flag1, rename, out var note);
        Expect(note is null, $"every vertex kept its node ({note})");
        var flver = FLVER2.Read(BND4.Read(result.Binder.Write()).Files.Single().Bytes);
        var mesh = flver.Meshes.Single();
        Expect(mesh.Vertices.Select(v => v.Position).Zip(before).All(p => Vector3.Distance(p.First, p.Second) < 1e-3f), "vertices stay in their node's space (not baked into the model)");
        Expect(mesh.Vertices.All(v => v.NormalWs[0] == 1) && mesh.NodeIndex == 0 && mesh.BoneIndices.Count == 0 && !mesh.UseBoneWeights,
            "each vertex names its node in NormalW on a plain mesh, like Elden Ring's door AEG219_000");
        Expect(flver.Nodes[0].Name == "AEG920_007" && flver.Nodes[1].Name == "arm" && flver.Nodes.Count == 2, "the DS3 root takes the asset name, the other nodes keep theirs");
        Expect(mesh.BoundingBox.Unk.Y > 4f, $"the culling box is where the part rests (centre y {mesh.BoundingBox.Unk.Y:0.0})");

        // A model whose root isn't named after the object gets a node of the asset's name, added last.
        var other = SyntheticObject();
        var objbnd2 = new BND4();
        objbnd2.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 200, @"N:\x\o302590.flver", other.Write()));
        var (port2, _) = VisualPort();
        var r2 = ObjectPort.ConvertBinder(objbnd2, "o302590", "AEG920_007", port2, new BND4 { Version = "07D7R6" }, Binder.FileFlags.Flag1, rename, out _);
        var f2 = FLVER2.Read(BND4.Read(r2.Binder.Write()).Files.Single().Bytes);
        Expect(f2.Nodes.Count == 3 && f2.Nodes[0].Name == "root" && f2.Nodes[2].Name == "AEG920_007" && f2.Meshes[0].Vertices.All(v => v.NormalWs[0] == 1),
            "a node named after the asset is added after the others, so no node number changes");

        Expect(ObjectPort.ParseMoving("off") == ObjectPort.MovingMethod.Off && ObjectPort.ParseMoving("b") == ObjectPort.MovingMethod.NoCollision
               && MapPorter.Options.Parse(["--moving-objects", "nocollision"]).MovingObjects == ObjectPort.MovingMethod.NoCollision
               && new MapPorter.Options().MovingObjects == ObjectPort.MovingMethod.Full, "--moving-objects is read");
        Expect(ObjectAnims.AnibndName("AEG920_007") == @"N:\GR\data\INTERROOT_win64\asset\aeg\AEG920\AEG920_007\anibnd\AEG920_007.anibnd", "the animation binder's name, as Elden Ring's door has it");
    }

    private static Ds3.hknpConvexPolytopeShape Ds3Box(Vector3 min, Vector3 max)
    {
        var vertices = new List<Vector4>();
        for (int i = 0; i < 8; i++)
            vertices.Add(new Vector4((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z, BitConverter.Int32BitsToSingle(0x3F000000 | i)));
        var planes = new List<Vector4>
        {
            new(1, 0, 0, -max.X), new(-1, 0, 0, min.X), new(0, 1, 0, -max.Y), new(0, -1, 0, min.Y), new(0, 0, 1, -max.Z), new(0, 0, -1, min.Z),
            new(0, 0, 0, 0), new(0, 0, 0, 0), // Havok 2014 pads to a multiple of four
        };
        byte[][] quads = [[1, 3, 7, 5], [0, 4, 6, 2], [2, 6, 7, 3], [0, 1, 5, 4], [4, 5, 7, 6], [0, 2, 3, 1]];
        return new Ds3.hknpConvexPolytopeShape
        {
            m_flags = 451,
            m_numShapeKeyBits = 0,
            m_convexRadius = 0.01f,
            m_vertices = vertices,
            m_planes = planes,
            m_faces = quads.Select((q, i) => new Ds3.hknpConvexPolytopeShapeFace { m_firstIndex = (ushort)(i * 4), m_numIndices = 4, m_minHalfAngle = 128 }).ToList(),
            m_indices = quads.SelectMany(q => q).ToList(),
        };
    }

    private static void ConvexHull()
    {
        var er = CollisionPort.ConvertConvex(Ds3Box(new Vector3(0, 0, -0.05f), new Vector3(1.2f, 2.2f, 0.05f)));
        Expect(er.m_type == hknpShapeType.Enum.CONVEX_POLYTOPE && er.m_dispatchType == hknpCollisionDispatchType.Enum.CONVEX, "a convex polytope, dispatched as convex");
        Expect(er.m_vertices.Count == 8 && er.m_faces.Count == 6 && er.m_planes.Count == 6 && er.m_indices.Count == 24, "vertices, faces and indices kept; the padding planes dropped");
        Expect(er.m_vertices.Select((v, i) => BitConverter.SingleToInt32Bits(v.W) == (0x3F000000 | i)).All(x => x), "each vertex carries its number in W");
        Expect(er.m_flags.HasFlag(hknpShape.FlagsEnum.IS_CONVEX_POLYTOPE_SHAPE) && er.m_flags.HasFlag(hknpShape.FlagsEnum.IS_SDF_EDGE_COLLISION_ENABLED)
               && er.m_flags.HasFlag(hknpShape.FlagsEnum.USE_SMALL_FACE_INDICES), $"DS3's flags plus SDF edge collision ({er.m_flags})");
        Expect(er.m_maxAllowedPenetration is > 0.015f and < 0.025f, $"penetration from the thinnest side: 40% of 5 cm ({er.m_maxAllowedPenetration})");
        var thin = new Ds3.hknpConvexPolytopeShape { m_vertices = [new(0, 0, 0, 0.5f)], m_planes = [], m_faces = [], m_indices = [] };
        bool threw = false;
        try { CollisionPort.ConvertConvex(thin); }
        catch (InvalidDataException) { threw = true; }
        Expect(threw, "a hull with too few vertices or faces is refused (the file then fails, not the game)");
    }

    private static void ConvexHullPatches()
    {
        Expect(Havok.HavokRelArrays.Installed, $"HKLib's array writer is hooked ({Havok.HavokRelArrays.Failure})");
        var hull = CollisionPort.ConvertConvex(Ds3Box(new Vector3(0, 0, -0.05f), new Vector3(1.2f, 2.2f, 0.05f)));
        var system = new hknpPhysicsSystemData();
        // Two bodies share the hull: it is written once, so its four arrays are four patches, not eight.
        system.m_bodyCinfos.Add(new hknpPhysicsSystemData.bodyCinfoWithAttachment { m_name = "AEG920_007", m_shape = hull, m_motionPropertiesId = 0xFFFF });
        system.m_bodyCinfos.Add(new hknpPhysicsSystemData.bodyCinfoWithAttachment { m_name = "AEG920_007_01", m_shape = hull, m_motionPropertiesId = 0xFFFF });
        var scene = new hknpPhysicsSceneData();
        scene.m_systemDatas.Add(system);
        var root = new hkRootLevelContainer();
        root.m_namedVariants.Add(new hkRootLevelContainer.NamedVariant { m_name = "Physics Scene Data", m_className = "hknpPhysicsSceneData", m_variant = scene });
        byte[] bytes;
        lock (AnimPort.HavokLock)
            bytes = CollisionPort.Write(root, xml: false);
        Expect(CollisionPort.RelArrayFields(root) == 4, $"one hull, four arrays ({CollisionPort.RelArrayFields(root)})");
        var problems = Havok.HavokRelArrays.Check(bytes, 4);
        Expect(problems.Count == 0, string.Join("; ", problems));
        var counts = Havok.HavokRelArrays.Fields(bytes).Select(f => f.Count).Order().ToList();
        Expect(counts.SequenceEqual([6, 6, 8, 24]), $"each patch names the item with that array's elements ({string.Join(", ", counts)})");
        // The other direction: a file whose patch list misses the hull's arrays is reported (what 1.0.410 wrote).
        Expect(Havok.HavokRelArrays.Check(bytes, 5).Count > 0, "a missing hkRelArray patch is reported");
    }

    /// <summary>
    /// The frame of DS3's gate o400340 as its collision file holds it (read 2026-11-19): one section, no packed
    /// vertices, three hull primitives whose records sit in the shared-vertex index list. A lift's platform is stored
    /// the same way.
    /// </summary>
    private static Ds3.hknpCompressedMeshShape Ds3HullMesh()
    {
        var domain = new Ds3.hkAabb { m_min = new Vector4(-1.04f, 0, -0.1f, 1), m_max = new Vector4(1.04f, 2.94f, 0.1f, 1) };
        Ds3.hkcdStaticTreeCodec3Axis4 Node(byte x, byte y, byte z, byte data) => new() { m_xyz_0 = x, m_xyz_1 = y, m_xyz_2 = z, m_data = data };
        Ds3.hkcdStaticMeshTreeBasePrimitive Hull(byte a, byte b) => new() { m_indices_0 = a, m_indices_1 = b, m_indices_2 = b, m_indices_3 = b };
        var section = new Ds3.hkcdStaticMeshTreeBaseSection
        {
            m_codecParms_0 = float.MaxValue, m_codecParms_1 = float.MaxValue, m_codecParms_2 = float.MaxValue,
            m_codecParms_3 = float.NegativeInfinity, m_codecParms_4 = float.NegativeInfinity, m_codecParms_5 = float.NegativeInfinity,
            m_firstPackedVertex = 0,
            m_sharedVertices = new Ds3.hkcdStaticMeshTreeBaseSectionSharedVertices { m_data = 0 },
            m_primitives = new Ds3.hkcdStaticMeshTreeBaseSectionPrimitives { m_data = 3 },
            m_dataRuns = new Ds3.hkcdStaticMeshTreeBaseSectionDataRuns { m_data = 1 },
            m_numPackedVertices = 0, m_numSharedIndices = 9, m_flags = 1,
            m_domain = domain,
            m_nodes = [Node(0x00, 0x00, 0x00, 3), Node(0x0D, 0x07, 0x00, 4), Node(0x00, 0x00, 0x00, 3), Node(0x00, 0xD0, 0x00, 2), Node(0xD0, 0x07, 0x00, 0)],
        };
        var tree = new Ds3.hknpCompressedMeshShapeTree
        {
            m_domain = domain,
            m_nodes = [new Ds3.hkcdStaticTreeCodec3Axis5()],
            m_numPrimitiveKeys = 3, m_bitsPerKey = 3, m_maxKeyValue = 4,
            m_sections = [section],
            m_primitives = [Hull(6, 4), Hull(3, 3), Hull(0, 1)],
            m_sharedVerticesIndex = [2130, 0, 15396, 2130, 4, 15396, 2130, 8, 15396],
            m_packedVertices = [],
            m_sharedVertices =
            [
                0xF33DE62AF3004E2A, 0x0CFDE62AF33DE027, 0x0CC04E2A0CFDE027, 0xF30048270CC04827,
                0xF33F1FF5F30447F5, 0x0CFF1FF5F33F180A, 0x0CC447F50CFF180A, 0xF304400A0CC4400A,
                0xF33DE7D8F3004FD8, 0x0CFDE7D8F33DE16A, 0x0CC04FD80CFDE16A, 0xF300496A0CC0496A,
            ],
            m_primitiveDataRuns = [new Ds3.hknpCompressedMeshShapeTreeDataRun { m_value = new Ds3.hknpCompressedMeshShapeTreeDataRunData { m_data = 0xFFFF }, m_index = 0, m_count = 3 }],
        };
        return new Ds3.hknpCompressedMeshShape
        {
            m_flags = 516, m_numShapeKeyBits = 3, m_dispatchType = Ds3.Enum.COMPOSITE, m_shapeTagCodecInfo = 0xFFFFFFFF,
            m_data = new Ds3.hknpCompressedMeshShapeData { m_meshTree = tree, m_simdTree = new Ds3.hkcdSimdTree { m_nodes = [new Ds3.hkcdSimdTreeNode(), new Ds3.hkcdSimdTreeNode()] } },
        };
    }

    private static void MeshHulls()
    {
        var mesh = Ds3HullMesh();
        Expect(CollisionHulls.HasHulls(mesh.m_data.m_meshTree), "primitives whose last three indices are equal are hulls");
        bool threw = false;
        try { CollisionPort.DecodeDs3(mesh.m_data.m_meshTree); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Expect(threw, "read as triangles they point past the section's vertices (why the body was left out through 1.0.47x)");

        var ds3 = new Ds3.hkRootLevelContainer
        {
            m_namedVariants =
            [
                new Ds3.hkRootLevelContainerNamedVariant
                {
                    m_name = "Physics Scene Data", m_className = "hknpPhysicsSceneData",
                    m_variant = new Ds3.hknpPhysicsSceneData
                    {
                        m_systemDatas =
                        [
                            new Ds3.hknpPhysicsSystemData
                            {
                                m_name = "Default Physics System Data", m_materials = [], m_motionProperties = [],
                                m_bodyCinfos = [new Ds3.hknpBodyCinfo { m_shape = mesh, m_name = "ev", m_motionId = 0x7FFFFFFF, m_qualityId = 255, m_flags = 1, m_orientation = Quaternion.Identity }],
                            },
                        ],
                    },
                },
            ],
        };
        var notes = new List<string>();
        Expect(CollisionHulls.Expand(ds3, notes) == 3 && notes.Single().Contains("3 solid blocks") && notes[0].Contains("18 faces"), string.Join("; ", notes));
        var tree = mesh.m_data.m_meshTree;
        Expect(!CollisionHulls.HasHulls(tree) && tree.m_primitives.Count == 18 && tree.m_primitives.All(p => p.m_indices_2 != p.m_indices_3), "three boxes: 18 quads, no hull left");
        Expect(tree.m_numPrimitiveKeys == 36 && tree.m_maxKeyValue == 35 && tree.m_bitsPerKey == 6 && mesh.m_numShapeKeyBits == 6, $"shape keys as DS3's own quad meshes count them ({tree.m_numPrimitiveKeys} keys, max {tree.m_maxKeyValue}, {tree.m_bitsPerKey} bits)");
        Expect(mesh.m_flags == 4 && mesh.m_data.m_simdTree.m_nodes.Count == 0 && mesh.m_quadIsFlat.m_storage.m_numBits == 18 && mesh.m_triangleIsInterior.m_storage.m_numBits == 36,
            "the shape's flags and bit fields are a triangle mesh's; the old tree's SIMD nodes are gone");
        Expect(CollisionHulls.Check(tree) is null, CollisionHulls.Check(tree) ?? "");
        var section = tree.m_sections.Single();
        Expect(section.m_numPackedVertices == 24 && (section.m_sharedVertices.m_data & 0xFF) == 24 && section.m_nodes.Count == 35 && tree.m_nodes.Count == 1 && section.m_leafIndex == 0,
            "one section: 24 packed vertices, a leaf per quad");

        var triangles = CollisionPort.DecodeDs3(tree);
        Expect(triangles.Count == 36, $"36 triangles ({triangles.Count})");
        // The gate's two posts and its lintel: 1.03 m either side of the middle, the door (0.62 m either side) between.
        int Count(Func<Vector3, bool> inside) => triangles.Count(t => inside(t.A) && inside(t.B) && inside(t.C));
        int left = Count(v => v.X is > -1.04f and < -0.62f && v.Y < 2.25f), right = Count(v => v.X is > 0.59f and < 1.04f && v.Y < 2.25f), lintel = Count(v => v.Y is > 2.24f and < 2.94f);
        Expect(left == 12 && right == 12 && lintel == 12, $"two posts and a lintel, 12 triangles each ({left}, {right}, {lintel})");
        Expect(triangles.All(t => MathF.Abs(t.A.Z) is > 0.085f and < 0.1f), "9 cm either side of the gate's plane");

        // As Elden Ring's: the same triangles, on a body that moves with the bone it is named after.
        var er = CollisionPort.Convert(ds3, ErCollisionTemplate.Default, keepSimdTree: false, notes);
        var after = CollisionPort.Decode(er).Single();
        Expect(after.Name == "ev" && after.Triangles.Count == 36 && after.Triangles.Zip(triangles).All(p => Vector3.Distance(p.First.A, p.Second.A) < 1e-4f && Vector3.Distance(p.First.C, p.Second.C) < 1e-4f),
            "the converted body holds the same triangles");
        int moving = ObjectAnims.MovingBodies(er, new HashSet<string>(["ev"], StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(), null, notes);
        var body = CollisionPort.Bodies(er).Single();
        Expect(moving == 1 && body.m_motionType == 1 && body.m_shape is HKLib.hk2018.hknpCompressedMeshShape, "the platform follows its bone");

        // A hull record that points past the lists leaves the body as it was, with a note (DropBroken then decides).
        var broken = Ds3HullMesh();
        broken.m_data.m_meshTree.m_sharedVerticesIndex[1] = 11;
        ((Ds3.hknpPhysicsSceneData)ds3.m_namedVariants[0].m_variant).m_systemDatas[0].m_bodyCinfos[0].m_shape = broken;
        notes.Clear();
        Expect(CollisionHulls.Expand(ds3, notes) == 0 && notes.Single().Contains("can't be read") && CollisionHulls.HasHulls(broken.m_data.m_meshTree), string.Join("; ", notes));
    }

    private static void HullFacesAndSections()
    {
        static bool Outwards(CollisionHulls.Primitive p, Vector3 middle) =>
            Vector3.Dot(Vector3.Cross(p.Corners[1] - p.Corners[0], p.Corners[2] - p.Corners[0]), p.Corners[0] - middle) > 0;
        var tetra = CollisionHulls.HullFaces([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)]);
        Expect(tetra.Count == 4 && tetra.All(p => p.Corners.Length == 3 && Outwards(p, new Vector3(0.25f))), "a tetrahedron: four triangles facing out");
        // A box with a point inside, one on an edge and one repeated: still six quads.
        var corners = Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) * 2, ((i >> 1) & 1) * 3, (i >> 2) & 1)).ToList();
        var box = CollisionHulls.HullFaces([.. corners, new(1, 1, 0.5f), new(1, 0, 0), corners[3]]);
        Expect(box.Count == 6 && box.All(p => p.Corners.Length == 4 && Outwards(p, new Vector3(1, 1.5f, 0.5f))), $"a box: six quads facing out ({box.Count})");
        Expect(CollisionHulls.HullFaces([new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0.5f, 0.5f, 0)]).Count == 0, "points in one plane have no volume: no faces");
        // A prism over a pentagon: two pentagons (a quad and a triangle each) and five quads.
        var pentagon = Enumerable.Range(0, 5).Select(i => new Vector3(MathF.Cos(i * 1.2566f), 0, MathF.Sin(i * 1.2566f))).ToList();
        var prism = CollisionHulls.HullFaces([.. pentagon, .. pentagon.Select(v => v + Vector3.UnitY)]);
        Expect(prism.Count == 9 && prism.Count(p => p.Corners.Length == 3) == 2 && prism.All(p => Outwards(p, new Vector3(0, 0.5f, 0))), $"a pentagon prism: 7 quads and 2 triangles ({prism.Count})");

        // A floor of 400 quads and 100 triangles, 40 m across: several sections, every box holding its faces.
        var faces = new List<CollisionHulls.Primitive>();
        for (int x = 0; x < 20; x++)
            for (int z = 0; z < 20; z++)
                faces.Add(new CollisionHulls.Primitive([new(x * 2, 0, z * 2), new(x * 2, 0.1f * x, z * 2 + 2), new(x * 2 + 2, 0.1f * x, z * 2 + 2), new(x * 2 + 2, 0, z * 2)]));
        for (int i = 0; i < 100; i++)
            faces.Add(new CollisionHulls.Primitive([new(i * 0.4f, 5, 0), new(i * 0.4f, 6, 1), new(i * 0.4f + 0.3f, 5, 2)]));
        var tree = CollisionHulls.Build(faces);
        Expect(tree.m_sections.Count > 8 && tree.m_sections.All(s => (s.m_primitives.m_data & 0xFF) <= CollisionHulls.SectionPrimitives && s.m_numPackedVertices > 0),
            $"sections of at most {CollisionHulls.SectionPrimitives} primitives ({tree.m_sections.Count})");
        Expect(CollisionHulls.Check(tree) is null, CollisionHulls.Check(tree) ?? "");
        Expect(tree.m_nodes.Count == tree.m_sections.Count * 2 - 1 && tree.m_primitives.Count == 500 && tree.m_numPrimitiveKeys == 900 && tree.m_primitiveDataRuns.Count == tree.m_sections.Count,
            "a leaf per section, a data run per section, two keys per quad and one per triangle");
        Expect(tree.m_bitsPerKey == 8 + (32 - BitOperations.LeadingZeroCount((uint)tree.m_sections.Count - 1)), $"keys name the section above 8 bits of primitive ({tree.m_bitsPerKey} bits)");
        var decoded = CollisionPort.DecodeDs3(tree);
        Expect(decoded.Count == 900, $"900 triangles decode ({decoded.Count})");
        // Packing moves a vertex by at most half a step of its section (a section here is under 30 m: 7 mm).
        var corners2 = faces.SelectMany(f => f.Corners).ToList();
        Expect(decoded.All(t => new[] { t.A, t.B, t.C }.All(v => corners2.Any(c => Vector3.Distance(c, v) < 0.02f))), "every decoded corner is one of the faces' corners");
    }

    private static void MovingBodies()
    {
        hknpPhysicsSystemData.bodyCinfoWithAttachment Body(string name) => new() { m_name = name, m_motionPropertiesId = 0xFFFF, m_motionId = 0x7FFFFFFF, m_flags = 1 };
        var system = new hknpPhysicsSystemData();
        system.m_bodyCinfos.Add(Body("o300310"));
        system.m_bodyCinfos.Add(Body("o300310_01"));
        var scene = new hknpPhysicsSceneData();
        scene.m_systemDatas.Add(system);
        var root = new hkRootLevelContainer();
        root.m_namedVariants.Add(new hkRootLevelContainer.NamedVariant { m_name = "Physics Scene Data", m_className = "hknpPhysicsSceneData", m_variant = scene });
        var motion = new hknpMotionProperties { m_gravityFactor = 1, m_maxLinearSpeed = 200 };
        var template = new ObjectAnims.Template
        {
            Binder = new BND4(), Flags = Binder.FileFlags.Flag1, Skeleton = [], Tae = [], Animation = [],
            MovingBody = new hknpPhysicsSystemData.bodyCinfoWithAttachment { m_motionType = 1, m_qualityId = 255, m_flags = 0 },
            MovingMotion = motion,
        };
        var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["o300310"] = "AEG920_310" };
        var notes = new List<string>();
        int moving = ObjectAnims.MovingBodies(root, new HashSet<string>(["o300310_01", "o300310_02"], StringComparer.OrdinalIgnoreCase), rename, template, notes);
        var frame = system.m_bodyCinfos[0];
        var leaf = system.m_bodyCinfos[1];
        Expect(moving == 1 && frame.m_name == "AEG920_310" && frame.m_motionType == 0 && frame.m_motionPropertiesId == 0xFFFF && frame.m_flags == 1,
            "the frame (named after the root, now the asset) stays a still body");
        Expect(leaf.m_motionType == 1 && leaf.m_motionPropertiesId == 0 && system.m_motionProperties.Single() == motion && leaf.m_motionId == 0x7FFFFFFF && leaf.m_flags == 0,
            "the leaf takes the door leaf's settings and its motion properties");
        Expect(notes.Count == 0, string.Join("; ", notes));
        // A lift's platform named like one bone only follows it, under the bone's name; the frame is said to stay.
        system.m_bodyCinfos.Add(Body("model_EV1"));
        system.m_bodyCinfos.Add(Body("Frame"));
        Expect(ObjectAnims.MovingBodies(root, new HashSet<string>(["EV1", "chain", "o300310_01"], StringComparer.OrdinalIgnoreCase), rename, template, notes) == 2
               && system.m_bodyCinfos[2].m_name == "EV1" && system.m_bodyCinfos[2].m_motionType == 1 && system.m_bodyCinfos[3].m_motionType == 0,
            "a body named like one bone takes that bone's name and moves");
        Expect(notes.Any(n => n.Contains("model_EV1 follows the bone EV1")) && notes.Any(n => n == "bodies that stay put: Frame"), string.Join("; ", notes));
        notes.Clear();
        system.m_bodyCinfos.RemoveRange(2, 2);
        Expect(ObjectAnims.MovingBodies(root, new HashSet<string>(["nothing"]), rename, null, notes) == 0 && notes.Any(n => n.Contains("no collision body")),
            "the report says when no body follows a bone");
    }

    private static void ObjActRows()
    {
        PARAMDEF Def(string type, params (PARAMDEF.DefType Type, string Name)[] fields)
        {
            var def = new PARAMDEF { ParamType = type, DataVersion = 1 };
            foreach (var (t, n) in fields)
                def.Fields.Add(new PARAMDEF.Field(def, t, n));
            return def;
        }
        var ds3Def = Def("OBJ_ACT_PARAM_ST", (PARAMDEF.DefType.s32, "actionEnableMsgId"), (PARAMDEF.DefType.s32, "spQualifiedPassEventFlag"), (PARAMDEF.DefType.u32, "playerAnimId"),
            (PARAMDEF.DefType.s32, "spQualifiedId"), (PARAMDEF.DefType.u16, "ObjDummyId"), (PARAMDEF.DefType.s32, "ObjAnimId"), (PARAMDEF.DefType.u8, "spQualifiedType"),
            (PARAMDEF.DefType.s32, "actionButtonParamId"));
        var erDef = Def("OBJ_ACT_PARAM_ST", (PARAMDEF.DefType.s32, "actionEnableMsgId"), (PARAMDEF.DefType.u32, "spQualifiedPassEventFlag"), (PARAMDEF.DefType.u32, "playerAnimId"),
            (PARAMDEF.DefType.s32, "spQualifiedId"), (PARAMDEF.DefType.u16, "objDummyId"), (PARAMDEF.DefType.u8, "isEventKickSync"), (PARAMDEF.DefType.s32, "objAnimId"),
            (PARAMDEF.DefType.u8, "spQualifiedType"), (PARAMDEF.DefType.s32, "actionButtonParamId"));
        var ds3 = new PARAM { ParamType = ds3Def.ParamType, Rows = [] };
        PARAM.Row Ds3Row(int id, int dummy, int anim, byte type, int qualified, int button)
        {
            var row = new PARAM.Row(id, $"row {id}", ds3Def);
            row["actionEnableMsgId"].Value = 10010400;
            row["spQualifiedPassEventFlag"].Value = -1;
            row["playerAnimId"].Value = 60000u;
            row["spQualifiedId"].Value = qualified;
            row["ObjDummyId"].Value = (ushort)dummy;
            row["ObjAnimId"].Value = anim;
            row["spQualifiedType"].Value = type;
            row["actionButtonParamId"].Value = button;
            ds3.Rows.Add(row);
            return row;
        }
        Ds3Row(0, 191, 1, 0, 0, -1);            // DS3's row 0: "none", not ported
        Ds3Row(300310, 121, 0, 2, 4150, 7200);  // one-way door: the side check stays
        Ds3Row(300320, 101, 1, 1, 2009, 3411000); // a key door with a prompt Elden Ring lacks
        Ds3Row(300510, 191, 1, 0, 0, 8320);     // a lever with a prompt Elden Ring lacks

        var er = new PARAM { ParamType = erDef.ParamType, Rows = [] };
        var template = new PARAM.Row(ObjectPort.ObjActTemplateRow, "door", erDef);
        template["isEventKickSync"].Value = (byte)0;
        er.Rows.Add(template);
        er.Rows.Add(new PARAM.Row(70300310, "stale", erDef));
        var buttonDef = Def("ACTION_BUTTON_PARAM_ST", (PARAMDEF.DefType.s32, "regionType"));
        var buttons = new PARAM { ParamType = buttonDef.ParamType, Rows = [new PARAM.Row(7200, "", buttonDef), new PARAM.Row(8301, "", buttonDef)] };

        var notes = new List<string>();
        Expect(ObjectPort.AddObjActRows(er, ds3, buttons, notes) == 3, "every DS3 row but 0");
        Expect(er.Rows.Select(r => r.ID).SequenceEqual([ObjectPort.ObjActTemplateRow, 70300310, 70300320, 70300510]), "at 70,000,000 + the DS3 ID, in order, the stale row replaced");
        var door = er[70300310];
        Expect((ushort)door["objDummyId"].Value == 121 && (int)door["objAnimId"].Value == 0 && (uint)door["playerAnimId"].Value == 60000u, "fields copied by name, whatever their case");
        Expect((byte)door["spQualifiedType"].Value == 2 && (int)door["spQualifiedId"].Value == 4150 && (int)door["actionButtonParamId"].Value == 7200, "the side check and a prompt Elden Ring has stay");
        Expect((uint)door["spQualifiedPassEventFlag"].Value == 0, "DS3's 'no flag' (-1) is Elden Ring's 0");
        var key = er[70300320];
        Expect((byte)key["spQualifiedType"].Value == 0 && (int)key["spQualifiedId"].Value == 0, "a key requirement is dropped (keys aren't ported)");
        Expect((int)key["actionButtonParamId"].Value == 7200 && (int)er[70300510]["actionButtonParamId"].Value == 8301, "missing prompts become the door or lever prompt");
        Expect(notes.Single().Contains("1 key requirements dropped") && notes[0].Contains("2 prompts"), notes[0]);
        ObjectPort.AddObjActRows(er, ds3, buttons);
        Expect(er.Rows.Count == 4, "a second pass replaces its rows");
        Expect(ObjectPort.ObjActRow(300310) == 70300310 && ObjectPort.ObjActRow(-1) == -1 && ObjectPort.ObjActRow(0) == 0, "row IDs move, 'none' stays");
        Expect(EventPort.KindOf("ObjAct Param ID") == EventPort.Kind.ObjAct && EventPort.KindOf("Object Parameter ID") == EventPort.Kind.ObjAct,
            "the scripts' ObjActParam arguments (Set ObjAct State) are moved the same way");
    }

    private static void ObjActEvents()
    {
        var ds3 = SyntheticDs3();
        ds3.Models.Objects.Add(new MSB3.Model.Object { Name = "o300310" });
        ds3.Models.Objects.Add(new MSB3.Model.Object { Name = "o302590" });
        ds3.Parts.Objects.Add(new MSB3.Part.Object { Name = "o300310_0000", ModelName = "o300310", EntityID = 4001510 });
        ds3.Parts.Objects.Add(new MSB3.Part.Object { Name = "o302590_0000", ModelName = "o302590", EntityID = 4001500 });
        ds3.Events.ObjActs.Add(new MSB3.Event.ObjAct
        {
            Name = "door", ObjActEntityID = 4001511, ObjActPartName = "o300310_0000", ObjActParamID = 300310,
            ObjActStateType = MSB3.Event.ObjAct.ObjActState.DoorState, EventFlagID = 63000155,
        });
        ds3.Events.ObjActs.Add(new MSB3.Event.ObjAct { Name = "still", ObjActPartName = "o302590_0000", ObjActParamID = 200, EventFlagID = 4000500 });
        ds3.Events.ObjActs.Add(new MSB3.Event.ObjAct { Name = "gone", ObjActPartName = "o999999_0000", ObjActParamID = 200 });
        var layout = MsbPort.Convert(ds3, SyntheticHost(), Ds3Id, ErId);
        var assets = new Dictionary<string, ObjectPort.Asset>(StringComparer.OrdinalIgnoreCase)
        {
            ["o300310"] = new() { Ds3Model = "o300310", Index = 3, Name = "AEG920_003", Converted = true, Moving = true, Collision = true },
            ["o302590"] = new() { Ds3Model = "o302590", Index = 7, Name = "AEG920_007", Converted = true },
        };
        var placed = ObjectPort.PlaceParts(ds3, layout.Msb, Ds3Id, ErId, new MsbPort.Options(), m => assets.GetValueOrDefault(m));
        Expect(placed.Notes.Any(n => n == "ObjActs: 1 placed, 1 left out (their object doesn't move), 1 left out (their object wasn't placed)"), string.Join("; ", placed.Notes));
        var msb = MSBE.Read(layout.Msb.Write());
        var act = msb.Events.ObjActs.Single(e => e.Name.EndsWith("door"));
        var door = msb.Parts.Assets.Single(a => a.ModelName == "AEG920_003");
        Expect(act.ObjActPartName == door.Name && act.ObjActID == 70300310 && act.StateType == 1, "names the door's asset part, the ported row and DS3's state type");
        Expect(act.ObjActEntityID == 45001511 && act.MapID == -1 && act.UnkE0C == 255 && act.UnkS0C == -1 && act.PartName is null, "entity moved with the map; laid out like Elden Ring's own");
        Expect(act.Name == ObjectPort.PendingFlag + "door" && act.EventFlagID == 63000155, "DS3's flag, marked for the event step");

        Expect(ObjectPort.RemapObjActFlags(msb, v => v == 63000155 ? 1048570155 : v) == 1, "the event step moves the flag");
        Expect(act.EventFlagID == 1048570155 && act.Name == "[DS3] door", "the flag the converted scripts wait on, and the mark is gone");
        Expect(ObjectPort.RemapObjActFlags(msb, v => v + 1) == 0 && act.EventFlagID == 1048570155, "a second pass leaves it alone");

        Expect(ObjectPort.Strip(msb) == 2 && msb.Events.ObjActs.Count == 0, "Strip takes the ObjActs out with the assets");
        MSBE.Read(msb.Write());
    }
}
