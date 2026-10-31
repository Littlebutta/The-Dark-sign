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

        /// <summary>The Elden Ring ID for a DS3 effect, or null when it wasn't ported.</summary>
        public int? Map(int ds3Id) => Effects.TryGetValue(ds3Id, out var id) ? id : null;
    }

    internal enum Kind { Texture = 0, Model = 1, Anibnd = 2 }

    internal sealed record Ds3File(BinderFile File, string Binder);

    /// <summary>Converts the DS3 effects with these IDs (and everything they reference) and adds them to Elden Ring.
    /// An effect that needs anything that can't be ported (a missing file, a model, a failed conversion) is left out,
    /// with every effect that references it, so the caller keeps Elden Ring's events there.</summary>
    public static Result Apply(PorterContext ctx, GameArchives erArchives, Ds3Effects ds3, IEnumerable<int> wanted)
    {
        var result = new Result();
        var queue = new Queue<int>(wanted.Where(id => id > 0).Distinct());
        if (queue.Count == 0)
            return result;

        var js = new FxrScript();
        var erBinders = ErBinders(erArchives);
        var erIds = erBinders.SelectMany(b => b.Bnd.Files).Select(f => (Ext(f.Name), LeafId(f.Name))).ToHashSet();
        var failed = new HashSet<int>();

        // 1. Follow references from the wanted effects, recording what each one needs.
        var effects = new Dictionary<int, (byte[] Bytes, FxrScript.Info Info)>();
        while (queue.TryDequeue(out var id))
        {
            if (effects.ContainsKey(id) || failed.Contains(id))
                continue;
            if (!ds3.Fxr.TryGetValue(id, out var file) || id >= IdBase / 9 || erIds.Contains((".fxr", IdBase + id)))
            {
                failed.Add(id);
                continue;
            }
            FxrScript.Info info;
            try { info = js.Inspect(file.File.Bytes); }
            catch (Exception e) { failed.Add(id); Console.WriteLine($"effects: can't read DS3 f{id:D9}: {Short(e)}"); continue; }
            // DS3 effect models use DS3 materials (MTD) that Elden Ring doesn't have, so effects with models wait.
            if (info.Models.Any(m => m > 0) || info.Anibnds.Any(m => m > 0) || info.Textures.Any(t => t > 0 && !ds3.Resources.ContainsKey((Kind.Texture, t))))
            {
                failed.Add(id);
                continue;
            }
            effects[id] = (file.File.Bytes, info);
            foreach (var child in info.Sfx.Where(c => c > 0))
                queue.Enqueue(child);
        }
        DropDependents(effects, failed);

        // 2. New IDs: effects by offset, textures counted up from ResourceBase past anything Elden Ring has.
        var textures = new Dictionary<int, int>();
        int next = ResourceBase;
        foreach (var t in effects.Values.SelectMany(e => e.Info.Textures).Where(t => t > 0).Distinct().Order())
        {
            while (erIds.Contains((".tpf", next)))
                next++;
            textures[t] = next++;
        }
        var mapJson = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>>
        {
            ["sfx"] = effects.Keys.ToDictionary(id => id.ToString(), id => IdBase + id),
            [((int)Kind.Texture).ToString()] = textures.ToDictionary(t => t.Key.ToString(), t => t.Value),
        });

        // 3. Convert every effect; a failed one takes the effects that reference it out too.
        var converted = new Dictionary<int, byte[]>();
        foreach (var (id, (bytes, _)) in effects.OrderBy(e => e.Key))
        {
            try { converted[id] = js.Convert(bytes, IdBase + id, mapJson); }
            catch (Exception e) { failed.Add(id); Console.WriteLine($"effects: can't convert DS3 f{id:D9}: {Short(e)}"); }
        }
        foreach (var id in failed)
            effects.Remove(id);
        DropDependents(effects, failed);

        // 4. Add the effects, their resource lists and their textures to Elden Ring's common effect binder.
        var added = new Dictionary<string, int>();
        foreach (var id in effects.Keys.Order())
        {
            result.Effects[id] = IdBase + id;
            Add(erBinders, ".fxr", $"f{IdBase + id:D9}.fxr", converted[id], added);
            AddResourceList(erBinders, IdBase + id, effects[id].Info.Textures.Where(textures.ContainsKey).Select(t => textures[t]), added);
        }
        foreach (var (old, newId) in textures.Where(t => effects.Values.Any(e => e.Info.Textures.Contains(t.Key))).OrderBy(t => t.Value))
        {
            var file = ds3.Resources[(Kind.Texture, old)].File;
            var leaf = Digits().Replace(ItemText.Leaf(file.Name), newId.ToString(), 1);
            var bytes = TPF.Is(file.Bytes) ? RenameTextures(TPF.Read(file.Bytes), old, newId).Write() : file.Bytes;
            Add(erBinders, ".tpf", leaf, bytes, added);
        }
        result.Failed.AddRange(failed.Order().Select(id => $"f{id}"));

        foreach (var b in erBinders.Where(b => b.Changed))
        {
            var path = ctx.ModPath(b.Path.TrimStart('/').Split('/'));
            File.WriteAllBytes(path, DCX.Compress(b.Bnd.Write(), b.Compression));
        }
        Console.WriteLine($"effects: {result.Effects.Count} DS3 effects ported ({string.Join(", ", added.Select(a => $"{a.Value} {a.Key}"))}), " +
                          $"{failed.Count} left out (missing, with models, or unconvertible)");
        return result;
    }

    private static void DropDependents(Dictionary<int, (byte[] Bytes, FxrScript.Info Info)> effects, HashSet<int> failed)
    {
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (id, (_, info)) in effects.ToList())
                if (info.Sfx.Any(c => c > 0 && !effects.ContainsKey(c)))
                {
                    effects.Remove(id);
                    failed.Add(id);
                    changed = true;
                }
        }
    }

    // Each Elden Ring effect has a resource list next to it (sfx\ResourceList\f*.ffxreslist). The new ones copy the
    // format of an existing list: when it is text naming texture files, the new list names the effect's textures the
    // same way; otherwise the effect's list is a copy of one for an effect without resources.
    private static void AddResourceList(List<ErBinder> binders, int fxrId, IEnumerable<int> textures, Dictionary<string, int> added)
    {
        const string ext = ".ffxreslist";
        var binder = binders.MaxBy(b => b.Bnd.Files.Count(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))!;
        var lists = binder.Bnd.Files.Where(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).ToList();
        if (lists.Count == 0)
            return;
        var smallest = lists.MinBy(f => f.Bytes.Length)!;
        if (!added.ContainsKey(ext))
            Console.WriteLine($"effects: resource list format, e.g. {ItemText.Leaf(smallest.Name)} = {Convert.ToHexString(smallest.Bytes, 0, Math.Min(48, smallest.Bytes.Length))}");
        byte[] bytes = smallest.Bytes;
        var withTexture = lists.FirstOrDefault(f => IsText(f.Bytes) && System.Text.Encoding.ASCII.GetString(f.Bytes).Contains(".tpf", StringComparison.OrdinalIgnoreCase));
        if (withTexture is not null && textures.Any())
        {
            var sample = System.Text.Encoding.ASCII.GetString(withTexture.Bytes);
            var line = sample.Split('\n').First(l => l.Contains(".tpf", StringComparison.OrdinalIgnoreCase)).TrimEnd('\r');
            var newline = sample.Contains("\r\n") ? "\r\n" : "\n";
            var text = string.Concat(textures.Select(t => ReplaceLastNumber(line, t) + newline));
            bytes = System.Text.Encoding.ASCII.GetBytes(text);
        }
        Add(binders, ext, $"f{fxrId:D9}{ext}", bytes, added);
    }

    private static string ReplaceLastNumber(string line, int value)
    {
        var matches = Digits().Matches(line);
        var last = matches[^1];
        return line[..last.Index] + value + line[(last.Index + last.Length)..];
    }

    private static bool IsText(byte[] bytes) => bytes.Length > 0 && bytes.All(b => b is >= 0x20 and < 0x7F or (byte)'\r' or (byte)'\n' or (byte)'\t');

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
                                                    && p.Contains("commoneffects", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path!.Length))
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
        var like = binder.Bnd.Files.LastOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ?? binder.Bnd.Files.Last();
        var folder = like.Name[..^ItemText.Leaf(like.Name).Length];
        // Files stay in ID order (the game expects it); new ones go after the last.
        binder.Bnd.Files.Add(new BinderFile(like.Flags, binder.Bnd.Files.Max(f => f.ID) + 1, folder + leaf, bytes));
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
                     .OrderBy(e => e.Path!.Contains("common", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(e => e.Path))
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
        foreach (var t in tpf.Textures)
            t.Name = t.Name.Replace(oldId.ToString(), newId.ToString(), StringComparison.Ordinal);
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
    private sealed class FxrScript
    {
        public sealed record Info(int[] Sfx, int[] Textures, int[] Models, int[] Anibnds, int[] Sounds);

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

        public byte[] Convert(byte[] bytes, int newId, string mapJson) =>
            (byte[])_engine.Invoke(_convert, _engine.Intrinsics.Uint8Array.Construct(bytes), newId, mapJson).ToObject()!;
    }
}
