using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// When the Elden Ring template's behaviour windows (JumpTable 0, charge 67, 608, 800) open on a ported DS3 move.
///
/// Friede's one-handed R1 played clean and chained from launcher 1.0.89 (PRs #28 and #30): the windows come from the
/// Elden Ring template move (DS3's JumpTable IDs mean other things in Elden Ring), stretched onto the DS3 timeline
/// between anchors: the start, each pair of matching hits, the JumpTable IDs both games share (the recovery cancels
/// 4, 103, 107, ...) and the end, which is the DS3 animation's own length. Every converted move of every weapon goes
/// through the same code, but three cases still let a window open while the DS3 swing was playing, cutting it short:
///
/// 1. One Elden Ring hit against several DS3 hits (a single sweep templated onto a DS3 double or triple swing,
///    twinblade spins, dual-wield two-handed moves): the Elden Ring hit was paired with DS3's FIRST hit, so the chain
///    and roll windows that follow it opened between DS3's first and last swing. Behaviour windows now pair the
///    template's last hit with DS3's last hit (sounds still follow the first, as before).
/// 2. A DS3 move whose JumpTables share no ID with its template's had no recovery anchor at all, so its windows
///    were spread evenly from the hit to the end, and a slow DS3 recovery was cut off. Now no Elden Ring window that
///    opens after the template's last hit may open before DS3's own first window after its last hit (or, with none,
///    before DS3's last hit ends): a ported move can never be left earlier than DS3 itself allowed.
/// 3. The template's behaviour clip can carry an enforced duration, crop or trigger times measured on the Elden Ring
///    animation; those are cleared or moved onto the DS3 timeline for converted moves (see MovesetPort).
/// 4. Elden Ring writes 100.000 s as "until the animation ends" on one event of most moves (e.g. the 605 that holds a
///    stance from 2.0 to 100.0). The end anchor took that as the template's length, so every window after the last
///    anchor was squeezed by 3 s / 100 s: Friede's stance loop opened its attack window for 0.08 s instead of 2.3 s
///    (the stance attacks only came out while mashing). The end anchor now comes from the finite events, and a
///    sentinel end stays a sentinel.
///
/// DARKSIGN_TIMING_GUARD=0 (launcher fallback "combo-windows" D) turns all four off: exactly the 1.0.89 timing.
/// </summary>
internal static class MoveTiming
{
    public static bool Guard = Environment.GetEnvironmentVariable("DARKSIGN_TIMING_GUARD") != "0";

    /// <summary>Elden Ring's "until the animation ends" event end (written as 100.000 s).</summary>
    public const float Sentinel = 99.9f;
    public static bool IsSentinel(float t) => Guard && t >= Sentinel;

    /// <summary>One move's stretch from the template's timeline onto DS3's.</summary>
    public sealed class Stretch
    {
        public required List<(float Er, float Ds3)> Anchors { get; init; }
        /// <summary>Start of the template's last hit (null: no hits).</summary>
        public float? ErLastHit { get; init; }
        /// <summary>Earliest time DS3 lets the move be left after its last hit (null: no guard).</summary>
        public float? Recovery { get; init; }
        public float Ds3End { get; init; }
        /// <summary>Windows held back to <see cref="Recovery"/>.</summary>
        public int Held { get; set; }
        /// <summary>Earliest JumpTable start after the last hit, as placed (for the report).</summary>
        public float? FirstExit { get; set; }

        public float Warp(float t)
        {
            for (int i = 1; i < Anchors.Count; i++)
                if (t <= Anchors[i].Er || i == Anchors.Count - 1)
                {
                    var (a, b) = (Anchors[i - 1], Anchors[i]);
                    float span = b.Er - a.Er;
                    return Math.Max(0, span <= 0 ? a.Ds3 + (t - a.Er) : a.Ds3 + (t - a.Er) * (b.Ds3 - a.Ds3) / span);
                }
            return t;
        }

        /// <summary>Where a template behaviour event lands on the DS3 move.</summary>
        public (float Start, float End) Place(TAE.Event e)
        {
            float start = Warp(e.StartTime), end = IsSentinel(e.EndTime) ? e.EndTime : Math.Max(start, Warp(e.EndTime));
            bool afterHit = e.Type == 0 && ErLastHit is { } hit && e.StartTime >= hit - 0.001f;
            if (afterHit && Recovery is { } floor && start < floor - 0.001f)
            {
                float length = end - start;
                start = floor;
                end = Math.Max(end, Math.Min(floor + length, Math.Max(end, Ds3End)));
                Held++;
            }
            if (afterHit)
                FirstExit = FirstExit is { } f ? Math.Min(f, start) : start;
            return (start, end);
        }
    }

