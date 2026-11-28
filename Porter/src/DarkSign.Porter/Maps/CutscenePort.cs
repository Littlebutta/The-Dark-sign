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
/// World's Friede intro, becomes s19_70_0000 by default). Sound: see CutsceneSound. Not ported: DS3 effect, light
/// and gparam IDs inside events, which point at DS3 data Elden Ring doesn't have.
///
/// Credits: the Elden Ring cutscene binder layout and the use of SoulsFormats' MQB class follow JortPob's
/// Cutscener.cs (infernoplus/JortPob, Morrowind to Elden Ring); remo part naming (root skeleton per map part)
/// follows soulstruct-havok's remobnd.py (Grimrukh) and DSAnimStudio's RemoManager (Meowmaritus); MQB reading and
/// writing is SoulsFormats (JKAnderson, SoulsFormatsNEXT); CutsceneMapIdParam fields from Paramdex (soulsmods).
/// </summary>
public static partial class CutscenePort
{
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

    /// <summary>
    /// DS3 event parameters Elden Ring keeps under another name, by resource kind: a spot light's (244) beam size,
    /// DS3's Width and Height, is Elden Ring's FovH and FovV.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, (string Ds3, string Er)[]> EventParamRenames = new Dictionary<int, (string, string)[]>
    {
        [244] = [("Width", "FovH"), ("Height", "FovV")],
    };

    /// <summary>
    /// Elden Ring light parameters DS3 has no such thing for, set to what DS3 does instead of Elden Ring's usual value.
    /// ScatteringScale makes a cutscene light glow in Elden Ring's volumetric fog; DS3 cutscene lights have none, and its
    /// spot lights run at 20-30 where Elden Ring's run at 0.05-8. With the shape conform's usual ScatteringScale (1),
    /// the Dancer intro's cyan spot lights (233,255,255, intensity 20-30) filled the hall with flat teal sheets cut
    /// along the screen's quarters (1.0.61x scratch build with #275-#279, s45_01_0010).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, (string Name, float Value)[]> Ds3LacksParams = new Dictionary<int, (string, float)[]>
    {
        [243] = [("ScatteringScale", 0f)],
        [244] = [("ScatteringScale", 0f)],
    };

    /// <summary>
    /// Keeps an event Elden Ring knows the kind of when most of its parameters are Elden Ring's: the parameters Elden
    /// Ring doesn't have for the kind are taken off (after <see cref="EventParamRenames"/>), and the shape conform
    /// adds the ones only Elden Ring has with their usual values. Up to 1.0.60x one parameter Elden Ring lacks left the
    /// whole event out, and every DS3 cutscene light was one: a point light (243) carries DS3's ShadingModel, a spot
    /// light (244) also Width and Height, so all 75 lights of the DS3 cutscenes were dropped and each cutscene was lit
    /// by the map alone (Devin's High Wall report: "the lighting was way off"). Returns how many parameters were taken
    /// off; an event with no more than half its parameters known is left as it is (the filter after this drops it).
    /// </summary>
    public static int KeepFittingParams(MQB.Event ev, int kind, Catalog catalog)
    {
        if (!catalog.Events.TryGetValue(kind, out var erParams) || ev.Parameters.Count == 0)
            return 0;
        if (Ds3LacksParams.TryGetValue(kind, out var lacks))
            foreach (var (name, value) in lacks)
                if (erParams.TryGetValue(name, out var type) && type == MQB.Parameter.DataType.Float && ev.Parameters.All(p => p.Name != name))
                    ev.Parameters.Add(new MQB.Parameter { Name = name, Type = type, Value = value, MemberCount = 1 });
        if (EventParamRenames.TryGetValue(kind, out var renames))
            foreach (var (ds3, er) in renames)
                if (ev.Parameters.FirstOrDefault(p => p.Name == ds3) is { } p && ev.Parameters.All(q => q.Name != er))
                    p.Name = er;
        int fitting = ev.Parameters.Count(p => Catalog.Fits(catalog.Events, kind, p));
        if (fitting == ev.Parameters.Count || fitting * 2 <= ev.Parameters.Count)
            return 0;
        return ev.Parameters.RemoveAll(p => !Catalog.Fits(catalog.Events, kind, p));
    }

    /// <summary>A timeline's kind: that of its first event's resource (-1 for none).</summary>
    public static int TimelineKind(MQB mqb, MQB.Timeline tl) =>
        tl.Events.FirstOrDefault(e => e.ResourceIndex >= 0 && e.ResourceIndex < mqb.Resources.Count) is { } ev ? mqb.Resources[ev.ResourceIndex].Unk48 : -1;

    // ---------------------------------------------------------------- the timeline

    public sealed class MqbStats
    {
        public int Resources, ResourcesDropped, Events, EventsDropped, ParamsDropped, Renamed, MapsMoved, Conformed;
        public SortedDictionary<int, int> DroppedKinds { get; } = new();

        public void Add(MqbStats o)
        {
            Resources += o.Resources; ResourcesDropped += o.ResourcesDropped; Events += o.Events; EventsDropped += o.EventsDropped;
            ParamsDropped += o.ParamsDropped; Renamed += o.Renamed; MapsMoved += o.MapsMoved; Conformed += o.Conformed;
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
    /// <param name="shape">Elden Ring's timeline shape to conform the result to (<see cref="CutsceneShape.Conform"/>), or null.</param>
    public static MqbStats ConvertMqb(MQB mqb, string ds3Name, string erName, Func<string, string?> rename, MapId ds3Map, MapId erMap, Catalog catalog,
        CutsceneShape? shape = null)
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
                foreach (var ev in tl.Events)
                    if (ev.ResourceIndex >= 0 && ev.ResourceIndex < keep.Length && keep[ev.ResourceIndex])
                        s.ParamsDropped += KeepFittingParams(ev, mqb.Resources[ev.ResourceIndex].Unk48, catalog);
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
        if (shape is not null)
            s.Conformed = shape.Conform(mqb);
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
        /// <summary>The skeleton of an Elden Ring character model ("c7126": the mod's ported one or the game's), or null
        /// when it can't be read. A character's cut animation is laid out on it (<see cref="ConvertHkx"/>).</summary>
        public Func<string, AnimPort.ErSkeleton?>? ModelSkeleton;
    }

