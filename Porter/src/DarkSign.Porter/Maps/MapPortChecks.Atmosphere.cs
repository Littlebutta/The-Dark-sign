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
        yield return ("far views: a low-detail copy of an area with ordinary textures, drawn only from a lookout, is left out; the sky dome, a building shell and a piece drawn with most of what it covers stay", FarViews);
        yield return ("atmosphere: the slot's gparam takes DS3's sun and fog colours, Stormveil's fog amounts, no colour grading", Atmosphere);
        yield return ("atmosphere: the sun's direction turns with DS3's map offset, like the map's lamps and baked light", SunTurned);
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
        Expect(MapAtmosphere.Value<Vector4>(fog, "VolumetricFog DirLight DirectColor") == new Vector4(1, 0.932f, 0.814f, MapAtmosphere.FogDirectStrength), "fog glow is DS3's colour at Stormveil's strength, not Caelid's red");
        Expect(MapAtmosphere.Value<Vector4>(fog, "VolumetricFog SkyLight SkyColor") == new Vector4(0.899f, 0.885f, 1, MapAtmosphere.FogSkyStrength), "fog sky colour is DS3's height colour");
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

        // Nothing to take from DS3: the host's values stay. Fallback map-atmosphere B keeps the file.
        var plain = new GPARAM();
        plain.Groups.Add(G("VolumetricFog", P("VolumetricFog FarDepth", GPARAM.ParamType.Float, 2000f)));
        MapAtmosphere.Apply(plain, new GPARAM(), 0);
        Expect(MapAtmosphere.Value<float>(MapAtmosphere.Group(plain, "VolumetricFog"), "VolumetricFog FarDepth") == 2000f, "no DS3 fog: the host's fog stays");
        Expect(MapPorter.Options.Parse(["--host-atmosphere"]).HostAtmosphere && !new MapPorter.Options().HostAtmosphere, "--host-atmosphere is read");
        Expect(MapAtmosphere.GparamPath(MapId.Parse("m45_01_00_00")) == "/param/drawparam/m45_01_0000.gparam.dcx", "slot gparam path");
    }
}
