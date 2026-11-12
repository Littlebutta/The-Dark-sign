using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// The TAE events of a template animation whose clip was replaced by a DS3 one (enemy port, method A). Events that
/// drive the template's behaviour and AI (combo/cancel windows, turning, invincibility...) and the template's sounds
/// and effects stay, stretched from the template clip's length to the DS3 clip's. DS3's events come along where both
/// games lay the event out the same (its attacks, which now find DS3's attack data in the character's own behaviour
/// variation, and the rest), joining the template's event group for that type. With a <see cref="ChrEffects.Session"/>
/// (the full port) the effect events and the character's own sounds are DS3's instead of the template's, and DS3's
/// SpEffect and projectile events come along converted.
/// <para>DS3 events whose type the template's TAE lacks but Elden Ring lays out the same (<see cref="SharedTypes"/>)
/// come along too, in a group of their own when no Elden Ring TAE has one: DS3's map scripts wait on them (type 231,
/// the event message the Hollow Soldier's burst move 3010 sends so common event 20005415 swaps it for the Pus of
/// Man). Fallbacks "enemy-ai": DARKSIGN_ENEMY_SIGNALS=0 drops them again, as up to 1.0.383. Type 233
/// ChangeChrDrawMask (32 bytes, one per display mask bit) shows or hides masked meshes mid-move: Iudex Gundyr's Pus of
/// Man body, horns and red eyes (mask 1, which neither his NpcParam row nor m40's script turns on) appear through it
/// when he transforms. Fallbacks "enemy-glow": DARKSIGN_ENEMY_DRAW_MASK=0 drops it again, as up to 1.0.403.</para>
/// Method (RULE 3): the type and its layout are DSAnimStudio's TAE templates (DS3 and Elden Ring both name type 231
/// RequestMsgMapList: s32 EzStateRequestID and three s32, 16 bytes); its condition in EMEVD is "IF Character Has Event
/// Message" (darkscript3's emedf).
/// </summary>
public static class ChrTae
{
    /// <summary>Kept from the template: behaviour windows (0 JumpTable, 67, 608, 800) and ID-based sounds/effects.</summary>
    public static readonly HashSet<int> TemplateTypes = [0, 67, 608, 800, 96, 112, 128, 129, 133, 139, 224, 792, 907];

    public static bool SignalsEnabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_SIGNALS")?.Trim() != "0";
    public static bool DrawMaskEnabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_DRAW_MASK")?.Trim() != "0";

    /// <summary>Whether a <see cref="SharedTypes"/> type comes along (each has its own fallback switch).</summary>
    public static bool Shared(int type) => SharedTypes.ContainsKey(type) && (type == DrawMaskType ? DrawMaskEnabled : SignalsEnabled);

    public const int DrawMaskType = 233;

    /// <summary>DS3 event types Elden Ring reads the same, with their parameter length, kept even when the template's TAE has none.</summary>
    public static readonly IReadOnlyDictionary<int, int> SharedTypes = new Dictionary<int, int>
    {
        [231] = 16, // RequestMsgMapList: the event message a map script's "Character Has Event Message" waits on
        [DrawMaskType] = 32, // ChangeChrDrawMask: one u8 per display mask bit (both games' TAE templates)
    };

    public static List<TAE.Event> Merge(List<TAE.Event> ds3Events, TAE.Animation anim, IReadOnlyDictionary<int, int> erLengths,
        IReadOnlyDictionary<int, TAE.EventGroup> templateGroups, float ds3Duration, float erDuration, ChrEffects.Session? fx = null,
        IReadOnlyDictionary<int, TAE.EventGroup>? otherGroups = null, Dictionary<int, int>? kept = null, Dictionary<int, int>? dropped = null)
    {
        float scale = ds3Duration > 0 && erDuration > 0 ? ds3Duration / erDuration : 1;
        var merged = new List<TAE.Event>();
        var groupOf = anim.Events.Where(e => e.Group is not null).GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First().Group!);
        anim.EventGroups ??= [];
        TAE.EventGroup? Group(int type)
        {
            if (groupOf.TryGetValue(type, out var g))
                return g;
            // A type the template's TAE has no group of takes one from any other Elden Ring TAE (the art port's
            // lesson: DS3 effect events were dropped silently wherever the template section had none).
            if (templateGroups.TryGetValue(type, out var proto) || otherGroups?.TryGetValue(type, out proto) == true)
                g = proto!.GetClone();
            else if (Shared(type))
                g = new TAE.EventGroup(type); // Elden Ring's own groups are the event type with GroupData0
            else
                return null;
            anim.EventGroups.Add(g);
            return groupOf[type] = g;
        }
        foreach (var e in anim.Events.Where(e => TemplateTypes.Contains(e.Type) && fx?.DropsTemplate(e) != true))
            merged.Add(new TAE.Event(e.StartTime * scale, e.EndTime * scale, e.Type, e.Unk04, (byte[])e.GetParameterBytes(false).Clone(), false)
                { Group = e.Group });
        foreach (var e in ds3Events)
        {
            // DS3's effects, SpEffects, projectiles and own sounds, converted (ChrEffects); one that can't be is dropped.
            if (fx is not null && fx.Convert(e) is var (fate, type, converted) && fate != ChrEffects.Session.Fate.Other)
            {
                if (fate == ChrEffects.Session.Fate.Add && Group(type) is { } own)
                    merged.Add(new TAE.Event(e.StartTime, e.EndTime, type, e.Unk04, converted!, false) { Group = own });
                continue;
            }
            if (TemplateTypes.Contains(e.Type))
                continue;
            var bytes = e.GetParameterBytes(false);
            bool known = erLengths.TryGetValue(e.Type, out var length);
            if (!known && Shared(e.Type) && SharedTypes.TryGetValue(e.Type, out length))
            {
                known = true;
                if (length == bytes.Length && kept is not null)
                    kept[e.Type] = kept.GetValueOrDefault(e.Type) + 1;
            }
            if (!known || length != bytes.Length || Group(e.Type) is not { } group)
            {
                if (dropped is not null)
                    dropped[e.Type] = dropped.GetValueOrDefault(e.Type) + 1;
                continue;
            }
            merged.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, (byte[])bytes.Clone(), false) { Group = group });
        }
        return merged.OrderBy(e => e.StartTime).ToList();
    }
}
