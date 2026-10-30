using System.Text;
using System.Text.RegularExpressions;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// explore-behavior: how Elden Ring's player behaviour (c0000.behbnd) reaches each weapon's animations, so new DS3
/// movesets can be registered under new motion categories. Lists the selectors (CustomManualSelectorGenerator) by
/// offset type, which motion categories each weapon selector has clips for, and how animation names are registered.
/// </summary>
public static partial class BehaviorExplore
{
    [GeneratedRegex(@"^a(\d{3})_(\d{6})$")]
    private static partial Regex ClipName();

    public static int Run(PorterContext ctx, IReadOnlyList<string> categories)
    {
        using var archives = ctx.OpenArchives(PorterGame.EldenRing);
        var bnd = BND4.Read(archives.Read("/chr/c0000.behbnd.dcx"));
        var report = new StringBuilder();
        report.AppendLine($"# /chr/c0000.behbnd.dcx: {bnd.Files.Count} files");
        foreach (var file in bnd.Files)
            report.AppendLine($"  {file.ID}\t{file.Name}\t{file.Bytes.Length}");
        var compendium = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;

        foreach (var file in bnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
        {
            List<IHavokObject> objects;
            try
            {
                var serializer = new HavokBinarySerializer();
                if (compendium is not null)
                    serializer.LoadCompendium(new MemoryStream(compendium));
                objects = serializer.ReadAllObjects(new MemoryStream(file.Bytes)).ToList();
            }
            catch (Exception e)
            {
                report.AppendLine($"\n## {ItemText.Leaf(file.Name)}: unreadable ({e.GetType().Name}: {e.Message})");
                continue;
            }
            report.AppendLine($"\n## {ItemText.Leaf(file.Name)}: {objects.Count} objects");
            foreach (var group in objects.GroupBy(o => o.GetType().Name).OrderByDescending(g => g.Count()).Take(25))
                report.AppendLine($"  {group.Key}: {group.Count()}");

            var clips = objects.OfType<hkbClipGenerator>().ToList();
            if (clips.Count > 0)
            {
                report.AppendLine($"  clip generators: {clips.Count}, animation internal ids {clips.Min(c => c.m_animationInternalId)}..{clips.Max(c => c.m_animationInternalId)}");
                report.AppendLine($"  sample clips: {string.Join(", ", clips.Take(8).Select(c => $"{c.m_name}={c.m_animationName}#{c.m_animationInternalId}"))}");
            }

            var selectors = objects.OfType<CustomManualSelectorGenerator>().ToList();
            if (selectors.Count > 0)
            {
                report.AppendLine($"  selectors: {selectors.Count}; by offset type: {string.Join(", ", selectors.GroupBy(s => s.m_offsetType).Select(g => $"{g.Key}={g.Count()}"))}");
                var allCategories = new SortedSet<int>();
                foreach (var selector in selectors.Where(s => s.m_offsetType is CustomManualSelectorGenerator.OffsetType.WeaponCategoryRight
                             or CustomManualSelectorGenerator.OffsetType.WeaponCategoryLeft or CustomManualSelectorGenerator.OffsetType.WeaponCategoryHandStyle
                             or CustomManualSelectorGenerator.OffsetType.SwordArtsCategory))
                {
                    var sections = selector.m_generators.OfType<hkbClipGenerator>().Select(c => ClipName().Match(c.m_animationName ?? c.m_name ?? ""))
                        .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
                    allCategories.UnionWith(sections);
                    var nonClips = selector.m_generators.Count(g => g is not hkbClipGenerator);
                    report.AppendLine($"    {selector.m_offsetType} animId {selector.m_animId} '{selector.m_name}': {selector.m_generators.Count} generators ({nonClips} not clips), sections {string.Join(" ", sections.Distinct().OrderBy(x => x))}");
                }
                report.AppendLine($"  motion sections reached through weapon selectors: {string.Join(" ", allCategories)}");
                foreach (var wanted in categories.Select(int.Parse))
                {
                    var hits = clips.Where(c => ClipName().Match(c.m_animationName ?? "") is { Success: true } m && int.Parse(m.Groups[1].Value) == wanted).ToList();
                    report.AppendLine($"  section a{wanted:D3}: {hits.Count} clips, e.g. {string.Join(", ", hits.Take(10).Select(c => c.m_animationName))}");
                }
            }

            foreach (var strings in objects.OfType<hkbCharacterStringData>())
                report.AppendLine($"  character string data: {strings.m_animationBundleNameData.Count} animation bundles, {strings.m_animationBundleFilenameData.Count} bundle file lists; " +
                                  $"first bundle: {strings.m_animationBundleNameData.FirstOrDefault()?.m_assetNames.Count ?? 0} names, e.g. {string.Join(", ", strings.m_animationBundleNameData.FirstOrDefault()?.m_assetNames.Take(5) ?? [])}");
        }

        var path = ctx.WorkPath("reports", "explore", "movesets", "behavior.txt");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"behaviour report -> {path}");
        return 0;
    }
}

/// <summary>
/// tae-dump: every event of one animation section's TAE (e.g. DS3 38 = great scythes), with raw parameter bytes,
/// so event types can be matched between the games.
/// </summary>
public static class TaeDump
{
    public static int Run(PorterContext ctx, PorterGame game, int section)
    {
        using var archives = ctx.OpenArchives(game);
        var leaf = $"a{section:D2}.tae";
        foreach (var entry in archives.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/chr/c0000", StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path))
        {
            var bnd = BND4.Read(archives.Read(entry.Path!));
            var file = bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(leaf, StringComparison.OrdinalIgnoreCase));
            if (file is null)
                continue;
            TAE.ValidateEventBank = false;
            var tae = TAE.Read(file.Bytes);
            var text = new StringBuilder();
            text.AppendLine($"# {game.ShortName} {entry.Path} {file.Name}: id {tae.ID}, {tae.Animations.Count} animations");
            foreach (var anim in tae.Animations)
            {
                var header = anim.MiniHeader is { } h ? $" mini {h.GetType().Name}" : "";
                text.AppendLine($"\n## {anim.ID}{header} file '{anim.AnimFileName}', {anim.Events.Count} events");
                foreach (var ev in anim.Events.OrderBy(e => e.StartTime))
                    text.AppendLine($"  {ev.Type,4} {ev.StartTime,7:0.000}-{ev.EndTime,7:0.000}  {Convert.ToHexString(ev.GetParameterBytes(false))}");
            }
            var path = ctx.WorkPath("reports", "explore", "movesets", $"tae-{game.ShortName}-a{section:D3}.txt");
            File.WriteAllText(path, text.ToString());
            Console.WriteLine($"{tae.Animations.Count} animations, {tae.Animations.Sum(a => a.Events.Count)} events -> {path}");
            return 0;
        }
        Console.Error.WriteLine($"{game.ShortName} has no {leaf} in its c0000 anim binders");
        return 1;
    }
}

/// <summary>
/// anim-id-map: pairs DS3 and Elden Ring player animation IDs by the name of the behaviour selector that plays them
/// (e.g. AttackRightLight1_CMSG plays 030000 in both). Elden Ring's behaviour is read with HKLib. DS3's (Havok 2014) is
/// read as a string table: a packfile stores each selector's name just before the names of the clips under it, so
/// every aXXX_YYYYYY clip name is credited to the selector name that precedes it.
/// </summary>
public static partial class AnimIdMap
{
    [GeneratedRegex(@"^a\d{3}_(\d{6})")]
    private static partial Regex ClipId();

