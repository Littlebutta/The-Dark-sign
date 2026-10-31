using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// Gives a ported DS3 weapon its own DS3 moveset. Elden Ring picks a weapon's animations by its motion category N:
/// the behaviour's weapon selectors (e.g. AttackRightLight1) hold one clip per category, aNNN_030000, whose events are
/// in TAE aNN.tae. For each weapon this:
/// <list type="number">
/// <item>picks a motion category no Elden Ring weapon uses and starts it as a copy of the template weapon's (so every
/// move Elden Ring has keeps working);</item>
/// <item>replaces each move DS3 also has with the DS3 animation, converted (AnimPort), paired by selector name
/// (anim-id-map), taking the weapon's own art section first (e.g. Friede's a232) and then its class (a038);</item>
/// <item>builds that move's events from DS3's (hits, cancel windows, stamina, ...) where the two games use the same
/// event layout, keeping Elden Ring's sound and effect events (DS3's sound and effect IDs don't exist in Elden Ring),
/// moved to DS3's hit timing;</item>
/// <item>ports the DS3 attack rows (BehaviorParam_PC and AtkParam_Pc) the hits point at, under a new behaviour
/// variation, and points the weapon at the new category and variation.</item>
/// </list>
/// Output: Mods\ER\chr\c0000.anibnd.dcx (TAE), c0000_a9x.anibnd.dcx (animations), c0000.behbnd.dcx (behaviour).
/// </summary>
public static partial class MovesetPort
{
    /// <summary>Limits the port to these DS3 weapons (base row IDs) when set; empty ports every weapon.</summary>
    public static int[] Only = [];

    private const string AnimBinder = "/chr/c0000_a9x.anibnd.dcx";

    // Event types Elden Ring keeps from its own template animation: sounds, effects and other references to IDs that
    // DS3 numbers differently. Every other type comes from DS3 when both games lay it out the same.
    private static readonly HashSet<int> EldenRingOnlyTypes = [96, 112, 128, 129, 133, 139, 224, 792, 907];

    [GeneratedRegex(@"^a(\d{3})_(\d{6})")]
    private static partial Regex AnimName();

