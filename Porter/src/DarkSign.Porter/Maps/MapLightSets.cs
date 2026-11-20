using System.Globalization;
using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's light set per map piece, object, floor and character (2026-11-19 playtest on 1.0.499: "sister friede's boss
/// room doesn't look right, there also isn't lighting like there is in the original game").
/// <para>Every DS3 part names the light set (LightSet group of the map's draw params, by ID) it is lit with. Most of
/// a map is on set 0, the outdoor light the slot's draw params already take (<see cref="MapAtmosphere"/>), but DS3
/// lights its rooms with sets of their own. Measured on the game files: Friede's hall in the Painted World (DS3 m45_00,
/// the box -262..-195, 15..60, -255..-205) is 132 map pieces and 135 objects on set 1, which DS3's draw params call
/// 暗闇用 ("for darkness"): no sun, no second light, no local light, so the hall is lit only by its candles, its
/// windows' light shafts and the baked light. 1,653 of the Painted World's parts are on set 1, 489 on set 10 (the
/// valley floor), and the bosses have their own (Friede 111, Ariandel 120: a warm light from the fire bowl). The High
/// Wall has 30 sets besides 0 (its rooms on 20 to 30, the plaza statues on 24 and 25), Firelink 14, Undead Settlement
/// 18. The port copied every part from a colosseum part that names set -1 (the default), so every DS3 room was lit by
/// the outdoor sun through its roof.</para>
/// <para>Now each ported part names DS3's set (<see cref="Carry"/>), the slot's draw params get every DS3 set the map
/// uses that the host lacks (<see cref="Add"/>: DS3's colours and angles, strengths on the same scale as set 0's), and
/// a part whose set the draw params still lack goes back to -1 (<see cref="Finish"/>). The host's own sets (0, 100,
/// 1000, 1100) stay the host's: Elden Ring's floors name 100 (92 of Stormveil's 111 collisions), as DS3's outdoor
/// floors and characters do.</para>
/// <para>Unproven until tested: Elden Ring's own map pieces name only 0 and -1, so that the game reads other set IDs
/// from a map piece is taken from DS3 and Sekiro, whose draw params Elden Ring's format follows. Fallback
/// "map-light-sets" B (port-maps --one-light-set) leaves every part on -1, as up to 1.0.499.</para>
/// </summary>
public static class MapLightSets
{
    public static MSB3.Part.GparamConfig? Ds3(MSB3.Part part) => part switch
    {
        MSB3.Part.MapPiece p => p.Gparam,
        MSB3.Part.ObjectBase o => o.Gparam,
        MSB3.Part.EnemyBase e => e.Gparam,
        MSB3.Part.Collision c => c.Gparam,
        _ => null,
    };

    public static MSBE.Part.GparamConfig? Er(MSBE.Part part) => part switch
    {
        MSBE.Part.MapPiece p => p.Gparam,
        MSBE.Part.Asset a => a.Gparam,
        MSBE.Part.DummyAsset d => d.Gparam,
        MSBE.Part.EnemyBase e => e.Gparam,
        MSBE.Part.Collision c => c.Gparam,
        _ => null,
    };

    /// <summary>The ported part names the DS3 part's light set.</summary>
    public static void Carry(MSB3.Part src, MSBE.Part part)
    {
        if (Ds3(src) is { } from && Er(part) is { } to)
            to.LightSetID = from.LightSetID;
    }

    /// <summary>Every light set a part of the layout names (-1, the default, left out).</summary>
    public static SortedSet<int> Wanted(MSBE msb) =>
        new(msb.Parts.GetEntries().Select(Er).Where(g => g is not null).Select(g => g!.LightSetID).Where(id => id >= 0));

    /// <summary>The light set params DS3 writes in its own map space whose angles turn with the map offset.</summary>
    private static bool IsAngle(GPARAM.Param p) => p.Name1.Contains("Angle", StringComparison.Ordinal) && p.Type == GPARAM.ParamType.Float2;

