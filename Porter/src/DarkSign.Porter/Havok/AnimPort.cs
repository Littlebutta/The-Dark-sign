using System.Numerics;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Converts Dark Souls III player animations (Havok 2014, spline-compressed, 110-bone skeleton) into Elden Ring ones
/// (Havok 2018, 150-bone skeleton). The two skeletons share 103 bones by name. Each shared bone keeps DS3's rotation
/// and scale, and its translation is DS3's moved by the difference between the two skeletons' reference poses, so
/// motion carries over while bone lengths stay Elden Ring's. Bones only Elden Ring has hold their reference pose.
/// Root motion (the reference frame) carries over as it is. The result is written uncompressed (interleaved).
/// </summary>
public static class AnimPort
{
    public sealed class Ds3Skeleton
    {
        public required List<string> Names;
        public required List<NewBlendableTransform> ReferencePose;
    }

    public sealed class ErSkeleton
    {
        public required List<string> Names;
        public required List<hkQsTransform> ReferencePose;
    }

    public static Ds3Skeleton ReadDs3Skeleton(byte[] bytes)
    {
        var hkx = HKX.Read(bytes, HKX.HKXVariation.HKXDS3, false);
        var skeleton = hkx.DataSection.Objects.OfType<HKX.HKASkeleton>().First();
        int count = (int)skeleton.Bones.Size;
        return new Ds3Skeleton
        {
            Names = Enumerable.Range(0, count).Select(i => skeleton.Bones[i].Name.GetString()).ToList(),
            ReferencePose = Enumerable.Range(0, count).Select(i => NewBlendableTransform.FromHKXTransform(skeleton.Transforms[i])).ToList(),
        };
    }

    public static ErSkeleton ReadErSkeleton(byte[] bytes, byte[]? compendium)
    {
        var root = (hkRootLevelContainer)ReadEr(bytes, compendium);
        var skeleton = Container(root).m_skeletons.First()!;
        return new ErSkeleton { Names = skeleton.m_bones.Select(b => b.m_name ?? "").ToList(), ReferencePose = skeleton.m_referencePose };
    }

    /// <summary>
    /// Builds an Elden Ring animation file from a DS3 one. <paramref name="erTemplate"/> is any Elden Ring player
    /// animation file; its container layout is kept and its animation replaced.
    /// </summary>
    /// <summary>Writes spline-compressed animations (see <see cref="SplineTranscode"/>); DARKSIGN_ANIM_INTERLEAVED=1 writes the old uncompressed ones.</summary>
    public static bool Spline = Environment.GetEnvironmentVariable("DARKSIGN_ANIM_INTERLEAVED") != "1";

    public static (byte[] Hkx, string Summary) Convert(byte[] ds3Hkx, Ds3Skeleton ds3, ErSkeleton er, byte[] erTemplate, byte[]? compendium) =>
        Convert(ds3Hkx, ds3, er, erTemplate, compendium, Spline);

