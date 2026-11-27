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
        yield return ("light sets: parts name Elden Ring's sets (one set lights the whole frame; DS3's per-part sets flickered), and the draw params write any DS3 set something names", LightSets);
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

        var report = MapPorter.PieceReport(ds3, VisualPort().Port, new HashSet<string>(), result.LeftOut, result.FarViews);
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

        // The piece report says why.
        var report = MapPorter.PieceReport(ds3, VisualPort().Port, new HashSet<string>(), left);
        Expect(report.Split('\n').Any(l => l.StartsWith("m900060_1000\tm900060\tleft out: low detail stand-in")), "the piece report names a stand-in");
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

        var changes = MapAtmosphere.Apply(host, ds3, 0, 0, [0, 1, 4, 100, 111, 120, 999], characterSets: new HashSet<int> { 4 });
        var light = MapAtmosphere.Group(host, "LightSet")!;
        Vector4 Colour(string name, int id) { var p = light.Params.Single(x => x.Name1 == name); return (Vector4)p.Values[p.ValueIDs.IndexOf(id)]; }
        Expect(light.Params.All(p => p.ValueIDs.SequenceEqual([0, 1, 4, 100, 111])), $"sets 1, 4 and 111 added in ID order; 100 stays the host's; 120 and 999 (not in DS3) left out ({string.Join(",", light.Params[0].ValueIDs)})");
        Expect(host.Unk3s[0].ValueIDs.SequenceEqual([0, 1, 4, 100, 111]) && light.Comments.Count == 5 && light.Comments[1].Contains("暗闇用"), "the group's ID list and comments follow");
        // Every light value stays the host's: set 0's for map sets, set 100's for character sets (with ray tracing on,
        // DS3's light values made the whole screen flicker).
        Expect(Colour("Directional Light DiffColor0", 0) == new Vector4(1, 0.94f, 0.9f, 0.6f) && Colour("Directional Light DiffColor1", 0) == new Vector4(0.67f, 0.78f, 1, 0.1f),
            "set 0 keeps the host's sun and second light (not DS3's colours or strengths)");
        Expect(Colour("Directional Light DiffColor0", 1) == new Vector4(1, 0.94f, 0.9f, 0.6f) && Colour("Directional Light DiffColor1", 1) == new Vector4(0.67f, 0.78f, 1, 0.1f),
            "an added map set (1, Friede's dark hall) is a copy of the host's set 0, not DS3's black lights");
        Expect(Colour("Directional Light DiffColor1", 111) == new Vector4(1, 1, 1, 0.05f) && Colour("Directional Light DiffColor1", 4) == new Vector4(1, 1, 1, 0.05f),
            "a character set (111 by number, 4 named only by characters) is a copy of the host's set 100");
        Expect(Colour("Directional Light DiffColor0", 100) == new Vector4(1, 0.94f, 0.9f, 0.6f), "the host's set 100 is untouched");
        var enable = light.Params.Single(p => p.Name1 == "Directional Enable LocalLight1");
        Expect((bool)enable.Values[enable.ValueIDs.IndexOf(1)] && !(bool)enable.Values[enable.ValueIDs.IndexOf(111)], "the local light switches follow the copied set, not DS3's");
        var sharp = light.Params.Single(p => p.Name1 == "Directional Light Sharpness0");
        Expect(sharp.Values.All(v => (float)v == 0.3f), "sharpness stays the host's");
        var angle = light.Params.Single(p => p.Name1 == "Directional Light Angle0");
        Expect((Vector2)angle.Values[angle.ValueIDs.IndexOf(0)] == new Vector2(-0.79f, -1.22f) && (Vector2)angle.Values[angle.ValueIDs.IndexOf(1)] == new Vector2(-0.51f, 0.66f)
               && (Vector2)angle.Values[angle.ValueIDs.IndexOf(111)] == new Vector2(-0.79f, -1.57f), "each set keeps its own DS3 sun direction");
        Expect(changes.Any(c => c.Contains("DS3 light sets 1 (暗闇用), 4, 111 (ボス_娘　戦闘) added") && c.Contains("character sets 4, 111 of set 100")), string.Join(" | ", changes));
        Expect(changes.Any(c => c.Contains("are the light reference's")), string.Join(" | ", changes));
        var back = GPARAM.Read(host.Write());
        Expect(MapAtmosphere.Group(back, "LightSet")!.Params.All(p => p.Values.Count == 5), "the file reads back");

        var mixed = new MSBE();
        var hallPiece = new MSBE.Part.MapPiece();
        hallPiece.Gparam.LightSetID = 111;
        var friede = new MSBE.Part.Enemy();
        friede.Gparam.LightSetID = 111;
        var father = new MSBE.Part.Enemy();
        father.Gparam.LightSetID = 120;
        mixed.Parts.MapPieces.Add(hallPiece);
        mixed.Parts.Enemies.AddRange([friede, father]);
        Expect(MapLightSets.CharacterOnly(mixed).SetEquals([120]), "only sets no map piece names count as character sets");

        // Parts: Elden Ring's set for the part's kind, never DS3's (one light set lights the whole frame in Elden Ring,
        // so DS3's per-part sets made the sun flicker); one the draw params lack goes to -1.
        var piece = new MSB3.Part.MapPiece { Name = "m010701_5000", ModelName = "m010701" };
        piece.Gparam.LightSetID = 40;
        var ported = new MSBE.Part.MapPiece();
        ported.Gparam.LightSetID = -1;
        MapLightSets.Carry(piece, ported);
        var floor = new MSB3.Part.Collision { Name = "h002000", ModelName = "h002000" };
        floor.Gparam.LightSetID = 51;
        var portedFloor = new MSBE.Part.Collision();
        MapLightSets.Carry(floor, portedFloor);
        var enemy = new MSB3.Part.Enemy { Name = "c6020_0000", ModelName = "c6020" };
        enemy.Gparam.LightSetID = 111;
        var portedEnemy = new MSBE.Part.Enemy();
        portedEnemy.Gparam.LightSetID = -1;
        MapLightSets.Carry(enemy, portedEnemy);
        var stray = new MSBE.Part.MapPiece();
        stray.Gparam.LightSetID = 999;
        var msb = new MSBE();
        msb.Parts.MapPieces.AddRange([ported, stray]);
        msb.Parts.Collisions.Add(portedFloor);
        msb.Parts.Enemies.Add(portedEnemy);
        Expect(ported.Gparam.LightSetID == 0 && portedFloor.Gparam.LightSetID == 100 && portedEnemy.Gparam.LightSetID == -1,
            "ported parts name Elden Ring's sets (pieces 0, floors 100, characters their template's), not DS3's 40 / 51 / 111");
        Expect(MapLightSets.Wanted(msb).SetEquals([0, 100, 999]), "no DS3 set is wanted by a ported part");
        var line = MapLightSets.Finish(msb, new HashSet<int> { 0, 100, 1000, 1100 });
        Expect(stray.Gparam.LightSetID == -1 && line.Contains("1 on sets the draw params lack"), line);
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
        Expect(sun.Values.All(v => v is Vector4 c && c == new Vector4(0.897f, 0.947f, 0.895f, 0.5f)), "the sun's colour and strength stay the host's (DS3's flickered under ray tracing)");
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
        Expect(MapAtmosphere.Value<float>(fog, "VolumetricFog FarDepth") == MapAtmosphere.FogFarDepth && MapAtmosphere.Value<float>(fog, "VolumetricFog Global HeightFogOffset") == -44f,
            "Stormveil's reach when DS3 writes no fog distances, height fog from the DS3 ground");
        // DS3's reach: Fog-DistOffset + Fog-Dist (Painted World 10 + 60 = 70 m at 0.5), clamped.
        var pwFog = G("Fog", P("Fog-Color", GPARAM.ParamType.Float4, new Vector4(0.455f, 0.537f, 0.627f, 0.5f)),
            P("Fog-Dist", GPARAM.ParamType.Float, 60f), P("Fog-DistOffset", GPARAM.ParamType.Float, 10f));
        Expect(MapAtmosphere.Reach(pwFog) == 70f, $"Painted World reach {MapAtmosphere.Reach(pwFog)}");
        Expect(MapAtmosphere.Reach(G("Fog", P("Fog-Dist", GPARAM.ParamType.Float, 5f))) == MapAtmosphere.MinReach
               && MapAtmosphere.Reach(G("Fog", P("Fog-Dist", GPARAM.ParamType.Float, 2000f), P("Fog-DistOffset", GPARAM.ParamType.Float, 1600f))) == MapAtmosphere.MaxReach,
            "the reach is clamped");
        var (pwDensity, _, _) = MapAtmosphere.DepthFog(0.5f, new MapAtmosphere.FogLayer(0, 100), 70);
        Expect(MathF.Abs(1 - MathF.Exp(-pwDensity * 70) - 0.5f) < 1e-4f, $"half the view hidden at 70 m: density {pwDensity}");
        var pwDs3 = new GPARAM();
        pwDs3.Groups.Add(pwFog);
        var pwHost = new GPARAM();
        pwHost.Groups.Add(G("VolumetricFog", P("VolumetricFog FarDepth", GPARAM.ParamType.Float, 300f), P("VolumetricFog FogEndDepth", GPARAM.ParamType.Float, 300f)));
        MapAtmosphere.Apply(pwHost, pwDs3, 0, layer: new MapAtmosphere.FogLayer(0, 100));
        var pwVolume = MapAtmosphere.Group(pwHost, "VolumetricFog");
        Expect(MapAtmosphere.Value<float>(pwVolume, "VolumetricFog FarDepth") == 70f && MapAtmosphere.Value<float>(pwVolume, "VolumetricFog FogEndDepth") == 70f,
            "the fog's depth and end depth take DS3's reach");
        Expect(MapAtmosphere.Value<int>(MapAtmosphere.Group(host, "ColorGrading"), "Yebis-ColorGrading-Enable") == 0, "colour grading off");
        Expect(changes.Count == 3 && changes[2] == "colour grading off", string.Join(" | ", changes));
        // The light reference (m45_01, the High Wall slot's) replaces a Leyndell host's (m45_00) light and exposure,
        // value ID by value ID; DS3's sun direction is still set after it; a param the reference lacks is kept.
        var leyndell = new GPARAM();
        leyndell.Groups.Add(G("LightSet",
            P("Directional Light Angle0", GPARAM.ParamType.Float2, new Vector2(-0.524f, 1.571f), new Vector2(-0.524f, 1.571f)),
            P("Directional Light SpecColor1", GPARAM.ParamType.Float4, new Vector4(0.667f, 0.741f, 0.843f, 0.2f), new Vector4(0.667f, 0.741f, 0.843f, 0.2f)),
            P("Directional Enable LocalLight1", GPARAM.ParamType.BoolA, true, true),
            P("Directional Light giOcclusionRate", GPARAM.ParamType.Float, 0.3f, 0.3f)));
        leyndell.Groups.Add(G("Tone Map", P("GS Presents - ToneMap-Exposure", GPARAM.ParamType.Float, 3.9f), P("GS Presents - ToneMap-Enable AutoExposure", GPARAM.ParamType.Byte, (byte)1)));
        var caelid = new GPARAM();
        caelid.Groups.Add(G("LightSet",
            P("Directional Light Angle0", GPARAM.ParamType.Float2, new Vector2(-0.873f, 0.873f), new Vector2(-0.873f, 0.873f)),
            P("Directional Light SpecColor1", GPARAM.ParamType.Float4, new Vector4(0.702f, 0.785f, 0.843f, 0), new Vector4(0.702f, 0.741f, 0.843f, 0)),
            P("Directional Enable LocalLight1", GPARAM.ParamType.BoolA, false, false)));
        caelid.Groups.Add(G("Tone Map", P("GS Presents - ToneMap-Exposure", GPARAM.ParamType.Float, 3.15f), P("GS Presents - ToneMap-Enable AutoExposure", GPARAM.ParamType.Byte, (byte)0)));
        Expect(MapAtmosphere.UseReferenceLight(leyndell, caelid) > 0 && MapAtmosphere.UseReferenceLight(leyndell, caelid) == 0,
            "applying the reference twice changes nothing");
        var lit = MapAtmosphere.Apply(leyndell, ds3, -44, lightReference: caelid);
        var leyLight = MapAtmosphere.Group(leyndell, "LightSet")!;
        Expect(leyLight.Params.Single(p => p.Name1 == "Directional Light SpecColor1").Values.All(v => ((Vector4)v).W == 0)
               && leyLight.Params.Single(p => p.Name1 == "Directional Enable LocalLight1").Values.All(v => !(bool)v),
            "the reference's fill light (no specular) and local light switches replace the host's");
        Expect(leyLight.Params.Single(p => p.Name1 == "Directional Light Angle0").Values.All(v => (Vector2)v == new Vector2(-0.768f, -0.96f)), "DS3's sun direction still wins over the reference's");
        Expect(MapAtmosphere.Value<float>(leyLight, "Directional Light giOcclusionRate") == 0.3f, "a param the reference lacks is kept");
        var tone = MapAtmosphere.Group(leyndell, "Tone Map");
        Expect(MapAtmosphere.Value<float>(tone, "GS Presents - ToneMap-Exposure") == 3.15f && tone!.Params.Single(p => p.Name1 == "GS Presents - ToneMap-Enable AutoExposure").Values[0] is (byte)1,
            "exposure 3.15 from the reference, auto exposure on to lift dark scenes");
        Expect(lit.Any(c => c.StartsWith("exposure lifts in dark scenes")), string.Join(" | ", lit));
        var ae = new GPARAM();
        ae.Groups.Add(G("Tone Map", P("GS Presents - ToneMap-Enable AutoExposure", GPARAM.ParamType.Byte, (byte)0),
            P("GS Presents - ToneMap-AutoExposure Adaption Min", GPARAM.ParamType.Float, -8f),
            P("GS Presents - ToneMap-AutoExposure Adaption Max", GPARAM.ParamType.Float, 8f),
            P("GS Presents - ToneMap-AutoExposure Middle Gray", GPARAM.ParamType.Float, -6.4f)));
        var aeTone = MapAtmosphere.Group(ae, "Tone Map");
        Expect(MapAtmosphere.LiftDarkScenes(ae) == 4 && MapAtmosphere.Value<float>(aeTone, "GS Presents - ToneMap-AutoExposure Adaption Min") == 0f
               && MapAtmosphere.Value<float>(aeTone, "GS Presents - ToneMap-AutoExposure Adaption Max") == MapAtmosphere.DarkSceneLift
               && MapAtmosphere.Value<float>(aeTone, "GS Presents - ToneMap-AutoExposure Middle Gray") == MapAtmosphere.DarkSceneMiddleGray,
            "dark scenes: never below the reference's exposure, at most 1.25 steps up, metered like m45_00");


        // One DS3 light scale for every map (MapLightScale), set by the High Wall (DS3's values from its files).
        GPARAM Ds3Light(float sun, float exposure, float sh)
        {
            var g = new GPARAM { Game = GPARAM.GPGame.DarkSouls3 };
            g.Groups.Add(G("LightSet", P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(1, 0.9f, 0.8f, sun))));
            g.Groups.Add(G("Tone Map", P("Yebis-ToneMapExposure", GPARAM.ParamType.Float, exposure)));
            g.Groups.Add(G("Dominant Light", P("Dominant Param  SH Color Scale", GPARAM.ParamType.Float, sh)));
            return g;
        }
        var erRef = new GPARAM();
        erRef.Groups.Add(G("LightSet",
            P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(0.9f, 0.95f, 0.9f, 0.5f), new Vector4(0.9f, 0.95f, 0.9f, 0.5f)),
            P("Directional Light SpecColor0", GPARAM.ParamType.Float4, new Vector4(0.9f, 0.95f, 0.9f, 0.5f), new Vector4(0.9f, 0.95f, 0.9f, 0.5f)),
            P("Directional Light DiffColor1", GPARAM.ParamType.Float4, new Vector4(1, 1, 1, 0.25f), new Vector4(1, 1, 1, 0.25f))));
        var highWall = Ds3Light(1.23f, 2.3f, 0.5f);
        var hw = MapLightScale.For(erRef, highWall, highWall);
        Expect(MathF.Abs(hw.Light - 0.5f / 1.23f) < 1e-4f && hw.Sky == hw.Light && MathF.Abs(hw.Sun - 1) < 1e-4f,
            $"the High Wall keeps its own-sun scale and the reference sun ({hw.Text})");
        var cathedral = MapLightScale.For(erRef, highWall, Ds3Light(3.8f, 2.0f, 1));
        Expect(MathF.Abs(cathedral.Light - 0.5f / 1.23f * 2 / 2.3f) < 1e-4f && MathF.Abs(cathedral.Sky - 2 * cathedral.Light) < 1e-4f
               && MathF.Abs(cathedral.Sun - 3.8f / 1.23f * 2 / 2.3f) < 1e-3f,
            $"the Cathedral: lamps on the High Wall's scale times exposure 2.0/2.3, sky light twice that (SH 1 vs 0.5), DS3's strong sun ({cathedral.Text})");
        var none = MapLightScale.For(null, null, null);
        Expect(MathF.Abs(none.Light - MapLightScale.ErReferenceSun / MapLightScale.Ds3ReferenceSun) < 1e-4f && none.Sky == none.Light && none.Sun == 1,
            "without files the High Wall's measured values are used");
        Expect(MapLightScale.For(erRef, highWall, Ds3Light(50, 2.3f, 0.5f)).Sun == MapLightScale.MaxSun, "the sun is kept inside its range");
        var sunLit = new GPARAM();
        sunLit.Groups.Add(G("LightSet", erRef.Groups[0].Params.Select(p => P(p.Name1, p.Type, p.Values.ToArray())).ToArray()));
        Expect(MapLightScale.ScaleSun(sunLit, 2) == 4, "both sun strengths of every light set are scaled");
        var sunSet = MapAtmosphere.Group(sunLit, "LightSet")!;
        Expect(sunSet.Params.Where(p => p.Name1.Contains("Color0")).SelectMany(p => p.Values).All(v => ((Vector4)v).W == 1f)
               && MapAtmosphere.Value<Vector4>(sunSet, "Directional Light DiffColor1")!.Value.W == 0.25f
               && MapAtmosphere.Value<Vector4>(sunSet, "Directional Light DiffColor0")!.Value.X == 0.9f,
            "only the sun's strength changes, not its colour or the fill light");

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

        // DS3's per-place draw params: a collision's scene sub IDs carry over when the variant was written.
        var sceneDs3 = new MSB3();
        var foggy = new MSB3.Part.Collision { Name = "h004500", ModelName = "h004500" };
        foggy.SceneGparam.EventIDs[0] = 0;
        foggy.SceneGparam.EventIDs[1] = 3;
        foggy.SceneGparam.EventIDs[2] = -1;
        foggy.SceneGparam.EventIDs[3] = 0;
        foggy.SceneGparam.Unk40 = 2;
        var lacking = new MSB3.Part.Collision { Name = "h004600", ModelName = "h004600" };
        lacking.SceneGparam.EventIDs[1] = 9;
        sceneDs3.Parts.Collisions.AddRange([foggy, lacking, new MSB3.Part.Collision { Name = "h004700", ModelName = "h004700" }]);
        var sceneEr = new MSBE();
        foreach (var name in new[] { "h004500", "h004600", "h004700" })
        {
            var c = new MSBE.Part.Collision { Name = name, ModelName = name };
            c.SceneGparam.GparamSubID_Base = 5; // the host colosseum's own setting
            sceneEr.Parts.Collisions.Add(c);
        }
        Expect(MapAtmosphere.SceneSubs(foggy).SequenceEqual([3]), "sub IDs past none");
        Expect(MapAtmosphere.Name(sceneDs3, sceneEr, new HashSet<int> { 3 }) == 1, "one collision names a written variant");
        var sc = sceneEr.Parts.Collisions[0].SceneGparam;
        Expect(sc.GparamSubID_Base == 0 && sc.GparamSubID_Override1 == 3 && sc.GparamSubID_Override2 == -1 && sc.GparamSubID_Override3 == -1 && sc.TransitionTime == 2,
            $"DS3's [0,3,-1,0] -> base 0, override 3, nothing else ({sc.GparamSubID_Base},{sc.GparamSubID_Override1},{sc.GparamSubID_Override2},{sc.GparamSubID_Override3})");
        Expect(sceneEr.Parts.Collisions.Skip(1).All(c => c.SceneGparam.GparamSubID_Base == -1 && c.SceneGparam.GparamSubID_Override1 == -1),
            "a variant DS3 lacks and a collision with none name nothing (not the host's)");
        Expect(MapAtmosphere.GparamPath(new MapId(19, 70, 0, 0), 3) == "/param/drawparam/m19_70_0300.gparam.dcx", "variant file name");

        // Nothing to take from DS3: the host's values stay.
        var plain = new GPARAM();
        plain.Groups.Add(G("VolumetricFog", P("VolumetricFog FarDepth", GPARAM.ParamType.Float, 2000f)));
        MapAtmosphere.Apply(plain, new GPARAM(), 0);
        Expect(MapAtmosphere.Value<float>(MapAtmosphere.Group(plain, "VolumetricFog"), "VolumetricFog FarDepth") == 2000f, "no DS3 fog: the host's fog stays");
        Expect(MapAtmosphere.GparamPath(MapId.Parse("m45_01_00_00")) == "/param/drawparam/m45_01_0000.gparam.dcx", "slot gparam path");
    }
}
