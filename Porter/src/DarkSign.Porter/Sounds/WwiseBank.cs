using System.Text;

namespace DarkSign.Porter;

/// <summary>
/// A Wwise sound bank (.bnk) edited at the level the sound port needs: sections are kept as they are, except the
/// hierarchy (HIRC) whose objects are kept as raw blobs, and the embedded media index and data (DIDX/DATA). New sounds
/// are added by cloning an existing event's chain (Event -> Play action -> Sound) with new IDs and new embedded media,
/// so they inherit the original's bus, volume and 3D settings.
/// </summary>
public sealed class WwiseBank
{
    public const byte SoundType = 2, ActionType = 3, EventType = 4;

    public sealed class HircObject(byte type, uint id, byte[] data)
    {
        public byte Type { get; } = type;
        public uint Id { get; } = id;
        /// <summary>The object's bytes after its type and size, starting with its ID.</summary>
        public byte[] Data { get; set; } = data;
    }

    private readonly List<(string Tag, byte[] Body)> _sections = [];
    private readonly List<HircObject> _objects = [];
    private readonly SortedDictionary<uint, byte[]> _media = [];
    private readonly Dictionary<uint, HircObject> _byId = [];

    public int Version { get; private set; }

    public static WwiseBank Read(byte[] bytes)
    {
        var bank = new WwiseBank();
        byte[]? didx = null, data = null;
        for (int at = 0; at + 8 <= bytes.Length;)
        {
            var tag = Encoding.ASCII.GetString(bytes, at, 4);
            int size = BitConverter.ToInt32(bytes, at + 4);
            var body = bytes[(at + 8)..(at + 8 + size)];
            at += 8 + size;
            switch (tag)
            {
                case "BKHD":
                    bank.Version = BitConverter.ToInt32(body, 0);
                    break;
                case "DIDX": didx = body; break;
                case "DATA": data = body; break;
                case "HIRC":
                    int count = BitConverter.ToInt32(body, 0), p = 4;
                    for (int i = 0; i < count; i++)
                    {
                        byte type = body[p];
                        int objSize = BitConverter.ToInt32(body, p + 1);
                        var obj = new HircObject(type, BitConverter.ToUInt32(body, p + 5), body[(p + 5)..(p + 5 + objSize)]);
                        bank._objects.Add(obj);
                        bank._byId.TryAdd(obj.Id, obj);
                        p += 5 + objSize;
                    }
                    break;
            }
            bank._sections.Add((tag, body));
        }
        if (didx is not null && data is not null)
            for (int i = 0; i + 12 <= didx.Length; i += 12)
            {
                uint id = BitConverter.ToUInt32(didx, i);
                int offset = BitConverter.ToInt32(didx, i + 4), size = BitConverter.ToInt32(didx, i + 8);
                bank._media[id] = data[offset..(offset + size)];
            }
        return bank;
    }

    public bool Has(uint id) => _byId.ContainsKey(id) || _media.ContainsKey(id);

    public HircObject? Get(uint id) => _byId.GetValueOrDefault(id);

    /// <summary>The Sound an event plays: through its first Play action, directly or as the first Sound whose
    /// parent is the container the action plays.</summary>
    public (HircObject Event, HircObject Action, HircObject Sound)? Chain(uint eventId)
    {
        if (Get(eventId) is not { Type: EventType } ev)
            return null;
        foreach (var actionId in EventActions(ev))
        {
            if (Get(actionId) is not { Type: ActionType } action || BitConverter.ToUInt16(action.Data, 4) != 0x0403)
                continue;
            uint target = BitConverter.ToUInt32(action.Data, 6);
            if (Get(target) is { Type: SoundType } sound)
                return (ev, action, sound);
            var child = _objects.FirstOrDefault(o => o.Type == SoundType && ParentIs(o, target));
            if (child is not null)
                return (ev, action, child);
        }
        return null;
    }

