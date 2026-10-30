using System.Text;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// First step of the DS3 moveset port: a read-only report on both games' player animation files, so the retargeting
/// and event translation can be designed from facts. It lists the c0000 anim binders, counts animations per section
/// (aXXX), counts TAE event types, compares the two player skeletons bone by bone, and decodes one animation from
/// each game to prove the Havok readers work on these files.
/// </summary>
public static class MovesetExplore
{
    public static int Run(PorterContext ctx)
    {
        var reportDir = ctx.WorkPath("reports", "explore", "movesets");
        Directory.CreateDirectory(reportDir);
        List<string>? ds3Bones = null, erBones = null;
        foreach (var game in new[] { PorterGame.DarkSouls3, PorterGame.EldenRing })
        {
            using var archives = ctx.OpenArchives(game);
            var report = new StringBuilder();
            var binders = archives.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/chr/c0000", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Path).ToList();
            report.AppendLine($"# {game.ShortName}: {binders.Count} /chr/c0000* files");
            foreach (var entry in binders)
                report.AppendLine($"{entry.Path}\t{entry.Header.PaddedFileSize}");

            var bones = (List<string>?)null;
            var sampleDone = false;
            var eventTypes = new SortedDictionary<int, int>();
            foreach (var entry in binders.Where(e => e.Path!.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)))
            {
                BND4 bnd;
                try { bnd = BND4.Read(archives.Read(entry.Path!)); }
                catch (Exception e) { report.AppendLine($"\n## {entry.Path}: not a BND4 ({e.Message})"); continue; }
                report.AppendLine($"\n## {entry.Path}: {bnd.Files.Count} files");
                var compendium = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase));
                var sections = new SortedDictionary<string, int>();
                foreach (var file in bnd.Files)
                {
                    var leaf = ItemText.Leaf(file.Name);
                    var ext = Path.GetExtension(leaf).ToLowerInvariant();
                    if (ext == ".hkx" && leaf.StartsWith('a') && leaf.Length > 4)
                        sections[leaf[..4]] = sections.GetValueOrDefault(leaf[..4]) + 1;
                    else
                        report.AppendLine($"  {file.ID}\t{file.Name}\t{file.Bytes.Length}");

                    if (ext == ".tae")
                        CountTae(file.Bytes, leaf, report, eventTypes);
                    else if (bones is null && leaf.Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase))
                        bones = ReadBones(game, file.Bytes, compendium?.Bytes, report);
                    else if (!sampleDone && ext == ".hkx" && leaf.StartsWith("a000_00", StringComparison.OrdinalIgnoreCase))
                        sampleDone = DescribeAnimation(game, leaf, file.Bytes, compendium?.Bytes, report);
                }
                report.AppendLine($"  animations by section: {string.Join(", ", sections.Select(s => $"{s.Key}={s.Value}"))}");
            }
            report.AppendLine($"\n# TAE event types (type=count): {string.Join(", ", eventTypes.Select(t => $"{t.Key}={t.Value}"))}");

            var path = Path.Combine(reportDir, $"{game.ShortName}.txt");
            File.WriteAllText(path, report.ToString());
            Console.WriteLine($"{game.ShortName}: {binders.Count} c0000 files, skeleton {bones?.Count ?? 0} bones, sample animation {(sampleDone ? "decoded" : "NOT decoded")}, {eventTypes.Count} TAE event types -> {path}");
            if (game == PorterGame.DarkSouls3) ds3Bones = bones; else erBones = bones;
        }

        if (ds3Bones is not null && erBones is not null)
        {
            var erSet = erBones.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dsSet = ds3Bones.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shared = ds3Bones.Where(erSet.Contains).ToList();
            var text = new StringBuilder();
            text.AppendLine($"shared {shared.Count}, DS3 only {ds3Bones.Count - shared.Count}, ER only {erBones.Count - shared.Count}");
            text.AppendLine($"\nDS3 only: {string.Join(", ", ds3Bones.Where(b => !erSet.Contains(b)))}");
            text.AppendLine($"\nER only: {string.Join(", ", erBones.Where(b => !dsSet.Contains(b)))}");
            var path = Path.Combine(reportDir, "skeletons.txt");
            File.WriteAllText(path, text.ToString());
            Console.WriteLine($"skeletons: {text.ToString().Split('\n')[0].Trim()} -> {path}");
        }
        return 0;
    }

    private static void CountTae(byte[] bytes, string leaf, StringBuilder report, SortedDictionary<int, int> eventTypes)
    {
        try
        {
            TAE.ValidateEventBank = false;
            var tae = TAE.Read(bytes);
            int events = 0;
            foreach (var anim in tae.Animations)
                foreach (var ev in anim.Events)
                {
                    eventTypes[ev.Type] = eventTypes.GetValueOrDefault(ev.Type) + 1;
                    events++;
                }
            report.AppendLine($"    tae {leaf}: id {tae.ID}, format {tae.Format}, {tae.Animations.Count} animations, {events} events, first anim ids {string.Join(" ", tae.Animations.Take(5).Select(a => a.ID))}");
        }
        catch (Exception e)
        {
            report.AppendLine($"    tae {leaf}: unreadable ({e.GetType().Name}: {e.Message})");
        }
    }

    private static List<string>? ReadBones(PorterGame game, byte[] bytes, byte[]? compendium, StringBuilder report)
    {
        try
        {
            List<string> names;
            if (game == PorterGame.DarkSouls3)
            {
                var hkx = HKX.Read(bytes, HKX.HKXVariation.HKXDS3, false);
                var skeleton = hkx.DataSection.Objects.OfType<HKX.HKASkeleton>().First();
                names = Enumerable.Range(0, (int)skeleton.Bones.Size).Select(i => skeleton.Bones[i].Name.GetString()).ToList();
            }
            else
            {
                var root = (hkRootLevelContainer)ReadEr(bytes, compendium);
                var skeleton = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>().SelectMany(c => c.m_skeletons).First()!;
                names = skeleton.m_bones.Select(b => b.m_name ?? "").ToList();
            }
            report.AppendLine($"    skeleton: {names.Count} bones: {string.Join(", ", names)}");
            return names;
        }
        catch (Exception e)
        {
            report.AppendLine($"    skeleton: unreadable ({e.GetType().Name}: {e.Message})");
            return null;
        }
    }

    private static bool DescribeAnimation(PorterGame game, string leaf, byte[] bytes, byte[]? compendium, StringBuilder report)
    {
        try
        {
            if (game == PorterGame.DarkSouls3)
            {
                var hkx = HKX.Read(bytes, HKX.HKXVariation.HKXDS3, false);
                var anim = hkx.DataSection.Objects.OfType<HKX.HKASplineCompressedAnimation>().FirstOrDefault();
                if (anim is null)
                {
                    report.AppendLine($"    sample {leaf}: objects {string.Join(", ", hkx.DataSection.Objects.Select(o => o.GetType().Name))}");
                    return false;
                }
                var frames = SplineCompressedAnimation.ReadSplCmpAnimBytesAndSampleToUncomp(
                    anim.Data.GetArrayData().Elements.Select(b => b.data).ToArray(), anim.TransformTrackCount, anim.BlockCount, anim.FrameCount, anim.FramesPerBlock);
                report.AppendLine($"    sample {leaf}: spline, {anim.Duration:0.###}s, {anim.FrameCount} frames, {anim.TransformTrackCount} tracks, decoded {frames.Count} transforms");
            }
            else
            {
                var root = (hkRootLevelContainer)ReadEr(bytes, compendium);
                var container = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>().First();
                var anim = container.m_animations.First()!;
                report.AppendLine($"    sample {leaf}: {anim.GetType().Name} ({anim.m_type}), {anim.m_duration:0.###}s, {anim.m_numberOfTransformTracks} tracks, extracted motion {anim.m_extractedMotion?.GetType().Name ?? "none"}");
            }
            return true;
        }
        catch (Exception e)
        {
            report.AppendLine($"    sample {leaf}: unreadable ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }

    private static IHavokObject ReadEr(byte[] bytes, byte[]? compendium)
    {
        var serializer = new HavokBinarySerializer();
        if (compendium is not null)
            serializer.LoadCompendium(new MemoryStream(compendium));
        return serializer.Read(new MemoryStream(bytes));
    }
}