    public static (byte[] Hkx, string Summary) Convert(byte[] ds3Hkx, Ds3Skeleton ds3, ErSkeleton er, byte[] erTemplate, byte[]? compendium, bool spline)
    {
        var hkx = HKX.Read(ds3Hkx, HKX.HKXVariation.HKXDS3, false);
        var source = hkx.DataSection.Objects.OfType<HKX.HKASplineCompressedAnimation>().FirstOrDefault()
            ?? throw new InvalidDataException("the DS3 file has no spline-compressed animation");
        var binding = hkx.DataSection.Objects.OfType<HKX.HKAAnimationBinding>().FirstOrDefault();
        var refFrame = hkx.DataSection.Objects.OfType<HKX.HKADefaultAnimatedReferenceFrame>().FirstOrDefault();

        int tracks = source.TransformTrackCount, frames = source.FrameCount;

        // DS3 track -> DS3 bone (an empty binding means track i is bone i).
        var trackBone = binding is not null && binding.TransformTrackToBoneIndices.Size > 0
            ? Enumerable.Range(0, tracks).Select(t => (int)binding.TransformTrackToBoneIndices[t].data).ToArray()
            : Enumerable.Range(0, tracks).ToArray();
        var ds3TrackByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int t = 0; t < tracks; t++)
            if (trackBone[t] >= 0 && trackBone[t] < ds3.Names.Count)
                ds3TrackByName.TryAdd(ds3.Names[trackBone[t]], t);
        var ds3BoneByName = ds3.Names.Select((n, i) => (n, i)).GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);

        int erBones = er.Names.Count, mapped = 0;
        var ds3TrackOf = new int[erBones];
        var offsets = new Vector3[erBones];
        for (int b = 0; b < erBones; b++)
        {
            ds3TrackOf[b] = ds3TrackByName.TryGetValue(er.Names[b], out var t) ? t : -1;
            if (ds3TrackOf[b] < 0)
                continue;
            var erRef = er.ReferencePose[b].m_translation;
            offsets[b] = new Vector3(erRef.X, erRef.Y, erRef.Z) - ds3.ReferencePose[ds3BoneByName[er.Names[b]]].Translation;
            mapped++;
        }

        hkaAnimation animation = spline
            ? SplineTranscode.Build(source, ds3TrackOf, offsets, er.ReferencePose)
            : Interleaved(source, tracks, frames, ds3TrackOf, offsets, er);
        animation.m_annotationTracks = er.Names.Select(n => new hkaAnnotationTrack { m_trackName = n }).ToList();
        if (refFrame is not null)
            animation.m_extractedMotion = new hkaDefaultAnimatedReferenceFrame
            {
                m_up = refFrame.Up,
                m_forward = refFrame.Forward,
                m_duration = refFrame.Duration,
                m_referenceFrameSamples = Enumerable.Range(0, (int)refFrame.ReferenceFrameSamples.Size).Select(i => refFrame.ReferenceFrameSamples[i].Vector).ToList(),
            };

        var root = (hkRootLevelContainer)ReadEr(erTemplate, compendium);
        var container = Container(root);
        container.m_animations.Clear();
        container.m_animations.Add(animation);
        var erBinding = container.m_bindings.First()!;
        erBinding.m_animation = animation;
        erBinding.m_transformTrackToBoneIndices = Enumerable.Range(0, erBones).Select(i => (short)i).ToList();
        erBinding.m_floatTrackToFloatSlotIndices = [];
        erBinding.m_partitionIndices = [];

        using var output = new MemoryStream();
        new HavokBinarySerializer().Write(root, output);
        var summary = $"{source.Duration:0.###}s, {frames} frames, {mapped} of {erBones} bones from DS3, root motion {(refFrame is null ? "none" : $"{refFrame.ReferenceFrameSamples.Size} samples")}";
        return (output.ToArray(), summary);
    }

    private static hkaInterleavedUncompressedAnimation Interleaved(HKX.HKASplineCompressedAnimation source, int tracks, int frames, int[] ds3TrackOf, Vector3[] offsets, ErSkeleton er)
    {
        var samples = SplineCompressedAnimation.ReadSplCmpAnimBytesAndSampleToUncomp(source.GetData(), tracks, source.BlockCount, frames, source.FramesPerBlock);
        int erBones = er.Names.Count;
        var transforms = new List<hkQsTransform>(frames * erBones);
        for (int f = 0; f < frames; f++)
            for (int b = 0; b < erBones; b++)
            {
                var erRef = er.ReferencePose[b];
                if (ds3TrackOf[b] is var t and >= 0)
                {
                    var s = samples[f * tracks + t];
                    transforms.Add(new hkQsTransform
                    {
                        m_translation = new Vector4(s.Translation + offsets[b], 0),
                        m_rotation = Quaternion.Normalize(s.Rotation),
                        m_scale = new Vector4(s.Scale, 0),
                    });
                }
                else
                    transforms.Add(new hkQsTransform { m_translation = erRef.m_translation, m_rotation = erRef.m_rotation, m_scale = erRef.m_scale });
            }
        return new hkaInterleavedUncompressedAnimation
        {
            m_type = hkaAnimation.AnimationType.HK_INTERLEAVED_ANIMATION,
            m_duration = source.Duration,
            m_numberOfTransformTracks = erBones,
            m_numberOfFloatTracks = 0,
            m_transforms = transforms,
        };
    }

    internal static hkaAnimationContainer Container(hkRootLevelContainer root) =>
        root.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>().First();

    internal static IHavokObject ReadEr(byte[] bytes, byte[]? compendium)
    {
        var serializer = new HavokBinarySerializer();
        if (compendium is not null)
            serializer.LoadCompendium(new MemoryStream(compendium));
        return serializer.Read(new MemoryStream(bytes));
    }
}

