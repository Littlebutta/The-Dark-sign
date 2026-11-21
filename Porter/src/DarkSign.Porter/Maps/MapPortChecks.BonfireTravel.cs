using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> BonfireTravelChecks()
    {
        yield return ("bonfire travel: DS3's order (tabs, then rows), areas grouped, request flags and events per bonfire", BonfireTravelOrder);
        yield return ("bonfire travel: the grace menu gets Travel and Return, and lists that only show lit bonfires", BonfireTravelMenu);
        yield return ("bonfire travel: DS3's bonfire pictures (names ending in the PictureId, halved to fit, DKIM files) and the DLL's list", BonfireTravelPictures);
        yield return ("bonfires: DS3's bonfire fire comes over as the sword's own animation events (DS3's 16 bytes -> Elden Ring's 24)", BonfireFireEvents);
        yield return ("talk: a flag only DS3's character sets (the Fire Keeper's level-up handshake) reads as on; one-time signals and quest flags don't", TalkEngineFlags);
    }

    private static List<BonfireTravel.Destination> TravelSample()
    {
        MapId hw = MapId.Parse("m45_01_00_00"), fl = MapId.Parse("m45_00_00_00");
        return
        [
            new(hw, 5, "Knight's Rest", "High Wall of Lothric", 1, 8, 45010005, 45010985),
            new(fl, 0, "Firelink Shrine", "Cemetery of Ash", 0, 1, 45000000, 45000980),
            new(hw, 9, "High Wall of Lothric", "High Wall of Lothric", 1, 6, 45010009, 45010989),
            new(fl, 3, "Somewhere", "Odd", 99, 0, 45000003, 45000983),
            new(fl, 1, "Cemetery of Ash", "Cemetery of Ash", 0, 2, 45000001, 45000981),
        ];
    }

    private static void BonfireTravelOrder()
    {
        var sorted = BonfireTravel.Sort(TravelSample());
        Expect(sorted.Select(d => d.Name).SequenceEqual(["Firelink Shrine", "Cemetery of Ash", "High Wall of Lothric", "Knight's Rest", "Somewhere"])
               && sorted.Select(d => d.Index).SequenceEqual([0, 1, 2, 3, 4]), "order: " + string.Join(", ", sorted.Select(d => d.Name)));
        var areas = BonfireTravel.Areas(sorted);
        Expect(areas.Select(a => a.Area).SequenceEqual(["Cemetery of Ash", "High Wall of Lothric", "Odd"]) && areas[1].Bonfires.Count == 2, "areas");
        var common = new EMEVD();
        common.Events.Add(new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        BonfireTravel.AddEvents(common, sorted);
        BonfireTravel.AddEvents(common, sorted);   // a rerun replaces, not adds
        common = EMEVD.Read(common.Write());
        var travel = common.Events.Where(e => BonfireTravel.IsTravelEvent(e.ID)).ToList();
        Expect(travel.Count == 5 && common.Events[0].Instructions.Count == 5, $"{travel.Count} events, {common.Events[0].Instructions.Count} starts");
        var kr = travel.Single(e => e.ID == BonfireTravel.EventId(3)).Instructions;
        Expect(kr[0] is { Bank: 1003, ID: 0 } && BitConverter.ToUInt32(kr[0].ArgData, 4) == BonfireTravel.RequestFlag(3)
               && kr[2] is { Bank: 2003, ID: 66 } && BitConverter.ToUInt32(kr[2].ArgData, 4) == BonfireTravel.PreviewFlag(3)
               && kr[4] is { Bank: 2003, ID: 14 } && kr[4].ArgData[0] == 45 && kr[4].ArgData[1] == 1 && BitConverter.ToUInt32(kr[4].ArgData, 4) == 45010985,
            "Knight's Rest's event waits for its flag, turns its picture off and warps to its standing point");
        Expect(BonfireTravel.PreviewFlag(0) == 1039292000 && BonfireTravel.PreviewFlag(BonfireTravel.MaxDestinations - 1) < RunEvents.StartDs3Request,
            "preview flags in the reset-on-load range, clear of the run requests (2100+)");
        Expect(BonfireTravel.RequestFlag(0) == 1039292110 && BonfireTravel.RequestFlag(BonfireTravel.MaxDestinations - 1) < RunEvents.WarpRequestFlag(0)
               && BonfireTravel.EventId(0) == 1039291110 && BonfireTravel.EventId(BonfireTravel.MaxDestinations - 1) < RunEvents.WarpEventId(0),
            "flags in the reset-on-load range, events clear of the per-map warps");
        Expect(BonfireTravel.RequestFlag(BonfireTravel.MaxDestinations - 1) < RunEvents.WarpRequestFlag(0), "request flags clear of the per-map warps");
    }

    private static byte[] Num(long v) => TalkPort.Emit([new TalkPort.Op(0xA1, [new TalkPort.Lit(v)])]);

    /// <summary>The grace menu in small: clear, list (with Leave), show and wait, branch on the pick.</summary>
    private static ESD GraceMenu()
    {
        var esd = new ESD(true, 3) { StateGroups = new() };
        var main = new Dictionary<long, ESD.State>();
        ESD.State St(params ESD.CommandCall[] entry) { var s = new ESD.State(); s.EntryCommands.AddRange(entry); return s; }
        main[0] = St(); main[0].Conditions.Add(new ESD.Condition(1, Num(1)));
        main[1] = St(new ESD.CommandCall(1, 20)); main[1].Conditions.Add(new ESD.Condition(2, Num(1)));
        main[2] = St(new ESD.CommandCall(1, 19, Num(1), Num(15000420), Num(-1)), new ESD.CommandCall(1, 19, Num(BonfireTravel.BackEntry), Num(20000009), Num(-1)));
        main[2].Conditions.Add(new ESD.Condition(3, Num(1)));
        // show: 1:10, wait until the menu closes (function 59)
        var wait = TalkPort.Emit([new TalkPort.Op(0xA1, [new TalkPort.Op(0x95, [new TalkPort.Call(new TalkPort.Lit(59), [new TalkPort.Lit(1), new TalkPort.Lit(0)]), new TalkPort.Lit(0)])])]);
        main[3] = St(new ESD.CommandCall(1, 10, Num(1))); main[3].Conditions.Add(new ESD.Condition(4, wait));
        var pick = St();
        pick.Conditions.Add(new ESD.Condition(5, TalkPort.Emit([new TalkPort.Op(0xA1, [new TalkPort.Op(0x95, [new TalkPort.Op(0xA7, [new TalkPort.Call(new TalkPort.Lit(23), [])]), new TalkPort.Lit(1)])])])));
        for (int i = 2; i <= 10; i++)
            pick.Conditions.Add(new ESD.Condition(5, TalkPort.Emit([new TalkPort.Op(0xA1, [new TalkPort.Op(0x95, [new TalkPort.Raw([0xAF]), new TalkPort.Lit(i)])])])));
        pick.Conditions.Add(new ESD.Condition(6, Num(1)));
        main[4] = pick;
        main[5] = St(); main[5].Conditions.Add(new ESD.Condition(1, Num(1)));
        main[6] = St(); var ret = new ESD.Condition { Evaluator = Num(1) }; ret.PassCommands.Add(new ESD.CommandCall(7, -1, Num(0))); main[6].Conditions.Add(ret);
        esd.StateGroups[0x7FFFFFF0] = main;
        return esd;
    }

    private static void BonfireTravelMenu()
    {
        var sorted = BonfireTravel.Sort(TravelSample());
        var areas = BonfireTravel.Areas(sorted);
        var texts = new BonfireTravel.Texts(1, 2, 3, [.. areas.Select((_, i) => 10 + i)], [.. sorted.Select(d => 100 + d.Index)], 4);
        var esd = GraceMenu();
        int added = BonfireTravel.AddTo(esd, sorted, texts, RunEvents.LeaveDs3Request);
        esd = ESD.Read(esd.Write());
        Expect(added == areas.Count + 2 && esd.StateGroups.Count == 1 + added, $"{added} lists added, {esd.StateGroups.Count} groups");
        var main = esd.StateGroups[0x7FFFFFF0];
        var list = main[2].EntryCommands.Where(c => c.CommandBank == 1 && c.CommandID == 19).Select(c => TalkPort.Parse(c.Arguments[0])).ToList();
        Expect(list.Count == 4, $"main list has {list.Count} entries");
        var entries = main[2].EntryCommands.Select(c => System.Convert.ToHexString(c.Arguments.FirstOrDefault() ?? [])).ToList();
        Expect(entries.IndexOf(System.Convert.ToHexString(Num(BonfireTravel.TravelEntry))) < entries.IndexOf(System.Convert.ToHexString(Num(BonfireTravel.BackEntry)))
               && entries.IndexOf(System.Convert.ToHexString(Num(BonfireTravel.ReturnEntry))) < entries.IndexOf(System.Convert.ToHexString(Num(BonfireTravel.BackEntry))),
            "Travel and Return come before Leave");
        Expect(main[4].Conditions.Count == 13 && main[4].Conditions[^1].TargetState == 6, "two branches added before the last one");
        var returnState = main[main[4].Conditions[^2].TargetState!.Value];
        Expect(returnState.EntryCommands.Any(c => c.CommandBank == 1 && c.CommandID == 11 && c.Arguments[0].SequenceEqual(Num(RunEvents.LeaveDs3Request))), "Return sets the leave request");
        // Every state a condition names exists, every group a state calls exists.
        foreach (var (id, g) in esd.StateGroups)
            foreach (var s in g.Values)
            {
                foreach (var c in s.Conditions.SelectMany(TalkPort.Flatten))
                    Expect(c.TargetState is null || g.ContainsKey(c.TargetState.Value), $"group {id} names state {c.TargetState} it lacks");
                foreach (var c in s.EntryCommands.Where(c => c.CommandBank == 6))
                    Expect(esd.StateGroups.ContainsKey(c.CommandID), $"group {id} calls missing group {c.CommandID}");
            }
        // An area list shows a bonfire only once it is lit, and picking it sets its request flag.
        var high = esd.StateGroups.Values.First(g => g.Values.Any(s => s.EntryCommands.Any(c => c.CommandBank == 1 && c.CommandID == 11
            && c.Arguments[0].SequenceEqual(Num(BonfireTravel.RequestFlag(3))))));
        var adds = high.Values.SelectMany(s => s.EntryCommands).Where(c => c.CommandBank == 5 && c.CommandID == 19).ToList();
        // Picking Knight's Rest turns its picture on and asks "Travel here" (the confirm group); yes turns it off and asks for the warp.
        var confirm = esd.StateGroups.Single(g => g.Value.Values.Any(s => s.EntryCommands.Any(c => c.CommandBank == 1 && c.CommandID == 19
            && c.Arguments[1].SequenceEqual(Num(4))))).Key;
        var ask = high.Values.Single(s => s.EntryCommands.Any(c => c.CommandBank == 1 && c.CommandID == 11 && c.Arguments[0].SequenceEqual(Num(BonfireTravel.PreviewFlag(3)))
            && c.Arguments[1].SequenceEqual(Num(1))));
        Expect(ask.EntryCommands.Any(c => c.CommandBank == 6 && c.CommandID == confirm) && ask.Conditions.Count == 2, "the pick shows the picture and calls Travel here");
        var go = high[ask.Conditions[0].Subconditions[0].TargetState!.Value];
        Expect(go.EntryCommands.Any(c => c.CommandBank == 1 && c.CommandID == 11 && c.Arguments[0].SequenceEqual(Num(BonfireTravel.PreviewFlag(3))) && c.Arguments[1].SequenceEqual(Num(0)))
               && go.EntryCommands.Any(c => c.CommandBank == 1 && c.CommandID == 11 && c.Arguments[0].SequenceEqual(Num(BonfireTravel.RequestFlag(3)))),
            "Travel here turns the picture off and asks for the warp");
        Expect(ask.Conditions[1].Subconditions[0].TargetState == 1, "Back rebuilds the area's list (which turns its pictures off)");
        Expect(adds.Count == 2 && TalkPort.Parse(adds[1].Arguments[0]).SelectMany(n => new[] { n }).Any()
               && System.Convert.ToHexString(adds[1].Arguments[0]).Contains(System.Convert.ToHexString(BitConverter.GetBytes(45010005))), "Knight's Rest shows once its lit flag is on");
        bool threw = false;
        try { BonfireTravel.AddTo(new ESD(true, 3) { StateGroups = new() { [1] = new() { [0] = new ESD.State() } } }, sorted, texts, 1); }
        catch (InvalidDataException) { threw = true; }
        Expect(threw, "a script laid out another way is refused (the bonfires keep the plain grace menu)");
    }

    private static void BonfireTravelPictures()
    {
        Expect(BonfireImages.PictureOf("MENU_MapImage_10011") == 10011 && BonfireImages.PictureOf("mapimage_10011.dds") == 10011
               && BonfireImages.PictureOf("MENU_Frame") is null, "picture numbers from texture names");
        var rgba = new byte[1300 * 4 * 2];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i % 4 == 3 ? 255 : 100);
        var (w, h, px) = BonfireImages.Shrink(1300, 2, rgba);
        Expect(w == 650 && h == 1 && px.Length == 650 * 4 && px[0] == 100 && px[3] == 255, $"halved to {w}x{h}");
        var back = BonfireImages.Unpack(BonfireImages.Pack(w, h, px));
        Expect(back.Width == 650 && back.Height == 1 && back.Rgba.SequenceEqual(px), "DKIM round trip");
        // A real DDS (BCnEncoder's own BC1) decodes to RGBA.
        var encoder = new BCnEncoder.Encoder.BcEncoder(BCnEncoder.Shared.CompressionFormat.Bc1);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.FileFormat = BCnEncoder.Shared.OutputFileFormat.Dds;
        var red = new byte[8 * 4 * 4];
        for (int i = 0; i < red.Length; i += 4) { red[i] = 255; red[i + 3] = 255; }
        var dds = encoder.EncodeToDds(red, 8, 4, BCnEncoder.Encoder.PixelFormat.Rgba32);
        using var ms = new MemoryStream();
        dds.Write(ms);
        var decoded = BonfireImages.Decode(ms.ToArray());
        Expect(decoded.Width == 8 && decoded.Height == 4 && decoded.Rgba[0] > 240 && decoded.Rgba[1] < 16, $"decoded {decoded.Width}x{decoded.Height} r{decoded.Rgba[0]} g{decoded.Rgba[1]}");
        var sorted = BonfireTravel.Sort(TravelSample().Select(d => d.Name == "Knight's Rest" ? d with { Picture = 10011 } : d));
        var lines = BonfireTravel.ListLines(sorted, new Dictionary<int, string> { [10011] = "darksign_bonfires/10011.dkimg" }).ToList();
        Expect(lines.Count == 6 && lines[0].StartsWith('#') && lines[4] == $"{BonfireTravel.PreviewFlag(3)}\tHigh Wall of Lothric\tKnight's Rest\tdarksign_bonfires/10011.dkimg"
               && lines[1].EndsWith('\t'), "list: " + string.Join(" | ", lines));
    }

    private static void BonfireFireEvents()
    {
        // DS3 o000100's lit animation: the lighting burst (24 bytes) and the fire on dummy poly 201 (16 bytes).
        var tae = new TAE { Format = TAE.TAEFormat.DS3, Animations = [] };
        var anim = new TAE.Animation(10, new TAE.Animation.AnimMiniHeader.Standard(), "a000_000010.hkt");
        byte[] Ints(params int[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
        anim.Events.Add(new TAE.Event(0.833f, 1.167f, 96, 0, Ints(1000, 200, -1, 257, 0, 0), false));
        anim.Events.Add(new TAE.Event(0.833f, 1.167f, 96, 0, Ints(1011, 201, 1, 256), false));
        anim.Events.Add(new TAE.Event(0.833f, 1.167f, 194, 0, Ints(327680, 0, 1065353216, 0), false));
        anim.Events.Add(new TAE.Event(0, 1, 96, 0, Ints(7777, 201, 1, 256), false));
        var (events, dropped) = ObjectAnims.EffectEvents(anim, false, new Dictionary<int, int> { [1000] = 900001000, [1011] = 900001011 });
        Expect(events.Count == 2 && dropped == 1 && events.All(e => e.Type == 96 && e.Group is not null), $"{events.Count} events, {dropped} dropped");
        var fire = events[1].GetParameterBytes(false);
        Expect(fire.Length == 24 && fire.AsSpan(0, 16).SequenceEqual(Ints(900001011, 201, 1, 256)) && fire.AsSpan(16).ToArray().All(b => b == 0),
            "the fire event in Elden Ring's 24 bytes, pointing at the converted effect");
        Expect(events[0].GetParameterBytes(false).SequenceEqual(Ints(900001000, 200, -1, 257, 0, 0)), "a 24-byte event keeps its length");
        tae.Animations.Add(anim);
        Expect(ObjectAnims.Effects(tae).SetEquals([1000, 1011, 7777]), "the effects an object's animations play");
        Expect(Bonfires.OwnEffects.Contains(Bonfires.LitEffect) && Bonfires.LitDummy == 201, "the lit fire is one of the effects port-weapons converts");
    }

    private static void TalkEngineFlags()
    {
        ESD Script(params (ESD.CommandCall[] Entry, uint Wait)[] states)
        {
            var esd = new ESD(false, 1) { StateGroups = new() };
            var g = new Dictionary<long, ESD.State>();
            for (int i = 0; i < states.Length; i++)
            {
                var s = new ESD.State();
                s.EntryCommands.AddRange(states[i].Entry);
                var read = new TalkPort.Op(0xA1, [new TalkPort.Op(0x95, [new TalkPort.Call(new TalkPort.Lit(15), [new TalkPort.Lit(states[i].Wait)]), new TalkPort.Lit(1)])]);
                s.Conditions.Add(new ESD.Condition(i, TalkPort.Emit([read])));
                g[i] = s;
            }
            esd.StateGroups[0x7FFFFFF0] = g;
            return esd;
        }
        ESD.CommandCall Set(uint flag, int state) => new(1, 11, Num(flag), Num(state));
        // The Fire Keeper: asks with 136, waits for 137 and 138; a signal 170 the script turns off itself; 200 waited
        // with no request next to it; 300 next to a request but set by an event.
        var keeper = Script(([Set(74000135, 1)], 0), ([Set(74000136, 1)], 74000137), ([], 74000138));
        var signal = Script(([Set(74000171, 1)], 74000170), ([Set(74000170, 0)], 0));
        var quest = Script(([], 74000200));
        var byEvent = Script(([Set(74000301, 1)], 74000300));
        var events = new EMEVD();
        var e = new EMEVD.Event(1, EMEVD.Event.RestBehaviorType.Default);
        e.Instructions.Add(RunEvents.Make(2003, 22, 74000135u, 74000138u, (byte)0));
        e.Instructions.Add(RunEvents.Make(2003, 22, 74000170u, 74000170u, (byte)0));
        e.Instructions.Add(RunEvents.Make(2003, 22, 74000200u, 74000200u, (byte)0));
        e.Instructions.Add(RunEvents.Make(2003, 66, (byte)0, 74000300u, (byte)1));
        events.Events.Add(e);
        var found = TalkPort.EngineFlags([keeper, signal, quest, byEvent], [events]);
        Expect(found.SetEquals([74000137, 74000138]), "engine flags: " + string.Join(" ", found.Order()));
    }
}
