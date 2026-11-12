using System.Numerics;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Converts Dark Souls III player animations (Havok 2014, spline-compressed, 110-bone skeleton) into Elden Ring ones
/// (Havok 2018, 150-bone skeleton). The two skeletons share 103 bones by name. Each shared bone takes DS3's rotation
/// carried over relative to its rest pose (<see cref="PlanRetarget"/>) and DS3's scale, and its translation is DS3's
/// moved by the difference between the two skeletons' reference poses, so motion carries over while bone lengths
/// stay Elden Ring's. Bones only Elden Ring has hold their reference pose. Root motion (the reference frame) carries
/// over as it is. The result is spline-compressed (<see cref="SplineTranscode"/>) or, on request, uncompressed.
/// </summary>
public static class AnimPort
{
    public sealed class Ds3Skeleton
    {
        public required List<string> Names;
        public required List<NewBlendableTransform> ReferencePose;
        public List<short> Parents = [];
    }

    public sealed class ErSkeleton
    {
        public required List<string> Names;
        public required List<hkQsTransform> ReferencePose;
        public List<short> Parents = [];
        /// <summary>Bones that keep Elden Ring's reference pose instead of DS3's track (see <see cref="PlanRetarget"/>);
        /// the moveset port sets it for the player, the enemy port leaves it unset.</summary>
        public HashSet<int>? Frozen;
        /// <summary>Per Elden Ring bone, the rotations put before and after DS3's (see <see cref="PlanRetarget"/>):
        /// the ported rotation is Pre, then DS3's, then Post. Null, or identity for a bone, copies DS3's rotation.</summary>
        public Quaternion[]? Pre, Post;
    }

    /// <summary>
    /// DARKSIGN_WEAPON_BONES: unset, every shared bone's rotation is carried over relative to its own rest pose
    /// (<see cref="PlanRetarget"/>); "freeze" keeps Elden Ring's rest pose on the end bones whose rest differs, as
    /// 1.0.225 did; "ds3" copies DS3's rotation into every shared bone, as builds up to 1.0.221 did.
    /// </summary>
    public static readonly string BoneMode = (Environment.GetEnvironmentVariable("DARKSIGN_WEAPON_BONES") ?? "").Trim().ToLowerInvariant();
    private const float RestAngleDegrees = 10f;
    /// <summary>Rest differences under this are noise and leave the DS3 rotation bytes untouched.</summary>
    private const float CorrectAngleDegrees = 0.5f;