    /// <param name="pair">The DS3 hit each template hit belongs to (hits paired in order, spread evenly).</param>
    public static Stretch Build(List<TAE.Event> ds3Events, List<TAE.Event> erEvents, List<float> ds3Hits, List<float> erHits,
        Func<int, int> pair, float ds3Duration)
    {
        var candidates = new List<(float Er, float Ds3)>();
        for (int h = 0; h < erHits.Count && ds3Hits.Count > 0; h++)
            candidates.Add((erHits[h], ds3Hits[Guard && h == erHits.Count - 1 ? ds3Hits.Count - 1 : pair(h)]));
        // JumpTable windows both games share (the recovery cancels: 4, 103, 107, ...) open where each game's move
        // can end, so they anchor the stretch too; without them Elden Ring's quick chain window opened while the
        // slower DS3 swing was still recovering.
        var ds3Jt = JumpTables(ds3Events).GroupBy(j => j.Id).ToDictionary(g => g.Key, g => g.Min(j => j.Start));
        foreach (var g in JumpTables(erEvents).GroupBy(j => j.Id))
            if (ds3Jt.TryGetValue(g.Key, out var ds3Start) && g.Min(j => j.Start) is var erStart && erStart > 0.05f && ds3Start > 0.05f)
                candidates.Add((erStart, ds3Start));
        // The DS3 end is the animation's own length: its events can stop well before it (Friede's R1 ended at 0.625 s).
        var erEnds = erEvents.Select(e => e.EndTime).Where(t => !IsSentinel(t)).ToList();
        float erEnd = (erEnds.Count > 0 ? erEnds : erEvents.Select(e => e.EndTime)).DefaultIfEmpty(0).Max();
        float ds3End = Math.Max(ds3Duration, ds3Events.Select(e => e.EndTime).DefaultIfEmpty(0).Max());
        candidates.Add((erEnd, ds3End));
        // Keep the anchors in order on both timelines; one that would run backwards is dropped.
        var anchors = new List<(float Er, float Ds3)> { (0, 0) };
        foreach (var c in candidates.OrderBy(c => c.Er).ThenBy(c => c.Ds3))
            if (c.Er > anchors[^1].Er + 0.001f && c.Ds3 > anchors[^1].Ds3 + 0.001f)
                anchors.Add(c);
        return new Stretch
        {
            Anchors = anchors,
            ErLastHit = erHits.Count > 0 ? erHits[^1] : null,
            Recovery = Guard && erHits.Count > 0 ? Recovery(ds3Events, ds3Hits) : null,
            Ds3End = ds3End,
        };
    }

    /// <summary>The earliest time DS3 lets its move be left after the last hit: its first JumpTable window that opens
    /// at or after the last swing starts, else the end of that swing. Null for a move without hits.</summary>
    public static float? Recovery(List<TAE.Event> ds3Events, List<float> ds3Hits)
    {
        if (ds3Hits.Count == 0)
            return null;
        float last = ds3Hits[^1];
        var after = JumpTables(ds3Events).Where(j => j.Start >= last - 0.001f).Select(j => j.Start).ToList();
        if (after.Count > 0)
            return after.Min();
        var swing = ds3Events.Where(e => e.Type == 1 && e.StartTime >= last - 0.001f).Select(e => e.EndTime).DefaultIfEmpty(last).Max();
        return swing;
    }

    private static IEnumerable<(int Id, float Start)> JumpTables(List<TAE.Event> events) =>
        events.Where(e => e.Type == 0 && e.GetParameterBytes(false).Length >= 4)
            .Select(e => (BitConverter.ToInt32(e.GetParameterBytes(false), 0), e.StartTime));

    // ---- self-test: the cases above on made-up moves, run in CI (porter timing-selftest) ----

