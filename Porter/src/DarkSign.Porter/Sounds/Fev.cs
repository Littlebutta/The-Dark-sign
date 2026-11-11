using System.Text;

namespace DarkSign.Porter;

/// <summary>
/// Reads the events of an FMOD Designer event file (.fev) as Dark Souls III ships them (/sound/*.fev, format version
/// 0x00450000): which waves each event plays, layer by layer. A DS3 sound number only names the event; the waves it
/// plays can be named anything (c000005500 plays large-weapon-hit.wav) and an event can play several layers at once
/// (c000004320 plays c000004320.wav and c000004310.wav together). It also reads how loud and at what pitch DS3 plays
/// them, and whether a layer loops.
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
/// <item>Event header fields (offsets from the record's start; distributions over all 18,648 events): +24 f32
/// volume, linear (1.0 in 68%, the rest sit on dB steps: 0.7079 = -3 dB, 0.5012 = -6 dB); +28 f32 pitch and +32 f32
/// pitch randomisation (0 in over 90%, the rest multiples of 1/48); +36 f32 volume randomisation, stored as 1 minus
/// the lowest linear volume (0.2921 = down to 0.7079 = -3 dB); +60 f32 maximum distance (equal to the event's
/// "playDistance" user property in 17,725 events and to its "(distance)" parameter's maximum in 18,610); +72 u32
/// flags, bit 0x80000 set on one-shot events (it disagrees with the loop mode of the event's sounds in 5 events);
/// +144 u32 fade-out time in ms (0 in 11,737, 100 in 5,994, then 1500, 1000, 2000, 500).</item>
/// <item>A sound in a layer (58 bytes): after the u16 definition index come i32 fields; the fourth (+14) is the
/// loop mode: 1 in 18,862 sounds, 0 in 696, 2 in 45.</item>
/// <item>Right before the sound-definition list sits a u32 count and that many 74-byte property records (the count
/// is the highest property-set index of the definitions plus one in every file). In a record: +16 f32 volume, linear
/// (1.0 in 76%, the rest dB steps); +32 f32 the lowest linear volume of its volume randomisation (1.0 = none);
/// +36 f32 pitch; +52 f32 pitch randomisation.</item>
/// </list>
/// <para>INFERENCE (the sizes are confirmed by every record reading to its end; the meaning is not):</para>
/// <list type="bullet">
/// <item>An envelope is i32 -1 (a parent), a string (a DSP's name, length 0 for none), four u32 of which the last
/// counts the u32 point indices that follow, then two more u32. A parameter is eight 4-byte fields, the last
/// counting f32 values that follow. A user property is a string, u32 type and a value (0 int, 1 float, 2 string).</item>
/// <item>All layers of an event start together when it plays (FMOD Designer's layers; the start time of a sound in
/// its layer is not read).</item>
/// <item>Several waves in one sound definition are random variants. Their weights and the definition's play mode
/// are not read, nor are the envelopes (volume by distance, DSP effects).</item>
/// <item>Loop modes: 1 plays once, 0 loops until the event is stopped, 2 loops and plays its end when stopped
/// (FMOD Designer's "one-shot", "loop and cutoff", "loop and play to end"; the names are a guess, that modes 0
/// and 2 loop is supported by their samples carrying FSB loop points).</item>
/// <item>The pitch unit: 1.0 = 4 octaves = 4,800 cents. Measured: the non-zero pitches are multiples of 1/48
/// (the ten commonest are 1/48 to 6/48 either way), i.e. whole semitones, a unit FMOD Designer offers next to
/// octaves and tones. That the raw value spans 4 octaves follows from that step; it was not checked against
/// FMOD's source or by ear.</item>
/// <item>Randomisation: FMOD lowers the volume by a random amount down to the stored minimum, and moves the pitch
/// by a random amount up to the stored value either way (the Dragon Age toolset wiki's FMOD page describes a sound
/// definition's pitch randomisation as a plus-or-minus deviation, https://datoolset.net/wiki/FMOD). An event's
/// and its definition's values add up (dB and pitch).</item>
/// </list>
/// A record that doesn't read this way is skipped and counted; the reader never throws on a file's content.
/// </summary>
public sealed class Fev
{
    /// <summary>One layer: the wave files of its sound definition, as the event file names them, the definition's
    /// level and pitch, and how the layer's sound loops.</summary>
    public sealed record Layer(IReadOnlyList<string> Waves)
    {
        /// <summary>1 plays once, 0 loops until stopped, 2 loops and plays to its end when stopped.</summary>
        public int LoopMode { get; init; } = 1;
        public bool Loops => LoopMode is 0 or 2;
        /// <summary>The sound definition's volume, linear (1 = unchanged).</summary>
        public float Volume { get; init; } = 1;
        /// <summary>The lowest linear volume the definition's volume randomisation reaches (1 = none).</summary>
        public float VolumeRandomMin { get; init; } = 1;
        /// <summary>The definition's pitch and pitch randomisation, in FMOD's raw unit (see <see cref="Cents"/>).</summary>
        public float Pitch { get; init; }
        public float PitchRandom { get; init; }
    }

