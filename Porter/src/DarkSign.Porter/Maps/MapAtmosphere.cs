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
/// 3,502). How strong each light is (the fourth value) stays Elden Ring's: DS3's strengths are on another scale.</item>
/// <item>VolumetricFog: the colours from DS3's fog 0 (Fog-Color for the sunward glow, heightColor for the rest), each
/// scaled by its own DS3 strength (<see cref="FogColours"/>); how
/// thick the fog is, how far it reaches and how strongly it is lit come from Stormveil Castle (m10_00, an outdoor
/// castle like DS3's maps, its first time of day), with the height fog starting at the DS3 map's own ground.</item>
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
    public static string Write(PorterContext ctx, GameArchives ds3, GameArchives er, MapId ds3Id, MapId erId, MapId hostId, MSB3 ds3Msb, bool hostAtmosphere)
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
        var changes = Apply(gparam, ds3Draw, GroundHeight(ds3Msb));
        var target = ctx.ModPath(MapPorter.ModParts(GparamPath(erId)));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, DCX.Compress(gparam.Write(), compression));
        return "atmosphere: " + string.Join("; ", changes);
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

    /// <summary>Changes the host's draw params in place. Returns what changed, for the log.</summary>
    public static List<string> Apply(GPARAM gparam, GPARAM ds3, float ground)
    {
        var inv = CultureInfo.InvariantCulture;
        string C(Vector4 v) => string.Create(inv, $"{v.X:F2} {v.Y:F2} {v.Z:F2}");
        var changes = new List<string>();

        var ds3Light = Group(ds3, "LightSet");
        var sunColour = Value<Vector4>(ds3Light, "Directional Light DiffColor0");
        var sunAngle = Value<Vector2>(ds3Light, "Directional Light Angle0");
        var light = Group(gparam, "LightSet");
        if (light is not null && ds3Light is not null)
        {
            int set = 0;
            foreach (var name in new[] { "Directional Light DiffColor0", "Directional Light SpecColor0", "Directional Light DiffColor1", "Directional Light SpecColor1" })
                if (Value<Vector4>(ds3Light, name) is { } colour)
                    set += SetAll(light, name, v => v is Vector4 old ? new Vector4(colour.X, colour.Y, colour.Z, old.W) : v);
            if (sunAngle is { } angle)
                set += SetAll(light, "Directional Light Angle0", v => v is Vector2 ? angle : v);
            changes.Add(sunColour is { } sc && sunAngle is { } sa
                ? string.Create(inv, $"sun colour {C(sc)} at {sa.X:F2},{sa.Y:F2} from DS3 light set 0 ({set} values)")
                : $"light set: {set} values from DS3");
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
            SetAll(fog, "VolumetricFog FarDepth", _ => FogFarDepth);
            SetAll(fog, "VolumetricFog FogEndDepth", _ => FogEndDepth);
            SetAll(fog, "VolumetricFog Global HeightFogDistribution", _ => FogDistribution);
            SetAll(fog, "VolumetricFog Global HeightFogDensity Scale", _ => FogDensity);
            SetAll(fog, "VolumetricFog Global HeightFogOffset", _ => ground);
            SetAll(fog, "VolumetricFog Global HeightFogReferenceOffset", _ => ground);
            changes.Add(string.Create(inv, $"fog colour {C(direct)} / {C(rest)} from DS3 fog 0 ({(FogStrengthEnabled ? $"its colours {C(ds3Colour)} at {ds3Colour.W:F2} and {C(heightColour ?? ds3Colour)} at {(heightColour ?? ds3Colour).W:F2}" : "colours without their strengths")}), Stormveil's thickness (density {FogDensity}, out to {FogFarDepth} m) from height {ground:F0}"));
        }
        else
            changes.Add("fog: " + (fog is null ? "no volumetric fog in the host" : "no fog colour in DS3") + "; kept");

        if (Group(gparam, "ColorGrading") is { } grading && SetAll(grading, "Yebis-ColorGrading-Enable", v => v is int ? 0 : v) > 0)
            changes.Add("colour grading off");
        return changes;
    }

    /// <summary>The group whose short name (Name2, trimmed) is <paramref name="name"/>.</summary>
    public static GPARAM.Group? Group(GPARAM gparam, string name) =>
        gparam.Groups.FirstOrDefault(g => (g.Name2 ?? "").Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first value (ID 0 where there is one) of a param named by its long name.</summary>
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
