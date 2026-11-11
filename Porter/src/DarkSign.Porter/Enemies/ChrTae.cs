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
/// </summary>
public static class ChrTae
{
    /// <summary>Kept from the template: behaviour windows (0 JumpTable, 67, 608, 800) and ID-based sounds/effects.</summary>
    public static readonly HashSet<int> TemplateTypes = [0, 67, 608, 800, 96, 112, 128, 129, 133, 139, 224, 792, 907];

    public static List<TAE.Event> Merge(List<TAE.Event> ds3Events, TAE.Animation anim, IReadOnlyDictionary<int, int> erLengths,
        IReadOnlyDictionary<int, TAE.EventGroup> templateGroups, float ds3Duration, float erDuration, ChrEffects.Session? fx = null,
        IReadOnlyDictionary<int, TAE.EventGroup>? otherGroups = null)
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
            if (!templateGroups.TryGetValue(type, out var proto) && otherGroups?.TryGetValue(type, out proto) != true)
                return null;
            g = proto.GetClone();
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
            if (!erLengths.TryGetValue(e.Type, out var length) || length != bytes.Length || Group(e.Type) is not { } group)
                continue;
            merged.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, (byte[])bytes.Clone(), false) { Group = group });
        }
        return merged.OrderBy(e => e.StartTime).ToList();
    }
}
