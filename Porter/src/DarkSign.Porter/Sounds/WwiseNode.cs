namespace DarkSign.Porter;

/// <summary>
/// Where the fields the sound port edits sit in a Wwise (bank version 135, Elden Ring) sound-tree node: its parent,
/// its property bundle, and for containers their children list and a random/sequence container's playlist.
/// <see cref="HircObject.Data"/> starts with the object's ID, so every offset here counts it.
/// The layout follows Spellforge's reader (formats/wwise.ts, MIT-0, which reads every object of Elden Ring's 217
/// banks to its exact size) and wwiser (bnnm). A node only counts as read when the reader ends exactly at the end of
/// its data; anything else returns null and the caller keeps its older, byte-scanning way.
/// </summary>
public sealed class WwiseNode
{
    public const byte PropVolume = 0x00, PropPitch = 0x02, PropLowPass = 0x03, PropHighPass = 0x04;
    /// <summary>How often a Sound plays: an i32, 0 = until it is stopped. Elden Ring's looping sounds carry it on the
    /// Sound itself, always as 0 (4,421 Sounds; 2,595 of its looping Play_s/Play_c events, 142 loop their container
    /// instead).</summary>
    public const byte PropLoop = 0x3A;

    public required byte Type { get; init; }
    /// <summary>Offset of the u32 direct parent ID.</summary>
    public int ParentAt { get; init; }
    /// <summary>Offset of the property bundle (u8 count, IDs, values, then the ranged ones) and its end.</summary>
    public int PropsAt { get; init; }
    public int PropsEnd { get; init; }
    /// <summary>Offset of the u32 children count, or -1 for a Sound.</summary>
    public int ChildrenAt { get; init; } = -1;
    public List<uint> Children { get; init; } = [];
    /// <summary>A random/sequence container's playlist (u16 count, then child ID and weight): offset or -1.</summary>
    public int PlaylistAt { get; init; } = -1;
    public List<(uint Id, int Weight)> Playlist { get; init; } = [];
    /// <summary>A random/sequence container's u16 avoid-repeat count, or -1.</summary>
    public int AvoidRepeatAt { get; init; } = -1;
    public Dictionary<byte, uint> Props { get; init; } = [];
    /// <summary>The ranged part of the property bundle: what Wwise adds at random to a property each time the node
    /// plays, between Min and Max. Elden Ring's Volume ranges go down only (Min below 0, Max 0, in 3,774 of its
    /// 3,882 nodes with one); its Pitch ranges mostly go both ways (Min below 0, Max above, in 13,749 of 15,297).</summary>
    public Dictionary<byte, (float Min, float Max)> Ranges { get; init; } = [];
    public uint Parent(byte[] data) => BitConverter.ToUInt32(data, ParentAt);

    public static float AsFloat(uint bits) => BitConverter.Int32BitsToSingle((int)bits);
    public static uint Bits(float value) => (uint)BitConverter.SingleToInt32Bits(value);

    private sealed class Reader(byte[] d, int at)
    {
        public int At = at;
        public byte U8() => d[At++];
        public ushort U16() { var v = BitConverter.ToUInt16(d, At); At += 2; return v; }
        public uint U32() { var v = BitConverter.ToUInt32(d, At); At += 4; return v; }
        public void Skip(long n) { if (n < 0 || At + n > d.Length) throw new IndexOutOfRangeException(); At += (int)n; }
        public uint Var()
        {
            uint cur = U8(), v = cur & 0x7F;
            for (int n = 0; (cur & 0x80) != 0 && n < 5; n++)
            {
                cur = U8();
                v = (v << 7) | (cur & 0x7F);
            }
            return v;
        }
    }

