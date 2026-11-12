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
/// animations replace the template's with the same IDs (so the behaviour graph, which asks for those IDs, plays DS3's
/// moves), their attack events come from DS3 with DS3's attack data (BehaviorParam / AtkParam_Npc) under the
/// character's own behaviour variation. The character thinks with DS3's own AI scripts and NpcThinkParam senses
/// (<see cref="AiPort"/>), DS3 attacks the template has no slot for get new behaviour states (<see cref="ChrMoves"/>),
/// and the template's own SpEffects (a skeleton's revival) are taken off (<see cref="TemplateEffects"/>). The
/// behaviour graph's other states, ragdoll and anything DS3 doesn't have are the template's.</item>
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

    /// <summary>Below this share of DS3's bones on the template the DS3 model and animations aren't put on it (C is used).</summary>
    public const double MinBoneMatch = 0.25;

    /// <summary>A DS3 character as the maps use it: its model, the NpcParam rows placed with it, its name.</summary>
    public sealed record Ds3Chr(int Chr, string Name, bool Boss, SortedSet<int> NpcParams);

    /// <summary>The choice for one DS3 character.</summary>
    public sealed record Plan(Ds3Chr Ds3, EnemyPort.ErEnemy Template, int TemplateChr, double BoneMatch, Method Method, int NewChr, Stage Stage = Stage.All);

    /// <summary>
    /// How far a character's port goes. Every ported character is <see cref="All"/>; the others are the test set
    /// (<see cref="TestPlans"/>): the same character built one step further each time, so a fault seen in game (an
    /// enemy that freezes once it is hit) can be pinned to the step that brings it in with one play session.
    /// </summary>
    public enum Stage
    {
        /// <summary>The template's files under the new ID, nothing from DS3; the template's NpcParam row.</summary>
        Clone = 1,
        /// <summary>+ DS3's model and textures.</summary>
        Model,
        /// <summary>+ DS3's bones in the animation skeleton (the template's animations still play).</summary>
        Skeleton,
        /// <summary>+ DS3's animations in the template's files (the template's events untouched).</summary>
        Moves,
        /// <summary>+ DS3's events in the moves: the full port's files, still on the template's NpcParam row.</summary>
        Events,
        /// <summary>The full port: + DS3's health and attack data.</summary>
        All,
    }

    /// <summary>What a test stage adds, for the F1 spawn list and the report.</summary>
    public static string StageName(Stage stage) => stage switch
    {
        Stage.Clone => "Elden Ring template files only",
        Stage.Model => "+ DS3 model and textures",
        Stage.Skeleton => "+ DS3 bones",
        Stage.Moves => "+ DS3 animations",
        Stage.Events => "+ DS3 move events",
        _ => "full port",
    };

    /// <summary>DS3 characters the test set is built for: the Jailer, the enemy every playtest so far has used.</summary>
    public static readonly int[] TestChrs = [2230];

    /// <summary>Health of the test set's characters: enough to be hit many times by a strong character.</summary>
    public const int TestHp = 200_000;

    /// <summary>
    /// The test set: each <see cref="TestChrs"/> character that is fully ported, once more per stage before
    /// <see cref="Stage.All"/>, under character IDs taken from the top of the pool (so the ordinary characters keep
    /// theirs from build to build).
    /// </summary>
    public static List<Plan> TestPlans(IReadOnlyList<Plan> plans, Func<int, bool> taken)
    {
        var tests = new List<Plan>();
        var used = plans.Select(p => p.NewChr).ToHashSet();
        int next = LastNewChr;
        foreach (var plan in plans.Where(p => p.Method == Method.Full && TestChrs.Contains(p.Ds3.Chr)).OrderBy(p => p.Ds3.Chr))
            foreach (var stage in new[] { Stage.Clone, Stage.Model, Stage.Skeleton, Stage.Moves, Stage.Events })
            {
                while (next >= FirstNewChr && (used.Contains(next) || taken(next)))
                    next--;
                if (next < FirstNewChr)
                    return tests;
                used.Add(next);
                tests.Add(plan with { NewChr = next--, Stage = stage });
            }
        return tests;
    }

    [GeneratedRegex(@"^c(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex ChrModelName();

    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimLeaf();

    public static int ChrId(string model) => ChrModelName().Match(model) is { Success: true } m ? int.Parse(m.Groups[1].Value) : -1;

    // ---- Planning (pure; offline-tested by enemy-selftest) ----

    /// <summary>
    /// A character's animation skeleton as the template choice sees it: bones, their parents, and how big it stands
    /// in its rest pose (metres; 0 = unknown): Size is the diagonal of the box around the middle 80% of its bones on
    /// each axis (helper bones stand far off: every skeleton has a throw target 3 m up), Head the height of its
    /// "Head" bone when it has one (the Imp 0.96, the Jailer 2.20, Lorian 4.29).
    /// </summary>
    public sealed record Shape(IReadOnlyList<string> Names, IReadOnlyList<short> Parents, float Size = 0, float Head = 0);

    /// <summary>
    /// How many DS3 bones land on a bone the template has (the same count <see cref="MergeSkeleton"/> reports as
    /// shared + moved under their DS3 parent): those are the bones the template's ragdoll, hit shapes and behaviour
    /// know. Without parents on either side, the names both have.
    /// </summary>
    public static int KeptBones(Shape template, Shape ds3)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < template.Names.Count; b++)
            index.TryAdd(template.Names[b], b);
        if (template.Parents.Count != template.Names.Count || ds3.Parents.Count != ds3.Names.Count)
            return ds3.Names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(index.ContainsKey);
        var target = new int[ds3.Names.Count];
        int next = template.Names.Count, kept = 0;
        for (int d = 0; d < ds3.Names.Count; d++)
        {
            target[d] = -1;
            int ds3Parent = ds3.Parents[d];
            int parent = ds3Parent >= 0 && ds3Parent < d ? target[ds3Parent] : -1;
            if (ds3Parent >= 0 && parent < 0)
                continue;
            if (index.TryGetValue(ds3.Names[d], out var b) && (template.Parents[b] == parent || parent < b))
            {
                target[d] = b;
                kept++;
            }
            else
            {
                index[ds3.Names[d]] = target[d] = next++;
            }
        }
        return kept;
    }

    /// <summary>Share of bone names two skeletons have in common (Jaccard, case-insensitive; 0 when either is empty).</summary>
    public static double BoneMatch(IEnumerable<string> a, IEnumerable<string> b)
    {
        var x = a.Where(n => n.Length > 0).Select(n => n.ToLowerInvariant()).ToHashSet();
        var y = b.Where(n => n.Length > 0).Select(n => n.ToLowerInvariant()).ToHashSet();
        if (x.Count == 0 || y.Count == 0)
            return 0;
        return x.Intersect(y).Count() / (double)x.Union(y).Count();
    }

    /// <summary>How alike two sizes are, 0 to 1 (1 = equal; 0.5 when either is unknown).</summary>
    public static double SizeMatch(float a, float b) => a <= 0 || b <= 0 ? 0.5 : Math.Min(a, b) / Math.Max(a, b);

    /// <summary>How alike two characters' sizes are: by head height when both have a head, else by their boxes.</summary>
    public static double SizeMatch(Shape a, Shape b) => a.Head > 0 && b.Head > 0 ? SizeMatch(a.Head, b.Head) : SizeMatch(a.Size, b.Size);

    /// <summary>Below this size match a template is only used when nothing fits (a dragon's files for a skeleton).</summary>
    public const double MinSizeMatch = 0.2;

    /// <summary>
    /// The template for a DS3 character. The template gives the new character everything DS3's files don't: its AI,
    /// its behaviour (which moves follow which), its ragdoll and hit shapes. So the best one has DS3's bones (half
    /// the score: the share of DS3's bones it has), stands as big (0.3: an Imp's AI keeps an Imp's distance, wrong
    /// for the Nameless King), and is the same kind (0.15: knight, dog...) and boss-ness (0.05). Until 1.0.271 the
    /// score was the share of bone names in common out of all names of both, which the smallest skeleton with the
    /// usual human bone names won nearly every time: 58 of 93 DS3 enemies were Imps and 26 Giant Beast Skeletons.
    /// Match is the share of DS3's bones the template has. A template with too few of DS3's bones to carry its
    /// model (<see cref="MinBoneMatch"/>) or of a wholly different size comes after every one that fits, whatever
    /// its score: the DS3 model is only given up when no template can carry it.
    /// </summary>
    public static (EnemyPort.ErEnemy Template, double Match)? PickTemplate(Ds3Chr ds3, Shape ds3Shape,
        IReadOnlyList<EnemyPort.ErEnemy> candidates, Func<EnemyPort.ErEnemy, Shape?> shapeOf) =>
        RankTemplates(ds3, ds3Shape, candidates, shapeOf).Select(r => ((EnemyPort.ErEnemy, double)?)(r.Template, r.Match)).FirstOrDefault();

    /// <summary>Every candidate with a skeleton, best first (the diagnostics list the first few).</summary>
    public static List<(EnemyPort.ErEnemy Template, double Match, double Size, double Score)> RankTemplates(Ds3Chr ds3, Shape ds3Shape,
        IReadOnlyList<EnemyPort.ErEnemy> candidates, Func<EnemyPort.ErEnemy, Shape?> shapeOf)
    {
        var kind = EnemyPort.KindOf(ds3.Name);
        int bones = ds3Shape.Names.Count(n => n.Length > 0);
        var ranked = new List<(EnemyPort.ErEnemy, double, double, double)>();
        // Per model, the placement that fits best (a model placed as a boss and as a plain enemy is both).
        foreach (var group in candidates.GroupBy(c => c.Model, StringComparer.OrdinalIgnoreCase))
        {
            if (shapeOf(group.First()) is not { Names.Count: > 0 } shape)
                continue;
            double match = bones == 0 ? 0 : KeptBones(shape, ds3Shape) / (double)bones;
            double size = SizeMatch(shape, ds3Shape);
            var c = group.OrderByDescending(c => (kind.Length > 0 && c.Kind == kind ? 3 : 0) + (c.Boss == ds3.Boss ? 1 : 0)).First();
            double score = 0.5 * match + 0.3 * size + (kind.Length > 0 && c.Kind == kind ? 0.15 : 0) + (c.Boss == ds3.Boss ? 0.05 : 0);
            ranked.Add((c, match, size, score));
        }
        return ranked.OrderByDescending(r => r.Item2 >= MinBoneMatch && r.Item3 >= MinSizeMatch).ThenByDescending(r => r.Item3 >= MinSizeMatch)
            .ThenByDescending(r => r.Item4)
            .ThenBy(r => r.Item1.Model, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The Elden Ring body for a DS3 character whose own model isn't ported (method C: the bone match is below
    /// <see cref="MinBoneMatch"/>, or the template has no animations). The player sees this body, so it is picked
    /// by what the enemy is, not by leftover bone names: an enemy of the same kind and boss-ness, else of the same
    /// kind, else of the same boss-ness, else any; the bone match only ranks within the first group that has one.
    /// Until 1.0.271 the best bone match was kept, which made DS3's skeletons deer and Midir an eagle.
    /// </summary>
    public static (EnemyPort.ErEnemy Template, double Match)? PickBody(Ds3Chr ds3, IReadOnlyList<string> ds3Bones,
        IReadOnlyList<EnemyPort.ErEnemy> candidates, Func<EnemyPort.ErEnemy, IReadOnlyList<string>?> bonesOf)
    {
        var kind = EnemyPort.KindOf(ds3.Name);
        var bodies = candidates.DistinctBy(c => c.Model, StringComparer.OrdinalIgnoreCase)
            .Where(c => !DarkSign.Randomizer.Logic.EnemyKinds.IsBystander(c.Name) && bonesOf(c) is { Count: > 0 }).ToList();
        Func<EnemyPort.ErEnemy, bool>[] groups =
        [
            c => kind.Length > 0 && c.Kind == kind && c.Boss == ds3.Boss,
            c => kind.Length > 0 && c.Kind == kind,
            c => c.Boss == ds3.Boss,
            _ => true,
        ];
        foreach (var group in groups)
        {
            (EnemyPort.ErEnemy, double)? best = null;
            foreach (var c in bodies.Where(group))
            {
                var match = BoneMatch(ds3Bones, bonesOf(c)!);
                if (best is null || match > best.Value.Item2)
                    best = (c, match);
            }
            if (best is not null)
                return best;
        }
        return null;
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
    /// Renames the character ID inside a Havok file's own strings, in place. A behbnd's project data names its
    /// character and behaviour files (Characters\c4300.hkx, Behaviors\c4300.hkx) and the character's string data names
    /// its skeleton, ragdoll and animation files; the game looks every one of them up by name when the behbnd loads.
    /// Renaming the binder entries alone leaves those lookups finding nothing: every spawn of a cloned character
    /// crashed right after its behbnd loaded (2026-11-08 me3 logs, crash in the behaviour set-up at RVA 0x1421be8
    /// reading the result of a failed keyed lookup). Both IDs are 5 characters, so the bytes are replaced in place
    /// and no offset in the file moves. Only an ID that stands on its own is replaced (not part of a longer name or
    /// number). Returns how many occurrences changed.
    /// </summary>
    public static int RenameInside(byte[] bytes, int from, int to)
    {
        var old = System.Text.Encoding.ASCII.GetBytes($"c{from:D4}");
        var fresh = System.Text.Encoding.ASCII.GetBytes($"c{to:D4}");
        int count = 0;
        for (int i = 0; i + old.Length <= bytes.Length; i++)
        {
            if ((bytes[i] | 0x20) != old[0])
                continue;
            bool match = true;
            for (int k = 1; k < old.Length && match; k++)
                match = bytes[i + k] == old[k];
            if (!match)
                continue;
            if (i > 0 && IsNameChar(bytes[i - 1]))
                continue;
            if (i + old.Length < bytes.Length && bytes[i + old.Length] is >= (byte)'0' and <= (byte)'9')
                continue;
            for (int k = 1; k < old.Length; k++)
                bytes[i + k] = fresh[k];
            count++;
            i += old.Length - 1;
        }
        return count;

        static bool IsNameChar(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';
    }

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

    /// <summary>A chr map npc/chr line put back on its template's ThinkParam (when the AI port's scripts couldn't be written).</summary>
    private static string RestoreThink(string line, IEnumerable<Plan> plans)
    {
        var p = line.Split('\t');
        if (p.Length < 5 || p[0] is not ("npc" or "chr"))
            return line;
        var plan = plans.FirstOrDefault(x => $"c{x.NewChr:D4}" == p[2] || x.Template.Model == p[2]);
        if (plan is null)
            return line;
        p[4] = plan.Template.ThinkParam.ToString();
        return string.Join('\t', p);
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
        public int Characters, Full, Model, Stats, Failed, Animations, NpcRows, Tests;
        public List<string> Lines = [];
        public List<string> Notes = [];
        /// <summary>What the params step adds once port-weapons has written regulation.bin.</summary>
        public List<(Plan Plan, Dictionary<int, int> NpcRows)> Params = [];
        public List<ChrMaterial> Materials = [];
        /// <summary>Both games' item names, read while DS3's archives are open, for the drops (<see cref="DropPort"/>).</summary>
        public DropPort.Names? ItemNames;
        /// <summary>The effect, sound and ability port of this build (null: off or failed, the templates' stay).</summary>
        public ChrEffects? Effects;
        /// <summary>DS3's AI for the fully ported characters (<see cref="AiPort"/>): their NpcThinkParam rows, added by the params step.</summary>
        public List<(Plan Plan, AiPort.ChrAi Ai)> Ai = [];
        /// <summary>
        /// What went wrong when DS3 models that should have been built weren't: how many, and the reason most of them
        /// share (one cause usually fails them all). Null when every planned model was built. Printed as a FAIL line
        /// by the build and by map-validate, so a build with no DS3 enemy models can't pass for a good one
        /// (1.0.234-1.0.242 did: 93 of 93 failed and the report still read "0 FAIL").
        /// </summary>
        public string? Problem => FailureSummary(Failed, Full + Model, Notes);

        public string Summary => $"{Characters} DS3 characters: {Full} with DS3 model and animations, {Model} with the DS3 model, " +
                                 $"{Stats} on an Elden Ring body, {Failed} fell back; {Animations} DS3 animations, {NpcRows} NpcParam rows";
    }

    /// <summary>The marker of a "files failed" note (<see cref="Build"/> writes them, <see cref="FailureSummary"/> reads them).</summary>
    public const string FilesFailed = ": files failed (";

    /// <summary><see cref="Result.Problem"/>, from the counts and notes (also read back from the report by map-validate).</summary>
    public static string? FailureSummary(int failed, int built, IEnumerable<string> notes)
    {
        if (failed <= 0)
            return null;
        const string tail = "); using the template's body";
        var reasons = notes.Where(n => n.Contains(FilesFailed, StringComparison.Ordinal))
            .Select(n => n[(n.IndexOf(FilesFailed, StringComparison.Ordinal) + FilesFailed.Length)..])
            .Select(r => r.EndsWith(tail, StringComparison.Ordinal) ? r[..^tail.Length] : r)
            .GroupBy(r => r).OrderByDescending(g => g.Count()).FirstOrDefault();
        return $"{failed} of {failed + built} DS3 enemy models could not be built and are on Elden Ring bodies" +
               (reasons is null ? "" : $" ({reasons.Count()} with: {reasons.Key})");
    }

    /// <summary><see cref="FailureSummary"/> from the lines of Work\reports\enemy-port.tsv (what map-validate has to go on).</summary>
    public static string? ReportProblem(IEnumerable<string> reportLines)
    {
        int built = 0;
        var notes = new List<string>();
        foreach (var line in reportLines)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                if (line.Contains(FilesFailed, StringComparison.Ordinal))
                    notes.Add(line[2..]);
            }
            else if (line.Split('\t') is { Length: >= 8 } p && p[7] is "full" or "model")
                built++;
        }
        return FailureSummary(notes.Count, built, notes);
    }

    /// <summary>The choice for every DS3 character: its template, how it is ported, its new ID.</summary>
    public static List<Plan> MakePlans(GameArchives erArchives, GameArchives ds3Archives, IReadOnlyList<Ds3Chr> characters,
        IReadOnlyList<EnemyPort.ErEnemy> catalog, Method method, List<string> notes, Action<Ds3Chr>? skipped = null,
        Action<Ds3Chr, List<(EnemyPort.ErEnemy Template, double Match, double Size, double Score)>>? ranked = null)
    {
        var erShapes = new ConcurrentDictionary<int, Shape?>();
        Shape? ErShape(EnemyPort.ErEnemy e) => erShapes.GetOrAdd(ChrId(e.Model), c => ErShapeOf(erArchives, c));
        IReadOnlyList<string>? ErNames(EnemyPort.ErEnemy e) => ErShape(e)?.Names;
        // DS3's skeletons are in its animation binders (large): read side by side, once.
        var ds3Shapes = new ConcurrentDictionary<int, Shape?>();
        Parallel.ForEach(characters, Parallelism.Options, c => ds3Shapes[c.Chr] = Ds3ShapeOf(ds3Archives, c.Chr));
        var ids = AssignIds(characters.Select(c => c.Chr), n => erArchives.Contains($"/chr/c{n:D4}.chrbnd.dcx"));

        var plans = new List<Plan>();
        var fileChrs = new ConcurrentDictionary<int, int>();
        foreach (var ds3Chr in characters)
        {
            if (ds3Shapes.GetValueOrDefault(ds3Chr.Chr) is { } known)
                ranked?.Invoke(ds3Chr, RankTemplates(ds3Chr, known, catalog, ErShape));
            if (ds3Shapes.GetValueOrDefault(ds3Chr.Chr) is not { } shape || PickTemplate(ds3Chr, shape, catalog, ErShape) is not { } pick || !ids.TryGetValue(ds3Chr.Chr, out var newChr))
            {
                skipped?.Invoke(ds3Chr);
                continue;
            }
            var chosen = pick.Match < MinBoneMatch && method != Method.Stats ? Method.Stats : method;
            double boneMatch = pick.Match;
            // The template's own body will be what stands there: pick it by kind (PickBody), not by bone names.
            if (chosen == Method.Stats && PickBody(ds3Chr, shape.Names, catalog, ErNames) is { } body)
                pick = body;
            int templateChr = ChrId(pick.Template.Model);
            if (chosen != Method.Stats)
            {
                // A variant (c3061) keeps its animations and behaviour in its base character (c3060): the new
                // character is built from the base's files, which are complete.
                int files = fileChrs.GetOrAdd(templateChr, c => FileChr(c, chr => AnimationCount(erArchives, chr)));
                if (files < 0)
                {
                    notes.Add($"c{ds3Chr.Chr:D4} {ds3Chr.Name}: template {pick.Template.Model} and its base have no animations; using an Elden Ring body");
                    chosen = Method.Stats;
                    if (PickBody(ds3Chr, shape.Names, catalog, ErNames) is { } other)
                        pick = other;
                    templateChr = ChrId(pick.Template.Model);
                }
                else
                    templateChr = files;
            }
            plans.Add(new Plan(ds3Chr, pick.Template, templateChr, boneMatch, chosen, newChr));
        }
        return plans;
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
        var notes = result.Notes;
        var erNpc = ctx.LoadParams(PorterGame.EldenRing).Get("NpcParam") ?? throw new InvalidDataException("Elden Ring has no NpcParam");
        var catalog = EnemyPort.BuildCatalog(erArchives, erNpc, notes);
        if (method == Method.Off)
        {
            Console.WriteLine("enemies: off (Fallbacks enemy-port); DS3 maps use stand-ins");
            WriteSpawnList(ctx, [], catalog, new HashSet<string>(), new HashSet<string>());
            return result;
        }
        TAE.ValidateEventBank = false;
        var ds3Npc = ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcParam");
        var characters = Ds3Characters(ds3Archives, ds3Npc);
        if (DropPort.Enabled)
            result.ItemNames = DropPort.Names.Load(erArchives, ds3Archives, notes);
        Console.WriteLine($"enemies: {characters.Count} DS3 characters in DS3's maps, {catalog.Count} Elden Ring templates");

        var plans = MakePlans(erArchives, ds3Archives, characters, catalog, method, notes,
            skipped => result.Lines.Add(Line(skipped, null, "skipped", "no Elden Ring template or no free character ID")));

        // DS3's effects, sounds and abilities for every fully ported character (ChrEffects); null leaves the templates'.
        var fx = method == Method.Full ? ChrEffects.Create(ctx, erArchives, ds3Archives, plans, notes) : null;
        result.Effects = fx;

        // DS3's AI (AiPort): every fully ported character thinks with DS3's goal scripts; null keeps the templates'.
        AiPort? ai = null;
        PARAM? ds3Think = null, erThink = null;
        if (method == Method.Full && AiPort.Enabled)
            try
            {
                ds3Think = ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcThinkParam") ?? throw new InvalidDataException("DS3 has no NpcThinkParam");
                erThink = ctx.LoadParams(PorterGame.EldenRing).Get("NpcThinkParam") ?? throw new InvalidDataException("Elden Ring has no NpcThinkParam");
                ai = AiPort.Create(erArchives, ds3Archives, erThink);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"enemy AI: not ported ({Describe(ex)}); the templates' AI stays");
            }
        var usedThink = erThink?.Rows.Select(r => r.ID).ToHashSet() ?? [];

        var mapLines = new List<string> { "# kind\tDS3\tElden Ring model\tNpcParam\tThinkParam\tname (written by DarkSignPorter port-maps, enemy port)" };
        var written = new List<string>();
        var portedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Files are built side by side; the Havok serializer is used one thread at a time (AnimPort.HavokLock).
        var built = new ConcurrentDictionary<int, (ChrFiles? Files, string? Error)>();
        MaskedErrors.Install();
        Parallel.ForEach(plans.Where(p => p.Method != Method.Stats), Parallelism.Options, plan =>
        {
            try
            {
                built[plan.Ds3.Chr] = (BuildFiles(ctx, erArchives, ds3Archives, plan, fx), null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                built[plan.Ds3.Chr] = (null, Describe(ex));
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
                    notes.Add($"c{plan.Ds3.Chr:D4} {plan.Ds3.Name}{FilesFailed}{b.Error}); using the template's body");
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
                portedModels.Add($"c{plan.NewChr:D4}");
            }
            result.Params.Add((effective, npcRows));
            AiPort.ChrAi? chrAi = null;
            if (ai is not null && ds3Think is not null && files is not null && effective.Method == Method.Full)
                try
                {
                    chrAi = ai.Port(plan.Ds3.Chr, plan.NewChr, plan.Ds3.Name, ds3Think, usedThink.Contains);
                    if (chrAi is not null)
                    {
                        usedThink.UnionWith(chrAi.Rows.Select(r => r.ErThink));
                        result.Ai.Add((effective, chrAi));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    notes.Add($"c{plan.Ds3.Chr:D4} {plan.Ds3.Name}: DS3 AI not ported ({Describe(ex)}); the template's stays");
                }
            mapLines.AddRange(MapLines(effective, npcRows, chrAi?.Primary ?? effective.Template.ThinkParam));
            if (chrAi is not null)
                mapLines.AddRange(chrAi.Rows.Select(r => string.Join('\t', "think", r.Ds3Think, $"c{plan.NewChr:D4}", r.ErThink, plan.Ds3.Name.Replace('\t', ' '))));
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

        // The test set (RULE 4): one character built stage by stage, only in the F1 spawn list, never placed in a map.
        var testLines = new List<string>();
        foreach (var test in TestPlans(plans.Where(p => portedModels.Contains($"c{p.NewChr:D4}")).ToList(), c => erArchives.Contains($"/chr/c{c:D4}.chrbnd.dcx")))
        {
            string label = $"TEST {(int)test.Stage} {test.Ds3.Name}: {StageName(test.Stage)}";
            try
            {
                var files = BuildFiles(ctx, erArchives, ds3Archives, test);
                int row = test.NewChr * 10000;
                if (test.Ds3.NpcParams.Count == 0 || !usedRows.Add(row))
                    continue;
                foreach (var (path, bytes) in files.Outputs)
                {
                    var full = ctx.ModPath(ModParts(path));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, bytes);
                    written.Add(full);
                }
                result.Materials.AddRange(files.Materials);
                result.Params.Add((test, new Dictionary<int, int> { [test.Ds3.NpcParams.Min] = row }));
                testLines.Add(SpawnList.Line("DS3", $"c{test.NewChr:D4}", row, test.Template.ThinkParam, false, label, true));
                result.Lines.Add(Line(test.Ds3, test, $"test {(int)test.Stage}", $"{StageName(test.Stage)}; {files.Note}"));
                result.Tests++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"{label}: not built ({Describe(ex)})");
            }
        }
        if (result.Tests > 0)
            Console.WriteLine($"enemies: test set: {result.Tests} staged builds at the top of the F1 spawn list (TEST 1-5), for pinning a fault to one port step");

        if (ai is not null)
            try
            {
                foreach (var (path, bytes) in ai.Finish())
                {
                    var full = ctx.ModPath(ModParts(path));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, bytes);
                    written.Add(full);
                }
                File.WriteAllLines(ctx.WorkPath("reports", AiPort.ReportFile), ai.ReportLines());
                Console.WriteLine($"enemies: {ai.Summary} (report: Work\\reports\\{AiPort.ReportFile})");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Without the scripts the new think rows would name goals that don't exist: the templates' AI stays.
                notes.Add($"enemy AI: scripts not written ({Describe(ex)}); the templates' AI stays");
                result.Ai.Clear();
                mapLines.RemoveAll(l => l.StartsWith("think\t", StringComparison.Ordinal));
                mapLines = mapLines.Select(l => RestoreThink(l, plans)).ToList();
            }
        File.WriteAllLines(ctx.ModPath(EnemyPort.ChrMap.FileName), mapLines);
        WriteSpawnList(ctx, mapLines, catalog, plans.Where(p => p.Ds3.Boss).Select(p => $"c{p.Ds3.Chr:D4}").ToHashSet(), portedModels, testLines);
        if (fx is not null)
        {
            written.AddRange(fx.Written);
            try { fx.WriteReport(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"enemy effects report not written ({ex.Message})"); }
        }
        File.WriteAllLines(ctx.ModPath(ManifestFile), written.Select(w => Path.GetRelativePath(ctx.ModPath(), w)));
        WriteReport(ctx, result);
        Console.WriteLine($"enemies: {result.Summary} (report: Work\\reports\\{ReportFile})");
        if (result.Problem is { } problem)
            Console.WriteLine($"enemies: FAIL: {problem}");
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
        // With the effect port, DS3's SpEffects and projectiles are ported along with the attacks (ChrEffects).
        var fx = result.Effects;
        var attacks = fx?.Attacks(regulation, ds3, result.Notes) ?? new AttackParamPort(regulation, ds3, "BehaviorParam", "AtkParam_Npc");
        int rows = 0, failed = 0;
        // DS3's drops for every ported row (one port for all of them: rows that share DS3 lots share the new ones).
        var drops = DropPort.Create(ctx, regulation, ds3, result.ItemNames, result.Notes);
        TemplateEffects? templateFx = null;
        if (TemplateEffects.Enabled)
            try { templateFx = new TemplateEffects(regulation.Get("NpcParam")!, regulation.Get("SpEffectParam")); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result.Notes.Add($"template effects: kept ({Describe(ex)})"); }
        foreach (var (plan, npcRows) in result.Params)
        {
            try
            {
                rows += PortParams(regulation, ds3, attacks, plan, npcRows, result.Notes, drops, fx, templateFx);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                result.Notes.Add($"c{plan.Ds3.Chr:D4} {plan.Ds3.Name}: params failed ({Describe(ex)})");
            }
        }
        int thinkRows = 0;
        if (result.Ai.Count > 0 && regulation.Get("NpcThinkParam") is { } erThink)
        {
            var ds3Think = ds3.Get("NpcThinkParam");
            foreach (var (plan, ai) in result.Ai)
            {
                try { thinkRows += AiPort.ApplyParams(erThink, ds3Think, plan.Template.ThinkParam, ai, result.Notes); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { result.Notes.Add($"c{plan.Ds3.Chr:D4} {plan.Ds3.Name}: NpcThinkParam rows failed ({Describe(ex)})"); }
            }
            // A placed character whose NpcThinkParam row is missing has no AI at all: every row the maps name exists.
            thinkRows += EnsureNpcRows(erThink, result.Ai.SelectMany(a => a.Ai.Rows.Select(r => (a.Plan.Template.ThinkParam, r.ErThink))));
        }
        int fallback = EnsureNpcRows(regulation.Get("NpcParam")!,
            result.Params.SelectMany(p => p.NpcRows.Values.Select(row => (p.Plan.Template.NpcParam, row))));
        if (fallback > 0)
            result.Notes.Add($"{fallback} NpcParam rows added as plain copies of their template's (their params step failed)");
        string dropSummary = "";
        if (drops is not null)
        {
            dropSummary = "\n  " + drops.Finish(ctx);
            result.Notes.AddRange(drops.Notes);
        }
        if (templateFx is not null)
            try { File.WriteAllLines(ctx.WorkPath("reports", TemplateEffects.ReportFile), templateFx.ReportLines()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Notes.Add($"template effects report not written ({ex.Message})"); }
        regulation.Save(ctx.ModPath("regulation.bin"));
        string effects = "";
        if (fx is not null)
            try { effects = "; " + fx.Finish(result.Notes); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result.Notes.Add($"enemy effects: {Describe(ex)}"); }
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
        return $"enemies: {rows + fallback} NpcParam rows ({fallback} plain template copies), {thinkRows} NpcThinkParam rows (DS3 AI), {attacks.Summary}" +
               $"{(failed > 0 ? $", {failed} characters' params failed (first: {result.Notes.FirstOrDefault(n => n.Contains("params failed")) ?? ""})" : "")}" +
               $"{(result.Notes.FirstOrDefault(n => n.Contains("attacks not ported")) is { } a ? $"; {a}" : "")}" + effects + dropSummary;
    }

    /// <summary>The F1 menu's spawn list; a failure only costs the menu its list (it falls back to a built-in one).</summary>
    private static void WriteSpawnList(PorterContext ctx, IEnumerable<string> mapLines, IEnumerable<EnemyPort.ErEnemy> catalog, ISet<string> ds3Bosses,
        ISet<string> portedModels, IEnumerable<string>? testLines = null)
    {
        try
        {
            SpawnList.Write(ctx, mapLines, catalog, ds3Bosses, portedModels, testLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"enemies: F1 spawn list not written ({ex.Message})");
        }
    }

    /// <summary>
    /// port-enemies: the enemy port on its own (port-maps runs it too), then a read-back of what the game will look up:
    /// every NpcParam row darksign_chr_map.tsv names must be in the mod's regulation.bin, and every new model must
    /// have its chrbnd, anibnd and behbnd with a TAE and animations. Run it with --root on a scratch folder to try a
    /// porter build without touching the real mod. Returns 1 when a check fails.
    /// </summary>
    public static int Run(PorterContext ctx)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var result = Build(ctx, er, ds3);
        Console.WriteLine(ApplyParams(ctx, er, result));
        var check = CheckOutputs(ctx, model => er.Contains($"/chr/{model}.chrbnd.dcx"));
        foreach (var line in check)
            Console.WriteLine(line);
        foreach (var note in result.Notes.Where(n => n.Contains("failed") || n.Contains("not ported")).Take(15))
            Console.WriteLine($"enemies: note: {note}");
        return check.Any(l => l.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0;
    }

    /// <summary>The read-back of port-enemies (and of validate tools): missing rows and incomplete models are FAIL lines.</summary>
    /// <param name="gameHas">Whether Elden Ring itself has a model (those are bodies the port borrows, not models it made).</param>
    public static List<string> CheckOutputs(PorterContext ctx, Func<string, bool> gameHas)
    {
        var lines = new List<string>();
        var mapPath = ctx.ModPath(EnemyPort.ChrMap.FileName);
        if (!File.Exists(mapPath))
            return ["check: no darksign_chr_map.tsv (enemy port off or failed)"];
        var rows = File.ReadLines(mapPath).Where(l => !l.StartsWith('#')).Select(l => l.Split('\t')).Where(p => p.Length >= 5).ToList();
        var npc = ActionButtons.LoadRegulation(ctx).Get("NpcParam")!;
        var missing = rows.Where(p => int.TryParse(p[3], out var id) && npc[id] is null).Select(p => $"{p[3]} ({p[^1]})").Distinct().ToList();
        lines.Add(missing.Count == 0
            ? $"check: ok   all {rows.Select(p => p[3]).Distinct().Count()} NpcParam rows the maps and spawn list use are in regulation.bin"
            : $"FAIL check: {missing.Count} NpcParam rows missing from regulation.bin (the game crashes loading them): {string.Join(", ", missing.Take(8))}");
        int bad = 0;
        foreach (var model in rows.Select(p => p[2]).Where(m => ChrId(m) is >= FirstNewChr and <= LastNewChr && !gameHas(m)).Distinct())
        {
            var problems = new List<string>();
            foreach (var kind in new[] { "chrbnd", "anibnd", "behbnd" })
                if (!File.Exists(ctx.ModPath("chr", $"{model}.{kind}.dcx")))
                    problems.Add($"no {kind}");
            var anibnd = ctx.ModPath("chr", $"{model}.anibnd.dcx");
            if (File.Exists(anibnd))
            {
                var files = Directory.GetFiles(Path.GetDirectoryName(anibnd)!, $"{model}*.anibnd.dcx")
                    .SelectMany(f => BND4.Read(DCX.Decompress(File.ReadAllBytes(f))).Files).ToList();
                if (!files.Any(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
                    problems.Add("no TAE");
                if (!files.Any(f => AnimLeaf().IsMatch(ItemText.Leaf(f.Name))))
                    problems.Add("no animations");
            }
            var behbnd = ctx.ModPath("chr", $"{model}.behbnd.dcx");
            if (File.Exists(behbnd))
            {
                // The game resolves the project's Characters\cXXXX.hkx and Behaviors\cXXXX.hkx by name: a template's ID
                // left inside means a crash at spawn.
                var foreign = BND4.Read(DCX.Decompress(File.ReadAllBytes(behbnd))).Files
                    .Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(f => Regex.Matches(System.Text.Encoding.Latin1.GetString(f.Bytes), @"(?:Characters|Behaviors)\\(c\d{4})\.hkx", RegexOptions.IgnoreCase)
                        .Select(m => m.Groups[1].Value.ToLowerInvariant()))
                    .Where(id => id != model.ToLowerInvariant()).Distinct().ToList();
                if (foreign.Count > 0)
                    problems.Add($"behbnd still names {string.Join(", ", foreign)} inside");
            }
            if (problems.Count > 0 && bad++ < 8)
                lines.Add($"FAIL check: {model}: {string.Join(", ", problems)}");
        }
        if (bad == 0)
            lines.Add("check: ok   every new model has its chrbnd, anibnd (TAE and animations) and behbnd, named its own inside");
        else if (bad > 8)
            lines.Add($"FAIL check: {bad} new models incomplete in all");
        return lines;
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
        foreach (var file in new[] { manifest, ctx.ModPath(EnemyPort.ChrMap.FileName), ctx.ModPath(SpawnList.FileName) })
            if (File.Exists(file))
                File.Delete(file);
    }

    private static string[] ModParts(string archivePath) => archivePath.TrimStart('/').Split('/');

    // ---- DS3 side ----

    /// <summary>Every enemy character (not c0000 humans, not talkers unless they are a boss) placed in DS3's single-player maps.</summary>
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
            var bosses = EnemyPort.BossEncounter(msb, EnemyPort.BossEntities(scripts, ds3Layout: true));
            foreach (var e in msb.Parts.Enemies)
            {
                int chr = ChrId(e.ModelName);
                // Talkers are people, except bosses who speak (Friede, Ariandel): see EnemyPort.BossEncounter.
                if (chr <= 0 || e.TalkID > 0 && !bosses.Contains(e.EntityID) || e.NPCParamID <= 0)
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
            var file = bnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes));
            if (file is null)
                return null;
            var flver = FLVER2.Read(file.Bytes);
            // A model with nothing to draw (an invisible dummy, or c0000, whose body comes from equipment) can't carry or
            // be replaced by another model: treated as having no model.
            if (!HasBody(flver))
                return null;
            return flver.Nodes.Select(n => n.Name ?? "").ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// An Elden Ring character's shape: the animation skeleton of the character whose files it plays with (itself, or
    /// its base: c3061 -> c3060); its model's bones when it has no skeleton file. Null when it has no model to wear.
    /// </summary>
    public static Shape? ErShapeOf(GameArchives er, int chr)
    {
        if (FlverBones(er, chr) is not { } model)
            return null;
        foreach (var candidate in new[] { chr, chr / 10 * 10, chr / 100 * 100 }.Distinct())
        {
            var path = $"/chr/c{candidate:D4}.anibnd.dcx";
            if (candidate <= 0 || !er.Contains(path))
                continue;
            try
            {
                var bnd = BND4.Read(er.Read(path));
                if (bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase)) is not { } file)
                    continue;
                lock (AnimPort.HavokLock)
                {
                    var s = AnimPort.ReadErSkeleton(file.Bytes, Compendium(bnd));
                    var (size, head) = RestSize(s.Names, s.Parents, s.ReferencePose.Select(t =>
                        (new Vector3(t.m_translation.X, t.m_translation.Y, t.m_translation.Z), t.m_rotation)).ToList());
                    return new Shape(s.Names, s.Parents, size, head);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
        return new Shape(model, []);
    }

    /// <summary>A DS3 character's shape: its animation skeleton (its own or its base's); its model's bones without one; null without a model.</summary>
    public static Shape? Ds3ShapeOf(GameArchives ds3, int chr)
    {
        if (FlverBones(ds3, chr) is not { } model)
            return null;
        try
        {
            foreach (int source in Ds3AnimationSources(chr))
                foreach (var entry in ds3.Entries.Where(e => e.Path is { } p && Regex.IsMatch(ItemText.Leaf(p), $@"^c{source:D4}(_.*)?\.anibnd\.dcx$", RegexOptions.IgnoreCase))
                             .OrderBy(e => e.Path, StringComparer.Ordinal))
                    if (BND4.Read(ds3.Read(entry.Path!)).Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase)) is { } file)
                    {
                        var s = AnimPort.ReadDs3Skeleton(file.Bytes);
                        var (size, head) = RestSize(s.Names, s.Parents, s.ReferencePose.Select(t => (t.Translation, t.Rotation)).ToList());
                        return new Shape(s.Names, s.Parents, size, head);
                    }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
        return new Shape(model, []);
    }

    /// <summary>A skeleton's size in its rest pose (see <see cref="Shape"/>): the box diagonal and the head height.</summary>
    public static (float Size, float Head) RestSize(IReadOnlyList<string> names, IReadOnlyList<short> parents, IReadOnlyList<(Vector3 T, Quaternion R)> local)
    {
        if (local.Count == 0)
            return (0, 0);
        var pos = new Vector3[local.Count];
        var rot = new Quaternion[local.Count];
        for (int i = 0; i < local.Count; i++)
        {
            int p = i < parents.Count ? parents[i] : -1;
            bool top = p < 0 || p >= i;
            pos[i] = top ? local[i].T : pos[p] + Vector3.Transform(local[i].T, rot[p]);
            rot[i] = top ? local[i].R : rot[p] * local[i].R;
        }
        float Spread(Func<Vector3, float> axis)
        {
            var v = pos.Select(axis).OrderBy(x => x).ToArray();
            return v[(int)(0.9 * (v.Length - 1))] - v[(int)(0.1 * (v.Length - 1))];
        }
        int head = Enumerable.Range(0, Math.Min(names.Count, pos.Length)).FirstOrDefault(i => names[i].Equals("Head", StringComparison.OrdinalIgnoreCase), -1);
        return (new Vector3(Spread(v => v.X), Spread(v => v.Y), Spread(v => v.Z)).Length(), head >= 0 ? Math.Max(0, pos[head].Y) : 0);
    }

    /// <summary>
    /// The character whose files a new character is built from: the template itself when its anibnd has a TAE and
    /// animations, else its base (c3061 -> c3060), else -1. <paramref name="animations"/> counts a character's.
    /// </summary>
    public static int FileChr(int chr, Func<int, int> animations)
    {
        foreach (var candidate in new[] { chr, chr / 10 * 10, chr / 100 * 100 }.Distinct())
            if (candidate > 0 && animations(candidate) > 0)
                return candidate;
        return -1;
    }

    /// <summary>Animations (hkx) in a character's anibnds when its main anibnd has a TAE; 0 otherwise.</summary>
    public static int AnimationCount(GameArchives er, int chr)
    {
        var main = $"/chr/c{chr:D4}.anibnd.dcx";
        if (!er.Contains(main))
            return 0;
        try
        {
            if (!BND4.Read(er.Read(main)).Files.Any(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
                return 0;
            return er.Entries.Where(e => e.Path is { } p && p.StartsWith($"/chr/c{chr:D4}", StringComparison.OrdinalIgnoreCase)
                                         && p.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)
                                         && Regex.IsMatch(ItemText.Leaf(p), $@"^c{chr:D4}(_div\d+)?\.anibnd\.dcx$", RegexOptions.IgnoreCase))
                .Sum(e => BND4.Read(er.Read(e.Path!)).Files.Count(f => AnimLeaf().IsMatch(ItemText.Leaf(f.Name))));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>False with DARKSIGN_ENEMY_BONES=template: the skeleton keeps only the template's bones, as builds up to 1.0.263 did.</summary>
    public static bool AddDs3Bones => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_BONES")?.Trim().ToLowerInvariant() != "template";

    public readonly record struct SkeletonMerge(int Shared, int Added, int Replaced, int Reparented = 0);

    /// <summary>
    /// Makes the template's animation skeleton carry DS3's whole skeleton. A bone both have, under the same parent,
    /// stays where it is in the template's list (the template's other animations, ragdoll and behaviour name bones by
    /// number) and takes DS3's rest position. Every other DS3 bone (weapons, skirts, hats, capes, a boss's second
    /// body) is added at the end under its DS3 parent: until 1.0.263 those had no bone to follow, so the meshes
    /// skinned to them stood still or stretched while the rest of the body moved, and the dummy points on them
    /// (weapon hit boxes) never left their rest place. A bone both have under different parents is moved under its
    /// DS3 parent where it is (it keeps its number, so nothing after it changes); only when that parent comes later in
    /// the list (parents must come first) the template's is renamed (name + "_er") and DS3's is added like a new one.
    /// Until 1.0.271 every such bone was set aside, and so was everything below it: one different parent near the
    /// root (the Lothric Knight on the beast skeleton) left 7 bones shared and 79 set aside.
    /// </summary>
    public static SkeletonMerge MergeSkeleton(hkaSkeleton skeleton, AnimPort.Ds3Skeleton ds3) => MergeSkeleton(skeleton, ds3, AddDs3Bones);

    public static SkeletonMerge MergeSkeleton(hkaSkeleton skeleton, AnimPort.Ds3Skeleton ds3, bool addBones)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < skeleton.m_bones.Count; b++)
            index.TryAdd(skeleton.m_bones[b].m_name ?? "", b);
        hkQsTransform Pose(int d) => new()
        {
            m_translation = new Vector4(ds3.ReferencePose[d].Translation, 0),
            m_rotation = ds3.ReferencePose[d].Rotation,
            m_scale = new Vector4(ds3.ReferencePose[d].Scale, 0),
        };
        bool hierarchy = addBones && ds3.Parents.Count == ds3.Names.Count;
        var target = new int[ds3.Names.Count];
        int shared = 0, added = 0, replaced = 0, reparented = 0;
        for (int d = 0; d < ds3.Names.Count && d < ds3.ReferencePose.Count; d++)
        {
            target[d] = -1;
            bool has = index.TryGetValue(ds3.Names[d], out var b) && b < skeleton.m_referencePose.Count;
            if (!hierarchy)
            {
                if (!has)
                    continue;
                skeleton.m_referencePose[b] = Pose(d);
                shared++;
                continue;
            }
            int ds3Parent = ds3.Parents[d];
            int parent = ds3Parent >= 0 && ds3Parent < d ? target[ds3Parent] : -1;
            if (ds3Parent >= 0 && parent < 0)
                continue; // its parent could not be placed (a malformed DS3 skeleton): left out
            if (has && skeleton.m_parentIndices[b] == parent)
            {
                skeleton.m_referencePose[b] = Pose(d);
                target[d] = b;
                shared++;
                continue;
            }
            if (has && parent < b)
            {
                skeleton.m_parentIndices[b] = (short)parent;
                skeleton.m_referencePose[b] = Pose(d);
                target[d] = b;
                reparented++;
                continue;
            }
            if (skeleton.m_bones.Count >= short.MaxValue)
                continue;
            if (has)
            {
                skeleton.m_bones[b].m_name += "_er";
                index.Remove(ds3.Names[d]);
                replaced++;
            }
            skeleton.m_bones.Add(new hkaBone { m_name = ds3.Names[d], m_lockTranslation = false });
            skeleton.m_parentIndices.Add((short)parent);
            skeleton.m_referencePose.Add(Pose(d));
            target[d] = skeleton.m_bones.Count - 1;
            index[ds3.Names[d]] = target[d];
            added++;
        }
        return new SkeletonMerge(shared, added, replaced, reparented);
    }

    /// <summary>False with DARKSIGN_ENEMY_ANIMS=template: a template move DS3 has no move of the same ID for keeps the template's animation, as builds up to 1.0.271 did.</summary>
    public static bool FillMoves => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_ANIMS")?.Trim().ToLowerInvariant() != "template";

    /// <summary>The blocks of a thousand animation IDs looked in for a stand-in, in order: the move's own, then the one that plays alike.</summary>
    private static int[] MoveBands(int band) => band switch
    {
        1 => [1, 0],      // idle variants -> idle
        5 => [5, 6],      // steps <-> dodges
        6 => [6, 5],
        10 => [10, 11],   // deaths
        11 => [11, 10],
        _ => [band],
    };

    /// <summary>
    /// The DS3 move that plays where the template has move <paramref name="want"/> (section, ID): DS3's move of the same
    /// ID (in that section, else the base section); else, when <paramref name="fill"/>, the nearest DS3 move of the
    /// same block of a thousand (both games number enemy moves alike: 2xxx walking, 3xxx attacks, 5xxx/6xxx steps,
    /// 8xxx/9xxx being hit, 10xxx/11xxx dying), preferring the same last digit (the direction: 8031 -> 8021); else
    /// DS3's idle. A template animation left in place plays the template's bone lengths on the DS3 body: the Jailer
    /// backstepping as an Imp, with its legs pulled to an Imp's size (1.0.271). Null when DS3 has no moves.
    /// </summary>
    public static (int Section, int Id)? ChooseMove((int Section, int Id) want, IReadOnlySet<(int Section, int Id)> have, bool fill = true)
    {
        if (have.Contains(want))
            return want;
        if (have.Contains((0, want.Id)))
            return (0, want.Id);
        if (!fill)
            return null;
        var ids = have.Where(k => k.Section == 0).Select(k => k.Id).ToList();
        if (ids.Count == 0)
            return null;
        foreach (int band in MoveBands(want.Id / 1000))
        {
            int near = band * 1000 + want.Id % 1000;
            var inBand = ids.Where(x => x / 1000 == band).ToList();
            if (inBand.Count > 0)
                return (0, inBand.OrderBy(x => x % 10 == want.Id % 10 ? 0 : 1).ThenBy(x => Math.Abs(x - near)).ThenBy(x => x).First());
        }
        return (0, ids.Contains(0) ? 0 : ids.Min());
    }

    /// <summary>A DS3 move: its TAE events and the animation file it plays (its own, or the one its TAE entry borrows).</summary>
    public sealed record Ds3Move(List<TAE.Event> Events, (int Section, int Id) Hkx);

    /// <summary>
    /// DS3's moves by (section, ID): every TAE entry whose animation file exists (following "plays another entry" and
    /// "borrows another entry's animation": the Jailer's idle, entry 0, plays file 20), and every animation file
    /// without an entry (no events).
    /// </summary>
    public static Dictionary<(int Section, int Id), Ds3Move> Ds3Moves(IEnumerable<(int Section, TAE Tae)> taes, IReadOnlySet<(int Section, int Id)> hkx)
    {
        var moves = new Dictionary<(int, int), Ds3Move>();
        foreach (var (section, tae) in taes)
        {
            var byId = tae.Animations.GroupBy(a => a.ID).ToDictionary(g => g.Key, g => g.First());
            foreach (var anim in tae.Animations)
            {
                var source = anim;
                for (int hop = 0; hop < 4 && source.MiniHeader is TAE.Animation.AnimMiniHeader.ImportOtherAnim imp
                                          && byId.TryGetValue(imp.ImportFromAnimID, out var other); hop++)
                    source = other;
                var file = source.MiniHeader is TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: true } s
                    ? AnimKey(s.ImportHKXSourceAnimID, section)
                    : AnimKey(source.ID, section);
                if (hkx.Contains(file))
                    moves.TryAdd(AnimKey(anim.ID, section), new Ds3Move(source.Events, file));
            }
        }
        foreach (var file in hkx)
            moves.TryAdd(file, new Ds3Move([], file));
        return moves;
    }

    /// <summary>The animation file an Elden Ring TAE entry plays: its own, or the one it borrows; null when it plays another entry whole.</summary>
    public static (int Section, int Id)? ErHkx(TAE.Animation anim, int section) => anim.MiniHeader switch
    {
        TAE.Animation.AnimMiniHeader.ImportOtherAnim => null,
        TAE.Animation.AnimMiniHeader.Standard { ImportsHKX: true } s => AnimKey(s.ImportHKXSourceAnimID, section),
        _ => AnimKey(anim.ID, section),
    };

    /// <summary>
    /// DS3's ModelDispMask0-31 onto Elden Ring's modelDispMask0-31 (same meaning: mask N shows the meshes whose
    /// material name starts with "#NN#"). Returns how many were copied.
    /// </summary>
    public static int CopyDisplayMasks(PARAM.Row ds3Row, PARAM.Row erRow)
    {
        int copied = 0;
        for (int k = 0; k < 32; k++)
            if (Cell(ds3Row, $"ModelDispMask{k}") is { } from && Cell(erRow, $"modelDispMask{k}") is { } to)
            {
                Set(to, from.Value);
                copied++;
            }
        return copied;
    }

    /// <summary>The DS3 characters whose animations a DS3 character can play: itself, then its base (c2191 -> c2190).</summary>
    public static int[] Ds3AnimationSources(int chr) => new[] { chr, chr / 10 * 10 }.Distinct().ToArray();

    /// <summary>Whether a model draws anything: meshes with faces, materials and a GX list to give them.</summary>
    public static bool HasBody(FLVER2 flver) =>
        flver.Materials.Count > 0 && flver.GXLists.Count > 0 && flver.Nodes.Count > 0
        && flver.Meshes.Any(m => m.Vertices.Count > 0 && m.FaceSets.Any(f => f.Indices.Count > 0));

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


    /// <summary>
    /// Where a template's files live: its character binders, its effects and its behaviour script
    /// (action\script\cXXXX.hks). The script is cloned too: the behaviour graph's script loader is handed a null
    /// buffer when action\script has no file for the character's ID, and Havok Script asserts on it, so every spawn of
    /// a ported character whose template has a script crashed right after its behbnd loaded (2026-11-08 evening,
    /// RVA 0x14e34d1, hksi_hksL_loadbuffer "Parameter 'buff' must not be NULL").
    /// </summary>
    private static readonly string[] ClonedFolders = ["/chr/", "/sfx/", "/action/script/"];

    internal static ChrFiles BuildFiles(PorterContext ctx, GameArchives er, GameArchives ds3, Plan plan, ChrEffects? fx = null)
    {
        int t = plan.TemplateChr, n = plan.NewChr, x = plan.Ds3.Chr;
        var files = new ChrFiles();
        var notes = new List<string>();
        // The template's character and effect files, under the new ID. Its sound bank isn't copied: the new NpcParam
        // rows load the template's own bank (SoundAddBankId), so its sound events keep playing.
        var binders = new Dictionary<string, (BND4 Bnd, DCX.CompressionInfo Compression)>(StringComparer.OrdinalIgnoreCase);
        int renamedInside = 0, fromVariant = 0;
        // The files come from the character that holds the template's animations (a variant such as c4353 keeps them
        // in its base, c4350), then, for every file the base lacks, from the variant itself: a base can be nothing
        // but animations and behaviour (c4350 has no chrbnd), and with the base alone every enemy on such a template
        // failed to build and stood there as the Elden Ring body (the Lothric Knight on the Leyndell Knight, 1.0.284).
        int variant = ChrId(plan.Template.Model);
        foreach (int from in new[] { t, variant }.Where(c => c > 0).Distinct())
            foreach (var entry in er.Entries.Where(e => e.Path is { } p && ClonedFolders.Any(f => p.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                                                         && Regex.IsMatch(ItemText.Leaf(p), $@"(^|_)c{from:D4}(\.|_)", RegexOptions.IgnoreCase))
                         .OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                var path = Rename(entry.Path!, from, n);
                if (binders.ContainsKey(path) || files.Outputs.Any(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var (bytes, compression) = er.ReadWithCompression(entry.Path!);
                if (BND4.Is(bytes))
                {
                    var bnd = BND4.Read(bytes);
                    foreach (var f in bnd.Files)
                    {
                        f.Name = Rename(f.Name, from, n);
                        if (f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
                            renamedInside += RenameInside(f.Bytes, from, n);
                    }
                    binders[path] = (bnd, compression);
                }
                else
                    files.Outputs.Add((path, compression.Type == DCX.Type.None ? bytes : DCX.Compress(bytes, compression)));
                if (from != t)
                    fromVariant++;
            }
        var chrbndPath = $"/chr/c{n:D4}.chrbnd.dcx";
        if (!binders.ContainsKey(chrbndPath))
            throw new InvalidDataException($"template c{t:D4} has no chrbnd");
        if (fromVariant > 0)
            notes.Add($"{fromVariant} files from the template's own model c{variant:D4} (its base c{t:D4} lacks them)");
        notes.Add(binders.ContainsKey($"/chr/c{n:D4}.behbnd.dcx") ? $"{renamedInside} Havok names renamed" : $"template c{t:D4} has no behbnd; {renamedInside} Havok names renamed");
        notes.Add(files.Outputs.Any(o => o.Path.Equals($"/action/script/c{n:D4}.hks", StringComparison.OrdinalIgnoreCase)) ? "behaviour script cloned" : $"template c{t:D4} has no behaviour script");

        if (plan.Stage == Stage.Clone)
        {
            foreach (var (path, (bnd, compression)) in binders.OrderBy(b => b.Key, StringComparer.Ordinal))
                files.Outputs.Add((path, DCX.Compress(bnd.Write(), compression)));
            files.Note = string.Join("; ", notes);
            return files;
        }

        // The DS3 model, on the template's binder.
        var ds3Chrbnd = BND4.Read(ds3.Read($"/chr/c{x:D4}.chrbnd.dcx"));
        var ds3FlverFile = ds3Chrbnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes)) ?? throw new InvalidDataException($"DS3 c{x:D4} has no model");
        var chrbnd = binders[chrbndPath].Bnd;
        var templateFlverFile = chrbnd.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes)) ?? throw new InvalidDataException($"template c{t:D4} has no model");
        var templateFlver = FLVER2.Read(templateFlverFile.Bytes);
        var flver = FLVER2.Read(ds3FlverFile.Bytes);
        var textureNames = ChrModel.TextureNames(flver);
        var textureOwners = ChrModel.TextureOwners(flver, x);
        int glowing = flver.Materials.Count(m => m.Textures.Any(tx => tx.Path.Length > 0 && tx.ParamName.Contains("Emissive", StringComparison.OrdinalIgnoreCase)
                                                                    && !ItemText.Leaf(tx.Path).StartsWith("SYSTEX", StringComparison.OrdinalIgnoreCase)));

        // DS3's textures, renamed to the new ID, in the new character's texture binders.
        bool ownFile = ChrModel.OwnTextureFile;
        var ds3Textures = ds3.Entries.Where(e => e.Path is { } p && p.StartsWith($"/chr/c{x:D4}", StringComparison.OrdinalIgnoreCase)
                                                 && p.EndsWith(".texbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).ToList();
        var tpfs = new List<TPF>();
        var notDds = new List<string>();
        // Textures the model names under another character's ID (the Undead Dog c2020 wears c2029's, c6080 some of
        // c6089's): that character's binders, only the textures named. Without them the mesh draws nothing.
        foreach (int owner in textureOwners)
            foreach (var path in ds3.Entries.Where(e => e.Path is { } p && p.StartsWith($"/chr/c{owner:D4}", StringComparison.OrdinalIgnoreCase)
                                                         && p.EndsWith(".texbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).ToList())
                foreach (var f in BND4.Read(ds3.Read(path)).Files.Where(f => TPF.Is(f.Bytes)))
                {
                    var tpf = TPF.Read(f.Bytes);
                    tpf.Textures.RemoveAll(tx => !textureNames.Contains(tx.Name));
                    if (tpf.Textures.Count > 0)
                        tpfs.Add(ChrModel.ConvertTpf(tpf, x, n, !ownFile, notDds));
                }
        foreach (var path in ds3Textures)
            foreach (var f in BND4.Read(ds3.Read(path)).Files.Where(f => TPF.Is(f.Bytes)))
                tpfs.Add(ChrModel.ConvertTpf(TPF.Read(f.Bytes), x, n, !ownFile, notDds));
        foreach (var f in ds3Chrbnd.Files.Where(f => TPF.Is(f.Bytes)))
            tpfs.Add(ChrModel.ConvertTpf(TPF.Read(f.Bytes), x, n, !ownFile, notDds));
        if (notDds.Count > 0)
            notes.Add($"FAIL textures: {notDds.Count} DS3 textures are not DDS files and are left out: {string.Join("; ", notDds.Take(6))}{(notDds.Count > 6 ? "; ..." : "")}");
        // Materials whose colour texture the port does not find (another sampler name, or a path left to the DS3
        // material file): they draw blank. Named here so one build report tells which enemies and why.
        var blank = flver.Materials.Where(m => !m.Textures.Any(tx => tx.Path.Length > 0
                && (tx.ParamName.EndsWith("g_DiffuseTexture", StringComparison.Ordinal) || tx.ParamName.EndsWith("_AlbedoMap_0", StringComparison.Ordinal))))
            .Select(m => $"{Path.GetFileNameWithoutExtension(ItemText.Leaf(m.MTD))} [{string.Join(",", m.Textures.Select(tx => tx.ParamName + (tx.Path.Length > 0 ? "" : "=empty")))}]")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var areas = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var tx in tpfs.SelectMany(p => p.Textures).Where(tx => tx.Bytes.Length >= 20))
            areas.TryAdd(tx.Name, (long)BitConverter.ToInt32(tx.Bytes, 16) * BitConverter.ToInt32(tx.Bytes, 12));
        var made = ChrModel.Convert(flver, templateFlver, x, n, name => areas.GetValueOrDefault(name), glowing > 0 ? ChrGlow.Find(er) : null);
        if (ChrModel.VertexColoursSet > 0 || ChrModel.SecondLayers > 0)
            notes.Add($"{ChrModel.VertexColoursSet} vertex colours set to Elden Ring's" + (ChrModel.SecondLayers > 0 ? $", {ChrModel.SecondLayers} two-layer materials show their larger layer" : ""));
        if (blank.Count > 0)
            notes.Add($"FAIL textures: {blank.Count} kinds of DS3 material name no colour texture the port reads ({string.Join("; ", blank.Take(6))}{(blank.Count > 6 ? "; ..." : "")})");
        files.Materials.AddRange(made);
        notes.Add($"{flver.Meshes.Count} meshes, {flver.Materials.Count} DS3 materials on {made.Count} {(ChrModel.PlainMaterials ? "plain character" : "template")} materials"
                  + $" ({made.Count(m => m.AlphaTest)} cut-out, {flver.Materials.Count(m => ChrModel.MaskPrefix(m.Name).Length > 0)} with a display mask"
                  + (glowing > 0 ? ChrModel.Glowing > 0 ? $", {ChrModel.Glowing} glowing ones on Elden Ring's glowing material" + (glowing > ChrModel.Glowing ? $" ({glowing - ChrModel.Glowing} without their glow)" : "")
                      : $", {glowing} glowing ones drawn without their glow" : "") + ")"
                  + $"; node flags {string.Join(" ", flver.Nodes.GroupBy(node => (int)node.Flags).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}");
        templateFlverFile.Bytes = flver.Write();
        var texbnd = binders.Keys.Where(k => k.EndsWith(".texbnd.dcx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k.Contains("_h", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        (int W, int H) templateLargest = (0, 0);
        if (texbnd is not null)
            foreach (var f in binders[texbnd].Bnd.Files.Where(f => TPF.Is(f.Bytes)))
                foreach (var tx in TPF.Read(f.Bytes).Textures.Where(tx => tx.Bytes.Length >= 20))
                    if ((long)BitConverter.ToInt32(tx.Bytes, 16) * BitConverter.ToInt32(tx.Bytes, 12) > (long)templateLargest.W * templateLargest.H)
                        templateLargest = (BitConverter.ToInt32(tx.Bytes, 16), BitConverter.ToInt32(tx.Bytes, 12));
        if (tpfs.Count > 0)
        {
            var merged = new TPF { Platform = TPF.TPFPlatform.PC, Encoding = 1, Flag2 = 3 };
            merged.Textures.AddRange(tpfs.SelectMany(p => p.Textures).DistinctBy(tx => tx.Name, StringComparer.OrdinalIgnoreCase));
            var target = texbnd is not null ? binders[texbnd].Bnd : chrbnd;
            var like = target.Files.FirstOrDefault(f => TPF.Is(f.Bytes)) ?? target.Files[0];
            var folder = like.Name[..^ItemText.Leaf(like.Name).Length];
            string where;
            if (ownFile && texbnd is not null && TPF.Is(like.Bytes))
            {
                // Elden Ring's character texture binders hold one file each (cXXXX_h.tpf, and cXXXX_l.tpf with the
                // same textures two sizes down under name + "_l"). Up to 1.0.271 DS3's textures were a second file in
                // the high-quality binder, which the game never read: every ported enemy drew blank (silver). They
                // now are that one file (the template's textures are of no use to a DS3 model), in both binders,
                // with metal read from DS3's reflectance maps the way the weapon port does it.
                WeaponTextures.AddMetallicMaps(merged, WeaponTextures.CurrentMode);
                like.Bytes = merged.Write();
                target.Files.RemoveAll(f => f != like && TPF.Is(f.Bytes));
                where = "in the character's own texture file";
                var lowKey = binders.Keys.FirstOrDefault(k => k.EndsWith("_l.texbnd.dcx", StringComparison.OrdinalIgnoreCase));
                if (lowKey is not null && binders[lowKey].Bnd.Files.FirstOrDefault(f => TPF.Is(f.Bytes)) is { } lowFile)
                {
                    lowFile.Bytes = ChrModel.LowTpf(merged).Write();
                    binders[lowKey].Bnd.Files.RemoveAll(f => f != lowFile && TPF.Is(f.Bytes));
                    where += " and its low-quality copy";
                }
            }
            else
            {
                target.Files.Add(new BinderFile(like.Flags, target.Files.Max(f => f.ID) + 1, $"{folder}c{n:D4}_ds3.tpf", merged.Write()));
                where = texbnd is not null ? "in a second file of the template's texture binder" : "in the character binder (the template has no texture binder)";
            }
            // Sizes, so a "low resolution" report can be told from small DS3 textures: the largest texture in the
            // file the game reads at full quality, the file's size, and the template's own largest for comparison.
            static (int W, int H) Size(byte[] dds) => dds.Length >= 20 ? (BitConverter.ToInt32(dds, 16), BitConverter.ToInt32(dds, 12)) : (0, 0);
            var largest = merged.Textures.Select(tx => Size(tx.Bytes)).MaxBy(sz => (long)sz.W * sz.H);
            notes.Add($"largest DS3 texture {largest.W}x{largest.H}, {merged.Textures.Sum(tx => (long)tx.Bytes.Length) / 1048576} MB in all"
                      + (templateLargest.W > 0 ? $" (the template's largest was {templateLargest.W}x{templateLargest.H})" : ""));
            notes.Add($"{merged.Textures.Count} DS3 textures {where}" + (textureOwners.Count > 0 ? $" (some from {string.Join(", ", textureOwners.Select(o => $"c{o:D4}"))})" : ""));
        }
        var have = tpfs.SelectMany(p => p.Textures).Select(tx => tx.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        have.UnionWith(have.Where(name => name.EndsWith("_r", StringComparison.OrdinalIgnoreCase)).Select(name => name[..^2] + "_m").ToList());
        var lacking = made.SelectMany(m => new[] { m.Albedo, m.Normal }).Where(t => t is not null && !have.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (lacking.Count > 0)
            notes.Add($"FAIL textures: {lacking.Count} named by the model are in no DS3 binder ({string.Join(", ", lacking.Take(4))}{(lacking.Count > 4 ? ", ..." : "")}); those meshes draw wrong or not at all");

        if (plan.Method == Method.Full && plan.Stage > Stage.Model)
        {
            var animated = PortAnimations(ds3, x, binders, n, notes, t, plan.Stage, plan.Stage == Stage.All ? fx : null, plan);
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
    private static int PortAnimations(GameArchives ds3, int x, Dictionary<string, (BND4 Bnd, DCX.CompressionInfo Compression)> binders, int n, List<string> notes,
        int templateChr = 0, Stage stage = Stage.All, ChrEffects? fx = null, Plan? plan = null)
    {
        // A DS3 variant (c2191) plays its base's animations (c2190): its own files first, then the base's.
        var ds3Files = Ds3AnimationSources(x).SelectMany(chr => ds3.Entries
                .Where(e => e.Path is { } p && Regex.IsMatch(ItemText.Leaf(p), $@"^c{chr:D4}(_.*)?\.anibnd\.dcx$", RegexOptions.IgnoreCase))
                .OrderBy(e => e.Path, StringComparer.Ordinal)
                .SelectMany(e => BND4.Read(ds3.Read(e.Path!)).Files))
            .ToList();
        var ds3SkeletonFile = ds3Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
        if (ds3SkeletonFile is null)
        {
            notes.Add("DS3 has no skeleton; template animations kept");
            return 0;
        }
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(ds3SkeletonFile.Bytes);
        var ds3Hkx = ds3Files.Where(f => AnimLeaf().IsMatch(ItemText.Leaf(f.Name)))
            .GroupBy(f => ItemText.Leaf(f.Name).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Bytes);
        (int, int) LeafKey(string leaf) => AnimLeaf().Match(leaf) is var lm ? (int.Parse(lm.Groups[1].Value), int.Parse(lm.Groups[2].Value)) : default;
        var ds3Taes = new List<(int, TAE)>();
        foreach (var file in ds3Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
            try { ds3Taes.Add((TaeSection(file.Name), TAE.Read(file.Bytes))); }
            catch (Exception) { }
        var ds3Keys = ds3Hkx.Keys.Select(LeafKey).ToHashSet();
        var moves = Ds3Moves(ds3Taes, ds3Keys);
        var moveKeys = moves.Keys.ToHashSet();
        bool fill = FillMoves;
        // Additive clips (flinches played on top of the current move, 9000-9499 in both games) and whole poses only
        // stand in for their own kind. Builds 1.0.284-1.0.285 filled every template file with the nearest DS3 move by
        // ID alone, which could put a whole-body move in an additive slot: added to the pose the enemy already has
        // each time it is hit.
        var ds3Additive = ds3Hkx.Where(f => { try { return AnimPort.Ds3Additive(f.Value); } catch (Exception) { return false; } })
            .Select(f => LeafKey(f.Key)).ToHashSet();
        var additiveKeys = moveKeys.Where(k => ds3Additive.Contains(moves[k].Hkx)).ToHashSet();
        var wholeKeys = moveKeys.Where(k => !ds3Additive.Contains(moves[k].Hkx)).ToHashSet();

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
            var merge = MergeSkeleton(skeleton, ds3Skeleton);
            using var output = new MemoryStream();
            new HavokBinarySerializer().Write(root, output);
            skeletonFile.Bytes = output.ToArray();
            erSkeleton = new AnimPort.ErSkeleton { Names = skeleton.m_bones.Select(bone => bone.m_name ?? "").ToList(), ReferencePose = skeleton.m_referencePose, Parents = skeleton.m_parentIndices };
            notes.Add($"skeleton: {merge.Shared} bones shared with the template, {merge.Added} DS3 bones added"
                      + (merge.Reparented > 0 ? $", {merge.Reparented} moved under their DS3 parent" : "")
                      + (merge.Replaced > 0 ? $", {merge.Replaced} template bones set aside (other parent in DS3)" : "") + $", {skeleton.m_bones.Count} in all");
        }

        if (stage == Stage.Skeleton)
            return 0;

        // DS3 attacks the template has no slot for (ChrMoves): a file and TAE entry each, filled below like any move,
        // and a behaviour state once the events are in. Only with DS3's AI, which is what asks for them.
        var extra = new List<ChrMoves.Added>();
        if (stage == Stage.All && plan is not null && plan.Method == Method.Full && ChrMoves.Enabled && AiPort.Enabled)
            try
            {
                var attackFiles = anibnds.SelectMany(b => b.Value.Bnd.Files.Select(f => (b.Value.Bnd, File: f)))
                    .Select(p => (p.Bnd, p.File, Leaf: ItemText.Leaf(p.File.Name).ToLowerInvariant()))
                    .Where(p => AnimLeaf().IsMatch(p.Leaf) && LeafKey(p.Leaf) is (0, var id) && ChrMoves.IsAttack(id)).ToList();
                var templateFiles = attackFiles.Select(p => LeafKey(p.Leaf).Item2).ToHashSet();
                var templateAdditive = attackFiles.Where(p => TemplateAdditive.GetOrAdd((templateChr, p.Leaf, p.File.Bytes.Length), _ =>
                {
                    try { return AnimPort.IsAdditive(p.File.Bytes, Compendium(p.Bnd) ?? compendium); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
                })).Select(p => LeafKey(p.Leaf).Item2).ToHashSet();
                var templateEntries = anibnds.SelectMany(b => b.Value.Bnd.Files)
                    .Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase) && TaeSection(f.Name) == 0)
                    .SelectMany(f => TAE.Read(f.Bytes).Animations.Select(a => (int)a.ID)).ToHashSet();
                var wanted = ChrMoves.Plan(wholeKeys.Where(k => k.Item1 == 0).Select(k => k.Item2), templateEntries, templateFiles, templateAdditive);
                extra = ChrMoves.AddAnimations(anibnds.Select(b => b.Value.Bnd), wanted);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"extra DS3 attacks: not added ({Describe(ex)})");
                extra.Clear();
            }

        int replaced = 0, failed = 0, filled = 0, kept = 0, additive = 0;
        var durations = new Dictionary<(int, int), (float Ds3, float Er)>();
        var chosen = new Dictionary<(int, int), (int, int)>();
        foreach (var (_, (bnd, _)) in anibnds)
        {
            var bndCompendium = Compendium(bnd) ?? compendium;
            foreach (var file in bnd.Files)
            {
                var leaf = ItemText.Leaf(file.Name).ToLowerInvariant();
                if (!AnimLeaf().IsMatch(leaf))
                    continue;
                var key = LeafKey(leaf);
                bool isAdditive = TemplateAdditive.GetOrAdd((templateChr, leaf, file.Bytes.Length), _ =>
                {
                    try { return AnimPort.IsAdditive(file.Bytes, bndCompendium); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
                });
                if (isAdditive)
                    additive++;
                if (ChooseMove(key, isAdditive ? additiveKeys : wholeKeys, fill) is not { } pick
                    || !ds3Hkx.TryGetValue($"a{moves[pick].Hkx.Section:D3}_{moves[pick].Hkx.Id:D6}.hkx", out var ds3Bytes))
                {
                    kept++;
                    continue;
                }
                try
                {
                    var (hkx, _, templateDuration) = AnimPort.ConvertTimed(ds3Bytes, ds3Skeleton, erSkeleton, file.Bytes, bndCompendium, AnimPort.Spline);
                    file.Bytes = hkx;
                    durations[key] = (AnimPort.Duration(ds3Bytes), templateDuration);
                    chosen[key] = pick;
                    replaced++;
                    if (pick != key && pick != (0, key.Item2))
                        filled++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed++;
                }
            }
        }
        // What the character shows, sounds like and casts (the full port only): its effect binder and sound bank are
        // made first, so each move's DS3 events can name what was converted. A failure leaves the template's.
        ChrEffects.Session? session = null;
        if (fx is not null && plan is not null && stage > Stage.Moves)
            try
            {
                session = fx.Open(plan, binders, ds3Taes.SelectMany(t => t.Item2.Animations.SelectMany(a => a.Events)).ToList(), notes);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"effects: not ported, the template's effects and sounds stay ({Describe(ex)})");
            }
        int merged = 0;
        foreach (var (_, (bnd, _)) in anibnds)
            foreach (var file in bnd.Files.Where(f => stage > Stage.Moves && f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
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
                    // The entry's events go with the animation its file now holds: DS3's entry of the same ID when
                    // that plays the same DS3 animation, else the DS3 move the file was filled with.
                    if (ErHkx(anim, section) is not { } hkxKey || !durations.TryGetValue(hkxKey, out var d) || !chosen.TryGetValue(hkxKey, out var content))
                        continue;
                    var source = ChooseMove(AnimKey(anim.ID, section), ds3Additive.Contains(moves[content].Hkx) ? additiveKeys : wholeKeys, fill) is { } own
                                 && moves[own].Hkx == moves[content].Hkx ? own : content;
                    anim.Events = ChrTae.Merge(moves[source].Events, anim, lengths, groups, d.Ds3, d.Er, session, fx?.Groups);
                    changed = true;
                    merged++;
                }
                if (changed)
                    file.Bytes = tae.Write();
            }
        notes.Add($"{replaced} DS3 animations ({failed} failed; {filled} are the nearest DS3 move in place of a template move DS3 has no match for, {kept} template animations kept; {additive} additive flinch files matched with DS3 additive clips only), {merged} moves with DS3 events");
        if (extra.Count > 0)
        {
            int states = 0;
            if (binders.TryGetValue($"/chr/c{n:D4}.behbnd.dcx", out var behbnd))
                try { states = ChrMoves.AddStates(behbnd.Bnd, extra, notes); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { notes.Add($"extra DS3 attacks: behaviour states not added ({Describe(ex)})"); }
            notes.Add($"{extra.Count} DS3 attacks the template has no slot for added ({string.Join(",", extra.Select(e => e.Id))}), {states} behaviour states");
        }
        return replaced;
    }

    /// <summary>Which template animation files are additive, read once per template (many characters share one).</summary>
    private static readonly ConcurrentDictionary<(int Chr, string Leaf, int Length), bool> TemplateAdditive = new();

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

    // ---- Params ----

    /// <summary>
    /// The new character's param rows: ChrModelParam (copied from the template), one NpcParam row per DS3 NpcParam
    /// row placed with it (the template's row with DS3's health and souls, the template's sound bank, and for method A
    /// DS3's attack data under its own behaviour variation). Returns how many NpcParam rows were added.
    /// </summary>
    private static int PortParams(ParamStore er, ParamStore ds3, AttackParamPort attacks, Plan plan, IReadOnlyDictionary<int, int> npcRows,
        List<string>? notes = null, DropPort? drops = null, ChrEffects? fx = null, TemplateEffects? templateFx = null)
    {
        var npc = er.Get("NpcParam")!;
        var ds3Npc = ds3.Get("NpcParam");
        var template = npc[plan.Template.NpcParam] ?? throw new InvalidDataException($"template NpcParam {plan.Template.NpcParam} is missing");
        bool newModel = plan.Method != Method.Stats;
        // ChrModelParam is optional (a model without a row loads with the defaults) and Paramdex's def for it doesn't
        // fit Elden Ring 2.7.2 (row size 16): reading it threw before any NpcParam row was added (2026-11-07 test).
        if (newModel)
            try
            {
                if (er.Get("ChrModelParam") is { } models && models[plan.TemplateChr] is { } modelRow && models[plan.NewChr] is null)
                {
                    models.Rows.Add(CopyRow(models, modelRow, plan.NewChr));
                    models.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (notes is not null && !notes.Any(n => n.StartsWith("ChrModelParam:", StringComparison.Ordinal)))
                    notes.Add($"ChrModelParam: left out ({Describe(ex)}); the new models load with the defaults");
            }
        int added = 0;
        foreach (var (ds3Id, id) in npcRows.OrderBy(r => r.Key))
        {
            if (npc[id] is not null)
                continue;
            var row = CopyRow(npc, template, id);
            row.Name = $"[DS3] {ds3Npc?[ds3Id]?.Name ?? plan.Ds3.Name}" + (plan.Stage == Stage.All ? "" : $" (test {(int)plan.Stage})");
            // The test set stays on the template's row with health to spare, so it can be hit again and again.
            if (plan.Stage != Stage.All && Cell(row, "hp") is { } testHp)
                Set(testHp, TestHp);
            if (plan.Stage == Stage.All && ds3Npc?[ds3Id] is { } source)
                foreach (var field in new[] { "hp", "getSoul", "stamina", "mp" })
                    if (Cell(source, field) is { } from && Cell(row, field) is { } to)
                        Set(to, from.Value);
            // DS3's model hides and shows its "#NN#" meshes (weapon and armour variants) by DS3's display masks.
            if (newModel && plan.Stage != Stage.Clone && ds3Npc?[ds3Id] is { } maskSource)
                CopyDisplayMasks(maskSource, row);
            // DS3's drops with DS3's chances, on any body; the staged test set keeps the template's.
            if (plan.Stage == Stage.All && ds3Npc?[ds3Id] is { } dropSource)
                drops?.Apply(dropSource, row);
            // The template's own effects (a skeleton's revival) don't come with its body (TemplateEffects).
            if (templateFx is not null && plan.Method == Method.Full && plan.Stage == Stage.All)
                templateFx.Strip(row, plan.Template.NpcParam, plan.Template.Name);
            if (newModel && Cell(row, "SoundAddBankId") is { } bank && Convert.ToInt32(bank.Value) < 0)
                Set(bank, plan.TemplateChr);
            // The row goes in first: a placed or spawned character whose NpcParam row is missing crashes the game, a
            // character that keeps the template's attack data only hits like the template.
            npc.Rows.Add(row);
            added++;
            // DS3's own effects (a lantern's glow, an aura), and the character's effect binder and sound bank.
            if (fx is not null && plan.Method == Method.Full && plan.Stage == Stage.All)
                try { fx.ApplyNpc(row, ds3Npc?[ds3Id], plan); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { notes?.Add($"c{plan.Ds3.Chr:D4} {plan.Ds3.Name}: DS3 effects not added to its row ({Describe(ex)})"); }
            if (plan.Method == Method.Full && plan.Stage == Stage.All && ds3Npc?[ds3Id] is { } src && Cell(src, "behaviorVariationId") is { } ds3Var
                && Cell(row, "behaviorVariationId") is { } erVar)
            {
                try
                {
                    int variation = attacks.PortVariation(Convert.ToInt32(ds3Var.Value), Convert.ToInt32(erVar.Value), plan.NewChr * 100);
                    if (variation > 0)
                        Set(erVar, variation);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    notes?.Add($"c{plan.Ds3.Chr:D4} {plan.Ds3.Name}: DS3 attacks not ported, the template's kept ({Describe(ex)})");
                }
            }
        }
        npc.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return added;
    }

    /// <summary>
    /// The last safety net: every NpcParam row the maps and the F1 spawn list name exists, at least as a plain copy of
    /// the template's row (a missing row crashes the game when the character loads). Returns how many were added.
    /// </summary>
    public static int EnsureNpcRows(PARAM npc, IEnumerable<(int Template, int Row)> rows)
    {
        int added = 0;
        foreach (var (templateId, id) in rows)
        {
            if (npc[id] is not null || npc[templateId] is not { } template)
                continue;
            npc.Rows.Add(CopyRow(npc, template, id));
            added++;
        }
        if (added > 0)
            npc.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return added;
    }

    /// <summary>An exception as one report line: type, message and the method it came from.</summary>
    public static string Describe(Exception ex)
    {
        var frame = ex.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal)) ?? "";
        var at = frame.Length > 0 ? " " + (frame.IndexOf(" in ", StringComparison.Ordinal) is > 0 and var i ? frame[..i] : frame) : "";
        // A write's "Not all reservations filled" hides what stopped it: name that first, the reservations after.
        if (MaskedErrors.Cause(ex) is { } cause)
            return $"{Describe(cause)} (then: {ex.GetType().Name}: {(ex.Message.Length > 120 ? ex.Message[..120] + "..." : ex.Message)})";
        return $"{ex.GetType().Name}: {ex.Message}{at}";
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

    public static IEnumerable<string> OutputFiles(PorterContext ctx) => [ctx.ModPath(ManifestFile), ctx.ModPath(EnemyPort.ChrMap.FileName), ctx.ModPath(SpawnList.FileName)];

    internal static string Describe(Plan p) => new StringBuilder()
        .Append($"c{p.Ds3.Chr:D4} {p.Ds3.Name} -> {p.Template.Model} ({p.Template.Name}, bone match {p.BoneMatch:0.00}) as c{p.NewChr:D4}, {p.Method}")
        .ToString();
}
