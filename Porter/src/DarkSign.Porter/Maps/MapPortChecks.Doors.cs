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
