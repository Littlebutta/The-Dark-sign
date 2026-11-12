namespace DarkSign.Porter;

/// <summary>
/// FromSoftware's own index of which sound events each Wwise bank provides: /sound/soundbanksinfo.mobnkinfo ("SBI",
/// read by the game's MOSoundBankInfoAccessor). Wwise itself only needs the event in a loaded bank, but the game
/// consults this list first, presumably to resolve an event to its bank. A brand-new event that isn't listed is a
/// likely reason why ported swings with new sound numbers stayed silent while the same audio played under an existing
/// number (seen on Devin's PC, 2026-11-01). The Nightreign mod "Nightreign Ultimate Voices" registers its new
/// Play_c9xxxxxxxx events here for the same reason.
/// <para>Layout (TKGP's SBI.bt in Smithbox; ndahn's yonder/game/mo.py): "SBI\0", u32 version, u64 section table
/// offset (0x18), u64 section count; a section is a 16-byte GUID, u64 bank count, u64 bank array offset; a bank is
/// u32 bank ID (its BKHD ID), u32 event count, u64 event array offset; an event is a u64: the event ID in the low 32
/// bits, bit 32 set for events that loop until stopped.</para>
/// <para>Edits are appended, the way Nightreign Ultimate Voices patches it: every original byte stays where it is,
/// a changed bank gets a new event array at the end of the file, and only that bank's count and offset change (a new
/// bank: a new bank array for its section). Undoing the pointer changes and cutting the tail gives the original back.</para>
/// </summary>
public sealed class SoundIndex
{
    public const string ArchivePath = "/sound/soundbanksinfo.mobnkinfo";

    private readonly byte[] _original;
    private readonly List<Section> _sections = [];

    private sealed class Section
    {
        public long Offset;               // of the 32-byte section record
        public List<Bank> Banks = [];
        public bool BanksChanged;
    }

    private sealed class Bank
    {
        public uint Id;
        public List<ulong> Events = [];
        public bool Changed;
    }

    private SoundIndex(byte[] bytes) => _original = bytes;

    public int SectionCount => _sections.Count;

    /// <summary>Reads the index; throws InvalidDataException when the file isn't laid out as expected.</summary>
    public static SoundIndex Read(byte[] bytes)
    {
        if (bytes.Length < 0x18 || bytes[0] != 'S' || bytes[1] != 'B' || bytes[2] != 'I' || bytes[3] != 0)
            throw new InvalidDataException("not an SBI file");
        var index = new SoundIndex(bytes);
        long table = BitConverter.ToInt64(bytes, 8);
        long count = BitConverter.ToInt64(bytes, 16);
        if (table < 0x18 || count is < 1 or > 64 || table + 32 * count > bytes.Length)
            throw new InvalidDataException($"SBI section table {table:X} x{count} doesn't fit");
        for (int s = 0; s < count; s++)
        {
            long at = table + 32 * s;
            long banks = BitConverter.ToInt64(bytes, (int)at + 16), bankAt = BitConverter.ToInt64(bytes, (int)at + 24);
            if (banks < 0 || banks > 100_000 || (banks > 0 && (bankAt < 0x18 || bankAt + 16 * banks > bytes.Length)))
                throw new InvalidDataException($"SBI section {s}: bank array {bankAt:X} x{banks} doesn't fit");
            var section = new Section { Offset = at };
            for (int b = 0; b < banks; b++)
            {
                int p = (int)(bankAt + 16 * b);
                var bank = new Bank { Id = BitConverter.ToUInt32(bytes, p) };
                int events = BitConverter.ToInt32(bytes, p + 4);
                long eventAt = BitConverter.ToInt64(bytes, p + 8);
                if (events < 0 || (events > 0 && (eventAt < 0x18 || eventAt + 8L * events > bytes.Length)))
                    throw new InvalidDataException($"SBI bank {bank.Id}: event array {eventAt:X} x{events} doesn't fit");
                for (int e = 0; e < events; e++)
                    bank.Events.Add(BitConverter.ToUInt64(bytes, (int)eventAt + 8 * e));
                section.Banks.Add(bank);
            }
            index._sections.Add(section);
        }
        return index;
    }

    /// <summary>Whether any bank lists this event.</summary>
    public bool Lists(uint eventId) => _sections.Any(s => s.Banks.Any(b => b.Events.Any(e => (uint)e == eventId)));

    /// <summary>Whether a bank lists this event.</summary>
    public bool Lists(uint bankId, uint eventId) =>
        _sections.SelectMany(s => s.Banks).Where(b => b.Id == bankId).Any(b => b.Events.Any(e => (uint)e == eventId));

    public bool HasBank(uint bankId) => _sections.Any(s => s.Banks.Any(b => b.Id == bankId));

    /// <summary>For surveys: each section's bank count, and the banks holding <paramref name="bankId"/> with their events.</summary>
    public IEnumerable<string> Describe(uint? bankId = null)
    {
        for (int i = 0; i < _sections.Count; i++)
        {
            var banks = _sections[i].Banks;
            if (bankId is null)
                yield return $"section {i}: {banks.Count} banks, {banks.Sum(b => b.Events.Count)} events";
            foreach (var b in banks.Where(b => b.Id == bankId))
                yield return $"section {i} bank {b.Id}: {b.Events.Count} events: " +
                             string.Join(" ", b.Events.Take(40).Select(e => $"{(uint)e}{((e >> 32) != 0 ? $"+{e >> 32:X}" : "")}"));
        }
    }

