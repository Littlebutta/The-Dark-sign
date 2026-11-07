using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using DarkSign.Porter.Maps;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// The enemy porter: one pass over every Dark Souls III enemy and boss placed in DS3's maps, each converted the same
/// way, so whatever is fixed for one is fixed for all (Devin, 2026-11-03: "load everything at a time").
///
/// For each DS3 character it picks the Elden Ring enemy whose skeleton shares the most bone names (and, between
/// equals, the same kind and boss-ness) as its template, gives it a new character ID (c7000-c7999, which Elden Ring
/// doesn't ship) and builds it from the template's files, by method (launcher Fallbacks window, "enemy-port",
/// DARKSIGN_ENEMY_PORT):
/// <list type="bullet">
/// <item>A (default): DS3's model and DS3's animations. The template's skeleton takes DS3's bone positions, DS3's
/// animations replace the template's with the same IDs (so the template's AI and behaviour, which ask for those IDs,
/// play DS3's moves), their attack events come from DS3 with DS3's attack data (BehaviorParam / AtkParam_Npc) under
/// the character's own behaviour variation. Moves DS3 doesn't have, the AI, behaviour, ragdoll, sounds and effects are
/// the template's.</item>
/// <item>B ("model"): DS3's model on the template, which keeps all its own animations, attacks and AI.</item>
/// <item>C ("stats"): no new files: the template's own model with DS3's name and health, so the DS3 maps and the
/// randomizer get the best-matching Elden Ring body instead of a stand-in picked by name.</item>
/// <item>off ("0"): nothing ported; DS3 maps use stand-ins (port-maps' own choice).</item>
/// </list>
/// The result is Mods\ER\darksign_chr_map.tsv (read by port-maps, which places the ported characters at DS3's spots
/// and lists them for the randomizer) and Work\reports\enemy-port.tsv (one line per character). Every character is
/// built on its own: one that fails is left to the stand-ins and the rest go on.
///
/// The steps follow GompDS's DS3PortingTool (which ports characters the other way, into DS3): model materials to the
/// target game's, Havok to the target version, TAE events filtered to what the target understands.
/// </summary>
public static partial class ChrPort
{
    public enum Method { Full, Model, Stats, Off }

