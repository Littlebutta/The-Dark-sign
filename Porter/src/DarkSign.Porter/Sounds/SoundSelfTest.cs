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
        var act = Cat(U32(A), U16(0x0403), U32(R), [0], new byte[3], U32(12345));
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
        Console.WriteLine(_failures == 0 ? "sound-selftest: all checks passed" : $"sound-selftest: {_failures} checks FAILED");
        return _failures == 0 ? 0 : 1;
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
