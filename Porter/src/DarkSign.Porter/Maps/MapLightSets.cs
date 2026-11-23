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
/// from a map piece is taken from DS3 and Sekiro, whose draw params Elden Ring's format follows.</para>
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

    /// <summary>
    /// The light sets only characters name (each part of the layout that names it is an enemy, a person or a boss):
    /// DS3's boss and character sets (m45_00 111 Friede, 120 Ariandel; m40_00 110 the Fire Keeper, 120 Gundyr).
    /// </summary>
    public static SortedSet<int> CharacterOnly(MSBE msb)
    {
        var map = new HashSet<int>();
        var characters = new HashSet<int>();
        foreach (var part in msb.Parts.GetEntries())
            if (Er(part) is { LightSetID: >= 0 } g)
                (part is MSBE.Part.EnemyBase ? characters : map).Add(g.LightSetID);
        return new(characters.Except(map));
    }

    /// <summary>
    /// The set Elden Ring lights its own characters with (2026-11-20 playtest: "Father Ariandel's lighting is too
    /// bright just like the firekeeper in the firelink shrine"). Measured on the hosts' draw params: Elden Ring's
    /// characters name set 100, whose lights are the sun (0.6 on m45_00) and next to nothing else (a second light of
    /// 0.05 that follows the camera, a local light of 0.015): 0.665 in all, as diffuse and as specular light. DS3 gives
    /// its characters strong lights of their own besides the sun: Ariandel's set 120 has a second light of 1.6, local
    /// lights of 0.47 and 1.59 (2.74 in all on the slot's scale, 4.1 times Elden Ring's set 100), the Fire Keeper's
    /// set 110 0.4, 0.3 and 0.4 with specular light of 1 and 2 (2.7 as specular light). Elden Ring has no baked light
    /// to brighten the room around them as DS3 did, so they stood out lit like a stage. A character-only set now lights
    /// no more in all than the slot's character set: its lights keep DS3's colours, directions and proportions, all
    /// scaled down together.
    /// </summary>
    public const int HostCharacterSet = 100;

    /// <summary>The diffuse colours, the specular ones, and the switch each light needs on to count (null: always on).</summary>
    private static readonly (string Diffuse, string Specular, string? Switch)[] Lights =
    [
        ("Directional Light DiffColor0", "Directional Light SpecColor0", null),
        ("Directional Light DiffColor1", "Directional Light SpecColor1", null),
        ("Directional Light LocalLight DiffColor1", "Directional Light LocalLight SpecColor1", "Directional Enable LocalLight1"),
        ("Directional Light LocalLight DiffColor2", "Directional Light LocalLight SpecColor2", "Directional Enable LocalLight2"),
    ];

    /// <summary>All the light a set gives (strengths of the lights that are on and not black), as diffuse and as
    /// specular light, times <paramref name="scale"/>.</summary>
    internal static (float Diffuse, float Specular) Total(GPARAM.Group group, int id, float scale = 1)
    {
        T? At<T>(string name) where T : struct
        {
            var p = group.Params.FirstOrDefault(x => x.Name1 == name);
            int i = p?.ValueIDs.IndexOf(id) ?? -1;
            return p is not null && i >= 0 && i < p.Values.Count && p.Values[i] is T v ? v : null;
        }
        float W(string name) => At<Vector4>(name) is { } c && Math.Max(c.X, Math.Max(c.Y, c.Z)) > 0 ? Math.Max(0, c.W) * scale : 0;
        float diffuse = 0, specular = 0;
        foreach (var (d, s, on) in Lights)
        {
            if (on is not null && At<bool>(on) == false)
                continue;
            diffuse += W(d);
            specular += W(s);
        }
        return (diffuse, specular);
    }

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

    /// <summary>
    /// How much stronger than the slot's sun a light of a far-view set may be (<see cref="IsFarView"/>). DS3 lights
    /// the scenery past the play area with sets of its own, far stronger than its sun, so it stands out bright through
    /// the haze (2026-11-20 playtest: Irithyll's Anor Londo and far mountains black against the sky beside DS3's pale
    /// blue). At <see cref="MaxOverSun"/> they came out black, so they keep DS3's proportion to the sun up to this.
    /// </summary>
    public const float MaxFarOverSun = 8;

    /// <summary>
    /// A light set for the scenery past the play area: DS3 names them 遠景 ("distant view": m30_00 13, 132, 137, 138,
    /// 300 最遠景; m40_00 300, 310; m45_00 8; m37_00 6 最遠景の山脈, 8 他マップの遠景) or 天球 ("sky sphere", m37_00 9),
    /// and numbers them from 300 where unnamed (m31_00 300, 310 light the m901xxx and m903xxx pieces 6 km out).
    /// </summary>
    public static bool IsFarView(int id, string ds3Name) =>
        ds3Name.Contains("遠景", StringComparison.Ordinal) || ds3Name.Contains("天球", StringComparison.Ordinal) || id is >= 300 and < 400;

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
    /// <param name="characterSets">Sets only characters name (<see cref="CharacterOnly"/>): their lights are scaled down
    /// together to light no more in all than the slot's own character set (<see cref="HostCharacterSet"/>).</param>
    public static (List<int> Added, string? Note) Add(GPARAM gparam, GPARAM ds3, IEnumerable<int> wanted, float sunScale, float sunTurnDegrees,
        IReadOnlySet<int>? characterSets = null)
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
        float farCap = hostSun > 0 ? hostSun * MaxFarOverSun : float.MaxValue;
        var capped = new SortedSet<int>();
        var far = new SortedSet<int>();
        // Elden Ring's own character lighting: the slot's set 100 (set 0 when it has none).
        var hostCharacter = Total(light, hostIds.Contains(HostCharacterSet) ? HostCharacterSet : 0);
        var toned = new List<string>();
        var inv = CultureInfo.InvariantCulture;

        foreach (var id in added)
        {
            bool farView = IsFarView(id, Ds3Name(id));
            if (farView)
                far.Add(id);
            float setCap = farView ? farCap : cap;
            // A character-only set: every light scaled by one factor so the set's total stays within the slot's
            // character set, as diffuse and as specular light.
            float diffuseScale = 1, specularScale = 1;
            if (characterSets is not null && characterSets.Contains(id) && !farView)
            {
                var (d, s) = Total(ds3Light, id, sunScale);
                if (d > hostCharacter.Diffuse && hostCharacter.Diffuse > 0)
                    diffuseScale = hostCharacter.Diffuse / d;
                if (s > hostCharacter.Specular && hostCharacter.Specular > 0)
                    specularScale = hostCharacter.Specular / s;
                if (diffuseScale < 1 || specularScale < 1)
                    toned.Add(string.Create(inv, $"{id} ({d:0.##}/{s:0.##} -> {d * diffuseScale:0.##}/{s * specularScale:0.##})"));
            }
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
                        float w = c.W * sunScale * (p.Name1.Contains("Spec", StringComparison.Ordinal) ? specularScale : diffuseScale);
                        // A black light gives nothing whatever its strength (DS3 switches lights off that way).
                        if (w > setCap && Math.Max(c.X, Math.Max(c.Y, c.Z)) > 0)
                        {
                            w = setCap;
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

        var names = added.Select(id => Ds3Name(id) is { Length: > 0 } n ? $"{id} ({n})" : id.ToString(inv));
        return (added, string.Create(inv, $"DS3 light sets {string.Join(", ", names)} added beside the host's {string.Join(", ", hostIds.Order())} (strengths x{sunScale:0.##})")
            + (far.Count > 0 ? string.Create(inv, $"; far-view sets {string.Join(", ", far)} may reach {MaxFarOverSun} times the sun ({farCap:0.##})") : "")
            + (capped.Count > 0 ? string.Create(inv, $"; lights over their limit turned down in sets {string.Join(", ", capped)}") : "")
            + (toned.Count > 0 ? string.Create(inv, $"; character sets toned down to the slot's character light ({hostCharacter.Diffuse:0.##} diffuse / {hostCharacter.Specular:0.##} specular in all): {string.Join(", ", toned)}") : ""));
    }

    /// <summary>Every ID a group's params have a value for.</summary>
    public static HashSet<int> Ids(GPARAM.Group group) => [.. group.Params.SelectMany(p => p.ValueIDs)];

    /// <summary>
    /// After the draw params are written: a part whose light set they lack goes back to -1, the default. Returns a line for the log.
    /// </summary>
    public static string Finish(MSBE msb, IReadOnlySet<int> defined)
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
            if (!defined.Contains(g.LightSetID))
            {
                g.LightSetID = -1;
                reset++;
                continue;
            }
            kept[g.LightSetID] = kept.GetValueOrDefault(g.LightSetID) + 1;
        }
        var own = kept.Where(k => k.Key != 0).Select(k => $"{k.Key} x{k.Value}").ToList();
        return $"light sets: {kept.Values.Sum()} of {parts} parts name DS3's light set ({kept.GetValueOrDefault(0)} on set 0"
               + (own.Count > 0 ? $"; {string.Join(", ", own.Take(24))}{(own.Count > 24 ? ", ..." : "")}" : "") + ")"
               + (reset > 0 ? $"; {reset} on sets the draw params lack went back to the default" : "");
    }
}