    public static Method CurrentMethod => ParseMethod(Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_PORT"));

    public static Method ParseMethod(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "model" or "b" => Method.Model,
        "stats" or "c" => Method.Stats,
        "0" or "off" or "none" => Method.Off,
        _ => Method.Full,
    };

    public const int FirstNewChr = 7000, LastNewChr = 7999;
    public const string ManifestFile = "darksign_chr_files.txt";
    public const string ReportFile = "enemy-port.tsv";

    /// <summary>Below this share of bone names the DS3 model and animations aren't put on a template (C is used).</summary>
    public const double MinBoneMatch = 0.25;

    /// <summary>A DS3 character as the maps use it: its model, the NpcParam rows placed with it, its name.</summary>
    public sealed record Ds3Chr(int Chr, string Name, bool Boss, SortedSet<int> NpcParams);

    /// <summary>The choice for one DS3 character.</summary>
    public sealed record Plan(Ds3Chr Ds3, EnemyPort.ErEnemy Template, int TemplateChr, double BoneMatch, Method Method, int NewChr);

    [GeneratedRegex(@"^c(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex ChrModelName();

    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimLeaf();

    public static int ChrId(string model) => ChrModelName().Match(model) is { Success: true } m ? int.Parse(m.Groups[1].Value) : -1;

    // ---- Planning (pure; offline-tested by enemy-selftest) ----

    /// <summary>Share of bone names two skeletons have in common (Jaccard, case-insensitive; 0 when either is empty).</summary>
    public static double BoneMatch(IEnumerable<string> a, IEnumerable<string> b)
    {
        var x = a.Where(n => n.Length > 0).Select(n => n.ToLowerInvariant()).ToHashSet();
        var y = b.Where(n => n.Length > 0).Select(n => n.ToLowerInvariant()).ToHashSet();
        if (x.Count == 0 || y.Count == 0)
            return 0;
        return x.Intersect(y).Count() / (double)x.Union(y).Count();
    }

    /// <summary>
    /// The template for a DS3 character: the candidate with the best bone match, plus a small bonus for the same kind
    /// (knight, dog...) and the same boss-ness, so equal skeletons prefer the enemy that plays alike.
    /// </summary>
    public static (EnemyPort.ErEnemy Template, double Match)? PickTemplate(Ds3Chr ds3, IReadOnlyList<string> ds3Bones,
        IReadOnlyList<EnemyPort.ErEnemy> candidates, Func<EnemyPort.ErEnemy, IReadOnlyList<string>?> bonesOf)
    {
        var kind = EnemyPort.KindOf(ds3.Name);
        (EnemyPort.ErEnemy, double, double)? best = null;
        foreach (var c in candidates.DistinctBy(c => c.Model, StringComparer.OrdinalIgnoreCase))
        {
            if (bonesOf(c) is not { Count: > 0 } bones)
                continue;
            var match = BoneMatch(ds3Bones, bones);
            var score = match + (kind.Length > 0 && c.Kind == kind ? 0.05 : 0) + (c.Boss == ds3.Boss ? 0.03 : 0);
            if (best is null || score > best.Value.Item3)
                best = (c, match, score);
        }
        return best is { } b ? (b.Item1, b.Item2) : null;
    }

    /// <summary>New character IDs, in DS3 ID order (so every PC gets the same ones), skipping IDs the game has.</summary>
    public static Dictionary<int, int> AssignIds(IEnumerable<int> ds3Chrs, Func<int, bool> taken)
    {
        var ids = new Dictionary<int, int>();
        int next = FirstNewChr;
        foreach (var chr in ds3Chrs.Distinct().Order())
        {
            while (next <= LastNewChr && taken(next))
                next++;
            if (next > LastNewChr)
                break;
            ids[chr] = next++;
        }
        return ids;
    }

    /// <summary>An archive or binder path under the new character ID: every "c&lt;template&gt;" becomes "c&lt;new&gt;".</summary>
    public static string Rename(string path, int from, int to) =>
        Regex.Replace(path, $"c{from:D4}", $"c{to:D4}", RegexOptions.IgnoreCase);

    /// <summary>
    /// Vertex bone indices of a DS3 mesh as indices into the model's node list: DS3 meshes index a per-mesh palette
    /// (Mesh.BoneIndices), Elden Ring's index the nodes directly.
    /// </summary>
    public static void GlobalBoneIndices(FLVER2 flver)
    {
        foreach (var mesh in flver.Meshes)
        {
            if (mesh.BoneIndices is not { Count: > 0 } palette)
                continue;
            foreach (var v in mesh.Vertices)
                for (int i = 0; i < 4; i++)
                {
                    int local = v.BoneIndices[i];
                    v.BoneIndices[i] = local >= 0 && local < palette.Count ? palette[local] : 0;
                }
            mesh.BoneIndices = [];
        }
    }

    /// <summary>darksign_chr_map.tsv lines for a ported character (EnemyPort.ChrMap's format).</summary>
    public static IEnumerable<string> MapLines(Plan plan, IReadOnlyDictionary<int, int> npcRows, int thinkParam)
    {
        var model = plan.Method == Method.Stats ? plan.Template.Model : $"c{plan.NewChr:D4}";
        var name = plan.Ds3.Name.Replace('\t', ' ');
        foreach (var (ds3Npc, erNpc) in npcRows.OrderBy(r => r.Key))
            yield return string.Join('\t', "npc", ds3Npc, model, erNpc, thinkParam, name);
        if (npcRows.Count > 0)
            yield return string.Join('\t', "chr", $"c{plan.Ds3.Chr:D4}", model, npcRows.OrderBy(r => r.Key).First().Value, thinkParam, name);
    }

    // ---- The run ----

    public sealed class Result
    {
        public int Characters, Full, Model, Stats, Failed, Animations, NpcRows;
        public List<string> Lines = [];
        public List<string> Notes = [];
        /// <summary>What the params step adds once port-weapons has written regulation.bin.</summary>
        public List<(Plan Plan, Dictionary<int, int> NpcRows)> Params = [];
        public List<ChrMaterial> Materials = [];
        public string Summary => $"{Characters} DS3 characters: {Full} with DS3 model and animations, {Model} with the DS3 model, " +
                                 $"{Stats} on an Elden Ring body, {Failed} fell back; {Animations} DS3 animations, {NpcRows} NpcParam rows";
    }

    /// <summary>
    /// Step 1 (port-maps, side by side with port-weapons): plans every DS3 character placed in DS3's maps, builds and
    /// writes its files, and writes darksign_chr_map.tsv, so the maps placed after it use the ported characters. The
    /// NpcParam row numbers are picked from Elden Ring's own params, so they are known before regulation.bin exists.
    /// </summary>
    public static Result Build(PorterContext ctx, GameArchives erArchives, GameArchives ds3Archives)
    {
        var result = new Result();
        CleanOutputs(ctx);
        var method = CurrentMethod;
        if (method == Method.Off)
        {
            Console.WriteLine("enemies: off (Fallbacks enemy-port); DS3 maps use stand-ins");
            return result;
        }
        TAE.ValidateEventBank = false;
        var notes = result.Notes;
        var erNpc = ctx.LoadParams(PorterGame.EldenRing).Get("NpcParam") ?? throw new InvalidDataException("Elden Ring has no NpcParam");
        var ds3Npc = ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcParam");
        var catalog = EnemyPort.BuildCatalog(erArchives, erNpc, notes);
        var characters = Ds3Characters(ds3Archives, ds3Npc);
        Console.WriteLine($"enemies: {characters.Count} DS3 characters in DS3's maps, {catalog.Count} Elden Ring templates");

        var erBones = new ConcurrentDictionary<int, IReadOnlyList<string>?>();
        IReadOnlyList<string>? ErBones(EnemyPort.ErEnemy e) => erBones.GetOrAdd(ChrId(e.Model), c => FlverBones(erArchives, c));
        var ids = AssignIds(characters.Select(c => c.Chr), n => erArchives.Contains($"/chr/c{n:D4}.chrbnd.dcx"));

        var plans = new List<Plan>();
        foreach (var ds3Chr in characters)
        {
            var bones = FlverBones(ds3Archives, ds3Chr.Chr) ?? [];
            if (PickTemplate(ds3Chr, bones, catalog, ErBones) is not { } pick || !ids.TryGetValue(ds3Chr.Chr, out var newChr))
            {
                result.Lines.Add(Line(ds3Chr, null, "skipped", "no Elden Ring template or no free character ID"));
                continue;
            }
            var chosen = pick.Match < MinBoneMatch && method != Method.Stats ? Method.Stats : method;
            plans.Add(new Plan(ds3Chr, pick.Template, ChrId(pick.Template.Model), pick.Match, chosen, newChr));
        }

        var mapLines = new List<string> { "# kind\tDS3\tElden Ring model\tNpcParam\tThinkParam\tname (written by DarkSignPorter port-maps, enemy port)" };
        var written = new List<string>();
        // Files are built side by side; the Havok serializer is used one thread at a time (AnimPort.HavokLock).
        var built = new ConcurrentDictionary<int, (ChrFiles? Files, string? Error)>();
        Parallel.ForEach(plans.Where(p => p.Method != Method.Stats), Parallelism.Options, plan =>
        {
            try
            {
                built[plan.Ds3.Chr] = (BuildFiles(ctx, erArchives, ds3Archives, plan), null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                built[plan.Ds3.Chr] = (null, $"{ex.GetType().Name}: {ex.Message}");
            }
        });

        var usedRows = erNpc.Rows.Select(r => r.ID).ToHashSet();
        foreach (var plan in plans.OrderBy(p => p.Ds3.Chr))
        {
            var effective = plan;
            ChrFiles? files = null;
            if (plan.Method != Method.Stats && built.TryGetValue(plan.Ds3.Chr, out var b))
            {
                if (b.Files is null)
                {
                    // Files failed: this character falls back to the template's body (C) instead of a stand-in.
                    notes.Add($"c{plan.Ds3.Chr:D4} {plan.Ds3.Name}: files failed ({b.Error}); using the template's body");
                    result.Failed++;
                    effective = plan with { Method = Method.Stats };
                }
                else
                    files = b.Files;
            }
            var npcRows = NpcRowIds(effective, usedRows);
            if (npcRows.Count == 0)
            {
                result.Lines.Add(Line(plan.Ds3, plan, "skipped", "no free NpcParam rows"));
                continue;
            }
            if (files is not null)
            {
                foreach (var (path, bytes) in files.Outputs)
                {
                    var full = ctx.ModPath(ModParts(path));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, bytes);
                    written.Add(full);
                }
                result.Materials.AddRange(files.Materials);
            }
            result.Params.Add((effective, npcRows));
            mapLines.AddRange(MapLines(effective, npcRows, effective.Template.ThinkParam));
            result.Characters++;
            result.NpcRows += npcRows.Count;
            result.Animations += files?.Animations ?? 0;
            switch (effective.Method)
            {
                case Method.Full: result.Full++; break;
                case Method.Model: result.Model++; break;
                default: result.Stats++; break;
            }
            result.Lines.Add(Line(plan.Ds3, effective, effective.Method.ToString().ToLowerInvariant(),
                files?.Note ?? (plan.Method == Method.Stats && method != Method.Stats ? $"bone match {plan.BoneMatch:0.00} below {MinBoneMatch}" : "")));
        }

        File.WriteAllLines(ctx.ModPath(EnemyPort.ChrMap.FileName), mapLines);
        File.WriteAllLines(ctx.ModPath(ManifestFile), written.Select(w => Path.GetRelativePath(ctx.ModPath(), w)));
        WriteReport(ctx, result);
        Console.WriteLine($"enemies: {result.Summary} (report: Work\\reports\\{ReportFile})");
        foreach (var note in notes.Take(10))
            Console.WriteLine($"enemies: note: {note}");
        return result;
    }

    /// <summary>
    /// Step 2 (port-maps, once port-weapons has written regulation.bin): the param rows and the materials. A failure
    /// here leaves the files but takes the characters back out of darksign_chr_map.tsv... it can't, the maps are
    /// already placed; so rows are added even when a part fails, and the report says what didn't.
    /// </summary>
    public static string ApplyParams(PorterContext ctx, GameArchives erArchives, Result result)
    {
        if (result.Params.Count == 0)
            return "enemies: nothing to add";
        var regulation = ActionButtons.LoadRegulation(ctx);
        var ds3 = ctx.LoadParams(PorterGame.DarkSouls3);
        var attacks = new AttackParamPort(regulation, ds3, "BehaviorParam", "AtkParam_Npc");
        int rows = 0, failed = 0;
        foreach (var (plan, npcRows) in result.Params)
        {
            try
            {
                rows += PortParams(regulation, ds3, attacks, plan, npcRows);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                result.Notes.Add($"c{plan.Ds3.Chr:D4} {plan.Ds3.Name}: params failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
        regulation.Save(ctx.ModPath("regulation.bin"));
        try
        {
            if (result.Materials.Count > 0)
                ChrMaterial.Write(ctx, erArchives, result.Materials);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Notes.Add($"materials: failed ({ex.Message}); ported models may render untextured");
        }
        WriteReport(ctx, result);
        return $"enemies: {rows} NpcParam rows, {attacks.Summary}{(failed > 0 ? $", {failed} characters' params failed" : "")}";
    }

    private static void WriteReport(PorterContext ctx, Result result)
    {
        var report = new List<string> { "DS3 chr\tname\tboss\ttemplate\ttemplate name\tbone match\tnew chr\tmethod\tnotes" };
        report.AddRange(result.Lines);
        report.AddRange(result.Notes.Select(n => "# " + n));
        File.WriteAllLines(ctx.WorkPath("reports", ReportFile), report);
    }

    /// <summary>New NpcParam row IDs for a character: its new character ID * 10000 and up, one per DS3 row placed with it.</summary>
    public static Dictionary<int, int> NpcRowIds(Plan plan, HashSet<int> used)
    {
        var rows = new Dictionary<int, int>();
        int baseId = plan.NewChr * 10000, next = baseId;
        foreach (var ds3Id in plan.Ds3.NpcParams)
        {
            while (used.Contains(next) && next < baseId + 9999)
                next++;
            if (used.Contains(next))
                break;
            used.Add(next);
            rows[ds3Id] = next++;
        }
        return rows;
    }

    private static string Line(Ds3Chr ds3, Plan? plan, string what, string note) => string.Join('\t',
        $"c{ds3.Chr:D4}", ds3.Name.Replace('\t', ' '), ds3.Boss ? 1 : 0, plan?.Template.Model ?? "", plan?.Template.Name.Replace('\t', ' ') ?? "",
        plan is null ? "" : plan.BoneMatch.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        plan is null || plan.Method == Method.Stats ? "" : $"c{plan.NewChr:D4}", what, note.Replace('\t', ' '));

    /// <summary>Deletes what the last enemy port wrote (its manifest), so a method change or a smaller port leaves nothing behind.</summary>
    public static void CleanOutputs(PorterContext ctx)
    {
        var manifest = ctx.ModPath(ManifestFile);
        if (File.Exists(manifest))
            foreach (var rel in File.ReadAllLines(manifest).Where(l => l.Length > 0))
            {
                var full = Path.GetFullPath(Path.Combine(ctx.ModPath(), rel));
                if (full.StartsWith(Path.GetFullPath(ctx.ModPath()), StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                    && !full.EndsWith("regulation.bin", StringComparison.OrdinalIgnoreCase))
                    File.Delete(full);
            }
        foreach (var file in new[] { manifest, ctx.ModPath(EnemyPort.ChrMap.FileName) })
            if (File.Exists(file))
                File.Delete(file);
    }

    private static string[] ModParts(string archivePath) => archivePath.TrimStart('/').Split('/');

    // ---- DS3 side ----

    /// <summary>Every enemy character (not c0000 humans, not talkers) placed in DS3's single-player maps.</summary>
    public static List<Ds3Chr> Ds3Characters(GameArchives ds3, PARAM? ds3Npc)
    {
        var byChr = new SortedDictionary<int, Ds3Chr>();
        foreach (var (id, _) in MapSlots.Ds3Maps)
        {
            var mapId = MapId.Parse(id);
            var path = MapPorter.MsbPath(mapId);
            if (!ds3.Contains(path))
                continue;
            MSB3 msb;
            try { msb = MSB3.Read(ds3.Read(path)); }
            catch (Exception) { continue; }
            var scripts = new List<EMEVD>();
            foreach (var script in new[] { MapEvents.ScriptPath(mapId), MapEvents.CommonFunc })
                if (ds3.Contains(script))
                    try { scripts.Add(EMEVD.Read(ds3.Read(script))); } catch (Exception) { }
            var bosses = EnemyPort.BossEntities(scripts, ds3Layout: true);
            foreach (var e in msb.Parts.Enemies)
            {
                int chr = ChrId(e.ModelName);
                if (chr <= 0 || e.TalkID > 0 || e.NPCParamID <= 0)
                    continue;
                var name = ds3Npc?[e.NPCParamID]?.Name is { Length: > 0 } n ? n : $"c{chr:D4}";
                if (!byChr.TryGetValue(chr, out var known))
                    byChr[chr] = known = new Ds3Chr(chr, name, false, []);
                known.NpcParams.Add(e.NPCParamID);
                if (bosses.Contains(e.EntityID) && !known.Boss)
                    byChr[chr] = known with { Boss = true };
            }
        }
        return byChr.Values.ToList();
    }

    /// <summary>The bone (node) names of a character's model, or null when it has no readable model.</summary>
    public static IReadOnlyList<string>? FlverBones(GameArchives archives, int chr)
    {
        var path = $"/chr/c{chr:D4}.chrbnd.dcx";
        if (chr <= 0 || !archives.Contains(path))
            return null;
        try
        {
            var bnd = BND4.Read(archives.Read(path));
            var flver = bnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes));
            return flver is null ? null : FLVER2.Read(flver.Bytes).Nodes.Select(n => n.Name ?? "").ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Files ----

    /// <summary>Everything one character's port writes (archive path -> bytes) and the materials its model needs.</summary>
    public sealed class ChrFiles
    {
        public List<(string Path, byte[] Bytes)> Outputs = [];
        public List<ChrMaterial> Materials = [];
        public int Animations;
        public string Note = "";
        public int TemplateVariation;
    }

    private static readonly string[] ClonedFolders = ["/chr/", "/sfx/"];

    private static ChrFiles BuildFiles(PorterContext ctx, GameArchives er, GameArchives ds3, Plan plan)
    {
        int t = plan.TemplateChr, n = plan.NewChr, x = plan.Ds3.Chr;
        var files = new ChrFiles();
        var notes = new List<string>();
        // The template's character and effect files, under the new ID. Its sound bank isn't copied: the new NpcParam
        // rows load the template's own bank (SoundAddBankId), so its sound events keep playing.
        var binders = new Dictionary<string, (BND4 Bnd, DCX.CompressionInfo Compression)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in er.Entries.Where(e => e.Path is { } p && ClonedFolders.Any(f => p.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                                                     && Regex.IsMatch(ItemText.Leaf(p), $@"(^|_)c{t:D4}(\.|_)", RegexOptions.IgnoreCase))
                     .OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            var (bytes, compression) = er.ReadWithCompression(entry.Path!);
            if (BND4.Is(bytes))
            {
                var bnd = BND4.Read(bytes);
                foreach (var f in bnd.Files)
                    f.Name = Rename(f.Name, t, n);
                binders[Rename(entry.Path!, t, n)] = (bnd, compression);
            }
            else
                files.Outputs.Add((Rename(entry.Path!, t, n), DCX.Compress(bytes, compression)));
        }
        var chrbndPath = $"/chr/c{n:D4}.chrbnd.dcx";
        if (!binders.ContainsKey(chrbndPath))
            throw new InvalidDataException($"template c{t:D4} has no chrbnd");

        // The DS3 model, on the template's binder.
        var ds3Chrbnd = BND4.Read(ds3.Read($"/chr/c{x:D4}.chrbnd.dcx"));
        var ds3FlverFile = ds3Chrbnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes)) ?? throw new InvalidDataException($"DS3 c{x:D4} has no model");
        var chrbnd = binders[chrbndPath].Bnd;
        var templateFlverFile = chrbnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes)) ?? throw new InvalidDataException($"template c{t:D4} has no model");
        var templateFlver = FLVER2.Read(templateFlverFile.Bytes);
        var flver = FLVER2.Read(ds3FlverFile.Bytes);
        files.Materials.AddRange(ChrModel.Convert(flver, templateFlver, x, n));
        templateFlverFile.Bytes = flver.Write();

        // DS3's textures, renamed to the new ID, next to the template's in its high-quality texture binder.
        var ds3Textures = ds3.Entries.Where(e => e.Path is { } p && p.StartsWith($"/chr/c{x:D4}", StringComparison.OrdinalIgnoreCase)
                                                 && p.EndsWith(".texbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).ToList();
        var tpfs = new List<TPF>();
        foreach (var path in ds3Textures)
            foreach (var f in BND4.Read(ds3.Read(path)).Files.Where(f => TPF.Is(f.Bytes)))
                tpfs.Add(ChrModel.ConvertTpf(TPF.Read(f.Bytes), x, n));
        foreach (var f in ds3Chrbnd.Files.Where(f => TPF.Is(f.Bytes)))
            tpfs.Add(ChrModel.ConvertTpf(TPF.Read(f.Bytes), x, n));
        var texbnd = binders.Keys.Where(k => k.EndsWith(".texbnd.dcx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k.Contains("_h", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        if (tpfs.Count > 0)
        {
            var merged = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
            merged.Textures.AddRange(tpfs.SelectMany(p => p.Textures).DistinctBy(tx => tx.Name, StringComparer.OrdinalIgnoreCase));
            var target = texbnd is not null ? binders[texbnd].Bnd : chrbnd;
            var like = target.Files.FirstOrDefault(f => TPF.Is(f.Bytes)) ?? target.Files[0];
            var folder = like.Name[..^ItemText.Leaf(like.Name).Length];
            target.Files.Add(new BinderFile(like.Flags, target.Files.Max(f => f.ID) + 1, $"{folder}c{n:D4}_ds3.tpf", merged.Write()));
            notes.Add($"{merged.Textures.Count} DS3 textures");
        }

        if (plan.Method == Method.Full)
        {
            var animated = PortAnimations(ds3, x, binders, n, notes);
            files.Animations = animated;
        }

        foreach (var (path, (bnd, compression)) in binders.OrderBy(b => b.Key, StringComparer.Ordinal))
            files.Outputs.Add((path, DCX.Compress(bnd.Write(), compression)));
        files.Note = string.Join("; ", notes);
        return files;
    }

    /// <summary>
    /// Method A: DS3's bone positions in the template's skeleton, and DS3's animations in place of the template's with
    /// the same IDs, with their TAE events merged (template's behaviour windows, sounds and effects, stretched to the
    /// DS3 move's length; DS3's attacks and the rest where both games lay the event out the same).
    /// </summary>
    private static int PortAnimations(GameArchives ds3, int x, Dictionary<string, (BND4 Bnd, DCX.CompressionInfo Compression)> binders, int n, List<string> notes)
    {
        var ds3Anibnds = ds3.Entries.Where(e => e.Path is { } p && p.StartsWith($"/chr/c{x:D4}", StringComparison.OrdinalIgnoreCase)
                                                && p.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase))
            .Select(e => BND4.Read(ds3.Read(e.Path!))).ToList();
        var ds3Files = ds3Anibnds.SelectMany(b => b.Files).ToList();
        var ds3SkeletonFile = ds3Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
        if (ds3SkeletonFile is null)
        {
            notes.Add("DS3 has no skeleton; template animations kept");
            return 0;
        }
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(ds3SkeletonFile.Bytes);
        var ds3Hkx = ds3Files.Where(f => AnimLeaf().IsMatch(ItemText.Leaf(f.Name)))
            .GroupBy(f => ItemText.Leaf(f.Name).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Bytes);
        var ds3Events = TaeIndex(ds3Files);

        var anibnds = binders.Where(b => b.Key.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)).ToList();
        var main = anibnds.FirstOrDefault(b => b.Key.Equals($"/chr/c{n:D4}.anibnd.dcx", StringComparison.OrdinalIgnoreCase));
        if (main.Value.Bnd is null)
        {
            notes.Add("template has no anibnd; template animations kept");
            return 0;
        }
        var skeletonFile = main.Value.Bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
        if (skeletonFile is null)
        {
            notes.Add("template has no skeleton.hkx; template animations kept");
            return 0;
        }
        var compendium = Compendium(main.Value.Bnd);
        AnimPort.ErSkeleton erSkeleton;
        lock (AnimPort.HavokLock)
        {
            // The template's skeleton with DS3's bone positions, so DS3's animations and model need no stretching.
            var root = (hkRootLevelContainer)AnimPort.ReadEr(skeletonFile.Bytes, compendium);
            var skeleton = AnimPort.Container(root).m_skeletons.First()!;
            var ds3Index = ds3Skeleton.Names.Select((name, i) => (name, i)).GroupBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
            int moved = 0;
            for (int b = 0; b < skeleton.m_bones.Count && b < skeleton.m_referencePose.Count; b++)
            {
                if (!ds3Index.TryGetValue(skeleton.m_bones[b].m_name ?? "", out var d))
                    continue;
                var pose = ds3Skeleton.ReferencePose[d];
                skeleton.m_referencePose[b] = new hkQsTransform
                {
                    m_translation = new Vector4(pose.Translation, 0),
                    m_rotation = pose.Rotation,
                    m_scale = new Vector4(pose.Scale, 0),
                };
                moved++;
            }
            using var output = new MemoryStream();
            new HavokBinarySerializer().Write(root, output);
            skeletonFile.Bytes = output.ToArray();
            erSkeleton = new AnimPort.ErSkeleton { Names = skeleton.m_bones.Select(bone => bone.m_name ?? "").ToList(), ReferencePose = skeleton.m_referencePose };
            notes.Add($"{moved}/{skeleton.m_bones.Count} skeleton bones from DS3");
        }

        int replaced = 0, failed = 0;
        var durations = new Dictionary<(int, int), (float Ds3, float Er)>();
        foreach (var (_, (bnd, _)) in anibnds)
        {
            var bndCompendium = Compendium(bnd) ?? compendium;
            foreach (var file in bnd.Files)
            {
                var leaf = ItemText.Leaf(file.Name).ToLowerInvariant();
                if (!AnimLeaf().IsMatch(leaf) || !ds3Hkx.TryGetValue(leaf, out var ds3Bytes))
                    continue;
                try
                {
                    var (hkx, _, templateDuration) = AnimPort.ConvertTimed(ds3Bytes, ds3Skeleton, erSkeleton, file.Bytes, bndCompendium, AnimPort.Spline);
                    file.Bytes = hkx;
                    var m = AnimLeaf().Match(leaf);
                    durations[(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))] = (AnimPort.Duration(ds3Bytes), templateDuration);
                    replaced++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed++;
                }
            }
        }
        int merged = 0;
        foreach (var (_, (bnd, _)) in anibnds)
            foreach (var file in bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
            {
                TAE tae;
                try { tae = TAE.Read(file.Bytes); }
                catch (Exception) { continue; }
                int section = TaeSection(file.Name);
                var lengths = tae.Animations.SelectMany(a => a.Events).GroupBy(e => e.Type)
                    .ToDictionary(g => g.Key, g => g.First().GetParameterBytes(false).Length);
                var groups = tae.Animations.SelectMany(a => a.Events).Where(e => e.Group is not null)
                    .GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First().Group!);
                bool changed = false;
                foreach (var anim in tae.Animations)
                {
                    var key = AnimKey(anim.ID, section);
                    if (!durations.TryGetValue(key, out var d) || !ds3Events.TryGetValue(key, out var events))
                        continue;
                    anim.Events = ChrTae.Merge(events, anim, lengths, groups, d.Ds3, d.Er);
                    changed = true;
                    merged++;
                }
                if (changed)
                    file.Bytes = tae.Write();
            }
        notes.Add($"{replaced} DS3 animations ({failed} failed), {merged} with DS3 events");
        return replaced;
    }

    private static byte[]? Compendium(BND4 bnd) =>
        bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;

    /// <summary>A TAE file's section: "a00.tae"/"a000.tae" -> 0; a character's own "c1020.tae" -> 0.</summary>
    public static int TaeSection(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(ItemText.Leaf(name));
        return stem.Length > 1 && (stem[0] is 'a' or 'A') && int.TryParse(stem[1..], out var s) ? s : 0;
    }

    /// <summary>(section, animation) of a TAE animation ID, which is either the bare number or section * 1e6 + number.</summary>
    public static (int Section, int Id) AnimKey(long id, int fileSection) =>
        id >= 1_000_000 ? ((int)(id / 1_000_000), (int)(id % 1_000_000)) : (fileSection, (int)id);

    /// <summary>DS3 TAE events by (section, animation), following imports of another animation's events.</summary>
    private static Dictionary<(int, int), List<TAE.Event>> TaeIndex(IEnumerable<BinderFile> files)
    {
        var index = new Dictionary<(int, int), List<TAE.Event>>();
        foreach (var file in files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
        {
            TAE tae;
            try { tae = TAE.Read(file.Bytes); }
            catch (Exception) { continue; }
            int section = TaeSection(file.Name);
            var byId = tae.Animations.GroupBy(a => a.ID).ToDictionary(g => g.Key, g => g.First());
            foreach (var anim in tae.Animations)
            {
                var source = anim;
                for (int hop = 0; hop < 4 && source.MiniHeader is TAE.Animation.AnimMiniHeader.ImportOtherAnim imp
                                          && byId.TryGetValue(imp.ImportFromAnimID, out var other); hop++)
                    source = other;
                index.TryAdd(AnimKey(anim.ID, section), source.Events);
            }
        }
        return index;
    }

    // ---- Params ----

    /// <summary>
    /// The new character's param rows: ChrModelParam (copied from the template), one NpcParam row per DS3 NpcParam
    /// row placed with it (the template's row with DS3's health and souls, the template's sound bank, and for method A
    /// DS3's attack data under its own behaviour variation). Returns how many NpcParam rows were added.
    /// </summary>
    private static int PortParams(ParamStore er, ParamStore ds3, AttackParamPort attacks, Plan plan, IReadOnlyDictionary<int, int> npcRows)
    {
        var npc = er.Get("NpcParam")!;
        var ds3Npc = ds3.Get("NpcParam");
        var template = npc[plan.Template.NpcParam] ?? throw new InvalidDataException($"template NpcParam {plan.Template.NpcParam} is missing");
        bool newModel = plan.Method != Method.Stats;
        if (newModel && er.Get("ChrModelParam") is { } models && models[plan.TemplateChr] is { } modelRow && models[plan.NewChr] is null)
        {
            models.Rows.Add(CopyRow(models, modelRow, plan.NewChr));
            models.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        }
        int added = 0;
        foreach (var (ds3Id, id) in npcRows.OrderBy(r => r.Key))
        {
            if (npc[id] is not null)
                continue;
            var row = CopyRow(npc, template, id);
            row.Name = $"[DS3] {ds3Npc?[ds3Id]?.Name ?? plan.Ds3.Name}";
            if (ds3Npc?[ds3Id] is { } source)
                foreach (var field in new[] { "hp", "getSoul", "stamina", "mp" })
                    if (Cell(source, field) is { } from && Cell(row, field) is { } to)
                        Set(to, from.Value);
            if (newModel && Cell(row, "SoundAddBankId") is { } bank && Convert.ToInt32(bank.Value) < 0)
                Set(bank, plan.TemplateChr);
            if (plan.Method == Method.Full && ds3Npc?[ds3Id] is { } src && Cell(src, "behaviorVariationId") is { } ds3Var
                && Cell(row, "behaviorVariationId") is { } erVar)
            {
                int variation = attacks.PortVariation(Convert.ToInt32(ds3Var.Value), Convert.ToInt32(erVar.Value), plan.NewChr * 100);
                if (variation > 0)
                    Set(erVar, variation);
            }
            npc.Rows.Add(row);
            added++;
        }
        npc.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return added;
    }

    private static PARAM.Row CopyRow(PARAM param, PARAM.Row from, int id)
    {
        var row = new PARAM.Row(id, from.Name ?? "", param.AppliedParamdef);
        for (int i = 0; i < row.Cells.Count; i++)
            row.Cells[i].Value = from.Cells[i].Value is byte[] b ? b.Clone() : from.Cells[i].Value;
        return row;
    }

    private static PARAM.Cell? Cell(PARAM.Row row, string name) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void Set(PARAM.Cell cell, object? value)
    {
        if (value is null || cell.Value is null)
            return;
        try { cell.Value = Convert.ChangeType(value, cell.Value.GetType()); }
        catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException) { }
    }

    public static IEnumerable<string> OutputFiles(PorterContext ctx) => [ctx.ModPath(ManifestFile), ctx.ModPath(EnemyPort.ChrMap.FileName)];

    internal static string Describe(Plan p) => new StringBuilder()
        .Append($"c{p.Ds3.Chr:D4} {p.Ds3.Name} -> {p.Template.Model} ({p.Template.Name}, bone match {p.BoneMatch:0.00}) as c{p.NewChr:D4}, {p.Method}")
        .ToString();
}