    /// <summary>
    /// A cut skeleton's bone without its part: "Pelvis" for c6020_0000's "c6020_0000_Pelvis" and for the DS3 player's
    /// "c0000_0000Pelvis" (DS3 leaves the underscore out for the player), "" for the root bone, which carries the
    /// part's own name. Both games name a cut skeleton's bones after the part that plays them (Elden Ring's Margit
    /// intro: skeleton "c2130_9000", bones "c2130_9000", "c2130_9000_Pelvis", ...; its player "c0000_0000_Pelvis").
    /// </summary>
    public static string BareBone(string bone, string part)
    {
        if (bone.Equals(part, StringComparison.OrdinalIgnoreCase))
            return "";
        if (!bone.StartsWith(part, StringComparison.OrdinalIgnoreCase))
            return bone;
        var rest = bone[part.Length..];
        return rest.StartsWith('_') ? rest[1..] : rest;
    }

    /// <summary>A bone under its part's name, as Elden Ring's cut skeletons have it: "c7126_9000_Pelvis"; the part's name for the root ("").</summary>
    public static string PartBone(string bare, string part) => bare.Length == 0 ? part : part + "_" + bare;

    /// <summary>A DS3 cut bone under the Elden Ring part's name; a bone not named after its DS3 part is kept.</summary>
    private static string RenameBone(string bone, string ds3Part, string erPart) =>
        bone.StartsWith(ds3Part, StringComparison.OrdinalIgnoreCase) ? PartBone(BareBone(bone, ds3Part), erPart) : bone;

