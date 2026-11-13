using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Ports DS3 visual effects (FXR) into Elden Ring under new IDs. DS3 writes FXR version 4 and Elden Ring reads version
/// 5 with a different set of actions, so each effect is converted by the fxr library (third_party/fxr, run with Jint
/// through Effects/fxr-port.js). Effects pull in the effects they reference and their textures, models and anibnds,
/// which are renumbered too, so nothing Elden Ring has is replaced. Everything goes into Elden Ring's common effect
/// binders (Mods\ER\sfx\...), which the game loads at boot.
/// </summary>
public static partial class FxrPort
{
    /// <summary>New FXR IDs are this plus the DS3 ID (DS3 IDs stay below it; Elden Ring's don't reach it).</summary>
    public const int IdBase = 900_000_000;

    /// <summary>New texture, model and anibnd IDs start here.</summary>
    private const int ResourceBase = 900_000;

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex Digits();

    public sealed class Result
    {
        public Dictionary<int, int> Effects { get; } = [];
        public List<string> Failed { get; } = [];
        /// <summary>Why each DS3 effect that was asked for (or referenced by one) was left out.</summary>
        public Dictionary<int, string> Reasons { get; } = [];
        /// <summary>DS3 sound IDs (type s) the ported effects play, renamed to SoundPort.IdBase + ID in them.</summary>
        public HashSet<int> Sounds { get; } = [];
        /// <summary>The ones of <see cref="Sounds"/> some ported effect plays without holding them (no node of it ends
        /// them when it ends): they must not loop, nothing would stop them.</summary>
        public HashSet<int> UnheldSounds { get; } = [];

        /// <summary>The Elden Ring ID for a DS3 effect, or null when it wasn't ported.</summary>
        public int? Map(int ds3Id) => Effects.TryGetValue(ds3Id, out var id) ? id : null;
    }

    /// <summary>The reason recorded for an effect DS3's params or moves name but DS3 ships no FXR for.</summary>
    public const string NoDs3File = "no DS3 file";

    internal enum Kind { Texture = 0, Model = 1, Anibnd = 2, Sound = 3 }

    /// <summary>Fallback "weapon-effects-files" C: DARKSIGN_FXR_HELD_SOUNDS=1 keeps the sounds DS3 effects hold while
    /// they live as they are, as in builds up to 1.0.282 (the hum of Friede's ice blade never stopped).</summary>
    private static readonly bool HeldSoundsOnce = Environment.GetEnvironmentVariable("DARKSIGN_FXR_HELD_SOUNDS") != "1";

    private static readonly bool PortFxrSounds = Environment.GetEnvironmentVariable("DARKSIGN_FXR_SOUNDS") != "0";

    /// <summary>Lights DS3's smoke-like billboards (Normal and Unk6 blend) the way Elden Ring lights its own (lighting
    /// -2): see fxr-port.js. Fallback "map-effects" D: DARKSIGN_FXR_LIT=0 leaves them unlit, as up to 1.0.44x.</summary>
    internal static readonly bool LitSmoke = Environment.GetEnvironmentVariable("DARKSIGN_FXR_LIT") != "0";

    private static IEnumerable<int> FxrSounds(IEnumerable<int> ids) => ids.Where(id => id is > 0 and < 100_000_000).Distinct();

    /// <summary>
    /// The DS3 sounds (as the keys of fxr-port.js's map.held) an effect may keep holding: the ones it plays as ported
    /// sounds, when those get Stop events (SoundPort.StopEvents: every ported sound does). Measured: Elden Ring's own
    /// effects hold 1,188 sounds, each with a Stop event, 814 of them looping. Inferred from that and from the game
    /// building "Stop_%s" names: it ends a held sound by posting its Stop event when the node ends (not observed in
    /// game). The effects are converted before the sounds are ported, so this goes by the port's rule, not its
    /// result: a sound that then doesn't port has no Play event either and stays silent.
    /// </summary>
    private static Dictionary<string, int> StoppableSounds(IEnumerable<int> heldSounds) =>
        PortFxrSounds && SoundPort.StopEvents ? FxrSounds(heldSounds).ToDictionary(id => id.ToString(), _ => 1) : [];

