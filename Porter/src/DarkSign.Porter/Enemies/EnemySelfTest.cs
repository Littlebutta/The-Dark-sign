using DarkSign.Porter.Maps;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// enemy-selftest: offline checks of the enemy porter's planning and conversion steps (no game files needed): template
/// choice, character and NpcParam IDs, archive renames, skin indices, the chr map it writes (read back by the map
/// port), TAE section keys and the TAE merge.
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

        // Template choice: the template that has DS3's bones, stands as big and is the same kind wins.
        ChrPort.Shape Sk(float size, params (string, int)[] b) => new(b.Select(x => x.Item1).ToList(), b.Select(x => (short)x.Item2).ToList(), size);
        var human = Sk(2.0f, ("Master", -1), ("Pelvis", 0), ("Spine", 1), ("Neck", 2), ("Head", 3), ("L_Thigh", 1), ("R_Thigh", 1));
        var imp = Sk(1.1f, ("Master", -1), ("Pelvis", 0), ("Spine", 1), ("Neck", 2), ("Head", 3), ("L_Thigh", 1), ("R_Thigh", 1));
        var dog = Sk(1.2f, ("Master", -1), ("Pelvis", 0), ("Tail1", 1), ("Tail2", 2), ("FrontLeg_L", 1), ("FrontLeg_R", 1));
        var knight = new EnemyPort.ErEnemy("c4300", 43000000, 43000000, "Banished Knight", false, "test");
        var soldier = new EnemyPort.ErEnemy("c4310", 43100000, 43100000, "Godrick Soldier", false, "test");
        var hound = new EnemyPort.ErEnemy("c4410", 44100000, 44100000, "Wolf", false, "test");
        var impEnemy = new EnemyPort.ErEnemy("c3080", 30800000, 30800000, "Imp", false, "test");
        var shapes = new Dictionary<string, ChrPort.Shape> { ["c4300"] = human, ["c4310"] = human, ["c4410"] = dog, ["c3080"] = imp };
        // Behaviour variations stay five digits (both games' highest: Elden Ring 91000, DS3 99900). Up to 1.0.368 a
        // ported enemy got its character ID * 100 (Pontiff c7105 = 710500, rows 910500010...) and no hit found its row.
        Check("attack variation: a ported enemy gets its character ID * 10, the next free one of its ten when taken, never six digits",
            AttackParamPort.ChooseVariation(new HashSet<int>(), 7105 * 10) == 71050
            && AttackParamPort.ChooseVariation(new HashSet<int> { 70000 }, 7000 * 10) == 70001
            && AttackParamPort.ChooseVariation(new HashSet<int> { 71050 }, 7105 * 10) == 71051
            && AttackParamPort.ChooseVariation(new HashSet<int>(), 7105 * 100) == 9000
            && AttackParamPort.RowId(2, 71050, 10) == 271050010 && AttackParamPort.RowId(2, 710500, 10) == -1);
        // Material numbers as Elden Ring's models carry them (vanilla c4340: 0-12, 2, 14-17). DS3 leaves 0 in every
        // material, and up to 1.0.383 ported models kept those 0s: enemies drew untextured until frostbite redrew them.
        var numbered = new FLVER2();
        foreach (var (name, mtd, tex) in new[] { ("body", "C[a].matxml", ""), ("head", "C[b].matxml", ""), ("wire", "C[c].matxml", ""),
                     ("wire2", "c[C].MATXML", ""), ("other", "C[c].matxml", "x.tif") })
        {
            var m = new FLVER2.Material(name, mtd, 0);
            m.Textures.Add(new FLVER2.Texture("g_DiffuseTexture", tex, System.Numerics.Vector2.One, 0, 0, 0, 0, 0));
            numbered.Materials.Add(m);
        }
        int renumbered = FlverMaterialNumbers.Apply(numbered);
        Check("material numbers: each material gets the position of the first one with the same material file, as Elden Ring's own models (0 1 2 2 4)",
            string.Join(" ", numbered.Materials.Select(m => m.Index)) == "0 1 2 2 4" && renumbered == 4 && FlverMaterialNumbers.Apply(numbered) == 0);
        Check("kept bones: equal skeletons keep all, names are case-insensitive",
            ChrPort.KeptBones(human, Sk(2, ("master", -1), ("PELVIS", 0), ("Spine", 1))) == 3);
        Check("kept bones: a bone under another parent that comes earlier is kept, under a new bone it is not",
            ChrPort.KeptBones(human, Sk(2, ("Master", -1), ("Pelvis", 0), ("Spine", 0), ("Neck1", 2), ("Neck", 3))) == 3);
        Check("kept bones without parents count the names both have", ChrPort.KeptBones(new ChrPort.Shape(["A", "B"], []), new ChrPort.Shape(["B", "C"], [])) == 1);
        Check("size match: equal 1, half 0.5, unknown 0.5", ChrPort.SizeMatch(2, 2) == 1 && ChrPort.SizeMatch(1, 2) == 0.5 && ChrPort.SizeMatch(0, 2) == 0.5);
        var ds3Knight = new ChrPort.Ds3Chr(1100, "Lothric Knight", false, [110000]);
        var pick = ChrPort.PickTemplate(ds3Knight, human, [hound, impEnemy, soldier, knight], e => shapes[e.Model]);
        Check($"a DS3 knight gets the knight, not the Imp with the same bone names or the soldier ({pick?.Template.Model})", pick?.Template.Model == "c4300" && pick?.Match == 1);
        var ds3Jailer = new ChrPort.Ds3Chr(2230, "Jailer", false, [223000]);
        Check("a man-sized DS3 enemy with no kind gets a man-sized template, not the Imp",
            ChrPort.PickTemplate(ds3Jailer, human, [impEnemy, soldier], e => shapes[e.Model])?.Template.Model == "c4310");
        var dragon = new EnemyPort.ErEnemy("c4500", 45000000, 45000000, "Flying Dragon", false, "test");
        var skeletonEnemy = new EnemyPort.ErEnemy("c3500", 35000000, 35000000, "Large Skeleton", false, "test");
        var other = Sk(2.0f, ("Root", -1), ("Hips", 0), ("Chest", 1));
        shapes["c4500"] = human with { Size = 60 };
        shapes["c3500"] = other;
        var ranked = ChrPort.RankTemplates(new ChrPort.Ds3Chr(1070, "Skeleton", false, [107000]), human, [dragon, skeletonEnemy], e => shapes[e.Model]);
        Check("a template of a wholly different size comes last even with every bone (a dragon's files for a skeleton), and the pick then has too few bones for the DS3 model",
            ranked[0].Template.Model == "c3500" && ranked[0].Match < ChrPort.MinBoneMatch && ranked[1].Match == 1);
        Check("a template with DS3's bones comes before a better-scoring one that cannot carry the DS3 model",
            ChrPort.PickTemplate(new ChrPort.Ds3Chr(1310, "Outrider Knight", false, [131000]), human with { Size = 4 }, [knight, skeletonEnemy], e => e.Model == "c4300" ? human : other with { Size = 4 })?.Template.Model == "c4300");
        var hollow = new ChrPort.Shape(["Master", "RootPos", "Pelvis"], [-1, 0, 1], 2.7f, 1.6f, 323);
        var donors = ChrPort.PickAnimationDonors(new Dictionary<int, ChrPort.Shape>
        {
            [1100] = hollow, [1101] = hollow with { Moves = 323 }, [1105] = hollow with { Size = 3.6f, Head = 2.2f, Moves = 250 },
            [1430] = hollow with { Moves = 0 }, [1445] = hollow with { Size = 3.6f, Head = 2.2f, Moves = 0 },
            [2020] = new(["Master", "Root", "Tail"], [-1, 0, 1], 2f, 0, 81), [2290] = new(["master", "root", "tail"], [-1, 0, 1], 2f, 0, 0),
            [3000] = new(["Master", "Root", "Tail"], [-1, 0, 0], 2f, 0, 0), [1240] = new(["Master"], [-1], 0, 0, 0),
        });
        Check($"shared moves: a DS3 character with no animations plays those of the character with its skeleton, the one of its size ({string.Join(",", donors.OrderBy(d => d.Key).Select(d => $"{d.Key}>{d.Value}"))})",
            donors.Count == 3 && donors[1430] == 1100 && donors[1445] == 1105 && donors[2290] == 2020);
        ChrPort.AnimationDonors[1430] = 1100;
        bool sourcesOk = ChrPort.Ds3AnimationSources(1431).SequenceEqual([1431, 1430]) && ChrPort.Ds3AnimationSources(1430).SequenceEqual([1430, 1100]);
        ChrPort.AnimationDonors.Clear();
        Check("shared moves: the donor's binders are read after the character's own and its base's; the a999 placeholder is no move",
            sourcesOk && ChrPort.IsMoveFile("a000_003000.hkx") && !ChrPort.IsMoveFile("a999_999999.hkx") && !ChrPort.IsMoveFile("skeleton.hkx"));
        var lostMoves = new Dictionary<(int Section, int Id), ChrPort.Ds3Move>
        {
            [(0, 0)] = new([], (0, 0)), [(0, 3000)] = new([], (0, 3000)), [(0, 3001)] = new([], (0, 3000)),
            [(0, 3005)] = new([], (0, 3005)), [(0, 6001)] = new([], (0, 6001)), [(1, 3005)] = new([], (1, 3005)),
        };
        var lost = ChrPort.LostMoves(lostMoves, lostMoves.Keys, [(0, 0), (0, 3000)]);
        Check($"lost moves: a DS3 move whose clip plays nowhere is listed, one that shares a played clip or sits in a variant section is not ({ChrPort.LostSummary(lost)})",
            lost.SequenceEqual([3005, 6001]) && ChrPort.LostSummary(lost) == "3xxx x1 (3005) 6xxx x1 (6001)");
        var markerDir = Path.Combine(Path.GetTempPath(), "darksign-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(markerDir);
        try
        {
            var marker = Path.Combine(markerDir, "ItemlotParam.xml.missing");
            File.WriteAllText(marker, "https://x/ER/Defs/ItemlotParam.xml");
            Check("download cache: a not-found marker answers only for its own address (Windows reads ItemLotParam.xml.missing as ItemlotParam.xml.missing)",
                ExternalData.MarkedMissing(marker, "https://x/ER/Defs/ItemlotParam.xml") && !ExternalData.MarkedMissing(marker, "https://x/ER/Defs/ItemLotParam.xml")
                && !ExternalData.MarkedMissing(Path.Combine(markerDir, "none.missing"), "https://x/ER/Defs/ItemlotParam.xml"));
        }
        finally
        {
            Directory.Delete(markerDir, true);
        }
        Check("size: by head height when both have a head, by the box otherwise",
            ChrPort.SizeMatch(new ChrPort.Shape([], [], 2, 1), new ChrPort.Shape([], [], 2, 2)) == 0.5 && ChrPort.SizeMatch(new ChrPort.Shape([], [], 2, 0), new ChrPort.Shape([], [], 2, 2)) == 1);
        var rest = ChrPort.RestSize(["Master", "Pelvis", "Head", "Throw_Target2"], [-1, 0, 1, 0],
            [(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity), (new(0, 1, 0), System.Numerics.Quaternion.Identity), (new(0, 0.7f, 0), System.Numerics.Quaternion.Identity), (new(0, 30, 0), System.Numerics.Quaternion.Identity)]);
        Check($"rest size: head height adds up the chain ({rest.Head})", Math.Abs(rest.Head - 1.7f) < 1e-5);
        var ds3Dog = new ChrPort.Ds3Chr(1300, "Hound Rat", false, [130000]);
        Check("a DS3 four-legged enemy gets the four-legged template",
            ChrPort.PickTemplate(ds3Dog, dog, [knight, hound], e => shapes[e.Model])?.Template.Model == "c4410");
        Check("no candidates with a skeleton means no template", ChrPort.PickTemplate(ds3Dog, dog, [knight], _ => null) is null);

        // Moves: DS3's own move of the same ID, else the nearest DS3 move of the same kind, never the template's.
        var have = new HashSet<(int, int)> { (0, 0), (0, 2000), (0, 2001), (0, 3000), (0, 3004), (0, 3006), (0, 5000), (0, 5001), (0, 8000), (0, 8001), (0, 8021), (0, 10000), (1, 8000) };
        Check("moves: the same ID wins, in its section first", ChrPort.ChooseMove((0, 3004), have) == (0, 3004) && ChrPort.ChooseMove((1, 8000), have) == (1, 8000)
            && ChrPort.ChooseMove((1, 3000), have) == (0, 3000));
        Check("moves: a template attack DS3 lacks plays the nearest DS3 attack, same last digit first (3016 -> 3006, 3003 -> 3004)",
            ChrPort.ChooseMove((0, 3016), have) == (0, 3006) && ChrPort.ChooseMove((0, 3003), have) == (0, 3004));
        Check("moves: being hit from a side keeps the side (8031 -> 8021), a dodge plays a step (6001 -> 5001), a second death the first (11010 -> 10000)",
            ChrPort.ChooseMove((0, 8031), have) == (0, 8021) && ChrPort.ChooseMove((0, 6001), have) == (0, 5001) && ChrPort.ChooseMove((0, 11010), have) == (0, 10000));
        Check("moves: a kind DS3 has nothing of plays its idle (13000 -> 0)", ChrPort.ChooseMove((0, 13000), have) == (0, 0));
        var moveTae = new TAE { Format = TAE.TAEFormat.DS3, Animations = [] };
        var idleEvents = new List<TAE.Event> { new(0, 1, 16, 0, new byte[16], false) };
        moveTae.Animations.Add(new TAE.Animation(0, new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = 20 }, "a000_000000.hkt") { Events = idleEvents });
        moveTae.Animations.Add(new TAE.Animation(20, new TAE.Animation.AnimMiniHeader.Standard(), "a000_000020.hkt") { Events = [] });
        moveTae.Animations.Add(new TAE.Animation(2300, new TAE.Animation.AnimMiniHeader.ImportOtherAnim { ImportFromAnimID = 0 }, "a000_002300.hkt") { Events = [] });
        moveTae.Animations.Add(new TAE.Animation(7401, new TAE.Animation.AnimMiniHeader.Standard(), "a000_007401.hkt") { Events = [] });
        var ds3Moves = ChrPort.Ds3Moves([(0, moveTae)], new HashSet<(int, int)> { (0, 20), (0, 3000) });
        Check("DS3 moves: an entry that borrows a file plays it (idle 0 -> file 20), one that plays another entry follows it, one without a file is left out, a file without an entry is a move",
            ds3Moves.Count == 4 && ds3Moves[(0, 0)].Hkx == (0, 20) && ReferenceEquals(ds3Moves[(0, 0)].Events, idleEvents)
            && ds3Moves[(0, 2300)].Hkx == (0, 20) && ReferenceEquals(ds3Moves[(0, 2300)].Events, idleEvents)
            && !ds3Moves.ContainsKey((0, 7401)) && ds3Moves[(0, 3000)].Hkx == (0, 3000));
        Check("an Elden Ring entry's file: its own, the one it borrows, none when it plays another entry",
            ChrPort.ErHkx(moveTae.Animations[1], 0) == (0, 20) && ChrPort.ErHkx(moveTae.Animations[0], 0) == (0, 20) && ChrPort.ErHkx(moveTae.Animations[2], 0) is null);

        // The body of a character whose model isn't ported: by kind, never wildlife (the Undead Settlement's
        // Overgrown Skeletons were deer on 1.0.271: the deer shared the most bone names with them).
        var deer = new EnemyPort.ErEnemy("c6010", 60100000, 60100000, "Deer", false, "test");
        var erSkeleton = new EnemyPort.ErEnemy("c3500", 35000000, 35000000, "Large Skeleton", false, "test");
        var erBoss = new EnemyPort.ErEnemy("c4640", 46400000, 46400000, "Ulcerated Tree Spirit", true, "test");
        var ds3SkeletonBones = new[] { "Master", "Pelvis", "Tail1", "RootPos", "Sword" };
        var bodyBones = new Dictionary<string, IReadOnlyList<string>>(shapes.ToDictionary(kv => kv.Key, kv => kv.Value.Names))
        {
            ["c6010"] = ["Master", "Pelvis", "Tail1", "Tail2"], ["c3500"] = ["Master", "Skull"], ["c4640"] = ["Master", "Root1"],
        };
        var ds3Skeleton = new ChrPort.Ds3Chr(1070, "Overgrown Skeleton - Scimitar - Undead Settlement", false, [107000]);
        Check("bone names alone would pick the deer",
            ChrPort.BoneMatch(ds3SkeletonBones, bodyBones["c6010"]) > ChrPort.BoneMatch(ds3SkeletonBones, bodyBones["c3500"]));
        Check("an unported DS3 skeleton stands as an Elden Ring skeleton",
            ChrPort.PickBody(ds3Skeleton, ds3SkeletonBones, [deer, erSkeleton, knight], e => bodyBones[e.Model])?.Template.Model == "c3500");
        var ds3Odd = new ChrPort.Ds3Chr(2150, "Crystal Lizard", false, [215000]);
        Check("with no Elden Ring enemy of its kind, the best-matching opponent stands in, not wildlife or a boss",
            ChrPort.PickBody(ds3Odd, dog.Names, [deer, erBoss, knight, hound], e => bodyBones[e.Model])?.Template.Model == "c4410");
        var ds3Boss = new ChrPort.Ds3Chr(5270, "Dancer of the Boreal Valley", true, [527000]);
        Check("an unported DS3 boss stands as an Elden Ring boss",
            ChrPort.PickBody(ds3Boss, human.Names, [deer, knight, erBoss], e => bodyBones[e.Model])?.Template.Model == "c4640");
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
        // Every shipped texture carries its own model's name (texture-names, 2026-11-12: c1100's textures in six ported
        // hollows, c3060_* in c7049 like Elden Ring's own c3060, dummy_n in c7094 and a weapon).
        var shippedNames = new HashSet<string>(["c1105_body_a", "c1100_0203_a", "dummy_n", "SYSTEX_DummyNormal"], StringComparer.OrdinalIgnoreCase);
        var ownNames = ChrModel.OwnedNames(name => ChrModel.TextureName(name, "c1105", "c7007"), "c7007", shippedNames);
        Check("shipped textures carry the character's own ID (own, borrowed, generic; engine textures and unshipped names kept)",
            ownNames("c1105_body_a") == "c7007_body_a" && ownNames("c1100_0203_a") == "c7007_c1100_0203_a" && ownNames("dummy_n") == "c7007_dummy_n"
            && ownNames("SYSTEX_DummyNormal") == "SYSTEX_DummyNormal" && ownNames("c1100_other_n") == "c1100_other_n"
            && ChrModel.Owned(ChrModel.Owned("x_a", "BD_M_5000_L"), "BD_M_5000_L") == "BD_M_5000_L_x_a");
        Check("texture-name check: own tags and model keys of ported files",
            TextureNames.OwnTag("/chr/c7007_h.texbnd.dcx") == "c7007" && TextureNames.OwnTag("/parts/bd_f_5003_l.partsbnd.dcx") == "BD_F_5003_L"
            && TextureNames.OwnTag("/parts/bd_m_1290.partsbnd.dcx") is null && TextureNames.OwnTag("/parts/wp_a_9174.partsbnd.dcx") is null
            && TextureNames.ModelKey("/parts/bd_f_5003_l.partsbnd.dcx") == TextureNames.ModelKey("/parts/bd_m_5003.partsbnd.dcx")
            && TextureNames.ModelKey("/chr/c7007_h.texbnd.dcx") != TextureNames.ModelKey("/chr/c7008_h.texbnd.dcx"));
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

        // The test set: one fully ported character built stage by stage, IDs from the top of the pool.
        var jailer = plan with { Ds3 = new ChrPort.Ds3Chr(ChrPort.TestChrs[0], "Jailer", false, [223000]), Method = ChrPort.Method.Full, NewChr = 7001 };
        var staged = ChrPort.TestPlans([plan with { NewChr = 7000 }, jailer], n => n == 7998);
        Check("test set: five stages before the full port, on free IDs from c7999 down, for the test character only",
            staged.Select(t => t.Stage).SequenceEqual([ChrPort.Stage.Clone, ChrPort.Stage.Model, ChrPort.Stage.Skeleton, ChrPort.Stage.Moves, ChrPort.Stage.Events])
            && staged.Select(t => t.NewChr).SequenceEqual([7999, 7997, 7996, 7995, 7994]) && staged.All(t => t.Ds3.Chr == ChrPort.TestChrs[0]));
        Check("test set: none for a test character left on an Elden Ring body",
            ChrPort.TestPlans([jailer with { Method = ChrPort.Method.Stats }], _ => false).Count == 0);
        var withTests = SpawnList.Lines([], [knight], new HashSet<string>(), new HashSet<string>(),
            [SpawnList.Line("DS3", "c7999", 79990000, 1, false, "TEST 1 Jailer: Elden Ring template files only", true)]);
        Check("spawn list: test lines come first, shown with the DS3 models", withTests.Count == 3 && withTests[1].StartsWith("DS3\tc7999\t79990000\t") && withTests[1].EndsWith("\tds3"));

        // The F1 menu's spawn list (TestMenu/src/spawn.c parses these lines).
        var spawn = SpawnList.Lines(ChrPort.MapLines(plan, rows, 43000000), [knight, knight, hound with { Boss = true }], new HashSet<string>(),
            new HashSet<string> { "c7000" });
        Check("spawn list: header, one DS3 line per ported character on the new model, Elden Ring deduplicated, bosses last",
            spawn.Count == 4 && spawn[0].StartsWith('#') && spawn[1] == "DS3\tc7000\t70000000\t43000000\t0\tLothric Knight\tds3"
            && spawn[2] == "ER\tc4300\t43000000\t43000000\t0\tBanished Knight\ter" && spawn[3] == "ER\tc4410\t44100000\t44100000\t1\tWolf\ter");
        // High Wall's Pus of Man grows out of a hollow: its line names the hollow's rows for the menu to spawn first.
        var pusMap = new[]
        {
            "npc\t110070\tc7006\t70060011\t70069000\tHollow Soldier", "think\t110054\tc7006\t70069054\tHollow Soldier",
            "chr\tc1100\tc7006\t70060000\t70069000\tHollow Soldier",
            "npc\t109000\tc7005\t70050000\t70059000\tPus of Man", "chr\tc1090\tc7005\t70050000\t70059000\tPus of Man",
        };
        var pusSpawn = SpawnList.Lines(pusMap, [], new HashSet<string>(), new HashSet<string> { "c7005", "c7006" });
        Check("spawn list: the Pus of Man names the hollow it bursts out of (DS3 NpcParam 110070, ThinkParam 110054), the hollow names none",
            pusSpawn.Contains("DS3\tc7005\t70050000\t70059000\t0\tPus of Man\tds3\tfrom c7006:70060011:70069054")
            && pusSpawn.Contains("DS3\tc7006\t70060000\t70069000\t0\tHollow Soldier\tds3"));
        var noHollow = SpawnList.Lines(pusMap.Skip(3), [], new HashSet<string>(), new HashSet<string> { "c7005" });
        Check("spawn list: without the hollow's rows the Pus of Man spawns as itself",
            noHollow.Contains("DS3\tc7005\t70050000\t70059000\t0\tPus of Man\tds3"));
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
        // A template death move's kill/disappear flags never ride on a DS3 move that doesn't die (Gundyr's stand-up
        // 20002 on c3060's death move 20002: JumpTable 12 Kill Character, 46 Disappear Character).
        static byte[] Jt(int id) { var b = new byte[16]; BitConverter.GetBytes(id).CopyTo(b, 0); return b; }
        var deathAnim = new TAE.Animation(20002, new TAE.Animation.AnimMiniHeader.Standard(), "a000_020002.hkx");
        var deathJt = new TAE.EventGroup(0);
        deathAnim.EventGroups.Add(deathJt);
        foreach (var (id, t) in new[] { (12, 0.8f), (46, 4.9f), (49, 0f) })
            deathAnim.Events.Add(new TAE.Event(t, t + 0.1f, 0, 0, Jt(id), false) { Group = deathJt });
        var lethalDropped = new Dictionary<int, int>();
        var standUp = ChrTae.Merge([new(3.6f, 4.6f, 231, 0, new byte[16], false)], deathAnim, new Dictionary<int, int> { [0] = 16 },
            new Dictionary<int, TAE.EventGroup>(), 5f, 5f, lethalDropped: lethalDropped);
        Check("TAE merge: template JumpTables 12/46 (kill, disappear) left off a DS3 move that doesn't die; 49 kept",
            standUp.Where(e => e.Type == 0).Select(ChrTae.JumpTableId).SequenceEqual([49]) && lethalDropped.Count == 2);
        var dying = ChrTae.Merge([new(0.5f, 0.6f, 0, 0, Jt(12), false)], deathAnim, new Dictionary<int, int> { [0] = 16 },
            new Dictionary<int, TAE.EventGroup>(), 5f, 5f);
        Check("TAE merge: a DS3 death move keeps the template's kill/disappear JumpTables",
            dying.Where(e => e.Type == 0).Select(ChrTae.JumpTableId).OrderBy(i => i).SequenceEqual([12, 46, 49]));
        // A map script's signal (type 231, the Hollow Soldier's burst telling common event 20005415 to swap in the Pus
        // of Man) comes along though the template's TAE has no event of the type, in a group of its own.
        var signalAnim = new TAE.Animation(3010, new TAE.Animation.AnimMiniHeader.Standard(), "a000_003010.hkx");
        var signalBytes = new byte[16];
        BitConverter.GetBytes(100).CopyTo(signalBytes, 0);
        var signalKept = new Dictionary<int, int>();
        var signalMerged = ChrTae.Merge([new TAE.Event(0.8f, 0.9f, 231, 0, signalBytes, false), new TAE.Event(0.1f, 0.2f, 9999, 0, new byte[16], false)],
            signalAnim, new Dictionary<int, int> { [0] = 16 }, new Dictionary<int, TAE.EventGroup>(), 2f, 2f, kept: signalKept);
        Check("TAE merge: DS3's map-script signal event is kept in a group of its type, unknown types still dropped",
            signalMerged.Count == 1 && signalMerged[0] is { Type: 231, Group.GroupType: 231 } sig && BitConverter.ToInt32(sig.GetParameterBytes(false), 0) == 100
            && signalAnim.EventGroups.Contains(sig.Group!) && signalKept.GetValueOrDefault(231) == 1);
        var mask = new byte[32];
        mask[1] = 1;
        var transform = new TAE.Animation(3010, new TAE.Animation.AnimMiniHeader.Standard(), "a000_003010.hkx");
        var drawMask = ChrTae.Merge([new(0.4f, 0.5f, 233, 0, mask, false), new(0.4f, 0.5f, 233, 0, new byte[16], false), new(0.1f, 0.2f, 5, 0, new byte[4], false)],
            transform, new Dictionary<int, int> { [0] = 16 }, new Dictionary<int, TAE.EventGroup>(), 1f, 1f);
        Check("TAE merge: DS3's draw mask change (Iudex Gundyr's Pus of Man and red eyes) comes along though the template never uses it, with a group of its own; other unknown types don't",
            drawMask.Count == 1 && drawMask[0] is { Type: 233, Group.GroupType: 233 } && drawMask[0].GetParameterBytes(false)[1] == 1 && transform.EventGroups.Contains(drawMask[0].Group!));

        // Event layouts (2026-11-19, the Hollow Manservant lost 174 type-5 events): the usual length counts, not the
        // first event of a file, and DS3's odd-length events (the reader's guess off by 8) are written at the usual one.
        var layout = ChrTae.Layout([new(0, 1, 5, 0, new byte[24], false), new(0, 1, 5, 0, new byte[16], false), new(0, 1, 5, 0, new byte[16], false)]);
        Check("TAE layout: a type's usual parameter length is its most common one, not its first", layout[5] == 16);
        var behaviour = new byte[8];
        BitConverter.GetBytes(3002).CopyTo(behaviour, 4);
        var attack24 = new byte[24];
        attack24[8] = 0x6E;
        var padAnim = new TAE.Animation(7000, new TAE.Animation.AnimMiniHeader.Standard(), "a000_007000.hkx");
        var padded = ChrTae.Merge([new(0.1f, 0.2f, 5, 0, behaviour, false), new(0.3f, 0.4f, 1, 0, attack24, false), new(0.5f, 0.6f, 1, 0, Enumerable.Repeat((byte)1, 24).ToArray(), false)],
            padAnim, new Dictionary<int, int> { [1] = 16, [5] = 16 }, new Dictionary<int, TAE.EventGroup> { [1] = new(1), [5] = new(5) }, 1f, 1f,
            ds3Lengths: new Dictionary<int, int> { [1] = 16, [5] = 16 });
        Check("TAE merge: DS3 events 8 bytes off their type's usual length come along at that length (zero padding only)",
            padded.Count == 2 && padded.All(e => e.GetParameterBytes(false).Length == 16)
            && BitConverter.ToInt32(padded.First(e => e.Type == 5).GetParameterBytes(false), 4) == 3002 && padded.First(e => e.Type == 1).GetParameterBytes(false)[8] == 0x6E);
        // A character no template fits is carried by one with a move set (1.0.501: the Ancient Wyvern on Rykard's
        // Corpse, one animation), else the first with any.
        Check("carrying: the first ranked template with a move set is taken over one with a single animation",
            ChrPort.CarryPick([1, 300, 900], c => c) == 1 && ChrPort.CarryPick([0, 1, 5], c => c) == 1 && ChrPort.CarryPick([0, 0], c => c) is null);

        // 1.0.5xx: the High Wall's Winged Knight vanished after its attacks. #226 let DS3's 601 SetAdditiveAnim events in
        // (other Elden Ring characters use the type at DS3's length), and Elden Ring plays the animation their last field
        // names, which DS3 leaves 0. They never come along, even where the template's TAE has the type.
        var additive = new byte[16];
        BitConverter.GetBytes(1f).CopyTo(additive, 4);
        var additiveDropped = new Dictionary<int, int>();
        var additiveAnim = new TAE.Animation(3000, new TAE.Animation.AnimMiniHeader.Standard(), "a000_003000.hkx");
        var additiveMerged = ChrTae.Merge([new(0.1f, 0.9f, 601, 0, additive, false)], additiveAnim, new Dictionary<int, int> { [601] = 16 },
            new Dictionary<int, TAE.EventGroup> { [601] = new(601) }, 1f, 1f, dropped: additiveDropped);
        Check("TAE merge: DS3's SetAdditiveAnim (601) never comes along, even at Elden Ring's length with a template group",
            additiveMerged.Count == 0 && additiveDropped.GetValueOrDefault(601) == 1);
        Check("TAE layouts: types Elden Ring reads differently (601 additive, 193 opacity) are not admitted from other Elden Ring characters",
            ChrTae.ReadDifferently.Contains(601) && ChrTae.ReadDifferently.Contains(193) && !ChrTae.ReadDifferently.Contains(5) && !ChrTae.ReadDifferently.Contains(1));
        // 1.0.5xx: Pontiff's right-hand cape stuck to his left = the template's cloth (c3570_c.hkx, its own skirt and tail
        // bones) kept in the chrbnd under DS3's model.
        Check("cloth: the template's cloth files are recognised and left out by default",
            ChrPort.IsClothFile(@"N:\GR\data\INTERROOT_win64\chr\c7113\c7113_c.hkx") && ChrPort.IsClothFile("c7113_c.clm2")
            && !ChrPort.IsClothFile("c7113.hkx") && !ChrPort.IsClothFile("c7113_cloth.flver") && !ChrPort.IsClothFile("c7113.flver"));

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

        // A binder write that fails partway names its own error, not the reservations it left (1.0.340 c6330 / c6331).
        MaskedErrors.Install();
        string writeError;
        try
        {
            var broken = new BND4();
            broken.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, "a.flver", null!));
            broken.Write();
            writeError = "";
        }
        catch (Exception ex) { writeError = ChrPort.Describe(ex); }
        Check($"a binder write that fails partway is described by what stopped it ({writeError[..Math.Min(writeError.Length, 160)]})",
            writeError.StartsWith("NullReferenceException", StringComparison.Ordinal) && writeError.Contains("(then: InvalidOperationException: Not all reservations filled", StringComparison.Ordinal));

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
        foreach (var coloured in ds3Model.Meshes.SelectMany(m => m.Vertices))
            coloured.Colors.Add(new FLVER.VertexColor(0.5f, 1f, 0f, 1f));
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

        Check("every vertex carries Elden Ring's vertex colour 1 0 0 1 (DS3: 1 0 1 1, layer weights in alpha, or none)",
            ds3Model.Meshes.SelectMany(m => m.Vertices).All(vx => vx.Colors.Count > 0 && vx.Colors.All(c => c.A == 1f && c.R == 1f && c.G == 0f && c.B == 0f)));
        var layered = Model(@"N:\FDP\data\Material\mtd\character\C[ARSN]_m.mtd");
        foreach (var m in layered.Materials)
            m.Textures.AddRange(
            [
                new FLVER2.Texture("g_DiffuseTexture2", @"N:\FDP\chr\c1310\tex\c1310_main part_a.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
                new FLVER2.Texture("g_SpecularTexture2", @"N:\FDP\chr\c1310\tex\c1310_main part_r.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
                new FLVER2.Texture("g_BumpmapTexture2", @"N:\FDP\chr\c1310\tex\c1310_main part_n.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
            ]);
        var layeredMaterials = ChrModel.Convert(layered, erTemplate, 1310, 7031, plainMaterials: true, erNodes: true,
            textureArea: name => name.Contains("main part") ? 2048 * 2048 : 512 * 512);
        Check("a two-layer material shows the layer with the larger colour texture",
            layeredMaterials.Count == 1 && layeredMaterials[0] is { Albedo: "c7031_main part_a", Normal: "c7031_main part_n", Metallic: "c7031_main part_m" } && ChrModel.SecondLayers == 2);
        var firstLayer = ChrModel.Convert(Model(@"N:\FDP\data\Material\mtd\character\C[ARSN]_m.mtd"), erTemplate, 1310, 7031, plainMaterials: true, erNodes: true, textureArea: _ => 4);
        // DS3 fur (Ariandel's wolf, C[Fur]): the strand atlas on the first UV set, cut out, though the body texture is larger.
        var furModel = Model(@"N:\FDP\data\Material\mtd\character\C[Fur].mtd");
        foreach (var m in furModel.Materials)
            m.Textures =
            [
                new FLVER2.Texture("Fur_FDP_snp_Texture2D_2_AlbedoMap_1", @"N:\FDP\chr\c6040\tex\c6040_a.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
                new FLVER2.Texture("Fur_FDP_snp_Texture2D_1_AlbedoMap_0", @"N:\FDP\chr\c6040\tex\c6040_Hair_01_a.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
                new FLVER2.Texture("Fur_FDP_snp_Texture2D_3_NormalMap_0", @"N:\FDP\chr\c6040\tex\c6040_Hair_01_n.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
            ];
        var furMaterials = ChrModel.Convert(furModel, erTemplate, 6040, 7128, plainMaterials: true, erNodes: true,
            textureArea: name => name.EndsWith("Hair_01_a") ? 1024 * 1024 : 2048 * 2048);
        Check("DS3 fur draws its strand layer, cut out, not the larger body texture over the strand UVs",
            furMaterials.Count == 1 && furMaterials[0] is { AlphaTest: true, Albedo: "c7128_Hair_01_a", Normal: "c7128_Hair_01_n" } && ChrModel.SecondLayers == 0
            && ChrModel.IsFur("C[Fur].mtd") && !ChrModel.IsFur("C[ARSN].mtd"));
        // 1.0.567: the Dancer's veil (c5270[Veil].mtd, DS3 g_BlendMode 2) drew as a solid white sheet: every plain
        // material was written solid. DS3's MTD now decides (ChrBlend), and a blended one goes on the see-through template.
        var veilModel = Model(@"N:\FDP\data\Material\mtd\character\c5270[Veil].mtd");
        var seeThrough = new ChrGlow.Template("C[c2100]_BD_MeshDecal", ChrBlend.AlphaShader,
            ["C_AMSN__Alpha_snp_Texture2D_2_AlbedoMap_0", "C_AMSN__Alpha_snp_Texture2D_0_MetallicMap_0", "C_AMSN__Alpha_snp_Texture2D_7_NormalMap_4"],
            new FLVER2.Material(), new FLVER2.GXList());
        var mtds = new Dictionary<string, ChrBlend.Ds3Draw>
        {
            [ChrBlend.Key("c5270[Veil].mtd")] = new(2, 0), [ChrBlend.Key(@"x\c2170[Veil] .mtd")] = new(3, 0),
            [ChrBlend.Key("C[ARSN]_Add.mtd")] = new(4, 0), [ChrBlend.Key("C[ARSN]_e_Decal.mtd")] = new(0, 128), [ChrBlend.Key("Plain.mtd")] = new(0, 0),
        };
        var veilMade = ChrModel.Convert(veilModel, erTemplate, 5270, 7130, plainMaterials: true, erNodes: true, ds3Mtds: mtds, seeThrough: seeThrough);
        Check("a DS3 blended material (Dancer's veil) is a see-through copy: alpha blend, alpha cut 1, its own GX list and the template's samplers",
            veilMade.Count == 1 && veilMade[0] is { TemplateMatbin: "C[c2100]_BD_MeshDecal", BlendMode: 2, AlphaRef: 1, AlphaTest: false, Plain: true }
            && ChrModel.SeeThrough == 2 && veilModel.Materials.All(m => m.GXIndex == 1 && m.Textures[0].ParamName == seeThrough.Samplers[0])
            && veilModel.GXLists.Count == 2);
        var veilSolid = ChrModel.Convert(Model(@"N:\FDP\data\Material\mtd\character\c5270[Veil].mtd"), erTemplate, 5270, 7130, plainMaterials: true, erNodes: true, ds3Mtds: mtds);
        Check("without a see-through template it stays solid, and says so",
            veilSolid[0] is { BlendMode: 0, TemplateMatbin: ChrMaterial.PlainSource } && ChrModel.SeeThroughSolid == 2 && ChrModel.SeeThrough == 0);
        // DS3 keeps some meshes' vertices in two buffers (c1430's cape, chestpiece and tunic); Elden Ring's ray tracing
        // flickered over the whole scene with one such model on the map. Each mesh ends with one buffer.
        var split = Model(@"N:\FDP\data\Material\mtd\character\C[ARSN]_e_Decal.mtd");
        split.BufferLayouts.Add([new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Normal),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.BoneIndices),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.BoneWeights)]);
        split.BufferLayouts.Add([new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor),
            new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV)]);
        split.BufferLayouts.Add([new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Normal),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.BoneIndices),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.BoneWeights),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor),
            new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV)]);
        foreach (var sv in split.Meshes.SelectMany(m => m.Vertices))
        {
            sv.Normals.Add(System.Numerics.Vector3.UnitY);
            sv.NormalWs.Add(127);
            sv.Colors.Add(new FLVER.VertexColor(1f, 1f, 0f, 1f));
            sv.UVs.Add(new System.Numerics.Vector3(0.25f, 0.5f, 0));
        }
        split.Meshes[0].VertexBuffers = [new FLVER2.VertexBuffer(0), new FLVER2.VertexBuffer(1)];
        split.Meshes[1].VertexBuffers = [new FLVER2.VertexBuffer(2)];
        ChrModel.Convert(split, erTemplate, 1310, 7031, plainMaterials: true, erNodes: true);
        var splitBack = FLVER2.Read(split.Write());
        var joinedLayout = splitBack.BufferLayouts[splitBack.Meshes[0].VertexBuffers[0].LayoutIndex];
        Check("a mesh DS3 keeps in two vertex buffers ends with one, holding both buffers' members in order; a one-buffer mesh is left alone; the vertices survive a write and read ("
              + $"{ChrModel.BuffersJoined} joined, buffers {string.Join(",", splitBack.Meshes.Select(m => m.VertexBuffers.Count))}, layout {string.Join(" ", joinedLayout.Select(m => m.Semantic))})",
            ChrModel.BuffersJoined == 1 && splitBack.Meshes.All(m => m.VertexBuffers.Count == 1)
            && splitBack.Meshes[1].VertexBuffers[0].LayoutIndex == 2
            && joinedLayout.Select(m => m.Semantic).SequenceEqual([FLVER.LayoutSemantic.Position, FLVER.LayoutSemantic.Normal, FLVER.LayoutSemantic.BoneIndices,
                FLVER.LayoutSemantic.BoneWeights, FLVER.LayoutSemantic.VertexColor, FLVER.LayoutSemantic.UV])
            && joinedLayout.All(m => m.Stream == 0)
            && splitBack.Meshes[0].Vertices.Count == 3
            && splitBack.Meshes[0].Vertices.Select(vx => vx.Position.X).SequenceEqual([-1f, 3f, 1f])
            && splitBack.Meshes[0].Vertices.All(vx => vx.Colors.Count == 1 && vx.UVs.Count == 1 && MathF.Abs(vx.UVs[0].X - 0.25f) < 0.01f && vx.BoneWeights[0] > 0.99f)
            && splitBack.Meshes[0].Vertices.Select(vx => vx.BoneIndices[0]).SequenceEqual([1, 2, 2]));
        Check("DS3 blend modes: modulate becomes alpha, additive stays additive, an alpha cut with blend 0 cuts out, MTD keys ignore spaces and folders",
            mtds[ChrBlend.Key("c2170[Veil].mtd")] is { Blended: true, ErBlendMode: 2 } && mtds[ChrBlend.Key("C[ARSN]_Add.mtd")] is { ErBlendMode: 4 }
            && mtds[ChrBlend.Key("C[ARSN]_e_Decal.mtd")] is { Blended: false, AlphaTested: true } && mtds[ChrBlend.Key("plain.MTD")] is { Blended: false, AlphaTested: false });
        var blendBin = new MATBIN { ShaderPath = @"N:\GR\data\Material\mtd\Chr\Shader\C[AMSN]_Alpha.spx", SourcePath = @"N:\x\C[c2100]_BD_MeshDecal.matxml" };
        blendBin.Params.Add(new MATBIN.Param { Name = "g_BlendMode", Type = MATBIN.ParamType.Int, Value = 2 });
        blendBin.Params.Add(new MATBIN.Param { Name = "g_AlphaRef", Type = MATBIN.ParamType.Int2, Value = new[] { 40, 0 } });
        foreach (var smp in seeThrough.Samplers)
            blendBin.Samplers.Add(new MATBIN.Sampler { Type = smp, Path = "" });
        Check("the see-through template is found by shader with albedo, normal and metallic samplers", ChrBlend.Score(blendBin) == 3);
        new ChrMaterial("C[c2100]_BD_MeshDecal", "C[c7130]_ds3_0", "c7130_veil_a", "c7130_veil_n", null) { Plain = true, BlendMode = 4, AlphaRef = 1, TextureFolder = @"N:\t\" }.Apply(blendBin);
        Check("the copy takes DS3's blend mode and the alpha cut, and names DS3's textures",
            blendBin.Params.First(p => p.Name == "g_BlendMode").Value is 4 && blendBin.Params.First(p => p.Name == "g_AlphaRef").Value is int[] { Length: 2 } r && r[0] == 1
            && blendBin.Samplers[0].Path == @"N:\t\c7130_veil_a.tif" && blendBin.Samplers[2].Path == @"N:\t\c7130_veil_n.tif");
        Check("a one-layer material keeps its textures", firstLayer.Count == 1 && firstLayer[0].Albedo == "c7031_a" && ChrModel.SecondLayers == 0);

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

        // DS3's body capsule on the template's row (Ariandel's wolf kept the Elden Ring wolf's 1.2 / 0.6 up to 1.0.410).
        var ds3Size = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        var erSize = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        foreach (var f in new[] { "hitHeight", "hitRadius" })
            ds3Size.Fields.Add(new PARAMDEF.Field(ds3Size, PARAMDEF.DefType.f32, f));
        foreach (var f in new[] { "hitHeight", "hitRadius", "chrHitHeight", "chrHitRadius" })
            erSize.Fields.Add(new PARAMDEF.Field(erSize, PARAMDEF.DefType.f32, f));
        var ds3SizeRow = new PARAM.Row(1, "ds3 wolf", ds3Size);
        ds3SizeRow["hitHeight"]!.Value = 1.7f;
        ds3SizeRow["hitRadius"]!.Value = 0.55f;
        var erSizeRow = new PARAM.Row(2, "er wolf", erSize);
        erSizeRow["hitHeight"]!.Value = 1.2f;
        erSizeRow["hitRadius"]!.Value = 0.6f;
        erSizeRow["chrHitHeight"]!.Value = 0.6f;
        erSizeRow["chrHitRadius"]!.Value = 0f;
        ChrPort.CopyBodySize(ds3SizeRow, erSizeRow);
        float Size(string f) => (float)erSizeRow[f]!.Value;
        Check("DS3's capsule size replaces the template's; the character capsule scales with it, an unset one stays unset",
            Size("hitHeight") == 1.7f && Size("hitRadius") == 0.55f && Math.Abs(Size("chrHitHeight") - 0.85f) < 1e-5f && Size("chrHitRadius") == 0f);

        // DS3's values in every shared NpcParam field; the template's where another step owns the field or the games
        // number it differently; Elden Ring-only fields that describe the template back to their default.
        var ds3All = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        var erAll = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        foreach (var (t, f) in new[] { (PARAMDEF.DefType.s16, "material"), (PARAMDEF.DefType.s16, "subMaterialSfx_weak"), (PARAMDEF.DefType.s16, "guardAngle"),
                     (PARAMDEF.DefType.s32, "weight"), (PARAMDEF.DefType.s16, "defPhys"), (PARAMDEF.DefType.s16, "SuperArmorDurability"),
                     (PARAMDEF.DefType.u8, "teamType"), (PARAMDEF.DefType.s32, "spEffectId0"), (PARAMDEF.DefType.s16, "DeactivateDistance"),
                     (PARAMDEF.DefType.s32, "basicToughnessValue"), (PARAMDEF.DefType.s16, "normalChangeModelId"), (PARAMDEF.DefType.s32, "Hp"),
                     (PARAMDEF.DefType.f32, "itemSearchRadius"), (PARAMDEF.DefType.u8, "parryDefense") })
            ds3All.Fields.Add(new PARAMDEF.Field(ds3All, t, f));
        foreach (var (t, f, d) in new (PARAMDEF.DefType, string, object?)[] { (PARAMDEF.DefType.u16, "materialSe1", null), (PARAMDEF.DefType.u16, "materialSfx_Weak2", null),
                     (PARAMDEF.DefType.s16, "guardAngle", null), (PARAMDEF.DefType.u32, "weight", null), (PARAMDEF.DefType.u16, "def_phys", null),
                     (PARAMDEF.DefType.f32, "superArmorDurability", null), (PARAMDEF.DefType.u8, "teamType", null), (PARAMDEF.DefType.s32, "spEffectID0", -1),
                     (PARAMDEF.DefType.s16, "deactivateDist", -1), (PARAMDEF.DefType.u32, "toughness", null), (PARAMDEF.DefType.s16, "normalChangeModelId", -1),
                     (PARAMDEF.DefType.u32, "hp", null), (PARAMDEF.DefType.u8, "itemSearchRadius", null), (PARAMDEF.DefType.u8, "parryDefence", null),
                     (PARAMDEF.DefType.u32, "chrActivateConditionParamId", null), (PARAMDEF.DefType.s32, "loadAssetId", -1),
                     (PARAMDEF.DefType.u8, "isWeakC", null), (PARAMDEF.DefType.s32, "autoFootEffectSfxId", -1) })
            erAll.Fields.Add(new PARAMDEF.Field(erAll, t, f) { Default = d ?? 0 });
        var ds3NpcRow = new PARAM.Row(1, "ds3 knight", ds3All);
        foreach (var (f, val) in new (string, object)[] { ("material", (short)7), ("subMaterialSfx_weak", (short)139), ("guardAngle", (short)60), ("weight", 120),
                     ("defPhys", (short)100), ("SuperArmorDurability", (short)35), ("teamType", (byte)6), ("spEffectId0", 30030), ("DeactivateDistance", (short)0),
                     ("basicToughnessValue", 40), ("normalChangeModelId", (short)1100), ("Hp", 900), ("itemSearchRadius", 1.5f), ("parryDefense", (byte)30) })
            ds3NpcRow[f]!.Value = val;
        var erTemplateRow = new PARAM.Row(2, "er template", erAll);
        foreach (var (f, val) in new (string, object)[] { ("materialSe1", (ushort)101), ("materialSfx_Weak2", (ushort)101), ("guardAngle", (short)0), ("weight", 400u),
                     ("def_phys", (ushort)120), ("superArmorDurability", 80f), ("teamType", (byte)26), ("spEffectID0", 5000), ("deactivateDist", (short)-1),
                     ("toughness", 10u), ("normalChangeModelId", (short)-1), ("hp", 2000u), ("itemSearchRadius", (byte)3), ("parryDefence", (byte)0),
                     ("chrActivateConditionParamId", 3u), ("loadAssetId", 1234), ("isWeakC", (byte)1), ("autoFootEffectSfxId", 910) })
            erTemplateRow[f]!.Value = val;
        int sharedCopied = ChrPort.CopyDs3Fields(ds3NpcRow, erTemplateRow);
        int templateCleared = ChrPort.ClearTemplateOnly(erTemplateRow);
        object Npc(string f) => erTemplateRow[f]!.Value;
        Check("NpcParam: DS3's value in every shared field (renamed hit materials, guard, weight, defence, poise as float, toughness, parry)",
            sharedCopied == 8 && Npc("materialSe1") is (ushort)7 && Npc("materialSfx_Weak2") is (ushort)139 && Npc("guardAngle") is (short)60 && Npc("weight") is 120u
            && Npc("def_phys") is (ushort)100 && Npc("superArmorDurability") is 35f && Npc("toughness") is 40u && Npc("parryDefence") is (byte)30);
        Check("NpcParam: team, resident effects, model swaps, health, DS3's 0 deactivate distance and a float into a byte field stay the template's",
            Npc("teamType") is (byte)26 && Npc("spEffectID0") is 5000 && Npc("normalChangeModelId") is (short)-1 && Npc("hp") is 2000u
            && Npc("deactivateDist") is (short)-1 && Npc("itemSearchRadius") is (byte)3);
        // A template row that refers to another character's resources doesn't pass them on.
        var swapDef = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        foreach (var f in ChrPort.ResourceSwapFields)
            swapDef.Fields.Add(new PARAMDEF.Field(swapDef, PARAMDEF.DefType.s16, f) { Default = -1 });
        var swapRow = new PARAM.Row(3, "c3061 row", swapDef);
        // Gundyr's rows 71120000/71120001 as built: retarget and texture swap to c3060.
        swapRow["RetargetReferenceChrId"]!.Value = (short)3060;
        swapRow["normalChangeTexChrId"]!.Value = (short)3060;
        foreach (var f in new[] { "normalChangeAnimChrId", "normalChangeModelId", "normalChangeResouceId" })
            swapRow[f]!.Value = (short)-1;
        var swaps = ChrPort.OwnResources(swapRow);
        Check($"NpcParam: a ported model plays its own anibnd: the template row's retarget/texture swaps to another character go to -1 ({string.Join(" ", swaps)})",
            swaps.SequenceEqual(["RetargetReferenceChrId=3060", "normalChangeTexChrId=3060"]) && ChrPort.ResourceSwapFields.All(f => swapRow[f]!.Value is (short)-1)
            && ChrPort.OwnResources(swapRow).Count == 0);
        // The Soldier of Godrick's sheathed-weapon effect (13065) comes off a ported row; the shared effects stay, in order.
        var fxDef = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        for (int k = 0; k < TemplateEffects.Slots; k++)
            fxDef.Fields.Add(new PARAMDEF.Field(fxDef, PARAMDEF.DefType.s32, $"spEffectID{k}"));
        var fxRow = new PARAM.Row(70860000, "Abyss Watcher", fxDef);
        int[] fxIds = [7000, 90400, 13065, 5815, 13064, 4301];
        for (int k = 0; k < TemplateEffects.Slots; k++)
            fxRow[$"spEffectID{k}"]!.Value = k < fxIds.Length ? fxIds[k] : 0;
        var sheathed = ChrPort.StripEffects(fxRow, new HashSet<int> { 13065, 13064 });
        Check("NpcParam: the template's weapon on/off effects come off a ported row, the others move up in order",
            sheathed.SequenceEqual([13065, 13064]) && TemplateEffects.Effects(fxRow).SequenceEqual([7000, 90400, 5815, 4301]));
        Check("NpcParam: the template's appear condition, carried asset, footprint effect and Elden Ring weakness go back to the defaults",
            templateCleared == 4 && Npc("chrActivateConditionParamId") is 0u && Npc("loadAssetId") is -1 && Npc("isWeakC") is (byte)0 && Npc("autoFootEffectSfxId") is -1);

        // DS3's damage taken on the template's row: the template's weaknesses go, the Pus of Man is weak to fire.
        var rateDef = new PARAMDEF { ParamType = "NPC_PARAM_ST", Fields = [] };
        foreach (var f in ChrPort.DamageRateFields)
            rateDef.Fields.Add(new PARAMDEF.Field(rateDef, PARAMDEF.DefType.f32, f));
        PARAM.Row SkeletonRates(int id)
        {
            var r = new PARAM.Row(id, "giant beast skeleton", rateDef);
            r["blowDamageCutRate"]!.Value = 1.4f;
            r["thunderDamageCutRate"]!.Value = 0.6f;
            return r;
        }
        var pusRates = SkeletonRates(3);
        ChrPort.SetDamageRates(1090, pusRates);
        var hollowRates = SkeletonRates(4);
        ChrPort.SetDamageRates(1100, hollowRates);
        Check("damage rates: the template's weaknesses reset to 1, the Pus of Man takes 1.5x fire",
            (float)pusRates["fireDamageCutRate"]!.Value == 1.5f && (float)pusRates["blowDamageCutRate"]!.Value == 1f
            && (float)pusRates["thunderDamageCutRate"]!.Value == 1f
            && ChrPort.DamageRateFields.All(f => (float)hollowRates[f]!.Value == 1f));
        var sp3Def = new PARAMDEF { ParamType = "SP_EFFECT_PARAM_ST", Fields = [] };
        foreach (var f in ChrPort.DamageRateFields)
            sp3Def.Fields.Add(new PARAMDEF.Field(sp3Def, PARAMDEF.DefType.f32, f) { Default = 1f });
        var gaelPhantom = new PARAM.Row(221000, "", sp3Def);
        foreach (var f in ChrPort.DamageRateFields)
            gaelPhantom[f]!.Value = 0.57f;
        var plainEffect = new PARAM.Row(30030, "NPC: Disable Curse", sp3Def);
        foreach (var f in ChrPort.DamageRateFields)
            plainEffect[f]!.Value = 1f;
        Check("DS3 effects that change the damage a character takes come with it, others without a visible use don't",
            ChrEffects.NpcEffectWanted(gaelPhantom) && !ChrEffects.NpcEffectWanted(plainEffect));

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
        var movedBones = new[] { ("Master", -1), ("Pelvis", 0), ("Neck", 1), ("Head", 2), ("Jaw", 3), ("Chain", 0) };
        var movedSk = new AnimPort.Ds3Skeleton
        {
            Names = movedBones.Select(b => b.Item1).ToList(),
            Parents = movedBones.Select(b => (short)b.Item2).ToList(),
            ReferencePose = movedBones.Select((_, i) => new SoulsAssetPipeline.Animation.NewBlendableTransform
                { Translation = new System.Numerics.Vector3(i, 0, 0), Rotation = System.Numerics.Quaternion.Identity, Scale = System.Numerics.Vector3.One }).ToList(),
        };
        var merged3 = Template();
        Check("a bone DS3 has under another, earlier bone moves there and keeps its number (nothing set aside)",
            ChrPort.MergeSkeleton(merged3, movedSk, addBones: true) == new ChrPort.SkeletonMerge(5, 0, 0, 1) && merged3.m_bones.Count == 6
            && merged3.m_parentIndices[5] == 0 && merged3.m_bones[5].m_name == "Chain"
            && ChrPort.KeptBones(new ChrPort.Shape(Template().m_bones.Select(b => b.m_name!).ToList(), Template().m_parentIndices), new ChrPort.Shape(movedSk.Names, movedSk.Parents)) == 6
            && ChrPort.KeptBones(new ChrPort.Shape(Template().m_bones.Select(b => b.m_name!).ToList(), Template().m_parentIndices), new ChrPort.Shape(ds3Sk.Names, ds3Sk.Parents)) == 3);
        var merged2 = Template();
        Check("fallback enemy-model D: only shared bones take DS3's rest position, none added",
            ChrPort.MergeSkeleton(merged2, ds3Sk, addBones: false) == new ChrPort.SkeletonMerge(5, 0, 0) && merged2.m_bones.Count == 6
            && merged2.m_referencePose[3].m_translation.X == 4);

        // Cloth bones (ClothBones): Gundyr's shoulder plate hangs off a helper under the root; it moves under the nearest
        // bone its cloth is skinned to and keeps its rest place; its child follows it; a bone later in the list is no parent.
        {
            var sk = new HKLib.hk2018.hkaSkeleton();
            foreach (var (name, parent, x, y) in new[] { ("Master", -1, 0f, 0f), ("Clav", 0, 0.2f, 2.5f), ("Arm", 1, 0.3f, 0f), ("XXX_cloth", 0, 0f, 1.7f),
                         ("XXX_pad", 3, 0.6f, 1.0f), ("XXX_pad2", 4, 0.1f, 0f), ("Late", 0, 0.7f, 2.7f) })
            {
                sk.m_bones.Add(new HKLib.hk2018.hkaBone { m_name = name });
                sk.m_parentIndices.Add((short)parent);
                sk.m_referencePose.Add(new HKLib.hk2018.hkQsTransform { m_translation = new System.Numerics.Vector4(x, y, 0, 0), m_rotation = System.Numerics.Quaternion.Identity, m_scale = System.Numerics.Vector4.One });
            }
            var before = ClothBones.World(sk);
            var moved = ClothBones.Reparent(sk, [("shoulder", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "XXX_pad", "XXX_pad2" }, ["Clav", "Arm", "Late"])]);
            var after = ClothBones.World(sk);
            Check($"cloth bones: a piece DS3's cloth moves rides on the nearest earlier bone its cloth is skinned to, rest place kept, its child follows it ({string.Join(", ", moved.Select(m => m.Bone + "->" + m.To))})",
                moved.Count == 1 && moved[0].Bone == "XXX_pad" && moved[0].To == "Arm" && sk.m_parentIndices[4] == 2 && sk.m_parentIndices[5] == 4
                && Enumerable.Range(0, before.Length).All(i => (before[i].Translation - after[i].Translation).Length() < 1e-5f));
        }
        Check("hit shapes: Elden Ring's hkxpwv part flags (trunk 0x80, calves and feet 0x20, arms and head 0x10)",
            ChrHitShapes.PartFlag("Collidable_Bip001 Spine001") == 0x80 && ChrHitShapes.PartFlag("Collidable_Bip001 L Calf001") == 0x20
            && ChrHitShapes.PartFlag("Ragdoll_Soul_Head001") == 0x10 && ChrHitShapes.PartFlag("Ragdoll_Soul_Tail003") == 0x80);
        {
            var rag = new HKX2.hknpRagdollData { m_bodyCinfos = [new HKX2.hknpBodyCinfo { m_name = "Collidable_Bip001 Head001" }, new HKX2.hknpBodyCinfo { m_name = "Pelvis" }] };
            var ds3Pwv = new byte[32 + 2 * 4 + 3 * 8 + 2 * 16];
            BitConverter.GetBytes((ushort)2).CopyTo(ds3Pwv, 6);
            BitConverter.GetBytes((ushort)3).CopyTo(ds3Pwv, 8);
            new byte[] { 0xE8, 0x03, 0xFF, 0xFF, 0x1F, 0x09 }.CopyTo(ds3Pwv, 32 + 8 + 24);
            var pwv = ChrHitShapes.Pwv(rag, 5, ds3Pwv);
            int tail = 32 + 2 * 4 + 5 * 8;
            Check("hit shapes: the hkxpwv is Elden Ring's version 2 for DS3's body count and the ported bone count, each body's record from DS3",
                pwv.Length == tail + 2 * 16 && BitConverter.ToUInt16(pwv, 0) == 2 && BitConverter.ToUInt16(pwv, 6) == 2 && BitConverter.ToUInt16(pwv, 8) == 5
                && BitConverter.ToUInt32(pwv, 32) == 0xFFFF && BitConverter.ToUInt16(pwv, tail) == 1000 && BitConverter.ToUInt16(pwv, tail + 4) == 0x091F
                && BitConverter.ToUInt16(pwv, tail + 6) == 0x10 && pwv[tail + 8] == 0xFF && BitConverter.ToUInt32(pwv, tail + 16) == 0
                && BitConverter.ToUInt16(pwv, tail + 16 + 6) == 0x80);
        }

        // Limb IK (ChrIk): Placidusax pins four limbs to target bones; Ariandel has the foot targets, not the hand ones.
        {
            HKLib.hk2018.hkbVariableBindingSet Bind(params (string Path, int Variable)[] to) => new()
            {
                m_bindings = to.Select(b => new HKLib.hk2018.hkbVariableBindingSet.Binding
                    { m_memberPath = b.Path, m_variableIndex = b.Variable, m_bindingType = HKLib.hk2018.hkbVariableBindingSet.Binding.BindingType.BINDING_TYPE_VARIABLE }).ToList(),
            };
            HKLib.hk2018.hkbHandIkControlsModifier.Hand Hand(bool on = true) => new()
                { m_enable = on, m_controlData = new HKLib.hk2018.hkbHandIkControlData { m_transformOnFraction = 1, m_normalOnFraction = 1 } };
            var controls = new HKLib.hk2018.hkbHandIkControlsModifier
            {
                m_name = "HandIKControlsModifier", m_hands = [Hand(), Hand(), Hand(), Hand(on: false), Hand()],
                m_variableBindingSet = Bind(("hands:0/controlData/targetHandle", 0), ("hands:1/controlData/targetHandle", 1), ("hands:2/controlData/targetHandle", 2),
                    ("hands:3/controlData/targetHandle", 2)),
            };
            var ikGraph = new HKLib.hk2018.hkbModifierList
            {
                m_modifiers =
                [
                    new HKLib.hk2018.hkbGetHandleOnBoneModifier { m_name = "L_Foot_Target", m_animationBoneIndex = 1, m_variableBindingSet = Bind(("handleOut", 0)) },
                    new HKLib.hk2018.hkbGetHandleOnBoneModifier { m_name = "L_Hand_Target", m_animationBoneIndex = 3, m_variableBindingSet = Bind(("handleOut", 1)) },
                    new HKLib.hk2018.hkbGetHandleOnBoneModifier { m_name = "Head", m_animationBoneIndex = 2, m_variableBindingSet = Bind(("handleOut", 2)) },
                    controls,
                ],
            };
            // The built skeleton: the template's Head was set aside (renamed), DS3's is a new bone at the end.
            string[] built = ["Master", "L_Foot_Target", "Head_er", "L_Hand_Target", "Head"];
            var ikResult = ChrIk.Fit(ikGraph, built, new HashSet<string>(["master", "l_foot_target", "head"], StringComparer.OrdinalIgnoreCase));
            Check($"limb IK: a limb pinned to a bone DS3 doesn't animate is let go, one on a DS3 bone stays ({ikResult.Limbs} limbs, off: {string.Join(",", ikResult.Off)})",
                ikResult.Limbs == 4 && ikResult.Off.SequenceEqual(["L_Hand_Target", "Head_er"])
                && controls.m_hands.Select(h => h.m_enable).SequenceEqual([true, false, false, false, true])
                && controls.m_hands[1].m_controlData.m_transformOnFraction == 0 && controls.m_hands[1].m_controlData.m_normalOnFraction == 0
                && controls.m_hands[0].m_controlData.m_transformOnFraction == 1 && controls.m_hands[4].m_controlData.m_transformOnFraction == 1);
            var again = ChrIk.Fit(ikGraph, built, new HashSet<string>(["master", "l_foot_target", "head"], StringComparer.OrdinalIgnoreCase));
            Check("limb IK: a second pass changes nothing", again.Limbs == 2 && again.Off.Count == 0);
        }

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

        // Textures: one file per binder, a low-quality copy two sizes down.
        var dds16 = WeaponTextures.Encode(new byte[16 * 16 * 4], 16, 16, BCnEncoder.Shared.CompressionFormat.Bc1, 72);
        var small = ChrModel.DropMips(dds16, 2);
        Check($"a low-quality texture drops the two largest sizes (16x16 with {BitConverter.ToInt32(dds16, 28)} sizes -> {BitConverter.ToInt32(small, 16)}x{BitConverter.ToInt32(small, 12)} with {BitConverter.ToInt32(small, 28)})",
            BitConverter.ToInt32(dds16, 28) == 5 && BitConverter.ToInt32(small, 16) == 4 && BitConverter.ToInt32(small, 12) == 4 && BitConverter.ToInt32(small, 28) == 3
            && small.Length == 148 + 8 + 8 + 8 && BitConverter.ToInt32(small, 20) == 8
            && small.AsSpan(148).SequenceEqual(dds16.AsSpan(dds16.Length - 24)));
        var tiny = ModelPort.BlackBc4Dds();
        Check("a texture too small to shrink, or not block-compressed, is kept as it is",
            ReferenceEquals(ChrModel.DropMips(tiny, 2), tiny) && ChrModel.DropMips(new byte[200], 2).Length == 200);
        var highTpf = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
        highTpf.Textures.Add(new TPF.Texture("c7000_body_a", 0, 0, dds16, TPF.TPFPlatform.PC));
        var lowTpf = ChrModel.LowTpf(highTpf);
        Check("the low-quality file names every texture name + _l", lowTpf.Textures.Count == 1 && lowTpf.Textures[0].Name == "c7000_body_a_l" && lowTpf.Textures[0].Bytes.Length < dds16.Length
            && lowTpf.Encoding == 1 && lowTpf.Flag2 == 3);
        var r1 = new TPF { Platform = TPF.TPFPlatform.PC };
        r1.Textures.Add(new TPF.Texture("c1100_body_r", 0, 0, dds16, TPF.TPFPlatform.PC));
        var r2 = new TPF { Platform = TPF.TPFPlatform.PC };
        r2.Textures.Add(new TPF.Texture("c1100_body_r", 0, 0, dds16, TPF.TPFPlatform.PC));
        Check("converted textures get a flat metallic map only for fallback enemy-model F (the default reads metal from the reflectance map later)",
            ChrModel.ConvertTpf(r1, 1100, 7000).Textures.Select(t => t.Name).SequenceEqual(["c7000_body_r"])
            && ChrModel.ConvertTpf(r2, 1100, 7000, flatMetallic: true).Textures.Select(t => t.Name).SequenceEqual(["c7000_body_r", "c7000_body_m"]));

        // A texture that isn't a DDS file (1.0.347: the locusts' "%)!") is left out and named, so the binder can be written.
        var odd = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
        odd.Textures.Add(new TPF.Texture("c6330_body_a", 0, 0, dds16, TPF.TPFPlatform.PC));
        odd.Textures.Add(new TPF.Texture { Name = "c6330_weapon_r", Format = 0, Bytes = [0x25, 0x29, 0x21, 0x00, .. new byte[200]] });
        var leftOut = new List<string>();
        var oddConverted = ChrModel.ConvertTpf(odd, 6330, 7000, false, leftOut);
        bool written;
        try { _ = oddConverted.Write(); written = true; }
        catch (Exception) { written = false; }
        Check($"a texture that isn't a DDS file is left out and named; the rest is written ({string.Join(" ", leftOut)})",
            oddConverted.Textures.Select(t => t.Name).SequenceEqual(["c7000_body_a"]) && leftOut.Count == 1 && leftOut[0].StartsWith("c6330_weapon_r", StringComparison.Ordinal)
            && leftOut[0].Contains("25292100", StringComparison.Ordinal) && written);

        DropPort.SelfTest(Check);
        ThrowRows.SelfTest(Check);
        AiSelfTest.Run(Check);
        ChrVariantsSelfTest.Run(Check);
        EventChains.SelfTest(Check);

        Console.WriteLine(failures == 0 ? "enemy-selftest: all checks passed" : $"enemy-selftest: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