    public static void Apply(PorterContext ctx, GameArchives erArchives, GameArchives ds3Archives, ParamStore er, ParamStore ds3,
        IReadOnlyList<WeaponPlan> plans)
    {
        var ports = plans.Where(p => p.Status == "port" && (Only.Length == 0 || Only.Contains(p.Ds3Id))).ToList();
        if (ports.Count == 0)
            return;

        var ds3Selectors = AnimIdMap.Ds3Selectors(ctx);
        var erSelectors = AnimIdMap.ErSelectors(ctx);
        // Elden Ring animation ID -> DS3 ID, from selectors that hold exactly one ID in each game.
        var erToDs3 = new Dictionary<int, int>();
        foreach (var (name, ds3Ids) in ds3Selectors)
            if (ds3Ids.Count == 1 && erSelectors.TryGetValue(name, out var erIds) && erIds.Count == 1)
                erToDs3.TryAdd(erIds.First(), ds3Ids.First());

        var erMain = BND4.Read(erArchives.Read("/chr/c0000.anibnd.dcx"));
        var erMainCompression = erArchives.ReadWithCompression("/chr/c0000.anibnd.dcx").Compression;
        var erAnims = BND4.Read(erArchives.Read(AnimBinder));
        var erAnimsCompression = erArchives.ReadWithCompression(AnimBinder).Compression;
        var behbnd = BND4.Read(erArchives.Read("/chr/c0000.behbnd.dcx"));
        var behbndCompression = erArchives.ReadWithCompression("/chr/c0000.behbnd.dcx").Compression;
        var behaviorFile = behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!;
        var behavior = (hkRootLevelContainer)new HavokBinarySerializer().Read(new MemoryStream(behaviorFile.Bytes));
        var graph = HavokGraph.All(behavior);
        var clips = graph.OfType<hkbClipGenerator>().ToList();
        var selectors = graph.OfType<CustomManualSelectorGenerator>().ToList();

        var ds3Main = BND4.Read(ds3Archives.Read("/chr/c0000.anibnd.dcx"));
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(BinderLeaf(ds3Main, "skeleton.hkx")!.Bytes);
        var erSkeleton = AnimPort.ReadErSkeleton(BinderLeaf(erMain, "skeleton.hkx")!.Bytes, Compendium(erMain));
        var ds3Hkx = new Ds3AnimFiles(ds3Archives);

        var usedSections = clips.Select(c => AnimName().Match(c.m_animationName ?? "")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        usedSections.UnionWith(erMain.Files.Select(f => ItemText.Leaf(f.Name)).Where(n => n.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            .Select(n => int.TryParse(n[1..^4], out var s) ? s : -1));
        int nextInternalId = clips.Max(c => c.m_animationInternalId) + 1;

        var erWeapons = er.Get("EquipParamWeapon")!;
        var ds3Weapons = ds3.Get("EquipParamWeapon")!;
        var attackPort = new AttackParamPort(er, ds3);
        // Param changes wait until every file is written, so a failure leaves the weapons on their template moveset.
        var pointWeapons = new List<Action>();

        // Weapons that share a DS3 moveset and an Elden Ring template share one new category; each keeps its own DS3
        // attack data through its behaviour variation. A weapon's art section only splits the moveset when it replaces
        // moves of its class (Friede's a232 has its own two-handed attacks); most hold just the art's own moves.
        // Many art TAEs list class moves too, but only as imports of the class's own animations; those don't count.
        var mappedDs3 = erToDs3.Values.Distinct().ToList();
        TAE.ValidateEventBank = false;
        var ds3TaeCache = new Dictionary<int, TAE?>();
        TAE? Ds3Tae(int section) => ds3TaeCache.TryGetValue(section, out var t) ? t
            : ds3TaeCache[section] = BinderLeaf(ds3Main, $"a{section:D2}.tae") is { } f ? TAE.Read(f.Bytes) : null;
        var overridesClass = new Dictionary<(int, int), bool>();
        bool Overrides(int cls, int art)
        {
            if (art <= 0 || Ds3Tae(art) is not { } artTae)
                return false;
            if (overridesClass.TryGetValue((cls, art), out var known))
                return known;
            var classOnly = new List<(int, TAE?)> { (cls, Ds3Tae(cls)) }.Where(t => t.Item2 is not null).ToList();
            var withArt = new List<(int, TAE?)> { (art, artTae) }.Concat(classOnly).ToList();
            return overridesClass[(cls, art)] = mappedDs3.Where(id => artTae.Animations.Any(a => a.ID == id))
                .Any(id => FindDs3(withArt, ds3Main, id)?.HkxName != FindDs3(classOnly, ds3Main, id)?.HkxName);
        }
        (int Ds3Section, int Ds3ArtSection, int TemplateSection) Key(WeaponPlan p, bool withArt)
        {
            int cls = Convert.ToInt32(ds3Weapons[p.Ds3Id]!["wepmotionCategory"].Value);
            int art = Convert.ToInt32(ds3Weapons[p.Ds3Id]!["spAtkCategory"].Value);
            return (cls, withArt && Overrides(cls, art) ? art : 0, Convert.ToInt32(erWeapons[p.NewId!.Value]!["wepmotionCategory"].Value));
        }
        // wepmotionCategory is one byte. Sections from 90 up keep their animations in c0000_a9x, where these go.
        var freeSections = new Queue<int>(Enumerable.Range(90, 166).Reverse().Where(s => !usedSections.Contains(s)));
        // Too few categories for every DS3 moveset, so they are handed out in this order: Friede's (Devin's first pick),
        // then each DS3 class's own moveset, then the weapons with their own moves, most shared first. A weapon whose
        // own moveset doesn't fit gets its class's: DS3's moves for its class, without the ones its art replaces.
        var own = ports.GroupBy(p => Key(p, true)).ToList();
        var classes = ports.GroupBy(p => Key(p, false)).ToList();
        var order = own.Where(g => g.Any(p => p.Ds3Id == 10180000)).Select(g => g.Key)
            .Concat(classes.OrderByDescending(g => g.Count()).Select(g => g.Key))
            .Concat(own.Where(g => g.Key.Ds3ArtSection != 0).OrderByDescending(g => g.Count()).Select(g => g.Key))
            .Distinct().Take(freeSections.Count).ToHashSet();
        var groups = ports.GroupBy(p => order.Contains(Key(p, true)) ? Key(p, true) : Key(p, false))
            .OrderByDescending(g => g.Any(p => p.Ds3Id == 10180000)).ThenByDescending(g => g.Count()).ToList();
        int portedGroups = 0, skippedGroups = 0;
        Console.WriteLine($"moveset: {ports.Count} weapons in {own.Count} DS3 movesets ({classes.Count} by class alone), " +
                          $"{freeSections.Count} free motion categories; {groups.Count(g => order.Contains(g.Key))} to build");

        foreach (var group in groups)
        {
            var (ds3Section, ds3ArtSection, templateSection) = group.Key;
            var groupName = string.Join(", ", group.Select(p => p.Name).Take(3)) + (group.Count() > 3 ? $" +{group.Count() - 3}" : "");
            if (!order.Contains(group.Key) || freeSections.Count == 0)
            {
                Console.WriteLine($"moveset: skipped {groupName}: no free motion category left");
                skippedGroups++;
                continue;
            }
            // Taken even if the port fails below: its files may then hold part of the category.
            int section = freeSections.Dequeue();
            var started = DateTime.UtcNow;
            try
            {
                // 1. Start the new category as a copy of the template's TAE.
                var templateTaeFile = BinderLeaf(erMain, $"a{templateSection:D2}.tae")
                    ?? throw new FileNotFoundException($"Elden Ring has no a{templateSection:D2}.tae");
                TAE.ValidateEventBank = false;
                var templateTae = TAE.Read(templateTaeFile.Bytes);
                var tae = TAE.Read(templateTaeFile.Bytes);
                tae.ID = 2000 + section;
                var erEventLengths = templateTae.Animations.SelectMany(a => a.Events).GroupBy(e => e.Type)
                    .ToDictionary(g => g.Key, g => g.First().GetParameterBytes(false).Length);
                var ds3Taes = new[] { ds3ArtSection, ds3Section }.Where(s => s > 0)
                    .Select(s => (Section: s, Tae: BinderLeaf(ds3Main, $"a{s:D2}.tae") is { } f ? TAE.Read(f.Bytes) : null))
                    .Where(t => t.Tae is not null).ToList();

                int converted = 0, copied = 0;
                foreach (var anim in tae.Animations)
                {
                    var erId = (int)anim.ID;
                    var templateName = $"a{templateSection:D3}_{erId:D6}";
                    var newName = $"a{section:D3}_{erId:D6}";
                    if (anim.AnimFileName is { } fileName)
                        anim.AnimFileName = fileName.Replace(templateName, newName, StringComparison.OrdinalIgnoreCase);

                    var source = erToDs3.TryGetValue(erId, out var ds3Id) ? FindDs3(ds3Taes!, ds3Main, ds3Id) : null;
                    if (source is { } s && ds3Hkx.Read(s.HkxName) is { } ds3Bytes)
                    {
                        var template = ErAnimFile(erArchives, templateName) ?? ErAnimFile(erArchives, "a000_000000")!;
                        var (hkx, _) = AnimPort.Convert(ds3Bytes, ds3Skeleton, erSkeleton, template.Bytes, template.Compendium);
                        AddFile(erAnims, newName + ".hkx", hkx);
                        anim.MiniHeader = new TAE.Animation.AnimMiniHeader.Standard();
                        anim.AnimFileName = newName + ".hkt";
                        anim.Events = MergeEvents(s.Events, anim.Events, erEventLengths);
                        converted++;
                    }
                    else if (anim.MiniHeader is TAE.Animation.AnimMiniHeader.Standard && ErAnimFile(erArchives, templateName) is { } original)
                    {
                        AddFile(erAnims, newName + ".hkx", original.Bytes);
                        copied++;
                    }
                }
                AddFile(erMain, $"a{section}.tae", tae.Write(), templateTaeFile);

                // 2. Register the category in every weapon selector: a clip per move, copied from the template's.
                int registered = 0;
                var internalIds = new Dictionary<string, short>();
                foreach (var selector in selectors.Where(IsWeaponSelector))
                {
                    foreach (var clip in selector.m_generators.OfType<hkbClipGenerator>().ToList())
                    {
                        var m = AnimName().Match(clip.m_animationName ?? "");
                        if (!m.Success || int.Parse(m.Groups[1].Value) != templateSection)
                            continue;
                        var name = $"a{section:D3}_{m.Groups[2].Value}";
                        if (!internalIds.TryGetValue(name, out var internalId))
                            internalIds[name] = internalId = checked((short)nextInternalId++);
                        var copy = HavokGraph.ShallowCopy(clip);
                        copy.m_animationName = name;
                        copy.m_name = clip.m_name?.Replace(clip.m_animationName!, name, StringComparison.Ordinal);
                        copy.m_animationInternalId = internalId;
                        selector.m_generators.Add(copy);
                        registered++;
                    }
                }

                Console.WriteLine($"moveset: {groupName} -> motion category {section} (from Elden Ring {templateSection}, DS3 {ds3Section}/{ds3ArtSection}): " +
                                  $"{converted} DS3 animations, {copied} kept from Elden Ring, {registered} behaviour clips, {(DateTime.UtcNow - started).TotalSeconds:0}s");
            }
            catch (Exception e) when (e is OverflowException or InvalidDataException or FileNotFoundException or KeyNotFoundException or InvalidCastException)
            {
                // No weapon points at the partly built category.
                Console.WriteLine($"moveset: skipped {groupName}: {e.GetType().Name}: {e.Message}");
                skippedGroups++;
                continue;
            }
            portedGroups++;

            // 3. Point every row of each weapon at the new category and its own DS3 attacks.
            foreach (var plan in group)
            {
                int ds3Variation = Convert.ToInt32(ds3Weapons[plan.Ds3Id]!["behaviorVariationId"].Value), newId = plan.NewId!.Value;
                pointWeapons.Add(() =>
                {
                    int variation = attackPort.PortVariation(ds3Variation);
                    foreach (var row in erWeapons.Rows.Where(r => r.ID >= newId && r.ID < newId + 10000))
                    {
                        row["wepmotionCategory"].Value = Convert.ChangeType(section, row["wepmotionCategory"].Value!.GetType());
                        if (variation > 0)
                            row["behaviorVariationId"].Value = Convert.ChangeType(variation, row["behaviorVariationId"].Value!.GetType());
                    }
                });
            }
        }

        // Params first: a failure there must not leave behaviour and animation files for weapons that don't use them.
        foreach (var point in pointWeapons)
            point();
        Console.WriteLine($"moveset: {portedGroups} movesets ported, {skippedGroups} skipped; attacks: {attackPort.Summary}");
        if (portedGroups == 0)
            return;
        using var output = new MemoryStream();
        new HavokBinarySerializer().Write(behavior, output);
        behaviorFile.Bytes = output.ToArray();
        File.WriteAllBytes(ctx.ModPath("chr", "c0000.behbnd.dcx"), DCX.Compress(behbnd.Write(), behbndCompression));
        File.WriteAllBytes(ctx.ModPath("chr", "c0000.anibnd.dcx"), DCX.Compress(erMain.Write(), erMainCompression));
        File.WriteAllBytes(ctx.ModPath("chr", ItemText.Leaf(AnimBinder)), DCX.Compress(erAnims.Write(), erAnimsCompression));
    }

    /// <summary>The player files Apply writes; removed when a port fails so the game loads Elden Ring's own.</summary>
    public static IEnumerable<string> OutputFiles(PorterContext ctx) =>
        new[] { "c0000.behbnd.dcx", "c0000.anibnd.dcx", ItemText.Leaf(AnimBinder) }.Select(f => ctx.ModPath("chr", f));

    private static bool IsWeaponSelector(CustomManualSelectorGenerator s) => s.m_offsetType is
        CustomManualSelectorGenerator.OffsetType.WeaponCategoryRight or CustomManualSelectorGenerator.OffsetType.WeaponCategoryLeft
        or CustomManualSelectorGenerator.OffsetType.WeaponCategoryHandStyle;

    /// <summary>DS3's events for a move, with Elden Ring's sound and effect events moved to DS3's first hit.</summary>
    private static List<TAE.Event> MergeEvents(List<TAE.Event> ds3Events, List<TAE.Event> erEvents, Dictionary<int, int> erLengths)
    {
        float ds3Hit = ds3Events.Where(e => e.Type == 1).Select(e => e.StartTime).DefaultIfEmpty(-1).Min();
        float erHit = erEvents.Where(e => e.Type == 1).Select(e => e.StartTime).DefaultIfEmpty(-1).Min();
        float shift = ds3Hit >= 0 && erHit >= 0 ? ds3Hit - erHit : 0;
        var merged = new List<TAE.Event>();
        foreach (var e in ds3Events)
        {
            var bytes = e.GetParameterBytes(false);
            if (EldenRingOnlyTypes.Contains(e.Type) || !erLengths.TryGetValue(e.Type, out var length) || length != bytes.Length)
                continue;
            merged.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, (byte[])bytes.Clone(), false));
        }
        foreach (var e in erEvents.Where(e => EldenRingOnlyTypes.Contains(e.Type)))
            merged.Add(new TAE.Event(Math.Max(0, e.StartTime + shift), Math.Max(0, e.EndTime + shift), e.Type, e.Unk04,
                (byte[])e.GetParameterBytes(false).Clone(), false) { Group = e.Group });
        return merged.OrderBy(e => e.StartTime).ToList();
    }

