using System.Text.RegularExpressions;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The moving part of a DS3 object (a door, a lift, a lever, a gate): its animations, as the animation binder an
/// Elden Ring asset carries, and the settings its collision needs to move with them.
///
/// Elden Ring's moving assets are laid out like the door AEG219_000 (read off the game's files): the asset binder
/// holds the model under file ID 200 and an animation binder under 400
/// (...\aeg\AEG219\AEG219_000\anibnd\AEG219_000.anibnd), which holds
/// <list type="bullet">
/// <item>1000000 hkx_compendium\skeleton.hkx: a Havok 2018 skeleton named after the asset, root bone = the asset
/// name, one bone per moving part, named like the model's nodes;</item>
/// <item>3000000 tae\AEG219_000.tae: one entry per animation (format SDT, event bank 36);</item>
/// <item>7000000 hkx_compendium\AEG219_000.compendium: the Havok types of the binder's files;</item>
/// <item>1000000000 + ID hkx_compendium\a000_00000N.hkx: the animations (spline-compressed, bound to the skeleton).</item>
/// </list>
/// DS3's objbnd holds the same pieces under the same file IDs (anibnd 400: skeleton 1000000, tae 3000000, animations
/// 1000000000 + ID) in Havok 2014, and the animation IDs mean the same in both: 0 open/use, 1 opened, 2-3 a lever's
/// way back (DS3's ObjActParam names them, and so do its scripts' "Force Animation Playback"). So the skeleton is
/// DS3's (bones, parents, rest pose) in Elden Ring's skeleton file, every animation goes through the same converter
/// as the weapon moves (<see cref="AnimPort"/>, onto the same skeleton, so nothing is retargeted), and the TAE gets
/// one entry per animation in Elden Ring's format.
///
/// Not ported yet: DS3's TAE events (the door's creak and dust: DS3 object sounds and effects, which Elden Ring's
/// banks don't have), so the objects move silently.
///
/// Collision: Elden Ring's door collision (AEG219_000_L.hkx) has one body per part, named after the bone it follows
/// (the leaf is "Model_AEG219_000_01", a box that turns with the bone of that name); the frame stays put. A moving
/// body has motion type 1, its own motion properties and no motion ID. DS3's object collision is laid out the same
/// way (bodies named after the bones), so the moving bodies take those settings (<see cref="MovingBodies"/>).
/// </summary>
public static partial class ObjectAnims
{
    /// <summary>The vanilla door whose binder layout, skeleton file, TAE and collision settings the ported objects copy.</summary>
    public const string TemplateAsset = "/asset/aeg/aeg219/aeg219_000.geombnd.dcx";
    public const string TemplateCollision = "/asset/aeg/aeg219/aeg219_000_l.geomhkxbnd.dcx";

    public sealed class Template
    {
        public required BND4 Binder;              // the anibnd, for its settings
        public required Binder.FileFlags Flags;
        public required byte[] Skeleton, Tae, Animation;
        public byte[]? Compendium;
        /// <summary>The door leaf's body and motion properties in the vanilla collision; null when it couldn't be read.</summary>
        public hknpPhysicsSystemData.bodyCinfoWithAttachment? MovingBody;
        public hknpMotionProperties? MovingMotion;
        public List<string> Notes = [];
    }

    /// <summary>Reads the template from Elden Ring's archives. Throws when the door's animation binder can't be read.</summary>
    public static Template LoadTemplate(GameArchives er)
    {
        if (!er.Contains(TemplateAsset))
            throw new FileNotFoundException($"Elden Ring has no {TemplateAsset}");
        return TemplateFrom(er.Read(TemplateAsset), er.Contains(TemplateCollision) ? er.Read(TemplateCollision) : null);
    }

