using DarkSign.Porter.Maps;
using HKLib.hk2018;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// Part of enemy-selftest: the AI port (<see cref="HksChunk"/>, <see cref="AiPort"/>), the extra DS3 attacks
/// (<see cref="ChrMoves"/>), the template effect filter (<see cref="TemplateEffects"/>) and the chr map's think lines,
/// on hand-made files (no game files needed).
/// </summary>
public static class AiSelfTest
{
    private static readonly byte[] Header = [0x1B, (byte)'L', (byte)'u', (byte)'a', 0x50, .. new byte[17]];

    private static uint Get(int k, int a = 0) => 5u | ((uint)k << 6) | ((uint)a << 24);
    private static uint Closure(int f, int a = 0) => (uint)HksChunk.ClosureOp | ((uint)f << 6) | ((uint)a << 24);

    /// <summary>A flat list of "name = number" (goal_list / logic_list layout).</summary>
    private static byte[] List(params (string, double)[] globals)
    {
        var chunk = new HksChunk { Header = Header, Main = new HksChunk.Function { Name = "=(none)", Instructions = [HksChunk.Return] } };
        chunk.AddNumericGlobals(globals);
        return chunk.Write();
    }

    /// <summary>A function that reads the given globals.</summary>
    private static HksChunk.Function Reads(params string[] globals) => new()
    {
        Constants = globals.Select(HksChunk.Constant.Text).ToList(),
        Instructions = [.. globals.Select((_, k) => Get(k)), HksChunk.Return],
    };

    /// <summary>A script: optional GETGLOBAL pairs in its main chunk (registrations), then "function name() ... end" per function.</summary>
    private static byte[] Script(string[] mainReads, params (string Name, HksChunk.Function Body)[] functions)
    {
        var main = new HksChunk.Function { Name = "=(none)" };
        foreach (var r in mainReads)
        {
            main.Instructions.Add(Get(main.Constants.Count, 1));
            main.Constants.Add(HksChunk.Constant.Text(r));
        }
        for (int i = 0; i < functions.Length; i++)
        {
            main.Functions.Add(functions[i].Body);
            main.Instructions.Add(Closure(i));
            main.Instructions.Add(HksChunk.SetGlobal(main.Constants.Count));
            main.Constants.Add(HksChunk.Constant.Text(functions[i].Name));
        }
        main.Instructions.Add(HksChunk.Return);
        return new HksChunk { Header = Header, Main = main }.Write();
    }

    /// <summary>A function that reads the given globals and upvalue 0 (Fixed byte 4 = 1 upvalue).</summary>
    private static HksChunk.Function ReadsUpvalue(params string[] globals)
    {
        var f = Reads(globals);
        f.Fixed[4] = 1;
        f.Instructions.Insert(0, 4u);   // GETUPVAL R0, U0
        return f;
    }

    /// <summary>
    /// A script like DS3's common_battle_func.lua: "local MAX = 99" (LOADK R0), then each function made with CLOSURE R1
    /// and, for one with an upvalue, the MOVE pseudo-instruction binding R0. <paramref name="changed"/> sets R0 again
    /// after the functions are made (the local is no constant then).
    /// </summary>
    private static byte[] UpvalueScript(double local, bool changed, params (string Name, HksChunk.Function Body)[] functions)
    {
        var main = new HksChunk.Function { Name = "=(none)" };
        main.Fixed[7] = 2;
        main.Constants.Add(HksChunk.Constant.Number(local));
        main.Instructions.Add(HksChunk.LoadK(0));
        for (int i = 0; i < functions.Length; i++)
        {
            main.Functions.Add(functions[i].Body);
            main.Instructions.Add(Closure(i, 1));
            for (int u = 0; u < HksChunk.Upvalues(functions[i].Body); u++)
                main.Instructions.Add(0u);   // MOVE pseudo-instruction: upvalue = R0
            main.Instructions.Add(HksChunk.SetGlobal(main.Constants.Count) | (1u << 24));
            main.Constants.Add(HksChunk.Constant.Text(functions[i].Name));
        }
        if (changed)
        {
            main.Instructions.Add(HksChunk.LoadK(main.Constants.Count));
            main.Constants.Add(HksChunk.Constant.Number(local + 1));
        }
        main.Instructions.Add(HksChunk.Return);
        return new HksChunk { Header = Header, Main = main }.Write();
    }

