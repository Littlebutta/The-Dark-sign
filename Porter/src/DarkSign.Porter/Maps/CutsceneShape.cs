using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// What Elden Ring's own cutscene timelines hold, beyond the resource kinds and parameter names the cutscene port's
/// <see cref="CutscenePort.Catalog"/> keeps: for each resource kind, how its events fill their unnamed fields (a fixed
/// value, or the event's duration, end frame or its cut's length), its timelines' Unk10, its resources' parent kind,
/// and the full ordered parameter list (with the values Elden Ring uses most) of its resources, timelines and events.
/// Learned from every Elden Ring cutscene, so what it says is what the game's own files always do.
/// <para><see cref="Check"/> lists where a converted timeline differs (cutscene-diag); <see cref="Conform"/> makes it
/// match: fields that Elden Ring fills the same way in at least <see cref="RuleShare"/> of its events are set that
/// way, and parameter lists get Elden Ring's order, with any parameter DS3 lacks at Elden Ring's most used value.
/// DARKSIGN_CUTSCENE_CONFORM=0 turns the conforming off.</para>
/// </summary>
public sealed class CutsceneShape
{
    public static bool ConformEnabled => Environment.GetEnvironmentVariable("DARKSIGN_CUTSCENE_CONFORM") != "0";

    /// <summary>A field rule counts when Elden Ring's events follow it this often (and there are enough of them).</summary>
    public const double RuleShare = 0.97;
    public const int RuleMinimum = 5;

    public static readonly string[] Fields = ["Unk08", "Unk14", "Unk18", "Unk1C", "Unk20", "Unk28"];

    private static int Get(MQB.Event e, int f) => f switch
    {
        0 => e.Unk08, 1 => e.Unk14, 2 => e.Unk18, 3 => e.Unk1C, 4 => e.Unk20, _ => e.Unk28,
    };

    private static void Set(MQB.Event e, int f, int v)
    {
        switch (f)
        {
            case 0: e.Unk08 = v; break;
            case 1: e.Unk14 = v; break;
            case 2: e.Unk18 = v; break;
            case 3: e.Unk1C = v; break;
            case 4: e.Unk20 = v; break;
            default: e.Unk28 = v; break;
        }
    }

    /// <summary>The relations a field can have with its event; "const" is a fixed value.</summary>
    public static readonly string[] Relations = ["duration", "end", "start", "cut", "cut+1", "duration-1"];

    private static int Related(string relation, MQB.Event e, MQB.Cut cut) => relation switch
    {
        "duration" => e.Duration,
        "end" => e.StartFrame + e.Duration,
        "start" => e.StartFrame,
        "cut" => cut.Duration,
        "cut+1" => cut.Duration + 1,
        "duration-1" => e.Duration - 1,
        _ => throw new ArgumentOutOfRangeException(nameof(relation)),
    };

    /// <summary>How Elden Ring fills one field of one kind's events: a relation, or "const" with a value.</summary>
    public sealed record Rule(string Relation, int Value, int Hits, int Total)
    {
        public double Share => Total == 0 ? 0 : (double)Hits / Total;
        public bool Holds => Total >= RuleMinimum && Share >= RuleShare;
        public int Want(MQB.Event e, MQB.Cut cut) => Relation == "const" ? Value : Related(Relation, e, cut);
        public override string ToString() => $"{(Relation == "const" ? $"= {Value}" : $"= {Relation}")} ({Hits}/{Total})";
    }

    /// <summary>An ordered parameter list and the values Elden Ring uses most for each.</summary>
    public sealed record Signature(List<(string Name, MQB.Parameter.DataType Type, MQB.Parameter Typical)> Params, int Hits, int Total)
    {
        public string Key => string.Join(",", Params.Select(p => p.Name));
    }

    public Dictionary<int, Rule[]> EventRules { get; } = new();
    public Dictionary<int, Rule> TimelineUnk10 { get; } = new();
    public Dictionary<int, (int ParentKind, int Hits, int Total)> ParentKind { get; } = new();
    public Dictionary<int, Signature> ResourceParams { get; } = new();
    public Dictionary<int, Signature> TimelineParams { get; } = new();
    /// <summary>The player's own part timelines (c0000_*): their parameters can differ from other parts' (equipment display).</summary>
    public Dictionary<int, Signature> PlayerTimelineParams { get; } = new();
    public Dictionary<int, Signature> EventParams { get; } = new();
    /// <summary>For each kind, in how many of Elden Ring's cutscenes it appears.</summary>
    public Dictionary<int, int> KindFiles { get; } = new();
    public int Files { get; private set; }
    public Dictionary<float, int> Framerates { get; } = new();
    public Rule? CutUnk44 { get; private set; }
    /// <summary>For each kind, how many of Elden Ring's events end after their cut (end frame > cut length + 1), and how many events there are.</summary>
    public Dictionary<int, (int Past, int Total)> PastCut { get; } = new();

