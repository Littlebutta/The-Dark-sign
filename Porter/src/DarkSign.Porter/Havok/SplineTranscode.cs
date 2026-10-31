using System.Numerics;
using HKLib.hk2018;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Rebuilds a DS3 spline-compressed animation for Elden Ring's skeleton without decompressing it. Havok's spline data
/// is the same format in both games: per block, a 4-byte mask per track, then each track's position, rotation and
/// scale data, each starting 4-aligned. So each DS3 track's data is copied as it is into the Elden Ring bone's slot.
/// Only the position bounds (or static values) move by the bone's reference pose offset, which is exact. Bones only
/// Elden Ring has get their reference pose as static values. The result is about the size of the DS3 file, where an
/// uncompressed (interleaved) animation is around 8 times bigger: 5,568 of those made a 926 MB binder that hung loading.
/// </summary>
public static class SplineTranscode
{
    private const byte SplineXyz = 0x70, StaticXyz = 0x07, SplineRot = 0xF0, StaticRot = 0x0F;
    private const int RotThreeComp48 = 2;

    /// <param name="ds3TrackOf">For each Elden Ring bone, the DS3 track that animates it, or -1.</param>
    /// <param name="offsets">For each Elden Ring bone, what to add to the DS3 track's translation.</param>
    public static hkaSplineCompressedAnimation Build(HKX.HKASplineCompressedAnimation src, int[] ds3TrackOf, Vector3[] offsets, IReadOnlyList<hkQsTransform> erReference)
    {
        var data = src.GetData();
        int ds3Tracks = src.TransformTrackCount, erTracks = ds3TrackOf.Length;
        var output = new List<byte>();
        var blockOffsets = new List<uint>();
        var floatBlockOffsets = new List<uint>();
        for (int block = 0; block < src.BlockCount; block++)
        {
            int start = src.BlockOffsets.Size > block ? (int)src.BlockOffsets[block].data : SequentialStart(data, ds3Tracks, block);
            var (_, tracks) = ReadBlock(data, start, ds3Tracks);
            int blockStart = output.Count;
            blockOffsets.Add((uint)blockStart);

            var masks = new byte[erTracks][];
            var bodies = new List<byte>[erTracks];
            for (int b = 0; b < erTracks; b++)
            {
                var body = new List<byte>();
                byte[] mask;
                if (ds3TrackOf[b] >= 0)
                {
                    var t = tracks[ds3TrackOf[b]];
                    mask = (byte[])t.Mask.Clone();
                    mask[1] = AppendPosition(body, t, offsets[b]);
                    Pad(body, 4);
                    body.AddRange(t.Rotation);
                    Pad(body, 4);
                    body.AddRange(t.Scale);
                    Pad(body, 4);
                }
                else
                    mask = AppendStatic(body, erReference[b]);
                masks[b] = mask;
                bodies[b] = body;
            }
            foreach (var m in masks)
                output.AddRange(m);
            PadFrom(output, blockStart, 4);
            foreach (var body in bodies)
                output.AddRange(body); // every body is a multiple of 4 bytes, so each track starts 4-aligned
            PadFrom(output, 0, 16);
            floatBlockOffsets.Add((uint)(output.Count - blockStart));
        }

        return new hkaSplineCompressedAnimation
        {
            m_type = hkaAnimation.AnimationType.HK_SPLINE_COMPRESSED_ANIMATION,
            m_duration = src.Duration,
            m_numberOfTransformTracks = erTracks,
            m_numberOfFloatTracks = 0,
            m_numFrames = src.FrameCount,
            m_numBlocks = src.BlockCount,
            m_maxFramesPerBlock = src.FramesPerBlock,
            m_maskAndQuantizationSize = erTracks * 4,
            m_blockDuration = src.BlockDuration,
            m_blockInverseDuration = src.InverseBlockDuration,
            m_frameDuration = src.FrameDuration,
            m_blockOffsets = blockOffsets,
            m_floatBlockOffsets = floatBlockOffsets,
            m_transformOffsets = [],
            m_floatOffsets = [],
            m_data = output,
            m_endian = 0,
        };
    }

    private sealed class Track
    {
        public required byte[] Mask;
        public required byte[] Position, Rotation, Scale;
    }

    /// <summary>Where block <paramref name="block"/> starts when the file has no block offsets (blocks back to back).</summary>
    private static int SequentialStart(byte[] data, int tracks, int block)
    {
        int at = 0;
        for (int i = 0; i < block; i++)
            at = ReadBlock(data, at, tracks).End;
        return at;
    }

