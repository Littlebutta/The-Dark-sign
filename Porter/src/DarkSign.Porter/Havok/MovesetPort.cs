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
/// each moved to the timing of the DS3 hit it pairs with;</item>
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

    /// <summary>The last Apply's ported art names by SwordArtsParam row (its textId), for <see cref="WriteArtNames"/>.</summary>
    public static IReadOnlyDictionary<int, string> ArtNames { get; private set; } = new Dictionary<int, string>();

    /// <summary>DARKSIGN_COPY_ER_ANIMS=1 copies the template's own animations under the new category, as before, instead of importing them.</summary>
    private static readonly bool CopyErAnims = Environment.GetEnvironmentVariable("DARKSIGN_COPY_ER_ANIMS") == "1";

    /// <summary>Weapons whose motion category doesn't fit the row's byte, for the mod DLL (Mods\ER\motion-categories.txt).</summary>
    public const string WideTable = "motion-categories.txt";

    // Event types that drive the behaviour (when a combo can continue, when the move can be cancelled, charging):
    // JumpTable (0) IDs mean different things in the two games (Elden Ring's R1 chain window is JumpTable 115, which
    // DS3 doesn't have, and DS3's full-length 108 isn't Elden Ring's), so a DS3 move copied with DS3's never chained.
    // These come from the Elden Ring template, stretched onto the DS3 move's timeline between matching hits.
    private static readonly HashSet<int> BehaviourTypes = [0, 67, 608, 800];

    /// <summary>The effect port for SpEffect events in DS3 moves (set by Apply; null = DS3 IDs are left as they were).</summary>
    private static SpEffectPort? TaeEffects;

    /// <summary>TAE events that cast SpEffects: Elden Ring's parameter length and where the effect IDs are
    /// (DSAnimStudio's DS3 and ER templates: 66 AddSpEffect_Multiplayer, 67 AddSpEffect, 302 AddSpEffect_DragonForm,
    /// 331 AddSpEffect_WeaponArts with its low-FP effect, 401 AddSpEffect_Multiplayer_401).</summary>
    private static readonly Dictionary<int, (int Length, int[] Ids)> SpEffectEvents = new()
    {
        [66] = (8, [0]), [67] = (8, [0]), [302] = (16, [0]), [331] = (16, [0, 4]), [401] = (8, [0]),
    };

    /// <summary>
    /// DS3 weapons whose resident effects are not carried over, with the reason. Friede's Great Scythe: DS3's row
    /// plays 303151 (a frost mist and a blue light, always on) on the main scythe's dummy polys 300-303; in the port
    /// it read as frost on the wrong scythe (Devin, 1.0.255 playtest: the frost belongs on the second scythe's ice
    /// blade in the art, which the art's own events draw on model 1's dummy 800).
    /// </summary>
    internal static readonly Dictionary<int, string> ResidentLeftOut = new()
    {
        [10180000] = "its frost mist sits on the main scythe, where the playtest found it wrong",
    };

    private static bool KeepAllResident => Environment.GetEnvironmentVariable("DARKSIGN_RESIDENT_ALL") == "1";

    /// <summary>
    /// Gives each ported weapon (every affinity row) its DS3 resident effects under their converted IDs. A slot whose
    /// effect wasn't converted keeps what the template row had (nothing, for almost every template).
    /// </summary>
    internal static int ResidentEffects(IEnumerable<WeaponPlan> ports, PARAM ds3Weapons, PARAM erWeapons, FxrPort.Result effects)
    {
        int weapons = 0;
        foreach (var plan in ports)
        {
            if (ResidentLeftOut.TryGetValue(plan.Ds3Id, out var why) && !KeepAllResident)
            {
                Console.WriteLine($"effects: {plan.Name} keeps no DS3 effect on its model ({why}; DARKSIGN_RESIDENT_ALL=1 puts it back)");
                continue;
            }
            var slots = Ds3EffectRefs.Resident(ds3Weapons[plan.Ds3Id]!).Select(r => (r.Slot, Fxr: effects.Map(r.Fxr), r.Dummy)).Where(r => r.Fxr is not null).ToList();
            if (slots.Count == 0)
                continue;
            weapons++;
            foreach (var row in erWeapons.Rows.Where(r => r.ID >= plan.NewId!.Value && r.ID < plan.NewId.Value + 10000))
                foreach (var (slot, fxr, dummy) in slots)
                {
                    ParamDefaults.Set(row, $"residentSfxId_{slot}", fxr!.Value);
                    ParamDefaults.Set(row, $"residentSfx_DmyId_{slot}", dummy);
                }
        }
        return weapons;
    }

    /// <summary>The SpEffect IDs DS3 events cast (66 / 67 / 302 / 331 / 401).</summary>
    internal static IEnumerable<int> TaeSpEffects(IEnumerable<TAE.Event> events)
    {
        foreach (var e in events)
        {
            if (!SpEffectEvents.TryGetValue(e.Type, out var layout))
                continue;
            var bytes = e.GetParameterBytes(false);
            foreach (var offset in layout.Ids)
                if (offset + 4 <= bytes.Length && BitConverter.ToInt32(bytes, offset) is > 0 and var id)
                    yield return id;
        }
    }

    /// <summary>Effect IDs the game's own scripts look for (Elden Ring's hks GetSpEffectID checks), kept as they are.</summary>
    private static HashSet<int> EngineEffects = [];

    internal static HashSet<int> HksEffectIds(string? hks)
    {
        var ids = new HashSet<int>(Enumerable.Range(100050, 6)) { 100280 };
        if (hks is not null)
            foreach (Match m in Regex.Matches(hks, @"GetSpEffectID\s*,\s*(\d+)"))
                ids.Add(int.Parse(m.Groups[1].Value));
        return ids;
    }

    internal enum TaeEffect { Keep, Drop, Port }

    /// <summary>
    /// What becomes of a SpEffect ID a DS3 move casts (TAE 66/67/331...). Both games' scripts (hks) look for marker
    /// effects named "[HKS] ...", and number them differently: DS3's 100500 "[HKS] Non-generator Transition" (on
    /// Friede's stance attacks after their chain window) is Elden Ring's 100500 "[HKS] Sword Arts - Raptor Dodge", so a copied
    /// ID made the game dodge when a stance attack was hit (1.0.225 kept every ID Elden Ring's hks checks). A marker is
    /// kept only when both games name it the same (100280 "[HKS] Is Charging Heavy Attack"), otherwise dropped: a DS3
    /// marker means nothing to Elden Ring's scripts and a ported copy of it would do nothing either. Any other effect
    /// is kept when Elden Ring's hks checks the ID, else ported under a new ID.
    /// </summary>
    internal static TaeEffect TaeEffectFate(int id, string? ds3Name, string? erName, bool hksChecksId)
    {
        static string Norm(string? n) => Regex.Replace(n ?? "", @"\s+", " ").Trim().ToLowerInvariant();
        bool marker = Norm(ds3Name).StartsWith("[hks]", StringComparison.Ordinal);
        if (marker)
            return erName is not null && Norm(ds3Name) == Norm(erName) ? TaeEffect.Keep : TaeEffect.Drop;
        return hksChecksId ? TaeEffect.Keep : TaeEffect.Port;
    }

    /// <summary>
    /// Both games open a parry as a TAE attack event (type 1, 0x40 flag, the behaviour judge at offset 8) whose
    /// behaviour is a parry: DS3's judges are 450 (two-handed, the Farron Greatsword's a161 38400) and 600, every DS3
    /// class default variation names them "... - Parry"; Elden Ring's is 591 ("Default - Greatsword - Parry" and
    /// the like, which the game falls back to when the weapon's own variation has no 591, as for its own weapons).
    /// A DS3 parry judge copied as is found no behaviour, so the Farron Greatsword's parry parried nothing.
    /// </summary>
    internal static HashSet<int> Ds3ParryJudges = [];
    internal const int ErParryJudge = 591;

    internal static HashSet<int> ParryJudges(PARAM? ds3Behavior) =>
        ds3Behavior is null ? [] : ds3Behavior.Rows.Where(r => (r.Name ?? "").Trim() is var n && (n.Equals("Parry", StringComparison.OrdinalIgnoreCase) || n.EndsWith("- Parry", StringComparison.OrdinalIgnoreCase)))
            .Select(r => Convert.ToInt32(r["behaviorJudgeId"].Value)).ToHashSet();

    /// <summary>Rewrites a DS3 parry judge in a copied attack event (judge at offset 8) to Elden Ring's.</summary>
    internal static bool RemapParryJudge(byte[] attackEvent)
    {
        if (attackEvent.Length < 12 || !Ds3ParryJudges.Contains(BitConverter.ToInt32(attackEvent, 8)))
            return false;
        BitConverter.GetBytes(ErParryJudge).CopyTo(attackEvent, 8);
        return true;
    }

    private static int _taeEffectEvents, _bulletEvents;
    private const int BulletEvent = 2;
    /// <summary>The parameter lengths of Elden Ring's own bullet events (empty: bullets off, DS3's are not copied).</summary>
    private static HashSet<int> ErBulletLengths = [];

    /// <summary>An event group of each type from anywhere in Elden Ring's player TAE (see MergeEvents.TryPlayerGroup).</summary>
    private static Dictionary<int, TAE.EventGroup> PlayerEffectGroups = [];
    private static int _effectGroupsAdded;
    private static readonly bool Ds3JumpTables = Environment.GetEnvironmentVariable("DARKSIGN_DS3_JUMPTABLES") == "1";
    // Event types Elden Ring keeps from its own template animation: sounds, effects and other references to IDs that
    // DS3 numbers differently. Every other type comes from DS3 when both games lay it out the same.
    private static readonly HashSet<int> EldenRingOnlyTypes = [96, 112, 128, 129, 133, 139, 224, 792, 907, .. BehaviourTypes];

    [GeneratedRegex(@"^a(\d{3})_(\d{6})")]
    private static partial Regex AnimName();

    public static void Apply(PorterContext ctx, GameArchives erArchives, GameArchives ds3Archives, ParamStore er, ParamStore ds3,
        IReadOnlyList<WeaponPlan> plans, SpEffectPort? spEffects = null)
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
        var erSelectorIds = erSelectors.Values.SelectMany(v => v).ToHashSet();
        ExtendMap(erToDs3, erSelectorIds);
        AddTwoHanded(erToDs3, erSelectorIds);

        // Each binder is read once (decompressing the large animation binder took a while).
        var (erMainBytes, erMainCompression) = erArchives.ReadWithCompression("/chr/c0000.anibnd.dcx");
        var erMain = BND4.Read(erMainBytes);
        var (erAnimsBytes, erAnimsCompression) = erArchives.ReadWithCompression(AnimBinder);
        var erAnims = BND4.Read(erAnimsBytes);
        var hkxFiles = erAnims.Files.Where(f => AnimName().IsMatch(ItemText.Leaf(f.Name))).ToList();
        int byRule = hkxFiles.Count(f => AnimName().Match(ItemText.Leaf(f.Name)) is var m
            && f.ID == 1_000_000_000L + int.Parse(m.Groups[1].Value) * 1_000_000L + int.Parse(m.Groups[2].Value));
        Console.WriteLine($"moveset: {byRule} of {hkxFiles.Count} Elden Ring animations have ID 1e9 + section*1e6 + animation");
        var (behbndBytes, behbndCompression) = erArchives.ReadWithCompression("/chr/c0000.behbnd.dcx");
        var behbnd = BND4.Read(behbndBytes);
        var behaviorFile = behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Bytes.Length)!;
        var behavior = (hkRootLevelContainer)new HavokBinarySerializer().Read(new MemoryStream(behaviorFile.Bytes));
        var graph = HavokGraph.All(behavior);
        var clips = graph.OfType<hkbClipGenerator>().ToList();
        var selectors = graph.OfType<CustomManualSelectorGenerator>().ToList();

        var ds3Main = BND4.Read(ds3Archives.Read("/chr/c0000.anibnd.dcx"));
        var ds3Skeleton = AnimPort.ReadDs3Skeleton(BinderLeaf(ds3Main, "skeleton.hkx")!.Bytes);
        var erSkeleton = AnimPort.ReadErSkeleton(BinderLeaf(erMain, "skeleton.hkx")!.Bytes, Compendium(erMain));
        AnimPort.PlanRetarget(ds3Skeleton, erSkeleton);
        var ds3Hkx = new Ds3AnimFiles(ds3Archives);

        var usedSections = clips.Select(c => AnimName().Match(c.m_animationName ?? "")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        usedSections.UnionWith(erMain.Files.Select(f => ItemText.Leaf(f.Name)).Where(n => n.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            .Select(n => int.TryParse(n[1..^4], out var s) ? s : -1));
        int nextInternalId = clips.Max(c => c.m_animationInternalId) + 1;

        var erWeapons = er.Get("EquipParamWeapon")!;
        var ds3Weapons = ds3.Get("EquipParamWeapon")!;
        var attackPort = new AttackParamPort(er, ds3) { Effects = spEffects };
        BulletPort? bullets = null;
        if (BulletPort.Enabled)
            try { bullets = attackPort.Bullets = new BulletPort(er, ds3, spEffects, attackPort.PortBulletAttack); }
            catch (Exception e) when (e is InvalidDataException or FileNotFoundException)
            {
                Console.WriteLine($"bullets: off, bullet behaviours keep the template's ({e.Message})");
            }
        Ds3ParryJudges = ParryJudges(ds3.Get("BehaviorParam_PC"));
        Console.WriteLine($"moveset: DS3 parry judges {string.Join(" ", Ds3ParryJudges.Order())} -> Elden Ring {ErParryJudge}");
        TaeEffects = spEffects;
        EngineEffects = HksEffectIds(spEffects is null ? null : LoadHks(ctx));
        _taeEffectEvents = _bulletEvents = 0;
        ErBulletLengths = [];
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
        bool IsPairedPort(WeaponPlan p) => erWeapons[p.NewId!.Value]?["isDualBlade"] is { } dual && Convert.ToInt32(dual.Value) == 1;
        (int Ds3Section, int Ds3ArtSection, int TemplateSection) Key(WeaponPlan p, bool withArt)
        {
            int cls = Convert.ToInt32(ds3Weapons[p.Ds3Id]!["wepmotionCategory"].Value);
            int art = Convert.ToInt32(ds3Weapons[p.Ds3Id]!["spAtkCategory"].Value);
            return (cls, withArt && Overrides(cls, art) ? art : 0, Convert.ToInt32(erWeapons[p.NewId!.Value]!["wepmotionCategory"].Value));
        }
        // Sections 90-255 are handed out first (from 90 up the animations live in c0000_a9x, where these go), highest
        // first, then 256-399. The moveset goes in spAtkcategory, a u16 (Paramdex: "50 to 999 allowed"), and the
        // community's range for weapon moveset sections is 70-399 (Themyys, Introduction to Moveset Modding; 400-599
        // are spells, 600-999 arts), so 256-399 are ordinary numbers there. Builds up to 1.0.284 stopped at 255, which
        // left 62 numbers for 111 DS3 movesets. That limit dates from the wepmotionCategory builds: that field is one
        // byte, a section above 255 needed the mod DLL to hand it to the game (motion-categories.txt), the DLL's hook
        // never found its code, and the one build that tried closed at startup. That close, like the one of
        // 1.0.297, came from the number of animation files in c0000_a9x (ClipBudget.AnimFileLimit), not the numbers.
        // DARKSIGN_WIDE_MOTION=0 stops at 255 again; with DARKSIGN_MOVESET_FIELD=wmc the wide sections stay off
        // unless DARKSIGN_WIDE_MOTION=1.
        var wide = WideSections ? Enumerable.Range(256, UseWeaponMotionCategory ? 744 : 144) : [];
        var freeSections = new Queue<int>(Enumerable.Range(90, 166).Reverse().Concat(wide)
            .Where(s => !usedSections.Contains(s)));
        var wideCategories = new SortedDictionary<int, int>();
        // Too few categories for every DS3 moveset, so they are handed out in this order: Friede's (Devin's first pick),
        // then each DS3 class's own moveset, then the weapons with their own moves, most shared first. A weapon whose
        // own moveset doesn't fit gets its class's: DS3's moves for its class, without the ones its art replaces.
        var own = ports.GroupBy(p => Key(p, true)).ToList();
        var classes = ports.GroupBy(p => Key(p, false)).ToList();
        var wanted = own.Where(g => g.Any(p => p.Ds3Id == 10180000)).Select(g => g.Key)
            .Concat(classes.OrderByDescending(g => g.Count()).Select(g => g.Key))
            // Paired weapons next (AbsorpPort marked them isDualBlade): their art section holds their own two-handed
            // set, which their class doesn't have (the Ringed Knight Paired Greatswords two-hand with DS3 a253's moves;
            // as part of the Ultra Greatsword class moveset they two-handed like a Fume Ultra Greatsword).
            .Concat(own.Where(g => g.Key.Ds3ArtSection != 0 && g.Any(IsPairedPort)).OrderByDescending(g => g.Count()).Select(g => g.Key))
            .Concat(own.Where(g => g.Key.Ds3ArtSection != 0).OrderByDescending(g => g.Count()).Select(g => g.Key))
            .Distinct().ToList();
        // The other limit is the behaviour's animation IDs (ClipBudget): what each moveset costs as a full copy of its
        // template's category and as a lean build of its DS3 moves alone.
        var erClipMoves = new Dictionary<int, HashSet<int>>();
        foreach (var clip in selectors.Where(IsWeaponSelector).SelectMany(s => s.m_generators.OfType<hkbClipGenerator>()))
            if (AnimName().Match(clip.m_animationName ?? "") is { Success: true } m)
            {
                int clipSection = int.Parse(m.Groups[1].Value);
                if (!erClipMoves.TryGetValue(clipSection, out var clipIds))
                    erClipMoves[clipSection] = clipIds = [];
                clipIds.Add(int.Parse(m.Groups[2].Value));
            }
        var erTaeMoves = new Dictionary<int, HashSet<int>>();
        HashSet<int> ErTaeMoves(int section) => erTaeMoves.TryGetValue(section, out var known) ? known
            : erTaeMoves[section] = BinderLeaf(erMain, $"a{section:D2}.tae") is { } f ? TAE.Read(f.Bytes).Animations.Select(a => (int)a.ID).ToHashSet() : [];
        var ds3TaeMoves = new Dictionary<int, HashSet<int>>();
        bool Ds3Has(int section, int id) => section > 0 && (ds3TaeMoves.TryGetValue(section, out var known) ? known
            : ds3TaeMoves[section] = Ds3Tae(section)?.Animations.Select(a => (int)a.ID).ToHashSet() ?? []).Contains(id);
        bool canBeLean = LeanMovesets && !UseWeaponMotionCategory && !UseBothFields;
        var pairedKeys = own.Concat(classes).Where(g => g.Any(IsPairedPort)).Select(g => g.Key).ToHashSet();
        ClipBudget.Want Cost((int Ds3Section, int Ds3ArtSection, int TemplateSection) key)
        {
            var clipMoves = erClipMoves.GetValueOrDefault(key.TemplateSection) ?? [];
            if (!canBeLean)
                return new(clipMoves.Count, clipMoves.Count);
            bool paired = PairedTwoHanded && pairedKeys.Contains(key);
            bool Has(int id) => Ds3Has(key.Ds3ArtSection, id) || Ds3Has(key.Ds3Section, id);
            int lean = clipMoves.Count(erId => ErTaeMoves(key.TemplateSection).Contains(erId) && erToDs3.TryGetValue(erId, out var ds3Id)
                && (Has(ds3Id) || paired && erId is >= 32000 and < 33000 && ds3Id is >= 34000 and < 35000 && Has(ds3Id - 1000)));
            return new(clipMoves.Count, lean);
        }
        int clipIdsFree = Math.Max(0, ClipBudget.Limit - nextInternalId + 1 - ClipBudget.ArtReserve);
        // A class's moveset is only built for weapons that don't get their own; an own moveset left out sends its
        // weapons to their class's, which may then need building, so this repeats until nothing more is left out.
        var modes = new Dictionary<(int Ds3Section, int Ds3ArtSection, int TemplateSection), ClipBudget.Mode>();
        var leftOut = new HashSet<(int Ds3Section, int Ds3ArtSection, int TemplateSection)>();
        while (true)
        {
            var needed = wanted.Where(k => !leftOut.Contains(k) && (k.Ds3ArtSection != 0
                    || ports.Any(p => Key(p, false) == k && (Key(p, true).Ds3ArtSection == 0 || leftOut.Contains(Key(p, true))))))
                .ToList();
            var fit = needed.Take(freeSections.Count).ToList();
            var plan = ClipBudget.Plan(fit.Select(Cost).ToList(), clipIdsFree);
            modes = fit.Select((k, n) => (k, plan[n])).ToDictionary(x => x.k, x => x.Item2);
            var drops = needed.Where(k => k.Ds3ArtSection != 0 && modes.GetValueOrDefault(k) == ClipBudget.Mode.None).ToList();
            if (drops.Count == 0)
                break;
            leftOut.UnionWith(drops);
        }
        var order = modes.Where(m => m.Value != ClipBudget.Mode.None).Select(m => m.Key).ToHashSet();
        Console.WriteLine($"moveset: behaviour animation IDs: Elden Ring's reach {nextInternalId - 1} of {ClipBudget.Limit}, {ClipBudget.ArtReserve} kept for arts, {clipIdsFree} for movesets; " +
                          $"{modes.Count(m => m.Value == ClipBudget.Mode.Full)} movesets as full copies ({modes.Where(m => m.Value == ClipBudget.Mode.Full).Sum(m => Cost(m.Key).Full)} IDs), " +
                          $"{modes.Count(m => m.Value == ClipBudget.Mode.Lean)} lean ({modes.Where(m => m.Value == ClipBudget.Mode.Lean).Sum(m => Cost(m.Key).Lean)} IDs)" +
                          $"{(canBeLean ? "" : " (lean builds off)")}; {freeSections.Count} free motion categories{(WideSections ? "" : " (none above 255)")}, {freeSections.Count(n => n > byte.MaxValue)} of them above 255");
        foreach (var g in own.Where(g => g.Key.Ds3ArtSection != 0 && g.Any(IsPairedPort)))
            Console.WriteLine($"moveset: paired {string.Join(", ", g.Select(p => p.Name))}: own DS3 moveset {g.Key.Ds3Section}/{g.Key.Ds3ArtSection} " +
                              (order.Contains(g.Key) ? "built" : "did not fit, class moveset used"));
        var groups = ports.GroupBy(p => order.Contains(Key(p, true)) ? Key(p, true) : Key(p, false))
            .OrderByDescending(g => g.Any(p => p.Ds3Id == 10180000)).ThenByDescending(g => g.Count()).ToList();
        // DS3's effects: every effect the DS3 sections of these movesets spawn is converted under a new ID first.
        var effectsTime = Timing.Stage("effects");
        var ds3EffectFiles = FxrPort.Ds3Index(ds3Archives);
        var effectEvents = EffectEvents.Find(ds3Main, erMain, ds3EffectFiles);
        var artSections = ArtSections(ports, ds3Weapons).ToList();
        var ds3SectionEvents = groups.Where(g => order.Contains(g.Key)).SelectMany(g => new[] { g.Key.Ds3Section, g.Key.Ds3ArtSection })
            .Concat(artSections).Distinct().Where(s => s > 0).Select(Ds3Tae).Where(t => t is not null).SelectMany(t => t!.Animations.SelectMany(a => a.Events)).ToList();
        var wantedEffects = effectEvents.Ids(ds3SectionEvents).ToHashSet();
        // The effects that aren't in a move's own effect events: what the weapons' SpEffects look like (worn, on hit,
        // cast by a move or a behaviour) and what their bullets show. These rows are ported as the moves convert, after
        // the effects, so DS3's params are followed here first to know which effects to convert.
        var effectRefs = new Ds3EffectRefs(ds3);
        var weaponRefs = effectRefs.Fresh();
        foreach (var p in ports)
            weaponRefs.AddWeapon(ds3Weapons[p.Ds3Id]!);
        foreach (var id in TaeSpEffects(ds3SectionEvents))
            weaponRefs.AddSpEffect(id);
        if (spEffects?.Visuals is not null)
            wantedEffects.UnionWith(weaponRefs.VisualFxr);
        if (bullets is not null)
            wantedEffects.UnionWith(weaponRefs.BulletFxr);
        if (SpEffectPort.VisualsEnabled)
            wantedEffects.UnionWith(weaponRefs.ResidentFxr);
        Console.WriteLine($"effects: {wantedEffects.Count} DS3 effects wanted: move events, {weaponRefs.VisualFxr.Count} shown by {weaponRefs.SpEffects.Count} SpEffects" +
                          $"{(spEffects?.Visuals is null ? " (visuals off)" : "")}, {weaponRefs.BulletFxr.Count} by {weaponRefs.Bullets.Count} bullets{(bullets is null ? " (bullets off)" : "")}, " +
                          $"{weaponRefs.ResidentFxr.Count} on weapon models{(SpEffectPort.VisualsEnabled ? "" : " (off)")}");
        // DS3's map effects (Firelink's candles, fog, light shafts): port-maps places them as SFX regions once they
        // are converted here, into the same common binder (MapSfx).
        Maps.MapSfx.ClearTable(ctx);
        var mapEffects = new SortedSet<int>();
        if (Maps.MapSfx.Enabled)
            try
            {
                var notes = new List<string>();
                mapEffects = Maps.MapSfx.Wanted(ds3Archives, Maps.MapSlots.All, notes);
                notes.ForEach(Console.WriteLine);
                wantedEffects.UnionWith(mapEffects);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"map effects: DS3 map files not read ({e.GetType().Name}: {e.Message}); the maps get no DS3 effects");
            }
        FxrPort.Result effects;
        try { effects = FxrPort.Apply(ctx, erArchives, ds3EffectFiles, wantedEffects); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Moves keep Elden Ring's effect events.
            Console.WriteLine($"effects: failed, moves keep Elden Ring effects ({e.GetType().Name}: {e.Message})");
            effects = new FxrPort.Result();
        }
        if (mapEffects.Count > 0)
            try { Console.WriteLine(Maps.MapSfx.WriteTable(ctx, mapEffects, effects)); }
            catch (Exception e) when (e is not OutOfMemoryException) { Console.WriteLine($"map effects: table not written ({e.Message}); the maps get no DS3 effects"); }
        // The visual rows ported so far (the weapons' worn and on-hit effects) and every one ported from here on get
        // their converted effects.
        spEffects?.BindVisuals(effects);
        bullets?.Bind(effects);
        // The effects a DS3 weapon plays on its own model while held (the Dancer's Enchanted Swords' fire and dark, the
        // Demon's Scar's flame blade), on the same dummy polys of the ported model.
        if (SpEffectPort.VisualsEnabled)
            pointWeapons.Add(() => Console.WriteLine($"effects: {ResidentEffects(ports, ds3Weapons, erWeapons, effects)} weapons play DS3 effects on their model"));
        effectsTime.Dispose();

        // DS3's sounds, the same way: every character and effect sound the sections play, under new IDs.
        var soundsTime = Timing.Stage("sounds");
        var erMainEvents = erMain.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => { try { return TAE.Read(f.Bytes).Animations.SelectMany(a => a.Events).ToList(); } catch (Exception) { return []; } }).ToList();
        var soundSample = erMainEvents.Where(e => e.Type == 128).Select(e => e.GetParameterBytes(false))
            .GroupBy(Convert.ToHexString).MaxBy(g => g.Count())?.First();
        var templateSounds = erMainEvents.Select(SoundPort.Sound).Where(s => s is { Kind: 1 }).GroupBy(s => s!.Value.Id)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(50).ToList();
        PlayerEffectGroups = erMainEvents.Where(e => e.Group is not null).GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First().Group!);
        _effectGroupsAdded = 0;
        if (bullets is not null)
            ErBulletLengths = erMainEvents.Where(e => e.Type == BulletEvent).Select(e => e.GetParameterBytes(false).Length).ToHashSet();
        Console.WriteLine($"bullets: Elden Ring bullet event lengths {string.Join(" ", ErBulletLengths.Order())}, DS3's in these movesets " +
                          string.Join(" ", ds3SectionEvents.Where(e => e.Type == BulletEvent).GroupBy(e => e.GetParameterBytes(false).Length).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}")));
        SoundPort.Result sounds;
        try
        {
            sounds = soundSample is null ? new SoundPort.Result()
                : SoundPort.Apply(ctx, erArchives, ds3Archives, ds3SectionEvents.Select(SoundPort.Sound).OfType<(int, int)>()
                    .Concat(effects.Sounds.Select(id => (5, id))), templateSounds, effects.UnheldSounds); // 5: s-sounds, the ones effects play
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Moves keep Elden Ring's sounds.
            Console.WriteLine($"sounds: failed, moves keep Elden Ring sounds ({e.GetType().Name}: {e.Message})");
            sounds = new SoundPort.Result();
        }
        soundsTime.Dispose();
        if (sounds.InOwnBank)
            LoadOwnBank(er);
        // The most common Elden Ring character swing sound (129, kind 1): the layout for ported sounds in moves whose
        // template has no 129 of its own (128 isn't used for them: see SoundEvents).
        var swingSample = erMainEvents.Where(e => e.Type == 129 && SoundPort.Sound(e) is { Kind: 1 }).Select(e => e.GetParameterBytes(false))
            .Where(b => b.Length >= 32).GroupBy(Convert.ToHexString).MaxBy(g => g.Count())?.First();
        var soundPort = new SoundEvents(sounds, soundSample, swingSample);

        int portedGroups = 0, skippedGroups = 0, clipFixes = 0;
        var timing = new List<string> { "section\tweapons\tmove\tDS3 animation\tDS3 length\tElden Ring length\tDS3 last hit\tDS3 first exit\tported first exit\twindows held back\tanchors" };
        int? timeActVariable = null;
        Console.WriteLine($"moveset: {ports.Count} weapons in {own.Count} DS3 movesets ({classes.Count} by class alone), " +
                          $"{freeSections.Count} free motion categories; {groups.Count(g => order.Contains(g.Key))} to build");

        // Weapons whose own moveset found no free category are built as their class. That is said here, for every
        // weapon (it used to be printed for paired weapons only, so a missing own move had no trace in the log).
        var unfit = ports.Where(p => Key(p, true).Ds3ArtSection != 0 && !order.Contains(Key(p, true))).ToList();
        if (unfit.Count > 0)
            Console.WriteLine($"moveset: {unfit.Count} weapons' own DS3 moves did not fit a free motion category and use their class's moveset: " +
                              string.Join(", ", unfit.Select(p => $"{p.Name} (a{Key(p, true).Ds3ArtSection})")));
        // Their projectiles are kept all the same, see WithOwnBullets below.
        var spareJudges = new Queue<int>(BorrowBullets && bullets is not null ? attackPort.FreeJudges() : []);
        var borrowed = new Dictionary<(int Art, int Judge), int>();
        int borrowedEvents = 0, borrowedRows = 0;

        // The player's animation binder holds a limited number of files (ClipBudget.AnimFileLimit). Movesets keep
        // their own converted files while there is room; after that a move whose DS3 animation an earlier moveset
        // already converted on the same Elden Ring template plays that file (the TAE entry keeps its own events and
        // names the other animation as its file, as Elden Ring's own sections do: Standard header, ImportsHKX).
        int animFileBudget = ClipBudget.AnimFileLimit - ClipBudget.ArtFileReserve;
        var convertedFiles = new Dictionary<(string Ds3File, string Template), (string Name, int AnimId, float ErDuration)>();
        // The same DS3 animation converted on another Elden Ring template: the motion is DS3's either way (the
        // template gives the file its layout and the pose of bones DS3 doesn't animate), used when the exact one isn't there.
        var convertedAnyTemplate = new Dictionary<string, (string Name, int AnimId, float ErDuration)>(StringComparer.OrdinalIgnoreCase);
        int sharedMovesets = 0, sharedMoves = 0;

        foreach (var group in groups)
        {
            var (ds3Section, ds3ArtSection, templateSection) = group.Key;
            // A class moveset shared with weapons whose own section did not fit: those sections.
            var unfitArts = ds3ArtSection == 0 ? group.Select(p => Key(p, true).Ds3ArtSection).Where(a => a != 0).Distinct().ToList() : [];
            // The projectile a weapon's own section fires from a move that plays the class's animation (the Moonlight
            // Greatsword's a158 replays the greatsword heavies a025_030320 ... with the wave's bullet event added) is
            // added to the class move. It gets a behaviour judge no weapon in either game uses, and only the weapons
            // of that section get a row for it (AliasJudge), so the other weapons sharing the moveset fire nothing.
            Ds3Move WithOwnBullets(Ds3Move source, int ds3Id)
            {
                List<TAE.Event>? added = null;
                string file = source.FileName ?? source.HkxName;
                foreach (var art in unfitArts)
                {
                    if (FindDs3([(art, Ds3Tae(art))], ds3Main, ds3Id) is not { } own
                        || !string.Equals(own.FileName ?? own.HkxName, file, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var e in own.Events.Where(e => e.Type == BulletEvent))
                    {
                        var bytes = e.GetParameterBytes(false);
                        if (bytes.Length < 12)
                            continue;
                        int judge = BitConverter.ToInt32(bytes, 8);
                        if (source.Events.Any(c => c.Type == BulletEvent && c.GetParameterBytes(false) is { Length: >= 12 } cb && BitConverter.ToInt32(cb, 8) == judge))
                            continue;
                        if (!borrowed.TryGetValue((art, judge), out var alias))
                        {
                            if (spareJudges.Count == 0)
                                continue;
                            alias = borrowed[(art, judge)] = spareJudges.Dequeue();
                        }
                        var copy = (byte[])bytes.Clone();
                        BitConverter.GetBytes(alias).CopyTo(copy, 8);
                        (added ??= []).Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, copy, false));
                        borrowedEvents++;
                    }
                }
                return added is null ? source : source with { Events = [.. source.Events, .. added] };
            }
            var groupName = string.Join(", ", group.Select(p => p.Name).Take(3)) + (group.Count() > 3 ? $" +{group.Count() - 3}" : "");
            if (!order.Contains(group.Key) || freeSections.Count == 0)
            {
                Console.WriteLine($"moveset: skipped {groupName}: no free motion category left");
                skippedGroups++;
                continue;
            }
            // Taken even if the port fails below: its files may then hold part of the category.
            int section = freeSections.Dequeue();
            // A lean build holds DS3's moves only (ClipBudget): the rest play from the row's own motion category.
            bool lean = modes.GetValueOrDefault(group.Key) == ClipBudget.Mode.Lean;
            int firstClipId = nextInternalId;
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
                var erEventLengths = EventLengths(templateTae.Animations.SelectMany(a => a.Events));
                // An event group of each type anywhere in the template's TAE, for DS3 events whose type this move's
                // Elden Ring template doesn't use (they were dropped: e.g. a DS3 move with an extra effect or model event).
                var templateGroups = templateTae.Animations.SelectMany(a => a.Events).Where(e => e.Group is not null)
                    .GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First().Group!);
                var ds3Taes = new[] { ds3ArtSection, ds3Section }.Where(s => s > 0)
                    .Select(s => (Section: s, Tae: BinderLeaf(ds3Main, $"a{s:D2}.tae") is { } f ? TAE.Read(f.Bytes) : null))
                    .Where(t => t.Tae is not null).ToList();

                int converted = 0, copied = 0, dualMoves = 0;
                // A paired weapon's two-handed attacks are DS3's dual-wield set (033xxx), not its 034xxx two-handed
                // set: see PairedTwoHanded.
                bool pairedGroup = PairedTwoHanded && group.Any(IsPairedPort);
                // Each converted move's stretch onto the DS3 timeline, for its behaviour clips below.
                var stretches = new Dictionary<int, MoveTiming.Stretch>();
                // Elden Ring's own imports show the ID scheme (section * 1,000,000 + ID) and the unknown field.
                var imports = tae.Animations.Select(a => (Anim: a, Import: a.MiniHeader as TAE.Animation.AnimMiniHeader.ImportOtherAnim))
                    .Where(x => x.Import is not null).ToList();
                int importUnknown = imports.FirstOrDefault().Import?.Unknown ?? -1;
                foreach (var (a, imp) in imports.Take(1))
                    Console.WriteLine($"moveset: template import example: a{templateSection:D3}_{a.ID:D6} imports {imp!.ImportFromAnimID} (file {a.AnimFileName}, unknown {imp.Unknown})");
                // First pass, in order: find each move's DS3 source and Elden Ring template (lookups fill shared caches).
                var anims = tae.Animations.ToList();
                var jobs = new (Ds3Move? Source, byte[]? Ds3Bytes, ErAnim? Template)[anims.Count];
                var templateNames = new string?[anims.Count];
                var sharedFiles = new (string Name, int AnimId, float ErDuration)?[anims.Count];
                for (int i = 0; i < anims.Count; i++)
                {
                    var erId = (int)anims[i].ID;
                    var source = erToDs3.TryGetValue(erId, out var ds3Id) ? FindDs3(ds3Taes!, ds3Main, ds3Id) : null;
                    if (pairedGroup && erId is >= 32000 and < 33000 && ds3Id is >= 34000 and < 35000
                        && FindDs3(ds3Taes!, ds3Main, ds3Id - 1000) is { } dual)
                    {
                        source = dual;
                        ds3Id -= 1000;
                        dualMoves++;
                    }
                    if (source is not null && unfitArts.Count > 0 && spareJudges.Count + borrowed.Count > 0)
                        source = WithOwnBullets(source, ds3Id);
                    var ds3File = source is { } found ? ds3Hkx.Read(found.HkxName) : null;
                    if (group.Any(p => p.Ds3Id == 10180000) && erToDs3.ContainsKey(erId))
                        Console.WriteLine($"moveset: Friede's {erId} <- DS3 {ds3Id}: " +
                                          (source is null ? "not in its DS3 sections" : ds3File is null ? $"{source.HkxName} NOT FOUND" : $"{source.HkxName}, {source.Events.Count} events"));
                    ErAnim? template = null;
                    if (source is not null && ds3File is not null)
                    {
                        var ownTemplate = ErAnimFile(erArchives, $"a{templateSection:D3}_{erId:D6}");
                        template = ownTemplate ?? ErAnimFile(erArchives, "a000_000000")!;
                        templateNames[i] = ownTemplate is null ? "a000_000000" : $"a{templateSection:D3}_{erId:D6}";
                    }
                    jobs[i] = (source, ds3File, template);
                }
                // Room for a file of its own per DS3 move? Otherwise the moves another moveset already converted
                // play that moveset's file.
                int ownFiles = jobs.Count(j => j is { Source: not null, Ds3Bytes: not null });
                int movesShared = 0;
                if (erAnims.Files.Count + ownFiles > animFileBudget)
                    for (int i = 0; i < anims.Count; i++)
                        if (jobs[i] is { Source: { } src, Ds3Bytes: not null }
                            && (convertedFiles.TryGetValue((src.HkxName, templateNames[i]!), out var done) || convertedAnyTemplate.TryGetValue(src.HkxName, out done)))
                        {
                            sharedFiles[i] = done;
                            jobs[i].Template = null; // no conversion
                            movesShared++;
                        }
                if (erAnims.Files.Count + ownFiles - movesShared > animFileBudget)
                    throw new InvalidOperationException($"the player's animation binder has no room for its {ownFiles - movesShared} animations " +
                                                        $"({erAnims.Files.Count} files, {animFileBudget} allowed before the weapon arts)");
                // Second pass: the animation conversions, on every core. A failure is kept and thrown in order below,
                // so a bad animation skips this moveset exactly as it did when the conversions ran one by one.
                var convertedHkx = new (byte[]? Hkx, float ErDuration, Exception? Error)[anims.Count];
                Parallel.For(0, anims.Count, Parallelism.Options, i =>
                {
                    if (jobs[i] is not { Ds3Bytes: { } bytes, Template: { } template })
                        return;
                    try
                    {
                        var (converted, _, erDuration) = AnimPort.ConvertTimed(bytes, ds3Skeleton, erSkeleton, template.Bytes, template.Compendium, AnimPort.Spline);
                        convertedHkx[i] = (converted, erDuration, null);
                    }
                    catch (Exception e)
                    {
                        convertedHkx[i] = (null, 0, e);
                    }
                });

                for (int i = 0; i < anims.Count; i++)
                {
                    var anim = anims[i];
                    var erId = (int)anim.ID;
                    var templateName = $"a{templateSection:D3}_{erId:D6}";
                    var newName = $"a{section:D3}_{erId:D6}";
                    if (anim.AnimFileName is { } fileName)
                        anim.AnimFileName = fileName.Replace(templateName, newName, StringComparison.OrdinalIgnoreCase);

                    if (jobs[i] is { Source: { } s, Ds3Bytes: { } ds3Bytes })
                    {
                        float erDuration;
                        if (sharedFiles[i] is { } shared)
                        {
                            anim.MiniHeader = new TAE.Animation.AnimMiniHeader.Standard { ImportsHKX = true, ImportHKXSourceAnimID = shared.AnimId };
                            anim.AnimFileName = shared.Name + ".hkt";
                            erDuration = shared.ErDuration;
                        }
                        else
                        {
                            if (convertedHkx[i].Error is { } error)
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                            var hkx = convertedHkx[i].Hkx
                                      ?? throw new InvalidDataException($"no Elden Ring animation to use as the template for {newName}");
                            AddFile(erAnims, newName + ".hkx", hkx);
                            anim.MiniHeader = new TAE.Animation.AnimMiniHeader.Standard();
                            anim.AnimFileName = newName + ".hkt";
                            erDuration = convertedHkx[i].ErDuration;
                            convertedFiles.TryAdd((s.HkxName, templateNames[i]!), (newName, section * 1_000_000 + erId, erDuration));
                            convertedAnyTemplate.TryAdd(s.HkxName, (newName, section * 1_000_000 + erId, erDuration));
                        }
                        float ds3Duration = AnimPort.Duration(ds3Bytes);
                        anim.Events = MergeEvents(s.Events, anim, templateGroups, erEventLengths, effectEvents, effects, soundPort, ds3Duration, out var stretch);
                        stretches[erId] = stretch;
                        timing.Add(TimingLine(section, groupName, erId, s, ds3Duration, erDuration, stretch));
                        converted++;
                    }
                    else if (lean)
                        continue;
                    else if (anim.MiniHeader is TAE.Animation.AnimMiniHeader.Standard && ErAnimFile(erArchives, templateName) is { } original)
                    {
                        if (CopyErAnims)
                            AddFile(erAnims, newName + ".hkx", original.Bytes);
                        else
                        {
                            // Elden Ring's own move: import the template's animation (motion and events), as Elden
                            // Ring's own categories do, instead of a copy. 4,087 copies made the binder 4 times vanilla.
                            anim.MiniHeader = new TAE.Animation.AnimMiniHeader.ImportOtherAnim
                            {
                                ImportFromAnimID = templateSection * 1_000_000 + erId,
                                Unknown = importUnknown,
                            };
                            anim.AnimFileName = templateName + ".hkt";
                        }
                        copied++;
                    }
                }
                if (movesShared > 0)
                {
                    sharedMovesets++;
                    sharedMoves += movesShared;
                }
                if (lean)
                {
                    if (converted == 0)
                        throw new InvalidDataException("none of its DS3 moves pairs with a move of the Elden Ring template");
                    tae.Animations.RemoveAll(a => !stretches.ContainsKey((int)a.ID));
                }
                AddFile(erMain, $"a{section}.tae", tae.Write(), templateTaeFile);

                // 2. Register the category in every weapon selector: a clip per move, copied from the template's
                // (a lean build: per DS3 move).
                timeActVariable ??= graph.OfType<hkbBehaviorGraphStringData>().Select(d => d.m_variableNames.IndexOf("TimeActEditor_10"))
                    .FirstOrDefault(i => i >= 0, -1);
                int registered = 0;
                var internalIds = new Dictionary<string, short>();
                foreach (var selector in selectors.Where(IsWeaponSelector))
                {
                    foreach (var clip in selector.m_generators.OfType<hkbClipGenerator>().ToList())
                    {
                        var m = AnimName().Match(clip.m_animationName ?? "");
                        if (!m.Success || int.Parse(m.Groups[1].Value) != templateSection)
                            continue;
                        if (lean && !stretches.ContainsKey(int.Parse(m.Groups[2].Value)))
                            continue;
                        var name = $"a{section:D3}_{m.Groups[2].Value}";
                        if (!internalIds.TryGetValue(name, out var internalId))
                            internalIds[name] = internalId = checked((short)nextInternalId++);
                        var copy = HavokGraph.ShallowCopy(clip);
                        if (JumpBinding && int.Parse(m.Groups[2].Value) % 100 == 70)
                            BindStartTime(copy, timeActVariable);
                        copy.m_animationName = name;
                        copy.m_name = clip.m_name?.Replace(clip.m_animationName!, name, StringComparison.Ordinal);
                        copy.m_animationInternalId = internalId;
                        if (stretches.TryGetValue(int.Parse(m.Groups[2].Value), out var moveStretch))
                            clipFixes += FitClip(copy, moveStretch);
                        selector.m_generators.Add(copy);
                        registered++;
                    }
                }

                Console.WriteLine($"moveset: {groupName} -> motion category {section} (from Elden Ring {templateSection}, DS3 {ds3Section}/{ds3ArtSection}): " +
                                  $"{converted} DS3 animations, " +
                                  (lean ? $"lean (every other move plays from Elden Ring's {templateSection}), " : $"{copied} kept from Elden Ring ({(CopyErAnims ? "copied" : "imported")}), ") +
                                  (movesShared > 0 ? $"{movesShared} of them play another moveset's file, " : "") +
                                  $"{registered} behaviour clips, {nextInternalId - firstClipId} animation IDs, {erAnims.Files.Count} files in the binder, {(DateTime.UtcNow - started).TotalSeconds:0}s" +
                                  (pairedGroup ? $"; paired: {dualMoves} two-handed moves from DS3's dual-wield set (033xxx)" : ""));
            }
            // Any failure (HKLib, a converter's NullReference or IndexOutOfRange, ...) skips this moveset only: it used
            // to escape for anything but these types, which dropped every weapon's moveset and the effects and sounds.
            catch (Exception e) when (e is not OutOfMemoryException)
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
                int templateVariation = Convert.ToInt32(erWeapons[newId]!["behaviorVariationId"].Value);
                int ownArt = ds3ArtSection == 0 ? Key(plan, true).Ds3ArtSection : 0;
                pointWeapons.Add(() =>
                {
                    int variation = attackPort.PortVariation(ds3Variation, templateVariation);
                    foreach (var ((_, judge), alias) in borrowed.Where(b => b.Key.Art == ownArt && ownArt != 0))
                        if (attackPort.AliasJudge(variation, judge, alias))
                            borrowedRows++;
                    if (UseWeaponMotionCategory && section > byte.MaxValue)
                        wideCategories[newId] = section; // the row keeps its template's category for the DLL to replace
                    foreach (var row in erWeapons.Rows.Where(r => r.ID >= newId && r.ID < newId + 10000))
                        PointRow(row, section, variation, sounds.Count > 0);
                });
            }
        }

        // Params first: a failure there must not leave behaviour and animation files for weapons that don't use them.
        foreach (var point in pointWeapons)
            point();
        // DS3 weapon arts, as Ashes of War of their own (MovesetPort.Arts.cs).
        ArtResult artResult;
        using (Timing.Stage("weapon arts"))
        {
            try
            {
                artResult = PortArts(ctx, erArchives, ds3Archives, er, ds3, ports, erMain, erAnims, ds3Main, Ds3Tae, ds3Hkx, ds3Skeleton, erSkeleton,
                    selectors, ref nextInternalId, effectEvents, effects, soundPort, timing);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"arts: failed, ported weapons keep Elden Ring Ashes of War ({e.GetType().Name}: {e.Message})");
                // Where it failed (a PARAM row indexed by a field name its def lacks throws this same "no matching
                // element"), so the next log names the line.
                foreach (var line in (e.StackTrace ?? "").Split('\n').Take(8))
                    Console.WriteLine("  " + line.Trim());
                artResult = new ArtResult(0, 0, [], []);
            }
        }
        ArtNames = artResult.Names;
        var timingReport = ctx.WorkPath("reports", "moveset-timing.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(timingReport)!);
        File.WriteAllLines(timingReport, timing);
        int moves = timing.Count - 1, held = timing.Skip(1).Count(l => l.Split('\t')[9] != "0"), early = timing.Skip(1).Count(l => l.EndsWith("\tEARLY"));
        Console.WriteLine($"moveset: timing guard {(MoveTiming.Guard ? "on" : "off (1.0.89 timing)")}: {moves} DS3 moves, {held} had windows held back to DS3's own recovery, " +
                          $"{clipFixes} behaviour clip limits fitted, {early} still exit before DS3 does; per move: {timingReport}");
        Console.WriteLine($"moveset: animation files: {erAnims.Files.Count} in {ItemText.Leaf(AnimBinder)} of {ClipBudget.AnimFileLimit} the game takes; " +
                          $"{sharedMovesets} movesets play {sharedMoves} moves from another moveset's file (no room for a file of their own)");
        Console.WriteLine($"moveset: event switches: DS3 effect events {Ds3EffectEvents}, DS3 sound events {Ds3SoundEvents}, swing align {SwingAlign}");
        Console.WriteLine($"moveset: {portedGroups} movesets ported, {skippedGroups} skipped; attacks: {attackPort.Summary}; {_taeEffectEvents} DS3 SpEffect events with ported effects; " +
                          $"{wideCategories.Count} weapons on categories above 255 (mod DLL)");
        Console.WriteLine($"effects: {_effectGroupsAdded} moves got an effect event group their template section has none of (DS3 effects there were dropped before)");
        if (bullets is not null)
            Console.WriteLine($"bullets: {bullets.Summary}; {_bulletEvents} DS3 bullet events copied into the moves");
            Console.WriteLine($"bullets: {borrowedEvents} projectile events of weapons built as their class were added to the class moves under {borrowed.Count} spare judges " +
                              $"({string.Join(" ", borrowed.OrderBy(b => b.Value).Select(b => $"a{b.Key.Art}:{b.Key.Judge}={b.Value}"))}), {borrowedRows} behaviour rows added for them");
            Console.WriteLine($"bullets: {attackPort.BulletBaseAttacks} bullet attacks take the weapon's attack power (percentages only in DS3)");
        if (spEffects?.Visuals is { } visualRows)
            Console.WriteLine($"effect visuals: {visualRows.Summary}");
        try
        {
            EffectCoverage.Write(ctx, ports, ds3Weapons, Ds3Tae, effectEvents, effectRefs, effects, spEffects?.Visuals is not null, bullets is not null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine($"effect coverage: report failed ({e.GetType().Name}: {e.Message})");
        }
        var table = ctx.ModPath(WideTable);
        if (wideCategories.Count > 0)
            File.WriteAllLines(table, new[] { "# weapon base row\tmotion category, read by dksign_testmenu.dll" }
                .Concat(wideCategories.Select(e => $"{e.Key}\t{e.Value}")));
        else
            File.Delete(table);
        if (portedGroups == 0 && artResult.Arts == 0)
            return;
        using var output = new MemoryStream();
        new HavokBinarySerializer().Write(behavior, output);
        behaviorFile.Bytes = output.ToArray();
        using (Timing.Stage("moveset files"))
        {
            // Compressed side by side: each binder is independent.
            Parallel.Invoke(
                () => File.WriteAllBytes(ctx.ModPath("chr", "c0000.behbnd.dcx"), DCX.Compress(behbnd.Write(), behbndCompression)),
                () => File.WriteAllBytes(ctx.ModPath("chr", "c0000.anibnd.dcx"), DCX.Compress(erMain.Write(), erMainCompression)),
                () => File.WriteAllBytes(ctx.ModPath("chr", ItemText.Leaf(AnimBinder)), DCX.Compress(erAnims.Write(), erAnimsCompression)));
        }
    }

    /// <summary>
    /// DARKSIGN_ANIM_MAP (launcher fallback "anim-map"): how Elden Ring moves find their DS3 animation beyond the
    /// behaviour selectors that hold exactly one ID in each game (the default, which left moves such as charged heavies
    /// on Elden Ring's animation when a selector holds several IDs or the two games name it differently).
    /// "offsets": an unpaired Elden Ring ID takes the ID offset its paired neighbours in the same block of 100 share
    /// (Elden Ring 0305xx -> DS3 0303xx when most of that block's pairs differ by the same amount).
    /// "table": offsets, plus the pairs found by hand while porting Friede's scythe (heavy 030500/030501/030505 = DS3
    /// 030320/030330/030321; two-handed moves are DS3 034xxx = Elden Ring 032xxx), which win over everything else.
    /// A pair is only used when the weapon's DS3 sections have that animation, so a wrong guess keeps Elden Ring's move.
    /// </summary>
    public static readonly string AnimMap = (Environment.GetEnvironmentVariable("DARKSIGN_ANIM_MAP") ?? "").ToLowerInvariant();

    internal static readonly (int Er, int Ds3)[] HandPairs = [(30500, 30320), (30501, 30330), (30505, 30321)];

    /// <summary>Adds pairs to <paramref name="erToDs3"/> per <see cref="AnimMap"/>; <paramref name="erIds"/> are every
    /// Elden Ring ID the weapon selectors play.</summary>
    internal static void ExtendMap(Dictionary<int, int> erToDs3, IReadOnlySet<int> erIds, string? mode = null)
    {
        mode ??= AnimMap;
        if (mode is not ("offsets" or "table"))
            return;
        int before = erToDs3.Count;
        // The usual offset in each block of 100 (Elden Ring ID / 100), from the pairs the selectors gave.
        var blockOffset = erToDs3.GroupBy(p => p.Key / 100)
            .Select(g => (Block: g.Key, Best: g.GroupBy(p => p.Value - p.Key).OrderByDescending(o => o.Count()).First(), Total: g.Count()))
            .Where(b => b.Best.Count() * 2 > b.Total) // a clear majority, else the block is left alone
            .ToDictionary(b => b.Block, b => b.Best.Key);
        foreach (var er in erIds.Where(id => !erToDs3.ContainsKey(id)).Order())
            if (blockOffset.TryGetValue(er / 100, out var offset) && er + offset > 0)
                erToDs3[er] = er + offset;
        if (mode == "table")
            foreach (var (er, ds3) in HandPairs)
            {
                erToDs3[er] = ds3;
                if (erIds.Contains(er + 2000)) // two-handed: Elden Ring 032xxx, DS3 034xxx
                    erToDs3[er + 2000] = ds3 + 4000;
            }
        Console.WriteLine($"moveset: anim map '{mode}' added {erToDs3.Count - before} pairs to the {before} from behaviour selectors");
    }

    // DARKSIGN_TWOHAND_MAP=0: two-handed moves are paired by behaviour selector names only, as before.
    private static readonly bool TwoHandMap = Environment.GetEnvironmentVariable("DARKSIGN_TWOHAND_MAP") != "0";

    /// <summary>
    /// A paired weapon's two-handed attacks come from DS3's dual-wield set. In DS3 a paired weapon (Ringed Knight Paired
    /// Greatswords, Farron Greatsword, Sellsword Twinblades, fists) two-hands into its L1 dual attacks, which live in
    /// its own section as 033xxx (a253_033000/010/020, run 033500, roll 033900); its 034xxx two-handed entries only import
    /// the class's one-handed swings (a253_034000 -> a026_030000). Elden Ring plays a paired weapon's (isDualBlade)
    /// dual attacks through the ordinary two-handed selectors, 032xxx. So, in a paired weapon's moveset, Elden Ring
    /// 032yyy takes DS3 033yyy wherever the DS3 sections have it (the offsets inside the block are the same as the
    /// 034xxx pairing's), else the 034xxx pair as before. Without this (builds up to 1.0.217) the Ringed Knight pair
    /// and the Farron Greatsword two-handed like a one-handed Ultra Greatsword. DARKSIGN_PAIRED_2H=0 turns it off.
    /// </summary>
    private static readonly bool PairedTwoHanded = Environment.GetEnvironmentVariable("DARKSIGN_PAIRED_2H") != "0";

    /// <summary>
    /// Two-handed moves the selector names didn't pair: each one-handed pair (Elden Ring 030xxx = DS3 030xxx) gives
    /// its two-handed twin, Elden Ring 032xxx = DS3 034xxx (found on Friede's scythe: DS3's two-handed R1 chain is
    /// 034000/034010/034020, Elden Ring's 032000/032010/032020). Without it two-handed swings kept the Elden Ring
    /// template's animations (the Ringed Knight Paired Greatswords two-handed like an Elden Ring greatsword). As with
    /// every pair, a weapon whose DS3 sections lack the animation keeps Elden Ring's move.
    /// </summary>
    internal static int AddTwoHanded(Dictionary<int, int> erToDs3, IReadOnlySet<int> erIds)
    {
        if (!TwoHandMap)
            return 0;
        int added = 0;
        foreach (var (er, ds3) in erToDs3.Where(p => p.Key is >= 30000 and < 31000 && p.Value is >= 30000 and < 31000).ToList())
            if (erIds.Contains(er + 2000) && erToDs3.TryAdd(er + 2000, ds3 + 4000))
                added++;
        Console.WriteLine($"moveset: {added} two-handed moves paired through their one-handed twins (Elden Ring 032xxx = DS3 034xxx)");
        return added;
    }

    /// <summary>
    /// DARKSIGN_MOVESET_FIELD=wmc puts the new section in wepmotionCategory, as builds up to 1.0.1xx did. By default it
    /// goes in spAtkcategory ("Special Motion Category"), the way Elden Ring's own unique movesets and the community's
    /// custom movesets do it (Themyys, Introduction to Moveset Modding): every animation in that section overrides the
    /// weapon motion category's. With the section only in wepmotionCategory, the template's own special section (e.g.
    /// a weapon's unique heavies) still overrode the ported DS3 moves, so R2 kept looking like Elden Ring. The row
    /// keeps its template's wepmotionCategory, which Elden Ring also uses for class rules (powerstance, guard, ...).
    /// </summary>
    public static readonly bool UseWeaponMotionCategory =
        string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_MOVESET_FIELD"), "wmc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DARKSIGN_MOVESET_FIELD=both: the section goes in spAtkcategory and, when it fits the byte, in wepmotionCategory
    /// too, so whichever field the game reads for a move finds the DS3 moveset. Elden Ring's class rules that follow
    /// wepmotionCategory (powerstance, guard) then come from the new section, which starts as a copy of the template's.
    /// </summary>
    public static readonly bool UseBothFields =
        string.Equals(Environment.GetEnvironmentVariable("DARKSIGN_MOVESET_FIELD"), "both", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether motion categories above 255 are handed out (see Apply). On by default with the moveset in
    /// spAtkcategory, where DARKSIGN_WIDE_MOTION=0 (fallback "moveset-capacity" B) stops at 255 as builds up to
    /// 1.0.284 did; with DARKSIGN_MOVESET_FIELD=wmc they need the mod DLL and stay off unless DARKSIGN_WIDE_MOTION=1.
    /// </summary>
    internal static bool WideSections => Environment.GetEnvironmentVariable("DARKSIGN_WIDE_MOTION") is var v
        && (UseWeaponMotionCategory ? v == "1" : v != "0");

    /// <summary>Fallback "moveset-capacity" C: DARKSIGN_LEAN_MOVESETS=0 builds full copies only (ClipBudget); a
    /// moveset the animation IDs have no room for is left out and its weapons use their class's.</summary>
    private static readonly bool LeanMovesets = Environment.GetEnvironmentVariable("DARKSIGN_LEAN_MOVESETS") != "0";

    /// <summary>Points one EquipParamWeapon row at its ported section and DS3 attack variation.</summary>
    public static void PointRow(PARAM.Row row, int section, int variation, bool ds3Sounds)
    {
        if (UseWeaponMotionCategory || UseBothFields)
        {
            if (section <= byte.MaxValue)
                row["wepmotionCategory"].Value = Convert.ChangeType(section, row["wepmotionCategory"].Value!.GetType());
        }
        if (!UseWeaponMotionCategory)
            row["spAtkcategory"].Value = Convert.ChangeType(section, row["spAtkcategory"].Value!.GetType());
        if (variation > 0)
            row["behaviorVariationId"].Value = Convert.ChangeType(variation, row["behaviorVariationId"].Value!.GetType());
        // Elden Ring adds wepSeIdOffset (0-99) to a weapon's sound IDs so one TAE serves weapons with different
        // whooshes. The ported events are named after the exact DS3 sound (Play_c9xxxxxxxx), so any offset copied from
        // the template makes the game ask for an event that isn't in the bank, and the swing stays silent.
        if (ds3Sounds)
            row["wepSeIdOffset"].Value = Convert.ChangeType(0, row["wepSeIdOffset"].Value!.GetType());
    }

    /// <summary>
    /// DARKSIGN_JUMP_BINDING=1 (launcher fallback "jump-attacks" B): every new clip of an animation whose ID ends in 70
    /// gets its own variable binding set with its Start Time bound to the behaviour variable TimeActEditor_10, as
    /// Themyys' moveset guide says jump attacks need (else they play again on landing). By default a new clip shares
    /// the template clip's binding set, which is the same binding when the template has it.
    /// </summary>
    private static readonly bool JumpBinding = Environment.GetEnvironmentVariable("DARKSIGN_JUMP_BINDING") == "1";

    private static int _jumpBindings;

    private static void BindStartTime(hkbClipGenerator clip, int? variable)
    {
        if (variable is not >= 0)
            return;
        var set = new hkbVariableBindingSet { m_indexOfBindingToEnable = clip.m_variableBindingSet?.m_indexOfBindingToEnable ?? -1 };
        foreach (var b in clip.m_variableBindingSet?.m_bindings ?? [])
            if (b.m_memberPath != "startTime")
                set.m_bindings.Add(new hkbVariableBindingSet.Binding
                    { m_memberPath = b.m_memberPath, m_variableIndex = b.m_variableIndex, m_bitIndex = b.m_bitIndex, m_bindingType = b.m_bindingType });
        set.m_bindings.Add(new hkbVariableBindingSet.Binding
        {
            m_memberPath = "startTime", m_variableIndex = variable.Value, m_bitIndex = -1,
            m_bindingType = hkbVariableBindingSet.Binding.BindingType.BINDING_TYPE_VARIABLE,
        });
        clip.m_variableBindingSet = set;
        if (_jumpBindings++ == 0)
            Console.WriteLine($"moveset: jump attack clips get Start Time bound to TimeActEditor_10 (variable {variable})");
    }

    /// <summary>The player files Apply writes; removed when a port fails so the game loads Elden Ring's own.</summary>
    public static IEnumerable<string> OutputFiles(PorterContext ctx) =>
        new[] { "c0000.behbnd.dcx", "c0000.anibnd.dcx", ItemText.Leaf(AnimBinder) }.Select(f => ctx.ModPath("chr", f))
            .Append(ctx.ModPath(WideTable))
            // The ported effects and sounds name IDs only the ported moves play; a failed moveset drops them too, so a
            // binder or bank from an earlier build (or game patch) doesn't keep replacing Elden Ring's own.
            .Concat(FxrPort.StaleBinders(ctx)).Append(ctx.ModPath("sd", "enus", "cs_main.bnk"));

    private static bool IsWeaponSelector(CustomManualSelectorGenerator s) => s.m_offsetType is
        CustomManualSelectorGenerator.OffsetType.WeaponCategoryRight or CustomManualSelectorGenerator.OffsetType.WeaponCategoryLeft
        or CustomManualSelectorGenerator.OffsetType.WeaponCategoryHandStyle;

    // TAE 712 OverrideWeaponModelLocations, same layout in both games (DSAnimStudio templates): 4 right-hand models then
    // 4 left-hand models, each (u8 AbsorpPosParamCondition, s8 ChangeType), then IsEnabledWhile1Handing / 2Handing R / L.
    private const int OverrideWeaponModelLocations = 712;
    // ChangeType 5 is "Friede's Scythe Override", hardcoded in DS3's engine by weapon ID: it is how Friede's art takes the
    // second scythe off the back into the left hand. Elden Ring doesn't know the ported weapon's ID, so the event did
    // nothing. DARKSIGN_FRIEDE_OVERRIDE=keep leaves it as DS3 wrote it; see FixModelOverride for the other modes.
    /// <summary>
    /// Turns DS3's Friede-only ChangeType 5 (TAE 712 OverrideWeaponModelLocations, per slot: AbsorpPosParamCondition
    /// byte, ChangeType byte) into a move Elden Ring knows. Elden Ring's own player animations move a right-hand
    /// model 1 with two ChangeTypes only (every c0000 TAE section counted): 2, "the model's two-handed location"
    /// (Rellana's a918 and the Starscourge Greatsword's a913 bring the second blade out one-handed: slot RH1
    /// condition 2, ChangeType 2), and 3, "the model's hang location" (a612's art sends model 1 there: slot RH1
    /// condition 2, ChangeType 3; the paired arts put the blade back with it). Condition 2 matches a model shown only
    /// in use (wepInvisibleType 2), which Friede's model 1 is in DS3's row and ours.
    /// Default ("art"): ChangeType 3, and AbsorpPort makes model 1's hang slot the left hand, so the second scythe
    /// stays on the back until the art (a612's bytes exactly). "both" (the previous build): ChangeType 2, with AbsorpPort's
    /// Elden Ring paired layout (left hand two-handed and in the art). "back" (1.0.221-1.0.238): ChangeType 1
    /// ("1H left location", left_1 = left hand), which no Elden Ring move uses on a right-hand model and never showed
    /// the scythe; a left-hand model's slot goes the other way (ChangeType 0). "keep": untouched. The condition byte
    /// stays DS3's (2) in every mode.
    /// </summary>
    internal static int FixModelOverride(byte[] bytes) => FixModelOverride(bytes, AbsorpPort.FriedeMode);

    internal static int FixModelOverride(byte[] bytes, string mode)
    {
        if (mode == "keep" || bytes.Length < 16)
            return 0;
        int fixedCount = 0;
        for (int slot = 0; slot < 8; slot++)
            if ((sbyte)bytes[2 * slot + 1] == 5)
            {
                bytes[2 * slot + 1] = mode switch
                {
                    "back" => slot < 4 ? (byte)1 : (byte)0,
                    "both" => 2,
                    _ => 3,
                };
                fixedCount++;
            }
        return fixedCount;
    }

    /// <summary>DS3's events for a move, with Elden Ring's sound and effect events moved to DS3's first hit.</summary>
    private static List<TAE.Event> MergeEvents(List<TAE.Event> ds3Events, TAE.Animation anim, Dictionary<int, TAE.EventGroup> templateGroups,
        Dictionary<int, HashSet<int>> erLengths, EffectEvents effectEvents, FxrPort.Result effects, SoundEvents sounds, float ds3Duration, out MoveTiming.Stretch stretch)
    {
        var erEvents = anim.Events;
        var merged = new List<TAE.Event>();
        // Elden Ring files events in event groups (one per type), and its TAE skips events outside them: ported
        // sounds and effects never played. A converted or copied DS3 event joins the group Elden Ring's event of its
        // type is in, in this move (sounds: 128, else 129). A type this move has no group for gets a copy of the
        // group another move of the template uses for it; a type the template never uses isn't added.
        var groupOf = erEvents.Where(e => e.Group is not null).GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First().Group);
        anim.EventGroups ??= [];
        bool TryGroup(int type, out TAE.EventGroup? group)
        {
            if (groupOf.TryGetValue(type, out group))
                return true;
            if (!AddGroups || !templateGroups.TryGetValue(type, out var proto))
                return false;
            group = proto.GetClone();
            anim.EventGroups.Add(group);
            groupOf[type] = group;
            return true;
        }
        // An effect event in a move whose whole template section has no event of that type (Elden Ring's stance art
        // a614 plays no effect at all, so every effect of a DS3 stance built on it was dropped: Friede's ice blade,
        // 440501, showed only in the two chain moves built on a612). It takes a copy of the group any move of the
        // player's TAE files that type under. DARKSIGN_ADD_EVENT_GROUPS=0 turns this off with the template's groups.
        bool TryPlayerGroup(int type, out TAE.EventGroup? group)
        {
            group = null;
            if (!AddGroups || !PlayerEffectGroups.TryGetValue(type, out var proto))
                return false;
            group = proto.GetClone();
            anim.EventGroups.Add(group);
            groupOf[type] = group;
            _effectGroupsAdded++;
            return true;
        }
        // Effect types this move now spawns DS3's own effects with; Elden Ring's of those types are dropped.
        var ds3Effects = new HashSet<int>();
        bool ds3Sounds = false;
        // Elden Ring plays a weapon's swing sounds with PlaySound at a dummy poly (129: kind, ID, dummy poly 100, ...),
        // not with 128: the ported sounds use the move's own 129 character sound as their layout when it has one.
        var erSwing = erEvents.FirstOrDefault(e => e.Type == 129 && e.Group is not null && SoundPort.Sound(e) is { Kind: 1 });
        foreach (var e in ds3Events)
        {
            if (Ds3SoundEvents && sounds.Convert(e, erSwing) is { } sound
                && (erSwing?.Group ?? (sound.Type == 129 ? groupOf.GetValueOrDefault(129) ?? groupOf.GetValueOrDefault(128)
                    : groupOf.GetValueOrDefault(128) ?? groupOf.GetValueOrDefault(129))) is { } soundGroup)
            {
                sound.Group = soundGroup;
                merged.Add(sound);
                ds3Sounds = true;
                continue;
            }
            if (Ds3EffectEvents && effectEvents.IsEffect(e.Type))
            {
                if (effectEvents.Convert(e, effects) is { } converted && (TryGroup(e.Type, out var effectGroup) || TryPlayerGroup(e.Type, out effectGroup)))
                {
                    merged.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, converted, false) { Group = effectGroup });
                    ds3Effects.Add(e.Type);
                }
                continue;
            }
            var bytes = e.GetParameterBytes(false);
            // SpEffects a move casts: 66 / 67 (DS3 16 bytes, Elden Ring 8), 302, 401, and 331, the weapon art's buff
            // with its not-enough-FP twin (Perseverance 120750 / 120760). DS3's IDs name nothing in Elden Ring, or an
            // unrelated effect (DS3's Stomp 6340 is something else there), so each is ported (SpEffectPort) under a new
            // ID. Only the engine's IDs, the ones Elden Ring's hks checks (charge 100280, art combo windows 100050-100055,
            // ...), stay as they are. An event whose effect can't be ported is left out.
            if (TaeEffects is { } fx && SpEffectEvents.TryGetValue(e.Type, out var fxLayout))
            {
                var fxBytes = new byte[fxLayout.Length];
                bool any = false;
                foreach (var offset in fxLayout.Ids)
                {
                    int ds3Effect = offset + 4 <= bytes.Length ? BitConverter.ToInt32(bytes, offset) : -1;
                    int mapped = ds3Effect <= 0 ? ds3Effect : TaeEffectFate(ds3Effect, fx.Ds3Name(ds3Effect), fx.ErName(ds3Effect), EngineEffects.Contains(ds3Effect)) switch
                    {
                        TaeEffect.Keep => ds3Effect,
                        TaeEffect.Drop => -1,
                        _ => fx.Port(ds3Effect) is > 0 and var portedId ? portedId : -1,
                    };
                    BitConverter.GetBytes(mapped).CopyTo(fxBytes, offset);
                    any |= offset == fxLayout.Ids[0] && mapped > 0;
                }
                if (any)
                {
                    if (!TryGroup(e.Type, out var fxGroup))
                    {
                        fxGroup = new TAE.EventGroup(e.Type);
                        anim.EventGroups.Add(fxGroup);
                        groupOf[e.Type] = fxGroup;
                    }
                    merged.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, fxBytes, false) { Group = fxGroup });
                    _taeEffectEvents++;
                }
                continue;
            }
            // A move that fires a bullet (type 2: dummy poly, behaviour judge): the weapon's ported behaviour row for
            // that judge names the ported DS3 bullet. The generic rule below only copies a type the template's own
            // section uses, and a plain weapon class never fires anything, so every DS3 projectile event was dropped
            // there (the Moonlight Greatsword's waves on a greatsword template). Both games lay the event out the
            // same; it is copied when Elden Ring has bullet events of the same length anywhere in the player's TAE.
            if (e.Type == BulletEvent && ErBulletLengths.Contains(bytes.Length))
            {
                if (!TryGroup(e.Type, out var bulletGroup))
                {
                    bulletGroup = new TAE.EventGroup(e.Type);
                    anim.EventGroups.Add(bulletGroup);
                    groupOf[e.Type] = bulletGroup;
                }
                merged.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, BulletPort.ConvertEvent(bytes), false) { Group = bulletGroup });
                _bulletEvents++;
                continue;
            }
            if (ErOwned(e.Type) || !erLengths.TryGetValue(e.Type, out var lengths) || !lengths.Contains(bytes.Length))
                continue;
            if (!TryGroup(e.Type, out var group))
                continue;
            var copy = (byte[])bytes.Clone();
            if (e.Type == OverrideWeaponModelLocations)
                FixModelOverride(copy);
            if (e.Type == 1)
                RemapParryJudge(copy);
            merged.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, copy, false) { Group = group });
        }

        // Each kept Elden Ring event follows the hit it belongs to (the nearest one): hits are paired in order, spread
        // evenly when the two games swing a different number of times, so later swings keep their own whooshes.
        var ds3Hits = HitStarts(ds3Events);
        var erHits = HitStarts(erEvents);
        if (!SwingAlign)
        {
            // As first ported: every kept event moves with the first hit.
            ds3Hits = [.. ds3Hits.Take(1)];
            erHits = [.. erHits.Take(1)];
        }
        // A move that plays DS3's own character and effect sounds drops Elden Ring's (footsteps and the rest stay).
        var kept = erEvents.Where(e => (ErOwned(e.Type) && (KeepErEffects || !ds3Effects.Contains(e.Type))
                                        || KeepErEffects && effectEvents.IsEffect(e.Type) && e.Group is not null)
                                       && !(ds3Sounds && SoundPort.Sound(e) is { } s && SoundPort.Letter(s.Kind) is not null)).ToList();
        int Pair(int erHit) => erHits.Count == 1 ? 0 : (int)Math.Round(erHit * (ds3Hits.Count - 1) / (double)(erHits.Count - 1));
        var pairedDs3 = new HashSet<int>();
        // Behaviour windows are stretched, not shifted: anchors at the start, each paired hit, the shared recovery
        // cancels and the end; see MoveTiming for the rules that keep them from cutting the DS3 swing short.
        stretch = MoveTiming.Build(ds3Events, erEvents, ds3Hits, erHits, Pair, ds3Duration);
        var moveStretch = stretch;
        // The charge window (67) names the same charge in both games (100280 for a heavy attack): where DS3's move has
        // one with the same ID it keeps DS3's own timing, so holding R2 charges where the DS3 swing winds up.
        static int? FirstInt(TAE.Event e) => e.GetParameterBytes(false) is { Length: >= 4 } b ? BitConverter.ToInt32(b, 0) : null;
        var ds3Charge = ds3Events.Where(e => e.Type == 67 && FirstInt(e) is not null).GroupBy(e => FirstInt(e)!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartTime).ToList());
        var chargeUsed = new HashSet<int>();
        foreach (var e in kept.Where(e => BehaviourTypes.Contains(e.Type) && !Ds3JumpTables))
        {
            if (e.Type == 67 && FirstInt(e) is { } charge && ds3Charge.TryGetValue(charge, out var own))
            {
                if (chargeUsed.Add(charge))
                    foreach (var d in own)
                        merged.Add(new TAE.Event(d.StartTime, d.EndTime, 67, e.Unk04, (byte[])e.GetParameterBytes(false).Clone(), false) { Group = e.Group });
                continue;
            }
            var (start, end) = moveStretch.Place(e);
            merged.Add(new TAE.Event(start, end, e.Type, e.Unk04, (byte[])e.GetParameterBytes(false).Clone(), false) { Group = e.Group });
        }
        foreach (var e in kept.Where(e => !BehaviourTypes.Contains(e.Type) || Ds3JumpTables))
        {
            float shift = 0;
            if (ds3Hits.Count > 0 && erHits.Count > 0)
            {
                int hit = NearestHit(erHits, e.StartTime), target = Pair(hit);
                pairedDs3.Add(target);
                shift = ds3Hits[target] - erHits[hit];
            }
            merged.Add(Shifted(e, shift));
        }

        // DS3 swings with no Elden Ring hit of their own (e.g. a 3-hit combo on a 2-hit template) repeat the
        // swing sounds of the nearest paired hit. Only when the move plays Elden Ring's swing sounds: with DS3's own
        // (every DS3 sound event is ported, one per DS3 swing), what is left of Elden Ring's (kinds other than c and
        // s) was copied once per extra DS3 hitbox, so a swing with several hitboxes played them several times
        // (1.0.347: the Dragonslayer Greataxe; 261 moves, +4,187 events). DARKSIGN_SWING_SOUND_REPEAT=1 repeats them
        // anyway, as before.
        if (erHits.Count > 0 && (!ds3Sounds || SwingSoundRepeat))
            for (int d = 0; d < ds3Hits.Count; d++)
            {
                if (pairedDs3.Contains(d))
                    continue;
                int source = Enumerable.Range(0, erHits.Count).MinBy(h => Math.Abs(Pair(h) - d));
                foreach (var e in kept.Where(e => SwingTypes.Contains(e.Type) && NearestHit(erHits, e.StartTime) == source))
                    merged.Add(Shifted(e, ds3Hits[d] - erHits[source]));
            }
        return merged.OrderBy(e => e.StartTime).ToList();
    }

    /// <summary>
    /// Sounds method E: Torrent's NpcParam rows name the ported sounds' own bank in SoundAddBankId, so the game keeps
    /// it loaded (Yonder's "Custom Soundbanks" guide: Torrent's rows 8002-8005 from Elden Ring 1.17, 8000 before; a
    /// row that already names a bank keeps it).
    /// </summary>
    private static void LoadOwnBank(ParamStore er)
    {
        if (er.Get("NpcParam") is not { } npcs)
        {
            Console.WriteLine("sounds: method E: no NpcParam; the own bank won't be loaded");
            return;
        }
        var set = new List<int>();
        foreach (var row in npcs.Rows.Where(r => r.ID is >= 8000 and <= 8005))
        {
            if (row.Cells.FirstOrDefault(c => c.Def.InternalName == "SoundAddBankId") is not { } cell || System.Convert.ToInt32(cell.Value) >= 0)
                continue;
            cell.Value = System.Convert.ChangeType(SoundPort.OwnBankNumber, cell.Value!.GetType());
            set.Add(row.ID);
        }
        Console.WriteLine($"sounds: method E: Torrent's NpcParam rows {(set.Count == 0 ? "(none free)" : string.Join(", ", set))} load {SoundPort.OwnBankName}");
    }

    /// <summary>
    /// The one-shot form of Wwise_PlaySound_BySlot (129) ported sounds use unless DARKSIGN_SOUND_ONESHOT=0:
    /// PlaybackBehaviorType 0 (play until the sound ends; DS3's sound events last a frame or two, and type 1 cuts the
    /// sound off when the event ends) and no slot (-1). Nightreign Ultimate Voices' new Play_c9xxxxxxxx events stayed
    /// silent as 128s and in a cut-off layout, and played once written as the game's own one-shot 129s.
    /// </summary>
    private static readonly bool OneShot = Environment.GetEnvironmentVariable("DARKSIGN_SOUND_ONESHOT") != "0";

    /// <summary>DS3 sound events as Elden Ring PlaySound events for the ported sounds: in the layout of the move's
    /// own swing sound (129) when it has one, else of Elden Ring's most common character 129 (else 128).</summary>
    private sealed class SoundEvents(SoundPort.Result sounds, byte[]? sample, byte[]? swingSample)
    {
        public TAE.Event? Convert(TAE.Event e, TAE.Event? erSwing)
        {
            int type = erSwing?.Type ?? (OneShot && swingSample is not null ? 129 : 128);
            var layout = erSwing?.GetParameterBytes(false) ?? (type == 129 ? swingSample : sample);
            if (layout is null || SoundPort.Sound(e) is not { } s || SoundPort.Letter(s.Kind) is null || sounds.Map(s.Kind, s.Id) is not { } id)
                return null;
            var bytes = (byte[])layout.Clone();
            BitConverter.GetBytes(s.Kind).CopyTo(bytes, 0);
            BitConverter.GetBytes(id).CopyTo(bytes, 4);
            // Wwise_PlaySound_BySlot (129; Smithbox's ER TAE defs): the template swing's StateInfo, its override sound
            // (+20 type, +24 ID) and its SoundIDOffsetType (+28; 2 adds the weapon's wepSeIdOffset) all belong to the
            // Elden Ring whoosh. The community's copied sound events clear StateInfo; the override is set to the
            // ported sound itself so either ID the game picks is ours, and -1 means no weapon offset.
            if (type == 129 && bytes.Length >= 32)
            {
                BitConverter.GetBytes((short)0).CopyTo(bytes, 16);
                BitConverter.GetBytes(s.Kind).CopyTo(bytes, 20);
                BitConverter.GetBytes(id).CopyTo(bytes, 24);
                bytes[28] = unchecked((byte)-1);
                if (OneShot)
                {
                    BitConverter.GetBytes(-1).CopyTo(bytes, 12); // SlotID: none
                    // PlaybackBehaviorType: until the sound ends. A sound that loops never ends by itself, so it
                    // gets the type Elden Ring's own long sound events have (1: the sound ends with the event;
                    // inferred from those events and the field's name, not observed in game).
                    bytes[18] = sounds.Loops(s.Kind, s.Id) ? (byte)1 : (byte)0;
                }
            }
            return new TAE.Event(e.StartTime, e.EndTime, type, e.Unk04, bytes, false);
        }
    }

    // Switches for finding which part of the event port Elden Ring rejects; all on unless set to 0.
    private static readonly bool Ds3EffectEvents = Environment.GetEnvironmentVariable("DARKSIGN_DS3_EFFECT_EVENTS") != "0";
    /// <summary>
    /// DARKSIGN_KEEP_ER_EFFECTS=1 (launcher fallback "effects" C): a move that spawns DS3's effects keeps the
    /// template's Elden Ring effects of the same types too, so a DS3 effect that doesn't show leaves Elden Ring's
    /// trail and sparks rather than nothing.
    /// </summary>
    private static readonly bool KeepErEffects = Environment.GetEnvironmentVariable("DARKSIGN_KEEP_ER_EFFECTS") == "1";
    private static readonly bool Ds3SoundEvents = Environment.GetEnvironmentVariable("DARKSIGN_DS3_SOUND_EVENTS") != "0";
    private static readonly bool SwingAlign = Environment.GetEnvironmentVariable("DARKSIGN_SWING_ALIGN") != "0";
    private static readonly bool SwingSoundRepeat = Environment.GetEnvironmentVariable("DARKSIGN_SWING_SOUND_REPEAT") == "1";
    private static readonly bool AddGroups = Environment.GetEnvironmentVariable("DARKSIGN_ADD_EVENT_GROUPS") != "0";

    // Sound events repeated for extra DS3 swings.
    private static readonly HashSet<int> SwingTypes = [128, 129];

    // Start of each swing: hit windows starting within 0.1 s of the previous one are the same swing (several hitboxes).
    private static List<float> HitStarts(List<TAE.Event> events)
    {
        var starts = new List<float>();
        foreach (var t in events.Where(e => e.Type == 1).Select(e => e.StartTime).Order())
            if (starts.Count == 0 || t - starts[^1] > 0.1f)
                starts.Add(t);
        return starts;
    }

    /// <summary>One row of Work\reports\moveset-timing.tsv; ends in EARLY when the ported move can still be left
    /// before DS3's own first exit after its last hit.</summary>
    private static string TimingLine(int section, string weapons, int erId, Ds3Move source, float ds3Duration, float erDuration, MoveTiming.Stretch st)
    {
        var hits = HitStarts(source.Events);
        static string F(float? v) => v is { } x ? x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "";
        var exit = st.Recovery ?? MoveTiming.Recovery(source.Events, hits);
        bool early = exit is { } r && st.FirstExit is { } f && f < r - 0.001f;
        return string.Join('\t', section, weapons, erId, source.HkxName, F(ds3Duration), F(erDuration), F(hits.Count > 0 ? hits[^1] : null),
            F(exit), F(st.FirstExit), st.Held,
            string.Join(' ', st.Anchors.Select(a => $"{F(a.Er)}>{F(a.Ds3)}"))) + (early ? "\tEARLY" : "");
    }

    /// <summary>
    /// A converted move's behaviour clip is a copy of the template's, whose limits were set for the Elden Ring
    /// animation: an enforced duration plays the DS3 animation in the Elden Ring move's time, and a crop cuts its
    /// end off. Both are cleared, and trigger times fixed on the Elden Ring timeline are moved onto DS3's (triggers
    /// measured from the clip's end stay). Returns 1 when anything changed. Off with DARKSIGN_TIMING_GUARD=0.
    /// </summary>
    private static int FitClip(hkbClipGenerator clip, MoveTiming.Stretch stretch)
    {
        if (!MoveTiming.Guard)
            return 0;
        bool changed = clip.m_enforcedDuration > 0 || clip.m_cropStartAmountLocalTime != 0 || clip.m_cropEndAmountLocalTime != 0;
        clip.m_enforcedDuration = 0;
        clip.m_cropStartAmountLocalTime = 0;
        clip.m_cropEndAmountLocalTime = 0;
        if (clip.m_triggers is { m_triggers.Count: > 0 } triggers && triggers.m_triggers.Any(t => !t.m_relativeToEndOfClip && t.m_localTime > 0))
        {
            // The array is shared with the template's clip: the copy gets its own.
            clip.m_triggers = new hkbClipTriggerArray
            {
                m_triggers = triggers.m_triggers.Select(t => new hkbClipTrigger
                {
                    m_localTime = t.m_relativeToEndOfClip ? t.m_localTime : stretch.Warp(t.m_localTime),
                    m_event = t.m_event, m_relativeToEndOfClip = t.m_relativeToEndOfClip, m_acyclic = t.m_acyclic, m_isAnnotation = t.m_isAnnotation,
                }).ToList(),
            };
            changed = true;
        }
        return changed ? 1 : 0;
    }

    private static int NearestHit(List<float> hits, float time) =>
        Enumerable.Range(0, hits.Count).MinBy(h => Math.Abs(hits[h] - time));

    /// <summary>
    /// Every parameter length the template section's events of each type have. A DS3 event is copied as it is only
    /// when Elden Ring writes its type at that length, and Elden Ring writes some types at two: the FP charge of a
    /// weapon art (330, all zeros) is 16 bytes like DS3's in most moves and 24 in a few (Unsheathe's stance light
    /// a614 40060, Rellana's a918). Builds up to 1.0.276 kept only the first length met, so on the stance template
    /// a614 that was 24 and every DS3 FP charge (16) was dropped: no attack of a ported stance art cost any FP.
    /// </summary>
    internal static Dictionary<int, HashSet<int>> EventLengths(IEnumerable<TAE.Event> events) =>
        events.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Select(e => e.GetParameterBytes(false).Length).ToHashSet());

    private static bool ErOwned(int type) => EldenRingOnlyTypes.Contains(type) && !(Ds3JumpTables && BehaviourTypes.Contains(type));

    private static TAE.Event Shifted(TAE.Event e, float shift) =>
        new(Math.Max(0, e.StartTime + shift), Math.Max(0, e.EndTime + shift), e.Type, e.Unk04,
            (byte[])e.GetParameterBytes(false).Clone(), false) { Group = e.Group };

    /// <summary>Fallback "weapon-visuals" H: DARKSIGN_BORROW_BULLETS=0 leaves a weapon built as its class without the
    /// projectiles of its own section, as in builds up to 1.0.282.</summary>
    private static readonly bool BorrowBullets = Environment.GetEnvironmentVariable("DARKSIGN_BORROW_BULLETS") != "0";

    /// <summary>A DS3 move: its animation file, events, and the file its TAE entry names (when that differs).</summary>
    private sealed record Ds3Move(string HkxName, List<TAE.Event> Events, string? FileName = null);

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
            var named = (anim.AnimFileName ?? "").Replace(".hkt", ".hkx", StringComparison.OrdinalIgnoreCase);
            return new Ds3Move($"a{section:D3}_{id:D6}.hkx", anim.Events, AnimName().IsMatch(named) ? named : null);
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
            // Animations: Elden Ring's IDs are 1,000,000,000 + section * 1,000,000 + animation (a090_030500 is
            // 1090030500), and the game finds them by that ID: under a made-up ID they never played.
            : ext.Equals(".hkx", StringComparison.OrdinalIgnoreCase) && AnimName().Match(leaf) is { Success: true } a
              && 1_000_000_000L + int.Parse(a.Groups[1].Value) * 1_000_000L + int.Parse(a.Groups[2].Value) is var animId and <= int.MaxValue
              && !ids.Contains((int)animId)
                ? (int)animId
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
public sealed class AttackParamPort(ParamStore er, ParamStore ds3, string behaviorParam = "BehaviorParam_PC", string attackParam = "AtkParam_Pc")
{
    // Weapons use the player's params; the enemy port (Enemies/ChrPort.cs) passes BehaviorParam and AtkParam_Npc.
    private readonly PARAM _erBehavior = er.Get(behaviorParam)!, _ds3Behavior = ds3.Get(behaviorParam)!;
    private readonly PARAM _erAttack = er.Get(attackParam)!, _ds3Attack = ds3.Get(attackParam)!;
    private int _behaviors, _attacks, _effectBehaviors;

