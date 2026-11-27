using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Offline checks for what lights a ported map (2026-11-10 playtests: every DS3 map dark, side faces black, no
/// shadows): the sky dome's material casts no shadow, the layout carries an environment map box that holds the
/// whole map, and DS3's lamps are written in the host's light file version. See <see cref="MapLighting"/>.
/// </summary>
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> LightingChecks()
    {
        yield return ("lighting: unlit DS3 materials (sky domes) cast no shadow, lit ones still do", UnlitCastsNoShadow);
        yield return ("lighting: the host's environment map box is carried over, grown to hold the whole DS3 map", AmbientBox);
        yield return ("lighting: DS3 lamps are written in the host's light file version, the DS3 sun is left out", Lamps);
        yield return ("lighting: DS3 lamps move by the map offset onto the layout (Firelink's lights into the shrine)", LampsOffset);
        yield return ("lamps: DS3 strengths take the host's sun scale and stay inside what Elden Ring's own lamps do (the High Wall's statue and door spots)", LampStrengths);
        yield return ("flame lights: a lamp like Stormveil's torch lamps stands at each DS3 flame; near flames share one, a warm DS3 lamp nearby means none", FlameLamps);
    }

    private static void UnlitCastsNoShadow()
    {
        static bool NoShadow(BinderFile file) =>
            (bool)MATBIN.Read(file.Bytes).Params.Single(p => p.Name == MapModelPort.NoShadowParam).Value;
        FLVER2 Model()
        {
            var flver = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN]_l.mtd", ("g_DiffuseTexture", @"N:\x\m40_00_wall_a.tif"));
            flver.Materials.Add(new FLVER2.Material("sky", @"N:\FDP\data\Material\mtd\map\M[A]_NoLight.mtd", 0) { GXIndex = 0 });
            flver.Materials[1].Textures.Add(new FLVER2.Texture { ParamName = "g_DiffuseTexture", Path = @"N:\x\m40_00_sky_50_a.tif", TilingScale = Vector2.One });
            var dome = FLVER2.Read(flver.Write()).Meshes[0];
            dome.MaterialIndex = 1;
            flver.Meshes.Add(dome);
            return flver;
        }

        var (port, hostBnd) = VisualPort();
        ConvertOne(port, hostBnd, Model());
        var byKind = port.Materials.OrderBy(m => m.Name, StringComparer.Ordinal).Zip(port.MatbinFiles(0))
            .ToDictionary(x => x.First.Template.Kind, x => NoShadow(x.Second));
        Expect(byKind.Count == 2, $"a wall and a sky material ({string.Join(", ", byKind.Keys)})");
        Expect(byKind["nolight"], "the sky dome's material casts no shadow");
        Expect(!byKind.Single(k => k.Key is "opaque" or "plain").Value, "the wall's material (no normal map: the albedo-only template) still casts shadows");

    }

    private static void AmbientBox()
    {
        // The colosseum's regions, as read from m45_00_00_00.msb.
        var host = SyntheticHost();
        host.Regions.EnvironmentMapPoints.Add(new MSBE.Region.EnvironmentMapPoint { Name = "Env_Point000", Position = new Vector3(0, 15, 9.493f), UnkT00 = 4 });
        host.Regions.EnvironmentMapEffectBoxes.Add(new MSBE.Region.EnvironmentMapEffectBox
        {
            Name = "Env_Box000", Position = new Vector3(10, -213.286f, -52.507f),
            Shape = new MSB.Shape.Box { Width = 246, Depth = 316, Height = 350 }, TransitionDist = 3,
        });
        // The Cemetery of Ash starts 550 m north of the arena, 60 m down; a far region lies 3 km out.
        var ds3 = SyntheticDs3();
        ds3.Parts.Players[1].Position = new Vector3(-8.5f, -64.8f, 547.9f);
        ds3.Regions.EnvironmentMapPoints.Add(new MSB3.Region.EnvironmentMapPoint { Name = "Env_Point100", Position = new Vector3(2900, 120, -1400) });
        var start = ds3.Parts.Players[1].Position;
        Expect(!MapLighting.Holds(host.Regions.EnvironmentMapEffectBoxes[0], start), "the host's own box ends short of the DS3 start point");

        var result = MsbPort.Convert(ds3, host, Ds3Id, ErId);
        Expect(result.Msb.Regions.EnvironmentMapEffectBoxes.Count == 0, "the bare layout has no environment map region");
        var line = MapLighting.AddAmbient(result.Msb, host, ds3);
        var msb = MSBE.Read(result.Msb.Write());
        var box = msb.Regions.EnvironmentMapEffectBoxes.Single();
        var point = msb.Regions.EnvironmentMapPoints.Single();
        Expect(line.StartsWith("ambient light: the host's environment map"), line);
        Expect(point.Position == new Vector3(0, 15, 9.493f) && point.UnkT00 == 4, "the capture point is the host's, untouched");
        Expect(box.TransitionDist == 3 && box.Rotation == Vector3.Zero, "the box keeps the host's settings");
        foreach (var p in ds3.Parts.Players.Select(e => e.Position).Concat(ds3.Regions.GetEntries().Select(r => r.Position)).Append(point.Position))
            Expect(MapLighting.Holds(box, p), $"the box holds {p}");
        var shape = (MSB.Shape.Box)box.Shape;
        Expect(shape.Width >= 4500 && shape.Depth >= 4500 && shape.Height >= 3500, $"at least as large as Stormveil's catch-all box ({shape.Width} x {shape.Depth} x {shape.Height})");
        // Scenery kilometres out (the Cemetery's far mountains are map pieces 7 km away) doesn't stretch the box.
        var far = SyntheticDs3();
        far.Parts.MapPieces[0].Position = new Vector3(-7198, -1069, 3838);
        var farResult = MsbPort.Convert(far, host, Ds3Id, ErId);
        MapLighting.AddAmbient(farResult.Msb, host, far);
        Expect(farResult.Msb.Regions.EnvironmentMapEffectBoxes.Single().Shape is MSB.Shape.Box { Width: 4500, Depth: 4500, Height: 3500 }, "far scenery leaves the box at Stormveil's size");
        Expect(host.Regions.EnvironmentMapEffectBoxes[0].Shape is MSB.Shape.Box { Width: 246 }, "the host's own region is not changed");

        // A host without the regions: nothing added, and the log says so.
        var bare = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId);
        var none = MapLighting.AddAmbient(bare.Msb, SyntheticHost(), SyntheticDs3());
        Expect(none.Contains("no environment map region") && bare.Msb.Regions.EnvironmentMapEffectBoxes.Count == 0, none);
    }

    private static void Lamps()
    {
        // DS3 lights as the Cemetery of Ash's file holds them (version 6).
        BTL.Light Ds3Light(string name, BTL.LightType type) => new()
        {
            Name = name, Type = type, DiffuseColor = System.Drawing.Color.FromArgb(255, 255, 225, 170), DiffusePower = 7,
            SpecularPower = 7, Position = new Vector3(-1.3f, -57.3f, 536.9f), Radius = 6, Sharpness = 20, ConeAngle = type == BTL.LightType.Spot ? 32 : 0,
            Unk30 = 5, Unk54 = 2, UnkA0 = [1, 0, 2, 1],
        };
        var ds3 = new BTL { Version = 6, LongOffsets = true };
        ds3.Lights.Add(Ds3Light("candle", BTL.LightType.Point));
        ds3.Lights.Add(Ds3Light("candle", BTL.LightType.Point));
        ds3.Lights.Add(Ds3Light("window", BTL.LightType.Spot));
        ds3.Lights.Add(Ds3Light("sun", BTL.LightType.Directional));
        var source = BTL.Read(ds3.Write()).Lights;

        var ported = BTL.Read(MapLighting.Convert(source, 18, true, out var skipped).Write());
        Expect(ported.Version == 18 && ported.Lights.Count == 3 && skipped == 1, $"version {ported.Version}, {ported.Lights.Count} lights, {skipped} left out");
        Expect(ported.Lights.Select(l => l.Name).SequenceEqual(["candle", "candle_2", "window"]), "names stay apart: " + string.Join(", ", ported.Lights.Select(l => l.Name)));
        var candle = ported.Lights[0];
        Expect(candle.DiffusePower == 7 && candle.Radius == 6 && candle.Sharpness == 20 && candle.Position == new Vector3(-1.3f, -57.3f, 536.9f)
               && candle.DiffuseColor.G == 225, "what the lamp is stays DS3's");
        // The values every one of Stormveil's 216 lights holds.
        Expect(ported.Lights.All(l => l.Unk30 == 0 && l.Unk54 == 0 && l.UnkAC == 1 && l.UnkC4 == 20 && l.UnkC8 == 1 && l.UnkCC == 1
                                      && l.UnkD8 == 0.2f && l.UnkE4 == 1 && l.UnkC0.SequenceEqual(new byte[] { 1, 1, 0, 1 })), "Elden Ring's own fields take Elden Ring's values");
        Expect(candle.UnkA0.SequenceEqual(new byte[] { 0, 0, 2, 0 }) && ported.Lights[2].UnkA0.SequenceEqual(new byte[] { 0, 0, 2, 1 })
               && ported.Lights[2].ConeAngle == 32, "point and spot lights are marked as Elden Ring marks them");
        Expect(source[0].Unk54 == 2 && source[0].Name == "candle", "the DS3 lights read are not changed");
        Expect(MapLighting.ErLightPath(ErId) == "/map/m45/m45_00_00_00/m45_00_00_00_0000.btl.dcx"
               && MapLighting.Ds3LightPath(Ds3Id, 1) == "/map/m40_00_00_00/m40_00_00_00_0001.btl.dcx", "light file paths");
    }

    private static void LampStrengths()
    {
        // The High Wall's lamps as DS3's light file holds them: a plaza statue spot, the spot aimed at Vordt's door
        // from 154 m away, the steep-falloff statue spot, and an ordinary lamp.
        BTL.Light Lamp(string name, BTL.LightType type, float power, float reach, float sharpness) =>
            new() { Name = name, Type = type, DiffusePower = power, SpecularPower = power, Radius = reach, Sharpness = sharpness };
        var statue = Lamp("statue spot 001", BTL.LightType.Spot, 20, 23, 20);
        var door = Lamp("door spot", BTL.LightType.Spot, 4, 240, 20);
        var steep = Lamp("statue spot", BTL.LightType.Spot, 40, 23, 0.3f);
        var plain = Lamp("corridor", BTL.LightType.Point, 1.2f, 7, 0.5f);
        var specOnly = new BTL.Light { Name = "spec", Type = BTL.LightType.Point, DiffusePower = 0, SpecularPower = 30, Radius = 10, Sharpness = 20 };
        Expect(MathF.Abs(MapLampStrength.Air(statue) - 438.1f) < 0.5f && MathF.Abs(MapLampStrength.Surface(statue) - 19.32f) < 0.05f
               && MathF.Abs(MapLampStrength.Air(door) - 914.3f) < 0.5f,
            $"DS3's spots as measured: statue air {MapLampStrength.Air(statue)}, surface {MapLampStrength.Surface(statue)}, door air {MapLampStrength.Air(door)}");
        // Stormveil's strongest (spot_食堂の窓A001: 6, reach 20 m, sharpness 1.5) is the limit itself.
        var stormveil = Lamp("spot_食堂の窓A001", BTL.LightType.Spot, 6, 20, 1.5f);
        Expect(MapLampStrength.Fit(stormveil, 1) is null && stormveil.DiffusePower == 6, "Elden Ring's own strongest lamp passes unchanged");

        const float sun = 0.5f / 1.23f;   // the colosseum's sun over the High Wall's
        var changes = new[] { statue, door, steep, plain, specOnly }.Select(l => MapLampStrength.Fit(l, sun)).ToList();
        Expect(changes[3] is null && MathF.Abs(plain.DiffusePower - 1.2f * sun) < 1e-4f, $"an ordinary lamp only takes the sun scale ({plain.DiffusePower})");
        Expect(changes[0] is { Why: "light along its reach", Before: 20 } && MathF.Abs(MapLampStrength.Air(statue) - MapLampStrength.ErMaxAir) < 0.01f
               && MapLampStrength.Surface(statue) <= MapLampStrength.ErMaxSurface && statue.DiffusePower is > 3 and < 3.5f && statue.SpecularPower == statue.DiffusePower,
            $"the statue spot comes down to Elden Ring's limit ({statue.DiffusePower})");
        Expect(changes[1] is not null && door.DiffusePower < 0.35f && MathF.Abs(MapLampStrength.Air(door) - MapLampStrength.ErMaxAir) < 0.01f,
            $"the 240 m door spot comes down the most ({door.DiffusePower})");
        Expect(changes[2] is not null && MapLampStrength.Air(steep) <= MapLampStrength.ErMaxAir + 0.01f && MapLampStrength.Surface(steep) <= MapLampStrength.ErMaxSurface,
            "a strong lamp with a steep falloff is measured with its falloff");
        Expect(changes[4] is { Why: "light on surfaces" } && specOnly.DiffusePower == 0 && MathF.Abs(MapLampStrength.Surface(specOnly) - MapLampStrength.ErMaxSurface) < 0.01f,
            "a lamp that only lights reflections is measured by that strength");
        Expect(statue.Radius == 23 && statue.Sharpness == 20, "reach and falloff stay DS3's");

        GPARAM Sun(float strength, bool ds3)
        {
            var g = ds3 ? new GPARAM { Game = GPARAM.GPGame.DarkSouls3 } : new GPARAM();
            g.Groups.Add(G("LightSet", P("Directional Light DiffColor0", GPARAM.ParamType.Float4, new Vector4(1, 0.95f, 0.87f, strength))));
            return g;
        }
        Expect(MathF.Abs(MapLampStrength.SunScale(Sun(0.5f, false), Sun(1.23f, true)) - sun) < 1e-4f, "sun scale: the host's sun strength over DS3's");
        Expect(MapLampStrength.SunScale(null, Sun(1.23f, true)) == 1 && MapLampStrength.SunScale(Sun(0.5f, false), new GPARAM()) == 1, "sun scale: 1 when either file has no sun");
        Expect(MapLampStrength.SunScale(Sun(0.5f, false), Sun(50, true)) == MapLampStrength.MinSunScale
               && MapLampStrength.SunScale(Sun(5, false), Sun(0.5f, true)) == MapLampStrength.MaxSunScale, "sun scale: kept inside its range");
        Expect(MapLampStrength.Describe(sun, changes.OfType<MapLampStrength.Change>().ToList(), 5).Contains("4 of 5 turned down")
               && MapLampStrength.Describe(1, [], 3).Contains("every lamp is inside"), "the log line names what was turned down");
    }

    private static void FlameLamps()
    {
        // DS3's rows: the lit candle (effect 839010 on dummy 200), a lantern with a light of its own, a blue crystal,
        // smoke, and a candle whose effect didn't convert.
        var def = new PARAMDEF { ParamType = "MODEL_SFX_PARAM_ST", DataVersion = 1 };
        for (int i = 1; i <= ObjectSfx.Ds3Slots; i++)
        {
            def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.s32, $"VfxId{i}"));
            def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.s32, $"DummyPolyId{i}"));
        }
        PARAM.Row Row(int id, int effect, int dummy)
        {
            var row = new PARAM.Row(id, "", def);
            row["VfxId1"].Value = effect;
            row["DummyPolyId1"].Value = dummy;
            return row;
        }
        var ds3Param = new PARAM { ParamType = def.ParamType, Rows =
            [Row(391000000, 839010, 200), Row(391000001, 839012, 200), Row(392000000, 840060, 200), Row(393000000, 831000, 200), Row(394000000, 830050, 200)] };
        var converted = new[] { 839010, 840060, 831000, 830050 }.ToDictionary(id => id, id => FxrPort.IdBase + id);
        var looks = new Dictionary<int, FxrPort.Look>
        {
            [839010] = new(0, 2, [1, 0.8f, 0.5f]),      // a flame
            [839012] = new(0, 2, [1, 0.8f, 0.5f]),      // a flame that didn't convert: nothing burns there
            [840060] = new(1, 1, [1, 0.8f, 0.5f]),      // carries its own light
            [831000] = new(0, 1, [0.3f, 0.6f, 1]),      // cold
            [830050] = new(0, 0, null),                 // smoke
        };
        Expect(looks[839010].IsFlame && !looks[840060].IsFlame && !looks[831000].IsFlame && !looks[830050].IsFlame && new FxrPort.Look(0, 1, [1, 1, 1]).IsFlame
               && !new FxrPort.Look(0, 1, null).IsFlame, "a flame glows warm or white without a light of its own");

        var ds3 = SyntheticDs3();
        var names = new Dictionary<string, string>();
        void Obj(string name, string model, short rel, Vector3 at, float yaw = 0, bool placed = true)
        {
            var o = new MSB3.Part.Object { Name = name, ModelName = model, Position = at, Rotation = new Vector3(0, yaw, 0) };
            o.ModelSfxParamRelativeIDs[0] = rel;
            ds3.Parts.Objects.Add(o);
            if (placed)
                names[name] = "AEG920_000_" + names.Count;
        }
        Obj("o391000_1000", "o391000", 0, new Vector3(10, 0, 10));             // a table of three candles
        Obj("o391000_1001", "o391000", 0, new Vector3(11, 0, 10));
        Obj("o391000_1002", "o391000", 0, new Vector3(10, 0, 11), yaw: 90);
        Obj("o391000_1003", "o391000", 0, new Vector3(40, 0, 10));             // one alone
        Obj("o391000_1004", "o391000", 0, new Vector3(80, 0, 10));             // one beside a warm DS3 lamp
        Obj("o391000_1005", "o391000", -1, new Vector3(120, 0, 10));           // unlit
        Obj("o391000_1006", "o391000", 1, new Vector3(140, 0, 10));            // effect not converted
        Obj("o391000_1007", "o391000", 0, new Vector3(160, 0, 10), placed: false);   // not placed (another map state)
        Obj("o392000_1000", "o392000", 0, new Vector3(180, 0, 10));            // lantern with its own light
        Obj("o393000_1000", "o393000", 0, new Vector3(200, 0, 10));            // blue crystal
        Obj("o394000_1000", "o394000", 0, new Vector3(220, 0, 10));            // smoke
        // The candle's flame is 0.4 m up and 0.2 m along the model's X.
        var points = new Dictionary<int, Vector3> { [200] = new(0.2f, 0.4f, 0) };
        var tally = new Dictionary<int, (int Parts, string Why)>();
        var flames = FlameLights.Flames(ds3, names, ds3Param, converted, looks, m => m == "o391000" ? points : null, tally);
        Expect(flames.Count == 5 && flames.All(f => f.Effect == 839010), $"five lit, placed, converted candles burn ({flames.Count})");
        Expect(Vector3.Distance(flames[0].Position, new Vector3(10.2f, 0.4f, 10)) < 1e-3f, $"a flame stands at its dummy point ({flames[0].Position})");
        Expect(MathF.Abs(flames[2].Position.Y - 0.4f) < 1e-3f && MathF.Abs(MathF.Abs(flames[2].Position.Z - 11) - 0.2f) < 1e-3f && MathF.Abs(flames[2].Position.X - 10) < 1e-3f,
            $"the dummy point turns with the part ({flames[2].Position})");
        Expect(tally[840060] == (1, "carries its own light") && tally[831000].Why == "cold glow" && tally[830050].Why == "draws nothing that glows" && tally[839010].Parts == 5,
            "the log says why each object effect did or didn't get lamps");

        // The slot's light file: a blue DS3 fill beside the table, a warm DS3 lamp beside the candle at x 80.
        var btl = new BTL { Version = MapLighting.ErLightVersion, LongOffsets = true };
        btl.Lights.Add(new BTL.Light { Name = "fill", Position = new Vector3(10, 1, 10), DiffusePower = 1.4f, DiffuseColor = System.Drawing.Color.FromArgb(255, 150, 195, 255) });
        btl.Lights.Add(new BTL.Light { Name = "warm", Position = new Vector3(80.5f, 0.5f, 10), DiffusePower = 1, DiffuseColor = System.Drawing.Color.FromArgb(255, 255, 200, 120) });
        var result = FlameLights.Build(btl, [.. flames.Select(f => f.Position)], "m45_00_00_00");
        var lamps = btl.Lights.Where(l => l.Name.StartsWith(FlameLights.NamePrefix)).ToList();
        Expect(result is { Lamps: 2, Flames: 5, NearDs3Lamp: 1 } && lamps.Count == 2 && btl.Lights.Count == 4, $"two lamps for five flames, one by a warm DS3 lamp ({result})");
        var table = lamps.Single(l => l.Position.X < 20);
        var alone = lamps.Single(l => l.Position.X > 20);
        Expect(Vector3.Distance(table.Position, (flames[0].Position + flames[1].Position + flames[2].Position) / 3) < 1e-3f, "near flames share a lamp in their middle");
        Expect(alone.DiffusePower == FlameLights.Strength && alone.Radius == FlameLights.Reach && table.DiffusePower > alone.DiffusePower && table.Radius > alone.Radius
               && table.DiffusePower <= FlameLights.MaxStrength, "a shared lamp is a little stronger and reaches farther");
        // Every field of Stormveil's touch_室内R_009 but place, strength and reach.
        Expect(alone is { Type: BTL.LightType.Point, CastShadows: false, Unk1C: true, Unk50: 4, Unk5C: -1, Unk80: -1, Unk98: 1, NearClip: 1, UnkAC: 1, UnkC4: 20, UnkC8: 1, UnkCC: 1,
                   UnkD8: 0.2f, UnkE4: 1, FlickerBrightnessMult: 1 } && alone.SpecularPower == alone.DiffusePower && alone.DiffuseColor.R == 240 && alone.DiffuseColor.G == 180
               && alone.DiffuseColor.B == 120 && alone.UnkA0.SequenceEqual(new byte[] { 0, 0, 2, 0 }) && alone.UnkC0.SequenceEqual(new byte[] { 1, 1, 0, 1 }),
            "the lamp has the settings of Elden Ring's torch lamps");
        Expect(MapLampStrength.Fit(FlameLights.NewLamp("x", "y"), 1) is null, "a flame lamp is inside Elden Ring's limits");
        Expect(!alone.Unk00.SequenceEqual(table.Unk00) && alone.Unk00.Any(b => b != 0), "each lamp carries its own ID bytes");
        var read = BTL.Read(btl.Write());
        Expect(read.Lights.Count == 4 && read.Lights[3].Name == alone.Name && read.Lights[3].Unk00.SequenceEqual(alone.Unk00) && read.Lights[3].Radius == alone.Radius,
            "the light file reads back with its flame lamps");
        // A rerun replaces the lamps it added; no flames, no lamps.
        FlameLights.Build(btl, [.. flames.Select(f => f.Position)], "m45_00_00_00");
        Expect(btl.Lights.Count == 4, $"a rerun keeps two flame lamps ({btl.Lights.Count})");
        Expect(FlameLights.Build(btl, [], "m45_00_00_00").Lamps == 0 && btl.Lights.Count == 2, "with no flames the lamps are taken out again");
        // A map with more flames than lamps allowed merges them over a wider distance.
        var many = Enumerable.Range(0, 900).Select(i => new Vector3(i % 30 * 5, 0, i / 30 * 5)).ToList();
        var wide = FlameLights.Build(new BTL { Version = 18, LongOffsets = true }, many, "m45_02_00_00");
        Expect(wide.Lamps <= FlameLights.MaxLamps && wide.Lamps > 20 && wide.MergedWithin > FlameLights.MergeDistance, $"900 spread flames stay under the cap ({wide})");
    }

    private static void LampsOffset()
    {
        // m40's map offset event and two of its lamps as DS3's light file holds them (map space).
        var msb = new MSB3();
        msb.Events.MapOffsets.Add(new MSB3.Event.MapOffset { Name = "map offset", Position = new Vector3(-203.6f, 7.3f, 33.5f), Degree = 28 });
        var hub = new BTL.Light { Name = "hub_001", Type = BTL.LightType.Point, Position = new Vector3(149.9f, -58.0f, 727.9f), Radius = 12, UnkA0 = [0, 0, 2, 0] };
        var spot = new BTL.Light { Name = "spot_001", Type = BTL.LightType.Spot, Position = new Vector3(12.4f, -57.9f, 562.8f), Rotation = new Vector3(0.68f, -2.90f, 0), UnkA0 = [0, 0, 2, 1] };
        var offset = MapNavmesh.MapOffset(msb) ?? throw new Exception("no offset read");
        var ported = MapLighting.Convert([hub, spot], 18, true, out _, offset, MapLighting.OffsetDegrees(msb)).Lights;
        // Firelink Shrine's floor in the layout (the Firekeeper stands at 271, -56, 605).
        Expect(Vector3.Distance(ported[0].Position, new Vector3(271, -50.7f, 605)) < 4, $"Firelink's lamp lands in the shrine: {ported[0].Position}");
        float turned = ported[1].Rotation.Y - spot.Rotation.Y;
        Expect(MathF.Abs(MathF.IEEERemainder(turned - 28 * MathF.PI / 180, 2 * MathF.PI)) < 1e-4f && ported[1].Rotation.X == 0.68f,
            $"a spot's yaw turns with the map ({turned} rad)");
        Expect(hub.Position == new Vector3(149.9f, -58.0f, 727.9f), "the DS3 lights read are not changed");
        var none = MapLighting.Convert([hub], 18, true, out _).Lights[0];
        Expect(none.Position == hub.Position, "no offset, no move (m30 has none)");
    }
}