    private static (int End, Track[] Tracks) ReadBlock(byte[] data, int start, int count)
    {
        var tracks = new Track[count];
        int at = start + count * 4;
        at = Align(at, 4);
        for (int i = 0; i < count; i++)
        {
            var mask = data.AsSpan(start + i * 4, 4).ToArray();
            byte quant = mask[0], pos = mask[1], rot = mask[2], scale = mask[3];
            int p = at;
            at = Align(SkipVector(data, at, pos, quant & 3), 4);
            int r = at;
            at = Align(SkipRotation(data, at, rot, (quant >> 2) & 0xF), 4);
            int s = at;
            at = Align(SkipVector(data, at, scale, (quant >> 6) & 3), 4);
            tracks[i] = new Track
            {
                Mask = mask,
                Position = data.AsSpan(p, r - p).ToArray(),
                Rotation = data.AsSpan(r, s - r).ToArray(),
                Scale = data.AsSpan(s, at - s).ToArray(),
            };
        }
        return (Align(at, 16), tracks);
    }

    private static int SkipVector(byte[] data, int at, byte flags, int quant)
    {
        if ((flags & SplineXyz) == 0)
            return at + 4 * BitCount(flags & StaticXyz);
        int items = BitConverter.ToInt16(data, at), degree = data[at + 2];
        at = Align(at + 3 + items + degree + 2, 4);
        int splines = 0;
        for (int c = 0; c < 3; c++)
            if ((flags & (0x10 << c)) != 0)
            {
                at += 8;
                splines++;
            }
            else if ((flags & (1 << c)) != 0)
                at += 4;
        return at + (items + 1) * splines * (quant == 0 ? 1 : 2);
    }

    private static int SkipRotation(byte[] data, int at, byte flags, int quant)
    {
        int size = quant switch { 0 => 4, 1 => 5, 2 => 6, 3 => 3, 4 => 2, 5 => 16, _ => throw new InvalidDataException($"rotation quantization {quant}") };
        int align = quant switch { 0 => 4, 2 => 2, 4 => 2, 5 => 4, _ => 1 };
        if ((flags & SplineRot) != 0)
        {
            int items = BitConverter.ToInt16(data, at), degree = data[at + 2];
            return Align(at + 3 + items + degree + 2, align) + (items + 1) * size;
        }
        return (flags & StaticRot) != 0 ? Align(at, align) + size : at;
    }

    /// <summary>Writes the track's position data moved by <paramref name="offset"/>; returns the new position flags.</summary>
    private static byte AppendPosition(List<byte> body, Track t, Vector3 offset)
    {
        byte flags = t.Mask[1];
        var src = t.Position;
        int at = 0;
        if ((flags & SplineXyz) != 0)
        {
            int items = BitConverter.ToInt16(src, 0), degree = src[2];
            int header = Align(3 + items + degree + 2, 4);
            body.AddRange(src.AsSpan(0, header).ToArray());
            at = header;
        }
        byte result = flags;
        for (int c = 0; c < 3; c++)
        {
            float o = c == 0 ? offset.X : c == 1 ? offset.Y : offset.Z;
            if ((flags & (0x10 << c)) != 0)
            {
                AddFloat(body, BitConverter.ToSingle(src, at) + o);
                AddFloat(body, BitConverter.ToSingle(src, at + 4) + o);
                at += 8;
            }
            else if ((flags & (1 << c)) != 0)
            {
                AddFloat(body, BitConverter.ToSingle(src, at) + o);
                at += 4;
            }
            else if (o != 0)
            {
                AddFloat(body, o); // was 0 (absent); now a static value
                result |= (byte)(1 << c);
            }
        }
        body.AddRange(src.AsSpan(at).ToArray()); // the quantized control points, unchanged
        return result;
    }

    /// <summary>Writes a track holding <paramref name="pose"/> still; returns its mask.</summary>
    private static byte[] AppendStatic(List<byte> body, hkQsTransform pose)
    {
        byte pos = 0, scale = 0;
        var t = pose.m_translation;
        foreach (var (v, bit) in new[] { (t.X, 1), (t.Y, 2), (t.Z, 4) })
            if (v != 0)
            {
                AddFloat(body, v);
                pos |= (byte)bit;
            }
        Pad(body, 4);
        var q = Quaternion.Normalize(pose.m_rotation);
        byte rot = 0;
        if (q != Quaternion.Identity)
        {
            body.AddRange(ThreeComp48(q));
            rot = StaticRot;
        }
        Pad(body, 4);
        var s = pose.m_scale;
        foreach (var (v, bit) in new[] { (s.X, 1), (s.Y, 2), (s.Z, 4) })
            if (v != 1)
            {
                AddFloat(body, v);
                scale |= (byte)bit;
            }
        Pad(body, 4);
        return [(byte)(1 | (RotThreeComp48 << 2) | (1 << 6)), pos, rot, scale];
    }