    // Fallback weapon-hits: A (unset) DS3 attack fields over the template's attack, B "ds3" DS3 fields only (unnamed
    // Elden Ring fields at their defaults), C "template" the template weapon's own attack row for each judge DS3 has.
    private static readonly string? AttackMode = Environment.GetEnvironmentVariable("DARKSIGN_ATTACKS");
    private static bool AttacksFromTemplate => AttackMode == "template";
    private static bool AttacksDs3Only => AttackMode == "ds3";

    /// <summary>When set, DS3 SpEffect references (behaviours that cast an effect, attacks that apply one) are ported
    /// with it instead of standing in the template's or being dropped.</summary>
    public SpEffectPort? Effects { get; init; }

    /// <summary>When set, DS3 bullet references (behaviours that fire a projectile) are ported with it instead of
    /// standing in the template's or being dropped.</summary>
    public BulletPort? Bullets { get; set; }

    /// <summary>When set, the effects a hit applies are ported in full (an enemy's curse or slow), not only the
    /// status build-up a weapon's hit carries.</summary>
    public bool FullHitEffects { get; init; }

    public string Summary => $"{_behaviors} behaviour rows ({_templateBehaviors} more from the templates, {_effectBehaviors} casting ported DS3 effects, " +
                             $"{_bulletBehaviors} firing ported DS3 bullets), {_attacks} attack rows";

