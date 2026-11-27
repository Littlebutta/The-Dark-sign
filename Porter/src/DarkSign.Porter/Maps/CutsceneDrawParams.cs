using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The draw settings of DS3's cutscenes (2026-11-24, High Wall cutscene on 1.0.610: lit by its lamps again but far
/// darker than DS3's). Both games keep one draw params file per cutscene, /param/drawparam/sAA_BB_NNNN.gparam, beside
/// the map's. A cut's events pick, per draw group, a bank and an ID: bank 0 is the cutscene's own file, bank 1 the
/// default (DS3 s30_00_0000 cut0010: light set 0/10, fog 1/0, tone map 1/0; Elden Ring s10_00_0030 cut0110: light set
/// 0/110, shadow 0/110, tone map 0/110, colour grading 1/0).
/// <para>DS3 splits these over two kinds of event, 199 (light set, fog, scattering, environment map) and 215 (shadow,
/// tone map, depth of field, bloom, colour grading, light shaft, blurs, SSAO ...), where Elden Ring has one, 197
/// (GparamAllSetting), with the same field names for the post-process groups and its own for the light set
/// (beginLightSetAllBank/Id, and Elden Ring's misspelt endLighSetAlltBank). Up to 1.0.610 the port dropped both DS3
/// kinds and wrote no cutscene file, so every ported cutscene was lit by the map's own light set 0 and its default
/// post-process: the High Wall's opening came out near black where DS3 lights each cut with sets 10 to 90.</para>
/// <para>Now each 199 and 215 event becomes a 197 event (<see cref="ConvertEvents"/>) carrying the light set and the
/// post-process choices of both DS3 events that run at its start; and the slot writes the cutscene's file
/// (<see cref="Write"/>): the host map's Elden Ring file with DS3's cutscene file applied as the map port applies a
/// map's (<see cref="MapAtmosphere.Apply"/>), plus every DS3 light set the cuts name. DS3's post-process IDs other than
/// the base (tone map 90, shadow 10 to 90 ...) have no converted values, so their bank-0 choices take the file's base
/// (ID 0), which holds DS3's converted cutscene look. Fog, scattering and environment map choices have no 197 field
/// and keep Elden Ring's default.</para>
/// <para>A light set DS3 gives to one part (kinds 187 and 183 on a timeline that hangs on a part: the High Wall
/// flight's "sky dome's own light" and "castle light", a boss's "character light", 214 events over DS3's cutscenes)
/// is Elden Ring's kind 199 (LightSetModel, beginLightSetModelBank/Id on a part's timeline, 108 events in eight of
/// its own cutscenes). Up to 1.0.66x those were turned into scene-wide 197 events too, so a cut with a sky dome ran
/// three scene-wide choices at once and the part lost its own light (2026-11-26, the flight to the Undead
/// Settlement). DS3's per-part fog (188, 69 events) has no Elden Ring kind and stays left out.</para>
/// </summary>
public static class CutsceneDrawParams
{
    public const int ErKind = 197;
    public const int Ds3LightKind = 199;

    /// <summary>
    /// Every DS3 kind that picks a light set with beginLightBank/Id: 199 (all-in-one), 183 (the same fields; the Dancer's
    /// intro s30_00_0010 lights all its cuts with set 120 through it) and 187 (light set only, s30_00_0000 cuts 0030-0050).
    /// </summary>
    public static readonly IReadOnlySet<int> Ds3LightKinds = new HashSet<int> { 183, 187, Ds3LightKind };
    public const int Ds3PostKind = 215;
    /// <summary>Elden Ring's light set of one part (LightSetModel); DS3's scene-wide kind has the same number.</summary>
    public const int ErPartLightKind = 199;
    /// <summary>DS3 kinds that may hang on a part and then set that part's light set alone.</summary>
    public static readonly IReadOnlySet<int> Ds3PartLightKinds = new HashSet<int> { 183, 187 };

