using HKLib.hk2018;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// DS3 weapon arts (skills) as Elden Ring Ashes of War of their own. How the two games do arts (survey 2026-11-07):
/// <list type="bullet">
/// <item>DS3: a weapon's art moves are in TAE section spAtkCategory (Friede's a232), at IDs 36000-37999 by kind of
/// skill: 362xx DrawStance (start 36200, loop 36210, walking loop 36215, end 36220, light 36230, heavy 36240), 374xx
/// Stampede, 377xx Endure, 3767x Warcry, 369xx Parry, ...</item>
/// <item>Elden Ring: the weapon's swordArtsParamId names a SwordArtsParam row, whose art ID is the u16 Paramdex still
/// calls shieldIconType + pad (Unending Dance 309, Moon-and-Fire Stance 318, Rancor Shot 371; swordArtsType is 0 in
/// every row). Art ID N plays TAE section 600 + N through the behaviour selectors with offset type
/// SwordArtsCategory: 40000 one-shot, 40005 the same without enough FP, 40050-40078 the stance (Elden Ring's DrawStance
/// is DS3's, same names). c0000.hks decides which: IDs from 300 up with no special case get the plain one-shot
/// (GetSwordArtsDiffCategory returns the default), stance only for IDs listed in IsStanceArts.</item>
/// </list>
/// So each DS3 art gets a free art ID (300-398, then 170-254; animations go in c0000_a9x with the movesets), a TAE built
/// from an Elden Ring art of the same kind with DS3's animations and events, behaviour clips copied from that art's,
/// and a SwordArtsParam row (a copy of the template weapon's, with the FP costs of DS3's SwordArtsParam row) the ported weapon points at.
/// Stance arts need c0000.hks to list their ID: ividyon's EldenRingHKS (the community's decompiled hks) is fetched and
/// written to Mods\ER\action\script\c0000.hks with our IDs added to IsStanceArts.
/// Most DS3 weapons (spAtkCategory 0) do their class's skill: those come from the class's moveset section. One-shot
/// skills with follow-ups (FPCostLight / FpCostStrong) get them as ComboEnd (R1) / ComboEnd_2 (R2) behind the hks's
/// combo window SpEffects. Every ported weapon is locked to its art like Elden Ring's unique weapons.
/// Fallback "weapon-arts": DARKSIGN_WEAPON_ARTS=nohks (no hks; stance arts become a one-shot of their light attack),
/// unlocked (Ashes of War allowed), 0 (off: ported weapons keep their template's Elden Ring Ash of War, as before).
/// </summary>
public static partial class MovesetPort
{
    public static readonly string ArtsMode = (Environment.GetEnvironmentVariable("DARKSIGN_WEAPON_ARTS") ?? "").ToLowerInvariant();
    private static bool ArtsEnabled => ArtsMode != "0";
    private static bool ArtsWithHks => ArtsMode != "nohks";
    // DARKSIGN_STANCE_CHAIN=0: stance light attacks don't chain (R1 after the first stance attack is a plain R1).
    private static readonly bool StanceChain = Environment.GetEnvironmentVariable("DARKSIGN_STANCE_CHAIN") != "0";

    /// <summary>First SwordArtsParam row of the ported arts (Elden Ring's own end at 5600).</summary>
    public const int FirstArtRow = 90000;

    private const string HksUrl = "https://raw.githubusercontent.com/ividyon/EldenRingHKS/d88d6441f5fccfdd6a5fd10d493309b680181897/c0000.hks";

    // Elden Ring stance clips <- DS3 DrawStance animations (both games name these selectors DrawStanceRight*).
    private static readonly (int Er, int Ds3)[] StanceMap =
    [
        (40050, 36200), (40055, 36200), (40051, 36210), (40056, 36210), (40052, 36215), (40057, 36215),
        (40053, 36220), (40058, 36220), (40060, 36230), (40065, 36230), (40070, 36240), (40075, 36240),
    ];

    // Elden Ring stance arts with light and heavy follow-ups, the template for DS3's: Unsheathe, Square Off, ...
    private static readonly int[] StanceTemplates = [614, 615, 610, 611, 625];

    private sealed record ArtJob(int Ds3Section, int ClassSection, bool Stance, int Ds3Start, List<WeaponPlan> Weapons, List<int> Skill, string Source);

    private sealed record ArtBuild(bool Stance, int Start, int? Light, int? Heavy, int TemplateSection, int Moves, int Clips, List<int> Chain, int ChainTemplate);

    /// <summary>
    /// The stance light attacks after the first that a DS3 stance chains through (Friede's a232: 36230, then 36250,
    /// then 36290; the x1 IDs are the no-FP copies, 36240 the heavy attack): the block's x0 moves above 36230 other
    /// than the heavy, at most two (Elden Ring's chain has two more states). A stance with only 36230 chains nothing.
    /// </summary>
    internal static List<int> StanceChainMoves(IEnumerable<int> skill) =>
        skill.Where(id => id / 100 == 362 && id > 36230 && id % 10 == 0 && id != 36240).Order().Take(2).ToList();

    /// <summary>
    /// The moves a DS3 one-shot art continues into on R1 then R2: the block's x0 moves after the one-shot (Ember's
    /// a253: 36400, then 36410 thrust, 36420 slam). The x1 IDs are the no-FP copies of the move before them (36401 is
    /// 36400 again, without the buff), not follow-ups: 1.0.257 offered 36401 as the R1 follow-up, so R1 inside the art
    /// replayed the art itself and the thrust sat on R2.
    /// </summary>
    internal static List<int> OneShotFollowUps(IEnumerable<int> skill, int start) =>
        skill.Where(id => id / 100 == start / 100 && id > start && id % 10 == 0).Order().ToList();

    // Elden Ring's chain states and the clips they play: W_SwordArtsOneShotComboEnd_MesmerSowrdArts (the hks's state
    // for Rellana's second stance hit) selects animation 40010 like ComboEnd, W_SwordArtsOneShotComboEnd_2 40020.
    private static readonly (int Er, int NoMp)[] ChainSlots = [(40010, 40015), (40020, 40025)];

    /// <summary>An Elden Ring TAE event that holds an art combo window SpEffect (100050-100055): type, bytes, group.</summary>
    internal sealed record WindowEvent(int Type, byte[] Bytes, TAE.EventGroup Group);

    // Art IDs vanilla Elden Ring's hks special-cases in the 300s (stance and attack-stance lists), skipped even when
    // the hks text can't be read.
    private static readonly int[] VanillaSpecialArtIds = [305, 309, 318, 335, 337, 340, 341, 342, 343, 346, 347, 357, 371, 372, 373, 399];