/// <summary>
/// anim-test: puts one converted DS3 animation in place of one Elden Ring animation, in the mod folder only
/// (Mods\ER\chr\c0000_aXX.anibnd.dcx), so the retargeting can be judged in game. "anim-test undo" removes it.
/// </summary>
public static class AnimTest
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        // The binder anim-test wrote is recorded here, so undo removes only that (port-weapons writes others).
        var marker = ctx.ModPath("chr", "anim-test.txt");
        if (args.Count == 1 && args[0].Equals("undo", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(marker))
            {
                foreach (var file in File.ReadAllLines(marker).Where(File.Exists))
                {
                    File.Delete(file);
                    Console.WriteLine($"removed {file} (run port-weapons again if it held ported movesets)");
                }
                File.Delete(marker);
            }
            return 0;
        }
        if (args.Count is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: anim-test <DS3 animation, e.g. a030_030000> [Elden Ring animation to replace, default the same]  |  anim-test undo");
            return 1;
        }
        string ds3Name = Hkx(args[0]), erName = Hkx(args.Count == 2 ? args[1] : args[0]);

        using var ds3Archives = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var erArchives = ctx.OpenArchives(PorterGame.EldenRing);
        var ds3Main = BND4.Read(ds3Archives.Read("/chr/c0000.anibnd.dcx"));
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(FileNamed(ds3Main, "skeleton.hkx")!.Bytes);
        var erMain = BND4.Read(erArchives.Read("/chr/c0000.anibnd.dcx"));
        var erSkeleton = AnimPort.ReadErSkeleton(FileNamed(erMain, "skeleton.hkx")!.Bytes, Compendium(erMain));

        var (_, _, ds3File) = Find(ds3Archives, ds3Name) ?? throw new FileNotFoundException($"DS3 has no player animation {ds3Name}");
        var (erPath, erBnd, erFile) = Find(erArchives, erName) ?? throw new FileNotFoundException($"Elden Ring has no player animation {erName}");

        var (bytes, summary) = AnimPort.Convert(ds3File.Bytes, ds3Skeleton, erSkeleton, erFile.Bytes, Compendium(erBnd));
        // Read it back the way the game will, to catch a file HKLib can't round-trip.
        var check = AnimPort.Container((hkRootLevelContainer)AnimPort.ReadEr(bytes, Compendium(erBnd))).m_animations.First()!;
        erFile.Bytes = bytes;

        var compression = erArchives.ReadWithCompression(erPath).Compression;
        var output = ctx.ModPath("chr", ItemText.Leaf(erPath));
        File.WriteAllBytes(output, DCX.Compress(erBnd.Write(), compression));
        File.AppendAllLines(marker, [output]);
        Console.WriteLine($"{ds3Name} -> {erName}: {summary}; read back as {check.GetType().Name}, {check.m_numberOfTransformTracks} tracks");
        Console.WriteLine($"wrote {output} (remove with: anim-test undo)");
        return 0;
    }

    private static string Hkx(string name) => name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase) ? name : name + ".hkx";

    private static BinderFile? FileNamed(BND4 bnd, string leaf) =>
        bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(leaf, StringComparison.OrdinalIgnoreCase));

    private static byte[]? Compendium(BND4 bnd) =>
        bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;

    /// <summary>The c0000 anim binder holding an animation, the binder, and the file.</summary>
    private static (string Path, BND4 Bnd, BinderFile File)? Find(GameArchives archives, string leaf)
    {
        foreach (var entry in archives.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/chr/c0000", StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path))
        {
            var bnd = BND4.Read(archives.Read(entry.Path!));
            if (FileNamed(bnd, leaf) is { } file)
                return (entry.Path!, bnd, file);
        }
        return null;
    }
}