    private int _bulletBehaviors;

    /// <summary>
    /// Ports the attack a DS3 bullet deals. Elden Ring-only attack fields start from the attack of an Elden Ring
    /// bullet a player behaviour fires (the first one, by ID), then DS3's fields are laid over it.
    /// </summary>
    public int PortBulletAttack(int ds3Id)
    {
        _bulletAttackBase ??= er.Get("Bullet") is { } bullets
            ? _erBehavior.Rows.Where(r => Convert.ToInt32(r["refType"].Value) == 1).Select(r => bullets[Convert.ToInt32(r["refId"].Value)])
                .Where(b => b is not null).Select(b => _erAttack[Convert.ToInt32(b!["atkId_Bullet"].Value)]).FirstOrDefault(a => a is not null)
            : null;
        int id = PortAttack(ds3Id, _bulletAttackBase);
        // A DS3 weapon's projectile takes its power from the weapon: its attack row has only percentages (Friede's
        // shatter 200% magic, the Moonlight wave 110%) and no attack power of its own. In Elden Ring a bullet's
        // attack only counts the weapon when isAddBaseAtk is set: all 98 Ash of War bullet attacks that have
        // percentages and no power of their own set it (Storm Assault), and the bullet attack the port starts from
        // doesn't, so every such DS3 projectile hit for nothing and built up none of the weapon's frost or bleed.
        if (BulletBaseAttack && _attackIds.ContainsKey(ds3Id) && _bulletAttacks.Add(id) && _erAttack[id] is { } row
            && Power(row, "Correction") && !Power(row, "") && ParamDefaults.Cell(row, "isAddBaseAtk") is { } add)
        {
            add.Value = Convert.ChangeType(1, add.Value!.GetType());
            _bulletBaseAttacks++;
        }
        return id;
    }