    /// <summary>
    /// Lists these events under the bank (flag 0: one-shot sounds). A bank the index doesn't have yet is added to the
    /// section holding <paramref name="sectionOf"/> (the bank whose Wwise project it shares, e.g. cs_main), else to
    /// the first section. Returns how many events were new.
    /// </summary>
    public int Register(uint bankId, IEnumerable<uint> eventIds, uint? sectionOf = null)
    {
        var bank = _sections.SelectMany(s => s.Banks).FirstOrDefault(b => b.Id == bankId);
        if (bank is null)
        {
            var section = _sections.FirstOrDefault(s => sectionOf is { } o && s.Banks.Any(b => b.Id == o)) ?? _sections[0];
            bank = new Bank { Id = bankId };
            section.Banks.Add(bank);
            section.Banks.Sort((a, b) => a.Id.CompareTo(b.Id)); // the game's bank arrays are sorted by ID
            section.BanksChanged = true;
        }
        var known = bank.Events.Select(e => (uint)e).ToHashSet();
        // The game's lists are sorted (it binary searches them). Keep whichever order this one has: by event ID, or
        // by the whole u64 (flagged loops after the rest); the two agree when no event is flagged.
        bool byId = IsSorted(bank.Events, e => (uint)e);
        int added = 0;
        foreach (var id in eventIds.Distinct())
            if (known.Add(id))
            {
                bank.Events.Add(id);
                added++;
            }
        if (added > 0)
        {
            bank.Events = byId ? [.. bank.Events.OrderBy(e => (uint)e)] : [.. bank.Events.Order()];
            bank.Changed = true;
        }
        return added;
    }

    private static bool IsSorted(List<ulong> list, Func<ulong, ulong> key)
    {
        for (int i = 1; i < list.Count; i++)
            if (key(list[i - 1]) > key(list[i]))
                return false;
        return true;
    }

    public bool Changed => _sections.Any(s => s.BanksChanged || s.Banks.Any(b => b.Changed));

    /// <summary>The original bytes, with changed bank and event arrays appended (8-aligned) and pointed at.</summary>
    public byte[] Write()
    {
        var output = new MemoryStream();
        output.Write(_original);
        void Align()
        {
            while (output.Length % 8 != 0)
                output.WriteByte(0);
        }
        var buffer = output; // patched in place below
        void PutU32(long at, uint v) { buffer.Position = at; buffer.Write(BitConverter.GetBytes(v)); buffer.Position = buffer.Length; }
        void PutI64(long at, long v) { buffer.Position = at; buffer.Write(BitConverter.GetBytes(v)); buffer.Position = buffer.Length; }

        foreach (var section in _sections)
        {
            // New event arrays first, so a rewritten bank array can point at them.
            var eventAt = new Dictionary<Bank, long>();
            foreach (var bank in section.Banks.Where(b => b.Changed))
            {
                Align();
                eventAt[bank] = output.Length;
                foreach (var e in bank.Events)
                    output.Write(BitConverter.GetBytes(e));
            }
            if (section.BanksChanged)
            {
                // A whole new bank array: unchanged banks keep pointing at their original event arrays.
                var original = OriginalBanks(section);
                Align();
                long arrayAt = output.Length;
                foreach (var bank in section.Banks)
                {
                    output.Write(BitConverter.GetBytes(bank.Id));
                    output.Write(BitConverter.GetBytes(bank.Events.Count));
                    long at = eventAt.TryGetValue(bank, out var moved) ? moved
                        : original.TryGetValue(bank.Id, out var kept) ? kept : 0;
                    output.Write(BitConverter.GetBytes(bank.Events.Count == 0 ? 0 : at));
                }
                PutI64(section.Offset + 16, section.Banks.Count);
                PutI64(section.Offset + 24, arrayAt);
            }
            else
            {
                long bankArray = BitConverter.ToInt64(_original, (int)section.Offset + 24);
                for (int b = 0; b < section.Banks.Count; b++)
                {
                    var bank = section.Banks[b];
                    if (!eventAt.TryGetValue(bank, out var at))
                        continue;
                    long record = bankArray + 16 * b;
                    PutU32(record + 4, (uint)bank.Events.Count);
                    PutI64(record + 8, at);
                }
            }
        }
        Align();
        return output.ToArray();
    }

    // Each original bank's event array offset, by bank ID.
    private Dictionary<uint, long> OriginalBanks(Section section)
    {
        var result = new Dictionary<uint, long>();
        long banks = BitConverter.ToInt64(_original, (int)section.Offset + 16), bankAt = BitConverter.ToInt64(_original, (int)section.Offset + 24);
        for (int b = 0; b < banks; b++)
            result[BitConverter.ToUInt32(_original, (int)(bankAt + 16 * b))] = BitConverter.ToInt64(_original, (int)(bankAt + 16 * b + 8));
        return result;
    }
}
