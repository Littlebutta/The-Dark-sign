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
        yield return ("atmosphere: the slot's gparam takes DS3's sun and fog colours, Stormveil's fog amounts, no colour grading", Atmosphere);
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

    private static void Atmosphere()
    {
        GPARAM.Param P(string name, GPARAM.ParamType type, params object[] values)
        {
            var p = new GPARAM.Param(name, name, type);
            for (int i = 0; i < values.Length; i++)
            {
                p.Values.Add(values[i]);
                p.ValueIDs.Add(i * 100);
            }
            return p;
        }
        GPARAM.Group G(string name, params GPARAM.Param[] ps)
        {
            var g = new GPARAM.Group(name + " ParamEditor", name);
            g.Params.AddRange(ps);
            return g;
        }
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