    /// <summary>Fallback "weapon-visuals" G: DARKSIGN_BULLET_BASE_ATK=0 leaves ported bullet attacks without the
    /// weapon's own attack power, as in builds up to 1.0.279.</summary>
    private static readonly bool BulletBaseAttack = Environment.GetEnvironmentVariable("DARKSIGN_BULLET_BASE_ATK") != "0";

    private static bool Power(PARAM.Row row, string suffix) =>
        new[] { "atkPhys", "atkMag", "atkFire", "atkThun", "atkDark" }
            .Any(f => ParamDefaults.Cell(row, f + suffix) is { Value: not null } c && Convert.ToDouble(c.Value) > 0);

    private readonly HashSet<int> _bulletAttacks = [];
    private int _bulletBaseAttacks;
    public int BulletBaseAttacks => _bulletBaseAttacks;

    private PARAM.Row? _bulletAttackBase;

    /// <summary>
    /// Copies a DS3 behaviour variation under a new Elden Ring variation ID and returns it (0 if DS3 has none). The
    /// new variation also gets the Elden Ring template's rows for every judge ID DS3 lacks: the moves kept from the
    /// template (Elden Ring-only attacks) look their hits up in the weapon's variation too, and found nothing there.
    /// </summary>
    public int PortVariation(int ds3Variation, int templateVariation = 0, int? newVariation = null)
    {
        if (_variations.TryGetValue((ds3Variation, templateVariation), out var done))
            return done;
        return _variations[(ds3Variation, templateVariation)] = PortNewVariation(ds3Variation, templateVariation, newVariation);
    }

