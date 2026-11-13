using System.Text.RegularExpressions;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// DS3's enemy variants (2026-11-13: the High Wall's spear Lothric Knights held their shield sideways). One DS3
/// character holds several weapon sets: c1280 has a sword and kite shield, a spear and another shield, a greatsword
/// and a crossbow, shown by the row's display masks. Each set has its own moves in its own TAE section (a001 spear,
/// a002 greatsword...), and the NpcParam row picks the section with a resident SpEffect whose animIdOffset is
/// section × 1,000,000 (5404 = +1,000,000 for every spear knight; 363 DS3 rows do this). A move the section lacks plays
/// the base one. The shield bone (L_Weapon001) rests ~94° turned in the spear set compared with the sword set, so
/// the spear knights, who played the sword set's moves, held their shield on its side.
/// <para>Elden Ring does variants the same way (Juvenile Scholars: SpEffect 5402 = +2,000,000; c4340's selectors hold
/// a000_X and a001_X), with two more pieces the port has to lay out: each section move is a TAE entry
/// (section × 1,000,000 + ID) with its own file, and every behaviour selector of type AnimIdOffset lists one clip per
/// section, found by name (c3000's selectors list a000 and a005 with nothing between).</para>
/// For every ported character, the same way:
/// <list type="bullet">
/// <item><see cref="AddSectionFiles"/>: for each DS3 section other than 0, a file and TAE entry next to each template
/// move whose ID DS3's section has its own animation for (the port then fills them with DS3's clip and events);</item>
/// <item><see cref="AddSelectorClips"/>: those clips in the template's AnimIdOffset selectors;</item>
/// <item>the row: DS3's offset effect is ported with its animIdOffset (SpEffectPort, ChrEffects.NpcEffectWanted), the
/// template's own offset effects come off the row (a ported Cathedral Evangelist kept the template's 5402 and played
/// section 2), and DS3's NpcParam animIdOffset is copied (ChrPort.PortParams).</item>
/// </list>
/// Fallback enemy-model L: DARKSIGN_ENEMY_VARIANTS=0 (every variant plays the base moves, as up to 1.0.443).
/// </summary>
public static partial class ChrVariants
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_VARIANTS")?.Trim() != "0";

    public const int SectionStep = 1_000_000;

    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex HkxLeaf();

    /// <summary>
    /// The (section, ID) pairs to add: for each DS3 section above 0, every template base move (an ID with its own file
    /// and entry) whose ID DS3's section plays its own animation for (not the base one), of the same kind (additive or
    /// whole pose), and that the template doesn't already have in that section.
    /// </summary>
    /// <param name="templateMoves">The template's section 0 IDs that have a file and an entry.</param>
    /// <param name="templateHas">Every (section, ID) the template's binders already have a file or entry for.</param>
    /// <param name="ds3Plays">DS3's moves: (section, ID) -> the animation file it plays.</param>
    public static List<(int Section, int Id)> Plan(IEnumerable<int> templateMoves, IReadOnlySet<(int Section, int Id)> templateHas,
        IReadOnlyDictionary<(int Section, int Id), (int Section, int Id)> ds3Plays, Func<int, bool> templateAdditive, Func<(int, int), bool> ds3Additive)
    {
        var plan = new List<(int, int)>();
        var sections = ds3Plays.Keys.Select(k => k.Section).Where(s => s is > 0 and < 1000).Distinct().Order().ToList();
        var ids = templateMoves.Distinct().Order().ToList();
        foreach (int s in sections)
            foreach (int id in ids)
            {
                if (templateHas.Contains((s, id)) || !ds3Plays.TryGetValue((s, id), out var file))
                    continue;
                // A section entry that plays the base file is the base move again: the game falls back to it anyway.
                if (file.Section == 0 || (ds3Plays.TryGetValue((0, id), out var baseFile) && baseFile == file))
                    continue;
                if (templateAdditive(id) != ds3Additive(file))
                    continue;
                plan.Add((s, id));
            }
        return plan;
    }

    /// <summary>An entry's file name for a section move: "a001_003000.hkt".</summary>
    public static string HktName(int section, int id) => $"a{section:D3}_{id:D6}.hkt";

    /// <summary>
    /// Adds each planned section move to the binders: a copy of the base move's file (same binder, the file ID moved by
    /// a section, path and name for the section) and a TAE entry cloned from the base entry under section × 1,000,000 + ID,
    /// playing its own file. Returns the moves added.
    /// </summary>
    public static List<(int Section, int Id)> AddSectionFiles(IEnumerable<BND4> anibnds, IReadOnlyList<(int Section, int Id)> plan)
    {
        var added = new List<(int, int)>();
        if (plan.Count == 0)
            return added;
        var binders = anibnds.ToList();
        var taeFile = binders.SelectMany(b => b.Files).Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(f => ChrPort.TaeSection(f.Name) == 0);
        if (taeFile is null)
            return added;
        var tae = TAE.Read(taeFile.Bytes);
        var entries = tae.Animations.GroupBy(a => a.ID).ToDictionary(g => g.Key, g => g.First());
        var baseFiles = new Dictionary<int, (BND4 Bnd, BinderFile File)>();
        foreach (var b in binders)
            foreach (var f in b.Files)
                if (HkxLeaf().Match(ItemText.Leaf(f.Name)) is { Success: true } m && int.Parse(m.Groups[1].Value) == 0)
                    baseFiles.TryAdd(int.Parse(m.Groups[2].Value), (b, f));
        foreach (var (s, id) in plan)
        {
            long entryId = (long)s * SectionStep + id;
            if (!entries.TryGetValue(id, out var donor) || entries.ContainsKey(entryId) || !baseFiles.TryGetValue(id, out var donorFile))
                continue;
            long fileId = donorFile.File.ID + (long)s * SectionStep;
            if (fileId > int.MaxValue || donorFile.Bnd.Files.Any(f => f.ID == fileId))
                continue;
            var leaf = ItemText.Leaf(donorFile.File.Name);
            var name = donorFile.File.Name[..^leaf.Length] + $"a{s:D3}_{id:D6}.hkx";
            donorFile.Bnd.Files.Add(new BinderFile(donorFile.File.Flags, (int)fileId, name, (byte[])donorFile.File.Bytes.Clone()));
            var entry = ChrMoves.CloneEntry(donor, id);
            entry.ID = entryId;
            entry.AnimFileName = HktName(s, id);
            // Its own file, whatever the base entry borrowed.
            entry.MiniHeader = donor.MiniHeader is TAE.Animation.AnimMiniHeader.Standard std
                ? new TAE.Animation.AnimMiniHeader.Standard { IsLoopByDefault = std.IsLoopByDefault, AllowDelayLoad = std.AllowDelayLoad, ImportsHKX = false, ImportHKXSourceAnimID = 0 }
                : new TAE.Animation.AnimMiniHeader.Standard();
            tae.Animations.Add(entry);
            entries[entryId] = entry;
            added.Add((s, id));
        }
        foreach (var b in binders)
            b.Files.Sort((x, y) => x.ID.CompareTo(y.ID));
        tae.Animations.Sort((x, y) => x.ID.CompareTo(y.ID));
        taeFile.Bytes = tae.Write();
        return added;
    }

    /// <summary>
    /// Step 2, the behaviour: in every file of the behaviour binder, each AnimIdOffset selector playing a base move that
    /// got section moves gets a clip per section (a copy of its base clip naming the section's file, registered in the
    /// graph's animation list). Returns how many clips were added.
    /// </summary>
    public static int AddSelectorClips(BND4 behbnd, IReadOnlyList<(int Section, int Id)> added)
    {
        if (added.Count == 0)
            return 0;
        var compendium = behbnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
        int total = 0;
        foreach (var file in behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
            lock (AnimPort.HavokLock)
            {
                IHavokObject root;
                try { root = AnimPort.ReadEr(file.Bytes, compendium); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
                int clips = AddSelectorClips(root, added);
                if (clips <= 0)
                    continue;
                using var output = new MemoryStream();
                new HavokBinarySerializer().Write(root, output);
                file.Bytes = output.ToArray();
                total += clips;
            }
        return total;
    }

    /// <summary>The clips for a graph already read.</summary>
    public static int AddSelectorClips(IHavokObject root, IReadOnlyList<(int Section, int Id)> added)
    {
        var all = HavokGraph.All(root);
        var strings = all.OfType<hkbBehaviorGraphStringData>().FirstOrDefault();
        if (strings is null)
            return 0;
        var bySection = added.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.Select(a => a.Section).Distinct().Order().ToList());
        var animations = strings.m_animationNames;
        int count = 0;
        foreach (var selector in all.OfType<CustomManualSelectorGenerator>().Where(s => s.m_offsetType == CustomManualSelectorGenerator.OffsetType.AnimIdOffset))
        {
            if (!bySection.TryGetValue(selector.m_animId, out var sections))
                continue;
            string baseName = $"a000_{selector.m_animId:D6}";
            var baseClip = selector.m_generators.OfType<hkbClipGenerator>().FirstOrDefault(c => c.m_animationName == baseName);
            if (baseClip is null)
                continue;
            foreach (int s in sections)
            {
                var name = $"a{s:D3}_{selector.m_animId:D6}";
                if (selector.m_generators.OfType<hkbClipGenerator>().Any(c => c.m_animationName == name))
                    continue;
                // A clip's internal ID is its index in the graph's animation list ("..\Model\chr\c4340\hkx\a001_000020.hkx").
                string path = baseClip.m_animationInternalId >= 0 && baseClip.m_animationInternalId < animations.Count && animations[baseClip.m_animationInternalId] is { } basePath
                    ? basePath[..(basePath.LastIndexOf('\\') + 1)] + name + ".hkx" : name + ".hkx";
                int index = animations.FindIndex(n => n is not null && n.EndsWith("\\" + name + ".hkx", StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    index = animations.Count;
                    animations.Add(path);
                }
                var copy = HavokGraph.ShallowCopy(baseClip);
                copy.m_name = baseClip.m_name?.Replace(baseName, name, StringComparison.Ordinal) ?? $"{name}_hkx_AutoSet_00";
                copy.m_animationName = name;
                copy.m_animationInternalId = checked((short)index);
                selector.m_generators.Add(copy);
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// The animation offset a DS3 NpcParam row plays with: its own animIdOffset (DS3's def names two such fields), else
    /// the first resident effect with one. 0 when neither sets one.
    /// </summary>
    public static int Ds3Offset(PARAM.Row ds3Row, Func<int, PARAM.Row?> ds3SpEffect)
    {
        foreach (var cell in ds3Row.Cells.Where(c => c.Def.InternalName.Equals("animIdOffset", StringComparison.OrdinalIgnoreCase)))
            if (cell.Value is not byte[] && Convert.ToInt32(cell.Value) > 0)
                return Convert.ToInt32(cell.Value);
        for (int k = 0; k < 32; k++)
            if (ParamDefaults.Int(ds3Row, $"spEffectId{k}") is > 0 and var id && ds3SpEffect(id) is { } effect
                && ParamDefaults.Int(effect, "animIdOffset") is > 0 and var offset)
                return offset;
        return 0;
    }

    /// <summary>
    /// Takes the template's resident effects that shift animations off a ported row (they would play the template's
    /// variant section with DS3's moves in it). Returns the IDs taken off.
    /// </summary>
    public static List<int> StripTemplateOffsets(PARAM.Row row, Func<int, PARAM.Row?> erSpEffect)
    {
        var taken = new List<int>();
        for (int k = 0; k < 32; k++)
        {
            var name = $"spEffectID{k}";
            if (ParamDefaults.Int(row, name) is > 0 and var id && erSpEffect(id) is { } effect && ParamDefaults.Int(effect, "animIdOffset") > 0)
            {
                ParamDefaults.Set(row, name, -1);
                taken.Add(id);
            }
        }
        return taken;
    }
}