    /// <summary>DS3's light-set fields under Elden Ring 199's names (one part's light set).</summary>
    public static readonly IReadOnlyDictionary<string, string> PartLightRenames = new Dictionary<string, string>
    {
        ["beginLightBank"] = "beginLightSetModelBank",
        ["beginLightId"] = "beginLightSetModelId",
        ["endLightBank"] = "endLightSetModelBank",
        ["endLightId"] = "endLightSetModelId",
    };

    /// <summary>DS3 199's light-set fields under Elden Ring 197's names.</summary>
    public static readonly IReadOnlyDictionary<string, string> LightRenames = new Dictionary<string, string>
    {
        ["beginLightBank"] = "beginLightSetAllBank",
        ["beginLightId"] = "beginLightSetAllId",
        ["endLightBank"] = "endLighSetAlltBank",
        ["endLightId"] = "endLightSetAllId",
    };

    private const string LightBank = "beginLightSetAllBank", LightId = "beginLightSetAllId";

    /// <summary>The value a 197 field takes when no DS3 event sets it: bank 1, ID 0 (the default) to begin with, -1 at the end.</summary>
    public static int Default(string name) =>
        name.StartsWith("end", StringComparison.Ordinal) ? -1 : name.EndsWith("Bank", StringComparison.Ordinal) ? 1 : 0;