    /// <summary>Havok's 48-bit rotation: the three smallest components in 15 bits each, the largest's index and sign in the top bits.</summary>
    internal static byte[] ThreeComp48(Quaternion q)
    {
        Span<float> c = [q.X, q.Y, q.Z, q.W];
        int largest = 0;
        for (int i = 1; i < 4; i++)
            if (MathF.Abs(c[i]) > MathF.Abs(c[largest]))
                largest = i;
        const float fractal = 0.000043161f;
        Span<ushort> v = stackalloc ushort[3];
        for (int i = 0, k = 0; i < 4; i++)
            if (i != largest)
                v[k++] = (ushort)Math.Clamp((int)MathF.Round(c[i] / fractal) + 16383, 0, 32767);
        v[0] |= (ushort)((largest & 1) << 15);
        v[1] |= (ushort)(((largest >> 1) & 1) << 15);
        if (c[largest] < 0)
            v[2] |= 0x8000;
        var bytes = new byte[6];
        for (int i = 0; i < 3; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), v[i]);
        return bytes;
    }

    private static void AddFloat(List<byte> list, float value) => list.AddRange(BitConverter.GetBytes(value));
    private static void Pad(List<byte> list, int align) => PadFrom(list, 0, align);
    private static void PadFrom(List<byte> list, int origin, int align)
    {
        while ((list.Count - origin) % align != 0)
            list.Add(0);
    }
    private static int Align(int value, int align) => (value + align - 1) / align * align;
    private static int BitCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);

    /// <summary>
    /// Samples a built animation with the same decoder used for DS3 files, for checking against the uncompressed port.
    /// </summary>
    public static List<NewBlendableTransform> Sample(hkaSplineCompressedAnimation a) =>
        SplineCompressedAnimation.ReadSplCmpAnimBytesAndSampleToUncomp(a.m_data.ToArray(), a.m_numberOfTransformTracks, a.m_numBlocks, a.m_numFrames, a.m_maxFramesPerBlock);
}