    // DS3 art names whose block is known, for class skills no weapon with its own section teaches.
    private static readonly Dictionary<string, int> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Stomp"] = 360, ["Stance"] = 362, ["Spin Slash"] = 364, ["Charge"] = 367, ["Warcry"] = 376,
    };

    public sealed record ArtResult(int Arts, int Weapons, Dictionary<int, string> Names, List<int> StanceIds);

    /// <summary>
    /// DS3's generic class skills (the ones many weapon classes share, each class with its own animations) and the
    /// Elden Ring Ash of War that does the same thing. Free art IDs are few (about 115 with ividyon's hks: art ID N is
    /// TAE section 600 + N, and Elden Ring's own Ashes of War hold most of 1-399), and 1.0.242-1.0.251 handed them out
    /// in section order, so the DLC weapons' signature arts (a239-a267: the Ringed Knight pair's Ember, Gael's, the
    /// Demon's Scar) were the ones left without and fell back to their template's Ash of War. Now the unique arts get
    /// IDs first; a generic class skill is ported while IDs last and otherwise takes Elden Ring's own Ash of War of
    /// the same kind (by its GemName text), never a random template's. A skill two weapons share by name but that is
    /// theirs alone (Falling Bolt on the Dragonslayer Greataxe and Swordspear, Guiding Light on the candlesticks) is
    /// unique, not generic: 1.0.257 left the Dragonslayer Greataxe with its template's Barbaric Roar.
    /// </summary>
    internal static readonly Dictionary<string, string?> GenericArts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Stomp"] = "Stamp (Upward Cut)", ["Charge"] = "Charge Forth", ["Warcry"] = "War Cry", ["Spin Slash"] = "Spinning Slash",
        ["Stance"] = "Square Off", ["Perseverance"] = "Endure", ["Quickstep"] = "Quickstep", ["Shield Bash"] = "Shield Bash",
        ["Parry"] = "Parry", ["Spin Sweep"] = "Spinning Strikes", ["Spin Bash"] = "Spinning Strikes", ["Hold"] = "Unsheathe",
        ["Shield Splitter"] = "Impaling Thrust", ["Weapon Skill"] = "No Skill", ["Steady Chant"] = null, ["Puncturing Arrow"] = "Mighty Shot",
        ["Unfaltering Prayer"] = null, ["Gentle Prayer"] = null,
    };

    internal static bool IsGenericArt(string? name) => name is not null && GenericArts.ContainsKey(name.Trim());

    /// <summary>The SwordArtsParam row of Elden Ring's own Ash of War named <paramref name="ashName"/> (GemName "Ash of War: X"), or null.</summary>
    internal static int? ErAshRow(string ashName, IReadOnlyDictionary<int, string> gemNames, PARAM? gems)
    {
        if (gems is null)
            return null;
        foreach (var (id, text) in gemNames)
        {
            var name = text.Trim();
            if (name.StartsWith("Ash of War: ", StringComparison.OrdinalIgnoreCase))
                name = name["Ash of War: ".Length..].Trim();
            if (!name.Equals(ashName, StringComparison.OrdinalIgnoreCase) || gems[id] is not { } gem)
                continue;
            var v = gem["swordArtsParamId"]?.Value;
            if (v is null or byte[])
                continue;
            int row = Convert.ToInt32(v);
            if (row > 0)
                return row;
        }
        return null;
    }

    /// <summary>The DS3 art sections whose effects and sounds the arts need (so they are ported with the movesets').</summary>
    private static IEnumerable<int> ArtSections(IEnumerable<WeaponPlan> ports, PARAM ds3Weapons) => !ArtsEnabled ? []
        : ports.SelectMany(p => new[] { Convert.ToInt32(ds3Weapons[p.Ds3Id]!["spAtkCategory"].Value), Convert.ToInt32(ds3Weapons[p.Ds3Id]!["wepmotionCategory"].Value) })
            .Where(s => s > 0).Distinct();

    private static ArtResult PortArts(PorterContext ctx, GameArchives erArchives, GameArchives ds3Archives, ParamStore er, ParamStore ds3,
        IReadOnlyList<WeaponPlan> ports, BND4 erMain, BND4 erAnims, BND4 ds3Main, Func<int, TAE?> ds3Tae, Ds3AnimFiles ds3Hkx,
        AnimPort.Ds3Skeleton ds3Skeleton, AnimPort.ErSkeleton erSkeleton, List<CustomManualSelectorGenerator> selectors, ref int nextInternalId,
        EffectEvents effectEvents, FxrPort.Result effects, SoundEvents soundPort, List<string> timing)
    {
        var none = new ArtResult(0, 0, [], []);
        if (!ArtsEnabled)
        {
            Console.WriteLine("arts: off (weapon-arts fallback), ported weapons keep Elden Ring Ashes of War");
            return none;
        }
        var artParam = er.Get("SwordArtsParam")!;
        var erWeapons = er.Get("EquipParamWeapon")!;
        var ds3Weapons = ds3.Get("EquipParamWeapon")!;
        var artSelectors = selectors.Where(s => s.m_offsetType == CustomManualSelectorGenerator.OffsetType.SwordArtsCategory).ToList();
        // Sections every art selector reaches, by animation ID.
        var clipsBySection = artSelectors.SelectMany(s => s.m_generators.OfType<hkbClipGenerator>().Select(c => (s.m_animId, Clip: c)))
            .Select(t => (t.m_animId, t.Clip, Match: AnimName().Match(t.Clip.m_animationName ?? "")))
            .Where(t => t.Match.Success)
            .GroupBy(t => int.Parse(t.Match.Groups[1].Value))
            .ToDictionary(g => g.Key, g => g.Select(t => t.m_animId).ToHashSet());

        // Free art IDs: no Elden Ring art row uses them, no TAE or behaviour clip has their section, and c0000.hks
        // gives them no special case (305, 357 and 399 have one).
        var usedIds = artParam.Rows.Select(ArtId).ToHashSet();
        var taeSections = erMain.Files.Select(f => ItemText.Leaf(f.Name)).Where(n => n.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            .Select(n => int.TryParse(n[1..^4], out var s) ? s : -1).ToHashSet();
        // 300-398 first (Elden Ring's own DLC range), then 170-254: Elden Ring's own c0000.hks gives both the plain
        // one-shot. With our hks (ividyon's, which also gives an ID missing from its SwordArtsCategory table the
        // default), every other ID it never names is free too: 39 IDs weren't enough for DS3's 77 arts.
        // No range is free of special cases: the hks lists 340-343, 346 and 347 ("UNUSED") as stances and 340/341 as
        // attack stances like Spinning Wheel, so an art given 340 played as one (the Moonlight Greatsword did Ghiza's
        // Wheel in 1.0.206). Every ID the hks names is skipped, whichever range it is in; the hks is read for that
        // even when it isn't written (weapon-arts B).
        string? hksText = LoadHks(ctx);
        string? hks = ArtsWithHks ? hksText : null;
        var hksNamed = hksText is null ? [] : HksNamedIds(hksText);
        hksNamed.UnionWith(VanillaSpecialArtIds);
        var extra = hks is null ? [] : Enumerable.Range(1, 169).Concat(Enumerable.Range(255, 45));
        var free = new Queue<int>(Enumerable.Range(300, 99).Concat(Enumerable.Range(170, 85)).Where(id => id is not (196 or 305 or 357 or 399))
            .Concat(extra)
            .Where(id => !hksNamed.Contains(id) && !usedIds.Contains(id) && !taeSections.Contains(600 + id) && !clipsBySection.ContainsKey(600 + id)));

        // Which DS3 moves make each weapon's art. A weapon with its own art section (spAtkCategory, Friede's a232) uses
        // that; every other weapon (spAtkCategory 0: Vordt's Great Hammer, the Dragonslayer Greataxe, most weapons) does
        // its class's skill, in its moveset section (wepmotionCategory). A section can hold several skills, one block
        // of 100 IDs each (360xx Stomp, 362xx stance, 364xx Spin Slash, 367xx Charge, 3767x Warcry, ...): the block is
        // the one weapons with their own section use for the same DS3 art (by its SwordArtsParam ActionId, then its
        // name), else Kinds below. Weapons without skill animations (talismans, most catalysts) are left alone.
        var ds3ArtParam = ds3.Get("SwordArtsParam");
        var skillCache = new Dictionary<(int, bool), List<int>>();
        List<int> SkillIds(int section, bool own = false) => skillCache.TryGetValue((section, own), out var s) ? s
            : skillCache[(section, own)] = section <= 0 || ds3Tae(section) is not { } t ? []
                // 36000-37999 are DS3's skill moves; a weapon's own section can keep its skill at 38400 instead (the
                // Moonlight Greatsword's Moonlight Vortex, the Farron Greatsword's parry: a158 / a161 hold nothing in
                // 36xxx-37xxx but a Stomp left over from development), which 1.0.225 never looked at, so those weapons
                // did their class's skill. 369xx is the parry block (not a skill of its own). Class sections keep to
                // 36000-37999: their 38xxx moves (a20's 38020, 38700) aren't the class skill.
                : t.Animations.Select(a => (int)a.ID).Where(id => id is >= 36000 and < 38000 || (own && id is >= 38000 and < 39000)).Where(id => id / 100 != 369).Distinct().Order().ToList();
        (string? Name, int Action) Kind(WeaponPlan p)
        {
            var row = ds3ArtParam?[Convert.ToInt32(ds3Weapons[p.Ds3Id]!["swordArtId"].Value)];
            return (row?.Name?.Trim(), row is not null && Field(row, "ActionId")?.Value is { } v and not byte[] ? Convert.ToInt32(v) : -1);
        }
        // A DS3 parry skill (the Farron Greatsword's "Parry"): its move is the one that opens a parry (an attack event
        // with a DS3 parry judge, a161 38400), not the section's first skill move (a161 36000, a Stomp left over from
        // development, which 1.0.225 played: "looks like an Elden Ring parry" with nothing behind it).
        bool HasParryHit(int section, int animId) => ds3Tae(section)?.Animations.FirstOrDefault(a => a.ID == animId)?.Events
            .Any(e => e.Type == 1 && e.GetParameterBytes(false) is { Length: >= 12 } b && Ds3ParryJudges.Contains(BitConverter.ToInt32(b, 8))) == true;
        ArtJob MakeJob(int section, int cls, List<int> skill, List<WeaponPlan> weapons, string source)
        {
            bool stance = skill.Contains(36200);
            // One-shot: the block's first move (its x00 animation). Without the hks, a stance plays its light attack.
            int start = stance && hks is null ? 36230 : stance ? 36200 : skill.FirstOrDefault(id => id % 100 == 0, skill[0]);
            if (!stance && weapons.Any(p => (Kind(p).Name ?? "").Trim().Equals("Parry", StringComparison.OrdinalIgnoreCase))
                && skill.FirstOrDefault(id => HasParryHit(section, id)) is > 0 and var parry)
                start = parry;
            return new ArtJob(section, cls, stance && hks is not null, start, weapons, skill, source);
        }
        var jobs = new List<ArtJob>();
        var classWeapons = new List<WeaponPlan>();
        foreach (var g in ports.GroupBy(p => Convert.ToInt32(ds3Weapons[p.Ds3Id]!["spAtkCategory"].Value)))
        {
            var skill = SkillIds(g.Key, own: true);
            if (g.Key <= 0 || skill.Count == 0)
            {
                classWeapons.AddRange(g);
                continue;
            }
            jobs.Add(MakeJob(g.Key, Convert.ToInt32(ds3Weapons[g.First().Ds3Id]!["wepmotionCategory"].Value), skill, g.ToList(), "own"));
        }
        // What the weapons with their own section teach about blocks: DS3 art ActionId / name -> block.
        var blockByAction = new Dictionary<int, Dictionary<int, int>>();
        // DS3's weapon row also has swordArt_ActId; if it names the skill's motion (an animation ID or an index), the
        // weapons with their own section teach it too, and it beats the art's name.
        var blockByActId = new Dictionary<int, Dictionary<int, int>>();
        int ActId(WeaponPlan p) => ds3Weapons[p.Ds3Id]!["swordArt_ActId"]?.Value is { } a and not byte[] ? Convert.ToInt32(a) : 0;
        var blockByName = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        void Learn<TK>(Dictionary<TK, Dictionary<int, int>> map, TK key, int block) where TK : notnull
        {
            if (!map.TryGetValue(key, out var counts))
                map[key] = counts = [];
            counts[block] = counts.GetValueOrDefault(block) + 1;
        }
        foreach (var job in jobs)
            foreach (var p in job.Weapons)
            {
                var (name, action) = Kind(p);
                if (action >= 0)
                    Learn(blockByAction, action, job.Ds3Start / 100);
                if (ActId(p) > 0)
                    Learn(blockByActId, ActId(p), job.Ds3Start / 100);
                if (!string.IsNullOrEmpty(name))
                    Learn(blockByName, name, job.Ds3Start / 100);
            }
        Console.WriteLine("arts: swordArt_ActId -> block from weapons with their own section: " + string.Join(", ",
            blockByActId.OrderBy(k => k.Key).Select(k => $"{k.Key}:{string.Join("/", k.Value.Select(b => $"{b.Key * 100}x{b.Value}"))}")));
        int noClassSkill = 0;
        var unknownBlocks = new List<string>();
        foreach (var g in classWeapons.GroupBy(p => Convert.ToInt32(ds3Weapons[p.Ds3Id]!["wepmotionCategory"].Value)))
        {
            var skill = SkillIds(g.Key);
            if (skill.Count == 0)
            {
                noClassSkill += g.Count();
                continue;
            }
            var blocks = skill.Select(id => id / 100).Distinct().ToList();
            // One art per block and DS3 art (by name): weapons that share moves but not the art (Black Knight Sword's
            // and the Moonlight Greatsword's) keep their own name and FP cost. Name first: DS3 gives many unrelated arts
            // the same ActionId (22: Crescent Blade, Bloodlust, Unleash Dragon), so that is the last resort.
            foreach (var byBlock in g.GroupBy(p =>
                     {
                         var (name, action) = Kind(p);
                         if (blocks.Count == 1)
                             return (Block: blocks[0], Name: name);
                         int? Pick<TK>(Dictionary<TK, Dictionary<int, int>> map, TK? key) where TK : notnull =>
                             key is not null && map.TryGetValue(key, out var counts)
                                 ? counts.Where(c => blocks.Contains(c.Key)).OrderByDescending(c => c.Value).Select(c => (int?)c.Key).FirstOrDefault()
                                 : null;
                         int actId = ActId(p);
                         // Taught only when every weapon with that value used one block (a real motion ID, not noise).
                         int? byActId = actId >= 36000 && actId < 38000 && blocks.Contains(actId / 100) ? actId / 100
                             : actId > 0 && blockByActId.TryGetValue(actId, out var actCounts) && actCounts.Count == 1 && blocks.Contains(actCounts.Keys.First())
                                 ? actCounts.Keys.First() : null;
                         return (Block: byActId ?? Pick(blockByName, name)
                                        ?? (name is not null && Kinds.TryGetValue(name, out var known) && blocks.Contains(known) ? known : (int?)null)
                                        ?? (action >= 0 ? Pick(blockByAction, action) : null) ?? -1, Name: name);
                     }))
            {
                if (byBlock.Key.Block < 0)
                {
                    unknownBlocks.Add($"{string.Join(", ", byBlock.Select(p => p.Name))} (DS3 a{g.Key} blocks {string.Join("/", blocks.Select(b => b * 100))}, " +
                                      $"art '{Kind(byBlock.First()).Name}' action {Kind(byBlock.First()).Action} swordArt_ActId {ActId(byBlock.First())})");
                    continue;
                }
                // Which weapons do a class skill, and what their own section holds: a weapon whose DS3 art has its own
                // name (the Moonlight Greatsword's "Moonlight Vortex", 1.0.221: it did the class's Stomp) tells here
                // whether DS3 keeps its skill in a section this port didn't look at, or outside the 36000-37999 range.
                foreach (var p in byBlock)
                {
                    int ownSection = Convert.ToInt32(ds3Weapons[p.Ds3Id]!["spAtkCategory"].Value);
                    var ownTae = ownSection > 0 ? ds3Tae(ownSection) : null;
                    string own = ownSection <= 0 ? "no spAtkCategory section"
                        : ownTae is null ? $"spAtkCategory a{ownSection} has no TAE"
                        : $"spAtkCategory a{ownSection}: {ownTae.Animations.Count} animations, IDs {string.Join(" ", ownTae.Animations.Select(a => a.ID / 1000 * 1000).Distinct().Order().Select(k => $"{k}+"))}";
                    if (ownSection > 0 || !string.Equals(byBlock.Key.Name, Kinds.FirstOrDefault(k => k.Value == byBlock.Key.Block).Key, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"arts: {p.Name} does class a{g.Key}'s block {byBlock.Key.Block * 100} for its DS3 art '{byBlock.Key.Name}' ({own})");
                }
                jobs.Add(MakeJob(g.Key, g.Key, skill.Where(id => id / 100 == byBlock.Key.Block).ToList(), byBlock.ToList(), "class"));
            }
        }
        // Order: Friede's, then every unique art (its own skill, not a generic class skill: see GenericArts), then the
        // generic ones by how many weapons they serve. The free IDs run out before the end; what's left takes Elden
        // Ring's own Ash of War of the same kind below.
        jobs = jobs.OrderByDescending(j => j.Weapons.Any(p => p.Ds3Id == 10180000)).ThenBy(j => IsGenericArt(Kind(j.Weapons[0]).Name) ? 1 : 0)
            .ThenByDescending(j => j.Weapons.Count).ThenBy(j => j.Ds3Section).ToList();
        Console.WriteLine($"arts: {jobs.Count} DS3 weapon arts with skill animations ({jobs.Count(j => j.Stance)} stances, " +
                          $"{jobs.Count(j => j.Source == "class")} class skills for {jobs.Where(j => j.Source == "class").Sum(j => j.Weapons.Count)} weapons), " +
                          $"{free.Count} free art IDs; {noClassSkill} weapons have no skill animations");
        foreach (var line in unknownBlocks)
            Console.WriteLine($"arts: skipped {line}: can't tell which of its class's skills it does");

        // The art combo windows: Elden Ring's hks lets R1 / R2 continue a one-shot art while the move holds SpEffect
        // 100054 / 100050 (into ComboEnd, 40010) or 100055 / 100051 (into ComboEnd_2, 40020), set by a TAE event.
        // The event's type and layout come from an Elden Ring art that has one.
        var windowProto = FindWindowEvent(erMain, artSelectors.Count > 0 ? clipsBySection.Keys.Where(s => s is >= 600 and < 1000) : []);

        // Stance chains need the hks's art-318 chain (see PatchHks) and a combo window event to copy.
        bool chainHks = hks is not null && StanceChain && windowProto is not null && CanChainStances(hks);
        if (hks is not null && !chainHks)
            Console.WriteLine(StanceChain ? "arts: stance light attacks won't chain (the hks has no art-318 chain to reuse, or no combo window event was found)"
                                          : "arts: stance light attacks won't chain (DARKSIGN_STANCE_CHAIN=0)");

        var ds3Captions = new Dictionary<int, string>();
        var names = new Dictionary<int, string>();
        var stanceIds = new List<int>();
        var chainIds = new List<int>();
        var portedIds = new List<int>();
        int arts = 0, weapons = 0, nextRow = FirstArtRow, followUps = 0, internalIdNext = nextInternalId, erAshes = 0;
        Dictionary<int, string>? gemNames = null;
        foreach (var job in jobs)
        {
            var label = string.Join(", ", job.Weapons.Select(p => p.Name).Take(3)) + (job.Weapons.Count > 3 ? $" +{job.Weapons.Count - 3}" : "");
            if (free.Count == 0)
            {
                var kindName = Kind(job.Weapons[0]).Name;
                gemNames ??= ItemText.Load(erArchives, "GemName");
                int? erRow = IsGenericArt(kindName) && GenericArts[kindName!.Trim()] is { } ash ? ErAshRow(ash, gemNames, er.Get("EquipParamGem")) : null;
                if (erRow is { } ashRow)
                {
                    foreach (var plan in job.Weapons)
                        foreach (var w in erWeapons.Rows.Where(r => r.ID >= plan.NewId!.Value && r.ID < plan.NewId.Value + 10000))
                            SetCell(w, "swordArtsParamId", ashRow);
                    erAshes++;
                    Console.WriteLine($"arts: {label} (DS3 a{job.Ds3Section}): no free art ID left; its DS3 skill '{kindName}' is Elden Ring's '{GenericArts[kindName!.Trim()]}' (SwordArtsParam {ashRow})");
                }
                else
                    Console.WriteLine($"arts: skipped {label} (DS3 a{job.Ds3Section}, '{kindName}'): no free art ID left and no Elden Ring Ash of War of that kind, keeps its template's");
                continue;
            }
            var templateWeapon = erWeapons[job.Weapons[0].TemplateId!.Value]!;
            int templateRowId = Convert.ToInt32(templateWeapon["swordArtsParamId"].Value);
            var templateArtRow = artParam[templateRowId] ?? artParam.Rows.First(r => r.ID > 10);
            var (fp, fpLight, fpHeavy, ds3ArtName) = Ds3Art(ds3, ds3Weapons, job.Weapons[0].Ds3Id);
            int artId = free.Peek(), section = 600 + artId;
            var ds3Taes = new List<(int Section, TAE? Tae)> { (job.Ds3Section, ds3Tae(job.Ds3Section)), (job.ClassSection, ds3Tae(job.ClassSection)) }
                .Where(t => t.Tae is not null).DistinctBy(t => t.Section).ToList();

            // Tries in order: the stance (stance skills), the block's first move, then its other moves; the first that
            // builds wins. Each failure says why, so the log tells what to fix next.
            var attempts = new List<(bool Stance, int Start)>();
            if (job.Stance)
                attempts.Add((true, 36200));
            var block = job.Skill.Where(id => id / 100 == job.Ds3Start / 100).ToList();
            foreach (var start in new[] { job.Stance ? 36230 : job.Ds3Start }.Concat(block.Where(id => id % 100 == 0)).Concat(job.Skill.Where(id => id % 100 == 0)).Concat(block)
                         .Where(id => !(id / 100 == 362 && id % 100 < 30))) // never the stance's own start, loop or end alone
                if (!attempts.Contains((false, start)) && attempts.Count < 5)
                    attempts.Add((false, start));
            var reasons = new List<string>();
            ArtBuild? built = null;
            try
            {
                foreach (var (stance, start) in attempts)
                {
                    built = BuildArt(stance, start, out var reason);
                    if (built is not null)
                        break;
                    reasons.Add($"{(stance ? "stance" : start.ToString())}: {reason}");
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"arts: skipped {label}: {e.GetType().Name}: {e.Message}");
                continue;
            }
            if (built is null)
            {
                Console.WriteLine($"arts: skipped {label} (DS3 a{job.Ds3Section}): no move fits ({string.Join("; ", reasons)})");
                continue;
            }

            // The Ash of War row: the template weapon's, with the new art ID and DS3's FP cost.
            while (artParam[nextRow] is not null)
                nextRow += 10;
            var row = new PARAM.Row(nextRow, "", artParam.AppliedParamdef);
            for (int i = 0; i < row.Cells.Count; i++)
                row.Cells[i].Value = templateArtRow.Cells[i].Value is byte[] b ? b.Clone() : templateArtRow.Cells[i].Value;
            SetArtId(row, artId);
            if (fp >= 0)
                SetCell(row, "useMagicPoint_L2", fp);
            if (built.Stance || built.Light is not null || built.Heavy is not null)
            {
                // DS3 charges the follow-ups separately (FPCostLight, FpCostStrong; -1 = the skill's cost).
                SetCell(row, "useMagicPoint_R1", fpLight >= 0 ? fpLight : Math.Max(fp, 0));
                SetCell(row, "useMagicPoint_R2", fpHeavy >= 0 ? fpHeavy : Math.Max(fp, 0));
            }
            SetCell(row, "textId", nextRow);
            artParam.Rows.Add(row);
            artParam.Rows.Sort((a, c) => a.ID.CompareTo(c.ID));
            if (ds3Captions.Count == 0)
                foreach (var (k, v) in ItemText.Load(ds3Archives, "WeaponCaption"))
                    ds3Captions[k] = v;
            names[nextRow] = SkillName(ds3Captions.GetValueOrDefault(job.Weapons[0].Ds3Id))
                             ?? (string.IsNullOrWhiteSpace(ds3ArtName) ? null : ds3ArtName) ?? $"{job.Weapons[0].Name} Skill";
            foreach (var plan in job.Weapons)
                foreach (var w in erWeapons.Rows.Where(r => r.ID >= plan.NewId!.Value && r.ID < plan.NewId.Value + 10000))
                    SetCell(w, "swordArtsParamId", nextRow);
            if (built.Stance)
                stanceIds.Add(artId);
            if (built.Chain.Count > 0)
                chainIds.Add(artId);
            portedIds.Add(artId);
            free.Dequeue();
            arts++;
            weapons += job.Weapons.Count;
            if (built.Light is not null || built.Heavy is not null)
                followUps++;
            var followText = (built.Light is { } l ? $", R1 follow-up {l}" : "") + (built.Heavy is { } h ? $", R2 follow-up {h}" : "")
                             + (built.Chain.Count > 0 ? $", stance light chain {string.Join(" -> ", built.Chain)} (on Elden Ring a{built.ChainTemplate})" : "");
            Console.WriteLine($"arts: {label} -> art {artId} (a{section}, SwordArtsParam {nextRow} '{names[nextRow]}', FP {fp}/{fpLight}/{fpHeavy}) from DS3 a{job.Ds3Section} " +
                              $"{(job.Source == "class" ? "class skill " : "")}{(built.Stance ? "stance" : $"one-shot {built.Start}")}{followText} on Elden Ring a{built.TemplateSection}: " +
                              $"{built.Moves} moves, {built.Clips} behaviour clips{(reasons.Count > 0 ? $" (after {string.Join("; ", reasons)})" : "")}");
            nextRow += 10;
            continue;

            ArtBuild? BuildArt(bool stance, int start, out string reason)
            {
                // Follow-ups (one-shots): DS3 gives the skill a light (R1) and / or heavy (R2) follow-up when its
                // SwordArtsParam has an FP cost for one; they are the block's next moves, in order.
                var next = stance ? new List<int>() : OneShotFollowUps(job.Skill, start);
                int? light = !stance && fpLight >= 0 && next.Count > 0 ? next[0] : null;
                int? heavy = !stance && fpHeavy >= 0 && next.Count > (light is null ? 0 : 1) ? next[light is null ? 0 : 1] : null;
                if (windowProto is null)
                    light = heavy = null;
                // The template art: an Elden Ring stance art for stances; for one-shots the template weapon's own art
                // when it has every clip needed, else Lion's Claw's section (600), else any art that has them.
                int templateSection = -1;
                if (stance)
                    templateSection = StanceTemplates.FirstOrDefault(s => clipsBySection.TryGetValue(s, out var ids) && ids.Contains(40050) && taeSections.Contains(s), -1);
                else
                {
                    var own = 600 + ArtId(templateArtRow);
                    var candidates = new[] { own, 600 }.Concat(clipsBySection.Keys.Where(s => s is >= 600 and < 1000).Order()).Where(taeSections.Contains).ToList();
                    // Every follow-up first, then without the heavy one, then none.
                    var options = new List<(int? L, int? H)> { (light, heavy) };
                    if (heavy is not null)
                        options.Add((light, null));
                    if (light is not null || heavy is not null)
                        options.Add((null, null));
                    foreach (var (l, h) in options)
                    {
                        var need = new List<int> { 40000 };
                        if (l is not null) need.Add(40010);
                        if (h is not null) need.Add(40020);
                        templateSection = candidates.FirstOrDefault(c => clipsBySection.TryGetValue(c, out var ids) && need.All(ids.Contains), -1);
                        if (templateSection < 0)
                            continue;
                        (light, heavy) = (l, h);
                        break;
                    }
                }
                if (templateSection < 0 || BinderLeaf(erMain, $"a{templateSection}.tae") is not { } templateTaeFile)
                {
                    reason = "no Elden Ring art to start from";
                    return null;
                }
                var map = stance ? StanceMap.ToList() : new List<(int Er, int Ds3)> { (40000, start), (40005, start) };
                if (light is { } li)
                    map.AddRange([(40010, li), (40015, li)]);
                if (heavy is { } hi)
                    map.AddRange([(40020, hi), (40025, hi)]);
                // A stance's further light attacks (DS3: R1 again after the stance attack) go through Elden Ring's own
                // chain for Rellana's stance (hks art 318): stance light -> ComboEnd (40010) -> ComboEnd_2 (40020),
                // opened by combo window SpEffects 100054 / 100055. The stance template (Unsheathe's a614) has no
                // 40010/40020 clips, so those two moves start from an art that has them (the one-shot template).
                var chain = stance && chainHks ? StanceChainMoves(job.Skill) : [];
                int chainTemplate = -1;
                var chainOf = new Dictionary<int, int>(); // Elden Ring move -> its template section, when not templateSection
                if (chain.Count > 0)
                {
                    var need = ChainSlots.Take(chain.Count).Select(c => c.Er).ToList();
                    chainTemplate = new[] { 600 + ArtId(templateArtRow), 600 }.Concat(clipsBySection.Keys.Where(k => k is >= 600 and < 1000).Order())
                        .Where(taeSections.Contains).FirstOrDefault(c => clipsBySection.TryGetValue(c, out var ids) && need.All(ids.Contains), -1);
                    if (chainTemplate < 0)
                        chain.Clear();
                    for (int i = 0; i < chain.Count; i++)
                    {
                        map.AddRange([(ChainSlots[i].Er, chain[i]), (ChainSlots[i].NoMp, chain[i])]);
                        chainOf[ChainSlots[i].Er] = chainOf[ChainSlots[i].NoMp] = chainTemplate;
                    }
                }
                int SectionOf(int erId) => chainOf.GetValueOrDefault(erId, templateSection);
                var templateIds = clipsBySection[templateSection];
                if (chainTemplate > 0)
                    templateIds = [.. templateIds, .. clipsBySection[chainTemplate].Where(chainOf.ContainsKey)];
                TAE.ValidateEventBank = false;
                var templateTae = TAE.Read(templateTaeFile.Bytes);
                var tae = TAE.Read(templateTaeFile.Bytes);
                tae.ID = 2000 + section;
                if (chainTemplate > 0 && BinderLeaf(erMain, $"a{chainTemplate}.tae") is { } chainTaeFile)
                    foreach (var a in TAE.Read(chainTaeFile.Bytes).Animations.Where(a => chainOf.ContainsKey((int)a.ID) && tae.Animations.All(t => t.ID != a.ID)))
                        tae.Animations.Add(a);
                // Lengths from the chain template too: its moves (40010 / 40020) are built on that section.
                var erEventLengths = EventLengths(templateTae.Animations.Concat(tae.Animations).SelectMany(a => a.Events));
                var templateGroups = templateTae.Animations.SelectMany(a => a.Events).Where(e => e.Group is not null)
                    .GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First().Group!);
                var stretches = new Dictionary<int, MoveTiming.Stretch>();
                var kept = new List<TAE.Animation>();
                var why = new List<string>();
                var sources = new Dictionary<int, (List<TAE.Event> Events, float Duration)>();
                foreach (var (erId, ds3Id) in map)
                {
                    var anim = tae.Animations.FirstOrDefault(a => a.ID == erId);
                    if (anim is null || !templateIds.Contains(erId))
                    {
                        if (erId is 40000 or 40010 or 40020 or 40050)
                            why.Add($"a{templateSection} has no {erId}");
                        continue;
                    }
                    if (FindDs3(ds3Taes, ds3Main, ds3Id) is not { } source)
                    {
                        why.Add($"DS3 {ds3Id} not in a{string.Join("/a", ds3Taes.Select(t => t.Section))}");
                        continue;
                    }
                    var ds3Bytes = ds3Hkx.Read(source.HkxName);
                    if (ds3Bytes is null && source.FileName is { } alt && alt != source.HkxName)
                    {
                        ds3Bytes = ds3Hkx.Read(alt);
                        if (ds3Bytes is not null)
                            source = source with { HkxName = alt };
                    }
                    if (ds3Bytes is null)
                    {
                        why.Add($"no DS3 file {source.HkxName}");
                        continue;
                    }
                    var template = ErAnimFile(erArchives, $"a{SectionOf(erId):D3}_{erId:D6}")
                                   ?? ErAnimFile(erArchives, AnimSourceName(anim, SectionOf(erId), erId))
                                   ?? ErAnimFile(erArchives, "a000_000000")!;
                    var (hkx, _, erDuration) = AnimPort.ConvertTimed(ds3Bytes, ds3Skeleton, erSkeleton, template.Bytes, template.Compendium, AnimPort.Spline);
                    var newName = $"a{section:D3}_{erId:D6}";
                    if (erAnims.Files.Count >= ClipBudget.AnimFileLimit)
                        throw new InvalidOperationException($"the player's animation binder is full ({erAnims.Files.Count} files, the game takes {ClipBudget.AnimFileLimit})");
                    AddFile(erAnims, newName + ".hkx", hkx);
                    anim.MiniHeader = new TAE.Animation.AnimMiniHeader.Standard();
                    anim.AnimFileName = newName + ".hkt";
                    float ds3Duration = AnimPort.Duration(ds3Bytes);
                    anim.Events = MergeEvents(source.Events, anim, templateGroups, erEventLengths, effectEvents, effects, soundPort, ds3Duration, out var stretch);
                    sources[erId] = (source.Events, ds3Duration);
                    stretches[erId] = stretch;
                    timing.Add(TimingLine(section, label, erId, source, ds3Duration, erDuration, stretch));
                    kept.Add(anim);
                }
                if (kept.Count == 0 || (stance && !kept.Any(a => a.ID == 40050)) || (!stance && !kept.Any(a => a.ID == 40000)))
                {
                    reason = why.Count > 0 ? string.Join(", ", why.Distinct().Take(3)) : $"Elden Ring a{templateSection} has none of its moves";
                    return null;
                }
                // A follow-up that didn't build isn't offered.
                if (light is not null && !kept.Any(a => a.ID == 40010))
                    light = null;
                if (heavy is not null && !kept.Any(a => a.ID == 40020))
                    heavy = null;
                // The art's own combo windows: only the follow-ups this art has (a template's would lead to clips
                // the new section doesn't have).
                if (!stance && windowProto is not null)
                    foreach (var a in kept.Where(a => a.ID is 40000 or 40005))
                        SetComboWindows(a, windowProto, sources[(int)a.ID].Events, sources[(int)a.ID].Duration, light is not null, heavy is not null);
                // The stance chain's windows: the stance light attack opens 100054 (-> 40010) when the second light
                // attack built, 40010 opens 100055 (-> 40020) when the third did; the last move opens nothing. The
                // window is DS3's own chain marker (SpEffect 100490, see ChainSpan) when the move has one.
                if (stance && chain.Count > 0 && windowProto is not null)
                {
                    bool second = kept.Any(a => a.ID == ChainSlots[0].Er), third = kept.Any(a => a.ID == ChainSlots[1].Er);
                    foreach (var a in kept.Where(a => a.ID is 40060 or 40065))
                        SetComboWindows(a, windowProto, sources[(int)a.ID].Events, sources[(int)a.ID].Duration, second, false, ds3Marker: true);
                    foreach (var a in kept.Where(a => a.ID == ChainSlots[0].Er || a.ID == ChainSlots[0].NoMp))
                        SetComboWindows(a, windowProto, sources[(int)a.ID].Events, sources[(int)a.ID].Duration, third, false, lightWindow: 100055, ds3Marker: true);
                    foreach (var a in kept.Where(a => a.ID == ChainSlots[1].Er || a.ID == ChainSlots[1].NoMp))
                        SetComboWindows(a, windowProto, sources[(int)a.ID].Events, sources[(int)a.ID].Duration, false, false);
                    chain = chain.Take(third ? 2 : second ? 1 : 0).ToList();
                    if (!second)
                        kept.RemoveAll(a => chainOf.ContainsKey((int)a.ID));
                }
                tae.Animations = kept;
                AddFile(erMain, $"a{section}.tae", tae.Write(), templateTaeFile);

                // Behaviour: the template art's clip for each move, under the same selector, for the new section.
                int registered = 0;
                var internalIds = new Dictionary<string, short>();
                foreach (var selector in artSelectors.Where(s => kept.Any(a => a.ID == s.m_animId)))
                {
                    foreach (var clip in selector.m_generators.OfType<hkbClipGenerator>().ToList())
                    {
                        var m = AnimName().Match(clip.m_animationName ?? "");
                        if (!m.Success || int.Parse(m.Groups[1].Value) != SectionOf(int.Parse(m.Groups[2].Value)))
                            continue;
                        var name = $"a{section:D3}_{m.Groups[2].Value}";
                        if (!internalIds.TryGetValue(name, out var internalId))
                            internalIds[name] = internalId = checked((short)internalIdNext++);
                        var copy = HavokGraph.ShallowCopy(clip);
                        copy.m_animationName = name;
                        copy.m_name = clip.m_name?.Replace(clip.m_animationName!, name, StringComparison.Ordinal);
                        copy.m_animationInternalId = internalId;
                        if (stretches.TryGetValue(int.Parse(m.Groups[2].Value), out var moveStretch))
                            FitClip(copy, moveStretch);
                        selector.m_generators.Add(copy);
                        registered++;
                    }
                }
                reason = "";
                return new ArtBuild(stance, start, light, heavy, templateSection, kept.Count, registered, chain, chainTemplate);
            }
        }
        if (followUps > 0 || windowProto is null)
            Console.WriteLine(windowProto is null
                ? "arts: no Elden Ring art has an R1/R2 combo window event, ported one-shots get no follow-ups"
                : $"arts: {followUps} arts have R1/R2 follow-ups (combo window event type {windowProto.Type})");
        nextInternalId = internalIdNext;
        LockArts(er, ports, erWeapons);
        // The hks goes with every build that has arts (the extra IDs rely on it), and is removed otherwise.
        bool hksWritten = false;
        if (hks is not null && arts > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HksPath(ctx))!);
            File.WriteAllText(HksPath(ctx), PatchHks(hks, stanceIds, portedIds.Where(id => id < 300).ToList(), chainIds), new System.Text.UTF8Encoding(false));
            hksWritten = true;
        }
        else if (File.Exists(HksPath(ctx)))
            File.Delete(HksPath(ctx));
        Console.WriteLine($"arts: {arts} DS3 weapon arts for {weapons} weapons ({stanceIds.Count} stances, {chainIds.Count} with a light chain{(hksWritten ? ", c0000.hks written" : ", no c0000.hks")})" +
                          (erAshes > 0 ? $"; {erAshes} generic class skills use Elden Ring's own Ash of War of the same kind (no free art ID left)" : ""));
        return new ArtResult(arts, weapons, names, stanceIds);
    }

    private static int? WindowId(TAE.Event e) =>
        e.GetParameterBytes(false) is { Length: >= 4 } b && BitConverter.ToInt32(b, 0) is >= 100050 and <= 100055 and var id ? id : null;

    /// <summary>The first art combo window event (SpEffect 100050-100055) in the given Elden Ring art sections.</summary>
    private static WindowEvent? FindWindowEvent(BND4 erMain, IEnumerable<int> sections)
    {
        TAE.ValidateEventBank = false;
        foreach (var section in sections.Order().Take(150))
        {
            if (BinderLeaf(erMain, $"a{section}.tae") is not { } file)
                continue;
            var e = TAE.Read(file.Bytes).Animations.SelectMany(a => a.Events).FirstOrDefault(e => e.Group is not null && WindowId(e) is not null);
            if (e is not null)
                return new WindowEvent(e.Type, (byte[])e.GetParameterBytes(false).Clone(), e.Group!);
        }
        return null;
    }

    /// <summary>
    /// Gives a one-shot art move the combo windows for its follow-ups only: R1 (SpEffect 100054, into ComboEnd 40010)
    /// for the light one, R2 (100051, into ComboEnd_2 40020) for the heavy one. The window is DS3's own when the DS3
    /// move sets one of these SpEffects. Otherwise it opens where DS3 first lets the move be left after its last hit
    /// (its first JumpTable cancel; DS3's own art windows are per-weapon hks SpEffects such as 100570 "Harald Curved
    /// Greatsword", so they can't be read by ID) and stays open to the end of the DS3 move: the hks starts the
    /// follow-up at once when R1/R2 is pressed inside the window (SwordArtsOneShot_onUpdate), so a window that opened
    /// at 45% of the move, as before, either came before the swing could be cancelled or after the player had pressed
    /// R1 (which then played a plain R1: the Ringed Knight pair's art "didn't chain"). Without a cancel, the second half
    /// of the move, as before. Other windows (the template's) are removed.
    /// </summary>
    internal static void SetComboWindows(TAE.Animation anim, WindowEvent proto, List<TAE.Event>? ds3Events, float duration, bool light, bool heavy,
        int lightWindow = 100054, bool ds3Marker = false)
    {
        anim.Events.RemoveAll(e => e.Type == proto.Type && WindowId(e) is not null);
        if (!light && !heavy)
            return;
        var ds3Window = ds3Events?.FirstOrDefault(e => e.Type == proto.Type && WindowId(e) is not null);
        float? exit = MoveTiming.Guard && ds3Window is null && ds3Events is not null ? MoveTiming.Recovery(ds3Events, HitStarts(ds3Events)) : null;
        float start = ds3Window?.StartTime ?? exit ?? duration * 0.45f;
        float end = ds3Window?.EndTime ?? (exit is not null ? Math.Max(duration, start + 0.5f) : duration * 0.9f);
        // Stance chains: a DS3 stance attack holds SpEffect 100490 ("[HKS] Non-generator Transition - Friede's Great
        // Scythe"; also Lockout's a250 and the Aquamarine Dagger's a256) while R1 goes to the next attack of the chain,
        // from the start of the move (Friede's 36230: 0-2.5 s, 36250: 0-2.2 s; the dagger's 36230: 0-0.87 s). DS3
        // takes the press once the move's R1 cancel (JumpTable 4, 36230: from 1.6 s) is open; when 100490 ends R1 is a
        // plain attack again (Friede's then carry 100500, the generic marker, to the end: 2.5-3.9 s). 1.0.242-1.0.271
        // read 100500 as the chain window, so the chain opened at 2.5 s, after the plain R1 the cancels had allowed
        // since 1.9 s: the stance's light attacks never chained. The window is 100490's span from where the R1 cancel
        // opens (not before the last hit ends) to its end.
        if (ds3Marker && ds3Window is null && ChainSpan(ds3Events) is { } span)
            (start, end) = span;
        if (end <= start)
            end = start + 0.5f;
        var group = anim.Events.FirstOrDefault(e => e.Type == proto.Type && e.Group is not null)?.Group;
        if (group is null)
        {
            group = proto.Group.GetClone();
            anim.EventGroups ??= [];
            anim.EventGroups.Add(group);
        }
        foreach (var id in new[] { light ? lightWindow : 0, heavy ? 100051 : 0 }.Where(id => id > 0))
        {
            var bytes = (byte[])proto.Bytes.Clone();
            BitConverter.GetBytes(id).CopyTo(bytes, 0);
            anim.Events.Add(new TAE.Event(start, end, proto.Type, 0, bytes, false) { Group = group });
        }
        // The window alone did nothing in 1.0.221 (the hks fired the follow-up, the behaviour didn't take it): the game
        // accepts the follow-up input only while the matching cancel JumpTable is open. Every vanilla art says which
        // (c0000.anibnd, all a6xx-a9xx): an R2 window (100050/100051) comes with JumpTable 116 (Stamp (Upward Cut),
        // a606 40000: window 0.33-1.43 s, 116 0.40-2.17 s), an R1 window (100054/100055) with JumpTable 115 (Rellana's
        // stance light a918 40060: 100054 1.17-1.67 s, 115 1.17-1.37 s; a848/a964 with both windows have both). 1.0.251
        // to 1.0.259 opened 116 for every window, so R2 follow-ups chained and R1 ones (Ember's thrust, every stance
        // light chain) were never accepted. Each window now opens its own cancel for the whole window.
        if (anim.Events.FirstOrDefault(e => e.Type == 0 && e.Group is not null && e.GetParameterBytes(false).Length >= 4) is { } jumpProto)
            foreach (var jump in new[] { light ? LightFollowUpJumpTable : 0, heavy ? HeavyFollowUpJumpTable : 0 }.Where(id => id > 0))
            {
                var bytes = (byte[])jumpProto.GetParameterBytes(false).Clone();
                BitConverter.GetBytes(jump).CopyTo(bytes, 0);
                anim.Events.Add(new TAE.Event(start, end, 0, jumpProto.Unk04, bytes, false) { Group = jumpProto.Group });
            }
    }

    /// <summary>TAE JumpTable (type 0) ID Elden Ring opens over an art's R1 follow-up window (100054/100055: a918, a848).</summary>
    internal const int LightFollowUpJumpTable = 115;

    /// <summary>TAE JumpTable (type 0) ID Elden Ring opens over an art's R2 follow-up window (100050/100051: a606, a848).</summary>
    internal const int HeavyFollowUpJumpTable = 116;

    /// <summary>DS3 SpEffect "[HKS] Non-generator Transition - Friede's Great Scythe": a DS3 stance attack carries it
    /// while R1 continues the stance's light chain (100500, the generic marker after it, is not the chain).</summary>
    internal const int Ds3ChainMarker = 100490;

    /// <summary>DS3 TAE JumpTable (type 0) ID that lets R1 leave a move.</summary>
    internal const int Ds3LightAttackCancel = 4;

    /// <summary>
    /// When a DS3 stance attack takes R1 for the next attack of its chain: the span of its chain marker (SpEffect
    /// 100490) from where the move's R1 cancel opens, never before its last hit ends, to the marker's end. Null when
    /// the move has no marker. A marker that ends before the cancel opens keeps its last half second.
    /// </summary>
    internal static (float Start, float End)? ChainSpan(List<TAE.Event>? ds3Events)
    {
        static int Id(TAE.Event e) => e.GetParameterBytes(false) is { Length: >= 4 } b ? BitConverter.ToInt32(b, 0) : -1;
        if (ds3Events?.FirstOrDefault(e => e.Type is 66 or 67 && Id(e) == Ds3ChainMarker) is not { } marker)
            return null;
        float open = marker.StartTime;
        var cancels = ds3Events.Where(e => e.Type == 0 && Id(e) == Ds3LightAttackCancel && e.EndTime > marker.StartTime).ToList();
        if (cancels.Count > 0)
            open = Math.Max(open, cancels.Min(e => e.StartTime));
        var hits = ds3Events.Where(e => e.Type == 1 && e.StartTime < marker.EndTime).ToList();
        if (hits.Count > 0)
            open = Math.Max(open, hits.Max(e => e.EndTime));
        if (open > marker.EndTime - 0.2f)
            open = Math.Max(marker.StartTime, marker.EndTime - 0.5f);
        return (open, marker.EndTime);
    }

    /// <summary>
    /// Ties every ported weapon to its art, as DS3 does: no Ash of War can be put on it or its affinity changed, like
    /// Elden Ring's unique weapons (their gemMountType, read from the weapons whose art no Ash of War gives).
    /// DARKSIGN_WEAPON_ARTS=unlocked keeps the template's (Ashes of War allowed).
    /// </summary>
    private static void LockArts(ParamStore er, IReadOnlyList<WeaponPlan> ports, PARAM erWeapons)
    {
        if (ArtsMode == "unlocked")
        {
            Console.WriteLine("arts: weapons not locked (unlocked fallback), Ashes of War can replace the ported arts");
            return;
        }
        var arts = er.Get("SwordArtsParam")!;
        var gemArts = er.Get("EquipParamGem")?.Rows.Select(r => Convert.ToInt32(r["swordArtsParamId"]?.Value ?? -1)).ToHashSet() ?? [];
        var ported = ports.Where(p => p.NewId is not null).Select(p => p.NewId!.Value).ToList();
        bool IsPorted(int id) => ported.Any(n => id >= n && id < n + 10000);
        var uniques = erWeapons.Rows.Where(r => r.ID % 10000 == 0 && !IsPorted(r.ID) && r["gemMountType"] is not null)
            .Where(r => Convert.ToInt32(r["swordArtsParamId"].Value) is var a && a > 0 && !gemArts.Contains(a) && arts[a] is { } row && ArtId(row) > 0)
            .Select(r => Convert.ToInt32(r["gemMountType"]!.Value)).Where(v => v != 2).ToList();
        if (uniques.Count == 0)
        {
            Console.WriteLine("arts: weapons not locked: no Elden Ring unique weapon to copy gemMountType from");
            return;
        }
        int lockValue = uniques.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        int rows = 0;
        foreach (var w in erWeapons.Rows.Where(r => IsPorted(r.ID)))
        {
            SetCell(w, "gemMountType", lockValue);
            SetCell(w, "disableGemAttr", 1);
            rows++;
        }
        Console.WriteLine($"arts: {ported.Count} ported weapons locked to their art ({rows} rows, gemMountType {lockValue} like {uniques.Count} Elden Ring unique weapons, affinity fixed)");
    }

    private static string AnimSourceName(TAE.Animation anim, int section, int id) =>
        anim.MiniHeader is TAE.Animation.AnimMiniHeader.ImportOtherAnim imp && imp.ImportFromAnimID > 0
            ? $"a{imp.ImportFromAnimID / 1_000_000:D3}_{imp.ImportFromAnimID % 1_000_000:D6}"
            : $"a{section:D3}_{id:D6}";

    /// <summary>A SwordArtsParam row's art ID: the u16 Paramdex labels shieldIconType (low byte) + pad (high byte).</summary>
    internal static int ArtId(PARAM.Row row)
    {
        int low = row["shieldIconType"]?.Value is { } v ? Convert.ToInt32(v) & 0xFF : 0;
        int high = row["pad"]?.Value is byte[] { Length: > 0 } pad ? pad[0] : 0;
        return low | high << 8;
    }

    internal static void SetArtId(PARAM.Row row, int id)
    {
        var low = row["shieldIconType"]!;
        low.Value = Convert.ChangeType(unchecked((sbyte)(id & 0xFF)), low.Value!.GetType());
        if (row["pad"]?.Value is byte[] { Length: > 0 } pad)
        {
            var copy = (byte[])pad.Clone();
            copy[0] = (byte)(id >> 8);
            row["pad"]!.Value = copy;
        }
    }

    private static void SetCell(PARAM.Row row, string field, int value)
    {
        if (row[field] is { } cell)
            cell.Value = Convert.ChangeType(value, cell.Value!.GetType());
    }

    /// <summary>DS3's weapon art row (EquipParamWeapon swordArtId -> SwordArtsParam): FP for the skill, its light and
    /// heavy follow-ups (-1 = none given), and the row's name. All -1 when DS3 has no such row.</summary>
    private static (int Fp, int Light, int Heavy, string? Name) Ds3Art(ParamStore ds3, PARAM ds3Weapons, int ds3Id)
    {
        int id = Convert.ToInt32(ds3Weapons[ds3Id]!["swordArtId"].Value);
        if (ds3.Get("SwordArtsParam") is not { } arts || arts[id] is not { } row)
            return (-1, -1, -1, null);
        int Get(string field) => Field(row, field)?.Value is { } v and not byte[] ? Convert.ToInt32(v) : -1;
        return (Get("FPCost"), Get("FPCostLight"), Get("FpCostStrong"), row.Name);
    }

    /// <summary>
    /// A cell by field name, or null. PARAM.Row[name] throws ("Sequence contains no matching element") when the def
    /// lacks the name, and Paramdex's DS3 SwordArtsParam def writes some names with a range ("u8 ActionId [0-36]"),
    /// which SoulsFormats keeps in the name: indexing "ActionId" failed every art of the 1.0.199-1.0.202 builds.
    /// </summary>
    internal static PARAM.Cell? Field(PARAM.Row row, string name) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName == name)
        ?? row.Cells.FirstOrDefault(c => c.Def.InternalName.StartsWith(name + " ", StringComparison.Ordinal)
                                         || c.Def.InternalName.StartsWith(name + "[", StringComparison.Ordinal));

    /// <summary>"Skill: Spin Sweep" from a DS3 weapon description, if it names one.</summary>
    internal static string? SkillName(string? caption)
    {
        if (caption is null)
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(caption, @"Skill:\s*([^\r\n]+)");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string HksPath(PorterContext ctx) => ctx.ModPath("action", "script", "c0000.hks");

    /// <summary>ividyon's c0000.hks (cached under Work\cache), or null when it can't be had: then stance arts aren't
    /// built as stances and only the IDs Elden Ring's own hks treats as plain one-shots are used.</summary>
    private static string? LoadHks(PorterContext ctx)
    {
        try
        {
            var cache = ctx.WorkPath("cache", "eldenringhks", "c0000.hks");
            if (!File.Exists(cache))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                File.WriteAllBytes(cache, http.GetByteArrayAsync(HksUrl).GetAwaiter().GetResult());
            }
            var text = File.ReadAllText(cache);
            PatchHks(text, []); // refuses a file without IsStanceArts
            return text;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine($"arts: no c0000.hks ({e.GetType().Name}: {e.Message}); stance arts become one-shots and only art IDs 170-254/300-398 are used");
            return null;
        }
    }

    /// <summary>Art IDs the hks gives behaviour of its own outside the SwordArtsCategory table: compared with an art ID
    /// variable (c_SwordArtsID == 318, arts_id ~= 17), inside a range test (130 <= arts_id and arts_id <= 140), or
    /// listed in an aow_list. Those are never handed to a ported art. (Table entries are overridden by PatchHks.)</summary>
    internal static HashSet<int> HksNamedIds(string hks)
    {
        const string artVar = @"(?:c_SwordArtsID|arts_?id|art_?id|artsID|weaponswordartid)";
        var opt = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        int table = hks.IndexOf("SwordArtsCategory = {", StringComparison.Ordinal);
        int tableEnd = table < 0 ? -1 : hks.IndexOf("\n}", table, StringComparison.Ordinal);
        var body = table < 0 || tableEnd < 0 ? hks : hks[..table] + hks[tableEnd..];
        var ids = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, artVar + @"\s*(?:[=~]=|<=?|>=?)\s*(\d{1,3})\b", opt))
            ids.Add(int.Parse(m.Groups[1].Value));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"\b(\d{1,3})\s*(?:<=?|>=?)\s*" + artVar, opt))
            ids.Add(int.Parse(m.Groups[1].Value));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body,
                     @"\b(\d{1,3})\s*<=?\s*" + artVar + @"\s+and\s+" + artVar + @"\s*<=?\s*(\d{1,3})\b", opt))
            for (int id = int.Parse(m.Groups[1].Value); id <= int.Parse(m.Groups[2].Value); id++)
                ids.Add(id);
        foreach (System.Text.RegularExpressions.Match list in System.Text.RegularExpressions.Regex.Matches(body, @"aow_\w+\s*=\s*\{([^}]*)\}"))
            foreach (System.Text.RegularExpressions.Match n in System.Text.RegularExpressions.Regex.Matches(list.Groups[1].Value, @"(?m)^\s*(\d{1,3})\b"))
                ids.Add(int.Parse(n.Groups[1].Value));
        return ids;
    }

    private const string ChainState = "W_SwordArtsOneShotComboEnd_MesmerSowrdArts";
    private const string ChainTest = "c_SwordArtsID == 318";
    private const string ChainFunction = "DarkSignIsStanceChain";

    /// <summary>Whether the hks has the art-318 chain to generalise: a test of art 318 next to the chain state.</summary>
    internal static bool CanChainStances(string hks) => ChainLines(hks.Split('\n')).Any();

    private static IEnumerable<int> ChainLines(string[] lines) =>
        Enumerable.Range(0, lines.Length).Where(i => lines[i].Contains(ChainTest, StringComparison.Ordinal)
                                                     && lines.Skip(i).Take(3).Any(l => l.Contains(ChainState, StringComparison.Ordinal)));

    /// <summary>
    /// Adds the ported arts to ividyon's c0000.hks: stance IDs into IsStanceArts' list; IDs below 300 as plain default
    /// animations; and the stance light chain. Elden Ring's hks chains a stance's light attacks for one art only,
    /// Rellana's Moon-and-Fire Stance (318): in DrawStanceRightAttackLight_onUpdate, R1 with SpEffect 100054 goes to
    /// W_SwordArtsOneShotComboEnd_MesmerSowrdArts (animation 40010) and with 100055 to W_SwordArtsOneShotComboEnd_2
    /// (40020); ArtsCommonFunction charges those as the R1 art attack. Every test of "c_SwordArtsID == 318" that
    /// names that state is widened to the ported arts in chainIds (a DarkSignIsStanceChain function added before
    /// IsStanceArts); the 318 tests about where the stance itself charges FP are left alone.
    /// </summary>
    internal static string PatchHks(string hks, IReadOnlyList<int> stanceIds, IReadOnlyCollection<int>? defaultIds = null, IReadOnlyCollection<int>? chainIds = null)
    {
        const string fn = "function IsStanceArts(arts_id)";
        int at = hks.IndexOf(fn, StringComparison.Ordinal);
        int list = at < 0 ? -1 : hks.IndexOf("local aow_list = {", at, StringComparison.Ordinal);
        if (list < 0)
            throw new InvalidDataException("c0000.hks has no IsStanceArts list");
        int insert = hks.IndexOf('\n', list) + 1;
        var lines = string.Concat(stanceIds.Select(id => $"        {id}, -- The Dark Sign: DS3 stance art\n"));
        hks = hks[..insert] + lines + hks[insert..];
        if (chainIds is { Count: > 0 })
        {
            var text = hks.Split('\n');
            var chainLines = ChainLines(text).ToList();
            if (chainLines.Count == 0)
                throw new InvalidDataException("c0000.hks has no art-318 stance chain to widen");
            foreach (int i in chainLines)
                text[i] = text[i].Replace(ChainTest, $"({ChainTest} or {ChainFunction}(c_SwordArtsID))", StringComparison.Ordinal);
            hks = string.Join('\n', text);
            var test = string.Join(" or ", chainIds.Order().Select(id => $"arts_id == {id}"));
            at = hks.IndexOf(fn, StringComparison.Ordinal);
            hks = hks[..at] + $"-- The Dark Sign: DS3 stance arts whose light attacks chain like Rellana's (art 318)\nfunction {ChainFunction}(arts_id)\n" +
                  $"    if {test} then\n        return TRUE\n    end\n    return FALSE\nend\n\n" + hks[at..];
        }
        // Ported arts on IDs below 300 that SwordArtsCategory has a row for: plain default animations, like Elden Ring's
        // own IDs from 300 up (the row would ask for per-weapon-class variants the DS3 art doesn't have).
        if (defaultIds is { Count: > 0 })
        {
            const string diff = "function GetSwordArtsDiffCategory(artID, idleCat, wepCat)";
            int d = hks.IndexOf(diff, StringComparison.Ordinal);
            if (d < 0)
                throw new InvalidDataException("c0000.hks has no GetSwordArtsDiffCategory");
            int after = hks.IndexOf('\n', d) + 1;
            var test = string.Join(" or ", defaultIds.Order().Select(id => $"artID == {id}"));
            hks = hks[..after] + $"    if {test} then -- The Dark Sign: DS3 weapon arts\n        return SWORD_ART_DIFF_CAT_DEFAULT\n    end\n" + hks[after..];
        }
        return hks;
    }

    /// <summary>Adds the ported arts' names to Elden Ring's DLC02 art name text (ArtsName_dlc02), in the built mod.</summary>
    public static void WriteArtNames(PorterContext ctx, GameArchives er, IReadOnlyDictionary<int, string> names)
    {
        if (names.Count == 0)
            return;
        var path = ctx.ModPath("msg", "engus", "item_dlc02.msgbnd.dcx");
        var (bytes, compression) = File.Exists(path)
            ? (DCX.Decompress(File.ReadAllBytes(path), out DCX.CompressionInfo info), info)
            : er.ReadWithCompression("/msg/engus/item_dlc02.msgbnd.dcx");
        var bnd = BND4.Read(bytes);
        var file = bnd.Files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)).Equals("ArtsName_dlc02", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            Console.WriteLine("arts: item_dlc02 has no ArtsName_dlc02.fmg, ported arts show their template's name");
            return;
        }
        var fmg = FMG.Read(file.Bytes);
        foreach (var (id, name) in names)
        {
            fmg.Entries.RemoveAll(e => e.ID == id);
            fmg.Entries.Add(new FMG.Entry(id, name));
        }
        file.Bytes = fmg.Write();
        File.WriteAllBytes(path, DCX.Compress(bnd.Write(), compression));
    }
}

