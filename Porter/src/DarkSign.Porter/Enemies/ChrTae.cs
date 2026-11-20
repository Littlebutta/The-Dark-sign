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

    /// <summary>
    /// Event types both games have whose Elden Ring layout reads a field DS3 always writes 0 or doesn't have
    /// (DSAnimStudio's TAE.Template.DS3.xml / TAE.Template.ER.xml, compared field by field 2026-11-20). They come along
    /// only where the template itself uses the type, never just because another Elden Ring character does.
    /// 601 SetAdditiveAnim never comes along: Elden Ring plays the animation its last field names, which DS3 leaves 0
    /// (the Winged Knight's 258 such events in 1.0.5xx, after #226 let ER-wide types in, played animation 0 on top of
    /// every move).
    /// </summary>
    public static readonly HashSet<int> ReadDifferently = [64, 65, 95, 119, 123, 132, 145, 193, 224, 522, 601, 604, 606, 706, 10096, 10130, 10137, 10138];

    /// <summary>DS3 event types that never come along (see <see cref="ReadDifferently"/>).</summary>
    public static readonly HashSet<int> NeverPorted = [601];

    /// <summary>DS3 event types Elden Ring reads the same, with their parameter length, kept even when the template's TAE has none.</summary>
    public static readonly IReadOnlyDictionary<int, int> SharedTypes = new Dictionary<int, int>
    {
        [231] = 16, // RequestMsgMapList: the event message a map script's "Character Has Event Message" waits on
        [DrawMaskType] = 32, // ChangeChrDrawMask: one u8 per display mask bit (both games' TAE templates)
    };

    /// <summary>
    /// Whether event layouts are compared by each type's usual length over the whole character (default) or, as up to
    /// 1.0.501, by the first event of the type in one TAE file. Fallback enemy-port F: DARKSIGN_ENEMY_EVENT_LAYOUT=0.
    /// </summary>
    public static bool PooledLayout => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_EVENT_LAYOUT")?.Trim() != "0";

    /// <summary>
    /// Each event type's parameter length: the most common one among the events (the longer on a tie). A TAE file
    /// doesn't store the length; the reader takes the distance to whatever is written next, so an event at the end of
    /// a block reads 8 bytes long or short (measured 2026-11-19 on c3060, c3000, c4340, c1100, c1310, c6040: type 5 is
    /// 16 bytes 401 times and 24 bytes 30 times in Elden Ring, 16 x131 and 8 x5 in DS3; type 1 16 x317 and 24 x14).
    /// Comparing one sample of each game dropped every DS3 event of a type whenever either sample was such an odd one
    /// (the Hollow Manservant lost 174 type-5 behaviour events, the Wolf of Ariandel its type 5 and 16).
    /// </summary>
    public static Dictionary<int, int> Layout(IEnumerable<TAE.Event> events) =>
        events.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.GroupBy(e => e.GetParameterBytes(false).Length)
            .OrderByDescending(l => l.Count()).ThenByDescending(l => l.Key).First().Key);

    /// <param name="erLengths">Each type's parameter length in the template (see <see cref="Layout"/>).</param>
    /// <param name="ds3Lengths">Each type's usual length in the DS3 character. With it, a DS3 event comes along when
    /// its type's usual length is the template's, written at that length (padding cut or added); without it, only an
    /// event whose own length is the template's.</param>
    public static List<TAE.Event> Merge(List<TAE.Event> ds3Events, TAE.Animation anim, IReadOnlyDictionary<int, int> erLengths,
        IReadOnlyDictionary<int, TAE.EventGroup> templateGroups, float ds3Duration, float erDuration, ChrEffects.Session? fx = null,
        IReadOnlyDictionary<int, TAE.EventGroup>? otherGroups = null, Dictionary<int, int>? kept = null, Dictionary<int, int>? dropped = null,
        IReadOnlyDictionary<int, int>? ds3Lengths = null)
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
            if (NeverPorted.Contains(e.Type))
            {
                if (dropped is not null)
                    dropped[e.Type] = dropped.GetValueOrDefault(e.Type) + 1;
                continue;
            }
            var bytes = e.GetParameterBytes(false);
            bool known = erLengths.TryGetValue(e.Type, out var length);
            if (!known && Shared(e.Type) && SharedTypes.TryGetValue(e.Type, out length))
            {
                known = true;
                if (length == bytes.Length && kept is not null)
                    kept[e.Type] = kept.GetValueOrDefault(e.Type) + 1;
            }
            // The odd-length events are the same layout with the reader's padding guess off by 8 (see Layout).
            if (known && ds3Lengths is not null && ds3Lengths.TryGetValue(e.Type, out var usual) && usual == length && bytes.Length != length
                && Math.Abs(bytes.Length - length) <= 8 && bytes.Skip(length).All(b => b == 0))
            {
                bytes = (byte[])bytes.Clone();
                Array.Resize(ref bytes, length);
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
