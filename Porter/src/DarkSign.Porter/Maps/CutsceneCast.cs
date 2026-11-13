using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Makes a converted cutscene ask Elden Ring only for things it can load: the cast (every animated part), the cut
/// files, and the draw settings. Cutscenes that named something Elden Ring doesn't have went to a black screen and
/// never ended (Friede, Dancer, Ariandel in the 1.0.417 and 1.0.442 playtests), because the game waits for every
/// resource of a cutscene before it fades in.
/// <list type="bullet">
/// <item><b>Cast.</b> Every part timeline (resource kind 1) of Elden Ring's own cutscenes names its map as -1 (areaNo,
/// blockNo, mapNo, altNo: 100 cutscenes read on Devin's PC, cutscene-survey 2026-11-12): Elden Ring spawns a
/// cutscene's characters and assets from their models (c4750_0000, AEG210_901_5003), not from the map. DS3 names
/// its own models (c6020 Friede, o459020 objects, h45_00_00_00_008100 a collision), which Elden Ring doesn't have
/// unless they were ported, and then under another name. So each part is resolved to an Elden Ring model that
/// exists (the map port's new part names, the enemy port's DS3 -> Elden Ring models, the object port's assets);
/// cameras, the player and cutscene dummies (dNNNN_NNNN) keep their names; a part with no Elden Ring model is left
/// out, with the parts under it and its timelines (and its Havok track, <see cref="CutscenePort.ConvertHkx"/>).</item>
/// <item><b>Cut files.</b> Elden Ring keeps each cut's Havok file as cutNNNN\a0000.hkx with binder ID NNNN*10000 and
/// nothing else (every Elden Ring cutscene read), while DS3 also numbers them a0001 (Dancer, IDs 110001...) and
/// ships cut0000 files no cut plays (Friede's standby poses). Each cut's file becomes a0000 at Elden Ring's ID, its
/// HKT resource follows, and files no cut plays are left out.</item>
/// <item><b>Draw settings.</b> Kinds 199 (all-in-one draw settings), 187 (character light) and 239 (in-game draw
/// setting) only name DS3 light banks and gparam IDs (DS3's 239 names gparam 1002, which the slot's gparam doesn't
/// hold), so they are left out and the cutscene keeps the map's look.</item>
/// </list>
/// DARKSIGN_CUTSCENE_CAST=0 (launcher fallback map-cutscenes E) turns all three off.
/// </summary>
public static partial class CutsceneCast
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_CUTSCENE_CAST") != "0";

    /// <summary>Resource kinds that only point at DS3 draw data (light banks, gparam IDs): left out.</summary>
    public static readonly IReadOnlySet<int> Ds3DrawKinds = new HashSet<int> { 187, 199, 239 };

    public const int PartKind = 1, HktKind = 266;

    [GeneratedRegex(@"^d\d{4}_\d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex DummyRegex();

    [GeneratedRegex(@"^c(\d{4})_(\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex ChrRegex();

    [GeneratedRegex(@"^AEG\d{3}_\d{3}_\d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex AssetPartRegex();

    [GeneratedRegex(@"^cut(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CutRegex();

    /// <summary>Names Elden Ring needs no model for: the player, cameras and cutscene dummies.</summary>
    public static bool NeedsNoModel(string name) =>
        name.StartsWith(CutscenePort.PlayerPart[..5] + "_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("cam", StringComparison.OrdinalIgnoreCase)
        || DummyRegex().IsMatch(name);

    /// <summary>The model of an Elden Ring part name: "c7073" for c7073_9000, "AEG920_001" for AEG920_001_9000, or null.</summary>
    public static string? ModelOf(string erName)
    {
        if (ChrRegex().Match(erName) is { Success: true } c)
            return "c" + c.Groups[1].Value;
        if (AssetPartRegex().IsMatch(erName))
            return erName[..10];
        return null;
    }

    /// <summary>
    /// What one map's cutscenes call their parts: DS3 part name -> the Elden Ring name the cutscene uses, or null when
    /// Elden Ring has no model for it (the part is left out).
    /// </summary>
    /// <param name="partNames">The map port's DS3 -> Elden Ring part names (placed enemies, people and assets).</param>
    /// <param name="chrModel">The enemy port's Elden Ring model for a DS3 character model (c6020 -> c7xxx), or null.</param>
    /// <param name="modelExists">Whether Elden Ring (the mod or the game) has a model ("c7073", "AEG920_001").</param>
    public static Func<string, string?> Resolver(IReadOnlyDictionary<string, string> partNames, Func<string, string?> chrModel, Func<string, bool> modelExists)
    {
        // DS3 model -> Elden Ring model, from the parts the map port renamed (o459020 -> AEG920_001, c6020 -> c7120).
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in partNames)
        {
            int a = from.LastIndexOf('_');
            if (a > 0 && ModelOf(to) is { } m)
                models.TryAdd(from[..a], m);
        }
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        return name =>
        {
            if (cache.TryGetValue(name, out var known))
                return known;
            string? result = null;
            if (NeedsNoModel(name))
                result = name;
            else if (partNames.TryGetValue(name, out var placed) && ModelOf(placed) is { } pm && modelExists(pm))
                result = placed;
            else
            {
                int a = name.LastIndexOf('_');
                var ds3Model = a > 0 ? name[..a] : name;
                var instance = a > 0 && int.TryParse(name[(a + 1)..], out var n) ? n : 0;
                var erModel = models.GetValueOrDefault(ds3Model) ?? (ds3Model.StartsWith('c') ? chrModel(ds3Model) : null);
                if (erModel is not null && modelExists(erModel))
                    result = $"{erModel}_{instance:D4}";
                // An Elden Ring name already (a part the map port placed under its own name).
                else if (ModelOf(name) is { } own && modelExists(own))
                    result = name;
            }
            cache[name] = result;
            return result;
        };
    }

    public sealed class CastStats
    {
        public int Kept, Renamed;
        public SortedSet<string> LeftOut { get; } = new(StringComparer.Ordinal);
        public int DrawLeftOut, FilesRenamed, FilesLeftOut;
        public List<string> Notes { get; } = [];

        public override string ToString() =>
            $"cast {Kept} kept ({Renamed} under Elden Ring names), {LeftOut.Count} left out with no Elden Ring model"
            + (LeftOut.Count > 0 ? $" ({string.Join(" ", LeftOut.Take(12))}{(LeftOut.Count > 12 ? " ..." : "")})" : "")
            + $"; {DrawLeftOut} DS3 draw settings left out; cut files: {FilesRenamed} renamed to a0000, {FilesLeftOut} no cut plays left out"
            + (Notes.Count > 0 ? "; " + string.Join("; ", Notes.Distinct().Take(4)) : "");
    }

    /// <summary>
    /// The DS3 timeline's resources to leave out before conversion: parts with no Elden Ring model (and everything
    /// under them) and DS3-only draw settings. Returns their indices.
    /// </summary>
    public static HashSet<int> Unplayable(MQB mqb, Func<string, string?> resolve, CastStats stats)
    {
        var drop = new HashSet<int>();
        for (int i = 0; i < mqb.Resources.Count; i++)
        {
            var r = mqb.Resources[i];
            if (Ds3DrawKinds.Contains(r.Unk48))
            {
                drop.Add(i);
                stats.DrawLeftOut++;
            }
            else if (r.Unk48 == PartKind)
            {
                var to = resolve(r.Name);
                if (to is null)
                {
                    drop.Add(i);
                    stats.LeftOut.Add(r.Name);
                }
                else
                {
                    stats.Kept++;
                    if (to != r.Name)
                        stats.Renamed++;
                }
            }
        }
        // Whatever hangs under a part that goes, goes too.
        bool more = true;
        while (more)
        {
            more = false;
            for (int i = 0; i < mqb.Resources.Count; i++)
                if (!drop.Contains(i) && mqb.Resources[i].ParentIndex >= 0 && drop.Contains(mqb.Resources[i].ParentIndex))
                    more = drop.Add(i) | more;
        }
        return drop;
    }

    /// <summary>Leaves the given resources out of a timeline: their events, timelines left empty, and indices moved down.</summary>
    public static void Remove(MQB mqb, IReadOnlySet<int> drop)
    {
        if (drop.Count == 0)
            return;
        var newIndex = new int[mqb.Resources.Count];
        for (int i = 0, n = 0; i < mqb.Resources.Count; i++)
            newIndex[i] = drop.Contains(i) ? -1 : n++;
        int Map(int i) => i >= 0 && i < newIndex.Length ? newIndex[i] : i;
        foreach (var cut in mqb.Cuts)
        {
            var kept = new List<MQB.Timeline>();
            foreach (var tl in cut.Timelines)
            {
                int had = tl.Events.Count;
                tl.Events.RemoveAll(e => e.ResourceIndex >= 0 && e.ResourceIndex < newIndex.Length && newIndex[e.ResourceIndex] < 0);
                if (had > 0 && tl.Events.Count == 0)
                    continue;
                foreach (var e in tl.Events)
                    e.ResourceIndex = Map(e.ResourceIndex);
                kept.Add(tl);
            }
            cut.Timelines = kept;
        }
        var resources = new List<MQB.Resource>();
        for (int i = 0; i < mqb.Resources.Count; i++)
        {
            if (drop.Contains(i))
                continue;
            var r = mqb.Resources[i];
            r.ParentIndex = r.ParentIndex >= 0 && r.ParentIndex < newIndex.Length ? newIndex[r.ParentIndex] : r.ParentIndex;
            resources.Add(r);
        }
        mqb.Resources = resources;
    }

    /// <summary>The binder ID Elden Ring gives a cut's Havok file: cut0145 -> 1450000; -1 for a name that isn't cutNNNN.</summary>
    public static int CutFileId(string cutName) =>
        CutRegex().Match(cutName) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n * 10_000 : -1;

    /// <summary>
    /// The Havok file name ("a0001") each cut of a timeline plays, from its HKT resource (kind 266), by cut name.
    /// </summary>
    public static Dictionary<string, string> CutFiles(MQB mqb)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cut in mqb.Cuts)
        {
            var hkt = cut.Timelines.SelectMany(t => t.Events)
                .Where(e => e.ResourceIndex >= 0 && e.ResourceIndex < mqb.Resources.Count && mqb.Resources[e.ResourceIndex].Unk48 == HktKind)
                .Select(e => mqb.Resources[e.ResourceIndex]).FirstOrDefault();
            var leaf = hkt is null ? "a0000" : Path.GetFileNameWithoutExtension((hkt.Path ?? hkt.Name).Replace('\\', '/').Split('/')[^1]);
            files[cut.Name] = string.IsNullOrEmpty(leaf) ? "a0000" : leaf;
        }
        return files;
    }

    /// <summary>Points every HKT resource at a0000 (the name Elden Ring gives every cut's Havok file).</summary>
    public static int RenameHkts(MQB mqb)
    {
        int n = 0;
        foreach (var r in mqb.Resources.Where(r => r.Unk48 == HktKind))
        {
            var name = Regex.Replace(r.Name, @"^a\d{4}\.HKT$", "a0000.HKT", RegexOptions.IgnoreCase);
            var path = r.Path is null ? null : Regex.Replace(r.Path, @"\\a\d{4}\.HKT$", @"\a0000.HKT", RegexOptions.IgnoreCase);
            if (name != r.Name || path != r.Path)
                n++;
            r.Name = name;
            r.Path = path;
        }
        return n;
    }

    /// <summary>
    /// Lays out a converted binder's Havok files as Elden Ring's: each cut's file as cutNNNN\a0000.hkx with ID
    /// NNNN*10000; files no cut plays are left out. <paramref name="cutFiles"/> is <see cref="CutFiles"/> of the
    /// timeline before its HKTs were renamed.
    /// </summary>
    public static void LayOutCutFiles(BND4 bnd, IReadOnlyDictionary<string, string> cutFiles, CastStats stats)
    {
        var kept = new List<BinderFile>();
        foreach (var f in bnd.Files)
        {
            if (!f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(f);
                continue;
            }
            var parts = f.Name.Split('\\');
            var cut = parts.Length >= 2 ? parts[^2] : "";
            var leaf = Path.GetFileNameWithoutExtension(parts[^1]);
            if (!cutFiles.TryGetValue(cut, out var wanted) || !leaf.Equals(wanted, StringComparison.OrdinalIgnoreCase) || CutFileId(cut) < 0)
            {
                stats.FilesLeftOut++;
                continue;
            }
            int id = CutFileId(cut);
            var name = string.Join('\\', parts[..^1]) + @"\a0000.hkx";
            if (id != f.ID || name != f.Name)
                stats.FilesRenamed++;
            f.ID = id;
            f.Name = name;
            kept.Add(f);
        }
        var missing = cutFiles.Keys.Where(c => kept.All(f => !f.Name.Contains($"\\{c}\\", StringComparison.OrdinalIgnoreCase) || !f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
            stats.Notes.Add($"no Havok file for {string.Join(" ", missing)}");
        bnd.Files.Clear();
        bnd.Files.AddRange(kept.OrderBy(f => f.ID));
    }
}
