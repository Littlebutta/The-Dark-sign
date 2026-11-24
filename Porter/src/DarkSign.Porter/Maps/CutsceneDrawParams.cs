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
/// </summary>
public static class CutsceneDrawParams
{
    public const int ErKind = 197;
    public const int Ds3LightKind = 199;
    public const int Ds3PostKind = 215;

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
        /// <summary>DS3 light sets the cuts name in the cutscene's own file (bank 0).</summary>
        public SortedSet<int> LightSets { get; } = [];
    }

    /// <summary>
    /// Turns the timeline's DS3 199 and 215 resources into Elden Ring 197 ones and rewrites their events with
    /// Elden Ring's full field list. Does nothing when <paramref name="erFields"/> (Elden Ring's 197 event fields, in
    /// order, with their types) is empty.
    /// </summary>
    public static Result ConvertEvents(MQB mqb, IReadOnlyList<(string Name, MQB.Parameter.DataType Type)> erFields)
    {
        var result = new Result();
        if (erFields.Count == 0)
            return result;
        var known = erFields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        int KindOf(MQB.Event e) => e.ResourceIndex >= 0 && e.ResourceIndex < mqb.Resources.Count ? mqb.Resources[e.ResourceIndex].Unk48 : -1;
        bool IsDraw(MQB.Event e) => KindOf(e) is Ds3LightKind or Ds3PostKind;

        void Fill(Dictionary<string, int> values, MQB.Event e)
        {
            int kind = KindOf(e);
            foreach (var p in e.Parameters)
            {
                if (!TryInt(p.Value, out int v))
                    continue;
                string? name = kind == Ds3LightKind ? LightRenames.GetValueOrDefault(p.Name) : known.Contains(p.Name) && !LightRenames.Values.Contains(p.Name) ? p.Name : null;
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
        foreach (var r in mqb.Resources.Where(r => r.Unk48 is Ds3LightKind or Ds3PostKind))
        {
            r.Unk48 = ErKind;
            r.Parameters.Clear();
        }
        return result;
    }

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
    public static string? Write(PorterContext ctx, GameArchives ds3, GameArchives er, string ds3Name, string erName, MapId host, MSB3 ds3Msb,
        IReadOnlyCollection<int> lightSets)
    {
        var source = ds3.Contains(Ds3Path(ds3Name)) ? Ds3Path(ds3Name) : ds3.Contains(Ds3Path(ds3Name)[..^4]) ? Ds3Path(ds3Name)[..^4] : null;
        if (source is null)
            return null;
        var hostPath = MapAtmosphere.GparamPath(host);
        if (!er.Contains(hostPath))
            return $"{ds3Name} draw params: the host has none ({hostPath.TrimStart('/')}); not written";
        var (hostBytes, compression) = er.ReadWithCompression(hostPath);
        var gparam = GPARAM.Read(hostBytes);
        var changes = MapAtmosphere.Apply(gparam, GPARAM.Read(ds3.Read(source)), MapAtmosphere.GroundHeight(ds3Msb),
            MapLighting.OffsetDegrees(ds3Msb), lightSets, MapAtmosphere.Layer(ds3Msb));
        var target = ctx.ModPath(MapPorter.ModParts(ErPath(erName)));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, DCX.Compress(gparam.Write(), compression));
        var written = MapAtmosphere.Group(gparam, "LightSet") is { } g ? MapLightSets.Ids(g) : [];
        var missing = lightSets.Where(id => !written.Contains(id)).ToList();
        return $"{ds3Name} draw params -> {erName}.gparam: light sets {string.Join(" ", written.Order())}"
               + (missing.Count > 0 ? $" (DS3's file lacks {string.Join(" ", missing)})" : "") + $"; {changes.Count} groups changed";
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