    private sealed record Ds3Move(string HkxName, List<TAE.Event> Events);

    /// <summary>The DS3 animation for an ID, from the first section that has it, following imports to their file.</summary>
    private static Ds3Move? FindDs3(List<(int Section, TAE? Tae)> taes, BND4 ds3Main, int id)
    {
        foreach (var (section, tae) in taes)
        {
            var anim = tae!.Animations.FirstOrDefault(a => a.ID == id);
            if (anim is null)
                continue;
            if (anim.MiniHeader is TAE.Animation.AnimMiniHeader.ImportOtherAnim import)
            {
                // Imported motion: the file named by AnimFileName (e.g. a050_030000.hkt), with the source's events
                // when the import carries hardly any of its own.
                var hkx = (anim.AnimFileName ?? "").Replace(".hkt", ".hkx", StringComparison.OrdinalIgnoreCase);
                var events = anim.Events;
                if (events.Count < 3 && AnimName().Match(hkx) is { Success: true } m
                    && BinderLeaf(ds3Main, $"a{int.Parse(m.Groups[1].Value):D2}.tae") is { } f
                    && TAE.Read(f.Bytes).Animations.FirstOrDefault(a => a.ID == int.Parse(m.Groups[2].Value)) is { } src)
                    events = src.Events;
                return string.IsNullOrEmpty(hkx) ? null : new Ds3Move(hkx, events);
            }
            return new Ds3Move($"a{section:D3}_{id:D6}.hkx", anim.Events);
        }
        return null;
    }