    public static int Run(PorterContext ctx)
    {
        var ds3 = Ds3Selectors(ctx);
        var er = ErSelectors(ctx);
        var text = new StringBuilder("selector\tDS3 ids\tElden Ring ids\n");
        foreach (var name in ds3.Keys.Union(er.Keys).OrderBy(n => n, StringComparer.Ordinal))
            text.AppendLine($"{name}\t{Ids(ds3, name)}\t{Ids(er, name)}");
        var path = ctx.WorkPath("reports", "explore", "movesets", "anim-id-map.tsv");
        File.WriteAllText(path, text.ToString());
        Console.WriteLine($"{ds3.Count} DS3 selectors, {er.Count} Elden Ring selectors, {ds3.Keys.Intersect(er.Keys).Count()} in both -> {path}");
        return 0;
    }

    private static string Ids(Dictionary<string, SortedSet<int>> map, string name) =>
        map.TryGetValue(name, out var ids) ? string.Join(" ", ids) : "";

    internal static Dictionary<string, SortedSet<int>> Ds3Selectors(PorterContext ctx)
    {
        using var archives = ctx.OpenArchives(PorterGame.DarkSouls3);
        var bnd = BND4.Read(archives.Read("/chr/c0000.behbnd.dcx"));
        var behavior = bnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!;
        var map = new Dictionary<string, SortedSet<int>>();
        string? current = null;
        foreach (var s in Strings(behavior.Bytes))
        {
            if (s.EndsWith("_CMSG", StringComparison.Ordinal) || s.Contains("_CMSG0", StringComparison.Ordinal))
                current = s;
            else if (current is not null && ClipId().Match(s) is { Success: true } m)
            {
                if (!map.TryGetValue(current, out var ids))
                    map[current] = ids = [];
                ids.Add(int.Parse(m.Groups[1].Value));
            }
        }
        return map;
    }

    internal static Dictionary<string, SortedSet<int>> ErSelectors(PorterContext ctx)
    {
        using var archives = ctx.OpenArchives(PorterGame.EldenRing);
        var bnd = BND4.Read(archives.Read("/chr/c0000.behbnd.dcx"));
        var behavior = bnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!;
        var map = new Dictionary<string, SortedSet<int>>();
        foreach (var selector in new HavokBinarySerializer().ReadAllObjects(new MemoryStream(behavior.Bytes)).OfType<CustomManualSelectorGenerator>())
        {
            var name = selector.m_name ?? "";
            if (!map.TryGetValue(name, out var ids))
                map[name] = ids = [];
            foreach (var clip in selector.m_generators.OfType<hkbClipGenerator>())
                if (ClipId().Match(clip.m_animationName ?? "") is { Success: true } m)
                    ids.Add(int.Parse(m.Groups[1].Value));
        }
        return map;
    }

    /// <summary>Printable ASCII runs of 4+ characters, in file order.</summary>
    private static IEnumerable<string> Strings(byte[] data)
    {
        int start = -1;
        for (int i = 0; i <= data.Length; i++)
        {
            bool printable = i < data.Length && data[i] >= 0x20 && data[i] < 0x7F;
            if (printable && start < 0)
                start = i;
            else if (!printable && start >= 0)
            {
                if (i - start >= 4)
                    yield return Encoding.ASCII.GetString(data, start, i - start);
                start = -1;
            }
        }
    }
}