    public sealed class Result
    {
        public int Events;
        /// <summary>Events that set one part's light set, now Elden Ring's kind 199.</summary>
        public int PartEvents;
        /// <summary>The resources now of an Elden Ring draw kind whose number DS3 uses too (199): the cast must not
        /// take them for DS3's (<see cref="CutsceneCast.Unplayable"/>).</summary>
        public HashSet<MQB.Resource> ErResources { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>DS3 light sets the cuts name in the cutscene's own file (bank 0).</summary>
        public SortedSet<int> LightSets { get; } = [];
        /// <summary>The fog each DS3 scene event (199) names, bank and ID, with the frames it is named for. Bank 1 is
        /// the map's draw params, bank 0 the cutscene's own.</summary>
        public Dictionary<(int Bank, int Id), int> SceneFogs { get; } = [];
        /// <summary>The fog the scene is under longest, null when no DS3 scene event names one.</summary>
        public (int Bank, int Id)? SceneFog => SceneFogs.Count == 0 ? null : SceneFogs.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    /// <summary>
    /// Turns the timeline's DS3 199 and 215 resources into Elden Ring 197 ones and rewrites their events with
    /// Elden Ring's full field list. Does nothing when <paramref name="erFields"/> (Elden Ring's 197 event fields, in
    /// order, with their types) is empty.
    /// </summary>
    /// <param name="erPartFields">Elden Ring's 199 event fields (one part's light set); null or empty leaves DS3's
    /// per-part light sets as they are, for the cast to leave out.</param>
    public static Result ConvertEvents(MQB mqb, IReadOnlyList<(string Name, MQB.Parameter.DataType Type)> erFields,
        IReadOnlyList<(string Name, MQB.Parameter.DataType Type)>? erPartFields = null)
    {
        var result = new Result();
        if (erFields.Count == 0)
            return result;
        var known = erFields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        int KindOf(MQB.Event e) => e.ResourceIndex >= 0 && e.ResourceIndex < mqb.Resources.Count ? mqb.Resources[e.ResourceIndex].Unk48 : -1;

        // One part's light set: a 183/187 resource whose every event sits on a timeline that hangs on a part's.
        var partLights = PartLightResources(mqb);
        bool IsPartLight(MQB.Event e) => e.ResourceIndex >= 0 && e.ResourceIndex < mqb.Resources.Count && partLights.Contains(mqb.Resources[e.ResourceIndex]);
        bool IsDraw(MQB.Event e) => !IsPartLight(e) && (Ds3LightKinds.Contains(KindOf(e)) || KindOf(e) == Ds3PostKind);
        if (partLights.Count > 0)
        {
            bool have = erPartFields is { Count: > 0 } && PartLightRenames.Values.All(n => erPartFields.Any(f => f.Name == n));
            foreach (var e in mqb.Cuts.SelectMany(c => c.Timelines).SelectMany(t => t.Events).Where(IsPartLight))
            {
                if (!have)
                    continue;
                var values = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var p in e.Parameters)
                    if (TryInt(p.Value, out int v) && PartLightRenames.TryGetValue(p.Name, out var name))
                        values.TryAdd(name, v);
                if (values.TryGetValue("beginLightSetModelBank", out int bank) && bank == 0 && values.TryGetValue("beginLightSetModelId", out int id) && id >= 0)
                    result.LightSets.Add(id);
                e.Parameters = erPartFields!.Select(f => new MQB.Parameter
                {
                    Name = f.Name, Type = f.Type, MemberCount = 1,
                    Value = Typed(values.TryGetValue(f.Name, out var have2) ? have2 : Default(f.Name), f.Type),
                }).ToList();
                result.PartEvents++;
            }
            if (have)
                foreach (var r in partLights)
                {
                    r.Unk48 = ErPartLightKind;
                    r.Parameters.Clear();
                    result.ErResources.Add(r);
                }
        }

        void Fill(Dictionary<string, int> values, MQB.Event e)
        {
            int kind = KindOf(e);
            foreach (var p in e.Parameters)
            {
                if (!TryInt(p.Value, out int v))
                    continue;
                string? name = Ds3LightKinds.Contains(kind) ? LightRenames.GetValueOrDefault(p.Name) : known.Contains(p.Name) && !LightRenames.Values.Contains(p.Name) ? p.Name : null;
                if (name is not null)
                    values.TryAdd(name, v);
            }
        }

        var rewrites = new List<(MQB.Event Event, List<MQB.Parameter> Params)>();
        foreach (var cut in mqb.Cuts)
        {
            var events = cut.Timelines.SelectMany(t => t.Events).Where(IsDraw).ToList();
            foreach (var e in events)
            {
                var values = new Dictionary<string, int>(StringComparer.Ordinal);
                Fill(values, e);
                if (Ds3LightKinds.Contains(KindOf(e))
                    && e.Parameters.FirstOrDefault(p => p.Name == "beginFogBank") is { } fb && TryInt(fb.Value, out int fogBank)
                    && e.Parameters.FirstOrDefault(p => p.Name == "beginFogId") is { } fi && TryInt(fi.Value, out int fogId) && fogBank is 0 or 1 && fogId >= 0)
                    result.SceneFogs[(fogBank, fogId)] = result.SceneFogs.GetValueOrDefault((fogBank, fogId)) + Math.Max(1, (int)e.Duration);
                // The other kind's events running at this one's start fill the fields this one lacks.
                foreach (var other in events)
                    if (KindOf(other) != KindOf(e) && other.StartFrame <= e.StartFrame && e.StartFrame < other.StartFrame + Math.Max(1, other.Duration))
                        Fill(values, other);
                if (values.TryGetValue(LightBank, out int bank) && bank == 0 && values.TryGetValue(LightId, out int id) && id >= 0)
                    result.LightSets.Add(id);
                var ps = new List<MQB.Parameter>(erFields.Count);
                foreach (var (name, type) in erFields)
                {
                    int v = values.TryGetValue(name, out var have) ? have : Default(name);
                    // Only the light set keeps DS3's own IDs in the cutscene's file; the other groups take its base.
                    if (name.StartsWith("begin", StringComparison.Ordinal) && name.EndsWith("Id", StringComparison.Ordinal) && name != LightId
                        && values.TryGetValue(name[..^2] + "Bank", out int b) && b == 0)
                        v = 0;
                    ps.Add(new MQB.Parameter { Name = name, Type = type, Value = Typed(v, type), MemberCount = 1 });
                }
                rewrites.Add((e, ps));
            }
        }
        foreach (var (e, ps) in rewrites)
            e.Parameters = ps;
        result.Events = rewrites.Count;
        foreach (var r in mqb.Resources.Where(r => !partLights.Contains(r) && (Ds3LightKinds.Contains(r.Unk48) || r.Unk48 == Ds3PostKind)))
        {
            r.Unk48 = ErKind;
            r.Parameters.Clear();
        }
        return result;
    }

