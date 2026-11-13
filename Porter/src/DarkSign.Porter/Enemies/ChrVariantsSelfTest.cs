using HKLib.hk2018;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>Part of enemy-selftest: DS3's variant move sets (<see cref="ChrVariants"/>) on hand-made files.</summary>
public static class ChrVariantsSelfTest
{
    public static void Run(Action<string, bool> check)
    {
        // DS3's c1280 layout: a000 base moves, a001 the spear set, one a001 entry playing the base file.
        var ds3 = new Dictionary<(int, int), (int, int)>
        {
            [(0, 0)] = (0, 0), [(0, 3000)] = (0, 3000), [(0, 9000)] = (0, 9000),
            [(1, 0)] = (1, 0), [(1, 3000)] = (1, 3000), [(1, 3001)] = (1, 3001), [(1, 9000)] = (1, 9000), [(1, 2000)] = (0, 2000),
            [(2, 0)] = (2, 0),
        };
        var plan = ChrVariants.Plan([0, 2000, 3000, 9000], new HashSet<(int, int)> { (0, 0), (0, 3000), (1, 3000) }, ds3,
            id => id == 9000, key => key == (0, 9000));
        check("variants: a section move per template base move DS3's section has its own clip for (not one playing the base file, not one the template has, same kind)",
            plan.SequenceEqual([(1, 0), (2, 0)]));

        var tae = new TAE { Format = TAE.TAEFormat.SDT, Animations = [], Flags = new byte[8], SkeletonName = "skeleton.hkt", SibName = "c7000.sib", ID = 200007000 };
        tae.Animations.Add(new TAE.Animation(0, new TAE.Animation.AnimMiniHeader.Standard(), "a000_000000.hkt"));
        tae.Animations[0].Events.Add(new TAE.Event(0, 1, 1, 0, new byte[16], false));
        tae.Animations.Add(new TAE.Animation(3000, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = 3001 }, "a000_003000.hkt"));
        var main = new BND4();
        main.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 5000000, @"N:\GR\data\INTERROOT_win64\chr\c7000\tae\c7000.tae", tae.Write()));
        var div = new BND4();
        div.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 1000000000, @"N:\GR\data\INTERROOT_win64\chr\c7000\hkx_div00_compendium\a000_000000.hkx", [1, 2, 3]));
        div.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 1000003000, @"N:\GR\data\INTERROOT_win64\chr\c7000\hkx_div00_compendium\a000_003000.hkx", [4]));
        var added = ChrVariants.AddSectionFiles([main, div], [(1, 0), (2, 3000), (1, 4000)]);
        var after = TAE.Read(main.Files[0].Bytes);
        var e1 = after.Animations.FirstOrDefault(a => a.ID == 1000000);
        var e2 = after.Animations.FirstOrDefault(a => a.ID == 2003000);
        check("variants: each section move gets a file next to its base move (ID + section x 1,000,000) and a TAE entry playing its own file",
            added.SequenceEqual([(1, 0), (2, 3000)])
            && div.Files.Any(f => f.ID == 1001000000 && f.Name.EndsWith(@"hkx_div00_compendium\a001_000000.hkx", StringComparison.Ordinal) && f.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }))
            && div.Files.Any(f => f.ID == 1002003000 && f.Name.EndsWith("a002_003000.hkx", StringComparison.Ordinal))
            && e1 is { AnimFileName: "a001_000000.hkt", MiniHeader: TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: false } } && e1.Events.Count == 1
            && e2?.MiniHeader is TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: false }
            && after.Animations.Select(a => a.ID).SequenceEqual(after.Animations.Select(a => a.ID).Order()));

        var strings = new hkbBehaviorGraphStringData { m_animationNames = [@"..\Model\chr\c7000\hkx\a000_000000.hkx", @"..\Model\chr\c7000\hkx\a000_003000.hkx"] };
        var idle = new CustomManualSelectorGenerator
        {
            m_name = "IdleDefault_CMSG", m_animId = 0, m_offsetType = CustomManualSelectorGenerator.OffsetType.AnimIdOffset,
            m_generators = [new hkbClipGenerator { m_animationName = "a000_000000", m_name = "a000_000000_hkx_AutoSet_01", m_animationInternalId = 0 }],
        };
        var noOffset = new CustomManualSelectorGenerator
        {
            m_name = "Other_CMSG", m_animId = 0, m_offsetType = CustomManualSelectorGenerator.OffsetType.OffsetNone,
            m_generators = [new hkbClipGenerator { m_animationName = "a000_000000", m_name = "a000_000000_hkx_AutoSet_02", m_animationInternalId = 0 }],
        };
        var sm = new hkbStateMachine
        {
            m_name = "Idle_SM",
            m_states = [new hkbStateMachine.StateInfo { m_name = "Idle", m_stateId = 0, m_generator = idle }, new hkbStateMachine.StateInfo { m_name = "Other", m_stateId = 1, m_generator = noOffset }],
        };
        var graph = new hkbBehaviorGraph { m_rootGenerator = sm, m_data = new hkbBehaviorGraphData { m_stringData = strings } };
        int clips = ChrVariants.AddSelectorClips(graph, [(1, 0), (3, 0)]);
        int again = ChrVariants.AddSelectorClips(graph, [(1, 0)]);
        var names = idle.m_generators.OfType<hkbClipGenerator>().Select(c => c.m_animationName).ToList();
        var a001 = idle.m_generators.OfType<hkbClipGenerator>().FirstOrDefault(c => c.m_animationName == "a001_000000");
        check("variants: an AnimIdOffset selector gets a clip per added section (registered in the animation list), others are left alone, and twice adds nothing",
            clips == 2 && again == 0 && names.SequenceEqual(["a000_000000", "a001_000000", "a003_000000"])
            && a001 is { m_name: "a001_000000_hkx_AutoSet_01", m_animationInternalId: 2 } && strings.m_animationNames[2] == @"..\Model\chr\c7000\hkx\a001_000000.hkx"
            && noOffset.m_generators.Count == 1);

        // The row: DS3's offset from its resident effect; the template's offset effects come off.
        var spDef = new PARAMDEF { ParamType = "SP_EFFECT_PARAM_ST", Fields = [] };
        spDef.Fields.Add(new PARAMDEF.Field(spDef, PARAMDEF.DefType.s32, "animIdOffset"));
        var spear = new PARAM.Row(5404, "", spDef); spear["animIdOffset"]!.Value = 1000000;
        var light = new PARAM.Row(7010, "", spDef); light["animIdOffset"]!.Value = -1;
        var scholar = new PARAM.Row(5402, "", spDef); scholar["animIdOffset"]!.Value = 2000000;
        var effects = new[] { spear, light, scholar }.ToDictionary(r => r.ID);
        var ds3Def = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        for (int k = 0; k < 8; k++)
            ds3Def.Fields.Add(new PARAMDEF.Field(ds3Def, PARAMDEF.DefType.s32, $"spEffectId{k}"));
        ds3Def.Fields.Add(new PARAMDEF.Field(ds3Def, PARAMDEF.DefType.s32, "animIdOffset"));
        var spearKnight = new PARAM.Row(128010, "", ds3Def);
        spearKnight["spEffectId0"]!.Value = 7010; spearKnight["spEffectId1"]!.Value = 5404;
        var swordKnight = new PARAM.Row(128000, "", ds3Def);
        swordKnight["spEffectId0"]!.Value = 7010;
        check("variants: a DS3 row's offset is its resident effect's animIdOffset (0 without one)",
            ChrVariants.Ds3Offset(spearKnight, id => effects.GetValueOrDefault(id)) == 1000000 && ChrVariants.Ds3Offset(swordKnight, id => effects.GetValueOrDefault(id)) == 0);
        var erDef = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        for (int k = 0; k < 4; k++)
            erDef.Fields.Add(new PARAMDEF.Field(erDef, PARAMDEF.DefType.s32, $"spEffectID{k}"));
        var row = new PARAM.Row(70200004, "", erDef);
        row["spEffectID0"]!.Value = 5402; row["spEffectID1"]!.Value = 7010; row["spEffectID2"]!.Value = -1;
        var taken = ChrVariants.StripTemplateOffsets(row, id => effects.GetValueOrDefault(id));
        check("variants: the template's animation-offset effects come off a ported row, others stay",
            taken.SequenceEqual([5402]) && (int)row["spEffectID0"]!.Value == -1 && (int)row["spEffectID1"]!.Value == 7010);
    }
}
