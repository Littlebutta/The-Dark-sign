using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Offline checks for the 1.0.307 playtest's map 2: DS3's low-detail stand-ins drawn over the detailed pieces, and
/// the colosseum's red fog and colour grading over DS3's maps.
/// </summary>
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> AtmosphereChecks()
    {
        yield return ("stand-ins: a low-detail piece over detailed pieces it is never drawn with is left out; one with nothing under it stays", StandIns);
        yield return ("world stand-ins: a kept stand-in over a neighbouring map's real pieces is left out; one over nothing stays", WorldStandIns);
        yield return ("far views: a low-detail copy of an area with ordinary textures, drawn only from a lookout, is left out; the sky dome, a building shell and a piece drawn with most of what it covers stay", FarViews);
        yield return ("atmosphere: the slot's gparam takes DS3's sun and fog colours, Stormveil's fog amounts, no colour grading", Atmosphere);
        yield return ("atmosphere: the sun's direction turns with DS3's map offset, like the map's lamps and baked light", SunTurned);
        yield return ("atmosphere: the lights without shadows (second directional light, local lights) take DS3's share of the sun, not the colosseum's", FillLights);
        yield return ("light sets: each part keeps DS3's light set and the slot's draw params get DS3's sets (Friede's dark hall, the bosses' own light)", LightSets);
    }

    /// <summary>
    /// Undead Settlement (map 3): DS3's light set 0 puts the sun at -0.51, 0.66 in the map's own space; the map offset
    /// turns the map -156 degrees into the layout, so the sun's yaw becomes 0.66 - 2.72 rad. Firelink Shrine (map 1)
    /// turns 28 degrees: its sun at yaw 89 degrees, which DS3's probes confirm in the map's space, comes out at 117.
    /// </summary>
    private static void SunTurned()
    {
        var m31 = MapAtmosphere.SunTurn(new Vector2(-0.51f, 0.66f), -156);
        Expect(m31.X == -0.51f && MathF.Abs(m31.Y - (0.66f - 156 * MathF.PI / 180)) < 1e-4f, $"map 3's sun yaw turned by the offset ({m31})");
        var m40 = MapAtmosphere.SunTurn(new Vector2(-0.58f, 1.55f), 28);
        Expect(MathF.Abs(m40.Y * 180 / MathF.PI - (1.55f * 180 / MathF.PI + 28)) < 0.01f, $"map 1's sun yaw 28 degrees on ({m40.Y * 180 / MathF.PI:0.0})");
        var wrap = MapAtmosphere.SunTurn(new Vector2(-0.5f, 3f), 90);
        Expect(wrap.Y >= -MathF.PI && wrap.Y <= MathF.PI && MathF.Abs(wrap.Y - (3f + MathF.PI / 2 - 2 * MathF.PI)) < 1e-4f, $"yaw kept within -pi..pi ({wrap.Y})");
        Expect(MapAtmosphere.SunTurn(new Vector2(-0.77f, -0.96f), 0) == new Vector2(-0.77f, -0.96f), "map 2 (no offset): DS3's angle as written");
        Expect(MathF.Abs(MapAtmosphere.SunHeight(new Vector2(-0.51f, 0)) - 29.2f) < 0.1f, "sun height in degrees");

        // Apply with the offset turns every value of the param.
        var host = new GPARAM();
        host.Groups.Add(G("LightSet", P("Directional Light Angle0", GPARAM.ParamType.Float2, new Vector2(-0.873f, 0.873f), new Vector2(-0.873f, 0.873f))));
        var ds3 = new GPARAM { Game = GPARAM.GPGame.DarkSouls3 };
        ds3.Groups.Add(G("LightSet",
            P("Directional Light Angle0", GPARAM.ParamType.Float2, new Vector2(-0.51f, 0.66f)),
            P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(0.97f, 0.95f, 0.93f, 1.5f))));
        var changes = MapAtmosphere.Apply(host, ds3, 0, -156);
        var angles = MapAtmosphere.Group(host, "LightSet")!.Params.Single(p => p.Name1 == "Directional Light Angle0").Values;
        Expect(angles.All(v => (Vector2)v == m31), "every sun angle value is the turned one");
        Expect(changes[0].Contains("turned -156 degrees with the map"), changes[0]);

        var options = MapPorter.Options.Parse(["--unturned-sun"]);
        Expect(options.UnturnedSun && !new MapPorter.Options().UnturnedSun, "--unturned-sun is read");
        Expect(MapPorter.Options.Parse(["--no-light-offset"]).UnturnedSun, "--no-light-offset leaves the sun unturned too, like the lamps and probes");
        Expect(MapPorter.Options.Parse(["--guess-probe-axes"]).BakedLight.GuessAxes, "--guess-probe-axes is read");
    }

    /// <summary>
    /// Map 3 (m31_00) as DS3 lays it out: the settlement's detailed pieces are drawn from collisions 1 and 2, its far
    /// views (m903xxx: roofs and rock walls with ordinary textures) only from the lookout collision 3, which draws none of
    /// the settlement. The 1.0.366 playtest showed those far views as low-resolution polygons floating over the map.
    /// </summary>
    private static void FarViews()
    {
        var ds3 = new MSB3();
        for (int i = 0; i < 4; i++)
        {
            var c = new MSB3.Part.Collision { Name = $"h00000{i}_0000", ModelName = $"h00000{i}" };
            Array.Clear(c.DispGroups);
            c.DispGroups[0] = 1u << i;
            ds3.Parts.Collisions.Add(c);
        }
        var meshes = new Dictionary<string, List<MapModelPort.MeshInfo>>(StringComparer.OrdinalIgnoreCase);
        void Piece(string name, Vector3 position, float half, float height, string albedo, int vertices, uint groups)
        {
            var model = name[..7];
            var p = new MSB3.Part.MapPiece { Name = name, ModelName = model, Position = position };
            Array.Clear(p.DrawGroups);
            p.DrawGroups[0] = groups;
            ds3.Parts.MapPieces.Add(p);
            meshes[MapPorter.ModelNumber(model)] = [new MapModelPort.MeshInfo("M[ARSN]_l", "opaque", vertices, new Vector3(2 * half, height, 2 * half), albedo)
                { Min = new Vector3(-half, 0, -half), Max = new Vector3(half, height, half) }];
        }
        // The settlement: 12 detailed pieces of 3,000 vertices, drawn from collisions 1 and 2.
        for (int i = 0; i < 12; i++)
            Piece($"m0000{i:D2}_1000", new Vector3(-110 + i * 20, 5, 0), 5, 10, "m31_00_roof_01_a", 3000, 0b0110);
        // Its far view: 2,000 vertices over the whole settlement, drawn only from the lookout (collision 3).
        Piece("m903400_9500", Vector3.Zero, 200, 100, "m30_00_m31_00_roof_01_a", 2000, 0b1000);
        // The sky dome covers everything too, but is drawn from collisions 0 to 2 (with the settlement).
        Piece("m009000_9000", Vector3.Zero, 9000, 3000, "m31_00_sky_a", 1600, 0b0111);
        // A building shell far from the settlement, drawn from the lookout, around 12 small inside pieces drawn
        // from collision 0: the inside has less than ten times its vertices, so it is not a stand-in for it.
        for (int i = 0; i < 12; i++)
            Piece($"m0001{i:D2}_1000", new Vector3(2000 + i * 2, 1, 0), 0.5f, 1, "m31_00_floor_a", 400, 0b0001);
        Piece("m002000_1000", new Vector3(2010, 0, 0), 30, 20, "m31_00_brickwall_11_a", 5000, 0b1000);
        // A cliff over 12 pieces it is never drawn with (60,000 vertices) and 30 it is drawn with (90,000).
        for (int i = 0; i < 12; i++)
            Piece($"m0002{i:D2}_1000", new Vector3(-4000 + i * 10, 5, 0), 2, 4, "m31_00_rock_a", 5000, 0b0010);
        for (int i = 0; i < 30; i++)
            Piece($"m0003{i:D2}_1000", new Vector3(-4000 + i * 4, 5, 20), 2, 4, "m31_00_rock_a", 3000, 0b0001);
        Piece("m009210_9000", new Vector3(-3900, 0, 0), 300, 100, "m31_00_enkei_cliff_00_a", 1000, 0b0001);

        var result = MapStandIns.Find(ds3, meshes, new HashSet<string>());
        var lines = string.Join(" | ", result.Lines);
        Expect(result.LeftOut.SetEquals(["m903400_9500"]) && result.FarViews.SetEquals(["m903400_9500"]), "only the far view is left out: " + lines);
        Expect(result.Verdicts.Single(v => v.Part == "m903400_9500") is { FarView: true, Covered: 12, CoveredVertices: 36000, InsideVertices: 36000 }, lines);
        Expect(result.Verdicts.All(v => v.Part != "m009000_9000"), "the sky dome, drawn with the settlement, is not even near the test: " + lines);
        Expect(result.Verdicts.Single(v => v.Part == "m002000_1000") is { LeftOut: false } shell && shell.Reason.Contains("10 times"), "the shell stays: " + lines);
        Expect(result.Verdicts.Single(v => v.Part == "m009210_9000") is { LeftOut: false, CoveredVertices: 60000, InsideVertices: 150000 } cliff
               && cliff.Reason.Contains("most of the detail"), "the cliff drawn with most of what it covers stays: " + lines);
        Expect(result.Summary.EndsWith("far views: 1 left out (m903400_9500)"), result.Summary);
        Expect(MapStandIns.IsFarView(100, 9, 100000, 100000).FarView == false && MapStandIns.IsFarView(100, 10, 1000, 1000).FarView, "the thresholds");

        var report = MapPorter.PieceReport(ds3, VisualPort().Port, new HashSet<string>(), false, result.LeftOut, result.FarViews);
        Expect(report.Split('\n').Any(l => l.StartsWith("m903400_9500\tm903400\tleft out: far view")), "the piece report names a far view");
    }

    private static void WorldStandIns()
    {
        var own = new List<MapStandIns.PieceBox>
        {
            new("m900065_9000", new Vector3(0, 0, 0), new Vector3(100, 50, 100), 1000, true),
            new("m900070_9000", new Vector3(500, 0, 500), new Vector3(600, 50, 600), 1000, true),
            new("m000100_0000", new Vector3(10, 0, 10), new Vector3(20, 10, 20), 5000, false),
        };
        var neighbour = new List<MapStandIns.PieceBox>
        {
            new("m001000_0000", new Vector3(10, 0, 10), new Vector3(20, 10, 20), 200, false),
            new("m001001_0000", new Vector3(30, 0, 30), new Vector3(40, 10, 40), 200, false),
            new("m001002_0000", new Vector3(50, 0, 50), new Vector3(60, 10, 60), 200, false),
            new("m909000_0000", new Vector3(500, 0, 500), new Vector3(600, 50, 600), 5000, true), // the neighbour's own stand-in
        };
        var verdicts = MapStandIns.AcrossMaps(own, new HashSet<string> { "m900065_9000", "m900070_9000" }, neighbour);
        Expect(verdicts.Count == 2, "only kept stand-ins are judged");
        Expect(verdicts.Single(v => v.Part == "m900065_9000").LeftOut, "three detailed neighbour pieces with 600 of 1000 vertices: left out");
        Expect(!verdicts.Single(v => v.Part == "m900070_9000").LeftOut, "only the neighbour's own stand-in there: kept");
        var few = MapStandIns.AcrossMaps(own, new HashSet<string> { "m900065_9000" }, neighbour.Take(2));
        Expect(!few.Single().LeftOut, "two pieces are too few");
    }

    private static void StandIns()
    {
        var ds3 = new MSB3();
        for (int i = 0; i < 4; i++)
        {
            var c = new MSB3.Part.Collision { Name = $"h00000{i}_0000", ModelName = $"h00000{i}" };
            Array.Clear(c.DispGroups);
            c.DispGroups[0] = 1u << i;
            ds3.Parts.Collisions.Add(c);
        }
        var meshes = new Dictionary<string, List<MapModelPort.MeshInfo>>(StringComparer.OrdinalIgnoreCase);
        void Piece(string model, Vector3 position, Vector3 min, Vector3 max, string albedo, int vertices, uint groups, float rotY = 0)
        {
            var p = new MSB3.Part.MapPiece { Name = model + "_1000", ModelName = model, Position = position, Rotation = new Vector3(0, rotY, 0) };
            Array.Clear(p.DrawGroups);
            p.DrawGroups[0] = groups;
            ds3.Parts.MapPieces.Add(p);
            meshes[MapPorter.ModelNumber(model)] = [new MapModelPort.MeshInfo("M[ARSN]_l", "opaque", vertices, max - min, albedo) { Min = min, Max = max }];
        }
        // As m30_00 has them: a hall stand-in drawn from collision 0, the detailed hall drawn from collisions 1 and 2.
        Piece("m900060", Vector3.Zero, new Vector3(-50, 0, -50), new Vector3(50, 30, 50), "m30_00_low_hall_01_a", 10000, 0b0001);
        for (int i = 0; i < 4; i++)
            Piece($"m00010{i}", new Vector3(-30 + i * 20, 5, 0), new Vector3(-5, 0, -5), new Vector3(5, 10, 5), "m30_00_wall_a", 3000, 0b0110);
        // A castle stand-in (m980xxx) with only far scenery inside, drawn from the same collision as the stand-in.
        Piece("m980000", new Vector3(500, 0, 0), new Vector3(-50, 0, -50), new Vector3(50, 80, 50), "m30_00_low_Castlewall_01_a", 20000, 0b0001);
        Piece("m000200", new Vector3(500, 10, 0), new Vector3(-5, 0, -5), new Vector3(5, 10, 5), "m30_00_tree_a", 2000, 0b1001);
        // A stand-in drawn from no collision, and one turned 90 degrees whose box is long on z only after turning.
        Piece("m900300", new Vector3(-500, 0, 0), new Vector3(-50, 0, -50), new Vector3(50, 30, 50), "m30_00_low_billding_01_a", 100, 0);
        Piece("m900400", new Vector3(0, 0, 1000), new Vector3(-100, 0, -5), new Vector3(100, 20, 5), "m30_00_low_etower_01_a", 1000, 0b0001, 90);
        for (int i = 0; i < 3; i++)
            Piece($"m00030{i}", new Vector3(0, 5, 950 + i * 40), new Vector3(-2, 0, -2), new Vector3(2, 4, 2), "m30_00_floor_a", 500, 0b1000);
        // A piece that is mostly ordinary texture with a little low detail is not a stand-in.
        ds3.Parts.MapPieces.Add(new MSB3.Part.MapPiece { Name = "m900500_0000", ModelName = "m900500" });
        meshes["900500"] = [new("M[ARSN]_l", "opaque", 900, Vector3.One, "m30_00_ground_06_a") { Max = Vector3.One }, new("M[ARSN]_l", "opaque", 100, Vector3.One, "m30_00_low_x_a") { Max = Vector3.One }];

        Expect(MapStandIns.IsLowTexture("m30_00_low_hall_01_a") && MapStandIns.IsLowTexture("m30_00_o008814_low_tree_01_a")
               && !MapStandIns.IsLowTexture("m30_00_lower_wall_a") && !MapStandIns.IsLowTexture("m30_00_wall_a"), "low texture names");
        var result = MapStandIns.Find(ds3, meshes, new HashSet<string>());
        var left = result.LeftOut;
        Expect(left.Contains("m900060_1000"), "the hall stand-in over the detailed hall is left out: " + string.Join(" | ", result.Lines));
        Expect(!left.Contains("m980000_1000"), "the castle stand-in that shares a collision with what is inside it stays");
        Expect(!left.Contains("m900300_1000"), "a stand-in drawn from no collision stays");
        Expect(left.Contains("m900400_1000"), "a turned stand-in is placed by its rotation: " + string.Join(" | ", result.Lines));
        Expect(!left.Contains("m900500_0000") && result.Verdicts.All(v => v.Part != "m900500_0000"), "a mostly ordinary piece is not judged");
        Expect(result.Verdicts.Count == 4 && result.Summary.StartsWith("low-detail stand-ins: 2 left out, 2 kept"), result.Summary);
        Expect(MapStandIns.Find(ds3, meshes, new HashSet<string> { "m900060_1000" }).Verdicts.All(v => v.Part != "m900060_1000"), "a piece already left out is not judged");

        // The piece report says why, and the fallback keeps them.
        var report = MapPorter.PieceReport(ds3, VisualPort().Port, new HashSet<string>(), false, left);
        Expect(report.Split('\n').Any(l => l.StartsWith("m900060_1000\tm900060\tleft out: low detail stand-in")), "the piece report names a stand-in");
        Expect(MapPorter.Options.Parse(["--stand-ins"]).StandIns && !new MapPorter.Options().StandIns, "--stand-ins is read");
    }

    private static GPARAM.Param P(string name, GPARAM.ParamType type, params object[] values)
    {
        var p = new GPARAM.Param(name, name, type);
        for (int i = 0; i < values.Length; i++)
        {
            p.Values.Add(values[i]);
            p.ValueIDs.Add(i * 100);
        }
        return p;
    }

    private static GPARAM.Group G(string name, params GPARAM.Param[] ps)
    {
        var g = new GPARAM.Group(name + " ParamEditor", name);
        g.Params.AddRange(ps);
        return g;
    }

    private static void FillLights()
    {
        // The Royal colosseum's light set (m45_02_0000, map 3's host): a second light as strong as the sun. DS3's
        // m31_00 light set 0: 0.2 beside a sun of 1.5, local lights dark.
        GPARAM Host()
        {
            var g = new GPARAM();
            g.Groups.Add(G("LightSet",
                P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(0.98f, 1, 0.992f, 0.4f), new Vector4(0.98f, 1, 0.992f, 0.4f)),
                P("Directional Light SsaoWeight0", GPARAM.ParamType.Float, 0.5f, 0.5f),
                P("Directional Light DiffColor1", GPARAM.ParamType.Float4, new Vector4(0.675f, 0.784f, 1, 0.4f), new Vector4(1, 1, 1, 0.05f)),
                P("Directional Light SpecColor1", GPARAM.ParamType.Float4, new Vector4(0.667f, 0.741f, 0.843f, 0.3f), new Vector4(1, 1, 1, 0.05f)),
                P("Directional Light SsaoWeight1", GPARAM.ParamType.Float, 0f, 1f),
                P("Directional Light LocalLight DiffColor1", GPARAM.ParamType.Float4, new Vector4(0.761f, 0.816f, 1, 0.1f), new Vector4(0.761f, 0.816f, 1, 0.1f)),
                P("Directional Light SsaoWeight2", GPARAM.ParamType.Float, 0.65f, 0.65f),
                P("Directional Light SsaoWeight3", GPARAM.ParamType.Float, 0f, 1f),
                P("Directional Light Hemi Color Up", GPARAM.ParamType.Float4, new Vector4(0.885f, 0.918f, 0.912f, 0.1f)),
                P("Directional Enable LocalLight1", GPARAM.ParamType.BoolA, true, true),
                P("Directional Enable LocalLight2", GPARAM.ParamType.BoolA, true, true)));
            return g;
        }
        var ds3 = new GPARAM { Game = GPARAM.GPGame.DarkSouls3 };
        ds3.Groups.Add(G("LightSet",
            P("Directional Light Angle0", GPARAM.ParamType.Float2, new Vector2(-0.51f, 0.66f)),
            P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(0.968f, 0.953f, 0.933f, 1.5f)),
            P("Directional Light DiffColor1", GPARAM.ParamType.Float4, new Vector4(0.603f, 0.706f, 1, 0.2f), new Vector4(0.875f, 0.942f, 1, 0)),
            P("Directional Light SpecColor1", GPARAM.ParamType.Float4, new Vector4(0.603f, 0.706f, 1, 0.2f)),
            P("Directional Light LocalLight DiffColor1", GPARAM.ParamType.Float4, new Vector4(0, 0, 0, 1)),
            P("Directional Enable LocalLight1", GPARAM.ParamType.BoolA, true),
            P("Directional Enable LocalLight2", GPARAM.ParamType.BoolA, false)));

        var host = Host();
        var changes = MapAtmosphere.Apply(host, ds3, -209);
        var light = MapAtmosphere.Group(host, "LightSet")!;
        GPARAM.Param Param(string name) => light.Params.Single(p => p.Name1 == name);
        static bool Near(Vector4 a, Vector4 b) => Vector4.Distance(a, b) < 1e-4f;
        float scale = 0.4f / 1.5f;
        Expect(Param("Directional Light DiffColor0").Values.All(v => ((Vector4)v).W == 0.4f), "the sun keeps the host's strength");
        Expect(Param("Directional Light DiffColor1").Values.All(v => Near((Vector4)v, new Vector4(0.603f, 0.706f, 1, 0.2f * scale))),
            $"the second light: DS3's colour at DS3's share of the sun, 0.4 -> {0.2f * scale:0.###} ({Param("Directional Light DiffColor1").Values[0]})");
        Expect(Param("Directional Light SpecColor1").Values.All(v => Near((Vector4)v, new Vector4(0.603f, 0.706f, 1, 0.2f * scale))), "its reflections too");
        Expect(Param("Directional Light LocalLight DiffColor1").Values.All(v => Near((Vector4)v, new Vector4(0, 0, 0, scale))), "a local light DS3 has dark is dark");
        Expect(Param("Directional Enable LocalLight1").Values.All(v => (bool)v) && Param("Directional Enable LocalLight2").Values.All(v => !(bool)v), "local lights are on where DS3 has them on");
        Expect(Param("Directional Light SsaoWeight0").Values.All(v => (float)v == 1) && Param("Directional Light SsaoWeight1").Values.All(v => (float)v == 1)
               && Param("Directional Light SsaoWeight3").Values.All(v => (float)v == 1) && Param("Directional Light SsaoWeight2").Values.All(v => (float)v == 0.65f),
            "corner shade dims the sun and the second light as on Stormveil; the third weight stays the host's");
        Expect((Vector4)Param("Directional Light Hemi Color Up").Values[0] == new Vector4(0.885f, 0.918f, 0.912f, 0.1f), "the hemisphere light stays the host's");
        Expect(changes.Any(c => c.Contains("second light 0.4 -> 0.053") && c.Contains("local light 1 0.1 -> 0 (DS3 has it dark)")), string.Join(" | ", changes));

        // The fallback keeps the colosseum's strengths; the second light still takes DS3's colour, as before.
        var kept = Host();
        var keptChanges = MapAtmosphere.Apply(kept, ds3, -209, hostFillLights: true);
        var keptLight = MapAtmosphere.Group(kept, "LightSet")!;
        Expect(Near((Vector4)keptLight.Params.Single(p => p.Name1 == "Directional Light DiffColor1").Values[0], new Vector4(0.603f, 0.706f, 1, 0.4f))
               && (float)keptLight.Params.Single(p => p.Name1 == "Directional Light SsaoWeight0").Values[0] == 0.5f
               && Near((Vector4)keptLight.Params.Single(p => p.Name1 == "Directional Light LocalLight DiffColor1").Values[0], new Vector4(0.761f, 0.816f, 1, 0.1f))
               && keptChanges.Any(c => c.Contains("--host-fill-lights")), "--host-fill-lights keeps the host's strengths and weights");
        Expect(MapPorter.Options.Parse(["--host-fill-lights"]).HostFillLights && !MapPorter.Options.Parse([]).HostFillLights, "--host-fill-lights (fallback map-fill-light B) is read");
    }

    private static void LightSets()
    {
        // The Painted World: host m45_00 sets 0 and 100 (sun 0.6); DS3 m45_00 set 0 (sun 0.8), set 1 "for darkness"
        // (no light at all: Friede's hall), set 111 (Friede's own light) and a made-up set 4 with a sun far past set 0's.
        // Elden Ring's draw params read as the Sekiro format.
        var host = new GPARAM { Game = GPARAM.GPGame.Sekiro, Unk0D = true, Unk40 = 6.5f, Unk50 = 24 };
        host.Groups.Add(G("LightSet",
            P("Directional Light Angle0", GPARAM.ParamType.Float2, new Vector2(-0.52f, 1.57f), new Vector2(-0.52f, 1.57f)),
            P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(1, 0.94f, 0.9f, 0.6f), new Vector4(1, 0.94f, 0.9f, 0.6f)),
            P("Directional Light DiffColor1", GPARAM.ParamType.Float4, new Vector4(0.67f, 0.78f, 1, 0.1f), new Vector4(1, 1, 1, 0.05f)),
            P("Directional Light Sharpness0", GPARAM.ParamType.Float, 0.3f, 0.3f),
            P("Directional Enable LocalLight1", GPARAM.ParamType.BoolA, true, false)));
        host.Groups[0].Comments.AddRange(["", ""]);
        foreach (var p in host.Groups[0].Params)
            p.TimeOfDay = [.. p.Values.Select(_ => 0f)];
        host.Unk3s.Add(new GPARAM.Unk3(0) { ValueIDs = [0, 100] });
        var ds3 = new GPARAM { Game = GPARAM.GPGame.DarkSouls3 };
        GPARAM.Param D(string name, GPARAM.ParamType type, params (int Id, object Value)[] values)
        {
            var p = new GPARAM.Param(name, name, type);
            foreach (var (id, v) in values)
            {
                p.Values.Add(v);
                p.ValueIDs.Add(id);
            }
            return p;
        }
        ds3.Groups.Add(G("LightSet",
            D("Directional Light Angle0", GPARAM.ParamType.Float2, (0, new Vector2(-0.79f, -1.22f)), (1, new Vector2(-0.51f, 0.66f)), (4, new Vector2(-0.3f, 0)), (111, new Vector2(-0.79f, -1.57f))),
            D("Directional Light DiffColor0", GPARAM.ParamType.Float4, (0, new Vector4(0.81f, 0.9f, 1, 0.8f)), (1, new Vector4(1, 1, 1, 0)), (4, new Vector4(1, 0.86f, 0.66f, 13.4f)), (111, new Vector4(1, 1, 1, 0))),
            D("Directional Light DiffColor1", GPARAM.ParamType.Float4, (0, new Vector4(0.62f, 0.77f, 1, 0.3f)), (1, new Vector4(0.6f, 0.71f, 1, 0)), (4, new Vector4(0, 0, 0, 5)), (111, new Vector4(0.91f, 0.96f, 1, 0.8f))),
            D("Directional Enable LocalLight1", GPARAM.ParamType.BoolA, (0, true), (1, true), (4, false), (111, true))));
        ds3.Groups[0].Comments.AddRange(["MAP_メイン:Outside", "暗闇用", "", "ボス_娘　戦闘"]);

        var changes = MapAtmosphere.Apply(host, ds3, 0, 0, false, [0, 1, 4, 100, 111, 999]);
        var light = MapAtmosphere.Group(host, "LightSet")!;
        Vector4 Colour(string name, int id) { var p = light.Params.Single(x => x.Name1 == name); return (Vector4)p.Values[p.ValueIDs.IndexOf(id)]; }
        float scale = 0.6f / 0.8f;
        Expect(light.Params.All(p => p.ValueIDs.SequenceEqual([0, 1, 4, 100, 111])), $"sets 1, 4 and 111 added in ID order; 100 stays the host's; 999 (not in DS3) left out ({string.Join(",", light.Params[0].ValueIDs)})");
        Expect(host.Unk3s[0].ValueIDs.SequenceEqual([0, 1, 4, 100, 111]) && light.Comments.Count == 5 && light.Comments[1].Contains("暗闇用"), "the group's ID list and comments follow");
        Expect(Colour("Directional Light DiffColor0", 1).W == 0 && Colour("Directional Light DiffColor1", 1).W == 0, "set 1 (Friede's hall) has no sun and no second light, as in DS3");
        Expect(Vector4.Distance(Colour("Directional Light DiffColor1", 111), new Vector4(0.91f, 0.96f, 1, 0.8f * scale)) < 1e-4f, $"Friede's light at DS3's strength on the slot's scale ({Colour("Directional Light DiffColor1", 111)})");
        Expect(Colour("Directional Light DiffColor0", 4).W == 0.6f * MapLightSets.MaxOverSun, $"a sun far past DS3's set 0 is kept within {MapLightSets.MaxOverSun} times the slot's ({Colour("Directional Light DiffColor0", 4).W})");
        Expect(Colour("Directional Light DiffColor1", 4) == new Vector4(0, 0, 0, 5 * scale), "a black (switched off) light is left as DS3 writes it");
        Expect(Colour("Directional Light DiffColor0", 100) == new Vector4(0.81f, 0.9f, 1, 0.6f), "the host's set 100 keeps the host's sun strength with DS3's colour, as set 0");
        var sharp = light.Params.Single(p => p.Name1 == "Directional Light Sharpness0");
        Expect(sharp.Values.All(v => (float)v == 0.3f), "what DS3 doesn't set comes from the slot's set 0");
        var angle = light.Params.Single(p => p.Name1 == "Directional Light Angle0");
        Expect((Vector2)angle.Values[angle.ValueIDs.IndexOf(1)] == new Vector2(-0.51f, 0.66f), "each set's own sun direction");
        Expect(changes.Any(c => c.Contains("DS3 light sets 1 (暗闇用), 4, 111 (ボス_娘　戦闘) added") && c.Contains("turned down in sets 4")), string.Join(" | ", changes));
        var back = GPARAM.Read(host.Write());
        Expect(MapAtmosphere.Group(back, "LightSet")!.Params.All(p => p.Values.Count == 5), "the file reads back");
        // Far-view and sky sets (DS3 names them 遠景 / 天球, or numbers them from 300) may be brighter than other sets.
        Expect(MapLightSets.IsFarView(8, "他マップの遠景") && MapLightSets.IsFarView(9, "天球") && MapLightSets.IsFarView(310, "")
               && !MapLightSets.IsFarView(4, "") && !MapLightSets.IsFarView(1, "暗闇用"), "far-view sets are told apart");
        var farHost = new GPARAM();
        farHost.Groups.Add(G("LightSet", P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(1, 1, 1, 0.6f))));
        farHost.Groups[0].Params[0].TimeOfDay = [0f];
        var farDs3 = new GPARAM { Game = GPARAM.GPGame.DarkSouls3 };
        farDs3.Groups.Add(G("LightSet", D("Directional Light DiffColor0", GPARAM.ParamType.Float4, (0, new Vector4(1, 1, 1, 1.2f)), (8, new Vector4(0.6f, 0.85f, 1, 6)), (11, new Vector4(1, 1, 1, 6)))));
        farDs3.Groups[0].Comments.AddRange(["", "他マップの遠景", ""]);
        MapLightSets.Add(farHost, farDs3, [8, 11], 0.5f, 0);
        var farSun = farHost.Groups[0].Params[0];
        float W(int id) => ((Vector4)farSun.Values[farSun.ValueIDs.IndexOf(id)]).W;
        Expect(W(8) == 3 && W(11) == 0.6f * MapLightSets.MaxOverSun, $"a far view keeps DS3's 6 x 0.5 = 3 (within {MapLightSets.MaxFarOverSun} x the sun); another set is capped at {MapLightSets.MaxOverSun} x ({W(8)}, {W(11)})");

        // Parts: a DS3 part's set is carried; one the draw params lack, and every one with the fallback, goes to -1.
        var piece = new MSB3.Part.MapPiece { Name = "m010701_5000", ModelName = "m010701" };
        piece.Gparam.LightSetID = 1;
        var ported = new MSBE.Part.MapPiece();
        ported.Gparam.LightSetID = -1;
        MapLightSets.Carry(piece, ported);
        var enemy = new MSB3.Part.Enemy { Name = "c6020_0000", ModelName = "c6020" };
        enemy.Gparam.LightSetID = 111;
        var portedEnemy = new MSBE.Part.Enemy();
        MapLightSets.Carry(enemy, portedEnemy);
        var stray = new MSBE.Part.MapPiece();
        stray.Gparam.LightSetID = 999;
        var msb = new MSBE();
        msb.Parts.MapPieces.AddRange([ported, stray]);
        msb.Parts.Enemies.Add(portedEnemy);
        Expect(ported.Gparam.LightSetID == 1 && portedEnemy.Gparam.LightSetID == 111 && MapLightSets.Wanted(msb).SetEquals([1, 111, 999]), "DS3's sets carried to the ported parts");
        var line = MapLightSets.Finish(msb, new HashSet<int> { 0, 1, 100, 111 }, off: false);
        Expect(ported.Gparam.LightSetID == 1 && portedEnemy.Gparam.LightSetID == 111 && stray.Gparam.LightSetID == -1 && line.Contains("1 x1") && line.Contains("1 on sets the draw params lack"), line);
        MapLightSets.Finish(msb, new HashSet<int> { 0, 1, 100, 111 }, off: true);
        Expect(ported.Gparam.LightSetID == -1 && portedEnemy.Gparam.LightSetID == -1, "--one-light-set leaves every part on the default");
        Expect(MapPorter.Options.Parse(["--one-light-set"]).OneLightSet && !MapPorter.Options.Parse([]).OneLightSet, "--one-light-set (fallback map-light-sets B) is read");
    }

    private static void Atmosphere()
    {
        // The Caelid colosseum's values (m45_01_0000) and DS3's m30_00 light set 0 and fog 0.
        var host = new GPARAM();
        host.Groups.Add(G("LightSet",
            P("Directional Light Angle0", GPARAM.ParamType.Float2, new Vector2(-0.873f, 0.873f), new Vector2(-0.873f, 0.873f)),
            P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(0.897f, 0.947f, 0.895f, 0.5f), new Vector4(0.897f, 0.947f, 0.895f, 0.5f)),
            P("Directional Light Hemi Color Up", GPARAM.ParamType.Float4, new Vector4(0.945f, 0.945f, 0.945f, 0.1f))));
        host.Groups.Add(G("ColorGrading", P("Yebis-ColorGrading-Enable", GPARAM.ParamType.IntA, 1), P("Yebis-LutSourceId", GPARAM.ParamType.IntA, 101)));
        host.Groups.Add(G("VolumetricFog",
            P("VolumetricFog FarDepth", GPARAM.ParamType.Float, 2000f),
            P("VolumetricFog Global HeightFogOffset", GPARAM.ParamType.Float, -40f),
            P("VolumetricFog Global HeightFogDensity Scale", GPARAM.ParamType.Float, 0.003f),
            P("VolumetricFog DirLight DirectColor", GPARAM.ParamType.Float4, new Vector4(0.533f, 0.243f, 0.184f, 1.6f)),
            P("VolumetricFog SkyLight SkyColor", GPARAM.ParamType.Float4, new Vector4(0.141f, 0.176f, 0.18f, 0.5f))));
        var ds3 = new GPARAM { Game = GPARAM.GPGame.DarkSouls3 };
        ds3.Groups.Add(G("LightSet",
            P("Directional Light Angle0", GPARAM.ParamType.Float2, new Vector2(-0.768f, -0.96f), new Vector2(-0.209f, 0.367f)),
            P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(1, 0.945f, 0.869f, 1.23f), new Vector4(1, 0.963f, 0.948f, 3.265f))));
        ds3.Groups.Add(G("Fog",
            P("Fog-Color", GPARAM.ParamType.Float4, new Vector4(1, 0.932f, 0.814f, 0.12f)),
            P("Fog-heightColor", GPARAM.ParamType.Float4, new Vector4(0.899f, 0.885f, 1, 0))));

        var changes = MapAtmosphere.Apply(host, ds3, -44);
        var light = MapAtmosphere.Group(host, "LightSet");
        var fog = MapAtmosphere.Group(host, "VolumetricFog");
        var sun = light!.Params.Single(p => p.Name1 == "Directional Light DiffColor0");
        Expect(sun.Values.All(v => v is Vector4 c && c == new Vector4(1, 0.945f, 0.869f, 0.5f)), "every sun colour value takes DS3's colour and keeps Elden Ring's strength");
        Expect(light.Params.Single(p => p.Name1 == "Directional Light Angle0").Values.All(v => (Vector2)v == new Vector2(-0.768f, -0.96f)), "the sun comes from DS3's angle");
        Expect((Vector4)light.Params.Single(p => p.Name1 == "Directional Light Hemi Color Up").Values[0] == new Vector4(0.945f, 0.945f, 0.945f, 0.1f), "other light params stay the host's");
        // m30_00: a cream at 0.12 (maps 1 and 3: about 0.26) and no height fog (strength 0).
        static bool Near(Vector4 a, Vector4 b) => Vector4.Distance(a, b) < 0.002f;
        var m30Fog = new Vector4(1 * 0.12f / MapAtmosphere.Ds3FogStrength, 0.932f * 0.12f / MapAtmosphere.Ds3FogStrength, 0.814f * 0.12f / MapAtmosphere.Ds3FogStrength, 0);
        Expect(Near(MapAtmosphere.Value<Vector4>(fog, "VolumetricFog DirLight DirectColor") ?? default, m30Fog with { W = MapAtmosphere.FogDirectStrength }),
            $"fog glow is DS3's colour times its own strength, at Stormveil's strength, not Caelid's red: {MapAtmosphere.Value<Vector4>(fog, "VolumetricFog DirLight DirectColor")}");
        Expect(Near(MapAtmosphere.Value<Vector4>(fog, "VolumetricFog SkyLight SkyColor") ?? default, m30Fog with { W = MapAtmosphere.FogSkyStrength }),
            "DS3 draws no height fog on m30 (strength 0): the sky colour takes the distance colour");
        // m40_00 and m31_00, whose fog looked right: nearly unchanged; colours never above 1; the old way on request.
        var (m40Direct, m40Rest) = MapAtmosphere.FogColours(new Vector4(0.417f, 0.459f, 0.524f, 0.256f), new Vector4(0.572f, 0.623f, 0.71f, 1));
        Expect(Near(m40Direct, new Vector4(0.417f, 0.459f, 0.524f, 0.256f) * new Vector4(0.256f / MapAtmosphere.Ds3FogStrength, 0.256f / MapAtmosphere.Ds3FogStrength, 0.256f / MapAtmosphere.Ds3FogStrength, 1))
            && Near(m40Rest, new Vector4(0.572f, 0.623f, 0.71f, 1)), $"m40's fog keeps its colours: {m40Direct} / {m40Rest}");
        var (bright, _) = MapAtmosphere.FogColours(new Vector4(1, 0.5f, 0.25f, 1), null);
        Expect(Near(bright, new Vector4(1, 0.5f, 0.25f, 1)), $"a strong fog colour is capped at 1, keeping its hue: {bright}");
        var (raw, rawRest) = MapAtmosphere.FogColours(new Vector4(1, 0.932f, 0.814f, 0.12f), new Vector4(0.899f, 0.885f, 1, 0), strengths: false);
        Expect(raw == new Vector4(1, 0.932f, 0.814f, 0.12f) && rawRest == new Vector4(0.899f, 0.885f, 1, 0), "fallback map-atmosphere C: DS3's colours as they are");
        Expect(MapAtmosphere.Value<float>(fog, "VolumetricFog FarDepth") == MapAtmosphere.FogFarDepth && MapAtmosphere.Value<float>(fog, "VolumetricFog Global HeightFogOffset") == -44f,
            "Stormveil's reach, height fog from the DS3 ground");
        Expect(MapAtmosphere.Value<int>(MapAtmosphere.Group(host, "ColorGrading"), "Yebis-ColorGrading-Enable") == 0, "colour grading off");
        Expect(changes.Count == 3 && changes[2] == "colour grading off", string.Join(" | ", changes));
        // Writing is not checked here: a hand-built GPARAM lacks the sections a real one has. Checked on the real files
        // (2026-11-11): m45_00, m45_01 and m45_02_0000.gparam read and write back byte for byte, and read back changed.

        // DS3 ground: one in twenty entities stand lower.
        var msb = new MSB3();
        for (int i = 0; i < 40; i++)
            msb.Parts.Enemies.Add(new MSB3.Part.Enemy { Name = $"c1000_{i:D4}", ModelName = "c1000", Position = new Vector3(0, i - 10, 0) });
        Expect(MapAtmosphere.GroundHeight(msb) == -8, $"ground height {MapAtmosphere.GroundHeight(msb)}");
        Expect(MapAtmosphere.GroundHeight(new MSB3()) == 0, "a map with no entities starts the fog at 0");

        // DS3's depth haze: as thick at the middle play height as hides DS3's fog 0 strength at the fog's reach.
        var layer = MapAtmosphere.Layer(msb);
        Expect(layer.Height == 10 && layer.Span == 36, $"play height {layer.Height}, span {layer.Span}");
        var (density, distribution, offset) = MapAtmosphere.DepthFog(0.268f, new MapAtmosphere.FogLayer(-180, 130));
        Expect(MathF.Abs(1 - MathF.Exp(-density * MapAtmosphere.FogFarDepth) - 0.268f) < 1e-4f && offset == -180
               && MathF.Abs(MathF.Exp(distribution * 65) - 2) < 1e-3f, $"map 3's haze: density {density}, distribution {distribution}, offset {offset}");
        var (thin, flat, _) = MapAtmosphere.DepthFog(0, new MapAtmosphere.FogLayer(0, 10));
        Expect(thin > 0 && MathF.Abs(MathF.Exp(flat * MapAtmosphere.MinFogSpan / 2) - 2) < 1e-3f, "a map with no DS3 strength or height still gets a little haze, spread over the least span");
        var depthHost = new GPARAM();
        depthHost.Groups.Add(G("VolumetricFog", P("VolumetricFog Global HeightFogOffset", GPARAM.ParamType.Float, 0f), P("VolumetricFog Global HeightFogDensity Scale", GPARAM.ParamType.Float, 0.004f)));
        MapAtmosphere.Apply(depthHost, ds3, -278, layer: new MapAtmosphere.FogLayer(-150, 200));
        Expect(MapAtmosphere.Value<float>(MapAtmosphere.Group(depthHost, "VolumetricFog"), "VolumetricFog Global HeightFogOffset") == -150f, "the fog sits at the play height, not the lowest ground");

        // Nothing to take from DS3: the host's values stay. Fallback map-atmosphere B keeps the file.
        var plain = new GPARAM();
        plain.Groups.Add(G("VolumetricFog", P("VolumetricFog FarDepth", GPARAM.ParamType.Float, 2000f)));
        MapAtmosphere.Apply(plain, new GPARAM(), 0);
        Expect(MapAtmosphere.Value<float>(MapAtmosphere.Group(plain, "VolumetricFog"), "VolumetricFog FarDepth") == 2000f, "no DS3 fog: the host's fog stays");
        Expect(MapPorter.Options.Parse(["--host-atmosphere"]).HostAtmosphere && !new MapPorter.Options().HostAtmosphere, "--host-atmosphere is read");
        Expect(MapAtmosphere.GparamPath(MapId.Parse("m45_01_00_00")) == "/param/drawparam/m45_01_0000.gparam.dcx", "slot gparam path");
    }
}
