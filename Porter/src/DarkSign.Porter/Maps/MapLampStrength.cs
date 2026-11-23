using System.Globalization;
using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// How strong DS3's lamps are once they stand in Elden Ring (the large white light outside the Dancer's chapel and
/// Vordt's gate on the High Wall, 2026-11-19 playtest).
/// <para>
/// Up to 1.0.491 a lamp kept DS3's strength as written. Two things make that wrong in Elden Ring:
/// </para>
/// <list type="number">
/// <item><b>The scale.</b> The ported map takes Elden Ring's sun strength, because DS3's is on another scale
/// (<see cref="MapAtmosphere"/>): DS3's sun is 0.8 (Firelink), 1.23 (High Wall) and 1.5 (Undead Settlement), the
/// hosts' 0.6, 0.5 and 0.4. The lamps stayed on DS3's scale, so against the sun they shone 1.3 to 3.8 times as
/// strong as DS3 shows them. <see cref="SunScale"/> gives a lamp the host's sun strength over DS3's.</item>
/// <item><b>The reach.</b> DS3's artists lit a statue or a door from far away with a very strong spot. The High
/// Wall has such spots exactly where the white light was seen: 広場中央の像スポット ("plaza statue spot") 000-004 in the
/// plaza outside the Dancer's chapel (strength 20 to 40, the three of 001-003 in a row 6.5 m apart), six Spot_廊下
/// in a row beside them (7, reach 60 m), and Spot_ボス部屋大扉用_001 ("for the boss room's great door": strength 4
/// without falloff over 240 m, aimed at Vordt's door from 154 m away). Whatever stands in such a beam is lit far
/// past white in Elden Ring: the stone, and the fog DS3 places there (three 王城1霧 fog banks in the plaza, two at
/// Vordt's gate), whose billboards the port lights like Elden Ring's own since #201 (fxr-port.js), and which stood
/// there twice over (<see cref="MapSfx.Convert"/>). Elden Ring's fog may take light from lamps as well
/// (VolumetricFog Global LocalLightScale is 1 in every draw param file; a lamp's own share of that is probably the
/// light file's UnkD0, which is 0.5 on Stormveil's spots, 0 on its other lamps and 0 on every ported one, so this
/// part is not proven). Elden Ring's own lamps never come near DS3's spots: over Stormveil's 216 lamps the strongest
/// light on a surface at half a lamp's reach (<see cref="Surface"/>) is 3.8 and the most light a lamp puts along
/// a line through it (<see cref="Air"/>) 72; DS3's statue spots give 19.3 and 438, the door spot 914.
/// <see cref="Fit"/> turns a lamp down until it is inside what Elden Ring's lamps do. Measured on DS3's files:
/// 11 of the High Wall's 184 lamps are turned down (the statue spots 20 to 3.3, the door spot 4 to 0.32), 7 of
/// Firelink's 44, 2 of the Undead Settlement's 160; all but one of them spots.</item>
/// </list>
/// Both measures use the falloff the two games' lamps share (the light file's Sharpness: below 1 the light falls
/// off early, at 20 it keeps its full strength to the edge of its reach; Elden Ring pairs strength 20 with
/// sharpness 0.2 and never gives a lamp of sharpness 4 or more a strength above 1.5), written as
/// strength * (1 - distance / reach) ^ (1 / sharpness). The exact curve isn't known; the limits are Elden Ring's
/// own lamps measured with the same curve, so a lamp that passes is one Elden Ring could have placed.
/// </summary>
public static class MapLampStrength
{
    /// <summary>The most light an Elden Ring lamp puts on a surface at half its reach (Stormveil: spot_食堂の窓A001,
    /// strength 6, sharpness 1.5).</summary>
    public const float ErMaxSurface = 3.8f;

    /// <summary>The most light an Elden Ring lamp puts along a line through it (the same Stormveil spot: 6 * 20 m * 1.5 / 2.5).</summary>
    public const float ErMaxAir = 72f;

