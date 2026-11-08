using DarkSign.Porter.Maps;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// enemy-selftest: offline checks of the enemy porter's planning and conversion steps (no game files needed): template
/// choice, character and NpcParam IDs, archive renames, skin indices, the chr map it writes (read back by the map
/// port), TAE section keys, the TAE merge and the Fallbacks method switch.
/// </summary>
public static class EnemySelfTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"enemy-selftest: {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }

        // Bone match: Jaccard of names, case-insensitive.
        Check("bone match of equal skeletons is 1", ChrPort.BoneMatch(["Master", "Pelvis", "Spine"], ["master", "pelvis", "SPINE"]) == 1);
        Check("bone match of half-shared skeletons is 1/3", Math.Abs(ChrPort.BoneMatch(["A", "B"], ["B", "C"]) - 1.0 / 3) < 1e-9);
        Check("bone match with an empty skeleton is 0", ChrPort.BoneMatch([], ["A"]) == 0);

        // Template choice: the best skeleton wins; on equal skeletons the same kind and boss-ness win.
        var human = new[] { "Master", "Pelvis", "Spine", "Neck", "Head", "L_Thigh", "R_Thigh" };
        var dog = new[] { "Master", "Pelvis", "Tail1", "Tail2", "FrontLeg_L", "FrontLeg_R" };
        var knight = new EnemyPort.ErEnemy("c4300", 43000000, 43000000, "Banished Knight", false, "test");
        var soldier = new EnemyPort.ErEnemy("c4310", 43100000, 43100000, "Godrick Soldier", false, "test");
        var hound = new EnemyPort.ErEnemy("c4410", 44100000, 44100000, "Wolf", false, "test");
        var bones = new Dictionary<string, string[]> { ["c4300"] = human, ["c4310"] = human, ["c4410"] = dog };
        var ds3Knight = new ChrPort.Ds3Chr(1100, "Lothric Knight", false, [110000]);
        var pick = ChrPort.PickTemplate(ds3Knight, human, [hound, soldier, knight], e => bones[e.Model]);
        Check($"a DS3 knight gets a humanoid template ({pick?.Template.Model})", pick?.Template.Model is "c4300" or "c4310" && pick?.Match == 1);
        var ds3Dog = new ChrPort.Ds3Chr(1300, "Hound Rat", false, [130000]);
        Check("a DS3 four-legged enemy gets the four-legged template",
            ChrPort.PickTemplate(ds3Dog, dog, [knight, hound], e => bones[e.Model])?.Template.Model == "c4410");
        Check("no candidates with bones means no template", ChrPort.PickTemplate(ds3Dog, dog, [knight], _ => null) is null);

        // New character IDs: DS3 order, skipping IDs the game has, identical on every PC.
        var ids = ChrPort.AssignIds([1300, 1100, 1100, 1200], n => n == 7001);
        Check("character IDs go in DS3 order and skip taken ones (c1100->7000, c1200->7002, c1300->7003)",
            ids.Count == 3 && ids[1100] == 7000 && ids[1200] == 7002 && ids[1300] == 7003);
        Check("character IDs stop at c7999", ChrPort.AssignIds([1, 2], n => n < 7999).Count == 1);

        // Renames.
        Check("archive paths are renamed to the new ID",
            ChrPort.Rename(@"/chr/c4300_h.texbnd.dcx", 4300, 7000) == "/chr/c7000_h.texbnd.dcx"
            && ChrPort.Rename(@"N:\GR\data\INTERROOT_win64\chr\C4300\c4300.flver", 4300, 7000) == @"N:\GR\data\INTERROOT_win64\chr\c7000\c7000.flver");
        Check("texture names move to the new ID", ChrModel.TextureName("C1100_body_a", "c1100", "c7000") == "c7000_body_a");
        Check("model names give their character ID", ChrPort.ChrId("c4300") == 4300 && ChrPort.ChrId("m10_00") == -1);

        // Skin indices: DS3's per-mesh palettes become node indices.
        var flver = new FLVER2();
        var mesh = new FLVER2.Mesh { BoneIndices = [5, 9, 12] };
        var vertex = new FLVER.Vertex();
        vertex.BoneIndices[0] = 2;
        vertex.BoneIndices[1] = 0;
        vertex.BoneIndices[2] = 1;
        vertex.BoneIndices[3] = 40; // out of the palette
        mesh.Vertices.Add(vertex);
        flver.Meshes.Add(mesh);
        ChrPort.GlobalBoneIndices(flver);
        var v = flver.Meshes[0].Vertices[0];
        Check("palette skin indices become node indices (2,0,1 -> 12,5,9; out of range -> 0) and the palette is cleared",
            v.BoneIndices[0] == 12 && v.BoneIndices[1] == 5 && v.BoneIndices[2] == 9 && v.BoneIndices[3] == 0 && flver.Meshes[0].BoneIndices.Count == 0);

        // The chr map: what the map port reads back.
        var plan = new ChrPort.Plan(new ChrPort.Ds3Chr(1100, "Lothric Knight", false, [110000, 110010]), knight, 4300, 0.8, ChrPort.Method.Full, 7000);
        var rows = ChrPort.NpcRowIds(plan, [70000001]);
        Check("NpcParam rows are the new ID * 10000 and up, skipping used rows",
            rows.Count == 2 && rows[110000] == 70000000 && rows[110010] == 70000002);
        var map = EnemyPort.ChrMap.Parse(ChrPort.MapLines(plan, rows, 43000000).Prepend("# header"));
        var byNpc = map.Find(new EnemyPort.Ds3Enemy("c1100", 110010, "Lothric Knight", false, false));
        var byModel = map.Find(new EnemyPort.Ds3Enemy("c1100", 999, "Lothric Knight", true, false));
        Check("the chr map round-trips: a DS3 NpcParam row finds its ported row on the new model",
            byNpc is { Model: "c7000", NpcParam: 70000002, ThinkParam: 43000000 });
        Check("the chr map round-trips: an unlisted row of the same model falls back to the model line, boss kept",
            byModel is { Model: "c7000", NpcParam: 70000000, Boss: true } && map.Count == 3);
        var stats = ChrPort.MapLines(plan with { Method = ChrPort.Method.Stats }, rows, 43000000).First().Split('\t');
        Check("method C (stats) points the map at the template's model", stats[2] == "c4300");

        // The F1 menu's spawn list (TestMenu/src/spawn.c parses these lines).
        var spawn = SpawnList.Lines(ChrPort.MapLines(plan, rows, 43000000), [knight, knight, hound with { Boss = true }], new HashSet<string>());
        Check("spawn list: header, one DS3 line per ported character on the new model, Elden Ring deduplicated, bosses last",
            spawn.Count == 4 && spawn[0].StartsWith('#') && spawn[1] == "DS3\tc7000\t70000000\t43000000\t0\tLothric Knight"
            && spawn[2] == "ER\tc4300\t43000000\t43000000\t0\tBanished Knight" && spawn[3] == "ER\tc4410\t44100000\t44100000\t1\tWolf");

        // TAE keys.
        Check("TAE sections: a000.tae -> 0, a061.tae -> 61, c1100.tae -> 0",
            ChrPort.TaeSection("a000.tae") == 0 && ChrPort.TaeSection(@"N:\x\a061.tae") == 61 && ChrPort.TaeSection("c1100.tae") == 0);
        Check("TAE animation keys: 3000 in a000 -> (0,3000); 61003000 -> (61,3000)",
            ChrPort.AnimKey(3000, 0) == (0, 3000) && ChrPort.AnimKey(61003000, 0) == (61, 3000));

        // TAE merge: template behaviour windows kept and scaled to DS3's length, DS3's other events added when they fit.
        var anim = new TAE.Animation(3000, new TAE.Animation.AnimMiniHeader.Standard(), "a000_003000.hkx");
        var jumpTable = new TAE.EventGroup(0);
        anim.EventGroups.Add(jumpTable);
        anim.Events.Add(new TAE.Event(1f, 2f, 0, 0, new byte[16], false) { Group = jumpTable });
        anim.Events.Add(new TAE.Event(0.5f, 0.6f, 1, 0, new byte[48], false)); // template attack: replaced by DS3's
        var ds3Events = new List<TAE.Event>
        {
            new(0.7f, 0.9f, 1, 0, new byte[48], false), // DS3 attack, same size: kept
            new(0.1f, 0.2f, 2, 0, new byte[8], false), // wrong size for Elden Ring: dropped
            new(0.3f, 0.4f, 0, 0, new byte[16], false), // DS3 jump table: the template's stays
        };
        var merged = ChrTae.Merge(ds3Events, anim, new Dictionary<int, int> { [0] = 16, [1] = 48, [2] = 12 },
            new Dictionary<int, TAE.EventGroup> { [1] = new TAE.EventGroup(1) }, 3f, 1.5f);
        Check("TAE merge: template jump table kept and stretched to DS3's length (1-2s -> 2-4s)",
            merged.Count(e => e.Type == 0) == 1 && merged.First(e => e.Type == 0) is { StartTime: 2f, EndTime: 4f });
        Check("TAE merge: DS3's attack replaces the template's, wrong-size events dropped",
            merged.Count(e => e.Type == 1) == 1 && merged.First(e => e.Type == 1).StartTime == 0.7f && merged.All(e => e.Type != 2));
        Check("TAE merge: every event has a group", merged.All(e => e.Group is not null));

        // Every NpcParam row the maps and spawn list name exists, even when its params step failed (a missing row
        // crashed the game when a ported character loaded: 2026-11-07 test).
        var def = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.s32, "hp"));
        var npc = new PARAM { Rows = [] };
        npc.ApplyParamdef(def);
        var templateRow = new PARAM.Row(41500000, "template", def);
        templateRow["hp"]!.Value = 800;
        npc.Rows.Add(templateRow);
        npc.Rows.Add(new PARAM.Row(70640001, "already there", def));
        int fallback = ChrPort.EnsureNpcRows(npc, [(41500000, 70640000), (41500000, 70640001), (99, 70640002)]);
        Check("missing NpcParam rows are added as template copies (one added, existing and template-less ones left)",
            fallback == 1 && npc[70640000] is { } copy && Convert.ToInt32(copy["hp"]!.Value) == 800 && npc[70640002] is null
            && npc.Rows.Select(r => r.ID).SequenceEqual(npc.Rows.Select(r => r.ID).Order()));
        string described;
        try { _ = new List<int>()[3]; described = ""; }
        catch (Exception ex) { described = ChrPort.Describe(ex); }
        Check($"failures name the exception and where it came from ({described})",
            described.StartsWith("ArgumentOutOfRangeException", StringComparison.Ordinal) && described.Contains(" at ", StringComparison.Ordinal));

        // A model with nothing to draw is neither a template nor ported (the 6 "files failed" of 2026-11-07).
        Check("an empty model has no body", !ChrPort.HasBody(new FLVER2()));

        // Variants: Elden Ring's c3061 keeps its animations in c3060, DS3's c2191 plays c2190's (2026-11-07: 21 ports on
        // c3061 had 0 DS3 animations).
        var anims = new Dictionary<int, int> { [3060] = 190, [4150] = 117 };
        Check("a template without animations is built from its base (c3061 -> c3060), one with its own stays (c4150)",
            ChrPort.FileChr(3061, c => anims.GetValueOrDefault(c)) == 3060 && ChrPort.FileChr(4150, c => anims.GetValueOrDefault(c)) == 4150
            && ChrPort.FileChr(5021, c => anims.GetValueOrDefault(c)) == -1);
        Check("DS3 animations come from the character, then its base (c2191 -> c2191, c2190)",
            ChrPort.Ds3AnimationSources(2191).SequenceEqual([2191, 2190]) && ChrPort.Ds3AnimationSources(2190).SequenceEqual([2190]));

        // Fallbacks enemy-port switch.
        Check("method switch: unset/A -> full, model -> B, stats -> C, 0 -> off",
            ChrPort.ParseMethod(null) == ChrPort.Method.Full && ChrPort.ParseMethod("model") == ChrPort.Method.Model
            && ChrPort.ParseMethod("stats") == ChrPort.Method.Stats && ChrPort.ParseMethod("0") == ChrPort.Method.Off);

        Console.WriteLine(failures == 0 ? "enemy-selftest: all checks passed" : $"enemy-selftest: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
