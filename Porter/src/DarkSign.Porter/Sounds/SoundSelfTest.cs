using System.Text;

namespace DarkSign.Porter;

/// <summary>
/// `sound-selftest` (no game files needed; CI runs it): builds a small Wwise bank laid out like Elden Ring's (version
/// 135: an actor-mixer, a random container, a Sound, a Play action and an event), adds a ported sound to it the way
/// SoundPort does, and checks the result keeps the community's rules: a container of its own under the actor-mixer,
/// children before parents, every parent listing its children, pitch and filters left out, and a byte-exact round trip.
/// </summary>
public static class SoundSelfTest
{
    private static int _failures;

    public static int Run()
    {
        uint M = 100, R = 200, S = 300, A = 400, E = 500, media = 600, Other = 50;
        var mixer = Cat(U32(M), Base(0, [(0, -3f)]), U32(2), U32(Other), U32(R));
        var rsc = Cat(U32(R), Base(M, [(0, -14f), (2, -2200f), (3, 20f), (4, 10f)], [(2, -100f, 100f)]), U16(1), U16(0), U16(0), new byte[12], U16(1), [0, 1, 0, 0], U32(1), U32(S), U16(1), U32(S), U32(50000));
        var snd = Cat(U32(S), U32(0x00040001), [0], U32(media), U32(4), [0], Base(R, [(0, -2f), (2, -300f)]));
        var act = Cat(U32(A), U16(0x0403), U32(R), [0], new byte[3], U32(1)); // last u32: the bank holding the target (this one, ID 1)
        var ev = Cat(U32(E), [1], U32(A));
        var other = Cat(U32(Other), U32(0x00040001), [0], U32(media), U32(4), [0], Base(M, []));
        var objs = new (byte t, byte[] d)[] { (2, other), (2, snd), (5, rsc), (7, mixer), (3, act), (4, ev) };
        var hirc = Cat(U32((uint)objs.Length), objs.SelectMany(o => Cat([o.t], U32((uint)o.d.Length), o.d)).ToArray());
        var bnk = Cat(Sec("BKHD", Cat(U32(135), U32(1), new byte[8])), Sec("DIDX", Cat(U32(media), U32(0), U32(4))), Sec("DATA", [1, 2, 3, 4]), Sec("HIRC", hirc));

        var bank = WwiseBank.Read(bnk);
        Check(bank.Write().SequenceEqual(bnk), "round trip");
        foreach (var o in bank.Objects.Where(o => o.Type is 2 or 5 or 7)) Check(WwiseNode.Read(o) is not null, $"reads {o.Id}");
        var chain = bank.Chain(E)!.Value;
        Check(chain.Sound.Id == S, "chain sound");
        var wem = WwiseBank.PcmWem(new short[100], 1, 44100);
        bank.AddSound(chain, 9001, 9002, 9003, 9004, 9005, wem, false);
        var out1 = bank.Write();
        var b2 = WwiseBank.Read(out1);
        var order = b2.Objects.Select(o => o.Id).ToList();
        Check(order.IndexOf(9002) < order.IndexOf(9004) && order.IndexOf(9004) < order.IndexOf(M), "children before parents");
        var nS = WwiseNode.Read(b2.Get(9002)!)!; var nC = WwiseNode.Read(b2.Get(9004)!)!; var nM = WwiseNode.Read(b2.Get(M)!)!; var nR = WwiseNode.Read(b2.Get(R)!)!;
        Check(nS.Parent(b2.Get(9002)!.Data) == 9004, "sound parent = new container");
        Check(nC.Parent(b2.Get(9004)!.Data) == M, "container parent = mixer");
        Check(nC.Children.SequenceEqual([9002u]) && nC.Playlist.Count == 1 && nC.Playlist[0] == (9002u, 50000), "container lists sound");
        Check(nM.Children.SequenceEqual([Other, R, 9004u]), "mixer lists sorted: " + string.Join(",", nM.Children));
        Check(nR.Children.SequenceEqual([S]), "vanilla container untouched");
        Check(!nS.Props.ContainsKey(2) && nS.Props[0] == BitConverter.SingleToUInt32Bits(-2f), "sound pitch removed, volume kept");
        Check(!nC.Props.ContainsKey(2) && !nC.Props.ContainsKey(3) && !nC.Props.ContainsKey(4) && nC.Props.ContainsKey(0), "container retuned");
        Check(BitConverter.ToUInt32(b2.Get(9003)!.Data, 6) == 9004, "action plays container");
        Check(b2.Chain(9001)!.Value.Sound.Id == 9002, "chain of new event");
        Check(b2.Media(9005)!.SequenceEqual(wem), "media embedded");
        Check(BitConverter.ToUInt32(b2.Get(9002)!.Data, 4) == 0x00010001, "pcm plugin");
        Check(b2.Write().SequenceEqual(out1), "new bank round trip");
        // second sound
        bank.AddSound(chain, 9101, 9102, 9103, 9104, 9105, wem, false);
        var b3 = WwiseBank.Read(bank.Write());
        Check(WwiseNode.Read(b3.Get(M)!)!.Children.SequenceEqual([Other, R, 9004u, 9104u]), "mixer lists both");
        // Method E: the new sounds as a bank of their own: new ID, SFX language, the new media, the actor-mixer copied
        // listing only the new containers, children before parents, Play actions naming the new bank.
        var ownBytes = bank.WriteOwnBank(777);
        Check(ownBytes is not null, "own bank written");
        if (ownBytes is not null)
        {
            var own = WwiseBank.Read(ownBytes);
            Check(own.BankId == 777 && BitConverter.ToUInt32(Section(ownBytes, "BKHD"), 8) == 393239870, "own bank header");
            Check(own.Chain(9001)?.Sound.Id == 9002 && own.Chain(9101)?.Sound.Id == 9102, "own bank chains");
            Check(own.Media(9005) is not null && own.Media(media) is null, "own bank holds only new media");
            var ownMixer = own.Get(M);
            Check(ownMixer is not null && WwiseNode.Read(ownMixer)!.Children.SequenceEqual([9004u, 9104u]), "own mixer lists only new containers");
            Check(own.Get(R) is null && own.Get(S) is null && own.Get(E) is null, "own bank leaves vanilla objects out");
            var ids = own.Objects.Select(o => o.Id).ToList();
            Check(ids.IndexOf(9002) < ids.IndexOf(9004) && ids.IndexOf(9004) < ids.IndexOf(M) && ids.IndexOf(M) < ids.IndexOf(9003), "own bank order");
            var ownAction = own.Get(9003)!.Data;
            Check(BitConverter.ToUInt32(ownAction, ownAction.Length - 4) == 777, "own action names own bank");
            Check(own.Write().SequenceEqual(ownBytes), "own bank round trip");
        }

        // The game's sound index (soundbanksinfo.mobnkinfo): new events listed under their bank, appended so the
        // original bytes stay, sorted lists, a new bank added to its section in ID order.
        var sbi = Sbi([(10, [5, 7]), (20, [3, 9, 0x1_0000_0001])], [(30, [1])]);
        var idx = SoundIndex.Read(sbi);
        Check(idx.SectionCount == 2 && idx.Lists(20, 9) && idx.Lists(20, 1) && !idx.Lists(20, 4), "sbi reads");
        Check(idx.Register(20, [4, 9, 12]) == 2, "sbi register counts new");
        Check(idx.Register(15, [8], sectionOf: 20) == 1, "sbi new bank");
        var sbi2 = idx.Write();
        Check(sbi2.Length % 8 == 0 && sbi2.Length > sbi.Length, "sbi appended");
        var back = SoundIndex.Read(sbi2);
        Check(back.Lists(20, 4) && back.Lists(20, 12) && back.Lists(20, 3) && back.Lists(20, 1) && back.Lists(15, 8) && back.Lists(10, 7) && back.Lists(30, 1), "sbi round trip");
        Check(SbiEvents(sbi2, 0, 20).SequenceEqual(new ulong[] { 3, 4, 9, 12, 0x1_0000_0001 }), "sbi list sorted, flags kept: " + string.Join(",", SbiEvents(sbi2, 0, 20)));
        Check(SbiBanks(sbi2, 0).SequenceEqual(new uint[] { 10, 15, 20 }), "sbi banks sorted");
        Check(sbi2.AsSpan(0, 0x18).SequenceEqual(sbi.AsSpan(0, 0x18)) && SbiBanks(sbi2, 1).SequenceEqual([30u]), "sbi rest untouched");
        Check(new SoundIndex[] { SoundIndex.Read(SoundIndex.Read(sbi).Write()) }.All(i => !i.Changed) && SoundIndex.Read(sbi).Write().SequenceEqual(sbi), "sbi unchanged writes the original");

        // Media: appended wems start on a 16-byte boundary of DATA, and the index stays sorted by ID.
        var b3Bytes = bank.Write();
        var didx = Section(b3Bytes, "DIDX");
        var entries = Enumerable.Range(0, didx.Length / 12).Select(i => (Id: BitConverter.ToUInt32(didx, 12 * i), Offset: BitConverter.ToInt32(didx, 12 * i + 4))).ToList();
        Check(entries.Where(e => e.Id != media).All(e => e.Offset % 16 == 0), "new media 16-byte aligned");
        Check(entries.Zip(entries.Skip(1)).All(p => p.First.Id < p.Second.Id), "media index sorted");

        // PCM wem header as Wwise reads it: RIFF/WAVE, 24-byte fmt (0xFFFE, cbSize 6, 16 valid bits, AkChannelConfig).
        var w2 = WwiseBank.PcmWem(new short[8], 2, 48000);
        Check(Encoding.ASCII.GetString(w2, 0, 4) == "RIFF" && BitConverter.ToInt32(w2, 4) == w2.Length - 8 && Encoding.ASCII.GetString(w2, 8, 8) == "WAVEfmt ", "wem RIFF header");
        Check(BitConverter.ToInt32(w2, 16) == 24 && BitConverter.ToUInt16(w2, 20) == 0xFFFE && BitConverter.ToUInt16(w2, 22) == 2
              && BitConverter.ToInt32(w2, 24) == 48000 && BitConverter.ToInt32(w2, 28) == 48000 * 4 && BitConverter.ToUInt16(w2, 32) == 4
              && BitConverter.ToUInt16(w2, 34) == 16 && BitConverter.ToUInt16(w2, 36) == 6 && BitConverter.ToUInt16(w2, 38) == 16, "wem fmt chunk");
        Check(BitConverter.ToUInt32(w2, 40) == (2u | (1u << 8) | (3u << 12)), "wem stereo channel config");
        Check(BitConverter.ToUInt32(wem, 40) == (1u | (1u << 8) | (4u << 12)), "wem mono channel config");
        Check(Encoding.ASCII.GetString(w2, 44, 4) == "data" && BitConverter.ToInt32(w2, 48) == 16 && w2.Length == 52 + 16, "wem data chunk");
        var wav = SoundPort.Wav(new short[8], 2, 48000);
        Check(wav.Length == 44 + 16 && BitConverter.ToInt32(wav, 4) == wav.Length - 8 && BitConverter.ToUInt16(wav, 20) == 1, "dll wav header");

        // FSB5 sample headers: rate code 10 (96 kHz) and channel codes 2-3 (6/8 channels) read, offsets in 32-byte units.
        static byte[] Fsb(params ulong[] heads)
        {
            var hdr = heads.SelectMany(BitConverter.GetBytes).ToArray();
            var b = new byte[0x3C + hdr.Length + 64];
            "FSB5"u8.CopyTo(b); BitConverter.GetBytes(1).CopyTo(b, 4); BitConverter.GetBytes(heads.Length).CopyTo(b, 8);
            BitConverter.GetBytes(hdr.Length).CopyTo(b, 12); BitConverter.GetBytes(64).CopyTo(b, 20); BitConverter.GetBytes(15).CopyTo(b, 24);
            hdr.CopyTo(b, 0x3C);
            return b;
        }
        var fsb = Fsb((8ul << 1) | (0ul << 5) | (0ul << 7) | (100ul << 34), (10ul << 1) | (1ul << 5) | (1ul << 7) | (200ul << 34), (9ul << 1) | (2ul << 5) | (1ul << 7));
        var fs = Fsb5.Read(fsb).Samples;
        Check(fs.Count == 3 && fs[0] is { Rate: 44100, Channels: 1, Frames: 100 } && fs[0].Data.Length == 32
              && fs[1] is { Rate: 96000, Channels: 2, Frames: 200 } && fs[2] is { Rate: 48000, Channels: 6 }, "fsb5 sample headers");

        Console.WriteLine(_failures == 0 ? "sound-selftest: all checks passed" : $"sound-selftest: {_failures} checks FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // A small SBI file: header, section table, then each section's bank array and event arrays.
    private static byte[] Sbi(params (uint Bank, ulong[] Events)[][] sections)
    {
        var w = new MemoryStream();
        w.Write("SBI\0"u8); w.Write(BitConverter.GetBytes(0x10000)); w.Write(BitConverter.GetBytes(0x18L)); w.Write(BitConverter.GetBytes((long)sections.Length));
        long table = w.Length;
        w.Write(new byte[32 * sections.Length]);
        for (int s = 0; s < sections.Length; s++)
        {
            var eventAt = new List<long>();
            foreach (var (_, events) in sections[s])
            {
                eventAt.Add(w.Length);
                foreach (var e in events) w.Write(BitConverter.GetBytes(e));
            }
            long banksAt = w.Length;
            for (int b = 0; b < sections[s].Length; b++)
            {
                w.Write(BitConverter.GetBytes(sections[s][b].Bank)); w.Write(BitConverter.GetBytes(sections[s][b].Events.Length)); w.Write(BitConverter.GetBytes(eventAt[b]));
            }
            long end = w.Length;
            w.Position = table + 32 * s;
            w.Write(Enumerable.Repeat((byte)(s + 1), 16).ToArray()); w.Write(BitConverter.GetBytes((long)sections[s].Length)); w.Write(BitConverter.GetBytes(banksAt));
            w.Position = end;
        }
        return w.ToArray();
    }

    private static List<uint> SbiBanks(byte[] sbi, int section)
    {
        long at = 0x18 + 32 * section, count = BitConverter.ToInt64(sbi, (int)at + 16), banks = BitConverter.ToInt64(sbi, (int)at + 24);
        return Enumerable.Range(0, (int)count).Select(b => BitConverter.ToUInt32(sbi, (int)banks + 16 * b)).ToList();
    }

    private static List<ulong> SbiEvents(byte[] sbi, int section, uint bank)
    {
        long at = 0x18 + 32 * section, count = BitConverter.ToInt64(sbi, (int)at + 16), banks = BitConverter.ToInt64(sbi, (int)at + 24);
        for (int b = 0; b < count; b++)
        {
            int p = (int)banks + 16 * b;
            if (BitConverter.ToUInt32(sbi, p) != bank) continue;
            int n = BitConverter.ToInt32(sbi, p + 4); long e = BitConverter.ToInt64(sbi, p + 8);
            return Enumerable.Range(0, n).Select(i => BitConverter.ToUInt64(sbi, (int)e + 8 * i)).ToList();
        }
        return [];
    }

    private static byte[] Section(byte[] bnk, string tag)
    {
        for (int at = 0; at + 8 <= bnk.Length; at += 8 + BitConverter.ToInt32(bnk, at + 4))
            if (Encoding.ASCII.GetString(bnk, at, 4) == tag)
                return bnk[(at + 8)..(at + 8 + BitConverter.ToInt32(bnk, at + 4))];
        return [];
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok)
            _failures++;
    }

    private static byte[] Cat(params byte[][] p) => p.SelectMany(x => x).ToArray();
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);
    private static byte[] F(float v) => BitConverter.GetBytes(v);
    private static byte[] U16(ushort v) => BitConverter.GetBytes(v);
    // NodeBase: no fx, bus, parent, priority, props, positioning 0, aux 0, reflections 0, 6 advanced, 0 state props, 0 groups, 0 rtpcs
    private static byte[] Base(uint parent, (byte id, float v)[] props, (byte id, float a, float b)[]? ranged = null)
    {
        ranged ??= [];
        return Cat([0, 0, 0], U32(0), U32(parent), [0], [(byte)props.Length], props.Select(p => p.id).ToArray(), props.SelectMany(p => F(p.v)).ToArray(),
            [(byte)ranged.Length], ranged.Select(p => p.id).ToArray(), ranged.SelectMany(p => Cat(F(p.a), F(p.b))).ToArray(),
            [0], [0], U32(0), new byte[6], [0], [0], U16(0));
    }

    private static byte[] Sec(string t, byte[] b) => Cat(Encoding.ASCII.GetBytes(t), U32((uint)b.Length), b);
}
