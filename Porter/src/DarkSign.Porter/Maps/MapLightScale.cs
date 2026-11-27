using System.Globalization;
using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// One scale from DS3's light values to the port's, the same for every map (Firelink and the Cathedral of the Deep
/// still dim on 1.0.721 while the High Wall looks right).
/// <para>
/// Up to 1.0.72x every map was scaled by its own sun: the reference sun (0.5, <see cref="MapAtmosphere.LightReference"/>)
/// over the DS3 map's sun, applied to the lamps (<see cref="MapLampStrength.Fit"/>) and the baked light
/// (<see cref="MapBakedLight.SkyAlpha"/>), while the sun itself was 0.5 everywhere. That assumed each DS3 map was
/// exposed for its own sun. It isn't: every DS3 map has auto exposure off and nearly the same fixed exposure
/// (Tone Map Yebis-ToneMapExposure: High Wall 2.3, Firelink 2.3, Undead Settlement 2.2, Cathedral 2.0), so DS3's
/// light values are on one scale across maps. A map with a strong sun simply has bright sunlight beside ordinary lamps
/// and sky light. The Cathedral's sun is 3.8 (High Wall 1.23): its own-sun scale was 0.13, which took its baked light
/// to alpha 3.9 (High Wall 21.6), its reflections to 0.07 of the colosseum's and its lamps to 0.13 of DS3's, five and
/// three times darker against the High Wall than DS3 has them, and its sun to 0.5 instead of DS3's much brighter one.
/// </para>
/// <para>
/// DS3 also scales its baked probes per map: Dominant Light "SH Color Scale" is 0.5 on the High Wall and 1 on
/// Firelink, the Undead Settlement and the Cathedral (DS3's lighting splits a probe into a dominant light and the
/// rest; "Dominant Sharpness RGB Scale" is 0.5, 1.2, 0.8 and 1). So the High Wall's sky light is shown at half its
/// probes while the others show theirs in full, and the port had every map at the High Wall's half.
/// </para>
/// Now the High Wall, the map that looks right, is the calibration and keeps every value it had: the light scale is
/// the reference sun over the High Wall's DS3 sun times the map's DS3 exposure over the High Wall's, for every map;
/// the sun is DS3's sun times that scale; the baked light also takes the map's SH Color Scale over the High Wall's.
/// </summary>
public static class MapLightScale
{
    /// <summary>The DS3 map the light reference's slot is built from (High Wall of Lothric, on m45_01).</summary>
    public static readonly MapId Ds3Reference = MapId.Parse("m30_00_00_00");

    /// <summary>The High Wall's DS3 values, used when DS3's file can't be read (measured on DS3's m30_00_0000.gparam).</summary>
    public const float Ds3ReferenceSun = 1.23f, Ds3ReferenceExposure = 2.3f, Ds3ReferenceSh = 0.5f;

    /// <summary>The port's sun strength when the reference file can't be read (m45_01's light set 0).</summary>
    public const float ErReferenceSun = 0.5f;

    /// <summary>The sun is kept within this many times the reference sun (a DS3 interior or cutscene with an odd sun).</summary>
    public const float MinSun = 0.25f, MaxSun = 3f;

    /// <param name="Light">Times DS3's strength for lamps (and DS3's sky light before <see cref="Sky"/>'s SH share).</param>
    /// <param name="Sky">Times DS3's probe brightness for the baked light (<see cref="MapBakedLight.SkyAlpha"/>).</param>
    /// <param name="Sun">Times the reference's sun strength (1 on the High Wall).</param>
    public sealed record Scale(float Light, float Sky, float Sun, string Text);

    public static float? SunOf(GPARAM? g) =>
        g is null ? null : MapAtmosphere.Value<Vector4>(MapAtmosphere.Group(g, "LightSet"), "Directional Light DiffColor0")?.W;

    public static float? ExposureOf(GPARAM? g) =>
        g is null ? null : MapAtmosphere.Value<float>(MapAtmosphere.Group(g, "Tone Map"), "Yebis-ToneMapExposure");

    public static float? ShOf(GPARAM? g) =>
        g is null ? null : MapAtmosphere.Value<float>(MapAtmosphere.Group(g, "Dominant Light"), "Dominant Param  SH Color Scale");

    /// <summary>The scale for a DS3 map's draw params <paramref name="ds3"/>; <paramref name="erReference"/> is the
    /// light reference's (m45_01), <paramref name="ds3Reference"/> the High Wall's DS3 file. Missing values fall back
    /// to the High Wall's, so a map without them is lit like the High Wall.</summary>
    public static Scale For(GPARAM? erReference, GPARAM? ds3Reference, GPARAM? ds3)
    {
        float erSun = Positive(SunOf(erReference)) ?? ErReferenceSun;
        float refSun = Positive(SunOf(ds3Reference)) ?? Ds3ReferenceSun;
        float refExposure = Positive(ExposureOf(ds3Reference)) ?? Ds3ReferenceExposure;
        float refSh = Positive(ShOf(ds3Reference)) ?? Ds3ReferenceSh;
        float exposure = Positive(ExposureOf(ds3)) ?? refExposure;
        float sh = Positive(ShOf(ds3)) ?? refSh;
        float sun = Positive(SunOf(ds3)) ?? refSun;

        float light = Math.Clamp(erSun / refSun * (exposure / refExposure), MapLampStrength.MinSunScale, MapLampStrength.MaxSunScale);
        float sky = light * (sh / refSh);
        float sunTimes = Math.Clamp(sun / refSun * (exposure / refExposure), MinSun, MaxSun);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"one light scale for every map, set by the High Wall: DS3 exposure {exposure:0.##} (High Wall {refExposure:0.##}), " +
            $"lamps x{light:0.###}, sky light x{sky:0.###} (DS3 SH share {sh:0.##}, High Wall {refSh:0.##}), " +
            $"sun {sun:0.##} -> {erSun * sunTimes:0.###} ({sunTimes:0.##} x the reference's {erSun:0.##})");
        return new Scale(light, sky, sunTimes, text);
    }

    /// <summary>Reads the three files and returns the map's scale (the High Wall's when the DS3 map has no draw params).</summary>
    public static Scale For(GameArchives ds3, GameArchives er, MapId ds3Id) =>
        For(MapAtmosphere.ReadLightReference(er), ReadDs3(ds3, Ds3Reference), ReadDs3(ds3, ds3Id));

    /// <summary>DS3's draw params of a map, or null.</summary>
    public static GPARAM? ReadDs3(GameArchives ds3, MapId id)
    {
        var path = MapAtmosphere.Ds3GparamPaths(id).FirstOrDefault(ds3.Contains);
        if (path is null)
            return null;
        try { return GPARAM.Read(ds3.Read(path)); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>Multiplies the sun's two strengths (light set DiffColor0 / SpecColor0 W, every value ID) by
    /// <paramref name="times"/>. Returns how many values changed.</summary>
    public static int ScaleSun(GPARAM gparam, float times)
    {
        if (MathF.Abs(times - 1) < 1e-4f || MapAtmosphere.Group(gparam, "LightSet") is not { } light)
            return 0;
        int changed = 0;
        foreach (var p in light.Params.Where(p => p.Name1 is "Directional Light DiffColor0" or "Directional Light SpecColor0"))
            for (int i = 0; i < p.Values.Count; i++)
                if (p.Values[i] is Vector4 v)
                {
                    p.Values[i] = v with { W = v.W * times };
                    changed++;
                }
        return changed;
    }

    private static float? Positive(float? v) => v is > 0 ? v : null;
}