    /// <summary>Whether Elden Ring never lets events of this kind end after their cut (so a converted one that does is cut short).</summary>
    public bool NeverPastCut(int kind) => PastCut.TryGetValue(kind, out var p) && p.Total >= RuleMinimum && p.Past == 0;

    private static string ValueKey(MQB.Parameter p) => p.Value switch
    {
        null => "null",
        System.Collections.IEnumerable list and not string => string.Join(",", list.Cast<object>()),
        _ => p.Value.ToString() ?? "",
    };

    private sealed class SigTally
    {
        public readonly Dictionary<string, int> Keys = new();
        public readonly Dictionary<string, List<MQB.Parameter>> Examples = new();
        // name -> value key -> (count, example)
        public readonly Dictionary<string, Dictionary<string, (int Count, MQB.Parameter Example)>> Values = new();
        public int Total;

        public void Add(List<MQB.Parameter> ps)
        {
            Total++;
            var key = string.Join(",", ps.Select(p => p.Name));
            Keys[key] = Keys.GetValueOrDefault(key) + 1;
            Examples.TryAdd(key, ps);
            foreach (var p in ps)
            {
                var d = Values.TryGetValue(p.Name, out var x) ? x : Values[p.Name] = new();
                var v = ValueKey(p);
                d[v] = d.TryGetValue(v, out var c) ? (c.Count + 1, c.Example) : (1, p);
            }
        }

