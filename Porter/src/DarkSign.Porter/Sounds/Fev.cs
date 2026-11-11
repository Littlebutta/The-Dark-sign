using System.Text;

namespace DarkSign.Porter;

/// <summary>
/// Reads the events of an FMOD Designer event file (.fev) as Dark Souls III ships them (/sound/*.fev, format version
/// 0x00450000): which waves each event plays, layer by layer. A DS3 sound number only names the event; the waves it
/// plays can be named anything (c000005500 plays large-weapon-hit.wav) and an event can play several layers at once
/// (c000004320 plays c000004320.wav and c000004310.wav together).
/// <para>The layout was worked out from DS3's own files. The event header's field names follow xoreos'
/// src/sound/fmodeventfile.cpp (GPL-3, an older FEV version; read for reference, no code taken).</para>
/// <para>FACTS (measured on all 235 DS3 .fev files):</para>
/// <list type="bullet">
/// <item>The file is RIFF "FEV " with an "FMT " version, then a LIST of chunks. The ones used here are STRR (the
/// string table: u32 count, count u32 offsets, then the NUL-terminated strings the offsets count from) and LGCY (all
/// the event data). Event and sound-definition names are u32 indices into STRR.</item>
/// <item>Sound definitions sit in LGCY as u32 count, then per definition: u32 name, u32 property set, u32 wave count
/// and the waves: u32 type, u32 weight, then for type 0 (a wave file) a string (u32 length with its NUL, bytes), u32
/// bank, u32 subsound index, u32 length in ms; type 1 (oscillator) 8 bytes; types 2 and 3 nothing. In every file the
/// list reads back to back and its count is the file's own object count for sound definitions (OBCT entry 0x11);
/// each wave's length in ms is the duration of the FSB sample named like it.</item>
/// <item>An event record is u32 type (8 in the records looked at by hand; 16 is accepted too), u32 name, a 16-byte
/// GUID, 144 bytes of header (volume, pitch, priority, 3D settings...), u32 layer count, the layers, u32 parameter
/// count and parameters, u32 user-property count and properties, u32 category count and category names. Read this
/// way no record is skipped, and in 234 of the 235 files the number of event names is the file's own event count
/// (OBCT entry 0x0B); fdp_c6270.fev gives 87 names for a count of 94 (not looked into).</item>
/// <item>A layer is five u16 (the first is 2, or 0 in the layers without sounds that were looked at; the fourth
/// and fifth are the sound and envelope counts), then its sounds, 58 bytes each starting with the u16 index of a
/// sound definition in the list above, then its envelopes. No DS3 layer has more than one sound.</item>
/// </list>
/// <para>INFERENCE (the sizes are confirmed by every record reading to its end; the meaning is not):</para>
/// <list type="bullet">
/// <item>An envelope is i32 -1 (a parent), a string (a DSP's name, length 0 for none), four u32 of which the last
/// counts the u32 point indices that follow, then two more u32. A parameter is eight 4-byte fields, the last
/// counting f32 values that follow. A user property is a string, u32 type and a value (0 int, 1 float, 2 string).</item>
/// <item>All layers of an event start together when it plays (FMOD Designer's layers; the start time of a sound in
/// its layer is not read).</item>
/// <item>Several waves in one sound definition are random variants. Their weights and the definition's play mode
/// are not read, nor are the event's volume, pitch and envelopes.</item>
/// </list>
/// A record that doesn't read this way is skipped and counted; the reader never throws on a file's content.
/// </summary>
public sealed class Fev
{
    /// <summary>One layer: the wave files of its sound definition, as the event file names them.</summary>
    public sealed record Layer(IReadOnlyList<string> Waves);

    public sealed record Event(string Name, IReadOnlyList<Layer> Layers);

    private readonly Dictionary<string, Event> _events = [];

    /// <summary>The events that read, by name (the first of a name when a file repeats one).</summary>
    public IReadOnlyDictionary<string, Event> Events => _events;

    /// <summary>Records that start like an event but didn't read to the end of their layers (left out).</summary>
    public int Skipped { get; private set; }

    /// <summary>How many sound definitions the file's list holds (0 when the list wasn't found).</summary>
    public int Definitions { get; private set; }

