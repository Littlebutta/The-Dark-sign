using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Converts Dark Souls III cutscenes (remo binders) into Elden Ring cutscene binders, so the boss intros and other
/// cutscenes the ported maps' scripts play run in Elden Ring. Every DS3 cutscene of every ported map is converted
/// (RULE 1), not a chosen few.
///
/// What the two games share, read from both games' files with <c>cutscene-survey</c> on 2026-11-12: the same
/// binder (BND4 "07D7R6", file IDs cut*100000 for each cut's Havok file and 100000000 for the timeline, names
/// "...\{cutscene}\cutNNNN\a0000.hkx" and "...\{cutscene}\{cutscene}.mqb"), the same MQB timeline version (0xCC,
/// which SoulsFormats round-trips byte for byte in both games), cutscene IDs AABBNNNN for file sAA_BB_NNNN, and
/// one Havok animation per animated thing in each cut (skeleton, animation and binding named after the map part,
/// camera "camNNNN" or cutscene dummy "dNNNN_NNNN" it moves). What differs:
/// <list type="bullet">
/// <item>Paths: DS3's live under N:\FDP\...\remo\, Elden Ring's under N:\GR\...\cutscene\.</item>
/// <item>Havok: DS3 writes 2014 packfiles, Elden Ring 2018 tagfiles. The spline-compressed animation data is the same
/// format in both (see <see cref="SplineTranscode"/>), so each animation's data, float tracks (camera field of view)
/// included, is copied as it is into a 2018 file; skeletons and bindings are rebuilt from DS3's.</item>
/// <item>The player: DS3's 110-bone skeleton becomes Elden Ring's (141 bones in its cutscenes) by the moveset port's
/// retarget (<see cref="AnimPort.PlanRetarget"/>), with Elden Ring's cutscene player skeleton.</item>
/// <item>Map parts: ported enemies and objects get new part names (model_9000 and up), so every name the cutscene
/// uses for one (skeleton, binding, MQB resource) is renamed with the map port's DS3 -> Elden Ring part names.</item>
/// <item>Timeline events: each MQB resource has a kind (Unk48) and its events have named, typed parameters. The
/// kinds and parameters Elden Ring's own cutscenes use are read from them (<see cref="Catalog"/>); a DS3 resource
/// of a kind Elden Ring never uses, or an event with a parameter Elden Ring doesn't have for that kind, is left
/// out, so the game is never handed an event shape it doesn't know. The map in part timelines (areaNo, blockNo)
/// moves to the slot.</item>
/// <item>Elden Ring needs a CutsceneMapIdParam row per cutscene (the map it is placed against): one is added.</item>
/// </list>
/// Cutscene IDs move with the map: DS3 sAA_BB_NNNN becomes the slot's sCC_DD_NNNN (s45_00_0000, the Painted
/// World's Friede intro, becomes s19_70_0000 by default). Not ported: DS3 sound (FMOD banks) and DS3 effect, light
/// and gparam IDs inside events, which point at DS3 data Elden Ring doesn't have.
///
/// Credits: the Elden Ring cutscene binder layout and the use of SoulsFormats' MQB class follow JortPob's
/// Cutscener.cs (infernoplus/JortPob, Morrowind to Elden Ring); remo part naming (root skeleton per map part)
/// follows soulstruct-havok's remobnd.py (Grimrukh) and DSAnimStudio's RemoManager (Meowmaritus); MQB reading and
/// writing is SoulsFormats (JKAnderson, SoulsFormatsNEXT); CutsceneMapIdParam fields from Paramdex (soulsmods).
/// </summary>
public static partial class CutscenePort
{
    /// <summary>DARKSIGN_CUTSCENES=0 (launcher fallback "map-cutscenes" B): no cutscenes are converted, and the scripts
    /// leave cutscene lines out (a warping one keeps its warp), as before the cutscene port.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_CUTSCENES") != "0";

    /// <summary>"/remo/s45_00_0010.remobnd.dcx": where DS3 keeps a cutscene.</summary>
    public static string Ds3Path(string name) => $"/remo/{name}.remobnd.dcx";

    /// <summary>"/cutscene/s19_70_0010.cutscenebnd.dcx": where Elden Ring keeps one.</summary>
    public static string ErPath(string name) => $"/cutscene/{name}.cutscenebnd.dcx";

    /// <summary>"s45_00_0010" for cutscene ID 45000010: both games name a cutscene sAA_BB_NNNN after its ID AABBNNNN.</summary>
    public static string Name(int id) => $"s{id / 1_000_000:D2}_{id / 10_000 % 100:D2}_{id % 10_000:D4}";

