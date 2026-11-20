using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> TalkChecks()
    {
        yield return ("talk: script expressions read and write back byte for byte", TalkExpressions);
        yield return ("talk: a DS3 talk script's flags, lines and shops move (through state group arguments too), commands and functions Elden Ring lacks go", TalkConvert);
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