    /// <summary>
    /// DS3's light-set resources (183, 187) that only ever light one part: every event of theirs sits on a timeline
    /// whose parent timeline (Unk10) is a part's. A 187 that hangs on the cut's Havok file lights the scene, as 199 does.
    /// </summary>
    public static HashSet<MQB.Resource> PartLightResources(MQB mqb)
    {
        var onPart = new Dictionary<MQB.Resource, bool>(ReferenceEqualityComparer.Instance);
        foreach (var cut in mqb.Cuts)
            foreach (var tl in cut.Timelines)
            {
                bool part = tl.Unk10 >= 0 && tl.Unk10 < cut.Timelines.Count
                            && cut.Timelines[tl.Unk10].Events.FirstOrDefault() is { } pe && pe.ResourceIndex >= 0 && pe.ResourceIndex < mqb.Resources.Count
                            && mqb.Resources[pe.ResourceIndex].Unk48 == CutsceneCast.PartKind;
                foreach (var e in tl.Events)
                {
                    if (e.ResourceIndex < 0 || e.ResourceIndex >= mqb.Resources.Count)
                        continue;
                    var r = mqb.Resources[e.ResourceIndex];
                    if (Ds3PartLightKinds.Contains(r.Unk48))
                        onPart[r] = onPart.GetValueOrDefault(r, true) && part;
                }
            }
        return onPart.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet<MQB.Resource>(ReferenceEqualityComparer.Instance);
    }

    /// <summary>Elden Ring's 199 event fields (one part's light set) as its own cutscenes carry them.</summary>
    public static List<(string Name, MQB.Parameter.DataType Type)> ErPartFields(CutscenePort.Catalog catalog) =>
        catalog.Events.TryGetValue(ErPartLightKind, out var d) ? d.Select(kv => (kv.Key, kv.Value)).ToList() : [];

    /// <summary>Elden Ring's 197 event fields as its own cutscenes carry them (the catalog's first-seen order).</summary>
    public static List<(string Name, MQB.Parameter.DataType Type)> ErFields(CutscenePort.Catalog catalog) =>
        catalog.Events.TryGetValue(ErKind, out var d) ? d.Select(kv => (kv.Key, kv.Value)).ToList() : [];

    public static string Ds3Path(string ds3Name) => $"/param/drawparam/{ds3Name}.gparam.dcx";
    public static string ErPath(string erName) => $"/param/drawparam/{erName}.gparam.dcx";