    private readonly Dictionary<(int, int), int> _variations = new();

    /// <summary>The variation a ported attack set gets: the first free one of the ten from <paramref name="wanted"/>
    /// (never above <see cref="MaxVariation"/>), else the first free one from 9000.</summary>
    public static int ChooseVariation(IReadOnlySet<int> used, int? wanted) =>
        Enumerable.Range(0, 10).Select(k => wanted + k).OfType<int>()
            .FirstOrDefault(v => v > 0 && v <= MaxVariation && !used.Contains(v), 0) is > 0 and var own
            ? own
            : Enumerable.Range(9000, 1000).First(v => !used.Contains(v));

    private static int Judge(PARAM.Row row) => Convert.ToInt32(row["behaviorJudgeId"].Value);

    /// <summary>
    /// Both games look a hit's behaviour row up by its row ID, not by its variationId / behaviorJudgeId fields:
    /// prefix * 100,000,000 + variation * 1000 + judge. The prefix is 1 for a player weapon's ordinary hits (TAE judge
    /// 0-999), 2 for every enemy row, and in Elden Ring 3, 4, 6 and 7 for Ash of War and special rows, which a move
    /// asks for with a four-digit TAE judge (vanilla a613 plays judge 3830 and the row is 300000830, variation 0,
    /// judge field 830). Builds 1.0.183-1.0.271 numbered every ported row variation * 1000 + judge, prefix 0, where
    /// the game never looks: no ported weapon's hit or projectile and no ported enemy's attack found its row, so DS3
    /// weapons dealt no damage and DS3 enemies could not hurt the player.
    /// </summary>
    public const int PrefixUnit = 100_000_000;

