using System.Text.Json;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// DS3 effects for ported enemies. Where the weapons' effects all go into Elden Ring's common effect binder (loaded at
/// boot, written once by port-weapons), an enemy's go into its own character's effect binder
/// (sfx\sfxbnd_cXXXX.ffxbnd.dcx), which the game loads with the character (NpcParam SfxResBankId), the way Elden Ring
/// keeps its own enemies' effects. So 93 enemies' effects cost nothing at boot and port-maps needs nothing from
/// port-weapons, which runs beside it.
/// <para>Each effect is converted once (see <see cref="Apply"/> for the conversion itself) and is the same bytes in
/// every binder that needs it: its new ID and its resources' new IDs depend only on the DS3 IDs (effect 800,000,000 +
/// ID; texture and model 950,000 + the DS3 file's place among all DS3 effect textures / models), never on which
/// other effects a build converts. The weapons' effects (900,000,000 + ID, resources from 900,000) are left alone.</para>
/// </summary>
public static partial class FxrPort
{
    /// <summary>New IDs of enemy effects are this plus the DS3 ID.</summary>
    public const int ChrIdBase = 800_000_000;

    private const int ChrResourceBase = 950_000;

    /// <summary>One converted effect: its Elden Ring bytes and what it needs next to it.</summary>
    public sealed record ChrEffect(byte[] Fxr, int[] Children, int[] Textures, int[] Models, int[] Sounds, int[] UnheldSounds);

    /// <summary>Converts DS3 effects for character binders, each once. Not thread-safe: callers hold one lock.</summary>
    public sealed class ChrConverter
    {
        private readonly Ds3Effects _ds3;
        private readonly Func<IEnumerable<(string Name, byte[] Bytes)>> _erModels;
        private readonly Dictionary<int, int> _textureIds, _modelIds;
        private readonly Dictionary<int, ChrEffect?> _done = [];
        private readonly HashSet<int> _busy = [];
        private readonly Dictionary<int, byte[]?> _models = [];
        private readonly Dictionary<int, List<int>> _modelTextures = [];
        private FxrScript? _js;
        private EffectModelPort? _modelPort;

        /// <summary>Why each effect that was asked for was left out.</summary>
        public Dictionary<int, string> Reasons { get; } = [];

        public int Converted => _done.Count(d => d.Value is not null);

        /// <param name="erModels">Elden Ring's own effect models (name, bytes), read only when a DS3 effect uses a model.</param>
        public ChrConverter(Ds3Effects ds3, Func<IEnumerable<(string Name, byte[] Bytes)>> erModels)
        {
            _ds3 = ds3;
            _erModels = erModels;
            _textureIds = Rank(ds3, Kind.Texture);
            _modelIds = Rank(ds3, Kind.Model);
        }

        public ChrConverter(Ds3Effects ds3, GameArchives er)
            : this(ds3, () => ErBinders(er).SelectMany(b => b.Bnd.Files).Where(f => Ext(f.Name) == ".flver").Select(f => (f.Name, f.Bytes)).ToList())
        {
        }

        private static Dictionary<int, int> Rank(Ds3Effects ds3, Kind kind) =>
            ds3.Resources.Keys.Where(k => k.Item1 == kind).Select(k => k.Item2).Order().Select((id, i) => (id, i)).ToDictionary(t => t.id, t => ChrResourceBase + t.i);

        /// <summary>The new ID of a DS3 effect texture / model file, or null when DS3 has no such file.</summary>
        public int? TextureId(int ds3Id) => _textureIds.TryGetValue(ds3Id, out var id) ? id : null;
        public int? ModelId(int ds3Id) => _modelIds.TryGetValue(ds3Id, out var id) ? id : null;

        /// <summary>The converted effect, or null (with a reason) when it or anything it plays can't be converted.</summary>
        public ChrEffect? Get(int id)
        {
            if (_done.TryGetValue(id, out var done))
                return done;
            if (!_busy.Add(id))
                return Fail(id, "plays itself");
            try
            {
                return _done[id] = Convert(id);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Reasons[id] = $"conversion failed ({Short(e)})";
                return _done[id] = null;
            }
            finally
            {
                _busy.Remove(id);
            }
        }

        private ChrEffect? Fail(int id, string reason)
        {
            Reasons[id] = reason;
            return null;
        }