    /// <summary>The sounds a converted effect plays without holding them: DS3's own unheld ones, and the held ones
    /// too when the conversion clears their flag (no Stop events, see <see cref="StoppableSounds"/>).</summary>
    private static IEnumerable<int> Unheld(FxrScript.Info info) =>
        FxrSounds(HeldSoundsOnce && !SoundPort.StopEvents ? info.Sounds : info.UnheldSounds ?? info.Sounds);

    internal sealed record Ds3File(BinderFile File, string Binder);

    /// <summary>Converts the DS3 effects with these IDs (and everything they reference) and adds them to Elden Ring.
    /// An effect that needs anything that can't be ported (a missing file, a model, a failed conversion) is left out,
    /// with every effect that references it, so the caller keeps Elden Ring's events there.</summary>
    public static Result Apply(PorterContext ctx, GameArchives erArchives, Ds3Effects ds3, IEnumerable<int> wanted)
    {
        var result = new Result();
        // A common effect binder from an earlier build would still replace Elden Ring's (with that build's effects,
        // or from an older game patch); it is written again below whenever this build adds effects to it.
        foreach (var stale in StaleBinders(ctx))
            File.Delete(stale);
        var queue = new Queue<int>(wanted.Where(id => id > 0).Distinct());
        if (queue.Count == 0)
            return result;

        var js = new FxrScript();
        var erBinders = ErBinders(erArchives);
        var erIds = erBinders.SelectMany(b => b.Bnd.Files).Select(f => (Ext(f.Name), LeafId(f.Name))).ToHashSet();
        var failed = new HashSet<int>();
        var reasons = new Dictionary<string, List<int>>();
        void Fail(int id, string reason)
        {
            failed.Add(id);
            result.Reasons[id] = reason;
            if (!reasons.TryGetValue(reason, out var list))
                reasons[reason] = list = [];
            list.Add(id);
        }

        // 1. Follow references from the wanted effects, recording what each one needs.
        var effects = new Dictionary<int, (byte[] Bytes, FxrScript.Info Info)>();
        EffectModelPort? modelPort = null;
        var modelWhy = new Dictionary<int, string?>();
        while (queue.TryDequeue(out var id))
        {
            if (effects.ContainsKey(id) || failed.Contains(id))
                continue;
            if (!ds3.Fxr.TryGetValue(id, out var file) || id >= IdBase / 9 || erIds.Contains((".fxr", IdBase + id)))
            {
                Fail(id, NoDs3File);
                continue;
            }
            FxrScript.Info info;
            try { info = js.Inspect(file.File.Bytes); }
            catch (Exception e) { Fail(id, "unreadable"); Console.WriteLine($"effects: can't read DS3 f{id:D9}: {Short(e)}"); continue; }
            // Models are converted onto Elden Ring's effect materials (EffectModelPort), and a model's animations
            // (an anibnd with the model's own number) through EffectAnimPort; not a model whose material Elden Ring
            // has nothing like.
            if (info.Anibnds.Any(m => m > 0) && (!EffectAnimPort.Enabled || !EffectModelPort.Enabled
                    || info.Anibnds.Any(a => a > 0 && (!info.Models.Contains(a) || !ds3.Resources.ContainsKey((Kind.Anibnd, a))))))
            {
                Fail(id, "uses animated models");
                continue;
            }
            if (info.Models.Any(m => m > 0))
            {
                if (!EffectModelPort.Enabled)
                {
                    Fail(id, "uses models");
                    continue;
                }
                modelPort ??= new EffectModelPort(erBinders.SelectMany(b => b.Bnd.Files).Where(f => Ext(f.Name) == ".flver").Select(f => (f.Name, f.Bytes)));
                string? why = null;
                foreach (var m in info.Models.Where(m => m > 0).Distinct())
                {
                    if (!modelWhy.TryGetValue(m, out var reason))
                        modelWhy[m] = reason = ds3.Resources.TryGetValue((Kind.Model, m), out var model)
                            ? modelPort.Unsupported(model.File.Bytes) : $"model s{m} missing";
                    why ??= reason;
                }
                if (why is not null)
                {
                    Fail(id, why);
                    continue;
                }
            }
            if (info.Textures.FirstOrDefault(t => t > 0 && !ds3.Resources.ContainsKey((Kind.Texture, t))) is var missing and > 0)
            {
                Fail(id, $"texture s{missing} missing");
                continue;
            }
            effects[id] = (file.File.Bytes, info);
            foreach (var child in info.Sfx.Where(c => c > 0))
                queue.Enqueue(child);
        }
        DropDependents(effects, failed, result.Reasons);

        // 2. New IDs: effects by offset, textures counted up from ResourceBase past anything Elden Ring has.
        var textures = new Dictionary<int, int>();
        int next = ResourceBase;
        // A model's own textures (named in its materials) come along with it; one DS3 doesn't ship keeps the
        // material's default.
        var models = new Dictionary<int, int>();
        var modelTextures = new Dictionary<int, List<int>>();
        foreach (var m in effects.Values.SelectMany(e => e.Info.Models).Where(m => m > 0).Distinct().Order())
            modelTextures[m] = EffectModelPort.TextureIds(ds3.Resources[(Kind.Model, m)].File.Bytes)
                .Where(t => ds3.Resources.ContainsKey((Kind.Texture, t))).ToList();
        foreach (var t in effects.Values.SelectMany(e => e.Info.Textures).Concat(modelTextures.Values.SelectMany(t => t)).Where(t => t > 0).Distinct().Order())
        {
            while (erIds.Contains((".tpf", next)))
                next++;
            textures[t] = next++;
        }
        int nextModel = ResourceBase;
        foreach (var m in modelTextures.Keys.Order())
        {
            while (erIds.Contains((".flver", nextModel)) || erIds.Contains((".anibnd", nextModel)))
                nextModel++;
            models[m] = nextModel++;
        }
        // Every texture file an effect needs: its own and its models'.
        IEnumerable<int> TexturesOf(FxrScript.Info info) =>
            info.Textures.Concat(info.Models.Where(modelTextures.ContainsKey).SelectMany(m => modelTextures[m])).Where(textures.ContainsKey).Distinct();
        // Sounds an effect holds for as long as one of its nodes lives (the hum of Friede's ice blade): see fxr-port.js.
        var held = effects.Where(e => e.Value.Info.HeldSounds is { Length: > 0 }).ToList();
        var stoppable = StoppableSounds(effects.Values.SelectMany(e => e.Info.HeldSounds ?? []));
        if (held.Count > 0)
        {
            int kept = held.Count(e => !HeldSoundsOnce || e.Value.Info.HeldSounds!.Any(s => stoppable.ContainsKey(s.ToString())));
            Console.WriteLine($"effects: {held.Count} DS3 effects hold a sound while they live ({string.Join(" ", held.Take(12).Select(e => $"f{e.Key}:s{e.Value.Info.HeldSounds![0]}"))}{(held.Count > 12 ? " ..." : "")}); " +
                              (!HeldSoundsOnce ? "kept held (DARKSIGN_FXR_HELD_SOUNDS=1)"
                                  : kept == 0 ? "ported to play once, because Elden Ring could not end them (the ported sounds have no Stop events)"
                                  : $"{kept} keep it held: Elden Ring ends the ported sound with its Stop event when the effect ends" +
                                    (kept < held.Count ? $"; {held.Count - kept} play theirs once (not a ported sound)" : "")));
        }
        var mapJson = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>>
        {
            ["once"] = HeldSoundsOnce ? new() { ["all"] = 1 } : [],
            ["lit"] = LitSmoke ? new() { ["all"] = 1 } : [],
            ["held"] = stoppable,
            ["sfx"] = effects.Keys.ToDictionary(id => id.ToString(), id => IdBase + id),
            [((int)Kind.Texture).ToString()] = textures.ToDictionary(t => t.Key.ToString(), t => t.Value),
            [((int)Kind.Model).ToString()] = models.ToDictionary(t => t.Key.ToString(), t => t.Value),
            // An animated model's anibnd has the model's number in both games.
            [((int)Kind.Anibnd).ToString()] = effects.Values.SelectMany(e => e.Info.Anibnds).Where(models.ContainsKey).Distinct()
                .ToDictionary(a => a.ToString(), a => models[a]),
            // DS3 effect sounds are DS3 s-sounds: they play the ported copy (SoundPort, same ID base). Unmapped they
            // played whatever Elden Ring sound has that number. DARKSIGN_FXR_SOUNDS=0 leaves them as they were.
            [((int)Kind.Sound).ToString()] = PortFxrSounds ? FxrSounds(effects.Values.SelectMany(e => e.Info.Sounds))
                .ToDictionary(id => id.ToString(), id => SoundPort.IdBase + id) : [],
        });