    /// <summary>
    /// The highest behaviour variation either game uses is five digits (Elden Ring 91000, DS3 99900): variation * 1000
    /// + judge must stay below one prefix unit. Builds up to 1.0.368 gave every ported enemy its new character ID * 100
    /// (Pontiff c7105 = 710500), so its rows (910500010...) sat where the prefix digit belongs: no ported DS3 enemy's
    /// hit found its attack row and none could hurt the player.
    /// </summary>
    public const int MaxVariation = PrefixUnit / 1000 - 1;

    /// <summary>The prefix of a row numbered the games' way, else -1 (the few shared rows numbered on their own).</summary>
    public static int Prefix(PARAM.Row row)
    {
        long rest = row.ID - (Convert.ToInt64(row["variationId"].Value) * 1000 + Judge(row));
        return rest >= 0 && rest % PrefixUnit == 0 ? (int)(rest / PrefixUnit) : -1;
    }

    /// <summary>The row ID the game computes for a hit, or -1 when it does not fit a row ID.</summary>
    public static int RowId(int prefix, int variation, int judge)
    {
        if (variation > MaxVariation)
            return -1;
        long id = (long)prefix * PrefixUnit + (long)variation * 1000 + judge;
        return id is > 0 and <= int.MaxValue ? (int)id : -1;
    }