        private ChrEffect? Convert(int id)
        {
            if (id <= 0 || id >= 100_000_000 || !_ds3.Fxr.TryGetValue(id, out var file))
                return Fail(id, NoDs3File);
            _js ??= new FxrScript();
            FxrScript.Info info;
            try { info = _js.Inspect(file.File.Bytes); }
            catch (Exception e) when (e is not OutOfMemoryException) { return Fail(id, $"unreadable ({Short(e)})"); }
            if (info.Anibnds.Any(m => m > 0))
                return Fail(id, "uses animated models");
            var models = info.Models.Where(m => m > 0).Distinct().ToArray();
            if (models.Length > 0 && !EffectModelPort.Enabled)
                return Fail(id, "uses models");
            var textures = info.Textures.Where(t => t > 0).Distinct().ToList();
            if (textures.FirstOrDefault(t => !_textureIds.ContainsKey(t)) is var missing and > 0)
                return Fail(id, $"texture s{missing} missing");
            foreach (var m in models)
            {
                if (ConvertModel(m) is { } why)
                    return Fail(id, why);
                textures.AddRange(_modelTextures[m]);
            }
            var children = info.Sfx.Where(c => c > 0).Distinct().ToArray();
            foreach (var child in children)
                if (Get(child) is null)
                    return Fail(id, $"plays f{child} ({Reasons.GetValueOrDefault(child, "left out")})");
            var sounds = PortFxrSounds ? FxrSounds(info.Sounds).ToArray() : [];
            var map = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>>
            {
                ["once"] = HeldSoundsOnce ? new() { ["all"] = 1 } : [],
                ["held"] = StoppableSounds(info.HeldSounds ?? []),
                ["sfx"] = children.ToDictionary(c => c.ToString(), c => ChrIdBase + c),
                [((int)Kind.Texture).ToString()] = info.Textures.Where(t => t > 0).Distinct().ToDictionary(t => t.ToString(), t => _textureIds[t]),
                [((int)Kind.Model).ToString()] = models.ToDictionary(m => m.ToString(), m => _modelIds[m]),
                [((int)Kind.Sound).ToString()] = sounds.ToDictionary(s => s.ToString(), s => SoundPort.IdBase + s),
            });
            return new ChrEffect(_js.Convert(file.File.Bytes, ChrIdBase + id, map), children, [.. textures.Distinct()], models, sounds,
                PortFxrSounds ? [.. Unheld(info)] : []);
        }

        // A DS3 effect model on Elden Ring's effect materials (EffectModelPort), once; returns why it can't be.
        private string? ConvertModel(int m)
        {
            if (_models.TryGetValue(m, out var known))
                return known is null ? $"model s{m} can't be converted" : null;
            _models[m] = null;
            _modelTextures[m] = [];
            if (!_ds3.Resources.TryGetValue((Kind.Model, m), out var model))
                return $"model s{m} missing";
            _modelPort ??= new EffectModelPort(_erModels());
            if (_modelPort.Unsupported(model.File.Bytes) is { } why)
                return why;
            // A model's own textures come along with it; one DS3 doesn't ship keeps the material's default.
            var own = EffectModelPort.TextureIds(model.File.Bytes).Where(_textureIds.ContainsKey).Distinct().ToList();
            try
            {
                _models[m] = _modelPort.Convert(model.File.Bytes, t => _textureIds.TryGetValue(t, out var id) ? id : null);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return $"model s{m} conversion failed ({Short(e)})";
            }
            _modelTextures[m] = own;
            return null;
        }