    public sealed record Event(string Name, IReadOnlyList<Layer> Layers)
    {
        /// <summary>The event's volume, linear (1 = unchanged).</summary>
        public float Volume { get; init; } = 1;
        /// <summary>The lowest linear volume the event's volume randomisation reaches (1 = none).</summary>
        public float VolumeRandomMin { get; init; } = 1;
        /// <summary>The event's pitch and pitch randomisation, in FMOD's raw unit (see <see cref="Cents"/>).</summary>
        public float Pitch { get; init; }
        public float PitchRandom { get; init; }
        /// <summary>The event's one-shot flag: clear on events that play until they are stopped.</summary>
        public bool OneShot { get; init; } = true;
        /// <summary>How long the event fades out when it is stopped, in ms.</summary>
        public int FadeOutMs { get; init; }
        /// <summary>The distance at which the event is no longer heard (0 when the header holds none).</summary>
        public float MaxDistance { get; init; }
    }

    /// <summary>A linear volume in dB, no lower than -96 (silence).</summary>
    public static float Decibels(float linear) =>
        linear > 0 && float.IsFinite(linear) ? Math.Max(-96f, 20f * MathF.Log10(linear)) : -96f;

    /// <summary>A raw FMOD pitch in cents: 1.0 is 4 octaves (see the class remarks: measured as semitone steps of 1/48).</summary>
    public static float Cents(float raw) => float.IsFinite(raw) ? raw * 4800f : 0f;

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
    private const int PropertySetSize = 74;
    private const uint OneShotFlag = 0x80000;

    // A sound definition: its wave files and the index of its property record.
    private sealed record Definition(List<string> Waves, int PropertySet);

