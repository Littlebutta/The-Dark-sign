using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Offline checks for what makes a ported map piece draw (2026-11-09 playtest: the maps loaded and were walkable but
/// invisible): the part's draw flags, the mesh's vertex layout and culling box, and the material kinds.
/// </summary>
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> VisualChecks()
    {
        yield return ("visuals: a piece copied from a reflection-only host piece draws in the main camera", ReflectionOnlyHost);
        yield return ("visuals: every ported mesh takes its material's vanilla vertex layout and Elden Ring's culling box", MeshLayoutAndBox);
        yield return ("visuals: DS3 materials sort into kinds; effect, decal, water and shadow meshes are left out", MaterialKinds);
        yield return ("visuals: UVs that a short can't hold are rescaled or moved by whole tiles", UvRange);
        yield return ("visuals: pieces name a PartsDrawParam row, carry no masks", DrawSettings);
        yield return ("visuals: test maps hold the host's own model, its own material, or the port's materials with vanilla textures", PieceTests);
        yield return ("visuals: fallbacks map-groups C and map-materials B", VisualFallbacks);
    }

    private static void ReflectionOnlyHost()
    {
        // The colosseum's only map piece, as read from m45_00_00_00.msb.
        var host = SyntheticHost();
        var proxy = host.Parts.MapPieces[0];
        proxy.DrawOnlyReflectCam = true;
        proxy.IsShadowDest = false;
        proxy.EnableOnAboveShadow = 1;
        proxy.IsCascade3ShadowSrc = 1;
        var result = MsbPort.Convert(SyntheticDs3(), host, Ds3Id, ErId);
        var piece = MSBE.Read(result.Msb.Write()).Parts.MapPieces.Single();
        Expect(!piece.DrawOnlyReflectCam && !piece.DrawByReflectCam && !piece.IsShadowOnly, "drawn by the main camera");
        Expect(piece.IsShadowDest && piece.EnableOnAboveShadow == 0 && piece.IsCascade3ShadowSrc == 0, "ordinary shadow flags");
        Expect(result.Notes.Any(n => n.Contains("reflections")), "the report says the host piece was a reflection proxy");
        Expect(MapValidate.CheckPieceDraw(piece) is null, "the validator accepts it");
        Expect(MapValidate.CheckPieceDraw(proxy) is not null, "the validator rejects a reflection-only piece");

        // An ordinary piece is preferred when the host has one.
        var mixed = SyntheticHost();
        mixed.Parts.MapPieces[0].DrawOnlyReflectCam = true;
        mixed.Parts.MapPieces.Add(new MSBE.Part.MapPiece { Name = "m001000_0001", ModelName = "m001000", PartsDrawParamID = 77 });
        var second = MsbPort.Convert(SyntheticDs3(), mixed, Ds3Id, ErId);
        Expect(second.Notes.All(n => !n.Contains("reflections")), "ordinary host piece used");
    }

    private static void DrawSettings()
    {
        // A piece copied from the colosseum's proxy names no PartsDrawParam row; every ported piece must.
        var host = SyntheticHost();
        host.Parts.MapPieces[0].isUsePartsDrawParamID = 0;
        var result = MsbPort.Convert(SyntheticDs3(), host, Ds3Id, ErId,
            new MsbPort.Options { DrawParamId = MapDrawParams.FirstRow });
        var msb = MSBE.Read(result.Msb.Write());
        Expect(msb.Parts.MapPieces.All(p => p.isUsePartsDrawParamID == 1 && p.PartsDrawParamID == MapDrawParams.FirstRow), "every piece names the row");
        Expect(msb.Parts.MapPieces.All(p => MapValidate.CheckPieceDraw(p) is null), "the validator accepts them");
        Expect(MapValidate.CheckPieceDraw(host.Parts.MapPieces[0]) is not null, "the validator rejects a piece that names no row");

        // DS3's masks, when asked for, come with Condition2 as vanilla parts with masks have it.
        var masked = MsbPort.Convert(SyntheticDs3(), host, Ds3Id, ErId, new MsbPort.Options { AllVisible = false });
        foreach (var p in masked.Msb.Parts.MapPieces)
            Expect(MapValidate.CheckPieceDraw(p) is null, "masks never come without Condition2");

        // The row: a copy of the template with an unlimited draw distance and no LOD switch.
        var def = new PARAMDEF { ParamType = "PARTS_DRAW_PARAM_ST", DataVersion = 1 };
        foreach (var (type, name) in new[] { (PARAMDEF.DefType.f32, "lv01_BorderDist"), (PARAMDEF.DefType.f32, "drawDist"),
                     (PARAMDEF.DefType.s8, "isShadowOnly"), (PARAMDEF.DefType.s8, "drawOnlyReflectCam"), (PARAMDEF.DefType.u8, "lodType") })
            def.Fields.Add(new PARAMDEF.Field(def, type, name));
        var param = new PARAM { ParamType = def.ParamType, Rows = [] };
        param.ApplyParamdef(def);
        var template = new PARAM.Row(MapDrawParams.TemplateRow, "", def);
        template.Cells.Single(c => c.Def.InternalName == "drawDist").Value = 120f;
        template.Cells.Single(c => c.Def.InternalName == "drawOnlyReflectCam").Value = (sbyte)1;
        param.Rows.Add(template);
        param.Rows.Add(new PARAM.Row(MapDrawParams.FirstRow, "stale", def));
        var id = MapDrawParams.AddRow(param);
        var row = param[id]!;
        Expect(id == MapDrawParams.FirstRow && param.Rows.Count(r => r.ID == id) == 1, "one row, replacing an earlier run's");
        Expect((float)row.Cells.Single(c => c.Def.InternalName == "drawDist").Value > 9000f
               && (float)row.Cells.Single(c => c.Def.InternalName == "lv01_BorderDist").Value > 9000f, "unlimited draw distance, no LOD switch");
        Expect((sbyte)row.Cells.Single(c => c.Def.InternalName == "drawOnlyReflectCam").Value == 0, "never a reflection proxy");
        Expect((float)param[MapDrawParams.TemplateRow]!.Cells.Single(c => c.Def.InternalName == "drawDist").Value == 120f, "the template row is untouched");
    }

    private static void PieceTests()
    {
        // Which slot is which: the first is the real port, the second and third are the two tests.
        var slots = MapSlots.All.Where(s => !s.Probe).ToList();
        var none = new MapPorter.Options();
        Expect(MapPorter.PieceTestFor(slots[0], none) == MapPorter.PieceTest.None, "the first slot is the real port");
        Expect(slots.Count < 3 || (MapPorter.PieceTestFor(slots[1], none) == MapPorter.PieceTest.PortedMaterial
                                   && MapPorter.PieceTestFor(slots[2], none) == MapPorter.PieceTest.VanillaMaterial), "the second and third slots are the tests");
        var off = MapPorter.Options.Parse(["--no-piece-test"]);
        Expect(slots.All(s => MapPorter.PieceTestFor(s, off) == MapPorter.PieceTest.None), "--no-piece-test builds every slot normally");
        var asked = MapPorter.Options.Parse(["--no-piece-test", "--piece-test", $"{slots[0].Er}=vanilla-material"]);
        Expect(MapPorter.PieceTestFor(slots[0], asked) == MapPorter.PieceTest.VanillaMaterial && asked.Only.Count == 0, "--piece-test names a slot's test");

        // vanilla-model: the host's FLVER, byte for byte, under the new model's name and the same file ID.
        var (port, hostBnd) = VisualPort(plain: true);
        var hostFile = hostBnd.Files.Single();
        var shell = BND4.Read(MapModelPort.VanillaBinder(hostBnd, ErId, "000100").Write()).Files.Single();
        Expect(shell.Bytes.AsSpan().SequenceEqual(hostFile.Bytes) && shell.ID == hostFile.ID, "vanilla-model: Elden Ring's own bytes and file ID");
        Expect(shell.Name == @"N:\GR\data\Model\map\m45_00_00_00\m45_00_00_00_000100\sib\m45_00_00_00_000100.flver", $"vanilla-model: named after the new model ({shell.Name})");

        // vanilla-material: DS3's geometry, the template's own material, and nothing for the port to add.
        var vanilla = port.WithVanillaMaterials();
        var ds3 = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN].mtd");
        var vertices = ds3.Meshes[0].Vertices.Count;
        var result = ConvertOne(vanilla, hostBnd, ds3);
        var hostMaterial = FLVER2.Read(hostFile.Bytes).Materials[0];
        Expect(result.Materials.All(m => m.MTD == hostMaterial.MTD && m.Name == hostMaterial.Name), $"vanilla-material: the host's material ({result.Materials[0].MTD})");
        Expect(result.Meshes.Count == 1 && result.Meshes[0].Vertices.Count == vertices, "vanilla-material: DS3's geometry");
        Expect(!vanilla.Materials.Any() && vanilla.Textures.Count == 0 && !vanilla.MatbinFiles(0).Any(), "vanilla-material: no MATBIN or texture added");
        Expect(vanilla.Fresh().VanillaMaterials, "a fresh port keeps the test mode");

        // ported-material: the port's own MATBIN (renamed copy of the template) with the template's textures.
        Expect(MapPorter.ParsePieceTest("ported-material") == MapPorter.PieceTest.PortedMaterial, "--piece-test ported-material");
        var kept = port.WithTemplateTextures();
        var keptResult = ConvertOne(kept, hostBnd, SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN].mtd"));
        var keptMaterial = kept.Materials.Single();
        var keptMatbin = MATBIN.Read(kept.MatbinFiles(0).Single().Bytes);
        var templateMatbin = MATBIN.Read(keptMaterial.Template.File.Bytes);
        Expect(keptResult.Materials.All(m => m.Name == keptMaterial.Name && m.Name != hostMaterial.Name), "ported-material: the model names the port's own material");
        Expect(keptMatbin.Samplers.Select(s => s.Path).SequenceEqual(templateMatbin.Samplers.Select(s => s.Path)), "ported-material: the MATBIN keeps the template's textures");
        Expect(kept.Textures.Count == 0 && kept.Fresh().TemplateTextures, "ported-material: no texture asked for, and a fresh port keeps the mode");
    }

    private static (MapModelPort Port, BND4 HostBnd) VisualPort(bool plain = false)
    {
        var matbins = new Dictionary<string, BinderFile>();
        var flvers = new List<FLVER2>();
        void Add(string name, string shader, params string[] samplers)
        {
            var matbin = new MATBIN { ShaderPath = $@"N:\GR\data\Material\sat\OutputData\SPX\{shader}.spx", SourcePath = $@"N:\GR\data\Material\mtd\Map_m10_00\matxml\{name}.matxml" };
            foreach (var s in samplers)
                matbin.Samplers.Add(new MATBIN.Sampler { Type = s, Path = @"N:\GR\data\Other\SysTex\SYSTEX_Dummy.tif" });
            matbins[name] = new(Binder.FileFlags.Flag1, matbins.Count, $@"N:\GR\data\INTERROOT_win64\material\matbin\Map_m10_00\matxml\{name}.matbin", MATBIN.Read(matbin.Write()).Write());
            flvers.Add(SyntheticFlver(0x2001A, $@"N:\GR\data\Material\mtd\Map_m10_00\matxml\{name}.matxml"));
        }
        // Real shader and sampler names (allmaterial.matbinbnd): the colosseum's albedo-only one first, as in a build.
        Add("m45_00_001", "M[A]", "M_A__snp_Texture2D_0_AlbedoMap_0");
        Add("m10_00_006", "M[AMSN_V][Ov_N]", "M_AMSN_V_Ov_N__snp_Texture2D_8_AlbedoMap_0", "M_AMSN_V_Ov_N__snp_Texture2D_1_NormalMap_0", "M_AMSN_V_Ov_N__snp_Texture2D_2_NormalMap_0");
        Add("m10_00_004", "M[AMSN_V]", "M_AMSN_V__snp_Texture2D_0_AlbedoMap_0", "M_AMSN_V__snp_Texture2D_1_NormalMap_0");
        Add("m10_00_207", "M[AMSN_V]_Edge", "M_AMSN_V__Edge_snp_Texture2D_0_AlbedoMap_0", "M_AMSN_V__Edge_snp_Texture2D_1_NormalMap_0");
        Add("m10_00_218", "M[AMSN_V]_Alpha", "M_AMSN_V__Alpha_snp_Texture2D_0_AlbedoMap_0", "M_AMSN_V__Alpha_snp_Texture2D_1_NormalMap_0");
        Add("m10_00_003", "M[A]_NoLight", "M_A__NoLight_snp_Texture2D_0_AlbedoMap_0");
        Add("m10_00_185", "M[AMSN_V]_Grass", "M_AMSN_V__Grass_snp_Texture2D_0_AlbedoMap_0", "M_AMSN_V__Grass_snp_Texture2D_1_NormalMap_0");
        var hostBnd = new BND4();
        hostBnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 200, @"N:\GR\data\Model\map\m45_00_00_00\m45_00_00_00_001000\sib\m45_00_00_00_001000.flver", flvers[0].Write()));
        return (MapModelPort.Create(flvers, matbins, flvers[0], plain), hostBnd);
    }

    private static FLVER2 ConvertOne(MapModelPort port, BND4 hostBnd, FLVER2 ds3)
    {
        var bnd = new BND4();
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\FDP\data\Model\map\m40_00_00_00\sib\m40_00_00_00_000100.flver", ds3.Write()));
        var file = BND4.Read(port.ConvertBinder(bnd, hostBnd, Ds3Id, ErId, "000100").Write()).Files.Single();
        Expect(file.ID == 200, $"the model keeps the template's binder file ID (200), got {file.ID}");
        return FLVER2.Read(file.Bytes);
    }

    private static string LayoutText(IEnumerable<FLVER.LayoutMember> layout) =>
        string.Join(" ", layout.Select(m => $"{m.Semantic}{m.Index}:{m.Type}"));

    private static void MeshLayoutAndBox()
    {
        var (port, hostBnd) = VisualPort();
        Expect(port.TemplateSummary == "opaque=m10_00_004, edge=m10_00_207, alpha=m10_00_218, nolight=m10_00_003"
               || MapModelPort.Kinds.All(k => port.TemplateSummary.Contains(k + "=")), $"templates {port.TemplateSummary}");
        Expect(port.TemplateSummary.Contains("opaque=m10_00_004"), $"the normal-mapped shader wins over albedo-only and overlay ones: {port.TemplateSummary}");
        Expect(port.Complete(false), "every kind found");

        // A multi-layer DS3 ground: textures under blend names, vertex colour 0, the mesh on a node the file lacks.
        var ds3 = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\MultiBlend\M[ARSN]_4m_l.mtd",
            ("MultiBlend4_et1_snp_Texture2D_1_GSBlendMap_AlbedoMap_0", @"N:\FDP\data\Model\map\m40\tex\m40_00_ground_001_a.tif"),
            ("MultiBlend4_et1_snp_Texture2D_2_GSBlendMap_AlbedoMap_1", @"N:\FDP\data\Model\map\m40\tex\m40_00_ground_002_a.tif"),
            ("MultiBlend4_et1_snp_Texture2D_13_GSBlendMap_NormalMap_0", @"N:\FDP\data\Model\map\m40\tex\m40_00_ground_001_n.tif"));
        ds3.Meshes[0].NodeIndex = 1;
        ds3.Meshes[0].BoundingBox = new FLVER2.Mesh.BoundingBoxes { Min = new Vector3(0, 0, 0), Max = new Vector3(10, 0, 10) };
        var flver = ConvertOne(port, hostBnd, ds3);
        var mesh = flver.Meshes.Single();
        var layout = flver.BufferLayouts[mesh.VertexBuffers.Single().LayoutIndex];
        const string Vanilla = "Position0:Float3 Normal0:UByte4 Tangent0:UByte4 VertexColor1:UByte4Norm UV0:Short2";
        Expect(LayoutText(layout) == Vanilla, $"layout {LayoutText(layout)}");
        Expect(mesh.Vertices.All(v => v.UVs.Count == 1 && v.Tangents.Count == 1 && v.Colors.Count == 1
                                      && v.Colors[0] is { R: 1, G: 0, B: 0, A: 1 }), "one UV, one tangent, vanilla's vertex colour (1, 0, 0, 1)");
        Expect(flver.Nodes.Count == 1 && flver.Nodes[0].Name.StartsWith(ErId.ToString()), $"node named after the Elden Ring model: {flver.Nodes[0].Name}");
        Expect(flver.Materials.Select(m => m.Index).SequenceEqual(Enumerable.Range(0, flver.Materials.Count)), "materials numbered in order");
        Expect(mesh.Vertices.Select(v => v.Position).SequenceEqual([new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 0, 10)]), "positions kept");
        // Elden Ring's box: half size, rotation, centre.
        var box = mesh.BoundingBox;
        Expect(box.Min == new Vector3(5, 0, 5) && box.Max == Vector3.Zero && box.Unk == new Vector3(5, 0, 5), $"culling box {box.Min} {box.Max} {box.Unk}");
        Expect(port.Textures.SetEquals(["m40_00_ground_001_a", "m40_00_ground_001_n"]), $"first-layer textures {string.Join(",", port.Textures)}");
        Expect(port.Notes.Count == 0, "no 'keeps the template's texture' note for a multi-layer material");
        Expect(MapValidate.CheckFlverDraw(flver, port.TemplateLayouts()).ToList() is { Count: 0 }, "the validator accepts the converted model");

        // What the 1.0.242 build wrote (DS3's layout and box under an Elden Ring material) is rejected.
        var old = SyntheticFlver(0x2001A, @"N:\GR\data\Material\mtd\Map_m45_00\matxml\DKS_40_00_00_00_0BADF00D.matxml");
        old.BufferLayouts[0].Add(new FLVER.LayoutMember(FLVER.LayoutType.Short2, FLVER.LayoutSemantic.UV, 1));
        foreach (var v in old.Meshes[0].Vertices)
            v.UVs.Add(Vector3.Zero);
        old.Meshes[0].BoundingBox = new FLVER2.Mesh.BoundingBoxes { Min = new Vector3(0, 0, 0), Max = new Vector3(10, 0, 10) };
        var issues = MapValidate.CheckFlverDraw(FLVER2.Read(old.Write()), port.TemplateLayouts()).ToList();
        Expect(issues.Any(i => i.Contains("layout")) && issues.Any(i => i.Contains("box")), $"old output must fail: {string.Join("; ", issues)}");

        // A sky dome's tiling carries over to the sampler.
        var sky = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[A]_NoLight.mtd");
        sky.Materials[0].Textures.Add(new FLVER2.Texture("g_DiffuseTexture", @"N:\FDP\data\Model\map\m40\tex\m40_00_sky_50_a.tif", new Vector2(3, 1), 0, 0, 0, 0, 0));
        var skyOut = ConvertOne(port, hostBnd, sky);
        var skyMatbin = port.MatbinFiles(0).Select(f => MATBIN.Read(f.Bytes)).Single(m => m.Samplers.Any(s => s.Path == "m40_00_sky_50_a"));
        Expect(skyMatbin.ShaderPath.Contains("NoLight") && skyMatbin.Samplers[0].Unk14 == new Vector2(3, 1), "sky on the unlit shader with DS3's tiling");
        Expect(ModelPort.MatbinName(skyOut.Materials[0].MTD).StartsWith("DKS_") && skyOut.Meshes.Single().Vertices.All(v => v.Tangents.Count == 1),
            "the sky mesh names its new material and gets the tangent DS3's unlit layout lacks");
    }

    private static void MaterialKinds()
    {
        static string Kind(string mtd) => MapModelPort.Ds3Kind(new FLVER2.Material("m", $@"N:\FDP\data\Material\mtd\map\{mtd}.mtd", 0));
        foreach (var (mtd, kind) in new[]
                 {
                     ("M[ARSN]", "opaque"), ("M[ARSN]_l", "opaque"), ("M[ARSN]_4m_et1_l", "opaque"), ("M[ARSN]_e", "edge"), ("M[ARSN]_l_e", "edge"),
                     ("M[ARSN]_e_grass", "edge"), ("M[ARSN]_Alp", "edge"), ("M[A]_NoLight", "nolight"), ("M[A]_2m_NoLight_scroll", "nolight"),
                     ("M[A]_al_NoLight", "none"), ("M[A]_al_m_NoLight_Opacity", "none"), ("M[ARSN]_al", "alpha"), ("M[A]_Add_NoLight", "none"),
                     ("M[ARSN]_Decal", "none"), ("M[Shadow]", "none"), ("M[ARSN]_Water_3normaltet", "none"), ("M[Fog]", "none"),
                 })
            Expect(Kind(mtd) == kind, $"{mtd} is {Kind(mtd)}, expected {kind}");

        // A model with a wall and a light shaft keeps the wall; one with only the shaft is left out as a whole.
        var (port, hostBnd) = VisualPort();
        var both = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN].mtd", ("g_DiffuseTexture", @"N:\x\m40_00_wall_a.tif"));
        both.Materials.Add(new FLVER2.Material("shaft", @"N:\FDP\data\Material\mtd\map\M[A]_Add_NoLight.mtd", 0) { GXIndex = 0 });
        var shaft = FLVER2.Read(both.Write()).Meshes[0];
        shaft.MaterialIndex = 1;
        both.Meshes.Add(shaft);
        var kept = ConvertOne(port, hostBnd, both);
        Expect(kept.Meshes.Count == 1 && kept.Meshes[0].MaterialIndex == 0, "the additive mesh is left out, the wall stays");
        Expect(port.LeftOut.TryGetValue("M[A]_Add_NoLight", out var n) && n == 1, "left-out meshes are counted by DS3 material");
        var only = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN]_Decal.mtd", ("g_DiffuseTexture", @"N:\x\m40_00_dirt_a.tif"));
        try
        {
            ConvertOne(port, hostBnd, only);
            Expect(false, "a model with nothing drawable must be left out");
        }
        catch (InvalidDataException)
        {
        }
        Expect(MapModelPort.Classify(MATBIN.Read(VisualMatbin("M[AMSN_V][Snow]"))) is null && MapModelPort.Classify(MATBIN.Read(VisualMatbin("M[AMSN_V]_Edge"))) == "edge",
            "only Elden Ring's plain one-layer shaders are templates");
    }

    private static byte[] VisualMatbin(string shader)
    {
        var matbin = new MATBIN { ShaderPath = $@"N:\GR\data\Material\sat\OutputData\SPX\{shader}.spx", SourcePath = @"N:\x\m.matxml" };
        matbin.Samplers.Add(new MATBIN.Sampler { Type = "X_snp_Texture2D_0_AlbedoMap_0", Path = "" });
        matbin.Samplers.Add(new MATBIN.Sampler { Type = "X_snp_Texture2D_1_NormalMap_0", Path = "" });
        return matbin.Write();
    }

    private static void UvRange()
    {
        var layout = new FLVER2.BufferLayout
        {
            new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.Normal),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.Tangent),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor, 1),
            new FLVER.LayoutMember(FLVER.LayoutType.Short2, FLVER.LayoutSemantic.UV),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.UV, 1),
        };
        FLVER2.Mesh Mesh(params float[] us)
        {
            var mesh = new FLVER2.Mesh();
            foreach (var u in us)
            {
                var v = new FLVER.Vertex();
                v.Positions.Add(Vector3.Zero);
                v.Normals.Add(Vector3.UnitY);
                v.UVs.Add(new Vector3(u, u / 2, 0));
                v.UVs.Add(new Vector3(9, 9, 0));
                mesh.Vertices.Add(v);
            }
            return mesh;
        }
        // DS3's multi-layer ground: float UVs in short units (measured -32715..32709 on m40_00_00_00_000051).
        var floats = Mesh(-32715, 0, 32709);
        MapModelPort.Conform(floats, layout, 0, [new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV)]);
        Expect(MathF.Abs(floats.Vertices[0].UVs[0].X - -32715f / 2048) < 1e-4f && MathF.Abs(floats.Vertices[2].UVs[0].X - 32709f / 2048) < 1e-4f, "float UVs in short units are divided by 2048");
        Expect(floats.Vertices.All(v => v.UVs.Count == 3 && v.UVs[1] == Vector3.Zero && v.Tangents.Count == 1 && v.NormalWs.SequenceEqual([0])), "three UVs for Short2 + UByte4Norm, the extra ones zero");
        // A far-off tiling range is moved back by whole tiles.
        var far = Mesh(100.25f, 101.5f, 102.75f);
        MapModelPort.Conform(far, layout, 0, [new FLVER.LayoutMember(FLVER.LayoutType.Short4, FLVER.LayoutSemantic.UV)]);
        var xs = far.Vertices.Select(v => v.UVs[0].X).ToList();
        Expect(xs.All(x => MathF.Abs(x) < 16) && MathF.Abs(xs[1] - xs[0] - 1.25f) < 1e-4f && MathF.Abs((100.25f - xs[0]) % 1) < 1e-4f, $"moved by whole tiles: {string.Join(",", xs)}");
        // In range: untouched.
        var near = Mesh(-7.5f, 0, 6.5f);
        MapModelPort.Conform(near, layout, 0, [new FLVER.LayoutMember(FLVER.LayoutType.Short4, FLVER.LayoutSemantic.UV)]);
        Expect(near.Vertices[0].UVs[0].X == -7.5f && near.Vertices[2].UVs[0].X == 6.5f, "UVs in range stay as they are");
    }

    private static void VisualFallbacks()
    {
        var none = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId, new MsbPort.Options { NoGroups = true }).Msb;
        Expect(none.Parts.MapPieces.Single().Unk1.DrawGroups.All(g => g == 0) && none.Parts.MapPieces.Single().Unk1.DisplayGroups.All(g => g == 0), "map-groups C: no groups");
        var (plain, _) = VisualPort(plain: true);
        Expect(plain.TemplateSummary == "opaque=m45_00_001" && plain.Complete(true), $"map-materials B: {plain.TemplateSummary}");
    }
}
