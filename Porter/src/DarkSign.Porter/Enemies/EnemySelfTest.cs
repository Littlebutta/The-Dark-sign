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

        // The body of a character whose model isn't ported: by kind, never wildlife (the Undead Settlement's
        // Overgrown Skeletons were deer on 1.0.271: the deer shared the most bone names with them).
        var deer = new EnemyPort.ErEnemy("c6010", 60100000, 60100000, "Deer", false, "test");
        var erSkeleton = new EnemyPort.ErEnemy("c3500", 35000000, 35000000, "Large Skeleton", false, "test");
        var erBoss = new EnemyPort.ErEnemy("c4640", 46400000, 46400000, "Ulcerated Tree Spirit", true, "test");
        var ds3SkeletonBones = new[] { "Master", "Pelvis", "Tail1", "RootPos", "Sword" };
        var bodyBones = new Dictionary<string, string[]>(bones)
        {
            ["c6010"] = ["Master", "Pelvis", "Tail1", "Tail2"], ["c3500"] = ["Master", "Skull"], ["c4640"] = ["Master", "Root1"],
        };
        var ds3Skeleton = new ChrPort.Ds3Chr(1070, "Overgrown Skeleton - Scimitar - Undead Settlement", false, [107000]);
        Check("bone names alone would pick the deer",
            ChrPort.PickTemplate(ds3Skeleton, ds3SkeletonBones, [deer, erSkeleton, knight], e => bodyBones[e.Model])?.Template.Model == "c6010");
        Check("an unported DS3 skeleton stands as an Elden Ring skeleton",
            ChrPort.PickBody(ds3Skeleton, ds3SkeletonBones, [deer, erSkeleton, knight], e => bodyBones[e.Model])?.Template.Model == "c3500");
        var ds3Odd = new ChrPort.Ds3Chr(2150, "Crystal Lizard", false, [215000]);
        Check("with no Elden Ring enemy of its kind, the best-matching opponent stands in, not wildlife or a boss",
            ChrPort.PickBody(ds3Odd, dog, [deer, erBoss, knight, hound], e => bodyBones[e.Model])?.Template.Model == "c4410");
        var ds3Boss = new ChrPort.Ds3Chr(5270, "Dancer of the Boreal Valley", true, [527000]);
        Check("an unported DS3 boss stands as an Elden Ring boss",
            ChrPort.PickBody(ds3Boss, human, [deer, knight, erBoss], e => bodyBones[e.Model])?.Template.Model == "c4640");
        Check("only wildlife to choose from means no body", ChrPort.PickBody(ds3Skeleton, ds3SkeletonBones, [deer], e => bodyBones[e.Model]) is null);
        Check("wildlife and helper characters are bystanders, enemies are not",
            new[] { "Deer", "Eagle", "Talk Dummy", "Bullet Dummy", "Bonfire", "BuddyStone", "Ash of War Scarab", "Melina" }.All(DarkSign.Randomizer.Logic.EnemyKinds.IsBystander)
            && !new[] { "Warhawk", "Large Skeleton", "Giant Rat", "Wolf", "Howling Imp", "Goatman", "Exile Soldier" }.Where(DarkSign.Randomizer.Logic.EnemyKinds.IsBystander).Any());

        // New character IDs: DS3 order, skipping IDs the game has, identical on every PC.
        var ids = ChrPort.AssignIds([1300, 1100, 1100, 1200], n => n == 7001);
        Check("character IDs go in DS3 order and skip taken ones (c1100->7000, c1200->7002, c1300->7003)",
            ids.Count == 3 && ids[1100] == 7000 && ids[1200] == 7002 && ids[1300] == 7003);
        Check("character IDs stop at c7999", ChrPort.AssignIds([1, 2], n => n < 7999).Count == 1);

        // Renames.
        Check("archive paths are renamed to the new ID",
            ChrPort.Rename(@"/chr/c4300_h.texbnd.dcx", 4300, 7000) == "/chr/c7000_h.texbnd.dcx"
            && ChrPort.Rename(@"/action/script/c4300.hks", 4300, 7000) == "/action/script/c7000.hks"
            && ChrPort.Rename(@"N:\GR\data\INTERROOT_win64\chr\C4300\c4300.flver", 4300, 7000) == @"N:\GR\data\INTERROOT_win64\chr\c7000\c7000.flver");
        // Havok files name the character inside: the project data its Characters\c4300.hkx and Behaviors\c4300.hkx, the
        // character its animations. Renamed in place (same length), case kept, longer names and numbers left alone.
        var hkx = System.Text.Encoding.ASCII.GetBytes("Characters\\c4300.hkx\0..\\..\\chr\\C4300\\hkx\\a000_003000.hkx\0c43000\0xc4300\0c4300");
        int renamed = ChrPort.RenameInside(hkx, 4300, 7000);
        Check("Havok names inside a cloned file get the new ID (3 renamed, c43000 and xc4300 kept)",
            renamed == 3 && System.Text.Encoding.ASCII.GetString(hkx) == "Characters\\c7000.hkx\0..\\..\\chr\\C7000\\hkx\\a000_003000.hkx\0c43000\0xc4300\0c7000");
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
        var spawn = SpawnList.Lines(ChrPort.MapLines(plan, rows, 43000000), [knight, knight, hound with { Boss = true }], new HashSet<string>(),
            new HashSet<string> { "c7000" });
        Check("spawn list: header, one DS3 line per ported character on the new model, Elden Ring deduplicated, bosses last",
            spawn.Count == 4 && spawn[0].StartsWith('#') && spawn[1] == "DS3\tc7000\t70000000\t43000000\t0\tLothric Knight\tds3"
            && spawn[2] == "ER\tc4300\t43000000\t43000000\t0\tBanished Knight\ter" && spawn[3] == "ER\tc4410\t44100000\t44100000\t1\tWolf\ter");
        // Elden Ring ships c7000 itself (Fallen Hawks Soldier): a DS3 character left on that body is an Elden Ring
        // body, whatever its number (1.0.242: "Tree Woman" on c7000 was the one enemy listed as having its DS3 model).
        var hawk = new EnemyPort.ErEnemy("c7000", 70001065, 70001000, "Fallen Hawks Soldier", false, "test");
        var onHawk = new ChrPort.Plan(new ChrPort.Ds3Chr(6060, "Tree Woman", false, [606000]), hawk, 7000, 0.1, ChrPort.Method.Stats, 7093);
        var hawkSpawn = SpawnList.Lines(ChrPort.MapLines(onHawk, ChrPort.NpcRowIds(onHawk, []), 70001000), [], new HashSet<string>(), new HashSet<string>());
        Check("spawn list: a DS3 character left on Elden Ring's own c7000 is marked as an Elden Ring body",
            hawkSpawn.Count == 2 && hawkSpawn[1] == "DS3\tc7000\t70930000\t70001000\t0\tTree Woman\ter");

        // Files the game stores uncompressed (behaviour scripts) are read as they are; compressed ones are unpacked.
        var plainScript = "\u001bLuaS plain behaviour script"u8.ToArray();
        var (plainBytes, plainCompression) = GameArchives.SplitCompression(plainScript);
        Check("an uncompressed game file (action\\script\\cXXXX.hks) is read as it is, with no compression to write back",
            plainBytes.SequenceEqual(plainScript) && plainCompression.Type == DCX.Type.None);
        var packed = DCX.Compress(plainScript, new DCX.DcxDfltCompressionInfo(DCX.DfltCompressionPreset.DCX_DFLT_10000_44_9));
        var (unpacked, packedCompression) = GameArchives.SplitCompression(packed);
        Check("a compressed game file is unpacked and keeps its compression for writing back",
            unpacked.SequenceEqual(plainScript) && packedCompression.Type == DCX.Type.DCX_DFLT);

        // A build whose DS3 models all failed says so (1.0.234-1.0.242: 93 of 93 failed, the report read 0 FAIL).
        var failNotes = Enumerable.Range(0, 3).Select(i => $"c110{i} Hollow{ChrPort.FilesFailed}FormatException: Unknown DCX format Unknown. at X); using the template's body")
            .Append($"c1200 Dog{ChrPort.FilesFailed}InvalidDataException: no model); using the template's body").ToList();
        var problemText = ChrPort.FailureSummary(4, 1, failNotes);
        Check($"failed models are summed up with the reason most share ({problemText})",
            problemText is not null && problemText.StartsWith("4 of 5 DS3 enemy models", StringComparison.Ordinal)
            && problemText.Contains("(3 with: FormatException: Unknown DCX format Unknown. at X)", StringComparison.Ordinal)
            && ChrPort.FailureSummary(0, 5, []) is null);
        var reportLines = new[] { "DS3 chr\tname\tboss\ttemplate\ttemplate name\tbone match\tnew chr\tmethod\tnotes",
            "c1300\tRat\t0\tc4410\tWolf\t0.50\tc7002\tfull\t", "c1100\tHollow\t0\tc4300\tKnight\t0.50\t\tstats\t" }
            .Concat(failNotes.Select(n => "# " + n)).Append("# some other note").ToList();
        Check("map-validate reads the same failure back from the enemy report, and none from a clean one",
            ChrPort.ReportProblem(reportLines)?.StartsWith("4 of 5 DS3 enemy models", StringComparison.Ordinal) == true
            && ChrPort.ReportProblem(reportLines.Take(3)) is null);

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

        // The model: Elden Ring's node flags, culling boxes and a plain character material per set of DS3 textures.
        Check("DS3 cut-out materials are told by the words after the shader's name",
            ChrModel.IsAlphaTested(@"N:\FDP\data\Material\mtd\character\C[ARSN]_e_Decal.mtd") && ChrModel.IsAlphaTested("C[ARSN]_Edge.mtd")
            && !ChrModel.IsAlphaTested("C[ARSN]_em_Glow.mtd") && !ChrModel.IsAlphaTested("C[ARSN]_Decal.mtd") && !ChrModel.IsAlphaTested("C[ARSN].mtd"));
        Check("a character's own textures are told from shared detail maps",
            ChrMaterial.IsOwnTexture(@"N:\GR\data\Model\chr\c3060\tex\c3060_Skeleton_a.tif") && !ChrMaterial.IsOwnTexture(@"N:\GR\data\Model\parts\tex\AAT100_Bone_01_a.tif"));

        FLVER.Vertex Skinned(int bone, float x)
        {
            var sv = new FLVER.Vertex();
            sv.Positions.Add(new System.Numerics.Vector3(x, 1, 2));
            sv.BoneIndices[0] = bone;
            sv.BoneWeights[0] = 1;
            return sv;
        }
        FLVER2 Model(string mtd)
        {
            var model = new FLVER2();
            // 0 Master, 1 Pelvis (skinned, dummy), 2 Spine (skinned), 3 unused, 4 Dummy holder under 3... no: under 0, 5 mesh node.
            foreach (var (name, parent) in new[] { ("Master", -1), ("Pelvis", 0), ("Spine", 1), ("Unused", 0), ("Holder", 0), ("body", -1) })
                model.Nodes.Add(new FLVER.Node { Name = name, ParentIndex = (short)parent });
            model.Dummies.Add(new FLVER.Dummy { ReferenceID = 220, ParentBoneIndex = 4, AttachBoneIndex = 1 });
            model.GXLists.Add(new FLVER2.GXList());
            foreach (var matName in new[] { "body", "cloak" })
                model.Materials.Add(new FLVER2.Material { Name = matName, MTD = mtd, Textures =
                [
                    new FLVER2.Texture("g_DiffuseTexture", @"N:\FDP\chr\c1310\tex\c1310_a.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
                    new FLVER2.Texture("g_SpecularTexture", @"N:\FDP\chr\c1310\tex\c1310_r.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
                    new FLVER2.Texture("g_BumpmapTexture", @"N:\FDP\chr\c1310\tex\c1310_n.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
                ] });
            for (int mi = 0; mi < 2; mi++)
            {
                var body = new FLVER2.Mesh { MaterialIndex = mi, NodeIndex = 5, UseBoneWeights = true, BoneIndices = [1, 2] };
                body.Vertices.AddRange([Skinned(0, -1), Skinned(1, 3), Skinned(1, 1)]);
                body.FaceSets.Add(new FLVER2.FaceSet { Indices = [0, 1, 2] });
                model.Meshes.Add(body);
            }
            return model;
        }
        var erTemplate = Model(@"N:\GR\data\Material\mtd\Chr\matxml\C[c3060]_Body.matxml");
        erTemplate.Header.Version = 0x2001A;
        var ds3Model = Model(@"N:\FDP\data\Material\mtd\character\C[ARSN]_e_Decal.mtd");
        var plainMaterials = ChrModel.Convert(ds3Model, erTemplate, 1310, 7031, plainMaterials: true, erNodes: true);
        Check("node flags as Elden Ring has them: carrier 8, skinned + dummy 10, skinned 8, unused 1, dummy holder 2, mesh node 4 ("
              + string.Join(",", ds3Model.Nodes.Select(n => (int)n.Flags)) + ")",
            ds3Model.Nodes.Select(n => (int)n.Flags).SequenceEqual([8, 10, 8, 1, 2, 4]));
        var box = ds3Model.Meshes[0].BoundingBox;
        Check("mesh boxes are half size, no rotation, centre",
            box.Min == new System.Numerics.Vector3(2, 0, 0) && box.Max == System.Numerics.Vector3.Zero && box.Unk == new System.Numerics.Vector3(1, 1, 2));
        Check("meshes with the same DS3 textures share one plain character material",
            plainMaterials.Count == 1 && plainMaterials[0] is { Plain: true, AlphaTest: true, NewMatbin: "C[c7031]_ds3_0", Albedo: "c7031_a", Normal: "c7031_n", Metallic: "c7031_m" }
            && ds3Model.Materials.All(m => m.MTD == @"N:\GR\data\Material\mtd\Chr\matxml\C[c7031]_ds3_0.matxml"
                && m.Textures.Select(t => t.ParamName).SequenceEqual([ChrModel.PlainAlbedo, ChrModel.PlainMetallic, ChrModel.PlainNormal])));

        Check("display mask prefixes stay on material names", ChrModel.MaskPrefix("#01# spear") == "#01#" && ChrModel.MaskPrefix("c1280_body_mat") == ""
            && ChrModel.MaskPrefix("#00#  body") == "#00#");
        var ds3Masks = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        var erMasks = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        for (int k = 0; k < 3; k++)
        {
            ds3Masks.Fields.Add(new PARAMDEF.Field(ds3Masks, PARAMDEF.DefType.u8, $"ModelDispMask{k}"));
            erMasks.Fields.Add(new PARAMDEF.Field(erMasks, PARAMDEF.DefType.u8, $"modelDispMask{k}"));
        }
        var ds3MaskRow = new PARAM.Row(1, "ds3", ds3Masks);
        ds3MaskRow["ModelDispMask2"]!.Value = (byte)1;
        var erMaskRow = new PARAM.Row(2, "er", erMasks);
        erMaskRow["modelDispMask0"]!.Value = (byte)1;
        Check("DS3's display masks replace the template's (0,0,1)",
            ChrPort.CopyDisplayMasks(ds3MaskRow, erMaskRow) == 3
            && Enumerable.Range(0, 3).Select(k => System.Convert.ToInt32(erMaskRow[$"modelDispMask{k}"]!.Value)).SequenceEqual([0, 0, 1]));

        MATBIN Matbin(string source, params (string Type, string Path)[] samplers)
        {
            var mb = new MATBIN { ShaderPath = @"N:\GR\data\Material\sat\OutputData\SPX\C[AMSN].spx", SourcePath = source, Params = [], Samplers = [] };
            mb.Params.Add(new MATBIN.Param { Name = "g_AlphaRef", Type = MATBIN.ParamType.Int2, Value = new[] { 128, 128 } });
            mb.Params.Add(new MATBIN.Param { Name = "C_AMSN__snp_0_color_0", Type = MATBIN.ParamType.Float5, Value = new[] { 0.2f, 0.2f, 0.2f, 1f, 3f } });
            foreach (var (type, path) in samplers)
                mb.Samplers.Add(new MATBIN.Sampler { Type = type, Path = path });
            return mb;
        }
        var horse = Matbin(@"N:\GR\data\Material\mtd\Chr\matxml\C[c3250]_Horse_Body.matxml",
            (ChrModel.PlainAlbedo, @"N:\GR\data\Model\chr\c3250\tex\c3250_Horse_a.tif"), (ChrModel.PlainMetallic, @"N:\GR\data\Model\chr\c3250\tex\c3250_Horse_m.tif"),
            (ChrModel.PlainNormal, @"N:\GR\data\Model\chr\c3250\tex\c3250_Horse_n.tif"));
        int named = (plainMaterials[0] with { Metallic = null }).Apply(horse);
        Check("a plain material names DS3's textures, clears what DS3 lacks, resets the colour and cuts out at 50",
            named == 2 && horse.Samplers[0].Path == @"N:\GR\data\Model\chr\c7031\tex\c7031_a.tif" && horse.Samplers[1].Path == ""
            && horse.Samplers[2].Path == @"N:\GR\data\Model\chr\c7031\tex\c7031_n.tif"
            && ((int[])horse.Params[0].Value).SequenceEqual([50, 0]) && ((float[])horse.Params[1].Value).SequenceEqual([1f, 1f, 1f, 1f, 1f])
            && horse.SourcePath == @"N:\GR\data\Material\mtd\Chr\matxml\C[c7031]_ds3_0.matxml");

        // Fallback enemy-model B: the template's material, with its own textures swapped and shared detail maps kept
        // (up to 1.0.255 nothing was swapped: every ported enemy wore its template's textures).
        var ds3Model2 = Model(@"N:\FDP\data\Material\mtd\character\C[ARSN]_Decal.mtd");
        var templateMaterials = ChrModel.Convert(ds3Model2, erTemplate, 1310, 7031, plainMaterials: false, erNodes: false);
        var beast = Matbin(@"N:\GR\data\Material\mtd\Chr\matxml\C[c3060]_Body.matxml",
            ("C_DetailBlend__snp_Texture2D_7_AlbedoMap", @"N:\GR\data\Model\chr\c3060\tex\c3060_Skeleton_a.tif"),
            ("C_DetailBlend__snp_Texture2D_1_AlbedoMap", @"N:\GR\data\Model\parts\tex\AAT100_Bone_01_a.tif"));
        int swapped = templateMaterials[0].Apply(beast);
        Check("template materials: one per DS3 material, own textures swapped, detail maps and values kept, DS3 node flags kept",
            templateMaterials.Count == 2 && !templateMaterials[0].Plain && templateMaterials[0].NewMatbin == "C[c3060]_Body_c7031_0" && swapped == 1
            && beast.Samplers[0].Path == @"N:\GR\data\Model\chr\c3060\tex\c7031_a.tif" && beast.Samplers[1].Path.EndsWith("AAT100_Bone_01_a.tif", StringComparison.Ordinal)
            && ((int[])beast.Params[0].Value).SequenceEqual([128, 128]) && ds3Model2.Nodes.All(n => n.Flags == 0));

        // The skeleton: every DS3 bone gets a bone to follow.
        HKLib.hk2018.hkaSkeleton Template()
        {
            var sk = new HKLib.hk2018.hkaSkeleton();
            foreach (var (name, parent) in new[] { ("Master", -1), ("Pelvis", 0), ("Neck", 1), ("Head", 2), ("Jaw", 3), ("Chain", 1) })
            {
                sk.m_bones.Add(new HKLib.hk2018.hkaBone { m_name = name });
                sk.m_parentIndices.Add((short)parent);
                sk.m_referencePose.Add(new HKLib.hk2018.hkQsTransform { m_rotation = System.Numerics.Quaternion.Identity });
            }
            return sk;
        }
        var ds3Bones = new[] { ("Master", -1), ("Pelvis", 0), ("Neck", 1), ("Neck1", 2), ("Head", 3), ("Jaw", 4), ("R_weapon", 1) };
        var ds3Sk = new AnimPort.Ds3Skeleton
        {
            Names = ds3Bones.Select(b => b.Item1).ToList(),
            Parents = ds3Bones.Select(b => (short)b.Item2).ToList(),
            ReferencePose = ds3Bones.Select((_, i) => new SoulsAssetPipeline.Animation.NewBlendableTransform
                { Translation = new System.Numerics.Vector3(i, 0, 0), Rotation = System.Numerics.Quaternion.Identity, Scale = System.Numerics.Vector3.One }).ToList(),
        };
        var merged1 = Template();
        var mergeResult = ChrPort.MergeSkeleton(merged1, ds3Sk, addBones: true);
        var mergedNames = merged1.m_bones.Select(b => b.m_name).ToList();
        Check($"DS3 bones the template lacks are added under their DS3 parent; a bone under another parent is set aside ({string.Join(",", mergedNames)})",
            mergeResult == new ChrPort.SkeletonMerge(3, 4, 2)
            && mergedNames.SequenceEqual(["Master", "Pelvis", "Neck", "Head_er", "Jaw_er", "Chain", "Neck1", "Head", "Jaw", "R_weapon"])
            && merged1.m_parentIndices.SequenceEqual(new short[] { -1, 0, 1, 2, 3, 1, 2, 6, 7, 1 })
            && merged1.m_referencePose[9].m_translation.X == 6 && merged1.m_referencePose[2].m_translation.X == 2
            && Enumerable.Range(0, merged1.m_bones.Count).All(i => merged1.m_parentIndices[i] < i));
        var merged2 = Template();
        Check("fallback enemy-model D: only shared bones take DS3's rest position, none added",
            ChrPort.MergeSkeleton(merged2, ds3Sk, addBones: false) == new ChrPort.SkeletonMerge(5, 0, 0) && merged2.m_bones.Count == 6
            && merged2.m_referencePose[3].m_translation.X == 4);

        // Textures under another character's ID.
        var dogModel = new FLVER2();
        dogModel.Materials.Add(new FLVER2.Material { Name = "body", MTD = "C[ARSN].mtd", Textures =
        [
            new FLVER2.Texture("g_DiffuseTexture", @"N:\FDP\data\Model\chr\c2029\tex\c2029_body_a.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
            new FLVER2.Texture("g_BumpmapTexture", @"N:\FDP\data\Model\chr\c2020\tex\c2020_n.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
            new FLVER2.Texture("g_DetailBumpmapTexture", @"N:\FDP\data\Other\SysTex\SYSTEX_DummyNormal.tga", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
        ] });
        Check("textures a model names under another character's ID are found (c2020 wears c2029's)",
            ChrModel.TextureOwners(dogModel, 2020).SequenceEqual([2029]) && ChrModel.TextureNames(dogModel).SetEquals(["c2029_body_a", "c2020_n", "SYSTEX_DummyNormal"]));

        Console.WriteLine(failures == 0 ? "enemy-selftest: all checks passed" : $"enemy-selftest: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