    [GeneratedRegex(@"^c\d{4}_\d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex ChrPartRegex();

    public sealed class HkxStats
    {
        public int Files, Animations, Copied, Retargeted, Renamed, Failed, Uncompressed, LeftOut;
        /// <summary>Character animations laid out on their Elden Ring model's skeleton, and DS3 tracks of bones that model lacks.</summary>
        public int OnModel, TracksWithoutBone;
        /// <summary>Animations of assets with bones (a DS3 object or map piece) laid out on the asset's own skeleton.</summary>
        public int OnAsset;
        /// <summary>Elden Ring models that aren't the DS3 character's own (too few bones in common): placed, not animated.</summary>
        public SortedSet<string> StandIns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Notes { get; } = [];

        public void Add(HkxStats o)
        {
            Files += o.Files; Animations += o.Animations; Copied += o.Copied; Retargeted += o.Retargeted; Renamed += o.Renamed; Failed += o.Failed; Uncompressed += o.Uncompressed; LeftOut += o.LeftOut;
            OnModel += o.OnModel; TracksWithoutBone += o.TracksWithoutBone; OnAsset += o.OnAsset;
            StandIns.UnionWith(o.StandIns);
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

    /// <summary>One DS3 animated thing whose animation is stored uncompressed (frame by frame) instead of as a spline.</summary>
    public sealed record InterleavedTrack(HKX.HKASkeleton Skeleton, HKX.HKAInterleavedUncompressedAnimation Animation, HKX.HKAAnimationBinding? Binding);

    /// <summary>
    /// Pairs a DS3 cut file's skeletons, animations and bindings (each list in the container's order). Spline
    /// animations come back as tracks; uncompressed ones go to <paramref name="interleaved"/> when it is given (some
    /// High Wall cutscenes, s30_00_0020 and s30_00_0040, store every animation of a cut that way).
    /// </summary>
    public static List<Track> Tracks(HKX hkx, List<string> notes, List<InterleavedTrack>? interleaved = null)
    {
        var skeletons = hkx.DataSection.Objects.OfType<HKX.HKASkeleton>().ToList();
        var animations = hkx.DataSection.Objects.Where(o => o is HKX.HKASplineCompressedAnimation or HKX.HKAInterleavedUncompressedAnimation).ToList();
        var bindings = hkx.DataSection.Objects.OfType<HKX.HKAAnimationBinding>().ToList();
        var frames = hkx.DataSection.Objects.OfType<HKX.HKADefaultAnimatedReferenceFrame>().ToList();
        if (interleaved is null && animations.Any(a => a is HKX.HKAInterleavedUncompressedAnimation))
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
            if (animations[i] is HKX.HKAInterleavedUncompressedAnimation u)
            {
                interleaved?.Add(new InterleavedTrack(skeletons[i], u, binding));
                continue;
            }
            // Frames (root motion) are rare in cutscenes; one per animation when present at all.
            result.Add(new Track(skeletons[i], (HKX.HKASplineCompressedAnimation)animations[i], binding, frames.Count == animations.Count ? frames[i] : null));
        }
        return result;
    }

    /// <summary>Each skeleton of a DS3 cut file: the part it is named after and how many bones it has.</summary>
    public static List<(string Part, int Bones)> SkeletonSizes(byte[] ds3Hkx) =>
        HKX.Read(ds3Hkx, HKX.HKXVariation.HKXDS3, false).DataSection.Objects.OfType<HKX.HKASkeleton>()
            .Select(s => (s.Name.GetString(), (int)s.Bones.Size)).ToList();

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
    /// <param name="keep">Whether a DS3 animated thing (by skeleton name) stays in the cutscene; null keeps all
    /// (<see cref="CutsceneCast"/> leaves out parts Elden Ring has no model for).</param>
    public static (byte[] Hkx, HkxStats Stats) ConvertHkx(byte[] ds3Bytes, Func<string, string?> rename, HavokTemplate template, Func<string, bool>? keep = null)
    {
        var stats = new HkxStats { Files = 1 };
        var hkx = HKX.Read(ds3Bytes, HKX.HKXVariation.HKXDS3, false);
        var uncompressed = new List<InterleavedTrack>();
        var tracks = Tracks(hkx, stats.Notes, uncompressed);
        var skeletons = new List<hkaSkeleton?>();
        var animations = new List<hkaAnimation?>();
        var bindings = new List<hkaAnimationBinding?>();
        foreach (var t in tracks)
        {
            if (keep is not null && !keep(t.Skeleton.Name.GetString()))
            {
                stats.LeftOut++;
                continue;
            }
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
                // Bones are matched without their part: DS3 writes the player's "c0000_0000Pelvis", Elden Ring
                // "c0000_0000_Pelvis" (template.Player's names are bare already). Until 1.0.47x the full names were
                // compared, only the root matched, and the player slid through every cutscene in one pose.
                var ds3Sk = new AnimPort.Ds3Skeleton
                {
                    Names = Enumerable.Range(0, bones).Select(i => BareBone(t.Skeleton.Bones[i].Name.GetString(), ds3Name)).ToList(),
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
                skeleton = Clone(erPlayerSkeleton, name);
                spline.m_annotationTracks = bound.Select(b => new hkaAnnotationTrack { m_trackName = skeleton.m_bones[b].m_name }).ToList();
                animation = spline;
                boneOf = bound.Select(b => (short)b).ToList();
                stats.Retargeted++;
            }
            else if ((ChrPartRegex().IsMatch(name) || bones > 1) && CutsceneCast.ModelOf(name) is { } modelName
                     && template.ModelSkeleton?.Invoke(modelName) is { Names.Count: > 0 } model)
            {
                // An asset with bones of its own (the demons that carry the player off the High Wall, a boss room's
                // falling debris) goes the same way: Elden Ring's own cutscenes give an animated asset a cut skeleton
                // with one bone per bone of the asset, named after the part (s13_00_0010's AEG090_101_0000: 119 bones,
                // 119 tracks). DS3's cut skeleton holds only the bones the cut moves (o007010: 141 to 144 of them, by
                // copy), in its own order.
                // A character: its DS3 tracks on the Elden Ring model's own skeleton, bone by bone by name. The ported
                // model's skeleton is its template's with DS3's bones added (ChrPort.MergeSkeleton), so it has every
                // DS3 bone, at DS3's rest pose, but in another order and among bones DS3 doesn't have. Until 1.0.47x
                // the cut kept DS3's skeleton with DS3's bone names ("c6020_0000_Pelvis" under part c7126_9000), which
                // Elden Ring couldn't match to the model: the boss stood in one pose and only its root moved.
                var erBone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int b = 1; b < model.Names.Count; b++)
                    erBone.TryAdd(model.Names[b], b);
                var trackOf = Enumerable.Repeat(-1, model.Names.Count).ToArray();
                int unmatched = 0;
                for (int tr = 0; tr < trackBone.Count; tr++)
                {
                    if (trackBone[tr] < 0 || trackBone[tr] >= bones)
                        continue;
                    var bare = BareBone(t.Skeleton.Bones[trackBone[tr]].Name.GetString(), ds3Name);
                    // The root (named after the part) places the character in the scene: the model's root bone.
                    int b = bare.Length == 0 ? 0 : erBone.GetValueOrDefault(bare, -1);
                    if (b >= 0 && trackOf[b] < 0)
                        trackOf[b] = tr;
                    else
                    {
                        stats.TracksWithoutBone++;
                        unmatched++;
                    }
                }
                // A model that isn't DS3's (the enemy port put the Dancer's stats on an Elden Ring body: bone match
                // 0.02) shares a handful of bone names at most; DS3's motion on those few would only twist it. It is
                // placed by its root alone and keeps its rest pose.
                if (unmatched > trackBone.Count / 2)
                {
                    for (int b = 1; b < trackOf.Length; b++)
                        trackOf[b] = -1;
                    stats.StandIns.Add(modelName);
                }
                var bound = Enumerable.Range(0, model.Names.Count).Where(b => trackOf[b] >= 0).ToList();
                var spline = SplineTranscode.Build(t.Animation, bound.Select(b => trackOf[b]).ToArray(), new Vector3[bound.Count],
                    bound.Select(b => model.ReferencePose[b]).ToList());
                skeleton = new hkaSkeleton
                {
                    m_name = name,
                    m_parentIndices = [.. model.Parents],
                    m_bones = model.Names.Select((n, b) => new hkaBone { m_name = PartBone(b == 0 ? "" : n, name), m_lockTranslation = false }).ToList(),
                    m_referencePose = [.. model.ReferencePose],
                    m_referenceFloats = [],
                    m_floatSlots = [],
                };
                spline.m_annotationTracks = bound.Select(b => new hkaAnnotationTrack { m_trackName = skeleton.m_bones[b].m_name }).ToList();
                animation = spline;
                boneOf = bound.Select(b => (short)b).ToList();
                if (ChrPartRegex().IsMatch(name))
                    stats.OnModel++;
                else
                    stats.OnAsset++;
            }
            else
            {
                skeleton = new hkaSkeleton
                {
                    m_name = name,
                    m_parentIndices = Enumerable.Range(0, (int)t.Skeleton.ParentIndices.Size).Select(i => t.Skeleton.ParentIndices[i].data).ToList(),
                    m_bones = Enumerable.Range(0, bones).Select(i =>
                    {
                        // Every bone carries its part's name (the root is the part itself): the Elden Ring part's.
                        var boneName = t.Skeleton.Bones[i].Name.GetString();
                        return new hkaBone { m_name = RenameBone(boneName, ds3Name, name), m_lockTranslation = t.Skeleton.Bones[i].LockTranslation != 0 };
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
                m_floatTrackToFloatSlotIndices = animation.m_numberOfFloatTracks == 0 ? [] : floatSlots,
                m_partitionIndices = [],
                m_blendHint = hkaAnimationBinding.BlendHint.NORMAL,
            });
        }

        // Uncompressed animations: copied frame by frame into Havok 2018's own uncompressed form, after the spline ones
        // (cutscene-test pairs the spline ones by position). The player keeps DS3's skeleton in such a cut: the
        // retarget works on splines.
        foreach (var u in uncompressed)
        {
            if (keep is not null && !keep(u.Skeleton.Name.GetString()))
            {
                stats.LeftOut++;
                continue;
            }
            stats.Animations++;
            stats.Copied++;
            stats.Uncompressed++;
            var ds3Name = u.Skeleton.Name.GetString();
            var name = rename(ds3Name) is { } n && n != ds3Name ? n : ds3Name;
            if (name != ds3Name)
                stats.Renamed++;
            int bones = (int)u.Skeleton.Bones.Size;
            var skeleton = new hkaSkeleton
            {
                m_name = name,
                m_parentIndices = Enumerable.Range(0, (int)u.Skeleton.ParentIndices.Size).Select(i => u.Skeleton.ParentIndices[i].data).ToList(),
                m_bones = Enumerable.Range(0, bones).Select(i =>
                {
                    var boneName = u.Skeleton.Bones[i].Name.GetString();
                    return new hkaBone { m_name = RenameBone(boneName, ds3Name, name), m_lockTranslation = u.Skeleton.Bones[i].LockTranslation != 0 };
                }).ToList(),
                m_referencePose = Enumerable.Range(0, (int)u.Skeleton.Transforms.Size).Select(i => Qs(u.Skeleton.Transforms[i])).ToList(),
                m_referenceFloats = Enumerable.Range(0, (int)u.Skeleton.ReferenceFloats.Size).Select(i => u.Skeleton.ReferenceFloats[i].data).ToList(),
            };
            int floats = u.Animation.FloatTrackCount;
            int slots = Math.Max(floats, skeleton.m_referenceFloats.Count);
            if (slots > 0)
            {
                skeleton.m_floatSlots = template.Camera is { } cam && cam.m_floatSlots.Count >= slots
                    ? cam.m_floatSlots.Take(slots).ToList()
                    : Enumerable.Range(0, slots).Select(i => (string?)$"float{i}").ToList();
                while (skeleton.m_referenceFloats.Count < slots)
                    skeleton.m_referenceFloats.Add(0f);
            }
            var trackBone = u.Binding is not null && u.Binding.TransformTrackToBoneIndices.Size > 0
                ? Enumerable.Range(0, (int)u.Binding.TransformTrackToBoneIndices.Size).Select(i => u.Binding.TransformTrackToBoneIndices[i].data).ToList()
                : Enumerable.Range(0, u.Animation.TransformTrackCount).Select(i => (short)i).ToList();
            var animation = new hkaInterleavedUncompressedAnimation
            {
                m_type = hkaAnimation.AnimationType.HK_INTERLEAVED_ANIMATION,
                m_duration = u.Animation.Duration,
                m_numberOfTransformTracks = u.Animation.TransformTrackCount,
                m_numberOfFloatTracks = u.Animation.FloatTrackCount,
                m_transforms = Enumerable.Range(0, (int)u.Animation.Transforms.Size).Select(i => Qs(u.Animation.Transforms[i])).ToList(),
                m_floats = Enumerable.Range(0, (int)u.Animation.Floats.Size).Select(i => u.Animation.Floats[i].data).ToList(),
                m_annotationTracks = trackBone.Select(b => new hkaAnnotationTrack { m_trackName = b >= 0 && b < skeleton.m_bones.Count ? skeleton.m_bones[b].m_name : "" }).ToList(),
            };
            var floatSlots = u.Binding is not null && u.Binding.FloatTrackToFloatSlotIndices.Size > 0
                ? Enumerable.Range(0, (int)u.Binding.FloatTrackToFloatSlotIndices.Size).Select(i => u.Binding.FloatTrackToFloatSlotIndices[i].data).ToList()
                : Enumerable.Range(0, floats).Select(i => (short)i).ToList();
            skeletons.Add(skeleton);
            animations.Add(animation);
            bindings.Add(new hkaAnimationBinding
            {
                m_originalSkeletonName = name,
                m_animation = animation,
                m_transformTrackToBoneIndices = trackBone,
                m_floatTrackToFloatSlotIndices = floatSlots,
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
        /// <summary>How Elden Ring's own timelines fill what the catalog doesn't cover (<see cref="CutsceneShape"/>).</summary>
        public CutsceneShape? Shape;
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
                    // Bare bone names ("Pelvis"), to match DS3's player bones by (BareBone).
                    havok.Player = new AnimPort.ErSkeleton { Names = sk.m_bones.Select(b => BareBone(b.m_name ?? "", PlayerPart)).ToList(), ReferencePose = sk.m_referencePose, Parents = sk.m_parentIndices };
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
        return new ErSide { Layout = layout, Compression = compression, Havok = havok, Catalog = catalog, Shape = CutsceneShape.Learn(mqbs) };
    }

    public sealed class BinderStats
    {
        public MqbStats Mqb { get; } = new();
        public HkxStats Hkx { get; } = new();
        public CutsceneCast.CastStats Cast { get; } = new();
        /// <summary>Events' timeline numbers after the port took timelines out and added some.</summary>
        public CutsceneTimelineLinks.Result? Links;
        /// <summary>DS3 draw-setting events turned into Elden Ring's (<see cref="CutsceneDrawParams"/>).</summary>
        public CutsceneDrawParams.Result Draw { get; set; } = new();
        public int OtherFiles;
        /// <summary>Cuts whose offset moved by the place of the DS3 collision it is measured from, and those collisions (<see cref="CutsceneAnchor"/>).</summary>
        public int AnchoredCuts;
        public SortedSet<string> Anchors { get; } = new(StringComparer.Ordinal);
        /// <summary>Effect events pointed at a converted DS3 effect, and ones left out because theirs didn't convert.</summary>
        public int EffectsMapped, EffectsLeftOut;
        public SortedSet<int> EffectsMissing { get; } = [];
    }

    /// <summary>The parameter of a timeline event that names the effect it plays (kind 103 "SFX" events in both games).</summary>
    public const string EffectParam = "sfxId";

    /// <summary>The DS3 effect IDs a timeline's events play.</summary>
    public static IEnumerable<int> EffectIds(MQB mqb) =>
        mqb.Cuts.SelectMany(c => c.Timelines).SelectMany(t => t.Events).SelectMany(e => e.Parameters)
            .Where(p => p.Name == EffectParam && p.Value is int and > 0).Select(p => (int)p.Value);

    /// <summary>
    /// The DS3 effects the cutscenes of the given maps play (Vordt's and the Dancer's dark mist, Friede's scythe
    /// frost), for port-weapons to convert with the map effects (<see cref="MapSfx"/>): cutscene events name effects
    /// by DS3's IDs, which Elden Ring doesn't have (or has as something else), so up to 1.0.47x no boss intro showed
    /// its effects.
    /// </summary>
    public static SortedSet<int> WantedEffects(GameArchives ds3, IEnumerable<MapSlots.Slot> slots, List<string>? notes = null)
    {
        var ids = new SortedSet<int>();
        foreach (var map in slots.Select(s => s.Ds3).Distinct())
        {
            int cutscenes = 0;
            foreach (var name in CutscenesOf(ds3, map))
                try
                {
                    foreach (var f in BND4.Read(ds3.Read(Ds3Path(name))).Files.Where(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase)))
                    {
                        ids.UnionWith(EffectIds(MQB.Read(f.Bytes)));
                        cutscenes++;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    notes?.Add($"cutscene effects: {name} not read ({ex.Message})");
                }
            if (cutscenes > 0)
                notes?.Add($"cutscene effects: {map}'s {cutscenes} cutscenes read");
        }
        notes?.Add($"cutscene effects: {ids.Count} DS3 effects played by the ported maps' cutscenes");
        return ids;
    }

    /// <summary>
    /// Points a DS3 timeline's effect events at the converted effects; an event whose effect didn't convert is left
    /// out (Elden Ring would play nothing, or its own effect of that number).
    /// </summary>
    public static void MapEffects(MQB mqb, Func<int, int?> converted, BinderStats stats)
    {
        foreach (var tl in mqb.Cuts.SelectMany(c => c.Timelines))
            tl.Events.RemoveAll(ev =>
            {
                foreach (var p in ev.Parameters)
                {
                    if (p.Name != EffectParam || p.Value is not int id || id <= 0)
                        continue;
                    if (converted(id) is not { } to)
                    {
                        stats.EffectsLeftOut++;
                        stats.EffectsMissing.Add(id);
                        return true;
                    }
                    p.Value = to;
                    stats.EffectsMapped++;
                }
                return false;
            });
    }

    /// <summary>
    /// Converts one DS3 remo binder into an Elden Ring cutscene binder (uncompressed BND4; the caller compresses).
    /// </summary>
    /// <param name="sound">Rewrites the DS3 timeline's sound events first (<see cref="CutsceneSound.Prepare"/>), or null.</param>
    /// <param name="cast">The map's <see cref="CutsceneCast.Resolver"/> (DS3 part name -> the Elden Ring name, null = no
    /// Elden Ring model). Given, it renames parts, leaves out what Elden Ring can't load and lays the cut files out as
    /// Elden Ring's (<see cref="CutsceneCast"/>); null converts without it.</param>
    public static (BND4 Binder, BinderStats Stats) ConvertBinder(BND4 ds3, string ds3Name, string erName, Func<string, string?> rename,
        MapId ds3Map, MapId erMap, ErSide side, Action<MQB>? sound = null, Func<string, string?>? cast = null, Func<int, int?>? effects = null,
        Func<string, (Vector3 Position, Vector3 Rotation)?>? placed = null)
    {
        var stats = new BinderStats();
        var bnd = new BND4
        {
            Version = side.Layout.Version, Format = side.Layout.Format, Unicode = side.Layout.Unicode, Extended = side.Layout.Extended,
            BigEndian = side.Layout.BigEndian, BitBigEndian = side.Layout.BitBigEndian, Unk04 = side.Layout.Unk04, Unk05 = side.Layout.Unk05,
        };
        // The timeline first: it says which parts stay and which Havok file each cut plays.
        Dictionary<string, string>? cutFiles = null;
        var names = cast is null ? rename : name => cast(name) is { } to && to != name ? to : null;
        var converted = new Dictionary<BinderFile, byte[]>();
        foreach (var f in ds3.Files)
        {
            var data = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
            if (!MQB.IsRead(data, out MQB? mqb))
                continue;
            var links = CutsceneTimelineLinks.Take(mqb);
            if (effects is not null)
                MapEffects(mqb, effects, stats);
            sound?.Invoke(mqb);
            // Before the cast drops DS3's other draw kinds: 199 and 215 become Elden Ring's 197.
            stats.Draw = CutsceneDrawParams.ConvertEvents(mqb, CutsceneDrawParams.ErFields(side.Catalog), CutsceneDrawParams.ErPartFields(side.Catalog));
            // Before the cast leaves DS3's collisions out: each cut's offset moves by where the map places the one it
            // is measured from (CutsceneAnchor).
            if (placed is not null)
                stats.AnchoredCuts += CutsceneAnchor.Apply(mqb, CutsceneAnchor.PartsOfCuts(ds3, CutsceneCast.CutFiles(mqb)), placed, stats.Anchors);
            if (cast is not null)
            {
                cutFiles = CutsceneCast.CutFiles(mqb);
                CutsceneCast.Remove(mqb, CutsceneCast.Unplayable(mqb, cast, stats.Cast, stats.Draw.ErResources));
            }
            stats.Mqb.Add(ConvertMqb(mqb, ds3Name, erName, names, ds3Map, erMap, side.Catalog, side.Shape));
            if (cast is not null)
                CutsceneCast.RenameHkts(mqb);
            // Last: every event's own timeline and the one it hangs on, under their numbers after the edits above.
            stats.Links = CutsceneTimelineLinks.Relink(mqb, links);
            converted[f] = mqb.Write();
        }
        Func<string, bool>? keep = cast is null ? null : name => cast(name) is not null;
        foreach (var f in ds3.Files)
        {
            if (!converted.TryGetValue(f, out var bytes))
            {
                var data = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
                if (f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
                {
                    // A file no cut plays isn't converted at all when the cut files are laid out as Elden Ring's.
                    var parts = f.Name.Split('\\');
                    if (cutFiles is not null && (parts.Length < 2 || !cutFiles.TryGetValue(parts[^2], out var leaf)
                                                 || !Path.GetFileNameWithoutExtension(parts[^1]).Equals(leaf, StringComparison.OrdinalIgnoreCase)))
                    {
                        stats.Cast.FilesLeftOut++;
                        continue;
                    }
                    var (hkx, hs) = ConvertHkx(data, names, side.Havok, keep);
                    stats.Hkx.Add(hs);
                    bytes = hkx;
                }
                else
                {
                    stats.OtherFiles++;
                    bytes = data;
                }
            }
            bnd.Files.Add(new BinderFile(f.Flags, f.ID, RePath(f.Name, ds3Name, erName), bytes));
        }
        if (cutFiles is not null)
            CutsceneCast.LayOutCutFiles(bnd, cutFiles, stats.Cast);
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
    /// <summary>What the F1 menu shows of a converted cutscene: its slot, the DS3 characters in it (models other than
    /// the player's, e.g. c6020 Friede) and its length.</summary>
    public sealed record Info(string Ds3Name, string ErName, MapId Slot, IReadOnlyList<string> Characters, double Seconds);

    /// <summary>The characters a DS3 timeline animates (part resources named cNNNN_NNNN), the player left out.</summary>
    public static List<string> Characters(MQB mqb) =>
        mqb.Resources.Where(r => r.Unk48 == 1 && r.Name.Length >= 5 && r.Name[0] == 'c' && r.Name[1..5].All(char.IsDigit) && r.Name[1..5] != "0000")
            .Select(r => r.Name[..5]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

    public static (Dictionary<int, int> Ported, string Summary) Port(PorterContext ctx, GameArchives ds3, GameArchives er, IReadOnlyList<MapInput> maps) =>
        Port(ctx, ds3, er, maps, out _);

    public static (Dictionary<int, int> Ported, string Summary) Port(PorterContext ctx, GameArchives ds3, GameArchives er, IReadOnlyList<MapInput> maps,
        out Dictionary<int, Info> infos)
    {
        var ported = new Dictionary<int, int>();
        infos = [];
        var lines = new List<string>();
        var notes = new List<string>();
        var side = ReadEr(er, notes);
        lines.Add($"Elden Ring side: {side.Catalog.Files} cutscene timelines read, {side.Catalog.Kinds.Count} resource kinds; "
                  + $"player skeleton {(side.Havok.Player is null ? "not found (DS3's kept)" : $"{side.Havok.Player.Names.Count} bones")}, "
                  + $"camera float slots {side.Havok.Camera?.m_floatSlots.Count.ToString() ?? "none"} ({string.Join(" ", side.Havok.Camera?.m_floatSlots ?? [])})");
        var total = new BinderStats();
        var rows = new List<(int Id, MapId Map)>();
        // Each character's cut animation goes on its Elden Ring model's skeleton: the mod's (a ported DS3 enemy) or the game's.
        var modelSkeletons = new Dictionary<string, AnimPort.ErSkeleton?>(StringComparer.OrdinalIgnoreCase);
        side.Havok.ModelSkeleton = model =>
        {
            if (modelSkeletons.TryGetValue(model, out var known))
                return known;
            AnimPort.ErSkeleton? skeleton = null;
            try
            {
                bool asset = model.StartsWith("AEG", StringComparison.OrdinalIgnoreCase);
                var archive = asset ? ObjectPort.GeomBndPath(model) : $"/chr/{model.ToLowerInvariant()}.anibnd.dcx";
                var modFile = ctx.ModPath(MapPorter.ModParts(archive));
                var bytes = File.Exists(modFile) ? File.ReadAllBytes(modFile) : er.Contains(archive) ? er.Read(archive) : null;
                var bnd = bytes is null ? null : BND4.Read(bytes);
                // An asset keeps its animation binder inside its asset binder (file 400); one without has no bones to move.
                if (asset && bnd is not null)
                {
                    if (bnd.Files.FirstOrDefault(f => f.ID == 400) is not { } inner)
                        return modelSkeletons[model] = null;
                    bnd = BND4.Read(DCX.Is(inner.Bytes) ? DCX.Decompress(inner.Bytes) : inner.Bytes);
                }
                if (bnd is not null)
                {
                    var file = bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals("skeleton.hkx", StringComparison.OrdinalIgnoreCase));
                    var compendium = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
                    if (file is not null)
                        lock (AnimPort.HavokLock)
                            skeleton = AnimPort.ReadErSkeleton(file.Bytes, compendium);
                }
                if (skeleton is null)
                    notes.Add($"{model}: no skeleton found in the mod or the game; its cut animations keep DS3's skeleton");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"{model}: skeleton not read ({ex.GetType().Name}: {ex.Message}); its cut animations keep DS3's skeleton");
            }
            return modelSkeletons[model] = skeleton;
        };
        // DS3 effect ID -> the converted effect in the common binder (port-weapons converts the ones WantedEffects lists).
        var effectTable = MapSfx.ReadTable(ctx);
        Func<int, int?> effects = id => effectTable.TryGetValue(id, out var to) ? to : null;
        lines.Add($"effects: {effectTable.Count} converted DS3 effects known (Work\\{MapSfx.TableFile}); a cutscene effect that didn't convert is left out");
        // The cutscenes' sound (CutsceneSound): one bank per cutscene, built as each is converted.
        CutsceneSound? sounds = null;
        try
        {
            sounds = CutsceneSound.Create(ctx, ds3, er, notes);
            if (sounds is not null)
                lines.Add($"sound: template bank {sounds.TemplateName}, {sounds.EventCount} DS3 sound events, {sounds.VoiceCount} DS3 talks with a voice");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"sound: FAILED to start ({ex.GetType().Name}: {ex.Message}); cutscenes stay silent");
        }
        var soundNumbers = SoundNumbers(ds3);
        // Subtitles (CutsceneSubtitles): DS3's words for each line, rows written with the CutsceneMapIdParam rows.
        Func<int, string?>? subtitleText = null;
        var subtitleLines = new List<CutsceneSubtitles.Line>();
        var silentVoices = new HashSet<int>();
        int subtitlesDropped = 0;
        ParamStore? ds3Params = null;
        try { ds3Params = ctx.LoadParams(PorterGame.DarkSouls3); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { notes.Add($"subtitles: DS3's params unreadable ({ex.Message}); talk numbers used as text numbers"); }
        try { subtitleText = CutsceneSubtitles.LoadText(ds3, ds3Params); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { lines.Add($"subtitles: DS3's text unreadable ({ex.Message}); subtitles left out"); subtitleText = _ => null; }
        // What Elden Ring can load for a cutscene's cast (CutsceneCast): models in the mod or the game.
        EnemyPort.ChrMap? chrMap = null;
        if (File.Exists(ctx.ModPath(EnemyPort.ChrMap.FileName)))
            chrMap = EnemyPort.ChrMap.Parse(File.ReadAllLines(ctx.ModPath(EnemyPort.ChrMap.FileName)));
        var modelExists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool ModelExists(string model)
        {
            if (modelExists.TryGetValue(model, out var known))
                return known;
            var lower = model.ToLowerInvariant();
            var archive = lower.StartsWith("aeg") ? ObjectPort.GeomBndPath(model) : $"/chr/{lower}.chrbnd.dcx";
            return modelExists[model] = er.Contains(archive) || File.Exists(ctx.ModPath(MapPorter.ModParts(archive)));
        }
        // The object port's assets (darksign_objects.tsv): DS3 object and map-piece models, placed or cutscene-only.
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(ctx.ModPath(ObjectPort.TableFile)))
            foreach (var line in File.ReadAllLines(ctx.ModPath(ObjectPort.TableFile)).Where(l => !l.StartsWith('#')))
                if (line.Split('\t') is { Length: >= 2 } cols)
                    assets.TryAdd(cols[0], cols[1]);
        lines.Add($"cast: parts resolved to Elden Ring models (map part names, {(chrMap is null ? "no enemy model list" : "the enemy port's model list")}, "
                  + $"{assets.Count} object port assets); DS3 draw settings and unplayed cut files left out");
        var ds3Msbs = new Dictionary<MapId, MSB3>();
        foreach (var map in maps.Where(m => !m.Slot.Probe))
        {
            var rename = Renamer(map.PartNames);
            var anchors = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var cast = CutsceneCast.Resolver(map.PartNames, m => chrMap?.ModelOf(m), ModelExists, m => assets.GetValueOrDefault(m), anchors);
            foreach (var name in CutscenesOf(ds3, map.Slot.Ds3))
            {
                var ds3Id = IdOf(name)!.Value;
                var erId = ErId(ds3Id, map.Slot.Er);
                var erName = Name(erId);
                try
                {
                    var source = BND4.Read(ds3.Read(Ds3Path(name)));
                    // Elden Ring plays a cutscene from its timeline; a binder without one (DS3's s45_00_0090: Havok files
                    // only) would leave the game nothing to play, so it isn't ported and the scripts leave its lines out.
                    if (!source.Files.Any(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase)))
                    {
                        lines.Add($"{name}: no timeline (.mqb) in the DS3 binder; left out, its script lines too");
                        continue;
                    }
                    CutsceneSound.Plan? plan = null;
                    int thisIndex = soundNumbers[name];
                    List<CutsceneSubtitles.Line> said = [];
                    int saidDropped = 0;
                    Action<MQB>? prepare = sounds is null && subtitleText is null ? null : m =>
                    {
                        if (sounds is not null)
                            plan = CutsceneSound.Prepare(m, erName, thisIndex, sounds.Known, sounds.Voice);
                        if (subtitleText is not null)
                            said = CutsceneSubtitles.Prepare(m, thisIndex, subtitleText, plan?.VoiceOfTalk, out saidDropped);
                    };
                    if (!ds3Msbs.TryGetValue(map.Slot.Ds3, out var placedMsb))
                        ds3Msbs[map.Slot.Ds3] = placedMsb = MSB3.Read(ds3.Read(MapPorter.MsbPath(map.Slot.Ds3)));
                    var (bnd, stats) = ConvertBinder(source, name, erName, rename, map.Slot.Ds3, map.Slot.Er, side, prepare, cast, effects,
                        CutsceneAnchor.Placed(placedMsb, map.Slot.Ds3));
                    if (stats.AnchoredCuts > 0)
                        lines.Add($"{name} -> {erName}: {stats.AnchoredCuts} cut offsets moved by where DS3's map places the collision they are measured from ({string.Join(", ", stats.Anchors)})");
                    // The bank first: the timeline keeps only the sounds it holds.
                    if (sounds is not null && plan is not null)
                        lines.Add($"{name} sound: {plan}; {BuildSound(sounds, plan, bnd, null, silentVoices)}");
                    if (subtitleText is not null && (said.Count > 0 || saidDropped > 0))
                        lines.Add($"{name} subtitles: {said.Count} lines ({said.Count(l => l.VoiceId >= 0)} with a DS3 voice){(saidDropped > 0 ? $", {saidDropped} events without DS3 words left out" : "")}");
                    // Checked by reading it back before it is written.
                    foreach (var f in bnd.Files)
                        if (f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase))
                            MQB.Read(f.Bytes);
                    var path = ctx.ModPath(MapPorter.ModParts(ErPath(erName)));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, DCX.Compress(bnd.Write(), side.Compression));
                    // The cutscene's own draw params, which its converted draw-setting events pick from.
                    try
                    {
                        if (!ds3Msbs.TryGetValue(map.Slot.Ds3, out var ds3Msb))
                            ds3Msbs[map.Slot.Ds3] = ds3Msb = MSB3.Read(ds3.Read(MapPorter.MsbPath(map.Slot.Ds3)));
                        if (CutsceneDrawParams.Write(ctx, ds3, er, name, erName, map.Slot.Host, ds3Msb, stats.Draw.LightSets, stats.Draw.SceneFog, map.Slot.Ds3) is { } drawLine)
                            lines.Add($"{drawLine}; {stats.Draw.Events} DS3 draw-setting events as Elden Ring's kind {CutsceneDrawParams.ErKind}, {stats.Draw.PartEvents} one-part light sets as its kind {CutsceneDrawParams.ErPartLightKind}");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"{name} draw params: FAILED: {ex.Message}");
                    }
                    ported[ds3Id] = erId;
                    rows.Add((erId, map.Slot.Er));
                    subtitleLines.AddRange(said);
                    subtitlesDropped += saidDropped;
                    // A binder without a timeline (DS3's s45_00_0090) still converts; the menu shows it with no characters.
                    var sourceMqb = source.Files.FirstOrDefault(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase)) is { } mqbFile ? MQB.Read(mqbFile.Bytes) : null;
                    infos[ds3Id] = new Info(name, erName, map.Slot.Er, sourceMqb is null ? [] : Characters(sourceMqb),
                        sourceMqb is { Framerate: > 0 } ? sourceMqb.Cuts.Sum(c => (double)c.Duration) / sourceMqb.Framerate : 0);
                    total.Mqb.Add(stats.Mqb);
                    total.Hkx.Add(stats.Hkx);
                    total.OtherFiles += stats.OtherFiles;
                    lines.Add($"{name} -> {erName}: {stats.Hkx.Files} cut files ({stats.Hkx.Animations} animations, {stats.Hkx.Renamed} renamed parts"
                              + $"{(stats.Hkx.Retargeted > 0 ? ", player retargeted" : "")}), timeline {stats.Mqb.Resources - stats.Mqb.ResourcesDropped}/{stats.Mqb.Resources} resources, "
                              + $"{stats.Mqb.Events - stats.Mqb.EventsDropped}/{stats.Mqb.Events} events kept, {stats.Mqb.Renamed} names renamed, {stats.Mqb.MapsMoved} map numbers moved, {stats.Mqb.Conformed} fields set to Elden Ring's shape"
                              + (stats.Links is { } l ? $"; {l}" : "")
                              + (stats.Hkx.Notes.Count > 0 ? $"; notes: {string.Join("; ", stats.Hkx.Notes.Distinct().Take(3))}" : ""));
                    if (cast is not null)
                        lines.Add($"{name} -> {erName}: {stats.Cast}; {stats.Hkx.LeftOut} Havok tracks of left-out parts dropped");
                    lines.Add($"{name} -> {erName}: {stats.Hkx.OnModel} character animations on their Elden Ring model's skeleton, {stats.Hkx.OnAsset} animations of assets with bones on the asset's"
                              + (stats.Hkx.TracksWithoutBone > 0 ? $" ({stats.Hkx.TracksWithoutBone} DS3 bone tracks the model has no bone for)" : "")
                              + (stats.Hkx.StandIns.Count > 0 ? $"; WARN not DS3's own model, placed but not animated: {string.Join(" ", stats.Hkx.StandIns)}" : "")
                              + $"; effects: {stats.EffectsMapped} events play a converted DS3 effect, {stats.EffectsLeftOut} left out"
                              + (stats.EffectsMissing.Count > 0 ? $" (not converted: {string.Join(" ", stats.EffectsMissing.Take(12))})" : ""));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lines.Add($"{name}: FAILED ({ex.GetType().Name}: {ex.Message}); its script lines are left out (a warp stays a warp)");
                }
            }
            // The asset models these cutscenes cast that the map places nothing of: one hidden part each, so the map
            // loads the model and the cutscenes spawn their copies (CutsceneCast.PlaceAnchors).
            if (anchors.Count > 0)
                lines.Add(PlaceAnchors(ctx, map.Slot.Er, anchors));
        }

        // The map each cutscene is placed against. Without its row the cutscene isn't played; if the rows can't be
        // written, the scripts must not play the cutscenes either.
        if (rows.Count > 0)
        {
            try
            {
                var regulation = ActionButtons.LoadRegulation(ctx);
                lines.Add(WriteRows(regulation.Get(ParamName) ?? throw new InvalidDataException("Elden Ring has no " + ParamName), rows));
                if (subtitleText is not null)
                {
                    try { lines.Add(CutsceneSubtitles.Write(ctx, er, regulation, subtitleLines, silentVoices) + (subtitlesDropped > 0 ? $"; {subtitlesDropped} events without DS3 words left out" : "")); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { lines.Add($"subtitles: FAILED ({ex.GetType().Name}: {ex.Message}); the cutscenes show no words"); }
                }
                regulation.Save(ctx.ModPath("regulation.bin"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines.Add($"CutsceneMapIdParam: FAILED ({ex.Message}); the cutscenes are written but the scripts won't play them");
                ported.Clear();
                infos.Clear();
            }
        }
        if (sounds is not null)
        {
            try { lines.Add("sound " + sounds.WriteIndex(er)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { lines.Add($"sound index: FAILED ({ex.Message}); the cutscene banks may stay silent"); }
        }
        if (total.Mqb.DroppedKinds.Count > 0)
            lines.Add("left out (kind: count; negative kind = events of a kind Elden Ring has, dropped for a parameter it doesn't): "
                      + string.Join(", ", total.Mqb.DroppedKinds.Select(k => $"{k.Key}:{k.Value}")));
        lines.AddRange(notes.Take(10).Select(n => "note: " + n));
        return (ported, $"cutscenes: {ported.Count} DS3 cutscenes converted\n  " + string.Join("\n  ", lines));
    }

    private static string PlaceAnchors(PorterContext ctx, MapId er, IEnumerable<string> models)
    {
        var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(er)));
        try
        {
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            var placed = CutsceneCast.PlaceAnchors(msb, models);
            var bytes = msb.Write();
            MSBE.Read(bytes);
            File.WriteAllBytes(path, DCX.Compress(bytes, compression));
            return $"{er} cutscene anchors: {placed.Count} hidden parts so the map loads models only its cutscenes cast ({string.Join(" ", placed.Take(12))}{(placed.Count > 12 ? " ..." : "")})";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"{er} cutscene anchors: WARN not placed ({ex.GetType().Name}: {ex.Message}); its cutscenes' copies of {string.Join(" ", models.Take(8))} may not show";
        }
    }

    /// <summary>
    /// Each DS3 cutscene's number for its sound IDs (<see cref="CutsceneSound.NewId"/>): its place among all DS3
    /// cutscenes by name, so a cutscene's sounds keep their IDs whichever maps are ported, in whatever order.
    /// </summary>
    public static Dictionary<string, int> SoundNumbers(GameArchives ds3)
    {
        var names = ds3.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/remo/", StringComparison.OrdinalIgnoreCase)
                                           && e.Path.EndsWith(".remobnd.dcx", StringComparison.OrdinalIgnoreCase))
            .Select(e => ItemText.Leaf(e.Path!).Split('.')[0].ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            numbers[names[i]] = i;
        return numbers;
    }

    /// <summary>Builds a cutscene's bank and strips the converted timeline's events for sounds it couldn't build (all
    /// of them when the bank failed). Returns the log text.</summary>
    public static string BuildSound(CutsceneSound sounds, CutsceneSound.Plan plan, BND4 bnd, string? folder, ISet<int>? silentOut = null)
    {
        string built;
        IReadOnlySet<int> silent;
        try { (built, silent) = sounds.Build(plan, folder); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            (built, silent) = ($"bank FAILED ({ex.GetType().Name}: {ex.Message})", plan.Sounds.Values.ToHashSet());
        }
        silentOut?.UnionWith(silent);
        int stripped = CutsceneSound.Strip(bnd, silent);
        return stripped > 0 ? $"{built}; {stripped} events of sounds not in the bank left out" : built;
    }

    /// <summary>Adds (or replaces) a CutsceneMapIdParam row per converted cutscene: the slot's map, no hit parts.</summary>
    public static string WriteRows(PARAM param, IReadOnlyList<(int Id, MapId Map)> rows)
    {
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
        var soundNotes = new List<string>();
        var sounds = CutsceneSound.Create(ctx, ds3, er, soundNotes);
        r.AppendLine(sounds is null ? $"  sound: none ({string.Join("; ", soundNotes)})"
            : $"  sound: template bank {sounds.TemplateName}, {sounds.EventCount} DS3 sound events, {sounds.VoiceCount} DS3 talks with a voice{string.Concat(soundNotes.Select(n => "; " + n))}");
        var soundNumbers = SoundNumbers(ds3);
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
                CutsceneSound.Plan? plan = null;
                int thisIndex = soundNumbers.TryGetValue(name, out var number) ? number : 0;
                var (bnd, stats) = ConvertBinder(source, name, erName, _ => null, ds3Map, erMap, side,
                    sounds is null ? null : m => plan = CutsceneSound.Prepare(m, erName, thisIndex, sounds.Known, sounds.Voice));
                if (sounds is not null && plan is not null)
                {
                    var soundDir = Path.Combine(outDir, "sd");
                    r.AppendLine($"  sound: {plan}; {BuildSound(sounds, plan, bnd, soundDir)}");
                    var mqbFile = bnd.Files.First(f => f.Name.EndsWith(".mqb", StringComparison.OrdinalIgnoreCase));
                    var (soundLines, soundFailures) = CutsceneSoundCheck.Check(MQB.Read(mqbFile.Bytes), Path.Combine(soundDir, erName + ".bnk"));
                    foreach (var l in soundLines)
                        r.AppendLine("    " + l);
                    failures += soundFailures;
                }
                r.AppendLine($"  timeline: {stats.Mqb.Resources - stats.Mqb.ResourcesDropped}/{stats.Mqb.Resources} resources, {stats.Mqb.Events - stats.Mqb.EventsDropped}/{stats.Mqb.Events} events, "
                             + $"{stats.Mqb.ParamsDropped} parameters left out, {stats.Mqb.MapsMoved} map numbers moved, {stats.Mqb.Conformed} fields set to Elden Ring's shape; dropped kinds {string.Join(", ", stats.Mqb.DroppedKinds.Select(k => $"{k.Key}:{k.Value}"))}");
                r.AppendLine($"  havok: {stats.Hkx.Files} files, {stats.Hkx.Animations} animations ({stats.Hkx.Copied} copied, {stats.Hkx.Uncompressed} of them uncompressed, {stats.Hkx.Retargeted} retargeted); notes: {string.Join("; ", stats.Hkx.Notes.Distinct())}");
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
                        bool copied = t.Skeleton.Name.GetString() != PlayerPart || side.Havok.Player is null;
                        if (copied)
                        {
                            // Copied tracks: the spline data must be DS3's byte for byte.
                            bool same = a.m_data.SequenceEqual(t.Animation.GetData()) && a.m_numberOfFloatTracks == t.Animation.FloatTrackCount
                                        && a.m_numFrames == t.Animation.FrameCount && a.m_numBlocks == t.Animation.BlockCount;
                            line += same ? ", data identical to DS3's" : ", DATA DIFFERS FROM DS3's";
                            if (!same)
                                failures++;
                        }
                        if (copied && t.Animation.FloatTrackCount == 0)
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
