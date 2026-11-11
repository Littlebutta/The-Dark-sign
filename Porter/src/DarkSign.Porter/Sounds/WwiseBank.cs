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
    private readonly Dictionary<uint, byte[]> _media = [];
    private readonly List<(uint Id, int Offset, int Size)> _index = [];
    private readonly List<(uint Id, byte[] Bytes)> _added = [];
    private byte[] _data = [];
    private readonly Dictionary<uint, HircObject> _byId = [];

    public int Version { get; private set; }

    /// <summary>The bank's own ID (BKHD), the FNV-1 hash of its name: what Play actions and the game's index name.</summary>
    public uint BankId { get; private set; }

    // The objects each AddSound created, in bank order (Sound, its container, Play action, event), for WriteOwnBank.
    private readonly List<HircObject> _new = [];

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
                    bank.BankId = body.Length >= 8 ? BitConverter.ToUInt32(body, 4) : 0;
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
        bank._data = data ?? [];
        if (didx is not null && data is not null)
            for (int i = 0; i + 12 <= didx.Length; i += 12)
            {
                uint id = BitConverter.ToUInt32(didx, i);
                int offset = BitConverter.ToInt32(didx, i + 4), size = BitConverter.ToInt32(didx, i + 8);
                bank._index.Add((id, offset, size));
                bank._media.TryAdd(id, data[offset..(offset + size)]);
            }
        return bank;
    }

    public IReadOnlyList<HircObject> Objects => _objects;
    public int MediaCount => _media.Count;
    /// <summary>Whether the original media index is sorted by ID.</summary>
    public bool MediaSorted => _index.Zip(_index.Skip(1)).All(p => p.First.Id < p.Second.Id);
    public (uint Id, byte[] Bytes)? FirstMedia() => _index.Count > 0 ? (_index[0].Id, _media[_index[0].Id]) : null;

    /// <summary>Embedded media by ID, or null when it isn't in this bank.</summary>
    public byte[]? Media(uint id) => _media.GetValueOrDefault(id);

    public bool Has(uint id) => _byId.ContainsKey(id) || _media.ContainsKey(id);

    public HircObject? Get(uint id) => _byId.GetValueOrDefault(id);

    /// <summary>The Sound an event plays: through its first Play action, directly or as the first Sound whose
    /// parent is the container the action plays.</summary>
    public (HircObject Event, HircObject Action, HircObject Sound)? Chain(uint eventId) =>
        Plays(eventId) is [var (action, sound), ..] ? (Get(eventId)!, action, sound) : null;

    /// <summary>Adds an event that plays new PCM media, cloned from a template chain. Streamed media stays out of
    /// the bank (the game reads it from /wem/NN/&lt;media ID&gt;.wem), so the bank only grows by the new objects;
    /// otherwise the media is embedded.
    /// <para>The new objects are laid out the way the community's sound tools add a sound (Yonder's "simple sound",
    /// ndahn's er_soundbank_helper, Spellforge's copy into cs_main): Event -> Play action -> a random container of
    /// its own -> the Sound, with the container hung under the template's actor-mixer (so it keeps the weapon
    /// sounds' bus, attenuation and 3D settings) and listed by it, children before parents in the hierarchy.
    /// The template's pitch and low/high-pass filters are left out: Elden Ring's swing whoosh container is tuned
    /// for its own sample (about -2,200 cents with both filters), which buried a DS3 sample under it.
    /// When the template's nodes can't be read exactly, the older layout is used (the Sound under the template's
    /// own container, played directly).</para></summary>
    public void AddSound((HircObject Event, HircObject Action, HircObject Sound) template, uint eventId, uint soundId, uint actionId,
        uint containerId, uint mediaId, byte[] wem, bool streamed, IReadOnlyList<(uint SoundId, uint MediaId, byte[] Wem)>? variants = null) =>
        AddSound(template, eventId, [new Layer(actionId, containerId, [(soundId, mediaId, wem), .. variants ?? []])], streamed);

    /// <summary>One layer of a new event: its Play action, its container, and its Sounds (the first, then its random
    /// variants). Two Sounds may name the same media ID (the same sample under two events); it is embedded once.</summary>
    public sealed record Layer(uint ActionId, uint ContainerId, IReadOnlyList<(uint SoundId, uint MediaId, byte[] Wem)> Sounds);

    /// <summary>Adds an event whose layers all start together, as the layers of a DS3 (FMOD) event do: one Play
    /// action per layer, each playing that layer's own Sound or random container, built like a single new sound
    /// (see the overload above). Elden Ring layers sounds the same way: 161 of cs_main's own events have two to four
    /// Play actions.</summary>
    public void AddSound((HircObject Event, HircObject Action, HircObject Sound) template, uint eventId, IReadOnlyList<Layer> layers, bool streamed)
    {
        if (layers.Count == 0 || layers.Count > 127 || layers.Any(l => l.Sounds.Count == 0))
            throw new ArgumentException("an event needs 1 to 127 layers with a sound each", nameof(layers));
        var actions = new List<HircObject>();
        foreach (var layer in layers)
            actions.Add(AddLayer(template, layer, streamed));
        LayersAdded += layers.Count - 1;
        // Event: ID and its action list, in the template's count format (u8 in newer banks, u32 before).
        bool byteCount = template.Event.Data.Length == 5 + 4 * template.Event.Data[4];
        var ev = new MemoryStream();
        ev.Write(BitConverter.GetBytes(eventId));
        if (byteCount) ev.WriteByte((byte)actions.Count);
        else ev.Write(BitConverter.GetBytes(actions.Count));
        foreach (var action in actions) ev.Write(BitConverter.GetBytes(action.Id));
        foreach (var obj in actions.Append(new HircObject(EventType, eventId, ev.ToArray())))
        {
            _objects.Add(obj);
            _byId[obj.Id] = obj;
            _new.Add(obj);
        }
    }

    /// <summary>How many layers beyond their event's first were added (each with a Play action of its own).</summary>
    public int LayersAdded { get; private set; }

    // One layer's Sounds and container go into the hierarchy; returns its Play action (the caller adds it with the event).
    private HircObject AddLayer((HircObject Event, HircObject Action, HircObject Sound) template, Layer layer, bool streamed)
    {
        var (soundId, mediaId, wem) = layer.Sounds[0];
        uint actionId = layer.ActionId, containerId = layer.ContainerId;
        // DS3 sounds come in variants (c000001043, c000001043b, c...c, ...d), played at random by FMOD: they become
        // extra Sounds in the new random container (only with a container of their own; the older layout keeps one).
        var variants = layer.Sounds.Skip(1).ToList();
        // Sound: ID, then AkBankSourceData: plugin (PCM), stream type (0 in the bank, 2 streamed), media ID and size.
        // A source-plugin template (e.g. Wwise Silence, plugin type 2) carries a u32 parameter size and its parameters
        // after that; a codec Sound has neither, and leaving them in makes Wwise reject the whole bank.
        var sound = template.Sound.Data;
        if ((BitConverter.ToUInt32(sound, 4) & 0xF) == 2)
            sound = [.. sound.AsSpan(0, 18), .. sound.AsSpan(22 + BitConverter.ToInt32(sound, 18))];
        else
            sound = (byte[])sound.Clone();
        BitConverter.GetBytes(soundId).CopyTo(sound, 0);
        BitConverter.GetBytes(0x00010001u).CopyTo(sound, 4);
        sound[8] = streamed ? (byte)2 : (byte)0;
        BitConverter.GetBytes(mediaId).CopyTo(sound, 9);
        BitConverter.GetBytes(wem.Length).CopyTo(sound, 13);
        sound[17] &= unchecked((byte)~0x02); // not prefetched
        if (OwnContainers && WwiseNode.Read(new HircObject(SoundType, soundId, sound)) is { } soundNode)
            sound = soundNode.WithoutProps(sound, Retuned);

        uint target = soundId;
        HircObject? insertBefore = null, newContainer = null;
        var allIds = new[] { soundId }.Concat(variants.Select(v => v.SoundId)).ToList();
        if (OwnContainers && ContainerFor(template.Sound, sound, allIds, containerId) is { } own)
        {
            (newContainer, insertBefore, sound) = own;
            target = containerId;
            OwnContainerCount++;
        }

        var action = (byte[])template.Action.Data.Clone();
        BitConverter.GetBytes(actionId).CopyTo(action, 0);
        BitConverter.GetBytes(target).CopyTo(action, 6);

        var newSound = new HircObject(SoundType, soundId, sound);
        if (newContainer is not null)
        {
            // Wwise loads the hierarchy children first: Sound, then its container, then the actor-mixer listing it.
            int at = _objects.IndexOf(insertBefore!);
            _objects.Insert(at, newContainer);
            _objects.Insert(at, newSound);
            _byId[newContainer.Id] = newContainer;
            _new.Add(newSound);
            foreach (var (vId, vMedia, vWem) in variants)
            {
                var v = (byte[])sound.Clone();
                BitConverter.GetBytes(vId).CopyTo(v, 0);
                BitConverter.GetBytes(vMedia).CopyTo(v, 9);
                BitConverter.GetBytes(vWem.Length).CopyTo(v, 13);
                var vSound = new HircObject(SoundType, vId, v);
                _objects.Insert(_objects.IndexOf(newContainer), vSound);
                _byId[vId] = vSound;
                _new.Add(vSound);
                if (!streamed && _media.TryAdd(vMedia, vWem))
                    _added.Add((vMedia, vWem));
                VariantsAdded++;
            }
            _new.Add(newContainer);
        }
        else
        {
            // A Sound added to its parent's list goes right before the parent; the rest go at the end.
            var parent = AddChild(template.Sound, soundId);
            if (parent is not null)
                _objects.Insert(_objects.IndexOf(parent), newSound);
            else
                _objects.Add(newSound);
            _new.Add(newSound);
        }
        _byId[soundId] = newSound;
        if (!streamed && _media.TryAdd(mediaId, wem))
            _added.Add((mediaId, wem));
        return new HircObject(ActionType, actionId, action);
    }

    /// <summary>The Sound each of an event's Play actions plays (see <see cref="Chain"/>), one per action.</summary>
    public IReadOnlyList<(HircObject Action, HircObject Sound)> Plays(uint eventId)
    {
        var plays = new List<(HircObject, HircObject)>();
        if (Get(eventId) is not { Type: EventType } ev)
            return plays;
        foreach (var actionId in EventActions(ev))
        {
            if (Get(actionId) is not { Type: ActionType } action || BitConverter.ToUInt16(action.Data, 4) != 0x0403)
                continue;
            uint target = BitConverter.ToUInt32(action.Data, 6);
            if (Get(target) is { Type: SoundType } sound)
                plays.Add((action, sound));
            else if (_objects.FirstOrDefault(o => o.Type == SoundType && ParentIs(o, target)) is { } child)
                plays.Add((action, child));
        }
        return plays;
    }

    /// <summary>DARKSIGN_SOUND_CONTAINERS=0 goes back to the older layout (the Sound under Elden Ring's own container).</summary>
    public static readonly bool OwnContainers = Environment.GetEnvironmentVariable("DARKSIGN_SOUND_CONTAINERS") != "0";

    private static readonly byte[] Retuned = [WwiseNode.PropPitch, WwiseNode.PropLowPass, WwiseNode.PropHighPass];

    /// <summary>How many extra DS3 variants went into the new containers.</summary>
    public int VariantsAdded { get; private set; }

    /// <summary>How many new sounds got a container of their own.</summary>
    public int OwnContainerCount { get; private set; }

    // A random container of its own for a new Sound: a copy of the template Sound's container (its volume and
    // playback settings, without pitch and filters) holding just the new Sound, under the nearest actor-mixer above
    // it, which lists it. Returns the container, the actor-mixer (the container goes right before it) and the Sound
    // data with its parent set to the container; null when the template isn't laid out that way.
    private (HircObject Container, HircObject Mixer, byte[] Sound)? ContainerFor(HircObject templateSound, byte[] sound, IReadOnlyList<uint> soundIds, uint containerId)
    {
        uint soundId = soundIds[0];
        if (WwiseNode.Read(new HircObject(SoundType, soundId, sound)) is not { } soundNode
            || Get(soundNode.Parent(sound)) is not { Type: RanSeqCntrType } container
            || WwiseNode.Read(container) is not { PlaylistAt: >= 0 } containerNode)
            return null;
        // Up to the nearest actor-mixer (a container can sit under a switch or layer container first).
        HircObject? mixer = null;
        WwiseNode? mixerNode = null;
        var node = containerNode;
        var obj = container;
        for (int depth = 0; depth < 8 && mixer is null; depth++)
        {
            if (Get(node.Parent(obj.Data)) is not { } up || WwiseNode.Read(up) is not { } upNode)
                return null;
            if (up.Type == ActorMixerType && upNode.ChildrenAt >= 0)
                (mixer, mixerNode) = (up, upNode);
            (obj, node) = (up, upNode);
        }
        if (mixer is null || mixerNode is null)
            return null;

        var data = containerNode.WithoutProps(container.Data, Retuned);
        if (WwiseNode.Read(new HircObject(RanSeqCntrType, containerId, data)) is not { PlaylistAt: >= 0 } copy)
            return null;
        int weight = containerNode.Playlist.Count > 0 ? containerNode.Playlist[0].Weight : 50000;
        BitConverter.GetBytes(containerId).CopyTo(data, 0);
        BitConverter.GetBytes(mixer.Id).CopyTo(data, copy.ParentAt);
        // Avoid repeating the variant just played, as the game's own random containers do (one child: nothing to avoid).
        int avoid = Math.Min(BitConverter.ToUInt16(container.Data, containerNode.AvoidRepeatAt), soundIds.Count - 1);
        BitConverter.GetBytes((ushort)Math.Max(0, avoid)).CopyTo(data, copy.AvoidRepeatAt);
        var sorted = soundIds.Order().ToList();
        var lists = new MemoryStream();
        lists.Write(BitConverter.GetBytes(sorted.Count));
        foreach (var id in sorted) lists.Write(BitConverter.GetBytes(id));
        lists.Write(BitConverter.GetBytes((ushort)soundIds.Count));
        foreach (var id in soundIds) { lists.Write(BitConverter.GetBytes(id)); lists.Write(BitConverter.GetBytes(weight)); }
        data = [.. data.AsSpan(0, copy.ChildrenAt), .. lists.ToArray()];

        // The actor-mixer lists its children sorted by ID (every game bank does), at the end of its data.
        var kids = mixerNode.Children.Append(containerId).Distinct().Order().ToList();
        var list = new MemoryStream();
        list.Write(BitConverter.GetBytes(kids.Count));
        foreach (var k in kids) list.Write(BitConverter.GetBytes(k));
        mixer.Data = [.. mixer.Data.AsSpan(0, mixerNode.ChildrenAt), .. list.ToArray(), .. mixer.Data.AsSpan(mixerNode.ChildrenAt + 4 + 4 * mixerNode.Children.Count)];
        ChildrenAdded++;

        var newSound = (byte[])sound.Clone();
        BitConverter.GetBytes(containerId).CopyTo(newSound, soundNode.ParentAt);
        return (new HircObject(RanSeqCntrType, containerId, data), mixer, newSound);
    }

    /// <summary>How many new Sounds were added to their parent container's child list.</summary>
    public int ChildrenAdded { get; private set; }

    // Containers list their children as a count and IDs sorted ascending; the new ID goes into the list holding the
    // sibling, in order. Returns the parent when the child was added to it.
    private HircObject? AddChild(HircObject sibling, uint child)
    {
        var parent = ParentOf(sibling);
        if (parent is null)
            return null;
        var d = parent.Data;
        for (int p = 4; p + 8 <= d.Length; p++)
        {
            int count = BitConverter.ToInt32(d, p);
            if (count < 1 || count > 4096 || p + 4 + 4 * count > d.Length)
                continue;
            var ids = Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(d, p + 4 + 4 * i)).ToList();
            if (!ids.Contains(sibling.Id) || !ids.Zip(ids.Skip(1)).All(x => x.First < x.Second) || !ids.All(_byId.ContainsKey))
                continue;
            int at = ids.Count(id => id < child);
            parent.Data = [.. d.AsSpan(0, p), .. BitConverter.GetBytes(count + 1), .. d.AsSpan(p + 4, 4 * at), .. BitConverter.GetBytes(child),
                .. d.AsSpan(p + 4 + 4 * at)];
            ChildrenAdded++;
            return parent;
        }
        return null;
    }

    public const byte RanSeqCntrType = 5, SwitchCntrType = 6, ActorMixerType = 7, LayerCntrType = 9;

    private HircObject? ParentOf(HircObject sound)
    {
        var d = sound.Data;
        for (int p = 18; p + 4 <= Math.Min(d.Length, 64); p++)
            if (Get(BitConverter.ToUInt32(d, p)) is { Type: RanSeqCntrType or SwitchCntrType or ActorMixerType or LayerCntrType } parent)
                return parent;
        return null;
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
            // The original DATA stays byte for byte; new media is appended, each aligned to 16 bytes. The index keeps
            // the original's order, with new entries sorted in by ID when the original's is sorted.
            var data = new MemoryStream();
            data.Write(_data);
            var entries = new List<(uint Id, int Offset, int Size)>(_index);
            foreach (var (id, bytes) in _added)
            {
                while (data.Length % 16 != 0)
                    data.WriteByte(0);
                entries.Add((id, (int)data.Length, bytes.Length));
                data.Write(bytes);
            }
            if (MediaSorted)
                entries = [.. entries.OrderBy(e => e.Id)];
            var index = new MemoryStream();
            foreach (var (id, offset, size) in entries)
            {
                index.Write(BitConverter.GetBytes(id));
                index.Write(BitConverter.GetBytes(offset));
                index.Write(BitConverter.GetBytes(size));
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

    public const byte AttenuationType = 14;
    private const byte PropAttenuation = 0x46;
    private const uint LanguageSfx = 393239870; // FNV-1 of "SFX": banks that aren't localized

    /// <summary>
    /// The sounds added so far as a bank of their own (launcher fallback "sounds" E), the way the community's custom
    /// banks are made (ndahn's empty soundbank template, Yonder's "Custom Soundbanks" guide): this bank's header with
    /// a new ID, not localized; the new media; attenuations first, then the new Sounds and containers, then unchanged
    /// copies of every actor-mixer above them (Wwise keeps the first loaded copy of an ID, so these are the same
    /// objects as cs_main's), each listing only the children this bank has; then the Play actions, now naming this
    /// bank, and the events. Null when the new sounds don't hang under actor-mixers this reader can rewrite.
    /// </summary>
    public byte[]? WriteOwnBank(uint bankId)
    {
        var bkhd = _sections.FirstOrDefault(s => s.Tag == "BKHD").Body;
        if (bkhd is null || bkhd.Length < 12 || _new.Count == 0)
            return null;
        var header = (byte[])bkhd.Clone();
        BitConverter.GetBytes(bankId).CopyTo(header, 4);
        BitConverter.GetBytes(LanguageSfx).CopyTo(header, 8);

        var tree = _new.Where(o => o.Type is SoundType or RanSeqCntrType).ToList();
        var mixers = new List<HircObject>(); // nearest first
        var childrenOf = new Dictionary<uint, SortedSet<uint>>();
        var treeIds = tree.Select(o => o.Id).ToHashSet();
        foreach (var obj in tree)
        {
            var current = obj;
            for (int depth = 0; depth < 16; depth++)
            {
                if (WwiseNode.Read(current) is not { } node)
                    return null;
                uint parent = node.Parent(current.Data);
                if (parent == 0 || treeIds.Contains(parent))
                    break; // the root, or the Sound's own new container
                if (Get(parent) is not { Type: ActorMixerType } up || WwiseNode.Read(up) is not { ChildrenAt: >= 0 })
                    return null;
                bool seen = childrenOf.ContainsKey(parent);
                if (!seen)
                    childrenOf[parent] = [];
                childrenOf[parent].Add(current.Id);
                if (seen)
                    break; // the rest of this branch is already collected
                mixers.Add(up);
                current = up;
            }
        }
        // Each copied actor-mixer lists only the children this bank has.
        var copies = new List<HircObject>();
        foreach (var mixer in mixers)
        {
            var node = WwiseNode.Read(mixer)!;
            var kids = childrenOf[mixer.Id];
            var list = new MemoryStream();
            list.Write(BitConverter.GetBytes(kids.Count));
            foreach (var k in kids) list.Write(BitConverter.GetBytes(k));
            var data = (byte[])mixer.Data.Clone();
            data = [.. data.AsSpan(0, node.ChildrenAt), .. list.ToArray(), .. data.AsSpan(node.ChildrenAt + 4 + 4 * node.Children.Count)];
            copies.Add(new HircObject(ActorMixerType, mixer.Id, data));
        }
        // Children before parents: a mixer goes after every mixer below it.
        copies = [.. copies.OrderBy(c => Depth(c.Id, copies))];
        var attenuations = tree.Concat(mixers).Select(o => WwiseNode.Read(o)?.Props.GetValueOrDefault(PropAttenuation) ?? 0)
            .Where(id => id != 0).Distinct().Select(Get).OfType<HircObject>().Where(o => o.Type == AttenuationType).ToList();
        var actions = _new.Where(o => o.Type == ActionType).Select(a =>
        {
            var d = (byte[])a.Data.Clone();
            if (d.Length >= 4 && BitConverter.ToUInt32(d, d.Length - 4) == BankId)
                BitConverter.GetBytes(bankId).CopyTo(d, d.Length - 4); // a Play action names the bank holding its target
            return new HircObject(ActionType, a.Id, d);
        }).ToList();
        var objects = attenuations.Concat(tree).Concat(copies).Concat(actions).Concat(_new.Where(o => o.Type == EventType)).ToList();

        var output = new MemoryStream();
        void Section(string tag, byte[] body)
        {
            output.Write(Encoding.ASCII.GetBytes(tag));
            output.Write(BitConverter.GetBytes(body.Length));
            output.Write(body);
        }
        Section("BKHD", header);
        if (_added.Count > 0)
        {
            var data = new MemoryStream();
            var index = new MemoryStream();
            foreach (var (id, bytes) in _added.OrderBy(m => m.Id))
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
        var hirc = new MemoryStream();
        hirc.Write(BitConverter.GetBytes(objects.Count));
        foreach (var obj in objects)
        {
            hirc.WriteByte(obj.Type);
            hirc.Write(BitConverter.GetBytes(obj.Data.Length));
            hirc.Write(obj.Data);
        }
        Section("HIRC", hirc.ToArray());
        return output.ToArray();
    }

    // How many of the copied mixers sit below this one (0 for the nearest to the sounds).
    private int Depth(uint mixerId, List<HircObject> copies)
    {
        int depth = 0;
        foreach (var c in copies)
        {
            var obj = c;
            for (int i = 0; i < 16 && WwiseNode.Read(obj) is { } n; i++)
            {
                uint up = n.Parent(obj.Data);
                if (up == mixerId) { depth++; break; }
                if (copies.FirstOrDefault(x => x.Id == up) is not { } next) break;
                obj = next;
            }
        }
        return depth;
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

    // Standard speaker masks: mono FC, stereo FL FR, 5.1 and 7.1 (FL FR FC LFE + back/side pairs).
    private static int SpeakerMask(int channels) => channels switch { 1 => 0x4, 2 => 0x3, 4 => 0x33, 6 => 0x3F, 8 => 0x63F, _ => 0x3 };

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
        // Wwise keeps an AkChannelConfig here: channel count, config type 1 (standard), speaker mask.
        U32(channels | (1 << 8) | (SpeakerMask(channels) << 12));
        w.Write("data"u8);
        U32(data.Length);
        w.Write(data);
        return w.ToArray();
    }
}
