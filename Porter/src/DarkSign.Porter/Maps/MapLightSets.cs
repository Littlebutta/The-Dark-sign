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
/// <para>2026-11-24: proven wrong in game. Elden Ring lights the whole frame with one light set, so parts naming
/// different sets made the sun flicker on and off (<see cref="Carry"/>). Ported parts now name Elden Ring's own sets
/// again; <see cref="Add"/> still writes any other set something names (cutscenes pick theirs for the whole frame,
/// which is how Elden Ring uses them).</para>
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

    /// <summary>
    /// The light set Elden Ring's own maps give a part of this kind (Stormveil, m10_00_00_00: map pieces and assets
    /// set 0, floors 100). Characters keep their template's.
    /// </summary>
    public static int? ErSet(MSBE.Part part) => part switch
    {
        MSBE.Part.Collision => HostCharacterSet,
        MSBE.Part.MapPiece or MSBE.Part.Asset or MSBE.Part.DummyAsset => 0,
        _ => null,
    };

    /// <summary>
    /// The ported part names the light set Elden Ring's own part of its kind names (<see cref="ErSet"/>), never DS3's
    /// own set. 2026-11-24 playtest on 1.0.614: "a lot of flickering from the lighting, as well as poorer
    /// performance", on map 1 and map 2: the sun and every sun shadow went off and on over the whole screen about once
    /// a second. Proven on Devin's PC (1.0.620, map 1 in front of the shrine, the same scripted 15 s run each time,
    /// reports/flicker-ab on the reports/boss-start branch): with the map's draw params as built the picture had 8
    /// brightness jumps while moving and a mean brightness of 34; with the same file but every light set given the
    /// colosseum's values it had 1 jump and 89; with no file at all 0 and 91; putting the fog back changed nothing
    /// (7 jumps, 31). So it is not the light sets' values but that they differ: Elden Ring lights the whole frame with
    /// one light set at a time, it does not light each part with its own as DS3 does. With DS3's sets on the parts
    /// (since PR #227: m40 has 13 sets besides 0, 398 of its pieces on set 40 "MAP 暗闇用" with no sun, m30 28 sets)
    /// the frame switched between a set with the sun and one without as the parts in view changed. Elden Ring's own
    /// pieces name only 0 and -1, which was the unproven assumption #227 noted. DS3's darker rooms come from the light
    /// areas (<see cref="MapLightAreas"/>), the roofs' sun shadows and the per-place draw params collisions switch to
    /// (<see cref="MapAtmosphere.WriteScenes"/>), which is how Elden Ring darkens its own interiors.
    /// </summary>
    public static void Carry(MSB3.Part src, MSBE.Part part)
    {
        if (Er(part) is { } to && ErSet(part) is { } id)
            to.LightSetID = id;
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
    /// to brighten the room around them as DS3 did, so they stood out lit like a stage. A character set DS3 adds is
    /// now a copy of this one (<see cref="Add"/>).
    /// </summary>
    public const int HostCharacterSet = 100;

    /// <summary>
    /// Adds to the slot's light sets every set in <paramref name="wanted"/> that DS3's draw params have and the host's
    /// lack (in practice the sets DS3's cutscenes name, since parts name Elden Ring's sets: <see cref="Carry"/>). Each
    /// new set is a copy of the slot's own set: <see cref="HostCharacterSet"/> for a set only characters name, or a
    /// character set by DS3's numbering (100 to 199), set 0 for the rest. From DS3's set it takes only the sun's
    /// direction, turned by the map offset like set 0's. Up to 2026-11-25 new sets took DS3's light colours and
    /// strengths (scaled to the slot, capped, character sets toned down); with ray tracing on, DS3's light values made
    /// the screen flicker (<see cref="MapAtmosphere.Lights"/>). Values are kept in ID order. Returns the IDs added and
    /// a line for the log (null when none).
    /// </summary>
    public static (List<int> Added, string? Note) Add(GPARAM gparam, GPARAM ds3, IEnumerable<int> wanted, float sunTurnDegrees,
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
        var inv = CultureInfo.InvariantCulture;
        var asCharacter = new List<int>();

        foreach (var id in added)
        {
            bool character = hostIds.Contains(HostCharacterSet)
                             && ((characterSets?.Contains(id) ?? false) || id is >= 100 and < 200);
            if (character)
                asCharacter.Add(id);
            int baseId = character ? HostCharacterSet : 0;
            int at = -1;
            foreach (var p in light.Params)
            {
                if (p.Values.Count == 0)
                    continue;
                int baseIndex = p.ValueIDs.IndexOf(baseId);
                if (baseIndex < 0)
                    baseIndex = Math.Max(0, p.ValueIDs.IndexOf(0));
                var value = p.Values[baseIndex];
                if (p.Name1 == "Directional Light Angle0" && value is Vector2)
                {
                    var source = ds3Light.Params.FirstOrDefault(x => x.Name1 == p.Name1);
                    int si = source?.ValueIDs.IndexOf(id) ?? -1;
                    if (source is not null && si >= 0 && si < source.Values.Count && source.Values[si] is Vector2 a)
                        value = MapAtmosphere.SunTurn(a, sunTurnDegrees);
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
        return (added, $"DS3 light sets {string.Join(", ", names)} added beside the host's {string.Join(", ", hostIds.Order())}, as copies of the host's set 0"
            + (asCharacter.Count > 0 ? $" (character sets {string.Join(", ", asCharacter)} of set {HostCharacterSet})" : "")
            + " with DS3's sun direction");
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
        return $"light sets: {kept.Values.Sum()} of {parts} parts name a light set, as Elden Ring's own parts do ({kept.GetValueOrDefault(0)} on set 0"
               + (own.Count > 0 ? $"; {string.Join(", ", own.Take(24))}{(own.Count > 24 ? ", ..." : "")}" : "") + ")"
               + (reset > 0 ? $"; {reset} on sets the draw params lack went back to the default" : "");
    }
}
