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

            var index = new Dictionary<MQB.Timeline, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < cut.Timelines.Count; i++)
                index[cut.Timelines[i]] = i;
            for (int i = 0; i < cut.Timelines.Count; i++)
                foreach (var ev in cut.Timelines[i].Events)
                {
                    int link = target[ev] is { } t && index.TryGetValue(t, out var n) && n < i ? n : -1;
                    if (ev.Unk08 != i || ev.Unk28 != link)
                        renumbered++;
                    ev.Unk08 = i;
                    ev.Unk28 = link;
                }
        }
        return new Result(renumbered, eventsDropped, timelinesDropped);
    }

    /// <summary>Every event's links point inside its cut: Unk08 its own timeline, Unk28 -1 or a lower number.</summary>
    public static IEnumerable<string> Problems(MQB mqb)
    {
        foreach (var cut in mqb.Cuts)
            for (int i = 0; i < cut.Timelines.Count; i++)
                foreach (var ev in cut.Timelines[i].Events)
                {
                    if (ev.Unk08 != i)
                        yield return $"{cut.Name} timeline {i}: event {ev.ID} Unk08 {ev.Unk08}";
                    if (ev.Unk28 < -1 || ev.Unk28 >= i)
                        yield return $"{cut.Name} timeline {i}: event {ev.ID} Unk28 {ev.Unk28}";
                }
    }
}