    /// <summary>The sun scale is kept inside this range: a DS3 map with a very weak sun (an interior) shouldn't
    /// multiply its lamps.</summary>
    public const float MinSunScale = 0.1f, MaxSunScale = 1.5f;

    /// <summary>The part of a lamp's strength left at half its reach.</summary>
    public static float HalfReach(float sharpness) => MathF.Pow(0.5f, 1 / Math.Max(sharpness, 0.01f));

    /// <summary>A lamp's strength: the stronger of its two (some of Elden Ring's lamps only light reflections).</summary>
    public static float Strength(BTL.Light light) => Math.Max(light.DiffusePower, light.SpecularPower);

    /// <summary>The light a lamp puts on a surface at half its reach.</summary>
    public static float Surface(BTL.Light light) => Strength(light) * HalfReach(light.Sharpness);

    /// <summary>The light a lamp puts along a line through it (on fog, smoke or dust in its way): its strength summed over its reach
    /// (strength * reach * s / (1 + s) for sharpness s).</summary>
    public static float Air(BTL.Light light)
    {
        float s = Math.Max(light.Sharpness, 0.01f);
        return Strength(light) * light.Radius * s / (1 + s);
    }

    /// <summary>The host's sun strength over DS3's (light set 0's fourth value in both draw param files), kept inside
    /// <see cref="MinSunScale"/> to <see cref="MaxSunScale"/>; 1 when either isn't there.</summary>
    public static float SunScale(GPARAM? host, GPARAM? ds3)
    {
        float? Sun(GPARAM? g) => g is null ? null : MapAtmosphere.Value<Vector4>(MapAtmosphere.Group(g, "LightSet"), "Directional Light DiffColor0")?.W;
        return Sun(host) is { } h && Sun(ds3) is { } d && h > 0 && d > 0 ? Math.Clamp(h / d, MinSunScale, MaxSunScale) : 1;
    }

    public sealed record Change(string Name, float Before, float After, float Reach, float Sharpness, string Why);

    /// <summary>
    /// Scales the lamp's two strengths by <paramref name="sunScale"/>, then turns it down as far as Elden Ring's
    /// limits ask. Returns what was done when a limit applied, null when the lamp fits.
    /// </summary>
    public static Change? Fit(BTL.Light light, float sunScale)
    {
        float before = Strength(light);
        light.DiffusePower *= sunScale;
        light.SpecularPower *= sunScale;
        float surface = Surface(light), air = Air(light);
        float factor = 1;
        string why = "";
        if (surface > ErMaxSurface)
        {
            factor = ErMaxSurface / surface;
            why = "light on surfaces";
        }
        if (air * factor > ErMaxAir)
        {
            factor = ErMaxAir / air;
            why = "light along its reach";
        }
        if (factor >= 1)
            return null;
        light.DiffusePower *= factor;
        light.SpecularPower *= factor;
        return new Change(light.Name, before, Strength(light), light.Radius, light.Sharpness, why);
    }

    /// <summary>For the log: the scale and the lamps turned down, strongest first.</summary>
    public static string Describe(float sunScale, IReadOnlyList<Change> changes, int lamps)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = string.Create(inv, $"strengths times {sunScale:0.##} (the host's sun over DS3's)");
        if (changes.Count == 0)
            return text + "; every lamp is inside what Elden Ring's own lamps do";
        return text + string.Create(inv, $"; {changes.Count} of {lamps} turned down to what Elden Ring's own lamps do (on a surface {ErMaxSurface}, along its reach {ErMaxAir}): ")
               + string.Join(", ", changes.OrderByDescending(c => c.Before / Math.Max(c.After, 1e-6f)).Take(8)
                   .Select(c => string.Create(inv, $"{c.Name} {c.Before:0.##} -> {c.After:0.##} ({c.Why}; reach {c.Reach:0.#} m, sharpness {c.Sharpness:0.##})")))
               + (changes.Count > 8 ? ", ..." : "");
    }
}