    /// <summary>
    /// Decides how each Elden Ring bone takes its rotation from the DS3 track of the same name, and records it on
    /// <paramref name="er"/> (<see cref="ErSkeleton.Pre"/>, <see cref="ErSkeleton.Post"/>, <see cref="ErSkeleton.Frozen"/>).
    /// <para>The game moves a skinned vertex, and a dummy point a weapon hangs on, by the bone's current world
    /// transform times the inverse of its rest (bind) world transform. Copying DS3's bone rotation into Elden Ring's
    /// bone gives the bone DS3's world orientation, which is right only where the two skeletons rest alike. Where
    /// they don't, the part is turned by the rest difference: DS3's L_Weapon rests turned 180° about its X axis
    /// relative to L_Hand while Elden Ring's rests at identity, so every DS3 move showed the left-hand weapon model
    /// (the Ringed Knight pair's second blade, Friede's small scythe) pointing backwards, while Elden Ring's own
    /// moves held it right (1.0.221, 1.0.225).</para>
    /// <para>Carrying the rotation over relative to rest makes the Elden Ring bone move the mesh and dummies exactly
    /// as DS3's bone moves DS3's: with W(b) a bone's rest world rotation and p its parent, the ported local rotation
    /// is Pre · L_ds3 · Post, Pre = W_er(b) · W_ds3(b)⁻¹ and Post = W_ds3(p) · W_er(p)⁻¹ (apply left to right). Where
    /// the two skeletons rest alike both are identity and the rotation is copied unchanged; where only the bone's
    /// own rest differs (L_Weapon) Pre is Elden Ring's rest times the inverse of DS3's. Both are constant per bone,
    /// so spline control points can be rewritten without resampling. A shared bone whose parent is a different bone
    /// in the two skeletons (Elden Ring hangs L_Shield under L_Weapon, DS3 under L_Hand) can't take a single
    /// DS3 track and keeps Elden Ring's rest pose. anim-selftest checks the formula against matrices.</para>
    /// </summary>
    public static void PlanRetarget(Ds3Skeleton ds3, ErSkeleton er)
    {
        er.Frozen = null;
        er.Pre = er.Post = null;
        if (BoneMode == "ds3")
        {
            Console.WriteLine("moveset: DARKSIGN_WEAPON_BONES=ds3: every shared bone copies DS3's rotation");
            return;
        }
        if (BoneMode == "freeze")
        {
            er.Frozen = RestDifferent(ds3, er);
            return;
        }
        if (er.Parents.Count != er.Names.Count || ds3.Parents.Count != ds3.Names.Count)
        {
            Console.WriteLine("moveset: a skeleton has no parent indices; every shared bone copies DS3's rotation");
            return;
        }
        var ds3Index = ds3.Names.Select((n, i) => (n, i)).GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var wDs3 = WorldRest(ds3.Names.Count, ds3.Parents, i => ds3.ReferencePose[i].Rotation);
        var wEr = WorldRest(er.Names.Count, er.Parents, i => er.ReferencePose[i].m_rotation);
        int n = er.Names.Count;
        var pre = new Quaternion[n];
        var post = new Quaternion[n];
        var frozen = new HashSet<int>();
        var corrected = new List<string>();
        var kept = new List<string>();
        for (int b = 0; b < n; b++)
        {
            pre[b] = post[b] = Quaternion.Identity;
            if (!ds3Index.TryGetValue(er.Names[b], out var d))
                continue;
            int p = er.Parents[b], dp = ds3.Parents[d];
            bool sameParent = p < 0 ? dp < 0 : dp >= 0 && er.Names[p].Equals(ds3.Names[dp], StringComparison.OrdinalIgnoreCase);
            if (!sameParent)
            {
                frozen.Add(b);
                kept.Add($"{er.Names[b]} (under {(p < 0 ? "the root" : er.Names[p])} here, {(dp < 0 ? "the root" : ds3.Names[dp])} in DS3)");
                continue;
            }
            var (a, c) = Corrections(wEr[b], wDs3[d], p < 0 ? Quaternion.Identity : wEr[p], dp < 0 ? Quaternion.Identity : wDs3[dp]);
            float degrees = Math.Max(Degrees(a), Degrees(c));
            if (degrees < CorrectAngleDegrees)
                continue;
            pre[b] = a;
            post[b] = c;
            corrected.Add($"{er.Names[b]} {Degrees(Quaternion.Normalize(er.ReferencePose[b].m_rotation), Quaternion.Normalize(ds3.ReferencePose[d].Rotation)):0.#}°");
        }
        er.Pre = pre;
        er.Post = post;
        er.Frozen = frozen;
        Console.WriteLine(corrected.Count == 0
            ? "moveset: every shared bone rests alike in both skeletons; all take DS3's rotation"
            : $"moveset: {corrected.Count} shared bones rest differently in the two skeletons; their DS3 rotation is carried over relative to rest: {string.Join(", ", corrected)} [DARKSIGN_WEAPON_BONES=freeze or ds3 for the old behaviours]");
        if (kept.Count > 0)
            Console.WriteLine($"moveset: {kept.Count} shared bones hang from a different parent in the two skeletons and keep Elden Ring's rest pose: {string.Join(", ", kept)}");
    }