    /// <summary>The node's layout, or null when it isn't a node type the port edits or doesn't read to its exact size.</summary>
    public static WwiseNode? Read(WwiseBank.HircObject o)
    {
        try { return ReadOrThrow(o); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { return null; }
    }

    private static WwiseNode? ReadOrThrow(WwiseBank.HircObject o)
    {
        var d = o.Data;
        var r = new Reader(d, 4);
        if (o.Type == WwiseBank.SoundType)
        {
            uint plugin = r.U32();
            r.Skip(1 + 4 + 4 + 1); // stream type, media ID, in-memory size, source bits
            if ((plugin & 0xF) == 2)
                r.Skip(r.U32()); // a source plugin's (generator's) parameters
        }
        else if (o.Type is not (WwiseBank.RanSeqCntrType or WwiseBank.SwitchCntrType or WwiseBank.ActorMixerType or WwiseBank.LayerCntrType))
            return null;

        // NodeBaseParams.
        r.U8(); // overrides the parent's effects
        int fx = r.U8();
        if (fx > 0)
        {
            r.U8(); // bypass bits
            r.Skip(fx * 7L); // slot, effect ID, shareset, rendered
        }
        r.U8(); // overrides attachment params
        r.U32(); // override bus
        int parentAt = r.At;
        r.U32();
        r.U8(); // priority bits
        int propsAt = r.At;
        var props = new Dictionary<byte, uint>();
        int n = r.U8();
        var ids = Enumerable.Range(0, n).Select(_ => r.U8()).ToList();
        foreach (var id in ids)
            props[id] = r.U32();
        int ranged = r.U8();
        var rangedIds = Enumerable.Range(0, ranged).Select(_ => r.U8()).ToList();
        var ranges = new Dictionary<byte, (float, float)>();
        foreach (var id in rangedIds)
            ranges[id] = (AsFloat(r.U32()), AsFloat(r.U32()));
        int propsEnd = r.At;
        int bits = r.U8(); // positioning
        if ((bits & 1) != 0 && (bits & 2) != 0)
        {
            r.U8(); // 3D bits
            if (((bits >> 5) & 3) != 0)
            {
                r.Skip(5); // path mode, transition time
                r.Skip(r.U32() * 16L); // vertices
                uint items = r.U32();
                r.Skip(items * 8L + items * 12L);
            }
        }
        if ((r.U8() & 8) != 0) // aux sends
            r.Skip(16);
        r.U32(); // reflections aux bus
        r.Skip(6); // advanced settings
        uint stateProps = r.Var();
        for (int i = 0; i < stateProps; i++) { r.Var(); r.Skip(2); }
        uint groups = r.Var();
        for (int i = 0; i < groups; i++)
        {
            r.Skip(5);
            r.Skip(r.Var() * 8L);
        }
        int rtpcs = r.U16();
        for (int i = 0; i < rtpcs; i++)
        {
            r.Skip(4 + 1 + 1);
            r.Var();
            r.Skip(4 + 1);
            r.Skip(r.U16() * 12L);
        }

        int childrenAt = -1, playlistAt = -1, avoidAt = -1;
        var children = new List<uint>();
        var playlist = new List<(uint, int)>();
        void ReadChildren()
        {
            childrenAt = r.At;
            uint count = r.U32();
            if (count > 100_000) throw new IndexOutOfRangeException();
            for (int i = 0; i < count; i++) children.Add(r.U32());
        }
        switch (o.Type)
        {
            case WwiseBank.RanSeqCntrType:
                r.Skip(6 + 12); // loop counts, transition times
                avoidAt = r.At;
                r.Skip(2 + 2 + 1 + 1); // avoid repeats, transition and random modes, mode, flags
                ReadChildren();
                playlistAt = r.At;
                int items = r.U16();
                for (int i = 0; i < items; i++) playlist.Add((r.U32(), (int)r.U32()));
                break;
            case WwiseBank.SwitchCntrType:
                r.Skip(1 + 4 + 4 + 1); // group type, group, default switch, continuous validation
                ReadChildren();
                uint packages = r.U32();
                for (int i = 0; i < packages; i++) { r.U32(); r.Skip(r.U32() * 4L); }
                r.Skip(r.U32() * 14L);
                break;
            case WwiseBank.ActorMixerType:
                ReadChildren();
                break;
            case WwiseBank.LayerCntrType:
                ReadChildren();
                uint layers = r.U32();
                for (int i = 0; i < layers; i++)
                {
                    r.U32();
                    int lr = r.U16();
                    for (int k = 0; k < lr; k++) { r.Skip(6); r.Var(); r.Skip(5); r.Skip(r.U16() * 12L); }
                    r.Skip(4 + 1);
                    uint assoc = r.U32();
                    for (int k = 0; k < assoc; k++) { r.U32(); r.Skip(r.U32() * 12L); }
                }
                r.U8();
                break;
        }
        if (r.At != d.Length)
            return null;
        return new WwiseNode
        {
            Type = o.Type, ParentAt = parentAt, PropsAt = propsAt, PropsEnd = propsEnd, ChildrenAt = childrenAt, Children = children,
            PlaylistAt = playlistAt, Playlist = playlist, AvoidRepeatAt = avoidAt, Props = props, Ranges = ranges,
        };
    }

    /// <summary>
    /// The data with these properties and random ranges set in the property bundle (added, or replacing the one of
    /// the same ID), written the way Elden Ring's banks hold them: a count, the IDs in ascending order, the values in
    /// the same order, then the ranged ones likewise with a Min and a Max each (ascending IDs in all 93,218 nodes of
    /// the game's 602 banks; a range needs no value of its own ID, 12,530 ranges have none). The other properties
    /// and ranges stay. More than 255 of either kind doesn't fit the bundle: the data is returned unchanged.
    /// </summary>
    public byte[] WithProps(byte[] data, IEnumerable<(byte Id, uint Bits)> props, IEnumerable<(byte Id, float Min, float Max)>? ranges = null)
    {
        var values = new SortedDictionary<byte, uint>(Props);
        foreach (var (id, bits) in props)
            values[id] = bits;
        var ranged = new SortedDictionary<byte, (float Min, float Max)>(Ranges);
        foreach (var (id, min, max) in ranges ?? [])
            ranged[id] = (min, max);
        if (values.Count > 255 || ranged.Count > 255)
            return (byte[])data.Clone();
        var bundle = new MemoryStream();
        bundle.WriteByte((byte)values.Count);
        foreach (var id in values.Keys) bundle.WriteByte(id);
        foreach (var bits in values.Values) bundle.Write(BitConverter.GetBytes(bits));
        bundle.WriteByte((byte)ranged.Count);
        foreach (var id in ranged.Keys) bundle.WriteByte(id);
        foreach (var (min, max) in ranged.Values)
        {
            bundle.Write(BitConverter.GetBytes(min));
            bundle.Write(BitConverter.GetBytes(max));
        }
        return [.. data.AsSpan(0, PropsAt), .. bundle.ToArray(), .. data.AsSpan(PropsEnd)];
    }

    /// <summary>The data with the given properties (and their random ranges) taken out of the property bundle.</summary>
    public byte[] WithoutProps(byte[] data, params byte[] drop)
    {
        var r = new Reader(data, PropsAt);
        int n = r.U8();
        var ids = Enumerable.Range(0, n).Select(_ => r.U8()).ToList();
        var values = ids.Select(_ => r.U32()).ToList();
        int ranged = r.U8();
        var rids = Enumerable.Range(0, ranged).Select(_ => r.U8()).ToList();
        var ranges = new List<byte[]>();
        foreach (var _ in rids) { ranges.Add(data[r.At..(r.At + 8)]); r.At += 8; }
        if (!ids.Any(drop.Contains) && !rids.Any(drop.Contains))
            return (byte[])data.Clone();
        var bundle = new MemoryStream();
        var keep = Enumerable.Range(0, n).Where(i => !drop.Contains(ids[i])).ToList();
        bundle.WriteByte((byte)keep.Count);
        foreach (var i in keep) bundle.WriteByte(ids[i]);
        foreach (var i in keep) bundle.Write(BitConverter.GetBytes(values[i]));
        var keepR = Enumerable.Range(0, ranged).Where(i => !drop.Contains(rids[i])).ToList();
        bundle.WriteByte((byte)keepR.Count);
        foreach (var i in keepR) bundle.WriteByte(rids[i]);
        foreach (var i in keepR) bundle.Write(ranges[i]);
        return [.. data.AsSpan(0, PropsAt), .. bundle.ToArray(), .. data.AsSpan(PropsEnd)];
    }
}