    private sealed record ErAnim(byte[] Bytes, byte[]? Compendium);

    private static readonly Dictionary<string, ErAnim?> ErAnimCache = new();

    /// <summary>An Elden Ring player animation file by name, searched across the c0000 anim binders.</summary>
    private static ErAnim? ErAnimFile(GameArchives archives, string name)
    {
        if (ErAnimCache.TryGetValue(name, out var cached))
            return cached;
        foreach (var entry in archives.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/chr/c0000_", StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)))
        {
            var bnd = ErBinders.TryGetValue(entry.Path!, out var b) ? b : ErBinders[entry.Path!] = BND4.Read(archives.Read(entry.Path!));
            if (BinderLeaf(bnd, name + ".hkx") is { } file)
                return ErAnimCache[name] = new ErAnim(file.Bytes, Compendium(bnd));
        }
        return ErAnimCache[name] = null;
    }

    private static readonly Dictionary<string, BND4> ErBinders = new(StringComparer.OrdinalIgnoreCase);

    private static BinderFile? BinderLeaf(BND4 bnd, string leaf) =>
        bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(leaf, StringComparison.OrdinalIgnoreCase));

    private static byte[]? Compendium(BND4 bnd) =>
        bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;

    /// <summary>Adds or replaces a file, named and flagged like the binder's other files of that kind.</summary>
    private static void AddFile(BND4 bnd, string leaf, byte[] bytes, BinderFile? like = null)
    {
        var existing = BinderLeaf(bnd, leaf);
        if (existing is not null)
        {
            existing.Bytes = bytes;
            return;
        }
        var ext = Path.GetExtension(leaf);
        like ??= bnd.Files.Last(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var folder = like.Name[..^ItemText.Leaf(like.Name).Length];
        var ids = bnd.Files.Where(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).Select(f => f.ID).ToHashSet();
        int id = ext.Equals(".tae", StringComparison.OrdinalIgnoreCase) && int.TryParse(leaf[1..^4], out var s) && !ids.Contains(like.ID - like.ID % 1000 + s)
            ? like.ID - like.ID % 1000 + s
            : bnd.Files.Max(f => f.ID) + 1;
        // The game expects a binder's files in ID order: an a208.tae appended after #9999999 hung loading.
        var file = new BinderFile(like.Flags, id, folder + leaf, bytes);
        int at = bnd.Files.FindIndex(f => f.ID > id);
        bnd.Files.Insert(at < 0 ? bnd.Files.Count : at, file);
    }

    /// <summary>Reads DS3 player animation files by name from whichever c0000 anim binder holds them.</summary>
    private sealed class Ds3AnimFiles(GameArchives archives)
    {
        private Dictionary<string, BinderFile>? _files;

        public byte[]? Read(string leaf)
        {
            _files ??= archives.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/chr/c0000", StringComparison.OrdinalIgnoreCase)
                    && e.Path.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => BND4.Read(archives.Read(e.Path!)).Files)
                .Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => ItemText.Leaf(f.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return _files.TryGetValue(leaf, out var file) ? file.Bytes : null;
        }
    }
}

/// <summary>
/// Ports the DS3 attack data a weapon's hits use: its BehaviorParam_PC rows (one per hit, by behaviour variation and
/// judge ID) and the AtkParam_Pc rows they point at, copying every field both games have by name.
/// </summary>
public sealed class AttackParamPort(ParamStore er, ParamStore ds3)
{
    private readonly PARAM _erBehavior = er.Get("BehaviorParam_PC")!, _ds3Behavior = ds3.Get("BehaviorParam_PC")!;
    private readonly PARAM _erAttack = er.Get("AtkParam_Pc")!, _ds3Attack = ds3.Get("AtkParam_Pc")!;
    private int _behaviors, _attacks;

