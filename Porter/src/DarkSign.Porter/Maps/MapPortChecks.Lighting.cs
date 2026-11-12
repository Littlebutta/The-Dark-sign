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
        Expect(!byKind["opaque"], "the wall's material still casts shadows");

        // --host-lighting: the template's own setting.
        Expect(MapPorter.Options.Parse(["--host-lighting"]).HostLighting && !new MapPorter.Options().HostLighting, "--host-lighting is read");
        var (hostLit, bnd) = VisualPort();
        hostLit.UnlitCastShadows = true;
        ConvertOne(hostLit, bnd, Model());
        Expect(hostLit.MatbinFiles(0).All(f => !NoShadow(f)), "--host-lighting leaves the sky dome casting");
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
        Expect(MapPorter.Options.Parse(["--no-light-offset"]) is { NoLightOffset: true, BakedLight.NoOffset: true }
               && !MapPorter.Options.Parse([]).NoLightOffset, "--no-light-offset (fallback map-light-place B) reaches lamps and baked light");
    }
}
