using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// The TAE events that spawn effects, and where their FXR IDs are. Both games number these types the same (the
/// SpawnFFX family, 95-121), but which parameters hold IDs is read from the data: the fields whose values are mostly
/// DS3 FXR IDs and typically 200 or more (small values such as slots and dummy polys also match low FXR IDs). A
/// type can carry several (blade trails, 120). A ported DS3 event gets its effects' new Elden Ring IDs and Elden
/// Ring's parameter length (extra bytes copied from Elden Ring's most common event of that type). Any other DS3 event
/// of these types is dropped, as its DS3 IDs would name unrelated Elden Ring effects.
/// </summary>
public sealed class EffectEvents
{
    // 112 spawns by floor material and 113 has no ID.
    private static readonly int[] Candidates = [.. Enumerable.Range(95, 27).Where(t => t is not 112 and not 113)];

    private readonly Dictionary<int, int[]> _field = [];
    private readonly Dictionary<int, byte[]> _erSample = [];

    public static EffectEvents Find(BND4 ds3Main, BND4 erMain, FxrPort.Ds3Effects ds3)
    {
        var fx = new EffectEvents();
        TAE.ValidateEventBank = false;
        var ds3Events = AllEvents(ds3Main).Where(e => Candidates.Contains(e.Type)).GroupBy(e => e.Type);
        foreach (var group in ds3Events)
        {
            var bytes = group.Select(e => e.GetParameterBytes(false)).ToList();
            var fields = new List<int>();
            var shares = new List<string>();
            for (int field = 0; field < 4; field++)
            {
                var values = bytes.Where(b => b.Length >= 4 * field + 4).Select(b => BitConverter.ToInt32(b, 4 * field)).Where(v => v > 0).Order().ToList();
                if (values.Count < 10 || values.Count < bytes.Count / 3)
                    continue;
                double share = values.Count(ds3.Has) / (double)values.Count;
                int median = values[values.Count / 2];
                shares.Add($"f{field}={share:P0}/{values.Count}/median {median}");
                if (share >= 0.75 && median >= 200)
                    fields.Add(field);
            }
            fx._candidates.Add(group.Key);
            if (fields.Count > 0)
                fx._field[group.Key] = [.. fields];
            Console.WriteLine($"effects: DS3 type {group.Key}: {bytes.Count} events, FXR IDs by field {string.Join(" ", shares)}");
        }
        foreach (var group in AllEvents(erMain).Where(e => fx._field.ContainsKey(e.Type)).GroupBy(e => e.Type))
            fx._erSample[group.Key] = group.Select(e => e.GetParameterBytes(false)).GroupBy(System.Convert.ToHexString)
                .MaxBy(g => g.Count())!.First();
        foreach (var type in fx._field.Keys.Where(t => !fx._erSample.ContainsKey(t)).ToList())
            fx._field.Remove(type); // Elden Ring doesn't use it: its layout is unknown
        Console.WriteLine($"effects: effect event types (type:fields) {string.Join(" ", fx._field.OrderBy(f => f.Key).Select(f => $"{f.Key}:{string.Join(",", f.Value)}"))}");
        return fx;
    }

    private readonly HashSet<int> _candidates = [];

    /// <summary>Whether DS3 events of this type spawn effects (converted, or dropped when they can't be).</summary>
    public bool IsEffect(int type) => _candidates.Contains(type);

    /// <summary>The DS3 effect IDs these events spawn.</summary>
    public IEnumerable<int> Ids(IEnumerable<TAE.Event> events) =>
        events.Where(e => _field.ContainsKey(e.Type))
            .SelectMany(e => _field[e.Type].Select(f => Id(e.GetParameterBytes(false), f))).Where(id => id > 0);

    /// <summary>The event's parameters for Elden Ring, or null when it can't be ported.</summary>
    public byte[]? Convert(TAE.Event e, FxrPort.Result effects)
    {
        if (!_field.TryGetValue(e.Type, out var fields))
            return null;
        var bytes = (byte[])e.GetParameterBytes(false).Clone();
        foreach (var field in fields)
        {
            int id = Id(bytes, field);
            if (id <= 0)
                continue;
            if (effects.Map(id) is not { } newId)
                return null;
            BitConverter.GetBytes(newId).CopyTo(bytes, 4 * field);
        }
        var sample = _erSample[e.Type];
        if (sample.Length > bytes.Length)
            return [.. bytes, .. sample.AsSpan(bytes.Length)];
        if (sample.Length < bytes.Length)
            return bytes.AsSpan(sample.Length).IndexOfAnyExcept((byte)0) < 0 ? bytes[..sample.Length] : null;
        return bytes;
    }

    private static int Id(byte[] bytes, int field) => bytes.Length >= 4 * field + 4 ? BitConverter.ToInt32(bytes, 4 * field) : -1;

    private static IEnumerable<TAE.Event> AllEvents(BND4 main)
    {
        foreach (var file in main.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
        {
            TAE tae;
            try { tae = TAE.Read(file.Bytes); }
            catch (Exception) { continue; }
            foreach (var e in tae.Animations.SelectMany(a => a.Events))
                yield return e;
        }
    }
}