    public string Summary => $"{_behaviors} behaviour rows, {_attacks} attack rows";

    /// <summary>Copies a DS3 behaviour variation under a new Elden Ring variation ID and returns it (0 if DS3 has none).</summary>
    public int PortVariation(int ds3Variation)
    {
        if (_variations.TryGetValue(ds3Variation, out var done))
            return done;
        return _variations[ds3Variation] = PortNewVariation(ds3Variation);
    }

    private readonly Dictionary<int, int> _variations = new();

    private int PortNewVariation(int ds3Variation)
    {
        var rows = _ds3Behavior.Rows.Where(r => Convert.ToInt32(r["variationId"].Value) == ds3Variation).ToList();
        if (ds3Variation <= 0 || rows.Count == 0)
            return 0;
        var used = _erBehavior.Rows.Select(r => Convert.ToInt32(r["variationId"].Value)).ToHashSet();
        int variation = Enumerable.Range(9000, 1000).First(v => !used.Contains(v));
        foreach (var source in rows)
        {
            int id = source.ID + (variation - ds3Variation) * 1000;
            if (_erBehavior[id] is not null)
                continue;
            var row = CopyByName(source, new PARAM.Row(id, source.Name ?? "", _erBehavior.AppliedParamdef));
            row["variationId"].Value = Convert.ChangeType(variation, row["variationId"].Value!.GetType());
            if (Convert.ToInt32(source["refType"].Value) == 0)
                row["refId"].Value = Convert.ChangeType(PortAttack(Convert.ToInt32(source["refId"].Value)), row["refId"].Value!.GetType());
            _erBehavior.Rows.Add(row);
            _behaviors++;
        }
        _erBehavior.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return variation;
    }

