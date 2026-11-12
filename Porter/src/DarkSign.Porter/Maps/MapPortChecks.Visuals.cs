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
        yield return ("visuals: DS3's reflection proxies, shadow casters and off-layer pieces are left out and reported", HelperPieces);
        yield return ("visuals: DS3 overlays keep their vertex alpha fade; two-layer materials show the layer each triangle shows", Overlays);
        yield return ("visuals: a faded overlay triangle is left out only where a surface lies right behind it (map-overlays C cuts them all)", OverlayBacking);
        yield return ("visuals: DS3 terrain blends (2m/3m/4m) draw each triangle with the layer DS3 shows there", TerrainBlends);
    }

    /// <summary>
    /// A DS3 model as m40's m000080 has them: a brick overlay fading out by vertex alpha, an ash-over-brick two-layer
    /// mesh, a solid rock and grass (whose colour sways it).
    /// </summary>
    private static FLVER2 OverlayFlver()
    {
        var flver = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN]_l.mtd",
            ("g_DiffuseTexture", @"N:\x\m40_00_brick_001_a.tif"), ("g_BumpmapTexture", @"N:\x\m40_00_brick_001_n.tif"));
        flver.BufferLayouts[0] =
        [
            new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Normal),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Tangent),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Tangent, 1),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor, 1),
            new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV),
            new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV, 1),
        ];
        FLVER2.Material Material(string mtd, params (string Param, string Path, float Scale)[] textures)
        {
            var m = new FLVER2.Material("m", @"N:\FDP\data\Material\mtd\map\" + mtd + ".mtd", 0) { GXIndex = 0 };
            foreach (var (param, path, scale) in textures)
                m.Textures.Add(new FLVER2.Texture(param, path, new Vector2(scale), 0, 0, 0, 0, 0));
            return m;
        }
        flver.Materials.Add(Material("M[ARSN]_l_m", ("g_DiffuseTexture", @"N:\x\m40_00_brick_001_a.tif", 1), ("g_DiffuseTexture2", @"N:\x\m40_00_ash_332_a.tif", 0.5f),
            ("g_BumpmapTexture", @"N:\x\m40_00_brick_001_n.tif", 1), ("g_BumpmapTexture2", @"N:\x\m40_00_ash_332_n.tif", 0.5f)));
        flver.Materials.Add(Material("M[ARSN]_l", ("g_DiffuseTexture", @"N:\x\m40_00_rock_a.tif", 1)));
        flver.Materials.Add(Material("M[ARSN]_e_grass", ("g_DiffuseTexture", @"N:\x\m40_00_grass_a.tif", 1)));

        // A quad of two triangles per mesh: (0,1,2) and (1,3,2), each vertex with its own alpha.
        FLVER2.Mesh Quad(int material, float[] alpha, float y)
        {
            var mesh = new FLVER2.Mesh { MaterialIndex = material, NodeIndex = 0 };
            var corners = new[] { new Vector3(0, y, 0), new Vector3(10, y, 0), new Vector3(0, y, 10), new Vector3(10, y, 10) };
            for (int i = 0; i < 4; i++)
            {
                var v = new FLVER.Vertex();
                v.Positions.Add(corners[i]);
                v.Normals.Add(Vector3.UnitY);
                v.NormalWs.Add(127);
                v.Tangents.Add(new Vector4(1, 0, 0, 1));
                v.Tangents.Add(new Vector4(0, 0, 1, 1));
                v.Colors.Add(new FLVER.VertexColor(alpha[i], 1f, 0f, 1f));
                v.UVs.Add(new Vector3(corners[i].X / 10, corners[i].Z / 10, 0));
                v.UVs.Add(new Vector3(corners[i].X / 5 + 0.25f, corners[i].Z / 5, 0));
                mesh.Vertices.Add(v);
            }
            mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
            mesh.FaceSets.Add(new FLVER2.FaceSet(FLVER2.FaceSet.FSFlags.None, false, true, 0, [0, 1, 2, 1, 3, 2]));
            return mesh;
        }
        flver.Meshes.Clear();
        flver.Meshes.Add(Quad(0, [1f, 0.6f, 0.1f, 0.2f], 0));      // overlay: both triangles have a corner over half
        flver.Meshes.Add(Quad(0, [0.9f, 0.1f, 0.1f, 0.0f], 1));    // overlay: (0,1,2) has a corner at 0.9, (1,3,2) is cut
        flver.Meshes.Add(Quad(1, [1f, 1f, 0f, 0f], 2));            // two-layer: (0,1,2) mostly brick, (1,3,2) mostly ash
        flver.Meshes.Add(Quad(2, [1f, 1f, 1f, 1f], 0.9f));         // solid rock, 10 cm under the second overlay (what it fades into)
        flver.Meshes.Add(Quad(3, [0.3f, 0.3f, 0.3f, 0.3f], 4));    // grass: alpha is sway, untouched
        return FLVER2.Read(flver.Write());
    }

    private static void Overlays()
    {
        // DS3's side: what each mesh's alpha means.
        var ds3 = OverlayFlver();
        Expect(ds3.Meshes[0].Vertices[2].Colors.Single().A is > 0.09f and < 0.11f, "the test model keeps its vertex alpha through a write");
        Expect(ds3.Materials.Select(MapOverlays.KindOf).SequenceEqual([MapOverlays.Kind.Overlay, MapOverlays.Kind.TwoLayer, MapOverlays.Kind.Overlay, MapOverlays.Kind.None]),
            $"kinds {string.Join(",", ds3.Materials.Select(MapOverlays.KindOf))}");
        foreach (var (mtd, kind) in new[] { ("M[ARSN]_m", MapOverlays.Kind.TwoLayer), ("M[ARSN]_l_p_m", MapOverlays.Kind.TwoLayer), ("M[ARSN]_2m_l", MapOverlays.Kind.None),
                     ("MultiBlend\\M[ARSN]_4m_l", MapOverlays.Kind.None), ("M[ARSN]_l_e", MapOverlays.Kind.Overlay), ("M[ARSN]", MapOverlays.Kind.Overlay),
                     ("M[ARSN]_l_snow", MapOverlays.Kind.None), ("M[A]_e_grass", MapOverlays.Kind.None), ("M[ARSN]_Water_DL", MapOverlays.Kind.None) })
        {
            var m = new FLVER2.Material("m", @"N:\x\" + mtd + ".mtd", 0);
            m.Textures.Add(new FLVER2.Texture("g_DiffuseTexture2", "x.tif", Vector2.One, 0, 0, 0, 0, 0));
            Expect(MapOverlays.KindOf(m) == kind, $"{mtd} is {MapOverlays.KindOf(m)}, not {kind}");
        }

        var (port, hostBnd) = VisualPort();
        var flver = ConvertOne(port, hostBnd, OverlayFlver());
        Expect(flver.Meshes.Count == 6, $"the two-layer mesh is split in two: {flver.Meshes.Count} meshes");
        int Triangles(FLVER2.Mesh m) => m.FaceSets.Where(f => f.Flags == FLVER2.FaceSet.FSFlags.None).Sum(f => f.Triangulate(true).Count / 3);
        Expect(Triangles(flver.Meshes[0]) == 2, "an overlay triangle with one corner over half stays (DS3 shows part of it)");
        Expect(Triangles(flver.Meshes[1]) == 1 && flver.Meshes[1].Vertices.Count == 3, $"an overlay triangle under half everywhere is cut, its unused vertex dropped ({Triangles(flver.Meshes[1])}, {flver.Meshes[1].Vertices.Count})");
        Expect(flver.Meshes[0].Vertices.Select(v => v.Colors.Single()).Select(c => MathF.Round(c.A, 1)).SequenceEqual([1f, 0.6f, 0.1f, 0.2f])
               && flver.Meshes[0].Vertices.All(v => v.Colors.Single() is { R: 1, G: 0, B: 0 }), "an overlay keeps DS3's alpha, on vanilla's red");
        Expect(flver.Meshes.Skip(2).All(m => m.Vertices.All(v => v.Colors.Single() is { R: 1, G: 0, B: 0, A: 1 })), "everything else is solid");
        Expect(Triangles(flver.Meshes[2]) == 1 && Triangles(flver.Meshes[3]) == 1, "each layer of the two-layer mesh gets its triangle");
        var albedo = flver.Meshes.Take(4).Select(m => port.Materials.Single(x => x.Name == flver.Materials[m.MaterialIndex].Name).Albedo).ToList();
        Expect(albedo.SequenceEqual(["m40_00_brick_001_a", "m40_00_brick_001_a", "m40_00_brick_001_a", "m40_00_ash_332_a"]), $"albedos {string.Join(", ", albedo)}");
        var ash = port.Materials.Single(x => x.Albedo == "m40_00_ash_332_a");
        Expect(ash.Normal == "m40_00_ash_332_n" && ash.Scale == new Vector2(0.5f), $"the second layer brings its normal map and tiling ({ash.Normal}, {ash.Scale})");
        Expect(flver.Meshes[3].Vertices.Any(v => MathF.Abs(v.UVs[0].X - 0.25f) < 0.01f), "the second layer is drawn with DS3's second UV");
        Expect(Triangles(flver.Meshes[5]) == 2, "grass keeps every triangle");
        Expect(MapValidate.CheckFlverDraw(flver, port.TemplateLayouts()).ToList() is { Count: 0 }, "the validator accepts the converted model");
        Expect(port.Overlays.OverlayMeshes == 2 && port.Overlays.OverlayTrianglesCut == 1 && port.Overlays.TwoLayerMeshes == 1 && port.Overlays.SecondLayerTriangles == 1,
            $"counted: {port.Overlays.Summary}");

        Expect(port.Overlays.FadedKept == 0, $"nothing kept solid: {port.Overlays.Summary}");

        // Fallback map-overlays B: as up to 1.0.340.
        Expect(MapPorter.Options.Parse(["--solid-overlays"]).SolidOverlays && !new MapPorter.Options().SolidOverlays, "--solid-overlays is read");
        var (solid, bnd) = VisualPort();
        solid.Ds3Alpha = false;
        var old = ConvertOne(solid, bnd, OverlayFlver());
        Expect(old.Meshes.Count == 5 && old.Meshes.All(m => Triangles(m) == 2 && m.Vertices.All(v => v.Colors.Single().A == 1)), "--solid-overlays keeps every triangle solid");

        // Diagnostic --tint-materials: each family in its colour, everything else its real texture.
        Expect(MapPorter.Options.Parse(["--tint-materials"]).TintMaterials, "--tint-materials is read");
        var (tinted, tbnd) = VisualPort();
        tinted.TintMaterials = true;
        var t = ConvertOne(tinted, tbnd, OverlayFlver());
        var tints = t.Meshes.Select(m => tinted.Materials.Single(x => x.Name == t.Materials[m.MaterialIndex].Name).Albedo).ToList();
        Expect(tints.SequenceEqual(["DKS_tint_green", "DKS_tint_green", "DKS_tint_red", "DKS_tint_magenta", "m40_00_rock_a", "DKS_tint_yellow"]),
            $"tints {string.Join(", ", tints)}");
        Expect(MapModelPort.Tints.All(x => x.Name.StartsWith("DKS_tint_")) && ModelPort.SolidDxt1(0xF800).Length == ModelPort.FlatNormalDds().Length, "tint textures");
    }

    /// <summary>
    /// The 1.0.37x holes (Firelink Shrine's floor, the cliffs and the tree above Iudex Gundyr): a faded overlay triangle
    /// over nothing is kept, solid; one over a surface is left out; across a map, only when every placement has one.
    /// </summary>
    private static void OverlayBacking()
    {
        int Triangles(FLVER2.Mesh m) => m.FaceSets.Where(f => f.Flags == FLVER2.FaceSet.FSFlags.None).Sum(f => f.Triangulate(true).Count / 3);

        // Without the rock under it, the second overlay's faded triangle has nothing to fade into.
        var (port, hostBnd) = VisualPort();
        var bare = OverlayFlver();
        bare.Meshes.RemoveAt(3);
        var flver = ConvertOne(port, hostBnd, bare);
        Expect(Triangles(flver.Meshes[1]) == 2, $"a faded triangle over nothing stays ({Triangles(flver.Meshes[1])} triangles)");
        Expect(flver.Meshes[1].Vertices.Select(v => MathF.Round(v.Colors.Single().A, 1)).SequenceEqual([0.9f, 1f, 1f, 1f]),
            $"its corners are solid: {string.Join(",", flver.Meshes[1].Vertices.Select(v => v.Colors.Single().A))}");
        Expect(port.Overlays.OverlayTrianglesCut == 0 && port.Overlays.FadedKept == 1, $"counted: {port.Overlays.Summary}");

        // Too far under it (beyond Reach) is nothing either.
        var (far, farBnd) = VisualPort();
        var deep = OverlayFlver();
        foreach (var v in deep.Meshes[3].Vertices)
            v.Position = v.Position with { Y = 1 - MapOverlayBacking.Reach - 0.1f };
        ConvertOne(far, farBnd, deep);
        Expect(far.Overlays.FadedKept == 1, $"a surface {MapOverlayBacking.Reach + 0.1f} m under it doesn't count: {far.Overlays.Summary}");

        // Fallback map-overlays C: as 1.0.353 to 1.0.37x, every faded triangle cut.
        Expect(MapPorter.Options.Parse(["--cut-faded-overlays"]).CutFadedOverlays && !new MapPorter.Options().CutFadedOverlays, "--cut-faded-overlays is read");
        var (cut, cutBnd) = VisualPort();
        cut.CutFadedOverlays = true;
        var c = ConvertOne(cut, cutBnd, bare);
        Expect(Triangles(c.Meshes[1]) == 1 && cut.Overlays.OverlayTrianglesCut == 1, $"--cut-faded-overlays cuts it anyway: {cut.Overlays.Summary}");

        // Across a map: the overlay model (the second overlay alone) placed twice, the rock model under one of them.
        var overlay = OverlayFlver();
        overlay.Meshes.RemoveAt(4);
        overlay.Meshes.RemoveAt(3);
        overlay.Meshes.RemoveAt(2);
        overlay.Meshes.RemoveAt(0);
        var rock = OverlayFlver();
        rock.Meshes.RemoveRange(0, 3);
        rock.Meshes.RemoveAt(1);
        var at = (Vector3 p) => Matrix4x4.CreateTranslation(p);
        var placements = new Dictionary<string, Matrix4x4[]>
        {
            ["000080"] = [at(new(100, 5, 100)), at(new(300, 5, 100))],
            ["000081"] = [at(new(100, 5, 100))],
            ["000082"] = [at(new(100, 5, 100))],
        };
        var models = new Dictionary<string, FLVER2[]> { ["000080"] = [overlay], ["000081"] = [rock], ["000082"] = [overlay] };
        var map = MapOverlayBacking.Build(placements, n => models[n]);
        Expect(map.Faded == 2 && map.Backed == 1, $"one model placed twice is cut only where both placements are backed: {map.Summary}");
        var faded = MapOverlayBacking.FadedTriangles(overlay).Single();
        Expect(!map.For("000080")(faded.A, faded.B, faded.C, faded.Normal), "the model placed over nothing once keeps it");
        Expect(map.For("000082")(faded.A, faded.B, faded.C, faded.Normal), "the model placed over the rock leaves it out");
        Expect(!map.For("000099")(faded.A, faded.B, faded.C, faded.Normal), "a model the map doesn't place keeps it");
        var lone = new Dictionary<string, Matrix4x4[]> { ["000080"] = [at(new(100, 5, 100))], ["000081"] = [at(new(100, 5.2f, 100))] };
        var above = MapOverlayBacking.Build(lone, n => models[n]);
        Expect(above.Backed == 0, $"a surface 0.1 m in front of it (the side its normal faces) isn't behind it: {above.Summary}");
        var turned = new Dictionary<string, Matrix4x4[]>
        {
            ["000080"] = [Matrix4x4.CreateRotationX(MathF.PI) * at(new(100, 7, 110))],   // at y 6, facing down, the rock 10 cm above
            ["000081"] = [at(new(100, 5.2f, 100))],
        };
        Expect(MapOverlayBacking.Build(turned, n => models[n]).Backed == 1, "a placement upside down looks the other way");
    }

    /// <summary>
    /// A DS3 model with terrain blends as m40 has them (m000051, m900285): weights in a third UV member of four
    /// normalized bytes, every layer on UV 1 with its own tiling.
    /// </summary>
    private static FLVER2 BlendFlver()
    {
        var flver = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\MultiBlend\M[ARSN]_4m_l.mtd");
        flver.BufferLayouts[0] =
        [
            new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Normal),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Tangent),
            new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV),
            new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV, 1),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.UV, 2),
        ];
        FLVER2.Material Blend(string mtd, string prefix, params (string Texture, float Scale)[] layers)
        {
            var m = new FLVER2.Material("ground", @"N:\FDP\data\Material\mtd\map\MultiBlend\" + mtd + ".mtd", 0) { GXIndex = 0 };
            m.Textures.Add(new FLVER2.Texture("g_DOLTexture1", @"n:\x\40000000_m000051_gi_0000_00_dol_00.tif", Vector2.One, 0, 0, 0, 0, 0));
            for (int k = 0; k < layers.Length; k++)
            {
                var (tex, scale) = layers[k];
                m.Textures.Add(new FLVER2.Texture($"{prefix}_snp_Texture2D_{k + 1}_GSBlendMap_AlbedoMap_{k}", $@"N:\x\{tex}_a.tif", new Vector2(scale), 0, 0, 0, 0, 0));
                m.Textures.Add(new FLVER2.Texture($"{prefix}_snp_Texture2D_{k + 13}_GSBlendMap_NormalMap_{k}", $@"N:\x\{tex}_n.tif", new Vector2(scale), 0, 0, 0, 0, 0));
            }
            m.Textures.Add(new FLVER2.Texture($"{prefix}_snp_Texture2D_0_GSBlendMap_BlendEdgeTexture", @"N:\x\m40_00_mask_image_000.tif", Vector2.One, 0, 0, 0, 0, 0));
            return m;
        }
        flver.Materials.Clear();
        flver.Materials.Add(Blend("M[ARSN]_4m_l", "MultiBlend4_et1", ("m40_00_ground_001", 3), ("m40_00_ground_002", 1), ("m40_00_ground_003", 0.5f), ("m40_00_ground_004", 1)));
        flver.Materials.Add(Blend("M[ARSN]_3m_l", "MultiBlend3_et1", ("m40_00_wall_01", 1), ("m40_00_wall_02", 1), ("m40_00_wall_03", 1)));
        flver.Materials.Add(Blend("M[ARSN]_2m_et1_l", "MultiBlend2_et1", ("m40_00_wall_011", 1), ("m40_00_wall_013", 1)));

        // Four quads of two triangles: (0,1,2) and (1,3,2); each corner's weights (c0, c1, c2, c3).
        FLVER2.Mesh Quad(int material, float y, params float[][] weights)
        {
            var mesh = new FLVER2.Mesh { MaterialIndex = material, NodeIndex = 0 };
            var corners = new[] { new Vector3(0, y, 0), new Vector3(10, y, 0), new Vector3(0, y, 10), new Vector3(10, y, 10) };
            for (int i = 0; i < 4; i++)
            {
                var v = new FLVER.Vertex();
                v.Positions.Add(corners[i]);
                v.Normals.Add(Vector3.UnitY);
                v.NormalWs.Add(127);
                v.Tangents.Add(new Vector4(1, 0, 0, 1));
                v.Colors.Add(new FLVER.VertexColor(1f, 1f, 1f, 1f));
                v.UVs.Add(new Vector3(corners[i].X * 200, corners[i].Z * 200, 0));   // DS3's raw short units
                v.UVs.Add(new Vector3(corners[i].X * 100, corners[i].Z * 100, 0));   // lightmap
                v.UVs.Add(new Vector3(weights[i][0], weights[i][1], 0));
                v.UVs.Add(new Vector3(weights[i][2], weights[i][3], 0));
                mesh.Vertices.Add(v);
            }
            mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
            mesh.FaceSets.Add(new FLVER2.FaceSet(FLVER2.FaceSet.FSFlags.None, false, true, 0, [0, 1, 2, 1, 3, 2]));
            return mesh;
        }
        float[] W(float c0, float c1, float c2) => [c0, c1, c2, 0];
        flver.Meshes.Clear();
        // 4m ground as m000051 has it: (0,1,2) is layer 4 (c2 over half), (1,3,2) layer 2 (c0 over half, c2 not).
        flver.Meshes.Add(Quad(0, 0, W(0, 0, 0.74f), W(0.2f, 0, 0.51f), W(0.6f, 0, 0.9f), W(0.78f, 0.19f, 0)));
        // 4m ground all on layer 1: weights under half everywhere.
        flver.Meshes.Add(Quad(0, 1, W(0.1f, 0, 0.2f), W(0.3f, 0, 0), W(0, 0.2f, 0.4f), W(0, 0, 0)));
        // 3m wall as m900285: (1,1,0,0) corners show layer 3, (1,0,0,0) layer 2; the third byte is not read.
        flver.Meshes.Add(Quad(1, 2, W(1, 1, 1), W(1, 1, 1), W(1, 0, 1), W(1, 0, 1)));
        // 2m wall as m004001: only c0 counts, though c1 and c2 hold values.
        flver.Meshes.Add(Quad(2, 3, W(0.9f, 0, 0.8f), W(0.6f, 0, 0.1f), W(0.3f, 0.1f, 0.2f), W(0, 0.9f, 0.9f)));
        return FLVER2.Read(flver.Write());
    }

    private static void TerrainBlends()
    {
        var ds3 = BlendFlver();
        Expect(ds3.Meshes[0].Vertices[0].UVs.Count == 4 && MathF.Abs(ds3.Meshes[0].Vertices[0].UVs[3].X - 0.74f) < 0.01f,
            "SoulsFormats reads the four weight bytes as two UV entries (c0, c1 and c2, c3)");
        Expect(MapTerrainBlends.WeightEntry(ds3, ds3.Meshes[0]) == 2, "the weights are UV entry 2");
        foreach (var (mtd, n) in new[] { ("M[ARSN]_4m_l", 4), ("MultiBlend\\M[ARSN]_2m_et1_l", 2), ("M[ARSN]_3m", 3), ("M[ARSN]_l_m", 0), ("M[ARSN]_l", 0), ("M[ARSN]_m", 0),
                                         ("M[ARSN]_2m_pom_l", 2), ("M[A]_2m_NoLight_scroll", 0) })
            Expect(MapTerrainBlends.LayerCount(mtd) == n, $"{mtd} has {MapTerrainBlends.LayerCount(mtd)} layers, not {n}");

        var (port, hostBnd) = VisualPort();
        var flver = ConvertOne(port, hostBnd, BlendFlver());
        int Triangles(FLVER2.Mesh m) => m.FaceSets.Where(f => f.Flags == FLVER2.FaceSet.FSFlags.None).Sum(f => f.Triangulate(true).Count / 3);
        var shown = flver.Meshes.Select(m => (port.Materials.Single(x => x.Name == flver.Materials[m.MaterialIndex].Name), Triangles(m))).ToList();
        string Text((MapModelPort.NewMaterial M, int T) x) => $"{x.M.Albedo}x{x.T}";
        Expect(shown.Select(Text).SequenceEqual(["m40_00_ground_002_ax1", "m40_00_ground_004_ax1", "m40_00_ground_001_ax2", "m40_00_wall_02_ax1", "m40_00_wall_03_ax1", "m40_00_wall_011_ax1", "m40_00_wall_013_ax1"]),
            $"each triangle shows its layer: {string.Join(", ", shown.Select(Text))}");
        Expect(shown[0].Item1.Normal == "m40_00_ground_002_n" && shown[0].Item1.Scale == Vector2.One && shown[2].Item1.Scale == new Vector2(3),
            $"a layer brings its own normal map and tiling ({shown[0].Item1.Normal}, {shown[0].Item1.Scale}, {shown[2].Item1.Scale})");
        Expect(flver.Meshes.All(m => m.Vertices.All(v => v.Colors.Single() is { R: 1, G: 0, B: 0, A: 1 })), "every layer is solid");
        Expect(flver.Meshes[1].Vertices.Count == 3 && flver.Meshes[0].Vertices.Count == 3, "a split mesh keeps only the vertices its triangles use");
        Expect(MapValidate.CheckFlverDraw(flver, port.TemplateLayouts()).ToList() is { Count: 0 }, "the validator accepts the converted model");
        var b = port.Blends;
        Expect(b.Meshes == 4 && b.Triangles == 8 && b.LayerTriangles.SequenceEqual([3, 3, 1, 1]) && b.NoWeights == 0, $"counted: {b.Summary}");

        // A sky dome (m31/m40 m009000: "M[A]_2m_NoLight_scroll") has two cloud layers and blend bytes too, but stays
        // whole on its first layer: split, it drew blue shards across map 3's sky (1.0.366).
        var sky = BlendFlver();
        sky.Materials[2].MTD = @"N:\FDP\data\Material\mtd\map\M[A]_2m_NoLight_scroll.mtd";
        var skyStats = new MapTerrainBlends.Stats();
        MapTerrainBlends.Apply(sky, skyStats);
        Expect(skyStats.Meshes == 3 && sky.Meshes.Count(m => m.MaterialIndex == 2) == 1 && Triangles(sky.Meshes.Single(m => m.MaterialIndex == 2)) == 2,
            $"the sky dome stays one mesh on its first layer ({skyStats.Summary})");

        // Fallback map-blend-layers B: the first layer everywhere, as up to 1.0.353.
        Expect(MapPorter.Options.Parse(["--first-blend-layer"]).FirstBlendLayer && !new MapPorter.Options().FirstBlendLayer, "--first-blend-layer is read");
        var (first, bnd) = VisualPort();
        first.TerrainLayers = false;
        var old = ConvertOne(first, bnd, BlendFlver());
        Expect(old.Meshes.Count == 4 && old.Meshes.All(m => Triangles(m) == 2), "--first-blend-layer keeps every mesh whole");
        Expect(first.Materials.Select(m => m.Albedo).ToHashSet().SetEquals(["m40_00_ground_001_a", "m40_00_wall_01_a", "m40_00_wall_011_a"]),
            $"and shows the first layers: {string.Join(", ", first.Materials.Select(m => m.Albedo))}");

        // Diagnostic --tint-materials: a colour per layer.
        var (tinted, tbnd) = VisualPort();
        tinted.TintMaterials = true;
        var t = ConvertOne(tinted, tbnd, BlendFlver());
        var tints = t.Meshes.Select(m => tinted.Materials.Single(x => x.Name == t.Materials[m.MaterialIndex].Name).Albedo).ToList();
        Expect(tints.SequenceEqual(["DKS_tint_orange", "DKS_tint_purple", "DKS_tint_blue", "DKS_tint_orange", "DKS_tint_white", "DKS_tint_blue", "DKS_tint_orange"]),
            $"tints {string.Join(", ", tints)}");
    }

    private static void HelperPieces()
    {
        MSB3 Map()
        {
            var ds3 = SyntheticDs3();
            ds3.Parts.MapPieces[0].MapStudioLayer = 0xFFFFFFFF;
            void Add(string model, Action<MSB3.Part.MapPiece> set)
            {
                ds3.Models.MapPieces.Add(new MSB3.Model.MapPiece { Name = model });
                var piece = new MSB3.Part.MapPiece { Name = model + "_0000", ModelName = model, MapStudioLayer = 0xFFFFFFFF };
                set(piece);
                ds3.Parts.MapPieces.Add(piece);
            }
            // As m40 has them: a reflection proxy of a whole area, plus a shadow card and an off-layer piece.
            Add("m200000", p => { p.DrawOnlyReflectCam = true; p.DrawByReflectCam = true; });
            Add("m000700", p => p.IsShadowOnly = true);
            Add("m004435", p => p.MapStudioLayer = 2);
            Add("m000010", p => p.DrawByReflectCam = true);
            // More scenery, so the off-layer piece is the exception it is in a real map.
            for (int i = 0; i < 6; i++)
                Add($"m00030{i}", _ => { });
            return ds3;
        }

        var result = MsbPort.Convert(Map(), SyntheticHost(), Ds3Id, ErId);
        var names = result.Msb.Parts.MapPieces.Select(p => p.ModelName).ToList();
        Expect(names.Count == 8 && !names.Contains("m200000") && !names.Contains("m000700") && !names.Contains("m004435"),
            $"the reflection proxy, shadow card and off-layer piece are left out ({string.Join(", ", names)})");
        Expect(names.Contains("m000010"), "a piece that reflections draw too (water) stays");
        Expect(result.Msb.Models.MapPieces.All(m => m.Name is not ("m200000" or "m000700" or "m004435")), "no model is converted for a left-out piece");
        Expect(result.Notes.Count(n => n.StartsWith("helper pieces left out: 1 ")) == 3, $"each kind is reported ({string.Join(" | ", result.Notes)})");

        // Fallback map-helper-pieces B keeps them.
        Expect(MapPorter.Options.Parse(["--helper-pieces"]).HelperPieces && !new MapPorter.Options().HelperPieces, "--helper-pieces is read");
        var kept = MsbPort.Convert(Map(), SyntheticHost(), Ds3Id, ErId, new MsbPort.Options { HelperPieces = true });
        Expect(kept.Msb.Parts.MapPieces.Count == 11, "--helper-pieces keeps every piece");

        // A map whose pieces are mostly off bit 0 uses layers some other way: none is dropped for its layer.
        var odd = Map();
        foreach (var p in odd.Parts.MapPieces.Take(5))
            p.MapStudioLayer = 2;
        var oddResult = MsbPort.Convert(odd, SyntheticHost(), Ds3Id, ErId);
        Expect(oddResult.Msb.Parts.MapPieces.Count == 9, $"layers are ignored when most pieces are off the base layer ({oddResult.Msb.Parts.MapPieces.Count})");

        // The piece report names every piece, written or not, with its meshes.
        var (port, hostBnd) = VisualPort();
        var flver = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN]_l.mtd", ("g_DiffuseTexture", @"N:\x\m40_00_wall_a.tif"));
        var ds3Bnd = new BND4();
        ds3Bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\x\m40_00_00_00_000100.flver", flver.Write()));
        port.ConvertBinder(ds3Bnd, hostBnd, Ds3Id, ErId, "000100");
        var report = MapPorter.PieceReport(Map(), port, new HashSet<string>(), false).Split('\n');
        Expect(report.Length >= 12, "one line per piece");
        Expect(report.Any(l => l.StartsWith("m000100_0000\tm000100\twritten\t") && l.Contains("M[ARSN]_l [opaque]") && l.Contains("m40_00_wall_a")), "a written piece lists its meshes");
        Expect(report.Any(l => l.StartsWith("m200000_0000\tm200000\tleft out: " + MsbPort.ReflectionOnly)), "a left-out piece says why");
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
        // Every slot is the real port unless --piece-test names a test for it.
        var slots = MapSlots.All.Where(s => !s.Probe).ToList();
        var none = new MapPorter.Options();
        Expect(slots.All(s => MapPorter.PieceTestFor(s, none) == MapPorter.PieceTest.None), "every slot is the real port by default");
        var off = MapPorter.Options.Parse(["--no-piece-test"]);
        Expect(slots.All(s => MapPorter.PieceTestFor(s, off) == MapPorter.PieceTest.None), "--no-piece-test is still accepted");
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
            // As every vanilla map material has it: off (the material's meshes cast shadows).
            matbin.Params.Add(new MATBIN.Param { Name = MapModelPort.NoShadowParam, Type = MATBIN.ParamType.Bool, Value = false });
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
