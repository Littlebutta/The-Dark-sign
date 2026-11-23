using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Two event fields of a cutscene timeline (MQB) are timeline numbers within the event's cut: Unk08 is the number of
/// the timeline that holds the event, Unk28 the number of an earlier timeline it hangs on (a part on its cut's Havok
/// timeline, an effect or sound on a part's, the Havok timeline on offset.sib's; -1 for none). Every DS3 and Elden
/// Ring cutscene in the 2026-11-12 survey holds to this (1,967 events: Unk08 is always the event's timeline, Unk28
/// always -1 or a lower number).
/// <para>The port takes timelines out (the ones whose events were all left out, and those of parts with no Elden
/// Ring model) and adds some (cutscene sound). Without renumbering, events point past the end of their cut's
/// timeline list: Elden Ring then reads a part's custom data ("EventFlagId", CSRemoAct) from whatever bytes lie
/// there, takes text for a parameter count and runs on until it hangs or crashes (1.0.460: Friede's intro crashed at
/// eldenring.exe+0x2676b42 with a count of 0x65006E, UTF-16 "ne"; the "not responding" hangs on High Wall's
/// s45_01_0020 in 1.0.417 and 1.0.442 fit too).</para>
/// <para><see cref="Take"/> notes each cut's timelines before the edits; <see cref="Relink"/> afterwards sets Unk08 to
/// the event's timeline and points Unk28 at the same timeline as before under its new number. An event that hung on
/// a timeline that is gone is left out (the part it followed isn't in the cutscene), and so is a timeline left with
/// no events. An event added after <see cref="Take"/> (cutscene sound) whose timeline is gone hangs on nothing.</para>
/// <para>The timeline's own Unk10 is the same link: in every DS3 and Elden Ring cutscene surveyed (2026-11-12, 1,967
/// events in 1,841 timelines) a timeline's Unk10 equals every one of its events' Unk28, -1 or a lower number. Up to
/// 1.0.549 only the events were renumbered, so after the port took timelines out (DS3 draw settings, parts with no
/// Elden Ring model, sound it couldn't build) a timeline's Unk10 still named its parent's old number: a later
/// timeline, a different one, or one past the end of the cut. <see cref="Relink"/> and <see cref="Compact"/> set
/// Unk10 and the events' Unk28 together.</para>
/// </summary>
public static class CutsceneTimelineLinks
{
    /// <summary>Each cut's timelines and events as they were before the port changed them.</summary>
    public sealed class Snapshot
    {
        internal readonly Dictionary<MQB.Cut, List<MQB.Timeline>> Cuts = new(ReferenceEqualityComparer.Instance);
        internal readonly HashSet<MQB.Event> Events = new(ReferenceEqualityComparer.Instance);
        /// <summary>Timelines that had events (an empty one stays as DS3 had it).</summary>
        internal readonly HashSet<MQB.Timeline> Filled = new(ReferenceEqualityComparer.Instance);
        /// <summary>The timeline each timeline hangs on (its Unk10), as it was before the edits (null: none).</summary>
        internal readonly Dictionary<MQB.Timeline, MQB.Timeline?> Parents = new(ReferenceEqualityComparer.Instance);
    }

    public sealed record Result(int Renumbered, int EventsDropped, int TimelinesDropped)
    {
        public override string ToString() => $"timeline links: {Renumbered} renumbered, {EventsDropped} events on left-out timelines dropped"
                                             + (TimelinesDropped > 0 ? $" ({TimelinesDropped} timelines emptied)" : "");
    }

    public static Snapshot Take(MQB mqb)
    {
        var s = new Snapshot();
        foreach (var cut in mqb.Cuts)
        {
            s.Cuts[cut] = [.. cut.Timelines];
            foreach (var tl in cut.Timelines)
            {
                s.Parents[tl] = tl.Unk10 >= 0 && tl.Unk10 < cut.Timelines.Count && !ReferenceEquals(cut.Timelines[tl.Unk10], tl) ? cut.Timelines[tl.Unk10] : null;
                if (tl.Events.Count > 0)
                    s.Filled.Add(tl);
                foreach (var ev in tl.Events)
                    s.Events.Add(ev);
            }
        }
        return s;
    }

