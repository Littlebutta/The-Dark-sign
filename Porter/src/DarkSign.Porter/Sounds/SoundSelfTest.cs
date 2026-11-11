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
        // The template sound's Stop event, as the game's look: a Stop action (0x0103) on what the Play action plays,
        // here with a delay (0x0F) and a 1 s fade (0x10) and a tail of its own.
        uint SA = 450, SE = 550;
        var stopAct = Cat(U32(SA), U16(0x0103), U32(R), [0], [2], [0x0F, 0x10], U32(500), U32(1000), [0], [5, 6, 0]);
        var stopEv = Cat(U32(SE), [1], U32(SA));
        var objs = new (byte t, byte[] d)[] { (2, other), (2, snd), (5, rsc), (7, mixer), (3, act), (4, ev), (3, stopAct), (4, stopEv) };
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
        // second sound, with two DS3 random variants (c...b, c...c) in its container
        var wemB = WwiseBank.PcmWem([1, 2, 3, 4], 1, 44100);
        var wemC = WwiseBank.PcmWem([5, 6, 7, 8, 9, 10], 1, 44100);
        bank.AddSound(chain, 9101, 9102, 9103, 9104, 9105, wem, false, [(9108u, 9109u, wemC), (9106u, 9107u, wemB)]);
        var b3 = WwiseBank.Read(bank.Write());
        Check(WwiseNode.Read(b3.Get(M)!)!.Children.SequenceEqual([Other, R, 9004u, 9104u]), "mixer lists both");
        var vC = b3.Get(9104)!;
        var nVC = WwiseNode.Read(vC)!;
        Check(nVC.Children.SequenceEqual([9102u, 9106u, 9108u]) && nVC.Playlist.Count == 3 && nVC.Playlist.All(p => p.Weight == 50000), "variant container lists all: " + string.Join(",", nVC.Children));
        Check(BitConverter.ToUInt16(vC.Data, nVC.AvoidRepeatAt) <= 2, "variant avoid repeat fits");
        Check(new uint[] { 9106, 9108 }.All(v => b3.Get(v) is { } o && WwiseNode.Read(o)!.Parent(o.Data) == 9104), "variants under the container");
        Check(BitConverter.ToUInt32(b3.Get(9106)!.Data, 9) == 9107 && BitConverter.ToInt32(b3.Get(9106)!.Data, 13) == wemB.Length, "variant media ref");
        Check(b3.Media(9107)!.SequenceEqual(wemB) && b3.Media(9109)!.SequenceEqual(wemC), "variant media embedded");
        Check(bank.VariantsAdded == 2, "variants counted");
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
            Check(own.Media(9107) is not null && own.Get(9108) is not null, "own bank holds the variants");
            var ownMixer = own.Get(M);
            Check(ownMixer is not null && WwiseNode.Read(ownMixer)!.Children.SequenceEqual([9004u, 9104u]), "own mixer lists only new containers");
            Check(own.Get(R) is null && own.Get(S) is null && own.Get(E) is null, "own bank leaves vanilla objects out");
            var ids = own.Objects.Select(o => o.Id).ToList();
            Check(ids.IndexOf(9002) < ids.IndexOf(9004) && ids.IndexOf(9004) < ids.IndexOf(M) && ids.IndexOf(M) < ids.IndexOf(9003), "own bank order");
            var ownAction = own.Get(9003)!.Data;
            Check(BitConverter.ToUInt32(ownAction, ownAction.Length - 4) == 777, "own action names own bank");
            Check(own.Write().SequenceEqual(ownBytes), "own bank round trip");
        }

        // A DS3 event with two layers: one Play action per layer, all in one event, each layer with a container and
        // media of its own. The second layer has two variants, and its first Sound plays media an earlier sound embedded.
        var wemL = WwiseBank.PcmWem([11, 12], 1, 48000);
        bank.AddSound(chain, 9201, [new WwiseBank.Layer(9203, 9204, [(9202u, 9205u, wemB)]), new WwiseBank.Layer(9213, 9214, [(9212u, 9005u, wem), (9216u, 9217u, wemL)])], false);
        var b4Bytes = bank.Write();
        var b4 = WwiseBank.Read(b4Bytes);
        var plays = b4.Plays(9201);
        Check(b4.Get(9201)!.Data.SequenceEqual(Cat(U32(9201), [2], U32(9203), U32(9213))), "layered event lists both actions");
        Check(plays.Count == 2 && plays[0].Action.Id == 9203 && plays[1].Action.Id == 9213, "layered event: one Play action per layer");
        Check(plays.Count == 2 && BitConverter.ToUInt32(plays[0].Action.Data, 6) == 9204 && BitConverter.ToUInt32(plays[1].Action.Data, 6) == 9214, "layer actions play their own containers");
        Check(plays.Count == 2 && plays[0].Sound.Id == 9202 && plays[1].Sound.Id == 9212, "layers resolve to their sounds");
        Check(plays.All(p => b4.Media(BitConverter.ToUInt32(p.Sound.Data, 9)) is { } m && BitConverter.ToInt32(p.Sound.Data, 13) == m.Length), "each layer resolves to media");
        Check(new uint[] { 9204, 9214 }.All(c => b4.Get(c) is { } o && WwiseNode.Read(o)!.Parent(o.Data) == M), "layer containers under the mixer");
        Check(WwiseNode.Read(b4.Get(9214)!)!.Children.SequenceEqual([9212u, 9216u]) && WwiseNode.Read(b4.Get(9204)!)!.Children.SequenceEqual([9202u]), "layer containers list their sounds");
        Check(WwiseNode.Read(b4.Get(M)!)!.Children.SequenceEqual([Other, R, 9004u, 9104u, 9204u, 9214u]), "mixer lists the layer containers");
        var ids4 = b4.Objects.Select(o => o.Id).ToList();
        Check(ids4.IndexOf(9212) < ids4.IndexOf(9214) && ids4.IndexOf(9214) < ids4.IndexOf(M) && ids4.IndexOf(M) < ids4.IndexOf(9213) && ids4.IndexOf(9213) < ids4.IndexOf(9201), "layered order: sounds, containers, mixer, actions, event");
        var didx4 = Section(b4Bytes, "DIDX");
        Check(Enumerable.Range(0, didx4.Length / 12).Count(i => BitConverter.ToUInt32(didx4, 12 * i) == 9005) == 1 && b4.Media(9217)!.SequenceEqual(wemL), "shared media embedded once");
        Check(bank.LayersAdded == 1 && b4.Chain(9201)?.Sound.Id == 9202 && b4.Write().SequenceEqual(b4Bytes), "layered bank round trip");
        Check(bank.WriteOwnBank(778) is { } own4 && WwiseBank.Read(own4).Plays(9201).Count == 2 && WwiseBank.Read(own4).Media(9005) is not null, "own bank keeps the layers");

        // Properties and random ranges written into a node's bundle the way the game's banks hold them (ascending
        // IDs, values, then the ranged ones), read back from the bytes.
        var rscObj = bank.Get(R)!;
        var rscNode = WwiseNode.Read(rscObj)!;
        Check(rscNode.Ranges.Count == 1 && rscNode.Ranges[2] == (-100f, 100f), "node reads its random ranges");
        var retuned = rscNode.WithProps(rscObj.Data, [(WwiseNode.PropLoop, 0u), (WwiseNode.PropVolume, WwiseNode.Bits(-17f))], [(WwiseNode.PropVolume, -3f, 0f)]);
        var retunedNode = WwiseNode.Read(new WwiseBank.HircObject(5, R, retuned));
        Check(retuned.AsSpan(rscNode.PropsAt, 6).SequenceEqual(new byte[] { 5, 0x00, 0x02, 0x03, 0x04, 0x3A })
              && retuned.AsSpan(rscNode.PropsAt + 6 + 20, 3).SequenceEqual(new byte[] { 2, 0x00, 0x02 }), "props and ranges written in ascending ID order");
        Check(retunedNode is not null && retunedNode.Props[0] == WwiseNode.Bits(-17f) && retunedNode.Props[0x3A] == 0 && retunedNode.Props[2] == WwiseNode.Bits(-2200f)
              && retunedNode.Ranges[0] == (-3f, 0f) && retunedNode.Ranges[2] == (-100f, 100f) && retunedNode.Children.SequenceEqual([S]) && retunedNode.Playlist.Count == 1,
            "node with new props and ranges reads back, the rest untouched");
        Check(rscNode.WithProps(rscObj.Data, []).SequenceEqual(rscObj.Data), "no new props: the same bytes");

        // A DS3 sound that loops, 9 dB down with random volume and pitch: the level, pitch and ranges go on its
        // container (relative to the template's -14 dB), the Loop property on its Sounds, its sample's loop points in
        // the wem, and it gets a Stop event with one Stop action on what the Play action plays, DS3's fade-out in it.
        var stopTemplate = bank.StopTemplate(SE);
        Check(stopTemplate?.Id == SA && bank.StopTemplate(E) is null, "the template's Stop action is found");
        var wemLoop = WwiseBank.PcmWem(new short[100], 1, 44100, (10, 89));
        bank.AddSound(chain, 9301, [new WwiseBank.Layer(9303, 9304, [(9302u, 9305u, wemLoop), (9306u, 9307u, wemL)])
        {
            Tuning = new WwiseBank.Tuning(-9f, -100f, -5f, 150f, Loop: true), StopActionId = 9308,
        }], false, new WwiseBank.StopEvent(9310, 1500, stopTemplate));
        var b5Bytes = bank.Write();
        var b5 = WwiseBank.Read(b5Bytes);
        var loopCntr = WwiseNode.Read(b5.Get(9304)!)!;
        Check(loopCntr.Props[0] == WwiseNode.Bits(-23f) && loopCntr.Props[2] == WwiseNode.Bits(-100f) && loopCntr.Ranges[0] == (-5f, 0f) && loopCntr.Ranges[2] == (-150f, 150f)
              && !loopCntr.Props.ContainsKey(0x3A), "looping layer: DS3's level, pitch and ranges on the container");
        Check(new uint[] { 9302, 9306 }.All(s => WwiseNode.Read(b5.Get(s)!) is { } n && n.Props.TryGetValue(0x3A, out var loop) && loop == 0 && n.Props[0] == WwiseNode.Bits(-2f)),
            "looping layer: Loop property (0 = until stopped) on every Sound, their own volume kept");
        Check(b5.Get(9310)?.Data.SequenceEqual(Cat(U32(9310), [1], U32(9308))) == true, "Stop event lists its Stop action");
        Check(b5.Get(9308)?.Data.SequenceEqual(Cat(U32(9308), U16(0x0103), U32(9304), [0], [1], [0x10], U32(1500), [0], [5, 6, 0])) == true,
            "Stop action: type 0x0103, the Play action's target, DS3's fade-out, the template's tail, no delay");
        Check(b5.Plays(9301) is [var loopPlay] && BitConverter.ToUInt32(loopPlay.Action.Data, 6) == 9304 && b5.Plays(9310).Count == 0, "Play event still plays; the Stop event plays nothing");
        var ids5 = b5.Objects.Select(o => o.Id).ToList();
        Check(ids5.IndexOf(9303) < ids5.IndexOf(9301) && ids5.IndexOf(9301) < ids5.IndexOf(9308) && ids5.IndexOf(9308) < ids5.IndexOf(9310), "order: Play action, Play event, Stop action, Stop event");
        Check(bank.StopEventsAdded == 1 && bank.TuningsLost == 0 && b5.Media(9305)!.SequenceEqual(wemLoop) && b5.Write().SequenceEqual(b5Bytes), "looping bank round trip");
        Check(bank.WriteOwnBank(779) is { } own5 && WwiseBank.Read(own5) is var o5 && o5.Get(9310) is not null && o5.Get(9308) is not null
              && WwiseNode.Read(o5.Get(9302)!)!.Props.ContainsKey(0x3A) && o5.Write().SequenceEqual(own5), "own bank keeps the loop and the Stop event");
        // Two layers, no fade-out, no template: one Stop action per layer in the game's commonest layout; a one-shot
        // sound without tuning keeps the template's settings.
        bank.AddSound(chain, 9401, [new WwiseBank.Layer(9403, 9404, [(9402u, 9005u, wem)]) { StopActionId = 9408 },
            new WwiseBank.Layer(9413, 9414, [(9412u, 9005u, wem)]) { StopActionId = 9418, Tuning = new WwiseBank.Tuning(VolumeDb: -200f, PitchCents: 5000f) }], false, new WwiseBank.StopEvent(9410, 0, null));
        var b6 = WwiseBank.Read(bank.Write());
        Check(b6.Get(9410)?.Data.SequenceEqual(Cat(U32(9410), [2], U32(9408), U32(9418))) == true
              && b6.Get(9408)!.Data.SequenceEqual(Cat(U32(9408), U16(0x0103), U32(9404), [0], [0], [0], [4, 6, 0]))
              && b6.Get(9418)!.Data.SequenceEqual(Cat(U32(9418), U16(0x0103), U32(9414), [0], [0], [0], [4, 6, 0])), "two layers: one Stop action each, no fade, default tail");
        var plain = WwiseNode.Read(b6.Get(9404)!)!; var limited = WwiseNode.Read(b6.Get(9414)!)!;
        Check(plain.Props[0] == WwiseNode.Bits(-14f) && !plain.Props.ContainsKey(2) && plain.Ranges.Count == 0 && !WwiseNode.Read(b6.Get(9402)!)!.Props.ContainsKey(0x3A), "untuned one-shot layer as before");
        Check(limited.Props[0] == WwiseNode.Bits(-96f) && limited.Props[2] == WwiseNode.Bits(2400f), "level and pitch kept inside Wwise's limits");
        // Loop points in a PCM wem: a "smpl" chunk with one loop between fmt and data; bad points leave it out.
        Check(Encoding.ASCII.GetString(wemLoop, 44, 4) == "smpl" && BitConverter.ToInt32(wemLoop, 48) == 60 && BitConverter.ToInt32(wemLoop, 52 + 28) == 1
              && BitConverter.ToInt32(wemLoop, 52 + 36 + 8) == 10 && BitConverter.ToInt32(wemLoop, 52 + 36 + 12) == 89
              && Encoding.ASCII.GetString(wemLoop, 112, 4) == "data" && BitConverter.ToInt32(wemLoop, 4) == wemLoop.Length - 8 && wemLoop.Length == wem.Length + 68, "wem loop points (smpl chunk)");
        Check(BitConverter.ToInt32(WwiseBank.PcmWem(new short[100], 1, 44100, (10, 500)), 52 + 36 + 12) == 99, "wem loop end kept inside the audio");
        Check(WwiseBank.PcmWem(new short[100], 1, 44100, (50, 50)).SequenceEqual(wem) && WwiseBank.PcmWem(new short[100], 1, 44100, (200, 300)).SequenceEqual(wem)
              && WwiseBank.PcmWem(new short[100], 1, 44100, (-1, 5)).SequenceEqual(wem), "wem without usable loop points is the plain wem");

        // DS3 event files (Fev): a file built here from the layout the reader documents. Two sound definitions (the
        // first with two wave files, the second with a "don't play" entry and one file), an event with two layers (the
        // first with an envelope), an event whose only layer has no sound, and a record whose layer doesn't read.
        static byte[] FevStr(string s) => Cat(U32((uint)s.Length + 1), Encoding.ASCII.GetBytes(s), [0]);
        static byte[] FevEvent(uint name, Action<byte[]>? header, params byte[][] layers)
        {
            var head = new byte[0xA8];
            BitConverter.GetBytes(8).CopyTo(head, 0);
            BitConverter.GetBytes(name).CopyTo(head, 4);
            BitConverter.GetBytes(1f).CopyTo(head, 24);
            BitConverter.GetBytes(128).CopyTo(head, 40);
            BitConverter.GetBytes(0x40080000u).CopyTo(head, 72); // DS3's commonest flags: the one-shot bit set
            header?.Invoke(head);
            return Cat(head, U32((uint)layers.Length), Cat(layers),
                U32(1), new byte[28], U32(0),                       // one parameter, no values after it
                U32(2), FevStr("playDistance"), U32(1), F(29f), FevStr("returnType"), U32(0), U32(1), // user properties
                U32(1), FevStr("SE"));                              // category
        }
        // A sound in a layer: the definition's index, then i32 fields of which the fourth is the loop mode.
        static byte[] FevSound(ushort definition, uint loopMode = 1) => Cat(U16(definition), new byte[12], U32(loopMode), new byte[40]);
        // A definition property record: volume +16, lowest random volume +32, pitch +36, pitch randomisation +52.
        static byte[] FevProps(float volume, float volumeRandomMin, float pitch, float pitchRandom)
        {
            var record = new byte[74];
            F(volume).CopyTo(record, 16); F(volumeRandomMin).CopyTo(record, 32); F(pitch).CopyTo(record, 36); F(pitchRandom).CopyTo(record, 52);
            return record;
        }
        // The first event's header: -6 dB, a semitone down, a semitone of random pitch, random volume down to -3 dB,
        // heard up to 27 m, not one-shot, fading out over 1.5 s.
        static void FevLoopHeader(byte[] head)
        {
            F(0.5012f).CopyTo(head, 24); F(-1 / 48f).CopyTo(head, 28); F(1 / 48f).CopyTo(head, 32); F(0.2921f).CopyTo(head, 36);
            F(27f).CopyTo(head, 60); U32(0x40000000).CopyTo(head, 72); U32(1500).CopyTo(head, 144);
        }
        string[] fevStrings = ["", "main", "c000000001", "/x/one", "/x/two", "c000000002", "c000000003"];
        var fevText = Cat(fevStrings.Select(s => Cat(Encoding.ASCII.GetBytes(s), [0])).ToArray());
        var fevOffsets = fevStrings.Select((_, i) => (uint)fevStrings.Take(i).Sum(s => s.Length + 1)).ToArray();
        var strr = Cat(U32((uint)fevStrings.Length), Cat(fevOffsets.Select(U32).ToArray()), fevText);
        var envelope = Cat(U32(0xFFFFFFFF), FevStr("FMOD Lowpass Simple"), U32(0), U32(4), U32(0), U32(3), U32(0), U32(1), U32(2), U32(0), U32(1));
        var lgcy = Cat(
            new byte[20],                                           // what comes before the first event
            FevEvent(2, FevLoopHeader, Cat(U16(2), U16(0xFFFF), U16(0), U16(1), U16(1), FevSound(0, loopMode: 0), envelope), Cat(U16(2), U16(0xFFFF), U16(0), U16(1), U16(0), FevSound(1))),
            FevEvent(5, null, Cat(U16(0), U16(0xFFFF), U16(0), U16(0), U16(0))),
            FevEvent(6, null, Cat(U16(7), U16(0xFFFF), U16(0), U16(1), U16(0), FevSound(0))), // not a layer: skipped
            U32(2), FevProps(0.7079f, 0.7943f, 0, 1 / 96f), FevProps(1, 1, 2 / 48f, 0), // the definitions' property records
            U32(2),
            U32(3), U32(0), U32(2), U32(0), U32(100), FevStr("bank/x/a.wav"), U32(0), U32(0), U32(100), U32(0), U32(100), FevStr("sfx\\b.wav"), U32(0), U32(1), U32(200),
            U32(4), U32(1), U32(2), U32(2), U32(100), U32(0), U32(100), FevStr("c.wav"), U32(0), U32(2), U32(300),
            new byte[12]);
        static byte[] Riff(string tag, byte[] body) => Cat(Sec(tag, body), new byte[body.Length & 1]); // chunks are padded to even sizes
        byte[] FevFile(params byte[][] chunks) =>
            Sec("RIFF", Cat(Encoding.ASCII.GetBytes("FEV "), Riff("FMT ", U32(0x00450000)), Sec("LIST", Cat(Encoding.ASCII.GetBytes("PROJ"), Cat(chunks)))));
        var fevBytes = FevFile(Riff("LGCY", lgcy), Riff("STRR", strr));
        var fev = Fev.Read(fevBytes);
        Check(fev.Definitions == 2 && fev.Events.Count == 2 && fev.Skipped == 1, $"fev reads: {fev.Definitions} definitions, {fev.Events.Count} events, {fev.Skipped} skipped");
        Check(fev.Events.TryGetValue("c000000001", out var fe) && fe.Layers.Count == 2
              && fe.Layers[0].Waves.SequenceEqual(["bank/x/a.wav", "sfx\\b.wav"]) && fe.Layers[1].Waves.SequenceEqual(["c.wav"]), "fev event layers and their waves");
        Check(fev.Events.TryGetValue("c000000002", out var fe2) && fe2.Layers.Count == 1 && fe2.Layers[0].Waves.Count == 0, "fev layer without a sound");
        Check(!fev.Events.ContainsKey("c000000003"), "fev record that doesn't read is left out");
        // Levels, pitch and loops: the event header's and the definitions' property records.
        static bool Near(float a, float b, float within = 0.05f) => Math.Abs(a - b) <= within;
        Check(fe is not null && Near(Fev.Decibels(fe.Volume), -6) && Near(Fev.Cents(fe.Pitch), -100) && Near(Fev.Cents(fe.PitchRandom), 100)
              && Near(Fev.Decibels(fe.VolumeRandomMin), -3) && fe.MaxDistance == 27 && !fe.OneShot && fe.FadeOutMs == 1500, "fev event level, pitch, randomisation, distance, one-shot bit and fade-out");
        Check(fe is not null && fe.Layers[0] is { LoopMode: 0, Loops: true } l0 && Near(Fev.Decibels(l0.Volume), -3) && Near(Fev.Decibels(l0.VolumeRandomMin), -2)
              && l0.Pitch == 0 && Near(Fev.Cents(l0.PitchRandom), 50), "fev looping layer and its definition's level");
        Check(fe is not null && fe.Layers[1] is { LoopMode: 1, Loops: false, Volume: 1, VolumeRandomMin: 1, PitchRandom: 0 } l1 && Near(Fev.Cents(l1.Pitch), 200), "fev one-shot layer and its definition's pitch");
        Check(fe2 is { Volume: 1, Pitch: 0, PitchRandom: 0, VolumeRandomMin: 1, OneShot: true, FadeOutMs: 0 }, "fev plain event reads as unchanged");
        Check(fe is not null && SoundPort.TuningOf(fe, fe.Layers[0]) is { Loop: true } t0 && Near(t0.VolumeDb, -9) && Near(t0.PitchCents, -100) && Near(t0.VolumeRandomDb, -5) && Near(t0.PitchRandomCents, 150)
              && SoundPort.TuningOf(fe, fe.Layers[1]) is { Loop: false } t1 && Near(t1.VolumeDb, -6) && Near(t1.PitchCents, 100) && Near(t1.VolumeRandomDb, -3) && Near(t1.PitchRandomCents, 100),
            "event and definition add up to a layer's tuning (dB, cents)");
        Check(fe2 is not null && SoundPort.TuningOf(fe2, new Fev.Layer([])) is null, "a plain layer has no tuning");
        Check(Fev.Decibels(0) == -96 && Fev.Decibels(float.NaN) == -96 && Fev.Decibels(1) == 0 && Fev.Cents(float.NaN) == 0, "fev dB and cents of odd values");
        Check(Fev.SampleName("bank/fdp_main/sfx/s000525503.wav") == "s000525503" && Fev.SampleName("sfx\\b.wav") == "b" && Fev.SampleName("magic-atom") == "magic-atom", "fev wave names to sample names");
        bool fevThrew = false;
        try
        {
            for (int cut = 0; cut < fevBytes.Length; cut += 7)
                Fev.Read(fevBytes[..cut]);
            Check(Fev.Read(new byte[64]).Events.Count == 0 && Fev.Read(FevFile(Riff("LGCY", lgcy))).Events.Count == 0, "fev: not a FEV, or no string table, gives no events");
        }
        catch (Exception) { fevThrew = true; }
        Check(!fevThrew, "fev reader doesn't throw on cut files");
        var encrypted = new byte[0x40];
        for (int i = 0; i < 12; i++)
        {
            int b = new byte[] { (byte)'F', (byte)'S', (byte)'B', (byte)'5', 1, 0, 0, 0, 77, 0, 0, 0 }[i] ^ "FDPrVuT4fAFvdHJYAgyMzRF4EcBAnKg"[i];
            b = ((b & 0xF0) >> 4) | ((b & 0x0F) << 4); b = ((b & 0xCC) >> 2) | ((b & 0x33) << 2); b = ((b & 0xAA) >> 1) | ((b & 0x55) << 1);
            encrypted[i] = (byte)b;
        }
        Check(Fsb5.EncryptedSampleCount(encrypted) == 77 && Fsb5.EncryptedSampleCount(new byte[0x40]) == 0 && !Fsb5.Is(encrypted), "encrypted fsb5 sample count");

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