    /// <summary>
    /// The FSB sample name of a wave: its file name without folders and extension ("bank/fdp_main/sfx/s000525503.wav"
    /// is sample "s000525503"). Checked on DS3's weapon sounds: every wave has an FSB sample of that name, and the
    /// sample's duration is the wave's length in the event file.
    /// </summary>
    public static string SampleName(string waveFile)
    {
        var leaf = waveFile[(waveFile.LastIndexOfAny(['/', '\\']) + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[..dot] : leaf;
    }

    public static bool Is(byte[] bytes) =>
        bytes.Length >= 0x2C && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("FEV "u8);

    /// <summary>Reads what it can; a file that isn't a FEV, or whose parts aren't where expected, gives no events.</summary>
    public static Fev Read(byte[] bytes)
    {
        var fev = new Fev();
        try { fev.Load(bytes); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException) { }
        return fev;
    }

    private const int HeaderSize = 0xA8; // type, name, GUID and the fixed header, up to the layer count
    private const int SoundSize = 58;

    private void Load(byte[] d)
    {
        if (!Is(d))
            return;
        // "RIFF" size "FEV ", then "FMT " and a LIST whose chunks follow its form tag.
        (int At, int Size) strr = default, lgcy = default;
        for (int at = 12; at + 8 <= d.Length;)
        {
            var tag = Encoding.ASCII.GetString(d, at, 4);
            int size = BitConverter.ToInt32(d, at + 4);
            if (size < 0 || at + 8 + (long)size > d.Length)
                break;
            if (tag == "LIST") { at += 12; continue; }
            if (tag == "STRR") strr = (at + 8, size);
            if (tag == "LGCY") lgcy = (at + 8, size);
            at += 8 + size + (size & 1);
        }
        if (strr.Size < 4 || lgcy.Size < HeaderSize)
            return;

        int count = BitConverter.ToInt32(d, strr.At);
        if (count < 0 || 4 + 4L * count > strr.Size)
            return;
        int textAt = strr.At + 4 + 4 * count, strrEnd = strr.At + strr.Size;
        var strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            int start = textAt + BitConverter.ToInt32(d, strr.At + 4 + 4 * i), end = start;
            if (start < textAt || start >= strrEnd) { strings[i] = ""; continue; }
            while (end < strrEnd && d[end] != 0) end++;
            strings[i] = Encoding.ASCII.GetString(d, start, end - start);
        }

        int lgcyEnd = lgcy.At + lgcy.Size;
        var definitions = ReadDefinitions(d, lgcy.At, lgcyEnd, strings);
        Definitions = definitions.Count;

        // Event records follow each other; whatever sits between them (the group headers before the first) or a
        // record that doesn't read is stepped over a byte at a time until the next record that does.
        for (int p = lgcy.At; p + HeaderSize + 4 <= lgcyEnd;)
        {
            if (!LooksLikeEvent(d, p, strings))
            {
                p++;
                continue;
            }
            var name = strings[BitConverter.ToInt32(d, p + 4)];
            int end = -1;
            List<Layer>? layers = null;
            try { layers = ReadLayers(d, p + HeaderSize, lgcyEnd, definitions, out end); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException) { }
            if (layers is null)
            {
                Skipped++;
                p++;
                continue;
            }
            _events.TryAdd(name, new Event(name, layers));
            int tail = -1;
            try { tail = SkipTail(d, end, lgcyEnd); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException) { }
            p = tail > 0 ? tail : end;
        }
    }

    // u32 type 8 or 16 (FMOD's complex and simple events), a name that is a string of the table
    // (sound definitions' names start with '/', events' don't), and header values in their ranges: volume 0..8,
    // priority 0..256, at most 32 layers.
    private static bool LooksLikeEvent(byte[] d, int p, string[] strings)
    {
        if (BitConverter.ToInt32(d, p) is not (8 or 16))
            return false;
        int name = BitConverter.ToInt32(d, p + 4);
        if (name <= 0 || name >= strings.Length || strings[name].Length < 2 || strings[name][0] == '/')
            return false;
        float volume = BitConverter.ToSingle(d, p + 24);
        return volume >= 0 && volume <= 8 && BitConverter.ToInt32(d, p + 40) is >= 0 and <= 256
               && BitConverter.ToInt32(d, p + HeaderSize) is >= 0 and <= 32;
    }

    // The layers at p (their u32 count first); null when they don't read. end: the first byte after them.
    private static List<Layer>? ReadLayers(byte[] d, int p, int limit, List<List<string>> definitions, out int end)
    {
        end = -1;
        int count = BitConverter.ToInt32(d, p);
        p += 4;
        var layers = new List<Layer>();
        for (int l = 0; l < count; l++)
        {
            if (p + 10 > limit || BitConverter.ToUInt16(d, p) is not (0 or 2))
                return null;
            int sounds = BitConverter.ToUInt16(d, p + 6), envelopes = BitConverter.ToUInt16(d, p + 8);
            p += 10;
            if (sounds > 256 || envelopes > 256 || p + (long)sounds * SoundSize > limit)
                return null;
            var waves = new List<string>();
            for (int s = 0; s < sounds; s++, p += SoundSize)
            {
                int definition = BitConverter.ToUInt16(d, p);
                if (definition >= definitions.Count)
                    return null;
                waves.AddRange(definitions[definition]);
            }
            for (int e = 0; e < envelopes; e++)
            {
                if (p + 8 > limit || BitConverter.ToInt32(d, p) is < -1 or > 256)
                    return null;
                p = SkipString(d, p + 4, limit);
                if (p < 0 || p + 16 > limit)
                    return null;
                uint points = BitConverter.ToUInt32(d, p + 12);
                if (points > 4096)
                    return null;
                p += 16 + 4 * (int)points + 8;
                if (p > limit)
                    return null;
            }
            layers.Add(new Layer(waves));
        }
        end = p;
        return layers;
    }

    // What follows an event's layers: parameters, user properties, categories. Returns the record's end, or -1.
    private static int SkipTail(byte[] d, int p, int limit)
    {
        if (p + 4 > limit)
            return -1;
        uint parameters = BitConverter.ToUInt32(d, p);
        p += 4;
        if (parameters > 64)
            return -1;
        for (int i = 0; i < parameters; i++)
        {
            if (p + 32 > limit)
                return -1;
            uint values = BitConverter.ToUInt32(d, p + 28);
            if (values > 256)
                return -1;
            p += 32 + 4 * (int)values;
        }
        if (p + 4 > limit)
            return -1;
        uint properties = BitConverter.ToUInt32(d, p);
        p += 4;
        if (properties > 256)
            return -1;
        for (int i = 0; i < properties; i++)
        {
            p = SkipString(d, p, limit);
            if (p < 0 || p + 8 > limit)
                return -1;
            uint type = BitConverter.ToUInt32(d, p);
            p += 4;
            if (type > 2)
                return -1;
            p = type == 2 ? SkipString(d, p, limit) : p + 4;
            if (p < 0)
                return -1;
        }
        if (p + 4 > limit)
            return -1;
        uint categories = BitConverter.ToUInt32(d, p);
        p += 4;
        if (categories > 16)
            return -1;
        for (int i = 0; i < categories; i++)
        {
            p = SkipString(d, p, limit);
            if (p < 0)
                return -1;
        }
        return p;
    }

    // Steps over a string (u32 length counting its NUL, then the bytes; length 0 is no string). -1 when it isn't one.
    private static int SkipString(byte[] d, int p, int limit)
    {
        if (p + 4 > limit)
            return -1;
        int length = BitConverter.ToInt32(d, p);
        if (length < 0 || length > 512 || p + 4 + length > limit || (length > 0 && d[p + 4 + length - 1] != 0))
            return -1;
        return p + 4 + length;
    }

    // The sound-definition list: the place in LGCY where a u32 count is followed by exactly that many definitions
    // whose names are strings starting with '/'. Each definition's wave files, in list order.
    private static List<List<string>> ReadDefinitions(byte[] d, int from, int limit, string[] strings)
    {
        bool Name(int p) => BitConverter.ToInt32(d, p) is var n && n >= 0 && n < strings.Length && strings[n].StartsWith('/');
        List<List<string>> best = [];
        for (int p = from + 4; p + 12 <= limit; p++)
        {
            int count = BitConverter.ToInt32(d, p - 4);
            if (count < 1 || count > 1_000_000 || !Name(p))
                continue;
            var list = new List<List<string>>(Math.Min(count, 65536));
            int q = p;
            try
            {
                for (int i = 0; i < count && q >= 0; i++)
                {
                    if (q + 12 > limit || !Name(q)) { q = -1; break; }
                    uint waves = BitConverter.ToUInt32(d, q + 8);
                    q += 12;
                    if (waves > 4096) { q = -1; break; }
                    var files = new List<string>();
                    for (int w = 0; w < waves && q >= 0; w++)
                    {
                        if (q + 8 > limit) { q = -1; break; }
                        uint type = BitConverter.ToUInt32(d, q);
                        q += 8; // type, weight
                        switch (type)
                        {
                            case 0:
                                int after = SkipString(d, q, limit);
                                if (after < 0 || after + 12 > limit || after == q + 4) { q = -1; break; }
                                files.Add(Encoding.ASCII.GetString(d, q + 4, after - q - 5));
                                q = after + 12; // bank, subsound index, length in ms
                                break;
                            case 1: q += 8; break; // oscillator: type, frequency
                            case 2 or 3: break; // "don't play" and programmer sounds: no file
                            default: q = -1; break;
                        }
                    }
                    list.Add(files);
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { q = -1; }
            if (q < 0 || list.Count != count)
                continue;
            // The longest such list is the real one (a chance match of a short count can't outgrow it).
            if (count > best.Count)
                best = list;
            p = q - 1;
        }
        return best;
    }
}