    public static Result Relink(MQB mqb, Snapshot before)
    {
        int renumbered = 0, eventsDropped = 0, timelinesDropped = 0;
        foreach (var cut in mqb.Cuts)
        {
            var old = before.Cuts.GetValueOrDefault(cut) ?? [];
            // What each event hung on, as a timeline (null: nothing, or a number DS3 itself had out of range).
            var target = new Dictionary<MQB.Event, MQB.Timeline?>(ReferenceEqualityComparer.Instance);
            foreach (var ev in cut.Timelines.SelectMany(t => t.Events))
                target[ev] = ev.Unk28 >= 0 && ev.Unk28 < old.Count ? old[ev.Unk28] : null;

            // Leave out what hung on a timeline that is gone, until nothing more empties.
            bool changed = true;
            while (changed)
            {
                changed = false;
                var present = new HashSet<MQB.Timeline>(cut.Timelines, ReferenceEqualityComparer.Instance);
                foreach (var tl in cut.Timelines)
                    eventsDropped += tl.Events.RemoveAll(ev => target[ev] is { } t && !present.Contains(t) && before.Events.Contains(ev));
                int had = cut.Timelines.Count;
                cut.Timelines.RemoveAll(tl => tl.Events.Count == 0 && before.Filled.Contains(tl));
                if (cut.Timelines.Count != had)
                {
                    timelinesDropped += had - cut.Timelines.Count;
                    changed = true;
                }
            }

            // A timeline hangs on what it hung on before the edits; one added since, on what its first event names.
            MQB.Timeline? ParentOf(MQB.Timeline tl) =>
                before.Parents.TryGetValue(tl, out var p) ? p : tl.Events.Select(ev => target[ev]).FirstOrDefault(t => t is not null);
            renumbered += Number(cut, ParentOf);
        }
        return new Result(renumbered, eventsDropped, timelinesDropped);
    }

    /// <summary>
    /// Gives each timeline of a cut its number in every event (Unk08) and its parent's number in both its own Unk10 and
    /// its events' Unk28 (-1 when the parent is gone or not earlier). Returns how many fields changed.
    /// </summary>
    private static int Number(MQB.Cut cut, Func<MQB.Timeline, MQB.Timeline?> parentOf)
    {
        int changed = 0;
        var index = new Dictionary<MQB.Timeline, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < cut.Timelines.Count; i++)
            index[cut.Timelines[i]] = i;
        var links = cut.Timelines.Select((tl, i) => parentOf(tl) is { } p && index.TryGetValue(p, out var n) && n < i ? n : -1).ToList();
        for (int i = 0; i < cut.Timelines.Count; i++)
        {
            var tl = cut.Timelines[i];
            if (tl.Unk10 != links[i])
                changed++;
            tl.Unk10 = links[i];
            foreach (var ev in tl.Events)
            {
                if (ev.Unk08 != i || ev.Unk28 != links[i])
                    changed++;
                ev.Unk08 = i;
                ev.Unk28 = links[i];
            }
        }
        return changed;
    }

    /// <summary>
    /// After events were taken out of an already linked timeline (sounds the bank couldn't hold): timelines left with
    /// no events by that edit (<paramref name="emptied"/>) go, with the timelines that hung on them, and the rest are
    /// renumbered. A timeline that was already empty stays (Elden Ring's own cutscenes have a few). Returns how many
    /// timelines went.
    /// </summary>
    public static int Compact(MQB mqb, IReadOnlySet<MQB.Timeline> emptied)
    {
        int dropped = 0;
        foreach (var cut in mqb.Cuts)
        {
            var old = cut.Timelines.ToList();
            var parent = new Dictionary<MQB.Timeline, MQB.Timeline?>(ReferenceEqualityComparer.Instance);
            foreach (var tl in old)
                parent[tl] = tl.Unk10 >= 0 && tl.Unk10 < old.Count && !ReferenceEquals(old[tl.Unk10], tl) ? old[tl.Unk10] : null;
            bool changed = true;
            while (changed)
            {
                var present = new HashSet<MQB.Timeline>(cut.Timelines, ReferenceEqualityComparer.Instance);
                int had = cut.Timelines.Count;
                cut.Timelines.RemoveAll(tl => tl.Events.Count == 0 && emptied.Contains(tl) || parent[tl] is { } p && !present.Contains(p));
                dropped += had - cut.Timelines.Count;
                changed = cut.Timelines.Count != had;
            }
            Number(cut, tl => parent[tl]);
        }
        return dropped;
    }

    /// <summary>Every event's links point inside its cut: Unk08 its own timeline, Unk28 -1 or a lower number and the same
    /// as its timeline's Unk10, which points at an earlier timeline or is -1.</summary>
    public static IEnumerable<string> Problems(MQB mqb)
    {
        foreach (var cut in mqb.Cuts)
            for (int i = 0; i < cut.Timelines.Count; i++)
            {
                var tl = cut.Timelines[i];
                if (tl.Unk10 < -1 || tl.Unk10 >= i)
                    yield return $"{cut.Name} timeline {i}: Unk10 {tl.Unk10}";
                foreach (var ev in tl.Events)
                {
                    if (ev.Unk28 != tl.Unk10)
                        yield return $"{cut.Name} timeline {i}: event {ev.ID} Unk28 {ev.Unk28}, timeline Unk10 {tl.Unk10}";
                    if (ev.Unk08 != i)
                        yield return $"{cut.Name} timeline {i}: event {ev.ID} Unk08 {ev.Unk08}";
                    if (ev.Unk28 < -1 || ev.Unk28 >= i)
                        yield return $"{cut.Name} timeline {i}: event {ev.ID} Unk28 {ev.Unk28}";
                }
            }
    }
}
