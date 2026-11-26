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
/// DS3's effect events (TAE type 96: effect, dummy poly) come along where the effect is converted (<see cref="MapSfx"/>'s
/// table): a door's dust, and the fire of a bonfire, which is nothing but its object's animations (0 hidden, 1
/// unlit with effect 1010, 10-103 lit with 1011 on dummy poly 201; Elden Ring's grace AEG099_060 has the same
/// animations with the same events).
/// DS3's sound events (TAE types 128/129/131, sound type 3) become Elden Ring's asset sound events (129, type 14) under
/// the asset's own sound IDs (<see cref="ObjectSounds"/>), whose bank the object port builds after the maps.
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
        /// <summary>The door's sound event (129, type 14) to lay ported sound events out like; null when it has none.</summary>
        public (byte[] Bytes, int Unk04)? SoundLayout;
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
        try { t.SoundLayout = ObjectSounds.Layout(TAE.Read(t.Tae)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { t.Notes.Add($"the template TAE's sound events unreadable ({ex.Message}); objects move silently"); }
        if (t.SoundLayout is null)
            t.Notes.Add("the template TAE has no sound event (129) to copy; objects move silently");
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

    public sealed record Result(byte[] Anibnd, int Animations, int Failed, IReadOnlyList<string> Bones, string Summary)
    {
        public int EffectEvents { get; init; }
        public int SoundEvents { get; init; }
        /// <summary>The object's sounds: DS3 ID -> its new g ID (empty when it plays none or sounds are off).</summary>
        public IReadOnlyDictionary<int, int> Sounds { get; init; } = new Dictionary<int, int>();
    }

    /// <summary>
    /// The animation binder for one ported object. <paramref name="rename"/> renames DS3 names (the object's root,
    /// "o300310", becomes the asset name, as in Elden Ring's skeletons) the same way the model's nodes were renamed.
    /// Throws when the objbnd has no usable skeleton or no animation converts.
    /// </summary>
    public const int EffectEvent = 96;
    /// <summary>
    /// Elden Ring's effect event is 24 bytes (effect, dummy poly, two more values DS3 has too, then zeros); DS3's is
    /// 16 or 24. Written at 24, DS3's bonfire events come out byte for byte as Elden Ring's grace has them.
    /// </summary>
    public static int ErLength(int ds3Length) => Math.Max(ds3Length, 24);

    /// <summary>DS3's TAE of an objbnd (null when it has no animation binder or no TAE).</summary>
    public static TAE? Ds3Tae(BND4 objbnd)
    {
        var anibnd = objbnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase));
        if (anibnd is null)
            return null;
        var inner = BND4.Read(DCX.Is(anibnd.Bytes) ? DCX.Decompress(anibnd.Bytes) : anibnd.Bytes);
        var file = inner.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".tae", StringComparison.OrdinalIgnoreCase));
        return file is null ? null : TAE.Read(file.Bytes);
    }

    /// <summary>The effects an object's animations play (TAE type 96, first value).</summary>
    public static SortedSet<int> Effects(TAE tae)
    {
        var ids = new SortedSet<int>();
        foreach (var e in tae.Animations.SelectMany(a => a.Events).Where(e => e.Type == EffectEvent))
        {
            var bytes = e.GetParameterBytes(tae.BigEndian);
            if (bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) > 0)
                ids.Add(BitConverter.ToInt32(bytes, 0));
        }
        return ids;
    }

    /// <summary>The effects a ported asset's animation binder (file 400 of its asset binder) plays; empty when unreadable.</summary>
    public static SortedSet<int> BinderEffects(byte[] anibnd)
    {
        var file = BND4.Read(anibnd).Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".tae", StringComparison.OrdinalIgnoreCase));
        return file is null ? [] : Effects(TAE.Read(file.Bytes));
    }

    /// <summary>The effects the animations of every slot's DS3 objects play (for port-weapons, which converts them).</summary>
    public static SortedSet<int> WantedEffects(GameArchives ds3, IEnumerable<MapSlots.Slot> slots, List<string>? notes = null)
    {
        var models = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ds3Id in slots.Select(s => s.Ds3).Distinct())
            if (ds3.Contains(MapPorter.MsbPath(ds3Id)))
                models.UnionWith(MSB3.Read(ds3.Read(MapPorter.MsbPath(ds3Id))).Parts.Objects.Select(o => o.ModelName));
        var ids = new SortedSet<int>();
        int animated = 0;
        Parallel.ForEach(models, Parallelism.Options, model =>
        {
            var path = $"/obj/{model.ToLowerInvariant()}.objbnd.dcx";
            try
            {
                if (!ds3.Contains(path) || Ds3Tae(BND4.Read(ds3.Read(path))) is not { } tae)
                    return;
                var mine = Effects(tae);
                lock (ids)
                {
                    animated++;
                    ids.UnionWith(mine);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; }
        });
        notes?.Add($"object animation effects: {animated} of {models.Count} DS3 object models are animated; their animations play {ids.Count} effects (bonfire flames, door dust...)");
        return ids;
    }

    /// <summary>
    /// DS3's effect events of one animation in Elden Ring's layout, for the effects <paramref name="converted"/> names.
    /// </summary>
    public static (List<TAE.Event> Events, int Dropped) EffectEvents(TAE.Animation ds3, bool bigEndian, IReadOnlyDictionary<int, int> converted)
    {
        var events = new List<TAE.Event>();
        int dropped = 0;
        TAE.EventGroup? group = null;
        foreach (var e in ds3.Events.Where(e => e.Type == EffectEvent))
        {
            var bytes = e.GetParameterBytes(bigEndian);
            if (bytes.Length < 16 || !converted.TryGetValue(BitConverter.ToInt32(bytes, 0), out int effect) || effect <= 0)
            {
                dropped++;
                continue;
            }
            var er = new byte[ErLength(bytes.Length)];
            bytes.CopyTo(er, 0);
            BitConverter.GetBytes(effect).CopyTo(er, 0);
            group ??= new TAE.EventGroup(EffectEvent);
            events.Add(new TAE.Event(e.StartTime, e.EndTime, EffectEvent, e.Unk04, er, false) { Group = group });
        }
        return (events, dropped);
    }

    /// <param name="soundIds">Gives the object's DS3 sounds (in ID order) their new IDs; null: the sounds are left out.</param>
    /// <param name="cutsceneBones">For a model DS3's cutscenes move by its bones but that doesn't move in play (it
    /// breaks, it is a ladder, its binder holds no animation, or it has no binder at all): the DS3 model. The result
    /// is then the skeleton alone, with no animation of its own for play to start: DS3's skeleton file when the
    /// object has one, else the model's nodes (<see cref="NodeSkeleton"/>).</param>
    public static Result Build(BND4 objbnd, string assetName, Template t, IReadOnlyDictionary<string, string> rename,
        IReadOnlyDictionary<int, int>? effects = null, Func<IReadOnlyList<int>, IReadOnlyDictionary<int, int>>? soundIds = null,
        FLVER2? cutsceneBones = null)
    {
        bool skeletonOnly = cutsceneBones is not null;
        var anibndFile = objbnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".anibnd", StringComparison.OrdinalIgnoreCase));
        if (anibndFile is null && !skeletonOnly)
            throw new InvalidDataException("the objbnd has no animation binder");
        var ds3Bnd = anibndFile is null ? new BND4() : BND4.Read(DCX.Is(anibndFile.Bytes) ? DCX.Decompress(anibndFile.Bytes) : anibndFile.Bytes);
        var skeletonFile = ds3Bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
        if (skeletonFile is null && !skeletonOnly)
            throw new InvalidDataException("the animation binder has no skeleton.hkx");
        var ds3Skeleton = skeletonFile is not null ? AnimPort.ReadDs3Skeleton(skeletonFile.Bytes) : NodeSkeleton(cutsceneBones!, rename.Keys.FirstOrDefault() ?? assetName);
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
        if (skeletonOnly)
        {
            // Elden Ring's own cutscene-only assets (AEG090_101, 119 bones; AEG237_018) carry exactly this: a binder
            // with one file, hkx\skeleton.hkx, and no TAE, compendium or animation.
            bnd.Files.Add(new BinderFile(flags, 1000000, folder + @"hkx\skeleton.hkx", skeletonBytes));
            return new Result(bnd.Write(), 0, 0, ds3Skeleton.Names,
                $"skeleton of {count} bones for DS3's cutscenes ({(skeletonFile is not null ? "DS3's skeleton file" : "the model's nodes")})");
        }
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
                var (hkx, _, _) = AnimPort.ConvertTimed(file.Bytes, ds3Skeleton, erSkeleton, t.Animation, t.Compendium, true, assetName);
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
        if (converted == 0 && !skeletonOnly)
            throw new InvalidDataException(failed == 0 ? "the animation binder has no animations" : $"no animation converted ({string.Join("; ", errors.Take(2))})");

        // One TAE entry per animation, in the template's format, with DS3's effect and sound events.
        var tae = TAE.Read(t.Tae);
        tae.SibName = $"{upper}.sib";
        tae.Animations = ids.Order().Select(id => new TAE.Animation(id, new TAE.Animation.AnimMiniHeader.Standard(), $"a{id / 1_000_000:D3}_{id % 1_000_000:D6}.hkt")).ToList();
        int effectEvents = 0, effectsDropped = 0, soundEvents = 0, soundsDropped = 0;
        IReadOnlyDictionary<int, int> sounds = new Dictionary<int, int>();
        bool wantSounds = soundIds is not null && t.SoundLayout is not null;
        if (effects is not null || wantSounds)
            try
            {
                var ds3TaeFile = ds3Bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).EndsWith(".tae", StringComparison.OrdinalIgnoreCase));
                if (ds3TaeFile is not null)
                {
                    var ds3Tae = TAE.Read(ds3TaeFile.Bytes);
                    if (wantSounds && ObjectSounds.Ds3Sounds(ds3Tae) is { Count: > 0 } played)
                        sounds = soundIds!([.. played]);
                    foreach (var anim in tae.Animations)
                    {
                        if (ds3Tae.Animations.FirstOrDefault(a => a.ID == anim.ID) is not { } source)
                            continue;
                        if (effects is not null)
                        {
                            var (events, dropped) = EffectEvents(source, ds3Tae.BigEndian, effects);
                            effectsDropped += dropped;
                            if (events.Count > 0)
                            {
                                anim.EventGroups.Add(events[0].Group);
                                anim.Events.AddRange(events);
                                effectEvents += events.Count;
                            }
                        }
                        if (sounds.Count > 0)
                        {
                            var (events, dropped) = ObjectSounds.Events(source, ds3Tae.BigEndian, t.SoundLayout!.Value, sounds);
                            soundsDropped += dropped;
                            if (events.Count > 0)
                            {
                                anim.EventGroups.Add(events[0].Group);
                                anim.Events.AddRange(events);
                                soundEvents += events.Count;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"effect or sound events not read ({ex.Message})");
            }
        bnd.Files.Add(new BinderFile(flags, 3000000, folder + $@"tae\{upper}.tae", tae.Write()));
        if (t.Compendium is not null)
            bnd.Files.Add(new BinderFile(flags, 7000000, folder + $@"hkx_compendium\{upper}.compendium", t.Compendium));
        bnd.Files.Sort((a, b) => a.ID.CompareTo(b.ID));

        var summary = ($"{converted} animations ({string.Join(" ", ids.Order())}), {count} bones") + (failed > 0 ? $", {failed} failed ({errors[0]})" : "")
                      + (effectEvents + effectsDropped > 0 ? $", {effectEvents} effect events" + (effectsDropped > 0 ? $" ({effectsDropped} more name effects that aren't converted)" : "") : "")
                      + (soundEvents + soundsDropped > 0 ? $", {soundEvents} sound events of {sounds.Count} sounds" + (soundsDropped > 0 ? $" ({soundsDropped} more past the bank's {ObjectSounds.MaxPerAsset})" : "") : "");
        return new Result(bnd.Write(), converted, failed, ds3Skeleton.Names, summary) { EffectEvents = effectEvents, SoundEvents = soundEvents, Sounds = sounds };
    }

    /// <summary>
    /// A skeleton from a DS3 model's nodes, for an object that has no skeleton file (its objbnd holds only the model,
    /// and DS3's cutscenes move it by its nodes): bone 0 is the root, named <paramref name="rootName"/> (the node of
    /// that name when the model has one), then every other node in the model's order under its own parent; a node
    /// without a parent hangs on the root. Rest pose = the nodes' own translation, rotation and scale.
    /// </summary>
    public static AnimPort.Ds3Skeleton NodeSkeleton(FLVER2 flver, string rootName)
    {
        int rootNode = flver.Nodes.FindIndex(n => rootName.Equals(n.Name, StringComparison.OrdinalIgnoreCase));
        var names = new List<string> { rootName };
        var pose = new List<SoulsAssetPipeline.Animation.NewBlendableTransform> { rootNode >= 0 ? Local(flver.Nodes[rootNode]) : new() };
        var boneOf = new int[flver.Nodes.Count];
        for (int i = 0; i < flver.Nodes.Count; i++)
        {
            if (i == rootNode)
            {
                boneOf[i] = 0;
                continue;
            }
            boneOf[i] = names.Count;
            names.Add(flver.Nodes[i].Name ?? $"node{i}");
            pose.Add(Local(flver.Nodes[i]));
        }
        var parents = new short[names.Count];
        parents[0] = -1;
        for (int i = 0; i < flver.Nodes.Count; i++)
            if (i != rootNode)
            {
                int parent = flver.Nodes[i].ParentIndex;
                parents[boneOf[i]] = (short)(parent >= 0 && parent < flver.Nodes.Count && parent != i ? boneOf[parent] : 0);
            }
        return new AnimPort.Ds3Skeleton { Names = names, ReferencePose = pose, Parents = [.. parents] };
    }

    private static SoulsAssetPipeline.Animation.NewBlendableTransform Local(FLVER.Node node)
    {
        var m = node.ComputeLocalTransform();
        return System.Numerics.Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation)
            ? new SoulsAssetPipeline.Animation.NewBlendableTransform(translation, scale, rotation)
            : new SoulsAssetPipeline.Animation.NewBlendableTransform(node.Translation, System.Numerics.Vector3.One, System.Numerics.Quaternion.Identity);
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
        var still = new List<string>();
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
                {
                    // A body named like one bone only ("model_EV1" for the bone "EV1", or the other way round) follows
                    // that bone: Elden Ring finds a body's bone by name, so the body takes the bone's.
                    var alike = movingBones.Where(b => b.Length >= 2 && (body.m_name.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(body.m_name, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (body.m_name.Length < 2 || alike.Count != 1)
                    {
                        // The body named after the root (the frame) is meant to stay; any other is worth a note.
                        if (!rename.Values.Contains(body.m_name, StringComparer.OrdinalIgnoreCase))
                            still.Add(body.m_name);
                        continue;
                    }
                    notes.Add($"body {body.m_name} follows the bone {alike[0]} (named alike)");
                    body.m_name = alike[0];
                }
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
        else if (still.Count > 0)
            notes.Add($"bodies that stay put: {string.Join(" ", still.Take(6))}");
        return moving;
    }

    /// <summary>
    /// "object-anim-test &lt;o304822&gt;...": converts DS3 objects' animations as port-maps would (into
    /// Work\scratch\object-anims, not the mod) and reads every converted animation back: its class, length and
    /// track count. Written for the lifts whose DS3 animations are interleaved, which the port refused up to 1.0.53x.
    /// </summary>
    public static int Test(PorterContext ctx, IReadOnlyList<string> objects)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var t = LoadTemplate(er);
        var folder = ctx.WorkPath("scratch", "object-anims");
        Directory.CreateDirectory(folder);
        int failures = 0;
        foreach (var o in objects)
        {
            var model = o.StartsWith('o') ? o : "o" + o;
            var path = $"/obj/{model}.objbnd.dcx";
            if (!ds3.Contains(path))
            {
                Console.WriteLine($"{model}: not in DS3's archives");
                failures++;
                continue;
            }
            var assetName = "AEG999_" + (900 + objects.ToList().IndexOf(o)).ToString("D3");
            try
            {
                var result = Build(BND4.Read(ds3.Read(path)), assetName, t,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [model] = assetName });
                Console.WriteLine($"{model}: {result.Summary}");
                File.WriteAllBytes(Path.Combine(folder, $"{model}.anibnd"), result.Anibnd);
                var bnd = BND4.Read(result.Anibnd);
                foreach (var f in bnd.Files.Where(f => f.ID >= 1_000_000_000))
                {
                    lock (AnimPort.HavokLock)
                    {
                        var root = (hkRootLevelContainer)AnimPort.ReadEr(f.Bytes, t.Compendium);
                        var anim = AnimPort.Container(root).m_animations.First()!;
                        var binding = AnimPort.Container(root).m_bindings.First()!;
                        Console.WriteLine($"  {ItemText.Leaf(f.Name)}: {anim.GetType().Name}, {anim.m_duration:0.###}s, {anim.m_numberOfTransformTracks} tracks, binding {binding.m_transformTrackToBoneIndices.Count} bones, skeleton {binding.m_originalSkeletonName}");
                        if (anim.m_duration <= 0 || anim.m_numberOfTransformTracks == 0)
                            failures++;
                    }
                }
                if (result.Failed > 0)
                    failures++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"{model}: FAILED {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }
        Console.WriteLine($"object-anim-test: {failures} problem(s); binders in {folder}");
        return failures == 0 ? 0 : 1;
    }
}