    /// <summary>The light colours whose strength (fourth value) is on DS3's scale.</summary>
    public static readonly string[] Colours =
    [
        "Directional Light DiffColor0", "Directional Light SpecColor0", "Directional Light DiffColor1", "Directional Light SpecColor1",
        "Directional Light LocalLight DiffColor1", "Directional Light LocalLight SpecColor1",
        "Directional Light LocalLight DiffColor2", "Directional Light LocalLight SpecColor2",
    ];

    /// <summary>
    /// How much stronger than the slot's sun a light of an added set may be. DS3 boosts a few sets far past its sun:
    /// the High Wall's set 131 (four pieces in the plaza before the Dancer's chapel) has a sun of 11 beside set 0's
    /// 1.23, set 4 13.4, set 11 16. On DS3's scale times the sun scale (0.41) set 131's sun would be 4.5 beside the
    /// slot's 0.5. Elden Ring's own draw params light nothing above 1 (Stormveil's suns 0.35 to 0.4, the colosseums'
    /// 0.4 to 0.6) and the High Wall's earlier white glare came from DS3 lights placed past Elden Ring's range
    /// (<see cref="MapLampStrength"/>), so a light is kept within twice the sun.
    /// </summary>
    public const float MaxOverSun = 2;

    /// <summary>Switches DS3 sets per light set.</summary>
    public static readonly string[] Switches = ["Directional Enable LocalLight1", "Directional Enable LocalLight2", "Directional LightLight1 FollowedCamera"];

    /// <summary>
    /// Adds to the slot's light sets every set in <paramref name="wanted"/> that DS3's draw params have and the host's
    /// lack. Each new set starts as a copy of the slot's set 0 (sharpness, corner shade weights, hemisphere light: the
    /// values set 0 got from the host and <see cref="MapAtmosphere.FillLights"/>) and takes from DS3's set: the colour
    /// of the sun, the second light and the local lights with DS3's strength times <paramref name="sunScale"/> (the
    /// host's sun over DS3's set-0 sun, the scale set 0's fill lights and the lamps take), so the sets keep DS3's
    /// proportions to each other; every light direction, turned by the map offset like set 0's sun; and which local
    /// lights are on. Values are kept in ID order. Returns the IDs added and a line for the log (null when none).
    /// </summary>
    public static (List<int> Added, string? Note) Add(GPARAM gparam, GPARAM ds3, IEnumerable<int> wanted, float sunScale, float sunTurnDegrees)
    {
        var added = new List<int>();
        var light = MapAtmosphere.Group(gparam, "LightSet");
        var ds3Light = MapAtmosphere.Group(ds3, "LightSet");
        if (light is null || ds3Light is null || light.Params.Count == 0)
            return (added, null);
        var hostIds = Ids(light);
        var ds3Ids = Ids(ds3Light);
        foreach (var id in wanted.Where(id => id >= 0 && !hostIds.Contains(id) && ds3Ids.Contains(id)).Distinct().Order())
            added.Add(id);
        if (added.Count == 0)
            return (added, null);

        // DS3's comments name each set (m45_00: 0 "MAP_メイン:Outside", 1 "暗闇用").
        var first = ds3Light.Params[0];
        string Ds3Name(int id)
        {
            int i = first.ValueIDs.IndexOf(id);
            return i >= 0 && ds3Light.Comments.Count == first.Values.Count ? ds3Light.Comments[i].Trim() : "";
        }
        int ownComments = light.Params[0].Values.Count == light.Comments.Count ? light.Comments.Count : -1;
        // No light of a new set stronger than MaxOverSun times the slot's sun.
        float hostSun = MapAtmosphere.Value<Vector4>(light, "Directional Light DiffColor0")?.W ?? 0;
        float cap = hostSun > 0 ? hostSun * MaxOverSun : float.MaxValue;
        var capped = new SortedSet<int>();

        foreach (var id in added)
        {
            int at = -1;
            foreach (var p in light.Params)
            {
                int baseIndex = Math.Max(0, p.ValueIDs.IndexOf(0));
                if (p.Values.Count == 0)
                    continue;
                var value = p.Values[baseIndex];
                var source = ds3Light.Params.FirstOrDefault(x => x.Name1 == p.Name1);
                int si = source?.ValueIDs.IndexOf(id) ?? -1;
                if (source is not null && si >= 0 && si < source.Values.Count)
                {
                    var ds3Value = source.Values[si];
                    if (Colours.Contains(p.Name1) && ds3Value is Vector4 c && value is Vector4)
                    {
                        float w = c.W * sunScale;
                        // A black light gives nothing whatever its strength (DS3 switches lights off that way).
                        if (w > cap && Math.Max(c.X, Math.Max(c.Y, c.Z)) > 0)
                        {
                            w = cap;
                            capped.Add(id);
                        }
                        value = new Vector4(c.X, c.Y, c.Z, w);
                    }
                    else if (IsAngle(p) && ds3Value is Vector2 a && value is Vector2)
                        value = MapAtmosphere.SunTurn(a, sunTurnDegrees);
                    else if (Switches.Contains(p.Name1) && ds3Value is bool on && value is bool)
                        value = on;
                }
                // After every value whose ID is lower, so the IDs stay in order.
                int insert = p.ValueIDs.Count;
                for (int i = 0; i < p.ValueIDs.Count; i++)
                    if (p.ValueIDs[i] > id)
                    {
                        insert = i;
                        break;
                    }
                p.Values.Insert(insert, value);
                p.ValueIDs.Insert(insert, id);
                if (p.TimeOfDay is { } tod && tod.Count >= baseIndex + 1)
                    tod.Insert(Math.Min(insert, tod.Count), tod[baseIndex]);
                if (p == light.Params[0])
                    at = insert;
            }
            if (ownComments >= 0 && at >= 0)
            {
                var name = Ds3Name(id);
                light.Comments.Insert(Math.Min(at, light.Comments.Count), $"DS3 light set {id}" + (name.Length > 0 ? $": {name}" : ""));
            }
        }

        // The group's ID list (Unk3 in SoulsFormats), as every Elden Ring draw param file keeps one per group.
        int groupIndex = gparam.Groups.IndexOf(light);
        foreach (var u in gparam.Unk3s.Where(u => u.GroupIndex == groupIndex))
            u.ValueIDs = [.. Ids(light).Order()];

        var inv = CultureInfo.InvariantCulture;
        var names = added.Select(id => Ds3Name(id) is { Length: > 0 } n ? $"{id} ({n})" : id.ToString(inv));
        return (added, string.Create(inv, $"DS3 light sets {string.Join(", ", names)} added beside the host's {string.Join(", ", hostIds.Order())} (strengths x{sunScale:0.##})")
            + (capped.Count > 0 ? string.Create(inv, $"; lights over {MaxOverSun} times the sun ({cap:0.##}) turned down in sets {string.Join(", ", capped)}") : ""));
    }