    /// <summary>Adds an event that plays new embedded PCM media, cloned from a template chain.</summary>
    public void AddSound((HircObject Event, HircObject Action, HircObject Sound) template, uint eventId, uint soundId, uint actionId,
        uint mediaId, byte[] wem)
    {
        // Sound: ID, then AkBankSourceData: plugin (PCM), stream type (embedded in the bank), media ID and size.
        var sound = (byte[])template.Sound.Data.Clone();
        BitConverter.GetBytes(soundId).CopyTo(sound, 0);
        BitConverter.GetBytes(0x00010001u).CopyTo(sound, 4);
        sound[8] = 0;
        BitConverter.GetBytes(mediaId).CopyTo(sound, 9);
        BitConverter.GetBytes(wem.Length).CopyTo(sound, 13);
        sound[17] &= unchecked((byte)~0x02); // not prefetched
        var action = (byte[])template.Action.Data.Clone();
        BitConverter.GetBytes(actionId).CopyTo(action, 0);
        BitConverter.GetBytes(soundId).CopyTo(action, 6);
        // Event: ID and an action list of one, in the template's count format (u8 in newer banks, u32 before).
        bool byteCount = template.Event.Data.Length == 5 + 4 * template.Event.Data[4];
        var ev = byteCount ? new byte[9] : new byte[12];
        BitConverter.GetBytes(eventId).CopyTo(ev, 0);
        if (byteCount) { ev[4] = 1; BitConverter.GetBytes(actionId).CopyTo(ev, 5); }
        else { BitConverter.GetBytes(1).CopyTo(ev, 4); BitConverter.GetBytes(actionId).CopyTo(ev, 8); }

        foreach (var obj in new[] { new HircObject(SoundType, soundId, sound), new HircObject(ActionType, actionId, action), new HircObject(EventType, eventId, ev) })
        {
            _objects.Add(obj);
            _byId[obj.Id] = obj;
        }
        _media[mediaId] = wem;
    }

    public byte[] Write()
    {
        var output = new MemoryStream();
        bool wroteMedia = false;
        void Section(string tag, byte[] body)
        {
            output.Write(Encoding.ASCII.GetBytes(tag));
            output.Write(BitConverter.GetBytes(body.Length));
            output.Write(body);
        }
        void Media()
        {
            if (wroteMedia || _media.Count == 0)
                return;
            wroteMedia = true;
            // Media sorted by ID, each aligned to 16 bytes in DATA.
            var index = new MemoryStream();
            var data = new MemoryStream();
            foreach (var (id, bytes) in _media)
            {
                while (data.Length % 16 != 0)
                    data.WriteByte(0);
                index.Write(BitConverter.GetBytes(id));
                index.Write(BitConverter.GetBytes((int)data.Length));
                index.Write(BitConverter.GetBytes(bytes.Length));
                data.Write(bytes);
            }
            Section("DIDX", index.ToArray());
            Section("DATA", data.ToArray());
        }
        foreach (var (tag, body) in _sections)
        {
            switch (tag)
            {
                case "DIDX":
                case "DATA":
                    Media();
                    break;
                case "HIRC":
                    Media(); // media comes before the hierarchy
                    var hirc = new MemoryStream();
                    hirc.Write(BitConverter.GetBytes(_objects.Count));
                    foreach (var obj in _objects)
                    {
                        hirc.WriteByte(obj.Type);
                        hirc.Write(BitConverter.GetBytes(obj.Data.Length));
                        hirc.Write(obj.Data);
                    }
                    Section("HIRC", hirc.ToArray());
                    break;
                default:
                    Section(tag, body);
                    break;
            }
        }
        return output.ToArray();
    }

    private static IEnumerable<uint> EventActions(HircObject ev)
    {
        var d = ev.Data;
        if (d.Length >= 5 && d.Length == 5 + 4 * d[4])
            return Enumerable.Range(0, d[4]).Select(i => BitConverter.ToUInt32(d, 5 + 4 * i));
        int count = d.Length >= 8 ? BitConverter.ToInt32(d, 4) : 0;
        return d.Length == 8 + 4 * count ? Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(d, 8 + 4 * i)) : [];
    }

    // A Sound's parent ID sits in its node parameters, a few bytes after the 18-byte source data.
    private static bool ParentIs(HircObject sound, uint parent)
    {
        var d = sound.Data;
        for (int p = 18; p + 4 <= Math.Min(d.Length, 64); p++)
            if (BitConverter.ToUInt32(d, p) == parent)
                return true;
        return false;
    }

    /// <summary>A 16-bit PCM .wem (RIFF WAVE, WAVEFORMATEXTENSIBLE), which Wwise plays with its PCM codec.</summary>
    public static byte[] PcmWem(short[] pcm, int channels, int rate)
    {
        var data = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, data, 0, data.Length);
        var w = new MemoryStream();
        void U16(int v) => w.Write(BitConverter.GetBytes((ushort)v));
        void U32(int v) => w.Write(BitConverter.GetBytes(v));
        w.Write("RIFF"u8);
        U32(4 + 8 + 24 + 8 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        U32(24);
        U16(0xFFFE);
        U16(channels);
        U32(rate);
        U32(rate * channels * 2);
        U16(channels * 2);
        U16(16);
        U16(6);
        U16(16);
        U32(channels == 1 ? 0x4 : 0x3);
        w.Write("data"u8);
        U32(data.Length);
        w.Write(data);
        return w.ToArray();
    }
}