        // 3. Convert every model and effect; a failed one takes the effects that reference it out too.
        var convertedModels = new Dictionary<int, byte[]>();
        foreach (var (old, _) in models.OrderBy(m => m.Key))
        {
            try { convertedModels[old] = modelPort!.Convert(ds3.Resources[(Kind.Model, old)].File.Bytes, t => textures.TryGetValue(t, out var id) ? id : null); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"effects: can't convert DS3 model s{old:D5}: {Short(e)}");
                foreach (var (id, _) in effects.Where(x => x.Value.Info.Models.Contains(old)).ToList())
                    Fail(id, $"model s{old} conversion failed");
            }
        }
        // Their animations: each anibnd onto an Elden Ring effect anibnd (skeleton and animation files).
        var convertedAnibnds = new Dictionary<int, byte[]>();
        var animated = effects.Where(e => !failed.Contains(e.Key)).SelectMany(e => e.Value.Info.Anibnds).Where(a => a > 0 && convertedModels.ContainsKey(a)).Distinct().Order().ToList();
        if (animated.Count > 0)
        {
            var template = EffectAnimPort.FindTemplate(erBinders.SelectMany(b => b.Bnd.Files).Where(f => Ext(f.Name) == ".anibnd"));
            if (template is null)
                Console.WriteLine("effects: no Elden Ring effect anibnd to convert DS3's animated models onto");
            foreach (var old in animated)
            {
                try
                {
                    if (template is null)
                        throw new InvalidDataException("no Elden Ring template");
                    convertedAnibnds[old] = EffectAnimPort.Convert(ds3.Resources[(Kind.Anibnd, old)].File.Bytes, models[old], template, out var note);
                    Console.WriteLine($"effects: animated model s{old:D5} -> s{models[old]:D5} on Elden Ring's {template.Stem}: {note}");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Console.WriteLine($"effects: can't convert DS3 anibnd s{old:D5}: {Short(e)}");
                    foreach (var (id, _) in effects.Where(x => x.Value.Info.Anibnds.Contains(old)).ToList())
                        Fail(id, "animated model conversion failed");
                }
            }
        }
        foreach (var id in failed)
            effects.Remove(id);
        var converted = new Dictionary<int, byte[]>();
        foreach (var (id, (bytes, _)) in effects.OrderBy(e => e.Key))
        {
            try { converted[id] = js.Convert(bytes, IdBase + id, mapJson); }
            catch (Exception e) { Fail(id, "conversion failed"); Console.WriteLine($"effects: can't convert DS3 f{id:D9}: {Short(e)}"); }
        }
        foreach (var id in failed)
            effects.Remove(id);
        DropDependents(effects, failed, result.Reasons);

        // 4. Add the effects, their resource lists and their textures to Elden Ring's common effect binder.
        var added = new Dictionary<string, int>();
        foreach (var id in effects.Keys.Order())
        {
            result.Effects[id] = IdBase + id;
            if (PortFxrSounds)
            {
                result.Sounds.UnionWith(FxrSounds(effects[id].Info.Sounds));
                result.UnheldSounds.UnionWith(Unheld(effects[id].Info));
            }
            Add(erBinders, ".fxr", $"f{IdBase + id:D9}.fxr", converted[id], added);
        }
        foreach (var (old, newId) in models.Where(m => effects.Values.Any(e => e.Info.Models.Contains(m.Key))).OrderBy(m => m.Value))
        {
            var leaf = Digits().Replace(ItemText.Leaf(ds3.Resources[(Kind.Model, old)].File.Name), newId.ToString(), 1);
            Add(erBinders, ".flver", leaf, convertedModels[old], added);
        }
        foreach (var (old, bytes) in convertedAnibnds.Where(a => effects.Values.Any(e => e.Info.Anibnds.Contains(a.Key))).OrderBy(a => models[a.Key]))
            Add(erBinders, ".anibnd", $"s{models[old]:D5}.anibnd", bytes, added);
        if (modelPort is not null)
            foreach (var (ds3Material, erMaterial) in modelPort.Used)
                Console.WriteLine($"effects: model material {ds3Material} -> {(erMaterial.Length > 0 ? erMaterial : "no Elden Ring equivalent")}");
        var usedTextures = effects.Values.SelectMany(e => TexturesOf(e.Info)).ToHashSet();
        var textureNames = new Dictionary<int, List<string>>();
        foreach (var (old, newId) in textures.Where(t => usedTextures.Contains(t.Key)).OrderBy(t => t.Value))
        {
            var file = ds3.Resources[(Kind.Texture, old)].File;
            var leaf = Digits().Replace(ItemText.Leaf(file.Name), newId.ToString(), 1);
            var bytes = file.Bytes;
            textureNames[old] = [];
            if (TPF.Is(file.Bytes))
            {
                var tpf = RenameTextures(TPF.Read(file.Bytes), old, newId);
                textureNames[old] = tpf.Textures.Select(t => t.Name).ToList();
                bytes = tpf.Write();
            }
            Add(erBinders, ".tpf", leaf, bytes, added);
        }
        foreach (var id in effects.Keys.Order())
            AddResourceList(erBinders, IdBase + id,
                TexturesOf(effects[id].Info).SelectMany(t => textureNames[t]).Select(n => n + ".tif")
                    .Concat(effects[id].Info.Models.Where(models.ContainsKey).Distinct().Select(m => $"s{models[m]:D5}.sib"))
                    // Elden Ring's lists name an animated model's skeleton too (f000450940: s88880_skeleton.hkt).
                    .Concat(effects[id].Info.Anibnds.Where(convertedAnibnds.ContainsKey).Distinct().Select(a => $"s{models[a]:D5}_skeleton.hkt")), added);
        result.Failed.AddRange(failed.Order().Select(id => $"f{id}"));

        // Compressing the common effect binder (Kraken, one thread, ~200 MB) takes minutes; nothing else in the build
        // reads it, so it is written in the background while the movesets convert. FinishWrites joins it.
        foreach (var b in erBinders.Where(b => b.Changed))
        {
            var path = ctx.ModPath(b.Path.TrimStart('/').Split('/'));
            var binder = b;
            Pending.Add((path, Task.Run(() => File.WriteAllBytes(path, DCX.Compress(binder.Bnd.Write(), binder.Compression)))));
        }
        foreach (var (reason, ids) in reasons)
            Console.WriteLine($"effects: left out ({reason}): {ids.Count}, e.g. {string.Join(" ", ids.Take(6))}");
        Console.WriteLine($"effects: {result.Effects.Count} DS3 effects ported ({string.Join(", ", added.Select(a => $"{a.Value} {a.Key}"))}), " +
                          $"{failed.Count} left out (missing, with animated or unconvertible models, or unconvertible)");
        return result;
    }

    private static readonly List<(string Path, Task Write)> Pending = [];

    /// <summary>
    /// Waits for the effect binders <see cref="Apply"/> left writing in the background. Must run before the build
    /// ends or deletes the moveset's outputs; a failed write is reported and its half-written file removed.
    /// </summary>
    public static void FinishWrites()
    {
        foreach (var (path, write) in Pending)
        {
            try
            {
                write.GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"effects: could not write {path} ({e.GetType().Name}: {e.Message}); the moves keep Elden Ring effects");
                try { File.Delete(path); } catch (IOException) { }
            }
        }
        Pending.Clear();
    }

    /// <summary>The common effect binders in the mod folder (Mods\ER\sfx\*commoneffects*.ffxbnd.dcx).</summary>
    public static IEnumerable<string> StaleBinders(PorterContext ctx)
    {
        var dir = Path.GetDirectoryName(ctx.ModPath("sfx", "x"))!;
        return Directory.EnumerateFiles(dir, "*commoneffects*.ffxbnd.dcx").ToList();
    }

    private static void DropDependents(Dictionary<int, (byte[] Bytes, FxrScript.Info Info)> effects, HashSet<int> failed, Dictionary<int, string> reasons)
    {
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (id, (_, info)) in effects.ToList())
                if (info.Sfx.FirstOrDefault(c => c > 0 && !effects.ContainsKey(c)) is var child and > 0)
                {
                    effects.Remove(id);
                    failed.Add(id);
                    reasons[id] = $"plays f{child} ({reasons.GetValueOrDefault(child, "left out")})";
                    changed = true;
                }
        }
    }

    // Each Elden Ring effect has a resource list next to it (sfx\ResourceList\f*.ffxreslist) naming what it draws
    // with: every texture by name with ".tif" (s10011_a.tif) and every model with ".sib" (s84005.sib), whatever the
    // files are called in the binder (probe on 1.16, 2026-11-10: 7157 lists, 2130 empty). The ported effects' lists
    // name their ported textures and models the same way. Until 1.0.249 they were written empty.
    private static void AddResourceList(List<ErBinder> binders, int fxrId, IEnumerable<string> lines, Dictionary<string, int> added)
    {
        const string ext = ".ffxreslist";
        // Plain ASCII, one resource per CRLF line; an effect without resources has an empty list.
        var bytes = System.Text.Encoding.ASCII.GetBytes(string.Concat(lines.Distinct().Select(l => l + "\r\n")));
        // The first list that names a model is shown, so a build log says what the lists look like.
        if (!_listShown && bytes.AsSpan().IndexOf(".sib"u8) >= 0)
        {
            _listShown = true;
            Console.WriteLine($"effects: resource list, e.g. f{fxrId:D9} = {System.Text.Encoding.ASCII.GetString(bytes).Replace("\r\n", " ").TrimEnd()}");
        }
        Add(binders, ext, $"f{fxrId:D9}{ext}", bytes, added);
    }

    private static bool _listShown;

    private sealed class ErBinder(string path, BND4 bnd, DCX.CompressionInfo compression)
    {
        public string Path { get; } = path;
        public BND4 Bnd { get; } = bnd;
        public DCX.CompressionInfo Compression { get; } = compression;
        public bool Changed { get; set; }
    }

    // The common effect binders, loaded at boot: effects in the first, resources wherever Elden Ring keeps that kind.
    private static List<ErBinder> ErBinders(GameArchives er)
    {
        var binders = new List<ErBinder>();
        foreach (var entry in er.Entries.Where(e => e.Path is { } p && p.StartsWith("/sfx/") && p.EndsWith(".ffxbnd.dcx", StringComparison.OrdinalIgnoreCase)
                                                    && p.Contains("commoneffects", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path!.Length).ThenBy(e => e.Path, StringComparer.Ordinal))
        {
            var (bytes, compression) = er.ReadWithCompression(entry.Path!);
            binders.Add(new ErBinder(entry.Path!, BND4.Read(bytes), compression));
        }
        if (binders.Count == 0)
            throw new FileNotFoundException("Elden Ring has no common effect binder (/sfx/*commoneffects*.ffxbnd.dcx)");
        return binders;
    }

    private static void Add(List<ErBinder> binders, string ext, string leaf, byte[] bytes, Dictionary<string, int> added)
    {
        var binder = binders.MaxBy(b => b.Bnd.Files.Count(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))!;
        var files = binder.Bnd.Files;
        int last = files.FindLastIndex(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var like = last >= 0 ? files[last] : files.Last();
        var folder = like.Name[..^ItemText.Leaf(like.Name).Length];
        // Each kind of file has its own ID range and run in the binder (.fxr from 0, .tpf from 100000, .flver 200000,
        // .anibnd 300000, .ffxreslist 400000, where list 400000 + n belongs to effect n). A new file goes right after
        // the last of its kind with the next ID; appending everything after the lists crashed Elden Ring at startup.
        int id = last >= 0 ? like.ID + 1 : files.Max(f => f.ID) + 1;
        if (files.Any(f => f.ID == id))
            throw new InvalidDataException($"no free ID after {like.Name} in {binder.Path}");
        files.Insert(last >= 0 ? last + 1 : files.Count, new BinderFile(like.Flags, id, folder + leaf, bytes));
        binder.Changed = true;
        added[ext] = added.GetValueOrDefault(ext) + 1;
    }

    /// <summary>Every DS3 effect binder's FXRs, textures, models and anibnds by ID; common binders win over map ones.</summary>
    public sealed class Ds3Effects
    {
        internal Dictionary<int, Ds3File> Fxr { get; } = [];
        internal Dictionary<(Kind, int), Ds3File> Resources { get; } = [];

        public bool Has(int fxrId) => Fxr.ContainsKey(fxrId);
    }

    public static Ds3Effects Ds3Index(GameArchives ds3)
    {
        var index = new Ds3Effects();
        foreach (var entry in ds3.Entries.Where(e => e.Path is { } p && p.StartsWith("/sfx/") && p.EndsWith(".ffxbnd.dcx", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.Path!.Contains("common", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(e => e.Path, StringComparer.Ordinal))
        {
            BND4 bnd;
            try { bnd = BND4.Read(ds3.Read(entry.Path!)); }
            catch (Exception) { continue; }
            foreach (var f in bnd.Files)
            {
                int id = LeafId(f.Name);
                if (id < 0)
                    continue;
                var file = new Ds3File(f, entry.Path!);
                switch (Ext(f.Name))
                {
                    case ".fxr": index.Fxr.TryAdd(id, file); break;
                    case ".tpf": index.Resources.TryAdd((Kind.Texture, id), file); break;
                    case ".flver": index.Resources.TryAdd((Kind.Model, id), file); break;
                    case ".anibnd": index.Resources.TryAdd((Kind.Anibnd, id), file); break;
                }
            }
        }
        return index;
    }

    private static TPF RenameTextures(TPF tpf, int oldId, int newId)
    {
        // Only a digit run that is the old ID itself (s10020 for 10020, not the "20" inside s10020 for ID 20).
        foreach (var t in tpf.Textures)
            t.Name = Digits().Replace(t.Name, m => int.TryParse(m.Value, out var v) && v == oldId ? newId.ToString() : m.Value);
        return tpf;
    }

    private static string Extension(Kind kind) => kind switch { Kind.Texture => ".tpf", Kind.Model => ".flver", _ => ".anibnd" };

    private static string Ext(string name)
    {
        var leaf = ItemText.Leaf(name);
        if (leaf.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
            leaf = leaf[..^4];
        return Path.GetExtension(leaf).ToLowerInvariant();
    }

    private static int LeafId(string name)
    {
        var m = Digits().Match(ItemText.Leaf(name));
        return m.Success && int.TryParse(m.Value, out var id) ? id : -1;
    }

    private static string Short(Exception e) => (e is Jint.Runtime.JavaScriptException js ? js.Message : $"{e.GetType().Name}: {e.Message}").Split('\n')[0];

    /// <summary>The fxr library, loaded once into a Jint engine.</summary>
    internal sealed class FxrScript
    {
        public sealed record Info(int[] Sfx, int[] Textures, int[] Models, int[] Anibnds, int[] Sounds, int[]? HeldSounds = null, int[]? UnheldSounds = null);

        private readonly Engine _engine;
        private readonly JsValue _inspect, _convert;

        public FxrScript()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "fxr");
            _engine = new Engine(o => o.EnableModules(dir));
            var module = _engine.Modules.Import("./fxr-port.js");
            _inspect = module.Get("inspect");
            _convert = module.Get("convert");
        }

        public Info Inspect(byte[] bytes)
        {
            var json = _engine.Invoke(_inspect, _engine.Intrinsics.Uint8Array.Construct(bytes)).AsString();
            return JsonSerializer.Deserialize<Info>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }

        /// <summary>For the self-tests: a small DS3 effect with one texture that plays another effect (child > 0).</summary>
        public byte[] Sample(int id, int child, int texture, int heldSound = 0, int blendMode = 2) =>
            (byte[])_engine.Invoke(_engine.Modules.Import("./fxr-port.js").Get("sample"), id, child, texture, heldSound, blendMode).ToObject()!;

        /// <summary>For the self-tests: the lighting mode of each billboard of a converted (Elden Ring) effect.</summary>
        public int[] LightingOfConverted(byte[] bytes) =>
            JsonSerializer.Deserialize<int[]>(_engine.Invoke(_engine.Modules.Import("./fxr-port.js").Get("lightingOfConverted"), _engine.Intrinsics.Uint8Array.Construct(bytes)).AsString())!;

        /// <summary>For the self-tests: the sounds a converted (Elden Ring) effect plays, and the ones it holds.</summary>
        public (int[] Held, int[] Sounds) SoundsOfConverted(byte[] bytes)
        {
            var json = _engine.Invoke(_engine.Modules.Import("./fxr-port.js").Get("soundsOfConverted"), _engine.Intrinsics.Uint8Array.Construct(bytes)).AsString();
            using var doc = JsonDocument.Parse(json);
            int[] Ids(string name) => [.. doc.RootElement.GetProperty(name).EnumerateArray().Select(e => e.GetInt32())];
            return (Ids("held"), Ids("sounds"));
        }

        public byte[] Convert(byte[] bytes, int newId, string mapJson) =>
            (byte[])_engine.Invoke(_convert, _engine.Intrinsics.Uint8Array.Construct(bytes), newId, mapJson).ToObject()!;
    }
}
