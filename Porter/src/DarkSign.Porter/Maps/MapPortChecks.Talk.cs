using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> TalkChecks()
    {
        yield return ("talk: script expressions read and write back byte for byte", TalkExpressions);
        yield return ("talk: a DS3 talk script's flags, lines and shops move (through state group arguments too), commands and functions Elden Ring lacks go", TalkConvert);
        yield return ("talk: a talk pose DS3's people animations cast reads as on; any other SpEffect asked of the speaker moves to the ported effects", TalkPoses);
        yield return ("talk: a named line brings the rows of its speech that follow it (12000000 -> 12000001...), up to the first gap", TalkChains);
        yield return ("talk: each converted script has a prefix (voice bank vc900-vc998) of its own, never Elden Ring's vc999; speech is halved to 22 kHz", TalkVoices);
    }

    private static void TalkVoices()
    {
        var talk = new TalkPort();
        Expect(talk.IdFor(400120) == 900_400_120 && talk.IdFor(300330) == 901_300_330 && talk.IdFor(400120) == 900_400_120,
            "scripts get prefixes 900, 901... in the order they are first placed, and keep them");
        for (int i = 2; i < 120; i++)
            talk.IdFor(500000 + i);
        Expect(Enumerable.Range(2, 118).All(i => talk.IdFor(500000 + i) / 1_000_000 is >= 900 and <= 998), "past 99 scripts the prefixes are shared, 999 never used");
        Expect(TalkPort.IsOurs(talk.IdFor(400120)), "converted scripts are still recognised as the port's");
        var (half, rate) = CutsceneSound.Halve([0, 100, 200, 300, -100, -300], 1, 44100);
        Expect(rate == 22050 && half.SequenceEqual(new short[] { 50, 250, -200 }), "44.1 kHz speech is averaged down to 22.05 kHz");
        var (same, keep) = CutsceneSound.Halve([1, 2], 1, 22050);
        Expect(keep == 22050 && same.Length == 2, "lower rates are kept");
    }

    private static void TalkPoses()
    {
        // The Fire Keeper's level-up handshake: (f60(150) == 1 || f60(152) == 1) -> ask; and Friede's f60(13133) == 1.
        static TalkPort.Node Has(int effect) =>
            new TalkPort.Op(0x95, [new TalkPort.Call(new TalkPort.Lit(TalkPort.SelfSpEffect), [new TalkPort.Lit(effect)]), new TalkPort.Lit(1)]);
        var pose = TalkPort.Emit([new TalkPort.Op(0xA1, [new TalkPort.Op(0x99, [Has(150), Has(152)])])]);
        var boss = TalkPort.Emit([new TalkPort.Op(0xA1, [Has(13133)])]);
        var esd = new ESD(false, 1) { StateGroups = new() { [0x7FFFFFE6] = new() { [0] = new ESD.State(), [1] = new ESD.State() } } };
        esd.StateGroups[0x7FFFFFE6][0].Conditions.Add(new ESD.Condition(1, pose));
        esd.StateGroups[0x7FFFFFE6][1].Conditions.Add(new ESD.Condition(0, boss));
        var er = new ESD(true, 3) { StateGroups = new() { [1] = new() { [0] = new ESD.State() } } };
        er.StateGroups[1][0].Conditions.Add(new ESD.Condition(0, TalkPort.Emit([new TalkPort.Op(0xA1, [Has(9626)])])));
        var vocabulary = new TalkPort.Vocabulary();
        vocabulary.Add(er);
        var stats = TalkPort.Convert(esd, vocabulary, f => f, _ => true, new TalkPort.Needs(), null, id => id is 150 or 151 or 152 or 159,
            id => Enemies.ChrEffects.SpEffectId((int)id));
        var poseOut = TalkPort.Parse(esd.StateGroups[0x7FFFFFE6][0].Conditions[0].Evaluator).SelectMany(Walk).ToList();
        var bossOut = TalkPort.Parse(esd.StateGroups[0x7FFFFFE6][1].Conditions[0].Evaluator).SelectMany(Walk).ToList();
        Expect(stats.PoseEffects == 2 && !poseOut.OfType<TalkPort.Call>().Any(), "the talk poses 150/152 read as on (no SpEffect call left)");
        // The handshake's answer flags: "both off" before the request, "both on" after it; each passes (state 11 and
        // group 0x7FFFFFE6 state 2 of t400120). The request flag itself stays a real read.
        static TalkPort.Node Flag(long flag, int value) =>
            new TalkPort.Op(0x95, [new TalkPort.Op(0xA6, [new TalkPort.Call(new TalkPort.Lit(15), [new TalkPort.Lit(flag)])]), new TalkPort.Lit(value)]);
        var shake = new ESD(false, 1) { StateGroups = new() { [0x7FFFFFF7] = new() { [11] = new ESD.State(), [2] = new ESD.State(), [3] = new ESD.State() } } };
        shake.StateGroups[0x7FFFFFF7][11].Conditions.Add(new ESD.Condition(19, TalkPort.Emit([new TalkPort.Op(0xA1, [new TalkPort.Op(0x98, [Flag(74000137, 0), Flag(74000138, 0)])])])));
        shake.StateGroups[0x7FFFFFF7][2].Conditions.Add(new ESD.Condition(1, TalkPort.Emit([new TalkPort.Op(0xA1, [new TalkPort.Op(0x98, [Flag(74000137, 1), Flag(74000138, 1)])])])));
        shake.StateGroups[0x7FFFFFF7][3].Conditions.Add(new ESD.Condition(6, TalkPort.Emit([new TalkPort.Op(0xA1, [Flag(74000135, 0)])])));
        var flagVocabulary = new TalkPort.Vocabulary();
        var erFlags = new ESD(true, 3) { StateGroups = new() { [1] = new() { [0] = new ESD.State() } } };
        erFlags.StateGroups[1][0].Conditions.Add(new ESD.Condition(0, TalkPort.Emit([new TalkPort.Op(0xA1, [Flag(4651, 0)])])));
        flagVocabulary.Add(erFlags);
        var shakeStats = TalkPort.Convert(shake, flagVocabulary, f => f + 1_000_000_000, _ => true, new TalkPort.Needs(), f => f is 74000137 or 74000138);
        List<TalkPort.Node> Out(long state) => TalkPort.Parse(shake.StateGroups[0x7FFFFFF7][state].Conditions[0].Evaluator).SelectMany(Walk).ToList();
        Expect(shakeStats.EngineFlags == 4 && !Out(11).OfType<TalkPort.Call>().Any() && !Out(2).OfType<TalkPort.Call>().Any(),
            $"the handshake's answer flags pass asked off and asked on ({shakeStats.EngineFlags} checks)");
        Expect(Out(11).OfType<TalkPort.Op>().Where(o => o.Code == 0x98).All(o => o.Kids.All(k => k is TalkPort.Lit { Value: 1 })) && Out(2).OfType<TalkPort.Op>().Where(o => o.Code == 0x98).All(o => o.Kids.All(k => k is TalkPort.Lit { Value: 1 })),
            "both waits read 1 and 1");
        Expect(Out(3).OfType<TalkPort.Call>().Any(c => c.Args is [TalkPort.Lit { Value: 1_074_000_135 }]), "the request flag is still read, at its moved number");
        Expect(stats.EffectsMoved == 1 && bossOut.OfType<TalkPort.Call>().Any(c => c.Args is [TalkPort.Lit { Value: 700_013_133 }]),
            "a DS3 character effect asked of the speaker is its ported number 700,013,133");
    }

    private static IEnumerable<TalkPort.Node> Walk(TalkPort.Node n)
    {
        yield return n;
        var kids = n switch { TalkPort.Call c => c.Args.Prepend(c.Func), TalkPort.Op p => p.Kids, _ => Enumerable.Empty<TalkPort.Node>() };
        foreach (var k in kids)
            foreach (var d in Walk(k))
                yield return d;
    }

    private static void TalkChains()
    {
        // Greirat's first speech is 12000000-12000011, his next 12000100-12000103; a script names 12000000 and 12000100.
        var ds3Rows = Enumerable.Range(12000000, 12).Concat(Enumerable.Range(12000100, 4)).Append(12000200).ToHashSet();
        var rows = TalkPort.Chains([12000100, 12000000], ds3Rows.Contains);
        Expect(rows.Count == 16 && rows[12000011] == 12000000 && rows[12000103] == 12000100 && !rows.ContainsKey(12000200),
            "speeches: " + string.Join(" ", rows.Keys));
        Expect(TalkPort.Chains([5], _ => true, longest: 3).Keys.SequenceEqual([5, 6, 7, 8]), "a speech stops after the longest allowed run");
        var shared = TalkPort.Chains([10, 12], id => id is >= 10 and <= 14);
        Expect(shared[12] == 12 && shared[11] == 10 && shared[14] == 12, "a row a script names itself starts its own speech");
    }

    private static byte[] TalkInt(int v) => EsdExpr.WriteInt(v);
    private static byte[] TalkArg(int n) => [(byte)(n + 64), 0xB8, 0xA1];

    private static void TalkExpressions()
    {
        // f15(1116) == 1 && f31() == 0, a float, a register and a state group argument
        byte[][] samples =
        [
            [0x4F, 0x82, 0x5C, 0x04, 0x00, 0x00, 0x85, 0x41, 0x95, 0x5F, 0x84, 0x40, 0x95, 0x98, 0xA1],
            [0x80, 0x00, 0x00, 0x80, 0x3F, 0xA7, 0xAF, 0x93, 0xA1],
            TalkArg(3), TalkInt(-1), TalkInt(74000802), [0xB9, 0xBA, 0x96, 0xA1],
        ];
        foreach (var s in samples)
            Expect(TalkPort.Emit(TalkPort.Parse(s)).SequenceEqual(s), $"expression {System.Convert.ToHexString(s)} survives");
        bool threw = false;
        try { TalkPort.Parse([0xFE, 0xA1]); } catch (InvalidDataException) { threw = true; }
        Expect(threw, "an unknown byte is refused, not guessed");
    }

    private static void TalkConvert()
    {
        const int group = 2147483646;
        // Elden Ring's side: say a line with four arguments, set a flag, open a shop, read a flag.
        var er = new ESD(true, 3) { StateGroups = new() { [1] = new() { [0] = new ESD.State() } } };
        var erState = er.StateGroups[1][0];
        erState.EntryCommands.Add(new ESD.CommandCall(1, 1, TalkInt(100), TalkInt(-1), TalkInt(-1), TalkInt(7)));
        erState.EntryCommands.Add(new ESD.CommandCall(1, 11, TalkInt(5), TalkInt(1)));
        erState.EntryCommands.Add(new ESD.CommandCall(1, 22, TalkInt(1), TalkInt(2)));
        erState.Conditions.Add(new ESD.Condition(0, [0x4F, 0x45, 0x85, 0x41, 0x95, 0xA1]));
        var vocabulary = new TalkPort.Vocabulary();
        vocabulary.Add(er);

        var ds3 = new ESD(true, 3) { StateGroups = new() { [1] = new() { [0] = new ESD.State() }, [group] = new() { [0] = new ESD.State() } } };
        var top = ds3.StateGroups[1][0];
        top.EntryCommands.Add(new ESD.CommandCall(6, group, TalkInt(17000410), TalkInt(74000802)));
        top.EntryCommands.Add(new ESD.CommandCall(1, 1, TalkInt(17000300), TalkInt(-1), TalkInt(-1)));
        top.EntryCommands.Add(new ESD.CommandCall(1, 99));
        top.EntryCommands.Add(new ESD.CommandCall(1, 104, TalkInt(60310)));
        top.EntryCommands.Add(new ESD.CommandCall(1, 22, TalkInt(40000), TalkInt(59999)));
        top.Conditions.Add(new ESD.Condition(0, [0x4F, 0x82, 0x5C, 0x04, 0x00, 0x00, 0x85, 0x41, 0x95, 0x5F, 0x84, 0x40, 0x95, 0x98, 0xA1]));
        var inner = ds3.StateGroups[group][0];
        inner.EntryCommands.Add(new ESD.CommandCall(1, 1, TalkArg(0), TalkInt(-1), TalkInt(-1), TalkInt(0)));
        inner.EntryCommands.Add(new ESD.CommandCall(1, 11, TalkArg(1), TalkInt(1)));

        var needs = new TalkPort.Needs();
        var stats = TalkPort.Convert(ds3, vocabulary, f => f + 1_000_000_000, _ => true, needs);
        var back = ESD.Read(ds3.Write());
        var commands = back.StateGroups[1][0].EntryCommands;
        Expect(commands.Count == 3 && stats.CommandsLeftOut == 2, "the command Elden Ring lacks and the item gift are left out");
        Expect(EsdExpr.ReadInt(commands[0].Arguments[0]) == TalkPort.TextBase + 17000410, "a line handed to a state group moves with the group's argument");
        Expect(EsdExpr.ReadInt(commands[0].Arguments[1]) == 1_074_000_802, "a flag handed to a state group moves");
        Expect(commands[1].Arguments.Count == 4 && EsdExpr.ReadInt(commands[1].Arguments[0]) == TalkPort.TextBase + 17000300
               && EsdExpr.ReadInt(commands[1].Arguments[3]) == 7, "a line with DS3's three arguments gets Elden Ring's fourth");
        Expect(EsdExpr.ReadInt(commands[2].Arguments[0]) == TalkPort.ShopBase + 40000 && EsdExpr.ReadInt(commands[2].Arguments[1]) == TalkPort.ShopBase + 59999
               && needs.Shops.Contains((40000, 59999)), "the shop range moves and is asked for");
        Expect(needs.Lines.SetEquals([17000300, 17000410]), "both lines are asked for");
        var condition = TalkPort.Parse(back.StateGroups[1][0].Conditions[0].Evaluator);
        var calls = condition.SelectMany(n => Calls(n)).ToList();
        Expect(calls.Count == 1 && calls[0].Args[0] is TalkPort.Lit { Value: 1_000_001_116 } && stats.FunctionsZeroed == 1,
            "the flag a condition reads moves; the function Elden Ring lacks reads as 0");
        Expect(EsdExpr.ReadInt(back.StateGroups[group][0].EntryCommands[1].Arguments[1]) == 1, "the state group itself is untouched");

        static IEnumerable<TalkPort.Call> Calls(TalkPort.Node n) => n switch
        {
            TalkPort.Call c => c.Args.SelectMany(Calls).Prepend(c),
            TalkPort.Op p => p.Kids.SelectMany(Calls),
            _ => [],
        };
    }
}