        public Signature? Best()
        {
            if (Keys.Count == 0)
                return null;
            var (key, hits) = Keys.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).First();
            var list = Examples[key].Select(p => (p.Name, p.Type, Values[p.Name].Values.OrderByDescending(v => v.Count).First().Example)).ToList();
            return new Signature(list, hits, Total);
        }
    }

    /// <summary>Learns the shape from Elden Ring's own timelines.</summary>
    public static CutsceneShape Learn(IEnumerable<MQB> er)
    {
        var shape = new CutsceneShape();
        // kind -> field -> relation/const tallies
        var hits = new Dictionary<int, (Dictionary<string, int>[] Rel, Dictionary<int, int>[] Const, int Total)>();
        var unk10 = new Dictionary<int, Dictionary<int, int>>();
        var parents = new Dictionary<int, Dictionary<int, int>>();
        var resSig = new Dictionary<int, SigTally>();
        var tlSig = new Dictionary<int, SigTally>();
        var playerSig = new Dictionary<int, SigTally>();
        var evSig = new Dictionary<int, SigTally>();
        var unk44 = new Dictionary<int, int>();
        int cuts = 0;
        SigTally Tally(Dictionary<int, SigTally> d, int k) => d.TryGetValue(k, out var t) ? t : d[k] = new SigTally();

        foreach (var mqb in er)
        {
            shape.Files++;
            shape.Framerates[mqb.Framerate] = shape.Framerates.GetValueOrDefault(mqb.Framerate) + 1;
            foreach (var k in mqb.Resources.Select(r => r.Unk48).Distinct())
                shape.KindFiles[k] = shape.KindFiles.GetValueOrDefault(k) + 1;
            foreach (var r in mqb.Resources)
            {
                Tally(resSig, r.Unk48).Add(r.Parameters);
                var pk = r.ParentIndex >= 0 && r.ParentIndex < mqb.Resources.Count ? mqb.Resources[r.ParentIndex].Unk48 : -1;
                var d = parents.TryGetValue(r.Unk48, out var x) ? x : parents[r.Unk48] = new();
                d[pk] = d.GetValueOrDefault(pk) + 1;
            }
            foreach (var cut in mqb.Cuts)
            {
                cuts++;
                unk44[cut.Unk44] = unk44.GetValueOrDefault(cut.Unk44) + 1;
                foreach (var tl in cut.Timelines)
                {
                    var tk = CutscenePort.TimelineKind(mqb, tl);
                    var u = unk10.TryGetValue(tk, out var y) ? y : unk10[tk] = new();
                    u[tl.Unk10] = u.GetValueOrDefault(tl.Unk10) + 1;
                    Tally(tlSig, tk).Add(tl.Parameters);
                    if (IsPlayer(mqb, tl))
                        Tally(playerSig, tk).Add(tl.Parameters);
                    foreach (var ev in tl.Events)
                    {
                        int k = ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count ? mqb.Resources[ev.ResourceIndex].Unk48 : -1;
                        Tally(evSig, k).Add(ev.Parameters);
                        var pc = shape.PastCut.GetValueOrDefault(k);
                        shape.PastCut[k] = (pc.Past + (ev.StartFrame + ev.Duration > cut.Duration + 1 ? 1 : 0), pc.Total + 1);
                        if (!hits.TryGetValue(k, out var h))
                            hits[k] = h = (Fields.Select(_ => new Dictionary<string, int>()).ToArray(), Fields.Select(_ => new Dictionary<int, int>()).ToArray(), 0);
                        for (int f = 0; f < Fields.Length; f++)
                        {
                            int v = Get(ev, f);
                            h.Const[f][v] = h.Const[f].GetValueOrDefault(v) + 1;
                            foreach (var rel in Relations)
                                if (v == Related(rel, ev, cut))
                                    h.Rel[f][rel] = h.Rel[f].GetValueOrDefault(rel) + 1;
                        }
                        hits[k] = (h.Rel, h.Const, h.Total + 1);
                    }
                }
            }
        }
        foreach (var (k, h) in hits)
        {
            var rules = new Rule[Fields.Length];
            for (int f = 0; f < Fields.Length; f++)
            {
                var (cv, cn) = h.Const[f].OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).FirstOrDefault();
                var best = new Rule("const", cv, cn, h.Total);
                foreach (var (rel, n) in h.Rel[f])
                    if (n > best.Hits)
                        best = new Rule(rel, 0, n, h.Total);
                rules[f] = best;
            }
            shape.EventRules[k] = rules;
        }
        foreach (var (k, d) in unk10)
        {
            var (v, n) = d.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).First();
            shape.TimelineUnk10[k] = new Rule("const", v, n, d.Values.Sum());
        }
        foreach (var (k, d) in parents)
        {
            var (v, n) = d.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).First();
            shape.ParentKind[k] = (v, n, d.Values.Sum());
        }
        foreach (var (k, t) in resSig) if (t.Best() is { } s) shape.ResourceParams[k] = s;
        foreach (var (k, t) in tlSig) if (t.Best() is { } s) shape.TimelineParams[k] = s;
        foreach (var (k, t) in playerSig) if (t.Best() is { } s) shape.PlayerTimelineParams[k] = s;
        foreach (var (k, t) in evSig) if (t.Best() is { } s) shape.EventParams[k] = s;
        if (unk44.Count > 0)
        {
            var (v, n) = unk44.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).First();
            shape.CutUnk44 = new Rule("const", v, n, cuts);
        }
        return shape;
    }

    /// <summary>Whether a timeline animates the player (its first event's resource is c0000_*).</summary>
    public static bool IsPlayer(MQB mqb, MQB.Timeline tl) =>
        tl.Events.FirstOrDefault(e => e.ResourceIndex >= 0 && e.ResourceIndex < mqb.Resources.Count) is { } ev
        && mqb.Resources[ev.ResourceIndex].Name.StartsWith(CutscenePort.PlayerPart[..5], StringComparison.OrdinalIgnoreCase);

    private static bool SigHolds(Signature s) => s.Total >= RuleMinimum && (double)s.Hits / s.Total >= 0.9;

    /// <summary>Elden Ring's rules, for the report.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"learned from {Files} Elden Ring timelines; framerates {string.Join(" ", Framerates.Select(f => $"{f.Key}:{f.Value}"))}; cut Unk44 {CutUnk44}";
        foreach (var k in EventRules.Keys.Union(TimelineParams.Keys).Order())
        {
            var sb = new StringBuilder($"kind {k} (in {KindFiles.GetValueOrDefault(k)}/{Files} cutscenes):");
            if (EventRules.TryGetValue(k, out var rules))
                for (int f = 0; f < Fields.Length; f++)
                    sb.Append($" {Fields[f]} {rules[f]}{(rules[f].Holds ? "" : "?")};");
            if (TimelineUnk10.TryGetValue(k, out var u)) sb.Append($" timeline Unk10 {u}{(u.Holds ? "" : "?")};");
            if (ParentKind.TryGetValue(k, out var p)) sb.Append($" parent kind {p.ParentKind} ({p.Hits}/{p.Total});");
            if (ResourceParams.TryGetValue(k, out var rs) && rs.Params.Count > 0) sb.Append($" resource params [{rs.Key}] ({rs.Hits}/{rs.Total});");
            if (TimelineParams.TryGetValue(k, out var ts) && ts.Params.Count > 0) sb.Append($" timeline params [{ts.Key}] ({ts.Hits}/{ts.Total}) typical {string.Join(" ", ts.Params.Select(p => $"{p.Name}={ValueKey(p.Typical)}"))};");
            if (PlayerTimelineParams.TryGetValue(k, out var ps) && ps.Params.Count > 0) sb.Append($" player timeline params [{ps.Key}] ({ps.Hits}/{ps.Total}) typical {string.Join(" ", ps.Params.Select(p => $"{p.Name}={ValueKey(p.Typical)}"))};");
            if (EventParams.TryGetValue(k, out var es) && es.Params.Count > 0) sb.Append($" event params [{es.Key}] ({es.Hits}/{es.Total});");
            if (PastCut.TryGetValue(k, out var pc)) sb.Append($" events ending after their cut {pc.Past}/{pc.Total}");
            yield return sb.ToString();
        }
    }

    /// <summary>Where a timeline differs from what Elden Ring's own always do: one line per kind and field, with counts.</summary>
    public List<string> Check(MQB mqb)
    {
        var lines = new List<string>();
        var counts = new SortedDictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        void Note(string what, string example)
        {
            counts[what] = counts.TryGetValue(what, out var c) ? (c.Count + 1, c.Example) : (1, example);
        }
        if (!Framerates.ContainsKey(mqb.Framerate))
            Note("framerate", $"{mqb.Framerate} fps");
        foreach (var (k, n) in KindFiles.Where(x => Files > 0 && x.Value >= Files * 0.9))
            if (!mqb.Resources.Any(r => r.Unk48 == k))
                Note($"kind {k} is in {n}/{Files} Elden Ring cutscenes but not here", "");
        foreach (var r in mqb.Resources)
        {
            if (ParentKind.TryGetValue(r.Unk48, out var p) && p.Total >= RuleMinimum && (double)p.Hits / p.Total >= RuleShare)
            {
                var pk = r.ParentIndex >= 0 && r.ParentIndex < mqb.Resources.Count ? mqb.Resources[r.ParentIndex].Unk48 : -1;
                if (pk != p.ParentKind)
                    Note($"kind {r.Unk48} resource parent kind {pk}, Elden Ring {p.ParentKind}", r.Name);
            }
            if (ResourceParams.TryGetValue(r.Unk48, out var s) && SigHolds(s) && string.Join(",", r.Parameters.Select(x => x.Name)) != s.Key)
                Note($"kind {r.Unk48} resource params [{string.Join(",", r.Parameters.Select(x => x.Name))}], Elden Ring [{s.Key}]", r.Name);
        }
        foreach (var cut in mqb.Cuts)
        {
            if (CutUnk44 is { Holds: true } c44 && cut.Unk44 != c44.Value)
                Note($"cut Unk44 {cut.Unk44}, Elden Ring {c44.Value}", cut.Name);
            foreach (var tl in cut.Timelines)
            {
                var tk = CutscenePort.TimelineKind(mqb, tl);
                if (TimelineUnk10.TryGetValue(tk, out var u) && u.Holds && tl.Unk10 != u.Value)
                    Note($"kind {tk} timeline Unk10 {tl.Unk10}, Elden Ring {u.Value}", cut.Name);
                if (TimelineParams.TryGetValue(tk, out var ts) && SigHolds(ts) && string.Join(",", tl.Parameters.Select(x => x.Name)) != ts.Key)
                    Note($"kind {tk} timeline params [{string.Join(",", tl.Parameters.Select(x => x.Name))}], Elden Ring [{ts.Key}]", cut.Name);
                foreach (var ev in tl.Events)
                {
                    int k = ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count ? mqb.Resources[ev.ResourceIndex].Unk48 : -1;
                    if (!EventRules.ContainsKey(k) && !EventParams.ContainsKey(k))
                    {
                        Note($"kind {k} events: Elden Ring has none of this kind", cut.Name);
                        continue;
                    }
                    if (EventRules.TryGetValue(k, out var rules))
                        for (int f = 0; f < Fields.Length; f++)
                            if (rules[f].Holds && Get(ev, f) != rules[f].Want(ev, cut))
                                Note($"kind {k} event {Fields[f]} {(rules[f].Relation == "const" ? "" : $"!= {rules[f].Relation} ")}(Elden Ring {rules[f]})",
                                    $"{cut.Name} start {ev.StartFrame} dur {ev.Duration}: {Get(ev, f)}, want {rules[f].Want(ev, cut)}");
                    if (EventParams.TryGetValue(k, out var es) && SigHolds(es) && string.Join(",", ev.Parameters.Select(x => x.Name)) != es.Key)
                        Note($"kind {k} event params [{string.Join(",", ev.Parameters.Select(x => x.Name))}], Elden Ring [{es.Key}]", cut.Name);
                    if (NeverPastCut(k) && ev.StartFrame + ev.Duration > cut.Duration + 1)
                        Note($"kind {k} event ends after its cut (end frame > cut length + 1; Elden Ring 0/{PastCut[k].Total})", $"{cut.Name} start {ev.StartFrame} dur {ev.Duration}, cut {cut.Duration}");
                }
            }
        }
        foreach (var (what, (n, example)) in counts)
            lines.Add($"{n,5} x {what}{(example.Length > 0 ? $"   e.g. {example}" : "")}");
        return lines;
    }

    /// <summary>Makes a converted timeline match Elden Ring's shape (see the class summary). Returns how many fields,
    /// timelines and parameter lists changed.</summary>
    public int Conform(MQB mqb)
    {
        int changed = 0;
        List<MQB.Parameter> Fit(List<MQB.Parameter> ps, Signature s)
        {
            var result = new List<MQB.Parameter>(s.Params.Count);
            foreach (var (name, type, typical) in s.Params)
            {
                var have = ps.FirstOrDefault(p => p.Name == name && p.Type == type);
                result.Add(have ?? Copy(typical));
            }
            return result;
        }
        bool Differs(List<MQB.Parameter> ps, Signature s) => string.Join(",", ps.Select(x => x.Name)) != s.Key;

        foreach (var r in mqb.Resources)
            if (ResourceParams.TryGetValue(r.Unk48, out var s) && SigHolds(s) && Differs(r.Parameters, s))
            {
                r.Parameters = Fit(r.Parameters, s);
                changed++;
            }
        foreach (var cut in mqb.Cuts)
        {
            // Elden Ring's cuts all carry the same Unk44 (0); DS3's Dancer cut0042 has 2.
            if (CutUnk44 is { Holds: true } c44 && cut.Unk44 != c44.Value)
            {
                cut.Unk44 = c44.Value;
                changed++;
            }
            foreach (var tl in cut.Timelines)
            {
                // Events that run past the cut's end (DS3 sfx, sound, depth of field, subtitles) are cut short to it, and
                // events that start after it are left out, for the kinds Elden Ring never lets run past a cut.
                int end = cut.Duration + 1;
                int before = tl.Events.Count;
                tl.Events.RemoveAll(ev => NeverPastCut(KindOf(mqb, ev)) && ev.StartFrame >= end);
                changed += before - tl.Events.Count;
                foreach (var ev in tl.Events)
                    if (NeverPastCut(KindOf(mqb, ev)) && ev.StartFrame + ev.Duration > end)
                    {
                        ev.Duration = end - ev.StartFrame;
                        changed++;
                    }
                var tk = CutscenePort.TimelineKind(mqb, tl);
                if (TimelineUnk10.TryGetValue(tk, out var u) && u.Holds && tl.Unk10 != u.Value)
                {
                    tl.Unk10 = u.Value;
                    changed++;
                }
                if (TimelineParams.TryGetValue(tk, out var ts) && SigHolds(ts) && Differs(tl.Parameters, ts))
                {
                    // The player's parameters take the values Elden Ring gives the player, where its lists match.
                    var fit = IsPlayer(mqb, tl) && PlayerTimelineParams.TryGetValue(tk, out var ps) && ps.Key == ts.Key ? ps : ts;
                    tl.Parameters = Fit(tl.Parameters, fit);
                    changed++;
                }
                foreach (var ev in tl.Events)
                {
                    int k = ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count ? mqb.Resources[ev.ResourceIndex].Unk48 : -1;
                    if (EventRules.TryGetValue(k, out var rules))
                        for (int f = 0; f < Fields.Length; f++)
                            if (rules[f].Holds && Get(ev, f) != rules[f].Want(ev, cut))
                            {
                                Set(ev, f, rules[f].Want(ev, cut));
                                changed++;
                            }
                    if (EventParams.TryGetValue(k, out var es) && SigHolds(es) && Differs(ev.Parameters, es))
                    {
                        ev.Parameters = Fit(ev.Parameters, es);
                        changed++;
                    }
                }
            }
        }
        return changed;
    }

    private static int KindOf(MQB mqb, MQB.Event ev) =>
        ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count ? mqb.Resources[ev.ResourceIndex].Unk48 : -1;

    private static MQB.Parameter Copy(MQB.Parameter p) => new()
    {
        Name = p.Name, Type = p.Type, Value = p.Value is Array a ? a.Clone() : p.Value, MemberCount = p.MemberCount,
    };
}