    /// <summary>Every ID a group's params have a value for.</summary>
    public static HashSet<int> Ids(GPARAM.Group group) => [.. group.Params.SelectMany(p => p.ValueIDs)];

    /// <summary>
    /// After the draw params are written: a part whose light set they lack goes back to -1, the default (all of them
    /// when <paramref name="off"/>, fallback "map-light-sets" B). Returns a line for the log.
    /// </summary>
    public static string Finish(MSBE msb, IReadOnlySet<int> defined, bool off)
    {
        var kept = new SortedDictionary<int, int>();
        int reset = 0, parts = 0;
        foreach (var g in msb.Parts.GetEntries().Select(Er))
        {
            if (g is null)
                continue;
            parts++;
            if (g.LightSetID < 0)
                continue;
            if (off || !defined.Contains(g.LightSetID))
            {
                g.LightSetID = -1;
                reset++;
                continue;
            }
            kept[g.LightSetID] = kept.GetValueOrDefault(g.LightSetID) + 1;
        }
        if (off)
            return $"light sets: every part on the default set (--one-light-set); {reset} DS3 light set names dropped";
        var own = kept.Where(k => k.Key != 0).Select(k => $"{k.Key} x{k.Value}").ToList();
        return $"light sets: {kept.Values.Sum()} of {parts} parts name DS3's light set ({kept.GetValueOrDefault(0)} on set 0"
               + (own.Count > 0 ? $"; {string.Join(", ", own.Take(24))}{(own.Count > 24 ? ", ..." : "")}" : "") + ")"
               + (reset > 0 ? $"; {reset} on sets the draw params lack went back to the default" : "");
    }
}