/// <summary>
/// anim-spline-check [count]: converts DS3 player animations both ways (spline, as port-weapons now writes them, and the
/// old uncompressed way), reads both back with HKLib, and compares every bone on every frame. Also prints how Elden
/// Ring's own animations are laid out, to compare with ours. Read-only; writes Work\reports\explore\anim-spline.txt.
/// </summary>
public static class AnimSplineCheck
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        int count = args.Count > 0 ? int.Parse(args[0]) : 300;
        var report = new System.Text.StringBuilder();
        void Log(string line) { Console.WriteLine(line); report.AppendLine(line); }

        using var ds3Archives = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var erArchives = ctx.OpenArchives(PorterGame.EldenRing);
        var ds3Main = BND4.Read(ds3Archives.Read("/chr/c0000.anibnd.dcx"));
        var erMain = BND4.Read(erArchives.Read("/chr/c0000.anibnd.dcx"));
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(Leaf(ds3Main, "skeleton.hkx")!.Bytes);
        var erSkeleton = AnimPort.ReadErSkeleton(Leaf(erMain, "skeleton.hkx")!.Bytes, Compendium(erMain));

        // Elden Ring's own layout, from a few of its animations.
        var erAnims = BND4.Read(erArchives.Read("/chr/c0000_a9x.anibnd.dcx"));
        var erCompendium = Compendium(erAnims);
        var template = erAnims.Files.First(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("skeleton", StringComparison.OrdinalIgnoreCase));
        foreach (var file in erAnims.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).Take(6))
        {
            var root = (hkRootLevelContainer)AnimPort.ReadEr(file.Bytes, erCompendium);
            var c = AnimPort.Container(root);
            var a = c.m_animations.FirstOrDefault();
            var line = $"ER {ItemText.Leaf(file.Name)} id {file.ID}: {file.Bytes.Length} bytes, {a?.GetType().Name}, {a?.m_numberOfTransformTracks} tracks, {a?.m_numberOfFloatTracks} float tracks, " +
                       $"binding {c.m_bindings.FirstOrDefault()?.m_transformTrackToBoneIndices.Count} track->bone";
            if (a is hkaSplineCompressedAnimation s)
                line += $", {s.m_numFrames} frames, {s.m_numBlocks} blocks of {s.m_maxFramesPerBlock}, maskSize {s.m_maskAndQuantizationSize}, data {s.m_data.Count}, " +
                        $"blockOffsets [{string.Join(",", s.m_blockOffsets.Take(4))}], floatBlockOffsets [{string.Join(",", s.m_floatBlockOffsets.Take(4))}], " +
                        $"transformOffsets {s.m_transformOffsets.Count}, floatOffsets {s.m_floatOffsets.Count}, endian {s.m_endian}, first masks {Convert.ToHexString(s.m_data.Take(16).ToArray())}";
            Log(line);
        }
        Log($"ER skeleton {erSkeleton.Names.Count} bones; template {ItemText.Leaf(template.Name)}");

        int done = 0, failed = 0;
        long splineBytes = 0, interleavedBytes = 0;
        float worstPos = 0, worstRot = 0;
        string worstPosName = "", worstRotName = "";
        foreach (var entry in ds3Archives.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/chr/c0000_a", StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path))
        {
            foreach (var file in BND4.Read(ds3Archives.Read(entry.Path!)).Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
            {
                if (done >= count)
                    break;
                var name = ItemText.Leaf(file.Name);
                try
                {
                    var (spline, _) = AnimPort.Convert(file.Bytes, ds3Skeleton, erSkeleton, template.Bytes, erCompendium, spline: true);
                    var (flat, _) = AnimPort.Convert(file.Bytes, ds3Skeleton, erSkeleton, template.Bytes, erCompendium, spline: false);
                    var s = (hkaSplineCompressedAnimation)AnimPort.Container((hkRootLevelContainer)AnimPort.ReadEr(spline, erCompendium)).m_animations.First()!;
                    var u = (hkaInterleavedUncompressedAnimation)AnimPort.Container((hkRootLevelContainer)AnimPort.ReadEr(flat, erCompendium)).m_animations.First()!;
                    var samples = SplineTranscode.Sample(s);
                    int bones = s.m_numberOfTransformTracks;
                    if (samples.Count != u.m_transforms.Count)
                        throw new InvalidDataException($"{samples.Count} samples vs {u.m_transforms.Count}");
                    for (int i = 0; i < samples.Count; i++)
                    {
                        var want = u.m_transforms[i];
                        var got = samples[i];
                        float pos = Vector3.Distance(got.Translation, new Vector3(want.m_translation.X, want.m_translation.Y, want.m_translation.Z));
                        var q = Quaternion.Normalize(got.Rotation);
                        float dot = MathF.Min(1, MathF.Abs(Quaternion.Dot(q, want.m_rotation)));
                        float rot = 2 * MathF.Acos(dot) * 180 / MathF.PI;
                        if (pos > worstPos) { worstPos = pos; worstPosName = $"{name} bone {erSkeleton.Names[i % bones]} frame {i / bones}"; }
                        if (rot > worstRot) { worstRot = rot; worstRotName = $"{name} bone {erSkeleton.Names[i % bones]} frame {i / bones}"; }
                    }
                    splineBytes += spline.Length;
                    interleavedBytes += flat.Length;
                    done++;
                }
                catch (Exception e)
                {
                    failed++;
                    if (failed <= 10)
                        Log($"FAILED {name}: {e.GetType().Name}: {e.Message}");
                }
            }
            if (done >= count)
                break;
        }
        Log($"checked {done} DS3 animations, {failed} failed");
        Log($"sizes: spline {splineBytes / 1024} KB, uncompressed {interleavedBytes / 1024} KB ({(double)interleavedBytes / Math.Max(1, splineBytes):0.0}x)");
        Log($"worst position difference {worstPos:0.#####} ({worstPosName}); worst rotation difference {worstRot:0.###} degrees ({worstRotName})");
        Directory.CreateDirectory(ctx.WorkPath("reports", "explore"));
        File.WriteAllText(ctx.WorkPath("reports", "explore", "anim-spline.txt"), report.ToString());
        return failed == 0 ? 0 : 1;
    }

    private static BinderFile? Leaf(BND4 bnd, string leaf) =>
        bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(leaf, StringComparison.OrdinalIgnoreCase));

    private static byte[]? Compendium(BND4 bnd) =>
        bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
}
