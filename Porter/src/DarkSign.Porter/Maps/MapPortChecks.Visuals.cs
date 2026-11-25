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
        yield return ("visuals: DS3 materials sort into kinds; effect, decal and shadow meshes are left out", MaterialKinds);
        yield return ("visuals: DS3 water is drawn with Elden Ring's own pool water (M_MuddyWater) as it is, and left out when no water template was found", WaterSurface);
        yield return ("visuals: UVs that a short can't hold are rescaled or moved by whole tiles", UvRange);
        yield return ("visuals: pieces name a PartsDrawParam row, carry no masks", DrawSettings);
        yield return ("visuals: pieces whose models carry DS3's lower detail lists switch to them at vanilla's distances", LowerDetail);
        yield return ("visuals: test maps hold the host's own model, its own material, or the port's materials with vanilla textures", PieceTests);
        yield return ("visuals: plain templates take only the albedo-only shader (the piece tests' vanilla materials)", PlainTemplates);
        yield return ("visuals: DS3's reflection proxies, shadow casters and off-layer pieces are left out and reported", HelperPieces);
        yield return ("visuals: one-layer DS3 meshes are solid whatever their vertex alpha; two-layer materials show the layer each triangle shows", Overlays);
        yield return ("visuals: a FLVER holding only a shadow caster is left out of its binder, the drawable FLVER beside it kept", ShadowOnlyFlver);
        yield return ("visuals: DS3 terrain blends (2m/3m/4m) draw each triangle with the layer DS3 shows there", TerrainBlends);
        yield return ("visuals: a triangle between two DS3 layers is cut along the layer's edge, not given whole to one (map 3's jagged floor patches)", LayerContours);
        yield return ("visuals: DS3's half-float normals (terrain blends) face their surface ", HalfFloatNormals);
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

        // The default: one-layer meshes are solid whatever their alpha (DS3 draws Firelink's tunnel walls at alpha 0);
        // two-layer meshes are still split by layer.
        var (plain, plainBnd) = VisualPort();
        var drawn = ConvertOne(plain, plainBnd, OverlayFlver());
        int Tris(FLVER2.Mesh m) => m.FaceSets.Where(f => f.Flags == FLVER2.FaceSet.FSFlags.None).Sum(f => f.Triangulate(true).Count / 3);
        Expect(drawn.Meshes.Count == 6 && Tris(drawn.Meshes[0]) == 2 && Tris(drawn.Meshes[1]) == 2,
            $"by default no one-layer triangle is cut ({drawn.Meshes.Count} meshes, {Tris(drawn.Meshes[0])} and {Tris(drawn.Meshes[1])} triangles)");
        Expect(drawn.Meshes.Take(2).All(m => m.Vertices.All(v => v.Colors.Single().A == 1)), "by default one-layer meshes are written solid");
        Expect(Tris(drawn.Meshes[2]) >= 1 && Tris(drawn.Meshes[3]) >= 1, "by default the two-layer mesh is still split by layer");
        Expect(plain.Overlays.SolidOverlayMeshes == 2 && plain.Overlays.TwoLayerMeshes == 1,
            $"counted: {plain.Overlays.Summary}");
        // A mesh at alpha 0 everywhere (m004001's wall) stays whole.
        var wall = OverlayFlver();
        foreach (var v in wall.Meshes[0].Vertices)
            v.Colors[0] = new FLVER.VertexColor(0f, 1f, 0f, 1f);
        var (wallPort, wallBnd) = VisualPort();
        Expect(Tris(ConvertOne(wallPort, wallBnd, wall).Meshes[0]) == 2, "a one-layer mesh at alpha 0 everywhere is drawn whole");

        // The second layer of the two-layer mesh brings its own textures, tiling and UVs.
        var albedo = drawn.Meshes.Take(4).Select(m => plain.Materials.Single(x => x.Name == drawn.Materials[m.MaterialIndex].Name).Albedo).ToList();
        Expect(albedo.SequenceEqual(["m40_00_brick_001_a", "m40_00_brick_001_a", "m40_00_brick_001_a", "m40_00_ash_332_a"]), $"albedos {string.Join(", ", albedo)}");
        var ash = plain.Materials.Single(x => x.Albedo == "m40_00_ash_332_a");
        Expect(ash.Normal == "m40_00_ash_332_n" && ash.Scale == new Vector2(0.5f), $"the second layer brings its normal map and tiling ({ash.Normal}, {ash.Scale})");
        Expect(drawn.Meshes[3].Vertices.Any(v => MathF.Abs(v.UVs[0].X - 0.25f) < 0.01f), "the second layer is drawn with DS3's second UV");
        Expect(Tris(drawn.Meshes[5]) == 2, "grass keeps every triangle");
        Expect(MapValidate.CheckFlverDraw(drawn, plain.TemplateLayouts()).ToList() is { Count: 0 }, "the validator accepts the converted model");

        // Diagnostic --tint-materials: each family in its colour, everything else its real texture.
        Expect(MapPorter.Options.Parse(["--tint-materials"]).TintMaterials, "--tint-materials is read");
        var (tinted, tbnd) = VisualPort();
        tinted.TintMaterials = true;
        var t = ConvertOne(tinted, tbnd, OverlayFlver());
        var tints = t.Meshes.Select(m => tinted.Materials.Single(x => x.Name == t.Materials[m.MaterialIndex].Name).Albedo).ToList();
        Expect(tints.SequenceEqual(["m40_00_brick_001_a", "m40_00_brick_001_a", "DKS_tint_red", "DKS_tint_magenta", "m40_00_rock_a", "DKS_tint_yellow"]),
            $"tints {string.Join(", ", tints)}");
        Expect(MapModelPort.Tints.All(x => x.Name.StartsWith("DKS_tint_")) && ModelPort.SolidDxt1(0xF800).Length == ModelPort.FlatNormalDds().Length, "tint textures");
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
        Expect(shown.Select(x => x.Item1.Albedo).ToHashSet().SetEquals(["m40_00_ground_001_a", "m40_00_ground_002_a", "m40_00_ground_004_a", "m40_00_wall_02_a", "m40_00_wall_03_a", "m40_00_wall_011_a", "m40_00_wall_013_a"]),
            $"each layer DS3 shows is drawn: {string.Join(", ", shown.Select(Text))}");
        var ground2 = shown.First(x => x.Item1.Albedo == "m40_00_ground_002_a").Item1;
        var ground1 = shown.First(x => x.Item1.Albedo == "m40_00_ground_001_a").Item1;
        Expect(ground2.Normal == "m40_00_ground_002_n" && ground2.Scale == Vector2.One && ground1.Scale == new Vector2(3),
            $"a layer brings its own normal map and tiling ({ground2.Normal}, {ground2.Scale}, {ground1.Scale})");
        Expect(flver.Meshes.All(m => m.Vertices.All(v => v.Colors.Single() is { R: 1, G: 0, B: 0, A: 1 })), "every layer is solid");
        Expect(MapValidate.CheckFlverDraw(flver, port.TemplateLayouts()).ToList() is { Count: 0 }, "the validator accepts the converted model");
        var b = port.Blends;
        Expect(b.Meshes == 4 && b.Cut == 5 && b.NoWeights == 0, $"counted: {b.Summary}");

        // A sky dome (m31/m40 m009000: "M[A]_2m_NoLight_scroll") has two cloud layers and blend bytes too, but stays
        // whole on its first layer: split, it drew blue shards across map 3's sky (1.0.366).
        var sky = BlendFlver();
        sky.Materials[2].MTD = @"N:\FDP\data\Material\mtd\map\M[A]_2m_NoLight_scroll.mtd";
        var skyStats = new MapTerrainBlends.Stats();
        MapTerrainBlends.Apply(sky, skyStats);
        Expect(skyStats.Meshes == 3 && sky.Meshes.Count(m => m.MaterialIndex == 2) == 1 && Triangles(sky.Meshes.Single(m => m.MaterialIndex == 2)) == 2,
            $"the sky dome stays one mesh on its first layer ({skyStats.Summary})");
        var firstSky = new MapTerrainBlends.SkyStats();
        MapTerrainBlends.ApplySky(sky, firstSky);
        Expect(firstSky.Meshes == 1 && firstSky.SecondLayer == 0 && sky.Meshes.Count(m => m.MaterialIndex == 2) == 1,
            $"a dome whose second layer covers half of it or less keeps its first, as m31/m40 ({firstSky.Summary})");
        Expect(MapTerrainBlends.IsSkyBlend(@"N:\FDP\data\Material\mtd\map\MultiBlend\M[A]_2m_NoLight_scroll.mtd")
               && !MapTerrainBlends.IsSkyBlend("M[ARSN]_2m_l") && !MapTerrainBlends.IsSkyBlend("M[A]_NoLight"), "only two-layer unlit domes are sky blends");

        // The Painted World's dome (m45 m090000): clouds first (kumo_30), sky second (sky_01), c0 over half on most of it.
        var painted = BlendFlver();
        painted.Materials[2].MTD = @"N:\FDP\data\Material\mtd\map\MultiBlend\M[A]_2m_NoLight_scroll.mtd";
        foreach (var v in painted.Meshes[3].Vertices.Take(3))
            v.UVs[2] = new Vector3(0.64f, 0, 0);
        var secondUv = painted.Meshes[3].Vertices.Select(v => v.UVs[1]).ToList();
        var paintedStats = new MapTerrainBlends.SkyStats();
        MapTerrainBlends.ApplySky(painted, paintedStats);
        var domeMaterial = painted.Materials[painted.Meshes[3].MaterialIndex];
        Expect(paintedStats.SecondLayer == 1 && MapTerrainBlends.LayerOf(domeMaterial) == 1
               && MapModelPort.Ds3Texture(domeMaterial, "albedo")?.Path.Contains("m40_00_wall_013") == true
               && painted.Meshes[3].FaceSets[0].Indices.Count == 6,
            $"a dome mostly covered by its second layer shows that layer, whole ({paintedStats.Summary})");
        // ... on its own texture coordinates (DS3's UV 2: the Painted World's panorama), not the cloud tile's (1.0.52x).
        Expect(paintedStats.OwnUv == 1 && painted.Meshes[3].Vertices.Select(v => v.UVs[0]).SequenceEqual(secondUv),
            $"the second layer is drawn on DS3's UV 2 ({paintedStats.Summary})");

        // Diagnostic --tint-materials: a colour per layer.
        var (tinted, tbnd) = VisualPort();
        tinted.TintMaterials = true;
        var t = ConvertOne(tinted, tbnd, BlendFlver());
        var tints = t.Meshes.Select(m => tinted.Materials.Single(x => x.Name == t.Materials[m.MaterialIndex].Name).Albedo).ToList();
        Expect(tints.ToHashSet().SetEquals(["DKS_tint_orange", "DKS_tint_purple", "DKS_tint_blue", "DKS_tint_white"]),
            $"tints {string.Join(", ", tints)}");
    }

    private static void LayerContours()
    {
        static float Area(FLVER2.Mesh m, IEnumerable<int> tris)
        {
            var t = tris.ToList();
            float area = 0;
            for (int i = 0; i + 2 < t.Count; i += 3)
                area += Vector3.Cross(m.Vertices[t[i + 1]].Position - m.Vertices[t[i]].Position, m.Vertices[t[i + 2]].Position - m.Vertices[t[i]].Position).Length() / 2;
            return area;
        }
        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;

        // A 10 m quad of 2m wall: the second layer at full weight along z = 0, none along z = 10. DS3 changes layer along
        // z = 5; whole triangles (1.0.354 to 1.0.46x) put the edge on the quad's diagonal instead.
        var ds3 = BlendFlver();
        var mesh = ds3.Meshes[3];
        float[] c0 = [1, 1, 0, 0];
        for (int i = 0; i < 4; i++)
            mesh.Vertices[i].UVs[2] = new Vector3(c0[i], 0, 0);
        int entry = MapTerrainBlends.WeightEntry(ds3, mesh);
        var whole = MapLayerContours.Split(mesh, 2, (v, k) => MapTerrainBlends.Weight(v, entry, k - 1), _ => true, 0.5f, false, out int wholeCut);
        Expect(wholeCut == 0 && mesh.Vertices.Count == 4 && whole[1][0].SequenceEqual([0, 1, 2]) && whole[0][0].SequenceEqual([1, 3, 2]),
            "without contours each triangle goes whole to the layer its corners average to");
        var split = MapLayerContours.Split(mesh, 2, (v, k) => MapTerrainBlends.Weight(v, entry, k - 1), _ => true, 0.5f, true, out int cut);
        Expect(cut == 2 && mesh.Vertices.Count == 7, $"both triangles are cut, at three shared points on their edges ({cut} cut, {mesh.Vertices.Count} vertices)");
        Expect(split[1][0].All(i => mesh.Vertices[i].Position.Z <= 5.001f) && split[0][0].All(i => mesh.Vertices[i].Position.Z >= 4.999f),
            "the second layer lies on its side of z = 5, the first on the other");
        Expect(Near(Area(mesh, split[1][0]), 50) && Near(Area(mesh, split[0][0]), 50), $"each layer covers half the quad ({Area(mesh, split[1][0])}, {Area(mesh, split[0][0])})");
        var added = mesh.Vertices.Skip(4).ToList();
        Expect(added.All(v => Near(v.Position.Z, 5) && Near(MapTerrainBlends.Weight(v, entry, 0), 0.5f) && Near(v.Normal.Length(), 1) && Near(v.UVs[0].Y, 1000)),
            "a cut point lies on the boundary and takes every value from between its edge's two ends");

        // No gaps: every edge inside the quad is shared by exactly two triangles, in opposite directions, whatever
        // layers they went to (4m ground, three weights in play).
        var ground = BlendFlver().Meshes[0];
        var parts = MapLayerContours.Split(ground, 4, (v, k) => MapTerrainBlends.Weight(v, entry, k - 1), _ => true, 0.5f, true, out _);
        var all = parts.SelectMany(part => part[0]).ToList();
        Expect(Near(Area(ground, all), 100), $"the layers together cover the quad once ({Area(ground, all)} of 100 m2)");
        Expect(parts.Count(part => part[0].Count > 0) == 3, $"the quad shows three layers ({string.Join(", ", parts.Select(part => part[0].Count / 3))} triangles)");
        var edges = new Dictionary<(int, int), int>();
        for (int i = 0; i + 2 < all.Count; i += 3)
            for (int j = 0; j < 3; j++)
            {
                var e = (all[i + j], all[i + (j + 1) % 3]);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        bool Border((int A, int B) e)
        {
            Vector3 a = ground.Vertices[e.A].Position, b = ground.Vertices[e.B].Position;
            return (a.X == b.X && a.X is 0 or 10) || (a.Z == b.Z && a.Z is 0 or 10);
        }
        Expect(edges.All(e => e.Value == 1 && (Border(e.Key) || edges.ContainsKey((e.Key.Item2, e.Key.Item1)))),
            "every inner edge has a triangle on each side: no gap and no vertex in the middle of a neighbour's side");
        Expect(all.Chunk(3).All(t => Vector3.Cross(ground.Vertices[t[1]].Position - ground.Vertices[t[0]].Position, ground.Vertices[t[2]].Position - ground.Vertices[t[0]].Position).Y
                                     * Vector3.Cross(ground.Vertices[1].Position - ground.Vertices[0].Position, ground.Vertices[2].Position - ground.Vertices[0].Position).Y > 0),
            "every cut triangle faces the way DS3's did");

        // Through the whole conversion: the cut model is one Elden Ring can draw, and each layer has its own material.
        var (port, hostBnd) = VisualPort();
        var flver = ConvertOne(port, hostBnd, BlendFlver());
        Expect(MapValidate.CheckFlverDraw(flver, port.TemplateLayouts()).ToList() is { Count: 0 }, "the validator accepts the cut model");
        Expect(port.Blends.Cut == 5 && port.Blends.Summary.Contains("5 DS3 triangles cut along a layer's edge"), $"counted: {port.Blends.Summary}");
        Expect(flver.Meshes.All(m => m.Vertices.All(v => v.Colors.Single() is { R: 1, G: 0, B: 0, A: 1 })), "every layer is solid");

        // Two-layer materials are cut the same way, by vertex alpha.
        var (two, twoBnd) = VisualPort();
        ConvertOne(two, twoBnd, OverlayFlver());
        Expect(two.Overlays.TwoLayerTriangles > 2, $"a two-layer triangle between the layers is cut ({two.Overlays.Summary})");

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
        var report = MapPorter.PieceReport(Map(), port, new HashSet<string>()).Split('\n');
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
        var result = MsbPort.Convert(SyntheticDs3(), host, Ds3Id, ErId);
        var msb = MSBE.Read(result.Msb.Write());
        Expect(msb.Parts.MapPieces.All(p => p.isUsePartsDrawParamID == 1 && p.PartsDrawParamID == MapDrawParams.FirstRow), "every piece names the row");
        Expect(msb.Parts.MapPieces.All(p => MapValidate.CheckPieceDraw(p) is null), "the validator accepts them");
        Expect(MapValidate.CheckPieceDraw(host.Parts.MapPieces[0]) is not null, "the validator rejects a piece that names no row");

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
        Expect(param[MapDrawParams.LodRow] is not null, "an LOD twin beside it");
    }

    private static void LowerDetail()
    {
        // The LOD row: the first row's settings, switching at vanilla's 30 / 60 m (Stormveil's rows 2006/2304/2404).
        var def = new PARAMDEF { ParamType = "PARTS_DRAW_PARAM_ST", DataVersion = 1 };
        foreach (var name in new[] { "lv01_BorderDist", "lv01_PlayDist", "lv12_BorderDist", "lv12_PlayDist", "lv23_BorderDist", "drawDist" })
            def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.f32, name));
        var param = new PARAM { ParamType = def.ParamType, Rows = [] };
        param.ApplyParamdef(def);
        param.Rows.Add(new PARAM.Row(MapDrawParams.TemplateRow, "", def));
        MapDrawParams.AddRow(param);
        float Cell(int id, string name) => (float)param[id]!.Cells.Single(c => c.Def.InternalName == name).Value;
        Expect(Cell(MapDrawParams.FirstRow, "lv01_BorderDist") > 9000f, "the first row still never switches");
        Expect(Cell(MapDrawParams.LodRow, "lv01_BorderDist") == 30f && Cell(MapDrawParams.LodRow, "lv12_BorderDist") == 60f
               && Cell(MapDrawParams.LodRow, "lv01_PlayDist") == 1f && Cell(MapDrawParams.LodRow, "lv12_PlayDist") == 2f
               && Cell(MapDrawParams.LodRow, "lv23_BorderDist") > 9000f, "LOD row switches at 30 and 60 m, never to a third level");
        Expect(Cell(MapDrawParams.LodRow, "drawDist") > 9000f, "LOD row pieces still draw at any distance");
        Expect(MapDrawParams.FirstRow + MapDrawParams.LodOffset >= ObjectPort.FirstDrawRow + ObjectPort.DrawBySize.Length
               && MapDrawParams.FirstRow + MapDrawParams.LodOffset + 9 <= MapDrawParams.LastRow, "LOD twins don't collide with the objects' rows");
        // Every map piece row of ours gets a twin (rows by size, PR #187), replacing an earlier run's.
        param.Rows.Add(new PARAM.Row(MapDrawParams.FirstRow + 3, "by size", def));
        param.Rows.Add(new PARAM.Row(MapDrawParams.FirstRow + MapDrawParams.LodOffset + 9, "stale twin", def));
        MapDrawParams.AddLodTwins(param, MapDrawParams.Lod.Vanilla);
        Expect(Cell(MapDrawParams.FirstRow + 3 + MapDrawParams.LodOffset, "lv01_BorderDist") == 30f && param[MapDrawParams.FirstRow + 9 + MapDrawParams.LodOffset] is null
               && param.Rows.Count(r => r.ID == MapDrawParams.LodRow) == 1, "a twin per piece row, stale twins gone");

        // Completing a model: a mesh that lacks a level gets a copy of its next more detailed list.
        static FLVER2.Mesh Mesh(params (FLVER2.FaceSet.FSFlags Flags, int[] Indices)[] sets)
        {
            var mesh = new FLVER2.Mesh();
            for (int i = 0; i < 6; i++)
                mesh.Vertices.Add(new FLVER.Vertex());
            foreach (var (flags, indices) in sets)
                mesh.FaceSets.Add(new FLVER2.FaceSet(flags, false, true, 0, [.. indices]));
            return mesh;
        }
        const FLVER2.FaceSet.FSFlags None = FLVER2.FaceSet.FSFlags.None, L1 = FLVER2.FaceSet.FSFlags.LodLevel1,
            L2 = FLVER2.FaceSet.FSFlags.LodLevel2, Blur = FLVER2.FaceSet.FSFlags.MotionBlur;
        var flver = new FLVER2();
        flver.Meshes.Add(Mesh((None, [0, 1, 2, 3, 4, 5]), (L1, [0, 1, 2]), (L2, [0, 1, 2]), (Blur, [0, 1, 2, 3, 4, 5]), (Blur | L1, [0, 1, 2]), (Blur | L2, [0, 1, 2])));
        flver.Meshes.Add(Mesh((None, [0, 1, 2, 3, 4, 5]), (L1, [3, 4, 5])));
        flver.Meshes.Add(Mesh((None, [0, 1, 2])));
        var r = MapLod.Complete(flver);
        Expect(r.HasLod && r.Copies == 3, $"three lists copied ({r.Copies})");
        Expect(flver.Meshes[0].FaceSets.Count == 6, "a complete mesh is left as it is");
        Expect(flver.Meshes[1].FaceSets.Single(f => f.Flags == L2).Indices.SequenceEqual([3, 4, 5]), "a missing level 2 copies level 1");
        Expect(flver.Meshes[2].FaceSets.Single(f => f.Flags == L1).Indices.SequenceEqual([0, 1, 2])
               && flver.Meshes[2].FaceSets.Any(f => f.Flags == L2) && !flver.Meshes[2].FaceSets.Any(f => (f.Flags & Blur) != 0),
            "a mesh with only full detail gets both levels, and no motion blur lists it never had");
        Expect(r.FullTriangles == 5 && r.Lod1Triangles == 3 && r.Lod2Triangles == 3, $"triangle counts {r.FullTriangles}/{r.Lod1Triangles}/{r.Lod2Triangles}");
        var plain = new FLVER2();
        plain.Meshes.Add(Mesh((None, [0, 1, 2])));
        Expect(!MapLod.Complete(plain).HasLod && plain.Meshes[0].FaceSets.Count == 1, "a model without lower detail lists is left alone (it never switches)");
    }

    private static void PieceTests()
    {
        // Every slot is the real port unless --piece-test names a test for it.
        var slots = MapSlots.All.Where(s => !s.Probe).ToList();
        var none = new MapPorter.Options();
        Expect(slots.All(s => MapPorter.PieceTestFor(s, none) == MapPorter.PieceTest.None), "every slot is the real port by default");
        var asked = MapPorter.Options.Parse(["--piece-test", $"{slots[0].Er}=vanilla-material"]);
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

    private static void WaterSurface()
    {
        var pool = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN]_Water_3normaltet.mtd", ("g_DiffuseTexture", @"N:\x\m40_00_water_a.tif"));
        var wall = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN].mtd", ("g_DiffuseTexture", @"N:\x\m40_00_wall_a.tif"));
        var both = FLVER2.Read(wall.Write());
        both.Materials.Add(pool.Materials[0]);
        var poolMesh = FLVER2.Read(pool.Write()).Meshes[0];
        poolMesh.MaterialIndex = 1;
        both.Meshes.Add(poolMesh);

        var (dry, dryHost) = VisualPort();
        Expect(!dry.HasWater, "no water template among Stormveil-like models");
        var kept = ConvertOne(dry, dryHost, FLVER2.Read(both.Write()));
        Expect(kept.Meshes.Count == 1 && dry.LeftOut.ContainsKey("M[ARSN]_Water_3normaltet"), "without a water template the pool is left out, the wall stays");

        var (port, hostBnd) = VisualPort(water: true);
        Expect(port.HasWater && port.TemplateSummary.Contains("water=m22_00_110"), $"Volcano Manor's pool water is the template ({port.TemplateSummary})");
        var flver = ConvertOne(port, hostBnd, FLVER2.Read(both.Write()));
        Expect(flver.Meshes.Count == 2, $"wall and pool kept ({flver.Meshes.Count} meshes)");
        var water = flver.Materials.Single(m => m.MTD.Contains("m22_00_110"));
        Expect(water.MTD.EndsWith(@"\m22_00_110.matxml", StringComparison.Ordinal) && water.Textures.All(t => t.Path == ""), $"the vanilla material as it is ({water.MTD})");
        var waterMesh = flver.Meshes.Single(m => flver.Materials[m.MaterialIndex] == water);
        var kinds = MapModelPort.Kinds.Where(k => port.TemplateSummary.Contains(k + "=")).ToList();
        // (Identical GX lists are written once, so the GX index read back can be another kind's: the layout tells.)
        Expect(waterMesh.VertexBuffers[0].LayoutIndex == kinds.IndexOf("water"), $"its own vertex layout ({waterMesh.VertexBuffers[0].LayoutIndex}, kinds {string.Join(",", kinds)})");
        Expect(!port.Materials.Any(m => m.Template.Kind == "water"), "no MATBIN of the port's own for water");
        Expect(port.Textures.All(t => !t.Contains("water")), "the DS3 water texture isn't asked for");
    }

    private static (MapModelPort Port, BND4 HostBnd) VisualPort(bool plain = false, bool water = false)
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
        if (water)
            Add("m22_00_110", "M[MuddyWater]", "M_MuddyWater__snp_Texture2D_0_NormalMap_0", "M_MuddyWater__snp_Texture2D_2_NormalMap_0");
        var hostBnd = new BND4();
        hostBnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 200, @"N:\GR\data\Model\map\m45_00_00_00\m45_00_00_00_001000\sib\m45_00_00_00_001000.flver", flvers[0].Write()));
        return (MapModelPort.Create(flvers, matbins, flvers[0], plain), hostBnd);
    }

    /// <summary>
    /// A DS3 binder with a second FLVER that holds only a shadow caster (Undead Settlement's trees, cages and hanging
    /// corpses): the shadow FLVER is left out, the drawable one is kept. Up to 1.0.383 the whole binder was dropped.
    /// </summary>
    private static void ShadowOnlyFlver()
    {
        var (port, hostBnd) = VisualPort();
        var tree = OverlayFlver();
        var shadow = OverlayFlver();
        shadow.Materials[0].MTD = @"N:\FDP\data\Material\mtd\map\M[Shadow].mtd";
        shadow.Meshes.RemoveRange(1, shadow.Meshes.Count - 1);
        var bnd = new BND4();
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\FDP\data\Model\map\m31_00_00_00\sib\m31_00_00_00_002050.flver", tree.Write()));
        bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 1, @"N:\FDP\data\Model\map\m31_00_00_00\sib\m31_00_00_00_002050_S.flver", shadow.Write()));
        var written = BND4.Read(port.ConvertBinder(bnd, hostBnd, Ds3Id, ErId, "002050").Write());
        Expect(written.Files.Count == 1 && FLVER2.Read(written.Files[0].Bytes).Meshes.Count > 0 && port.EmptyFlvers == 1,
            $"the tree is kept, its shadow FLVER left out ({written.Files.Count} files, {port.EmptyFlvers} left out)");

        var only = new BND4();
        only.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\FDP\data\Model\map\m31_00_00_00\sib\m31_00_00_00_002051.flver", shadow.Write()));
        bool threw = false;
        try { port.ConvertBinder(only, hostBnd, Ds3Id, ErId, "002051"); }
        catch (InvalidDataException) { threw = true; }
        Expect(threw, "a binder with nothing drawable is still left out");
    }

    /// <summary>
    /// DS3 terrain blends store normals as four half floats (layout type Half4), which SoulsFormats reads as unsigned
    /// shorts: ground facing up came out facing down and drew far too dark (High Wall, 2026-11-19).
    /// </summary>
    private static void HalfFloatNormals()
    {
        // A DS3 file: the layout DS3's MultiBlend meshes use (m40 m000051/m000080), each corner's normal as DS3 stores it.
        FLVER2 Ds3(Vector3 normal)
        {
            var flver = BlendFlver();
            var layout = flver.BufferLayouts[0];
            int at = layout.FindIndex(m => m.Semantic == FLVER.LayoutSemantic.Normal);
            layout[at] = new FLVER.LayoutMember(FLVER.LayoutType.Half4, FLVER.LayoutSemantic.Normal);
            var stored = new Vector3(Ds3HalfNormals.AsUShortNorm(normal.X), Ds3HalfNormals.AsUShortNorm(normal.Y), Ds3HalfNormals.AsUShortNorm(normal.Z));
            foreach (var v in flver.Meshes.SelectMany(m => m.Vertices))
                v.Normals[0] = stored;
            return FLVER2.Read(flver.Write());
        }
        var up = Vector3.Normalize(new Vector3(0.3f, 0.9f, -0.2f));
        var read = Ds3(up);
        var asRead = read.Meshes[0].Vertices[0].Normal;
        Expect(Ds3HalfNormals.HasHalfNormals(read, read.Meshes[0]) && asRead.Y < 0,
            $"SoulsFormats reads a half-float normal facing up as one facing down ({asRead})");
        Expect(MathF.Abs(Ds3HalfNormals.AsHalf(Ds3HalfNormals.AsUShortNorm(0.4375f)) - 0.4375f) < 1e-6f, "a half float survives the trip through SoulsFormats' reading");

        var (port, hostBnd) = VisualPort();
        var flver = ConvertOne(port, hostBnd, Ds3(up));
        var normals = flver.Meshes.SelectMany(m => m.Vertices).Select(v => v.Normal).ToList();
        Expect(normals.Count > 0 && normals.All(n => Vector3.Distance(n, up) < 0.03f), $"every converted normal faces the surface ({normals[0]}, wanted {up})");
        var s = port.HalfNormalStats;
        Expect(s.Meshes == 4 && s.Vertices == 16 && s.Unreadable == 0, $"counted: {s.Summary}");

        // A one-byte normal (every other DS3 map mesh) is left as it was.
        var (plainPort, plainBnd) = VisualPort();
        var plain = ConvertOne(plainPort, plainBnd, BlendFlver());
        Expect(plain.Meshes.SelectMany(m => m.Vertices).All(v => Vector3.Distance(v.Normal, Vector3.UnitY) < 0.03f) && plainPort.HalfNormalStats.Meshes == 0,
            "a one-byte normal is untouched");

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
        Expect(port.TemplateSummary == "opaque=m10_00_004, edge=m10_00_207, alpha=m10_00_218, nolight=m10_00_003, nolight-blend=m10_00_003, nolight-edge=m10_00_003, nolight-add=m10_00_003, nolight-opacity=m10_00_003"
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
                     ("M[ARSN]_e_grass", "edge"), ("M[ARSN]_Alp", "edge"), ("M[A]_NoLight", "nolight"), ("M[A]_2m_NoLight_scroll", "nolight"), ("M[A]_NoLight_e", "nolight-edge"),
                     ("M[A]_al_NoLight", "nolight-blend"), ("M[A]_al_m_NoLight_Opacity", "nolight-opacity"), ("M[A]_add", "nolight-add"), ("M[ARSN]_al", "alpha"), ("M[A]_Add_NoLight", "nolight-add"),
                     ("M[ARSN]_Decal", "none"), ("M[Shadow]", "none"), ("M[ARSN]_Water_3normaltet", "water"), ("M[ARSN]_Water_DL", "water"), ("M[A]_Waterfall_add", "none"), ("M[Fog]", "none"),
                 })
            Expect(Kind(mtd) == kind, $"{mtd} is {Kind(mtd)}, expected {kind}");

        // A model with a wall and a fog card keeps the wall; one with only the fog card is left out as a whole.
        var (port, hostBnd) = VisualPort();
        var both = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN].mtd", ("g_DiffuseTexture", @"N:\x\m40_00_wall_a.tif"));
        both.Materials.Add(new FLVER2.Material("fog", @"N:\FDP\data\Material\mtd\map\M[Fog].mtd", 0) { GXIndex = 0 });
        var shaft = FLVER2.Read(both.Write()).Meshes[0];
        shaft.MaterialIndex = 1;
        both.Meshes.Add(shaft);
        var kept = ConvertOne(port, hostBnd, both);
        Expect(kept.Meshes.Count == 1 && kept.Meshes[0].MaterialIndex == 0, "the fog mesh is left out, the wall stays");
        Expect(port.LeftOut.TryGetValue("M[Fog]", out var n) && n == 1, "left-out meshes are counted by DS3 material");

        // DS3's see-through unlit layers (M[A]_al_NoLight: High Wall's sky_02 rings, Firelink's horizon cloud band and
        // far mountain ring) are drawn unlit and blended, with DS3's vertex fade baked into a texture of their own
        // (MapSkyFades). Nothing is cut: a layer DS3 shows at 20% is drawn at 20%, not left out or drawn solid.
        var (cloudPort, cloudBnd) = VisualPort();
        var cloud = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[A]_al_NoLight.mtd", ("g_DiffuseTexture", @"N:\x\m30_00_sky_02_a.tif"));
        cloud.Materials[0].Textures[0].TilingScale = new Vector2(2, 1);
        cloud.BufferLayouts[0].Insert(1, new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor));
        float[] fades = [0.8f, 0.3f, 0.3f];
        for (int i = 0; i < 3; i++)
            cloud.Meshes[0].Vertices[i].Colors.Add(new FLVER.VertexColor(fades[i], 1f, 0f, 1f));
        var faint = FLVER2.Read(cloud.Write()).Meshes[0];
        foreach (var v in faint.Vertices)
            v.Colors[0] = new FLVER.VertexColor(0.2f, 1f, 0f, 1f);
        cloud.Meshes.Add(faint);
        var gone = FLVER2.Read(cloud.Write()).Meshes[0];
        foreach (var v in gone.Vertices)
            v.Colors[0] = new FLVER.VertexColor(0f, 1f, 0f, 1f);
        cloud.Meshes.Add(gone);
        var drawn = ConvertOne(cloudPort, cloudBnd, FLVER2.Read(cloud.Write()));
        Expect(drawn.Meshes.Count == 2 && drawn.Meshes.All(m => m.Vertices.Count == 3 && m.Vertices.All(v => v.Colors.Single().A == 1f)),
            $"both visible layers drawn whole, their fade in the texture, not the vertex colour: {string.Join("; ", drawn.Meshes.Select(m => m.Vertices.Count + " " + string.Join(",", m.Vertices.Select(v => v.Colors.FirstOrDefault().A))))}");
        Expect(cloudPort.Materials.All(m => m.Template.Kind == "nolight-blend" && m.Albedo.StartsWith("m30_00_sky_02_a" + MapSkyFades.Suffix) && m.Scale == Vector2.One)
               && cloudPort.Materials.Count() == 2,
            $"drawn on the unlit template, blended, each on its own baked texture at 1 x 1: {string.Join(", ", cloudPort.Materials.Select(m => m.Template.Kind + " " + m.Albedo + " " + m.Scale))}");
        Expect(cloudPort.SkyFades is { Meshes: 3, Baked: 2, Empty: 1, TooWide: 0 } && cloudPort.UnlitBlends.TrianglesCut == 0,
            $"counted: {cloudPort.SkyFades.Summary} / {cloudPort.UnlitBlends.Summary}");
        Expect(cloudPort.LeftOut.Count == 0, $"nothing left out: {string.Join(", ", cloudPort.LeftOut.Keys)}");
        var strong = MapSkyFades.Recipes[cloudPort.Materials.Select(m => m.Albedo).First(a => MapSkyFades.Recipes[a].Fade.Max() > 100)];
        var weak = MapSkyFades.Recipes[cloudPort.Materials.Select(m => m.Albedo).First(a => MapSkyFades.Recipes[a].Fade.Max() < 100)];
        Expect(Math.Abs(strong.Fade.Max() - 204) <= 3 && Math.Abs(weak.Fade.Max() - 51) <= 2 && weak.Fade.Count(f => f == 51) > MapSkyFades.FadeSize * MapSkyFades.FadeSize / 2 && strong.AlbedoScale == new Vector2(2, 1),
            $"the fade rasterised from DS3's vertex alpha (0.8 -> 204; 0.2 -> 51 over the triangle and its edge), DS3's tiling kept for the bake: {strong.Fade.Max()} {weak.Fade.Min()}..{weak.Fade.Max()} {strong.AlbedoScale}");
        // The bake itself: RGB from the albedo at DS3's tiling, alpha = albedo alpha x fade.
        {
            const int sz = 8;
            var px = new byte[sz * sz * 4];
            for (int i = 0; i < sz * sz; i++)
            {
                px[i * 4] = (byte)(i % sz < sz / 2 ? 200 : 40); px[i * 4 + 1] = 120; px[i * 4 + 2] = 60; px[i * 4 + 3] = 255;
            }
            var albedo = new TPF.Texture("m30_00_sky_02_a", 0, 0, WeaponTextures.Encode(px, sz, sz, BCnEncoder.Shared.CompressionFormat.Bc1, 72), TPF.TPFPlatform.PC);
            var bakedTex = MapSkyFades.Build("baked", weak, t => t == "m30_00_sky_02_a" ? albedo : null);
            Expect(bakedTex is not null && MapSkyFades.IsSrgb(bakedTex.Bytes) && BitConverter.ToInt32(bakedTex.Bytes, 128) == 99 && bakedTex.Format == 102,
                $"an sRGB albedo bakes into an sRGB BC7 (DXGI 99) under Elden Ring's alpha format byte 102: {(bakedTex is null ? "none" : $"{BitConverter.ToInt32(bakedTex.Bytes, 128)} / {bakedTex.Format}")}");
            var (bp, bw, bh) = MapSkyFades.Decode(bakedTex!.Bytes);
            Expect(bp is not null && bw == 16 && bh == 8, $"the bake covers the UV range at the albedo's density x tiling (16 x 8), got {bw} x {bh}");
            if (bp is not null)
            {
                Expect(bp.All(c => c.a <= 55) && bp.Count(c => Math.Abs(c.a - 51) <= 4) >= bp.Length / 2, $"alpha = 255 x 0.2 over the triangle, 0 beyond it: {string.Join(",", bp.Select(c => c.a).Distinct().Take(6))}");
                Expect(bp[0].r > 170 && bp[5].r < 70 && bp[8].r > 170 && bp[13].r < 70, $"the albedo repeats twice across (DS3's 2 x 1 tiling): {bp[0].r} {bp[5].r} {bp[8].r} {bp[13].r}");
            }
            Expect(MapSkyFades.Build("x", weak, _ => null) is null, "no albedo, no bake (the texture writer puts a see-through stand-in)");
        }

        // DS3's soft cloud cards (M[A]_al_m_NoLight_Opacity, m45 m091020): float UVs in short units (1..2047), the
        // fade in UV channel 3, both opacity masks multiplied in; and DS3's additive sun glow (M[A]_add, m30_00_sky_06_a).
        {
            var (cardsPort, cardsBnd) = VisualPort();
            var cards = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[A]_al_m_NoLight_Opacity.mtd",
                ("Fujir_Nolight_OpacityBlend_snp_Texture2D_1_GSBlendMap_AlbedoMap_0", @"N:\x\m45_00_kumo_30_a.tif"),
                ("Fujir_Nolight_OpacityBlend_snp_Texture2D_4_GSBlendMap_OpacityTexture1", @"N:\x\m45_00_kumo_33_m.tif"),
                ("Fujir_Nolight_OpacityBlend_snp_Texture2D_5_GSBlendMap_OpacityTexture2", @"N:\x\m45_00_kumo_60_m.tif"));
            cards.BufferLayouts[0].Add(new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.UV, 3));
            float[] cardFade = [1f, 0.5f, 0f];
            for (int i = 0; i < 3; i++)
            {
                var v = cards.Meshes[0].Vertices[i];
                v.UVs[0] = new Vector3(1 + v.UVs[0].X * 2046, 1 + v.UVs[0].Y * 2046, 0);
                v.UVs.Add(new Vector3(cardFade[i], cardFade[i], 0));
                v.UVs.Add(Vector3.Zero);
            }
            cards.Materials.Add(new FLVER2.Material("glow", @"N:\FDP\data\Material\mtd\map\M[A]_add.mtd", 0) { GXIndex = 0 });
            cards.Materials[1].Textures.Add(new FLVER2.Texture("g_DiffuseTexture", @"N:\x\m30_00_sky_06_a.tif", Vector2.One, 0, 0, 0, 0, 0));
            var glow = FLVER2.Read(SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[A]_add.mtd").Write()).Meshes[0];
            glow.MaterialIndex = 1;
            foreach (var v in glow.Vertices)
            {
                v.UVs.Add(Vector3.Zero); // the shared layout's UV3 (unused by an add mesh; its fade is the vertex alpha, 1 here)
                v.UVs.Add(Vector3.Zero);
            }
            cards.Meshes.Add(glow);
            var cardsOut = ConvertOne(cardsPort, cardsBnd, FLVER2.Read(cards.Write()));
            Expect(cardsOut.Meshes.Count == 2 && cardsPort.LeftOut.Count == 0, $"the cloud card and the sun glow are drawn: {cardsOut.Meshes.Count}, left out {string.Join(",", cardsPort.LeftOut.Keys)}");
            var cardMat = cardsPort.Materials.FirstOrDefault(m => m.Template.Kind == "nolight-opacity");
            var glowMat = cardsPort.Materials.FirstOrDefault(m => m.Template.Kind == "nolight-add");
            Expect(cardMat is not null && glowMat is not null && cardMat.Albedo.StartsWith("m45_00_kumo_30_a" + MapSkyFades.Suffix) && glowMat.Albedo.StartsWith("m30_00_sky_06_a" + MapSkyFades.Suffix),
                $"kinds and baked textures: {string.Join(", ", cardsPort.Materials.Select(m => m.Template.Kind + " " + m.Albedo))}");
            if (cardMat is not null)
            {
                var r = MapSkyFades.Recipes[cardMat.Albedo];
                Expect(r.Mask1 == "m45_00_kumo_33_m" && r.Mask2 == "m45_00_kumo_60_m" && Math.Abs(r.UvSpan.X - 2046f / 2048) < 1e-3f && r.Fade.Max() >= 250 && r.Fade.Min() <= 5,
                    $"both masks named, UVs read in short units, fade from UV 3 (1 .. 0): span {r.UvSpan} fade {r.Fade.Min()}..{r.Fade.Max()} masks {r.Mask1} {r.Mask2}");
                Expect(cardsOut.Meshes.SelectMany(m => m.Vertices).All(v => v.UVs[0].X is >= -0.01f and <= 1.01f && v.UVs[0].Y is >= -0.01f and <= 1.01f),
                    "the baked meshes' UVs run 0..1 over their own texture");
            }
            var bins = cardsPort.MatbinFiles(0).Select(f => (f.Name, Bin: MATBIN.Read(f.Bytes))).ToList();
            int Mode(string albedo) => bins.Where(b => b.Bin.Samplers.Any(x => x.Path == albedo)).Select(b => b.Bin.Params.FirstOrDefault(p => p.Name == "g_BlendMode")?.Value is int i ? i : -1).FirstOrDefault(-1);
            if (cardMat is not null && glowMat is not null && bins.Any(b => b.Bin.Params.Any(p => p.Name == "g_BlendMode")))
                Expect(Mode(cardMat.Albedo) == 2 && Mode(glowMat.Albedo) == 4, $"the card blends (2) and the glow adds (4): {Mode(cardMat.Albedo)} {Mode(glowMat.Albedo)}");
        }
        var addBin = new MATBIN();
        addBin.Params.Add(new MATBIN.Param { Name = "g_BlendMode", Type = MATBIN.ParamType.Int, Value = 0 });
        addBin.Params.Add(new MATBIN.Param { Name = "g_GX_bDisableDepthWrite", Type = MATBIN.ParamType.Bool, Value = false });
        addBin.Params.Add(new MATBIN.Param { Name = "g_AlphaRef", Type = MATBIN.ParamType.Int2, Value = new[] { 0, 0 } });
        Expect(MapModelPort.SetUnlitAdd(addBin).Count == 0 && MATBIN.Read(addBin.Write()) is var addRead
               && (int)addRead.Params[0].Value == 4 && (bool)addRead.Params[1].Value && ((int[])addRead.Params[2].Value).SequenceEqual([1, 0]),
            "the unlit template's copy adds as vanilla m11_00_900 light shafts (g_BlendMode 4, no depth write, alpha cut 1)");
        var blendBin = new MATBIN();
        blendBin.Params.Add(new MATBIN.Param { Name = "g_BlendMode", Type = MATBIN.ParamType.Int, Value = 0 });
        blendBin.Params.Add(new MATBIN.Param { Name = "g_GX_bDisableDepthWrite", Type = MATBIN.ParamType.Bool, Value = false });
        blendBin.Params.Add(new MATBIN.Param { Name = "g_AlphaRef", Type = MATBIN.ParamType.Int2, Value = new[] { 0, 0 } });
        Expect(MapModelPort.SetUnlitBlend(blendBin).Count == 0 && MATBIN.Read(blendBin.Write()) is var reread
               && (int)reread.Params[0].Value == 2 && (bool)reread.Params[1].Value && ((int[])reread.Params[2].Value).SequenceEqual([1, 0]),
            "the unlit template's copy blends as vanilla m15_00_865 (g_BlendMode 2, no depth write, alpha cut 1)");

        // DS3's alpha-tested unlit cards (M[A]_NoLight_e: the Painted World's far forest m002110, enkei_70) are drawn
        // unlit with Elden Ring's alpha cut, as vanilla AEG007_145 (g_AlphaRef 160); up to 1.0.53x they had no cut and
        // stood as grey rectangles over the trees (the "cloud tower").
        var (cardPort, cardBnd) = VisualPort();
        var card = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[A]_NoLight_e.mtd", ("g_DiffuseTexture", @"N:\x\m45_00_enkei_70_a.tif"));
        var cardOut = ConvertOne(cardPort, cardBnd, card);
        Expect(cardOut.Meshes.Count == 1 && cardPort.Materials.Single() is { Template.Kind: "nolight-edge", Albedo: "m45_00_enkei_70_a" },
            $"the far card is drawn on the unlit template, cut out: {string.Join(", ", cardPort.Materials.Select(m => m.Template.Kind + " " + m.Albedo))}");
        var cardBin = MATBIN.Read(cardPort.MatbinFiles(0).Single().Bytes);
        Expect(cardBin.ShaderPath.Contains("NoLight")
               && (cardBin.Params.FirstOrDefault(p => p.Name == "g_AlphaRef") is not { } aref ? cardPort.Notes.Any(n => n.Contains("g_AlphaRef"))
                   : ((int[])aref.Value).SequenceEqual([160, 0])),
            $"its MATBIN cuts at 160 or the port notes it can't: {string.Join("; ", cardPort.Notes)}");
        var edgeBin = new MATBIN();
        edgeBin.Params.Add(new MATBIN.Param { Name = "g_BlendMode", Type = MATBIN.ParamType.Int, Value = 2 });
        edgeBin.Params.Add(new MATBIN.Param { Name = "g_GX_bDisableDepthWrite", Type = MATBIN.ParamType.Bool, Value = true });
        edgeBin.Params.Add(new MATBIN.Param { Name = "g_AlphaRef", Type = MATBIN.ParamType.Int2, Value = new[] { 0, 0 } });
        Expect(MapModelPort.SetUnlitEdge(edgeBin).Count == 0 && MATBIN.Read(edgeBin.Write()) is var edgeRead
               && (int)edgeRead.Params[0].Value == 0 && !(bool)edgeRead.Params[1].Value && ((int[])edgeRead.Params[2].Value).SequenceEqual([160, 0]),
            "the unlit template's copy cuts as vanilla AEG007_145 (g_BlendMode 0, depth written, alpha cut 160)");
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

    private static void PlainTemplates()
    {
        var (plain, _) = VisualPort(plain: true);
        Expect(plain.TemplateSummary == "opaque=m45_00_001" && plain.Complete(true), $"plain: {plain.TemplateSummary}");
    }
}