    private readonly Dictionary<int, int> _attackIds = new();

    private int PortAttack(int ds3Id)
    {
        if (ds3Id <= 0 || _ds3Attack[ds3Id] is not { } source)
            return ds3Id;
        if (_attackIds.TryGetValue(ds3Id, out var mapped))
            return mapped;
        int id = _erAttack[ds3Id] is null ? ds3Id : 700_000_000 + ds3Id % 100_000_000;
        while (_erAttack[id] is not null)
            id++;
        _erAttack.Rows.Add(CopyByName(source, new PARAM.Row(id, source.Name ?? "", _erAttack.AppliedParamdef)));
        _erAttack.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        _attacks++;
        return _attackIds[ds3Id] = id;
    }

    private static PARAM.Row CopyByName(PARAM.Row from, PARAM.Row to)
    {
        // Only names a paramdef uses once: unnamed padding fields share names like "0x81".
        var fields = Unique(from).ToDictionary(c => c.Def.InternalName, c => c.Value, StringComparer.Ordinal);
        foreach (var cell in Unique(to))
        {
            if (!fields.TryGetValue(cell.Def.InternalName, out var value) || value is null || cell.Value is null)
                continue;
            try
            {
                cell.Value = value is byte[] bytes ? bytes.Clone() : Convert.ChangeType(value, cell.Value.GetType());
            }
            catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException)
            {
                // Same name, different meaning or range in Elden Ring: leave the default.
            }
        }
        return to;
    }

    private static IEnumerable<PARAM.Cell> Unique(PARAM.Row row) =>
        row.Cells.GroupBy(c => c.Def.InternalName, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single());
}

/// <summary>Walks an HKLib object graph (public fields, lists) and copies objects.</summary>
public static class HavokGraph
{
    public static List<IHavokObject> All(IHavokObject root)
    {
        var seen = new HashSet<IHavokObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<IHavokObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            if (!seen.Add(obj))
                continue;
            foreach (var field in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var value = field.GetValue(obj);
                if (value is IHavokObject child)
                    stack.Push(child);
                else if (value is IList list)
                    foreach (var item in list)
                        if (item is IHavokObject listed)
                            stack.Push(listed);
            }
        }
        return seen.ToList();
    }

    public static T ShallowCopy<T>(T source) where T : class
    {
        var copy = (T)Activator.CreateInstance(source.GetType())!;
        foreach (var field in source.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(copy, field.GetValue(source));
        return copy;
    }
}