    /// <summary>Judges (1-999) that no row of either game's behaviour param uses, lowest first.</summary>
    public List<int> FreeJudges()
    {
        var used = _erBehavior.Rows.Concat(_ds3Behavior.Rows).Select(Judge).ToHashSet();
        return [.. Enumerable.Range(1, 999).Where(j => !used.Contains(j))];
    }

    /// <summary>Gives a ported variation a second row for one of its judges, under a judge of its own (a copy of the
    /// first, e.g. the row that names a bullet). False when the variation has no such row or the new one exists.</summary>
    public bool AliasJudge(int variation, int judge, int alias)
    {
        int from = RowId(MainPrefix, variation, judge), to = RowId(MainPrefix, variation, alias);
        if (variation <= 0 || from < 0 || to < 0 || _erBehavior[from] is not { } source || _erBehavior[to] is not null)
            return false;
        var row = CopyRow(source, to);
        row["behaviorJudgeId"].Value = Convert.ChangeType(alias, row["behaviorJudgeId"].Value!.GetType());
        _erBehavior.Rows.Add(row);
        _erBehavior.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return true;
    }

    // The prefix this param's rows of real variations carry: 1 in BehaviorParam_PC, 2 in BehaviorParam.
    private int MainPrefix => _mainPrefix ??= _erBehavior.Rows.Where(r => Convert.ToInt32(r["variationId"].Value) > 0).Select(Prefix)
        .Where(p => p > 0).GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(1);

