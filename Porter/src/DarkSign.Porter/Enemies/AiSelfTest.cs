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
        Check("HKS: registrations are a Register call's GOAL_ argument",
            AiPort.Registered(HksChunk.Read(Script(["RegisterTableGoal", "GOAL_A", "GOAL_B"]))).SequenceEqual(["GOAL_A"]));

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
            (1002, "common_func.lua", Script([], ("Common_Func", Reads("B")), ("DS3Only_Helper", Reads("C_Unknown")))),
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
        Check("AI: aicommon's goal list and global list carry the new names, Elden Ring's numbers untouched",
            commonBnd is not null && HksChunk.Read(commonBnd.Files.First(f => f.ID == 1000).Bytes).NumericGlobals() is var goals
            && goals["GOAL_ErOwn_223000_Battle"] == 223000 && goals["GOAL_Jailer_223000_Battle"] == AiPort.FirstRemapped && goals["GOAL_EnemyStep"] == 2603
            && LUAGNL.Read(commonBnd.Files.First(f => f.ID == 1000000).Bytes).Globals is var gnl && gnl.Contains("DS3Only_Helper") && gnl.Contains("EnemyStep_Activate"));

        // Think rows: DS3's senses (same name and kind), the ported goal; a decimal field doesn't take a whole number.
        var erDef = new PARAMDEF { ParamType = "NPC_THINK_PARAM_ST", Fields = [] };
        foreach (var (name, type) in new[] { ("battleGoalID", PARAMDEF.DefType.s32), ("logicId", PARAMDEF.DefType.s32), ("eye_dist", PARAMDEF.DefType.u16), ("ear_dist", PARAMDEF.DefType.f32) })
            erDef.Fields.Add(new PARAMDEF.Field(erDef, type, name));
        var ds3Def = new PARAMDEF { ParamType = "NPC_THINK_PARAM_ST", Fields = [] };
        foreach (var (name, type) in new[] { ("battleGoalId", PARAMDEF.DefType.s32), ("logicId", PARAMDEF.DefType.s32), ("eye_dist", PARAMDEF.DefType.s16), ("ear_dist", PARAMDEF.DefType.s16) })
            ds3Def.Fields.Add(new PARAMDEF.Field(ds3Def, type, name));
        var erThink = new PARAM { Rows = [] };
        erThink.ApplyParamdef(erDef);
        var template = new PARAM.Row(39010000, "template", erDef);
        template["battleGoalID"]!.Value = 390000; template["logicId"]!.Value = 10000; template["eye_dist"]!.Value = (ushort)30; template["ear_dist"]!.Value = 0.5f;
        erThink.Rows.Add(template);
        var ds3Think = new PARAM { Rows = [] };
        ds3Think.ApplyParamdef(ds3Def);
        foreach (var (id, goal) in new[] { (223000, 223000), (223005, 555555) })
        {
            var r = new PARAM.Row(id, "Jailer", ds3Def);
            r["battleGoalId"]!.Value = goal; r["logicId"]!.Value = 10000; r["eye_dist"]!.Value = (short)18; r["ear_dist"]!.Value = (short)7;
            ds3Think.Rows.Add(r);
        }
        var chrAi = ai.Port(2230, 7071, "Jailer", ds3Think, erThink.Rows.Select(r => r.ID).ToHashSet().Contains);
        Check("AI: a think row per DS3 row of the character, the first with a ported goal is the one the spawn list uses",
            chrAi is { Rows.Count: 2 } && chrAi.Primary == 7071 * 10000 + AiPort.ThinkOffset && chrAi.Rows[1].Battle is null);
        int rows = chrAi is null ? 0 : AiPort.ApplyParams(erThink, ds3Think, 39010000, chrAi);
        var row = erThink[70719000];
        Check("AI: the think row is the template's with DS3's senses and the ported goal and logic",
            rows == 2 && row is not null && Convert.ToInt32(row["battleGoalID"]!.Value) == AiPort.FirstRemapped && Convert.ToInt32(row["logicId"]!.Value) == 10000
            && Convert.ToInt32(row["eye_dist"]!.Value) == 18 && (float)row["ear_dist"]!.Value == 0.5f
            && erThink[70719005] is { } kept && Convert.ToInt32(kept["battleGoalID"]!.Value) == 390000);

        // The chr map carries the think rows, by DS3 row and model; the map port gives a placement its own.
        var map = EnemyPort.ChrMap.Parse(["chr\tc2230\tc7071\t70710000\t70719000\tJailer", "think\t223005\tc7071\t70719005\tJailer"]);
        var chooser = new EnemyPort.Chooser([], map, EnemyPort.Method.Auto);
        var jailer = map.Find(new EnemyPort.Ds3Enemy("c2230", 0, "Jailer", false, false))!;
        Check("chr map: a placement's own DS3 think row picks its ported row, others the character's",
            chooser.ThinkFor(jailer, 223005) == 70719005 && chooser.ThinkFor(jailer, 223000) == 70719000 && map.Think(223005, "c9999") is null);

        // ---- ChrMoves ----
        var plan = ChrMoves.Plan([3000, 3010, 3033, 5000], new HashSet<int> { 3000, 3001, 3002 }, new HashSet<int> { 3000, 3001, 3002 }, new HashSet<int> { 3002 });
        Check("extra attacks: only DS3 attacks the template lacks, each with its nearest whole attack",
            plan.SequenceEqual([new ChrMoves.Added(3010, 3001), new ChrMoves.Added(3033, 3001)]));
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