    /// <summary>The cutscene ID of a name like "s45_00_0010", or null.</summary>
    public static int? IdOf(string name)
    {
        var m = NameRegex().Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value) * 1_000_000 + int.Parse(m.Groups[2].Value) * 10_000 + int.Parse(m.Groups[3].Value) : null;
    }

    /// <summary>The Elden Ring ID of a DS3 cutscene on the slot's map: area and block become the slot's, the number stays.</summary>
    public static int ErId(int ds3Id, MapId er) => er.Area * 1_000_000 + er.Block * 10_000 + ds3Id % 10_000;

    /// <summary>The packed map ID Elden Ring's params and scripts use: 19700000 for m19_70_00_00.</summary>
    public static int PackedMap(MapId id) => id.Area * 1_000_000 + id.Block * 10_000 + id.Region * 100 + id.Index;

    [GeneratedRegex(@"s(\d\d)_(\d\d)_(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>The ER cutscene the Havok template (and the camera's float slots) come from: a small one (JortPob's choice).</summary>
    public const string TemplateCutscene = "s10_00_0050";

    /// <summary>An ER cutscene with the player in it, for Elden Ring's cutscene player skeleton.</summary>
    public const string PlayerCutscene = "s10_00_0010";

    public const string PlayerPart = "c0000_0000";

    public const string ParamName = "CutsceneMapIdParam";

    /// <summary>The CutsceneMapIdParam row added rows copy: Elden Ring's s10_00_0050, which names no hit parts.</summary>
    public const int TemplateRow = 10000050;

    // ---------------------------------------------------------------- what Elden Ring's cutscenes use

    /// <summary>
    /// The MQB resource kinds (Unk48) Elden Ring's own cutscenes use, and for each the parameters (name and type)
    /// of its resources, timelines and events. Read from the game's own cutscenes, so it is what this game version
    /// understands.
    /// </summary>
    public sealed class Catalog
    {
        public Dictionary<int, Dictionary<string, MQB.Parameter.DataType>> Resources { get; } = new();
        public Dictionary<int, Dictionary<string, MQB.Parameter.DataType>> Timelines { get; } = new();
        public Dictionary<int, Dictionary<string, MQB.Parameter.DataType>> Events { get; } = new();
        public HashSet<int> Kinds { get; } = [];
        public int Files { get; set; }

        private static void Note(Dictionary<int, Dictionary<string, MQB.Parameter.DataType>> into, int kind, IEnumerable<MQB.Parameter> ps)
        {
            var d = into.TryGetValue(kind, out var x) ? x : into[kind] = new(StringComparer.Ordinal);
            foreach (var p in ps)
                d.TryAdd(p.Name, p.Type);
        }

        public void Add(MQB mqb)
        {
            Files++;
            foreach (var r in mqb.Resources)
            {
                Kinds.Add(r.Unk48);
                Note(Resources, r.Unk48, r.Parameters);
            }
            foreach (var cut in mqb.Cuts)
                foreach (var tl in cut.Timelines)
                {
                    var kind = TimelineKind(mqb, tl);
                    Note(Timelines, kind, tl.Parameters);
                    foreach (var ev in tl.Events)
                        if (ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count)
                            Note(Events, mqb.Resources[ev.ResourceIndex].Unk48, ev.Parameters);
                }
        }

        public static bool Fits(Dictionary<int, Dictionary<string, MQB.Parameter.DataType>> table, int kind, MQB.Parameter p) =>
            table.TryGetValue(kind, out var d) && d.TryGetValue(p.Name, out var type) && type == p.Type;
    }

    /// <summary>A timeline's kind: that of its first event's resource (-1 for none).</summary>
    public static int TimelineKind(MQB mqb, MQB.Timeline tl) =>
        tl.Events.FirstOrDefault(e => e.ResourceIndex >= 0 && e.ResourceIndex < mqb.Resources.Count) is { } ev ? mqb.Resources[ev.ResourceIndex].Unk48 : -1;

    // ---------------------------------------------------------------- the timeline

    public sealed class MqbStats
    {
        public int Resources, ResourcesDropped, Events, EventsDropped, ParamsDropped, Renamed, MapsMoved;
        public SortedDictionary<int, int> DroppedKinds { get; } = new();

        public void Add(MqbStats o)
        {
            Resources += o.Resources; ResourcesDropped += o.ResourcesDropped; Events += o.Events; EventsDropped += o.EventsDropped;
            ParamsDropped += o.ParamsDropped; Renamed += o.Renamed; MapsMoved += o.MapsMoved;
            foreach (var (k, v) in o.DroppedKinds)
                DroppedKinds[k] = DroppedKinds.GetValueOrDefault(k) + v;
        }
    }

    /// <summary>
    /// A DS3 path or name in Elden Ring's form: N:\FDP\ -> N:\GR\, \remo\ -> \cutscene\, the DS3 cutscene's name ->
    /// the Elden Ring one's.
    /// </summary>
    public static string RePath(string s, string ds3Name, string erName)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        var r = Regex.Replace(s, @"N:\\FDP\\", @"N:\GR\", RegexOptions.IgnoreCase);
        r = Regex.Replace(r, @"\\remo\\", @"\cutscene\", RegexOptions.IgnoreCase);
        return Regex.Replace(r, Regex.Escape(ds3Name), erName, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Converts a DS3 MQB in place for Elden Ring (see the class summary): name, paths, part names, the map in part
    /// timelines, and resources, events and parameters Elden Ring doesn't have left out.
    /// </summary>
    /// <param name="rename">A DS3 part name's Elden Ring name, or null to keep it.</param>
    public static MqbStats ConvertMqb(MQB mqb, string ds3Name, string erName, Func<string, string?> rename, MapId ds3Map, MapId erMap, Catalog catalog)
    {
        var s = new MqbStats { Resources = mqb.Resources.Count };
        mqb.Name = erName;
        mqb.ResourceDirectory = RePath(mqb.ResourceDirectory, ds3Name, erName);

        // Resources of a kind Elden Ring never uses go, and the indices that point past them move down.
        var keep = mqb.Resources.Select(r => catalog.Kinds.Contains(r.Unk48)).ToArray();
        var newIndex = new int[mqb.Resources.Count];
        for (int i = 0, n = 0; i < mqb.Resources.Count; i++)
            newIndex[i] = keep[i] ? n++ : -1;
        foreach (var (r, i) in mqb.Resources.Select((r, i) => (r, i)).Where(x => !keep[x.i]))
            s.DroppedKinds[r.Unk48] = s.DroppedKinds.GetValueOrDefault(r.Unk48) + 1;
        s.ResourcesDropped = keep.Count(k => !k);

        string? Renamed(string value)
        {
            var to = rename(value);
            if (to is null || to == value)
                return null;
            s.Renamed++;
            return to;
        }
        void FixParams(List<MQB.Parameter> ps)
        {
            foreach (var p in ps)
            {
                if (p.Value is string str)
                    p.Value = Renamed(str) ?? RePath(str, ds3Name, erName);
                // The map a part is looked up in, when DS3 names its own map.
                if (p.Value is int v && (p.Name == "areaNo" && v == ds3Map.Area && ds3Map.Area != erMap.Area
                                         || p.Name == "blockNo" && v == ds3Map.Block && p.Value is int && ps.Any(q => q.Name == "areaNo")))
                {
                    p.Value = p.Name == "areaNo" ? erMap.Area : erMap.Block;
                    s.MapsMoved++;
                }
            }
        }

        var resources = new List<MQB.Resource>();
        for (int i = 0; i < mqb.Resources.Count; i++)
        {
            if (!keep[i])
                continue;
            var r = mqb.Resources[i];
            r.Name = Renamed(r.Name) ?? r.Name;
            r.Path = r.Path is null ? null : RePath(r.Path, ds3Name, erName);
            r.ParentIndex = r.ParentIndex >= 0 && r.ParentIndex < newIndex.Length ? newIndex[r.ParentIndex] : r.ParentIndex;
            int before = r.Parameters.Count;
            r.Parameters.RemoveAll(p => !Catalog.Fits(catalog.Resources, r.Unk48, p));
            s.ParamsDropped += before - r.Parameters.Count;
            FixParams(r.Parameters);
            resources.Add(r);
        }

        foreach (var cut in mqb.Cuts)
        {
            var timelines = new List<MQB.Timeline>();
            foreach (var tl in cut.Timelines)
            {
                var kind = TimelineKind(mqb, tl);
                int hadEvents = tl.Events.Count;
                s.Events += hadEvents;
                tl.Events.RemoveAll(ev =>
                {
                    var ok = ev.ResourceIndex < 0 || ev.ResourceIndex < keep.Length && keep[ev.ResourceIndex]
                             && ev.Parameters.All(p => Catalog.Fits(catalog.Events, mqb.Resources[ev.ResourceIndex].Unk48, p));
                    if (!ok && ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count)
                    {
                        var k = mqb.Resources[ev.ResourceIndex].Unk48;
                        if (keep[ev.ResourceIndex])
                            s.DroppedKinds[-k] = s.DroppedKinds.GetValueOrDefault(-k) + 1; // negative: an event dropped for its parameters
                    }
                    return !ok;
                });
                s.EventsDropped += hadEvents - tl.Events.Count;
                if (hadEvents > 0 && tl.Events.Count == 0)
                    continue; // everything it did was left out
                foreach (var ev in tl.Events)
                {
                    if (ev.ResourceIndex >= 0 && ev.ResourceIndex < newIndex.Length)
                        ev.ResourceIndex = newIndex[ev.ResourceIndex];
                    FixParams(ev.Parameters);
                }
                int before = tl.Parameters.Count;
                if (kind >= 0)
                    tl.Parameters.RemoveAll(p => !Catalog.Fits(catalog.Timelines, kind, p));
                s.ParamsDropped += before - tl.Parameters.Count;
                FixParams(tl.Parameters);
                timelines.Add(tl);
            }
            cut.Timelines = timelines;
        }
        mqb.Resources = resources;
        return s;
    }

    // ---------------------------------------------------------------- Havok

    /// <summary>What a converted Havok file needs from Elden Ring: its file layout, and the player and camera skeletons.</summary>
    public sealed class HavokTemplate
    {
        public required byte[] Hkx;
        /// <summary>Elden Ring's cutscene player skeleton (null: the player's DS3 skeleton is kept).</summary>
        public AnimPort.ErSkeleton? Player;
        public hkaSkeleton? PlayerSkeleton;
        /// <summary>A camera skeleton of Elden Ring's, for its float slots (field of view).</summary>
        public hkaSkeleton? Camera;
    }

    public sealed class HkxStats
    {
        public int Files, Animations, Copied, Retargeted, Renamed, Failed;
        public List<string> Notes { get; } = [];

        public void Add(HkxStats o)
        {
            Files += o.Files; Animations += o.Animations; Copied += o.Copied; Retargeted += o.Retargeted; Renamed += o.Renamed; Failed += o.Failed;
            Notes.AddRange(o.Notes.Take(Math.Max(0, 20 - Notes.Count)));
        }
    }

    private static hkQsTransform Qs(HKX.Transform t)
    {
        var b = NewBlendableTransform.FromHKXTransform(t);
        return new hkQsTransform
        {
            m_translation = new Vector4(b.Translation, 0),
            m_rotation = b.Rotation,
            m_scale = new Vector4(b.Scale, 0),
        };
    }

    /// <summary>One DS3 animated thing in a cut: its skeleton, animation and binding (the file holds them in the same order).</summary>
    public sealed record Track(HKX.HKASkeleton Skeleton, HKX.HKASplineCompressedAnimation Animation, HKX.HKAAnimationBinding? Binding, HKX.HKADefaultAnimatedReferenceFrame? Frame);

    /// <summary>Pairs a DS3 cut file's skeletons, animations and bindings (each list in the container's order).</summary>
    public static List<Track> Tracks(HKX hkx, List<string> notes)
    {
        var skeletons = hkx.DataSection.Objects.OfType<HKX.HKASkeleton>().ToList();
        var animations = hkx.DataSection.Objects.OfType<HKX.HKASplineCompressedAnimation>().ToList();
        var bindings = hkx.DataSection.Objects.OfType<HKX.HKAAnimationBinding>().ToList();
        var frames = hkx.DataSection.Objects.OfType<HKX.HKADefaultAnimatedReferenceFrame>().ToList();
        if (hkx.DataSection.Objects.OfType<HKX.HKAInterleavedUncompressedAnimation>().Any())
            notes.Add("an uncompressed animation is in the file; only spline-compressed ones are converted");
        if (skeletons.Count != animations.Count || bindings.Count != animations.Count)
            notes.Add($"{skeletons.Count} skeletons, {animations.Count} animations, {bindings.Count} bindings: paired in order");
        var result = new List<Track>();
        for (int i = 0; i < Math.Min(skeletons.Count, animations.Count); i++)
        {
            var binding = i < bindings.Count ? bindings[i] : null;
            var bones = (int)skeletons[i].Bones.Size;
            if (binding is not null && Enumerable.Range(0, (int)binding.TransformTrackToBoneIndices.Size).Any(t => binding.TransformTrackToBoneIndices[t].data >= bones))
                notes.Add($"binding {i} names a bone past skeleton {skeletons[i].Name.GetString()}'s {bones}");
            // Frames (root motion) are rare in cutscenes; one per animation when present at all.
            result.Add(new Track(skeletons[i], animations[i], binding, frames.Count == animations.Count ? frames[i] : null));
        }
        return result;
    }

    /// <summary>A DS3 spline animation in Havok 2018's form, its data copied as it is (float tracks included).</summary>
    public static hkaSplineCompressedAnimation CopySpline(HKX.HKASplineCompressedAnimation a) => new()
    {
        m_type = hkaAnimation.AnimationType.HK_SPLINE_COMPRESSED_ANIMATION,
        m_duration = a.Duration,
        m_numberOfTransformTracks = a.TransformTrackCount,
        m_numberOfFloatTracks = a.FloatTrackCount,
        m_numFrames = a.FrameCount,
        m_numBlocks = a.BlockCount,
        m_maxFramesPerBlock = a.FramesPerBlock,
        m_maskAndQuantizationSize = (int)a.MaskAndQuantization,
        m_blockDuration = a.BlockDuration,
        m_blockInverseDuration = a.InverseBlockDuration,
        m_frameDuration = a.FrameDuration,
        m_blockOffsets = Enumerable.Range(0, (int)a.BlockOffsets.Size).Select(i => a.BlockOffsets[i].data).ToList(),
        m_floatBlockOffsets = Enumerable.Range(0, (int)a.FloatBlockOffsets.Size).Select(i => a.FloatBlockOffsets[i].data).ToList(),
        m_transformOffsets = Enumerable.Range(0, (int)a.TransformOffsets.Size).Select(i => a.TransformOffsets[i].data).ToList(),
        m_floatOffsets = Enumerable.Range(0, (int)a.FloatOffsets.Size).Select(i => a.FloatOffsets[i].data).ToList(),
        m_data = [.. a.GetData()],
        m_endian = a.Endian,
    };

    /// <summary>
    /// Converts one DS3 cut file (Havok 2014 packfile) into Elden Ring's form (Havok 2018 tagfile, laid out as
    /// <paramref name="template"/>'s). Names go through <paramref name="rename"/>.
    /// </summary>
    public static (byte[] Hkx, HkxStats Stats) ConvertHkx(byte[] ds3Bytes, Func<string, string?> rename, HavokTemplate template)
    {
        var stats = new HkxStats { Files = 1 };
        var hkx = HKX.Read(ds3Bytes, HKX.HKXVariation.HKXDS3, false);
        var tracks = Tracks(hkx, stats.Notes);
        var skeletons = new List<hkaSkeleton?>();
        var animations = new List<hkaAnimation?>();
        var bindings = new List<hkaAnimationBinding?>();
        foreach (var t in tracks)
        {
            stats.Animations++;
            var ds3Name = t.Skeleton.Name.GetString();
            var name = rename(ds3Name) is { } n && n != ds3Name ? n : ds3Name;
            if (name != ds3Name)
                stats.Renamed++;
            int bones = (int)t.Skeleton.Bones.Size;
            var trackBone = t.Binding is not null && t.Binding.TransformTrackToBoneIndices.Size > 0
                ? Enumerable.Range(0, (int)t.Binding.TransformTrackToBoneIndices.Size).Select(i => t.Binding.TransformTrackToBoneIndices[i].data).ToList()
                : Enumerable.Range(0, t.Animation.TransformTrackCount).Select(i => (short)i).ToList();

            hkaSkeleton skeleton;
            hkaAnimation animation;
            List<short> boneOf;
            if (ds3Name == PlayerPart && template is { Player: { } erPlayer, PlayerSkeleton: { } erPlayerSkeleton })
            {
                // DS3's player bones carried over to Elden Ring's player skeleton, as the moveset port does.
                var ds3Sk = new AnimPort.Ds3Skeleton
                {
                    Names = Enumerable.Range(0, bones).Select(i => t.Skeleton.Bones[i].Name.GetString()).ToList(),
                    ReferencePose = Enumerable.Range(0, bones).Select(i => NewBlendableTransform.FromHKXTransform(t.Skeleton.Transforms[i])).ToList(),
                    Parents = (int)t.Skeleton.ParentIndices.Size == bones ? Enumerable.Range(0, bones).Select(i => t.Skeleton.ParentIndices[i].data).ToList() : [],
                };
                AnimPort.PlanRetarget(ds3Sk, erPlayer);
                var trackByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int tr = 0; tr < trackBone.Count; tr++)
                    if (trackBone[tr] >= 0 && trackBone[tr] < bones)
                        trackByName.TryAdd(ds3Sk.Names[trackBone[tr]], tr);
                var ds3BoneByName = ds3Sk.Names.Select((nm, i) => (nm, i)).GroupBy(x => x.nm, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
                int erBones = erPlayer.Names.Count;
                var frozen = erPlayer.Frozen ?? [];
                var bound = new List<int>();
                var ds3TrackOf = new List<int>();
                var offsets = new List<Vector3>();
                for (int b = 0; b < erBones; b++)
                {
                    if (frozen.Contains(b) || !trackByName.TryGetValue(erPlayer.Names[b], out var tr))
                        continue;
                    var erRef = erPlayer.ReferencePose[b].m_translation;
                    bound.Add(b);
                    ds3TrackOf.Add(tr);
                    offsets.Add(new Vector3(erRef.X, erRef.Y, erRef.Z) - ds3Sk.ReferencePose[ds3BoneByName[erPlayer.Names[b]]].Translation);
                }
                var pre = erPlayer.Pre ?? Enumerable.Repeat(Quaternion.Identity, erBones).ToArray();
                var post = erPlayer.Post ?? Enumerable.Repeat(Quaternion.Identity, erBones).ToArray();
                var spline = SplineTranscode.Build(t.Animation, [.. ds3TrackOf], [.. offsets], bound.Select(b => erPlayer.ReferencePose[b]).ToList(),
                    bound.Select(b => pre[b]).ToArray(), bound.Select(b => post[b]).ToArray());
                spline.m_annotationTracks = bound.Select(b => new hkaAnnotationTrack { m_trackName = erPlayer.Names[b] }).ToList();
                animation = spline;
                skeleton = Clone(erPlayerSkeleton, name);
                boneOf = bound.Select(b => (short)b).ToList();
                stats.Retargeted++;
            }
            else
            {
                skeleton = new hkaSkeleton
                {
                    m_name = name,
                    m_parentIndices = Enumerable.Range(0, (int)t.Skeleton.ParentIndices.Size).Select(i => t.Skeleton.ParentIndices[i].data).ToList(),
                    m_bones = Enumerable.Range(0, bones).Select(i =>
                    {
                        var boneName = t.Skeleton.Bones[i].Name.GetString();
                        // A part's root bone is usually named after the part too.
                        return new hkaBone { m_name = boneName == ds3Name ? name : boneName, m_lockTranslation = t.Skeleton.Bones[i].LockTranslation != 0 };
                    }).ToList(),
                    m_referencePose = Enumerable.Range(0, (int)t.Skeleton.Transforms.Size).Select(i => Qs(t.Skeleton.Transforms[i])).ToList(),
                    m_referenceFloats = Enumerable.Range(0, (int)t.Skeleton.ReferenceFloats.Size).Select(i => t.Skeleton.ReferenceFloats[i].data).ToList(),
                };
                // Float tracks (a camera's field of view) need float slots; DS3's slot names aren't read, so the
                // slots are Elden Ring's camera's where it has as many, else numbered.
                int floats = t.Animation.FloatTrackCount;
                int slots = Math.Max(floats, skeleton.m_referenceFloats.Count);
                if (slots > 0)
                {
                    skeleton.m_floatSlots = template.Camera is { } cam && cam.m_floatSlots.Count >= slots
                        ? cam.m_floatSlots.Take(slots).ToList()
                        : Enumerable.Range(0, slots).Select(i => (string?)$"float{i}").ToList();
                    while (skeleton.m_referenceFloats.Count < slots)
                        skeleton.m_referenceFloats.Add(template.Camera is { } c2 && c2.m_referenceFloats.Count > skeleton.m_referenceFloats.Count
                            ? c2.m_referenceFloats[skeleton.m_referenceFloats.Count] : 0f);
                }
                var copy = CopySpline(t.Animation);
                copy.m_annotationTracks = trackBone.Select(b => new hkaAnnotationTrack { m_trackName = b >= 0 && b < skeleton.m_bones.Count ? skeleton.m_bones[b].m_name : "" }).ToList();
                animation = copy;
                boneOf = trackBone;
                stats.Copied++;
            }
            if (t.Frame is { } f)
                animation.m_extractedMotion = new hkaDefaultAnimatedReferenceFrame
                {
                    m_up = f.Up, m_forward = f.Forward, m_duration = f.Duration,
                    m_referenceFrameSamples = Enumerable.Range(0, (int)f.ReferenceFrameSamples.Size).Select(i => f.ReferenceFrameSamples[i].Vector).ToList(),
                };
            var floatSlots = t.Binding is not null && t.Binding.FloatTrackToFloatSlotIndices.Size > 0
                ? Enumerable.Range(0, (int)t.Binding.FloatTrackToFloatSlotIndices.Size).Select(i => t.Binding.FloatTrackToFloatSlotIndices[i].data).ToList()
                : Enumerable.Range(0, t.Animation.FloatTrackCount).Select(i => (short)i).ToList();
            skeletons.Add(skeleton);
            animations.Add(animation);
            bindings.Add(new hkaAnimationBinding
            {
                m_originalSkeletonName = name,
                m_animation = animation,
                m_transformTrackToBoneIndices = boneOf,
                m_floatTrackToFloatSlotIndices = ds3Name == PlayerPart && stats.Retargeted > 0 ? [] : floatSlots,
                m_partitionIndices = [],
                m_blendHint = hkaAnimationBinding.BlendHint.NORMAL,
            });
        }

        using var output = new MemoryStream();
        lock (AnimPort.HavokLock)
        {
            var root = (hkRootLevelContainer)AnimPort.ReadEr(template.Hkx, null);
            var container = AnimPort.Container(root);
            container.m_skeletons = skeletons;
            container.m_animations = animations;
            container.m_bindings = bindings;
            container.m_attachments = [];
            container.m_skins = [];
            new HavokBinarySerializer().Write(root, output);
        }
        return (output.ToArray(), stats);
    }

    private static hkaSkeleton Clone(hkaSkeleton s, string name) => new()
    {
        m_name = name,
        m_parentIndices = [.. s.m_parentIndices],
        m_bones = s.m_bones.Select(b => new hkaBone { m_name = b.m_name == s.m_name ? name : b.m_name, m_lockTranslation = b.m_lockTranslation }).ToList(),
        m_referencePose = [.. s.m_referencePose],
        m_referenceFloats = [.. s.m_referenceFloats],
        m_floatSlots = [.. s.m_floatSlots],
    };

    // ---------------------------------------------------------------- binders

    /// <summary>Elden Ring's binder settings and compression for cutscenes, and what the conversion reads from its cutscenes.</summary>
    public sealed class ErSide
    {
        public required BND4 Layout;
        public required DCX.CompressionInfo Compression;
        public required HavokTemplate Havok;
        public required Catalog Catalog;
    }

    /// <summary>Reads what the conversion needs from Elden Ring's own cutscenes (every one, for the catalog).</summary>
    public static ErSide ReadEr(GameArchives er, List<string> notes)
    {
        var (templateBytes, compression) = er.ReadWithCompression(ErPath(TemplateCutscene));
        var layout = BND4.Read(templateBytes);
        var hkxFile = layout.Files.FirstOrDefault(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidDataException($"{TemplateCutscene} has no Havok file");
        var havok = new HavokTemplate { Hkx = hkxFile.Bytes };
        lock (AnimPort.HavokLock)
        {
            var root = (hkRootLevelContainer)AnimPort.ReadEr(hkxFile.Bytes, null);
            havok.Camera = AnimPort.Container(root).m_skeletons.FirstOrDefault(s => s?.m_name?.StartsWith("cam", StringComparison.OrdinalIgnoreCase) == true);
        }
        try
        {
            var bnd = BND4.Read(er.Read(ErPath(PlayerCutscene)));
            foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
            {
                lock (AnimPort.HavokLock)
                {
                    var root = (hkRootLevelContainer)AnimPort.ReadEr(f.Bytes, null);
                    var sk = AnimPort.Container(root).m_skeletons.FirstOrDefault(s => s?.m_name == PlayerPart);
                    if (sk is null)
                        continue;
                    havok.PlayerSkeleton = sk;
                    havok.Player = new AnimPort.ErSkeleton { Names = sk.m_bones.Select(b => b.m_name ?? "").ToList(), ReferencePose = sk.m_referencePose, Parents = sk.m_parentIndices };
                }
                break;
            }
            if (havok.Player is null)
                notes.Add($"no player skeleton in {PlayerCutscene}; the player keeps DS3's skeleton in cutscenes");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"player skeleton not read ({ex.Message}); the player keeps DS3's skeleton in cutscenes");
        }

        var catalog = new Catalog();
        var paths = er.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/cutscene/", StringComparison.OrdinalIgnoreCase)
                                          && e.Path.EndsWith(".cutscenebnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).ToList();
        var mqbs = new System.Collections.Concurrent.ConcurrentBag<MQB>();
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
        {
            try
            {
                foreach (var f in BND4.Read(er.Read(path)).Files.Where(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase)))
                    mqbs.Add(MQB.Read(f.Bytes));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (notes)
                    notes.Add($"{path} not read for the catalog ({ex.Message})");
            }
        });
        foreach (var m in mqbs)
            catalog.Add(m);
        return new ErSide { Layout = layout, Compression = compression, Havok = havok, Catalog = catalog };
    }

    public sealed class BinderStats
    {
        public MqbStats Mqb { get; } = new();
        public HkxStats Hkx { get; } = new();
        public int OtherFiles;
    }

    /// <summary>
    /// Converts one DS3 remo binder into an Elden Ring cutscene binder (uncompressed BND4; the caller compresses).
    /// </summary>
    public static (BND4 Binder, BinderStats Stats) ConvertBinder(BND4 ds3, string ds3Name, string erName, Func<string, string?> rename,
        MapId ds3Map, MapId erMap, ErSide side)
    {
        var stats = new BinderStats();
        var bnd = new BND4
        {
            Version = side.Layout.Version, Format = side.Layout.Format, Unicode = side.Layout.Unicode, Extended = side.Layout.Extended,
            BigEndian = side.Layout.BigEndian, BitBigEndian = side.Layout.BitBigEndian, Unk04 = side.Layout.Unk04, Unk05 = side.Layout.Unk05,
        };
        foreach (var f in ds3.Files)
        {
            var data = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
            byte[] converted;
            if (MQB.IsRead(data, out MQB? mqb))
            {
                stats.Mqb.Add(ConvertMqb(mqb, ds3Name, erName, rename, ds3Map, erMap, side.Catalog));
                converted = mqb.Write();
            }
            else if (f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
            {
                var (hkx, hs) = ConvertHkx(data, rename, side.Havok);
                stats.Hkx.Add(hs);
                converted = hkx;
            }
            else
            {
                stats.OtherFiles++;
                converted = data;
            }
            bnd.Files.Add(new BinderFile(f.Flags, f.ID, RePath(f.Name, ds3Name, erName), converted));
        }
        return (bnd, stats);
    }

    // ---------------------------------------------------------------- the port

    /// <summary>One ported map: its slot and the map port's DS3 -> Elden Ring part names.</summary>
    public sealed record MapInput(MapSlots.Slot Slot, IReadOnlyDictionary<string, string> PartNames);

    /// <summary>
    /// A name lookup for one map: the map port's part names, and a renamed part's model where a name is only the
    /// model ("c6020"). Cameras, cutscene dummies and the player keep their names.
    /// </summary>
    public static Func<string, string?> Renamer(IReadOnlyDictionary<string, string> partNames)
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in partNames)
        {
            int a = from.IndexOf('_'), b = to.IndexOf('_');
            if (a > 0 && b > 0 && from[..a] != to[..b])
                models.TryAdd(from[..a], to[..b]);
        }
        return name => partNames.TryGetValue(name, out var to) ? to : models.TryGetValue(name, out var m) ? m : null;
    }

    /// <summary>The DS3 cutscenes on a DS3 map (sAA_B0_NNNN for map mAA_0B): every remo binder whose name gives that area and block.</summary>
    public static List<string> CutscenesOf(GameArchives ds3, MapId map) =>
        ds3.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/remo/", StringComparison.OrdinalIgnoreCase)
                               && e.Path.EndsWith(".remobnd.dcx", StringComparison.OrdinalIgnoreCase))
            .Select(e => ItemText.Leaf(e.Path!).Split('.')[0])
            .Where(n => IdOf(n) is int id && id / 1_000_000 == map.Area && id / 10_000 % 100 == map.Block)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Converts every cutscene of the ported maps, writes them and their CutsceneMapIdParam rows into the mod, and
    /// returns DS3 cutscene ID -> Elden Ring ID for the event port (only cutscenes that converted and were written).
    /// </summary>
    public static (Dictionary<int, int> Ported, string Summary) Port(PorterContext ctx, GameArchives ds3, GameArchives er, IReadOnlyList<MapInput> maps)
    {
        var ported = new Dictionary<int, int>();
        var lines = new List<string>();
        if (!Enabled)
            return (ported, "cutscenes: off (DARKSIGN_CUTSCENES=0, fallback map-cutscenes B); cutscene lines in the scripts are left out");
        var notes = new List<string>();
        var side = ReadEr(er, notes);
        lines.Add($"Elden Ring side: {side.Catalog.Files} cutscene timelines read, {side.Catalog.Kinds.Count} resource kinds; "
                  + $"player skeleton {(side.Havok.Player is null ? "not found (DS3's kept)" : $"{side.Havok.Player.Names.Count} bones")}, "
                  + $"camera float slots {side.Havok.Camera?.m_floatSlots.Count.ToString() ?? "none"} ({string.Join(" ", side.Havok.Camera?.m_floatSlots ?? [])})");
        var total = new BinderStats();
        var rows = new List<(int Id, MapId Map)>();
        foreach (var map in maps.Where(m => !m.Slot.Probe))
        {
            var rename = Renamer(map.PartNames);
            foreach (var name in CutscenesOf(ds3, map.Slot.Ds3))
            {
                var ds3Id = IdOf(name)!.Value;
                var erId = ErId(ds3Id, map.Slot.Er);
                var erName = Name(erId);
                try
                {
                    var source = BND4.Read(ds3.Read(Ds3Path(name)));
                    var (bnd, stats) = ConvertBinder(source, name, erName, rename, map.Slot.Ds3, map.Slot.Er, side);
                    // Checked by reading it back before it is written.
                    foreach (var f in bnd.Files)
                        if (f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase))
                            MQB.Read(f.Bytes);
                    var path = ctx.ModPath(MapPorter.ModParts(ErPath(erName)));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, DCX.Compress(bnd.Write(), side.Compression));
                    ported[ds3Id] = erId;
                    rows.Add((erId, map.Slot.Er));
                    total.Mqb.Add(stats.Mqb);
                    total.Hkx.Add(stats.Hkx);
                    total.OtherFiles += stats.OtherFiles;
                    lines.Add($"{name} -> {erName}: {stats.Hkx.Files} cut files ({stats.Hkx.Animations} animations, {stats.Hkx.Renamed} renamed parts"
                              + $"{(stats.Hkx.Retargeted > 0 ? ", player retargeted" : "")}), timeline {stats.Mqb.Resources - stats.Mqb.ResourcesDropped}/{stats.Mqb.Resources} resources, "
                              + $"{stats.Mqb.Events - stats.Mqb.EventsDropped}/{stats.Mqb.Events} events kept, {stats.Mqb.Renamed} names renamed, {stats.Mqb.MapsMoved} map numbers moved"
                              + (stats.Hkx.Notes.Count > 0 ? $"; notes: {string.Join("; ", stats.Hkx.Notes.Distinct().Take(3))}" : ""));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lines.Add($"{name}: FAILED ({ex.GetType().Name}: {ex.Message}); its script lines are left out (a warp stays a warp)");
                }
            }
        }

        // The map each cutscene is placed against. Without its row the cutscene isn't played; if the rows can't be
        // written, the scripts must not play the cutscenes either.
        if (rows.Count > 0)
        {
            try
            {
                var regulation = ActionButtons.LoadRegulation(ctx);
                lines.Add(WriteRows(regulation, rows));
                regulation.Save(ctx.ModPath("regulation.bin"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"CutsceneMapIdParam: FAILED ({ex.Message}); the cutscenes are written but the scripts won't play them");
                ported.Clear();
            }
        }
        if (total.Mqb.DroppedKinds.Count > 0)
            lines.Add("left out (kind: count; negative kind = events of a kind Elden Ring has, dropped for a parameter it doesn't): "
                      + string.Join(", ", total.Mqb.DroppedKinds.Select(k => $"{k.Key}:{k.Value}")));
        lines.AddRange(notes.Take(10).Select(n => "note: " + n));
        return (ported, $"cutscenes: {ported.Count} DS3 cutscenes converted\n  " + string.Join("\n  ", lines));
    }

    /// <summary>Adds (or replaces) a CutsceneMapIdParam row per converted cutscene: the slot's map, no hit parts.</summary>
    public static string WriteRows(ParamStore regulation, IReadOnlyList<(int Id, MapId Map)> rows)
    {
        var param = regulation.Get(ParamName) ?? throw new InvalidDataException("Elden Ring has no " + ParamName);
        var template = param[TemplateRow] ?? param.Rows.LastOrDefault() ?? throw new InvalidDataException(ParamName + " has no rows to copy");
        foreach (var (id, map) in rows)
        {
            param.Rows.RemoveAll(r => r.ID == id);
            var row = new PARAM.Row(template) { ID = id, Name = $"[DS3] cutscene {Name(id)}" };
            Set(row, "PlayMapId", (uint)PackedMap(map));
            Set(row, "RequireMapId0", 0u);
            Set(row, "RefCamPosHitPartsID", -1);
            Set(row, "HitParts_0", -1);
            Set(row, "HitParts_1", -1);
            param.Rows.Add(row);
        }
        param.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return $"CutsceneMapIdParam: {rows.Count} rows (copies of {template.ID}, PlayMapId = the slot's map)";
    }

    private static void Set(PARAM.Row row, string field, object value)
    {
        var cell = row.Cells.FirstOrDefault(c => c.Def.InternalName == field);
        if (cell is not null)
            cell.Value = Convert.ChangeType(value, cell.Value.GetType());
    }

    // ---------------------------------------------------------------- cutscene-test

    /// <summary>
    /// `cutscene-test [ds3 names...]`: converts DS3 cutscenes (default: the Painted World's) without touching the mod
    /// and checks each result: the binder and every Havok file read back, and every converted animation samples to
    /// the DS3 one (non-player tracks copied exactly). Writes the binders to Work\scratch\cutscenes for inspection.
    /// </summary>
    public static int Test(PorterContext ctx, IReadOnlyList<string> args)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var names = args.Count > 0 ? args.ToList() : CutscenesOf(ds3, MapSlots.PaintedWorld);
        var notes = new List<string>();
        var r = new StringBuilder();
        var side = ReadEr(er, notes);
        r.AppendLine($"Elden Ring side: {side.Catalog.Files} timelines, kinds {string.Join(" ", side.Catalog.Kinds.Order())}");
        r.AppendLine($"  player skeleton: {(side.Havok.Player is null ? "none" : $"{side.Havok.Player.Names.Count} bones")}; camera float slots [{string.Join(", ", side.Havok.Camera?.m_floatSlots ?? [])}], reference floats [{string.Join(", ", side.Havok.Camera?.m_referenceFloats ?? [])}]");
        foreach (var n in notes)
            r.AppendLine("  note: " + n);
        var outDir = ctx.WorkPath("scratch", "cutscenes");
        Directory.CreateDirectory(outDir);
        int failures = 0;
        foreach (var name in names)
        {
            var ds3Id = IdOf(name) ?? throw new FormatException($"not a cutscene name: {name}");
            var slot = MapSlots.All.FirstOrDefault(s => s.Ds3.Area == ds3Id / 1_000_000 && s.Ds3.Block == ds3Id / 10_000 % 100);
            var erMap = slot?.Er ?? MapId.Parse("m19_70_00_00");
            var ds3Map = slot?.Ds3 ?? new MapId(ds3Id / 1_000_000, ds3Id / 10_000 % 100, 0, 0);
            var erName = Name(ErId(ds3Id, erMap));
            r.AppendLine($"=== {name} -> {erName}");
            try
            {
                var source = BND4.Read(ds3.Read(Ds3Path(name)));
                var (bnd, stats) = ConvertBinder(source, name, erName, _ => null, ds3Map, erMap, side);
                r.AppendLine($"  timeline: {stats.Mqb.Resources - stats.Mqb.ResourcesDropped}/{stats.Mqb.Resources} resources, {stats.Mqb.Events - stats.Mqb.EventsDropped}/{stats.Mqb.Events} events, "
                             + $"{stats.Mqb.ParamsDropped} parameters left out, {stats.Mqb.MapsMoved} map numbers moved; dropped kinds {string.Join(", ", stats.Mqb.DroppedKinds.Select(k => $"{k.Key}:{k.Value}"))}");
                r.AppendLine($"  havok: {stats.Hkx.Files} files, {stats.Hkx.Animations} animations ({stats.Hkx.Copied} copied, {stats.Hkx.Retargeted} retargeted); notes: {string.Join("; ", stats.Hkx.Notes.Distinct())}");
                var bytes = bnd.Write();
                var back = BND4.Read(bytes);
                foreach (var (f, src) in back.Files.Zip(source.Files))
                {
                    r.AppendLine($"  [{f.ID}] {f.Name} {f.Bytes.Length} bytes");
                    if (f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase))
                    {
                        var m = MQB.Read(f.Bytes);
                        r.AppendLine($"    MQB \"{m.Name}\" {m.Resources.Count} resources, {m.Cuts.Count} cuts; HKT paths: {string.Join(" ", m.Resources.Where(x => x.Path is not null).Take(2).Select(x => x.Path))}");
                        continue;
                    }
                    if (!f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var ds3Hkx = HKX.Read(DCX.Is(src.Bytes) ? DCX.Decompress(src.Bytes) : src.Bytes, HKX.HKXVariation.HKXDS3, false);
                    var tracks = Tracks(ds3Hkx, []);
                    hkaAnimationContainer container;
                    lock (AnimPort.HavokLock)
                        container = AnimPort.Container((hkRootLevelContainer)AnimPort.ReadEr(f.Bytes, null));
                    for (int i = 0; i < tracks.Count && i < container.m_animations.Count; i++)
                    {
                        var t = tracks[i];
                        var a = (hkaSplineCompressedAnimation)container.m_animations[i]!;
                        var sk = container.m_skeletons[i]!;
                        var line = $"    {sk.m_name}: {sk.m_bones.Count} bones, {a.m_numberOfTransformTracks}+{a.m_numberOfFloatTracks} tracks, {a.m_numFrames} frames, slots [{string.Join(",", sk.m_floatSlots)}]";
                        if (t.Skeleton.Name.GetString() != PlayerPart || side.Havok.Player is null)
                        {
                            // Copied tracks: sample both and compare.
                            var want = SplineCompressedAnimation.ReadSplCmpAnimBytesAndSampleToUncomp(t.Animation.GetData(), t.Animation.TransformTrackCount, t.Animation.BlockCount, t.Animation.FrameCount, t.Animation.FramesPerBlock);
                            var got = SplineTranscode.Sample(a);
                            float worst = 0;
                            for (int k = 0; k < Math.Min(want.Count, got.Count); k++)
                                worst = Math.Max(worst, Vector3.Distance(want[k].Translation, got[k].Translation));
                            line += $", sampled {got.Count}/{want.Count} transforms, worst position difference {worst:0.####} m";
                            if (got.Count != want.Count || worst > 0.001f)
                                failures++;
                        }
                        r.AppendLine(line);
                    }
                }
                File.WriteAllBytes(Path.Combine(outDir, erName + ".cutscenebnd"), bytes);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures++;
                r.AppendLine($"  FAILED: {ex}");
            }
        }
        r.AppendLine(failures == 0 ? "cutscene-test: all checks passed" : $"cutscene-test: {failures} problems");
        var log = ctx.WorkPath("logs", $"cutscene-test-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(log, r.ToString());
        Console.WriteLine(r.ToString());
        Console.WriteLine($"report written to {log}");
        return failures == 0 ? 0 : 1;
    }
}