    /// <summary>Pre and Post for one bone (see <see cref="PlanRetarget"/>): its rest world rotation in both skeletons, and its parent's.</summary>
    internal static (Quaternion Pre, Quaternion Post) Corrections(Quaternion erBone, Quaternion ds3Bone, Quaternion erParent, Quaternion ds3Parent) =>
        (Quaternion.Normalize(Quaternion.Concatenate(erBone, Quaternion.Inverse(ds3Bone))),
         Quaternion.Normalize(Quaternion.Concatenate(ds3Parent, Quaternion.Inverse(erParent))));

    /// <summary>The ported rotation of one sample: Pre, then DS3's rotation, then Post (apply left to right).</summary>
    public static Quaternion Apply(Quaternion pre, Quaternion ds3, Quaternion post) =>
        Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Concatenate(pre, ds3), post));

    /// <summary>Each bone's rest world rotation: its local rotation, then its parent's world rotation.</summary>
    public static Quaternion[] WorldRest(int count, IReadOnlyList<short> parents, Func<int, Quaternion> local)
    {
        var world = new Quaternion?[count];
        Quaternion Of(int i)
        {
            if (world[i] is { } w)
                return w;
            var q = Quaternion.Normalize(local(i));
            w = parents[i] >= 0 && parents[i] != i ? Quaternion.Normalize(Quaternion.Concatenate(q, Of(parents[i]))) : q;
            world[i] = w;
            return w;
        }
        return Enumerable.Range(0, count).Select(Of).ToArray();
    }

    public static float Degrees(Quaternion q) => Degrees(q, Quaternion.Identity);
    public static float Degrees(Quaternion a, Quaternion b)
    {
        // Through the chord, not acos of the dot product, which float can't resolve under about 0.05°.
        a = Quaternion.Normalize(a);
        b = Quaternion.Normalize(b);
        float chord = MathF.Min((a - b).Length(), (a + b).Length());
        return 4 * MathF.Asin(MathF.Min(1, chord / 2)) * 180 / MathF.PI;
    }

    /// <summary>
    /// 1.0.225's way (DARKSIGN_WEAPON_BONES=freeze): shared end bones (no children) whose reference-pose rotation
    /// differs between the two skeletons by more than <see cref="RestAngleDegrees"/> keep Elden Ring's reference pose;
    /// every other shared bone copies DS3's rotation. It missed L_Weapon, which has a child (L_Shield) in Elden Ring.
    /// </summary>
    public static HashSet<int> RestDifferent(Ds3Skeleton ds3, ErSkeleton er)
    {
        var result = new HashSet<int>();
        if (er.Parents.Count != er.Names.Count || ds3.Parents.Count != ds3.Names.Count)
            return result;
        var ds3Index = ds3.Names.Select((n, i) => (n, i)).GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var hasChildren = er.Parents.Where(p => p >= 0).Select(p => (int)p).ToHashSet();
        var notes = new List<string>();
        for (int b = 0; b < er.Names.Count; b++)
        {
            if (hasChildren.Contains(b) || !ds3Index.TryGetValue(er.Names[b], out var d))
                continue;
            float degrees = Degrees(er.ReferencePose[b].m_rotation, ds3.ReferencePose[d].Rotation);
            if (degrees <= RestAngleDegrees)
                continue;
            result.Add(b);
            notes.Add($"{er.Names[b]} {degrees:0}°");
        }
        Console.WriteLine(result.Count == 0
            ? "moveset: DARKSIGN_WEAPON_BONES=freeze: every shared end bone rests alike in both skeletons; all take DS3's rotation"
            : $"moveset: DARKSIGN_WEAPON_BONES=freeze: {result.Count} shared end bones keep Elden Ring's pose in ported moves: {string.Join(", ", notes)}");
        return result;
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
            Parents = (int)skeleton.ParentIndices.Size == count ? Enumerable.Range(0, count).Select(i => skeleton.ParentIndices[i].data).ToList() : [],
        };
    }

    public static ErSkeleton ReadErSkeleton(byte[] bytes, byte[]? compendium)
    {
        var root = (hkRootLevelContainer)ReadEr(bytes, compendium);
        var skeleton = Container(root).m_skeletons.First()!;
        return new ErSkeleton { Names = skeleton.m_bones.Select(b => b.m_name ?? "").ToList(), ReferencePose = skeleton.m_referencePose, Parents = skeleton.m_parentIndices };
    }

    /// <summary>
    /// Builds an Elden Ring animation file from a DS3 one. <paramref name="erTemplate"/> is any Elden Ring player
    /// animation file; its container layout is kept and its animation replaced.
    /// </summary>
    /// <summary>Writes spline-compressed animations (see <see cref="SplineTranscode"/>); DARKSIGN_ANIM_INTERLEAVED=1 writes the old uncompressed ones.</summary>
    public static bool Spline = Environment.GetEnvironmentVariable("DARKSIGN_ANIM_INTERLEAVED") != "1";

    /// <summary>Held around every use of HKLib's serializer (see <see cref="Convert(byte[], Ds3Skeleton, ErSkeleton, byte[], byte[], bool)"/>).</summary>
    public static readonly object HavokLock = new();

    public static (byte[] Hkx, string Summary) Convert(byte[] ds3Hkx, Ds3Skeleton ds3, ErSkeleton er, byte[] erTemplate, byte[]? compendium) =>
        Convert(ds3Hkx, ds3, er, erTemplate, compendium, Spline);

    public static (byte[] Hkx, string Summary) Convert(byte[] ds3Hkx, Ds3Skeleton ds3, ErSkeleton er, byte[] erTemplate, byte[]? compendium, bool spline)
    {
        var (hkx, summary, _) = ConvertTimed(ds3Hkx, ds3, er, erTemplate, compendium, spline);
        return (hkx, summary);
    }

    /// <summary><see cref="Convert(byte[], Ds3Skeleton, ErSkeleton, byte[], byte[], bool)"/>, plus the Elden Ring
    /// template animation's own length in seconds (0 when it has none), which the moveset port's timing needs.</summary>
    /// <param name="skeletonName">The skeleton the binding names (an object's asset name); null keeps the template's.</param>
    public static (byte[] Hkx, string Summary, float TemplateDuration) ConvertTimed(byte[] ds3Hkx, Ds3Skeleton ds3, ErSkeleton er, byte[] erTemplate, byte[]? compendium, bool spline,
        string? skeletonName = null)
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
        var frozen = er.Frozen ?? [];
        var pre = er.Pre ?? Enumerable.Repeat(Quaternion.Identity, erBones).ToArray();
        var post = er.Post ?? Enumerable.Repeat(Quaternion.Identity, erBones).ToArray();
        for (int b = 0; b < erBones; b++)
        {
            ds3TrackOf[b] = ds3TrackByName.TryGetValue(er.Names[b], out var t) && !frozen.Contains(b) ? t : -1;
            if (ds3TrackOf[b] < 0)
                continue;
            var erRef = er.ReferencePose[b].m_translation;
            offsets[b] = new Vector3(erRef.X, erRef.Y, erRef.Z) - ds3.ReferencePose[ds3BoneByName[er.Names[b]]].Translation;
            mapped++;
        }

        // Spline files bind only the bones DS3 animates, as Elden Ring's own animations do (most bind 42 of 150);
        // the other bones keep the reference pose.
        var bound = spline ? Enumerable.Range(0, erBones).Where(b => ds3TrackOf[b] >= 0).ToArray() : Enumerable.Range(0, erBones).ToArray();
        hkaAnimation animation = spline
            ? SplineTranscode.Build(source, bound.Select(b => ds3TrackOf[b]).ToArray(), bound.Select(b => offsets[b]).ToArray(), bound.Select(b => er.ReferencePose[b]).ToList(),
                bound.Select(b => pre[b]).ToArray(), bound.Select(b => post[b]).ToArray())
            : Interleaved(source, tracks, frames, ds3TrackOf, offsets, pre, post, er);
        animation.m_annotationTracks = bound.Select(b => new hkaAnnotationTrack { m_trackName = er.Names[b] }).ToList();
        if (refFrame is not null)
            animation.m_extractedMotion = new hkaDefaultAnimatedReferenceFrame
            {
                m_up = refFrame.Up,
                m_forward = refFrame.Forward,
                m_duration = refFrame.Duration,
                m_referenceFrameSamples = Enumerable.Range(0, (int)refFrame.ReferenceFrameSamples.Size).Select(i => refFrame.ReferenceFrameSamples[i].Vector).ToList(),
            };

        // HKLib's serializer keeps type caches in unsynchronised static dictionaries, so reading the template and
        // writing the result happen one thread at a time; the transcode above, the slow part, runs on every core.
        using var output = new MemoryStream();
        float templateDuration;
        lock (HavokLock)
        {
            var root = (hkRootLevelContainer)ReadEr(erTemplate, compendium);
            var container = Container(root);
            templateDuration = container.m_animations.FirstOrDefault()?.m_duration ?? 0;
            container.m_animations.Clear();
            container.m_animations.Add(animation);
            var erBinding = container.m_bindings.First()!;
            erBinding.m_animation = animation;
            erBinding.m_transformTrackToBoneIndices = bound.Select(b => (short)b).ToList();
            erBinding.m_floatTrackToFloatSlotIndices = [];
            erBinding.m_partitionIndices = [];
            if (skeletonName is not null)
                erBinding.m_originalSkeletonName = skeletonName;
            new HavokBinarySerializer().Write(root, output);
        }
        var summary = $"{source.Duration:0.###}s, {frames} frames, {mapped} of {erBones} bones from DS3, root motion {(refFrame is null ? "none" : $"{refFrame.ReferenceFrameSamples.Size} samples")}";
        return (output.ToArray(), summary, templateDuration);
    }

    private static hkaInterleavedUncompressedAnimation Interleaved(HKX.HKASplineCompressedAnimation source, int tracks, int frames, int[] ds3TrackOf, Vector3[] offsets, Quaternion[] pre, Quaternion[] post, ErSkeleton er)
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
                        m_rotation = Apply(pre[b], s.Rotation, post[b]),
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

    /// <summary>
    /// Whether an Elden Ring animation file is an additive clip (a flinch played on top of the current move: enemy
    /// 9000-9499) rather than a whole pose. The two kinds must not stand in for each other: a whole-body move in an
    /// additive slot is added to the pose the character already has.
    /// </summary>
    public static bool IsAdditive(byte[] erHkx, byte[]? compendium)
    {
        lock (HavokLock)
            return Container((hkRootLevelContainer)ReadEr(erHkx, compendium)).m_bindings.FirstOrDefault() is { } binding
                   && binding.m_blendHint != hkaAnimationBinding.BlendHint.NORMAL;
    }

    /// <summary>Whether a DS3 animation file is an additive clip.</summary>
    public static bool Ds3Additive(byte[] ds3Hkx) =>
        HKX.Read(ds3Hkx, HKX.HKXVariation.HKXDS3, false).DataSection.Objects.OfType<HKX.HKAAnimationBinding>().FirstOrDefault() is { } binding
        && binding.BlendHint.ToString() != "NORMAL";

    /// <summary>A DS3 animation file's length in seconds.</summary>
    public static float Duration(byte[] ds3Hkx) =>
        HKX.Read(ds3Hkx, HKX.HKXVariation.HKXDS3, false).DataSection.Objects.OfType<HKX.HKASplineCompressedAnimation>().FirstOrDefault()?.Duration ?? 0;

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
