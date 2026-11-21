using System.Globalization;
using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The colour of a slot's light and air. A slot used to keep its Elden Ring colosseum's draw params (gparam), made
/// for a small arena: Caelid's red fog lit at 1.6 (map 2 looked red), Leyndell's golden colour grading (LUT 101) and
/// thick fog out to 2 km (map 1's mountains hidden). This writes the slot's own gparam from the host's, so every
/// format detail and every setting not named here stays Elden Ring's, with:
/// <list type="bullet">
/// <item>LightSet: the sun's angle and the colour (red, green, blue) of the sun and the second directional light from
/// DS3's light set 0, the one nearly every DS3 map piece names (m30_00: 2,049 of 2,273; m31_00: 2,808; m40_00:
/// 3,502). How strong the sun is (the fourth value) stays Elden Ring's: DS3's strengths are on another scale.
/// The sun's direction is turned by DS3's map offset (<see cref="SunTurn"/>).</item>
/// <item>LightSet, the lights without shadows (<see cref="FillLights"/>): the second directional light and the two
/// "local" directional lights take DS3's strength in DS3's proportion to its sun, and the sun and second light are
/// dimmed by screen-space occlusion as on Stormveil. The colosseums light their open arenas with these far more than
/// DS3 or a legacy dungeon does.</item>
/// <item>VolumetricFog: the colours from DS3's fog 0 (Fog-Color for the sunward glow, heightColor for the rest), each
/// scaled by its own DS3 strength (<see cref="FogColours"/>); how
/// far the fog reaches and how strongly it is lit come from Stormveil Castle (m10_00, an outdoor castle like DS3's
/// maps, its first time of day); how thick it is comes from DS3's fog 0 strength at the height the map is played at
/// (<see cref="DepthFog"/>).</item>
/// <item>ColorGrading off, so no colosseum's tint is laid over DS3's colours.</item>
/// </list>
/// Exposure, bloom and every other group stay the host's. Fallback "map-atmosphere" B (--host-atmosphere) keeps the
/// host's file untouched.
/// </summary>
public static class MapAtmosphere
{
    // Stormveil Castle m10_00_0000.gparam, first time of day (read 2026-11-11).
    public const float FogFarDepth = 300, FogEndDepth = 300, FogDistribution = 0.06f, FogDensity = 0.004f;
    public const float FogDirectStrength = 0.3f, FogOppositeStrength = 0.05f, FogSkyStrength = 0.05f;

    /// <summary>
    /// The strength (fourth value) of DS3's fog 0 colour on the maps whose fog looked right after the port took DS3's
    /// colours: m40_00 0.256, m31_00 0.268 (read 2026-11-13). DS3 keeps a colour and how strongly it applies apart, as
    /// Elden Ring does with its light colours. m30_00's fog colour is a bright cream (1, 0.932, 0.814) at 0.12, less
    /// than half that: written as the colour alone, map 2's haze came out more than twice as bright as maps 1 and 3
    /// and cream-yellow where DS3 shows a dim neutral one. The colour is scaled by its strength over this.
    /// </summary>
    public const float Ds3FogStrength = 0.26f;

    /// <summary>Fallback "map-atmosphere" C: DARKSIGN_FOG_STRENGTH=0 writes DS3's fog colours without their
    /// strengths, as builds up to 1.0.44x did.</summary>
    public static bool FogStrengthEnabled => Environment.GetEnvironmentVariable("DARKSIGN_FOG_STRENGTH") != "0";

    /// <summary>
    /// DS3's fog colours with their own strengths applied: the distance colour times its strength over
    /// <see cref="Ds3FogStrength"/>, the height colour times its strength (DS3 writes 1 or nearly where it draws height
    /// fog). A height colour of strength 0 means DS3 draws no height fog there (m30_00), so the rest of the fog takes
    /// the distance colour. No channel goes above 1.
    /// </summary>
    public static (Vector4 Direct, Vector4 Others) FogColours(Vector4 colour, Vector4? height, bool strengths = true)
    {
        if (!strengths)
            return (colour, height ?? colour);
        static Vector4 Scaled(Vector4 c, float by)
        {
            var v = new Vector3(c.X, c.Y, c.Z) * Math.Max(0, by);
            var max = Math.Max(v.X, Math.Max(v.Y, v.Z));
            if (max > 1)
                v /= max;
            return new Vector4(v, c.W);
        }
        var direct = Scaled(colour, colour.W / Ds3FogStrength);
        var rest = height is { } h && h.W > 0 ? Scaled(h, Math.Min(1, h.W)) : direct;
        return (direct, rest);
    }

