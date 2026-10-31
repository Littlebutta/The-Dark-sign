using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// The TAE events that spawn effects, and where their FXR ID is. Both games number these types the same (the
/// SpawnFFX family, 95-121), but which parameter holds the ID is read from the data: the field whose values are
/// nearly all DS3 FXR IDs. A ported DS3 event gets its effect's new Elden Ring ID and Elden Ring's parameter length
/// (extra bytes copied from Elden Ring's most common event of that type); an event whose effect wasn't ported is
/// dropped, as its DS3 ID would name an unrelated Elden Ring effect.
/// </summary>
public sealed class EffectEvents
{
    // 112 spawns by floor material and 113 has no ID.
    private static readonly int[] Candidates = [.. Enumerable.Range(95, 27).Where(t => t is not 112 and not 113)];

    private readonly Dictionary<int, int> _field = [];
    private readonly Dictionary<int, byte[]> _erSample = [];

    public IReadOnlyDictionary<int, int> Fields => _field;

    public static EffectEvents Find(BND4 ds3Main, BND4 erMain, FxrPort.Ds3Effects ds3)
    {
        var fx = new EffectEvents();
        TAE.ValidateEventBank = false;
        var ds3Events = AllEvents(ds3Main).Where(e => Candidates.Contains(e.Type)).GroupBy(e => e.Type);
        foreach (var group in ds3Events)
        {
            var bytes = group.Select(e => e.GetParameterBytes(false)).ToList();
            int best = -1;
            double bestShare = 0;
            for (int field = 0; field < 4; field++)
            {
                var values = bytes.Where(b => b.Length >= 4 * field + 4).Select(b => BitConverter.ToInt32(b, 4 * field)).Where(v => v >= 1000).ToList();
                if (values.Count < 10 || values.Count < bytes.Count / 2)
                    continue;
                double share = values.Count(ds3.Has) / (double)values.Count;
                if (share > bestShare)
                    (best, bestShare) = (field, share);
            }
            if (best >= 0 && bestShare >= 0.9)
                fx._field[group.Key] = best;
        }
        foreach (var group in AllEvents(erMain).Where(e => fx._field.ContainsKey(e.Type)).GroupBy(e => e.Type))
            fx._erSample[group.Key] = group.Select(e => e.GetParameterBytes(false)).GroupBy(System.Convert.ToHexString)
                .MaxBy(g => g.Count())!.First();
        foreach (var type in fx._field.Keys.Where(t => !fx._erSample.ContainsKey(t)).ToList())
            fx._field.Remove(type); // Elden Ring doesn't use it: its layout is unknown
        Console.WriteLine($"effects: effect event types (type:field) {string.Join(" ", fx._field.OrderBy(f => f.Key).Select(f => $"{f.Key}:{f.Value}"))}");
        return fx;
    }

    public bool IsEffect(int type) => _field.ContainsKey(type);

    /// <summary>The DS3 effect IDs these events spawn.</summary>
    public IEnumerable<int> Ids(IEnumerable<TAE.Event> events) =>
        events.Where(e => _field.ContainsKey(e.Type)).Select(e => Id(e.GetParameterBytes(false), _field[e.Type])).Where(id => id > 0);

    /// <summary>The event's parameters for Elden Ring, or null when its effect wasn't ported.</summary>
    public byte[]? Convert(TAE.Event e, FxrPort.Result effects)
    {
        var bytes = (byte[])e.GetParameterBytes(false).Clone();
        int field = _field[e.Type], id = Id(bytes, field);
        if (id > 0)
        {
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
