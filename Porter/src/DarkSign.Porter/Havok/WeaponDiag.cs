using System.Numerics;
using System.Text;
using System.Text.Json;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// weapon-diag &lt;weapon row ID&gt;...: read-only comparison of what the BUILT mod (Mods\ER) gives one ported weapon
/// against the vanilla Elden Ring template it was built from and the DS3 source, for the things a build log can't show:
/// <list type="bullet">
/// <item>the art section's TAE (every move's cancel windows, attack events, SpEffect windows, model-location overrides)
/// next to the Elden Ring art it was built on and the DS3 skill moves it came from;</item>
/// <item>the ported weapon model's dummy polys (where hit spheres and effects sit) next to the DS3 model's and the
/// template's, with the bones they hang from;</item>
/// <item>the behaviour clips and selectors that reach the art and moveset sections;</item>
/// <item>the weapon's params: art row, absorb row, which attack rows its behaviour variation points at.</item>
/// </list>
/// Writes Work\reports\diag\weapon-&lt;id&gt;.txt. Needs the game files (reads the vanilla binders) and the built mod.
/// </summary>
public static class WeaponDiag
{
    // Event types worth printing in full: 0 JumpTable (cancel windows), 1 attack (judge at +8), 66/67 SpEffect windows,
    // 331 art buff, 712 weapon model locations, 96 effect, 129 sound, 605, 608, 224.
    private static readonly int[] Verbose = [0, 1, 66, 67, 331, 712, 605];


    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var ids = args.Select(a => int.TryParse(a, out var v) ? v : -1).Where(v => v > 0).ToList();
        // weapon-diag tae 2 92 ds3:0 ...   dumps whole TAE sections (Elden Ring's a002 / a092; ds3:N for DS3's aN);
        // weapon-diag flver 454 ds3:1616 ... dumps weapon models (Elden Ring's wp_a_0454; ds3:N for DS3's).
        string mode = args.Count > 0 ? args[0].ToLowerInvariant() : "";
        if (mode == "bones")
            return BoneDiag.Run(ctx);
        if (ids.Count == 0 && mode is not ("tae" or "flver"))
        {
            Console.WriteLine("usage: weapon-diag <Elden Ring weapon row ID> [more IDs]  (ported weapons are 80000000 + 10000 * n)\n" +
                              "       weapon-diag tae <section> [ds3:<section>] ...    weapon-diag flver <model> [ds3:<model>] ...    weapon-diag bones");
            return 1;
        }
        TAE.ValidateEventBank = false;
        var er = ctx.OpenArchives(PorterGame.EldenRing);
        var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        if (mode is "tae" or "flver")
        {
            var r = new StringBuilder();
            void Log(string line) => r.AppendLine(line);
            BND4? erMainLazy = null;
            BND4? ds3MainLazy = null;
            List<CustomManualSelectorGenerator>? erSelectors = null;
            foreach (var arg in args.Skip(1))
            {
                bool fromDs3 = arg.StartsWith("ds3:", StringComparison.OrdinalIgnoreCase);
                if (!int.TryParse(fromDs3 ? arg[4..] : arg, out var n))
                    continue;
                try
                {
                    if (mode == "tae")
                    {
                        var main = fromDs3 ? ds3MainLazy ??= BND4.Read(ds3.Read("/chr/c0000.anibnd.dcx")) : erMainLazy ??= BND4.Read(er.Read("/chr/c0000.anibnd.dcx"));
                        DumpTae(Log, fromDs3 ? "DS3" : "vanilla", main, n, a => true, fromDs3);
                        // Which of Elden Ring's own behaviour selectors play the section's clips (the states an art's
                        // moves belong to: a918's 40010 is under SwordArtsOneShotComboEnd_MesmerSowrdArts_CMSG).
                        if (!fromDs3)
                        {
                            erSelectors ??= LoadSelectors(BND4.Read(er.Read("/chr/c0000.behbnd.dcx")));
                            var prefix = $"a{n:D3}_";
                            foreach (var sel in erSelectors)
                            {
                                var clips = sel.m_generators.OfType<hkbClipGenerator>().Where(c => (c.m_animationName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                                if (clips.Count > 0)
                                    Log($"  selector '{sel.m_name}' {sel.m_offsetType} animId {sel.m_animId} ({sel.m_generators.Count} generators): {string.Join(" ", clips.Select(c => $"{c.m_animationName}#{c.m_animationInternalId} {c.m_mode}"))}");
                            }
                        }
                    }
                    else
                    {
                        var archives = fromDs3 ? ds3 : er;
                        var path = ModelPort.PartsPath(n);
                        DumpModel(Log, fromDs3 ? "DS3" : "vanilla", archives.Contains(path) ? BND4.Read(archives.Read(path)) : null, n);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log($"### {arg}: FAILED {e}");
                }
            }
            Directory.CreateDirectory(ctx.WorkPath("reports", "diag"));
            var outPath = ctx.WorkPath("reports", "diag", $"{mode}-{string.Join("-", args.Skip(1).Select(a => a.Replace(':', '_')))}.txt");
            File.WriteAllText(outPath, r.ToString());
            Console.WriteLine($"weapon-diag: {mode} -> {outPath} ({r.Length / 1024} KB)");
            return 0;
        }
        var vanilla = ctx.LoadParams(PorterGame.EldenRing);
        var ds3Params = ctx.LoadParams(PorterGame.DarkSouls3);
        var mod = ParamStore.Load(PorterGame.EldenRing, Path.GetDirectoryName(ctx.ModPath("regulation.bin"))!, ctx.Data);
        var modMain = BND4.Read(DCX.Decompress(File.ReadAllBytes(ctx.ModPath("chr", "c0000.anibnd.dcx"))));
        var erMain = BND4.Read(er.Read("/chr/c0000.anibnd.dcx"));
        var ds3Main = BND4.Read(ds3.Read("/chr/c0000.anibnd.dcx"));
        var selectors = LoadSelectors(BND4.Read(DCX.Decompress(File.ReadAllBytes(ctx.ModPath("chr", "c0000.behbnd.dcx")))));
        var plans = JsonSerializer.Deserialize<List<WeaponPlan>>(File.ReadAllText(ctx.WorkPath("reports", "weapons-plan.json")))
            ?? throw new InvalidOperationException("weapons-plan.json is empty; run weapons-plan first");
        Directory.CreateDirectory(ctx.WorkPath("reports", "diag"));
        int failures = 0;
        foreach (var id in ids)
        {
            var report = new StringBuilder();
            try
            {
                Diagnose(id, report, mod, vanilla, ds3Params, modMain, erMain, ds3Main, selectors, er, ds3, plans, ctx);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                report.AppendLine($"FAILED: {e}");
                failures++;
            }
            var path = ctx.WorkPath("reports", "diag", $"weapon-{id}.txt");
            File.WriteAllText(path, report.ToString());
            Console.WriteLine($"weapon-diag: {id} -> {path} ({report.Length / 1024} KB)");
        }
        return failures == 0 ? 0 : 1;
    }

    private static void Diagnose(int id, StringBuilder r, ParamStore mod, ParamStore vanilla, ParamStore ds3Params, BND4 modMain, BND4 erMain,
        BND4 ds3Main, List<CustomManualSelectorGenerator> selectors, GameArchives er, GameArchives ds3, IReadOnlyList<WeaponPlan>? plans, PorterContext ctx)
    {
        void Log(string s) => r.AppendLine(s);
        var weapons = mod.Get("EquipParamWeapon")!;
        var row = weapons[id] ?? throw new InvalidDataException($"Mods\\ER\\regulation.bin has no EquipParamWeapon {id}");
        var plan = plans?.FirstOrDefault(p => p.NewId == id);
        var ds3Weapons = ds3Params.Get("EquipParamWeapon")!;
        var ds3Row = plan is null ? null : ds3Weapons[plan.Ds3Id];
        int templateId = plan?.TemplateId ?? -1;
        var template = templateId > 0 ? vanilla.Get("EquipParamWeapon")![templateId] : null;
        Log($"# weapon {id} '{plan?.Name ?? row.Name}' (DS3 {plan?.Ds3Id}, template {templateId} '{plan?.TemplateName}') built {File.GetLastWriteTime(ctx.ModPath("regulation.bin")):u}");
        Log("");
        Log("## 1. Params");
        Fields(Log, "mod weapon", row, "wepmotionCategory", "spAtkcategory", "behaviorVariationId", "swordArtsParamId", "absorpParamId", "equipModelId",
            "isDualBlade", "wepmotionOneHandId", "wepmotionBothHandId", "spEffectBehaviorId0", "spEffectBehaviorId1", "spEffectMsgId0", "spEffectMsgId1", "wepType", "atkAttribute", "atkAttribute2");
        if (template is not null)
            Fields(Log, "template", template, "wepmotionCategory", "spAtkcategory", "behaviorVariationId", "swordArtsParamId", "absorpParamId", "equipModelId",
                "isDualBlade", "wepmotionOneHandId", "wepmotionBothHandId", "wepType", "atkAttribute", "atkAttribute2");
        if (ds3Row is not null)
            Fields(Log, "DS3 weapon", ds3Row, "wepmotionCategory", "spAtkCategory", "behaviorVariationId", "swordArtId", "swordArt_ActId", "wepAbsorpPosId", "equipModelId", "wepmotionOneHandId", "wepmotionBothHandId");

        // Art rows.
        var arts = mod.Get("SwordArtsParam")!;
        int artRowId = Convert.ToInt32(row["swordArtsParamId"].Value);
        var artRow = arts[artRowId];
        int artId = artRow is null ? -1 : MovesetPort.ArtId(artRow);
        int templateArtRow = template is null ? -1 : Convert.ToInt32(template["swordArtsParamId"].Value);
        int templateArtId = templateArtRow > 0 && vanilla.Get("SwordArtsParam")![templateArtRow] is { } t ? MovesetPort.ArtId(t) : -1;
        Log($"art: SwordArtsParam {artRowId} -> art ID {artId} (TAE a{600 + artId}); template's row {templateArtRow} -> art {templateArtId} (a{600 + templateArtId})");
        if (artRow is not null)
            Fields(Log, "mod art", artRow, "artsSpeedType", "useMagicPoint_L2", "useMagicPoint_R1", "useMagicPoint_R2", "shieldIconType", "pad", "iconId", "aiUsageId", "isRefRightArts", "isGrayoutLeftHand");
        if (ds3Row is not null && ds3Params.Get("SwordArtsParam") is { } ds3Arts && ds3Arts[Convert.ToInt32(ds3Row["swordArtId"].Value)] is { } ds3Art)
            Log($"DS3 art row {ds3Art.ID} '{ds3Art.Name}': " + string.Join(", ", ds3Art.Cells.Where(c => c.Value is not byte[]).Take(16).Select(c => $"{c.Def.InternalName} {c.Value}")));

        // Absorb rows.
        var absorp = mod.Get("WepAbsorpPosParam")!;
        int absorpId = Convert.ToInt32(row["absorpParamId"].Value);
        if (absorp[absorpId] is { } ab)
            Log($"absorb row {absorpId}: " + string.Join(" ", ab.Cells.Where(c => c.Value is not byte[] && !c.Def.InternalName.EndsWith("_2") && !c.Def.InternalName.EndsWith("_3") && !c.Def.InternalName.StartsWith("disableParam")).Select(c => $"{c.Def.InternalName}={c.Value}")));

        // Behaviour variation: which attack rows the ported hits use.
        var behavior = mod.Get("BehaviorParam_PC")!;
        var attacks = mod.Get("AtkParam_Pc")!;
        var vanillaAttacks = vanilla.Get("AtkParam_Pc")!;
        int variation = Convert.ToInt32(row["behaviorVariationId"].Value);
        var rows = behavior.Rows.Where(b => Convert.ToInt32(b["variationId"].Value) == variation).OrderBy(b => b.ID).ToList();
        int fromDs3 = rows.Count(b => Convert.ToInt32(b["refType"].Value) == 0 && vanillaAttacks[Convert.ToInt32(b["refId"].Value)] is null);
        Log($"behaviour variation {variation}: {rows.Count} rows, {fromDs3} attacks ported from DS3, {rows.Count - fromDs3} the template's own Elden Ring rows (by attack ID)");
        foreach (var b in rows.Where(b => Convert.ToInt32(b["behaviorJudgeId"].Value) is 0 or 1 or 10 or 100 or 200 or 300 or 400 or 620 or 700))
            if (Convert.ToInt32(b["refType"].Value) == 0 && attacks[Convert.ToInt32(b["refId"].Value)] is { } atk)
                Log($"  judge {b["behaviorJudgeId"].Value}: attack {atk.ID} '{atk.Name}' hit0 r{atk["hit0_Radius"].Value} dmy {atk["hit0_DmyPoly1"].Value}/{atk["hit0_DmyPoly2"].Value}, hit1 r{atk["hit1_Radius"].Value} dmy {atk["hit1_DmyPoly1"].Value}/{atk["hit1_DmyPoly2"].Value}, phys {atk["atkPhysCorrection"].Value}%, attr {atk["atkAttribute"].Value}, isAddBaseAtk {atk["isAddBaseAtk"].Value}");

        // 2. TAE: art section (mod), template art (vanilla), DS3 skill moves.
        Log("");
        Log("## 2. Art TAE");
        int section = 600 + artId;
        if (artId >= 0)
            DumpTae(Log, "mod", modMain, section, a => true);
        if (templateArtId >= 0)
            DumpTae(Log, "vanilla", erMain, 600 + templateArtId, a => a.ID is >= 40000 and < 40100);
        foreach (var s in new[] { 612, 614 }.Where(s => s != 600 + templateArtId))
            DumpTae(Log, "vanilla", erMain, s, a => a.ID is 40000 or 40010 or 40020 or 40050 or 40060 or 40070);
        if (ds3Row is not null)
            foreach (var s in new[] { Convert.ToInt32(ds3Row["spAtkCategory"].Value), Convert.ToInt32(ds3Row["wepmotionCategory"].Value) }.Where(s => s > 0).Distinct())
                DumpTae(Log, "DS3", ds3Main, s, a => a.ID is >= 36000 and < 40000 or 30000 or 33000, ds3: true);

        // Moveset section: first R1 one-handed, two-handed, dual.
        Log("");
        Log("## 3. Moveset TAE (mod), first attacks");
        int moveSection = Convert.ToInt32(row[MovesetPort.UseWeaponMotionCategory ? "wepmotionCategory" : "spAtkcategory"].Value);
        if (moveSection <= 0)
            moveSection = Convert.ToInt32(row["wepmotionCategory"].Value);
        DumpTae(Log, "mod", modMain, moveSection, a => a.ID is 30000 or 30500 or 32000 or 32500);
        if (template is not null)
            DumpTae(Log, "vanilla", erMain, Convert.ToInt32(template["wepmotionCategory"].Value), a => a.ID is 30000 or 32000);

        // 4. Model dummies.
        Log("");
        Log("## 4. Weapon model dummy polys (hit spheres and effects hang on these)");
        int model = Convert.ToInt32(row["equipModelId"].Value);
        DumpModel(Log, "mod", ReadModPart(ctx, model), model);
        if (ds3Row is not null)
        {
            int ds3Model = Convert.ToInt32(ds3Row["equipModelId"].Value);
            if (ds3.Contains(ModelPort.PartsPath(ds3Model)))
                DumpModel(Log, "DS3", BND4.Read(ds3.Read(ModelPort.PartsPath(ds3Model))), ds3Model);
        }
        if (template is not null)
        {
            int erModel = Convert.ToInt32(template["equipModelId"].Value);
            if (er.Contains(ModelPort.PartsPath(erModel)))
                DumpModel(Log, "vanilla", BND4.Read(er.Read(ModelPort.PartsPath(erModel))), erModel);
        }

        // 5. Behaviour clips.
        Log("");
        Log("## 5. Behaviour selectors reaching the art and moveset sections");
        foreach (var (name, sec) in new[] { ("art", section), ("moveset", moveSection) })
        {
            if (sec <= 0) continue;
            var prefix = $"a{sec:D3}_";
            var hits = selectors.Where(s => s.m_generators.OfType<hkbClipGenerator>().Any(c => (c.m_animationName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
            Log($"{name} a{sec}: {hits.Count} selectors hold its clips");
            foreach (var s in hits.OrderBy(s => s.m_animId).Take(name == "art" ? 40 : 12))
            {
                var gens = s.m_generators.OfType<hkbClipGenerator>().ToList();
                var ours = gens.Where(c => (c.m_animationName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                var other = gens.FirstOrDefault(c => !ours.Contains(c));
                Log($"  '{s.m_name}' {s.m_offsetType} animId {s.m_animId} ({s.m_generators.Count} generators): ours {string.Join(" ", ours.Select(c => $"{c.m_animationName}#{c.m_animationInternalId} mode {c.m_mode}"))}; e.g. {other?.m_animationName}#{other?.m_animationInternalId} mode {other?.m_mode}");
            }
        }
    }

    private static void Fields(Action<string> log, string label, PARAM.Row row, params string[] names) =>
        log($"{label} {row.ID}: " + string.Join(", ", names.Where(n => row.Cells.Any(c => c.Def.InternalName == n)).Select(n => $"{n} {Cell(row, n)}")));

    private static string Cell(PARAM.Row row, string name) => row[name].Value is byte[] b ? Convert.ToHexString(b) : row[name].Value?.ToString() ?? "";

    private static List<CustomManualSelectorGenerator> LoadSelectors(BND4 behbnd)
    {
        var behaviorFile = behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!;
        var graph = HavokGraph.All((hkRootLevelContainer)new HavokBinarySerializer().Read(new MemoryStream(behaviorFile.Bytes)));
        return graph.OfType<CustomManualSelectorGenerator>().ToList();
    }

    private static void DumpTae(Action<string> log, string source, BND4 main, int section, Func<TAE.Animation, bool> which, bool ds3 = false)
    {
        var file = main.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(ds3 ? $"a{section:D2}.tae" : $"a{section:D3}.tae", StringComparison.OrdinalIgnoreCase))
                   ?? main.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals($"a{section}.tae", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            log($"### {source} a{section}: NOT IN BINDER");
            return;
        }
        TAE tae;
        try { tae = TAE.Read(file.Bytes); }
        catch (Exception e) { log($"### {source} a{section}: unreadable ({e.Message})"); return; }
        var all = tae.Animations.Select(a => a.ID).ToList();
        log($"### {source} a{section} ({ItemText.Leaf(file.Name)} #{file.ID}, TAE id {tae.ID}): {all.Count} animations: {string.Join(" ", all.Where(i => i >= 36000 || all.Count <= 40).Take(80))}");
        foreach (var anim in tae.Animations.Where(which))
        {
            var head = anim.MiniHeader switch
            {
                TAE.Animation.AnimMiniHeader.ImportOtherAnim i => $"import {i.ImportFromAnimID}",
                TAE.Animation.AnimMiniHeader.Standard s => $"standard{(s.ImportsHKX ? $" hkx from {s.ImportHKXSourceAnimID}" : "")}{(s.IsLoopByDefault ? " loop" : "")}",
                var h => h?.GetType().Name ?? "none",
            };
            float end = anim.Events.Count == 0 ? 0 : anim.Events.Where(e => e.EndTime < 99).Select(e => e.EndTime).DefaultIfEmpty(0).Max();
            var counts = anim.Events.GroupBy(e => e.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            log($"  anim {anim.ID} ({head}, file '{anim.AnimFileName}'): {anim.Events.Count} events, last end {end:0.###} s: {string.Join(" ", counts)}");
            foreach (var e in anim.Events.Where(e => Verbose.Contains(e.Type)).OrderBy(e => e.Type).ThenBy(e => e.StartTime))
            {
                var p = e.GetParameterBytes(false);
                var decoded = e.Type switch
                {
                    0 => $"JumpTable {Int(p, 0)}" + (Int(p, 8) is var j && j != -1 ? $" arg {j}" : ""),
                    1 => $"attack judge {Int(p, 8)}, flag {Int(p, 12)}",
                    66 or 67 => $"SpEffect {Int(p, 0)}",
                    331 => $"art buff SpEffect {Int(p, 0)} / no FP {Int(p, 4)}",
                    712 => "model locations " + string.Join(" ", Enumerable.Range(0, 8).Select(i => $"{(i < 4 ? "RH" : "LH")}{i % 4}:{p[2 * i]}/{(sbyte)p[2 * i + 1]}")) + $" tail {Convert.ToHexString(p[16..Math.Min(p.Length, 24)])}",
                    _ => "",
                };
                log($"    {e.Type,4} {e.StartTime,6:0.000}-{e.EndTime,6:0.000} {decoded,-40} {Convert.ToHexString(p)}{(e.Group is null ? " NO GROUP" : "")}");
            }
        }
    }

    private static int Int(byte[] p, int at) => p.Length >= at + 4 ? BitConverter.ToInt32(p, at) : -1;

    private static BND4? ReadModPart(PorterContext ctx, int model)
    {
        var path = ctx.ModPath("parts", $"wp_a_{model:D4}.partsbnd.dcx");
        return File.Exists(path) ? BND4.Read(DCX.Decompress(File.ReadAllBytes(path))) : null;
    }

    private static void DumpModel(Action<string> log, string source, BND4? part, int model)
    {
        if (part is null)
        {
            log($"### {source} wp_a_{model:D4}: no part");
            return;
        }
        foreach (var file in part.Files.Where(f => FLVER2.Is(f.Bytes)))
        {
            var flver = FLVER2.Read(file.Bytes);
            string Node(int i) => i >= 0 && i < flver.Nodes.Count ? $"{i}:{flver.Nodes[i].Name}" : i.ToString();
            log($"### {source} {ItemText.Leaf(file.Name)}: FLVER {flver.Header.Version:X}, {flver.Nodes.Count} nodes, {flver.Meshes.Count} meshes, {flver.Dummies.Count} dummies, bbox {V(flver.Header.BoundingBoxMin)}..{V(flver.Header.BoundingBoxMax)}");
            foreach (var (n, i) in flver.Nodes.Select((n, i) => (n, i)))
                log($"  node {i} '{n.Name}' parent {n.ParentIndex} pos {V(n.Translation)} rot {V(n.Rotation)} scale {V(n.Scale)} flags {n.Flags}");
            foreach (var mesh in flver.Meshes)
                log($"  mesh node {Node(mesh.NodeIndex)} bones [{string.Join(" ", mesh.BoneIndices)}] {mesh.Vertices.Count} vertices, material {flver.Materials[mesh.MaterialIndex].Name}");
            foreach (var d in flver.Dummies.OrderBy(d => d.ReferenceID))
                log($"  dummy {d.ReferenceID} parent {Node(d.ParentBoneIndex)} attach {Node(d.AttachBoneIndex)} pos {V(d.Position)} fwd {V(d.Forward)} up {V(d.Upward)} useUp {d.UseUpwardVector}");
        }
    }

    private static string V(Vector3 v) => $"({v.X:0.###},{v.Y:0.###},{v.Z:0.###})";
}