    private static BND4 Binder(params (int Id, string Leaf, byte[] Bytes)[] files)
    {
        var bnd = new BND4 { Version = "07D7R6", Format = SoulsFormats.Binder.Format.IDs | SoulsFormats.Binder.Format.Names1 | SoulsFormats.Binder.Format.Names2 | SoulsFormats.Binder.Format.Compression, Unicode = true, Extended = 4 };
        foreach (var (id, leaf, bytes) in files)
            bnd.Files.Add(new BinderFile(SoulsFormats.Binder.FileFlags.Flag1, id, @"N:\GR\data\INTERROOT_win64\script\ai\out\bin\" + leaf, bytes));
        return bnd;
    }

    public static int Run(Action<string, bool> check)
    {
        int before = 0;
        void Check(string what, bool ok) { check(what, ok); if (!ok) before++; }

        // ---- HksChunk ----
        var list = HksChunk.Read(List(("GOAL_A", 100), ("GOAL_B", 200)));
        Check("HKS: a goal list reads back its numbers", list.NumericGlobals() is { Count: 2 } g && g["GOAL_A"] == 100 && g["GOAL_B"] == 200);
        Check("HKS: adding to a goal list skips names it has", list.AddNumericGlobals([("GOAL_A", 5), ("GOAL_C", 300)]) == 1
            && HksChunk.Read(list.Write()).NumericGlobals() is { Count: 3 } g2 && g2["GOAL_A"] == 100 && g2["GOAL_C"] == 300);
        var lib = HksChunk.Read(Script([], ("F1", Reads("X")), ("F2", Reads("Y", "Z"))));
        var functions = lib.GlobalFunctions();
        var lifted = HksChunk.Read(HksChunk.Lifted(lib, [("F2", functions["F2"])]).Write());
        Check("HKS: a function lifted out of its file is defined alone and is the same code",
            functions.Count == 2 && lifted.GlobalFunctions() is { Count: 1 } l && HksChunk.SameCode(l["F2"], functions["F2"])
            && lifted.AssignedGlobals().SequenceEqual(["F2"]) && HksChunk.ReadGlobals(l["F2"]).Order().SequenceEqual(["Y", "Z"]));
        // DS3's common_battle_func.lua: "local MAX = 99" read as an upvalue by Common_Battle_Activate (acts 1-99).
        var withLocal = HksChunk.Read(UpvalueScript(99, false, ("Plain", Reads("X")), ("Common_Battle_Activate", ReadsUpvalue("REGIST_FUNC"))));
        var cba = withLocal.GlobalFunctions()["Common_Battle_Activate"];
        var liftedCba = HksChunk.Read(HksChunk.Lifted(withLocal, [("Ds3_Common_Battle_Activate", cba)]).Write());
        var lm = liftedCba.Main;
        Check("HKS: a function whose upvalue is a file constant is lifted with that constant bound again",
            HksChunk.UpvalueConstants(withLocal, cba) is [{ AsNumber: 99 }]
            && liftedCba.GlobalFunctions() is { Count: 1 } lc && HksChunk.SameCode(lc["Ds3_Common_Battle_Activate"], cba)
            && lm.Instructions.Count == 5 && HksChunk.Op(lm.Instructions[0]) == 1 && HksChunk.A(lm.Instructions[0]) == 1
            && lm.Constants[HksChunk.Bx(lm.Instructions[0])].AsNumber == 99
            && HksChunk.Op(lm.Instructions[1]) == HksChunk.ClosureOp && lm.Instructions[2] == (1u << 15)
            && HksChunk.Op(lm.Instructions[3]) == 7 && lm.Instructions[4] == HksChunk.Return && lm.Fixed[7] >= 2
            && HksChunk.Liftable(withLocal, withLocal.GlobalFunctions()["Plain"]));
        var changedLocal = HksChunk.Read(UpvalueScript(99, true, ("Common_Battle_Activate", ReadsUpvalue())));
        Check("HKS: a function whose upvalue changes after it is made is not liftable",
            !HksChunk.Liftable(changedLocal, changedLocal.GlobalFunctions()["Common_Battle_Activate"]));
        var onLine9 = Reads("REGISTER_GOAL"); onLine9.Fixed[0] = 9;
        var onLine40 = Reads("REGISTER_GOAL"); onLine40.Fixed[0] = 40;
        Check("HKS: the same function on another line is the same code (DS3's and Elden Ring's RegisterTableGoal)",
            HksChunk.SameCode(onLine9, onLine40) && !HksChunk.SameCode(onLine9, Reads("REGISTER_GOAL", "X")));
        Check("HKS: registrations are a Register call's GOAL_ argument",
            AiPort.Registered(HksChunk.Read(Script(["RegisterTableGoal", "GOAL_A", "GOAL_B"]))).SequenceEqual(["GOAL_A"]));

        // DS3's scripts check which think row they run under: a ported row has another number.
        var checks = new HksChunk { Header = Header, Main = new HksChunk.Function { Name = "=(none)", Instructions = [HksChunk.Return] } };
        checks.Main.Functions.Add(new HksChunk.Function
        {
            Constants = [HksChunk.Constant.Text(AiPort.ThinkCall), HksChunk.Constant.Number(110054), HksChunk.Constant.Number(3010), HksChunk.Constant.Number(144040)],
            Instructions = [HksChunk.Return],
        });
        checks.Main.Functions.Add(new HksChunk.Function { Constants = [HksChunk.Constant.Number(110054)], Instructions = [HksChunk.Return] });
        int renumbered = AiPort.RenumberThinkIds(checks, new Dictionary<int, int> { [110054] = 70069054 });
        var reread = HksChunk.Read(checks.Write());
        Check("AI: a DS3 think row a script checks for becomes the ported row, only where the script asks for its row",
            renumbered == 1 && reread.Main.Functions[0].Constants.Select(c => c.AsNumber).SequenceEqual(new double?[] { null, 70069054, 3010, 144040 })
            && reread.Main.Functions[1].Constants[0].AsNumber == 110054);

        // DS3's scripts check their own state effects by DS3's numbers; a ported enemy's are 700,000,000 + DS3 ID.
        // Lua 5.0: op | C << 6 | B << 15 | A << 24, a B / C of 250 + k names constant k.
        static uint Op(int op, int a, int b = 0, int c = 0) => (uint)op | ((uint)c << 6) | ((uint)b << 15) | ((uint)a << 24);
        static uint LoadK(int a, int k) => 1u | ((uint)k << 6) | ((uint)a << 24);
        const int K = 250;
        // K: 0 "HasSpecialEffectId", 1 "TARGET_SELF", 2 5020, 3 "TARGET_ENE_0", 4 3000 (an animation), 5 "AddObserveSpecialEffectAttribute",
        // 6 13020, 7 "GetSpecialEffectActivateInterruptType", 8 0, 9 "AddSubGoal", 10 100 (an effect DS3 doesn't have)
        var spFn = new HksChunk.Function
        {
            Constants = [HksChunk.Constant.Text("HasSpecialEffectId"), HksChunk.Constant.Text("TARGET_SELF"), HksChunk.Constant.Number(5020),
                HksChunk.Constant.Text("TARGET_ENE_0"), HksChunk.Constant.Number(3000), HksChunk.Constant.Text(AiPort.ObserveCall),
                HksChunk.Constant.Number(13020), HksChunk.Constant.Text(AiPort.InterruptCall), HksChunk.Constant.Number(0),
                HksChunk.Constant.Text("AddSubGoal"), HksChunk.Constant.Number(100)],
            Instructions =
            [
                // self:HasSpecialEffectId(TARGET_SELF, 5020)            -> 700005020
                Op(11, 2, 0, K + 0), Get(1, 4), LoadK(5, 2), Op(25, 2, 4, 2),
                // self:HasSpecialEffectId(TARGET_ENE_0, 3000)           -> kept: the player's effect
                Op(11, 2, 0, K + 0), Get(3, 4), LoadK(5, 4), Op(25, 2, 4, 2),
                // goal:AddSubGoal(TARGET_SELF, 3000)                    -> kept: not an effect check
                Op(11, 2, 1, K + 9), Get(1, 4), LoadK(5, 4), Op(25, 2, 4, 1),
                // self:HasSpecialEffectId(TARGET_SELF, 3000)            -> 700003000, while the AddSubGoal's 3000 stays
                Op(11, 2, 0, K + 0), Get(1, 4), LoadK(5, 4), Op(25, 2, 4, 2),
                // self:HasSpecialEffectId(TARGET_SELF, 100)             -> kept: DS3 has no such effect
                Op(11, 2, 0, K + 0), Get(1, 4), LoadK(5, 10), Op(25, 2, 4, 2),
                // self:AddObserveSpecialEffectAttribute(TARGET_SELF, 13020)
                Op(11, 2, 0, K + 5), Get(1, 4), LoadK(5, 6), Op(25, 2, 4, 1),
                // local r3 = self:GetSpecialEffectActivateInterruptType(0); if r3 == 13020
                Op(11, 3, 0, K + 7), LoadK(5, 8), Op(25, 3, 3, 2), Op(21, 0, 3, K + 6),
                HksChunk.Return,
            ],
        };
        var spConstants = spFn.Constants.ToList();
        var spChunk = new HksChunk { Header = Header, Main = new HksChunk.Function { Name = "=(none)", Instructions = [HksChunk.Return] } };
        spChunk.Main.Functions.Add(spFn);
        var spFound = new SortedSet<int>();
        int spRenumbered = AiPort.RenumberSpEffectIds(spChunk, id => id != 100, spFound, out int spSkipped);
        var spRead = HksChunk.Read(spChunk.Write()).Main.Functions[0];
        double? LoadedAt(int i) => spRead.Constants[HksChunk.Bx(spRead.Instructions[i])].AsNumber;
        double? ComparedAt(int i) => spRead.Constants[(int)((spRead.Instructions[i] >> 6) & 0x1FF) - K].AsNumber;
        Check("AI: a DS3 SpEffect a script checks on itself becomes the ported row (700,000,000 + ID); the player's, other numbers and effects DS3 lacks stay",
            spRenumbered == 4 && spSkipped == 0 && spFound.SequenceEqual([3000, 5020, 13020])
            && LoadedAt(2) == 700005020 && LoadedAt(6) == 3000 && LoadedAt(10) == 3000 && LoadedAt(14) == 700003000 && LoadedAt(18) == 100
            && LoadedAt(22) == 700013020 && ComparedAt(27) == 700013020 && spRead.Constants[4].AsNumber == 3000);
        var keptChunk = new HksChunk { Header = Header, Main = new HksChunk.Function { Name = "=(none)", Instructions = [HksChunk.Return] } };
        keptChunk.Main.Functions.Add(new HksChunk.Function
        {
            Constants = spConstants,
            Instructions = [Op(11, 2, 0, K + 5), Get(3, 4), LoadK(5, 6), Op(25, 2, 4, 1), Op(11, 3, 0, K + 7), LoadK(5, 8), Op(25, 3, 3, 2), Op(21, 0, 3, K + 6), HksChunk.Return],
        });
        Check("AI: effects checked on the team leader (Pontiff's clone checking Pontiff) count as the script's own side; the player's don't",
            AiPort.OwnSide("TARGET_TEAM_LEADER") && AiPort.OwnSide("TARGET_SELF") && AiPort.OwnSide("TARGET_FRI_0")
            && !AiPort.OwnSide("TARGET_ENE_0") && !AiPort.OwnSide("TARGET_LOCALPLAYER"));
        Check("AI: an interrupt compared with an effect observed on the player keeps DS3's number",
            AiPort.RenumberSpEffectIds(keptChunk, _ => true, null, out _) == 0);

        // ---- AiPort ----
        // Elden Ring: goal 223000 is its own; logic 10000 is the same as DS3's; Common_Func is Elden Ring's.
        var erCommon = Binder(
            (1000, "goal_list.lua", List(("GOAL_COMMON_Wait", 2000), ("GOAL_ErOwn_223000_Battle", 223000))),
            (1001, "logic_list.lua", List(("LOGIC_ID_Common10000", 10000))),
            (1002, "common_func.lua", Script([], ("Common_Func", Reads("A")))),
            (1000000, "aiCommon.luagnl", new LUAGNL(false, true) { Globals = ["GOAL_COMMON_Wait", "Common_Func"] }.Write()),
            (1000001, "aiCommon.luainfo", new LUAINFO(false, true) { Goals = [new LUAINFO.Goal(2000, "Wait", false, false)] }.Write()));
        // DS3: the Jailer's goal is 223000 too; its battle script needs DS3's enemy step goal (a function goal in its
        // own common file) and a helper only DS3's common_func.lua has, which can't come whole (it would replace
        // Elden Ring's Common_Func with other code).
        var ds3Common = Binder(
            (1000, "goal_list.lua", List(("GOAL_Jailer_223000_Battle", 223000), ("GOAL_EnemyStep", 2603))),
            (1001, "logic_list.lua", List(("LOGIC_ID_Common10000", 10000))),
            // DS3's Common_Func reads a file-level local as an upvalue, like DS3's Common_Battle_Activate (MAX = 99).
            (1002, "common_func.lua", UpvalueScript(99, false, ("Common_Func", ReadsUpvalue("B")), ("DS3Only_Helper", Reads("C_Unknown")))),
            (1003, "enemy_step.lua", Script([], ("EnemyStep_Activate", Reads("Common_Func")))),
            (1000000, "aiCommon.luagnl", new LUAGNL(false, true) { Globals = ["GOAL_Jailer_223000_Battle"] }.Write()));
        var ds3Map = Binder((1100, "223000_battle.lua", Script(["RegisterTableGoal", "GOAL_Jailer_223000_Battle"],
            ("Jailer223000_Act01", Reads("GOAL_EnemyStep", "DS3Only_Helper", "Common_Func")))));
        var ai = new AiPort(erCommon, new DCX.NoCompressionInfo(), _ => false, [("/script/aicommon.luabnd.dcx", ds3Common), ("/script/m39_00_00_00.luabnd.dcx", ds3Map)]);
        var (battle, note) = ai.PortBattle(223000);
        Check($"AI: a DS3 goal whose number Elden Ring uses moves to a free one ({battle}, {note})", battle == AiPort.FirstRemapped);
        Check("AI: the logic both games have under one name and number stays", ai.PortLogic(10000).Er == 10000);
        Check("AI: the DS3 common file the script needs comes along, its function goal keeps DS3's free number",
            ai.Libraries.Select(s => s.Leaf).SequenceEqual(["enemy_step.lua"]) && ai.NewGoals.GetValueOrDefault("GOAL_EnemyStep") == 2603);
        Check("AI: the goal list learns the moved goal", ai.NewGoals.GetValueOrDefault("GOAL_Jailer_223000_Battle") == AiPort.FirstRemapped);
        Check("AI: nothing is missing (the helper is lifted out of the file that can't come)", ai.Missing.Count == 0);

        // DS3's aicommon SkeletonKingDefault_Battle.lua (High Lord Wolnir) still calls REGISTER_GOAL_NO_SUB_GOAL on the
        // Nameless King's goal; the goal's script is the one that gives it a table (m32's 501000_battle.lua).
        string[] strayCall = ["REGISTER_GOAL_NO_SUB_GOAL", "GOAL_King_501000_Battle", "RegisterTableGoal", "GOAL_Wolnir_516001_Battle"];
        Check("HKS: a REGISTER_GOAL_ option call names a goal, only RegisterTableGoal defines one",
            AiPort.Registered(HksChunk.Read(Script(strayCall))).SequenceEqual(["GOAL_King_501000_Battle", "GOAL_Wolnir_516001_Battle"])
            && AiPort.Registered(HksChunk.Read(Script(strayCall)), tablesOnly: true).SequenceEqual(["GOAL_Wolnir_516001_Battle"]));
        var strayCommon = Binder(
            (1000, "goal_list.lua", List(("GOAL_King_501000_Battle", 501000), ("GOAL_Wolnir_516001_Battle", 516001))),
            (1001, "logic_list.lua", List(("LOGIC_ID_Common10000", 10000))),
            (1002, "WolnirDefault_Battle.lua", Script(strayCall, ("Wolnir_Act01", Reads()))));
        var strayMap = Binder((1100, "501000_battle.lua", Script(["RegisterTableGoal", "GOAL_King_501000_Battle"], ("King_Act01", Reads()))));
        var strayAi = new AiPort(erCommon, new DCX.NoCompressionInfo(), _ => false, [("/script/aicommon.luabnd.dcx", strayCommon), ("/script/m32_00_00_00.luabnd.dcx", strayMap)]);
        var (kingGoal, kingNote) = strayAi.PortBattle(501000);
        var kingBinder = kingGoal is { } kg ? strayAi.Finish().FirstOrDefault(o => o.Path.Equals(AiPort.BattlePath(kg), StringComparison.OrdinalIgnoreCase)).Bytes : null;
        var kingScript = kingBinder is null ? null : BND4.Read(kingBinder).Files.FirstOrDefault(f => f.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
        Check($"AI: a goal's script is the one that defines it, not one that only sets an option on it ({kingGoal}, {kingNote})",
            kingScript is not null && HksChunk.Read(kingScript.Bytes).DefinedGlobals().SequenceEqual(["King_Act01"]));
        var outputs = ai.Finish();
        var goalBinder = outputs.FirstOrDefault(o => o.Path == AiPort.BattlePath(AiPort.FirstRemapped));
        Check("AI: the goal gets a binder of its own with the script and its global list",
            goalBinder.Bytes is not null && BND4.Read(goalBinder.Bytes) is var gb && gb.Files.Count == 2 && gb.Files[0].ID == 1000
            && LUAGNL.Read(gb.Files[1].Bytes).Globals.SequenceEqual(["Jailer223000_Act01"]));
        var common = outputs.FirstOrDefault(o => o.Path == AiPort.CommonPath);
        var commonBnd = common.Bytes is null ? null : BND4.Read(common.Bytes);
        var leaves = commonBnd?.Files.Select(f => ItemText.Leaf(f.Name)).ToList() ?? [];
        Check($"AI: aicommon gets DS3's common file and the lifted helper, never DS3's common_func.lua whole ({string.Join(",", leaves)})",
            leaves.Contains("enemy_step.lua") && leaves.Contains("common_func_ds3.lua") && leaves.Count(l => l == "common_func.lua") == 1);
        // DS3 files that come along whole call lifted functions as they load (Gunda_battle.lua: RegisterTableGoal):
        // the binder loads in ID order, so lifted files must come first (Gundyr stood still on 1.0.718).
        var byLeaf = commonBnd?.Files.ToDictionary(f => ItemText.Leaf(f.Name), f => f.ID) ?? [];
        Check("AI: lifted DS3 functions load before the DS3 files that came along whole",
            byLeaf.TryGetValue("common_func_ds3.lua", out var liftedId) && byLeaf.TryGetValue("enemy_step.lua", out var libId) && liftedId < libId);
        Check("AI: aicommon's goal list and global list carry the new names, Elden Ring's numbers untouched",
            commonBnd is not null && HksChunk.Read(commonBnd.Files.First(f => f.ID == 1000).Bytes).NumericGlobals() is var goals
            && goals["GOAL_ErOwn_223000_Battle"] == 223000 && goals["GOAL_Jailer_223000_Battle"] == AiPort.FirstRemapped && goals["GOAL_EnemyStep"] == 2603
            && LUAGNL.Read(commonBnd.Files.First(f => f.ID == 1000000).Bytes).Globals is var gnl && gnl.Contains("DS3Only_Helper") && gnl.Contains("EnemyStep_Activate"));

        // Common_Func is in both games with other code (DS3 reads B, Elden Ring A): ported scripts and DS3's files that
        // came along read DS3's under a name of its own; Elden Ring's own scripts keep theirs (Friede, 2026-11-27).
        var jailerChunk = goalBinder.Bytes is null ? null : HksChunk.Read(BND4.Read(goalBinder.Bytes).Files[0].Bytes);
        var step = commonBnd?.Files.FirstOrDefault(f => ItemText.Leaf(f.Name) == "enemy_step.lua") is { } sf ? HksChunk.Read(sf.Bytes) : null;
        var liftedFile = commonBnd?.Files.FirstOrDefault(f => ItemText.Leaf(f.Name) == "common_func_ds3.lua") is { } lf ? HksChunk.Read(lf.Bytes) : null;
        var erFunc = commonBnd?.Files.FirstOrDefault(f => ItemText.Leaf(f.Name) == "common_func.lua") is { } ef ? HksChunk.Read(ef.Bytes) : null;
        Check("AI: a common function both games define with other code is DS3's under Ds3_ for ported scripts and DS3 files, Elden Ring's stays for its own",
            ai.Ds3Renamed.GetValueOrDefault("Common_Func") == "Ds3_Common_Func"
            && jailerChunk is not null && jailerChunk.ReadGlobals().Contains("Ds3_Common_Func") && !jailerChunk.ReadGlobals().Contains("Common_Func")
            && step is not null && step.ReadGlobals().Contains("Ds3_Common_Func") && !step.ReadGlobals().Contains("Common_Func")
            && liftedFile is not null && liftedFile.GlobalFunctions() is var liftedFns && liftedFns.ContainsKey("Ds3_Common_Func") && liftedFns.ContainsKey("DS3Only_Helper")
            && HksChunk.ReadGlobals(liftedFns["Ds3_Common_Func"]).Contains("B") && HksChunk.UpvalueConstants(liftedFile, liftedFns["Ds3_Common_Func"]) is [{ AsNumber: 99 }]
            && erFunc is not null && erFunc.GlobalFunctions().ContainsKey("Common_Func") && HksChunk.ReadGlobals(erFunc.GlobalFunctions()["Common_Func"]).Contains("A"));
        var renameChunk = HksChunk.Read(Script([], ("F", Reads("Common_Func", "Other"))));
        Check("HKS: renaming a global read changes only reads of that name",
            renameChunk.RenameGlobalReads(new Dictionary<string, string> { ["Common_Func"] = "Ds3_Common_Func" }) == 1
            && renameChunk.ReadGlobals().OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["Ds3_Common_Func", "Other"]) && renameChunk.AssignedGlobals().Contains("F"));

        // Think rows: DS3's senses (same name and kind), the ported goal; a decimal field doesn't take a whole number.
        var erDef = new PARAMDEF { ParamType = "NPC_THINK_PARAM_ST", Fields = [] };
        foreach (var (name, type) in new[] { ("battleGoalID", PARAMDEF.DefType.s32), ("logicId", PARAMDEF.DefType.s32), ("eye_dist", PARAMDEF.DefType.u16), ("ear_dist", PARAMDEF.DefType.f32),
                     ("SightTargetForgetTime", PARAMDEF.DefType.f32), ("SoundTargetForgetTime", PARAMDEF.DefType.f32), ("MemoryTargetForgetTime", PARAMDEF.DefType.f32),
                     ("searchEye_dist", PARAMDEF.DefType.u16), ("searchEye_angY", PARAMDEF.DefType.u8), ("isUpdateBattleSight", PARAMDEF.DefType.u8), ("disableDark", PARAMDEF.DefType.u8),
                     ("goalAction_ToSearchLv1", PARAMDEF.DefType.u8), ("goalAction_ToSearchLv2", PARAMDEF.DefType.u8),
                     ("enableWeaponOnOff", PARAMDEF.DefType.u8), ("weaponOffSpecialEffectId", PARAMDEF.DefType.s32), ("weaponOnSpecialEffectId", PARAMDEF.DefType.s32),
                     ("weaponOffAnimId", PARAMDEF.DefType.s32), ("weaponOnAnimId", PARAMDEF.DefType.s32), ("soundBehaviorId01", PARAMDEF.DefType.s32) })
            erDef.Fields.Add(new PARAMDEF.Field(erDef, type, name));
        var ds3Def = new PARAMDEF { ParamType = "NPC_THINK_PARAM_ST", Fields = [] };
        foreach (var (name, type) in new[] { ("battleGoalId", PARAMDEF.DefType.s32), ("logicId", PARAMDEF.DefType.s32), ("eye_dist", PARAMDEF.DefType.s16), ("ear_dist", PARAMDEF.DefType.s16),
                     // Paramdex's DS3 names for Elden Ring's SightTargetForgetTime, ear_dist, the two halves of SoundTargetForgetTime and MemoryTargetForgetTime.
                     ("goalID_ToCaution", PARAMDEF.DefType.f32), ("goalID_toFind", PARAMDEF.DefType.f32), ("SightTargetForgetTime", PARAMDEF.DefType.s16),
                     ("SoundTargetForgetTime", PARAMDEF.DefType.s16), ("Unk7", PARAMDEF.DefType.f32), ("goalAction_ToCaution", PARAMDEF.DefType.u8), ("goalAction_ToFind", PARAMDEF.DefType.u8) })
            ds3Def.Fields.Add(new PARAMDEF.Field(ds3Def, type, name));
        var erThink = new PARAM { Rows = [] };
        erThink.ApplyParamdef(erDef);
        var template = new PARAM.Row(39010000, "template", erDef);
        template["battleGoalID"]!.Value = 390000; template["logicId"]!.Value = 10000; template["eye_dist"]!.Value = (ushort)30; template["ear_dist"]!.Value = 0.5f;
        template["SightTargetForgetTime"]!.Value = 20f; template["SoundTargetForgetTime"]!.Value = 5f; template["MemoryTargetForgetTime"]!.Value = 7f;
        template["searchEye_dist"]!.Value = (ushort)35; template["searchEye_angY"]!.Value = (byte)20; template["isUpdateBattleSight"]!.Value = (byte)1;
        template["goalAction_ToSearchLv1"]!.Value = (byte)15; template["goalAction_ToSearchLv2"]!.Value = (byte)16;
        // The Soldier of Godrick's (c4311) sheathed weapon and sound reactions, as Crystal Sage's and the Abyss Watchers' rows had them in 1.0.640.
        template["enableWeaponOnOff"]!.Value = (byte)1; template["weaponOffSpecialEffectId"]!.Value = 13065; template["weaponOnSpecialEffectId"]!.Value = 13064;
        template["weaponOffAnimId"]!.Value = 20020; template["weaponOnAnimId"]!.Value = 20021; template["soundBehaviorId01"]!.Value = 431000;
        erThink.Rows.Add(template);
        var ds3Think = new PARAM { Rows = [] };
        ds3Think.ApplyParamdef(ds3Def);
        foreach (var (id, goal) in new[] { (223000, 223000), (223005, 555555) })
        {
            var r = new PARAM.Row(id, "Jailer", ds3Def);
            r["battleGoalId"]!.Value = goal; r["logicId"]!.Value = 10000; r["eye_dist"]!.Value = (short)18; r["ear_dist"]!.Value = (short)7;
            r["goalID_ToCaution"]!.Value = 8f; r["goalID_toFind"]!.Value = 0.1f; r["SightTargetForgetTime"]!.Value = (short)0; r["SoundTargetForgetTime"]!.Value = (short)16448; r["Unk7"]!.Value = 2f; r["goalAction_ToCaution"]!.Value = (byte)1; r["goalAction_ToFind"]!.Value = (byte)3;
            ds3Think.Rows.Add(r);
        }
        var chrAi = ai.Port(2230, 7071, "Jailer", ds3Think, erThink.Rows.Select(r => r.ID).ToHashSet().Contains);
        Check("AI: a think row per DS3 row of the character, the first with a ported goal is the one the spawn list uses",
            chrAi is { Rows.Count: 2 } && chrAi.Primary == 7071 * 10000 + AiPort.ThinkOffset && chrAi.Rows[1].Battle == AiPort.NoBattleGoal);
        int rows = chrAi is null ? 0 : AiPort.ApplyParams(erThink, ds3Think, 39010000, chrAi);
        var row = erThink[70719000];
        Check("AI: the think row is the template's with DS3's senses and the ported goal and logic; a goal no DS3 script defines is no goal, not the template's",
            rows == 2 && row is not null && Convert.ToInt32(row["battleGoalID"]!.Value) == AiPort.FirstRemapped && Convert.ToInt32(row["logicId"]!.Value) == 10000
            && Convert.ToInt32(row["eye_dist"]!.Value) == 18
            && erThink[70719005] is { } kept && Convert.ToInt32(kept["battleGoalID"]!.Value) == AiPort.NoBattleGoal);

        Check("AI: DS3's hearing and forget times come from the fields Paramdex's DS3 def mislabels; no Elden Ring search sight, battle sight or darkness",
            row is not null && (float)row["ear_dist"]!.Value == 0.1f && (float)row["SightTargetForgetTime"]!.Value == 8f && (float)row["SoundTargetForgetTime"]!.Value == 3f
            && (float)row["MemoryTargetForgetTime"]!.Value == 2f && Convert.ToInt32(row["searchEye_dist"]!.Value) == 0 && Convert.ToInt32(row["searchEye_angY"]!.Value) == 0
            && Convert.ToInt32(row["isUpdateBattleSight"]!.Value) == 0 && Convert.ToInt32(row["disableDark"]!.Value) == 1);
        Check("AI: no template weapon on/off (draw move 20021, weapon-off effect 13065) or template sound reactions on a ported think row",
            row is not null && Convert.ToInt32(row["enableWeaponOnOff"]!.Value) == 0 && Convert.ToInt32(row["weaponOffSpecialEffectId"]!.Value) == -1
            && Convert.ToInt32(row["weaponOnSpecialEffectId"]!.Value) == -1 && Convert.ToInt32(row["weaponOnAnimId"]!.Value) == -1
            && Convert.ToInt32(row["weaponOffAnimId"]!.Value) == -1 && Convert.ToInt32(row["soundBehaviorId01"]!.Value) == -1
            && AiPort.WeaponStateEffects(template).Order().SequenceEqual([13064, 13065]));
        Check("AI: Elden Ring's search levels take DS3's caution and find actions, not the template's search",
            row is not null && Convert.ToInt32(row["goalAction_ToSearchLv1"]!.Value) == 1 && Convert.ToInt32(row["goalAction_ToSearchLv2"]!.Value) == 3);

        // The chr map carries the think rows, by DS3 row and model; the map port gives a placement its own.
        var map = EnemyPort.ChrMap.Parse(["chr\tc2230\tc7071\t70710000\t70719000\tJailer", "think\t223005\tc7071\t70719005\tJailer"]);
        var chooser = new EnemyPort.Chooser([], map);
        var jailer = map.Find(new EnemyPort.Ds3Enemy("c2230", 0, "Jailer", false, false))!;
        Check("chr map: a placement's own DS3 think row picks its ported row, others the character's",
            chooser.ThinkFor(jailer, 223005) == 70719005 && chooser.ThinkFor(jailer, 223000) == 70719000 && map.Think(223005, "c9999") is null);

        // ---- ChrMoves ----
        var plan = ChrMoves.Plan([3000, 3010, 3033], new HashSet<int> { 3000, 3001, 3002 }, new HashSet<int> { 3000, 3001, 3002 }, new HashSet<int> { 3002 });
        Check("extra attacks: only DS3 attacks the template lacks, each with its nearest whole attack",
            plan.SequenceEqual([new ChrMoves.Added(3010, 3001), new ChrMoves.Added(3033, 3001)]));
        var idlePlan = ChrMoves.Plan([702, 1702, 9760, 20005, 30001, 3010], new HashSet<int> { 700, 701, 1700, 20004, 3001 },
            new HashSet<int> { 700, 701, 1700, 20004, 3001 }, new HashSet<int>());
        Check("extra moves: waiting poses, getting-up moves and map-script moves get a donor of their own family (none for a family the template lacks)",
            idlePlan.SequenceEqual([new ChrMoves.Added(702, 701), new ChrMoves.Added(1702, 1700), new ChrMoves.Added(3010, 3001), new ChrMoves.Added(20005, 20004)])
            && ChrMoves.Wanted(702, false) && !ChrMoves.Wanted(3010, false) && ChrMoves.Wanted(3010, true) && !ChrMoves.Wanted(5000, true) && !ChrMoves.Wanted(9760, true));
        // The Dancer's AI asks for 1500 (her phase change) as an attack: it takes a state cloned from the nearest whole
        // attack, as a move the template has an entry but no file for does (its entry gets a file of its own).
        var namedBefore = ChrMoves.AiNamed;
        ChrMoves.AiNamed = new HashSet<int> { 1500, 1600 };
        var phasePlan = ChrMoves.Plan([1500, 1600, 1702, 3010], new HashSet<int> { 1600, 1700, 3000, 3020 }, new HashSet<int> { 1700, 3000, 3020 }, new HashSet<int>());
        Check("AI moves: moves DS3's AI asks for outside 3000-3999 (phase changes) take the nearest whole attack as donor, only with DS3's AI naming them",
            phasePlan.SequenceEqual([new ChrMoves.Added(1500, 3000), new ChrMoves.Added(1600, 3000, OwnEntry: true), new ChrMoves.Added(1702, 1700), new ChrMoves.Added(3010, 3000)])
            && ChrMoves.Wanted(1500, true) && !ChrMoves.Wanted(1500, false) && !ChrMoves.Wanted(1510, true) && ChrMoves.FamilyOf(1702)?.Name == "getting up"
            && ChrMoves.FamilyOf(1500)?.Machine == "Attack_SM");
        ChrMoves.AiNamed = namedBefore;
        // Elden Ring's c3061 plays other moves' clips for its waiting poses (700 imports 30016): DS3's 700 gets a file
        // of its own, its other poses take a 700s entry as donor, and a family with no entry at all stays out.
        var borrowedPlan = ChrMoves.Plan([700, 702, 1702, 3010], new HashSet<int> { 700, 701, 1700, 1701, 3000 },
            new HashSet<int> { 3000 }, new HashSet<int>());
        Check("extra moves: a template pose that borrows another clip gets DS3's own file, other DS3 poses a donor of the family without a file",
            borrowedPlan.SequenceEqual([new ChrMoves.Added(700, 700, true), new ChrMoves.Added(702, 701), new ChrMoves.Added(1702, 1701), new ChrMoves.Added(3010, 3000)]));
        var borrowTae = new TAE { Format = TAE.TAEFormat.SDT, Animations = [], Flags = new byte[8], SkeletonName = "skeleton.hkt", SibName = "c7000.sib", ID = 200007000 };
        borrowTae.Animations.Add(new TAE.Animation(700, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = 30016 }, "a000_030016.hkt"));
        borrowTae.Animations.Add(new TAE.Animation(701, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = 10001 }, "a000_010001.hkt"));
        borrowTae.Animations.Add(new TAE.Animation(1700, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = 20003 }, "a000_020003.hkt"));
        borrowTae.Animations.Add(new TAE.Animation(1701, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = 20000 }, "a000_020000.hkt"));
        borrowTae.Animations.Add(new TAE.Animation(3000, new TAE.Animation.AnimMiniHeader.Standard(), "a000_003000.hkt"));
        const string folder = @"N:\GR\data\INTERROOT_win64\chr\c7000\hkx_div00_compendium\";
        var borrowMain = new BND4();
        borrowMain.Files.Add(new BinderFile(SoulsFormats.Binder.FileFlags.Flag1, 5000000, @"N:\GR\data\INTERROOT_win64\chr\c7000\tae\c7000.tae", borrowTae.Write()));
        var borrowBnd = new BND4();
        borrowBnd.Files.Add(new BinderFile(SoulsFormats.Binder.FileFlags.Flag1, 1000003000, folder + "a000_003000.hkx", [1, 2, 3]));
        var borrowAdded = ChrMoves.AddAnimations([borrowMain, borrowBnd], borrowedPlan, new HashSet<int> { 3000 });
        var borrowBack = TAE.Read(borrowMain.Files[0].Bytes);
        Check("extra moves: the borrowed pose and the new ones get files copied from a whole template clip, and their entries play them",
            borrowAdded.Count == 4 && borrowBnd.Files.Any(f => f.ID == 1000000700 && f.Name == folder + "a000_000700.hkx" && f.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }))
            && borrowBnd.Files.Any(f => f.ID == 1000000702) && borrowBnd.Files.Any(f => f.ID == 1000001702)
            && borrowBack.Animations.First(a => a.ID == 700) is { AnimFileName: "a000_000700.hkt", MiniHeader: TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: false } }
            && borrowBack.Animations.First(a => a.ID == 702) is { AnimFileName: "a000_000702.hkt", MiniHeader: TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: false } }
            && borrowBack.Animations.First(a => a.ID == 701).MiniHeader is TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: true, ImportHKXSourceAnimID: 10001 });

        var group = new TAE.EventGroup(0);
        var donorEntry = new TAE.Animation(3001, new TAE.Animation.AnimMiniHeader.Standard(), "a000_003001.hkt");
        donorEntry.EventGroups.Add(group);
        donorEntry.Events.Add(new TAE.Event(0.1f, 0.5f, 1, 0, new byte[16], false) { Group = group });
        var clone = ChrMoves.CloneEntry(donorEntry, 3033);
        Check("extra attacks: the TAE entry is the donor's under the new ID, with groups of its own",
            clone.ID == 3033 && clone.AnimFileName == "a000_003033.hkt" && clone.Events.Count == 1 && clone.Events[0].Group is { } cg
            && !ReferenceEquals(cg, group) && clone.EventGroups.Contains(cg));
        var graph = Graph();
        int states = ChrMoves.AddStates(graph, [new ChrMoves.Added(3033, 3001)]);
        var all = HavokGraph.All(graph);
        var sm = all.OfType<hkbStateMachine>().First();
        var strings = all.OfType<hkbBehaviorGraphStringData>().First();
        var added = sm.m_states.FirstOrDefault(s => s.m_name == "Attack3033");
        var clip = (added?.m_generator as CustomManualSelectorGenerator)?.m_generators.OfType<hkbClipGenerator>().ToList();
        Check("extra attacks: a behaviour state per attack, playing the new clip (registered in the animation list), with both wildcard events",
            states == 1 && added is not null && clip is { Count: 1 } && clip[0].m_animationName == "a000_003033" && clip[0].m_animationInternalId == 2
            && strings.m_animationNames[2] == @"..\Model\chr\c7000\hkx\a000_003033.hkx"
            && sm.m_wildcardTransitions!.m_transitions.Where(t => t.m_toStateId == added.m_stateId).Select(t => strings.m_eventNames[t.m_eventId]).Order()
                .SequenceEqual(["W_Attack3033", "W_Event3033"])
            && all.OfType<hkbBehaviorGraphData>().First().m_eventInfos.Count == strings.m_eventNames.Count);

        var phaseGraph = Graph();
        int phaseStates = ChrMoves.AddStates(phaseGraph, [new ChrMoves.Added(1500, 3001)]);
        var phaseAll = HavokGraph.All(phaseGraph);
        var phaseSm = phaseAll.OfType<hkbStateMachine>().First();
        var phaseStrings = phaseAll.OfType<hkbBehaviorGraphStringData>().First();
        var phase = phaseSm.m_states.FirstOrDefault(st => st.m_name == "Attack1500");
        Check("AI moves: a phase change gets an Attack_SM state playing its own clip, reached by W_Attack1500 (what the AI's attack request fires)",
            phaseStates == 1 && phase?.m_generator is CustomManualSelectorGenerator { m_name: "Attack1500_CMSG", m_animId: 1500 } pg
            && pg.m_generators.OfType<hkbClipGenerator>().Single().m_animationName == "a000_001500"
            && phaseSm.m_wildcardTransitions!.m_transitions.Where(t => t.m_toStateId == phase.m_stateId).Select(t => phaseStrings.m_eventNames[t.m_eventId]).Order()
                .SequenceEqual(["W_Attack1500", "W_Event1500"]));

        var ownTae = new TAE { Format = TAE.TAEFormat.SDT, Animations = [], Flags = new byte[8], SkeletonName = "skeleton.hkt", SibName = "c7000.sib", ID = 200007000 };
        ownTae.Animations.Add(new TAE.Animation(1600, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = 3000 }, "a000_003000.hkt"));
        ownTae.Animations.Add(new TAE.Animation(3000, new TAE.Animation.AnimMiniHeader.Standard(), "a000_003000.hkt"));
        var ownMain = new BND4();
        ownMain.Files.Add(new BinderFile(SoulsFormats.Binder.FileFlags.Flag1, 5000000, @"N:\GR\data\INTERROOT_win64\chr\c7000\tae\c7000.tae", ownTae.Write()));
        var ownBnd = new BND4();
        ownBnd.Files.Add(new BinderFile(SoulsFormats.Binder.FileFlags.Flag1, 1000003000, folder + "a000_003000.hkx", [1, 2, 3]));
        var ownAdded = ChrMoves.AddAnimations([ownMain, ownBnd], [new ChrMoves.Added(1500, 3000), new ChrMoves.Added(1600, 3000, OwnEntry: true)], new HashSet<int> { 3000 });
        var ownBack = TAE.Read(ownMain.Files[0].Bytes);
        Check("AI moves: a phase change gets a file and an entry; one the template has an entry for keeps it, pointed at its own file",
            ownAdded.Count == 2 && ownBnd.Files.Any(f => f.ID == 1000001500) && ownBnd.Files.Any(f => f.ID == 1000001600)
            && ownBack.Animations.Count(a => a.ID == 1600) == 1 && ownBack.Animations.Any(a => a.ID == 1500 && a.AnimFileName == "a000_001500.hkt")
            && ownBack.Animations.First(a => a.ID == 1600) is { AnimFileName: "a000_001600.hkt", MiniHeader: TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: false } });

        var idleGraph = IdleGraph();
        int idleStates = ChrMoves.AddStates(idleGraph, [new ChrMoves.Added(702, 700)]);
        var idleAll = HavokGraph.All(idleGraph);
        var idles = idleAll.OfType<hkbStateMachine>().First(m => m.m_name == "IdleUniques_SM");
        var idleStrings = idleAll.OfType<hkbBehaviorGraphStringData>().First();
        var pose = idles.m_states.FirstOrDefault(st => st.m_name == "IdleUnique702");
        Check("extra moves: a waiting pose gets a state in the idle machine, named as Elden Ring names them, reached by its own wildcard only",
            idleStates == 1 && pose?.m_generator is CustomManualSelectorGenerator { m_name: "IdleUnique702_CMSG", m_animId: 702 } ps
            && ps.m_generators.OfType<hkbClipGenerator>().Single().m_animationName == "a000_000702"
            && idles.m_wildcardTransitions!.m_transitions.Where(t => t.m_toStateId == pose.m_stateId).Select(t => idleStrings.m_eventNames[t.m_eventId]).SequenceEqual(["W_IdleUnique702"])
            && idleAll.OfType<hkbStateMachine>().First(m => m.m_name == "Attack_SM").m_states.Count == 1);

        // ---- TemplateEffects ----
        var npcDef = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        for (int k = 0; k < TemplateEffects.Slots; k++)
            npcDef.Fields.Add(new PARAMDEF.Field(npcDef, PARAMDEF.DefType.s32, $"spEffectID{k}"));
        var npc = new PARAM { Rows = [] };
        npc.ApplyParamdef(npcDef);
        for (int chr = 0; chr < 10; chr++)
        {
            var r = new PARAM.Row(40000000 + chr * 10000, "", npcDef);
            r["spEffectID0"]!.Value = 90030;
            npc.Rows.Add(r);
        }
        var skeleton = new PARAM.Row(30610000, "Giant Beast Skeleton", npcDef);
        skeleton["spEffectID0"]!.Value = 5832; skeleton["spEffectID1"]!.Value = 90030; skeleton["spEffectID2"]!.Value = 12540;
        npc.Rows.Add(skeleton);
        var spDef = new PARAMDEF { ParamType = "SP_EFFECT_PARAM_ST", Fields = [] };
        spDef.Fields.Add(new PARAMDEF.Field(spDef, PARAMDEF.DefType.u16, "stateInfo"));
        var sp = new PARAM { Rows = [] };
        sp.ApplyParamdef(spDef);
        var revive = new PARAM.Row(5832, "revive", spDef);
        revive["stateInfo"]!.Value = (ushort)143;
        sp.Rows.Add(revive);
        var fx = new TemplateEffects(npc, sp);
        var ported = new PARAM.Row(70050000, "", npcDef);
        for (int k = 0; k < 3; k++)
            ported[$"spEffectID{k}"]!.Value = skeleton[$"spEffectID{k}"]!.Value;
        var dropped = fx.Strip(ported, 30610000, "Giant Beast Skeleton");
        Check("template effects: the skeleton's revival and its own effects come off, shared ones stay (first slot)",
            dropped.Order().SequenceEqual([5832, 12540]) && TemplateEffects.Effects(ported).SequenceEqual([90030]));
        return before;
    }

    /// <summary>A master machine over an Attack_SM and an IdleUniques_SM with IdleUnique700 (its wildcard and a named blend into it).</summary>
    private static hkbBehaviorGraph IdleGraph()
    {
        var attack = (hkbStateMachine)Graph().m_rootGenerator!;
        var strings = new hkbBehaviorGraphStringData
        {
            m_eventNames = ["W_Attack3001", "W_Event3001", "W_IdleUnique700", "IdleToIdleUnique1750_to_IdleUnique700"],
            m_animationNames = [@"..\Model\chr\c7000\hkx\a000_003001.hkx", @"..\Model\chr\c7000\hkx\a001_003001.hkx", @"..\Model\chr\c9997\hkx\a000\a000_000700.hkx"],
        };
        var data = new hkbBehaviorGraphData { m_stringData = strings, m_eventInfos = [new hkbEventInfo(), new hkbEventInfo(), new hkbEventInfo(), new hkbEventInfo()] };
        var idle = new hkbStateMachine
        {
            m_name = "IdleUniques_SM",
            m_states =
            [
                new hkbStateMachine.StateInfo
                {
                    m_name = "IdleUnique700", m_stateId = 0,
                    m_generator = new CustomManualSelectorGenerator
                    {
                        m_name = "IdleUnique700_CMSG", m_animId = 700,
                        m_generators = [new hkbClipGenerator { m_animationName = "a000_000700", m_name = "a000_000700_hkx_AutoSet_00", m_animationInternalId = 2 }],
                    },
                },
            ],
            m_wildcardTransitions = new hkbStateMachine.TransitionInfoArray
            {
                m_transitions = [new hkbStateMachine.TransitionInfo { m_eventId = 2, m_toStateId = 0 }, new hkbStateMachine.TransitionInfo { m_eventId = 3, m_toStateId = 0 }],
            },
        };
        var master = new hkbStateMachine
        {
            m_name = "Master_SM",
            m_states = [new hkbStateMachine.StateInfo { m_name = "Attack", m_stateId = 0, m_generator = attack }, new hkbStateMachine.StateInfo { m_name = "IdleUnique", m_stateId = 1, m_generator = idle }],
        };
        return new hkbBehaviorGraph { m_rootGenerator = master, m_data = data };
    }

    /// <summary>An Attack_SM with Attack3001 (two clips, a000 and a001) and its two wildcard transitions.</summary>
    private static hkbBehaviorGraph Graph()
    {
        var strings = new hkbBehaviorGraphStringData
        {
            m_eventNames = ["W_Attack3001", "W_Event3001"],
            m_animationNames = [@"..\Model\chr\c7000\hkx\a000_003001.hkx", @"..\Model\chr\c7000\hkx\a001_003001.hkx"],
        };
        var data = new hkbBehaviorGraphData { m_stringData = strings, m_eventInfos = [new hkbEventInfo(), new hkbEventInfo()] };
        var selector = new CustomManualSelectorGenerator
        {
            m_name = "Attack3001_CMSG", m_animId = 3001,
            m_generators =
            [
                new hkbClipGenerator { m_animationName = "a000_003001", m_name = "a000_003001_hkx_AutoSet_01", m_animationInternalId = 0 },
                new hkbClipGenerator { m_animationName = "a001_003001", m_name = "a001_003001_hkx_AutoSet_01", m_animationInternalId = 1 },
            ],
        };
        var sm = new hkbStateMachine
        {
            m_name = "Attack_SM",
            m_states = [new hkbStateMachine.StateInfo { m_name = "Attack3001", m_stateId = 1, m_generator = selector }],
            m_wildcardTransitions = new hkbStateMachine.TransitionInfoArray
            {
                m_transitions = [new hkbStateMachine.TransitionInfo { m_eventId = 0, m_toStateId = 1 }, new hkbStateMachine.TransitionInfo { m_eventId = 1, m_toStateId = 1 }],
            },
        };
        return new hkbBehaviorGraph { m_rootGenerator = sm, m_data = data };
    }
}