    /// <summary>The template from the door's asset binder and (optionally) its collision binder, both without DCX.</summary>
    public static Template TemplateFrom(byte[] geombndBytes, byte[]? hkxbndBytes)
    {
        var geombnd = BND4.Read(geombndBytes);
        var file = geombnd.Files.FirstOrDefault(f => f.ID == 400) ?? throw new InvalidDataException($"{ItemText.Leaf(TemplateAsset)} has no animation binder");
        var anibnd = BND4.Read(DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes);
        byte[] Get(Func<BinderFile, bool> match, string what) =>
            anibnd.Files.FirstOrDefault(match)?.Bytes.ToArray() ?? throw new InvalidDataException($"the template animation binder has no {what}");
        var t = new Template
        {
            Binder = anibnd,
            Flags = file.Flags,
            Skeleton = Get(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase), "skeleton.hkx"),
            Tae = Get(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase), "TAE"),
            Animation = Get(f => AnimLeaf().IsMatch(ItemText.Leaf(f.Name)), "animation"),
            Compendium = anibnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes.ToArray(),
        };
        try
        {
            if (hkxbndBytes is not null)
            {
                var hkxbnd = BND4.Read(hkxbndBytes);
                var hkx = hkxbnd.Files.First(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).Bytes.ToArray();
                var compendium = hkxbnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes.ToArray();
                hkRootLevelContainer root;
                lock (AnimPort.HavokLock)
                    root = CollisionPort.Read(hkx, compendium);
                foreach (var system in root.m_namedVariants.Select(v => v.m_variant).OfType<hknpPhysicsSceneData>().SelectMany(s => s.m_systemDatas))
                {
                    if (system is null || t.MovingBody is not null)
                        continue;
                    foreach (var body in system.m_bodyCinfos)
                        if (body?.m_shape is hknpConvexPolytopeShape && body.m_motionPropertiesId < system.m_motionProperties.Count)
                        {
                            t.MovingBody = body;
                            t.MovingMotion = system.m_motionProperties[body.m_motionPropertiesId];
                            break;
                        }
                }
            }
            if (t.MovingBody is null)
                t.Notes.Add($"{ItemText.Leaf(TemplateCollision)} has no moving body; moving bodies use default settings");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            t.Notes.Add($"{ItemText.Leaf(TemplateCollision)} unreadable ({ex.Message}); moving bodies use default settings");
        }
        return t;
    }

    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimLeaf();

    public sealed record Result(byte[] Anibnd, int Animations, int Failed, IReadOnlyList<string> Bones, string Summary);

    /// <summary>
    /// The animation binder for one ported object. <paramref name="rename"/> renames DS3 names (the object's root,
    /// "o300310", becomes the asset name, as in Elden Ring's skeletons) the same way the model's nodes were renamed.
    /// Throws when the objbnd has no usable skeleton or no animation converts.
    /// </summary>
    public static Result Build(BND4 objbnd, string assetName, Template t, IReadOnlyDictionary<string, string> rename)
    {
        var anibndFile = objbnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidDataException("the objbnd has no animation binder");
        var ds3Bnd = BND4.Read(DCX.Is(anibndFile.Bytes) ? DCX.Decompress(anibndFile.Bytes) : anibndFile.Bytes);
        var skeletonFile = ds3Bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidDataException("the animation binder has no skeleton.hkx");
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(skeletonFile.Bytes);
        if (ds3Skeleton.Names.Count == 0)
            throw new InvalidDataException("the DS3 skeleton has no bones");
        ds3Skeleton.Names = ds3Skeleton.Names.Select(n => rename.GetValueOrDefault(n, n)).ToList();
        int count = ds3Skeleton.Names.Count;
        var parents = ds3Skeleton.Parents.Count == count ? ds3Skeleton.Parents : Enumerable.Range(0, count).Select(i => (short)(i == 0 ? -1 : 0)).ToList();
        var pose = ds3Skeleton.ReferencePose.Select(p => new hkQsTransform
        {
            m_translation = new System.Numerics.Vector4(p.Translation, 0),
            m_rotation = p.Rotation,
            m_scale = new System.Numerics.Vector4(p.Scale, 0),
        }).ToList();

        // Elden Ring's skeleton file with DS3's bones.
        byte[] skeletonBytes;
        lock (AnimPort.HavokLock)
        {
            var root = (hkRootLevelContainer)AnimPort.ReadEr(t.Skeleton, t.Compendium);
            var skeleton = AnimPort.Container(root).m_skeletons.First()!;
            skeleton.m_name = assetName;
            skeleton.m_bones = ds3Skeleton.Names.Select(n => new hkaBone { m_name = n, m_lockTranslation = false }).ToList();
            skeleton.m_parentIndices = [.. parents];
            skeleton.m_referencePose = pose;
            skeleton.m_referenceFloats = [];
            skeleton.m_floatSlots = [];
            skeleton.m_localFrames = [];
            skeleton.m_partitions = [];
            using var output = new MemoryStream();
            new HavokBinarySerializer().Write(root, output);
            skeletonBytes = output.ToArray();
        }
        var erSkeleton = new AnimPort.ErSkeleton { Names = ds3Skeleton.Names, ReferencePose = pose, Parents = [.. parents] };

        var upper = assetName.ToUpperInvariant();
        var folder = $@"N:\GR\data\INTERROOT_win64\asset\aeg\{upper[..6]}\{upper}\";
        var bnd = new BND4
        {
            Version = t.Binder.Version, Format = t.Binder.Format, BigEndian = t.Binder.BigEndian, BitBigEndian = t.Binder.BitBigEndian,
            Unicode = t.Binder.Unicode, Extended = t.Binder.Extended, Unk04 = t.Binder.Unk04, Unk05 = t.Binder.Unk05,
        };
        var flags = t.Binder.Files.FirstOrDefault()?.Flags ?? Binder.FileFlags.Flag1;
        bnd.Files.Add(new BinderFile(flags, 1000000, folder + @"hkx_compendium\skeleton.hkx", skeletonBytes));

        int converted = 0, failed = 0;
        var ids = new List<int>();
        var errors = new List<string>();
        foreach (var file in ds3Bnd.Files)
        {
            var match = AnimLeaf().Match(ItemText.Leaf(file.Name));
            if (!match.Success)
                continue;
            int id = int.Parse(match.Groups[1].Value) * 1_000_000 + int.Parse(match.Groups[2].Value);
            try
            {
                var (hkx, _, _) = AnimPort.ConvertTimed(file.Bytes, ds3Skeleton, erSkeleton, t.Animation, t.Compendium, AnimPort.Spline, assetName);
                bnd.Files.Add(new BinderFile(flags, 1_000_000_000 + id, folder + @"hkx_compendium\" + ItemText.Leaf(file.Name).ToLowerInvariant(), hkx));
                ids.Add(id);
                converted++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                errors.Add($"{ItemText.Leaf(file.Name)}: {ex.Message}");
            }
        }
        if (converted == 0)
            throw new InvalidDataException(failed == 0 ? "the animation binder has no animations" : $"no animation converted ({string.Join("; ", errors.Take(2))})");

        // One TAE entry per animation, in the template's format; DS3's events (sounds, effects) are left out.
        var tae = TAE.Read(t.Tae);
        tae.SibName = $"{upper}.sib";
        tae.Animations = ids.Order().Select(id => new TAE.Animation(id, new TAE.Animation.AnimMiniHeader.Standard(), $"a{id / 1_000_000:D3}_{id % 1_000_000:D6}.hkt")).ToList();
        bnd.Files.Add(new BinderFile(flags, 3000000, folder + $@"tae\{upper}.tae", tae.Write()));
        if (t.Compendium is not null)
            bnd.Files.Add(new BinderFile(flags, 7000000, folder + $@"hkx_compendium\{upper}.compendium", t.Compendium));
        bnd.Files.Sort((a, b) => a.ID.CompareTo(b.ID));

        var summary = $"{converted} animations ({string.Join(" ", ids.Order())}), {count} bones" + (failed > 0 ? $", {failed} failed ({errors[0]})" : "");
        return new Result(bnd.Write(), converted, failed, ds3Skeleton.Names, summary);
    }

    /// <summary>The path the asset binder names its animation binder by (file ID 400).</summary>
    public static string AnibndName(string assetName)
    {
        var upper = assetName.ToUpperInvariant();
        return $@"N:\GR\data\INTERROOT_win64\asset\aeg\{upper[..6]}\{upper}\anibnd\{upper}.anibnd";
    }

    /// <summary>
    /// Makes the bodies named after a moving bone (every bone but the skeleton's root) move with it: the settings of
    /// Elden Ring's door leaf (motion type, quality, flags, its motion properties added to the system), else motion
    /// type 1 and the system's first motion properties. Renames bodies by <paramref name="rename"/> first. Returns how
    /// many bodies move.
    /// </summary>
    public static int MovingBodies(hkRootLevelContainer root, IReadOnlySet<string> movingBones, IReadOnlyDictionary<string, string> rename, Template? t, List<string> notes)
    {
        int moving = 0;
        foreach (var system in root.m_namedVariants.Select(v => v.m_variant).OfType<hknpPhysicsSceneData>().SelectMany(s => s.m_systemDatas))
        {
            if (system is null)
                continue;
            int motionId = -1;
            foreach (var body in system.m_bodyCinfos)
            {
                if (body?.m_name is null)
                    continue;
                body.m_name = rename.GetValueOrDefault(body.m_name, body.m_name);
                if (!movingBones.Contains(body.m_name))
                    continue;
                if (motionId < 0)
                {
                    if (t?.MovingMotion is { } motion)
                    {
                        system.m_motionProperties.Add(motion);
                        motionId = system.m_motionProperties.Count - 1;
                    }
                    else if (system.m_motionProperties.Count > 0)
                        motionId = 0;
                    else
                    {
                        system.m_motionProperties.Add(new hknpMotionProperties { m_gravityFactor = 1, m_timeFactor = 1, m_maxLinearSpeed = 200, m_maxAngularSpeed = 100 });
                        motionId = 0;
                    }
                }
                var m = t?.MovingBody;
                body.m_motionType = m?.m_motionType ?? 1;
                body.m_qualityId = m?.m_qualityId ?? 255;
                body.m_flags = m?.m_flags ?? 0;
                body.m_collisionFilterInfo = m?.m_collisionFilterInfo ?? 0;
                body.m_collisionCntrl = m?.m_collisionCntrl ?? 0;
                body.m_mass = m?.m_mass ?? 0;
                body.m_motionId = 0x7FFFFFFF;
                body.m_motionPropertiesId = (ushort)motionId;
                moving++;
            }
        }
        if (moving == 0 && movingBones.Count > 0)
            notes.Add("no collision body is named after a moving bone: the collision stays where the object was placed");
        return moving;
    }
}
