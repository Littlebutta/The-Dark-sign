using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Animated effect models (fx17): a DS3 effect's Model action can name an anibnd with the model's own number (Demon's
/// Scar's art: model and anibnd 8290, animation 10). DS3's sXXXXX.anibnd holds sXXXXX_skeleton.hkx (ID 1000000) and
/// sXXXXX_a000_0000NN.hkx (ID 1000000000 + NN), Havok 2014 spline animations. Elden Ring keeps the same layout and
/// numbering in its common effect binders (s88880.anibnd next to s88880.flver; its RichModel action names anibnd
/// 88880, animation 0), as Havok 2018 tagfiles. The conversion takes an Elden Ring effect anibnd as the template:
/// its skeleton file gets DS3's bones (in DS3's order, so nothing needs remapping), and each DS3 animation goes
/// through AnimPort onto its animation file. Up to 1.0.378 such effects were left out ("uses animated models").
/// DARKSIGN_FX_ANIMATED_MODELS=0 leaves them out again.
/// </summary>
public static class EffectAnimPort
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DARKSIGN_FX_ANIMATED_MODELS") != "0";

    /// <summary>The Elden Ring effect anibnd used as the template: its skeleton and first animation file.</summary>
    public sealed record Template(string Folder, string Stem, BinderFile Skeleton, BinderFile Animation, Binder.FileFlags Flags, BND4 Header);

    public static Template? FindTemplate(IEnumerable<BinderFile> erAnibnds)
    {
        foreach (var file in erAnibnds)
        {
            try
            {
                var bytes = DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes).ToArray() : file.Bytes;
                if (!BND4.Is(bytes))
                    continue;
                var bnd = BND4.Read(bytes);
                var skeleton = bnd.Files.FirstOrDefault(f => f.Name.Contains("_skeleton", StringComparison.OrdinalIgnoreCase));
                var anim = bnd.Files.Where(f => f != skeleton && f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.ID).FirstOrDefault();
                if (skeleton is null || anim is null)
                    continue;
                var leaf = ItemText.Leaf(skeleton.Name);
                var stem = leaf[..leaf.IndexOf("_skeleton", StringComparison.OrdinalIgnoreCase)];
                return new Template(skeleton.Name[..^leaf.Length], stem, skeleton, anim, skeleton.Flags, bnd);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        return null;
    }

    /// <summary>Converts DS3 effect anibnd sOLD to Elden Ring's format as sNEW. Notes say what came across.</summary>
    public static byte[] Convert(byte[] ds3Anibnd, int newId, Template template, out string note)
    {
        var bytes = DCX.Is(ds3Anibnd) ? DCX.Decompress(ds3Anibnd).ToArray() : ds3Anibnd;
        var ds3 = BND4.Read(bytes);
        var ds3SkeletonFile = ds3.Files.FirstOrDefault(f => f.Name.Contains("_skeleton", StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidDataException("the DS3 anibnd has no skeleton");
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(ds3SkeletonFile.Bytes);
        string newStem = $"s{newId:D5}";

        byte[] skeletonBytes;
        AnimPort.ErSkeleton erSkeleton;
        lock (AnimPort.HavokLock)
        {
            var root = (hkRootLevelContainer)AnimPort.ReadEr(template.Skeleton.Bytes, null);
            var skeleton = AnimPort.Container(root).m_skeletons.First()!;
            // The template's own bones go: DS3's are added in DS3's order, so DS3 bone i is bone i here too.
            skeleton.m_bones.Clear();
            skeleton.m_parentIndices.Clear();
            skeleton.m_referencePose.Clear();
            skeleton.m_referenceFloats.Clear();
            skeleton.m_floatSlots.Clear();
            skeleton.m_localFrames.Clear();
            skeleton.m_partitions.Clear();
            skeleton.m_name = newStem;
            Enemies.ChrPort.MergeSkeleton(skeleton, ds3Skeleton, addBones: true);
            using var output = new MemoryStream();
            new HavokBinarySerializer().Write(root, output);
            skeletonBytes = output.ToArray();
            erSkeleton = new AnimPort.ErSkeleton { Names = skeleton.m_bones.Select(b => b.m_name ?? "").ToList(), ReferencePose = skeleton.m_referencePose, Parents = skeleton.m_parentIndices };
        }

        var h = template.Header; // the binder header of Elden Ring's own effect anibnd
        // The template's animation binding names its skeleton ("s88730"); the converted files name the new one, and sit
        // in its own folder (sfx\hkx\s900023\), as Elden Ring's do.
        byte[] animTemplate;
        lock (AnimPort.HavokLock)
        {
            var root = (hkRootLevelContainer)AnimPort.ReadEr(template.Animation.Bytes, null);
            foreach (var binding in AnimPort.Container(root).m_bindings.Where(b => b is not null))
                binding!.m_originalSkeletonName = newStem;
            using var output = new MemoryStream();
            new HavokBinarySerializer().Write(root, output);
            animTemplate = output.ToArray();
        }
        var folder = template.Folder.Replace(template.Stem, newStem, StringComparison.OrdinalIgnoreCase);

        var result = new BND4
        {
            Format = h.Format, Version = h.Version, Unicode = h.Unicode, Extended = h.Extended,
            Unk04 = h.Unk04, Unk05 = h.Unk05, BigEndian = h.BigEndian, BitBigEndian = h.BitBigEndian,
        };
        result.Files.Add(new BinderFile(template.Flags, 1000000, $"{folder}{newStem}_skeleton.hkx", skeletonBytes));
        int animations = 0;
        var failed = new List<string>();
        foreach (var f in ds3.Files.Where(f => f != ds3SkeletonFile && f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.ID))
        {
            var leaf = ItemText.Leaf(f.Name);
            int a = leaf.IndexOf("_a", StringComparison.OrdinalIgnoreCase);
            var suffix = a >= 0 ? leaf[a..] : $"_a000_{f.ID % 1000000:D6}.hkx";
            try
            {
                var (hkx, _, _) = AnimPort.ConvertTimed(f.Bytes, ds3Skeleton, erSkeleton, animTemplate, null, AnimPort.Spline);
                result.Files.Add(new BinderFile(template.Flags, f.ID, $"{folder}{newStem}{suffix}", hkx));
                animations++;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failed.Add($"{leaf}: {e.GetType().Name}: {e.Message}");
            }
        }
        if (animations == 0)
            throw new InvalidDataException("no animation converted" + (failed.Count > 0 ? $" ({string.Join("; ", failed)})" : ""));
        note = $"{ds3Skeleton.Names.Count} bones, {animations} animations" + (failed.Count > 0 ? $", {failed.Count} failed ({string.Join("; ", failed)})" : "");
        return result.Write();
    }
}