    /// <summary>
    /// Writes the cutscene's draw params for Elden Ring: the host map's file with DS3's cutscene file applied as a map's
    /// is, and the light sets <paramref name="lightSets"/> the cuts name. Returns a line for the log, null when DS3 has
    /// no file for the cutscene.
    /// </summary>
    /// <param name="sceneFog">The fog DS3's scene events name (<see cref="Result.SceneFog"/>). The file's fog is made
    /// from row 0 of DS3's cutscene file, which is right only when the scene names bank 0, ID 0: the flight to the
    /// Undead Settlement names bank 1, ID 0 (the map's fog, warm, 0.12 strong from 20 m), and its own file's row 0 is
    /// an empty one (black, no distance), so the cutscene had black fog of no strength. The row the scene names is
    /// put in row 0's place first.</param>
    public static string? Write(PorterContext ctx, GameArchives ds3, GameArchives er, string ds3Name, string erName, MapId host, MSB3 ds3Msb,
        IReadOnlyCollection<int> lightSets, (int Bank, int Id)? sceneFog = null, MapId? ds3Map = null)
    {
        var source = ds3.Contains(Ds3Path(ds3Name)) ? Ds3Path(ds3Name) : ds3.Contains(Ds3Path(ds3Name)[..^4]) ? Ds3Path(ds3Name)[..^4] : null;
        if (source is null)
            return null;
        var hostPath = MapAtmosphere.GparamPath(host);
        if (!er.Contains(hostPath))
            return $"{ds3Name} draw params: the host has none ({hostPath.TrimStart('/')}); not written";
        var (hostBytes, compression) = er.ReadWithCompression(hostPath);
        var gparam = GPARAM.Read(hostBytes);
        var ds3Gparam = GPARAM.Read(ds3.Read(source));
        string fogNote = "";
        if (sceneFog is { } fog && fog != (0, 0))
        {
            GPARAM? from = fog.Bank == 0 ? ds3Gparam
                : ds3Map is { } m && MapAtmosphere.Ds3GparamPaths(m).FirstOrDefault(ds3.Contains) is { } mapPath ? GPARAM.Read(ds3.Read(mapPath)) : null;
            bool used = from is not null && UseFogRow(ds3Gparam, from, fog.Id);
            fogNote = $"; fog from {(fog.Bank == 0 ? "the cutscene's" : "the map's")} row {fog.Id}, the one DS3's scene names" + (used ? "" : " (not found: the cutscene's row 0 kept)");
        }
        var changes = MapAtmosphere.Apply(gparam, ds3Gparam, MapAtmosphere.GroundHeight(ds3Msb),
            MapLighting.OffsetDegrees(ds3Msb), lightSets, MapAtmosphere.Layer(ds3Msb), lightReference: MapAtmosphere.ReadLightReference(er),
            ds3Reference: MapLightScale.ReadDs3(ds3, MapLightScale.Ds3Reference));
        var target = ctx.ModPath(MapPorter.ModParts(ErPath(erName)));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, DCX.Compress(gparam.Write(), compression));
        var written = MapAtmosphere.Group(gparam, "LightSet") is { } g ? MapLightSets.Ids(g) : [];
        var missing = lightSets.Where(id => !written.Contains(id)).ToList();
        return $"{ds3Name} draw params -> {erName}.gparam: light sets {string.Join(" ", written.Order())}"
               + (missing.Count > 0 ? $" (DS3's file lacks {string.Join(" ", missing)})" : "") + $"; {changes.Count} groups changed" + fogNote;
    }

    /// <summary>
    /// Puts row <paramref name="id"/> of <paramref name="from"/>'s fog in row 0 of <paramref name="target"/>'s (added
    /// where a param has no row 0), so the fog conversion, which reads row 0, reads the row DS3's scene names. False
    /// when either file has no fog group or the row isn't there.
    /// </summary>
    public static bool UseFogRow(GPARAM target, GPARAM from, int id)
    {
        var source = MapAtmosphere.Group(from, "Fog");
        var fog = MapAtmosphere.Group(target, "Fog");
        if (source is null || fog is null || !source.Params.Any(p => p.ValueIDs.Contains(id)))
            return false;
        foreach (var p in fog.Params)
        {
            if (source.Params.FirstOrDefault(x => x.Name1 == p.Name1) is not { } sp || sp.ValueIDs.IndexOf(id) is not (>= 0 and var at) || at >= sp.Values.Count)
                continue;
            int i = p.ValueIDs.IndexOf(0);
            if (i >= 0 && i < p.Values.Count)
                p.Values[i] = sp.Values[at];
            else
            {
                p.ValueIDs.Insert(0, 0);
                p.Values.Insert(0, sp.Values[at]);
                if (p.TimeOfDay is { Count: > 0 })
                    p.TimeOfDay.Insert(0, 0);
            }
        }
        return true;
    }

    private static bool TryInt(object? value, out int v)
    {
        switch (value)
        {
            case int i: v = i; return true;
            case uint u: v = (int)u; return true;
            case short s: v = s; return true;
            case byte b: v = b; return true;
            case sbyte sb: v = sb; return true;
            default: v = 0; return false;
        }
    }

    private static object Typed(int v, MQB.Parameter.DataType type) => type switch
    {
        MQB.Parameter.DataType.UInt => (object)(uint)v,
        MQB.Parameter.DataType.Short => (object)(short)v,
        MQB.Parameter.DataType.Byte => (object)(byte)v,
        MQB.Parameter.DataType.SByte => (object)(sbyte)v,
        MQB.Parameter.DataType.Float => (object)(float)v,
        _ => (object)v,
    };
}