    private int? _mainPrefix;

    private int PortNewVariation(int ds3Variation, int templateVariation, int? newVariation)
    {
        var rows = _ds3Behavior.Rows.Where(r => Convert.ToInt32(r["variationId"].Value) == ds3Variation).ToList();
        if (ds3Variation <= 0 || rows.Count == 0)
            return 0;
        // The template's own rows, then its base variation's (XX00, which the game falls back to) for judges it lacks.
        // Keyed by prefix and judge: a variation holds the same judge under several prefixes (ordinary hit 1, Ash of
        // War 3, ...), and each copy must keep its own.
        var templateRows = new Dictionary<(int Prefix, int Judge), PARAM.Row>();
        if (templateVariation > 0)
            foreach (var v in new[] { templateVariation, templateVariation / 100 * 100 }.Distinct())
                foreach (var r in _erBehavior.Rows.Where(r => Convert.ToInt32(r["variationId"].Value) == v && Prefix(r) > 0))
                    templateRows.TryAdd((Prefix(r), Judge(r)), r);
        var used = _erBehavior.Rows.Select(r => Convert.ToInt32(r["variationId"].Value)).ToHashSet();
        // Weapons take the first free variation from 9000; an enemy asks for its own (its new character ID * 10, the
        // way Elden Ring numbers enemy variations: c3570 Godskin Noble = 35700) and takes the next free one of its ten
        // (a second DS3 NpcParam row of the same character, or an Elden Ring variation such as 70000 or 71000).
        int variation = ChooseVariation(used, newVariation);
        var ds3Judges = new HashSet<(int Prefix, int Judge)>();
        foreach (var source in rows)
        {
            // A DS3 row keeps its own prefix (1 for weapons, 2 for enemies: the same in both games).
            int judge = Judge(source), prefix = Prefix(source) > 0 ? Prefix(source) : MainPrefix, id = RowId(prefix, variation, judge);
            ds3Judges.Add((prefix, judge));
            if (id < 0 || _erBehavior[id] is not null)
                continue;
            var templateRow = templateRows.GetValueOrDefault((prefix, judge));
            int refType = Convert.ToInt32(source["refType"].Value);
            PARAM.Row row;
            if (refType == 0 && AttacksFromTemplate && templateRow is not null)
                row = CopyRow(templateRow, id);
            else if (refType == 0)
            {
                row = CopyByName(source, new PARAM.Row(id, source.Name ?? "", _erBehavior.AppliedParamdef));
                // Elden Ring-only attack fields start from the template's attack for this judge (or any of its attacks).
                var baseAttack = TemplateAttack(templateRow)
                                 ?? templateRows.OrderBy(t => t.Key).Select(t => TemplateAttack(t.Value)).FirstOrDefault(a => a is not null);
                row["refId"].Value = Convert.ChangeType(PortAttack(Convert.ToInt32(source["refId"].Value), baseAttack), row["refId"].Value!.GetType());
            }
            // SpEffects (2): the DS3 effect ported under its new ID (weapon art buffs).
            else if (refType == 2 && Effects?.Port(Convert.ToInt32(source["refId"].Value)) is > 0 and var effect)
            {
                row = CopyByName(source, new PARAM.Row(id, source.Name ?? "", _erBehavior.AppliedParamdef));
                row["refId"].Value = Convert.ChangeType(effect, row["refId"].Value!.GetType());
                _effectBehaviors++;
            }
            // Bullets (1): the DS3 bullet ported under its new ID (the Moonlight Greatsword's wave).
            else if (refType == 1 && Bullets?.Port(Convert.ToInt32(source["refId"].Value)) is > 0 and var bullet)
            {
                row = CopyByName(source, new PARAM.Row(id, source.Name ?? "", _erBehavior.AppliedParamdef));
                row["refId"].Value = Convert.ChangeType(bullet, row["refId"].Value!.GetType());
                _bulletBehaviors++;
            }
            // Unported bullets and SpEffects are DS3 IDs, which name unrelated Elden Ring bullets and effects (or
            // none): the template's row for the same judge stands in, else the row is left out.
            else if (templateRow is not null)
                row = CopyRow(templateRow, id);
            else
                continue;
            row["variationId"].Value = Convert.ChangeType(variation, row["variationId"].Value!.GetType());
            _erBehavior.Rows.Add(row);
            _behaviors++;
        }
        foreach (var ((prefix, judge), er) in templateRows.OrderBy(t => t.Key))
        {
            int id = RowId(prefix, variation, judge);
            if (ds3Judges.Contains((prefix, judge)) || judge < 0 || judge >= 1000 || id < 0 || _erBehavior[id] is not null)
                continue;
            var row = CopyRow(er, id);
            row["variationId"].Value = Convert.ChangeType(variation, row["variationId"].Value!.GetType());
            _erBehavior.Rows.Add(row);
            _templateBehaviors++;
        }
        _erBehavior.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return variation;
    }

    private int _templateBehaviors;

    private PARAM.Row CopyRow(PARAM.Row from, int id)
    {
        var row = new PARAM.Row(id, from.Name ?? "", _erBehavior.AppliedParamdef);
        for (int i = 0; i < row.Cells.Count; i++)
            row.Cells[i].Value = from.Cells[i].Value is byte[] b ? b.Clone() : from.Cells[i].Value;
        return row;
    }

    private readonly Dictionary<int, int> _attackIds = new();

    private PARAM.Row? TemplateAttack(PARAM.Row? behavior) =>
        behavior is not null && Convert.ToInt32(behavior["refType"].Value) == 0 ? _erAttack[Convert.ToInt32(behavior["refId"].Value)] : null;

    private int PortAttack(int ds3Id, PARAM.Row? erBase = null)
    {
        if (ds3Id <= 0 || _ds3Attack[ds3Id] is not { } source)
            return ds3Id;
        if (_attackIds.TryGetValue(ds3Id, out var mapped))
            return mapped;
        int id = _erAttack[ds3Id] is null ? ds3Id : 700_000_000 + ds3Id % 100_000_000;
        while (_erAttack[id] is not null)
            id++;
        // Start from an Elden Ring attack of the template weapon (its fields DS3 lacks: isAddBaseAtk, regain, decals,
        // dmgLevel_vsPlayer...), then lay every DS3 field over it.
        var row = new PARAM.Row(id, source.Name ?? "", _erAttack.AppliedParamdef);
        if (erBase is not null && !AttacksDs3Only && StructurallyEqual(erBase, row))
            for (int i = 0; i < row.Cells.Count; i++)
                row.Cells[i].Value = erBase.Cells[i].Value is byte[] b ? b.Clone() : erBase.Cells[i].Value;
        row = CopyByName(source, row);
        // DS3 names these spEffect0-4, Elden Ring spEffectId0-4: the effect a hit applies (an art's frost or poison).
        // The template's own hit effects (its status build-up) don't belong to the DS3 weapon.
        for (int i = 0; i < 5; i++)
            if (row[$"spEffectId{i}"] is { } cleared)
                cleared.Value = Convert.ChangeType(-1, cleared.Value!.GetType());
        if (Effects is not null)
            for (int i = 0; i < 5; i++)
                if (source[$"spEffect{i}"] is { } from && row[$"spEffectId{i}"] is { } to && Convert.ToInt32(from.Value) > 0
                    && Effects.Port(Convert.ToInt32(from.Value), statusOnly: !FullHitEffects) is > 0 and var effect)
                    to.Value = Convert.ChangeType(effect, to.Value!.GetType());
        _erAttack.Rows.Add(row);
        _erAttack.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        _attacks++;
        return _attackIds[ds3Id] = id;
    }

    private static bool StructurallyEqual(PARAM.Row a, PARAM.Row b) => a.Cells.Count == b.Cells.Count;

    /// <summary>
    /// DS3 field names (Paramdex DS3 ATK_PARAM_ST) that Elden Ring's Paramdex def spells differently. Everything else
    /// matches ignoring case (DS3 "Hit0_Radius", "AtkPhys", "HitSourceType" = Elden Ring "hit0_Radius", "atkPhys",
    /// "hitSourceType"). Before this, names were matched case-sensitively and only 21 of DS3's 189 attack fields were
    /// copied: every ported hit had radius 0, no attack power and default dummy polys, so DS3 weapons hit nothing (not
    /// enemies, not walls).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Aliases = BuildAliases();

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Blowing"] = "blowingCorrection",
            ["damageLevel"] = "dmgLevel",
            ["DefMaterial"] = "defSeMaterial1",
            ["DefSfxMaterial"] = "defSfxMaterial1",
            ["atkPowForSfxSe"] = "atkPow_forSfx",
            ["atkDirForSfxSe"] = "atkDir_forSfx",
            ["spawnAiSoundId"] = "AppearAiSoundId",
        };
        for (int i = 0; i < 16; i++)
            map[$"hit{i}_Priority"] = $"hti{i}_Priority";
        // Not mapped: DS3's Hit<N>_VfxId / DummyPolyId (the swing trail) name DS3 effect IDs that mean other effects in
        // Elden Ring, so the template's trail stays; spEffect0-4 are ported by ID in PortAttack.
        return map;
    }

    private static PARAM.Row CopyByName(PARAM.Row from, PARAM.Row to)
    {
        // Only names a paramdef uses once: unnamed padding fields share names like "0x81". Names match ignoring case
        // (Paramdex's DS3 defs capitalise many fields Elden Ring's don't), plus the renames in Aliases.
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Unique(from))
            fields.TryAdd(Aliases.TryGetValue(c.Def.InternalName, out var renamed) ? renamed : c.Def.InternalName, c.Value);
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