        /// <summary>
        /// Adds the wanted effects that convert, with every effect they play and their textures, models and resource
        /// lists, to a character's effect binder. Returns the effects now in it (DS3 ID -> new ID); what was left out
        /// is in <see cref="Reasons"/>.
        /// </summary>
        public Result AddTo(BND4 binder, IEnumerable<int> wanted, ChrBinderLayout layout)
        {
            var result = new Result();
            var effects = new SortedDictionary<int, ChrEffect>();
            var queue = new Queue<int>(wanted.Where(id => id > 0).Distinct().Order());
            while (queue.TryDequeue(out var id))
            {
                if (effects.ContainsKey(id) || Get(id) is not { } effect)
                    continue;
                effects[id] = effect;
                foreach (var child in effect.Children)
                    queue.Enqueue(child);
            }
            foreach (var id in wanted.Where(id => id > 0 && !effects.ContainsKey(id)).Distinct())
                result.Reasons[id] = Reasons.GetValueOrDefault(id, "left out");
            var textureNames = new Dictionary<int, List<string>>();
            foreach (var t in effects.Values.SelectMany(e => e.Textures).Distinct().Order())
            {
                var file = _ds3.Resources[(Kind.Texture, t)].File;
                int newId = _textureIds[t];
                var bytes = file.Bytes;
                textureNames[t] = [];
                if (TPF.Is(bytes))
                {
                    var tpf = RenameTextures(TPF.Read(bytes), t, newId);
                    textureNames[t] = tpf.Textures.Select(x => x.Name).ToList();
                    bytes = tpf.Write();
                }
                layout.Add(binder, ".tpf", Digits().Replace(ItemText.Leaf(file.Name), newId.ToString(), 1), bytes);
            }
            foreach (var m in effects.Values.SelectMany(e => e.Models).Distinct().Order())
                layout.Add(binder, ".flver", Digits().Replace(ItemText.Leaf(_ds3.Resources[(Kind.Model, m)].File.Name), _modelIds[m].ToString(), 1), _models[m]!);
            foreach (var (id, effect) in effects)
            {
                int newId = ChrIdBase + id;
                layout.Add(binder, ".fxr", $"f{newId:D9}.fxr", effect.Fxr);
                // The list of what the effect draws with, by name: textures ".tif", models ".sib" (see AddResourceList).
                var lines = effect.Textures.SelectMany(t => textureNames[t]).Select(n => n + ".tif")
                    .Concat(effect.Models.Select(m => $"s{_modelIds[m]:D5}.sib")).Distinct();
                layout.Add(binder, ".ffxreslist", $"f{newId:D9}.ffxreslist", System.Text.Encoding.ASCII.GetBytes(string.Concat(lines.Select(l => l + "\r\n"))));
                result.Effects[id] = newId;
                result.Sounds.UnionWith(effect.Sounds);
                result.UnheldSounds.UnionWith(effect.UnheldSounds);
            }
            return result;
        }
    }

    /// <summary>
    /// How Elden Ring lays a character's effect binder out, read from its own (the first ones in the archives):
    /// for each kind of file the folder inside the binder and the file flags, and each kind's ID range (.fxr from 0,
    /// .tpf 100000, .flver 200000, .anibnd 300000, .ffxreslist 400000; a file placed after the lists crashed the
    /// game at startup when the common binder was first extended). An empty binder to start a character from whose
    /// template has none.
    /// </summary>
    public sealed class ChrBinderLayout
    {
        private static readonly Dictionary<string, (int Base, string Folder)> Kinds = new()
        {
            [".fxr"] = (0, "effect"), [".tpf"] = (100_000, "tex"), [".flver"] = (200_000, "model"), [".anibnd"] = (300_000, "hkx"),
            [".ffxreslist"] = (400_000, "ResourceList"),
        };

        private readonly Dictionary<string, (string Folder, Binder.FileFlags Flags)> _samples = [];
        private readonly byte[]? _empty;
        private readonly DCX.CompressionInfo? _compression;

        public bool Known(string ext) => _samples.ContainsKey(ext);

        /// <summary>Layout from sample binders (path, bytes, compression), stopping once every kind has a sample.</summary>
        public ChrBinderLayout(IEnumerable<(BND4 Bnd, DCX.CompressionInfo Compression)> samples)
        {
            foreach (var (bnd, compression) in samples)
            {
                foreach (var f in bnd.Files)
                    _samples.TryAdd(Ext(f.Name), (f.Name[..^ItemText.Leaf(f.Name).Length], f.Flags));
                if (_empty is null)
                {
                    var copy = BND4.Read(bnd.Write());
                    copy.Files.Clear();
                    _empty = copy.Write();
                    _compression = compression;
                }
                if (Kinds.Keys.Where(k => k != ".anibnd").All(_samples.ContainsKey))
                    break;
            }
            // A kind none of the samples holds sits next to the effects, in the folder Elden Ring names it by.
            if (_samples.Count > 0)
            {
                var any = _samples.TryGetValue(".fxr", out var fxr) ? fxr : _samples.Values.First();
                var folder = any.Folder.TrimEnd('\\', '/');
                int cut = folder.LastIndexOfAny(['\\', '/']);
                var root = cut >= 0 ? folder[..(cut + 1)] : "";
                foreach (var (ext, (_, name)) in Kinds)
                    _samples.TryAdd(ext, (root + name + "\\", any.Flags));
            }
        }

        public bool CanCreate => _empty is not null && _samples.Count > 0;

        /// <summary>A binder with no files, in Elden Ring's format, with the compression its character binders use.</summary>
        public (BND4 Bnd, DCX.CompressionInfo Compression) Create() => (BND4.Read(_empty!), _compression!);

        /// <summary>Adds a file after the last of its kind (or at its kind's place by ID), replacing one of the same name.</summary>
        public void Add(BND4 binder, string ext, string leaf, byte[] bytes)
        {
            var files = binder.Files;
            if (files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(leaf, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                same.Bytes = bytes;
                return;
            }
            int last = files.FindLastIndex(f => Ext(f.Name) == ext);
            var (folder, flags) = last >= 0 ? (files[last].Name[..^ItemText.Leaf(files[last].Name).Length], files[last].Flags) : _samples[ext];
            int id = last >= 0 ? files[last].ID + 1 : Kinds.TryGetValue(ext, out var kind) ? kind.Base : 0;
            while (files.Any(f => f.ID == id))
                id++;
            int at = last >= 0 ? last + 1 : files.FindIndex(f => f.ID > id);
            files.Insert(at < 0 ? files.Count : at, new BinderFile(flags, id, folder + leaf, bytes));
        }
    }

    /// <summary>Elden Ring's own character effect binders, in path order, read one at a time as they are asked for.</summary>
    public static IEnumerable<(BND4 Bnd, DCX.CompressionInfo Compression)> ErChrBinders(GameArchives er)
    {
        foreach (var path in er.Entries.Where(e => e.Path is { } p && p.StartsWith("/sfx/sfxbnd_c", StringComparison.OrdinalIgnoreCase)
                                                    && p.EndsWith(".ffxbnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).Order(StringComparer.Ordinal).Take(40))
        {
            BND4 bnd;
            DCX.CompressionInfo compression;
            try
            {
                (var bytes, compression) = er.ReadWithCompression(path);
                bnd = BND4.Read(bytes);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { continue; }
            yield return (bnd, compression);
        }
    }
}