    // A definition property record's level and pitch.
    private readonly record struct PropertySet(float Volume, float VolumeRandomMin, float Pitch, float PitchRandom);

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
        var definitions = ReadDefinitions(d, lgcy.At, lgcyEnd, strings, out int listAt);
        Definitions = definitions.Count;
        var propertySets = ReadPropertySets(d, lgcy.At, listAt, definitions);

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
            try { layers = ReadLayers(d, p + HeaderSize, lgcyEnd, definitions, propertySets, out end); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException) { }
            if (layers is null)
            {
                Skipped++;
                p++;
                continue;
            }
            // The header's level, pitch and stop behaviour. A value outside what DS3's events hold reads as "unchanged".
            float Field(int at, float otherwise, float low, float high) =>
                BitConverter.ToSingle(d, p + at) is var v && float.IsFinite(v) && v >= low && v <= high ? v : otherwise;
            int fade = BitConverter.ToInt32(d, p + 144);
            _events.TryAdd(name, new Event(name, layers)
            {
                Volume = Field(24, 1, 0, 8), Pitch = Field(28, 0, -1, 1), PitchRandom = Math.Abs(Field(32, 0, -1, 1)),
                VolumeRandomMin = 1 - Field(36, 0, 0, 1), MaxDistance = Field(60, 0, 0, 100_000),
                OneShot = (BitConverter.ToUInt32(d, p + 72) & OneShotFlag) != 0,
                FadeOutMs = fade is >= 0 and <= 600_000 ? fade : 0,
            });
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
    private static List<Layer>? ReadLayers(byte[] d, int p, int limit, List<Definition> definitions, List<PropertySet> propertySets, out int end)
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
            // The layer's level, pitch and loop mode are its first sound's (no DS3 layer has more than one).
            int loopMode = 1;
            PropertySet set = new(1, 1, 0, 0);
            for (int s = 0; s < sounds; s++, p += SoundSize)
            {
                int definition = BitConverter.ToUInt16(d, p);
                if (definition >= definitions.Count)
                    return null;
                waves.AddRange(definitions[definition].Waves);
                if (s > 0)
                    continue;
                loopMode = BitConverter.ToInt32(d, p + 14) is var mode && mode is 0 or 2 ? mode : 1;
                if (definitions[definition].PropertySet is >= 0 and var index && index < propertySets.Count)
                    set = propertySets[index];
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
            layers.Add(new Layer(waves)
            {
                LoopMode = loopMode, Volume = set.Volume, VolumeRandomMin = set.VolumeRandomMin, Pitch = set.Pitch, PitchRandom = set.PitchRandom,
            });
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
    // whose names are strings starting with '/'. Each definition's wave files, in list order. listAt: where the
    // list's first definition starts (its count is the u32 before it), or -1.
    private static List<Definition> ReadDefinitions(byte[] d, int from, int limit, string[] strings, out int listAt)
    {
        listAt = -1;
        bool Name(int p) => BitConverter.ToInt32(d, p) is var n && n >= 0 && n < strings.Length && strings[n].StartsWith('/');
        List<Definition> best = [];
        for (int p = from + 4; p + 12 <= limit; p++)
        {
            int count = BitConverter.ToInt32(d, p - 4);
            if (count < 1 || count > 1_000_000 || !Name(p))
                continue;
            var list = new List<Definition>(Math.Min(count, 65536));
            int q = p;
            try
            {
                for (int i = 0; i < count && q >= 0; i++)
                {
                    if (q + 12 > limit || !Name(q)) { q = -1; break; }
                    int propertySet = BitConverter.ToInt32(d, q + 4);
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
                    list.Add(new Definition(files, propertySet));
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { q = -1; }
            if (q < 0 || list.Count != count)
                continue;
            // The longest such list is the real one (a chance match of a short count can't outgrow it).
            if (count > best.Count)
            {
                best = list;
                listAt = p;
            }
            p = q - 1;
        }
        return best;
    }

    // The definitions' property records: a u32 count and that many 74-byte records ending right before the
    // definition list's own count. The count is at least the highest index a definition names plus one (equal to it
    // in every DS3 file); when no such count sits there, no record is read and every definition keeps the defaults.
    private static List<PropertySet> ReadPropertySets(byte[] d, int from, int listAt, List<Definition> definitions)
    {
        var sets = new List<PropertySet>();
        if (listAt < 0 || definitions.Count == 0)
            return sets;
        int least = Math.Max(1, definitions.Max(x => x.PropertySet) + 1);
        for (int count = least; count < least + 64; count++)
        {
            long at = listAt - 8L - (long)count * PropertySetSize;
            if (at < from)
                break;
            if (BitConverter.ToInt32(d, (int)at) != count)
                continue;
            for (int i = 0; i < count; i++)
            {
                int r = (int)at + 4 + i * PropertySetSize;
                float Field(int o, float otherwise, float low, float high) =>
                    BitConverter.ToSingle(d, r + o) is var v && float.IsFinite(v) && v >= low && v <= high ? v : otherwise;
                sets.Add(new PropertySet(Field(16, 1, 0, 8), Field(32, 1, 0, 1), Field(36, 0, -1, 1), Math.Abs(Field(52, 0, -1, 1))));
            }
            break;
        }
        return sets;
    }
}