    public static string GparamPath(MapId id) => $"/param/drawparam/m{id.Area:D2}_{id.Block:D2}_0000.gparam.dcx";

    /// <summary>DS3's own draw params of a map, by archive path (the .dcx form first).</summary>
    public static IEnumerable<string> Ds3GparamPaths(MapId id)
    {
        yield return $"/param/drawparam/m{id.Area:D2}_{id.Block:D2}_0000.gparam.dcx";
        yield return $"/param/drawparam/m{id.Area:D2}_{id.Block:D2}_0000.gparam";
    }

    /// <summary>port-maps step: writes the slot's gparam. Returns a line for the log.</summary>
    public static string Write(PorterContext ctx, GameArchives ds3, GameArchives er, MapId ds3Id, MapId erId, MapId hostId, MSB3 ds3Msb, bool hostAtmosphere,
        bool unturnedSun = false, bool hostFillLights = false, MSBE? layout = null, ISet<int>? lightSets = null, bool oneLightSet = false)
    {
        if (hostAtmosphere)
            return "atmosphere: the host's own colours, fog and colour grading (--host-atmosphere)";
        var hostPath = GparamPath(hostId);
        if (!er.Contains(hostPath))
            return $"atmosphere: the host has no draw params ({hostPath.TrimStart('/')}); nothing written";
        var source = Ds3GparamPaths(ds3Id).FirstOrDefault(ds3.Contains);
        if (source is null)
            return "atmosphere: the DS3 map has no draw params; the host's stay";
        var (hostBytes, compression) = er.ReadWithCompression(hostPath);
        var gparam = GPARAM.Read(hostBytes);
        var ds3Draw = GPARAM.Read(ds3.Read(source));
        var wanted = layout is null || oneLightSet ? null : MapLightSets.Wanted(layout);
        var characters = wanted is null ? null : MapLightSets.CharacterOnly(layout!);
        var changes = Apply(gparam, ds3Draw, GroundHeight(ds3Msb), unturnedSun ? 0 : MapLighting.OffsetDegrees(ds3Msb), hostFillLights, wanted, Layer(ds3Msb), characters);
        if (unturnedSun && MapLighting.OffsetDegrees(ds3Msb) != 0)
            changes.Add("sun direction as DS3's file writes it, not turned with the map (--unturned-sun)");
        var target = ctx.ModPath(MapPorter.ModParts(GparamPath(erId)));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, DCX.Compress(gparam.Write(), compression));
        if (lightSets is not null && Group(gparam, "LightSet") is { } written)
            lightSets.UnionWith(MapLightSets.Ids(written));
        if (layout is not null && ScenesEnabled)
            changes.Add(WriteScenes(ctx, ds3, ds3Id, erId, ds3Msb, layout, hostBytes, compression,
                (host, draw) => Apply(host, draw, GroundHeight(ds3Msb), unturnedSun ? 0 : MapLighting.OffsetDegrees(ds3Msb), hostFillLights, wanted, Layer(ds3Msb), characters)));
        return "atmosphere: " + string.Join("; ", changes);
    }

    /// <summary>Fallback "map-atmosphere" F: DARKSIGN_SCENE_GPARAMS=0 gives every place the map's one set of draw
    /// params again (no DS3 per-place fog and light), as builds up to 1.0.55x.</summary>
    public static bool ScenesEnabled => Environment.GetEnvironmentVariable("DARKSIGN_SCENE_GPARAMS") != "0";

    /// <summary>The draw params file of a map's place variant <paramref name="sub"/> (1-99: mAA_BB_0N00), the one a
    /// collision's scene setting switches to while the player stands on it.</summary>
    public static string GparamPath(MapId id, int sub) => $"/param/drawparam/m{id.Area:D2}_{id.Block:D2}_{sub:D2}00.gparam.dcx";

    /// <summary>DS3's own draw params of a map's place variant, by archive path (the .dcx form first).</summary>
    public static IEnumerable<string> Ds3GparamPaths(MapId id, int sub)
    {
        yield return $"/param/drawparam/m{id.Area:D2}_{id.Block:D2}_{sub:D2}00.gparam.dcx";
        yield return $"/param/drawparam/m{id.Area:D2}_{id.Block:D2}_{sub:D2}00.gparam";
    }

    /// <summary>The place variants a DS3 collision's scene setting names (the four slots, -1 or 0 for none past the first).</summary>
    public static IEnumerable<int> SceneSubs(MSB3.Part.Collision c) =>
        c.SceneGparam?.EventIDs?.Where(id => id > 0).Select(id => (int)id) ?? [];

    /// <summary>
    /// DS3's draw params per place (2026-11-21 playtest: "the painted world is too clear, it looks like it doesn't
    /// have any of its weather effects"). DS3 changes a map's fog and light from place to place: a collision's scene
    /// setting (MSB3 SceneGparam: four sub IDs, read 2026-11-21) names a variant file mAA_BB_0N00.gparam the game
    /// switches to while the player stands on it. The Painted World ships eight (m45_00_0100 to 0800, fog reaching
    /// 12 to 100 m at 0.25 to 1 of the view, far thicker than its base file's) and 77 of its 187 collisions name one.
    /// The port read only the base file (mAA_BB_0000), so the whole map had its one, lightest fog. Elden Ring has the
    /// same switch (MSBE collision SceneGparam: base and three overrides, the "hundredths place" of the gparam file),
    /// so each variant DS3 names is written for the slot from the host's base file with the variant's colours and fog
    /// (<paramref name="apply"/>, the same conversion as the base file), and each ported collision takes DS3's sub
    /// IDs. A variant DS3 lacks is not named. Every other ported collision gets "none", so no collision keeps the host
    /// colosseum's settings. Returns a line for the log.
    /// </summary>
    public static string WriteScenes(PorterContext ctx, GameArchives ds3, MapId ds3Id, MapId erId, MSB3 ds3Msb, MSBE layout,
        byte[] hostBytes, DCX.CompressionInfo compression, Func<GPARAM, GPARAM, List<string>> apply)
    {
        var wanted = ds3Msb.Parts.Collisions.SelectMany(SceneSubs).Where(n => n <= 99).Distinct().Order().ToList();
        var written = new SortedSet<int>();
        var missing = new List<int>();
        foreach (var sub in wanted)
        {
            var source = Ds3GparamPaths(ds3Id, sub).FirstOrDefault(ds3.Contains);
            if (source is null)
            {
                missing.Add(sub);
                continue;
            }
            var gparam = GPARAM.Read(hostBytes);
            apply(gparam, GPARAM.Read(ds3.Read(source)));
            var target = ctx.ModPath(MapPorter.ModParts(GparamPath(erId, sub)));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, DCX.Compress(gparam.Write(), compression));
            written.Add(sub);
        }
        int named = Name(ds3Msb, layout, written);
        if (wanted.Count == 0)
            return "places: DS3 changes no draw params by place on this map";
        return $"places: {written.Count} of DS3's per-place draw params written (sub IDs {string.Join(", ", written)}), named by {named} collisions"
               + (missing.Count > 0 ? $"; DS3 lacks {string.Join(", ", missing)}" : "");
    }

    /// <summary>
    /// Sets each ported collision's scene setting from its DS3 collision (same name), keeping only the sub IDs in
    /// <paramref name="available"/> (index 0 may be 0, the base file); every other collision gets none (-1).
    /// Returns how many collisions name a variant.
    /// </summary>
    public static int Name(MSB3 ds3Msb, MSBE layout, IReadOnlySet<int> available)
    {
        var byName = ds3Msb.Parts.Collisions.GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        int named = 0;
        foreach (var part in layout.Parts.Collisions)
        {
            var scene = new MSBE.Part.SceneGparamConfig();
            if (byName.TryGetValue(part.Name, out var src) && src.SceneGparam?.EventIDs is { } ids
                && ids.Any(id => id > 0 && available.Contains(id)))
            {
                sbyte Pick(int i, bool first) => i < ids.Length && (ids[i] > 0 && available.Contains(ids[i]) || first && ids[i] == 0) ? ids[i] : (sbyte)-1;
                scene.GparamSubID_Base = Pick(0, true);
                scene.GparamSubID_Override1 = Pick(1, false);
                scene.GparamSubID_Override2 = Pick(2, false);
                scene.GparamSubID_Override3 = Pick(3, false);
                scene.TransitionTime = src.SceneGparam.Unk40;
                named++;
            }
            part.SceneGparam = scene;
        }
        return named;
    }

    /// <summary>
    /// Where the DS3 map's ground is: the height that only one in twenty of its enemies and player points stand
    /// below. Elden Ring's height fog thickens below its offset, and DS3's maps sit far from height 0 (Firelink Shrine
    /// near -60, m31_00 near -200), so the fog starts there instead of at Stormveil's 0 and the
    /// places the player walks get Stormveil's thickness or less. Collisions can't tell: m30_00's all stand at 0
    /// with their floors in the model, m40_00's all at 7. Measured: m40_00 -98, m30_00 -44, m31_00 -209.
    /// </summary>
    public static float GroundHeight(MSB3 ds3)
    {
        var heights = ds3.Parts.Enemies.Select(e => e.Position.Y).Concat(ds3.Parts.Players.Select(p => p.Position.Y)).Order().ToList();
        return heights.Count == 0 ? 0 : heights[heights.Count / 20];
    }

    /// <summary>
    /// The sun's angle (elevation, yaw in radians) in the layout's space: DS3's yaw turned by the map offset's
    /// <paramref name="offsetDegrees"/>. DS3 keeps a map's lighting in the map's own space, like its lamps (.btl) and
    /// light probes (.btpb), which the map offset moves into place (<see cref="MapLighting.PortLights"/>,
    /// <see cref="MapBakedLight.ReadProbes"/>); the map pieces themselves are already placed in the layout. Measured on
    /// Firelink Shrine's probes (map 1, offset 28 degrees), whose first-order terms carry DS3's baked sunlight: in the
    /// map's own space their sunward pull peaks at a yaw of 90 to 100 degrees, where light set 0 puts the sun (89),
    /// not at the 61 the unturned angle would mean there (<see cref="MapBakedLight.SunYawFromProbes"/>). Up to this
    /// change the sun kept DS3's angle as written, so on Undead Settlement (map 3, offset -156 degrees) it shone from
    /// almost the opposite side to the one DS3's baked shade was made for: the realtime shadows fell where DS3's
    /// baked light (now the rooms' and streets' light volumes) is brightest, and the shade DS3 baked lay where Elden
    /// Ring's sun shines, so roofs seemed to cast shade in some places and none in others. Map 2 has no offset.
    /// </summary>
    public static Vector2 SunTurn(Vector2 angle, float offsetDegrees)
    {
        var yaw = angle.Y + offsetDegrees * MathF.PI / 180f;
        // Kept within -pi to pi like DS3's and Elden Ring's own values.
        yaw = MathF.IEEERemainder(yaw, 2 * MathF.PI);
        return angle with { Y = yaw };
    }

    /// <summary>Stormveil Castle's screen-space occlusion weights for the sun, the second directional light and the
    /// fourth weight (m10_00_0000.gparam: 1, 1, 0, 1; the third stays the host's).</summary>
    public const float SsaoWeight = 1;

    /// <summary>
    /// The lights of a light set that cast no shadow, brought to DS3's proportion (2026-11-19 playtest: "lighting
    /// across all the maps looks like it could use baked lighting"; 2026-11-13: map 3's roofs and objects cast little
    /// shade). The port kept the host's strength for every light, and the colosseums fill their open arenas with
    /// light that has no shadow:
    /// <list type="bullet">
    /// <item>The second directional light, a blue light straight down from the sky (Angle1 -1.571 in the hosts and
    /// in DS3): m45_00 0.10 beside a sun of 0.6, m45_01 0.25 beside 0.5, m45_02 0.40 beside 0.4 (as strong as the
    /// sun). Stormveil Castle's is 0. DS3's own is 0.1 beside a sun of 0.8 (Firelink), 0.1 beside 1.23 (High Wall)
    /// and 0.2 beside 1.5 (Undead Settlement): 8 to 13% of the sun, where the port had 17%, 50% and 100%. Only the
    /// sun casts shadows, so on map 3 every shadow was filled back in with light as strong as the sun's, and rooms
    /// were lit from above through their roofs.</item>
    /// <item>The "local" directional lights 1 and 2: m45_00 has the first switched on at 0.10 (reflections 0.23);
    /// DS3's light set 0 leaves both without light on all three maps (black, or strength 0).</item>
    /// <item>How far screen-space occlusion (the shade in corners and under things) dims the sun and the second
    /// light: m45_00 0.5 and 0, m45_01 1 and 0; Stormveil and m45_02 1 and 1.</item>
    /// </list>
    /// So each of these lights takes DS3's colour and DS3's strength times the host's sun strength over DS3's (the
    /// scale the lamps take, <see cref="MapLampStrength.SunScale"/>), the local lights are on where DS3 has them on,
    /// and the occlusion weights are Stormveil's. The hemisphere light stays the host's (the hosts and Stormveil
    /// agree on it). "--host-fill-lights" (fallback "map-fill-light" B) keeps the host's strengths.
    /// Returns what changed, for the log; nothing when either file lacks a sun strength.
    /// </summary>
    public static string? FillLights(GPARAM.Group light, GPARAM.Group ds3Light, float sunScale)
    {
        var inv = CultureInfo.InvariantCulture;
        var notes = new List<string>();
        foreach (var (name, label) in new[]
                 {
                     ("Directional Light DiffColor1", "second light"), ("Directional Light SpecColor1", ""),
                     ("Directional Light LocalLight DiffColor1", "local light 1"), ("Directional Light LocalLight SpecColor1", ""),
                     ("Directional Light LocalLight DiffColor2", "local light 2"), ("Directional Light LocalLight SpecColor2", ""),
                 })
        {
            if (Value<Vector4>(ds3Light, name) is not { } ds3Colour || Value<Vector4>(light, name) is not { } old)
                continue;
            var value = new Vector4(ds3Colour.X, ds3Colour.Y, ds3Colour.Z, ds3Colour.W * sunScale);
            SetAll(light, name, v => v is Vector4 ? value : v);
            // What the light gives: its strength times its brightest channel (DS3 switches a light off by making it black).
            static float Gives(Vector4 c) => c.W * Math.Max(c.X, Math.Max(c.Y, c.Z));
            if (label.Length > 0)
                notes.Add(string.Create(inv, $"{label} {Gives(old):0.###} -> {Gives(value):0.###}")
                          + (Gives(ds3Colour) <= 0 ? " (DS3 has it dark)" : string.Create(inv, $" (DS3's {Gives(ds3Colour):0.###})")));
        }
        foreach (var name in new[] { "Directional Enable LocalLight1", "Directional Enable LocalLight2" })
            if (Value<bool>(ds3Light, name) is { } on)
                SetAll(light, name, v => v is bool ? on : v);
        int weights = 0;
        foreach (var name in new[] { "Directional Light SsaoWeight0", "Directional Light SsaoWeight1", "Directional Light SsaoWeight3" })
            weights += SetAll(light, name, v => v is float ? SsaoWeight : v);
        if (notes.Count == 0 && weights == 0)
            return null;
        return string.Create(inv, $"lights without shadows at DS3's share of the sun (x{sunScale:0.##}): ") + string.Join(", ", notes)
               + (weights > 0 ? "; corner shade (SSAO) on the sun and second light as on Stormveil" : "");
    }

    /// <summary>Changes the host's draw params in place. Returns what changed, for the log.</summary>
    /// <param name="lightSets">The light sets the layout's parts name: DS3's sets among them that the host lacks are
    /// added (<see cref="MapLightSets.Add"/>).</param>
    /// <param name="characterSets">The sets only characters name (<see cref="MapLightSets.CharacterOnly"/>).</param>
    public static List<string> Apply(GPARAM gparam, GPARAM ds3, float ground, float sunTurnDegrees = 0, bool hostFillLights = false,
        IEnumerable<int>? lightSets = null, FogLayer? layer = null, IReadOnlySet<int>? characterSets = null)
    {
        var inv = CultureInfo.InvariantCulture;
        string C(Vector4 v) => string.Create(inv, $"{v.X:F2} {v.Y:F2} {v.Z:F2}");
        var changes = new List<string>();

        var ds3Light = Group(ds3, "LightSet");
        var sunColour = Value<Vector4>(ds3Light, "Directional Light DiffColor0");
        var sunAngle = Value<Vector2>(ds3Light, "Directional Light Angle0");
        var light = Group(gparam, "LightSet");
        // Before anything is changed: the host's sun strength over DS3's.
        float sunScale = MapLampStrength.SunScale(gparam, ds3);
        bool sunStrengths = Value<Vector4>(light, "Directional Light DiffColor0") is { W: > 0 } && sunColour is { W: > 0 };
        if (light is not null && ds3Light is not null)
        {
            int set = 0;
            foreach (var name in new[] { "Directional Light DiffColor0", "Directional Light SpecColor0", "Directional Light DiffColor1", "Directional Light SpecColor1" })
                if (Value<Vector4>(ds3Light, name) is { } colour)
                    set += SetAll(light, name, v => v is Vector4 old ? new Vector4(colour.X, colour.Y, colour.Z, old.W) : v);
            var turned = sunAngle is { } a0 ? SunTurn(a0, sunTurnDegrees) : (Vector2?)null;
            if (turned is { } angle)
                set += SetAll(light, "Directional Light Angle0", v => v is Vector2 ? angle : v);
            changes.Add(sunColour is { } sc && sunAngle is { } sa && turned is { } ta
                ? string.Create(inv, $"sun colour {C(sc)} at {sa.X:F2},{sa.Y:F2} from DS3 light set 0 ({set} values)")
                  + (sunTurnDegrees != 0 ? string.Create(inv, $", turned {sunTurnDegrees:0.#} degrees with the map to {ta.X:F2},{ta.Y:F2} (sun yaw {ta.Y * 180 / MathF.PI:0} degrees, {SunHeight(ta):0} above the horizon)") : "")
                : $"light set: {set} values from DS3");
            if (hostFillLights)
                changes.Add("lights without shadows keep the host's strengths (--host-fill-lights)");
            else if (sunStrengths && FillLights(light, ds3Light, sunScale) is { } fill)
                changes.Add(fill);
            // After set 0 is done: the other sets start from it.
            if (lightSets is not null && MapLightSets.Add(gparam, ds3, lightSets, sunScale, sunTurnDegrees, characterSets).Note is { } sets)
                changes.Add(sets);
        }
        else
            changes.Add("light set: none in " + (light is null ? "the host" : "DS3") + "; kept");

        var ds3Fog = Group(ds3, "Fog");
        var fogColour = Value<Vector4>(ds3Fog, "Fog-Color");
        var heightColour = Value<Vector4>(ds3Fog, "Fog-heightColor");
        var fog = Group(gparam, "VolumetricFog");
        if (fog is not null && fogColour is { } ds3Colour)
        {
            var (direct, rest) = FogColours(ds3Colour, heightColour, FogStrengthEnabled);
            SetAll(fog, "VolumetricFog DirLight DirectColor", _ => new Vector4(direct.X, direct.Y, direct.Z, FogDirectStrength));
            SetAll(fog, "VolumetricFog DirLight OppositeColor", _ => new Vector4(rest.X, rest.Y, rest.Z, FogOppositeStrength));
            SetAll(fog, "VolumetricFog SkyLight SkyColor", _ => new Vector4(rest.X, rest.Y, rest.Z, FogSkyStrength));
            var reach = Reach(ds3Fog);
            SetAll(fog, "VolumetricFog FarDepth", _ => reach);
            SetAll(fog, "VolumetricFog FogEndDepth", _ => reach);
            var depth = layer is { } l && FogDepthEnabled ? DepthFog(ds3Colour.W, l, reach) : ((float Density, float Distribution, float Offset)?)null;
            var (density, distribution, offset) = depth ?? (FogDensity, FogDistribution, ground);
            SetAll(fog, "VolumetricFog Global HeightFogDistribution", _ => distribution);
            SetAll(fog, "VolumetricFog Global HeightFogDensity Scale", _ => density);
            SetAll(fog, "VolumetricFog Global HeightFogOffset", _ => offset);
            SetAll(fog, "VolumetricFog Global HeightFogReferenceOffset", _ => offset);
            var thickness = depth is null
                ? string.Create(inv, $"Stormveil's thickness (density {FogDensity}, out to {FogFarDepth} m) from height {ground:F0}")
                : string.Create(inv, $"DS3's depth haze: {Math.Clamp(ds3Colour.W, MinHaze, MaxHaze):0.##} of the view hidden at {reach:0} m (density {density:0.#####}) through the height the map is played at ({layer!.Value.Height:F0}, halving every {MathF.Log(2) / distribution:F0} m up)");
            changes.Add(string.Create(inv, $"fog colour {C(direct)} / {C(rest)} from DS3 fog 0 ({(FogStrengthEnabled ? $"its colours {C(ds3Colour)} at {ds3Colour.W:F2} and {C(heightColour ?? ds3Colour)} at {(heightColour ?? ds3Colour).W:F2}" : "colours without their strengths")}), ") + thickness);
        }
        else
            changes.Add("fog: " + (fog is null ? "no volumetric fog in the host" : "no fog colour in DS3") + "; kept");

        if (Group(gparam, "ColorGrading") is { } grading && SetAll(grading, "Yebis-ColorGrading-Enable", v => v is int ? 0 : v) > 0)
            changes.Add("colour grading off");
        return changes;
    }

    /// <summary>Where a DS3 map is played: the middle height of its enemies and player points, and the height
    /// between the lowest and highest twentieth of them.</summary>
    public readonly record struct FogLayer(float Height, float Span);

    /// <summary>The height a map is played at (<see cref="FogLayer"/>); 0 and 0 for a map with no entities.</summary>
    public static FogLayer Layer(MSB3 ds3)
    {
        var heights = ds3.Parts.Enemies.Select(e => e.Position.Y).Concat(ds3.Parts.Players.Select(p => p.Position.Y)).Order().ToList();
        if (heights.Count == 0)
            return new FogLayer(0, 0);
        return new FogLayer(heights[heights.Count / 2], heights[Math.Min(heights.Count - 1, heights.Count * 19 / 20)] - heights[heights.Count / 20]);
    }

    /// <summary>Fallback "map-atmosphere" D: DARKSIGN_FOG_DEPTH=stormveil keeps Stormveil's fog thickness from the
    /// map's ground up, as builds up to 1.0.52x had it.</summary>
    public static bool FogDepthEnabled => !string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_FOG_DEPTH"), "stormveil", StringComparison.OrdinalIgnoreCase);

    /// <summary>The least and most of the view DS3's haze may hide (DS3's fog 0 strength, clamped).</summary>
    public const float MinHaze = 0.02f, MaxHaze = 0.9f;

    /// <summary>The play height's span below which the fog's change with height is spread as over this one.</summary>
    public const float MinFogSpan = 50;

    /// <summary>
    /// DS3's depth haze (2026-11-20 playtest, Irithyll beside DS3: "look at all the lighting differences"; the port's
    /// far cathedral and Anor Londo stood sharp and black where DS3's fade into blue). DS3's distance fog hides up to
    /// its fog 0 strength (the fourth value of Fog-Color: m30_00 0.12, m40_00 0.26, m31_00 0.27) of what lies past its
    /// reach, wherever the camera stands; its height fog is a separate layer. The port gave every map Stormveil's
    /// height fog (density 0.004, falling to half every 12 m above the map's ground: the twentieth lowest entity), so
    /// a map played well above its lowest point had next to no haze: Irithyll's ground is -278 and its bridge some 130 m
    /// higher, where that fog is 1/2000 as thick. Now the fog is as thick at the height the map is played at (the
    /// middle of its entities) as hides DS3's share of the view at the fog's reach (<see cref="FogFarDepth"/>), and it
    /// changes four times over the map's played heights (twice as thick at the lowest twentieth, half at the
    /// highest), so it thins out into the sky above. Returns the density, the height distribution and the offset.
    /// </summary>
    public static (float Density, float Distribution, float Offset) DepthFog(float ds3Strength, FogLayer layer, float reach = FogFarDepth)
    {
        float hidden = Math.Clamp(ds3Strength, MinHaze, MaxHaze);
        float density = -MathF.Log(1 - hidden) / reach;
        float distribution = MathF.Log(4) / Math.Max(MinFogSpan, layer.Span);
        return (density, distribution, layer.Height);
    }

    /// <summary>The nearest and farthest the fog's reach may be set (<see cref="Reach"/>).</summary>
    public const float MinReach = 40, MaxReach = 2000;

    /// <summary>Fallback "map-atmosphere" E: DARKSIGN_FOG_REACH=stormveil gives every map Stormveil's 300 m fog reach,
    /// as builds up to 1.0.55x did.</summary>
    public static bool FogReachEnabled => !string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_FOG_REACH"), "stormveil", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How far DS3's fog 0 takes to reach its full strength: Fog-DistOffset (where it starts) plus Fog-Dist (how
    /// far it then thickens), clamped to <see cref="MinReach"/>..<see cref="MaxReach"/>. DS3's distance fog reaches
    /// its strength (the fourth value of Fog-Color) there and hides that share of everything farther, the sky
    /// included. Read 2026-11-21: Painted World (m45_00) 10 + 60 = 70 m at 0.5, Firelink (m40_00) 15 + 60 = 75 m at
    /// 0.26, High Wall (m30_00) 20 + 120 = 140 m at 0.12, Undead Settlement (m31_00) 15 + 50 = 65 m at 0.27. The port
    /// gave every map Stormveil's 300 m (Elden Ring's fog keeps what it hides at its end depth for everything past
    /// it, as DS3 does at its reach), so DS3's haze only arrived four times farther out than DS3 draws it: the
    /// Painted World's snowy air hid half the view at 300 m instead of 70 m and the map looked clear (2026-11-21
    /// playtest). Elden Ring's fog depth and end depth now take DS3's reach. Stormveil's 300 m when DS3 has no
    /// distances or with fallback "map-atmosphere" E.
    /// </summary>
    public static float Reach(GPARAM.Group? ds3Fog)
    {
        if (!FogReachEnabled || Value<float>(ds3Fog, "Fog-Dist") is not { } dist || dist <= 0)
            return FogFarDepth;
        var offset = Math.Max(0, Value<float>(ds3Fog, "Fog-DistOffset") ?? 0);
        return Math.Clamp(dist + offset, MinReach, MaxReach);
    }

    /// <summary>The group whose short name (Name2, trimmed) is <paramref name="name"/>.</summary>
    public static GPARAM.Group? Group(GPARAM gparam, string name) =>
        gparam.Groups.FirstOrDefault(g => (g.Name2 ?? "").Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first value (ID 0 where there is one) of a param named by its long name.</summary>
    /// <summary>Degrees above the horizon of a sun angle (DS3 and Elden Ring write it as a negative pitch).</summary>
    public static float SunHeight(Vector2 angle) => -angle.X * 180 / MathF.PI;

    public static T? Value<T>(GPARAM.Group? group, string name) where T : struct
    {
        var p = group?.Params.FirstOrDefault(x => x.Name1 == name);
        if (p is null || p.Values.Count == 0)
            return null;
        int i = Math.Max(0, p.ValueIDs.IndexOf(0));
        return p.Values[i] is T t ? t : null;
    }

    /// <summary>Sets every value (each ID and time of day) of a param. Returns how many were set.</summary>
    private static int SetAll(GPARAM.Group group, string name, Func<object, object> change)
    {
        var p = group.Params.FirstOrDefault(x => x.Name1 == name);
        if (p is null)
            return 0;
        for (int i = 0; i < p.Values.Count; i++)
            p.Values[i] = change(p.Values[i]);
        return p.Values.Count;
    }
}