    public static int SelfTest()
    {
        int failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"timing-selftest: {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        static TAE.Event Ev(float s, float e, int type, int id = 0)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(id).CopyTo(bytes, 0);
            return new TAE.Event(s, e, type, 0, bytes, false);
        }
        static List<float> Hits(List<TAE.Event> ev) =>
            ev.Where(e => e.Type == 1).Select(e => e.StartTime).Order().Aggregate(new List<float>(), (l, t) => { if (l.Count == 0 || t - l[^1] > 0.1f) l.Add(t); return l; });
        bool saved = Guard;
        try
        {
            // 1. Single template hit vs DS3 triple swing: the chain window (JT 115) must open after DS3's third hit.
            var er = new List<TAE.Event> { Ev(0.4f, 0.5f, 1), Ev(0.7f, 1.2f, 0, 115), Ev(0.9f, 1.2f, 0, 4) };
            var ds3 = new List<TAE.Event> { Ev(0.3f, 0.4f, 1), Ev(0.8f, 0.9f, 1), Ev(1.3f, 1.4f, 1), Ev(1.7f, 2.2f, 0, 30) };
            int Pair1(int h) => 0;
            Guard = false;
            var old = Build(ds3, er, Hits(ds3), Hits(er), Pair1, 2.4f);
            var oldChain = old.Place(er[1]).Start;
            Guard = true;
            var now = Build(ds3, er, Hits(ds3), Hits(er), Pair1, 2.4f);
            var chain = now.Place(er[1]).Start;
            Check($"multi-hit DS3 swing: chain window {oldChain:0.00}s before (inside the swings), {chain:0.00}s now (after the last hit at 1.30s)",
                oldChain < 1.3f && chain >= 1.3f);
            Check("multi-hit DS3 swing: chain window not before DS3's own recovery (1.70s)", chain >= 1.7f - 0.001f);

            // 2. No shared JumpTable IDs, slow DS3 recovery: windows held back to DS3's first window after the hit.
            er = [Ev(0.3f, 0.4f, 1), Ev(0.5f, 1.0f, 0, 115), Ev(0.6f, 1.0f, 0, 4)];
            ds3 = [Ev(0.3f, 0.45f, 1), Ev(1.2f, 1.6f, 0, 30)];
            now = Build(ds3, er, Hits(ds3), Hits(er), h => 0, 1.6f);
            var (s1, e1) = now.Place(er[1]);
            var (s2, _) = now.Place(er[2]);
            Check($"slow DS3 recovery: chain opens at {s1:0.00}s, roll at {s2:0.00}s (DS3's own window 1.20s)", s1 >= 1.2f - 0.001f && s2 >= 1.2f - 0.001f && e1 >= s1);
            Check($"slow DS3 recovery: {now.Held} windows held back", now.Held == 2);

            // 3. Friede's R1 case (one hit each, a shared recovery ID): the shared anchor stays where 1.0.89 put it, and
            //    a template chain window that opened before DS3's first exit (0.70s) now waits for it.
            er = [Ev(0.35f, 0.45f, 1), Ev(0.6f, 1.0f, 0, 4), Ev(0.55f, 1.0f, 0, 115)];
            ds3 = [Ev(0.40f, 0.50f, 1), Ev(0.7f, 1.4f, 0, 4)];
            Guard = false;
            old = Build(ds3, er, Hits(ds3), Hits(er), h => 0, 1.5f);
            var before = er.Skip(1).Select(old.Place).ToList();
            Guard = true;
            now = Build(ds3, er, Hits(ds3), Hits(er), h => 0, 1.5f);
            var after = er.Skip(1).Select(now.Place).ToList();
            Check($"shared cancel ID: JT 4 stays at {after[0].Start:0.00}s (1.0.89: {before[0].Start:0.00}s)", Math.Abs(after[0].Start - before[0].Start) < 0.002f);
            Check($"shared cancel ID: chain {before[1].Start:0.00}s -> {after[1].Start:0.00}s, not before DS3's first exit 0.70s", after[1].Start >= 0.7f - 0.001f);

            // 4. A move without hits (e.g. a guard): no guard applied, plain stretch to DS3's length.
            er = [Ev(0.2f, 0.8f, 0, 4)];
            ds3 = [Ev(0.3f, 1.0f, 0, 4)];
            now = Build(ds3, er, Hits(ds3), Hits(er), h => 0, 1.6f);
            Check("no hits: no guard", now.Recovery is null && now.Place(er[0]).Start > 0.29f);

            // 5. Elden Ring's "until the end" sentinel (100 s) on a stance loop: the attack window (67 100150, 0-2.33 s on
            //    a 2.5 s template) must cover the 3.33 s DS3 loop, not 0.08 s of it; the sentinel end stays 100.
            er = [Ev(0.0f, 2.33f, 67, 100150), Ev(0.0f, 100.0f, 605, 0), Ev(2.4f, 2.5f, 0, 4)];
            ds3 = [Ev(0.0f, 3.2f, 67, 100150), Ev(3.3f, 3.333f, 0, 30)];
            Guard = false;
            old = Build(ds3, er, Hits(ds3), Hits(er), h => 0, 3.333f);
            var oldWindow = old.Place(er[0]);
            Guard = true;
            now = Build(ds3, er, Hits(ds3), Hits(er), h => 0, 3.333f);
            var window = now.Place(er[0]);
            Check($"sentinel end: stance window {oldWindow.End:0.00}s long before, {window.End:0.00}s now (DS3 loop 3.33s)", oldWindow.End < 0.2f && window.End > 3.0f);
            Check($"sentinel end: the until-the-end event keeps its 100s end ({now.Place(er[1]).End:0})", now.Place(er[1]).End >= 99.9f);
        }
        finally
        {
            Guard = saved;
        }
        Console.WriteLine($"timing-selftest: {(failures == 0 ? "all passed" : $"{failures} failed")}");
        return failures == 0 ? 0 : 1;
    }
}