/// <summary>arts-selftest: offline checks of the weapon art port's pure parts (no game files needed).</summary>
public static class ArtSelfTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        const string hks = "function IsStanceArts(arts_id)\n    local aow_list = {\n        10, -- Wild Strikes\n    }\nend\n";
        var patched = MovesetPort.PatchHks(hks, [300, 301]);
        Check(patched.Contains("local aow_list = {\n        300, -- The Dark Sign: DS3 stance art\n        301,"), "hks: ported stance IDs go first in IsStanceArts' list");
        Check(patched.Contains("        10, -- Wild Strikes"), "hks: Elden Ring's own stances stay listed");
        bool threw = false;
        try { MovesetPort.PatchHks("function Other()\nend\n", [300]); } catch (InvalidDataException) { threw = true; }
        Check(threw, "hks: a file without IsStanceArts is refused (the build then skips the hks)");
        var named = MovesetPort.HksNamedIds("if c_SwordArtsID == 318 then end\nif arts_id ~= 17 then end\nif 130 <= arts_id and arts_id <= 140 then end\n" +
                                            "local aow_list = {\n        10, -- Wild Strikes\n        372\n    }\nSwordArtsCategory = {\n    [26] = {SWORD_ART_DIFF_CAT_DEFAULT},\n}\nlocal idx = 0\nif idx == 26 then end\n");
        Check(named.SetEquals([318, 17, 10, 372, .. Enumerable.Range(130, 11)]), $"hks: art IDs with behaviour of their own found (got {string.Join(" ", named.Order())})");
        var withDefault = MovesetPort.PatchHks("function IsStanceArts(arts_id)\n    local aow_list = {\n    }\nend\nfunction GetSwordArtsDiffCategory(artID, idleCat, wepCat)\n    return 1\nend\n", [], [26, 27]);
        Check(withDefault.Contains("function GetSwordArtsDiffCategory(artID, idleCat, wepCat)\n    if artID == 26 or artID == 27 then"), "hks: ported IDs below 300 get the default animations");
        Check(MovesetPort.SkillName("A great scythe.\n\nSkill: Spin Sweep\nSpin the scythe.") == "Spin Sweep", "skill name read from a DS3 description");
        Check(MovesetPort.SkillName("No skill here.") is null, "no skill name when the description names none");
        // Art IDs: unique arts (Ember, Elfriede's Stance) come before generic class skills; a generic one without an ID takes Elden Ring's.
        Check(!MovesetPort.IsGenericArt("Ember") && !MovesetPort.IsGenericArt("Elfriede's Stance") && MovesetPort.IsGenericArt("Stomp") && MovesetPort.IsGenericArt(" warcry "),
            "arts: Ember and Elfriede's Stance are unique arts, Stomp and Warcry generic class skills");
        Check(MovesetPort.GenericArts["Stomp"] == "Stamp (Upward Cut)" && MovesetPort.GenericArts["Stance"] == "Square Off" && MovesetPort.GenericArts["Steady Chant"] is null,
            "arts: generic skills map to Elden Ring's own Ash of War (Stomp -> Stamp (Upward Cut), Stance -> Square Off), staves' chant to none");
        Check(MovesetPort.ErAshRow("Stamp (Upward Cut)", new Dictionary<int, string> { [10100] = "Ash of War: Stamp (Upward Cut)" }, null) is null,
            "arts: no Elden Ring ash row without EquipParamGem");
        // Combo windows: a template's window is replaced by the ported art's own follow-ups (R1 -> 100054, R2 -> 100051).
        var group = new SoulsFormats.TAE.EventGroup(67);
        byte[] Win(int id) { var b = new byte[16]; BitConverter.GetBytes(id).CopyTo(b, 0); return b; }
        var proto = new MovesetPort.WindowEvent(67, Win(100050), group);
        var move = new SoulsFormats.TAE.Animation(40000, new SoulsFormats.TAE.Animation.AnimMiniHeader.Standard(), "a906_040000.hkt")
            { Events = [new SoulsFormats.TAE.Event(0.2f, 0.9f, 67, 0, Win(100055), false) { Group = group }, new SoulsFormats.TAE.Event(0, 0.1f, 67, 0, Win(100280), false) { Group = group }] };
        var jumpGroup = new SoulsFormats.TAE.EventGroup(0);
        byte[] Jump(int id) { var b = new byte[16]; BitConverter.GetBytes(id).CopyTo(b, 0); BitConverter.GetBytes(-1).CopyTo(b, 8); BitConverter.GetBytes(6).CopyTo(b, 12); return b; }
        move.Events.Add(new SoulsFormats.TAE.Event(0, 0.3f, 0, 0, Jump(7), false) { Group = jumpGroup });
        MovesetPort.SetComboWindows(move, proto, [new SoulsFormats.TAE.Event(0.5f, 1.2f, 67, 0, Win(100050), false)], 2f, light: true, heavy: true);
        var ids = move.Events.Where(e => e.Type == 67).Select(e => BitConverter.ToInt32(e.GetParameterBytes(false), 0)).Order().ToList();
        Check(ids.SequenceEqual([100051, 100054, 100280]), $"arts: one-shot gets R1 and R2 follow-up windows, the template's is dropped, other effects stay (got {string.Join(" ", ids)})");
        int JumpId(SoulsFormats.TAE.Event e) => BitConverter.ToInt32(e.GetParameterBytes(false), 0);
        var jump116 = move.Events.Where(e => e.Type == 0 && JumpId(e) == MovesetPort.HeavyFollowUpJumpTable).ToList();
        var jump115 = move.Events.Where(e => e.Type == 0 && JumpId(e) == MovesetPort.LightFollowUpJumpTable).ToList();
        Check(jump116.Count == 1 && jump116[0].StartTime == 0.5f && jump116[0].EndTime == 1.2f && jump116[0].Group == jumpGroup && jump116[0].GetParameterBytes(false)[12] == 6,
            $"arts: cancel JumpTable 116 opens with the R2 follow-up window, in the move's JumpTable group (got {jump116.Count})");
        Check(jump115.Count == 1 && jump115[0].StartTime == 0.5f && jump115[0].EndTime == 1.2f && jump115[0].Group == jumpGroup,
            $"arts: cancel JumpTable 115 opens with the R1 follow-up window (got {jump115.Count})");
        move.Events.RemoveAll(e => e.Type == 0 && JumpId(e) is MovesetPort.HeavyFollowUpJumpTable or MovesetPort.LightFollowUpJumpTable);
        Check(move.Events.Where(e => BitConverter.ToInt32(e.GetParameterBytes(false), 0) is 100051 or 100054).All(e => e.StartTime == 0.5f && e.EndTime == 1.2f), "arts: follow-up windows use DS3's own timing");
        MovesetPort.SetComboWindows(move, proto, null, 2f, light: false, heavy: false);
        Check(move.Events.Count(e => e.Type == 67) == 1 && !move.Events.Any(e => e.Type == 0 && JumpId(e) is MovesetPort.HeavyFollowUpJumpTable or MovesetPort.LightFollowUpJumpTable),
            "arts: a one-shot without follow-ups has no combo window and no 115/116");
        // No DS3 window of these IDs (DS3 uses per-weapon hks SpEffects): the window opens at DS3's first cancel after
        // the last hit (JumpTable at 1.567 s) and stays open to the end of the DS3 move (3.0 s); before it was 45-90%.
        byte[] Jt(int id) { var b = new byte[16]; BitConverter.GetBytes(id).CopyTo(b, 0); return b; }
        var rkpg = new List<SoulsFormats.TAE.Event>
        {
            new(0.9f, 1.1f, 1, 0, new byte[16], false), new(0.767f, 2.933f, 67, 0, Win(100570), false), new(1.567f, 3.0f, 0, 0, Jt(4), false),
        };
        MovesetPort.SetComboWindows(move, proto, rkpg, 3f, light: true, heavy: true);
        var windows = move.Events.Where(e => e.Type == 67 && BitConverter.ToInt32(e.GetParameterBytes(false), 0) is 100051 or 100054).ToList();
        Check(windows.Count == 2 && windows.All(w => Math.Abs(w.StartTime - 1.567f) < 0.001f && Math.Abs(w.EndTime - 3f) < 0.001f),
            $"arts: without a DS3 window of its own, the follow-up window runs from DS3's first cancel to the move's end (got {string.Join(" ", windows.Select(w => $"{w.StartTime:0.000}-{w.EndTime:0.000}"))})");
        var engine = MovesetPort.HksEffectIds("if env(GetSpEffectID, 102355) == TRUE then\nelseif env( GetSpEffectID , 30) == TRUE then end");
        Check(engine.Contains(102355) && engine.Contains(30) && engine.Contains(100280) && engine.Contains(100054) && !engine.Contains(6340),
            "effects: the hks's own SpEffect IDs (and charge / combo windows) are kept, others are ported");
        // Parry: a DS3 parry judge in an attack event becomes Elden Ring's 591; other judges stay.
        MovesetPort.Ds3ParryJudges = [450, 600];
        byte[] Atk(int judge) { var b = new byte[16]; b[0] = 0x40; BitConverter.GetBytes(judge).CopyTo(b, 8); b[12] = 0xFF; return b; }
        var parry = Atk(450); var hit = Atk(700);
        Check(MovesetPort.RemapParryJudge(parry) && BitConverter.ToInt32(parry, 8) == 591 && parry[0] == 0x40 && !MovesetPort.RemapParryJudge(hit) && BitConverter.ToInt32(hit, 8) == 700,
            "parry: DS3 parry judges 450/600 become Elden Ring's 591, other judges and the event's flags stay");
        MovesetPort.Ds3ParryJudges = [];
        // DS3 hks markers in TAE SpEffect events: kept only when Elden Ring names the ID the same, else dropped; other
        // effects are kept when Elden Ring's hks checks them, else ported.
        Check(MovesetPort.TaeEffectFate(100280, "[HKS] Is Charging Heavy Attack", "[HKS] Is Charging Heavy Attack", true) == MovesetPort.TaeEffect.Keep, "effects: a marker both games name the same is kept");
        Check(MovesetPort.TaeEffectFate(100500, "[HKS] Non-generator Transition", "[HKS] Sword Arts - Raptor Dodge", true) == MovesetPort.TaeEffect.Drop, "effects: a DS3 marker Elden Ring's hks reads as something else is dropped");
        Check(MovesetPort.TaeEffectFate(100490, "[HKS] Non-generator Transition - Friede's Great Scythe", null, false) == MovesetPort.TaeEffect.Drop, "effects: a DS3 marker Elden Ring lacks is dropped, not ported");
        Check(MovesetPort.TaeEffectFate(31, "Counter damage (puncture)", "Behavior - Counter Frames", false) == MovesetPort.TaeEffect.Port, "effects: a real DS3 effect is ported");
        Check(MovesetPort.TaeEffectFate(6340, "Weapon Art - Stomp", "Toughness Unk - Damage Level Small", true) == MovesetPort.TaeEffect.Keep, "effects: a real effect the hks checks by ID is kept");
        // Node flags (ModelPort.SetNodeFlags): dummy owners, mesh node; a Disabled node is left alone.
        var flver = new SoulsFormats.FLVER2();
        flver.Nodes.Add(new SoulsFormats.FLVER.Node { Name = "Dummy_SFX" });
        flver.Nodes.Add(new SoulsFormats.FLVER.Node { Name = "Dummy_Plan" });
        flver.Nodes.Add(new SoulsFormats.FLVER.Node { Name = "WP_A_1616" });
        flver.Nodes.Add(new SoulsFormats.FLVER.Node { Name = "WP_A_1616_old", Flags = SoulsFormats.FLVER.Node.NodeFlags.Disabled });
        flver.Dummies.Add(new SoulsFormats.FLVER.Dummy { ReferenceID = 100, ParentBoneIndex = 1, AttachBoneIndex = -1 });
        flver.Dummies.Add(new SoulsFormats.FLVER.Dummy { ReferenceID = 300, ParentBoneIndex = 0, AttachBoneIndex = -1 });
        flver.Meshes.Add(new SoulsFormats.FLVER2.Mesh { NodeIndex = 2 });
        ModelPort.SetNodeFlags(flver);
        Check(flver.Nodes[0].Flags == SoulsFormats.FLVER.Node.NodeFlags.DummyOwner && flver.Nodes[1].Flags == SoulsFormats.FLVER.Node.NodeFlags.DummyOwner,
            $"model: nodes dummy polys hang on are DummyOwner (got {flver.Nodes[0].Flags}, {flver.Nodes[1].Flags})");
        Check(flver.Nodes[2].Flags == (SoulsFormats.FLVER.Node.NodeFlags.Mesh | SoulsFormats.FLVER.Node.NodeFlags.Bone), $"model: the mesh's node is Mesh | Bone (got {flver.Nodes[2].Flags})");
        Check(flver.Nodes[3].Flags == SoulsFormats.FLVER.Node.NodeFlags.Disabled, "model: a Disabled node keeps its flag");
        // Bitfield fillers (ParamStore.FillBitfields): Elden Ring 2.7.2's SpEffectParam sets flags in bits Paramdex leaves unnamed.
        var xml = Path.Combine(Path.GetTempPath(), $"darksign-bits-{Environment.ProcessId}.xml");
        File.WriteAllText(xml, """
            <?xml version="1.0" encoding="utf-8"?>
            <PARAMDEF XmlVersion="2"><ParamType>TEST_ST</ParamType><DataVersion>1</DataVersion><BigEndian>False</BigEndian>
            <Unicode>True</Unicode><FormatVersion>203</FormatVersion><Fields>
            <Field Def="u8 a" /><Field Def="u8 b:1" /><Field Def="u8 c:1" /><Field Def="u8 d:3" /><Field Def="dummy8 pad[1]" />
            <Field Def="f32 e" /></Fields></PARAMDEF>
            """);
        var def = SoulsFormats.PARAMDEF.XmlDeserialize(xml, versionAware: true, validateFields: false);
        File.Delete(xml);
        int size = def.GetRowSize(), filled = ParamStore.FillBitfields(def);
        Check(filled == 3 && def.GetRowSize() == size && def.Fields.Count == 7, $"params: unnamed bits of a bitfield byte get a filler, row size unchanged ({filled} bits, {size} -> {def.GetRowSize()} bytes)");
        // Friede's 712 override (DS3 ChangeType 5 on slots RH1 and LH1, condition 2): a612's "hang location" move
        // (ChangeType 3) by default, Elden Ring's paired-art move (ChangeType 2) with "both", the 1.0.221 move (1 / 0)
        // with "back", untouched with "keep"; the condition stays 2.
        byte[] Override() { var b = new byte[24]; b[1] = 0xFF; b[2] = 2; b[3] = 5; b[5] = 0xFF; b[7] = 0xFF; b[8] = 0; b[9] = 3; b[10] = 2; b[11] = 5; b[16] = 1; b[17] = 1; return b; }
        var ov = Override();
        Check(MovesetPort.FixModelOverride(ov, "") == 2 && ov[2] == 2 && ov[3] == 3 && ov[10] == 2 && ov[11] == 3 && ov[9] == 3 && ov[16] == 1,
            $"712: Friede's override becomes 'hang location' (condition 2, ChangeType 3, a612's bytes) on both slots, other slots and tail untouched (got RH1 {ov[2]}/{(sbyte)ov[3]} LH1 {ov[10]}/{(sbyte)ov[11]})");
        ov = Override();
        Check(MovesetPort.FixModelOverride(ov, "art") == 2 && ov[3] == 3 && ov[11] == 3, "712: 'art' is the default");
        ov = Override();
        Check(MovesetPort.FixModelOverride(ov, "both") == 2 && ov[2] == 2 && ov[3] == 2 && ov[10] == 2 && ov[11] == 2,
            $"712: 'both' moves the models to their two-handed location (ChangeType 2) (got RH1 {ov[2]}/{(sbyte)ov[3]} LH1 {ov[10]}/{(sbyte)ov[11]})");
        ov = Override();
        Check(MovesetPort.FixModelOverride(ov, "back") == 2 && ov[2] == 2 && ov[3] == 1 && ov[10] == 2 && ov[11] == 0,
            $"712: 'back' keeps condition 2 and moves right-hand models to the 1H-left location, left-hand ones to 1H-right (got RH1 {ov[2]}/{(sbyte)ov[3]} LH1 {ov[10]}/{(sbyte)ov[11]})");
        ov = Override();
        Check(MovesetPort.FixModelOverride(ov, "keep") == 0 && ov[3] == 5, "712: 'keep' leaves DS3's override as it is");
        // The stance chain: Friede's block gives two more light attacks, a plain DS3 stance none.
        Check(MovesetPort.StanceChainMoves([36200, 36210, 36215, 36220, 36230, 36231, 36240, 36241, 36250, 36251, 36290, 36291]).SequenceEqual([36250, 36290]),
            "chain: Friede's stance chains 36230 -> 36250 -> 36290 (no-FP copies and the heavy attack left out)");
        Check(MovesetPort.StanceChainMoves([36200, 36210, 36220, 36230, 36240, 36241, 36300, 36310]).Count == 0, "chain: a stance with one light attack chains nothing");
        Check(MovesetPort.OneShotFollowUps([36400, 36401, 36410, 36411, 36420, 36421], 36400).SequenceEqual([36410, 36420]),
            "arts: a one-shot's follow-ups are the block's next x0 moves, never the x1 no-FP copies (Ember: 36410 thrust, 36420 slam)");
        Check(MovesetPort.OneShotFollowUps([36520, 36521], 36520).Count == 0, "arts: a one-shot with only its no-FP copy has no follow-up");
        Check(!MovesetPort.IsGenericArt("Falling Bolt") && !MovesetPort.IsGenericArt("Guiding Light"), "arts: Falling Bolt and Guiding Light are unique arts");
        // Its hks: the three art-318 tests that name the chain state are widened, the FP-charging ones aren't.
        const string chainHks = "function IsStanceArts(arts_id)\n    local aow_list = {\n    }\nend\n" +
                                "    if c_SwordArtsID == 318 then\n        SetSwordArtsPointInfo(ACTION_ARM_R1, TRUE)\n    else\n" +
                                "        if c_SwordArtsID == 318 then\n            if r1 == \"W_SwordArtsOneShotComboEnd_MesmerSowrdArts\" then\n" +
                                "        if c_SwordArtsID == 318 and r1 == \"W_SwordArtsOneShotComboEnd_MesmerSowrdArts\" then\n" +
                                "    if c_SwordArtsID == 318 then\n        if env(GetSpEffectID, 100054) == TRUE then\n            r1 = \"W_SwordArtsOneShotComboEnd_MesmerSowrdArts\"\n";
        Check(MovesetPort.CanChainStances(chainHks) && !MovesetPort.CanChainStances("if c_SwordArtsID == 318 then\n    SetSwordArtsPointInfo(ACTION_ARM_R1, TRUE)\n"),
            "chain: the hks is usable when an art-318 test names the chain state");
        var chained = MovesetPort.PatchHks(chainHks, [300], null, [300, 301]);
        int widened = chained.Split("(c_SwordArtsID == 318 or DarkSignIsStanceChain(c_SwordArtsID))").Length - 1;
        Check(widened == 3 && chained.Contains("    if c_SwordArtsID == 318 then\n        SetSwordArtsPointInfo(ACTION_ARM_R1, TRUE)"),
            $"chain: the 3 art-318 tests naming the chain state are widened to the ported stances, the FP one stays (got {widened})");
        Check(chained.Contains("function DarkSignIsStanceChain(arts_id)\n    if arts_id == 300 or arts_id == 301 then\n        return TRUE") && chained.IndexOf("function DarkSignIsStanceChain", StringComparison.Ordinal) < chained.IndexOf("function IsStanceArts", StringComparison.Ordinal),
            "chain: DarkSignIsStanceChain lists the chained arts, before IsStanceArts");
        Check(MovesetPort.PatchHks(chainHks, [300], null, []) == MovesetPort.PatchHks(chainHks, [300]), "chain: no chained arts, no hks change");
        // Its windows: the stance light attack opens 100054 over DS3's own chain marker (100490) once R1 may leave; the second
        // light attack opens 100055; the template's window goes.
        var stanceLight = new SoulsFormats.TAE.Animation(40060, new SoulsFormats.TAE.Animation.AnimMiniHeader.Standard(), "a906_040060.hkt")
            { Events = [new SoulsFormats.TAE.Event(0.1f, 0.5f, 67, 0, Win(100050), false) { Group = group }, new SoulsFormats.TAE.Event(0, 0.3f, 0, 0, Jump(7), false) { Group = jumpGroup }] };
        var friede = new List<SoulsFormats.TAE.Event>
        {
            new(0.833f, 1.633f, 1, 0, new byte[16], false), new(0f, 2.5f, 66, 0, Win(100490), false), new(2.5f, 3.9f, 66, 0, Win(100500), false), new(1.6f, 4.033f, 0, 0, Jt(4), false),
        };
        MovesetPort.SetComboWindows(stanceLight, proto, friede, 4.033f, true, false, ds3Marker: true);
        var w = stanceLight.Events.Where(e => e.Type == 67).ToList();
        Check(w.Count == 1 && BitConverter.ToInt32(w[0].GetParameterBytes(false), 0) == 100054 && Math.Abs(w[0].StartTime - 1.633f) < 0.001f && Math.Abs(w[0].EndTime - 2.5f) < 0.001f,
            $"chain: the stance light attack opens 100054 over DS3's chain marker from the last hit to its end, 1.633-2.5 s, not over 100500 (got {string.Join(" ", w.Select(e => $"{BitConverter.ToInt32(e.GetParameterBytes(false), 0)} {e.StartTime:0.###}-{e.EndTime:0.###}"))})");
        Check(stanceLight.Events.Any(e => e.Type == 0 && BitConverter.ToInt32(e.GetParameterBytes(false), 0) == MovesetPort.LightFollowUpJumpTable && Math.Abs(e.StartTime - 1.633f) < 0.001f && Math.Abs(e.EndTime - 2.5f) < 0.001f)
              && !stanceLight.Events.Any(e => e.Type == 0 && BitConverter.ToInt32(e.GetParameterBytes(false), 0) == MovesetPort.HeavyFollowUpJumpTable),
            "chain: cancel JumpTable 115 (R1), not 116, opens with the chain window");
        MovesetPort.SetComboWindows(stanceLight, proto, friede, 4.033f, true, false, lightWindow: 100055, ds3Marker: true);
        Check(stanceLight.Events.Count(e => e.Type == 67) == 1 && stanceLight.Events.Any(e => e.Type == 67 && BitConverter.ToInt32(e.GetParameterBytes(false), 0) == 100055),
            "chain: the second light attack opens 100055 (into ComboEnd_2) instead");
        // The Aquamarine Dagger's 36230: marker 0-0.867 s, R1 cancel from 0.5 s, hit 0.333-0.433 s; a marker that ends
        // before the cancel opens keeps its last half second; no marker, no span (the Ringed Knight Straight Sword).
        var dagger = new List<SoulsFormats.TAE.Event> { new(0.333f, 0.433f, 1, 0, new byte[16], false), new(0f, 0.867f, 66, 0, Win(100490), false), new(0.5f, 1.8f, 0, 0, Jt(4), false) };
        Check(MovesetPort.ChainSpan(dagger) is { } d && Math.Abs(d.Start - 0.5f) < 0.001f && Math.Abs(d.End - 0.867f) < 0.001f, "chain: the window opens with DS3's R1 cancel when that comes after the hit (0.5-0.867 s)");
        var late = new List<SoulsFormats.TAE.Event> { new(0f, 1f, 66, 0, Win(100490), false), new(1.5f, 3f, 0, 0, Jt(4), false) };
        Check(MovesetPort.ChainSpan(late) is { } l8 && Math.Abs(l8.Start - 0.5f) < 0.001f && Math.Abs(l8.End - 1f) < 0.001f, "chain: a marker that ends before the R1 cancel keeps its last half second");
        Check(MovesetPort.ChainSpan([new(2.5f, 3.9f, 66, 0, Win(100500), false), new(1.6f, 4f, 0, 0, Jt(4), false)]) is null, "chain: a move without the chain marker has no span (keeps the first-exit window)");
        MovesetPort.SetComboWindows(stanceLight, proto, friede, 4.033f, true, false);
        Check(stanceLight.Events.Single(e => e.Type == 67).StartTime > 1.5f && stanceLight.Events.Single(e => e.Type == 67).StartTime < 2.5f,
            "windows: without ds3Marker the DS3 transition marker is not read (one-shot follow-ups keep their timing)");
        // FP charge (330): Elden Ring writes it at 16 and 24 bytes in one section; DS3's 16 must still be copied.
        var lengths = MovesetPort.EventLengths([new(0.4f, 0.5f, 330, 0, new byte[24], false), new(0.5f, 0.6f, 330, 0, new byte[16], false), new(0f, 1f, 67, 0, new byte[8], false)]);
        Check(lengths[330].SetEquals([16, 24]) && lengths[67].SetEquals([8]), "events: every length the template writes a type at is known (FP charge 330: 16 and 24 bytes)");
        Console.WriteLine(failures == 0 ? "arts-selftest: all checks passed" : $"arts-selftest: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
